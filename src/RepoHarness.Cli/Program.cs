using System.CommandLine;
using RepoHarness.Cli;
using RepoHarness.Cli.Commands;
using RepoHarness.Core.Execution;
using RepoHarness.Core.Hosts;
using RepoHarness.Core.Results;
using RepoHarness.Core.Worktrees;

// Command selection and dependency wiring only. Every behaviour lives in a service
// in RepoHarness.Core, which is a library precisely so that nothing can accumulate here.
using var consoleEncoding = ConsoleEncoding.UseUtf8();

var root = new RootCommand(
    $"{ToolPackage.Id} - cross-platform repository harness. Run '{ToolPackage.Command} help' for reference material.");

root.Subcommands.Add(InitCommand.Create());
root.Subcommands.Add(VerifyGitCommand.Create());
root.Subcommands.Add(CreateWorktreeCommand.Create());
root.Subcommands.Add(DeleteWorktreeCommand.Create());
root.Subcommands.Add(ListWorktreeCommand.Create());
root.Subcommands.Add(CreateOrchestratorCommand.Create());
root.Subcommands.Add(DeleteOrchestratorCommand.Create());
root.Subcommands.Add(ListOrchestratorCommand.Create());
root.Subcommands.Add(CreateAgentCommand.Create());
root.Subcommands.Add(SeedAgentCommand.Create());
root.Subcommands.Add(RefreshAgentCommand.Create());
root.Subcommands.Add(RebaseAgentCommand.Create());
root.Subcommands.Add(FoldAgentCommand.Create());
root.Subcommands.Add(DeleteAgentCommand.Create());
root.Subcommands.Add(WriteAnchorCommand.Create());
root.Subcommands.Add(SetAnchorCommand.Create());
root.Subcommands.Add(ReadAnchorCommand.Create());
root.Subcommands.Add(ReadAnchorsCommand.Create());
root.Subcommands.Add(CheckAnchorBalanceCommand.Create());
root.Subcommands.Add(CheckAnchorCitationsCommand.Create());
root.Subcommands.Add(CheckRootLitterCommand.Create());
root.Subcommands.Add(FixLineEndingsCommand.Create());
root.Subcommands.Add(CheckCiLegsCommand.Create());
root.Subcommands.Add(LegsCommand.Create());
root.Subcommands.Add(InstallMissingToolsCommand.Create());
root.Subcommands.Add(SyncCommand.Create());
root.Subcommands.Add(BuildCommand.Create());
root.Subcommands.Add(TestCommand.Create());
root.Subcommands.Add(RunCommand.Create());
root.Subcommands.Add(CheckMutationsCommand.Create());
root.Subcommands.Add(CleanCommand.Create());
root.Subcommands.Add(HostExecCommand.Create());
root.Subcommands.Add(HelpCommand.Create());

// Served on a host for the machine syncing to it, and never typed: every operation it performs is
// one the machine asking already decided on.
root.Subcommands.Add(SyncServeCommand.Create());

// Served on a host, for the DssHarness on the machine that reaches it. A run request goes back
// through this same parser, in the host's copy of the repository, which is why it is wired here.
root.Subcommands.Add(HostAgentCommand.Create(RunInAsync));

// Started by a host agent asked to hold its machine awake between commands, detached, and never typed.
root.Subcommands.Add(HostHoldCommand.Create());

return await RunAsync(args, CancellationToken.None).ConfigureAwait(false);

async Task<int> RunAsync(string[] arguments, CancellationToken cancellationToken)
{
    var parseResult = root.Parse(arguments);

    // System.CommandLine reports a parse failure as exit code 1, which collides with
    // verify-git's contract, where 1 means "git is not installed". Usage errors are
    // reported with the documented shared code instead.
    if (parseResult.Errors.Count > 0)
    {
        foreach (var error in parseResult.Errors)
        {
            Console.Error.WriteLine(error.Message);
        }

        Console.Error.WriteLine($"Run '{ToolPackage.Command} --help' for usage.");
        return HarnessExit.UsageError;
    }

    // A command past its point of no return goes on after Ctrl+C - a deletion, a fold, a hand-over or a
    // move of an agent's base is never left half written, and a host agent can be running one for another
    // machine - so each is waited for as long as a deletion expects. No other command is any slower to stop.
    var invocation = new InvocationConfiguration();

    if (PointOfNoReturn.Commands.Contains(parseResult.CommandResult.Command.Name))
    {
        invocation.ProcessTerminationTimeout = WorktreeService.DefaultInterruptionGrace;
    }

    return await parseResult.InvokeAsync(invocation, cancellationToken).ConfigureAwait(false);
}

// A command acts on the current directory when it is given no --directory, so a host runs a request
// in its copy of the repository by starting there, without its arguments being rewritten - and with what
// the machine that asked says of it beside them: the run it is a leg of, the drive its WSL disk grows on.
Task<int> RunInAsync(string directory, string[] arguments, Dispatch dispatch, CancellationToken cancellationToken)
{
    Directory.SetCurrentDirectory(directory);
    CommandRunner.ServeDispatch(dispatch);
    return RunAsync(arguments, cancellationToken);
}
