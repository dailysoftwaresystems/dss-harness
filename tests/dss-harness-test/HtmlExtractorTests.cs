using DssHarness.Core.Codebase;
using DssHarness.Core.Codebase.Languages;

namespace DssHarness.Tests;

/// <summary>An HTML document, held to the contract's kit, and the rules of its own the kit does not name.</summary>
public sealed class HtmlExtractorTests : CodeGraphExtractorContractTests<HtmlExtractor>
{
    private const string Page = """
        <!DOCTYPE html>
        <html lang="en">
        <head>
          <title>Billing</title>
          <link rel="stylesheet" href="./site.css">
          <script src="https://cdn.example.com/lib.js"></script>
        </head>
        <body>
          <main id="content">
            <h1 id="title" class="dark">Billing</h1>
            <p class="lead muted">Welcome.</p>
            <checkout-widget data-kind="cart" onclick="open()" onmouseover="go"></checkout-widget>
          </main>
          <a href="/abs.html">Elsewhere</a>
          <a href="rel.html?x=1">Here</a>
          <script>
            function open() {
              return "done";
            }
          </script>
        </body>
        </html>
        """;

    /// <inheritdoc/>
    public override IReadOnlyList<CodeGraphFixture> Fixtures { get; } =
    [
        new("index.html", Page)
        {
            Nodes =
            [
                new("html:index.html#content", CodeNodeKinds.Section),
                new("html:index.html#title", CodeNodeKinds.Section),
                new("html:open", CodeNodeKinds.Member, (17, 19)),
            ],
        },
        new("rel.html", "<html><body><p>Plain.</p></body></html>"),
        new("plain.htm", "<p>Nothing of its own.</p>"),
    ];

    /// <summary>
    /// Each landmark and each element with an id is a section over its lines, the body one over all; each class a use of
    /// the rule <c>.name</c> and each id a use of <c>#id</c>; a custom element is an instantiates of a component of its
    /// name; an <c>on*=</c> handler a call of the function it names; and an inline script is read by the JavaScript
    /// reader, its declarations carrying the lines they stand on.
    /// </summary>
    [Fact]
    public void LandmarksAndIdElementsAreSections_ClassesAndIdsAreUsesOfRules_ScriptsCarryTheirLines()
    {
        var extract = Read("index.html", Page);

        Assert.Equal(["body", "content", "title"], extract.Nodes.Where(node => node.Kind == CodeNodeKinds.Section).OrderBy(node => node.Start).Select(node => node.Name));
        Assert.Equal("landmark", extract.Nodes.Single(node => node.Name == "content").SubKind);

        var uses = extract.References.Where(reference => reference.Kind == CodeEdgeKinds.Uses).Select(reference => reference.Name).Order(StringComparer.Ordinal).ToList();

        Assert.Equal(["#content", "#title", ".dark", ".lead", ".muted"], uses);
        Assert.All(extract.References.Where(reference => reference.Name[0] is '.' or '#'), reference => Assert.Equal(CodeQualifierKinds.Selector, reference.Qualifier?.Kind));

        Assert.Equal("checkout-widget", Assert.Single(extract.References, reference => reference.Kind == CodeEdgeKinds.Instantiates).Name);
        Assert.Equal(["go", "open"], extract.References.Where(reference => reference.Kind == CodeEdgeKinds.Calls).Select(reference => reference.Name).Order(StringComparer.Ordinal));

        var script = Assert.Single(extract.Nodes, node => node.Id == "html:open");

        Assert.Equal(17, script.Start);
        Assert.Contains(new ExtractLiteral("done", 18), extract.Literals ?? []);
        Assert.Equal([("site.css", 5), ("rel.html", 15)], extract.Imports.Select(import => (import.Target, import.Line)));
    }

    /// <summary>An href that names no file of the tree - another site's, an absolute path - is no import of it.</summary>
    [Fact]
    public void AnHrefNamingNoFileOfTheTreeIsNoImport()
    {
        var extract = Read("index.html", Page);

        Assert.DoesNotContain(extract.Imports, import => import.Target.Contains("cdn.example.com", StringComparison.Ordinal) || import.Target.Contains("/abs.html", StringComparison.Ordinal));
    }

    private static FileExtract Read(string path, string text) => new HtmlExtractor().Extract(new CodeFileInput(path, text));
}
