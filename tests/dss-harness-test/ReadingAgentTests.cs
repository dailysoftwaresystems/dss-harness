using System.Text.Json.Nodes;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Results;

namespace DssHarness.Tests;

/// <summary>
/// The agent that reads the main tree: a record and its two directories, and nothing in the tree. The engine makes one
/// for a read-only reason step, and no verb that works a worktree works on it.
/// </summary>
public sealed class ReadingAgentTests
{
    /// <summary>What deleting one says of its transcripts: that none is recorded, and that it looks for none.</summary>
    private const string NoSession = "no Claude session is recorded for it - an agent with no worktree records none - so no transcript is looked for";

    /// <summary>What a dry run of deleting one says it does, before the words that say how to do it.</summary>
    private const string Deleting =
        "dry run: deleting agent 'a7' of 'o1' folds nothing and removes no worktree - it reads the main tree, and has neither - and marks it deleted; ";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// Making one is one write and two directories: the record says it reads the main tree and holds none of what a
    /// worktree's record does, no worktree is made, nothing is held, and nothing is handed over - and the log takes the
    /// one line every making takes. The file says <c>access</c> only because this is the rarer kind: an agent with a
    /// worktree's record is what an older tool reads.
    /// </summary>
    [Fact]
    public async Task MakingOne_IsOneWriteTwoDirectories_AndNoWorktree()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var disk = new WritesSeen(kit.Harness.FileSystem);
        var agents = kit.Harness.Agents(disk, kit.Harness.AnchorRegistryService);

        var created = await agents.CreateAsync(
            new AgentCreateRequest(kit.Main, OrchestrationKit.Orchestrator, "a7", "model-b", Empty: false, Session: null, NoWorktree: true), Token);

        Assert.Equal(HarnessExit.Success, created.Outcome.ExitCode);
        Assert.Equal(
            $"Agent 'a7' of 'o1' was created with no worktree: it reads the main tree, and writes only '{kit.Layout.WorkDirectory("a7")}'",
            created.Outcome.Message);
        Assert.Equal(
            [
                $"record {kit.Layout.AgentRecordFile("a7")}",
                kit.Layout.WorkDirectory("a7"),
                kit.Layout.PlansDirectory("a7"),
                $"log {kit.Layout.LogFile("a7")}",
            ],
            created.Outcome.Details);

        // The record once and the log's one line, and nothing else written: a second write of either is how a making
        // said twice would show.
        Assert.Equal([kit.Layout.AgentRecordFile("a7"), kit.Layout.LogFile("a7")], disk.Written);
        Assert.Equal([kit.Layout.WorkDirectory("a7"), kit.Layout.PlansDirectory("a7")], disk.Made);
        Assert.Single(File.ReadAllLines(kit.Layout.LogFile("a7")));

        var record = kit.Record("a7");

        Assert.Equal(AgentRecord.AccessRead, record.Access);
        Assert.True(record.Reads);
        Assert.Null(record.Worktree);
        Assert.Null(record.WorktreesRoot);
        Assert.Null(record.Base);
        Assert.Null(record.Session);
        Assert.Null(record.WorktreePath(new DssHarness.Core.Repository.HarnessLayout(kit.Main, kit.Main)));
        Assert.Equal(AgentStates.Live, record.State);

        var text = File.ReadAllText(kit.Layout.AgentRecordFile("a7"));

        Assert.Contains("\"access\": \"read\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("worktree", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\"base\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\"session\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\"reads\"", text, StringComparison.Ordinal);

        Assert.True(Directory.Exists(kit.Layout.WorkDirectory("a7")));
        Assert.True(Directory.Exists(kit.Layout.PlansDirectory("a7")));
        Assert.False(File.Exists(kit.Layout.SeedFile("a7")));
        Assert.False(Directory.Exists(kit.Worktree("a7")));
    }

    /// <summary>
    /// It counts against no limit, and git's worktrees are not listed for it: beside an orchestrator's last open place it is
    /// made, where one more with a worktree is refused - and that refusal names the worktree agent alone, so the open
    /// agents leave the reader out.
    /// </summary>
    [Fact]
    public async Task MakingOne_CountsAgainstNoLimit_AndTheOpenAgentsLeaveItOut()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp, parallel: 1);
        await kit.CreateAgentAsync("a1");

        await kit.CreateReadingAgentAsync("a2");

        var refused = await kit.Harness.AgentService.CreateAsync(
            new AgentCreateRequest(kit.Main, OrchestrationKit.Orchestrator, "a3", "model-b", Empty: false, Session: null), Token);

        Assert.Equal(HarnessExit.Refused, refused.Outcome.ExitCode);
        Assert.Equal(
            "Orchestrator 'o1' already has 1 open agent(s), its limit: a1. Delete one with 'dssharness delete-agent o1 <agent> --apply', "
                + "or raise the limit with 'dssharness create-orchestrator o1 --model model-a --parallel <n>'. Nothing was created.",
            refused.Outcome.Message);
    }

    /// <summary>
    /// --empty hands a worktree nothing, and an agent with no worktree has nothing to hand; --session records the session
    /// whose transcripts deleting it keeps, and an agent with no worktree records none. Either beside --no-worktree is a
    /// usage error, which changes nothing - neither a new agent's directory nor an existing one's record.
    /// </summary>
    [Theory]
    [InlineData("empty", "--empty hands an agent's worktree nothing of the main tree's uncommitted state, and an agent made with --no-worktree has no worktree to hand anything to. Give one or the other. Nothing was changed.")]
    [InlineData("session", "--session records the Claude session whose transcripts delete-agent keeps, and an agent made with --no-worktree records none, so deleting it looks for none. Give one or the other. Nothing was changed.")]
    public async Task MakingOneWithEmptyOrASession_IsAUsageError_ThatChangesNothing(string given, string said)
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        await kit.CreateReadingAgentAsync("a7");
        var before = File.ReadAllText(kit.Layout.AgentRecordFile("a7"));

        foreach (var agent in new[] { "a7", "a8" })
        {
            var refused = await kit.Harness.AgentService.CreateAsync(
                new AgentCreateRequest(
                    kit.Main,
                    OrchestrationKit.Orchestrator,
                    agent,
                    "model-b",
                    Empty: given == "empty",
                    Session: given == "session" ? "sess1" : null,
                    NoWorktree: true),
                Token);

            Assert.Equal(HarnessExit.UsageError, refused.Outcome.ExitCode);
            Assert.Equal(said, refused.Outcome.Message);
            Assert.Null(refused.Outcome.Details);
        }

        Assert.Equal(before, File.ReadAllText(kit.Layout.AgentRecordFile("a7")));
        Assert.Single(File.ReadAllLines(kit.Layout.LogFile("a7")));
        Assert.False(Directory.Exists(kit.Layout.AgentDirectory("a8")));
    }

    /// <summary>
    /// Asked again with the option and the same model, it is already as asked, since there is nothing of it to record
    /// again; with another model it is refused, saying that create-agent changes nothing of such an agent and naming no
    /// seed-agent; and a worktree agent is another agent whichever option names it. None of them writes its record.
    /// </summary>
    [Fact]
    public async Task AskedAgain_ChangesNothing_OrRefusesTheOtherKind()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        await kit.CreateReadingAgentAsync("a7");
        await kit.CreateAgentAsync("w1");
        var before = File.ReadAllText(kit.Layout.AgentRecordFile("a7"));

        var again = await kit.Harness.AgentService.CreateAsync(
            new AgentCreateRequest(kit.Main, OrchestrationKit.Orchestrator, "a7", "model-b", Empty: false, Session: null, NoWorktree: true), Token);

        Assert.Equal(HarnessExit.Success, again.Outcome.ExitCode);
        Assert.Equal("agent 'a7' of 'o1' is already as asked", again.Outcome.Message);
        Assert.Equal([$"record {kit.Layout.AgentRecordFile("a7")}"], again.Outcome.Details);

        var without = await kit.Harness.AgentService.CreateAsync(
            new AgentCreateRequest(kit.Main, OrchestrationKit.Orchestrator, "a7", "model-b", Empty: false, Session: null), Token);

        Assert.Equal(HarnessExit.Refused, without.Outcome.ExitCode);
        Assert.Equal("Agent 'a7' of 'o1' has no worktree: an agent with one is another agent. Nothing was changed.", without.Outcome.Message);

        var with = await kit.Harness.AgentService.CreateAsync(
            new AgentCreateRequest(kit.Main, OrchestrationKit.Orchestrator, "w1", "model-b", Empty: false, Session: null, NoWorktree: true), Token);

        Assert.Equal(HarnessExit.Refused, with.Outcome.ExitCode);
        Assert.Equal("Agent 'w1' of 'o1' has a worktree: an agent with no worktree is another agent. Nothing was changed.", with.Outcome.Message);

        var model = await kit.Harness.AgentService.CreateAsync(
            new AgentCreateRequest(kit.Main, OrchestrationKit.Orchestrator, "a7", "model-c", Empty: false, Session: null, NoWorktree: true), Token);

        Assert.Equal(HarnessExit.Refused, model.Outcome.ExitCode);
        Assert.Equal(
            "Agent 'a7' of 'o1' exists, created for model 'model-b'. Run again, create-agent changes nothing of an agent with no worktree. "
                + "Nothing was changed.",
            model.Outcome.Message);

        Assert.Equal(before, File.ReadAllText(kit.Layout.AgentRecordFile("a7")));
    }

    /// <summary>
    /// Every verb that works a worktree refuses it with the one sentence that is all four of them, before git is asked
    /// anything about the agent: the service's own git, which every question about an agent goes through, fails any
    /// call, so a guard that let one through first would fail the verb rather than refuse it - and counts the calls
    /// besides, so one caught on the way is still seen. Finding the repository, as every command does first, asks the
    /// harness's own git, which this test leaves as it is.
    /// </summary>
    [Theory]
    [InlineData("seed")]
    [InlineData("refresh")]
    [InlineData("rebase")]
    [InlineData("fold")]
    public async Task EveryVerbThatWorksAWorktree_RefusesIt_BeforeGitIsAskedAboutTheAgent(string verb)
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        await kit.CreateReadingAgentAsync("a7");
        var before = File.ReadAllText(kit.Layout.AgentRecordFile("a7"));
        var asked = 0;
        var git = new InterceptingGitClient(kit.Harness.GitClient)
        {
            BeforeEveryCall = () =>
            {
                asked++;
                throw new InvalidOperationException("git was asked before the agent with no worktree was refused.");
            },
        };
        var agents = kit.Harness.Agents(kit.Harness.FileSystem, kit.Harness.AnchorRegistryService, git);

        var refused = verb switch
        {
            "seed" => await agents.SeedAsync(kit.Main, OrchestrationKit.Orchestrator, "a7", empty: false, force: false, Token),
            "refresh" => await agents.RefreshAsync(kit.Main, OrchestrationKit.Orchestrator, "a7", new RefreshRequest(), apply: false, Token, json: false),
            "rebase" => await agents.RebaseAsync(kit.Main, OrchestrationKit.Orchestrator, "a7", new RebaseRequest(), apply: false, Token, json: false),
            _ => await agents.FoldAsync(kit.Main, OrchestrationKit.Orchestrator, "a7", new FoldAllowances(), apply: false, Token, json: false),
        };

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Equal(
            "Agent 'a7' of 'o1' has no worktree: it reads the main tree, and there is nothing of it to seed, refresh, rebase or fold.",
            refused.Message);
        Assert.Equal(0, asked);
        Assert.Equal(before, File.ReadAllText(kit.Layout.AgentRecordFile("a7")));
    }

    /// <summary>
    /// The dry run says what deleting it is: nothing folded, no worktree removed, no session recorded and so no transcript
    /// looked for. --discard-uncommitted and the allowances have nothing to act on, and it says so, line by line - and the
    /// line that does it then leaves them out, so none of them reads as doing something. It writes nothing, its log
    /// included.
    /// </summary>
    [Theory]
    [InlineData("", null)]
    [InlineData("--discard-uncommitted", "--discard-uncommitted discards the changes in an agent's worktree, and this agent has none")]
    [InlineData("--merge", "the allowances let a fold write, and there is nothing of this agent to fold")]
    [InlineData("--new", "the allowances let a fold write, and there is nothing of this agent to fold")]
    public async Task DeletingOne_DryRun_SaysThereIsNothingToFoldOrRemove(string given, string? nothing)
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        await kit.CreateReadingAgentAsync("a7");
        var before = File.ReadAllText(kit.Layout.AgentRecordFile("a7"));

        var dry = await kit.DeleteAsync(
            "a7",
            apply: false,
            given switch
            {
                "--merge" => new FoldAllowances { Merge = ["a.txt"] },
                "--new" => new FoldAllowances { New = ["r1"] },
                _ => new FoldAllowances(),
            },
            discard: given == "--discard-uncommitted");

        Assert.Equal(HarnessExit.Success, dry.ExitCode);
        Assert.Equal(Deleting + (nothing is null ? "pass --apply to do it" : "'dssharness delete-agent o1 a7 --apply' does it"), dry.Message);
        Assert.Equal(
            [$"record {kit.Layout.AgentRecordFile("a7")}", NoSession, .. nothing is null ? Array.Empty<string>() : [nothing]],
            dry.Details);
        Assert.Equal(before, File.ReadAllText(kit.Layout.AgentRecordFile("a7")));
        Assert.Single(File.ReadAllLines(kit.Layout.LogFile("a7")));
    }

    /// <summary>
    /// Deleting one is one write, live to deleted - the path the states already have for an agent whose worktree was
    /// never made - and one line of its log, and its directory stays, as every deleted agent's does. An option with
    /// nothing to act on is said, and stops nothing.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeletingOne_IsOneWrite_LiveToDeleted(bool discard)
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        await kit.CreateReadingAgentAsync("a7");
        var disk = new WritesSeen(kit.Harness.FileSystem);
        var agents = kit.Harness.Agents(disk, kit.Harness.AnchorRegistryService);

        var deleted = await agents.DeleteAsync(kit.Main, OrchestrationKit.Orchestrator, "a7", new FoldAllowances(), apply: true, discard, Token);

        Assert.Equal(HarnessExit.Success, deleted.ExitCode);
        Assert.Equal(
            "deleted agent 'a7' of 'o1', which read the main tree: nothing was folded and no worktree removed, and its directory is kept as its history",
            deleted.Message);
        Assert.Equal(
            [
                $"record {kit.Layout.AgentRecordFile("a7")}",
                NoSession,
                .. discard ? ["--discard-uncommitted discards the changes in an agent's worktree, and this agent has none"] : Array.Empty<string>(),
                $"log {kit.Layout.LogFile("a7")}",
            ],
            deleted.Details);

        // The record once and the log's one line, and nothing else written or removed: a second line of the log is how a
        // deletion logged twice would show.
        Assert.Equal([kit.Layout.AgentRecordFile("a7"), kit.Layout.LogFile("a7")], disk.Written);
        Assert.Empty(disk.Made);
        Assert.Equal(2, File.ReadAllLines(kit.Layout.LogFile("a7")).Length);

        var record = kit.Record("a7");
        Assert.Equal(AgentStates.Deleted, record.State);
        Assert.NotNull(record.DeletedAt);
        Assert.False(record.Abandoned);
        Assert.Null(record.Closing);
        Assert.True(Directory.Exists(kit.Layout.AgentDirectory("a7")));
    }

    /// <summary>
    /// Asked for one document, deleting one answers delete-agent's own, whole: its request sound on the dry run as on the
    /// --apply - a request is null only where the command stopped before it weighed anything - nobody holding anything,
    /// and the same lines the text gives. The facts the answer carries say what the document says.
    /// </summary>
    [Fact]
    public async Task DeletingOne_AnswersItsOwnDocument_SoundOnTheDryRunAsOnTheApply()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        await kit.CreateReadingAgentAsync("a7");
        var record = $"record {kit.Layout.AgentRecordFile("a7")}";

        foreach (var apply in new[] { false, true })
        {
            var answered = await kit.Harness.AgentService.DeleteAsync(
                kit.Main, OrchestrationKit.Orchestrator, "a7", new FoldAllowances(), apply, discardUncommitted: false, Token, json: true);

            var lines = apply ? new JsonArray(record, NoSession, $"log {kit.Layout.LogFile("a7")}") : new JsonArray(record, NoSession);
            var expected = new JsonObject
            {
                ["exitCode"] = HarnessExit.Success,
                ["summary"] = apply
                    ? "deleted agent 'a7' of 'o1', which read the main tree: nothing was folded and no worktree removed, and its directory is kept as its history"
                    : Deleting + "pass --apply to do it",
                ["applied"] = apply,
                ["request"] = "sound",
                ["heldNow"] = new JsonArray(),
                ["heldUnread"] = null,
                ["lines"] = lines,
                ["needs"] = new JsonArray(),
                ["refusals"] = new JsonArray(),
                ["folded"] = null,
                ["planned"] = null,
            };

            Assert.Equal(HarnessExit.Success, answered.ExitCode);
            Assert.True(
                JsonNode.DeepEquals(expected, JsonNode.Parse(Assert.Single(answered.Data))),
                $"{expected.ToJsonString()}{Environment.NewLine}{Assert.Single(answered.Data)}");
            Assert.Equal(RequestStanding.Sound, Assert.IsType<AgentVerbFacts>(answered.Facts).Standing);
        }
    }

    /// <summary>
    /// Its line names where it stands instead of a worktree, and the document gives <c>access</c> with a null
    /// <c>worktree</c> - and it is not among the open agents, while an agent with a worktree's node is what it always
    /// was, with no <c>access</c> of its own.
    /// </summary>
    [Fact]
    public async Task TheListing_NamesWhereItStands_WithNoWorktree_AndLeavesItOutOfTheOpen()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp, parallel: 2);
        await kit.CreateAgentAsync("w1");
        await kit.CreateReadingAgentAsync("a7");

        var listed = await kit.Harness.OrchestratorService.ListAsync(kit.Main, OrchestrationKit.Orchestrator, false, false, json: false, Token);

        Assert.Contains("  a7  live, reads the main tree (no worktree)", listed.Details!);
        Assert.Contains("o1  model model-a, 1 of at most 2 agent(s) open", listed.Details!);

        var json = await kit.Harness.OrchestratorService.ListAsync(kit.Main, OrchestrationKit.Orchestrator, false, false, json: true, Token);
        var agents = JsonNode.Parse(Assert.Single(json.Data!))!["orchestrators"]!.AsArray()[0]!["agents"]!.AsArray();

        var reader = agents.Single(node => (string?)node?["name"] == "a7")!;

        Assert.Equal("read", (string?)reader["access"]);
        Assert.Null((string?)reader["worktree"]);
        Assert.Null((string?)reader["path"]);
        Assert.False((bool?)reader["worktreeExists"]);
        Assert.Null((string?)reader["baseCommit"]);
        Assert.Null((string?)reader["session"]);

        var writer = agents.Single(node => (string?)node?["name"] == "w1")!;

        Assert.Null((string?)writer["access"]);
        Assert.Equal("o1/w1", (string?)writer["worktree"]);
    }

    /// <summary>
    /// A live one does not stand in the way of deleting its orchestrator: it holds nothing but records, keeps no
    /// evidence and no transcript - so nothing --delete-evidence guards - and goes with it, counted as what it is, beside
    /// the agents that were deleted, never as one of them.
    /// </summary>
    [Fact]
    public async Task ALiveOne_DoesNotStandInTheWayOfDeletingItsOrchestrator_AndIsCountedAsWhatItIs()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        await kit.CreateReadingAgentAsync("a7");
        await kit.CreateReadingAgentAsync("a8");
        Assert.True((await kit.DeleteAsync("a8", apply: true)).Succeeded);

        var deleted = await kit.Harness.OrchestratorService.DeleteAsync(kit.Main, OrchestrationKit.Orchestrator, deleteEvidence: false, Token);

        Assert.Equal(HarnessExit.Success, deleted.ExitCode);
        Assert.Equal("deleted orchestrator 'o1'", deleted.Message);
        Assert.Equal(
            [$"removed {kit.Layout.Directory}", "with the history of 1 deleted agent(s) and 1 agent(s) with no worktree, not deleted"],
            deleted.Details);
        Assert.False(Directory.Exists(kit.Layout.Directory));
    }

    /// <summary>
    /// What the steps that ran as an agent and its sessions left in its directory is kept as its evidence and transcripts
    /// are - a step's stored stream is its transcript - whatever its kind and state: deleting its orchestrator without
    /// --delete-evidence is refused, naming each agent that keeps any and changing nothing, and with it they go, counted.
    /// </summary>
    [Fact]
    public async Task ItsStepsAndSessions_AreKeptUntilDeleteEvidence_LiveOrDeleted()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        await kit.CreateReadingAgentAsync("a7");
        await kit.CreateReadingAgentAsync("a8");
        Assert.True((await kit.DeleteAsync("a8", apply: true)).Succeeded);
        var step = Path.Combine(kit.Layout.AgentStepDirectory("a7", "s1"), "stream.jsonl");
        var session = Path.Combine(kit.Layout.AgentSessionDirectory("a8", "c1"), "conversation.json");
        OrchestrationKit.Write(kit.Layout.AgentStepDirectory("a7", "s1"), "stream.jsonl", "{}\n");
        OrchestrationKit.Write(kit.Layout.AgentSessionDirectory("a8", "c1"), "conversation.json", "{}\n");

        var refused = await kit.Harness.OrchestratorService.DeleteAsync(kit.Main, OrchestrationKit.Orchestrator, deleteEvidence: false, Token);

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Equal(
            "Orchestrator 'o1' was not deleted, because it keeps 0 evidence file(s), 0 transcript file(s), 1 step file(s) and 1 session "
                + "file(s) of its agents 'a7', 'a8'. Copy out what you need, or pass --delete-evidence to delete them with it. Nothing was changed.",
            refused.Message);
        Assert.True(File.Exists(step));
        Assert.True(File.Exists(session));
        Assert.Equal(AgentStates.Live, kit.Record("a7").State);

        var deleted = await kit.Harness.OrchestratorService.DeleteAsync(kit.Main, OrchestrationKit.Orchestrator, deleteEvidence: true, Token);

        Assert.Equal(HarnessExit.Success, deleted.ExitCode);
        Assert.Equal(
            [
                $"removed {kit.Layout.Directory}",
                "with the history of 1 deleted agent(s) and 1 agent(s) with no worktree, not deleted",
                "and 0 evidence file(s), 0 transcript file(s), 1 step file(s) and 1 session file(s)",
            ],
            deleted.Details);
        Assert.False(Directory.Exists(kit.Layout.Directory));
    }

    /// <summary>
    /// Where the steps of a run and of an agent are, and a step's handoff: named here and nowhere else, for the
    /// readers that come with the contracts and the definition read.
    /// </summary>
    [Fact]
    public void TheLayoutNamesWhereStepsAndSessionsAndHandoffsAre()
    {
        using var temp = new TempDirectory();
        var layout = OrchestratorLayout.Of(new DssHarness.Core.Repository.HarnessLayout(temp.Path, temp.Path), "r1");

        Assert.Equal(Path.Combine(layout.Directory, "steps"), layout.StepsDirectory);
        Assert.Equal(Path.Combine(layout.StepsDirectory, "s1"), layout.StepDirectory("s1"));
        Assert.Equal(Path.Combine(layout.AgentDirectory("a7"), "steps"), layout.AgentStepsDirectory("a7"));
        Assert.Equal(Path.Combine(layout.AgentStepsDirectory("a7"), "s1"), layout.AgentStepDirectory("a7", "s1"));
        Assert.Equal(Path.Combine(layout.AgentDirectory("a7"), "sessions"), layout.AgentSessionsDirectory("a7"));
        Assert.Equal(Path.Combine(layout.AgentSessionsDirectory("a7"), "conv1"), layout.AgentSessionDirectory("a7", "conv1"));
        Assert.Equal(Path.Combine(layout.AgentStepDirectory("a7", "s1"), "handoff.md"), layout.HandoffFile("a7", "s1"));
    }

    /// <summary>The option reaches the service through the command line: one run of the tool makes the engine's kind.</summary>
    [Fact]
    public async Task TheCommandMakesOne_ThroughTheCommandLine()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);

        var created = await CliRunner.RunAsync(
            ["create-agent", OrchestrationKit.Orchestrator, "a7", "--model", "model-b", "--no-worktree"],
            Token,
            workingDirectory: kit.Main);

        Assert.Equal(HarnessExit.Success, created.ExitCode);
        Assert.Equal(AgentRecord.AccessRead, kit.Record("a7").Access);
        Assert.False(Directory.Exists(kit.Worktree("a7")));
        Assert.True(Directory.Exists(kit.Layout.WorkDirectory("a7")));
    }

    /// <summary>
    /// The option's help is the line the engine's design gives it: whose it is - the engine's, of no use typed by hand -
    /// what it makes, and what is never done to it. The session's says it is not given beside it.
    /// </summary>
    [Fact]
    public async Task TheCommandsHelp_SaysTheOptionIsTheEngines()
    {
        var help = await CliRunner.RunAsync(["create-agent", "--help"], Token);
        var text = Prose.Words(help.StandardOutput);

        Assert.Equal(HarnessExit.Success, help.ExitCode);
        Assert.Contains(
            "--no-worktree Used by the orchestrator engine for a step that only reads; of no use typed by hand. Makes an agent that "
                + "has no worktree: it reads the main tree and keeps only records. Nothing of it is seeded, refreshed, rebased or folded.",
            text,
            StringComparison.Ordinal);
        Assert.Contains("Run again, it records only this. Not with --no-worktree: that agent records none.", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The real file system, saying which files the service wrote or removed and which directories it made, in order: what
    /// a test of one write holds the service to.
    /// </summary>
    private sealed class WritesSeen(IFileSystem inner) : PassThroughFileSystem(inner)
    {
        /// <summary>Every file written, replaced, copied to or removed, and every directory removed or moved, in order.</summary>
        public List<string> Written { get; } = [];

        /// <summary>Every directory made, in order.</summary>
        public List<string> Made { get; } = [];

        public override void CreateDirectory(string path)
        {
            Made.Add(path);
            base.CreateDirectory(path);
        }

        public override void WriteAtomic(string path, Action<Stream> write)
        {
            Written.Add(path);
            base.WriteAtomic(path, write);
        }

        public override Task WriteAllBytesAtomicAsync(string path, byte[] contents, CancellationToken cancellationToken = default)
        {
            Written.Add(path);
            return base.WriteAllBytesAtomicAsync(path, contents, cancellationToken);
        }

        public override void CopyFile(string source, string destination, bool overwrite = false)
        {
            Written.Add(destination);
            base.CopyFile(source, destination, overwrite);
        }

        public override void ReplaceFile(string source, string destination)
        {
            Written.Add(destination);
            base.ReplaceFile(source, destination);
        }

        public override void DeleteFile(string path)
        {
            Written.Add(path);
            base.DeleteFile(path);
        }

        public override void DeleteDirectory(string path)
        {
            Written.Add(path);
            base.DeleteDirectory(path);
        }

        public override void MoveDirectory(string source, string destination)
        {
            Written.Add(source);
            base.MoveDirectory(source, destination);
        }
    }
}
