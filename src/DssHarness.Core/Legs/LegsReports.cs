using System.Text.Json;
using System.Text.Json.Serialization;
using RepoHarness.Core.Execution;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Hosts;
using RepoHarness.Core.Output;
using RepoHarness.Core.Results;

namespace RepoHarness.Core.Legs;

/// <summary>Turns what checking legs found into what <c>legs</c> prints.</summary>
/// <remarks>
/// JSON is written alone on standard output, with each leg that cannot run warned about on standard
/// error, so the document always parses.
/// </remarks>
public static class LegsReports
{
    private static readonly JsonSerializerOptions JsonOptions = ReportJson.Options;

    /// <summary>What <c>legs</c> reports.</summary>
    public static CommandOutcome Render(LegsReport report, bool json, bool verbose = false)
    {
        ArgumentNullException.ThrowIfNull(report);

        // A leg no host can run is a complete answer: the survey looked and says so. A host that
        // could not be reached is not — nothing was established about the legs it would have taken,
        // and 'OK' for both differs only by a number somebody has to parse out of prose. Incomplete
        // is the code the run verbs already use for exactly this: nothing failed, and not
        // everything reported.
        var silent = report.Hosts.Where(host => !host.Available).ToList();

        var exitCode = report.Defect is not null
            ? Verdicts.ExitCodeFor(LegVerdict.Poisoned)
            : !report.Passed
                ? LegsExit.Unavailable
                : silent.Count > 0
                    ? HarnessExit.Incomplete
                    : HarnessExit.Success;

        var message = Summary(report, silent);

        if (json)
        {
            return new CommandOutcome(exitCode, message)
            {
                Data = [JsonSerializer.Serialize(Document(report), JsonOptions)],
                Quiet = true,
            };
        }

        var details = new List<string>();

        foreach (var host in report.Hosts)
        {
            details.AddRange(host.Actions.Select(action => $"{host.Host}: {action}"));

            // A leg placed on a host further down its candidates says nothing of the hosts it passed over.
            if (host.Reason is { } reason)
            {
                details.Add($"{host.Host}: cannot run legs: {reason}");
            }

            // Asked for with -v: a host that is nearly full shows before a run that fills it, and before the
            // legs its builds would no longer fit are turned away.
            if (verbose && Room(host) is { } room)
            {
                details.Add($"{host.Host}: {room}");
            }
        }

        var width = report.Placements.Count == 0 ? 0 : report.Placements.Max(placement => placement.Leg.Name.Length);

        foreach (var placement in report.Placements.Where(placement => placement.Runnable))
        {
            var host = placement.Host!;
            var through = placement.Leg.Leg.Emulator is { } emulator ? $" through {emulator}" : string.Empty;

            details.Add($"{placement.Leg.Name.PadRight(width)}  runs on {host.Host} ({host.Os} {host.Processor}){through}");
        }

        return new CommandOutcome(exitCode, message, details);
    }

    /// <summary>
    /// The document <c>legs</c> answers with where it stops before it measures anything - a <c>--directory</c> that is
    /// not there, a usage error: its own, placing no leg and measuring no host, after the code it exits with and the
    /// line it ends on.
    /// </summary>
    /// <param name="exitCode">The code the command exits with.</param>
    /// <param name="summary">The line it ends on.</param>
    public static string Stopped(int exitCode, string summary)
        => ReportJson.Stopped(exitCode, summary, JsonSerializer.SerializeToNode(Document(new LegsReport([], [], Named: false)), JsonOptions)!.AsObject());

    /// <summary>The one document of <c>legs --json</c>: where each leg is placed, and what each host is.</summary>
    private static object Document(LegsReport report) => new
    {
        Legs = report.Placements.Select(placement => new
        {
            placement.Leg.Name,
            placement.Leg.Leg.Os,
            placement.Leg.Leg.Processor,
            placement.Leg.Leg.Emulator,
            placement.Runnable,
            Host = placement.Host?.Host.ToString(),
            placement.Reason,
        }),
        Hosts = report.Hosts.Select(host => new
        {
            Host = host.Host.ToString(),
            host.Available,
            host.Reason,
            host.Os,
            host.Processor,
            host.ToolVersion,
            host.ToolPath,
            host.Actions,

            // The room where its copies are kept - the main checkout, for this machine -
            // or why it could not be measured: whole here, where -v adds nothing a script has to ask for.
            host.Space,
            host.SpaceUnmeasured,

            // For a WSL distribution, the drive of this machine its disk grows on.
            host.DiskImageSpace,
            host.DiskImageUnmeasured,
        }),
    };

    /// <summary>The room on <paramref name="host"/>, or why it could not be measured; <see langword="null"/> where it was not asked.</summary>
    private static string? Room(HostReport host)
    {
        var room = host.Space is { } space
            ? space.Describe()
            : host.SpaceUnmeasured is { } why
                ? $"the room there could not be measured: {why}"
                : null;

        var image = host.DiskImageSpace is { } drive
            ? $"its disk grows on this machine's '{drive.Filesystem}', {DiskSpace.Size(drive.FreeBytes)} free"
            : host.DiskImageUnmeasured is { } unmeasured
                ? $"the room on this machine's drive its disk grows on could not be measured: {unmeasured}"
                : null;

        return image is null ? room : room is null ? image : $"{room}; {image}";
    }

    private static string Summary(LegsReport report, IReadOnlyList<HostReport> silent)
    {
        if (report.Placements.Count == 0)
        {
            return "no legs are declared; add them under \"legs\"";
        }

        var runnable = report.Placements.Count(placement => placement.Runnable);
        var counted = $"{runnable} of {ReportText.Counted(report.Placements.Count, "leg")} can run";

        if (report.Defect is { } defect)
        {
            return $"{counted}; whether leg '{defect.Leg.Name}' can run was never established, "
                + $"through a defect in this tool: {defect.Reason}";
        }

        if (report.Passed)
        {
            // Each host is named with its own reason rather than under one verb for all of them.
            // "Did not answer" was said of every host with a reason, including one that answered
            // and turned out to need a newer SDK, and one that was never asked because its
            // connection data is missing - so a host busy with a run read as unreachable, and
            // nothing on this line could say which. Only the reason knows the cause, so the line
            // carries it rather than a class this file would have to guess from the wording.
            return silent.Count == 0
                ? counted
                : $"{counted}; {ReportText.Counted(silent.Count, "host")} cannot take legs, so this survey is incomplete: "
                    + string.Join("; ", silent.Select(host => $"{host.Host} ({host.Reason})"));
        }

        return runnable == 0
            ? $"{counted}: no selected leg can run on any host"
            : $"{counted}: a leg named with --legs cannot run";
    }
}
