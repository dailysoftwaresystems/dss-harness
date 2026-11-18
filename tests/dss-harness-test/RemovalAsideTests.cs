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
    /// A build directory's removal names its aside by the process removing it - its id, and the stamp that tells it from a
    /// later holder of that id, each letter, digit and dash as it is and every other character by its code - beside the
    /// directory and hidden as any aside is, so that process alone deletes it; an aside says which process moved it there,
    /// stamp and all, one named by an id alone, as builds before this one wrote, says the id, and one named by none - which
    /// builds before this one wrote, and other removals still write - says none.
    /// </summary>
    [Fact]
    public void ARemovalsOwnAside_IsNamedByItsProcessAndItsStamp_AndSaysWhich()
    {
        using var temp = new TempDirectory();
        var directory = temp.Combine("repo", "build", "x86_64-gcc-debug");
        const string Stamp = "6f1c0d1e-90ab-4cde-8f01-23456789abcd:4182";
        var aside = temp.Combine("repo", "build", ".x86_64-gcc-debug.removing-4242-6f1c0d1e-90ab-4cde-8f01-23456789abcd_003A4182");

        Assert.Equal(aside, RemovalAside.Of(directory, 4242, Stamp));
        Assert.Equal("x86_64-gcc-debug", RemovalAside.Was(Path.GetFileName(aside)));
        Assert.Equal(new AsideRemover(4242, Stamp), RemovalAside.By(Path.GetFileName(aside)));
        Assert.Equal(temp.Combine("repo", "build", ".x86_64-gcc-debug.removing-4242"), RemovalAside.Of(directory, 4242, stamp: null));
        Assert.Equal(new AsideRemover(4242, null), RemovalAside.By(".x86_64-gcc-debug.removing-4242"));
        Assert.Equal(new AsideRemover(12, "638123456789012345"), RemovalAside.By(Path.GetFileName(RemovalAside.Of(directory, 12, "638123456789012345"))));
        Assert.Null(RemovalAside.By(".x86_64-gcc-debug.removing"));
        Assert.Null(RemovalAside.By("x86_64-gcc-debug"));
        Assert.Throws<ArgumentOutOfRangeException>(() => RemovalAside.Of(directory, 0, Stamp));
    }

    /// <summary>
    /// A stamp in an aside's name is read only as this class spells it: a name that holds one any other way - empty, a
    /// character it would have escaped, an escape of a character it writes as it is, or one not four upper-case hex
    /// digits - is no aside's, and never read as one naming some other process.
    /// </summary>
    [Theory]
    [InlineData(".build.removing-12-a_003A1", "build")]
    [InlineData(".build.removing-12--a", "build")]
    [InlineData(".build.removing-12-", null)]
    [InlineData(".build.removing-12-a.b", null)]
    [InlineData(".build.removing-12-a_003a1", null)]
    [InlineData(".build.removing-12-_0041", null)]
    [InlineData(".build.removing-12-a_003", null)]
    [InlineData(".build.removing-12-a_", null)]
    [InlineData(".build.removing-12-a:1", null)]
    public void OnlyAStampSpeltAsAnAsideSpellsIt_IsOne(string name, string? was)
        => Assert.Equal(was, RemovalAside.Was(name));
}
