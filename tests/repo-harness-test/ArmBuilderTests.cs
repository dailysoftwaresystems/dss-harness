using RepoHarness.Core.Build;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Mutations;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Processes;
using RepoHarness.Core.Results;

namespace RepoHarness.Tests;

/// <summary>
/// What a worker's build says of how it builds each target, read from its own manifest, dependency records and log: the
/// files a target builds, the program a runner builds or why it builds none, the objects depending on a site within what
/// a build builds, and the steps a failed build named.
/// </summary>
public sealed class ArmBuilderTests
{
    private const string FixtureObject = "CMakeFiles/fixture.dir/src/fixture.cpp.o";
    private const string TestsObject = "CMakeFiles/fixture_tests.dir/tests/fixture_tests.cpp.o";

    private static readonly VariantKey Variant = new("x86_64", "gcc", "debug", null);

    /// <summary>
    /// A target builds the file it names, through however many phony names stand for it; a target standing for several
    /// builds each that is built, and one standing for a source, or that nothing builds, builds none.
    /// </summary>
    [Theory]
    [InlineData("fixture", new[] { "libfixture.a" })]
    [InlineData("fixture_tests", new[] { "bin/fixture_tests" })]
    [InlineData("all", new[] { "libfixture.a", "bin/fixture_tests" })]
    [InlineData("edit_template", new string[0])]
    [InlineData("no-such-target", new string[0])]
    public void ATarget_BuildsTheFileItNames_OrEachOfThoseItStandsFor(string target, string[] outputs)
    {
        using var temp = new TempDirectory();

        Assert.Equal(outputs, Graph(temp).OutputsOf(target));
    }

    /// <summary>
    /// A runner builds a program where the file it names is linked as one; otherwise it says why it builds none it could
    /// run: a library, several files, or nothing the build builds.
    /// </summary>
    [Theory]
    [InlineData("fixture_tests", "bin/fixture_tests", null)]
    [InlineData("fixture", null, "runner 'fixture' builds 'libfixture.a', which its build makes as no program")]
    [InlineData("all", null, "runner 'all' stands for several files, so it names no one program to run")]
    [InlineData("no-such-target", null, "runner 'no-such-target' is built by no line of the leg's build")]
    public void ARunner_BuildsAProgram_OrSaysWhyItBuildsNone(string runner, string? program, string? problem)
    {
        using var temp = new TempDirectory();

        Assert.Equal(new WorkerProgram(program, problem), Graph(temp).ProgramOf(runner));
    }

    /// <summary>
    /// The objects depending on a site are those what the build builds reaches: the target's own, a runner's linking the
    /// target's library, and none the build does not build.
    /// </summary>
    [Fact]
    public void TheObjectsDependingOnASite_AreThoseWhatTheBuildBuildsReaches()
    {
        using var temp = new TempDirectory();
        var graph = Graph(temp);
        var fixture = temp.Combine("src", "fixture.cpp");
        var tests = temp.Combine("tests", "fixture_tests.cpp");

        Assert.Equal([FixtureObject], graph.DependentObjects(["fixture"], [fixture]));
        Assert.Equal([FixtureObject], graph.DependentObjects(["fixture_tests"], [fixture]));
        Assert.Empty(graph.DependentObjects(["fixture"], [tests]));
        Assert.Equal([TestsObject], graph.DependentObjects(["fixture", "fixture_tests"], [tests]));
    }

    /// <summary>The steps a failed build named are read from its lines, as its manifest names each.</summary>
    [Fact]
    public void TheStepsAFailedBuildNamed_AreReadFromItsLines()
    {
        using var temp = new TempDirectory();

        Assert.Equal(
            [FixtureObject],
            Graph(temp).FailedOutputs(["[1/3] Building CXX object " + FixtureObject, "FAILED: " + FixtureObject, "fixture.cpp:1: error: no integer", "ninja: build stopped: subcommand failed."]));
    }

    /// <summary>
    /// A worker's graph is read from its build directory: the records ninja answers for, asked with the ninja its CMake
    /// recorded, and the manifest beside them.
    /// </summary>
    [Fact]
    public async Task AWorkersGraph_IsReadFromItsBuildDirectory_AskingTheNinjaItsCMakeRecorded()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var worker = temp.Combine("tree.mutation-x86_64-gcc-debug-1");
        var build = Variant.DirectoryUnder(worker);
        var ninja = temp.Combine("tools", "ninja");

        WriteBuild(temp, build);
        File.WriteAllText(Path.Combine(build, BuildDirectoryGuard.CMakeCacheFileName), $"CMAKE_MAKE_PROGRAM:FILEPATH={ninja.Replace('\\', '/')}\n");

        var asked = new Answering($"{FixtureObject}: #deps 1, deps mtime 1 (VALID)\n    {temp.Combine("src", "fixture.cpp").Replace('\\', '/')}\n");
        var builder = new ArmBuilder(
            NSubstitute.Substitute.For<IBuildService>(),
            asked,
            new BuildDirectoryGuard(harness.FileSystem, harness.Platform, harness.FilePermissions),
            harness.FileSystem);
        var request = new BuildRequest("native/workers/1", worker, new ProjectConfig { Name = "app", Type = "cmake" }, Variant, harness.Platform.PlatformKey, 2, temp.Combine("runs"));

        var graph = await builder.ReadGraphAsync(new HarnessConfig(), request, TestContext.Current.CancellationToken);

        Assert.Equal(["libfixture.a"], graph.OutputsOf("fixture"));
        Assert.Equal([FixtureObject], graph.DependentObjects(["fixture"], [temp.Combine("src", "fixture.cpp")]));

        var sent = Assert.Single(asked.Requests);

        Assert.Equal(["-C", build, "-t", "deps"], sent.Arguments.TakeLast(4));
        Assert.Equal(Path.GetFullPath(ninja), Path.GetFullPath(sent.FileName));
    }

    /// <summary>
    /// A worker's graph whose manifest could not be read whole - <c>build.ninja</c>, or the rules it includes, held by
    /// another process the instant after the build, or not there - is no graph: it is refused, naming each file and why.
    /// Read with what was left, it said the build builds less than it does, and every arm the worker drew was violated
    /// for a target no line builds or a site nothing depends on - the registry blamed for a file this could not read.
    /// </summary>
    [Theory]
    [InlineData("build.ninja", true, "'build.ninja' could not be read: it is held by another process")]
    [InlineData("CMakeFiles/rules.ninja", true, "'CMakeFiles/rules.ninja' could not be read: it is held by another process")]
    [InlineData("CMakeFiles/rules.ninja", false, "'CMakeFiles/rules.ninja' is not there")]
    public async Task AWorkersGraphWhoseManifestCouldNotBeReadWhole_IsRefused_NamingEachFileAndWhy(string file, bool held, string said)
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var worker = temp.Combine("tree.mutation-x86_64-gcc-debug-1");
        var build = Variant.DirectoryUnder(worker);
        var path = Path.Combine(build, file.Replace('/', Path.DirectorySeparatorChar));

        WriteBuild(temp, build);
        File.WriteAllText(
            Path.Combine(build, NinjaDependencyCheck.ManifestFileName),
            "include CMakeFiles/rules.ninja\n" + File.ReadAllText(Path.Combine(build, NinjaDependencyCheck.ManifestFileName)));

        if (held || file == NinjaDependencyCheck.ManifestFileName)
        {
            Directory.CreateDirectory(Path.Combine(build, "CMakeFiles"));
            File.WriteAllText(Path.Combine(build, "CMakeFiles", "rules.ninja"), "rule extra\n  command = true\n");
        }

        IFileSystem files = held ? new Held(harness.FileSystem, path) : harness.FileSystem;
        var builder = new ArmBuilder(
            NSubstitute.Substitute.For<IBuildService>(),
            new Answering($"{FixtureObject}: #deps 1, deps mtime 1 (VALID)\n    {temp.Combine("src", "fixture.cpp").Replace('\\', '/')}\n"),
            new BuildDirectoryGuard(harness.FileSystem, harness.Platform, harness.FilePermissions),
            files);
        var request = new BuildRequest("native/workers/1", worker, new ProjectConfig { Name = "app", Type = "cmake" }, Variant, harness.Platform.PlatformKey, 2, temp.Combine("runs"));

        var refusal = await Assert.ThrowsAsync<HarnessException>(() => builder.ReadGraphAsync(new HarnessConfig(), request, TestContext.Current.CancellationToken));

        Assert.Equal(HarnessExit.CommandFailed, refusal.ExitCode);
        Assert.Equal($"ninja's manifest in '{build}' could not be read whole, so what the build there builds is not known: {said}.", refusal.Message);
    }

    /// <summary>A worker's ninja log is read as ninja wrote it; one it did not write, or of a version not read, is none.</summary>
    [Fact]
    public void AWorkersNinjaLog_IsReadAsNinjaWroteIt_AndAnyOtherIsNone()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var builder = new ArmBuilder(NSubstitute.Substitute.For<IBuildService>(), new Answering(string.Empty), new BuildDirectoryGuard(harness.FileSystem, harness.Platform, harness.FilePermissions), harness.FileSystem);

        Assert.Null(builder.ReadLog(temp.Combine("none")));

        temp.WriteFile(Path.Combine("v5", NinjaLog.FileName), $"# ninja log v5\n0\t10\t1000\t{FixtureObject}\tabc123\n");
        temp.WriteFile(Path.Combine("v4", NinjaLog.FileName), $"# ninja log v4\n0\t10\t1000\t{FixtureObject}\n");

        Assert.Equal(new NinjaLogEntry(FixtureObject, "0", "10", "1000", "abc123"), builder.ReadLog(temp.Combine("v5"))?.Entries[FixtureObject]);
        Assert.Null(builder.ReadLog(temp.Combine("v4")));
    }

    /// <summary>
    /// A worker's build as CMake lays one out: a library, a test program linking it, a group of both, and a phony name
    /// for a template no step builds.
    /// </summary>
    private static NinjaWorkerGraph Graph(TempDirectory temp)
    {
        var build = temp.Combine("build");

        WriteBuild(temp, build);

        var records = NinjaRebuildGraph.ReadRecords(
        [
            $"{FixtureObject}: #deps 1, deps mtime 1 (VALID)",
            $"    {temp.Combine("src", "fixture.cpp").Replace('\\', '/')}",
            string.Empty,
            $"{TestsObject}: #deps 1, deps mtime 1 (VALID)",
            $"    {temp.Combine("tests", "fixture_tests.cpp").Replace('\\', '/')}",
        ]);
        var fileSystem = new PhysicalFileSystem(FilePermissionsFactory.Create());

        return new NinjaWorkerGraph(new NinjaRebuildGraph(build, NinjaManifest.Read(fileSystem, build), records, fileSystem, PathCase.In(fileSystem, build)), null);
    }

    /// <summary>Writes the sources, and the manifest of a build of them in <paramref name="build"/>.</summary>
    private static void WriteBuild(TempDirectory temp, string build)
    {
        foreach (var source in new[] { Path.Combine("src", "fixture.cpp"), Path.Combine("tests", "fixture_tests.cpp"), Path.Combine("src", "config.h.in") })
        {
            temp.WriteFile(source, "// " + source + "\n");
        }

        string Source(params string[] parts) => temp.Combine(parts).Replace("$", "$$", StringComparison.Ordinal).Replace(" ", "$ ", StringComparison.Ordinal).Replace(":", "$:", StringComparison.Ordinal);

        Directory.CreateDirectory(build);
        File.WriteAllText(
            Path.Combine(build, NinjaDependencyCheck.ManifestFileName),
            "rule CXX_COMPILER__fixture_\n  deps = gcc\n  command = g++ -c $in -o $out\n"
            + "rule CXX_STATIC_LIBRARY_LINKER__fixture_\n  command = ar qc $out $in\n"
            + "rule CXX_EXECUTABLE_LINKER__fixture_tests_\n  command = g++ $in -o $out\n"
            + $"build {FixtureObject}: CXX_COMPILER__fixture_ {Source("src", "fixture.cpp")}\n"
            + $"build libfixture.a: CXX_STATIC_LIBRARY_LINKER__fixture_ {FixtureObject}\n"
            + "build fixture: phony libfixture.a\n"
            + $"build {TestsObject}: CXX_COMPILER__fixture_ {Source("tests", "fixture_tests.cpp")}\n"
            + $"build bin/fixture_tests: CXX_EXECUTABLE_LINKER__fixture_tests_ {TestsObject} | libfixture.a\n"
            + "build fixture_tests: phony bin/fixture_tests\n"
            + "build all: phony libfixture.a bin/fixture_tests\n"
            + $"build edit_template: phony {Source("src", "config.h.in")}\n");
    }

    /// <summary>A ninja whose every answer is <paramref name="output"/>, recording what it was asked.</summary>
    private sealed class Answering(string output) : IProcessRunner
    {
        public List<ProcessRequest> Requests { get; } = [];

        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(new ProcessResult(0, output, string.Empty, TimeSpan.Zero, TimedOut: false));
        }

        public string? FindExecutable(string command) => command;
    }
}
