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

    /// <summary>The real file system, except that <paramref name="link"/> leads to <paramref name="target"/>.</summary>
    private sealed class LinkedTo(string link, string target) : PassThroughFileSystem(new PhysicalFileSystem(FilePermissionsFactory.Create()))
    {
        public override string ResolveLinks(string path)
            => path.StartsWith(link, StringComparison.OrdinalIgnoreCase) ? target + path[link.Length..] : base.ResolveLinks(path);
    }
}
