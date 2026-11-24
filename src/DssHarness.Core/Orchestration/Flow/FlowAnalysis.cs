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
/// <param name="Impact">The time and dollars it cost, every number of it one of its citations' - written bare as the citation holds it, or joined to its unit and converted exactly into the unit the cited member is in (<see cref="FlowAnalysisEvidence"/>).</param>
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
/// of its own citations' values, read in the unit the report holds it in (<see cref="FlowRunReport.Units"/>): a number
/// written bare is held where a citation's value is that number as written; a number joined to its unit (<c>3600s</c>,
/// <c>1.5h</c>, <c>12k tokens</c>, <c>$0.02</c>, <c>28%</c>) where a citation of a member of that unit's kind holds it,
/// converted exactly into the member's unit - 1.5h is a budget's 90 minutes, and 60 hours is no wait's 60 seconds - and
/// one of a kind no member of the report is (<c>2x</c>, a size, another currency) never; and a word that starts as a
/// number and reads as no one number (a date, a range, <c>1h30m</c>) is refused as such. A number is read whatever
/// wraps it - Markdown, a comparison, a currency's sign - for every character at a word's ends that is neither a letter
/// nor a digit is read off it first, a sign directly before a digit kept. An analysis cannot invent a measurement.
/// </summary>
public static class FlowAnalysisEvidence
{
    /// <summary>The kinds a member of the report is in (<see cref="FlowRunReport.Units"/>): a number of any other kind is one no citation can hold.</summary>
    private static readonly HashSet<string> Measured = [.. FlowRunReport.Units.Values.Select(unit => unit.Kind)];

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
            var cited = new List<SaidNumber>();

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
                    cited.AddRange(numbers);
                }
            }

            foreach (var said in NumbersIn(TextOf(point?["impact"]), bare: null))
            {
                if (!said.Reads)
                {
                    problems.Add($"painPoints[{at}].impact: says {said.Text}, which reads as no one number - write each number alone or joined to its unit, as one of its citations' values");
                }
                else if (!cited.Any(said.IsHeldBy))
                {
                    problems.Add(said.Unit switch
                    {
                        null => $"painPoints[{at}].impact: says {said.Text}, which none of its citations holds - cite where the report holds it, or leave it out",
                        { } unit when !Measured.Contains(unit.Kind) => $"painPoints[{at}].impact: says {said.Text}, {unit.KindSaid}, and no member of the report is one - write the report's own numbers, each cited, or leave it out",
                        { } unit => $"painPoints[{at}].impact: says {said.Text}, {unit.KindSaid} none of its citations holds - a number joined to its unit is held where a cited member of its kind holds it, converted exactly into that member's unit; cite where the report holds it, or leave it out",
                    });
                }
            }
        }

        return problems;
    }

    /// <summary>
    /// Why <paramref name="citation"/> is not a value the report holds; null where it is, with the numbers it holds, each
    /// in its unit - the value where it is one, in its member's unit; each number a text says where it is a text, in the
    /// unit written beside it or, written bare, in its member's.
    /// </summary>
    private static string? Held(JsonNode citation, JsonNode report, out IReadOnlyList<SaidNumber> numbers)
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

            numbers = decimal.TryParse(holds, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? [new SaidNumber(holds, Reads: true, number, unit)] : [];
            return null;
        }

        var text = kind == JsonValueKind.String ? value.GetValue<string>() : value.ToJsonString();

        if (!string.Equals(text, said, StringComparison.Ordinal))
        {
            // Said back to the analyst in its own conversation, so written as every JSON an agent reads is: a text with an
            // apostrophe reads as the report holds it, never as an escape.
            return $"{named} is {value.ToJsonString(Agents.AgentEnvelope.Written)}, not '{said}'";
        }

        numbers = [.. NumbersIn(text, unit).Where(one => one.Reads)];
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
    /// Each number <paramref name="text"/> says: every word that starts as one once what wraps it is read off (<see cref="Word"/>)
    /// - a sign, then a digit - read for its numeric head (digits, thousands grouped, one fraction, an exponent) and its
    /// unit: a sign against it (<c>$0.02</c>, <c>28%</c>), a word joined to it, glued (<c>3600s</c>, <c>2x</c>) or after
    /// one hyphen (<c>40-minute</c>), a scale joined to it multiplying it (<c>12k</c>); a number with no unit of its own
    /// takes the words after it that name one - a scale, then a unit (<c>30 s</c>, <c>1 hour</c>, <c>1.2 million
    /// tokens</c>), or a sign alone (<c>28 %</c>). A word the check does not know as a unit leaves the number bare, and a
    /// number with no unit is in <paramref name="bare"/>. A word that starts as a number and holds more than one number
    /// and its unit - a date, a time of day, a range, <c>1h30m</c>, a currency's amount in minutes - reads as no one
    /// number. A digit inside an id is never read: an id starts with a letter.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="bare">The unit of a number written with none of its own: a cited member's; null in an impact, whose bare number is held as written.</param>
    private static IEnumerable<SaidNumber> NumbersIn(string text, FlowUnit? bare)
    {
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(Word.Of).ToList();

        for (var at = 0; at < words.Count; at++)
        {
            var word = words[at];
            var head = HeadOf(word.Core);

            if (head == 0)
            {
                continue;
            }

            var glued = word.Core[head..];

            if (glued.Length > 1 && glued[0] == '-' && glued.Skip(1).All(char.IsLetter))
            {
                glued = glued[1..];
            }

            if (!glued.All(char.IsLetter))
            {
                yield return new SaidNumber(word.Said, Reads: false, null, null);
                continue;
            }

            var scale = glued.Length > 0 ? FlowUnit.ScaleOf(glued) : null;
            var units = new List<FlowUnit>(word.Signs);
            var said = word.Said;

            if (glued.Length > 0 && scale is null && FlowUnit.Named(glued) is { } joined)
            {
                units.Add(joined);
            }

            // A number joined to no word, or to its scale alone, takes the words after it that scale it and name its unit;
            // a number joined to any other word is that word's.
            if (glued.Length == 0 || scale is not null)
            {
                var next = at + 1;

                if (scale is null && next < words.Count && FlowUnit.ScaleOf(words[next].Core) is { } spoken)
                {
                    scale = spoken;
                    said = $"{said} {words[next].Core}";
                    next++;
                }

                if (units.Count == 0 && next < words.Count && words[next].Unit is { } after)
                {
                    units.Add(after);
                    said = $"{said} {words[next].Said}";
                }
            }

            if (units.Distinct().Count() > 1)
            {
                yield return new SaidNumber(said, Reads: false, null, null);
                continue;
            }

            // Past what a decimal holds, so no citation can hold it: said as a number none of them holds.
            yield return new SaidNumber(said, Reads: true, ValueOf(word.Core[..head], scale), units.Count > 0 ? units[0] : bare);
        }
    }

    /// <summary>The value of a numeric head, multiplied by its scale where it has one; null past what a decimal holds.</summary>
    private static decimal? ValueOf(string head, decimal? scale)
    {
        if (!decimal.TryParse(head, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var value))
        {
            return null;
        }

        try
        {
            return value * (scale ?? 1m);
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    /// <summary>
    /// One word of a text, read off what wraps it: every character at either end that is neither a letter nor a digit -
    /// Markdown's marks, quotes, brackets, a comparison, punctuation - a sign directly before a digit kept as the number's;
    /// with the units the signs read off name, a currency's or a multiple's before it and a percent's, a multiple's or a
    /// currency's after it.
    /// </summary>
    /// <param name="Core">What is left of the word: its letters and digits and what stands between them.</param>
    /// <param name="Signs">The units the signs read off its ends name, in the order they stood.</param>
    /// <param name="Said">The word as an answer says it back: its core, with the signs read as its unit around it.</param>
    private readonly record struct Word(string Core, IReadOnlyList<FlowUnit> Signs, string Said)
    {
        /// <summary>The unit the word names where it stands after a number of no unit of its own: a unit's name, or a unit's sign alone; null where it names none.</summary>
        public FlowUnit? Unit => Core.Length > 0 ? FlowUnit.Named(Core) : Signs.Count == 1 ? Signs[0] : null;

        /// <summary>The word <paramref name="text"/>, read off what wraps it.</summary>
        public static Word Of(string text)
        {
            var start = 0;

            while (start < text.Length && !char.IsLetterOrDigit(text[start]) && !(text[start] is '-' or '+' && start + 1 < text.Length && char.IsAsciiDigit(text[start + 1])))
            {
                start++;
            }

            var end = text.Length;

            while (end > start && !char.IsLetterOrDigit(text[end - 1]))
            {
                end--;
            }

            // A word of signs alone - the % after a number, written apart - is the one sign of it that names a unit.
            if (start == end)
            {
                var signs = text.Where(sign => FlowUnit.OfSign(sign) is not null).ToList();
                return signs.Count == 1 ? new Word(string.Empty, [FlowUnit.OfSign(signs[0])!], signs[0].ToString()) : new Word(string.Empty, [], string.Empty);
            }

            // Before a number a currency's sign or a multiple's names its unit, after it the sign that ends it: a
            // percent's, a multiple's or a currency's.
            var before = text[..start].Where(sign => FlowUnit.OfSign(sign) is { Kind: not "percent" }).Select(sign => (char?)sign).LastOrDefault();
            var after = end < text.Length && FlowUnit.OfSign(text[end]) is not null ? text[end] : (char?)null;
            var core = text[start..end];

            return new Word(
                core,
                [.. new[] { before, after }.OfType<char>().Select(sign => FlowUnit.OfSign(sign)!)],
                $"{before}{core}{after}");
        }
    }

    /// <summary>
    /// How many characters of <paramref name="word"/> its numeric head is: a sign, digits - thousands grouped by commas
    /// between digits - one fraction and an exponent; 0 where the word does not start as a number (a sign alone, or a
    /// letter - an id).
    /// </summary>
    private static int HeadOf(string word)
    {
        var at = word.Length > 0 && word[0] is '-' or '+' ? 1 : 0;

        if (at >= word.Length || !char.IsAsciiDigit(word[at]))
        {
            return 0;
        }

        while (at < word.Length && (char.IsAsciiDigit(word[at]) || (word[at] == ',' && at + 1 < word.Length && char.IsAsciiDigit(word[at + 1]))))
        {
            at++;
        }

        if (at + 1 < word.Length && word[at] == '.' && char.IsAsciiDigit(word[at + 1]))
        {
            at++;

            while (at < word.Length && char.IsAsciiDigit(word[at]))
            {
                at++;
            }
        }

        if (at + 1 < word.Length && word[at] is 'e' or 'E')
        {
            var digits = word[at + 1] is '-' or '+' ? at + 2 : at + 1;

            if (digits < word.Length && char.IsAsciiDigit(word[digits]))
            {
                at = digits;

                while (at < word.Length && char.IsAsciiDigit(word[at]))
                {
                    at++;
                }
            }
        }

        return at;
    }

    /// <summary>
    /// One number a text says: the words it is said in; whether it reads as one number with its unit; its value - null
    /// where it reads as none, or past what a decimal holds, so no citation could hold it - and its unit, null for a
    /// number held as written.
    /// </summary>
    private readonly record struct SaidNumber(string Text, bool Reads, decimal? Value, FlowUnit? Unit)
    {
        /// <summary>
        /// Whether <paramref name="cited"/>, a number a citation holds, holds this one: written bare, its value as
        /// written; joined to its unit, a value of a unit of its kind, the two the same once converted exactly.
        /// </summary>
        public bool IsHeldBy(SaidNumber cited)
            => Value is { } value && cited.Value is { } held
                && (Unit is not { } unit ? value == held : cited.Unit is { } its && unit.Holds(value, its, held));
    }

    /// <summary>A text member's text; empty where it is none.</summary>
    private static string TextOf(JsonNode? node)
        => node is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : string.Empty;
}
