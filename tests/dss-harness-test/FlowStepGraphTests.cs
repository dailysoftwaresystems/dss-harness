using System.Text;
using DssHarness.Core.Agents.Policy;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Orchestration.Flow;

namespace DssHarness.Tests;

/// <summary>What a step's graph is keyed and served by (<see cref="FlowStepGraph"/>).</summary>
public sealed class FlowStepGraphTests
{
    /// <summary>
    /// A step's read rules are named by the whole digest of them, sorted and once each - so two sets of rules whose digests
    /// begin alike are two keys, and no step reads a map or a page kept under another's reads - and a step none of whose
    /// rules is a read rule is named by none. Red with the digest cut to its first sixteen characters ([..16]).
    /// </summary>
    [Fact]
    public void FlowStepGraph_KeysReadRulesByTheFullDigest()
    {
        var key = FlowStepGraph.ReadsOf(Ground("Read(secrets/**)", "Read(b/**)", "Read(secrets/**)", "Run(npm publish *)"));

        Assert.Equal(FileContentHash.Of(Encoding.UTF8.GetBytes("Read(b/**)\nRead(secrets/**)")), key);
        Assert.Equal(64, key!.Length);
        Assert.Null(FlowStepGraph.ReadsOf(Ground("Run(npm publish *)")));
    }

    /// <summary>
    /// Why a launch was given no pack is said by what the step was offered: a pack that held nothing is said so first - a
    /// step at tools that pushes none is then offered no pack to ask, and its line never says it asks the tool for pages
    /// there are none of - a pack that holds pages and is not pushed is asked of the tool, a step that made none asked for
    /// none, and a pushed pack that holds pages is given. Red with "pushed none" decided before "held nothing".
    /// </summary>
    [Fact]
    public void WhyALaunchWasGivenNoPack_SaysAPackThatHeldNothingFirst()
    {
        Assert.Equal(
            [FlowStepGraph.NoPackHeld, FlowStepGraph.NoPackHeld, FlowStepGraph.NoPackPushed, FlowStepGraph.NoPackAsked, FlowStepGraph.NoPackAsked, null],
            [
                FlowStepGraph.NoPackOf(tokens: 3000, pushed: 0, held: false),
                FlowStepGraph.NoPackOf(tokens: 3000, pushed: 3000, held: false),
                FlowStepGraph.NoPackOf(tokens: 3000, pushed: 0, held: true),
                FlowStepGraph.NoPackOf(tokens: 0, pushed: 0, held: false),
                FlowStepGraph.NoPackOf(tokens: 0, pushed: 0, held: true),
                FlowStepGraph.NoPackOf(tokens: 3000, pushed: 3000, held: true),
            ]);
    }

    /// <summary>A ground whose restrictions are <paramref name="rules"/>, each the configuration's.</summary>
    private static AgentPolicyGround Ground(params string[] rules)
    {
        var root = Path.GetFullPath(".");

        return new AgentPolicyGround(root, [root], [root], [], [], [], [.. rules.Select(rule => new AgentRestriction(rule, AgentPolicySource.Config))], AgentPolicyGround.EveryCapability, null, null, ReporterRoot: null, WorktreeRules: true, ProtectedPaths: true, []);
    }
}
