using System.Globalization;
using DssHarness.Core.Output;

namespace DssHarness.Core.Web.Search;

/// <summary>
/// What an endpoint's status and headers answer a query before its body is read, for every kind alike, by the rules of
/// <see cref="WebRateLimit"/>: a limit, which answers it; a status that is no answer, which fails it, its body's own words
/// told; and a pause the headers ask for where the answer itself is good, which the source is held to after it.
/// </summary>
internal static class WebSearchStatus
{
    /// <summary>How much of a failed answer's body is told: enough for its own words, as Stack Exchange's <c>error_message</c>.</summary>
    private const int MostSaid = 200;

    /// <summary>
    /// What <paramref name="response"/>'s status and headers answer before its body is read: a rate limit or a failure, or
    /// <see langword="null"/> to read on - with <paramref name="pause"/>, a pause they ask for after it.
    /// </summary>
    /// <param name="response">The endpoint's response.</param>
    /// <param name="pause">When the source may be asked again, where its headers say it is spent; <see langword="null"/> otherwise.</param>
    public static WebSearchAnswer? Before(WebSearchResponse.Http response, out DateTimeOffset? pause)
    {
        ArgumentNullException.ThrowIfNull(response);

        pause = null;

        var limit = WebRateLimit.Read(response.Status, response.Headers, response.At);

        if (limit is { Limited: true, Until: var until })
        {
            return WebSearchAnswer.Limited(until, limit.Said
                ? $"it answered {Status(response.Status)} and asks to be left alone until {WebRateLimit.Said(until)}"
                : $"it answered {Status(response.Status)} and asks to be left alone, saying not for how long; it is left until {WebRateLimit.Said(until)}");
        }

        if (response.Status is < 200 or > 299)
        {
            // An error page the source says is HTML is markup, read to its text; any other body - JSON, plain words - is text as it stands.
            var said = HarnessFetch.IsHtml(response.Type) ? WebPageText.OfMarkup(response.Body) : WebPageText.OfText(response.Body);

            return WebSearchAnswer.Failed(said.Length == 0
                ? $"it answered {Status(response.Status)}"
                : $"it answered {Status(response.Status)}: {TextCut.AtWord(said, MostSaid)}");
        }

        pause = limit?.Until;
        return null;
    }

    private static string Status(int status) => status.ToString(CultureInfo.InvariantCulture);
}
