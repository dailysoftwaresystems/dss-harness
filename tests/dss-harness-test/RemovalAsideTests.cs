using DssHarness.Core.FileSystem;

namespace DssHarness.Tests;

/// <summary>Where a directory is renamed to while it is removed, and which names are such a directory's.</summary>
public sealed class RemovalAsideTests
{
    /// <summary>A directory's aside is beside it, hidden, under its own name; a trailing separator names the same one.</summary>
    [Fact]
    public void ADirectorysAside_IsBesideIt_HiddenUnderItsOwnName()
    {
        using var temp = new TempDirectory();
        var directory = temp.Combine("repo", ".m", "357e24cw-1");
        var aside = temp.Combine("repo", ".m", ".357e24cw-1.removing");

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
    [InlineData(".build.removing-12", "build")]
    [InlineData(".build.removing-12.removing", "build.removing-12")]
    [InlineData(".build.removing-", null)]
    [InlineData(".build.removing-x", null)]
    [InlineData(".build.removing-012", null)]
    [InlineData(".build.removing-0", null)]
    [InlineData(".build.removing--1", null)]
    public void OnlyANameSpeltAsAnAsides_IsOne(string name, string? was)
        => Assert.Equal(was, RemovalAside.Was(name));

    /// <summary>
    /// A build directory's removal names its aside by the process removing it, beside the directory and hidden as any aside
    /// is, so that process alone deletes it; an aside says which process moved it there, and one named by none - which
    /// builds before this one wrote, and other removals still write - says none.
    /// </summary>
    [Fact]
    public void ARemovalsOwnAside_IsNamedByItsProcess_AndSaysWhich()
    {
        using var temp = new TempDirectory();
        var directory = temp.Combine("repo", "build", "x86_64-gcc-debug");
        var aside = temp.Combine("repo", "build", ".x86_64-gcc-debug.removing-4242");

        Assert.Equal(aside, RemovalAside.Of(directory, 4242));
        Assert.Equal("x86_64-gcc-debug", RemovalAside.Was(Path.GetFileName(aside)));
        Assert.Equal(4242, RemovalAside.By(Path.GetFileName(aside)));
        Assert.Null(RemovalAside.By(".x86_64-gcc-debug.removing"));
        Assert.Null(RemovalAside.By("x86_64-gcc-debug"));
        Assert.Throws<ArgumentOutOfRangeException>(() => RemovalAside.Of(directory, 0));
    }
}
