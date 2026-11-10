using DssHarness.Core.Orchestration;

namespace DssHarness.Tests;

/// <summary>What a side-task's chat says of a wait for the main tree's lock - the one form every client says it in.</summary>
public sealed class MainTreeWriteLockTests
{
    /// <summary>
    /// A take that met nobody says nothing; one shorter than a tenth of a second says it waited a moment, never "0 s";
    /// a longer one says its seconds to a tenth.
    /// </summary>
    [Fact]
    public void AWait_IsSaidInItsOneForm_AMomentUnderATenth_ItsSecondsToATenthAbove()
    {
        Assert.Null(MainTreeWriteLock.WaitedNotice(TimeSpan.Zero));
        Assert.Equal(
            $"waited a moment for {MainTreeWriteLock.WaitedOutOnFromSideTask} to end",
            MainTreeWriteLock.WaitedNotice(TimeSpan.FromMilliseconds(40)));
        Assert.Equal(
            $"waited 1.3 s for {MainTreeWriteLock.WaitedOutOnFromSideTask} to end",
            MainTreeWriteLock.WaitedNotice(TimeSpan.FromMilliseconds(1260)));
        Assert.Equal(
            $"waited 12 s for {MainTreeWriteLock.WaitedOutOnFromSideTask} to end",
            MainTreeWriteLock.WaitedNotice(TimeSpan.FromSeconds(12)));
    }
}
