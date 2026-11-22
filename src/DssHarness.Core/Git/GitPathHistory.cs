namespace DssHarness.Core.Git;

/// <summary>A version of a path a history held: the blob, and a commit that holds it there.</summary>
/// <param name="Commit">The oldest of the commits read, one after another, that hold the blob at the path.</param>
/// <param name="Blob">The blob's id.</param>
public sealed record GitHeldAt(string Commit, string Blob);

/// <summary>What a history held at each of some paths: every version found among every commit of it that changed any of them.</summary>
/// <param name="Held">For each path asked about, its versions, newest first; none where no commit holds a file there.</param>
/// <param name="Commits">How many commits changed them, each read.</param>
public sealed record GitPathHistory(IReadOnlyDictionary<string, IReadOnlyList<GitHeldAt>> Held, int Commits);

/// <summary>A rename of a file, as git's rename detection finds it: the name the file had before it.</summary>
/// <param name="From">The path the file had before the rename, from the repository's root.</param>
public sealed record GitRename(string From);
