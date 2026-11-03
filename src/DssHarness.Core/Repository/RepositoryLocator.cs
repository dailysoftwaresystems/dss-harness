using DssHarness.Core.FileSystem;
using DssHarness.Core.Git;
using DssHarness.Core.Output;
using DssHarness.Core.Platform;
using DssHarness.Core.Results;

namespace DssHarness.Core.Repository;

/// <inheritdoc cref="IRepositoryLocator"/>
/// <param name="gitClient">Asks git where the tree and its main checkout are.</param>
/// <param name="fileSystem">Looks for an agent's repository's own configuration.</param>
/// <param name="platform">How paths compare.</param>
public sealed class RepositoryLocator(IGitClient gitClient, IFileSystem fileSystem, IHostPlatform platform) : IRepositoryLocator
{
    private readonly IGitClient _gitClient = gitClient;
    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly IHostPlatform _platform = platform;

    public async Task<HarnessLayout?> LocateAsync(
        string startDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(startDirectory);

        var repositoryRoot = await _gitClient
            .GetRepositoryRootAsync(startDirectory, cancellationToken)
            .ConfigureAwait(false);

        if (repositoryRoot is null)
        {
            return null;
        }

        // Asking git for the main worktree is what makes worktree handling correct
        // without parsing the ".git" file ourselves. A worktree created on Windows
        // records a drive-lettered gitdir path that a POSIX git cannot follow, and
        // hand-rolled parsing of it is a defect this avoids entirely.
        var main = await _gitClient
            .GetMainWorktreeAsync(startDirectory, cancellationToken)
            .ConfigureAwait(false);

        // There is deliberately no fallback to the tree itself. Inside a linked
        // worktree that guess is wrong, and every path derived from it moves with it:
        // worktrees would nest, and the run lock and ssh secrets would resolve inside
        // the worktree, where two runs of the same leg could no longer see each other.
        if (main is null)
        {
            throw new HarnessException(
                HarnessExit.CommandFailed,
                $"git found a repository at '{repositoryRoot}' but names no checkout as its main worktree - "
                + "as for a worktree of one whose git directory was made apart from it, which records none - "
                + "so the main checkout cannot be located. Run it from the main checkout, or record that "
                + "checkout as the git directory's core.worktree.");
        }

        if (main.IsBare)
        {
            throw new HarnessException(
                HarnessExit.Refused,
                $"'{repositoryRoot}' is a worktree of the bare repository '{main.Path}'. "
                + "DssHarness keeps its state in the main checkout, and a bare repository has none.");
        }

        // An agent's tree is a repository of its own, which git names its own main checkout: its mark names the main
        // checkout it was made from, taken as the tree's only where that checkout still lends it its objects. One whose
        // checkout does not is refused, never taken for a main checkout: every path derived from it would be the agent's.
        if (PathContainment.AreSame(main.Path, repositoryRoot, _platform.PathComparison)
            && await AgentRepositoryMark.ReadAsync(_gitClient, _fileSystem, repositoryRoot, cancellationToken).ConfigureAwait(false) is { } mark)
        {
            if (await mark.NotLentAsync(_gitClient, _fileSystem, _platform, repositoryRoot, cancellationToken).ConfigureAwait(false) is { } why)
            {
                throw new HarnessException(
                    HarnessExit.Refused,
                    $"'{ReportText.Printable(repositoryRoot)}' is an agent's repository of '{ReportText.Printable(mark.MainCheckout)}', which {why}. "
                    + "DssHarness keeps its state in the main checkout, and so runs nothing from here; run it from a main checkout, "
                    + $"and delete '{ReportText.Printable(repositoryRoot)}' once nothing in it is needed.");
            }

            return new HarnessLayout(repositoryRoot, mark.MainCheckout);
        }

        return new HarnessLayout(repositoryRoot, main.Path);
    }
}
