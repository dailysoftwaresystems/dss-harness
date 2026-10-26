using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DssHarness.Core.Agents;
using DssHarness.Core.Execution;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Results;

namespace DssHarness.Core.Web.Search;

/// <summary>
/// What a run's web search keeps, in <c>web/searches.jsonl</c>: what each source's lookups found, read once for the run,
/// and each query a source answered - so the same query again, of the same source as the run asked it, is answered from
/// here with no request. One for each run in the process, shared by its steps; a resumed run reads back what it kept.
/// </summary>
/// <remarks>
/// A source is told apart by its name, its keys as written and its kind's version: a source the configuration changed
/// between steps, or a kind this build reads otherwise, asks again. Only what a source answered is kept - results, or
/// none, as any step may be told them, with each address it asked, so each step is given them under its own rules - never a
/// failure, a refusal or a limit, which a later query asks again; an index's answer is kept once it was no longer being
/// filled; and what lookups found, never why they found nothing. Each line is written with every text in it masked, by
/// the record's one writer in the process, which holds it open until it is disposed - and kept for the run only once it
/// is written, so the run answers nothing from what its record does not hold, which a resumed run would read otherwise.
/// </remarks>
internal sealed class WebSearchRun : IDisposable
{
    private readonly Lock _gate = new();
    private readonly IFileSystem _fileSystem;
    private readonly TimeProvider _clock;
    private readonly Dictionary<string, (WebSearchFound Found, IReadOnlyList<string> Addresses)> _found = new(StringComparer.Ordinal);
    private readonly Dictionary<string, WebSearchRunLine> _searched = new(StringComparer.Ordinal);
    private AppendLog? _log;

    /// <summary>The record of the run whose directory is <paramref name="runDirectory"/>, read back as it stands.</summary>
    /// <exception cref="HarnessException">The record is not this build's.</exception>
    public WebSearchRun(IFileSystem fileSystem, TimeProvider clock, string runDirectory)
    {
        _fileSystem = fileSystem;
        _clock = clock;
        FilePath = OrchestratorLayout.WebSearchesFile(runDirectory);

        var (lines, _) = JsonStateFile.ReadLog<WebSearchRunLine>(
            fileSystem,
            FilePath,
            (line, _) => line.Line switch
            {
                "found" => line is { Values: not null, Addresses: not null } ? null : "it is a found line that holds no values or addresses",
                "searched" => line is { Query: not null, Limit: not null, Keyed: not null, Addresses: [_, ..], Outcome: WebSearchOutcome.Ok or WebSearchOutcome.NoResults }
                    ? null
                    : "it is a searched line that names no query, limit, key's sending, address or answer",
                _ => $"it is a '{line.Line}' line, which is none of found and searched",
            },
            (number, why) => new HarnessException(HarnessExit.InternalError, $"The run's web search record '{FilePath}' does not read at line {number}: {why}. Remove it: the run asks its sources again."));

        foreach (var line in lines)
        {
            if (line.Line == "found")
            {
                _found[Key(line.Source, line.Settings, line.Version)] = (WebSearchFound.Of(line.Values ?? []), line.Addresses ?? []);
            }
            else
            {
                _searched[Key(line.Source, line.Settings, line.Version, line.Query ?? string.Empty, line.Limit ?? 0, line.Keyed ?? false)] = line;
            }
        }
    }

    /// <summary>The record's file.</summary>
    public string FilePath { get; }

    /// <summary>What tells <paramref name="source"/> as it is now from itself as the configuration wrote it otherwise: its keys' digest.</summary>
    public static string SettingsOf(WebSearchSource source)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(source.Settings.GetRawText())))[..16];

    /// <summary>
    /// What <paramref name="source"/>'s lookups found for the run, with each address they asked, which a later step's rules
    /// decide again; <see langword="null"/> where they were not read.
    /// </summary>
    public (WebSearchFound Found, IReadOnlyList<string> Addresses)? Found(WebSearchSource source, int version)
    {
        lock (_gate)
        {
            return _found.TryGetValue(Key(source.Name, SettingsOf(source), version), out var found) ? found : null;
        }
    }

    /// <summary>Keeps what <paramref name="source"/>'s lookups found at <paramref name="addresses"/>, for the run.</summary>
    /// <exception cref="ArgumentException">They found nothing: why is that query's answer alone.</exception>
    public void Keep(WebSearchSource source, int version, WebSearchFound found, IReadOnlyList<string> addresses, ILineMask mask)
    {
        if (found.Failure is not null)
        {
            throw new ArgumentException("Why lookups found nothing is the answer of the query that asked them, never kept for the run.", nameof(found));
        }

        var settings = SettingsOf(source);

        lock (_gate)
        {
            Append(new WebSearchRunLine { At = _clock.GetUtcNow(), Line = "found", Source = source.Name, Settings = settings, Version = version, Values = new(found.Values, StringComparer.Ordinal), Addresses = [.. addresses] }, mask);
            _found[Key(source.Name, settings, version)] = (found, addresses);
        }
    }

    /// <summary>
    /// What <paramref name="source"/> answered <paramref name="query"/>, asked for <paramref name="limit"/> results with its
    /// key or without it as <paramref name="keyed"/> says, earlier in the run; <see langword="null"/> where it was not asked it.
    /// </summary>
    public WebSearchRunLine? Searched(WebSearchSource source, int version, string query, int limit, bool keyed)
    {
        lock (_gate)
        {
            return _searched.GetValueOrDefault(Key(source.Name, SettingsOf(source), version, query, limit, keyed));
        }
    }

    /// <summary>
    /// Keeps what <paramref name="source"/> answered <paramref name="query"/> at <paramref name="addresses"/> - the first
    /// asked, then each redirect, its key sent as <paramref name="keyed"/> says: <paramref name="answer"/> as any step
    /// may be told it - results, or none - before any step's rules.
    /// </summary>
    public void Keep(WebSearchSource source, int version, string query, int limit, bool keyed, IReadOnlyList<string> addresses, WebSearchAnswer answer, ILineMask mask)
    {
        if (answer.Outcome is not (WebSearchOutcome.Ok or WebSearchOutcome.NoResults))
        {
            throw new ArgumentException($"A source's answer is kept where it answered, and this one is {answer.Outcome}.", nameof(answer));
        }

        if (addresses.Count == 0)
        {
            throw new ArgumentException("A source's answer is kept with each address it was asked at, which a later step's rules decide again.", nameof(addresses));
        }

        var line = new WebSearchRunLine
        {
            At = _clock.GetUtcNow(),
            Line = "searched",
            Source = source.Name,
            Settings = SettingsOf(source),
            Version = version,
            Query = query,
            Limit = limit,
            Keyed = keyed,
            Addresses = [.. addresses],
            Outcome = answer.Outcome,
            Results = [.. answer.Results],
            Total = answer.Total,
            Held = answer.Held,
            LeftOut = answer.LeftOut,
        };

        lock (_gate)
        {
            Append(line, mask);
            _searched[Key(line.Source, line.Settings, version, query, limit, keyed)] = line;
        }
    }

    /// <summary>Closes the record; it ends at its last whole line.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            _log?.Dispose();
            _log = null;
        }
    }

    /// <summary>Appends <paramref name="line"/>, each of its texts masked where it stands, so no secret's spelling in the line's own form escapes the mask nor the mask the line's form.</summary>
    private void Append(WebSearchRunLine line, ILineMask mask)
    {
        if (_log is null)
        {
            _fileSystem.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            _log = AppendLog.Open(_fileSystem, FilePath);
        }

        _log.Append((StoredStream.Masked(JsonSerializer.SerializeToNode(line, JsonStateFile.LineOptions), mask) ?? new JsonObject()).ToJsonString(JsonStateFile.LineOptions));
    }

    private static string Key(params object[] parts) => string.Join('\u001F', parts);
}

/// <summary>One line of a run's web search record.</summary>
internal sealed record WebSearchRunLine
{
    /// <summary>When it was written.</summary>
    public required DateTimeOffset At { get; init; }

    /// <summary><c>found</c>, what a source's lookups found; <c>searched</c>, what it answered a query.</summary>
    public required string Line { get; init; }

    /// <summary>The source's name.</summary>
    public required string Source { get; init; }

    /// <summary>The digest of the source's keys as written (<see cref="WebSearchRun.SettingsOf"/>).</summary>
    public required string Settings { get; init; }

    /// <summary>Its kind's version.</summary>
    public required int Version { get; init; }

    /// <summary>What its lookups found, by name: a found line's.</summary>
    public Dictionary<string, string>? Values { get; init; }

    /// <summary>The query: a searched line's.</summary>
    public string? Query { get; init; }

    /// <summary>
    /// Each address the source asked for it - the first, then each redirect - which a later step's rules decide again: a
    /// searched line's, or a found line's, each its lookups asked.
    /// </summary>
    public List<string>? Addresses { get; init; }

    /// <summary>The most results it was asked for: a searched line's.</summary>
    public int? Limit { get; init; }

    /// <summary>Whether its key was sent with it, which an answer to a step that sends it, or one that does not, is apart from: a searched line's.</summary>
    public bool? Keyed { get; init; }

    /// <summary>What it came to: a searched line's.</summary>
    public WebSearchOutcome? Outcome { get; init; }

    /// <summary>Its results, as any step may be told them, before any step's rules: a searched line's.</summary>
    public List<WebSearchResult>? Results { get; init; }

    /// <summary>How many results it said it holds.</summary>
    public long? Total { get; init; }

    /// <summary>What an index answered from.</summary>
    public string? Held { get; init; }

    /// <summary>How many results named no title or no address.</summary>
    public int LeftOut { get; init; }

    /// <summary>A searched line's answer, as any step may be told it.</summary>
    public WebSearchAnswer Answer() => WebSearchAnswer.Read(Results ?? []) with { Total = Total, Held = Held, LeftOut = LeftOut };
}
