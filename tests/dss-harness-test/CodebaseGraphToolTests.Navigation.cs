using System.Text.RegularExpressions;
using DssHarness.Core.Agents.Runtime;
using DssHarness.Core.Codebase;

namespace DssHarness.Tests;

public sealed partial class CodebaseGraphToolTests
{
    /// <summary>
    /// What the navigation operations are asked of beside <see cref="Demo"/>: a chain of calls - <c>Leaf</c>, called by
    /// <c>Middle</c>, called by <c>Top</c> - and a test of <c>Top</c>; a helper of the tests in a directory of its own; six
    /// types each overriding the one before; and a member that writes a field.
    /// </summary>
    internal static readonly (string Path, string Text)[] Navigation =
    [
        ("src/Chain.cs", """
            namespace Demo.Chain;

            public static class Chain
            {
                public static int Leaf()
                {
                    return 1;
                }

                public static int Middle()
                {
                    return Leaf() + 1;
                }

                public static int Top()
                {
                    return Middle() + 1;
                }
            }

            """),
        ("tests/ChainTests.cs", """
            namespace Demo.Chain.Tests;

            public sealed class ChainTests
            {
                [Fact]
                public void TopAddsTwo()
                {
                    _ = Demo.Chain.Chain.Top();
                }
            }

            """),
        ("tests/Support/Builders.cs", """
            namespace Demo.Tests.Support;

            public static class Builders
            {
                public static int One()
                {
                    return 1;
                }
            }

            """),
        ("src/Levels.cs", """
            namespace Demo.Levels;

            public class L0
            {
                public virtual int Go() => 0;
            }

            public class L1 : L0
            {
                public override int Go() => 1;
            }

            public class L2 : L1
            {
                public override int Go() => 2;
            }

            public class L3 : L2
            {
                public override int Go() => 3;
            }

            public class L4 : L3
            {
                public override int Go() => 4;
            }

            public class L5 : L4
            {
                public override int Go() => 5;
            }

            """),
        ("src/Counter.cs", """
            namespace Demo.Counting;

            public sealed class Counter
            {
                private int _count;

                public void Bump()
                {
                    _count++;
                }
            }

            """),
    ];

    /// <summary>
    /// <c>outline</c> of a directory gives every file under it, its subdirectories' too, a section for each directory by
    /// path: each file its own node's line, the language that read it and <c>test</c> where it is test code, then its top
    /// declarations - a test file's each naming what it tests by the graph's <c>tests</c> edges - in one answer, what a
    /// listing and a read of every file would take.
    /// </summary>
    [Fact]
    public async Task Outline_OfADirectory_GivesEveryFileUnderIt_ATestFileMarkedWithWhatItTests()
    {
        using var tree = await TreeAsync(Navigation);
        var tool = Tool(tree, await QueriesAsync(tree));

        var answer = await AskAsync(tool, """{"op":"outline","path":"tests"}""");

        Assert.Equal(GraphToolOutcome.Answered, answer.Outcome);
        Assert.Equal(
            """
            outline of tests/ (3 file(s)):
            tests/ (2 file(s)):
            aa81d6 file tests/ChainTests.cs | tests/ChainTests.cs:1-10 | csharp | test
              3aeb5f type public sealed class ChainTests | tests/ChainTests.cs:3-10 #f890c714 | tests Top (be96ab)
            aef0d1 file tests/GeometryTests.cs | tests/GeometryTests.cs:1-10 | csharp | test
              f58ed5 type public sealed class GeometryTests | tests/GeometryTests.cs:3-10 #49da99d3 | tests Total (bc11b2)
            tests/Support/ (1 file(s)):
            2f02e7 file tests/Support/Builders.cs | tests/Support/Builders.cs:1-9 | csharp
              08985a type public static class Builders | tests/Support/Builders.cs:3-9 #d50c6992
            """.ReplaceLineEndings("\n"),
            answer.Text);

        // Each file is one result, its declarations riding on it: the total a page counts is the files'.
        Assert.Equal(3, answer.Paging!.Total);
    }

    /// <summary>
    /// A directory is the one its path names however it is written - either separator, a leading <c>./</c>, a trailing
    /// separator - and <c>.</c> is the tree's root, every file under it.
    /// </summary>
    [Theory]
    [InlineData("tests", "tests")]
    [InlineData("tests/", "tests")]
    [InlineData("./tests/Support", "tests/Support")]
    [InlineData("tests\\Support\\", "tests/Support")]
    [InlineData(".", "")]
    [InlineData("./", "")]
    public void ADirectory_IsNamedAsTheViewsPathsSpellIt(string written, string named)
    {
        Assert.Equal(named, CodebaseGraphQueries.DirectoryNamed(written));
    }

    /// <summary>The repository's <c>outline</c> outlines the root's own files as a directory's are, after its projects, directories and namespaces.</summary>
    [Fact]
    public async Task Outline_OfTheRepository_OutlinesTheRootsOwnFiles()
    {
        using var tree = await TreeAsync();
        var tool = Tool(tree, await QueriesAsync(tree));

        var lines = (await AskAsync(tool, """{"op":"outline"}""")).Text.Split('\n');

        Assert.Matches("^base at [0-9a-f]{40}: 6 file\\(s\\)$", lines[0]);
        Assert.Equal(
            [
                "directory . (1 file(s))",
                "directory docs/ (1 file(s))",
                "directory src/ (3 file(s))",
                "directory tests/ (1 file(s))",
                "namespace Demo (3 declaration(s))",
                "namespace Demo.Generated (1 declaration(s))",
                "namespace Demo.Tests (1 declaration(s))",
                "./ (1 file(s)):",
                "42c59a file README.md | README.md:1-1 | markdown",
            ],
            lines[1..]);
    }

    /// <summary>
    /// <c>around</c> a member of a type gives its place in one answer: what it is declared in - its file, its namespace, its
    /// type - its siblings, the declarations just before and after it, and its callers and callees, each a node line with
    /// its span and hash.
    /// </summary>
    [Fact]
    public async Task Around_OfAMemberOfAType_GivesItsPlace()
    {
        using var tree = await TreeAsync(Navigation);
        var tool = Tool(tree, await QueriesAsync(tree));

        var answer = await AskAsync(tool, """{"op":"around","symbol":"Middle"}""");

        Assert.Equal(GraphToolOutcome.Answered, answer.Outcome);
        Assert.Equal(
            """
            8c3027 member public static int Middle() | src/Chain.cs:10-13 #89fe2d65 | 1 caller(s), 1 callee(s)
            declared in:
              4b5f67 file src/Chain.cs | src/Chain.cs:1-19
              1c3a49 namespace namespace Demo.Chain | src/Chain.cs:1-19 #baffda06
              35c93f type public static class Chain | src/Chain.cs:3-19 #98f7aaa4
            siblings (2):
              414b55 member public static int Leaf() | src/Chain.cs:5-8 #157a08bf | 1 caller(s), 0 callee(s)
              be96ab member public static int Top() | src/Chain.cs:15-18 #54195ec1 | 1 caller(s), 1 callee(s)
            beside it:
              before: 414b55 member public static int Leaf() | src/Chain.cs:5-8 #157a08bf | 1 caller(s), 0 callee(s)
              after: be96ab member public static int Top() | src/Chain.cs:15-18 #54195ec1 | 1 caller(s), 1 callee(s)
            callers (1):
              be96ab member public static int Top() | src/Chain.cs:15-18 #54195ec1 | 1 caller(s), 1 callee(s)
            callees (1):
              414b55 member public static int Leaf() | src/Chain.cs:5-8 #157a08bf | 1 caller(s), 0 callee(s)
            """.ReplaceLineEndings("\n"),
            answer.Text);
        Assert.Equal(["csharp:Demo.Chain.Chain.Middle()"], answer.Facts.Asked);
    }

    /// <summary><c>around</c> a type gives its members too: what is declared inside it, every part of it.</summary>
    [Fact]
    public async Task Around_OfAType_GivesItsMembers()
    {
        using var tree = await TreeAsync(Navigation);
        var tool = Tool(tree, await QueriesAsync(tree));

        var lines = (await AskAsync(tool, """{"op":"around","symbol":"Demo.Counting.Counter"}""")).Text.Split('\n');

        Assert.Equal(
            [
                "members (2):",
                "  ea4dae member private int _count | src/Counter.cs:5-5 #d7aa9f15",
                "  080dc8 member public void Bump() | src/Counter.cs:7-10 #863e7490",
            ],
            lines.SkipWhile(line => line != "members (2):"));
    }

    /// <summary>
    /// <c>impact</c> gives what a change of a node reaches - its callers at every distance, and the test that reaches the
    /// caller two hops away - each with how many edges its path has and the path itself, every node on it by its handle.
    /// </summary>
    [Fact]
    public async Task Impact_ReachesATest_ThroughACallerTwoHopsAway_WithThePathItReachesBy()
    {
        using var tree = await TreeAsync(Navigation);
        var tool = Tool(tree, await QueriesAsync(tree));

        var answer = await AskAsync(tool, """{"op":"impact","symbol":"Leaf"}""");

        Assert.Equal(GraphToolOutcome.Answered, answer.Outcome);
        Assert.StartsWith(
            """
            414b55 member public static int Leaf() | src/Chain.cs:5-8 #157a08bf | 1 caller(s), 0 callee(s)
            callers (2):
              1: 8c3027 member public static int Middle() | src/Chain.cs:10-13 #89fe2d65 | 1 caller(s), 1 callee(s) | calls Leaf (414b55)
              2: be96ab member public static int Top() | src/Chain.cs:15-18 #54195ec1 | 1 caller(s), 1 callee(s) | calls Middle (8c3027), which calls Leaf (414b55)
            tests (1):
              3: 9a9fa5 member public void TopAddsTwo() | tests/ChainTests.cs:5-9 #c5bdbaa4 | 0 caller(s), 1 callee(s) | tests Top (be96ab), which calls Middle (8c3027), which calls Leaf (414b55)
            text: lines that hold 'Leaf'

            """.ReplaceLineEndings("\n"),
            answer.Text,
            StringComparison.Ordinal);

        // The test it reaches names an attribute the graph does not hold, so the graph is not sure: the lines that hold the
        // name are added, as related adds them.
        Assert.Matches("\ntext: lines that hold 'Leaf'\n  src/Chain\\.cs:5 public static int Leaf\\(\\)\n  src/Chain\\.cs:12 return Leaf\\(\\) \\+ 1;\n\\[[0-9]+ file\\(s\\) you may not read were left out\\]$", answer.Text);
        Assert.Equal(["csharp:Demo.Chain.Chain.Leaf()"], answer.Facts.Asked);
    }

    /// <summary>
    /// <c>impact</c> follows overrides both ways and through every node it reaches: a change of a base member reaches each
    /// member that overrides it, however deep, each under the relation that reached it.
    /// </summary>
    [Fact]
    public async Task Impact_OfABaseMember_ReachesEveryOverride()
    {
        using var tree = await TreeAsync(Navigation);
        var tool = Tool(tree, await QueriesAsync(tree));

        var answer = await AskAsync(tool, """{"op":"impact","symbol":"Demo.Levels.L3.Go"}""");

        Assert.Equal(
            """
            8f60f9 member public override int Go() | src/Levels.cs:20-20 #cec9fb28
            overrides (3):
              1: cbf653 member public override int Go() | src/Levels.cs:15-15 #333f5045 | overridden by Go (8f60f9)
              2: dc545a member public override int Go() | src/Levels.cs:10-10 #bfa49a5a | overridden by Go (cbf653), which is overridden by Go (8f60f9)
              3: 984f29 member public virtual int Go() | src/Levels.cs:5-5 #a46d557d | overridden by Go (dc545a), which is overridden by Go (cbf653), which is overridden by Go (8f60f9)
            overriddenBy (2):
              1: c30b0d member public override int Go() | src/Levels.cs:25-25 #abddf906 | overrides Go (8f60f9)
              2: fddd7a member public override int Go() | src/Levels.cs:30-30 #c07a5810 | overrides Go (c30b0d), which overrides Go (8f60f9)
            """.ReplaceLineEndings("\n"),
            answer.Text);
    }

    /// <summary>
    /// <c>related</c> follows what a member overrides and what overrides it, to any depth asked: four hops reach four
    /// overrides, and a fifth hop the base of them all.
    /// </summary>
    [Fact]
    public async Task Related_FollowsOverridesBothWays_ToAnyDepth()
    {
        using var tree = await TreeAsync(Navigation);
        var tool = Tool(tree, await QueriesAsync(tree));

        var up = (await AskAsync(tool, """{"op":"related","symbol":"Demo.Levels.L5.Go","relation":"overrides","depth":4}""")).Text;
        var further = (await AskAsync(tool, """{"op":"related","symbol":"Demo.Levels.L5.Go","relation":"overrides","depth":5}""")).Text.Split('\n');
        var down = (await AskAsync(tool, """{"op":"related","symbol":"Demo.Levels.L0.Go","relation":"overriddenBy","depth":4}""")).Text;

        Assert.Equal(
            """
            1: c30b0d member public override int Go() | src/Levels.cs:25-25 #abddf906
            2: 8f60f9 member public override int Go() | src/Levels.cs:20-20 #cec9fb28
            3: cbf653 member public override int Go() | src/Levels.cs:15-15 #333f5045
            4: dc545a member public override int Go() | src/Levels.cs:10-10 #bfa49a5a
            """.ReplaceLineEndings("\n"),
            up);
        Assert.Equal([.. up.Split('\n'), "5: 984f29 member public virtual int Go() | src/Levels.cs:5-5 #a46d557d"], further);
        Assert.Equal(
            """
            1: dc545a member public override int Go() | src/Levels.cs:10-10 #bfa49a5a
            2: cbf653 member public override int Go() | src/Levels.cs:15-15 #333f5045
            3: 8f60f9 member public override int Go() | src/Levels.cs:20-20 #cec9fb28
            4: c30b0d member public override int Go() | src/Levels.cs:25-25 #abddf906
            """.ReplaceLineEndings("\n"),
            down);
    }

    /// <summary>
    /// <c>related</c> follows every edge the <c>node</c> operation groups: the types a type is built on, the interface
    /// members a member answers, what a member uses and what uses it, what a test exercises, and the other parts of a
    /// declaration - which every other relation walks as the node itself.
    /// </summary>
    [Fact]
    public async Task Related_FollowsEveryEdgeTheNodeOperationGroups()
    {
        using var tree = await TreeAsync([.. Navigation, .. XamlView]);
        var tool = Tool(tree, await QueriesAsync(tree));

        var bases = (await AskAsync(tool, """{"op":"related","symbol":"Demo.Levels.L5","relation":"bases","depth":4}""")).Text.Split('\n');
        var dispatchers = (await AskAsync(tool, """{"op":"related","symbol":"Demo.Circle.Area","relation":"dispatchers"}""")).Text;
        var uses = (await AskAsync(tool, """{"op":"related","symbol":"Bump","relation":"uses"}""")).Text;
        var users = (await AskAsync(tool, """{"op":"related","symbol":"_count","relation":"users"}""")).Text;
        var tested = (await AskAsync(tool, """{"op":"related","symbol":"TopAddsTwo","relation":"tested"}""")).Text.Split('\n');
        var parts = (await AskAsync(tool, """{"op":"related","symbol":"csharp:Shop.Cart","relation":"parts"}""")).Text;

        Assert.Equal(["1: a7ae0e type public class L4 : L3", "2: 294cb1 type public class L3 : L2", "3: eb0abc type public class L2 : L1", "4: adf2ce type public class L1 : L0"], bases.Select(line => line[..line.IndexOf(" | ", StringComparison.Ordinal)]));
        Assert.Equal("1: dff23b member double Area() | src/Shapes.cs:6-6 #704ac59f | 1 caller(s), 0 callee(s)", dispatchers);
        Assert.Equal("1: ea4dae member private int _count | src/Counter.cs:5-5 #d7aa9f15", uses);
        Assert.Equal("1: 080dc8 member public void Bump() | src/Counter.cs:7-10 #863e7490", users);
        Assert.Equal("1: be96ab member public static int Top() | src/Chain.cs:15-18 #54195ec1 | 1 caller(s), 1 callee(s)", tested[0]);
        Assert.Equal("1: 517bd8 type Shop.Cart | Views/Cart.xaml:1-5 #5463169e", parts);
    }

    /// <summary>
    /// An answer longer than a page pages whole entries: a change of the hub's leaf reaches the hub and its 220 callers a hop
    /// past it, every one of them once across the pages, each page naming the section it holds of.
    /// </summary>
    [Fact]
    public async Task Impact_OfAHub_PagesWholeEntries_EveryOneOnce()
    {
        using var tree = await TreeAsync(("src/Hub.cs", HubFile(220)));
        var tool = Tool(tree, await QueriesAsync(tree));

        var pages = new List<string[]>();

        for (var page = 1; page <= 4; page++)
        {
            pages.Add((await AskAsync(tool, $$"""{"op":"impact","symbol":"Leaf","page":{{page}}}""")).Text.Split('\n'));
        }

        var past = await AskAsync(tool, """{"op":"impact","symbol":"Leaf","page":5}""");

        Assert.Equal("page 1 of 4 · 221 results · ask again with page: 2 for the next", pages[0][0]);
        Assert.Equal("page 4 of 4 · the last", pages[3][0]);
        Assert.All(pages, page => Assert.Matches("^[0-9a-f]{6} member public static void Leaf\\(\\) \\| src/Hub\\.cs:5-7 #", page[1]));
        Assert.All(pages, page => Assert.Equal("callers (221):", page[2]));
        Assert.Matches("^  1: [0-9a-f]{6} member public static void Center\\(\\) \\| .* \\| calls Leaf \\([0-9a-f]{6}\\)$", pages[0][3]);
        Assert.Equal([CodebaseGraphTool.RelatedLines, CodebaseGraphTool.RelatedLines, CodebaseGraphTool.RelatedLines, 221 - (3 * CodebaseGraphTool.RelatedLines)], pages.Select(page => page[3..].Length));

        // Every caller a hop past the hub, each once across the pages, with the path through the hub.
        var callers = pages.SelectMany(page => page[3..]).ToList();

        Assert.Equal(221, callers.Count);
        Assert.Distinct(callers);
        Assert.All(callers[1..], line => Assert.Matches("^  2: [0-9a-f]{6} member public static void M[0-9]+\\(\\) \\| .* \\| calls Center \\([0-9a-f]{6}\\), which calls Leaf \\([0-9a-f]{6}\\)$", line));
        Assert.Equal((GraphToolOutcome.Usage, "page 5 of 'impact Leaf': it has 4 pages (1 to 4)"), (past.Outcome, past.Text));
    }

    /// <summary>
    /// The architecture document and the README say the navigation operations as the code holds them: a directory's
    /// outline, a node's place, a change's reach by the walk the pack leads with, and the relations <c>related</c> follows.
    /// </summary>
    [Fact]
    public void TheDocuments_SayTheNavigationOperations()
    {
        var architecture = Prose.Document("docs", "architecture.md");
        var readme = Prose.Document("README.md");

        Assert.Contains(Prose.Words("whose `op` is `find`, `node`, `related`, `outline`, `around`, `impact`, `body`, `sites`, `pack`, `changes`, `map`, `edit` or `wholeFile`"), architecture, StringComparison.Ordinal);
        Assert.Contains(Prose.Words($"`related` walks one relation (`{nameof(GraphRelations)}.{nameof(GraphRelations.Described)}`, the one list of each relation and what it reaches, which the schema's `relation` and the help say: {string.Join(", ", GraphRelations.All.Take(9).Select(relation => $"`{relation}`"))}, and {string.Join(", ", GraphRelations.All.Skip(9).SkipLast(1).Select(relation => $"`{relation}`"))} and `{GraphRelations.All[^1]}`"), architecture, StringComparison.Ordinal);
        Assert.Contains(Prose.Words($"`outline` of a directory (`{nameof(CodebaseGraphQueries)}.{nameof(CodebaseGraphQueries.FilesUnder)}`, the directory as `{nameof(CodebaseGraphQueries)}.{nameof(CodebaseGraphQueries.DirectoryNamed)}` reads it"), architecture, StringComparison.Ordinal);
        Assert.Contains(Prose.Words($"then each of its top declarations (`{nameof(CodebaseGraphQueries)}.{nameof(CodebaseGraphQueries.TopDeclarations)}`: every type and member no type or member of the file holds"), architecture, StringComparison.Ordinal);
        Assert.Contains(Prose.Words($"`around` gives a node's place: what it is declared in, outermost first - its file, its namespace's part there, its type (`{nameof(CodebaseGraphQueries)}.{nameof(CodebaseGraphQueries.Containers)}`"), architecture, StringComparison.Ordinal);
        Assert.Contains(Prose.Words($"`impact` gives what a change of a node reaches, by the one walk of a change's impact the change-first pack leads with (`{nameof(CodebaseContextPack)}.{nameof(CodebaseContextPack.Impact)}`, over `{nameof(CodebaseContextPack)}.{nameof(CodebaseContextPack.Impacted)}`, the edges of `{nameof(GraphRelations)}.{nameof(GraphRelations.Impact)}`)"), architecture, StringComparison.Ordinal);
        Assert.Contains(Prose.Words("`--outline` (a file the view holds; a path that names no node and is written as a file is, or is a directory the view holds files under; a symbol else), `--around`, `--impact`, `--body` and `--sites`"), architecture, StringComparison.Ordinal);
        Assert.Contains(Prose.Words("\\| --outline [<path, directory or symbol>] \\| --around <symbol> \\| --impact <symbol> \\| --body <symbol>..."), readme, StringComparison.Ordinal);
        Assert.Contains(Prose.Words("`--impact` gives what a change of a node reaches - its callers at every distance, what it overrides and what overrides it, its implementations, dispatchers and derived types, and every test that reaches any of them, each with the path it reaches by, by the walk the change-first pack leads with;"), readme, StringComparison.Ordinal);
        Assert.Contains(Prose.Words($"`--related` follows any relation of `help codebase-graph`, {string.Join(", ", GraphRelations.All.Skip(9).SkipLast(1).Select(relation => $"`{relation}`"))} and `{GraphRelations.All[^1]}` among them, to any depth;"), readme, StringComparison.Ordinal);
    }

    /// <summary>A XAML view and the C# type its <c>x:Class</c> names: two parts of one declaration.</summary>
    private static readonly (string Path, string Text)[] XamlView =
    [
        ("Views/Cart.xaml", """
            <UserControl x:Class="Shop.Cart"
                xmlns="http://schemas.microsoft.com/winfx/2009/xaml"
                xmlns:x="http://schemas.microsoft.com/winfx/2009/xaml">
              <Button x:Name="Checkout" />
            </UserControl>

            """),
        ("Views/Cart.xaml.cs", """
            namespace Shop
            {
                public partial class Cart
                {
                    public void Checkout()
                    {
                    }
                }
            }

            """),
    ];
}
