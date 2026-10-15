using System.Text.Json;
using DssHarness.Core.Configuration;
using DssHarness.Core.Execution;
using DssHarness.Core.Output;
using DssHarness.Core.Results;

namespace DssHarness.Core.SetUps;

/// <summary>Turns what <c>up &lt;name&gt;</c> or <c>down &lt;name&gt;</c> ran into what the command prints.</summary>
/// <remarks>
/// The line's own output streamed as it ran, so the text answer is one line saying how it ended. Under <c>--json</c>
/// the document is written alone on standard output, the line's output having gone to standard error with every other
/// line the command said.
/// </remarks>
public static class SetUpReports
{
    private static readonly JsonSerializerOptions JsonOptions = ReportJson.Options;

    /// <summary>What the command reports: success where the line exited zero, and <see cref="HarnessExit.CommandFailed"/> otherwise.</summary>
    /// <param name="report">What ran.</param>
    /// <param name="json">Whether the answer is a document rather than a line.</param>
    public static CommandOutcome Render(SetUpReport report, bool json)
    {
        ArgumentNullException.ThrowIfNull(report);

        var key = SetUpService.CommandName(report.Direction);
        var ended = $"'{report.Name}'{SetUpValues.Said(report.Inputs)} {key}: '{report.Ran.Line}' exited {report.Ran.ExitCode}";
        var outcome = report.Ran.Succeeded
            ? CommandOutcome.Ok(ended)
            : CommandOutcome.Failed(HarnessExit.CommandFailed, ended);

        return json
            ? outcome with { Data = [JsonSerializer.Serialize(Document(report), JsonOptions)], Quiet = true }
            : outcome;
    }

    /// <summary>
    /// The document the command answers with where it stops before running a line - a name nobody declared, a line
    /// this platform has none of, a program it may not start: the code it exits with and the line it ends on, then
    /// which direction it was asked to run.
    /// </summary>
    /// <param name="exitCode">The code the command exits with.</param>
    /// <param name="summary">The line it ends on.</param>
    /// <param name="direction">Up or down.</param>
    public static string Stopped(int exitCode, string summary, SetUpDirection direction)
        => ReportJson.Stopped(
            exitCode,
            summary,
            JsonSerializer.SerializeToNode(new { Direction = SetUpService.CommandName(direction) }, JsonOptions)!.AsObject());

    /// <summary>The one document of <c>up</c> and <c>down</c> under <c>--json</c>.</summary>
    private static object Document(SetUpReport report) => new
    {
        Direction = SetUpService.CommandName(report.Direction),
        report.Name,
        report.Inputs,
        report.Platform,
        report.Directory,
        Ran = new
        {
            report.Ran.Line,
            report.Ran.ExitCode,
            DurationSeconds = LedgerReport.Seconds(report.Ran.Duration),
        },
    };
}
