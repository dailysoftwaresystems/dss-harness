using DssHarness.Core.Agents;
using DssHarness.Core.Configuration;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Platform;
using DssHarness.Core.Repository;
using DssHarness.Core.Secrets;

namespace DssHarness.Tests;

/// <summary>
/// The running order a step's resolution becomes (<see cref="AgentStepOrder"/>): each entry of the resolution with its
/// credential read from the slot, a key the slot does not hold an entry said and passed over, and the Claude Code
/// directory a launch reads.
/// </summary>
public sealed class AgentStepOrderTests
{
    [Fact]
    public void EachEntryOfTheResolutionBecomesOneRunningEntry_InOrder_WithItsCredential()
    {
        var (providers, layout, keys, temp) = Ready(["ANTHROPIC_KEY=sk-first", "ANTHROPIC_SECOND=sk-second"]);
        var order = AgentStepOrder.Of(
            providers,
            new AgentResolution(
                new AgentModelEntry("claude-opus-5-5", "anthropic", "claude-api", "ANTHROPIC_KEY", "high"),
                [new AgentModelEntry("claude-opus-5-5", "anthropic", "claude-api", "ANTHROPIC_SECOND", "high")],
                Tier: "best"),
            layout,
            keys,
            toolConfigDirectory: null);

        Assert.Equal(
            [
                ("claude-opus-5-5", "anthropic", "claude-api", "ANTHROPIC_KEY", true),
                ("claude-opus-5-5", "anthropic", "claude-api", "ANTHROPIC_SECOND", true),
            ],
            order.Select(entry => (entry.Model, entry.Provider, entry.Client, entry.Key!, entry.Usable)));
        Assert.All(order, entry => Assert.NotNull(entry.Credential));
        Assert.Equal("sk-first", order[0].Credential!.Keys[0].Value);
        Assert.Equal("ANTHROPIC_KEY", order[0].Credential!.Keys[0].Name);
        Assert.Equal("sk-second", order[1].Credential!.Keys[0].Value);
        Assert.Equal("https://api.anthropic.com", order[0].Credential!.Api);
    }

    [Fact]
    public void AKeyTheSlotDoesNotHoldIsAnEntryThatCannotRun_ItsRefusalSayingWhy()
    {
        var (providers, layout, keys, temp) = Ready(["ANTHROPIC_KEY=sk-first"]);

        var order = AgentStepOrder.Of(
            providers,
            new AgentResolution(
                new AgentModelEntry("claude-opus-5-5", "anthropic", "claude-api", "ANTHROPIC_KEY", null),
                [new AgentModelEntry("claude-opus-5-5", "anthropic", "claude-api", "ANTHROPIC_SECOND", null)],
                Tier: null),
            layout,
            keys,
            toolConfigDirectory: null);

        Assert.True(order[0].Usable);
        Assert.False(order[1].Usable);
        Assert.Null(order[1].Credential);
        Assert.Contains("ANTHROPIC_SECOND", order[1].WhyNot, StringComparison.Ordinal);
    }

    [Fact]
    public void AClientThatSignsWithTheLoginReadsNoKey_AndCarriesNoCredential()
    {
        var (providers, layout, keys, temp) = Ready(["ANTHROPIC_KEY=sk-first"], clients: new Dictionary<string, AgentClientConfig>(StringComparer.Ordinal)
        {
            ["claude-code"] = new() { Auth = "login" },
        });

        var order = AgentStepOrder.Of(
            providers,
            new AgentResolution(new AgentModelEntry("claude-opus-5-5", "anthropic", "claude-code", string.Empty, null), [], Tier: null),
            layout,
            keys,
            toolConfigDirectory: null);

        Assert.Null(order[0].Key);
        Assert.True(order[0].Usable);
        Assert.Null(order[0].Credential);
    }

    [Fact]
    public void AClaudeCodeLaunchIsGivenTheDirectoryTheSlotHoldsForConfigDir_ElseTheToolsOwn()
    {
        var (withSlot, layout, keys, temp) = Ready(
            ["ANTHROPIC_KEY=sk-first", "CLAUDE_CONFIG_DIR=C:/user/claude"],
            clients: new Dictionary<string, AgentClientConfig>(StringComparer.Ordinal) { ["claude-code"] = new() { Auth = "apiKey" } });

        var resolution = new AgentResolution(new AgentModelEntry("claude-opus-5-5", "anthropic", "claude-code", "ANTHROPIC_KEY", null), [], Tier: null);

        Assert.Equal("C:/user/claude", AgentStepOrder.Of(withSlot, resolution, layout, keys, toolConfigDirectory: "C:/tool/claude")[0].Credential!.ConfigDirectory);

        var (without, layoutAgain, keysAgain, _) = Ready(
            ["ANTHROPIC_KEY=sk-first"],
            clients: new Dictionary<string, AgentClientConfig>(StringComparer.Ordinal) { ["claude-code"] = new() { Auth = "apiKey" } });

        Assert.Equal("C:/tool/claude", AgentStepOrder.Of(without, resolution, layoutAgain, keysAgain, toolConfigDirectory: "C:/tool/claude")[0].Credential!.ConfigDirectory);
    }

    /// <summary>The configuration and slot the orders above are built over.</summary>
    private static (AgentProviders Providers, HarnessLayout Layout, AgentKeyStore Keys, TempDirectory Temp) Ready(
        string[] slot,
        Dictionary<string, AgentClientConfig>? clients = null)
    {
        var temp = new TempDirectory();
        var platform = HostDoubles.Platform(home: temp.Path);
        var layout = new HarnessLayout(temp.Path, temp.Path);
        var permissions = FilePermissionsFactory.Create();

        foreach (var line in slot)
        {
            var file = Path.Combine(temp.Path, ".harness-config", "workflows", ".env", $"{line[..line.IndexOf('=')]}.env");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, line + Environment.NewLine);
            permissions.ProtectSecret(file);
        }

        var config = new HarnessConfig
        {
            AgentClients = clients ?? [],
            TierConfig = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase) { ["best"] = ["claude-opus-5-5"] },
            Providers =
            [
                new AgentProviderConfig
                {
                    Name = "anthropic",
                    Type = "anthropic",
                    Api = "https://api.anthropic.com",
                    ApiKeyNames = ["ANTHROPIC_KEY", "ANTHROPIC_SECOND"],
                    Client = "claude-api",
                    Models = [new AgentModelConfig { Code = "claude-opus-5-5", Name = "Opus", Tiers = ["best"], Efforts = ["high"], ContextTokens = 200000, MaxOutputTokens = 32000 }],
                },
            ],
        };

        var (providers, problems) = AgentProviders.Resolve(config);

        Assert.Empty(string.Join("; ", problems));
        Assert.NotNull(providers);

        return (providers!, layout, new AgentKeyStore(new PhysicalFileSystem(permissions), permissions, platform), temp);
    }
}
