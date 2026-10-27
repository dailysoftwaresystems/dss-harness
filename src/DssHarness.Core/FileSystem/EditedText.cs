namespace DssHarness.Core.FileSystem;

/// <summary>
/// A text file as an edit changes it: its encoding, its lines each with the ending it has, and so whether it ends with a
/// newline - every one kept as found, so an edit changes the lines it is asked to and nothing else of the file. The one
/// place a file an agent edits is changed, by a string replaced, lines inserted, or a range of lines replaced.
/// </summary>
/// <remarks>
/// Lines are cut by <see cref="Codebase.CodeFileText.Spans"/> - at CR LF, LF and CR - so a line number here is the one a
/// file an agent viewed, and a span the codebase graph names, count; an empty file has no line here. Text an agent gives is written with the
/// file's own line ending, where the file holds one kind alone: a model writes LF, and a file of CR LF keeps CR LF.
/// </remarks>
public sealed class EditedText
{
    private const string Lf = "\n";

    private readonly EncodedText _found;
    private readonly List<(string Text, string Ending)> _lines;

    private EditedText(EncodedText found)
    {
        _found = found;
        _lines = Split(found.Text);
        Ending = OneEnding(_lines);
    }

    /// <summary>The ending every line of the file has, where all that end have one kind; null where they have several, or none ends.</summary>
    public string? Ending { get; }

    /// <summary>How many lines the file holds: none where it is empty.</summary>
    public int LineCount => _lines.Count;

    /// <summary>The file's whole text as it now stands.</summary>
    public string Text => Joined(_lines);

    /// <summary>The file's text as it was found, its encoding kept: read from <paramref name="found"/>.</summary>
    /// <param name="found">The file's text and the encoding it was found in.</param>
    public static EditedText Of(EncodedText found)
    {
        ArgumentNullException.ThrowIfNull(found);
        return new EditedText(found);
    }

    /// <summary>The file's bytes as it now stands, in the encoding it was found in, with its mark where it had one.</summary>
    public byte[] Bytes() => _found.Encode(Text);

    /// <summary>
    /// <paramref name="given"/> written with the file's own line ending, where the file holds one kind alone: every CR LF,
    /// LF and lone CR it holds made that ending. Given as it is where the file mixes endings, or has none. A file of one
    /// kind holds no break of another, so text given in another stands in it only so.
    /// </summary>
    /// <param name="given">Text an agent gave.</param>
    public string InFileEndings(string given)
    {
        ArgumentNullException.ThrowIfNull(given);

        return Ending is not { } ending
            ? given
            : string.Concat(Codebase.CodeFileText.Spans(given).Select(line => given[line.Start..line.End] + (line.Next > line.End ? ending : string.Empty)));
    }

    /// <summary>How many times <paramref name="old"/> stands in the file in its endings, without overlap: as <see cref="Replace"/> finds it.</summary>
    /// <param name="old">The text to find; never empty.</param>
    public int Count(string old) => Occurrences(Text, Sought(old));

    /// <summary>
    /// Replaces <paramref name="old"/> with <paramref name="replacement"/>, each in the file's endings where the file holds
    /// one kind: every occurrence, or the one there is. The caller has counted them (<see cref="Count"/>).
    /// </summary>
    /// <param name="old">The text to replace; never empty.</param>
    /// <param name="replacement">What replaces it.</param>
    public void Replace(string old, string replacement)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        Reset(Text.Replace(Sought(old), InFileEndings(replacement), StringComparison.Ordinal));
    }

    /// <summary>
    /// Inserts <paramref name="text"/> as lines before line <paramref name="line"/>, from 1 - one past the last appending -
    /// each ending as the file's lines end, so a file that ends with no newline still does, and one that ends with one
    /// still does.
    /// </summary>
    /// <param name="line">The line it goes before, from 1; one past the last line to append.</param>
    /// <param name="text">The lines to insert.</param>
    /// <exception cref="ArgumentOutOfRangeException">The line is not one of the file's, nor one past its last.</exception>
    public void Insert(int line, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentOutOfRangeException.ThrowIfLessThan(line, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(line, LineCount + 1);

        ReplaceLines(line, line - 1, text);
    }

    /// <summary>
    /// Replaces lines <paramref name="start"/> to <paramref name="end"/>, from 1 and inclusive, with <paramref name="text"/>
    /// as lines - none replaced where <paramref name="end"/> is one before <paramref name="start"/>, which inserts - the
    /// last of them ending as the last line replaced ended, so the file's final newline, or its lack of one, is kept.
    /// </summary>
    /// <param name="start">The first line replaced, from 1.</param>
    /// <param name="end">The last, inclusive; one before <paramref name="start"/> to replace none.</param>
    /// <param name="text">The lines that take their place; empty to take them out.</param>
    /// <exception cref="ArgumentOutOfRangeException">The lines are not the file's.</exception>
    public void ReplaceLines(int start, int end, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentOutOfRangeException.ThrowIfLessThan(start, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(end, start - 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(end, LineCount);

        var given = Split(text);
        var ending = Ending ?? NearestEnding(start) ?? Lf;
        var givenEnds = given.Count > 0 && given[^1].Ending.Length > 0;

        // The lines given end as the file's do; the last of them ends as what it stands before needs.
        for (var at = 0; at < given.Count; at++)
        {
            given[at] = (given[at].Text, ending);
        }

        if (given.Count > 0)
        {
            string last;

            if (end >= start)
            {
                // In place of lines: as the last line replaced ended.
                last = _lines[end - 1].Ending;
            }
            else if (start <= LineCount)
            {
                // Before a line, which stays after it.
                last = ending;
            }
            else if (LineCount == 0)
            {
                // The whole of an empty file: ending as the text given ends.
                last = givenEnds ? ending : string.Empty;
            }
            else
            {
                // Appended: the file ends as it did. After a last line with no ending, that line now ends, and the last
                // line given is the one with none.
                last = _lines[^1].Ending;

                if (last.Length == 0)
                {
                    _lines[^1] = (_lines[^1].Text, ending);
                }
            }

            given[^1] = (given[^1].Text, last);
        }
        else if (end >= start && end == LineCount && start > 1 && _lines[end - 1].Ending.Length == 0)
        {
            // The last lines taken out of a file that ends with no newline: the line now last loses its ending, as the
            // one taken out had none.
            _lines[start - 2] = (_lines[start - 2].Text, string.Empty);
        }

        _lines.RemoveRange(start - 1, end - start + 1);
        _lines.InsertRange(start - 1, given);
    }

    /// <summary><paramref name="old"/> as the file would hold it: in its endings.</summary>
    private string Sought(string old)
    {
        ArgumentException.ThrowIfNullOrEmpty(old);
        return InFileEndings(old);
    }

    /// <summary>Takes <paramref name="text"/> as the file's whole text.</summary>
    private void Reset(string text)
    {
        _lines.Clear();
        _lines.AddRange(Split(text));
    }

    /// <summary>The ending of the line nearest <paramref name="line"/> that has one: the one before it, else after.</summary>
    private string? NearestEnding(int line)
    {
        for (var at = Math.Min(line - 1, _lines.Count) - 1; at >= 0; at--)
        {
            if (_lines[at].Ending.Length > 0)
            {
                return _lines[at].Ending;
            }
        }

        return _lines.Skip(line - 1).Select(held => held.Ending).FirstOrDefault(ending => ending.Length > 0);
    }

    /// <summary>How many times <paramref name="sought"/> stands in <paramref name="text"/>, without overlap.</summary>
    private static int Occurrences(string text, string sought)
    {
        var count = 0;

        for (var at = text.IndexOf(sought, StringComparison.Ordinal); at >= 0; at = text.IndexOf(sought, at + sought.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    /// <summary><paramref name="text"/>'s lines, each with its ending, as the one cut makes them; none for an empty text.</summary>
    private static List<(string Text, string Ending)> Split(string text)
        => text.Length == 0 ? [] : [.. Codebase.CodeFileText.Spans(text).Select(line => (text[line.Start..line.End], text[line.End..line.Next]))];

    /// <summary>The one ending every line that ends has; null where they have several kinds, or none ends.</summary>
    private static string? OneEnding(IReadOnlyList<(string Text, string Ending)> lines)
    {
        var endings = lines.Select(line => line.Ending).Where(ending => ending.Length > 0).Distinct(StringComparer.Ordinal).ToList();

        return endings.Count == 1 ? endings[0] : null;
    }

    /// <summary>The lines joined, each with its ending.</summary>
    private static string Joined(IEnumerable<(string Text, string Ending)> lines)
        => string.Concat(lines.Select(line => line.Text + line.Ending));
}
