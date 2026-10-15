namespace RepoHarness.Core.Mutations;

/// <summary>
/// Whether a run said an arm's diagnostic: the diagnostic's text, exactly, anywhere in the lines of the run's output, its
/// line endings read as one - matched a line at a time as the run's log is read back, so a run of any length is never
/// held whole. A line longer than a log keeps whole is read in the pieces it was kept in, as every reader of a phase's
/// output reads it, so a diagnostic lying across the end of a piece is not found.
/// </summary>
/// <remarks>
/// A text of <c>n</c> lines can begin part way along one line of output and end part way along another, so it lies
/// within any <c>n</c> lines in a row that hold it; the window keeps the last <c>n + 1</c>, and is searched each time a
/// line arrives, so a diagnostic is found however the output is broken into lines - and only once a line holding its
/// end has arrived, as it would be read in the whole output.
/// </remarks>
public sealed class DiagWindow
{
    private readonly string _text;
    private readonly int _size;
    private readonly Queue<string> _lines = new();

    /// <summary>Starts watching for <paramref name="text"/>.</summary>
    /// <param name="text">The diagnostic, as its file holds it; a CRLF in it is read as a line feed.</param>
    public DiagWindow(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        _text = text.ReplaceLineEndings("\n");
        _size = _text.Count(character => character == '\n') + 2;
    }

    /// <summary>Whether the diagnostic has appeared in the lines fed so far.</summary>
    public bool Found { get; private set; }

    /// <summary>Feeds the next line of output, without its line ending.</summary>
    /// <param name="line">The line; a carriage return ending it is dropped, as a Windows program ends its lines with one.</param>
    public void Feed(string line)
    {
        ArgumentNullException.ThrowIfNull(line);

        if (Found)
        {
            return;
        }

        _lines.Enqueue(line.TrimEnd('\r'));

        if (_lines.Count > _size)
        {
            _lines.Dequeue();
        }

        Found = string.Join('\n', _lines).Contains(_text, StringComparison.Ordinal);
    }

    /// <summary>Whether <paramref name="text"/> appears in <paramref name="lines"/>, fed one at a time.</summary>
    /// <param name="text">The diagnostic.</param>
    /// <param name="lines">The output's lines.</param>
    public static bool Appears(string text, IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var window = new DiagWindow(text);

        foreach (var line in lines)
        {
            window.Feed(line);

            if (window.Found)
            {
                return true;
            }
        }

        return false;
    }
}
