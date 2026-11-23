using System.Text.Json.Nodes;
using DssHarness.Core.FileSystem;

namespace DssHarness.Core.Execution;

/// <summary>
/// What a unit of the dispatch queue is expected to cost, in minutes: the median of the last ten durations the
/// records the tool already keeps hold for the same leg - a flow's step ledgers, each an entry per leg with its whole
/// duration - or one quantum where nothing is known. Read from the tree the unit works in, never beyond it: the
/// estimate a tenant's burst is charged by is its own history and no other repository's.
/// </summary>
public static class DispatchCosts
{
    /// <summary>How many past durations the estimate reads at most.</summary>
    public const int Read = 10;

    /// <summary>
    /// The median duration, in minutes, of the last <see cref="Read"/> records <paramref name="tree"/> holds for the leg
    /// <paramref name="leg"/> - or one quantum where the tree holds none.
    /// </summary>
    /// <param name="fileSystem">Reads the ledgers.</param>
    /// <param name="tree">The tree the leg works in, whose orchestrators' records hold its history.</param>
    /// <param name="leg">The leg, as the ledger names it.</param>
    public static double Of(IFileSystem fileSystem, string tree, string leg)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tree);
        ArgumentException.ThrowIfNullOrWhiteSpace(leg);

        var orchestrators = Path.Combine(tree, ".orchestrators");
        var durations = new List<double>();

        if (fileSystem.DirectoryExists(orchestrators))
        {
            foreach (var run in fileSystem.EnumerateDirectories(orchestrators).Take(20))
            {
                var steps = Path.Combine(run, "steps");

                if (!fileSystem.DirectoryExists(steps))
                {
                    continue;
                }

                foreach (var ledger in fileSystem.EnumerateFiles(steps, recursive: true).Where(file => string.Equals(Path.GetFileName(file), "ledger.json", StringComparison.OrdinalIgnoreCase)))
                {
                    if (fileSystem.ReadAllText(ledger) is not { Length: > 0 } text
                        || JsonNode.Parse(text)?["legs"]?.AsArray() is not { } legs)
                    {
                        continue;
                    }

                    foreach (var one in legs)
                    {
                        if (one?["leg"]?.GetValue<string>() is { } named
                            && string.Equals(named, leg, StringComparison.Ordinal)
                            && one["duration"]?.GetValue<string>() is { } said
                            && TimeSpan.TryParse(said, System.Globalization.CultureInfo.InvariantCulture, out var took))
                        {
                            durations.Add(took.TotalMinutes);
                        }
                    }
                }
            }
        }

        if (durations.Count == 0)
        {
            return 1;
        }

        var ordered = durations.Order().TakeLast(Read).ToList();

        return ordered.Count % 2 == 1
            ? ordered[ordered.Count / 2]
            : (ordered[(ordered.Count / 2) - 1] + ordered[ordered.Count / 2]) / 2;
    }
}
