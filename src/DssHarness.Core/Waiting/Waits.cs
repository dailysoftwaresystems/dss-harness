using DssHarness.Core.Execution;

namespace DssHarness.Core.Waiting;

/// <summary>
/// Waits for the thing itself - a work's end, a holder's death - never for a number of the code's own. Each wait is told
/// every <see cref="SaidEvery"/>, is ended by the caller's token, and ends early only at the bound the user configured:
/// a step's <c>timeoutMinutes</c>, <c>stallSeconds</c>, a budget's minutes. An unattended run is therefore still ended by
/// its own configured bounds, and a slow thing is waited for as long as it takes.
/// </summary>
/// <remarks>
/// Time is read, and every pause is waited, on the watch's own clock, so a test's clock decides when a wait ends. No pause
/// is longer than the time to the next telling, so a bound of any length - a budget of months - is waited in pieces that
/// no timer refuses. Telling goes through the watch's callback alone: nothing here writes to the console or to the
/// harness's output, and nothing branches on the operating system.
/// </remarks>
public static class Waits
{
    /// <summary>
    /// The longest a wait goes without saying where it stands: the planned cadence every wait of the harness is told at,
    /// a leg's admission's among them (<see cref="LegAdmission.SaidAgainEvery"/>). A wait that says nothing for long reads
    /// as a hang.
    /// </summary>
    public static readonly TimeSpan SaidEvery = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How often a wait on a holder looks again whether something holds and whether its holder lives. A cadence, never a
    /// bound: the wait looks again, look after look, for as long as the holder lives. A second is soon enough for a person
    /// to see the wait end, and seldom enough that the looks cost nothing beside what is waited for.
    /// </summary>
    public static readonly TimeSpan LooksAgainEvery = TimeSpan.FromSeconds(1);

    /// <summary>What a wait on a holder says, after the holder's name, as it finds the holder ended.</summary>
    private const string HolderEnded = "the holder has ended";

    /// <summary>
    /// <paramref name="wanted"/>, or what is <paramref name="left"/> of the time allowed where that is less - never below
    /// nothing, since time passes between the look that found some left and the wait.
    /// </summary>
    /// <param name="wanted">How long it would wait: its poll, or a settle.</param>
    /// <param name="left">What is left of the time it is allowed.</param>
    internal static TimeSpan Shorter(TimeSpan wanted, TimeSpan left)
        => left <= TimeSpan.Zero ? TimeSpan.Zero : wanted < left ? wanted : left;

    /// <summary>
    /// Awaits <paramref name="work"/> to its end, telling how long it has run each time <see cref="SaidEvery"/> has passed
    /// since the wait last said so - the first once the work has run that long, never as it starts.
    /// </summary>
    /// <remarks>
    /// The work is given a token that both the user's bound and <paramref name="cancellationToken"/> cancel. Once either
    /// has, the work's own end is still awaited - told as before - so nothing it started is left running behind the
    /// answer; a work that ends with its answer all the same, past its cancellation, ends <see cref="WaitEndKind.Done"/>.
    /// A failure of the work's own is raised through, a cancellation it raised for a reason of its own included. A tell
    /// that throws ends the wait with its exception, the work cancelled first.
    /// </remarks>
    /// <typeparam name="T">What the work answers.</typeparam>
    /// <param name="work">The work, given the token that cancels it.</param>
    /// <param name="watch">What the wait is told as, the user's bound, and the clock it is measured on.</param>
    /// <param name="cancellationToken">Ends the wait, cancelling the work.</param>
    /// <returns>
    /// <see cref="WaitEndKind.Done"/> with the work's answer; <see cref="WaitEndKind.Bound"/> where the work ended on the
    /// cancellation the user's bound made; <see cref="WaitEndKind.Cancelled"/> where it ended on the caller's. Each with
    /// how long the wait lasted.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="work"/> or <paramref name="watch"/> is null.</exception>
    public static async Task<WaitEnd<T>> ForAsync<T>(Func<CancellationToken, Task<T>> work, WaitWatch watch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(watch);

        var clock = watch.Clock;
        var started = clock.GetTimestamp();
        var saidAt = started;
        var bounded = false;

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var running = work(stop.Token);

        try
        {
            while (!running.IsCompleted)
            {
                var now = clock.GetTimestamp();
                var waited = clock.GetElapsedTime(started, now);

                // The bound weighs only until the work is cancelled: past that, the wait is the work's draining, told as
                // before, and a bound passed again would say nothing new.
                if (!stop.IsCancellationRequested && watch.Bound is { } bound && waited >= bound)
                {
                    bounded = true;
                    stop.Cancel();
                    continue;
                }

                var sinceSaid = clock.GetElapsedTime(saidAt, now);

                if (sinceSaid >= SaidEvery)
                {
                    watch.Tell(new WaitTold(watch.Subject, waited, null, null));
                    (saidAt, sinceSaid) = (now, TimeSpan.Zero);
                }

                var pause = SaidEvery - sinceSaid;

                if (!stop.IsCancellationRequested && watch.Bound is { } left)
                {
                    pause = Shorter(pause, left - waited);
                }

                // A pause of its own, never the work's token: a cancelled work that has not yet ended is waited for still,
                // and a pause ended by that token would be waited again at once, without end.
                using var paused = new CancellationTokenSource();
                _ = await Task.WhenAny(running, Task.Delay(pause, clock, paused.Token)).ConfigureAwait(false);
                paused.Cancel();
            }
        }
        catch
        {
            stop.Cancel();
            throw;
        }

        try
        {
            var answer = await running.ConfigureAwait(false);
            return new WaitEnd<T>(WaitEndKind.Done, answer, clock.GetElapsedTime(started));
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            return new WaitEnd<T>(bounded ? WaitEndKind.Bound : WaitEndKind.Cancelled, default, clock.GetElapsedTime(started));
        }
    }

    /// <summary>
    /// Waits while something is held by a holder that lives: asks <paramref name="holder"/> who holds it, and looks again
    /// every <see cref="LooksAgainEvery"/> for as long as the holder it names lives by <paramref name="identity"/>.
    /// </summary>
    /// <remarks>
    /// The wait tells once as it first finds a live holder, naming it, and again each time <see cref="SaidEvery"/> has
    /// passed since it last said so; a live holder of another process than the one told is told as found anew. It tells
    /// once more as it finds a holder ended, naming it, where it had told a live one - a holder found ended at the first
    /// look was never waited for. Nothing is told as the holder lets go: the caller's next act says what it then took. A
    /// holder is told apart from the next by its process id and stamp, never by its name, which may say more each look.
    /// A failure of <paramref name="holder"/>'s or <paramref name="identity"/>'s own is raised through.
    /// </remarks>
    /// <param name="holder">Who holds what is waited for, asked at every look; <see langword="null"/> where nothing does.</param>
    /// <param name="identity">Whether a holder's process still runs: its id and stamp, never its id alone.</param>
    /// <param name="watch">What the wait is told as, the user's bound, and the clock it is measured on.</param>
    /// <param name="cancellationToken">Ends the wait.</param>
    /// <returns>
    /// <see cref="WaitEndKind.Done"/> where nothing holds or the holder has ended, its value true where the holder was found
    /// ended - what it held is then the caller's to take back, since no live process holds it - and false where nothing
    /// held at the last look; <see cref="WaitEndKind.Bound"/> at the user's bound, and <see cref="WaitEndKind.Cancelled"/>
    /// on the caller's token, each with the value false. Each with how long the wait lasted.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="holder"/>, <paramref name="identity"/> or <paramref name="watch"/> is null.</exception>
    public static async Task<WaitEnd<bool>> WhileHeldAsync(
        Func<CancellationToken, Task<WaitHolder?>> holder,
        IProcessIdentity identity,
        WaitWatch watch,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(holder);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(watch);

        var clock = watch.Clock;
        var started = clock.GetTimestamp();
        var saidAt = started;
        WaitHolder? told = null;

        try
        {
            while (true)
            {
                var holding = await holder(cancellationToken).ConfigureAwait(false);
                var now = clock.GetTimestamp();
                var waited = clock.GetElapsedTime(started, now);

                if (holding is null)
                {
                    return new WaitEnd<bool>(WaitEndKind.Done, false, waited);
                }

                if (!identity.IsAlive(holding.ProcessId, holding.Stamp))
                {
                    if (told is not null)
                    {
                        watch.Tell(new WaitTold(watch.Subject, waited, holding.Name, HolderEnded));
                    }

                    return new WaitEnd<bool>(WaitEndKind.Done, true, waited);
                }

                if (told is null || told.ProcessId != holding.ProcessId || !string.Equals(told.Stamp, holding.Stamp, StringComparison.Ordinal))
                {
                    watch.Tell(new WaitTold(watch.Subject, waited, holding.Name, null));
                    (told, saidAt) = (holding, now);
                }

                if (watch.Bound is { } bound && waited >= bound)
                {
                    return new WaitEnd<bool>(WaitEndKind.Bound, false, waited);
                }

                var sinceSaid = clock.GetElapsedTime(saidAt, now);

                if (sinceSaid >= SaidEvery)
                {
                    watch.Tell(new WaitTold(watch.Subject, waited, holding.Name, null));
                    (saidAt, sinceSaid) = (now, TimeSpan.Zero);
                }

                var pause = Shorter(LooksAgainEvery, SaidEvery - sinceSaid);

                if (watch.Bound is { } left)
                {
                    pause = Shorter(pause, left - waited);
                }

                await Task.Delay(pause, clock, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new WaitEnd<bool>(WaitEndKind.Cancelled, false, clock.GetElapsedTime(started));
        }
    }
}
