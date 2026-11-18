using System.Text.Json.Nodes;
using DssHarness.Core.Codebase;
using DssHarness.Core.FileSystem;

namespace DssHarness.Tests;

/// <summary>
/// The results a conversation's graph answers keep: written to their file and read back by a store made again over it -
/// a resumed conversation's - each "does not read" told with why, never thrown; read from the work a conversation goes on
/// from where its own holds nothing, under its own read rules alone; bounded in memory and on disk; and keyed by the ask
/// alone, never its page.
/// </summary>
public sealed class GraphPageStoreTests
{
    private static readonly IFileSystem Disk = new PhysicalFileSystem(DssHarness.Core.Platform.FilePermissionsFactory.Create());

    /// <summary>A result one store kept is read by another made over the same directory: the file is what answers, whole.</summary>
    [Fact]
    public void AResultKept_IsReadBackByAStoreMadeAgainOverItsDirectory()
    {
        using var temp = new TempDirectory();
        var kept = Kept("find M");

        Assert.Null(new GraphPageStore(temp.Path, Disk).Keep(kept));

        var found = new GraphPageStore(temp.Path, Disk).Find(kept.Key);

        Assert.Null(found.Why);
        Assert.Equal(Json(kept), Json(found.Kept!));

        // One line a file: a result is held whole while it is read, and a person still reads it.
        Assert.Single(File.ReadAllLines(Path.Combine(temp.Path, kept.Key + ".json")));
    }

    /// <summary>An ask nothing was kept for finds nothing, and says no why.</summary>
    [Fact]
    public void AnAskNothingWasKeptFor_FindsNothing()
    {
        using var temp = new TempDirectory();

        Assert.Equal(GraphPageFind.None, new GraphPageStore(temp.Path, Disk).Find(GraphPageStore.KeyOf("find", new JsonObject { ["query"] = "M" })));
    }

    /// <summary>
    /// A kept file that does not read - of another schema, no JSON, missing a member, of another ask, kept under other read
    /// rules - is found as nothing, with why, and is never thrown.
    /// </summary>
    [Fact]
    public void AKeptFileThatDoesNotRead_IsFoundAsNothing_WithWhy()
    {
        using var temp = new TempDirectory();
        var kept = Kept("find M");
        var file = Path.Combine(temp.Path, kept.Key + ".json");
        new GraphPageStore(temp.Path, Disk).Keep(kept);
        var whole = File.ReadAllText(file);

        File.WriteAllText(file, whole.Replace("\"schema\":2", "\"schema\":1", StringComparison.Ordinal));
        Assert.Equal(new GraphPageFind(null, "its schema is 1, and this build reads 2"), new GraphPageStore(temp.Path, Disk).Find(kept.Key));

        File.WriteAllText(file, "{");
        Assert.StartsWith("its file does not read: ", new GraphPageStore(temp.Path, Disk).Find(kept.Key).Why, StringComparison.Ordinal);

        File.WriteAllText(file, whole.Replace("\"leading\":[\"the card\"],", string.Empty, StringComparison.Ordinal));
        var missing = new GraphPageStore(temp.Path, Disk).Find(kept.Key);
        Assert.Null(missing.Kept);
        Assert.StartsWith("its file does not read: ", missing.Why, StringComparison.Ordinal);

        File.WriteAllText(file, whole.Replace(kept.Key, new string('0', 64), StringComparison.Ordinal));
        Assert.Equal(new GraphPageFind(null, "its file is of another ask"), new GraphPageStore(temp.Path, Disk).Find(kept.Key));

        File.WriteAllText(file, whole);
        Assert.Equal(new GraphPageFind(null, "it was kept under other read rules"), new GraphPageStore(temp.Path, Disk, reads: "0123456789abcdef").Find(kept.Key));
    }

    /// <summary>A result that cannot be written - its directory is a file - is said, never thrown, and is held in memory for the process that made it.</summary>
    [Fact]
    public void AResultThatCannotBeWritten_SaysWhy_AndIsHeldInMemory()
    {
        using var temp = new TempDirectory();
        var blocked = temp.Combine("graph-pages");
        File.WriteAllText(blocked, "not a directory");
        var store = new GraphPageStore(blocked, Disk);
        var kept = Kept("find M");

        var why = store.Keep(kept);

        Assert.NotNull(why);
        Assert.Same(kept, store.Find(kept.Key).Kept);
        Assert.Equal(GraphPageFind.None, new GraphPageStore(blocked, Disk).Find(kept.Key));
    }

    /// <summary>
    /// A conversation that goes on with earlier work reads what that work kept where its own store holds nothing, the
    /// nearest first, and keeps it as its own - so a conversation that goes on from it in turn reads it too - but never a
    /// result kept under other read rules.
    /// </summary>
    [Fact]
    public void AStoreReadsTheEarlierWorksResults_KeepingThemAsItsOwn_UnderItsReadRulesAlone()
    {
        using var temp = new TempDirectory();
        var earlier = temp.Combine("plan", "graph-pages");
        var own = temp.Combine("implement", "graph-pages");
        var kept = Kept("find M");
        new GraphPageStore(earlier, Disk).Keep(kept);

        var found = new GraphPageStore(own, Disk, earlier: [temp.Combine("none", "graph-pages"), earlier]).Find(kept.Key);

        Assert.Equal(Json(kept), Json(found.Kept!));
        Assert.True(File.Exists(Path.Combine(own, kept.Key + ".json")), "the result read from the earlier work was not kept as the store's own");
        Assert.Equal(Json(kept), Json(new GraphPageStore(temp.Combine("review", "graph-pages"), Disk, earlier: [own]).Find(kept.Key).Kept!));

        var narrowed = new GraphPageStore(temp.Combine("narrow", "graph-pages"), Disk, reads: "0123456789abcdef", earlier: [earlier]).Find(kept.Key);

        Assert.Equal(new GraphPageFind(null, "it was kept under other read rules"), narrowed);
        Assert.False(Directory.Exists(temp.Combine("narrow", "graph-pages")));
    }

    /// <summary>
    /// The store is bounded: the most recent results are held in memory, the rest read from disk, and the most recently
    /// kept are on disk, the oldest put away as one more is kept.
    /// </summary>
    [Fact]
    public void TheStore_IsBoundedInMemoryAndOnDisk()
    {
        using var temp = new TempDirectory();
        var store = new GraphPageStore(temp.Path, Disk);
        var results = Enumerable.Range(0, GraphPageStore.MostKept + 1).Select(at => Kept($"find M{at}")).ToList();

        foreach (var result in results)
        {
            Assert.Null(store.Keep(result));
        }

        // On disk, the most recently kept alone: the first is put away.
        Assert.Equal(
            results.Skip(1).Select(result => result.Key + ".json").Order(StringComparer.Ordinal),
            Directory.EnumerateFiles(temp.Path).Select(Path.GetFileName).Order(StringComparer.Ordinal));

        // In memory, the most recent alone: with their files gone, the newest still answers and an older one does not.
        foreach (var file in Directory.EnumerateFiles(temp.Path))
        {
            File.Delete(file);
        }

        Assert.Same(results[^1], store.Find(results[^1].Key).Kept);
        Assert.Same(results[^GraphPageStore.MostHeld], store.Find(results[^GraphPageStore.MostHeld].Key).Kept);
        Assert.Equal(GraphPageFind.None, store.Find(results[^(GraphPageStore.MostHeld + 1)].Key));
    }

    /// <summary>An ask's key is its operation and its members, whatever their order and with nothing of a member left null; a page is never part of it.</summary>
    [Fact]
    public void AnAsksKey_IsItsOperationAndMembers_WhateverTheirOrder_AndNeverAPage()
    {
        var key = GraphPageStore.KeyOf("related", new JsonObject { ["symbol"] = "csharp:Demo.Total", ["relation"] = "callers", ["depth"] = 1 });

        Assert.Equal(key, GraphPageStore.KeyOf("related", new JsonObject { ["depth"] = 1, ["relation"] = "callers", ["symbol"] = "csharp:Demo.Total", ["kind"] = null }));
        Assert.NotEqual(key, GraphPageStore.KeyOf("related", new JsonObject { ["symbol"] = "csharp:Demo.Total", ["relation"] = "callers", ["depth"] = 2 }));
        Assert.NotEqual(key, GraphPageStore.KeyOf("node", new JsonObject { ["symbol"] = "csharp:Demo.Total", ["relation"] = "callers", ["depth"] = 1 }));
        Assert.Throws<ArgumentException>(() => GraphPageStore.KeyOf("find", new JsonObject { ["query"] = "M", ["page"] = 2 }));
    }

    /// <summary>A result that is no result, or one of other read rules than its store's, is refused as it is kept.</summary>
    [Fact]
    public void AResultThatIsNoResult_IsRefusedAsItIsKept()
    {
        using var temp = new TempDirectory();
        var store = new GraphPageStore(temp.Path, Disk);

        Assert.Throws<ArgumentException>(() => store.Keep(Kept("find M") with { Digest = " " }));
        Assert.Throws<ArgumentException>(() => store.Keep(Kept("find M") with { Read = [new GraphFileRead("a.cs", "")] }));
        Assert.Throws<ArgumentException>(() => store.Keep(Kept("find M") with { Reads = "0123456789abcdef" }));
        Assert.Empty(Directory.EnumerateFileSystemEntries(temp.Path));
    }

    /// <summary>A result of <paramref name="asked"/>, of two pages' worth, under no read rules: its members left null - its read rules, a section's caption - read back as null.</summary>
    private static GraphPageKept Kept(string asked)
        => new(
            GraphPageKept.CurrentSchema,
            GraphPageStore.KeyOf("find", new JsonObject { ["query"] = asked }),
            new string('a', 64),
            null,
            asked,
            ["the card"],
            [new GraphSection("callers (2):", [new GraphEntry("  caller 1"), new GraphEntry("1\tline", Lines: 1, GoesOn: "A", Node: "csharp:A", File: "src/A.cs")], 1), new GraphSection(null, [new GraphEntry("a line of no caption")], 1)],
            ["1 node(s) left out: in files your reads do not reach"],
            ["csharp:A"],
            [new GraphFileRead("src/A.cs", new string('b', 64))]);

    private static string Json(GraphPageKept kept) => System.Text.Json.JsonSerializer.Serialize(kept, JsonStateFile.LineOptions);
}
