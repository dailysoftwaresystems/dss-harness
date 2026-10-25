using System.Text.Json.Nodes;
using DssHarness.Core.Agents.Runtime;
using DssHarness.Core.Codebase;
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
        Assert.Equal(["exitCode", "summary", "run", "view", "head", "op", "answer"], document.Select(member => member.Key));
        Assert.Equal((0, "run", "base", await tree.HeadAsync(), "node"), (document["exitCode"]!.GetValue<int>(), document["run"]!.GetValue<string>(), document["view"]!.GetValue<string>(), document["head"]!.GetValue<string>(), document["op"]!.GetValue<string>()));
        Assert.StartsWith("bc11b2 member public static double Total(IShape[] shapes)", document["answer"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.DoesNotContain(HarnessLayout.OrchestratorsDirectoryName, document["answer"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(before, Snapshot(tree));
    }

    /// <summary><c>--outline</c> outlines a file where the view holds one at what it is given, a symbol otherwise, and the repository with nothing.</summary>
    [Theory]
    [InlineData("src/Shapes.cs", "outline of src/Shapes.cs (6 declaration(s)):")]
    [InlineData("Circle", "51b2d4 type public sealed class Circle(double radius) : IShape | src/Shapes.cs:10-21 #fd46243a | A circle.")]
    [InlineData(null, "base at ")]
    public async Task TheVerbsOutline_TakesAFileASymbolOrNothing(string? of, string first)
    {
        using var tree = await InitialisedTreeAsync();

        var outcome = await Service(tree).QueryAsync(tree.Tree, new CodebaseGraphQuery("run", null) { Outline = true, OutlineOf = of }, json: false, Token);

        Assert.Equal(HarnessExit.Success, outcome.ExitCode);
        Assert.StartsWith(first, Assert.Single(outcome.Data), StringComparison.Ordinal);
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
        var misused = await service.QueryAsync(tree.Tree, new CodebaseGraphQuery("run", null) { Body = ["1", "2", "3", "4", "5", "6", "7", "8", "9"] }, json: true, Token);
        var noneDocument = JsonNode.Parse(Assert.Single(none.Data))!.AsObject();
        var misusedDocument = JsonNode.Parse(Assert.Single(misused.Data))!.AsObject();

        Assert.Equal(HarnessExit.Refused, none.ExitCode);
        Assert.StartsWith("run 'nope' has no codebase graph in '", none.Message, StringComparison.Ordinal);
        Assert.Equal(["exitCode", "summary", "run", "view", "head", "op", "answer"], noneDocument.Select(member => member.Key));
        Assert.Equal((13, "nope", "find"), (noneDocument["exitCode"]!.GetValue<int>(), noneDocument["run"]!.GetValue<string>(), noneDocument["op"]!.GetValue<string>()));
        Assert.Null(noneDocument["view"]);
        Assert.Null(noneDocument["answer"]);
        Assert.Equal((HarnessExit.UsageError, "give exactly one of --find, --node, --related, --outline or --body"), (two.ExitCode, two.Message));
        Assert.Equal((HarnessExit.UsageError, "--related needs --relation: one of callers, callees, derived, implementations, references, tests, includedBy"), (noRelation.ExitCode, noRelation.Message));

        foreach (var (outcome, op) in new[] { (two, (string?)null), (noRelation, "related") })
        {
            var usage = JsonNode.Parse(Assert.Single(outcome.Data))!.AsObject();

            Assert.Equal(["exitCode", "summary", "run", "view", "head", "op", "answer"], usage.Select(member => member.Key));
            Assert.Equal((10, outcome.Message, "run", op), (usage["exitCode"]!.GetValue<int>(), usage["summary"]!.GetValue<string>(), usage["run"]!.GetValue<string>(), usage["op"]?.GetValue<string>()));
            Assert.Null(usage["view"]);
            Assert.Null(usage["answer"]);
        }
        Assert.Equal((HarnessExit.UsageError, "--relation and --depth are --related's: give them with --related"), (strayDepth.ExitCode, strayDepth.Message));
        Assert.Equal((HarnessExit.UsageError, strayDepth.Message), (strayRelation.ExitCode, strayRelation.Message));
        Assert.Equal((HarnessExit.UsageError, "op 'body' needs symbols: 1 to 8 handles, ids or names"), (misused.ExitCode, misused.Message));
        Assert.Equal(("op 'body' needs symbols: 1 to 8 handles, ids or names", "body"), (misusedDocument["summary"]!.GetValue<string>(), misusedDocument["op"]!.GetValue<string>()));
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
        Assert.True(outline is null || outcome.Message == "--outline's value is empty: give a file, a symbol, or nothing for the repository", outcome.Message);
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

        Assert.Matches("^no node matched\ntext: lines that hold 'Quagga'\n  docs/open\\.txt:1 And a Quagga in the open\\.\n  \\[[0-9]+ file\\(s\\) you may not read were left out\\]$", Assert.Single(found.Data));
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
    [InlineData("relation", "relation is 'siblings': give one of callers, callees, derived, implementations, references, tests, includedBy")]
    [InlineData("depth", "depth is 9: give 1 to 3")]
    [InlineData("find", "query is empty: the words to match, split at case changes, underscores, digits and dots as a name is.")]
    [InlineData("body", "op 'body' needs symbols: 1 to 8 handles, ids or names")]
    public async Task TheVerb_TellsAMisuse_BeforeItReadsAnyGraph(string misuse, string usage)
    {
        using var tree = await InitialisedTreeAsync();
        var query = misuse switch
        {
            "relation" => new CodebaseGraphQuery("nope", null) { Related = "Total", Relation = "siblings" },
            "depth" => new CodebaseGraphQuery("nope", null) { Related = "Total", Relation = "callers", Depth = 9 },
            "find" => new CodebaseGraphQuery("nope", null) { Find = " " },
            _ => new CodebaseGraphQuery("nope", null) { Body = ["1", "2", "3", "4", "5", "6", "7", "8", "9"] },
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

        Assert.Equal("the view holds no file at 'src/Missing.cs'", Assert.Single(missing.Data));
        Assert.Equal("the view holds no file at 'src\\Missing.cs'", Assert.Single(backslashed.Data));
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

    /// <summary>The topic says what the tool and the verb do, from the code: the operations' bounds, that no step is given the tool yet, and the verb's exits.</summary>
    [Fact]
    public void TheCodebaseGraphTopic_SaysWhatTheToolAndTheVerbDo()
    {
        var topic = Prose.Words(HelpTopics.Render("codebase-graph"));

        Assert.Contains($"find - the nodes a query's words match, best first, generated code after every other, at most {CodebaseGraphTool.FindLines} ({CodebaseGraphTool.FindMost} with limit);", topic, StringComparison.Ordinal);
        Assert.Contains("No step is given the tool yet - the API clients will offer it - and query-codebase-graph below runs its read operations;", topic, StringComparison.Ordinal);
        Assert.Contains($"{ToolPackage.Command} {CodebaseGraphQueryService.CommandName} <run> runs one of the read operations by hand", topic, StringComparison.Ordinal);
        Assert.Contains($"It ends 0, {HarnessExit.Refused} where the run has no graph, and {HarnessExit.UsageError} for a usage error; no host is asked to run it.", topic, StringComparison.Ordinal);
        Assert.Contains($"at most {CodebaseGraphTool.TextHits} lines that hold the name as a whole word", topic, StringComparison.Ordinal);
    }

    private static async Task<CodebaseGraphTree> InitialisedTreeAsync()
    {
        var tree = await CodebaseGraphTree.CreateAsync();
        var initialised = await tree.Harness.InitService.InitializeAsync(tree.Tree, Token);
        Assert.True(initialised.Succeeded, initialised.Message);

        tree.Over([.. CodeGraphExtractors.OfThisBuild.All]);

        foreach (var (path, text) in CodebaseGraphToolTests.Demo)
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
