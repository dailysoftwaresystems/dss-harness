using RepoHarness.Core.Platform;
using RepoHarness.Core.Results;

namespace RepoHarness.Tests;

/// <summary>
/// The machine-wide lock every read-decide-write here is taken under, opened through the race the
/// runtime's own first opening has on Linux and macOS.
/// </summary>
public sealed class MachineWideMutexTests
{
    /// <summary>
    /// An open that fails for a moment - as two processes racing to create the runtime's directory for
    /// named mutexes make it fail - is tried again, a little later each time, and the mutex it then
    /// opens is the one returned.
    /// </summary>
    [Fact]
    public void AnOpenThatFailsForAMoment_IsTriedAgain_ALittleLaterEachTime()
    {
        var name = MachineWideMutex.NameFor("test", [Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))], ignoreCase: true);
        var tries = 0;
        var pauses = new List<TimeSpan>();

        using var mutex = MachineWideMutex.Open(
            name,
            "a test's subject",
            () => ++tries < 3
                ? throw new IOException("One or more system calls failed: stat(\"/tmp/.dotnet/shm\", ...) == -1; errno == ENOENT;")
                : new Mutex(initiallyOwned: false, name),
            pauses.Add);

        Assert.Equal(3, tries);
        Assert.Equal([MachineWideMutex.Backoff, MachineWideMutex.Backoff * 2], pauses);
        Assert.True(MachineWideMutex.Wait(mutex, TimeSpan.FromSeconds(5)));
        mutex.ReleaseMutex();
    }

    /// <summary>
    /// One that keeps failing is said as this machine being unavailable, naming the lock, what the system
    /// said the last time and the fix - never an internal error, never a refusal that would end a run of
    /// which this machine is one host, and never a run that goes on without the lock.
    /// </summary>
    [Fact]
    public void AnOpenThatKeepsFailing_IsUnavailable_NamingTheLockAndWhatTheSystemSaidLast()
    {
        var tries = 0;

        var refusal = Assert.Throws<HarnessException>(() => MachineWideMutex.Open(
            @"Global\repo-harness-test-lock",
            "a test's subject",
            () =>
            {
                tries++;
                throw new IOException($"One or more system calls failed: mkdir(\"/tmp/.dotnet/shm/global\", ...) == -1; errno == EEXIST; try {tries}");
            },
            _ => { }));

        Assert.Equal(MachineWideMutex.Attempts, tries);
        Assert.Equal(HarnessExit.HostUnavailable, refusal.ExitCode);
        Assert.Contains(@"'Global\repo-harness-test-lock' on a test's subject", refusal.Message, StringComparison.Ordinal);
        Assert.Contains($"errno == EEXIST; try {MachineWideMutex.Attempts}.", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("fix what the system said, and run the command again", refusal.Message, StringComparison.Ordinal);
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

    /// <summary>A mutex another user holds is refused at once: no retry makes it this user's.</summary>
    [Fact]
    public void AMutexAnotherUserHolds_IsRefusedAtOnce()
    {
        var tries = 0;

        var refusal = Assert.Throws<HarnessException>(() => MachineWideMutex.Open(
            @"Global\repo-harness-test-lock",
            "a test's subject",
            () =>
            {
                tries++;
                throw new UnauthorizedAccessException();
            },
            _ => throw new InvalidOperationException("paused before refusing")));

        Assert.Equal(1, tries);
        Assert.Equal(HarnessExit.Refused, refusal.ExitCode);
        Assert.Contains("belongs to another user", refusal.Message, StringComparison.Ordinal);
    }
}
