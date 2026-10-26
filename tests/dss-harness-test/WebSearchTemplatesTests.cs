using System.Text.Json;
using DssHarness.Core.Web.Search;

namespace DssHarness.Tests;

/// <summary>The templates a source writes: a request's, filled for each query, and a result's field, read from an item.</summary>
public sealed class WebSearchTemplatesTests
{
    /// <summary>A request's template fills its names - the query escaped in an address, as it is in a text - and keeps a doubled brace as one.</summary>
    [Fact]
    public void ARequestsTemplate_FillsItsNames()
    {
        var template = WebRequestTemplate.Parse("{base}/s?q={query}&n={limit}&f={{x}}&again={query}");
        var found = new Dictionary<string, string> { ["base"] = "https://a.test/api" };

        Assert.Equal(["base", "query", "limit"], template.Names);
        Assert.Equal("https://a.test/api/s?q=a%2Bb%20c&n=7&f={x}&again=a%2Bb%20c", template.Address("a+b c", 7, found));
        Assert.Equal("https://a.test/api/s?q=a+b c&n=7&f={x}&again=a+b c", template.Plain("a+b c", 7, found));
        Assert.True(WebRequestTemplate.Parse("{limit}").IsLimitAlone);
        Assert.False(WebRequestTemplate.Parse(" {limit}").IsLimitAlone);
    }

    /// <summary>A request's template that cannot be read says why.</summary>
    [Theory]
    [InlineData("https://a.test/?q={query", "is no template: the placeholder at column 19 of line 1 is never closed")]
    [InlineData("https://a.test/?q={query{limit}}", "is no template: the placeholder at column 19 of line 1 is never closed")]
    [InlineData("https://a.test/?q={}", "is no template: the placeholder at column 19 of line 1 names nothing (a brace meant literally is written twice)")]
    [InlineData("https://a.test/?q={9query}", "is no template: '{9query}' names no value: a name starts with a letter or '_' and holds only letters, digits, '_', '-' and '.'")]
    [InlineData("https://a.test/?q={query|x}", "is no template: '{query|x}' names no value")]
    [InlineData("https://a.test/?q={query}&x={found}", "names '{found}', which nothing fills: {query}, {limit}, {other}")]
    public void ARequestsTemplateThatCannotBeRead_SaysWhy(string text, string problem)
        => Assert.StartsWith(problem, WebRequestTemplate.Problem(text, ["other"], needsQuery: true), StringComparison.Ordinal);

    /// <summary>
    /// A request's template is read by the configuration's one brace grammar: a brace standing alone is itself, a doubled
    /// one is one, another expander's group is left as written, a name may hold '.' and begin with '_'.
    /// </summary>
    [Fact]
    public void ARequestsTemplate_IsReadByTheOneGrammar()
    {
        var template = WebRequestTemplate.Parse("https://a.test/?q={query}&t=${TOKEN}&b=}&c={{x}}&d={_found.id}");

        Assert.Equal(["query", "_found.id"], template.Names);
        Assert.Equal("https://a.test/?q=a%20b&t=${TOKEN}&b=}&c={x}&d=7", template.Address("a b", 5, new Dictionary<string, string> { ["_found.id"] = "7" }));
    }

    /// <summary>
    /// A request's template writes its scheme, host and port whole - as an address's authority reads, however it spells
    /// them - where no name it fills stands before its path, query or fragment; one a name fills, a lookup's host, is none.
    /// </summary>
    [Theory]
    [InlineData("https://api.example.test/search?q={query}", "https://api.example.test")]
    [InlineData("HTTPS://API.example.test:443/search?q={query}", "https://api.example.test")]
    [InlineData("https://api.example.test:8443/search?q={query}", "https://api.example.test:8443")]
    [InlineData("https://api.example.test?q={query}", "https://api.example.test")]
    [InlineData("https://api.example.test#{query}", "https://api.example.test")]
    [InlineData("https://api.example.test", "https://api.example.test")]
    [InlineData("{search}?q={query}", null)]
    [InlineData("https://{tenant}.example.test/search?q={query}", null)]
    [InlineData("https://api.example.test{path}?q={query}", null)]
    [InlineData("https://api.example.test:{port}/search?q={query}", null)]
    [InlineData("api.example.test/search?q={query}", null)]
    public void ARequestsTemplate_WritesItsOriginWhole(string text, string? origin)
        => Assert.Equal(origin, WebRequestTemplate.Parse(text).Origin());

    /// <summary>A field's template reads each path of the item or of the answer, joins several values with commas, and keeps a brace a quoted name holds.</summary>
    [Fact]
    public void AFieldsTemplate_ReadsItsPaths()
    {
        using var document = JsonDocument.Parse("""{ "base": "https://a.test", "items": [ { "path": "/x", "tags": ["c#", 5, true, null, {}], "a}b": "brace", "a{b": "open", "it's}": "quote", "n": null } ] }""");
        var item = document.RootElement.GetProperty("items")[0];

        Assert.Equal("https://a.test/x", WebItemTemplate.Parse("{$.base}{@.path}").Fill(item, document.RootElement));
        Assert.Equal("tags: c#, 5, true", WebItemTemplate.Parse("tags: {@.tags[*]}").Fill(item, document.RootElement));
        Assert.Equal("{brace}", WebItemTemplate.Parse("{{{@['a}b']}}}").Fill(item, document.RootElement));
        Assert.Equal("open", WebItemTemplate.Parse("{@['a{b']}").Fill(item, document.RootElement));
        Assert.Equal("quote", WebItemTemplate.Parse("{@['it\\'s}']}").Fill(item, document.RootElement));
        Assert.Equal("is no template: the placeholder at column 1 of line 1 is never closed", WebItemTemplate.Problem("{@['a\n']}"));
        Assert.Null(WebItemTemplate.Parse("{@.n}").Fill(item, document.RootElement));
        Assert.Null(WebItemTemplate.Parse("x {@.missing}").Fill(item, document.RootElement));

        // Another expander's group is text, as everywhere the configuration's templates are read.
        Assert.Equal("${@.path} /x", WebItemTemplate.Parse("${@.path} {@.path}").Fill(item, document.RootElement));
        Assert.Equal("is no template: it reads no value of the item, and a template reads at least one, as {@.title}", WebItemTemplate.Problem("${@.path}"));
        Assert.Equal("is no template: the placeholder at column 1 of line 1 is never closed", WebItemTemplate.Problem("{@.path{@.x}"));
    }

    /// <summary>A snippet is cut at a blank near its end, never inside a pair of surrogates, and marked; a short one is kept whole.</summary>
    [Fact]
    public void ASnippet_IsCutAtABlank()
    {
        Assert.Equal("short", DssHarness.Core.Output.TextCut.AtWord("short", WebSearchResult.MostSnippet));

        var unbroken = new string('x', 298) + "\U0001F600" + "tail";
        Assert.Equal(new string('x', 298) + "…", DssHarness.Core.Output.TextCut.AtWord(unbroken, WebSearchResult.MostSnippet));

        var words = string.Concat(Enumerable.Repeat("abcd ", 100));
        var cut = DssHarness.Core.Output.TextCut.AtWord(words, WebSearchResult.MostSnippet);
        Assert.EndsWith("abcd…", cut, StringComparison.Ordinal);
        Assert.True(cut.Length <= WebSearchResult.MostSnippet);

        // A blank early in the text is no place to cut: the word is cut instead, most of the text kept.
        var early = DssHarness.Core.Output.TextCut.AtWord("a " + new string('x', 400), WebSearchResult.MostSnippet);
        Assert.Equal(("a xxx", WebSearchResult.MostSnippet), (early[..5], early.Length));
    }

    /// <summary>A result's title and snippet are text with their blanks made single.</summary>
    [Fact]
    public void AResultsFields_HaveTheirBlanksMadeSingle()
        => Assert.Equal(new WebSearchResult("a b", "https://a.test/", "c d e"), WebSearchResult.Of(" a \n b ", "https://a.test/", "  c   d\n e ", null));

    /// <summary>A result with no title, or with no address that is the web's, is none.</summary>
    [Theory]
    [InlineData("", "https://a.test/", null)]
    [InlineData(" \n ", "https://a.test/", null)]
    [InlineData("T", "javascript:alert(1)", null)]
    [InlineData("T", "/relative", "https://base.test/relative")]
    [InlineData("T", "//cdn.test/x", "https://cdn.test/x")]
    public void AResult_NeedsATitleAndAnAddress(string title, string link, string? address)
        => Assert.Equal(address, WebSearchResult.Of(title, link, null, "https://base.test/search")?.Link);
}
