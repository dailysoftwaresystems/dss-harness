using DssHarness.Core.Codebase;
using DssHarness.Core.Codebase.Languages;

namespace DssHarness.Tests;

/// <summary>How every implementation fits a node's text into the model's bounds, and tells two ids alike apart.</summary>
public sealed class ExtractTextTests
{
    /// <summary>A summary is the first sentence: up to a stop a space or the end follows, a stop inside a word no end.</summary>
    /// <param name="text">The line.</param>
    /// <param name="expected">Its first sentence.</param>
    [Theory]
    [InlineData("One. Two.", "One.")]
    [InlineData("Ends with no stop", "Ends with no stop")]
    [InlineData("Version 1.2 is out. Next.", "Version 1.2 is out.")]
    [InlineData("Why? Because.", "Why?")]
    [InlineData("Wow! Really.", "Wow!")]
    [InlineData("e.g.this stays. Then.", "e.g.this stays.")]
    public void ASummary_IsTheFirstSentence_UpToAStopASpaceOrTheEndFollows(string text, string expected)
        => Assert.Equal(expected, ExtractText.Summary(text));

    /// <summary>
    /// A text is put on one line, and cut to its bound at a word before it with an ellipsis - at the bound where no word
    /// ends late enough, and never inside a surrogate pair.
    /// </summary>
    [Fact]
    public void AText_IsCutToItsBound_AtAWord_WithAnEllipsis_NeverInsideASurrogatePair()
    {
        Assert.Equal("a b c", ExtractText.Flat("  a\r\n\tb \n c  "));

        // Seven characters a word with its space, so the bound falls inside one.
        Assert.InRange((FileExtract.MostSignature - 1) % 7, 1, 5);
        var words = ExtractText.Signature(string.Join(' ', Enumerable.Repeat("sevens", FileExtract.MostSignature)));
        Assert.True(words.Length <= FileExtract.MostSignature);
        Assert.EndsWith(" sevens…", words, StringComparison.Ordinal);

        var letters = new string('a', FileExtract.MostSignature - 2);
        Assert.Equal(letters + "…", ExtractText.Signature(letters + "😀😀"));
        Assert.Equal(letters, ExtractText.Signature(letters));
    }

    /// <summary>An id already given is told apart by its place among those alike - the next one no declaration has.</summary>
    [Fact]
    public void AnIdAlreadyGiven_IsToldApartByItsPlace_TheNextOneNoDeclarationHas()
    {
        var ids = new ExtractIds();

        Assert.Equal(["a", "b~2", "a~2", "b", "b~3", "a~3"], new[] { "a", "b~2", "a", "b", "b", "a" }.Select(ids.Next));
    }
}
