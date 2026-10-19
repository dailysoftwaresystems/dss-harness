using DssHarness.Core.Anchors;
using DssHarness.Core.Configuration;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Orchestration.Flow;

namespace DssHarness.Tests;

/// <summary>
/// The table of step types: each type mapped to the service it calls, what a step of it holds while it runs, and
/// the limit it counts against - the one table the engine's switch reads, so that no other code says what a type
/// does.
/// </summary>
public sealed class WorkflowStepTypesTests
{
    /// <summary>Every type maps to the service the plan's table names it, one for one, in the order the help lists them.</summary>
    [Fact]
    public void EveryTypeMapsToTheServiceItCalls()
    {
        var calls = WorkflowStepTypes.All.Select(type => (type.Type, type.Calls)).ToList();

        Assert.Equal(
            [
                ("orient", "IGitClient (ResolveCommitAsync, GetStatusAsync), OrchestrationStore.Agents, LegSelection.Resolve"),
                ("reason", "IAgentClient; a read-only one that begins work first makes its agent (IAgentService.CreateAsync, no worktree)"),
                ("build", "IBuildLegs over LegRunService"),
                ("test", "ITestLegs over LegRunService"),
                ("run", "IRunnerLegs over LegRunService"),
                ("check-mutations", "IMutationLegs through MutationService, over LegRunService"),
                ("create-agent", "IAgentService.CreateAsync"),
                ("fold", "IAgentService.FoldAsync"),
                ("delete-agent", "IAgentService.DeleteAsync"),
                ("refresh", "IAgentService.RefreshAsync"),
                ("rebase", "IAgentService.RebaseAsync"),
                ("validate-plan", "PlanValidator with IPathBudget.Check and CopyPathBudget.Check"),
                ("anchor-balance", "IAnchorBalanceService.CheckAsync"),
                ("anchor-citations", "IAnchorCitationService.CheckAsync"),
                ("ci-read", "ICiLegsService.CheckAsync"),
                ("ci-wait", "ICiChecksService"),
                ("commit", "IGitPublisher"),
                ("push", "IGitPublisher"),
                ("pull-request", "IGitPublisher"),
                ("pause", "the engine itself"),
            ],
            calls);
    }

    /// <summary>What a step of each kind holds while it runs: the claims of the plan's table, one for one.</summary>
    [Fact]
    public void WhatAStepOfEachKindHolds_IsTheTablesWithClaims()
    {
        var claims = WorkflowStepTypes.All.Select(type => (type.Type, string.Join(", ", type.ClaimsOf))).ToList();

        Assert.Equal(
            [
                ("orient", "read main"),
                ("reason", "read main, write the agent's tree where its access is write"),
                ("build", "run(tree)"),
                ("test", "run(tree)"),
                ("run", "run(tree), write(tree) where its runner says writesTree"),
                ("check-mutations", "run(tree)"),
                ("create-agent", "write main"),
                ("fold", "write main, write the agent's tree"),
                ("delete-agent", "write main, write the agent's tree"),
                ("refresh", "write main, write its target"),
                ("rebase", "write its target"),
                ("validate-plan", "read main"),
                ("anchor-balance", "read main"),
                ("anchor-citations", "read main"),
                ("ci-read", ""),
                ("ci-wait", ""),
                ("commit", "write main"),
                ("push", "the remote resource"),
                ("pull-request", "the remote resource"),
                ("pause", ""),
            ],
            claims);
    }

    /// <summary>
    /// The limits: reasoning counts against the reasoning agents, the four leg runners against the parallel
    /// processes, an agent's making takes a slot and its deletion gives the slot back, and every other kind counts
    /// against nothing.
    /// </summary>
    [Fact]
    public void WhatEachKindCountsAgainst()
    {
        var against = WorkflowStepTypes.All.Select(type => (type.Type, type.CountsAgainst)).ToList();

        Assert.Equal(
            [
                ("orient", null),
                ("reason", "maxReasoningAgents"),
                ("build", "maxParallelProcesses"),
                ("test", "maxParallelProcesses"),
                ("run", "maxParallelProcesses"),
                ("check-mutations", "maxParallelProcesses"),
                ("create-agent", "an open-agent slot"),
                ("fold", null),
                ("delete-agent", "releases the open-agent slot"),
                ("refresh", null),
                ("rebase", null),
                ("validate-plan", null),
                ("anchor-balance", null),
                ("anchor-citations", null),
                ("ci-read", null),
                ("ci-wait", null),
                ("commit", null),
                ("push", null),
                ("pull-request", null),
                ("pause", null),
            ],
            against);
    }

    /// <summary>
    /// A fold family request's refusal names its options in a step's words where a workflow wrote it: each option the
    /// command line takes, spelt as a key, is the key the step's table lists for it - every one of a fold's allow, and
    /// those a refresh and a rebase take - so one sentence of a rule reads right on either side.
    /// </summary>
    [Fact]
    public void TheFoldFamilysOptions_SpeltAsKeys_AreTheKeysItsStepsTake()
    {
        static IReadOnlyList<string> Keys(params string[] options) => [.. options.Select(option => OptionSpelling.Key.Spell(option)).Order(StringComparer.Ordinal)];

        Assert.Equal(
            [.. KeyDescription.Names(WorkflowStepTypes.AllowKeys).Order(StringComparer.Ordinal)],
            Keys(FoldAllowances.MergeOption, FoldAllowances.SettledOption, AnchorBatchRequest.NewOption, AnchorBatchRequest.AcceptLostOption, AnchorBatchRequest.AcceptOverOption));

        Assert.Equal(["all", "except", "merge", "stale"], Keys(RefreshRequest.AllOption, RefreshRequest.ExceptOption, RefreshRequest.StaleOption, FoldAllowances.MergeOption));
        Assert.Subset(WorkflowStepTypes.KeysOf(WorkflowStepTypes.Find("refresh")!).ToHashSet(StringComparer.Ordinal), Keys(RefreshRequest.AllOption, RefreshRequest.ExceptOption, RefreshRequest.StaleOption, FoldAllowances.MergeOption).ToHashSet(StringComparer.Ordinal));

        Assert.Equal(["merge", "settled"], Keys(FoldAllowances.MergeOption, FoldAllowances.SettledOption));
        Assert.Subset(WorkflowStepTypes.KeysOf(WorkflowStepTypes.Find("rebase")!).ToHashSet(StringComparer.Ordinal), Keys(FoldAllowances.MergeOption, FoldAllowances.SettledOption).ToHashSet(StringComparer.Ordinal));

        // On the command line, an option is its flag.
        Assert.Equal("--accept-lost", OptionSpelling.Flag.Spell(AnchorBatchRequest.AcceptLostOption));
    }
}
