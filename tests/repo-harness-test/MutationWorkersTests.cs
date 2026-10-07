using System.Globalization;
using RepoHarness.Core.Build;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.Execution;
using RepoHarness.Core.Hosts;
using RepoHarness.Core.Legs;
using RepoHarness.Core.Mutations;
using RepoHarness.Core.Repository;
using RepoHarness.Core.Runs;
using RepoHarness.Core.Sync;

namespace RepoHarness.Tests;

/// <summary>
/// Where a leg's mutation workers are kept, the lock a sweep of the leg takes, and the claim a sweep holds on each
/// worker: copies beside the tree, numbered per variant; a lock no build of the leg meets; and a claim that never makes
/// the copy it claims, released and said where a sweep died holding it.
/// </summary>
public sealed class MutationWorkersTests
{
    private static readonly VariantKey Variant = new("x86_64", "gcc", "debug", null);

    /// <summary>
    /// A worker is a copy beside the tree, named for the variant and its number, spelt as the tree is - this machine's
    /// way, or a host's - and a sweep is locked by the same name with no number, which no worker is.
    /// </summary>
    [Theory]
    [InlineData("/home/pi/repo", "/home/pi/repo.mutation-x86_64-gcc-debug")]
    [InlineData("/home/pi/repo/", "/home/pi/repo.mutation-x86_64-gcc-debug")]
    [InlineData("C:/src/repo", "C:/src/repo.mutation-x86_64-gcc-debug")]
    public void AWorker_IsACopyBesideTheTree_NamedForItsVariantAndNumber(string tree, string root)
    {
        Assert.Equal(root, MutationWorkers.RootOf(tree, Variant));
        Assert.Equal(root + "-1", MutationWorkers.PathOf(tree, Variant, 1));
        Assert.Equal(root + "-12", MutationWorkers.PathOf(tree, Variant, 12));
        Assert.Throws<ArgumentOutOfRangeException>(() => MutationWorkers.PathOf(tree, Variant, 0));
    }

    /// <summary>
    /// A listed copy is a worker of the variant only where its name is the variant's and a number from 1: another
    /// variant's, whose name starts alike and goes on, a number spelt otherwise, or no number, is none of its workers.
    /// </summary>
    [Theory]
    [InlineData("x86_64-gcc-debug-1", 1)]
    [InlineData("x86_64-gcc-debug-23", 23)]
    [InlineData("x86_64-gcc-debug-asan-1", null)]
    [InlineData("x86_64-gcc-debug-01", null)]
    [InlineData("x86_64-gcc-debug-0", null)]
    [InlineData("x86_64-gcc-debug-+1", null)]
    [InlineData("x86_64-gcc-debug-", null)]
    [InlineData("x86_64-gcc-debug", null)]
    [InlineData("x86_64-gcc-debug-99999999999", null)]
    [InlineData("arm64-gcc-debug-1", null)]
    public void AListedCopy_IsAWorkerOfTheVariant_OnlyByItsNameAndANumber(string name, int? number)
        => Assert.Equal(number, MutationWorkers.NumberOf(name, Variant));

    /// <summary>
    /// A sweep's lock is its workers' key, whole: a build holding the leg's tree and variant never meets it, and another
    /// sweep of the leg does, as does a clean of its workers.
    /// </summary>
    [Fact]
    public async Task ASweepsLock_NeverMeetsABuildOfTheLeg_AndRefusesAnotherSweep()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var runLock = new RunLock(harness.FileSystem, harness.Output, harness.Identity);
        var layout = new HarnessLayout(temp.Path, temp.Path);
        var cancellationToken = TestContext.Current.CancellationToken;
        var tree = temp.Combine("tree");

        await using var build = await runLock.AcquireAsync(layout, PlacedLeg.BuildLock(HostId.Local, tree, Variant, RunId.New(), "build"), cancellationToken);

        var sweep = MutationWorkers.SweepLock(HostId.Local, tree, Variant, RunId.New(), "check-mutations");

        var first = await runLock.TryAcquireAsync(layout, sweep, cancellationToken);
        var second = await runLock.TryAcquireAsync(layout, MutationWorkers.SweepLock(HostId.Local, tree, Variant, RunId.New(), "check-mutations"), cancellationToken);

        await using (first.Handle)
        {
            Assert.NotNull(first.Handle);
            Assert.Null(second.Handle);
            Assert.Contains(sweep.RunId.Value, second.HeldBy, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// A claim on a worker records its owner beside the copy and never makes the copy, which the sync that makes it would
    /// then refuse as a directory it did not make; a live sweep holding it refuses another, naming it.
    /// </summary>
    [Fact]
    public void AClaimOnAWorker_MakesNoCopy_AndALiveSweepHoldsItFromAnother()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var claims = new DirectoryClaims(harness.FileSystem, harness.Output, harness.Identity, MutationWorkers.Claims("check-mutations"));
        var worker = temp.Combine("repo.mutation-x86_64-gcc-debug-1");
        var mine = RunId.New();

        var claim = claims.Claim(worker, mine, force: false);
        var other = claims.Claim(worker, RunId.New(), force: false);

        Assert.True(claim.Taken);
        Assert.False(Directory.Exists(worker));
        Assert.True(File.Exists(worker + MutationWorkers.ClaimSuffix));
        Assert.False(other.Taken);
        Assert.Contains(mine.Value, other.HeldBy, StringComparison.Ordinal);

        claims.Release(worker, mine);

        Assert.False(File.Exists(worker + MutationWorkers.ClaimSuffix));
    }

    /// <summary>
    /// A worker a sweep on this machine died holding is released before it is claimed again, and said in a worker's terms
    /// - its copy may still hold a mutated site, which the next sync puts back; one a live sweep holds is left as it is.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AWorkerASweepDiedHolding_IsReleasedAndSaid_AndOneStillHeldIsLeft(bool copyKept)
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var claims = new DirectoryClaims(harness.FileSystem, harness.Output, harness.Identity, MutationWorkers.Claims("check-mutations"));
        var dead = temp.Combine("repo.mutation-x86_64-gcc-debug-1");
        var live = temp.Combine("repo.mutation-x86_64-gcc-debug-2");

        if (copyKept)
        {
            Directory.CreateDirectory(dead);
        }

        WriteOwner(dead, int.MaxValue - 1, "a-process-that-has-gone");
        claims.Claim(live, RunId.New(), force: false);

        var released = claims.ReleaseAbandoned(dead);
        var left = claims.ReleaseAbandoned(live);
        var said = harness.StandardError.ToString();

        Assert.Equal("20250101-120000-deadbeef", released?.RunId);
        Assert.Null(left);
        Assert.False(File.Exists(dead + MutationWorkers.ClaimSuffix));
        Assert.True(File.Exists(live + MutationWorkers.ClaimSuffix));
        Assert.Contains("check-mutations: WARN - An earlier run was abandoned", said, StringComparison.Ordinal);
        Assert.Contains(
            copyKept ? $"never gave up the mutation worker at '{dead}'" : $"never gave up '{dead}', which is gone",
            said,
            StringComparison.Ordinal);
        Assert.Contains("so a site it had mutated there may still be mutated, until the worker's copy is synced again", said, StringComparison.Ordinal);
        Assert.Contains("Its claim is released.", said, StringComparison.Ordinal);
        Assert.Null(claims.ReleaseAbandoned(temp.Combine("never-claimed")));
    }

    /// <summary>
    /// A retargeted project builds the targets it is given, witnessed by what they make, and is in every other way the
    /// project it was: each of its settings carried over, whatever settings a project gains.
    /// </summary>
    [Fact]
    public void ARetargetedProject_BuildsItsTargets_WitnessedByTheirOutputs_AndIsOtherwiseTheProject()
    {
        var project = new ProjectConfig
        {
            Name = "app",
            Type = "cmake",
            Path = "native",
            Targets = ["all_tests"],
            BuildOutputs = [BuildOutput.Everywhere("bin/app")],
            Env = { ["CC"] = "gcc" },
            CacheVars = { ["FOO"] = "1" },
            DefaultToolchain = { ["linux"] = "gcc" },
            RebuildableFormats = [".cpp"],
            Test = new TestConfig(),
        };

        var retargeted = project.Retargeted(["fixture"], ["bin/fixture"]);

        Assert.Equal(["fixture"], retargeted.Targets);
        Assert.Equal(["bin/fixture"], retargeted.BuildOutputs.Select(output => output.For("linux")));

        foreach (var property in typeof(ProjectConfig).GetProperties().Where(property => property.Name is not (nameof(ProjectConfig.Targets) or nameof(ProjectConfig.BuildOutputs))))
        {
            Assert.Same(property.GetValue(project), property.GetValue(retargeted));
        }

        Assert.Throws<ArgumentException>(() => project.Retargeted([], []));
    }

    /// <summary>
    /// A project configured with cache variables beneath its own is given each it sets itself as it sets it - its spelling
    /// and its value, whatever case one given beneath is in - and each other given, and is in every other way the project
    /// it was: each of its settings carried over, whatever settings a project gains.
    /// </summary>
    [Fact]
    public void CacheVarsBeneathAProjectsOwn_AreOutrankedByItsOwn_AndItIsOtherwiseTheProject()
    {
        var project = new ProjectConfig
        {
            Name = "app",
            Type = "cmake",
            Path = "native",
            Targets = ["all_tests"],
            BuildOutputs = [BuildOutput.Everywhere("bin/app")],
            Env = { ["CC"] = "gcc" },
            CacheVars = { ["FetchContent_Source_Dir_Json"] = "/mine/json", ["FOO"] = "1" },
            DefaultToolchain = { ["linux"] = "gcc" },
            RebuildableFormats = [".cpp"],
            Test = new TestConfig(),
        };

        var beneath = project.WithCacheVarsBeneath(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["FETCHCONTENT_SOURCE_DIR_JSON"] = "/build/_deps/json-src",
            ["FETCHCONTENT_SOURCE_DIR_GOOGLETEST"] = "/build/_deps/googletest-src",
        });

        Assert.Equal(
            [
                ("FETCHCONTENT_SOURCE_DIR_GOOGLETEST", "/build/_deps/googletest-src"),
                ("FetchContent_Source_Dir_Json", "/mine/json"),
                ("FOO", "1"),
            ],
            beneath.CacheVars.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).Select(pair => (pair.Key, pair.Value)));
        Assert.Equal("1", beneath.CacheVars["foo"]);
        Assert.Equal([("FetchContent_Source_Dir_Json", "/mine/json"), ("FOO", "1")], project.CacheVars.Select(pair => (pair.Key, pair.Value)));

        foreach (var property in typeof(ProjectConfig).GetProperties().Where(property => property.Name is not nameof(ProjectConfig.CacheVars)))
        {
            Assert.Same(property.GetValue(project), property.GetValue(beneath));
        }
    }

    /// <summary>
    /// What a build of a leg's variant is expected to come to - by which a leg's build is placed and a sweep's workers are
    /// counted alike: its declared buildSpaceGiB, else what its own last build recorded, else the main checkout's copy's.
    /// </summary>
    [Theory]
    [InlineData(4.5, 7L, 9L, 4831838208L, "as its buildSpaceGiB, 4.5, declares")]
    [InlineData(null, 7L, 9L, 7L, "what its last build there came to")]
    [InlineData(null, null, 9L, 9L, "what the main checkout's copy of the same variant came to there")]
    [InlineData(null, null, null, null, "what the main checkout's copy of the same variant came to there")]
    public void ABuildsExpectedSize_IsDeclared_ElseItsOwnRecord_ElseTheMainCheckoutsCopys(double? declared, long? own, long? main, long? bytes, string source)
        => Assert.Equal(
            (bytes, source),
            LegRoom.ExpectedBuildBytes(new LegConfig { Os = "linux", Processor = "x86_64", Config = "debug", BuildSpaceGiB = declared }, own, main));

    /// <summary>An owner file for <paramref name="worker"/> naming a process of this machine, as a sweep's claim records one.</summary>
    private static void WriteOwner(string worker, int processId, string processStamp)
    {
        File.WriteAllText(
            worker + MutationWorkers.ClaimSuffix,
            "{ \"machine\": \"" + Environment.MachineName + "\", \"processId\": " + processId.ToString(CultureInfo.InvariantCulture)
            + ", \"processStamp\": \"" + processStamp + "\", \"runId\": \"20250101-120000-deadbeef\""
            + ", \"takenUtc\": \"" + DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture) + "\" }");
    }
}
