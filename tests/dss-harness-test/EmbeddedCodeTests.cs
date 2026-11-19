using DssHarness.Core.Codebase;
using DssHarness.Core.Codebase.Languages;

namespace DssHarness.Tests;

/// <summary>The one way a markup file's reader embeds the code it holds: what the code's reader is given, and how what it answers is merged.</summary>
public sealed class EmbeddedCodeTests
{
    /// <summary>
    /// Every field of what an embedded reader answers is merged into the host's: an id another reader gave is told apart,
    /// what the node holds and what it makes following it; a scope once by its name; the imports, address roots and
    /// dynamic marks added; the literals, every one of each; and why the file is generated the first reader's that says.
    /// </summary>
    [Fact]
    public void EveryFieldOfAnEmbeddedExtract_IsMergedIntoTheHosts()
    {
        var host = new FileExtract([new ExtractNode("html:x.html#body", CodeNodeKinds.Section, "body", "x.html#body", 1, 9, "body")])
        {
            References = [new ExtractReference("html:x.html#body", CodeEdgeKinds.Uses, ".a", 1)],
            Literals = [.. Enumerable.Range(0, 2_500).Select(index => new ExtractLiteral($"v{index}", 1))],
        };

        var merged = EmbeddedCode.Merged(host, [Script(2, "one"), Script(5, "two") with { Generated = "the second says" }]);

        Assert.Equal(
            [
                ("html:x.html#body", null), ("html:open", null), ("html:open.inner", "html:open"), ("html:open~2", null), ("html:open.inner~2", "html:open~2"),
            ],
            merged.Nodes.Select(node => (node.Id, node.Parent)));
        Assert.Equal(
            [("html:x.html#body", ".a"), ("html:open", "one"), ("html:open~2", "two")],
            merged.References.Select(reference => (reference.From, reference.Name)));
        Assert.Equal([new ExtractScope("x.html", false)], merged.Scopes);
        Assert.Equal([("./one.js", 2), ("./two.js", 5)], merged.Imports.Select(import => (import.Target, import.Line)));
        Assert.Equal([new ExtractAddressRoot("@", "src"), new ExtractAddressRoot("@", "src")], merged.AddressRoots);
        Assert.Equal([new ExtractDynamic(2, "one"), new ExtractDynamic(5, "two")], merged.Dynamic);
        Assert.Equal([.. Enumerable.Range(0, 2_500).Select(index => $"v{index}"), "one", "one-2", "two", "two-2"], (merged.Literals ?? []).Select(literal => literal.Value));
        Assert.Equal("the first says", merged.Generated);
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

        var kept = EmbeddedCode.Kept(Text, [(first, first + 11), (second, second + 5), (third, third + 7)]);

        Assert.Equal("\nvar x = foo bar()\n\nlast()", kept);
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
        Literals = [new ExtractLiteral(name, line), new ExtractLiteral($"{name}-2", line)],
        Generated = name == "one" ? "the first says" : null,
    };
}
