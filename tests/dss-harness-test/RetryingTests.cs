using System.Collections.Concurrent;
using DssHarness.Core.Execution;
using DssHarness.Core.Waiting;

namespace DssHarness.Tests;

/// <summary>
/// The harness's one way of trying again: while the failure can change - a cause that passes waited out as long as it
/// lasts, as long as it says; a failure retried while it is new - and stopped where it cannot, never at a count of tries
/// or a cap on a wait of the code's own. Every wait is told as it begins and every five minutes while it lasts.
/// </summary>
public sealed class RetryingTests
{
    private const string Checks = "the forge's checks";
    private const string Busy = "the forge is busy";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// A cause that passes is tried again for as long as it lasts - fifty times here, then the answer - each wait told as
    /// it begins, saying how long the run has waited and when it tries again. A passing cause is never counted: the
    /// fiftieth says no more about the next try than the first did, so a count of them would end a run that was about to
    /// succeed.
    /// </summary>
    [Fact]
    public async Task APassingCause_IsRetriedWhileItLasts_ToldEachTime()
    {
        var clock = new TimerClock();
        var told = new ConcurrentQueue<WaitTold>();
        var tries = new ConcurrentQueue<(int Try, TimeSpan At)>();

        var run = Retrying.RunAsync(
            (attempt, _) =>
            {
                tries.Enqueue((attempt, clock.Moved));
                return Task.FromResult<RetryTry<string>>(
                    attempt <= 50 ? new RetryTry<string>.Passing(Busy, TimeSpan.FromSeconds(30)) : new RetryTry<string>.Done("green"));
            },
            new RetryWatch(Checks, told.Enqueue, TimeSpan.FromSeconds(2), null, clock),
            Token);

        var end = await ClockDrive.UntilEndedAsync(clock, run, TimeSpan.FromSeconds(30), 100, Token);

        Assert.Equal(new RetryEnd<string>(RetryEndKind.Done, "green", 51, Busy), end);
        Assert.Equal(Enumerable.Range(1, 51).Select(attempt => (Try: attempt, At: TimeSpan.FromSeconds(30 * (attempt - 1)))), tries);
        Assert.Equal(
            Enumerable.Range(0, 50).Select(waited => new WaitTold(Checks, TimeSpan.FromSeconds(30 * waited), null, "the forge is busy; tries again in 30s")),
            told);
    }

    /// <summary>
    /// A failure is tried again while its digest is new, and the run ends <see cref="RetryEndKind.Repeated"/> on a digest
    /// it met before - not only the last one: the same failure on the same state comes again however often it is tried.
    /// Each failure's wait is the doubling wait, from the first.
    /// </summary>
    [Fact]
    public async Task AFailedDigestSeenTwice_EndsRepeated()
    {
        var clock = new TimerClock();
        var told = new ConcurrentQueue<WaitTold>();
        var tries = new ConcurrentQueue<(int Try, TimeSpan At)>();
        (string Failure, string Digest)[] failures = [("lint failed", "lint@4f2c"), ("build failed", "build@4f2c"), ("lint failed", "lint@4f2c")];

        var run = Retrying.RunAsync(
            (attempt, _) =>
            {
                tries.Enqueue((attempt, clock.Moved));
                return Task.FromResult<RetryTry<string>>(
                    attempt <= failures.Length
                        ? new RetryTry<string>.Failed(failures[attempt - 1].Failure, failures[attempt - 1].Digest)
                        : new RetryTry<string>.Done("past the repeat"));
            },
            new RetryWatch(Checks, told.Enqueue, TimeSpan.FromSeconds(2), null, clock),
            Token);

        var end = await ClockDrive.UntilEndedAsync(clock, run, TimeSpan.FromSeconds(1), 100, Token);

        Assert.Equal(new RetryEnd<string>(RetryEndKind.Repeated, null, 3, "lint failed"), end);
        Assert.Equal([(1, TimeSpan.Zero), (2, TimeSpan.FromSeconds(2)), (3, TimeSpan.FromSeconds(6))], tries);
        Assert.Equal(
            [
                new WaitTold(Checks, TimeSpan.Zero, null, "lint failed; tries again in 2s"),
                new WaitTold(Checks, TimeSpan.FromSeconds(2), null, "build failed; tries again in 4s"),
            ],
            told);
    }

    /// <summary>
    /// A cause that says to wait a day is waited a day, exactly as given, and the next try made then: a wait cut at a cap
    /// of the code's own tries again against a cause that said it had not passed. The day is told as it begins and every
    /// five minutes while it lasts, each line saying how long is left of it.
    /// </summary>
    [Fact]
    public async Task ARetryAfterOfADay_IsWaitedAsGiven()
    {
        const string Spent = "the forge's rate limit is spent";
        var day = TimeSpan.FromDays(1);
        var clock = new TimerClock();
        var told = new ConcurrentQueue<WaitTold>();
        var tries = new ConcurrentQueue<(int Try, TimeSpan At)>();

        var run = Retrying.RunAsync(
            (attempt, _) =>
            {
                tries.Enqueue((attempt, clock.Moved));
                return Task.FromResult<RetryTry<string>>(attempt == 1 ? new RetryTry<string>.Passing(Spent, day) : new RetryTry<string>.Done("green"));
            },
            new RetryWatch(Checks, told.Enqueue, TimeSpan.FromSeconds(2), null, clock),
            Token);

        var end = await ClockDrive.UntilEndedAsync(clock, run, Waits.SaidEvery, 300, Token);

        List<WaitTold> expected = [new(Checks, TimeSpan.Zero, null, $"{Spent}; tries again in 24h00m")];

        for (var waited = Waits.SaidEvery; waited < day; waited += Waits.SaidEvery)
        {
            expected.Add(new(Checks, waited, null, $"{Spent}; tries again in {LedgerReport.FormatDuration(day - waited)}"));
        }

        Assert.Equal(new RetryEnd<string>(RetryEndKind.Done, "green", 2, Spent), end);
        Assert.Equal([(1, TimeSpan.Zero), (2, day)], tries);
        Assert.Equal(288, expected.Count);
        Assert.Equal(expected, told);
    }

    /// <summary>
    /// A failure no retry changes ends the run at once, after its one try, with nothing waited and nothing told: trying a
    /// refused token again only meets the refusal again.
    /// </summary>
    [Fact]
    public async Task AFinalFailure_EndsAtOnce()
    {
        const string Refused = "the forge refused the token: 401";
        var clock = new TimerClock();
        var told = new ConcurrentQueue<WaitTold>();
        var tries = new ConcurrentQueue<int>();

        var run = Retrying.RunAsync(
            (attempt, _) =>
            {
                tries.Enqueue(attempt);
                return Task.FromResult<RetryTry<string>>(attempt == 1 ? new RetryTry<string>.Final(Refused) : new RetryTry<string>.Done("past the refusal"));
            },
            new RetryWatch(Checks, told.Enqueue, TimeSpan.FromSeconds(2), null, clock),
            Token);

        var end = await ClockDrive.UntilEndedAsync(clock, run, TimeSpan.FromSeconds(1), 10, Token);

        Assert.Equal(new RetryEnd<string>(RetryEndKind.Final, null, 1, Refused), end);
        Assert.Equal([1], tries);
        Assert.Empty(told);
        Assert.Equal(TimeSpan.Zero, clock.Moved);
        Assert.Equal(0, clock.Waiting);
    }

    /// <summary>
    /// A wait no cause names the length of starts at the first wait, doubles up to five minutes - the cadence a wait is
    /// told at - and then stays there, try after try: the pace slows to what a person is told at, and the tries go on.
    /// </summary>
    [Fact]
    public async Task TheDoublingWait_StartsAtTheFirstWait_DoublesUpToFiveMinutes_ThenStays()
    {
        const string Answered = "the forge answered 503";
        var clock = new TimerClock();
        var told = new ConcurrentQueue<WaitTold>();
        var tries = new ConcurrentQueue<(int Try, TimeSpan At)>();

        var run = Retrying.RunAsync(
            (attempt, _) =>
            {
                tries.Enqueue((attempt, clock.Moved));
                return Task.FromResult<RetryTry<string>>(attempt <= 11 ? new RetryTry<string>.Passing(Answered, null) : new RetryTry<string>.Done("green"));
            },
            new RetryWatch(Checks, told.Enqueue, TimeSpan.FromSeconds(2), null, clock),
            Token);

        var end = await ClockDrive.UntilEndedAsync(clock, run, TimeSpan.FromSeconds(2), 800, Token);

        int[] at = [0, 2, 6, 14, 30, 62, 126, 254, 510, 810, 1110, 1410];
        string[] again = ["2s", "4s", "8s", "16s", "32s", "1m04s", "2m08s", "4m16s", "5m00s", "5m00s", "5m00s"];

        Assert.Equal(new RetryEnd<string>(RetryEndKind.Done, "green", 12, Answered), end);
        Assert.Equal(at.Select((seconds, index) => (Try: index + 1, At: TimeSpan.FromSeconds(seconds))), tries);
        Assert.Equal(
            again.Select((said, index) => new WaitTold(Checks, TimeSpan.FromSeconds(at[index]), null, $"{Answered}; tries again in {said}")),
            told);
    }

    /// <summary>
    /// A wait longer than what is left of the user's bound ends at the bound, and the run with it, saying so - told as
    /// before until then, and with no further try: the person's bound ends an unattended run, a cause's long wait
    /// included.
    /// </summary>
    [Fact]
    public async Task ABoundEndsARetryWait_AtTheBound()
    {
        var clock = new TimerClock();
        var told = new ConcurrentQueue<WaitTold>();
        var tries = new ConcurrentQueue<(int Try, TimeSpan At)>();

        var run = Retrying.RunAsync(
            (attempt, _) =>
            {
                tries.Enqueue((attempt, clock.Moved));
                return Task.FromResult<RetryTry<string>>(attempt == 1 ? new RetryTry<string>.Passing(Busy, TimeSpan.FromHours(1)) : new RetryTry<string>.Done("past the bound"));
            },
            new RetryWatch(Checks, told.Enqueue, TimeSpan.FromSeconds(2), TimeSpan.FromMinutes(12), clock),
            Token);

        var end = await ClockDrive.UntilEndedAsync(clock, run, TimeSpan.FromMinutes(1), 120, Token);

        Assert.Equal(new RetryEnd<string>(RetryEndKind.Bound, null, 1, Busy), end);
        Assert.Equal([(1, TimeSpan.Zero)], tries);
        Assert.Equal(TimeSpan.FromMinutes(12), clock.Moved);
        Assert.Equal(
            [
                new WaitTold(Checks, TimeSpan.Zero, null, "the forge is busy; tries again in 1h00m"),
                new WaitTold(Checks, TimeSpan.FromMinutes(5), null, "the forge is busy; tries again in 55m00s"),
                new WaitTold(Checks, TimeSpan.FromMinutes(10), null, "the forge is busy; tries again in 50m00s"),
            ],
            told);
    }

    /// <summary>
    /// The caller's token ends a wait between tries at once, with no further try, leaving nothing on the clock: a wait of
    /// an hour that cannot be stopped holds its command for the hour.
    /// </summary>
    [Fact]
    public async Task ARetryWaitIsCancellable()
    {
        var clock = new TimerClock();
        var told = new ConcurrentQueue<WaitTold>();
        var tries = new ConcurrentQueue<int>();
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);

        var run = Retrying.RunAsync(
            (attempt, _) =>
            {
                tries.Enqueue(attempt);
                return Task.FromResult<RetryTry<string>>(attempt == 1 ? new RetryTry<string>.Passing(Busy, TimeSpan.FromHours(1)) : new RetryTry<string>.Done("past the cancellation"));
            },
            new RetryWatch(Checks, told.Enqueue, TimeSpan.FromSeconds(2), null, clock),
            cancel.Token);

        await clock.WaitingAsync().WaitAsync(ClockDrive.Within, Token);
        clock.Advance(Waits.SaidEvery);
        await clock.WaitingAsync().WaitAsync(ClockDrive.Within, Token);
        await cancel.CancelAsync();

        // Cancelled, the run has dropped its wait: one that never heard the token would still wait on the clock.
        Assert.Same(run, await Task.WhenAny(run, clock.WaitingAsync()).WaitAsync(ClockDrive.Within, Token));
        Assert.Equal(new RetryEnd<string>(RetryEndKind.Cancelled, null, 1, Busy), await run);
        Assert.Equal([1], tries);
        Assert.Equal(
            [
                new WaitTold(Checks, TimeSpan.Zero, null, "the forge is busy; tries again in 1h00m"),
                new WaitTold(Checks, Waits.SaidEvery, null, "the forge is busy; tries again in 55m00s"),
            ],
            told);
    }
}
