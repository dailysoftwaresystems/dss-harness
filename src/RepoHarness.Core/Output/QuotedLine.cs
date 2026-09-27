namespace RepoHarness.Core.Output;

/// <summary>
/// A row that quotes one line a program printed, as it printed it: beneath a ledger, the last lines a phase
/// that did not pass printed.
/// </summary>
/// <remarks>
/// Spelt once, for the ledger that writes such a row and for the output that has to leave it as it is: a host
/// answering another machine writes its own lines with its home as <c>~</c> (see
/// <see cref="Platform.HomeShorthand"/>), and a program's own words are not the harness's to rewrite.
/// </remarks>
public static class QuotedLine
{
    /// <summary>What a row quoting a program's line starts with.</summary>
    private const string Mark = "  | ";

    /// <summary>The row quoting <paramref name="printed"/>.</summary>
    /// <param name="printed">The line, as the program printed it.</param>
    public static string Of(string printed) => Mark + printed;

    /// <summary>Whether <paramref name="line"/> quotes a line a program printed.</summary>
    /// <param name="line">A line the harness is about to write.</param>
    public static bool Is(string line)
    {
        ArgumentNullException.ThrowIfNull(line);

        return line.StartsWith(Mark, StringComparison.Ordinal);
    }
}
