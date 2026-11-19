using DssHarness.Core.Text;

namespace DssHarness.Core.Codebase;

/// <summary>
/// Which lines of a newer text are not an older one's, told line by line: each run of lines the one diff of two sequences
/// (<see cref="SequenceDiff"/>) finds changed, with the older lines it stands for. No bound is set on the edits and no
/// part of either text is taken as changed whole for what finding it would cost, so a regeneration that keeps most of a
/// long file's lines between the ones it changed still tells each line it kept; nothing changed is ever said unchanged.
/// </summary>
public static class CodeLineDiff
{
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

        // A changed run stands before the item it puts its lines in front of, or takes them from in front of: from 1, that
        // item's line is the line after the place, as a hunk names it.
        return [.. SequenceDiff.Of(before, after, StringComparer.Ordinal)
            .Where(run => !run.Equal)
            .Select(run => new CodeLineHunk(run.After + 1, run.AfterCount, run.BeforeCount, run.Before + 1))];
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
