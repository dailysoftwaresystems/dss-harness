using System.Text.Json.Nodes;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Results;

namespace DssHarness.Tests;

/// <summary>
/// The agent that reads the main tree: a record and its two directories, and nothing in the tree. The engine makes one
/// for a read-only reason step, and no verb that works a worktree works on it.
/// </summary>
public sealed class ReadingAgentTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// Making one is one write and two directories: the record says it reads the main tree and holds none of what a
    /// worktree's record does, no worktree is made, nothing is held, and nothing is handed over. The file says
    /// <c>access</c> only because this is the rarer kind: an agent with a worktree's record is what an older tool reads.
    /// </summary>
    [Fact]
    public async Task MakingOne_IsOneWriteTwoDirectories_AndNoWorktree()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        await kit.CreateReadingAgentAsync("a7", session: "sess1");

        var record = kit.Record("a7");

        Assert.Equal(AgentRecord.AccessRead, record.Access);
        Assert.True(record.Reads);
        Assert.Null(record.Worktree);
        Assert.Null(record.WorktreesRoot);
        Assert.Null(record.Base);
        Assert.Null(record.WorktreePath(new DssHarness.Core.Repository.HarnessLayout(kit.Main, kit.Main)));
        Assert.Equal(AgentStates.Live, record.State);
        Assert.Equal("sess1", record.Session);

        var text = File.ReadAllText(kit.Layout.AgentRecordFile("a7"));

        Assert.Contains("\"access\": \"read\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("worktree", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\"base\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\"reads\"", text, StringComparison.Ordinal);

        Assert.True(Directory.Exists(kit.Layout.WorkDirectory("a7")));
        Assert.True(Directory.Exists(kit.Layout.PlansDirectory("a7")));
        Assert.False(File.Exists(kit.Layout.SeedFile("a7")));
        Assert.False(Directory.Exists(kit.Worktree("a7")));
    }

    /// <summary>
    /// It counts against no limit, and nothing of git is asked for it: beside an orchestrator's last open place it is
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
        Assert.Contains("its limit: a1", refused.Outcome.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("a2", refused.Outcome.Message, StringComparison.Ordinal);
    }

    /// <summary>--empty hands a worktree nothing, and an agent with no worktree has nothing to hand: one or the other.</summary>
    [Fact]
    public async Task MakingOneWithEmpty_IsAUsageError()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);

        var refused = await kit.Harness.AgentService.CreateAsync(
            new AgentCreateRequest(kit.Main, OrchestrationKit.Orchestrator, "a7", "model-b", Empty: true, Session: null, NoWorktree: true), Token);

        Assert.Equal(HarnessExit.UsageError, refused.Outcome.ExitCode);
        Assert.Contains("Give one or the other", refused.Outcome.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(kit.Layout.AgentRecordFile("a7")));
    }

    /// <summary>
    /// Asked again with the option, it is as today: the session is recorded and nothing else changes; the model must be
    /// the same, and a worktree agent is another agent whichever option names it.
    /// </summary>
    [Fact]
    public async Task AskedAgain_RecordsTheSession_OrRefusesTheOtherKind()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        await kit.CreateReadingAgentAsync("a7");
        await kit.CreateAgentAsync("w1");

        var again = await kit.Harness.AgentService.CreateAsync(
            new AgentCreateRequest(kit.Main, OrchestrationKit.Orchestrator, "a7", "model-b", Empty: false, Session: "sess2", NoWorktree: true), Token);

        Assert.True(again.Outcome.Succeeded);
        Assert.Equal("sess2", kit.Record("a7").Session);

        var without = await kit.Harness.AgentService.CreateAsync(
            new AgentCreateRequest(kit.Main, OrchestrationKit.Orchestrator, "a7", "model-b", Empty: false, Session: null), Token);

        Assert.Equal(HarnessExit.Refused, without.Outcome.ExitCode);
        Assert.Contains("Agent 'a7' of 'o1' has no worktree: an agent with one is another agent", without.Outcome.Message, StringComparison.Ordinal);

        var with = await kit.Harness.AgentService.CreateAsync(
            new AgentCreateRequest(kit.Main, OrchestrationKit.Orchestrator, "w1", "model-b", Empty: false, Session: null, NoWorktree: true), Token);

        Assert.Equal(HarnessExit.Refused, with.Outcome.ExitCode);
        Assert.Contains("Agent 'w1' of 'o1' has a worktree: an agent with no worktree is another agent", with.Outcome.Message, StringComparison.Ordinal);

        var model = await kit.Harness.AgentService.CreateAsync(
            new AgentCreateRequest(kit.Main, OrchestrationKit.Orchestrator, "a7", "model-c", Empty: false, Session: null, NoWorktree: true), Token);

        Assert.Equal(HarnessExit.Refused, model.Outcome.ExitCode);
        Assert.Contains("created for model 'model-b'", model.Outcome.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("seed-agent", model.Outcome.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every verb that works a worktree refuses it, before anything is asked of git, with the one sentence that is all
    /// four of them.
    /// </summary>
    [Theory]
    [InlineData("seed")]
    [InlineData("refresh")]
    [InlineData("rebase")]
    [InlineData("fold")]
    public async Task EveryVerbThatWorksAWorktree_RefusesIt_BeforeAnythingOfGit(string verb)
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        await kit.CreateReadingAgentAsync("a7");

        var refused = verb switch
        {
            "seed" => await kit.Harness.AgentService.SeedAsync(kit.Main, OrchestrationKit.Orchestrator, "a7", empty: false, force: false, Token),
            "refresh" => await kit.Harness.AgentService.RefreshAsync(kit.Main, OrchestrationKit.Orchestrator, "a7", new RefreshRequest(), apply: false, Token, json: false),
            "rebase" => await kit.Harness.AgentService.RebaseAsync(kit.Main, OrchestrationKit.Orchestrator, "a7", new RebaseRequest(), apply: false, Token, json: false),
            _ => await kit.Harness.AgentService.FoldAsync(kit.Main, OrchestrationKit.Orchestrator, "a7", new FoldAllowances(), apply: false, Token, json: false),
        };

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Contains(
            "Agent 'a7' of 'o1' has no worktree: it reads the main tree, and there is nothing of it to seed, refresh, rebase or fold",
            refused.Message,
            StringComparison.Ordinal);
        Assert.Equal(AgentStates.Live, kit.Record("a7").State);
    }

    /// <summary>
    /// The dry run says what deleting it is: nothing folded, no worktree removed, and the options that act on a
    /// worktree's changes have nothing to act on. Its directory stays, as every deleted agent's does.
    /// </summary>
    [Fact]
    public async Task DeletingOne_DryRun_SaysThereIsNothingToFoldOrRemove()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        await kit.CreateReadingAgentAsync("a7");

        var withDiscard = await kit.DeleteAsync("a7", apply: false, discard: true);

        Assert.True(withDiscard.Succeeded, OrchestrationKit.Describe(withDiscard));
        Assert.Contains("folds nothing and removes no worktree", withDiscard.Message, StringComparison.Ordinal);
        Assert.Contains("pass --apply to do it", withDiscard.Message, StringComparison.Ordinal);
        Assert.Contains("--discard-uncommitted discards the changes in an agent's worktree, and this agent has none", withDiscard.Details!);

        var withAllowances = await kit.DeleteAsync("a7", apply: false, new FoldAllowances { Merge = ["a.txt"] });

        Assert.True(withAllowances.Succeeded, OrchestrationKit.Describe(withAllowances));
        Assert.Contains("the allowances let a fold write, and there is nothing of this agent to fold", withAllowances.Details!);

        var withRows = await kit.DeleteAsync("a7", apply: false, new FoldAllowances { New = ["r1"] });

        Assert.True(withRows.Succeeded, OrchestrationKit.Describe(withRows));
        Assert.Contains("the allowances let a fold write, and there is nothing of this agent to fold", withRows.Details!);
        Assert.Equal(AgentStates.Live, kit.Record("a7").State);
    }

    /// <summary>
    /// Deleting one is one write, live to deleted - the path the states already have for an agent whose worktree was
    /// never made - and its directory stays, as every deleted agent's does.
    /// </summary>
    [Fact]
    public async Task DeletingOne_IsOneWrite_LiveToDeleted()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        await kit.CreateReadingAgentAsync("a7");

        var deleted = await kit.DeleteAsync("a7", apply: true);

        Assert.True(deleted.Succeeded);
        Assert.Contains("nothing was folded and no worktree removed, and its directory is kept as its history", deleted.Message, StringComparison.Ordinal);
        Assert.Contains($"record {kit.Layout.AgentRecordFile("a7")}", deleted.Details!);
        var record = kit.Record("a7");
        Assert.Equal(AgentStates.Deleted, record.State);
        Assert.NotNull(record.DeletedAt);
        Assert.False(record.Abandoned);
        Assert.Null(record.Closing);
        Assert.True(Directory.Exists(kit.Layout.AgentDirectory("a7")));
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
        await kit.CreateReadingAgentAsync("a7", session: "sess1");

        var listed = await kit.Harness.OrchestratorService.ListAsync(kit.Main, OrchestrationKit.Orchestrator, json: false, Token);

        Assert.Contains("  a7  live, reads the main tree (no worktree), session sess1", listed.Details!);
        Assert.Contains("o1  model model-a, 1 of at most 2 agent(s) open", listed.Details!);

        var json = await kit.Harness.OrchestratorService.ListAsync(kit.Main, OrchestrationKit.Orchestrator, json: true, Token);
        var agents = JsonNode.Parse(Assert.Single(json.Data!))!["orchestrators"]!.AsArray()[0]!["agents"]!.AsArray();

        var reader = agents.Single(node => (string?)node?["name"] == "a7")!;

        Assert.Equal("read", (string?)reader["access"]);
        Assert.Null((string?)reader["worktree"]);
        Assert.Null((string?)reader["path"]);
        Assert.False((bool?)reader["worktreeExists"]);
        Assert.Null((string?)reader["baseCommit"]);

        var writer = agents.Single(node => (string?)node?["name"] == "w1")!;

        Assert.Null((string?)writer["access"]);
        Assert.Equal("o1/w1", (string?)writer["worktree"]);
    }

    /// <summary>
    /// A live one does not stand in the way of deleting its orchestrator: it holds nothing but records, and goes with
    /// it.
    /// </summary>
    [Fact]
    public async Task ALiveOne_DoesNotStandInTheWayOfDeletingItsOrchestrator()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        await kit.CreateReadingAgentAsync("a7");

        var deleted = await kit.Harness.OrchestratorService.DeleteAsync(kit.Main, OrchestrationKit.Orchestrator, deleteEvidence: true, Token);

        Assert.True(deleted.Succeeded);
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

        Assert.Equal(0, created.ExitCode);
        Assert.Equal(AgentRecord.AccessRead, kit.Record("a7").Access);
        Assert.False(Directory.Exists(kit.Worktree("a7")));
        Assert.True(Directory.Exists(kit.Layout.WorkDirectory("a7")));
    }

    /// <summary>The option's help says what it is and whose it is: the engine's, which a person has no use for typing.</summary>
    [Fact]
    public async Task TheCommandsHelp_SaysTheOptionIsTheEngines()
    {
        var help = await CliRunner.RunAsync(["create-agent", "--help"], Token);

        Assert.Equal(0, help.ExitCode);
        Assert.Contains("--no-worktree", help.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("The engine makes one for a read-only reason step", help.StandardOutput, StringComparison.Ordinal);
    }
}
