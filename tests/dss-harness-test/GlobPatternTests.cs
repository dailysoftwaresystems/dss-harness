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

    /// <summary>A path of many segments is matched in time: every depth it could be covered at is decided in one pass.</summary>
    [Fact]
    public void APathOfManySegments_IsMatchedInTime()
    {
        var path = string.Join('/', Enumerable.Repeat("a", 64_000));
        var clock = System.Diagnostics.Stopwatch.StartNew();

        Assert.False(GlobPattern.Parse("**/private/**").Matches(path));
        Assert.True(GlobPattern.Parse("**/a/**").Matches(path));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"took {clock.Elapsed}");
    }

    /// <summary>
    /// A pattern of many <c>**</c> is matched in time however many ways it could split a path among them - each place in
    /// the pattern against each in the path decided once - and a run of <c>**</c> is one, a trailing run one or more still.
    /// </summary>
    [Fact]
    public void APatternOfManyDoubleStars_IsMatchedInTime()
    {
        var spread = GlobPattern.Parse(string.Concat(Enumerable.Repeat("**/a/", 15)) + "zzz");
        var clock = System.Diagnostics.Stopwatch.StartNew();

        Assert.False(spread.Matches(string.Join('/', Enumerable.Repeat("a", 30))));
        Assert.True(spread.Matches(string.Join('/', Enumerable.Repeat("a", 30)) + "/zzz"));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"took {clock.Elapsed}");

        Assert.Equal("a/**/b", GlobPattern.Parse("a/**/**/**/b").ToString());
        Assert.Equal("a/**", GlobPattern.Parse("a/**/**").ToString());
        Assert.False(GlobPattern.Parse("a/**/**").Matches("a"));
        Assert.True(GlobPattern.Parse("a/**/**").Matches("a/b"));
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

        // A name may hold a '*' of its own, which the pattern's '*' runs over as any other character.
        Assert.True(GlobPattern.Parse("**/*.env").Matches("*prod.env"));
        Assert.True(GlobPattern.Parse("**/*.env").Matches("a/*x.env"));
        Assert.True(GlobPattern.Parse("**/secret*").Matches("secret*x"));
        Assert.False(GlobPattern.Parse("**/*.env").Matches("*prod.txt"));
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
    /// Backslashes are read as separators and a leading <c>./</c> or separator on a path is read
    /// past, so a pattern written one way matches a path written the other.
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
        Assert.Equal(
            "holds a '.' segment or a doubled separator, which the file system reads past and a comparison of paths never does; write 'a/b'",
            GlobPattern.Problem("a//b"));
        Assert.Equal(
            "holds a '.' segment or a doubled separator, which the file system reads past and a comparison of paths never does; write 'a/b'",
            GlobPattern.Problem("a/./b"));

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

    /// <summary>
    /// A trailing separator anchors nothing, as gitignore reads it - only a leading or middle one
    /// does - and says the pattern names a directory: <c>build/</c> matches what is beneath a
    /// <c>build</c> at any depth, and never a path whose last segment is <c>build</c>, which is all
    /// a rule asked about a path can know is not a directory. The rules are handed to a client as its
    /// own deny rules, which read the pattern so.
    /// </summary>
    [Fact]
    public void ATrailingSeparator_AnchorsNothing_AndMatchesOnlyWhatIsBeneathADirectory()
    {
        var directory = GlobPattern.Parse("build/");

        Assert.True(directory.Matches("build/out.o"));
        Assert.True(directory.Matches("src/build/out.o"));
        Assert.True(directory.Matches("src/build/obj/out.o"));
        Assert.False(directory.Matches("build"));
        Assert.False(directory.Matches("src/build"));
        Assert.False(directory.Matches("src/rebuild/out.o"));

        // A middle separator anchors it, the trailing one still naming a directory.
        var rooted = GlobPattern.Parse("src/build/");

        Assert.True(rooted.Matches("src/build/out.o"));
        Assert.False(rooted.Matches("src/build"));
        Assert.False(rooted.Matches("lib/src/build/out.o"));
    }

    /// <summary>
    /// A pattern's spelling reads back as the same matcher, so a rule rendered from it means what the
    /// pattern meant: anchoring and a trailing separator survive, where joining the segments alone
    /// once made <c>/docs</c> read as <c>docs</c> - a name at any depth.
    /// </summary>
    [Theory]
    [InlineData("/docs", "/docs")]
    [InlineData("./docs", "/docs")]
    [InlineData("docs", "docs")]
    [InlineData("build/", "build/")]
    [InlineData("/build/", "/build/")]
    [InlineData(@"src\build\", "src/build/")]
    [InlineData("/**", "/**")]
    [InlineData("**/cache", "**/cache")]
    [InlineData(".github/**", ".github/**")]
    public void APatternsSpelling_ReadsBackAsTheSameMatcher(string pattern, string spelled)
    {
        var once = GlobPattern.Parse(pattern);
        var twice = GlobPattern.Parse(once.ToString());

        Assert.Equal(spelled, once.ToString());
        Assert.Equal(spelled, twice.ToString());

        foreach (var path in new[] { "docs", "docs/a.md", "en/docs", "en/docs/a.md", "build", "build/x", "src/build/x", ".github/ci.yml", "a/cache/x" })
        {
            Assert.Equal(once.Matches(path), twice.Matches(path));
        }
    }

    /// <summary>
    /// A misspelling is asked of the pattern as written, by the one spelling rule the configured
    /// lists are held to, so a pattern refused there is refused here: <c>././x</c> once passed, read
    /// past one <c>./</c> at a time. A <c>..</c> segment is refused in its own words, since no path a
    /// pattern is matched against holds one - by the rule that refuses a configured path outside the
    /// tree, while the configured lists' spelling rule stays as it was released; and the root alone,
    /// which no path in it is.
    /// </summary>
    [Fact]
    public void AMisspelledPattern_IsRefusedByTheOneSpellingRuleBothMatchersShare()
    {
        const string Dotted = "holds a '.' segment or a doubled separator, which the file system reads past and a comparison of paths never does; write 'x'";
        const string Climbing = "holds a '..' segment, which no path inside the tree holds, so it would name nothing; name the path from the tree's root";

        Assert.Equal(Dotted, GlobPattern.Problem("././x"));
        Assert.Equal(Dotted, PathPatterns.Problem("././x"));
        Assert.Equal(Climbing, GlobPattern.Problem("../x/**"));
        Assert.Equal(Climbing, GlobPattern.Problem("a/../b"));
        Assert.Null(PathPatterns.Misspelling("../x"));

        foreach (var root in new[] { ".", "./", "/." })
        {
            Assert.Equal("is '.', the root itself, which no path in it is; write '**' for everything beneath it", GlobPattern.Problem(root));
        }
    }

    /// <summary>
    /// A path is compared by the case rule of the tree it is in: folded, where the tree's file system
    /// folds case - <c>Docs/README.MD</c> is <c>docs/readme.md</c> there - and exact where it does
    /// not, with <c>?</c> and <c>*</c> matching as ever.
    /// </summary>
    [Fact]
    public void APathInATreeThatFoldsCase_IsMatchedFolded_AndOnlyThere()
    {
        var pattern = GlobPattern.Parse("docs/*.md");

        Assert.True(pattern.Matches("Docs/README.MD", StringComparison.OrdinalIgnoreCase));
        Assert.False(pattern.Matches("Docs/README.MD"));
        Assert.False(pattern.Matches("Docs/README.MD", StringComparison.Ordinal));
        Assert.True(GlobPattern.Parse("**/Cach?").Matches("src/CACHE/x", StringComparison.OrdinalIgnoreCase));
        Assert.False(GlobPattern.Parse("**/Cach?").Matches("src/CACHE/x"));

        var refused = Assert.Throws<ArgumentException>(() => pattern.Matches("docs/a.md", StringComparison.InvariantCultureIgnoreCase));

        Assert.Equal("comparison", refused.ParamName);
    }

    /// <summary>
    /// A path that is not spelled as the tree's owner spells one - the root alone, a <c>.</c> or
    /// <c>..</c> segment, a doubled separator - is the caller's mistake, refused rather than matched
    /// or missed: <c>docs/../secrets/key</c> once read as no match of <c>secrets/**</c>, which is the
    /// one path it names.
    /// </summary>
    [Theory]
    [InlineData("/")]
    [InlineData(".")]
    [InlineData("./")]
    [InlineData("docs/../secrets/key")]
    [InlineData("../secrets/key")]
    [InlineData("secrets//key")]
    [InlineData("secrets/./key")]
    public void APathNotSpelledAsTheTreeSpellsOne_IsRefused_NeverMatchedOrMissed(string path)
    {
        var refused = Assert.Throws<ArgumentException>(() => GlobPattern.Parse("secrets/**").Matches(path));

        Assert.Equal("relativePath", refused.ParamName);
        Assert.Equal(
            $"'{path}' is not a path inside the tree spelled as its owner spells one: it is the root alone, or holds a '.' or '..' "
                + "segment or a doubled separator (Parameter 'relativePath')",
            refused.Message);
    }
}
