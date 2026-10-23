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
    /// The digest of a manifest of <paramref name="extractors"/> and <paramref name="files"/>: what of the manifest changes
    /// the link result, and nothing else - not when it was synced, not the tool that synced it. The linker that links them is
    /// the link result's to name (<see cref="CodeGraphSymbolsFile.Linker"/>), so a raised one links the graph again and
    /// reads no file again.
    /// </summary>
    /// <param name="extractors">Each implementation and its version.</param>
    /// <param name="files">Every file kept.</param>
    public static string DigestOf(IReadOnlyList<CodeGraphExtractorVersion> extractors, IReadOnlyDictionary<string, CodeGraphFileEntry> files)
    {
        ArgumentNullException.ThrowIfNull(extractors);
        ArgumentNullException.ThrowIfNull(files);

        return CodeGraphIds.Sha256(JsonSerializer.Serialize(new { extractors, files = files.OrderBy(file => file.Key, StringComparer.Ordinal).ToList() }, JsonStateFile.LineOptions));
    }

    /// <summary>What is wrong with this manifest; <see langword="null"/> where nothing is.</summary>
    public string? Problem()
        => Schema != CurrentSchema ? $"its schema is {Schema.ToString(CultureInfo.InvariantCulture)}, and this build reads {CurrentSchema.ToString(CultureInfo.InvariantCulture)}"
            : string.IsNullOrWhiteSpace(Tool) ? "it names no tool"
            : string.IsNullOrWhiteSpace(Tree) ? "it names no tree"
            : Extractors.FirstOrDefault(extractor => extractor is null || string.IsNullOrWhiteSpace(extractor.Language) || extractor.Version < 1) is { } wrong ? $"it names an implementation as '{wrong?.Language}' at version {wrong?.Version.ToString(CultureInfo.InvariantCulture)}"
            : Files.FirstOrDefault(file => OrchestrationRules.RelativePathProblem(file.Key, "a file's path") is not null || file.Value?.Problem() is not null) is { Key: not null } bad ? OrchestrationRules.RelativePathProblem(bad.Key, "a file's path") ?? $"its file '{bad.Key}': {bad.Value?.Problem() ?? "it is null"}"
            : Digest != DigestOf(Extractors, Files) ? "its digest is not the digest of what it says"
            : null;
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

    /// <summary>Why it is in its state, where it was not read: the content tests that disagreed, the encoding that was not read.</summary>
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
/// The base's link result, <c>symbols.json</c>: every node with its handle and the files that declare it, every edge,
/// and what each node's references named that the graph does not hold - with the digest of the manifest it was made
/// from and the revision of the linker that made it, so one that does not match either is made again and never read.
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

        foreach (var node in Nodes)
        {
            if (node is null || string.IsNullOrWhiteSpace(node.Id) || string.IsNullOrWhiteSpace(node.Handle) || node.Paths.Count == 0)
            {
                return "it holds a node with no id, handle or file";
            }

            if (!ids.Add(node.Id))
            {
                return $"it holds the node '{node.Id}' twice";
            }
        }

        return Edges.FirstOrDefault(edge => edge is null || !ids.Contains(edge.From) || !ids.Contains(edge.To)
                || !CodeEdgeKinds.All.Contains(edge.Kind, StringComparer.Ordinal) || !CodeConfidences.All.Contains(edge.Confidence, StringComparer.Ordinal)) is { } wrong
            ? $"its edge '{wrong?.From}' -> '{wrong?.To}' ({wrong?.Kind}, {wrong?.Confidence}) is not one between its nodes of a kind and a confidence an edge has"
            : null;
    }
}

/// <summary>One node of a link result.</summary>
/// <param name="Id">Its id.</param>
/// <param name="Handle">Its handle in the base (<see cref="CodeGraphIds.Handles"/>).</param>
/// <param name="Kind">Its kind.</param>
/// <param name="Paths">The files that declare it.</param>
public sealed record CodeGraphSymbolEntry(string Id, string Handle, string Kind, IReadOnlyList<string> Paths);

/// <summary>One line of <c>sync.jsonl</c>: one sync of one view.</summary>
/// <param name="At">When it ended.</param>
/// <param name="View">Which view: <c>base</c>, or an agent's name.</param>
/// <param name="Why">Why it synced: the step that asked, by its instance.</param>
/// <param name="Listed">How many files the tree listed that the graph keeps.</param>
/// <param name="Hashed">How many files git was asked the blob of.</param>
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

    /// <summary>What is wrong with this line; <see langword="null"/> where nothing is.</summary>
    public string? Problem()
        => string.IsNullOrWhiteSpace(View) ? "it names no view"
            : string.IsNullOrWhiteSpace(Why) ? "it says nothing of why it synced"
            : Listed < 0 || Hashed < 0 || Extracted < 0 || Reused < 0 || Removed < 0 ? "it counts files below zero"
            : !double.IsFinite(Seconds) || Seconds < 0 ? "its time is no time a clock gives"
            : Rebuilt is { } rebuilt && string.IsNullOrWhiteSpace(rebuilt) ? "it says the graph was built again and not why"
            : SeededFrom is { } seeded && string.IsNullOrWhiteSpace(seeded) ? "it says it was seeded and not from which run"
            : null;
}
