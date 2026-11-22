using System.Collections.Concurrent;
using DssHarness.Core.Output;
using DssHarness.Core.Processes;
using DssHarness.Core.Waiting;

namespace DssHarness.Core.Hosts;

/// <summary>
/// Keeps asking for an ssh host that is waking up - its name, then a connection - for as long as the window
/// its <c>wakeWaitSeconds</c> gives it, before the host is reported unavailable.
/// </summary>
/// <remarks>
/// A personal Mac reached by its mDNS name falls back asleep between commands, and answers again moments
/// later: a consumer saw a run skip it seconds after a check had reached it, three times in fifteen minutes,
/// and a retry about 20 seconds later always found it. Three quick lookups miss that; a window catches it.
/// Only what a waking host explains is asked again: a name no answer came for yet, a connection nothing
/// took or that timed out. A key refused, a name its DNS server says does not exist, or one the host is not known by,
/// is an answer, and is never retried. Whatever ran out is kept for as long as the window it ran out of, so the other
/// legs a command places there in that time do not each wait the whole window again, and one placed later waits a
/// window of its own.
/// </remarks>
/// <param name="lookup">How a name is looked up, asked afresh each time: an answer kept would miss the wake.</param>
/// <param name="clock">What measures the window: its timestamps, which only move forward, never its time of day.</param>
/// <param name="pollDelay">
/// How long to wait between tries. Passed in rather than fixed so that a test measures the trying and not
/// the waiting; production uses <see cref="DefaultPollDelay"/>.
/// </param>
/// <param name="output">Where the lookups of a waking host are told while they last; nowhere where none is given.</param>
public sealed class SshWakeWindow(INameLookup lookup, TimeProvider clock, TimeSpan pollDelay, IHarnessOutput? output = null)
{
    /// <summary>
    /// The wait between tries: a few within the 20-30 seconds a consumer measured a dark-waking Mac taking to
    /// answer, and few enough that a host that stays asleep costs a handful of lookups, not a flood.
    /// </summary>
    public static readonly TimeSpan DefaultPollDelay = TimeSpan.FromSeconds(5);

    private readonly ConcurrentDictionary<HostId, (string Refusal, long Kept, TimeSpan Window)> _ranOut = new();

    private readonly INameLookup _lookup = lookup;
    private readonly TimeProvider _clock = clock;
    private readonly TimeSpan _pollDelay = pollDelay;
    private readonly IHarnessOutput? _output = output;

    /// <summary>
    /// Now, on the clock a window is measured by: one that only moves forward, at the pace time passes. Never the
    /// time of day, which a machine may set while a host wakes - as one that syncs its time does - and which then
    /// cut a window short, or drew it out by as much as the clock was set back.
    /// </summary>
    public long Now => _clock.GetTimestamp();

    /// <summary>How long it has been since <paramref name="then"/>, a <see cref="Now"/> read earlier.</summary>
    /// <param name="then">When it was.</param>
    public TimeSpan Since(long then) => _clock.GetElapsedTime(then);

    /// <summary>
    /// Why <paramref name="host"/>'s window ran out, where it did less than that window's own length ago; otherwise
    /// <see langword="null"/>, and the host is given a window of its own again.
    /// </summary>
    /// <param name="host">The host.</param>
    public string? RanOut(HostId host)
        => _ranOut.TryGetValue(host, out var kept) && Since(kept.Kept) < kept.Window ? kept.Refusal : null;

    /// <summary>
    /// Keeps <paramref name="refusal"/> as why <paramref name="host"/>'s window ran out, for as long as that window,
    /// <paramref name="window"/>, lasts.
    /// </summary>
    /// <param name="host">The host.</param>
    /// <param name="refusal">Why it could not be reached, window and all.</param>
    /// <param name="window">The window it ran out of.</param>
    public void Remember(HostId host, string refusal, TimeSpan window) => _ranOut[host] = (refusal, Now, window);

    /// <summary>
    /// Looks the name <paramref name="missed"/> got no answer for up again, until it resolves, its DNS server says it
    /// does not exist, or the window ends, and says how many lookups it took in all.
    /// </summary>
    /// <param name="missed">The resolution no answer came to, with the lookups it made.</param>
    /// <param name="started">When the window began, a <see cref="Now"/>.</param>
    /// <param name="window">How long it lasts.</param>
    /// <param name="cancellationToken">Stops the looking.</param>
    /// <remarks>
    /// Through <see cref="Retrying"/>: no answer from a host that is waking passes by nature, and is asked again every
    /// <see cref="DefaultPollDelay"/> for as long as the window lasts, told while it does; a name that does not exist is
    /// an answer, and ends the lookups at once. A name its DNS server already said does not exist is not asked again.
    /// </remarks>
    public async Task<AddressResolution> KeepResolvingAsync(AddressResolution missed, long started, TimeSpan window, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(missed);

        if (missed.NoSuchName is not null || Since(started) >= window)
        {
            return missed;
        }

        var looked = await Retrying
            .RunAsync<IReadOnlyList<string>>(
                async (_, token) =>
                {
                    try
                    {
                        return await _lookup.LookupAsync(missed.Address, token).ConfigureAwait(false) is { Count: > 0 } found
                            ? new RetryTry<IReadOnlyList<string>>.Done(found)
                            : new RetryTry<IReadOnlyList<string>>.Passing("no answer came, as from a host that is waking", _pollDelay);
                    }
                    catch (NoSuchNameException ex)
                    {
                        return new RetryTry<IReadOnlyList<string>>.Final(ex.Said);
                    }
                },
                new RetryWatch(
                    $"'{missed.Address}' to resolve as its host wakes",
                    ProgramWait.Told(_output, ToolPackage.Command),
                    Waits.LooksAgainEvery,
                    window - Since(started),
                    _clock),
                cancellationToken)
            .ConfigureAwait(false);

        var attempts = missed.Attempts + looked.Tries;

        return looked.Kind switch
        {
            RetryEndKind.Done => new AddressResolution(missed.Address, attempts, looked.Value!),
            RetryEndKind.Final => missed with { Attempts = attempts, NoSuchName = looked.Failure },
            RetryEndKind.Cancelled => throw new OperationCanceledException(cancellationToken),
            _ => missed with { Attempts = attempts },
        };
    }

    /// <summary>
    /// Runs <paramref name="probe"/> again while what it fails with is what a waking host explains, until it
    /// succeeds or the window ends, and says what the last try came to and how many there were.
    /// </summary>
    /// <param name="failed">What the first try came to.</param>
    /// <param name="started">When the window began, a <see cref="Now"/>.</param>
    /// <param name="window">How long it lasts.</param>
    /// <param name="probe">One try.</param>
    /// <param name="cancellationToken">Stops the trying.</param>
    public async Task<(ProcessResult Result, int Attempts)> KeepProbingAsync(
        ProcessResult failed,
        long started,
        TimeSpan window,
        Func<CancellationToken, Task<ProcessResult>> probe,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(failed);
        ArgumentNullException.ThrowIfNull(probe);

        var result = failed;
        var attempts = 1;

        // A try that itself takes connectTimeoutSeconds is charged to the window when it returns: the last one
        // may end after the window does, and none starts once it has.
        while (!result.Succeeded && HostProbes.MayBeWaking(result) && Since(started) < window)
        {
            await WaitAsync(started, window, cancellationToken).ConfigureAwait(false);

            if (Since(started) >= window)
            {
                break;
            }

            attempts++;
            result = await probe(cancellationToken).ConfigureAwait(false);
        }

        return (result, attempts);
    }

    /// <summary>Waits the delay between tries, or what is left of the window where that is less.</summary>
    private Task WaitAsync(long started, TimeSpan window, CancellationToken cancellationToken)
    {
        var left = window - Since(started);

        return Task.Delay(Waits.Shorter(_pollDelay, left), cancellationToken);
    }
}
