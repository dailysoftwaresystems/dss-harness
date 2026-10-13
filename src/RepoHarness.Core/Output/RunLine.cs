using System.Diagnostics.CodeAnalysis;
using RepoHarness.Core.Execution;

namespace RepoHarness.Core.Output;

/// <summary>
/// How a run is spelt in a line, said once for everything that names one and for the one reader that has to find a
/// command's own run line again.
/// </summary>
/// <remarks>
/// A command says its run in its first line, and every sentence that names a run after that - who holds a lock, a
/// slot, a directory of records - spells it the same way, so what a command printed can be searched for a run and
/// every line about it found. A host runs a leg as a command of its own, under a run of its own, and the machine
/// that dispatched the leg reads that command's run line back out of what the host wrote: the writing and the
/// reading are here so they cannot drift apart.
/// </remarks>
public static class RunLine
{
    /// <summary>How a line names <paramref name="run"/>: <c>run 20261012-150233-1a2b3c4d</c>.</summary>
    /// <param name="run">The run.</param>
    public static string Message(RunId run)
    {
        ArgumentNullException.ThrowIfNull(run);

        return Message(run.Value);
    }

    /// <summary>How a line names the run a record keeps as <paramref name="runId"/>, as <see cref="Message(RunId)"/> names one in hand.</summary>
    /// <param name="runId">The run's id, as the record wrote it.</param>
    public static string Message(string runId) => $"run {runId}";

    /// <summary>
    /// Reads <paramref name="line"/> as the line <paramref name="command"/> says its own run in: the command, then
    /// <see cref="Message(RunId)"/>, and nothing after it.
    /// </summary>
    /// <param name="line">A line a command wrote.</param>
    /// <param name="command">The command whose run line is looked for.</param>
    /// <param name="runId">The run the line names, when it is one.</param>
    public static bool TryRead(string line, string command, [NotNullWhen(true)] out RunId? runId)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentException.ThrowIfNullOrWhiteSpace(command);

        // As a command's output writes any line of its own: the command, then what it says.
        var prefix = $"{command}: {Message(string.Empty)}";

        runId = line.StartsWith(prefix, StringComparison.Ordinal) && RunId.TryParse(line[prefix.Length..], out var read) ? read : null;
        return runId is not null;
    }
}
