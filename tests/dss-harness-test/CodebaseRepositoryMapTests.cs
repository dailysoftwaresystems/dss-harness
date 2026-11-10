using DssHarness.Core.Codebase;

namespace DssHarness.Tests;

/// <summary>
/// The repository map's ranking: a declaration's centrality is how many files other than its own and a test's hold a
/// reference that binds to it, raised by how its name matches the task as find weighs a name's words - whole words,
/// never a piece of one, so a task's small words raise nothing - and no declaration of a test's file is ranked; a cap of
/// 0 gives no map at all.
/// </summary>
public sealed class CodebaseRepositoryMapTests
{
    /// <summary>The fixture: a few declarations, one used within its own file and from one other, and a test's file.</summary>
    private static readonly (string Path, string Text)[] Files =
    [
        ("src/Total.cs", "public class Total\n{\n    public int Sum() => 1;\n}\n"),
        ("src/Theater.cs", "public class Theater\n{\n}\n"),
        ("src/Totalizer.cs", "public class Totalizer\n{\n}\n"),
        ("src/Hub.cs", "public class Hub\n{\n    public static int Count() => 1;\n\n    public int Twice() => Count() + Count();\n}\n"),
        ("src/User.cs", "public class User\n{\n    public int Use() => Hub.Count();\n}\n"),
        ("tests/TotalTests.cs", "public class TotalTests\n{\n    [Fact]\n    public void ATotalThatIsSummed_IsTheTotalOfTheShapes() => new Total().Sum();\n}\n\npublic class Shapes\n{\n}\n"),
    ];

    /// <summary>
    /// A declaration's centrality counts the files other than its own whose references bind to it - a use inside its own
    /// file, and a test's, count for nothing.
    /// </summary>
    [Fact]
    public async Task ADeclarationsCentrality_CountsOtherFilesOnly_NeverItsOwnNorATests()
    {
        using var tree = await TreeAsync();
        var view = await QueriesAsync(tree);

        Assert.Equal((1, 0.0), view.CentralityOf(Card(view, "Count").Id, string.Empty));
        Assert.Equal((0, 0.0), view.CentralityOf(Card(view, "Sum").Id, string.Empty));
    }

    /// <summary>
    /// A task raises a declaration whose name holds its words whole - a name holding all of them by its whole share - and
    /// none whose name only holds them as pieces of longer words, nor any for the task's small words, which no name holds.
    /// </summary>
    [Fact]
    public async Task ATaskRaisesANameThatHoldsItsWordsWhole_NeverOneThatHoldsThemAsPieces()
    {
        using var tree = await TreeAsync();
        var view = await QueriesAsync(tree);

        Assert.Equal((0, 1.0), view.CentralityOf(Card(view, "Total").Id, "total"));
        Assert.Equal((0, 0.0), view.CentralityOf(Card(view, "Theater").Id, "fix the total, a total to the end"));
        Assert.Equal((0, 0.0), view.CentralityOf(Card(view, "Totalizer").Id, "fix the total, a total to the end"));

        // The two whose names hold the task whole lead, before the one other files use; neither piece-holder is raised.
        var central = view.Central("total");

        Assert.Equal(["Sum", "Total"], central.Take(2).Select(card => card.Name).Order(StringComparer.Ordinal));
        Assert.Equal("Count", central[2].Name);
    }

    /// <summary>No declaration of a file that declares a test is ranked: the test, its class and what stands beside it.</summary>
    [Fact]
    public async Task NoDeclarationOfATestsFile_IsRanked()
    {
        using var tree = await TreeAsync();
        var view = await QueriesAsync(tree);

        Assert.Equal(["ATotalThatIsSummed_IsTheTotalOfTheShapes", "Shapes", "TotalTests"], view.OutlineFile("tests/TotalTests.cs")!.Select(card => card.Name).Order(StringComparer.Ordinal));
        Assert.Equal(
            ["Count", "Hub", "Sum", "Theater", "Total", "Totalizer", "Twice", "Use", "User"],
            view.Central("the total of the shapes").Select(card => card.Name).Order(StringComparer.Ordinal));
    }

    /// <summary>A cap of 0 gives no map: no text at all, not even its header, and its meta says so.</summary>
    [Fact]
    public async Task ACapOfNothing_GivesNoMap()
    {
        using var tree = await TreeAsync();
        var view = await QueriesAsync(tree);

        var (text, meta) = CodebaseRepositoryMap.Make(view, "total", 0);

        Assert.Equal(string.Empty, text);
        Assert.Equal((0, 0), (meta["declarations"]!.GetValue<int>(), meta["tokens"]!.GetValue<int>()));
        Assert.StartsWith("at commit ", CodebaseRepositoryMap.Make(view, "total", 100).Text, StringComparison.Ordinal);
    }

    /// <summary>The card of the declaration <paramref name="symbol"/> resolves to.</summary>
    private static GraphCard Card(CodebaseGraphQueries view, string symbol)
        => Assert.IsType<GraphResolve.Found>(view.Resolve(symbol)).Card;

    private static async Task<CodebaseGraphTree> TreeAsync()
    {
        var tree = await CodebaseGraphTree.CreateAsync();
        tree.Over([.. CodeGraphExtractors.OfThisBuild.All]);

        foreach (var (path, text) in Files)
        {
            tree.Write(path, text);
        }

        await tree.CommitAsync();
        await tree.SyncAsync();
        return tree;
    }

    private static async Task<CodebaseGraphQueries> QueriesAsync(CodebaseGraphTree tree) => new((await tree.CheckAsync(null, null)).Read);
}
