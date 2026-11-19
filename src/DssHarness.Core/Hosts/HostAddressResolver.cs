using System.Globalization;
using System.Net;
using System.Net.Sockets;
using DssHarness.Core.Waiting;

namespace DssHarness.Core.Hosts;

/// <summary>
/// Looks a name up with whatever this machine resolves names with: DNS, mDNS for a <c>.local</c> name,
/// or the hosts file. A seam rather than an abstraction over networking: it exists so that the retrying
/// above it is testable without a LAN.
/// </summary>
public interface INameLookup
{
    /// <summary>
    /// The addresses <paramref name="name"/> resolves to, empty where no answer came - which another lookup may change.
    /// </summary>
    /// <param name="name">The name to look up.</param>
    /// <param name="cancellationToken">Stops the lookup.</param>
    /// <exception cref="NoSuchNameException">
    /// The name's own authority answered that it does not exist, or that it holds no address: an answer no further
    /// lookup changes.
    /// </exception>
    Task<IReadOnlyList<string>> LookupAsync(string name, CancellationToken cancellationToken = default);
}

/// <summary>
/// A name whose own authority answered that it does not exist - DNS's NXDOMAIN - or that it holds no address: the fact no
/// further lookup changes, as a missing file is.
/// </summary>
/// <param name="name">The name looked up.</param>
/// <param name="said">What the authority answered, as words that follow the name: <c>does not exist</c>.</param>
public sealed class NoSuchNameException(string name, string said) : Exception($"'{name}' {said}")
{
    /// <summary>The name looked up.</summary>
    public string Name { get; } = name;

    /// <summary>What the authority answered, as words that follow the name.</summary>
    public string Said { get; } = said;
}

/// <inheritdoc cref="INameLookup"/>
/// <remarks>
/// Only a name a unicast DNS server answers with authority is ever said not to exist: one of several labels, outside
/// <c>.local</c>. A <c>.local</c> name is asked over mDNS, whose responders answer only for themselves and never that a
/// name does not exist - a miss is a responder that did not answer the first query, measured to answer the second - and a
/// single-label name is asked of LLMNR or NetBIOS beside the search domains, which have no authority either: for those a
/// miss is no answer, and is looked up again.
/// </remarks>
public sealed class DnsNameLookup : INameLookup
{
    public async Task<IReadOnlyList<string>> LookupAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(name, cancellationToken).ConfigureAwait(false);
            return [.. addresses.Select(address => address.ToString())];
        }
        catch (SocketException ex) when (Authoritative(name) && ex.SocketErrorCode is SocketError.HostNotFound or SocketError.NoData)
        {
            throw new NoSuchNameException(name, ex.SocketErrorCode == SocketError.NoData ? "holds no address" : "does not exist");
        }
        catch (SocketException)
        {
            // No answer came - a server that did not answer, a responder that missed the query: another lookup may hear one.
            return [];
        }
    }

    /// <summary>Whether a miss of <paramref name="name"/> is a DNS server's answer with authority (see the remarks).</summary>
    internal static bool Authoritative(string name)
    {
        var trimmed = name.TrimEnd('.');

        return trimmed.Contains('.', StringComparison.Ordinal)
            && !trimmed.EndsWith(".local", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>What resolving one host's address found.</summary>
/// <param name="Address">
/// The name looked up: the HostName ssh's own configuration gives the host, or the address its item
/// declares where ssh could not say.
/// </param>
/// <param name="Attempts">How many lookups it took, so a name that needs retrying is visible.</param>
/// <param name="Addresses">
/// Every address it resolved to - the literal itself for one - and none where it resolved to none: any of them is one
/// ssh may dial and name, and each is written as the address the configuration declares wherever it is named, as
/// <see cref="HostProbes.AsConfigured"/> explains.
/// </param>
/// <param name="NoSuchName">
/// What the name's own authority answered where it said the name does not exist, or holds no address, as words that
/// follow the name; <see langword="null"/> where it resolved, or where no answer came.
/// </param>
public sealed record AddressResolution(string Address, int Attempts, IReadOnlyList<string> Addresses, string? NoSuchName = null)
{
    /// <summary>Whether it names a machine this one can reach: it resolved to an address.</summary>
    public bool Resolved => Addresses.Count > 0;

    /// <summary>
    /// The address a pin gives ssh as its HostName - the literal itself, or an IPv4 one where the name resolved to
    /// any - or <see langword="null"/> where it resolved to none.
    /// </summary>
    public string? ResolvedTo => Resolved ? HostAddressResolver.Preferred(Addresses) : null;

    /// <summary>A name that got no answer in <paramref name="attempts"/> lookups.</summary>
    /// <param name="address">The name looked up.</param>
    /// <param name="attempts">How many lookups it took.</param>
    public static AddressResolution Missed(string address, int attempts) => new(address, attempts, []);
}

/// <summary>
/// Looks an ssh host's name up before ssh is started, looking it up again while no answer comes, and says which of its
/// addresses a pin would give ssh.
/// </summary>
/// <remarks>
/// Measured: a host on a DHCP LAN reached by an mDNS <c>.local</c> name fails a lookup as a matter of
/// course - the responder does not answer the first query and answers the second - and ssh then exits
/// with "Could not resolve hostname", which reads as a host that is switched off. So the name ssh would
/// look up is looked up here, again where no answer came, and ssh is given the address it resolved to, as an
/// <see cref="SshPin"/> explains. A name that does not exist is refused at once, with that as the reason.
/// </remarks>
public interface IHostAddressResolver
{
    /// <summary>
    /// Resolves <paramref name="address"/>, afresh each time: looked up again after a lookup no answer came to, until one
    /// does or the same silence is met twice; ended at once by a name its authority says does not exist.
    /// </summary>
    /// <param name="address">
    /// The name to look up: the HostName ssh's own configuration gives the host, or the address its item
    /// declares where ssh could not say.
    /// </param>
    /// <param name="cancellationToken">Stops the lookups.</param>
    Task<AddressResolution> ResolveAsync(string address, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IHostAddressResolver"/>
/// <remarks>
/// Through <see cref="Retrying"/>: a lookup that heard no answer is tried again, a second silence ends the tries - the
/// same failure on the same state, which a third lookup would only meet again - and an answer that the name does not
/// exist ends them at once. No answer is kept of this tool's own: the system's resolver keeps each for as long as its
/// own time to live says, so a machine that moved is found where it is now, and a name looked up again is never one this
/// tool kept past its truth.
/// </remarks>
/// <param name="lookup">How a name is looked up.</param>
/// <param name="clock">What the wait between lookups is measured on.</param>
/// <param name="pace">
/// The wait before the second lookup: the pace the tries go at, never a bound. Passed in so that a test measures the
/// retrying and not the waiting; production uses <see cref="DefaultRetryDelay"/>.
/// </param>
public sealed class HostAddressResolver(INameLookup lookup, TimeProvider clock, TimeSpan pace) : IHostAddressResolver
{
    /// <summary>
    /// The wait before a name is looked up again: long enough for an mDNS responder to answer a repeated query, as
    /// measured, and short beside any leg. A pace, never a bound: the lookups go on while each brings news.
    /// </summary>
    public static readonly TimeSpan DefaultRetryDelay = TimeSpan.FromMilliseconds(400);

    private readonly INameLookup _lookup = lookup;
    private readonly TimeProvider _clock = clock;
    private readonly TimeSpan _pace = pace;

    public async Task<AddressResolution> ResolveAsync(string address, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);

        // A literal address resolves to itself; asking the resolver about one would fail on a machine
        // with no resolver at all, which is exactly the machine an address was written out for.
        if (IPAddress.TryParse(address, out _))
        {
            return new AddressResolution(address, Attempts: 0, [address]);
        }

        var looked = await Retrying
            .RunAsync<IReadOnlyList<string>>(
                async (_, token) =>
                {
                    try
                    {
                        return await _lookup.LookupAsync(address, token).ConfigureAwait(false) is { Count: > 0 } found
                            ? new RetryTry<IReadOnlyList<string>>.Done(found)
                            : new RetryTry<IReadOnlyList<string>>.Failed("no answer came", "no answer");
                    }
                    catch (NoSuchNameException ex)
                    {
                        return new RetryTry<IReadOnlyList<string>>.Final(ex.Said);
                    }
                },
                new RetryWatch($"'{address}' to resolve", Waits.Told, _pace, Bound: null, _clock),
                cancellationToken)
            .ConfigureAwait(false);

        return looked.Kind switch
        {
            RetryEndKind.Done => new AddressResolution(address, looked.Tries, looked.Value!),
            RetryEndKind.Final => new AddressResolution(address, looked.Tries, [], looked.Failure),
            RetryEndKind.Cancelled => throw new OperationCanceledException(cancellationToken),
            _ => AddressResolution.Missed(address, looked.Tries),
        };
    }

    /// <summary>The one of <paramref name="addresses"/> a pin gives ssh: an IPv4 one where there is one.</summary>
    /// <remarks>
    /// IPv4 first: a name answered over mDNS often comes back with a link-local IPv6 address as well,
    /// which reaches the machine only through the interface its scope names.
    /// </remarks>
    internal static string Preferred(IReadOnlyList<string> addresses)
        => addresses.FirstOrDefault(address => IPAddress.TryParse(address, out var parsed) && parsed.AddressFamily == AddressFamily.InterNetwork)
            ?? addresses[0];

    /// <summary>What a host that does not resolve is reported as, with what to do about it.</summary>
    /// <param name="resolution">The failed resolution.</param>
    /// <param name="declared">
    /// The address the host's item declares, where ssh's own configuration gives it another name to look up
    /// - a HostName - and that name was the one looked up.
    /// </param>
    /// <param name="window">The window the host's <c>wakeWaitSeconds</c> gave the lookups, where it gave one.</param>
    public static string Unresolved(AddressResolution resolution, string? declared = null, TimeSpan? window = null)
    {
        ArgumentNullException.ThrowIfNull(resolution);

        var lookups = resolution.Attempts.ToString(CultureInfo.InvariantCulture)
            + (window is { } waited
                ? string.Create(CultureInfo.InvariantCulture, $" over the {waited.TotalSeconds:0} seconds wakeWaitSeconds gives it to wake")
                : string.Empty);

        // The name's own authority's answer, said as it is: no lookup changes it, and no window waits on it.
        var outcome = resolution.NoSuchName is { } said
            ? $"{said}, as its DNS server answered"
            : $"got no answer in {lookups} lookups";

        return declared is not null && !string.Equals(declared, resolution.Address, StringComparison.OrdinalIgnoreCase)
            ? $"'{resolution.Address}', the HostName ssh's own configuration gives '{declared}', {outcome}; check that the "
                + "machine is on, and that the HostName is a name this machine's network answers for"
            : $"'{resolution.Address}' {outcome}; check that the machine is on, "
                + "and that ADDRESS in its item's .env is the name this machine's network answers for";
    }
}
