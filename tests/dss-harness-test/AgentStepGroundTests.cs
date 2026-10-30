using DssHarness.Core.Agents;
using DssHarness.Core.Agents.Policy;
using DssHarness.Core.Configuration;
using DssHarness.Core.Orchestration.Flow;

namespace DssHarness.Tests;

/// <summary>
/// The policy's ground of one reason step (<see cref="AgentStepGround"/>): where it works and what it may write - a
/// worktree agent writes its worktree, a read-only one its work directory alone and reads the main tree - and every
/// restriction that applies to it, from the configuration, its definition and its step together.
/// </summary>
public sealed class AgentStepGroundTests
{
    [Fact]
    public void AnAgentWithAWorktreeWritesIt_AndItsWorkDirectory_TheMainTreeUnread()
    {
        var ground = AgentStepGround.Of(
            new HarnessConfig(),
            Definition(),
            Step(),
            new AgentWorkspace("C:/trees/w1", "C:/runs/r1/agents/a1/work", "C:/source/repo", [], "C:/trees/w1", "C:/source/repo/.orchestrators/r1"),
            webSearchGiven: false,
            claudeDirectories: [],
            slotDirectory: "C:/source/repo/.harness-config/workflows/.env");

        Assert.Equal("C:/trees/w1", ground.WorkingDirectory);
        Assert.Contains("C:/trees/w1", ground.ReadRoots);
        Assert.DoesNotContain("C:/source/repo", ground.ReadRoots);
        Assert.Equal(["C:/trees/w1", "C:/runs/r1/agents/a1/work"], ground.WritableRoots);
    }

    [Fact]
    public void AReadOnlyAgentReadsTheMainTree_AndWritesItsWorkDirectoryAlone()
    {
        var ground = AgentStepGround.Of(
            new HarnessConfig(),
            Definition(),
            Step(),
            new AgentWorkspace(null, "C:/runs/r1/agents/a1/work", "C:/source/repo", [], "C:/runs/r1/agents/a1/work", "C:/source/repo/.orchestrators/r1"),
            webSearchGiven: false,
            claudeDirectories: [],
            slotDirectory: "C:/source/repo/.harness-config/workflows/.env");

        Assert.Contains("C:/source/repo", ground.ReadRoots);
        Assert.Equal(["C:/runs/r1/agents/a1/work"], ground.WritableRoots);
    }

    [Fact]
    public void EveryRestrictionAppliesTogether_FromTheConfigurationTheDefinitionAndTheStep()
    {
        var ground = AgentStepGround.Of(
            new HarnessConfig { AgentPolicy = new AgentPolicyConfig { Restrictions = ["Run(git push *)"] } },
            Definition(restrictions: ["Write(.github/**)"]),
            Step(restrictions: ["Read(secrets/**)"]),
            Workspace(),
            webSearchGiven: false,
            claudeDirectories: [],
            slotDirectory: "slot");

        Assert.Equal(
            [
                ("Run(git push *)", AgentPolicySource.Config),
                ("Write(.github/**)", AgentPolicySource.Agent),
                ("Read(secrets/**)", AgentPolicySource.Agent),
            ],
            ground.Restrictions.Select(one => (one.Text, one.Source)));
    }

    [Fact]
    public void TheWebCapabilityIsKeptOnlyWhereTheRunIsGivenASearch()
    {
        var without = AgentStepGround.Of(new HarnessConfig(), Definition(), Step(), Workspace(), webSearchGiven: false, claudeDirectories: [], slotDirectory: "slot");
        var with = AgentStepGround.Of(new HarnessConfig(), Definition(), Step(), Workspace(), webSearchGiven: true, claudeDirectories: [], slotDirectory: "slot");

        Assert.DoesNotContain(AgentCapability.Web, without.Capabilities);
        Assert.Contains(AgentCapability.Web, with.Capabilities);
    }

    [Fact]
    public void TheStepsWritesNarrowItsWritableRoots_WhereItNamesThem()
    {
        var narrowed = AgentStepGround.Of(
            new HarnessConfig(),
            Definition(),
            Step(writes: new FlowList(["src/**"], Expression: null)),
            Workspace(),
            webSearchGiven: false,
            claudeDirectories: [],
            slotDirectory: "slot");

        Assert.Equal(["src/**"], narrowed.Writes);

        var open = AgentStepGround.Of(new HarnessConfig(), Definition(), Step(), Workspace(), webSearchGiven: false, claudeDirectories: [], slotDirectory: "slot");

        Assert.Null(open.Writes);
    }

    /// <summary>A workspace over a worktree, the shape most of the tests use.</summary>
    private static AgentWorkspace Workspace()
        => new("C:/trees/w1", "C:/runs/r1/agents/a1/work", "C:/source/repo", [], "C:/trees/w1", "C:/source/repo/.orchestrators/r1");

    /// <summary>An agent definition, with what a test changes of it.</summary>
    private static AgentDefinition Definition(IReadOnlyList<string>? restrictions = null)
        => new("planner", "what it is for", "the system prompt", Restrictions: restrictions);

    /// <summary>A reason step, with what a test changes of it.</summary>
    private static WorkflowStep Step(IReadOnlyList<string>? restrictions = null, FlowList? writes = null)
        => new()
        {
            Name = "plan",
            Type = "reason",
            Prompt = "prompts/plan.md",
            Restrictions = restrictions ?? [],
            Writes = writes,
        };
}
