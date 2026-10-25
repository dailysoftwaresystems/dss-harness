using DssHarness.Core.Output;

namespace DssHarness.Core.Web.Search;

/// <summary>What one source's part of a search came to: never an empty list for a failure.</summary>
public enum WebSearchOutcome
{
    /// <summary>Results read.</summary>
    Ok,

    /// <summary>The source answered, and found nothing.</summary>
    NoResults,

    /// <summary>The source asks to be left alone, or its pace is spent: it was not asked, or answered nothing for it.</summary>
    RateLimited,

    /// <summary>Its answer could not be read as its kind reads one: it changed, and nothing was read from it.</summary>
    Changed,

    /// <summary>No answer, a timeout, or a status that is no answer.</summary>
    Failed,

    /// <summary>A rule refused it before it was asked.</summary>
    Refused,
}

/// <summary>One result a source gave: its title, its address and a snippet, each text.</summary>
/// <param name="Title">Its title, reduced to text.</param>
/// <param name="Link">Its address, absolute.</param>
/// <param name="Snippet">What it says, reduced to text and cut to <see cref="MostSnippet"/> characters (<see cref="TextCut.AtWord"/>); empty where the source gave none.</param>
public sealed record WebSearchResult(string Title, string Link, string Snippet)
{
    /// <summary>The most characters a snippet keeps.</summary>
    public const int MostSnippet = 300;

    /// <summary>The most characters a title keeps.</summary>
    public const int MostTitle = 300;

    /// <summary>
    /// The result a source gave as <paramref name="title"/>, <paramref name="link"/> and <paramref name="snippet"/>: the
    /// title and the snippet text (<see cref="WebPageText.OfText"/>), each cut, the link made absolute against
    /// <paramref name="relativeTo"/>; <see langword="null"/> where it has no title or names no address. A field that is
    /// markup is reduced to its text first (<see cref="WebPageText.OfMarkup"/>), by the reader that knows it is: nothing
    /// here reads one as markup, for whether a field is markup is its source's or its format's word, never guessed.
    /// </summary>
    /// <param name="title">Its title, text.</param>
    /// <param name="link">Its address, as the source gave it.</param>
    /// <param name="snippet">What it says, text.</param>
    /// <param name="relativeTo">The address a relative link is read against: the answer's own.</param>
    public static WebSearchResult? Of(string? title, string? link, string? snippet, string? relativeTo)
    {
        var text = TextCut.AtWord(WebPageText.OfText(title ?? string.Empty), MostTitle);

        return text.Length == 0 || WebAddresses.Absolute(link, relativeTo) is not { } address
            ? null
            : new WebSearchResult(text, address, TextCut.AtWord(WebPageText.OfText(snippet ?? string.Empty), MostSnippet));
    }
}

/// <summary>
/// One source's answer to a query: its outcome, the results it gave, and what it said beside them - how many it holds, a
/// pause it asked for, results it gave that could not be read, how much of a site an index held.
/// </summary>
public sealed record WebSearchAnswer
{
    /// <summary>The most of an answer's items read: a query asks for a few, and an answer that holds more is read no further.</summary>
    public const int MostResults = 100;

    private WebSearchAnswer(WebSearchOutcome outcome, IReadOnlyList<WebSearchResult> results, string? why)
    {
        Outcome = outcome;
        Results = results;
        Why = why;
    }

    /// <summary>What the source's part came to.</summary>
    public WebSearchOutcome Outcome { get; }

    /// <summary>The results, in the source's order; empty but for <see cref="WebSearchOutcome.Ok"/>.</summary>
    public IReadOnlyList<WebSearchResult> Results { get; }

    /// <summary>Why it came to no results: a limit, a change, a failure, a refusal; <see langword="null"/> for results and for none.</summary>
    public string? Why { get; private init; }

    /// <summary>How many results the source says it holds for the query; <see langword="null"/> where it said not.</summary>
    public long? Total { get; init; }

    /// <summary>When the source may be asked again, as it asked; <see langword="null"/> where it asked for no pause.</summary>
    public DateTimeOffset? Until { get; init; }

    /// <summary>How many of the results it gave named no title or no address, and were left out.</summary>
    public int LeftOut { get; init; }

    /// <summary>What an index answered from: how much of its site it holds; <see langword="null"/> for an endpoint.</summary>
    public string? Held { get; init; }

    /// <summary>The results <paramref name="results"/>: <see cref="WebSearchOutcome.Ok"/>, or <see cref="WebSearchOutcome.NoResults"/> where there are none.</summary>
    /// <param name="results">The results read.</param>
    public static WebSearchAnswer Read(IReadOnlyList<WebSearchResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        return new(results.Count > 0 ? WebSearchOutcome.Ok : WebSearchOutcome.NoResults, results, null);
    }

    /// <summary>The source asks to be left alone until <paramref name="until"/>, as <paramref name="why"/> says.</summary>
    public static WebSearchAnswer Limited(DateTimeOffset until, string why) => new(WebSearchOutcome.RateLimited, [], Said(why)) { Until = until };

    /// <summary>The source's answer could not be read as its kind reads one, as <paramref name="why"/> says.</summary>
    public static WebSearchAnswer Changed(string why) => new(WebSearchOutcome.Changed, [], Said(why));

    /// <summary>The source gave no answer, or one that is none, as <paramref name="why"/> says.</summary>
    public static WebSearchAnswer Failed(string why) => new(WebSearchOutcome.Failed, [], Said(why));

    /// <summary>A rule refused the source before it was asked, as <paramref name="why"/> says.</summary>
    public static WebSearchAnswer Refused(string why) => new(WebSearchOutcome.Refused, [], Said(why));

    /// <summary>The same answer, its cause said as part of <paramref name="what"/>: what was being read when it came to it.</summary>
    /// <param name="what">What was being read, as <c>its 'search', looked up at '...'</c>.</param>
    public WebSearchAnswer Of(string what) => Why is null ? this : this with { Why = $"{what}: {Why}" };

    private static string Said(string why)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(why);
        return why;
    }
}
