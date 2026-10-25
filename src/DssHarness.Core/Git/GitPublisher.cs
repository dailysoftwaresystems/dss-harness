using DssHarness.Core.Output;
using DssHarness.Core.Results;

namespace DssHarness.Core.Git;

/// <summary>
/// The git writes a run's steps publish with (part 04's <c>IGitPublisher</c>): a commit's staging, by explicit path in
/// chunks and never <c>git add -A</c>; its one <c>git commit</c>; and the unstaging of what it staged and did not
/// commit. Each path is read literally, and nothing here merges, rewrites history or runs a workflow.
/// </summary>
public interface IGitPublisher
{
    /// <summary>
    /// Stages each of <paramref name="present"/> as the work tree at <paramref name="tree"/> holds it, and the removal of
    /// each of <paramref name="deleted"/> - one the index does not hold is left as it is - and nothing else.
    /// </summary>
    /// <param name="tree">The work tree's root.</param>
    /// <param name="present">Paths whose files the work tree holds, relative to its root with forward separators.</param>
    /// <param name="deleted">Paths whose files it no longer holds.</param>
    /// <param name="cancellationToken">Stops git.</param>
    /// <exception cref="HarnessException">git would not stage them (<see cref="HarnessExit.CommandFailed"/>).</exception>
    Task StageAsync(string tree, IReadOnlyList<string> present, IReadOnlyList<string> deleted, CancellationToken cancellationToken);

    /// <summary>
    /// Commits what the index at <paramref name="tree"/> holds, with the message <paramref name="messageFile"/> holds -
    /// blank lines at its ends and trailing blanks dropped and a run of blank lines made one, nothing else - and a sign-off
    /// where <paramref name="signOff"/> says; the repository's commit hooks run inside it.
    /// </summary>
    /// <param name="tree">The work tree's root.</param>
    /// <param name="messageFile">The file that holds the message.</param>
    /// <param name="signOff">Whether to add a sign-off (<c>-s</c>).</param>
    /// <param name="cancellationToken">Stops git.</param>
    /// <returns>The id of the commit made.</returns>
    /// <exception cref="HarnessException">git would not commit - a hook among the reasons - (<see cref="HarnessExit.CommandFailed"/>).</exception>
    Task<string> CommitAsync(string tree, string messageFile, bool signOff, CancellationToken cancellationToken);

    /// <summary>
    /// Puts the index at <paramref name="tree"/> back at each of <paramref name="paths"/> as HEAD holds it - where the
    /// branch has no commit yet, takes it out of the index, as <c>git reset</c> does there too - and leaves the work tree
    /// as it is.
    /// </summary>
    /// <param name="tree">The work tree's root.</param>
    /// <param name="paths">The paths, relative to its root with forward separators.</param>
    /// <param name="cancellationToken">Stops git.</param>
    /// <exception cref="HarnessException">git would not unstage them (<see cref="HarnessExit.CommandFailed"/>).</exception>
    Task UnstageAsync(string tree, IReadOnlyList<string> paths, CancellationToken cancellationToken);
}

/// <summary>The git writes a run's steps publish with, over <see cref="IGitClient.RunAsync"/>.</summary>
/// <param name="git">Runs git, and reads the commit made.</param>
public sealed class GitPublisher(IGitClient git) : IGitPublisher
{
    /// <inheritdoc/>
    public async Task StageAsync(string tree, IReadOnlyList<string> present, IReadOnlyList<string> deleted, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(present);
        ArgumentNullException.ThrowIfNull(deleted);

        await EachBatchAsync(tree, present, ["add", "--"], "stage", cancellationToken).ConfigureAwait(false);
        await EachBatchAsync(tree, deleted, ["rm", "--cached", "--ignore-unmatch", "-q", "--"], "stage the removal of", cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<string> CommitAsync(string tree, string messageFile, bool signOff, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageFile);

        var result = await git.RunAsync(tree, ["commit", "-F", messageFile, "--cleanup=whitespace", .. signOff ? ["-s"] : Array.Empty<string>()], echoOutput: false, cancellationToken)
            .ConfigureAwait(false);

        if (!result.Succeeded)
        {
            throw new HarnessException(HarnessExit.CommandFailed, $"git did not commit: {result.FailureMessage}");
        }

        return await git.ResolveCommitAsync(tree, "HEAD", cancellationToken).ConfigureAwait(false)
            ?? throw new HarnessException(HarnessExit.CommandFailed, "git said it committed, and HEAD names no commit");
    }

    /// <inheritdoc/>
    public Task UnstageAsync(string tree, IReadOnlyList<string> paths, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paths);

        return EachBatchAsync(tree, paths, ["reset", "-q", "--"], "unstage", cancellationToken);
    }

    /// <summary>Runs <paramref name="command"/> over <paramref name="paths"/>, each read literally, in as many runs as one command line holds.</summary>
    private async Task EachBatchAsync(string tree, IReadOnlyList<string> paths, IReadOnlyList<string> command, string what, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tree);

        foreach (var batch in GitClient.CommandLineBatches(paths))
        {
            var result = await git.RunAsync(tree, ["--literal-pathspecs", .. command, .. batch], echoOutput: false, cancellationToken).ConfigureAwait(false);

            if (!result.Succeeded)
            {
                throw new HarnessException(HarnessExit.CommandFailed, $"git could not {what} {ReportText.Counted(batch.Count, "path")}: {result.FailureMessage}");
            }
        }
    }
}
