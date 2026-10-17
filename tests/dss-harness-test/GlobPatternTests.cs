using DssHarness.Core.Repository;
using DssHarness.Core.Results;

namespace DssHarness.Tests;

/// <summary>
/// The grammar of a path pattern the agent clients' rules take: gitignore's, read by
/// <see cref="GlobPattern"/> alone and never by the configured path lists' matcher, whose grammar
/// is poorer on purpose. Each rule of the grammar is pinned here, because nothing else reads a
/// pattern until the policy that renders these rules is built.
/// </summary>
public sealed class GlobPatternTests
{
    /// <summary>
    /// A trailing <c>/**</c> matches everything beneath what precedes it and not that path itself:
    /// a rule refuses what is written <em>in</em> a place, and the example the plan itself gives is
    /// <c>.github/**</c>.
    /// </summary>
    [Fact]
    public void ATrailingDoubleStar_MatchesEverythingBeneath_AndNotThePlaceItself()
    {
        var pattern = GlobPattern.Parse(".github/**");

        Assert.True(pattern.Matches(".github/workflows/ci.yml"));
        Assert.True(pattern.Matches(".github/a"));
        Assert.False(pattern.Matches(".github"));
        Assert.False(pattern.Matches("docs/.github/notes"));
    }

    /// <summary>A leading <c>**/</c> matches the name that follows at any depth, and at the root.</summary>
    [Fact]
    public void ALeadingDoubleStar_MatchesTheNameAtAnyDepth()
    {
        var pattern = GlobPattern.Parse("**/cache");

        Assert.True(pattern.Matches("cache"));
        Assert.True(pattern.Matches("src/cache"));
        Assert.True(pattern.Matches("src/cache/x.tmp"));

        // Matched on whole segments: a name that merely ends the same way is a different name.
        Assert.False(pattern.Matches("src/mycache"));
    }

    /// <summary>
    /// A <c>**</c> in the middle matches any number of segments, none included: gitignore's own
    /// example, where a zero-segment match is the case a reader would otherwise file as a bug.
    /// </summary>
    [Fact]
    public void AMiddleDoubleStar_MatchesAnyNumberOfSegments_NoneIncluded()
    {
        var pattern = GlobPattern.Parse("a/**/b");

        Assert.True(pattern.Matches("a/b"));
        Assert.True(pattern.Matches("a/x/b"));
        Assert.True(pattern.Matches("a/x/y/b"));
        Assert.False(pattern.Matches("a"));
        Assert.False(pattern.Matches("b"));
    }

    /// <summary>
    /// A <c>*</c> matches any run of characters within one segment and never across a separator:
    /// the one difference from a shell glob that a rule's meaning turns on.
    /// </summary>
    [Fact]
    public void AStar_MatchesWithinASegment_AndNeverAcrossASeparator()
    {
        var pattern = GlobPattern.Parse("docs/*.md");

        Assert.True(pattern.Matches("docs/readme.md"));
        Assert.True(pattern.Matches("docs/a-b-c.md"));
        Assert.False(pattern.Matches("docs/inner/readme.md"));
        Assert.False(pattern.Matches("docs/readme.txt"));
    }

    /// <summary>A <c>?</c> matches one character of a segment, and nothing else does.</summary>
    [Fact]
    public void AQuestionMark_MatchesExactlyOneCharacter()
    {
        var pattern = GlobPattern.Parse("file?.txt");

        Assert.True(pattern.Matches("file1.txt"));
        Assert.True(pattern.Matches("fileX.txt"));
        Assert.False(pattern.Matches("file.txt"));
        Assert.False(pattern.Matches("file12.txt"));
    }

    /// <summary>
    /// A pattern holding a separator is matched from the root; one holding none matches that name
    /// at any depth - and everything beneath a matched name, as gitignore reads it.
    /// </summary>
    [Fact]
    public void APatternHoldingNoSeparator_MatchesTheNameAtAnyDepth_AndBeneathIt()
    {
        var name = GlobPattern.Parse("notes");

        Assert.True(name.Matches("notes"));
        Assert.True(name.Matches("src/notes"));
        Assert.True(name.Matches("src/notes/today.txt"));
        Assert.False(name.Matches("release-notes"));

        var rooted = GlobPattern.Parse("docs/README.md");

        Assert.True(rooted.Matches("docs/README.md"));
        Assert.False(rooted.Matches("en/docs/README.md"));
    }

    /// <summary>
    /// Backslashes, a leading <c>./</c> and trailing separators are normalised away, so a pattern
    /// written one way matches a path written the other.
    /// </summary>
    [Fact]
    public void APatternOrPathSpeltWithTheOtherSeparator_MatchesAllTheSame()
    {
        var pattern = GlobPattern.Parse(@"docs\**\*.md");

        Assert.True(pattern.Matches("docs/readme.md"));
        Assert.True(pattern.Matches(@"docs\inner\readme.md"));
        Assert.True(pattern.Matches("./docs/inner/readme.md"));

        // A leading separator is normalised away on the candidate's side as on the pattern's: what
        // the rule rules on is a relative path, spelled either way.
        Assert.True(GlobPattern.Parse("docs/**").Matches("/docs/readme.md"));

        Assert.Equal("docs/**/*.md", pattern.ToString());
    }

    /// <summary>
    /// A pattern that cannot be read is refused naming why, and brackets are ordinary characters:
    /// a reader who writes a character class is told what the matcher does with it rather than
    /// having it guessed at.
    /// </summary>
    [Fact]
    public void APatternThatCannotBeRead_IsRefusedNamingWhy()
    {
        Assert.Equal("is empty", GlobPattern.Problem(""));
        Assert.Equal("is empty", GlobPattern.Problem("/"));

        Assert.Equal(
            "holds '**' inside a segment; '**' is a segment of its own, standing for any number of segments",
            GlobPattern.Problem("a**b"));
        Assert.Equal(
            "holds '**' inside a segment; '**' is a segment of its own, standing for any number of segments",
            GlobPattern.Problem("***"));

        // A misspelling the file system would read past is refused by the one spelling rule this
        // shares with the configured path lists: a pattern that looks right and matches nothing is
        // a rule that silently protects nothing.
        Assert.StartsWith("holds a '.' segment or a doubled separator", GlobPattern.Problem("a//b"));
        Assert.StartsWith("holds a '.' segment or a doubled separator", GlobPattern.Problem("a/./b"));

        Assert.Null(GlobPattern.Problem("[a]/x"));
        Assert.Null(GlobPattern.Problem("**"));

        var refused = Assert.Throws<HarnessException>(() => GlobPattern.Parse("src/**cache"));
        Assert.Equal(
            "the pattern 'src/**cache' holds '**' inside a segment; '**' is a segment of its own, standing for any number of segments",
            refused.Message);
        Assert.Equal(HarnessExit.ConfigInvalid, refused.ExitCode);
    }

    /// <summary>
    /// A leading separator anchors the pattern to the root, exactly as a middle one does, though
    /// normalising the pattern takes the separator away: the anchoring is decided from the pattern
    /// as written.
    /// </summary>
    [Fact]
    public void ALeadingSeparator_AnchorsThePattern_AsAMiddleOneDoes()
    {
        var pattern = GlobPattern.Parse("/docs");

        Assert.True(pattern.Matches("docs"));
        Assert.True(pattern.Matches("docs/readme.md"));
        Assert.False(pattern.Matches("src/docs"));
    }

    /// <summary>
    /// The extreme shapes of the grammar, pinned because a rule turns on each: a pattern of only
    /// <c>**</c> matches every path; so does <c>**/**</c>, through a leading <c>**</c> that takes
    /// no segments; and brackets match as the ordinary characters they are, not as a class.
    /// </summary>
    [Fact]
    public void TheExtremeShapes_MatchAsTheGrammarSays()
    {
        foreach (var everything in new[] { "**", "**/**" })
        {
            Assert.True(GlobPattern.Parse(everything).Matches("a"));
            Assert.True(GlobPattern.Parse(everything).Matches("a/b/c"));
        }

        var brackets = GlobPattern.Parse("[a]");

        Assert.True(brackets.Matches("[a]"));
        Assert.False(brackets.Matches("a"));
    }

    /// <summary>
    /// A plain anchored pattern covers everything beneath what it matches, as the <c>**</c> shapes
    /// do: gitignore matches a directory by matching what is written inside it.
    /// </summary>
    [Fact]
    public void APlainAnchoredPattern_CoversEverythingBeneathItsMatch()
    {
        var pattern = GlobPattern.Parse("docs/README.md");

        Assert.True(pattern.Matches("docs/README.md"));
        Assert.True(pattern.Matches("docs/README.md/inner/note.txt"));
        Assert.False(pattern.Matches("docs/other.md"));
    }

    /// <summary>A <c>?</c> matches one character of a segment, and a whole segment may be only it.</summary>
    [Fact]
    public void AWholeSegmentOfQuestionMarks_MatchesThatManyCharacters()
    {
        var pattern = GlobPattern.Parse("docs/?");

        Assert.True(pattern.Matches("docs/a"));
        Assert.False(pattern.Matches("docs/ab"));
    }
}
