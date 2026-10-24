using System.Text.RegularExpressions;

namespace DssHarness.Core.Codebase.Languages;

/// <summary>
/// How every implementation fits the text it gives a node into the model's bounds: a signature within
/// <see cref="FileExtract.MostSignature"/> characters, and a summary - the first sentence of what the code says of itself
/// - within <see cref="FileExtract.MostSummary"/>. One place, so every language cuts alike.
/// </summary>
internal static partial class ExtractText
{
    /// <summary><paramref name="text"/> on one line: each run of white space, line breaks among it, one space.</summary>
    /// <param name="text">Text written over one line or more.</param>
    public static string Flat(string text) => Spaces().Replace(text, " ").Trim();

    /// <summary><paramref name="header"/>, a declaration's header on one line, cut to fit a node's signature.</summary>
    /// <param name="header">The header.</param>
    public static string Signature(string header) => Cut(header, FileExtract.MostSignature);

    /// <summary>
    /// The first sentence of <paramref name="text"/>, one line of what the code says of itself - up to the first <c>.</c>,
    /// <c>!</c> or <c>?</c> that a space or the end follows - cut to fit a node's summary.
    /// </summary>
    /// <param name="text">The line.</param>
    public static string Summary(string text)
    {
        var stop = Sentence().Match(text);
        return Cut(stop.Success ? text[..(stop.Index + 1)] : text, FileExtract.MostSummary);
    }

    /// <summary><paramref name="text"/> cut, where it is longer than <paramref name="most"/>, at a word before it with an ellipsis - never inside a surrogate pair.</summary>
    private static string Cut(string text, int most)
    {
        if (text.Length <= most)
        {
            return text;
        }

        var cut = text[..(most - 1)];
        cut = char.IsHighSurrogate(cut[^1]) ? cut[..^1] : cut;
        var space = cut.LastIndexOf(' ');

        return (space > most / 2 ? cut[..space] : cut).TrimEnd() + "…";
    }

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Spaces();

    [GeneratedRegex(@"[.!?](?=\s|$)", RegexOptions.CultureInvariant)]
    private static partial Regex Sentence();
}
