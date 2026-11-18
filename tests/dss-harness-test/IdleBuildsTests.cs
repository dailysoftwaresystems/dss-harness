using DssHarness.Core.Build;
using DssHarness.Core.Execution;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Hosts;
using DssHarness.Core.Mutations;
using DssHarness.Core.Repository;
using DssHarness.Core.Runs;
using DssHarness.Core.Worktrees;

namespace DssHarness.Tests;

/// <summary>
/// Room made for a heavy leg from other trees' idle builds (<see cref="IdleBuilds"/>): the least recently built first, only
/// as many as it is short of, each removed as <c>clean --legs</c> removes one and leaving a note its tree's next build says
/// - and never one a run or a sweep holds, one no build of this tool made, one on another filesystem, or one of the leg's
/// own tree. A consumer's host of 47 GiB, holding five trees' copies of ~16 GiB builds, stopped two legs at the floor
/// while idle trees' builds sat beside them.
/// </summary>
public sealed class IdleBuildsTests
{
    private const string Variant = "x86_64-gcc-debug";

    private static readonly DateTime Older = new(2026, 11, 1, 8, 0, 0, DateTimeKind.Utc);

    private static readonly DateTime Newer = new(2026, 11, 10, 9, 30, 0, DateTimeKind.Utc);

    /// <summary>
    /// The least recently built of the other trees' builds is removed first, and no more are once what was removed covers
    /// what the leg is short of: the newer one stays. What was done is said whole - whose tree, where, when it was last
    /// built, the room before and after - and the note left where the build was says when, and for which leg.
    /// </summary>
    [Fact]
    public void TheLeastRecentlyBuilt_IsRemovedFirst_AndNoMoreOnceTheNeedIsCovered()
    {
        using var temp = new TempDirectory();
        var idle = Idle();
        var older = Built(temp, "older", Older);
        var newer = Built(temp, "newer", Newer);
        var bytes = Size(older.Build);

        var made = idle.MakeRoom(Scope(temp, older, newer), temp.Path, 1)!;

        Assert.Equal([new ReclaimedBuild("worktree o1/older", older.Build, bytes, Older)], made.Removed);
        Assert.Empty(made.Left);
        Assert.False(Directory.Exists(older.Build));
        Assert.True(Directory.Exists(newer.Build));
        Assert.Equal(
            $"removed ~{DiskSpace.Size(bytes)} of other trees' idle builds on '/data' to make room: worktree o1/older's at '{older.Build}', "
            + "last built 2026-11-01 08:00 UTC; 40 GiB free before, 40 GiB after",
            made.Describe());
        Assert.Equal(
            "its build directory was removed 2026-09-30 16:32 UTC, to make room for leg 'native' of the main checkout\n",
            File.ReadAllText(ReclaimedBuildNote.PathOf(older.Build)));
        Assert.False(File.Exists(ReclaimedBuildNote.PathOf(newer.Build)));
    }

    /// <summary>A leg short of more than one build holds goes on to the next least recently built, in that order.</summary>
    [Fact]
    public void ALegShortOfMoreThanOneBuild_RemovesTheNextLeastRecentlyBuilt_InThatOrder()
    {
        using var temp = new TempDirectory();
        var idle = Idle();
        var newer = Built(temp, "newer", Newer);
        var older = Built(temp, "older", Older);

        var made = idle.MakeRoom(Scope(temp, newer, older), temp.Path, Size(older.Build) + 1)!;

        Assert.Equal([older.Build, newer.Build], made.Removed.Select(build => build.Directory));
        Assert.False(Directory.Exists(newer.Build));
    }

    /// <summary>
    /// A build a run holds - by the lock a build of it takes, in the repository's lock here or in a host's copy's own - is
    /// never touched, and said with who holds it; the leg goes on to the next.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ABuildARunHolds_IsNeverTouched_AndIsSaid(bool hostCopies)
    {
        using var temp = new TempDirectory();
        var idle = Idle();
        var runLock = new RunLock(new HarnessFactory().FileSystem, new HarnessFactory().Output, new HarnessFactory().Identity);
        var held = Built(temp, "held", Older);
        var free = Built(temp, "free", Newer);
        Directory.CreateDirectory(Path.Combine(held.Root, HarnessLayout.DirectoryName));

        var layout = hostCopies ? new HarnessLayout(held.Root, held.Root) : MainLayout(temp);

        await using var building = (await runLock.TryAcquireAsync(
                layout,
                PlacedLeg.BuildLock(HostId.Local, held.Root, Variant, RunId.New(), "build"),
                TestContext.Current.CancellationToken))
            .Handle!;

        var made = idle.MakeRoom(Scope(temp, held, free) with { Layout = hostCopies ? null : MainLayout(temp) }, temp.Path, 1)!;

        Assert.True(Directory.Exists(held.Build));
        Assert.Equal([free.Build], made.Removed.Select(build => build.Directory));
        Assert.StartsWith($"worktree o1/held's build at '{held.Build}' was left, as a run holds it: ", Assert.Single(made.Left), StringComparison.Ordinal);
        Assert.False(File.Exists(ReclaimedBuildNote.PathOf(held.Build)));
    }

    /// <summary>A build whose variant a sweep in that tree holds is never touched, though no build of it runs.</summary>
    [Fact]
    public async Task ABuildASweepHolds_IsNeverTouched_AndIsSaid()
    {
        using var temp = new TempDirectory();
        var idle = Idle();
        var harness = new HarnessFactory();
        var runLock = new RunLock(harness.FileSystem, harness.Output, harness.Identity);
        var swept = Built(temp, "swept", Older);

        await using var sweeping = (await runLock.TryAcquireAsync(
                MainLayout(temp),
                MutationWorkers.SweepLock(HostId.Local, WorkerFamily.Of(swept.Root, VariantKey.CopyKeyOf(Variant)), RunId.New(), "check-mutations"),
                TestContext.Current.CancellationToken))
            .Handle!;

        var made = idle.MakeRoom(Scope(temp, swept), temp.Path, 1)!;

        Assert.True(Directory.Exists(swept.Build));
        Assert.Empty(made.Removed);
        Assert.StartsWith($"worktree o1/swept's build at '{swept.Build}' was left, as a sweep holds it: ", Assert.Single(made.Left), StringComparison.Ordinal);
        Assert.Equal($"no other tree's idle build on '/data' could be removed to make room; {made.Left[0]}", made.Describe());
    }

    /// <summary>
    /// Only a build directory a build of this tool recorded is one: a directory below build holding no record, an aside an
    /// earlier removal left, and a build on another filesystem than the one short of room are none - removing one there
    /// gives the leg nothing - and where nothing is left to remove, nothing is made.
    /// </summary>
    [Fact]
    public void WhatNoBuildRecorded_AnAside_AndABuildOnAnotherFilesystem_AreNeverRemoved()
    {
        using var temp = new TempDirectory();
        var elsewhere = Built(temp, "elsewhere", Older);
        var idle = Idle(path => path.StartsWith(elsewhere.Root, StringComparison.Ordinal) ? "/other" : "/data");
        var unrecorded = temp.Combine("unrecorded", "build", Variant);
        var aside = temp.Combine("aside", "build", "." + Variant + RemovalAside.Suffix);

        temp.WriteFile(Path.Combine("unrecorded", "build", Variant, "obj.o"), "object");
        temp.WriteFile(Path.Combine("aside", "build", "." + Variant + RemovalAside.Suffix, BuildRecord.FileName), "clean\n");

        var trees = new IdleBuildsScope(
            [new RepositoryTree(elsewhere.Root, "worktree o1/elsewhere"), new RepositoryTree(temp.Combine("unrecorded"), "worktree o1/unrecorded"), new RepositoryTree(temp.Combine("aside"), "worktree o1/aside")],
            MainLayout(temp),
            "leg 'native' of the main checkout");

        Assert.Null(idle.MakeRoom(trees, temp.Path, 1L << 40));
        Assert.True(Directory.Exists(elsewhere.Build));
        Assert.True(Directory.Exists(unrecorded));
        Assert.True(Directory.Exists(aside));
    }

    /// <summary>
    /// The trees a leg may make room from are every one of its machine's but its own: never its tree, the copy its work
    /// runs in, nor any copy its tree keeps in .legs - while another tree's copies are another tree's. The note names the
    /// leg by the tree its work runs in.
    /// </summary>
    [Fact]
    public void TheLegsOwnTree_TheCopyItWorksIn_AndItsTreesCopies_AreNeverAmongThem()
    {
        var root = TestHost.TemporaryRoot;
        var main = new RepositoryTree(Path.Combine(root, "repo"), RepositoryTree.MainCheckout);
        var mine = new RepositoryTree(Path.Combine(root, "wt", "o1", "mine"), "worktree o1/mine");
        var myCopy = new RepositoryTree(Path.Combine(mine.Root, HarnessLayout.LegsDirectoryName, "0a1b2c3"), "worktree o1/mine's leg copy") { LegCopy = true };
        var myOtherCopy = new RepositoryTree(Path.Combine(mine.Root, HarnessLayout.LegsDirectoryName, "4d5e6f7"), "worktree o1/mine's leg copy") { LegCopy = true };
        var theirs = new RepositoryTree(Path.Combine(root, "wt", "o1", "theirs"), "worktree o1/theirs");
        var theirCopy = new RepositoryTree(Path.Combine(theirs.Root, HarnessLayout.LegsDirectoryName, "0a1b2c3"), "worktree o1/theirs's leg copy") { LegCopy = true };

        var scope = IdleBuildsScope.Beside(
            new RepositoryTreesFound([main, mine, myCopy, myOtherCopy, theirs, theirCopy]),
            mine.Root,
            myCopy.Root,
            "native",
            layout: null,
            StringComparison.Ordinal);

        Assert.Equal([main, theirs, theirCopy], scope.Trees);
        Assert.Equal("leg 'native' of worktree o1/mine's leg copy", scope.For);
        Assert.Null(scope.Layout);
    }

    /// <summary>
    /// The note a removal leaves is said once, by the build that finds the directory gone, and removed; one whose directory
    /// is there again - built since by a build that did not read it - is removed unsaid.
    /// </summary>
    [Fact]
    public void TheNoteARemovalLeaves_IsSaidOnce_AndANoteWhoseDirectoryIsBackIsRemovedUnsaid()
    {
        using var temp = new TempDirectory();
        var fileSystem = new HarnessFactory().FileSystem;
        var gone = temp.Combine("gone", "build", Variant);
        var back = temp.Combine("back", "build", Variant);

        Directory.CreateDirectory(Path.GetDirectoryName(gone)!);
        Directory.CreateDirectory(back);
        ReclaimedBuildNote.Write(fileSystem, gone, "its build directory was removed 2026-09-30 16:32 UTC, to make room for leg 'native' of the main checkout");
        ReclaimedBuildNote.Write(fileSystem, back, "its build directory was removed 2026-09-30 16:32 UTC, to make room for leg 'native' of the main checkout");

        Assert.Equal(
            "its build directory was removed 2026-09-30 16:32 UTC, to make room for leg 'native' of the main checkout",
            ReclaimedBuildNote.Take(fileSystem, gone));
        Assert.Null(ReclaimedBuildNote.Take(fileSystem, gone));
        Assert.Null(ReclaimedBuildNote.Take(fileSystem, back));
        Assert.False(File.Exists(ReclaimedBuildNote.PathOf(gone)));
        Assert.False(File.Exists(ReclaimedBuildNote.PathOf(back)));
        Assert.Equal(Path.Combine(temp.Combine("gone", "build"), "." + Variant + ".reclaimed"), ReclaimedBuildNote.PathOf(gone));
    }

    /// <summary>
    /// The making of room over a file system on which every path's room is on one filesystem - <paramref name="filesystemOf"/>
    /// names another for some - holding 40 GiB, its clock's time the one the notes say.
    /// </summary>
    private static IdleBuilds Idle(Func<string, string>? filesystemOf = null)
    {
        var harness = new HarnessFactory();
        var room = new FilesystemsOf(harness.FileSystem, filesystemOf ?? (_ => "/data"));

        return new IdleBuilds(new RunLock(room, harness.Output, harness.Identity), room, new ManualClock());
    }

    /// <summary>The trees given, each a worktree named by its directory, held by the main checkout's lock, room made for leg native.</summary>
    private static IdleBuildsScope Scope(TempDirectory temp, params (string Root, string Build)[] trees)
        => new([.. trees.Select(tree => new RepositoryTree(tree.Root, $"worktree o1/{Path.GetFileName(tree.Root)}"))], MainLayout(temp), "leg 'native' of the main checkout");

    /// <summary>The repository whose main checkout holds the lock of every tree the test makes.</summary>
    private static HarnessLayout MainLayout(TempDirectory temp)
    {
        Directory.CreateDirectory(temp.Combine("main", HarnessLayout.DirectoryName));

        return new(temp.Combine("main"), temp.Combine("main"));
    }

    /// <summary>A tree named <paramref name="name"/> whose build of the variant holds a file and the record a build writes, written at <paramref name="built"/>.</summary>
    private static (string Root, string Build) Built(TempDirectory temp, string name, DateTime built)
    {
        var root = temp.Combine(name);
        var build = Path.Combine(root, VariantKey.BuildRootName, Variant);

        temp.WriteFile(Path.Combine(name, VariantKey.BuildRootName, Variant, "obj.o"), new string('x', 3000));
        File.SetLastWriteTimeUtc(temp.WriteFile(Path.Combine(name, VariantKey.BuildRootName, Variant, BuildRecord.FileName), "clean\n" + Variant + "\n"), built);

        return (root, build);
    }

    private static long Size(string directory) => new DirectoryInfo(directory).EnumerateFiles("*", SearchOption.AllDirectories).Sum(file => file.Length);

    /// <summary>The real file system, every path's room 40 GiB free on the filesystem <paramref name="filesystemOf"/> names for it.</summary>
    private sealed class FilesystemsOf(IFileSystem inner, Func<string, string> filesystemOf) : PassThroughFileSystem(inner)
    {
        public override DiskSpace SpaceAt(string path) => new(40 * AdmissionKit.Gibibyte, 100 * AdmissionKit.Gibibyte, filesystemOf(path));
    }
}
