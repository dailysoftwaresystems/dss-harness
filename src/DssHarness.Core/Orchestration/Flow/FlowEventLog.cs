using System.Globalization;
using System.Text.Json;
using DssHarness.Core.Agents;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Results;

namespace DssHarness.Core.Orchestration.Flow;

/// <summary>
/// A run's events, <c>events.jsonl</c>: opened once for append, a per-run <see cref="FlowEvent.Seq"/>, each line handed
/// to the system whole and in order as it is appended and put on the disk once per pass of the engine
/// (<see cref="Flush"/>) and as the log closes - so an end of the engine's process loses none. A power loss loses what the
/// last pass had not put on the disk, never a line before it - where the file system shows a byte it never wrote as a NUL,
/// as ext4's ordered and journalled modes, XFS, Btrfs, APFS and NTFS do; one that can show a block's old bytes instead
/// (ext4's writeback mode) can leave a line among the last pass's that reads as no event, which is refused by its number.
/// History, never truth - the state files are written before the event that tells of them, so a client woken by an event
/// reads the files that tell of it already written.
/// </summary>
/// <remarks>
/// The log is one <see cref="AppendLog"/>, written a batch at a time: its one in-process lock keeps two writers in this
/// process from interleaving their lines, its claim keeps a second writer out, and its opening takes back the half of a
/// torn line a killed engine left, and what a power loss left of the pass it cut off - from the log's first NUL to its end,
/// for no line holds one and the pass's unwritten pages can come back as NULs ahead of its later lines. The reader sets
/// aside a last line with no newline, and the first that holds a NUL with every line after it, and says so - for a line is
/// whole only once its newline is written, and on the disk only once its pass is; every whole line before them is an event
/// numbered one after the one before it, and one that is not is refused by its line, never skipped.
/// </remarks>
public sealed class FlowEventLog : IDisposable
{
    private readonly AppendLog _log;
    private readonly object _gate = new();

    private int _seq;

    private FlowEventLog(AppendLog log, int seq, int sessions, string? setAside)
    {
        _log = log;
        _seq = seq;
        Sessions = sessions;
        SetAside = setAside;
    }

    /// <summary>
    /// The sessions of the run the log told of as it was opened (<see cref="SessionsOf"/>): what the sessions the engine that
    /// opened it drives are counted on from, so each of its events names the run's session it belongs to
    /// (<see cref="FlowEvent.Session"/>).
    /// </summary>
    public int Sessions { get; }

    /// <summary>The log's path.</summary>
    public string File => _log.FilePath;

    /// <summary>
    /// What the open took back, in the reader's words (<see cref="Read"/>): the half of a torn line a killed engine left,
    /// or what a power loss left of the pass it cut off; null where the log was whole.
    /// </summary>
    public string? SetAside { get; }

    /// <summary>
    /// Opens the run's event log for appending, taking back the half of a torn line a killed engine left and what a power
    /// loss left of the pass it cut off, from the first NUL on - read strictly first, the lines it takes back set aside and
    /// said (<see cref="SetAside"/>), so a log refused is left as it was. Its last whole line's seq is where the run's
    /// events go on.
    /// </summary>
    /// <param name="fileSystem">Writes the lines.</param>
    /// <param name="file">The log's path, its directory already there.</param>
    /// <exception cref="HarnessException">A whole line of the log is not an event, or not the next one (<see cref="HarnessExit.Refused"/>).</exception>
    public static FlowEventLog Open(IFileSystem fileSystem, string file)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentException.ThrowIfNullOrWhiteSpace(file);

        var (events, setAside) = Read(fileSystem, file);
        return new FlowEventLog(AppendLog.Open(fileSystem, file, batched: true), events.Count > 0 ? events[^1].Seq : 0, SessionsOf(events), setAside);
    }

    /// <summary>
    /// The sessions of a run its events tell of: the highest session an event names - one whose engine wrote events and never
    /// recorded its beginning, its start refused or a crash ending it, among them - and no fewer than the sessions whose
    /// beginning they record, as an older build's events, which name none, tell of theirs; 0 for a run with none.
    /// </summary>
    /// <param name="events">The run's events, in the order written.</param>
    public static int SessionsOf(IReadOnlyList<FlowEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);

        string[] beginnings = [FlowEvent.KindOf(FlowChangeKind.SessionBegan), FlowEvent.KindOf(FlowChangeKind.Resumed)];

        return Math.Max(
            events.Select(@event => @event.Session ?? 0).DefaultIfEmpty(0).Max(),
            events.Count(@event => beginnings.Contains(@event.Kind, StringComparer.Ordinal)));
    }

    /// <summary>
    /// Appends one event whole, numbered from the run's own count: the system's before it answers, and on the disk at the
    /// next <see cref="Flush"/>.
    /// </summary>
    /// <param name="at">When the event happened.</param>
    /// <param name="kind">What changed: the event's kind is its name (<see cref="FlowEvent.KindOf"/>).</param>
    /// <param name="instance">The instance it tells of; null for the run's own.</param>
    /// <param name="step">The instance's declaration path; null where no instance is named.</param>
    /// <param name="from">The state the instance stood in before it.</param>
    /// <param name="to">The state the instance stands in now.</param>
    /// <param name="attempt">Which attempt the event tells of.</param>
    /// <param name="detail">What the event says in words, where the kinds alone cannot.</param>
    /// <param name="session">The run's session the event belongs to (<see cref="FlowEvent.Session"/>); null where none is named.</param>
    public FlowEvent Append(DateTimeOffset at, FlowChangeKind kind, string? instance = null, string? step = null, string? from = null, string? to = null, int? attempt = null, string? detail = null, int? session = null)
    {
        FlowEvent written;

        lock (_gate)
        {
            written = new FlowEvent(_seq + 1, at, FlowEvent.KindOf(kind), instance, step, from, to, attempt, detail, session);

            if (written.Problem() is { } problem)
            {
                throw new InvalidOperationException($"Refusing to append an event that does not read back: {problem}.");
            }

            _log.Write(JsonSerializer.Serialize(written, JsonStateFile.LineOptions));
            _seq = written.Seq;
        }

        return written;
    }

    /// <summary>
    /// Puts every event appended since the last flush on the disk: the engine asks it as each of its passes ends and as
    /// its session does (<see cref="IFlowRecords.Flush"/>). Nothing where nothing was appended.
    /// </summary>
    /// <exception cref="IOException">They could not be put on the disk.</exception>
    public void Flush() => _log.Flush();

    /// <summary>
    /// Closes the log, every event appended put on the disk first where it can be - never throwing a flush's failure over
    /// one a session leaves by (<see cref="AppendLog.Dispose"/>): a writer that must hear of it flushes first
    /// (<see cref="Flush"/>). Its last line is whole, and the next open begins after it.
    /// </summary>
    public void Dispose() => _log.Dispose();

    /// <summary>
    /// Reads the log: each whole line an event, in order, numbered one after the other from 1. A last line with no
    /// newline is the crash's own - half an append, or a whole object whose newline never reached the disk - and is set
    /// aside and said, never read as an event and never repaired; so is the first line that holds a NUL, with every line
    /// after it - what a power loss left of the pass it cut off.
    /// </summary>
    /// <param name="fileSystem">Reads the log.</param>
    /// <param name="file">The log's path.</param>
    /// <returns>
    /// Each event the log holds, and what was set aside where anything was - a torn last line, or the lines from the first
    /// that holds a NUL on - in the words every reader says it in.
    /// </returns>
    /// <exception cref="HarnessException">A whole line is not an event, or not the one after the line before it (<see cref="HarnessExit.Refused"/>).</exception>
    public static (IReadOnlyList<FlowEvent> Events, string? Torn) Read(IFileSystem fileSystem, string file)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentException.ThrowIfNullOrWhiteSpace(file);

        var (events, torn) = JsonStateFile.ReadLog<FlowEvent>(
            fileSystem,
            file,
            (read, before) => read.Problem() ?? (read.Seq != before.Count + 1
                ? $"its seq is {read.Seq.ToString(CultureInfo.InvariantCulture)}, and the event before it was {before.Count.ToString(CultureInfo.InvariantCulture)}"
                : null),
            (line, why) => Corrupt(file, line, why),
            batched: true);

        return (events, torn switch
        {
            null => null,
            [.. var before, '\0'] => $"the log's lines from the one after event {events.Count.ToString(CultureInfo.InvariantCulture)} on hold a NUL, what a power loss left of a pass never put on the disk, and were set aside: '{before}'",
            _ => $"the log's last line has no newline and was set aside: '{torn}'",
        });
    }

    /// <summary>The refusal of a whole line of the log that is not the next event: history is never guessed at either.</summary>
    private static HarnessException Corrupt(string file, int line, string why)
        => new(
            HarnessExit.Refused,
            $"line {line.ToString(CultureInfo.InvariantCulture)} of '{file}', the run's events, does not read as its next event: {why}. Nothing was changed: an event is never guessed at, "
            + "and the run's state files - not its events - are what a resume reads; move the file aside to go on with a log of its own.");
}
