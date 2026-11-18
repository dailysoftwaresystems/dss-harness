using System.Globalization;
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
/// it was computed - its leading lines, its sections, its notes - under the digest of the graph it was computed on and
/// the read rules it was computed under, so a later page of the same ask is a page of that one result, and a resumed
/// conversation reads what the stream holds page 1 of.
/// </summary>
/// <param name="Schema">The record's schema it was written under.</param>
/// <param name="Key">The ask it is kept for, as <see cref="GraphPageStore.KeyOf"/> spells it.</param>
/// <param name="Digest">The digest of the graph it was computed on (<see cref="CodebaseGraphQueries.Graph"/>).</param>
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

/// <summary>What a store found kept for an ask: the result, or why none reads - null for both where none was kept.</summary>
/// <param name="Kept">The result kept; null where none reads.</param>
/// <param name="Why">Why what was kept does not read, where something was kept; null where nothing was, or it read.</param>
public sealed record GraphPageFind(GraphPageKept? Kept, string? Why)
{
    /// <summary>Nothing kept.</summary>
    public static GraphPageFind None { get; } = new(null, null);
}

/// <summary>
/// The results a conversation's graph answers keep, one file an ask, in a <c>graph-pages</c> directory of the
/// conversation's records (<see cref="Orchestration.OrchestratorLayout.StepPagesDirectoryName"/>): a step's own, and a
/// side-task's. The page that computes a result of more than one page keeps it; a later page of the same ask - in the
/// same process, or a conversation resumed over the same directory - reads what was kept, so it is a page of that one
/// result. A conversation that goes on with earlier work - a continuation record carries that work's last iterations
/// word for word - reads too what the directories of that work kept, where its own holds nothing, and keeps what it
/// reads there as its own. The tool alone uses it; the verb, which reads and answers in one call, keeps nothing.
/// </summary>
/// <remarks>
/// A kept result is a cache of one computation, not a record of what is true: one that does not read - a shape this
/// build does not know, a file no disk gives, a result kept under other read rules - is made again, and the page says
/// why; one that cannot be written leaves the answer it was made for standing, held in memory alone, and that page says
/// a later page may be counted again. It is bounded: an answer of one page is never kept, the most recent <see cref="MostHeld"/> results are
/// held in memory and the most recently kept <see cref="MostKept"/> on disk, each written on one line, the oldest put
/// away as a later one is kept.
/// </remarks>
public sealed class GraphPageStore
{
    /// <summary>The most results held in memory, the most recently asked; the rest are read from disk.</summary>
    public const int MostHeld = 8;

    /// <summary>The most results kept on disk in the store's directory, the most recently kept; the oldest is removed as one more is kept.</summary>
    public const int MostKept = 32;

    /// <summary>What a kept file is named by: the ask's key, then this.</summary>
    private const string Extension = ".json";

    private readonly string _directory;
    private readonly IFileSystem _fileSystem;
    private readonly string? _reads;
    private readonly IReadOnlyList<string> _earlier;
    private readonly object _lock = new();

    /// <summary>The results held in memory, the most recently asked first.</summary>
    private readonly List<GraphPageKept> _held = [];

    /// <summary>The asks this store wrote, by their keys, the oldest first: the order its own files are put away in.</summary>
    private readonly List<string> _order = [];

    /// <summary>The store over <paramref name="directory"/>, read and written through <paramref name="fileSystem"/>.</summary>
    /// <param name="directory">The <c>graph-pages</c> directory, absolute.</param>
    /// <param name="fileSystem">The file system its files are read and written through.</param>
    /// <param name="reads">
    /// The digest of the read rules the tool that keeps here answers under, null where none narrow its reads: a result is
    /// read back only under the rules it was computed under, so no page gives a line of a file the reader may not read.
    /// </param>
    /// <param name="earlier">
    /// The <c>graph-pages</c> directories of the work this conversation goes on with, the nearest first: read, never
    /// written, where this store's own holds nothing for an ask.
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
    /// The result kept for <paramref name="key"/>, where one reads: held in memory, else in the store's directory, else in
    /// the earlier work's directories, the nearest first - one found there kept as this store's own. Where none reads,
    /// why the nearest one kept does not, or nothing where none was kept.
    /// </summary>
    /// <param name="key">The ask's key, as <see cref="KeyOf"/> spells it.</param>
    public GraphPageFind Find(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        lock (_lock)
        {
            if (_held.FindIndex(kept => kept.Key == key) is var index and >= 0)
            {
                var found = _held[index];
                _held.RemoveAt(index);
                _held.Insert(0, found);
                return new GraphPageFind(found, null);
            }

            string? why = null;

            foreach (var (directory, own) in new[] { (_directory, true) }.Concat(_earlier.Select(earlier => (earlier, false))))
            {
                var read = ReadIn(directory, key);

                if (read.Kept is { } kept)
                {
                    Hold(kept);

                    if (!own)
                    {
                        Write(kept);
                    }

                    return read;
                }

                why ??= read.Why;
            }

            return new GraphPageFind(null, why);
        }
    }

    /// <summary>
    /// Keeps <paramref name="kept"/> for its ask, in memory and in the store's directory for a resume; the oldest kept
    /// file is removed where the directory would hold more than <see cref="MostKept"/>. Where it cannot be written, why -
    /// what the page that computed it says, for a later page asked once it is no longer held may be counted again; null
    /// where it was written.
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

        lock (_lock)
        {
            Hold(kept);
            return Write(kept);
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

    /// <summary>The result <paramref name="directory"/> keeps for <paramref name="key"/>, where it reads under this store's read rules; else why not.</summary>
    private GraphPageFind ReadIn(string directory, string key)
    {
        var file = Path.Combine(directory, key + Extension);

        try
        {
            if (!_fileSystem.FileExists(file))
            {
                return GraphPageFind.None;
            }

            var read = JsonStateFile.Read<GraphPageKept>(_fileSystem, file, JsonStateFile.LineOptions);

            return read is null ? new(null, "its file holds nothing")
                : read.Problem() is { } problem ? new(null, problem)
                : read.Key != key ? new(null, "its file is of another ask")
                : !string.Equals(read.Reads, _reads, StringComparison.Ordinal) ? new(null, "it was kept under other read rules")
                : new(read, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return new(null, $"its file does not read: {ex.Message.TrimEnd('.')}");
        }
    }

    /// <summary>Holds <paramref name="kept"/> in memory as the most recently asked, the least recently asked let go past <see cref="MostHeld"/>.</summary>
    private void Hold(GraphPageKept kept)
    {
        _held.RemoveAll(held => held.Key == kept.Key);
        _held.Insert(0, kept);

        if (_held.Count > MostHeld)
        {
            _held.RemoveRange(MostHeld, _held.Count - MostHeld);
        }
    }

    /// <summary>Writes <paramref name="kept"/> in the store's directory, on one line, and removes the oldest kept past <see cref="MostKept"/>; why it could not, or null.</summary>
    private string? Write(GraphPageKept kept)
    {
        try
        {
            _fileSystem.CreateDirectory(_directory);
            JsonStateFile.Write(_fileSystem, Path.Combine(_directory, kept.Key + Extension), kept, JsonStateFile.LineOptions);
            _order.Remove(kept.Key);
            _order.Add(kept.Key);

            // The oldest first: what an earlier process kept, by when it was written, then what this one kept, in the order
            // it kept it - its own order, for a clock may give two writes of one moment the same time.
            var files = _fileSystem.EnumerateWrittenFiles(_directory)
                .Where(file => file.Path.EndsWith(Extension, StringComparison.Ordinal))
                .Select(file => (file.Path, file.LastWriteTimeUtc, Order: _order.IndexOf(Path.GetFileNameWithoutExtension(file.Path))))
                .OrderBy(file => file.Order)
                .ThenBy(file => file.LastWriteTimeUtc)
                .ThenBy(file => file.Path, StringComparer.Ordinal)
                .ToList();

            foreach (var old in files.Take(Math.Max(0, files.Count - MostKept)))
            {
                _fileSystem.DeleteFile(old.Path);
                _order.Remove(Path.GetFileNameWithoutExtension(old.Path));
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message.TrimEnd('.');
        }
    }
}
