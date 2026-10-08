using RepoHarness.Core.Build;
using RepoHarness.Core.Mutations;
using RepoHarness.Core.Sync;

namespace RepoHarness.Tests;

/// <summary>
/// The dependency sources a leg's own build fetched, as each worker of its sweep is given them: each that is there found,
/// pointed at where the worker has it, and fetching turned off only where every one the build declared is.
/// </summary>
public sealed class FetchedSourcesTests
{
    /// <summary>
    /// Each dependency the leg's build declared is found where it is - fetched into the base directory under its name in
    /// lower case, or where its own entry names - and a worker is pointed at where it keeps each, spelt with forward
    /// separators, as CMake spells every path; with every one there, fetching is turned off, so a worker the network
    /// cannot reach still configures.
    /// </summary>
    [Fact]
    public void EachDependencyThere_IsFound_AndAWorkerIsPointedAtWhereItKeepsEach_WithFetchingOff()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var deps = temp.Combine("build", "_deps");
        var googletest = Directory.CreateDirectory(Path.Combine(deps, "googletest-src")).FullName;
        var json = Directory.CreateDirectory(temp.Combine("opt", "json")).FullName;
        var worker = temp.Combine("repo.mutation-x86_64-gcc-debug-1");

        var fetched = FetchedSources.Of(Record(deps, ("JSON", json), ("GOOGLETEST", string.Empty)), harness.FileSystem);

        Assert.Equal([new FetchedSource("GOOGLETEST", googletest), new FetchedSource("JSON", json)], fetched.Found);
        Assert.True(fetched.Every);
        Assert.Equal(Path.Combine(worker, ".harness-config", "deps"), FetchedSources.KeptIn(worker));
        Assert.Equal(Path.Combine(worker, ".harness-config", "deps", "googletest"), FetchedSources.KeptIn(worker, "GOOGLETEST"));

        var variables = FetchedSources.CacheVarsFor(fetched, source => FetchedSources.KeptIn(worker, source.Name));

        Assert.Equal(
            [
                (FetchedSources.FullyDisconnected, "ON"),
                ("FETCHCONTENT_SOURCE_DIR_GOOGLETEST", FetchedSources.KeptIn(worker, "GOOGLETEST").Replace('\\', '/')),
                ("FETCHCONTENT_SOURCE_DIR_JSON", FetchedSources.KeptIn(worker, "JSON").Replace('\\', '/')),
            ],
            variables.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => (pair.Key, pair.Value)));
        Assert.DoesNotContain(variables.Values, value => value.Contains('\\', StringComparison.Ordinal));
    }

    /// <summary>
    /// A dependency that is not there - never fetched, or fetched where nothing says - is not found, and fetching is
    /// left on, so a worker fetches it as the leg would; each other is still found.
    /// </summary>
    [Fact]
    public void ADependencyNotThere_LeavesFetchingOn_AndEachOtherIsStillFound()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var deps = temp.Combine("build", "_deps");
        var googletest = Directory.CreateDirectory(Path.Combine(deps, "googletest-src")).FullName;

        var missing = FetchedSources.Of(Record(deps, ("GOOGLETEST", string.Empty), ("JSON", string.Empty)), harness.FileSystem);
        var nowhere = FetchedSources.Of(Record(null, ("GOOGLETEST", string.Empty)), harness.FileSystem);
        var namedAway = FetchedSources.Of(Record(deps, ("GOOGLETEST", temp.Combine("moved"))), harness.FileSystem);

        Assert.Equal([new FetchedSource("GOOGLETEST", googletest)], missing.Found);
        Assert.False(missing.Every);
        Assert.Equal(
            [("FETCHCONTENT_SOURCE_DIR_GOOGLETEST", "/w/googletest")],
            FetchedSources.CacheVarsFor(missing, source => "/w/" + source.Name.ToLowerInvariant()).Select(pair => (pair.Key, pair.Value)));
        Assert.Equal((0, false), (nowhere.Found.Count, nowhere.Every));
        Assert.Equal((0, false), (namedAway.Found.Count, namedAway.Every));
    }

    /// <summary>A leg never built, or whose build declared no dependency, gives its workers nothing: they configure as it would.</summary>
    [Fact]
    public void ALegNeverBuilt_OrDeclaringNoDependency_GivesNothing()
    {
        var harness = new HarnessFactory();

        Assert.Same(FetchedSet.None, FetchedSources.Of(null, harness.FileSystem));
        Assert.Same(FetchedSet.None, FetchedSources.Of(Record("/nowhere/_deps"), harness.FileSystem));
        Assert.Empty(FetchedSources.CacheVarsFor(FetchedSet.None, _ => throw new InvalidOperationException("nothing is kept")));
    }

    /// <summary>
    /// Sources the tree's own reading carries are each worker's already, in its copy of the tree, and are told by where
    /// they are in it; sources outside the tree, or in what a sync withholds from it - the leg's own build directory -
    /// are none of the tree's, and neither is a directory whose name only starts as theirs does.
    /// </summary>
    [Theory]
    [InlineData("vendor/json", false, "vendor/json")]
    [InlineData("vendor/json/", false, "vendor/json")]
    [InlineData("Vendor/JSON", true, "Vendor/JSON")]
    [InlineData("Vendor/JSON", false, null)]
    [InlineData("vendor/js", false, null)]
    [InlineData("vendor", false, "vendor")]
    [InlineData("build/x86_64-gcc-debug/_deps/googletest-src", false, null)]
    [InlineData("", false, null)]
    [InlineData("../elsewhere/json", false, null)]
    public void SourcesTheTreesReadingCarries_AreToldByWhereTheyAreInIt(string directory, bool ignoringCase, string? within)
    {
        using var temp = new TempDirectory();
        var tree = temp.Combine("repo");
        var reading = new SyncManifest(
            tree,
            new Dictionary<string, SyncEntry>(StringComparer.Ordinal)
            {
                ["vendor/json/CMakeLists.txt"] = new("vendor/json/CMakeLists.txt", 1, "a"),
                ["vendor/json/include/json.hpp"] = new("vendor/json/include/json.hpp", 2, "b"),
                ["src/main.cpp"] = new("src/main.cpp", 3, "c"),
            });
        var source = new FetchedSource("JSON", Path.GetFullPath(Path.Combine(tree, directory.Replace('/', Path.DirectorySeparatorChar))));

        Assert.Equal(
            within,
            FetchedSources.CarriedAt(source, tree, reading, ignoringCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal));
    }

    /// <summary>
    /// What a leg's build directory says FetchContent declared: <paramref name="sources"/>, each by name with the directory
    /// its entry names, or none; and <paramref name="fetchedInto"/>, where it puts what it fetches, or nothing said.
    /// </summary>
    private static BuildDirectoryRecord Record(string? fetchedInto, params (string Name, string Named)[] sources) => new(null, null, null, null)
    {
        FetchContentSources = sources.ToDictionary(source => source.Name, source => source.Named, StringComparer.Ordinal),
        FetchContentBaseDirectory = fetchedInto,
    };
}
