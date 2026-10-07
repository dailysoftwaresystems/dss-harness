using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace RepoHarness.Core.Hosts;

/// <summary>
/// Looks a name up with whatever this machine resolves names with: DNS, mDNS for a <c>.local</c> name,
/// or the hosts file. A seam rather than an abstraction over networking: it exists so that the retry
/// and the cache above it are testable without a LAN.
/// </summary>
public interface INameLookup
{
    /// <summary>The addresses <paramref name="name"/> resolves to, empty when it resolves to none.</summary>
    /// <param name="name">The name to look up.</param>
    /// <param name="cancellationToken">Stops the lookup.</param>
    Task<IReadOnlyList<string>> LookupAsync(string name, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="INameLookup"/>
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
        catch (SocketException)
        {
            // A name that did not resolve is an answer, not a failure of this machine: the caller
            // retries, because one miss is normal for a name answered by the host itself.
            return [];
        }
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
public sealed record AddressResolution(string Address, int Attempts, IReadOnlyList<string> Addresses)
{
    /// <summary>Whether it names a machine this one can reach: it resolved to an address.</summary>
    public bool Resolved => Addresses.Count > 0;

    /// <summary>
    /// The address a pin gives ssh as its HostName - the literal itself, or an IPv4 one where the name resolved to
    /// any - or <see langword="null"/> where it resolved to none.
    /// </summary>
    public string? ResolvedTo => Resolved ? HostAddressResolver.Preferred(Addresses) : null;

    /// <summary>A name that resolved to nothing in <paramref name="attempts"/> lookups.</summary>
    /// <param name="address">The name looked up.</param>
    /// <param name="attempts">How many lookups it took.</param>
    public static AddressResolution Missed(string address, int attempts) => new(address, attempts, []);
}

/// <summary>
/// Looks an ssh host's name up before ssh is started, retrying the lookup and keeping the answer for a
/// short while, and says which of its addresses a pin would give ssh.
/// </summary>
/// <remarks>
/// Measured: a host on a DHCP LAN reached by an mDNS <c>.local</c> name fails a lookup as a matter of
/// course - the responder does not answer the first query and answers the second - and ssh then exits
/// with "Could not resolve hostname", which reads as a host that is switched off. So the name ssh would
/// look up is looked up here, with retries, and ssh is given the address it resolved to, as an
/// <see cref="SshPin"/> explains. A name that genuinely resolves to nothing is refused, with that as the
/// reason.
/// </remarks>
public interface IHostAddressResolver
{
    /// <summary>Resolves <paramref name="address"/>, retrying a miss.</summary>
    /// <param name="address">
    /// The name to look up: the HostName ssh's own configuration gives the host, or the address its item
    /// declares where ssh could not say.
    /// </param>
    /// <param name="cancellationToken">Stops the lookups.</param>
    Task<AddressResolution> ResolveAsync(string address, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves <paramref name="address"/> as <see cref="ResolveAsync"/> does, but past any answer kept from a moment
    /// ago, keeping what it finds in that answer's place: where the address the name had may have moved since.
    /// </summary>
    /// <param name="address">The name to look up, as <see cref="ResolveAsync"/> takes it.</param>
    /// <param name="cancellationToken">Stops the lookups.</param>
    /// <remarks>
    /// A name that resolves to nothing now leaves the answer kept as it was: a missed lookup is no news that the
    /// machine moved, and kept as a miss it would refuse the host to every connection opened in the half minute it
    /// is kept.
    /// </remarks>
    Task<AddressResolution> ResolveAgainAsync(string address, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IHostAddressResolver"/>
/// <param name="lookup">How a name is looked up.</param>
/// <param name="clock">What decides when a cached answer has aged out.</param>
/// <param name="retryDelay">
/// How long to wait between lookups. Passed in rather than fixed so that a test measures the retrying
/// and not the waiting; production uses <see cref="DefaultRetryDelay"/>.
/// </param>
public sealed class HostAddressResolver(INameLookup lookup, TimeProvider clock, TimeSpan retryDelay) : IHostAddressResolver
{
    /// <summary>
    /// How many lookups a name gets before it is reported as resolving to nothing. Three, because the
    /// measured failure is a single miss of an mDNS query: one retry already covers it, and the third
    /// attempt covers a miss on a LAN busy enough to drop two.
    /// </summary>
    public const int Attempts = 3;

    /// <summary>
    /// The wait between lookups. Long enough for an mDNS responder to answer a repeated query, and
    /// short enough that three misses cost a second rather than a leg's worth of time.
    /// </summary>
    public static readonly TimeSpan DefaultRetryDelay = TimeSpan.FromMilliseconds(400);

    /// <summary>
    /// How long an answer is reused. One command measures a host several times over, and paying the
    /// same miss for each is exactly the failure this exists to end; half a minute is far shorter than
    /// any DHCP lease, so a connection opened later in the same command finds a machine that moved. Each
    /// command is a process of its own, and keeps no answer past its end. A connection keeps the address
    /// it was given for as long as the address takes its calls, and lets ssh look the name up once it
    /// does not: see <see cref="SshPin.Drop"/>.
    /// </summary>
    public static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<string, (IReadOnlyList<string> Found, DateTimeOffset Until)> _answers =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly INameLookup _lookup = lookup;
    private readonly TimeProvider _clock = clock;
    private readonly TimeSpan _retryDelay = retryDelay;

    public Task<AddressResolution> ResolveAsync(string address, CancellationToken cancellationToken = default)
        => ResolveAsync(address, afresh: false, cancellationToken);

    public Task<AddressResolution> ResolveAgainAsync(string address, CancellationToken cancellationToken = default)
        => ResolveAsync(address, afresh: true, cancellationToken);

    private async Task<AddressResolution> ResolveAsync(string address, bool afresh, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);

        // A literal address resolves to itself; asking the resolver about one would fail on a machine
        // with no resolver at all, which is exactly the machine an address was written out for.
        if (IPAddress.TryParse(address, out _))
        {
            return new AddressResolution(address, Attempts: 0, [address]);
        }

        if (!afresh && _answers.TryGetValue(address, out var cached) && cached.Until > _clock.GetUtcNow())
        {
            return new AddressResolution(address, Attempts: 0, cached.Found);
        }

        for (var attempt = 1; attempt <= Attempts; attempt++)
        {
            if (attempt > 1)
            {
                await Task.Delay(_retryDelay, cancellationToken).ConfigureAwait(false);
            }

            if (await _lookup.LookupAsync(address, cancellationToken).ConfigureAwait(false) is { Count: > 0 } found)
            {
                _answers[address] = (found, _clock.GetUtcNow() + CacheLifetime);
                return new AddressResolution(address, attempt, found);
            }
        }

        // A miss is cached too, so that a command measuring several legs on one switched-off machine
        // does not pay three lookups for each of them; but not one a lookup made afresh finds, which
        // leaves the answer kept as it was.
        if (!afresh)
        {
            _answers[address] = ([], _clock.GetUtcNow() + CacheLifetime);
        }

        return AddressResolution.Missed(address, Attempts);
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

        return declared is not null && !string.Equals(declared, resolution.Address, StringComparison.OrdinalIgnoreCase)
            ? $"'{resolution.Address}', the HostName ssh's own configuration gives '{declared}', resolved to no address "
                + $"in {lookups} lookups; check that the machine is on, and that the HostName is a name this machine's "
                + "network answers for"
            : $"'{resolution.Address}' resolved to no address in {lookups} lookups; check that the machine is on, "
                + "and that ADDRESS in its item's .env is the name this machine's network answers for";
    }
}
