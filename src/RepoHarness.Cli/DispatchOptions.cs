using System.CommandLine;
using RepoHarness.Core.Hosts;
using RepoHarness.Core.Runs;

namespace RepoHarness.Cli;

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
    /// <c>--use-staged</c>, as each command that runs legs takes it: <paramref name="verb"/> what each host already holds,
    /// without syncing again. One spelling for every such command, so none can say less than the others about the copy
    /// it refuses.
    /// </summary>
    /// <param name="verb">What the command does with the copy, as its description starts: <c>Build</c>.</param>
    internal static Option<bool> UseStaged(string verb) => new("--use-staged")
    {
        Description = $"{verb} what is already staged on each host, without syncing again. Legs on a copy a sync or a takeover began and did not finish are inputs-moved.",
    };
}
