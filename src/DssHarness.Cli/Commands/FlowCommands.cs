using System.CommandLine;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Orchestration.Flow;

namespace DssHarness.Cli.Commands;

/// <summary>
/// Wires the five run verbs: <c>run-orchestrator</c>, <c>resume-orchestrator</c>, <c>answer-orchestrator</c>,
/// <c>pause-orchestrator</c> and <c>stop-orchestrator</c>. The three sessions count two interrupts: the first asks
/// the engine for a safe pause, the second stops it where it stands.
/// </summary>
internal static class FlowCommands
{
    /// <summary>The flow document each of the five verbs answers with where it stops before its session: the session it would have run, empty.</summary>
    internal static readonly JsonAnswer Answer = new(OrchestrationArguments.Json, (_, exitCode, summary, _, _) => OrchestrationReports.FlowStopped(exitCode, summary));

    /// <summary>Counts the interrupts of a session the terminal delivers: Ctrl+C here, SIGTERM and a closed terminal on Linux and macOS.</summary>
    /// <param name="interrupts">The session's interrupts.</param>
    /// <returns>The handler, and whether to keep the process running past the press.</returns>
    internal static (ConsoleCancelEventHandler Handler, Action Detach) InterruptsOf(FlowInterrupts interrupts)
    {
        ConsoleCancelEventHandler handler = (_, pressed) =>
        {
            pressed.Cancel = true;
            interrupts.Pressed();
        };

        Console.CancelKeyPress += handler;

        return (handler, () => Console.CancelKeyPress -= handler);
    }
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
        Description = "The run's name, which names its directory under .orchestrators; the first free r<N> where none is given. A name that is taken is refused, never reused.",
    };

    private static readonly Option<string[]> InputOption = new("--input")
    {
        HelpName = "k=v",
        Description = "An input the run is started with, as name=value; @<file> gives a text input a file's text. Once for each input.",
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
            async (context, run, cancellationToken) =>
            {
                var interrupts = new FlowInterrupts();
                var (handler, detach) = FlowCommands.InterruptsOf(interrupts);

                try
                {
                    return await context.Get<IFlowService>()
                        .RunAsync(
                            context.Directory,
                            context.ParseResult.GetRequiredValue(DefinitionArgument),
                            context.ParseResult.GetValue(RunOption),
                            Inputs(context.ParseResult.GetValue(InputOption)),
                            context.Json,
                            interrupts,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                finally
                {
                    detach();
                    _ = handler;
                }
            },
            beginsRun: run => true, FlowCommands.Answer);

        return command;
    }

    /// <summary>The inputs the command line gave, by name; <c>@file</c> reads the file's text.</summary>
    internal static IReadOnlyDictionary<string, string>? Inputs(string[]? given)
    {
        if (given is null || given.Length == 0)
        {
            return null;
        }

        var inputs = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var input in given)
        {
            var at = input.IndexOf('=', StringComparison.Ordinal);

            if (at <= 0 || at == input.Length - 1)
            {
                throw new DssHarness.Core.Results.HarnessException(
                    DssHarness.Core.Results.HarnessExit.UsageError,
                    $"'{input}' is not an input as name=value; --input k=v gives one, and @<file> a text input a file's text");
            }

            var name = input[..at];
            var value = input[(at + 1)..];

            if (value.Length > 1 && value[0] == '@')
            {
                value = File.ReadAllText(value[1..]);
            }

            inputs[name] = value;
        }

        return inputs;
    }
}

/// <summary>Wires <c>DssHarness resume-orchestrator</c>.</summary>
internal static class ResumeOrchestratorCommand
{
    internal const string Name = FlowService.ResumeCommand;

    private static readonly Argument<string> RunArgument = OrchestrationArguments.Orchestrator("The run to go on with: everything that can run, beside any question still open.");

    internal static Command Create()
    {
        var command = new Command(Name, "Go on with a run: what a stop, a crash or an answer left, each stopped step from its own handoff, refused while another run of the repository is unfinished with work in the tree.");
        command.Arguments.Add(RunArgument);
        command.Options.Add(OrchestrationArguments.Json);
        GlobalOptions.AddTo(command);

        CommandRunner.Wire(
            command,
            async (context, run, cancellationToken) =>
            {
                var interrupts = new FlowInterrupts();
                var (_, detach) = FlowCommands.InterruptsOf(interrupts);

                try
                {
                    return await context.Get<IFlowService>()
                        .ResumeAsync(context.Directory, context.ParseResult.GetRequiredValue(RunArgument), context.Json, interrupts, cancellationToken)
                        .ConfigureAwait(false);
                }
                finally
                {
                    detach();
                }
            },
            beginsRun: run => true, FlowCommands.Answer);

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
        Description = "Change the last answer a person gave, within the window the configuration sets.",
    };

    private static readonly Option<string?> TextOption = new("--text")
    {
        Description = "The text an answer that takes one is given.",
    };

    private static readonly Option<string?> TextFileOption = new("--text-file")
    {
        HelpName = "path",
        Description = "A file holding the text an answer that takes one is given.",
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
            async (context, run, cancellationToken) =>
            {
                var text = context.ParseResult.GetValue(TextOption);
                var file = context.ParseResult.GetValue(TextFileOption);

                if (text is not null && file is not null)
                {
                    return DssHarness.Core.Results.CommandOutcome.Usage("--text and --text-file are one answer's text given two ways; give one");
                }

                if (file is not null)
                {
                    text = File.ReadAllText(file);
                }

                var interrupts = new FlowInterrupts();
                var (_, detach) = FlowCommands.InterruptsOf(interrupts);

                try
                {
                    return await context.Get<IFlowService>()
                        .AnswerAsync(
                            context.Directory,
                            context.ParseResult.GetRequiredValue(RunArgument),
                            context.ParseResult.GetRequiredValue(AnswerArgument),
                            context.ParseResult.GetValue(QuestionOption),
                            text,
                            context.ParseResult.GetValue(AgainOption),
                            context.Json,
                            interrupts,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                finally
                {
                    detach();
                }
            },
            beginsRun: run => true, FlowCommands.Answer);

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
        Description = "End the engine's process and every child its steps recorded, without asking it: for an engine that no longer answers.",
    };

    internal static Command Create()
    {
        var command = new Command(Name, "The hard stop: once a pause is complete, the stop of the machine steps it left running; --now ends the engine's process where it stands.");
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
