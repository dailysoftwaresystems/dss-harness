using DssHarness.Core.Hosts;
using DssHarness.Core.Output;
using DssHarness.Core.Processes;
using DssHarness.Core.Waiting;

namespace DssHarness.Core.Git;

/// <summary>
/// The forge's host a remote's address reaches over ssh - an alias a person's ssh configuration maps to it, as one
/// account among several is reached - read as ssh would dial it (<c>ssh -G</c>, <see cref="SshSettings.Read"/>), which
/// connects to nothing, and named as the forge's tool translates it when it reads a remote itself: the host a forge
/// serves ssh from on the web's port is the forge's own (<see cref="SshOnlyHosts"/>).
/// </summary>
/// <remarks>
/// ssh is waited for until it says, however long its configuration takes to read - a <c>Match exec</c> line runs a
/// program of the person's - and the wait is told every <see cref="Waits.SaidEvery"/> while it lasts, so a slow
/// configuration reads as one, never as a hang; the caller's token ends it, and nothing of the code's own does.
/// </remarks>
/// <param name="processRunner">Runs ssh.</param>
/// <param name="output">Tells a wait for ssh that lasts; null tells nothing.</param>
/// <param name="clock">Measures the wait; the system's where none is given.</param>
public sealed class SshHostNames(IProcessRunner processRunner, IHarnessOutput? output = null, TimeProvider? clock = null)
{
    /// <summary>What a wait for ssh is told under, as the harness's own lines name what speaks.</summary>
    public const string Said = HostCommandRunner.SshProgram;

    /// <summary>
    /// Each host a forge serves ssh from alone - through a firewall that lets only the web's port out - by the forge's own
    /// host, which its tool knows: as the forge's tool reads a remote itself.
    /// </summary>
    public static IReadOnlyDictionary<string, string> SshOnlyHosts { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["ssh.github.com"] = "github.com",
    };

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>
    /// The forge's host ssh's configuration gives <paramref name="host"/>; null where ssh is not here, did not say, or the
    /// host would read as an option of ssh's - whoever asks then goes on without it. ssh is waited for as long as it
    /// takes, told every <see cref="Waits.SaidEvery"/> (<see cref="Waits.ForAsync"/>).
    /// </summary>
    /// <param name="host">The host, as the address names it.</param>
    /// <param name="cancellationToken">Stops ssh, and the wait.</param>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> ended the wait.</exception>
    public async Task<string?> HostNameAsync(string host, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);

        if (host.StartsWith('-') || processRunner.FindExecutable(HostCommandRunner.SshProgram) is null)
        {
            return null;
        }

        var said = await Waits.ForAsync(
                token => processRunner.RunAsync(
                    new ProcessRequest
                    {
                        FileName = HostCommandRunner.SshProgram,
                        Arguments = ["-G", host],
                        StandardInput = string.Empty,
                    },
                    token),
                new WaitWatch($"ssh -G {host} to say the host it dials", told => output?.Info(Said, told.Line()), null, _clock),
                cancellationToken)
            .ConfigureAwait(false);

        // No bound of its own is given the wait, so it ends only at its work's end or at the caller's token.
        if (said is not { Kind: WaitEndKind.Done, Value: { } answer })
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new OperationCanceledException($"the wait for ssh -G {host} ended before ssh said", cancellationToken);
        }

        return SshSettings.Read(answer)?.HostName is { } dialed
            ? SshOnlyHosts.GetValueOrDefault(dialed, dialed)
            : null;
    }
}
