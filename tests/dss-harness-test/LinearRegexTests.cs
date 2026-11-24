using System.Text.RegularExpressions;
using DssHarness.Core.Text;

namespace DssHarness.Tests;

/// <summary>The one maker of regular expressions: every one linear in the text it matches, and a pattern the linear engine cannot match refused, naming what it lacks.</summary>
public sealed class LinearRegexTests
{
    /// <summary>
    /// Every regular expression is made non-backtracking and culture-invariant beside the options asked, with no match
    /// timeout, by the maker for the code's own patterns and by the one for others' alike: a pattern that backtracks
    /// exponentially answers at once, so no timeout is needed to bound a match and none can cut one short.
    /// </summary>
    [Fact]
    public void EveryRegex_IsNonBacktrackingWithNoTimeout()
    {
        var compiled = LinearRegex.Compile("(a+)+b");

        Assert.True(LinearRegex.TryCompile("(a+)+b", RegexOptions.IgnoreCase, out var tried, out var refusal));
        Assert.Null(refusal);
        Assert.Equal(RegexOptions.NonBacktracking | RegexOptions.CultureInvariant, compiled.Options);
        Assert.Equal(RegexOptions.NonBacktracking | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, tried.Options);
        Assert.Equal(Regex.InfiniteMatchTimeout, compiled.MatchTimeout);
        Assert.Equal(Regex.InfiniteMatchTimeout, tried.MatchTimeout);

        // A backtracking matcher tries every way of sharing the run among the nested groups before it fails: 2^100000.
        var run = new string('a', 100_000);

        Assert.DoesNotMatch(compiled, run);
        Assert.DoesNotMatch(tried, run);
    }

    /// <summary>
    /// A backreference, numbered or named, is refused by a sentence that names the pattern and the construct; the maker for
    /// the code's own patterns throws that sentence as its message, since a refusal there is a bug.
    /// </summary>
    [Fact]
    public void ABackreference_IsRefusedNamingIt()
    {
        const string Refused = @"The pattern '(\w)\1' holds a backreference, which .NET's linear-time matcher lacks.";

        Assert.False(LinearRegex.TryCompile(@"(\w)\1", RegexOptions.None, out var regex, out var refusal));
        Assert.Null(regex);
        Assert.Equal(Refused, refusal);
        Assert.Equal(Refused, Assert.Throws<ArgumentException>(() => LinearRegex.Compile(@"(\w)\1")).Message);

        Assert.False(LinearRegex.TryCompile(@"(?<n>\w)\k<n>", RegexOptions.None, out _, out var named));
        Assert.Equal(@"The pattern '(?<n>\w)\k<n>' holds a backreference, which .NET's linear-time matcher lacks.", named);
    }

    /// <summary>
    /// Each other construct the linear engine lacks, and each option it cannot match by, is refused by a sentence naming
    /// it: a lookbehind as a lookahead is, both conditionals alike, and two options asked together both named.
    /// </summary>
    [Theory]
    [InlineData("a(?=b)", RegexOptions.None, "The pattern 'a(?=b)' holds a lookahead or lookbehind, which .NET's linear-time matcher lacks.")]
    [InlineData("a(?!b)", RegexOptions.None, "The pattern 'a(?!b)' holds a lookahead or lookbehind, which .NET's linear-time matcher lacks.")]
    [InlineData("(?<=a)b", RegexOptions.None, "The pattern '(?<=a)b' holds a lookahead or lookbehind, which .NET's linear-time matcher lacks.")]
    [InlineData("(?<!a)b", RegexOptions.None, "The pattern '(?<!a)b' holds a lookahead or lookbehind, which .NET's linear-time matcher lacks.")]
    [InlineData("(?>a+)b", RegexOptions.None, "The pattern '(?>a+)b' holds an atomic group, which .NET's linear-time matcher lacks.")]
    [InlineData("(?(a)b|c)", RegexOptions.None, "The pattern '(?(a)b|c)' holds a conditional, which .NET's linear-time matcher lacks.")]
    [InlineData("(a)?(?(1)b|c)", RegexOptions.None, "The pattern '(a)?(?(1)b|c)' holds a conditional, which .NET's linear-time matcher lacks.")]
    [InlineData(@"\Ga", RegexOptions.None, @"The pattern '\Ga' holds a \G anchor, which .NET's linear-time matcher lacks.")]
    [InlineData("(?<o>a)(?<c-o>b)", RegexOptions.None, "The pattern '(?<o>a)(?<c-o>b)' holds a balancing group, which .NET's linear-time matcher lacks.")]
    [InlineData("a", RegexOptions.RightToLeft, "The pattern 'a' is asked for RightToLeft, which .NET's linear-time matcher lacks.")]
    [InlineData("a", RegexOptions.ECMAScript, "The pattern 'a' is asked for ECMAScript, which .NET's linear-time matcher lacks.")]
    [InlineData("a", RegexOptions.RightToLeft | RegexOptions.ECMAScript, "The pattern 'a' is asked for RightToLeft and ECMAScript, which .NET's linear-time matcher lacks.")]
    public void EachOtherConstructTheLinearEngineLacks_IsRefusedNamingIt(string pattern, RegexOptions options, string refused)
    {
        Assert.False(LinearRegex.TryCompile(pattern, options, out var regex, out var refusal));
        Assert.Null(regex);
        Assert.Equal(refused, refusal);
    }

    /// <summary>
    /// An option the linear engine lacks is refused before the pattern is read, so it is named even of a pattern that is
    /// no regular expression: .NET's own answer to the option names only the parameter.
    /// </summary>
    [Fact]
    public void AnOptionTheLinearEngineLacks_IsRefusedBeforeThePatternIsRead()
    {
        Assert.False(LinearRegex.TryCompile("(", RegexOptions.RightToLeft, out var regex, out var refusal));
        Assert.Null(regex);
        Assert.Equal("The pattern '(' is asked for RightToLeft, which .NET's linear-time matcher lacks.", refusal);
    }

    /// <summary>A pattern that is no regular expression at all is refused in .NET's own words, which name it and where it breaks.</summary>
    [Fact]
    public void APatternThatIsNoRegularExpression_IsRefusedInDotNetsWords()
    {
        Assert.False(LinearRegex.TryCompile("(a", RegexOptions.None, out var regex, out var refusal));
        Assert.Null(regex);
        Assert.Equal("Invalid pattern '(a' at offset 2. Not enough )'s.", refusal);
    }

    /// <summary>
    /// A refusal of the linear engine that is no construct it lacks - an automaton larger than it builds - is told in .NET's
    /// own words after the pattern, so nothing the runtime said is lost.
    /// </summary>
    [Fact]
    public void APatternLargerThanTheLinearEngineBuilds_IsRefusedInDotNetsWords()
    {
        Assert.False(LinearRegex.TryCompile(".{10000}", RegexOptions.None, out var regex, out var refusal));
        Assert.Null(regex);
        Assert.Equal(
            "The pattern '.{10000}' is refused by .NET's linear-time matcher: The specified pattern with RegexOptions.NonBacktracking "
            + "could result in an automata as large as '10001' nodes, which is larger than the configured limit of '10000'.",
            refusal);
    }

    /// <summary>
    /// A pattern written in another dialect and handed over in .NET's writing is refused naming it as its reader wrote it -
    /// a construct the linear engine lacks, and a writing that is no regular expression .NET reads - never by .NET's writing
    /// or an offset into it; one that reads compiles as any does.
    /// </summary>
    [Fact]
    public void APatternWrittenInAnotherDialect_IsRefusedNamingItAsWritten()
    {
        Assert.False(LinearRegex.TryCompile(@"\A(?:(a)\1)\z", RegexOptions.None, @"(a)\1", out var regex, out var refusal));
        Assert.Null(regex);
        Assert.Equal(@"The pattern '(a)\1' holds a backreference, which .NET's linear-time matcher lacks.", refusal);

        Assert.False(LinearRegex.TryCompile(@"\A(?:(a)\z", RegexOptions.None, "(a", out _, out var unread));
        Assert.Equal("The pattern '(a' is no regular expression .NET reads.", unread);

        Assert.True(LinearRegex.TryCompile(@"\A(?:a)\z", RegexOptions.None, "a", out var read, out var none));
        Assert.Null(none);
        Assert.Matches(read, "a");
        Assert.DoesNotMatch(read, "ab");
    }

    /// <summary>An options value .NET does not define is the caller's mistake, not the pattern's: it is thrown as .NET throws it, never refused.</summary>
    [Fact]
    public void AnOptionsValueDotNetDoesNotDefine_IsThrownNotRefused()
    {
        var thrown = Assert.Throws<ArgumentOutOfRangeException>(() => LinearRegex.TryCompile("a", (RegexOptions)0x1000, out _, out _));

        Assert.Equal("options", thrown.ParamName);
    }

    /// <summary>
    /// A match sought from where a reader stands reads the text from there as the whole of it: the pattern's <c>\A</c>
    /// stands there - the <c>\G</c> the linear engine lacks - so an anchored pattern matches only there, an unanchored
    /// one is sought on from there, and nothing before it is read, so a word's boundary stands at its start.
    /// </summary>
    [Fact]
    public void AMatchFromAPlace_ReadsTheTextFromThereAsTheWholeOfIt()
    {
        var anchored = LinearRegex.Compile(@"\Ab");

        Assert.Equal((1, 1), Place(LinearRegex.MatchFrom(anchored, "ab", 1)));
        Assert.Equal((-1, -1), Place(LinearRegex.MatchFrom(anchored, "abab", 0)));
        Assert.Equal((3, 1), Place(LinearRegex.MatchFrom(LinearRegex.Compile("b"), "abab", 2)));
        Assert.Equal((1, 1), Place(LinearRegex.MatchFrom(LinearRegex.Compile(@"\bb"), "ab", 1)));

        static (int Index, int Length) Place(Match match) => match.Success ? (match.Index, match.Length) : (-1, -1);
    }

    /// <summary>
    /// The fact the maker's end anchor stands on, of the runtime the suite runs on: .NET's linear engine, given a match a
    /// <c>\z</c> of the pattern could end at the end of a text whose last character is a line feed, finds the match but
    /// reports its groups unmatched - where the backtracking engine reports them - and keeps them where the end is written
    /// <c>(?:\Z\z)</c>. The day a runtime keeps them at a lone <c>\z</c>, this goes red and the maker's rewrite can go.
    /// </summary>
    [Theory]
    [InlineData(@"(?<g>a)\s*\z")]
    [InlineData(@"(?<g>a)(?:[^b]|\z)")]
    [InlineData(@"(?<g>a)\n\z")]
    public void DotNetsLinearEngine_LosesTheGroupsOfAMatchAnEndAnchorEndsAfterAFinalLineFeed(string pattern)
    {
        var linear = new Regex(pattern, RegexOptions.NonBacktracking).Match("a\n");
        var backtracking = new Regex(pattern).Match("a\n");
        var kept = new Regex(pattern.Replace(@"\z", @"(?:\Z\z)", StringComparison.Ordinal), RegexOptions.NonBacktracking).Match("a\n");

        Assert.Equal((0, 2), (linear.Index, linear.Length));
        Assert.False(linear.Groups["g"].Success, "the linear engine keeps a group a \\z ends after a final line feed now: LinearRegex's rewrite of \\z can go");
        Assert.Equal((true, 0, 1), (backtracking.Groups["g"].Success, backtracking.Groups["g"].Index, backtracking.Groups["g"].Length));
        Assert.Equal((true, 0, 1), (kept.Groups["g"].Success, kept.Groups["g"].Index, kept.Groups["g"].Length));
    }

    /// <summary>
    /// A pattern with groups keeps every group of a match its <c>\z</c> ends after a final line feed: each <c>\z</c> the
    /// pattern holds as an anchor is found as .NET reads the pattern - never one a backslash escapes, one in a class or a
    /// <c>(?#...)</c> comment, nor a <c>[</c> in a comment of a pattern that ignores whitespace mistaken for a class, nor a
    /// <c>#</c> after the group such a setting ended mistaken for a comment.
    /// </summary>
    [Theory]
    [InlineData(@"(?<g>a)\s*\z", RegexOptions.None, "a\n", 2)]
    [InlineData(@"(?<g>a)(?:[^b]|\z)", RegexOptions.None, "a\n", 2)]
    [InlineData(@"(?<g>a)\n\z", RegexOptions.None, "a\n", 2)]
    [InlineData(@"(?<g>a)\n?\z", RegexOptions.Multiline, "a\n", 2)]
    [InlineData(@"(?<g>a)\\z", RegexOptions.None, @"a\z", 3)]
    [InlineData(@"(?<g>[(?#])\n?\z", RegexOptions.None, "(\n", 2)]
    [InlineData(@"(?#\z)(?<g>a)\n?\z", RegexOptions.None, "a\n", 2)]
    [InlineData("(?<g>a)\\n? # [ \n\\z", RegexOptions.IgnorePatternWhitespace, "a\n", 2)]
    [InlineData("(?x)(?<g>a)\\n? # [ \n\\z", RegexOptions.None, "a\n", 2)]
    [InlineData(@"(?<g>a)(?x: \n? )#[\n]?\z", RegexOptions.None, "a\n#\n", 4)]
    public void AnEndAnchor_KeepsEveryGroupOfTheMatchItEnds(string pattern, RegexOptions options, string text, int length)
    {
        var match = LinearRegex.Compile(pattern, options).Match(text);

        Assert.Equal((0, length), (match.Index, match.Length));
        Assert.Equal((true, 0, 1), (match.Groups["g"].Success, match.Groups["g"].Index, match.Groups["g"].Length));
    }

    /// <summary>A pattern with groups and a <c>\z</c> that the engine refuses is refused as written, never as the end anchor is rewritten.</summary>
    [Fact]
    public void APatternWithAnEndAnchorTheEngineRefuses_IsRefusedAsWritten()
    {
        Assert.False(LinearRegex.TryCompile(@"(?<g>a)\z(?<=a)", RegexOptions.None, out _, out var refusal));
        Assert.Equal(@"The pattern '(?<g>a)\z(?<=a)' holds a lookahead or lookbehind, which .NET's linear-time matcher lacks.", refusal);
    }
}
