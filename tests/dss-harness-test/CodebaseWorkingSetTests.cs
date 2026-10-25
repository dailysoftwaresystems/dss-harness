using System.Text.Json.Nodes;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Agents;
using DssHarness.Core.Agents.ClaudeCode;
using DssHarness.Core.Agents.Runtime;
using DssHarness.Core.Codebase;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Orchestration.Flow;
using DssHarness.Core.Results;

namespace DssHarness.Tests;

/// <summary>The working set (value 372): <see cref="CodebaseWorkingSet"/> made from a step's graph lines and its stored stream, kept, and given to the packs after it.</summary>
public sealed class CodebaseWorkingSetTests
{
    private const string Loyalty = "src/Pricing/LoyaltyDiscount.cs";

    private const string Type = "Shop.Pricing.LoyaltyDiscount";

    private readonly CancellationToken _token = TestContext.Current.CancellationToken;

    [Fact]
    public async Task AReadsLines_AreTheOnesItsAnswerNumbered_EachClientsForm_AndMeetAMembersOwnLinesNotItsTypes()
    {
        using var tree = await CodebaseContextPackTests.ShopAsync();
        var view = await ViewAsync(tree);

        var claude = Make(tree, view, Call("1", ClaudeCodeTools.Read, new() { ["file_path"] = Full(tree, Loyalty), ["offset"] = 8, ["limit"] = 4 }, "     8→    public decimal Apply(Order order, decimal total)\n     9→    {\n    10→        return 1;\n    11→    }"));
        var runtime = Make(tree, view, Call("1", AgentTools.ViewFile, new() { ["path"] = Loyalty, ["start_line"] = 9, ["end_line"] = 10 }, "9\t    {\n10\t        return 1;", AgentToolRunner.Harness));
        var whole = Make(tree, view, Call("1", AgentTools.ViewFile, new() { ["path"] = Loyalty }, string.Concat(Enumerable.Range(1, 13).Select(line => $"{line}\tx\n")), AgentToolRunner.Harness));

        // The lines given stand nowhere now: their numbers place them. Where they stand once, they place themselves.
        var moved = Make(tree, view,
        [
            new AssistantMessage("", [new AgentToolCall("1", AgentTools.ViewFile)]),
            new ToolCallFinished("1", true, TimeSpan.Zero, new JsonObject { ["path"] = Loyalty }.ToJsonString(), "1\t    public decimal Apply(Order order, decimal total)  \n2\t    {\n3\t        return history.HasOrdered(order.Customer) ? total * 0.95m : total;\n4\t    }\n<note>more</note>"),
        ]);

        Assert.Equal([("Shop.Pricing.LoyaltyDiscount.Apply", WorkingSetUses.Read)], claude.Nodes.Select(node => (Qualified(view, node), Assert.Single(node.Uses))));
        Assert.Equal(["Shop.Pricing.LoyaltyDiscount.Apply"], moved.Nodes.Select(node => Qualified(view, node)));

        // Lines given that do not follow one another are placed by their numbers alone: 8 to 10 is Apply's.
        var gapped = Make(tree, view, Call("1", AgentTools.ViewFile, new() { ["path"] = Loyalty }, "8\t{\n10\t    public decimal Apply(Order order, decimal total)", AgentToolRunner.Harness));
        Assert.Equal(["Shop.Pricing.LoyaltyDiscount.Apply"], gapped.Nodes.Select(node => Qualified(view, node)));
        Assert.Equal(["Shop.Pricing.LoyaltyDiscount.Apply"], runtime.Nodes.Select(node => Qualified(view, node)));
        Assert.Equal(["Shop.Pricing.LoyaltyDiscount", "Shop.Pricing.LoyaltyDiscount.Apply"], whole.Nodes.Select(node => Qualified(view, node)).Order(StringComparer.Ordinal));

        var apply = Assert.Single(claude.Nodes);
        var card = view.Card(apply.Id);
        Assert.Equal((card.Handle, card.Path, card.Start, card.End, card.Hash, false, 1), (apply.Handle, apply.Path, apply.Start, apply.End, apply.Hash, apply.Gone, apply.Times));
    }

    [Fact]
    public async Task AWrite_IsWhereItsNewTextStandsOnceInTheFileAsTheStepLeftIt_ElseTheLinesItNames_ElseUnplaced()
    {
        using var tree = await CodebaseContextPackTests.ShopAsync();
        tree.Write(Loyalty, (await File.ReadAllTextAsync(Path.Combine(tree.Tree, Loyalty), _token)).Replace("total * 0.95m", "total * 0.90m", StringComparison.Ordinal));
        var view = await ViewAsync(tree, Loyalty);

        string Edited(string tool, JsonObject input, bool ok = true)
        {
            var set = Make(tree, view, Call("1", tool, input, "done", tool is ClaudeCodeTools.Edit ? AgentToolRunner.ClaudeCode : AgentToolRunner.Harness, ok));
            Assert.All(set.Nodes, node => Assert.Equal((WorkingSetUses.Edited, 1), (Assert.Single(node.Uses), node.Times)));
            return string.Join(", ", set.Nodes.Select(node => Qualified(view, node)[(Type.Length - "LoyaltyDiscount".Length)..])) + $" | {set.Unplaced}";
        }

        JsonObject Claude(string text, bool every = false) => new() { ["file_path"] = Full(tree, Loyalty), ["old_string"] = "a", ["new_string"] = text, ["replace_all"] = every };
        JsonObject Replace(string text, bool every = false) => new() { ["path"] = Loyalty, ["old_string"] = "a", ["new_string"] = text, ["replace_all"] = every };
        JsonObject Insert(string text, int? line) => line is { } at ? new() { ["path"] = Loyalty, ["line"] = at, ["text"] = text } : new() { ["path"] = Loyalty, ["text"] = text };

        // Where it stands once - a CR LF's lines as a LF's - meeting the own lines of each declaration there: line 10 is
        // Apply's, 6 and 7 the type's, 8 Apply's.
        Assert.Equal("LoyaltyDiscount.Apply | 0", Edited(ClaudeCodeTools.Edit, Claude("total * 0.90m")));
        Assert.Equal("LoyaltyDiscount, LoyaltyDiscount.Apply | 0", Edited(ClaudeCodeTools.Edit, Claude("{\r\n    public decimal Apply(")));
        Assert.Equal("LoyaltyDiscount | 0", Edited(AgentTools.ReplaceInFile, Replace("IDiscount\n{\n")));
        Assert.Equal("LoyaltyDiscount, LoyaltyDiscount.Apply | 0", Edited(ClaudeCodeTools.Edit, Claude("{ \t\n    public decimal Apply(  ")));
        Assert.Equal("LoyaltyDiscount.Apply | 0", Edited(AgentTools.ReplaceInFile, Replace("total * 0.90m : total;\n    }\n    ")));
        Assert.Equal("LoyaltyDiscount | 0", Edited(AgentTools.ReplaceInFile, Replace("\n{")));
        Assert.Equal("LoyaltyDiscount | 0", Edited(AgentTools.ReplaceInFile, Replace("\n}")));

        // Every place it stands, where it replaced every occurrence; a node met in several, touched once.
        Assert.Equal("LoyaltyDiscount, LoyaltyDiscount.Apply | 0", Edited(ClaudeCodeTools.Edit, Claude("{", every: true)));
        Assert.Equal("LoyaltyDiscount.Apply | 0", Edited(AgentTools.ReplaceInFile, Replace("total", every: true)));
        Assert.Equal(" | 1", Edited(AgentTools.ReplaceInFile, Replace("nowhere in the file", every: true)));

        // Where it stands more than once, nowhere, or holds nothing - a deletion: the lines the call names, else none and counted.
        Assert.Equal(" | 1", Edited(AgentTools.ReplaceInFile, Replace("total")));
        Assert.Equal(" | 1", Edited(AgentTools.ReplaceInFile, Replace("")));
        Assert.Equal(" | 1", Edited(AgentTools.ReplaceInFile, Replace("nowhere in the file")));
        Assert.Equal("LoyaltyDiscount | 0", Edited(AgentTools.InsertInFile, Insert("{", 7)));
        Assert.Equal("LoyaltyDiscount, LoyaltyDiscount.Apply | 0", Edited(AgentTools.InsertInFile, Insert("one\ntwo", 7)));
        Assert.Equal(" | 1", Edited(AgentTools.InsertInFile, Insert("one", null)));

        // A call that failed wrote nothing.
        Assert.Equal(" | 0", Edited(ClaudeCodeTools.Edit, Claude("total * 0.90m"), ok: false));
    }

    [Fact]
    public async Task ANodeTouchedInOnePartOfAType_IsNamedByThatPartsFileAndLines()
    {
        using var tree = await CodebaseContextPackTests.TreeAsync(
            ("src/Ledger.cs", "namespace Shop;\n\npublic partial class Ledger\n{\n    public int One() => 1;\n}\n"),
            ("src/Ledger.More.cs", "namespace Shop;\n\npublic partial class Ledger\n{\n    public int Two() => 2;\n\n    public int Three() => 3;\n}\n"));
        var view = await ViewAsync(tree);

        var more = Make(tree, view, Call("1", AgentTools.ViewFile, new() { ["path"] = "src/Ledger.More.cs", ["start_line"] = 3, ["end_line"] = 6 }, "3\tx\n4\tx\n5\tx\n6\tx", AgentToolRunner.Harness));
        var first = Make(tree, view, Call("1", AgentTools.ViewFile, new() { ["path"] = "src/Ledger.cs", ["start_line"] = 3, ["end_line"] = 4 }, "3\tx\n4\tx", AgentToolRunner.Harness));

        Assert.Equal(
            [("Shop.Ledger", "src/Ledger.More.cs", 3, 8), ("Shop.Ledger.Two", "src/Ledger.More.cs", 5, 5)],
            more.Nodes.Select(node => (Qualified(view, node), node.Path!, node.Start!.Value, node.End!.Value)));
        Assert.Equal([("Shop.Ledger", "src/Ledger.cs", 3, 6)], first.Nodes.Select(node => (Qualified(view, node), node.Path!, node.Start!.Value, node.End!.Value)));

        // A graph call names no file: the read after it does.
        var ledger = Id(view, "Shop.Ledger");
        var asked = CodebaseWorkingSet.Make(
            view,
            Call("1", AgentTools.ViewFile, new() { ["path"] = "src/Ledger.cs" }, "3\tx\n4\tx", AgentToolRunner.Harness),
            [new GraphCallLine("node", GraphToolOutcome.Answered, [ledger], [], [])],
            tree.Harness.FileSystem,
            tree.Harness.Platform.PathComparison);
        Assert.Equal([(ledger, "src/Ledger.cs", 3, 6, 2)], asked.Nodes.Select(node => (node.Id, node.Path!, node.Start!.Value, node.End!.Value, node.Times)));
    }

    [Fact]
    public async Task AWholeFilesWrite_EditsEveryDeclarationInIt_AndAPathOutsideTheTreeOrTheViewIsNone()
    {
        using var tree = await CodebaseContextPackTests.ShopAsync();
        var view = await ViewAsync(tree);
        AgentEvent[] stream =
        [
            .. Call("1", ClaudeCodeTools.Write, new() { ["file_path"] = Full(tree, Loyalty), ["content"] = "x" }, "written"),
            .. Call("2", AgentTools.CreateFile, new() { ["path"] = "./src/Pricing/SeasonalDiscount.cs".Replace('/', Path.DirectorySeparatorChar), ["content"] = "x" }, "wrote", AgentToolRunner.Harness),
            .. Call("3", ClaudeCodeTools.Write, new() { ["file_path"] = Path.Combine(Path.GetTempPath(), "elsewhere.cs"), ["content"] = "x" }, "written"),
            .. Call("4", AgentTools.CreateFile, new() { ["path"] = "notes/new.txt", ["content"] = "x" }, "wrote", AgentToolRunner.Harness),
            .. Call("5", AgentTools.CreateFile, new() { ["path"] = "../outside.cs", ["content"] = "x" }, "wrote", AgentToolRunner.Harness),
        ];

        var set = Make(tree, view, stream);

        Assert.Equal(
            ["Shop.Pricing.LoyaltyDiscount", "Shop.Pricing.LoyaltyDiscount.Apply", "Shop.Pricing.SeasonalDiscount", "Shop.Pricing.SeasonalDiscount.Apply"],
            set.Nodes.Select(node => Qualified(view, node)).Order(StringComparer.Ordinal));
        Assert.Equal(0, set.Unplaced);
    }

    [Fact]
    public async Task TheGraphsCalls_AreFoundServedAndEdited_ANodeGoneKeptByItsId_EditedFirstThenByHowOften()
    {
        using var tree = await CodebaseContextPackTests.ShopAsync();
        var view = await ViewAsync(tree);
        var total = Id(view, "Shop.Orders.Order.Total");
        var price = Id(view, "Shop.Pricing.PriceCalculator.Price");
        var apply = Id(view, "Shop.Pricing.LoyaltyDiscount.Apply");
        GraphStepLine[] graph =
        [
            new GraphCallLine("node", GraphToolOutcome.Answered, [total], [], []),
            new GraphCallLine("find", GraphToolOutcome.Answered, [], [], []),
            new GraphCallLine("body", GraphToolOutcome.Answered, [price], [price], []),
            new GraphCallLine("related", GraphToolOutcome.Answered, [price], [], []),
            new GraphCallLine("edit", GraphToolOutcome.Wrote, [], [], [apply], new GraphWrittenLines(Loyalty, 8, 4)),
            new GraphCallLine("node", GraphToolOutcome.Answered, ["gone:node"], [], []),
        ];

        var set = CodebaseWorkingSet.Make(view, [], graph, tree.Harness.FileSystem, tree.Harness.Platform.PathComparison);

        // Price, touched three times, above Total, touched once and first; Total above the gone node, touched once and last.
        Assert.Equal(
            [(apply, new[] { WorkingSetUses.Edited }, 1), (price, [WorkingSetUses.Served, WorkingSetUses.Found], 3), (total, [WorkingSetUses.Found], 1), ("gone:node", [WorkingSetUses.Found], 1)],
            set.Nodes.Select(node => (node.Id, node.Uses.ToArray(), node.Times)));
        Assert.Equal(new WorkingSetNode("gone:node", [WorkingSetUses.Found], 1, Gone: true), set.Nodes[^1], new NodeComparer());
        Assert.Null(set.Problem());
    }

    [Fact]
    public async Task AGraphEdit_IsMatchedByTheLinesItWrote_TheNodeItNamedKeptWhereTheyDeclareItNoLonger()
    {
        using var tree = await CodebaseContextPackTests.ShopAsync();
        var view = await ViewAsync(tree);
        var apply = Id(view, "Shop.Pricing.LoyaltyDiscount.Apply");
        const string Renamed = "csharp:Shop.Pricing.LoyaltyDiscount.Applied(Order,decimal)";
        GraphStepLine[] graph =
        [
            new GraphCallLine("edit", GraphToolOutcome.Wrote, [], [], [apply], new GraphWrittenLines(Loyalty, 8, 4)),
            new GraphCallLine("edit", GraphToolOutcome.Wrote, [], [], [Renamed], new GraphWrittenLines(Loyalty, 7, 2)),
            new GraphCallLine("edit", GraphToolOutcome.Wrote, [], [], ["gone:removed"], new GraphWrittenLines(Loyalty, 9, 0)),
        ];

        var set = CodebaseWorkingSet.Make(view, [], graph, tree.Harness.FileSystem, tree.Harness.Platform.PathComparison);

        // Apply by the lines of both edits it was in, once each; the type by line 7 alone; the names the lines no longer declare kept, gone.
        Assert.Equal(
            [(apply, 2, false), (Id(view, Type), 1, false), (Renamed, 1, true), ("gone:removed", 1, true)],
            set.Nodes.Select(node => (node.Id, node.Times, node.Gone)));
        Assert.All(set.Nodes, node => Assert.Equal(WorkingSetUses.Edited, Assert.Single(node.Uses)));
    }

    [Fact]
    public async Task AGraphEditsLines_AreFoundByTheirHashWhereALaterWriteMovedThem_ElseByWhereItWroteThem()
    {
        using var tree = await CodebaseContextPackTests.ShopAsync();
        var view = await ViewAsync(tree);
        var lines = CodeFileText.Lines(await File.ReadAllTextAsync(Path.Combine(tree.Tree, Loyalty), _token));
        var hash = CodeGraphIds.Digest(lines, 8, 11)[..8];

        // Apply's lines, written at 1 to 4 and moved to 8 to 11 since; written at 7 and 8, by a hash that stands nowhere now.
        var moved = CodebaseWorkingSet.Make(view, [], [new GraphCallLine("edit", GraphToolOutcome.Wrote, [], [], ["gone:old"], new GraphWrittenLines(Loyalty, 1, 4, hash))], tree.Harness.FileSystem, tree.Harness.Platform.PathComparison);
        var stale = CodebaseWorkingSet.Make(view, [], [new GraphCallLine("edit", GraphToolOutcome.Wrote, [], [], ["gone:old"], new GraphWrittenLines(Loyalty, 7, 2, "00000000"))], tree.Harness.FileSystem, tree.Harness.Platform.PathComparison);

        // A blank line's hash stands at lines 2 and 4: more than once, so where it was written - line 7, the type's.
        var twice = CodebaseWorkingSet.Make(view, [], [new GraphCallLine("edit", GraphToolOutcome.Wrote, [], [], ["gone:old"], new GraphWrittenLines(Loyalty, 7, 1, CodeGraphIds.Digest([""], 1, 1)[..8]))], tree.Harness.FileSystem, tree.Harness.Platform.PathComparison);

        Assert.Equal([Id(view, "Shop.Pricing.LoyaltyDiscount.Apply"), "gone:old"], moved.Nodes.Select(node => node.Id));
        Assert.Equal([Id(view, Type), Id(view, "Shop.Pricing.LoyaltyDiscount.Apply"), "gone:old"], stale.Nodes.Select(node => node.Id));
        Assert.Equal([Id(view, Type), "gone:old"], twice.Nodes.Select(node => node.Id));
        Assert.Equal(["", ""], new[] { lines[1], lines[3] });
    }

    [Fact]
    public async Task ABody_ServesANode_WhereItsAnswerGivesALineOfIt_WithinItsBoundAndItsCap()
    {
        var wide = "    // " + new string('w', 120) + "\n";
        var many = string.Concat(Enumerable.Repeat("        Count++;\n", CodebaseGraphTool.BodyLines - 3));
        using var tree = await CodebaseGraphToolTests.TreeAsync(
            ("src/Long.cs", "namespace Demo;\n\npublic static class Long\n{\n" + string.Concat(Enumerable.Repeat(wide, 300)) + "}\n"),
            ("src/Big.cs", "namespace Demo;\n\npublic static class Big\n{\n    public static int Count;\n\n    public static void Run()\n    {\n" + many + "    }\n}\n"));
        var queries = await CodebaseGraphToolTests.QueriesAsync(tree);
        var tool = CodebaseGraphToolTests.Tool(tree, queries);
        var (run, twice, wordy) = (Id(queries, "Demo.Big.Run"), Id(queries, "Demo.Geometry.Twice"), Id(queries, "Demo.Long"));

        async Task<GraphToolFacts> BodyAsync(params string[] symbols)
            => (await tool.RunAsync(new JsonObject { ["op"] = "body", ["symbols"] = new JsonArray([.. symbols.Select(symbol => JsonValue.Create(symbol))]), ["full"] = true }, CodebaseGraphTool.Operations(write: false, narrow: false), _token)).Facts;

        // Run's lines are the bound's every one: Twice is asked about, and cut before a line of it is given.
        var bound = await BodyAsync("Demo.Big.Run", "Demo.Geometry.Twice");

        // Long's lines pass the cap: Twice after it is cut away whole; before it, both are given.
        var capped = await BodyAsync("Demo.Long", "Demo.Geometry.Twice");
        var both = await BodyAsync("Demo.Geometry.Twice", "Demo.Long");

        Assert.Equal([run, twice], bound.Asked);
        Assert.Equal([run], bound.Served);
        Assert.Equal([wordy, twice], capped.Asked);
        Assert.Equal([wordy], capped.Served);
        Assert.Equal([twice, wordy], both.Asked);
        Assert.Equal([twice, wordy], both.Served);
    }

    [Fact]
    public async Task AWorkingSet_KeepsAtMostItsCap_CountingTheRest()
    {
        using var tree = await CodebaseContextPackTests.ShopAsync();
        var view = await ViewAsync(tree);
        GraphStepLine[] graph = [new GraphCallLine("node", GraphToolOutcome.Answered, [.. Enumerable.Range(0, CodebaseWorkingSet.MostNodes + 5).Select(at => $"gone:{at:D3}")], [], [])];

        var set = CodebaseWorkingSet.Make(view, [], graph, tree.Harness.FileSystem, tree.Harness.Platform.PathComparison);

        Assert.Equal(CodebaseWorkingSet.MostNodes, set.Nodes.Count);
        Assert.Equal(5, set.LeftOut);
        Assert.Equal("gone:000", set.Nodes[0].Id);
    }

    [Fact]
    public void AWorkingSetThatDoesNotReadAsOne_SaysWhy()
    {
        var held = new WorkingSetNode("a", [WorkingSetUses.Read], 1, "abc123", "src/a.cs", 1, 2, "12345678");

        Assert.Null(new CodebaseWorkingSet([held]).Problem());
        Assert.Equal("a node is named by its id", new CodebaseWorkingSet([held with { Id = " " }]).Problem());
        Assert.Equal("'a' is used as each of edited, served, read, found once at most, in that order, and as one at least", new CodebaseWorkingSet([held with { Uses = [] }]).Problem());
        Assert.Equal("'a' is used as each of edited, served, read, found once at most, in that order, and as one at least", new CodebaseWorkingSet([held with { Uses = [WorkingSetUses.Read, WorkingSetUses.Edited] }]).Problem());
        Assert.Equal("'a' is used as each of edited, served, read, found once at most, in that order, and as one at least", new CodebaseWorkingSet([held with { Uses = ["looked"] }]).Problem());
        Assert.Equal("'a' is touched at least once for each of its uses", new CodebaseWorkingSet([held with { Uses = [WorkingSetUses.Edited, WorkingSetUses.Read] }]).Problem());
        Assert.Equal("'a' is gone, and kept by its id alone", new CodebaseWorkingSet([held with { Gone = true }]).Problem());
        Assert.Equal("'a' is gone, and kept by its id alone", new CodebaseWorkingSet([new WorkingSetNode("a", [WorkingSetUses.Read], 1, Start: 1, Gone: true)]).Problem());
        Assert.Equal("'a' is held, and named by its handle, its file and its lines", new CodebaseWorkingSet([held with { Path = null }]).Problem());
        Assert.Equal("'a' is held, and named by its handle, its file and its lines", new CodebaseWorkingSet([held with { End = 0 }]).Problem());
        Assert.Equal("'a' is in the working set twice", new CodebaseWorkingSet([held, held]).Problem());
        Assert.Equal("a count is never below 0", new CodebaseWorkingSet([held], Unplaced: -1).Problem());
        Assert.Equal("a working set keeps at most 200 nodes", new CodebaseWorkingSet([.. Enumerable.Range(0, 201).Select(at => held with { Id = $"n{at}" })]).Problem());
    }

    [Fact]
    public void AGraphLineThatDoesNotReadAsOne_SaysWhy()
    {
        const string Wrote = "a call that wrote edited one node and names the lines it wrote, and one that wrote nothing neither";
        var line = new GraphCallLine("edit", GraphToolOutcome.Wrote, [], [], ["n1"], new GraphWrittenLines("src/a.cs", 3, 0));

        Assert.Null(line.Problem());
        Assert.Null((line with { Outcome = GraphToolOutcome.Refused, Edited = [], Wrote = null }).Problem());
        Assert.Equal("a call names its operation, unless it was refused as misused", (line with { Op = " " }).Problem());
        Assert.Null(new GraphCallLine("", GraphToolOutcome.Usage, [], [], []).Problem());
        Assert.Equal(new GraphCallLine("", GraphToolOutcome.Usage, [], [], []), GraphCallLine.Of(null, new GraphToolAnswer("op is missing", GraphToolOutcome.Usage)) with { Asked = [], Served = [], Edited = [] });
        Assert.Equal("'99' is no way a call ends", (line with { Outcome = (GraphToolOutcome)99 }).Problem());
        Assert.Equal("a node is named by its id", (line with { Asked = [""] }).Problem());
        Assert.Equal("a node is named by its id", (line with { Served = [" "] }).Problem());
        Assert.Equal("a node is named by its id", (line with { Edited = [" "] }).Problem());
        Assert.Equal(Wrote, (line with { Edited = [] }).Problem());
        Assert.Equal(Wrote, (line with { Edited = ["n1", "n2"] }).Problem());
        Assert.Equal(Wrote, (line with { Outcome = GraphToolOutcome.Answered }).Problem());
        Assert.Equal(Wrote, (line with { Wrote = null }).Problem());
        Assert.Equal(Wrote, (line with { Outcome = GraphToolOutcome.Answered, Edited = [] }).Problem());
        Assert.Equal(Wrote, (line with { Outcome = GraphToolOutcome.Answered, Edited = ["n1", "n2"], Wrote = null }).Problem());
        Assert.Equal("an edit names the file it wrote", (line with { Wrote = new GraphWrittenLines(" ", 3, 1) }).Problem());
        Assert.Equal("an edit's lines start at line 1 at the earliest, and are never fewer than none", (line with { Wrote = new GraphWrittenLines("src/a.cs", 0, 1) }).Problem());
        Assert.Equal("an edit's lines start at line 1 at the earliest, and are never fewer than none", (line with { Wrote = new GraphWrittenLines("src/a.cs", 1, -1) }).Problem());
        Assert.Null((line with { Wrote = new GraphWrittenLines("src/a.cs", 1, 2, "0123abcd") }).Problem());
        const string Hashed = "an edit's lines are named by a hash of eight lowercase hexadecimal digits, and no lines by none";
        Assert.Equal(Hashed, (line with { Wrote = new GraphWrittenLines("src/a.cs", 1, 0, "0123abcd") }).Problem());
        Assert.Equal(Hashed, (line with { Wrote = new GraphWrittenLines("src/a.cs", 1, 2, "0123ABCD") }).Problem());
        Assert.Equal(Hashed, (line with { Wrote = new GraphWrittenLines("src/a.cs", 1, 2, "0123abc") }).Problem());
    }

    [Fact]
    public void TheSeedsOfWorkingSets_AreEachNodeOnceInTheOrderGiven_EditedWhereAnySaysSo_AndNoNodeGone()
    {
        var read = new WorkingSetNode("a", [WorkingSetUses.Read], 1, "aaaaaa", "a.cs", 1, 1);
        var edited = new WorkingSetNode("a", [WorkingSetUses.Edited], 1, "aaaaaa", "a.cs", 1, 1);
        var other = new WorkingSetNode("b", [WorkingSetUses.Found], 1, "bbbbbb", "b.cs", 1, 1);
        var gone = new WorkingSetNode("c", [WorkingSetUses.Edited], 1, Gone: true);

        Assert.Equal(
            [new ContextPackWorked("a", Edited: true), new ContextPackWorked("b", Edited: false)],
            CodebaseWorkingSet.Seeds([new CodebaseWorkingSet([read, gone]), new CodebaseWorkingSet([other, edited])]));
        Assert.Equal([new ContextPackWorked("a", Edited: true)], CodebaseWorkingSet.Seeds([new CodebaseWorkingSet([edited]), new CodebaseWorkingSet([read])]));
    }

    [Fact]
    public async Task AWorkingSet_IsKeptInItsStepsDirectory_AndReadBack_AndOneThatDoesNotReadIsRefused()
    {
        using var tree = await CodebaseContextPackTests.ShopAsync();
        var directory = tree.Run.InstanceDirectory("implement", "a1");
        var set = new CodebaseWorkingSet([new WorkingSetNode("a", [WorkingSetUses.Edited, WorkingSetUses.Read], 2, "abc123", "src/a.cs", 3, 9, "12345678"), new WorkingSetNode("g", [WorkingSetUses.Found], 1, Gone: true)], LeftOut: 1, Unplaced: 2);

        Assert.Null(tree.Store.ReadWorkingSet(directory));
        tree.Store.WriteWorkingSet(directory, set);
        var read = tree.Store.ReadWorkingSet(directory)!;

        Assert.Equal(set.Nodes, read.Nodes, new NodeComparer());
        Assert.Equal((1, 2), (read.LeftOut, read.Unplaced));
        Assert.Equal(Path.Combine(directory, OrchestratorLayout.StepWorkingSetFileName), Path.Combine(directory, "working-set.json"));
        Assert.Throws<InvalidOperationException>(() => tree.Store.WriteWorkingSet(directory, set with { Unplaced = -1 }));

        await File.WriteAllTextAsync(Path.Combine(directory, OrchestratorLayout.StepWorkingSetFileName), """{"nodes": [{"id": "a", "uses": ["read"], "times": 1, "gone": true, "path": "x"}]}""", _token);
        var refused = Assert.Throws<HarnessException>(() => tree.Store.ReadWorkingSet(directory));
        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Contains("'a' is gone, and kept by its id alone", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheGraphsLines_AreAppendedAndReadBackInOrder_ATornLastLineSetAside_AWrongOneRefusedByItsNumber()
    {
        using var tree = await CodebaseContextPackTests.ShopAsync();
        var directory = tree.Run.InstanceDirectory("implement", "a1");
        GraphStepLine first = new GraphCallLine("find", GraphToolOutcome.Answered, [], [], []);
        GraphStepLine second = new GraphCallLine("edit", GraphToolOutcome.Wrote, [], [], ["n1"], new GraphWrittenLines("src/a.cs", 3, 2));

        Assert.Empty(tree.Store.ReadGraphStepLines(directory));
        tree.Store.AppendGraphStepLine(directory, first);
        tree.Store.AppendGraphStepLine(directory, second);
        await File.AppendAllTextAsync(Path.Combine(directory, OrchestratorLayout.StepGraphFileName), """{"kind":"call","op":""", _token);

        var lines = tree.Store.ReadGraphStepLines(directory);

        Assert.Equal(2, lines.Count);
        Assert.Equal(["n1"], Assert.IsType<GraphCallLine>(lines[1]).Edited);
        Assert.Equal(new GraphWrittenLines("src/a.cs", 3, 2), Assert.IsType<GraphCallLine>(lines[1]).Wrote);
        Assert.Equal("find", Assert.IsType<GraphCallLine>(lines[0]).Op);
        Assert.Throws<InvalidOperationException>(() => tree.Store.AppendGraphStepLine(directory, new GraphCallLine("edit", GraphToolOutcome.Answered, [], [], ["n1"])));

        await File.WriteAllTextAsync(Path.Combine(directory, OrchestratorLayout.StepGraphFileName), "{\"kind\":\"call\",\"op\":\"find\",\"outcome\":\"Answered\",\"asked\":[],\"served\":[],\"edited\":[]}\n{\"kind\":\"call\",\"op\":\"edit\",\"outcome\":\"Wrote\",\"asked\":[],\"served\":[],\"edited\":[]}\n", _token);
        var refused = Assert.Throws<HarnessException>(() => tree.Store.ReadGraphStepLines(directory));
        Assert.Equal(
            $"'{Path.Combine(directory, OrchestratorLayout.StepGraphFileName)}' is {OrchestrationStore.GraphStepLinesKind}, and does not read as one: line 2: a call that wrote edited one node and names the lines it wrote, and one that wrote nothing neither. Nothing was changed: a record is never guessed at.",
            refused.Message);
        Assert.Equal(HarnessExit.Refused, refused.ExitCode);

        await File.WriteAllTextAsync(Path.Combine(directory, OrchestratorLayout.StepGraphFileName), "{\"kind\":\"call\",\"op\":\"find\",\"outcome\":\"Answered\",\"asked\":[],\"served\":[],\"edited\":[],\"extra\":1}\n", _token);
        Assert.Contains("line 1: ", Assert.Throws<HarnessException>(() => tree.Store.ReadGraphStepLines(directory)).Message, StringComparison.Ordinal);

        await File.WriteAllTextAsync(Path.Combine(directory, OrchestratorLayout.StepGraphFileName), "{\"kind\":\"call\",\"op\":\"find\",\"outcome\":\"Answered\",\"asked\":[],\"served\":[],\"edited\":[]}\r\nnull\n", _token);
        Assert.Contains("line 2: it holds null", Assert.Throws<HarnessException>(() => tree.Store.ReadGraphStepLines(directory)).Message, StringComparison.Ordinal);

        // A line that names no kind, or not first, is refused as every other wrong line is.
        foreach (var nameless in new[] { "{}", "{\"op\":\"find\",\"kind\":\"call\",\"outcome\":\"Answered\",\"asked\":[],\"served\":[],\"edited\":[]}" })
        {
            await File.WriteAllTextAsync(Path.Combine(directory, OrchestratorLayout.StepGraphFileName), nameless + "\n", _token);
            var unnamed = Assert.Throws<HarnessException>(() => tree.Store.ReadGraphStepLines(directory));
            Assert.Equal(HarnessExit.Refused, unnamed.ExitCode);
            Assert.Contains("line 1: ", unnamed.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task TheStoredStream_IsReadBackEveryAttemptInOrder_ADeltaNeverStored_ATornLastLineSetAside_AWrongOneRefusedByItsNumber()
    {
        using var tree = await CodebaseContextPackTests.ShopAsync();
        var directory = tree.Run.InstanceDirectory("implement", "a1");

        using (var stream = StoredStream.Open(tree.Harness.FileSystem, directory, new Masking()))
        {
            await stream.EmitAsync(new RunStarted(1) { Seq = 1 });
            await stream.EmitAsync(new ToolCallStarted("t1", ClaudeCodeTools.Read, AgentToolRunner.ClaudeCode) { Seq = 2 });
            await stream.EmitAsync(new TextDelta("x") { Seq = 3 });
            await stream.EmitAsync(new RunFinished(AgentRunStatus.Completed) { Seq = 4 });
            await stream.EmitAsync(new RunStarted(2) { Seq = 1 });
        }

        await File.AppendAllTextAsync(Path.Combine(directory, StoredStream.FileName), "{\"kind\":\"runFin", _token);

        var events = tree.Store.ReadStepStream(directory);

        Assert.Equal([typeof(RunStarted), typeof(ToolCallStarted), typeof(RunFinished), typeof(RunStarted)], events.Select(one => one.GetType()));
        Assert.Equal(2, Assert.IsType<RunStarted>(events[^1]).Attempt);
        Assert.Empty(tree.Store.ReadStepStream(tree.Run.InstanceDirectory("never", null)));

        await File.AppendAllTextAsync(Path.Combine(directory, StoredStream.FileName), "ished\"}\n", _token);
        var refused = Assert.Throws<HarnessException>(() => tree.Store.ReadStepStream(directory));
        Assert.StartsWith($"'{Path.Combine(directory, StoredStream.FileName)}' is {OrchestrationStore.StepStreamKind}, and does not read as one: line 5: ", refused.Message, StringComparison.Ordinal);

        await File.WriteAllTextAsync(Path.Combine(directory, StoredStream.FileName), "{}\n", _token);
        var nameless = Assert.Throws<HarnessException>(() => tree.Store.ReadStepStream(directory));
        Assert.Equal(HarnessExit.Refused, nameless.ExitCode);
        Assert.StartsWith($"'{Path.Combine(directory, StoredStream.FileName)}' is {OrchestrationStore.StepStreamKind}, and does not read as one: line 1: ", nameless.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheGraphTool_SaysWhatItAskedAboutServedAndEdited_AndItsLineSaysItToo()
    {
        using var tree = await CodebaseGraphToolTests.TreeAsync(CodebaseGraphToolTests.Demo);
        var queries = await CodebaseGraphToolTests.QueriesAsync(tree);
        var tool = CodebaseGraphToolTests.Tool(tree, queries);
        var fold = Assert.IsType<GraphResolve.Found>(queries.Resolve("Demo.Geometry.Twice")).Card;

        var every = CodebaseGraphTool.Operations(write: true, narrow: true);
        var node = await tool.RunAsync(new JsonObject { ["op"] = "node", ["symbol"] = fold.Handle }, every, _token);
        var related = await tool.RunAsync(new JsonObject { ["op"] = "related", ["symbol"] = fold.Handle, ["relation"] = "callers" }, every, _token);
        var body = await tool.RunAsync(new JsonObject { ["op"] = "body", ["symbols"] = new JsonArray(fold.Handle, "nothing.held") }, every, _token);
        var find = await tool.RunAsync(new JsonObject { ["op"] = "find", ["query"] = "Twice" }, every, _token);

        Assert.Equal(new GraphCallLine("node", GraphToolOutcome.Answered, [fold.Id], [], []), GraphCallLine.Of("node", node), new LineComparer());
        Assert.Equal(new GraphCallLine("related", GraphToolOutcome.Answered, [fold.Id], [], []), GraphCallLine.Of("related", related), new LineComparer());
        Assert.Equal(new GraphCallLine("body", GraphToolOutcome.Answered, [fold.Id], [fold.Id], []), GraphCallLine.Of("body", body), new LineComparer());
        Assert.Equal(GraphToolFacts.None, find.Facts);

        var edit = await tool.RunAsync(new JsonObject { ["op"] = "edit", ["symbol"] = fold.Handle, ["hash"] = fold.Hash, ["text"] = "    public static double Twice(IShape shape) => 0;" }, every, _token);
        var refused = await tool.RunAsync(new JsonObject { ["op"] = "edit", ["symbol"] = fold.Handle, ["hash"] = fold.Hash, ["text"] = "    public static double Twice(IShape shape) => 1;" }, every, _token);

        var written = CodeFileText.Lines(await File.ReadAllTextAsync(Path.Combine(tree.Tree, "src", "Geometry.cs"), _token));
        Assert.Equal("    public static double Twice(IShape shape) => 0;", written[15]);
        Assert.Equal(new GraphCallLine("edit", GraphToolOutcome.Wrote, [], [], [fold.Id], new GraphWrittenLines("src/Geometry.cs", 16, 1, CodeGraphIds.Digest(written, 16, 16)[..8])), GraphCallLine.Of("edit", edit), new LineComparer());
        Assert.Empty(GraphCallLine.Of("edit", refused).Edited);
        Assert.Null(GraphCallLine.Of("edit", refused).Problem());
    }

    /// <summary>A step whose pack is given the working set of the step before it holds what that step edited, an edited node above the rest.</summary>
    [Fact]
    public async Task AStepsPack_HoldsWhatTheStepBeforeItEdited()
    {
        using var tree = await CodebaseContextPackTests.ShopAsync();
        tree.Write(Loyalty, (await File.ReadAllTextAsync(Path.Combine(tree.Tree, Loyalty), _token)).Replace("total * 0.95m", "total * 0.90m", StringComparison.Ordinal));
        var view = await ViewAsync(tree, Loyalty);
        tree.Store.WriteWorkingSet(tree.Run.InstanceDirectory("implement", "a1"), Make(tree, view, Call("1", ClaudeCodeTools.Edit, new() { ["file_path"] = Full(tree, Loyalty), ["new_string"] = "total * 0.90m" }, "updated")));

        var seeds = FlowWorkingSets.SeedsOf(tree.Store, tree.Run, [new FlowWorkedFrom("implement", "a1"), new FlowWorkedFrom("never-ran", null)]);
        var pack = CodebaseContextPack.Make(view, new ContextPackRequest("Make the tests pass.", _ => true) { Worked = seeds });

        Assert.Equal([new ContextPackWorked(Id(view, "Shop.Pricing.LoyaltyDiscount.Apply"), Edited: true)], seeds);
        Assert.Contains(pack.Text.Split('\n'), line => line.StartsWith(view.Card(seeds[0].Id).Handle + " ", StringComparison.Ordinal) && line.EndsWith(" | edited before", StringComparison.Ordinal));
        Assert.Contains("; packed from the task: 1 working set node(s)", pack.Text.Split('\n')[0], StringComparison.Ordinal);
    }

    /// <summary>A fix round's step is packed first from what the round before it edited and what its reviewers read.</summary>
    [Fact]
    public async Task AFixRoundsStepsPack_StartsFromWhatTheRoundBeforeEditedAndRead()
    {
        using var tree = await CodebaseContextPackTests.ShopAsync();
        var view = await ViewAsync(tree);
        var reviewer = tree.Run.InstanceDirectory("fix#2.review.correctness", null);
        var rework = tree.Run.InstanceDirectory("fix#1.revise.rework", "a1");
        tree.Store.WriteWorkingSet(reviewer, Make(tree, view, Call("1", ClaudeCodeTools.Read, new() { ["file_path"] = Full(tree, "src/Pricing/PriceCalculator.cs") }, "8\tx\n18\tx")));
        tree.Store.WriteWorkingSet(rework, Make(tree, view, Call("1", AgentTools.InsertInFile, new() { ["path"] = Loyalty, ["line"] = 10, ["text"] = "zzz" }, "edited", AgentToolRunner.Harness)));

        var seeds = FlowWorkingSets.SeedsOf(tree.Store, tree.Run, [new FlowWorkedFrom("implement", "a1"), new FlowWorkedFrom("fix#2.review.correctness", null), new FlowWorkedFrom("fix#1.revise.rework", "a1")]);
        var pack = CodebaseContextPack.Make(view, new ContextPackRequest("Address the findings.", _ => true) { Worked = seeds });

        Assert.Equal(
            [("Shop.Pricing.PriceCalculator.Price", false), ("Shop.Pricing.LoyaltyDiscount.Apply", true)],
            seeds.Select(seed => (view.Card(seed.Id).Qualified, seed.Edited)));
        Assert.EndsWith(" | used before", Line(pack, view, "Shop.Pricing.PriceCalculator.Price"), StringComparison.Ordinal);
        Assert.EndsWith(" | edited before", Line(pack, view, "Shop.Pricing.LoyaltyDiscount.Apply"), StringComparison.Ordinal);
        Assert.Equal([view.Card(seeds[1].Id).Id, view.Card(seeds[0].Id).Id], pack.Seeds.Take(2).Select(seed => seed.Id).Reverse());
    }

    private CodebaseWorkingSet Make(CodebaseGraphTree tree, CodebaseGraphQueries view, IReadOnlyList<AgentEvent> stream)
        => CodebaseWorkingSet.Make(view, stream, [], tree.Harness.FileSystem, tree.Harness.Platform.PathComparison);

    private static AgentEvent[] Call(string id, string tool, JsonObject input, string result, AgentToolRunner runner = AgentToolRunner.ClaudeCode, bool ok = true)
        => [new ToolCallStarted(id, tool, runner), new ToolCallFinished(id, ok, TimeSpan.Zero, input.ToJsonString(), result)];

    private static AgentEvent[] Call(string id, string tool, JsonObject input, string result, bool ok)
        => Call(id, tool, input, result, AgentToolRunner.ClaudeCode, ok);

    private static string Full(CodebaseGraphTree tree, string path) => Path.Combine(tree.Tree, path.Replace('/', Path.DirectorySeparatorChar));

    private static async Task<CodebaseGraphQueries> ViewAsync(CodebaseGraphTree tree, params string[] changed)
        => new((await tree.CheckAsync(null, null, changed)).Read);

    private static string Id(CodebaseGraphQueries view, string qualified) => Assert.IsType<GraphResolve.Found>(view.Resolve(qualified)).Card.Id;

    private static string Qualified(CodebaseGraphQueries view, WorkingSetNode node) => view.Card(node.Id).Qualified;

    private static string Line(ContextPack pack, CodebaseGraphQueries view, string qualified)
        => pack.Text.Split('\n').Single(line => line.StartsWith(view.Card(Id(view, qualified)).Handle + " ", StringComparison.Ordinal));

    /// <summary>Nodes alike member by member, their uses by their items.</summary>
    private sealed class NodeComparer : IEqualityComparer<WorkingSetNode>
    {
        public bool Equals(WorkingSetNode? x, WorkingSetNode? y)
            => x is not null && y is not null && (x with { Uses = [] }) == (y with { Uses = [] }) && x.Uses.SequenceEqual(y.Uses);

        public int GetHashCode(WorkingSetNode obj) => obj.Id.GetHashCode(StringComparison.Ordinal);
    }

    /// <summary>Call lines alike member by member, their lists by their items.</summary>
    private sealed class LineComparer : IEqualityComparer<GraphCallLine>
    {
        public bool Equals(GraphCallLine? x, GraphCallLine? y)
            => x is not null && y is not null && x.Op == y.Op && x.Outcome == y.Outcome && x.Asked.SequenceEqual(y.Asked) && x.Served.SequenceEqual(y.Served) && x.Edited.SequenceEqual(y.Edited) && Equals(x.Wrote, y.Wrote);

        public int GetHashCode(GraphCallLine obj) => obj.Op.GetHashCode(StringComparison.Ordinal);
    }
}
