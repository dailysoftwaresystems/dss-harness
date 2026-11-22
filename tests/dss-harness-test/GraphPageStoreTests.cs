using System.Text.Json.Nodes;
using DssHarness.Core.Codebase;
using DssHarness.Core.FileSystem;

namespace DssHarness.Tests;

/// <summary>
/// The results a conversation's graph answers keep: written to their file and read back by a store made again over it -
/// a resumed conversation's - each "does not read" told with why, never thrown; read from the work a conversation goes on
/// from where its own holds nothing, under its own read rules alone; every result kept for the conversation's life,
/// packed, each directory read once for an ask; and keyed by the ask alone, never its page.
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

        var found = new GraphPageStore(temp.Path, Disk).Find(kept.Key, kept.Digest);

        Assert.Null(found.Why);
        Assert.Equal(Json(kept), Json(found.Kept!));

        // One line a file, compressed: unpacked, a person reads it whole.
        Assert.Equal(Json(kept), Unpacked(Path.Combine(temp.Path, kept.Key + GraphPageStore.Extension)));
    }

    /// <summary>An ask nothing was kept for finds nothing, and says no why.</summary>
    [Fact]
    public void AnAskNothingWasKeptFor_FindsNothing()
    {
        using var temp = new TempDirectory();

        Assert.Equal(GraphPageFind.None, new GraphPageStore(temp.Path, Disk).Find(GraphPageStore.KeyOf("find", new JsonObject { ["query"] = "M" }), new string('a', 64)));
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
        var file = Path.Combine(temp.Path, kept.Key + GraphPageStore.Extension);
        new GraphPageStore(temp.Path, Disk).Keep(kept);
        var whole = Unpacked(file);

        Pack(file, whole.Replace("\"schema\":2", "\"schema\":1", StringComparison.Ordinal));
        Assert.Equal(new GraphPageFind(null, "its schema is 1, and this build reads 2"), new GraphPageStore(temp.Path, Disk).Find(kept.Key, kept.Digest));

        Pack(file, "{");
        Assert.StartsWith("its file does not read: ", new GraphPageStore(temp.Path, Disk).Find(kept.Key, kept.Digest).Why, StringComparison.Ordinal);

        // A file not packed as a result is - written over, or another build's - is one that does not read, never thrown.
        File.WriteAllText(file, whole);
        Assert.StartsWith("its file does not read: ", new GraphPageStore(temp.Path, Disk).Find(kept.Key, kept.Digest).Why, StringComparison.Ordinal);

        Pack(file, whole.Replace("\"leading\":[\"the card\"],", string.Empty, StringComparison.Ordinal));
        var missing = new GraphPageStore(temp.Path, Disk).Find(kept.Key, kept.Digest);
        Assert.Null(missing.Kept);
        Assert.StartsWith("its file does not read: ", missing.Why, StringComparison.Ordinal);

        Pack(file, whole.Replace(kept.Key, new string('0', 64), StringComparison.Ordinal));
        Assert.Equal(new GraphPageFind(null, "its file is of another ask"), new GraphPageStore(temp.Path, Disk).Find(kept.Key, kept.Digest));

        Pack(file, whole);
        Assert.Equal(new GraphPageFind(null, "it was kept under other read rules"), new GraphPageStore(temp.Path, Disk, reads: "0123456789abcdef").Find(kept.Key, kept.Digest));
    }

    /// <summary>A result that cannot be written - its directory is a file - is said, never thrown, and is held in memory for the conversation's process.</summary>
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
        Assert.Equal(Json(kept), Json(store.Find(kept.Key, kept.Digest).Kept!));
        Assert.Equal(GraphPageFind.None, new GraphPageStore(blocked, Disk).Find(kept.Key, kept.Digest));
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

        var found = new GraphPageStore(own, Disk, earlier: [temp.Combine("none", "graph-pages"), earlier]).Find(kept.Key, kept.Digest);

        Assert.Equal(Json(kept), Json(found.Kept!));
        Assert.True(File.Exists(Path.Combine(own, kept.Key + GraphPageStore.Extension)), "the result read from the earlier work was not kept as the store's own");
        Assert.Equal(Json(kept), Json(new GraphPageStore(temp.Combine("review", "graph-pages"), Disk, earlier: [own]).Find(kept.Key, kept.Digest).Kept!));

        var narrowed = new GraphPageStore(temp.Combine("narrow", "graph-pages"), Disk, reads: "0123456789abcdef", earlier: [earlier]).Find(kept.Key, kept.Digest);

        Assert.Equal(new GraphPageFind(null, "it was kept under other read rules"), narrowed);
        Assert.False(Directory.Exists(temp.Combine("narrow", "graph-pages")));
    }

    /// <summary>
    /// Every result is kept for the conversation's life, however many there are: each in its file, packed smaller than its
    /// line, and each held in memory, so a later page reads it with its file gone; a store made again over the directory -
    /// a resumed conversation's - reads every one.
    /// </summary>
    [Fact]
    public void TheStore_KeepsEveryResult_ForTheConversationsLife_Packed()
    {
        using var temp = new TempDirectory();
        var store = new GraphPageStore(temp.Path, Disk);
        var results = Enumerable.Range(0, 100).Select(at => Kept($"find M{at}")).ToList();

        foreach (var result in results)
        {
            Assert.Null(store.Keep(result));
        }

        Assert.Equal(
            results.Select(result => result.Key + GraphPageStore.Extension).Order(StringComparer.Ordinal),
            Directory.EnumerateFiles(temp.Path).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.All(results, result => Assert.True(
            new FileInfo(Path.Combine(temp.Path, result.Key + GraphPageStore.Extension)).Length < System.Text.Encoding.UTF8.GetByteCount(Json(result)),
            "a result was not kept packed"));
        Assert.All(results, result => Assert.Equal(Json(result), Json(new GraphPageStore(temp.Path, Disk).Find(result.Key, result.Digest).Kept!)));

        foreach (var file in Directory.EnumerateFiles(temp.Path))
        {
            File.Delete(file);
        }

        Assert.All(results, result => Assert.Equal(Json(result), Json(store.Find(result.Key, result.Digest).Kept!)));
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

        // A member left null is asked of as any other: said as what is wrong, never thrown from reading it.
        Assert.Equal("its leading lines are lines", Assert.Throws<ArgumentException>(() => store.Keep(Kept("find M") with { Leading = null! })).Message.Split(" (")[0]);
        Assert.Equal("it holds a list of sections", Assert.Throws<ArgumentException>(() => store.Keep(Kept("find M") with { Sections = null! })).Message.Split(" (")[0]);
        Assert.Empty(Directory.EnumerateFileSystemEntries(temp.Path));
    }

    /// <summary>
    /// A store is asked under the digest of the graph it answers from, and the nearest result kept under that digest
    /// answers - in memory, its own directory or the earlier work's - whatever a nearer one kept under another digest
    /// holds; where none is kept under it but one is under another, it says the graph moved, and never why one does not
    /// read.
    /// </summary>
    [Fact]
    public void AStore_AnswersTheNearestResultKeptUnderTheDigestAsked_AndSaysTheGraphMovedWhereNoneIs()
    {
        using var temp = new TempDirectory();
        var own = temp.Combine("implement#2", "graph-pages");
        var visit = temp.Combine("implement#1", "graph-pages");
        var plan = temp.Combine("plan", "graph-pages");
        var older = Kept("find M") with { Digest = new string('c', 64), Leading = ["plan's card"] };
        var current = Kept("find M") with { Leading = ["the visit's card"] };
        new GraphPageStore(plan, Disk).Keep(older);
        new GraphPageStore(visit, Disk).Keep(current);

        // The nearest of the earlier work keeps the ask under another digest: passed over for the one that keeps it under
        // the digest asked, though that one is further.
        var store = new GraphPageStore(own, Disk, earlier: [plan, visit]);
        var found = store.Find(current.Key, current.Digest);

        Assert.Equal(Json(current), Json(found.Kept!));
        Assert.False(found.Moved);

        // Kept as the store's own, it answers from memory and from its own file alike, and the older digest still reads
        // where it is asked.
        Assert.Equal(Json(current), Json(new GraphPageStore(own, Disk).Find(current.Key, current.Digest).Kept!));
        Assert.Equal(Json(older), Json(store.Find(older.Key, older.Digest).Kept!));

        // A digest no result is kept under: the graph moved since page 1, and no why of a file that does not read.
        Assert.Equal(new GraphPageFind(null, null) { Moved = true }, store.Find(current.Key, new string('d', 64)));
    }

    /// <summary>A result is kept whole whatever it holds: written, held, and read back by a store made again, never refused for its size.</summary>
    [Fact]
    public void AResult_IsKeptWhole_WhateverItHolds()
    {
        using var temp = new TempDirectory();
        var wide = Kept("find Wide") with { Leading = [new string('w', 9 << 20)] };

        Assert.Null(new GraphPageStore(temp.Path, Disk).Keep(wide));
        Assert.Equal(Json(wide), Json(new GraphPageStore(temp.Path, Disk).Find(wide.Key, wide.Digest).Kept!));
    }

    /// <summary>
    /// What a directory keeps for an ask is read once: a later find of the ask - under the digest it was kept under, or
    /// another - reads neither the store's own directory nor the earlier work's again.
    /// </summary>
    [Fact]
    public void WhatADirectoryKeepsForAnAsk_IsReadOnce()
    {
        using var temp = new TempDirectory();
        var own = temp.Combine("implement#2", "graph-pages");
        var earlier = temp.Combine("implement#1", "graph-pages");
        var kept = Kept("find M");
        new GraphPageStore(earlier, Disk).Keep(kept);
        var reads = new OpensCounted(Disk);
        var store = new GraphPageStore(own, reads, earlier: [earlier]);

        Assert.True(store.Find(kept.Key, new string('d', 64)).Moved);
        Assert.True(store.Find(kept.Key, new string('e', 64)).Moved);
        Assert.Equal(Json(kept), Json(store.Find(kept.Key, kept.Digest).Kept!));
        Assert.Equal(Json(kept), Json(store.Find(kept.Key, kept.Digest).Kept!));

        Assert.Equal(1, reads.Opened(Path.Combine(earlier, kept.Key + GraphPageStore.Extension)));
        Assert.Equal(1, reads.Standings(Path.Combine(own, kept.Key + GraphPageStore.Extension)));
    }

    /// <summary>A step's graph-pages directory is put away whole, and one never made is no failure.</summary>
    [Fact]
    public void AStoresDirectory_IsPutAwayWhole()
    {
        using var temp = new TempDirectory();
        var directory = temp.Combine("graph-pages");
        new GraphPageStore(directory, Disk).Keep(Kept("find M"));

        Assert.Null(GraphPageStore.PutAway(directory, Disk));
        Assert.False(Directory.Exists(directory));
        Assert.Null(GraphPageStore.PutAway(directory, Disk));
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

    /// <summary>The line the kept file at <paramref name="file"/> holds, unpacked.</summary>
    private static string Unpacked(string file)
    {
        using var reader = new StreamReader(new System.IO.Compression.GZipStream(File.OpenRead(file), System.IO.Compression.CompressionMode.Decompress));
        return reader.ReadToEnd();
    }

    /// <summary>Writes <paramref name="text"/> to the kept file at <paramref name="file"/>, packed as a result is.</summary>
    private static void Pack(string file, string text)
    {
        using var packed = new System.IO.Compression.GZipStream(File.Create(file), System.IO.Compression.CompressionLevel.Optimal);
        packed.Write(System.Text.Encoding.UTF8.GetBytes(text));
    }

    /// <summary>A file system that counts, by path, each file opened and each place looked up.</summary>
    private sealed class OpensCounted(IFileSystem inner) : PassThroughFileSystem(inner)
    {
        private readonly Dictionary<string, int> _opened = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _standings = new(StringComparer.Ordinal);

        public int Opened(string path) => _opened.GetValueOrDefault(path);

        public int Standings(string path) => _standings.GetValueOrDefault(path);

        public override Stream OpenRead(string path)
        {
            _opened[path] = Opened(path) + 1;
            return base.OpenRead(path);
        }

        public override FileStanding? StandingOf(string path)
        {
            _standings[path] = Standings(path) + 1;
            return base.StandingOf(path);
        }
    }
}
