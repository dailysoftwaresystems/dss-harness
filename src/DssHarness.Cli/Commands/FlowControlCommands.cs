using System.CommandLine;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Orchestration.Flow;

namespace DssHarness.Cli.Commands;

/// <summary>Wires <c>DssHarness pause-orchestrator</c>.</summary>
internal static class PauseOrchestratorCommand
{
    internal const string Name = FlowService.PauseCommand;

    private static readonly Argument<string> RunArgument = OrchestrationArguments.Orchestrator("The run to pause.");

    private static readonly Option<bool> AllOption = new("--all")
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

    private static readonly Option<bool> NowOption = new("--now")
    {
        Description = "End the engine's process and all it started, without asking it - only an engine of this machine, and only the process its owner names by its start: for an engine that no longer answers. What its steps left running - a contained child's session, the process group of what its legs started - is ended with it.",
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

/// <summary>Wires <c>DssHarness ask-orchestrator-status</c>.</summary>
internal static class AskOrchestratorStatusCommand
{
    internal const string Name = FlowService.AskStatusCommand;

    private static readonly Argument<string> RunArgument = OrchestrationArguments.Orchestrator("The run to ask about.");

    internal static Command Create()
    {
        var command = new Command(Name, "The tool's own question of a run - what is happening now, what is done, what is left, what it waits for, and when it may finish - answered with the engine's facts and, where the workflow names a reporter, its words. Reads only: nothing of the run is written or locked but the command's own record under the run's asks/, and what the answer costs is the person's, never the run's. Where no model words the facts, they are still the answer, and the command says why no model did.");
        command.Arguments.Add(RunArgument);
        command.Options.Add(OrchestrationArguments.Json);
        GlobalOptions.AddTo(command);

        CommandRunner.Wire(
            command,
            (context, cancellationToken) => context.Get<IFlowService>()
                .AskStatusAsync(context.Directory, context.ParseResult.GetRequiredValue(RunArgument), context.Json, cancellationToken),
            FlowCommands.AskAnswer);

        return command;
    }
}

/// <summary>Wires <c>DssHarness ask-orchestrator</c>.</summary>
internal static class AskOrchestratorCommand
{
    internal const string Name = FlowService.AskCommand;

    private static readonly Argument<string> RunArgument = OrchestrationArguments.Orchestrator("The run to ask about.");

    private static readonly Argument<string> QuestionArgument = new("question")
    {
        Description = "The question, in the person's words, answered from the run's records by its reporter.",
    };

    internal static Command Create()
    {
        var command = new Command(Name, "A person's question about a run, answered from its records by the workflow's reporter over the engine's facts. Reads only, as ask-orchestrator-status does; where no model can answer - no reporter named, no key, no Claude Code, a limit, a failure - the command is refused, naming why.");
        command.Arguments.Add(RunArgument);
        command.Arguments.Add(QuestionArgument);
        command.Options.Add(OrchestrationArguments.Json);
        GlobalOptions.AddTo(command);

        CommandRunner.Wire(
            command,
            (context, cancellationToken) => context.Get<IFlowService>()
                .AskAsync(context.Directory, context.ParseResult.GetRequiredValue(RunArgument), context.ParseResult.GetRequiredValue(QuestionArgument), context.Json, cancellationToken),
            FlowCommands.AskAnswer);

        return command;
    }
}
