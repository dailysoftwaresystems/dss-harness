using DssHarness.Core.Hosts;
using DssHarness.Core.Processes;

namespace DssHarness.Core.Git;

/// <summary>
/// The forge's host a remote's address reaches over ssh - an alias a person's ssh configuration maps to it, as one
/// account among several is reached - read as ssh would dial it (<c>ssh -G</c>, <see cref="SshSettings.Read"/>), which
/// connects to nothing, and named as the forge's tool translates it when it reads a remote itself: the host a forge
/// serves ssh from on the web's port is the forge's own (<see cref="SshOnlyHosts"/>).
/// </summary>
/// <param name="processRunner">Runs ssh.</param>
public sealed class SshHostNames(IProcessRunner processRunner)
{
    /// <summary>How long ssh may take to say: it reads its configuration, and dials nothing.</summary>
    public static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Each host a forge serves ssh from alone - through a firewall that lets only the web's port out - by the forge's own
    /// host, which its tool knows: as the forge's tool reads a remote itself.
    /// </summary>
    public static IReadOnlyDictionary<string, string> SshOnlyHosts { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["ssh.github.com"] = "github.com",
    };

    /// <summary>
    /// The forge's host ssh's configuration gives <paramref name="host"/>; null where ssh is not here, did not say within
    /// <see cref="Limit"/>, or the host would read as an option of ssh's - whoever asks then goes on without it.
    /// </summary>
    /// <param name="host">The host, as the address names it.</param>
    /// <param name="cancellationToken">Stops ssh.</param>
    public async Task<string?> HostNameAsync(string host, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);

        if (host.StartsWith('-') || processRunner.FindExecutable(HostCommandRunner.SshProgram) is null)
        {
            return null;
        }

        var said = await processRunner.RunAsync(
            new ProcessRequest
            {
                FileName = HostCommandRunner.SshProgram,
                Arguments = ["-G", host],
                StandardInput = string.Empty,
                Timeout = Limit,
            },
            cancellationToken).ConfigureAwait(false);

        return SshSettings.Read(said)?.HostName is { } dialed
            ? SshOnlyHosts.GetValueOrDefault(dialed, dialed)
            : null;
    }
}
