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

    /// <summary>A ground whose restrictions are <paramref name="rules"/>, each the configuration's.</summary>
    private static AgentPolicyGround Ground(params string[] rules)
    {
        var root = Path.GetFullPath(".");

        return new AgentPolicyGround(root, [root], [root], [], [], [], [.. rules.Select(rule => new AgentRestriction(rule, AgentPolicySource.Config))], AgentPolicyGround.EveryCapability, null, null, ReporterRoot: null, WorktreeRules: true, ProtectedPaths: true, []);
    }
}
