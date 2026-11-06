using DssHarness.Core.Codebase;
using DssHarness.Core.Results;

namespace DssHarness.Tests;

/// <summary>
/// The changes a conversation is told of the codebase (part 08, "Deltas, never a rewrite of what was sent"): the
/// declarations its view changed since the conversation's last pack or delta - moved or changed with the new range and
/// hash, removed - and the nodes of the step's own pack the conversation was not given, added.
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

        var told = CodebaseConversationChanges.Make(after, now, was.Text, CodebaseContextPack.DefaultTokens).Split('\n').ToList();

        Assert.Contains("moved or changed", told[0], StringComparison.Ordinal);
        Assert.Contains("removed", told[0], StringComparison.Ordinal);

        // One and Two, moved: their lines as the view holds them now, and never their old ones.
        Assert.Contains(GraphLines.Of(Card(after, "One")), told, StringComparer.Ordinal);
        Assert.Contains(GraphLines.Of(Card(after, "Two")), told, StringComparer.Ordinal);
        Assert.DoesNotContain(told, line => line.Contains("src/A.cs:3-5", StringComparison.Ordinal) || line.Contains("src/A.cs:7-9", StringComparison.Ordinal));

        // Gone, taken out: its old line, named removed.
        Assert.Contains(told, line => line.StartsWith("removed: ", StringComparison.Ordinal) && line.Contains("Gone", StringComparison.Ordinal));

        // Three, of the step's own pack and not given: added, its line as the pack holds it.
        Assert.Contains(Line(now, "Three"), told, StringComparer.Ordinal);

        // Stays, standing as it stood: told again by nothing.
        Assert.DoesNotContain(told, line => line.Contains("Stays", StringComparison.Ordinal) || line.Contains("src/C.cs", StringComparison.Ordinal));
    }

    /// <summary>A conversation that held no codebase is told every node of the step's own pack as added, and nothing else.</summary>
    [Fact]
    public async Task AConversationThatHeldNoCodebase_IsToldEveryNodeOfTheStepsOwnPack()
    {
        using var tree = await TreeAsync(("src/A.cs", A));
        var view = await QueriesAsync(tree);
        var pack = CodebaseContextPack.Make(view, new ContextPackRequest("see `One`", All));

        var told = CodebaseConversationChanges.Make(view, pack, last: null, CodebaseContextPack.DefaultTokens).Split('\n').ToList();

        var nodes = pack.Lines.Count(line => line.Id is not null);

        Assert.Equal($"0 declarations the conversation held: 0 moved or changed, 0 removed; {nodes} node{(nodes == 1 ? string.Empty : "s")} of this step's pack added", told[0]);        Assert.Equal([.. pack.Lines.Where(line => line.Id is not null).Select(line => line.Text)], told[1..]);
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
            CodebaseConversationChanges.Make(view, pack, pack.Text, CodebaseContextPack.DefaultTokens));    }

    private static readonly Func<string, bool> All = _ => true;

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
