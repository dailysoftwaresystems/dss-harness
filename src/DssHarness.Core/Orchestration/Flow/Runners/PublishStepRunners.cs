using System.Text.Json.Nodes;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Git;
using DssHarness.Core.Platform;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;

namespace DssHarness.Core.Orchestration.Flow;

/// <summary>
/// A <c>push</c> step (part 04): pushes the commit the main checkout's HEAD names to its branch on the remote - the one
/// the step names, else the one the run's branch goes to (<see cref="FlowCheckout.Remote"/>) - as a fast-forward and never
/// forced, through <see cref="IGitPublisher.PushAsync"/> under the step's time limit, else the push's own
/// (<see cref="PublisherCommands.Push"/>). It
/// answers whether the remote's branch moved, the remote, the branch and the commit it pushed, which a <c>ci-wait</c> that
/// names no commit waits on. HEAD detached, the main checkout on another branch than the run started on
/// (<see cref="FlowRunPlace.OffItsBranch"/>), no commit yet or no remote fails it 13 with nothing pushed; a push the remote
/// refused fails it 20, one that did not finish in time 21. A checkpoint push the switch keeps off never reaches it: the
/// engine skips it (<c>pushCheckpoints</c>). Run again, it pushes what is pushed already, and answers <c>pushed</c> false.
/// </summary>
/// <param name="contextLoader">Finds the repository.</param>
/// <param name="platform">This machine.</param>
/// <param name="git">Reads the main checkout: its branch, its HEAD and its remotes.</param>
/// <param name="publisher">Pushes.</param>
public sealed class PushStepRunner(
    IHarnessContextLoader contextLoader,
    IHostPlatform platform,
    IGitClient git,
    IGitPublisher publisher) : IFlowStepTypeRunner
{
    /// <inheritdoc/>
    public string Type => "push";

    /// <inheritdoc/>
    public async Task<FlowStepResult> RunAsync(FlowStepRun run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);

        var (place, _) = await FlowStepRunners.PlaceOf(run, contextLoader, platform, cancellationToken).ConfigureAwait(false);
        var tree = place.Repository;
        var checkout = await FlowCheckout.ReadAsync(git, tree, place.Definition, cancellationToken).ConfigureAwait(false);

        if (checkout.Branch is not { } branch)
        {
            return Refused($"'{run.Instance}' pushes the branch the main checkout is on, and its HEAD is detached: check out the run's branch, then resume. Nothing was pushed.");
        }

        if (place.OffItsBranch(run.Instance, branch, "pushed") is { } off)
        {
            return Refused(off);
        }

        if (checkout.Head is not { } commit)
        {
            return Refused($"'{run.Instance}' pushes '{branch}', which holds no commit yet. Nothing was pushed.");
        }

        if ((run.Step.Remote ?? checkout.Remote) is not { } remote)
        {
            return Refused($"'{run.Instance}' pushes to the remote the run's branch goes to, and {checkout.NoRemote}: name one on the step. Nothing was pushed.");
        }

        if (checkout.NotARemote(remote) is { } unknown)
        {
            return Refused($"'{run.Instance}' pushes to {unknown}: name one of them, or add it. Nothing was pushed.");
        }

        var pushed = await publisher.PushAsync(tree, remote, commit, branch, WorkflowStepTypes.CommandLimitOf(run.Step), cancellationToken).ConfigureAwait(false);

        return FlowStepResult.Ended(
            passed: true,
            HarnessExit.Success,
            output: new JsonObject { ["pushed"] = pushed, ["remote"] = remote, ["branch"] = branch, ["sha"] = commit });
    }

    private static FlowStepResult Refused(string why) => FlowStepResult.Ended(passed: false, HarnessExit.Refused, why);
}

/// <summary>
/// A <c>pull-request</c> step (part 04): opens the pull request of the branch the main checkout is on, against the base
/// its step names - filled from the run's inputs - else the branch the remote names its default
/// (<see cref="FlowCheckout.BaseBranch"/>), on the repository the address of the remote the run's branch goes to names
/// (<see cref="GitRemotes.ForgeAddress"/>) - on the host ssh's configuration gives an address over ssh, an alias for one
/// account among several (<see cref="SshHostNames"/>), and where neither names one, the repository the forge's tool reads
/// of the remotes itself - with its title filled and its body - the file it names, filled as the step
/// started - written to its directory for the forge's tool to read. One already open from that branch to that base is
/// answered as it is, <c>created</c> false, so a step run again opens no second; its title and body are a person's then.
/// It answers the pull request's number and address - one open from a fork's branch of the same name is none of the run's
/// (<see cref="IGitPublisher.FindPullRequestAsync"/>). A title blank once filled fails it 12; HEAD detached, the main
/// checkout on another branch than the run started on (<see cref="FlowRunPlace.OffItsBranch"/>), no remote or no base it
/// can tell fails it 13, nothing opened; the forge's tool not there 14, saying no 20, not answering in time 21
/// (<see cref="Ci.GhProcess"/>). The run never merges what it opened.
/// </summary>
/// <param name="contextLoader">Finds the repository and its configuration.</param>
/// <param name="platform">This machine.</param>
/// <param name="git">Reads the main checkout: its branch, its remotes and the address of the one used.</param>
/// <param name="publisher">Finds and opens the pull request.</param>
/// <param name="fileSystem">Writes the body the forge's tool reads.</param>
/// <param name="sshHosts">Says which host an address over ssh reaches.</param>
public sealed class PullRequestStepRunner(
    IHarnessContextLoader contextLoader,
    IHostPlatform platform,
    IGitClient git,
    IGitPublisher publisher,
    IFileSystem fileSystem,
    SshHostNames sshHosts) : IFlowStepTypeRunner
{
    /// <inheritdoc/>
    public string Type => "pull-request";

    /// <inheritdoc/>
    public async Task<FlowStepResult> RunAsync(FlowStepRun run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);

        var (place, context) = await FlowStepRunners.PlaceOf(run, contextLoader, platform, cancellationToken).ConfigureAwait(false);
        var layout = OrchestratorLayout.Of(context.Layout, place.Run);
        var tree = place.Repository;
        var body = FlowStepKeys.TextAtStart(run, "body");

        if (run.Step.Title?.Trim() is not { Length: > 0 } title)
        {
            return FlowStepResult.Ended(passed: false, HarnessExit.ConfigInvalid, $"'{run.Instance}' has a title that is blank once filled, and a pull request takes one: give the step a title, or values that fill it. Nothing was opened.");
        }

        var checkout = await FlowCheckout.ReadAsync(git, tree, place.Definition, cancellationToken).ConfigureAwait(false);

        if (checkout.Branch is not { } head)
        {
            return Refused($"'{run.Instance}' opens the pull request of the branch the main checkout is on, and its HEAD is detached: check out the run's branch, then resume. Nothing was opened.");
        }

        if (place.OffItsBranch(run.Instance, head, "opened") is { } off)
        {
            return Refused(off);
        }

        if (checkout.Remote is not { } remote)
        {
            return Refused($"'{run.Instance}' opens its pull request on the remote the run's branch goes to, and {checkout.NoRemote}: name one on the push step. Nothing was opened.");
        }

        if (checkout.NotARemote(remote) is { } unknown)
        {
            return Refused($"'{run.Instance}' opens its pull request on the remote the push step names, {unknown}: name one of them on the push step, or add it. Nothing was opened.");
        }

        if (checkout.BaseBranch(run.Step.Base is { Length: > 0 } named ? named : null) is not { } target)
        {
            return Refused($"'{run.Instance}' cannot tell the branch its pull request goes to: it names no base, and remote '{remote}' names no default branch here. Name the base on the step, or read the remote's own with git remote set-head {remote} --auto. Nothing was opened.");
        }

        if (string.Equals(target, head, StringComparison.Ordinal))
        {
            return Refused($"'{run.Instance}' would open a pull request from '{head}' to itself: the run's work is on a branch of its own, and its base is another. Nothing was opened.");
        }

        var address = GitRemotes.ForgeAddress(await git.UrlAsync(tree, remote, cancellationToken).ConfigureAwait(false));

        if (address is { OverSsh: true })
        {
            address = await sshHosts.HostNameAsync(address.Host, cancellationToken).ConfigureAwait(false) is { } dialed ? address with { Host = dialed } : null;
        }

        var where = new GitPullRequestTarget(address?.Repository, target, head);
        var limit = WorkflowStepTypes.CommandLimitOf(run.Step);

        if (await publisher.FindPullRequestAsync(tree, where, limit, cancellationToken).ConfigureAwait(false) is { } open)
        {
            return Answered(open, created: false);
        }

        var file = layout.InstancePullRequestBodyFile(run.Instance);
        fileSystem.CreateDirectory(Path.GetDirectoryName(file)!);
        fileSystem.WriteAllTextAtomic(file, body);

        return Answered(await publisher.OpenPullRequestAsync(tree, where, title, file, run.Step.Draft == true, limit, cancellationToken).ConfigureAwait(false), created: true);
    }

    private static FlowStepResult Refused(string why) => FlowStepResult.Ended(passed: false, HarnessExit.Refused, why);

    private static FlowStepResult Answered(GitPullRequest request, bool created)
        => FlowStepResult.Ended(
            passed: true,
            HarnessExit.Success,
            output: new JsonObject { ["number"] = request.Number, ["url"] = request.Url, ["created"] = created });
}
