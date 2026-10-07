using RepoHarness.Core.Hosts;

namespace RepoHarness.Tests;

/// <summary>
/// A host reached by an mDNS name on a DHCP LAN misses a lookup as a matter of course, and ssh then
/// exits saying the name could not be resolved, which reads as a machine that is switched off. One
/// miss must never end a leg.
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

    [Fact]
    public async Task ANameThatResolvesToNothing_IsRefused_AfterEveryAttempt()
    {
        var lookup = new ScriptedLookup(_ => []);
        var resolver = Resolver(lookup);

        var resolution = await resolver.ResolveAsync(Name, TestContext.Current.CancellationToken);

        Assert.False(resolution.Resolved);
        Assert.Equal(HostAddressResolver.Attempts, resolution.Attempts);
        Assert.Equal(HostAddressResolver.Attempts, lookup.Calls);
        Assert.Contains("resolved to no address in 3 lookups", HostAddressResolver.Unresolved(resolution), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAnswer_IsKeptBriefly_SoOneCommandDoesNotPayTheSameMissForEveryProbe()
    {
        var clock = new ManualClock();
        var lookup = new ScriptedLookup(_ => ["192.0.2.10"]);
        var resolver = new HostAddressResolver(lookup, clock, TimeSpan.Zero);

        _ = await resolver.ResolveAsync(Name, TestContext.Current.CancellationToken);
        var second = await resolver.ResolveAsync(Name, TestContext.Current.CancellationToken);

        Assert.True(second.Resolved);
        Assert.Equal(0, second.Attempts);
        Assert.Equal(1, lookup.Calls);

        // Past the lifetime it is looked up again, so a machine whose lease moved is found where it is.
        clock.Advance(HostAddressResolver.CacheLifetime + TimeSpan.FromSeconds(1));
        _ = await resolver.ResolveAsync(Name, TestContext.Current.CancellationToken);
        Assert.Equal(2, lookup.Calls);
    }

    [Fact]
    public async Task AMissIsKeptToo_SoASwitchedOffMachineIsNotLookedUpForEveryLeg()
    {
        var lookup = new ScriptedLookup(_ => []);
        var resolver = Resolver(lookup);

        _ = await resolver.ResolveAsync(Name, TestContext.Current.CancellationToken);
        _ = await resolver.ResolveAsync(Name, TestContext.Current.CancellationToken);

        Assert.Equal(HostAddressResolver.Attempts, lookup.Calls);
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
    /// only through the interface its scope names, and a literal to itself; one kept briefly keeps its
    /// address, and a name that resolved to none has none. Every address it resolved to is kept beside it,
    /// as any of them is one ssh may dial and name.
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
        var kept = await resolver.ResolveAsync(Name, TestContext.Current.CancellationToken);
        var literal = await resolver.ResolveAsync("198.51.100.7", TestContext.Current.CancellationToken);

        Assert.Equal(resolvedTo, first.ResolvedTo);
        Assert.Equal(resolvedTo, kept.ResolvedTo);
        Assert.Equal(0, kept.Attempts);
        Assert.Equal("198.51.100.7", literal.ResolvedTo);
        Assert.Equal(addresses, first.Addresses);
        Assert.Equal(addresses, kept.Addresses);
        Assert.Equal(["198.51.100.7"], literal.Addresses);
    }

    /// <summary>
    /// A name looked up afresh is looked up past the answer kept, and what it finds is kept in that answer's place;
    /// a lookup afresh that finds nothing leaves the answer kept as it was, since a miss is no news that the
    /// machine moved.
    /// </summary>
    [Fact]
    public async Task ANameLookedUpAfresh_LooksPastTheAnswerKept_AndKeepsANewOneButNoMiss()
    {
        var lookup = new ScriptedLookup(call => call switch
        {
            1 => ["192.0.2.10"],
            <= 4 => [],
            _ => ["192.0.2.23"],
        });
        var resolver = Resolver(lookup);

        await resolver.ResolveAsync(Name, TestContext.Current.CancellationToken);
        var missed = await resolver.ResolveAgainAsync(Name, TestContext.Current.CancellationToken);
        var keptAfterTheMiss = await resolver.ResolveAsync(Name, TestContext.Current.CancellationToken);
        var moved = await resolver.ResolveAgainAsync(Name, TestContext.Current.CancellationToken);
        var keptAfterTheMove = await resolver.ResolveAsync(Name, TestContext.Current.CancellationToken);

        Assert.False(missed.Resolved);
        Assert.Equal(["192.0.2.10"], keptAfterTheMiss.Addresses);
        Assert.Equal(["192.0.2.23"], moved.Addresses);
        Assert.Equal(["192.0.2.23"], keptAfterTheMove.Addresses);
        Assert.Equal(5, lookup.Calls);
    }

    private static HostAddressResolver Resolver(INameLookup lookup)
        => new(lookup, new ManualClock(), TimeSpan.Zero);

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
