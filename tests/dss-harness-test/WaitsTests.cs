using System.Collections.Concurrent;
using DssHarness.Core.Execution;
using DssHarness.Core.Waiting;

namespace DssHarness.Tests;

/// <summary>
/// The harness's one wait: for the thing itself - a work's end, a holder's death - told every five minutes in one sentence
/// form, ended by the caller's token, and ended early only at the bound the user configured, never at a number of the
/// code's own.
/// </summary>
public sealed class WaitsTests
{
    private const string Vcvars = "vcvarsall to end";
    private const string Lock = "the run's lock";
    private const string Checks = "the forge's checks";
    private const string Holder = "pid 4242 (dssharness run)";
    private const int HolderId = 4242;
    private const string HolderStamp = "2026-11-19T03:10:00.0000000";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// A work that runs a quarter of an hour is told at five, ten and fifteen minutes, each in the one sentence form, and
    /// never as it starts: a wait that says nothing for long reads as a hang, and one that speaks as it starts has nothing
    /// yet to say. Once the work ends, nothing of the wait is left on the clock.
    /// </summary>
    [Fact]
    public async Task AWaitIsToldEveryFiveMinutes()
    {
        var clock = new TimerClock();
        var told = new ConcurrentQueue<WaitTold>();
        var answer = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        var run = Waits.ForAsync(_ => answer.Task, new WaitWatch(Vcvars, told.Enqueue, null, clock), Token);

        await clock.WaitingAsync().WaitAsync(ClockDrive.Within, Token);
        Assert.Empty(told);

        for (var tellings = 0; tellings < 3; tellings++)
        {
            clock.Advance(Waits.SaidEvery);
            await clock.WaitingAsync().WaitAsync(ClockDrive.Within, Token);
        }

        answer.SetResult(42);

        Assert.Equal(new WaitEnd<int>(WaitEndKind.Done, 42, TimeSpan.FromMinutes(15)), await run.WaitAsync(ClockDrive.Within, Token));
        Assert.Equal(
            ["has waited 5m00s for vcvarsall to end", "has waited 10m00s for vcvarsall to end", "has waited 15m00s for vcvarsall to end"],
            told.Select(one => one.Line()));
        Assert.Equal(0, clock.Waiting);
    }

    /// <summary>
    /// A work still running at the user's bound is cancelled there, and the wait ends saying it was the bound - told as
    /// before until then, and at seven minutes, not at the next telling: the person's bound is what ends an unattended
    /// wait, so it is kept to the minute.
    /// </summary>
    [Fact]
    public async Task AWaitEndsAtTheUsersBound_SayingBound()
    {
        var clock = new TimerClock();
        var told = new ConcurrentQueue<WaitTold>();
        var given = CancellationToken.None;

        var run = Waits.ForAsync(
            token =>
            {
                given = token;
                return Unending<int>(token);
            },
            new WaitWatch(Vcvars, told.Enqueue, TimeSpan.FromMinutes(7), clock),
            Token);

        var end = await ClockDrive.UntilEndedAsync(clock, run, TimeSpan.FromMinutes(1), 60, Token);

        Assert.Equal(new WaitEnd<int>(WaitEndKind.Bound, 0, TimeSpan.FromMinutes(7)), end);
        Assert.True(given.IsCancellationRequested, "the work is cancelled at the bound");
        Assert.Equal(["has waited 5m00s for vcvarsall to end"], told.Select(one => one.Line()));
    }

    /// <summary>
    /// The caller's token ends the wait and cancels the work with it: a wait nobody can stop holds its command for as
    /// long as the work takes. The work's answer, given after it was cancelled, is never taken for its end.
    /// </summary>
    [Fact]
    public async Task AWaitIsCancellable()
    {
        var clock = new TimerClock();
        var told = new ConcurrentQueue<WaitTold>();
        var given = CancellationToken.None;
        var answer = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);

        var run = Waits.ForAsync(
            token =>
            {
                given = token;
                _ = token.Register(() => answer.TrySetCanceled(token));
                return answer.Task;
            },
            new WaitWatch(Vcvars, told.Enqueue, null, clock),
            cancel.Token);

        await clock.WaitingAsync().WaitAsync(ClockDrive.Within, Token);
        clock.Advance(Waits.SaidEvery);
        await clock.WaitingAsync().WaitAsync(ClockDrive.Within, Token);
        await cancel.CancelAsync();

        // What the work would have answered had the cancellation not reached it: the wait would then end done.
        _ = answer.TrySetResult(7);

        Assert.Equal(new WaitEnd<int>(WaitEndKind.Cancelled, 0, Waits.SaidEvery), await run.WaitAsync(ClockDrive.Within, Token));
        Assert.True(given.IsCancellationRequested, "the work is cancelled with the wait");
        Assert.Equal(["has waited 5m00s for vcvarsall to end"], told.Select(one => one.Line()));
    }

    /// <summary>
    /// A work that fails of itself has its failure raised through the wait, whole: a wait that read it as done, or as its
    /// bound, would hide why the work did not finish.
    /// </summary>
    [Fact]
    public async Task AWorksOwnFailure_IsRaisedThrough()
    {
        var clock = new TimerClock();
        var told = new ConcurrentQueue<WaitTold>();
        var answer = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        var run = Waits.ForAsync(_ => answer.Task, new WaitWatch(Vcvars, told.Enqueue, TimeSpan.FromHours(1), clock), Token);

        await clock.WaitingAsync().WaitAsync(ClockDrive.Within, Token);
        clock.Advance(Waits.SaidEvery);
        await clock.WaitingAsync().WaitAsync(ClockDrive.Within, Token);
        answer.SetException(new InvalidOperationException("vcvarsall exited with 1"));

        var raised = await Assert.ThrowsAsync<InvalidOperationException>(() => run.WaitAsync(ClockDrive.Within, Token));

        Assert.Equal("vcvarsall exited with 1", raised.Message);
        Assert.Equal(["has waited 5m00s for vcvarsall to end"], told.Select(one => one.Line()));
    }

    /// <summary>
    /// A wait on a holder is told as it finds the holder alive, every five minutes after, and once more as it finds the
    /// holder ended - naming it each time - and then ends at once, saying the holder was found ended: what a dead process
    /// held is the caller's to take back. Liveness is asked by the holder's id and its stamp, never its id alone, which a
    /// later process may have been given.
    /// </summary>
    [Fact]
    public async Task WhileHeld_EndsWhenTheHolderDies_NamingIt()
    {
        var clock = new TimerClock();
        var told = new ConcurrentQueue<WaitTold>();
        var machine = new LivingHolder(HolderId, HolderStamp);

        var run = Waits.WhileHeldAsync(
            _ => Task.FromResult<WaitHolder?>(new WaitHolder(HolderId, HolderStamp, Holder)),
            machine,
            new WaitWatch(Lock, told.Enqueue, null, clock),
            Token);

        await clock.WaitingAsync().WaitAsync(ClockDrive.Within, Token);
        clock.Advance(Waits.SaidEvery);
        await clock.WaitingAsync().WaitAsync(ClockDrive.Within, Token);
        machine.End();
        clock.Advance(Waits.LooksAgainEvery);

        // Ended, the wait arms no further look: one that took the holder for alive still would.
        Assert.Same(run, await Task.WhenAny(run, clock.WaitingAsync()).WaitAsync(ClockDrive.Within, Token));
        Assert.Equal(new WaitEnd<bool>(WaitEndKind.Done, true, Waits.SaidEvery + Waits.LooksAgainEvery), await run);
        Assert.Equal(
            [
                "waits for the run's lock, held by pid 4242 (dssharness run)",
                "has waited 5m00s for the run's lock, held by pid 4242 (dssharness run)",
                "has waited 5m01s for the run's lock, held by pid 4242 (dssharness run): the holder has ended",
            ],
            told.Select(one => one.Line()));
        Assert.Equal([(HolderId, HolderStamp), (HolderId, HolderStamp), (HolderId, HolderStamp)], machine.Asked);
    }

    /// <summary>
    /// A wait on a holder that lets go ends at the look that finds nothing held, saying no holder was found ended - nothing
    /// is the caller's to take back - and says nothing more: the caller's next act says what it then took.
    /// </summary>
    [Fact]
    public async Task WhileHeld_EndsWhenNothingHolds_SayingNoMore()
    {
        var clock = new TimerClock();
        var told = new ConcurrentQueue<WaitTold>();
        var machine = new LivingHolder(HolderId, HolderStamp);
        var looks = 0;

        var run = Waits.WhileHeldAsync(
            _ => Task.FromResult(Interlocked.Increment(ref looks) == 1 ? new WaitHolder(HolderId, HolderStamp, Holder) : null),
            machine,
            new WaitWatch(Lock, told.Enqueue, null, clock),
            Token);

        var end = await ClockDrive.UntilEndedAsync(clock, run, Waits.LooksAgainEvery, 10, Token);

        Assert.Equal(new WaitEnd<bool>(WaitEndKind.Done, false, Waits.LooksAgainEvery), end);
        Assert.Equal(["waits for the run's lock, held by pid 4242 (dssharness run)"], told.Select(one => one.Line()));
        Assert.Equal(2, looks);
        Assert.Equal([(HolderId, HolderStamp)], machine.Asked);
    }

    /// <summary>
    /// A holder that lives past the user's bound ends the wait at the bound, saying so: a live holder is waited for only as
    /// long as the person allows, and no longer.
    /// </summary>
    [Fact]
    public async Task WhileHeld_EndsAtTheUsersBound_SayingBound()
    {
        var clock = new TimerClock();
        var told = new ConcurrentQueue<WaitTold>();
        var machine = new LivingHolder(HolderId, HolderStamp);

        var run = Waits.WhileHeldAsync(
            _ => Task.FromResult<WaitHolder?>(new WaitHolder(HolderId, HolderStamp, Holder)),
            machine,
            new WaitWatch(Lock, told.Enqueue, TimeSpan.FromSeconds(3), clock),
            Token);

        var end = await ClockDrive.UntilEndedAsync(clock, run, Waits.LooksAgainEvery, 10, Token);

        Assert.Equal(new WaitEnd<bool>(WaitEndKind.Bound, false, TimeSpan.FromSeconds(3)), end);
        Assert.Equal(["waits for the run's lock, held by pid 4242 (dssharness run)"], told.Select(one => one.Line()));
        Assert.Equal([(HolderId, HolderStamp), (HolderId, HolderStamp), (HolderId, HolderStamp), (HolderId, HolderStamp)], machine.Asked);
    }

    /// <summary>A wait on a holder ends on the caller's token between two looks, leaving nothing on the clock.</summary>
    [Fact]
    public async Task WhileHeld_IsCancellable()
    {
        var clock = new TimerClock();
        var told = new ConcurrentQueue<WaitTold>();
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);

        var run = Waits.WhileHeldAsync(
            _ => Task.FromResult<WaitHolder?>(new WaitHolder(HolderId, HolderStamp, Holder)),
            new LivingHolder(HolderId, HolderStamp),
            new WaitWatch(Lock, told.Enqueue, null, clock),
            cancel.Token);

        await clock.WaitingAsync().WaitAsync(ClockDrive.Within, Token);
        await cancel.CancelAsync();

        // Cancelled, the wait has dropped its look: one that never heard the token would still wait on the clock.
        Assert.Same(run, await Task.WhenAny(run, clock.WaitingAsync()).WaitAsync(ClockDrive.Within, Token));
        Assert.Equal(new WaitEnd<bool>(WaitEndKind.Cancelled, false, TimeSpan.Zero), await run);
        Assert.Equal(["waits for the run's lock, held by pid 4242 (dssharness run)"], told.Select(one => one.Line()));
    }

    /// <summary>
    /// A wait as it begins says that it waits, and for what - under the half second a line rounds to <c>0s</c> as well:
    /// <c>has waited 0s</c> would say nothing.
    /// </summary>
    [Fact]
    public void ALineOfAWaitJustBegun_SaysItWaits()
    {
        Assert.Equal("waits for vcvarsall to end", new WaitTold(Vcvars, TimeSpan.Zero, null, null).Line());
        Assert.Equal("waits for vcvarsall to end", new WaitTold(Vcvars, TimeSpan.FromMilliseconds(400), null, null).Line());
    }

    /// <summary>A wait under way says how long it has waited, as the ledger's table says a time, and for what.</summary>
    [Fact]
    public void ALineOfAWaitUnderway_SaysHowLongItHasWaited()
    {
        Assert.Equal("has waited 1s for vcvarsall to end", new WaitTold(Vcvars, TimeSpan.FromMilliseconds(600), null, null).Line());
        Assert.Equal("has waited 5m00s for vcvarsall to end", new WaitTold(Vcvars, Waits.SaidEvery, null, null).Line());
        Assert.Equal("has waited 26h05m for vcvarsall to end", new WaitTold(Vcvars, TimeSpan.FromHours(26) + TimeSpan.FromMinutes(5), null, null).Line());
    }

    /// <summary>A wait on a holder names the holder after a comma, as it begins and as it goes on.</summary>
    [Fact]
    public void ALineWithAHolder_NamesItAfterAComma()
    {
        Assert.Equal("waits for the run's lock, held by pid 4242 (dssharness run)", new WaitTold(Lock, TimeSpan.Zero, Holder, null).Line());
        Assert.Equal(
            "has waited 5m00s for the run's lock, held by pid 4242 (dssharness run)",
            new WaitTold(Lock, Waits.SaidEvery, Holder, null).Line());
    }

    /// <summary>A wait with something to add - why it waits, when it tries again - says it after a colon.</summary>
    [Fact]
    public void ALineWithProgress_SaysItAfterAColon()
    {
        Assert.Equal(
            "waits for the forge's checks: it answered 503; tries again in 2s",
            new WaitTold(Checks, TimeSpan.Zero, null, "it answered 503; tries again in 2s").Line());
        Assert.Equal(
            "has waited 2s for the forge's checks: it answered 503; tries again in 4s",
            new WaitTold(Checks, TimeSpan.FromSeconds(2), null, "it answered 503; tries again in 4s").Line());
    }

    /// <summary>A wait with a holder and something to add names the holder first, then says what it adds.</summary>
    [Fact]
    public void ALineWithAHolderAndProgress_NamesTheHolderThenSaysTheProgress()
    {
        Assert.Equal(
            "has waited 5m01s for the run's lock, held by pid 4242 (dssharness run): the holder has ended",
            new WaitTold(Lock, TimeSpan.FromSeconds(301), Holder, "the holder has ended").Line());
    }

    /// <summary>A work that never ends of itself: cancelled when, and only when, its token is.</summary>
    /// <summary>
    /// A work that goes on moving is waited for to its end, however far past the user's bound it runs: the bound is a
    /// silence, never the time a work takes. Told every five minutes with where the work says it stands.
    /// </summary>
    [Fact]
    public async Task AMovingWork_IsWaitedForPastTheBound()
    {
        var clock = new TimerClock();
        var told = new ConcurrentQueue<WaitTold>();
        var answer = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var moves = 0;
        var at = clock.GetTimestamp();

        var run = Waits.WhileMovingAsync(
            _ => answer.Task,
            () => new WaitMoved(Volatile.Read(ref at), $"{Volatile.Read(ref moves)} moves"),
            new WaitWatch(Checks, told.Enqueue, TimeSpan.FromMinutes(1), clock),
            Token);

        for (var step = 0; step < 24; step++)
        {
            await clock.WaitingAsync().WaitAsync(ClockDrive.Within, Token);
            clock.Advance(TimeSpan.FromSeconds(30));
            Interlocked.Increment(ref moves);
            Volatile.Write(ref at, clock.GetTimestamp());
        }

        answer.SetResult(7);

        Assert.Equal(new WaitEnd<int>(WaitEndKind.Done, 7, TimeSpan.FromMinutes(12)), await run.WaitAsync(ClockDrive.Within, Token));
        Assert.Equal(2, told.Count);
        Assert.All(told, one => Assert.EndsWith(" moves", one.Progress, StringComparison.Ordinal));
        Assert.Equal([Waits.SaidEvery, Waits.SaidEvery * 2], told.Select(one => one.Waited));
    }

    /// <summary>
    /// A work silent for the user's bound is cancelled there, and the wait ends saying it was the bound - told as before
    /// until then: a silence is what the bound measures, from the wait's start where the work never moved.
    /// </summary>
    [Fact]
    public async Task ASilentWork_EndsAtTheBound_SayingBound()
    {
        var clock = new TimerClock();
        var told = new ConcurrentQueue<WaitTold>();
        var given = CancellationToken.None;
        var before = clock.GetTimestamp() - 1;

        var run = Waits.WhileMovingAsync(
            token =>
            {
                given = token;
                return Unending<int>(token);
            },
            () => new WaitMoved(before, null),
            new WaitWatch(Checks, told.Enqueue, TimeSpan.FromMinutes(7), clock),
            Token);

        var end = await ClockDrive.UntilEndedAsync(clock, run, TimeSpan.FromMinutes(1), 60, Token);

        Assert.Equal(new WaitEnd<int>(WaitEndKind.Bound, 0, TimeSpan.FromMinutes(7)), end);
        Assert.True(given.IsCancellationRequested, "the work is cancelled at the bound");
        Assert.Equal(["has waited 5m00s for the forge's checks"], told.Select(one => one.Line()));
    }

    /// <summary>
    /// A moment less than a millisecond ahead is waited out by a whole delay, the least a delay can be asked for: asked
    /// for less, a delay is asked for none and ends at once, so a clock only its delays move - as one a test drives is -
    /// could never pass the moment, and a wait measured on it would never end. The wait runs beside the test so a
    /// <see cref="ClockDrive"/> that never sees it arm a delay fails the test, rather than the wait taking the test's own
    /// thread with it.
    /// </summary>
    [Fact]
    public async Task AMomentLessThanAMillisecondAhead_IsWaitedOutByAWholeDelay()
    {
        var clock = new TimerClock();
        var moment = clock.GetUtcNow() + TimeSpan.FromTicks(2);
        var run = Task.Run(() => Waits.UntilAsync(moment, new WaitWatch("the clock to pass a site's date", _ => { }, null, clock), CancellationToken.None), CancellationToken.None);

        var end = await ClockDrive.UntilEndedAsync(clock, run, TimeSpan.FromMilliseconds(2), 10, Token);

        Assert.Equal(new WaitEnd<bool>(WaitEndKind.Done, true, TimeSpan.FromMilliseconds(2)), end);
        Assert.Equal(0, clock.Waiting);
    }

    /// <summary>
    /// The pause a wait asks of a delay is never shorter than a millisecond, the least a delay can be asked for; where
    /// nothing is left of what is allowed, or nothing is asked, it is no pause at all.
    /// </summary>
    [Fact]
    public void APauseIsNeverShorterThanADelayCanBeAskedFor()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(1), Waits.Shorter(TimeSpan.FromTicks(2), Waits.SaidEvery));
        Assert.Equal(TimeSpan.FromMilliseconds(1), Waits.Shorter(Waits.SaidEvery, TimeSpan.FromMilliseconds(1)));
        Assert.Equal(TimeSpan.FromSeconds(2), Waits.Shorter(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(2)));
        Assert.Equal(TimeSpan.Zero, Waits.Shorter(TimeSpan.FromSeconds(3), TimeSpan.Zero));
        Assert.Equal(TimeSpan.Zero, Waits.Shorter(TimeSpan.Zero, Waits.SaidEvery));
    }

    private static Task<T> Unending<T>(CancellationToken token)
    {
        var never = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = token.Register(() => never.TrySetCanceled(token));
        return never.Task;
    }

    /// <summary>
    /// A machine on which one holder lives, by its id and stamp, until the test ends it, and no other process lives. Every
    /// liveness asked of it is kept, so a test sees what the wait asked by.
    /// </summary>
    /// <param name="id">The holder's process id.</param>
    /// <param name="started">The holder's stamp.</param>
    private sealed class LivingHolder(int id, string started) : IProcessIdentity
    {
        private volatile bool _ended;

        /// <summary>Each liveness asked, as the id and stamp it was asked by, in order.</summary>
        public ConcurrentQueue<(int ProcessId, string? Stamp)> Asked { get; } = new();

        public int CurrentId => Environment.ProcessId;

        public string CurrentMachine => Environment.MachineName;

        public string? Current => null;

        public bool IsAlive(int processId, string? stamp)
        {
            Asked.Enqueue((processId, stamp));
            return Holds(processId, stamp);
        }

        public string? StampOf(int processId) => !_ended && processId == id ? started : null;

        public bool IsSame(int processId, string stamp) => Holds(processId, stamp);

        /// <summary>Ends the holder: every later look finds it gone.</summary>
        public void End() => _ended = true;

        private bool Holds(int processId, string? stamp)
            => !_ended && processId == id && string.Equals(stamp, started, StringComparison.Ordinal);
    }
}
