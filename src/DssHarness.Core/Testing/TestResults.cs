using DssHarness.Core.Execution;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Results;

namespace DssHarness.Core.Testing;

/// <summary>
/// The structured results a test invocation declared, read where they are: one owner of what <c>test.results</c> names
/// and of the directory it names it in. Each run of an invocation has a results directory of its own,
/// <c>{resultsDir}</c> - inside the leg's records of the run, made empty before the runner starts - which its args point
/// its runner at and its <c>results</c> name paths under, so a file read there is that run's and never an earlier one's.
/// Where the invocation does not pass, each named file - a TRX, a JUnit XML, Dart's JSON stream, a SARIF log, by its own
/// shape and never by its name - is read as a stream, never through a link, masked as the phase's log is, and digested
/// with every other into the typed diagnostics and failures the leg's entry carries, the phase's log read beside them
/// for what they lack (<see cref="PhaseDigest"/>). Nothing is copied and nothing is
/// refused: the verdict is already reached, and what a path could not give is said in the digest's unparsed lines - a
/// path naming nothing may mean the failure came before it was written, and a file no form owns is a line to fix.
/// </summary>
public static class TestResults
{
    /// <summary>What the leg's records of a run call the directories its test runs write their results into: <c>results</c>, one per phase below it.</summary>
    public const string DirectoryName = "results";

    /// <summary>
    /// The results directory of the test phase <paramref name="phase"/> of leg <paramref name="leg"/> in the run whose
    /// directory is <paramref name="runDirectory"/>: <c>&lt;run&gt;/&lt;leg&gt;/results/&lt;phase&gt;</c>, as that run's
    /// machine spells it.
    /// </summary>
    /// <param name="runDirectory">The run's own directory.</param>
    /// <param name="leg">The leg, as the configuration names it.</param>
    /// <param name="phase">The test phase, as its log is named for it.</param>
    public static string DirectoryFor(string runDirectory, string leg, string phase)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(leg);
        ArgumentException.ThrowIfNullOrWhiteSpace(phase);

        return new LegRecords(runDirectory, leg).Under(DirectoryName, PhaseLogs.NameFor(phase));
    }

    /// <summary>
    /// Makes <paramref name="directory"/> the empty results directory a run of a test invocation starts with: whatever an
    /// earlier run of the same phase left there removed, and the directory made - before the runner starts, so nothing it
    /// does not write this run is ever read as its.
    /// </summary>
    /// <param name="fileSystem">The file system the run's records are on.</param>
    /// <param name="directory">The run's results directory.</param>
    public static void MakeEmpty(IFileSystem fileSystem, string directory)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        fileSystem.DeleteDirectory(directory);
        fileSystem.CreateDirectory(directory);
    }

    /// <summary>
    /// Reads what <paramref name="declared"/> names under <paramref name="resultsDirectory"/> into
    /// <paramref name="into"/>: every file that reads - each file's findings gathered with every other's before the
    /// digest's bounds apply, so its counts are over all of them, a case's own output read by <paramref name="parsers"/>
    /// (<see cref="ResultFiles.Read"/>) - and what every path could not give said beside them, all of it masked through
    /// <paramref name="mask"/> as it is gathered. Answers whether anything was read or said; false where nothing was
    /// declared.
    /// </summary>
    /// <param name="fileSystem">The file system the files are read on.</param>
    /// <param name="declared">The declared paths, as the invocation names them, relative to the results directory - <c>{resultsDir}</c> among the names they use.</param>
    /// <param name="paths">The leg's paths, the placeholders' own.</param>
    /// <param name="resultsDirectory">The run's results directory, the root the paths are read under and no link is followed beneath.</param>
    /// <param name="parsers">The parsers chosen for the phase, which read what a case's own test printed.</param>
    /// <param name="into">Gathers what the files hold.</param>
    /// <param name="mask">The mask the phase's log is written through; null where it is written through none.</param>
    /// <param name="cancellationToken">Ends the reading.</param>
    public static bool Read(
        IFileSystem fileSystem,
        IReadOnlyList<string>? declared,
        LegPaths paths,
        string resultsDirectory,
        IReadOnlyList<IResultParser> parsers,
        ResultDigest.Gathering into,
        ILineMask? mask,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentException.ThrowIfNullOrWhiteSpace(resultsDirectory);
        ArgumentNullException.ThrowIfNull(parsers);
        ArgumentNullException.ThrowIfNull(into);

        if (declared is not { Count: > 0 })
        {
            return false;
        }

        var found = DeclaredFiles.Find(fileSystem, declared, paths, resultsDirectory, resultsDirectory, "test.results");
        var gathering = into.Through(mask);
        var said = 0;

        void Say(string line)
        {
            gathering.Unparsed(line);
            said++;
        }

        foreach (var entry in found.NamedNothing)
        {
            Say($"results: '{entry}' named no file");
        }

        foreach (var link in found.Links)
        {
            Say($"results: '{link}' is a link, never read through");
        }

        foreach (var unreadable in found.Unreadable)
        {
            Say($"results: '{unreadable}' could not be read");
        }

        foreach (var directory in found.UnreadableDirectories)
        {
            Say($"results: the directory '{directory}' could not be read");
        }

        var read = 0;

        foreach (var relative in found.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var file = Path.Combine(resultsDirectory, relative);

            try
            {
                if (ResultFiles.Read(() => UnfollowedFiles.OpenRead(fileSystem, resultsDirectory, file), gathering, parsers) is { } problem)
                {
                    Say($"results: '{relative}': {problem}");
                }

                read++;
            }
            catch (LinkOnTheWayException)
            {
                Say($"results: '{relative}' is a link, never read through");
            }
            catch (Exception unreadable) when (unreadable is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                Say($"results: '{relative}' could not be read");
            }
        }

        return read > 0 || said > 0;
    }
}
