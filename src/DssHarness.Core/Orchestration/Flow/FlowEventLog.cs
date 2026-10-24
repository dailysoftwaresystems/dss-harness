using System.Globalization;
using System.Text.Json;
using DssHarness.Core.Agents;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Results;

namespace DssHarness.Core.Orchestration.Flow;

/// <summary>
/// A run's events, <c>events.jsonl</c>: opened once for append, each durable line flushed, a per-run
/// <see cref="FlowEvent.Seq"/>. History, never truth - the state files are written before the event that tells of
/// them, so a client woken by an event reads the files that tell of it already written.
/// </summary>
/// <remarks>
/// The log is one <see cref="AppendLog"/>: its one in-process lock keeps two writers in this process from interleaving
/// their lines, its claim keeps a second writer out, and its opening takes back the half of a torn line a killed engine
/// left. The reader sets aside a last line with no newline, and says so - that line alone, whatever it holds, for a
/// line is durable only once its newline is written; every whole line is an event numbered one after the one before
/// it, and one that is not is refused by its line, never skipped.
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
    /// <exception cref="HarnessException">A whole line of the log is not an event, or not the next one (<see cref="HarnessExit.Refused"/>).</exception>
    public static FlowEventLog Open(IFileSystem fileSystem, string file)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentException.ThrowIfNullOrWhiteSpace(file);

        var (events, _) = Read(fileSystem, file);
        return new FlowEventLog(AppendLog.Open(fileSystem, file), events.Count > 0 ? events[^1].Seq : 0);
    }

    /// <summary>Appends one event whole, flushed before it answers, numbered from the run's own count.</summary>
    /// <param name="at">When the event happened.</param>
    /// <param name="kind">What changed: the event's kind is its name (<see cref="FlowEvent.KindOf"/>).</param>
    /// <param name="instance">The instance it tells of; null for the run's own.</param>
    /// <param name="step">The instance's declaration path; null where no instance is named.</param>
    /// <param name="from">The state the instance stood in before it.</param>
    /// <param name="to">The state the instance stands in now.</param>
    /// <param name="attempt">Which attempt the event tells of.</param>
    /// <param name="detail">What the event says in words, where the kinds alone cannot.</param>
    public FlowEvent Append(DateTimeOffset at, FlowChangeKind kind, string? instance = null, string? step = null, string? from = null, string? to = null, int? attempt = null, string? detail = null)
    {
        FlowEvent written;

        lock (_gate)
        {
            written = new FlowEvent(_seq + 1, at, FlowEvent.KindOf(kind), instance, step, from, to, attempt, detail);

            if (written.Problem() is { } problem)
            {
                throw new InvalidOperationException($"Refusing to append an event that does not read back: {problem}.");
            }

            _log.Append(JsonSerializer.Serialize(written, JsonStateFile.LineOptions));
            _seq = written.Seq;
        }

        return written;
    }

    /// <summary>Closes the log. Its last line is whole, and the next open begins after it.</summary>
    public void Dispose() => _log.Dispose();

    /// <summary>
    /// Reads the log: each whole line an event, in order, numbered one after the other from 1. A last line with no
    /// newline is the crash's own - half an append, or a whole object whose newline never reached the disk - and is set
    /// aside and said, never read as an event and never repaired.
    /// </summary>
    /// <param name="fileSystem">Reads the log.</param>
    /// <param name="file">The log's path.</param>
    /// <returns>Each event the log holds, and why its last line was set aside where it was.</returns>
    /// <exception cref="HarnessException">A whole line is not an event, or not the one after the line before it (<see cref="HarnessExit.Refused"/>).</exception>
    public static (IReadOnlyList<FlowEvent> Events, string? Torn) Read(IFileSystem fileSystem, string file)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentException.ThrowIfNullOrWhiteSpace(file);

        if (fileSystem.KindOf(file) != PathKind.File)
        {
            return ([], null);
        }

        string? torn = null;
        var events = new List<FlowEvent>();
        var at = 0;

        // A line at a time: the log grows with its run.
        foreach (var (text, ended) in FileLines.Read(fileSystem, file))
        {
            if (!ended)
            {
                torn = $"the log's last line has no newline and was set aside: '{text}'";
                break;
            }

            var line = text.TrimEnd('\r');
            var number = (++at).ToString(CultureInfo.InvariantCulture);
            FlowEvent? read;

            try
            {
                read = JsonSerializer.Deserialize<FlowEvent>(line, JsonStateFile.LineOptions);
            }
            catch (JsonException ex)
            {
                throw Corrupt(file, number, ex.Message.TrimEnd('.'));
            }

            var problem = read is null ? "it holds null" : read.Problem();

            if (problem is null && read!.Seq != events.Count + 1)
            {
                problem = $"its seq is {read.Seq.ToString(CultureInfo.InvariantCulture)}, and the event before it was {events.Count.ToString(CultureInfo.InvariantCulture)}";
            }

            if (problem is not null)
            {
                throw Corrupt(file, number, problem);
            }

            events.Add(read!);
        }

        return (events, torn);
    }

    /// <summary>The refusal of a whole line of the log that is not the next event: history is never guessed at either.</summary>
    private static HarnessException Corrupt(string file, string line, string why)
        => new(
            HarnessExit.Refused,
            $"line {line} of '{file}', the run's events, does not read as its next event: {why}. Nothing was changed: an event is never guessed at, "
            + "and the run's state files - not its events - are what a resume reads; move the file aside to go on with a log of its own.");
}
