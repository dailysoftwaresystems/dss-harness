using System.Globalization;
using RepoHarness.Core.Build;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.Execution;
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
    /// The workers of a variant are listed by number, each saying what it holds and whether a sync made it: another
    /// variant's, a name that is no worker's, and the tree's other copies are none of them.
    /// </summary>
    [Fact]
    public async Task TheWorkersOfAVariant_AreListedByNumber_SayingWhetherASyncMadeEach()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var (harness, tree) = await PrepareAsync(temp, cancellationToken);
        var copies = Copies(harness);
        var reading = await SyncKit.Service(harness).ReadSourceAsync(tree, cancellationToken);

        await copies.SyncAsync(reading, MutationWorkers.PathOf(tree, Variant, 2), cancellationToken);
        await copies.SyncAsync(reading, MutationWorkers.PathOf(tree, Variant, 1), cancellationToken);
        await copies.SyncAsync(reading, MutationWorkers.PathOf(tree, Variant with { Sanitizer = "asan" }, 1), cancellationToken);
        temp.WriteFile(Path.Combine(Path.GetFileName(MutationWorkers.PathOf(tree, Variant, 3)), "notes.txt"), "mine");
        Directory.CreateDirectory(MutationWorkers.RootOf(tree, Variant) + "-spare");
        Directory.CreateDirectory(tree + ".worktree-other");

        var workers = await copies.ListAsync(tree, Variant, cancellationToken);

        Assert.Equal([1, 2, 3], workers.Select(worker => worker.Number));
        Assert.Equal([true, true, false], workers.Select(worker => worker.Made));
        Assert.Equal(MutationWorkers.PathOf(tree, Variant, 1), workers[0].Path);
        Assert.True(workers[0].Bytes > 0);
        Assert.Equal(4, workers[2].Bytes);
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

    private static WorkerCopies Copies(HarnessFactory harness)
        => new(SyncKit.Service(harness), SyncKit.Transport(harness), harness.FileSystem, harness.Output, harness.Identity, MutationService.CommandName);

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
