using DssHarness.Core.Text;

namespace DssHarness.Core.Anchors;

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

        var before = AnchorCells.Words(stored);
        var after = AnchorCells.Words(written);

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
    /// <c>{+like this+}</c>, word by word, every unchanged word quoted as it stands: the whole of both, however long, so
    /// what a reader is asked to accept losing is never shown in part.
    /// </summary>
    /// <remarks>
    /// Compared by the one diff of two sequences (<see cref="SequenceDiff"/>), over words: linear in memory, with no
    /// bound on the words compared, so a cell of two hundred thousand words is compared exactly rather than shown as the
    /// one removed and the other written.
    /// </remarks>
    /// <param name="stored">The cell as it reads.</param>
    /// <param name="written">The text as the cell would store it.</param>
    public static string WordDiff(string stored, string written)
    {
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(written);

        var before = AnchorCells.Words(stored);
        var after = AnchorCells.Words(written);
        var pieces = new List<string>();

        foreach (var run in SequenceDiff.Of(before, after, StringComparer.Ordinal))
        {
            if (run.Equal)
            {
                pieces.Add(string.Join(' ', before, run.Before, run.BeforeCount));
                continue;
            }

            if (run.BeforeCount > 0)
            {
                pieces.Add($"[-{string.Join(' ', before, run.Before, run.BeforeCount)}-]");
            }

            if (run.AfterCount > 0)
            {
                pieces.Add($"{{+{string.Join(' ', after, run.After, run.AfterCount)}+}}");
            }
        }

        return string.Join(' ', pieces);
    }

    /// <summary>
    /// Whether <paramref name="words"/> holds <paramref name="run"/>, its words together and in order: found in one pass
    /// over each (Knuth, Morris and Pratt), so a long cell held inside a longer one is found in time linear in the two.
    /// </summary>
    private static bool Holds(string[] words, string[] run)
    {
        if (run.Length == 0)
        {
            return true;
        }

        // fallback[i]: how long the longest proper prefix of run[..(i + 1)] is that is also its suffix.
        var fallback = new int[run.Length];

        for (int index = 1, matched = 0; index < run.Length; index++)
        {
            while (matched > 0 && !string.Equals(run[index], run[matched], StringComparison.Ordinal))
            {
                matched = fallback[matched - 1];
            }

            if (string.Equals(run[index], run[matched], StringComparison.Ordinal))
            {
                matched++;
            }

            fallback[index] = matched;
        }

        for (int index = 0, matched = 0; index < words.Length; index++)
        {
            while (matched > 0 && !string.Equals(words[index], run[matched], StringComparison.Ordinal))
            {
                matched = fallback[matched - 1];
            }

            if (string.Equals(words[index], run[matched], StringComparison.Ordinal) && ++matched == run.Length)
            {
                return true;
            }
        }

        return false;
    }
}
