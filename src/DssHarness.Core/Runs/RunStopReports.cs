using System.Text.Json.Nodes;
using DssHarness.Core.Output;
using DssHarness.Core.Results;

namespace DssHarness.Core.Runs;

/// <summary>What <c>stop-run</c> says of the run it was asked to stop: the run, its dispatcher, whether it stopped, the stop record it names.</summary>
/// <param name="Run">The run's id.</param>
/// <param name="RunDirectory">Its records.</param>
public sealed record RunStopReport(string Run, string RunDirectory)
{
    /// <summary>Its dispatcher, as a line names it, where it has one.</summary>
    public string? Dispatcher { get; init; }

    /// <summary>The stop record the command names: the request it wrote, or the run's record that its dispatcher took it.</summary>
    public string? Record { get; init; }

    /// <summary>Whether the run was stopped by the command: its dispatcher took the stop, or was ended where it stood.</summary>
    public bool Stopped { get; init; }
}

/// <summary>
/// Turns what <c>stop-run</c> did into what it prints: its line, and under <c>--json</c> its one document, with the same
/// members on every exit - a run the command never reached among them - so a script reads one shape whatever happened.
/// </summary>
public static class RunStopReports
{
    /// <summary>The outcome as the command answers with it: its document under <c>--json</c>, its lines otherwise.</summary>
    /// <param name="outcome">How the command ended.</param>
    /// <param name="report">What it says of the run; <see langword="null"/> where it named none it could read.</param>
    /// <param name="json">Whether the answer is the command's document.</param>
    public static CommandOutcome Render(CommandOutcome outcome, RunStopReport? report, bool json)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        return json
            ? outcome with { Data = [Document(outcome.ExitCode, outcome.Message, report)], Quiet = true }
            : outcome;
    }

    /// <summary>The document the command answers with where it stops before it reaches a run: nothing stopped, no record.</summary>
    /// <param name="exitCode">The code the command exits with.</param>
    /// <param name="summary">The line it ends on.</param>
    public static string Stopped(int exitCode, string summary) => Document(exitCode, summary, report: null);

    /// <summary>
    /// The command's one document: how it ended, the run, its dispatcher, whether it stopped, and its records by kind, as
    /// <c>stop-orchestrator</c>'s are.
    /// </summary>
    private static string Document(int exitCode, string summary, RunStopReport? report)
    {
        var records = new JsonObject();

        if (report?.Record is { } stop)
        {
            records["stop"] = stop;
        }

        return ReportJson.Stopped(
            exitCode,
            summary,
            new JsonObject
            {
                ["run"] = report?.Run,
                ["runDirectory"] = report?.RunDirectory,
                ["dispatcher"] = report?.Dispatcher,
                ["stopped"] = report?.Stopped ?? false,
                ["records"] = records,
            });
    }
}
