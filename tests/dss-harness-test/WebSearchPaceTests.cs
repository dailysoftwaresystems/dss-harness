using DssHarness.Core.FileSystem;
using DssHarness.Core.Platform;
using DssHarness.Core.Web;
using DssHarness.Core.Web.Search;
using static DssHarness.Tests.WebSearchFixtures;

namespace DssHarness.Tests;

/// <summary>
/// The pace of each search source on the machine, kept in one record every process reads: a minute's requests waited past,
/// a day's spent told as a hold, a pause a source asked for held until its time and never shortened.
/// </summary>
public sealed class WebSearchPaceTests
{
    /// <summary>
    /// The record is the machine's, named as its other records are: where the machine is told only by its name, that is
    /// said with why, since a command started after the name changes keeps a record of its own.
    /// </summary>
    [Fact]
    public void TheRecord_IsTheMachines_SaidWhereItIsNamedByItsName()
    {
        var named = new HarnessFactory();
        var platform = NSubstitute.Substitute.For<IHostPlatform>();
        NSubstitute.SubstituteExtensions.Returns(platform.MachineId, new MachineIdentity("build-box", "neither /etc/machine-id nor /var/lib/dbus/machine-id holds one"));

        var path = WebSearchPace.PathFor(platform, named.Output);

        Assert.Equal("web-search-build-box.json", Path.GetFileName(path));
        Assert.Contains(
            "This machine's search sources' paces are recorded under its name, 'build-box', since neither /etc/machine-id nor "
            + "/var/lib/dbus/machine-id holds one; a command started after that name changes keeps a record of its own, and "
            + "paces its search sources beside the ones recorded under the old.",
            named.StandardOutput.ToString(),
            StringComparison.Ordinal);
    }

    /// <summary>A source asked past its perMinute waits for the oldest request of its minute to be a minute old, then is counted.</summary>
    [Fact]
    public async Task AMinuteSpent_IsWaitedPast()
    {
        using var directory = new TempDirectory();
        var clock = new ManualClock(At);
        var waited = new List<TimeSpan>();
        var pace = Pace(directory, clock, waited);
        var source = Source("so", """{ "kind": "feed", "url": "https://a.test/?q={query}", "perMinute": 2 }""");

        Assert.Null(await pace.TurnAsync(source, TestContext.Current.CancellationToken));
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Null(await pace.TurnAsync(source, TestContext.Current.CancellationToken));
        Assert.Empty(waited);

        Assert.Null(await pace.TurnAsync(source, TestContext.Current.CancellationToken));
        Assert.Equal([TimeSpan.FromSeconds(50)], waited);

        // Another source's minute is its own.
        Assert.Null(await pace.TurnAsync(Source("other", """{ "kind": "feed", "url": "https://b.test/?q={query}", "perMinute": 1 }"""), TestContext.Current.CancellationToken));
        Assert.Single(waited);
    }

    /// <summary>A source asked past its perDay is held until the end of its oldest hour is a day old, nothing counted; then asked again.</summary>
    [Fact]
    public async Task ADaySpent_IsAHold_UntilItsOldestHourIsADayOld()
    {
        using var directory = new TempDirectory();
        var clock = new ManualClock(At.AddMinutes(20));
        var pace = Pace(directory, clock, []);
        var source = Source("so", """{ "kind": "feed", "url": "https://a.test/?q={query}", "perDay": 3, "perMinute": 600 }""");

        Assert.Null(await pace.TurnAsync(source, TestContext.Current.CancellationToken));
        clock.Advance(TimeSpan.FromHours(2));
        Assert.Null(await pace.TurnAsync(source, TestContext.Current.CancellationToken));
        Assert.Null(await pace.TurnAsync(source, TestContext.Current.CancellationToken));

        var hold = await pace.TurnAsync(source, TestContext.Current.CancellationToken);

        Assert.NotNull(hold);
        Assert.False(hold.Paused);
        Assert.Equal(At.AddDays(1).AddHours(1), hold.Until);
        Assert.Equal($"its perDay of 3 requests on this machine is spent until {WebRateLimit.Said(hold.Until)}", hold.Why);
        Assert.Null(pace.Held("so"));

        // Held until the time it said, not a moment less; then the first hour has left the day, and one request is free
        // again while the other two still count.
        clock.Advance(hold.Until - clock.GetUtcNow() - TimeSpan.FromSeconds(1));
        Assert.Equal(hold.Until, (await pace.TurnAsync(source, TestContext.Current.CancellationToken))?.Until);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Null(await pace.TurnAsync(source, TestContext.Current.CancellationToken));
        Assert.NotNull(await pace.TurnAsync(source, TestContext.Current.CancellationToken));
    }

    /// <summary>A pause a source asked for holds it until its time, a later one extends it, a sooner one never shortens it.</summary>
    [Fact]
    public async Task APause_HoldsUntilItsTime_AndIsNeverShortened()
    {
        using var directory = new TempDirectory();
        var clock = new ManualClock(At);
        var pace = Pace(directory, clock, []);
        var source = Source("gh", """{ "kind": "feed", "url": "https://a.test/?q={query}" }""");

        pace.Pause("gh", At.AddSeconds(30), "it answered 429, and asks to wait 30 s");
        pace.Pause("gh", At.AddSeconds(10), "it answered 429, and asks to wait 10 s");

        var hold = await pace.TurnAsync(source, TestContext.Current.CancellationToken);

        Assert.Equal(new WebSearchHold(At.AddSeconds(30), "it answered 429, and asks to wait 30 s", Paused: true), hold);
        Assert.Equal(hold, pace.Held("gh"));

        pace.Pause("gh", At.AddSeconds(90), "a body's backoff of 90 s");
        Assert.Equal(At.AddSeconds(90), pace.Held("gh")!.Until);

        clock.Advance(TimeSpan.FromSeconds(90));
        Assert.Null(pace.Held("gh"));
        Assert.Null(await pace.TurnAsync(source, TestContext.Current.CancellationToken));
    }

    /// <summary>Two processes keep one record: a turn one takes counts against the other's, and a pause one keeps holds the other.</summary>
    [Fact]
    public async Task TwoPaces_OfOneRecord_ShareItsCounts()
    {
        using var directory = new TempDirectory();
        var clock = new ManualClock(At);
        var waited = new List<TimeSpan>();
        var one = Pace(directory, clock, waited);
        var two = Pace(directory, clock, waited);
        var source = Source("so", """{ "kind": "feed", "url": "https://a.test/?q={query}", "perMinute": 1 }""");

        Assert.Null(await one.TurnAsync(source, TestContext.Current.CancellationToken));
        Assert.Null(await two.TurnAsync(source, TestContext.Current.CancellationToken));
        Assert.Equal([TimeSpan.FromMinutes(1)], waited);

        one.Pause("so", clock.GetUtcNow().AddMinutes(5), "it answered 503 with Retry-After");
        Assert.True((await two.TurnAsync(source, TestContext.Current.CancellationToken))!.Paused);
    }

    /// <summary>What the record holds is the last day's: a source with nothing left in it is dropped from the file.</summary>
    [Fact]
    public async Task TheRecord_KeepsTheLastDayAlone()
    {
        using var directory = new TempDirectory();
        var clock = new ManualClock(At);
        var pace = Pace(directory, clock, []);

        Assert.Null(await pace.TurnAsync(Source("old-source", """{ "kind": "feed", "url": "https://a.test/?q={query}" }"""), TestContext.Current.CancellationToken));
        Assert.Contains("old-source", File.ReadAllText(directory.Combine("pace.json")), StringComparison.Ordinal);

        clock.Advance(TimeSpan.FromDays(1) + TimeSpan.FromHours(1));
        Assert.Null(await pace.TurnAsync(Source("new-source", """{ "kind": "feed", "url": "https://a.test/?q={query}" }"""), TestContext.Current.CancellationToken));

        var record = File.ReadAllText(directory.Combine("pace.json"));

        Assert.DoesNotContain("old-source", record, StringComparison.Ordinal);
        Assert.Contains("new-source", record, StringComparison.Ordinal);
    }

    /// <summary>A pace over the record <c>pace.json</c> in <paramref name="directory"/>, its waits moving <paramref name="clock"/> and kept in <paramref name="waited"/>.</summary>
    private static WebSearchPace Pace(TempDirectory directory, ManualClock clock, List<TimeSpan> waited)
        => new(
            new PhysicalFileSystem(FilePermissionsFactory.Create()),
            () => directory.Combine("pace.json"),
            clock,
            (wait, _) =>
            {
                waited.Add(wait);
                clock.Advance(wait);
                return Task.CompletedTask;
            });
}
