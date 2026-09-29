using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Repository;

namespace RepoHarness.Core.Worktrees;

/// <summary>What a worktree's declared evidence roots hold, root by root, as a removal of the worktree would reach it.</summary>
/// <param name="Files">Each root holding files, with every file under it, relative to the worktree and spelt with forward slashes.</param>
/// <param name="Linked">
/// The roots that are links, or are reached through one: what they lead to is outside the worktree, which a removal leaves
/// where it is, so nothing of it is the removal's to lose, to keep or to delete.
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
/// through is one, or one sits under it. A root reaching outside the worktree holds nothing a removal would take.
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

                if (!PathContainment.AreSame(fileSystem.ResolveLinks(top), Path.Combine(reachedWorktree, root), comparison))
                {
                    linked.Add(root);
                    continue;
                }

                IReadOnlyList<string> found = fileSystem.KindOf(top) switch
                {
                    PathKind.File => [root],
                    PathKind.Directory => [.. fileSystem.EnumerateFiles(top, recursive: true).Select(file => PathPatterns.Normalize(Path.GetRelativePath(worktree, file))).Order(StringComparer.Ordinal)],
                    PathKind.Link => throw new IOException($"'{top}' became a link while it was being read"),
                    _ => [],
                };

                if (found.Count > 0)
                {
                    files[root] = found;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                unreadable.Add((root, ex.Message.TrimEnd('.')));
            }
        }

        return new EvidenceFiles(new Dictionary<string, IReadOnlyList<string>>(files, StringComparer.Ordinal), linked, unreadable);
    }
}
