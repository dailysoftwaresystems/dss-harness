using DssHarness.Core.Ci;
using DssHarness.Core.Git;
using DssHarness.Core.Processes;
using DssHarness.Core.Repository;
using DssHarness.Core.Secrets;
using DssHarness.Core.Web.Search;
using NSubstitute;
using static DssHarness.Tests.WebSearchFixtures;

namespace DssHarness.Tests;

/// <summary>
/// The keys of the web search's sources: a variable of the slot read by the slot's reader, the machine's gh login read
/// once for every source that takes it, and a key not there said - never a failure of the step.
/// </summary>
public sealed class WebSearchKeysTests
{
    private static readonly IReadOnlyList<WebSearchSource> Sources =
    [
        Source("alpha", """{ "kind": "feed", "url": "https://a.test/?q={query}", "credential": { "from": "env", "name": "ALPHA_KEY", "query": "key" } }"""),
        Source("beta", """{ "kind": "feed", "url": "https://b.test/?q={query}", "credential": { "from": "env", "name": "BETA_KEY", "header": "X-Key" } }"""),
        Source("repos", """{ "kind": "feed", "url": "https://c.test/?q={query}", "credential": { "from": "gh" } }"""),
        Source("issues", """{ "kind": "feed", "url": "https://d.test/?q={query}", "credential": { "from": "gh" } }"""),
        Source("plain", """{ "kind": "feed", "url": "https://e.test/?q={query}" }"""),
        Source("enterprise", """{ "kind": "feed", "url": "https://ghe.example.test/?q={query}", "credential": { "from": "gh", "host": "GHE.example.test" } }"""),
    ];

    private static readonly IReadOnlyList<AppointedKey> Appointed =
    [
        new("ALPHA_KEY", "web-search source 'alpha'", "the key web-search source 'alpha' is sent, where it is there", "credential.name"),
        new("BETA_KEY", "web-search source 'beta'", "the key web-search source 'beta' is sent, where it is there", "credential.name"),
    ];

    /// <summary>
    /// A key of the slot is read from it, one not there is said; the gh login of each host is read once for every source of
    /// that host, never gh's default host's; a source naming none has none.
    /// </summary>
    [Fact]
    public async Task EachKey_IsReadWhereItsCredentialSays_TheLoginOnce()
    {
        using var temp = new TempDirectory();
        temp.WriteFile(".harness-config/workflows/.env/keys.env", "ALPHA_KEY=k-alpha\n");
        var requests = new List<ProcessRequest>();

        var keys = await Keys(requests, new ProcessResult(0, "gho_token\n", string.Empty, TimeSpan.Zero, TimedOut: false))
            .ReadAsync(new HarnessLayout(temp.Path, temp.Path), Appointed, Sources, TestContext.Current.CancellationToken);

        Assert.Equal(["alpha", "beta", "repos", "issues", "enterprise"], keys.Keys);
        Assert.Equal(new WebSearchKey("k-alpha", null), keys["alpha"]);
        Assert.Equal(
            new WebSearchKey(null, "no file under '.harness-config/workflows/.env/' declares 'BETA_KEY', the name web-search source 'beta' appoints for its key "
                + "(its credential.name); add BETA_KEY= to a file there, holding the key web-search source 'beta' is sent, where it is there "
                + "(it is read from this slot and nowhere else)"),
            keys["beta"]);
        Assert.Equal(new WebSearchKey("gho_token", null), keys["repos"]);
        Assert.Same(keys["repos"], keys["issues"]);
        Assert.Equal(["auth token --hostname github.com", "auth token --hostname ghe.example.test"], requests.Select(request => string.Join(' ', request.Arguments)));
    }

    /// <summary>A login gh cannot give - signed out, or gh failing - is said, and the sources are asked without it.</summary>
    [Fact]
    public async Task ALoginGhCannotGive_IsSaid()
    {
        using var temp = new TempDirectory();
        var layout = new HarnessLayout(temp.Path, temp.Path);

        var signedOut = await Keys([], new ProcessResult(0, "\n", string.Empty, TimeSpan.Zero, TimedOut: false))
            .ReadAsync(layout, Appointed, [Sources[2]], TestContext.Current.CancellationToken);
        var failed = await Keys([], new ProcessResult(1, string.Empty, "not logged into any GitHub hosts\n", TimeSpan.Zero, TimedOut: false))
            .ReadAsync(layout, Appointed, [Sources[2]], TestContext.Current.CancellationToken);

        Assert.Equal(new WebSearchKey(null, "gh answered no token: the machine's gh login for github.com is signed out"), signedOut["repos"]);
        Assert.Equal(new WebSearchKey(null, "gh auth token --hostname github.com failed, so the source is asked without the machine's gh login: not logged into any GitHub hosts"), failed["repos"]);
    }

    /// <summary>The keys over the slot of the store's tests and a gh that answers <paramref name="answer"/>, its requests kept in <paramref name="requests"/>.</summary>
    private static WebSearchKeys Keys(List<ProcessRequest> requests, ProcessResult answer)
    {
        var runner = Substitute.For<IProcessRunner>();

        runner.FindExecutable(GhProcess.Executable).Returns(GhProcess.Executable);
        runner.RunAsync(Arg.Do<ProcessRequest>(requests.Add), Arg.Any<CancellationToken>()).Returns(Task.FromResult(answer));

        return new WebSearchKeys(AgentKeyStoreTests.Store().Store, new GhProcess(runner, GitLocalVariables.Fixed([]), _ => null));
    }
}
