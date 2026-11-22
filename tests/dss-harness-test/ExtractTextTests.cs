using DssHarness.Core.Codebase;
using DssHarness.Core.Codebase.Languages;

namespace DssHarness.Tests;

/// <summary>How every implementation gives a node its text - whole, on one line - and tells two ids alike apart.</summary>
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

    /// <summary>A text is put on one line: each run of white space, line breaks among it, one space, none at either end.</summary>
    [Fact]
    public void AText_IsPutOnOneLine()
        => Assert.Equal(("a b c", "one", string.Empty), (ExtractText.Flat("  a\r\n\tb \n c  "), ExtractText.Flat("one"), ExtractText.Flat(" \t\n ")));

    /// <summary>
    /// A signature is the header whole however long it runs - a generated template's twelve thousand characters among
    /// them - and the reader that names it keeps it whole in an extract the core takes: what serves it gives one wider
    /// than a page in pieces, so no cut is needed and none is made.
    /// </summary>
    [Fact]
    public void ATwelveThousandCharacterSignature_IsKeptWhole()
    {
        var header = $"template <{string.Join(", ", Enumerable.Range(0, 1_200).Select(index => $"class T{index:D4}"))}> struct Wide";

        Assert.InRange(header.Length, 12_000, 20_000);
        Assert.Equal(header, ExtractText.Signature(header));

        var name = new string('k', 12_000);
        var text = $"{{ \"{name}\": 1 }}";
        var extract = new JsonExtractor().Extract(new CodeFileInput("wide.json", text));

        Assert.Equal([name], extract.Nodes.Select(node => node.Signature));
        Assert.Null(extract.WithDigests(text).Problem("json", 1));
    }

    /// <summary>
    /// A summary is the whole first sentence what the code says of itself writes, however many lines it runs over - a
    /// generated document's sentence of forty lines among them - since the sentence's own end is what bounds it.
    /// </summary>
    [Fact]
    public void AFortyLineFirstSentence_IsTheSummary()
    {
        var lines = Enumerable.Range(0, 40).Select(index => $"clause {index:D2} of the one sentence the comment writes,").ToList();
        var sentence = string.Join(' ', lines) + " ending here.";
        var text = string.Concat(lines.Select(line => $"// {line}\n")) + "// ending here. A second sentence.\nint answer(void);\n";
        var extract = new CExtractor().Extract(new CodeFileInput("answer.c", text));

        Assert.True(sentence.Length > 1_000, $"the sentence is {sentence.Length} characters");
        Assert.Equal(sentence, ExtractText.Summary(sentence + " A second sentence."));
        Assert.Equal([sentence], extract.Nodes.Select(node => node.Summary));
        Assert.Null(extract.WithDigests(text).Problem("c", CodeFileText.Lines(text).Count));
    }

    /// <summary>A text cut inside a character is made whole: each surrogate apart from its pair the replacement character, a pair kept, a whole text the very one.</summary>
    [Fact]
    public void ATextCutInsideACharacter_IsMadeWhole()
    {
        var whole = "a😀b";

        Assert.Same(whole, ExtractText.Whole(whole));
        Assert.Equal("a\uFFFDb\uFFFD😀\uFFFD", ExtractText.Whole("a" + whole[1] + "b" + whole[2] + whole[1] + whole[2] + whole[1]));
        Assert.True(FileExtract.IsWhole(ExtractText.Whole(whole[2] + "x")));
    }

    /// <summary>A name is joined to the scope it is declared in by the model's separator, and stands alone at the top.</summary>
    [Fact]
    public void AName_IsJoinedToItsScopeByADot()
        => Assert.Equal(("a.b.c", "c"), (ExtractText.Qualify("a.b", "c"), ExtractText.Qualify(null, "c")));

    /// <summary>An id already given is told apart by its place among those alike - the next one no declaration has.</summary>
    [Fact]
    public void AnIdAlreadyGiven_IsToldApartByItsPlace_TheNextOneNoDeclarationHas()
    {
        var ids = new ExtractIds();

        Assert.Equal(["a", "b~2", "a~2", "b", "b~3", "a~3"], new[] { "a", "b~2", "a", "b", "b", "a" }.Select(name => ids.Next(QualifiedName.Of(name))));
    }
}
