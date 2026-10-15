using RepoHarness.Core.Execution;

namespace RepoHarness.Tests;

/// <summary>
/// What a clock step is, for whoever times a window on both clocks: how far they disagreed, whichever ran ahead, and
/// whether that is past a tolerance - none being watched for where no tolerance is set.
/// </summary>
public sealed class ClockStepTests
{
    /// <summary>The drift is how far the two clocks disagreed across the window, whichever ran ahead, or back.</summary>
    [Theory]
    [InlineData(100, 10, 90)]
    [InlineData(10, 100, 90)]
    [InlineData(-80, 10, 90)]
    [InlineData(30, 30, 0)]
    public void Drift_IsHowFarTheClocksDisagreed_WhicheverWay(int wallSeconds, int monotonicSeconds, int driftSeconds)
        => Assert.Equal(TimeSpan.FromSeconds(driftSeconds), ClockStep.Drift(TimeSpan.FromSeconds(wallSeconds), TimeSpan.FromSeconds(monotonicSeconds)));

    /// <summary>A drift is a step past its tolerance, never at it, and never where no tolerance is set.</summary>
    [Theory]
    [InlineData(2001, 2000, true)]
    [InlineData(2000, 2000, false)]
    [InlineData(1999, 2000, false)]
    [InlineData(2, 1, true)]
    [InlineData(1, 1, false)]
    [InlineData(90000, 0, false)]
    [InlineData(90000, -1, false)]
    public void IsPast_IsADriftPastItsTolerance_AndNeverWhereNoneIsSet(int driftMilliseconds, int tolerance, bool stepped)
        => Assert.Equal(stepped, ClockStep.IsPast(TimeSpan.FromMilliseconds(driftMilliseconds), tolerance));
}
