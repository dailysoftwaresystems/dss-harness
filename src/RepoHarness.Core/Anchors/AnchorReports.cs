using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using RepoHarness.Core.Output;
using RepoHarness.Core.Results;

namespace RepoHarness.Core.Anchors;

/// <summary>Turns anchor results into what the commands print.</summary>
/// <remarks>
/// Results are data, written unprefixed so another program can read them; the command's own status
/// line follows only where a person needs one. JSON is written alone on standard output, with
/// problems on standard error, so it always parses.
/// </remarks>
public static class AnchorReports
{
    private const int ValueWidth = 60;

    private static readonly JsonSerializerOptions JsonOptions = ReportJson.Options;

    /// <summary>What write-anchor or set-anchor reports.</summary>
    public static CommandOutcome Change(AnchorChange change)
    {
        ArgumentNullException.ThrowIfNull(change);

        var details = new List<string>();

        foreach (var field in change.Fields)
        {
            details.Add($"{field.Field,-11} {Show(field.Before)} -> {Show(field.After)}");
        }

        if (change.IsNew)
        {
            details.Add($"{AnchorCellNames.Status,-11} (new) -> {change.StatusAfter}");
            details.Add($"{"registry",-11} (new) -> {change.To.RelativePath}");
        }
        else if (change.Moved)
        {
            details.Add($"{"registry",-11} {change.From!.RelativePath} -> {change.To.RelativePath}");
        }

        var action = change.IsNew ? "added" : change.Moved ? "moved" : "updated";
        var where = change.IsNew || change.Moved ? $"to the {change.To.Name} registry" : $"in the {change.To.Name} registry";

        return CommandOutcome.Ok(
            change.Written
                ? $"{action} {change.Id} {where}"
                : $"dry run: {change.Id} would be {action} {where}; nothing was written",
            details);
    }

    /// <summary>What read-anchor reports.</summary>
    public static CommandOutcome Lookup(AnchorLookup lookup, bool json)
    {
        ArgumentNullException.ThrowIfNull(lookup);

        var found = lookup.Results.SelectMany(result => result.Matches).ToList();
        IReadOnlyList<string> data = json ? [LookupJson(found)] : LookupText(lookup);

        if (lookup.Missing.Count == 0)
        {
            return CommandOutcome.Ok($"{found.Count} anchor(s)") with { Data = data, Quiet = true };
        }

        var message = new List<string>
        {
            $"{lookup.Missing.Count} id(s) not found: {string.Join(", ", lookup.Missing.Select(miss => miss.Id))}",
        };

        message.AddRange(lookup.Missing
            .Where(miss => miss.SameNamespace.Count > 0)
            .Select(miss => $"  {miss.Id}: ids beginning the same way: {string.Join(", ", miss.SameNamespace)}"));

        return CommandOutcome.Failed(AnchorExit.Findings, string.Join(Environment.NewLine, message)) with { Data = data };
    }

    /// <summary>What read-anchors reports.</summary>
    public static CommandOutcome List(IReadOnlyList<AnchorEntry> entries, AnchorScope scope, bool json)
    {
        ArgumentNullException.ThrowIfNull(entries);

        if (json)
        {
            var array = new JsonArray([.. entries.Select(entry => (JsonNode)new JsonObject
            {
                ["anchor"] = entry.Row.Id,
                ["priority"] = entry.Row.Priority,
                ["status"] = entry.Row.Status,
                ["registry"] = entry.Registry.Name,
            })]);

            return CommandOutcome.Ok($"{entries.Count} anchor(s)") with { Data = [array.ToJsonString(JsonOptions)], Quiet = true };
        }

        var lines = entries
            .Select(entry => $"{Pad(entry.Row.Priority, 3)} {Pad(entry.Row.Status, 13)} {entry.Row.Id}")
            .ToList();

        lines.Add($"{entries.Count} row(s){scope switch
        {
            AnchorScope.Pending => " in the pending registry",
            AnchorScope.Done => " in the done registry",
            _ => string.Empty,
        }}.");

        return CommandOutcome.Ok($"{entries.Count} anchor(s)") with { Data = lines, Quiet = true };
    }

    /// <summary>What read-anchors --lint reports: a finding fails it, and a note does not.</summary>
    public static CommandOutcome Lint(IReadOnlyList<AnchorFinding> findings, bool json)
    {
        ArgumentNullException.ThrowIfNull(findings);

        IReadOnlyList<string> data = json
            ? [FindingsJson(findings).ToJsonString(JsonOptions)]
            : [.. findings.Select(FindingLine), $"{findings.Count} finding(s)."];

        var problems = findings.Count(finding => finding.Severity != AnchorFindingSeverity.Note);

        return problems == 0
            ? CommandOutcome.Ok("the anchor registries are sound") with { Data = data, Quiet = true }
            : CommandOutcome.Failed(AnchorExit.Findings, $"{problems} problem(s) in the anchor registries") with { Data = data };
    }

    /// <summary>What check-anchor-balance reports.</summary>
    public static CommandOutcome Balance(AnchorBalanceReport report, bool json)
    {
        ArgumentNullException.ThrowIfNull(report);

        IReadOnlyList<string> data = json ? [BalanceJson(report)] : BalanceText(report);

        if (report.Passed)
        {
            return CommandOutcome.Ok(
                $"the balance holds: {report.OpenNow} open now against {report.OpenAtBase} {Measured(report)}") with { Data = data, Quiet = json };
        }

        var reasons = new List<string>();

        if (report.NetNew > 0)
        {
            reasons.Add($"this change creates {report.NetNew} more anchor(s) than its work closes; close what was opened, or disclose debt that already existed");
        }

        var fatal = report.Findings.Count(finding => finding.Severity == AnchorFindingSeverity.Fatal);
        if (fatal > 0)
        {
            reasons.Add($"{fatal} problem(s) in the registries must be fixed first");
        }

        return CommandOutcome.Failed(AnchorExit.Findings, string.Join("; ", reasons)) with { Data = data };
    }

    /// <summary>The full text of one row, as read-anchor prints it.</summary>
    public static IReadOnlyList<string> Detail(AnchorEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var row = entry.Row;

        return
        [
            $"anchor      : {row.Id}",
            $"registry    : {entry.Registry.RelativePath}",
            $"priority    : {Unset(row.Priority)}",
            $"status      : {Unset(row.Status)}   -> {(row.IsClosed ? "CLOSED" : "OPEN")}",
            string.Empty,
            $"{AnchorCellNames.TriggerHeading}:",
            $"  {Empty(row.Trigger)}",
            string.Empty,
            $"{AnchorCellNames.ClosingHeading}:",
            $"  {Empty(row.ClosingWork)}",
            string.Empty,
            $"{AnchorCellNames.CrossRefsHeading}:",
            $"  {Empty(row.CrossRefs)}",
        ];
    }

    private static List<string> LookupText(AnchorLookup lookup)
    {
        var lines = new List<string>();

        foreach (var result in lookup.Results.Where(result => result.Matches.Count > 0))
        {
            foreach (var entry in result.Matches)
            {
                if (lines.Count > 0)
                {
                    lines.Add(new string('-', 78));
                }

                lines.AddRange(Detail(entry));
            }

            if (result.Matches.Count > 1)
            {
                lines.Add(
                    $"⚠ {result.Matches.Count} rows carry {result.Id}. One id has one row, and which of these is the real "
                    + "one is for a person to decide.");
            }
        }

        return lines;
    }

    private static string LookupJson(IEnumerable<AnchorEntry> entries)
    {
        var array = new JsonArray([.. entries.Select(entry => (JsonNode)new JsonObject
        {
            ["anchor"] = entry.Row.Id,
            ["registry"] = entry.Registry.RelativePath,
            ["priority"] = entry.Row.Priority,
            ["status"] = entry.Row.Status,
            ["closed"] = entry.Row.IsClosed,
            ["trigger"] = entry.Row.Trigger,
            ["closing"] = entry.Row.ClosingWork,
            ["cross_refs"] = entry.Row.CrossRefs,
        })]);

        return array.ToJsonString(JsonOptions);
    }

    private static List<string> BalanceText(AnchorBalanceReport report)
    {
        var created = report.Opened.Count - report.Disclosed;
        var lostClosed = report.Lost.Count(loss => loss.Closed);

        var lines = new List<string>
        {
            $"base      {report.Base} ({ReportText.Commit(report.BaseCommit)})"
            + (report.BaseMovedOn ? $", measured from where HEAD left it ({ReportText.Commit(report.Commit)})" : string.Empty),
            $"open      {report.OpenAtBase} {Measured(report)}, {report.OpenNow} now",
            $"change    {report.Closed.Count} closed ({report.Bookkeeping} bookkeeping), "
            + (report.Lost.Count > 0
                ? $"{report.Lost.Count} lost{(lostClosed > 0 ? $" ({lostClosed} already closed)" : string.Empty)}, "
                : string.Empty)
            + $"{report.Opened.Count} opened ({created} created, {report.Disclosed} disclosed); counted {SignedCount(report.NetNew)}",
        };

        lines.AddRange(report.Closed.Select(closing =>
            $"  - {closing.Id}{(closing.Bookkeeping ? "   [bookkeeping: not credited]" : string.Empty)}"));
        lines.AddRange(report.Lost.Select(loss => $"  ! {loss.Id}   [lost: {(loss.Closed ? "already closed" : "not credited")}]"));
        lines.AddRange(report.Opened.Select(opening =>
            $"  + {opening.Id}   {opening.Excerpt}{(opening.Disclosed ? "   [disclosed: not counted]" : string.Empty)}"));

        if (report.Merging.Count > 0)
        {
            lines.Add(
                $"note      HEAD is merging {string.Join(", ", report.Merging.Select(ReportText.Commit))}, so the change is "
                + "measured as the commit finishing that merge would be");
        }

        lines.AddRange(report.MissingAtBase.Select(path =>
            $"note      {path} did not exist {Measured(report)}, so it counts as empty there"));
        lines.AddRange(report.MalformedAtBase.Select(path =>
            $"note      {path} was malformed {Measured(report)}, so a row it held that could not be read was not counted there"));
        lines.AddRange(report.Findings
            .Where(finding => finding.Severity == AnchorFindingSeverity.Note)
            .Select(finding => $"note      {finding.File}:{finding.LineNumber}   {finding.Message}"));

        var problems = report.Findings.Where(finding => finding.Severity != AnchorFindingSeverity.Note).ToList();

        if (problems.Count > 0)
        {
            lines.Add("problems");
            lines.AddRange(problems.Select(finding => "  " + FindingLine(finding)));
        }

        return lines;
    }

    private static string BalanceJson(AnchorBalanceReport report)
    {
        var node = new JsonObject
        {
            ["base"] = report.Base,
            ["baseCommit"] = report.BaseCommit,
            ["commit"] = report.Commit,
            ["merging"] = Strings(report.Merging),
            ["openAtBase"] = report.OpenAtBase,
            ["openNow"] = report.OpenNow,
            ["netNew"] = report.NetNew,
            ["passed"] = report.Passed,
            ["closed"] = new JsonArray([.. report.Closed.Select(closing => (JsonNode)new JsonObject
            {
                ["anchor"] = closing.Id,
                ["bookkeeping"] = closing.Bookkeeping,
            })]),
            ["lost"] = new JsonArray([.. report.Lost.Select(loss => (JsonNode)new JsonObject
            {
                ["anchor"] = loss.Id,
                ["closed"] = loss.Closed,
            })]),
            ["opened"] = new JsonArray([.. report.Opened.Select(opening => (JsonNode)new JsonObject
            {
                ["anchor"] = opening.Id,
                ["excerpt"] = opening.Excerpt,
                ["disclosed"] = opening.Disclosed,
            })]),
            ["missingAtBase"] = Strings(report.MissingAtBase),
            ["malformedAtBase"] = Strings(report.MalformedAtBase),
            ["findings"] = FindingsJson(report.Findings),
        };

        return node.ToJsonString(JsonOptions);
    }

    private static JsonArray Strings(IEnumerable<string> values) => new([.. values.Select(value => (JsonNode?)JsonValue.Create(value))]);

    private static JsonArray FindingsJson(IEnumerable<AnchorFinding> findings)
        => new([.. findings.Select(finding => (JsonNode)new JsonObject
        {
            ["file"] = finding.File,
            ["line"] = finding.LineNumber,
            ["severity"] = finding.Severity switch
            {
                AnchorFindingSeverity.Fatal => "fatal",
                AnchorFindingSeverity.Warning => "warning",
                _ => "note",
            },
            ["message"] = finding.Message,
        })]);

    private static string FindingLine(AnchorFinding finding)
        => $"{finding.File}:{finding.LineNumber}   {finding.Severity switch
        {
            AnchorFindingSeverity.Warning => "warning: ",
            AnchorFindingSeverity.Note => "note: ",
            _ => string.Empty,
        }}{finding.Message}";

    /// <summary>Where the balance was measured from: the base, or where HEAD left it where the base has moved on.</summary>
    private static string Measured(AnchorBalanceReport report)
        => report.BaseMovedOn ? $"where HEAD left {report.Base}" : $"at {report.Base}";

    private static string SignedCount(int value)
        => value > 0 ? "+" + value.ToString(CultureInfo.InvariantCulture) : value.ToString(CultureInfo.InvariantCulture);

    private static string Show(string value)
    {
        var flat = AnchorCells.Collapse(value);
        return flat.Length == 0 ? "(empty)" : flat.Length > ValueWidth ? flat[..ValueWidth] + "..." : flat;
    }

    private static string Unset(string value) => value.Length == 0 ? "(unset)" : value;

    private static string Empty(string value) => value.Length == 0 ? "(empty)" : value;

    /// <summary>Pads by what a reader sees rather than by UTF-16 units, so emoji statuses line up.</summary>
    private static string Pad(string text, int width)
    {
        var length = new StringInfo(text).LengthInTextElements;
        return length >= width ? text : text + new string(' ', width - length);
    }
}
