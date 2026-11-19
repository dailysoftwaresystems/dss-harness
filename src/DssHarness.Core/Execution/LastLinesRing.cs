using DssHarness.Core.Output;

namespace DssHarness.Core.Execution;

/// <summary>
/// The last lines of a phase's output that fit a page's room, kept as they arrive, so that keeping them costs a page
/// whatever the child prints: the one rule for <see cref="PhaseResult.LastLines"/>, whether a phase is running or its
/// output is read back. A page's room bounds what one look gives, never what is kept: the whole output is in the phase's
/// log, which a reader pages through for the rest.
/// </summary>
/// <remarks>
/// <para>
/// A line costs its characters and its ending, as a line of a page does (<see cref="Pages.PageRoom"/>): the oldest lines
/// go first once the room is spent, and a last line wider than the whole room is kept as its end, marked where it went
/// on before it.
/// </para>
/// <para>
/// A line is shown divided however it divides itself - at a carriage return alone, a form feed, a Unicode line separator -
/// as a reader looking at it sees it, and no empty line comes after the last that is not: empty lines are counted rather
/// than kept, and take their places only once a line that is not empty follows them.
/// </para>
/// </remarks>
/// <param name="room">How many characters the lines kept may take, each with its ending.</param>
internal sealed class LastLinesRing(int room)
{
    private readonly Queue<string> _kept = new();

    private readonly int _room = room > 1
        ? room
        : throw new ArgumentOutOfRangeException(nameof(room), room, "A page's room holds at least one character and its ending.");

    /// <summary>What the lines kept take of the room, each with its ending.</summary>
    private long _taken;

    /// <summary>The empty lines since the last that was not, which take their places only once one that is not follows them.</summary>
    private long _empty;

    /// <summary>Takes the next line.</summary>
    /// <param name="line">A line as the log keeps it.</param>
    public void Add(string line)
    {
        ArgumentNullException.ThrowIfNull(line);

        if (!line.AsSpan().ContainsAny(TextLines.Endings))
        {
            Part(line);
            return;
        }

        // Ended as the log ends it, so that a carriage return at its end and the line feed after it are one ending, as they
        // are in what was written, and not a line and an empty one.
        var parts = TextLines.Split(line + "\n");

        for (var index = 0; index < parts.Count - 1; index++)
        {
            Part(parts[index].Text);
        }
    }

    /// <summary>The lines kept, oldest first.</summary>
    public IReadOnlyList<string> ToList() => [.. _kept];

    /// <summary>Takes one line as a reader sees it, which holds no ending of any kind.</summary>
    private void Part(string line)
    {
        if (line.Length == 0)
        {
            _empty++;
            return;
        }

        // Only as many as the room could hold: more would go again at once.
        for (var empty = Math.Min(_empty, _room); empty > 0; empty--)
        {
            Keep(string.Empty);
        }

        _empty = 0;
        Keep(line);
    }

    private void Keep(string line)
    {
        // A line wider than the whole room is its end, marked where it went on: what a reader is shown first is where the
        // line ended.
        var kept = line.Length + 1 > _room ? TextCut.Ellipsis + TextCut.Tail(line, _room - 2) : line;

        _kept.Enqueue(kept);
        _taken += kept.Length + 1;

        while (_taken > _room)
        {
            _taken -= _kept.Dequeue().Length + 1;
        }
    }
}
