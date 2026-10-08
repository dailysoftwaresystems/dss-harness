using System.Globalization;
using RepoHarness.Core.Build;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.Execution;
using RepoHarness.Core.Hosts;
using RepoHarness.Core.Legs;
using RepoHarness.Core.Mutations;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Repository;
using RepoHarness.Core.Runs;
using RepoHarness.Core.Sync;

namespace RepoHarness.Tests;

/// <summary>
/// Where a leg's mutation workers are kept, the lock a sweep of the leg takes, and the claim a sweep holds on each
/// worker: copies beside the tree, each named by a short key of its variant and its number; a lock no build of the leg
/// meets; and a claim that never makes the copy it claims, released and said where a sweep died holding it.
/// </summary>
public sealed class MutationWorkersTests
{
    private static readonly VariantKey Variant = new("x86_64", "gcc", "debug", null);

    /// <summary>
    /// A worker is a copy beside the tree, named by its variant's key, its family's mark and its number, spelt as the
    /// tree is - this machine's way, or a host's - and a sweep is locked by the key alone, which no worker is named.
    /// </summary>
    [Theory]
    [InlineData("/home/pi/repo", "/home/pi/repo.mutation-357e24c")]
    [InlineData("/home/pi/repo/", "/home/pi/repo.mutation-357e24c")]
    [InlineData("C:/src/repo", "C:/src/repo.mutation-357e24c")]
    public void AWorker_IsACopyBesideTheTree_NamedByItsVariantsKeyAndItsNumber(string tree, string keyed)
    {
        Assert.Equal(keyed + "w", MutationWorkers.Of(tree, Variant).Root);
        Assert.Equal(keyed + "w-1", MutationWorkers.PathOf(tree, Variant, 1));
        Assert.Equal(keyed + "w-12", MutationWorkers.PathOf(tree, Variant, 12));
        Assert.Equal(keyed, MutationWorkers.Of(tree, Variant).SweepKey);
        Assert.Throws<ArgumentOutOfRangeException>(() => MutationWorkers.PathOf(tree, Variant, 0));
    }

    /// <summary>
    /// A variant's key is the first seven hexadecimal digits of the SHA-256 of its name, in lower case - each derived
    /// here apart from this tool - so every machine a leg is swept on, and the one that dispatched it, names its workers
    /// alike, and a variant under any other name is another's.
    /// </summary>
    [Theory]
    [InlineData("x86_64", "gcc", "debug", null, "357e24c")]
    [InlineData("x86_64", "mingw-gcc", "debug", null, "d1638ab")]
    [InlineData("x86_64", "msvc", "release", null, "ab6a19f")]
    [InlineData("x86_64", "gcc", "debug", "asan", "20b4d38")]
    [InlineData("arm64", "none", "debug", null, "aa1fa54")]
    public void AVariantsKey_IsSevenDigitsOfItsNamesHash(string processor, string toolchain, string config, string? sanitizer, string key)
    {
        Assert.Equal(7, WorkerFamily.KeyLength);
        Assert.Equal(key, MutationWorkers.KeyOf(new VariantKey(processor, toolchain, config, sanitizer)));
        Assert.Equal(key, MutationWorkers.Of("/home/pi/repo", new VariantKey(processor, toolchain, config, sanitizer)).Key);
    }

    /// <summary>
    /// A worker's name adds twenty characters to its tree's path whatever its variant is called, a self-test's as its
    /// own - so a repository whose path budget is reckoned to the character, leaving twenty-two beside its tree, holds a
    /// worker of each of its variants, where a name spelling the variant out left none.
    /// </summary>
    [Theory]
    [InlineData("mingw-gcc", "debug", 257)]
    [InlineData("msvc", "release", 254)]
    public void AWorkersName_AddsTwentyCharactersToItsTrees_WhateverItsVariantIsCalled(string toolchain, string config, int needed)
    {
        // A tree of 38 characters, a build whose longest path below its build directory is 168, a margin of 1, under 260.
        const string tree = "C:/Source/SomeCompany/consumer-project";
        var variant = new VariantKey("x86_64", toolchain, config, null);
        var worker = MutationWorkers.PathOf(tree, variant, 1);

        Assert.Equal(38, tree.Length);
        Assert.Equal(tree.Length + 20, worker.Length);
        Assert.Equal(tree.Length + 20, MutationWorkers.Of(tree, variant, selfTest: true).PathOf(9).Length);

        // What a sweep reckons a worker by: its build directory below it, the separator before it counted, and the reserve.
        var below = $"/build/{variant.DirectoryName}".Length + 168;
        var budget = new PathBudget(new HostPlatform()).Check(worker, below, 1, 260);

        Assert.True(budget.IsWithinBudget);
        Assert.Equal(needed, budget.RequiredLength);
    }

    /// <summary>
    /// A self-test's workers are a family of their own beside the leg's tree, told from the variant's own by the mark
    /// after its key: none of them a worker of the variant's own family, locked as the variant's sweep is, and read back
    /// from the name they are kept under as what they are.
    /// </summary>
    [Fact]
    public void ASelfTestsWorkers_AreAFamilyOfTheirOwn_LockedAsTheVariantsSweepIs()
    {
        const string tree = "/home/pi/repo";
        var run = RunId.New();
        var own = MutationWorkers.Of(tree, Variant);
        var selfTest = MutationWorkers.Of(tree, Variant, selfTest: true);

        Assert.Equal(('w', 's'), (WorkerFamily.OwnMark, WorkerFamily.SelfTestMark));
        Assert.Equal("/home/pi/repo.mutation-357e24cs-2", selfTest.PathOf(2));
        Assert.Equal(("357e24cs", "357e24cw"), (selfTest.Name, own.Name));
        Assert.Equal("/home/pi/repo.mutation-357e24cs", selfTest.Root);
        Assert.Equal(("/home/pi/repo.mutation-357e24c", "/home/pi/repo.mutation-357e24c"), (own.SweepKey, selfTest.SweepKey));
        Assert.Equal(selfTest, WorkerFamily.Named(tree, selfTest.Name));
        Assert.Equal(own, WorkerFamily.Named(tree, own.Name));
        Assert.Equal(
            MutationWorkers.SweepLock(HostId.Local, tree, Variant, run, "check-mutations"),
            MutationWorkers.SweepLock(HostId.Local, selfTest, run, "check-mutations"));
        Assert.Throws<ArgumentOutOfRangeException>(() => selfTest.PathOf(0));
    }

    /// <summary>
    /// A family's name is spelt one way: a key of seven hexadecimal digits in lower case, then the mark of the variant's
    /// own workers or of its self-test's. Any other is no family's - a variant's name spelt out, above all.
    /// </summary>
    [Theory]
    [InlineData("357e24cw", "357e24c", false)]
    [InlineData("357e24cs", "357e24c", true)]
    [InlineData("0000000w", "0000000", false)]
    [InlineData("abcdef0s", "abcdef0", true)]
    [InlineData("357e24c", null, false)]
    [InlineData("357e24cx", null, false)]
    [InlineData("357e24cW", null, false)]
    [InlineData("357E24Cw", null, false)]
    [InlineData("357e24gw", null, false)]
    [InlineData("357e24ccw", null, false)]
    [InlineData("357e24cww", null, false)]
    [InlineData("357e24cw-1", null, false)]
    [InlineData("57e24cw", null, false)]
    [InlineData(" 357e24cw", null, false)]
    [InlineData("x86_64-gcc-debug", null, false)]
    [InlineData("self-test-x86_64-gcc-debug", null, false)]
    [InlineData("w", null, false)]
    [InlineData("", null, false)]
    public void AFamilysName_IsAKeyAndAMark_AndAnyOtherIsNone(string name, string? key, bool selfTest)
        => Assert.Equal(key is null ? null : new WorkerFamily("/home/pi/repo", key, selfTest), WorkerFamily.Named("/home/pi/repo", name));

    /// <summary>
    /// A worker's name is its family's, a hyphen and its number, in digits alone and never a leading zero; a name ending
    /// any other way is no worker's, and neither is one whose family is spelt as none.
    /// </summary>
    [Theory]
    [InlineData("357e24cw-1", "357e24cw", 1)]
    [InlineData("357e24cs-12", "357e24cs", 12)]
    [InlineData("357e24cw-23", "357e24cw", 23)]
    [InlineData("357e24cw", null, null)]
    [InlineData("357e24cw-+1", null, null)]
    [InlineData("357e24cw- 1", null, null)]
    [InlineData("357e24cw-01", null, null)]
    [InlineData("357e24cw-0", null, null)]
    [InlineData("357e24cw-", null, null)]
    [InlineData("357e24cw-99999999999", null, null)]
    [InlineData("357e24c-1", null, null)]
    [InlineData("357e24cw-1-1", null, null)]
    [InlineData("x86_64-gcc-debug-1", null, null)]
    [InlineData("self-test-x86_64-gcc-debug-12", null, null)]
    [InlineData("-1", null, null)]
    [InlineData("1", null, null)]
    [InlineData("notes", null, null)]
    [InlineData("", null, null)]
    public void AWorkersName_IsItsFamilysAndItsNumber(string name, string? family, int? number)
        => Assert.Equal(family is null ? null : (family, number!.Value), MutationWorkers.Named(name));

    /// <summary>
    /// A directory named as a mutation worker names the tree it copies, by what comes before the family's suffix where
    /// its name ends as a worker's does - the last of them, so a tree whose own name holds the suffix is still told from
    /// its workers; one with no tree before it, or spelt as no worker after it, is none.
    /// </summary>
    [Theory]
    [InlineData("alpha.mutation-357e24cw-1", "alpha")]
    [InlineData("a1.mutation-357e24cs-2", "a1")]
    [InlineData("alpha.mutation-notes.mutation-357e24cw-2", "alpha.mutation-notes")]
    [InlineData("alpha.mutation-357e24cw-1.mutation-357e24cs-2", "alpha.mutation-357e24cw-1")]
    [InlineData("alpha.mutation-357e24cw-1.mutation-notes", null)]
    [InlineData("alpha.mutation-x86_64-gcc-debug-1", null)]
    [InlineData("alpha.mutation-notes", null)]
    [InlineData("alpha.mutation-", null)]
    [InlineData(".mutation-357e24cw-1", null)]
    [InlineData("alpha.worktree-357e24cw-1", null)]
    [InlineData("alpha", null)]
    public void ADirectoryNamedAsAWorker_NamesTheTreeItCopies(string name, string? tree)
        => Assert.Equal(tree, MutationWorkers.TreeNamed(name));

    /// <summary>
    /// The trees with a worker in a directory are named once each, however many workers each has, in order - whether
    /// the tree itself is still there or gone - and a directory that is not there holds none.
    /// </summary>
    [Fact]
    public void TheTreesWithAWorkerInADirectory_AreNamedOnceEach_InOrder()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();

        foreach (var name in new[] { "beta", "beta.mutation-357e24cw-2", "beta.mutation-357e24cw-1", "alpha.mutation-357e24cs-1", "gamma", "gamma.mutation-notes", "delta.worktree-x" })
        {
            Directory.CreateDirectory(temp.Combine("group", name));
        }

        Assert.Equal(["alpha", "beta"], MutationWorkers.TreesWithWorkersIn(harness.FileSystem, temp.Combine("group")));
        Assert.Empty(MutationWorkers.TreesWithWorkersIn(harness.FileSystem, temp.Combine("nowhere")));
    }

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
        var worker = temp.Combine("repo.mutation-357e24cw-1");
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
        var dead = temp.Combine("repo.mutation-357e24cw-1");
        var live = temp.Combine("repo.mutation-357e24cw-2");

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
