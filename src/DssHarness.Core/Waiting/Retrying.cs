using DssHarness.Core.Execution;

namespace DssHarness.Core.Waiting;

/// <summary>
/// The one owner of trying again: a try is made again while its failure can change, and the run stops where it cannot -
/// the same failure met again on the same state, or a failure no retry changes - never at a count of tries or a cap on a
/// wait of the code's own.
/// </summary>
/// <remarks>
/// A cause that passes by nature is waited out for as long as it lasts, its own word on how long waited exactly as given;
/// any other wait doubles from the caller's first wait up to <see cref="Waits.SaidEvery"/>. Each wait is told as it begins
/// and every <see cref="Waits.SaidEvery"/> while it lasts, waited in pieces no longer than that on the watch's clock, and
/// ended by the caller's token; the run ends early only at the bound the user configured.
/// </remarks>
public static class Retrying
{
    /// <summary>
    /// Makes <paramref name="attempt"/>'s tries, numbered from 1, until one gives its answer, fails in a way no retry
    /// changes, fails as an earlier one did on the same state, the user's bound comes before the next try, or
    /// <paramref name="cancellationToken"/> ends the run.
    /// </summary>
    /// <remarks>
    /// The first try is made at once. Before each later one the run waits: what a passing cause says, exactly as given, or
    /// otherwise the doubling wait (<see cref="RetryWatch.FirstWait"/>). The wait is told as it begins - its cause or
    /// failure, and how long until the next try - and every <see cref="Waits.SaidEvery"/> while it lasts, each line saying
    /// how long the whole run has waited. The user's bound ends the whole run: a wait longer than what is left of it ends
    /// at the bound, without a further try, and a try is never cut short by it. A failure of a try's own is raised
    /// through, a cancellation it raised for a reason of its own included.
    /// </remarks>
    /// <typeparam name="T">What a try answers.</typeparam>
    /// <param name="attempt">Makes one try, given its number and the caller's token, and says what it came to.</param>
    /// <param name="watch">What the run is told as, its first doubling wait, the user's bound, and its clock.</param>
    /// <param name="cancellationToken">Ends the run, in a try or in a wait.</param>
    /// <returns>How the run ended, what it gave, how many tries it made, and the last failure or cause it met.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="attempt"/> or <paramref name="watch"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The watch's first wait is no time, or less.</exception>
    /// <exception cref="InvalidOperationException">A try came to no kind <see cref="RetryTry{T}"/> has.</exception>
    public static async Task<RetryEnd<T>> RunAsync<T>(Func<int, CancellationToken, Task<RetryTry<T>>> attempt, RetryWatch watch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        ArgumentNullException.ThrowIfNull(watch);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(watch.FirstWait, TimeSpan.Zero);

        var clock = watch.Clock;
        var started = clock.GetTimestamp();

        // Every failure's digest the run has met: a digest met again is the same failure on the same state, which another
        // try would only meet a third time.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var doubled = watch.FirstWait;
        var tries = 0;
        string? last = null;

        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                tries++;

                var tried = await attempt(tries, cancellationToken).ConfigureAwait(false);
                TimeSpan wait;

                switch (tried)
                {
                    case RetryTry<T>.Done done:
                        return new RetryEnd<T>(RetryEndKind.Done, done.Value, tries, last);

                    case RetryTry<T>.Final final:
                        return new RetryEnd<T>(RetryEndKind.Final, default, tries, final.Failure);

                    case RetryTry<T>.Failed failed:
                        last = failed.Failure;

                        if (!seen.Add(failed.Digest))
                        {
                            return new RetryEnd<T>(RetryEndKind.Repeated, default, tries, last);
                        }

                        wait = Doubling();
                        break;

                    case RetryTry<T>.Passing passing:
                        last = passing.Cause;
                        wait = passing.After is { } after ? (after > TimeSpan.Zero ? after : TimeSpan.Zero) : Doubling();
                        break;

                    default:
                        throw new InvalidOperationException(
                            $"a try came to '{tried?.GetType().Name ?? "nothing"}', which is no try of a retry's: a try is done, passing, failed or final");
                }

                if (!await WaitAsync(watch, started, last, wait, cancellationToken).ConfigureAwait(false))
                {
                    return new RetryEnd<T>(RetryEndKind.Bound, default, tries, last);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new RetryEnd<T>(RetryEndKind.Cancelled, default, tries, last);
        }

        // The doubling wait due now, and the next one, by the one rule of it (Doubled).
        TimeSpan Doubling()
        {
            var due = doubled;
            doubled = Doubled(doubled, watch.FirstWait);
            return due;
        }
    }

    /// <summary>
    /// The wait that follows <paramref name="last"/> where no cause names its length: <paramref name="first"/> where there
    /// was none, else the last doubled until it reaches the cadence a wait is told at (<see cref="Waits.SaidEvery"/>), then
    /// kept there - a cadence, never a bound, since the tries go on - and a first wait already past it kept as given. The
    /// one rule of the doubling, for a run of tries here and for a wait kept between runs: a source that asks to be left
    /// alone and says not for how long, left the next time for twice as long.
    /// </summary>
    /// <param name="last">The wait before; <see langword="null"/> where there was none.</param>
    /// <param name="first">The first wait: the caller's own pace, or a planned value; more than nothing.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="first"/> is no time, or less.</exception>
    public static TimeSpan Doubled(TimeSpan? last, TimeSpan first)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(first, TimeSpan.Zero);

        if (last is not { } before || before <= TimeSpan.Zero)
        {
            return first;
        }

        if (before >= Waits.SaidEvery)
        {
            return before;
        }

        return before + before < Waits.SaidEvery ? before + before : Waits.SaidEvery;
    }

    /// <summary>
    /// Waits <paramref name="wait"/> before the next try, told as it begins and every <see cref="Waits.SaidEvery"/> while it
    /// lasts; false where the user's bound came first, which ends the wait at the bound.
    /// </summary>
    /// <param name="watch">What the run is told as, the user's bound, and its clock.</param>
    /// <param name="started">When the run began, as a timestamp of the watch's clock: each line says how long it has waited.</param>
    /// <param name="reason">The cause or failure the wait is for, as a line says it.</param>
    /// <param name="wait">How long to wait.</param>
    /// <param name="cancellationToken">Ends the wait, by throwing.</param>
    private static async Task<bool> WaitAsync(RetryWatch watch, long started, string reason, TimeSpan wait, CancellationToken cancellationToken)
    {
        var clock = watch.Clock;
        var begun = clock.GetTimestamp();
        var waited = clock.GetElapsedTime(started, begun);

        if (watch.Bound is { } reached && waited >= reached)
        {
            return false;
        }

        watch.Tell(new WaitTold(watch.Subject, waited, null, Again(reason, wait)));
        var saidAt = begun;

        while (true)
        {
            var now = clock.GetTimestamp();
            var lasted = clock.GetElapsedTime(begun, now);
            waited = clock.GetElapsedTime(started, now);

            if (watch.Bound is { } bound && waited >= bound)
            {
                return false;
            }

            if (lasted >= wait)
            {
                return true;
            }

            var sinceSaid = clock.GetElapsedTime(saidAt, now);

            if (sinceSaid >= Waits.SaidEvery)
            {
                watch.Tell(new WaitTold(watch.Subject, waited, null, Again(reason, wait - lasted)));
                (saidAt, sinceSaid) = (now, TimeSpan.Zero);
            }

            var pause = Waits.Shorter(wait - lasted, Waits.SaidEvery - sinceSaid);

            if (watch.Bound is { } left)
            {
                pause = Waits.Shorter(pause, left - waited);
            }

            await Task.Delay(pause, clock, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// A wait's progress as a line says it: <c>it answered 503; tries again in 4s</c>, the time as the ledger's table says
    /// it, and <c>tries again now</c> where what is left would be said as no time.
    /// </summary>
    private static string Again(string reason, TimeSpan left)
        => LedgerReport.FormatDuration(left) is { Length: > 0 } said and not "0s"
            ? $"{reason}; tries again in {said}"
            : $"{reason}; tries again now";
}
