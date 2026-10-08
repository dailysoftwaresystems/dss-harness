namespace RepoHarness.Core.Sync;

/// <summary>
/// Which run is using a copy kept beside a tree, as whatever would remove the copy asks first: a mutation worker's copy
/// is claimed by the sweep mutating it, and is not removed from under it. And what such a copy is named, which is all
/// that tells what an unfinished removal of one left aside.
/// </summary>
public interface ICopyClaims
{
    /// <summary>
    /// Whether <paramref name="name"/> is spelt as a copy of the family is: what follows the family's suffix in the name
    /// of a directory beside the tree. What a removal left aside holds no marker any more, and is told by this alone.
    /// </summary>
    /// <param name="name">What follows the family's suffix in a directory's name.</param>
    bool Names(string name);

    /// <summary>
    /// The run that holds <paramref name="copy"/> and still runs, as a line names it; <see langword="null"/> where none
    /// does. A claim that cannot be read holds its copy, and is said as that: never read as no claim.
    /// </summary>
    /// <param name="copy">The copy.</param>
    string? HeldBy(string copy);

    /// <summary>
    /// Forgets the claim recorded on <paramref name="copy"/>, once the copy is gone - unless a run still running holds
    /// it: one that claimed the copy while it was being removed, to make it again, keeps its claim.
    /// </summary>
    /// <param name="copy">The copy that was removed.</param>
    void Forget(string copy);
}
