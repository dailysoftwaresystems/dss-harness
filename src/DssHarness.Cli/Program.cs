using System.CommandLine;
using DssHarness.Cli;
using DssHarness.Cli.Commands;
using DssHarness.Core.Execution;
using DssHarness.Core.Hosts;
using DssHarness.Core.Processes;
using DssHarness.Core.Repository;

// Command selection and dependency wiring only. Every behaviour lives in a service
// in DssHarness.Core, which is a library precisely so that nothing can accumulate here.
// The step that places a contained child leaves the console as it found it: on Windows the
// program it starts shares that console, and a code page set here would change how the
// program writes its output from how it writes it started as any other child.
using var consoleEncoding = args is [ContainExec.CommandName, ..] ? null : ConsoleEncoding.UseUtf8();

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
root.Subcommands.Add(ListAgentProviderCommand.Create());
root.Subcommands.Add(RunOrchestratorCommand.Create());
root.Subcommands.Add(ResumeOrchestratorCommand.Create());
root.Subcommands.Add(AnswerOrchestratorCommand.Create());
root.Subcommands.Add(PauseOrchestratorCommand.Create());
root.Subcommands.Add(StopOrchestratorCommand.Create());
root.Subcommands.Add(CreateAgentCommand.Create());
root.Subcommands.Add(SeedAgentCommand.Create());
root.Subcommands.Add(RefreshAgentCommand.Create());
root.Subcommands.Add(RebaseAgentCommand.Create());
root.Subcommands.Add(FoldAgentCommand.Create());
root.Subcommands.Add(DeleteAgentCommand.Create());
root.Subcommands.Add(ValidateWorkflowCommand.Create());
root.Subcommands.Add(ExportWorkflowCommand.Create());
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
root.Subcommands.Add(UpdateToolCommand.Create());
root.Subcommands.Add(SyncCommand.Create());
root.Subcommands.Add(BuildCommand.Create());
root.Subcommands.Add(TestCommand.Create());
root.Subcommands.Add(RunCommand.Create());
root.Subcommands.Add(CheckMutationsCommand.Create());
root.Subcommands.Add(CleanCommand.Create());
root.Subcommands.Add(UpCommand.Create());
root.Subcommands.Add(DownCommand.Create());
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

// Started by the process runner itself, as the step that places a contained child, and never typed.
root.Subcommands.Add(ContainExecCommand.Create());

return await RunAsync(args, CancellationToken.None).ConfigureAwait(false);

async Task<int> RunAsync(string[] arguments, CancellationToken cancellationToken)
{
    var parseResult = root.Parse(arguments);

    // A command line the parser refused reaches no command's action: it is answered as a usage error, with its
    // command's document where --json asked for it.
    if (parseResult.Errors.Count > 0)
    {
        return await CommandRunner.UnparsedAsync(parseResult).ConfigureAwait(false);
    }

    // A command past its point of no return goes on after Ctrl+C - a deletion, a fold, a hand-over or a
    // move of an agent's base is never left half written, a host agent can be running one for another
    // machine, and a run asked to set up brings down what it brought up - so each is waited for as long
    // as it asks. No other command is any slower to stop.
    var invocation = new InvocationConfiguration();

    if (PointOfNoReturn.GraceFor([parseResult.CommandResult.Command.Name, .. parseResult.Tokens.Select(token => token.Value)]) is { } grace)
    {
        invocation.ProcessTerminationTimeout = grace;
    }

    return await parseResult.InvokeAsync(invocation, cancellationToken).ConfigureAwait(false);
}

// A command acts on the current directory when it is given no --directory, so a host runs a request
// in its copy of the repository by starting there, without its arguments being rewritten - and with what
// the machine that asked says of it beside them: the run it is a leg of, the drive its WSL disk grows on.
Task<int> RunInAsync(string directory, string[] arguments, Dispatch dispatch, CancellationToken cancellationToken)
{
    Directory.SetCurrentDirectory(directory);
    CommandRunner.Serve(CommandOrigin.Served(dispatch));
    return RunAsync(arguments, cancellationToken);
}
