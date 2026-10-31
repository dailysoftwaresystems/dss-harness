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
            claudeDirectories: []);

        Assert.Equal("C:/trees/w1", ground.WorkingDirectory);
        Assert.Equal(["C:/trees/w1", "C:/runs/r1/agents/a1/work"], ground.ReadRoots);
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
            claudeDirectories: []);

        Assert.Equal(["C:/runs/r1/agents/a1/work", "C:/source/repo"], ground.ReadRoots);
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
            claudeDirectories: []);

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
        var without = AgentStepGround.Of(new HarnessConfig(), Definition(), Step(), Workspace(), webSearchGiven: false, claudeDirectories: []);
        var with = AgentStepGround.Of(new HarnessConfig(), Definition(), Step(), Workspace(), webSearchGiven: true, claudeDirectories: []);

        Assert.Equal([AgentCapability.Read, AgentCapability.Write, AgentCapability.Run], without.Capabilities);
        Assert.Equal([AgentCapability.Read, AgentCapability.Write, AgentCapability.Run, AgentCapability.Web], with.Capabilities);
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
            claudeDirectories: []);

        Assert.Equal(["src/**"], narrowed.Writes);

        var open = AgentStepGround.Of(new HarnessConfig(), Definition(), Step(), Workspace(), webSearchGiven: false, claudeDirectories: []);

        Assert.Null(open.Writes);
    }

    /// <summary>
    /// An agent whose definition says <c>writes: false</c>, and a run that only reads - the run's advisor - work in the
    /// worktree and never write it: the work directory is all either writes, and the worktree is still read and worked
    /// in.
    /// </summary>
    [Fact]
    public void ADefinitionThatWritesNothing_AndARunThatOnlyReads_LeaveTheWorktreeUnwritten()
    {
        var readOnly = new AgentDefinition("planner", "what it is for", "the system prompt", Writes: false);
        var byDefinition = AgentStepGround.Of(new HarnessConfig(), readOnly, Step(), Workspace(), webSearchGiven: false, claudeDirectories: []);
        var byRun = AgentStepGround.Of(new HarnessConfig(), Definition(), Step(), Workspace(), webSearchGiven: false, claudeDirectories: [], readsOnly: true);

        foreach (var ground in new[] { byDefinition, byRun })
        {
            Assert.Equal(["C:/runs/r1/agents/a1/work"], ground.WritableRoots);
            Assert.Equal("C:/trees/w1", ground.WorkingDirectory);
            Assert.Equal(["C:/trees/w1", "C:/runs/r1/agents/a1/work"], ground.ReadRoots);
        }
    }

    /// <summary>
    /// Every credential slot of the main checkout is guarded on a step's ground - each of the layout's slots, under the
    /// main checkout, never one alone - and on a side-task's, by the same list.
    /// </summary>
    [Fact]
    public void EveryCredentialSlotOfTheMainCheckout_IsGuarded_OnAStepsGroundAndASideTasks()
    {
        var ground = AgentStepGround.Of(new HarnessConfig(), Definition(), Step(), Workspace(), webSearchGiven: false, claudeDirectories: []);
        var side = DssHarness.Core.Agents.SideTasks.SideTaskPolicy.Ground(new DssHarness.Core.Repository.HarnessLayout("C:/source/repo", "C:/source/repo"), new HarnessConfig(), []);

        string[] every =
        [
            Path.Combine("C:/source/repo", ".harness-config", "sshItems"),
            Path.Combine("C:/source/repo", ".harness-config", "wslDistros"),
            Path.Combine("C:/source/repo", ".harness-config", "runner", ".env"),
            Path.Combine("C:/source/repo", ".harness-config", "runner", ".secrets"),
            Path.Combine("C:/source/repo", ".harness-config", "workflows", ".env"),
        ];

        Assert.Equal(every, ground.SlotDirectories);
        Assert.Equal(every, side.SlotDirectories);
    }

    /// <summary>
    /// The tool's own state a step's ground guards is each state path under the tree it works in - the configured
    /// worktrees root among them - never that tree itself, which its agent writes.
    /// </summary>
    [Fact]
    public void TheStateGuarded_IsEachStatePathUnderTheTree_NeverTheTreeItself()
    {
        var ground = AgentStepGround.Of(new HarnessConfig(), Definition(), Step(), Workspace(), webSearchGiven: false, claudeDirectories: []);

        Assert.Equal(
            [.. DssHarness.Core.Repository.HarnessLayout.StatePaths(WorktreeSettings.DefaultRoot).Select(path => Path.Combine("C:/trees/w1", path))],
            ground.StateDirectories);
        Assert.DoesNotContain("C:/trees/w1", ground.StateDirectories);
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
