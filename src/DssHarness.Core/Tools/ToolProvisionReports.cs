using System.Text.Json;
using System.Text.Json.Serialization;
using DssHarness.Core.Output;
using DssHarness.Core.Results;

namespace DssHarness.Core.Tools;

/// <summary>Turns what provisioning found into what <c>install-missing-tools</c> prints.</summary>
/// <remarks>
/// JSON is written alone on standard output, with every host that could not be reached warned about on
/// standard error while it happened, so the document always parses.
/// </remarks>
public static class ToolProvisionReports
{
    private static readonly JsonSerializerOptions JsonOptions = ReportJson.Options;

    /// <summary>What <c>install-missing-tools</c> reports.</summary>
    /// <param name="report">What provisioning found.</param>
    /// <param name="json">Whether the answer is a document rather than a table.</param>
    public static CommandOutcome Render(ToolProvisionReport report, bool json)
    {
        ArgumentNullException.ThrowIfNull(report);

        var exitCode = report.Passed
            ? HarnessExit.Success
            : report.AnyUnreachable ? HarnessExit.HostUnavailable : ToolsExit.NotProvisioned;

        var message = Summary(report);

        if (json)
        {
            return new CommandOutcome(exitCode, message)
            {
                Data = [JsonSerializer.Serialize(Document(report), JsonOptions)],
                Quiet = true,
            };
        }

        var details = new List<string>();
        var width = report.Legs.Count == 0 ? 0 : report.Legs.Max(leg => leg.Leg.Length);

        foreach (var leg in report.Legs)
        {
            var name = leg.Leg.PadRight(width);

            if (leg.Unreachable is { } unreachable)
            {
                details.Add($"{name}  {leg.Host}: not reached: {unreachable}");
                continue;
            }

            if (leg.Tools.Count == 0)
            {
                details.Add($"{name}  {leg.Host}: nothing to install");
                continue;
            }

            foreach (var tool in leg.Tools)
            {
                var version = tool.Version is { Length: > 0 } found ? $" {found}" : string.Empty;
                var detail = tool.Detail is { Length: > 0 } why ? $": {why}" : string.Empty;

                details.Add($"{name}  {leg.Host}: {tool.Tool}{version} {tool.StateName}{detail}");
            }
        }

        return new CommandOutcome(exitCode, message, details);
    }

    /// <summary>
    /// The document <c>install-missing-tools</c> answers with where it stops before it asks any host - a
    /// <c>--directory</c> that is not there, a usage error: its own, naming no leg, after the code it exits with and the
    /// line it ends on.
    /// </summary>
    /// <param name="exitCode">The code the command exits with.</param>
    /// <param name="summary">The line it ends on.</param>
    /// <param name="dryRun">Whether the command line asked for a dry run.</param>
    public static string Stopped(int exitCode, string summary, bool dryRun)
        => ReportJson.Stopped(exitCode, summary, JsonSerializer.SerializeToNode(Document(new ToolProvisionReport([]) { DryRun = dryRun }), JsonOptions)!.AsObject());

    /// <summary>The one document of <c>install-missing-tools --json</c>: whether it was a dry run, and what each leg's host has.</summary>
    private static object Document(ToolProvisionReport report) => new
    {
        report.DryRun,
        Legs = report.Legs.Select(leg => new
        {
            leg.Leg,
            Host = leg.Host.ToString(),
            leg.Provisioned,
            leg.Unreachable,
            Tools = leg.Tools.Select(tool => new
            {
                tool.Tool,
                State = tool.StateName,
                tool.Version,
                tool.Detail,
            }),
        }),
    };

    private static string Summary(ToolProvisionReport report)
    {
        if (report.Legs.Count == 0)
        {
            return "no legs are declared; add them under \"legs\"";
        }

        var ready = report.Legs.Count(leg => leg.Provisioned);
        var counted = $"{ready} of {ReportText.Counted(report.Legs.Count, "leg")} have every tool they need";
        var dryRun = report.DryRun ? "; --dry-run installed nothing" : string.Empty;

        if (report.Passed)
        {
            return counted + dryRun;
        }

        return report.AnyUnreachable
            ? $"{counted}: a host could not be reached{dryRun}"
            : $"{counted}: a tool is missing, out of date, or could not be installed{dryRun}";
    }
}
