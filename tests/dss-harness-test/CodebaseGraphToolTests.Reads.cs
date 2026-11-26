using DssHarness.Core.Agents.Runtime;
using DssHarness.Core.Codebase;
using DssHarness.Core.Output;

namespace DssHarness.Tests;

/// <summary>The graph in the answers of the runtime's reads: a file's declarations as it stands now, and a whole read answered with its outline.</summary>
public sealed partial class CodebaseGraphToolTests
{
    /// <summary>
    /// A whole read the narrowing takes is answered with its line and then the file's outline as <c>outline</c> gives it -
    /// every declaration with its span and its hash - as the file stands now: an edit the view was never synced by is read
    /// again, so the spans are the moved ones and the hash of a changed body is the one its lines give, and the tool answers
    /// from that view from then on.
    /// </summary>
    [Fact]
    public async Task AWholeReadTheNarrowingTakes_IsAnsweredWithTheFilesOutline_AsTheFileStandsNow()
    {
        using var tree = await TreeAsync(("src/Big.cs", TwoMembers));
        var stale = await QueriesAsync(tree);
        var tool = Tool(tree, stale);
        var was = Assert.Single(stale.OutlineFile("src/Big.cs")!, card => card.Name == "One");

        tree.Write("src/Big.cs", "// formatted\n" + TwoMembers.Replace("return 1;", "return 11;", StringComparison.Ordinal));
        var whole = await tool.WholeReadAsync("src/Big.cs", 224, TestContext.Current.CancellationToken);
        var now = tool.Queries;
        var outline = now.OutlineFile("src/Big.cs")!;
        var one = Assert.Single(outline, card => card.Name == "One");
        var lines = CodeFileText.Lines(File.ReadAllText(Path.Combine(tree.Tree, "src", "Big.cs")));

        Assert.NotSame(stale, now);
        Assert.Equal((6, 9, CodeGraphIds.Digest(lines, 6, 9)[..8]), (one.Start, one.End, one.Hash));
        Assert.NotEqual(was.Hash, one.Hash);
        Assert.Equal((true, "src/Big.cs", (string?)null), (whole.Narrowed, whole.Path, whole.Escape));
        Assert.Equal(
            string.Join('\n', [
                "'src/Big.cs' is 224 lines and in the codebase graph, so its outline is given in place of the whole file: read the lines you need with start_line and end_line, or the file a page at a time with page, ask graph's body for a declaration by its handle, or, if you need the file whole, say why with graph's wholeFile",
                $"outline of src/Big.cs ({ReportText.Counted(outline.Count, "declaration")}):",
                .. outline.Select(GraphLines.Of)]),
            whole.Text);

        // A read the narrowing does not take is answered as WholeRead decides it, its text untouched.
        Assert.Equal(tool.WholeRead("src/Big.cs", 20), await tool.WholeReadAsync("src/Big.cs", 20, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Where the tool cannot read a file again - here it is handed no way to - a declaration whose lines changed since the
    /// graph read the file stands for lines the file no longer holds: it is left out and counted, never given with a span
    /// or a hash the file does not hold - no hit is placed under it, no range is headed by it, and an outline says how many
    /// it left out - while one whose lines the file still holds is given as it is.
    /// </summary>
    [Fact]
    public async Task ADeclarationWhoseLinesChanged_WhereTheFileCannotBeReadAgain_IsLeftOutAndCounted()
    {
        using var tree = await TreeAsync(("src/Big.cs", TwoMembers));
        var view = await QueriesAsync(tree);
        var tool = Tool(tree, view, sync: false);
        var outline = view.OutlineFile("src/Big.cs")!;
        var one = Assert.Single(outline, card => card.Name == "One");

        tree.Write("src/Big.cs", TwoMembers.Replace("return 2;", "return 22;", StringComparison.Ordinal));
        var declarations = await tool.DeclarationsAsync("src/Big.cs", TestContext.Current.CancellationToken);

        // Two changed, and with it the class and the namespace that hold its lines: only One is as the graph read it.
        Assert.NotNull(declarations);
        Assert.Same(view, tool.Queries);
        Assert.Equal([one], declarations.Cards);
        Assert.Equal(outline.Count - 1, declarations.Moved);
        Assert.Equal(one, declarations.Holding(7));
        Assert.Null(declarations.Holding(221));
        Assert.Null(declarations.Holding(100));
        Assert.Equal([one], declarations.Meeting(1, 223));

        var whole = await tool.WholeReadAsync("src/Big.cs", 223, TestContext.Current.CancellationToken);

        Assert.Equal(
            string.Join('\n', [
                tool.WholeRead("src/Big.cs", 223).Text,
                "outline of src/Big.cs (1 declaration(s)):",
                GraphLines.Of(one),
                $"{ReportText.Counted(outline.Count - 1, "declaration")} left out: their lines changed since the graph read the file, and it could not read it again"]),
            whole.Text);
    }

    /// <summary>
    /// A file the view holds no parsed file at - a text no implementation reads, none there - or one the step's reads do not
    /// reach has no declarations to give: the reads that would use them are answered as they are.
    /// </summary>
    [Fact]
    public async Task AFileTheGraphParsedNot_OrOneTheReadsDoNotReach_HasNoDeclarations()
    {
        using var tree = await TreeAsync();
        var tool = Tool(tree, await QueriesAsync(tree), mayRead: path => path != "tests/GeometryTests.cs");

        Assert.Null(await tool.DeclarationsAsync("docs/zoo.txt", TestContext.Current.CancellationToken));
        Assert.Null(await tool.DeclarationsAsync("src/Missing.cs", TestContext.Current.CancellationToken));
        Assert.Null(await tool.DeclarationsAsync("tests/GeometryTests.cs", TestContext.Current.CancellationToken));
        Assert.NotNull(await tool.DeclarationsAsync("src/Geometry.cs", TestContext.Current.CancellationToken));
    }

    /// <summary>A C# class of two members, One at lines 5-8 and Two at 219-222, with 210 lines of comment between: 223 lines, past the narrowing's bound.</summary>
    private static readonly string TwoMembers =
        "namespace Demo;\n\npublic static class Big\n{\n    public static int One()\n    {\n        return 1;\n    }\n"
        + string.Concat(Enumerable.Range(0, 210).Select(at => $"    // line {at}\n"))
        + "    public static int Two()\n    {\n        return 2;\n    }\n}\n";
}
