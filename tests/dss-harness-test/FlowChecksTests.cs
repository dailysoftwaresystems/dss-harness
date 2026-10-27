using System.Text.Json.Nodes;
using DssHarness.Core.Orchestration.Flow;
using DssHarness.Core.Results;

namespace DssHarness.Tests;

/// <summary>
/// The texts of what a run checked (part 04, "What ran, in words"): which steps are checks - by their type, or an
/// independent reason step, never by name - every check that ended with where it ran and its verdict, each leg's for a
/// step that runs legs; what did not check, with why, and the checks not run yet; and each red result not seen green
/// since - none where there is none.
/// </summary>
public sealed class FlowChecksTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// A check is a step of a type the Checks column marks - a validate-plan among them, which never fails - or a reason
    /// step marked independent, whatever either is named; a fold, a graph's sync and a reason step that goes on are not.
    /// </summary>
    [Fact]
    public void AChecksStep_IsOneOfACheckingTypeOrAnIndependentReasonStep_WhateverItsName()
    {
        Assert.Equal(
            ["build", "test", "run", "check-mutations", "validate-plan", "anchor-balance", "anchor-citations", "ci-read", "ci-wait"],
            WorkflowStepTypes.All.Where(type => type.Checks).Select(type => type.Type));
        Assert.True(FlowChecks.IsCheck(new WorkflowStep { Name = "anything", Type = "run" }));
        Assert.True(FlowChecks.IsCheck(new WorkflowStep { Name = "plan", Type = "validate-plan" }));
        Assert.True(FlowChecks.IsCheck(new WorkflowStep { Name = "look", Type = "reason", Independent = true }));
        Assert.False(FlowChecks.IsCheck(new WorkflowStep { Name = "review", Type = "reason" }));
        Assert.False(FlowChecks.IsCheck(new WorkflowStep { Name = "gate", Type = "fold" }));
        Assert.False(FlowChecks.IsCheck(new WorkflowStep { Name = "graph", Type = "graphify-codebase" }));
        Assert.False(FlowChecks.IsCheck(new WorkflowStep { Name = "group" }));
        Assert.Equal(FlowChecks.Independent, FlowChecks.KindOf(new WorkflowStep { Name = "look", Type = "reason", Independent = true }));

        // A check that holds nothing where it runs is one CI runs, as what ran says of it.
        Assert.Equal(["ci-read", "ci-wait"], WorkflowStepTypes.All.Where(type => type.Checks && type.ClaimsOf.Count == 0).Select(type => type.Type));
        Assert.Equal("test", FlowChecks.KindOf(new WorkflowStep { Name = "gate", Type = "test" }));
    }

    /// <summary>
    /// What ran is every check that ended, in the order they ended, a line each: where it ran - the main tree, an agent's
    /// worktree, CI - and its verdict, each leg's for a step that runs legs, a failure with its code and why, and what
    /// became of it; a check skipped, never reached or not ended yet is not among them.
    /// </summary>
    [Fact]
    public void WhatRan_IsEveryCheckThatEnded_AVerdictALine()
    {
        FlowCheck[] checks =
        [
            Check("gate", "test", FlowInstanceState.Done, Legs(passed: false, ("win", "passed"), ("linux", "failed")), at: 2),
            Check("plan", "validate-plan", FlowInstanceState.Done, Output("""{"ok":true,"problems":[],"items":[]}"""), at: 1),
            Check("review", FlowChecks.Independent, FlowInstanceState.Done, null, at: 3, worksIn: "a1"),
            Check("ci", "ci-wait", FlowInstanceState.Failed, null, at: 4, exit: 1, why: "1 check red", passedOver: true),
            Check("lint", "run", FlowInstanceState.Failed, null, at: 5, exit: 20, handledBy: "fix"),
            Check("skipped", "test", FlowInstanceState.Skipped, null, at: 6, why: "its when read false"),
            Check("later", "build", FlowInstanceState.NotStarted, null, at: null),
        ];

        Assert.Equal(
            string.Join("\n",
                "plan (validate-plan) in the main tree: passed",
                "gate (test) in the main tree: red - win passed, linux failed",
                "review (independent) in a1's worktree: done",
                "ci (ci-wait) on CI: failed (1): 1 check red; passed over",
                "lint (run) in the main tree: failed (20); handled by fix"),
            FlowChecks.Ran(checks));
        Assert.Equal(FlowChecks.None, FlowChecks.Ran([]));
    }

    /// <summary>
    /// What did not check is each check skipped or never reached, with why, then the checks not run yet by their steps'
    /// names; a check whose failure was passed over is among it only where the text says so - a commit's - and a check
    /// that ran, or is running, is not.
    /// </summary>
    [Fact]
    public void WhatDidNotCheck_IsEachSkippedOrNeverReached_AndWhatIsStillToCome()
    {
        FlowCheck[] checks =
        [
            Check("gate", "test", FlowInstanceState.Done, Legs(passed: true, ("win", "passed")), at: 1),
            Check("audit", FlowChecks.Independent, FlowInstanceState.Skipped, null, at: 2, why: "its when read false"),
            Check("ci", "ci-wait", FlowInstanceState.NotRun, null, at: 3, why: "the run stopped before it"),
            Check("lint", "run", FlowInstanceState.Failed, null, at: 4, exit: 20, why: "no runner", passedOver: true),
            Check("running", "build", FlowInstanceState.Running, null, at: null),
        ];

        Assert.Equal(
            string.Join("\n",
                "audit (independent) in the main tree: skipped - its when read false",
                "ci (ci-wait) on CI: not run - the run stopped before it",
                "mutations (check-mutations)"),
            FlowChecks.NotRun(checks, ["mutations (check-mutations)"], passedOver: false));
        Assert.Contains("lint (run) in the main tree: failed and passed over - no runner", FlowChecks.NotRun(checks, [], passedOver: true), StringComparison.Ordinal);
        Assert.Equal(FlowChecks.None, FlowChecks.NotRun([checks[0]], [], passedOver: true));
    }

    /// <summary>
    /// What is red is each red result a check ended with - each red leg with its phase, each red check of CI, an anchor
    /// check's findings and a plan's problems counted, a failure that says no more as its verdict - until a later visit of
    /// the same step of the same work item ends green; another work item's green, or a later red, clears nothing.
    /// </summary>
    [Fact]
    public void WhatIsRed_IsEachRedResult_NotSeenGreenSince()
    {
        FlowCheck[] checks =
        [
            Check("items[0].gate", "test", FlowInstanceState.Failed, Legs(passed: false, ("win", "failed")), at: 1, exit: 1, red: [("win", "failed", "test")]),
            Check("items[1].gate", "test", FlowInstanceState.Done, Legs(passed: true, ("win", "passed")), at: 2),
            Check("ci", "ci-wait", FlowInstanceState.Done, Output("""{"passed":false,"red":[{"name":"build (linux)","conclusion":"failure"}]}"""), at: 3),
            Check("anchors", "anchor-balance", FlowInstanceState.Done, Output("""{"passed":false,"findings":["one","two"]}"""), at: 4),
            Check("plan", "validate-plan", FlowInstanceState.Done, Output("""{"ok":false,"problems":["no item"],"items":[]}"""), at: 5),
            Check("lint", "run", FlowInstanceState.Failed, null, at: 6, exit: 20, why: "no runner"),
            Check("fix#1.check", "build", FlowInstanceState.Failed, null, at: 7, exit: 1),
            Check("fix#2.check", "build", FlowInstanceState.Done, Legs(passed: true, ("win", "passed")), at: 8),
            Check("audit", FlowChecks.Independent, FlowInstanceState.Failed, null, at: 9, exit: 20),
            Check("audit+2", FlowChecks.Independent, FlowInstanceState.Done, null, at: 10),
        ];

        Assert.Equal(
            string.Join("\n",
                "items[0].gate (test) in the main tree: win failed at test",
                "ci (ci-wait) on CI: build (linux) failure",
                "anchors (anchor-balance) in the main tree: 2 finding(s)",
                "plan (validate-plan) in the main tree: 1 problem(s)",
                "lint (run) in the main tree: failed (20): no runner"),
            FlowChecks.Red(checks));

        // A red seen after the green is red again.
        Assert.Contains("fix#3.check (build)", FlowChecks.Red([.. checks, Check("fix#3.check", "build", FlowInstanceState.Failed, null, at: 11, exit: 1)]), StringComparison.Ordinal);
        Assert.Equal(FlowChecks.None, FlowChecks.Red([checks[1]]));
    }

    private static FlowCheck Check(
        string instance,
        string kind,
        FlowInstanceState state,
        JsonNode? output,
        int? at,
        int? exit = null,
        string? why = null,
        bool passedOver = false,
        string? handledBy = null,
        string? worksIn = null,
        (string Leg, string Verdict, string Phase)[]? red = null)
    {
        if (red is not null && output is JsonObject legs)
        {
            legs["red"] = new JsonArray([.. red.Select(entry => (JsonNode)new JsonObject { ["leg"] = entry.Leg, ["verdict"] = entry.Verdict, ["phase"] = entry.Phase })]);
        }

        return new(instance, kind, worksIn, state, output, exit ?? (state == FlowInstanceState.Done ? HarnessExit.Success : null), why, passedOver, handledBy, at is { } minutes ? At.AddMinutes(minutes) : null);
    }

    private static JsonNode Legs(bool passed, params (string Leg, string Verdict)[] verdicts)
        => new JsonObject
        {
            ["passed"] = passed,
            ["exitCode"] = passed ? 0 : 1,
            ["verdicts"] = new JsonArray([.. verdicts.Select(verdict => (JsonNode)new JsonObject { ["leg"] = verdict.Leg, ["verdict"] = verdict.Verdict })]),
            ["red"] = new JsonArray(),
            ["runDirectory"] = null,
        };

    private static JsonNode Output(string json) => JsonNode.Parse(json)!;
}
