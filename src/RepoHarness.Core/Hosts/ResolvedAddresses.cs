namespace RepoHarness.Core.Hosts;

/// <summary>
/// The addresses an ssh host's name has resolved to here, shared by every copy of the connection it was made for.
/// ssh names whichever it dialled when it fails, and none of them is said where ssh, or a program on the host, names
/// it as a word: <see cref="HostProbes.AsConfigured"/> writes each as the address the configuration declares.
/// </summary>
/// <remarks>
/// Learnt again, afresh, before every call that lets ssh look the name up itself - one never pinned, or whose pin
/// was dropped - as <see cref="HostCommandRunner"/> does it, so that whichever address ssh then dials is already
/// among them when it names it, in a line relayed as it arrives, wherever this machine's own lookup returns it too.
/// A machine that slept can wake with another lease, and ssh, looking the name up itself, dials the address it has
/// now; an answer learnt at the start of a command, or kept from a moment ago, would leave that one said.
/// </remarks>
public sealed class ResolvedAddresses
{
    private readonly Lock _gate = new();
    private readonly IHostAddressResolver _resolver;
    private IReadOnlyList<string> _all;

    /// <summary>The addresses <paramref name="resolution"/> found for its name, learnt again through <paramref name="resolver"/>.</summary>
    /// <param name="resolution">What looking the name up found: the HostName ssh's own configuration gives the host, or the address its item declares.</param>
    /// <param name="resolver">What looks it up again.</param>
    /// <exception cref="ArgumentException">The name resolved to nothing, so ssh can dial nothing it would name.</exception>
    public ResolvedAddresses(AddressResolution resolution, IHostAddressResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        ArgumentNullException.ThrowIfNull(resolver);

        if (!resolution.Resolved)
        {
            throw new ArgumentException($"'{resolution.Address}' resolved to no address, so there is none to keep.", nameof(resolution));
        }

        Name = resolution.Address;
        _all = resolution.Addresses;
        _resolver = resolver;
    }

    /// <summary>The name looked up.</summary>
    public string Name { get; }

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
