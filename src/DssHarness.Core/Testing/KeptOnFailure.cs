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
/// <param name="RefusedOverCap">
/// Each file left out because what it holds - as it is read, never as its entry in the directory says - would pass the
/// cap, named as <see cref="Kept"/> names one.
/// </param>
/// <param name="Unreadable">
/// Each file that could not be read - a pipe, a socket or a device among them, which is no file to read - and each entry
/// naming a directory that could not be, named as <see cref="Kept"/> names one.
/// </param>
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
    /// <summary>
    /// Each file or directory an entry named that is a link, or lies beneath one, from the build directory down, left as it
    /// is: nothing is kept through a link - what it leads to may be outside the build directory, or of any size - and what
    /// it leads to is kept where an entry names that.
    /// </summary>
    public IReadOnlyList<string> Links { get; init; } = [];

    /// <summary>
    /// Each directory a walk passed by because it could not be read, where an entry could have named what it holds - one
    /// beneath a directory an entry names, or one a pattern could reach - named as <see cref="Kept"/> names one: what it
    /// holds is evidence asked for and not kept, and the files beside it are kept all the same.
    /// </summary>
    public IReadOnlyList<string> UnreadableDirectories { get; init; } = [];

    /// <summary>
    /// Each file a stop left uncopied: the keep ends between one file and the next, and mid-file, when its run is stopped,
    /// keeping what it already kept, and what it had still to copy is named here rather than nowhere.
    /// </summary>
    public IReadOnlyList<string> LeftAtStop { get; init; } = [];

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
    /// <param name="cancellationToken">Stops between files and within one, keeping what is already kept whole and naming what was left.</param>
    /// <returns>What was kept and what was not, or <see langword="null"/> where nothing was asked for.</returns>
    /// <remarks>
    /// Never throws, and never changes a verdict: the evidence is beside it, and a keep that failed would trade a leg
    /// that had reached its verdict for one that reached none. What cannot be read, what is a link, what names nothing
    /// and what a stop left are named in the answer instead. Only a file of its own is copied, and no more of it than the
    /// cap has left: its size is what it holds as it is read, for a directory entry's says nothing true of a link and
    /// nothing at all of a file still growing.
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

        // What the test's work writes is the leg's tree, the build directory inside it among it, so every file is read from
        // the tree's root down, never through a link the work put on the way (UnfollowedFiles); a build directory outside the
        // tree, which the work writes nothing of, is the root of its own files.
        var root = PathContainment.IsStrictlyInside(paths.TreeRoot, buildDirectory, StringComparison.OrdinalIgnoreCase) ? paths.TreeRoot : buildDirectory;
        var kept = new List<string>();
        var overCap = new List<string>();
        var unreadable = new List<string>();
        var links = new List<string>();
        var unreadableDirectories = new List<string>();
        var leftAtStop = new List<string>();
        var namedNothing = new List<string>();
        long bytes = 0;

        // A directory beneath what a walk lists that cannot be read is passed by, and named once where an entry could have
        // named what it holds: what it holds is evidence asked for and not kept, and the files beside it are kept all the same.
        void PassedBy(string directory)
        {
            var relative = PathPatterns.Normalize(Path.GetRelativePath(buildDirectory, directory));

            if (!unreadableDirectories.Contains(relative, StringComparer.Ordinal))
            {
                unreadableDirectories.Add(relative);
            }
        }

        // The directories the listing of the whole build directory passed by, which each pattern is asked about.
        var unlisted = new List<string>();

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

            listed =
            [
                .. fileSystem
                    .EnumerateReachableFiles(buildDirectory, (directory, _) => unlisted.Add(directory))
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

                    // A directory the listing passed by is named where the pattern could have named what it holds, and
                    // no other.
                    var reachable = unlisted
                        .Where(directory => PathPatterns.Normalize(Path.GetRelativePath(buildDirectory, directory)) is var under
                            && GlobPattern.Reads(under) && pattern.Overlaps(GlobPattern.Parse(under + "/**")))
                        .ToList();

                    reachable.ForEach(PassedBy);

                    if (found.Count == 0)
                    {
                        if (reachable.Count == 0)
                        {
                            namedNothing.Add(entry);
                        }

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

                    // Never through a link, wherever on the way from the tree's root it stands.
                    if (UnfollowedFiles.LinkOnTheWay(fileSystem, root, full, itself: true) is not null)
                    {
                        links.Add(relative);
                    }
                    else if (fileSystem.FileExists(full))
                    {
                        matched[relative] = relative;
                    }
                    else if (fileSystem.DirectoryExists(full))
                    {
                        // Walked as the patterns' listing is: one subdirectory that cannot be read is passed by and
                        // named, never the whole entry lost with it.
                        foreach (var file in fileSystem.EnumerateReachableFiles(full, (directory, _) => PassedBy(directory)).Select(file => PathPatterns.Normalize(Path.GetRelativePath(buildDirectory, file))))
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

        var waiting = matched.Values.ToList();

        for (var next = 0; next < waiting.Count; next++)
        {
            var relative = waiting[next];

            // A stop asked for while the evidence crossed keeps what already did and names what did not: the verdict is
            // decided, and throwing here would trade it for a leg that reached none.
            if (cancellationToken.IsCancellationRequested)
            {
                leftAtStop.AddRange(waiting.Skip(next));
                break;
            }

            var full = Path.Combine(buildDirectory, relative);

            try
            {
                // Never through a link, the file's own or a directory's on the way to it: what it leads to may lie outside the
                // build directory - a place the test's work may not read - and is of any size.
                if (UnfollowedFiles.LinkOnTheWay(fileSystem, root, full, itself: true) is not null)
                {
                    links.Add(relative);
                    continue;
                }

                // Streamed, never held whole: a core dump is evidence this exists to keep, and a cap raised to hold one
                // is not a promise that it fits in memory. Its size is what the opened file holds, and no more is copied
                // than the cap has left, so a file that grows as it is read is refused, never kept past the cap.
                var room = capBytes - bytes;
                long copied = 0;

                await using (var read = UnfollowedFiles.OpenRead(fileSystem, root, full))
                {
                    if (read.CanSeek && read.Length > room)
                    {
                        overCap.Add(relative);
                        continue;
                    }

                    fileSystem.WriteAtomic(
                        Path.Combine(destination, relative),
                        write => copied = CopyAtMost(read, write, room, cancellationToken));
                }

                kept.Add(relative);
                bytes += copied;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                leftAtStop.AddRange(waiting.Skip(next));
                break;
            }
            catch (PastTheCapException)
            {
                overCap.Add(relative);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                unreadable.Add(relative);
            }
        }

        return new KeptOnFailure(destination, bytes, kept, overCap, unreadable, namedNothing)
        {
            Links = links,
            UnreadableDirectories = unreadableDirectories,
            LeftAtStop = leftAtStop,
        };
    }

    /// <summary>
    /// Copies <paramref name="read"/> into <paramref name="write"/> a block at a time, asking <paramref name="cancellationToken"/>
    /// between blocks, and refuses past <paramref name="room"/> bytes: a file that grows as it is read is not kept past the
    /// cap, however its directory entry measured it.
    /// </summary>
    /// <returns>How many bytes were copied.</returns>
    /// <exception cref="PastTheCapException">The file held more than <paramref name="room"/> bytes.</exception>
    /// <exception cref="OperationCanceledException">The run was stopped mid-file.</exception>
    private static long CopyAtMost(Stream read, Stream write, long room, CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        long copied = 0;
        int got;

        while ((got = read.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            copied += got;

            if (copied > room)
            {
                throw new PastTheCapException();
            }

            write.Write(buffer, 0, got);
        }

        return copied;
    }

    /// <summary>A file that held more, as it was read, than the cap had left: refused over the cap, its part-copy gone.</summary>
    private sealed class PastTheCapException : Exception
    {
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
