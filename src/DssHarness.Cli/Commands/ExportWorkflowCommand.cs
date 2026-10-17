using System.CommandLine;
using DssHarness.Core.Orchestration.Flow;

namespace DssHarness.Cli.Commands;

/// <summary>Wires <c>DssHarness export-workflow</c>.</summary>
internal static class ExportWorkflowCommand
{
    internal const string Name = WorkflowExportService.CommandName;

    private static readonly Argument<string> WorkflowArgument = new("workflow")
    {
        Description = "The workflow to export, as workflows in config.json declares it; with --shipped, the one this tool ships.",
    };

    private static readonly Option<string> ToOption = new("--to")
    {
        Description = "The folder to export into; the workflow is written into a folder of its own under it, named as the workflow, which must not be there.",
        Required = true,
    };

    private static readonly Option<bool> ShippedOption = new("--shipped")
    {
        Description = $"Export the workflow this tool ships ({WorkflowTemplates.ShippedWorkflowName}), to make a version of your own.",
    };

    private static readonly Option<bool> JsonOption = new("--json")
    {
        Description = "Write where the workflow went and each file as JSON.",
    };

    internal static Command Create()
    {
        var command = new Command(
            Name,
            "Copy a workflow - its yml and every file it names - into a folder of its own: a declared one once it holds, or the shipped one with --shipped.");

        command.Arguments.Add(WorkflowArgument);
        command.Options.Add(ToOption);
        command.Options.Add(ShippedOption);
        command.Options.Add(JsonOption);
        GlobalOptions.AddTo(command);

        CommandRunner.Wire(command, async (context, cancellationToken) =>
        {
            var arguments = context.ParseResult;
            var request = new WorkflowExportRequest(arguments.GetValue(WorkflowArgument)!, arguments.GetValue(ToOption)!, arguments.GetValue(ShippedOption));

            var export = await context.Get<IWorkflowExportService>()
                .ExportAsync(context.Directory, request, cancellationToken)
                .ConfigureAwait(false);

            return WorkflowExportReports.Render(export, context.Json);
        }, new JsonAnswer(JsonOption, (flags, exitCode, summary, _, _) => WorkflowExportReports.Stopped(exitCode, summary, flags.IsSet(ShippedOption))));

        return command;
    }
}
