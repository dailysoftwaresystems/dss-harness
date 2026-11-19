using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using DssHarness.Core.FileSystem;

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
/// What a store found kept for an ask under the digest of the graph asked of: the result, or why none reads - null for
/// both where none was kept - and whether one that reads was kept under another digest.
/// </summary>
/// <param name="Kept">The result kept under the digest asked of; null where none reads under it.</param>
/// <param name="Why">Why what was kept does not read, where something was kept and none reads at all; null otherwise.</param>
public sealed record GraphPageFind(GraphPageKept? Kept, string? Why)
{
    /// <summary>Nothing kept.</summary>
    public static GraphPageFind None { get; } = new(null, null);

    /// <summary>
    /// Whether no result reads under the digest asked of while one of the same ask reads under another: the graph the view
    /// answers from moved since page 1 was counted.
    /// </summary>
    public bool Moved { get; init; }
}

/// <summary>
/// The results a conversation's graph answers keep, one file an ask, in a <c>graph-pages</c> directory of the
/// conversation's records (<see cref="Orchestration.OrchestratorLayout.StepPagesDirectoryName"/>): a step's own, and a
/// side-task's. The page that computes a result of more than one page keeps it; a later page of the same ask - in the
/// same process, or a conversation resumed over the same directory - reads what was kept, so it is a page of that one
/// result. A conversation that goes on with earlier work - a continuation record carries that work's last iterations
/// word for word - reads too what the directories of that work kept, the work it went on from nearest first
/// (<see cref="Orchestration.Flow.FlowStepRun.GoesOnWith"/>), where its own holds nothing under the graph's digest, and
/// keeps what it reads there as its own. The tool alone uses it; the verb, which reads and answers in one call, keeps
/// nothing.
/// </summary>
/// <remarks>
/// A kept result is a cache of one computation, not a record of what is true: one that does not read - a shape this
/// build does not know, a file no disk gives, a result kept under other read rules - is made again, and the page says
/// why. Every result is kept for the conversation's life, whatever it holds and however many there are: in memory as it
/// was computed or read, and in its file packed - its one JSON line compressed - for a resume; one that cannot be written
/// is held in memory alone, and the page that computed it says a resumed conversation counts its later pages again. An
/// answer of one page is never kept. What a directory keeps for an ask is read and unpacked once: a later page is given
/// the same result from memory, never read from the disk again.
/// The store is put away whole when its conversation ends (<see cref="PutAway"/>): a step that is done and whose work no
/// later step goes on with, and every step of a run that is done.
/// </remarks>
public sealed class GraphPageStore
{
    /// <summary>What a kept file is named by: the ask's key, then this - its one JSON line, compressed.</summary>
    public const string Extension = ".json.gz";

    private readonly string _directory;
    private readonly IFileSystem _fileSystem;
    private readonly string? _reads;
    private readonly IReadOnlyList<string> _earlier;
    private readonly object _lock = new();

    /// <summary>
    /// What this store holds for each ask, by its key: the result it kept, or took from the earlier work as its own, or
    /// what its own directory was found to keep once it was read - a result, or why none reads.
    /// </summary>
    private readonly Dictionary<string, Visit> _own = new(StringComparer.Ordinal);

    /// <summary>What each of the earlier work's directories was found to keep for an ask, by the directory's place and the ask's key: each read once.</summary>
    private readonly Dictionary<(int At, string Key), Visit> _visited = [];

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
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(fileSystem);

        _directory = directory;
        _fileSystem = fileSystem;
        _reads = reads;
        _earlier = earlier ?? [];
    }

    /// <summary>The digest of the read rules the results kept here are computed under; null where none narrow the reads.</summary>
    public string? Reads => _reads;

    /// <summary>
    /// The result kept for <paramref name="key"/> under <paramref name="digest"/>, where one reads: this store's own - held,
    /// or read once from its directory - else the earlier work's, the nearest first, one found there kept as this store's
    /// own; the nearest one under the digest asked of answering, whatever a nearer one kept under another holds. Where none
    /// reads under it: whether one that reads was kept under another digest, and else why the nearest one kept does not
    /// read, or nothing where none was kept.
    /// </summary>
    /// <param name="key">The ask's key, as <see cref="KeyOf"/> spells it.</param>
    /// <param name="digest">The digest of the graph the view answers from now (<see cref="CodebaseGraphQueries.Graph"/>).</param>
    public GraphPageFind Find(string key, string digest)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(digest);

        lock (_lock)
        {
            var moved = false;
            string? why = null;

            for (var at = -1; at < _earlier.Count; at++)
            {
                var visit = at < 0 ? Own(key) : Earlier(at, key);

                if (visit.Kept is not { } kept)
                {
                    why ??= visit.Why;
                    continue;
                }

                if (!string.Equals(kept.Digest, digest, StringComparison.Ordinal))
                {
                    moved = true;
                    continue;
                }

                // What the earlier work kept is this store's own from now on, for a conversation that goes on from this one
                // in turn; where it cannot be written, it is held here, as every result this store holds is.
                if (at >= 0)
                {
                    _own[key] = visit;
                    _ = Write(key, Packed(kept));
                }

                return new GraphPageFind(kept, null);
            }

            return moved ? new GraphPageFind(null, null) { Moved = true } : new GraphPageFind(null, why);
        }
    }

    /// <summary>
    /// Keeps <paramref name="kept"/> for its ask, for the conversation's life: in memory, and packed in the store's
    /// directory for a resume. Where it is not written, why - what the page that computed it says, for a resumed
    /// conversation counts its later pages again: the disk refused it; null where it was written.
    /// </summary>
    /// <param name="kept">The result to keep.</param>
    public string? Keep(GraphPageKept kept)
    {
        ArgumentNullException.ThrowIfNull(kept);

        if (kept.Problem() is { } problem)
        {
            throw new ArgumentException(problem, nameof(kept));
        }

        if (!string.Equals(kept.Reads, _reads, StringComparison.Ordinal))
        {
            throw new ArgumentException("a result is kept under the read rules of the store that keeps it", nameof(kept));
        }

        var bytes = Packed(kept);

        lock (_lock)
        {
            _own[kept.Key] = new Visit(kept, null);
            return Write(kept.Key, bytes);
        }
    }

    /// <summary>
    /// Puts away the <c>graph-pages</c> directory <paramref name="directory"/> whole: what a conversation leaves as it ends,
    /// so no page of it is asked again. Why it could not be, or null where it was - or was never made.
    /// </summary>
    /// <param name="directory">The directory, absolute.</param>
    /// <param name="fileSystem">The file system it is removed through.</param>
    public static string? PutAway(string directory, IFileSystem fileSystem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(fileSystem);

        try
        {
            fileSystem.DeleteDirectory(directory);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message.TrimEnd('.');
        }
    }

    /// <summary>The ask a call is kept for, hashed to one name: the operation and the ask as the tool resolved it, its members in their names' order.</summary>
    /// <param name="operation">The operation's name, as <c>op</c> says it.</param>
    /// <param name="asked">
    /// The ask as the tool resolved it - each node by its id, each file by the path the view holds it at, each default
    /// filled in - so two spellings of one ask are one key; never the page.
    /// </param>
    public static string KeyOf(string operation, JsonObject asked)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentNullException.ThrowIfNull(asked);

        if (asked.ContainsKey("page"))
        {
            throw new ArgumentException("an ask is kept whatever page of it is asked", nameof(asked));
        }

        var members = asked.Where(member => member.Value is not null).OrderBy(member => member.Key, StringComparer.Ordinal);
        var said = operation + "\n" + string.Join("\n", members.Select(member => $"{member.Key}={member.Value!.ToJsonString()}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(said))).ToLowerInvariant();
    }

    /// <summary>The bytes <paramref name="kept"/> is held and written as: its one JSON line, compressed.</summary>
    private static byte[] Packed(GraphPageKept kept)
    {
        using var packed = new MemoryStream();

        using (var compressing = new GZipStream(packed, CompressionLevel.Optimal, leaveOpen: true))
        {
            JsonSerializer.Serialize(compressing, kept, JsonStateFile.LineOptions);
        }

        return packed.ToArray();
    }

    /// <summary>The result <paramref name="bytes"/> pack (<see cref="Packed(GraphPageKept)"/>); null where they hold null.</summary>
    /// <exception cref="JsonException">They do not read as a result.</exception>
    /// <exception cref="InvalidDataException">They are not compressed as a result is.</exception>
    private static GraphPageKept? Unpacked(byte[] bytes)
    {
        using var decompressing = new GZipStream(new MemoryStream(bytes, writable: false), CompressionMode.Decompress);
        return JsonSerializer.Deserialize<GraphPageKept>(decompressing, JsonStateFile.LineOptions);
    }

    /// <summary>What this store holds for <paramref name="key"/>: what it kept or took, else what its own directory keeps, read once.</summary>
    private Visit Own(string key)
    {
        if (!_own.TryGetValue(key, out var own))
        {
            _own[key] = own = ReadIn(_directory, key);
        }

        return own;
    }

    /// <summary>What the earlier work's directory at <paramref name="at"/> keeps for <paramref name="key"/>, read once.</summary>
    private Visit Earlier(int at, string key)
    {
        if (!_visited.TryGetValue((at, key), out var visit))
        {
            _visited[(at, key)] = visit = ReadIn(_earlier[at], key);
        }

        return visit;
    }

    /// <summary>
    /// The result <paramref name="directory"/> keeps for <paramref name="key"/>, unpacked, where it reads under this store's
    /// read rules; else why not; nothing where it keeps none.
    /// </summary>
    private Visit ReadIn(string directory, string key)
    {
        var file = Path.Combine(directory, key + Extension);

        try
        {
            if (_fileSystem.StandingOf(file) is null)
            {
                return Visit.None;
            }

            byte[] bytes;

            using (var stream = _fileSystem.OpenRead(file))
            using (var held = new MemoryStream())
            {
                stream.CopyTo(held);
                bytes = held.ToArray();
            }

            var read = Unpacked(bytes);

            return read is null ? new(null, "its file holds nothing")
                : read.Problem() is { } problem ? new(null, problem)
                : read.Key != key ? new(null, "its file is of another ask")
                : !string.Equals(read.Reads, _reads, StringComparison.Ordinal) ? new(null, "it was kept under other read rules")
                : new(read, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException or InvalidDataException)
        {
            // A file not compressed as a result is - another build's, or written over - is one that does not read.
            return new(null, $"its file does not read: {ex.Message.TrimEnd('.')}");
        }
    }

    /// <summary>Writes the result packed as <paramref name="bytes"/> in the store's directory for <paramref name="key"/>; why it was not written, or null.</summary>
    private string? Write(string key, byte[] bytes)
    {
        try
        {
            _fileSystem.CreateDirectory(_directory);
            _fileSystem.WriteAtomic(Path.Combine(_directory, key + Extension), stream => stream.Write(bytes));
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message.TrimEnd('.');
        }
    }

    /// <summary>What a directory was found to keep for an ask: a result that reads, or why none does; neither where none is kept.</summary>
    private sealed record Visit(GraphPageKept? Kept, string? Why)
    {
        public static Visit None { get; } = new(null, null);
    }
}
