using System.CommandLine;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Orchestration.Flow;
using DssHarness.Core.Output;
using DssHarness.Core.Results;

namespace DssHarness.Cli.Commands;

/// <summary>
/// Wires the five run verbs: <c>run-orchestrator</c>, <c>resume-orchestrator</c>, <c>answer-orchestrator</c>,
/// <c>pause-orchestrator</c> and <c>stop-orchestrator</c>. The three sessions count two interrupts through their
/// terminal (<see cref="FlowTerminal"/>): the first asks the engine for a safe pause, the second stops it where it stands.
/// </summary>
internal static class FlowCommands
{
    /// <summary>The flow document each of the five verbs answers with where it stops before its session: the session it would have run, empty.</summary>
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
        var command = new Command(Name, "Go on with a run: what a stop, a crash or an answer left, each stopped step from its own handoff, under the definition it began with; refused while another run of the repository is unfinished with work in the tree.");
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

/// <summary>Wires <c>DssHarness answer-orchestrator</c>.</summary>
internal static class AnswerOrchestratorCommand
{
    internal const string Name = FlowService.AnswerCommand;

    private static readonly Argument<string> RunArgument = OrchestrationArguments.Orchestrator("The run whose question is answered.");

    private static readonly Argument<string> AnswerArgument = new("answer")
    {
        Description = "The answer, spelled as the question's options spell them.",
    };

    private static readonly Option<string?> QuestionOption = new("--question")
    {
        HelpName = "ID",
        Description = "The id of the question the answer answers - left out, the one the run has open, which the command shows; with several open, leaving it out is a usage error that lists them.",
    };

    private static readonly Option<bool> AgainOption = new("--again")
    {
        Description = "Change an answer already given. This build takes no plan update yet, so it is refused.",
    };

    private static readonly Option<string?> TextOption = new("--text")
    {
        Description = "The text an answer that takes one is given.",
    };

    private static readonly Option<string?> TextFileOption = new("--text-file")
    {
        HelpName = "path",
        Description = "A file holding the text an answer that takes one is given, read relative to --directory.",
    };

    internal static Command Create()
    {
        var command = new Command(Name, "Answer a question of a run and go on with it; given to the engine where one is running the run, which takes it while the question is open.");
        command.Arguments.Add(RunArgument);
        command.Arguments.Add(AnswerArgument);
        command.Options.Add(QuestionOption);
        command.Options.Add(AgainOption);
        command.Options.Add(TextOption);
        command.Options.Add(TextFileOption);
        command.Options.Add(OrchestrationArguments.Json);
        GlobalOptions.AddTo(command);

        CommandRunner.Wire(
            command,
            (context, run, cancellationToken) => FlowCommands.SessionAsync(
                context,
                Name,
                (interrupts, token) => context.Get<IFlowService>().AnswerAsync(
                    context.Directory,
                    context.ParseResult.GetRequiredValue(RunArgument),
                    context.ParseResult.GetRequiredValue(AnswerArgument),
                    context.ParseResult.GetValue(QuestionOption),
                    context.ParseResult.GetValue(TextOption),
                    context.ParseResult.GetValue(TextFileOption),
                    context.ParseResult.GetValue(AgainOption),
                    context.Json,
                    interrupts,
                    token),
                cancellationToken),
            beginsRun: run => true,
            FlowCommands.Answer);

        return command;
    }
}

/// <summary>Wires <c>DssHarness pause-orchestrator</c>.</summary>
internal static class PauseOrchestratorCommand
{
    internal const string Name = FlowService.PauseCommand;

    private static readonly Argument<string> RunArgument = OrchestrationArguments.Orchestrator("The run to pause.");

    internal static readonly Option<bool> AllOption = new("--all")
    {
        Description = "Stop the machine steps in progress too. Without it they finish: the run is paused as soon as its agents have handed off, and the engine's process stays only until it has recorded them. A build directory is kept either way.",
    };

    internal static Command Create()
    {
        var command = new Command(Name, "The safe pause, asked of the engine running the run: it waits until the pause is complete, names each handoff and each machine step still in progress, and exits 0.");
        command.Arguments.Add(RunArgument);
        command.Options.Add(AllOption);
        command.Options.Add(OrchestrationArguments.Json);
        GlobalOptions.AddTo(command);

        CommandRunner.Wire(
            command,
            (context, cancellationToken) => context.Get<IFlowService>()
                .PauseAsync(context.Directory, context.ParseResult.GetRequiredValue(RunArgument), context.ParseResult.GetValue(AllOption), context.Json, cancellationToken),
            FlowCommands.Answer);

        return command;
    }
}

/// <summary>Wires <c>DssHarness stop-orchestrator</c>.</summary>
internal static class StopOrchestratorCommand
{
    internal const string Name = FlowService.StopCommand;

    private static readonly Argument<string> RunArgument = OrchestrationArguments.Orchestrator("The run to stop.");

    internal static readonly Option<bool> NowOption = new("--now")
    {
        Description = "End the engine's process and all it started, without asking it - only an engine of this machine, and only the process its owner names by its start: for an engine that no longer answers. What its steps' contained children left is ended with it.",
    };

    internal static Command Create()
    {
        var command = new Command(Name, "The hard stop, asked of the engine running the run and waited for 30 s: what is in flight is stopped where it stands; once a pause is complete, the machine steps it left running. --now ends the engine's process where it stands.");
        command.Arguments.Add(RunArgument);
        command.Options.Add(NowOption);
        command.Options.Add(OrchestrationArguments.Json);
        GlobalOptions.AddTo(command);

        CommandRunner.Wire(
            command,
            (context, cancellationToken) => context.Get<IFlowService>()
                .StopAsync(context.Directory, context.ParseResult.GetRequiredValue(RunArgument), context.ParseResult.GetValue(NowOption), context.Json, cancellationToken),
            FlowCommands.Answer);

        return command;
    }
}
