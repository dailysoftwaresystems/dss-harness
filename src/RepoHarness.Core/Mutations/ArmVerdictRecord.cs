using System.Text.Json;
using System.Text.Json.Nodes;
using RepoHarness.Core.Execution;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Hosts;
using RepoHarness.Core.Repository;

namespace RepoHarness.Core.Mutations;

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
}

/// <summary>Why a leg's record of verdicts cannot be read: what reading it said, or the newer format it was written in.</summary>
public sealed record RecordUnread
{
    private RecordUnread(string? why, int? format)
    {
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
/// <param name="File">The leg's record.</param>
/// <param name="Arms">
/// Each arm's entries, by its id, compared ignoring case: one to a key, in the order recorded, the newest last.
/// </param>
/// <param name="Tables">Each table an entry names, by its digest.</param>
/// <param name="Unread">Why the record cannot be read, or <see langword="null"/> where it was read or is not there.</param>
public sealed record LegVerdictRecord(
    string File,
    IReadOnlyDictionary<string, IReadOnlyList<RecordedVerdict>> Arms,
    IReadOnlyDictionary<string, TableIndex> Tables,
    RecordUnread? Unread);

/// <summary>
/// Each leg's recorded verdicts of its mutation arms, with what each was judged on, in the main checkout's harness
/// directory (<see cref="HarnessLayout.MutationRecordDirectory"/>): one file to a leg, and beside them each table an entry
/// names, written once under its digest.
/// </summary>
/// <remarks>
/// <para>
/// One file to a leg: a sweep of one leg never rewrites another's verdicts, two legs' sweeps never wait on one file, and a
/// file that cannot be read loses one leg. Each change is one read, merge and write under the file's machine-wide mutex,
/// entries merged by arm and key, so two sweeps recording at once - of one leg, from two trees - lose neither's verdicts,
/// whatever their order.
/// </para>
/// <para>
/// An arm keeps one entry to a key, at most <see cref="KeptPerArm"/>, in the order recorded: a later verdict on the same
/// key replaces the entry, whatever either was, and the oldest by position goes first - never by when, as a host's clock
/// may step. A table is written before the first entry naming it, and removed once no entry names it, under the same
/// mutex: a table is never written ahead of an entry that names it, so two sweeps cannot remove each other's.
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
    /// <summary>The format this build writes and reads.</summary>
    public const int Format = 1;

    /// <summary>How many entries an arm keeps on a leg: one to a key.</summary>
    public const int KeptPerArm = 8;

    /// <summary>The directory, in the record's, that holds a directory of tables for each leg.</summary>
    public const string TablesDirectoryName = "built";

    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly HarnessLayout _layout = layout;

    /// <summary>The record's directory.</summary>
    public string Directory => _layout.MutationRecordDirectory;

    /// <summary>Where <paramref name="leg"/>'s verdicts are recorded.</summary>
    /// <param name="leg">The leg, as the configuration names it.</param>
    public string FileOf(string leg) => Path.Combine(Directory, leg + ".json");

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
    /// </summary>
    /// <param name="leg">The leg.</param>
    /// <param name="digest">The table's digest, as an entry names it.</param>
    public (BuiltFromTable? Table, string? Unread) Table(string leg, string digest)
    {
        var file = TableFile(leg, digest);

        if (!MachineWideFile.TryRead<BuiltFromTable>(_fileSystem, file, out var table, out var unreadable))
        {
            return (null, $"'{file}' cannot be read: {unreadable.Message.TrimEnd('.')}");
        }

        if (table is null)
        {
            return (null, $"'{file}', which an entry names, is gone");
        }

        return table.Digest() == digest ? (table, null) : (null, $"'{file}' holds a table of another digest, {table.Digest()}");
    }

    /// <summary>
    /// Records <paramref name="verdict"/> of <paramref name="arm"/> on <paramref name="leg"/>: the tables it names written
    /// where absent, the entry merged - replacing the arm's entry on the same key, or with no key where it has none - the
    /// entries beyond <see cref="KeptPerArm"/> dropped oldest first, the leg's file written whole, and every table no entry
    /// names any more removed. Nothing is written where the record cannot be read, which is said instead.
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
            var read = ReadHeld(leg);

            if (read.Unread is { } unread)
            {
                return unread;
            }

            var names = Names(verdict);
            var given = new Dictionary<string, BuiltFromTable>(StringComparer.Ordinal);

            foreach (var table in tables)
            {
                if (names.Contains(table.Digest()))
                {
                    given.TryAdd(table.Digest(), table);
                }
            }

            _fileSystem.CreateDirectory(Directory);

            if (!_fileSystem.FileExists(_layout.MutationRecordIgnoreFile))
            {
                _fileSystem.WriteAllTextAtomic(_layout.MutationRecordIgnoreFile, HarnessLayout.SelfIgnoreRule);
            }

            // Each table before the entry that names it: once written, it is named by the time anything could remove it.
            foreach (var (digest, table) in given)
            {
                var file = TableFile(leg, digest);

                if (!_fileSystem.FileExists(file))
                {
                    _fileSystem.CreateDirectory(TablesOf(leg));
                    _fileSystem.WriteAllTextAtomic(file, JsonSerializer.Serialize(table, JsonStateFile.Options));
                }
            }

            var arms = new Dictionary<string, IReadOnlyList<RecordedVerdict>>(read.Arms, StringComparer.OrdinalIgnoreCase);
            var kept = arms.GetValueOrDefault(arm) ?? [];

            // Kept under the spelling it was last recorded by.
            arms.Remove(arm);

            List<RecordedVerdict> entries = [.. kept.Where(entry => !string.Equals(entry.Key, verdict.Key, StringComparison.Ordinal)), verdict];

            arms[arm] = entries[Math.Max(0, entries.Count - KeptPerArm)..];

            var named = arms.Values.SelectMany(entry => entry).SelectMany(Names).ToHashSet(StringComparer.Ordinal);
            var index = new SortedDictionary<string, TableIndex>(StringComparer.Ordinal);

            foreach (var (digest, table) in read.Tables.Where(pair => named.Contains(pair.Key)))
            {
                index[digest] = table;
            }

            foreach (var (digest, table) in given.Where(pair => named.Contains(pair.Key)))
            {
                index[digest] = table.Index();
            }

            _fileSystem.WriteAllTextAtomic(
                FileOf(leg),
                JsonSerializer.Serialize(
                    new Document
                    {
                        Format = Format,
                        Leg = leg,
                        Arms = new SortedDictionary<string, List<RecordedVerdict>>(
                            arms.ToDictionary(pair => pair.Key, pair => pair.Value.ToList(), StringComparer.Ordinal),
                            StringComparer.Ordinal),
                        Tables = index,
                    },
                    JsonStateFile.Options));

            Prune(leg, named);

            return null;
        });
    }

    /// <summary>The digests of the tables <paramref name="verdict"/> names.</summary>
    private static IEnumerable<string> Names(RecordedVerdict verdict) => verdict.JudgedOn?.Built.Values ?? [];

    /// <summary>Runs <paramref name="work"/> with no other process inside a change of <paramref name="leg"/>'s record.</summary>
    private T Held<T>(string leg, Func<T> work) => MachineWideFile.Update(Path.GetFullPath(FileOf(leg)), MachineWideFile.Window, work);

    /// <summary>What <paramref name="leg"/>'s record holds, read while its mutex is held, or why it cannot be read.</summary>
    private LegVerdictRecord ReadHeld(string leg)
    {
        var file = FileOf(leg);
        var none = new LegVerdictRecord(
            file,
            new Dictionary<string, IReadOnlyList<RecordedVerdict>>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, TableIndex>(StringComparer.Ordinal),
            null);

        if (!MachineWideFile.TryRead<JsonNode>(_fileSystem, file, out var node, out var unreadable))
        {
            return none with { Unread = RecordUnread.Because(unreadable.Message) };
        }

        if (node is null)
        {
            return none;
        }

        // Asked before the shape, which a newer format may have changed: its members are then no reason to call it unreadable.
        if (node is JsonObject { } written
            && written["format"] is JsonValue declared
            && declared.TryGetValue<int>(out var format)
            && format > Format)
        {
            return none with { Unread = RecordUnread.Newer(format) };
        }

        Document document;

        try
        {
            document = node.Deserialize<Document>(JsonStateFile.Options) ?? throw new JsonException("it holds null");
        }
        catch (JsonException ex)
        {
            return none with { Unread = RecordUnread.Because(ex.Message) };
        }

        if (document.Format != Format)
        {
            return none with { Unread = RecordUnread.Because($"it says it is of format {document.Format}, which no build of {ToolPackage.Id} writes") };
        }

        if (!string.Equals(document.Leg, leg, StringComparison.OrdinalIgnoreCase))
        {
            return none with { Unread = RecordUnread.Because($"it is the record of leg '{document.Leg}'") };
        }

        var arms = new Dictionary<string, IReadOnlyList<RecordedVerdict>>(StringComparer.OrdinalIgnoreCase);

        foreach (var (arm, entries) in document.Arms)
        {
            if (!arms.TryAdd(arm, entries))
            {
                return none with { Unread = RecordUnread.Because($"it records arm '{arm}' twice, under two spellings that differ only in case") };
            }
        }

        return new LegVerdictRecord(file, arms, new Dictionary<string, TableIndex>(document.Tables, StringComparer.Ordinal), null);
    }

    /// <summary>
    /// Removes each table of <paramref name="leg"/> whose digest is not among <paramref name="named"/>. One that cannot be
    /// removed is left for the next change of the record to remove: a table no entry names is never read.
    /// </summary>
    private void Prune(string leg, IReadOnlySet<string> named)
    {
        var directory = TablesOf(leg);

        if (!_fileSystem.DirectoryExists(directory))
        {
            return;
        }

        foreach (var file in _fileSystem.EnumerateFiles(directory, recursive: false).ToList())
        {
            if (string.Equals(Path.GetExtension(file), ".json", StringComparison.Ordinal) && !named.Contains(Path.GetFileNameWithoutExtension(file)))
            {
                try
                {
                    _fileSystem.DeleteFile(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Named by nothing, so read by nothing: the next change of the record tries again.
                }
            }
        }
    }

    /// <summary>A leg's record as it is written.</summary>
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
