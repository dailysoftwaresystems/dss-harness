using System.ComponentModel;
using DssHarness.Core.Orchestration.Flow;

// The guard's subjects must live where the walk looks for sections - the configuration's own namespace - and the
// test that holds them to their rule sits with them.
namespace DssHarness.Core.Configuration;

/// <summary>
/// A section that holds its own kind again - a step holding steps - lists the key once, saying what it takes,
/// instead of walking into itself without end.
/// </summary>
public sealed class ConfigKeysGuardTests
{
    /// <summary>A step's own <c>steps</c> key prints that it takes a step's keys, and the walk ends there.</summary>
    [Fact]
    public void ASectionHoldingItsOwnKind_SaysWhatItsKeyTakes_AndTheWalkEndsThere()
    {
        var keys = ConfigKeys.Of<Step>();

        var steps = Assert.Single(keys, key => key.Name == "steps");

        Assert.Equal("takes a step's keys", steps.Meaning);
        Assert.Empty(steps.Keys);
        Assert.Equal(["name", "steps"], keys.Select(key => key.Name));
    }

    /// <summary>A section that holds another that holds itself: the cut is where the kind comes back.</summary>
    [Fact]
    public void AKindThatComesBackThroughAnother_IsCutWhereItComesBack()
    {
        var keys = ConfigKeys.Of<Branch>();

        var held = Assert.Single(keys, key => key.Name == "steps");

        // The branch's steps take a step's keys, and among them the step's own steps key is the cut.
        var cut = Assert.Single(held.Keys, key => key.Name == "steps");

        Assert.Equal("takes a step's keys", cut.Meaning);
        Assert.Empty(cut.Keys);
    }

    /// <summary>
    /// The workflow's key tables are written by hand, beside their reader, and where a section holds its own kind again -
    /// a group's steps, the recovery's steps - they say it in the guard's own words: a workflow's step is a step, its
    /// model's prefix naming the file it is read from, not the thing it is.
    /// </summary>
    [Fact]
    public void TheWorkflowsKeyTables_SayWhatAStepsKeyTakes_InTheGuardsWords()
    {
        Assert.Equal("takes a step's keys", ConfigKeys.TakesKeysOf(typeof(WorkflowStep)));

        Assert.Equal(
            "the group's own steps; each takes a step's keys",
            Assert.Single(WorkflowStepTypes.Group, key => key.Name == "steps").Meaning);

        Assert.Equal(
            "the recovery's own steps; each takes a step's keys, reads the step that failed as source, and sees every step of the flow",
            Assert.Single(WorkflowKeys.Recover, key => key.Name == "steps").Meaning);
    }
}

/// <summary>A step's shape, holding steps of its own: the walk's own subject.</summary>
[Description("one step of an action file")]
public sealed record Step(string Name, IReadOnlyList<Step> Steps);

/// <summary>A section one level above the steps, holding them.</summary>
[Description("a branch of steps")]
public sealed record Branch(string Title, IReadOnlyList<Step> Steps);
