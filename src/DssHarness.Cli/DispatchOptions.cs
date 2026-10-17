using System.CommandLine;
using DssHarness.Core.Hosts;
using DssHarness.Core.Results;
using DssHarness.Core.Runs;
using DssHarness.Core.SetUps;

namespace DssHarness.Cli;

/// <summary>
/// Options every command that runs legs reads alike: what a machine dispatching a leg tells the host that runs it, and
/// what a run on what is already staged means.
/// </summary>
internal static class DispatchOptions
{
    /// <summary>
    /// The host this machine is to the machine that dispatched a leg here: every selected leg runs on
    /// this machine, with the settings that machine's configuration gives the host it names.
    /// </summary>
    /// <remarks>
    /// Hidden, because nobody types it. One option for every command that runs legs, so no command can
    /// read it differently.
    /// </remarks>
    internal static Option<HostId?> Here { get; } = new(RemoteLegRunner.HereOption)
    {
        Description = "Run every selected leg on this machine, as the host the machine that dispatched it names.",
        Hidden = true,
        CustomParser = result =>
        {
            var spelled = result.Tokens.Count == 1 ? result.Tokens[0].Value : null;

            if (HostId.TryParse(spelled, out var host))
            {
                return host;
            }

            result.AddError(
                $"{RemoteLegRunner.HereOption} names a host as 'local', 'wsl <distribution>' or 'ssh <name>', "
                + $"and '{spelled}' is none of them.");
            return null;
        },
    };

    /// <summary>
    /// <c>--use-staged</c>, as each command that runs legs takes it: <paramref name="verb"/> what each copy already
    /// holds, without syncing again. One spelling for every such command, so none can say less than the others about the
    /// copy it refuses.
    /// </summary>
    /// <param name="verb">What the command does with the copy, as its description starts: <c>Build</c>.</param>
    internal static Option<bool> UseStaged(string verb) => new("--use-staged")
    {
        Description = $"{verb} what is already staged on each copy, without syncing again. Legs on a copy a sync or a takeover began and did not finish, that is not there, or that holds no copy this tool made are inputs-moved.",
    };

    /// <summary>
    /// <c>--local-copy</c>: every leg this machine runs itself works in a copy of its tree, for this run. One definition
    /// for the three commands that take it, so none can place a leg differently from the others.
    /// </summary>
    internal static Option<bool> LocalCopy { get; } = new("--local-copy")
    {
        Description = "Every leg this machine runs itself works in a copy of its tree kept in the tree's .legs, for this run: over the leg's own localCopy and defaults.localCopy.",
    };

    /// <summary><c>--in-place</c>: every leg this machine runs itself works in its tree, for this run.</summary>
    internal static Option<bool> InPlace { get; } = new("--in-place")
    {
        Description = "Every leg this machine runs itself works in its tree itself, for this run: over the leg's own localCopy and defaults.localCopy.",
    };

    /// <summary>
    /// <c>--set-up</c>: each leg brings up the set-ups its work names before that work, and down after it. One definition
    /// for the two commands that take it.
    /// </summary>
    internal static Option<bool> SetUp { get; } = new(LegSetUps.Option)
    {
        Description = "Bring up the set-ups each leg's work names under setUp before that work starts, on the machine that runs it, and down after it. Without it, none is.",
    };
}
