using DssHarness.Core.Git;
using DssHarness.Core.Results;

namespace DssHarness.Core.Repository;

/// <summary>
/// Which paths name a directory in one tree that does not change - a commit's, as git holds it - read once: the answer
/// every reader of a list of a tree's own paths (<see cref="GlobPattern.FromRoot"/>) asks of a run, the tree of the commit
/// the run started from. The plan's check, the holds of the issues being fixed, a step's <c>writes</c> and a fold's
/// <c>within</c> so read one entry alike for the whole run, whatever the run's own folds make of the main tree or an
/// agent's meanwhile: an entry that named no directory when the plan was checked names none when it is folded.
/// </summary>
/// <remarks>
/// A tree that could not be read - the run names no commit it started from, or git could not list it - answers by what is
/// asked of it: where the question is whether two entries meet (<see cref="Meeting"/>), every path counts as a directory,
/// the wide reading, so nothing beneath one is taken to meet nothing; where it is what a step may write or a fold carry
/// (<see cref="Writing"/>), none does, the narrow reading, so nothing beneath one is let through.
/// </remarks>
public sealed class TreeDirectories
{
    private readonly HashSet<string>? _directories;

    private TreeDirectories(string? commit, HashSet<string>? directories, string? unread)
    {
        Commit = commit;
        _directories = directories;
        Unread = unread;
    }

    /// <summary>The tree of a run that names no commit it started from: read as one that could not be read.</summary>
    public static TreeDirectories NoCommit { get; } = new(null, null, "the run names no commit it started from");

    /// <summary>The commit whose tree this is; null where it names none.</summary>
    public string? Commit { get; }

    /// <summary>Why the tree could not be read; null where it was.</summary>
    public string? Unread { get; }

    /// <summary>
    /// The directories of <paramref name="commit"/>'s tree, read through git once (<see cref="IGitClient.ListDirectoriesAtCommitAsync"/>):
    /// each a path from the root, compared as <paramref name="comparison"/> compares the tree's names; one that could not
    /// be read where git could not list it, or <see cref="NoCommit"/> where no commit is named.
    /// </summary>
    /// <param name="git">Lists the tree.</param>
    /// <param name="repository">A directory inside the repository.</param>
    /// <param name="commit">The commit; null where the run names none.</param>
    /// <param name="comparison">How the tree's names compare: the case rule of the trees the paths are written in.</param>
    /// <param name="cancellationToken">Stops git.</param>
    public static async Task<TreeDirectories> AtAsync(IGitClient git, string repository, string? commit, StringComparison comparison, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(git);
        ArgumentException.ThrowIfNullOrWhiteSpace(repository);

        if (commit is null)
        {
            return NoCommit;
        }

        try
        {
            var listed = await git.ListDirectoriesAtCommitAsync(repository, commit, cancellationToken).ConfigureAwait(false);
            return new(commit, new HashSet<string>(listed.Where(name => name.IsUtf8).Select(name => name.Text), StringComparer.FromComparison(comparison)), null);
        }
        catch (HarnessException ex)
        {
            return new(commit, null, $"git could not list the directories of '{commit}': {ex.Message.TrimEnd('.')}");
        }
    }

    /// <summary>A tree that holds exactly <paramref name="directories"/>, each a path from its root with forward separators.</summary>
    /// <param name="commit">The commit the tree is of.</param>
    /// <param name="directories">Its directories.</param>
    /// <param name="comparison">How its names compare.</param>
    public static TreeDirectories Of(string commit, IEnumerable<string> directories, StringComparison comparison = StringComparison.Ordinal)
    {
        ArgumentNullException.ThrowIfNull(commit);
        ArgumentNullException.ThrowIfNull(directories);

        return new(commit, new HashSet<string>(directories, StringComparer.FromComparison(comparison)), null);
    }

    /// <summary>
    /// Whether <paramref name="path"/>, from the root with forward separators, names a directory, where the question is
    /// whether two entries meet: the tree's answer, and yes where it could not be read.
    /// </summary>
    /// <param name="path">The path.</param>
    public bool Meeting(string path) => _directories?.Contains(path) ?? true;

    /// <summary>
    /// Whether <paramref name="path"/>, from the root with forward separators, names a directory, where the question is
    /// what a step may write or a fold carry beneath it: the tree's answer, and no where it could not be read.
    /// </summary>
    /// <param name="path">The path.</param>
    public bool Writing(string path) => _directories?.Contains(path) ?? false;
}
