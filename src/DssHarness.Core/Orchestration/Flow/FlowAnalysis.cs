using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using DssHarness.Core.Json;
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
/// <param name="Impact">The time and dollars it cost, every number of it one of its citations' - written bare as the citation holds it, or joined to its unit and converted exactly into the unit the cited member is in, numbers joined to units of one kind as their sum (<see cref="FlowAnalysisEvidence"/>).</param>
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
/// <c>1.5h</c>, <c>12k tokens</c>, <c>$0.02</c>, <c>US$0.02</c>, <c>28%</c>) where a citation of a member of that unit's
/// kind holds it, converted exactly into the member's unit - 1.5h is a budget's 90 minutes, and 60 hours is no wait's 60
/// seconds - and one of a kind no member of the report is (<c>2x</c>, a size, another currency, by its sign, its ISO
/// 4217 code or its name: <c>€0.02</c>, <c>R$0.02</c>, <c>0.02 EUR</c>, <c>0.02 euros</c>) never; numbers joined to
/// units of one kind from the largest to the smallest (<c>1h 30m</c>, <c>1h30m</c>, <c>2 min 5 s</c>) are one value, held
/// as their sum; a currency is a number's only by the number's own phrase - a currency's name, code or sign, behind a
/// country's name of a closed list or not, directly before it, a colon between allowed (<c>EUR: 0.02</c>, <c>Canadian
/// dollars: 0.02</c>), or after it, beside it, in brackets or after <c>in</c> (<c>0.02 (EUR)</c>, <c>0.02 in euros</c>,
/// <c>0.02 Canadian dollars</c>) - never a currency's word farther from it in its sentence; and a word that starts as a
/// number and reads as no one number (a date, a
/// time of day, a range) is refused as such. The numbers are read by what the report and the citations say is an id,
/// never by a guess at one (<see cref="NumbersIn"/>): a word is an id only where the report holds it as one - a row's id
/// or a name it is made of, a section's or a member's name - or a citation writes it as its path, and is then read whole
/// and never for a number; every other word is parted at <c>: + @ # / =</c> - a colon or a slash between two digits
/// kept - and every mark no word keeps, hyphens, points and underscores staying inside it - so <c>queue:3600s</c> and
/// <c>T+3600s</c> each say a number, and <c>windows-2022</c> none. An analysis cannot invent a measurement.
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
        var held = Names.Of(report);

        foreach (var (point, at) in (analysis["painPoints"] as JsonArray ?? []).Select((point, at) => (point, at)))
        {
            var cited = new List<SaidNumber>();
            var evidence = point?["evidence"] as JsonArray ?? [];
            var names = held.With(evidence.SelectMany(PathsOf));

            foreach (var (step, named) in (point?["where"] as JsonArray ?? []).Select((step, named) => (TextOf(step), named)))
            {
                if (!steps.Contains(step))
                {
                    problems.Add($"painPoints[{at}].where[{named}]: the report's steps hold no row '{step}' - name a step by its row's id");
                }
            }

            foreach (var (citation, of) in evidence.Select((citation, of) => (citation, of)))
            {
                if (Held(citation!, report, names, out var numbers) is { } problem)
                {
                    problems.Add($"painPoints[{at}].evidence[{of}]: {problem}");
                }
                else
                {
                    cited.AddRange(numbers);
                }
            }

            foreach (var said in NumbersIn(TextOf(point?["impact"]), bare: null, names))
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
    /// Why <paramref name="citation"/> is not a value the report holds; null where it is, with the numbers it holds, each
    /// in its unit - the value where it is one, in its member's unit; each number a text says where it is a text, read with
    /// <paramref name="ids"/> as its ids, in the unit written beside it or, written bare, in its member's.
    /// </summary>
    private static string? Held(JsonNode citation, JsonNode report, Names ids, out IReadOnlyList<SaidNumber> numbers)
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

        numbers = [.. NumbersIn(text, unit, ids).Where(one => one.Reads)];
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
    /// Each number <paramref name="text"/> says. The text is read as words (<see cref="WordsOf"/>): a word is an id only
    /// where <paramref name="names"/> holds it - a name the report holds or a path a citation writes - and is then read
    /// whole and never for a number, its <c>: + @ # /</c> and brackets included (<c>c@1</c>, <c>slowestStep#2</c>, a
    /// hotspot's <c>hotspots:&lt;item&gt;:&lt;class&gt;:&lt;path&gt;</c> and its path); every other word ends at every
    /// character but a letter, a digit, a mark, a hyphen, a point, an underscore, a colon or a slash between two digits and
    /// the signs a number is written with, so <c>queue:3600s</c>, <c>T+3600s</c>, <c>wait=3600s</c> and
    /// <c>queue—3600s—before</c> each say a number while <c>windows-2022</c>, <c>net10.0</c> and <c>claude-opus-4-5</c> say
    /// none, and a currency's sign or its ISO 4217 code in capitals that starts a number inside a word starts a word of its
    /// own (<c>cost$0.40</c>). Every word that starts as a number once what wraps it is read off (<see cref="Word"/>) - a
    /// digit, a sign or a point directly before one, or a currency's sign or code, or a multiple's sign, directly before
    /// those, a sign before the currency's its own - is read for its numeric head (digits, thousands grouped in threes, one
    /// fraction, an exponent) and its unit: a sign against it (<c>$0.02</c>, <c>-$0.02</c>, <c>US$0.40</c>,
    /// <c>$US0.40</c>, <c>USD0.40</c>, <c>28%</c>), a word joined to it, glued (<c>3600s</c>, <c>2x</c>) or after one hyphen
    /// (<c>40-minute</c>), a scale joined to it multiplying it (<c>12k</c>); a number with no unit of its own takes the
    /// words written beside it that name one - a scale, then a unit after it, of one word or two (<c>30 s</c>, <c>1
    /// hour</c>, <c>1.2 million tokens</c>, <c>0.02 EUR</c>, <c>28 per cent</c>), or a sign alone (<c>28 %</c>) - and a
    /// number with no unit of its own or a currency's the currency's name, code or sign just before it, behind a country's
    /// name or not, a colon between allowed (<c>EUR 0.02</c>, <c>EUR: 0.02</c>, <c>R$ 0.40</c>, <c>Euros: 0.02</c>,
    /// <c>Canadian dollars: 0.02</c>), and no word before those (<see cref="CurrencyBefore"/>); beside means with nothing
    /// between but space and Markdown's emphasis and code marks, which a word's reading
    /// takes off its ends (<c>**1.5** hours</c>, <c>`3600` s</c>), a line's end space but where a list's item opens the
    /// next line (<see cref="Beside(string, int, int)"/>), so a comma or any other mark sets them apart. A number with no
    /// unit of its own, or a currency's, takes the currency written after it in its own phrase (<see
    /// cref="Reading.InPhrase"/>), and none its sentence names elsewhere. A word the check does not know as a unit leaves
    /// the number bare, and a number with no unit is in <paramref name="bare"/>. Numbers each joined to a unit of one kind,
    /// from the largest unit to the smallest, glued (<c>1h30m</c>) or beside each other (<c>1h 30m</c>, <c>2 min 5 s</c>),
    /// are one value, their sum. A word that starts as a number and holds more than one number and its unit otherwise - a
    /// date, a time of day, a range, a currency's amount in minutes - reads as no one number.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="bare">The unit of a number written with none of its own: a cited member's; null in an impact, whose bare number is held as written.</param>
    /// <param name="names">The words read as ids.</param>
    private static IReadOnlyList<SaidNumber> NumbersIn(string text, FlowUnit? bare, Names names)
    {
        var words = WordsOf(text, names);
        var read = new List<Reading>();

        for (var at = 0; at < words.Count; at++)
        {
            var head = words[at].Id ? 0 : HeadOf(words[at].Core, 0, words[at].Core.Length);

            if (head == 0)
            {
                continue;
            }

            var number = ReadingOf(text, words, at, head, read.Count > 0 ? read[^1].Last : -1, bare);

            if (read.Count > 0 && read[^1].Continued(number, words) is { } whole)
            {
                read[^1] = whole;
            }
            else
            {
                read.Add(number);
            }

            at = number.Last;
        }

        return [.. read.Select((one, index) => one.InPhrase(text, words, index + 1 < read.Count ? read[index + 1].First : words.Count).Number)];
    }

    /// <summary>
    /// The number the word at <paramref name="at"/> of <paramref name="text"/> starts, whose numeric head is <paramref
    /// name="head"/> characters long, read with its unit (<see cref="NumbersIn"/>); a currency's words before it
    /// (<see cref="CurrencyBefore"/>) taken only where no number before it took them, the words up to <paramref
    /// name="taken"/> being theirs.
    /// </summary>
    private static Reading ReadingOf(string text, IReadOnlyList<Word> words, int at, int head, int taken, FlowUnit? bare)
    {
        var word = words[at];
        var signed = word.Core[0] is '-' or '+' or Minus;
        var negative = word.Core[0] is '-' or Minus;
        var glued = word.Core[head..];

        if (glued.Length > 1 && glued[0] == '-' && glued.Skip(1).All(char.IsLetter))
        {
            glued = glued[1..];
        }

        if (!glued.All(char.IsLetter))
        {
            return SumOf(word, at, head, negative) ?? new Reading(new SaidNumber(word.Said, Reads: false, null, null), at, at, null, 0m, null, signed, negative, Led: false);
        }

        var scale = glued.Length > 0 ? FlowUnit.ScaleOf(glued) : null;
        var units = new List<FlowUnit>(word.Signs);
        var said = word.Said;
        var first = at;
        var last = at;

        if (glued.Length > 0 && scale is null && FlowUnit.Named(glued) is { } joined)
        {
            units.Add(joined);
        }

        // A number joined to no word, or to its scale alone, takes the words beside it that scale it and name its unit,
        // and a number of no unit or a currency's the currency's name, code or sign just before it, a colon between
        // allowed - a second currency making it no one number; a number joined to any other word is that word's.
        if (glued.Length == 0 || scale is not null)
        {
            var next = at + 1;

            if (scale is null && next < words.Count && words[next].Joined && FlowUnit.ScaleOf(words[next].Core) is { } spoken)
            {
                scale = spoken;
                said = $"{said} {words[next].Core}";
                last = next++;
            }

            if (units.Count == 0 && next < words.Count && words[next].Joined && UnitAfter(words, next) is { } after)
            {
                units.Add(after.Unit);
                said = $"{said} {string.Join(" ", words.Skip(next).Take(after.Last + 1 - next).Select(one => one.Said))}";
                last = after.Last;
            }

            if (units.All(one => one.IsCurrency) && at - 1 > taken && (word.Joined || Beside(text, words[at - 1].End, word.Start, ':'))
                && CurrencyBefore(text, words, at - 1, taken) is { } before)
            {
                units.Add(before.Unit);
                said = $"{string.Join(" ", words.Skip(before.First).Take(at - before.First).Select(one => one.Said))} {said}";
                first = before.First;
            }
        }

        if (units.Distinct().Count() > 1)
        {
            return new Reading(new SaidNumber(said, Reads: false, null, null), first, last, null, 0m, null, signed, negative, Led: first < at);
        }

        // Past what a decimal holds, so no citation can hold it: said as a number none of them holds.
        var value = ValueOf(word.Core[..head], scale);
        var unit = units.Count > 0 ? units[0] : null;

        return new Reading(
            new SaidNumber(said, Reads: true, value, unit ?? bare),
            first,
            last,
            unit?.Kind,
            unit?.Base ?? 0m,
            unit is null ? null : InBase(value, unit),
            signed,
            negative,
            Led: first < at);
    }

    /// <summary>
    /// The unit the words from <paramref name="at"/> name after a number (<see cref="FlowUnit.Named"/>), with the last
    /// word it takes: two words beside each other where the two name one (<c>per cent</c>), else the one word's
    /// (<see cref="Word.Unit"/>); null where they name none.
    /// </summary>
    private static (FlowUnit Unit, int Last)? UnitAfter(IReadOnlyList<Word> words, int at)
    {
        if (at + 1 < words.Count && words[at + 1].Joined && words[at] is { Id: false, Core.Length: > 0 } && words[at + 1] is { Id: false, Core.Length: > 0 }
            && FlowUnit.Named($"{words[at].Core} {words[at + 1].Core}") is { } two)
        {
            return (two, at + 1);
        }

        return words[at].Unit is { } one ? (one, at) : null;
    }

    /// <summary>
    /// The currency written after the number whose last word is at <paramref name="last"/>, in the number's own phrase
    /// and before the word at <paramref name="to"/>, with the last word it takes: a currency's name, code or sign
    /// (<see cref="Word.Currency"/>), or a country's name and a currency's name after it (<see cref="CountrysCurrencyAt"/>)
    /// - beside the number, after <c>in</c> beside it, or in brackets opened beside it and closed directly after it
    /// (<c>0.02 EUR</c>, <c>0.02 in euros</c>, <c>0.02 (EUR)</c>, <c>0.02 Canadian dollars</c>, <c>0.02 (US
    /// dollars)</c>); null where none is, whatever currency the clause names further on - <c>20 and cost time and
    /// dollars</c> says no currency of 20's.
    /// </summary>
    private static (FlowUnit Unit, int Last)? CurrencyAfter(string text, IReadOnlyList<Word> words, int last, int to)
    {
        var at = last + 1;

        if (at >= to)
        {
            return null;
        }

        var bracketed = Beside(text, words[last].End, words[at].Start, '(');

        if (!bracketed && !Beside(text, words[last].End, words[at].Start))
        {
            return null;
        }

        if (!bracketed && words[at] is { Id: false, Core: "in" } && at + 1 < to && Beside(text, words[at].End, words[at + 1].Start))
        {
            at++;
        }

        var named = words[at].Currency is { } currency ? (currency, at) : CountrysCurrencyAt(text, words, at, to);

        if (named is not { } found || !bracketed)
        {
            return named;
        }

        // A bracket holds the number's currency only where it closes on it: (EUR), never (dollars lost: 0.02).
        var after = words[found.Last].End;
        var close = text.IndexOf(')', after, (found.Last + 1 < words.Count ? words[found.Last + 1].Start : text.Length) - after);

        return close >= 0 && Beside(text, after, close) ? found : null;
    }

    /// <summary>
    /// The currency a country's name starting at the word at <paramref name="at"/> and a currency's name written after it
    /// say (<see cref="FlowUnit.OfCountry"/>), each word beside the one before it and the last before the word at
    /// <paramref name="to"/>, with the word the currency's name is: <c>Canadian dollars</c>, <c>New Zealand dollars</c>,
    /// <c>U.S. dollars</c> - an abbreviation's last point the country's own - <c>Swiss francs</c>; null where no country
    /// of the list is written there before a currency's name.
    /// </summary>
    private static (FlowUnit Unit, int Last)? CountrysCurrencyAt(string text, IReadOnlyList<Word> words, int at, int to)
    {
        var country = string.Empty;

        for (var last = at; last + 1 < to; last++)
        {
            var word = words[last];

            if (word is not { Id: false, Core.Length: > 0 } || (last > at && !Beside(text, words[last - 1].End, word.Start)))
            {
                return null;
            }

            country = last == at ? word.Core : $"{country} {word.Core}";

            var name = words[last + 1];
            var pointed = word.End < name.Start && text[word.End] == '.';
            var end = pointed ? word.End + 1 : word.End;

            if (name is { Id: false } && Beside(text, end, name.Start) && FlowUnit.OfCountry(pointed ? $"{country}." : country, name.Core) is { } currency)
            {
                return (currency, last + 1);
            }

            if (!FlowUnit.StartsCountry(country))
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>
    /// The currency the word at <paramref name="name"/>, written directly before a number, names - a currency's name, code
    /// or sign (<see cref="Word.Currency"/>), read with the longest country's name of the list written beside it before it
    /// where one is (<see cref="FlowUnit.OfCountry"/>: <c>Canadian dollars: 0.02</c>, <c>U.S. dollars 0.02</c>, <c>South Korean
    /// won: 20</c>) and alone where none is (<c>Wasted dollars: 0.02</c> the report's dollars, <c>Euros: 0.02</c>), the
    /// country's words after <paramref name="taken"/> - with the first word it takes; null where it names none. No word
    /// before those is read.
    /// </summary>
    private static (FlowUnit Unit, int First)? CurrencyBefore(string text, IReadOnlyList<Word> words, int name, int taken)
    {
        var currency = words[name];

        if (currency.Id)
        {
            return null;
        }

        var country = string.Empty;
        var pointed = name > 0 && words[name - 1].End < currency.Start && text[words[name - 1].End] == '.';
        (FlowUnit Unit, int First)? longest = currency.Currency is { } alone ? (alone, name) : null;

        // Read back word by word while the words read are a country's name or end one, the longest the list holds kept:
        // South Korean won, never Korean won.
        for (var first = name - 1; first > taken; first--)
        {
            var word = words[first];

            // An abbreviation's last point is the country's own (U.S.), read with it where the country is so written.
            if (word is not { Id: false, Core.Length: > 0 } || !Beside(text, first == name - 1 && pointed ? word.End + 1 : word.End, words[first + 1].Start))
            {
                break;
            }

            country = first == name - 1 ? word.Core : $"{word.Core} {country}";

            if (FlowUnit.OfCountry(pointed ? $"{country}." : country, currency.Core) is { } named)
            {
                longest = (named, first);
            }

            if (!FlowUnit.EndsCountry(country))
            {
                break;
            }
        }

        return longest;
    }

    /// <summary>
    /// The one value the word at <paramref name="at"/> says where it glues numbers each to a unit of one kind, from the
    /// largest unit to the smallest, with no sign around them (<c>1h30m</c>, <c>2m5s</c>) - their sum, in the base of
    /// their kind, a minus before the first the whole's; null where it says anything else.
    /// </summary>
    private static Reading? SumOf(Word word, int at, int head, bool negative)
    {
        if (word.Signs.Count > 0)
        {
            return null;
        }

        var core = word.Core;
        var number = core[..head];
        var position = head;
        FlowUnit? previous = null;
        decimal? whole = 0m;

        while (true)
        {
            var letters = position;

            while (position < core.Length && char.IsLetter(core[position]))
            {
                position++;
            }

            if (position == letters
                || FlowUnit.Named(core[letters..position]) is not { } unit
                || (previous is not null && (!string.Equals(unit.Kind, previous.Kind, StringComparison.Ordinal) || unit.Base >= previous.Base)))
            {
                return null;
            }

            whole = Plus(whole, InBase(ValueOf(number, null), unit), negative && previous is not null);
            previous = unit;

            if (position == core.Length)
            {
                break;
            }

            var digits = char.IsAsciiDigit(core[position]) ? HeadOf(core, position, core.Length) : 0;

            if (digits == 0)
            {
                return null;
            }

            number = core[position..(position + digits)];
            position += digits;
        }

        return new Reading(
            new SaidNumber(word.Said, Reads: true, whole, new FlowUnit(previous.Kind, 1m), Summed: true),
            at,
            at,
            previous.Kind,
            previous.Base,
            whole,
            core[0] is '-' or '+' or Minus,
            negative,
            Led: false);
    }

    /// <summary>A value in its unit as a value of its kind's base; null where it is none, or past what a decimal holds.</summary>
    private static decimal? InBase(decimal? value, FlowUnit unit)
    {
        try
        {
            return value * unit.Base;
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    /// <summary>
    /// <paramref name="whole"/> with <paramref name="part"/> added - taken away where the whole is said with a minus, so
    /// <c>-1h 30m</c> is an hour and a half before; null where either is none, or past what a decimal holds.
    /// </summary>
    private static decimal? Plus(decimal? whole, decimal? part, bool negative)
    {
        try
        {
            return whole + (negative ? -part : part);
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    /// <summary>The value of a numeric head, multiplied by its scale where it has one; null past what a decimal holds.</summary>
    private static decimal? ValueOf(string head, decimal? scale)
    {
        if (!decimal.TryParse(head.Replace(Minus, '-'), NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var value))
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

    /// <summary>The minus sign, written before a number as the hyphen is.</summary>
    private const char Minus = '−';

    /// <summary>
    /// The words of <paramref name="text"/>: each id <paramref name="names"/> holds where it stands whole (<see
    /// cref="Names.At"/>), a word of its own read for no number; else every run of the characters a word holds
    /// (<see cref="Joins"/>), a currency's sign or its code in capitals that starts a number inside one - with the capitals
    /// directly before the sign, which say whose it is, and a sign before those that starts the run - starting a word of
    /// its own (<c>cost$0.40</c>, <c>costUS$0.40</c>, <c>costUSD0.40</c>); each read off what wraps it, and joined to the
    /// word before where nothing but space and Markdown's marks stand between them (<see
    /// cref="Beside(string, int, int)"/>).
    /// </summary>
    private static List<Word> WordsOf(string text, Names names)
    {
        var words = new List<Word>();
        var ended = -1;

        for (var at = 0; at < text.Length;)
        {
            if (names.At(text, at) is > 0 and var name)
            {
                Add(Word.OfName(text, at, at + name));
                at += name;
                continue;
            }

            if (!Joins(text, at))
            {
                at++;
                continue;
            }

            var start = at++;

            while (at < text.Length && Joins(text, at) && names.At(text, at) == 0)
            {
                at++;
            }

            var from = start;

            for (var lead = start; lead < at; lead++)
            {
                if (AmountStartsAt(text, lead, from, at) is { } begins && begins > from)
                {
                    Add(Word.Of(text, from, begins));
                    from = begins;
                }
            }

            Add(Word.Of(text, from, at));
        }

        return words;

        void Add(Word word)
        {
            words.Add(word with { Joined = ended >= 0 && Beside(text, ended, word.Start) });
            ended = word.End;
        }
    }

    /// <summary>
    /// Where the word of an amount whose currency's sign or code is at <paramref name="at"/> starts, inside a run of a
    /// word's characters from <paramref name="from"/> to <paramref name="to"/>: at the capitals directly before a sign,
    /// which say whose it is, or at the code - a sign before either taken with it where the sign starts the run; null where
    /// no currency's sign or code that starts a number stands at <paramref name="at"/>.
    /// </summary>
    private static int? AmountStartsAt(string text, int at, int from, int to)
    {
        int begins;

        if (FlowUnit.IsCurrencySign(text[at]) && LeadAt(text, at, to) is not null)
        {
            begins = at;

            while (begins > from && char.IsAsciiLetterUpper(text[begins - 1]))
            {
                begins--;
            }
        }
        else if (char.IsAsciiLetterUpper(text[at]) && (at == from || !char.IsAsciiLetterUpper(text[at - 1])) && LeadAt(text, at, to) is not null)
        {
            begins = at;
        }
        else
        {
            return null;
        }

        return begins - 1 == from && text[from] is '-' or '+' or Minus ? from : begins;
    }

    /// <summary>
    /// The lead of an amount at <paramref name="at"/> of <paramref name="text"/>, before <paramref name="to"/>: what is
    /// written directly before a number and names its unit, with how many characters it is - a currency's sign
    /// (<c>$</c>, <c>€</c>), with the capitals that say whose it is before or after it (<c>US$</c>, <c>R$</c>,
    /// <c>$US</c>), a currency's ISO 4217 code in capitals (<c>USD</c>, <c>EUR</c>), or a multiple's sign (<c>×</c>); null
    /// where no number follows it, or nothing there names a unit.
    /// </summary>
    private static (int Length, FlowUnit Unit)? LeadAt(string text, int at, int to)
    {
        if (at >= to)
        {
            return null;
        }

        var character = text[at];

        if (FlowUnit.IsCurrencySign(character))
        {
            if (StartsNumber(text, at + 1, to))
            {
                return (1, FlowUnit.OfSign(character)!);
            }

            var after = CapitalsFrom(text, at + 1, to);

            return after > at + 1 && StartsNumber(text, after, to) && FlowUnit.OfSign(character, text[(at + 1)..after]) is { } whose ? (after - at, whose) : null;
        }

        if (character == '×')
        {
            return StartsNumber(text, at + 1, to) ? (1, FlowUnit.Multiple) : null;
        }

        var capitals = CapitalsFrom(text, at, to);

        if (capitals == at)
        {
            return null;
        }

        if (capitals < to && FlowUnit.IsCurrencySign(text[capitals]) && StartsNumber(text, capitals + 1, to) && FlowUnit.OfSign(text[capitals], text[at..capitals]) is { } prefixed)
        {
            return (capitals + 1 - at, prefixed);
        }

        return StartsNumber(text, capitals, to) && FlowUnit.OfCode(text[at..capitals]) is { } code ? (capitals - at, code) : null;
    }

    /// <summary>Where the run of capitals that starts at <paramref name="at"/> of <paramref name="text"/> ends, before <paramref name="to"/>; <paramref name="at"/> where none starts there.</summary>
    private static int CapitalsFrom(string text, int at, int to)
    {
        while (at < to && char.IsAsciiLetterUpper(text[at]))
        {
            at++;
        }

        return at;
    }

    /// <summary>
    /// Whether the character at <paramref name="at"/> of <paramref name="text"/> stays in a word: a letter, a digit, a mark
    /// or an underscore (<see cref="Whole"/>); a point; a hyphen but two or more in a row, which are a dash; a comma, a
    /// colon or a slash between two digits - thousands grouped, a time of day (<c>10:30</c>, <c>1:00:00</c>), a date or a
    /// ratio (<c>11/24</c>), each a word that reads as no one number; a currency's sign; <c>%</c> and <c>×</c> but directly
    /// after a letter; and a sign - the minus sign, or a plus - that starts an amount (<see cref="StartsAmount"/>) after no
    /// letter or digit. Any other ends a word: space, <c>: + @ # / =</c> but those, a bracket, a quote, an apostrophe,
    /// Markdown's marks, any other dash and mark.
    /// </summary>
    private static bool Joins(string text, int at)
        => text[at] switch
        {
            '-' => !(at > 0 && text[at - 1] == '-') && !(at + 1 < text.Length && text[at + 1] == '-'),
            '.' => true,
            ',' or ':' or '/' => at > 0 && at + 1 < text.Length && char.IsAsciiDigit(text[at - 1]) && char.IsAsciiDigit(text[at + 1]),
            '%' or '×' => at == 0 || !char.IsLetter(text[at - 1]),
            Minus or '+' => (at == 0 || !char.IsLetterOrDigit(text[at - 1])) && StartsAmount(text, at, text.Length),
            var character => Whole(character) || FlowUnit.IsCurrencySign(character),
        };

    /// <summary>Whether <paramref name="character"/> is one a word keeps whatever stands around it: a letter, a digit, a mark, or an underscore or another joining punctuation.</summary>
    private static bool Whole(char character)
        => char.IsLetterOrDigit(character)
            || char.GetUnicodeCategory(character) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark or UnicodeCategory.ConnectorPunctuation;

    /// <summary>
    /// Whether what stands between <paramref name="from"/> and <paramref name="to"/> of <paramref name="text"/> sets no
    /// word apart from the one before it: nothing but space and Markdown's emphasis and code marks (<c>* _ ` ~</c>), which a
    /// word's reading takes off its ends - a line's end is space, a number at the end of a line and its unit at the start of
    /// the next one being one (<c>60</c> and <c>hours</c>), but where a list's item opens the next line (<see
    /// cref="ListItemAt"/>), which sets apart what each line says (<c>* 1h</c> and <c>* 30m</c>).
    /// </summary>
    private static bool Beside(string text, int from, int to)
    {
        for (var at = from; at < to; at++)
        {
            var character = text[at];

            if (LineBreak(character) ? ListItemAt(text, at + 1) : !char.IsWhiteSpace(character) && character is not ('*' or '_' or '`' or '~'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Whether what stands between <paramref name="from"/> and <paramref name="to"/> of <paramref name="text"/> is what
    /// <see cref="Beside(string, int, int)"/> allows and one <paramref name="mark"/>: a colon after a currency's code
    /// before a number (<c>EUR: 0.02</c>), a bracket opening the currency after one (<c>0.02 (EUR)</c>).
    /// </summary>
    private static bool Beside(string text, int from, int to, char mark)
        => text.IndexOf(mark, from, to - from) is var at and >= 0 && Beside(text, from, at) && Beside(text, at + 1, to);

    /// <summary>
    /// Whether a list's item opens the line that starts at <paramref name="at"/> of <paramref name="text"/>: after spaces
    /// and tabs, a bullet (<c>*</c>, <c>-</c>, <c>+</c>) or a number and its point or bracket (<c>1.</c>, <c>2)</c>), and
    /// then a space or a tab.
    /// </summary>
    private static bool ListItemAt(string text, int at)
    {
        while (at < text.Length && text[at] is ' ' or '\t')
        {
            at++;
        }

        var marker = at;

        if (at < text.Length && text[at] is '*' or '-' or '+')
        {
            at++;
        }
        else
        {
            while (at < text.Length && char.IsAsciiDigit(text[at]))
            {
                at++;
            }

            if (at == marker || at == text.Length || text[at] is not ('.' or ')'))
            {
                return false;
            }

            at++;
        }

        return at < text.Length && text[at] is ' ' or '\t';
    }

    /// <summary>Whether <paramref name="character"/> ends a line.</summary>
    private static bool LineBreak(char character)
        => character is '\n' or '\r' or '\u2028' or '\u2029';

    /// <summary>Whether a number starts at <paramref name="at"/> of <paramref name="text"/>, before <paramref name="to"/>: a digit, or a sign or a point directly before one.</summary>
    private static bool StartsNumber(string text, int at, int to)
    {
        var digit = at < to && text[at] is '-' or '+' or Minus ? at + 1 : at;

        if (digit < to && text[digit] == '.')
        {
            digit++;
        }

        return digit < to && char.IsAsciiDigit(text[digit]);
    }

    /// <summary>
    /// Whether an amount starts at <paramref name="at"/> of <paramref name="text"/>, before <paramref name="to"/>: a number
    /// (<see cref="StartsNumber"/>), or the lead that names its unit written directly before it (<see cref="LeadAt"/>), or
    /// a sign directly before that lead (<c>-$0.40</c>).
    /// </summary>
    private static bool StartsAmount(string text, int at, int to)
        => StartsNumber(text, at, to)
            || LeadAt(text, at, to) is not null
            || (at < to && text[at] is '-' or '+' or Minus && LeadAt(text, at + 1, to) is not null);

    /// <summary>
    /// One word of a text, read off what wraps it: every character at either end that is neither a letter nor a digit -
    /// Markdown's underscores, points, signs - but where an amount starts (<see cref="StartsAmount"/>); with the units the
    /// signs read off name, the lead's before it (<see cref="LeadAt"/>: a currency's sign, its capitals, its code, a
    /// multiple's sign) and a percent's, a multiple's or a currency's after it; a sign directly before the lead the
    /// number's own (<c>-$0.40</c> is a negative amount). An id is a word read whole (<see cref="OfName"/>).
    /// </summary>
    /// <param name="Core">What is left of the word: its letters and digits and what stands between them, the number's sign before them.</param>
    /// <param name="Signs">The units the signs read off its ends name, in the order they stood.</param>
    /// <param name="Said">The word as an answer says it back: its core, with the signs read as its unit around it.</param>
    /// <param name="Trail">The sign read off its end, where one names a unit.</param>
    /// <param name="Start">Where what it says starts in the text - its core and the signs read as its unit; where the word does, for a word of signs alone.</param>
    /// <param name="End">Where what it says ends in the text; where the word does, for a word of signs alone.</param>
    /// <param name="Joined">Whether it is beside the word before it (<see cref="Beside(string, int, int)"/>): nothing but space and Markdown's marks between them, a line's end but where a list's item opens the next line.</param>
    /// <param name="Id">Whether it is an id the report or a citation holds, read for no number and naming no unit.</param>
    private readonly record struct Word(string Core, IReadOnlyList<FlowUnit> Signs, string Said, char? Trail, int Start, int End, bool Joined, bool Id = false)
    {
        /// <summary>
        /// The unit the word names where it stands after a number of no unit of its own: a unit's name or a currency's code,
        /// a currency's sign after its capitals (<c>R$</c>), or a unit's sign alone; null where it names none.
        /// </summary>
        public FlowUnit? Unit => Id ? null : Core.Length == 0 ? (Signs.Count == 1 ? Signs[0] : null) : FlowUnit.Named(Core) ?? Prefixed;

        /// <summary>
        /// The currency the word names: its name or its code (<c>euros</c>, <c>EUR</c>), or its sign, alone or after its
        /// capitals (<c>€</c>, <c>R$</c>); null where it names none.
        /// </summary>
        public FlowUnit? Currency => Id ? null : Core.Length == 0 ? (Signs is [{ IsCurrency: true } sign] ? sign : null) : FlowUnit.Named(Core) is { IsCurrency: true } named ? named : Prefixed;

        /// <summary>The currency a word of capitals ending in a currency's sign names (<c>R$</c>, <c>US$</c>); null for any other word.</summary>
        private FlowUnit? Prefixed => Signs.Count == 1 && Trail is { } sign ? FlowUnit.OfSign(sign, Core) : null;

        /// <summary>The id that stands in <paramref name="text"/> from <paramref name="from"/> to <paramref name="to"/>, a word read whole.</summary>
        public static Word OfName(string text, int from, int to)
            => new(text[from..to], [], text[from..to], null, from, to, Joined: false, Id: true);

        /// <summary>The word that stands in <paramref name="text"/> from <paramref name="from"/> to <paramref name="to"/>, read off what wraps it.</summary>
        public static Word Of(string text, int from, int to)
        {
            var start = from;

            while (start < to && !char.IsLetterOrDigit(text[start]) && !StartsAmount(text, start, to))
            {
                start++;
            }

            // A sign directly before the lead is the number's, but where the number after the lead has a sign of its own.
            var leads = start;
            char? sign = start < to && text[start] is '-' or '+' or Minus && LeadAt(text, start + 1, to) is not null ? text[start] : null;

            if (sign is not null)
            {
                start++;
            }

            string? lead = null;
            FlowUnit? before = null;

            if (LeadAt(text, start, to) is { } found)
            {
                lead = text[start..(start + found.Length)];
                before = found.Unit;
                start += found.Length;

                if (sign is not null && text[start] is '-' or '+' or Minus)
                {
                    sign = null;
                    leads++;
                }
            }

            var end = to;

            while (end > start && !char.IsLetterOrDigit(text[end - 1]))
            {
                end--;
            }

            // A word of signs alone - the % after a number, written apart - is the one sign of it that names a unit.
            if (start == end)
            {
                var signs = text[from..to].Where(one => FlowUnit.OfSign(one) is not null).ToList();

                return signs.Count == 1
                    ? new Word(string.Empty, [FlowUnit.OfSign(signs[0])!], signs[0].ToString(), null, from, to, Joined: false)
                    : new Word(string.Empty, [], string.Empty, null, from, to, Joined: false);
            }

            // After a number the sign that ends it: a percent's, a multiple's or a currency's.
            var trail = end < to && FlowUnit.OfSign(text[end]) is not null ? text[end] : (char?)null;
            var core = text[start..end];

            return new Word(
                $"{sign}{core}",
                [.. new[] { before, trail is { } after ? FlowUnit.OfSign(after) : null }.OfType<FlowUnit>()],
                $"{sign}{lead}{core}{trail}",
                trail,
                lead is null ? start : leads,
                trail is null ? end : end + 1,
                Joined: false);
        }
    }

    /// <summary>
    /// How many characters of <paramref name="word"/> from <paramref name="from"/> to <paramref name="to"/> its numeric
    /// head is: a sign, digits - thousands grouped by commas, three digits each after a first group of one to three - one
    /// fraction, its point first where no digit comes before it (<c>.5</c>), and an exponent; 0 where it does not start as
    /// a number (a sign alone, or a letter).
    /// </summary>
    private static int HeadOf(string word, int from, int to)
    {
        var at = from < to && word[from] is '-' or '+' or Minus ? from + 1 : from;
        var digits = at;

        while (at < to && char.IsAsciiDigit(word[at]))
        {
            at++;
        }

        if (at - digits is >= 1 and <= 3)
        {
            while (at + 3 < to && word[at] == ',' && char.IsAsciiDigit(word[at + 1]) && char.IsAsciiDigit(word[at + 2]) && char.IsAsciiDigit(word[at + 3]) && (at + 4 == to || !char.IsAsciiDigit(word[at + 4])))
            {
                at += 4;
            }
        }

        var whole = at > digits;

        if (at + 1 < to && word[at] == '.' && char.IsAsciiDigit(word[at + 1]))
        {
            at++;

            while (at < to && char.IsAsciiDigit(word[at]))
            {
                at++;
            }
        }
        else if (!whole)
        {
            return 0;
        }

        if (at + 1 < to && word[at] is 'e' or 'E')
        {
            var exponent = word[at + 1] is '-' or '+' ? at + 2 : at + 1;

            if (exponent < to && char.IsAsciiDigit(word[exponent]))
            {
                at = exponent;

                while (at < to && char.IsAsciiDigit(word[at]))
                {
                    at++;
                }
            }
        }

        return at - from;
    }

    /// <summary>
    /// The words a text is read with as ids (<see cref="WordsOf"/>), each read whole and never for a number: what the report
    /// holds as a name - each row's id and each name it is made of, as the row's own members hold them (a hotspot's item,
    /// class and path), and every section's and member's name - and the paths a pain point's citations write
    /// (<see cref="PathsOf"/>). A name is kept where it starts with a letter, once what is neither a letter nor a digit is
    /// read off its start, and holds a character a word does not keep whatever stands around it: a name of letters,
    /// digits, hyphens, points and underscores alone reads the same as a word, and one that starts as a number is read as
    /// any word is, so no number goes unread.
    /// </summary>
    private sealed class Names
    {
        /// <summary>The trees of names an id is looked for in, the report's first.</summary>
        private readonly IReadOnlyList<Node> _trees;

        /// <summary>The names <paramref name="trees"/> hold.</summary>
        private Names(IReadOnlyList<Node> trees) => _trees = trees;

        /// <summary>What <paramref name="report"/> holds as names: each row's id and the names it is made of, and every section's and member's name.</summary>
        public static Names Of(JsonNode report)
        {
            var tree = new Node();

            foreach (var (place, node) in JsonWalk.Descendants(report))
            {
                if (place.Name is { } name)
                {
                    tree.Add(name);
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
        /// that no character a word holds joins on either side - a letter, a digit, a mark or an underscore, a point or a
        /// hyphen with a letter or a digit beyond it, and after it a comma or a minus sign before a digit, or a sign a number
        /// is written with - so <c>c@10</c> holds no <c>c@1</c>; 0 where none does.
        /// </summary>
        public int At(string text, int at)
        {
            if (at > 0 && (Whole(text[at - 1]) || (text[at - 1] is '-' or '.' && at > 1 && char.IsLetterOrDigit(text[at - 2]))))
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
                || !(Whole(text[end])
                    || text[end] is '%' or '×'
                    || FlowUnit.IsCurrencySign(text[end])
                    || (text[end] is '-' or '.' or ',' or Minus && end + 1 < text.Length && char.IsLetterOrDigit(text[end + 1])));

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

            /// <summary>Adds <paramref name="name"/> below this node, where it is one a text is read with as an id (<see cref="Names"/>).</summary>
            public void Add(string name)
            {
                if (!(name.FirstOrDefault(char.IsLetterOrDigit) is var first && char.IsLetter(first)) || name.All(character => Whole(character) || character is '-' or '.'))
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

    /// <summary>
    /// One number a text says as it is read: the number; the words it takes, from its first to its last; the kind of the
    /// unit written against it, the base of the smallest unit it is said in and its value in its kind's base - none where
    /// no unit is written; whether it is written with a sign, and with a minus; and whether it takes a currency's words
    /// before it.
    /// </summary>
    private readonly record struct Reading(SaidNumber Number, int First, int Last, string? Kind, decimal Smallest, decimal? Whole, bool Signed, bool Negative, bool Led)
    {
        /// <summary>
        /// This number and <paramref name="next"/> as one value, their sum, where next continues it: a number joined to a
        /// smaller unit of its kind, with no sign and no currency's words before it, written directly beside it
        /// (<see cref="Beside(string, int, int)"/>: <c>1h 30m</c>, <c>2 min 5 s</c>); null where it does not.
        /// </summary>
        public Reading? Continued(Reading next, IReadOnlyList<Word> words)
        {
            if (!Number.Reads || !next.Number.Reads || Kind is not { } kind || !string.Equals(next.Kind, kind, StringComparison.Ordinal)
                || next.Smallest >= Smallest || next.Signed || next.Led || next.First != Last + 1 || !words[next.First].Joined)
            {
                return null;
            }

            var whole = Plus(Whole, next.Whole, Negative);

            return this with
            {
                Number = new SaidNumber($"{Number.Text} {next.Number.Text}", Reads: true, whole, new FlowUnit(kind, 1m), Summed: true),
                Last = next.Last,
                Smallest = next.Smallest,
                Whole = whole,
            };
        }

        /// <summary>
        /// This number with the currency written after it in its own phrase (<see cref="CurrencyAfter"/>), before the next
        /// number's words at <paramref name="next"/>, where it has no unit of its own or a currency's: <c>0.02 in
        /// euros</c>, <c>0.02 (EUR)</c>, <c>0.02 Canadian dollars</c>. A number whose phrase names a currency other than
        /// its own reads as no one number (<c>$0.02 CAD</c>); one whose phrase names none, or that is joined to a unit of
        /// another kind, is as it was read.
        /// </summary>
        public Reading InPhrase(string text, IReadOnlyList<Word> words, int next)
        {
            if (!Number.Reads || (Kind is not null && Number.Unit is { IsCurrency: false }) || CurrencyAfter(text, words, Last, next) is not { } named)
            {
                return this;
            }

            var said = string.Join(" ", words.Skip(Last + 1).Take(named.Last - Last).Select(word => word.Said).Prepend(Number.Text).Where(part => part.Length > 0));
            var one = Kind is null || named.Unit == Number.Unit;

            return this with { Number = one ? new SaidNumber(said, Reads: true, Number.Value, named.Unit) : new SaidNumber(said, Reads: false, null, null) };
        }
    }

    /// <summary>
    /// One number a text says: the words it is said in; whether it reads as one number with its unit; its value - null
    /// where it reads as none, or past what a decimal holds, so no citation could hold it - and its unit, null for a
    /// number held as written; and whether it is a sum of numbers said each in its unit, which no number written bare is.
    /// </summary>
    private readonly record struct SaidNumber(string Text, bool Reads, decimal? Value, FlowUnit? Unit, bool Summed = false)
    {
        /// <summary>
        /// Whether <paramref name="cited"/>, a number a citation holds, holds this one: written bare, its value as
        /// written, never a sum; joined to its unit, a value of a unit of its kind, the two the same once converted exactly.
        /// </summary>
        public bool IsHeldBy(SaidNumber cited)
            => Value is { } value && cited.Value is { } held
                && (Unit is not { } unit ? !cited.Summed && value == held : cited.Unit is { } its && unit.Holds(value, its, held));
    }

    /// <summary>A text member's text; empty where it is none.</summary>
    private static string TextOf(JsonNode? node)
        => node is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : string.Empty;
}
