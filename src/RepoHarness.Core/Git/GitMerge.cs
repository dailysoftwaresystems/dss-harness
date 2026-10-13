using RepoHarness.Core.Output;

namespace RepoHarness.Core.Git;

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
}

/// <summary>What the three sides of a merge are called, in the markers of a conflict.</summary>
/// <param name="Ours">The side the merge is made into.</param>
/// <param name="Ancestor">What both sides held.</param>
/// <param name="Theirs">The side merged in.</param>
public sealed record GitMergeNames(string Ours, string Ancestor, string Theirs);

/// <summary>
/// What merging three versions of one text gave, as <c>git merge-file</c> merges them: one of three outcomes - clean,
/// with the merged bytes and the hunks each side brought; conflicting, with how many places conflict and what they hold;
/// or no merge at all, with git's reason - each made by its own factory, so no value holds two of them.
/// </summary>
public sealed record GitTextMerge
{
    /// <summary>The most conflicts git counts: its exit code names the number, cut short at this many.</summary>
    public const int MostConflictsCounted = 127;

    /// <summary>How many conflicts are shown.</summary>
    public const int MostConflictsShown = 6;

    /// <summary>How many lines of one conflict are shown.</summary>
    public const int MostLinesShown = 40;

    private GitTextMerge(byte[]? merged, int conflicts, IReadOnlyList<string> shown, int oursHunks, int theirsHunks, string? notMerged)
    {
        Merged = merged;
        Conflicts = conflicts;
        Shown = shown;
        OursHunks = oursHunks;
        TheirsHunks = theirsHunks;
        NotMerged = notMerged;
    }

    /// <summary>The merged bytes, where the merge is clean; null otherwise.</summary>
    public byte[]? Merged { get; }

    /// <summary>
    /// How many places conflict, as git counts them: none where the merge is clean or was not made, and never more than
    /// <see cref="MostConflictsCounted"/>, which stands for that many or more.
    /// </summary>
    public int Conflicts { get; }

    /// <summary>
    /// Each conflict as git writes it, from its opening marker to its closing one, a line to an element: the first
    /// <see cref="MostConflictsShown"/> of them, <see cref="MostLinesShown"/> lines of each, with a line saying how much
    /// more there is; empty where nothing conflicts.
    /// </summary>
    public IReadOnlyList<string> Shown { get; }

    /// <summary>How many hunks of the clean merge <see cref="GitMergeNames.Ours"/> changed from what both held, as git diff counts them.</summary>
    public int OursHunks { get; }

    /// <summary>How many <see cref="GitMergeNames.Theirs"/> changed; a change both made alike is counted for each.</summary>
    public int TheirsHunks { get; }

    /// <summary>Why git would not merge them, in git's own words - a binary file, for one; null where it merged or found conflicts.</summary>
    public string? NotMerged { get; }

    /// <summary>Whether the three merged with no conflict.</summary>
    public bool IsClean => Merged is not null;

    /// <summary>How many places conflict, as a message says it: the count, or that it is the most git counts, or more.</summary>
    public string ConflictsSaid
        => Conflicts >= MostConflictsCounted ? $"{ReportText.Counted(MostConflictsCounted, "place")} or more" : ReportText.Counted(Conflicts, "place");

    /// <summary>A clean merge.</summary>
    /// <param name="merged">The merged bytes.</param>
    /// <param name="oursHunks">How many hunks ours changed from what both held.</param>
    /// <param name="theirsHunks">How many theirs changed.</param>
    public static GitTextMerge Clean(byte[] merged, int oursHunks, int theirsHunks)
    {
        ArgumentNullException.ThrowIfNull(merged);

        return new GitTextMerge(merged, 0, [], oursHunks, theirsHunks, null);
    }

    /// <summary>A merge that conflicts.</summary>
    /// <param name="conflicts">How many places conflict, as git counts them: at least one, at most <see cref="MostConflictsCounted"/>.</param>
    /// <param name="shown">The conflicts, as they are shown.</param>
    public static GitTextMerge Conflicting(int conflicts, IReadOnlyList<string> shown)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(conflicts, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(conflicts, MostConflictsCounted);
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
}
