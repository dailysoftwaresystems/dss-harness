using DssHarness.Core.Execution;
using DssHarness.Core.Platform;

namespace DssHarness.Core.Results;

/// <summary>
/// The one owner of digesting a phase that did not pass - a build's, a test's, a run check's: its structured results
/// files first, its log read by the parsers chosen for it in the same digest, what the log said that the files lack
/// gathered beside them (<see cref="ResultDigest.Gathering.Beside"/>), and every place marked and made the tree's once
/// (<see cref="ResultDigest.WithInTree(Func{string, string?}, Func{string, string, string?})"/>), so every caller's digest
/// is read and marked by one rule.
/// </summary>
public static class PhaseDigest
{
    /// <summary>
    /// The digest of a phase that did not pass. What its results files gave is gathered first - a file holds its findings
    /// typed already, the runner's own record of its run - and its log is read by <paramref name="parsers"/> in one
    /// streaming pass all the same, for the console says what no file holds: a diagnostic the build or the runner printed,
    /// a crash, a host that died, a test whose file was never written; what it said that the files lack is gathered beside
    /// them, each finding said once, and a failure of a test a file names gives the file's what the file's lacks
    /// (<see cref="ResultDigest.Gathering.Beside"/>). A log that is gone gives nothing - the verdict already says what the
    /// phase came to, and its tail what is left of it - and what the files said stands alone. Null where nothing was read
    /// and nothing said.
    /// </summary>
    /// <param name="output">The phase's own lines, read from its log a line at a time.</param>
    /// <param name="exit">
    /// The code the phase's program ended with (<see cref="PhaseResult.ProgramExit"/>), which its log's parsers are told as
    /// they end; null where that is not known.
    /// </param>
    /// <param name="parsers">The parsers chosen for the phase (<see cref="PhaseParsers"/>).</param>
    /// <param name="tree">The tree the phase ran in, and where its tool ran.</param>
    /// <param name="files">
    /// Reads the phase's results files into the gathering it is given, the files' text masked as the phase's log is, and
    /// answers whether it read or said anything (<see cref="Testing.TestResults.Read"/>); null where the phase names none.
    /// </param>
    public static ResultDigest? Of(PhaseOutput output, int? exit, IReadOnlyList<IResultParser> parsers, DigestTree tree, Func<ResultDigest.Gathering, bool>? files = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(parsers);
        ArgumentNullException.ThrowIfNull(tree);

        var gathering = new ResultDigest.Gathering();
        var filed = files?.Invoke(gathering) ?? false;
        PhaseFindings? log;

        try
        {
            log = PhaseParsers.FindingsOf(output.NumberedLines(), parsers, line => gathering.Unparsed(line.Text), () => exit);
        }
        catch (PhaseOutputUnreadException)
        {
            log = null;
        }

        if (log is not null)
        {
            // A path read as the tree reads it, so one file is one place whichever form named it; one the tree cannot
            // place, as written.
            gathering.Beside(log, file => PlatformPaths.RelativeAsWritten(tree.Root, file, tree.WorkingDirectory) ?? file);
        }

        var digest = gathering.Digest();

        return filed || digest.HoldsAFinding || digest.UnparsedCount > 0 ? digest.WithInTree(tree) : null;
    }
}
