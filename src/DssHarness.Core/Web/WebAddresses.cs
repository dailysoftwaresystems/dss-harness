using System.Net;
using System.Net.Sockets;

namespace DssHarness.Core.Web;

/// <summary>
/// Which addresses the harness's own fetch connects to: public ones alone - never this machine, its network, a range no
/// public host holds, or a cloud's metadata service - so a page an agent asks for cannot reach what only this machine
/// reaches. The one rule of it, asked of every address a host resolves to and of the one a connection was in fact made to.
/// </summary>
public static class WebAddresses
{
    /// <summary>The ranges no public host holds, IPv4's, each with what it is.</summary>
    private static readonly (byte[] Prefix, int Bits, string What)[] Version4 =
    [
        ([0, 0, 0, 0], 8, "'this network'"),
        ([10, 0, 0, 0], 8, "a private range"),
        ([100, 64, 0, 0], 10, "the carrier-shared range"),
        ([127, 0, 0, 0], 8, "this machine (loopback)"),
        ([169, 254, 0, 0], 16, "a link-local range, a cloud's metadata service among it"),
        ([172, 16, 0, 0], 12, "a private range"),
        ([192, 0, 0, 0], 24, "the protocols' own range"),
        ([192, 0, 2, 0], 24, "a range for documentation"),
        ([192, 88, 99, 0], 24, "the 6to4 relays' range"),
        ([192, 168, 0, 0], 16, "a private range"),
        ([198, 18, 0, 0], 15, "the benchmarking range"),
        ([198, 51, 100, 0], 24, "a range for documentation"),
        ([203, 0, 113, 0], 24, "a range for documentation"),
        ([224, 0, 0, 0], 4, "a multicast range"),
        ([240, 0, 0, 0], 4, "a reserved range"),
    ];

    /// <summary>The ranges no public host holds, IPv6's, each with what it is; one that embeds an IPv4 address is read by it.</summary>
    private static readonly (byte[] Prefix, int Bits, string What)[] Version6 =
    [
        (new byte[16], 128, "the unspecified address"),
        ([.. new byte[15], 1], 128, "this machine (loopback)"),
        ([0x01, 0x00, .. new byte[14]], 64, "the discard range"),
        ([0x20, 0x01, 0x00, 0x00, .. new byte[12]], 23, "the protocols' own range"),
        ([0x20, 0x01, 0x0D, 0xB8, .. new byte[12]], 32, "a range for documentation"),
        ([0x20, 0x02, .. new byte[14]], 16, "the 6to4 range"),
        ([0xFC, .. new byte[15]], 7, "a unique local range, a cloud's metadata service among it"),
        ([0xFE, 0x80, .. new byte[14]], 10, "a link-local range"),
        ([0xFE, 0xC0, .. new byte[14]], 10, "a site-local range"),
        ([0xFF, .. new byte[15]], 8, "a multicast range"),
    ];

    /// <summary>The prefix of NAT64's well-known range, whose last four bytes are an IPv4 address.</summary>
    private static readonly byte[] Nat64 = [0x00, 0x64, 0xFF, 0x9B, .. new byte[12]];

    /// <summary>Why <paramref name="address"/> is no public address, in a few words; null where it is one.</summary>
    /// <param name="address">The address.</param>
    public static string? NotPublic(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        if (address.IsIPv4MappedToIPv6)
        {
            return NotPublic(address.MapToIPv4());
        }

        var bytes = address.GetAddressBytes();

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return Version4.FirstOrDefault(range => Within(bytes, range.Prefix, range.Bits)).What;
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return "no internet address";
        }

        // An IPv4 address carried inside one of IPv6's: read as the address it carries, as the network reaches it.
        if (Within(bytes, Nat64, 96) || (bytes.AsSpan(0, 12).IndexOfAnyExcept((byte)0) < 0 && (bytes[12] | bytes[13]) != 0))
        {
            return NotPublic(new IPAddress(bytes[12..]));
        }

        return Version6.FirstOrDefault(range => Within(bytes, range.Prefix, range.Bits)).What;
    }

    /// <summary>Whether <paramref name="bytes"/> stand in the range of <paramref name="prefix"/>'s first <paramref name="bits"/> bits.</summary>
    private static bool Within(byte[] bytes, byte[] prefix, int bits)
    {
        if (bytes.Length != prefix.Length)
        {
            return false;
        }

        var whole = bits / 8;

        if (!bytes.AsSpan(0, whole).SequenceEqual(prefix.AsSpan(0, whole)))
        {
            return false;
        }

        var rest = bits % 8;

        if (rest == 0)
        {
            return true;
        }

        var mask = (byte)(0xFF << (8 - rest));

        return (bytes[whole] & mask) == (prefix[whole] & mask);
    }
}

/// <summary>Finds the addresses a host's name stands for: the system's own lookup, which a test replaces.</summary>
public interface IWebNames
{
    /// <summary>Every address <paramref name="host"/> resolves to.</summary>
    /// <param name="host">A host's name.</param>
    /// <param name="cancellationToken">Stops the lookup.</param>
    Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken);
}

/// <summary>The system's own lookup of a name.</summary>
public sealed class SystemWebNames : IWebNames
{
    /// <inheritdoc/>
    public async Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken)
        => await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
}
