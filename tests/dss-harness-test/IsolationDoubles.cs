using DssHarness.Core.Agents.Isolation;
using DssHarness.Core.Agents.Policy;
using DssHarness.Core.Configuration;
using DssHarness.Core.Platform;
using DssHarness.Core.Processes;

namespace DssHarness.Tests;

/// <summary>
/// The isolation of agents' commands as a test gives it: an answer of the test's for every launch, kept with what each
/// launch asked - by default none needed, as for an agent that runs no command, so a launch runs as it would with no
/// isolation at all.
/// </summary>
/// <param name="answer">The answer for a launch, from the configuration and the ground it asked with; none needed where left out.</param>
internal sealed class TestIsolation(Func<HarnessConfig, AgentPolicyGround, LaunchIsolation>? answer = null) : IAgentIsolation
{
    /// <summary>Every launch that asked, in order: its ground and its own directory.</summary>
    public List<(AgentPolicyGround Ground, string LaunchDirectory)> Asked { get; } = [];

    /// <summary>A launch's answer that refuses under the configuration's mode, or runs without under preferred: this machine has no mechanism.</summary>
    /// <param name="why">Why there is none.</param>
    /// <param name="remedy">What would give one.</param>
    public static TestIsolation Unavailable(string why, string remedy)
        => new((config, _) => new LaunchIsolation(AgentIsolationMode.Of(config), null, null, null, why, remedy));

    /// <inheritdoc/>
    public Task<LaunchIsolation> ForLaunchAsync(HarnessConfig config, AgentPolicyGround ground, string mainCheckout, string launchDirectory, CancellationToken cancellationToken)
    {
        lock (Asked)
        {
            Asked.Add((ground, launchDirectory));
        }

        return Task.FromResult(answer?.Invoke(config, ground) ?? LaunchIsolation.Unneeded(AgentIsolationMode.Of(config)));
    }
}

/// <summary>A mechanism of a test's: it hands the program its own command line unchanged, recording each it was asked for.</summary>
/// <param name="name">Its name.</param>
/// <param name="version">Its version.</param>
internal sealed class PassThroughMechanism(string name = "test", string version = "1.0") : IIsolationMechanism
{
    /// <summary>Every profile it was asked to start a program in, in order.</summary>
    public List<IsolationProfile> Profiles { get; } = [];

    /// <inheritdoc/>
    public string Name => name;

    /// <inheritdoc/>
    public string Version => version;

    /// <inheritdoc/>
    public (string Program, IReadOnlyList<string> Arguments) Command(IsolationProfile profile, string program, IReadOnlyList<string> arguments)
    {
        lock (Profiles)
        {
            Profiles.Add(profile);
        }

        return (program, arguments);
    }
}

/// <summary>The machine as it is, but for its home, which is a test's own: what the user's git configuration is read under.</summary>
/// <param name="home">The home.</param>
internal sealed class HomedPlatform(string home) : IHostPlatform
{
    private readonly HostPlatform _machine = new();

    /// <inheritdoc/>
    public PlatformId Current => _machine.Current;

    /// <inheritdoc/>
    public string PlatformKey => _machine.PlatformKey;

    /// <inheritdoc/>
    public string Processor => _machine.Processor;

    /// <inheritdoc/>
    public string HomeDirectory => home;

    /// <inheritdoc/>
    public bool IsPrivilegedProcess => false;

    /// <inheritdoc/>
    public StringComparison PathComparison => _machine.PathComparison;

    /// <inheritdoc/>
    public int? MaxPathLength => _machine.MaxPathLength;

    /// <inheritdoc/>
    public MachineIdentity MachineId => _machine.MachineId;

    /// <inheritdoc/>
    public string ExecutableName(string command) => _machine.ExecutableName(command);
}

/// <summary>A platform of a test's: the one answer it gives every probe, and how many times it was asked.</summary>
/// <param name="answer">The answer.</param>
internal sealed class FixedIsolationPlatform(IsolationAvailability answer) : IIsolationPlatform
{
    /// <summary>The trees each probe was asked for.</summary>
    public List<string> Probed { get; } = [];

    /// <inheritdoc/>
    public Task<IsolationAvailability> ProbeAsync(string tree, CancellationToken cancellationToken)
    {
        Probed.Add(tree);
        return Task.FromResult(answer);
    }
}
