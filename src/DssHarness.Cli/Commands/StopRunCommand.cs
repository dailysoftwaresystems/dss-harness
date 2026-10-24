using System.CommandLine;
using System.Globalization;
using DssHarness.Core.Runs;

namespace DssHarness.Cli.Commands;

/// <summary>Wires <c>DssHarness stop-run</c>.</summary>
internal static class StopRunCommand
{
    internal const string Name = RunStopService.CommandName;

    private static readonly Argument<string> RunArgument = new("run")
    {
        Description = "The run to stop, as the line its command began with names it: run 20261022-035935-e6571255.",
    };

    private static readonly Option<bool> NowOption = new("--now")
    {
        Description = "End the run's dispatcher where it stands, with all it started, without asking it - only one of this machine, and only the process its owner file names by its start: for a run that no longer answers. Its ledger is never written.",
    };

    private static readonly Option<bool> JsonOption = new("--json")
    {
        Description = "Write how the stop went as JSON: the run, its dispatcher, whether it stopped, and its stop record - the request it wrote, or the run's record that its dispatcher took it.",
    };

    internal static Command Create()
    {
        var command = new Command(
            Name,
            $"Stop a build, test, run or check-mutations run from another terminal: asked of its dispatcher, which stops every leg where it stands as an interrupt does and writes its ledger, and waited for {RunStopService.StopWait.TotalSeconds.ToString(CultureInfo.InvariantCulture)} s. --now ends the dispatcher's process where it stands.");

        command.Arguments.Add(RunArgument);
        command.Options.Add(NowOption);
        command.Options.Add(JsonOption);
        GlobalOptions.AddTo(command);

        CommandRunner.Wire(
            command,
            (context, cancellationToken) => context.Get<RunStopService>()
                .StopAsync(context.Directory, context.ParseResult.GetRequiredValue(RunArgument), context.ParseResult.GetValue(NowOption), context.Json, cancellationToken),
            new JsonAnswer(JsonOption, (_, exitCode, summary, _, _) => RunStopReports.Stopped(exitCode, summary)));

        return command;
    }
}
