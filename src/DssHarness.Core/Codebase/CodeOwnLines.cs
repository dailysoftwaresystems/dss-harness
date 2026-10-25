namespace DssHarness.Core.Codebase;

/// <summary>
/// A declaration's own lines: its span less the spans of what is declared inside it - what a change of it and a step's
/// touch of it are told by, so a line of a member is the member's and never also its type's.
/// </summary>
public static class CodeOwnLines
{
    /// <summary>The runs of lines from <paramref name="start"/> to <paramref name="end"/> that none of <paramref name="spans"/> holds, in order.</summary>
    /// <param name="start">The first line, from 1.</param>
    /// <param name="end">The last line, inclusive; none where it is before <paramref name="start"/>.</param>
    /// <param name="spans">The spans of what is declared inside, each inclusive.</param>
    public static IReadOnlyList<(int Start, int End)> Runs(int start, int end, IReadOnlyList<(int Start, int End)> spans)
    {
        ArgumentNullException.ThrowIfNull(spans);

        var runs = new List<(int Start, int End)>();
        int? open = null;

        for (var line = start; line <= end; line++)
        {
            var held = spans.Any(span => span.Start <= line && line <= span.End);

            if (!held && open is null)
            {
                open = line;
            }
            else if (held && open is { } begun)
            {
                runs.Add((begun, line - 1));
                open = null;
            }
        }

        if (open is { } last)
        {
            runs.Add((last, end));
        }

        return runs;
    }

    /// <summary>Whether the lines <paramref name="first"/> to <paramref name="last"/> meet the own lines of a declaration spanning <paramref name="start"/> to <paramref name="end"/>.</summary>
    /// <param name="first">The first line touched, from 1.</param>
    /// <param name="last">The last line touched, inclusive.</param>
    /// <param name="start">The declaration's first line.</param>
    /// <param name="end">Its last line, inclusive.</param>
    /// <param name="inner">The spans of what is declared inside it.</param>
    public static bool Meet(int first, int last, int start, int end, IReadOnlyList<(int Start, int End)> inner)
        => Runs(Math.Max(first, start), Math.Min(last, end), inner).Count > 0;
}
