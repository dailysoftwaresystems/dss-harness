namespace RepoHarness.Core.FileSystem;

/// <summary>
/// Whether a path reaches somewhere other than itself.
/// </summary>
/// <remarks>
/// One test, because every walk of a tree needs it and needs the same answer. A directory link
/// inside a tree makes a walk unbounded, and one that leaves the tree puts files outside it into
/// whatever the walk was collecting; a walk that asks the question differently from its neighbour is
/// a walk that is unbounded on a tree its neighbour handles.
/// </remarks>
public static class LinkPaths
{
    /// <summary>
    /// <paramref name="path"/> as an absolute path with every symbolic link and junction along it followed, the form git
    /// reports paths in: the one resolution, which <see cref="IFileSystem.ResolveLinks"/> and every static caller share.
    /// The part of the path that does not exist is kept as spelled.
    /// </summary>
    /// <param name="path">The path.</param>
    /// <exception cref="IOException">A link could not be read, or the links form a cycle.</exception>
    public static string Resolve(string path)
    {
        // Followed as realpath follows them: after each link the walk starts again from the root,
        // so a link within a link's target is followed too, and a cycle of links ends the walk.
        const int MaxLinks = 40;
        var pending = Path.GetFullPath(path);

        for (var links = 0; links <= MaxLinks; links++)
        {
            var root = Path.GetPathRoot(pending) ?? string.Empty;
            var segments = pending[root.Length..].Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries);
            var resolved = root;
            string? restart = null;

            for (var index = 0; index < segments.Length && restart is null; index++)
            {
                var next = Path.Combine(resolved, segments[index]);
                var directory = new DirectoryInfo(next);

                if (directory.LinkTarget is { } target)
                {
                    restart = Path.Combine([Path.Combine(resolved, target), .. segments[(index + 1)..]]);
                }
                else if (!directory.Exists)
                {
                    // Nothing below a directory that does not exist can be a link.
                    return Path.TrimEndingDirectorySeparator(Path.Combine([next, .. segments[(index + 1)..]]));
                }
                else
                {
                    resolved = next;
                }
            }

            if (restart is null)
            {
                return Path.TrimEndingDirectorySeparator(resolved);
            }

            pending = Path.GetFullPath(restart);
        }

        throw new IOException($"More than {MaxLinks} links along '{path}', which may form a cycle.");
    }

    /// <summary>
    /// Whether <paramref name="path"/> reaches somewhere other than itself, so that reading or
    /// walking it would act on something the tree does not contain.
    /// </summary>
    /// <param name="fileSystem">Resolves the path.</param>
    /// <param name="path">The path to test.</param>
    /// <param name="comparison">How this platform compares paths.</param>
    public static bool IsLink(IFileSystem fileSystem, string path, StringComparison comparison)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);

        var resolved = fileSystem.ResolveLinks(path);

        return !string.Equals(
            Path.TrimEndingDirectorySeparator(resolved),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)),
            comparison);
    }

    /// <summary>
    /// Where <paramref name="relative"/> below <paramref name="root"/> leads, where a link lies along it below the root;
    /// null where none does. A root reached through a link of its own - a temporary directory, a worktrees root on another
    /// disk - is no link below it: the path is compared with the same path below where the root reaches.
    /// </summary>
    /// <param name="fileSystem">Resolves the path.</param>
    /// <param name="root">The tree's root, as spelt.</param>
    /// <param name="reachedRoot">Where the root reaches, as <see cref="IFileSystem.ResolveLinks"/> gives it.</param>
    /// <param name="relative">The path below the root.</param>
    /// <param name="comparison">How this platform compares paths.</param>
    /// <exception cref="IOException">A link along it cannot be followed.</exception>
    public static string? LeadsElsewhere(IFileSystem fileSystem, string root, string reachedRoot, string relative, StringComparison comparison)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);

        var reached = fileSystem.ResolveLinks(Path.Combine(root, relative));
        return PathContainment.AreSame(reached, Path.Combine(reachedRoot, relative), comparison) ? null : reached;
    }
}
