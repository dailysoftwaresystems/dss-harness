using DssHarness.Core.Codebase;

namespace DssHarness.Tests;

/// <summary>
/// The changes a conversation is told of the codebase: the declarations its view changed since what the conversation
/// was told of them - its pack and every delta after it - moved or changed with the new range and hash, removed; and the
/// nodes of the step's own pack the conversation was not given, added.
/// </summary>
public sealed class CodebaseConversationChangesTests
{
    private readonly CancellationToken _token = TestContext.Current.CancellationToken;

    private const string A = "public class A\n{\n    public void One()\n    {\n    }\n\n    public void Two()\n    {\n    }\n}\n";

    private const string B = "public class B\n{\n    public void Gone()\n    {\n    }\n}\n";

    private const string C = "public class C\n{\n    public void Stays()\n    {\n    }\n}\n";

    /// <summary>
    /// A declaration that moved is told where it stands now - its line as the view holds it, the new range and hash in
    /// it - and is not named removed and added besides; one that was taken out is named removed with the line the
    /// conversation holds; a node of the step's own pack the conversation was not given is added; and one that stands
    /// as it stood is told again by nothing.
    /// </summary>
    [Fact]
    public async Task AConversationIsTold_WhatMovedAndWhatWasRemoved_AndWhatOfItsOwnPackItWasNotGiven()
    {
        using var tree = await TreeAsync(("src/A.cs", A), ("src/B.cs", B), ("src/C.cs", C));
        var before = await QueriesAsync(tree);
        var was = CodebaseContextPack.Make(before, new ContextPackRequest("see `One`, `Two`, `Gone` and `Stays`", All));

        // One and Two move two lines down, Three is new, Gone is taken out, and Stays stands as it stood.
        await File.WriteAllTextAsync(
            Path.Combine(tree.Tree, "src", "A.cs"),
            "public class A\n{\n    // a line that moves what follows it\n    // and one more\n    public void One()\n    {\n    }\n\n    public void Two()\n    {\n    }\n\n    public void Three()\n    {\n    }\n}\n",
            _token);
        await File.WriteAllTextAsync(Path.Combine(tree.Tree, "src", "B.cs"), "public class B\n{\n}\n", _token);

        var read = (await tree.CheckAsync(null, null, "src/A.cs", "src/B.cs")).Read;
        var after = new CodebaseGraphQueries(read);
        var now = CodebaseContextPack.Make(after, new ContextPackRequest("see `One`, `Two` and `Three`", All));

        var told = CodebaseConversationChanges.Make(after, now, [was.Text], CodebaseContextPack.DefaultTokens).Split('\n').ToList();
        Assert.Equal("4 declarations the conversation held: 2 moved or changed, 1 removed; 1 node of this step's pack added", told[0]);

        // One and Two, moved: their lines as the view holds them now, and never their old ones.
        Assert.Contains(GraphLines.Of(Card(after, "One")), told, StringComparer.Ordinal);
        Assert.Contains(GraphLines.Of(Card(after, "Two")), told, StringComparer.Ordinal);
        Assert.DoesNotContain(told, line => line.Contains("src/A.cs:3-5", StringComparison.Ordinal) || line.Contains("src/A.cs:7-9", StringComparison.Ordinal));

        // Gone, taken out: its old line, named removed.
        Assert.Equal([CodebaseConversationChanges.RemovedPrefix + Line(was, "Gone")], told.Where(line => line.StartsWith(CodebaseConversationChanges.RemovedPrefix, StringComparison.Ordinal)));

        // Three, of the step's own pack and not given: added, its line as the pack holds it.
        Assert.Contains(Line(now, "Three"), told, StringComparer.Ordinal);

        // Stays, standing as it stood: told again by nothing.
        Assert.DoesNotContain(told, line => line.Contains("Stays", StringComparison.Ordinal) || line.Contains("src/C.cs", StringComparison.Ordinal));
    }

    /// <summary>
    /// A declaration whose body changed in place - the same lines, another hash - is told as changed, its line as the
    /// view holds it now: a conversation that goes on never reasons over a body it was told before the change.
    /// </summary>
    [Fact]
    public async Task ADeclarationWhoseBodyChangedInPlace_IsToldAsChanged_ItsNewHashInItsLine()
    {
        using var tree = await TreeAsync(("src/A.cs", Bodied("1")));
        var before = await QueriesAsync(tree);
        var one = Card(before, "One");

        tree.Write("src/A.cs", Bodied("2"));
        var after = new CodebaseGraphQueries((await tree.CheckAsync(null, null, "src/A.cs")).Read);
        var changed = Card(after, "One");

        Assert.Equal((one.Start, one.End), (changed.Start, changed.End));
        Assert.NotEqual(one.Hash, changed.Hash);
        Assert.Equal(
            ["1 declaration the conversation held: 1 moved or changed, 0 removed; 0 nodes of this step's pack added", GraphLines.Of(changed)],
            CodebaseConversationChanges.Make(after, NoPack, [GraphLines.Of(one)], CodebaseContextPack.DefaultTokens).Split('\n'));
    }

    /// <summary>
    /// A file deleted takes every declaration the conversation held of it: each is named removed with the line the
    /// conversation holds, for the view no longer holds the file at all.
    /// </summary>
    [Fact]
    public async Task AFileDeleted_NamesEveryDeclarationTheConversationHeldOfItRemoved()
    {
        using var tree = await TreeAsync(("src/A.cs", A), ("src/B.cs", B));
        var before = await QueriesAsync(tree);
        string[] held = [GraphLines.Of(Card(before, "B")), GraphLines.Of(Card(before, "Gone"))];

        File.Delete(Path.Combine(tree.Tree, "src", "B.cs"));
        var after = new CodebaseGraphQueries((await tree.CheckAsync(null, null, "src/B.cs")).Read);

        Assert.Null(after.OutlineFile("src/B.cs"));
        Assert.Equal(
            ["2 declarations the conversation held: 0 moved or changed, 2 removed; 0 nodes of this step's pack added", .. held.Select(line => CodebaseConversationChanges.RemovedPrefix + line)],
            CodebaseConversationChanges.Make(after, NoPack, [string.Join('\n', held)], CodebaseContextPack.DefaultTokens).Split('\n'));
    }

    /// <summary>
    /// A delta told at a page size too small for its own first line is told whole all the same: the page is never less
    /// than what holds its first and last lines and a line beside them, so no change is left out for the size. Red with
    /// the delta cut at whole lines under the step's tokens.
    /// </summary>
    [Fact]
    public async Task ADeltaAtAPageTooSmallForItsHead_IsToldWhole()
    {
        using var tree = await TreeAsync(("src/A.cs", A));
        var view = await QueriesAsync(tree);
        var pack = CodebaseContextPack.Make(view, new ContextPackRequest("see `One` and `Two`", All));
        var nodes = pack.Lines.Where(line => line.Id is not null).Select(line => line.Text).ToList();
        var first = $"0 declarations the conversation held: 0 moved or changed, 0 removed; {nodes.Count} nodes of this step's pack added";

        Assert.True(nodes.Count > 1, $"the pack holds {nodes.Count} nodes: a cut needs more than one line to leave out");
        Assert.Equal([first, .. nodes], CodebaseConversationChanges.Make(view, pack, [], EstimatedTokens.Of(first) + 1).Split('\n'));
    }

    /// <summary>
    /// A conversation that held five thousand declarations, every one of which moved, is told every one, page by page at
    /// the step's size: page 1 in the message, its last line saying how to ask for the next, each page after it the delta
    /// going on, the last saying it is - each declaration on one page, once. Red with the delta cut at the step's tokens.
    /// </summary>
    [Fact]
    public async Task AFiveThousandNodeDelta_IsToldWholeByPage()
    {
        const int Methods = 5_000;
        var many = new System.Text.StringBuilder("public class Many\n{\n");

        for (var at = 0; at < Methods; at++)
        {
            many.Append(System.Globalization.CultureInfo.InvariantCulture, $"    public void M{at}()\n    {{\n    }}\n\n");
        }

        using var tree = await TreeAsync(("src/Many.cs", many.Append("}\n").ToString()));
        var before = await QueriesAsync(tree);
        var held = string.Join('\n', before.OutlineFile("src/Many.cs")!.Select(GraphLines.Of));

        // A line put at the head of the file moves every declaration in it.
        await File.WriteAllTextAsync(Path.Combine(tree.Tree, "src", "Many.cs"), "// moved\n" + many, _token);
        var after = new CodebaseGraphQueries((await tree.CheckAsync(null, null, "src/Many.cs")).Read);
        var declared = after.OutlineFile("src/Many.cs")!.Select(GraphLines.Of).ToList();

        var pages = new List<GraphTextPage>();
        var told = CodebaseConversationChanges.Pages(after, NoPack, [held], CodebaseContextPack.DefaultTokens);

        for (var at = 1; told.Page(at) is { } page; at++)
        {
            pages.Add(page);
        }

        var lines = pages.SelectMany(page => page.Lines).Select(line => line.Text).ToList();

        Assert.True(declared.Count > Methods, $"the view declares {declared.Count}");
        Assert.True(pages.Count > 1);
        Assert.StartsWith($"{declared.Count} declarations the conversation held: {declared.Count} moved or changed, 0 removed; 0 nodes of this step's pack added\n", pages[0].Text, StringComparison.Ordinal);
        Assert.Equal(pages[0].Text, CodebaseConversationChanges.Make(after, NoPack, [held], CodebaseContextPack.DefaultTokens));
        Assert.EndsWith($"codebase changes page 1 of {pages.Count} · ask graph for changes with page: 2 for the next", pages[0].Text, StringComparison.Ordinal);
        Assert.All(pages.Skip(1), page => Assert.StartsWith($"codebase changes page {page.Page}:\n", page.Text, StringComparison.Ordinal));
        Assert.All(pages.Skip(1).SkipLast(1), page => Assert.EndsWith($"codebase changes page {page.Page} of {pages.Count} · ask graph for changes with page: {page.Page + 1} for the next", page.Text, StringComparison.Ordinal));
        Assert.EndsWith($"codebase changes page {pages.Count} of {pages.Count} · the last", pages[^1].Text, StringComparison.Ordinal);
        Assert.All(pages, page => Assert.True(page.Tokens <= CodebaseContextPack.DefaultTokens, page.Text));
        Assert.Equal(declared.Order(StringComparer.Ordinal), lines.Order(StringComparer.Ordinal));
        Assert.Equal(pages.Count, told.Count);
    }

    /// <summary>
    /// A conversation holds what its pack told it and every delta after it: a declaration of the pack that a later delta
    /// did not name is still held, and told when it changes; one a delta named removed is no longer held, and never told
    /// again - its removed line never read as a declaration of its own.
    /// </summary>
    [Fact]
    public async Task WhatTheConversationHolds_IsItsPackAndEveryDeltaAfterIt_ARemovedLineTakingItsDeclarationOut()
    {
        using var tree = await TreeAsync(("src/A.cs", A), ("src/B.cs", B));
        var first = await QueriesAsync(tree);
        var pack = string.Join('\n', GraphLines.Of(Card(first, "One")), GraphLines.Of(Card(first, "Two")), GraphLines.Of(Card(first, "Gone")));

        // Two moves down a line and Gone is taken out: the first delta tells both.
        tree.Write("src/A.cs", "public class A\n{\n    public void One()\n    {\n    }\n\n\n    public void Two()\n    {\n    }\n}\n");
        tree.Write("src/B.cs", "public class B\n{\n}\n");
        var second = new CodebaseGraphQueries((await tree.CheckAsync(null, null, "src/A.cs", "src/B.cs")).Read);
        var delta = CodebaseConversationChanges.Make(second, NoPack, [pack], CodebaseContextPack.DefaultTokens);

        Assert.Equal(
            [
                "3 declarations the conversation held: 1 moved or changed, 1 removed; 0 nodes of this step's pack added",
                GraphLines.Of(Card(second, "Two")),
                CodebaseConversationChanges.RemovedPrefix + GraphLines.Of(Card(first, "Gone")),
            ],
            delta.Split('\n'));

        // Then One's body grows a line and Two stands where the delta told it: the pack's One, which the delta never named,
        // is told; Two and Gone are never told again.
        tree.Write("src/A.cs", "public class A\n{\n    public void One()\n    {\n        return;\n    }\n\n    public void Two()\n    {\n    }\n}\n");
        var third = new CodebaseGraphQueries((await tree.CheckAsync(null, null, "src/A.cs", "src/B.cs")).Read);

        Assert.Equal(
            [
                "2 declarations the conversation held: 1 moved or changed, 0 removed; 0 nodes of this step's pack added",
                GraphLines.Of(Card(third, "One")),
            ],
            CodebaseConversationChanges.Make(third, NoPack, [pack, delta], CodebaseContextPack.DefaultTokens).Split('\n'));
    }

    /// <summary>
    /// A declaration whose signature holds the separator a node's line parts its parts with is still known where it
    /// stands, read by the one reader of a node's line, and told when it moves.
    /// </summary>
    [Fact]
    public async Task ADeclarationWhoseSignatureHoldsTheSeparator_IsKnownWhereItStands_AndToldWhenItMoves()
    {
        using var tree = await TreeAsync(("src/F.cs", "public class F\n{\n    public int Flags(int given = 1 | 2) => given;\n}\n"));
        var before = await QueriesAsync(tree);
        var line = GraphLines.Of(Card(before, "Flags"));

        Assert.Contains("1 | 2", line, StringComparison.Ordinal);
        Assert.Equal(new GraphPlace(Card(before, "Flags").Handle, "src/F.cs", 3, 3, Card(before, "Flags").Hash), GraphLines.Placed(line));

        tree.Write("src/F.cs", "public class F\n{\n\n    public int Flags(int given = 1 | 2) => given;\n}\n");
        var after = new CodebaseGraphQueries((await tree.CheckAsync(null, null, "src/F.cs")).Read);

        Assert.Equal(
            ["1 declaration the conversation held: 1 moved or changed, 0 removed; 0 nodes of this step's pack added", GraphLines.Of(Card(after, "Flags"))],
            CodebaseConversationChanges.Make(after, NoPack, [line], CodebaseContextPack.DefaultTokens).Split('\n'));
    }

    /// <summary>A conversation that held no codebase is told every node of the step's own pack as added, and nothing else.</summary>
    [Fact]
    public async Task AConversationThatHeldNoCodebase_IsToldEveryNodeOfTheStepsOwnPack()
    {
        using var tree = await TreeAsync(("src/A.cs", A));
        var view = await QueriesAsync(tree);
        var pack = CodebaseContextPack.Make(view, new ContextPackRequest("see `One`", All));

        var told = CodebaseConversationChanges.Make(view, pack, [], CodebaseContextPack.DefaultTokens).Split('\n').ToList();

        var nodes = pack.Lines.Count(line => line.Id is not null);

        Assert.Equal($"0 declarations the conversation held: 0 moved or changed, 0 removed; {nodes} node{(nodes == 1 ? string.Empty : "s")} of this step's pack added", told[0]);
        Assert.Equal([.. pack.Lines.Where(line => line.Id is not null).Select(line => line.Text)], told[1..]);
    }

    /// <summary>Where nothing changed and nothing is new, the delta says so in its first line alone.</summary>
    [Fact]
    public async Task WhereNothingChanged_TheFirstLineAloneSaysSo()
    {
        using var tree = await TreeAsync(("src/A.cs", A));
        var view = await QueriesAsync(tree);
        var pack = CodebaseContextPack.Make(view, new ContextPackRequest("see `One`", All));

        var held = pack.Lines.Count(line => line.Id is not null);

        Assert.Equal(
            $"{held} declaration{(held == 1 ? string.Empty : "s")} the conversation held: 0 moved or changed, 0 removed; 0 nodes of this step's pack added",
            CodebaseConversationChanges.Make(view, pack, [pack.Text], CodebaseContextPack.DefaultTokens));
    }

    private static readonly Func<string, bool> All = _ => true;

    /// <summary>A pack of no nodes: a delta made with it tells only what the conversation held.</summary>
    private static readonly ContextPack NoPack = new(string.Empty, [], [], [], 0, 0, new GraphNearness([], _ => false, [], [], []));

    /// <summary>A class whose method <c>One</c> has a body of one line, <c>var x = <paramref name="value"/>;</c>.</summary>
    private static string Bodied(string value) => $"public class A\n{{\n    public void One()\n    {{\n        var x = {value};\n    }}\n}}\n";

    /// <summary>The card of the declaration <paramref name="symbol"/> resolves to.</summary>
    private static GraphCard Card(CodebaseGraphQueries view, string symbol)
        => Assert.IsType<GraphResolve.Found>(view.Resolve(symbol)).Card;

    /// <summary>The pack's line of the declaration <paramref name="symbol"/> resolves to.</summary>
    private static string Line(ContextPack pack, string symbol)
        => pack.Lines.Single(line => line.Id is not null && line.Text.Contains($"{symbol}(", StringComparison.Ordinal)).Text;

    private static async Task<CodebaseGraphTree> TreeAsync(params (string Path, string Text)[] files)
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
}
