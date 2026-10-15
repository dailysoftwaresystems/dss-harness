namespace DssHarness.Core.Repository;

/// <summary>
/// Maintains the harness's own block inside a repository's <c>.gitignore</c>.
/// </summary>
public interface IGitIgnoreManager
{
    /// <summary>
    /// Returns <paramref name="existingContent"/> with the managed block set to
    /// <paramref name="lines"/>, replacing a previous block when one is present and
    /// appending otherwise. A pure transformation: nothing outside the markers is
    /// touched, so hand written rules survive every run.
    /// </summary>
    string ApplyManagedBlock(string? existingContent, IReadOnlyList<string> lines);

    /// <summary>
    /// Applies the managed block to the file at <paramref name="path"/>, and says what that did to it.
    /// </summary>
    GitIgnoreUpdate Update(string path, IReadOnlyList<string> lines);
}

/// <summary>What <see cref="IGitIgnoreManager.Update"/> did to a <c>.gitignore</c>.</summary>
public enum GitIgnoreUpdate
{
    /// <summary>It already held the block as it is: nothing was written.</summary>
    Unchanged,

    /// <summary>It was written with the block as it is now.</summary>
    Updated,

    /// <summary>It was written with the block as it is now, the block moving from the markers of the tool's former name.</summary>
    MovedFromRetiredMarkers,
}
