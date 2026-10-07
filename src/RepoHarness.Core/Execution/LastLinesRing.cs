using System.Buffers;

namespace RepoHarness.Core.Execution;

/// <summary>
/// The last lines of a phase's output, kept as they arrive in a ring of a fixed number of them, so that keeping them costs
/// the same whatever the child prints: the one rule for <see cref="PhaseResult.LastLines"/>, whether a phase is running or
/// its output is read back.
/// </summary>
/// <remarks>
/// A line is shown divided however it divides itself - at a carriage return alone, a form feed, a Unicode line separator -
/// as a reader looking at it sees it, and no empty line comes after the last that is not: empty lines are counted rather
/// than kept, and take their places only once a line that is not empty follows them.
/// </remarks>
/// <param name="capacity">How many lines are kept.</param>
internal sealed class LastLinesRing(int capacity)
{
    /// <summary>
    /// Everything <see cref="string.ReplaceLineEndings(string)"/> reads as ending a line: a carriage return, a line feed, a form
    /// feed, the next-line character, and Unicode's line and paragraph separators.
    /// </summary>
    private static readonly SearchValues<char> Endings = SearchValues.Create(['\r', '\n', '\f', (char)0x0085, (char)0x2028, (char)0x2029]);

    private readonly string[] _ring = capacity > 0
        ? new string[capacity]
        : throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "A ring keeps at least one line.");

    /// <summary>Where the next line goes.</summary>
    private int _next;

    /// <summary>How many lines are kept, until the ring is full.</summary>
    private int _count;

    /// <summary>The empty lines since the last that was not, which take their places only once one that is not follows them.</summary>
    private long _empty;

    /// <summary>Takes the next line.</summary>
    /// <param name="line">A line as the log keeps it.</param>
    public void Add(string line)
    {
        ArgumentNullException.ThrowIfNull(line);

        if (!line.AsSpan().ContainsAny(Endings))
        {
            Part(line);
            return;
        }

        // Ended as the log ends it, so that a carriage return at its end and the line feed after it are one ending, as they
        // are in what was written, and not a line and an empty one.
        var parts = (line + "\n").ReplaceLineEndings("\n").Split('\n');

        for (var index = 0; index < parts.Length - 1; index++)
        {
            Part(parts[index]);
        }
    }

    /// <summary>The lines kept, oldest first.</summary>
    public IReadOnlyList<string> ToList()
    {
        var lines = new string[_count];

        for (var index = 0; index < _count; index++)
        {
            lines[index] = _ring[(_next - _count + index + _ring.Length) % _ring.Length];
        }

        return lines;
    }

    /// <summary>Takes one line as a reader sees it, which holds no ending of any kind.</summary>
    private void Part(string line)
    {
        if (line.Length == 0)
        {
            _empty++;
            return;
        }

        for (var empty = Math.Min(_empty, _ring.Length); empty > 0; empty--)
        {
            Keep(string.Empty);
        }

        _empty = 0;
        Keep(line);
    }

    private void Keep(string line)
    {
        _ring[_next] = line;
        _next = (_next + 1) % _ring.Length;
        _count = Math.Min(_count + 1, _ring.Length);
    }
}
