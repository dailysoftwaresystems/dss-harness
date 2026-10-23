using DssHarness.Core.Results;

namespace DssHarness.Core.Git;

/// <summary>
/// The branch a remote names its default, as one read of it: what the remote's own HEAD names where it names a branch
/// here, else the first of its conventional branches that resolves.
/// </summary>
/// <param name="Reference">The default branch, under the remote (<c>origin/main</c>); null where none resolves here.</param>
/// <param name="Named">What the remote's own HEAD names, where it names a branch at all - here or not; null where it names none.</param>
public sealed record GitDefaultBranch(string? Reference, string? Named);

/// <summary>What a repository's remotes say: their names, the one a branch tracks, and the branch each names its default.</summary>
public static class GitRemotes
{
    /// <summary>The branch names tried, in order, where a remote's own HEAD names no branch here.</summary>
    public static IReadOnlyList<string> ConventionalBranches { get; } = ["main", "master"];

    /// <summary>The remote looked for where nothing names one: the one git's own clone names.</summary>
    public const string ConventionalRemote = "origin";

    /// <summary>The names of the repository's remotes, as git lists them.</summary>
    /// <param name="gitClient">Git.</param>
    /// <param name="directory">A directory inside the repository.</param>
    /// <param name="cancellationToken">Cancels the git process.</param>
    /// <exception cref="HarnessException">Git could not list them (<see cref="HarnessExit.CommandFailed"/>).</exception>
    public static async Task<IReadOnlyList<string>> ListRemotesAsync(this IGitClient gitClient, string directory, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(gitClient);

        var listed = await gitClient.RunAsync(directory, ["remote"], cancellationToken: cancellationToken).ConfigureAwait(false);

        return listed.Succeeded
            ? listed.OutputLines
            : throw new HarnessException(HarnessExit.CommandFailed, $"git could not list the repository's remotes: {listed.StandardError.Trim()}");
    }

    /// <summary>
    /// The remote <paramref name="branch"/> tracks, or <see langword="null"/> where it tracks none - where git says the
    /// key is not set (its code 1), and only there: a configuration git could not read is never read as tracking none.
    /// </summary>
    /// <param name="gitClient">Git.</param>
    /// <param name="directory">A directory inside the repository.</param>
    /// <param name="branch">The local branch.</param>
    /// <param name="cancellationToken">Cancels the git process.</param>
    /// <exception cref="HarnessException">Git could not read the configuration (<see cref="HarnessExit.CommandFailed"/>).</exception>
    public static async Task<string?> TrackedRemoteAsync(this IGitClient gitClient, string directory, string branch, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(gitClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(branch);

        var tracked = await gitClient.RunAsync(directory, ["config", "--get", $"branch.{branch}.remote"], cancellationToken: cancellationToken).ConfigureAwait(false);

        if (!tracked.Succeeded && (tracked.TimedOut || tracked.ExitCode != 1))
        {
            throw new HarnessException(HarnessExit.CommandFailed, $"git could not read the remote branch '{branch}' tracks: {tracked.StandardError.Trim()}");
        }

        var remote = tracked.Succeeded ? tracked.StandardOutput.Trim() : string.Empty;

        return remote.Length > 0 ? remote : null;
    }

    /// <summary>
    /// The branch <paramref name="remote"/> names its default: what its own HEAD names, where that is a branch here, and
    /// failing that its <see cref="ConventionalBranches"/>, each tried until one resolves. A remote's HEAD still naming a
    /// branch since renamed - as after master became main - names none here, and is said in <see cref="GitDefaultBranch.Named"/>.
    /// </summary>
    /// <param name="gitClient">Git.</param>
    /// <param name="directory">A directory inside the repository.</param>
    /// <param name="remote">The remote's name.</param>
    /// <param name="cancellationToken">Cancels the git process.</param>
    public static async Task<GitDefaultBranch> DefaultBranchAsync(this IGitClient gitClient, string directory, string remote, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(gitClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(remote);

        var declared = await gitClient
            .RunAsync(directory, ["symbolic-ref", "--short", $"refs/remotes/{remote}/HEAD"], cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var named = declared.Succeeded ? declared.StandardOutput.Trim() : string.Empty;

        if (named.Length > 0 && await gitClient.ResolveCommitAsync(directory, named, cancellationToken).ConfigureAwait(false) is not null)
        {
            return new GitDefaultBranch(named, named);
        }

        foreach (var conventional in ConventionalBranches)
        {
            var candidate = $"{remote}/{conventional}";

            if (await gitClient.ResolveCommitAsync(directory, candidate, cancellationToken).ConfigureAwait(false) is not null)
            {
                return new GitDefaultBranch(candidate, named.Length > 0 ? named : null);
            }
        }

        return new GitDefaultBranch(null, named.Length > 0 ? named : null);
    }
}
