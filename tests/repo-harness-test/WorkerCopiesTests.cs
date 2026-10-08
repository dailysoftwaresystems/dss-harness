using System.Globalization;
using RepoHarness.Core.Build;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.Execution;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Mutations;
using RepoHarness.Core.Results;
using RepoHarness.Core.Sync;

namespace RepoHarness.Tests;

/// <summary>
/// The copies a leg's sweep keeps its workers in, against a real tree and real copies beside it: made and synced again by
/// the sync that makes a host's copy, listed as a family of their own, claimed for one sweep at a time without being
/// made, and removed only where a sync made them, their claims with them.
/// </summary>
public sealed class WorkerCopiesTests
{
    private static readonly VariantKey Variant = new("x86_64", "gcc", "debug", null);

    /// <summary>
    /// A worker is made the tree as the sweep read it, a repository of its own; synced again from a later reading, it puts
    /// back a site a sweep killed part way left mutated, and keeps its build directory warm.
    /// </summary>
    [Fact]
    public async Task AWorker_IsMadeTheTreeAsRead_AndSyncedAgainPutsBackAMutatedSite_KeepingItsBuild()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var (harness, tree) = await PrepareAsync(temp, cancellationToken);
        var copies = Copies(harness);
        var service = SyncKit.Service(harness);
        var worker = MutationWorkers.PathOf(tree, Variant, 1);

        await copies.SyncAsync(await service.ReadSourceAsync(tree, cancellationToken), worker, cancellationToken);

        Assert.Equal("a\n", await File.ReadAllTextAsync(Path.Combine(worker, "src", "a.c"), cancellationToken));
        Assert.True(Directory.Exists(Path.Combine(worker, ".git")), "The worker is not a repository of its own.");

        var built = Path.Combine(Variant.DirectoryUnder(worker), "a.o");

        Directory.CreateDirectory(Path.GetDirectoryName(built)!);
        await File.WriteAllTextAsync(built, "object", cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(worker, "src", "a.c"), "mutant\n", cancellationToken);

        await copies.SyncAsync(await service.ReadSourceAsync(tree, cancellationToken), worker, cancellationToken);

        Assert.Equal("a\n", await File.ReadAllTextAsync(Path.Combine(worker, "src", "a.c"), cancellationToken));
        Assert.Equal("object", await File.ReadAllTextAsync(built, cancellationToken));
    }

    /// <summary>A directory where a worker would be that no sync made is somebody's: the sync refuses to make a worker of it.</summary>
    [Fact]
    public async Task ADirectoryNoSyncMade_IsNeverMadeAWorker()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var (harness, tree) = await PrepareAsync(temp, cancellationToken);
        var worker = MutationWorkers.PathOf(tree, Variant, 1);

        Directory.CreateDirectory(worker);
        await File.WriteAllTextAsync(Path.Combine(worker, "notes.txt"), "mine", cancellationToken);

        var refusal = await Assert.ThrowsAsync<HarnessException>(
            async () => await Copies(harness).SyncAsync(await SyncKit.Service(harness).ReadSourceAsync(tree, cancellationToken), worker, cancellationToken));

        Assert.Equal(HarnessExit.Refused, refusal.ExitCode);
        Assert.Equal("mine", await File.ReadAllTextAsync(Path.Combine(worker, "notes.txt"), cancellationToken));
    }

    /// <summary>
    /// The workers of a family are listed by number, each saying what it holds and whether a sync made it: another
    /// variant's, the variant's self-test's, a name that is no worker's, and the tree's other copies are none of them -
    /// and the self-test's family lists its own alone.
    /// </summary>
    [Fact]
    public async Task TheWorkersOfAFamily_AreListedByNumber_SayingWhetherASyncMadeEach()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var (harness, tree) = await PrepareAsync(temp, cancellationToken);
        var copies = Copies(harness);
        var reading = await SyncKit.Service(harness).ReadSourceAsync(tree, cancellationToken);
        var selfTest = MutationWorkers.Of(tree, Variant, selfTest: true);

        await copies.SyncAsync(reading, MutationWorkers.PathOf(tree, Variant, 2), cancellationToken);
        await copies.SyncAsync(reading, MutationWorkers.PathOf(tree, Variant, 1), cancellationToken);
        await copies.SyncAsync(reading, MutationWorkers.PathOf(tree, Variant with { Sanitizer = "asan" }, 1), cancellationToken);
        await copies.SyncAsync(reading, selfTest.PathOf(1), cancellationToken);
        temp.WriteFile(Path.Combine(Path.GetFileName(MutationWorkers.PathOf(tree, Variant, 3)), "notes.txt"), "mine");
        Directory.CreateDirectory(MutationWorkers.PathOf(tree, Variant, 10));
        Directory.CreateDirectory(MutationWorkers.Of(tree, Variant).Root + "-spare");
        Directory.CreateDirectory(tree + ".worktree-other");

        var workers = await copies.ListAsync(MutationWorkers.Of(tree, Variant), cancellationToken);

        // By number, as it counts: the tenth after the third, where its name sorts it after the first.
        Assert.Equal([1, 2, 3, 10], workers.Select(worker => worker.Number));
        Assert.All(workers, worker => Assert.Equal(Variant.DirectoryName, worker.Family));
        Assert.Equal([true, true, false, false], workers.Select(worker => worker.Made));
        Assert.Equal(MutationWorkers.PathOf(tree, Variant, 1), workers[0].Path);
        Assert.True(workers[0].Bytes > 0);
        Assert.Equal(4, workers[2].Bytes);

        var own = Assert.Single(await copies.ListAsync(selfTest, cancellationToken));

        Assert.Equal((selfTest.Name, 1, selfTest.PathOf(1)), (own.Family, own.Number, own.Path));
    }

    /// <summary>
    /// Every worker kept beside a tree is listed by its family and then its number, of whichever variant and a
    /// self-test's among them - or those of the families asked for alone, no other weighed; a name spelt as no worker's
    /// is none.
    /// </summary>
    [Fact]
    public async Task TheWorkersBesideATree_AreListedByFamilyThenNumber_OfTheFamiliesAskedFor()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var (harness, tree) = await PrepareAsync(temp, cancellationToken);
        var weighed = new Weighs(harness.FileSystem);
        var copies = new WorkerCopies(SyncKit.Service(harness), SyncKit.Transport(harness, weighed), harness.FileSystem, harness.Output, harness.Identity, MutationService.CommandName);
        var reading = await SyncKit.Service(harness).ReadSourceAsync(tree, cancellationToken);
        var sanitized = Variant with { Sanitizer = "asan" };
        var selfTest = MutationWorkers.Of(tree, Variant, selfTest: true);

        await copies.SyncAsync(reading, MutationWorkers.PathOf(tree, Variant, 2), cancellationToken);
        await copies.SyncAsync(reading, MutationWorkers.PathOf(tree, Variant, 1), cancellationToken);
        await copies.SyncAsync(reading, MutationWorkers.PathOf(tree, sanitized, 1), cancellationToken);
        await copies.SyncAsync(reading, selfTest.PathOf(1), cancellationToken);
        Directory.CreateDirectory(MutationWorkers.Of(tree, Variant).Root + "-spare");
        Directory.CreateDirectory(MutationWorkers.Of(tree, Variant).Root + "-01");
        weighed.Weighed.Clear();

        var every = await copies.ListBesideAsync(tree, _ => true, cancellationToken);

        Assert.Equal(
            [(selfTest.Name, 1), (Variant.DirectoryName, 1), (Variant.DirectoryName, 2), (sanitized.DirectoryName, 1)],
            every.Select(worker => (worker.Family, worker.Number)));
        Assert.Equal(4, weighed.Weighed.Count);

        weighed.Weighed.Clear();

        var asked = await copies.ListBesideAsync(tree, family => family == sanitized.DirectoryName, cancellationToken);

        Assert.Equal([MutationWorkers.PathOf(tree, sanitized, 1)], asked.Select(worker => worker.Path));
        Assert.Equal([Path.GetFileName(MutationWorkers.PathOf(tree, sanitized, 1))], weighed.Weighed.Select(Path.GetFileName));
    }

    /// <summary>
    /// A claim that cannot be read holds its worker, naming its file: whatever asks - a clean of the leg, a sweep removing
    /// a worker it no longer runs - is told the worker is held, and is never ended by the asking.
    /// </summary>
    [Fact]
    public async Task AClaimThatCannotBeRead_HoldsItsWorker_NamingItsFile()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var (harness, tree) = await PrepareAsync(temp, cancellationToken);
        var copies = Copies(harness);
        var worker = MutationWorkers.PathOf(tree, Variant, 1);
        var claim = worker + MutationWorkers.ClaimSuffix;

        await File.WriteAllTextAsync(claim, "{ this is no claim", cancellationToken);

        var held = copies.HeldBy(worker);

        Assert.NotNull(held);
        Assert.StartsWith($"The mutation worker's claim file '{claim}' could not be read: ", held, StringComparison.Ordinal);
        Assert.EndsWith("Remove it once no sweep is using that worker.", held, StringComparison.Ordinal);
    }

    /// <summary>
    /// A worker is claimed for one sweep at a time, without being made: another sweep is refused while the first holds it,
    /// naming it, and takes it once the first gives it up.
    /// </summary>
    [Fact]
    public async Task AWorker_IsClaimedForOneSweepAtATime_WithoutBeingMade()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var (harness, tree) = await PrepareAsync(temp, cancellationToken);
        var copies = Copies(harness);
        var worker = MutationWorkers.PathOf(tree, Variant, 1);
        var first = RunId.New();
        var second = RunId.New();

        Assert.True(copies.Claim(worker, first, force: false).Taken);
        Assert.False(Directory.Exists(worker));
        Assert.Contains(first.Value, copies.HeldBy(worker), StringComparison.Ordinal);

        var refused = copies.Claim(worker, second, force: false);

        Assert.False(refused.Taken);
        Assert.Contains(first.Value, refused.HeldBy, StringComparison.Ordinal);

        copies.Release(worker, first);

        Assert.Null(copies.HeldBy(worker));
        Assert.True(copies.Claim(worker, second, force: false).Taken);
    }

    /// <summary>A worker a sweep on this machine died holding is released and said; one nobody claimed releases nothing.</summary>
    [Fact]
    public async Task AWorkerASweepDiedHolding_IsReleased_AndOneNobodyClaimedReleasesNothing()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var (harness, tree) = await PrepareAsync(temp, cancellationToken);
        var copies = Copies(harness);
        var worker = MutationWorkers.PathOf(tree, Variant, 1);

        File.WriteAllText(
            worker + MutationWorkers.ClaimSuffix,
            "{ \"machine\": \"" + Environment.MachineName + "\", \"processId\": " + (int.MaxValue - 1).ToString(CultureInfo.InvariantCulture)
            + ", \"processStamp\": \"gone\", \"runId\": \"20250101-120000-deadbeef\", \"takenUtc\": \"2025-01-01T12:00:00Z\" }");

        // A claim whose sweep is gone holds the worker from nobody, released or not.
        Assert.Null(copies.HeldBy(worker));
        Assert.Equal("20250101-120000-deadbeef", copies.ReleaseAbandoned(worker)?.RunId);
        Assert.False(File.Exists(worker + MutationWorkers.ClaimSuffix));
        Assert.Null(copies.ReleaseAbandoned(MutationWorkers.PathOf(tree, Variant, 2)));
        Assert.Contains("An earlier run was abandoned", harness.StandardError.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// A worker a sync made is removed with its claim; one gone already is absent, its claim removed with it; a directory
    /// no sync made is no copy, and is left with any claim beside it.
    /// </summary>
    [Fact]
    public async Task AWorkerIsRemovedWithItsClaim_OnlyWhereASyncMadeIt()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var (harness, tree) = await PrepareAsync(temp, cancellationToken);
        var copies = Copies(harness);
        var made = MutationWorkers.PathOf(tree, Variant, 1);
        var gone = MutationWorkers.PathOf(tree, Variant, 2);
        var somebodys = MutationWorkers.PathOf(tree, Variant, 3);

        await copies.SyncAsync(await SyncKit.Service(harness).ReadSourceAsync(tree, cancellationToken), made, cancellationToken);
        Directory.CreateDirectory(somebodys);
        await File.WriteAllTextAsync(Path.Combine(somebodys, "notes.txt"), "mine", cancellationToken);

        foreach (var worker in new[] { made, gone, somebodys })
        {
            await File.WriteAllTextAsync(worker + MutationWorkers.ClaimSuffix, "{}", cancellationToken);
        }

        Assert.Equal(CopyRemoval.Removed, await copies.RemoveAsync(made, cancellationToken));
        Assert.Equal(CopyRemoval.Absent, await copies.RemoveAsync(gone, cancellationToken));
        Assert.Equal(CopyRemoval.NotACopy, await copies.RemoveAsync(somebodys, cancellationToken));

        Assert.False(Directory.Exists(made));
        Assert.False(File.Exists(made + MutationWorkers.ClaimSuffix));
        Assert.False(File.Exists(gone + MutationWorkers.ClaimSuffix));
        Assert.True(File.Exists(Path.Combine(somebodys, "notes.txt")));
        Assert.True(File.Exists(somebodys + MutationWorkers.ClaimSuffix));
    }

    /// <summary>
    /// A claim that cannot be removed with its worker claims nothing once the worker is gone: it is said, with its
    /// file, and the removal that went through is still the answer.
    /// </summary>
    [Fact]
    public async Task AClaimThatCannotBeRemovedWithItsWorker_IsSaid_AndTheWorkerIsStillRemoved()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var (harness, tree) = await PrepareAsync(temp, cancellationToken);
        var worker = MutationWorkers.PathOf(tree, Variant, 1);
        var claim = worker + MutationWorkers.ClaimSuffix;

        await Copies(harness).SyncAsync(await SyncKit.Service(harness).ReadSourceAsync(tree, cancellationToken), worker, cancellationToken);
        await File.WriteAllTextAsync(claim, "{}", cancellationToken);

        var stuck = new KeepsFile(harness.FileSystem, claim);
        var copies = new WorkerCopies(SyncKit.Service(harness), SyncKit.Transport(harness), stuck, harness.Output, harness.Identity, MutationService.CommandName);

        Assert.Equal(CopyRemoval.Removed, await copies.RemoveAsync(worker, cancellationToken));
        Assert.False(Directory.Exists(worker));
        Assert.Contains(
            $"check-mutations: WARN - The claim file '{claim}' of a mutation worker that was removed could not be removed, and is yours to remove: it is held.",
            harness.StandardError.ToString(),
            StringComparison.Ordinal);
    }

    /// <summary>A disk that will not delete one file.</summary>
    private sealed class KeepsFile(IFileSystem inner, string kept) : PassThroughFileSystem(inner)
    {
        public override void DeleteFile(string path)
        {
            if (string.Equals(Path.GetFullPath(path), Path.GetFullPath(kept), StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("it is held.");
            }

            base.DeleteFile(path);
        }
    }

    private static WorkerCopies Copies(HarnessFactory harness)
        => new(SyncKit.Service(harness), SyncKit.Transport(harness), harness.FileSystem, harness.Output, harness.Identity, MutationService.CommandName);

    /// <summary>A disk that keeps which directories were weighed.</summary>
    private sealed class Weighs(IFileSystem inner) : PassThroughFileSystem(inner)
    {
        public List<string> Weighed { get; } = [];

        public override long DirectorySize(string path)
        {
            Weighed.Add(path);
            return base.DirectorySize(path);
        }
    }

    /// <summary>A tree in a repository of its own, in <paramref name="temp"/>, so the workers beside it are in it too.</summary>
    private static async Task<(HarnessFactory Harness, string Tree)> PrepareAsync(TempDirectory temp, CancellationToken cancellationToken)
    {
        var harness = new HarnessFactory();
        var tree = temp.Combine("tree");

        temp.WriteFile(Path.Combine("tree", "src", "a.c"), "a\n");
        temp.WriteFile(Path.Combine("tree", "src", "b.c"), "b\n");
        temp.WriteFile(Path.Combine("tree", ".gitignore"), "build/\n");

        await harness.InitializeHarnessAsync(tree, cancellationToken, new HarnessConfig());
        await harness.CommitAllAsync(tree, "initial", cancellationToken);

        return (harness, tree);
    }
}
