using System.CommandLine;
using DssHarness.Core.Application.Legs;
using DssHarness.Core.Runners;

namespace DssHarness.Cli.Commands;

/// <summary>Wires <c>DssHarness run</c>.</summary>
internal static class RunCommand
{
    internal const string Name = RunnerRunService.CommandName;

    private static readonly Argument<string> RunnerArgument = new("runner")
    {
        Description = "The predefined runner to run, as predefinedRunners names it.",
    };

    private static readonly Option<string[]> LegsOption = new("--legs")
    {
        Description = "Only these legs or leg sets: --legs a,b or --legs a b. Without it, the legs the runner declares.",
        AllowMultipleArgumentsPerToken = true,
    };

    private static readonly Option<bool> JsonOption = new("--json")
    {
        Description = "Write the per-leg ledger as JSON.",
    };

    private static readonly Option<bool> TimeOption = new("--time")
    {
        Description = "Pull runTimingRegex out of every step's output.",
    };

    private static readonly Option<bool> ForceLockOption = new("--force-lock")
    {
        Description = "Take a lock another run holds, on this host or another. Always a human decision.",
    };

    private static readonly Option<bool> UseStagedOption = DispatchOptions.UseStaged("Run against");

    private static readonly Option<string[]> InputOption = new(CommandLineInputs.Option)
    {
        Description = "Give one of the action's inputs a value for this run: --input name=value, once per input. Over the runner's .env values and the input's default; never a secret.",
    };

    private static readonly Option<string[]> ManualStepOption = new(StepSelection.Option)
    {
        Description = "Run only these manual steps of the runner's action, each after the steps it needs: --manual-step a,b or --manual-step a --manual-step b. Without it, every step that is not manual, or the steps the runner names.",
        AllowMultipleArgumentsPerToken = true,
    };

    private static readonly Option<bool> LocalCopyOption = DispatchOptions.LocalCopy;

    private static readonly Option<bool> InPlaceOption = DispatchOptions.InPlace;

    internal static Command Create()
    {
        var command = new Command(
            Name,
            "Run a predefined runner across the legs it declares, with the same isolation, locking, stall bounds, witnesses and reporting build and test get.");

        command.Arguments.Add(RunnerArgument);
        command.Options.Add(LegsOption);
        command.Options.Add(JsonOption);
        command.Options.Add(TimeOption);
        command.Options.Add(ForceLockOption);
        command.Options.Add(UseStagedOption);
        command.Options.Add(InputOption);
        command.Options.Add(ManualStepOption);
        command.Options.Add(LocalCopyOption);
        command.Options.Add(InPlaceOption);
        command.Options.Add(DispatchOptions.Here);
        command.Options.Add(DispatchOptions.SetUp);
        GlobalOptions.AddTo(command);

        CommandRunner.Wire(command, async (context, run, cancellationToken) =>
        {
            var arguments = context.ParseResult;

            // Read before anything else is: a pair that gives nothing is a mistake on this line
            // alone, and costs no configuration read to name. So is a --manual-step naming nothing.
            var inputs = CommandLineInputs.Parse(arguments.GetValue(InputOption) ?? []);
            var manualSteps = StepSelection.Names(arguments.GetResult(ManualStepOption) is { Implicit: false }
                ? arguments.GetValue(ManualStepOption) ?? []
                : null);

            return (await context.Get<IRunnerLegs>()
                .RunAsync(
                    new RunnerLegsRequest
                    {
                        Directory = context.Directory,
                        Runner = arguments.GetRequiredValue(RunnerArgument),
                        LegNames = arguments.GetResult(LegsOption) is { Implicit: false }
                            ? arguments.GetValue(LegsOption) ?? []
                            : null,
                        ForceLock = arguments.GetValue(ForceLockOption),
                        Json = context.Json,
                        UseStaged = arguments.GetValue(UseStagedOption),
                        Time = arguments.GetValue(TimeOption),
                        Here = arguments.GetValue(DispatchOptions.Here),
                        SetUp = arguments.GetValue(DispatchOptions.SetUp),
                        LocalCopy = arguments.GetValue(DispatchOptions.LocalCopy),
                        InPlace = arguments.GetValue(DispatchOptions.InPlace),
                        Inputs = inputs,
                        ManualSteps = manualSteps,
                    },
                    run!,
                    cancellationToken)
                .ConfigureAwait(false))
                .ToOutcome(context.Json);
        }, JsonAnswer.Ledger(JsonOption));

        return command;
    }
}
