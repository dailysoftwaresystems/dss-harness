using System.CommandLine;
using RepoHarness.Core.Mutations;
using RepoHarness.Core.Results;

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
        command.Options.Add(DispatchOptions.Here);
        GlobalOptions.AddTo(command);

        command.SetAction(CommandRunner.Wrap(Name, async (context, run, cancellationToken) =>
        {
            var arguments = context.ParseResult;
            var list = arguments.GetValue(ListOption);

            if (Refused(arguments) is { } refusal)
            {
                throw new HarnessException(HarnessExit.UsageError, refusal);
            }

            var legs = arguments.GetResult(LegsOption) is { Implicit: false }
                ? arguments.GetValue(LegsOption) ?? []
                : null;

            var arms = arguments.GetResult(ArmsOption) is { Implicit: false }
                ? arguments.GetValue(ArmsOption) ?? []
                : null;

            var selfTest = arguments.GetValue(SelfTestOption);
            var request = new MutationRequest(
                context.Directory,
                legs,
                arms,
                arguments.GetValue(ForceLockOption),
                arguments.GetValue(JsonOption),
                arguments.GetValue(UseStagedOption),
                arguments.GetValue(DispatchOptions.Here),
                selfTest,
                list,
                arguments.GetValue(PendingOption));
            var service = context.Get<MutationService>();

            return list
                ? await service.ListAsync(request, cancellationToken).ConfigureAwait(false)
                : await service.RunAsync(request, run!, cancellationToken).ConfigureAwait(false);
        }, beginsRun: arguments => !arguments.GetValue(ListOption), JsonOption));

        return command;
    }

    /// <summary>
    /// Why what <paramref name="arguments"/> holds cannot be given together, or <see langword="null"/> where it can: a
    /// self-test under <c>--pending</c>, which nothing of was ever recorded; and <c>--list</c> with each option only a
    /// sweep has a use for. Each said in one sentence.
    /// </summary>
    private static string? Refused(ParseResult arguments)
    {
        if (!arguments.GetValue(ListOption))
        {
            return arguments.GetValue(PendingOption) && arguments.GetValue(SelfTestOption)
                ? $"A self-test is never recorded, so nothing of it is pending: leave {PendingOption.Name} out."
                : null;
        }

        (Option Option, bool Given, string Wants)[] sweepsOnly =
        [
            (PendingOption, arguments.GetValue(PendingOption), "has no arm to sweep"),
            (SelfTestOption, arguments.GetValue(SelfTestOption), "has no fixture to sweep"),
            (ForceLockOption, arguments.GetValue(ForceLockOption), "has no lock to take"),
            (UseStagedOption, arguments.GetValue(UseStagedOption), "has nothing staged to sweep"),
            (DispatchOptions.Here, arguments.GetValue(DispatchOptions.Here) is not null, "has no leg to sweep for another machine"),
        ];

        return sweepsOnly.FirstOrDefault(option => option.Given) is { Option: { } given } named
            ? $"{ListOption.Name} sweeps nothing, so {given.Name} {named.Wants}: leave one of them out."
            : null;
    }
}
