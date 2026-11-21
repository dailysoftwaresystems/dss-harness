using DssHarness.Core.Hosts;

namespace DssHarness.Tests;

/// <summary>
/// A host reached by an mDNS name on a DHCP LAN misses a lookup as a matter of course, and ssh then
/// exits saying the name could not be resolved, which reads as a machine that is switched off. One
/// miss must never end a leg; a name its DNS server says does not exist ends the lookups at once; and no
/// answer is kept of the tool's own, since the system's resolver keeps each for as long as its time to live.
/// </summary>
public sealed class HostAddressResolverTests
{
    private const string Name = "build-box.local";

    [Fact]
    public async Task AMissedLookup_IsRetried_AndTheHostIsStillReached()
    {
        var lookup = new ScriptedLookup(attempt => attempt >= 2 ? ["192.0.2.10"] : []);
        var resolver = Resolver(lookup);

        var resolution = await resolver.ResolveAsync(Name, TestContext.Current.CancellationToken);

        Assert.True(resolution.Resolved);
        Assert.Equal(2, resolution.Attempts);
    }

    /// <summary>
    /// No answer is looked up again, and the same silence met twice ends the lookups: a third would only meet it again.
    /// Counted to a number of the code's own, a name got three lookups whatever the first two said.
    /// </summary>
    [Fact]
    public async Task NoAnswerTwice_EndsTheLookups_AndIsSaidAsNoAnswer()
    {
        var lookup = new ScriptedLookup(_ => []);
        var resolver = Resolver(lookup);

        var resolution = await resolver.ResolveAsync(Name, TestContext.Current.CancellationToken);

        Assert.False(resolution.Resolved);
        Assert.Null(resolution.NoSuchName);
        Assert.Equal(2, resolution.Attempts);
        Assert.Equal(2, lookup.Calls);
        Assert.Contains($"'{Name}' got no answer in 2 lookups", HostAddressResolver.Unresolved(resolution), StringComparison.Ordinal);
    }

    /// <summary>
    /// A name its DNS server answers does not exist ends the lookups at once, said as that answer: no lookup changes it.
    /// Retried as a miss, it was looked up three times and said as resolving to no address.
    /// </summary>
    [Fact]
    public async Task NxDomain_EndsAtOnce()
    {
        var lookup = new ScriptedLookup(_ => throw new NoSuchNameException("build.example.com", "does not exist"));

        var resolution = await Resolver(lookup).ResolveAsync("build.example.com", TestContext.Current.CancellationToken);

        Assert.False(resolution.Resolved);
        Assert.Equal(1, lookup.Calls);
        Assert.Equal(1, resolution.Attempts);
        Assert.Equal("does not exist", resolution.NoSuchName);
        Assert.StartsWith(
            "'build.example.com' does not exist, as its DNS server answered; check that the machine is on",
            HostAddressResolver.Unresolved(resolution),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Every resolution is a lookup of now: a name looked up twice is asked twice, so a machine whose lease moved is found
    /// where it is, and one that answered a moment ago is never refused for a miss kept from then.
    /// </summary>
    [Fact]
    public async Task EveryResolution_IsALookupOfNow_NoAnswerIsKept()
    {
        var lookup = new ScriptedLookup(call => call == 1 ? ["192.0.2.10"] : ["192.0.2.23"]);
        var resolver = Resolver(lookup);

        var first = await resolver.ResolveAsync(Name, TestContext.Current.CancellationToken);
        var second = await resolver.ResolveAsync(Name, TestContext.Current.CancellationToken);

        Assert.Equal(["192.0.2.10"], first.Addresses);
        Assert.Equal(["192.0.2.23"], second.Addresses);
        Assert.Equal(1, second.Attempts);
        Assert.Equal(2, lookup.Calls);
    }

    [Fact]
    public async Task ALiteralAddress_IsNeverLookedUp()
    {
        // A machine with no resolver at all is exactly the machine an address was written out for.
        var lookup = new ScriptedLookup(_ => throw new InvalidOperationException("A literal address was looked up."));

        var resolution = await Resolver(lookup).ResolveAsync("192.0.2.10", TestContext.Current.CancellationToken);

        Assert.True(resolution.Resolved);
        Assert.Equal(0, lookup.Calls);
    }

    /// <summary>
    /// A name resolves to an IPv4 address where it has one, since a link-local IPv6 one reaches the machine
    /// only through the interface its scope names, and a literal to itself; a name that resolved to none has none.
    /// Every address it resolved to is kept beside it, as any of them is one ssh may dial and name.
    /// </summary>
    [Theory]
    [InlineData(new[] { "fe80::1%12", "192.0.2.10" }, "192.0.2.10")]
    [InlineData(new[] { "fe80::1%12" }, "fe80::1%12")]
    [InlineData(new string[0], null)]
    public async Task ANameResolvesToAnIpv4AddressFirst_AndALiteralToItself(string[] addresses, string? resolvedTo)
    {
        var lookup = new ScriptedLookup(_ => addresses);
        var resolver = Resolver(lookup);

        var first = await resolver.ResolveAsync(Name, TestContext.Current.CancellationToken);
        var literal = await resolver.ResolveAsync("198.51.100.7", TestContext.Current.CancellationToken);

        Assert.Equal(resolvedTo, first.ResolvedTo);
        Assert.Equal("198.51.100.7", literal.ResolvedTo);
        Assert.Equal(addresses, first.Addresses);
        Assert.Equal(["198.51.100.7"], literal.Addresses);
    }

    /// <summary>
    /// Only a name a unicast DNS server answers with authority is ever said not to exist: one of several labels outside
    /// <c>.local</c>. mDNS, LLMNR and NetBIOS answer only for themselves, so a miss of one of their names is no answer.
    /// </summary>
    [Theory]
    [InlineData("build.example.com", true)]
    [InlineData("build.example.com.", true)]
    [InlineData("build-box.local", false)]
    [InlineData("build-box.LOCAL.", false)]
    [InlineData("build-box", false)]
    public void OnlyAUnicastDnsName_IsAnsweredWithAuthority(string name, bool authoritative)
        => Assert.Equal(authoritative, DnsNameLookup.Authoritative(name));

    private static HostAddressResolver Resolver(INameLookup lookup)
        => new(lookup, TimeProvider.System, TimeSpan.FromTicks(1));

    /// <summary>Answers each lookup by its number, so a miss followed by a hit is exactly expressible.</summary>
    private sealed class ScriptedLookup(Func<int, IReadOnlyList<string>> answer) : INameLookup
    {
        public int Calls { get; private set; }

        public Task<IReadOnlyList<string>> LookupAsync(string name, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(answer(Calls));
        }
    }
}
