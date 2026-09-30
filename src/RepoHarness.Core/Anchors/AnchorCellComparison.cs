namespace RepoHarness.Core.Anchors;

/// <summary>What becomes of a stored cell's text when the text declared for it is written.</summary>
public enum AnchorCellFate
{
    /// <summary>The cell would store exactly what it holds.</summary>
    Same,

    /// <summary>The words agree, and only the spacing between them changes.</summary>
    Respaced,

    /// <summary>The stored text survives, whole and word for word, inside the new: an addendum.</summary>
    Kept,

    /// <summary>The stored cell was empty.</summary>
    Filled,

    /// <summary>The stored text does not survive: writing it rewrites the row's history.</summary>
    Lost,
}

/// <summary>A stored cell and the text that would replace it, compared word by word.</summary>
/// <remarks>
/// A row's cells are its history: what was found, and what closed it. A declaration that replaces a cell with text that
/// does not keep what was there loses that history unseen, where a reader would have read two blobs as the same.
/// </remarks>
public static class AnchorCellComparison
{
    /// <summary>How many unchanged words a diff keeps on each side of a change.</summary>
    private const int ContextWords = 4;

    /// <summary>
    /// The most word pairs a diff compares: past it, the two texts are shown as one removed and the other written,
    /// never a comparison left to run for as long as a cell is long.
    /// </summary>
    private const long MostComparisons = 4_000_000;

    /// <summary>What writing <paramref name="written"/> over <paramref name="stored"/> does to the stored text.</summary>
    /// <param name="stored">The cell as it reads.</param>
    /// <param name="written">The text as the cell would store it.</param>
    public static AnchorCellFate Fate(string stored, string written)
    {
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(written);

        if (string.Equals(stored, written, StringComparison.Ordinal))
        {
            return AnchorCellFate.Same;
        }

        var before = Words(stored);
        var after = Words(written);

        if (before.Length == 0)
        {
            return AnchorCellFate.Filled;
        }

        if (before.AsSpan().SequenceEqual(after))
        {
            return AnchorCellFate.Respaced;
        }

        return Holds(after, before) ? AnchorCellFate.Kept : AnchorCellFate.Lost;
    }

    /// <summary>
    /// What writing <paramref name="written"/> over <paramref name="stored"/> removes, <c>[-like this-]</c>, and writes,
    /// <c>{+like this+}</c>, word by word, with a few unchanged words either side of each change and a <c>…</c> for the rest.
    /// </summary>
    /// <param name="stored">The cell as it reads.</param>
    /// <param name="written">The text as the cell would store it.</param>
    public static string WordDiff(string stored, string written)
    {
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(written);

        var before = Words(stored);
        var after = Words(written);
        var start = 0;

        while (start < before.Length && start < after.Length && before[start] == after[start])
        {
            start++;
        }

        var (endBefore, endAfter) = (before.Length, after.Length);

        while (endBefore > start && endAfter > start && before[endBefore - 1] == after[endAfter - 1])
        {
            (endBefore, endAfter) = (endBefore - 1, endAfter - 1);
        }

        var steps = new List<(char Kind, string Word)>();
        steps.AddRange(before[..start].Select(word => ('=', word)));
        steps.AddRange(Middle(before[start..endBefore], after[start..endAfter]));
        steps.AddRange(before[endBefore..].Select(word => ('=', word)));

        return Render(steps);
    }

    private static string[] Words(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Whether <paramref name="words"/> holds <paramref name="run"/>, its words together and in order.</summary>
    private static bool Holds(string[] words, string[] run)
    {
        for (var start = 0; start + run.Length <= words.Length; start++)
        {
            if (words.AsSpan(start, run.Length).SequenceEqual(run))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The words of <paramref name="before"/> kept, removed and written to make <paramref name="after"/>: the longest run kept.</summary>
    private static List<(char Kind, string Word)> Middle(string[] before, string[] after)
    {
        var steps = new List<(char Kind, string Word)>();

        if ((long)before.Length * after.Length > MostComparisons)
        {
            steps.AddRange(before.Select(word => ('-', word)));
            steps.AddRange(after.Select(word => ('+', word)));
            return steps;
        }

        // kept[i, j] is how many words the rest of each, from i and from j, have in common, in order.
        var kept = new int[before.Length + 1, after.Length + 1];

        for (var i = before.Length - 1; i >= 0; i--)
        {
            for (var j = after.Length - 1; j >= 0; j--)
            {
                kept[i, j] = before[i] == after[j] ? kept[i + 1, j + 1] + 1 : Math.Max(kept[i + 1, j], kept[i, j + 1]);
            }
        }

        var (x, y) = (0, 0);

        while (x < before.Length && y < after.Length)
        {
            if (before[x] == after[y])
            {
                steps.Add(('=', before[x]));
                (x, y) = (x + 1, y + 1);
            }
            else if (kept[x + 1, y] >= kept[x, y + 1])
            {
                steps.Add(('-', before[x++]));
            }
            else
            {
                steps.Add(('+', after[y++]));
            }
        }

        steps.AddRange(before[x..].Select(word => ('-', word)));
        steps.AddRange(after[y..].Select(word => ('+', word)));
        return steps;
    }

    private static string Render(List<(char Kind, string Word)> steps)
    {
        var pieces = new List<string>();
        var index = 0;

        while (index < steps.Count)
        {
            var kind = steps[index].Kind;
            var run = new List<string>();

            while (index < steps.Count && steps[index].Kind == kind)
            {
                run.Add(steps[index++].Word);
            }

            switch (kind)
            {
                case '-':
                    pieces.Add($"[-{string.Join(' ', run)}-]");
                    break;

                case '+':
                    pieces.Add($"{{+{string.Join(' ', run)}+}}");
                    break;

                default:
                    var first = pieces.Count == 0;
                    var last = index == steps.Count;
                    var head = first ? 0 : ContextWords;
                    var tail = last ? 0 : ContextWords;

                    if (run.Count <= head + tail)
                    {
                        pieces.Add(string.Join(' ', run));
                    }
                    else
                    {
                        pieces.AddRange(
                            new[] { string.Join(' ', run.Take(head)), "…", string.Join(' ', run.Skip(run.Count - tail)) }
                                .Where(piece => piece.Length > 0));
                    }

                    break;
            }
        }

        return string.Join(' ', pieces);
    }
}
