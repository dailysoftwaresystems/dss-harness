using System.CommandLine;
using DssHarness.Core.Application.Legs;
using DssHarness.Core.Output;
using DssHarness.Core.Testing;

namespace DssHarness.Cli.Commands;

/// <summary>Wires <c>DssHarness test</c>.</summary>
internal static class TestCommand
{
    internal const string Name = TestService.CommandName;

    private static readonly Option<string[]> LegsOption = new("--legs")
    {
        Description = "Only these legs or leg sets: --legs a,b or --legs a b. Without it, every declared leg.",
        AllowMultipleArgumentsPerToken = true,
    };

    private static readonly Option<string> FilterOption = new("--filter")
    {
        Description = "Run only tests the invocation's filterArg selects with this value.",
    };

    private static readonly Option<string[]> ExcludeOption = new("--exclude")
    {
        Description = "Skip tests the invocation's excludeArg excludes with these values.",
        AllowMultipleArgumentsPerToken = true,
    };

    private static readonly Option<string[]> LabelOption = new("--label")
    {
        Description = "Run only tests the invocation's labelArg selects with these values, such as a ctest label.",
        AllowMultipleArgumentsPerToken = true,
    };

    private static readonly Option<bool> JsonOption = new("--json")
    {
        Description = "Write the per-leg ledger as JSON.",
    };

    private static readonly Option<bool> TimeOption = new("--time")
    {
        Description = "Report the profile timing, taken from testTimingRegex where it is configured and measured here where it is not.",
    };

    private static readonly Option<bool> ForceLockOption = new("--force-lock")
    {
        Description = "Take a lock another run holds, on this host or another. Always a human decision.",
    };

    private static readonly Option<bool> UseStagedOption = DispatchOptions.UseStaged("Test");

    private static readonly Option<bool> SkipBuildOption = new("--no-build")
    {
        Description = "Test what is already built, without building first.",
    };

    private static readonly Option<bool> LocalCopyOption = DispatchOptions.LocalCopy;

    private static readonly Option<bool> InPlaceOption = DispatchOptions.InPlace;

    internal static Command Create()
    {
        var command = new Command(
            Name,
            "Build and test every selected leg, with a witness for each verdict: a zero exit code with no match, a tree that moved, or another run in the build directory each report as themselves.");

        command.Options.Add(LegsOption);
        command.Options.Add(FilterOption);
        command.Options.Add(ExcludeOption);
        command.Options.Add(LabelOption);
        command.Options.Add(JsonOption);
        command.Options.Add(TimeOption);
        command.Options.Add(ForceLockOption);
        command.Options.Add(UseStagedOption);
        command.Options.Add(SkipBuildOption);
        command.Options.Add(LocalCopyOption);
        command.Options.Add(InPlaceOption);
        command.Options.Add(DispatchOptions.Here);
        command.Options.Add(DispatchOptions.SetUp);
        GlobalOptions.AddTo(command);

        CommandRunner.Wire(command, async (context, run, cancellationToken) =>
        {
            var arguments = context.ParseResult;

            return (await context.Get<ITestLegs>()
                .RunAsync(
                    new TestLegsRequest
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
                        SetUp = arguments.GetValue(DispatchOptions.SetUp),
                        LocalCopy = arguments.GetValue(DispatchOptions.LocalCopy),
                        InPlace = arguments.GetValue(DispatchOptions.InPlace),

                        // Read once, for the legs run here and the hosts given theirs alike: which tests run is one
                        // choice, and a host told something else would report another suite under the same leg.
                        Filter = arguments.GetValue(FilterOption),
                        Excludes = arguments.GetValue(ExcludeOption) ?? [],
                        Labels = arguments.GetValue(LabelOption) ?? [],
                        SkipBuild = arguments.GetValue(SkipBuildOption),
                    },
                    run!,
                    cancellationToken)
                .ConfigureAwait(false))
                .ToOutcome(context.Json, context.Get<IHarnessOutput>().Shown);
        }, JsonAnswer.Ledger(JsonOption));

        return command;
    }
}
