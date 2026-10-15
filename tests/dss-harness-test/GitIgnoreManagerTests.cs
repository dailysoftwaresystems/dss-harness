using DssHarness.Core.FileSystem;
using DssHarness.Core.Platform;
using DssHarness.Core.Repository;

namespace DssHarness.Tests;

public sealed class GitIgnoreManagerTests
{
    private static readonly string[] Rules = ["/a", "!/a/.gitkeep"];

    private static GitIgnoreManager CreateManager()
        => new(new PhysicalFileSystem(FilePermissionsFactory.Create()));

    [Fact]
    public void ApplyManagedBlock_WritesBlock_WhenFileIsEmpty()
    {
        var result = CreateManager().ApplyManagedBlock(null, Rules);

        Assert.Contains(GitIgnoreManager.BeginMarker, result, StringComparison.Ordinal);
        Assert.Contains(GitIgnoreManager.EndMarker, result, StringComparison.Ordinal);
        Assert.Contains("/a", result, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplyManagedBlock_PreservesExistingRules()
    {
        var existing = "# mine\nbin/\nobj/\n";

        var result = CreateManager().ApplyManagedBlock(existing, Rules);

        Assert.Contains("# mine", result, StringComparison.Ordinal);
        Assert.Contains("bin/", result, StringComparison.Ordinal);
        Assert.Contains("obj/", result, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplyManagedBlock_ReplacesPreviousBlock_RatherThanAppendingASecond()
    {
        var manager = CreateManager();
        var once = manager.ApplyManagedBlock("bin/\n", ["/old"]);

        var twice = manager.ApplyManagedBlock(once, ["/new"]);

        Assert.Equal(1, CountOccurrences(twice, GitIgnoreManager.BeginMarker));
        Assert.Contains("/new", twice, StringComparison.Ordinal);
        Assert.DoesNotContain("/old", twice, StringComparison.Ordinal);
        Assert.Contains("bin/", twice, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplyManagedBlock_IsStable_WhenRulesAreUnchanged()
    {
        var manager = CreateManager();
        var once = manager.ApplyManagedBlock("bin/\n", Rules);

        var twice = manager.ApplyManagedBlock(once, Rules);

        Assert.Equal(once, twice);
    }

    [Fact]
    public void ApplyManagedBlock_KeepsCrlf_WhenTheFileUsesCrlf()
    {
        var result = CreateManager().ApplyManagedBlock("bin/\r\nobj/\r\n", Rules);

        // Every line ending must still be CRLF: rewriting a CRLF file with LF endings
        // would show every line as changed in a diff over a two line addition.
        var withoutCrlf = result.Replace("\r\n", string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", withoutCrlf, StringComparison.Ordinal);
    }

    [Fact]
    public void Update_ReportsChanged_OnlyWhenContentActuallyChanges()
    {
        using var temp = new TempDirectory();
        var manager = CreateManager();
        var path = temp.Combine(".gitignore");

        Assert.Equal(GitIgnoreUpdate.Updated, manager.Update(path, Rules));
        Assert.Equal(GitIgnoreUpdate.Unchanged, manager.Update(path, Rules));
        Assert.Equal(GitIgnoreUpdate.Updated, manager.Update(path, ["/different"]));
    }

    /// <summary>
    /// A block a build from before the rename wrote, under the former markers, is rewritten under the current ones where it
    /// stood, every other line as it was, in a file with either line ending.
    /// </summary>
    /// <param name="newline">The file's line ending.</param>
    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void ABlockWrittenUnderTheRetiredMarkers_IsRewrittenUnderTheCurrentOnes_WhereItStood(string newline)
    {
        var existing = $"bin/{newline}{GitIgnoreManager.RetiredBeginMarker}{newline}/old{newline}{GitIgnoreManager.RetiredEndMarker}{newline}after/{newline}";

        var result = CreateManager().ApplyManagedBlock(existing, Rules);

        Assert.Equal(
            $"bin/{newline}{GitIgnoreManager.BeginMarker}{newline}/a{newline}!/a/.gitkeep{newline}{GitIgnoreManager.EndMarker}{newline}after/{newline}",
            result);
    }

    /// <summary>A block under each spelling - one left by a teammate on an earlier build - becomes one block, at the first one's place.</summary>
    [Fact]
    public void ABlockUnderEachSpelling_BecomesOneBlock_AtTheFirstOnesPlace()
    {
        var existing = $"bin/\n{GitIgnoreManager.RetiredBeginMarker}\n/old\n{GitIgnoreManager.RetiredEndMarker}\nmid/\n"
            + $"{GitIgnoreManager.BeginMarker}\n/older\n{GitIgnoreManager.EndMarker}\nafter/\n";

        var result = CreateManager().ApplyManagedBlock(existing, Rules);

        Assert.Equal($"bin/\n{GitIgnoreManager.BeginMarker}\n/a\n!/a/.gitkeep\n{GitIgnoreManager.EndMarker}\nmid/\nafter/\n", result);
    }

    /// <summary>
    /// A file whose block moved from the former markers says so, once: the next run finds its block current and leaves the
    /// file as it is.
    /// </summary>
    [Fact]
    public void ARewrittenFile_IsLeftAsItIs_ByTheNextRun()
    {
        using var temp = new TempDirectory();
        var manager = CreateManager();
        var path = temp.Combine(".gitignore");
        File.WriteAllText(path, $"bin/\n{GitIgnoreManager.RetiredBeginMarker}\n/a\n!/a/.gitkeep\n{GitIgnoreManager.RetiredEndMarker}\n");

        Assert.Equal(GitIgnoreUpdate.MovedFromRetiredMarkers, manager.Update(path, Rules));

        var rewritten = File.ReadAllText(path);

        Assert.Equal(GitIgnoreUpdate.Unchanged, manager.Update(path, Rules));
        Assert.Equal(rewritten, File.ReadAllText(path));
        Assert.DoesNotContain("repo-harness", rewritten, StringComparison.Ordinal);
    }

    /// <summary>A former begin marker no end follows encloses nothing, as a current one does not: what follows it is kept.</summary>
    [Fact]
    public void ARetiredBeginWithNoEnd_KeepsWhatItEncloses()
    {
        var result = CreateManager().ApplyManagedBlock($"{GitIgnoreManager.RetiredBeginMarker}\nmine/\n", Rules);

        Assert.Equal($"{GitIgnoreManager.BeginMarker}\n/a\n!/a/.gitkeep\n{GitIgnoreManager.EndMarker}\nmine/\n", result);
    }

    /// <summary>Markers pair by position, never by spelling: a begin of one name and an end of the other are one block.</summary>
    /// <param name="formerBegin">Whether the begin marker is the former one, and the end the current one; otherwise the other way.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AMixedPair_IsReadAsOneBlock(bool formerBegin)
    {
        var begin = formerBegin ? GitIgnoreManager.RetiredBeginMarker : GitIgnoreManager.BeginMarker;
        var end = formerBegin ? GitIgnoreManager.EndMarker : GitIgnoreManager.RetiredEndMarker;
        var existing = $"bin/\n{begin}\n/old\n{end}\nafter/\n";

        Assert.Equal(
            $"bin/\n{GitIgnoreManager.BeginMarker}\n/a\n!/a/.gitkeep\n{GitIgnoreManager.EndMarker}\nafter/\n",
            CreateManager().ApplyManagedBlock(existing, Rules));
        Assert.Equal("bin/\n\n\n\nafter/\n", GitIgnoreManager.WithoutManagedBlock(existing));
    }

    /// <summary>
    /// With the block taken out, every other line keeps its number - the one git names a rule by -
    /// in a file with either line ending.
    /// </summary>
    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void WithoutManagedBlock_BlanksTheBlock_AndKeepsEveryOtherLineWhereItWas(string newline)
    {
        var content = CreateManager().ApplyManagedBlock($"bin/{newline}!/a/x{newline}", Rules) + $"after/{newline}";

        var lines = GitIgnoreManager.WithoutManagedBlock(content).Split('\n').Select(line => line.TrimEnd('\r'));

        Assert.Equal(["bin/", "!/a/x", "", "", "", "", "", "after/", ""], lines);
    }

    /// <summary>
    /// A block under the former markers is the managed block too: blanked, every other line keeping its number, so none of
    /// its rules is read as the repository's own.
    /// </summary>
    [Fact]
    public void WithoutManagedBlock_BlanksABlockUnderTheRetiredMarkers_AndKeepsEveryOtherLineWhereItWas()
    {
        var content = $"bin/\n{GitIgnoreManager.RetiredBeginMarker}\n/old\n{GitIgnoreManager.RetiredEndMarker}\nafter/\n";

        Assert.Equal("bin/\n\n\n\nafter/\n", GitIgnoreManager.WithoutManagedBlock(content));
    }

    /// <summary>
    /// A begin marker no end follows encloses nothing, as the block is paired when it is written: the
    /// lines after it are the repository's own, and blanked they would be rules nobody asked about.
    /// </summary>
    [Fact]
    public void WithoutManagedBlock_LeavesAnUnpairedMarker_AndWhatFollowsIt()
    {
        var content = $"{GitIgnoreManager.BeginMarker}\nmine/\n";

        Assert.Equal(content, GitIgnoreManager.WithoutManagedBlock(content));
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;

        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }
}
