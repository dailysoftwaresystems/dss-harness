using DssHarness.Core.Execution;

namespace DssHarness.Core.Results;

/// <summary>
/// The one owner of digesting a phase that did not pass - a build's, a test's, a run check's: its structured results
/// files first, its log read by the parsers chosen for it wherever the files gave no finding, and every place marked and
/// made the tree's once (<see cref="ResultDigest.WithInTree"/>), so every caller's digest is read and marked by one rule.
/// </summary>
public static class PhaseDigest
{
    /// <summary>
    /// The digest of a phase that did not pass. What its results files gave, where they gave any finding, is the digest -
    /// a file holds its findings typed already, and the console says them again in words; where they gave none - no file
    /// was named, none was written before the failure, none held one - the phase's log is read by
    /// <paramref name="parsers"/>, and what the files could not give is said beside what the log gave. A log that is gone
    /// gives nothing - the verdict already says what the phase came to, and its tail what is left of it - and what the
    /// files said stands alone. Null where nothing was read and nothing said.
    /// </summary>
    /// <param name="output">The phase's own lines, read from its log a line at a time.</param>
    /// <param name="parsers">The parsers chosen for the phase (<see cref="PhaseParsers"/>).</param>
    /// <param name="tree">The tree the phase ran in, and where its tool ran.</param>
    /// <param name="files">What the phase's results files gave (<see cref="Testing.TestResults.Read"/>); null where it names none.</param>
    public static ResultDigest? Of(PhaseOutput output, IReadOnlyList<IResultParser> parsers, DigestTree tree, ResultDigest? files = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(parsers);
        ArgumentNullException.ThrowIfNull(tree);

        if (files is { HoldsAFinding: true })
        {
            return files.WithInTree(tree);
        }

        ResultDigest? log;

        try
        {
            log = PhaseParsers.DigestOf([.. output.Lines()], parsers);
        }
        catch (PhaseOutputUnreadException)
        {
            log = null;
        }

        var digest = log is null ? files : files is null ? log : log.Beside(files);

        return digest?.WithInTree(tree);
    }
}
