namespace RepoHarness.Core.Git;

/// <summary>One change <c>git status</c> names.</summary>
/// <param name="Index">The staged column: what the index changes against HEAD.</param>
/// <param name="WorkTree">The work tree column: what the work tree changes against the index; <c>?</c> for an untracked file.</param>
/// <param name="Path">The path changed, or, for a rename or a copy, its new path.</param>
/// <param name="Source">The path a rename or a copy was made from; <see langword="null"/> for any other change.</param>
public sealed record GitStatusEntry(char Index, char WorkTree, GitName Path, GitName? Source)
{
    /// <summary>Whether this is a rename or a copy, marked in either column.</summary>
    public bool IsRenameOrCopy => Index is 'R' or 'C' || WorkTree is 'R' or 'C';

    /// <summary>Every path this change touches: its own, and, for a rename or a copy, the one it was made from.</summary>
    public IReadOnlyList<GitName> Paths => Source is null ? [Path] : [Path, Source];

    /// <summary>The entry as a person reads it: both status columns, a space, and its path as git quotes one.</summary>
    public string Line => $"{Index}{WorkTree} {Path.Quoted}";
}
