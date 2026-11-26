using System.Text.Json.Nodes;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Results;
using DssHarness.Core.Worktrees;

namespace DssHarness.Tests;

/// <summary>
/// The agent that only reads: a record, its two directories and a tree of its own, made by the making every agent's tree
/// is made by, at the main checkout's HEAD - brought there again, or to the commit its step names, as each of its steps
/// starts, never folded. The engine makes one for a read-only reason step, and no verb that works a worktree's work works
/// on it.
/// </summary>
public sealed class ReadingAgentTests
{
    /// <summary>What deleting one says of its transcripts: that none is recorded, and that it looks for none.</summary>
    private const string NoSession = "no Claude session is recorded for it - an agent that only reads records none - so no transcript is looked for";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// Making one makes its record, its two directories and its tree: a repository of its own - the kind every agent's
    /// tree is, listed as one - detached at the main checkout's HEAD, holding what the main checkout committed and none of
    /// its uncommitted state, the commit kept alive in the main checkout. The record says it only reads and names its
    /// tree, the root it was made under and the commit, never a session, and nothing is handed over: no seed is written.
    /// The log takes the one line every making takes.
    /// </summary>
    [Fact]
    public async Task MakingOne_MakesItsTreeAtTheMainCheckoutsHead_HandingItNothing()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        OrchestrationKit.Write(kit.Main, "a.txt", "changed in the main tree, not committed\n");
        OrchestrationKit.Write(kit.Main, "loose.txt", "untracked in the main tree\n");
        var head = (await kit.Harness.GitClient.ResolveCommitAsync(kit.Main, "HEAD", Token))!;
        var tree = kit.Worktree("a7");

        var created = await kit.Harness.AgentService.CreateAsync(
            new AgentCreateRequest(kit.Main, OrchestrationKit.Orchestrator, "a7", "model-b", Empty: false, Session: null, ReadsOnly: true), Token);

        Assert.Equal(HarnessExit.Success, created.Outcome.ExitCode);
        Assert.Equal(
            "Agent 'a7' of 'o1' was created to only read: its tree is the main checkout's HEAD, brought there again - or to the commit its step names - as each of its steps starts, "
                + $"and nothing it writes there or in '{kit.Layout.WorkDirectory("a7")}' is ever folded",
            created.Outcome.Message);
        Assert.Equal(
            [
                $"record {kit.Layout.AgentRecordFile("a7")}",
                tree,
                $"base {OrchestrationReports.Base(head)}",
                kit.Layout.WorkDirectory("a7"),
                kit.Layout.PlansDirectory("a7"),
                $"log {kit.Layout.LogFile("a7")}",
            ],
            created.Outcome.Details);
        Assert.Single(File.ReadAllLines(kit.Layout.LogFile("a7")));

        var record = kit.Record("a7");

        Assert.Equal(AgentRecord.AccessRead, record.Access);
        Assert.True(record.Reads);
        Assert.Equal(("o1/a7", AgentRecord.TreeRepository, OrchestrationKit.Settings.Root, head), (record.Worktree, record.Tree, record.WorktreesRoot, record.Base));
        Assert.Null(record.Session);
        Assert.Equal(AgentStates.Live, record.State);

        var text = File.ReadAllText(kit.Layout.AgentRecordFile("a7"));

        Assert.Contains("\"access\": \"read\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\"session\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\"reads\"", text, StringComparison.Ordinal);

        // The tree is the one making's: listed as an agent's repository, at the main checkout's HEAD, kept alive there.
        var listed = Assert.Single(await kit.Harness.WorktreeService.ListAsync(kit.Main, Token));
        Assert.Equal(("o1/a7", WorktreeListing.RepositoryKind, head), (listed.Name, listed.Kind, listed.BaseCommit));
        Assert.Equal(head, await kit.Harness.GitClient.ResolveCommitAsync(tree, "HEAD", Token));
        Assert.Equal(head, await kit.Harness.GitClient.ResolveCommitAsync(kit.Main, WorktreeKeepalive.BaseRef("o1/a7"), Token));

        // What the main checkout committed, and none of what it holds uncommitted.
        Assert.Equal("one\n", OrchestrationKit.Read(tree, "a.txt"));
        Assert.False(File.Exists(Path.Combine(tree, "loose.txt")));
        Assert.False(File.Exists(kit.Layout.SeedFile("a7")));
        Assert.True(Directory.Exists(kit.Layout.WorkDirectory("a7")));
        Assert.True(Directory.Exists(kit.Layout.PlansDirectory("a7")));
    }

    /// <summary>
    /// It counts against no limit, its tree listed or not: beside an orchestrator's last open place it is made, where one
    /// more with a worktree is refused - and that refusal names the worktree agent alone, though git's listing of the
    /// worktrees the limit is counted from holds the reader's tree too.
    /// </summary>
    [Fact]
    public async Task MakingOne_CountsAgainstNoLimit_AndTheOpenAgentsLeaveItsTreeOut()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp, parallel: 1);
        await kit.CreateAgentAsync("a1");

        await kit.CreateReadingAgentAsync("a2");
        await kit.CreateReadingAgentAsync("a3");

        Assert.Equal(["o1/a1", "o1/a2", "o1/a3"], (await kit.Harness.WorktreeService.ListAsync(kit.Main, Token)).Select(listed => listed.Name));

        var refused = await kit.Harness.AgentService.CreateAsync(
            new AgentCreateRequest(kit.Main, OrchestrationKit.Orchestrator, "a4", "model-b", Empty: false, Session: null), Token);

        Assert.Equal(HarnessExit.Refused, refused.Outcome.ExitCode);
        Assert.Equal(
            "Orchestrator 'o1' already has 1 open agent(s), its limit: a1. Delete one with 'dssharness delete-agent o1 <agent> --apply', "
                + "or raise the limit with 'dssharness create-orchestrator o1 --model model-a --parallel <n>'. Nothing was created.",
            refused.Outcome.Message);
    }

    /// <summary>
    /// --empty hands a worktree nothing of the main tree's uncommitted state, and an agent that only reads is handed none;
    /// --session records the session whose transcripts deleting it keeps, and an agent that only reads records none. Either
    /// beside --reads-only is a usage error, which changes nothing - neither a new agent's directory nor an existing one's
    /// record.
    /// </summary>
    [Theory]
    [InlineData("empty", "--empty hands an agent's worktree nothing of the main tree's uncommitted state, and an agent made with --reads-only is handed none: its tree is the main checkout's HEAD. Give one or the other. Nothing was changed.")]
    [InlineData("session", "--session records the Claude session whose transcripts delete-agent keeps, and an agent made with --reads-only records none, so deleting it looks for none. Give one or the other. Nothing was changed.")]
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
                    ReadsOnly: true),
                Token);

            Assert.Equal(HarnessExit.UsageError, refused.Outcome.ExitCode);
            Assert.Equal(said, refused.Outcome.Message);
            Assert.Null(refused.Outcome.Details);
        }

        Assert.Equal(before, File.ReadAllText(kit.Layout.AgentRecordFile("a7")));
        Assert.Single(File.ReadAllLines(kit.Layout.LogFile("a7")));
        Assert.False(Directory.Exists(kit.Layout.AgentDirectory("a8")));
        Assert.False(Directory.Exists(kit.Worktree("a8")));
    }

    /// <summary>
    /// Asked again with the option and the same model, it is already as asked, since there is nothing of it to record
    /// again; with another model it is refused, saying that create-agent changes nothing of such an agent and naming no
    /// seed-agent; and an agent whose work is folded is another agent whichever option names it. None of them writes its
    /// record.
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
            new AgentCreateRequest(kit.Main, OrchestrationKit.Orchestrator, "a7", "model-b", Empty: false, Session: null, ReadsOnly: true), Token);

        Assert.Equal(HarnessExit.Success, again.Outcome.ExitCode);
        Assert.Equal("agent 'a7' of 'o1' is already as asked", again.Outcome.Message);
        Assert.Equal([$"record {kit.Layout.AgentRecordFile("a7")}"], again.Outcome.Details);

        var without = await kit.Harness.AgentService.CreateAsync(
            new AgentCreateRequest(kit.Main, OrchestrationKit.Orchestrator, "a7", "model-b", Empty: false, Session: null), Token);

        Assert.Equal(HarnessExit.Refused, without.Outcome.ExitCode);
        Assert.Equal("Agent 'a7' of 'o1' only reads: an agent whose work is folded is another agent. Nothing was changed.", without.Outcome.Message);

        var with = await kit.Harness.AgentService.CreateAsync(
            new AgentCreateRequest(kit.Main, OrchestrationKit.Orchestrator, "w1", "model-b", Empty: false, Session: null, ReadsOnly: true), Token);

        Assert.Equal(HarnessExit.Refused, with.Outcome.ExitCode);
        Assert.Equal("Agent 'w1' of 'o1' has a worktree whose work is folded: an agent that only reads is another agent. Nothing was changed.", with.Outcome.Message);

        var model = await kit.Harness.AgentService.CreateAsync(
            new AgentCreateRequest(kit.Main, OrchestrationKit.Orchestrator, "a7", "model-c", Empty: false, Session: null, ReadsOnly: true), Token);

        Assert.Equal(HarnessExit.Refused, model.Outcome.ExitCode);
        Assert.Equal(
            "Agent 'a7' of 'o1' exists, created for model 'model-b'. Run again, create-agent changes nothing of an agent that only reads. "
                + "Nothing was changed.",
            model.Outcome.Message);

        Assert.Equal(before, File.ReadAllText(kit.Layout.AgentRecordFile("a7")));
    }

    /// <summary>
    /// A directory the run itself placed under the agent's name - holding the step records the engine wrote before the
    /// agent was made, and nothing else - is made in: the naming writes the record before the making, and a crash between
    /// the two leaves exactly that. Anything else the directory holds is unknown, and refused as one.
    /// </summary>
    [Fact]
    public async Task ADirectoryTheRunPlaced_UnderAnUnmadeAgent_IsMadeIn()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);

        Directory.CreateDirectory(Path.Combine(kit.Layout.AgentDirectory("a7"), "steps", "plan"));
        File.WriteAllText(Path.Combine(kit.Layout.AgentDirectory("a7"), "steps", "plan", "step.json"), "{}");

        var made = await kit.Harness.AgentService.CreateAsync(
            new AgentCreateRequest(kit.Main, OrchestrationKit.Orchestrator, "a7", "model-b", Empty: false, Session: null, ReadsOnly: true), Token);

        Assert.Equal(HarnessExit.Success, made.Outcome.ExitCode);
        Assert.Equal("live", kit.Record("a7").State);
        Assert.True(kit.Record("a7").Reads);

        Directory.CreateDirectory(kit.Layout.AgentDirectory("a8"));
        File.WriteAllText(Path.Combine(kit.Layout.AgentDirectory("a8"), "unknown.txt"), "not the engine's");

        var refused = await kit.Harness.AgentService.CreateAsync(
            new AgentCreateRequest(kit.Main, OrchestrationKit.Orchestrator, "a8", "model-b", Empty: false, Session: null, ReadsOnly: true), Token);

        Assert.Equal(HarnessExit.Refused, refused.Outcome.ExitCode);
        Assert.Equal(
            $"'{kit.Layout.AgentDirectory("a8")}' is there already, and no record in it names an agent. Nothing was created.",
            refused.Outcome.Message);
        Assert.False(Directory.Exists(kit.Worktree("a8")));
    }

    /// <summary>
    /// Every verb that works a worktree's work refuses it with the one sentence that is all four of them, before git is
    /// asked anything about the agent: the service's own git, which every question about an agent goes through, fails any
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
                throw new InvalidOperationException("git was asked before the agent that only reads was refused.");
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
            "Agent 'a7' of 'o1' only reads: its tree is the main checkout's HEAD, or the commit its step names, as each of its steps starts, and there is nothing of it to seed, "
                + "refresh, rebase or fold.",
            refused.Message);
        Assert.Equal(0, asked);
        Assert.Equal(before, File.ReadAllText(kit.Layout.AgentRecordFile("a7")));
    }

    /// <summary>
    /// Brought to the main checkout's HEAD, its tree is exactly what the main checkout committed then: a tracked file it
    /// changed put back, a file it made, an ignored one it built and a repository it made inside its tree gone, a commit the
    /// main checkout made since there - and the main checkout's uncommitted state not. The record and the keepalive name
    /// the new commit, the log takes a line, and nothing of the main checkout changes.
    /// </summary>
    [Fact]
    public async Task ReadyingIt_BringsItsTreeToTheMainCheckoutsHead_DiscardingWhatItWrote()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        await kit.CreateReadingAgentAsync("a7");
        var tree = kit.Worktree("a7");

        OrchestrationKit.Write(tree, "a.txt", "the agent's scratch edit\n");
        OrchestrationKit.Write(tree, "made.txt", "the agent made this\n");
        OrchestrationKit.Write(tree, Path.Combine("evidence", "built.log"), "ignored, built by the agent\n");
        await kit.GitAsync(Directory.CreateDirectory(Path.Combine(tree, "nested")).FullName, "init", "--quiet");

        OrchestrationKit.Write(kit.Main, "c.txt", "committed since\n");
        await kit.Harness.CommitAllAsync(kit.Main, "since", Token);
        OrchestrationKit.Write(kit.Main, "b.txt", "uncommitted in the main tree\n");
        var head = (await kit.Harness.GitClient.ResolveCommitAsync(kit.Main, "HEAD", Token))!;

        var ready = await kit.Harness.AgentService.ReadyAsync(new AgentReadyRequest(kit.Main, OrchestrationKit.Orchestrator, "a7"), Token);

        Assert.Equal(HarnessExit.Success, ready.Outcome.ExitCode);
        Assert.Equal("brought the tree of agent 'a7' of 'o1' to the main checkout's HEAD, whatever was written in it discarded", ready.Outcome.Message);
        Assert.Equal(
            [tree, $"base {OrchestrationReports.Base(head)}", $"record {kit.Layout.AgentRecordFile("a7")}", $"log {kit.Layout.LogFile("a7")}"],
            ready.Outcome.Details);
        Assert.Equal(new AgentReady("o1", "a7", tree, head, Made: false), ready.Facts);

        Assert.Equal(head, await kit.Harness.GitClient.ResolveCommitAsync(tree, "HEAD", Token));
        var committed = (await kit.Harness.RunGitAsync(kit.Main, ["ls-tree", "-r", "--name-only", "-z", "HEAD"], Token)).StandardOutput
            .Split('\0', StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal).ToList();

        Assert.Contains("c.txt", committed);
        Assert.Equal(committed, TreeFiles(tree));
        Assert.Equal(("one\n", "two\n", "committed since\n"), (OrchestrationKit.Read(tree, "a.txt"), OrchestrationKit.Read(tree, "b.txt"), OrchestrationKit.Read(tree, "c.txt")));
        Assert.Equal(head, kit.Record("a7").Base);
        Assert.Equal(head, await kit.Harness.GitClient.ResolveCommitAsync(kit.Main, WorktreeKeepalive.BaseRef("o1/a7"), Token));
        Assert.Equal(2, File.ReadAllLines(kit.Layout.LogFile("a7")).Length);

        Assert.Equal("uncommitted in the main tree\n", OrchestrationKit.Read(kit.Main, "b.txt"));
    }

    /// <summary>
    /// Readied at a commit asked - one the main checkout has moved on from, named as git reads a commit, by a short id -
    /// its tree is brought to that commit rather than the main checkout's HEAD, as it is brought to HEAD: what it wrote there
    /// is gone, every file as that commit holds it, a file committed since not there; the record and the keepalive name the
    /// commit, the answer says the commit asked and the commit it resolved to, and nothing of the main checkout changes.
    /// </summary>
    [Fact]
    public async Task ReadyingItAtACommitAsked_BringsItsTreeToThatCommit_DiscardingWhatItWrote()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        await kit.CreateReadingAgentAsync("a7");
        var tree = kit.Worktree("a7");
        var start = (await kit.Harness.GitClient.ResolveCommitAsync(kit.Main, "HEAD", Token))!;

        OrchestrationKit.Write(tree, "made.txt", "the agent made this\n");
        OrchestrationKit.Write(kit.Main, "a.txt", "changed since\n");
        OrchestrationKit.Write(kit.Main, "c.txt", "committed since\n");
        await kit.Harness.CommitAllAsync(kit.Main, "since", Token);
        var head = (await kit.Harness.GitClient.ResolveCommitAsync(kit.Main, "HEAD", Token))!;
        var asked = start[..12];

        var ready = await kit.Harness.AgentService.ReadyAsync(new AgentReadyRequest(kit.Main, OrchestrationKit.Orchestrator, "a7", asked), Token);

        Assert.Equal(HarnessExit.Success, ready.Outcome.ExitCode);
        Assert.Equal(
            $"brought the tree of agent 'a7' of 'o1' to {DssHarness.Core.Output.ReportText.Commit(start)}, the commit asked, whatever was written in it discarded",
            ready.Outcome.Message);
        Assert.Equal(
            [tree, $"base {OrchestrationReports.Base(start)}", $"record {kit.Layout.AgentRecordFile("a7")}", $"log {kit.Layout.LogFile("a7")}"],
            ready.Outcome.Details);
        Assert.Equal(new AgentReady("o1", "a7", tree, start, Made: false, Asked: asked), ready.Facts);

        Assert.Equal(start, await kit.Harness.GitClient.ResolveCommitAsync(tree, "HEAD", Token));
        Assert.Equal(("one\n", "two\n"), (OrchestrationKit.Read(tree, "a.txt"), OrchestrationKit.Read(tree, "b.txt")));
        Assert.False(File.Exists(Path.Combine(tree, "c.txt")));
        Assert.False(File.Exists(Path.Combine(tree, "made.txt")));
        Assert.Equal(start, kit.Record("a7").Base);
        Assert.Equal(start, await kit.Harness.GitClient.ResolveCommitAsync(kit.Main, WorktreeKeepalive.BaseRef("o1/a7"), Token));

        // The main checkout is where it was: its HEAD the commit it made since, its files that commit's.
        Assert.Equal(head, await kit.Harness.GitClient.ResolveCommitAsync(kit.Main, "HEAD", Token));
        Assert.Equal("changed since\n", OrchestrationKit.Read(kit.Main, "a.txt"));
    }

    /// <summary>
    /// Its record names the step attempt its tree was last brought for, as the bringing asked - the attempt an answer that
    /// goes on with it reads to tell whether another step's start brought the tree elsewhere since - and a bringing for no
    /// step the engine names leaves the record naming none, so no attempt ever takes that tree for the one it left.
    /// </summary>
    [Fact]
    public async Task ReadyingIt_RecordsTheStepAttemptItsTreeWasBroughtFor_AndNoneWhereItWasBroughtForNone()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        await kit.CreateReadingAgentAsync("a7");

        Assert.Null(kit.Record("a7").ReadiedFor);

        var forAnAttempt = await kit.Harness.AgentService.ReadyAsync(new AgentReadyRequest(kit.Main, OrchestrationKit.Orchestrator, "a7", For: new AgentReadiedFor("audit", 2)), Token);

        Assert.Equal(HarnessExit.Success, forAnAttempt.Outcome.ExitCode);
        Assert.Equal(new AgentReadiedFor("audit", 2), kit.Record("a7").ReadiedFor);

        var forNone = await kit.Harness.AgentService.ReadyAsync(new AgentReadyRequest(kit.Main, OrchestrationKit.Orchestrator, "a7"), Token);

        Assert.Equal(HarnessExit.Success, forNone.Outcome.ExitCode);
        Assert.Null(kit.Record("a7").ReadiedFor);
    }

    /// <summary>
    /// A commit asked that names none the repository holds - an id no object has, a name no ref has, a blank one - is
    /// refused before anything is held, saying what it named: the tree stands as it stood, what the agent wrote there kept,
    /// and the record and the log are as they were. It is never read as the main checkout's HEAD in its place.
    /// </summary>
    [Fact]
    public async Task ReadyingItAtACommitTheRepositoryDoesNotHold_IsRefused_NothingChanged()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        await kit.CreateReadingAgentAsync("a7");
        var tree = kit.Worktree("a7");
        var start = (await kit.Harness.GitClient.ResolveCommitAsync(kit.Main, "HEAD", Token))!;

        OrchestrationKit.Write(tree, "made.txt", "the agent made this\n");
        OrchestrationKit.Write(kit.Main, "c.txt", "committed since\n");
        await kit.Harness.CommitAllAsync(kit.Main, "since", Token);
        var record = File.ReadAllText(kit.Layout.AgentRecordFile("a7"));
        var log = File.ReadAllLines(kit.Layout.LogFile("a7"));

        foreach (var (asked, said) in new[]
        {
            ("0123456789abcdef0123456789abcdef01234567", "'0123456789abcdef0123456789abcdef01234567', the commit asked for the tree of agent 'a7' of 'o1', names no commit of the repository; name one the main checkout's repository holds. Nothing was changed."),
            ("no-such-branch", "'no-such-branch', the commit asked for the tree of agent 'a7' of 'o1', names no commit of the repository; name one the main checkout's repository holds. Nothing was changed."),
            ("  ", "The commit asked for the tree of agent 'a7' of 'o1' is blank, and names no commit; name one the repository holds. Nothing was changed."),
        })
        {
            var refused = await kit.Harness.AgentService.ReadyAsync(new AgentReadyRequest(kit.Main, OrchestrationKit.Orchestrator, "a7", asked), Token);

            Assert.Equal((HarnessExit.Refused, said), (refused.Outcome.ExitCode, refused.Outcome.Message));
            Assert.Null(refused.Facts);
        }

        Assert.Equal(start, await kit.Harness.GitClient.ResolveCommitAsync(tree, "HEAD", Token));
        Assert.Equal("the agent made this\n", OrchestrationKit.Read(tree, "made.txt"));
        Assert.False(File.Exists(Path.Combine(tree, "c.txt")));
        Assert.Equal(record, File.ReadAllText(kit.Layout.AgentRecordFile("a7")));
        Assert.Equal(log, File.ReadAllLines(kit.Layout.LogFile("a7")));
    }

    /// <summary>
    /// A record an older tool wrote - "access": "read" and no tree - is given its tree as it is readied: made by the one
    /// making at the main checkout's HEAD, and the record given the tree's three facts and the commit.
    /// </summary>
    [Fact]
    public async Task ReadyingOneAnOlderToolWrote_MakesItsTree_AndRecordsIt()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        await kit.CreateReadingAgentAsync("a7");
        await kit.Harness.WorktreeService.DeleteAsync(kit.Main, "o1/a7", force: true, deleteEvidence: true, cancellationToken: Token);
        kit.Harness.OrchestrationStore.UpdateAgent(kit.Layout, "a7", current => current with { Worktree = null, Tree = null, WorktreesRoot = null, Base = null });
        var head = (await kit.Harness.GitClient.ResolveCommitAsync(kit.Main, "HEAD", Token))!;

        Assert.False(Directory.Exists(kit.Worktree("a7")));

        var ready = await kit.Harness.AgentService.ReadyAsync(new AgentReadyRequest(kit.Main, OrchestrationKit.Orchestrator, "a7"), Token);

        Assert.Equal(HarnessExit.Success, ready.Outcome.ExitCode);
        Assert.Equal("made the tree of agent 'a7' of 'o1' at the main checkout's HEAD", ready.Outcome.Message);
        Assert.Equal(new AgentReady("o1", "a7", kit.Worktree("a7"), head, Made: true), ready.Facts);
        Assert.Equal(head, await kit.Harness.GitClient.ResolveCommitAsync(kit.Worktree("a7"), "HEAD", Token));

        var record = kit.Record("a7");
        Assert.Equal(("o1/a7", AgentRecord.TreeRepository, OrchestrationKit.Settings.Root, head), (record.Worktree, record.Tree, record.WorktreesRoot, record.Base));
    }

    /// <summary>
    /// A tree that is not there, readied at a commit asked the main checkout has moved on from, is made by the one making
    /// at the main checkout's HEAD and brought on to that commit: it stands there, the record and the keepalive name it.
    /// </summary>
    [Fact]
    public async Task ReadyingOneWithNoTree_AtACommitAsked_MakesItsTreeAndBringsItThere()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        await kit.CreateReadingAgentAsync("a7");
        var start = (await kit.Harness.GitClient.ResolveCommitAsync(kit.Main, "HEAD", Token))!;
        await kit.Harness.WorktreeService.DeleteAsync(kit.Main, "o1/a7", force: true, deleteEvidence: true, cancellationToken: Token);
        kit.Harness.OrchestrationStore.UpdateAgent(kit.Layout, "a7", current => current with { Worktree = null, Tree = null, WorktreesRoot = null, Base = null });
        OrchestrationKit.Write(kit.Main, "c.txt", "committed since\n");
        await kit.Harness.CommitAllAsync(kit.Main, "since", Token);
        var tree = kit.Worktree("a7");

        var ready = await kit.Harness.AgentService.ReadyAsync(new AgentReadyRequest(kit.Main, OrchestrationKit.Orchestrator, "a7", start), Token);

        Assert.Equal(HarnessExit.Success, ready.Outcome.ExitCode);
        Assert.Equal($"made the tree of agent 'a7' of 'o1' at {DssHarness.Core.Output.ReportText.Commit(start)}, the commit asked", ready.Outcome.Message);
        Assert.Equal(new AgentReady("o1", "a7", tree, start, Made: true, Asked: start), ready.Facts);
        Assert.Equal(start, await kit.Harness.GitClient.ResolveCommitAsync(tree, "HEAD", Token));
        Assert.False(File.Exists(Path.Combine(tree, "c.txt")));
        Assert.Equal(("o1/a7", start), (kit.Record("a7").Worktree, kit.Record("a7").Base));
        Assert.Equal(start, await kit.Harness.GitClient.ResolveCommitAsync(kit.Main, WorktreeKeepalive.BaseRef("o1/a7"), Token));
    }

    /// <summary>
    /// Only the tree of a live agent that only reads is brought to the main checkout's HEAD: an agent whose work is folded
    /// is refused, nothing of its worktree touched, and so is whatever stands at a reader's tree path that is not its
    /// repository - a directory with no .git of its own, which git would answer for with the main checkout.
    /// </summary>
    [Fact]
    public async Task ReadyingIt_IsRefused_ForAnAgentWhoseWorkIsFolded_AndOverWhatIsNotItsTree()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("w1");
        OrchestrationKit.Write(worktree, "a.txt", "the builder's work\n");

        var folded = await kit.Harness.AgentService.ReadyAsync(new AgentReadyRequest(kit.Main, OrchestrationKit.Orchestrator, "w1"), Token);

        Assert.Equal(HarnessExit.Refused, folded.Outcome.ExitCode);
        Assert.Equal(
            "Agent 'w1' of 'o1' has a worktree whose work is folded: only the tree of an agent that only reads is brought to the main checkout's HEAD, "
                + "or to the commit asked, whatever was written in it discarded. Nothing was changed.",
            folded.Outcome.Message);
        Assert.Equal("the builder's work\n", OrchestrationKit.Read(worktree, "a.txt"));

        await kit.CreateReadingAgentAsync("a7");
        await kit.Harness.WorktreeService.DeleteAsync(kit.Main, "o1/a7", force: true, deleteEvidence: true, cancellationToken: Token);
        OrchestrationKit.Write(kit.Worktree("a7"), "somebody.txt", "not the agent's\n");

        var husk = await kit.Harness.AgentService.ReadyAsync(new AgentReadyRequest(kit.Main, OrchestrationKit.Orchestrator, "a7"), Token);

        Assert.Equal(HarnessExit.Refused, husk.Outcome.ExitCode);
        Assert.StartsWith($"Agent 'a7' of 'o1': '{kit.Worktree("a7")}' is no worktree - it holds no .git of its own", husk.Outcome.Message, StringComparison.Ordinal);
        Assert.Equal("not the agent's\n", OrchestrationKit.Read(kit.Worktree("a7"), "somebody.txt"));
    }

    /// <summary>
    /// The dry run says what deleting it is: nothing folded, its tree removed, no session recorded and so no transcript
    /// looked for. --discard-uncommitted and the allowances have nothing to act on, and it says so, line by line - and the
    /// line that does it then leaves them out, so none of them reads as doing something. It writes and removes nothing, its
    /// log included.
    /// </summary>
    [Theory]
    [InlineData("", null)]
    [InlineData("--discard-uncommitted", "--discard-uncommitted keeps an agent's changes out of its fold, and nothing of an agent that only reads is ever folded")]
    [InlineData("--merge", "the allowances let a fold write, and there is nothing of this agent to fold")]
    [InlineData("--new", "the allowances let a fold write, and there is nothing of this agent to fold")]
    public async Task DeletingOne_DryRun_SaysItsTreeGoes_AndThereIsNothingToFold(string given, string? nothing)
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
        Assert.Equal(
            $"dry run: deleting agent 'a7' of 'o1' folds nothing - it only reads - removes its tree at '{kit.Worktree("a7")}' with whatever it wrote there, "
                + $"and marks it deleted; {(nothing is null ? "pass --apply to do it" : "'dssharness delete-agent o1 a7 --apply' does it")}",
            dry.Message);
        Assert.Equal(
            [$"record {kit.Layout.AgentRecordFile("a7")}", NoSession, .. nothing is null ? Array.Empty<string>() : [nothing]],
            dry.Details);
        Assert.Equal(before, File.ReadAllText(kit.Layout.AgentRecordFile("a7")));
        Assert.Single(File.ReadAllLines(kit.Layout.LogFile("a7")));
        Assert.True(Directory.Exists(kit.Worktree("a7")));
    }

    /// <summary>
    /// Deleting one removes its tree whole - a tracked file it changed, a file it made and an evidence root it filled with
    /// it - and folds nothing: the main checkout is as it was, the change never written there. Then it is marked deleted,
    /// its directory kept, the commit its tree stood on no longer kept alive, and its log takes one line. An option with
    /// nothing to act on is said, and stops nothing.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeletingOne_RemovesItsTree_AndFoldsNothing(bool discard)
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        await kit.CreateReadingAgentAsync("a7");
        var tree = kit.Worktree("a7");
        OrchestrationKit.Write(tree, "a.txt", "the agent's scratch edit\n");
        OrchestrationKit.Write(tree, "made.txt", "the agent made this\n");
        OrchestrationKit.Write(tree, Path.Combine("evidence", "built.log"), "built by the agent\n");

        var deleted = await kit.DeleteAsync("a7", apply: true, discard: discard);

        Assert.Equal(HarnessExit.Success, deleted.ExitCode);
        Assert.Equal(
            "deleted agent 'a7' of 'o1', which only read: nothing was folded, its tree is gone, and its directory is kept as its history",
            deleted.Message);
        Assert.Equal(
            [
                $"record {kit.Layout.AgentRecordFile("a7")}",
                NoSession,
                .. discard ? ["--discard-uncommitted keeps an agent's changes out of its fold, and nothing of an agent that only reads is ever folded"] : Array.Empty<string>(),
                $"delete-worktree o1/a7: removed worktree 'o1/a7'",
                $"  {tree}",
                "  discarded 2 uncommitted change(s): a.txt, made.txt",
                $"log {kit.Layout.LogFile("a7")}",
            ],
            deleted.Details);

        Assert.False(Directory.Exists(tree));
        Assert.Equal("one\n", OrchestrationKit.Read(kit.Main, "a.txt"));
        Assert.False(File.Exists(Path.Combine(kit.Main, "made.txt")));
        Assert.Null(await kit.Harness.GitClient.ResolveCommitAsync(kit.Main, WorktreeKeepalive.BaseRef("o1/a7"), Token));
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
        var tree = kit.Worktree("a7");

        foreach (var apply in new[] { false, true })
        {
            var answered = await kit.Harness.AgentService.DeleteAsync(
                kit.Main, OrchestrationKit.Orchestrator, "a7", new FoldAllowances(), apply, discardUncommitted: false, Token, json: true);

            var lines = apply
                ? new JsonArray(record, NoSession, "delete-worktree o1/a7: removed worktree 'o1/a7'", $"  {tree}", $"log {kit.Layout.LogFile("a7")}")
                : new JsonArray(record, NoSession);
            var expected = new JsonObject
            {
                ["exitCode"] = HarnessExit.Success,
                ["summary"] = apply
                    ? "deleted agent 'a7' of 'o1', which only read: nothing was folded, its tree is gone, and its directory is kept as its history"
                    : $"dry run: deleting agent 'a7' of 'o1' folds nothing - it only reads - removes its tree at '{tree}' with whatever it wrote there, and marks it deleted; pass --apply to do it",
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
    /// Its tree is a live agent's, as any agent's is: delete-worktree refuses it, naming delete-agent, which removes it with
    /// the agent, and changes nothing - the tree and the record as they were.
    /// </summary>
    [Fact]
    public async Task DeleteWorktree_RefusesItsTree_AsItRefusesAnyLiveAgentsWorktree()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        await kit.CreateReadingAgentAsync("a7");
        var before = File.ReadAllText(kit.Layout.AgentRecordFile("a7"));

        var refused = await kit.Harness.WorktreeService.DeleteAsync(kit.Main, "o1/a7", force: false, deleteEvidence: false, cancellationToken: Token);

        Assert.Equal(HarnessExit.Refused, refused.Outcome.ExitCode);
        Assert.Equal(
            "Worktree 'o1/a7' is the worktree of agent 'a7' of orchestrator 'o1', which is live, and is deleted with the agent: "
                + $"{OrchestrationReports.DeleteAgentLine("o1", "a7", string.Empty)} says what deleting it keeps and removes, and with --apply "
                + "deletes it, its evidence and transcripts kept. Nothing was deleted; --force deletes the worktree anyway, leaving the agent's record naming a "
                + "worktree that is gone.",
            refused.Outcome.Message);
        Assert.True(Directory.Exists(kit.Worktree("a7")));
        Assert.Equal(before, File.ReadAllText(kit.Layout.AgentRecordFile("a7")));
    }

    /// <summary>
    /// Its line names where its tree is and the commit it stands on, and the document gives <c>access</c> with its tree,
    /// a repository of its own - and it is not among the open agents, though its tree is there, while the node of an agent
    /// whose work is folded is what it always was, with no <c>access</c> of its own.
    /// </summary>
    [Fact]
    public async Task TheListing_NamesItsTree_AndLeavesItOutOfTheOpen()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp, parallel: 2);
        await kit.CreateAgentAsync("w1");
        await kit.CreateReadingAgentAsync("a7");
        var head = kit.Record("a7").Base!;

        var listed = await kit.Harness.OrchestratorService.ListAsync(kit.Main, OrchestrationKit.Orchestrator, false, false, json: false, Token);

        Assert.Contains($"  a7  live, only reads, in its tree at '{kit.Worktree("a7")}', base {OrchestrationReports.Base(head)}", listed.Details!);
        Assert.Contains("o1  model model-a, 1 of at most 2 agent(s) open", listed.Details!);

        var json = await kit.Harness.OrchestratorService.ListAsync(kit.Main, OrchestrationKit.Orchestrator, false, false, json: true, Token);
        var orchestrator = JsonNode.Parse(Assert.Single(json.Data!))!["orchestrators"]!.AsArray()[0]!;
        var agents = orchestrator["agents"]!.AsArray();

        Assert.Equal(["w1"], orchestrator["open"]!.AsArray().Select(node => (string?)node));
        Assert.Empty(orchestrator["worktreesWithoutAgent"]!.AsArray());

        var reader = agents.Single(node => (string?)node?["name"] == "a7")!;

        Assert.Equal("read", (string?)reader["access"]);
        Assert.Equal("o1/a7", (string?)reader["worktree"]);
        Assert.Equal(WorktreeListing.RepositoryKind, (string?)reader["kind"]);
        Assert.Equal(kit.Worktree("a7"), (string?)reader["path"]);
        Assert.True((bool?)reader["worktreeExists"]);
        Assert.Equal(head, (string?)reader["baseCommit"]);
        Assert.Null((string?)reader["session"]);

        var writer = agents.Single(node => (string?)node?["name"] == "w1")!;

        Assert.Null((string?)writer["access"]);
        Assert.Equal("o1/w1", (string?)writer["worktree"]);
    }

    /// <summary>
    /// A live one does not stand in the way of deleting its orchestrator: it holds nothing but records and its tree, which
    /// is removed as deleting the agent removes it - its scratch with it, nothing folded - keeps no evidence and no
    /// transcript - so nothing --delete-evidence guards - and goes with it, counted as what it is, beside the agents that
    /// were deleted, never as one of them.
    /// </summary>
    [Fact]
    public async Task ALiveOne_DoesNotStandInTheWayOfDeletingItsOrchestrator_ItsTreeRemoved()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        await kit.CreateReadingAgentAsync("a7");
        await kit.CreateReadingAgentAsync("a8");
        Assert.True((await kit.DeleteAsync("a8", apply: true)).Succeeded);
        OrchestrationKit.Write(kit.Worktree("a7"), "made.txt", "the agent made this\n");

        var deleted = await kit.Harness.OrchestratorService.DeleteAsync(kit.Main, OrchestrationKit.Orchestrator, deleteEvidence: false, Token);

        Assert.Equal(HarnessExit.Success, deleted.ExitCode);
        Assert.Equal("deleted orchestrator 'o1'", deleted.Message);
        Assert.Equal(
            [
                "delete-worktree o1/a7: removed worktree 'o1/a7'",
                $"removed {kit.Layout.Directory}",
                "with the history of 1 deleted agent(s) and 1 agent(s) that only read, not deleted",
            ],
            deleted.Details);
        Assert.False(Directory.Exists(kit.Layout.Directory));
        Assert.False(Directory.Exists(kit.Worktree("a7")));
        Assert.False(File.Exists(Path.Combine(kit.Main, "made.txt")));
        Assert.Empty(await kit.Harness.WorktreeService.ListAsync(kit.Main, Token));
    }

    /// <summary>
    /// What the steps that ran as an agent and its sessions left in its directory is kept as its evidence and transcripts
    /// are - a step's stored stream is its transcript - whatever its kind and state: deleting its orchestrator without
    /// --delete-evidence is refused, naming each agent that keeps any and changing nothing - the reader's tree still there
    /// - and with it they go, counted.
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
        Assert.True(Directory.Exists(kit.Worktree("a7")));
        Assert.Equal(AgentStates.Live, kit.Record("a7").State);

        var deleted = await kit.Harness.OrchestratorService.DeleteAsync(kit.Main, OrchestrationKit.Orchestrator, deleteEvidence: true, Token);

        Assert.Equal(HarnessExit.Success, deleted.ExitCode);
        Assert.Equal(
            [
                "delete-worktree o1/a7: removed worktree 'o1/a7'",
                $"removed {kit.Layout.Directory}",
                "with the history of 1 deleted agent(s) and 1 agent(s) that only read, not deleted",
                "and 0 evidence file(s), 0 transcript file(s), 1 step file(s) and 1 session file(s)",
            ],
            deleted.Details);
        Assert.False(Directory.Exists(kit.Layout.Directory));
        Assert.False(Directory.Exists(kit.Worktree("a7")));
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

    /// <summary>
    /// The option reaches the service through the command line: one run of the tool makes the engine's kind, its tree
    /// with it; the retired one, given alone or beside the new one, is a usage error naming what took its place, nothing
    /// made.
    /// </summary>
    [Fact]
    public async Task TheCommandMakesOne_ThroughTheCommandLine_AndRefusesTheRetiredOption()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);

        var created = await CliRunner.RunAsync(
            ["create-agent", OrchestrationKit.Orchestrator, "a7", "--model", "model-b", "--reads-only"],
            Token,
            workingDirectory: kit.Main);

        Assert.Equal(HarnessExit.Success, created.ExitCode);
        Assert.Equal(AgentRecord.AccessRead, kit.Record("a7").Access);
        Assert.True(Directory.Exists(kit.Worktree("a7")));
        Assert.True(Directory.Exists(kit.Layout.WorkDirectory("a7")));

        foreach (var retired in new[] { new[] { "--no-worktree" }, ["--no-worktree", "--reads-only"] })
        {
            var refused = await CliRunner.RunAsync(
                ["create-agent", OrchestrationKit.Orchestrator, "a8", "--model", "model-b", .. retired],
                Token,
                workingDirectory: kit.Main);

            Assert.Equal(HarnessExit.UsageError, refused.ExitCode);
            Assert.Contains(
                "--no-worktree is retired: every agent has a tree of its own now, and an agent that only reads is made with --reads-only, its tree "
                    + "brought to the main checkout's HEAD, or to the commit its step names, as each of its steps starts. Nothing was changed.",
                Prose.Words(refused.StandardError + refused.StandardOutput),
                StringComparison.Ordinal);
            Assert.False(Directory.Exists(kit.Layout.AgentDirectory("a8")));
            Assert.False(Directory.Exists(kit.Worktree("a8")));
        }

        Assert.Equal(AgentService.RetiredNoWorktree, Prose.Words(AgentService.RetiredNoWorktree));
    }

    /// <summary>
    /// The option's help is the line the engine's design gives it: whose it is - the engine's, of no use typed by hand -
    /// what it makes, and what is never done to it. The session's says it is not given beside it, and the retired option
    /// is not offered.
    /// </summary>
    [Fact]
    public async Task TheCommandsHelp_SaysTheOptionIsTheEngines_AndOffersNoRetiredOne()
    {
        var help = await CliRunner.RunAsync(["create-agent", "--help"], Token);
        var text = Prose.Words(help.StandardOutput);

        Assert.Equal(HarnessExit.Success, help.ExitCode);
        Assert.Contains(
            "--reads-only Used by the orchestrator engine for a step that only reads; of no use typed by hand. Makes an agent that only reads: "
                + "its tree, made as every agent's is, at the main checkout's HEAD and brought there again - or to the commit its step names - as each of its steps starts, whatever it "
                + "wrote there discarded. Nothing of it is seeded, refreshed, rebased or folded.",
            text,
            StringComparison.Ordinal);
        Assert.Contains("Run again, it records only this. Not with --reads-only: that agent records none.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("--no-worktree", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// An agent made while the tree of one that only reads is taken away is made. The engine deletes such an agent once its
    /// work is over apart from the run's own thread, so the run's next step - the shipped flow's notes, after the rounds'
    /// last reader - makes its agent while that tree goes, and the making counts the orchestrator's open agents over every
    /// tree below it, asking git in each. The tree taken away as git is asked about it is no tree, whether it went as its
    /// mark was read or as what it borrows was, and whether git could not be started in it or started and could not read
    /// it: the making goes on, never failed by the deletion.
    /// </summary>
    /// <param name="asked">The command the tree is taken away at.</param>
    /// <param name="started">Whether git had started in it when it went.</param>
    [Theory]
    [InlineData("config", false)]
    [InlineData("config", true)]
    [InlineData("count-objects", false)]
    [InlineData("count-objects", true)]
    public async Task AnAgentMadeWhileAReadersTreeIsTakenAway_IsMade(string asked, bool started)
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        await kit.CreateReadingAgentAsync("a1");
        var taken = kit.Worktree("a1");
        var agents = kit.Harness.Agents(
            kit.Harness.FileSystem,
            kit.Harness.AnchorRegistryService,
            InterceptingGitClient.TakingAway(kit.Harness.GitClient, kit.Harness.FileSystem, taken, asked, started));

        var created = await agents.CreateAsync(kit.Main, OrchestrationKit.Orchestrator, "a2", "model-b", empty: false, session: null, Token);

        Assert.True(created.Succeeded, OrchestrationKit.Describe(created));
        Assert.False(Directory.Exists(taken));
        Assert.True(Directory.Exists(Path.Combine(kit.Worktree("a2"), ".git")));
        Assert.Equal(AgentStates.Live, kit.Harness.OrchestrationStore.ReadAgent(kit.Layout, "a2")!.State);
    }

    /// <summary>Every file a tree holds but git's own, relative to it with forward separators, in ordinal order.</summary>
    private static IReadOnlyList<string> TreeFiles(string tree)
        => [.. Directory.EnumerateFiles(tree, "*", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(tree, file).Replace('\\', '/'))
            .Where(file => !file.StartsWith(".git/", StringComparison.Ordinal) && file != ".git")
            .Order(StringComparer.Ordinal)];
}
