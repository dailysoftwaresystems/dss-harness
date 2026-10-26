using System.Text;
using DssHarness.Core.Codebase;
using DssHarness.Core.Results;

namespace DssHarness.Tests;

/// <summary>The context pack (value 370) and change-first (value 371): <see cref="CodebaseContextPack"/> over a synced view.</summary>
public sealed class CodebaseContextPackTests
{
    private readonly CancellationToken _token = TestContext.Current.CancellationToken;

    /// <summary>The names of the C# shop's tasks.</summary>
    public static TheoryData<string> ShopTasks => [.. ContextPackFixtures.CSharpTasks.Select(task => task.Name)];

    /// <summary>
    /// Measurement 1 over the C# repository: each task's pack holds the declarations its reference answer needs, at the
    /// count stored; under change-first the changed nodes lead, then their tests.
    /// </summary>
    [Theory]
    [MemberData(nameof(ShopTasks))]
    public async Task EachShopTask_IsPackedWithTheDeclarationsItsAnswerNeeds_AtTheCountStored(string name)
    {
        var task = ContextPackFixtures.CSharpTasks.Single(one => one.Name == name);
        using var tree = await ShopAsync();
        var (queries, changes) = await ChangedAsync(tree, task.Change);

        var pack = CodebaseContextPack.Make(queries, new ContextPackRequest(task.Text, All) { From = task.Change is null ? ContextPackFrom.Task : ContextPackFrom.Changes, Changes = changes });

        var tight = CodebaseContextPack.Make(queries, new ContextPackRequest(task.Text, All) { From = task.Change is null ? ContextPackFrom.Task : ContextPackFrom.Changes, Changes = changes, Tokens = ContextPackFixtures.TightTokens });

        var held = task.Needed.Where(qualified => pack.Nodes.Any(id => queries.Card(id).Qualified == qualified)).ToList();
        Assert.True(held.Count == task.Held, $"{task.Name} holds {held.Count} of [{string.Join(", ", task.Needed)}], stored {task.Held}:\n{pack.Text}");
        Assert.True(pack.Tokens <= CodebaseContextPack.DefaultTokens, pack.Text);
        var heldTight = task.Needed.Count(qualified => tight.Nodes.Any(id => queries.Card(id).Qualified == qualified));
        Assert.True(heldTight == task.HeldTight && tight.LeftOut > 0, $"{task.Name} holds {heldTight} under {ContextPackFixtures.TightTokens} tokens, stored {task.HeldTight}, {tight.LeftOut} left out:\n{tight.Text}");

        if (task.Change is { } change)
        {
            var lines = pack.Text.Split('\n');
            Assert.Equal("changed:", lines[1]);
            Assert.Contains(change.Path, lines[2], StringComparison.Ordinal);
            Assert.Equal("their tests:", lines[3]);
            Assert.Contains(" | tests ", lines[4], StringComparison.Ordinal);
            Assert.Equal(task.Needed[0], queries.Card(pack.Nodes[0]).Qualified);
            Assert.Equal(task.Needed[1], queries.Card(pack.Nodes[1]).Qualified);
        }
    }

    [Fact]
    public async Task ThePack_IsTheSameEveryTime_FromOneViewAndOnePrompt()
    {
        using var tree = await ShopAsync();
        var request = new ContextPackRequest(ContextPackFixtures.CSharpTasks[1].Text, All) { Writes = Covered("src/Checkout/**") };

        var first = CodebaseContextPack.Make(await QueriesAsync(tree), request);
        var again = CodebaseContextPack.Make(await QueriesAsync(tree), request);

        Assert.Equal(first.Text, again.Text);
        Assert.Equal(first.Nodes, again.Nodes);
        Assert.Equal(first.Tokens, EstimatedTokens.Of(first.Text));
        Assert.Equal(first.Text.Split('\n')[1..^1], first.Lines.Select(line => line.Text));
        Assert.Equal(first.Nodes, first.Lines.Select(line => line.Id).Distinct());
    }

    [Fact]
    public async Task ANodeSeededTwice_IsRecordedByItsHeaviestSeed_TheOneItsLineSays()
    {
        using var tree = await ShopAsync();
        var queries = await QueriesAsync(tree);
        var total = Id(queries, "Shop.Orders.Order.Total");
        var declared = queries.OutlineFile("src/Orders/Order.cs")!.Count(card => CodeNodeKinds.Declarations.Contains(card.Kind));

        var edited = CodebaseContextPack.Make(queries, new ContextPackRequest("`Order.Total`", All) { Worked = [new(total, Edited: true)] });
        var filed = CodebaseContextPack.Make(queries, new ContextPackRequest("`src/Orders/Order.cs` and `Order.Total`", All));

        Assert.EndsWith(" | edited before", Line(edited, queries, "Shop.Orders.Order.Total"), StringComparison.Ordinal);
        Assert.Equal("edited before", Assert.Single(edited.Seeds, seed => seed.Id == total).Why);
        Assert.Contains("; packed from the task: 1 working set node(s), the ", edited.Text.Split('\n')[0], StringComparison.Ordinal);
        Assert.EndsWith(" | named", Line(filed, queries, "Shop.Orders.Order.Total"), StringComparison.Ordinal);
        Assert.Equal("named", Assert.Single(filed.Seeds, seed => seed.Id == total).Why);
        Assert.Contains($"; packed from the task: 1 node(s) it names, {declared - 1} declaration(s) of the files it names, the ", filed.Text.Split('\n')[0], StringComparison.Ordinal);
        Assert.Equal(filed.Seeds.Count, filed.Seeds.Select(seed => seed.Id).Distinct().Count());
    }

    [Fact]
    public async Task TheSeeds_AreWhatThePromptNamesExactly_InBackquotesOrAsAQualifiedNameAHandleOrAPathAndLine()
    {
        using var tree = await ShopAsync();
        var queries = await QueriesAsync(tree);
        var handle = queries.Card(Id(queries, "Shop.Storage.MemoryOrderStore.All")).Handle;
        var prompt = $"""
            Look at `SalesReport.Sales`, at `src/Orders/OrderLine.cs`, at `src/Checkout/Receipt.cs:4` and at Shop.Pricing.ICustomerHistory.HasOrdered.
            The last answer named {handle}.
            src/Storage/IOrderStore.cs(8,9): error CS0001: something
               at Shop.Tests.OrderTests.Total_AddsEveryLine() in C:\agent\_work\1\s\tests\Shop.Tests\OrderTests.cs:line 10
            """;

        var pack = CodebaseContextPack.Make(queries, new ContextPackRequest(prompt, All) { Tokens = CodebaseContextPack.MostTokens });

        Assert.EndsWith(" | named", Line(pack, queries, "Shop.Reports.SalesReport.Sales"), StringComparison.Ordinal);
        Assert.EndsWith(" | in a file named", Line(pack, queries, "Shop.Orders.OrderLine.Amount"), StringComparison.Ordinal);
        Assert.EndsWith(" | named", Line(pack, queries, "Shop.Checkout.Receipt"), StringComparison.Ordinal);
        Assert.EndsWith(" | named", Line(pack, queries, "Shop.Pricing.ICustomerHistory.HasOrdered"), StringComparison.Ordinal);
        Assert.EndsWith(" | named", Line(pack, queries, "Shop.Storage.MemoryOrderStore.All"), StringComparison.Ordinal);
        Assert.EndsWith(" | named", Line(pack, queries, "Shop.Storage.IOrderStore.Find"), StringComparison.Ordinal);
        Assert.EndsWith(" | named", Line(pack, queries, "Shop.Tests.OrderTests.Total_AddsEveryLine"), StringComparison.Ordinal);
        Assert.StartsWith("codebase graph: view 'base' at ", pack.Text, StringComparison.Ordinal);
        Assert.Contains("; packed from the task: 6 node(s) it names, 5 declaration(s) of the files it names, the ", pack.Text.Split('\n')[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task APathFromAnotherPlace_NamesTheFileTheViewHolds_ByItsLongestEndingHeld_OrTheOneFileEndingWithIt()
    {
        using var tree = await ShopAsync();
        var queries = await QueriesAsync(tree);

        var foreign = CodebaseContextPack.Make(queries, new ContextPackRequest("it threw at C:\\agent\\_work\\1\\s\\src\\Orders\\Order.cs:line 34", All));
        var under = CodebaseContextPack.Make(queries, new ContextPackRequest($"it threw at {Path.Combine(tree.Tree, "src", "Orders", "Order.cs")}:34", All));
        var project = CodebaseContextPack.Make(queries, new ContextPackRequest("Shop.Tests\\OrderTests.cs(10,5): error CS0001: something", All));
        var parameters = CodebaseContextPack.Make(queries, new ContextPackRequest("see `PriceCalculator.Price(Order order)`", All));

        Assert.EndsWith(" | named", Line(foreign, queries, "Shop.Orders.Order.Total"), StringComparison.Ordinal);
        Assert.EndsWith(" | named", Line(under, queries, "Shop.Orders.Order.Total"), StringComparison.Ordinal);
        Assert.EndsWith(" | named", Line(project, queries, "Shop.Tests.OrderTests.Total_AddsEveryLine"), StringComparison.Ordinal);
        Assert.EndsWith(" | named", Line(parameters, queries, "Shop.Pricing.PriceCalculator.Price"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheWordMatches_AreTheTenBestTheStepMayRead()
    {
        using var tree = await TreeAsync(
            ("secret/Gadget.cs", "public class Gadget\n{\n}\n"),
            ("src/Gadgets.cs", string.Concat(Enumerable.Range(1, 12).Select(at => $"public class Gadget{(char)('A' + at - 1)}\n{{\n}}\n\n"))));
        var queries = await QueriesAsync(tree);

        var pack = CodebaseContextPack.Make(queries, new ContextPackRequest("Gadget", path => !path.StartsWith("secret/", StringComparison.Ordinal)));

        Assert.EndsWith($"; packed from the task: the {CodebaseContextPack.WordSeeds} best word match(es)", pack.Text.Split('\n')[0], StringComparison.Ordinal);
        Assert.DoesNotContain("secret/", pack.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ANameSeveralNodesCarry_SeedsEach_WhereAnEdgeCouldNameThemAll_AndNoneWhereMore()
    {
        using var tree = await TreeAsync(
            ("src/A.cs", "public class A\n{\n    public void Go()\n    {\n    }\n}\n"),
            ("src/B.cs", "public class B\n{\n    public void Go()\n    {\n    }\n}\n"));
        var queries = await QueriesAsync(tree);

        var pack = CodebaseContextPack.Make(queries, new ContextPackRequest("see `Go`", All));

        Assert.EndsWith(" | one of 2 named 'Go'", Line(pack, queries, "A.Go"), StringComparison.Ordinal);
        Assert.EndsWith(" | one of 2 named 'Go'", Line(pack, queries, "B.Go"), StringComparison.Ordinal);

        var many = string.Concat(Enumerable.Range(1, CodeConfidences.MostCandidates + 1).Select(at => $"public class G{at}\n{{\n    public void Go()\n    {{\n    }}\n}}\n\n"));
        using var crowded = await TreeAsync(("src/G.cs", many));
        var crowdedQueries = await QueriesAsync(crowded);

        var none = CodebaseContextPack.Make(crowdedQueries, new ContextPackRequest("see `Go`", All));

        Assert.DoesNotContain(" named 'Go'", none.Text, StringComparison.Ordinal);
        Assert.Contains("matches the task", none.Text, StringComparison.Ordinal);

        // A node the step may not read is no candidate, and is counted left out.
        using var hidden = await TreeAsync(
            ("src/A.cs", "public class A\n{\n    public void Go()\n    {\n    }\n}\n"),
            ("src/B.cs", "public class B\n{\n    public void Go()\n    {\n    }\n}\n"),
            ("secret/C.cs", "public class C\n{\n    public void Go()\n    {\n    }\n}\n"));
        var hiddenQueries = await QueriesAsync(hidden);

        var readable = CodebaseContextPack.Make(hiddenQueries, new ContextPackRequest("see `Go`", path => !path.StartsWith("secret/", StringComparison.Ordinal)));

        Assert.EndsWith(" | one of 2 named 'Go'", Line(readable, hiddenQueries, "A.Go"), StringComparison.Ordinal);
        Assert.True(readable.Refused > 0);
    }

    [Fact]
    public async Task TheWalk_GoesTwoHopsFromANamedSeed_AndOneFromAWordMatch()
    {
        using var tree = await TreeAsync(("src/Chain.cs", Chain));
        var queries = await QueriesAsync(tree);

        var named = CodebaseContextPack.Make(queries, new ContextPackRequest("`First`", All));
        var matched = CodebaseContextPack.Make(queries, new ContextPackRequest("first", All));

        Assert.EndsWith(" | named", Line(named, queries, "Chain.First"), StringComparison.Ordinal);
        Assert.Contains(" | called by First", Line(named, queries, "Chain.Second"), StringComparison.Ordinal);
        Assert.Contains(" | called by Second", Line(named, queries, "Chain.Third"), StringComparison.Ordinal);
        Assert.Null(LineOrNull(named, queries, "Chain.Fourth"));
        Assert.EndsWith(" | matches the task", Line(matched, queries, "Chain.First"), StringComparison.Ordinal);
        Assert.Contains(" | called by First", Line(matched, queries, "Chain.Second"), StringComparison.Ordinal);
        Assert.Null(LineOrNull(matched, queries, "Chain.Third"));
    }

    [Fact]
    public async Task EachNode_IsScoredByItsSeedsWeightHalvedAtEachHop_AndByTheEdgesConfidence_GeneratedCodeLast()
    {
        // From the seed: Near (exact, 2), Far through Near (exact, 1), Loose by a call of candidates (0.6), Made in a
        // generated file (exact, 2, but generated) - written by file, each file where its best node stands.
        using var tree = await TreeAsync(
            ("src/Seed.cs", "public static class Seed\n{\n    public static void Start()\n    {\n        Near.Go();\n        Made.Go();\n        Run();\n    }\n}\n"),
            ("src/Near.cs", "public static class Near\n{\n    public static void Go()\n    {\n        Far.Go();\n    }\n}\n"),
            ("src/Far.cs", "public static class Far\n{\n    public static void Go()\n    {\n    }\n}\n"),
            ("src/Loose.cs", "public class L1\n{\n    public void Run()\n    {\n    }\n}\n\npublic class L2\n{\n    public void Run()\n    {\n    }\n}\n"),
            ("src/Made.g.cs", "// <auto-generated/>\npublic static class Made\n{\n    public static void Go()\n    {\n    }\n}\n"));
        var queries = await QueriesAsync(tree);

        var pack = CodebaseContextPack.Make(queries, new ContextPackRequest("`Seed.Start`", All));

        // Written by file, each file where its best node stands.
        var files = pack.Nodes.Select(id => queries.Card(id).Path).Distinct(StringComparer.Ordinal).ToList();
        Assert.Equal(["src/Seed.cs", "src/Near.cs", "src/Far.cs", "src/Loose.cs", "src/Made.g.cs"], files);
        Assert.EndsWith(" | called by Start", Line(pack, queries, "Near.Go"), StringComparison.Ordinal);
        Assert.EndsWith(" | called by Go", Line(pack, queries, "Far.Go"), StringComparison.Ordinal);
        Assert.EndsWith(" | called by Start | candidates", Line(pack, queries, "L1.Run"), StringComparison.Ordinal);
        Assert.EndsWith(", generated | called by Start", Line(pack, queries, "Made.Go"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ANodeTwoHopsFromANamedSeed_ScoresBelowAWordMatch_ForItsScoreIsHalvedAtEachHop()
    {
        // First is named (4): Second, one hop, scores 2; Third.Go, two hops, 1 - below Zebra, a word match (2).
        using var tree = await TreeAsync(
            ("src/Chain.cs", "public static class Chain\n{\n    public static void First()\n    {\n        Second();\n    }\n\n    public static void Second()\n    {\n        Third.Go();\n    }\n}\n"),
            ("src/Third.cs", "public static class Third\n{\n    public static void Go()\n    {\n    }\n}\n"),
            ("src/Zebra.cs", "public sealed class Zebra\n{\n    public int Stripes() => 3;\n}\n"));
        var queries = await QueriesAsync(tree);

        var pack = CodebaseContextPack.Make(queries, new ContextPackRequest("`Chain.First` and the zebra", All));

        Assert.Equal(["src/Chain.cs", "src/Zebra.cs", "src/Third.cs"], pack.Nodes.Select(id => queries.Card(id).Path).Distinct(StringComparer.Ordinal));
        Assert.Contains(" | called by Second", Line(pack, queries, "Third.Go"), StringComparison.Ordinal);
    }

    [Fact]
    public void EachWalkedEdge_IsSaidAsTheNodeItReachesStandsToTheNodeItWasReachedFrom()
    {
        Assert.Equal(
            ["calls Run", "called by Run", "base of Run", "derives from Run", "mixed into Run", "mixes in Run", "implemented by Run", "implements Run", "overridden by Run", "overrides Run", "answers Run", "dispatches to Run", "tests Run", "tested by Run", "named by Run"],
            CodebaseContextPack.Walked.Select(edge => CodebaseContextPack.Phrase(new GraphStep("x", edge.Side, edge.Kind, CodeConfidences.Exact), "Run")));
    }

    [Fact]
    public async Task AHub_IsListedAndNotGoneThrough()
    {
        using var tree = await TreeAsync(("src/Hub.cs", CodebaseGraphToolTests.HubFile(60)));
        var queries = await QueriesAsync(tree);

        var pack = CodebaseContextPack.Make(queries, new ContextPackRequest("`Demo.Hubs.Hub.M0`", All) { Tokens = CodebaseContextPack.MostTokens });

        Assert.EndsWith(" | called by M0 | hub, not gone through its 60 callers", Line(pack, queries, "Demo.Hubs.Hub.Center"), StringComparison.Ordinal);
        Assert.DoesNotContain(" | calls Center", pack.Text, StringComparison.Ordinal);
    }

    /// <summary>A hub named is walked from along every kind, as related walks from the node asked about, and its line says no hub.</summary>
    [Fact]
    public async Task AHubNamed_IsWalkedFrom_AsRelatedWalksFromTheNodeAskedAbout()
    {
        using var tree = await TreeAsync(("src/Hub.cs", CodebaseGraphToolTests.HubFile(60)));
        var queries = await QueriesAsync(tree);

        var pack = CodebaseContextPack.Make(queries, new ContextPackRequest("`Demo.Hubs.Hub.Center`", All) { Tokens = CodebaseContextPack.MostTokens });

        Assert.EndsWith(" | named", Line(pack, queries, "Demo.Hubs.Hub.Center"), StringComparison.Ordinal);
        Assert.Contains(" | called by Center", Line(pack, queries, "Demo.Hubs.Hub.Leaf"), StringComparison.Ordinal);
        Assert.Contains(" | calls Center", Line(pack, queries, "Demo.Hubs.Hub.M59"), StringComparison.Ordinal);
    }

    /// <summary>A node reached is not gone through along the kind it is a hub of, and is along the others.</summary>
    [Fact]
    public async Task AHubReached_IsGoneThroughAlongTheKindsItIsNoHubOf()
    {
        using var tree = await TreeAsync(("src/Hub.cs", CodebaseGraphToolTests.HubFile(60)), ("src/Seed.cs", "public static class Seed\n{\n    public static void Start()\n    {\n        Demo.Hubs.Hub.Center();\n    }\n}\n"));
        var queries = await QueriesAsync(tree);

        var pack = CodebaseContextPack.Make(queries, new ContextPackRequest("`Seed.Start`", All) { Tokens = CodebaseContextPack.MostTokens });

        Assert.Contains(" | called by Start", Line(pack, queries, "Demo.Hubs.Hub.Center"), StringComparison.Ordinal);
        Assert.Contains(" | called by Center", Line(pack, queries, "Demo.Hubs.Hub.Leaf"), StringComparison.Ordinal);
        Assert.Null(LineOrNull(pack, queries, "Demo.Hubs.Hub.M0"));
    }

    [Fact]
    public async Task WhatDoesNotFitTheCap_IsLeftOutWholeAndCounted()
    {
        using var tree = await ShopAsync();
        var queries = await QueriesAsync(tree);
        var whole = CodebaseContextPack.Make(queries, new ContextPackRequest(ContextPackFixtures.CSharpTasks[1].Text, All));

        var cut = CodebaseContextPack.Make(queries, new ContextPackRequest(ContextPackFixtures.CSharpTasks[1].Text, All) { Tokens = 300 });

        Assert.True(cut.Tokens <= 300, cut.Text);
        Assert.True(cut.LeftOut > 0);
        Assert.Equal(whole.Nodes.Count, cut.Nodes.Count + cut.LeftOut);
        Assert.EndsWith($"; {cut.LeftOut} more left out whole, past the cap", cut.Text, StringComparison.Ordinal);
        Assert.All(cut.Text.Split('\n')[1..^1], line => Assert.Contains(line, whole.Text.Split('\n')));
        Assert.EndsWith("; nothing left out", whole.Text, StringComparison.Ordinal);
        Assert.Matches($"\n{whole.Nodes.Count} node\\(s\\), ~{whole.Tokens} of 3000 tokens; nothing left out$", whole.Text);
    }

    [Fact]
    public async Task ACapTooSmallForItsFirstAndLastLines_GivesNoPack_EveryLineLeftOut()
    {
        using var tree = await ShopAsync();

        var pack = CodebaseContextPack.Make(await QueriesAsync(tree), new ContextPackRequest("`Order.Total`", All) { Tokens = 30 });

        Assert.Equal(string.Empty, pack.Text);
        Assert.Empty(pack.Nodes);
        Assert.True(pack.LeftOut > 0);
    }

    [Fact]
    public async Task ASectionsHeader_CountsTowardTheCap_WithItsFirstLine()
    {
        using var tree = await ShopAsync();
        var (queries, changes) = await ChangedAsync(tree, ("src/Pricing/LoyaltyDiscount.cs", "total * 0.95m", "total * 0.90m"));
        var request = new ContextPackRequest("Review it.", All) { From = ContextPackFrom.Changes, Changes = changes, Tokens = CodebaseContextPack.MostTokens };
        var whole = CodebaseContextPack.Make(queries, request);
        var changed = whole.Lines[0].Text;
        var alone = Encoding.UTF8.GetByteCount(whole.Text.Split('\n')[0]) + 1 + CodebaseContextPack.LastLineRoom + Encoding.UTF8.GetByteCount(changed) + 1;

        var narrow = CodebaseContextPack.Make(queries, request with { Tokens = EstimatedTokens.OfBytes(alone) });
        var room = CodebaseContextPack.Make(queries, request with { Tokens = EstimatedTokens.OfBytes(alone + Encoding.UTF8.GetByteCount("changed:") + 1) });

        Assert.DoesNotContain(changed, narrow.Text, StringComparison.Ordinal);
        Assert.Equal(["changed:", changed], room.Text.Split('\n')[1..3]);
    }

    [Fact]
    public async Task ACapOfZero_GivesNoPack()
    {
        using var tree = await ShopAsync();

        var pack = CodebaseContextPack.Make(await QueriesAsync(tree), new ContextPackRequest("`Order.Total`", All) { Tokens = 0 });

        Assert.Equal(string.Empty, pack.Text);
        Assert.Empty(pack.Nodes);
        Assert.Equal(0, pack.Tokens);
        Assert.Equal(0, pack.LeftOut);
        Assert.NotEmpty(pack.Seeds);
    }

    [Fact]
    public async Task TheReadRule_LeavesOutAndCountsANode_NeverNamesIt_AndIsNotGoneThrough()
    {
        // Third is reached from First only through Pass, in a file the step may not read.
        using var tree = await TreeAsync(
            ("src/Chain.cs", "public static class Chain\n{\n    public static void First()\n    {\n        Secret.Pass();\n    }\n\n    public static void Third()\n    {\n    }\n}\n"),
            ("secret/Secret.cs", "public static class Secret\n{\n    public static void Pass()\n    {\n        Chain.Third();\n    }\n}\n"));
        var queries = await QueriesAsync(tree);

        var pack = CodebaseContextPack.Make(queries, new ContextPackRequest("`First` and `Secret.Pass`", path => !path.StartsWith("secret/", StringComparison.Ordinal)));

        Assert.DoesNotContain("secret/", pack.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Pass", pack.Text, StringComparison.Ordinal);
        Assert.EndsWith(" | named", Line(pack, queries, "Chain.First"), StringComparison.Ordinal);
        Assert.Null(LineOrNull(pack, queries, "Chain.Third"));
        Assert.EndsWith("; 2 in files the step may not read left out", pack.Text, StringComparison.Ordinal);
        Assert.Equal(2, pack.Refused);
    }

    [Fact]
    public async Task TheFilesTheStepWrites_SeedTheirDeclarations_AndAWorkingSetItsNodes_AnEditedOneAboveTheRest()
    {
        using var tree = await ShopAsync();
        var queries = await QueriesAsync(tree);
        var used = Id(queries, "Shop.Orders.OrderLine.Amount");
        var edited = Id(queries, "Shop.Reports.SalesReport.Sales");

        var pack = CodebaseContextPack.Make(queries, new ContextPackRequest("nothing here matches", All) { Writes = Covered("src/Storage/Memory*.cs"), Worked = [new(used, Edited: false), new(edited, Edited: true), new("gone:node", Edited: true)] });

        Assert.Equal([edited, used], pack.Nodes.Take(2));
        Assert.EndsWith(" | edited before", Line(pack, queries, "Shop.Reports.SalesReport.Sales"), StringComparison.Ordinal);
        Assert.EndsWith(" | used before", Line(pack, queries, "Shop.Orders.OrderLine.Amount"), StringComparison.Ordinal);
        Assert.EndsWith(" | in a file the step writes", Line(pack, queries, "Shop.Storage.MemoryOrderStore.Save"), StringComparison.Ordinal);
        Assert.EndsWith(" | implemented by MemoryOrderStore", Line(pack, queries, "Shop.Storage.IOrderStore"), StringComparison.Ordinal);
        Assert.True(pack.Text.Split('\n')[0].Contains("packed from the task: 2 working set node(s), 5 declaration(s) of the files the step writes", StringComparison.Ordinal), pack.Text);
    }

    [Fact]
    public async Task APromptThatNamesAndMatchesNothing_SaysSoOnItsFirstLine()
    {
        using var tree = await ShopAsync();

        var pack = CodebaseContextPack.Make(await QueriesAsync(tree), new ContextPackRequest("zzz qqq", All));

        Assert.Equal(2, pack.Text.Split('\n').Length);
        Assert.EndsWith("; packed from the task: nothing in it names or matches a node the view holds - ask the graph tool", pack.Text.Split('\n')[0], StringComparison.Ordinal);
        Assert.StartsWith("0 node(s), ~", pack.Text.Split('\n')[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnderChangeFirst_WithNoChange_OrNoneReadable_OrNoDeclarationChanged_ThePackIsTheTasks_AndItsFirstLineSaysWhy()
    {
        using var tree = await ShopAsync();
        var head = await tree.HeadAsync();
        var queries = await QueriesAsync(tree);
        var task = new ContextPackRequest("`Order.Total`", All) { From = ContextPackFrom.Changes };
        var alone = CodebaseContextPack.Make(queries, task with { From = ContextPackFrom.Task });

        var none = CodebaseContextPack.Make(queries, task with { Changes = new CodebaseGraphChanges(head, [], []) });
        var unbased = CodebaseContextPack.Make(queries, task);
        var unread = CodebaseContextPack.Make(queries, task with { ChangesUnread = "git could not compare\r\n  the tree with it.\n" });
        var undeclared = CodebaseContextPack.Make(queries, task with { Changes = new CodebaseGraphChanges(head, ["README.md"], []) });

        var commit = head[..12];
        Assert.Contains($", 0 file(s) differing from its base {commit}; no change against its base, so packed from the task: 1 node(s) it names", none.Text.Split('\n')[0], StringComparison.Ordinal);
        Assert.Contains("; its changes could not be read (git could not compare the tree with it), so packed from the task: 1 node(s) it names", unread.Text.Split('\n')[0], StringComparison.Ordinal);
        Assert.Contains($", 1 file(s) differing from its base {commit}; no declaration among them changed, so packed from the task: ", undeclared.Text.Split('\n')[0], StringComparison.Ordinal);
        Assert.Contains("; no base to compare with, so packed from the task: 1 node(s) it names", unbased.Text.Split('\n')[0], StringComparison.Ordinal);
        Assert.Equal(alone.Nodes, none.Nodes);
        Assert.Equal(alone.Nodes, unread.Nodes);
        Assert.DoesNotContain("\nchanged:\n", none.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Under change-first, a differing file the view could not compare - too large now, so no extract tells its
    /// declarations - is named on the pack's first line with why, and the line never says no declaration changed while one
    /// was not compared: what it changed is not known.
    /// </summary>
    [Fact]
    public async Task UnderChangeFirst_AFileThatCouldNotBeCompared_IsNamed_AndNoDeclarationIsSaidUnchanged()
    {
        using var tree = await CodebaseGraphTree.CreateAsync(new CodebaseGraphShape([], [], null, 120, ExcludeGenerated: true, CodebaseGraphShape.DefaultMapTokens));
        tree.Over([.. CodeGraphExtractors.OfThisBuild.All]);
        tree.Write("src/Grows.cs", "public static class Grows\n{\n}\n");
        await tree.CommitAsync();
        await tree.SyncAsync();
        var based = await tree.HeadAsync();
        tree.Write("src/Grows.cs", "public static class Grows\n{\n" + string.Concat(Enumerable.Repeat("    // a comment that makes the file too large\n", 4)) + "}\n");
        var read = (await tree.CheckAsync(null, null, "src/Grows.cs")).Read;
        var changes = await tree.Service.ChangesAsync(tree.Request(), read, based, _token);

        var pack = CodebaseContextPack.Make(new CodebaseGraphQueries(read), new ContextPackRequest("Review it.", All) { From = ContextPackFrom.Changes, Changes = changes });

        var first = pack.Text.Split('\n')[0];
        Assert.Contains($", 1 file(s) differing from its base {based[..12]}; 1 differing file(s) could not be compared: src/Grows.cs (218 bytes, over maxFileBytes (120)), so packed from the task: ", first, StringComparison.Ordinal);
        Assert.DoesNotContain("no declaration", first, StringComparison.Ordinal);

        // Beside a file compared, the line says no declaration among the rest changed; one the step may not read is never named.
        var some = changes with { Files = ["README.md", .. changes.Files] };
        var beside = CodebaseContextPack.Make(new CodebaseGraphQueries(read), new ContextPackRequest("Review it.", All) { From = ContextPackFrom.Changes, Changes = some });
        var unreadable = CodebaseContextPack.Make(new CodebaseGraphQueries(read), new ContextPackRequest("Review it.", path => path != "src/Grows.cs") { From = ContextPackFrom.Changes, Changes = some });

        Assert.Contains($", 2 file(s) differing from its base {based[..12]}; 1 differing file(s) could not be compared: src/Grows.cs (218 bytes, over maxFileBytes (120)); no declaration among the rest changed, so packed from the task: ", beside.Text.Split('\n')[0], StringComparison.Ordinal);
        Assert.Contains($", 1 file(s) differing from its base {based[..12]}; no declaration among them changed, so packed from the task: ", unreadable.Text.Split('\n')[0], StringComparison.Ordinal);
        Assert.DoesNotContain("Grows", unreadable.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnderChangeFirst_ADeclarationTakenOut_IsListedAsTheBaseHeldIt_AndOneAdded_SaysSo()
    {
        using var tree = await ShopAsync();
        var file = Path.Combine(tree.Tree, "src", "Storage", "MemoryOrderStore.cs");
        var text = await File.ReadAllTextAsync(file, _token);
        await File.WriteAllTextAsync(file, text.Replace("    public IReadOnlyList<Order> All() => [.. _orders.Values];\n", "    public int Count() => _orders.Count;\n", StringComparison.Ordinal), _token);
        var (queries, changes) = await ChangedAsync(tree, null, "src/Storage/MemoryOrderStore.cs");

        var pack = CodebaseContextPack.Make(queries, new ContextPackRequest("Review it.", All) { From = ContextPackFrom.Changes, Changes = changes });

        var lines = pack.Text.Split('\n');
        Assert.Equal("changed:", lines[1]);
        Assert.EndsWith(" | added", lines[2], StringComparison.Ordinal);
        Assert.Contains("public int Count()", lines[2], StringComparison.Ordinal);
        Assert.Equal($"removed member Shop.Storage.MemoryOrderStore.All | was src/Storage/MemoryOrderStore.cs:17-17 at {changes!.Base[..12]}", lines[3]);
    }

    [Fact]
    public async Task UnderChangeFirst_TheImpactIsListedInItsOrder_CallersToTwoHops_Overrides_ImplementationsAndDispatches_DerivedTypes()
    {
        using var tree = await TreeAsync(
            ("src/Shape.cs", "public interface IMeasure\n{\n    double Measure();\n}\n\npublic class Shape : IMeasure\n{\n    public virtual double Area()\n    {\n        return 0;\n    }\n\n    public double Measure() => 1;\n}\n"),
            ("src/Circle.cs", "public sealed class Circle : Shape\n{\n    public override double Area()\n    {\n        return 3;\n    }\n}\n"),
            ("src/Geometry.cs", "public static class Geometry\n{\n    public static double Total(Shape shape)\n    {\n        return shape.Area();\n    }\n}\n"),
            ("src/Report.cs", "public static class Report\n{\n    public static double Print(Shape shape)\n    {\n        return Geometry.Total(shape);\n    }\n}\n"),
            ("tests/ShapeTests.cs", "using Xunit;\n\npublic sealed class ShapeTests\n{\n    [Fact]\n    public void Area_IsZero()\n    {\n        Assert.Equal(0, new Shape().Area());\n    }\n\n    [Fact]\n    public void Total_IsTheArea()\n    {\n        Assert.Equal(0, Geometry.Total(new Shape()));\n    }\n}\n"));
        var file = Path.Combine(tree.Tree, "src", "Shape.cs");
        await File.WriteAllTextAsync(file, (await File.ReadAllTextAsync(file, _token)).Replace("double Measure();", "double Measure(); // in units", StringComparison.Ordinal).Replace("public class Shape : IMeasure\n{\n", "public class Shape : IMeasure\n{\n    // a shape\n", StringComparison.Ordinal).Replace("return 0;", "return 0.0;", StringComparison.Ordinal), _token);
        var (queries, changes) = await ChangedAsync(tree, null, "src/Shape.cs");

        var pack = CodebaseContextPack.Make(queries, new ContextPackRequest("Review it.", All) { From = ContextPackFrom.Changes, Changes = changes, Tokens = CodebaseContextPack.MostTokens });

        // Nothing in the task names a node, so no task section follows the impact.
        var lines = pack.Text.Split('\n');
        var sections = lines.Where(line => line.EndsWith(':') && !line.Contains(" | ", StringComparison.Ordinal)).ToList();
        Assert.Equal(["changed:", "their tests:", "their impact:"], sections);
        var impact = lines.SkipWhile(line => line != "their impact:").Skip(1).TakeWhile(line => line.Contains(" | ", StringComparison.Ordinal)).ToList();
        Assert.Equal(
            ["Geometry.Total", "Report.Print", "Circle.Area", "Shape.Measure", "Circle"],
            impact.Select(line => pack.Nodes.Select(id => queries.Card(id)).Single(card => line.StartsWith(card.Handle + " ", StringComparison.Ordinal)).Qualified));
        Assert.Equal(
            [" | calls Area", " | calls Total", " | overrides Area", " | answers Measure | unique", " | derives from Shape | unique"],
            impact.Select(line => line[line.IndexOf(" | ", line.IndexOf(" #", StringComparison.Ordinal), StringComparison.Ordinal)..].Replace(" | 2 caller(s), 1 callee(s)", string.Empty, StringComparison.Ordinal).Replace(" | 0 caller(s), 1 callee(s)", string.Empty, StringComparison.Ordinal)));
        var tests = lines.SkipWhile(line => line != "their tests:").Skip(1).TakeWhile(line => line != "their impact:").ToList();
        Assert.Equal([" | tests Area", " | tests Total"], tests.Select(line => line[line.LastIndexOf(" | ", StringComparison.Ordinal)..]));
    }

    [Fact]
    public async Task UnderChangeFirst_ADeclarationMovedBetweenFiles_IsSaidMoved_AndAPartChangedInEachFile_ListedForEach()
    {
        using var tree = await TreeAsync(
            ("src/A.cs", "public static class Mover\n{\n    public static int Go() => 1;\n}\n"),
            ("src/B.cs", "public static class Keeper\n{\n}\n"),
            ("src/Wide.A.cs", "public partial class Wide\n{\n    public int One() => 1;\n}\n"),
            ("src/Wide.B.cs", "public partial class Wide\n{\n    public int Two() => 2;\n}\n"));
        File.Delete(Path.Combine(tree.Tree, "src", "A.cs"));
        tree.Write("src/B.cs", "public static class Keeper\n{\n}\n\npublic static class Mover\n{\n    public static int Go() => 1;\n}\n");
        tree.Write("src/Wide.A.cs", "public partial class Wide\n{\n    // one\n    public int One() => 1;\n}\n");
        tree.Write("src/Wide.B.cs", "public partial class Wide\n{\n    // two\n    public int Two() => 2;\n}\n");
        await tree.SyncAsync();
        var read = (await tree.CheckAsync(null, null)).Read;
        var queries = new CodebaseGraphQueries(read);
        var changes = await tree.Service.ChangesAsync(tree.Request(), read, await tree.HeadAsync(), _token);

        var pack = CodebaseContextPack.Make(queries, new ContextPackRequest("Review it.", All) { From = ContextPackFrom.Changes, Changes = changes });

        var changed = pack.Text.Split('\n').SkipWhile(line => line != "changed:").Skip(1).TakeWhile(line => line.Contains(" | ", StringComparison.Ordinal)).ToList();
        Assert.Contains(changed, line => line.Contains("public static class Mover | src/B.cs:5-8", StringComparison.Ordinal) && line.EndsWith(" | moved from src/A.cs:1-4", StringComparison.Ordinal));
        Assert.EndsWith(" | moved from src/A.cs:3-3", Line(pack, queries, "Mover.Go"), StringComparison.Ordinal);
        Assert.DoesNotContain(changed, line => line.StartsWith("removed ", StringComparison.Ordinal));
        Assert.Contains($"; packed from those changes first - {changed.Count} declaration(s) added, changed or taken out,", pack.Text.Split('\n')[0], StringComparison.Ordinal);
        Assert.Contains(changed, line => line.Contains("| src/Wide.A.cs:1-5", StringComparison.Ordinal) && line.EndsWith(" | changed at 3", StringComparison.Ordinal));
        Assert.Contains(changed, line => line.Contains("| src/Wide.B.cs:1-5", StringComparison.Ordinal) && line.EndsWith(" | changed at 3", StringComparison.Ordinal));
        Assert.Equal(pack.Nodes.Count, pack.Nodes.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task UnderChangeFirst_ADirectCallerThatIsAHub_IsMarkedAndNotGoneThrough_AndGeneratedCodeIsLastInEachSection()
    {
        using var tree = await TreeAsync(
            ("src/Hub.cs", CodebaseGraphToolTests.HubFile(60)),
            ("src/Aaa.g.cs", "// <auto-generated/>\npublic static class Made\n{\n    public static void Go()\n    {\n        Demo.Hubs.Hub.Leaf();\n    }\n}\n"),
            ("src/Bbb.g.cs", "// <auto-generated/>\npublic static class Kept\n{\n    public static void Go()\n    {\n        Demo.Hubs.Hub.Leaf();\n    }\n}\n"),
            ("src/Zed.cs", "public static class Zed\n{\n    public static void Go()\n    {\n        Demo.Hubs.Hub.Leaf();\n    }\n}\n"));
        tree.Write("src/Hub.cs", CodebaseGraphToolTests.HubFile(60).Replace("    public static void Leaf()\n    {\n    }\n", "    public static void Leaf()\n    {\n        return;\n    }\n", StringComparison.Ordinal));
        tree.Write("src/Aaa.g.cs", "// <auto-generated/>\npublic static class Made\n{\n    public static void Go()\n    {\n        Demo.Hubs.Hub.Leaf();\n        Demo.Hubs.Hub.Leaf();\n    }\n}\n");
        var read = (await tree.CheckAsync(null, null, "src/Hub.cs", "src/Aaa.g.cs")).Read;
        var queries = new CodebaseGraphQueries(read);
        var changes = await tree.Service.ChangesAsync(tree.Request(), read, await tree.HeadAsync(), _token);

        var pack = CodebaseContextPack.Make(queries, new ContextPackRequest("Review it.", All) { From = ContextPackFrom.Changes, Changes = changes, Tokens = CodebaseContextPack.MostTokens });

        var lines = pack.Text.Split('\n');
        var changed = lines.SkipWhile(line => line != "changed:").Skip(1).TakeWhile(line => line.Contains(" | ", StringComparison.Ordinal)).Select(Qualified).ToList();
        var impact = lines.SkipWhile(line => line != "their impact:").Skip(1).TakeWhile(line => line.Contains(" | ", StringComparison.Ordinal)).Select(Qualified).ToList();
        Assert.Equal(["Demo.Hubs.Hub.Leaf", "Made.Go"], changed);
        Assert.Equal(["Demo.Hubs.Hub.Center", "Zed.Go", "Kept.Go"], impact);
        Assert.EndsWith(" | calls Leaf | hub, not gone through its 60 callers", Line(pack, queries, "Demo.Hubs.Hub.Center"), StringComparison.Ordinal);
        Assert.Null(LineOrNull(pack, queries, "Demo.Hubs.Hub.M0"));

        string Qualified(string line) => pack.Nodes.Select(id => queries.Card(id)).Single(card => line.StartsWith(card.Handle + " ", StringComparison.Ordinal)).Qualified;
    }

    [Fact]
    public async Task UnderChangeFirst_EachChangedDeclaration_SaysWhereItsLinesDiffer_RunsAndPlacesLinesWereTakenOut()
    {
        using var tree = await TreeAsync(("src/Calc.cs", "public static class Calc\n{\n    public static int Sum(int a, int b)\n    {\n        var s = a;\n        s += b;\n        return s;\n    }\n\n    public static int Twice(int a)\n    {\n        var t = a;\n        t += a;\n        return t;\n    }\n}\n"));
        var (queries, changes) = await ChangedAsync(tree, ("src/Calc.cs", "        var s = a;\n        s += b;\n", "        var s = b;\n        s += a;\n"));
        tree.Write("src/Calc.cs", (await File.ReadAllTextAsync(Path.Combine(tree.Tree, "src", "Calc.cs"), _token)).Replace("        t += a;\n", string.Empty, StringComparison.Ordinal));
        (queries, changes) = await ChangedAsync(tree, null, "src/Calc.cs");

        var pack = CodebaseContextPack.Make(queries, new ContextPackRequest("Review it.", All) { From = ContextPackFrom.Changes, Changes = changes });

        Assert.EndsWith(" | changed at 5-6", Line(pack, queries, "Calc.Sum"), StringComparison.Ordinal);
        Assert.EndsWith(" | lines taken out after 12", Line(pack, queries, "Calc.Twice"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnderChangeFirst_APartTakenOutOfOneFile_IsSaidTakenOut_ThoughAnotherStillDeclaresIt_WhatItHeldWithIt()
    {
        using var tree = await TreeAsync(
            ("src/Wide.A.cs", "public partial class Wide\n{\n    public int One() => 1;\n}\n"),
            ("src/Wide.B.cs", "public partial class Wide\n{\n    public int Two() => 2;\n}\n"));
        var based = await tree.HeadAsync();
        File.Delete(Path.Combine(tree.Tree, "src", "Wide.B.cs"));
        await tree.SyncAsync();
        var read = (await tree.CheckAsync(null, null)).Read;
        var queries = new CodebaseGraphQueries(read);
        var changes = await tree.Service.ChangesAsync(tree.Request(), read, based, _token);

        var pack = CodebaseContextPack.Make(queries, new ContextPackRequest("Review it.", All) { From = ContextPackFrom.Changes, Changes = changes });

        var lines = pack.Text.Split('\n');
        Assert.Equal(["changed:", $"removed type Wide | was src/Wide.B.cs:1-4 at {based[..12]}"], lines[1..3]);
        Assert.DoesNotContain("Two", pack.Text, StringComparison.Ordinal);
        Assert.Contains("; packed from those changes first - 1 declaration(s) added, changed or taken out,", lines[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnderChangeFirst_AChangedImplementation_HasTheInterfaceMemberThatDispatchesToIt_InItsImpact()
    {
        using var tree = await TreeAsync(("src/Square.cs", "public interface IArea\n{\n    int Area();\n}\n\npublic sealed class Square : IArea\n{\n    public int Area()\n    {\n        return 4;\n    }\n}\n"));
        var (queries, changes) = await ChangedAsync(tree, ("src/Square.cs", "return 4;", "return 9;"));

        var pack = CodebaseContextPack.Make(queries, new ContextPackRequest("Review it.", All) { From = ContextPackFrom.Changes, Changes = changes });

        Assert.Contains(" | dispatches to Area", Line(pack, queries, "IArea.Area"), StringComparison.Ordinal);
        Assert.Equal("their impact:", pack.Text.Split('\n')[^3]);
    }

    [Fact]
    public async Task UnderChangeFirst_AChangeInAFileTheStepMayNotRead_IsNeitherListedNorCounted_NorNamedAsWhereADeclarationMovedFrom()
    {
        using var tree = await TreeAsync(
            ("src/Target.cs", "public static class Target\n{\n    public static int Hit()\n    {\n        return 1;\n    }\n}\n"),
            ("secret/Hidden.cs", "public static class Hidden\n{\n    public static int Through()\n    {\n        return 1;\n    }\n\n    public static void Dropped()\n    {\n    }\n}\n"),
            ("secret/Mover.cs", "public static class Moving\n{\n    public static int Go() => 1;\n}\n"));
        var based = await tree.HeadAsync();
        tree.Write("src/Target.cs", "public static class Target\n{\n    public static int Hit()\n    {\n        return 2;\n    }\n}\n");
        tree.Write("secret/Hidden.cs", "public static class Hidden\n{\n    public static int Through()\n    {\n        return 2;\n    }\n}\n");
        File.Delete(Path.Combine(tree.Tree, "secret", "Mover.cs"));
        tree.Write("src/Moving.cs", "public static class Moving\n{\n    public static int Go() => 1;\n}\n");
        await tree.SyncAsync();
        var read = (await tree.CheckAsync(null, null)).Read;
        var queries = new CodebaseGraphQueries(read);
        var changes = await tree.Service.ChangesAsync(tree.Request(), read, based, _token);

        var pack = CodebaseContextPack.Make(queries, new ContextPackRequest("Review it.", path => !path.StartsWith("secret/", StringComparison.Ordinal)) { From = ContextPackFrom.Changes, Changes = changes });

        Assert.Contains($", 2 file(s) differing from its base {based[..12]}; packed from those changes first - 3 declaration(s) added, changed or taken out,", pack.Text.Split('\n')[0], StringComparison.Ordinal);
        Assert.DoesNotContain("secret", pack.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Through", pack.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Dropped", pack.Text, StringComparison.Ordinal);
        Assert.EndsWith(" | added", Line(pack, queries, "Moving"), StringComparison.Ordinal);
        Assert.EndsWith(" | added", Line(pack, queries, "Moving.Go"), StringComparison.Ordinal);
        Assert.True(pack.Refused >= 3);
    }

    [Fact]
    public async Task UnderChangeFirst_ACallerTheStepMayNotRead_IsListedNowhere_AndItsTestsAndCallersAreNotSaidAsIts()
    {
        using var tree = await TreeAsync(
            ("src/Target.cs", "public static class Target\n{\n    public static int Hit()\n    {\n        return 1;\n    }\n}\n"),
            ("secret/Hidden.cs", "public static class Hidden\n{\n    public static int Through()\n    {\n        return Target.Hit();\n    }\n}\n"),
            ("src/Far.cs", "public static class Far\n{\n    public static int Away() => Hidden.Through();\n}\n"),
            ("tests/HiddenTests.cs", "using Xunit;\n\npublic sealed class HiddenTests\n{\n    [Fact]\n    public void Through_Hits()\n    {\n        Assert.Equal(1, Hidden.Through());\n    }\n}\n"));
        var (queries, changes) = await ChangedAsync(tree, ("src/Target.cs", "return 1;", "return 2;"));

        var pack = CodebaseContextPack.Make(queries, new ContextPackRequest("Review it.", path => !path.StartsWith("secret/", StringComparison.Ordinal)) { From = ContextPackFrom.Changes, Changes = changes });

        Assert.Equal("changed:", pack.Text.Split('\n')[1]);
        Assert.DoesNotContain("Hidden", pack.Text.Replace("HiddenTests", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.DoesNotContain("Through", pack.Text.Replace("Through_Hits", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Null(LineOrNull(pack, queries, "Hidden.Through"));
        Assert.Null(LineOrNull(pack, queries, "Far.Away"));
        Assert.True(pack.Refused > 0);
    }

    [Fact]
    public async Task TheNearness_ItGives_RanksAPackNode_ThenAWrittenFile_ThenAChangedOne_ThenANamedPathOrName()
    {
        using var tree = await ShopAsync();
        var queries = await QueriesAsync(tree);
        var changes = new CodebaseGraphChanges("base", ["src/Pricing/SeasonalDiscount.cs"], []);

        var request = new ContextPackRequest("`Order.Total`, `src/Checkout/Receipt.cs`, src/Pricing/LoyaltyDiscount.cs:9 and Charge", All) { Writes = Covered("src/Storage/**"), Changes = changes, Worked = [new(Id(queries, "Shop.Reports.SalesReport.Sales"), Edited: false)] };
        var pack = CodebaseContextPack.Make(queries, request);
        var none = CodebaseContextPack.Make(queries, request with { Tokens = 1 });

        // A node of the pack, or of a working set given; then the rest by where it is and what it is called.
        Assert.Equal(0, pack.Near.Of(queries.Card(Id(queries, "Shop.Orders.Order.Total"))));
        Assert.Empty(none.Nodes);
        Assert.Equal(0, none.Near.Of(queries.Card(Id(queries, "Shop.Reports.SalesReport.Sales"))));
        Assert.Equal(1, none.Near.Of(queries.Card(Id(queries, "Shop.Storage.IOrderStore"))));
        Assert.Equal(2, none.Near.Of(queries.Card(Id(queries, "Shop.Pricing.SeasonalDiscount"))));
        Assert.Equal(3, none.Near.Of(queries.Card(Id(queries, "Shop.Checkout.Receipt"))));
        Assert.Equal(3, none.Near.Of(queries.Card(Id(queries, "Shop.Checkout.IPaymentGateway.Charge"))));
        Assert.Equal(3, none.Near.Of(queries.Card(Id(queries, "Shop.Pricing.LoyaltyDiscount.Apply"))));
        Assert.Equal(GraphNearness.Far, none.Near.Of(queries.Card(Id(queries, "Shop.Reports.SalesReport"))));
    }

    [Fact]
    public async Task Find_BreaksATieByTheNearnessItIsGiven_ThenById()
    {
        using var tree = await TreeAsync(("src/A.cs", "public class A\n{\n    public void Go()\n    {\n    }\n}\n"), ("src/B.cs", "public class B\n{\n    public void Go()\n    {\n    }\n}\n"));
        var queries = await QueriesAsync(tree);
        var near = new GraphNearness([], path => path == "src/B.cs", [], [], []);

        var plain = queries.Find("Go", null, null, 10, 0).Cards.Select(card => card.Qualified);
        var nearer = queries.Find("Go", null, null, 10, 0, near).Cards.Select(card => card.Qualified);

        Assert.Equal(["A.Go", "B.Go"], plain);
        Assert.Equal(["B.Go", "A.Go"], nearer);
    }

    [Fact]
    public async Task ANameWrittenWithSomeOfItsScopes_NamesEachNodeItEnds_AtADot()
    {
        using var tree = await ShopAsync();
        var queries = await QueriesAsync(tree);

        Assert.Equal("Shop.Orders.Order.Total", Assert.IsType<GraphResolve.Found>(queries.Resolve("Order.Total")).Card.Qualified);
        Assert.Equal("Shop.Orders.Order.Total", Assert.IsType<GraphResolve.Found>(queries.Resolve("Orders::Order.Total")).Card.Qualified);
        Assert.Equal("Shop.Pricing.LoyaltyDiscount.Apply", Assert.IsType<GraphResolve.Found>(queries.Resolve("LoyaltyDiscount.Apply")).Card.Qualified);
        Assert.IsType<GraphResolve.NotFound>(queries.Resolve("Discount.Apply"));
        Assert.IsType<GraphResolve.NotFound>(queries.Resolve("rder.Total"));

        using var two = await TreeAsync(("src/A.cs", "public class A\n{\n    public class Inner\n    {\n        public void Go()\n        {\n        }\n    }\n}\n"), ("src/B.cs", "public class B\n{\n    public class Inner\n    {\n        public void Go()\n        {\n        }\n    }\n}\n"));
        Assert.Equal(["A.Inner.Go", "B.Inner.Go"], Assert.IsType<GraphResolve.Candidates>((await QueriesAsync(two)).Resolve("Inner.Go")).Cards.Select(card => card.Qualified));
    }

    [Fact]
    public void EstimatedTokens_AreTheBytesOverFour_RoundedUp()
    {
        Assert.Equal(0, EstimatedTokens.Of(string.Empty));
        Assert.Equal(1, EstimatedTokens.Of("abcd"));
        Assert.Equal(2, EstimatedTokens.Of("abcde"));
        Assert.Equal(1, EstimatedTokens.Of("é"));
    }

    /// <summary>Every file read.</summary>
    private static readonly Func<string, bool> All = _ => true;

    /// <summary>What a step whose writes is <paramref name="pattern"/> covers.</summary>
    private static Func<string, bool> Covered(string pattern) => path => DssHarness.Core.Repository.GlobPattern.Parse(pattern).Matches(path);

    private const string Chain = "public static class Chain\n{\n    public static void First()\n    {\n        Second();\n    }\n\n    public static void Second()\n    {\n        Third();\n    }\n\n    public static void Third()\n    {\n        Fourth();\n    }\n\n    public static void Fourth()\n    {\n    }\n}\n";

    /// <summary>The C# shop, synced.</summary>
    internal static Task<CodebaseGraphTree> ShopAsync() => TreeAsync([.. ContextPackFixtures.CSharp]);

    /// <summary>A synced tree of <paramref name="files"/>, read by every implementation of this build.</summary>
    internal static async Task<CodebaseGraphTree> TreeAsync(params (string Path, string Text)[] files)
    {
        var tree = await CodebaseGraphTree.CreateAsync();
        tree.Over([.. CodeGraphExtractors.OfThisBuild.All]);

        foreach (var (path, text) in files)
        {
            tree.Write(path, text);
        }

        await tree.CommitAsync();
        await tree.SyncAsync();
        return tree;
    }

    private static async Task<CodebaseGraphQueries> QueriesAsync(CodebaseGraphTree tree) => new((await tree.CheckAsync(null, null)).Read);

    /// <summary>The base's view once <paramref name="change"/> is applied to the tree - or once <paramref name="path"/> was changed by hand - and its changes against HEAD.</summary>
    private async Task<(CodebaseGraphQueries Queries, CodebaseGraphChanges? Changes)> ChangedAsync(CodebaseGraphTree tree, (string Path, string Old, string New)? change, string? path = null)
    {
        if (change is { } applied)
        {
            var file = Path.Combine(tree.Tree, applied.Path);
            var text = await File.ReadAllTextAsync(file, _token);
            Assert.Contains(applied.Old, text, StringComparison.Ordinal);
            await File.WriteAllTextAsync(file, text.Replace(applied.Old, applied.New, StringComparison.Ordinal), _token);
            path = applied.Path;
        }

        if (path is null)
        {
            return (await QueriesAsync(tree), null);
        }

        var read = (await tree.CheckAsync(null, null, path)).Read;
        return (new CodebaseGraphQueries(read), await tree.Service.ChangesAsync(tree.Request(), read, await tree.HeadAsync(), _token));
    }

    private static string Id(CodebaseGraphQueries queries, string qualified) => Assert.IsType<GraphResolve.Found>(queries.Resolve(qualified)).Card.Id;

    private static string Line(ContextPack pack, CodebaseGraphQueries queries, string qualified)
        => LineOrNull(pack, queries, qualified) ?? throw new Xunit.Sdk.XunitException($"'{qualified}' is not in the pack:\n{pack.Text}");

    private static string? LineOrNull(ContextPack pack, CodebaseGraphQueries queries, string qualified)
        => queries.Resolve(qualified) is GraphResolve.Found found && pack.Nodes.Contains(found.Card.Id)
            ? pack.Text.Split('\n').Single(line => line.StartsWith(found.Card.Handle + " ", StringComparison.Ordinal))
            : null;
}
