using System.Net;
using DssHarness.Core.Web;

namespace DssHarness.Tests;

/// <summary>
/// Which addresses the harness's own fetch connects to: public ones alone, an IPv4 address carried inside one of IPv6's
/// read as the address it carries.
/// </summary>
public sealed class WebAddressesTests
{
    /// <summary>Each range no public host holds is told by what it is; a public address, either family's, is told by none.</summary>
    [Theory]
    [InlineData("8.8.8.8", null)]
    [InlineData("1.1.1.1", null)]
    [InlineData("172.32.0.1", null)]
    [InlineData("100.128.0.1", null)]
    [InlineData("198.20.0.1", null)]
    [InlineData("2606:4700:4700::1111", null)]
    [InlineData("2001:4860:4860::8888", null)]
    [InlineData("0.0.0.0", "'this network'")]
    [InlineData("10.1.2.3", "a private range")]
    [InlineData("100.64.0.1", "the carrier-shared range")]
    [InlineData("127.0.0.1", "this machine (loopback)")]
    [InlineData("127.255.255.254", "this machine (loopback)")]
    [InlineData("169.254.169.254", "a link-local range, a cloud's metadata service among it")]
    [InlineData("172.16.0.1", "a private range")]
    [InlineData("172.31.255.255", "a private range")]
    [InlineData("192.0.0.170", "the protocols' own range")]
    [InlineData("192.0.2.1", "a range for documentation")]
    [InlineData("192.88.99.1", "the 6to4 relays' range")]
    [InlineData("192.168.1.1", "a private range")]
    [InlineData("198.19.255.1", "the benchmarking range")]
    [InlineData("198.51.100.1", "a range for documentation")]
    [InlineData("203.0.113.9", "a range for documentation")]
    [InlineData("224.0.0.1", "a multicast range")]
    [InlineData("255.255.255.255", "a reserved range")]
    [InlineData("::", "the unspecified address")]
    [InlineData("::1", "this machine (loopback)")]
    [InlineData("100::1", "the discard range")]
    [InlineData("2001::1", "the protocols' own range")]
    [InlineData("2001:db8::1", "a range for documentation")]
    [InlineData("2002:c0a8:101::1", "the 6to4 range")]
    [InlineData("fd00:ec2::254", "a unique local range, a cloud's metadata service among it")]
    [InlineData("fc00::1", "a unique local range, a cloud's metadata service among it")]
    [InlineData("fe80::1", "a link-local range")]
    [InlineData("fec0::1", "a site-local range")]
    [InlineData("ff02::1", "a multicast range")]
    public void EachRange_IsToldByWhatItIs(string address, string? what)
        => Assert.Equal(what, WebAddresses.NotPublic(IPAddress.Parse(address)));

    /// <summary>An IPv4 address mapped into IPv6, carried by NAT64's range or by the old compatible form, is read as the address it carries.</summary>
    [Theory]
    [InlineData("::ffff:127.0.0.1", "this machine (loopback)")]
    [InlineData("::ffff:169.254.169.254", "a link-local range, a cloud's metadata service among it")]
    [InlineData("::ffff:8.8.8.8", null)]
    [InlineData("64:ff9b::a00:1", "a private range")]
    [InlineData("64:ff9b::808:808", null)]
    [InlineData("::7f00:1", "this machine (loopback)")]
    [InlineData("::a9fe:a9fe", "a link-local range, a cloud's metadata service among it")]
    public void AnIPv4AddressCarriedInIPv6_IsReadAsTheAddressItCarries(string address, string? what)
        => Assert.Equal(what, WebAddresses.NotPublic(IPAddress.Parse(address)));
}
