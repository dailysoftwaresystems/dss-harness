using RepoHarness.Core.Mutations;

namespace RepoHarness.Tests;

/// <summary>
/// Whether a run said an arm's diagnostic, matched a line at a time as the output arrives: exactly the text, anywhere in
/// the output, its line endings read as one, however the output breaks into lines - and never across lines that do not
/// follow one another.
/// </summary>
public sealed class DiagWindowTests
{
    /// <summary>A one-line diagnostic is found inside a line, and not where only part of it is.</summary>
    [Fact]
    public void AOneLineDiagnostic_IsFoundInsideALine()
    {
        Assert.True(DiagWindow.Appears("Which is: 4", ["[ RUN      ] Fixture.Charge", "fixture.cpp:12: Failure", "    Which is: 4", "[  FAILED  ] Fixture.Charge"]));
        Assert.False(DiagWindow.Appears("Which is: 4", ["    Which is: 5", "Which is:", " 4"]));
    }

    /// <summary>
    /// A diagnostic of several lines is found beginning part way along one line and ending part way along another, its
    /// line endings and the output's read alike - a CRLF either side - and only once the line holding its end arrives.
    /// </summary>
    [Fact]
    public void ADiagnosticOfSeveralLines_IsFoundAcrossTheLinesItSpans()
    {
        var window = new DiagWindow("equality of these values:\r\n  charge(2)\r\n    Which");

        window.Feed("fixture.cpp:12: Failure\r");
        window.Feed("Expected equality of these values:\r");
        window.Feed("  charge(2)\r");

        Assert.False(window.Found);

        window.Feed("    Which is: 4\r");

        Assert.True(window.Found);
    }

    /// <summary>
    /// The window holds a diagnostic's lines and one more, so lines that do not follow one another never make one: the
    /// diagnostic's first line, other lines, then its last, is not it.
    /// </summary>
    [Fact]
    public void LinesThatDoNotFollowOneAnother_NeverMakeADiagnostic()
    {
        Assert.False(DiagWindow.Appears("first\nsecond", ["first", "between", "second"]));
        Assert.True(DiagWindow.Appears("first\nsecond", ["noise", "first", "second", "more"]));
        Assert.False(DiagWindow.Appears("a\nb\nc", ["a", "b", "x", "c"]));
    }

    /// <summary>Once found, it stays found, whatever follows.</summary>
    [Fact]
    public void OnceFound_ItStaysFound()
    {
        var window = new DiagWindow("boom");

        window.Feed("boom");
        window.Feed("something else");
        window.Feed("and more");

        Assert.True(window.Found);
    }
}
