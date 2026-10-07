using System.Text;

namespace RepoHarness.Core.Processes;

/// <summary>
/// Cuts text into lines as it arrives, the one rule for every reader of a child's output: the process runner reading the
/// child's pipes, and a phase's output read back from its log.
/// </summary>
/// <remarks>
/// <para>
/// A line ends at a line feed, and the carriage return before it, where its ending is one, goes with it; a last line with no
/// line feed after it is still a line. A carriage return anywhere else is the line's own and stays, as a progress bar's does.
/// </para>
/// <para>
/// Where a longest line is set, a line that runs longer is handed on in pieces, each a line of its own: one of at most that
/// many characters as soon as it has them, then the next, and the rest of it once its line feed arrives. Holding a line whole
/// until it ends is holding everything the child wrote until then, and a child that writes for gigabytes without a line feed
/// would hold all of it. A piece never ends between the two halves of a surrogate pair, which written apart would each become a
/// replacement character.
/// </para>
/// </remarks>
/// <param name="onLine">Receives each line, or piece of one, in order.</param>
/// <param name="longest">The most characters a line is handed on in, or <see langword="null"/> for every line whole.</param>
/// <param name="cut">
/// Where a line that reached <paramref name="longest"/> characters is cut, given those characters: by default after all of them.
/// What it leaves is the start of the next piece. A phase that masks secrets cuts where none is parted, so that each piece is
/// masked whole; an answer outside the line is no answer, and the line is cut after all of them.
/// </param>
/// <param name="carriageReturnEnds">
/// Whether a carriage return before a line feed is the line's ending rather than its own: so for what a program wrote,
/// whatever its platform; for a log this machine wrote, only where this machine ends its lines with one.
/// </param>
internal sealed class LineSplitter(Action<string> onLine, int? longest = null, Func<string, int>? cut = null, bool carriageReturnEnds = true)
{
    private readonly StringBuilder _line = new();

    /// <summary>
    /// The lines <paramref name="line"/>, which holds no line feed, is read back as once it is written down whole and its
    /// line ended: itself, or where it is longer than <paramref name="longest"/>, its pieces.
    /// </summary>
    /// <param name="line">A line.</param>
    /// <param name="longest">The most characters a line is read in.</param>
    public static IReadOnlyList<string> PiecesOf(string line, int longest)
    {
        ArgumentNullException.ThrowIfNull(line);

        if (line.Length <= longest)
        {
            return [line];
        }

        var pieces = new List<string>();

        // Its ending is the one written after it, so whatever it ends with is its own.
        var splitter = new LineSplitter(pieces.Add, longest, carriageReturnEnds: false);

        splitter.Add(line);
        splitter.End();

        return pieces;
    }

    /// <summary>Takes <paramref name="text"/>, handing on each line it completes, and each piece of a line it makes too long.</summary>
    /// <param name="text">What arrived next.</param>
    public void Add(ReadOnlySpan<char> text)
    {
        while (!text.IsEmpty)
        {
            var end = text.IndexOf('\n');

            Append(end < 0 ? text : text[..end]);

            if (end < 0)
            {
                return;
            }

            onLine(Ended());
            text = text[(end + 1)..];
        }
    }

    /// <summary>Hands on what is left once nothing more will arrive: a last line with no line feed after it is still a line.</summary>
    public void End()
    {
        if (_line.Length > 0)
        {
            onLine(Ended());
        }
    }

    /// <summary>The line held, without the carriage return that ended it where that is its ending, and holds nothing after.</summary>
    private string Ended()
    {
        var line = carriageReturnEnds && _line.Length > 0 && _line[^1] == '\r'
            ? _line.ToString(0, _line.Length - 1)
            : _line.ToString();

        _line.Clear();
        return line;
    }

    /// <summary>
    /// Adds <paramref name="part"/>, which holds no line feed, to the line held, handing on a piece first whenever the line has
    /// all the characters it may and more are coming. Cut only then, and never as it fills; and where a carriage return can end
    /// a line, one that arrives as the line is full is held past it until the next character says whether it does - cut
    /// before it, a line exactly as long as a piece and ended by a carriage return would be handed on as itself and an empty
    /// line the program never printed.
    /// </summary>
    private void Append(ReadOnlySpan<char> part)
    {
        if (longest is not { } most)
        {
            _line.Append(part);
            return;
        }

        while (!part.IsEmpty)
        {
            if (_line.Length == most && carriageReturnEnds && part[0] == '\r')
            {
                _line.Append('\r');
                part = part[1..];
                continue;
            }

            if (_line.Length >= most)
            {
                Piece(most);
            }

            var taken = Math.Min(most - _line.Length, part.Length);

            _line.Append(part[..taken]);
            part = part[taken..];
        }
    }

    /// <summary>
    /// Hands on the start of the first <paramref name="most"/> characters of the line held, where <c>cut</c> says, and holds
    /// the rest as the start of the next piece.
    /// </summary>
    private void Piece(int most)
    {
        var full = _line.ToString(0, most);
        var at = cut?.Invoke(full) is { } chosen && chosen > 0 && chosen <= full.Length ? chosen : full.Length;

        if (at > 1 && char.IsHighSurrogate(full[at - 1]))
        {
            at--;
        }

        onLine(full[..at]);
        _line.Remove(0, at);
    }
}
