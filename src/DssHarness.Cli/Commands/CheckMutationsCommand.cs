using System.CommandLine;
using DssHarness.Core.Application.Legs;
using DssHarness.Core.Mutations;
using DssHarness.Core.Output;

namespace DssHarness.Cli.Commands;

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
        Description = "Take a lock, or a worker, another run holds, on this host or another. Always a human decision.",
    };

    private static readonly Option<bool> UseStagedOption = DispatchOptions.UseStaged("Sweep");

    private static readonly Option<bool> SelfTestOption = new("--self-test")
    {
        Description = "Sweep the fixture this tool carries instead of the registry's arms, built the way each selected leg builds, and hold each of its arms to the verdict it is designed to reach: proves this tool judges arms rightly with that leg's toolchain.",
    };

    private static readonly Option<bool> ListOption = new("--list")
    {
        Description = "Sweep nothing: say each selected arm's last verdict on each leg and whether anything it was judged on that this machine reads has moved since. Asks no host, takes no lock, begins no run and writes nothing; exits 0 whatever is pending.",
    };

    private static readonly Option<bool> PendingOption = new("--pending")
    {
        Description = "Drive on each leg only the arms with no recorded passed that still holds: its first worker is made alone, each arm's key read from its build, and an arm whose key is a recorded passed's is left out, skipped-not-selected, saying which run's passed holds. With nothing pending the leg passes, and nothing is swept.",
    };

    /// <summary>
    /// The hidden option that carries, as bytes read as their base64 text, what the record of verdicts holds of one leg's
    /// arms: given to a host sweeping one leg of a run under --pending by the machine that asked, and typed by nobody.
    /// </summary>
    private static readonly Option<string?> HeldOption = new("--held")
    {
        Hidden = true,
    };

    /// <summary>
    /// The document a stop answers with under --json: a listing's, listing no leg, where --list is set, and a sweep's
    /// ledger, with no legs, where it is not - a --list whose value the parser refused among them (<see cref="CommandFlags"/>).
    /// </summary>
    private static readonly JsonAnswer Answer = new(
        JsonOption,
        (flags, exitCode, summary, shown, run) => flags.IsSet(ListOption)
            ? MutationService.ListStopped(exitCode, summary, shown)
            : JsonAnswer.Ledger(JsonOption).Stopped(flags, exitCode, summary, shown, run));

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
        command.Options.Add(ListOption);
        command.Options.Add(PendingOption);
        command.Options.Add(HeldOption);
        command.Options.Add(DispatchOptions.Here);
        GlobalOptions.AddTo(command);

        CommandRunner.Wire(command, async (context, run, cancellationToken) =>
        {
            var arguments = context.ParseResult;
            var legs = context.Get<IMutationLegs>();

            var request = new MutationLegsRequest
            {
                Directory = context.Directory,
                LegNames = arguments.GetResult(LegsOption) is { Implicit: false }
                    ? arguments.GetValue(LegsOption) ?? []
                    : null,
                ArmNames = arguments.GetResult(ArmsOption) is { Implicit: false }
                    ? arguments.GetValue(ArmsOption) ?? []
                    : null,
                ForceLock = arguments.GetValue(ForceLockOption),
                Json = context.Json,
                UseStaged = arguments.GetValue(UseStagedOption),
                Here = arguments.GetValue(DispatchOptions.Here),
                SelfTest = arguments.GetValue(SelfTestOption),
                List = arguments.GetValue(ListOption),
                Pending = arguments.GetValue(PendingOption),
                Held = arguments.GetValue(HeldOption),
            };

            return request.List
                ? await legs.ListAsync(request, cancellationToken).ConfigureAwait(false)
                : (await legs.RunAsync(request, run!, cancellationToken).ConfigureAwait(false)).ToOutcome(context.Json, context.Get<IHarnessOutput>().Shown);
        }, beginsRun: arguments => !arguments.GetValue(ListOption), Answer);

        return command;
    }
}
