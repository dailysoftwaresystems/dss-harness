namespace DssHarness.Core.Codebase;

/// <summary>
/// Which lines of a newer text are not an older one's: the shortest edit script between their lines (Myers' O(ND) walk),
/// after the lines both begin and end with are set aside, so what a change touched is told line by line. Past
/// <see cref="MostEdits"/> edits the lines between the common ends are taken as changed whole: the cost is bounded, and
/// nothing changed is ever said unchanged.
/// </summary>
public static class CodeLineDiff
{
    /// <summary>The most lines added and taken out the walk looks for before it takes what lies between the common ends as changed whole.</summary>
    public const int MostEdits = 1000;

    /// <summary>
    /// The places <paramref name="after"/> differs from <paramref name="before"/>, in order: each run of its lines that are
    /// not <paramref name="before"/>'s, with the lines of <paramref name="before"/> it stands for, and each place lines were
    /// only taken out.
    /// </summary>
    /// <param name="before">The older text's lines.</param>
    /// <param name="after">The newer text's lines.</param>
    public static IReadOnlyList<CodeLineHunk> Hunks(IReadOnlyList<string> before, IReadOnlyList<string> after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        var head = 0;

        while (head < before.Count && head < after.Count && string.Equals(before[head], after[head], StringComparison.Ordinal))
        {
            head++;
        }

        var tail = 0;

        while (tail < before.Count - head && tail < after.Count - head
            && string.Equals(before[before.Count - 1 - tail], after[after.Count - 1 - tail], StringComparison.Ordinal))
        {
            tail++;
        }

        var older = before.Count - head - tail;
        var newer = after.Count - head - tail;

        if (older == 0 && newer == 0)
        {
            return [];
        }

        if (older == 0 || newer == 0 || Script(before, after, head, older, newer) is not { } script)
        {
            return [new CodeLineHunk(head + 1, newer, older, head + 1)];
        }

        return Hunked(script, head);
    }

    /// <summary>
    /// The edit script between the middles - for each line of either, whether it is kept - or null where it takes more than
    /// <see cref="MostEdits"/> edits. Each step's trace keeps only the diagonals that step could reach and their neighbours,
    /// so the trace grows as the square of the edits, not of the lines.
    /// </summary>
    private static List<(bool Kept, bool Newer)>? Script(IReadOnlyList<string> before, IReadOnlyList<string> after, int head, int older, int newer)
    {
        var most = Math.Min(older + newer, MostEdits);
        var offset = most + 1;
        var reach = new int[(2 * most) + 3];
        var trace = new List<int[]>();

        for (var edits = 0; edits <= most; edits++)
        {
            // Diagonals -edits - 1 to edits + 1: those this step reads from.
            trace.Add(reach[(offset - edits - 1)..(offset + edits + 2)]);

            for (var diagonal = -edits; diagonal <= edits; diagonal += 2)
            {
                // Down (a line of the newer text added) where the walk must, or where the diagonal above reached further.
                var x = diagonal == -edits || (diagonal != edits && reach[offset + diagonal - 1] < reach[offset + diagonal + 1])
                    ? reach[offset + diagonal + 1]
                    : reach[offset + diagonal - 1] + 1;
                var y = x - diagonal;

                while (x < older && y < newer && string.Equals(before[head + x], after[head + y], StringComparison.Ordinal))
                {
                    x++;
                    y++;
                }

                reach[offset + diagonal] = x;

                if (x >= older && y >= newer)
                {
                    return Backtracked(trace, older, newer);
                }
            }
        }

        return null;
    }

    /// <summary>The script the walk found, read back from its end: each line kept, taken out of the older text, or added in the newer.</summary>
    private static List<(bool Kept, bool Newer)> Backtracked(List<int[]> trace, int older, int newer)
    {
        var steps = new List<(bool Kept, bool Newer)>();
        var x = older;
        var y = newer;

        for (var edits = trace.Count - 1; edits >= 0; edits--)
        {
            // The step's trace holds diagonal d at d + edits + 1.
            var reach = trace[edits];
            int At(int diagonal) => reach[diagonal + edits + 1];

            var diagonal = x - y;
            var down = diagonal == -edits || (diagonal != edits && At(diagonal - 1) < At(diagonal + 1));
            var previous = down ? diagonal + 1 : diagonal - 1;
            var fromX = edits == 0 ? 0 : At(previous);
            var fromY = fromX - previous;

            while (x > fromX && y > fromY && (edits > 0 || (x > 0 && y > 0)))
            {
                steps.Add((true, true));
                x--;
                y--;
            }

            if (edits == 0)
            {
                break;
            }

            if (down)
            {
                steps.Add((false, true));
                y--;
            }
            else
            {
                steps.Add((false, false));
                x--;
            }
        }

        steps.Reverse();
        return steps;
    }

    /// <summary>The script's runs of lines not kept, each a hunk of the newer text's lines and the older's it stands for, from 1, the common head counted.</summary>
    private static List<CodeLineHunk> Hunked(List<(bool Kept, bool Newer)> script, int head)
    {
        var hunks = new List<CodeLineHunk>();
        var line = head;
        var old = head;
        var at = 0;

        while (at < script.Count)
        {
            if (script[at].Kept)
            {
                line++;
                old++;
                at++;
                continue;
            }

            var start = line + 1;
            var from = old + 1;
            var added = 0;
            var taken = 0;

            for (; at < script.Count && !script[at].Kept; at++)
            {
                if (script[at].Newer)
                {
                    added++;
                    line++;
                }
                else
                {
                    taken++;
                    old++;
                }
            }

            hunks.Add(new CodeLineHunk(start, added, taken, from));
        }

        return hunks;
    }
}

/// <summary>One place a newer text differs from an older one.</summary>
/// <param name="Start">The newer text's first line that is not the older's, from 1; where lines were only taken out, the line after the place they were.</param>
/// <param name="Count">How many of the newer text's lines from <paramref name="Start"/> are new; 0 where lines were only taken out.</param>
/// <param name="Taken">How many of the older text's lines they stand for: those taken out or replaced.</param>
/// <param name="From">The older text's first line they stand for, from 1; where lines were only added, the older line they were put before.</param>
public readonly record struct CodeLineHunk(int Start, int Count, int Taken, int From)
{
    /// <summary>The newer text's last line of the hunk; one before <see cref="Start"/> where it adds none.</summary>
    public int End => Start + Count - 1;

    /// <summary>The older text's last line the hunk stands for; one before <see cref="From"/> where it takes none.</summary>
    public int TakenEnd => From + Taken - 1;
}
