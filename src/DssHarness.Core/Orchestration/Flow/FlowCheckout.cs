using DssHarness.Core.Git;
using DssHarness.Core.Output;
using DssHarness.Core.Results;

namespace DssHarness.Core.Orchestration.Flow;

/// <summary>
/// What the main checkout is as a run looks at it - the one probe a run's start and its <c>orient</c> step read alike: the
/// branch it is on and the commit, a merge under way, what is staged and what is changed, and the remote the run's
/// branch goes to with the branch that remote names its default.
/// </summary>
/// <param name="Branch">The branch HEAD is on; null where HEAD is detached.</param>
/// <param name="Head">The commit HEAD names; null where it names none yet.</param>
/// <param name="MergeInProgress">Whether a merge is under way in the checkout.</param>
/// <param name="Staged">The paths whose changes are staged, as git names them.</param>
/// <param name="Dirty">Every path the checkout changes - staged, not staged or untracked - as git names them, each once, in order.</param>
/// <param name="Remotes">The repository's remotes, as git lists them.</param>
/// <param name="Remote">
/// The remote the run's branch goes to: the one the flow's <c>push</c> step names, else the remote the branch tracks,
/// else <c>origin</c> (<see cref="GitRemotes.ConventionalRemote"/>), else the only one there is; null where none is found.
/// </param>
/// <param name="RemoteDefault">The branch <paramref name="Remote"/> names its default, without the remote's name; null where it names none here.</param>
public sealed record FlowCheckout(
    string? Branch,
    string? Head,
    bool MergeInProgress,
    IReadOnlyList<string> Staged,
    IReadOnlyList<string> Dirty,
    IReadOnlyList<string> Remotes,
    string? Remote,
    string? RemoteDefault)
{
    /// <summary>Whether the repository has a remote at all.</summary>
    public bool HasRemote => Remotes.Count > 0;

    /// <summary>
    /// The branch the run's pull request is opened against (<c>orient.baseBranch</c>): the flow's <c>pull-request</c>
    /// step's base where it names one, else the remote's default branch, and null where there is neither.
    /// </summary>
    /// <param name="pullRequestBase">The pull-request step's base, filled (<see cref="FlowTemplate.PullRequestBase"/>); null where none is named.</param>
    public string? BaseBranch(string? pullRequestBase) => pullRequestBase ?? RemoteDefault;

    /// <summary>
    /// Why a run cannot start from this checkout, worded once for the run's start: HEAD detached, the branch its pull
    /// request is opened against (<see cref="BaseBranch"/>) checked out, a merge under way, changes staged. Null where it can.
    /// </summary>
    /// <param name="pullRequestBase">The pull-request step's base, filled (<see cref="FlowTemplate.PullRequestBase"/>); null where none is named.</param>
    public string? StartRefusal(string? pullRequestBase)
        => Branch is null
            ? "the main checkout's HEAD is detached, and a run commits its work on a branch: check out a branch of its own for it"
            : BaseBranch(pullRequestBase) is { } baseBranch && string.Equals(Branch, baseBranch, StringComparison.Ordinal)
                ? $"the main checkout is on '{Branch}', the branch the run's pull request is opened against, and a run commits its work on a branch of its own: create one for it and check it out"
                : MergeInProgress
                    ? "a merge is under way in the main checkout, and a run folds its work into the tree the merge is writing: finish the merge, or abort it"
                    : Staged.Count > 0
                        ? $"the main checkout has staged changes ({ReportText.Counted(Staged.Count, "path")}: {string.Join(", ", Staged.Take(5))}{(Staged.Count > 5 ? ", ..." : string.Empty)}), and a run's commit stages what it folded alone: commit them, or unstage them"
                        : null;

    /// <summary>
    /// What a run's start says where the branch its pull request is opened against (<see cref="BaseBranch"/>) cannot be
    /// told although the repository has a remote - no pull-request step names a base, and the remote the run's branch
    /// goes to names no default branch here, or no remote is that one: the checkout's branch was held to none of it, and
    /// a run never starts so unsaid. Null where the base is told, where HEAD is detached, which refuses the start, and
    /// where the repository has no remote, which no pull request is opened on.
    /// </summary>
    /// <param name="pullRequestBase">The pull-request step's base, filled (<see cref="FlowTemplate.PullRequestBase"/>); null where none is named.</param>
    public string? BaseUnknown(string? pullRequestBase)
        => BaseBranch(pullRequestBase) is not null || Branch is null || !HasRemote
            ? null
            : "the branch the run's pull request is opened against cannot be told here - no pull-request step names a base, and "
                + (Remote is null
                    ? $"no remote is the one the run's branch goes to (the push step names none, the branch tracks none, and none is {GitRemotes.ConventionalRemote})"
                    : $"remote '{Remote}' names no default branch here")
                + $" - so the main checkout's branch '{Branch}' was held to none: name the base on the pull-request step"
                + (Remote is null ? ", or the remote on the push step" : $", or read the remote's own with git remote set-head {Remote} --auto");

    /// <summary>Reads the checkout at <paramref name="root"/>, its remote chosen as <paramref name="definition"/>'s push step names it.</summary>
    /// <param name="gitClient">Git.</param>
    /// <param name="root">The main checkout's root.</param>
    /// <param name="definition">The workflow the run runs, whose <c>push</c> step may name the remote.</param>
    /// <param name="cancellationToken">Cancels the git processes.</param>
    /// <exception cref="HarnessException">Git could not answer (<see cref="HarnessExit.CommandFailed"/>).</exception>
    public static async Task<FlowCheckout> ReadAsync(IGitClient gitClient, string root, WorkflowDefinition definition, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(gitClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(definition);

        var branch = await gitClient.GetBranchAsync(root, cancellationToken).ConfigureAwait(false);
        var head = await gitClient.ResolveCommitAsync(root, "HEAD", cancellationToken).ConfigureAwait(false);
        var merging = (await gitClient.ListMergeHeadsAsync(root, cancellationToken).ConfigureAwait(false)).Count > 0;
        var status = await gitClient.ReadStatusAsync(root, cancellationToken).ConfigureAwait(false);
        var remotes = await gitClient.ListRemotesAsync(root, cancellationToken).ConfigureAwait(false);

        var named = WorkflowFiles.Steps(definition).Select(entry => entry.Step).FirstOrDefault(step => step.Type == "push" && step.Remote is not null)?.Remote;
        var tracked = branch is null ? null : await gitClient.TrackedRemoteAsync(root, branch, cancellationToken).ConfigureAwait(false);
        // A branch may track a branch of this repository itself ('.'), which is no remote a pull request is opened on.
        var remote = named
            ?? (tracked is not null && remotes.Contains(tracked, StringComparer.Ordinal) ? tracked : null)
            ?? (remotes.Contains(GitRemotes.ConventionalRemote, StringComparer.Ordinal) ? GitRemotes.ConventionalRemote : remotes.Count == 1 ? remotes[0] : null);

        string? remoteDefault = null;

        if (remote is not null && remotes.Contains(remote, StringComparer.Ordinal)
            && (await gitClient.DefaultBranchAsync(root, remote, cancellationToken).ConfigureAwait(false)).Reference is { } reference)
        {
            remoteDefault = reference.StartsWith(remote + "/", StringComparison.Ordinal) ? reference[(remote.Length + 1)..] : reference;
        }

        return new FlowCheckout(
            branch,
            head,
            merging,
            [.. status.Where(entry => entry.Index is not (' ' or '?' or '!')).Select(entry => entry.Path.Text).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
            [.. status.SelectMany(entry => entry.Paths).Select(path => path.Text).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
            remotes,
            remote,
            remoteDefault);
    }
}
