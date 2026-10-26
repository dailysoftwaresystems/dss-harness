using System.Text.Json.Nodes;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Git;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Orchestration.Flow;
using DssHarness.Core.Results;
using DssHarness.Core.Schemas;
using NSubstitute;

namespace DssHarness.Tests;

/// <summary>
/// A commit step over a real repository and the run's ledger of its folds: it commits the folds upstream of it and no
/// others, each path held to what its fold left there; with nothing owed it is done and runs no git; it refuses an index
/// that is not empty and a path changed since, staging nothing; a registry two items' folds wrote is taken whole and
/// settled for both; a path git ignores is left out and marked so, and one committed by hand marked taken by HEAD; its
/// message is the template filled or its own; git refusing unstages what it staged; and what an attempt before left
/// staged is settled first.
/// </summary>
public sealed class CommitStepTests
{
    private static readonly WorkflowDefinition Definition = WorkflowFileParser.Parse("workflows/flow/flow.yml", """
        name: flow
        steps:
          - name: commit
            type: commit
            message: checkpoint
        """);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// A commit stages the paths the folds upstream of it changed and no sibling's, commits them once with its message
    /// and a sign-off, and marks its staged set committed and each fold path it took; it answers committed, the commit
    /// and the paths.
    /// </summary>
    [Fact]
    public async Task ACommit_TakesTheFoldsUpstreamOfIt_AndNoSiblings()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var mine = await FoldedAsync(kit, "items[0].fold", ("a.txt", "mine\n"), ("new/c.txt", "new\n"));
        var theirs = await FoldedAsync(kit, "items[1].fold", ("b.txt", "theirs\n"));

        var ended = await Runner(kit).RunAsync(Run(kit, Step(), "items[0].fold"), Token);

        Assert.True(ended.Passed, ended.Error);
        var head = await HeadAsync(kit);
        Assert.Equal($$"""{"committed":true,"sha":"{{head}}","paths":["a.txt","new/c.txt"],"ignored":[]}""", ended.Output!.ToJsonString());
        Assert.Matches(@"\Acheckpoint\n\nSigned-off-by: ", await MessageAsync(kit));
        Assert.Equal([" M b.txt"], await StatusAsync(kit));

        var ledger = kit.Harness.OrchestrationStore.ReadFolds(kit.Layout)!;
        var commit = Assert.Single(ledger.Commits);

        Assert.Equal(("c1", FlowCommitEntry.Committed, head, "commit", 1), (commit.Id, commit.State, commit.Sha, commit.Instance, commit.Attempt));
        Assert.Equal([mine], commit.Folds);
        Assert.All(ledger.Of(mine)!.Paths, path => Assert.Equal("c1", path.Committed));
        Assert.All(ledger.Of(theirs)!.Paths, path => Assert.Null(path.Committed));
        Assert.Equal("checkpoint", File.ReadAllText(kit.Layout.InstanceCommitMessageFile("commit")));
    }

    /// <summary>With nothing upstream owed - no fold, or every one committed - the step is done, committed false, and runs no git at all.</summary>
    [Fact]
    public async Task NothingOwed_IsDone_AndRunsNoGit()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        _ = await FoldedAsync(kit, "items[1].fold", ("b.txt", "theirs\n"));
        var git = Substitute.For<IGitClient>();
        var publisher = Substitute.For<IGitPublisher>();

        var ended = await Runner(kit, git, publisher).RunAsync(Run(kit, Step(), "items[0].fold"), Token);

        Assert.True(ended.Passed, ended.Error);
        Assert.Equal("""{"committed":false,"sha":null,"paths":[],"ignored":[]}""", ended.Output!.ToJsonString());
        Assert.Empty(git.ReceivedCalls());
        Assert.Empty(publisher.ReceivedCalls());
        Assert.Empty(kit.Harness.OrchestrationStore.ReadFolds(kit.Layout)!.Commits);
    }

    /// <summary>
    /// A path changed since its fold - edited, or put back - fails the step 13 naming each, the fold that left it and what
    /// to do, and nothing is staged; an index that holds anything is refused the same way, before any path is read.
    /// </summary>
    [Fact]
    public async Task APathChangedSince_OrAnIndexNotEmpty_IsRefused_AndNothingIsStaged()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        _ = await FoldedAsync(kit, "fold", ("a.txt", "folded\n"), ("b.txt", "folded\n"));
        OrchestrationKit.Write(kit.Main, "a.txt", "edited by hand\n");
        OrchestrationKit.Write(kit.Main, "b.txt", "two\n");

        var changed = await Runner(kit).RunAsync(Run(kit, Step(), "fold"), Token);

        Assert.Equal(HarnessExit.Refused, changed.Exit);
        Assert.Equal(
            "'a.txt' (folded by 'fold'), 'b.txt' (folded by 'fold') have changed since; commit or restore them, then resume. Nothing was staged.",
            changed.Error);
        Assert.Equal([" M a.txt"], await StatusAsync(kit));

        OrchestrationKit.Write(kit.Main, "a.txt", "folded\n");
        OrchestrationKit.Write(kit.Main, "b.txt", "folded\n");
        OrchestrationKit.Write(kit.Main, "mine.txt", "the person's\n");
        await kit.GitAsync(kit.Main, "add", "--", "mine.txt");

        var held = await Runner(kit).RunAsync(Run(kit, Step(), "fold"), Token);

        Assert.Equal(HarnessExit.Refused, held.Exit);
        Assert.Equal("'commit' commits only what the run's folds wrote, and the index holds A  mine.txt: commit or unstage it, then resume. Nothing was staged.", held.Error);
        Assert.Empty(kit.Harness.OrchestrationStore.ReadFolds(kit.Layout)!.Commits);
    }

    /// <summary>
    /// A registry two work items' folds both wrote is one file: staged whole as the newer fold left it, by the commit of
    /// either, and marked committed for both - the other item's commit then has nothing left to take.
    /// </summary>
    [Fact]
    public async Task ARegistryTwoItemsFoldsWrote_IsStagedWhole_AndSettledForBoth()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var first = await FoldedAsync(kit, "items[0].fold", ("rows.md", "row one\n"));
        var second = await FoldedAsync(kit, "items[1].fold", ("rows.md", "row one\nrow two\n"));

        var ended = await Runner(kit).RunAsync(Run(kit, Step(), "items[0].fold"), Token);

        Assert.True(ended.Passed, ended.Error);
        Assert.Equal("row one\nrow two\n", (await kit.Harness.RunGitAsync(kit.Main, ["show", "HEAD:rows.md"], Token)).StandardOutput);

        var ledger = kit.Harness.OrchestrationStore.ReadFolds(kit.Layout)!;

        Assert.Equal([first, second], ledger.Commits[0].Folds);
        Assert.Equal("c1", ledger.Of(second)!.Paths[0].Committed);
        Assert.Empty(FoldLedger.Uncommitted(ledger, differing: null));

        var other = await Runner(kit).RunAsync(Run(kit, Step() with { Name = "other" }, "items[1].fold"), Token);

        Assert.Equal("""{"committed":false,"sha":null,"paths":[],"ignored":[]}""", other.Output!.ToJsonString());
    }

    /// <summary>
    /// A path git ignores is left out of the commit and marked ignored, and one that already holds as HEAD does -
    /// committed by hand - is marked taken by HEAD; neither holds the run unfinished, and with nothing else owed the step
    /// commits nothing.
    /// </summary>
    [Fact]
    public async Task AnIgnoredPath_AndOneCommittedByHand_AreMarked_AndNotCommittedAgain()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var fold = await FoldedAsync(kit, "fold", ("evidence/run.txt", "measured\n"), ("a.txt", "folded\n"));
        await kit.GitAsync(kit.Main, "commit", "--quiet", "-am", "by hand");
        var head = await HeadAsync(kit);

        var ended = await Runner(kit).RunAsync(Run(kit, Step(), "fold"), Token);

        Assert.Equal("""{"committed":false,"sha":null,"paths":[],"ignored":["evidence/run.txt"]}""", ended.Output!.ToJsonString());
        Assert.Equal(head, await HeadAsync(kit));

        var ledger = kit.Harness.OrchestrationStore.ReadFolds(kit.Layout)!;

        Assert.Equal([("evidence/run.txt", null, true), ("a.txt", "c1", (bool?)null)], ledger.Of(fold)!.Paths.Select(path => (path.Path, path.Committed, path.Ignored)));
        Assert.Equal((FlowCommitEntry.Committed, head), (ledger.Commits[0].State, ledger.Commits[0].Sha));
        Assert.Empty(FoldLedger.Uncommitted(ledger, differing: null));
    }

    /// <summary>A deletion a fold made is staged as a removal, and committed.</summary>
    [Fact]
    public async Task ADeletion_IsCommittedAsARemoval()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var ledger = Ledger(kit);
        var id = await ledger.IntendAsync(kit.Layout, kit.Main, "fold", 1, "fold", "a1", new AgentPlanned([], [], [], [], null) { Deleted = ["b.txt"] }, Token);
        File.Delete(Path.Combine(kit.Main, "b.txt"));
        await ledger.SettleAsync(kit.Layout, kit.Main, id, HarnessExit.Success);

        var ended = await Runner(kit).RunAsync(Run(kit, Step(), "fold"), Token);

        Assert.True(ended.Passed, ended.Error);
        Assert.Empty(await StatusAsync(kit));
        Assert.DoesNotContain("b.txt", (await kit.Harness.RunGitAsync(kit.Main, ["ls-files"], Token)).StandardOutput, StringComparison.Ordinal);
    }

    /// <summary>
    /// Where the configuration declares a template, the message is it filled from the step's variables and the defaults,
    /// the step's own message unused; a message that would be none fails the step 12 before anything is staged.
    /// </summary>
    [Fact]
    public async Task TheMessage_IsTheTemplateFilled_AndNoneFailsTheStep12()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        _ = await FoldedAsync(kit, "fold", ("a.txt", "folded\n"));
        Templated(kit.Main);

        var none = await Runner(kit).RunAsync(Run(kit, Step(), "fold"), Token);

        Assert.Equal(HarnessExit.ConfigInvalid, none.Exit);
        Assert.Equal("'commit' gives no value for 'summary', which commit.template requires: add it under the step's variables. Nothing was staged.", none.Error);

        var ended = await Runner(kit).RunAsync(Run(kit, Step() with { Variables = new Dictionary<string, string> { ["Summary"] = "the fold" } }, "fold"), Token);

        Assert.True(ended.Passed, ended.Error);
        Assert.Equal("chore: the fold\n", await MessageAsync(kit));
    }

    /// <summary>
    /// git refusing the commit - a hook that says no - fails the step 20 with git's words, unstages what it staged and marks
    /// the set unstaged, its paths owed still; the next attempt stages them whole and commits.
    /// </summary>
    [Fact]
    public async Task AHookThatRefuses_UnstagesTheSet_AndFailsTheStep20()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var fold = await FoldedAsync(kit, "fold", ("a.txt", "folded\n"));
        var hook = Path.Combine(kit.Main, ".git", "hooks", "pre-commit");
        File.WriteAllText(hook, "#!/bin/sh\necho 'the hook says no' >&2\nexit 1\n");

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(hook, File.GetUnixFileMode(hook) | UnixFileMode.UserExecute);
        }

        var refused = await Runner(kit).RunAsync(Run(kit, Step(), "fold"), Token);

        Assert.Equal(HarnessExit.CommandFailed, refused.Exit);
        Assert.Contains("the hook says no", refused.Error, StringComparison.Ordinal);
        Assert.EndsWith("; what it staged was unstaged: fix what git reported, then resume.", refused.Error, StringComparison.Ordinal);
        Assert.Equal([" M a.txt"], await StatusAsync(kit));
        Assert.Equal(FlowCommitEntry.Unstaged, kit.Harness.OrchestrationStore.ReadFolds(kit.Layout)!.Commits[0].State);

        File.Delete(hook);
        var ended = await Runner(kit).RunAsync(Run(kit, Step(), "fold", attempt: 2), Token);

        Assert.True(ended.Passed, ended.Error);
        var ledger = kit.Harness.OrchestrationStore.ReadFolds(kit.Layout)!;

        Assert.Equal(("c2", FlowCommitEntry.Committed, 2), (ledger.Commits[1].Id, ledger.Commits[1].State, ledger.Commits[1].Attempt));
        Assert.Equal("c2", ledger.Of(fold)!.Paths[0].Committed);
    }

    /// <summary>
    /// A staged set an attempt before left - cut off after its commit was made - is settled as committed where HEAD moved
    /// and holds it, and the step is done with that commit, making none.
    /// </summary>
    [Fact]
    public async Task ASetLeftStaged_IsCommittedWhereHeadMovedAndHoldsIt()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var fold = await FoldedAsync(kit, "fold", ("a.txt", "folded\n"));
        var before = await HeadAsync(kit);
        var id = Ledger(kit).Staging(kit.Layout, "commit", 1, before, [new FlowCommitPath("a.txt", FileContentHash.Of("folded\n"u8))], [fold]);
        await kit.GitAsync(kit.Main, "commit", "--quiet", "-am", "the commit the crash cut off");
        var made = await HeadAsync(kit);

        var ended = await Runner(kit).RunAsync(Run(kit, Step(), "fold", attempt: 2), Token);

        Assert.Equal($$"""{"committed":true,"sha":"{{made}}","paths":["a.txt"],"ignored":[]}""", ended.Output!.ToJsonString());
        Assert.Equal(made, await HeadAsync(kit));

        var ledger = kit.Harness.OrchestrationStore.ReadFolds(kit.Layout)!;

        Assert.Equal((id, FlowCommitEntry.Committed, made), (Assert.Single(ledger.Commits).Id, ledger.Commits[0].State, ledger.Commits[0].Sha));
        Assert.Equal(id, ledger.Of(fold)!.Paths[0].Committed);
    }

    /// <summary>
    /// A staged set an attempt before left with no commit made - HEAD where it was - is unstaged and marked so, and the
    /// attempt stages it again whole and commits it; another instance's set is left as it is.
    /// </summary>
    [Fact]
    public async Task ASetLeftStaged_WithNoCommitMade_IsUnstaged_AndStagedAgainWhole()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var fold = await FoldedAsync(kit, "fold", ("a.txt", "folded\n"));
        var ledger = Ledger(kit);
        var head = await HeadAsync(kit);
        var others = ledger.Staging(kit.Layout, "other", 1, head, [new FlowCommitPath("b.txt", null)], [fold]);
        var left = ledger.Staging(kit.Layout, "commit", 1, head, [new FlowCommitPath("a.txt", FileContentHash.Of("folded\n"u8))], [fold]);
        await kit.GitAsync(kit.Main, "add", "--", "a.txt");

        var ended = await Runner(kit).RunAsync(Run(kit, Step(), "fold", attempt: 2), Token);

        Assert.True(ended.Passed, ended.Error);
        Assert.NotEqual(head, await HeadAsync(kit));

        var file = kit.Harness.OrchestrationStore.ReadFolds(kit.Layout)!;

        Assert.Equal(
            [(others, FlowCommitEntry.Staged), (left, FlowCommitEntry.Unstaged), ("c3", FlowCommitEntry.Committed)],
            file.Commits.Select(commit => (commit.Id, commit.State)));
        Assert.Equal("c3", file.Of(fold)!.Paths[0].Committed);
    }

    /// <summary>
    /// A set an attempt before left staged with HEAD where it was and its paths as HEAD holds them - its paths committed
    /// by hand, the attempt cut off before it marked them so - is no commit of the step's: it is unstaged, the paths are
    /// marked taken by HEAD again, and the step committed nothing.
    /// </summary>
    [Fact]
    public async Task ASetLeftStaged_WithHeadUnmovedAndItsPathsAsHeadHoldsThem_IsNoCommitOfTheSteps()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var fold = await FoldedAsync(kit, "fold", ("a.txt", "folded\n"));
        await kit.GitAsync(kit.Main, "commit", "--quiet", "-am", "by hand");
        var head = await HeadAsync(kit);
        var left = Ledger(kit).Staging(kit.Layout, "commit", 1, head, [new FlowCommitPath("a.txt", FileContentHash.Of("folded\n"u8))], [fold]);

        var ended = await Runner(kit).RunAsync(Run(kit, Step(), "fold", attempt: 2), Token);

        Assert.Equal("""{"committed":false,"sha":null,"paths":[],"ignored":[]}""", ended.Output!.ToJsonString());
        Assert.Equal(
            [(left, FlowCommitEntry.Unstaged, (string?)null), ("c2", FlowCommitEntry.Committed, head)],
            kit.Harness.OrchestrationStore.ReadFolds(kit.Layout)!.Commits.Select(commit => (commit.Id, commit.State, commit.Sha)));
    }

    /// <summary>
    /// Settling a set an attempt before left unstages its own paths alone: what else the index holds is the person's, left
    /// staged, and refused as an index that is not empty.
    /// </summary>
    [Fact]
    public async Task SettlingASetLeftStaged_LeavesThePersonsOwnStagedWorkAlone()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var fold = await FoldedAsync(kit, "fold", ("a.txt", "folded\n"));
        var left = Ledger(kit).Staging(kit.Layout, "commit", 1, await HeadAsync(kit), [new FlowCommitPath("a.txt", FileContentHash.Of("folded\n"u8))], [fold]);
        OrchestrationKit.Write(kit.Main, "mine.txt", "the person's\n");
        await kit.GitAsync(kit.Main, "add", "--", "a.txt", "mine.txt");

        var refused = await Runner(kit).RunAsync(Run(kit, Step(), "fold", attempt: 2), Token);

        Assert.Equal(HarnessExit.Refused, refused.Exit);
        Assert.Equal("'commit' commits only what the run's folds wrote, and the index holds A  mine.txt: commit or unstage it, then resume. Nothing was staged.", refused.Error);
        Assert.Equal([" M a.txt", "A  mine.txt"], await StatusAsync(kit));
        Assert.Equal(FlowCommitEntry.Unstaged, kit.Harness.OrchestrationStore.ReadFolds(kit.Layout)!.Commits.Single(commit => commit.Id == left).State);
    }

    /// <summary>
    /// A path committed by hand after a person changed it holds as HEAD does and not what the folds found there: HEAD took
    /// it, as the refusal's remedy says. One whose fold's change was undone - put back as it was - still refuses, and so
    /// does one edited and not committed.
    /// </summary>
    [Fact]
    public async Task APathCommittedByHandAfterAnEdit_IsTakenByHead_AndOneUndoneStillRefuses()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var fold = await FoldedAsync(kit, "fold", ("a.txt", "folded\n"), ("b.txt", "folded\n"));
        OrchestrationKit.Write(kit.Main, "a.txt", "folded, then edited\n");
        await kit.GitAsync(kit.Main, "commit", "--quiet", "-m", "the edit, by hand", "--", "a.txt");
        OrchestrationKit.Write(kit.Main, "b.txt", "two\n");

        var undone = await Runner(kit).RunAsync(Run(kit, Step(), "fold"), Token);

        Assert.Equal(HarnessExit.Refused, undone.Exit);
        Assert.Equal("'b.txt' (folded by 'fold') has changed since; commit or restore it, then resume. Nothing was staged.", undone.Error);
        Assert.DoesNotContain("a.txt", undone.Error, StringComparison.Ordinal);

        OrchestrationKit.Write(kit.Main, "b.txt", "folded\n");
        var head = await HeadAsync(kit);
        var ended = await Runner(kit).RunAsync(Run(kit, Step(), "fold"), Token);

        Assert.Equal($$"""{"committed":true,"sha":"{{await HeadAsync(kit)}}","paths":["b.txt"],"ignored":[]}""", ended.Output!.ToJsonString());

        var ledger = kit.Harness.OrchestrationStore.ReadFolds(kit.Layout)!;

        Assert.Equal((head, FileContentHash.Of("folded, then edited\n"u8)), (ledger.Commits[0].Sha, ledger.Commits[0].Paths[0].Digest));
        Assert.Equal(["c1", "c2"], ledger.Of(fold)!.Paths.Select(path => path.Committed));
    }

    /// <summary>
    /// Run again once it has committed - resumed after the commit, before the engine wrote the step done - the step answers
    /// the commit it made, committed true, and stages nothing.
    /// </summary>
    [Fact]
    public async Task RunAgainOnceItHasCommitted_TheStepAnswersTheCommitItMade()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        _ = await FoldedAsync(kit, "fold", ("a.txt", "folded\n"));

        var first = await Runner(kit).RunAsync(Run(kit, Step(), "fold"), Token);
        var publisher = Substitute.For<IGitPublisher>();
        var again = await Runner(kit, publisher: publisher).RunAsync(Run(kit, Step(), "fold", attempt: 2), Token);

        Assert.True(again.Passed, again.Error);
        Assert.Equal($$"""{"committed":true,"sha":"{{await HeadAsync(kit)}}","paths":["a.txt"],"ignored":[]}""", again.Output!.ToJsonString());
        Assert.Equal(first.Output!.ToJsonString(), again.Output.ToJsonString());
        Assert.Empty(publisher.ReceivedCalls());
    }

    /// <summary>
    /// A path marked ignored is asked of git again at each commit: where git no longer ignores it, its mark is cleared and
    /// it is committed like any other.
    /// </summary>
    [Fact]
    public async Task AnIgnoredMark_IsClearedWhereGitNoLongerIgnoresThePath()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var fold = await FoldedAsync(kit, "fold", ("evidence/run.txt", "measured\n"), ("a.txt", "folded\n"));
        _ = await Runner(kit).RunAsync(Run(kit, Step(), "fold"), Token);
        var gitignore = Path.Combine(kit.Main, ".gitignore");
        File.WriteAllText(gitignore, File.ReadAllText(gitignore).Replace("/evidence/\n", string.Empty, StringComparison.Ordinal));

        var ended = await Runner(kit).RunAsync(Run(kit, Step(), "fold", attempt: 2), Token);

        Assert.True(ended.Passed, ended.Error);
        Assert.Equal($$"""{"committed":true,"sha":"{{await HeadAsync(kit)}}","paths":["evidence/run.txt"],"ignored":[]}""", ended.Output!.ToJsonString());
        Assert.Equal(
            [("evidence/run.txt", "c2", (bool?)null), ("a.txt", "c1", null)],
            kit.Harness.OrchestrationStore.ReadFolds(kit.Layout)!.Of(fold)!.Paths.Select(path => (path.Path, path.Committed, path.Ignored)));
    }

    /// <summary>
    /// The message is filled only where something will be committed: a step that names none, where the configuration
    /// declares no template, is done where each path it owes was committed by hand.
    /// </summary>
    [Fact]
    public async Task WithNothingLeftToStage_NoMessageIsAsked()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        _ = await FoldedAsync(kit, "fold", ("a.txt", "folded\n"));
        await kit.GitAsync(kit.Main, "add", "--", "a.txt");
        await kit.GitAsync(kit.Main, "commit", "--quiet", "-m", "by hand");

        var ended = await Runner(kit).RunAsync(Run(kit, Step() with { Message = null }, "fold"), Token);

        Assert.True(ended.Passed, ended.Error);
        Assert.Equal("""{"committed":false,"sha":null,"paths":[],"ignored":[]}""", ended.Output!.ToJsonString());
    }

    /// <summary>A path git ignores is left out whatever it holds: changed after its fold, it neither refuses the commit nor is staged.</summary>
    [Fact]
    public async Task AnIgnoredPathChangedSince_NeitherRefusesNorIsStaged()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var fold = await FoldedAsync(kit, "fold", ("evidence/run.txt", "measured\n"), ("a.txt", "folded\n"));
        OrchestrationKit.Write(kit.Main, "evidence/run.txt", "measured again\n");

        var ended = await Runner(kit).RunAsync(Run(kit, Step(), "fold"), Token);

        Assert.True(ended.Passed, ended.Error);
        Assert.Equal(["a.txt"], ended.Output!["paths"]!.AsArray().Select(path => path!.GetValue<string>()));
        Assert.True(kit.Harness.OrchestrationStore.ReadFolds(kit.Layout)!.Of(fold)!.Paths[0].Ignored);
    }

    /// <summary>
    /// A staged set an attempt before left where HEAD moved and does not hold it - a person committed something else, and
    /// the set is still in the index - is unstaged and marked so, and staged again whole.
    /// </summary>
    [Fact]
    public async Task ASetLeftStaged_WhereHeadMovedAndDoesNotHoldIt_IsUnstaged_AndStagedAgain()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var fold = await FoldedAsync(kit, "fold", ("a.txt", "folded\n"));
        var left = Ledger(kit).Staging(kit.Layout, "commit", 1, await HeadAsync(kit), [new FlowCommitPath("a.txt", FileContentHash.Of("folded\n"u8))], [fold]);
        OrchestrationKit.Write(kit.Main, "other.txt", "the person's\n");
        await kit.GitAsync(kit.Main, "add", "--", "other.txt");
        await kit.GitAsync(kit.Main, "commit", "--quiet", "-m", "something else", "--", "other.txt");
        await kit.GitAsync(kit.Main, "add", "--", "a.txt");

        var ended = await Runner(kit).RunAsync(Run(kit, Step(), "fold", attempt: 2), Token);

        Assert.True(ended.Passed, ended.Error);

        var ledger = kit.Harness.OrchestrationStore.ReadFolds(kit.Layout)!;

        Assert.Equal([(left, FlowCommitEntry.Unstaged), ("c2", FlowCommitEntry.Committed)], ledger.Commits.Select(commit => (commit.Id, commit.State)));
        Assert.Equal("c2", ledger.Of(fold)!.Paths[0].Committed);
        Assert.Empty(await StatusAsync(kit));
    }

    /// <summary>
    /// git refusing to stage - its index locked - fails the step 20, and where it will not unstage either the set stays
    /// staged, said; the next attempt settles it and commits.
    /// </summary>
    [Fact]
    public async Task AStagingGitRefuses_FailsTheStep20_AndTheNextAttemptSettlesIt()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var fold = await FoldedAsync(kit, "fold", ("a.txt", "folded\n"));
        var locked = Path.Combine(kit.Main, ".git", "index.lock");
        File.WriteAllText(locked, string.Empty);

        var refused = await Runner(kit).RunAsync(Run(kit, Step(), "fold"), Token);

        Assert.Equal(HarnessExit.CommandFailed, refused.Exit);
        Assert.Contains("git could not stage 1 path(s): ", refused.Error, StringComparison.Ordinal);
        Assert.EndsWith("fix what git reported, then resume, which settles it.", refused.Error, StringComparison.Ordinal);
        Assert.Equal(FlowCommitEntry.Staged, kit.Harness.OrchestrationStore.ReadFolds(kit.Layout)!.Commits[0].State);

        File.Delete(locked);
        var ended = await Runner(kit).RunAsync(Run(kit, Step(), "fold", attempt: 2), Token);

        Assert.True(ended.Passed, ended.Error);
        Assert.Equal(
            [FlowCommitEntry.Unstaged, FlowCommitEntry.Committed],
            kit.Harness.OrchestrationStore.ReadFolds(kit.Layout)!.Commits.Select(commit => commit.State));
        Assert.Equal("c2", kit.Harness.OrchestrationStore.ReadFolds(kit.Layout)!.Of(fold)!.Paths[0].Committed);
    }

    /// <summary>On a branch with no commit yet the commit is the branch's first, holding the folds' paths and nothing else.</summary>
    [Fact]
    public async Task OnABranchWithNoCommitYet_TheCommitIsItsFirst()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        await kit.GitAsync(kit.Main, "checkout", "--quiet", "--orphan", "fresh");
        await kit.GitAsync(kit.Main, "rm", "-r", "--cached", "--quiet", ".");
        _ = await FoldedAsync(kit, "fold", ("a.txt", "folded\n"));

        var ended = await Runner(kit).RunAsync(Run(kit, Step(), "fold"), Token);

        Assert.True(ended.Passed, ended.Error);
        Assert.Equal("a.txt\n", (await kit.Harness.RunGitAsync(kit.Main, ["ls-files"], Token)).StandardOutput);
        Assert.Equal("1\n", (await kit.Harness.RunGitAsync(kit.Main, ["rev-list", "--count", "HEAD"], Token)).StandardOutput);
        Assert.Null(kit.Harness.OrchestrationStore.ReadFolds(kit.Layout)!.Commits[0].Head);
    }

    /// <summary>A commit the engine handed no folds it read is the engine's own fault (70), never read as owing nothing.</summary>
    [Fact]
    public async Task ACommitHandedNoFolds_IsTheEnginesFault()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        _ = await FoldedAsync(kit, "fold", ("a.txt", "folded\n"));

        var fault = await Assert.ThrowsAsync<HarnessException>(() => Runner(kit).RunAsync(Run(kit, Step(), "fold") with { Read = new Dictionary<string, FlowValue>() }, Token));

        Assert.Equal((HarnessExit.InternalError, "'commit' was handed no names its folds read where it started"), (fault.ExitCode, fault.Message));
    }

    /// <summary>A fold upstream whose intent nobody settled leaves what it wrote untold: the step fails 13 naming it, and stages nothing.</summary>
    [Fact]
    public async Task AnUpstreamIntentNobodySettled_IsRefused()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        _ = await Ledger(kit).IntendAsync(kit.Layout, kit.Main, "fold", 1, "fold", "a1", new AgentPlanned([], [], [], [], null) { Written = ["a.txt"] }, Token);

        var refused = await Runner(kit).RunAsync(Run(kit, Step(), "fold"), Token);

        Assert.Equal(HarnessExit.Refused, refused.Exit);
        Assert.StartsWith("'commit' commits the folds upstream of it, and 'fold' left a fold's intent nobody settled", refused.Error, StringComparison.Ordinal);
    }

    /// <summary>
    /// A staged set an attempt before left - cut off after its git add, before its git commit - whose paths the tree no
    /// longer holds as it staged them is no commit of the step's, whatever HEAD did since: a person who put the index and
    /// the tree back and took another's commit leaves a HEAD that moved and paths that read clean, and the set is unstaged,
    /// never settled as taken by that commit; the fold's path is owed still, and refused as changed since.
    /// </summary>
    [Fact]
    public async Task ASetLeftStaged_WhoseTreeHoldsNotWhatItStaged_IsNeverSettledByACommitThatMovedHead()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var fold = await FoldedAsync(kit, "fold", ("a.txt", "folded\n"));
        var left = Ledger(kit).Staging(kit.Layout, "commit", 1, await HeadAsync(kit), [new FlowCommitPath("a.txt", FileContentHash.Of("folded\n"u8))], [fold]);
        await kit.GitAsync(kit.Main, "add", "--", "a.txt");

        // The person puts the index and the tree back, then takes another's commit: HEAD moves, and nothing reads changed.
        await kit.GitAsync(kit.Main, "reset", "--quiet", "--hard");
        OrchestrationKit.Write(kit.Main, "other.txt", "another's\n");
        await kit.GitAsync(kit.Main, "add", "--", "other.txt");
        await kit.GitAsync(kit.Main, "commit", "--quiet", "-m", "another's work");
        var foreign = await HeadAsync(kit);

        var ended = await Runner(kit).RunAsync(Run(kit, Step(), "fold", attempt: 2), Token);

        Assert.Equal(
            (false, HarnessExit.Refused, "'a.txt' (folded by 'fold') has changed since; commit or restore it, then resume. Nothing was staged."),
            (ended.Passed, ended.Exit, ended.Error));
        Assert.Equal(foreign, await HeadAsync(kit));

        var ledger = kit.Harness.OrchestrationStore.ReadFolds(kit.Layout)!;

        Assert.Equal([(left, FlowCommitEntry.Unstaged, (string?)null)], ledger.Commits.Select(commit => (commit.Id, commit.State, commit.Sha)));
        Assert.Null(ledger.Of(fold)!.Paths[0].Committed);
    }

    /// <summary>
    /// A path git's status does not look at - marked assume-unchanged or skip-worktree - is held to HEAD by what it holds,
    /// never by status's silence: a fold's change of it is no commit made by hand, and since git add would not stage it,
    /// the step is refused naming each, nothing staged and nothing marked taken.
    /// </summary>
    [Fact]
    public async Task APathStatusDoesNotLookAt_IsHeldToHeadByWhatItHolds_AndRefusedForItsMark()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var fold = await FoldedAsync(kit, "fold", ("a.txt", "folded\n"), ("b.txt", "folded\n"));
        await kit.GitAsync(kit.Main, "update-index", "--assume-unchanged", "--", "a.txt");
        await kit.GitAsync(kit.Main, "update-index", "--skip-worktree", "--", "b.txt");
        var head = await HeadAsync(kit);

        var ended = await Runner(kit).RunAsync(Run(kit, Step(), "fold"), Token);

        Assert.Equal(
            (false, HarnessExit.Refused, "'commit' commits what its folds left at 'a.txt' (assume-unchanged), 'b.txt' (skip-worktree), which git add would not stage: clear each mark with git update-index --no-assume-unchanged -- <path>, or --no-skip-worktree, then resume. Nothing was staged."),
            (ended.Passed, ended.Exit, ended.Error));
        Assert.Equal(head, await HeadAsync(kit));

        var ledger = kit.Harness.OrchestrationStore.ReadFolds(kit.Layout)!;

        Assert.Empty(ledger.Commits);
        Assert.All(ledger.Of(fold)!.Paths, path => Assert.Null(path.Committed));
    }

    /// <summary>
    /// A staged set an attempt before left is settled by what HEAD and the tree hold, never by status's silence: where
    /// another commit moved HEAD and a path of the set is marked assume-unchanged - the tree holding what the set staged,
    /// HEAD not - no commit took the set, and it is unstaged.
    /// </summary>
    [Fact]
    public async Task ASetLeftStaged_IsSettledByWhatHeadHolds_NeverByStatusesSilence()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var fold = await FoldedAsync(kit, "fold", ("a.txt", "folded\n"));
        var left = Ledger(kit).Staging(kit.Layout, "commit", 1, await HeadAsync(kit), [new FlowCommitPath("a.txt", FileContentHash.Of("folded\n"u8))], [fold]);
        OrchestrationKit.Write(kit.Main, "other.txt", "another's\n");
        await kit.GitAsync(kit.Main, "add", "--", "other.txt");
        await kit.GitAsync(kit.Main, "commit", "--quiet", "-m", "another's work", "--", "other.txt");
        await kit.GitAsync(kit.Main, "update-index", "--assume-unchanged", "--", "a.txt");

        var ended = await Runner(kit).RunAsync(Run(kit, Step(), "fold", attempt: 2), Token);

        Assert.Equal(
            (false, HarnessExit.Refused, "'commit' commits what its folds left at 'a.txt' (assume-unchanged), which git add would not stage: clear each mark with git update-index --no-assume-unchanged -- <path>, or --no-skip-worktree, then resume. Nothing was staged."),
            (ended.Passed, ended.Exit, ended.Error));

        var ledger = kit.Harness.OrchestrationStore.ReadFolds(kit.Layout)!;

        Assert.Equal([(left, FlowCommitEntry.Unstaged, (string?)null)], ledger.Commits.Select(commit => (commit.Id, commit.State, commit.Sha)));
        Assert.Null(ledger.Of(fold)!.Paths[0].Committed);
    }

    /// <summary>
    /// A commit's contents are held to its staged set: a pre-commit hook that stages another file sweeps it into the
    /// commit, and the step fails (20) naming what the commit took besides - the commit left as it is for a person, never
    /// undone - and the set stays staged in the ledger with what that commit took, for the next attempt to settle from what
    /// the person did: the commit kept, the next attempt answers it as the step's.
    /// </summary>
    [Fact]
    public async Task ACommitAHookSweptMoreInto_FailsTheStep20_AndIsLeftForAPerson()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var fold = await FoldedAsync(kit, "fold", ("a.txt", "folded\n"));
        OrchestrationKit.Write(kit.Main, "other.txt", "not the fold's\n");
        var hook = Path.Combine(kit.Main, ".git", "hooks", "pre-commit");
        File.WriteAllText(hook, "#!/bin/sh\ngit add other.txt\n");

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(hook, File.GetUnixFileMode(hook) | UnixFileMode.UserExecute);
        }

        var before = await HeadAsync(kit);

        var swept = await Runner(kit).RunAsync(Run(kit, Step(), "fold"), Token);

        var made = await HeadAsync(kit);
        Assert.NotEqual(before, made);
        Assert.Equal(
            (false, HarnessExit.CommandFailed, $"'commit' made {made[..12]}, and it is not what its folds left alone: it took 'other.txt' besides - a hook, or git, made it so. The commit is left as it is: keep it, or undo it with git reset --soft HEAD^, then resume, which settles it."),
            (swept.Passed, swept.Exit, swept.Error));
        Assert.Equal("a.txt\nother.txt\n", (await kit.Harness.RunGitAsync(kit.Main, ["diff-tree", "-r", "--name-only", "--no-commit-id", "HEAD"], Token)).StandardOutput);

        var staged = Assert.Single(kit.Harness.OrchestrationStore.ReadFolds(kit.Layout)!.Commits);

        Assert.Equal((FlowCommitEntry.Staged, (string?)null), (staged.State, staged.Sha));
        Assert.Equal(made, staged.Strayed!.Sha);
        Assert.Equal(["other.txt"], staged.Strayed.Besides);
        Assert.Empty(staged.Strayed.Without);

        File.Delete(hook);
        var kept = await Runner(kit).RunAsync(Run(kit, Step(), "fold", attempt: 2), Token);

        Assert.Equal($$"""{"committed":true,"sha":"{{made}}","paths":["a.txt"],"ignored":[]}""", kept.Output!.ToJsonString());
        Assert.Equal(made, await HeadAsync(kit));
        Assert.Equal("c1", kit.Harness.OrchestrationStore.ReadFolds(kit.Layout)!.Of(fold)!.Paths[0].Committed);
    }

    /// <summary>
    /// A path git ignores is no commit's, and the step says so: it answers each one its folds changed under ignored, so a
    /// step whose every path is ignored never reads as having had nothing to commit.
    /// </summary>
    [Fact]
    public async Task EveryPathGitIgnores_IsAnsweredIgnored_NeverAsNothingToCommit()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        _ = await FoldedAsync(kit, "fold", ("evidence/run.txt", "measured\n"), ("evidence/more.txt", "measured\n"));
        var head = await HeadAsync(kit);

        var ended = await Runner(kit).RunAsync(Run(kit, Step(), "fold"), Token);

        Assert.True(ended.Passed, ended.Error);
        Assert.Equal("""{"committed":false,"sha":null,"paths":[],"ignored":["evidence/run.txt","evidence/more.txt"]}""", ended.Output!.ToJsonString());
        Assert.Equal(head, await HeadAsync(kit));
        Assert.Empty(JsonSchemaSubset.Problems(JsonNode.Parse(WorkflowStepTypes.Find("commit")!.Output!)!, ended.Output));
    }

    /// <summary>
    /// A commit works on the branch the run started on alone: with the main checkout on another - a person checked it out
    /// while the run was paused - or its HEAD detached, the step is refused naming both, nothing staged and nothing settled
    /// of a set an attempt before left; back on the run's branch, it commits.
    /// </summary>
    [Fact]
    public async Task OffTheBranchTheRunStartedOn_ACommitIsRefused_NothingStaged()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var other = (await kit.Harness.RunGitAsync(kit.Main, ["symbolic-ref", "--short", "HEAD"], Token)).StandardOutput.Trim();
        await kit.GitAsync(kit.Main, "branch", "work");
        var fold = await FoldedAsync(kit, "fold", ("a.txt", "folded\n"));
        var head = await HeadAsync(kit);
        var left = Ledger(kit).Staging(kit.Layout, "commit", 1, head, [new FlowCommitPath("a.txt", FileContentHash.Of("folded\n"u8))], [fold]);

        var elsewhere = await Runner(kit).RunAsync(Run(kit, Step(), "fold", branch: "work"), Token);
        await kit.GitAsync(kit.Main, "checkout", "--quiet", "--detach");
        var detached = await Runner(kit).RunAsync(Run(kit, Step(), "fold", branch: "work"), Token);

        Assert.Equal(
            (false, HarnessExit.Refused, $"'commit' works on 'work', the branch the run started on, and the main checkout is on '{other}' now: check out 'work', then resume. Nothing was staged."),
            (elsewhere.Passed, elsewhere.Exit, elsewhere.Error));
        Assert.Equal(
            (false, HarnessExit.Refused, "'commit' works on 'work', the branch the run started on, and the main checkout has its HEAD detached now: check out 'work', then resume. Nothing was staged."),
            (detached.Passed, detached.Exit, detached.Error));
        Assert.Equal(head, await HeadAsync(kit));
        Assert.Equal([" M a.txt"], await StatusAsync(kit));
        Assert.Equal([(left, FlowCommitEntry.Staged)], kit.Harness.OrchestrationStore.ReadFolds(kit.Layout)!.Commits.Select(commit => (commit.Id, commit.State)));

        await kit.GitAsync(kit.Main, "checkout", "--quiet", "work");
        var ended = await Runner(kit).RunAsync(Run(kit, Step(), "fold", attempt: 2, branch: "work"), Token);

        Assert.True(ended.Passed, ended.Error);
        Assert.Equal(await HeadAsync(kit), (await kit.Harness.RunGitAsync(kit.Main, ["rev-parse", "refs/heads/work"], Token)).StandardOutput.Trim());
        Assert.Equal(head, (await kit.Harness.RunGitAsync(kit.Main, ["rev-parse", $"refs/heads/{other}"], Token)).StandardOutput.Trim());
    }

    private static WorkflowStep Step() => new() { Name = "commit", Type = "commit", Message = "checkpoint" };

    /// <summary>
    /// Each git command a commit runs - its staging and its commit - is given the step's time limit where it says one, and
    /// its own where it does not.
    /// </summary>
    [Fact]
    public async Task EveryCommandOfACommit_IsGivenTheStepsLimit_ElseItsOwn()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        _ = await FoldedAsync(kit, "fold", ("a.txt", "folded\n"));
        var limits = new List<TimeSpan?>();
        var publisher = new Limits(kit.Harness.GitPublisher, limits);

        var limited = await Runner(kit, publisher: publisher).RunAsync(Run(kit, Step() with { TimeoutMinutes = 4 }, "fold"), Token);
        _ = await FoldedAsync(kit, "again", ("b.txt", "folded\n"));
        var own = limits.Count;
        var unlimited = await Runner(kit, publisher: publisher).RunAsync(Run(kit, Step() with { Name = "save" }, "again"), Token);

        Assert.True(limited.Passed, limited.Error);
        Assert.True(unlimited.Passed, unlimited.Error);
        Assert.Equal([TimeSpan.FromMinutes(4), TimeSpan.FromMinutes(4)], limits.Take(own));
        Assert.Equal([null, null], limits.Skip(own));
    }

    /// <summary>
    /// git refusing the commit and its unstaging then not finishing in time fails the step with git's refusal and the
    /// unstaging's both (20): the set stays staged for the next attempt to settle, and what git said is never lost.
    /// </summary>
    [Fact]
    public async Task ARefusedCommitWhoseUnstagingIsLate_FailsWithGitsRefusal_TheSetLeftStaged()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        _ = await FoldedAsync(kit, "fold", ("a.txt", "folded\n"));
        var publisher = Substitute.For<IGitPublisher>();
        publisher.CommitAsync(default!, default!, default, default, Token).ReturnsForAnyArgs(Task.FromException<string>(new HarnessException(HarnessExit.CommandFailed, "git did not commit: the hook says no")));
        publisher.UnstageAsync(default!, default!, default, Token).ReturnsForAnyArgs(Task.FromException(new HarnessException(HarnessExit.Incomplete, "git reset did not finish within 15 minutes")));

        var refused = await Runner(kit, publisher: publisher).RunAsync(Run(kit, Step(), "fold"), Token);

        Assert.Equal(
            (HarnessExit.CommandFailed, "'commit': git did not commit: the hook says no; and what it staged could not be unstaged (git reset did not finish within 15 minutes): fix what git reported, then resume, which settles it."),
            (refused.Exit, refused.Error));
        Assert.Equal(FlowCommitEntry.Staged, kit.Harness.OrchestrationStore.ReadFolds(kit.Layout)!.Commits[0].State);
    }

    private static FoldLedger Ledger(OrchestrationKit kit) => new(kit.Harness.OrchestrationStore, kit.Harness.FileSystem, TimeProvider.System);

    /// <summary>A publisher that records the limit each command it is asked for is given, then runs it.</summary>
    private sealed class Limits(IGitPublisher inner, List<TimeSpan?> limits) : IGitPublisher
    {
        public Task StageAsync(string tree, IReadOnlyList<string> present, IReadOnlyList<string> deleted, TimeSpan? limit, CancellationToken cancellationToken)
        {
            limits.Add(limit);
            return inner.StageAsync(tree, present, deleted, limit, cancellationToken);
        }

        public Task<string> CommitAsync(string tree, string messageFile, bool signOff, TimeSpan? limit, CancellationToken cancellationToken)
        {
            limits.Add(limit);
            return inner.CommitAsync(tree, messageFile, signOff, limit, cancellationToken);
        }

        public Task UnstageAsync(string tree, IReadOnlyList<string> paths, TimeSpan? limit, CancellationToken cancellationToken)
        {
            limits.Add(limit);
            return inner.UnstageAsync(tree, paths, limit, cancellationToken);
        }

        public Task<bool> PushAsync(string tree, string remote, string commit, string branch, TimeSpan? limit, CancellationToken cancellationToken)
            => throw new NotSupportedException("a commit pushes nothing");

        public Task<GitPullRequest?> FindPullRequestAsync(string tree, GitPullRequestTarget target, TimeSpan? limit, CancellationToken cancellationToken)
            => throw new NotSupportedException("a commit opens nothing");

        public Task<GitPullRequest> OpenPullRequestAsync(string tree, GitPullRequestTarget target, string title, string bodyFile, bool draft, TimeSpan? limit, CancellationToken cancellationToken)
            => throw new NotSupportedException("a commit opens nothing");
    }

    private static CommitStepRunner Runner(OrchestrationKit kit, IGitClient? git = null, IGitPublisher? publisher = null)
        => new(
            kit.Harness.ContextLoader,
            kit.Harness.Platform,
            kit.Harness.OrchestrationStore,
            Ledger(kit),
            git ?? kit.Harness.GitClient,
            publisher ?? kit.Harness.GitPublisher,
            kit.Harness.FileSystem);

    /// <summary>
    /// One run of <paramref name="step"/> on <paramref name="kit"/>'s orchestrator, the engine having read
    /// <paramref name="folds"/> upstream of it, of a run that started on <paramref name="branch"/> where one is given; its
    /// instance is the step's name.
    /// </summary>
    private static FlowStepRun Run(OrchestrationKit kit, WorkflowStep step, string folds, int attempt = 1, string? branch = null)
        => new(
            step,
            step.Name,
            attempt,
            FlowValue.Missing,
            null,
            null,
            [],
            new NoEvents(),
            Place: new FlowRunPlace(kit.Main, OrchestrationKit.Orchestrator, null, Definition, branch),
            Read: new Dictionary<string, FlowValue> { ["folds"] = FlowValue.Of(new JsonArray(folds)) });

    /// <summary>A fold of <paramref name="instance"/> that wrote each of <paramref name="writes"/> into the main tree, settled applied.</summary>
    private static async Task<string> FoldedAsync(OrchestrationKit kit, string instance, params (string Path, string Text)[] writes)
    {
        var ledger = Ledger(kit);
        var id = await ledger.IntendAsync(kit.Layout, kit.Main, instance, 1, "fold", "a1", new AgentPlanned([], [], [], [], null) { Written = [.. writes.Select(write => write.Path)] }, Token);

        foreach (var (path, text) in writes)
        {
            OrchestrationKit.Write(kit.Main, path, text);
        }

        await ledger.SettleAsync(kit.Layout, kit.Main, id, HarnessExit.Success);
        return id;
    }

    /// <summary>Gives the configuration of <paramref name="main"/> a commit template that requires a summary and gives the type a default, and no sign-off.</summary>
    private static void Templated(string main)
    {
        var path = HarnessFactory.ConfigPath(main);
        var config = JsonNode.Parse(File.ReadAllText(path))!.AsObject();

        config["commit"] = JsonNode.Parse("""
            {
              "signOff": false,
              "template": "{type}: {summary}",
              "variables": { "type": { "default": "chore" }, "summary": { "required": true } }
            }
            """);
        File.WriteAllText(path, config.ToJsonString());
    }

    private static async Task<string> HeadAsync(OrchestrationKit kit) => (await kit.Harness.GitClient.ResolveCommitAsync(kit.Main, "HEAD", Token))!;

    /// <summary>The message of HEAD, whole.</summary>
    private static async Task<string> MessageAsync(OrchestrationKit kit)
        => (await kit.Harness.RunGitAsync(kit.Main, ["log", "-1", "--format=%B"], Token)).StandardOutput.TrimEnd('\n') + "\n";

    /// <summary>The main tree's status, a line each, the run's own records left out.</summary>
    private static async Task<IReadOnlyList<string>> StatusAsync(OrchestrationKit kit)
        => [.. (await kit.Harness.RunGitAsync(kit.Main, ["status", "--porcelain", "--untracked-files=all"], Token)).StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries)];

    private sealed class NoEvents : IFlowRunEvents
    {
        public void LegPlaced(string leg, bool inACopy)
        {
        }

        public void EveryLegPlaced()
        {
        }

        public void LegCopyMade(string leg)
        {
        }

        public void LegEnded(string leg)
        {
        }

        public void Used(long tokens, double usd)
        {
        }

        public void HandoffTaken()
        {
        }
    }
}
