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
/// as many as it is short of, none where all of them would not cover the least it lacks, one leg at a time on a
/// filesystem, each removed as <c>clean --legs</c> removes one into an aside of its process's own and leaving a note its
/// tree's next build says - and never one a run or a sweep holds, one whose tree's lock cannot be read, one no build of
/// this tool made, one on another filesystem, or one of the leg's own tree. A consumer's host of 47 GiB, holding five trees' copies of ~16 GiB builds, stopped two legs at the floor
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
        var idle = Idle(temp);
        var older = Built(temp, "older", Older);
        var newer = Built(temp, "newer", Newer);
        var bytes = Size(older.Build);

        var made = idle.MakeRoom(Scope(temp, older, newer), Read, 1, 1)!;

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
        var idle = Idle(temp);
        var newer = Built(temp, "newer", Newer);
        var older = Built(temp, "older", Older);

        var made = idle.MakeRoom(Scope(temp, newer, older), Read, Size(older.Build) + 1, Size(older.Build) + 1)!;

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
        var idle = Idle(temp);
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

        var made = idle.MakeRoom(Scope(temp, held, free) with { Layout = hostCopies ? null : MainLayout(temp) }, Read, 1, 1)!;

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
        var idle = Idle(temp);
        var harness = new HarnessFactory();
        var runLock = new RunLock(harness.FileSystem, harness.Output, harness.Identity);
        var swept = Built(temp, "swept", Older);

        await using var sweeping = (await runLock.TryAcquireAsync(
                MainLayout(temp),
                MutationWorkers.SweepLock(HostId.Local, WorkerFamily.Of(swept.Root, VariantKey.CopyKeyOf(Variant)), RunId.New(), "check-mutations"),
                TestContext.Current.CancellationToken))
            .Handle!;

        var made = idle.MakeRoom(Scope(temp, swept), Read, 1, 1)!;

        Assert.True(Directory.Exists(swept.Build));
        Assert.Empty(made.Removed);
        Assert.StartsWith($"worktree o1/swept's build at '{swept.Build}' was left, as a sweep holds it: ", Assert.Single(made.Left), StringComparison.Ordinal);
        Assert.Equal($"no other tree's idle build on '/data' could be removed to make room; {made.Left[0]}", made.Describe());
    }

    /// <summary>
    /// Only a build directory a build of this tool recorded is one: a directory below build holding no record, an aside an
    /// earlier removal left - named by no process, or by one - and a build on another filesystem than the one short of room
    /// are none - removing one there gives the leg nothing - and where nothing is left to remove, nothing is made.
    /// </summary>
    [Fact]
    public void WhatNoBuildRecorded_AnAside_AndABuildOnAnotherFilesystem_AreNeverRemoved()
    {
        using var temp = new TempDirectory();
        var elsewhere = Built(temp, "elsewhere", Older);
        var idle = Idle(temp, path => path.StartsWith(elsewhere.Root, StringComparison.Ordinal) ? "/other" : "/data");
        var unrecorded = temp.Combine("unrecorded", "build", Variant);
        var aside = temp.Combine("aside", "build", "." + Variant + RemovalAside.Suffix);
        var processes = temp.Combine("aside", "build", "." + Variant + RemovalAside.Suffix + "-4242");

        temp.WriteFile(Path.Combine("unrecorded", "build", Variant, "obj.o"), "object");
        temp.WriteFile(Path.Combine("aside", "build", "." + Variant + RemovalAside.Suffix, BuildRecord.FileName), "clean\n");
        temp.WriteFile(Path.Combine("aside", "build", "." + Variant + RemovalAside.Suffix + "-4242", BuildRecord.FileName), "clean\n");

        var trees = new IdleBuildsScope(
            [new RepositoryTree(elsewhere.Root, "worktree o1/elsewhere"), new RepositoryTree(temp.Combine("unrecorded"), "worktree o1/unrecorded"), new RepositoryTree(temp.Combine("aside"), "worktree o1/aside")],
            MainLayout(temp),
            "leg 'native' of the main checkout",
            Run);

        Assert.Null(idle.MakeRoom(trees, Read, 1L << 40, 1L << 40));
        Assert.True(Directory.Exists(elsewhere.Build));
        Assert.True(Directory.Exists(unrecorded));
        Assert.True(Directory.Exists(aside));
        Assert.True(Directory.Exists(processes));
    }

    /// <summary>
    /// Where every build that could be removed comes to less than the least the leg lacks, none is removed: destroying them
    /// all would still not let it go on. Said, with what they come to and what it lacks.
    /// </summary>
    [Fact]
    public void WhereAllThatCouldBeRemoved_ComesToLessThanTheLeast_NoneIsRemoved()
    {
        using var temp = new TempDirectory();
        var idle = Idle(temp);
        var older = Built(temp, "older", Older);
        var newer = Built(temp, "newer", Newer);
        var all = Size(older.Build) + Size(newer.Build);

        var made = idle.MakeRoom(Scope(temp, older, newer), Read, all + 1, all + 1)!;

        Assert.Empty(made.Removed);
        Assert.Empty(made.Left);
        Assert.True(Directory.Exists(older.Build));
        Assert.True(Directory.Exists(newer.Build));
        Assert.Equal(
            $"no other tree's idle build on '/data' could be removed to make room: what no run or sweep holds there - 2 build(s) - comes to "
                + $"~{DiskSpace.Size(all)}, less than the ~{DiskSpace.Size(all + 1)} the leg lacks, so none of it was removed",
            made.Describe());
    }

    /// <summary>
    /// Covering the least the leg lacks, it removes on towards the most it is to have made: the floor's need beyond the
    /// floor, which the least alone would leave it short of a moment later.
    /// </summary>
    [Fact]
    public void CoveringTheLeast_ItRemovesOnTowardsTheMost()
    {
        using var temp = new TempDirectory();
        var idle = Idle(temp);
        var older = Built(temp, "older", Older);
        var newer = Built(temp, "newer", Newer);

        var made = idle.MakeRoom(Scope(temp, older, newer), Read, 1, Size(older.Build) + 1)!;

        Assert.Equal([older.Build, newer.Build], made.Removed.Select(build => build.Directory));
    }

    /// <summary>
    /// A tree whose lock cannot be read tells neither whether a run holds its build nor a sweep: that build is left, and
    /// said, and the leg goes on to the next - never failing the leg, nor the floor watch it runs in.
    /// </summary>
    [Fact]
    public void ABuildWhoseTreesLockCannotBeRead_IsLeft_AndTheNextIsRemoved()
    {
        using var temp = new TempDirectory();
        var idle = Idle(temp);
        var unread = Built(temp, "unread", Older);
        var free = Built(temp, "free", Newer);
        temp.WriteFile(Path.Combine("unread", HarnessLayout.DirectoryName, "lock.json"), "{ not json");

        var made = idle.MakeRoom(Scope(temp, unread, free) with { Layout = null }, Read, 1, 1)!;

        Assert.True(Directory.Exists(unread.Build));
        Assert.Equal([free.Build], made.Removed.Select(build => build.Directory));
        Assert.StartsWith($"worktree o1/unread's build at '{unread.Build}' was left, as its tree's lock could not be read: The run lock '", Assert.Single(made.Left), StringComparison.Ordinal);
    }

    /// <summary>
    /// One leg makes room on a filesystem at a time: a leg that finds another making room there removes nothing beside it,
    /// and is told who is; once that one has given its making back, the next makes room.
    /// </summary>
    [Fact]
    public void ALegFindingAnotherMakingRoomThere_RemovesNothing_AndIsToldWhoIs()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var slots = AdmissionKit.Slots(harness, temp.Combine("admission.json"));
        var idle = Idle(temp, slots: slots);
        var older = Built(temp, "older", Older);

        ReclaimedRoom made;

        using (var other = slots.MakeRoomOn(Run, "leg 'native' of worktree o1/other", "/data"))
        {
            Assert.True(other.Held);

            made = idle.MakeRoom(Scope(temp, older), Read, 1, 1)!;
        }

        var behind = made.Behind!;

        Assert.Equal("leg 'native' of worktree o1/other", behind.For);
        Assert.Empty(made.Removed);
        Assert.True(Directory.Exists(older.Build));
        Assert.Equal(
            $"{behind.Describe()} is making room on '/data' from other trees' idle builds, so this leg removes none beside it, and reads the room "
                + "again once it has",
            made.Describe());
        Assert.Equal([older.Build], idle.MakeRoom(Scope(temp, older), Read, 1, 1)!.Removed.Select(build => build.Directory));
    }

    /// <summary>
    /// The room is read again once the making is held: what another leg made there since the leg read the room is room it
    /// no longer lacks, and nothing is removed for it.
    /// </summary>
    [Fact]
    public void RoomMadeSinceTheLegReadIt_CountsAsMade()
    {
        using var temp = new TempDirectory();
        var idle = Idle(temp);
        var older = Built(temp, "older", Older);

        Assert.Null(idle.MakeRoom(Scope(temp, older), Read with { FreeBytes = Read.FreeBytes - 100 }, 100, 100));
        Assert.True(Directory.Exists(older.Build));
    }

    /// <summary>
    /// A removal moves a build into an aside of its own process's, and deletes only that: an aside a process still running
    /// is removing is never touched nor counted, while one an ended process left, or a build before this one named by no
    /// process, is taken over and removed with the build.
    /// </summary>
    [Fact]
    public void ARemoval_TakesOverWhatEndedRemovalsLeftAside_AndNeverOneARunningProcessIsRemoving()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var identity = new Processes(harness.Identity, alive: 4242);
        var removal = new BuildDirectoryRemoval(new RunLock(harness.FileSystem, harness.Output, identity), harness.FileSystem);
        var tree = Built(temp, "tree", Older);
        var running = Aside(temp, "tree", "-4242", 100);
        var ended = Aside(temp, "tree", "-4343", 200);
        var older = Aside(temp, "tree", string.Empty, 300);
        var build = Size(tree.Build);

        Assert.Equal([older, ended], removal.LeftAside(tree.Build));

        var removed = removal.Remove(MainLayout(temp), tree.Root, tree.Build, PlacedLeg.BuildLock(HostId.Local, tree.Root, Variant, RunId.New(), "clean"));

        Assert.Equal(new BuildDirectoryRemoved(build + 300 + 200, Moved: true, HeldBy: null), removed);
        Assert.False(Directory.Exists(tree.Build));
        Assert.False(Directory.Exists(ended));
        Assert.False(Directory.Exists(older));
        Assert.False(Directory.Exists(removal.AsideOf(tree.Build)));
        Assert.Equal(100, Size(running));
    }

    /// <summary>
    /// A removal's aside is named by its process's id and stamp, and read back by both: one an ended removal left under an id
    /// a running process carries now - under another stamp - is taken over, as is one naming this process's own id under
    /// another stamp, while one the running holder of the id named stays its own, and one named by that id alone, which
    /// cannot be told from it, is never taken. What the removal removes besides the directory is exactly what a dry run
    /// lists, this process's own aside first. Named by the id alone, a crashed removal's aside was passed over for as long
    /// as any process carried its id, and the dry run never listed this process's own.
    /// </summary>
    [Fact]
    public void AnAsideUnderAReusedId_IsTakenOver_AndTheDryRunListsWhatTheRemovalRemoves()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var identity = new StampedProcesses(harness.Identity, 4242, "1001");
        var removal = new BuildDirectoryRemoval(new RunLock(harness.FileSystem, harness.Output, identity), harness.FileSystem);
        var tree = Built(temp, "tree", Older);
        var own = Path.GetFileName(removal.AsideOf(tree.Build))[("." + Variant + RemovalAside.Suffix).Length..];
        var mine = Aside(temp, "tree", own, 50);
        var reused = Aside(temp, "tree", "-4242-1000", 200);
        var myIdBefore = Aside(temp, "tree", $"-{harness.Identity.CurrentId}-999", 300);
        var running = Aside(temp, "tree", "-4242-1001", 400);
        var byIdAlone = Aside(temp, "tree", "-4242", 500);
        var build = Size(tree.Build);

        Assert.Equal([mine, .. new[] { reused, myIdBefore }.Order(StringComparer.Ordinal)], removal.LeftAside(tree.Build));

        var removed = removal.Remove(MainLayout(temp), tree.Root, tree.Build, PlacedLeg.BuildLock(HostId.Local, tree.Root, Variant, RunId.New(), "clean"));

        Assert.Equal(new BuildDirectoryRemoved(build + 50 + 200 + 300, Moved: true, HeldBy: null), removed);
        Assert.Equal([false, false, false, false], new[] { tree.Build, mine, reused, myIdBefore }.Select(Directory.Exists));
        Assert.Equal((400, 500), (Size(running), Size(byIdAlone)));
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
            Run,
            StringComparison.Ordinal);

        Assert.Equal([main, theirs, theirCopy], scope.Trees);
        Assert.Equal("leg 'native' of worktree o1/mine's leg copy", scope.For);
        Assert.Equal(Run, scope.RunId);
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

    /// <summary>The run the room is made for.</summary>
    private static readonly string Run = RunId.New().Value;

    /// <summary>The room the leg read on the filesystem short of it: what every path there holds.</summary>
    private static readonly DiskSpace Read = new(40 * AdmissionKit.Gibibyte, 100 * AdmissionKit.Gibibyte, "/data");

    /// <summary>
    /// The making of room over a file system on which every path's room is on one filesystem - <paramref name="filesystemOf"/>
    /// names another for some - holding 40 GiB, its clock's time the one the notes say, and the heavy-leg records the test's
    /// own, or <paramref name="slots"/>.
    /// </summary>
    private static IdleBuilds Idle(TempDirectory temp, Func<string, string>? filesystemOf = null, HeavyLegSlots? slots = null)
    {
        var harness = new HarnessFactory();
        var room = new FilesystemsOf(harness.FileSystem, filesystemOf ?? (_ => "/data"));

        return new IdleBuilds(new RunLock(room, harness.Output, harness.Identity), room, slots ?? AdmissionKit.Slots(harness, temp.Combine("admission.json")), new ManualClock());
    }

    /// <summary>The trees given, each a worktree named by its directory, held by the main checkout's lock, room made for leg native.</summary>
    private static IdleBuildsScope Scope(TempDirectory temp, params (string Root, string Build)[] trees)
        => new([.. trees.Select(tree => new RepositoryTree(tree.Root, $"worktree o1/{Path.GetFileName(tree.Root)}"))], MainLayout(temp), "leg 'native' of the main checkout", Run);

    /// <summary>An aside of tree <paramref name="name"/>'s build, named as <paramref name="by"/> says after the suffix, holding <paramref name="bytes"/>.</summary>
    private static string Aside(TempDirectory temp, string name, string by, int bytes)
    {
        temp.WriteFile(Path.Combine(name, VariantKey.BuildRootName, "." + Variant + RemovalAside.Suffix + by, "left.o"), new string('l', bytes));

        return temp.Combine(name, VariantKey.BuildRootName, "." + Variant + RemovalAside.Suffix + by);
    }

    /// <summary>This machine's processes as <paramref name="real"/> tells them, but for the ids <paramref name="alive"/> names, alive, and every other ended.</summary>
    private sealed class Processes(IProcessIdentity real, params int[] alive) : IProcessIdentity
    {
        public int CurrentId => real.CurrentId;

        public string CurrentMachine => real.CurrentMachine;

        public string? Current => real.Current;

        public bool IsAlive(int processId, string? stamp) => processId == real.CurrentId || alive.Contains(processId);

        public string? StampOf(int processId) => processId == real.CurrentId ? real.StampOf(processId) : null;

        public bool IsSame(int processId, string stamp) => processId == real.CurrentId && real.IsSame(processId, stamp);
    }

    /// <summary>
    /// This machine's processes as <paramref name="real"/> tells them, but for <paramref name="id"/>, carried now by a process
    /// started as <paramref name="stamp"/> says - one recorded under that stamp, or under none, running, and one recorded
    /// under another ended, as the real identity tells them - and every other id ended.
    /// </summary>
    private sealed class StampedProcesses(IProcessIdentity real, int id, string stamp) : IProcessIdentity
    {
        public int CurrentId => real.CurrentId;

        public string CurrentMachine => real.CurrentMachine;

        public string? Current => real.Current;

        public bool IsAlive(int processId, string? recorded)
            => processId == real.CurrentId
                ? real.IsAlive(processId, recorded)
                : processId == id && (recorded is null || string.Equals(recorded, stamp, StringComparison.Ordinal));

        public string? StampOf(int processId) => processId == id ? stamp : processId == real.CurrentId ? real.StampOf(processId) : null;

        public bool IsSame(int processId, string recorded)
            => processId == id ? string.Equals(recorded, stamp, StringComparison.Ordinal) : processId == real.CurrentId && real.IsSame(processId, recorded);
    }

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
