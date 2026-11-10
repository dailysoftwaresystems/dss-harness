using DssHarness.Core.Agents;
using DssHarness.Core.Agents.ClaudeCode;
using DssHarness.Core.Configuration;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Orchestration.Flow;
using DssHarness.Core.Platform;
using DssHarness.Core.Results;
using DssHarness.Core.Secrets;

namespace DssHarness.Tests;

/// <summary>
/// The one owner of what a Claude Code that is not ready does to a launch, for every asker alike - a step, a side-task,
/// the advisor and the reporter: it refuses the asker only where the entry that would serve first starts it, and where
/// only a later entry does, that entry is passed over when its turn comes with the finder's own words as why.
/// </summary>
public sealed class ReasonLaunchTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>An API model's entry, and Claude Code's.</summary>
    private static readonly AgentRunEntry Api = new("glm-5.3", "zai", "glm", "ZAI_API_KEY", null, Usable: true);

    private static readonly AgentRunEntry Claude = new("claude-sonnet-5-5", "anthropic", ClaudeCodeClient.ClientName, null, null, Usable: true);

    /// <summary>
    /// With npm off the PATH and an order whose first entry an API client serves, the launch is not refused: its order
    /// passes the Claude Code entry over, the finder's own words - npm off the PATH and what to do - as why, so the walk
    /// says the real cause when its turn comes; whether the program may be changed or only found, the rule is one.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AProgramNotReadyThatOnlyAFallbackNeeds_PassesThatFallbackOver_WithTheFindersWords(bool change)
    {
        using var kit = new ClaudeCodeUpdateServiceTests.Kit(npm: false);
        var asker = ClaudeCodeAsker.Run(" (step 's')");
        var finder = change
            ? await kit.Service().ReadyAsync(kit.Layout, [], client: null, asker, Token)
            : await kit.Service().FoundAsync(kit.Layout, [], client: null, asker, Token);

        var launched = await Launch(kit).ProgramAsync(kit.Layout, Providers(), [Api, Claude], asker, change, Token);

        Assert.False(finder.Ready);
        Assert.Contains("no npm is on the PATH", finder.Message, StringComparison.Ordinal);
        Assert.Equal([Api, Claude with { Usable = false, WhyNot = finder.Message }], launched.Order);
        Assert.Equal(((string?)null, (string?)null, (ClaudeCodeAnswer?)null), (launched.Program, launched.Version, launched.Refusal));
    }

    /// <summary>
    /// Where the entry that would serve first is Claude Code's - the first usable one, an entry before it that cannot be
    /// used passed by - a program not ready refuses the asker with the finder's own answer, its words and code, and the
    /// order is left as it was.
    /// </summary>
    [Fact]
    public async Task AProgramNotReadyThatTheFirstUsableEntryNeeds_RefusesTheAsker_OnTheFindersAnswer()
    {
        using var kit = new ClaudeCodeUpdateServiceTests.Kit(npm: false);
        var asker = ClaudeCodeAsker.Run(" (step 's')");
        var finder = await kit.Service().ReadyAsync(kit.Layout, [], client: null, asker, Token);
        IReadOnlyList<AgentRunEntry> order = [Api with { Usable = false, WhyNot = "no key" }, Claude, Api];

        var launched = await Launch(kit).ProgramAsync(kit.Layout, Providers(), order, asker, change: true, Token);

        Assert.Equal((HarnessExit.ToolMissing, finder.Message), (launched.Refusal!.ExitCode, launched.Refusal.Message));
        Assert.Same(order, launched.Order);
        Assert.Equal(((string?)null, (string?)null), (launched.Program, launched.Version));
    }

    /// <summary>A program that is ready is started by every entry on its client: the order stands, the program and its version given.</summary>
    [Fact]
    public async Task AProgramThatIsReady_IsGivenWithItsVersion_TheOrderStanding()
    {
        using var kit = new ClaudeCodeUpdateServiceTests.Kit(package: "2.1.296", program: "2.1.296");
        IReadOnlyList<AgentRunEntry> order = [Api, Claude];

        var launched = await Launch(kit).ProgramAsync(kit.Layout, Providers(), order, ClaudeCodeAsker.Run(), change: true, Token);

        Assert.Same(order, launched.Order);
        Assert.Equal((kit.Program, "2.1.296", (ClaudeCodeAnswer?)null), (launched.Program, launched.Version, launched.Refusal));
    }

    /// <summary>The launch over the kit's machine, its Claude Code asked of the kit's finder.</summary>
    private static ReasonLaunch Launch(ClaudeCodeUpdateServiceTests.Kit kit)
    {
        var files = new PhysicalFileSystem(FilePermissionsFactory.Create());

        return new ReasonLaunch(files, kit.Platform, new AgentKeyStore(files, FilePermissionsFactory.Create(), kit.Platform), kit.Service(), new TestIsolation());
    }

    /// <summary>The template's providers, resolved.</summary>
    private static AgentProviders Providers()
    {
        var agents = AgentProviderTemplates.Read();

        return AgentProviders.Resolve(new HarnessConfig { Defaults = new() { AgentTier = agents.AgentTier }, Providers = [.. agents.Providers], TierConfig = new Dictionary<string, List<string>>(agents.TierConfig, StringComparer.OrdinalIgnoreCase) }).Providers
            ?? throw new InvalidOperationException("the template's providers did not resolve");
    }
}
