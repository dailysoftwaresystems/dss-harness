using RepoHarness.Core.Results;

namespace RepoHarness.Core.Git;

/// <summary>What a command that measures from HEAD asks of it first.</summary>
public static class GitHead
{
    /// <summary>
    /// The full id of the commit HEAD names, refused where it names none - none yet, or one git cannot read - in the one
    /// way every command measuring from HEAD refuses it.
    /// </summary>
    /// <param name="gitClient">Git.</param>
    /// <param name="directory">A directory inside the repository.</param>
    /// <param name="consequence">What a HEAD naming no commit leaves impossible, completing "so ...".</param>
    /// <param name="otherwise">What could be run instead, completing "Commit first, or ...", or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the git process.</param>
    /// <exception cref="HarnessException">
    /// HEAD names no commit (<see cref="HarnessExit.Refused"/>), or git could not answer at all.
    /// </exception>
    public static async Task<string> RequireHeadAsync(
        this IGitClient gitClient,
        string directory,
        string consequence,
        string? otherwise = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(gitClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(consequence);

        return await gitClient.ResolveCommitAsync(directory, "HEAD", cancellationToken).ConfigureAwait(false)
            ?? throw new HarnessException(
                HarnessExit.Refused,
                $"HEAD names no commit yet - or one git cannot read, which git fsck reports - so {consequence}. "
                + $"Commit first{(otherwise is null ? string.Empty : ", or " + otherwise)}.");
    }
}
