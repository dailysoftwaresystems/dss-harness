using System.CommandLine;
using DssHarness.Core.Orchestration.Flow;

namespace DssHarness.Cli.Commands;

/// <summary>Wires <c>DssHarness validate-workflow</c>.</summary>
internal static class ValidateWorkflowCommand
{
    internal const string Name = WorkflowValidationService.CommandName;

    private static readonly Argument<string?> WorkflowArgument = new("workflow")
    {
        Description = "The workflow to check, as workflows in config.json declares it. Omit for every declared one.",
        Arity = ArgumentArity.ZeroOrOne,
    };

    private static readonly Option<bool> JsonOption = new("--json")
    {
        Description = "Write the findings as JSON.",
    };

    internal static Command Create()
    {
        var command = new Command(
            Name,
            "Check the workflows config.json declares: that each definition file reads, holds, and names files that are there. Nothing is run.");

        command.Arguments.Add(WorkflowArgument);
        command.Options.Add(JsonOption);
        GlobalOptions.AddTo(command);

        CommandRunner.Wire(command, async (context, cancellationToken) =>
        {
            var report = await context.Get<IWorkflowValidationService>()
                .CheckAsync(context.Directory, context.ParseResult.GetValue(WorkflowArgument), cancellationToken)
                .ConfigureAwait(false);

            return WorkflowValidationReports.Render(report, context.Json);
        }, new JsonAnswer(JsonOption, (flags, exitCode, summary, _, _) => WorkflowValidationReports.Stopped(exitCode, summary)));

        return command;
    }
}
