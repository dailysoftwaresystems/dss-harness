using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DssHarness.Core.Agents.Runtime;
using DssHarness.Core.Codebase;
using DssHarness.Core.Codebase.Languages;

namespace DssHarness.Tests;

public sealed partial class CodebaseGraphToolTests
{
    /// <summary>
    /// <c>sites</c> answers every place that decides a name: its definers, its callers and its implementations - one
    /// hop off the definers' own edges, a hub listed and never walked through - each a card in a section of how it
    /// decides, and the text search of what the graph cannot answer of it - the listed file no extractor reads, and
    /// the files it is unsure of, their hits cards of the node at the line - with the coverage lines that end the
    /// answer, and no file the graph answers of said to be one the reader may not read.
    /// </summary>
    [Fact]
    public async Task Sites_OfAName_AnswerWhatDefinesCallsAndImplementsIt_WithTheCoverageLines()
    {
        using var tree = await TreeAsync();
        var tool = Tool(tree, await QueriesAsync(tree));

        var answer = await AskAsync(tool, """{"op":"sites","data":"Area"}""");
        var lines = answer.Text.Split('\n');

        Assert.Equal(GraphToolOutcome.Answered, answer.Outcome);

        // The answer of one page: no header, and its whole text as the graph holds the tree.
        Assert.Equal(
            [
                "every place that decides 'Area':",
                "defines (3):",
                "  7bfbc3 member public double Area() | src/Shapes.cs:12-15 #2a3dc106 | 1 caller(s), 0 callee(s)",
                "  df67b4 member public double Area() | src/Shapes.g.cs:6-9 #f393c58e | 1 caller(s), 0 callee(s), generated",
                "  dff23b member double Area() | src/Shapes.cs:6-6 #704ac59f | 1 caller(s), 0 callee(s)",
                "calls (1):",
                "  bc11b2 member public static double Total(IShape[] shapes) | src/Geometry.cs:5-14 #5b03176a | 2 caller(s), 3 callee(s)",
                "implements (1):",
                "  7bfbc3 member public double Area() | src/Shapes.cs:12-15 #2a3dc106 | 1 caller(s), 0 callee(s)",
                "text: lines that hold 'Area'",
                "  bc11b2 member public static double Total(IShape[] shapes) | src/Geometry.cs:5-14 #5b03176a | 2 caller(s), 3 callee(s) | src/Geometry.cs:10 sum += shape.Area();",
                "  df67b4 member public double Area() | src/Shapes.g.cs:6-9 #f393c58e | 1 caller(s), 0 callee(s), generated | src/Shapes.g.cs:6 public double Area()",
                "coverage: 1 file(s) the graph lists only (.txt) searched by text - 0 hit(s)",
                "coverage: 2 file(s) the graph is unsure of, searched by text too - 2 hit(s)",
                "coverage: nothing is assumed of a language with no extractor; those files are read by text alone",
            ],
            lines[..^1]);

        // Only the files no step reads are left out, as many as the text fallback leaves out for the same tree: the
        // files the graph answers of - passed over by the search's own will - are never said to be ones the reader
        // may not read.
        var fallback = (await AskAsync(tool, """{"op":"find","query":"Okapi"}""")).Text.Split('\n')[^1];

        Assert.Matches(@"^\[\d+ file\(s\) you may not read were left out\]$", fallback);
        Assert.Equal(fallback, lines[^1]);
        Assert.Equal(1, answer.Paging!.Pages);
        Assert.Equal(7, answer.Paging.Total);
        Assert.Equal(["csharp:Demo.Circle.Area()", "csharp:Demo.Generated.Square.Area()", "csharp:Demo.IShape.Area()"], answer.Facts.Asked);
        Assert.Equal(2, answer.Facts.Hits);
    }

    /// <summary>
    /// Where a <c>sites</c> answer searched files the graph is unsure of by text, it ends with the limits of their language's
    /// implementation that the files show the sign of - a dynamic mark's kind the file holds, or references the linker left
    /// candidates - each once, as its help says them, and with no other limit, which is the help's alone, and with nothing of a
    /// language whose files it did not search: read before what the answer says of their files is trusted.
    /// </summary>
    [Fact]
    public async Task Sites_WhereFilesOfALanguageAreSearchedByText_EndWithTheLimitsItsFilesShow()
    {
        // A file holding a mark of one kind; a file the linker left candidates in and holds no mark; and the tree every tree of these
        // tests holds, whose C# limits the code gives no sign of.
        using var marked = await TreeAsync(("shop/total.py", "def total(cart):\n    \"\"\"The cart's total.\"\"\"\n    return sum(getattr(line, \"cents\") for line in cart.lines)\n"));
        using var typed = await TreeAsync(("shop/total.py", "class Tea:\n    def cents(self):\n        return 350\n\n\nclass Cake:\n    def cents(self):\n        return 500\n\n\ndef total(cart):\n    return cart.cents()\n"));
        using var other = await TreeAsync();

        var computed = (await AskAsync(Tool(marked, await QueriesAsync(marked)), """{"op":"sites","data":"total"}""")).Text.Split('\n');
        var duck = (await AskAsync(Tool(typed, await QueriesAsync(typed)), """{"op":"sites","data":"total"}""")).Text.Split('\n');
        var plain = (await AskAsync(Tool(other, await QueriesAsync(other)), """{"op":"sites","data":"Area"}""")).Text.Split('\n');

        // The coverage lines, then the limits the files show, then the files left out: the notes in their order.
        string[] Tail(string[] answer)
        {
            var coverage = Array.FindIndex(answer, line => line.Contains("unsure of", StringComparison.Ordinal));

            Assert.True(coverage >= 0, string.Join('\n', answer));
            Assert.Matches(@"^\[\d+ file\(s\) you may not read were left out\]$", answer[^1]);

            return answer[(coverage + 2)..^1];
        }

        // A mark of one kind shows one limit: the limit that names the kind, and no other of the language's. The expected line is
        // taken by the limit's words, never by the sign, so a limit that loses its sign is seen to lose its place in the answer.
        Assert.Equal([Said("computed attribute access")], Tail(computed));
        Assert.Equal("coverage: 3 file(s) the graph is unsure of, searched by text too - 2 hit(s)", computed.Single(line => line.Contains("unsure of", StringComparison.Ordinal)));

        // A file left candidates in shows the limit that names candidates.
        Assert.Equal([Said("a duck-typed receiver")], Tail(duck));

        // What makes each shown: the signs the limits name - the kind of the mark the one file holds, candidates the other is left.
        var limits = CodeGraphExtractors.OfThisBuild.Find("python")!.Help.DoesNotSee;

        Assert.Equal([PythonReading.ComputedAttribute], Assert.Single(limits, limit => limit.Says.StartsWith("computed attribute access", StringComparison.Ordinal)).Signs);
        Assert.Equal([CodeGraphLimit.Candidates, PythonReading.FunctionValue], Assert.Single(limits, limit => limit.Says.StartsWith("a duck-typed receiver", StringComparison.Ordinal)).Signs);
        Assert.Equal(5, limits.Count(limit => limit.Signs.Count > 0));

        // A language whose limits the code gives no sign of: nothing said of them, where its files were searched as they were here.
        Assert.Empty(Tail(plain));
    }

    /// <summary>The one Python limit whose clause holds <paramref name="names"/>, as a <c>sites</c> answer ends with it.</summary>
    private static string Said(string names)
        => "python does not see " + Assert.Single(CodeGraphExtractors.OfThisBuild.Find("python")!.Help.DoesNotSee, limit => limit.Says.Contains(names, StringComparison.Ordinal)).Says + ".";

    /// <summary>
    /// A quoted data asks by its value alone, never the symbols: a name the graph holds declarations of answers no
    /// defines section quoted, and the graph's hold on a literal is the same answer as a text hit in a file no
    /// extractor reads, as a raw line.
    /// </summary>
    [Fact]
    public async Task AQuotedSitesData_AsksByItsValueAlone_NeverTheSymbols()
    {
        using var tree = await TreeAsync(
            ("src/Keys.cs", "namespace Demo;\n\npublic static class Keys\n{\n    public static string Of()\n    {\n        return \"Area\";\n    }\n}\n"));
        var tool = Tool(tree, await QueriesAsync(tree));

        var quoted = (await AskAsync(tool, """{"op":"sites","data":"\"Area\""}""")).Text.Split('\n');
        var okapi = await AskAsync(tool, """{"op":"sites","data":"'Okapi'"}""");

        // A quoted name of symbols the graph holds: no defines and no calls - the value alone is asked - but every
        // literal that holds it, as a card, and every line of the files the graph cannot answer of.
        Assert.Equal("every place that decides the literal 'Area':", quoted[0]);
        Assert.Equal("holds the literal (1):", quoted[1]);
        Assert.Equal("  f8d51c member public static string Of() | src/Keys.cs:5-8 #22f96ba7 | holds 'Area' at src/Keys.cs:7", quoted[2]);
        Assert.Equal("text: lines that hold 'Area'", quoted[3]);
        Assert.Contains(" | src/Geometry.cs:10 sum += shape.Area();", quoted[4], StringComparison.Ordinal);
        Assert.Contains(" | src/Shapes.g.cs:6 public double Area()", quoted[5], StringComparison.Ordinal);
        Assert.DoesNotContain(quoted, line => line.StartsWith("defines (", StringComparison.Ordinal) || line.StartsWith("calls (", StringComparison.Ordinal));

        // Single quotes ask by the value as double ones do, and a value only a listed file holds is a raw line.
        Assert.Equal("every place that decides the literal 'Okapi':", okapi.Text.Split('\n')[0]);
        Assert.Contains("text: lines that hold 'Okapi'\n  docs/zoo.txt:1 An Okapi grazes here.", okapi.Text, StringComparison.Ordinal);
        Assert.Contains("coverage: 1 file(s) the graph lists only (.txt) searched by text - 1 hit(s)", okapi.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>sites</c> answers in pages of whole entries like every read: the header naming the pages and the exact
    /// total, the last page saying it is the last, every site on exactly one page, none cut, none repeated.
    /// </summary>
    [Fact]
    public async Task Sites_AnswersInPagesOfWholeEntries_EverySiteOnExactlyOnePage()
    {
        var overloads = new StringBuilder("namespace Demo;\n\npublic static class Decider\n{");

        for (var at = 0; at < 61; at++)
        {
            overloads.Append(CultureInfo.InvariantCulture, $"\n    public static void Decides(int value{at})\n    {{\n    }}\n");
        }

        using var tree = await TreeAsync(("src/Decider.cs", overloads.Append("}\n").ToString()));
        var tool = Tool(tree, await QueriesAsync(tree));

        var first = (await AskAsync(tool, """{"op":"sites","data":"Decides"}""")).Text.Split('\n');
        var pages = int.Parse(first[0].Split(' ')[3], CultureInfo.InvariantCulture);

        Assert.Equal($"page 1 of {pages} · 61 results · ask again with page: 2 for the next", first[0]);
        Assert.Equal("every place that decides 'Decides':", first[1]);
        Assert.Equal("defines (61):", first[2]);

        // Every page read to the last: the union is every site once, and the coverage lines end the last.
        var all = first[3..].ToList();

        for (var page = 2; page <= pages; page++)
        {
            var later = (await AskAsync(tool, $$"""{"op":"sites","data":"Decides","page":{{page}}}""")).Text.Split('\n');
            all.AddRange(later[1..]);

            if (page == pages)
            {
                Assert.Equal($"page {pages} of {pages} · the last", later[0]);
                Assert.Contains("coverage: nothing is assumed of a language with no extractor; those files are read by text alone", later[^2], StringComparison.Ordinal);
            }
        }

        var sites = all.Where(line => line.StartsWith("  ", StringComparison.Ordinal)).ToList();

        Assert.Equal(61, sites.Count);
        Assert.Distinct(sites);

        var past = await AskAsync(tool, $$"""{"op":"sites","data":"Decides","page":{{pages + 1}}}""");

        Assert.Equal((GraphToolOutcome.Usage, $"page {pages + 1} of 'sites Decides': it has {pages} pages (1 to {pages})"), (past.Outcome, past.Text));
    }

    /// <summary>A <c>sites</c> call that names no data, one that is empty, or one with a member it does not read, is a misuse answered with a usage line.</summary>
    [Fact]
    public async Task ASitesCall_NeedsItsData_AndReadsNoOtherMember()
    {
        using var tree = await TreeAsync();
        var tool = Tool(tree, await QueriesAsync(tree));

        var missing = await AskAsync(tool, """{"op":"sites"}""");
        var blank = await AskAsync(tool, """{"op":"sites","data":" "}""");
        var stray = await AskAsync(tool, """{"op":"sites","data":"Area","kind":"type"}""");

        Assert.Equal(
            (GraphToolOutcome.Usage, "op 'sites' needs data: the data whose deciding places are asked: a name, a key path or a diagnostic id as symbol accepts a name, a rule's selector as a rule writes it (.btn, #main), or a quoted literal, asked by its value alone."),
            (missing.Outcome, missing.Text));
        Assert.Equal((GraphToolOutcome.Usage, "data is empty: the data whose deciding places are asked: a name, a key path or a diagnostic id as symbol accepts a name, a rule's selector as a rule writes it (.btn, #main), or a quoted literal, asked by its value alone."), (blank.Outcome, blank.Text));
        Assert.Equal((GraphToolOutcome.Usage, "op 'sites' does not read kind: it reads data, page"), (stray.Outcome, stray.Text));
    }

    /// <summary>
    /// Where nothing decides the data - no declaration, no literal, no line - the answer says so under its head, and
    /// still ends with its coverage lines.
    /// </summary>
    [Fact]
    public async Task Sites_OfDataNothingDecides_SaySo_AndStillEndWithTheCoverageLines()
    {
        using var tree = await TreeAsync();
        var tool = Tool(tree, await QueriesAsync(tree));

        var answer = await AskAsync(tool, """{"op":"sites","data":"NothingAnywhere"}""");

        Assert.Equal(
            [
                "every place that decides 'NothingAnywhere':",
                "the graph holds no declaration of it, no literal holds it and no line does",
                "coverage: 1 file(s) the graph lists only (.txt) searched by text - 0 hit(s)",
                "coverage: 2 file(s) the graph is unsure of, searched by text too - 0 hit(s)",
                "coverage: nothing is assumed of a language with no extractor; those files are read by text alone",
            ],
            answer.Text.Split('\n')[..^1]);
        Assert.Equal(0, answer.Paging!.Total);
    }
}
