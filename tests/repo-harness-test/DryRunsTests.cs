using RepoHarness.Core.Execution;
using RepoHarness.Core.Orchestration;
using RepoHarness.Core.Results;
using RepoHarness.Core.Worktrees;

namespace RepoHarness.Tests;

/// <summary>
/// The one rule of what a dry run of fold-agent, delete-agent, refresh-agent or rebase-agent exits with: what its --apply,
/// given the same arguments, would exit with at that moment. One function decides it; these hold it to its table.
/// </summary>
public sealed class DryRunsTests
{
    /// <summary>A dry run with nothing in its way is the outcome its verb composed, the same object, exit 0.</summary>
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

    /// <summary>A run holding a tree the --apply takes, as the lock names it, with where its leg stands said.</summary>
    private static TreeHeld ATreeHeld()
    {
        var entry = new LockEntry("local", @"C:\src\repo\.worktrees\o\a", LockScope.TreeExclusive, new LockHolder("this-machine", 4242, "20261012-151500-ab12cd34", DateTimeOffset.Parse("2026-10-12T15:15:00Z", System.Globalization.CultureInfo.InvariantCulture), "test"));

        return new([TreeHolder.Named(copy: false, entry, HolderStanding.NoSlot, ahead: null, said: "said")], null, null, []);
    }

    /// <summary>A run holding only a copy a deletion removes, as the lock names it.</summary>
    private static TreeHeld ACopyHeld()
    {
        var entry = new LockEntry("wsl:Ubuntu", "/home/u/repo--wt/o--a", LockScope.TreeExclusive, new LockHolder("this-machine", 4242, "20261012-151500-ab12cd34", DateTimeOffset.Parse("2026-10-12T15:15:00Z", System.Globalization.CultureInfo.InvariantCulture), "test"));

        return new([TreeHolder.Named(copy: true, entry, HolderStanding.OnHost, ahead: null, said: "said")], null, null, []);
    }

    /// <summary>
    /// The lines the copies kept in a worktree say decide the code by what each is, never by being said: a directory no
    /// sync made, which goes with the worktree, and copies that could not be looked for hold nothing - the dry run is
    /// the outcome its verb composed - while a copy a run or a sweep holds, named on its family's own line, leaves the
    /// removal unfinished, exit 21, whose ending points at that line and at no <c>held now:</c> line.
    /// </summary>
    [Fact]
    public void KeptCopyLinesHoldNothingButACopyARunOrASweepHolds()
    {
        var planned = CommandOutcome.Ok("dry run: deleting agent 'a' of 'o' folds 12 path(s), removes 0 and applies 3 row(s)", ["  a line"]);
        var somebodys = KeptCopyLine.Said(@"left 'C:\src\repo\.worktrees\o\a\.legs\357e24c', named as a leg copy kept in it: nothing says the harness made it");
        var unlooked = KeptCopyLine.Stopping("the leg copies kept in it could not be removed: the disk would not say");
        var held = KeptCopyLine.Held(
            @"C:\src\repo\.worktrees\o\a\.mutations\x86_64-gcc-debug-1",
            @"left the mutation worker 'C:\src\repo\.worktrees\o\a\.mutations\x86_64-gcc-debug-1' kept in it: a sweep still running holds it: pid 4242");

        var said = new TreeHeld([], null, null, [somebodys, unlooked]);
        var holding = new TreeHeld([TreeHolder.OfKeptCopy(held, entry: null, standing: null, ahead: null)], null, null, [somebodys, held]);

        Assert.False(said.HoldsCopies);
        Assert.False(said.HoldsOnlyCopies);
        Assert.Same(planned, DryRuns.Outcome(planned, RequestStanding.Sound, said));
        Assert.True(holding.HoldsOnlyCopies);

        var unfinished = DryRuns.Outcome(planned, RequestStanding.Sound, holding);

        Assert.Equal(HarnessExit.Incomplete, unfinished.ExitCode);
        Assert.Equal($"{planned.Message}, {TreeHolders.RemovalHeld}", unfinished.Message);
        Assert.DoesNotContain("held now:", TreeHolders.RemovalHeld, StringComparison.Ordinal);
        Assert.Contains("named above with what holds it", TreeHolders.RemovalHeld, StringComparison.Ordinal);
    }

    /// <summary>
    /// A sound dry run that a run holds off is refused, exit 13, its summary ending in the one sentence that says nothing
    /// in the request needs correcting and waiting is all there is to do - and a husk's outcome (21) held the same way is
    /// 13 too, as every ordering of the apply has it.
    /// </summary>
    [Fact]
    public void ASoundDryRunThatARunHoldsOff_IsRefused_SayingNothingNeedsCorrecting()
    {
        var planned = CommandOutcome.Ok("dry run: folding agent 'a' of 'o' would write 192 path(s) into the main tree, remove 0 and apply 3 row(s)", ["  a line"]);
        var husk = CommandOutcome.Failed(HarnessExit.Incomplete, "dry run: <what closing it recorded>; --apply keeps its evidence again and cannot finish the removal: ...");

        var held = DryRuns.Outcome(planned, RequestStanding.Sound, ATreeHeld());
        var heldHusk = DryRuns.Outcome(husk, RequestStanding.Refused, ATreeHeld());

        Assert.Equal(HarnessExit.Refused, held.ExitCode);
        Assert.Equal($"{planned.Message}, {TreeHolders.Blocked}", held.Message);
        Assert.Equal(planned.Details, held.Details);
        Assert.Equal(HarnessExit.Refused, heldHusk.ExitCode);
        Assert.Equal($"{husk.Message}{TreeHolders.AlsoHeld}", heldHusk.Message);
    }

    /// <summary>
    /// A dry run whose verb could not read what it weighs keeps its failure's code where a run also holds what its --apply
    /// takes: what cannot be read stands above a holder in the plan's order, and the holder is named on the dry run's
    /// lines as ever.
    /// </summary>
    [Fact]
    public void AFailingDryRunThatARunAlsoHolds_KeepsItsOwnCode_AndNamesTheHolderOnItsLines()
    {
        var failed = CommandOutcome.Failed(
            HarnessExit.CommandFailed,
            "the failure",
            ["  a line", $"held now: 'C:\\src\\repo\\.worktrees\\o\\a' on local is held by pid 4242, run 20261012-151500-ab12cd34, since then, running 'test'."]);

        var outcome = DryRuns.Outcome(failed, null, ATreeHeld());

        Assert.Same(failed, outcome);
        Assert.Equal(HarnessExit.CommandFailed, outcome.ExitCode);
        Assert.Equal("the failure", outcome.Message);
    }

    /// <summary>
    /// A dry run that is refused for its own request, or needs a consent, and that a run also holds, keeps its own words
    /// and ends in the one clause that says so - never the words "nothing in this request needs correcting".
    /// </summary>
    [Fact]
    public void ADryRunRefusedOrNeedingAConsentThatARunAlsoHolds_EndsInTheOneClause()
    {
        var refused = CommandOutcome.Refused("the refusal", ["  a line"]);
        var consent = CommandOutcome.Ok("dry run: ... 1 cell(s) of its rows do not keep their stored text word for word ...", ["to write them: '...'"]);

        var heldRefused = DryRuns.Outcome(refused, RequestStanding.Refused, ATreeHeld());
        var heldConsent = DryRuns.Outcome(consent, RequestStanding.NeedsConsent, ATreeHeld());

        Assert.Equal(HarnessExit.Refused, heldRefused.ExitCode);
        Assert.Equal($"{refused.Message}{TreeHolders.AlsoHeld}", heldRefused.Message);
        Assert.Equal(HarnessExit.Refused, heldConsent.ExitCode);
        Assert.Equal($"{consent.Message}{TreeHolders.AlsoHeld}", heldConsent.Message);
        Assert.DoesNotContain("nothing in this request needs correcting", heldConsent.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A sound dry run of a deletion that only a copy holds is unfinished, exit 21, as its --apply is; a tree held beside
    /// the copy is 13, whatever the copies make of it.
    /// </summary>
    [Fact]
    public void ASoundDryRunOfADeletionThatOnlyACopyHolds_IsUnfinished_AndATreeHeldBesideItRefuses()
    {
        var planned = CommandOutcome.Ok("dry run: deleting agent 'a' of 'o' folds 12 path(s), removes 0 and applies 3 row(s), keeps its transcripts and 2 evidence file(s)", ["  a line"]);

        var copyHeld = DryRuns.Outcome(planned, RequestStanding.Sound, ACopyHeld());
        var bothHeld = DryRuns.Outcome(planned, RequestStanding.Sound, new TreeHeld([.. ATreeHeld().Holders, .. ACopyHeld().Holders], null, null, []));

        Assert.Equal(HarnessExit.Incomplete, copyHeld.ExitCode);
        Assert.Equal($"{planned.Message}, {TreeHolders.RemovalHeld}", copyHeld.Message);
        Assert.Equal(planned.Details, copyHeld.Details);
        Assert.Equal(HarnessExit.Refused, bothHeld.ExitCode);
        Assert.Equal($"{planned.Message}, {TreeHolders.Blocked}", bothHeld.Message);
    }

    /// <summary>
    /// A lock file that cannot be read refuses a sound dry run, exit 13, saying why; one whose verb could not read what it
    /// weighs keeps its own code, with why the holders cannot be named among its lines.
    /// </summary>
    [Fact]
    public void ALockThatCannotBeReadRefusesASoundDryRun_AndOnlyAddsALineToAFailingOne()
    {
        var planned = CommandOutcome.Ok("dry run: ...", ["  a line"]);
        var failed = CommandOutcome.Failed(HarnessExit.CommandFailed, "the failure");
        var unread = new TreeHeld([], "The run lock 'C:\\src\\.harness-config\\lock.json' could not be read: the disk is full", null, []);

        var held = DryRuns.Outcome(planned, RequestStanding.Sound, unread);
        var failing = DryRuns.Outcome(failed, null, unread);

        Assert.Equal(HarnessExit.Refused, held.ExitCode);
        Assert.Equal(
            "dry run: ...; whether a run holds what --apply needs cannot be told: The run lock 'C:\\src\\.harness-config\\lock.json' could not be read: the disk is "
                + "full; --apply is refused until the lock file can be read",
            held.Message);
        Assert.Equal(HarnessExit.CommandFailed, failing.ExitCode);
        Assert.Equal(failed.Message, failing.Message);
        Assert.Contains(failing.Details ?? [], line => line.StartsWith("whether a run holds what --apply needs cannot be told: ", StringComparison.Ordinal));
    }
}
