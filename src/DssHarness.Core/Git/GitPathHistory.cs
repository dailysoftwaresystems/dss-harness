namespace DssHarness.Core.Git;

/// <summary>A version of a path a history held: the blob, and a commit that holds it there.</summary>
/// <param name="Commit">The oldest of the commits read, one after another, that hold the blob at the path.</param>
/// <param name="Blob">The blob's id.</param>
public sealed record GitHeldAt(string Commit, string Blob);

/// <summary>
/// What a history held at each of some paths: every version found among the newest commits that changed any of them, and
/// whether the history goes further back than was read.
/// </summary>
/// <param name="Held">For each path asked about, its versions, newest first; none where no commit read holds a file there.</param>
/// <param name="Commits">How many commits were read.</param>
/// <param name="Cut">Whether more commits changed the paths than were read: an older version is then not among those listed.</param>
public sealed record GitPathHistory(IReadOnlyDictionary<string, IReadOnlyList<GitHeldAt>> Held, int Commits, bool Cut);

/// <summary>A commit that renamed a file, as git's rename detection finds it against the commit's first parent.</summary>
/// <param name="Commit">The commit that renamed it.</param>
/// <param name="Parent">The commit's first parent, which held the file at <paramref name="From"/>.</param>
/// <param name="From">The path the file had before, from the repository's root.</param>
/// <param name="To">The path the commit gave it.</param>
public sealed record GitRename(string Commit, string Parent, string From, string To);
