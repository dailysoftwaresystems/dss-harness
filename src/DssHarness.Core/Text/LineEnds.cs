namespace DssHarness.Core.Text;

/// <summary>
/// The one rule of where a text's lines end: at each CR LF, LF and CR, an ending the text ends with closing its last line
/// rather than opening another, and an empty text one empty line. The codebase graph's lines of a file and a text's
/// pages streamed from its file are cut by it alike, so a line a page numbers is the line the graph names.
/// </summary>
public static class LineEnds
{
    /// <summary>Whether <paramref name="character"/> ends a line: a carriage return or a line feed.</summary>
    /// <param name="character">The character.</param>
    public static bool Ends(char character) => character is '\r' or '\n';

    /// <summary>Where the first character that ends a line stands in <paramref name="text"/>; -1 where none does.</summary>
    /// <param name="text">The text looked at.</param>
    public static int IndexOf(ReadOnlySpan<char> text) => text.IndexOfAny('\r', '\n');

    /// <summary>
    /// Whether the ending <paramref name="ending"/> followed by <paramref name="next"/> is one ending of two characters:
    /// a carriage return then a line feed. Any other ending is its one character.
    /// </summary>
    /// <param name="ending">The character that ends the line.</param>
    /// <param name="next">The character after it.</param>
    public static bool Joined(char ending, char next) => ending == '\r' && next == '\n';

    /// <summary>
    /// Each line of <paramref name="text"/>, in order, as <paramref name="line"/> makes it of where the line begins, where
    /// its text ends before its ending, and where its ending ends - where the next line begins, or the text's end.
    /// </summary>
    /// <typeparam name="TLine">What a line is made into.</typeparam>
    /// <param name="text">The text.</param>
    /// <param name="line">Makes a line of its start, its end and its next.</param>
    public static List<TLine> Spans<TLine>(string text, Func<int, int, int, TLine> line)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(line);

        var lines = new List<TLine>();
        var start = 0;

        for (var at = 0; at < text.Length; at++)
        {
            if (Ends(text[at]))
            {
                var end = at;

                if (at + 1 < text.Length && Joined(text[at], text[at + 1]))
                {
                    at++;
                }

                lines.Add(line(start, end, at + 1));
                start = at + 1;
            }
        }

        if (start < text.Length || lines.Count == 0)
        {
            lines.Add(line(start, text.Length, text.Length));
        }

        return lines;
    }
}
