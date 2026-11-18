using System.Security.Cryptography;
using DssHarness.Core.FileSystem;

namespace DssHarness.Core.Codebase;

/// <summary>
/// The whole result an answer's pages are read from, kept for the conversation that asked its first page: the answer as
/// it was computed - its leading lines, its sections, its notes - under the digest of the graph it was computed on, so
/// a later page of the same ask is a page of that one result, and a resumed conversation reads what the stream holds
/// page 1 of.
/// </summary>
/// <param name="Schema">The record's schema it was written under.</param>
/// <param name="Key">The ask it is kept for, as <see cref="GraphPageStore.KeyOf"/> spells it.</param>
/// <param name="Digest">The digest of the graph it was computed on (<see cref="CodebaseGraphQueries.Graph"/>).</param>
/// <param name="Asked">How the answer names what was asked, for a page past the last to say.</param>
/// <param name="Leading">The lines the answer begins with.</param>
/// <param name="Sections">The answer's sections.</param>
/// <param name="Trailing">The notes the answer ends with.</param>
/// <param name="Nodes">The nodes the ask named, by their ids.</param>
/// <param name="Viewed">The files the answer served lines of, relative to the tree, as the runtime counts views of them.</param>
public sealed record GraphPageKept(
    int Schema,
    string Key,
    string Digest,
    string Asked,
    IReadOnlyList<string> Leading,
    IReadOnlyList<GraphSection> Sections,
    IReadOnlyList<string> Trailing,
    IReadOnlyList<string> Nodes,
    IReadOnlyList<string> Viewed)
{
    /// <summary>The schema this build reads.</summary>
    public const int CurrentSchema = 1;

    /// <summary>What is wrong with the kept result; <see langword="null"/> where nothing is.</summary>
    public string? Problem()
        => Schema != CurrentSchema ? $"its schema is {Schema.ToString(System.Globalization.CultureInfo.InvariantCulture)}, and this build reads {CurrentSchema.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
            : string.IsNullOrWhiteSpace(Key) ? "it names no ask"
            : string.IsNullOrWhiteSpace(Digest) ? "it names no digest"
            : string.IsNullOrWhiteSpace(Asked) ? "it names no ask to say"
            : Leading.FirstOrDefault(line => string.IsNullOrEmpty(line)) is { } emptyLead ? "its leading lines are lines"
            : Trailing.FirstOrDefault(line => string.IsNullOrEmpty(line)) is { } emptyTail ? "its trailing notes are lines"
            : Sections.FirstOrDefault(section => section?.Problem() is not null) is { } wrong ? wrong.Problem()!
            : null;
}

/// <summary>
/// The results a conversation's graph answers keep, one file an ask, in the step's <c>graph-pages\</c> directory: the
/// first page of an answer computes its whole result once and keeps it, and later pages of the same ask - in the same
/// process, or a conversation resumed over the same directory - read what was kept, so they are pages of that one
/// result. The tool alone uses it; the verb, which reads and answers in one call, keeps nothing.
/// </summary>
/// <remarks>
/// A kept result is a cache of one computation, not a record of what is true: one that does not read - none there, a
/// shape this build does not know, or a file no disk gives - is made again as though none was kept, and one that cannot
/// be written leaves the answer it was made for standing, only its later pages computed again.
/// </remarks>
public sealed class GraphPageStore
{
    /// <summary>The directory's name, under the step's own directory.</summary>
    public const string DirectoryName = "graph-pages";

    private readonly string _directory;
    private readonly IFileSystem _fileSystem;
    private readonly object _lock = new();
    private readonly Dictionary<string, GraphPageKept> _kept = new(StringComparer.Ordinal);

    /// <summary>The store over <paramref name="directory"/>, read and written through <paramref name="fileSystem"/>.</summary>
    /// <param name="directory">The <c>graph-pages\</c> directory, absolute.</param>
    /// <param name="fileSystem">The file system its files are read and written through.</param>
    public GraphPageStore(string directory, IFileSystem fileSystem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(fileSystem);

        _directory = directory;
        _fileSystem = fileSystem;
    }

    /// <summary>The result kept for <paramref name="key"/>, where one is; null where none is.</summary>
    /// <param name="key">The ask's key, as <see cref="KeyOf"/> spells it.</param>
    public GraphPageKept? Find(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        lock (_lock)
        {
            if (_kept.TryGetValue(key, out var held))
            {
                return held;
            }

            try
            {
                if (JsonStateFile.Read<GraphPageKept>(_fileSystem, FileOf(key)) is not { } read || read.Problem() is not null)
                {
                    return null;
                }

                return _kept[key] = read;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                return null;
            }
        }
    }

    /// <summary>Keeps <paramref name="kept"/> for its ask, in memory for this process and in the store's directory for a resume.</summary>
    /// <param name="kept">The result to keep.</param>
    public void Keep(GraphPageKept kept)
    {
        ArgumentNullException.ThrowIfNull(kept);

        if (kept.Problem() is not null)
        {
            return;
        }

        lock (_lock)
        {
            _kept[kept.Key] = kept;

            try
            {
                _fileSystem.CreateDirectory(_directory);
                JsonStateFile.Write(_fileSystem, FileOf(kept.Key), kept);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>The ask a call is kept for: the operation and its input, but its page, hashed to one name.</summary>
    /// <param name="operation">The operation's name, as <c>op</c> says it.</param>
    /// <param name="input">The call's input, its page member left out where it holds one.</param>
    public static string KeyOf(string operation, System.Text.Json.Nodes.JsonObject input)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentNullException.ThrowIfNull(input);

        var members = input.Where(member => member.Key != "page").OrderBy(member => member.Key, StringComparer.Ordinal).ToList();
        var asked = operation + "\n" + string.Join("\n", members.Select(member => $"{member.Key}={member.Value?.ToJsonString() ?? "null"}"));
        return Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(asked))).ToLowerInvariant();
    }

    private string FileOf(string key) => Path.Combine(_directory, key + ".json");
}
