using System.CommandLine;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Orchestration.Flow;
using DssHarness.Core.Output;
using DssHarness.Core.Results;

namespace DssHarness.Cli.Commands;

/// <summary>
/// What the verbs that work a run share. The three that drive a session of it - <c>run-orchestrator</c>,
/// <c>resume-orchestrator</c> and <c>answer-orchestrator</c> - run it through <see cref="SessionAsync"/>, which counts
/// two interrupts through their terminal (<see cref="FlowTerminal"/>): the first asks the engine for a safe pause, the
/// second stops it where it stands. Those three, <c>amend-orchestrator-request</c>, <c>pause-orchestrator</c> and
/// <c>stop-orchestrator</c> answer with <see cref="Answer"/> where they stop before their work.
/// </summary>
internal static class FlowCommands
{
    /// <summary>The flow document each of the six verbs that work a run answers with where it stops before its work: the session it would have run, empty.</summary>
    internal static readonly JsonAnswer Answer = new(OrchestrationArguments.Json, (_, exitCode, summary, _, _) => OrchestrationReports.FlowStopped(exitCode, summary));

    /// <summary>
    /// Runs one session of a run with the terminal's interrupts counted for it - Ctrl+C, and off Windows SIGTERM and a
    /// hang-up - and the command line's own token kept from it: an interrupt reaches the session as one counted, never
    /// as a cancellation.
    /// </summary>
    /// <param name="context">The command's context.</param>
    /// <param name="command">The run verb.</param>
    /// <param name="session">The session, given its interrupts and the token it is driven by.</param>
    /// <param name="commandToken">The command line's own token, which the session is not given.</param>
    /// <returns>The session's outcome, or the hard stop's where the session did not end within its drain.</returns>
    internal static Task<CommandOutcome> SessionAsync(
        CommandContext context,
        string command,
        Func<FlowInterrupts, CancellationToken, Task<CommandOutcome>> session,
        CancellationToken commandToken)
        => new FlowTerminal(context.Get<IHarnessOutput>(), command).RunAsync(session, commandToken);

    /// <summary>
    /// The <c>--json</c> answer of the two ask commands: the same document wherever they stop, the facts and the answer
    /// apart.
    /// </summary>
    internal static readonly JsonAnswer AskAnswer = new(OrchestrationArguments.Json, (_, exitCode, summary, _, _) => FlowReports.AskStopped(exitCode, summary));
}

/// <summary>Wires <c>DssHarness run-orchestrator</c>.</summary>
internal static class RunOrchestratorCommand
{
    internal const string Name = FlowService.RunCommand;

    private static readonly Argument<string> DefinitionArgument = new("definition")
    {
        Description = "The workflow to run, as it is declared in the repository's config.",
    };

    private static readonly Option<string?> RunOption = new("--run")
    {
        Description = "The run's name, which names its directory under .orchestrators; one more than the highest r<N> there where none is given. A name that is taken is refused, never taken over.",
    };

    private static readonly Option<string[]> InputOption = new("--input")
    {
        HelpName = "k=v",
        Description = "An input the run is started with, as name=value; name=@<file> gives a text input the text of a file, read relative to --directory. Once for each input.",
    };

    internal static Command Create()
    {
        var command = new Command(Name, "Start a run of a workflow: validates it, makes the run's record and claims it for this engine, and drives it until nothing more can run - done, failed, paused on a question, or stopped on request.");
        command.Arguments.Add(DefinitionArgument);
        command.Options.Add(RunOption);
        command.Options.Add(InputOption);
        command.Options.Add(OrchestrationArguments.Json);
        GlobalOptions.AddTo(command);

        CommandRunner.Wire(
            command,
            (context, run, cancellationToken) => FlowCommands.SessionAsync(
                context,
                Name,
                (interrupts, token) => context.Get<IFlowService>().RunAsync(
                    context.Directory,
                    context.ParseResult.GetRequiredValue(DefinitionArgument),
                    context.ParseResult.GetValue(RunOption),
                    context.ParseResult.GetValue(InputOption),
                    context.Json,
                    interrupts,
                    token),
                cancellationToken),
            beginsRun: run => true,
            FlowCommands.Answer);

        return command;
    }
}

/// <summary>Wires <c>DssHarness resume-orchestrator</c>.</summary>
internal static class ResumeOrchestratorCommand
{
    internal const string Name = FlowService.ResumeCommand;

    private static readonly Argument<string> RunArgument = OrchestrationArguments.Orchestrator("The run to go on with: everything that can run, beside any question still open.");

    internal static Command Create()
    {
        var command = new Command(Name, "Go on with a run from its records alone, where it stood: what a stop, a crash or an answer left - each stopped step from its own handoff, a failure that ended the run run again, what the run owed made first - under the definition it began with; refused while another run of the repository is unfinished with work in the tree.");
        command.Arguments.Add(RunArgument);
        command.Options.Add(OrchestrationArguments.Json);
        GlobalOptions.AddTo(command);

        CommandRunner.Wire(
            command,
            (context, run, cancellationToken) => FlowCommands.SessionAsync(
                context,
                Name,
                (interrupts, token) => context.Get<IFlowService>().ResumeAsync(context.Directory, context.ParseResult.GetRequiredValue(RunArgument), context.Json, interrupts, token),
                cancellationToken),
            beginsRun: run => true,
            FlowCommands.Answer);

        return command;
    }
}
