using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using DssHarness.Core.Ci;
using DssHarness.Core.Output;
using DssHarness.Core.Results;

namespace DssHarness.Core.Git;

/// <summary>
/// What a run's steps publish with (part 04's <c>IGitPublisher</c>), each over one command of a closed list
/// (<see cref="PublisherCommands"/>): a commit's staging, by explicit path in chunks and never <c>git add -A</c>; its one
/// <c>git commit</c>; the unstaging of what it staged and did not commit; a push of one commit to one branch, never
/// forced; and the pull request of a branch, found where it is open and opened where it is not. Each path is read
/// literally, each command is ended where it has not finished in time - its step's limit where the step says one, else
/// its own - and nothing here merges, rewrites history or runs a workflow.
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
    /// <param name="limit">How long each command may take: the step's limit; null for each command's own.</param>
    /// <param name="cancellationToken">Stops git.</param>
    /// <exception cref="HarnessException">git would not stage them (<see cref="HarnessExit.CommandFailed"/>), or did not finish in time (<see cref="HarnessExit.Incomplete"/>).</exception>
    Task StageAsync(string tree, IReadOnlyList<string> present, IReadOnlyList<string> deleted, TimeSpan? limit, CancellationToken cancellationToken);

    /// <summary>
    /// Commits what the index at <paramref name="tree"/> holds, with the message <paramref name="messageFile"/> holds -
    /// blank lines at its ends and trailing blanks dropped and a run of blank lines made one, nothing else - and a sign-off
    /// where <paramref name="signOff"/> says; the repository's commit hooks run inside it.
    /// </summary>
    /// <param name="tree">The work tree's root.</param>
    /// <param name="messageFile">The file that holds the message.</param>
    /// <param name="signOff">Whether to add a sign-off (<c>-s</c>).</param>
    /// <param name="limit">How long the commit may take: the step's limit; null for its own.</param>
    /// <param name="cancellationToken">Stops git.</param>
    /// <returns>The id of the commit made.</returns>
    /// <exception cref="HarnessException">
    /// git would not commit - a hook among the reasons - (<see cref="HarnessExit.CommandFailed"/>), or did not finish in
    /// time (<see cref="HarnessExit.Incomplete"/>), its index as it left it.
    /// </exception>
    Task<string> CommitAsync(string tree, string messageFile, bool signOff, TimeSpan? limit, CancellationToken cancellationToken);

    /// <summary>
    /// Puts the index at <paramref name="tree"/> back at each of <paramref name="paths"/> as HEAD holds it - where the
    /// branch has no commit yet, takes it out of the index, as <c>git reset</c> does there too - and leaves the work tree
    /// as it is.
    /// </summary>
    /// <param name="tree">The work tree's root.</param>
    /// <param name="paths">The paths, relative to its root with forward separators.</param>
    /// <param name="limit">How long each command may take: the step's limit; null for each command's own.</param>
    /// <param name="cancellationToken">Stops git.</param>
    /// <exception cref="HarnessException">git would not unstage them (<see cref="HarnessExit.CommandFailed"/>), or did not finish in time (<see cref="HarnessExit.Incomplete"/>).</exception>
    Task UnstageAsync(string tree, IReadOnlyList<string> paths, TimeSpan? limit, CancellationToken cancellationToken);

    /// <summary>
    /// Pushes <paramref name="commit"/> to <paramref name="remote"/>'s <paramref name="branch"/>, as a fast-forward and
    /// never forced: a branch the remote moved since is refused there, and the push with it.
    /// </summary>
    /// <param name="tree">The work tree's root.</param>
    /// <param name="remote">The remote, by name.</param>
    /// <param name="commit">The commit pushed.</param>
    /// <param name="branch">The branch it is pushed to, on the remote, by its short name.</param>
    /// <param name="limit">How long the push may take: the step's limit; null for its own.</param>
    /// <param name="cancellationToken">Stops git.</param>
    /// <returns>Whether the remote's branch moved; false where it named the commit already.</returns>
    /// <exception cref="HarnessException">
    /// The remote refused it, or git could not reach it (<see cref="HarnessExit.CommandFailed"/>), or it did not finish in
    /// time (<see cref="HarnessExit.Incomplete"/>).
    /// </exception>
    Task<bool> PushAsync(string tree, string remote, string commit, string branch, TimeSpan? limit, CancellationToken cancellationToken);

    /// <summary>
    /// The pull request open from <paramref name="target"/>'s head to its base, of the target's own repository; null where
    /// none is - one open from a fork's branch of the same name is none.
    /// </summary>
    /// <param name="tree">The work tree's root, whose remotes the forge's tool reads where the target names no repository.</param>
    /// <param name="target">Where the pull request is.</param>
    /// <param name="limit">How long the read may take: the step's limit; null for its own.</param>
    /// <param name="cancellationToken">Stops the call.</param>
    /// <exception cref="HarnessException">The forge could not be read (<see cref="GhProcess.RunAsync"/>'s codes).</exception>
    Task<GitPullRequest?> FindPullRequestAsync(string tree, GitPullRequestTarget target, TimeSpan? limit, CancellationToken cancellationToken);

    /// <summary>Opens a pull request from <paramref name="target"/>'s head to its base.</summary>
    /// <param name="tree">The work tree's root, whose remotes the forge's tool reads where the target names no repository.</param>
    /// <param name="target">Where the pull request is.</param>
    /// <param name="title">Its title.</param>
    /// <param name="bodyFile">The file that holds its body.</param>
    /// <param name="draft">Whether it is opened as a draft.</param>
    /// <param name="limit">How long the call may take: the step's limit; null for its own.</param>
    /// <param name="cancellationToken">Stops the call.</param>
    /// <returns>The pull request opened.</returns>
    /// <exception cref="HarnessException">The forge would not open it (<see cref="GhProcess.RunAsync"/>'s codes), or named no address of it (<see cref="HarnessExit.CommandFailed"/>).</exception>
    Task<GitPullRequest> OpenPullRequestAsync(string tree, GitPullRequestTarget target, string title, string bodyFile, bool draft, TimeSpan? limit, CancellationToken cancellationToken);
}

/// <summary>Where a pull request is: the repository on the forge, and the branches it goes from and to.</summary>
/// <param name="Repository">
/// The repository on the forge, as <c>host/owner/name</c> (<see cref="GitForgeAddress.Repository"/>); null where its
/// remote's address names none, and the forge's tool then reads the work tree's remotes itself.
/// </param>
/// <param name="Base">The branch it goes to.</param>
/// <param name="Head">The branch it comes from.</param>
public sealed record GitPullRequestTarget(string? Repository, string Base, string Head);

/// <summary>A pull request: its number on the forge, and its address.</summary>
/// <param name="Number">Its number.</param>
/// <param name="Url">Its address.</param>
public sealed record GitPullRequest(long Number, string Url);

/// <summary>
/// One command of the closed list a run's steps publish with: the program, the arguments it always begins with, and how
/// long it may take where its step says no <c>timeoutMinutes</c>.
/// </summary>
/// <param name="Program">git, or the forge's tool.</param>
/// <param name="Leading">The arguments every run of it begins with.</param>
/// <param name="Limit">How long it may take where its step says none.</param>
public sealed record PublisherCommand(string Program, IReadOnlyList<string> Leading, TimeSpan Limit)
{
    /// <summary>The command as a person reads it: the program and its leading arguments.</summary>
    public string Shown => string.Join(' ', [Program, .. Leading]);
}

/// <summary>
/// The closed list of commands a run's steps publish with (<see cref="GitPublisher"/>): nothing else is run, and each is
/// ended where it has not finished in time - its step's <c>timeoutMinutes</c> where the step says one, else its own: five
/// minutes for a push and a call of the forge's tool, fifteen for a commit's, since the repository's commit hooks run
/// inside it.
/// </summary>
public static class PublisherCommands
{
    /// <summary>How long a commit's commands may take where the step says no limit: fifteen minutes, its hooks among it.</summary>
    public static readonly TimeSpan CommitLimit = TimeSpan.FromMinutes(15);

    /// <summary>How long a push may take where the step says no limit: five minutes, as a call of the forge's tool.</summary>
    public static readonly TimeSpan PushLimit = GhProcess.Limit;

    /// <summary>Stages paths as the work tree holds them.</summary>
    public static PublisherCommand Stage { get; } = new("git", ["--literal-pathspecs", "add", "--"], CommitLimit);

    /// <summary>Stages the removal of paths the work tree no longer holds.</summary>
    public static PublisherCommand StageRemoval { get; } = new("git", ["--literal-pathspecs", "rm", "--cached", "--ignore-unmatch", "-q", "--"], CommitLimit);

    /// <summary>Puts paths of the index back as HEAD holds them.</summary>
    public static PublisherCommand Unstage { get; } = new("git", ["--literal-pathspecs", "reset", "-q", "--"], CommitLimit);

    /// <summary>Commits the index, its message from a file and kept as written but for blanks.</summary>
    public static PublisherCommand Commit { get; } = new("git", ["commit", "--cleanup=whitespace", "-F"], CommitLimit);

    /// <summary>Pushes one commit to one branch of a remote, never forced.</summary>
    public static PublisherCommand Push { get; } = new("git", ["push", "--porcelain"], PushLimit);

    /// <summary>
    /// Reads the pull requests open from a branch to another, each with whether it comes from another repository: the forge's
    /// tool matches a head by its branch's name alone, a fork's among them.
    /// </summary>
    public static PublisherCommand FindPullRequest { get; } = new(GhProcess.Executable, ["pr", "list", "--state", "open", "--json", "number,url,isCrossRepository"], GhProcess.Limit);

    /// <summary>Opens a pull request.</summary>
    public static PublisherCommand OpenPullRequest { get; } = new(GhProcess.Executable, ["pr", "create"], GhProcess.Limit);

    /// <summary>Every command of the list, in the order a run uses them.</summary>
    public static IReadOnlyList<PublisherCommand> All { get; } = [Stage, StageRemoval, Unstage, Commit, Push, FindPullRequest, OpenPullRequest];
}

/// <summary>What a run's steps publish with, over <see cref="IGitClient.RunWithinAsync"/> and the one bounded <see cref="GhProcess"/>.</summary>
/// <param name="git">Runs git, and reads the commit made.</param>
/// <param name="gh">Runs the forge's tool.</param>
public sealed partial class GitPublisher(IGitClient git, GhProcess gh) : IGitPublisher
{
    /// <inheritdoc/>
    public async Task StageAsync(string tree, IReadOnlyList<string> present, IReadOnlyList<string> deleted, TimeSpan? limit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(present);
        ArgumentNullException.ThrowIfNull(deleted);

        await EachBatchAsync(tree, present, PublisherCommands.Stage, "stage", limit, cancellationToken).ConfigureAwait(false);
        await EachBatchAsync(tree, deleted, PublisherCommands.StageRemoval, "stage the removal of", limit, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<string> CommitAsync(string tree, string messageFile, bool signOff, TimeSpan? limit, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageFile);

        var result = await RunAsync(tree, PublisherCommands.Commit, [messageFile, .. signOff ? ["-s"] : Array.Empty<string>()], limit, "so whether it committed is not known", cancellationToken)
            .ConfigureAwait(false);

        if (!result.Succeeded)
        {
            throw new HarnessException(HarnessExit.CommandFailed, $"git did not commit: {result.FailureMessage}");
        }

        return await git.ResolveCommitAsync(tree, "HEAD", cancellationToken).ConfigureAwait(false)
            ?? throw new HarnessException(HarnessExit.CommandFailed, "git said it committed, and HEAD names no commit");
    }

    /// <inheritdoc/>
    public Task UnstageAsync(string tree, IReadOnlyList<string> paths, TimeSpan? limit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paths);

        return EachBatchAsync(tree, paths, PublisherCommands.Unstage, "unstage", limit, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<bool> PushAsync(string tree, string remote, string commit, string branch, TimeSpan? limit, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remote);
        ArgumentException.ThrowIfNullOrWhiteSpace(commit);
        ArgumentException.ThrowIfNullOrWhiteSpace(branch);

        var destination = $"refs/heads/{branch}";
        var result = await RunAsync(tree, PublisherCommands.Push, [remote, $"{commit}:{destination}"], limit, $"so whether {commit} reached {remote}'s '{branch}' is not known", cancellationToken)
            .ConfigureAwait(false);

        // One line of the porcelain for the branch: its flag, where from and to, and git's summary; '=' is a branch that
        // named the commit already, '!' one the remote refused.
        var line = result.OutputLines
            .Select(said => said.Split('\t'))
            .FirstOrDefault(fields => fields.Length >= 2 && fields[0].Length == 1 && fields[1].EndsWith($":{destination}", StringComparison.Ordinal));

        if (!result.Succeeded)
        {
            var refused = line is [_, _, var summary, ..] ? $"{summary.Trim()} - " : string.Empty;

            throw new HarnessException(HarnessExit.CommandFailed, $"git did not push {commit} to {remote}'s '{branch}': {refused}{result.FailureMessage}");
        }

        return line is null
            ? throw new HarnessException(HarnessExit.CommandFailed, $"git said it pushed {commit} to {remote}, and named no line of '{branch}' in what it pushed: {result.StandardOutput.Trim()}")
            : line[0] != "=";
    }

    /// <inheritdoc/>
    public async Task<GitPullRequest?> FindPullRequestAsync(string tree, GitPullRequestTarget target, TimeSpan? limit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);

        var listed = await gh.RunAsync(
            tree,
            [.. PublisherCommands.FindPullRequest.Leading, .. Where(target)],
            $"so whether a pull request is open from '{target.Head}' to '{target.Base}' was NOT read",
            limit,
            cancellationToken).ConfigureAwait(false);

        try
        {
            using var document = JsonDocument.Parse(listed);

            // The tool matches --head by the branch's name in any repository: a fork's branch of the same name is listed too,
            // and is none of this repository's.
            return document.RootElement.ValueKind == JsonValueKind.Array
                ? document.RootElement.EnumerateArray().Where(entry => !entry.GetProperty("isCrossRepository").GetBoolean()).Select(Read).FirstOrDefault()
                : throw new HarnessException(HarnessExit.CommandFailed, $"{GhProcess.Executable} answered no list of pull requests open from '{target.Head}': {listed.Trim()}");
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException or KeyNotFoundException)
        {
            throw new HarnessException(HarnessExit.CommandFailed, $"{GhProcess.Executable} answered something that is no list of pull requests open from '{target.Head}': {exception.Message}");
        }

        static GitPullRequest Read(JsonElement entry) => new(entry.GetProperty("number").GetInt64(), entry.GetProperty("url").GetString() ?? throw new FormatException("a pull request without its address"));
    }

    /// <inheritdoc/>
    public async Task<GitPullRequest> OpenPullRequestAsync(string tree, GitPullRequestTarget target, string title, string bodyFile, bool draft, TimeSpan? limit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(bodyFile);

        var said = await gh.RunAsync(
            tree,
            [.. PublisherCommands.OpenPullRequest.Leading, .. Where(target), "--title", title, "--body-file", bodyFile, .. draft ? ["--draft"] : Array.Empty<string>()],
            $"so no pull request was opened from '{target.Head}' to '{target.Base}'",
            limit,
            cancellationToken,
            late: $"so whether a pull request was opened from '{target.Head}' to '{target.Base}' is not known").ConfigureAwait(false);

        // The tool says the address of what it opened, on its last line.
        var address = said.Split('\n').Select(line => line.Trim()).LastOrDefault(line => line.Length > 0) ?? string.Empty;

        return PullRequestAddress().Match(address) is { Success: true } match
            ? new GitPullRequest(long.Parse(match.Groups["number"].Value, CultureInfo.InvariantCulture), address)
            : throw new HarnessException(HarnessExit.CommandFailed, $"{GhProcess.Executable} said it opened a pull request from '{target.Head}', and named no address of one: {said.Trim()}");
    }

    /// <summary>The arguments that say which pull request: the repository where the target names one, the base and the head.</summary>
    private static IReadOnlyList<string> Where(GitPullRequestTarget target)
        => [.. target.Repository is { } repository ? ["--repo", repository] : Array.Empty<string>(), "--base", target.Base, "--head", target.Head];

    /// <summary>
    /// Runs <paramref name="command"/> with <paramref name="rest"/> after its leading arguments, ended where it has not
    /// finished within <paramref name="limit"/>, else its own: one that did not finish is not done, which a caller may ask
    /// again (<see cref="HarnessExit.Incomplete"/>), saying what it may have done (<paramref name="late"/>).
    /// </summary>
    private async Task<GitCommandResult> RunAsync(string tree, PublisherCommand command, IReadOnlyList<string> rest, TimeSpan? limit, string late, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tree);

        var budget = limit ?? command.Limit;
        var result = await git.RunWithinAsync(tree, [.. command.Leading, .. rest], budget, cancellationToken).ConfigureAwait(false);

        return result.TimedOut
            ? throw new HarnessException(
                HarnessExit.Incomplete,
                $"{command.Shown} did not finish within {ReportText.Limit(budget)}, {late}: asked again, it may finish.")
            : result;
    }

    /// <summary>Runs <paramref name="command"/> over <paramref name="paths"/>, each read literally, in as many runs as one command line holds.</summary>
    private async Task EachBatchAsync(string tree, IReadOnlyList<string> paths, PublisherCommand command, string what, TimeSpan? limit, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tree);

        foreach (var batch in GitClient.CommandLineBatches(paths))
        {
            var result = await RunAsync(tree, command, batch, limit, $"so which of {ReportText.Counted(batch.Count, "path")} git got to {what} is not known", cancellationToken).ConfigureAwait(false);

            if (!result.Succeeded)
            {
                throw new HarnessException(HarnessExit.CommandFailed, $"git could not {what} {ReportText.Counted(batch.Count, "path")}: {result.FailureMessage}");
            }
        }
    }

    /// <summary>The address the forge's tool says a pull request it opened is at, its number last.</summary>
    [GeneratedRegex(@"^https?://\S+/pull/(?<number>\d+)/?$", RegexOptions.CultureInvariant)]
    private static partial Regex PullRequestAddress();
}
