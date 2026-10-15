using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Repository;

namespace RepoHarness.Core.Worktrees;

/// <summary>What a worktree's declared evidence roots hold, root by root, as a removal of the worktree would reach it.</summary>
/// <param name="Files">Each root holding files, with every file under it, relative to the worktree and spelt with forward slashes.</param>
/// <param name="Linked">
/// The roots that are links, or are reached through one, and the links to files under a root: what they lead to is outside
/// the worktree, or elsewhere in it, which a removal leaves where it is - it takes a link, never what the link leads to - so
/// nothing of it is the removal's to lose, to keep or to delete.
/// </param>
/// <param name="Unreadable">The roots that could not be read, each with why: an unread root is never an empty one.</param>
public sealed record EvidenceFiles(
    IReadOnlyDictionary<string, IReadOnlyList<string>> Files,
    IReadOnlyList<string> Linked,
    IReadOnlyList<(string Root, string Why)> Unreadable)
{
    /// <summary>Every file under every root, in order.</summary>
    public IReadOnlyList<string> All => [.. Files.Values.SelectMany(files => files).Order(StringComparer.Ordinal)];
}

/// <summary>
/// Finds what a worktree's evidence roots (<c>worktrees.evidenceRoots</c>) hold: the one reading every command asks of them
/// - delete-worktree deciding whether a removal would lose measurements, delete-agent keeping them - so the files one
/// counts are the files the other keeps.
/// </summary>
/// <remarks>
/// Found as a removal reaches them: never through a link or junction, whether the root is one, a directory it is reached
/// through is one, or one sits under it - a link to a file under a root included. A root reaching outside the worktree
/// holds nothing a removal would take.
/// </remarks>
public static class WorktreeEvidence
{
    /// <summary>What <paramref name="roots"/> hold in the worktree at <paramref name="worktree"/>.</summary>
    /// <param name="fileSystem">Looks.</param>
    /// <param name="comparison">How this machine compares paths.</param>
    /// <param name="worktree">The worktree, or what is left of it.</param>
    /// <param name="roots">The declared roots, relative to it.</param>
    public static EvidenceFiles Find(IFileSystem fileSystem, StringComparison comparison, string worktree, IReadOnlyList<string> roots)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(roots);

        var files = new SortedDictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var linked = new List<string>();
        var unreadable = new List<(string Root, string Why)>();
        string? reachedWorktree = null;

        foreach (var root in roots.Select(PathPatterns.Normalize).Where(root => root.Length > 0).Distinct(StringComparer.Ordinal))
        {
            var top = Path.GetFullPath(Path.Combine(worktree, root));

            if (!PathContainment.IsStrictlyInside(worktree, top, comparison))
            {
                continue;
            }

            try
            {
                reachedWorktree ??= fileSystem.ResolveLinks(worktree);

                if (LinkPaths.LeadsElsewhere(fileSystem, worktree, reachedWorktree, root, comparison) is not null)
                {
                    linked.Add(root);
                    continue;
                }

                var found = new List<string>();

                switch (fileSystem.KindOf(top))
                {
                    case PathKind.File:
                        found.Add(root);
                        break;

                    case PathKind.Directory:
                        foreach (var file in fileSystem.EnumerateFiles(top, recursive: true))
                        {
                            // Spelt with forward slashes where the platform separates, never by rewriting a backslash: on
                            // Linux a backslash is part of a name, and rewritten it would name another file.
                            var relative = Path.GetRelativePath(worktree, file).Replace(Path.DirectorySeparatorChar, '/');
                            (fileSystem.KindOf(file) == PathKind.Link ? linked : found).Add(relative);
                        }

                        break;

                    case PathKind.Link:
                        throw new IOException($"'{top}' became a link while it was being read");
                }

                if (found.Count > 0)
                {
                    files[root] = [.. found.Order(StringComparer.Ordinal)];
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                unreadable.Add((root, ex.Message.TrimEnd('.')));
            }
        }

        return new EvidenceFiles(new Dictionary<string, IReadOnlyList<string>>(files, StringComparer.Ordinal), [.. linked.Order(StringComparer.Ordinal)], unreadable);
    }
}
