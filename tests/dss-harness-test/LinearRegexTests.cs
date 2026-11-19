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
}
