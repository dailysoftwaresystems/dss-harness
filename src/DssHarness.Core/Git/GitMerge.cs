using DssHarness.Core.Output;

namespace DssHarness.Core.Git;

/// <summary>A file's content, wherever it is: a blob the repository holds, a file on disk, or bytes in hand.</summary>
public abstract record GitContent
{
    private GitContent()
    {
    }

    /// <summary>A blob the repository holds.</summary>
    /// <param name="Id">The blob's id.</param>
    public sealed record Blob(string Id) : GitContent;

    /// <summary>A file on disk, in a work tree or anywhere else.</summary>
    /// <param name="Path">The file's path.</param>
    public sealed record File(string Path) : GitContent;

    /// <summary>Bytes in hand, as a work tree would hold them.</summary>
    /// <param name="Content">The bytes.</param>
    public sealed record Bytes(byte[] Content) : GitContent;

    /// <summary>
    /// The bytes a work tree held, known by the blob the repository holds them in: the blob's bytes as git stores them,
    /// or, where <paramref name="CheckedOutIn"/> names a work tree, as a checkout of the blob at the path into it writes
    /// them. Read as a file is, and never through this process's memory.
    /// </summary>
    /// <param name="Id">The blob's id.</param>
    /// <param name="CheckedOutIn">The work tree whose rules check the blob out; null where the bytes are the blob's as git stores them.</param>
    public sealed record BlobAsFile(string Id, string? CheckedOutIn) : GitContent;
}

/// <summary>What the three sides of a merge are called, in the markers of a conflict.</summary>
/// <param name="Ours">The side the merge is made into.</param>
/// <param name="Ancestor">What both sides held.</param>
/// <param name="Theirs">The side merged in.</param>
public sealed record GitMergeNames(string Ours, string Ancestor, string Theirs);

/// <summary>
/// What merging three versions of one text gave, as <c>git merge-file</c> merges them: one of four outcomes - clean,
/// with the blob the merged text is and the hunks each side brought; conflicting, with how many places conflict and what
/// they hold, every one of them; no merge at all, with git's reason; or a version git could not bring to the form it
/// stores, with git's words - each made by its own factory, so no value holds two of them.
/// </summary>
public sealed record GitTextMerge
{
    /// <summary>
    /// The most conflicts <c>git merge-file</c>'s exit code names: git's own fact, its count cut there. Read only to tell
    /// an exit that says the merge conflicts from one that says it failed; the conflicts are counted by their markers,
    /// which have no such cut.
    /// </summary>
    public const int MostConflictsCounted = 127;

    private GitTextMerge(string? blob, int conflicts, IReadOnlyList<string> shown, int oursHunks, int theirsHunks, string? notMerged, string? notStored = null)
    {
        Blob = blob;
        Conflicts = conflicts;
        Shown = shown;
        OursHunks = oursHunks;
        TheirsHunks = theirsHunks;
        NotMerged = notMerged;
        NotStored = notStored;
    }

    /// <summary>
    /// The blob the merged text is, as git stores it, written into the repository's objects, where the merge is clean;
    /// null otherwise. A checkout of it is what a tree holds (<see cref="IGitClient.CheckedOutAsync"/>).
    /// </summary>
    public string? Blob { get; }

    /// <summary>How many places conflict, counted by their markers: none where the merge is clean or was not made.</summary>
    public int Conflicts { get; }

    /// <summary>
    /// Each conflict as git writes it, from its opening marker to its closing one, a line to an element: every conflict,
    /// and every line of each, in order; empty where nothing conflicts.
    /// </summary>
    public IReadOnlyList<string> Shown { get; }

    /// <summary>How many hunks of the clean merge <see cref="GitMergeNames.Ours"/> changed from what both held, as git diff counts them.</summary>
    public int OursHunks { get; }

    /// <summary>How many <see cref="GitMergeNames.Theirs"/> changed; a change both made alike is counted for each.</summary>
    public int TheirsHunks { get; }

    /// <summary>Why git would not merge them, in git's own words - a binary file, for one; null where it merged or found conflicts.</summary>
    public string? NotMerged { get; }

    /// <summary>Why git could not bring a version to the form it stores - a filter that failed, for one - in git's words; null otherwise.</summary>
    public string? NotStored { get; }

    /// <summary>Whether the three merged with no conflict.</summary>
    public bool IsClean => Blob is not null;

    /// <summary>How many places conflict, as a message says it: the count.</summary>
    public string ConflictsSaid => ReportText.Counted(Conflicts, "place");

    /// <summary>A clean merge.</summary>
    /// <param name="blob">The blob the merged text is, in the repository's objects.</param>
    /// <param name="oursHunks">How many hunks ours changed from what both held.</param>
    /// <param name="theirsHunks">How many theirs changed.</param>
    public static GitTextMerge Clean(string blob, int oursHunks, int theirsHunks)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(blob);

        return new GitTextMerge(blob, 0, [], oursHunks, theirsHunks, null);
    }

    /// <summary>A merge that conflicts.</summary>
    /// <param name="conflicts">How many places conflict, counted by their markers: at least one.</param>
    /// <param name="shown">Every conflict, every line of each.</param>
    public static GitTextMerge Conflicting(int conflicts, IReadOnlyList<string> shown)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(conflicts, 1);
        ArgumentNullException.ThrowIfNull(shown);

        return new GitTextMerge(null, conflicts, shown, 0, 0, null);
    }

    /// <summary>No merge at all.</summary>
    /// <param name="why">Why git would not merge them, in its own words.</param>
    public static GitTextMerge Unmerged(string why)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(why);

        return new GitTextMerge(null, 0, [], 0, 0, why);
    }

    /// <summary>No merge, for a version git could not bring to the form it stores.</summary>
    /// <param name="why">git's words.</param>
    public static GitTextMerge Unstored(string why)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(why);

        return new GitTextMerge(null, 0, [], 0, 0, null, why);
    }
}
