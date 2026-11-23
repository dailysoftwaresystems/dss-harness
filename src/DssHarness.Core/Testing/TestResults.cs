using DssHarness.Core.Execution;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Platform;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;

namespace DssHarness.Core.Testing;

/// <summary>
/// The structured results a test invocation declared, read where they are: one owner of what <c>test.results</c> names.
/// Where the invocation does not pass, each named file - a TRX, a JUnit XML, Dart's JSON stream, a SARIF log, by its
/// own shape and never by its name - is read, masked as the log is, and digested with every other into the typed
/// diagnostics and failures the leg's entry carries. Nothing is copied and nothing is refused: the verdict is already
/// reached, and what a path could not give is said in the digest's unparsed lines - a file naming nothing may mean the
/// failure came before it was written, and a file no form owns is a line to fix.
/// </summary>
public static class TestResults
{
    /// <summary>
    /// Reads what <paramref name="declared"/> names under <paramref name="buildDirectory"/>: one digest of every file
    /// that read, the problems of every one that did not; null where nothing was declared.
    /// </summary>
    /// <param name="fileSystem">The file system the files are read on.</param>
    /// <param name="declared">The declared paths, as the invocation names them - <c>{buildDir}</c> among the placeholders.</param>
    /// <param name="paths">The leg's paths, the placeholders' own.</param>
    /// <param name="buildDirectory">The leg's build directory, the root the paths are read under.</param>
    /// <param name="mask">The mask the files' messages are masked through; null to read them as the log is.</param>
    /// <param name="cancellationToken">Ends the reading.</param>
    public static ResultDigest? Read(
        IFileSystem fileSystem,
        IReadOnlyList<string>? declared,
        LegPaths paths,
        string buildDirectory,
        ILineMask? mask,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentException.ThrowIfNullOrWhiteSpace(buildDirectory);

        if (declared is not { Count: > 0 })
        {
            return null;
        }

        var diagnostics = new List<Diagnostic>();
        var failures = new List<TestFailure>();
        var said = new List<string>();

        // The build directory's files, listed once however many patterns name them.
        List<(string Relative, string File)>? listed = null;

        List<(string Relative, string File)> List()
            => listed ??=
            [
                .. fileSystem
                    .EnumerateReachableFiles(buildDirectory, (_, _) => { })
                    .Select(file => (Relative: PathPatterns.Normalize(Path.GetRelativePath(buildDirectory, file)), File: file))
                    .Where(entry => GlobPattern.Reads(entry.Relative)),
            ];

        foreach (var entry in declared)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string expanded;

            try
            {
                expanded = LegPathNames.Expand(entry, paths, "test.results");
            }
            catch (HarnessException)
            {
                // A name nothing fills in, refused everywhere else, is said here: the verdict is already reached.
                said.Add($"results: '{entry}' names nothing");
                continue;
            }

            if (!Within(buildDirectory, expanded, out var relative))
            {
                said.Add($"results: '{entry}' is not under the build directory");
                continue;
            }

            if (GlobPattern.IsPattern(relative))
            {
                var pattern = GlobPattern.Parse(relative);
                var found = List().Where(file => pattern.Matches(file.Relative)).ToList();

                if (found.Count == 0)
                {
                    said.Add($"results: '{entry}' named no file");
                    continue;
                }

                foreach (var file in found)
                {
                    Read(file.File, file.Relative);
                }
            }
            else
            {
                if (!fileSystem.FileExists(expanded))
                {
                    said.Add($"results: '{entry}' named no file");
                    continue;
                }

                Read(expanded, relative);
            }
        }

        return diagnostics.Count == 0 && failures.Count == 0 && said.Count == 0 ? null : ResultDigest.Of(diagnostics, failures, said);

        void Read(string file, string relative)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string text;

            try
            {
                text = fileSystem.ReadAllText(file);
            }
            catch (Exception unreadable) when (unreadable is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                said.Add($"results: '{relative}' could not be read");
                return;
            }

            if (ResultFiles.Digest(text, mask, out var problem) is not { } digest)
            {
                said.Add($"results: '{relative}' {problem}");
                return;
            }

            diagnostics.AddRange(digest.Diagnostics);
            failures.AddRange(digest.Failures);
        }
    }

    /// <summary>Whether <paramref name="expanded"/> is a path under <paramref name="buildDirectory"/>, and what it is relative to it.</summary>
    private static bool Within(string buildDirectory, string expanded, out string relative)
    {
        if (!PlatformPaths.IsRootedOnAnyPlatform(expanded))
        {
            relative = PathPatterns.Normalize(expanded);
        }
        else
        {
            var beneath = Path.GetRelativePath(buildDirectory, expanded);

            // Another drive, or a share: no path of .. segments leads there.
            if (PlatformPaths.IsRootedOnAnyPlatform(beneath)
                || beneath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(segment => segment == ".."))
            {
                relative = PathPatterns.Normalize(expanded);
                return false;
            }

            relative = PathPatterns.Normalize(beneath);
        }

        return true;
    }
}
