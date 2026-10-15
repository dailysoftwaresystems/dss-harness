using System.CommandLine;
using DssHarness.Core.Configuration;
using DssHarness.Core.Runners;
using DssHarness.Core.SetUps;

namespace DssHarness.Cli.Commands;

/// <summary>Wires <c>DssHarness up</c>.</summary>
internal static class UpCommand
{
    internal const string Name = SetUpService.UpCommand;

    internal static Command Create() => SetUpCommand.Create(
        SetUpDirection.Up,
        "Bring a set-up up on this machine: run the 'up' command line config.json declares for it under setUp, at the root of the tree.");
}

/// <summary>Wires <c>DssHarness down</c>.</summary>
internal static class DownCommand
{
    internal const string Name = SetUpService.DownCommand;

    internal static Command Create() => SetUpCommand.Create(
        SetUpDirection.Down,
        "Bring a set-up down on this machine: run the 'down' command line config.json declares for it under setUp, at the root of the tree.");
}

/// <summary>What <c>up</c> and <c>down</c> share: one argument, <c>--input</c>, <c>--json</c>, and the service that runs the line.</summary>
internal static class SetUpCommand
{
    internal static Command Create(SetUpDirection direction, string description)
    {
        var name = new Argument<string>("name")
        {
            Description = "The set-up, as config.json names it under setUp.",
        };

        var input = new Option<string[]>(CommandLineInputs.Option)
        {
            Description = "Give one of the set-up's inputs a value: --input name=value, once per input, over the input's default. Bring it down with the values it was brought up with.",
        };

        var json = new Option<bool>("--json")
        {
            Description = "Write the line that ran, its exit code and how long it took, as JSON.",
        };

        var command = new Command(SetUpService.CommandName(direction), description);

        command.Arguments.Add(name);
        command.Options.Add(input);
        command.Options.Add(json);
        GlobalOptions.AddTo(command);

        CommandRunner.Wire(command, async (context, cancellationToken) =>
        {
            var inputs = CommandLineInputs.Parse(context.ParseResult.GetValue(input) ?? []);
            var report = await context.Get<ISetUpService>()
                .RunAsync(context.Directory, direction, context.ParseResult.GetValue(name)!, inputs, cancellationToken)
                .ConfigureAwait(false);

            return SetUpReports.Render(report, context.Json);
        }, new JsonAnswer(json, (_, exitCode, summary, _, _) => SetUpReports.Stopped(exitCode, summary, direction)));

        return command;
    }
}
