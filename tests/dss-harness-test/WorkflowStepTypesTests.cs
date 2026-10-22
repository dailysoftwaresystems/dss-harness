using System.Text.Json.Nodes;
using DssHarness.Core.Anchors;
using DssHarness.Core.Configuration;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Orchestration.Flow;
using DssHarness.Core.Schemas;

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
                ("orient", "OrientRunner: FlowCheckout over IGitClient and GitRemotes, LegSelection.Resolve, TestInvocationResolver, AnchorCitationService.RootsOf, CiSettings.Unset, MutationService.Declares, OrchestrationRules.OpenAgents, TreeHolders"),
                ("reason", "IAgentClient; a read-only one that begins work first makes its agent (IAgentService.CreateAsync, no worktree)"),
                ("build", "BuildStepRunner: IBuildLegs over LegRunService"),
                ("test", "TestStepRunner: ITestLegs over LegRunService"),
                ("run", "RunStepRunner: IRunnerLegs over LegRunService, HarnessConfig.WritingRunners"),
                ("check-mutations", "SweepStepRunner: IMutationLegs through MutationService, over LegRunService"),
                ("create-agent", "CreateAgentRunner: IAgentService.CreateAsync, SeedAsync and DeleteAsync"),
                ("fold", "FoldStepRunner: IAgentService.FoldAsync held to its dry run, FoldLedger, FoldWithin"),
                ("delete-agent", "DeleteAgentStepRunner: IAgentService.DeleteAsync held to its dry run, FoldLedger, FoldWithin"),
                ("refresh", "RefreshStepRunner: IAgentService.RefreshAsync"),
                ("rebase", "RebaseStepRunner: IAgentService.RebaseAsync"),
                ("validate-plan", "PlanCheckRunner: PlanValidator with FlowRunPaths over IPathBudget.Check and CopyPathBudget"),
                ("anchor-balance", "AnchorBalanceStepRunner: IAnchorBalanceService.CheckAsync, AnchorBalanceReport.HeldTo, AnchorReports.BalanceFindings"),
                ("anchor-citations", "AnchorCitationsStepRunner: IAnchorCitationService.CheckAsync, AnchorCitationReport.HeldTo, AnchorCitationReports.Line"),
                ("ci-read", "CiReadStepRunner: ICiLegsService.CheckAsync over GhCiJobSource and GhProcess"),
                ("ci-wait", "CiWaitStepRunner: ICiChecksService over GhCiJobSource and GhProcess, ICiLegsService.ClassifyRunAsync"),
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

    /// <summary>
    /// Every type the plan lists an output for has a built-in schema the subset reads, holding the members the plan
    /// names and no other, so a path into a step of it is typed when the definition is loaded; a reason step's output is
    /// the schema file it names, and a type the plan lists none for - a create-agent, a refresh, a rebase - has none.
    /// </summary>
    [Fact]
    public void EveryBuiltInOutput_IsASchemaTheSubsetReads_HoldingTheMembersThePlanNames()
    {
        var outputs = WorkflowStepTypes.All
            .Select(type => (type.Type, Members: type.Output is { } output
                ? string.Join(", ", ((JsonObject)JsonNode.Parse(output)!["properties"]!).Select(member => member.Key))
                : null))
            .ToList();

        Assert.Equal(
            [
                ("orient", "branch, baseBranch, startCommit, hasRemote, hasLegs, legCount, anchors, ciConfigured, hasMutations, tests, dirtyPaths, openAgents, inFlight"),
                ("reason", null),
                ("build", "passed, exitCode, verdicts, red, runDirectory"),
                ("test", "passed, exitCode, verdicts, red, runDirectory"),
                ("run", "passed, exitCode, verdicts, red, runDirectory"),
                ("check-mutations", "passed, exitCode, verdicts, red, runDirectory"),
                ("create-agent", null),
                ("fold", "written, deleted, registries, rows"),
                ("delete-agent", "written, deleted, registries, rows"),
                ("refresh", null),
                ("rebase", null),
                ("validate-plan", "ok, problems, items"),
                ("anchor-balance", "passed, findings"),
                ("anchor-citations", "passed, findings"),
                ("ci-read", "passed, branch, matrixRan, legs, red"),
                ("ci-wait", "passed, commit, checks, red, pending, unsettled"),
                ("commit", "committed, sha, paths"),
                ("push", "pushed, remote, branch, sha"),
                ("pull-request", "number, url, created"),
                ("pause", "answer, text"),
            ],
            outputs);

        foreach (var type in WorkflowStepTypes.All.Where(type => type.Output is not null))
        {
            Assert.Empty(JsonSchemaSubset.SubsetProblems(JsonNode.Parse(type.Output!)!));
        }

        // A sweep's red legs each name the arm - none where the leg failed outside every arm - beside what every leg
        // step's say; a red leg's phase is none where no phase of it failed, and a run that placed no leg has no directory.
        var sweep = JsonSchemaSubset.Read(JsonNode.Parse(WorkflowStepTypes.Find("check-mutations")!.Output!)!);
        var test = JsonSchemaSubset.Read(JsonNode.Parse(WorkflowStepTypes.Find("test")!.Output!)!);
        var arm = sweep.Member("red")!.Items!;
        var leg = test.Member("red")!.Items!;

        Assert.Equal(["string", "null"], arm.Member("arm")!.Kinds);
        Assert.Null(leg.Member("arm"));
        Assert.Equal(["string", "null"], leg.Member("phase")!.Kinds);
        Assert.Equal(["string", "null"], test.Member("runDirectory")!.Kinds);
    }

    /// <summary>
    /// The keys of a step that hold an expression, and those that take a list or an expression yielding one, are each
    /// one list - read by the grammar, never filled as a template - and every one is a key a step takes.
    /// </summary>
    [Fact]
    public void TheKeysHoldingAnExpression_AreOneListEach_AndEachIsAStepsKey()
    {
        Assert.Equal(
            ["when", "filterWhen", "forEach", "repeat.until", "repeat.blocking", "repeat.leftover", "items", "scope", "questions", "preAnswer.when", "preAnswer.quote.text"],
            WorkflowStepTypes.ExpressionKeys);
        Assert.Equal(
            ["writes", "within", "paths", "except", "stale", "merge", "rows", "allow.new", "allow.merge"],
            WorkflowStepTypes.ListOrExpressionKeys);

        var keys = new HashSet<string>(
            [
                .. KeyDescription.Names(WorkflowStepTypes.Common),
                .. KeyDescription.Names(WorkflowStepTypes.Group),
                .. WorkflowStepTypes.AllKeys,
                .. KeyDescription.Names(WorkflowStepTypes.RepeatKeys).Select(key => $"repeat.{key}"),
                .. KeyDescription.Names(WorkflowStepTypes.PreAnswerKeys).Select(key => $"preAnswer.{key}"),
                .. KeyDescription.Names(WorkflowStepTypes.QuoteKeys).Select(key => $"preAnswer.quote.{key}"),
                .. KeyDescription.Names(WorkflowStepTypes.AllowKeys).Select(key => $"allow.{key}"),
            ],
            StringComparer.Ordinal);

        Assert.All(WorkflowStepTypes.ExpressionKeys.Concat(WorkflowStepTypes.ListOrExpressionKeys), key => Assert.Contains(key, keys));
    }

    /// <summary>
    /// A pause step alone only asks: every other type takes the keys of every step that runs - a time limit among them -
    /// and the flattened list a step's unknown keys are refused against names each key once.
    /// </summary>
    [Fact]
    public void APauseStepAloneOnlyAsks_AndEveryOtherTypeTakesTheKeysOfAStepThatRuns()
    {
        Assert.Equal(["pause"], WorkflowStepTypes.All.Where(type => type.Asks).Select(type => type.Type));
        Assert.Equal(["timeoutMinutes"], KeyDescription.Names(WorkflowStepTypes.Running));
        Assert.All(
            WorkflowStepTypes.All,
            type => Assert.Equal(!type.Asks, WorkflowStepTypes.KeysOf(type).Contains("timeoutMinutes", StringComparer.Ordinal)));
        Assert.Equal(WorkflowStepTypes.AllKeys.Count, WorkflowStepTypes.AllKeys.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>
    /// The keys <c>help workflows</c> lists for a step are every key the reader takes on one: the common keys, a group's,
    /// those of every step that runs, and each type's own under its name - none read without being listed.
    /// </summary>
    [Fact]
    public void TheStepKeysHelpLists_AreEveryKeyTheReaderTakesOnAStep()
    {
        var listed = KeyDescription.Names(WorkflowKeys.Step).ToHashSet(StringComparer.Ordinal);
        var typed = WorkflowStepTypes.All.SelectMany(type => WorkflowStepTypes.KeysOf(type).Select(key => (Type: type.Type, Key: key)));

        Assert.All(KeyDescription.Names(WorkflowStepTypes.Common), key => Assert.Contains(key, listed));
        Assert.All(KeyDescription.Names(WorkflowStepTypes.Group), key => Assert.Contains(key, listed));
        Assert.Contains("timeoutMinutes", listed);
        Assert.All(
            typed.Where(pair => !KeyDescription.Names(WorkflowStepTypes.Running).Contains(pair.Key, StringComparer.Ordinal)),
            pair => Assert.Contains($"{pair.Type}.{pair.Key}", listed));
    }
}
