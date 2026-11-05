using DssHarness.Core.Git;
using DssHarness.Core.Results;

namespace DssHarness.Core.Commands;

/// <summary>
/// Answers whether this machine and this directory can be worked with at all:
/// git installed, and a repository present.
/// </summary>
public sealed class VerifyGitService(IGitClient gitClient) : DssHarness.Core.Application.IUseCase
{
    private readonly IGitClient _gitClient = gitClient;

    /// <summary>
    /// Checks <paramref name="directory"/>. The two failure values are distinct
    /// because their remedies are: install git, versus go somewhere else.
    /// </summary>
    public async Task<VerifyGitStatus> VerifyAsync(
        string directory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        if (!_gitClient.IsInstalled())
        {
            return VerifyGitStatus.GitNotInstalled;
        }

        var isRepository = await _gitClient
            .IsRepositoryAsync(directory, cancellationToken)
            .ConfigureAwait(false);

        return isRepository ? VerifyGitStatus.Success : VerifyGitStatus.NotAGitRepository;
    }

    /// <summary>
    /// The status as the command answers it. The enum value is the exit code: this command's whole
    /// contract is its status, so it is reported rather than translated.
    /// </summary>
    public CommandOutcome Outcome(string directory, VerifyGitStatus status) => status switch
    {
        VerifyGitStatus.Success => CommandOutcome.Ok($"git is available and '{directory}' is a repository."),
        VerifyGitStatus.GitNotInstalled => CommandOutcome.Failed(
            (int)status, "git is not installed or not on PATH."),
        VerifyGitStatus.NotAGitRepository => CommandOutcome.Failed(
            (int)status, $"'{directory}' is not inside a git repository."),
        _ => CommandOutcome.Failed(HarnessExit.CommandFailed, $"Unexpected status '{status}'."),
    };
}
