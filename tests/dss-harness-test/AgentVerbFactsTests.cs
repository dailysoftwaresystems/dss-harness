using System.Text.Json.Nodes;
using DssHarness.Core.Application;
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
    /// lacks, each reason it is refused for, and what a fold wrote - so a script and a step of a run read one thing.
    /// </summary>
    [Fact]
    public void TheDocument_IsTheFactsSerialised_NeedsRefusalsAndFoldedAmongThem()
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
            new AgentFolded(["a.txt"], ["gone.txt"], [Both], [new HandedPath("h.txt", "abc"), new HandedPath("d.txt", null)], ["decisions"], [RowId], [$"{RowId}:closing"]));

        var document = JsonNode.Parse(Assert.Single(OrchestrationReports.Document(outcome, facts).Data))!.AsObject();

        Assert.Equal(
            ["exitCode", "summary", "applied", "request", "heldNow", "heldUnread", "lines", "needs", "refusals", "folded"],
            document.Select(member => member.Key));
        Assert.Equal(["fold-new-rows", "fold-lost-rows", "fold-over-cells", "fold-two-changes"], Texts(document["needs"]));
        Assert.Equal(["conflict", "other"], Texts(document["refusals"]));
        Assert.Equal(
            """{"written":["a.txt"],"deleted":["gone.txt"],"merged":["src/m.txt"],"handed":[{"path":"h.txt","digest":"abc"},{"path":"d.txt","digest":null}],"registries":["decisions"],"rows":["D-TEST-AGENT-ROW"],"overCells":["D-TEST-AGENT-ROW:closing"]}""",
            document["folded"]!.ToJsonString());
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
    /// A path both trees changed whose merge conflicts is typed among the refusals as a conflict, apart from every other
    /// refusal - a dry run of a fold and of a deletion alike, and an --apply - so a request refused for conflicting merges
    /// alone is told from any other without reading a line; and a misspelled option beside it is something else.
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
                + $"changed has a merge that conflicts, `{OrchestrationReports.Named(RefusalKind.Other)}` for anything else), and `folded` "
                + "what a fold's `--apply` wrote, merged, handed and applied.",
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
                + "conflicting merges alone says `conflict` and nothing else; and `folded`, null but for the `--apply` of a fold and "
                + "of a deletion, which folds back what is left of the agent: the paths written, deleted, merged and handed, each "
                + "handed with its digest, the registries its rows changed, the ids of the rows applied and the cells `--accept-over` "
                + "took.",
            architecture,
            StringComparison.Ordinal);
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
