using DssHarness.Core.Platform;
using DssHarness.Core.Results;
using DssHarness.Core.Waiting;

namespace DssHarness.Tests;

/// <summary>
/// The machine-wide lock every read-decide-write here is taken under, opened through the race the
/// runtime's own first opening has on Linux and macOS.
/// </summary>
public sealed class MachineWideMutexTests
{
    /// <summary>
    /// The lock is named for the tool, the subject's kind and a digest of its paths - one name for the same paths in any
    /// order, and, where the platform ignores case, in any case.
    /// </summary>
    [Fact]
    public void TheName_IsTheToolsTheKindsAndADigestOfThePaths()
    {
        var paths = new[] { Path.Combine(Path.GetTempPath(), "b"), Path.Combine(Path.GetTempPath(), "a") };
        var name = MachineWideMutex.NameFor("run", paths, ignoreCase: true);

        Assert.Matches(@"^Global\\dss-harness-run-[0-9a-f]{32}$", name);
        Assert.Equal(name, MachineWideMutex.NameFor("run", [.. paths.Reverse()], ignoreCase: true));
        Assert.Equal(name, MachineWideMutex.NameFor("run", [.. paths.Select(path => path.ToUpperInvariant())], ignoreCase: true));
    }

    /// <summary>
    /// An open that fails for a moment - as two processes racing to create the runtime's directory for named mutexes make
    /// it fail - is tried again after a wait that doubles from the first, and the mutex it then opens is the one returned.
    /// </summary>
    [Fact]
    public async Task AnOpenThatFailsForAMoment_IsTriedAgain_ALittleLaterEachTime()
    {
        var name = MachineWideMutex.NameFor("test", [Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))], ignoreCase: true);
        var clock = new TimerClock();
        var tries = 0;

        using var mutex = await ClockDrive.UntilEndedAsync(
            clock,
            Task.Run(() => MachineWideMutex.Open(
                name,
                "a test's subject",
                () => ++tries switch
                {
                    1 => throw new IOException("One or more system calls failed: stat(\"/tmp/.dotnet/shm\", ...) == -1; errno == ENOENT;"),
                    2 => throw new IOException("One or more system calls failed: mkdir(\"/tmp/.dotnet/shm\", ...) == -1; errno == EEXIST;"),
                    _ => new Mutex(initiallyOwned: false, name),
                },
                clock)),
            MachineWideMutex.FirstWait,
            mostSteps: 10,
            TestContext.Current.CancellationToken);

        Assert.Equal(3, tries);
        Assert.Equal(MachineWideMutex.FirstWait * 3, clock.Moved);
        Assert.True(MachineWideMutex.Wait(mutex, TimeSpan.FromSeconds(5)));
        mutex.ReleaseMutex();
    }

    /// <summary>
    /// An open the system fails in a new way each time is tried again for as long as it does - past any count of tries:
    /// nine failures, each saying something new, and then the mutex.
    /// </summary>
    [Fact]
    public async Task TheMutexOpen_IsRetriedWhileTransient()
    {
        var name = MachineWideMutex.NameFor("test", [Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))], ignoreCase: true);
        var clock = new TimerClock();
        var tries = 0;

        using var mutex = await ClockDrive.UntilEndedAsync(
            clock,
            Task.Run(() => MachineWideMutex.Open(
                name,
                "a test's subject",
                () => ++tries <= 9
                    ? throw new IOException($"One or more system calls failed: mkdir(\"/tmp/.dotnet/shm/global\", ...) == -1; errno == EEXIST; try {tries}")
                    : new Mutex(initiallyOwned: false, name),
                clock)),
            Waits.SaidEvery,
            mostSteps: 20,
            TestContext.Current.CancellationToken);

        Assert.Equal(10, tries);
        Assert.True(MachineWideMutex.Wait(mutex, TimeSpan.FromSeconds(5)));
        mutex.ReleaseMutex();
    }

    /// <summary>
    /// One that fails the same way twice is said as this machine being unavailable, naming the lock, what the system said
    /// and the fix - never an internal error, never a refusal that would end a run of which this machine is one host, and
    /// never a run that goes on without the lock: the same answer twice is one no further try changes.
    /// </summary>
    [Fact]
    public async Task AnOpenThatKeepsFailing_IsUnavailable_NamingTheLockAndWhatTheSystemSaid()
    {
        var clock = new TimerClock();
        var tries = 0;

        var refusal = await Assert.ThrowsAsync<HarnessException>(() => ClockDrive.UntilEndedAsync(
            clock,
            Task.Run(() => MachineWideMutex.Open(
                @"Global\dss-harness-test-lock",
                "a test's subject",
                () =>
                {
                    tries++;
                    throw new IOException("One or more system calls failed: mkdir(\"/tmp/.dotnet/shm/global\", ...) == -1; errno == ENOSPC.");
                },
                clock)),
            Waits.SaidEvery,
            mostSteps: 10,
            TestContext.Current.CancellationToken));

        Assert.Equal(2, tries);
        Assert.Equal(HarnessExit.HostUnavailable, refusal.ExitCode);
        Assert.Equal(
            "The lock 'Global\\dss-harness-test-lock' on a test's subject could not be opened, the system refusing it the same way twice, saying: "
            + "One or more system calls failed: mkdir(\"/tmp/.dotnet/shm/global\", ...) == -1; errno == ENOSPC. Nothing it guards was read or changed, "
            + "and nothing can run on this machine until it opens: fix what the system said, and run the command again.",
            refusal.Message);
    }

    /// <summary>
    /// A wait the system ends before the window has passed is waited again, for what is left of the window by a clock that
    /// never steps. Linux ends a wait for a named mutex at a deadline on the wall clock, which a WSL clock stepping forward
    /// by 24.8 seconds passes at once: a wait half a second old ended as though the whole window had passed.
    /// </summary>
    [Fact]
    public void AWaitEndedBeforeItsWindow_IsWaitedAgain_ForWhatIsLeftOfIt()
    {
        var elapsed = TimeSpan.Zero;
        var asked = new List<TimeSpan>();

        var taken = MachineWideMutex.Wait(
            slice =>
            {
                asked.Add(slice);
                elapsed += TimeSpan.FromMilliseconds(500);
                return asked.Count == 2;
            },
            TimeSpan.FromSeconds(10),
            () => elapsed);

        Assert.True(taken);
        Assert.Equal([TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(9.5)], asked);
    }

    /// <summary>
    /// A mutex nobody lets go of is given up on once the window has passed by that clock, and not before: a wait the system
    /// ends early is waited again, and one that lasts to the end of the window is the last. A window of nothing is one try.
    /// </summary>
    [Theory]
    [InlineData(10_000, new[] { 10_000.0, 9_800.0 })]
    [InlineData(0, new[] { 0.0 })]
    public void AMutexNobodyLetsGoOf_IsGivenUpOnOnceTheWindowHasPassed(int windowMilliseconds, double[] expected)
    {
        var elapsed = TimeSpan.Zero;
        var asked = new List<TimeSpan>();

        var taken = MachineWideMutex.Wait(
            slice =>
            {
                asked.Add(slice);

                // The first wait ended early, by a step; any after it lasts what it was given.
                elapsed += asked.Count == 1 && slice > TimeSpan.Zero ? TimeSpan.FromMilliseconds(200) : slice;
                return false;
            },
            TimeSpan.FromMilliseconds(windowMilliseconds),
            () => elapsed);

        Assert.False(taken);
        Assert.Equal(expected.Select(TimeSpan.FromMilliseconds), asked);
    }

    /// <summary>
    /// A window already passed when the wait first looks - its thread held up past it - is one try that does not wait: what
    /// is left of it is never handed to the system below nothing, which reads one millisecond below as for ever, and
    /// refuses the rest.
    /// </summary>
    [Fact]
    public void AWindowAlreadyPassedAtTheFirstLook_IsOneTryThatDoesNotWait()
    {
        var asked = new List<TimeSpan>();

        var taken = MachineWideMutex.Wait(
            slice =>
            {
                asked.Add(slice);
                return false;
            },
            TimeSpan.FromMilliseconds(500),
            () => TimeSpan.FromSeconds(1));

        Assert.False(taken);
        Assert.Equal([TimeSpan.Zero], asked);
    }

    /// <summary>
    /// A mutex whose holder ended holding it - a thread here, as a process killed holding one is elsewhere - is taken, as one
    /// let go is: everything it guards is written by replacing a whole file, so nothing its holder left is half written.
    /// Taken once: one release lets it go, for whoever waits next. Waited for again, as one not taken, it would be this
    /// thread's twice, and still held once the lock was let go.
    /// </summary>
    [Fact]
    public void AMutexItsHolderEndedHolding_IsTakenOnce()
    {
        using var mutex = new Mutex();
        var holder = new Thread(() => mutex.WaitOne());
        var takenNext = false;
        var next = new Thread(() =>
        {
            takenNext = mutex.WaitOne(0);

            if (takenNext)
            {
                mutex.ReleaseMutex();
            }
        });

        holder.Start();
        holder.Join();

        Assert.True(MachineWideMutex.Wait(mutex, TimeSpan.FromSeconds(5)));
        mutex.ReleaseMutex();

        next.Start();
        next.Join();

        Assert.True(takenNext, "the mutex was still held once it was let go: it had been taken twice");
    }

    /// <summary>
    /// A hold, for work that awaits while it holds the mutex, keeps every other holder out until it is let go, and is let
    /// go from whichever thread lets it go: a mutex is released by the thread that took it, and the work's last await may
    /// end on any thread. Let go twice, it is let go once.
    /// </summary>
    [Fact]
    public void AHold_KeepsEveryOtherOutUntilLetGo_FromWhicheverThreadLetsItGo()
    {
        var name = MachineWideMutex.NameFor("test", [Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))], ignoreCase: true);
        var hold = MachineWideMutex.TryHold(name, "a test's subject");
        Exception? letGo = null;

        Assert.NotNull(hold);
        Assert.Null(MachineWideMutex.TryHold(name, "a test's subject"));

        var other = new Thread(() =>
        {
            try
            {
                hold.Dispose();
            }
            catch (Exception ex)
            {
                letGo = ex;
            }
        });

        other.Start();
        other.Join();

        Assert.Null(letGo);
        Assert.True(hold.Released);

        hold.Dispose();

        using var next = MachineWideMutex.TryHold(name, "a test's subject");

        Assert.NotNull(next);
        Assert.False(next.Released);
    }

    /// <summary>A mutex another user holds is refused at once: no retry makes it this user's.</summary>
    [Fact]
    public void AMutexAnotherUserHolds_IsRefusedAtOnce()
    {
        var tries = 0;

        var refusal = Assert.Throws<HarnessException>(() => MachineWideMutex.Open(
            @"Global\dss-harness-test-lock",
            "a test's subject",
            () =>
            {
                tries++;
                throw new UnauthorizedAccessException();
            },
            TimeProvider.System));

        Assert.Equal(1, tries);
        Assert.Equal(HarnessExit.Refused, refusal.ExitCode);
        Assert.Contains("belongs to another user", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A waiting take waits for the mutex and says it waited, taking it where its holder let it go before the wait's whole
    /// window passed - the caller awaiting the hold, so the thread that waits is the hold's own; a take whose first look
    /// found it free says it waited not at all.
    /// </summary>
    [Fact]
    public async Task AWaitingTake_WaitsForTheMutex_AndSaysItWaited()
    {
        var name = Name();
        using var held = MachineWideMutex.TryHold(name, "a test's subject");

        Assert.NotNull(held);
        Assert.Equal(TimeSpan.Zero, held.Waited);

        var began = MachineWideHold.WaitBegins(name);
        var waiting = MachineWideMutex.HoldAsync(name, "a test's subject", Timeout.InfiniteTimeSpan, TestContext.Current.CancellationToken);

        // Let go only once the take met the hold and waits: it has waited then, however loaded the machine is.
        await began.WaitAsync(TestContext.Current.CancellationToken);
        Assert.False(waiting.IsCompleted, "the take answered while the mutex was held");

        held.Dispose();

        using var taken = await waiting;

        Assert.NotNull(taken);
        Assert.True(taken.Waited > TimeSpan.Zero, $"waited {taken.Waited}");
    }

    /// <summary>
    /// A waiting take whose window passes without the mutex answers null, and one a token ends while it waits answers null:
    /// neither holds what it did not take, so the mutex is free the moment its one holder lets it go.
    /// </summary>
    [Fact]
    public async Task AWaitingTake_ItsWindowPassedOrItsTokenEnded_AnswersNull_HoldingNothing()
    {
        var name = Name();
        using var held = MachineWideMutex.TryHold(name, "a test's subject");

        Assert.NotNull(held);
        Assert.Null(await MachineWideMutex.HoldAsync(name, "a test's subject", TimeSpan.FromMilliseconds(200), TestContext.Current.CancellationToken));

        using var ended = new CancellationTokenSource();
        var began = MachineWideHold.WaitBegins(name);
        var waiting = MachineWideMutex.HoldAsync(name, "a test's subject", Timeout.InfiniteTimeSpan, ended.Token);

        await began.WaitAsync(TestContext.Current.CancellationToken);
        await ended.CancelAsync();

        Assert.Null(await waiting);

        held.Dispose();

        using var next = MachineWideMutex.TryHold(name, "a test's subject");

        Assert.NotNull(next);
    }

    private static string Name()
        => MachineWideMutex.NameFor("test", [Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))], ignoreCase: true);
}
