namespace DssHarness.Core.Orchestration.Flow;

/// <summary>
/// A unit a number is in - a member of the report, or a number an analysis writes of one - by what it measures and how
/// many of that measure's base one of it is: a minute is 60 of a time's seconds, a cent a hundredth of a dollar. Two
/// numbers are one measure only where their kinds are one, the one converted exactly into the other's unit
/// (<see cref="Holds"/>); a number of one kind is never read as a number of another.
/// </summary>
/// <param name="Kind">What it measures: <c>time</c>, <c>count</c>, <c>percent</c>, <c>multiple</c>, <c>size</c>, or a currency - <c>USD</c> the report's dollars, any other by its own sign.</param>
/// <param name="Base">How many of its kind's base one of it is: seconds for a time, ones for a count, a percent and a multiple, dollars for <c>USD</c>, bytes for a size.</param>
public sealed record FlowUnit(string Kind, decimal Base)
{
    /// <summary>A time in seconds: the report's every duration but its budget's minutes.</summary>
    public static FlowUnit Seconds { get; } = new("time", 1m);

    /// <summary>A time in minutes: the budget's reasoning.</summary>
    public static FlowUnit Minutes { get; } = new("time", 60m);

    /// <summary>A count, or a number of no unit - tokens, steps, tries, an exit code, a line.</summary>
    public static FlowUnit Count { get; } = new("count", 1m);

    /// <summary>A share in percent.</summary>
    public static FlowUnit Percent { get; } = new("percent", 1m);

    /// <summary>United States dollars, the currency every cost of the report is in.</summary>
    public static FlowUnit Dollars { get; } = new("USD", 1m);

    /// <summary>A multiple of another number: <c>2x</c>.</summary>
    public static FlowUnit Multiple { get; } = new("multiple", 1m);

    /// <summary>The kind said in a sentence: <c>a time</c>, <c>an amount in USD</c>.</summary>
    public string KindSaid => Kind switch
    {
        "time" => "a time",
        "count" => "a count",
        "percent" => "a share in percent",
        "multiple" => "a multiple",
        "size" => "a size",
        var currency => $"an amount in {currency}",
    };

    /// <summary>
    /// Whether <paramref name="value"/> in this unit is <paramref name="held"/> in <paramref name="unit"/>: the two of one
    /// kind, and the same measure once converted exactly - 1.5 hours is 90 minutes, and 60 hours is not 60 seconds.
    /// </summary>
    /// <param name="value">The number in this unit.</param>
    /// <param name="unit">The unit the other number is in.</param>
    /// <param name="held">The other number.</param>
    public bool Holds(decimal value, FlowUnit unit, decimal held)
    {
        ArgumentNullException.ThrowIfNull(unit);

        if (!string.Equals(Kind, unit.Kind, StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            return value * Base == held * unit.Base;
        }
        catch (OverflowException)
        {
            // Past what a decimal holds in its kind's base: no number the report holds is that measure.
            return false;
        }
    }

    /// <summary>
    /// The unit a word written beside a number names - <c>s</c>, <c>min</c>, <c>hours</c>, <c>tokens</c>, <c>USD</c>,
    /// <c>KB</c>, <c>x</c> - read as written where it is one letter, for <c>m</c> and <c>M</c> are not one unit, and in
    /// any case where it is longer; null for a word that names none. A scale names no unit (<see cref="ScaleOf"/>).
    /// </summary>
    /// <param name="word">The word, without what wraps it.</param>
    public static FlowUnit? Named(string word)
    {
        ArgumentNullException.ThrowIfNull(word);

        return word.Length == 1 ? Letters.GetValueOrDefault(word) : Words.GetValueOrDefault(word);
    }

    /// <summary>
    /// What a scale written beside a number multiplies it by - <c>k</c>, <c>thousand</c>, <c>M</c>, <c>million</c>,
    /// <c>bn</c> - read as written where it is one letter and in any case where it is longer; null for a word that is no
    /// scale.
    /// </summary>
    /// <param name="word">The word, without what wraps it.</param>
    public static decimal? ScaleOf(string word)
    {
        ArgumentNullException.ThrowIfNull(word);

        return (word.Length == 1 ? ScaleLetters : Scales).TryGetValue(word, out var by) ? by : null;
    }

    /// <summary>
    /// The unit a sign written against a number names: <c>%</c> a share in percent, <c>×</c> a multiple, <c>$</c>
    /// dollars and any other currency's sign that currency; null for a sign that names none.
    /// </summary>
    /// <param name="sign">The sign.</param>
    public static FlowUnit? OfSign(char sign)
        => sign switch
        {
            '%' => Percent,
            '×' => Multiple,
            '$' => Dollars,
            _ when char.GetUnicodeCategory(sign) == System.Globalization.UnicodeCategory.CurrencySymbol => new(sign.ToString(), 1m),
            _ => null,
        };

    /// <summary>The units written as one letter, each read as written.</summary>
    private static readonly Dictionary<string, FlowUnit> Letters = new(StringComparer.Ordinal)
    {
        ["s"] = Seconds,
        ["m"] = Minutes,
        ["h"] = new("time", 3600m),
        ["d"] = new("time", 86400m),
        ["x"] = Multiple,
        ["X"] = Multiple,
        ["B"] = new("size", 1m),
    };

    /// <summary>The units written as a longer word, each read in any case.</summary>
    private static readonly Dictionary<string, FlowUnit> Words = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ns"] = new("time", 0.000000001m),
        ["nanosecond"] = new("time", 0.000000001m),
        ["nanoseconds"] = new("time", 0.000000001m),
        ["µs"] = new("time", 0.000001m),
        ["μs"] = new("time", 0.000001m),
        ["microsecond"] = new("time", 0.000001m),
        ["microseconds"] = new("time", 0.000001m),
        ["ms"] = new("time", 0.001m),
        ["msec"] = new("time", 0.001m),
        ["msecs"] = new("time", 0.001m),
        ["millisecond"] = new("time", 0.001m),
        ["milliseconds"] = new("time", 0.001m),
        ["sec"] = Seconds,
        ["secs"] = Seconds,
        ["second"] = Seconds,
        ["seconds"] = Seconds,
        ["min"] = Minutes,
        ["mins"] = Minutes,
        ["minute"] = Minutes,
        ["minutes"] = Minutes,
        ["hr"] = new("time", 3600m),
        ["hrs"] = new("time", 3600m),
        ["hour"] = new("time", 3600m),
        ["hours"] = new("time", 3600m),
        ["day"] = new("time", 86400m),
        ["days"] = new("time", 86400m),
        ["wk"] = new("time", 604800m),
        ["wks"] = new("time", 604800m),
        ["week"] = new("time", 604800m),
        ["weeks"] = new("time", 604800m),
        ["token"] = Count,
        ["tokens"] = Count,
        ["percent"] = Percent,
        ["pct"] = Percent,
        ["usd"] = Dollars,
        ["dollar"] = Dollars,
        ["dollars"] = Dollars,
        ["cent"] = new("USD", 0.01m),
        ["cents"] = new("USD", 0.01m),
        ["fold"] = Multiple,
        ["byte"] = new("size", 1m),
        ["bytes"] = new("size", 1m),
        ["kb"] = new("size", 1000m),
        ["kib"] = new("size", 1024m),
        ["mb"] = new("size", 1000000m),
        ["mib"] = new("size", 1048576m),
        ["gb"] = new("size", 1000000000m),
        ["gib"] = new("size", 1073741824m),
        ["tb"] = new("size", 1000000000000m),
        ["tib"] = new("size", 1099511627776m),
    };

    /// <summary>The scales written as one letter, each read as written.</summary>
    private static readonly Dictionary<string, decimal> ScaleLetters = new(StringComparer.Ordinal)
    {
        ["k"] = 1000m,
        ["K"] = 1000m,
        ["M"] = 1000000m,
    };

    /// <summary>The scales written as a longer word, each read in any case.</summary>
    private static readonly Dictionary<string, decimal> Scales = new(StringComparer.OrdinalIgnoreCase)
    {
        ["thousand"] = 1000m,
        ["thousands"] = 1000m,
        ["million"] = 1000000m,
        ["millions"] = 1000000m,
        ["bn"] = 1000000000m,
        ["billion"] = 1000000000m,
        ["billions"] = 1000000000m,
    };
}

/// <summary>The units of the report's numbers, beside the builder that writes them.</summary>
public static partial class FlowRunReport
{
    /// <summary>
    /// The unit of every number the sections an analysis may cite hold, keyed by its member's path: its section, a list's
    /// rows marked <c>[]</c> and the members inside joined by '.' - <c>theRun.wallSeconds</c>, <c>steps[].usd</c>,
    /// <c>totals.dollarsBy.groups[].usd</c>. An entry of a member that holds an object or a list covers every number
    /// inside it: a step's and the run's graph figures, a failed attempt's exception and first error (their places and
    /// counts), an item's counts - each a count. An entry of a text member is the unit of each number it writes bare - a
    /// step's <c>ran</c> in seconds, a signal's counts - its other numbers in the units written beside them. The one owner
    /// the analysis's evidence check reads a cited number's unit from (<see cref="FlowAnalysisEvidence"/>), held to every
    /// number the builder writes by the report's tests.
    /// </summary>
    public static IReadOnlyDictionary<string, FlowUnit> Units { get; } = new Dictionary<string, FlowUnit>(StringComparer.Ordinal)
    {
        ["theRun.sessions"] = FlowUnit.Count,
        ["theRun.wallSeconds"] = FlowUnit.Seconds,
        ["theRun.activeSeconds"] = FlowUnit.Seconds,
        ["theRun.pausedSeconds"] = FlowUnit.Seconds,
        ["theRun.waitingForAPersonSeconds"] = FlowUnit.Seconds,
        ["theRun.budget.usd"] = FlowUnit.Dollars,
        ["theRun.budget.tokens"] = FlowUnit.Count,
        ["theRun.budget.minutes"] = FlowUnit.Minutes,
        ["totals.tokens"] = FlowUnit.Count,
        ["totals.cacheSharePercent"] = FlowUnit.Percent,
        ["totals.dollarsBy.groups[].usd"] = FlowUnit.Dollars,
        ["totals.dollarsBy.groups[].tokens"] = FlowUnit.Count,
        ["totals.usd"] = FlowUnit.Dollars,
        ["totals.agents"] = FlowUnit.Count,
        ["totals.stepsRun"] = FlowUnit.Count,
        ["totals.stepsSkipped"] = FlowUnit.Count,
        ["totals.stepsFailed"] = FlowUnit.Count,
        ["totals.retries"] = FlowUnit.Count,
        ["totals.fallbacks"] = FlowUnit.Count,
        ["totals.graph"] = FlowUnit.Count,
        ["steps[].tries"] = FlowUnit.Count,
        ["steps[].waited"] = FlowUnit.Seconds,
        ["steps[].ran"] = FlowUnit.Seconds,
        ["steps[].tokens"] = FlowUnit.Count,
        ["steps[].usd"] = FlowUnit.Dollars,
        ["steps[].exit"] = FlowUnit.Count,
        ["steps[].graph"] = FlowUnit.Count,
        ["criticalPath[].seconds"] = FlowUnit.Seconds,
        ["criticalPath[].waited"] = FlowUnit.Seconds,
        ["errors[].attempt"] = FlowUnit.Count,
        ["errors[].exit"] = FlowUnit.Count,
        ["errors[].exception"] = FlowUnit.Count,
        ["errors[].firstError"] = FlowUnit.Count,
        ["people[].waitedSeconds"] = FlowUnit.Seconds,
        ["convergence.items[]"] = FlowUnit.Count,
        ["convergence.classesReopened[].reopens"] = FlowUnit.Count,
        ["convergence.hotspots[].touches"] = FlowUnit.Count,
        ["signals[].said"] = FlowUnit.Count,
    };

    /// <summary>
    /// The unit of the member at <paramref name="path"/> (<see cref="Units"/>): its own entry's, else that of the nearest
    /// member around it that has one; null where none does - a text whose numbers no entry gives a unit, which only a
    /// number written bare is held to.
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
}
