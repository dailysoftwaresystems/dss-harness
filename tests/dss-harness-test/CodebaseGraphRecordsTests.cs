using System.Reflection;
using System.Text.Json;
using DssHarness.Core.Codebase;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Repository;

namespace DssHarness.Tests;

/// <summary>
/// The codebase graph's model and records hold themselves to their shape: each thing an extract, a manifest, a stored
/// extract and a link result can get wrong is refused by name, and the ids, handles and digests are computed one way.
/// </summary>
public sealed class CodebaseGraphRecordsTests
{
    private static readonly string Blob = new('a', 40);

    private static readonly ExtractNode Node = new("toy:A", CodeNodeKinds.Type, "A", "A", 1, 3, "class A") { BodyStart = 2, Summary = "An A.", MinArity = 0, MaxArity = 1 };

    /// <summary>
    /// Each vocabulary's list holds every name the vocabulary declares, each once and nothing else - the edge kinds' list
    /// with their meanings among them - so a name added to one is never refused by the records, or left out of help, for
    /// want of its line in a list.
    /// </summary>
    [Fact]
    public void EachVocabularysList_HoldsEveryNameItDeclares_EachOnce()
    {
        static string[] Declared(Type vocabulary) =>
        [
            .. vocabulary.GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(field => field.IsLiteral && field.FieldType == typeof(string))
                .Select(field => (string)field.GetRawConstantValue()!)
                .Order(StringComparer.Ordinal),
        ];

        Assert.Equal(Declared(typeof(CodeEdgeKinds)), CodeEdgeKinds.Described.Select(described => described.Kind).Order(StringComparer.Ordinal));
        Assert.Equal(Declared(typeof(CodeEdgeKinds)), CodeEdgeKinds.All.Order(StringComparer.Ordinal));
        Assert.Equal(Declared(typeof(CodeConfidences)), CodeConfidences.All.Order(StringComparer.Ordinal));
        Assert.Equal(Declared(typeof(CodeFileStates)), CodeFileStates.All.Order(StringComparer.Ordinal));
        Assert.Equal(Declared(typeof(CodeImportForms)), CodeImportForms.All.Order(StringComparer.Ordinal));
        Assert.Equal(Declared(typeof(CodeQualifierKinds)), CodeQualifierKinds.All.Order(StringComparer.Ordinal));
        Assert.Equal(Declared(typeof(CodeNodeKinds)).Where(kind => kind != CodeNodeKinds.File), CodeNodeKinds.Extracted.Order(StringComparer.Ordinal));
    }

    /// <summary>A sound extract has no problem; each way an extract can be wrong is refused, saying what is.</summary>
    [Fact]
    public void AnExtract_IsRefusedForEachThingItGetsWrong()
    {
        var sound = new FileExtract([Node, Node with { Id = "toy:A.b", Kind = CodeNodeKinds.Member, Name = "b", Qualified = "A.b", Start = 2, End = 3, Parent = "toy:A" }])
        {
            Scopes = [new ExtractScope("app", Open: true)],
            Imports = [new ExtractImport(CodeImportForms.Scope, "lib", 1) { Prefix = "l" }],
            AddressRoots = [new ExtractAddressRoot("package:x/", "lib")],
            References = [new ExtractReference("toy:A", CodeEdgeKinds.Calls, "Go", 2) { Arity = 1, Qualifier = new ExtractQualifier(CodeQualifierKinds.Named, "B") }],
            Generated = "a generator wrote it",
            Dynamic = [new ExtractDynamic(3, "a call through a pointer")],
        };

        Assert.Null(sound.Problem("toy", 3));
        Assert.Null((sound with { Nodes = [Node with { Summary = "An \U0001F600 A." }] }).Problem("toy", 3));

        var wrong = new (FileExtract Extract, string Problem)[]
        {
            (sound with { Nodes = [Node, Node] }, "two of its nodes are 'toy:A'"),
            (sound with { Nodes = [Node with { Id = "" }] }, "it has no id"),
            (sound with { Nodes = [Node with { Id = "other:A" }] }, "its id does not begin 'toy:'"),
            (sound with { Nodes = [Node with { Kind = CodeNodeKinds.File }] }, "its kind is 'file'"),
            (sound with { Nodes = [Node with { Name = " " }] }, "it has no name"),
            (sound with { Nodes = [Node with { End = 4 }] }, "its lines 1-4 are not lines of the file's 3"),
            (sound with { Nodes = [Node with { Start = 0 }] }, "its lines 0-3 are not lines"),
            (sound with { Nodes = [Node with { Start = 3, End = 2 }] }, "its lines 3-2 are not lines"),
            (sound with { Nodes = [Node with { BodyStart = 4 }] }, "its body begins on line 4"),
            (sound with { Nodes = [Node with { Signature = new string('x', FileExtract.MostSignature + 1) }] }, "its signature is not one line"),
            (sound with { Nodes = [Node with { Signature = "class\nA" }] }, "its signature is not one line"),
            (sound with { Nodes = [Node with { Summary = "one\rtwo" }] }, "its summary is not one line"),
            (sound with { Nodes = [Node with { MinArity = -1 }] }, "its arity is no range"),
            (sound with { Nodes = [Node with { MinArity = 2, MaxArity = 1 }] }, "its arity is no range"),
            (sound with { Nodes = [Node with { MinArity = null, MaxArity = 1 }] }, "its arity is no range"),
            (sound with { Nodes = [Node with { Digest = "ABC" }] }, "its digest is no SHA-256"),
            (sound with { Nodes = [Node, Node with { Id = "toy:B", Parent = "toy:Z" }] }, "is declared inside 'toy:Z', which it holds no node of"),
            (sound with { Nodes = [Node with { End = 2 }, Node with { Id = "toy:B", Start = 2, End = 3, Parent = "toy:A" }] }, "whose lines do not hold it"),
            (sound with { Nodes = [Node with { Parent = "toy:A" }] }, "its node 'toy:A' is declared inside itself, through 'toy:A'"),
            (sound with { Nodes = [Node with { Parent = "toy:B" }, Node with { Id = "toy:B", Parent = "toy:A" }] }, "is declared inside itself, through"),
            (sound with { Nodes = [Node with { Summary = "cut \uD83D" }] }, "it holds text that is no whole UTF-16, a surrogate cut from its pair: 'cut \uD83D'"),
            (sound with { Nodes = [Node with { Name = "\uDE00x" }] }, "a surrogate cut from its pair"),
            (sound with { References = [new ExtractReference("toy:A", CodeEdgeKinds.Calls, "Go\uD83D", 1)] }, "a surrogate cut from its pair"),
            (sound with { Scopes = [new ExtractScope(" ", Open: true)] }, "it holds a scope with no name"),
            (sound with { Scopes = [new ExtractScope("app", true), new ExtractScope("app", false)] }, "it declares the scope 'app' twice"),
            (sound with { Imports = [new ExtractImport("wildcard", "lib", 1)] }, "its form is 'wildcard'"),
            (sound with { Imports = [new ExtractImport(CodeImportForms.Relative, " ", 1)] }, "it names nothing"),
            (sound with { Imports = [new ExtractImport(CodeImportForms.Scope, "lib", 4)] }, "it is on line 4, outside the file's 3"),
            (sound with { Imports = [new ExtractImport(CodeImportForms.Scope, "lib", 1) { Prefix = "" }] }, "its prefix is empty"),
            (sound with { AddressRoots = [new ExtractAddressRoot("package:x/", "")] }, "an address root with no prefix or no directory"),
            (sound with { References = [new ExtractReference("toy:Z", CodeEdgeKinds.Calls, "Go", 1)] }, "is made from 'toy:Z', which it holds no node of"),
            (sound with { References = [new ExtractReference("toy:A", CodeEdgeKinds.Dispatches, "Go", 1)] }, "its kind is 'dispatches'"),
            (sound with { References = [new ExtractReference("toy:A", CodeEdgeKinds.Calls, "", 1)] }, "it names nothing"),
            (sound with { References = [new ExtractReference("toy:A", CodeEdgeKinds.Calls, "Go", 0)] }, "it is on line 0"),
            (sound with { References = [new ExtractReference("toy:A", CodeEdgeKinds.Calls, "Go", 1) { Arity = -1 }] }, "its arity is below zero"),
            (sound with { References = [new ExtractReference("toy:A", CodeEdgeKinds.Calls, "Go", 1) { Qualifier = new ExtractQualifier("super") }] }, "its qualifier's kind is 'super'"),
            (sound with { References = [new ExtractReference("toy:A", CodeEdgeKinds.Calls, "Go", 1) { Qualifier = new ExtractQualifier(CodeQualifierKinds.Prefix) }] }, "its qualifier names nothing"),
            (sound with { Generated = " " }, "it says it is generated and not why"),
            (sound with { Dynamic = [new ExtractDynamic(1, "")] }, "it marks a line dynamic and not why"),
            (sound with { Dynamic = [new ExtractDynamic(9, "macro")] }, "it marks line 9 dynamic, outside its 3 lines"),
        };

        Assert.All(wrong, pair => Assert.Contains(pair.Problem, pair.Extract.Problem("toy", 3), StringComparison.Ordinal));
    }

    /// <summary>The digests the core sets are each node's lines', and a node whose lines are not the file's is left without one.</summary>
    [Fact]
    public void TheCoresDigests_AreEachNodesLines_AndNoneWhereItsLinesAreNotTheFiles()
    {
        var lines = CodeFileText.Lines("class A\r\n  go\r\nend\r\n");
        var digested = new FileExtract([Node, Node with { Id = "toy:B", End = 9 }]).WithDigests(lines);

        Assert.Equal(CodeGraphIds.Sha256("class A\n  go\nend"), digested.Nodes[0].Digest);
        Assert.Equal(CodeGraphIds.Digest(CodeFileText.Lines("class A\n  go\nend"), 1, 3), digested.Nodes[0].Digest);
        Assert.Null(digested.Nodes[1].Digest);
        Assert.Throws<ArgumentOutOfRangeException>(() => CodeGraphIds.Digest(lines, 2, 4));

        // A digest the implementation set is never kept: the core's replaces it, and none is left where the lines are not the file's.
        var set = new FileExtract([Node with { Digest = new string('0', 64) }, Node with { Id = "toy:B", End = 9, Digest = new string('0', 64) }]).WithDigests(lines);

        Assert.Equal(digested.Nodes[0].Digest, set.Nodes[0].Digest);
        Assert.Null(set.Nodes[1].Digest);
    }

    /// <summary>A sync line is refused for each thing it gets wrong, and the store appends none it would not read back.</summary>
    [Fact]
    public void ASyncLine_IsRefusedForEachThingItGetsWrong_AndTheStoreAppendsNone()
    {
        var line = new CodeGraphSyncLine(DateTimeOffset.UnixEpoch, "base", "map#1", 3, 3, 2, 1, 0, 0.5) { Rebuilt = "no graph was read", SeededFrom = "earlier" };

        Assert.Null(line.Problem());
        Assert.Equal("it names no view", (line with { View = " " }).Problem());
        Assert.Equal("it says nothing of why it synced", (line with { Why = "" }).Problem());
        Assert.Equal("it counts files below zero", (line with { Listed = -1 }).Problem());
        Assert.Equal("it counts files below zero", (line with { Hashed = -1 }).Problem());
        Assert.Equal("it counts files below zero", (line with { Extracted = -1 }).Problem());
        Assert.Equal("it counts files below zero", (line with { Reused = -1 }).Problem());
        Assert.Equal("it counts files below zero", (line with { Removed = -1 }).Problem());
        Assert.Equal("its time is no time a clock gives", (line with { Seconds = double.NaN }).Problem());
        Assert.Equal("its time is no time a clock gives", (line with { Seconds = -0.1 }).Problem());
        Assert.Equal("its time is no time a clock gives", (line with { Seconds = double.PositiveInfinity }).Problem());
        Assert.Equal("it says the graph was built again and not why", (line with { Rebuilt = " " }).Problem());
        Assert.Equal("it says it was seeded and not from which run", (line with { SeededFrom = "" }).Problem());

        using var directory = new TempDirectory();
        var (store, layout) = Store(directory);

        var refused = Assert.Throws<InvalidOperationException>(() => store.AppendGraphSync(layout, line with { Seconds = double.NaN }));
        Assert.Contains("its time is no time a clock gives", refused.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(layout.GraphSyncFile));

        store.AppendGraphSync(layout, line);
        Assert.Single(File.ReadAllLines(layout.GraphSyncFile));
    }

    /// <summary>
    /// Every record of the graph reads back from the strict store as it was written - an extract with every field, a
    /// manifest whose entry names no blob, a link result - and one the store would not read back is never written.
    /// </summary>
    [Fact]
    public void EachRecord_ReadsBackFromTheStore_AsItWasWritten()
    {
        using var directory = new TempDirectory();
        var (store, layout) = Store(directory);
        var lines = CodeFileText.Lines("class A\n  go\nend\n");
        var extract = new FileExtract([Node with { SubKind = "class", Scope = "app", Modifiers = ["public", "static"], Prototype = true, Test = true, Summary = "An \U0001F600 A." }, Node with { Id = "toy:A.b", Kind = CodeNodeKinds.Member, Name = "b", Qualified = "A.b", Start = 2, End = 3, Parent = "toy:A", BodyStart = null, MinArity = null, MaxArity = null }])
        {
            Scopes = [new ExtractScope("app", Open: true) { Parent = "root" }, new ExtractScope("root", Open: false)],
            Imports = [new ExtractImport(CodeImportForms.Scope, "lib", 1) { Prefix = "l", Show = ["X"], Hide = ["Y"], Global = true }],
            AddressRoots = [new ExtractAddressRoot("package:x/", "lib")],
            References = [new ExtractReference("toy:A", CodeEdgeKinds.Calls, "Go", 2) { Arity = 1, Qualifier = new ExtractQualifier(CodeQualifierKinds.Named, "B") }],
            Generated = "a generator wrote it",
            Dynamic = [new ExtractDynamic(3, "a call through a pointer")],
        }.WithDigests(lines);
        var stored = new CodeGraphStoredExtract(CodeGraphManifest.CurrentSchema, "toy", 1, Blob, "src/a.toy", lines.Count, extract);
        var file = layout.GraphExtractFile(CodeGraphIds.ExtractName(Blob, "src/a.toy", "toy", 1));

        store.WriteGraphExtract(file, stored);
        Assert.Equal(Json(stored), Json(store.ReadGraphExtract(file)!));

        var files = new Dictionary<string, CodeGraphFileEntry>(StringComparer.Ordinal)
        {
            ["src/a.toy"] = new(Blob, CodeFileStates.Parsed, 10) { Language = "toy", Extract = CodeGraphIds.ExtractName(Blob, "src/a.toy", "toy", 1) },
            ["link"] = new(null, CodeFileStates.Link, 0),
        };
        var extractors = new[] { new CodeGraphExtractorVersion("toy", 1) };
        var manifest = new CodeGraphManifest(CodeGraphManifest.CurrentSchema, "1.0.0", extractors, directory.Path, null, true, DateTimeOffset.UnixEpoch, files, CodeGraphManifest.DigestOf(extractors, files));

        store.WriteGraphManifest(layout, manifest);
        var manifestRead = store.ReadGraphManifest(layout)!;
        Assert.Null(manifestRead.Files["link"].Blob);
        Assert.Null(manifestRead.Head);
        Assert.Equal(Json(manifest), Json(manifestRead));

        var symbols = new CodeGraphSymbolsFile(
            CodeGraphManifest.CurrentSchema,
            manifest.Digest,
            CodeGraphLinker.Revision,
            [new("file:src/a.toy", "abcdef", CodeNodeKinds.File, ["src/a.toy"]), new("toy:A", "123456", CodeNodeKinds.Type, ["src/a.toy"])],
            [new CodeEdge("file:src/a.toy", "toy:A", CodeEdgeKinds.Contains, 1, CodeConfidences.Exact)],
            new Dictionary<string, int> { ["toy"] = 1 },
            new Dictionary<string, int> { ["toy"] = 2 });

        store.WriteGraphSymbols(layout, symbols);
        Assert.Equal(Json(symbols), Json(store.ReadGraphSymbols(layout)!));

        // A record the store would not read back is refused before a byte is written.
        var other = layout.GraphExtractFile(CodeGraphIds.ExtractName(Blob, "src/b.toy", "toy", 1));
        var refused = Assert.ThrowsAny<Exception>(() => store.WriteGraphExtract(other, stored with { Extract = extract with { Nodes = [Node with { Summary = "\uD83D" }] } }));
        Assert.Contains("a surrogate cut from its pair", refused.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(other));
    }

    /// <summary>A handle is the first six characters of an id's digest, lengthened for every id that shares them until none does.</summary>
    [Fact]
    public void AHandle_IsSixCharacters_LengthenedOnlyWhereTwoShareThem()
    {
        var ids = Enumerable.Range(0, 20_000).Select(at => $"toy:N{at}").ToList();
        var hashes = ids.ToDictionary(id => id, CodeGraphIds.Sha256, StringComparer.Ordinal);
        var groups = ids.GroupBy(id => hashes[id][..CodeGraphIds.HandleLength], StringComparer.Ordinal).ToList();
        var clash = groups.First(group => group.Count() > 1).ToList();
        var alone = groups.First(group => group.Count() == 1).Single();

        var handles = CodeGraphIds.Handles(ids);

        Assert.Equal(ids.Count, handles.Values.Distinct(StringComparer.Ordinal).Count());
        // Each of a clash is lengthened exactly past what it shares with another, and no further.
        static int Shared(string one, string other)
        {
            var at = 0;

            while (at < one.Length && one[at] == other[at])
            {
                at++;
            }

            return at;
        }

        Assert.All(clash, id => Assert.Equal(1 + clash.Where(other => other != id).Max(other => Shared(hashes[id], hashes[other])), handles[id].Length));
        Assert.Equal(hashes[alone][..CodeGraphIds.HandleLength], handles[alone]);
        Assert.All(handles, pair => Assert.StartsWith(pair.Value, hashes[pair.Key], StringComparison.Ordinal));
    }

    /// <summary>A file entry is refused for each thing it gets wrong: its state, its blob, its size, and an extract named where it was not read.</summary>
    [Fact]
    public void AFileEntry_IsRefusedForEachThingItGetsWrong()
    {
        var read = new CodeGraphFileEntry(Blob, CodeFileStates.Parsed, 10) { Language = "toy", Extract = "aa/x.toy-1.json" };

        Assert.Null(read.Problem());
        Assert.Null(new CodeGraphFileEntry(null, CodeFileStates.Link, 0).Problem());
        Assert.Null(new CodeGraphFileEntry(new string('b', 64), CodeFileStates.Binary, 3).Problem());

        Assert.Contains("its state is 'lost'", (read with { State = "lost" }).Problem(), StringComparison.Ordinal);
        Assert.Equal("it names no blob", (read with { Blob = null }).Problem());
        Assert.Contains("is no object id", (read with { Blob = Blob.ToUpperInvariant() }).Problem(), StringComparison.Ordinal);
        Assert.Contains("is no object id", (read with { Blob = Blob[..39] }).Problem(), StringComparison.Ordinal);
        Assert.Equal("its size is below zero", (read with { Bytes = -1 }).Problem());
        Assert.Equal("it is parsed and names no extract", (read with { Extract = null }).Problem());
        Assert.Equal("it is binary and names an extract", (read with { State = CodeFileStates.Binary }).Problem());
        Assert.Equal("it is parsed and names no implementation", (read with { Language = null }).Problem());
        Assert.NotNull((read with { Extract = "../x.json" }).Problem());
    }

    /// <summary>A manifest is refused for another schema, no tool or tree, an implementation unnamed, a path that is none, a bad entry, and a digest that is not its own.</summary>
    [Fact]
    public void AManifest_IsRefusedForEachThingItGetsWrong()
    {
        var files = new Dictionary<string, CodeGraphFileEntry>(StringComparer.Ordinal) { ["src/a.toy"] = new(Blob, CodeFileStates.Parsed, 10) { Language = "toy", Extract = "aa/x.json" } };
        var extractors = new[] { new CodeGraphExtractorVersion("toy", 1) };
        var manifest = new CodeGraphManifest(CodeGraphManifest.CurrentSchema, "1.0.0", extractors, "/tree", null, false, DateTimeOffset.UnixEpoch, files, CodeGraphManifest.DigestOf(extractors, files));

        Assert.Null(manifest.Problem());
        Assert.Equal("its schema is 2, and this build reads 1", (manifest with { Schema = 2 }).Problem());
        Assert.Equal("it names no tool", (manifest with { Tool = "" }).Problem());
        Assert.Equal("it names no tree", (manifest with { Tree = " " }).Problem());
        Assert.Contains("it names an implementation as 'toy' at version 0", (manifest with { Extractors = [new("toy", 0)] }).Problem(), StringComparison.Ordinal);
        Assert.NotNull((manifest with { Files = new Dictionary<string, CodeGraphFileEntry> { ["../a.toy"] = files["src/a.toy"] } }).Problem());
        Assert.Equal("its file 'src/a.toy': its size is below zero", (manifest with { Files = new Dictionary<string, CodeGraphFileEntry> { ["src/a.toy"] = files["src/a.toy"] with { Bytes = -1 } } }).Problem());
        Assert.Equal("its digest is not the digest of what it says", (manifest with { Digest = "0" }).Problem());

        // When it was synced and the tool that synced it are not what the link result is made from.
        Assert.Equal(manifest.Digest, CodeGraphManifest.DigestOf(extractors, new Dictionary<string, CodeGraphFileEntry>(files)));
        Assert.Null((manifest with { Tool = "2.0.0", SyncedAt = DateTimeOffset.UtcNow }).Problem());
    }

    /// <summary>A stored extract is refused for another schema, no implementation, no blob, a path that is none, no line, no extract, a node with no digest, and an extract that is not sound.</summary>
    [Fact]
    public void AStoredExtract_IsRefusedForEachThingItGetsWrong()
    {
        var extract = new FileExtract([Node]).WithDigests(["class A", "go", "end"]);
        var stored = new CodeGraphStoredExtract(CodeGraphManifest.CurrentSchema, "toy", 1, Blob, "src/a.toy", 3, extract);

        Assert.Null(stored.Problem());
        Assert.Equal("its schema is 0, and this build reads 1", (stored with { Schema = 0 }).Problem());
        Assert.Equal("it names no implementation", (stored with { Version = 0 }).Problem());
        Assert.Equal("it names no blob", (stored with { Blob = "x" }).Problem());
        Assert.NotNull((stored with { Path = "/abs" }).Problem());
        Assert.Equal("its file has no line", (stored with { Lines = 0 }).Problem());
        Assert.Equal("it holds no extract", (stored with { Extract = null! }).Problem());
        Assert.Equal("its node 'toy:A' has no digest", (stored with { Extract = new FileExtract([Node]) }).Problem());
        Assert.Contains("its lines 1-3 are not lines of the file's 2", (stored with { Lines = 2 }).Problem(), StringComparison.Ordinal);
    }

    /// <summary>A link result is refused for another schema, no manifest, a node with no handle or twice, and an edge that is not one between its nodes.</summary>
    [Fact]
    public void ALinkResult_IsRefusedForEachThingItGetsWrong()
    {
        var symbols = new CodeGraphSymbolsFile(
            CodeGraphManifest.CurrentSchema,
            "digest",
            CodeGraphLinker.Revision,
            [new("file:a.toy", "abcdef", CodeNodeKinds.File, ["a.toy"]), new("toy:A", "123456", CodeNodeKinds.Type, ["a.toy"])],
            [new CodeEdge("file:a.toy", "toy:A", CodeEdgeKinds.Contains, 1, CodeConfidences.Exact)],
            new Dictionary<string, int>(),
            new Dictionary<string, int>());

        Assert.Null(symbols.Problem());
        Assert.Equal("its schema is 3, and this build reads 1", (symbols with { Schema = 3 }).Problem());
        Assert.Equal("it names no manifest", (symbols with { Graph = "" }).Problem());
        Assert.Equal("it names no linker's revision", (symbols with { Linker = 0 }).Problem());
        Assert.Equal("it holds a node with no id, handle or file", (symbols with { Nodes = [new("toy:A", "", CodeNodeKinds.Type, ["a.toy"])] }).Problem());
        Assert.Equal("it holds a node with no id, handle or file", (symbols with { Nodes = [new("toy:A", "123456", CodeNodeKinds.Type, [])] }).Problem());
        Assert.Equal("it holds the node 'toy:A' twice", (symbols with { Nodes = [.. symbols.Nodes, symbols.Nodes[1]] }).Problem());
        Assert.StartsWith("its edge 'file:a.toy' -> 'toy:Z'", (symbols with { Edges = [new CodeEdge("file:a.toy", "toy:Z", CodeEdgeKinds.Contains, 1, CodeConfidences.Exact)] }).Problem(), StringComparison.Ordinal);
        Assert.NotNull((symbols with { Edges = [new CodeEdge("file:a.toy", "toy:A", "owns", 1, CodeConfidences.Exact)] }).Problem());
        Assert.NotNull((symbols with { Edges = [new CodeEdge("file:a.toy", "toy:A", CodeEdgeKinds.Contains, 1, "probably")] }).Problem());
    }

    /// <summary>An extract's name holds its blob, a digest of its path, its implementation and version: a renamed file is read again, a raised version too.</summary>
    [Fact]
    public void AnExtractsName_HoldsItsBlobPathImplementationAndVersion()
    {
        var name = CodeGraphIds.ExtractName(Blob, "src/a.toy", "toy", 2);

        Assert.Equal($"aa/{Blob}-{CodeGraphIds.Sha256("src/a.toy")[..8]}.toy-2.json", name);
        Assert.NotEqual(name, CodeGraphIds.ExtractName(Blob, "src/b.toy", "toy", 2));
        Assert.NotEqual(name, CodeGraphIds.ExtractName(Blob, "src/a.toy", "toy", 3));
    }

    /// <summary>A store over a real directory, and a run's layout in it.</summary>
    private static (OrchestrationStore Store, OrchestratorLayout Layout) Store(TempDirectory directory)
    {
        var harness = new HarnessFactory();
        return (new OrchestrationStore(harness.FileSystem, harness.Output), OrchestratorLayout.Of(new HarnessLayout(directory.Path, directory.Path), "run"));
    }

    /// <summary>A record as the store writes it, on one line.</summary>
    private static string Json<T>(T record) => JsonSerializer.Serialize(record, JsonStateFile.LineOptions);
}
