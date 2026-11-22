using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Pages;

namespace DssHarness.Core.Codebase;

/// <summary>
/// A file a graph answer served lines of, with the digest of the bytes those lines were read from
/// (<see cref="FileContentHash.Of(ReadOnlySpan{byte})"/>): what a page that gave its lines leaves as a view of it, so the
/// edit check compares the file as it is with the file as the page read it, never with the file as it is when the page is
/// given.
/// </summary>
/// <param name="Path">The file, relative to the tree with forward separators.</param>
/// <param name="Digest">The digest of its bytes as its lines were read.</param>
public sealed record GraphFileRead(string Path, string Digest);

/// <summary>
/// The whole result an answer's pages are read from, kept for the conversation that asked its first page: the answer as
/// it was computed - its leading lines, its sections, its notes - under the digest of the graph of the view it ended on
/// and the read rules it was computed under, so a later page of the same ask is a page of that one result, and a resumed
/// conversation reads what the stream holds page 1 of.
/// </summary>
/// <param name="Schema">The record's schema it was written under.</param>
/// <param name="Key">The ask it is kept for, as <see cref="GraphPageStore.KeyOf"/> spells it.</param>
/// <param name="Digest">
/// The digest of the graph of the view the answer ended on (<see cref="CodebaseGraphQueries.Graph"/>): the one it was
/// computed on, or the one a body's check of a moved node's file left - so a later page asked of that view reads it.
/// </param>
/// <param name="Reads">The digest of the read rules it was computed under, as its store names them; null where none narrow the reads.</param>
/// <param name="Asked">How the answer names what was asked, for a page past the last to say.</param>
/// <param name="Leading">The lines the answer begins with.</param>
/// <param name="Sections">The answer's sections.</param>
/// <param name="Trailing">The notes the answer ends with.</param>
/// <param name="Nodes">The nodes the ask named, by their ids.</param>
/// <param name="Read">The files the answer served lines of, each with the digest of the bytes its lines were read from.</param>
public sealed record GraphPageKept(
    int Schema,
    string Key,
    string Digest,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Reads,
    string Asked,
    IReadOnlyList<string> Leading,
    IReadOnlyList<GraphSection> Sections,
    IReadOnlyList<string> Trailing,
    IReadOnlyList<string> Nodes,
    IReadOnlyList<GraphFileRead> Read)
{
    /// <summary>The schema this build reads.</summary>
    public const int CurrentSchema = 2;

    /// <summary>What is wrong with the kept result; <see langword="null"/> where nothing is. Every member is asked of, a missing one among them.</summary>
    public string? Problem()
        => Schema != CurrentSchema ? $"its schema is {Schema.ToString(CultureInfo.InvariantCulture)}, and this build reads {CurrentSchema.ToString(CultureInfo.InvariantCulture)}"
            : string.IsNullOrWhiteSpace(Key) ? "it names no ask"
            : string.IsNullOrWhiteSpace(Digest) ? "it names no digest"
            : Reads is { } reads && string.IsNullOrWhiteSpace(reads) ? "it names its read rules by no digest"
            : string.IsNullOrWhiteSpace(Asked) ? "it names no ask to say"
            : Leading is null || Leading.Any(string.IsNullOrEmpty) ? "its leading lines are lines"
            : Trailing is null || Trailing.Any(string.IsNullOrEmpty) ? "its trailing notes are lines"
            : Nodes is null || Nodes.Any(string.IsNullOrWhiteSpace) ? "it names its nodes by their ids"
            : Read is null || Read.Any(file => file is null || string.IsNullOrWhiteSpace(file.Path) || string.IsNullOrWhiteSpace(file.Digest)) ? "it names each file it read with a digest"
            : Sections is null || Sections.Any(section => section is null) ? "it holds a list of sections"
            : Sections.Select(section => section.Problem()).FirstOrDefault(problem => problem is not null);
}

/// <summary>
/// What tells whether a result of the graph kept still answers (<see cref="IKeptFreshness{TKept}"/>): the ask it is kept
/// for; a shape this build does not read, or a result kept under other read rules than the store's - so no page gives a
/// line of a file the reader may not read; and the digest of the graph it was computed on, which a page asked of another
/// graph is not given from.
/// </summary>
/// <param name="reads">The digest of the read rules of the store that reads; null where none narrow its reads.</param>
public sealed class GraphPageFreshness(string? reads) : IKeptFreshness<GraphPageKept>
{
    /// <inheritdoc/>
    public string KeyOf(GraphPageKept kept)
    {
        ArgumentNullException.ThrowIfNull(kept);

        return kept.Key;
    }

    /// <inheritdoc/>
    public string? Problem(GraphPageKept kept)
    {
        ArgumentNullException.ThrowIfNull(kept);

        return kept.Problem() ?? (string.Equals(kept.Reads, reads, StringComparison.Ordinal) ? null : "it was kept under other read rules");
    }

    /// <inheritdoc/>
    public string StateOf(GraphPageKept kept)
    {
        ArgumentNullException.ThrowIfNull(kept);

        return kept.Digest;
    }
}

/// <summary>
/// The results a conversation's graph answers keep (<see cref="KeptAnswers{TKept}"/>), one file an ask, in a
/// <c>graph-pages</c> directory of the conversation's records (<see cref="Orchestration.OrchestratorLayout.StepPagesDirectoryName"/>):
/// a step's own, and a side-task's. The page that computes a result of more than one page keeps it; a later page of the
/// same ask - in the same process, or a conversation resumed over the same directory - reads what was kept, so it is a
/// page of that one result. A conversation that goes on with earlier work - a continuation record carries that work's last
/// iterations word for word - reads too what the directories of that work kept, the work it went on from nearest first
/// (<see cref="Orchestration.Flow.FlowStepRun.GoesOnWith"/>), where its own holds nothing under the graph's digest, and
/// keeps what it reads there as its own. The tool alone uses it; the verb, which reads and answers in one call, keeps
/// nothing.
/// </summary>
/// <remarks>
/// A result is read back only under the digest of the graph it was computed on and the read rules it was computed under
/// (<see cref="GraphPageFreshness"/>), and one that does not read - a shape this build does not know, a file no disk
/// gives, a result kept under other read rules - is made again, and the page says why. Everything else is the shared
/// store's: every result kept for the conversation's life whatever it holds and however many there are, packed - its one
/// JSON line compressed - each directory read once for an ask, one that cannot be written held in memory alone, and the
/// directory put away whole when its conversation ends (<see cref="PutAway"/>: a step that is done and whose work no later
/// step goes on with, and every step of a run that is done). An answer of one page is never kept.
/// </remarks>
public sealed class GraphPageStore
{
    /// <summary>What a kept file is named by: the ask's key, then this - its one JSON line, compressed.</summary>
    public const string Extension = KeptAnswers.Extension;

    /// <summary>The contract a result of the graph is kept with: one line of a state file's (<see cref="JsonStateFile.LineOptions"/>).</summary>
    private static readonly JsonTypeInfo<GraphPageKept> Json = KeptAnswers.Contract<GraphPageKept>(JsonStateFile.LineOptions);

    private readonly KeptAnswers<GraphPageKept> _kept;

    /// <summary>The store over <paramref name="directory"/>, read and written through <paramref name="fileSystem"/>.</summary>
    /// <param name="directory">The <c>graph-pages</c> directory, absolute.</param>
    /// <param name="fileSystem">The file system its files are read and written through.</param>
    /// <param name="reads">
    /// The digest of the read rules the tool that keeps here answers under, null where none narrow its reads: a result is
    /// read back only under the rules it was computed under, so no page gives a line of a file the reader may not read.
    /// </param>
    /// <param name="earlier">
    /// The <c>graph-pages</c> directories of the work this conversation goes on with, the nearest first - the order that
    /// work went, newest first: read, never written, where this store's own holds nothing for an ask under the graph's
    /// digest.
    /// </param>
    public GraphPageStore(string directory, IFileSystem fileSystem, string? reads = null, IReadOnlyList<string>? earlier = null)
    {
        _kept = new KeptAnswers<GraphPageKept>(directory, fileSystem, Json, new GraphPageFreshness(reads), earlier);
        Reads = reads;
    }

    /// <summary>The digest of the read rules the results kept here are computed under; null where none narrow the reads.</summary>
    public string? Reads { get; }

    /// <summary>
    /// The result kept for <paramref name="key"/> under <paramref name="digest"/>, where one reads
    /// (<see cref="KeptAnswers{TKept}.Find"/>): the nearest one under the digest asked of answering; where none reads under
    /// it, whether one that reads was kept under another digest, and else why the nearest one kept does not read, or
    /// nothing where none was kept.
    /// </summary>
    /// <param name="key">The ask's key, as <see cref="KeyOf"/> spells it.</param>
    /// <param name="digest">The digest of the graph the view answers from now (<see cref="CodebaseGraphQueries.Graph"/>).</param>
    public KeptFind<GraphPageKept> Find(string key, string digest) => _kept.Find(key, digest);

    /// <summary>
    /// Keeps <paramref name="kept"/> for its ask, for the conversation's life: in memory, and packed in the store's
    /// directory for a resume. Where it is not written, why - what the page that computed it says, for a resumed
    /// conversation counts its later pages again: the disk refused it; null where it was written.
    /// </summary>
    /// <param name="kept">The result to keep.</param>
    /// <exception cref="ArgumentException">It is no result, or one kept under other read rules than the store's, named in the message.</exception>
    public string? Keep(GraphPageKept kept) => _kept.Keep(kept);

    /// <inheritdoc cref="KeptAnswers.PutAway"/>
    public static string? PutAway(string directory, IFileSystem fileSystem) => KeptAnswers.PutAway(directory, fileSystem);

    /// <inheritdoc cref="PageKey.Of"/>
    public static string KeyOf(string operation, JsonObject asked) => PageKey.Of(operation, asked);
}
