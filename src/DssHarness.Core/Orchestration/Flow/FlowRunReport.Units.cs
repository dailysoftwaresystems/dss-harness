namespace DssHarness.Core.Orchestration.Flow;

/// <summary>
/// A unit a number is in - a member of the report, or a number an analysis writes of one - by what it measures and how
/// many of that measure's base one of it is: a minute is 60 of a time's seconds, a cent a hundredth of a dollar. Two
/// numbers are one measure only where their kinds are one, the one converted exactly into the other's unit
/// (<see cref="Holds"/>); a number of one kind is never read as a number of another.
/// </summary>
/// <param name="Kind">What it measures: <c>time</c>, <c>count</c>, <c>percent</c>, <c>multiple</c>, <c>size</c>, or a currency - <c>USD</c> the report's dollars, any other by the ISO 4217 code (<c>EUR</c>), the name (<c>euros</c>, <c>Canadian dollars</c>) or the sign (<c>€</c>, <c>R$</c>) it is written in.</param>
/// <param name="Base">How many of its kind's base one of it is: seconds for a time, ones for a count, a percent and a multiple, a currency's whole unit, bytes for a size.</param>
public sealed record FlowUnit(string Kind, decimal Base)
{
    /// <summary>The kinds that measure something other than money: a unit of any other kind is a currency's.</summary>
    private static readonly HashSet<string> Measures = new(StringComparer.Ordinal) { "time", "count", "percent", "multiple", "size" };

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

    /// <summary>Whether it is a currency's: the report's dollars, or any other.</summary>
    public bool IsCurrency => !Measures.Contains(Kind);

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
    /// <c>KB</c>, <c>x</c>, a currency's name (<c>euros</c>, <c>pounds</c>, <c>reais</c>) or its code (<see cref="OfCode"/>)
    /// - read as written where it is one letter, for <c>m</c> and <c>M</c> are not one unit, or a code, and in any case
    /// where it is a longer word, or two (<c>per cent</c>); null for a word that names none. A scale names no unit (<see
    /// cref="ScaleOf"/>).
    /// </summary>
    /// <param name="word">The word, without what wraps it; two words with one space between them.</param>
    public static FlowUnit? Named(string word)
    {
        ArgumentNullException.ThrowIfNull(word);

        return word.Length == 1 ? Letters.GetValueOrDefault(word) : Words.GetValueOrDefault(word) ?? OfCode(word);
    }

    /// <summary>
    /// The currency a currency's name says written after the name of a country it is named after (<c>Canadian
    /// dollars</c>, <c>New Zealand dollars</c>, <c>U.S. dollars</c>, <c>Swiss francs</c>, <c>South Korean won</c>), the
    /// country one of a closed list, in any case: a dollar's or a cent's name after the United States' (<c>US</c>,
    /// <c>U.S.</c>, <c>USA</c>, <c>American</c>, <c>United States</c>) the report's dollars, and after any other
    /// country's that country's dollars, a currency of its own; any other currency's name or code the currency it names,
    /// a name that is an ordinary English word (<c>won</c>, <c>real</c>) among them, read only here; null where
    /// <paramref name="country"/> is no country of the list, or <paramref name="name"/> names no currency - so a word
    /// that is no country's (<c>Wasted dollars</c>) never makes a currency of its own.
    /// </summary>
    /// <param name="country">The country's name as written, its words one space apart and an abbreviation's point kept (<c>U.S.</c>).</param>
    /// <param name="name">The currency's name written after it, without what wraps it.</param>
    public static FlowUnit? OfCountry(string country, string name)
    {
        ArgumentNullException.ThrowIfNull(country);
        ArgumentNullException.ThrowIfNull(name);

        if (!Countries.TryGetValue(country, out var listed) || (Named(name) is { IsCurrency: true } named ? named : Ordinary.GetValueOrDefault(name)) is not { } currency)
        {
            return null;
        }

        return DollarNames.Contains(name) && !UnitedStates.Contains(listed) ? new($"{listed} dollars", currency.Base) : currency;
    }

    /// <summary>
    /// Whether <paramref name="words"/>, one space apart, start the name of a country a currency is named after that goes
    /// on with more words (<c>New</c> of <c>New Zealand</c>, <c>Trinidad and</c> of <c>Trinidad and Tobago</c>).
    /// </summary>
    /// <param name="words">The words read so far.</param>
    public static bool StartsCountry(string words)
    {
        ArgumentNullException.ThrowIfNull(words);

        return CountryStarts.Contains(words);
    }

    /// <summary>The names of the dollar and its cent, which name the report's dollars where no other country's name qualifies them (<see cref="OfCountry"/>).</summary>
    private static readonly HashSet<string> DollarNames = new(StringComparer.OrdinalIgnoreCase) { "dollar", "dollars", "cent", "cents" };

    /// <summary>The names the United States is written by before its dollar's name.</summary>
    private static readonly HashSet<string> UnitedStates = new(StringComparer.OrdinalIgnoreCase) { "US", "U.S.", "USA", "U.S.A.", "American", "United States" };

    /// <summary>
    /// The closed list of countries a currency's name is written after (<see cref="OfCountry"/>): the United States', the
    /// other countries whose currency is a dollar, and the countries of every other currency whose name is read.
    /// </summary>
    private static readonly HashSet<string> Countries = new(StringComparer.OrdinalIgnoreCase)
    {
        "US", "U.S.", "USA", "U.S.A.", "American", "United States",
        "Australian", "Aussie", "Bahamian", "Barbadian", "Belizean", "Bermudian", "Brunei", "Bruneian", "Canadian", "Cayman",
        "Cayman Islands", "East Caribbean", "Fijian", "Guyanese", "Hong Kong", "HK", "Jamaican", "Kiwi", "Liberian", "Namibian",
        "New Taiwan", "New Zealand", "NZ", "Singapore", "Singaporean", "Solomon Islands", "Surinamese", "Taiwan", "Taiwanese",
        "Trinidad and Tobago", "Trinidadian", "Zimbabwean",
        "British", "UK", "Egyptian", "Lebanese", "Syrian", "Sudanese", "South Sudanese",
        "Japanese", "Chinese", "Brazilian",
        "Indian", "Pakistani", "Sri Lankan", "Nepalese", "Nepali", "Mauritian", "Seychellois", "Indonesian",
        "Swiss", "French", "Belgian", "CFA", "CFP", "Congolese", "Rwandan", "Burundian", "Guinean", "Djiboutian", "Comorian",
        "Mexican", "Argentine", "Argentinian", "Chilean", "Colombian", "Cuban", "Dominican", "Philippine", "Uruguayan",
        "Russian", "Belarusian", "Turkish", "Italian", "Swedish", "Icelandic", "Norwegian", "Danish", "Polish", "Hungarian",
        "Thai", "Malaysian", "Korean", "South Korean", "North Korean", "Israeli", "New Israeli",
        "Algerian", "Bahraini", "Iraqi", "Jordanian", "Kuwaiti", "Libyan", "Serbian", "Tunisian", "Moroccan", "Emirati", "UAE",
        "Saudi", "Qatari", "Omani", "Yemeni", "Iranian", "South African", "Nigerian", "Ukrainian",
    };

    /// <summary>Every run of whole words a country's name of more than one word starts with (<see cref="StartsCountry"/>).</summary>
    private static readonly HashSet<string> CountryStarts = new(
        Countries.SelectMany(country => country.Split(' ') is var words ? Enumerable.Range(1, words.Length - 1).Select(count => string.Join(' ', words[..count])) : []),
        StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The currencies whose name is an ordinary English word - <c>won</c> a verb, <c>real</c> an adjective - each read
    /// only after a country's name (<see cref="OfCountry"/>: <c>South Korean won</c>, <c>Brazilian real</c>), and
    /// elsewhere by its code or its sign alone (<c>KRW</c>, <c>₩</c>), as a name read beside any number would make an
    /// amount of a sentence's word.
    /// </summary>
    private static readonly Dictionary<string, FlowUnit> Ordinary = new(StringComparer.OrdinalIgnoreCase)
    {
        ["won"] = new("won", 1m),
        ["real"] = new("reais", 1m),
    };

    /// <summary>
    /// The currency a code names: one of ISO 4217's, written in capitals as the standard writes it - <c>USD</c> the
    /// report's dollars, any other its own (<c>EUR</c>, <c>GBP</c>, <c>BRL</c>); null for any other word, a code in small
    /// letters included, for <c>all</c>, <c>top</c> and <c>try</c> are a sentence's words.
    /// </summary>
    /// <param name="word">The word, without what wraps it.</param>
    public static FlowUnit? OfCode(string word)
    {
        ArgumentNullException.ThrowIfNull(word);

        return !Codes.Contains(word) ? null : word == "USD" ? Dollars : new(word, 1m);
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
    /// dollars and any other currency's sign that currency - written after capitals that say whose it is, the currency
    /// they say: <c>US$</c> dollars, and any other its own as written (<c>R$</c>, <c>CA$</c>); null for a sign that names
    /// none, or capitals before a sign that is no currency's.
    /// </summary>
    /// <param name="sign">The sign.</param>
    /// <param name="capitals">The capitals written directly before it; empty where none are.</param>
    public static FlowUnit? OfSign(char sign, string capitals = "")
    {
        ArgumentNullException.ThrowIfNull(capitals);

        if (capitals.Length > 0)
        {
            return !IsCurrencySign(sign) || !capitals.All(char.IsAsciiLetterUpper) ? null
                : capitals == "US" && sign == '$' ? Dollars
                : new($"{capitals}{sign}", 1m);
        }

        return sign switch
        {
            '%' => Percent,
            '×' => Multiple,
            '$' => Dollars,
            _ when IsCurrencySign(sign) => new(sign.ToString(), 1m),
            _ => null,
        };
    }

    /// <summary>Whether <paramref name="sign"/> is a currency's sign, as Unicode says one is.</summary>
    /// <param name="sign">The character.</param>
    public static bool IsCurrencySign(char sign)
        => char.GetUnicodeCategory(sign) == System.Globalization.UnicodeCategory.CurrencySymbol;

    /// <summary>
    /// The currency codes ISO 4217 lists - its funds, metals and units of account among them, and the codes it lately
    /// withdrew that are still written - but its code of a test and its code of no currency.
    /// </summary>
    private static readonly HashSet<string> Codes = new(StringComparer.Ordinal)
    {
        "AED", "AFN", "ALL", "AMD", "ANG", "AOA", "ARS", "AUD", "AWG", "AZN", "BAM", "BBD", "BDT", "BGN", "BHD", "BIF",
        "BMD", "BND", "BOB", "BOV", "BRL", "BSD", "BTN", "BWP", "BYN", "BZD", "CAD", "CDF", "CHE", "CHF", "CHW", "CLF",
        "CLP", "CNY", "COP", "COU", "CRC", "CUC", "CUP", "CVE", "CZK", "DJF", "DKK", "DOP", "DZD", "EGP", "ERN", "ETB",
        "EUR", "FJD", "FKP", "GBP", "GEL", "GHS", "GIP", "GMD", "GNF", "GTQ", "GYD", "HKD", "HNL", "HRK", "HTG", "HUF",
        "IDR", "ILS", "INR", "IQD", "IRR", "ISK", "JMD", "JOD", "JPY", "KES", "KGS", "KHR", "KMF", "KPW", "KRW", "KWD",
        "KYD", "KZT", "LAK", "LBP", "LKR", "LRD", "LSL", "LYD", "MAD", "MDL", "MGA", "MKD", "MMK", "MNT", "MOP", "MRU",
        "MUR", "MVR", "MWK", "MXN", "MXV", "MYR", "MZN", "NAD", "NGN", "NIO", "NOK", "NPR", "NZD", "OMR", "PAB", "PEN",
        "PGK", "PHP", "PKR", "PLN", "PYG", "QAR", "RON", "RSD", "RUB", "RWF", "SAR", "SBD", "SCR", "SDG", "SEK", "SGD",
        "SHP", "SLE", "SLL", "SOS", "SRD", "SSP", "STN", "SVC", "SYP", "SZL", "THB", "TJS", "TMT", "TND", "TOP", "TRY",
        "TTD", "TWD", "TZS", "UAH", "UGX", "USD", "USN", "UYI", "UYU", "UYW", "UZS", "VED", "VES", "VND", "VUV", "WST",
        "XAF", "XAG", "XAU", "XBA", "XBB", "XBC", "XBD", "XCD", "XCG", "XDR", "XOF", "XPD", "XPF", "XPT", "XSU", "XUA",
        "YER", "ZAR", "ZMW", "ZWG", "ZWL",
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

    /// <summary>
    /// The units written as a longer word, or as two (<c>per cent</c>, a share in percent and never a dollar's cent),
    /// each read in any case: a currency's name among them, its kind the name in the plural - so the pesos of several
    /// countries are one kind, as no member of the report is any - but not a name that is an ordinary English word,
    /// <c>won</c> or <c>real</c>, which after a number is far more often a sentence's word (<c>2 won</c>, <c>2 real
    /// failures</c>) than the currency, and is read only after a country's name (<see cref="Ordinary"/>); the plural
    /// <c>reais</c>, and every code and sign, are read.
    /// </summary>
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
        ["per cent"] = Percent,
        ["pct"] = Percent,
        ["usd"] = Dollars,
        ["dollar"] = Dollars,
        ["dollars"] = Dollars,
        ["cent"] = new("USD", 0.01m),
        ["cents"] = new("USD", 0.01m),
        ["euro"] = new("euros", 1m),
        ["euros"] = new("euros", 1m),
        ["pound"] = new("pounds", 1m),
        ["pounds"] = new("pounds", 1m),
        ["sterling"] = new("pounds", 1m),
        ["yen"] = new("yen", 1m),
        ["yuan"] = new("yuan", 1m),
        ["renminbi"] = new("yuan", 1m),
        ["reais"] = new("reais", 1m),
        ["rupee"] = new("rupees", 1m),
        ["rupees"] = new("rupees", 1m),
        ["rupiah"] = new("rupiah", 1m),
        ["franc"] = new("francs", 1m),
        ["francs"] = new("francs", 1m),
        ["peso"] = new("pesos", 1m),
        ["pesos"] = new("pesos", 1m),
        ["ruble"] = new("rubles", 1m),
        ["rubles"] = new("rubles", 1m),
        ["rouble"] = new("rubles", 1m),
        ["roubles"] = new("rubles", 1m),
        ["lira"] = new("lira", 1m),
        ["lire"] = new("lira", 1m),
        ["krona"] = new("kronor", 1m),
        ["kronor"] = new("kronor", 1m),
        ["krone"] = new("kroner", 1m),
        ["kroner"] = new("kroner", 1m),
        ["zloty"] = new("zloty", 1m),
        ["zlotys"] = new("zloty", 1m),
        ["forint"] = new("forints", 1m),
        ["forints"] = new("forints", 1m),
        ["baht"] = new("baht", 1m),
        ["ringgit"] = new("ringgit", 1m),
        ["shekel"] = new("shekels", 1m),
        ["shekels"] = new("shekels", 1m),
        ["dinar"] = new("dinars", 1m),
        ["dinars"] = new("dinars", 1m),
        ["dirham"] = new("dirhams", 1m),
        ["dirhams"] = new("dirhams", 1m),
        ["riyal"] = new("riyals", 1m),
        ["riyals"] = new("riyals", 1m),
        ["rial"] = new("rials", 1m),
        ["rials"] = new("rials", 1m),
        ["rand"] = new("rand", 1m),
        ["naira"] = new("naira", 1m),
        ["hryvnia"] = new("hryvnias", 1m),
        ["hryvnias"] = new("hryvnias", 1m),
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
