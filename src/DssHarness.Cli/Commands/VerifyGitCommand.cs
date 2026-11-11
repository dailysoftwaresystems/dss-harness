using System.CommandLine;
using DssHarness.Core.Commands;

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
            var service = context.Get<VerifyGitService>();

            var status = await service
                .VerifyAsync(directory, cancellationToken)
                .ConfigureAwait(false);

            return service.Outcome(directory, status);
        });

        return command;
    }
}
