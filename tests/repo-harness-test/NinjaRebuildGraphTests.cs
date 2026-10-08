using RepoHarness.Core.Build;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Mutations;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Processes;
using RepoHarness.Core.Results;

namespace RepoHarness.Tests;

/// <summary>
/// What a change to a file rebuilds, read from a build directory's manifest and its dependency records the way ninja
/// reads them: which objects a mutation must rebuild, through every way a site reaches an object - its source, a
/// header it includes, a precompiled header holding it, a library linked in, a header generated from it - and none
/// outside what the target builds.
/// </summary>
public sealed class NinjaRebuildGraphTests
{
    private const string Pch = "CMakeFiles/fixture.dir/cmake_pch.hxx.gch";
    private const string FixtureObject = "CMakeFiles/fixture.dir/src/fixture.cpp.o";
    private const string SupportObject = "CMakeFiles/upstream.dir/src/support.cpp.o";
    private const string OtherObject = "CMakeFiles/other.dir/src/other.cpp.o";

    /// <summary>The one unit of a shared library whose target is named as a rule that links a program is.</summary>
    private const string OddObject = "CMakeFiles/odd__EXECUTABLE_LINKER__name.dir/src/odd.cpp.o";
    private const string OddLibrary = "lib/libodd.so";

    /// <summary>
    /// <c>ninja -t deps</c> read a line at a time: each record's header, the dependencies indented beneath it, keyed as
    /// ninja canonicalizes the object, its line endings either way; a line that is neither ends the record before it.
    /// </summary>
    [Fact]
    public void Records_AreReadALineAtATime()
    {
        var records = NinjaRebuildGraph.ReadRecords(
        [
            "CMakeFiles\\a.dir\\./a.cpp.obj: #deps 2, deps mtime 1 (VALID)\r",
            "    C:/src/a.cpp\r",
            "\tC:/src/a.hpp",
            "",
            "not a record",
            "    C:/src/stray.hpp",
            "b dir/b.cpp.obj: #deps 0, deps mtime 2 (STALE)",
        ]);

        Assert.Equal(["CMakeFiles/a.dir/a.cpp.obj", "b dir/b.cpp.obj"], records.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(["C:/src/a.cpp", "C:/src/a.hpp"], records["CMakeFiles/a.dir/a.cpp.obj"].Dependencies);
        Assert.Equal((0, "b dir/b.cpp.obj"), (records["b dir/b.cpp.obj"].Count, records["b dir/b.cpp.obj"].Object));
        Assert.Empty(records["b dir/b.cpp.obj"].Dependencies);
    }

    /// <summary>
    /// ninja answering <c>-t deps</c> with no record is refused, never read as no object depending on anything: that very
    /// answer would call every arm's target independent of its site. What it does answer is read a line at a time.
    /// </summary>
    [Fact]
    public async Task AnAnswerOfNoRecord_IsRefused_AndAnAnswerIsReadALineAtATime()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        var failure = await Assert.ThrowsAsync<HarnessException>(
            () => NinjaRebuildGraph.ReadRecordsAsync(new Answering("ninja: no work to do.\n"), "build", [], null, null, cancellationToken));
        var records = await NinjaRebuildGraph.ReadRecordsAsync(new Answering("a.o: #deps 1, deps mtime 1 (VALID)\r\n    a.cpp\r\n"), "build", [], null, null, cancellationToken);

        Assert.Equal(HarnessExit.CommandFailed, failure.ExitCode);
        Assert.Contains("'ninja -t deps' listed no objects for 'build'", failure.Message, StringComparison.Ordinal);
        Assert.Equal(["a.cpp"], records["a.o"].Dependencies);
    }

    /// <summary>
    /// The objects a target's closure holds that depend on a site, each way a site reaches one: its own source; a header
    /// both a library's object and the target's own include, the library linked in; a header the precompiled header
    /// holds, reaching the unit built from it through the <c>.pch</c> it names; and a template a command generates a
    /// header from, which only the object's record names. An object the target does not build is never one, whatever it
    /// includes, and a site nothing builds from has none. What links an object is never one either, though its linker
    /// recorded what it read as a compiler does - the program, and a shared library linked into it - while a unit stays
    /// one whatever its target is named, as a rule that links a program too.
    /// </summary>
    [Theory]
    [InlineData("src/fixture.cpp", new[] { FixtureObject })]
    [InlineData("src/budget.hpp", new[] { FixtureObject, SupportObject })]
    [InlineData("src/pch-held.hpp", new[] { FixtureObject, Pch })]
    [InlineData("src/config.h.in", new[] { FixtureObject })]
    [InlineData("src/odd.cpp", new[] { OddObject })]
    [InlineData("src/other.cpp", new string[0])]
    [InlineData("src/unread.hpp", new string[0])]
    public void TheObjectsThatDependOnASite_AreThoseInTheClosureItReaches(string site, string[] expected)
    {
        using var temp = new TempDirectory();
        var graph = Graph(temp);
        var closure = graph.Manifest.Closure(["fixture"]);

        var objects = RebuildWitness.DependentObjects(graph, closure, [temp.Combine(site)]);

        Assert.Equal(expected.Order(StringComparer.Ordinal), objects.Order(StringComparer.Ordinal));
    }

    /// <summary>A site spelled in another case is the same file where the build's file system folds case, and another where it does not.</summary>
    [Fact]
    public void ASiteIsCompared_AsTheBuildsFileSystemComparesNames()
    {
        using var temp = new TempDirectory();
        var folding = Graph(temp, StringComparer.OrdinalIgnoreCase);
        var exact = Graph(temp, StringComparer.Ordinal);
        var edge = folding.EdgeFor(FixtureObject)!;
        var shouted = temp.Combine("SRC", "FIXTURE.CPP");

        Assert.True(folding.DependsOn(edge, shouted));
        Assert.False(exact.DependsOn(exact.EdgeFor(FixtureObject)!, shouted));
    }

    /// <summary>
    /// A target's closure is every build line building it and what they read, however far back - its phony name, its
    /// link, its objects, each library linked in and that library's own objects - and never a line only ordered before it.
    /// </summary>
    [Fact]
    public void AClosure_IsWhatTheTargetBuilds_AndNothingOnlyOrderedBeforeIt()
    {
        using var temp = new TempDirectory();
        var manifest = Graph(temp).Manifest;

        var closure = manifest.Closure(["fixture"]).SelectMany(edge => edge.Outputs).ToList();

        Assert.Equal(["fixture", "bin/fixture", FixtureObject, Pch, "libupstream.a", SupportObject, OddLibrary, OddObject], closure);
        Assert.DoesNotContain("cmake_object_order_depends_target_fixture", closure);
        Assert.Empty(manifest.Closure(["no-such-target"]));
        Assert.Contains("generated/config.h", manifest.Outputs);
        Assert.Contains(OtherObject, manifest.Outputs);
    }

    /// <summary>
    /// A target's file is found through the phony lines CMake names it by - however many stand between - and is the
    /// target itself where the line building it is named so; a phony standing for several paths, or for a source nothing
    /// builds, names no file, and nor does a name nothing builds.
    /// </summary>
    [Theory]
    [InlineData("fixture", "bin/fixture")]
    [InlineData("fixture.exe", "bin/fixture")]
    [InlineData("other", "other")]
    [InlineData("upstream", "libupstream.a")]
    [InlineData("all", null)]
    [InlineData("edit_template", null)]
    [InlineData("no-such-target", null)]
    public void ATargetsFile_IsFoundThroughItsPhonyNames(string target, string? expected)
    {
        using var temp = new TempDirectory();

        Assert.Equal(expected, Graph(temp).Manifest.ArtifactOf(target));
    }

    /// <summary>
    /// A build directory laid out as CMake lays one out for g++ where the linker writes what it read, so each program
    /// and shared library records its dependencies as an object does: a library and the program linking it, the
    /// program's unit built from a precompiled header and including a header a command generates, a shared library
    /// linked in too, its target named as a rule that links a program is, and another program beside them.
    /// </summary>
    private static NinjaRebuildGraph Graph(TempDirectory temp, StringComparer? paths = null)
    {
        foreach (var source in new[] { "fixture.cpp", "support.cpp", "other.cpp", "odd.cpp", "budget.hpp", "pch-held.hpp", "config.h.in", "unread.hpp" })
        {
            temp.WriteFile(Path.Combine("src", source), "// " + source + "\n");
        }

        string Source(string name) => Escape(temp.Combine("src", name));

        temp.WriteFile(
            Path.Combine("build", "CMakeFiles", "rules.ninja"),
            "rule CXX_COMPILER__fixture_\n  deps = gcc\n  command = g++ -c $in -o $out\n"
            + "rule CXX_STATIC_LIBRARY_LINKER__upstream_\n  command = ar qc $out $in\n"
            + "rule CXX_EXECUTABLE_LINKER__fixture_\n  deps = gcc\n  command = g++ $in -o $out\n"
            + "rule CXX_COMPILER__odd__EXECUTABLE_LINKER__name_\n  deps = gcc\n  command = g++ -c $in -o $out\n"
            + "rule CXX_SHARED_LIBRARY_LINKER__odd__EXECUTABLE_LINKER__name_\n  deps = gcc\n  command = g++ -shared $in -o $out\n"
            + "rule CUSTOM_COMMAND\n  command = $COMMAND\n");
        temp.WriteFile(
            Path.Combine("build", NinjaDependencyCheck.ManifestFileName),
            "include CMakeFiles/rules.ninja\n"
            + "build cmake_object_order_depends_target_fixture: phony || generated/config.h\n"
            + $"build {SupportObject}: CXX_COMPILER__fixture_ {Source("support.cpp")}\n"
            + $"build libupstream.a: CXX_STATIC_LIBRARY_LINKER__upstream_ {SupportObject}\n"
            + $"build {Pch}: CXX_COMPILER__fixture_ CMakeFiles/fixture.dir/cmake_pch.hxx.cxx || cmake_object_order_depends_target_fixture\n"
            + $"build {FixtureObject}: CXX_COMPILER__fixture_ {Source("fixture.cpp")} | {Pch} || cmake_object_order_depends_target_fixture\n"
            + $"build {OddObject}: CXX_COMPILER__odd__EXECUTABLE_LINKER__name_ {Source("odd.cpp")}\n"
            + $"build {OddLibrary}: CXX_SHARED_LIBRARY_LINKER__odd__EXECUTABLE_LINKER__name_ {OddObject}\n"
            + $"build bin/fixture: CXX_EXECUTABLE_LINKER__fixture_ {FixtureObject} | libupstream.a {OddLibrary} || libupstream.a {OddLibrary}\n"
            + "build fixture: phony bin/fixture\n"
            + "build fixture.exe: phony fixture\n"
            + $"build {OtherObject}: CXX_COMPILER__fixture_ {Source("other.cpp")}\n"
            + $"build other: CXX_EXECUTABLE_LINKER__fixture_ {OtherObject}\n"
            + "build upstream: phony libupstream.a\n"
            + "build all: phony bin/fixture other\n"
            + $"build edit_template: phony {Source("config.h.in")}\n"
            + $"build generated/config.h: CUSTOM_COMMAND {Source("config.h.in")}\n  COMMAND = configure\n");

        var build = temp.Combine("build");
        var records = NinjaRebuildGraph.ReadRecords(
        [
            $"{SupportObject}: #deps 2, deps mtime 1 (VALID)",
            $"    {Slashed(temp.Combine("src", "support.cpp"))}",
            $"    {Slashed(temp.Combine("src", "budget.hpp"))}",
            "",
            $"{Pch}: #deps 2, deps mtime 1 (VALID)",
            "    CMakeFiles/fixture.dir/cmake_pch.hxx.cxx",
            $"    {Slashed(temp.Combine("src", "pch-held.hpp"))}",
            "",
            $"{FixtureObject}: #deps 3, deps mtime 1 (VALID)",
            $"    {Slashed(temp.Combine("src", "fixture.cpp"))}",
            $"    {Slashed(temp.Combine("src", "budget.hpp"))}",
            "    generated/config.h",
            "",
            $"{OtherObject}: #deps 2, deps mtime 1 (VALID)",
            $"    {Slashed(temp.Combine("src", "other.cpp"))}",
            $"    {Slashed(temp.Combine("src", "budget.hpp"))}",
            "",
            $"{OddObject}: #deps 1, deps mtime 1 (VALID)",
            $"    {Slashed(temp.Combine("src", "odd.cpp"))}",
            "",
            $"{OddLibrary}: #deps 1, deps mtime 1 (VALID)",
            $"    {OddObject}",
            "",
            "bin/fixture: #deps 3, deps mtime 1 (VALID)",
            $"    {FixtureObject}",
            "    libupstream.a",
            $"    {OddLibrary}",
        ]);
        var fileSystem = new PhysicalFileSystem(FilePermissionsFactory.Create());

        return new NinjaRebuildGraph(build, NinjaManifest.Read(fileSystem, build), records, fileSystem, paths ?? PathCase.In(fileSystem, build));
    }

    private static string Slashed(string path) => path.Replace('\\', '/');

    /// <summary>A ninja whose every answer is <paramref name="output"/>.</summary>
    private sealed class Answering(string output) : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new ProcessResult(0, output, string.Empty, TimeSpan.Zero, TimedOut: false));

        public string? FindExecutable(string command) => command;
    }

    /// <summary>A path as a ninja build line spells it.</summary>
    private static string Escape(string path)
        => path.Replace("$", "$$", StringComparison.Ordinal).Replace(" ", "$ ", StringComparison.Ordinal).Replace(":", "$:", StringComparison.Ordinal);
}
