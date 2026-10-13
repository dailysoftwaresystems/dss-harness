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
