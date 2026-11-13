using System.CommandLine;
using DssHarness.Core.Orchestration.Flow;

namespace DssHarness.Cli.Commands;

/// <summary>Wires <c>DssHarness workflow-cache</c>.</summary>
internal static class WorkflowCacheCommands
{
    internal const string Name = WorkflowCacheService.CommandName;

    private static readonly Option<int?> UnusedDaysOption = new("--unused-days")
    {
        Description = "Prune the entries no run used for this many days, and the blobs no entry left holds; left out, the cache is listed.",
    };

    private static readonly Option<bool> JsonOption = new("--json")
    {
        Description = "Write what the cache holds, or what the pruning removed, as JSON.",
    };

    internal static Command Create()
    {
        var list = new Command(
            "list",
            "List each entry the workflow cache holds: where it came from, its commit, its files and bytes, and when it was last used.");

        list.Options.Add(JsonOption);
        GlobalOptions.AddTo(list);

        CommandRunner.Wire(list, async (context, cancellationToken) =>
            WorkflowCacheReports.Render(await context.Get<IWorkflowCacheService>().RunAsync(unusedDays: null, cancellationToken).ConfigureAwait(false), context.Json),
            new JsonAnswer(JsonOption, (flags, exitCode, summary, _, _) => WorkflowCacheReports.Stopped(exitCode, summary)));

        var prune = new Command(
            "prune",
            "Remove the entries no run used for the days --unused-days names, and the blobs no entry left holds.")
        {
            UnusedDaysOption,
            JsonOption,
        };

        GlobalOptions.AddTo(prune);

        CommandRunner.Wire(prune, async (context, cancellationToken) =>
            WorkflowCacheReports.Render(await context.Get<IWorkflowCacheService>().RunAsync(context.ParseResult.GetValue(UnusedDaysOption), cancellationToken).ConfigureAwait(false), context.Json),
            new JsonAnswer(JsonOption, (flags, exitCode, summary, _, _) => WorkflowCacheReports.Stopped(exitCode, summary)));

        var command = new Command(Name, "The hash cache of remote workflows used as steps: what it holds, and its pruning.")
        {
            list,
            prune,
        };

        return command;
    }
}
