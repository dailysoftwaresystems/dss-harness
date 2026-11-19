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

    private const string Nested = """
        <!DOCTYPE html>
        <html class="dark">
        <head>
          <link rel="stylesheet" href="../css/site.css">
          <style>
            .card { color: red; }
          </style>
        </head>
        <body id="page-top">
          <section>
            Text alone,
            over lines.
          </section>
          <template id="row"><my-row class="x"></my-row></template>
          <!-- <old-widget class="gone"></old-widget> -->
          <div class="a	b
        c"></div>
          <ul>
            <li id="first">one
            <li id="second">two
          </ul>
          <script
            type="module">
          export function shown() {
            return handlers[name]();
          }
          </script>
          <script>var left = foo</script><script>bar()</script>
          <script type="text/x-template"><div>{{ hidden() }}</div></script>
          <script src="../js/app.js"></script>
        </body>
        </html>
        """;

    private const string TwoScripts = """
        <html>
        <body>
        <script>
        window.config = { apiUrl: "https://a.example" };
        </script>
        <main id="app">
        <p>x</p>
        </main>
        <script>
        document.title = "Shop home";
        function init() { return "first one"; }
        </script>
        <script>
        function init() { return "second one"; }
        </script>
        </body>
        </html>
        """;

    /// <inheritdoc/>
    public override IReadOnlyList<CodeGraphFixture> Fixtures { get; } =
    [
        new("site/two.html", TwoScripts)
        {
            Nodes =
            [
                new("html:site/two.html#body", CodeNodeKinds.Section, (2, 16)),
                new("html:site/two.html.(module)", CodeNodeKinds.Member, (4, 4)),
                new("html:site/two.html.(module)~2", CodeNodeKinds.Member, (10, 10)),
                new("html:init", CodeNodeKinds.Member, (11, 11)),
                new("html:init~2", CodeNodeKinds.Member, (14, 14)),
            ],
        },
        new("index.html", Page)
        {
            Nodes =
            [
                new("html:index.html#body", CodeNodeKinds.Section, (8, 21)),
                new("html:index.html#content", CodeNodeKinds.Section, (9, 13)),
                new("html:index.html#title", CodeNodeKinds.Section, (10, 10)),
                new("html:open", CodeNodeKinds.Member, (17, 19)),
            ],
        },
        new("pages/nested.html", Nested)
        {
            Nodes =
            [
                new("html:pages/nested.html#page-top", CodeNodeKinds.Section, (9, 31)),
                new("html:pages/nested.html#section", CodeNodeKinds.Section, (10, 13)),
                new("html:pages/nested.html#row", CodeNodeKinds.Section, (14, 14)),
                new("html:pages/nested.html#first", CodeNodeKinds.Section, (19, 19)),
                new("html:pages/nested.html#second", CodeNodeKinds.Section, (20, 20)),
                new("html:pages/nested.html.shown", CodeNodeKinds.Member, (24, 26)),
                new("html:pages/nested.html#.card", CodeNodeKinds.Member, (6, 6)),
            ],
        },
        new("rel.html", "<html><body><p>Plain.</p></body></html>"),
        new("plain.htm", "<p>Nothing of its own.</p>"),
    ];

    /// <summary>
    /// Each landmark and each element with an id is a section from its start tag to its end tag, the body one over all;
    /// each class a use of the rule <c>.name</c> and each id a use of <c>#id</c>; a custom element is an instantiates of a
    /// component of its name; an <c>on*=</c> handler the body of the function HTML makes of it - <c>open()</c> a call,
    /// <c>go</c> alone a use, as HTML runs it; and an inline script is read by the JavaScript reader, its declarations
    /// carrying the lines they stand on.
    /// </summary>
    [Fact]
    public void LandmarksAndIdElementsAreSections_ClassesAndIdsAreUsesOfRules_ScriptsCarryTheirLines()
    {
        var extract = Read("index.html", Page);

        Assert.Equal([("body", 8, 21), ("content", 9, 13), ("title", 10, 10)], extract.Nodes.Where(node => node.Kind == CodeNodeKinds.Section).OrderBy(node => node.Start).Select(node => (node.Name, node.Start, node.End)));
        Assert.Equal("landmark", extract.Nodes.Single(node => node.Name == "content").SubKind);

        var uses = extract.References.Where(reference => reference.Kind == CodeEdgeKinds.Uses).Select(reference => reference.Name).Order(StringComparer.Ordinal).ToList();

        Assert.Equal(["#content", "#title", ".dark", ".lead", ".muted", "go"], uses);
        Assert.All(extract.References.Where(reference => reference.Name[0] is '.' or '#'), reference => Assert.Equal(CodeQualifierKinds.Selector, reference.Qualifier?.Kind));

        Assert.Equal("checkout-widget", Assert.Single(extract.References, reference => reference.Kind == CodeEdgeKinds.Instantiates).Name);
        Assert.Equal(["open"], extract.References.Where(reference => reference.Kind == CodeEdgeKinds.Calls).Select(reference => reference.Name));

        var script = Assert.Single(extract.Nodes, node => node.Id == "html:open");

        Assert.Equal(17, script.Start);
        Assert.Contains(new LiteralRow("done", 18, "html:open"), LiteralRow.Of(extract));
        Assert.Equal([("site.css", 5), ("rel.html", 15)], extract.Imports.Select(import => (import.Target, import.Line)));
    }

    /// <summary>An href that names no file of the tree - another site's, an absolute path - is no import of it.</summary>
    [Fact]
    public void AnHrefNamingNoFileOfTheTreeIsNoImport()
    {
        var extract = Read("index.html", Page);

        Assert.DoesNotContain(extract.Imports, import => import.Target.Contains("cdn.example.com", StringComparison.Ordinal) || import.Target.Contains("/abs.html", StringComparison.Ordinal));
    }

    /// <summary>
    /// A section ends at its end tag, not at the line its last child begins on: its text and its close are its own, and
    /// one that holds only text spans its lines; an item HTML closes at the next item ends where its own text does.
    /// </summary>
    [Fact]
    public void ASectionEndsAtItsEndTag_AndAnItemLeftOpenWhereHtmlClosesIt()
    {
        var extract = Read("pages/nested.html", Nested);

        Assert.Equal(
            [("page-top", 9, 31), ("section", 10, 13), ("row", 14, 14), ("first", 19, 19), ("second", 20, 20)],
            extract.Nodes.Where(node => node.Kind == CodeNodeKinds.Section).Select(node => (node.Name, node.Start, node.End)));
        Assert.Equal(
            ["html:pages/nested.html#page-top", "html:pages/nested.html#page-top", "html:pages/nested.html#page-top", "html:pages/nested.html#page-top"],
            extract.Nodes.Where(node => node.Kind == CodeNodeKinds.Section && node.SubKind != "body").Select(node => node.Parent));
    }

    /// <summary>
    /// Every element HTML closes where its end tag is left out ends where HTML closes it, at the last line its own content
    /// is written on: a paragraph at the next block, a row at the next row, a cell at the next cell, a table's head at
    /// its body, a term at its definition, an option at the next option, a group of options at the next group, a heading
    /// at the next heading; so each holds no section after it.
    /// </summary>
    [Fact]
    public void AnElementLeftOpen_EndsWhereHtmlClosesIt()
    {
        var extract = Read("left.html", """
            <body>
            <p id="one">first
            <div id="block">x</div>
            <table>
            <tr id="r1"><td id="c1">a<td id="c2">b
            <tr id="r2"><td>c
            </table>
            <dl><dt id="t">term<dd id="d">def</dl>
            <select><option id="o1">a<option id="o2">b</select>
            <select><optgroup id="g1"><option>a<optgroup id="g2"><option>b</select>
            <table><thead id="th1"><tr><td>a<tbody id="tb1"><tr><td>b</table>
            <h1 id="h">Title
            <h2 id="h2">Sub</h2>
            </body>
            """);
        var names = extract.Nodes.ToDictionary(node => node.Id, node => node.Name);

        Assert.Equal(
            [
                ("body", 1, 14, null), ("one", 2, 2, "body"), ("block", 3, 3, "body"), ("r1", 5, 5, "body"), ("c1", 5, 5, "r1"), ("c2", 5, 5, "r1"),
                ("r2", 6, 6, "body"), ("t", 8, 8, "body"), ("d", 8, 8, "body"), ("o1", 9, 9, "body"), ("o2", 9, 9, "body"), ("g1", 10, 10, "body"),
                ("g2", 10, 10, "body"), ("th1", 11, 11, "body"), ("tb1", 11, 11, "body"), ("h", 12, 12, "body"), ("h2", 13, 13, "body"),
            ],
            extract.Nodes.Select(node => (node.Name, node.Start, node.End, node.Parent is { } parent ? names[parent] : null)));
    }

    /// <summary>
    /// A page's head is left at the first element it does not hold - the body's start tag among them - and a second start
    /// tag of the body opens nothing: the body is the root's, and one.
    /// </summary>
    [Fact]
    public void AHeadLeftOpen_EndsAtTheBody_AndASecondBodyOpensNothing()
    {
        var markup = MarkupText.Read("<html><head><title>t</title>\n<body><p>x</p>\n<body class=\"late\"></body></html>", closesAsHtml: true);

        Assert.Equal([("html", -1), ("head", 0), ("title", 1), ("body", 0), ("p", 3)], markup.Elements.Select(element => (element.Name, element.Parent)));
    }

    /// <summary>
    /// The body's node is named as every section is - by its id, where it has one - and what stands outside every section,
    /// a class on the root element among it, is used from that node: one id, decided once, so no reference hangs from a
    /// node the extract does not hold.
    /// </summary>
    [Fact]
    public void ABodyWithAnId_IsTheNodeWhatNoSectionHoldsIsUsedFrom()
    {
        var extract = Read("pages/nested.html", Nested);

        var lines = new CodeFileInput("pages/nested.html", Nested).Lines;
        var body = Assert.Single(extract.Nodes, node => node.SubKind == "body");
        var dark = Assert.Single(extract.References, reference => reference.Name == ".dark");

        Assert.Null(extract.WithDigests(lines).Problem("html", lines.Count));
        Assert.Equal(("html:pages/nested.html#page-top", "page-top"), (body.Id, body.Name));
        Assert.Equal(("html:pages/nested.html#page-top", 2), (dark.From, dark.Line));
    }

    /// <summary>
    /// A template's content is read as the page's own markup - the components it makes and the classes it writes - and
    /// what a comment holds is not; a class list splits at any ASCII white space.
    /// </summary>
    [Fact]
    public void ATemplatesContentIsRead_ACommentsIsNot_AndClassesSplitAtWhiteSpace()
    {
        var extract = Read("pages/nested.html", Nested);

        Assert.Equal([("my-row", "html:pages/nested.html#row", 14)], extract.References.Where(reference => reference.Kind == CodeEdgeKinds.Instantiates).Select(reference => (reference.Name, reference.From, reference.Line)));
        Assert.Equal(
            [".a", ".b", ".c", ".dark", ".x"],
            extract.References.Where(reference => reference.Name.StartsWith('.')).Select(reference => reference.Name).Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// An href or src naming a file by a path that climbs out of the page's directory is an import of it, which the
    /// linker finds beside the page; one naming another site - a scheme, or <c>//</c> - or a rooted path is none.
    /// </summary>
    [Fact]
    public void APathThatClimbs_IsAnImport_AndAnotherSitesIsNone()
    {
        var extract = Read("pages/a.html", """
            <link href="../css/site.css"><script src="../js/app.js"></script>
            <a href="//cdn.example.com/x.html">x</a><a href="/rooted.html">r</a><a href="mailto:me@example.com">m</a>
            <a href="#top">t</a><img src="data:image/png;base64,AA=="><a href="my%20page.html">p</a><a href="..">up</a>
            """);

        Assert.Equal([("../css/site.css", 1), ("../js/app.js", 1), ("my page.html", 3)], extract.Imports.Select(import => (import.Target, import.Line)));

        var linked = CodeGraphLinker.Link(
        [
            new CodeGraphFile("pages/a.html", "html", extract),
            new CodeGraphFile("css/site.css", "css", FileExtract.Empty),
            new CodeGraphFile("js/app.js", "javascript", FileExtract.Empty),
        ]);

        Assert.Equal(
            [new CodeEdge("file:pages/a.html", "file:css/site.css", CodeEdgeKinds.Imports, 1, CodeConfidences.Exact), new CodeEdge("file:pages/a.html", "file:js/app.js", CodeEdgeKinds.Imports, 1, CodeConfidences.Exact)],
            linked.Edges.Where(edge => edge.Kind == CodeEdgeKinds.Imports));
    }

    /// <summary>
    /// Each inline script is a program of its own, read from where its start tag ends - a module where its type says - so
    /// two on one line are never one statement; a script of no JavaScript type, a template a framework reads, is read as
    /// no code.
    /// </summary>
    [Fact]
    public void EachScriptIsAProgramOfItsOwn_FromWhereItsStartTagEnds_AndOneOfAnotherTypeIsNoCode()
    {
        var extract = Read("pages/nested.html", Nested);

        var shown = Assert.Single(extract.Nodes, node => node.Name == "shown");

        Assert.Equal(("html:pages/nested.html.shown", 24, 26, "pages/nested.html"), (shown.Id, shown.Start, shown.End, shown.Scope));
        Assert.Equal(
            [("bar", CodeEdgeKinds.Calls, 28)],
            extract.References.Where(reference => reference.Name is "bar" or "foobar" or "hidden").Select(reference => (reference.Name, reference.Kind, reference.Line)));
        Assert.Contains(extract.References, reference => reference is { Name: "foo", Kind: CodeEdgeKinds.Uses, Line: 28 });
    }

    /// <summary>
    /// Each script's literals are held by that script's own nodes, under the ids the page's merge gives them - the second
    /// script's function and top alike - and what each script's top does is over the lines its code is written on, never
    /// the page's head, markup or other scripts before it.
    /// </summary>
    [Fact]
    public void EachScriptsLiterals_AreHeldByItsOwnNodes_AndItsTopIsOverItsOwnLines()
    {
        var extract = Read("site/two.html", TwoScripts);

        Assert.Equal(
            [("html:site/two.html.(module)", 4, 4), ("html:site/two.html.(module)~2", 10, 10), ("html:init", 11, 11), ("html:init~2", 14, 14)],
            extract.Nodes.Where(node => node.Kind == CodeNodeKinds.Member).OrderBy(node => node.Start).Select(node => (node.Id, node.Start, node.End)));
        Assert.Equal(
            [
                ("https://a.example", 4, "html:site/two.html.(module)"), ("Shop home", 10, "html:site/two.html.(module)~2"), ("first one", 11, "html:init"),
                ("second one", 14, "html:init~2"),
            ],
            LiteralRow.Of(extract).OrderBy(literal => literal.Line).Select(literal => (literal.Value, literal.Line, literal.Node)));
    }

    /// <summary>What an inline script's reader marks dynamic is the page's to say as it is the script's own file's: nothing the embedded reader saw is lost.</summary>
    [Fact]
    public void AScriptsDynamicCode_IsMarkedAsItsOwnFilesIs()
    {
        var alone = new JavaScriptExtractor().Extract(new CodeFileInput("a.js", "handlers[name](); eval(code);"));
        var page = Read("a.html", "<script>handlers[name](); eval(code);</script>");

        Assert.Equal(2, alone.Dynamic.Count);
        Assert.Equal(alone.Dynamic, page.Dynamic);
    }

    /// <summary>
    /// A handler is the body of the function HTML makes of it, given the event, read by the JavaScript reader: a
    /// <c>return</c> and an <c>if</c> are statements and no names, a <c>javascript:</c> a label, <c>event</c> the
    /// function's own and <c>this</c> no name; what each calls and uses is its section's, on the line it is written on.
    /// </summary>
    [Fact]
    public void AHandler_IsTheBodyOfTheFunctionHtmlMakesOfIt_ReadByTheJavaScriptReader()
    {
        var extract = Read("h.html", "<form onsubmit=\"return validate(this)\">\n<a onclick=\"javascript:openMenu()\">m</a>\n<button onclick=\"event.preventDefault(); save()\">s</button>\n<button onclick=\"if (ok) send(1, 2)\">t</button>\n</form>\n");

        Assert.Equal(
            [
                (CodeEdgeKinds.Calls, "validate", 1, (int?)1, (string?)null), (CodeEdgeKinds.Calls, "openMenu", 2, 0, null), (CodeEdgeKinds.Calls, "preventDefault", 3, 0, CodeQualifierKinds.Value),
                (CodeEdgeKinds.Calls, "save", 3, 0, null), (CodeEdgeKinds.Uses, "ok", 4, null, null), (CodeEdgeKinds.Calls, "send", 4, 2, null),
            ],
            extract.References.Select(reference => (reference.Kind, reference.Name, reference.Line, reference.Arity, reference.Qualifier?.Kind)));
        Assert.All(extract.References, reference => Assert.Equal("html:h.html#body", reference.From));
        Assert.Equal(["html:h.html#body"], extract.Nodes.Select(node => node.Id));
    }

    /// <summary>A style element's text is read by the style sheet's reader, its rules carrying the lines they stand on.</summary>
    [Fact]
    public void AStyleElement_IsReadByTheStyleSheetsReader()
    {
        var extract = Read("pages/nested.html", Nested);
        var rule = Assert.Single(extract.Nodes, node => node.SubKind == CodeSubKinds.Rule);

        Assert.Equal(("html:pages/nested.html#.card", ".card", 6, 6), (rule.Id, rule.Name, rule.Start, rule.End));
    }

    /// <summary>
    /// Markup nested deeper than any stack holds is read whole and no stack is spent on its depth: twenty thousand
    /// elements one inside the next, read on a thread with a small stack, give the innermost section its lines.
    /// </summary>
    [Fact]
    public void MarkupNestedDeeperThanAStack_IsReadWhole()
    {
        const int Depth = 20_000;
        var text = $"<main>\n{string.Concat(Enumerable.Repeat("<div>", Depth))}\n<p id=\"deep\">x</p>\n</main>\n";
        FileExtract? extract = null;
        var reading = new Thread(() => extract = Read("deep.html", text), maxStackSize: 256 * 1024);

        reading.Start();
        Assert.True(reading.Join(TimeSpan.FromMinutes(2)), "the deep page was not read in two minutes");

        Assert.Equal([("body", 1, 4), ("main", 1, 4), ("deep", 3, 3)], extract!.Nodes.Select(node => (node.Name, node.Start, node.End)));
    }

    /// <summary>
    /// A style element is read in the syntax its <c>type</c> names, as a browser's Less reads <c>text/less</c>: its
    /// <c>//</c> comment is no rule and its <c>@pad</c> a variable; one of CSS keeps <c>//</c> as CSS does, and one of a
    /// syntax no style sheet's reader reads is not read.
    /// </summary>
    [Fact]
    public void AStyleElement_IsReadInTheSyntaxItsTypeNames()
    {
        var extract = Read("index.html", "<style type=\"text/less\">\n// .ghost { a: b; }\n@pad: 2px;\n.box { margin: @pad; }\n</style>\n<style type=\"text/x-template\">\n.never { a: b; }\n</style>\n");

        Assert.Equal(
            [("@pad", CodeSubKinds.StyleVariable, 3), (".box", CodeSubKinds.Rule, 4)],
            extract.Nodes.Where(node => CodeSubKinds.IsData(node.SubKind)).Select(node => (node.Name, node.SubKind!, node.Start)));
        Assert.Equal(
            [("html:index.html#.box", "@pad", 4, CodeQualifierKinds.StyleVariable)],
            extract.References.Where(reference => reference.Qualifier?.Kind == CodeQualifierKinds.StyleVariable).Select(reference => (reference.From, reference.Name, reference.Line, reference.Qualifier?.Kind)));
        Assert.Equal(
            [".ghost"],
            Read("plain.html", "<style>\n// .ghost { a: b; }\n</style>\n").Nodes.Where(node => CodeSubKinds.IsData(node.SubKind)).Select(node => node.Name));
    }

    private static FileExtract Read(string path, string text) => new HtmlExtractor().Extract(new CodeFileInput(path, text));
}
