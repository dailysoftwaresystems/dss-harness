using System.Text.Json;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Repository;

namespace DssHarness.Core.Execution;

/// <summary>
/// What a unit of the dispatch queue is expected to cost, in minutes: the median of the leg's last ten durations - the
/// newest, by the run each was of - that the records the tool already keeps hold for it, or one quantum where they hold
/// none. The records are the ledgers a flow's steps keep of the legs they ran (<see cref="OrchestratorLayout.StepLedgerFileName"/>),
/// in every run of the tree's orchestrators, a step of the run's own and a step of each of its agents alike, each read by
/// the ledger's own reader (<see cref="LedgerReport.DurationsIn"/>), so a duration is the one the ledger wrote. Read from
/// the tree the unit works in, never beyond it: the estimate a tenant's burst is charged by is its own history and no
/// other repository's.
/// </summary>
/// <remarks>
/// A leg a ledger holds that never ran has no duration, and a ledger that cannot be read as one holds none: the estimate is
/// what the queue orders by, never a verdict, so a record nobody can read leaves the leg one quantum the less known, and
/// the unit is still admitted.
/// </remarks>
public static class DispatchCosts
{
    /// <summary>How many of the leg's newest durations the estimate is the median of: ten, as the plan sets it (part 06, "the median of the last ten durations").</summary>
    public const int Read = 10;

    /// <summary>
    /// The median duration, in minutes, of the newest <see cref="Read"/> durations the ledgers in <paramref name="tree"/>
    /// hold for the leg <paramref name="leg"/> - or one quantum where they hold none.
    /// </summary>
    /// <param name="fileSystem">Reads the ledgers.</param>
    /// <param name="tree">The tree the leg works in, whose orchestrators' records hold its history.</param>
    /// <param name="leg">The leg, as the ledger names it.</param>
    public static double Of(IFileSystem fileSystem, string tree, string leg)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentException.ThrowIfNullOrWhiteSpace(tree);
        ArgumentException.ThrowIfNullOrWhiteSpace(leg);

        var durations = new List<(string Run, double Minutes)>();
        var orchestrators = Path.Combine(tree, HarnessLayout.OrchestratorsDirectoryName);

        if (fileSystem.DirectoryExists(orchestrators))
        {
            foreach (var steps in fileSystem.EnumerateDirectories(orchestrators).SelectMany(run => StepsOf(fileSystem, run)))
            {
                foreach (var ledger in fileSystem.EnumerateFiles(steps, recursive: true).Where(file => string.Equals(Path.GetFileName(file), OrchestratorLayout.StepLedgerFileName, StringComparison.Ordinal)))
                {
                    if (Durations(fileSystem, ledger) is not { } read)
                    {
                        continue;
                    }

                    foreach (var line in read.Legs ?? [])
                    {
                        if (line is { Leg: { } named, DurationSeconds: > 0 } && string.Equals(named, leg, StringComparison.Ordinal))
                        {
                            durations.Add((read.RunId ?? string.Empty, line.DurationSeconds / 60));
                        }
                    }
                }
            }
        }

        if (durations.Count == 0)
        {
            return 1;
        }

        // The newest by the run each was of - a run's id begins with when it began, so its order is the runs' - and of
        // those, the middle one by length.
        var newest = durations
            .OrderByDescending(duration => duration.Run, StringComparer.Ordinal)
            .Take(Read)
            .Select(duration => duration.Minutes)
            .Order()
            .ToList();

        return newest.Count % 2 == 1
            ? newest[newest.Count / 2]
            : (newest[(newest.Count / 2) - 1] + newest[newest.Count / 2]) / 2;
    }

    /// <summary>The step directories of one run: its own steps', and each of its agents' steps'.</summary>
    private static IEnumerable<string> StepsOf(IFileSystem fileSystem, string run)
    {
        var own = Path.Combine(run, OrchestratorLayout.StepsDirectoryName);

        if (fileSystem.DirectoryExists(own))
        {
            yield return own;
        }

        var agents = Path.Combine(run, OrchestratorLayout.AgentsDirectoryName);

        if (!fileSystem.DirectoryExists(agents))
        {
            yield break;
        }

        foreach (var agent in fileSystem.EnumerateDirectories(agents))
        {
            var steps = Path.Combine(agent, OrchestratorLayout.StepsDirectoryName);

            if (fileSystem.DirectoryExists(steps))
            {
                yield return steps;
            }
        }
    }

    /// <summary>What one ledger says of its legs' durations; null where it cannot be read as a ledger.</summary>
    private static LedgerDurations? Durations(IFileSystem fileSystem, string ledger)
    {
        try
        {
            return fileSystem.ReadAllText(ledger) is { Length: > 0 } text ? LedgerReport.DurationsIn(text) : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
