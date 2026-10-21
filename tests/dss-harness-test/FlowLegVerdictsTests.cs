using DssHarness.Core.Execution;
using DssHarness.Core.Orchestration.Flow;
using DssHarness.Core.Results;

namespace DssHarness.Tests;

/// <summary>
/// How the engine reads the legs of a step that runs them, each leg's verdict on its own: which are red, which
/// incomplete, which a failure that is not red - and the code the worst of each class ends with.
/// </summary>
public sealed class FlowLegVerdictsTests
{
    /// <summary>Every verdict of the table, in the class the engine reads it: a failed leg and a sweep's own failures are red, the failures a retry answers are incomplete, the rest fail without being red, and a leg that passed or was never asked for decides nothing.</summary>
    [Theory]
    [InlineData(LegVerdict.Failed, FlowLegClass.Red)]
    [InlineData(LegVerdict.Violated, FlowLegClass.Red)]
    [InlineData(LegVerdict.Survived, FlowLegClass.Red)]
    [InlineData(LegVerdict.Unattributed, FlowLegClass.Red)]
    [InlineData(LegVerdict.Unwitnessed, FlowLegClass.FailureThatIsNotRed)]
    [InlineData(LegVerdict.LogHeld, FlowLegClass.FailureThatIsNotRed)]
    [InlineData(LegVerdict.Poisoned, FlowLegClass.FailureThatIsNotRed)]
    [InlineData(LegVerdict.Unmeasured, FlowLegClass.Incomplete)]
    [InlineData(LegVerdict.InputsMoved, FlowLegClass.Incomplete)]
    [InlineData(LegVerdict.Contended, FlowLegClass.Incomplete)]
    [InlineData(LegVerdict.RefusedLocked, FlowLegClass.Incomplete)]
    [InlineData(LegVerdict.NotAdmitted, FlowLegClass.Incomplete)]
    [InlineData(LegVerdict.Stopped, FlowLegClass.Incomplete)]
    [InlineData(LegVerdict.SkippedUnavailable, FlowLegClass.Incomplete)]
    [InlineData(LegVerdict.SkippedToolMissing, FlowLegClass.Incomplete)]
    [InlineData(LegVerdict.Passed, FlowLegClass.None)]
    [InlineData(LegVerdict.SkippedNotSelected, FlowLegClass.None)]
    public void EveryVerdict_IsReadInTheClassTheEngineTakes(LegVerdict verdict, FlowLegClass @class)
    {
        Assert.Equal(@class, FlowLegVerdicts.ClassOf(verdict));
    }

    /// <summary>A red build, test or run ends with 20, and a red sweep with its worst red arm's own code by the table's order: violated outranks survived, which outranks unattributed.</summary>
    [Fact]
    public void TheWorstRedLeg_DecidesTheCodeByTheTablesOwnOrder()
    {
        var sweep = new List<FlowLeg> { new("arm-b", LegVerdict.Survived), new("arm-a", LegVerdict.Violated), new("arm-c", LegVerdict.Unattributed) };
        var build = new List<FlowLeg> { new("here", LegVerdict.Failed), new("cs", LegVerdict.Failed) };

        Assert.Equal(LegVerdict.Violated, FlowLegVerdicts.Worst(FlowLegVerdicts.OfClass(sweep, FlowLegClass.Red)));
        Assert.Equal(LegExit.Violated, FlowLegVerdicts.CodeOf(FlowLegVerdicts.Worst(FlowLegVerdicts.OfClass(sweep, FlowLegClass.Red))!.Value));
        Assert.Equal(HarnessExit.CommandFailed, FlowLegVerdicts.CodeOf(FlowLegVerdicts.Worst(FlowLegVerdicts.OfClass(build, FlowLegClass.Red))!.Value));
    }

    /// <summary>The worst incomplete leg is the most fundamental by the table's order: a leg whose inputs moved outranks one not admitted, and its code is what the step ends with.</summary>
    [Fact]
    public void TheWorstIncompleteLeg_IsTheMostFundamental()
    {
        var legs = new List<FlowLeg> { new("big", LegVerdict.NotAdmitted), new("here", LegVerdict.InputsMoved), new("far", LegVerdict.Unmeasured) };
        var incomplete = FlowLegVerdicts.OfClass(legs, FlowLegClass.Incomplete);

        Assert.Equal(LegVerdict.Unmeasured, FlowLegVerdicts.Worst(incomplete));
        Assert.Equal(LegExit.InputsMoved, FlowLegVerdicts.CodeOf(FlowLegVerdicts.Worst(incomplete)!.Value));
    }

    /// <summary>A leg that was never asked for and one that passed decide nothing: no class of the step's ending holds them.</summary>
    [Fact]
    public void ASelectionLeftOutOrPassed_DecidesNothing()
    {
        var legs = new List<FlowLeg> { new("left-out", LegVerdict.SkippedNotSelected), new("green", LegVerdict.Passed) };

        Assert.Equal([], FlowLegVerdicts.OfClass(legs, FlowLegClass.Red));
        Assert.Equal([], FlowLegVerdicts.OfClass(legs, FlowLegClass.FailureThatIsNotRed));
        Assert.Equal([], FlowLegVerdicts.OfClass(legs, FlowLegClass.Incomplete));
        Assert.Equal([.. legs], FlowLegVerdicts.OfClass(legs, FlowLegClass.None));
    }

    /// <summary>
    /// A verdict is incomplete exactly where the retry table runs its ending again, its code read with the cause the
    /// verdict itself says - a lock another run holds for refused-locked - so the class and the retry never disagree.
    /// </summary>
    [Fact]
    public void AVerdictIsIncomplete_ExactlyWhereTheRetryTableRunsItAgain()
    {
        Assert.Equal(OutcomeCause.LockHeld, FlowLegVerdicts.CauseOf(LegVerdict.RefusedLocked));

        foreach (var verdict in Enum.GetValues<LegVerdict>().Where(verdict => FlowLegVerdicts.ClassOf(verdict) != FlowLegClass.Red))
        {
            Assert.Equal(
                FlowRetry.RunsAgain(FlowLegVerdicts.CodeOf(verdict), FlowLegVerdicts.CauseOf(verdict)),
                FlowLegVerdicts.ClassOf(verdict) == FlowLegClass.Incomplete);
        }
    }

    /// <summary>A failure that is not red keeps its own code: unwitnessed 5, log-held 6, poisoned 70.</summary>
    [Fact]
    public void AFailureThatIsNotRed_KeepsItsOwnCode()
    {
        Assert.Equal(LegExit.Unwitnessed, FlowLegVerdicts.CodeOf(LegVerdict.Unwitnessed));
        Assert.Equal(LegExit.LogHeld, FlowLegVerdicts.CodeOf(LegVerdict.LogHeld));
        Assert.Equal(HarnessExit.InternalError, FlowLegVerdicts.CodeOf(LegVerdict.Poisoned));
    }
}
