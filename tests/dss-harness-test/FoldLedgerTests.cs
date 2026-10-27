using DssHarness.Core.FileSystem;
using DssHarness.Core.Git;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Orchestration.Flow;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;

namespace DssHarness.Tests;

/// <summary>
/// The run's write-ahead ledger of its folds and what a fold family step keeps in its directory: each path a fold means to
/// write with what the main tree held there before, written before the verb is called, and what it left there after - a
/// crash between the two settled by the next attempt from what the tree holds - every record held strictly, and a run
/// whose ledger holds a fold no commit took counted unfinished.
/// </summary>
public sealed class FoldLedgerTests
{
    private const string Digest = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static readonly DateTimeOffset At = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// An intent names every path the plan writes, merges, removes and every registry file its rows change - each once,
    /// with what the main tree holds there now, none where it holds no file - and the rows it applies, before anything is
    /// written; and the ids are f&lt;n&gt; in the order intended.
    /// </summary>
    [Fact]
    public async Task AnIntent_RecordsWhatEachPathHeldBefore_EveryPathOnce()
    {
        using var temp = new TempDirectory();
        var (harness, layout, ledger) = Ledger(temp);
        File.WriteAllText(Path.Combine(temp.Path, "a.txt"), "before\n");
        Directory.CreateDirectory(Path.Combine(temp.Path, ".plans"));
        File.WriteAllText(Path.Combine(temp.Path, ".plans", "r.md"), "rows\n");
        var planned = new AgentPlanned([], [], ["h.txt"], ["m.txt"], "arms.txt")
        {
            Written = ["a.txt", "new.txt"],
            Deleted = ["gone.txt"],
            Registries = [".plans/r.md", "a.txt"],
            Rows = ["R-1"],
        };

        var first = await ledger.IntendAsync(layout, temp.Path, "fold", 1, "fold", "a1", planned, Token);
        var second = await ledger.IntendAsync(layout, temp.Path, "fold", 2, "fold", "a1", planned with { Written = [] }, Token);

        var entry = harness.OrchestrationStore.ReadFolds(layout)!.Of(first)!;

        Assert.Equal(("f1", "f2"), (first, second));
        Assert.Equal(FlowFoldEntry.Intended, entry.State);
        Assert.Null(entry.SettledAt);
        Assert.Equal(
            [("a.txt", "written"), ("new.txt", "written"), ("m.txt", "merged"), ("arms.txt", "merged"), ("gone.txt", "deleted"), (".plans/r.md", "registry")],
            entry.Paths.Select(path => (path.Path, path.What)));
        Assert.Equal(FileContentHash.Of("before\n"u8), entry.Paths[0].Before);
        Assert.Null(entry.Paths[1].Before);
        Assert.Equal(FileContentHash.Of("rows\n"u8), entry.Paths[^1].Before);
        Assert.All(entry.Paths, path => Assert.Null(path.Changed));
        Assert.Equal(["R-1"], entry.Rows);

        // An intent nobody settled holds what cannot be told, whatever the main tree holds.
        Assert.Equal([first, second], FoldLedger.Uncommitted(harness.OrchestrationStore.ReadFolds(layout), new HashSet<string>(StringComparer.Ordinal)).Select(fold => fold.Id));

        // An intent marks nothing committed, and no commit is written before one stages.
        Assert.All(entry.Paths, path => Assert.Null(path.Committed));
        Assert.Empty(harness.OrchestrationStore.ReadFolds(layout)!.Commits);
    }

    /// <summary>
    /// A settle records what the fold left at each path and whether that is what it held: applied for a fold that went
    /// through, stopped for one that stopped part way; a refusal wrote nothing and reads nothing - it holds nothing a
    /// commit would take.
    /// </summary>
    [Fact]
    public async Task ASettle_RecordsWhatTheFoldLeft_AndARefusalHoldsNothing()
    {
        using var temp = new TempDirectory();
        var (harness, layout, ledger) = Ledger(temp);
        File.WriteAllText(Path.Combine(temp.Path, "a.txt"), "before\n");
        File.WriteAllText(Path.Combine(temp.Path, "gone.txt"), "there\n");
        var planned = new AgentPlanned([], [], [], [], null) { Written = ["a.txt", "same.txt"], Deleted = ["gone.txt"] };
        File.WriteAllText(Path.Combine(temp.Path, "same.txt"), "same\n");

        var applied = await ledger.IntendAsync(layout, temp.Path, "fold", 1, "fold", "a1", planned, Token);
        File.WriteAllText(Path.Combine(temp.Path, "a.txt"), "after\n");
        File.Delete(Path.Combine(temp.Path, "gone.txt"));
        await ledger.SettleAsync(layout, temp.Path, applied, HarnessExit.Success);

        var refused = await ledger.IntendAsync(layout, temp.Path, "fold", 2, "fold", "a1", planned, Token);
        await ledger.SettleAsync(layout, temp.Path, refused, HarnessExit.Refused);

        var stopped = await ledger.IntendAsync(layout, temp.Path, "fold", 3, "fold", "a1", planned with { Deleted = [] }, Token);
        File.WriteAllText(Path.Combine(temp.Path, "a.txt"), "part way\n");
        await ledger.SettleAsync(layout, temp.Path, stopped, HarnessExit.Incomplete);

        var file = harness.OrchestrationStore.ReadFolds(layout)!;
        var first = file.Of(applied)!;

        Assert.Equal("applied", first.State);
        Assert.NotNull(first.SettledAt);
        Assert.Equal((FileContentHash.Of("after\n"u8), true), (first.Paths[0].Left, first.Paths[0].Changed));
        Assert.Equal((FileContentHash.Of("same\n"u8), false), (first.Paths[1].Left, first.Paths[1].Changed));
        Assert.Equal(((string?)null, true), (first.Paths[2].Left, first.Paths[2].Changed));
        Assert.Equal(FlowFoldEntry.Refused, file.Of(refused)!.State);
        Assert.All(file.Of(refused)!.Paths, path => Assert.Null(path.Changed));
        Assert.Equal("stopped", file.Of(stopped)!.State);
        Assert.True(file.Of(stopped)!.Paths[0].Changed);

        // The main tree unread, every fold that changed a path is counted; a refusal changed none.
        Assert.Equal([applied, stopped], FoldLedger.Uncommitted(file, differing: null).Select(entry => entry.Id));
        Assert.True(FoldLedger.OwesAny(file));

        // A path git ignores holds no run unfinished, though no commit took it.
        Assert.False(FoldLedger.OwesAny(file with { Folds = [.. file.Folds.Select(fold => fold with { Paths = [.. fold.Paths.Select(path => path.Changed == true ? path with { Ignored = true } : path)] })] }));

        // Read, a fold holds what a commit would take where a path it changed differs from HEAD there - and nothing where
        // every one is committed, by hand or otherwise, or put back as HEAD holds it.
        Assert.Equal([applied, stopped], FoldLedger.Uncommitted(file, new HashSet<string>(["a.txt"], StringComparer.Ordinal)).Select(entry => entry.Id));
        Assert.Equal([applied], FoldLedger.Uncommitted(file, new HashSet<string>(["gone.txt"], StringComparer.Ordinal)).Select(entry => entry.Id));
        Assert.Empty(FoldLedger.Uncommitted(file, new HashSet<string>(["same.txt", "other.txt"], StringComparer.Ordinal)));
    }

    /// <summary>
    /// A settle records a fold that failed - a code no refusal answers with, which a verb may answer after writing part of
    /// it - as failed, with what the main tree holds at each path: never as refused, which reads nothing and so would hide
    /// what it wrote.
    /// </summary>
    [Theory]
    [InlineData(HarnessExit.CommandFailed)]
    [InlineData(HarnessExit.InternalError)]
    public async Task ASettleOfAFoldThatFailed_ReadsWhatItLeft(int exitCode)
    {
        using var temp = new TempDirectory();
        var (harness, layout, ledger) = Ledger(temp);
        var planned = new AgentPlanned([], [], [], [], null) { Written = ["a.txt"] };

        var failed = await ledger.IntendAsync(layout, temp.Path, "fold", 1, "fold", "a1", planned, Token);
        File.WriteAllText(Path.Combine(temp.Path, "a.txt"), "part of it\n");
        var settled = await ledger.SettleAsync(layout, temp.Path, failed, exitCode);
        var file = harness.OrchestrationStore.ReadFolds(layout)!;

        Assert.Equal(FlowFoldEntry.Failed, settled.State);
        Assert.Equal((FileContentHash.Of("part of it\n"u8), true), (file.Of(failed)!.Paths[0].Left, file.Of(failed)!.Paths[0].Changed));
        Assert.Equal([failed], FoldLedger.Uncommitted(file, differing: null).Select(entry => entry.Id));
        Assert.Equal([failed], FoldLedger.Ungraphed(file).Select(entry => entry.Id));
        Assert.Null(file.Problem());
    }

    /// <summary>
    /// What a commit owes is each path a settled fold upstream of it changed - once, in the order the ledger first names
    /// it - with what the newest settled fold of any step left there, the step that left it, and every fold of any step
    /// that owes it; never a path no upstream fold changed, one a refusal or an intent names, or one a commit of the run
    /// took - one marked ignored is owed still, for git is asked again at each commit, and asked, the mark is set or
    /// cleared; and an intent of an upstream fold nobody settled is named apart.
    /// </summary>
    [Fact]
    public async Task WhatACommitOwes_IsEachPathItsUpstreamFoldsChanged_AsTheNewestFoldLeftIt()
    {
        using var temp = new TempDirectory();
        var (harness, layout, ledger) = Ledger(temp);
        File.WriteAllText(Path.Combine(temp.Path, "same.txt"), "same\n");

        var mine = await FoldedAsync(ledger, layout, temp.Path, "items[0].fold", ("a.txt", "mine\n"), ("r.md", "row one\n"), ("same.txt", "same\n"));
        var theirs = await FoldedAsync(ledger, layout, temp.Path, "items[1].fold", ("b.txt", "theirs\n"), ("r.md", "row one\nrow two\n"));
        var refused = await ledger.IntendAsync(layout, temp.Path, "items[0].fold", 2, "fold", "a1", new AgentPlanned([], [], [], [], null) { Written = ["c.txt"] }, Token);
        await ledger.SettleAsync(layout, temp.Path, refused, HarnessExit.Refused);

        var owed = FoldLedger.Owed(harness.OrchestrationStore.ReadFolds(layout), ["items[0].fold"]);

        Assert.Equal(
            [("a.txt", FileContentHash.Of("mine\n"u8), "items[0].fold"), ("r.md", FileContentHash.Of("row one\nrow two\n"u8), "items[1].fold")],
            owed.Paths.Select(path => (path.Path, path.Digest, path.LeftBy)));
        Assert.Equal([mine], owed.Paths[0].Folds);
        Assert.Equal([mine, theirs], owed.Paths[1].Folds);

        // What the tree held there before the oldest fold that owes it: none for a file a fold made.
        Assert.Equal(((string?)null, (string?)null), (owed.Paths[0].Before, owed.Paths[1].Before));
        Assert.Empty(owed.Unsettled);
        Assert.Empty(FoldLedger.Owed(null, ["items[0].fold"]).Paths);
        Assert.Empty(FoldLedger.Owed(harness.OrchestrationStore.ReadFolds(layout), ["elsewhere"]).Paths);

        // A path a commit of the run took is owed no more - by any fold; one git ignores is owed still, marked so.
        var staged = ledger.Staging(layout, "items[1].commit", 1, Head, [new FlowCommitPath("r.md", owed.Paths[1].Digest)], [mine, theirs]);
        ledger.Committed(layout, staged, Head);
        ledger.Ignored(layout, ["a.txt"], new HashSet<string>(["a.txt"], StringComparer.Ordinal));

        Assert.Equal(["a.txt"], FoldLedger.Owed(harness.OrchestrationStore.ReadFolds(layout), ["items[0].fold"]).Paths.Select(path => path.Path));
        Assert.Equal(["b.txt"], FoldLedger.Owed(harness.OrchestrationStore.ReadFolds(layout), ["items[1].fold"]).Paths.Select(path => path.Path));
        Assert.True(harness.OrchestrationStore.ReadFolds(layout)!.Of(mine)!.Paths[0].Ignored);

        // Asked again where git no longer ignores it, the mark is cleared.
        ledger.Ignored(layout, ["a.txt"], new HashSet<string>(StringComparer.Ordinal));

        Assert.Null(harness.OrchestrationStore.ReadFolds(layout)!.Of(mine)!.Paths[0].Ignored);

        // An intent of an upstream fold nobody settled is named, its paths owed by nobody yet.
        _ = await ledger.IntendAsync(layout, temp.Path, "items[0].fold", 3, "fold", "a1", new AgentPlanned([], [], [], [], null) { Written = ["d.txt"] }, Token);

        Assert.Equal(["f4"], FoldLedger.Owed(harness.OrchestrationStore.ReadFolds(layout), ["items[0].fold"]).Unsettled.Select(fold => fold.Id));
    }

    /// <summary>
    /// A fold that settled after a commit staged its set, writing a path of it again, is not marked taken by that commit:
    /// the commit took what the folds it names left, and the newer fold's path is owed still.
    /// </summary>
    [Fact]
    public async Task AFoldSettledAfterTheSetWasStaged_IsNotMarkedTakenByIt()
    {
        using var temp = new TempDirectory();
        var (harness, layout, ledger) = Ledger(temp);
        var first = await FoldedAsync(ledger, layout, temp.Path, "fold", ("a.txt", "one\n"));
        var staged = ledger.Staging(layout, "commit", 1, Head, [new FlowCommitPath("a.txt", FileContentHash.Of("one\n"u8))], [first]);
        var later = await FoldedAsync(ledger, layout, temp.Path, "again", ("a.txt", "two\n"));

        ledger.Committed(layout, staged, Other);
        var file = harness.OrchestrationStore.ReadFolds(layout)!;

        Assert.Equal(("c1", (string?)null), (file.Of(first)!.Paths[0].Committed, file.Of(later)!.Paths[0].Committed));
        Assert.Equal(["a.txt"], FoldLedger.Owed(file, ["again"]).Paths.Select(path => path.Path));
    }

    /// <summary>
    /// What the main tree holds otherwise than its HEAD is every path git's status names - both of a rename's - each once;
    /// a name that is not UTF-8 names no file this tool can ask about, and is not among them.
    /// </summary>
    [Fact]
    public void WhatDiffers_IsEveryPathTheStatusNames_BothOfARenames()
        => Assert.Equal(
            ["a.txt", "new.txt", "old.txt"],
            FoldLedger.DifferingOf(
            [
                new GitStatusEntry('R', ' ', new GitName("new.txt", IsUtf8: true), new GitName("old.txt", IsUtf8: true)),
                new GitStatusEntry(' ', 'M', new GitName("a.txt", IsUtf8: true), null),
                new GitStatusEntry('A', 'M', new GitName("a.txt", IsUtf8: true), null),
                new GitStatusEntry('?', '?', GitName.FromBytes("caf\u00e9.txt"), null),
            ]).Order(StringComparer.Ordinal));

    /// <summary>
    /// A commit's staged set is written before anything is staged, then marked committed with the commit that took it -
    /// each of its paths marked taken in every fold it names that owes it, and no other path - or unstaged, its paths owed
    /// still; a path git ignores is marked so in every fold that owes it, one a commit took left as it is. Taken or ignored, a path holds a run unfinished
    /// no more, whatever the main tree says; each set is settled once.
    /// </summary>
    [Fact]
    public async Task AStagedSet_IsMarkedCommittedOrUnstaged_AndWhatItTookIsOwedNoMore()
    {
        using var temp = new TempDirectory();
        var (harness, layout, ledger) = Ledger(temp);
        var fold = await FoldedAsync(ledger, layout, temp.Path, "fold", ("a.txt", "a\n"), ("b.txt", "b\n"), ("e.txt", "e\n"));

        var unstaged = ledger.Staging(layout, "commit", 1, null, [new FlowCommitPath("a.txt", FileContentHash.Of("a\n"u8))], [fold]);
        var file = harness.OrchestrationStore.ReadFolds(layout)!;

        Assert.Equal(("c1", FlowCommitEntry.Staged, (string?)null, (DateTimeOffset?)null, At), (unstaged, file.Commits[0].State, file.Commits[0].Head, file.Commits[0].SettledAt, file.Commits[0].StagedAt));
        Assert.Equal([fold], file.Commits[0].Folds);

        ledger.Unstaged(layout, unstaged);
        var committed = ledger.Staging(layout, "commit", 2, Head, [new FlowCommitPath("a.txt", FileContentHash.Of("a\n"u8))], [fold]);
        ledger.Committed(layout, committed, Other);
        ledger.Ignored(layout, ["a.txt", "e.txt", "elsewhere.txt"], new HashSet<string>(["a.txt", "e.txt", "elsewhere.txt"], StringComparer.Ordinal));
        file = harness.OrchestrationStore.ReadFolds(layout)!;

        Assert.Equal((FlowCommitEntry.Unstaged, (string?)null), (file.Commits[0].State, file.Commits[0].Sha));
        Assert.Equal(("c2", FlowCommitEntry.Committed, Other, Head), (file.Commits[1].Id, file.Commits[1].State, file.Commits[1].Sha, file.Commits[1].Head));
        Assert.NotNull(file.Commits[1].SettledAt);
        Assert.Equal([("a.txt", "c2", (bool?)null), ("b.txt", null, null), ("e.txt", null, true)], file.Of(fold)!.Paths.Select(path => (path.Path, path.Committed, path.Ignored)));
        Assert.Null(file.Problem());

        // What is left owed is b.txt alone: a commit by hand of it ends the run's hold, and the main tree unread holds it.
        Assert.Equal([fold], FoldLedger.Uncommitted(file, differing: null).Select(entry => entry.Id));
        Assert.Empty(FoldLedger.Uncommitted(file, new HashSet<string>(["a.txt", "e.txt"], StringComparer.Ordinal)));

        var settledTwice = Assert.Throws<HarnessException>(() => ledger.Committed(layout, committed, Other));

        Assert.Equal((HarnessExit.InternalError, "the run's ledger of its folds holds no staged commit 'c2' to settle"), (settledTwice.ExitCode, settledTwice.Message));
        Assert.Throws<HarnessException>(() => ledger.Unstaged(layout, "c9"));
    }

    /// <summary>
    /// The ledger's commits are read strictly: ids c&lt;n&gt; in the order staged; a state the engine has, settled exactly when
    /// it is no longer staged and naming its commit exactly where it is committed; commits' ids where it names any; a path
    /// once, relative, with a digest or none for a deletion; the folds it settles, each a fold the ledger holds; and a fold
    /// path marked taken by a commit the ledger holds as committed, or ignored, only where its fold changed it - never both.
    /// </summary>
    [Theory]
    [InlineData("misnumbered", "its commit 'c3' is out of order: ids are c<n>, in the order staged")]
    [InlineData("id", "its commit 'x1': 'x1' is not a commit's id")]
    [InlineData("instance", "its commit 'c1': 'a..b' is not an instance id")]
    [InlineData("attempt", "its commit 'c1': its attempt is 0, and the first is 1")]
    [InlineData("state", "its commit 'c1': 'done' is not a state a commit stands in")]
    [InlineData("head", "its commit 'c1': its head, 'HEAD', is not a commit's id")]
    [InlineData("sha", "its commit 'c1': its commit, 'HEAD', is not a commit's id")]
    [InlineData("digest", "its commit 'c1': 'a.txt' has 'xyz', which is not a SHA-256 digest")]
    [InlineData("stagedSettled", "its commit 'c1': it is settled exactly when it is no longer staged")]
    [InlineData("committedNoSha", "its commit 'c1': it names the commit that took its paths exactly where it is committed")]
    [InlineData("unstagedSha", "its commit 'c1': it names the commit that took its paths exactly where it is committed")]
    [InlineData("noPaths", "its commit 'c1': it stages no path")]
    [InlineData("pathTwice", "its commit 'c1': 'a.txt' is named twice")]
    [InlineData("rooted", "its commit 'c1': a committed path, '/a.txt', is not a path relative to the tree, spelt with forward slashes")]
    [InlineData("noFolds", "its commit 'c1': it names the folds its paths settle, by id")]
    [InlineData("foldTwice", "its commit 'c1': it names a fold twice")]
    [InlineData("notAFold", "its commit 'c1': it names the folds its paths settle, by id")]
    [InlineData("unknownFold", "its commit 'c1' names the fold 'f9', which it holds none of")]
    [InlineData("takenByNone", "its fold 'f1' says 'a.txt' was committed by 'c2', which it holds as no commit made")]
    [InlineData("takenByStaged", "its fold 'f1' says 'a.txt' was committed by 'c1', which it holds as no commit made")]
    [InlineData("takenUnchanged", "its fold 'f1': 'a.txt' says a commit took it, or that git ignores it, where the fold changed nothing there")]
    [InlineData("ignoredUnchanged", "its fold 'f1': 'a.txt' says a commit took it, or that git ignores it, where the fold changed nothing there")]
    [InlineData("both", "its fold 'f1': 'a.txt' says both that a commit took it and that git ignores it")]
    [InlineData("notIgnored", "its fold 'f1': 'a.txt' says git does not ignore it: only that it does is written")]
    [InlineData("takenById", "its fold 'f1': 'a.txt' says 'c' took it, which is not a commit's id")]
    public void TheLedgersCommits_AreStrict(string wrong, string problem)
    {
        var path = new FlowFoldPath("a.txt", "written", Digest, Left: null, Changed: true, Committed: "c1");
        var fold = new FlowFoldEntry("f1", "fold", 1, "fold", "a1", "applied", At, [path], [], SettledAt: At);
        var commit = new FlowCommitEntry("c1", "commit", 1, FlowCommitEntry.Committed, Head, [new FlowCommitPath("a.txt", null)], ["f1"], At, At, Other);
        var ledger = new FlowFoldLedgerFile(1, "r1", [fold]) { Commits = [commit] };

        Assert.Null(ledger.Problem());

        var broken = wrong switch
        {
            "misnumbered" => ledger with { Commits = [commit, commit with { Id = "c3" }] },
            "id" => ledger with { Commits = [commit with { Id = "x1" }] },
            "instance" => ledger with { Commits = [commit with { Instance = "a..b" }] },
            "attempt" => ledger with { Commits = [commit with { Attempt = 0 }] },
            "state" => ledger with { Commits = [commit with { State = "done" }] },
            "head" => ledger with { Commits = [commit with { Head = "HEAD" }] },
            "sha" => ledger with { Commits = [commit with { Sha = "HEAD" }] },
            "digest" => ledger with { Commits = [commit with { Paths = [new FlowCommitPath("a.txt", "xyz")] }] },
            "stagedSettled" => ledger with { Folds = [fold with { Paths = [path with { Committed = null }] }], Commits = [commit with { State = FlowCommitEntry.Staged, Sha = null }] },
            "committedNoSha" => ledger with { Commits = [commit with { Sha = null }] },
            "unstagedSha" => ledger with { Folds = [fold with { Paths = [path with { Committed = null }] }], Commits = [commit with { State = FlowCommitEntry.Unstaged }] },
            "noPaths" => ledger with { Commits = [commit with { Paths = [] }] },
            "pathTwice" => ledger with { Commits = [commit with { Paths = [.. commit.Paths, .. commit.Paths] }] },
            "rooted" => ledger with { Commits = [commit with { Paths = [new FlowCommitPath("/a.txt", Digest)] }] },
            "noFolds" => ledger with { Commits = [commit with { Folds = [] }] },
            "foldTwice" => ledger with { Commits = [commit with { Folds = ["f1", "f1"] }] },
            "notAFold" => ledger with { Commits = [commit with { Folds = ["f1", "fx"] }] },
            "unknownFold" => ledger with { Commits = [commit with { Folds = ["f1", "f9"] }] },
            "takenByNone" => ledger with { Folds = [fold with { Paths = [path with { Committed = "c2" }] }] },
            "takenByStaged" => ledger with { Commits = [commit with { State = FlowCommitEntry.Staged, SettledAt = null, Sha = null }] },
            "takenUnchanged" => ledger with { Folds = [fold with { Paths = [path with { Left = Digest, Changed = false }] }] },
            "ignoredUnchanged" => ledger with { Folds = [fold with { Paths = [path with { Left = Digest, Changed = false, Committed = null, Ignored = true }] }] },
            "both" => ledger with { Folds = [fold with { Paths = [path with { Ignored = true }] }] },
            "notIgnored" => ledger with { Folds = [fold with { Paths = [path with { Committed = null, Ignored = false }] }] },
            "takenById" => ledger with { Folds = [fold with { Paths = [path with { Committed = "c" }] }] },
            _ => throw new ArgumentOutOfRangeException(nameof(wrong), wrong, "no such case"),
        };

        Assert.Equal(problem, broken.Problem());
    }

    /// <summary>A ledger written before there were commits reads as one with none, and one with commits reads back whole.</summary>
    [Fact]
    public async Task TheLedgersCommits_ReadBackWhole_AndALedgerWithoutThemReadsAsNone()
    {
        using var temp = new TempDirectory();
        var (harness, layout, ledger) = Ledger(temp);
        var fold = await FoldedAsync(ledger, layout, temp.Path, "fold", ("a.txt", "a\n"));
        var text = File.ReadAllText(layout.FoldsFile);

        File.WriteAllText(layout.FoldsFile, text.Replace(",\n  \"commits\": []", string.Empty, StringComparison.Ordinal));

        Assert.DoesNotContain("commits", File.ReadAllText(layout.FoldsFile), StringComparison.Ordinal);
        Assert.Empty(harness.OrchestrationStore.ReadFolds(layout)!.Commits);

        ledger.Committed(layout, ledger.Staging(layout, "commit", 1, Head, [new FlowCommitPath("a.txt", FileContentHash.Of("a\n"u8)), new FlowCommitPath("gone.txt", null)], [fold]), Other);
        var read = harness.OrchestrationStore.ReadFolds(layout)!;

        Assert.Equal([new FlowCommitPath("a.txt", FileContentHash.Of("a\n"u8)), new FlowCommitPath("gone.txt", null)], read.Commits[0].Paths);
        Assert.Equal(("commit", 1, Head, Other), (read.Commits[0].Instance, read.Commits[0].Attempt, read.Commits[0].Head, read.Commits[0].Sha));
        Assert.Contains("\"digest\": null", File.ReadAllText(layout.FoldsFile), StringComparison.Ordinal);
    }

    /// <summary>
    /// What the run's graph is owed is every settled fold - one that wrote, one that stopped part way, one a crash cut off -
    /// until the graph is said to have taken it; an intent and a refusal owe it nothing.
    /// </summary>
    [Fact]
    public async Task TheFoldsTheGraphIsOwed_AreTheSettledOnes_UntilEachIsTaken()
    {
        using var temp = new TempDirectory();
        var (harness, layout, ledger) = Ledger(temp);
        var planned = new AgentPlanned([], [], [], [], null) { Written = ["a.txt"] };

        var applied = await ledger.IntendAsync(layout, temp.Path, "fold", 1, "fold", "a1", planned, Token);
        File.WriteAllText(Path.Combine(temp.Path, "a.txt"), "after\n");
        await ledger.SettleAsync(layout, temp.Path, applied, HarnessExit.Success);
        var refused = await ledger.IntendAsync(layout, temp.Path, "fold", 2, "fold", "a1", planned, Token);
        await ledger.SettleAsync(layout, temp.Path, refused, HarnessExit.Refused);
        var interrupted = await ledger.IntendAsync(layout, temp.Path, "fold", 3, "fold", "a1", planned, Token);
        _ = await ledger.SettleInterruptedAsync(layout, temp.Path, "fold");
        _ = await ledger.IntendAsync(layout, temp.Path, "other", 1, "fold", "a2", planned, Token);

        Assert.Equal([applied, interrupted], FoldLedger.Ungraphed(harness.OrchestrationStore.ReadFolds(layout)).Select(entry => entry.Id));
        Assert.Equal(["a.txt"], FoldLedger.Changed(FoldLedger.Ungraphed(harness.OrchestrationStore.ReadFolds(layout))));

        ledger.Graphed(layout, [applied]);

        Assert.Equal([interrupted], FoldLedger.Ungraphed(harness.OrchestrationStore.ReadFolds(layout)).Select(entry => entry.Id));
        Assert.True(harness.OrchestrationStore.ReadFolds(layout)!.Of(applied)!.Graphed);
        Assert.Empty(FoldLedger.Ungraphed(null));
    }

    /// <summary>
    /// An intent a crash left - the verb called, nothing recorded after - is settled interrupted by the step's next attempt,
    /// with what the tree holds now: another instance's intent is left as it is.
    /// </summary>
    [Fact]
    public async Task AnIntentACrashLeft_IsSettledInterrupted_WithWhatTheTreeHolds()
    {
        using var temp = new TempDirectory();
        var (harness, layout, ledger) = Ledger(temp);
        var planned = new AgentPlanned([], [], [], [], null) { Written = ["a.txt"] };

        var mine = await ledger.IntendAsync(layout, temp.Path, "fold", 1, "fold", "a1", planned, Token);
        var other = await ledger.IntendAsync(layout, temp.Path, "items[1].fold", 1, "fold", "a2", planned, Token);
        File.WriteAllText(Path.Combine(temp.Path, "a.txt"), "written before the crash\n");

        var settled = await ledger.SettleInterruptedAsync(layout, temp.Path, "fold");
        var file = harness.OrchestrationStore.ReadFolds(layout)!;

        Assert.Single(settled);
        Assert.Equal(FlowFoldEntry.Interrupted, file.Of(mine)!.State);
        Assert.Equal((FileContentHash.Of("written before the crash\n"u8), true), (file.Of(mine)!.Paths[0].Left, file.Of(mine)!.Paths[0].Changed));
        Assert.Equal(FlowFoldEntry.Intended, file.Of(other)!.State);
        Assert.Empty(await ledger.SettleInterruptedAsync(layout, temp.Path, "fold"));
    }

    /// <summary>
    /// What a path holds is read as the file there: a link to a file as the file it leads to; a directory, a link that leads
    /// nowhere and nothing at all hold no file.
    /// </summary>
    [Fact]
    public async Task WhatAPathHolds_IsTheFileThere_ALinkFollowed()
    {
        using var temp = new TempDirectory();
        var (harness, _, _) = Ledger(temp);
        File.WriteAllText(Path.Combine(temp.Path, "a.txt"), "held\n");
        Directory.CreateDirectory(Path.Combine(temp.Path, "dir"));
        TestLinks.OrSkip(() =>
        {
            File.CreateSymbolicLink(Path.Combine(temp.Path, "to-a.txt"), Path.Combine(temp.Path, "a.txt"));
            File.CreateSymbolicLink(Path.Combine(temp.Path, "nowhere.txt"), Path.Combine(temp.Path, "gone.txt"));
        });

        Task<string?> Of(string name) => FileContentHash.OfFileAsync(harness.FileSystem, Path.Combine(temp.Path, name), Token);

        Assert.Equal(FileContentHash.Of("held\n"u8), await Of("a.txt"));
        Assert.Equal(FileContentHash.Of("held\n"u8), await Of("to-a.txt"));
        Assert.Null(await Of("dir"));
        Assert.Null(await Of("nowhere.txt"));
        Assert.Null(await Of("absent.txt"));
    }

    /// <summary>
    /// The ledger is read strictly: ids f&lt;n&gt; in the order intended; settled exactly when it says what became of a
    /// fold; what a path left said only once it was read, and whether it changed true to what it left; a path relative to
    /// the tree; a state, a type and a kind of path the engine has; an agent's name the agents' rule holds.
    /// </summary>
    [Theory]
    [InlineData("misnumbered", "its fold 'f3' is out of order: ids are f<n>, in the order intended")]
    [InlineData("unsettled", "its fold 'f1': it is settled exactly when it says what became of it")]
    [InlineData("readWhileIntended", "its fold 'f1': 'a.txt' says what the fold left exactly where it wrote nothing yet")]
    [InlineData("changedUntrue", "its fold 'f1': 'a.txt' says it changed where what it left is what it held")]
    [InlineData("rooted", "its fold 'f1': a folded path, '/a.txt', is not a path relative to the tree, spelt with forward slashes")]
    [InlineData("state", "its fold 'f1': 'done' is not a state a fold stands in")]
    [InlineData("type", "its fold 'f1': 'refresh' is not a step that folds")]
    [InlineData("what", "its fold 'f1': 'copied' is not what a fold does to 'a.txt'")]
    [InlineData("digest", "its fold 'f1': 'a.txt' has 'x', which is not a SHA-256 digest")]
    [InlineData("twice", "its fold 'f1': 'a.txt' is named twice")]
    [InlineData("readWhileRefused", "its fold 'f1': 'a.txt' says what the fold left exactly where it wrote nothing yet")]
    [InlineData("unreadSettled", "its fold 'f1': 'a.txt' says what the fold left exactly where it was settled")]
    [InlineData("graphedRefused", "its fold 'f1': it says the graph took what it changed where it changed nothing yet, or says it did not")]
    [InlineData("graphedFalse", "its fold 'f1': it says the graph took what it changed where it changed nothing yet, or says it did not")]
    public void TheFoldLedger_IsStrict(string wrong, string problem)
    {
        var path = new FlowFoldPath("a.txt", "written", Digest, Left: null, Changed: true);
        var entry = new FlowFoldEntry("f1", "fold", 1, "fold", "a1", "applied", At, [path], [], SettledAt: At);
        var ledger = new FlowFoldLedgerFile(1, "r1", [entry]);

        Assert.Null(ledger.Problem());

        var broken = wrong switch
        {
            "misnumbered" => ledger with { Folds = [entry, entry with { Id = "f3" }] },
            "unsettled" => ledger with { Folds = [entry with { SettledAt = null }] },
            "readWhileIntended" => ledger with { Folds = [entry with { State = FlowFoldEntry.Intended, SettledAt = null }] },
            "changedUntrue" => ledger with { Folds = [entry with { Paths = [path with { Left = Digest }] }] },
            "rooted" => ledger with { Folds = [entry with { Paths = [path with { Path = "/a.txt" }] }] },
            "state" => ledger with { Folds = [entry with { State = "done" }] },
            "type" => ledger with { Folds = [entry with { Type = "refresh" }] },
            "what" => ledger with { Folds = [entry with { Paths = [path with { What = "copied" }] }] },
            "digest" => ledger with { Folds = [entry with { Paths = [path with { Before = "x" }] }] },
            "twice" => ledger with { Folds = [entry with { Paths = [path, path with { What = "merged" }] }] },
            "readWhileRefused" => ledger with { Folds = [entry with { State = FlowFoldEntry.Refused }] },
            "unreadSettled" => ledger with { Folds = [entry with { Paths = [path with { Changed = null }] }] },
            "graphedRefused" => ledger with { Folds = [entry with { State = FlowFoldEntry.Refused, Paths = [path with { Changed = null }], Graphed = true }] },
            "graphedFalse" => ledger with { Folds = [entry with { Graphed = false }] },
            _ => throw new ArgumentOutOfRangeException(nameof(wrong), wrong, "no such case"),
        };

        Assert.Equal(problem, broken.Problem());
    }

    /// <summary>
    /// An agent's name in the ledger, in what a step keeps and in a target - which names the target's own directory - is
    /// held to the one rule of agents' names, never only to being there.
    /// </summary>
    [Theory]
    [InlineData("../a1")]
    [InlineData("a/b")]
    [InlineData("a b")]
    [InlineData(" ")]
    public void AnAgentsNameInTheRecords_IsHeldToTheNameRule(string agent)
    {
        var rule = OrchestrationRules.NameProblem(agent);
        var path = new FlowFoldPath("a.txt", "written", Digest, Left: null, Changed: true);
        var entry = new FlowFoldEntry("f1", "fold", 1, "fold", agent, "applied", At, [path], [], SettledAt: At);
        var record = new FlowFoldRecord("fold", 1, agent, "fold", [], []);

        Assert.NotNull(rule);
        Assert.Equal($"its fold 'f1': its agent: {rule}", new FlowFoldLedgerFile(1, "r1", [entry]).Problem());
        Assert.Equal($"its agent: {rule}", record.Problem());
        Assert.Equal($"its agent: {rule}", new FlowTargetRecord(agent, FlowTargetRecord.NotStarted, 0).Problem());
    }

    /// <summary>
    /// What a fold family step keeps is read strictly: a standing, a consent and a reason the engine has; a consent given
    /// by an answer that gives one, for something it names; a target that failed names its code, and one not ended none.
    /// </summary>
    [Fact]
    public void TheFoldRecordAndTheTargets_AreStrict()
    {
        var record = new FlowFoldRecord(
            "fold",
            1,
            "a1",
            "fold",
            [new FlowFoldGrant("foldNewRows", ["R-1"], "q1", "allow")],
            [],
            new FlowFoldShown(13, "lacks", ["FoldNewRows"], [], [], "NeedsConsent"),
            new FlowFoldAsked("foldNewRows", ["R-1"], 0));

        Assert.Null(record.Problem());
        Assert.Equal("its dry run: 'Lacking' is not a standing", (record with { Shown = record.Shown! with { Standing = "Lacking" } }).Problem());
        Assert.Equal("its dry run: 'NewRows' is not a consent", (record with { Shown = record.Shown! with { Needs = ["NewRows"] } }).Problem());
        Assert.Equal("its dry run: 'conflict' is not a reason a request is refused for", (record with { Shown = record.Shown! with { Refusals = ["conflict"] } }).Problem());
        Assert.Equal("a consent it was given: 'retry' gives no consent", (record with { Granted = [record.Granted[0] with { Answer = "retry" }] }).Problem());
        Assert.Equal("a consent it was given: it names nothing it was given for", (record with { Granted = [record.Granted[0] with { Named = [] }] }).Problem());
        Assert.Equal("'merge' is not a step of the fold family", (record with { Type = "merge" }).Problem());
        Assert.Equal("its question: its answer stands at -1", (record with { Asked = record.Asked! with { AnswerAt = -1 } }).Problem());

        Assert.Equal("its question: 'retry' is not a question a fold family step asks", (record with { Asked = record.Asked! with { Kind = "retry" } }).Problem());
        Assert.Equal("a consent it was given: 'foldStale' is not a question a fold family step asks", (record with { Granted = [record.Granted[0] with { Kind = "foldStale" }] }).Problem());
        Assert.Null((record with { Granted = [record.Granted[0] with { Kind = "foldOutside", Named = ["../x.txt"] }] }).Problem());
        Assert.Equal("a consent it was given: 'merge' gives no consent", (record with { Granted = [record.Granted[0] with { Answer = "merge" }] }).Problem());
        Assert.Null((record with { Granted = [record.Granted[0] with { Kind = "foldTwoChanges", Answer = "merge" }] }).Problem());

        // A conflict is never answered merge: its question offers no consent to give.
        Assert.Equal("a consent it was given: 'merge' gives no consent", (record with { Granted = [record.Granted[0] with { Kind = "foldConflicts", Answer = "merge" }] }).Problem());

        Assert.Null(new FlowTargetRecord("a2", FlowTargetRecord.Failed, 1, Exit: 13).Problem());
        Assert.Equal("a target that failed names its code", new FlowTargetRecord("a2", FlowTargetRecord.Failed, 1).Problem());
        Assert.Equal("a target that failed names 0, the code of success", new FlowTargetRecord("a2", FlowTargetRecord.Failed, 1, Exit: 0).Problem());
        Assert.Equal("a target done names 0, the code of success, and no other", new FlowTargetRecord("a2", FlowTargetRecord.Done, 1, Exit: 13).Problem());
        Assert.Equal("a target done names 0, the code of success, and no other", new FlowTargetRecord("a2", FlowTargetRecord.Done, 1).Problem());
        Assert.Null(new FlowTargetRecord("a2", FlowTargetRecord.Done, 1, Exit: 0).Problem());
        Assert.Equal("a target notStarted names no code", new FlowTargetRecord("a2", FlowTargetRecord.NotStarted, 1, Exit: 21).Problem());
        Assert.Equal("'gone' is not where a target stands", new FlowTargetRecord("a2", "gone", 0).Problem());
        Assert.Null(new FlowTargetRecord("a2", FlowTargetRecord.Failed, 6, Exit: 13, Cause: "LockHeld", EndedByQuestion: "foldConflicts").Problem());
        Assert.Equal("'Held' is not a cause an ending is recorded with", new FlowTargetRecord("a2", FlowTargetRecord.Failed, 1, Exit: 13, Cause: "Held").Problem());
        Assert.Equal("'None' is not a cause an ending is recorded with", new FlowTargetRecord("a2", FlowTargetRecord.Failed, 1, Exit: 13, Cause: "None").Problem());
        Assert.Equal(
            "only a target that failed names a cause or a question that ended it",
            new FlowTargetRecord("a2", FlowTargetRecord.Done, 1, Exit: 0, Cause: "LockHeld").Problem());
        Assert.Equal(
            "only a target that failed names a cause or a question that ended it",
            new FlowTargetRecord("a2", FlowTargetRecord.NotStarted, 0, EndedByQuestion: "foldConflicts").Problem());
    }

    /// <summary>What a fold family step keeps reads back whole: what its dry run named among it, its plan's every member.</summary>
    [Fact]
    public void TheFoldRecord_ReadsBackWhole()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var planned = new AgentPlanned([], [], [], ["m.txt"], ArmRegistry: null) { Written = ["a.txt"], NewRows = ["R-1"], Registries = [".plans/r.md"] };
        var record = new FlowFoldRecord(
            "items[0].fold",
            2,
            "a1",
            "fold",
            [],
            ["c.txt"],
            new FlowFoldShown(13, "lacks", ["FoldNewRows"], [], ["out.txt"], "NeedsConsent", planned),
            Applied: new FlowFoldApplied(0, "folded", "Sound", new AgentFolded(["a.txt"], [], [], ArmRegistry: null, [new HandedPath("h.txt", null)], [], [], []), planned, "f1"));

        harness.OrchestrationStore.WriteFoldRecord(temp.Path, record);

        AgentVerbFactsTests.Same(record, harness.OrchestrationStore.ReadFoldRecord(temp.Path));
        Assert.Null(harness.OrchestrationStore.ReadFoldRecord(Path.Combine(temp.Path, "none")));
    }

    /// <summary>
    /// A stopped run whose ledger holds a fold no commit took is unfinished - it would fold into the main tree a second run
    /// folds into - and a session is refused, saying so; what is staged is not yet committed, and once a commit takes
    /// the fold's change - one made by hand, which no step of the run made - the run holds nothing in the tree.
    /// </summary>
    [Fact]
    public async Task ARunWithAFoldNotYetCommitted_IsUnfinished_UntilACommitTakesIt()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        await harness.InitializeGitRepositoryAsync(temp.Path, Token);
        var layout = new HarnessLayout(temp.Path, temp.Path);
        var sessions = new FlowSessions(harness.OrchestrationStore, harness.FileSystem, harness.Identity, harness.GitClient);
        var definition = WorkflowFileParser.Parse("workflows/flow/flow.yml", "name: flow\nsteps:\n  - name: one\n    type: orient\n");
        var (other, _) = await sessions.CreateAsync(layout, definition, cancellationToken: Token);
        new FlowRunOwner(harness.OrchestrationStore, harness.Identity).Release(other);
        // The run's own state says Stopped - written by hand, for the test is of the scan, not of the stop.
        harness.OrchestrationStore.WriteFlow(other, new FlowFile(1, other.Name, "flow", Digest, new Dictionary<string, string>(), "Stopped", new FlowBudgetRecord(0, 0, 0), 0, [], [], At));
        var ledger = new FoldLedger(harness.OrchestrationStore, harness.FileSystem, TimeProvider.System);
        var id = await ledger.IntendAsync(other, temp.Path, "one", 1, "fold", "a1", new AgentPlanned([], [], [], [], null) { Written = ["a.txt"] }, Token);
        File.WriteAllText(Path.Combine(temp.Path, "a.txt"), "folded\n");
        await ledger.SettleAsync(other, temp.Path, id, HarnessExit.Success);

        var held = await Assert.ThrowsAsync<HarnessException>(() => sessions.CreateAsync(layout, definition, wantedName: "second", cancellationToken: Token));

        Assert.Contains("run 'r1' of this repository is unfinished (stopped; 1 fold in its ledger not yet committed)", held.Message, StringComparison.Ordinal);

        // A ledger that does not read as one cannot say what it holds, and the run is unfinished all the same.
        var written = File.ReadAllText(other.FoldsFile);
        File.WriteAllText(other.FoldsFile, "{");
        var unread = await Assert.ThrowsAsync<HarnessException>(() => sessions.CreateAsync(layout, definition, wantedName: "second", cancellationToken: Token));
        File.WriteAllText(other.FoldsFile, written);

        Assert.Contains("run 'r1' of this repository is unfinished (stopped, and whether its ledger holds a fold not yet committed cannot be told: ", unread.Message, StringComparison.Ordinal);

        _ = await harness.RunGitAsync(temp.Path, ["add", "--", "a.txt"], Token);
        var staged = await Assert.ThrowsAsync<HarnessException>(() => sessions.CreateAsync(layout, definition, wantedName: "second", cancellationToken: Token));

        Assert.Contains("(stopped; 1 fold in its ledger not yet committed)", staged.Message, StringComparison.Ordinal);

        _ = await harness.RunGitAsync(temp.Path, ["commit", "--quiet", "-m", "the fold, committed by hand"], Token);

        Assert.Equal("second", (await sessions.CreateAsync(layout, definition, wantedName: "second", cancellationToken: Token)).Run.Name);
    }

    /// <summary>
    /// Whether a stopped run's fold is committed is told by what HEAD holds at its path, never by git's status, which does
    /// not look at a path marked assume-unchanged or skip-worktree: one so marked is unfinished all the same - and a fold
    /// of a path git ignores alone, which no commit takes, holds nothing in the tree.
    /// </summary>
    [Fact]
    public async Task ARunsFoldAMarkHidesFromStatus_IsUnfinished_AndOneGitIgnoresHoldsNothing()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        await harness.InitializeGitRepositoryAsync(temp.Path, Token);
        File.WriteAllText(Path.Combine(temp.Path, "a.txt"), "base\n");
        File.WriteAllText(Path.Combine(temp.Path, ".gitignore"), "*.log\n");
        _ = await harness.RunGitAsync(temp.Path, ["add", "--", "a.txt", ".gitignore"], Token);
        _ = await harness.RunGitAsync(temp.Path, ["commit", "--quiet", "-m", "a base"], Token);
        var layout = new HarnessLayout(temp.Path, temp.Path);
        var sessions = new FlowSessions(harness.OrchestrationStore, harness.FileSystem, harness.Identity, harness.GitClient);
        var definition = WorkflowFileParser.Parse("workflows/flow/flow.yml", "name: flow\nsteps:\n  - name: one\n    type: orient\n");
        var (other, _) = await sessions.CreateAsync(layout, definition, cancellationToken: Token);
        new FlowRunOwner(harness.OrchestrationStore, harness.Identity).Release(other);
        harness.OrchestrationStore.WriteFlow(other, new FlowFile(1, other.Name, "flow", Digest, new Dictionary<string, string>(), "Stopped", new FlowBudgetRecord(0, 0, 0), 0, [], [], At));
        var ledger = new FoldLedger(harness.OrchestrationStore, harness.FileSystem, TimeProvider.System);
        var id = await ledger.IntendAsync(other, temp.Path, "one", 1, "fold", "a1", new AgentPlanned([], [], [], [], null) { Written = ["run.log"] }, Token);
        File.WriteAllText(Path.Combine(temp.Path, "run.log"), "evidence\n");
        await ledger.SettleAsync(other, temp.Path, id, HarnessExit.Success);

        Assert.Equal("second", (await sessions.CreateAsync(layout, definition, wantedName: "second", cancellationToken: Token)).Run.Name);

        new FlowRunOwner(harness.OrchestrationStore, harness.Identity).Release(OrchestratorLayout.Of(layout, "second"));
        Directory.Delete(OrchestratorLayout.Of(layout, "second").Directory, recursive: true);
        id = await ledger.IntendAsync(other, temp.Path, "one", 2, "fold", "a1", new AgentPlanned([], [], [], [], null) { Written = ["a.txt"] }, Token);
        File.WriteAllText(Path.Combine(temp.Path, "a.txt"), "folded\n");
        await ledger.SettleAsync(other, temp.Path, id, HarnessExit.Success);
        _ = await harness.RunGitAsync(temp.Path, ["update-index", "--assume-unchanged", "--", "a.txt"], Token);

        var held = await Assert.ThrowsAsync<HarnessException>(() => sessions.CreateAsync(layout, definition, wantedName: "third", cancellationToken: Token));

        Assert.Contains("run 'r1' of this repository is unfinished (stopped; 1 fold in its ledger not yet committed)", held.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Whether the tree holds a path otherwise than HEAD is told by the blob git add would record for its file - its
    /// carriage returns kept where the index holds the path with them, as git's safer autocrlf rule keeps them - never by
    /// a hash that reads no index: under core.autocrlf a file HEAD holds with CRLF, the tree's byte for byte, differs in
    /// nothing. One marked assume-unchanged whose file changed differs all the same, as one gone and one HEAD does not
    /// hold do; a path neither holds does not.
    /// </summary>
    [Fact]
    public async Task WhatTheTreeHoldsOtherwiseThanHead_IsToldByTheBlobGitAddWouldRecord_ItsCarriageReturnsKept()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        await harness.InitializeGitRepositoryAsync(temp.Path, Token);
        _ = await harness.RunGitAsync(temp.Path, ["config", "core.autocrlf", "false"], Token);
        File.WriteAllText(Path.Combine(temp.Path, "crlf.txt"), "one\r\ntwo\r\n");
        File.WriteAllText(Path.Combine(temp.Path, "marked.txt"), "base\n");
        File.WriteAllText(Path.Combine(temp.Path, "gone.txt"), "base\n");
        _ = await harness.RunGitAsync(temp.Path, ["add", "--", "crlf.txt", "marked.txt", "gone.txt"], Token);
        _ = await harness.RunGitAsync(temp.Path, ["commit", "--quiet", "-m", "a base"], Token);
        _ = await harness.RunGitAsync(temp.Path, ["config", "core.autocrlf", "true"], Token);
        File.WriteAllText(Path.Combine(temp.Path, "marked.txt"), "changed\n");
        _ = await harness.RunGitAsync(temp.Path, ["update-index", "--assume-unchanged", "--", "marked.txt"], Token);
        File.Delete(Path.Combine(temp.Path, "gone.txt"));
        File.WriteAllText(Path.Combine(temp.Path, "new.txt"), "new\n");

        var differing = await FoldLedger.DifferingAsync(harness.GitClient, harness.FileSystem, temp.Path, ["crlf.txt", "marked.txt", "gone.txt", "new.txt", "never.txt"], Token);

        Assert.Equal(["gone.txt", "marked.txt", "new.txt"], differing.Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// A stopped run's fold committed by hand under core.autocrlf, of a file HEAD holds with CRLF - git add keeping the
    /// carriage returns the index held - holds nothing in the tree: a second run starts.
    /// </summary>
    [Fact]
    public async Task ARunsFoldCommittedByHandWithCarriageReturnsUnderAutocrlf_HoldsNothing()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        await harness.InitializeGitRepositoryAsync(temp.Path, Token);
        _ = await harness.RunGitAsync(temp.Path, ["config", "core.autocrlf", "false"], Token);
        File.WriteAllText(Path.Combine(temp.Path, "a.txt"), "one\r\n");
        _ = await harness.RunGitAsync(temp.Path, ["add", "--", "a.txt"], Token);
        _ = await harness.RunGitAsync(temp.Path, ["commit", "--quiet", "-m", "a file with carriage returns"], Token);
        _ = await harness.RunGitAsync(temp.Path, ["config", "core.autocrlf", "true"], Token);
        var layout = new HarnessLayout(temp.Path, temp.Path);
        var sessions = new FlowSessions(harness.OrchestrationStore, harness.FileSystem, harness.Identity, harness.GitClient);
        var definition = WorkflowFileParser.Parse("workflows/flow/flow.yml", "name: flow\nsteps:\n  - name: one\n    type: orient\n");
        var (other, _) = await sessions.CreateAsync(layout, definition, cancellationToken: Token);
        new FlowRunOwner(harness.OrchestrationStore, harness.Identity).Release(other);
        harness.OrchestrationStore.WriteFlow(other, new FlowFile(1, other.Name, "flow", Digest, new Dictionary<string, string>(), "Stopped", new FlowBudgetRecord(0, 0, 0), 0, [], [], At));
        var ledger = new FoldLedger(harness.OrchestrationStore, harness.FileSystem, TimeProvider.System);
        var id = await ledger.IntendAsync(other, temp.Path, "one", 1, "fold", "a1", new AgentPlanned([], [], [], [], null) { Written = ["a.txt"] }, Token);
        File.WriteAllText(Path.Combine(temp.Path, "a.txt"), "folded\r\n");
        await ledger.SettleAsync(other, temp.Path, id, HarnessExit.Success);
        _ = await harness.RunGitAsync(temp.Path, ["commit", "--quiet", "-m", "the fold, by hand", "--", "a.txt"], Token);

        Assert.Equal("folded\r\n", (await harness.RunGitAsync(temp.Path, ["show", "HEAD:a.txt"], Token)).StandardOutput);
        Assert.Equal("second", (await sessions.CreateAsync(layout, definition, wantedName: "second", cancellationToken: Token)).Run.Name);
    }

    private const string Head = "1111111111111111111111111111111111111111";

    private const string Other = "2222222222222222222222222222222222222222";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>A fold of <paramref name="instance"/> that wrote each of <paramref name="writes"/> into the tree at <paramref name="main"/>, settled applied.</summary>
    private static async Task<string> FoldedAsync(FoldLedger ledger, OrchestratorLayout layout, string main, string instance, params (string Path, string Text)[] writes)
    {
        var id = await ledger.IntendAsync(layout, main, instance, 1, "fold", "a1", new AgentPlanned([], [], [], [], null) { Written = [.. writes.Select(write => write.Path)] }, Token);

        foreach (var (path, text) in writes)
        {
            File.WriteAllText(Path.Combine(main, path), text);
        }

        await ledger.SettleAsync(layout, main, id, HarnessExit.Success);
        return id;
    }

    /// <summary>A ledger over a run of the repository at <paramref name="temp"/>, with the clock at <see cref="At"/>.</summary>
    private static (HarnessFactory Harness, OrchestratorLayout Layout, FoldLedger Ledger) Ledger(TempDirectory temp)
    {
        var harness = new HarnessFactory();
        var layout = OrchestratorLayout.Of(new HarnessLayout(temp.Path, temp.Path), "r1");
        Directory.CreateDirectory(layout.Directory);
        harness.OrchestrationStore.WriteOrchestrator(layout, new OrchestratorRecord { Kind = OrchestratorRecord.KindName, Name = "r1", Model = "flow-engine", CreatedAt = At, Parallel = 1 });

        return (harness, layout, new FoldLedger(harness.OrchestrationStore, harness.FileSystem, new ManualClock(At)));
    }
}
