namespace RepoHarness.Core.Sync;

/// <summary>
/// Which run is using a copy kept beside a tree, as whatever would remove the copy asks first: a mutation worker's copy
/// is claimed by the sweep mutating it, and is not removed from under it.
/// </summary>
public interface ICopyClaims
{
    /// <summary>
    /// The run that holds <paramref name="copy"/> and still runs, as a line names it; <see langword="null"/> where none
    /// does. A claim that cannot be read holds its copy, and is said as that: never read as no claim.
    /// </summary>
    /// <param name="copy">The copy.</param>
    string? HeldBy(string copy);

    /// <summary>Forgets whatever claim is recorded on <paramref name="copy"/>, once the copy is gone.</summary>
    /// <param name="copy">The copy that was removed.</param>
    void Forget(string copy);
}
