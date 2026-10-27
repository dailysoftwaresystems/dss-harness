using DssHarness.Core.Execution;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Platform;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;

namespace DssHarness.Core.Testing;

/// <summary>
/// What a test invocation that did not pass kept of the files it named, as failure evidence beside its verdict.
/// </summary>
/// <remarks>
/// Evidence, never an output: a later step reads nothing from it, and the runners' rule that a failed step keeps
/// nothing stands. A file the next run overwrites in the build directory is gone for good, and the one failure that
/// could not be reproduced is exactly the one whose evidence nobody can ask for again - so the copy is made beside the
/// leg's log, in the run's own directory, where nothing overwrites it.
/// </remarks>
/// <param name="Directory">
/// Where the evidence is kept: <c>&lt;run&gt;/&lt;leg&gt;/kept-on-failure</c>, as the machine that ran the invocation
/// spells it - a host's with its home as <c>~</c> - wherever the leg ran.
/// </param>
/// <param name="Bytes">How much the kept files hold.</param>
/// <param name="Kept">Each file kept, relative to the build directory with forward slashes, in the order kept.</param>
/// <param name="RefusedOverCap">Each file left out because the cap was spent, named as <see cref="Kept"/> names one.</param>
/// <param name="Unreadable">Each file that could not be read, and each entry naming a directory that could not be.</param>
/// <param name="NamedNothing">
/// Each declared entry that names nothing: said, not refused, because the failure may have been before it was written.
/// </param>
public sealed record KeptOnFailure(
    string Directory,
    long Bytes,
    IReadOnlyList<string> Kept,
    IReadOnlyList<string> RefusedOverCap,
    IReadOnlyList<string> Unreadable,
    IReadOnlyList<string> NamedNothing)
{
    /// <summary>What the leg's run directory calls the evidence: <c>kept-on-failure</c>, below the leg's own directory.</summary>
    public const string DirectoryName = "kept-on-failure";

    /// <summary>How much is kept in all where <c>keepOnFailureCapBytes</c> says nothing: 64 MiB.</summary>
    public const long DefaultCapBytes = 64L * 1024 * 1024;

    /// <summary>
    /// Keeps what <paramref name="declared"/> names under <paramref name="buildDirectory"/>, in
    /// <paramref name="destination"/>, within <paramref name="capBytes"/> in all.
    /// </summary>
    /// <param name="fileSystem">Reads and writes the files.</param>
    /// <param name="declared">
    /// What <c>keepOnFailure</c> names - files, directories, patterns - relative to the build directory, each expanded
    /// as <c>args</c> entries are, or <see langword="null"/> where the invocation names none.
    /// </param>
    /// <param name="capBytes">The most to keep in all; at least one.</param>
    /// <param name="paths">The leg's directories, which fill the names an entry may use.</param>
    /// <param name="buildDirectory">The leg's build directory, the root every entry is relative to.</param>
    /// <param name="destination">Where the evidence is kept; the leg's own <see cref="DirectoryName"/> directory below it.</param>
    /// <param name="cancellationToken">Stops between files, keeping what is already kept.</param>
    /// <returns>What was kept and what was not, or <see langword="null"/> where nothing was asked for.</returns>
    /// <remarks>
    /// Never throws, and never changes a verdict: the evidence is beside it, and a keep that failed would trade a leg
    /// that had reached its verdict for one that reached none. What cannot be read, and what names nothing, is named in
    /// the answer instead.
    /// </remarks>
    public static async Task<KeptOnFailure?> KeepAsync(
        IFileSystem fileSystem,
        IReadOnlyList<string>? declared,
        long capBytes,
        LegPaths paths,
        string buildDirectory,
        string destination,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentException.ThrowIfNullOrWhiteSpace(buildDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);

        if (declared is not { Count: > 0 })
        {
            return null;
        }

        var kept = new List<string>();
        var overCap = new List<string>();
        var unreadable = new List<string>();
        var namedNothing = new List<string>();
        long bytes = 0;

        // Every file the entries name, relative to the build directory with forward slashes, each once: the order the
        // cap spends itself in, so the same configuration keeps the same files on every machine.
        var matched = new SortedList<string, string>(StringComparer.Ordinal);

        // The build directory's files, listed once however many patterns name them: a build tree can hold a hundred
        // thousand files, and the walk is the expensive half of matching a pattern against it.
        List<(string Relative, string File)>? listed = null;

        List<(string Relative, string File)> List()
        {
            if (listed is not null)
            {
                return listed;
            }

            // A directory beneath the build directory that cannot be read is passed by, as a walk of a tree's own
            // files passes one by: a pattern that names only what it holds names nothing, which is said.
            listed =
            [
                .. fileSystem
                    .EnumerateReachableFiles(buildDirectory, (_, _) => { })
                    .Select(file => (Relative: PathPatterns.Normalize(Path.GetRelativePath(buildDirectory, file)), File: file))
                    .Where(entry => GlobPattern.Reads(entry.Relative)),
            ];

            return listed;
        }

        foreach (var entry in declared)
        {
            string expanded;

            try
            {
                expanded = LegPathNames.Expand(entry, paths, "test.keepOnFailure");
            }
            catch (HarnessException)
            {
                // A name nothing fills in, refused everywhere else, is said here: the verdict is already reached, and
                // the entry is evidence that was asked for and not kept.
                namedNothing.Add(entry);
                continue;
            }

            if (!Within(buildDirectory, expanded, out var relative))
            {
                namedNothing.Add(entry);
                continue;
            }

            try
            {
                if (GlobPattern.IsPattern(relative))
                {
                    var pattern = GlobPattern.Parse(relative);
                    var found = List().Where(file => pattern.Matches(file.Relative)).Select(file => file.Relative).ToList();

                    if (found.Count == 0)
                    {
                        namedNothing.Add(entry);
                        continue;
                    }

                    foreach (var file in found)
                    {
                        matched[file] = file;
                    }
                }
                else
                {
                    var full = Path.Combine(buildDirectory, relative);

                    if (fileSystem.FileExists(full))
                    {
                        matched[relative] = relative;
                    }
                    else if (fileSystem.DirectoryExists(full))
                    {
                        var beneath = fileSystem
                            .EnumerateFiles(full, recursive: true)
                            .Select(file => PathPatterns.Normalize(Path.GetRelativePath(buildDirectory, file)))
                            .ToList();

                        foreach (var file in beneath)
                        {
                            matched[file] = file;
                        }
                    }
                    else
                    {
                        namedNothing.Add(entry);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                unreadable.Add(relative);
            }
        }

        foreach (var relative in matched.Values)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                var full = Path.Combine(buildDirectory, relative);

                if (fileSystem.StandingOf(full)?.Length is not { } size)
                {
                    unreadable.Add(relative);
                    continue;
                }

                if (bytes + size > capBytes)
                {
                    overCap.Add(relative);
                    continue;
                }

                // Streamed, never held whole: a core dump is evidence this exists to keep, and a cap raised to hold
                // one is not a promise that it fits in memory.
                await using (var read = fileSystem.OpenRead(full))
                {
                    fileSystem.WriteAtomic(
                        Path.Combine(destination, relative),
                        write => read.CopyTo(write));
                }

                kept.Add(relative);
                bytes += size;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // A stop asked for while the evidence crossed keeps what already did: the verdict is decided, and
                // throwing here would trade it for a leg that reached none.
                break;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                unreadable.Add(relative);
            }
        }

        return new KeptOnFailure(destination, bytes, kept, overCap, unreadable, namedNothing);
    }

    /// <summary>
    /// Whether <paramref name="expanded"/> names something under <paramref name="buildDirectory"/>, and what it names
    /// there, in the one spelling both sides agree on; false for the build directory itself, which no entry names, and
    /// for an absolute path on another root, which names nothing under this one however it is spelled.
    /// </summary>
    private static bool Within(string buildDirectory, string expanded, out string relative)
    {
        if (!PlatformPaths.IsRootedOnAnyPlatform(expanded))
        {
            relative = PathPatterns.Normalize(expanded);
        }
        else
        {
            var beneath = Path.GetRelativePath(buildDirectory, expanded);

            // Another drive, or a share: no path of .. segments leads there, and the whole path back - still rooted -
            // would be taken for relative and copied over itself.
            if (PlatformPaths.IsRootedOnAnyPlatform(beneath)
                || beneath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(segment => segment == ".."))
            {
                relative = PathPatterns.Normalize(expanded);
                return false;
            }

            relative = PathPatterns.Normalize(beneath);
        }

        return relative.Length > 0 && relative != ".";
    }
}
