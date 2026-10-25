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
    public static string Signature(string header) => Cut(Whole(header), FileExtract.MostSignature);

    /// <summary>
    /// The first sentence of <paramref name="text"/>, one line of what the code says of itself - up to the first <c>.</c>,
    /// <c>!</c> or <c>?</c> that a space or the end follows - cut to fit a node's summary.
    /// </summary>
    /// <param name="text">The line.</param>
    public static string Summary(string text)
    {
        var stop = Sentence().Match(text);
        return Cut(Whole(stop.Success ? text[..(stop.Index + 1)] : text), FileExtract.MostSummary);
    }

    /// <summary>
    /// <paramref name="text"/> as whole UTF-16, as a record holds it: each surrogate apart from its pair - a file cut inside
    /// a character - the replacement character.
    /// </summary>
    /// <param name="text">Text read from a file.</param>
    public static string Whole(string text)
    {
        var apart = FileExtract.Apart(text, 0);

        if (apart < 0)
        {
            return text;
        }

        var whole = text.ToCharArray();

        for (; apart >= 0; apart = FileExtract.Apart(text, apart + 1))
        {
            whole[apart] = '\uFFFD';
        }

        return new string(whole);
    }

    /// <summary><paramref name="name"/> joined to <paramref name="scope"/>, the qualified name it is declared in, by the model's separator, a dot; itself at the top.</summary>
    /// <param name="scope">The qualified name of the scope it is declared in; <see langword="null"/> at the top.</param>
    /// <param name="name">Its name.</param>
    public static string Qualify(string? scope, string name) => scope is null ? name : $"{scope}.{name}";

    /// <summary><paramref name="text"/> cut, where it is longer than <paramref name="most"/>, at a word before it with an ellipsis - never inside a surrogate pair.</summary>
    private static string Cut(string text, int most)
    {
        if (text.Length <= most)
        {
            return text;
        }

        var cut = Output.TextCut.Head(text, most - 1);
        var space = cut.LastIndexOf(' ');

        return (space > most / 2 ? cut[..space] : cut).TrimEnd() + "…";
    }

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Spaces();

    [GeneratedRegex(@"[.!?](?=\s|$)", RegexOptions.CultureInvariant)]
    private static partial Regex Sentence();
}
