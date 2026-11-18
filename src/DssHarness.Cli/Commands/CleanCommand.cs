using System.CommandLine;
using DssHarness.Core.Runs;

namespace DssHarness.Cli.Commands;

/// <summary>Wires <c>DssHarness clean</c>.</summary>
internal static class CleanCommand
{
    internal const string Name = CleanService.CommandName;

    private static readonly Option<string[]> LegsOption = LegSelectionOption.Create();

    private static readonly Option<bool> DryRunOption = new(CleanService.DryRunOption)
    {
        Description = "Say what each leg's build directory, workers and copies hold and the room left on its filesystem, remove nothing, and exit as the clean would: 13 where a run holds the leg, a sweep its workers or a run its copy.",
    };

    private static readonly Option<bool> FormerWorkersOption = new(CleanService.FormerWorkersOption)
    {
        Description = "Remove only the mutation workers an earlier version of this tool kept beside the repository's trees, as <tree>.mutation-<key><mark>-<n>, or in them, as <tree>/.mutations/<key><mark>-<n>, on each machine a selected leg runs on - with --dry-run, list them with their sizes. One a sweep of that version still claims is kept, refused-locked.",
    };

    private static readonly Option<bool> GoneCopiesOption = new(CleanService.GoneCopiesOption)
    {
        Description = "Remove only the copies hosts keep of worktrees gone from this machine, told by the tree this machine's record names - never by name, so a worktree here of the same name keeps its copies - on every host the record names one on, a line each, or with --legs those a selected leg can run on; with --dry-run, say what each holds and would become. One a run or a sweep holds is kept, refused-locked; a host that cannot be reached keeps its copies recorded for a later clean.",
    };

    private static readonly Option<bool> JsonOption = new("--json")
    {
        Description = "Write the per-leg ledger as JSON.",
    };

    internal static Command Create()
    {
        var command = new Command(
            Name,
            "Remove each selected leg's build directory, the mutation workers its sweeps keep in its tree, and the copies of its tree it works in on this machine (.legs), wherever the leg runs, writing nothing there first, so a full disk can be freed.");

        command.Options.Add(LegsOption);
        command.Options.Add(DryRunOption);
        command.Options.Add(FormerWorkersOption);
        command.Options.Add(GoneCopiesOption);
        command.Options.Add(JsonOption);
        command.Options.Add(DispatchOptions.Here);
        GlobalOptions.AddTo(command);

        CommandRunner.Wire(command, async (context, cancellationToken) =>
        {
            var arguments = context.ParseResult;

            var legs = arguments.GetResult(LegsOption) is { Implicit: false }
                ? arguments.GetValue(LegsOption) ?? []
                : null;

            return await context.Get<CleanService>()
                .RunAsync(
                    new CleanRequest(
                        context.Directory,
                        legs,
                        arguments.GetValue(DryRunOption),
                        context.Json,
                        arguments.GetValue(DispatchOptions.Here))
                    {
                        FormerWorkers = arguments.GetValue(FormerWorkersOption),
                        GoneCopies = arguments.GetValue(GoneCopiesOption),
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }, JsonAnswer.Ledger(JsonOption));

        return command;
    }
}
