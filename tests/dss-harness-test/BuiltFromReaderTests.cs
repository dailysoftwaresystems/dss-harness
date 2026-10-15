using System.Text;
using DssHarness.Core.Build;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Mutations;
using DssHarness.Core.Platform;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;
using DssHarness.Core.Sync;

namespace DssHarness.Tests;

/// <summary>
/// What a target is built from, as a worker's build of it read: every file of the tree its build lines read, through
/// headers and generated files, by the reading of the tree; a dependency's sources as their manifest; what the worker's
/// configure made, and its build lines, with the worker's path told; and what lies outside, by directory - and nothing a
/// build line makes, nothing of another target, and the same table from every worker of the same content.
/// </summary>
public sealed class BuiltFromReaderTests
{
    private const string LibObject = "CMakeFiles/lib.dir/src/a.cpp.o";
    private const string OtherObject = "CMakeFiles/other.dir/src/other.cpp.o";

    /// <summary>
    /// A target's table holds each file of the tree its build lines read: its source, the header its compile recorded,
    /// and the template a command made a generated header from, which the compile is only ordered after and found - and
    /// the command making it is among the lines. It holds no file a build line makes, no file only another target reads,
    /// and each file of the tree as the reading holds it, though the worker's copy holds something else.
    /// </summary>
    [Fact]
    public async Task ATargetsTable_HoldsEveryFileOfTheTreeItsBuildLinesRead_ThroughHeadersAndGeneratedFiles_AndNoFileABuildLineProduces()
    {
        using var temp = new TempDirectory();
        var worker = new Worker(temp, "w1");
        var system = temp.Combine("sys", "include");

        worker.Tree("src/a.cpp", "#include \"a.hpp\"\n#include \"version.h\"\n");
        worker.Tree("src/a.hpp", "int a();\n");
        worker.Tree("src/version.h.in", "#define VERSION 1\n");
        worker.Tree("src/other.cpp", "#include \"a.hpp\"\n");
        worker.Tree("CMakeLists.txt", "add_library(lib src/a.cpp)\nadd_library(other src/other.cpp)\n");
        worker.Write("build/config.h", "#define CONFIGURED 1\n");
        worker.Write("build/gen/version.h", "#define VERSION 1\n");
        worker.Write("build/" + LibObject, "object");
        Directory.CreateDirectory(system);
        File.WriteAllText(Path.Combine(system, "sys.h"), "// the system's\n");

        // The worker's copy of a site holds a mutation, and the reading what the tree holds.
        File.WriteAllText(worker.Native("src/a.hpp"), "int a(); // mutated\n");

        var graph = worker.Graph(
            "rule CXX_COMPILER__lib_\n  deps = gcc\n  command = g++ $FLAGS -c $in -o $out\n"
            + "rule CXX_STATIC_LIBRARY_LINKER__lib_\n  command = ar qc $out $in\n"
            + "rule CUSTOM_COMMAND\n  command = $COMMAND\n"
            + $"build gen/version.h: CUSTOM_COMMAND {worker.Ninja("src/version.h.in")}\n"
            + $"  COMMAND = cmake -E copy {worker.Forward("src/version.h.in")} {worker.Forward("build/gen/version.h")}\n"
            + "build cmake_object_order_depends_target_lib: phony || gen/version.h\n"
            + $"build {LibObject}: CXX_COMPILER__lib_ {worker.Ninja("src/a.cpp")} || cmake_object_order_depends_target_lib\n"
            + $"  FLAGS = -I{worker.Forward("build/gen")} -I{worker.Forward("build")}\n"
            + $"build liblib.a: CXX_STATIC_LIBRARY_LINKER__lib_ {LibObject}\n"
            + "build lib: phony liblib.a\n"
            + $"build {OtherObject}: CXX_COMPILER__lib_ {worker.Ninja("src/other.cpp")}\n"
            + $"build libother.a: CXX_STATIC_LIBRARY_LINKER__lib_ {OtherObject}\n"
            + "build other: phony libother.a\n",
            $"{LibObject}: #deps 5, deps mtime 1 (VALID)",
            $"    {worker.Forward("src/a.cpp")}",
            $"    {worker.Forward("src/a.hpp")}",
            $"    {worker.Forward("build/gen/version.h")}",
            $"    {worker.Forward("build/config.h")}",
            $"    {system.Replace('\\', '/')}/sys.h",
            string.Empty,
            $"{OtherObject}: #deps 2, deps mtime 1 (VALID)",
            $"    {worker.Forward("src/other.cpp")}",
            $"    {worker.Forward("src/a.hpp")}");

        var source = graph.ReadBy("lib");
        var table = await TableAsync(worker.Reader(), source);

        Assert.Equal(["lib", "liblib.a", LibObject, "gen/version.h"], source.Lines.Select(line => line.Outputs[0]));
        Assert.Equal(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["src/a.cpp"] = worker.Reading["src/a.cpp"].ContentHash,
                ["src/a.hpp"] = Hash("int a();\n"),
                ["src/version.h.in"] = worker.Reading["src/version.h.in"].ContentHash,
            },
            table.Files);
        Assert.Equal([BuiltFromReader.BuildDirectory], table.Made.Keys);
        Assert.Equal(1, table.Made[BuiltFromReader.BuildDirectory].Files);
        Assert.Equal([system], table.Outside.Keys);
        Assert.Empty(table.Fetched);
    }

    /// <summary>
    /// A file of the tree a build spells in another case than the reading of the tree - as a file system that folds case
    /// lets it - is the reading's file, named as the reading spells it, on such a file system.
    /// </summary>
    [Fact]
    public async Task AFileOfTheTree_IsRecordedAsTheReadingSpellsIt_WhereTheBuildSpellsItInAnotherCase()
    {
        using var temp = new TempDirectory();
        var worker = new Worker(temp, "w1");

        worker.Tree("src/Budget.hpp", "#pragma once\n");

        var table = await TableAsync(
            worker.Reader(names: StringComparer.OrdinalIgnoreCase),
            new BuiltFromSource("lib", [], [new BuiltFromPath(Path.Combine(worker.Root, "SRC", "budget.HPP"), Produced: false)]));

        Assert.Equal(["src/Budget.hpp"], table.Files.Keys);
        Assert.Empty(table.Made);
    }

    /// <summary>
    /// A file of a dependency's sources the worker was given stands as the whole dependency: its name and the digest of
    /// every file of its sources as the sweep read them - the same in every worker given them, and another where a file
    /// of them holds something else.
    /// </summary>
    [Fact]
    public async Task AFetchedSource_StandsAsItsDependencysManifest()
    {
        using var temp = new TempDirectory();
        var first = new Worker(temp, "w1");
        var second = new Worker(temp, "another-worker");
        var given = Fetched(("include/plumb.hpp", "#pragma once\n"), ("CMakeLists.txt", "add_library(plumb INTERFACE)\n"));
        var moved = Fetched(("include/plumb.hpp", "#pragma once // moved\n"), ("CMakeLists.txt", "add_library(plumb INTERFACE)\n"));

        BuiltFromSource Reads(Worker worker)
        {
            var kept = FetchedSources.KeptIn(worker.Root, "plumb");

            Directory.CreateDirectory(Path.Combine(kept, "include"));
            File.WriteAllText(Path.Combine(kept, "include", "plumb.hpp"), "#pragma once\n");

            return new BuiltFromSource("lib", [], [new BuiltFromPath(Path.Combine(kept, "include", "plumb.hpp"), Produced: false)]);
        }

        var table = await TableAsync(first.Reader([given]), Reads(first));

        Assert.Equal(["plumb"], table.Fetched.Keys);
        Assert.Empty(table.Files);
        Assert.Empty(table.Made);
        Assert.Equal(table.Fetched, (await TableAsync(second.Reader([given]), Reads(second))).Fetched);
        Assert.NotEqual(table.Fetched["plumb"], (await TableAsync(first.Reader([moved]), Reads(first))).Fetched["plumb"]);

        static FetchedReading Fetched(params (string Path, string Text)[] files)
            => new(
                new FetchedSource("plumb", "elsewhere"),
                new SyncManifest("elsewhere", files.ToDictionary(file => file.Path, file => new SyncEntry(file.Path, Encoding.UTF8.GetByteCount(file.Text), Hash(file.Text)), StringComparer.Ordinal)));
    }

    /// <summary>
    /// A file the worker's configure made - in its build directory, or among the tree's files and none of them - is
    /// hashed with the worker's own path told, in each spelling its build may hold it in, so two workers whose configure
    /// wrote each its own path come to one table; one that holds something else does not.
    /// </summary>
    [Fact]
    public async Task AFileTheConfigureMade_IsHashedWithTheWorkersPathReplaced_SoTwoWorkersAgree()
    {
        using var temp = new TempDirectory();

        async Task<BuiltFromTable> MadeIn(Worker worker, string extra)
        {
            var native = worker.Root;

            worker.Write(
                "build/config.h",
                $"#define ROOT \"{native.Replace('\\', '/')}\"\n#define ESCAPED \"{native.Replace("\\", "\\\\", StringComparison.Ordinal)}\"\n"
                + $"#define NATIVE {native}\n#define LOWER {char.ToLowerInvariant(native[0])}{native[1..]}\n{extra}");
            worker.Write("src/generated.inc", $"{native}{Path.DirectorySeparatorChar}src\n");

            return await TableAsync(
                worker.Reader(),
                new BuiltFromSource(
                    "lib",
                    [],
                    [new BuiltFromPath(worker.Native("build/config.h"), Produced: false), new BuiltFromPath(worker.Native("src/generated.inc"), Produced: false)]));
        }

        var first = await MadeIn(new Worker(temp, "w1"), string.Empty);
        var second = await MadeIn(new Worker(temp, "another-worker"), string.Empty);
        var other = await MadeIn(new Worker(temp, "w3"), "#define MORE 1\n");

        Assert.Equal([BuiltFromReader.BuildDirectory, $"{BuiltFromReader.TreeDirectory}/src"], first.Made.Keys);
        Assert.Equal(first.Made, second.Made);
        Assert.Equal(first.Digest(), second.Digest());
        Assert.NotEqual(first.Made[BuiltFromReader.BuildDirectory], other.Made[BuiltFromReader.BuildDirectory]);
        Assert.Equal(first.Made[$"{BuiltFromReader.TreeDirectory}/src"], other.Made[$"{BuiltFromReader.TreeDirectory}/src"]);
    }

    /// <summary>
    /// A file outside the worker is hashed where it is, and counted and digested with the others of its directory; one
    /// that holds something else moves its directory's digest; and one that cannot be read leaves the target no table,
    /// saying which file and why.
    /// </summary>
    [Fact]
    public async Task AFileOutsideTheTree_IsHashedWhereItIs_PerDirectory_AndOneThatCannotBeReadLeavesNoKey()
    {
        using var temp = new TempDirectory();
        var worker = new Worker(temp, "w1");
        var include = temp.Combine("sys", "include");
        var lib = temp.Combine("sys", "lib");

        Directory.CreateDirectory(include);
        Directory.CreateDirectory(lib);
        File.WriteAllText(Path.Combine(include, "a.h"), "// a\n");
        File.WriteAllText(Path.Combine(include, "b.h"), "// b\n");
        File.WriteAllText(Path.Combine(lib, "libm.a"), "archive");

        var source = new BuiltFromSource(
            "lib",
            [],
            [.. new[] { Path.Combine(include, "a.h"), Path.Combine(include, "b.h"), Path.Combine(lib, "libm.a") }.Select(path => new BuiltFromPath(path, Produced: false))]);

        var table = await TableAsync(worker.Reader(), source);

        Assert.Equal([include, lib], table.Outside.Keys);
        Assert.Equal((2, 1), (table.Outside[include].Files, table.Outside[lib].Files));
        Assert.Empty(table.Made);

        File.WriteAllText(Path.Combine(include, "b.h"), "// b, upgraded\n");

        var upgraded = await TableAsync(worker.Reader(), source);

        Assert.NotEqual(table.Outside[include], upgraded.Outside[include]);
        Assert.Equal(table.Outside[lib], upgraded.Outside[lib]);

        File.Delete(Path.Combine(lib, "libm.a"));

        var gone = await worker.Reader().ReadAsync(source, TestContext.Current.CancellationToken);

        Assert.Null(gone.Table);
        Assert.Equal($"'{Path.Combine(lib, "libm.a")}', which the build of 'lib' reads, is gone", gone.Unread);
    }

    /// <summary>
    /// A directory among what a target's build reads - a command that depends on a directory, in the tree or outside the
    /// worker - leaves the target with no table, saying it is a directory whose files no build line names: what the build
    /// reads in it cannot be told, and a directory is never said to be gone.
    /// </summary>
    [Fact]
    public async Task ADirectoryABuildReads_LeavesTheTargetNoTable_SayingItIsADirectory()
    {
        using var temp = new TempDirectory();
        var worker = new Worker(temp, "w1");
        var outside = temp.Combine("sys", "share");

        worker.Tree("assets/a.png", "png");
        Directory.CreateDirectory(outside);

        foreach (var directory in new[] { worker.Native("assets"), outside })
        {
            var read = await worker.Reader().ReadAsync(
                new BuiltFromSource("lib", [], [new BuiltFromPath(directory, Produced: false)]),
                TestContext.Current.CancellationToken);

            Assert.Null(read.Table);
            Assert.Equal($"'{directory}', which the build of 'lib' reads, is a directory, and no build line names the files it reads in it", read.Unread);
        }
    }

    /// <summary>
    /// A file of the tree a phony line names - as CMake names each file its configure read - is read as the tree holds it:
    /// a phony line makes nothing, so the file is no file a build line makes, and is in the table.
    /// </summary>
    [Fact]
    public async Task AFileOfTheTreeAPhonyLineNames_IsInTheTable_AsTheReadingHoldsIt()
    {
        using var temp = new TempDirectory();
        var worker = new Worker(temp, "w1");

        worker.Tree("src/a.cpp", "#include \"a.hpp\"\n");
        worker.Tree("src/a.hpp", "int a();\n");
        worker.Write("build/" + LibObject, "object");

        var graph = worker.Graph(
            "rule CXX_COMPILER__lib_\n  deps = gcc\n  command = g++ -c $in -o $out\n"
            + "rule CXX_STATIC_LIBRARY_LINKER__lib_\n  command = ar qc $out $in\n"
            + $"build {LibObject}: CXX_COMPILER__lib_ {worker.Ninja("src/a.cpp")}\n"
            + $"build liblib.a: CXX_STATIC_LIBRARY_LINKER__lib_ {LibObject}\n"
            + "build lib: phony liblib.a\n"
            + "build ../src/a.hpp: phony\n",
            $"{LibObject}: #deps 2, deps mtime 1 (VALID)",
            $"    {worker.Forward("src/a.cpp")}",
            $"    {worker.Forward("src/a.hpp")}");

        var table = await TableAsync(worker.Reader(), graph.ReadBy("lib"));

        Assert.Equal(["src/a.cpp", "src/a.hpp"], table.Files.Keys.Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// A target's build lines are digested with the worker's own path told wherever it stands in them - as its machine
    /// spells a path, with <c>/</c> throughout, with each <c>\</c> doubled, and with its drive's letter in either case -
    /// so two workers' lines agree; a line that says another thing does not.
    /// </summary>
    [Fact]
    public async Task TheBuildLines_AreDigestedWithTheWorkersPathReplaced_InEitherSpelling()
    {
        using var temp = new TempDirectory();

        async Task<string> LinesOf(Worker worker, string flags)
        {
            var root = worker.Root;
            var lower = char.ToLowerInvariant(root[0]) + root[1..];
            var line = new NinjaEdge(
                "CXX_COMPILER__lib_",
                [LibObject],
                [worker.Native("src/a.cpp")],
                worker.Native("src/a.cpp"),
                "gcc",
                $"g++ -I{worker.Forward("include")} -I{Path.Combine(lower, "build")} -DROOT=\"{root.Replace("\\", "\\\\", StringComparison.Ordinal)}\"{flags} -c {worker.Native("src/a.cpp")}");

            return (await TableAsync(worker.Reader(), new BuiltFromSource("lib", [line], []))).Lines;
        }

        var first = await LinesOf(new Worker(temp, "w1"), string.Empty);

        Assert.Equal(first, await LinesOf(new Worker(temp, "another-worker"), string.Empty));
        Assert.NotEqual(first, await LinesOf(new Worker(temp, "w3"), " -O2"));
    }

    /// <summary>
    /// A list file edited to add another target changes nothing of what this one is built from: its table, read from a
    /// real build of the fixture before and after, is the same - the list file is a cause, never in a table.
    /// </summary>
    [Fact]
    public async Task AListFileEditedForAnotherTarget_ChangesNoTableOfThisOne()
    {
        var harness = new HarnessFactory();

        CheckMutationsEndToEndTests.SkipUnlessThisMachineBuilds(harness);

        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;

        using var clock = new ClockWatch();

        var repository = await CheckMutationsEndToEndTests.FixtureRepositoryAsync(harness, temp.Combine("r"), token);

        try
        {
            var before = await BuiltTableAsync(harness, temp, repository, "fixture", token);

            File.WriteAllText(Path.Combine(repository, "src", "extra.cpp"), "int extra() { return 2; }\n");
            File.AppendAllText(Path.Combine(repository, "CMakeLists.txt"), "\nadd_library(extra STATIC src/extra.cpp)\n");

            var after = await BuiltTableAsync(harness, temp, repository, "fixture", token);

            Assert.Contains("src/fixture.cpp", before.Files.Keys);
            Assert.DoesNotContain("CMakeLists.txt", before.Files.Keys);
            Assert.Equal(before.Lines, after.Lines);
            Assert.Equal(before.Files, after.Files);
            Assert.Equal(before.Digest(), after.Digest());
        }
        catch (Exception ex) when (clock.Explains(ex))
        {
            Assert.Skip($"Its builds did not run on an honest clock - {clock.Seen}: {ex.Message}");
        }
    }

    /// <summary>
    /// A source a list file's glob finds is in no table until a configure finds it, as a worker's does on each of its
    /// builds: then it joins the table of the target the glob builds, with the line that compiles it.
    /// </summary>
    [Fact]
    public async Task ASourceAGlobFinds_JoinsTheTable_AfterTheWorkersConfigure()
    {
        var harness = new HarnessFactory();

        CheckMutationsEndToEndTests.SkipUnlessThisMachineBuilds(harness);

        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;

        using var clock = new ClockWatch();

        const string Listed = "add_library(fixture STATIC src/fixture.cpp)";
        var repository = await CheckMutationsEndToEndTests.FixtureRepositoryAsync(
            harness,
            temp.Combine("r"),
            token,
            list =>
            {
                Assert.Contains(Listed, list, StringComparison.Ordinal);
                return list.Replace(Listed, "file(GLOB fixture_sources src/*.cpp)\nadd_library(fixture STATIC ${fixture_sources})", StringComparison.Ordinal);
            });

        try
        {
            var before = await BuiltTableAsync(harness, temp, repository, "fixture", token);

            File.WriteAllText(Path.Combine(repository, "src", "extra.cpp"), "int extra() { return 2; }\n");

            var after = await BuiltTableAsync(harness, temp, repository, "fixture", token);

            Assert.DoesNotContain("src/extra.cpp", before.Files.Keys);
            Assert.Contains("src/extra.cpp", after.Files.Keys);
            Assert.All(before.Files, file => Assert.Equal(file.Value, after.Files.GetValueOrDefault(file.Key)));
            Assert.NotEqual(before.Lines, after.Lines);
        }
        catch (Exception ex) when (clock.Explains(ex))
        {
            Assert.Skip($"Its builds did not run on an honest clock - {clock.Seen}: {ex.Message}");
        }
    }

    /// <summary>
    /// What a build's manifest is generated from is what the build line making it reads - the list files and the modules
    /// the configure read - and of those, the files of the tree are taken as the reading holds them, and no other.
    /// </summary>
    [Fact]
    public async Task WhatTheManifestIsGeneratedFrom_IsWhatItsLineReads_TheTreesFilesAsTheReadingHoldsThem()
    {
        var harness = new HarnessFactory();

        CheckMutationsEndToEndTests.SkipUnlessThisMachineBuilds(harness);

        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;

        using var clock = new ClockWatch();

        var repository = await CheckMutationsEndToEndTests.FixtureRepositoryAsync(harness, temp.Combine("r"), token);

        try
        {
            var (graph, reader) = await BuiltAsync(harness, temp, repository, token);
            var causes = reader.OfTheTree(graph.GeneratedFrom);

            Assert.Contains(graph.GeneratedFrom, path => !path.StartsWith(repository, StringComparison.OrdinalIgnoreCase));
            Assert.Equal(Hash(File.ReadAllText(Path.Combine(repository, "CMakeLists.txt"))), causes["CMakeLists.txt"]);
            Assert.All(causes.Keys, path => Assert.True(File.Exists(Path.Combine(repository, path)), path));
            Assert.DoesNotContain(causes.Keys, path => path.StartsWith(HarnessLayout.DirectoryName, StringComparison.Ordinal));
        }
        catch (Exception ex) when (clock.Explains(ex))
        {
            Assert.Skip($"Its builds did not run on an honest clock - {clock.Seen}: {ex.Message}");
        }
    }

    /// <summary>The SHA-256 of <paramref name="text"/> as UTF-8.</summary>
    private static string Hash(string text) => FileContentHash.Of(Encoding.UTF8.GetBytes(text));

    /// <summary>The table <paramref name="reader"/> makes of <paramref name="source"/>, which it must make.</summary>
    private static async Task<BuiltFromTable> TableAsync(BuiltFromReader reader, BuiltFromSource source)
    {
        var read = await reader.ReadAsync(source, TestContext.Current.CancellationToken);

        Assert.True(read.Table is not null, read.Unread);

        return read.Table;
    }

    /// <summary>
    /// <paramref name="target"/>'s table after the CLI builds the repository's leg - configuring, then building - read from
    /// the records of that build as a worker's are, with a reading of every file of the tree.
    /// </summary>
    private static async Task<BuiltFromTable> BuiltTableAsync(HarnessFactory harness, TempDirectory temp, string repository, string target, CancellationToken token)
    {
        var (graph, reader) = await BuiltAsync(harness, temp, repository, token);

        return await TableAsync(reader, graph.ReadBy(target));
    }

    /// <summary>
    /// The graph of the repository's leg once the CLI builds it - configuring, then building - read from the records of
    /// that build as a worker's are, and a reader of its tables over a reading of every file of the tree.
    /// </summary>
    private static async Task<(IWorkerGraph Graph, BuiltFromReader Reader)> BuiltAsync(HarnessFactory harness, TempDirectory temp, string repository, CancellationToken token)
    {
        var built = await CliRunner.RunAsync(["build", "--legs", "native", "--json", "-C", repository], token);

        Assert.True(built.ExitCode == HarnessExit.Success, built.StandardError + built.StandardOutput);

        var context = await harness.ContextLoader.LoadAsync(repository, token);
        var leg = context.Config.Legs["native"];
        var variant = VariantKey.For(context.Config, leg, harness.Platform.PlatformKey);
        var request = new BuildRequest("native", repository, VariantKey.ProjectFor(context.Config, leg)!, variant, harness.Platform.PlatformKey, 2, temp.Combine("runs"));
        var builder = new ArmBuilder(
            NSubstitute.Substitute.For<IBuildService>(),
            harness.ProcessRunner,
            new BuildDirectoryGuard(harness.FileSystem, harness.Platform, harness.FilePermissions),
            harness.FileSystem);
        var graph = await builder.ReadGraphAsync(context.Config, request, token);
        var build = variant.DirectoryUnder(repository);
        var git = Path.Combine(repository, ".git");
        var reading = Directory.EnumerateFiles(repository, "*", SearchOption.AllDirectories)
            .Where(file => !file.StartsWith(build + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !file.StartsWith(git + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .Select(file => (Path: Path.GetRelativePath(repository, file).Replace('\\', '/'), Bytes: File.ReadAllBytes(file)))
            .ToDictionary(file => file.Path, file => new SyncEntry(file.Path, file.Bytes.Length, FileContentHash.Of(file.Bytes)), StringComparer.Ordinal);
        var reader = new BuiltFromReader(harness.FileSystem, new TreeFiles(new SyncManifest(repository, reading), PathCase.In(harness.FileSystem, repository)), [], repository, build);

        return (graph, reader);
    }

    /// <summary>A worker's copy of a tree: its root, its build directory, and the sweep's reading of the tree.</summary>
    private sealed class Worker
    {
        public Worker(TempDirectory temp, string name)
        {
            Root = temp.Combine(name);
            Build = Path.Combine(Root, "build");
            Directory.CreateDirectory(Build);
        }

        public string Root { get; }

        public string Build { get; }

        /// <summary>The sweep's reading of the tree: each file of it, by its path, with what it holds.</summary>
        public Dictionary<string, SyncEntry> Reading { get; } = new(StringComparer.Ordinal);

        /// <summary><paramref name="path"/> in the worker, as its machine spells a path.</summary>
        public string Native(string path) => Path.Combine(Root, path.Replace('/', Path.DirectorySeparatorChar));

        /// <summary><paramref name="path"/> in the worker, with <c>/</c> throughout, as CMake spells it.</summary>
        public string Forward(string path) => Native(path).Replace('\\', '/');

        /// <summary><paramref name="path"/> in the worker, as a build line of ninja's manifest spells one.</summary>
        public string Ninja(string path)
            => Native(path).Replace("$", "$$", StringComparison.Ordinal).Replace(" ", "$ ", StringComparison.Ordinal).Replace(":", "$:", StringComparison.Ordinal);

        /// <summary>A file of the tree: in the worker's copy, and in the reading.</summary>
        public void Tree(string path, string text)
        {
            Write(path, text);
            Reading[path] = new SyncEntry(path, Encoding.UTF8.GetByteCount(text), Hash(text));
        }

        /// <summary>A file the worker holds that is no file of the tree.</summary>
        public void Write(string path, string text)
        {
            var file = Native(path);

            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, text);
        }

        /// <summary>A reader of this worker's tables, given <paramref name="fetched"/>, its names compared as <paramref name="names"/> says.</summary>
        public BuiltFromReader Reader(IReadOnlyList<FetchedReading>? fetched = null, StringComparer? names = null)
        {
            var fileSystem = new PhysicalFileSystem(FilePermissionsFactory.Create());

            return new BuiltFromReader(fileSystem, new TreeFiles(new SyncManifest(Root, Reading), names ?? PathCase.In(fileSystem, Root)), fetched ?? [], Root, Build);
        }

        /// <summary>The worker's graph, its manifest <paramref name="manifest"/> and its records <paramref name="records"/>, as <c>ninja -t deps</c> prints them.</summary>
        public NinjaWorkerGraph Graph(string manifest, params string[] records)
        {
            var fileSystem = new PhysicalFileSystem(FilePermissionsFactory.Create());

            File.WriteAllText(Path.Combine(Build, NinjaDependencyCheck.ManifestFileName), manifest);

            return new NinjaWorkerGraph(
                new NinjaRebuildGraph(Build, NinjaManifest.Read(fileSystem, Build), NinjaRebuildGraph.ReadRecords(records), fileSystem, PathCase.In(fileSystem, Build)),
                null);
        }
    }
}
