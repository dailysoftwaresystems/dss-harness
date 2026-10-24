using System.Text.Json;
using System.Text.Json.Nodes;
using DssHarness.Core.Application;
using DssHarness.Core.Configuration;
using DssHarness.Core.Execution;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;

namespace DssHarness.Tests;

/// <summary>
/// What an answer of fold-agent, delete-agent, refresh-agent or rebase-agent knows beyond its lines: one record, which the
/// <c>--json</c> document is serialised from member by member and a step of a run reads typed - the consents a request
/// lacks, the reasons it is refused for with a conflict told apart from every other, what a fold wrote - and the cause a
/// caller that can wait reads, carried whatever form the answer is asked in.
/// </summary>
public sealed class AgentVerbFactsTests
{
    private const string Agent = "ag";

    /// <summary>The file both trees change.</summary>
    private const string Both = "src/m.txt";

    /// <summary>A second file both trees change, at lines a three-way merge takes cleanly.</summary>
    private const string Clean = "src/n.txt";

    private const string RowId = "D-TEST-AGENT-ROW";

    private static readonly Dictionary<string, string> Row = new()
    {
        ["status"] = "open",
        ["priority"] = "P2",
        ["trigger"] = "something the agent found",
        ["closing"] = "the fix",
        ["cross-refs"] = "b.txt",
    };

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// The document is the facts serialised, member by member: beside what it always said, each consent the request
    /// lacks, each reason it is refused for, what a fold wrote - the arm registry merged by arm apart from the paths
    /// merged as text - and what a refresh's or a move's plan names, so a script and a step of a run read one thing.
    /// </summary>
    [Fact]
    public void TheDocument_IsTheFactsSerialised_NeedsRefusalsFoldedAndPlannedAmongThem()
    {
        var outcome = CommandOutcome.Refused("refused", ["a line"]);
        var facts = new AgentVerbFacts(
            outcome.ExitCode,
            outcome.Message,
            Applied: true,
            RequestStanding.Refused,
            Holders: null,
            ["a line"],
            [ConsentKind.FoldNewRows, ConsentKind.FoldLostRows, ConsentKind.FoldOverCells, ConsentKind.FoldTwoChanges],
            [RefusalKind.Conflict, RefusalKind.Other],
            new AgentFolded(["a.txt"], ["gone.txt"], [Both], "mutations/arms.txt", [new HandedPath("h.txt", "abc"), new HandedPath("d.txt", null)], [".plans/r.md"], [RowId], [$"{RowId}:closing"]),
            new AgentPlanned(["t.txt"], ["k.txt"], ["h.txt"], [Both], ArmRegistry: null)
            {
                Written = ["w.txt"],
                Deleted = ["x.txt"],
                Registries = [".plans/r.md"],
                Rows = [RowId],
                Mergeable = ["c.txt"],
                Conflicting = ["k2.txt"],
                NewRows = ["D-TEST-NEW"],
                LostCells = [$"{RowId}:closing"],
                OverCells = [$"{RowId}:status"],
            });

        var document = JsonNode.Parse(Assert.Single(OrchestrationReports.Document(outcome, facts).Data))!.AsObject();

        Assert.Equal(
            ["exitCode", "summary", "applied", "request", "heldNow", "heldUnread", "lines", "needs", "refusals", "folded", "planned"],
            document.Select(member => member.Key));
        Assert.Equal(["fold-new-rows", "fold-lost-rows", "fold-over-cells", "fold-two-changes"], Texts(document["needs"]));
        Assert.Equal(["conflict", "other"], Texts(document["refusals"]));
        Assert.Equal(
            """{"written":["a.txt"],"deleted":["gone.txt"],"merged":["src/m.txt"],"armRegistry":"mutations/arms.txt","handed":[{"path":"h.txt","digest":"abc"},{"path":"d.txt","digest":null}],"registries":[".plans/r.md"],"rows":["D-TEST-AGENT-ROW"],"overCells":["D-TEST-AGENT-ROW:closing"]}""",
            document["folded"]!.ToJsonString());
        Assert.Equal(
            """{"taken":["t.txt"],"kept":["k.txt"],"handed":["h.txt"],"merged":["src/m.txt"],"armRegistry":null,"written":["w.txt"],"deleted":["x.txt"],"registries":[".plans/r.md"],"rows":["D-TEST-AGENT-ROW"],"mergeable":["c.txt"],"conflicting":["k2.txt"],"newRows":["D-TEST-NEW"],"lostCells":["D-TEST-AGENT-ROW:closing"],"overCells":["D-TEST-AGENT-ROW:status"]}""",
            document["planned"]!.ToJsonString());
    }

    /// <summary>
    /// The document keeps everything of the outcome but its data: the cause a caller that can wait reads, and the lines
    /// the command says beside the document - a document that dropped them would tell a script asking with --json to give
    /// up on a run it should wait out.
    /// </summary>
    [Fact]
    public void TheDocument_KeepsTheOutcomesCauseAndLines()
    {
        var outcome = CommandOutcome.Refused("a run holds it", ["held now: a run"]) with { Cause = OutcomeCause.LockHeld };
        var facts = new AgentVerbFacts(outcome.ExitCode, outcome.Message, Applied: false, RequestStanding.Sound, Holders: null, ["held now: a run"], [], []);

        var answered = OrchestrationReports.Document(outcome, facts);

        Assert.Equal(OutcomeCause.LockHeld, answered.Cause);
        Assert.Equal(["held now: a run"], answered.Details);
        Assert.Equal(HarnessExit.Refused, answered.ExitCode);
        Assert.Equal("a run holds it", answered.Message);
        Assert.True(answered.Quiet);
        Assert.Same(facts, answered.Facts);
    }

    /// <summary>
    /// A fold's dry run that only a run holding its tree refuses says that run is why - asked for its document as asked for
    /// its lines - and its --apply, refused at its hold, says so too: the one refusal a caller waits out.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AFoldOnlyARunHolding_IsLockHeld_InItsDocumentAsInItsLines(bool json)
    {
        using var temp = new TempDirectory();
        var (kit, worktree) = await AFoldToMakeAsync(temp);

        await using var held = await HoldAsync(kit, worktree);

        var dry = await kit.Harness.AgentService.FoldAsync(kit.Main, OrchestrationKit.Orchestrator, Agent, new FoldAllowances(), apply: false, Token, json);
        var applied = await kit.Harness.AgentService.FoldAsync(kit.Main, OrchestrationKit.Orchestrator, Agent, new FoldAllowances(), apply: true, Token, json);

        Assert.Equal(HarnessExit.Refused, dry.ExitCode);
        Assert.Equal(OutcomeCause.LockHeld, dry.Cause);
        Assert.Equal(HarnessExit.Refused, applied.ExitCode);
        Assert.Equal(OutcomeCause.LockHeld, applied.Cause);
        Assert.Equal(RequestStanding.Sound, Assert.IsType<AgentVerbFacts>(applied.Facts).Standing);
    }

    /// <summary>
    /// A run holding the tree is no lock to wait on where the request also needs a consent or is refused for itself:
    /// waited out, the request meets that again, so the refusal carries no cause a caller would try again on.
    /// </summary>
    [Fact]
    public async Task ARunHoldingTheTree_IsNoLockToWaitOn_WhereTheRequestAlsoNeedsAConsentOrIsRefused()
    {
        using var temp = new TempDirectory();
        var (kit, worktree) = await AFoldToMakeAsync(temp);
        kit.FileRow(Agent, RowId, Row);

        await using var held = await HoldAsync(kit, worktree);

        var needing = await kit.FoldAsync(Agent, apply: false);
        var refused = await kit.FoldAsync(Agent, apply: false, new FoldAllowances { New = [RowId], Merge = ["src/typo.txt"] });

        Assert.Equal(HarnessExit.Refused, needing.ExitCode);
        Assert.Equal(RequestStanding.NeedsConsent, Assert.IsType<AgentVerbFacts>(needing.Facts).Standing);
        Assert.Equal(OutcomeCause.None, needing.Cause);
        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Equal(RequestStanding.Refused, Assert.IsType<AgentVerbFacts>(refused.Facts).Standing);
        Assert.Equal(OutcomeCause.None, refused.Cause);
    }

    /// <summary>
    /// An --apply refused for a path whose merge conflicts and for a clean merge nobody asked for is refused for both, the
    /// consent nobody gave as something else: an --apply refuses what its dry run asks, so it is never read as refused for
    /// conflicting merges alone - which a step of a run would put to a person as one, and the --apply would still refuse.
    /// Its dry run asks for the consent and is refused for the conflict, each in its own member.
    /// </summary>
    [Fact]
    public async Task AnApplyRefusedForAConflict_AndForAMergeNobodyAskedFor_IsRefusedForBoth()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        OrchestrationKit.Write(kit.Main, Both, AgentFoldMergeTests.Text());
        OrchestrationKit.Write(kit.Main, Clean, AgentFoldMergeTests.Text());
        await kit.Harness.CommitAllAsync(kit.Main, "two files both trees will change", Token);
        var worktree = await kit.CreateAgentAsync(Agent);
        OrchestrationKit.Write(worktree, Both, AgentFoldMergeTests.Text((2, "AGENT 2")));
        OrchestrationKit.Write(kit.Main, Both, AgentFoldMergeTests.Text((2, "MAIN 2")));
        OrchestrationKit.Write(worktree, Clean, AgentFoldMergeTests.Text((2, "AGENT 2")));
        OrchestrationKit.Write(kit.Main, Clean, AgentFoldMergeTests.Text((9, "MAIN 9")));

        var dry = Facts(await kit.FoldAsync(Agent, apply: false));
        var applied = Facts(await kit.FoldAsync(Agent, apply: true));

        Assert.Equal(RequestStanding.Refused, dry.Standing);
        Assert.Equal([ConsentKind.FoldTwoChanges], dry.Needs);
        Assert.Equal([RefusalKind.Conflict], dry.Refusals);
        Assert.Equal(RequestStanding.Refused, applied.Standing);
        Assert.Empty(applied.Needs);
        Assert.Equal([RefusalKind.Conflict, RefusalKind.Other], applied.Refusals);
        Assert.Null(applied.Folded);
    }

    /// <summary>
    /// What a fold did rides its answer whole - every path written and removed, exactly - and a fold that did nothing at
    /// all carries none, the one way an answer says so: a fold of an agent with no work, as a deletion that folds nothing.
    /// </summary>
    [Fact]
    public async Task WhatAFoldDid_RidesItsAnswer_AndOneThatDidNothingCarriesNone()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        await kit.CreateAgentAsync("idle");
        var worktree = await kit.CreateAgentAsync(Agent);
        OrchestrationKit.Write(worktree, "b.txt", "two\nagent edit\n");
        File.Delete(Path.Combine(worktree, "docs", "x.md"));
        var discarding = await kit.CreateAgentAsync("discarding");
        OrchestrationKit.Write(discarding, "a.txt", "one\nnot kept\n");

        var nothing = await kit.FoldAsync("idle", apply: true);
        var folded = await kit.FoldAsync(Agent, apply: true);
        var discarded = await kit.DeleteAsync("discarding", apply: true, discard: true);

        Assert.True(nothing.Succeeded, OrchestrationKit.Describe(nothing));
        Assert.Null(Facts(nothing).Folded);
        Assert.True(folded.Succeeded, OrchestrationKit.Describe(folded));
        Same(new AgentFolded(["b.txt"], ["docs/x.md"], [], ArmRegistry: null, [], [], [], []), Facts(folded).Folded);
        Assert.True(discarded.Succeeded, OrchestrationKit.Describe(discarded));
        Assert.Null(Facts(discarded).Folded);
    }

    /// <summary>
    /// A refresh and a move of an agent's base carry what their plan names, their dry run's and their --apply's alike: what
    /// a refresh hands and a move takes and keeps - so a step of a run reads what the plan was without reading a line.
    /// </summary>
    [Fact]
    public async Task ARefreshAndAMoveOfItsBase_CarryWhatTheirPlanNames()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        OrchestrationKit.Write(kit.Main, "h.txt", "handed\n");
        await kit.CreateAgentAsync(Agent);
        OrchestrationKit.Write(kit.Main, "a.txt", "one\nmain edit\n");

        var refreshDry = Facts(await kit.Harness.AgentService.RefreshAsync(kit.Main, OrchestrationKit.Orchestrator, Agent, RefreshRequest.Under("a.txt"), apply: false, Token));
        var refreshed = Facts(await kit.Harness.AgentService.RefreshAsync(kit.Main, OrchestrationKit.Orchestrator, Agent, RefreshRequest.Under("a.txt"), apply: true, Token));

        OrchestrationKit.Write(kit.Main, "c.txt", "new\n");
        await kit.Harness.CommitAllAsync(kit.Main, "the main tree moves on", Token);

        var moveDry = Facts(await kit.Harness.AgentService.RebaseAsync(kit.Main, OrchestrationKit.Orchestrator, Agent, new RebaseRequest(), apply: false, Token));
        var moved = Facts(await kit.Harness.AgentService.RebaseAsync(kit.Main, OrchestrationKit.Orchestrator, Agent, new RebaseRequest(), apply: true, Token));

        Same(new AgentPlanned([], [], ["a.txt"], [], ArmRegistry: null), refreshDry.Planned);
        Same(new AgentPlanned([], [], ["a.txt"], [], ArmRegistry: null), refreshed.Planned);
        Same(new AgentPlanned(["c.txt"], ["a.txt", "h.txt"], [], [], ArmRegistry: null), moveDry.Planned);
        Same(new AgentPlanned(["c.txt"], ["a.txt", "h.txt"], [], [], ArmRegistry: null), moved.Planned);
        Assert.Null(refreshed.Folded);
    }

    /// <summary>
    /// A refusal that is not over a conflict - a refresh told to leave out a path it would not hand, a misspelling most
    /// likely - is typed as something else, its plan carried all the same.
    /// </summary>
    [Fact]
    public async Task ARefreshRefusedForAnythingElse_IsTypedAsSuch()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        await kit.CreateAgentAsync(Agent);
        OrchestrationKit.Write(kit.Main, "a.txt", "one\nmain edit\n");

        var refused = await kit.Harness.AgentService.RefreshAsync(
            kit.Main, OrchestrationKit.Orchestrator, Agent, RefreshRequest.Under("a.txt") with { Except = ["a.txx"] }, apply: false, Token);

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Equal(RequestStanding.Refused, Facts(refused).Standing);
        Assert.Equal([RefusalKind.Other], Facts(refused).Refusals);
        Same(new AgentPlanned([], [], ["a.txt"], [], ArmRegistry: null), Facts(refused).Planned);
    }

    /// <summary>
    /// A path both trees changed whose merge conflicts is typed among the refusals as a conflict, apart from every other
    /// refusal - a dry run of a fold and of a deletion alike, and the --apply of each - so a request refused for
    /// conflicting merges alone is told from any other without reading a line; and a misspelled option beside it is
    /// something else.
    /// </summary>
    [Fact]
    public async Task AConflictingMerge_IsTypedAsAConflict_ApartFromEveryOtherRefusal()
    {
        using var temp = new TempDirectory();
        var (kit, worktree) = await AtItsBaseAsync(temp);
        OrchestrationKit.Write(worktree, Both, AgentFoldMergeTests.Text((2, "AGENT 2")));
        OrchestrationKit.Write(kit.Main, Both, AgentFoldMergeTests.Text((2, "MAIN 2")));

        var conflicting = Facts(await kit.FoldAsync(Agent, apply: false));
        var withATypo = Facts(await kit.FoldAsync(Agent, apply: false, new FoldAllowances { Merge = ["src/typo.txt"] }));
        var deleting = Facts(await kit.DeleteAsync(Agent, apply: false));
        var applying = Facts(await kit.FoldAsync(Agent, apply: true));
        var deletingApplied = Facts(await kit.DeleteAsync(Agent, apply: true));
        var document = JsonNode.Parse(Assert.Single(
            (await kit.Harness.AgentService.FoldAsync(kit.Main, OrchestrationKit.Orchestrator, Agent, new FoldAllowances(), apply: false, Token, json: true)).Data))!;

        Assert.Equal(RequestStanding.Refused, conflicting.Standing);
        Assert.Empty(conflicting.Needs);
        Assert.Equal([RefusalKind.Conflict], conflicting.Refusals);
        Assert.Equal(RequestStanding.Refused, withATypo.Standing);
        Assert.Equal([RefusalKind.Conflict, RefusalKind.Other], withATypo.Refusals);
        Assert.Equal(RequestStanding.Refused, deleting.Standing);
        Assert.Equal([RefusalKind.Conflict], deleting.Refusals);
        Assert.Equal(RequestStanding.Refused, applying.Standing);
        Assert.Equal([RefusalKind.Conflict], applying.Refusals);
        Assert.Equal(RequestStanding.Refused, deletingApplied.Standing);
        Assert.Equal([RefusalKind.Conflict], deletingApplied.Refusals);
        Assert.Null(deletingApplied.Folded);
        Assert.Equal(AgentStates.Live, kit.Record(Agent).State);
        Assert.Equal(["conflict"], Texts(document["refusals"]));
        Assert.Equal([], Texts(document["needs"]));
        Assert.Equal("refused", document["request"]!.GetValue<string>());
    }

    /// <summary>
    /// A refusal that is not over a conflict - a misspelled option alone - is typed as something else; a clean merge
    /// nobody asked for is no refusal at all but a consent the request lacks, and types no reason.
    /// </summary>
    [Fact]
    public async Task ARefusalOfAnythingElse_IsTypedAsSuch_AndAConsentTypesNoReason()
    {
        using var temp = new TempDirectory();
        var (kit, worktree) = await AtItsBaseAsync(temp);
        OrchestrationKit.Write(worktree, Both, AgentFoldMergeTests.Text((2, "AGENT 2")));
        OrchestrationKit.Write(kit.Main, Both, AgentFoldMergeTests.Text((9, "MAIN 9")));

        var lacking = Facts(await kit.FoldAsync(Agent, apply: false));
        var misspelt = Facts(await kit.FoldAsync(Agent, apply: false, new FoldAllowances { Merge = [Both, "src/typo.txt"] }));

        Assert.Equal(RequestStanding.NeedsConsent, lacking.Standing);
        Assert.Equal([ConsentKind.FoldTwoChanges], lacking.Needs);
        Assert.Empty(lacking.Refusals);
        Assert.Equal(RequestStanding.Refused, misspelt.Standing);
        Assert.Equal([RefusalKind.Other], misspelt.Refusals);
    }

    /// <summary>
    /// A move of an agent's base refused over a path whose merge conflicts types that refusal as a conflict too, its dry
    /// run and its --apply alike.
    /// </summary>
    [Fact]
    public async Task ARebaseRefusedOverAConflict_IsTypedAsOne()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync(Agent);
        OrchestrationKit.Write(worktree, "b.txt", "two\nagent edit\n");
        OrchestrationKit.Write(kit.Main, "b.txt", "two\ncommitted\n");
        await kit.Harness.CommitAllAsync(kit.Main, "between waves", Token);

        var dry = Facts(await kit.Harness.AgentService.RebaseAsync(kit.Main, OrchestrationKit.Orchestrator, Agent, new RebaseRequest(), apply: false, Token));
        var applied = Facts(await kit.Harness.AgentService.RebaseAsync(kit.Main, OrchestrationKit.Orchestrator, Agent, new RebaseRequest(), apply: true, Token));

        Assert.Equal(RequestStanding.Refused, dry.Standing);
        Assert.Equal([RefusalKind.Conflict], dry.Refusals);
        Assert.Equal(RequestStanding.Refused, applied.Standing);
        Assert.Equal([RefusalKind.Conflict], applied.Refusals);
    }

    /// <summary>
    /// An answer stopped before anything was weighed - its arguments a usage error, its agent not there - carries facts
    /// that say exactly that, through the use case a step of a run calls, and answers --json with the document's stopped
    /// form, the same bytes the command's plumbing answers every other stop with.
    /// </summary>
    [Fact]
    public async Task AnAnswerStoppedBeforeAnythingWasWeighed_CarriesFactsSayingSo()
    {
        using var temp = new TempDirectory();
        var (kit, _) = await AFoldToMakeAsync(temp);
        var agents = (IAgentService)kit.Harness.AgentService;

        var misspelt = await agents.FoldAsync(new FoldAgentRequest(kit.Main, OrchestrationKit.Orchestrator, Agent, new FoldAllowances { AcceptLost = [$"{RowId}:status"] }, Apply: true), Token);
        var nobody = await agents.DeleteAsync(new DeleteAgentRequest(kit.Main, OrchestrationKit.Orchestrator, "nobody", new FoldAllowances(), Apply: false, DiscardUncommitted: false, Json: true), Token);
        var both = await agents.DeleteAsync(new DeleteAgentRequest(kit.Main, OrchestrationKit.Orchestrator, Agent, OrchestrationKit.Making(RowId), Apply: true, DiscardUncommitted: true), Token);

        Assert.Equal(HarnessExit.UsageError, misspelt.Outcome.ExitCode);
        Unweighed(misspelt);
        Assert.Equal(HarnessExit.UsageError, both.Outcome.ExitCode);
        Assert.Equal("--new lets a fold through what it otherwise refuses, and --discard-uncommitted folds nothing: give one or the other.", both.Outcome.Message);
        Unweighed(both);

        Assert.Equal(HarnessExit.Refused, nobody.Outcome.ExitCode);
        Assert.Equal($"Orchestrator '{OrchestrationKit.Orchestrator}' has no agent named 'nobody'.", nobody.Outcome.Message);
        Unweighed(nobody);
        Assert.Equal([OrchestrationReports.Stopped(HarnessExit.Refused, nobody.Outcome.Message)], nobody.Outcome.Data);
        Assert.True(nobody.Outcome.Quiet);
    }

    /// <summary>
    /// A use case's answer whose outcome carries facts of another type is a defect where it was composed, said as one -
    /// never read as an answer that carries no facts.
    /// </summary>
    [Fact]
    public void FactsOfAnotherType_AreADefectSaid_NeverReadAsNone()
    {
        var outcome = CommandOutcome.Ok("done") with { Facts = "not the facts" };

        var refused = Assert.Throws<InvalidOperationException>(() => UseCaseResult<AgentVerbFacts>.From(outcome));

        Assert.Equal("The outcome 'done' carries facts of type 'String', and its use case answers with 'AgentVerbFacts'.", refused.Message);
        Assert.Null(UseCaseResult<AgentVerbFacts>.From(CommandOutcome.Ok("done")).Facts);
    }

    /// <summary>
    /// A deletion whose fold measured clean and whose evidence then could not be read failed for that, and stands as its
    /// failure does: no request weighed - never sound, never a lock to wait on where a run holds a tree as well - since a
    /// read that failed is no consent and no run in the way.
    /// </summary>
    [Fact]
    public async Task ADeletionWhoseEvidenceCannotBeRead_StandsAsItsFailure_NeverSoundNorHeld()
    {
        using var temp = new TempDirectory();
        var (kit, worktree) = await AFoldToMakeAsync(temp);
        var evidence = Directory.CreateDirectory(Path.Combine(worktree, "evidence")).FullName;
        var agents = kit.Harness.Agents(new CannotListFiles(kit.Harness.FileSystem, evidence), kit.Harness.AnchorRegistryService);

        var unread = await agents.DeleteAsync(kit.Main, OrchestrationKit.Orchestrator, Agent, new FoldAllowances(), apply: false, discardUncommitted: false, Token, json: true);

        Assert.Equal(HarnessExit.CommandFailed, unread.ExitCode);
        Assert.Equal(
            $"Agent '{Agent}' of '{OrchestrationKit.Orchestrator}' was not deleted: what 'evidence' (the disk would not say) holds cannot be read, "
                + "and what cannot be read is never taken for nothing there. Nothing was written or removed.",
            unread.Message);
        Assert.Null(Assert.IsType<AgentVerbFacts>(unread.Facts).Standing);
        Assert.Null(JsonNode.Parse(Assert.Single(unread.Data))!["request"]);

        await using var held = await HoldAsync(kit, worktree);

        var heldToo = await agents.DeleteAsync(kit.Main, OrchestrationKit.Orchestrator, Agent, new FoldAllowances(), apply: false, discardUncommitted: false, Token);

        Assert.Equal(HarnessExit.CommandFailed, heldToo.ExitCode);
        Assert.Equal(OutcomeCause.None, heldToo.Cause);
        Assert.Contains(heldToo.Details!, line => line.StartsWith("held now: ", StringComparison.Ordinal));
    }

    /// <summary>
    /// README and the architecture say what the document's new members are, as the code names them, and the stopped form
    /// with them.
    /// </summary>
    [Fact]
    public void TheDocumentsSayTheDocumentsNeedsRefusalsAndFolded_AsTheCodeNamesThem()
    {
        var readme = Prose.Document("README.md");
        var architecture = Prose.Document("docs", "architecture.md");

        Assert.Contains(
            "Beside them `needs` names each consent the request lacks "
                + $"(`{OrchestrationReports.Named(ConsentKind.FoldNewRows)}`, `{OrchestrationReports.Named(ConsentKind.FoldLostRows)}`, "
                + $"`{OrchestrationReports.Named(ConsentKind.FoldOverCells)}`, `{OrchestrationReports.Named(ConsentKind.FoldTwoChanges)}`), "
                + $"`refusals` each reason it is refused for (`{OrchestrationReports.Named(RefusalKind.Conflict)}` where a path both trees "
                + $"changed has a merge that conflicts, `{OrchestrationReports.Named(RefusalKind.Other)}` for anything else), `folded` "
                + "what a fold's `--apply` wrote, merged, handed and applied, and `planned` what the verb's plan names - what a rebase "
                + "takes and keeps, a refresh or a fold hands, each merges, a fold writes and deletes, the registry files its rows "
                + "change and the rows it applies - and what each consent it lacks and each conflict it meets is about.",
            readme,
            StringComparison.Ordinal);
        Assert.Contains(
            $"The document is the answer's facts serialised (`{nameof(AgentVerbFacts)}`), which a step of an orchestrator run will "
                + "read typed, so beside those it says `needs`, each consent the request lacks by kind - "
                + $"`{OrchestrationReports.Named(ConsentKind.FoldNewRows)}` (`--new`), `{OrchestrationReports.Named(ConsentKind.FoldLostRows)}` "
                + $"(`--accept-lost`), `{OrchestrationReports.Named(ConsentKind.FoldOverCells)}` (`--accept-over`), "
                + $"`{OrchestrationReports.Named(ConsentKind.FoldTwoChanges)}` (`--merge`); `refusals`, each reason it stands refused "
                + $"for, `{OrchestrationReports.Named(RefusalKind.Conflict)}` where a path both trees changed has a three-way merge that "
                + $"conflicts and `{OrchestrationReports.Named(RefusalKind.Other)}` for anything else, so a request refused for "
                + "conflicting merges alone says `conflict` and nothing else; and `folded`, what the `--apply` of a fold and of a "
                + "deletion, which folds back what is left of the agent, did - gone through or stopped part way after writing some of "
                + "it (21): the paths written, deleted, merged and handed, each handed with its digest, the arm registry where it was "
                + "merged by arm, the registries its rows changed, the ids of the rows applied and the cells `--accept-over` took; null "
                + "for every other answer and where it did nothing at all; and `planned`, what the plan of the verb names, its dry "
                + "run's and its `--apply`'s alike: the paths taken, kept, handed and merged, the arm registry merged by arm, the "
                + "paths a fold - a deletion's among them - writes and deletes, the registry files its rows change, as git names "
                + "them, and the rows it applies, and what each consent it lacks and each conflict it meets is about (`mergeable`, "
                + "`conflicting`, `newRows`, `lostCells`, `overCells`); null where no plan was measured.",
            architecture,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A fold's dry run names what it would write and what each consent it lacks is about - the paths it writes and
    /// removes, the clean merge nobody asked for and the row nobody named new, which it does not apply as asked - and,
    /// that row named new, the row it applies and the registry file it changes: the facts and the document alike, so a
    /// step of a run asks with them and holds its --apply to them.
    /// </summary>
    [Fact]
    public async Task AFoldsDryRun_NamesWhatItWouldWrite_AndWhatEachConsentItLacksIsAbout()
    {
        using var temp = new TempDirectory();
        var (kit, worktree) = await AtItsBaseAsync(temp);
        File.Delete(Path.Combine(worktree, "docs", "x.md"));
        OrchestrationKit.Write(worktree, Both, AgentFoldMergeTests.Text((2, "AGENT 2")));
        OrchestrationKit.Write(kit.Main, Both, AgentFoldMergeTests.Text((9, "MAIN 9")));
        kit.FileRow(Agent, RowId, Row);

        var dry = await kit.Harness.AgentService.FoldAsync(kit.Main, OrchestrationKit.Orchestrator, Agent, new FoldAllowances(), apply: false, Token, json: true);
        var planned = Facts(dry).Planned!;
        var document = JsonNode.Parse(Assert.Single(dry.Data))!["planned"]!;
        var making = await kit.Harness.AgentService.FoldAsync(kit.Main, OrchestrationKit.Orchestrator, Agent, OrchestrationKit.Making(RowId), apply: false, Token, json: true);
        var madeDocument = JsonNode.Parse(Assert.Single(making.Data))!["planned"]!;

        Assert.Equal(RequestStanding.NeedsConsent, Facts(dry).Standing);
        Assert.Equal([ConsentKind.FoldNewRows, ConsentKind.FoldTwoChanges], Facts(dry).Needs);
        Assert.Equal(["b.txt"], planned.Written);
        Assert.Equal(["docs/x.md"], planned.Deleted);
        Assert.Equal([Both], planned.Mergeable);
        Assert.Equal([Both], planned.Named(ConsentKind.FoldTwoChanges));
        Assert.Equal([RowId], planned.NewRows);
        Assert.Equal([RowId], planned.Named(ConsentKind.FoldNewRows));
        Assert.Empty(planned.Rows);
        Assert.Empty(planned.Registries);
        Assert.Empty(planned.Conflicting);
        Assert.Equal(["b.txt", "docs/x.md", Both], planned.WouldWrite);
        Assert.Equal(["b.txt"], Texts(document["written"]));
        Assert.Equal(["docs/x.md"], Texts(document["deleted"]));
        Assert.Equal([Both], Texts(document["mergeable"]));
        Assert.Equal([RowId], Texts(document["newRows"]));
        Assert.Equal([ConsentKind.FoldTwoChanges], Facts(making).Needs);
        Assert.Empty(Facts(making).Planned!.NewRows);
        Assert.Equal([RowId], Facts(making).Planned!.Rows);
        Assert.Equal([AnchorSettings.DefaultPendingAnchorsPath], Facts(making).Planned!.Registries);
        Assert.Equal([AnchorSettings.DefaultPendingAnchorsPath], Texts(madeDocument["registries"]));
        Assert.Equal([RowId], Texts(madeDocument["rows"]));
    }

    /// <summary>
    /// A row a fold moves from one registry to another - closed, from the pending registry to the done one - changes both
    /// files, and its plan names both by the path git names them by, never by the registry's role; what the fold did names
    /// them alike.
    /// </summary>
    [Fact]
    public async Task ARowMovedToDone_NamesBothRegistryFiles_ItsPlanAndWhatItDidAlike()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        await kit.CreateAgentAsync(Agent);
        kit.FileRow(Agent, RowId, Row);
        Assert.True((await kit.FoldAsync(Agent, apply: true, OrchestrationKit.Making(RowId))).Succeeded);
        kit.FileRow(Agent, RowId, new Dictionary<string, string>(Row) { ["status"] = "closed" });

        var dry = Facts(await kit.FoldAsync(Agent, apply: false));
        var folded = await kit.FoldAsync(Agent, apply: true);

        // In git's order of names, as every list a plan holds is.
        string[] both = [AnchorSettings.DefaultDoneAnchorsPath, AnchorSettings.DefaultPendingAnchorsPath];

        Assert.True(folded.Succeeded, OrchestrationKit.Describe(folded));
        Assert.Equal(both, dry.Planned!.Registries);
        Assert.Equal([RowId], dry.Planned.Rows);
        Assert.Equal(both, Facts(folded).Folded!.Registries);
        Assert.Equal(both, Facts(folded).Planned!.Registries);
    }

    /// <summary>
    /// A refresh over a path both trees changed whose three-way merge is clean lacks a consent - the merge nobody asked
    /// for - as a fold does, and says so by its kind with the path it is about; its code stays 13. Named with --merge it
    /// is sound; its --apply without it is refused for something else, an --apply asking no consent.
    /// </summary>
    [Fact]
    public async Task ARefreshOverACleanPathBothChanged_NeedsTheMergeConsent_ItsCodeStill13()
    {
        using var temp = new TempDirectory();
        var (kit, worktree) = await AtItsBaseAsync(temp);
        OrchestrationKit.Write(worktree, Both, AgentFoldMergeTests.Text((2, "AGENT 2")));
        OrchestrationKit.Write(kit.Main, Both, AgentFoldMergeTests.Text((9, "MAIN 9")));

        var dry = await kit.Harness.AgentService.RefreshAsync(kit.Main, OrchestrationKit.Orchestrator, Agent, RefreshRequest.Under(Both), apply: false, Token);
        var merging = await kit.Harness.AgentService.RefreshAsync(kit.Main, OrchestrationKit.Orchestrator, Agent, RefreshRequest.Under(Both) with { Merge = [Both] }, apply: false, Token);
        var applied = await kit.Harness.AgentService.RefreshAsync(kit.Main, OrchestrationKit.Orchestrator, Agent, RefreshRequest.Under(Both), apply: true, Token);

        Assert.Equal(HarnessExit.Refused, dry.ExitCode);
        Assert.Equal(RequestStanding.NeedsConsent, Facts(dry).Standing);
        Assert.Equal([ConsentKind.FoldTwoChanges], Facts(dry).Needs);
        Assert.Empty(Facts(dry).Refusals);
        Assert.Equal([Both], Facts(dry).Planned!.Mergeable);
        Assert.Equal(RequestStanding.Sound, Facts(merging).Standing);
        Assert.Equal(HarnessExit.Refused, applied.ExitCode);
        Assert.Equal(RequestStanding.Refused, Facts(applied).Standing);
        Assert.Equal([RefusalKind.Other], Facts(applied).Refusals);
    }

    /// <summary>A refresh over a path both trees changed whose merge conflicts is refused for a conflict, with the path its plan names.</summary>
    [Fact]
    public async Task ARefreshOverAConflictingPath_IsRefusedForAConflict()
    {
        using var temp = new TempDirectory();
        var (kit, worktree) = await AtItsBaseAsync(temp);
        OrchestrationKit.Write(worktree, Both, AgentFoldMergeTests.Text((2, "AGENT 2")));
        OrchestrationKit.Write(kit.Main, Both, AgentFoldMergeTests.Text((2, "MAIN 2")));

        var dry = Facts(await kit.Harness.AgentService.RefreshAsync(kit.Main, OrchestrationKit.Orchestrator, Agent, RefreshRequest.Under(Both), apply: false, Token));
        var applied = Facts(await kit.Harness.AgentService.RefreshAsync(kit.Main, OrchestrationKit.Orchestrator, Agent, RefreshRequest.Under(Both), apply: true, Token));

        Assert.Equal(RequestStanding.Refused, dry.Standing);
        Assert.Empty(dry.Needs);
        Assert.Equal([RefusalKind.Conflict], dry.Refusals);
        Assert.Equal([Both], dry.Planned!.Conflicting);
        Assert.Empty(dry.Planned.Mergeable);
        Assert.Equal([RefusalKind.Conflict], applied.Refusals);
    }

    /// <summary>
    /// A refresh over a path the agent changed where no merge is measured at all - the agent removed it, the main tree
    /// changed it - is refused for something else: neither a consent anybody can give nor a conflict a merge met.
    /// </summary>
    [Fact]
    public async Task ARefreshOverAPathOneTreeRemoved_IsRefusedForSomethingElse()
    {
        using var temp = new TempDirectory();
        var (kit, worktree) = await AtItsBaseAsync(temp);
        File.Delete(Path.Combine(worktree, Both));
        OrchestrationKit.Write(kit.Main, Both, AgentFoldMergeTests.Text((9, "MAIN 9")));

        var dry = Facts(await kit.Harness.AgentService.RefreshAsync(kit.Main, OrchestrationKit.Orchestrator, Agent, RefreshRequest.Under(Both), apply: false, Token));

        Assert.Equal(RequestStanding.Refused, dry.Standing);
        Assert.Empty(dry.Needs);
        Assert.Equal([RefusalKind.Other], dry.Refusals);
        Assert.Empty(dry.Planned!.Mergeable);
        Assert.Empty(dry.Planned.Conflicting);
    }

    /// <summary>
    /// A refresh over a path whose merge conflicts and another it would undo for something else is refused for both: a
    /// conflict alone is never what it is refused for, so no step hands around it.
    /// </summary>
    [Fact]
    public async Task ARefreshOverAConflictAndAnythingElse_IsRefusedForBoth()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        OrchestrationKit.Write(kit.Main, Both, AgentFoldMergeTests.Text());
        OrchestrationKit.Write(kit.Main, Clean, AgentFoldMergeTests.Text());
        await kit.Harness.CommitAllAsync(kit.Main, "two files both trees will change", Token);
        var worktree = await kit.CreateAgentAsync(Agent);
        OrchestrationKit.Write(worktree, Both, AgentFoldMergeTests.Text((2, "AGENT 2")));
        OrchestrationKit.Write(kit.Main, Both, AgentFoldMergeTests.Text((2, "MAIN 2")));
        File.Delete(Path.Combine(worktree, Clean));
        OrchestrationKit.Write(kit.Main, Clean, AgentFoldMergeTests.Text((9, "MAIN 9")));

        var dry = Facts(await kit.Harness.AgentService.RefreshAsync(kit.Main, OrchestrationKit.Orchestrator, Agent, new RefreshRequest { Paths = [Both, Clean] }, apply: false, Token));

        Assert.Equal(RequestStanding.Refused, dry.Standing);
        Assert.Empty(dry.Needs);
        Assert.Equal([RefusalKind.Conflict, RefusalKind.Other], dry.Refusals);
        Assert.Equal([Both], dry.Planned!.Conflicting);
    }

    /// <summary>
    /// A fold whose only path it cannot fold is a clean merge nobody asked for, but one of whose rows is refused for more
    /// than a consent, is refused for something else: the merge is still a consent it lacks, and no consent would let it go.
    /// </summary>
    [Fact]
    public async Task AFoldLackingAMergeWithARowRefusedForItself_IsRefusedForSomethingElse()
    {
        using var temp = new TempDirectory();
        var (kit, worktree) = await AtItsBaseAsync(temp);
        OrchestrationKit.Write(worktree, Both, AgentFoldMergeTests.Text((2, "AGENT 2")));
        OrchestrationKit.Write(kit.Main, Both, AgentFoldMergeTests.Text((9, "MAIN 9")));
        OrchestrationKit.WriteRow(kit.Layout.RowsDirectory(Agent), RowId, ("status", "open"), ("trigger", "t"));

        var dry = Facts(await kit.FoldAsync(Agent, apply: false, OrchestrationKit.Making(RowId)));

        Assert.Equal(RequestStanding.Refused, dry.Standing);
        Assert.Equal([ConsentKind.FoldTwoChanges], dry.Needs);
        Assert.Equal([RefusalKind.Other], dry.Refusals);
    }

    /// <summary>
    /// A move of an agent's base over a path both changed whose merge is clean lacks the merge consent, as a fold does;
    /// over a conflict and a clean merge both, it is refused for the conflict alone, the clean merge still a consent it
    /// lacks - and its --apply, asking none, is refused for something else besides.
    /// </summary>
    [Fact]
    public async Task ARebaseOverACleanPath_NeedsTheMergeConsent_AndOverAConflictBesidesIsRefusedForTheConflictAlone()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        OrchestrationKit.Write(kit.Main, Both, AgentFoldMergeTests.Text());
        OrchestrationKit.Write(kit.Main, Clean, AgentFoldMergeTests.Text());
        await kit.Harness.CommitAllAsync(kit.Main, "two files both trees will change", Token);
        var worktree = await kit.CreateAgentAsync(Agent);
        OrchestrationKit.Write(worktree, Clean, AgentFoldMergeTests.Text((2, "AGENT 2")));
        OrchestrationKit.Write(kit.Main, Clean, AgentFoldMergeTests.Text((9, "MAIN 9")));
        await kit.Harness.CommitAllAsync(kit.Main, "between waves", Token);

        var clean = Facts(await kit.Harness.AgentService.RebaseAsync(kit.Main, OrchestrationKit.Orchestrator, Agent, new RebaseRequest(), apply: false, Token));

        OrchestrationKit.Write(worktree, Both, AgentFoldMergeTests.Text((2, "AGENT 2")));
        OrchestrationKit.Write(kit.Main, Both, AgentFoldMergeTests.Text((2, "MAIN 2")));
        await kit.Harness.CommitAllAsync(kit.Main, "a conflict besides", Token);

        var both = Facts(await kit.Harness.AgentService.RebaseAsync(kit.Main, OrchestrationKit.Orchestrator, Agent, new RebaseRequest(), apply: false, Token));
        var applied = Facts(await kit.Harness.AgentService.RebaseAsync(kit.Main, OrchestrationKit.Orchestrator, Agent, new RebaseRequest(), apply: true, Token));

        Assert.Equal(RequestStanding.NeedsConsent, clean.Standing);
        Assert.Equal([ConsentKind.FoldTwoChanges], clean.Needs);
        Assert.Empty(clean.Refusals);
        Assert.Equal([Clean], clean.Planned!.Mergeable);
        Assert.Equal(RequestStanding.Refused, both.Standing);
        Assert.Equal([ConsentKind.FoldTwoChanges], both.Needs);
        Assert.Equal([RefusalKind.Conflict], both.Refusals);
        Assert.Equal([Both], both.Planned!.Conflicting);
        Assert.Equal([RefusalKind.Conflict, RefusalKind.Other], applied.Refusals);
    }

    /// <summary>
    /// An --apply held to what its dry run named writes it where it still names exactly that, and where the trees moved
    /// between the two - its fold would write a path the dry run never named - it is refused for something else and writes
    /// nothing: a fold and a deletion's fold alike. Held to nothing, as from the command line, it is never compared.
    /// </summary>
    [Fact]
    public async Task AnApplyHeldToItsDryRun_RefusesWhatTheDryRunNeverNamed_AndWritesWhatItDid()
    {
        using var temp = new TempDirectory();
        var (kit, worktree) = await AFoldToMakeAsync(temp);
        var agents = (IAgentService)kit.Harness.AgentService;
        var named = Facts(await kit.FoldAsync(Agent, apply: false)).Planned!;
        OrchestrationKit.Write(worktree, "c.txt", "written after the dry run\n");

        var moved = await agents.FoldAsync(new FoldAgentRequest(kit.Main, OrchestrationKit.Orchestrator, Agent, new FoldAllowances(), Apply: true) { Expected = named }, Token);
        var deleting = await agents.DeleteAsync(new DeleteAgentRequest(kit.Main, OrchestrationKit.Orchestrator, Agent, new FoldAllowances(), Apply: true, DiscardUncommitted: false) { Expected = named }, Token);

        Assert.Equal(HarnessExit.Refused, moved.Outcome.ExitCode);
        Assert.Equal(
            $"Agent '{Agent}' of '{OrchestrationKit.Orchestrator}' was not folded: what it would write is not what its dry run named - written adds 'c.txt'. Nothing was written.",
            moved.Outcome.Message);
        Assert.Equal([RefusalKind.Other], moved.Facts!.Refusals);
        Assert.Equal(["b.txt", "c.txt"], moved.Facts.Planned!.Written);
        Assert.Null(moved.Facts.Folded);
        Assert.False(File.Exists(Path.Combine(kit.Main, "c.txt")));
        Assert.Equal(HarnessExit.Refused, deleting.Outcome.ExitCode);
        Assert.Equal(
            $"Agent '{Agent}' of '{OrchestrationKit.Orchestrator}' was not deleted: what it would write is not what its dry run named - written adds 'c.txt'. Nothing was written or removed.",
            deleting.Outcome.Message);
        Assert.Equal([RefusalKind.Other], deleting.Facts!.Refusals);
        Assert.Equal(AgentStates.Live, kit.Record(Agent).State);

        var again = Facts(await kit.FoldAsync(Agent, apply: false)).Planned!;
        var held = await agents.FoldAsync(new FoldAgentRequest(kit.Main, OrchestrationKit.Orchestrator, Agent, new FoldAllowances(), Apply: true) { Expected = again }, Token);

        Assert.True(held.Outcome.Succeeded, OrchestrationKit.Describe(held.Outcome));
        Same(new AgentFolded(["b.txt", "c.txt"], [], [], ArmRegistry: null, [], [], [], []), held.Facts!.Folded);
        Assert.Equal(["b.txt", "c.txt"], held.Facts.Planned!.Written);
    }

    /// <summary>
    /// What one plan names differs from another's by each member it writes, merges, hands, removes or applies, said with
    /// what it adds and what it drops, compared as sets; the consents' names and the order of each list are no difference.
    /// </summary>
    [Fact]
    public void ADifferenceOfPlans_SaysEachMemberAddedAndDropped_ComparedAsSets()
    {
        var named = new AgentPlanned([], [], ["h.txt"], ["m.txt"], ArmRegistry: null) { Written = ["a.txt", "b.txt"], Registries = [".plans/r.md"], Rows = ["R-1"] };

        Assert.Null(named.DifferenceFrom(named with { Written = ["b.txt", "a.txt"], Mergeable = ["x.txt"], NewRows = ["R-9"] }));
        Assert.Equal(
            "written adds 'c.txt' and drops 'a.txt'; merged adds 'arms.txt'; rows drops 'R-1'",
            (named with { Written = ["b.txt", "c.txt"], ArmRegistry = "arms.txt", Rows = [] }).DifferenceFrom(named));
        Assert.Equal("removed adds 'gone.txt'; handed drops 'h.txt'; registries adds '.plans/d.md'", (named with { Deleted = ["gone.txt"], Handed = [], Registries = [".plans/r.md", ".plans/d.md"] }).DifferenceFrom(named));
    }

    /// <summary>An agent with one change of its own to fold, <c>b.txt</c>.</summary>
    private static async Task<(OrchestrationKit Kit, string Worktree)> AFoldToMakeAsync(TempDirectory temp)
    {
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync(Agent);
        OrchestrationKit.Write(worktree, "b.txt", "two\nagent edit\n");

        return (kit, worktree);
    }

    /// <summary>An agent made at a commit that holds <see cref="Both"/>, with a change of its own besides.</summary>
    private static async Task<(OrchestrationKit Kit, string Worktree)> AtItsBaseAsync(TempDirectory temp)
    {
        var kit = await OrchestrationKit.PrepareAsync(temp);
        OrchestrationKit.Write(kit.Main, Both, AgentFoldMergeTests.Text());
        await kit.Harness.CommitAllAsync(kit.Main, "a file two will change", Token);
        var worktree = await kit.CreateAgentAsync(Agent);
        OrchestrationKit.Write(worktree, "b.txt", "two\nagent edit\n");

        return (kit, worktree);
    }

    /// <summary>Takes <paramref name="tree"/> whole for a run of another command, for as long as the caller holds it.</summary>
    private static async Task<RunLockHandle> HoldAsync(OrchestrationKit kit, string tree)
    {
        var attempt = await kit.Harness.RunLock.TryAcquireAsync(
            new HarnessLayout(kit.Main, kit.Main),
            new LockRequest { Host = "local", Tree = tree, Scope = LockScope.TreeExclusive, RunId = RunId.New(), Command = "test" },
            Token);

        return attempt.Handle ?? throw new InvalidOperationException($"the test could not hold '{tree}': {attempt.HeldBy}");
    }

    private static AgentVerbFacts Facts(CommandOutcome outcome) => Assert.IsType<AgentVerbFacts>(outcome.Facts);

    /// <summary>
    /// That <paramref name="actual"/> holds exactly what <paramref name="expected"/> does, member by member and each list
    /// in its order: a record's own equality compares its lists by reference.
    /// </summary>
    internal static void Same<T>(T expected, T? actual)
        where T : class
        => Assert.Equal(JsonSerializer.Serialize(expected), actual is null ? "null" : JsonSerializer.Serialize(actual));

    /// <summary>
    /// That <paramref name="answer"/> carries the facts of an answer stopped before anything was weighed: its own code and
    /// line, nothing applied, no standing, nobody asked, no lines, nothing needed, no reason typed and nothing folded.
    /// </summary>
    private static void Unweighed(UseCaseResult<AgentVerbFacts> answer)
    {
        var facts = Assert.IsType<AgentVerbFacts>(answer.Facts);

        Assert.Equal(answer.Outcome.ExitCode, facts.ExitCode);
        Assert.Equal(answer.Outcome.Message, facts.Summary);
        Assert.False(facts.Applied);
        Assert.Null(facts.Standing);
        Assert.Null(facts.Holders);
        Assert.Empty(facts.Lines);
        Assert.Empty(facts.Needs);
        Assert.Empty(facts.Refusals);
        Assert.Null(facts.Folded);
    }

    private static List<string> Texts(JsonNode? array) => [.. array!.AsArray().Select(item => item!.GetValue<string>())];

    /// <summary>The real file system, save that one directory's files cannot be listed, as one the disk will not say of.</summary>
    private sealed class CannotListFiles(IFileSystem inner, string directory) : PassThroughFileSystem(inner)
    {
        public override IEnumerable<string> EnumerateFiles(string path, bool recursive)
            => string.Equals(Path.GetFullPath(path), Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase)
                ? throw new IOException("the disk would not say.")
                : base.EnumerateFiles(path, recursive);
    }
}
