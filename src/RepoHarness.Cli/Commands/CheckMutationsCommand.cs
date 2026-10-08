using System.CommandLine;
using RepoHarness.Core.Mutations;

namespace RepoHarness.Cli.Commands;

/// <summary>Wires <c>DssHarness check-mutations</c>.</summary>
internal static class CheckMutationsCommand
{
    internal const string Name = MutationService.CommandName;

    private static readonly Option<string[]> LegsOption = new("--legs")
    {
        Description = "Only these legs or leg sets: --legs a,b or --legs a b. Without it, every declared leg.",
        AllowMultipleArgumentsPerToken = true,
    };

    private static readonly Option<string[]> ArmsOption = new("--arms")
    {
        Description = "Only these arms of the registry - with --self-test, of the fixture's - by id: --arms a,b or --arms a b. Without it, every arm.",
        AllowMultipleArgumentsPerToken = true,
    };

    private static readonly Option<bool> JsonOption = new("--json")
    {
        Description = "Write the per-leg ledger as JSON, each arm beneath its leg.",
    };

    private static readonly Option<bool> ForceLockOption = new("--force-lock")
    {
        Description = "Take a lock, or a worker, a run on another host holds. Always a human decision.",
    };

    private static readonly Option<bool> UseStagedOption = DispatchOptions.UseStaged("Sweep");

    private static readonly Option<bool> SelfTestOption = new("--self-test")
    {
        Description = "Sweep the fixture this tool carries instead of the registry's arms, built the way each selected leg builds, and hold each of its arms to the verdict it is designed to reach: proves this tool judges arms rightly with that leg's toolchain.",
    };

    internal static Command Create()
    {
        var command = new Command(
            Name,
            "Prove each selected leg's tests can fail: in worker copies of its tree, mutate each arm the registry declares, build it, witness every object depending on the site rebuilt, run its test binary whole, and judge what reddened against what the arm declares.");

        command.Options.Add(LegsOption);
        command.Options.Add(ArmsOption);
        command.Options.Add(JsonOption);
        command.Options.Add(ForceLockOption);
        command.Options.Add(UseStagedOption);
        command.Options.Add(SelfTestOption);
        command.Options.Add(DispatchOptions.Here);
        GlobalOptions.AddTo(command);

        command.SetAction(CommandRunner.Wrap(Name, async (context, run, cancellationToken) =>
        {
            var arguments = context.ParseResult;

            var legs = arguments.GetResult(LegsOption) is { Implicit: false }
                ? arguments.GetValue(LegsOption) ?? []
                : null;

            var arms = arguments.GetResult(ArmsOption) is { Implicit: false }
                ? arguments.GetValue(ArmsOption) ?? []
                : null;

            var selfTest = arguments.GetValue(SelfTestOption);

            return await context.Get<MutationService>()
                .RunAsync(
                    new MutationRequest(
                        context.Directory,
                        legs,
                        arms,
                        arguments.GetValue(ForceLockOption),
                        arguments.GetValue(JsonOption),
                        arguments.GetValue(UseStagedOption),
                        arguments.GetValue(DispatchOptions.Here),
                        selfTest),
                    run,
                    cancellationToken)
                .ConfigureAwait(false);
        }, JsonOption));

        return command;
    }
}
