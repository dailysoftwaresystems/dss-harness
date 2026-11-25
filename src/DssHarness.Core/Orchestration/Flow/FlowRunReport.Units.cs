using System.Globalization;
using System.Text.Json.Nodes;

namespace DssHarness.Core.Orchestration.Flow;

/// <summary>
/// The unit a number of the report is in, by what it measures - the form the report's words say a value of it in
/// (<see cref="FlowRunReport.SaidIn"/>).
/// </summary>
public enum FlowUnit
{
    /// <summary>A time in seconds: the report's every duration but its budget's minutes.</summary>
    Seconds,

    /// <summary>A time in minutes: the budget's reasoning.</summary>
    Minutes,

    /// <summary>United States dollars, the currency every cost of the report is in.</summary>
    Dollars,

    /// <summary>A share in percent.</summary>
    Percent,

    /// <summary>A count of tokens.</summary>
    Tokens,

    /// <summary>A count of anything else, or a number of no unit - steps, tries, searches, an exit code, a line.</summary>
    Count,
}

/// <summary>The units of the report's numbers, beside the builder that writes them.</summary>
public static partial class FlowRunReport
{
    /// <summary>
    /// The unit of every number the sections an analysis may cite hold, keyed by its member's path: its section, a list's
    /// rows marked <c>[]</c> and the members inside joined by '.' - <c>theRun.wallSeconds</c>, <c>steps[].usd</c>,
    /// <c>totals.dollarsBy.groups[].usd</c>. An entry of a member that holds an object or a list covers every number
    /// inside it but where an entry of its own says otherwise: a step's and the run's graph figures, a failed attempt's
    /// exception and first error (their places and counts), an item's counts - each a count, a graph's tokens tokens. An
    /// entry of a text member is the unit of the one number it holds whole, as the report writes it: a step's <c>ran</c>,
    /// in seconds, a hotspot's touches and a class's reopens, each a count. The one owner of a cited number's unit an
    /// analysis's impact is written in (<see cref="FlowAnalysisEvidence"/>), held to every number the builder writes by
    /// the report's tests.
    /// </summary>
    public static IReadOnlyDictionary<string, FlowUnit> Units { get; } = new Dictionary<string, FlowUnit>(StringComparer.Ordinal)
    {
        ["theRun.sessions"] = FlowUnit.Count,
        ["theRun.wallSeconds"] = FlowUnit.Seconds,
        ["theRun.activeSeconds"] = FlowUnit.Seconds,
        ["theRun.pausedSeconds"] = FlowUnit.Seconds,
        ["theRun.waitingForAPersonSeconds"] = FlowUnit.Seconds,
        ["theRun.budget.usd"] = FlowUnit.Dollars,
        ["theRun.budget.tokens"] = FlowUnit.Tokens,
        ["theRun.budget.minutes"] = FlowUnit.Minutes,
        ["totals.tokens"] = FlowUnit.Tokens,
        ["totals.tokens.searches"] = FlowUnit.Count,
        ["totals.cacheSharePercent"] = FlowUnit.Percent,
        ["totals.dollarsBy.groups[].usd"] = FlowUnit.Dollars,
        ["totals.dollarsBy.groups[].tokens"] = FlowUnit.Tokens,
        ["totals.usd"] = FlowUnit.Dollars,
        ["totals.agents"] = FlowUnit.Count,
        ["totals.stepsRun"] = FlowUnit.Count,
        ["totals.stepsSkipped"] = FlowUnit.Count,
        ["totals.stepsFailed"] = FlowUnit.Count,
        ["totals.retries"] = FlowUnit.Count,
        ["totals.fallbacks"] = FlowUnit.Count,
        ["totals.graph"] = FlowUnit.Count,
        ["totals.graph.packTokens"] = FlowUnit.Tokens,
        ["totals.graph.servedTokens"] = FlowUnit.Tokens,
        ["totals.graph.wholeFileReads.tokens"] = FlowUnit.Tokens,
        ["totals.graph.escapes.tokens"] = FlowUnit.Tokens,
        ["steps[].tries"] = FlowUnit.Count,
        ["steps[].ran"] = FlowUnit.Seconds,
        ["steps[].tokens"] = FlowUnit.Tokens,
        ["steps[].usd"] = FlowUnit.Dollars,
        ["steps[].exit"] = FlowUnit.Count,
        ["steps[].graph"] = FlowUnit.Count,
        ["steps[].graph.mapTokens"] = FlowUnit.Tokens,
        ["steps[].graph.pack.tokens"] = FlowUnit.Tokens,
        ["steps[].graph.changesTokens"] = FlowUnit.Tokens,
        ["steps[].graph.servedTokens"] = FlowUnit.Tokens,
        ["steps[].graph.wholeFileReads.tokens"] = FlowUnit.Tokens,
        ["steps[].graph.escapes.tokens"] = FlowUnit.Tokens,
        ["criticalPath[].seconds"] = FlowUnit.Seconds,
        ["errors[].attempt"] = FlowUnit.Count,
        ["errors[].exit"] = FlowUnit.Count,
        ["errors[].exception"] = FlowUnit.Count,
        ["errors[].firstError"] = FlowUnit.Count,
        ["people[].waitedSeconds"] = FlowUnit.Seconds,
        ["convergence.items[]"] = FlowUnit.Count,
        ["convergence.classesReopened[].reopens"] = FlowUnit.Count,
        ["convergence.hotspots[].touches"] = FlowUnit.Count,
    };

    /// <summary>
    /// The unit of the member at <paramref name="path"/> (<see cref="Units"/>): its own entry's, else that of the nearest
    /// member around it that has one; null where none does - a member the report measures nothing in.
    /// </summary>
    /// <param name="path">The member's path, a list's rows marked <c>[]</c>: <c>steps[].graph.pack.tokens</c>.</param>
    public static FlowUnit? UnitOf(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        for (var key = path; key.Length > 0; key = key.LastIndexOf('.') is var dot and >= 0 ? key[..dot] : string.Empty)
        {
            if (Units.TryGetValue(key, out var unit))
            {
                return unit;
            }
        }

        return null;
    }

    /// <summary>
    /// <paramref name="value"/>, a number of the report or a text that holds one whole, said in <paramref name="unit"/> as
    /// the report's own words say a value of that unit, by the same forms: a time in seconds with one decimal
    /// (<c>412.3 s</c>), the budget's minutes with one (<c>90.0 minutes</c>), dollars with four and the currency
    /// (<c>0.0200 USD</c>), a share with one and its sign (<c>28.0%</c>), tokens as a count of them (<c>180000
    /// tokens</c>) and any other count as the report writes it (<c>2</c>) - the one way an analysis's impact says a
    /// figure it cites (<see cref="FlowAnalysisEvidence"/>).
    /// </summary>
    /// <param name="unit">The cited member's unit (<see cref="UnitOf"/>).</param>
    /// <param name="value">The cited value, as the report holds it.</param>
    public static string SaidIn(FlowUnit unit, JsonValue value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var number = double.Parse(value.TryGetValue<string>(out var text) ? text : value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture);

        return unit switch
        {
            FlowUnit.Seconds => SecondsSaid(number),
            FlowUnit.Minutes => MinutesOf(number),
            FlowUnit.Dollars => UsdOf(number),
            FlowUnit.Percent => PercentOf(number),
            FlowUnit.Tokens => TokensOf(value),
            _ => Said(value),
        };
    }
}
