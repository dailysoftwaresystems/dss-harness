using System.Text.Json;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Hosts;
using RepoHarness.Core.Output;
using RepoHarness.Core.Results;

namespace RepoHarness.Core.Mutations;

/// <summary>
/// Where a sweep hands each arm's verdict as the arm ends, to be kept with what it was judged on: each verdict reached by
/// observing the arm, and each arm stopped because its runner's unmutated control did not pass. Never an arm its machine
/// did not admit, one the sweep's own stop or a refusal of the run ended, one no worker reached, or one skipped: nothing
/// was observed of those. A self-test has none: nothing of it is kept.
/// </summary>
internal interface IArmRecordSink
{
    /// <summary>Keeps <paramref name="verdict"/> of <paramref name="arm"/>, with the tables it names.</summary>
    /// <param name="arm">The arm, as the registry names it.</param>
    /// <param name="verdict">What it reached, and what it was judged on.</param>
    /// <param name="tables">The tables of the targets its build builds, as a worker's build read them.</param>
    void Judged(string arm, RecordedVerdict verdict, IReadOnlyList<BuiltFromTable> tables);

    /// <summary>
    /// Keeps that each arm <paramref name="keys"/> names held on the key it names, found so by <paramref name="by"/>: what
    /// a sweep under <c>--pending</c> verified, once, for every arm it held.
    /// </summary>
    /// <param name="keys">The key that held of each arm, by the arm's id.</param>
    /// <param name="by">The sweep that found them holding, and when.</param>
    void Verified(IReadOnlyDictionary<string, string> keys, RecordedRun by);

    /// <summary>What the leg's line says of the verdicts that were not kept, or <see langword="null"/> where each handed was.</summary>
    string? Unrecorded { get; }
}

/// <summary>
/// Keeps each verdict a sweep of a leg on this machine hands it in the record of verdicts (<see cref="ArmVerdictRecord"/>),
/// as the arm ends: so a sweep stopped part way keeps what it had judged.
/// </summary>
/// <remarks>
/// A record that cannot be read, or that a newer build wrote, is said once as a warning and once on the leg's line, and
/// the sweep records nothing more; so is one that cannot be written. Either is the record's, never the arm's: its verdict
/// stands as it was reached.
/// </remarks>
/// <param name="record">The record of verdicts.</param>
/// <param name="leg">The leg swept.</param>
/// <param name="output">Says the warning.</param>
/// <param name="commandName">The command, as a warning names it.</param>
internal sealed class RecordSink(ArmVerdictRecord record, string leg, IHarnessOutput output, string commandName) : IArmRecordSink
{
    private readonly ArmVerdictRecord _record = record;
    private readonly string _leg = leg;
    private readonly IHarnessOutput _output = output;
    private readonly string _commandName = commandName;
    private readonly Lock _gate = new();
    private string? _unrecorded;

    /// <inheritdoc/>
    public string? Unrecorded
    {
        get
        {
            lock (_gate)
            {
                return _unrecorded;
            }
        }
    }

    /// <inheritdoc/>
    public void Judged(string arm, RecordedVerdict verdict, IReadOnlyList<BuiltFromTable> tables)
    {
        lock (_gate)
        {
            if (_unrecorded is not null)
            {
                return;
            }

            var file = _record.FileOf(_leg);

            try
            {
                if (_record.Merge(_leg, arm, verdict, tables) is { } unread)
                {
                    _output.Warn(_commandName, unread.Warning(file, _leg));
                    _unrecorded = unread.Note(file);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HarnessException)
            {
                var why = ex.Message.TrimEnd('.');

                _output.Warn(_commandName, $"'{file}' could not be written, so this sweep records no verdict of '{_leg}' from arm '{arm}' on: {why}");
                _unrecorded = $"its verdicts from arm '{arm}' on were not recorded: '{file}' could not be written: {why}";
            }
        }
    }

    /// <inheritdoc/>
    public void Verified(IReadOnlyDictionary<string, string> keys, RecordedRun by)
    {
        lock (_gate)
        {
            if (_unrecorded is not null || keys.Count == 0)
            {
                return;
            }

            var file = _record.FileOf(_leg);

            try
            {
                if (_record.Verified(_leg, keys, by) is { } unread)
                {
                    _output.Warn(_commandName, unread.Warning(file, _leg));
                    _unrecorded = unread.Note(file);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HarnessException)
            {
                var why = ex.Message.TrimEnd('.');

                _output.Warn(_commandName, $"'{file}' could not be written, so this sweep records nothing of '{_leg}', not even which passed it found holding: {why}");
                _unrecorded = $"none of its verdicts was recorded, nor which passed held: '{file}' could not be written: {why}";
            }
        }
    }
}

/// <summary>
/// Keeps each verdict a leg swept on a host hands it as notes to the machine that dispatched the leg
/// (<see cref="HostNotes"/>), as the arm ends: that machine's own record-writing sink is fed by them as they arrive, so a
/// sweep stopped part way, or cut off, keeps on the dispatcher what it had judged.
/// </summary>
/// <remarks>
/// The tables an entry names are written before it, as <see cref="RecordSink"/> writes them, so the reader has each the
/// entry names by the time it reads the entry. Notes are the host's own channel: nothing of them is the arm's verdict,
/// which stands as it was reached.
/// </remarks>
internal sealed class NoteSink(HostNotes notes) : IArmRecordSink
{
    private readonly HostNotes _notes = notes;

    /// <inheritdoc/>
    public string? Unrecorded => null;

    /// <inheritdoc/>
    public void Judged(string arm, RecordedVerdict verdict, IReadOnlyList<BuiltFromTable> tables)
    {
        foreach (var table in tables)
        {
            _notes.Write(TableKind, JsonSerializer.Serialize(table, JsonStateFile.LineOptions));
        }

        _notes.Write(ArmKind, JsonSerializer.Serialize(new NotedArm(arm, verdict), JsonStateFile.LineOptions));
    }

    /// <inheritdoc/>
    public void Verified(IReadOnlyDictionary<string, string> keys, RecordedRun by)
        => _notes.Write(VerifiedKind, JsonSerializer.Serialize(new NotedVerified(keys, by), JsonStateFile.LineOptions));

    /// <summary>The kind of a note carrying a table an entry names.</summary>
    public const string TableKind = "table";

    /// <summary>The kind of a note carrying an arm's verdict, as the arm ended.</summary>
    public const string ArmKind = "arm";

    /// <summary>The kind of a note carrying what a sweep under <c>--pending</c> found holding.</summary>
    public const string VerifiedKind = "verified";
}

/// <summary>An arm's verdict, as a note carries it to the machine that dispatched the leg (<see cref="NoteSink.ArmKind"/>).</summary>
/// <param name="Arm">The arm, as the registry names it.</param>
/// <param name="Verdict">What it reached, and what it was judged on.</param>
internal sealed record NotedArm(string Arm, RecordedVerdict Verdict);

/// <summary>What a sweep under <c>--pending</c> found holding, as a note carries it (<see cref="NoteSink.VerifiedKind"/>).</summary>
/// <param name="Keys">The key that held of each arm, by the arm's id.</param>
/// <param name="By">The sweep that found them holding, and when.</param>
internal sealed record NotedVerified(IReadOnlyDictionary<string, string> Keys, RecordedRun By);
