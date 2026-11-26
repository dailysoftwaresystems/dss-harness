using DssHarness.Core.Ci;
using DssHarness.Core.Execution;
using DssHarness.Core.Git;
using DssHarness.Core.Processes;
using NSubstitute;

namespace DssHarness.Tests;

/// <summary>
/// The machine's gh login, as its one owner holds it: read once per engine session, read again before the expiry the host
/// told of it passes - once for that expiry - and read again once where the host refused it, never twice for one refusal;
/// its token never in what a login prints.
/// </summary>
public sealed class GhLoginsTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// Inside an engine session the login is read once, whoever asks and however often - the session holds one owner of it
    /// for every step; outside a session nothing is held past its holder, and each holder reads it of its own.
    /// </summary>
    [Fact]
    public async Task ALogin_IsReadOncePerEngineSession()
    {
        var (gh, asked) = Gh("gho_one\n");

        using (EngineSession.Begin())
        {
            var first = GhLogins.For(gh);

            Assert.Same(first, GhLogins.For(gh));
            Assert.Equal("gho_one", (await first.ReadAsync("/repo", "github.com", Token)).Token);
            Assert.Equal("gho_one", (await GhLogins.For(gh).ReadAsync("/repo", "github.com", Token)).Token);
            Assert.Single(asked);
        }

        // The session ended: what it held is gone, and a holder outside one is a caller's own.
        Assert.NotSame(GhLogins.For(gh), GhLogins.For(gh));
        _ = await GhLogins.For(gh).ReadAsync("/repo", "github.com", Token);
        _ = await GhLogins.For(gh).ReadAsync("/repo", "github.com", Token);

        Assert.Equal(["auth token --hostname github.com", "auth token --hostname github.com", "auth token --hostname github.com"], asked.Select(request => string.Join(' ', request.Arguments)));
    }

    /// <summary>
    /// The expiry the host told of the token held is heeded: a reading less than a minute before it reads the token again,
    /// before it is used; where gh answers the same token, it is read again for that expiry no more, and a token read in its
    /// place is held with no expiry until its host tells one. An expiry told of another token than the one held is not kept.
    /// </summary>
    [Fact]
    public async Task AnExpiredToken_IsReadAgainBeforeUse_OnceForItsExpiry()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 11, 26, 12, 0, 0, TimeSpan.Zero));
        var (gh, asked) = Gh("gho_old\n", "gho_old\n", "gho_new\n");
        var logins = new GhLogins(gh, clock);

        Assert.Equal("gho_old", (await logins.ReadAsync("/repo", "github.com", Token)).Token);

        logins.Expires("github.com", "gho_other", clock.GetUtcNow().AddMinutes(2));
        logins.Expires("github.com", "gho_old", clock.GetUtcNow().AddMinutes(2));

        // More than a minute before it: held as it is.
        Assert.Equal("gho_old", (await logins.ReadAsync("/repo", "github.com", Token)).Token);
        Assert.Single(asked);

        // Within the minute: read again, gh answering the same token, which is then held for that expiry without asking again.
        clock.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal(("gho_old", clock.GetUtcNow().AddSeconds(59)), ((await logins.ReadAsync("/repo", "github.com", Token)).Token, (await logins.ReadAsync("/repo", "github.com", Token)).Expiry!.Value));
        Assert.Equal(2, asked.Count);

        // A later expiry told of it is heeded again, and gh's newer token taken in its place, its expiry not known.
        logins.Expires("github.com", "gho_old", clock.GetUtcNow().AddSeconds(30));
        var renewed = await logins.ReadAsync("/repo", "github.com", Token);

        Assert.Equal(("gho_new", (DateTimeOffset?)null), (renewed.Token, renewed.Expiry));
        Assert.Equal("gho_new", (await logins.ReadAsync("/repo", "github.com", Token)).Token);
        Assert.Equal(3, asked.Count);
    }

    /// <summary>
    /// A token the host refused is read again where it is still the one held; where another asking read a newer one since,
    /// that one is answered and gh is not asked: one refusal, one reading.
    /// </summary>
    [Fact]
    public async Task ARefusedToken_IsReadAgainOnce()
    {
        var (gh, asked) = Gh("gho_old\n", "gho_new\n", "gho_newer\n");
        var logins = new GhLogins(gh);

        _ = await logins.ReadAsync("/repo", "github.com", Token);

        Assert.Equal("gho_new", (await logins.RenewAsync("/repo", "github.com", "gho_old", Token)).Token);
        Assert.Equal("gho_new", (await logins.RenewAsync("/repo", "github.com", "gho_old", Token)).Token);
        Assert.Equal(2, asked.Count);

        Assert.Equal("gho_newer", (await logins.RenewAsync("/repo", "github.com", "gho_new", Token)).Token);
        Assert.Equal(3, asked.Count);
    }

    /// <summary>The expiry GitHub's API names, as it writes it, is read; anything else names none.</summary>
    [Theory]
    [InlineData("2026-12-01 10:00:00 UTC", "2026-12-01T10:00:00+00:00")]
    [InlineData("2026-12-01 10:00:00 +0100", "2026-12-01T10:00:00+01:00")]
    [InlineData("2026-12-01 10:00:00 -03:00", "2026-12-01T10:00:00-03:00")]
    [InlineData("tomorrow", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void TheExpiry_IsReadAsTheHostWritesIt(string? said, string? expected)
        => Assert.Equal(expected is null ? null : DateTimeOffset.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), GhLogins.ExpiryOf(said));

    /// <summary>A login says whether it holds a token, never the token: no text made of it - a log line, a message - holds it.</summary>
    [Fact]
    public void ALogin_NeverPrintsItsToken()
    {
        Assert.Equal("a token", new GhLogins.Login("gho_secret", null).ToString());
        Assert.Equal("no token: signed out", new GhLogins.Login(null, "signed out").ToString());
        Assert.DoesNotContain("gho_secret", $"{new GhLogins.Login("gho_secret", null) with { Expiry = DateTimeOffset.UnixEpoch }}", StringComparison.Ordinal);
    }

    /// <summary>A gh that answers each of <paramref name="tokens"/> in turn, the last again after them, each asking kept.</summary>
    private static (GhProcess Gh, List<ProcessRequest> Asked) Gh(params string[] tokens)
    {
        var asked = new List<ProcessRequest>();
        var runner = Substitute.For<IProcessRunner>();
        var answers = tokens.Select(token => Task.FromResult(new ProcessResult(0, token, string.Empty, TimeSpan.Zero, TimedOut: false))).ToArray();

        runner.FindExecutable(GhProcess.Executable).Returns(GhProcess.Executable);
        runner.RunAsync(Arg.Do<ProcessRequest>(asked.Add), Arg.Any<CancellationToken>()).Returns(answers[0], answers[1..]);

        return (new GhProcess(runner, GitLocalVariables.Fixed([]), _ => null), asked);
    }
}
