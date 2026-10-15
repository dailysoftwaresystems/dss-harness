using System.Buffers;

namespace RepoHarness.Core.Output;

/// <summary>
/// Where a line of text ends: the one set of characters this tool divides lines at, as
/// <see cref="string.ReplaceLineEndings(string)"/> reads them - a carriage return, a line feed, a form feed, the next-line
/// character, and Unicode's line and paragraph separators - a carriage return and the line feed after it being one
/// ending, and the other way round two.
/// </summary>
/// <remarks>
/// A vertical tab and the information separators U+001C to U+001E end no line here, though some readers end one there:
/// a line's number is the one an editor shows, and a reader of output sees it divided as it divides itself.
/// </remarks>
internal static class TextLines
{
    /// <summary>Every character that ends a line, alone or as the first of two.</summary>
    public static SearchValues<char> Endings { get; } = SearchValues.Create(['\r', '\n', '\f', (char)0x0085, (char)0x2028, (char)0x2029]);

    /// <summary>
    /// Each line of <paramref name="text"/> with the characters that end it, in order: the text after the last ending is a
    /// line too, with none, and empty where the text ends with one. So a text of no characters is one empty line, and
    /// joining every line with its ending gives the text back.
    /// </summary>
    /// <param name="text">The text.</param>
    public static IReadOnlyList<(string Text, string Ending)> Split(ReadOnlySpan<char> text)
    {
        var lines = new List<(string, string)>();
        var rest = text;

        while (rest.IndexOfAny(Endings) is >= 0 and var at)
        {
            var length = rest[at] == '\r' && at + 1 < rest.Length && rest[at + 1] == '\n' ? 2 : 1;

            lines.Add((rest[..at].ToString(), rest.Slice(at, length).ToString()));
            rest = rest[(at + length)..];
        }

        lines.Add((rest.ToString(), string.Empty));

        return lines;
    }
}
