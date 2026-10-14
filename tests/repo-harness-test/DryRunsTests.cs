using RepoHarness.Core.Execution;
using RepoHarness.Core.Orchestration;
using RepoHarness.Core.Results;
using RepoHarness.Core.Sync;
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

        var outcome = DryRuns.Answer(planned, RequestStanding.Sound).Outcome;

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

        var outcome = DryRuns.Answer(planned, RequestStanding.NeedsConsent).Outcome;

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

        Assert.Same(refused, DryRuns.Answer(refused, RequestStanding.Refused).Outcome);
        Assert.Same(unreadable, DryRuns.Answer(unreadable, null).Outcome);
        Assert.Same(husk, DryRuns.Answer(husk, RequestStanding.Refused).Outcome);
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

    /// <summary>A line saying the copies kept in a worktree could not be looked for: what the removal cannot take as it stands.</summary>
    private static KeptCopyLine Unlooked() => KeptCopyLine.Stopping("the leg copies kept in it could not be removed: the disk would not say");

    /// <summary>
    /// The lines the copies kept in a worktree say decide the code by what each is, never by being said: a directory no
    /// sync made, which goes with the worktree, holds nothing - the dry run is the outcome its verb composed - while a
    /// copy a run or a sweep holds, named on its family's own line, leaves the removal unfinished, exit 21, whose ending
    /// points at that line and at no <c>held now:</c> line.
    /// </summary>
    [Fact]
    public void KeptCopyLinesHoldNothingButACopyARunOrASweepHolds()
    {
        var planned = CommandOutcome.Ok("dry run: deleting agent 'a' of 'o' folds 12 path(s), removes 0 and applies 3 row(s)", ["  a line"]);
        var somebodys = KeptCopyLine.Said(@"left 'C:\src\repo\.worktrees\o\a\.legs\357e24c', named as a leg copy kept in it: nothing says the harness made it");
        var held = KeptCopyLine.Held(
            @"C:\src\repo\.worktrees\o\a\.mutations\x86_64-gcc-debug-1",
            @"left the mutation worker 'C:\src\repo\.worktrees\o\a\.mutations\x86_64-gcc-debug-1' kept in it: a sweep still running holds it: pid 4242");

        var said = new TreeHeld([], null, null, [somebodys]);
        var holding = new TreeHeld([TreeHolder.OfKeptCopy(held, entry: null, standing: null, ahead: null)], null, null, [somebodys, held]);

        Assert.False(said.HoldsCopies);
        Assert.False(said.HoldsOnlyCopies);
        Assert.Same(planned, DryRuns.Answer(planned, RequestStanding.Sound, said).Outcome);
        Assert.Equal(RequestStanding.Sound, DryRuns.Answer(planned, RequestStanding.Sound, said).Standing);
        Assert.True(holding.HoldsOnlyCopies);

        var unfinished = DryRuns.Answer(planned, RequestStanding.Sound, holding).Outcome;

        Assert.Equal(HarnessExit.Incomplete, unfinished.ExitCode);
        Assert.Equal($"{planned.Message}, {TreeHolders.RemovalHeld}", unfinished.Message);
        Assert.DoesNotContain("held now:", TreeHolders.RemovalHeld, StringComparison.Ordinal);
        Assert.Contains("named above with what holds it", TreeHolders.RemovalHeld, StringComparison.Ordinal);
    }

    /// <summary>
    /// What a copy was left as is weighed in one place, the removal's verdict on it, and the line a deletion says of it
    /// follows that verdict: held, a copy a run or a sweep holds, with what its claim records of the holder; not removed,
    /// what the removal cannot take as it stands; somebody's, only said. Every value is weighed, and one this build does
    /// not know is refused there and by the line alike - never read as only said.
    /// </summary>
    [Fact]
    public void WhatACopyWasLeftAs_IsWeighedInOnePlace_AndAValueThisBuildDoesNotKnowIsRefused()
    {
        var path = @"C:\src\repo\.worktrees\o\a\.mutations\x86_64-gcc-debug-1";
        var claim = new LogOwner("this-machine", 4242, "20261012-151500-ab12cd34", DateTimeOffset.Parse("2026-10-12T15:15:00Z", System.Globalization.CultureInfo.InvariantCulture));

        foreach (var left in Enum.GetValues<WorkerLeftAs>())
        {
            _ = new WorkerLeft(path, "why", left).Verdict;
        }

        const string Again = "'dssharness delete-worktree o/a'";
        var held = KeptCopyLine.Left(new WorkerLeft(path, "why.", WorkerLeftAs.Held, claim), "mutation worker", Again);
        var stopping = KeptCopyLine.Left(new WorkerLeft(path, "why.", WorkerLeftAs.NotRemoved), "mutation worker", Again);
        var said = KeptCopyLine.Left(new WorkerLeft(path, "why.", WorkerLeftAs.Somebodys), "mutation worker", Again);

        Assert.Equal((path, false, claim), (held.Told.HeldPath, held.Told.Stops, held.Told.Claim));
        Assert.Equal($"left the mutation worker '{path}' kept in it: why; {Again} removes it once nothing holds it", held.Told.Line);
        Assert.Equal((null, true), (stopping.Told.HeldPath, stopping.Told.Stops));
        Assert.Equal($"left the mutation worker '{path}' kept in it: why; {Again} removes it once nothing stops it", stopping.Told.Line);
        Assert.Equal((null, false), (said.Told.HeldPath, said.Told.Stops));
        Assert.Equal($"left '{path}', named as a mutation worker kept in it: why", said.Told.Line);
        Assert.Equal($"the mutation worker '{path}' kept in it went with it, forced: why", held.WentWithIt);
        Assert.Equal($"the mutation worker '{path}' kept in it went with it, forced: why", stopping.WentWithIt);
        Assert.Equal(WorktreeReports.WentWithItsTree(path), said.WentWithIt);

        var unknown = new WorkerLeft(path, "why", (WorkerLeftAs)99);

        Assert.Throws<ArgumentOutOfRangeException>(() => unknown.Verdict);
        Assert.Throws<ArgumentOutOfRangeException>(() => KeptCopyLine.Left(unknown, "mutation worker", Again));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WorkersRemoval([], [unknown]).Verdict);
    }

    /// <summary>
    /// What a claim records of the run holding a copy is carried only by a copy left held, which a claim holds: refused on
    /// one left otherwise, as it is made and as a host's answer is read - never carried, and never silently dropped.
    /// </summary>
    [Fact]
    public void AClaimIsCarriedOnlyByACopyLeftHeld_AsMadeAndAsAHostsAnswerIsRead()
    {
        var path = @"C:\src\repo\.worktrees\o\a\.mutations\x86_64-gcc-debug-1";
        var claim = new LogOwner("this-machine", 4242, "20261012-151500-ab12cd34", DateTimeOffset.Parse("2026-10-12T15:15:00Z", System.Globalization.CultureInfo.InvariantCulture));

        Assert.Equal(claim, new WorkerLeft(path, "held", WorkerLeftAs.Held, claim).Claim);
        Assert.Throws<ArgumentException>(() => new WorkerLeft(path, "not removed", WorkerLeftAs.NotRemoved, claim));
        Assert.Throws<ArgumentException>(() => new WorkerLeft(path, "somebody's", WorkerLeftAs.Somebodys, claim));

        var answer = SyncServe.Answer(new SyncWorkersAnswer(new WorkersRemoval([], [new WorkerLeft(path, "held", WorkerLeftAs.Held, claim)])));

        Assert.Equal(claim, SyncServe.ReadAnswer<SyncWorkersAnswer>(answer)!.Workers.Left.Single().Claim);
        Assert.Contains("\"Held\"", answer, StringComparison.Ordinal);

        var refusal = Assert.Throws<HarnessException>(() => SyncServe.ReadAnswer<SyncWorkersAnswer>(answer.Replace("\"Held\"", "\"NotRemoved\"", StringComparison.Ordinal)));

        Assert.StartsWith("The far side answered in a shape this build cannot read: ", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>heldNow</c> says each standing a holder can have, and nothing only where none is known: a standing this build
    /// does not know is refused, never said as unknown.
    /// </summary>
    [Fact]
    public void HeldNowSaysEveryStanding_AndRefusesOneThisBuildDoesNotKnow()
    {
        var entry = new LockEntry("local", @"C:\src\repo\.worktrees\o\a", LockScope.TreeExclusive, new LockHolder("this-machine", 4242, "20261012-151500-ab12cd34", DateTimeOffset.Parse("2026-10-12T15:15:00Z", System.Globalization.CultureInfo.InvariantCulture), "test"));
        var planned = CommandOutcome.Ok("dry run: ...");

        string? Said(HolderStanding? standing)
            => System.Text.Json.Nodes.JsonNode.Parse(OrchestrationReports.Document(planned, applied: false, RequestStanding.Sound, new TreeHeld([TreeHolder.Named(copy: false, entry, standing, ahead: null, said: "said")], null, null, [])).Data[0])!["heldNow"]![0]!["standing"]?.GetValue<string>();

        Assert.Equal(["no-slot", "waiting", "holds-slot", "on-host"], Enum.GetValues<HolderStanding>().Select(standing => Said(standing)));
        Assert.Null(Said(null));
        Assert.Throws<ArgumentOutOfRangeException>(() => Said((HolderStanding)99));
    }

    /// <summary>
    /// What is kept in a deletion's worktree that its removal cannot take as it stands decides a sound request only, and
    /// only below the rows that refuse its --apply before it reaches the removal: sound, the dry run is unfinished, exit 21,
    /// its request refused and its summary ending in the one sentence that says so - over a copy a run or a sweep holds as
    /// well, which waiting would end where this does not; a consent nobody gave keeps its own refusal and its standing; a
    /// refused request is the refusal it met; and a tree a run holds, or a lock file that cannot be read, refuses it as
    /// either always does, the request still sound - never the words that the --apply would go as far as its removal.
    /// </summary>
    [Fact]
    public void WhatTheRemovalCannotTake_DecidesASoundRequestOnly_BelowALockThatCannotBeReadAndATreeHeld()
    {
        var planned = CommandOutcome.Ok("dry run: deleting agent 'a' of 'o' folds 12 path(s), removes 0 and applies 3 row(s)", ["  a line"]);
        var consent = CommandOutcome.Ok("dry run: ... 1 cell(s) of its rows do not keep their stored text word for word ...", ["to do it: '...'"]);
        var refused = CommandOutcome.Refused("the refusal", ["  a line"]);
        var stops = new TreeHeld([], null, null, [Unlooked()]);
        var held = KeptCopyLine.Held(@"C:\src\repo\.worktrees\o\a\.mutations\x86_64-gcc-debug-1", "left the mutation worker ... a sweep still running holds it: pid 4242");

        var sound = DryRuns.Answer(planned, RequestStanding.Sound, stops);

        Assert.Equal(HarnessExit.Incomplete, sound.Outcome.ExitCode);
        Assert.Equal($"{planned.Message}{DryRuns.Unremovable}", sound.Outcome.Message);
        Assert.Equal(planned.Details, sound.Outcome.Details);
        Assert.Equal(RequestStanding.Refused, sound.Standing);

        var heldToo = DryRuns.Answer(planned, RequestStanding.Sound, new TreeHeld([TreeHolder.OfKeptCopy(held, null, null, null)], null, null, [held, Unlooked()]));

        Assert.Equal(HarnessExit.Incomplete, heldToo.Outcome.ExitCode);
        Assert.Equal($"{planned.Message}{DryRuns.Unremovable}", heldToo.Outcome.Message);
        Assert.Equal(RequestStanding.Refused, heldToo.Standing);

        var needsConsent = DryRuns.Answer(consent, RequestStanding.NeedsConsent, stops);

        Assert.Equal(HarnessExit.Refused, needsConsent.Outcome.ExitCode);
        Assert.Equal(consent.Message, needsConsent.Outcome.Message);
        Assert.Equal(RequestStanding.NeedsConsent, needsConsent.Standing);
        Assert.Same(refused, DryRuns.Answer(refused, RequestStanding.Refused, stops).Outcome);

        var treeHeld = DryRuns.Answer(planned, RequestStanding.Sound, ATreeHeld() with { KeptCopies = [Unlooked()] });

        Assert.Equal(HarnessExit.Refused, treeHeld.Outcome.ExitCode);
        Assert.Equal($"{planned.Message}, {TreeHolders.Blocked}", treeHeld.Outcome.Message);
        Assert.Equal(RequestStanding.Sound, treeHeld.Standing);

        var unread = DryRuns.Answer(planned, RequestStanding.Sound, new TreeHeld([], "The run lock 'C:\\src\\.harness-config\\lock.json' could not be read", null, [Unlooked()]));

        Assert.Equal(HarnessExit.Refused, unread.Outcome.ExitCode);
        Assert.EndsWith("; --apply is refused until the lock file can be read", unread.Outcome.Message, StringComparison.Ordinal);
        Assert.Equal(RequestStanding.Sound, unread.Standing);

        foreach (var before in new[] { needsConsent, treeHeld, unread })
        {
            Assert.DoesNotContain("could not finish it", before.Outcome.Message, StringComparison.Ordinal);
        }
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

        var held = DryRuns.Answer(planned, RequestStanding.Sound, ATreeHeld()).Outcome;
        var heldHusk = DryRuns.Answer(husk, RequestStanding.Refused, ATreeHeld()).Outcome;

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

        var outcome = DryRuns.Answer(failed, null, ATreeHeld()).Outcome;

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

        var heldRefused = DryRuns.Answer(refused, RequestStanding.Refused, ATreeHeld()).Outcome;
        var heldConsent = DryRuns.Answer(consent, RequestStanding.NeedsConsent, ATreeHeld()).Outcome;

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

        var copyHeld = DryRuns.Answer(planned, RequestStanding.Sound, ACopyHeld()).Outcome;
        var bothHeld = DryRuns.Answer(planned, RequestStanding.Sound, new TreeHeld([.. ATreeHeld().Holders, .. ACopyHeld().Holders], null, null, [])).Outcome;

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

        var held = DryRuns.Answer(planned, RequestStanding.Sound, unread).Outcome;
        var failing = DryRuns.Answer(failed, null, unread).Outcome;

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
