using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Orchestration.Flow;

namespace DssHarness.Core.Codebase;

/// <summary>
/// The base's manifest, <c>graph.json</c>: what the graph was made from - the tree, its HEAD and whether it was dirty,
/// each implementation's version - and every file it keeps, each with its blob, the implementation that read it, its state
/// and the name of its extract. Written after the extracts it names; the last whole one is what a crash leaves.
/// </summary>
/// <param name="Schema">The shape of the graph's records and of the neutral extract; a graph of another is built again whole, never read.</param>
/// <param name="Tool">The version of the tool that wrote it.</param>
/// <param name="Extractors">Each implementation of the registry, with its version, by name.</param>
/// <param name="Tree">The tree it lists, absolute.</param>
/// <param name="Head">The commit the tree's HEAD named; <see langword="null"/> where it named none yet.</param>
/// <param name="Dirty">Whether the tree held anything its HEAD does not.</param>
/// <param name="SyncedAt">When it was synced.</param>
/// <param name="Files">Every file it keeps, by its path relative to the tree with forward separators, in ordinal order.</param>
/// <param name="Digest">The SHA-256 of what it says of its implementations and its files (<see cref="DigestOf"/>), which its link result names.</param>
public sealed record CodeGraphManifest(
    int Schema,
    string Tool,
    IReadOnlyList<CodeGraphExtractorVersion> Extractors,
    string Tree,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Head,
    bool Dirty,
    [property: JsonConverter(typeof(FlowTimeConverter))] DateTimeOffset SyncedAt,
    IReadOnlyDictionary<string, CodeGraphFileEntry> Files,
    string Digest)
{
    /// <summary>The schema this build reads and writes.</summary>
    public const int CurrentSchema = 1;

    /// <summary>
    /// The implementations the workflow's <c>codebaseGraph.languages</c> named when it was synced; <see langword="null"/>
    /// where it named none, and every one was used. A file whose claimant is left out is read by the fallback, so a
    /// change of them reads such files again.
    /// </summary>
    public IReadOnlyList<string>? Languages { get; init; }

    /// <summary>
    /// The digest of a manifest of <paramref name="extractors"/> and <paramref name="files"/>: each implementation and every
    /// file's whole entry - its time, its size and its why among it, so a file that was only looked at again moves it too -
    /// and nothing else: not when it was synced, not the tool that synced it. The linker that links them is the link
    /// result's to name (<see cref="CodeGraphSymbolsFile.Linker"/>), so a raised one links the graph again and reads no file
    /// again.
    /// </summary>
    /// <param name="extractors">Each implementation and its version.</param>
    /// <param name="files">Every file kept.</param>
    public static string DigestOf(IReadOnlyList<CodeGraphExtractorVersion> extractors, IReadOnlyDictionary<string, CodeGraphFileEntry> files)
    {
        ArgumentNullException.ThrowIfNull(extractors);
        ArgumentNullException.ThrowIfNull(files);

        // Hashed as it is serialized: the list of every file grows with the repository.
        return FileContentHash.Of(stream => JsonSerializer.Serialize(stream, new { extractors, files = files.OrderBy(file => file.Key, StringComparer.Ordinal) }, JsonStateFile.LineOptions));
    }

    /// <summary>What is wrong with this manifest; <see langword="null"/> where nothing is.</summary>
    public string? Problem()
        => Schema != CurrentSchema ? $"its schema is {Schema.ToString(CultureInfo.InvariantCulture)}, and this build reads {CurrentSchema.ToString(CultureInfo.InvariantCulture)}"
            : string.IsNullOrWhiteSpace(Tool) ? "it names no tool"
            : string.IsNullOrWhiteSpace(Tree) ? "it names no tree"
            : Extractors.FirstOrDefault(extractor => extractor is null || string.IsNullOrWhiteSpace(extractor.Language) || extractor.Version < 1) is { } wrong ? $"it names an implementation as '{wrong?.Language}' at version {wrong?.Version.ToString(CultureInfo.InvariantCulture)}"
            : Extractors.GroupBy(extractor => extractor.Language, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1) is { } twice ? $"it names the implementation '{twice.Key}' twice"
            : Files.FirstOrDefault(file => OrchestrationRules.RelativePathProblem(file.Key, "a file's path") is not null || file.Value?.Problem() is not null) is { Key: not null } bad ? OrchestrationRules.RelativePathProblem(bad.Key, "a file's path") ?? $"its file '{bad.Key}': {bad.Value?.Problem() ?? "it is null"}"
            : Files.Select(file => ReadBy(file.Key, file.Value)).FirstOrDefault(found => found is not null) is { } unread ? unread
            : Digest != DigestOf(Extractors, Files) ? "its digest is not the digest of what it says"
            : null;

    /// <summary>
    /// What is wrong with what <paramref name="entry"/> says read the file at <paramref name="path"/>: an implementation this
    /// manifest names no version of, or an extract other than the one that implementation's version makes of its blob.
    /// </summary>
    private string? ReadBy(string path, CodeGraphFileEntry entry)
    {
        if (entry.Language is not { } language)
        {
            return null;
        }

        if (Extractors.FirstOrDefault(extractor => extractor.Language == language) is not { } version)
        {
            return $"its file '{path}' was read by '{language}', which it names no version of";
        }

        return entry.Extract is { } name && name != CodeGraphIds.ExtractName(entry.Blob!, path, language, version.Version)
            ? $"its file '{path}' names the extract '{name}', which is not the one '{language}' at version {version.Version.ToString(CultureInfo.InvariantCulture)} makes of its blob"
            : null;
    }
}

/// <summary>One implementation and the version of it a manifest's extracts were made by.</summary>
/// <param name="Language">Its name.</param>
/// <param name="Version">Its version.</param>
public sealed record CodeGraphExtractorVersion(string Language, int Version);

/// <summary>One file a manifest keeps.</summary>
/// <param name="Blob">The id git gives its content, as it would store it; <see langword="null"/> for a link it does not hold.</param>
/// <param name="State">What became of it: one of <see cref="CodeFileStates.All"/>.</param>
/// <param name="Bytes">Its size.</param>
public sealed record CodeGraphFileEntry([property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Blob, string State, long Bytes)
{
    /// <summary>The implementation that read it; <see langword="null"/> where none did.</summary>
    public string? Language { get; init; }

    /// <summary>
    /// Why it is in its state, where it was not read: the content tests that disagreed, the encoding that was not read. For a
    /// file read, why it is generated code, where its implementation said so - the mark a later sync keeps it generated by,
    /// unread; <see langword="null"/> for a file read that is not.
    /// </summary>
    public string? Why { get; init; }

    /// <summary>How many lines it has, where it was read.</summary>
    public int? Lines { get; init; }

    /// <summary>When it was last written, as the sync saw it, where it was looked at.</summary>
    public DateTime? Mtime { get; init; }

    /// <summary>Its extract's name under the graph's <c>extracts</c> directory (<see cref="CodeGraphIds.ExtractName"/>), where it was read.</summary>
    public string? Extract { get; init; }

    /// <summary>What is wrong with this entry; <see langword="null"/> where nothing is.</summary>
    public string? Problem()
        => !CodeFileStates.All.Contains(State, StringComparer.Ordinal) ? $"its state is '{State}', which is none of {string.Join(", ", CodeFileStates.All)}"
            : Blob is null && State != CodeFileStates.Link ? "it names no blob"
            : Blob is { } blob && !OrchestrationRules.IsObjectId(blob) ? $"its blob '{blob}' is no object id"
            : Bytes < 0 ? "its size is below zero"
            : CodeFileStates.Extracted.Contains(State, StringComparer.Ordinal) != (Extract is not null) ? $"it is {State} and {(Extract is null ? "names no extract" : "names an extract")}"
            : CodeFileStates.Extracted.Contains(State, StringComparer.Ordinal) && Language is null ? $"it is {State} and names no implementation"
            : Extract is { } name && OrchestrationRules.RelativePathProblem(name, "its extract's name") is { } path ? path
            : null;
}

/// <summary>
/// One file's extract as it is kept, once, under <c>extracts/</c>: the implementation and version that made it, and the
/// blob and the path it was made for, which are its name.
/// </summary>
/// <param name="Schema">The graph's schema it was written under.</param>
/// <param name="Language">The implementation that made it.</param>
/// <param name="Version">Its version.</param>
/// <param name="Blob">The blob of the file it was made from.</param>
/// <param name="Path">The path it was made for: an implementation's answer may read it.</param>
/// <param name="Lines">How many lines the file has.</param>
/// <param name="Extract">The extract, every node's digest set.</param>
public sealed record CodeGraphStoredExtract(int Schema, string Language, int Version, string Blob, string Path, int Lines, FileExtract Extract)
{
    /// <summary>What is wrong with this extract; <see langword="null"/> where nothing is.</summary>
    public string? Problem()
        => Schema != CodeGraphManifest.CurrentSchema ? $"its schema is {Schema.ToString(CultureInfo.InvariantCulture)}, and this build reads {CodeGraphManifest.CurrentSchema.ToString(CultureInfo.InvariantCulture)}"
            : string.IsNullOrWhiteSpace(Language) || Version < 1 ? "it names no implementation"
            : !OrchestrationRules.IsObjectId(Blob) ? "it names no blob"
            : OrchestrationRules.RelativePathProblem(Path, "its path") is { } path ? path
            : Lines < 1 ? "its file has no line"
            : Extract is null ? "it holds no extract"

            // A node whose lines are not the file's is given no digest: what is wrong with it is said before what follows from it.
            : Extract.Problem(Language, Lines) is { } wrong ? wrong
            : Extract.Nodes.FirstOrDefault(node => node?.Digest is null) is { } undigested ? $"its node '{undigested?.Id}' has no digest"
            : null;
}

/// <summary>
/// The base's link result, <c>symbols.jsonl</c>: every node with its handle and the files that declare it, every edge,
/// and what each node's references named that the graph does not hold - with the digest of the manifest it was made
/// from and the revision of the linker that made it, so one that does not match either is made again and never read.
/// Kept as lines (<see cref="Write"/>): it grows with the repository, to tens of megabytes, and a list read or written
/// whole is held whole while it is.
/// </summary>
/// <param name="Schema">The graph's schema.</param>
/// <param name="Graph">The digest of the manifest it was linked from.</param>
/// <param name="Linker">The revision of the linker that made it (<see cref="CodeGraphLinker.Revision"/>).</param>
/// <param name="Nodes">Every node.</param>
/// <param name="Edges">Every edge.</param>
/// <param name="External">For each node, how many of its references named nothing the repository declares.</param>
/// <param name="Unresolved">For each node, how many of its references named more declarations than an edge lists.</param>
public sealed record CodeGraphSymbolsFile(
    int Schema,
    string Graph,
    int Linker,
    IReadOnlyList<CodeGraphSymbolEntry> Nodes,
    IReadOnlyList<CodeEdge> Edges,
    IReadOnlyDictionary<string, int> External,
    IReadOnlyDictionary<string, int> Unresolved)
{
    /// <summary>What is wrong with this link result; <see langword="null"/> where nothing is.</summary>
    public string? Problem()
    {
        if (Schema != CodeGraphManifest.CurrentSchema)
        {
            return $"its schema is {Schema.ToString(CultureInfo.InvariantCulture)}, and this build reads {CodeGraphManifest.CurrentSchema.ToString(CultureInfo.InvariantCulture)}";
        }

        if (string.IsNullOrWhiteSpace(Graph))
        {
            return "it names no manifest";
        }

        if (Linker < 1)
        {
            return "it names no linker's revision";
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var handles = new HashSet<string>(StringComparer.Ordinal);

        foreach (var node in Nodes)
        {
            if (node is null || string.IsNullOrWhiteSpace(node.Id) || string.IsNullOrWhiteSpace(node.Handle) || node.Paths is not { Count: > 0 })
            {
                return "it holds a node with no id, handle or file";
            }

            if (!ids.Add(node.Id))
            {
                return $"it holds the node '{node.Id}' twice";
            }

            if (!handles.Add(node.Handle))
            {
                return $"its node '{node.Id}' has the handle '{node.Handle}', which another node has: a handle names one node of its view";
            }

            if (node.Kind != CodeNodeKinds.File && !CodeNodeKinds.Extracted.Contains(node.Kind, StringComparer.Ordinal))
            {
                return $"its node '{node.Id}' is of the kind '{node.Kind}', which no node of a graph is";
            }
        }

        if (Edges.FirstOrDefault(edge => edge is null || !ids.Contains(edge.From) || !ids.Contains(edge.To)
                || !CodeEdgeKinds.All.Contains(edge.Kind, StringComparer.Ordinal) || !CodeConfidences.All.Contains(edge.Confidence, StringComparer.Ordinal)) is { } wrong)
        {
            return $"its edge '{wrong?.From}' -> '{wrong?.To}' ({wrong?.Kind}, {wrong?.Confidence}) is not one between its nodes of a kind and a confidence an edge has";
        }

        foreach (var (what, counts) in new[] { ("outside the repository", External), ("ambiguously", Unresolved) })
        {
            if (counts.FirstOrDefault(count => !ids.Contains(count.Key) || count.Value < 0) is { Key: not null } bad)
            {
                return $"it counts {bad.Value.ToString(CultureInfo.InvariantCulture)} of what the references of '{bad.Key}' named {what}, and that is no count of one of its nodes";
            }
        }

        return null;
    }

    /// <summary>
    /// Writes it as lines: its head, which counts what follows, then each node, each edge, each node's count of what its
    /// references named outside the repository and each node's count of what they named ambiguously, in that order.
    /// </summary>
    /// <param name="lines">The file's lines.</param>
    public void Write(JsonLineWriter lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        lines.Write(new CodeGraphSymbolsHead(Schema, Graph, Linker, Nodes.Count, Edges.Count, External.Count, Unresolved.Count));

        foreach (var node in Nodes)
        {
            lines.Write(node);
        }

        foreach (var edge in Edges)
        {
            lines.Write(edge);
        }

        foreach (var (node, count) in External)
        {
            lines.Write(new CodeGraphCount(node, count));
        }

        foreach (var (node, count) in Unresolved)
        {
            lines.Write(new CodeGraphCount(node, count));
        }
    }

    /// <summary>Reads it from the lines <see cref="Write"/> wrote, refusing a file of fewer or more lines than its head counts.</summary>
    /// <param name="lines">The file's lines.</param>
    /// <exception cref="JsonException">A line does not read as what it is, or the file holds another number of lines.</exception>
    public static CodeGraphSymbolsFile Read(JsonLineReader lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var head = lines.Next<CodeGraphSymbolsHead>("its head");

        if (head is { Nodes: < 0 } or { Edges: < 0 } or { External: < 0 } or { Unresolved: < 0 })
        {
            throw new JsonException("its head counts fewer than no lines of a kind");
        }

        var nodes = new List<CodeGraphSymbolEntry>();
        var edges = new List<CodeEdge>();

        for (var at = 0; at < head.Nodes; at++)
        {
            nodes.Add(lines.Next<CodeGraphSymbolEntry>("a node"));
        }

        for (var at = 0; at < head.Edges; at++)
        {
            edges.Add(lines.Next<CodeEdge>("an edge"));
        }

        var external = Counts(lines, head.External, "a count of what a node's references named outside the repository");
        var unresolved = Counts(lines, head.Unresolved, "a count of what a node's references named ambiguously");
        lines.End();

        return new CodeGraphSymbolsFile(head.Schema, head.Graph, head.Linker, nodes, edges, external, unresolved);
    }

    private static Dictionary<string, int> Counts(JsonLineReader lines, int count, string what)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);

        for (var at = 0; at < count; at++)
        {
            var line = lines.Next<CodeGraphCount>(what);

            if (!counts.TryAdd(line.Node, line.Count))
            {
                throw new JsonException($"it counts the node '{line.Node}' twice");
            }
        }

        return counts;
    }
}

/// <summary>The first line of a link result's file: what it was made from, and how many lines of each kind follow.</summary>
/// <param name="Schema">The graph's schema.</param>
/// <param name="Graph">The digest of the manifest it was linked from.</param>
/// <param name="Linker">The revision of the linker that made it.</param>
/// <param name="Nodes">How many nodes follow.</param>
/// <param name="Edges">How many edges follow them.</param>
/// <param name="External">How many counts of what references named outside the repository follow them.</param>
/// <param name="Unresolved">How many counts of what references named ambiguously follow them.</param>
public sealed record CodeGraphSymbolsHead(int Schema, string Graph, int Linker, int Nodes, int Edges, int External, int Unresolved);

/// <summary>One node's count, on a line of a link result's file.</summary>
/// <param name="Node">The node's id.</param>
/// <param name="Count">The count.</param>
public sealed record CodeGraphCount(string Node, int Count);

/// <summary>One node of a link result.</summary>
/// <param name="Id">Its id.</param>
/// <param name="Handle">Its handle in the base (<see cref="CodeGraphIds.Handles"/>).</param>
/// <param name="Kind">Its kind.</param>
/// <param name="Paths">The files that declare it.</param>
public sealed record CodeGraphSymbolEntry(string Id, string Handle, string Kind, IReadOnlyList<string> Paths);

/// <summary>
/// One agent's view, <c>views/&lt;agent&gt;.json</c>: what its worktree holds that the base does not - each path whose blob
/// there is not the base's, with its entry, and each of the base's paths the worktree does not hold - against the base
/// whose manifest's digest it names. Written after the extracts it names; held against a base that moved without a file
/// being read again.
/// </summary>
/// <param name="Schema">The graph's schema.</param>
/// <param name="Agent">The agent whose worktree it is.</param>
/// <param name="Tree">The worktree it lists, absolute.</param>
/// <param name="Base">The digest of the base's manifest it holds its overlay against.</param>
/// <param name="Head">The commit the worktree's HEAD named; <see langword="null"/> where it named none yet.</param>
/// <param name="Dirty">Whether the worktree held anything its HEAD does not.</param>
/// <param name="SyncedAt">When it was synced.</param>
/// <param name="Files">Each path the worktree holds otherwise than the base, with its entry, in ordinal order.</param>
/// <param name="Deleted">Each of the base's paths the worktree does not hold, in ordinal order.</param>
public sealed record CodeGraphView(
    int Schema,
    string Agent,
    string Tree,
    string Base,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Head,
    bool Dirty,
    [property: JsonConverter(typeof(FlowTimeConverter))] DateTimeOffset SyncedAt,
    IReadOnlyDictionary<string, CodeGraphFileEntry> Files,
    IReadOnlyList<string> Deleted)
{
    /// <summary>What is wrong with this view; <see langword="null"/> where nothing is.</summary>
    public string? Problem()
        => Schema != CodeGraphManifest.CurrentSchema ? $"its schema is {Schema.ToString(CultureInfo.InvariantCulture)}, and this build reads {CodeGraphManifest.CurrentSchema.ToString(CultureInfo.InvariantCulture)}"
            : OrchestrationRules.NameProblem(Agent) is { } name ? $"its agent's name: {name}"
            : string.IsNullOrWhiteSpace(Tree) ? "it names no tree"
            : string.IsNullOrWhiteSpace(Base) ? "it names no base"
            : Files.FirstOrDefault(file => OrchestrationRules.RelativePathProblem(file.Key, "a file's path") is not null || file.Value?.Problem() is not null) is { Key: not null } bad
                ? OrchestrationRules.RelativePathProblem(bad.Key, "a file's path") ?? $"its file '{bad.Key}': {bad.Value?.Problem() ?? "it is null"}"
            : Deleted.FirstOrDefault(path => OrchestrationRules.RelativePathProblem(path, "a deleted path") is not null) is { } wrong ? OrchestrationRules.RelativePathProblem(wrong, "a deleted path")
            : Deleted.Distinct(StringComparer.Ordinal).Count() != Deleted.Count ? "it names a deleted path twice"
            : Deleted.FirstOrDefault(Files.ContainsKey) is { } both ? $"it holds '{both}' and says it is deleted"
            : null;
}

/// <summary>One line of <c>sync.jsonl</c>: one sync of one view.</summary>
/// <param name="At">When it ended.</param>
/// <param name="View">Which view: <c>base</c>, or an agent's name.</param>
/// <param name="Why">Why it synced: the step that asked, by its instance.</param>
/// <param name="Listed">How many files the tree listed that the graph keeps.</param>
/// <param name="Hashed">How many files git was asked the blob of to know what changed.</param>
/// <param name="Extracted">How many files an implementation read.</param>
/// <param name="Reused">How many files kept an extract already made.</param>
/// <param name="Removed">How many paths left the graph.</param>
/// <param name="Seconds">How long it took.</param>
public sealed record CodeGraphSyncLine(
    [property: JsonConverter(typeof(FlowTimeConverter))] DateTimeOffset At,
    string View,
    string Why,
    int Listed,
    int Hashed,
    int Extracted,
    int Reused,
    int Removed,
    double Seconds)
{
    /// <summary>Why the graph was built again whole, where it was: no graph read, another schema, <c>rebuild</c>.</summary>
    public string? Rebuilt { get; init; }

    /// <summary>The run whose graph it was seeded from, where it was.</summary>
    public string? SeededFrom { get; init; }

    /// <summary>How many references linking found, where it linked: every one where it linked whole, those a change could have changed otherwise.</summary>
    public int? Resolved { get; init; }

    /// <summary>
    /// Why a sync the engine made of its own - after a fold, at a step's start - failed, or why a view was not held against
    /// the base a sync moved, where either was: the graph is a map, and the step goes on.
    /// </summary>
    public string? Failed { get; init; }

    /// <summary>
    /// How many files the view the sync left could not read, each listed unreadable with why: what makes it incomplete, said
    /// for every sync - the engine's own, which no step answers for, among them.
    /// </summary>
    public int Unreadable { get; init; }

    /// <summary>What is wrong with this line; <see langword="null"/> where nothing is.</summary>
    public string? Problem()
        => string.IsNullOrWhiteSpace(View) ? "it names no view"
            : string.IsNullOrWhiteSpace(Why) ? "it says nothing of why it synced"
            : Listed < 0 || Hashed < 0 || Extracted < 0 || Reused < 0 || Removed < 0 || Resolved < 0 || Unreadable < 0 ? "it counts files below zero"
            : Failed is { } failed && string.IsNullOrWhiteSpace(failed) ? "it says it failed and not why"
            : !double.IsFinite(Seconds) || Seconds < 0 ? "its time is no time a clock gives"
            : Rebuilt is { } rebuilt && string.IsNullOrWhiteSpace(rebuilt) ? "it says the graph was built again and not why"
            : SeededFrom is { } seeded && string.IsNullOrWhiteSpace(seeded) ? "it says it was seeded and not from which run"
            : null;
}
