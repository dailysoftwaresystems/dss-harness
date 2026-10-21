using DssHarness.Core.Execution;
using DssHarness.Core.Orchestration.Flow;
using DssHarness.Core.Results;

namespace DssHarness.Tests;

/// <summary>
/// When a step that ended incomplete is run again, and how long it waits first: every row of the retry table, and the
/// codes no row names - which fail and are never run again.
/// </summary>
public sealed class FlowRetryTests
{
    /// <summary>An incomplete run is tried again at once, then after 5 s, then 10 s.</summary>
    [Fact]
    public void AnIncompleteRun_IsTriedAgainAtOnceThen5sThen10s()
    {
        Assert.Equal(TimeSpan.Zero, FlowRetry.Wait(HarnessExit.Incomplete, OutcomeCause.None, 0));
        Assert.Equal(TimeSpan.FromSeconds(5), FlowRetry.Wait(HarnessExit.Incomplete, OutcomeCause.None, 1));
        Assert.Equal(TimeSpan.FromSeconds(10), FlowRetry.Wait(HarnessExit.Incomplete, OutcomeCause.None, 2));
        Assert.Null(FlowRetry.Wait(HarnessExit.Incomplete, OutcomeCause.None, 3));
    }

    /// <summary>A leg whose inputs moved or could not be measured is tried again after 10 s, 30 s, 60 s: the tree settles.</summary>
    [Fact]
    public void AnInputsMovedLeg_IsTriedAgainAfter10s30s60s()
    {
        Assert.Equal([TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60)],
            new[] { 0, 1, 2 }.Select(attempt => FlowRetry.Wait(LegExit.InputsMoved, OutcomeCause.None, attempt)));
        Assert.Null(FlowRetry.Wait(LegExit.InputsMoved, OutcomeCause.None, 3));
    }

    /// <summary>A build directory another process used, and a leg not admitted, are tried again after 30 s, 60 s, 120 s.</summary>
    [Fact]
    public void AContendedOrNotAdmittedLeg_IsTriedAgainAfter30s60s120s()
    {
        foreach (var exit in new[] { LegExit.Contended, LegExit.NotAdmitted })
        {
            Assert.Equal([TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(120)],
                new[] { 0, 1, 2 }.Select(attempt => FlowRetry.Wait(exit, OutcomeCause.None, attempt)));
            Assert.Null(FlowRetry.Wait(exit, OutcomeCause.None, 3));
        }
    }

    /// <summary>A refusal whose only obstacle is a held lock is tried again after 5 s, doubling to 60 s.</summary>
    [Fact]
    public void AHeldLock_IsTriedAgainAfter5sDoublingTo60s()
    {
        Assert.Equal(
            [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(40), TimeSpan.FromSeconds(60)],
            new[] { 0, 1, 2, 3, 4 }.Select(attempt => FlowRetry.Wait(HarnessExit.Refused, OutcomeCause.LockHeld, attempt)));
        Assert.Null(FlowRetry.Wait(HarnessExit.Refused, OutcomeCause.LockHeld, 5));
    }

    /// <summary>Every other code is a failure or a refusal running again does not change: no retry, whatever its cause.</summary>
    [Theory]
    [InlineData(20)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(8)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(14)]
    [InlineData(15)]
    [InlineData(70)]
    public void AnyOtherCode_IsNotTriedAgain(int exit)
    {
        Assert.False(FlowRetry.RunsAgain(exit, OutcomeCause.None));
        Assert.Null(FlowRetry.Wait(exit, OutcomeCause.None, 0));
    }

    /// <summary>A 13 whose cause is not a held lock is a refusal running again would repeat: it is not retried.</summary>
    [Fact]
    public void ARefusalThatIsNotAHeldLock_IsNotTriedAgain()
    {
        Assert.False(FlowRetry.RunsAgain(HarnessExit.Refused, OutcomeCause.None));
        Assert.Null(FlowRetry.Wait(HarnessExit.Refused, OutcomeCause.None, 0));
    }
}
