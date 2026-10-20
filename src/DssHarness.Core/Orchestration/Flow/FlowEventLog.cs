using System.Text.Json;
using DssHarness.Core.Agents;
using DssHarness.Core.FileSystem;

namespace DssHarness.Core.Orchestration.Flow;

/// <summary>
/// A run's events, <c>events.jsonl</c>: opened once for append, each durable line flushed, a per-run
/// <see cref="FlowEvent.Seq"/>. History, never truth - the state files are written before the event that tells of
/// them, so a client woken by an event reads the files that tell of it already written.
/// </summary>
/// <remarks>
/// The log is one <see cref="AppendLog"/>: its one in-process lock keeps two writers in this process from interleaving
/// their lines, its claim keeps a second writer out, and its opening takes back the half of a torn line a killed engine
/// left. The reader of a log no writer holds ignores a last line with no newline and says so.
/// </remarks>
public sealed class FlowEventLog : IDisposable
{
    private readonly AppendLog _log;
    private readonly object _gate = new();

    private int _seq;

    private FlowEventLog(AppendLog log, int seq)
    {
        _log = log;
        _seq = seq;
    }

    /// <summary>The log's path.</summary>
    public string File => _log.FilePath;

    /// <summary>
    /// Opens the run's event log for appending, taking back the half of a torn line a killed engine left. Its last
    /// whole line's seq is where the run's events go on.
    /// </summary>
    /// <param name="fileSystem">Writes the lines.</param>
    /// <param name="file">The log's path, its directory already there.</param>
    public static FlowEventLog Open(IFileSystem fileSystem, string file)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentException.ThrowIfNullOrWhiteSpace(file);

        var (events, _) = Read(fileSystem, file);
        return new FlowEventLog(AppendLog.Open(fileSystem, file), events.Count > 0 ? events[^1].Seq : 0);
    }

    /// <summary>Appends one event whole, flushed before it answers, numbered from the run's own count.</summary>
    /// <param name="at">When the event happened.</param>
    /// <param name="kind">What kind of event it is.</param>
    /// <param name="instance">The instance it tells of; null for the run's own.</param>
    /// <param name="step">The instance's declaration path; null where no instance is named.</param>
    /// <param name="from">The state the instance stood in before it.</param>
    /// <param name="to">The state the instance stands in now.</param>
    /// <param name="attempt">Which attempt the event tells of.</param>
    /// <param name="detail">What the event says in words, where the kinds alone cannot.</param>
    public FlowEvent Append(DateTimeOffset at, string kind, string? instance = null, string? step = null, string? from = null, string? to = null, int? attempt = null, string? detail = null)
    {
        FlowEvent written;

        lock (_gate)
        {
            written = new FlowEvent(++_seq, at, kind, instance, step, from, to, attempt, detail);

            if (written.Problem() is { } problem)
            {
                throw new InvalidOperationException($"Refusing to append an event that does not read back: {problem}.");
            }

            _log.Append(JsonSerializer.Serialize(written, JsonStateFile.LineOptions));
        }

        return written;
    }

    /// <summary>Closes the log. Its last line is whole, and the next open begins after it.</summary>
    public void Dispose() => _log.Dispose();

    /// <summary>
    /// Reads the log: each whole line an event, in order. A last line with no newline is the crash's own - half an
    /// append - and is ignored and said, never read as an event and never repaired.
    /// </summary>
    /// <param name="fileSystem">Reads the log.</param>
    /// <param name="file">The log's path.</param>
    /// <returns>Each event the log holds, and why its last line was ignored where it was.</returns>
    public static (IReadOnlyList<FlowEvent> Events, string? Torn) Read(IFileSystem fileSystem, string file)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentException.ThrowIfNullOrWhiteSpace(file);

        if (fileSystem.KindOf(file) != PathKind.File)
        {
            return ([], null);
        }

        var text = fileSystem.ReadAllText(file);
        var torn = text.Length > 0 && !text.EndsWith('\n')
            ? $"the log's last line has no newline and was ignored: '{text[(text.LastIndexOf('\n') + 1)..]}'"
            : null;
        var events = new List<FlowEvent>();

        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length == 0)
            {
                continue;
            }

            var read = line.TrimEnd('\r');

            try
            {
                if (JsonSerializer.Deserialize<FlowEvent>(read, JsonStateFile.LineOptions) is { } readEvent
                    && readEvent.Problem() is null)
                {
                    events.Add(readEvent);
                }
            }
            catch (JsonException)
            {
                // A line that holds no event is ignored here as the reader of a record names it elsewhere; the log is
                // history, and no run stands on it.
            }
        }

        return (events, torn);
    }
}
