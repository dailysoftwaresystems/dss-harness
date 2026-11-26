using System.Text;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Output;
using DssHarness.Core.Results;

namespace DssHarness.Core.Execution;

/// <summary>How long each of a unit name's newest runs held its slot: one record of <see cref="UnitDurations"/>.</summary>
/// <param name="Repository">The repository its units were of, as its main checkout is spelt on this machine; empty where none was said.</param>
/// <param name="Command">The command that ran them: <c>build</c>, <c>test</c>, <c>run</c>, <c>check-mutations</c>.</param>
/// <param name="Unit">The unit's name: a leg, or a unit of a leg's work, <c>&lt;leg&gt;/&lt;unit&gt;</c> - an arm, a worker.</param>
/// <param name="Ran">Each run, the newest <see cref="UnitDurations.Kept"/>, in the order they ended.</param>
public sealed record UnitHistory(string Repository, string Command, string Unit, IReadOnlyList<UnitRan> Ran);

/// <summary>One run of a unit: when its work ended, and how long it held its slot.</summary>
/// <param name="EndedUtc">When its work ended and it gave its slot back.</param>
/// <param name="Seconds">How long it held its slot, from when it was let start to when its work ended.</param>
public sealed record UnitRan(DateTimeOffset EndedUtc, double Seconds);

/// <summary>
/// What a unit of the dispatch queue is expected to cost: the one owner of each unit name's history - how long each of its
/// newest runs held its slot, measured by the queue itself from when the unit was let start to when its work ended and it
/// gave its slot back, the same for every command, on every machine that admits (an ssh host its own, this machine the
/// legs it sends to WSL), whether a flow or a command typed ran it. Keyed by the repository, the command and the unit's
/// name - a leg, an arm of a sweep or a worker it makes, and a sweep a WSL distribution runs, taken whole - so a build
/// and a test of one leg, or two arms of one sweep, are never one history. One small record per name, beside the slots'
/// record, read whole at one look as a unit of that name asks: the median of its newest <see cref="Kept"/> runs, or one
/// quantum where it has run none.
/// </summary>
/// <remarks>
/// The time a unit held its slot is what the queue charges, so it is what is kept: never the time it waited first, which
/// grows with the machine's contention and would charge a tenant more the longer it was kept waiting. A record that cannot
/// be read is said, and the unit is charged one quantum: the estimate orders the queue, and is never a reason to refuse a
/// leg. A run that cannot be kept is said, and fails nothing.
/// </remarks>
/// <param name="fileSystem">Reads and writes the records.</param>
/// <param name="output">Says a record that could not be read or written.</param>
/// <param name="directory">The directory the records are kept in.</param>
public sealed class UnitDurations(IFileSystem fileSystem, IHarnessOutput output, string directory)
{
    /// <summary>How many of a unit name's newest runs its cost is the median of: ten, as plan 2.0 part 06 sets it ("the median of the last ten durations").</summary>
    public const int Kept = 10;

    /// <summary>What a unit costs, in minutes, where its name has no run kept: one quantum, which a round credits a tenant of weight and priority 1.</summary>
    public const double Quantum = 1;

    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly IHarnessOutput _output = output;
    private readonly string _directory = directory;

    /// <summary>
    /// What a unit named <paramref name="unit"/> of <paramref name="command"/> in <paramref name="repository"/> is expected
    /// to cost, in minutes: the median of the newest runs its history keeps, or <see cref="Quantum"/> where it keeps none or
    /// cannot be read, which is said.
    /// </summary>
    /// <param name="repository">The repository, as its main checkout is spelt on this machine; <see langword="null"/> where none was said.</param>
    /// <param name="command">The command running it.</param>
    /// <param name="unit">The unit's name.</param>
    public double CostOf(string? repository, string command, string unit)
    {
        var key = new UnitHistory(repository ?? string.Empty, command, unit, []);
        var path = PathOf(key);

        if (!MachineWideFile.TryRead<UnitHistory>(_fileSystem, path, out var history, out var unread))
        {
            _output.Warn(MachineDispatchQueue.CommandName, $"The record of how long '{unit}' held its slot, '{path}', could not be read: {unread.Message.TrimEnd('.')}. It is charged one quantum.");
            return Quantum;
        }

        return history is not null && Names(history, key) && history.Ran.Count > 0
            ? LedgerReport.Median(history.Ran.Select(ran => TimeSpan.FromSeconds(ran.Seconds))).TotalMinutes
            : Quantum;
    }

    /// <summary>
    /// Keeps that a unit named <paramref name="unit"/> of <paramref name="command"/> in <paramref name="repository"/> held its
    /// slot for <paramref name="held"/>, ending at <paramref name="endedUtc"/>: its history's newest <see cref="Kept"/> runs,
    /// by when each ended, written whole under its own machine-wide step, so two units of one name ending together both
    /// count. One that cannot be kept is said, and fails nothing.
    /// </summary>
    /// <param name="repository">The repository, as its main checkout is spelt on this machine; <see langword="null"/> where none was said.</param>
    /// <param name="command">The command running it.</param>
    /// <param name="unit">The unit's name.</param>
    /// <param name="held">How long it held its slot.</param>
    /// <param name="endedUtc">When its work ended.</param>
    public void Record(string? repository, string command, string unit, TimeSpan held, DateTimeOffset endedUtc)
    {
        var key = new UnitHistory(repository ?? string.Empty, command, unit, []);
        var path = PathOf(key);

        try
        {
            MachineWideFile.Update(path, MachineWideFile.Window, () =>
            {
                var kept = MachineWideFile.TryRead<UnitHistory>(_fileSystem, path, out var history, out _) && history is not null && Names(history, key)
                    ? history.Ran
                    : [];

                var written = key with
                {
                    Ran = [.. kept.Append(new UnitRan(endedUtc, held.TotalSeconds)).OrderBy(ran => ran.EndedUtc).TakeLast(Kept)],
                };

                MachineWideFile.Written($"The record of how long '{unit}' held its slot '{path}'", "Its next unit is charged by the runs kept before.", () =>
                {
                    _fileSystem.CreateDirectory(_directory);
                    JsonStateFile.Write(_fileSystem, path, written);
                });

                return 0;
            });
        }
        catch (HarnessException ex)
        {
            _output.Warn(MachineDispatchQueue.CommandName, ex.Message);
        }
    }

    /// <summary>The record of <paramref name="key"/>'s name, named by what its repository, command and unit hash to.</summary>
    private string PathOf(UnitHistory key)
        => Path.Combine(_directory, FileContentHash.Of(Encoding.UTF8.GetBytes($"{key.Repository}\n{key.Command}\n{key.Unit}")) + ".json");

    /// <summary>Whether <paramref name="history"/> is <paramref name="key"/>'s: a record another name's hash shared would never be read as its.</summary>
    private static bool Names(UnitHistory history, UnitHistory key)
        => string.Equals(history.Repository, key.Repository, StringComparison.Ordinal)
            && string.Equals(history.Command, key.Command, StringComparison.Ordinal)
            && string.Equals(history.Unit, key.Unit, StringComparison.Ordinal);
}
