using System.CommandLine;
using DssHarness.Core.Commands;
using DssHarness.Core.Git;
using DssHarness.Core.Results;

namespace DssHarness.Cli.Commands;

/// <summary>Wires <c>DssHarness verify-git</c>.</summary>
internal static class VerifyGitCommand
{
    internal const string Name = "verify-git";

    /// <summary>Builds the command.</summary>
    internal static Command Create()
    {
        var command = new Command(
            Name,
            "Check that git is installed and this directory is a git repository (0 ok, 1 no git, 2 not a repository).");

        GlobalOptions.AddTo(command);

        CommandRunner.Wire(command, async (context, cancellationToken) =>
        {
            var directory = context.Directory;

            var status = await context
                .Get<VerifyGitService>()
                .VerifyAsync(directory, cancellationToken)
                .ConfigureAwait(false);

            // The enum value is the exit code: this command's whole contract is its
            // status, so it is reported rather than translated.
            return status switch
            {
                VerifyGitStatus.Success => CommandOutcome.Ok($"git is available and '{directory}' is a repository."),
                VerifyGitStatus.GitNotInstalled => CommandOutcome.Failed(
                    (int)status, "git is not installed or not on PATH."),
                VerifyGitStatus.NotAGitRepository => CommandOutcome.Failed(
                    (int)status, $"'{directory}' is not inside a git repository."),
                _ => CommandOutcome.Failed(HarnessExit.CommandFailed, $"Unexpected status '{status}'."),
            };
        });

        return command;
    }
}
