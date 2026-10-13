using RepoHarness.Core.Orchestration;
using RepoHarness.Core.Results;

namespace RepoHarness.Tests;

/// <summary>
/// The one rule of what a dry run of fold-agent, delete-agent, refresh-agent or rebase-agent exits with: what its --apply,
/// given the same arguments, would exit with at that moment. One function decides it; these hold it to its table.
/// </summary>
public sealed class DryRunsTests
{
    /// <summary>A sound dry run with nothing in its way is the outcome its verb composed, the same object, exit 0.</summary>
    [Fact]
    public void ASoundDryRunWithNothingInItsWay_IsTheOutcomeItComposed()
    {
        var planned = CommandOutcome.Ok("dry run: folding agent 'ag' of 'o1' writes 2 path(s) into the main tree; pass --apply to write them", ["  a line"]);

        var outcome = DryRuns.Outcome(planned, RequestStanding.Sound);

        Assert.Same(planned, outcome);
    }

    /// <summary>
    /// A dry run whose rows need a consent nobody gave is refused - exit 13, as its --apply is - with the same words and
    /// the same lines, the command it names among them: what changes is the code, not anything it printed.
    /// </summary>
    [Fact]
    public void ADryRunThatNeedsAConsent_IsRefusedWithTheSameWordsAndLines()
    {
        var planned = CommandOutcome.Ok(
            "dry run: folding agent 'ag' of 'o1' writes 1 path(s) into the main tree and applies 1 row(s); 1 cell(s) of its rows do not keep their stored text "
            + "word for word: the line 'to write them:' above is the command that does it",
            ["  a line", "to write them: 'dssharness fold-agent o1 ag --apply --accept-lost D-X-Y:closing'"]);

        var outcome = DryRuns.Outcome(planned, RequestStanding.NeedsConsent);

        Assert.Equal(HarnessExit.Refused, outcome.ExitCode);
        Assert.Equal(planned.Message, outcome.Message);
        Assert.Equal(planned.Details, outcome.Details);
    }

    /// <summary>
    /// A dry run whose request is refused, and one whose verb could not read what it weighs, answer as they composed: the
    /// refusal with its code, the failure with its own - a closed agent's husk among them, exit 21, as every --apply of it
    /// is.
    /// </summary>
    [Fact]
    public void ADryRunRefusedOrUnreadable_IsTheOutcomeItComposed_WithItsOwnCode()
    {
        var refused = CommandOutcome.Refused("the refusal", ["  a line"]);
        var unreadable = CommandOutcome.Failed(HarnessExit.CommandFailed, "the failure");
        var husk = CommandOutcome.Failed(HarnessExit.Incomplete, "dry run: ...; --apply keeps its evidence again and cannot finish the removal: ...");

        Assert.Same(refused, DryRuns.Outcome(refused, RequestStanding.Refused));
        Assert.Same(unreadable, DryRuns.Outcome(unreadable, null));
        Assert.Same(husk, DryRuns.Outcome(husk, RequestStanding.Refused));
    }

    /// <summary>
    /// How a request stands whose verb met a refusal or a failure rather than a plan: a refusal or a usage error is a
    /// request put right; anything a command meets only by failing is nothing the request could correct.
    /// </summary>
    [Fact]
    public void StandingOf_RefusedAndUsageErrorsAreRefused_AndFailuresAreNothing()
    {
        Assert.Equal(RequestStanding.Refused, DryRuns.StandingOf(CommandOutcome.Refused("no")));
        Assert.Equal(RequestStanding.Refused, DryRuns.StandingOf(CommandOutcome.Usage("no")));
        Assert.Null(DryRuns.StandingOf(CommandOutcome.Failed(HarnessExit.CommandFailed, "no")));
        Assert.Null(DryRuns.StandingOf(CommandOutcome.Failed(HarnessExit.Incomplete, "no")));
    }
}
