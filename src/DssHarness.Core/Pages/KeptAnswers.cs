using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using DssHarness.Core.FileSystem;

namespace DssHarness.Core.Pages;

/// <summary>
/// How a store of kept answers tells whether what it kept still answers: the ask it is kept for, what is wrong with it for
/// this build and this store, and the state of what it was computed on - which an answer asked under another state is not
/// given from.
/// </summary>
/// <typeparam name="TKept">The kept form.</typeparam>
public interface IKeptFreshness<in TKept>
{
    /// <summary>The ask <paramref name="kept"/> is kept for: its key, as <see cref="PageKey"/> spells it; a file of another ask never answers.</summary>
    /// <param name="kept">The kept form.</param>
    string KeyOf(TKept kept);

    /// <summary>
    /// What is wrong with <paramref name="kept"/>: a shape this build does not read, or rules the store does not read
    /// under; <see langword="null"/> where nothing is.
    /// </summary>
    /// <param name="kept">The kept form.</param>
    string? Problem(TKept kept);

    /// <summary>The state <paramref name="kept"/> was computed on, compared with the state an answer is asked under.</summary>
    /// <param name="kept">The kept form.</param>
    string StateOf(TKept kept);
}

/// <summary>
/// What a store found kept for an ask under the state asked of: the result, or why none reads - null for both where none
/// was kept - and whether one that reads was kept under another state.
/// </summary>
/// <typeparam name="TKept">The kept form.</typeparam>
/// <param name="Kept">The result kept under the state asked of; null where none reads under it.</param>
/// <param name="Why">Why what was kept does not read, where something was kept and none reads at all; null otherwise.</param>
public sealed record KeptFind<TKept>(TKept? Kept, string? Why)
    where TKept : class
{
    /// <summary>Nothing kept.</summary>
    public static KeptFind<TKept> None { get; } = new(null, null);

    /// <summary>
    /// Whether no result reads under the state asked of while one of the same ask reads under another: what the answer is
    /// computed on moved since page 1 was counted.
    /// </summary>
    public bool Moved { get; init; }
}

/// <summary>What every store of kept answers shares: the name of a kept file, and how a conversation's are put away.</summary>
public static class KeptAnswers
{
    /// <summary>What a kept file is named by: the ask's key, then this - its one JSON line, compressed.</summary>
    public const string Extension = ".json.gz";

    /// <summary>
    /// Puts away the directory of kept answers <paramref name="directory"/> whole: what a conversation leaves as it ends,
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

    /// <summary>
    /// The contract <typeparamref name="TKept"/> is written and read with under <paramref name="options"/>: the options made
    /// read-only first, their default resolver filled in where none is set, as the serializer itself does on their first
    /// use - without which the options give no contract at all before something has been serialized with them.
    /// </summary>
    /// <typeparam name="TKept">The kept form.</typeparam>
    /// <param name="options">The options its files are written with.</param>
    public static JsonTypeInfo<TKept> Contract<TKept>(JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.MakeReadOnly(populateMissingResolver: true);
        return (JsonTypeInfo<TKept>)options.GetTypeInfo(typeof(TKept));
    }
}

/// <summary>
/// The answers a conversation keeps, one file an ask, in a directory of the conversation's records: the page that computes
/// a result of more than one page keeps it, and a later page of the same ask - in the same process, or a conversation
/// resumed over the same directory - reads what was kept, so it is a page of that one result. A conversation that goes on
/// with earlier work reads too what the directories of that work kept, the work it went on from nearest first, where its
/// own holds nothing under the state asked of, and keeps what it reads there as its own.
/// </summary>
/// <remarks>
/// A kept result is a cache of one computation, not a record of what is true: one that does not read - a shape this build
/// does not know, a file no disk gives, a result kept under other rules (<see cref="IKeptFreshness{TKept}.Problem"/>) - is
/// made again, and the page says why. Every result is kept for the conversation's life, whatever it holds and however many
/// there are: in memory as it was computed or read, and in its file packed - its one JSON line compressed - for a resume;
/// one that cannot be written is held in memory alone, and its keeper is told why. What a directory keeps for an ask is
/// read and unpacked once: a later find of it is answered from memory, never read from the disk again. The directory is
/// put away whole when its conversation ends (<see cref="KeptAnswers.PutAway"/>).
/// </remarks>
/// <typeparam name="TKept">The kept form.</typeparam>
public sealed class KeptAnswers<TKept>
    where TKept : class
{
    private readonly string _directory;
    private readonly IFileSystem _fileSystem;
    private readonly JsonTypeInfo<TKept> _json;
    private readonly IKeptFreshness<TKept> _freshness;
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
    /// <param name="directory">The directory its results are kept in, absolute.</param>
    /// <param name="fileSystem">The file system its files are read and written through.</param>
    /// <param name="json">The contract a result is written and read with (<see cref="KeptAnswers.Contract"/>).</param>
    /// <param name="freshness">What tells whether a result kept still answers: its ask, what is wrong with it, its state.</param>
    /// <param name="earlier">
    /// The directories of the work this conversation goes on with, the nearest first - the order that work went, newest
    /// first: read, never written, where this store's own holds nothing for an ask under the state asked of.
    /// </param>
    public KeptAnswers(string directory, IFileSystem fileSystem, JsonTypeInfo<TKept> json, IKeptFreshness<TKept> freshness, IReadOnlyList<string>? earlier = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(freshness);

        _directory = directory;
        _fileSystem = fileSystem;
        _json = json;
        _freshness = freshness;
        _earlier = earlier ?? [];
    }

    /// <summary>
    /// The result kept for <paramref name="key"/> under <paramref name="state"/>, where one reads: this store's own - held,
    /// or read once from its directory - else the earlier work's, the nearest first, one found there kept as this store's
    /// own; the nearest one under the state asked of answering, whatever a nearer one kept under another holds. Where none
    /// reads under it: whether one that reads was kept under another state, and else why the nearest one kept does not
    /// read, or nothing where none was kept.
    /// </summary>
    /// <param name="key">The ask's key, as <see cref="PageKey"/> spells it.</param>
    /// <param name="state">The state the answer is asked under (<see cref="IKeptFreshness{TKept}.StateOf"/>).</param>
    public KeptFind<TKept> Find(string key, string state)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(state);

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

                if (!string.Equals(_freshness.StateOf(kept), state, StringComparison.Ordinal))
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

                return new KeptFind<TKept>(kept, null);
            }

            return moved ? new KeptFind<TKept>(null, null) { Moved = true } : new KeptFind<TKept>(null, why);
        }
    }

    /// <summary>
    /// Keeps <paramref name="kept"/> for its ask (<see cref="IKeptFreshness{TKept}.KeyOf"/>), for the conversation's life:
    /// in memory, and packed in the store's directory for a resume. Where it is not written, why - what the page that
    /// computed it says, for a resumed conversation counts its later pages again: the disk refused it; null where it was
    /// written.
    /// </summary>
    /// <param name="kept">The result to keep.</param>
    /// <exception cref="ArgumentException">It is no result this store keeps (<see cref="IKeptFreshness{TKept}.Problem"/>), named in the message.</exception>
    public string? Keep(TKept kept)
    {
        ArgumentNullException.ThrowIfNull(kept);

        if (_freshness.Problem(kept) is { } problem)
        {
            throw new ArgumentException(problem, nameof(kept));
        }

        var key = _freshness.KeyOf(kept);
        var bytes = Packed(kept);

        lock (_lock)
        {
            _own[key] = new Visit(kept, null);
            return Write(key, bytes);
        }
    }

    /// <summary>The bytes <paramref name="kept"/> is held and written as: its one JSON line, compressed.</summary>
    private byte[] Packed(TKept kept)
    {
        using var packed = new MemoryStream();

        using (var compressing = new GZipStream(packed, CompressionLevel.Optimal, leaveOpen: true))
        {
            JsonSerializer.Serialize(compressing, kept, _json);
        }

        return packed.ToArray();
    }

    /// <summary>The result <paramref name="bytes"/> pack (<see cref="Packed"/>); null where they hold null.</summary>
    /// <exception cref="JsonException">They do not read as a result.</exception>
    /// <exception cref="InvalidDataException">They are not compressed as a result is.</exception>
    private TKept? Unpacked(byte[] bytes)
    {
        using var decompressing = new GZipStream(new MemoryStream(bytes, writable: false), CompressionMode.Decompress);
        return JsonSerializer.Deserialize(decompressing, _json);
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
    /// rules; else why not; nothing where it keeps none.
    /// </summary>
    private Visit ReadIn(string directory, string key)
    {
        var file = Path.Combine(directory, key + KeptAnswers.Extension);

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
                : _freshness.Problem(read) is { } problem ? new(null, problem)
                : !string.Equals(_freshness.KeyOf(read), key, StringComparison.Ordinal) ? new(null, "its file is of another ask")
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
            _fileSystem.WriteAtomic(Path.Combine(_directory, key + KeptAnswers.Extension), stream => stream.Write(bytes));
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message.TrimEnd('.');
        }
    }

    /// <summary>What a directory was found to keep for an ask: a result that reads, or why none does; neither where none is kept.</summary>
    private sealed record Visit(TKept? Kept, string? Why)
    {
        public static Visit None { get; } = new(null, null);
    }
}
