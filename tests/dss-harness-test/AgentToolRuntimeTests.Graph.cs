using System.Globalization;
using DssHarness.Core.Agents;
using DssHarness.Core.Agents.Runtime;
using DssHarness.Core.Codebase;
using DssHarness.Core.Pages;

namespace DssHarness.Tests;

/// <summary>
/// The codebase graph inside the reads an agent already makes: a search's hits under the declarations they are in - a page
/// of the answer that begins among a declaration's hits headed by its line again - a range headed by the declarations it
/// meets - each as the file stands now - and the tools saying so in a graph step.
/// </summary>
public sealed partial class AgentToolRuntimeTests
{
    /// <summary>
    /// In a step that has a graph, each hit of a search inside a declaration of a file the graph parsed is given under that
    /// declaration's line - the innermost that holds it, named as the graph names a node - the declaration's hits and their
    /// context indented beneath it, said again only where a hit outside it came between; a hit outside every declaration, or
    /// in a file the graph does not parse, is given as it is. The spans and hashes are the file's as it stands after an edit
    /// the view was never synced by - its declarations read again - and a step with no graph answers exactly as before.
    /// </summary>
    [Fact]
    public async Task ASearchInAGraphStep_GivesEachHitUnderItsDeclaration_AsTheFileStandsNow()
    {
        using var tree = await CodebaseGraphToolTests.TreeAsync(("src/notes.txt", "the sum of the shapes\n"));
        var stale = await CodebaseGraphToolTests.QueriesAsync(tree);
        var tool = CodebaseGraphToolTests.Tool(tree, stale);
        var graphed = Narrowing(tree, tool, []);
        var plain = OverTree(tree, graph: null);
        var geometry = Path.Combine(tree.Tree, "src", "Geometry.cs");
        var was = Declared(stale, "Demo.Geometry.Total");

        // An edit no sync followed: every line moved one down, and the body of Total changed.
        File.WriteAllText(geometry, "// formatted\n" + File.ReadAllText(geometry).Replace("var sum = 0.0;", "var sum = 0.5;", StringComparison.Ordinal));
        var text = File.ReadAllLines(geometry);
        string Hit(int line) => $"src/Geometry.cs:{line.ToString(CultureInfo.InvariantCulture)}: {text[line - 1]}";
        string Around(int line) => $"src/Geometry.cs-{line.ToString(CultureInfo.InvariantCulture)}- {text[line - 1]}";

        const string Sought = @"\bsum\b|namespace Demo;|return Total";
        var found = await Call(graphed, AgentTools.SearchFiles, ("pattern", Sought), ("path", "src"));
        var total = Declared(tool.Queries, "Demo.Geometry.Total");
        var twice = Declared(tool.Queries, "Demo.Geometry.Twice");

        // The header names the declaration as the file stands now: its new lines, and the hash those lines give.
        Assert.NotSame(stale, tool.Queries);
        Assert.Equal((6, 15, CodeGraphIds.Digest(text, 6, 15)[..8]), (total.Start, total.End, total.Hash));
        Assert.NotEqual(was.Hash, total.Hash);
        Assert.True(found.Ok, found.Text);
        Assert.Equal(
            string.Join('\n', [
                Hit(2),
                GraphLines.Of(total), "  " + Hit(8), "  " + Hit(11), "  " + Hit(14),
                GraphLines.Of(twice), "  " + Hit(19),
                "src/Shapes.cs:1: namespace Demo;",
                "src/notes.txt:1: the sum of the shapes"]),
            found.Text);

        // With context, each hit's lines around it are its declaration's, and the separator between two hits of one
        // declaration is indented with them.
        var context = await Call(graphed, AgentTools.SearchFiles, ("pattern", "var sum|return sum|return Total"), ("glob", "src/Geometry.cs"), ("context", 1));

        Assert.Equal(
            string.Join('\n', [
                GraphLines.Of(total), "  " + Around(7), "  " + Hit(8), "  " + Around(9), "  --", "  " + Around(13), "  " + Hit(14), "  " + Around(15),
                "--",
                GraphLines.Of(twice), "  " + Around(18), "  " + Hit(19), "  " + Around(20)]),
            context.Text);

        // A step with no graph answers as it always has.
        Assert.Equal(
            string.Join('\n', [Hit(2), Hit(8), Hit(11), Hit(14), Hit(19), "src/Shapes.cs:1: namespace Demo;", "src/notes.txt:1: the sum of the shapes"]),
            (await Call(plain, AgentTools.SearchFiles, ("pattern", Sought), ("path", "src"))).Text);
        Assert.Equal(
            string.Join('\n', [Around(7), Hit(8), Around(9), "--", Around(13), Hit(14), Around(15), "--", Around(18), Hit(19), Around(20)]),
            (await Call(plain, AgentTools.SearchFiles, ("pattern", "var sum|return sum|return Total"), ("glob", "src/Geometry.cs"), ("context", 1))).Text);
    }

    /// <summary>
    /// A search whose hits fall in several files whose declarations moved since the view was read - edits no sync followed -
    /// has the graph check those files again in one check, never one a file, each of which would cost a whole view, and
    /// still gives every hit under its declaration as the files stand now, file by file in the listing's order, a file that
    /// did not move among them. Red with each moved file checked again at its first hit.
    /// </summary>
    [Fact]
    public async Task ASearchMeetingSeveralMovedFiles_ChecksThemAgainInOneCheck_AndGivesEachHitAsTheFilesStandNow()
    {
        using var tree = await CodebaseGraphToolTests.TreeAsync();
        var checks = new List<IReadOnlyList<string>>();
        var tool = CodebaseGraphToolTests.Tool(tree, await CodebaseGraphToolTests.QueriesAsync(tree), checks: checks);
        var graphed = Narrowing(tree, tool, []);

        foreach (var file in new[] { "Geometry.cs", "Shapes.cs" })
        {
            var full = Path.Combine(tree.Tree, "src", file);
            File.WriteAllText(full, "// formatted\n" + File.ReadAllText(full));
        }

        var found = await Call(graphed, AgentTools.SearchFiles, ("pattern", "return"), ("path", "src"));

        string Hit(string file, int line) => $"src/{file}:{line.ToString(CultureInfo.InvariantCulture)}: {File.ReadAllLines(Path.Combine(tree.Tree, "src", file))[line - 1]}";
        string Head(string qualified) => GraphLines.Of(Declared(tool.Queries, qualified));

        Assert.Equal([["src/Geometry.cs", "src/Shapes.cs"]], checks);
        Assert.True(found.Ok, found.Text);
        Assert.Equal(
            string.Join('\n', [
                Head("Demo.Geometry.Total"), "  " + Hit("Geometry.cs", 14),
                Head("Demo.Geometry.Twice"), "  " + Hit("Geometry.cs", 19),
                Head("Demo.Circle.Area"), "  " + Hit("Shapes.cs", 15),
                Head("Demo.Circle.Diameter"), "  " + Hit("Shapes.cs", 20),
                Head("Demo.Generated.Square.Area"), "  " + Hit("Shapes.g.cs", 8)]),
            found.Text);

        // The heads are the lines the files hold now: each declaration one line down.
        Assert.Equal((18, 21), (Declared(tool.Queries, "Demo.Circle.Diameter").Start, Declared(tool.Queries, "Demo.Circle.Diameter").End));
    }

    /// <summary>
    /// In a step that has a graph, a range of a file the graph parsed is headed by the declarations its lines meet - those
    /// that hold them among them, each named as the graph names a node - before the lines a step with no graph is given;
    /// a range that meets none, and a range of a file the graph does not parse, is given as it is.
    /// </summary>
    [Fact]
    public async Task ARangeViewInAGraphStep_IsHeadedByTheDeclarationsItsLinesMeet()
    {
        using var tree = await CodebaseGraphToolTests.TreeAsync();
        var queries = await CodebaseGraphToolTests.QueriesAsync(tree);
        var graphed = Narrowing(tree, CodebaseGraphToolTests.Tool(tree, queries), []);
        var plain = OverTree(tree, graph: null);

        async Task<(string Graphed, string Plain)> RangeAsync(string path, int start, int end)
        {
            var asked = new (string, object?)[] { ("path", path), ("start_line", start), ("end_line", end) };
            return ((await Call(graphed, AgentTools.ViewFile, asked)).Text, (await Call(plain, AgentTools.ViewFile, asked)).Text);
        }

        var (headed, lines) = await RangeAsync("src/Geometry.cs", 15, 19);

        Assert.Equal(
            string.Join('\n', [
                AgentToolRuntime.RangeDeclarations,
                "  " + GraphLines.Of(Declared(queries, "Demo.Geometry")),
                "  " + GraphLines.Of(Declared(queries, "Demo.Geometry.Twice")),
                lines]),
            headed);
        Assert.StartsWith("15\t\n16\t    public static double Twice(IShape shape)\n", lines, StringComparison.Ordinal);

        // The namespace a file declares is no declaration a range is headed by, and a text file the graph reads none of.
        var (outside, outsidePlain) = await RangeAsync("src/Geometry.cs", 1, 2);
        var (zoo, zooPlain) = await RangeAsync("docs/zoo.txt", 1, 1);

        Assert.Equal(outsidePlain, outside);
        Assert.Equal(zooPlain, zoo);
    }

    /// <summary>
    /// A read is matched to the graph's view by the tree that view is of, never by the tree the run works in: a run working
    /// in another tree - a copy of the view's, as a reader's own tree is of the main checkout - is given no declarations for
    /// its own copy of a file the view holds, whose lines the view says nothing of, and is given them for the view's tree's
    /// file it reads beside it. Red with a read matched to the tree the run works in.
    /// </summary>
    [Fact]
    public async Task ARead_IsMatchedToTheGraphsView_ByTheTreeTheViewIsOf_NeverTheTreeTheRunWorksIn()
    {
        using var tree = await CodebaseGraphToolTests.TreeAsync();
        using var other = new TempDirectory();
        var queries = await CodebaseGraphToolTests.QueriesAsync(tree);
        var tool = CodebaseGraphToolTests.Tool(tree, queries);
        var geometry = Path.Combine(tree.Tree, "src", "Geometry.cs");
        other.WriteFile(Path.Combine("src", "Geometry.cs"), File.ReadAllText(geometry));
        var graphed = OverTree(tree, new AgentGraphSupport(tool, _ => { }, Operations: CodebaseGraphTool.Operations(write: true, narrow: true), Narrow: true), workingIn: other.Path);

        var own = await Call(graphed, AgentTools.ViewFile, ("path", "src/Geometry.cs"), ("start_line", 15), ("end_line", 19));
        var beside = await Call(graphed, AgentTools.ViewFile, ("path", geometry), ("start_line", 15), ("end_line", 19));

        Assert.Equal(("src/Geometry.cs", (string?)null), (tool.PathOf(geometry), tool.PathOf(other.Combine("src", "Geometry.cs"))));
        Assert.True(own.Ok, own.Text);
        Assert.StartsWith("15\t\n16\t    public static double Twice(IShape shape)\n", own.Text, StringComparison.Ordinal);
        Assert.Equal(
            string.Join('\n', [
                AgentToolRuntime.RangeDeclarations,
                "  " + GraphLines.Of(Declared(queries, "Demo.Geometry")),
                "  " + GraphLines.Of(Declared(queries, "Demo.Geometry.Twice")),
                own.Text]),
            beside.Text);
    }

    /// <summary>
    /// A range whose head of declarations would take the page past its room gives its lines only as far as the head and
    /// they fit, the first always given, and says where to ask again - its head naming exactly the declarations the lines
    /// given meet - so no answer passes a page however many declarations a range meets.
    /// </summary>
    [Fact]
    public async Task ARangeHeadedByManyDeclarations_IsHeldWithinAPage_TheRestAskedForAgain()
    {
        using var tree = await CodebaseGraphToolTests.TreeAsync(("src/Many.cs", ManyMembers(800)));
        var queries = await CodebaseGraphToolTests.QueriesAsync(tree);
        var graphed = Narrowing(tree, CodebaseGraphToolTests.Tool(tree, queries), []);
        var plain = OverTree(tree, graph: null);

        var headed = (await Call(graphed, AgentTools.ViewFile, ("path", "src/Many.cs"), ("start_line", 1))).Text;
        var rest = headed.Split('\n')[^1];
        var match = System.Text.RegularExpressions.Regex.Match(rest, @"^\[lines 1-(?<last>\d+) of 805; ask again with start_line (?<next>\d+)\]$");

        Assert.True(match.Success, rest);

        var last = int.Parse(match.Groups["last"].Value, CultureInfo.InvariantCulture);
        var met = queries.OutlineFile("src/Many.cs")!.Where(card => CodeNodeKinds.Declarations.Contains(card.Kind) && card.Start <= last).ToList();
        var given = (await Call(plain, AgentTools.ViewFile, ("path", "src/Many.cs"), ("start_line", 1), ("end_line", last))).Text;

        Assert.Equal(last + 1, int.Parse(match.Groups["next"].Value, CultureInfo.InvariantCulture));
        Assert.InRange(last, 2, 804);
        Assert.InRange(headed.Length, 1, PageRoom.ModelAnswer);
        Assert.Equal(string.Join('\n', [AgentToolRuntime.RangeDeclarations, .. met.Select(card => "  " + GraphLines.Of(card)), given, rest]), headed);
    }

    /// <summary>
    /// A range of one line that 800 declarations begin on - a file written on one line, as a bundled one is - has a head
    /// that alone passes a page: it is charged to the room on the first line too, and never sent over it - the head and the
    /// line are the call's answer, kept whole and given a page at a time by read_result, each page within a page's room,
    /// a page that begins among the declarations giving the head's line again, said as going on - and the pages together
    /// hold the head's line, every declaration once and in order, and the line. Red with the first line given whatever its
    /// head, which sends the whole as one page several times a page's room.
    /// </summary>
    [Fact]
    public async Task ARangeWhoseHeadAlonePassesAPage_IsPagedByReadResult_NeverSentOverTheRoom()
    {
        using var tree = await CodebaseGraphToolTests.TreeAsync(("src/Bundle.cs", OneLineMembers(800)));
        var queries = await CodebaseGraphToolTests.QueriesAsync(tree);
        var graphed = Narrowing(tree, CodebaseGraphToolTests.Tool(tree, queries), []);
        var plain = OverTree(tree, graph: null);
        var met = queries.OutlineFile("src/Bundle.cs")!.Where(card => CodeNodeKinds.Declarations.Contains(card.Kind)).ToList();
        var line = (await Call(plain, AgentTools.ViewFile, ("path", "src/Bundle.cs"), ("start_line", 1), ("end_line", 1))).Text;
        List<string> expected = [AgentToolRuntime.RangeDeclarations, .. met.Select(card => "  " + GraphLines.Of(card)), line];

        var found = await Call(graphed, AgentTools.ViewFile, "bundle", ("path", "src/Bundle.cs"), ("start_line", 1), ("end_line", 1));

        // Every declaration begins on the line, and their head alone is more than a page.
        Assert.Equal(801, met.Count);
        Assert.All(met, card => Assert.Equal(1, card.Start));
        Assert.InRange(met.Sum(card => GraphLines.Of(card).Length + 3), PageRoom.ModelAnswer + 1, int.MaxValue);
        Assert.True(found.Ok, found.Text);
        Assert.Equal(AgentCallRecords.ResultFile(_step, "bundle"), found.File);
        Assert.Equal(string.Join('\n', expected), File.ReadAllText(found.File!));

        var first = found.Text.Split('\n');
        var pages = int.Parse(first[0].Split(' ')[3], CultureInfo.InvariantCulture);
        List<string> given = [.. first.Skip(1)];

        Assert.Equal($"page 1 of {pages.ToString(CultureInfo.InvariantCulture)} · {expected.Count.ToString(CultureInfo.InvariantCulture)} results · ask read_result with id: 'bundle', page: 2 for the next · the whole is '{found.File}'", first[0]);
        Assert.InRange(pages, 2, int.MaxValue);
        Assert.InRange(found.Text.Length, 1, PageRoom.ModelAnswer);

        for (var page = 2; page <= pages; page++)
        {
            var next = (await Call(graphed, AgentTools.ReadResult, $"bundle_{page.ToString(CultureInfo.InvariantCulture)}", ("id", "bundle"), ("page", page))).Text;
            var lines = next.Split('\n').Skip(1).ToList();

            Assert.InRange(next.Length, 1, PageRoom.ModelAnswer);

            // A page that begins among the declarations begins with the head's line again, said as going on.
            if (expected[given.Count].StartsWith("  ", StringComparison.Ordinal))
            {
                Assert.Equal($"{AgentToolRuntime.RangeDeclarations} … goes on", lines[0]);
                lines.RemoveAt(0);
            }

            given.AddRange(lines);
        }

        Assert.Equal(expected, given);
    }

    /// <summary>
    /// A search whose grouped answer passes a page is kept whole and given a page at a time by read_result, as every search
    /// is: each page begins with 'page k of n', each holds whole lines, a page that begins at a hit beneath a declaration
    /// begins with the declaration's line again, said as going on, and one that begins at a declaration's line begins with
    /// it alone - and the pages together hold every declaration's line and every hit beneath it once and in order, nothing
    /// dropped.
    /// </summary>
    [Fact]
    public async Task AGroupedSearchLongerThanAPage_IsGivenWholeByPage_EveryHeaderAndHitOnce()
    {
        using var tree = await CodebaseGraphToolTests.TreeAsync(("src/Many.cs", ManyMembers(1600, varied: true)));
        var queries = await CodebaseGraphToolTests.QueriesAsync(tree);
        var graphed = Narrowing(tree, CodebaseGraphToolTests.Tool(tree, queries), []);
        var text = File.ReadAllLines(Path.Combine(tree.Tree, "src", "Many.cs"));
        var members = queries.OutlineFile("src/Many.cs")!.Where(card => card.Kind == CodeNodeKinds.Member).ToList();
        List<string> expected = [.. members.SelectMany(card => new[] { GraphLines.Of(card), $"  src/Many.cs:{card.Start.ToString(CultureInfo.InvariantCulture)}: {text[card.Start - 1]}" })];

        var found = await Call(graphed, AgentTools.SearchFiles, "grouped", ("pattern", "=> "), ("glob", "src/Many.cs"));

        Assert.Equal(1600, members.Count);
        Assert.Equal(AgentCallRecords.ResultFile(_step, "grouped"), found.File);
        Assert.Equal(string.Join('\n', expected), File.ReadAllText(found.File!));

        var header = found.Text.Split('\n')[0];
        var pages = int.Parse(header.Split(' ')[3], CultureInfo.InvariantCulture);
        var given = found.Text.Split('\n').Skip(1).ToList();
        var inside = new List<bool>();

        Assert.Equal($"page 1 of {pages.ToString(CultureInfo.InvariantCulture)} · {expected.Count.ToString(CultureInfo.InvariantCulture)} results · ask read_result with id: 'grouped', page: 2 for the next · the whole is '{found.File}'", header);
        Assert.InRange(pages, 2, int.MaxValue);

        for (var page = 2; page <= pages; page++)
        {
            var next = (await Call(graphed, AgentTools.ReadResult, $"read_{page.ToString(CultureInfo.InvariantCulture)}", ("id", "grouped"), ("page", page))).Text.Split('\n');
            var lines = next.Skip(1).ToList();

            // Each declaration holds one hit: a page that begins at a hit begins inside the group the page before ended with
            // the line of, and gives that line again first.
            inside.Add(expected[given.Count].StartsWith("  ", StringComparison.Ordinal));

            if (inside[^1])
            {
                Assert.Equal($"{expected[given.Count - 1]} … goes on", lines[0]);
                lines.RemoveAt(0);
            }

            Assert.StartsWith($"page {page.ToString(CultureInfo.InvariantCulture)} of {pages.ToString(CultureInfo.InvariantCulture)} · ", next[0], StringComparison.Ordinal);
            Assert.InRange(next.Sum(line => line.Length + 1) - 1, 1, PageRoom.ModelAnswer);
            given.AddRange(lines);
        }

        Assert.Equal(expected, given);

        // Pages begin both ways: at a hit beneath a declaration, and at a declaration's line.
        Assert.Contains(true, inside);
        Assert.Contains(false, inside);
    }

    /// <summary>
    /// A search whose hits beneath one declaration pass a page gives each page after the first the declaration's line
    /// again, exactly, said as going on before the hits it goes on with - counted as no result, each page within a page of
    /// an answer - and every hit once and in order across the pages; the same search in a step with no graph gives its hits
    /// as they are, page by page, with no declaration's line and no blocks kept beside its result.
    /// </summary>
    [Fact]
    public async Task AGroupedSearchWhoseHitsPassAPage_GivesTheirDeclarationsLineAgainOnEveryLaterPage()
    {
        using var tree = await CodebaseGraphToolTests.TreeAsync(("src/Long.cs", LongMember(1500)));
        var queries = await CodebaseGraphToolTests.QueriesAsync(tree);
        var graphed = Narrowing(tree, CodebaseGraphToolTests.Tool(tree, queries), []);
        var plain = OverTree(tree, graph: null);
        var text = File.ReadAllLines(Path.Combine(tree.Tree, "src", "Long.cs"));
        var sum = Declared(queries, "Demo.Long.Sum");
        var hits = Enumerable.Range(0, text.Length).Where(at => text[at].Contains("// tally", StringComparison.Ordinal)).Select(at => $"src/Long.cs:{(at + 1).ToString(CultureInfo.InvariantCulture)}: {text[at]}").ToList();

        async Task<List<string[]>> PagesAsync(AgentToolRuntime runtime, string id)
        {
            var first = await Call(runtime, AgentTools.SearchFiles, id, ("pattern", "tally"), ("glob", "src/Long.cs"));
            var pages = new List<string[]> { first.Text.Split('\n') };
            var count = int.Parse(pages[0][0].Split(' ')[3], CultureInfo.InvariantCulture);

            Assert.Equal(AgentCallRecords.ResultFile(_step, id), first.File);

            for (var page = 2; page <= count; page++)
            {
                pages.Add((await Call(runtime, AgentTools.ReadResult, $"{id}_{page.ToString(CultureInfo.InvariantCulture)}", ("id", id), ("page", page))).Text.Split('\n'));
            }

            return pages;
        }

        var grouped = await PagesAsync(graphed, "long");
        var again = $"{GraphLines.Of(sum)} … goes on";

        Assert.Equal(1500, hits.Count);
        Assert.InRange(grouped.Count, 3, int.MaxValue);
        Assert.StartsWith($"page 1 of {grouped.Count.ToString(CultureInfo.InvariantCulture)} · 1501 results · ", grouped[0][0], StringComparison.Ordinal);
        Assert.Equal(GraphLines.Of(sum), grouped[0][1]);
        Assert.All(grouped.Skip(1), page => Assert.Equal(again, page[1]));
        Assert.All(grouped, page => Assert.InRange(page.Sum(line => line.Length + 1) - 1, 1, PageRoom.ModelAnswer));
        Assert.Equal([GraphLines.Of(sum), .. hits.Select(hit => "  " + hit)], grouped.SelectMany((page, at) => page.Skip(at == 0 ? 1 : 2)));
        Assert.True(File.Exists(TextBlocks.FileOf(AgentCallRecords.ResultFile(_step, "long"))));

        var ungrouped = await PagesAsync(plain, "long_plain");

        Assert.StartsWith($"page 1 of {ungrouped.Count.ToString(CultureInfo.InvariantCulture)} · 1500 results · ", ungrouped[0][0], StringComparison.Ordinal);
        Assert.Equal(hits, ungrouped.SelectMany(page => page.Skip(1)));
        Assert.False(File.Exists(TextBlocks.FileOf(AgentCallRecords.ResultFile(_step, "long_plain"))));
    }

    /// <summary>
    /// A file viewed is laid as its lines alone, whatever blocks are kept beside it: a view's page that begins among lines
    /// a blocks file names as a block's gives no head again - the rule is a call's result's, never a file's.
    /// </summary>
    [Fact]
    public async Task AViewOfAFile_GivesNoHeadAgain_WhateverBlocksAreKeptBesideIt()
    {
        var lines = Enumerable.Range(1, 1000).Select(at => $"  indented line {at.ToString("D4", CultureInfo.InvariantCulture)} " + new string('i', 30)).ToList();
        Write("notes/indented.txt", string.Empty);
        TextPagesTests.WriteBlocked(Path.Combine(_worktree, "notes", "indented.txt"), [("the head", 'h'), .. lines.Select(line => (line, 'b'))]);

        var runtime = Open();
        var first = (await Call(runtime, AgentTools.ViewFile, ("path", "notes/indented.txt"))).Text.Split('\n');
        var second = (await Call(runtime, AgentTools.ViewFile, ("path", "notes/indented.txt"), ("page", 2))).Text.Split('\n');
        var shown = first.Length - 1;

        Assert.Equal("page 2 of 2 · the last", second[0]);
        Assert.Equal($"{(shown + 1).ToString(CultureInfo.InvariantCulture)}\t{lines[shown - 1]}", second[1]);
        Assert.Equal(1001, shown + second.Length - 1);
    }

    /// <summary>
    /// A step that has a graph is told, in view_file's and search_files' own descriptions, how the graph is in their
    /// answers - a range headed by its declarations, a search's hits under theirs, and where its reads are narrowed a whole
    /// read answered with the file's outline - and a step with no graph, or whose graph was never built, is told nothing of it.
    /// </summary>
    [Fact]
    public async Task TheReadsOfAGraphStep_SayHowTheGraphIsInTheirAnswers()
    {
        using var tree = await CodebaseGraphToolTests.TreeAsync();
        var tool = CodebaseGraphToolTests.Tool(tree, await CodebaseGraphToolTests.QueriesAsync(tree));
        var table = AgentTools.All(AgentCommandSettings.Default).ToDictionary(definition => definition.Name, definition => definition.Description, StringComparer.Ordinal);

        string Said(AgentToolRuntime runtime, string name) => runtime.Tools.Single(definition => definition.Name == name).Description;

        var narrowed = Narrowing(tree, tool, []);
        var unnarrowed = OverTree(tree, new AgentGraphSupport(tool));
        var unbuilt = OverTree(tree, new AgentGraphSupport(null));
        var plain = OverTree(tree, graph: null);

        Assert.Equal(
            $"{table[AgentTools.ViewFile]} {AgentTools.ViewFileInAGraphStep} {AgentTools.ViewFileNarrowed}",
            Said(narrowed, AgentTools.ViewFile));
        Assert.Equal($"{table[AgentTools.ViewFile]} {AgentTools.ViewFileInAGraphStep}", Said(unnarrowed, AgentTools.ViewFile));
        Assert.Equal($"{table[AgentTools.SearchFiles]} {AgentTools.SearchFilesInAGraphStep}", Said(narrowed, AgentTools.SearchFiles));
        Assert.Equal($"{table[AgentTools.SearchFiles]} {AgentTools.SearchFilesInAGraphStep}", Said(unnarrowed, AgentTools.SearchFiles));

        foreach (var runtime in new[] { unbuilt, plain })
        {
            Assert.All(runtime.Tools.Where(definition => table.ContainsKey(definition.Name)), definition => Assert.Equal(table[definition.Name], definition.Description));
        }

        Assert.All(
            narrowed.Tools.Where(definition => definition.Name is not (AgentTools.ViewFile or AgentTools.SearchFiles) && table.ContainsKey(definition.Name)),
            definition => Assert.Equal(table[definition.Name], definition.Description));
    }

    /// <summary>
    /// The architecture says where the graph is in the reads by the owners that put it there - the tool's declarations of a
    /// file, the search and the range that use them, the outline a narrowed read is answered with and the line that records
    /// it - so a rename of any of them breaks this.
    /// </summary>
    [Fact]
    public void TheArchitecture_SaysWhereTheGraphIsInTheReads()
    {
        var architecture = Prose.Document("docs", "architecture.md");

        Assert.Contains(
            $"The tool owns what those reads are told of a file, `{nameof(CodebaseGraphTool)}.{nameof(CodebaseGraphTool.DeclarationsAsync)}`: the file's declarations (`{nameof(GraphFileDeclarations)}`) where the view holds it as parsed and the step's reads reach it",
            architecture,
            StringComparison.Ordinal);
        Assert.Contains(
            $"the innermost a type, a member or a section that holds the hit's line (`{nameof(GraphFileDeclarations)}.{nameof(GraphFileDeclarations.Holding)}`, by `{nameof(CodebaseGraphQueries)}.{nameof(CodebaseGraphQueries.AtLine)}`; a namespace or a project heads no hit)",
            architecture,
            StringComparison.Ordinal);
        Assert.Contains(
            $"A range of `view_file` (`{nameof(AgentToolRuntime)}.RangeAsync`) is headed (`{nameof(AgentToolRuntime)}.{nameof(AgentToolRuntime.RangeDeclarations)}`) by the declarations the lines it gives meet (`{nameof(GraphFileDeclarations)}.{nameof(GraphFileDeclarations.Meeting)}`)",
            architecture,
            StringComparison.Ordinal);
        Assert.Contains(
            $"declarations at its first hit, as its lines confirm them alone (`{nameof(CodebaseGraphTool)}.{nameof(CodebaseGraphTool.ConfirmedDeclarationsAsync)}`) - hits come file by file, each file's in order - and where a file's moved, writes nothing more and only finds the rest: every moved file of the call is checked again in one check (`{nameof(CodebaseGraphTool)}.{nameof(CodebaseGraphTool.ReadAgainAsync)}`)",
            architecture,
            StringComparison.Ordinal);
        Assert.Contains($"`{nameof(AgentTools)}.{nameof(AgentTools.InAGraphStep)}` says so in the two tools' own descriptions where the step's graph was built.", architecture, StringComparison.Ordinal);
        Assert.Contains(
            $"over `{nameof(CodebaseGraphTool)}.{nameof(CodebaseGraphTool.NarrowLines)}` lines with the file's outline in its place (`{nameof(CodebaseGraphTool)}.{nameof(CodebaseGraphTool.WholeReadAsync)}`)",
            architecture,
            StringComparison.Ordinal);
        Assert.Contains(
            $"each whole read the narrowing answered with the file's outline (`{nameof(GraphEscapeLine)}`, `{nameof(GraphOutlinedLine)}`: the file, its lines, what reading it whole would have cost and what the outline cost) - a line an earlier build wrote of a read the narrowing refused (`{nameof(GraphDeniedLine)}`) still read, and counted in no figure",
            architecture,
            StringComparison.Ordinal);
        Assert.Contains("the outlined reads (`outlinedReads`: how many, `tokens` what reading them whole would have cost, `outlineTokens` what the outlines given in their place cost)", architecture, StringComparison.Ordinal);
    }

    /// <summary>The node the view holds by <paramref name="qualified"/>, its one declaration.</summary>
    private static GraphCard Declared(CodebaseGraphQueries view, string qualified) => Assert.IsType<GraphResolve.Found>(view.Resolve(qualified)).Card;

    /// <summary>A C# class whose one member, Sum, holds <paramref name="count"/> lines that each say 'tally': a declaration of many hits.</summary>
    private static string LongMember(int count)
        => "namespace Demo;\n\npublic static class Long\n{\n    public static int Sum()\n    {\n        var total = 0;\n"
            + string.Concat(Enumerable.Range(1, count).Select(at => $"        total += 1; // tally {at.ToString("D4", CultureInfo.InvariantCulture)}\n"))
            + "        return total;\n    }\n}\n";

    /// <summary>A C# class of <paramref name="count"/> members written whole on its file's one line, as a bundled file is: every declaration begins on line 1.</summary>
    private static string OneLineMembers(int count)
        => "namespace Demo; public static class Bundle { "
            + string.Concat(Enumerable.Range(0, count).Select(at => $"public static int M{at.ToString("D3", CultureInfo.InvariantCulture)}()=>{at.ToString(CultureInfo.InvariantCulture)}; "))
            + "}\n";

    /// <summary>
    /// A C# class of <paramref name="count"/> one-line members, each its own line: a file of many declarations,
    /// <paramref name="count"/> and five lines - their names, where <paramref name="varied"/>, of lengths that vary, so the
    /// pages of a search of them end after either line of a member's.
    /// </summary>
    private static string ManyMembers(int count, bool varied = false)
        => "namespace Demo;\n\npublic static class Many\n{\n"
            + string.Concat(Enumerable.Range(0, count).Select(at => $"    public static int M{at.ToString("D3", CultureInfo.InvariantCulture)}{(varied ? new string('v', at % 13) : string.Empty)}() => {at.ToString(CultureInfo.InvariantCulture)};\n"))
            + "}\n";
}
