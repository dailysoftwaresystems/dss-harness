using System.Text.RegularExpressions;
using RepoHarness.Core.Configuration;

namespace RepoHarness.Core.Anchors;

/// <summary>How anchor ids are spelled, recognised, and turned into a row's identity.</summary>
/// <remarks>
/// Two rules, on purpose. A NEW id must have at least the configured number of segments, so every
/// id minted is specific enough to be found. An EXISTING id only has to be well formed: its identity
/// came from the registry rather than from the caller, and refusing to maintain a row the registry
/// already holds would strand exactly the rows written before the rule existed.
/// </remarks>
public sealed class AnchorIdRules
{
    private const int MaximumFallbackIdentityLength = 80;

    /// <summary>One segment of an id, the prefix's and every one after it.</summary>
    private const string Segment = "[A-Za-z0-9_]+";

    /// <summary>What may not come just before an id: a character of one, which would make it the tail of a longer word.</summary>
    private const string NotAfterIdCharacter = "(?<![A-Za-z0-9_-])";

    private readonly Regex _mintable;
    private readonly Regex _wellFormed;
    private readonly Regex _token;
    private readonly Regex _backticked;
    private readonly Regex _cited;
    private readonly Regex _brokenAfterHyphen;
    private readonly Regex _wrappedInsideSegment;

    public AnchorIdRules(string prefix, int minimumSegments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        ArgumentOutOfRangeException.ThrowIfLessThan(minimumSegments, 1);

        Prefix = prefix;
        MinimumSegments = minimumSegments;

        var escaped = Regex.Escape(prefix);

        // \z rather than $: $ also matches before a trailing line break, which would let an id
        // carrying one through and break its row in two.
        _mintable = new Regex(
            $@"^{escaped}-[A-Z0-9_]+(?:-{Segment}){{{minimumSegments - 1},}}\z",
            RegexOptions.CultureInvariant);
        _wellFormed = new Regex($@"^{escaped}-{Segment}(?:-{Segment})*\z", RegexOptions.CultureInvariant);
        _token = new Regex($@"{escaped}-{Segment}(?:[-.]{Segment})+", RegexOptions.CultureInvariant);
        _backticked = new Regex($@"^`{escaped}-{Segment}(?:-{Segment})*`\z", RegexOptions.CultureInvariant);
        _cited = new Regex($@"{NotAfterIdCharacter}{escaped}(?:-{Segment}){{{minimumSegments},}}(?![A-Za-z0-9_])", RegexOptions.CultureInvariant);
        _brokenAfterHyphen = new Regex($@"{NotAfterIdCharacter}{escaped}-(?:{Segment}-)*\s+[A-Za-z0-9_]", RegexOptions.CultureInvariant);
        _wrappedInsideSegment = new Regex(
            $@"{NotAfterIdCharacter}{escaped}-(?:{Segment}-)*{Segment}[ \t]*{AnchorCells.LineBreakPattern}\s*[A-Z0-9_]+-[A-Za-z0-9_]",
            RegexOptions.CultureInvariant);
    }

    /// <summary>What every id starts with, before its first hyphen.</summary>
    public string Prefix { get; }

    /// <summary>Fewest segments after the prefix that a new id may have.</summary>
    public int MinimumSegments { get; }

    /// <summary>The rules configured in <paramref name="settings"/>.</summary>
    public static AnchorIdRules From(AnchorSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new AnchorIdRules(settings.IdPrefix, settings.MinimumIdSegments);
    }

    /// <summary>Whether <paramref name="id"/> may name a NEW anchor.</summary>
    public bool IsMintable(string id) => _mintable.IsMatch(id);

    /// <summary>Whether <paramref name="id"/> is spelled like an anchor id at all.</summary>
    public bool IsWellFormed(string id) => _wellFormed.IsMatch(id);

    /// <summary>Whether <paramref name="text"/> contains something shaped like an anchor id.</summary>
    public bool ContainsId(string text) => _token.IsMatch(text);

    /// <summary>Whether an Anchor cell holds nothing but one id in backticks.</summary>
    public bool IsBareBacktickedId(string cell) => _backticked.IsMatch(cell.Trim());

    /// <summary>
    /// The ids <paramref name="text"/> cites: each whole token spelt as a new id is, with at least
    /// <see cref="MinimumSegments"/> segments, but for one followed by <c>-</c>, <c>*</c> or <c>{</c> - a family or
    /// a pattern of ids (<c>D-AREA-TOPIC-*</c>), which names no row.
    /// </summary>
    /// <param name="text">A cell as it is stored.</param>
    /// <remarks>
    /// An id with fewer segments than a new one needs is left out: a row written before the rule may hold one, and
    /// so may prose - a figure, a code - that only looks like one, which a citation check must never refuse.
    /// </remarks>
    public IReadOnlySet<string> CitedIds(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        return _cited.Matches(text)
            .Where(match => match.Index + match.Length >= text.Length || text[match.Index + match.Length] is not ('-' or '*' or '{'))
            .Select(match => match.Value)
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// Where <paramref name="stored"/> holds an id broken after one of its hyphens - <c>D-AREA- TOPIC</c> - as a line
    /// break stored as a space leaves it, or a space written there; otherwise <see langword="null"/>.
    /// </summary>
    /// <param name="stored">A cell as it is stored, its line breaks spaces.</param>
    public string? BrokenAfterHyphen(string stored)
    {
        ArgumentNullException.ThrowIfNull(stored);

        return _brokenAfterHyphen.Match(stored) is { Success: true } match ? match.Value : null;
    }

    /// <summary>
    /// Where <paramref name="text"/> breaks a line inside an id - an id's start ending a line whose next opens with a
    /// token in capitals and hyphens, <c>D-AREA-TO</c> then <c>PIC-DETAIL</c> - otherwise <see langword="null"/>.
    /// </summary>
    /// <param name="text">A cell as it was written, its line breaks kept: stored, a break inside a segment reads as
    /// prose (<c>D-AREA-TOPIC MF-4</c> is real one-line text), so only the text as written can show it.</param>
    public string? WrappedInsideSegment(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        return _wrappedInsideSegment.Match(text) is { Success: true } match ? match.Value : null;
    }

    /// <summary>An id new anchors could use, for examples in documentation.</summary>
    public string Example()
    {
        string[] words = ["AREA", "TOPIC", "DETAIL"];
        var segments = Enumerable.Range(0, Math.Max(MinimumSegments, words.Length))
            .Select(index => index < words.Length ? words[index] : $"PART{index + 1}");

        return $"{Prefix}-{string.Join('-', segments)}";
    }

    /// <summary>The identity of a row: the id in its Anchor cell, or the cell's own text when it has none.</summary>
    /// <remarks>
    /// Decoration is ignored, so a struck-through id (<c>~~D-A-B-C~~</c>) is still the same row rather
    /// than a different one. The fallback never fails: a row whose first cell is not an id is still
    /// readable, and linting reports it.
    /// </remarks>
    public string Identify(string anchorCell)
    {
        ArgumentNullException.ThrowIfNull(anchorCell);

        var match = _token.Match(anchorCell);
        if (match.Success)
        {
            return match.Value;
        }

        var stripped = AnchorCells.StripDecoration(anchorCell);

        return stripped.Length switch
        {
            0 => "<blank>",
            > MaximumFallbackIdentityLength => stripped[..MaximumFallbackIdentityLength],
            _ => stripped,
        };
    }
}
