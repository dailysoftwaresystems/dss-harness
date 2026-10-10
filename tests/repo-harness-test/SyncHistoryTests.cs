using NSubstitute;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.Execution;
using RepoHarness.Core.Git;
using RepoHarness.Core.Output;
using RepoHarness.Core.Results;
using RepoHarness.Core.Sync;

namespace RepoHarness.Tests;

/// <summary>
/// A copy's git HEAD names the commit its tree was at, so what a step asks git about HEAD is answered in the copy as
/// it is in the tree: the commit goes with every sync, as a pack of git's own, and the history behind it where the
/// configuration asks for it.
/// </summary>
public sealed class SyncHistoryTests
{
    /// <summary>
    /// A copy this tool makes names the tree's commit as its HEAD, and holds what that commit holds: a step asking git
    /// which commit it is at, or what a file held there, is answered as it is in the tree. Named no commit, as every
    /// copy this tool made was, both questions failed there and passed here, over files byte for byte the same.
    /// </summary>
    [Fact]
    public async Task ACopysHead_NamesTheCommitItsTreeIsAt_AndGitAnswersThereAsItDoesInTheTree()
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var (harness, service) = await PrepareAsync(temp, token);
        var copy = SyncKit.CopyPath(temp);

        try
        {
            await service.SyncAsync(temp.Path, SyncKit.Transport(harness), copy, new SyncOptions(), token);

            Assert.Equal(await HeadAsync(harness, temp.Path, token), await HeadAsync(harness, copy, token));
            Assert.Equal("a\n", await GitAsync(harness, copy, token, "show", "HEAD:src/a.c"));

            // The commit alone, where nothing asks for more: what is behind it is not there, and git says so itself.
            Assert.Equal("1", (await GitAsync(harness, copy, token, "rev-list", "--count", "HEAD")).Trim());
            Assert.False((await harness.GitClient.RunAsync(copy, ["rev-parse", "--verify", "--quiet", "HEAD~1"], cancellationToken: token)).Succeeded);

            // Detached: the copy is on no branch of anybody's.
            Assert.False((await harness.GitClient.RunAsync(copy, ["symbolic-ref", "--quiet", "HEAD"], cancellationToken: token)).Succeeded);

            // And nothing the commit holds reads as changed, where the tree changed nothing since.
            Assert.Equal(string.Empty, await GitAsync(harness, copy, token, "diff", "--cached", "--name-only", "HEAD", "--", "src"));
            Assert.Contains($"the HEAD of '{copy}' is this tree's", harness.StandardOutput.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            SyncKit.DeleteIfPresent(copy);
        }
    }

    /// <summary>
    /// What the tree changed and has not committed reads in the copy as it does in the tree: a change since HEAD. The
    /// copy holds the tree as it stands, and its HEAD the commit the tree is at.
    /// </summary>
    [Fact]
    public async Task AnEditNotCommitted_ReadsInTheCopyAsAChangeSinceHead()
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var (harness, service) = await PrepareAsync(temp, token);
        var copy = SyncKit.CopyPath(temp);

        try
        {
            await File.WriteAllTextAsync(Path.Combine(temp.Path, "src", "a.c"), "edited\n", token);
            await service.SyncAsync(temp.Path, SyncKit.Transport(harness), copy, new SyncOptions(), token);

            Assert.Equal(await HeadAsync(harness, temp.Path, token), await HeadAsync(harness, copy, token));
            Assert.Equal("a\n", await GitAsync(harness, copy, token, "show", "HEAD:src/a.c"));
            Assert.Equal("src/a.c", (await GitAsync(harness, copy, token, "diff", "--cached", "--name-only", "HEAD", "--", "src")).Trim());
        }
        finally
        {
            SyncKit.DeleteIfPresent(copy);
        }
    }

    /// <summary>
    /// A commit made since the last sync goes with the next, and what the copy's own HEAD already gives it is left out:
    /// only what the new commit adds crosses. A sync that finds the copy's HEAD where the tree's is sends nothing and
    /// moves nothing, and says nothing of it.
    /// </summary>
    [Fact]
    public async Task ACommitMadeSinceTheLastSync_IsCarriedWithTheNext_LessWhatTheCopyHolds_AndOneThatFindsHeadInPlaceSendsNothing()
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var (harness, service) = await PrepareAsync(temp, token);
        var copy = SyncKit.CopyPath(temp);
        var transport = new RecordingTransport(SyncKit.Transport(harness));

        try
        {
            await service.SyncAsync(temp.Path, transport, copy, new SyncOptions(), token);

            var first = await HeadAsync(harness, temp.Path, token);
            var whole = transport.HistorySent.Sum(piece => piece.Bytes);

            Assert.Null(Assert.Single(transport.HistoryTaken).LeftOut);

            await File.WriteAllTextAsync(Path.Combine(temp.Path, "src", "c.c"), "c\n", token);
            await harness.CommitAllAsync(temp.Path, "add c", token);
            transport.HistorySent.Clear();

            await service.SyncAsync(temp.Path, transport, copy, new SyncOptions(), token);

            Assert.Equal(await HeadAsync(harness, temp.Path, token), await HeadAsync(harness, copy, token));
            Assert.Equal("c\n", await GitAsync(harness, copy, token, "show", "HEAD:src/c.c"));
            Assert.Equal(first, transport.HistoryTaken[^1].LeftOut);
            Assert.InRange(transport.HistorySent.Sum(piece => piece.Bytes), 1, whole - 1);

            // The commit before is one the copy holds, so the new one's history reaches it, and stops there.
            Assert.Equal("2", (await GitAsync(harness, copy, token, "rev-list", "--count", "HEAD")).Trim());

            transport.HistorySent.Clear();
            transport.HistoryTaken.Clear();
            harness.StandardOutput.GetStringBuilder().Clear();

            await service.SyncAsync(temp.Path, transport, copy, new SyncOptions(), token);

            Assert.Empty(transport.HistorySent);
            Assert.Empty(transport.HistoryTaken);
            Assert.DoesNotContain("HEAD", harness.StandardOutput.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            SyncKit.DeleteIfPresent(copy);
        }
    }

    /// <summary>
    /// With <c>sync.history: full</c> a copy holds every commit behind the tree's: what git lists of the history is
    /// the same in both, and nothing records the copy's as cut short. A later commit crosses alone.
    /// </summary>
    [Fact]
    public async Task WithTheWholeHistoryAskedFor_ACopyHoldsEveryCommitBehindHead_AndALaterCommitCrossesAlone()
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var (harness, service) = await PrepareAsync(temp, token, new HarnessConfig { Sync = new SyncConfig { History = GitHistoryWanted.Full } });
        var copy = SyncKit.CopyPath(temp);
        var transport = new RecordingTransport(SyncKit.Transport(harness));

        try
        {
            await service.SyncAsync(temp.Path, transport, copy, new SyncOptions(), token);

            Assert.Equal(await GitAsync(harness, temp.Path, token, "rev-list", "HEAD"), await GitAsync(harness, copy, token, "rev-list", "HEAD"));
            Assert.False(await harness.GitClient.IsShallowAsync(copy, token));

            var whole = transport.HistorySent.Sum(piece => piece.Bytes);
            var first = await HeadAsync(harness, temp.Path, token);

            await File.WriteAllTextAsync(Path.Combine(temp.Path, "src", "c.c"), "c\n", token);
            await harness.CommitAllAsync(temp.Path, "add c", token);
            transport.HistorySent.Clear();

            await service.SyncAsync(temp.Path, transport, copy, new SyncOptions(), token);

            Assert.Equal(await GitAsync(harness, temp.Path, token, "rev-list", "HEAD"), await GitAsync(harness, copy, token, "rev-list", "HEAD"));
            Assert.Equal(first, transport.HistoryTaken[^1].LeftOut);
            Assert.InRange(transport.HistorySent.Sum(piece => piece.Bytes), 1, whole - 1);
        }
        finally
        {
            SyncKit.DeleteIfPresent(copy);
        }
    }

    /// <summary>
    /// A tree put back at a commit its copy still holds moves the copy's HEAD there and sends nothing, whichever is asked
    /// for, the commit alone or all behind it: holding the commit is not having HEAD name it, and a copy left at the
    /// later commit answered for that one.
    /// </summary>
    [Theory]
    [InlineData(GitHistoryWanted.HeadOnly)]
    [InlineData(GitHistoryWanted.Full)]
    public async Task ATreePutBackAtACommitItsCopyHolds_MovesTheCopysHeadThere_AndSendsNothing(string history)
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var (harness, service) = await PrepareAsync(temp, token, new HarnessConfig { Sync = new SyncConfig { History = history } });
        var copy = SyncKit.CopyPath(temp);
        var transport = new RecordingTransport(SyncKit.Transport(harness));

        try
        {
            var earlier = await HeadAsync(harness, temp.Path, token);

            await service.SyncAsync(temp.Path, transport, copy, new SyncOptions(), token);
            await File.WriteAllTextAsync(Path.Combine(temp.Path, "src", "c.c"), "c\n", token);
            await harness.CommitAllAsync(temp.Path, "add c", token);
            await service.SyncAsync(temp.Path, transport, copy, new SyncOptions(), token);

            await harness.RunGitAsync(temp.Path, ["reset", "--quiet", "--hard", earlier!], token);
            transport.HistorySent.Clear();

            await service.SyncAsync(temp.Path, transport, copy, new SyncOptions(), token);

            Assert.Equal(earlier, await HeadAsync(harness, copy, token));
            Assert.Empty(transport.HistorySent);
            Assert.Null(transport.HistoryTaken[^1].Pack);
        }
        finally
        {
            SyncKit.DeleteIfPresent(copy);
        }
    }

    /// <summary>
    /// A copy given the commit alone is given what is behind it once the whole history is asked for, though its HEAD
    /// already names the tree's commit: nothing of its own is left out of what it is sent, as it holds none of what is
    /// behind, and it no longer records its history as cut short. The sync after sends nothing.
    /// </summary>
    [Fact]
    public async Task ACopyGivenTheCommitAlone_IsGivenWhatIsBehindIt_OnceTheWholeHistoryIsAskedFor()
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var (harness, service) = await PrepareAsync(temp, token);
        var copy = SyncKit.CopyPath(temp);
        var transport = new RecordingTransport(SyncKit.Transport(harness));

        try
        {
            await service.SyncAsync(temp.Path, transport, copy, new SyncOptions(), token);
            Assert.True(await harness.GitClient.IsShallowAsync(copy, token));

            // Asked for by a configuration nobody committed: the copy's HEAD is where the tree's is, and what is behind it is not.
            harness.WriteConfig(temp.Path, new HarnessConfig { Sync = new SyncConfig { History = GitHistoryWanted.Full } });
            await service.SyncAsync(temp.Path, transport, copy, new SyncOptions(), token);

            Assert.Null(transport.HistoryTaken[^1].LeftOut);
            Assert.Equal(await GitAsync(harness, temp.Path, token, "rev-list", "HEAD"), await GitAsync(harness, copy, token, "rev-list", "HEAD"));
            Assert.False(await harness.GitClient.IsShallowAsync(copy, token));
            Assert.False(File.Exists(Path.Combine(copy, ".git", "shallow")));
            Assert.False(File.Exists(Path.Combine(copy, ".git", "shallow.lock")));

            // Its HEAD named the commit before and after, and the sync says what it did instead of moving it.
            Assert.Contains($"the HEAD of '{copy}' was this tree's already", harness.StandardOutput.ToString(), StringComparison.Ordinal);

            transport.HistorySent.Clear();
            transport.HistoryTaken.Clear();

            await service.SyncAsync(temp.Path, transport, copy, new SyncOptions(), token);

            Assert.Empty(transport.HistorySent);
            Assert.Empty(transport.HistoryTaken);
        }
        finally
        {
            SyncKit.DeleteIfPresent(copy);
        }
    }

    /// <summary>
    /// A tree whose own repository is a shallow clone - what a pipeline checks out - gives a copy all it holds and no
    /// more, the copy's history stopping where the tree's does; and the sync after sends nothing. Read as a copy still
    /// short of the whole, it was sent the whole of it again with every sync.
    /// </summary>
    [Fact]
    public async Task ATreeWhoseOwnHistoryIsCutShort_GivesACopyAllItHolds_AndTheNextSyncSendsNothing()
    {
        using var origin = new TempDirectory();
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var (harness, _) = await PrepareAsync(origin, token, new HarnessConfig { Sync = new SyncConfig { History = GitHistoryWanted.Full } });

        await File.WriteAllTextAsync(Path.Combine(origin.Path, "src", "c.c"), "c\n", token);
        await harness.CommitAllAsync(origin.Path, "add c", token);
        await File.WriteAllTextAsync(Path.Combine(origin.Path, "src", "d.c"), "d\n", token);
        await harness.CommitAllAsync(origin.Path, "add d", token);

        // Two commits deep, of a history longer than that.
        await harness.RunGitAsync(temp.Path, ["clone", "--quiet", "--depth", "2", new Uri(origin.Path).AbsoluteUri, "."], token);

        var service = SyncKit.Service(harness);
        var copy = SyncKit.CopyPath(temp);
        var transport = new RecordingTransport(SyncKit.Transport(harness));

        try
        {
            await service.SyncAsync(temp.Path, transport, copy, new SyncOptions(), token);

            Assert.Equal("2", (await GitAsync(harness, temp.Path, token, "rev-list", "--count", "HEAD")).Trim());
            Assert.Equal(await GitAsync(harness, temp.Path, token, "rev-list", "HEAD"), await GitAsync(harness, copy, token, "rev-list", "HEAD"));
            Assert.True(await harness.GitClient.IsShallowAsync(copy, token));

            transport.HistorySent.Clear();
            transport.HistoryTaken.Clear();

            await service.SyncAsync(temp.Path, transport, copy, new SyncOptions(), token);

            Assert.Empty(transport.HistorySent);
            Assert.Empty(transport.HistoryTaken);
        }
        finally
        {
            SyncKit.DeleteIfPresent(copy);
        }
    }

    /// <summary>
    /// A clone a sync takes over has its HEAD moved, detached, to the tree's commit, as any copy's is, and nothing else
    /// of its repository changes: the branch it was on names the commit it named, and a commit of its own is still
    /// there. The takeover says beforehand that HEAD will move, and the sync says the once it does where it was - not
    /// again on the sync after, when it moves a HEAD that is on no branch.
    /// </summary>
    [Fact]
    public async Task ACloneTakenOver_HasItsHeadMovedDetached_ItsBranchAndCommitsAsTheyWere_AndIsToldSoOnce()
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var (harness, service) = await PrepareAsync(temp, token);
        var copy = SyncKit.CopyPath(temp);

        try
        {
            // Somebody's clone, with a commit of their own on the branch they left it on.
            await harness.RunGitAsync(temp.Path, ["clone", "--quiet", temp.Path, copy], token);
            await harness.RunGitAsync(copy, ["config", "user.email", "somebody@test.invalid"], token);
            await harness.RunGitAsync(copy, ["config", "user.name", "Somebody"], token);
            await File.WriteAllTextAsync(Path.Combine(copy, "theirs.txt"), "theirs\n", token);
            await harness.CommitAllAsync(copy, "theirs", token);

            var branch = (await GitAsync(harness, copy, token, "symbolic-ref", "--short", "HEAD")).Trim();
            var theirs = await HeadAsync(harness, copy, token);

            await File.WriteAllTextAsync(Path.Combine(temp.Path, "src", "c.c"), "c\n", token);
            await harness.CommitAllAsync(temp.Path, "add c", token);

            await service.SyncAsync(temp.Path, SyncKit.Transport(harness), copy, new SyncOptions(Adopt: ["local"]), token);

            var mine = await HeadAsync(harness, temp.Path, token);

            Assert.Equal(mine, await HeadAsync(harness, copy, token));
            Assert.False((await harness.GitClient.RunAsync(copy, ["symbolic-ref", "--quiet", "HEAD"], cancellationToken: token)).Succeeded);
            Assert.Equal(theirs, await harness.GitClient.ResolveCommitAsync(copy, branch, token));
            Assert.Equal("theirs\n", await GitAsync(harness, copy, token, "show", $"{branch}:theirs.txt"));

            // A clone holds what is behind the commit before, so the tree's own history is whole there too.
            Assert.Equal(await GitAsync(harness, temp.Path, token, "rev-list", "HEAD"), await GitAsync(harness, copy, token, "rev-list", "HEAD"));

            var said = harness.StandardError.ToString();

            Assert.Contains($"move     its git HEAD, detached, to this tree's commit {ReportText.Commit(mine!)}; its branches, tags and commits stay", said, StringComparison.Ordinal);
            Assert.Contains(
                $"the HEAD of '{copy}' was on the branch '{branch}', at {ReportText.Commit(theirs!)}, and is now detached at {ReportText.Commit(mine!)}.",
                said,
                StringComparison.Ordinal);

            // And the copy's own record of where its HEAD has been says who moved it.
            Assert.Equal(
                "dssharness sync: moved to the commit of the tree synced",
                (await GitAsync(harness, copy, token, "reflog", "-1", "--format=%gs")).Trim());

            // It held the commit before the one it was given, so it is as whole as it was, and nothing says otherwise.
            Assert.False(await harness.GitClient.IsShallowAsync(copy, token));
            Assert.DoesNotContain("shallow repository from here on", said, StringComparison.Ordinal);

            await File.WriteAllTextAsync(Path.Combine(temp.Path, "src", "d.c"), "d\n", token);
            await harness.CommitAllAsync(temp.Path, "add d", token);
            harness.StandardError.GetStringBuilder().Clear();

            await service.SyncAsync(temp.Path, SyncKit.Transport(harness), copy, new SyncOptions(), token);

            Assert.Equal(await HeadAsync(harness, temp.Path, token), await HeadAsync(harness, copy, token));
            Assert.Equal(theirs, await harness.GitClient.ResolveCommitAsync(copy, branch, token));
            Assert.DoesNotContain("was on the branch", harness.StandardError.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            SyncKit.DeleteIfPresent(copy);
        }
    }

    /// <summary>
    /// A tree with no commit yet gives a copy none: the copy is made, its HEAD names nothing, and nothing is said of it.
    /// Where the copy is a clone with a commit of its own, the two then differ, and the sync says so: nothing else would.
    /// </summary>
    [Fact]
    public async Task ATreeWithNoCommitYet_GivesACopyNone_AndSaysWhereACopysHeadThenNamesOne()
    {
        using var temp = new TempDirectory();
        using var other = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var harness = new HarnessFactory();
        var made = SyncKit.CopyPath(temp);
        var clone = SyncKit.CopyPath(temp);

        try
        {
            await harness.RunGitAsync(temp.Path, ["init", "--quiet", "."], token);

            var seeded = await harness.InitService.InitializeAsync(temp.Path, token);
            Assert.True(seeded.Succeeded, seeded.Message);
            string[] shared = ["a.c", "b.c", "c.c", "d.c"];

            foreach (var file in shared)
            {
                await File.WriteAllTextAsync(Path.Combine(temp.Path, file), "same\n", token);
            }

            var service = SyncKit.Service(harness);

            await service.SyncAsync(temp.Path, SyncKit.Transport(harness), made, new SyncOptions(), token);

            Assert.True(File.Exists(Path.Combine(made, "a.c")));
            Assert.Null(await harness.GitClient.ResolveCommitAsync(made, "HEAD", token));
            Assert.DoesNotContain("HEAD", harness.StandardOutput.ToString() + harness.StandardError, StringComparison.Ordinal);

            // Somebody's clone of something else, holding enough of what the tree does that taking it over deletes little.
            foreach (var file in shared)
            {
                await File.WriteAllTextAsync(Path.Combine(other.Path, file), "same\n", token);
            }

            await harness.InitializeGitRepositoryAsync(other.Path, token);
            await harness.RunGitAsync(other.Path, ["clone", "--quiet", other.Path, clone], token);

            var theirs = await HeadAsync(harness, clone, token);

            await service.SyncAsync(temp.Path, SyncKit.Transport(harness), clone, new SyncOptions(Adopt: ["local"]), token);

            Assert.Equal(theirs, await HeadAsync(harness, clone, token));
            Assert.Contains(
                $"this tree's HEAD names no commit yet, and the HEAD of '{clone}' names {ReportText.Commit(theirs!)}",
                harness.StandardError.ToString(),
                StringComparison.Ordinal);
        }
        finally
        {
            SyncKit.DeleteIfPresent(made);
            SyncKit.DeleteIfPresent(clone);
        }
    }

    /// <summary>
    /// A copy on a host is asked, sent and moved through the host's own agent, each request parsed there as any is: its
    /// HEAD names the tree's commit after a first sync, and after a commit made since, which crosses less what the copy
    /// holds; and a sync that finds its HEAD in place sends it nothing.
    /// </summary>
    [Fact]
    public async Task ACopyOnAHost_IsGivenTheTreesCommit_ThroughTheHostsAgent()
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var (harness, service) = await PrepareAsync(temp, token);
        var copy = SyncKit.CopyPath(temp);
        var transport = new RecordingTransport(SyncKit.AgentHere(harness));

        try
        {
            await service.SyncAsync(temp.Path, transport, copy, new SyncOptions(), token);

            var first = await HeadAsync(harness, temp.Path, token);

            Assert.Equal(first, await HeadAsync(harness, copy, token));
            Assert.Equal("a\n", await GitAsync(harness, copy, token, "show", "HEAD:src/a.c"));

            await File.WriteAllTextAsync(Path.Combine(temp.Path, "src", "c.c"), "c\n", token);
            await harness.CommitAllAsync(temp.Path, "add c", token);

            await service.SyncAsync(temp.Path, transport, copy, new SyncOptions(), token);

            Assert.Equal(await HeadAsync(harness, temp.Path, token), await HeadAsync(harness, copy, token));
            Assert.Equal("c\n", await GitAsync(harness, copy, token, "show", "HEAD:src/c.c"));
            Assert.Equal(first, transport.HistoryTaken[^1].LeftOut);

            transport.HistorySent.Clear();
            transport.HistoryTaken.Clear();

            await service.SyncAsync(temp.Path, transport, copy, new SyncOptions(), token);

            Assert.Empty(transport.HistorySent);
            Assert.Empty(transport.HistoryTaken);
        }
        finally
        {
            SyncKit.DeleteIfPresent(copy);
        }
    }

    /// <summary>
    /// A pack larger than one request holds crosses a piece to a request, each following the last, and is taken whole.
    /// </summary>
    [Fact]
    public async Task APackLargerThanARequestHolds_CrossesInPieces_EachFollowingTheLast()
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var (harness, service) = await PrepareAsync(temp, token);
        var copy = SyncKit.CopyPath(temp);
        var transport = new RecordingTransport(SyncKit.Transport(harness));

        try
        {
            // Nothing a pack makes smaller.
            var noise = new byte[SyncServe.LargestBatch + (1024 * 1024)];
            new Random(7).NextBytes(noise);
            await File.WriteAllBytesAsync(Path.Combine(temp.Path, "src", "noise.bin"), noise, token);
            await harness.CommitAllAsync(temp.Path, "noise", token);

            // Whatever size the repository's own configuration holds a pack to: split, it would be several, and none the copy takes.
            await harness.RunGitAsync(temp.Path, ["config", "pack.packSizeLimit", "1m"], token);

            await service.SyncAsync(temp.Path, transport, copy, new SyncOptions(), token);

            Assert.Equal(2, transport.HistorySent.Count);
            Assert.Equal((0L, (int)SyncServe.LargestBatch), (transport.HistorySent[0].Offset, transport.HistorySent[0].Bytes));
            Assert.Equal(SyncServe.LargestBatch, transport.HistorySent[1].Offset);
            Assert.Equal(await HeadAsync(harness, temp.Path, token), await HeadAsync(harness, copy, token));
            Assert.True((await harness.GitClient.RunAsync(copy, ["cat-file", "-e", "HEAD:src/noise.bin"], cancellationToken: token)).Succeeded);

            // Nothing of the pack is left aside once it is taken.
            Assert.False(Directory.Exists(Path.Combine(copy, ".git", "dssharness-incoming")));
        }
        finally
        {
            SyncKit.DeleteIfPresent(copy);
        }
    }

    /// <summary>
    /// A copy whose repository names its objects another way than the tree's can hold no commit of the tree's, and the
    /// sync says so and fails, naming both: left to run, git there would go on answering about HEAD for another tree. A
    /// copy made for such a tree names its objects as the tree does.
    /// </summary>
    [Fact]
    public async Task ACopyNamingItsObjectsAnotherWay_IsRefused_AndOneMadeForATreeNamesThemAsTheTreeDoes()
    {
        using var temp = new TempDirectory();
        using var longer = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var (harness, service) = await PrepareAsync(temp, token);
        var copy = SyncKit.CopyPath(temp);
        var made = SyncKit.CopyPath(longer);

        try
        {
            await service.SyncAsync(temp.Path, SyncKit.Transport(harness), copy, new SyncOptions(), token);

            // As a copy of some other tree would be: its repository made anew, naming objects the long way.
            harness.FileSystem.DeleteDirectory(Path.Combine(copy, ".git"));
            await harness.RunGitAsync(copy, ["init", "--quiet", "--object-format=sha256", "."], token);

            // Before anything is written there: what the tree changed since is not in the copy when the sync is refused.
            await File.WriteAllTextAsync(Path.Combine(temp.Path, "src", "a.c"), "changed\n", token);

            var refused = await Assert.ThrowsAsync<HarnessException>(
                () => service.SyncAsync(temp.Path, SyncKit.Transport(harness), copy, new SyncOptions(), token));

            Assert.Equal(HarnessExit.Refused, refused.ExitCode);
            Assert.Contains($"the git repository at '{copy}' names its objects by sha256, and this tree's by sha1", refused.Message, StringComparison.Ordinal);
            Assert.Contains("Nothing was changed.", refused.Message, StringComparison.Ordinal);
            Assert.Equal("a\n", await File.ReadAllTextAsync(Path.Combine(copy, "src", "a.c"), token));

            await harness.RunGitAsync(longer.Path, ["init", "--quiet", "--object-format=sha256", "."], token);
            await harness.InitializeHarnessAsync(longer.Path, token, new HarnessConfig());
            await harness.CommitAllAsync(longer.Path, "initial", token);

            await service.SyncAsync(longer.Path, SyncKit.Transport(harness), made, new SyncOptions(), token);

            var head = await HeadAsync(harness, longer.Path, token);

            Assert.Equal(64, head!.Length);
            Assert.Equal(head, await HeadAsync(harness, made, token));
        }
        finally
        {
            SyncKit.DeleteIfPresent(copy);
            SyncKit.DeleteIfPresent(made);
        }
    }

    /// <summary>
    /// A piece of a pack that does not follow what arrived before it is refused, and nothing of it kept: a pack with a
    /// piece lost, or twice over, is no pack. A piece at the start begins again, whatever an earlier pack left aside.
    /// </summary>
    [Fact]
    public async Task APieceThatDoesNotFollowWhatArrived_IsRefused_AndOneAtTheStartBeginsAgain()
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var harness = new HarnessFactory();
        var first = new string('a', 40);
        var second = new string('b', 40);
        var kept = Path.Combine(temp.Path, ".git", "dssharness-incoming");

        await harness.RunGitAsync(temp.Path, ["init", "--quiet", "."], token);

        await harness.GitClient.ReceiveHistoryAsync(temp.Path, first, 0, [1, 2, 3], token);
        await harness.GitClient.ReceiveHistoryAsync(temp.Path, first, 3, [4, 5], token);

        Assert.Equal([1, 2, 3, 4, 5], await File.ReadAllBytesAsync(Path.Combine(kept, first + ".pack"), token));

        var refused = await Assert.ThrowsAsync<HarnessException>(() => harness.GitClient.ReceiveHistoryAsync(temp.Path, first, 9, [6], token));

        Assert.Contains("did not follow what arrived before it: it starts at byte 9, and 5 byte(s) had arrived", refused.Message, StringComparison.Ordinal);
        Assert.Equal(5, new FileInfo(Path.Combine(kept, first + ".pack")).Length);

        await harness.GitClient.ReceiveHistoryAsync(temp.Path, second, 0, [7], token);

        Assert.Equal([second + ".pack"], Directory.GetFiles(kept).Select(Path.GetFileName));
    }

    /// <summary>
    /// What is sent as a pack and is none is not taken: the copy's HEAD stays where it was, nothing of it is left aside,
    /// and the failure says, in git's own words, why.
    /// </summary>
    [Fact]
    public async Task WhatIsSentAsAPackAndIsNone_IsNotTaken_AndHeadStaysWhereItWas()
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var (harness, _) = await PrepareAsync(temp, token);
        var before = await HeadAsync(harness, temp.Path, token);
        var wanted = new GitHistoryWanted(new string('c', 40), Whole: false, "sha1", []);
        var name = new string('d', 40);

        await harness.GitClient.ReceiveHistoryAsync(temp.Path, name, 0, "not a pack"u8.ToArray(), token);

        var failed = await Assert.ThrowsAsync<HarnessException>(
            () => harness.GitClient.TakeHistoryAsync(temp.Path, new GitHistoryTaken(wanted, name, null), token));

        Assert.StartsWith($"Could not take the history sent to '{temp.Path}': ", failed.Message, StringComparison.Ordinal);
        Assert.Equal(before, await HeadAsync(harness, temp.Path, token));
        Assert.False(Directory.Exists(Path.Combine(temp.Path, ".git", "dssharness-incoming")));
    }

    /// <summary>
    /// A pack that leaves out what the repository taking it does not hold is taken and found short: every object the
    /// commit names is walked before HEAD names it, so HEAD stays where it was, and the repository records its history
    /// as stopping nowhere it did not before.
    /// </summary>
    [Fact]
    public async Task APackThatLeavesOutWhatTheCopyDoesNotHold_IsFoundShort_BeforeHeadIsMoved()
    {
        using var temp = new TempDirectory();
        using var bare = new TempDirectory();
        using var scratch = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var (harness, _) = await PrepareAsync(temp, token);

        var earlier = await HeadAsync(harness, temp.Path, token);

        await File.WriteAllTextAsync(Path.Combine(temp.Path, "src", "c.c"), "c\n", token);
        await harness.CommitAllAsync(temp.Path, "add c", token);

        // Packed as for a repository that holds the commit before, and handed to one that holds nothing.
        var wanted = (await harness.GitClient.DescribeHistoryAsync(temp.Path, whole: false, token))!;
        var pack = await harness.GitClient.PackHistoryAsync(temp.Path, wanted, earlier, scratch.Path, token);

        await harness.RunGitAsync(bare.Path, ["init", "--quiet", "."], token);
        await harness.GitClient.ReceiveHistoryAsync(bare.Path, pack.Name, 0, await File.ReadAllBytesAsync(pack.File, token), token);

        var failed = await Assert.ThrowsAsync<HarnessException>(
            () => harness.GitClient.TakeHistoryAsync(bare.Path, new GitHistoryTaken(wanted, pack.Name, earlier), token));

        Assert.Contains($"does not hold all that {wanted.Commit} names after taking what it was sent, so its HEAD was left where it was", failed.Message, StringComparison.Ordinal);
        Assert.Null(await harness.GitClient.ResolveCommitAsync(bare.Path, "HEAD", token));

        // What it took stays, recorded as what it is - a commit with no parent here - so nothing later reads it as damage;
        // and it is not read as held, since what the commit names is not all there.
        Assert.Equal([wanted.Commit], await File.ReadAllLinesAsync(Path.Combine(bare.Path, ".git", "shallow"), token));
        Assert.False((await harness.GitClient.ReadHistoryAsync(bare.Path, wanted, token)).Holds);

        // Sent again with nothing left out, it is taken.
        var whole = await harness.GitClient.PackHistoryAsync(temp.Path, wanted, null, scratch.Combine("whole"), token);

        await harness.GitClient.ReceiveHistoryAsync(bare.Path, whole.Name, 0, await File.ReadAllBytesAsync(whole.File, token), token);

        var moved = await harness.GitClient.TakeHistoryAsync(bare.Path, new GitHistoryTaken(wanted, whole.Name, null), token);

        Assert.True(moved.Moved);
        Assert.Null(moved.LeftAside);
        Assert.Equal(wanted.Commit, await HeadAsync(harness, bare.Path, token));
        Assert.Equal("1", (await GitAsync(harness, bare.Path, token, "rev-list", "--count", "HEAD")).Trim());

        // And a take naming a pack that was never sent says that, not that git found one cut short.
        var missing = await Assert.ThrowsAsync<HarnessException>(
            () => harness.GitClient.TakeHistoryAsync(bare.Path, new GitHistoryTaken(wanted, new string('f', 40), null), token));

        Assert.Contains("is not where it was kept", missing.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// What arrives from another machine as the name of a git object is one - whole, and nothing else - before it
    /// reaches a command line or a file's name, or is refused.
    /// </summary>
    [Theory]
    [InlineData("HEAD")]
    [InlineData("--upload-pack=x")]
    [InlineData("abc123")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("../../aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("")]
    public async Task WhatIsNotTheWholeNameOfAnObject_IsRefusedBeforeItReachesGit(string name)
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var harness = new HarnessFactory();
        var whole = new string('a', 40);

        await harness.RunGitAsync(temp.Path, ["init", "--quiet", "."], token);

        Assert.False(GitObjectId.IsWhole(name));
        Assert.True(GitObjectId.IsWhole(whole));
        Assert.True(GitObjectId.IsWhole(new string('0', 64)));

        async Task RefusedAsync(Func<Task> asked)
            => Assert.Equal(HarnessExit.UsageError, (await Assert.ThrowsAsync<HarnessException>(asked)).ExitCode);

        await RefusedAsync(() => harness.GitClient.ReceiveHistoryAsync(temp.Path, name, 0, [1], token));
        await RefusedAsync(() => harness.GitClient.ReadHistoryAsync(temp.Path, new GitHistoryWanted(name, false, "sha1", []), token));
        await RefusedAsync(() => harness.GitClient.ReadHistoryAsync(temp.Path, new GitHistoryWanted(whole, false, "sha1", [name]), token));
        await RefusedAsync(() => harness.GitClient.TakeHistoryAsync(temp.Path, new GitHistoryTaken(new GitHistoryWanted(name, false, "sha1", []), null, null), token));
        await RefusedAsync(() => harness.GitClient.TakeHistoryAsync(temp.Path, new GitHistoryTaken(new GitHistoryWanted(whole, false, "sha1", []), name, null), token));
        await RefusedAsync(() => harness.GitClient.TakeHistoryAsync(temp.Path, new GitHistoryTaken(new GitHistoryWanted(whole, false, "sha1", []), null, name), token));
        await RefusedAsync(() => harness.GitClient.TakeHistoryAsync(temp.Path, new GitHistoryTaken(new GitHistoryWanted(whole, false, "sha1", [name]), null, null), token));
        await RefusedAsync(() => harness.GitClient.PackHistoryAsync(temp.Path, new GitHistoryWanted(name, false, "sha1", []), null, temp.Combine("out"), token));
        await RefusedAsync(() => harness.GitClient.PackHistoryAsync(temp.Path, new GitHistoryWanted(whole, false, "sha1", []), name, temp.Combine("out"), token));

        Assert.False(Directory.Exists(Path.Combine(temp.Path, ".git", "dssharness-incoming")));
    }

    /// <summary>
    /// A request for a copy's history that arrives in part, or says how much is asked in a word this build does not
    /// know, is refused by the far side and changes nothing: read as less, the copy's HEAD would be left where the
    /// sync then says it is not.
    /// </summary>
    [Fact]
    public async Task ARequestForHistoryTheFarSideCannotRead_IsRefused_AndChangesNothing()
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var (harness, _) = await PrepareAsync(temp, token);
        var head = await HeadAsync(harness, temp.Path, token);
        var other = new string('e', 40);

        string[] Ask(string operation, params string[] operands) => ["sync-serve", operation, SyncServe.OperandsFollow, temp.Path, .. operands];

        var short_ = await CliRunner.RunAsync(Ask(SyncServe.Inspect, other, GitHistoryWanted.HeadOnly), token);
        var unknown = await CliRunner.RunAsync(Ask(SyncServe.Inspect, other, "everything", "sha1"), token);
        var noCommit = await CliRunner.RunAsync(Ask(SyncServe.TakeHistory, SyncServe.Nothing, SyncServe.Nothing), token);
        var noName = await CliRunner.RunAsync(Ask(SyncServe.TakeHistory, SyncServe.Nothing, SyncServe.Nothing, "HEAD", GitHistoryWanted.HeadOnly, "sha1"), token);
        var noOffset = await CliRunner.RunAsync(Ask(SyncServe.HistoryPiece, other, "start", Convert.ToBase64String([1])), token);
        var noFormat = await CliRunner.RunAsync(Ask(SyncServe.InitRepository, "--template=/elsewhere"), token);

        foreach (var refused in new[] { short_, unknown, noCommit, noName, noOffset, noFormat })
        {
            Assert.Equal(HarnessExit.UsageError, refused.ExitCode);
            Assert.Contains("The two ends are different builds.", refused.StandardError, StringComparison.Ordinal);
        }

        Assert.Contains("The history asked of the copy arrived in a shape this build cannot read", unknown.StandardError, StringComparison.Ordinal);
        Assert.Contains("was given no commit to take", noCommit.StandardError, StringComparison.Ordinal);
        Assert.Contains("'HEAD' is not the whole name of a git object", noName.StandardError, StringComparison.Ordinal);
        Assert.Contains("'start' is not how many bytes of a pack came before a piece of it", noOffset.StandardError, StringComparison.Ordinal);
        Assert.Contains("'--template=/elsewhere' is not how a git repository names its objects", noFormat.StandardError, StringComparison.Ordinal);
        Assert.Equal(head, await HeadAsync(harness, temp.Path, token));

        static SyncInspectAnswer Answered(RepoHarness.Core.Processes.ProcessResult result)
            => SyncServe.ReadAnswer<SyncInspectAnswer>(
                result.StandardOutput.Split('\n').Single(line => line.StartsWith(SyncServe.AnswerPrefix, StringComparison.Ordinal)).TrimEnd('\r'))!;

        // One it can read is answered: where HEAD stands, and that it holds nothing of a commit it has never seen.
        var asked = await CliRunner.RunAsync(Ask(SyncServe.Inspect, other, GitHistoryWanted.HeadOnly, "sha1"), token);
        var held = Answered(asked).Repository;

        Assert.Equal(HarnessExit.Success, asked.ExitCode);
        Assert.Equal(head, held!.Head);
        Assert.False(held.Holds);
        Assert.NotNull(held.Branch);

        // Asked of the commit its HEAD names, it holds it; asked with no commit at all - a tree that has none yet - it
        // says where its own HEAD stands and nothing more; and a directory that is no repository has none to speak of.
        Assert.True(Answered(await CliRunner.RunAsync(Ask(SyncServe.Inspect, head!, GitHistoryWanted.HeadOnly, "sha1"), token)).Repository!.Holds);

        var none = Answered(await CliRunner.RunAsync(Ask(SyncServe.Inspect, SyncServe.Nothing, GitHistoryWanted.HeadOnly, "sha1"), token)).Repository!;

        Assert.Equal((head, false), (none.Head, none.Holds));

        Directory.CreateDirectory(temp.Combine("build", "plain"));

        Assert.Null(Answered(await CliRunner.RunAsync(
            ["sync-serve", SyncServe.Inspect, SyncServe.OperandsFollow, temp.Combine("build", "plain"), other, GitHistoryWanted.HeadOnly, "sha1"],
            token)).Repository);
    }

    /// <summary>How much history a copy is given is one of two words; any other is refused, saying both.</summary>
    [Theory]
    [InlineData(GitHistoryWanted.HeadOnly, true)]
    [InlineData(GitHistoryWanted.Full, true)]
    [InlineData("none", false)]
    [InlineData("Full", false)]
    [InlineData("", false)]
    public void HowMuchHistoryACopyIsGiven_IsOneOfTwoWords(string history, bool allowed)
    {
        var problems = HarnessConfigValidator.Validate(new HarnessConfig { Sync = new SyncConfig { History = history } });

        if (allowed)
        {
            Assert.DoesNotContain(problems, problem => problem.Contains("sync.history", StringComparison.Ordinal));
            return;
        }

        Assert.Contains(
            $"sync.history must be 'head' (a copy is given the commit HEAD names) or 'full' (and every commit behind it), found '{history}'",
            problems);
    }

    /// <summary>
    /// A clone a sync takes over that is already at the tree's commit keeps its HEAD on the branch it is on: there is
    /// nothing to move, the takeover's list says nothing of HEAD, and nothing says it was detached.
    /// </summary>
    [Fact]
    public async Task ACloneAlreadyAtTheTreesCommit_KeepsItsHeadOnItsBranch_AndNothingIsSaidOfIt()
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var (harness, service) = await PrepareAsync(temp, token);
        var copy = SyncKit.CopyPath(temp);

        try
        {
            await harness.RunGitAsync(temp.Path, ["clone", "--quiet", temp.Path, copy], token);

            var branch = (await GitAsync(harness, copy, token, "symbolic-ref", "--short", "HEAD")).Trim();

            await service.SyncAsync(temp.Path, SyncKit.Transport(harness), copy, new SyncOptions(Adopt: ["local"]), token);

            Assert.Equal(branch, (await GitAsync(harness, copy, token, "symbolic-ref", "--short", "HEAD")).Trim());
            Assert.Equal(await HeadAsync(harness, temp.Path, token), await HeadAsync(harness, copy, token));
            Assert.DoesNotContain("HEAD", harness.StandardError.ToString() + harness.StandardOutput, StringComparison.Ordinal);
        }
        finally
        {
            SyncKit.DeleteIfPresent(copy);
        }
    }

    /// <summary>
    /// A clone given a commit no parent of which it holds has that commit recorded as where its history stops, and its
    /// own history reads as whole as it was: every commit of the branch it was on is still listed.
    /// </summary>
    [Fact]
    public async Task ACloneGivenACommitItHoldsNoParentOf_ReadsItsOwnHistoryAsWholeAsItWas()
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var (harness, service) = await PrepareAsync(temp, token);
        var copy = SyncKit.CopyPath(temp);

        try
        {
            await harness.RunGitAsync(temp.Path, ["clone", "--quiet", temp.Path, copy], token);

            var branch = (await GitAsync(harness, copy, token, "symbolic-ref", "--short", "HEAD")).Trim();
            var theirs = await GitAsync(harness, copy, token, "rev-list", branch);

            // Two commits on: the copy is given the second alone, and holds nothing of the first.
            await File.WriteAllTextAsync(Path.Combine(temp.Path, "src", "c.c"), "c\n", token);
            await harness.CommitAllAsync(temp.Path, "add c", token);
            await File.WriteAllTextAsync(Path.Combine(temp.Path, "src", "d.c"), "d\n", token);
            await harness.CommitAllAsync(temp.Path, "add d", token);

            await service.SyncAsync(temp.Path, SyncKit.Transport(harness), copy, new SyncOptions(Adopt: ["local"]), token);

            Assert.Equal(await HeadAsync(harness, temp.Path, token), await HeadAsync(harness, copy, token));
            Assert.Equal("1", (await GitAsync(harness, copy, token, "rev-list", "--count", "HEAD")).Trim());
            Assert.Equal(theirs, await GitAsync(harness, copy, token, "rev-list", branch));
            Assert.True((await harness.GitClient.RunAsync(copy, ["fsck", "--connectivity-only"], cancellationToken: token)).Succeeded);

            // Which makes a shallow repository of one that was whole, as git reads it: said in the list of what taking it
            // over does, and again once it has happened, with what undoes it.
            var said = harness.StandardError.ToString();
            var mine = await HeadAsync(harness, temp.Path, token);

            Assert.True(await harness.GitClient.IsShallowAsync(copy, token));
            Assert.Contains(
                "its branches, tags and commits stay, and where it lacks the commit before that one, git reads it as a shallow "
                + "repository from then on, which 'git fetch --unshallow' there undoes",
                said,
                StringComparison.Ordinal);
            Assert.Contains(
                $"'{copy}' held every commit behind those it holds, and was given {mine} without the commits behind it: git reads it "
                + "as a shallow repository from here on, and a fetch there brings nothing from behind that commit until "
                + "'git fetch --unshallow' is run there.",
                said,
                StringComparison.Ordinal);

            // Said the once: it is a shallow repository already the next time.
            await File.WriteAllTextAsync(Path.Combine(temp.Path, "src", "e.c"), "e\n", token);
            await harness.CommitAllAsync(temp.Path, "add e", token);
            harness.StandardError.GetStringBuilder().Clear();

            await service.SyncAsync(temp.Path, SyncKit.Transport(harness), copy, new SyncOptions(), token);

            Assert.DoesNotContain("shallow repository", harness.StandardError.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            SyncKit.DeleteIfPresent(copy);
        }
    }

    /// <summary>
    /// A take found short in a repository that held every commit behind its own says, with what it lacked, that git reads
    /// the repository as a shallow one from here on: what it took stays, recorded as where its history stops, and the
    /// take that failed is the only one that could say so.
    /// </summary>
    [Fact]
    public async Task ATakeFoundShortInARepositoryThatWasWhole_SaysGitReadsItAsShallowFromHereOn()
    {
        using var temp = new TempDirectory();
        using var scratch = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var (harness, _) = await PrepareAsync(temp, token);
        var copy = SyncKit.CopyPath(temp);

        try
        {
            await harness.RunGitAsync(temp.Path, ["clone", "--quiet", temp.Path, copy], token);

            var held = await HeadAsync(harness, copy, token);

            await File.WriteAllTextAsync(Path.Combine(temp.Path, "src", "y.c"), "y\n", token);
            await harness.CommitAllAsync(temp.Path, "add y", token);

            var between = await HeadAsync(harness, temp.Path, token);

            await File.WriteAllTextAsync(Path.Combine(temp.Path, "src", "z.c"), "z\n", token);
            await harness.CommitAllAsync(temp.Path, "add z", token);

            var wanted = await harness.GitClient.DescribeHistoryAsync(temp.Path, whole: false, token);
            var pack = await harness.GitClient.PackHistoryAsync(temp.Path, wanted, between, scratch.Path, token);

            await harness.GitClient.ReceiveHistoryAsync(copy, pack.Name, 0, await File.ReadAllBytesAsync(pack.File, token), token);

            var failed = await Assert.ThrowsAsync<HarnessException>(
                () => harness.GitClient.TakeHistoryAsync(copy, new GitHistoryTaken(wanted, pack.Name, between), token));

            Assert.Contains($"does not hold all that {wanted.Commit} names after taking what it was sent", failed.Message, StringComparison.Ordinal);
            Assert.Contains(
                $"'{copy}' held every commit behind those it holds, and was given {wanted.Commit} without the commits behind it: git reads it as a shallow repository from here on",
                failed.Message,
                StringComparison.Ordinal);
            Assert.Equal(held, await HeadAsync(harness, copy, token));
            Assert.True(await harness.GitClient.IsShallowAsync(copy, token));
        }
        finally
        {
            SyncKit.DeleteIfPresent(copy);
        }
    }

    /// <summary>
    /// Every copy made from one reading that lacks the same of its history is sent one pack, made once: a sweep's workers
    /// were each packed the same commit. It is kept in the tree's own git directory - on the volume that holds the
    /// repository, never a temporary directory a system may keep in memory - recorded as this process's, and removed
    /// with the reading; and what a new copy keeps of it is counted from that same pack.
    /// </summary>
    [Fact]
    public async Task EveryCopyOfOneReading_IsSentOnePack_MadeOnceInTheTreesGitDirectory_AndRemovedWithTheReading()
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var (harness, _) = await PrepareAsync(temp, token);
        var git = new InterceptingGitClient(harness.GitClient);
        var service = SyncKit.Service(harness, git: git);
        var first = SyncKit.CopyPath(temp);
        var second = SyncKit.CopyPath(temp);
        var own = Path.Combine(temp.Path, ".git");

        try
        {
            using (var reading = await service.ReadSourceAsync(temp.Path, token))
            {
                // Read, nothing is packed: only a copy that lacks something of the commit has it packed.
                Assert.Empty(Directory.GetFileSystemEntries(own, "dssharness-history-*"));

                var counted = await reading.NewCopyHistoryBytesAsync(token);

                await service.SyncAsync(reading, SyncKit.Transport(harness), first, new SyncOptions(), token);
                await service.SyncAsync(reading, SyncKit.Transport(harness), second, new SyncOptions(), token);

                Assert.Equal(new string?[] { null }, git.Packed);

                var kept = Assert.Single(Directory.GetDirectories(own, "dssharness-history-*"));
                var pack = Assert.Single(Directory.GetFiles(kept, "*.pack"));

                Assert.Equal(new FileInfo(pack).Length, counted);
                Assert.True(File.Exists(kept + ".owner.json"));
                Assert.Contains($"\"processId\": {Environment.ProcessId}", await File.ReadAllTextAsync(kept + ".owner.json", token), StringComparison.Ordinal);
                Assert.Equal(await HeadAsync(harness, temp.Path, token), await HeadAsync(harness, first, token));
                Assert.Equal(await HeadAsync(harness, temp.Path, token), await HeadAsync(harness, second, token));

                // A copy this tool made was never a whole repository, so nothing is said of it being a shallow one.
                Assert.DoesNotContain("shallow repository", harness.StandardError.ToString(), StringComparison.Ordinal);
            }

            Assert.Empty(Directory.GetFileSystemEntries(own, "dssharness-history-*"));

            // And the tree is as clean as it was: nothing kept there shows to git.
            Assert.Equal(string.Empty, (await GitAsync(harness, temp.Path, token, "status", "--porcelain")).Trim());
        }
        finally
        {
            SyncKit.DeleteIfPresent(first);
            SyncKit.DeleteIfPresent(second);
        }
    }

    /// <summary>
    /// Copies of one reading that lack different things of its history are each sent their own pack: one made for a
    /// copy that holds an earlier commit leaves out what that commit gives it, and is no pack for a copy that holds
    /// nothing.
    /// </summary>
    [Fact]
    public async Task CopiesOfOneReadingThatLackDifferentThings_AreEachSentTheirOwnPack()
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var (harness, _) = await PrepareAsync(temp, token);
        var git = new InterceptingGitClient(harness.GitClient);
        var service = SyncKit.Service(harness, git: git);
        var behind = SyncKit.CopyPath(temp);
        var made = SyncKit.CopyPath(temp);

        try
        {
            await service.SyncAsync(temp.Path, SyncKit.Transport(harness), behind, new SyncOptions(), token);

            var earlier = await HeadAsync(harness, temp.Path, token);

            await File.WriteAllTextAsync(Path.Combine(temp.Path, "src", "c.c"), "c\n", token);
            await harness.CommitAllAsync(temp.Path, "add c", token);
            git.Packed.Clear();

            using var reading = await service.ReadSourceAsync(temp.Path, token);

            // The one that holds the earlier commit first: its pack is the smaller, and a new copy given it would be short.
            await service.SyncAsync(reading, SyncKit.Transport(harness), behind, new SyncOptions(), token);
            await service.SyncAsync(reading, SyncKit.Transport(harness), made, new SyncOptions(), token);

            Assert.Equal(new[] { earlier, null }, git.Packed);

            foreach (var copy in new[] { behind, made })
            {
                Assert.Equal(await HeadAsync(harness, temp.Path, token), await HeadAsync(harness, copy, token));
                Assert.Equal("c\n", await GitAsync(harness, copy, token, "show", "HEAD:src/c.c"));
                Assert.Equal("a\n", await GitAsync(harness, copy, token, "show", "HEAD:src/a.c"));
            }
        }
        finally
        {
            SyncKit.DeleteIfPresent(behind);
            SyncKit.DeleteIfPresent(made);
        }
    }

    /// <summary>
    /// A clone taken over under <c>sync.history: full</c> is given every commit behind the tree's, so nothing says it
    /// may become a shallow repository - not the refusal, not the list of what taking it over does - and it does not.
    /// </summary>
    [Fact]
    public async Task ACloneTakenOverWithTheWholeHistoryAsked_IsToldNothingOfBecomingShallow_AndDoesNot()
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var (harness, service) = await PrepareAsync(temp, token, new HarnessConfig { Sync = new SyncConfig { History = GitHistoryWanted.Full } });
        var copy = SyncKit.CopyPath(temp);

        try
        {
            await harness.RunGitAsync(temp.Path, ["clone", "--quiet", temp.Path, copy], token);
            await File.WriteAllTextAsync(Path.Combine(temp.Path, "src", "c.c"), "c\n", token);
            await harness.CommitAllAsync(temp.Path, "add c", token);
            await File.WriteAllTextAsync(Path.Combine(temp.Path, "src", "d.c"), "d\n", token);
            await harness.CommitAllAsync(temp.Path, "add d", token);

            var refused = await Assert.ThrowsAsync<HarnessException>(
                () => service.SyncAsync(temp.Path, SyncKit.Transport(harness), copy, new SyncOptions(), token));

            Assert.Contains("no branch, tag or commit of it is changed.", refused.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("shallow", refused.Message, StringComparison.Ordinal);

            await service.SyncAsync(temp.Path, SyncKit.Transport(harness), copy, new SyncOptions(Adopt: ["local"]), token);

            Assert.DoesNotContain("shallow", harness.StandardError.ToString(), StringComparison.Ordinal);
            Assert.False(await harness.GitClient.IsShallowAsync(copy, token));
            Assert.Equal(await GitAsync(harness, temp.Path, token, "rev-list", "HEAD"), await GitAsync(harness, copy, token, "rev-list", "HEAD"));
        }
        finally
        {
            SyncKit.DeleteIfPresent(copy);
        }
    }

    /// <summary>
    /// What a sync that was killed left packed in the tree's git directory is removed by the next reading that packs,
    /// with the record that it was that process's: nothing else ever would. What a process still running holds is left,
    /// as is what another machine recorded, which cannot be asked, and what a record that cannot be read names, which is
    /// said.
    /// </summary>
    [Fact]
    public async Task WhatAKilledSyncLeftPacked_IsRemovedByTheNextReadingThatPacks_AndALiveProcesssIsLeft()
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var (harness, _) = await PrepareAsync(temp, token);
        var identity = Substitute.For<IProcessIdentity>();

        identity.CurrentMachine.Returns("here");
        identity.CurrentId.Returns(11);
        identity.Current.Returns("eleven");
        identity.IsAlive(7, Arg.Any<string?>()).Returns(false);
        identity.IsAlive(8, Arg.Any<string?>()).Returns(true);

        var service = SyncKit.Service(harness, identity: identity);
        var copy = SyncKit.CopyPath(temp);
        var own = Path.Combine(temp.Path, ".git");

        string Left(string name, string record)
        {
            var directory = Path.Combine(own, $"dssharness-history-{name}");

            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "history-left.pack"), "left");
            File.WriteAllText(directory + ".owner.json", record);

            return directory;
        }

        var dead = Left("dead", "{ \"machine\": \"here\", \"processId\": 7, \"processStamp\": \"seven\" }");
        var live = Left("live", "{ \"machine\": \"here\", \"processId\": 8 }");
        var away = Left("away", "{ \"machine\": \"elsewhere\", \"processId\": 7 }");
        var torn = Left("torn", "{ \"machine\": ");

        try
        {
            await service.SyncAsync(temp.Path, SyncKit.Transport(harness), copy, new SyncOptions(), token);

            Assert.False(Directory.Exists(dead));
            Assert.False(File.Exists(dead + ".owner.json"));

            foreach (var kept in new[] { live, away, torn })
            {
                Assert.True(File.Exists(Path.Combine(kept, "history-left.pack")), kept);
                Assert.True(File.Exists(kept + ".owner.json"), kept);
            }

            Assert.Contains(
                $"The packs '{torn}.owner.json' records, which an earlier sync left in '{own}', could not be cleared:",
                harness.StandardError.ToString(),
                StringComparison.Ordinal);

            // Its own is gone with its reading, and the record of it named this process.
            Assert.Equal(3, Directory.GetDirectories(own, "dssharness-history-*").Length);
            Assert.Equal(await HeadAsync(harness, temp.Path, token), await HeadAsync(harness, copy, token));
        }
        finally
        {
            SyncKit.DeleteIfPresent(copy);
        }
    }

    /// <summary>
    /// What a take puts among a repository's own packs is the pack and its index, under the name git gives it, and
    /// nothing of git's temporary: read where it was kept, a take stopped part way leaves nothing there for nothing to
    /// clear. The same pack sent again is the repository's own as it stands, and an index a stopped take left with no
    /// pack - which git passes over - is replaced by the take that follows.
    /// </summary>
    [Fact]
    public async Task WhatATakePutsAmongARepositorysOwn_IsThePackAndItsIndex_AndAnIndexLeftWithNoPackIsReplaced()
    {
        using var temp = new TempDirectory();
        using var bare = new TempDirectory();
        using var scratch = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var (harness, _) = await PrepareAsync(temp, token);
        var wanted = await harness.GitClient.DescribeHistoryAsync(temp.Path, whole: false, token);
        var pack = await harness.GitClient.PackHistoryAsync(temp.Path, wanted, null, scratch.Path, token);
        var among = Path.Combine(bare.Path, ".git", "objects", "pack");

        async Task<GitHeadMoved> TakeAsync()
        {
            await harness.GitClient.ReceiveHistoryAsync(bare.Path, pack.Name, 0, await File.ReadAllBytesAsync(pack.File, token), token);

            return await harness.GitClient.TakeHistoryAsync(bare.Path, new GitHistoryTaken(wanted, pack.Name, null), token);
        }

        List<string> Own() => [.. Directory.GetFiles(among).Select(file => Path.GetFileName(file)).Order(StringComparer.Ordinal)];

        await harness.RunGitAsync(bare.Path, ["init", "--quiet", "."], token);

        Assert.True((await TakeAsync()).Moved);

        var own = Own();

        Assert.Contains($"pack-{pack.Name}.pack", own);
        Assert.Contains($"pack-{pack.Name}.idx", own);
        Assert.All(own, name => Assert.StartsWith($"pack-{pack.Name}.", name, StringComparison.Ordinal));
        Assert.False(Directory.Exists(Path.Combine(bare.Path, ".git", "dssharness-incoming")));

        // Sent and taken again, it is the repository's own as it stands, and its HEAD where it was.
        var again = await TakeAsync();

        Assert.False(again.Moved);
        Assert.Null(again.LeftAside);
        Assert.Equal(own, Own());

        // As a take stopped between the index and the pack left it: git reads the repository as holding nothing of it.
        var stopped = Path.Combine(among, $"pack-{pack.Name}.pack");

        File.SetAttributes(stopped, FileAttributes.Normal);
        File.Delete(stopped);
        Assert.False((await harness.GitClient.RunAsync(bare.Path, ["cat-file", "-e", wanted.Commit!], cancellationToken: token)).Succeeded);

        await TakeAsync();

        Assert.Equal(own, Own());
        Assert.Equal(wanted.Commit, await HeadAsync(harness, bare.Path, token));
        Assert.True((await harness.GitClient.RunAsync(bare.Path, ["fsck", "--connectivity-only"], cancellationToken: token)).Succeeded);
    }

    /// <summary>
    /// A merge one parent of which the copy holds, and the other not, is where the copy's history stops: read as a
    /// commit with its parents there because one is, git would find the other missing and call the repository damaged.
    /// </summary>
    [Fact]
    public async Task AMergeOneParentOfWhichTheCopyHolds_IsWhereItsHistoryStops()
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var (harness, service) = await PrepareAsync(temp, token);
        var copy = SyncKit.CopyPath(temp);

        try
        {
            await service.SyncAsync(temp.Path, SyncKit.Transport(harness), copy, new SyncOptions(), token);

            var held = await HeadAsync(harness, temp.Path, token);

            await harness.RunGitAsync(temp.Path, ["checkout", "--quiet", "-b", "side"], token);
            await File.WriteAllTextAsync(Path.Combine(temp.Path, "src", "s.c"), "s\n", token);
            await harness.CommitAllAsync(temp.Path, "side", token);
            await harness.RunGitAsync(temp.Path, ["checkout", "--quiet", "--detach", held!], token);
            await harness.RunGitAsync(temp.Path, ["merge", "--quiet", "--no-ff", "-m", "merge side", "side"], token);

            await service.SyncAsync(temp.Path, SyncKit.Transport(harness), copy, new SyncOptions(), token);

            var merge = await HeadAsync(harness, temp.Path, token);

            Assert.Equal(merge, await HeadAsync(harness, copy, token));
            Assert.Equal("s\n", await GitAsync(harness, copy, token, "show", "HEAD:src/s.c"));
            Assert.Contains(merge!, await File.ReadAllLinesAsync(Path.Combine(copy, ".git", "shallow"), token));
            Assert.Equal("1", (await GitAsync(harness, copy, token, "rev-list", "--count", "HEAD")).Trim());
            Assert.True((await harness.GitClient.RunAsync(copy, ["fsck", "--connectivity-only"], cancellationToken: token)).Succeeded);
        }
        finally
        {
            SyncKit.DeleteIfPresent(copy);
        }
    }

    /// <summary>
    /// A merge already recorded as where a copy's history stops stays recorded so once one of its parents arrives and
    /// the other has not: left out because a parent is now there, git would walk on into the one that is not, and read
    /// the repository as damaged - from the record of where HEAD has been, though HEAD itself has moved on.
    /// </summary>
    [Fact]
    public async Task AMergeRecordedAsWhereHistoryStops_StaysRecorded_OnceOneOfItsParentsArrivesAndTheOtherHasNot()
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var (harness, service) = await PrepareAsync(temp, token);
        var copy = SyncKit.CopyPath(temp);

        try
        {
            var root = await HeadAsync(harness, temp.Path, token);

            await harness.RunGitAsync(temp.Path, ["checkout", "--quiet", "-b", "side"], token);
            await File.WriteAllTextAsync(Path.Combine(temp.Path, "src", "s.c"), "s\n", token);
            await harness.CommitAllAsync(temp.Path, "side", token);
            await harness.RunGitAsync(temp.Path, ["checkout", "--quiet", "--detach", root!], token);
            await File.WriteAllTextAsync(Path.Combine(temp.Path, "src", "p.c"), "p\n", token);
            await harness.CommitAllAsync(temp.Path, "first parent", token);

            var first = await HeadAsync(harness, temp.Path, token);

            await harness.RunGitAsync(temp.Path, ["merge", "--quiet", "--no-ff", "-m", "merge side", "side"], token);

            var merge = await HeadAsync(harness, temp.Path, token);

            // A copy made at the merge holds neither parent; put back at the first, it holds that one alone.
            await service.SyncAsync(temp.Path, SyncKit.Transport(harness), copy, new SyncOptions(), token);
            await harness.RunGitAsync(temp.Path, ["checkout", "--quiet", "--detach", first!], token);
            await service.SyncAsync(temp.Path, SyncKit.Transport(harness), copy, new SyncOptions(), token);

            Assert.Equal(first, await HeadAsync(harness, copy, token));
            Assert.Equal(
                new[] { first!, merge! }.Order(StringComparer.Ordinal),
                await File.ReadAllLinesAsync(Path.Combine(copy, ".git", "shallow"), token));
            Assert.True((await harness.GitClient.RunAsync(copy, ["fsck", "--connectivity-only"], cancellationToken: token)).Succeeded);
        }
        finally
        {
            SyncKit.DeleteIfPresent(copy);
        }
    }

    /// <summary>
    /// A clone on a branch that already names the tree's commit, given the commits behind it that it lacked, stays on
    /// its branch, and is not said to have left it: nothing of HEAD was moved, and what it was sent is what is said.
    /// </summary>
    [Fact]
    public async Task ACloneOnABranchAtTheTreesCommit_GivenWhatIsBehindIt_StaysOnItsBranch_AndIsNotSaidToHaveLeftIt()
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var (harness, service) = await PrepareAsync(temp, token, new HarnessConfig { Sync = new SyncConfig { History = GitHistoryWanted.Full } });
        var copy = SyncKit.CopyPath(temp);

        try
        {
            await File.WriteAllTextAsync(Path.Combine(temp.Path, "src", "c.c"), "c\n", token);
            await harness.CommitAllAsync(temp.Path, "add c", token);
            await harness.RunGitAsync(temp.Path, ["clone", "--quiet", "--depth", "1", new Uri(temp.Path).AbsoluteUri, copy], token);

            var branch = (await GitAsync(harness, copy, token, "symbolic-ref", "--short", "HEAD")).Trim();

            Assert.Equal("1", (await GitAsync(harness, copy, token, "rev-list", "--count", "HEAD")).Trim());

            await service.SyncAsync(temp.Path, SyncKit.Transport(harness), copy, new SyncOptions(Adopt: ["local"]), token);

            Assert.Equal(branch, (await GitAsync(harness, copy, token, "symbolic-ref", "--short", "HEAD")).Trim());
            Assert.Equal(await GitAsync(harness, temp.Path, token, "rev-list", "HEAD"), await GitAsync(harness, copy, token, "rev-list", "HEAD"));
            Assert.False(await harness.GitClient.IsShallowAsync(copy, token));
            Assert.Contains($"the HEAD of '{copy}' was this tree's already", harness.StandardOutput.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("is now detached", harness.StandardError.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("move     its git HEAD", harness.StandardError.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            SyncKit.DeleteIfPresent(copy);
        }
    }

    /// <summary>
    /// A commit the repository holds without its parents, recorded nowhere - what a take stopped part way leaves - does
    /// not break the history of one taken after it: where the history behind the commit taken cannot be walked, that
    /// commit is where history stops, and git reads the repository as whole.
    /// </summary>
    [Fact]
    public async Task ACommitHeldWithoutItsParentsAndRecordedNowhere_DoesNotBreakTheHistoryOfOneTakenAfterIt()
    {
        using var temp = new TempDirectory();
        using var bare = new TempDirectory();
        using var scratch = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var (harness, _) = await PrepareAsync(temp, token);

        await File.WriteAllTextAsync(Path.Combine(temp.Path, "src", "c.c"), "c\n", token);
        await harness.CommitAllAsync(temp.Path, "add c", token);

        var left = (await harness.GitClient.DescribeHistoryAsync(temp.Path, whole: false, token))!;
        var stopped = await harness.GitClient.PackHistoryAsync(temp.Path, left, null, scratch.Combine("stopped"), token);

        await File.WriteAllTextAsync(Path.Combine(temp.Path, "src", "d.c"), "d\n", token);
        await harness.CommitAllAsync(temp.Path, "add d", token);

        // As a take stopped after its pack was put among the repository's own, and before anything recorded it.
        await harness.RunGitAsync(bare.Path, ["init", "--quiet", "."], token);

        var planted = Path.Combine(bare.Path, ".git", "objects", "pack", $"pack-{stopped.Name}.pack");

        Directory.CreateDirectory(Path.GetDirectoryName(planted)!);
        File.Copy(stopped.File, planted);
        await harness.RunGitAsync(bare.Path, ["index-pack", planted], token);

        var wanted = (await harness.GitClient.DescribeHistoryAsync(temp.Path, whole: false, token))!;
        var pack = await harness.GitClient.PackHistoryAsync(temp.Path, wanted, null, scratch.Combine("after"), token);

        await harness.GitClient.ReceiveHistoryAsync(bare.Path, pack.Name, 0, await File.ReadAllBytesAsync(pack.File, token), token);
        await harness.GitClient.TakeHistoryAsync(bare.Path, new GitHistoryTaken(wanted, pack.Name, null), token);

        Assert.Equal(wanted.Commit, await HeadAsync(harness, bare.Path, token));
        Assert.Equal("1", (await GitAsync(harness, bare.Path, token, "rev-list", "--count", "HEAD")).Trim());
        Assert.Contains(wanted.Commit!, await File.ReadAllLinesAsync(Path.Combine(bare.Path, ".git", "shallow"), token));
    }

    /// <summary>
    /// A take found short from a copy that already stopped somewhere leaves the copy's HEAD and what it could read as
    /// they were: the commit it stopped at is still recorded, the one it took is recorded too, as a commit with no
    /// parent here, and its own history still reads.
    /// </summary>
    [Fact]
    public async Task ATakeFoundShort_LeavesACopyThatStoppedSomewhere_ReadingAsItDid()
    {
        using var temp = new TempDirectory();
        using var scratch = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var (harness, service) = await PrepareAsync(temp, token);
        var copy = SyncKit.CopyPath(temp);

        try
        {
            await service.SyncAsync(temp.Path, SyncKit.Transport(harness), copy, new SyncOptions(), token);

            var held = await HeadAsync(harness, copy, token);

            await File.WriteAllTextAsync(Path.Combine(temp.Path, "src", "y.c"), "y\n", token);
            await harness.CommitAllAsync(temp.Path, "add y", token);

            var between = await HeadAsync(harness, temp.Path, token);

            await File.WriteAllTextAsync(Path.Combine(temp.Path, "src", "z.c"), "z\n", token);
            await harness.CommitAllAsync(temp.Path, "add z", token);

            // Packed as for a copy that holds the commit between, which this one never saw.
            var wanted = (await harness.GitClient.DescribeHistoryAsync(temp.Path, whole: false, token))!;
            var pack = await harness.GitClient.PackHistoryAsync(temp.Path, wanted, between, scratch.Path, token);

            await harness.GitClient.ReceiveHistoryAsync(copy, pack.Name, 0, await File.ReadAllBytesAsync(pack.File, token), token);
            await Assert.ThrowsAsync<HarnessException>(() => harness.GitClient.TakeHistoryAsync(copy, new GitHistoryTaken(wanted, pack.Name, between), token));

            Assert.Equal(held, await HeadAsync(harness, copy, token));
            Assert.Equal("1", (await GitAsync(harness, copy, token, "rev-list", "--count", "HEAD")).Trim());
            Assert.Equal(
                new[] { held!, wanted.Commit! }.Order(StringComparer.Ordinal),
                await File.ReadAllLinesAsync(Path.Combine(copy, ".git", "shallow"), token));

            // The sync after sends it whole, and it lands.
            await service.SyncAsync(temp.Path, SyncKit.Transport(harness), copy, new SyncOptions(), token);

            Assert.Equal(wanted.Commit, await HeadAsync(harness, copy, token));
            Assert.Equal("z\n", await GitAsync(harness, copy, token, "show", "HEAD:src/z.c"));
            Assert.Equal("y\n", await GitAsync(harness, copy, token, "show", "HEAD:src/y.c"));
        }
        finally
        {
            SyncKit.DeleteIfPresent(copy);
        }
    }

    /// <summary>
    /// A lock on the record of where history stops that some git holds, or left, is not written past: the take fails
    /// naming it and what to do, the lock is as it was, and HEAD stays where it was. One this tool's own write made and
    /// could not finish under is removed, never left to be taken for another git's.
    /// </summary>
    [Fact]
    public async Task ALockOnWhereHistoryStops_IsNeverWrittenPast_AndThisToolsOwnIsNeverLeftBehind()
    {
        using var temp = new TempDirectory();
        using var bare = new TempDirectory();
        using var scratch = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var (harness, _) = await PrepareAsync(temp, token);
        var wanted = (await harness.GitClient.DescribeHistoryAsync(temp.Path, whole: false, token))!;
        var pack = await harness.GitClient.PackHistoryAsync(temp.Path, wanted, null, scratch.Path, token);
        var shallow = Path.Combine(bare.Path, ".git", "shallow");

        async Task<HarnessException> TakeFailsAsync()
        {
            await harness.GitClient.ReceiveHistoryAsync(bare.Path, pack.Name, 0, await File.ReadAllBytesAsync(pack.File, token), token);

            return await Assert.ThrowsAsync<HarnessException>(
                () => harness.GitClient.TakeHistoryAsync(bare.Path, new GitHistoryTaken(wanted, pack.Name, null), token));
        }

        await harness.RunGitAsync(bare.Path, ["init", "--quiet", "."], token);
        await File.WriteAllTextAsync(shallow + ".lock", "another git's", token);

        var locked = await TakeFailsAsync();

        Assert.Contains($"'{shallow}.lock' is there", locked.Message, StringComparison.Ordinal);
        Assert.Contains("Once no git runs there, remove it and sync again.", locked.Message, StringComparison.Ordinal);
        Assert.Equal("another git's", await File.ReadAllTextAsync(shallow + ".lock", token));
        Assert.Null(await harness.GitClient.ResolveCommitAsync(bare.Path, "HEAD", token));

        File.Delete(shallow + ".lock");

        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // Where the record itself cannot be replaced - Windows replaces no file marked read-only - the lock this write
        // made goes with the failure.
        await File.WriteAllTextAsync(shallow, string.Empty, token);
        File.SetAttributes(shallow, FileAttributes.ReadOnly);

        try
        {
            var unwritten = await TakeFailsAsync();

            Assert.Contains($"Could not write '{shallow}'", unwritten.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("is there, which a git at work", unwritten.Message, StringComparison.Ordinal);
            Assert.False(File.Exists(shallow + ".lock"));
        }
        finally
        {
            File.SetAttributes(shallow, FileAttributes.Normal);
        }
    }

    /// <summary>
    /// A tree that is a linked worktree of a shallow clone gives a copy all its repository holds, and the sync after
    /// sends nothing: where its history stops is read from where git keeps it for that worktree, not from a '.git'
    /// directory the worktree does not have.
    /// </summary>
    [Fact]
    public async Task ATreeThatIsALinkedWorktreeOfAShallowClone_GivesACopyAllItHolds_AndTheNextSyncSendsNothing()
    {
        using var origin = new TempDirectory();
        using var clone = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var (harness, _) = await PrepareAsync(origin, token, new HarnessConfig { Sync = new SyncConfig { History = GitHistoryWanted.Full } });

        await File.WriteAllTextAsync(Path.Combine(origin.Path, "src", "c.c"), "c\n", token);
        await harness.CommitAllAsync(origin.Path, "add c", token);
        await File.WriteAllTextAsync(Path.Combine(origin.Path, "src", "d.c"), "d\n", token);
        await harness.CommitAllAsync(origin.Path, "add d", token);
        await harness.RunGitAsync(clone.Path, ["clone", "--quiet", "--depth", "2", new Uri(origin.Path).AbsoluteUri, "."], token);

        var tree = SyncKit.CopyPath(clone);
        var copy = SyncKit.CopyPath(clone);
        var transport = new RecordingTransport(SyncKit.Transport(harness));

        try
        {
            await harness.RunGitAsync(clone.Path, ["worktree", "add", "--quiet", "--detach", tree, "HEAD"], token);

            var service = SyncKit.Service(harness);

            await service.SyncAsync(tree, transport, copy, new SyncOptions(), token);

            Assert.Equal("2", (await GitAsync(harness, copy, token, "rev-list", "--count", "HEAD")).Trim());
            Assert.Equal(await GitAsync(harness, tree, token, "rev-list", "HEAD"), await GitAsync(harness, copy, token, "rev-list", "HEAD"));

            transport.HistorySent.Clear();
            transport.HistoryTaken.Clear();

            await service.SyncAsync(tree, transport, copy, new SyncOptions(), token);

            Assert.Empty(transport.HistorySent);
            Assert.Empty(transport.HistoryTaken);
        }
        finally
        {
            SyncKit.DeleteIfPresent(copy);
            await harness.GitClient.RunAsync(clone.Path, ["worktree", "remove", "--force", tree], cancellationToken: token);
            SyncKit.DeleteIfPresent(tree);
        }
    }

    /// <summary>
    /// A copy that is a linked worktree of somebody's repository has its own HEAD moved, and nothing of the repository
    /// it belongs to: the main worktree's HEAD, and the branch the linked one was on, name what they named.
    /// </summary>
    [Fact]
    public async Task ACopyThatIsALinkedWorktree_HasItsOwnHeadMoved_AndNothingOfTheRepositoryItBelongsTo()
    {
        using var temp = new TempDirectory();
        using var theirs = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var (harness, service) = await PrepareAsync(temp, token);
        var copy = SyncKit.CopyPath(temp);

        try
        {
            await harness.RunGitAsync(theirs.Path, ["clone", "--quiet", temp.Path, "."], token);
            await harness.RunGitAsync(theirs.Path, ["worktree", "add", "--quiet", "-b", "linked", copy], token);

            var main = await HeadAsync(harness, theirs.Path, token);
            var on = (await GitAsync(harness, theirs.Path, token, "symbolic-ref", "--short", "HEAD")).Trim();

            await File.WriteAllTextAsync(Path.Combine(temp.Path, "src", "c.c"), "c\n", token);
            await harness.CommitAllAsync(temp.Path, "add c", token);

            await service.SyncAsync(temp.Path, SyncKit.Transport(harness), copy, new SyncOptions(Adopt: ["local"]), token);

            Assert.Equal(await HeadAsync(harness, temp.Path, token), await HeadAsync(harness, copy, token));
            Assert.Equal(main, await HeadAsync(harness, theirs.Path, token));
            Assert.Equal(on, (await GitAsync(harness, theirs.Path, token, "symbolic-ref", "--short", "HEAD")).Trim());
            Assert.Equal(main, await harness.GitClient.ResolveCommitAsync(theirs.Path, "linked", token));

            // What was kept aside for it was kept where git keeps that worktree's own, and is gone.
            var kept = (await GitAsync(harness, copy, token, "rev-parse", "--git-path", "dssharness-incoming")).Trim();

            Assert.False(Directory.Exists(Path.GetFullPath(Path.Combine(copy, kept))));
        }
        finally
        {
            await harness.GitClient.RunAsync(theirs.Path, ["worktree", "remove", "--force", copy], cancellationToken: token);
            SyncKit.DeleteIfPresent(copy);
        }
    }

    /// <summary>
    /// The commit a copy's HEAD is given is the one the tree was at when it was read, whatever is committed between
    /// the reading and the sync: every copy of one reading names one commit, as each holds one set of files.
    /// </summary>
    [Fact]
    public async Task ACommitMadeAfterTheTreeWasRead_IsNotTheCopysHead()
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var (harness, service) = await PrepareAsync(temp, token);
        var copy = SyncKit.CopyPath(temp);

        try
        {
            var read = await service.ReadSourceAsync(temp.Path, token);
            var then = await HeadAsync(harness, temp.Path, token);

            await harness.RunGitAsync(temp.Path, ["commit", "--quiet", "--allow-empty", "-m", "made meanwhile"], token);
            await service.SyncAsync(read, SyncKit.Transport(harness), copy, new SyncOptions(), token);

            Assert.Equal(then, await HeadAsync(harness, copy, token));
            Assert.NotEqual(await HeadAsync(harness, temp.Path, token), await HeadAsync(harness, copy, token));
        }
        finally
        {
            SyncKit.DeleteIfPresent(copy);
        }
    }

    /// <summary>
    /// A sync that has only a HEAD to move marks the copy unfinished until it has, as one that has files to write does:
    /// a copy whose HEAD names another commit than its files' is no tree a run began with, and one a sync left so is
    /// not then taken for a copy it finished. A dry run says what it would do, and does none of it.
    /// </summary>
    [Fact]
    public async Task ASyncWithOnlyAHeadToMove_MarksTheCopyUnfinishedUntilItHas_AndADryRunSaysWhatItWouldDo()
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var (harness, service) = await PrepareAsync(temp, token);
        var copy = SyncKit.CopyPath(temp);
        var transport = new RecordingTransport(SyncKit.Transport(harness));

        try
        {
            await service.SyncAsync(temp.Path, transport, copy, new SyncOptions(), token);

            var before = await HeadAsync(harness, copy, token);

            await harness.RunGitAsync(temp.Path, ["commit", "--quiet", "--allow-empty", "-m", "nothing but a commit"], token);

            var after = await HeadAsync(harness, temp.Path, token);

            transport.Marked.Clear();
            transport.HistorySent.Clear();
            harness.StandardOutput.GetStringBuilder().Clear();

            var planned = await service.SyncAsync(temp.Path, transport, copy, new SyncOptions(DryRun: true), token);

            Assert.True(planned.Plan.IsUpToDate);
            Assert.Contains(
                $"would make the HEAD of '{copy}' name this tree's commit, {ReportText.Commit(after!)}, where it names {ReportText.Commit(before!)}, sending what it lacks of it",
                harness.StandardOutput.ToString(),
                StringComparison.Ordinal);
            Assert.Equal(before, await HeadAsync(harness, copy, token));
            Assert.Empty(transport.Marked);
            Assert.Empty(transport.HistorySent);
            Assert.False(Directory.Exists(Path.Combine(copy, ".git", "dssharness-incoming")));

            await service.SyncAsync(temp.Path, transport, copy, new SyncOptions(), token);

            Assert.Equal(after, await HeadAsync(harness, copy, token));
            Assert.Equal([CopyMark.Unfinished, CopyMark.Complete], transport.Marked);
        }
        finally
        {
            SyncKit.DeleteIfPresent(copy);
        }
    }

    /// <summary>
    /// What a sync packs for a copy is removed from this machine once it has crossed, and once it has failed to: a pack
    /// is the size of what it carries, and nothing clears the temporary directory of every system. A pack whose sending
    /// stopped part way is sent again from its start by the sync after, and lands.
    /// </summary>
    [Fact]
    public async Task WhatIsPackedForACopy_IsRemovedOnceItCrossed_AndOnceItFailedTo_AndASendThatStoppedBeginsAgain()
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var (harness, _) = await PrepareAsync(temp, token);
        var files = new RemovalsSeen(harness.FileSystem);
        var service = SyncKit.Service(harness, fileSystem: files);
        var copy = SyncKit.CopyPath(temp);
        var transport = new RecordingTransport(SyncKit.Transport(harness)) { FailsPiece = 1 };

        try
        {
            var dropped = await Assert.ThrowsAsync<HarnessException>(() => service.SyncAsync(temp.Path, transport, copy, new SyncOptions(), token));

            Assert.Contains("the link dropped sending the piece at 0", dropped.Message, StringComparison.Ordinal);

            var packed = Assert.Single(files.Removed, path => Path.GetFileName(path).StartsWith("dssharness-history-", StringComparison.Ordinal));

            Assert.False(Directory.Exists(packed));
            Assert.Null(await harness.GitClient.ResolveCommitAsync(copy, "HEAD", token));

            transport.FailsPiece = 0;
            await service.SyncAsync(temp.Path, transport, copy, new SyncOptions(), token);

            Assert.Equal(await HeadAsync(harness, temp.Path, token), await HeadAsync(harness, copy, token));
            Assert.Equal(0L, transport.HistorySent[^1].Offset);
            Assert.Equal(2, files.Removed.Count(path => Path.GetFileName(path).StartsWith("dssharness-history-", StringComparison.Ordinal)));
            Assert.All(files.Removed, path => Assert.False(Directory.Exists(path)));
        }
        finally
        {
            SyncKit.DeleteIfPresent(copy);
        }
    }

    /// <summary>
    /// A directory a sync has not been told to take over is refused saying, with what else taking it over replaces,
    /// that its git HEAD is moved to this tree's commit - in the short form too, where nothing would be deleted - and a
    /// dry run of it moves nothing.
    /// </summary>
    [Fact]
    public async Task ADirectoryNotYetTakenOver_IsRefusedSayingItsHeadWouldMove_AndADryRunMovesNothing()
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var (harness, service) = await PrepareAsync(temp, token);
        var copy = SyncKit.CopyPath(temp);

        try
        {
            await harness.RunGitAsync(temp.Path, ["clone", "--quiet", temp.Path, copy], token);

            var theirs = await HeadAsync(harness, copy, token);
            var branch = (await GitAsync(harness, copy, token, "symbolic-ref", "--short", "HEAD")).Trim();
            var said = $"Where it is a git repository, its HEAD is moved, detached, to this tree's commit, {ReportText.Commit(theirs!)}";

            var refused = await Assert.ThrowsAsync<HarnessException>(
                () => service.SyncAsync(temp.Path, SyncKit.Transport(harness), copy, new SyncOptions(), token));

            Assert.Contains(said, refused.Message, StringComparison.Ordinal);

            var planned = await service.SyncAsync(temp.Path, SyncKit.Transport(harness), copy, new SyncOptions(DryRun: true), token);

            Assert.True(planned.RequiresAdoption);
            Assert.Contains(said, harness.StandardOutput.ToString(), StringComparison.Ordinal);
            Assert.Equal(theirs, await HeadAsync(harness, copy, token));
            Assert.Equal(branch, (await GitAsync(harness, copy, token, "symbolic-ref", "--short", "HEAD")).Trim());
            Assert.False(Directory.Exists(Path.Combine(copy, ".git", "dssharness-incoming")));
        }
        finally
        {
            SyncKit.DeleteIfPresent(copy);
        }
    }

    /// <summary>
    /// Through a host's agent, every commit behind the tree's crosses where the whole history is asked for, a copy's
    /// history stops where a shallow tree's does and the sync after sends nothing, and a copy made for a tree that
    /// names its objects the long way names them so: each of those rides on a request the agent parses.
    /// </summary>
    [Fact]
    public async Task ThroughAHostsAgent_TheWholeHistoryCrosses_AShallowTreesStopsWhereItDoes_AndObjectsAreNamedAsTheTreeNamesThem()
    {
        using var origin = new TempDirectory();
        using var temp = new TempDirectory();
        using var longer = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var (harness, _) = await PrepareAsync(origin, token, new HarnessConfig { Sync = new SyncConfig { History = GitHistoryWanted.Full } });

        await File.WriteAllTextAsync(Path.Combine(origin.Path, "src", "c.c"), "c\n", token);
        await harness.CommitAllAsync(origin.Path, "add c", token);
        await File.WriteAllTextAsync(Path.Combine(origin.Path, "src", "d.c"), "d\n", token);
        await harness.CommitAllAsync(origin.Path, "add d", token);
        await harness.RunGitAsync(temp.Path, ["clone", "--quiet", "--depth", "2", new Uri(origin.Path).AbsoluteUri, "."], token);

        var service = SyncKit.Service(harness);
        var whole = SyncKit.CopyPath(origin);
        var shallow = SyncKit.CopyPath(temp);
        var made = SyncKit.CopyPath(longer);
        var transport = new RecordingTransport(SyncKit.AgentHere(harness));

        try
        {
            await service.SyncAsync(origin.Path, transport, whole, new SyncOptions(), token);

            Assert.Equal(await GitAsync(harness, origin.Path, token, "rev-list", "HEAD"), await GitAsync(harness, whole, token, "rev-list", "HEAD"));
            Assert.False(await harness.GitClient.IsShallowAsync(whole, token));

            await service.SyncAsync(temp.Path, transport, shallow, new SyncOptions(), token);

            Assert.Equal(await GitAsync(harness, temp.Path, token, "rev-list", "HEAD"), await GitAsync(harness, shallow, token, "rev-list", "HEAD"));
            Assert.True(await harness.GitClient.IsShallowAsync(shallow, token));

            transport.HistorySent.Clear();
            transport.HistoryTaken.Clear();

            await service.SyncAsync(temp.Path, transport, shallow, new SyncOptions(), token);

            Assert.Empty(transport.HistorySent);
            Assert.Empty(transport.HistoryTaken);

            await harness.RunGitAsync(longer.Path, ["init", "--quiet", "--object-format=sha256", "."], token);
            await harness.InitializeHarnessAsync(longer.Path, token, new HarnessConfig());
            await harness.CommitAllAsync(longer.Path, "initial", token);

            await service.SyncAsync(longer.Path, transport, made, new SyncOptions(), token);

            var head = await HeadAsync(harness, longer.Path, token);

            Assert.Equal(64, head!.Length);
            Assert.Equal(head, await HeadAsync(harness, made, token));
        }
        finally
        {
            SyncKit.DeleteIfPresent(whole);
            SyncKit.DeleteIfPresent(shallow);
            SyncKit.DeleteIfPresent(made);
        }
    }

    /// <summary>A file system that does what the one it wraps does, and holds every directory it was asked to remove.</summary>
    private sealed class RemovalsSeen(RepoHarness.Core.FileSystem.IFileSystem inner) : PassThroughFileSystem(inner)
    {
        public List<string> Removed { get; } = [];

        public override void DeleteDirectory(string path)
        {
            Removed.Add(path);
            base.DeleteDirectory(path);
        }
    }

    private static async Task<string?> HeadAsync(HarnessFactory harness, string directory, CancellationToken cancellationToken)
        => await harness.GitClient.ResolveCommitAsync(directory, "HEAD", cancellationToken);

    private static async Task<string> GitAsync(HarnessFactory harness, string directory, CancellationToken cancellationToken, params string[] arguments)
        => (await harness.RunGitAsync(directory, arguments, cancellationToken)).StandardOutput;

    private static async Task<(HarnessFactory Harness, ISyncService Service)> PrepareAsync(
        TempDirectory temp,
        CancellationToken cancellationToken,
        HarnessConfig? config = null)
    {
        var harness = new HarnessFactory();

        Directory.CreateDirectory(Path.Combine(temp.Path, "src"));
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "src", "a.c"), "a\n", cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "src", "b.c"), "b\n", cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(temp.Path, ".gitignore"), "build/\n", cancellationToken);

        // Two commits, so the tree's has one behind it.
        await harness.InitializeHarnessAsync(temp.Path, cancellationToken, config ?? new HarnessConfig());
        await harness.CommitAllAsync(temp.Path, "the harness", cancellationToken);

        return (harness, SyncKit.Service(harness));
    }
}
