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
    public static List<TLine> Spans<TLine>(string text, Func<int, int, int, TLine> line) => [.. Streamed(text, line)];

    /// <summary>Each line of <paramref name="text"/>, in order, its ending left off: the lines <see cref="Spans"/> cuts, as texts.</summary>
    /// <param name="text">The text.</param>
    public static List<string> Lines(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        return Spans(text, (start, end, _) => text[start..end]);
    }

    /// <summary>
    /// Each line of <paramref name="text"/> as <see cref="Spans"/> makes them, made one at a time as they are read - so a
    /// reader that needs each line once, in order, never holds a second copy of the text as a list of its lines.
    /// </summary>
    /// <typeparam name="TLine">What a line is made into.</typeparam>
    /// <param name="text">The text.</param>
    /// <param name="line">Makes a line of its start, its end and its next.</param>
    public static IEnumerable<TLine> Streamed<TLine>(string text, Func<int, int, int, TLine> line)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(line);

        return Each(text, line);

        static IEnumerable<TLine> Each(string text, Func<int, int, int, TLine> line)
        {
            var start = 0;
            var made = false;

            for (var at = 0; at < text.Length; at++)
            {
                if (Ends(text[at]))
                {
                    var end = at;

                    if (at + 1 < text.Length && Joined(text[at], text[at + 1]))
                    {
                        at++;
                    }

                    made = true;
                    yield return line(start, end, at + 1);
                    start = at + 1;
                }
            }

            if (start < text.Length || !made)
            {
                yield return line(start, text.Length, text.Length);
            }
        }
    }

    /// <summary>How many lines <paramref name="text"/> holds by this rule - as many as <see cref="Spans"/> makes - counted without making them.</summary>
    /// <param name="text">The text.</param>
    public static int Count(ReadOnlySpan<char> text)
    {
        var lines = 0;
        var open = false;

        for (var at = 0; at < text.Length; at++)
        {
            if (Ends(text[at]))
            {
                if (at + 1 < text.Length && Joined(text[at], text[at + 1]))
                {
                    at++;
                }

                lines++;
                open = false;
            }
            else
            {
                open = true;
            }
        }

        return open || lines == 0 ? lines + 1 : lines;
    }
}

/// <summary>
/// A line as the file that holds it numbers it, by the one rule of where lines end (<see cref="LineEnds"/>): the number a
/// reader opening the file finds it at, and the last, where what was read as one line holds an ending of the rule's
/// inside it - a carriage return a program printed alone - and a reader's view counts it as more.
/// </summary>
/// <param name="Number">The number of the line's first line in its file.</param>
/// <param name="Last">The number of its last; <paramref name="Number"/> where it is one line of the file.</param>
/// <param name="Text">The line.</param>
public readonly record struct NumberedLine(int Number, int Last, string Text);
