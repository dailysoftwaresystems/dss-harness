using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using DssHarness.Core.Output;

namespace DssHarness.Core.Orchestration.Flow;

/// <summary>
/// One analysis of a run's report: what the analyst saw in it - where the run's time and money went, and each change
/// to make, none where the report shows nothing to change - with every number it cites held to the report it read. Its
/// shape is the schema's (<c>analysis.schema.json</c>), whose rules - every text written, a severity of the review's, a
/// section of the report's - the structured-output gate holds its answer to, and the citations are held to the report
/// beyond it (<see cref="FlowAnalysisEvidence"/>); said as text, as Markdown (<see cref="FlowAnalysis.Markdown"/>) and as
/// one JSON document (<see cref="FlowAnalysis.Document"/>).
/// </summary>
/// <param name="Summary">Where the run's time and money went, in three sentences at most.</param>
/// <param name="PainPoints">Each thing to change, ordered by what it cost; empty where the report shows nothing to change.</param>
/// <param name="Noted">What the analyst saw and could not confirm, each with why.</param>
public sealed record FlowAnalysis(
    string Summary,
    IReadOnlyList<FlowPainPoint> PainPoints,
    IReadOnlyList<FlowNoted> Noted)
{
    /// <summary>What every form says where the analysis names no pain point.</summary>
    public const string NothingToChange = "No pain point: the report shows nothing a change would have paid for.";

    /// <summary>The analysis as text for a terminal: the summary, and each pain point as one line with what it costs and what to change.</summary>
    public string Text()
    {
        var lines = new List<string> { Summary, string.Empty };

        if (PainPoints.Count == 0)
        {
            lines.Add(NothingToChange);
        }

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

        if (PainPoints.Count == 0)
        {
            lines.Add(NothingToChange);
            lines.Add(string.Empty);
        }

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
/// <param name="Where">The steps it names, by the ids of the report's step rows.</param>
/// <param name="Evidence">The report's own numbers it rests on, each cited by its section, row and member.</param>
/// <param name="Impact">The time and dollars it cost, every number of it one of its citations'.</param>
/// <param name="Cause">What the records show of why.</param>
/// <param name="Recommendation">The change to make: a configuration key, a workflow step's key, a prompt's line or a repository fix - the exact text where it is a key.</param>
public sealed record FlowPainPoint(
    string Title,
    string Severity,
    IReadOnlyList<string> Where,
    IReadOnlyList<FlowEvidence> Evidence,
    string Impact,
    string Cause,
    string Recommendation);

/// <summary>
/// One value of the report a pain point rests on: where the report holds it, and what it says - the citation the
/// analyst's answer is held to (<see cref="FlowAnalysisEvidence"/>).
/// </summary>
/// <param name="Section">The report's section that holds it, one the schema lists.</param>
/// <param name="Row">The id of the row that holds it, where the section is a list or holds lists; null or empty where the member sits in the section itself.</param>
/// <param name="Of">The member of the row that holds it, a path of members joined by '.' where it is inside one (<c>tokens.input</c>).</param>
/// <param name="Said">The value as the report holds it.</param>
public sealed record FlowEvidence(string Section, string? Row, string Of, string Said);

/// <summary>One thing the analyst saw and could not confirm.</summary>
/// <param name="What">What it saw.</param>
/// <param name="Why">Why it could not confirm it.</param>
public sealed record FlowNoted(string What, string Why);

/// <summary>
/// What an analysis is held to beyond its schema - the rule the analyst's answer is held to by the structured-output
/// gate (<see cref="Agents.AgentOutput.Holds"/>), asked again for in the analyst's own conversation under the schema's
/// count of repairs: each citation names a section the report holds, the row of it by the row's <c>id</c> - a step's
/// instance, a failed attempt's <c>&lt;instance&gt;@&lt;attempt&gt;</c>, a signal's <c>&lt;kind&gt;#&lt;n&gt;</c>, a
/// question's id, a dollars group's name, a convergence row's - the member by its path, and the value the report holds
/// there, read as the report's JSON holds it: a number as a number, whatever form it is written in, a text and a truth
/// word for word. Each step a pain point names is a row of the report's steps, and every number its impact says is one
/// of its own citations' values. An analysis cannot invent a measurement.
/// </summary>
public static class FlowAnalysisEvidence
{
    /// <summary>What a number in a text may be wrapped in, read off both its ends before it is read as one.</summary>
    private static readonly char[] Wrapping = ['(', ')', '[', ']', '{', '}', '"', '\'', ',', ';', ':', '!', '?', '$', '%', '~', '='];

    /// <summary>
    /// Why <paramref name="analysis"/>'s output is not held by the report it read, each problem a path in the output and
    /// what is wrong there; empty where it is.
    /// </summary>
    /// <param name="analysis">The analysis as the agent answered it, read against its schema.</param>
    /// <param name="report">The report it read, as the builder answered it.</param>
    public static IReadOnlyList<string> Problems(JsonNode analysis, JsonNode report)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(report);

        var problems = new List<string>();
        var steps = (report["steps"] as JsonArray ?? []).OfType<JsonObject>().Select(row => TextOf(row["id"])).ToHashSet(StringComparer.Ordinal);

        foreach (var (point, at) in (analysis["painPoints"] as JsonArray ?? []).Select((point, at) => (point, at)))
        {
            var cited = new HashSet<decimal>();

            foreach (var (step, named) in (point?["where"] as JsonArray ?? []).Select((step, named) => (TextOf(step), named)))
            {
                if (!steps.Contains(step))
                {
                    problems.Add($"painPoints[{at}].where[{named}]: the report's steps hold no row '{step}' - name a step by its row's id");
                }
            }

            foreach (var (citation, of) in (point?["evidence"] as JsonArray ?? []).Select((citation, of) => (citation, of)))
            {
                if (Held(citation!, report, out var numbers) is { } problem)
                {
                    problems.Add($"painPoints[{at}].evidence[{of}]: {problem}");
                }
                else
                {
                    cited.UnionWith(numbers);
                }
            }

            foreach (var said in NumbersIn(TextOf(point?["impact"])).Where(said => !cited.Contains(said.Value)))
            {
                problems.Add($"painPoints[{at}].impact: says {said.Text}, which none of its citations holds - cite where the report holds it, or leave it out");
            }
        }

        return problems;
    }

    /// <summary>
    /// Why <paramref name="citation"/> is not a value the report holds; null where it is, with the numbers it holds - the
    /// value where it is one, each number a text says where it is a text.
    /// </summary>
    private static string? Held(JsonNode citation, JsonNode report, out IReadOnlyList<decimal> numbers)
    {
        numbers = [];
        var section = TextOf(citation["section"]);
        var row = TextOf(citation["row"]);
        var of = TextOf(citation["of"]);

        var held = report[section];

        if (held is not (JsonObject or JsonArray))
        {
            return $"the report holds no section '{section}'";
        }

        JsonNode where = held;

        if (row.Length > 0)
        {
            var rows = RowsIn(held).Where(one => TextOf(one["id"]) == row).Take(2).ToList();

            if (rows.Count != 1)
            {
                return rows.Count == 0 ? $"the section '{section}' holds no row '{row}' - name a row by its id" : $"the section '{section}' holds two rows '{row}'";
            }

            where = rows[0];
        }
        else if (held is JsonArray)
        {
            return $"the section '{section}' is a list: name its row by its id";
        }

        var names = of.Split('.');
        var parent = names[..^1].Aggregate((JsonNode?)where, (inside, name) => inside is JsonObject members ? members[name] : null) as JsonObject;
        var member = parent?[names[^1]];
        var named = $"'{section}'{(row.Length == 0 ? string.Empty : $", {row}")}.{of}";

        if (member is not JsonValue value)
        {
            return member is not null ? $"{named} is no value: cite a member inside it"
                : parent?.ContainsKey(names[^1]) == true ? $"{named} holds nothing in the report"
                : $"{named} is no member the report holds";
        }

        var said = TextOf(citation["said"]);
        var kind = value.GetValueKind();

        if (kind == JsonValueKind.Number)
        {
            // A number is the same number however it is written: 182000, 0.030 and 3E-2 alike are read for their value.
            var holds = value.ToJsonString();
            var same = decimal.TryParse(holds, NumberStyles.Float, CultureInfo.InvariantCulture, out var exact)
                ? decimal.TryParse(said, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var given) && given == exact
                : double.TryParse(said, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var wide) && wide == double.Parse(holds, CultureInfo.InvariantCulture);

            if (!same)
            {
                return $"{named} is {holds}, not '{said}'";
            }

            numbers = decimal.TryParse(holds, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? [number] : [];
            return null;
        }

        var text = kind == JsonValueKind.String ? value.GetValue<string>() : value.ToJsonString();

        if (!string.Equals(text, said, StringComparison.Ordinal))
        {
            // Said back to the analyst in its own conversation, so written as every JSON an agent reads is: a text with an
            // apostrophe reads as the report holds it, never as an escape.
            return $"{named} is {value.ToJsonString(Agents.AgentEnvelope.Written)}, not '{said}'";
        }

        numbers = [.. NumbersIn(text).Select(one => one.Value)];
        return null;
    }

    /// <summary>Every row of every list in <paramref name="section"/>: its own rows where it is one, else each list it holds, at any depth.</summary>
    private static IEnumerable<JsonObject> RowsIn(JsonNode section)
        => section switch
        {
            JsonArray rows => rows.OfType<JsonObject>().Concat(rows.OfType<JsonObject>().SelectMany(one => one.Select(member => member.Value).OfType<JsonNode>().Where(inner => inner is JsonArray).SelectMany(RowsIn))),
            JsonObject members => members.Select(member => member.Value).OfType<JsonNode>().Where(inner => inner is JsonObject or JsonArray).SelectMany(RowsIn),
            _ => [],
        };

    /// <summary>Each number <paramref name="text"/> says, as a word of its own - a sum, a share, a count - with its text; never a digit inside an id.</summary>
    private static IEnumerable<(string Text, decimal Value)> NumbersIn(string text)
    {
        foreach (var word in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var bare = word.Trim(Wrapping).TrimEnd('.').Trim(Wrapping);

            if (bare.Length > 0 && (char.IsAsciiDigit(bare[0]) || (bare.Length > 1 && bare[0] is '-' or '+' && char.IsAsciiDigit(bare[1])))
                && decimal.TryParse(bare, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var value))
            {
                yield return (bare, value);
            }
        }
    }

    /// <summary>A text member's text; empty where it is none.</summary>
    private static string TextOf(JsonNode? node)
        => node is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : string.Empty;
}
