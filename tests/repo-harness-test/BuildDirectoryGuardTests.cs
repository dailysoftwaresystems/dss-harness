using RepoHarness.Core.Build;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Hosts;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Results;

namespace RepoHarness.Tests;

/// <summary>Which tree a build directory belongs to, as the guard in front of every build reads it.</summary>
public sealed class BuildDirectoryGuardTests
{
    /// <summary>
    /// A build directory configured from the tree spelt through a link is that tree's own: CMake compares the
    /// two with the link followed, and git names the tree with it followed. Refused, it was deleted for nothing
    /// - and on a host answering another machine, which writes both spellings of its home as <c>~</c>, the
    /// refusal named one path twice. A directory configured from a tree that really is another is still refused.
    /// </summary>
    [Fact]
    public void ADirectoryConfiguredFromTheTreeThroughALink_IsItsOwn_AndOneFromAnotherTreeIsStillRefused()
    {
        using var temp = new TempDirectory();
        var tree = Directory.CreateDirectory(temp.Combine("real", "app")).FullName;
        var build = Directory.CreateDirectory(Path.Combine(tree, "build", "x")).FullName;
        var linked = temp.Combine("linked");
        var guard = new BuildDirectoryGuard(new LinkedTo(linked, temp.Combine("real")), new HostPlatform(), FilePermissionsFactory.Create());
        var cache = Path.Combine(build, BuildDirectoryGuard.CMakeCacheFileName);

        File.WriteAllText(cache, $"CMAKE_HOME_DIRECTORY:INTERNAL={Path.Combine(linked, "app").Replace('\\', '/')}\n");
        guard.Check(build, tree, null, null, null, new PathSearch(null, []));

        File.WriteAllText(cache, $"CMAKE_HOME_DIRECTORY:INTERNAL={temp.Combine("other", "app").Replace('\\', '/')}\n");
        var refusal = Assert.Throws<HarnessException>(() => guard.Check(build, tree, null, null, null, new PathSearch(null, [])));

        Assert.Equal(HarnessExit.Refused, refusal.ExitCode);
        Assert.Contains("was configured from", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Each dependency FetchContent declared is read from the cache by its <c>FETCHCONTENT_SOURCE_DIR_&lt;NAME&gt;</c> entry -
    /// with the directory it names, or nothing where the build fetched it - by that entry's type alone, never the one CMake
    /// marks it advanced with; where FetchContent puts what it fetches is read beside them, and a cache declaring none has
    /// none.
    /// </summary>
    [Fact]
    public void EachDependencyFetchContentDeclared_IsReadFromTheCache_ByItsPathEntryAlone()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var guard = new BuildDirectoryGuard(harness.FileSystem, harness.Platform, harness.FilePermissions);

        temp.WriteFile(
            Path.Combine("fetched", BuildDirectoryGuard.CMakeCacheFileName),
            string.Join(
                "\r\n",
                "CMAKE_HOME_DIRECTORY:INTERNAL=/src/app",
                "FETCHCONTENT_BASE_DIR:PATH=/src/app/build/_deps",
                "FETCHCONTENT_FULLY_DISCONNECTED:BOOL=OFF",
                "FETCHCONTENT_SOURCE_DIR_GOOGLETEST:PATH=",
                "FETCHCONTENT_SOURCE_DIR_GOOGLETEST-ADVANCED:INTERNAL=1",
                "FETCHCONTENT_SOURCE_DIR_JSON:PATH=/opt/json ",
                "FETCHCONTENT_UPDATES_DISCONNECTED_JSON:BOOL=OFF",
                string.Empty));
        temp.WriteFile(Path.Combine("plain", BuildDirectoryGuard.CMakeCacheFileName), "CMAKE_HOME_DIRECTORY:INTERNAL=/src/app\n");

        var fetched = guard.Read(temp.Combine("fetched"))!;
        var plain = guard.Read(temp.Combine("plain"))!;

        Assert.Equal(
            [("GOOGLETEST", string.Empty), ("JSON", "/opt/json")],
            fetched.FetchContentSources.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => (pair.Key, pair.Value)));
        Assert.Equal("/src/app/build/_deps", fetched.FetchContentBaseDirectory);
        Assert.Equal("/src/app", fetched.HomeDirectory);
        Assert.Empty(plain.FetchContentSources);
        Assert.Null(plain.FetchContentBaseDirectory);
    }

    /// <summary>The real file system, except that <paramref name="link"/> leads to <paramref name="target"/>.</summary>
    private sealed class LinkedTo(string link, string target) : PassThroughFileSystem(new PhysicalFileSystem(FilePermissionsFactory.Create()))
    {
        public override string ResolveLinks(string path)
            => path.StartsWith(link, StringComparison.OrdinalIgnoreCase) ? target + path[link.Length..] : base.ResolveLinks(path);
    }
}
