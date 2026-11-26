using System.Text.Json.Nodes;
using DssHarness.Core.Agents.Runtime;
using DssHarness.Core.Codebase;
using DssHarness.Core.Codebase.Languages;
using DssHarness.Core.Help;
using DssHarness.Core.Hosts;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;

namespace DssHarness.Tests;

/// <summary>
/// A run's graph read by hand: the service's read-only open - the base and an agent's view as they stand on disk, no
/// sync waited on, nothing written - and <c>query-codebase-graph</c> over it, in its text and its document, on every exit.
/// </summary>
public sealed class CodebaseGraphQueryServiceTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// The read-only open answers the base as its records stand, by a process that holds nothing of it, while a sync of it
    /// is under way - it waits on no gate - and a link result that does not match is linked again in memory and never written.
    /// </summary>
    [Fact]
    public async Task TheReadOnlyOpen_AnswersTheBaseFromDisk_WaitingOnNoSync()
    {
        using var tree = await CodebaseGraphTree.CreateAsync();
        tree.Write("a.toy", "class A\n");
        await tree.CommitAsync();
        await tree.SyncAsync();
        tree.Restarted();
        var stale = File.ReadAllBytes(tree.Run.GraphSymbolsFile);

        tree.Write("b.toy", "class B\nhold\n");
        await tree.CommitAsync();
        tree.Toy.Gate = new ManualResetEventSlim(false);
        var syncing = tree.SyncAsync();

        try
        {
            Assert.True(tree.Toy.Holding.Wait(TimeSpan.FromSeconds(30), Token), "the sync never reached the file it holds on");

            var read = await tree.ReadOnlyAsync().WaitAsync(TimeSpan.FromSeconds(30), Token);
            var queries = new CodebaseGraphQueries(read!);

            Assert.Equal((CodebaseGraphService.BaseView, tree.Tree), (read!.View, read.Tree));
            Assert.IsType<GraphResolve.Found>(queries.Resolve("A"));
            Assert.IsType<GraphResolve.NotFound>(queries.Resolve("B"));
        }
        finally
        {
            tree.Toy.Gate.Set();
            await syncing;
        }

        // A link result of another manifest is linked again in memory, never read as this one's, and never written.
        var current = File.ReadAllBytes(tree.Run.GraphSymbolsFile);
        File.WriteAllBytes(tree.Run.GraphSymbolsFile, stale);
        var restored = new CodebaseGraphQueries((await tree.Restarted().ReadOnlyAsync())!);

        Assert.IsType<GraphResolve.Found>(restored.Resolve("B"));
        Assert.Equal(stale, File.ReadAllBytes(tree.Run.GraphSymbolsFile));
        Assert.NotEqual(stale, current);

        File.Delete(tree.Run.GraphSymbolsFile);

        var linked = new CodebaseGraphQueries((await tree.Restarted().ReadOnlyAsync())!);

        Assert.IsType<GraphResolve.Found>(linked.Resolve("B"));
        Assert.False(File.Exists(tree.Run.GraphSymbolsFile));
    }

    /// <summary>
    /// The read-only open answers an agent's view - the overlay of its worktree over the base, linked in memory, since its
    /// link result is on no disk - and null where the view was not made, or the run has no graph.
    /// </summary>
    [Fact]
    public async Task TheReadOnlyOpen_AnswersAnAgentsView_LinkedInMemory_AndNullWhereNoneIsMade()
    {
        using var tree = await CodebaseGraphTree.CreateAsync();
        Assert.Null(await tree.ReadOnlyAsync());

        tree.Write("a.toy", "class A\n");
        await tree.CommitAsync();
        await tree.SyncAsync();
        var worktree = await tree.WorktreeAsync("ann");
        CodebaseGraphTree.WriteIn(worktree, "x.toy", "class X\ncall A\n");
        await tree.SyncViewAsync("ann", worktree);
        tree.Restarted();

        var view = await tree.ReadOnlyAsync("ann");
        var queries = new CodebaseGraphQueries(view!);
        var x = ((GraphResolve.Found)queries.Resolve("X")).Card;

        Assert.Equal(("ann", worktree), (view!.View, view.Tree));
        Assert.Equal(["A"], queries.Edges(x.Id).References.Select(edge => edge.Card.Name));
        Assert.IsType<GraphResolve.NotFound>(new CodebaseGraphQueries((await tree.ReadOnlyAsync())!).Resolve("X"));
        Assert.Null(await tree.ReadOnlyAsync("bob"));

        // A view held against a base no longer kept reads as none until its next whole sync, as a sync reads it.
        var record = File.ReadAllText(tree.Run.GraphViewFile("ann"));
        File.WriteAllText(tree.Run.GraphViewFile("ann"), record.Replace(tree.Manifest().Digest, new string('a', 64), StringComparison.Ordinal));

        var stale = await Assert.ThrowsAsync<HarnessException>(() => tree.Restarted().ReadOnlyAsync("ann"));

        Assert.Equal(HarnessExit.Refused, stale.ExitCode);
        Assert.Equal("the codebase graph's view 'ann' of run 'run' stands on a base the graph no longer keeps; its next whole sync makes it again against the base as it stands", stale.Message);
    }

    /// <summary>
    /// <c>query-codebase-graph</c> answers a read operation with the answer an agent is given - and nothing more - and its
    /// document with the run, the view, the commit, the operation and that answer; it writes nothing of the graph.
    /// </summary>
    [Fact]
    public async Task TheVerb_AnswersWhatAnAgentIsGiven_InItsTextAndItsDocument_WritingNothing()
    {
        using var tree = await InitialisedTreeAsync();
        var service = Service(tree);
        var before = Snapshot(tree);

        var text = await service.QueryAsync(tree.Tree, new CodebaseGraphQuery("run", null) { Find = "Twice" }, json: false, Token);
        var json = await service.QueryAsync(tree.Tree, new CodebaseGraphQuery("run", null) { Node = "Total" }, json: true, Token);
        var document = JsonNode.Parse(Assert.Single(json.Data))!.AsObject();

        Assert.Equal((HarnessExit.Success, true), (text.ExitCode, text.Quiet));
        Assert.Equal("8257a8 member public static double Twice(IShape shape) | src/Geometry.cs:16-19 #aac0401c | 0 caller(s), 1 callee(s)", Assert.Single(text.Data));
        Assert.Equal(["exitCode", "summary", "run", "view", "head", "graph", "op", "answer", "page", "pages", "total", "entries"], document.Select(member => member.Key));
        Assert.Equal((0, "run", "base", await tree.HeadAsync(), new CodebaseGraphQueries((await tree.ReadOnlyAsync())!).Graph, "node"), (document["exitCode"]!.GetValue<int>(), document["run"]!.GetValue<string>(), document["view"]!.GetValue<string>(), document["head"]!.GetValue<string>(), document["graph"]!.GetValue<string>(), document["op"]!.GetValue<string>()));
        Assert.StartsWith("bc11b2 member public static double Total(IShape[] shapes)", document["answer"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.DoesNotContain(HarnessLayout.OrchestratorsDirectoryName, document["answer"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(before, Snapshot(tree));
    }

    /// <summary>
    /// <c>--outline</c> outlines a file where the view holds one at what it is given, a directory where it holds files under
    /// it and no node is named so, a symbol otherwise, and the repository with nothing.
    /// </summary>
    [Theory]
    [InlineData("src/Shapes.cs", "outline of src/Shapes.cs (6 declaration(s)):")]
    [InlineData("tests", "outline of tests/ (1 file(s)):\ntests/ (1 file(s)):\naef0d1 file tests/GeometryTests.cs | tests/GeometryTests.cs:1-10 | csharp | test\n")]
    [InlineData("Circle", "51b2d4 type public sealed class Circle(double radius) : IShape | src/Shapes.cs:10-21 #fd46243a | A circle.")]
    [InlineData(null, "base at ")]
    public async Task TheVerbsOutline_TakesAFileADirectoryASymbolOrNothing(string? of, string first)
    {
        using var tree = await InitialisedTreeAsync();

        var outcome = await Service(tree).QueryAsync(tree.Tree, new CodebaseGraphQuery("run", null) { Outline = true, OutlineOf = of }, json: false, Token);

        Assert.Equal(HarnessExit.Success, outcome.ExitCode);
        Assert.StartsWith(first, Assert.Single(outcome.Data), StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>--around</c> and <c>--impact</c> run the operations by hand, in the service and on the command line: the answer an
    /// agent is given, and the document naming the operation.
    /// </summary>
    [Fact]
    public async Task TheVerbsAroundAndImpact_AnswerWhatAnAgentIsGiven()
    {
        using var tree = await InitialisedTreeAsync();
        var service = Service(tree);

        var around = await service.QueryAsync(tree.Tree, new CodebaseGraphQuery("run", null) { Around = "Twice" }, json: true, Token);
        var impact = await service.QueryAsync(tree.Tree, new CodebaseGraphQuery("run", null) { Impact = "Total" }, json: false, Token);
        var aroundDocument = JsonNode.Parse(Assert.Single(around.Data))!.AsObject();
        var cli = await CliRunner.RunAsync(["query-codebase-graph", "run", "--impact", "Total", "-C", tree.Tree], Token);
        var both = await service.QueryAsync(tree.Tree, new CodebaseGraphQuery("run", null) { Around = "Twice", Impact = "Total" }, json: false, Token);

        Assert.Equal((HarnessExit.Success, "around"), (around.ExitCode, aroundDocument["op"]!.GetValue<string>()));
        Assert.StartsWith(
            "8257a8 member public static double Twice(IShape shape) | src/Geometry.cs:16-19 #aac0401c | 0 caller(s), 1 callee(s)\ndeclared in:\n",
            aroundDocument["answer"]!.GetValue<string>(),
            StringComparison.Ordinal);
        Assert.StartsWith(
            "bc11b2 member public static double Total(IShape[] shapes) | src/Geometry.cs:5-14 #5b03176a | 2 caller(s), 3 callee(s)\n"
            + "callers (1):\n  1: 8257a8 member public static double Twice(IShape shape) | src/Geometry.cs:16-19 #aac0401c | 0 caller(s), 1 callee(s) | calls Total (bc11b2)\n"
            + "tests (1):\n  1: 8ee778 member public void TotalAddsAreas() | tests/GeometryTests.cs:5-9 #482b035f | 0 caller(s), 1 callee(s) | tests Total (bc11b2)\n",
            Assert.Single(impact.Data),
            StringComparison.Ordinal);
        Assert.Equal(0, cli.ExitCode);
        Assert.Equal(Assert.Single(impact.Data), cli.StandardOutput.TrimEnd('\n', '\r').ReplaceLineEndings("\n"));
        Assert.Equal((HarnessExit.UsageError, "give exactly one of --find, --node, --related, --outline, --around, --impact, --body or --sites"), (both.ExitCode, both.Message));
    }

    /// <summary>
    /// <c>--sites</c> runs the operation by hand: the answer an agent is given, and its document naming the operation; a quoted
    /// data whose value is white space alone is refused before anything is read, as the tool refuses it, never thrown at the
    /// lookups.
    /// </summary>
    [Fact]
    public async Task TheVerbsSites_AnswerWhatAnAgentIsGiven()
    {
        using var tree = await InitialisedTreeAsync();
        var outcome = await Service(tree).QueryAsync(tree.Tree, new CodebaseGraphQuery("run", null) { Sites = "Area" }, json: true, Token);
        var document = JsonNode.Parse(Assert.Single(outcome.Data))!.AsObject();
        var cli = await CliRunner.RunAsync(["query-codebase-graph", "run", "--sites", "Area", "-C", tree.Tree], Token);
        var quotedBlank = await Service(tree).QueryAsync(tree.Tree, new CodebaseGraphQuery("run", null) { Sites = "\" \"" }, json: false, Token);

        Assert.Equal(HarnessExit.Success, outcome.ExitCode);
        Assert.Equal("sites", document["op"]!.GetValue<string>());
        Assert.StartsWith("every place that decides 'Area':\ncsharp in directory src/ (4):\n", document["answer"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal((HarnessExit.UsageError, $"data is empty: {CodebaseGraphToolTests.DataDescription}"), (quotedBlank.ExitCode, quotedBlank.Message));
        Assert.Equal(HarnessExit.Success, cli.ExitCode);
        Assert.Contains("every place that decides 'Area':", cli.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("coverage: nothing is assumed of a language with no extractor", cli.StandardOutput, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>--page</c> asks any page of the answer, the text carrying the page's header - an answer of one page has none; a
    /// page past the last, and a page before the first, are usage errors in text and in the document; and the document
    /// answers the page, the pages, the whole answer's total, the digest of the graph each page was counted on, and the
    /// page's entries, each with what it counts, so the entries of every page summed are the total.
    /// </summary>
    [Fact]
    public async Task TheVerbsPage_AsksAnyPage_AndItsDocumentAnswersThePages()
    {
        using var tree = await InitialisedTreeAsync(("src/Hub.cs", CodebaseGraphToolTests.HubFile(65)));
        var service = Service(tree);
        var graph = new CodebaseGraphQueries((await tree.ReadOnlyAsync())!).Graph;

        var outline = await service.QueryAsync(tree.Tree, new CodebaseGraphQuery("run", null) { Outline = true, OutlineOf = "src/Shapes.cs" }, json: false, Token);
        var text = await service.QueryAsync(tree.Tree, new CodebaseGraphQuery("run", null) { Find = "M" }, json: false, Token);
        var first = await service.QueryAsync(tree.Tree, new CodebaseGraphQuery("run", null) { Find = "M", Page = 1 }, json: true, Token);
        var second = await CliRunner.RunAsync(["query-codebase-graph", "run", "--find", "M", "--page", "2", "--json", "-C", tree.Tree], Token);
        var third = await service.QueryAsync(tree.Tree, new CodebaseGraphQuery("run", null) { Find = "M", Page = 3 }, json: true, Token);
        var fourth = await service.QueryAsync(tree.Tree, new CodebaseGraphQuery("run", null) { Find = "M", Page = 4 }, json: true, Token);
        var fifth = await service.QueryAsync(tree.Tree, new CodebaseGraphQuery("run", null) { Find = "M", Page = 5 }, json: true, Token);
        var past = await service.QueryAsync(tree.Tree, new CodebaseGraphQuery("run", null) { Find = "M", Page = 6 }, json: true, Token);
        var before = await CliRunner.RunAsync(["query-codebase-graph", "run", "--find", "M", "--page", "0", "-C", tree.Tree], Token);
        var pages = new[] { JsonNode.Parse(Assert.Single(first.Data))!.AsObject(), JsonNode.Parse(second.StandardOutput)!.AsObject(), JsonNode.Parse(Assert.Single(third.Data))!.AsObject(), JsonNode.Parse(Assert.Single(fourth.Data))!.AsObject(), JsonNode.Parse(Assert.Single(fifth.Data))!.AsObject() };
        var pastDocument = JsonNode.Parse(Assert.Single(past.Data))!.AsObject();

        Assert.Equal(HarnessExit.Success, outline.ExitCode);
        Assert.StartsWith("outline of src/Shapes.cs (6 declaration(s)):\n", Assert.Single(outline.Data), StringComparison.Ordinal);
        Assert.Equal("page 1 of 5 · 82 results · ask again with page: 2 for the next", Assert.Single(text.Data).Split('\n')[0]);
        Assert.Equal(Assert.Single(text.Data), pages[0]["answer"]!.GetValue<string>());
        Assert.Equal(0, second.ExitCode);
        Assert.Equal(["page 2 of 5 · 82 results · ask again with page: 3 for the next", "page 3 of 5 · 82 results · ask again with page: 4 for the next", "page 4 of 5 · 82 results · ask again with page: 5 for the next", "page 5 of 5 · the last"], pages[1..].Select(page => page["answer"]!.GetValue<string>().Split('\n')[0]));
        Assert.Equal([(1, 5, 82, graph), (2, 5, 82, graph), (3, 5, 82, graph), (4, 5, 82, graph), (5, 5, 82, graph)], pages.Select(page => (page["page"]!.GetValue<int>(), page["pages"]!.GetValue<int>(), page["total"]!.GetValue<int>(), page["graph"]!.GetValue<string>())));

        // A page's entries are its answer's lines past the header, each with what it counts; summed over the pages, the total.
        foreach (var page in pages)
        {
            var entries = page["entries"]!.AsArray().Select(entry => entry!.AsObject()).ToList();

            Assert.All(entries, entry => Assert.Equal(["text", "counts", "section", "node"], entry.Select(member => member.Key)));
            Assert.Equal(page["answer"]!.GetValue<string>().Split('\n')[1..], entries.Select(entry => entry["text"]!.GetValue<string>()));
        }

        Assert.Equal(82, pages.Sum(page => page["entries"]!.AsArray().Sum(entry => entry!["counts"]!.GetValue<int>())));

        // A body's head says something and counts nothing; each line it serves counts one, all of them the node's.
        var body = JsonNode.Parse(Assert.Single((await service.QueryAsync(tree.Tree, new CodebaseGraphQuery("run", null) { Body = ["Twice"] }, json: true, Token)).Data))!.AsObject();

        Assert.Equal(
            [(0, "csharp:Demo.Geometry.Twice(IShape)"), (1, "csharp:Demo.Geometry.Twice(IShape)"), (1, "csharp:Demo.Geometry.Twice(IShape)"), (1, "csharp:Demo.Geometry.Twice(IShape)"), (1, "csharp:Demo.Geometry.Twice(IShape)")],
            body["entries"]!.AsArray().Select(entry => (entry!["counts"]!.GetValue<int>(), entry["node"]!.GetValue<string>())));
        Assert.Equal(4, body["total"]!.GetValue<int>());
        Assert.Equal((HarnessExit.UsageError, "page 6 of 'find M': it has 5 pages (1 to 5)"), (past.ExitCode, past.Message));
        Assert.Equal((6, 5, 82, graph), (pastDocument["page"]!.GetValue<int>(), pastDocument["pages"]!.GetValue<int>(), pastDocument["total"]!.GetValue<int>(), pastDocument["graph"]!.GetValue<string>()));
        Assert.Empty(pastDocument["entries"]!.AsArray());
        Assert.Equal((HarnessExit.UsageError, $"{CodebaseGraphQueryService.CommandName}: FAIL - page is 0: pages start at 1"), (before.ExitCode, before.StandardError.Trim()));
    }

    /// <summary>An agent whose view was not made yet is said, and the base answered, in the text and in the document.</summary>
    [Fact]
    public async Task TheVerb_OfAViewNotMadeYet_SaysSo_AndAnswersTheBase()
    {
        using var tree = await InitialisedTreeAsync();

        var outcome = await Service(tree).QueryAsync(tree.Tree, new CodebaseGraphQuery("run", "ghost") { Find = "Twice" }, json: true, Token);
        var document = JsonNode.Parse(Assert.Single(outcome.Data))!.AsObject();

        Assert.Equal(HarnessExit.Success, outcome.ExitCode);
        Assert.Equal("base", document["view"]!.GetValue<string>());
        Assert.StartsWith("the view of 'ghost' is not made yet; the base is answered. 8257a8 member public static double Twice", document["answer"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    /// <summary>
    /// A run with no graph is refused (13), and a query that is no one read operation, a related walk with no relation and
    /// a misused operation are usage errors (10) - each with its own document, holding nothing it did not read.
    /// </summary>
    [Fact]
    public async Task TheVerb_RefusesARunWithNoGraph_AndAMisuse_EachInItsOwnDocument()
    {
        using var tree = await InitialisedTreeAsync();
        var service = Service(tree);

        var none = await service.QueryAsync(tree.Tree, new CodebaseGraphQuery("nope", null) { Find = "Twice" }, json: true, Token);
        var two = await service.QueryAsync(tree.Tree, new CodebaseGraphQuery("run", null) { Find = "Twice", Node = "Total" }, json: true, Token);
        var noRelation = await service.QueryAsync(tree.Tree, new CodebaseGraphQuery("run", null) { Related = "Total" }, json: true, Token);
        var strayDepth = await service.QueryAsync(tree.Tree, new CodebaseGraphQuery("run", null) { Node = "Total", Depth = 2 }, json: false, Token);
        var strayRelation = await service.QueryAsync(tree.Tree, new CodebaseGraphQuery("run", null) { Node = "Total", Relation = "callers" }, json: false, Token);
        var misused = await service.QueryAsync(tree.Tree, new CodebaseGraphQuery("run", null) { Body = ["Twice", " "] }, json: true, Token);
        var noneDocument = JsonNode.Parse(Assert.Single(none.Data))!.AsObject();
        var misusedDocument = JsonNode.Parse(Assert.Single(misused.Data))!.AsObject();

        Assert.Equal(HarnessExit.Refused, none.ExitCode);
        Assert.StartsWith("run 'nope' has no codebase graph in '", none.Message, StringComparison.Ordinal);
        Assert.Equal(["exitCode", "summary", "run", "view", "head", "graph", "op", "answer", "page", "pages", "total", "entries"], noneDocument.Select(member => member.Key));
        Assert.Equal((13, "nope", "find"), (noneDocument["exitCode"]!.GetValue<int>(), noneDocument["run"]!.GetValue<string>(), noneDocument["op"]!.GetValue<string>()));
        Assert.Null(noneDocument["view"]);
        Assert.Null(noneDocument["answer"]);
        Assert.Equal((HarnessExit.UsageError, "give exactly one of --find, --node, --related, --outline, --around, --impact, --body or --sites"), (two.ExitCode, two.Message));
        Assert.Equal((HarnessExit.UsageError, "--related needs --relation: one of callers, callees, derived, implementations, references, tests, includedBy, styledBy, styles, bases, overrides, overriddenBy, dispatchers, users, uses, parts, tested"), (noRelation.ExitCode, noRelation.Message));

        foreach (var (outcome, op) in new[] { (two, (string?)null), (noRelation, "related") })
        {
            var usage = JsonNode.Parse(Assert.Single(outcome.Data))!.AsObject();

            Assert.Equal(["exitCode", "summary", "run", "view", "head", "graph", "op", "answer", "page", "pages", "total", "entries"], usage.Select(member => member.Key));
            Assert.Equal((10, outcome.Message, "run", op), (usage["exitCode"]!.GetValue<int>(), usage["summary"]!.GetValue<string>(), usage["run"]!.GetValue<string>(), usage["op"]?.GetValue<string>()));
            Assert.Null(usage["view"]);
            Assert.Null(usage["answer"]);
        }
        Assert.Equal((HarnessExit.UsageError, "--relation and --depth are --related's: give them with --related"), (strayDepth.ExitCode, strayDepth.Message));
        Assert.Equal((HarnessExit.UsageError, strayDepth.Message), (strayRelation.ExitCode, strayRelation.Message));
        Assert.Equal((HarnessExit.UsageError, "op 'body' needs symbols: one or more handles, ids or names, none empty"), (misused.ExitCode, misused.Message));
        Assert.Equal(("op 'body' needs symbols: one or more handles, ids or names, none empty", "body"), (misusedDocument["summary"]!.GetValue<string>(), misusedDocument["op"]!.GetValue<string>()));
        Assert.Null(misusedDocument["answer"]);
    }

    /// <summary>The command line wires the verb: its answer, its document, a run with no graph, and its outline with no value.</summary>
    [Fact]
    public async Task TheCommandLine_RunsTheVerb_OnEveryExit()
    {
        using var tree = await InitialisedTreeAsync();

        var node = await CliRunner.RunAsync(["query-codebase-graph", "run", "--node", "Twice", "--json", "-C", tree.Tree], Token);
        var outline = await CliRunner.RunAsync(["query-codebase-graph", "run", "--outline", "-C", tree.Tree], Token);
        var related = await CliRunner.RunAsync(["query-codebase-graph", "run", "--related", "Total", "--relation", "callers", "--depth", "2", "-C", tree.Tree], Token);
        var body = await CliRunner.RunAsync(["query-codebase-graph", "run", "--body", "Twice", "Total", "-C", tree.Tree], Token);
        var none = await CliRunner.RunAsync(["query-codebase-graph", "nope", "--find", "x", "--json", "-C", tree.Tree], Token);

        Assert.Equal(0, node.ExitCode);
        Assert.Equal("node", JsonNode.Parse(node.StandardOutput)!["op"]!.GetValue<string>());
        Assert.StartsWith("8257a8 member public static double Twice", JsonNode.Parse(node.StandardOutput)!["answer"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(0, outline.ExitCode);
        Assert.StartsWith("base at ", outline.StandardOutput, StringComparison.Ordinal);
        Assert.Equal(0, related.ExitCode);
        Assert.StartsWith("1: 8257a8 member public static double Twice", related.StandardOutput, StringComparison.Ordinal);
        Assert.Equal(0, body.ExitCode);
        Assert.Contains("8257a8 Demo.Geometry.Twice src/Geometry.cs:16-19 #aac0401c", body.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("bc11b2 Demo.Geometry.Total src/Geometry.cs:5-14 #5b03176a", body.StandardOutput, StringComparison.Ordinal);
        Assert.Equal(HarnessExit.Refused, none.ExitCode);
        Assert.Equal(13, JsonNode.Parse(none.StandardOutput)!["exitCode"]!.GetValue<int>());
    }

    /// <summary>A run or an agent named by no name is a usage error, as every verb names them, and so is an empty --outline.</summary>
    [Theory]
    [InlineData("..", null, null)]
    [InlineData("run", "a/b", null)]
    [InlineData(" ", null, null)]
    [InlineData("run", null, " ")]
    public async Task TheVerb_RefusesANameThatIsNoName_AsAUsageError(string run, string? agent, string? outline)
    {
        using var tree = await InitialisedTreeAsync();

        var outcome = await Service(tree).QueryAsync(tree.Tree, new CodebaseGraphQuery(run, agent) { Find = outline is null ? "Twice" : null, Outline = outline is not null, OutlineOf = outline }, json: true, Token);

        Assert.Equal(HarnessExit.UsageError, outcome.ExitCode);
        Assert.Equal(10, JsonNode.Parse(Assert.Single(outcome.Data))!["exitCode"]!.GetValue<int>());
        Assert.True(outline is null || outcome.Message == "--outline's value is empty: give a file, a directory, a symbol, or nothing for the repository", outcome.Message);
    }

    /// <summary>
    /// A graph whose manifest does not read, a view whose record does not read and a view standing on a base no longer kept
    /// are each refused (13) saying why - never taken for no graph, nor answered by the base in the view's place.
    /// </summary>
    [Fact]
    public async Task TheVerb_RefusesARecordThatDoesNotRead_SayingWhy()
    {
        using var tree = await InitialisedTreeAsync();
        var worktree = await tree.WorktreeAsync("ann");
        await tree.SyncViewAsync("ann", worktree);
        var service = Service(tree);
        var view = File.ReadAllText(tree.Run.GraphViewFile("ann"));

        File.WriteAllText(tree.Run.GraphViewFile("ann"), view.Replace(tree.Manifest().Digest, new string('a', 64), StringComparison.Ordinal));
        var stale = await service.QueryAsync(tree.Tree, new CodebaseGraphQuery("run", "ann") { Find = "Twice" }, json: false, Token);
        File.WriteAllText(tree.Run.GraphViewFile("ann"), "{");
        var torn = await service.QueryAsync(tree.Tree, new CodebaseGraphQuery("run", "ann") { Find = "Twice" }, json: true, Token);
        File.WriteAllText(tree.Run.GraphManifestFile, "{");
        var manifest = await service.QueryAsync(tree.Tree, new CodebaseGraphQuery("run", null) { Find = "Twice" }, json: false, Token);

        Assert.Equal((HarnessExit.Refused, "the codebase graph's view 'ann' of run 'run' stands on a base the graph no longer keeps; its next whole sync makes it again against the base as it stands"), (stale.ExitCode, stale.Message));
        Assert.Equal(HarnessExit.Refused, torn.ExitCode);
        Assert.Contains(Path.GetFileName(tree.Run.GraphViewFile("ann")), torn.Message, StringComparison.Ordinal);
        Assert.Null(JsonNode.Parse(Assert.Single(torn.Data))!["answer"]);
        Assert.Equal(HarnessExit.Refused, manifest.ExitCode);
        Assert.Contains(OrchestratorLayout.GraphManifestFileName, manifest.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("has no codebase graph", manifest.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The verb reads an agent's view where one is made - its text, its document and the command line's <c>--agent</c> -
    /// answering from what the worktree holds that the base does not.
    /// </summary>
    [Fact]
    public async Task TheVerb_ReadsAnAgentsView_WhereOneIsMade()
    {
        using var tree = await InitialisedTreeAsync();
        var worktree = await tree.WorktreeAsync("ann");
        CodebaseGraphTree.WriteIn(worktree, "src/Extra.cs", "namespace Demo;\n\npublic static class Extra\n{\n}\n");
        await tree.SyncViewAsync("ann", worktree);

        var outcome = await Service(tree).QueryAsync(tree.Tree, new CodebaseGraphQuery("run", "ann") { Find = "Extra" }, json: true, Token);
        var document = JsonNode.Parse(Assert.Single(outcome.Data))!.AsObject();
        var line = await CliRunner.RunAsync(["query-codebase-graph", "run", "--agent", "ann", "--node", "Extra", "--json", "-C", tree.Tree], Token);
        var base_ = await Service(tree).QueryAsync(tree.Tree, new CodebaseGraphQuery("run", null) { Find = "Extra" }, json: false, Token);

        Assert.Equal(HarnessExit.Success, outcome.ExitCode);
        Assert.Equal(("ann", await tree.RunGitAsync("-C", worktree, "rev-parse", "HEAD")), (document["view"]!.GetValue<string>(), document["head"]!.GetValue<string>() + "\n"));
        Assert.Contains("type public static class Extra | src/Extra.cs:3-5", document["answer"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(0, line.ExitCode);
        Assert.Equal("ann", JsonNode.Parse(line.StandardOutput)!["view"]!.GetValue<string>());
        Assert.Contains("type public static class Extra", JsonNode.Parse(line.StandardOutput)!["answer"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.StartsWith("no node matched", Assert.Single(base_.Data), StringComparison.Ordinal);
    }

    /// <summary>
    /// What no graph lists - the tool's own state, a credential slot - is never read by the verb's text search, even where
    /// git tracks a file there; and a file named with backslashes is the file the view holds.
    /// </summary>
    [Fact]
    public async Task TheVerb_NeverSearchesWhatNoGraphLists_AndReadsABackslashedPath()
    {
        using var tree = await InitialisedTreeAsync();
        tree.Write(".orchestrators/notes.txt", "A Quagga grazes here.\n");
        tree.Write(".harness-config/sshItems/host/notes.txt", "A Quagga grazes here too.\n");
        tree.Write("docs/open.txt", "And a Quagga in the open.\n");
        await tree.RunGitAsync("add", "--force", ".orchestrators/notes.txt", ".harness-config/sshItems/host/notes.txt");
        await tree.CommitAsync();
        var service = Service(tree);

        var found = await service.QueryAsync(tree.Tree, new CodebaseGraphQuery("run", null) { Find = "Quagga" }, json: false, Token);
        var outline = await service.QueryAsync(tree.Tree, new CodebaseGraphQuery("run", null) { Outline = true, OutlineOf = "src\\Shapes.cs" }, json: false, Token);

        Assert.Matches("^no node matched\ntext: lines that hold 'Quagga'\n  docs/open\\.txt:1 And a Quagga in the open\\.\n\\[[0-9]+ file\\(s\\) you may not read were left out\\]$", Assert.Single(found.Data));
        Assert.StartsWith("outline of src/Shapes.cs (6 declaration(s)):", Assert.Single(outline.Data), StringComparison.Ordinal);
    }

    /// <summary>
    /// A file whose extract the graph no longer holds has its declarations in no answer, and every answer says so - the
    /// base's, whose link result names them, and an agent's view, linked in memory without them.
    /// </summary>
    [Fact]
    public async Task TheVerb_SaysInEveryAnswer_AFileWhoseExtractIsGone()
    {
        using var tree = await InitialisedTreeAsync();
        var worktree = await tree.WorktreeAsync("ann");
        await tree.SyncViewAsync("ann", worktree);
        File.Delete(tree.Run.GraphExtractFile(tree.Manifest().Files["src/Geometry.cs"].Extract!));
        var service = Service(tree);

        var found = await service.QueryAsync(tree.Tree, new CodebaseGraphQuery("run", null) { Find = "Twice" }, json: false, Token);
        var outline = await service.QueryAsync(tree.Tree, new CodebaseGraphQuery("run", null) { Outline = true }, json: false, Token);
        var view = await service.QueryAsync(tree.Tree, new CodebaseGraphQuery("run", "ann") { Node = "Circle" }, json: false, Token);
        const string Gone = "\n1 file(s) whose extract the graph no longer holds, its declarations in no answer: src/Geometry.cs";

        Assert.StartsWith("no node matched\ntext: lines that hold 'Twice'", Assert.Single(found.Data), StringComparison.Ordinal);
        Assert.EndsWith(Gone, Assert.Single(found.Data), StringComparison.Ordinal);
        Assert.Matches("^base at [0-9a-f]{40}: [0-9]+ file\\(s\\)\n", Assert.Single(outline.Data));
        Assert.EndsWith(Gone, Assert.Single(outline.Data), StringComparison.Ordinal);
        Assert.StartsWith("51b2d4 type public sealed class Circle", Assert.Single(view.Data), StringComparison.Ordinal);
        Assert.EndsWith(Gone, Assert.Single(view.Data), StringComparison.Ordinal);
    }

    /// <summary>A misuse is a usage error (10) told before anything is read - never hidden by a run with no graph (13).</summary>
    [Theory]
    [InlineData("relation", "relation is 'siblings': give one of callers, callees, derived, implementations, references, tests, includedBy, styledBy, styles, bases, overrides, overriddenBy, dispatchers, users, uses, parts, tested")]
    [InlineData("depth", "depth is 0: hops start at 1")]
    [InlineData("find", "query is empty: the words to match, split at case changes, underscores, digits and dots as a name is.")]
    [InlineData("body", "op 'body' needs symbols: one or more handles, ids or names, none empty")]
    public async Task TheVerb_TellsAMisuse_BeforeItReadsAnyGraph(string misuse, string usage)
    {
        using var tree = await InitialisedTreeAsync();
        var query = misuse switch
        {
            "relation" => new CodebaseGraphQuery("nope", null) { Related = "Total", Relation = "siblings" },
            "depth" => new CodebaseGraphQuery("nope", null) { Related = "Total", Relation = "callers", Depth = 0 },
            "find" => new CodebaseGraphQuery("nope", null) { Find = " " },
            _ => new CodebaseGraphQuery("nope", null) { Body = ["Total", " "] },
        };

        var outcome = await Service(tree).QueryAsync(tree.Tree, query, json: true, Token);
        var document = JsonNode.Parse(Assert.Single(outcome.Data))!.AsObject();

        Assert.Equal((HarnessExit.UsageError, usage), (outcome.ExitCode, outcome.Message));
        Assert.Equal((10, usage), (document["exitCode"]!.GetValue<int>(), document["summary"]!.GetValue<string>()));
        Assert.Null(document["answer"]);
    }

    /// <summary>An <c>--outline</c> of a file the view does not hold says so, and searches for no word of its name.</summary>
    [Fact]
    public async Task TheVerbsOutline_OfAFileTheViewDoesNotHold_SaysSo()
    {
        using var tree = await InitialisedTreeAsync();

        var missing = await Service(tree).QueryAsync(tree.Tree, new CodebaseGraphQuery("run", null) { Outline = true, OutlineOf = "src/Missing.cs" }, json: false, Token);
        var backslashed = await Service(tree).QueryAsync(tree.Tree, new CodebaseGraphQuery("run", null) { Outline = true, OutlineOf = "src\\Missing.cs" }, json: false, Token);

        Assert.Equal("the view holds no file or directory at 'src/Missing.cs'", Assert.Single(missing.Data));
        Assert.Equal("the view holds no file or directory at 'src\\Missing.cs'", Assert.Single(backslashed.Data));
    }

    /// <summary>
    /// Typed in an agent's worktree, the verb reads the main checkout's run with the main checkout's configuration - so what
    /// no graph lists is what the main checkout's configuration says, whatever the worktree's own says.
    /// </summary>
    [Fact]
    public async Task TheVerb_TypedInAnAgentsWorktree_ReadsTheMainCheckoutsRunAndConfiguration()
    {
        using var tree = await InitialisedTreeAsync();
        tree.Write(".worktrees/notes.txt", "A Quagga hides here.\n");
        await tree.CommitAsync(force: ".worktrees/notes.txt");
        var worktree = await tree.WorktreeAsync("ann");
        var config = Path.Combine(worktree, HarnessLayout.DirectoryName, HarnessLayout.ConfigFileName);
        File.WriteAllText(config, File.ReadAllText(config).Replace($"\"{Core.Configuration.WorktreeSettings.SeededRoot}\"", "\"elsewhere\"", StringComparison.Ordinal));

        var outcome = await Service(tree).QueryAsync(worktree, new CodebaseGraphQuery("run", null) { Find = "Quagga" }, json: false, Token);

        Assert.Equal(HarnessExit.Success, outcome.ExitCode);
        Assert.StartsWith("no node matched\ntext: no line holds 'Quagga'", Assert.Single(outcome.Data), StringComparison.Ordinal);
    }

    /// <summary>
    /// The command's own help says what its --sites option takes - a key path dotted, a name holding a dot or a bracket
    /// quoted in brackets in either quote mark or its names' dots as they are, or a path as .NET's configuration writes
    /// one - as the graph's help does, so a later wording change to the option is held.
    /// </summary>
    [Fact]
    public async Task TheCommandsSitesOption_SaysTheDatumFormsItTakes()
    {
        var help = await CliRunner.RunAsync([CodebaseGraphQueryService.CommandName, "--help"], Token);

        Assert.Equal(HarnessExit.Success, help.ExitCode);
        Assert.Contains(
            Prose.Words(
                "Every place that decides data - a name, a key path (dotted - a name holding a dot or a bracket quoted in "
                + "brackets, A[\"B.C\"] or A['B.C'], or its dots as they are, A.B.C, asking of every key it may mean; or as "
                + ".NET's configuration writes one: A:B, A__B), a diagnostic id, or a quoted literal asked by its value "
                + "alone: what defines, calls, uses, makes, references, inherits, implements or overrides it, every literal "
                + "that holds it and every key it names, with the coverage lines."),
            Prose.Words(help.StandardOutput),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Every read operation runs on an agent's view - a body read from the agent's worktree, as it holds the file - and none
    /// writes anything of the graph.
    /// </summary>
    [Fact]
    public async Task TheVerb_RunsEveryReadOperationOnAView_FromItsWorktree_WritingNothing()
    {
        using var tree = await InitialisedTreeAsync();
        var worktree = await tree.WorktreeAsync("ann");
        CodebaseGraphTree.WriteIn(worktree, "src/Geometry.cs", File.ReadAllText(Path.Combine(tree.Tree, "src", "Geometry.cs")).Replace("Total([shape, shape])", "Total([shape, shape, shape])", StringComparison.Ordinal));
        await tree.SyncViewAsync("ann", worktree);
        var service = Service(tree);
        var before = Snapshot(tree);

        var body = await service.QueryAsync(tree.Tree, new CodebaseGraphQuery("run", "ann") { Body = ["Twice"] }, json: false, Token);
        var outline = await service.QueryAsync(tree.Tree, new CodebaseGraphQuery("run", "ann") { Outline = true, OutlineOf = "src/Geometry.cs" }, json: false, Token);
        var related = await service.QueryAsync(tree.Tree, new CodebaseGraphQuery("run", "ann") { Related = "Total", Relation = "callers" }, json: false, Token);
        var found = await service.QueryAsync(tree.Tree, new CodebaseGraphQuery("run", "ann") { Find = "Twice" }, json: false, Token);
        var node = await service.QueryAsync(tree.Tree, new CodebaseGraphQuery("run", "ann") { Node = "Twice" }, json: false, Token);

        Assert.Matches("^8257a8 Demo\\.Geometry\\.Twice src/Geometry\\.cs:16-19 #[0-9a-f]{8}\n", Assert.Single(body.Data));
        Assert.Contains("\n18\t        return Total([shape, shape, shape]);", Assert.Single(body.Data), StringComparison.Ordinal);
        Assert.StartsWith("outline of src/Geometry.cs (4 declaration(s)):", Assert.Single(outline.Data), StringComparison.Ordinal);
        Assert.StartsWith("1: 8257a8 member public static double Twice", Assert.Single(related.Data), StringComparison.Ordinal);
        Assert.StartsWith("8257a8 member public static double Twice", Assert.Single(found.Data), StringComparison.Ordinal);
        Assert.StartsWith("8257a8 member public static double Twice", Assert.Single(node.Data), StringComparison.Ordinal);
        Assert.Equal(before, Snapshot(tree));
    }

    /// <summary>The topic says what the tool and the verb do, from the code: the operations' bounds, who is offered the tool, and the verb's exits.</summary>
    [Fact]
    public void TheCodebaseGraphTopic_SaysWhatTheToolAndTheVerbDo()
    {
        var topic = Prose.Words(HelpTopics.Render("codebase-graph"));

        Assert.Contains($"find - the nodes a query's words match, best first, generated code after every other, a tie broken by how near the step's own work each is - its context pack or a working set it was given, a file it writes, a file its agent changed, a path or a name its prompt holds - then by id, {CodebaseGraphTool.FindLines} a page;", topic, StringComparison.Ordinal);
        Assert.Contains($"related - the nodes one relation reaches ({string.Join("; ", GraphRelations.Described.Select(relation => $"{relation.Name}, {relation.Meaning}"))}), as many hops as asked, from 1, by distance - every node reached gone through and reached once, so a hop that reaches nothing new ends the walk, every other part of a declaration walked as the node itself but by parts - {CodebaseGraphTool.RelatedLines} a page;", topic, StringComparison.Ordinal);
        Assert.Contains($"outline - a directory's files, its subdirectories' too, a section for each directory by path - each file one result: its own node's line with the language that read it, or the state none read it in, and test where it is test code, then each of its top declarations, a type or a member no type or member of the file holds, with what it and the declarations inside it test where the graph's tests edges say so - a file's or a type's declarations, or, with neither, the repository's, the root's own files outlined as a directory's are; {CodebaseGraphTool.OutlineLines} a page;", topic, StringComparison.Ordinal);
        Assert.Contains($"around - a node's place: what it is declared in, outermost first - its file, its namespace, its type - its siblings in the nearest of those, its members, the declarations just before and just after it in its file, and its callers and callees, {CodebaseGraphTool.NodeLinesAKind} of each a page;", topic, StringComparison.Ordinal);
        Assert.Contains($"impact - what a change of a node reaches, by the one walk the change-first pack leads with: its callers at every distance, what it overrides and what overrides it, its implementations, the interface members that dispatch to it and its derived types, and every test that reaches any of them, each under the relation whose edge reached it, with how many edges its best path has and that path, nearest first, {CodebaseGraphTool.RelatedLines} of each a page;", topic, StringComparison.Ordinal);
        Assert.Contains($"no answer of a read is cut: each is pages of whole entries (GraphPages), every page's entries within {CodebaseGraphTool.MostCharacters} characters less what the pages reserve - an entry wider than that given in pieces, a page each, each marked with … where it goes on - and a block's head on the page that serves its first line; an answer of more than one page starts with the line 'page k of n', which gives the exact total and how to ask for the next on every page but the last, which says it is the last, and any page is asked with page, from 1, an answer with no header holding all of it; a page past the last is refused with the answer's range, and the totals a header says are counted, never estimated. An edit's answer and a wholeFile's are one page, said once it is done, and neither takes page.", topic, StringComparison.Ordinal);
        Assert.Contains("A step whose graph is tools is offered the tool - the API clients among their own, Claude Code through the engine's tool server - with the read operations, wholeFile only where its reads are narrowed and edit only on the API clients (the probe measured Claude Code takes an Edit after a ranged Read, so an agent that means to change a body reads its lines and Edits them), its schema naming those alone, and query-codebase-graph below runs its read operations by hand.", topic, StringComparison.Ordinal);
        Assert.Contains($"The reads such a step makes already answer from the graph too ('help agent-clients'), so the graph costs it no turn of its own: search_files gives each hit inside a declaration of a file the graph parsed under that declaration's line, the innermost that holds it, a range of view_file is headed by the declarations its lines meet, and where its reads are narrowed a whole view_file of a file the graph parsed over {CodebaseGraphTool.NarrowLines} lines is answered with the file's outline in its place - each declaration checked against the file as it stands, as a body is.", topic, StringComparison.Ordinal);
        Assert.Contains($"The run's repository map is the tree at a glance: a header line - the commit it is of, how many files and declarations the graph holds, and that the graph tool answers for the tree as it is now - the projects and top directories with their counts, then the most central declarations one line each as find gives it, a declaration's centrality the number of files other than its own and a test's whose references bind to it, raised by how its name matches the run's task as find weighs a name's words - the share of the task's words its name holds whole, each weighed by how often the task says it and how rare it is among the view's names, a name that holds them all raised as far as {DssHarness.Core.Codebase.CodebaseGraphQueries.TaskRaise} more such files would raise it - no declaration of a file that declares a test and nothing generated. It is made once for each ground of reads the run's steps share - steps whose read rules are the same share one, and no map lists a declaration of a file its steps may not read - from the base as it stands when the first graphed reason step of that ground is given its prompt, kept whole in pages in map.md with the pages after it in map.2.md and on (map-<a digest of the read rules>.md and on for steps whose read rules narrow them) with what it was made from in map.json under the run's graph, made under its own lock so steps that start together keep one, and never made again in the run, a resumed run included - the base moves as folds land and the map does not - paged at whole lines under codebaseGraph.mapTokens ({DssHarness.Core.Codebase.CodebaseGraphShape.DefaultMapTokens} by default; 0 gives no map), no line dropped: page 1 holds the header and the whole lines that fit that size, and ends, where more pages follow, with a line naming which page of how many - what a kept map's count is read from - and its pages are asked of the graph tool's map, from page 1 - the one wording of that line serving every step of the map's ground of reads, with the tool and without it, for the map is one file every step of the ground reads. One that cannot be made or read is said, and the step goes on without it. A step whose graph is tools is pushed no map: no step with the tool was measured asking it for a page. Every launch of a step whose graph is map that begins a conversation of its own is given it as the <repository-map> part ahead of its context pack, the same bytes for every step that reads alike, so a client that caches a prefix reads it from the cache ('help agent-clients' says where each client sets its marks). A launch that goes on with a stored conversation on the API clients is told, in its one new user message, <codebase-changes> instead: the declarations its view changed since what that conversation was told of them - its pack and every delta after it - moved or changed with their new range and hash, removed - and, where its step pushes its pack, the nodes of its own pack the conversation was not given, every one, told in pages of the step's pack's size, page 1 in the message and each later page by graph's changes with page, a page never too small to hold its first and last lines and a line; never a map or a pack again whole, for a conversation's prefix is never changed, and no earlier message, tool result, map or pack is ever sent changed; a step that pushes no pack, whose conversation holds no declaration of the codebase, is told none. A launch the walk of the step's order moves to another client or provider begins a conversation of its own, and is given what its step pushes, as Claude Code, whose every launch begins one, is. Each launch's first message is a line of the step's graph.jsonl - what of the graph it was pushed and what that cost, or why it was given no pack - and the step's figures are its last launch's, with the pages of the map and the pack its tool was asked for counted apart.", topic, StringComparison.Ordinal);
        Assert.Contains($"{ToolPackage.Command} {CodebaseGraphQueryService.CommandName} <run> runs one of the read operations by hand - --find <query>, --node <symbol>, --related <symbol> --relation <relation> [--depth <n>], --outline [<path, directory or symbol>], --around <symbol>, --impact <symbol>, --body <symbol>... or --sites <data>, any of them with --page <n> - on the run's graph", topic, StringComparison.Ordinal);
        Assert.Contains("--json the same in one document holding the digest of the graph answered from, the page, the pages, the whole answer's total and the page's entries, each with what it counts toward the total, its section's caption and its node. Each call counts its page on the graph as it stands, keeping nothing: a script that reads page after page compares the digest to know they are of one graph.", topic, StringComparison.Ordinal);
        Assert.Contains($"It ends 0, {HarnessExit.Refused} where the run has no graph, and {HarnessExit.UsageError} for a usage error; no host is asked to run it.", topic, StringComparison.Ordinal);
        Assert.Contains($"The page that computes an answer of more than one page keeps its whole result - under the digest of the graph the view answers from, its link result and every file as the view holds it, so the step's own writes move it as a fold does, in {Core.Orchestration.OrchestratorLayout.StepPagesDirectoryName}/ under the step's directory or the side-task's, every result kept for the conversation's life whatever it holds, packed - its one line compressed - and put away when the conversation ends: a step done whose work no later step goes on with, every step of a run that is done - and a later page, a resumed conversation and a step that goes on with the work read pages of that one result, each under the read rules it was computed under; an answer of one page is never kept, and page 1 never reads what was. A later page asked after the graph moved, or with no result of page 1 that reads, is made again on the graph as it stands, and its header says which.", topic, StringComparison.Ordinal);
        Assert.Contains($"searched as search_files searches but in every file whatever it holds and never timed, each path:line with no hash and its line whole, a section of their own, {CodebaseGraphTool.TextHits} of each word a page, all of them paged - nothing left out - and the files it did not read said", topic, StringComparison.Ordinal);

        // The workflow key says the maps as the topic does: one for each ground of reads, each kept in its own file.
        Assert.Contains(
            string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"mapTokens the size of a page of each of the run's repository maps - made once for each ground of reads the run's steps share - steps whose read rules are the same share one, and no map lists a declaration of a file its steps may not read - from the base as it stands when the first graphed reason step of that ground is given its prompt, kept whole in pages in map.md with the pages after it in map.2.md and on (map-<a digest of the read rules>.md and on for steps whose read rules narrow them) under the run's graph, and read as it is by every step of that ground after, no line dropped: the pages after the first kept with it - from 0 to {DssHarness.Core.Codebase.CodebaseGraphShape.MostMapTokens:N0} tokens; 0 asks for no map; absent, {DssHarness.Core.Codebase.CodebaseGraphShape.DefaultMapTokens:N0}"),
            topic,
            StringComparison.Ordinal);
    }

    private static async Task<CodebaseGraphTree> InitialisedTreeAsync(params (string Path, string Text)[] more)
    {
        var tree = await CodebaseGraphTree.CreateAsync();
        var initialised = await tree.Harness.InitService.InitializeAsync(tree.Tree, Token);
        Assert.True(initialised.Succeeded, initialised.Message);

        tree.Over([.. CodeGraphExtractors.OfThisBuild.All]);

        foreach (var (path, text) in CodebaseGraphToolTests.Demo.Concat(more))
        {
            tree.Write(path, text);
        }

        await tree.CommitAsync();
        await tree.SyncAsync();
        return tree;
    }

    private static CodebaseGraphQueryService Service(CodebaseGraphTree tree)
        => new(tree.Harness.ContextLoader, tree.Service, tree.Harness.GitClient, tree.Harness.FileSystem, tree.Harness.Platform, TimeProvider.System);

    /// <summary>Every file of the run's graph with its bytes and when it was written, so a read that wrote anything is told.</summary>
    private static string Snapshot(CodebaseGraphTree tree)
        => string.Join('\n', Directory.EnumerateFiles(tree.Run.GraphDirectory, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .Select(file => $"{file} {File.GetLastWriteTimeUtc(file):O} {Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(file)))}"));
}
