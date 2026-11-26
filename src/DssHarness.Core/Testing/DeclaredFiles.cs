using DssHarness.Core.Execution;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Platform;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;

namespace DssHarness.Core.Testing;

/// <summary>
/// The files a test invocation's declared paths name under the directory they are relative to - files, directories and
/// patterns, each expanded as <c>args</c> entries are - found the one way both readers of such a list find them: what
/// <c>keepOnFailure</c> keeps out of the build directory, and what <c>results</c> reads out of the run's results
/// directory. Never through a link, and never refused: the verdict is reached, and what an entry could not give is
/// named.
/// </summary>
/// <param name="Files">Each file the entries name, relative to the directory with forward slashes, each once, in ordinal order.</param>
/// <param name="NamedNothing">Each entry, as declared, that names nothing - said, not refused, for the failure may have been before it was written.</param>
/// <param name="Links">Each file or directory an entry named that is a link, or lies beneath one, named as <see cref="Files"/> names one.</param>
/// <param name="Unreadable">Each entry's path that could not be read, named as <see cref="Files"/> names one.</param>
/// <param name="UnreadableDirectories">Each directory a walk passed by because it could not be read, where an entry could have named what it holds.</param>
internal sealed record DeclaredFiles(
    IReadOnlyList<string> Files,
    IReadOnlyList<string> NamedNothing,
    IReadOnlyList<string> Links,
    IReadOnlyList<string> Unreadable,
    IReadOnlyList<string> UnreadableDirectories)
{
    /// <summary>
    /// Finds what <paramref name="declared"/> names under <paramref name="directory"/>: a plain entry's file, every file
    /// beneath a directory it names, and every file a pattern matches - the directory listed once however many patterns
    /// name it, a directory beneath it that cannot be read passed by and named where an entry could have named what it
    /// holds, and a link on the way from <paramref name="linkRoot"/> named and never followed.
    /// </summary>
    /// <param name="fileSystem">Lists the directory.</param>
    /// <param name="declared">The entries, as the invocation declares them.</param>
    /// <param name="paths">The leg's directories, which fill the names an entry may use.</param>
    /// <param name="directory">The directory every entry is relative to.</param>
    /// <param name="linkRoot">Where the walk for a link on the way to a file starts: the tree the work writes, where the directory is in it.</param>
    /// <param name="setting">What to call the setting where an entry's name is filled in.</param>
    public static DeclaredFiles Find(
        IFileSystem fileSystem,
        IReadOnlyList<string> declared,
        LegPaths paths,
        string directory,
        string linkRoot,
        string setting)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(declared);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(linkRoot);

        var namedNothing = new List<string>();
        var links = new List<string>();
        var unreadable = new List<string>();
        var unreadableDirectories = new List<string>();

        // A directory beneath what a walk lists that cannot be read is passed by, and named once where an entry could have
        // named what it holds: what it holds was asked for and not reached, and the files beside it are reached all the same.
        void PassedBy(string under)
        {
            var relative = PathPatterns.Normalize(Path.GetRelativePath(directory, under));

            if (!unreadableDirectories.Contains(relative, StringComparer.Ordinal))
            {
                unreadableDirectories.Add(relative);
            }
        }

        // The directories the listing of the whole directory passed by, which each pattern is asked about.
        var unlisted = new List<string>();

        // Every file the entries name, each once, in the one order on every machine.
        var matched = new SortedSet<string>(StringComparer.Ordinal);

        // The directory's files, listed once however many patterns name them: a build tree can hold a hundred thousand
        // files, and the walk is the expensive half of matching a pattern against it.
        List<(string Relative, string File)>? listed = null;

        List<(string Relative, string File)> List()
            => listed ??=
            [
                .. fileSystem
                    .EnumerateReachableFiles(directory, (passed, _) => unlisted.Add(passed))
                    .Select(file => (Relative: PathPatterns.Normalize(Path.GetRelativePath(directory, file)), File: file))
                    .Where(entry => GlobPattern.Reads(entry.Relative)),
            ];

        foreach (var entry in declared)
        {
            string expanded;

            try
            {
                expanded = LegPathNames.Expand(entry, paths, setting);
            }
            catch (HarnessException)
            {
                // A name nothing fills in, refused everywhere else, is said here: the verdict is already reached.
                namedNothing.Add(entry);
                continue;
            }

            if (!Within(directory, expanded, out var relative))
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
                        .Where(passed => PathPatterns.Normalize(Path.GetRelativePath(directory, passed)) is var under
                            && GlobPattern.Reads(under) && pattern.Overlaps(GlobPattern.Parse(under + "/**")))
                        .ToList();

                    reachable.ForEach(PassedBy);

                    if (found.Count == 0 && reachable.Count == 0)
                    {
                        namedNothing.Add(entry);
                    }

                    matched.UnionWith(found);
                }
                else
                {
                    var full = Path.Combine(directory, relative);

                    // Never through a link, wherever on the way from the root it stands.
                    if (UnfollowedFiles.LinkOnTheWay(fileSystem, linkRoot, full, itself: true) is not null)
                    {
                        links.Add(relative);
                    }
                    else if (fileSystem.FileExists(full))
                    {
                        matched.Add(relative);
                    }
                    else if (fileSystem.DirectoryExists(full))
                    {
                        // Walked as the patterns' listing is: one subdirectory that cannot be read is passed by and
                        // named, never the whole entry lost with it.
                        matched.UnionWith(fileSystem
                            .EnumerateReachableFiles(full, (passed, _) => PassedBy(passed))
                            .Select(file => PathPatterns.Normalize(Path.GetRelativePath(directory, file))));
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

        return new DeclaredFiles([.. matched], namedNothing, links, unreadable, unreadableDirectories);
    }

    /// <summary>
    /// Whether <paramref name="expanded"/> names something under <paramref name="directory"/>, and what it names there, in
    /// the one spelling both sides agree on; false for the directory itself, which no entry names, and for an absolute path
    /// on another root, which names nothing under this one however it is spelled.
    /// </summary>
    private static bool Within(string directory, string expanded, out string relative)
    {
        if (!PlatformPaths.IsRootedOnAnyPlatform(expanded))
        {
            relative = PathPatterns.Normalize(expanded);
        }
        else
        {
            var beneath = Path.GetRelativePath(directory, expanded);

            // Another drive, or a share: no path of .. segments leads there, and the whole path back - still rooted -
            // would be taken for relative and read as if it were under this one.
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
