using System.CommandLine;
using DssHarness.Core.Application.Legs;
using DssHarness.Core.Build;
using DssHarness.Core.Output;

namespace DssHarness.Cli.Commands;

/// <summary>Wires <c>DssHarness build</c>.</summary>
internal static class BuildCommand
{
    internal const string Name = BuildService.CommandName;

    private static readonly Option<string[]> LegsOption = new("--legs")
    {
        Description = "Only these legs or leg sets: --legs a,b or --legs a b. Without it, every declared leg.",
        AllowMultipleArgumentsPerToken = true,
    };

    private static readonly Option<bool> JsonOption = new("--json")
    {
        Description = "Write the per-leg ledger as JSON.",
    };

    private static readonly Option<bool> TimeOption = new("--time")
    {
        Description = "Report the profile timing, taken from buildTimingRegex where it is configured and measured here where it is not.",
    };

    private static readonly Option<bool> ForceLockOption = new("--force-lock")
    {
        Description = "Take a lock another run holds, on this host or another. Always a human decision.",
    };

    private static readonly Option<bool> UseStagedOption = DispatchOptions.UseStaged("Build");

    private static readonly Option<bool> LocalCopyOption = DispatchOptions.LocalCopy;

    private static readonly Option<bool> InPlaceOption = DispatchOptions.InPlace;

    internal static Command Create()
    {
        var command = new Command(
            Name,
            "Build every selected leg, each in its own variant-keyed build directory, and check that it produced what the project declares.");

        command.Options.Add(LegsOption);
        command.Options.Add(JsonOption);
        command.Options.Add(TimeOption);
        command.Options.Add(ForceLockOption);
        command.Options.Add(UseStagedOption);
        command.Options.Add(LocalCopyOption);
        command.Options.Add(InPlaceOption);
        command.Options.Add(DispatchOptions.Here);
        GlobalOptions.AddTo(command);

        CommandRunner.Wire(command, async (context, run, cancellationToken) =>
        {
            var arguments = context.ParseResult;

            return (await context.Get<IBuildLegs>()
                .RunAsync(
                    new BuildLegsRequest
                    {
                        Directory = context.Directory,
                        LegNames = arguments.GetResult(LegsOption) is { Implicit: false }
                            ? arguments.GetValue(LegsOption) ?? []
                            : null,
                        ForceLock = arguments.GetValue(ForceLockOption),
                        Json = context.Json,
                        UseStaged = arguments.GetValue(UseStagedOption),
                        Time = arguments.GetValue(TimeOption),
                        Here = arguments.GetValue(DispatchOptions.Here),
                        LocalCopy = arguments.GetValue(DispatchOptions.LocalCopy),
                        InPlace = arguments.GetValue(DispatchOptions.InPlace),
                    },
                    run!,
                    cancellationToken)
                .ConfigureAwait(false))
                .ToOutcome(context.Json, context.Get<IHarnessOutput>().Shown);
        }, JsonAnswer.Ledger(JsonOption));

        return command;
    }
}
