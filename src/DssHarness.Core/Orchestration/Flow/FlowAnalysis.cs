using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DssHarness.Core.Json;

namespace DssHarness.Core.Orchestration.Flow;

/// <summary>
/// One analysis of a run's report: what the analyst saw in it - where the run's time and money went, and each change
/// to make, none where the report shows nothing to change - with every value it cites held to the report it read and
/// every figure of its impacts the tool's own writing of a cited value. Its shape is the schema's
/// (<c>analysis.schema.json</c>), whose rules - every text written, a severity of the review's, a section of the
/// report's - the structured-output gate holds its answer to, and the citations and the impacts are held to the report
/// beyond it (<see cref="FlowAnalysisEvidence"/>), each impact kept as the tool wrote it with the template it was
/// written from; said as text, as Markdown (<see cref="FlowAnalysis.Markdown"/>) and as one JSON document
/// (<see cref="FlowAnalysis.Document"/>).
/// </summary>
/// <param name="Summary">Where the run's time and money went, in three sentences at most, with no number of its own: the figures are its pain points'.</param>
/// <param name="PainPoints">Each thing to change, ordered by what it cost; empty where the report shows nothing to change.</param>
/// <param name="Noted">What the analyst saw and could not confirm, each with why, with no number of its own.</param>
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

    /// <summary>
    /// The analysis as one JSON document, the shape <c>analysis.schema.json</c> holds with each pain point's impact as the
    /// tool wrote it and, beside it, the template the analyst wrote (<c>impactTemplate</c>).
    /// </summary>
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
                ["impactTemplate"] = point.ImpactTemplate,
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
/// <param name="Title">One line a person who never saw the run understands, with no number of its own.</param>
/// <param name="Severity">How much it matters: the review's levels - critical, high, medium, low.</param>
/// <param name="Where">The steps it names, by the ids of the report's step rows.</param>
/// <param name="Evidence">The report's own values it rests on, each cited by its section, row and member.</param>
/// <param name="Impact">The time and dollars it cost, as the tool wrote it from <paramref name="ImpactTemplate"/>: each placeholder there the cited value in the cited member's unit, as the report writes it (<see cref="FlowAnalysisEvidence"/>).</param>
/// <param name="Cause">What the records show of why, with no number of its own.</param>
/// <param name="Recommendation">The change to make: a configuration key, a workflow step's key, a prompt's line or a repository fix - the exact text where it is a key, the values it proposes and never a measurement.</param>
/// <param name="ImpactTemplate">The impact as the analyst wrote it: no number of its own, every figure a placeholder naming one of <paramref name="Evidence"/> by its place, from <c>{1}</c>.</param>
public sealed record FlowPainPoint(
    string Title,
    string Severity,
    IReadOnlyList<string> Where,
    IReadOnlyList<FlowEvidence> Evidence,
    string Impact,
    string Cause,
    string Recommendation,
    string ImpactTemplate);

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
/// count of repairs - and how its impacts are written once it holds (<see cref="Rendered"/>). Each citation names a
/// section the report holds, the row of it by the row's <c>id</c> - a step's instance, a failed attempt's
/// <c>&lt;instance&gt;@&lt;attempt&gt;</c>, a signal's <c>&lt;kind&gt;#&lt;n&gt;</c>, a question's id, a dollars group's
/// name, a convergence row's - the member by its path, and the value the report holds there, read as the report's JSON
/// holds it: a number as a number, whatever form it is written in, a text and a truth word for word. Each step a pain
/// point names is a row of the report's steps. An impact states no number of its own: it is a template whose every
/// figure is a placeholder naming one of its pain point's citations by its place in the evidence, <c>{1}</c> the first,
/// which the tool writes as the cited value with the unit the report holds that member in, as the report writes it - a
/// count with what it counts, a number that is no amount after its label (<see cref="FlowRunReport.SaidIn"/>, its unit
/// the one <see cref="FlowRunReport.Units"/> holds). A digit anywhere else in it is a number of its own and refused, but
/// inside an id - a word the report holds anywhere that starts with a letter and holds a digit, a name of its rows,
/// sections and members or a word of its texts, whole between white space or a piece of one, or a path a citation
/// writes (<see cref="Names"/>), the analysis's own word read whole first; and so are a placeholder past its evidence, one whose citation holds
/// no number of the report's, and one a letter, a number, a currency's sign, <c>%</c> or another placeholder touches, or
/// a sign before it. The summary, each pain point's title and cause, and each thing noted hold no number of their own
/// either, but in an id; the recommendation is not read - it states the values it proposes, a key's exact text, and is
/// no measurement. So the summary, the titles, the causes, the impacts and what was noted cannot invent a measurement:
/// their only figures are the citations, the report's own, and the impacts', written by the tool.
/// </summary>
public static class FlowAnalysisEvidence
{
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

        return Read(analysis, report).Problems;
    }

    /// <summary>
    /// <paramref name="analysis"/> with each pain point's impact written by the tool - every placeholder its cited value in
    /// the unit the report holds it in, as the report writes it - and the template it was written from beside it
    /// (<c>impactTemplate</c>); null where the report does not hold the analysis, each reason in <paramref name="problems"/>
    /// as <see cref="Problems"/> says it.
    /// </summary>
    /// <param name="analysis">The analysis as the agent answered it, read against its schema.</param>
    /// <param name="report">The report it read, as the builder answered it.</param>
    /// <param name="problems">Why the report does not hold it; empty where it does.</param>
    public static JsonNode? Rendered(JsonNode analysis, JsonNode report, out IReadOnlyList<string> problems)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(report);

        var read = Read(analysis, report);
        problems = read.Problems;

        if (problems.Count > 0)
        {
            return null;
        }

        var rendered = analysis.DeepClone();

        foreach (var (point, at) in (rendered["painPoints"] as JsonArray ?? []).Select((point, at) => (point as JsonObject, at)))
        {
            if (point is null)
            {
                continue;
            }

            var template = point["impact"]?.DeepClone();
            point["impact"] = read.Impacts[at];
            point.Insert(point.IndexOf("impact") + 1, "impactTemplate", template);
        }

        return rendered;
    }

    /// <summary>
    /// Each pain point of <paramref name="analysis"/>'s impact as the tool writes it, in their order, and why the report
    /// does not hold the analysis - each problem a path in the output and what is wrong there.
    /// </summary>
    private static (IReadOnlyList<string> Impacts, IReadOnlyList<string> Problems) Read(JsonNode analysis, JsonNode report)
    {
        var problems = new List<string>();
        var impacts = new List<string>();
        var steps = (report["steps"] as JsonArray ?? []).OfType<JsonObject>().Select(row => TextOf(row["id"])).ToHashSet(StringComparer.Ordinal);
        var held = Names.Of(report);
        var points = analysis["painPoints"] as JsonArray ?? [];

        // The summary and what was noted belong to no pain point, so every citation's paths are their ids.
        var every = held.With(points.SelectMany(point => (point?["evidence"] as JsonArray ?? []).SelectMany(PathsOf)));
        Written(TextOf(analysis["summary"]), cited: null, every, "summary", problems);

        foreach (var (point, at) in points.Select((point, at) => (point, at)))
        {
            var evidence = point?["evidence"] as JsonArray ?? [];
            var names = held.With(evidence.SelectMany(PathsOf));
            var cited = new List<Cited?>();

            Written(TextOf(point?["title"]), cited: null, names, $"painPoints[{at}].title", problems);

            foreach (var (step, named) in (point?["where"] as JsonArray ?? []).Select((step, named) => (TextOf(step), named)))
            {
                if (!steps.Contains(step))
                {
                    problems.Add($"painPoints[{at}].where[{named}]: the report's steps hold no row '{step}' - name a step by its row's id");
                }
            }

            foreach (var (citation, of) in evidence.Select((citation, of) => (citation, of)))
            {
                if (Held(citation!, report, out var one) is { } problem)
                {
                    problems.Add($"painPoints[{at}].evidence[{of}]: {problem}");
                    cited.Add(null);
                }
                else
                {
                    cited.Add(one);
                }
            }

            impacts.Add(Written(TextOf(point?["impact"]), cited, names, $"painPoints[{at}].impact", problems));
            Written(TextOf(point?["cause"]), cited: null, names, $"painPoints[{at}].cause", problems);
        }

        foreach (var (noted, at) in (analysis["noted"] as JsonArray ?? []).Select((noted, at) => (noted, at)))
        {
            Written(TextOf(noted?["what"]), cited: null, every, $"noted[{at}].what", problems);
            Written(TextOf(noted?["why"]), cited: null, every, $"noted[{at}].why", problems);
        }

        return (impacts, problems);
    }

    /// <summary>
    /// <paramref name="text"/> as the tool writes it, where it is an impact: its text as the analyst wrote it, and each
    /// placeholder the value of the citation it names, said with that value's unit (<see cref="FlowRunReport.SaidIn"/>).
    /// Each of its words is read whole first - its run between white space, from its first letter or digit to its last
    /// (<see cref="Inner"/>) - and is an id where <paramref name="names"/> holds it so, as the report wrote it
    /// (<c>_C++20_</c>, <c>`src/3rdparty/zlib/inflate.c`.</c>); else it is read through, each name that stands whole in
    /// it an id. Every numeral it writes of its own - outside a placeholder of an impact, and outside an id - is a
    /// problem, once for the word it stands in, for an analysis's figures are its impacts' placeholders and its citations;
    /// so is, in an impact, a placeholder that names no citation of <paramref name="cited"/>, one whose citation holds no
    /// number of the report's, and one a letter, a number, a currency's sign, <c>%</c> or another placeholder touches - or
    /// a sign before it - which would make the figure another measure than the one the tool writes. A placeholder whose
    /// citation the report does not hold says nothing more: its citation's own problem is said at its path.
    /// </summary>
    /// <param name="text">The member as the analyst wrote it.</param>
    /// <param name="cited">Its pain point's citations in their order, each null where the report does not hold it, where it is an impact; null for any other member, which no placeholder is read in.</param>
    /// <param name="names">The words read as ids.</param>
    /// <param name="path">Where the member is in the output.</param>
    /// <param name="problems">The problems found, appended to.</param>
    private static string Written(string text, IReadOnlyList<Cited?>? cited, Names names, string path, List<string> problems)
    {
        var written = new StringBuilder(text.Length);
        var said = 0;
        var whole = (Start: -1, End: -1);

        for (var at = 0; at < text.Length;)
        {
            if (!char.IsWhiteSpace(text[at]) && (at == 0 || char.IsWhiteSpace(text[at - 1])))
            {
                var end = at;

                while (end < text.Length && !char.IsWhiteSpace(text[end]))
                {
                    end++;
                }

                whole = Inner(text, at, end) is var inner && inner.End > inner.Start && names.Holds(text, inner.Start, inner.End) ? inner : (-1, -1);
            }

            if (at == whole.Start)
            {
                written.Append(text, at, whole.End - at);
                at = whole.End;
                continue;
            }

            if (names.At(text, at) is > 0 and var name)
            {
                written.Append(text, at, name);
                at += name;
                continue;
            }

            if (cited is not null && PlaceholderAt(text, at) is { } placeholder)
            {
                var end = at + placeholder.Text.Length;

                if (Touching(text, at, end) is { } touching)
                {
                    problems.Add($"{path}: {placeholder.Text} touches {touching} - a placeholder stands apart from every letter, number, currency's sign, % and other placeholder, and from a sign before it, for the tool writes the figure with its own unit; leave the unit to it");
                }

                if (placeholder.Place is not { } place || place < 1 || place > cited.Count)
                {
                    problems.Add($"{path}: {placeholder.Text} names no citation - its evidence holds {cited.Count.ToString(CultureInfo.InvariantCulture)}, and {{n}} names the n-th of them, from {{1}}");
                }
                else if (cited[place - 1] is { } citation)
                {
                    if (citation.Figure is { } figure)
                    {
                        written.Append(FlowRunReport.SaidIn(figure.Unit, figure.Value, figure.Member));
                    }
                    else
                    {
                        problems.Add($"{path}: {placeholder.Text} names {citation.Named}, which holds no number the report measures - a placeholder is written as a cited number in its unit; cite a number of the report's, or say it in words");
                    }
                }

                at = end;
                continue;
            }

            if (at >= said && char.IsNumber(text, at))
            {
                var (start, end) = WordAround(text, at);
                problems.Add(cited is not null
                    ? $"{path}: writes '{text[start..end]}', a number of its own - write each figure as the placeholder of the citation that holds it ({{1}} the first of its evidence), which the tool writes as the report does; a digit stands only in a placeholder or in an id the report holds"
                    : $"{path}: writes '{text[start..end]}', a number of its own - an analysis's figures are its impacts', each a placeholder of a citation the tool writes, and its evidence's; say this without one, a digit standing only in an id the report holds");
                said = end;
            }

            written.Append(text[at]);
            at++;
        }

        return written.ToString();
    }

    /// <summary>
    /// What touches the placeholder from <paramref name="start"/> to <paramref name="end"/> of <paramref name="text"/> and
    /// would make its figure another measure: a letter, a number, a currency's sign, <c>%</c> or another placeholder - whose
    /// figure the tool writes beside it, their digits run together - directly before or after it, or a sign directly
    /// before it (<c>${1}</c>, <c>{1}h</c>, <c>{1}%</c>, <c>{1}{2}</c>, <c>-{1}</c>) - only what touches it is read; null
    /// where nothing does.
    /// </summary>
    private static string? Touching(string text, int start, int end)
    {
        var said = new List<string>();

        if (PlaceholderEndingAt(text, start) is { } placeholder)
        {
            said.Add($"'{placeholder}' before it");
        }
        else if (start > 0 && Rune.DecodeLastFromUtf16(text.AsSpan(0, start), out var before, out _) == System.Buffers.OperationStatus.Done
            && (Measures(before) || before.Value is '+' or '-' or '−' or '±'))
        {
            said.Add($"'{before}' before it");
        }

        if (end < text.Length && PlaceholderAt(text, end) is { } next)
        {
            said.Add($"'{next.Text}' after it");
        }
        else if (end < text.Length && Rune.TryGetRuneAt(text, end, out var after) && Measures(after))
        {
            said.Add($"'{after}' after it");
        }

        return said.Count == 0 ? null : string.Join(" and ", said);

        static bool Measures(Rune rune)
            => Rune.IsLetter(rune) || Rune.IsNumber(rune) || Rune.GetUnicodeCategory(rune) == UnicodeCategory.CurrencySymbol || rune.Value == '%';
    }

    /// <summary>
    /// The placeholder that starts at <paramref name="at"/> of <paramref name="text"/>: <c>{</c>, the ASCII digits of the
    /// citation's place in its evidence, and <c>}</c> - its text, and the place where the digits hold one an
    /// <see cref="int"/> holds; null where none starts there.
    /// </summary>
    private static (string Text, int? Place)? PlaceholderAt(string text, int at)
    {
        if (text[at] != '{')
        {
            return null;
        }

        var end = at + 1;

        while (end < text.Length && char.IsAsciiDigit(text[end]))
        {
            end++;
        }

        if (end == at + 1 || end == text.Length || text[end] != '}')
        {
            return null;
        }

        return (text[at..(end + 1)], int.TryParse(text.AsSpan((at + 1)..end), NumberStyles.None, CultureInfo.InvariantCulture, out var place) ? place : null);
    }

    /// <summary>The placeholder of <paramref name="text"/> that ends just before <paramref name="end"/>, as its text; null where none does.</summary>
    private static string? PlaceholderEndingAt(string text, int end)
    {
        if (end < 1 || text[end - 1] != '}')
        {
            return null;
        }

        var open = end - 1;

        while (open > 0 && char.IsAsciiDigit(text[open - 1]))
        {
            open--;
        }

        return open > 0 && PlaceholderAt(text, open - 1) is { } placeholder && open - 1 + placeholder.Text.Length == end ? placeholder.Text : null;
    }

    /// <summary>Where the word of <paramref name="text"/> the character at <paramref name="at"/> stands in starts and ends: the run of what is not white space around it.</summary>
    private static (int Start, int End) WordAround(string text, int at)
    {
        var start = at;

        while (start > 0 && !char.IsWhiteSpace(text[start - 1]))
        {
            start--;
        }

        var end = at;

        while (end < text.Length && !char.IsWhiteSpace(text[end]))
        {
            end++;
        }

        return (start, end);
    }

    /// <summary>
    /// The paths <paramref name="citation"/> writes, each read as an id: its section, its row and its member, and the
    /// member under its row and under its section, as the analysis's own evidence lines write them (<c>c@1.exit</c>,
    /// <c>theRun.budget.minutes</c>).
    /// </summary>
    private static IEnumerable<string> PathsOf(JsonNode? citation)
    {
        var section = TextOf(citation?["section"]);
        var row = TextOf(citation?["row"]);
        var of = TextOf(citation?["of"]);

        return [section, row, of, $"{row}.{of}", $"{section}.{of}"];
    }

    /// <summary>
    /// Why <paramref name="citation"/> is not a value the report holds; null where it is, with what it names and the
    /// figure it holds (<see cref="Cited"/>).
    /// </summary>
    private static string? Held(JsonNode citation, JsonNode report, out Cited? cited)
    {
        cited = null;
        var section = TextOf(citation["section"]);
        var row = TextOf(citation["row"]);
        var of = TextOf(citation["of"]);

        var held = report[section];

        if (held is not (JsonObject or JsonArray))
        {
            return $"the report holds no section '{section}'";
        }

        JsonNode where = held;
        var path = section;

        if (row.Length > 0)
        {
            var rows = RowsIn(held, section).Where(one => TextOf(one.Row["id"]) == row).Take(2).ToList();

            if (rows.Count != 1)
            {
                return rows.Count == 0 ? $"the section '{section}' holds no row '{row}' - name a row by its id" : $"the section '{section}' holds two rows '{row}'";
            }

            (where, path) = rows[0];
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
        var unit = FlowRunReport.UnitOf($"{path}.{of}");

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

            cited = new Cited(named, unit is { } measured ? (value, measured, of) : null);
            return null;
        }

        var text = kind == JsonValueKind.String ? value.GetValue<string>() : value.ToJsonString();

        if (!string.Equals(text, said, StringComparison.Ordinal))
        {
            // Said back to the analyst in its own conversation, so written as every JSON an agent reads is: a text with an
            // apostrophe reads as the report holds it, never as an escape.
            return $"{named} is {value.ToJsonString(Agents.AgentEnvelope.Written)}, not '{said}'";
        }

        // A text is a figure only where it is one number whole, as the report writes a step's run or a hotspot's touches,
        // in a member the report measures in a unit.
        cited = new Cited(
            named,
            kind == JsonValueKind.String && unit is { } textUnit && decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out _)
                ? (value, textUnit, of)
                : null);
        return null;
    }

    /// <summary>
    /// Every row of every list in <paramref name="section"/>, with the path of the list that holds it (<c>[]</c> marking
    /// its rows): its own rows where it is one, else each list it holds, at any depth.
    /// </summary>
    private static IEnumerable<(JsonObject Row, string List)> RowsIn(JsonNode section, string path)
        => section switch
        {
            JsonArray rows => rows.OfType<JsonObject>().Select(row => (Row: row, List: $"{path}[]"))
                .Concat(rows.OfType<JsonObject>().SelectMany(one => one.Where(member => member.Value is JsonArray).SelectMany(member => RowsIn(member.Value!, $"{path}[].{member.Key}")))),
            JsonObject members => members.Where(member => member.Value is JsonObject or JsonArray).SelectMany(member => RowsIn(member.Value!, $"{path}.{member.Key}")),
            _ => [],
        };

    /// <summary>
    /// Where the word of <paramref name="text"/> from <paramref name="start"/> to <paramref name="end"/> starts and ends
    /// once what is neither a letter nor a digit is read off its start and its end - but a <c>]</c> that closes a <c>[</c>
    /// it holds: <c>(CS0246:</c> is <c>CS0246</c>, <c>[win-arm64]</c> is <c>win-arm64</c>, <c>_C++20_</c> is
    /// <c>C++20</c> and <c>items[1]</c> itself; it starts where it ends where it holds no letter or digit.
    /// </summary>
    private static (int Start, int End) Inner(string text, int start, int end)
    {
        while (start < end && !char.IsLetterOrDigit(text[start]))
        {
            start++;
        }

        var opened = start < end ? text.IndexOf('[', start, end - start) : -1;

        while (end > start && !(char.IsLetterOrDigit(text[end - 1]) || (text[end - 1] == ']' && opened >= 0 && opened < end - 1)))
        {
            end--;
        }

        return (start, end);
    }

    /// <summary>Whether <paramref name="character"/> is one a word keeps whatever stands around it: a letter, a digit, a mark, or an underscore or another joining punctuation.</summary>
    private static bool Whole(char character)
        => char.IsLetterOrDigit(character)
            || char.GetUnicodeCategory(character) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark or UnicodeCategory.ConnectorPunctuation;

    /// <summary>
    /// One citation the report holds: what it names, as a problem says it (<c>'steps', b.usd</c>), and the figure it holds
    /// - its value and the unit the report holds it in - where it holds one: a number of a member the report measures, or
    /// a text of one that is one number whole; null where it holds none, a text of words or a truth.
    /// </summary>
    /// <param name="Named">What it names.</param>
    /// <param name="Figure">The value, its unit and the member's path below its row as the citation writes it; null where it holds no number the report measures.</param>
    private sealed record Cited(string Named, (JsonValue Value, FlowUnit Unit, string Member)? Figure);

    /// <summary>
    /// The words every member the analysis is checked in is read with as ids, their digits never a number of its own:
    /// what the report holds as a name - each row's id and each name it is made of, as the row's own members hold them (a
    /// hotspot's item, class and path), every section's and member's name, and every word its texts hold, whole between
    /// white space and in its pieces (<see cref="WordsIn"/>: a test's <c>Sizes/ParserTest.ReadsItsForm/1</c>, a file's
    /// <c>src/3rdparty/zlib/inflate.c</c>, a diagnostic's <c>CS0246</c> and <c>Parser2.cs</c>, a leg's <c>win-arm64</c>, a
    /// model's <c>glm-4.6</c>) - and the paths the citations write (<see cref="PathsOf"/>). A name is kept where it holds a
    /// numeral and starts with a letter, once what is neither a letter nor a digit is read off its start: a name that
    /// starts as a number is no id, so no number - a duration, a date, a version - passes as one.
    /// </summary>
    private sealed class Names
    {
        /// <summary>The trees of names an id is looked for in, the report's first.</summary>
        private readonly IReadOnlyList<Node> _trees;

        /// <summary>The names <paramref name="trees"/> hold.</summary>
        private Names(IReadOnlyList<Node> trees) => _trees = trees;

        /// <summary>
        /// What <paramref name="report"/> holds as names: each row's id and the names it is made of, every section's and
        /// member's name, and every word its texts hold, whole and in its pieces (<see cref="WordsIn"/>) - a test's id, a
        /// diagnostic's code and file, a leg a signal names, a model a fallback left.
        /// </summary>
        public static Names Of(JsonNode report)
        {
            var tree = new Node();

            foreach (var (place, node) in JsonWalk.Descendants(report))
            {
                if (place.Name is { } name)
                {
                    tree.Add(name);
                }

                if (node is JsonValue && TextOf(node) is { Length: > 0 } text)
                {
                    foreach (var word in WordsIn(text))
                    {
                        tree.Add(word);
                    }
                }

                if (place.Index is not null && node is JsonObject row && TextOf(row["id"]) is { Length: > 0 } id)
                {
                    tree.Add(id);

                    foreach (var member in row)
                    {
                        if (TextOf(member.Value) is { Length: > 0 } part && PartOf(id, part))
                        {
                            tree.Add(part);
                        }
                    }
                }
            }

            return new([tree]);
        }

        /// <summary>These names and <paramref name="paths"/>.</summary>
        public Names With(IEnumerable<string> paths)
        {
            var tree = new Node();

            foreach (var path in paths)
            {
                tree.Add(path);
            }

            return new([.. _trees, tree]);
        }

        /// <summary>
        /// How many characters the longest name that stands whole at <paramref name="at"/> of <paramref name="text"/> is: one
        /// that no character a word holds joins on either side - a letter, a digit, a mark or an underscore, or a point, a
        /// hyphen or a comma with a letter or a digit beyond it - so <c>c@10</c> holds no <c>c@1</c>, nor <c>build2-x</c>
        /// a <c>build2</c>; 0 where none does.
        /// </summary>
        public int At(string text, int at)
        {
            if (at > 0 && (Whole(text[at - 1]) || (text[at - 1] is '-' or '.' or ',' && at > 1 && char.IsLetterOrDigit(text[at - 2]))))
            {
                return 0;
            }

            var longest = 0;

            foreach (var tree in _trees)
            {
                var node = tree;

                for (var end = at; end < text.Length && node.Next.TryGetValue(text[end], out var next);)
                {
                    node = next;
                    end++;

                    if (node.Ends && end - at > longest && StandsWhole(text, end))
                    {
                        longest = end - at;
                    }
                }
            }

            return longest;
        }

        /// <summary>Whether a name that ends at <paramref name="end"/> of <paramref name="text"/> ends there, no character a word holds joining it after.</summary>
        private static bool StandsWhole(string text, int end)
            => end == text.Length
                || !(Whole(text[end]) || (text[end] is '-' or '.' or ',' && end + 1 < text.Length && char.IsLetterOrDigit(text[end + 1])));

        /// <summary>
        /// Whether one of these names is <paramref name="text"/> from <paramref name="start"/> to <paramref name="end"/>,
        /// exactly.
        /// </summary>
        public bool Holds(string text, int start, int end)
        {
            foreach (var tree in _trees)
            {
                var node = tree;

                for (var at = start; at < end && node is not null; at++)
                {
                    node = node.Next.GetValueOrDefault(text[at]);
                }

                if (node?.Ends == true)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// The words <paramref name="text"/> holds: each run of it between white space, whole - <c>Sizes/ParserTest.ReadsItsForm/1</c>,
        /// <c>src/3rdparty/zlib/inflate.c</c>, <c>C++20</c> - and each piece of such a run between what no id is written with,
        /// a piece being a run of a letter, a digit, a mark, an underscore, <c>-</c>, <c>.</c>, <c>@</c>, <c>#</c>,
        /// <c>[</c> and <c>]</c>, parted at everything else (a quote, a parenthesis, a colon, a slash, a plus, a comma);
        /// each read from its first letter or digit to its last (<see cref="Inner"/>). So
        /// <c>src/Parser2.cs(12,5): error CS0246:</c> holds <c>Parser2.cs</c> and <c>CS0246</c> among its words,
        /// <c>leg 'win-arm64'.</c> and <c>[win-arm64]</c> hold <c>win-arm64</c>, and <c>items[1]</c> holds itself. Only the report's texts are parted so; an analyst's word is read whole first and parted only where no
        /// name is it whole (<see cref="Written"/>).
        /// </summary>
        private static IEnumerable<string> WordsIn(string text)
        {
            for (var at = 0; at < text.Length; at++)
            {
                if (char.IsWhiteSpace(text[at]))
                {
                    continue;
                }

                var end = at;

                while (end < text.Length && !char.IsWhiteSpace(text[end]))
                {
                    end++;
                }

                if (Inner(text, at, end) is var (start, last) && last > start)
                {
                    yield return text[start..last];
                }

                for (var piece = at; piece < end;)
                {
                    var stop = piece;

                    while (stop < end && (Whole(text[stop]) || text[stop] is '-' or '.' or '@' or '#' or '[' or ']'))
                    {
                        stop++;
                    }

                    if (stop > piece && Inner(text, piece, stop) is var (from, to) && to > from)
                    {
                        yield return text[from..to];
                    }

                    piece = Math.Max(stop, piece + 1);
                }

                at = end;
            }
        }

        /// <summary>Whether <paramref name="part"/> is a name <paramref name="id"/> is made of: the id itself, or a part of it that a colon sets apart.</summary>
        private static bool PartOf(string id, string part)
            => string.Equals(id, part, StringComparison.Ordinal)
                || id.StartsWith($"{part}:", StringComparison.Ordinal)
                || id.EndsWith($":{part}", StringComparison.Ordinal)
                || id.Contains($":{part}:", StringComparison.Ordinal);

        /// <summary>One character of a tree of names: what may follow it, and whether a name ends at it.</summary>
        private sealed class Node
        {
            /// <summary>The characters that may follow this one, each with its own node.</summary>
            public Dictionary<char, Node> Next { get; } = [];

            /// <summary>Whether a name ends at this node.</summary>
            public bool Ends { get; private set; }

            /// <summary>Adds <paramref name="name"/> below this node, where it is one an impact is read with as an id (<see cref="Names"/>).</summary>
            public void Add(string name)
            {
                if (!(name.FirstOrDefault(char.IsLetterOrDigit) is var first && char.IsLetter(first)) || !name.Any(char.IsNumber))
                {
                    return;
                }

                var node = this;

                foreach (var character in name)
                {
                    if (!node.Next.TryGetValue(character, out var next))
                    {
                        next = new Node();
                        node.Next[character] = next;
                    }

                    node = next;
                }

                node.Ends = true;
            }
        }
    }

    /// <summary>A text member's text; empty where it is none.</summary>
    private static string TextOf(JsonNode? node)
        => node is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : string.Empty;
}
