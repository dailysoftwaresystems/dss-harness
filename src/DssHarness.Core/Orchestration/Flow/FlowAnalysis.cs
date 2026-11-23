using System.Globalization;
using System.Text.Json.Nodes;
using DssHarness.Core.Output;

namespace DssHarness.Core.Orchestration.Flow;

/// <summary>
/// One analysis of a run's report: what the analyst saw in it - where the run's time and money went, and each change
/// to make - with every number it cites held to the report it read. One owner of the analysis's shape
/// (<see cref="FlowAnalysisEvidence"/> checks the citations), said as Markdown
/// (<see cref="FlowAnalysis.Markdown"/>) and as one JSON document (<see cref="FlowAnalysis.Document"/>).
/// </summary>
/// <param name="Summary">Where the run's time and money went, in three sentences at most.</param>
/// <param name="PainPoints">Each thing to change, ordered by what it cost.</param>
/// <param name="Noted">What the analyst saw and could not confirm, each with why.</param>
public sealed record FlowAnalysis(
    string Summary,
    IReadOnlyList<FlowPainPoint> PainPoints,
    IReadOnlyList<FlowNoted> Noted)
{
    /// <summary>What is wrong with this analysis as an answer; null when nothing is. The citations are checked apart.</summary>
    public string? Problem()
        => string.IsNullOrWhiteSpace(Summary) ? "its summary is empty"
            : PainPoints.Count == 0 ? "it names no pain point"
            : PainPoints.Any(point => point.Problem() is { } wrong) ? PainPoints.First(point => point.Problem() is not null).Problem()
            : null;

    /// <summary>The analysis as text for a terminal: the summary, and each pain point as one line with what it costs and what to change.</summary>
    public string Text()
    {
        var lines = new List<string> { Summary, string.Empty };

        foreach (var point in PainPoints)
        {
            lines.Add($"[{point.Severity}] {point.Title} ({string.Join(", ", point.Where)})");
            lines.Add($"  impact: {point.Impact}");
            lines.Add($"  recommendation: {point.Recommendation}");
        }

        if (Noted.Count > 0)
        {
            lines.Add(string.Empty);

            foreach (var noted in Noted)
            {
                lines.Add($"noted, not confirmed: {noted.What}: {noted.Why}");
            }
        }

        return string.Join("\n", lines);
    }

    /// <summary>The analysis as Markdown, for the run's record, a pull request or the clipboard.</summary>
    public string Markdown()
    {
        var lines = new List<string> { Summary, string.Empty, "## Pain points", string.Empty };

        foreach (var point in PainPoints)
        {
            lines.Add($"### {point.Title}");
            lines.Add(string.Empty);
            lines.Add($"- severity: {point.Severity}");
            lines.Add($"- where: {string.Join(", ", point.Where)}");
            lines.Add($"- impact: {point.Impact}");
            lines.Add($"- cause: {point.Cause}");
            lines.Add($"- recommendation: {point.Recommendation}");

            foreach (var cited in point.Evidence)
            {
                lines.Add($"- evidence: {cited.Section}{(cited.Row is { } row ? $", {row}" : string.Empty)}.{cited.Of} = {cited.Said}");
            }

            lines.Add(string.Empty);
        }

        if (Noted.Count > 0)
        {
            lines.Add("## Noted, not confirmed");
            lines.Add(string.Empty);

            foreach (var noted in Noted)
            {
                lines.Add($"- {noted.What}: {noted.Why}");
            }
        }

        return string.Join("\n", lines);
    }

    /// <summary>The analysis as one JSON document, the shape <c>analysis.schema.json</c> holds.</summary>
    public JsonObject Document()
        => new()
        {
            ["summary"] = Summary,
            ["painPoints"] = new JsonArray([.. PainPoints.Select(point => new JsonObject
            {
                ["title"] = point.Title,
                ["severity"] = point.Severity,
                ["where"] = new JsonArray([.. point.Where.Select(where => JsonValue.Create(where))]),
                ["evidence"] = new JsonArray([.. point.Evidence.Select(cited => new JsonObject
                {
                    ["section"] = cited.Section,
                    ["row"] = cited.Row,
                    ["of"] = cited.Of,
                    ["said"] = cited.Said,
                })]),
                ["impact"] = point.Impact,
                ["cause"] = point.Cause,
                ["recommendation"] = point.Recommendation,
            })]),
            ["noted"] = new JsonArray([.. Noted.Select(noted => new JsonObject
            {
                ["what"] = noted.What,
                ["why"] = noted.Why,
            })]),
        };
}

/// <summary>One thing to change, as the analyst sees it.</summary>
/// <param name="Title">One line a person who never saw the run understands.</param>
/// <param name="Severity">How much it matters: the review's levels - critical, high, medium, low.</param>
/// <param name="Where">The steps it names.</param>
/// <param name="Evidence">The report's own numbers it rests on, each cited by its section and row.</param>
/// <param name="Impact">The time and dollars it cost.</param>
/// <param name="Cause">What the records show of why.</param>
/// <param name="Recommendation">The change to make: a configuration key, a workflow step's key, a prompt's line or a repository fix - the exact text where it is a key.</param>
public sealed record FlowPainPoint(
    string Title,
    string Severity,
    IReadOnlyList<string> Where,
    IReadOnlyList<FlowEvidence> Evidence,
    string Impact,
    string Cause,
    string Recommendation)
{
    /// <summary>The severity levels, as the review's are.</summary>
    public static readonly IReadOnlyList<string> Severities = ["critical", "high", "medium", "low"];

    /// <summary>What is wrong with this point as an answer; null when nothing is. The citations are checked apart.</summary>
    public string? Problem()
        => string.IsNullOrWhiteSpace(Title) ? "a pain point names no title"
            : !Severities.Contains(Severity, StringComparer.Ordinal) ? $"'{Severity}' is not a severity a review gives"
            : Where.Count == 0 ? $"'{Title}' names no step"
            : Evidence.Count == 0 ? $"'{Title}' cites no evidence"
            : Evidence.Any(cited => cited.Problem() is { } wrong) ? $"'{Title}': {Evidence.First(cited => cited.Problem() is not null).Problem()}"
            : string.IsNullOrWhiteSpace(Impact) ? $"'{Title}' says no impact"
            : string.IsNullOrWhiteSpace(Cause) ? $"'{Title}' says no cause"
            : string.IsNullOrWhiteSpace(Recommendation) ? $"'{Title}' says no recommendation"
            : null;
}

/// <summary>
/// One number of the report a pain point rests on: where the report holds it, and what it says - the citation the
/// analyst's answer is held to (<see cref="FlowAnalysisEvidence"/>).
/// </summary>
/// <param name="Section">The report's section that holds it: <c>theRun</c>, <c>totals</c>, <c>steps</c>, <c>criticalPath</c>, <c>errors</c>, <c>people</c>, <c>convergence</c> or <c>signals</c>.</param>
/// <param name="Row">The row of the section that holds it - a step's instance, a signal's kind, a question's id; null where the section is one object.</param>
/// <param name="Of">The member of the row that holds it.</param>
/// <param name="Said">The value as the report says it, copied exactly.</param>
public sealed record FlowEvidence(string Section, string? Row, string Of, string Said)
{
    /// <summary>What is wrong with this citation as a citation; null when nothing is. The number is checked apart.</summary>
    public string? Problem()
        => string.IsNullOrWhiteSpace(Section) ? "a citation names no section"
            : string.IsNullOrWhiteSpace(Of) ? "a citation names no member"
            : string.IsNullOrWhiteSpace(Said) ? "a citation says no value"
            : null;
}

/// <summary>One thing the analyst saw and could not confirm.</summary>
/// <param name="What">What it saw.</param>
/// <param name="Why">Why it could not confirm it.</param>
public sealed record FlowNoted(string What, string Why)
{
    /// <summary>What is wrong with this note; null when nothing is.</summary>
    public string? Problem()
        => string.IsNullOrWhiteSpace(What) ? "a note says no what"
            : string.IsNullOrWhiteSpace(Why) ? "a note says no why"
            : null;
}

/// <summary>
/// The citations of an analysis held to the report it read: each must name a section the report holds, the row it
/// names must be the section's, the member must be the row's, and the value it says must be the value the report
/// holds - a number read as a number, everything else word for word. An analysis cannot invent a measurement: a
/// citation that fails is a problem like a schema's, and the answer is asked again for.
/// </summary>
public static class FlowAnalysisEvidence
{
    /// <summary>The sections of the report a citation may name, by the member of each row that identifies it.</summary>
    private static readonly IReadOnlyDictionary<string, string> RowsOf = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["steps"] = "step",
        ["criticalPath"] = "step",
        ["errors"] = "step",
        ["people"] = "question",
        ["signals"] = "kind",
    };

    /// <summary>Why <paramref name="analysis"/>'s citations are not the report's; empty where every one is.</summary>
    /// <param name="analysis">The analysis as the agent answered it.</param>
    /// <param name="report">The report it read, as the builder answered it.</param>
    public static IReadOnlyList<string> Problems(JsonNode analysis, JsonNode report)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(report);

        var problems = new List<string>();

        foreach (var (point, at) in (analysis["painPoints"]?.AsArray() ?? []).Select((point, at) => (point, at)))
        {
            foreach (var (cited, of) in (point?["evidence"]?.AsArray() ?? []).Select((cited, of) => (cited, of)))
            {
                var problem = cited is null ? "a citation the report does not read" : Held(cited, report);

                if (problem is not null)
                {
                    problems.Add($"$.painPoints[{at}].evidence[{of}]: {problem}");
                }
            }
        }

        return problems;
    }

    /// <summary>Why <paramref name="cited"/> is not a number the report holds; null where it is.</summary>
    private static string? Held(JsonNode cited, JsonNode report)
    {
        var section = cited["section"]?.GetValue<string>();

        if (section is null || report[section] is not { } held)
        {
            return $"the report holds no section '{section}'";
        }

        var of = cited["of"]?.GetValue<string>();
        var row = cited["row"]?.GetValue<string>() is { Length: > 0 } named ? named : null;
        JsonNode? where = held;

        if (RowsOf.TryGetValue(section ?? string.Empty, out var by))
        {
            if (row is null)
            {
                return $"the section '{section}' is a list: name its row by {by}";
            }

            where = held.AsArray().FirstOrDefault(one => one![by]?.GetValue<string>() == row);

            if (where is null)
            {
                return $"the section '{section}' holds no row '{row}'";
            }
        }
        else if (row is not null)
        {
            where = held[row];

            if (where is null)
            {
                return $"'{section}' holds no row '{row}'";
            }
        }

        var member = of is null ? null : where[of];

        if (member is null)
        {
            return $"'{section}'{(row is null ? string.Empty : $", {row}")} holds no member '{of}'";
        }

        var said = cited["said"]?.GetValue<string>();

        if (!SaysTheSame(said, member))
        {
            return $"'{section}'{(row is null ? string.Empty : $", {row}")}.{of} is {member.ToJsonString(ReportJson.Options)}, not '{said}'";
        }

        return null;
    }

    /// <summary>Whether <paramref name="said"/> is the value <paramref name="member"/> holds: as a number where both read as one, else word for word.</summary>
    private static bool SaysTheSame(string? said, JsonNode member)
        => said is not null
            && (double.TryParse(said, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                && member is JsonValue value
                && value.TryGetValue<double>(out var held)
                ? number == held
                : member is JsonValue text && text.TryGetValue<string>(out var word) && string.Equals(word, said, StringComparison.Ordinal));
}
