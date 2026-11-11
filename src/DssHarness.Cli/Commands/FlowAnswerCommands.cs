using System.CommandLine;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Orchestration.Flow;

namespace DssHarness.Cli.Commands;

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
        Description = "Change the last answer a person gave the run - the one set the command shows - within the minutes its workflow names and once; where the run has begun the scope its plan updates are planned in, the change raises the workflow's planning step and the run is held while it plans, or waits behind an update under way.",
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

/// <summary>Wires <c>DssHarness amend-orchestrator-request</c>.</summary>
internal static class AmendOrchestratorRequestCommand
{
    internal const string Name = FlowService.AmendCommand;

    private static readonly Argument<string> RunArgument = OrchestrationArguments.Orchestrator("The run whose request is amended.");

    private static readonly Argument<string?> TextArgument = new("text")
    {
        Description = "The amendment, in the person's words: it adds to the request, corrects it or narrows it. The request as it was given is never rewritten - the amendment is read with it.",
        Arity = ArgumentArity.ZeroOrOne,
    };

    private static readonly Option<string?> FileOption = new("--file")
    {
        HelpName = "path",
        Description = "A file holding the amendment, read relative to --directory, in the place of the text argument.",
    };

    internal static Command Create()
    {
        var command = new Command(Name, "Amend the request a run was started with: handed to the engine where one is running the run, else recorded as a plan update that the next session takes first. Starts nothing.");
        command.Arguments.Add(RunArgument);
        command.Arguments.Add(TextArgument);
        command.Options.Add(FileOption);
        command.Options.Add(OrchestrationArguments.Json);
        GlobalOptions.AddTo(command);

        CommandRunner.Wire(
            command,
            (context, cancellationToken) => context.Get<IFlowService>()
                .AmendAsync(context.Directory, context.ParseResult.GetRequiredValue(RunArgument), context.ParseResult.GetValue(TextArgument), context.ParseResult.GetValue(FileOption), context.Json, cancellationToken),
            FlowCommands.Answer);

        return command;
    }
}
