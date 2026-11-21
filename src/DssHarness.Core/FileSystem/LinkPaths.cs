using DssHarness.Core.Walks;

namespace DssHarness.Core.FileSystem;

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
    /// <remarks>
    /// No count of links ends it: a chain of links however long is followed to its end, each link once, and the walk ends
    /// short only where links lead back to one still being followed - a cycle, which no count could tell from a long chain.
    /// Where the system itself is asked to follow the path, its own bound on a chain of links is its fact, met there.
    /// </remarks>
    /// <param name="path">The path.</param>
    /// <exception cref="IOException">A link could not be read, or the links along the path lead back to one of themselves.</exception>
    public static string Resolve(string path) => new Resolution(path).Of(Path.GetFullPath(path)).Run();

    /// <summary>
    /// One resolution of a path (<see cref="Resolve"/>): realpath's walk, each directory along the path read in turn and a
    /// link replaced by where it leads, worked out once a link and kept, so a link a path meets again - through another link
    /// that leads through it - is never followed twice. A link met while where it leads is still being worked out leads
    /// back to itself: the cycle the walk ends at. The walk of a link's target runs on a stack of its own
    /// (<see cref="Frame{T}"/>), so a chain of links nesting however deep never reaches past the thread's.
    /// </summary>
    /// <param name="asked">The path asked about, as given, which a cycle is told by.</param>
    private sealed class Resolution(string asked)
    {
        private readonly string _asked = asked;

        /// <summary>Where each link met leads, by the path it stands at.</summary>
        private readonly Dictionary<string, string> _reached = new(StringComparer.Ordinal);

        /// <summary>The links whose targets are being worked out, by the path each stands at.</summary>
        private readonly HashSet<string> _following = new(StringComparer.Ordinal);

        /// <summary>Where the absolute, normalized <paramref name="full"/> leads.</summary>
        public Frame<string> Of(string full) => new(self => Walking(self, full));

        private IEnumerable<Frame> Walking(Frame<string> self, string full)
        {
            var root = Path.GetPathRoot(full) ?? string.Empty;
            var segments = full[root.Length..].Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries);
            var resolved = root;

            for (var index = 0; index < segments.Length; index++)
            {
                var next = Path.Combine(resolved, segments[index]);
                var directory = new DirectoryInfo(next);

                if (directory.LinkTarget is { } target)
                {
                    if (!_reached.TryGetValue(next, out var reached))
                    {
                        if (!_following.Add(next))
                        {
                            throw new IOException($"The links along '{_asked}' lead back to '{next}', which is still being followed: they form a cycle.");
                        }

                        // A target is read as the system reads one: below the directory the link stands in, its own
                        // '..' taken there, and every link along it followed in turn.
                        var leads = Of(Path.GetFullPath(Path.Combine(resolved, target)));
                        yield return leads;
                        reached = leads.Result;
                        _following.Remove(next);
                        _reached.Add(next, reached);
                    }

                    resolved = reached;
                }
                else if (!directory.Exists)
                {
                    // Nothing below a directory that does not exist can be a link.
                    self.Give(Path.TrimEndingDirectorySeparator(Path.Combine([next, .. segments[(index + 1)..]])));
                    yield break;
                }
                else
                {
                    resolved = next;
                }
            }

            self.Give(Path.TrimEndingDirectorySeparator(resolved));
        }
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
