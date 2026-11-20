using DssHarness.Core.Codebase;
using DssHarness.Core.Codebase.Languages;

namespace DssHarness.Tests;

/// <summary>The one way a markup file's reader embeds the code it holds: what the code's reader is given, and how what it answers is merged.</summary>
public sealed class EmbeddedCodeTests
{
    /// <summary>
    /// Every field of what an embedded reader answers is merged into the host's: an id another reader gave is told apart,
    /// what the node holds, what it makes and the literals it holds following it; a scope once by its name; the imports,
    /// address roots and dynamic marks added; the literals, every one of each; and why the file is generated the first
    /// reader's that says.
    /// </summary>
    [Fact]
    public void EveryFieldOfAnEmbeddedExtract_IsMergedIntoTheHosts()
    {
        var host = new FileExtract([new ExtractNode("html:x.html#body", CodeNodeKinds.Section, "body", "x.html#body", 1, 9, "body")])
        {
            References = [new ExtractReference("html:x.html#body", CodeEdgeKinds.Uses, ".a", 1)],
            Literals = LiteralRow.Table([.. Enumerable.Range(0, 2_500).Select(index => new LiteralRow($"value {index}", 1))]),
        };

        var merged = EmbeddedCode.Merged(host, [Script(2, "one"), Script(5, "two") with { Generated = "the second says" }]);

        Assert.Equal(
            [
                ("html:x.html#body", null), ("html:open", null), ("html:open.inner", "html:open"), ("html:open~2", null), ("html:open.inner~2", "html:open~2"),
            ],
            merged.Nodes.Select(node => (node.Id.Whole(), node.Parent?.Whole())));
        Assert.Equal(
            [("html:x.html#body", ".a"), ("html:open", "one"), ("html:open~2", "two")],
            merged.References.Select(reference => (reference.From.Whole(), reference.Name)));
        Assert.Equal([new ExtractScope("x.html", false)], merged.Scopes);
        Assert.Equal([("./one.js", 2), ("./two.js", 5)], merged.Imports.Select(import => (import.Target, import.Line)));
        Assert.Equal([new ExtractAddressRoot("@", "src"), new ExtractAddressRoot("@", "src")], merged.AddressRoots);
        Assert.Equal([new ExtractDynamic(2, "one"), new ExtractDynamic(5, "two")], merged.Dynamic);
        Assert.Equal([.. Enumerable.Range(0, 2_500).Select(index => $"value {index}"), "one", "one-2", "two", "two-2"], LiteralRow.Of(merged).Select(literal => literal.Value));

        // The second script's literal is held by its own node, under the id the merge gave it.
        Assert.Equal(
            [("one", 2, "html:open"), ("one-2", 2, "html:open.inner"), ("two", 5, "html:open~2"), ("two-2", 5, "html:open.inner~2")],
            LiteralRow.Of(merged).Skip(2_500).Select(literal => (literal.Value, literal.Line, literal.Node)));
        Assert.Equal("the first says", merged.Generated);
    }

    /// <summary>
    /// A node folded into one of the host's is gone, and every field that named it names the node it is folded into: what
    /// it held, the references it made and the literals it held - so the extract holds no field naming a node it lacks.
    /// </summary>
    [Fact]
    public void AFoldedNode_IsGone_AndEveryFieldThatNamedIt_NamesTheNodeItIsFoldedInto()
    {
        var read = Script(2, "one");
        var folded = EmbeddedCode.Folded(read, node => node.Id.Is("html:open") ? QualifiedName.Of("html:x.html#body") : null);

        Assert.Equal([("html:open.inner", "html:x.html#body")], folded.Nodes.Select(node => (node.Id.Whole(), node.Parent?.Whole())));
        Assert.Equal([("html:x.html#body", "one")], folded.References.Select(reference => (reference.From.Whole(), reference.Name)));
        Assert.Equal([("one", "html:x.html#body"), ("one-2", "html:open.inner")], LiteralRow.Of(folded).Select(literal => (literal.Value, literal.Node)));
    }

    /// <summary>
    /// Moving an extract's ids moves every field that names a node: a node's id, its parent and each node its name is
    /// written onto, the node a reference is made from and the node of its own file its qualifier names by id, and the
    /// node a literal is held by - and leaves a qualifier that names a value, no node, as it is.
    /// </summary>
    [Fact]
    public void MovingAnExtractsIds_MovesEveryFieldThatNamesANode()
    {
        var read = new FileExtract(
        [
            new ExtractNode("css:a.css#.a", CodeNodeKinds.Member, ".a", ".a", 1, 1, ".a") { SubKind = CodeSubKinds.Rule },
            new ExtractNode("css:a.css#.a&-x", CodeNodeKinds.Member, "&-x", "&-x", 1, 1, "&-x") { SubKind = CodeSubKinds.Rule, Parent = "css:a.css#.a", SuffixOf = ["css:a.css#.a"] },
        ])
        {
            References =
            [
                new ExtractReference("css:a.css#.a&-x", CodeEdgeKinds.Uses, "a", 1) { Qualifier = new ExtractQualifier(CodeQualifierKinds.Own, "css:a.css#.a") },
                new ExtractReference("css:a.css#.a", CodeEdgeKinds.Uses, "b", 1) { Qualifier = new ExtractQualifier(CodeQualifierKinds.Value, "css:a.css#.a") },
            ],
            Literals = LiteralRow.Table(new LiteralRow("red", 1, "css:a.css#.a")),
        };

        var moved = read.Renamed(id => $"{id}~2");

        Assert.Equal(
            [("css:a.css#.a~2", null, null), ("css:a.css#.a&-x~2", "css:a.css#.a~2", "css:a.css#.a~2")],
            moved.Nodes.Select(node => (node.Id.Whole(), node.Parent?.Whole(), node.SuffixOf is { } onto ? string.Join(',', onto) : null)));
        Assert.Equal(
            [("css:a.css#.a&-x~2", "css:a.css#.a~2"), ("css:a.css#.a~2", "css:a.css#.a")],
            moved.References.Select(reference => (reference.From.Whole(), reference.Qualifier!.Name?.Whole())));
        Assert.Equal(["css:a.css#.a~2"], LiteralRow.Of(moved).Select(literal => literal.Node));
    }

    /// <summary>
    /// The text an embedded reader is given keeps each range of code on the host's line it stands on - the line breaks
    /// between kept, nothing else - and two ranges on one line apart by a space, so no token of one runs into the next.
    /// </summary>
    [Fact]
    public void KeptCode_StandsOnItsHostsLines_AndTwoRangesOnALineStayApart()
    {
        const string Text = "<p>\n<script>var x = foo</script><script>bar()</script>\n<script>\nlast()</script>";
        var first = Text.IndexOf("var", StringComparison.Ordinal);
        var second = Text.IndexOf("bar", StringComparison.Ordinal);
        var third = Text.IndexOf("\nlast", StringComparison.Ordinal);

        var kept = EmbeddedCode.Kept(Text, CodeFileText.LineIndex(Text), [(first, first + 11), (second, second + 5), (third, third + 7)]);

        Assert.Equal("\nvar x = foo bar()\n\nlast()", kept.Slice(0, kept.Length));
    }

    private static FileExtract Script(int line, string name) => new(
    [
        new ExtractNode("html:open", CodeNodeKinds.Member, "open", "open", line, line + 1, "open()"),
        new ExtractNode("html:open.inner", CodeNodeKinds.Member, "inner", "open.inner", line, line, "inner") { Parent = "html:open" },
    ])
    {
        Scopes = [new ExtractScope("x.html", false)],
        Imports = [new ExtractImport(CodeImportForms.Relative, $"./{name}.js", line)],
        AddressRoots = [new ExtractAddressRoot("@", "src")],
        References = [new ExtractReference("html:open", CodeEdgeKinds.Calls, name, line)],
        Dynamic = [new ExtractDynamic(line, name)],
        Literals = LiteralRow.Table(new LiteralRow(name, line, "html:open"), new LiteralRow($"{name}-2", line, "html:open.inner")),
        Generated = name == "one" ? "the first says" : null,
    };
}
