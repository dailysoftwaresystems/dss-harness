namespace RepoHarness.Core.Hosts;

/// <summary>
/// The addresses an ssh host's name has resolved to here, shared by every copy of the connection it was made for.
/// ssh names whichever it dialled when it fails, and none of them is ever said: <see cref="HostProbes.AsConfigured"/>
/// writes each as the address the configuration declares.
/// </summary>
/// <remarks>
/// Learnt again, afresh, before every call that lets ssh look the name up itself - one never pinned, or whose pin
/// was dropped - as <see cref="HostCommandRunner"/> does it, so that whichever address ssh then dials is already
/// among them when it names it, in a line relayed as it arrives. A machine that slept can wake with another lease,
/// and ssh, looking the name up itself, dials the address it has now; an answer learnt at the start of a command,
/// or kept from a moment ago, would leave that one said.
/// </remarks>
/// <param name="name">The name looked up: the HostName ssh's own configuration gives the host, or the address its item declares.</param>
/// <param name="found">What looking it up found.</param>
/// <param name="resolver">What looks it up again.</param>
public sealed class ResolvedAddresses(string name, IReadOnlyList<string> found, IHostAddressResolver resolver)
{
    private readonly Lock _gate = new();
    private readonly IHostAddressResolver _resolver = resolver;
    private IReadOnlyList<string> _all = found;

    /// <summary>The name looked up.</summary>
    public string Name { get; } = name;

    /// <summary>Every address the name has resolved to so far.</summary>
    public IReadOnlyList<string> All
    {
        get
        {
            lock (_gate)
            {
                return _all;
            }
        }
    }

    /// <summary>
    /// Looks the name up again, past the answer kept from a moment ago, and keeps every address it finds that was not
    /// already among them.
    /// </summary>
    /// <param name="cancellationToken">Stops the lookup.</param>
    public async Task LearnAsync(CancellationToken cancellationToken)
    {
        var resolution = await _resolver.ResolveAgainAsync(Name, cancellationToken).ConfigureAwait(false);

        lock (_gate)
        {
            _all = [.. _all.Union(resolution.Addresses, StringComparer.OrdinalIgnoreCase)];
        }
    }
}
