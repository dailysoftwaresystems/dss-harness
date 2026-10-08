using RepoHarness.Core.FileSystem;

namespace RepoHarness.Tests;

/// <summary>Where a directory is renamed to while it is removed, and which names are such a directory's.</summary>
public sealed class RemovalAsideTests
{
    /// <summary>A directory's aside is beside it, hidden, under its own name; a trailing separator names the same one.</summary>
    [Fact]
    public void ADirectorysAside_IsBesideIt_HiddenUnderItsOwnName()
    {
        using var temp = new TempDirectory();
        var directory = temp.Combine("repo.mutation-x86_64-gcc-debug-1");
        var aside = temp.Combine(".repo.mutation-x86_64-gcc-debug-1.removing");

        Assert.Equal(aside, RemovalAside.Of(directory));
        Assert.Equal(aside, RemovalAside.Of(directory + Path.DirectorySeparatorChar));
        Assert.Equal(Path.GetFileName(directory), RemovalAside.Was(Path.GetFileName(aside)));
    }

    /// <summary>
    /// Only a name that starts with a dot and ends as an aside does, with a directory's name between them, is an
    /// aside's: any other is no aside, whatever else it resembles.
    /// </summary>
    [Theory]
    [InlineData(".build.removing", "build")]
    [InlineData(".a.removing", "a")]
    [InlineData("..removing.removing", ".removing")]
    [InlineData(".removing", null)]
    [InlineData("..removing", null)]
    [InlineData("build.removing", null)]
    [InlineData(".build.removed", null)]
    [InlineData(".build.removing.old", null)]
    [InlineData(".build.REMOVING", null)]
    [InlineData("", null)]
    public void OnlyANameSpeltAsAnAsides_IsOne(string name, string? was)
        => Assert.Equal(was, RemovalAside.Was(name));
}
