using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using DssHarness.Core.Execution;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Hosts;
using DssHarness.Core.Json;
using DssHarness.Core.Repository;

namespace DssHarness.Core.Mutations;

/// <summary>A run that reached or verified a recorded verdict, and when, as the clock of the machine it ran on said.</summary>
/// <param name="Run">The run's id.</param>
/// <param name="At">When.</param>
public sealed record RecordedRun(string Run, DateTimeOffset At);

/// <summary>
/// One verdict of an arm on a leg, as the record of verdicts keeps it: what the arm reached, which run reached it, when and
/// in which tree, and what it was judged on - its key, the parts the key is the digest of, and the causes kept beside them.
/// </summary>
public sealed record RecordedVerdict
{
    /// <summary>The verdict, as the ledger names it.</summary>
    public required string Verdict { get; init; }

    /// <summary>Why, in the words the arm's line said it.</summary>
    public string Detail { get; init; } = string.Empty;

    /// <summary>The run that reached it.</summary>
    public required string Run { get; init; }

    /// <summary>
    /// When its arm ended, as the clock of the machine that swept it said. Told, and never what entries are ordered by: a
    /// host's clock may step.
    /// </summary>
    public required DateTimeOffset At { get; init; }

    /// <summary>
    /// Which tree's sweep reached it, as a line names a tree: <c>the main checkout</c>, or <c>worktree o1/xa</c>. Told, and
    /// never compared: a verdict reached in one tree holds in another wherever nothing it was judged on differs.
    /// </summary>
    public required string SweptIn { get; init; }

    /// <summary>The key of what it was judged on, or <see langword="null"/> where a part could not be read: such a verdict never holds.</summary>
    public string? Key { get; init; }

    /// <summary>Why it has no key, as what follows "could not be read when it was swept: "; <see langword="null"/> where it has one.</summary>
    public string? KeyUnread { get; init; }

    /// <summary>What it was judged on, as far as each part could be read; <see langword="null"/> where nothing of it was.</summary>
    public JudgedOn? JudgedOn { get; init; }

    /// <summary>What made the leg's build lines what they were, kept beside the key and never in it.</summary>
    public JudgedCauses? Causes { get; init; }

    /// <summary>The last sweep that found its key holding on the leg's machine, where one has.</summary>
    public RecordedRun? Verified { get; init; }

    /// <summary>When its arm ended, as a line says it: <c>2026-10-12 14:58Z</c>.</summary>
    [JsonIgnore]
    public string When => At.UtcDateTime.ToString("yyyy-MM-dd HH:mm'Z'", CultureInfo.InvariantCulture);
}

/// <summary>Why a leg's record of verdicts cannot be read: what reading it said, or the newer format it was written in.</summary>
/// <remarks>
/// Made by <see cref="Because"/> or <see cref="Newer"/>, and read back by its one constructor where a dispatch carried
/// it to the host that sweeps the leg (<see cref="RecordedArms.Carried"/>): one of the two, never both, never neither.
/// </remarks>
public sealed record RecordUnread
{
    /// <summary>A record unreadable for <paramref name="why"/>, or written in the newer <paramref name="format"/>: exactly one.</summary>
    /// <exception cref="ArgumentException">Neither is given, both are, or <paramref name="why"/> says nothing.</exception>
    [JsonConstructor]
    private RecordUnread(string? why = null, int? format = null)
    {
        if ((why is null) == (format is null) || (why is not null && string.IsNullOrWhiteSpace(why)))
        {
            throw new ArgumentException("A record that cannot be read is so for what reading it said, or for the newer format it was written in: exactly one.");
        }

        Why = why;
        Format = format;
    }

    /// <summary>What reading it said, where it is unreadable; <see langword="null"/> where it is of a newer format.</summary>
    public string? Why { get; }

    /// <summary>The format it was written in, where a newer build wrote it; <see langword="null"/> where it is unreadable.</summary>
    public int? Format { get; }

    /// <summary>A record that cannot be read, saying <paramref name="why"/>.</summary>
    /// <param name="why">What reading it said.</param>
    public static RecordUnread Because(string why)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(why);

        return new(why.TrimEnd('.'), null);
    }

    /// <summary>A record a newer build wrote, in <paramref name="format"/>.</summary>
    /// <param name="format">Its format.</param>
    public static RecordUnread Newer(int format) => new(null, format);

    /// <summary>Why it cannot be read, in one clause: what reading it said, or which newer format it is in.</summary>
    [JsonIgnore]
    public string Said
        => Format is { } format
            ? $"it was written by a newer {ToolPackage.Id} (format {format}; this one reads {ArmVerdictRecord.Format})"
            : Why!;

    /// <summary>
    /// What a command that meets it says, once, as a warning: every arm of the leg is pending - and, of a sweep, that it
    /// records none.
    /// </summary>
    /// <param name="file">The leg's record.</param>
    /// <param name="leg">The leg.</param>
    /// <param name="sweep">Whether a sweep met it, which then records none of the leg's verdicts.</param>
    public string Warning(string file, string leg, bool sweep = true)
    {
        var consequence = sweep ? $"every arm of '{leg}' is pending and this sweep records none" : $"every arm of '{leg}' is pending";

        return Format is { } format
            ? $"'{file}' was written by a newer {ToolPackage.Id} (format {format}; this one reads {ArmVerdictRecord.Format}), so "
                + $"{consequence}. Update the tool ({ToolUpdateService.CommandName})."
            : $"'{file}' cannot be read, so {consequence}: {Why}. Delete it to start that leg's record again.";
    }

    /// <summary>What the leg's line says of it, once.</summary>
    /// <param name="file">The leg's record.</param>
    public string Note(string file)
        => Format is null
            ? $"none of its verdicts was recorded: '{file}' cannot be read"
            : $"none of its verdicts was recorded: '{file}' was written by a newer {ToolPackage.Id}";
}

/// <summary>What the record of verdicts holds of one leg, or why it holds nothing that can be read.</summary>
/// <remarks>
/// Made one way, by <see cref="Read"/> and <see cref="Unreadable"/>: a record either holds what it holds or is unreadable,
/// never both, and never neither.
/// </remarks>
public sealed record LegVerdictRecord
{
    private LegVerdictRecord(string file, IReadOnlyDictionary<string, IReadOnlyList<RecordedVerdict>> arms, IReadOnlyDictionary<string, TableIndex> tables, RecordUnread? unread)
    {
        File = file;
        Arms = arms;
        Tables = tables;
        Unread = unread;
    }

    /// <summary>The leg's record.</summary>
    public string File { get; }

    /// <summary>
    /// Each arm's entries, by its id, compared ignoring case: one to a key, in the order recorded, the newest last.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<RecordedVerdict>> Arms { get; }

    /// <summary>Each table an entry names, by its digest.</summary>
    public IReadOnlyDictionary<string, TableIndex> Tables { get; }

    /// <summary>Why the record cannot be read, or <see langword="null"/> where it was read or is not there.</summary>
    public RecordUnread? Unread { get; }

    /// <summary>A record read: what it holds.</summary>
    /// <param name="file">The leg's record.</param>
    /// <param name="arms">Each arm's entries, by its id.</param>
    /// <param name="tables">Each table an entry names, by its digest.</param>
    internal static LegVerdictRecord Read(string file, IReadOnlyDictionary<string, IReadOnlyList<RecordedVerdict>> arms, IReadOnlyDictionary<string, TableIndex> tables)
        => new(file, arms, tables, null);

    /// <summary>A record that cannot be read, for <paramref name="unread"/>.</summary>
    /// <param name="file">The leg's record.</param>
    /// <param name="unread">Why it cannot be read.</param>
    internal static LegVerdictRecord Unreadable(string file, RecordUnread unread)
    {
        ArgumentNullException.ThrowIfNull(unread);

        return new(file, new Dictionary<string, IReadOnlyList<RecordedVerdict>>(StringComparer.OrdinalIgnoreCase), new Dictionary<string, TableIndex>(StringComparer.Ordinal), unread);
    }
}

/// <summary>A table of the record, as an entry named it, or why it could not be read: one or the other.</summary>
public sealed record RecordedTable
{
    private RecordedTable(BuiltFromTable? table, string? unread)
    {
        Table = table;
        Unread = unread;
    }

    /// <summary>The table, or <see langword="null"/> where it could not be read.</summary>
    public BuiltFromTable? Table { get; }

    /// <summary>Why it could not be read - a table an entry names that is gone, or holds anything but a table of that digest; <see langword="null"/> where there is one.</summary>
    public string? Unread { get; }

    /// <summary>The table of <paramref name="table"/>, as read.</summary>
    /// <param name="table">The table.</param>
    public static RecordedTable Read(BuiltFromTable table)
    {
        ArgumentNullException.ThrowIfNull(table);

        return new(table, null);
    }

    /// <summary>A table that could not be read, for <paramref name="unread"/>.</summary>
    /// <param name="unread">Why.</param>
    public static RecordedTable Unreadable(string unread)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(unread);

        return new(null, unread.TrimEnd('.'));
    }
}

/// <summary>
/// Each leg's recorded verdicts of its mutation arms, with what each was judged on, in the main checkout's harness
/// directory (<see cref="HarnessLayout.MutationRecordDirectory"/>): one log to a leg, and beside them each table an entry
/// names, written once under its digest.
/// </summary>
/// <remarks>
/// <para>
/// One log to a leg: a sweep of one leg never touches another's verdicts, two legs' sweeps never wait on one file, and a
/// log that cannot be read loses one leg. Each change appends its lines under the log's machine-wide mutex, after reading
/// only what was appended since this record last read it, so two sweeps recording at once - of one leg, from two trees -
/// lose neither's verdicts, whatever their order, and a year of nightly sweeps costs each change what it adds, never a
/// rewrite of all that came before.
/// </para>
/// <para>
/// Every entry is kept. Read back, an arm holds one entry to a key, in the order recorded: a later verdict on the same key
/// replaces the entry, whatever either was, by its place in the log - never by when, as a host's clock may step - and no
/// key is ever dropped for the arm holding many. A table is written before the first entry naming it, and kept: every
/// entry the log holds may still name it.
/// </para>
/// <para>
/// The whole document an earlier build kept a leg's verdicts in (<see cref="DocumentOf"/>) is read once, where no log is
/// there yet, and carried into the log whole; it is never written again.
/// </para>
/// <para>
/// A record that cannot be read, or that a newer build wrote, is never written: it is said why, and its leg records
/// nothing until it is deleted or the tool updated.
/// </para>
/// </remarks>
/// <param name="fileSystem">Reads and writes the record.</param>
/// <param name="layout">The repository, whose main checkout keeps the record.</param>
public sealed class ArmVerdictRecord(IFileSystem fileSystem, HarnessLayout layout)
{
    /// <summary>The format of the log this build writes and reads.</summary>
    public const int Format = 2;

    /// <summary>The format of the retired whole document, which this build reads where no log is there yet, and never writes.</summary>
    public const int DocumentFormat = 1;

    /// <summary>The directory, in the record's, that holds a directory of tables for each leg.</summary>
    public const string TablesDirectoryName = "built";

    /// <summary>
    /// How a line of the log is read: one object, at any depth - and so every line of the record's own shape, wherever it
    /// comes from: a note a host wrote in passing, the arms a dispatch carried.
    /// </summary>
    internal static readonly JsonSerializerOptions LineReading = JsonDepth.Serializer(JsonStateFile.LineOptions);

    /// <summary>How the retired whole document is read: at any depth.</summary>
    private static readonly JsonSerializerOptions DocumentReading = JsonDepth.Serializer(JsonStateFile.Options);

    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly HarnessLayout _layout = layout;

    /// <summary>How far this record has read each leg's log, by the leg, so a change reads only what is new.</summary>
    private readonly Dictionary<string, LogState> _logs = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The record's directory.</summary>
    public string Directory => _layout.MutationRecordDirectory;

    /// <summary>Where <paramref name="leg"/>'s verdicts are recorded: its log, a line to each change.</summary>
    /// <param name="leg">The leg, as the configuration names it.</param>
    public string FileOf(string leg) => Path.Combine(Directory, leg + ".jsonl");

    /// <summary>Where an earlier build kept <paramref name="leg"/>'s verdicts, as one whole document: read once, never written.</summary>
    /// <param name="leg">The leg, as the configuration names it.</param>
    public string DocumentOf(string leg) => Path.Combine(Directory, leg + ".json");

    /// <summary>
    /// The file <paramref name="leg"/>'s record is read from, as a line about it names it: the retired whole document where
    /// only that is there, which is what a record that cannot be read then is; otherwise the log.
    /// </summary>
    /// <param name="leg">The leg.</param>
    public string ReadFrom(string leg)
        => !_fileSystem.FileExists(FileOf(leg)) && _fileSystem.FileExists(DocumentOf(leg)) ? DocumentOf(leg) : FileOf(leg);

    /// <summary>Where the tables <paramref name="leg"/>'s entries name are kept.</summary>
    /// <param name="leg">The leg.</param>
    public string TablesOf(string leg) => Path.Combine(Directory, TablesDirectoryName, leg);

    /// <summary>The table of <paramref name="leg"/> whose digest is <paramref name="digest"/>.</summary>
    /// <param name="leg">The leg.</param>
    /// <param name="digest">The table's digest (<see cref="BuiltFromTable.Digest"/>).</param>
    public string TableFile(string leg, string digest) => Path.Combine(TablesOf(leg), digest + ".json");

    /// <summary>What the record holds of <paramref name="leg"/>, or why it cannot be read.</summary>
    /// <param name="leg">The leg.</param>
    /// <exception cref="Results.HarnessException">Another process held the leg's record past the window.</exception>
    public LegVerdictRecord Read(string leg) => Held(leg, () => ReadHeld(leg));

    /// <summary>
    /// The table of <paramref name="leg"/> whose digest is <paramref name="digest"/>, as an entry naming it was recorded
    /// with, or why it cannot be read: a table an entry names that is gone, or holds anything but a table of that digest.
    /// Read holding the leg's record's mutex, so a change of the record pruning it meanwhile cannot take it away between
    /// the ask and the read.
    /// </summary>
    /// <param name="leg">The leg.</param>
    /// <param name="digest">The table's digest, as an entry names it.</param>
    public RecordedTable Table(string leg, string digest)
        => Held(leg, () =>
        {
            var file = TableFile(leg, digest);

            if (!MachineWideFile.TryRead<BuiltFromTable>(_fileSystem, file, out var table, out var unreadable))
            {
                return RecordedTable.Unreadable($"'{file}' cannot be read: {unreadable.Message}");
            }

            if (table is null)
            {
                return RecordedTable.Unreadable($"'{file}', which an entry names, is gone");
            }

            return table.Digest() == digest ? RecordedTable.Read(table) : RecordedTable.Unreadable($"'{file}' holds a table of another digest, {table.Digest()}");
        });

    /// <summary>
    /// What the record holds of <paramref name="arms"/> on <paramref name="leg"/>, as a sweep under <c>--pending</c> weighs
    /// them: every entry of each, and each table the newest of them names - or why that table, or the whole record,
    /// cannot be read.
    /// </summary>
    /// <param name="leg">The leg.</param>
    /// <param name="arms">The arms the sweep would drive on it, as the registry names them.</param>
    /// <exception cref="Results.HarnessException">Another process held the leg's record past the window.</exception>
    public RecordedArms ArmsOf(string leg, IEnumerable<string> arms)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leg);
        ArgumentNullException.ThrowIfNull(arms);

        var read = Read(leg);

        if (read.Unread is { } unread)
        {
            return RecordedArms.Unreadable(unread);
        }

        var entries = new Dictionary<string, IReadOnlyList<RecordedVerdict>>(StringComparer.OrdinalIgnoreCase);
        var tables = new SortedDictionary<string, BuiltFromTable>(StringComparer.Ordinal);
        var unreadTables = new SortedDictionary<string, string>(StringComparer.Ordinal);

        foreach (var arm in arms)
        {
            if (entries.ContainsKey(arm) || read.Arms.GetValueOrDefault(arm) is not { Count: > 0 } kept)
            {
                continue;
            }

            entries[arm] = kept;

            // Only the newest is ever compared part by part: an older entry is weighed by its key alone.
            foreach (var digest in kept[^1].JudgedOn?.Built.Values ?? [])
            {
                if (tables.ContainsKey(digest) || unreadTables.ContainsKey(digest))
                {
                    continue;
                }

                var held = Table(leg, digest);

                if (held.Table is { } table)
                {
                    tables[digest] = table;
                }
                else
                {
                    unreadTables[digest] = held.Unread!;
                }
            }
        }

        return new RecordedArms { Arms = entries, Tables = tables, TablesUnread = unreadTables };
    }

    /// <summary>
    /// Marks each entry of <paramref name="leg"/> whose arm and key <paramref name="keys"/> names as verified by
    /// <paramref name="by"/>, in one change of the record: the last sweep that found it holding on the leg's machine,
    /// appended to the leg's log as a line to each. An entry no longer there - another sweep replaced it meanwhile - is
    /// passed over as the log is read. Nothing is written where the record cannot be read, which is said instead.
    /// </summary>
    /// <param name="leg">The leg.</param>
    /// <param name="keys">The key that held of each arm, by the arm's id.</param>
    /// <param name="by">The sweep that found them holding, and when.</param>
    /// <returns>Why the record could not be read, and nothing was written; <see langword="null"/> otherwise.</returns>
    /// <exception cref="IOException">The record could not be written.</exception>
    /// <exception cref="UnauthorizedAccessException">The record could not be written.</exception>
    /// <exception cref="Results.HarnessException">Another process held the leg's record past the window.</exception>
    public RecordUnread? Verified(string leg, IReadOnlyDictionary<string, string> keys, RecordedRun by)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leg);
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(by);

        return Held(leg, () =>
        {
            if (Appendable(leg, out var log) is { } unread)
            {
                return unread;
            }

            Append(leg, log, [.. keys.Select(pair => new LogLine { Arm = pair.Key, Key = pair.Value, Verified = by })]);

            return null;
        });
    }

    /// <summary>
    /// Records <paramref name="verdict"/> of <paramref name="arm"/> on <paramref name="leg"/>: the tables it names written
    /// where absent, and the entry appended to the leg's log, with an index line of each table the log does not hold yet.
    /// Read back, it replaces the arm's entry on the same key, or with no key where it has none; every other entry stays.
    /// Nothing is written where the record cannot be read, which is said instead.
    /// </summary>
    /// <param name="leg">The leg.</param>
    /// <param name="arm">The arm, as the registry names it.</param>
    /// <param name="verdict">What it reached, and what it was judged on.</param>
    /// <param name="tables">The tables of the targets its build builds, as a worker's build read them.</param>
    /// <returns>Why the record could not be read, and nothing was written; <see langword="null"/> once the verdict is recorded.</returns>
    /// <exception cref="IOException">The record could not be written.</exception>
    /// <exception cref="UnauthorizedAccessException">The record could not be written.</exception>
    /// <exception cref="Results.HarnessException">Another process held the leg's record past the window.</exception>
    public RecordUnread? Merge(string leg, string arm, RecordedVerdict verdict, IReadOnlyList<BuiltFromTable> tables)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leg);
        ArgumentException.ThrowIfNullOrWhiteSpace(arm);
        ArgumentNullException.ThrowIfNull(verdict);
        ArgumentNullException.ThrowIfNull(tables);

        return Held(leg, () =>
        {
            if (Appendable(leg, out var log) is { } unread)
            {
                return unread;
            }

            var names = Names(verdict).ToHashSet(StringComparer.Ordinal);
            var given = new Dictionary<string, BuiltFromTable>(StringComparer.Ordinal);

            foreach (var table in tables)
            {
                if (names.Contains(table.Digest()))
                {
                    given.TryAdd(table.Digest(), table);
                }
            }

            // Each table before the entry that names it: once written, it is named by the time anything reads the entry.
            foreach (var (digest, table) in given)
            {
                var file = TableFile(leg, digest);

                if (!_fileSystem.FileExists(file))
                {
                    _fileSystem.CreateDirectory(TablesOf(leg));
                    JsonStateFile.Write(_fileSystem, file, table);
                }
            }

            List<LogLine> lines =
            [
                .. given.Where(pair => !log.Tables.Contains(pair.Key)).Select(pair => new LogLine { Table = pair.Key, Index = pair.Value.Index() }),
                new LogLine { Arm = arm, Entry = verdict },
            ];

            Append(leg, log, lines);

            return null;
        });
    }

    /// <summary>Runs <paramref name="work"/> with no other process inside a change of <paramref name="leg"/>'s record.</summary>
    private T Held<T>(string leg, Func<T> work) => MachineWideFile.Update(Path.GetFullPath(FileOf(leg)), MachineWideFile.Window, work);

    /// <summary>
    /// <paramref name="leg"/>'s log, made ready for a line, while its mutex is held: made where neither form of the record
    /// is there, made from the retired whole document where only that is, and otherwise read for whatever was appended
    /// since this record last read it - the whole of it the first time - so each change reads only what is new, and a log
    /// with a line this build cannot read is never written to. Why it cannot be read, or <see langword="null"/> with
    /// <paramref name="log"/> where a line may be appended.
    /// </summary>
    private RecordUnread? Appendable(string leg, out LogState log)
    {
        log = new LogState();
        var file = FileOf(leg);

        _fileSystem.CreateDirectory(Directory);

        if (!_fileSystem.FileExists(_layout.MutationRecordIgnoreFile))
        {
            _fileSystem.WriteAllTextAtomic(_layout.MutationRecordIgnoreFile, HarnessLayout.SelfIgnoreRule);
        }

        if (!_fileSystem.FileExists(file))
        {
            // The retired whole document, where one is there, read once and carried into the log whole: never written again.
            var document = ReadDocument(leg);

            if (document.Unread is { } unread)
            {
                _logs.Remove(leg);
                return unread;
            }

            var lines = new List<LogLine> { new() { Format = Format, Leg = leg } };

            lines.AddRange(document.Tables.Select(pair => new LogLine { Table = pair.Key, Index = pair.Value }));
            lines.AddRange(document.Arms.SelectMany(pair => pair.Value.Select(entry => new LogLine { Arm = pair.Key, Entry = entry })));
            _fileSystem.WriteAllTextAtomic(file, string.Concat(lines.Select(Line)));
            _logs.Remove(leg);
        }

        var known = _logs.GetValueOrDefault(leg);
        var length = _fileSystem.StandingOf(file)?.Length ?? 0;

        // A log shorter than this record last read it was replaced - deleted and begun again - and is read whole.
        log = known is not null && known.ReadTo <= length ? known : new LogState();

        if (Fold(leg, file, log, folded: null) is { } problem)
        {
            _logs.Remove(leg);
            return problem;
        }

        _logs[leg] = log;
        return null;
    }

    /// <summary>
    /// Appends <paramref name="lines"/> to <paramref name="leg"/>'s log, made ready by <see cref="Appendable"/>, each on the
    /// disk before the change ends; a line a writer that stopped part way left torn at the end is cut back first, so it
    /// never runs into the next.
    /// </summary>
    private void Append(string leg, LogState log, IReadOnlyList<LogLine> lines)
    {
        using var appended = _fileSystem.OpenForAppend(FileOf(leg));
        var end = appended.Length;

        if (end > 0)
        {
            Span<byte> last = stackalloc byte[1];
            appended.ReadExactlyAt(end - 1, last);

            if (last[0] != (byte)'\n')
            {
                end = log.ReadTo;
                appended.TruncateTo(end);
            }
        }

        appended.AppendFlushed(System.Text.Encoding.UTF8.GetBytes(string.Concat(lines.Select(Line))));

        log.ReadTo = appended.Length;

        foreach (var line in lines.Where(line => line.Table is not null))
        {
            log.Tables.Add(line.Table!);
        }
    }

    /// <summary>The digests of the tables <paramref name="verdict"/> names.</summary>
    private static IEnumerable<string> Names(RecordedVerdict verdict) => verdict.JudgedOn?.Built.Values ?? [];

    /// <summary>A line of the log as it is written: one object, then a line feed.</summary>
    private static string Line(LogLine line) => JsonSerializer.Serialize(line, JsonStateFile.LineOptions) + "\n";

    /// <summary>What <paramref name="leg"/>'s record holds, read while its mutex is held, or why it cannot be read.</summary>
    private LegVerdictRecord ReadHeld(string leg)
    {
        var file = FileOf(leg);

        if (!_fileSystem.FileExists(file))
        {
            return ReadDocument(leg);
        }

        var folded = new Folded();
        var read = Fold(leg, file, new LogState(), folded);

        return read is { } unread
            ? LegVerdictRecord.Unreadable(file, unread)
            : LegVerdictRecord.Read(
                file,
                folded.Arms.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<RecordedVerdict>)pair.Value, StringComparer.OrdinalIgnoreCase),
                folded.Tables);
    }

    /// <summary>
    /// Reads <paramref name="leg"/>'s log from where <paramref name="log"/> last read it to its end - a line torn at the
    /// end, by a writer that stopped part way, not yet read - noting each table index it holds in <paramref name="log"/>,
    /// and, where <paramref name="folded"/> is given, folding each line into it: an entry replacing its arm's entry on the
    /// same key, newest last, a verification marking the entry it names. Why it cannot be read, or <see langword="null"/>.
    /// </summary>
    private RecordUnread? Fold(string leg, string file, LogState log, Folded? folded)
    {
        using var stream = _fileSystem.OpenRead(file);

        // Read on from where this record last read it only while a line still ends there: a log deleted and begun again
        // since, by another process, is read whole.
        if (log.ReadTo > 0)
        {
            stream.Seek(log.ReadTo - 1, SeekOrigin.Begin);

            if (stream.ReadByte() != '\n')
            {
                log.ReadTo = 0;
                log.Tables.Clear();
                stream.Seek(0, SeekOrigin.Begin);
            }
        }

        var position = log.ReadTo;
        var line = new List<byte>();
        int read;

        while ((read = stream.ReadByte()) >= 0)
        {
            if (read != '\n')
            {
                line.Add((byte)read);
                continue;
            }

            var problem = Took(leg, log, folded, System.Text.Encoding.UTF8.GetString([.. line]), first: position == 0);

            if (problem is not null)
            {
                return problem;
            }

            position += line.Count + 1;
            line.Clear();
        }

        log.ReadTo = position;

        return position == 0 ? RecordUnread.Because("it holds no whole line saying its format") : null;
    }

    /// <summary>One line of the log, read: its kind refused where no build of this one's writes it, or taken.</summary>
    private RecordUnread? Took(string leg, LogState log, Folded? folded, string text, bool first)
    {
        LogLine line;

        try
        {
            line = JsonSerializer.Deserialize<LogLine>(text, LineReading) ?? throw new JsonException("it holds null");
        }
        catch (JsonException)
        {
            // Asked before the shape, which a newer format may have changed: its members are then no reason to call it unreadable.
            return first && NewerFormat(text) is { } newer ? RecordUnread.Newer(newer) : RecordUnread.Because(WrittenOtherwise);
        }

        if (first)
        {
            return line.Format is not { } format || line.Leg is null || line.Arm is not null || line.Entry is not null || line.Table is not null || line.Verified is not null
                ? RecordUnread.Because("its first line says no format")
                : format > Format ? RecordUnread.Newer(format)
                : format != Format ? RecordUnread.Because($"it says it is of format {format}, which no build of {ToolPackage.Id} writes")
                : !string.Equals(line.Leg, leg, StringComparison.OrdinalIgnoreCase) ? RecordUnread.Because($"it is the record of leg '{line.Leg}'")
                : null;
        }

        switch (line)
        {
            case { Arm: { } arm, Entry: { } entry, Table: null, Index: null, Key: null, Verified: null, Format: null, Leg: null }:
                if (entry.Key is not null && entry.JudgedOn is null)
                {
                    return RecordUnread.Because($"it holds an entry of arm '{arm}' with the key '{entry.Key}' and nothing it was judged on");
                }

                if (entry.JudgedOn is { } on && Damaged(arm, on) is { } damaged)
                {
                    return RecordUnread.Because(damaged);
                }

                if (folded is not null)
                {
                    // Kept under the spelling it was last recorded by.
                    var kept = folded.Arms.Remove(arm, out var entries) ? entries : [];

                    kept.RemoveAll(other => string.Equals(other.Key, entry.Key, StringComparison.Ordinal));
                    kept.Add(entry);
                    folded.Arms[arm] = kept;
                }

                return null;

            case { Table: { } digest, Index: { } index, Arm: null, Entry: null, Key: null, Verified: null, Format: null, Leg: null }:
                log.Tables.Add(digest);

                if (folded is not null)
                {
                    folded.Tables[digest] = index;
                }

                return null;

            case { Arm: { } marked, Key: { } key, Verified: { } by, Entry: null, Table: null, Index: null, Format: null, Leg: null }:
                if (folded?.Arms.GetValueOrDefault(marked) is { } named)
                {
                    var at = named.FindIndex(entry => string.Equals(entry.Key, key, StringComparison.Ordinal));

                    if (at >= 0)
                    {
                        named[at] = named[at] with { Verified = by };
                    }
                }

                return null;

            default:
                return RecordUnread.Because(WrittenOtherwise);
        }
    }

    /// <summary>
    /// What <paramref name="leg"/>'s record held in the retired whole document (<see cref="DocumentOf"/>), which an earlier
    /// build wrote and this one reads, once, to begin the log from - or why it cannot be read. Nothing where it is not there.
    /// </summary>
    private LegVerdictRecord ReadDocument(string leg)
    {
        var file = DocumentOf(leg);
        var none = LegVerdictRecord.Read(
            file,
            new Dictionary<string, IReadOnlyList<RecordedVerdict>>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, TableIndex>(StringComparer.Ordinal));

        if (!MachineWideFile.TryRead<JsonNode>(_fileSystem, file, out var node, out var unreadable))
        {
            return LegVerdictRecord.Unreadable(
                file,
                unreadable is JsonException ? RecordUnread.Because(WrittenOtherwise) : RecordUnread.Because(unreadable.Message));
        }

        if (node is null)
        {
            return none;
        }

        // Asked before the shape, which another format may have changed: its members are then no reason to call it what no
        // build writes. No build writes the whole document in any format but its one - a newer build writes the log - so
        // one of another is never said to be newer.
        if (node is JsonObject { } written
            && written["format"] is JsonValue declared
            && declared.TryGetValue<int>(out var format)
            && format != DocumentFormat)
        {
            return LegVerdictRecord.Unreadable(file, RecordUnread.Because($"it says it is of format {format}, which no build of {ToolPackage.Id} writes"));
        }

        Document document;

        try
        {
            document = node.Deserialize<Document>(DocumentReading) ?? throw new JsonException("it holds null");
        }
        catch (JsonException)
        {
            return LegVerdictRecord.Unreadable(file, RecordUnread.Because(WrittenOtherwise));
        }

        if (document.Format != DocumentFormat)
        {
            return LegVerdictRecord.Unreadable(file, RecordUnread.Because($"it says it is of format {document.Format}, which no build of {ToolPackage.Id} writes"));
        }

        if (!string.Equals(document.Leg, leg, StringComparison.OrdinalIgnoreCase))
        {
            return LegVerdictRecord.Unreadable(file, RecordUnread.Because($"it is the record of leg '{document.Leg}'"));
        }

        // What only this build's own hand keeps out: a null where entries or tables go, an entry with a key and nothing
        // it was judged on, or a null among the values of a map it was judged on where no null goes, would otherwise
        // reach every comparison of it as a crash. Refused here, once, as unreadable; a null item of a list never loads.
        foreach (var (arm, entries) in document.Arms)
        {
            if (entries is null)
            {
                return LegVerdictRecord.Unreadable(file, RecordUnread.Because($"it holds no list of entries for arm '{arm}'"));
            }

            foreach (var entry in entries.Where(entry => entry.Key is not null && entry.JudgedOn is null))
            {
                return LegVerdictRecord.Unreadable(
                    file,
                    RecordUnread.Because($"it holds an entry of arm '{arm}' with the key '{entry.Key}' and nothing it was judged on"));
            }

            foreach (var entry in entries)
            {
                if (entry.JudgedOn is { } on && Damaged(arm, on) is { } damaged)
                {
                    return LegVerdictRecord.Unreadable(file, RecordUnread.Because(damaged));
                }
            }
        }

        foreach (var (digest, _) in document.Tables.Where(pair => pair.Value is null))
        {
            return LegVerdictRecord.Unreadable(file, RecordUnread.Because($"it holds no table index for '{digest}'"));
        }

        var arms = new Dictionary<string, IReadOnlyList<RecordedVerdict>>(StringComparer.OrdinalIgnoreCase);

        foreach (var (arm, entries) in document.Arms)
        {
            if (!arms.TryAdd(arm, entries))
            {
                return LegVerdictRecord.Unreadable(file, RecordUnread.Because($"it records arm '{arm}' twice, under two spellings that differ only in case"));
            }
        }

        return LegVerdictRecord.Read(file, arms, new Dictionary<string, TableIndex>(document.Tables, StringComparer.Ordinal));
    }

    /// <summary>
    /// Why a hand-damaged <paramref name="on"/> cannot be read - a null among the values of one of its maps, which the
    /// strict reader lets load and no comparison can answer - or <see langword="null"/> where each is as this build
    /// writes it. A member of its own turned to null, or an item of its list of compilers, never reaches here: the reader
    /// refuses it as it reads.
    /// </summary>
    private static string? Damaged(string arm, JudgedOn on)
        => on.Built.FirstOrDefault(built => built.Value is null) is { Key: { } target } ? $"it holds an entry of arm '{arm}' whose target '{target}' is named by no digest"
        : on.RunReads.FirstOrDefault(read => read.Value is null) is { Key: { } named } ? $"it holds an entry of arm '{arm}' whose run-time read '{named}' is named by no hash"
        : null;

    /// <summary>Why a record this build cannot read says it cannot be, in this tool's words, wherever a shape is to blame.</summary>
    private static string WrittenOtherwise => $"it holds what no build of {ToolPackage.Id} writes - another build wrote it, or a hand changed it";

    /// <summary>The format a first line declares where it is newer than this build's, whatever else it holds; otherwise null.</summary>
    private static int? NewerFormat(string text)
    {
        try
        {
            return JsonDepth.ParseNode(text) is JsonObject { } written
                && written["format"] is JsonValue declared && declared.TryGetValue<int>(out var format) && format > Format
                ? format
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>How far this record has read a leg's log, and the table indexes it holds so far.</summary>
    private sealed class LogState
    {
        /// <summary>The byte the next unread line begins at.</summary>
        public long ReadTo { get; set; }

        /// <summary>The digests of the tables the log holds an index of.</summary>
        public HashSet<string> Tables { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>A log read whole: each arm's entries, one to a key, newest last, and each table's index.</summary>
    private sealed class Folded
    {
        public Dictionary<string, List<RecordedVerdict>> Arms { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, TableIndex> Tables { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>
    /// A line of a leg's log, of one of four kinds: the first, saying its <see cref="Format"/> and <see cref="Leg"/>; an
    /// entry (<see cref="Arm"/>, <see cref="Entry"/>); a table's index (<see cref="Table"/>, <see cref="Index"/>); or a
    /// verification (<see cref="Arm"/>, <see cref="Key"/>, <see cref="Verified"/>).
    /// </summary>
    private sealed record LogLine
    {
        public int? Format { get; init; }

        public string? Leg { get; init; }

        public string? Arm { get; init; }

        public RecordedVerdict? Entry { get; init; }

        public string? Table { get; init; }

        public TableIndex? Index { get; init; }

        public string? Key { get; init; }

        public RecordedRun? Verified { get; init; }
    }

    /// <summary>A leg's record as the retired whole document held it.</summary>
    private sealed record Document
    {
        /// <summary>The format it is written in.</summary>
        public required int Format { get; init; }

        /// <summary>The leg, as the configuration names it.</summary>
        public required string Leg { get; init; }

        /// <summary>Each arm's entries, by its id.</summary>
        public required IDictionary<string, List<RecordedVerdict>> Arms { get; init; }

        /// <summary>Each table an entry names, by its digest.</summary>
        public required IDictionary<string, TableIndex> Tables { get; init; }
    }
}
