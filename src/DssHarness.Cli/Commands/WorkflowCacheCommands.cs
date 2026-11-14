using System.CommandLine;
using System.Globalization;
using DssHarness.Core.Orchestration.Flow;

namespace DssHarness.Cli.Commands;

/// <summary>Wires <c>DssHarness workflow-cache</c>.</summary>
internal static class WorkflowCacheCommands
{
    internal const string Name = WorkflowCacheService.CommandName;

    private static readonly Option<int> UnusedDaysOption = new("--unused-days")
    {
        Description = $"Prune the entries no run used, and none was fetched, for this many days (0 to {WorkflowCache.MostUnusedDays.ToString("N0", CultureInfo.InvariantCulture)}), the resolutions that named them, and the blobs no entry left holds.",
        Required = true,
    };

    private static readonly Option<bool> JsonOption = new("--json")
    {
        Description = "Write what the cache holds, or what the pruning removed, and the cache's directory, as JSON.",
    };

    internal static Command Create()
    {
        var list = new Command(
            "list",
            "List each entry the workflow cache holds - where it came from, its commit, its files and bytes, when it was last used and the refs whose newest resolution it is - and every file of the cache's own that could not be read, naming the cache's directory.");

        list.Options.Add(JsonOption);
        GlobalOptions.AddTo(list);

        CommandRunner.Wire(list, async (context, cancellationToken) =>
            WorkflowCacheReports.Render(await context.Get<IWorkflowCacheService>().ListAsync(cancellationToken).ConfigureAwait(false), context.Json),
            new JsonAnswer(JsonOption, (flags, exitCode, summary, _, _) => WorkflowCacheReports.Stopped(exitCode, summary)));

        var prune = new Command(
            "prune",
            "Remove the entries no run used for the days --unused-days names, the resolutions that named them, and the blobs no entry left holds, naming the cache's directory; a file it could not remove or read is said, and the pruning ends incomplete.")
        {
            UnusedDaysOption,
            JsonOption,
        };

        GlobalOptions.AddTo(prune);

        CommandRunner.Wire(prune, async (context, cancellationToken) =>
            WorkflowCacheReports.Render(await context.Get<IWorkflowCacheService>().PruneAsync(context.ParseResult.GetValue(UnusedDaysOption), cancellationToken).ConfigureAwait(false), context.Json),
            new JsonAnswer(JsonOption, (flags, exitCode, summary, _, _) => WorkflowCacheReports.Stopped(exitCode, summary)));

        var command = new Command(Name, "The hash cache of remote workflows used as steps: what it holds, and its pruning.")
        {
            list,
            prune,
        };

        return command;
    }
}
