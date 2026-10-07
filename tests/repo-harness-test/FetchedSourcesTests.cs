using RepoHarness.Core.Build;
using RepoHarness.Core.Mutations;

namespace RepoHarness.Tests;

/// <summary>
/// The dependency sources a leg's own build fetched, as each worker of its sweep is given them: each that is there pointed
/// at, and fetching turned off only where every one the build declared is.
/// </summary>
public sealed class FetchedSourcesTests
{
    /// <summary>
    /// Each dependency the leg's build declared is pointed at where it is - fetched into the base directory under its
    /// name in lower case, or where its own entry names - spelt with forward separators, as CMake spells every path; and
    /// with every one there, fetching is turned off, so a worker the network cannot reach still configures.
    /// </summary>
    [Fact]
    public void EachDependencyThere_IsPointedAt_AndWithEveryOneThereFetchingIsOff()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var deps = temp.Combine("build", "_deps");
        var googletest = Directory.CreateDirectory(Path.Combine(deps, "googletest-src")).FullName;
        var json = Directory.CreateDirectory(temp.Combine("opt", "json")).FullName;

        var variables = FetchedSources.CacheVarsFor(Record(deps, ("GOOGLETEST", string.Empty), ("JSON", json)), harness.FileSystem);

        Assert.Equal(
            [
                (FetchedSources.FullyDisconnected, "ON"),
                ("FETCHCONTENT_SOURCE_DIR_GOOGLETEST", googletest.Replace('\\', '/')),
                ("FETCHCONTENT_SOURCE_DIR_JSON", json.Replace('\\', '/')),
            ],
            variables.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => (pair.Key, pair.Value)));
    }

    /// <summary>
    /// A dependency that is not there - never fetched, or fetched where nothing says - is not pointed at, and fetching is
    /// left on, so a worker fetches it as the leg would; each other is still pointed at.
    /// </summary>
    [Fact]
    public void ADependencyNotThere_LeavesFetchingOn_AndEachOtherIsStillPointedAt()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var deps = temp.Combine("build", "_deps");
        var googletest = Directory.CreateDirectory(Path.Combine(deps, "googletest-src")).FullName;

        var missing = FetchedSources.CacheVarsFor(Record(deps, ("GOOGLETEST", string.Empty), ("JSON", string.Empty)), harness.FileSystem);
        var nowhere = FetchedSources.CacheVarsFor(Record(null, ("GOOGLETEST", string.Empty)), harness.FileSystem);
        var namedAway = FetchedSources.CacheVarsFor(Record(deps, ("GOOGLETEST", temp.Combine("moved"))), harness.FileSystem);

        Assert.Equal([("FETCHCONTENT_SOURCE_DIR_GOOGLETEST", googletest.Replace('\\', '/'))], missing.Select(pair => (pair.Key, pair.Value)));
        Assert.Empty(nowhere);
        Assert.Empty(namedAway);
    }

    /// <summary>A leg never built, or whose build declared no dependency, gives its workers nothing: they configure as it would.</summary>
    [Fact]
    public void ALegNeverBuilt_OrDeclaringNoDependency_GivesNothing()
    {
        var harness = new HarnessFactory();

        Assert.Empty(FetchedSources.CacheVarsFor(null, harness.FileSystem));
        Assert.Empty(FetchedSources.CacheVarsFor(Record("/nowhere/_deps"), harness.FileSystem));
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
