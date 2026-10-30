using System.Globalization;
using DssHarness.Core.Agents;
using DssHarness.Core.Configuration;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Platform;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Repository;
using DssHarness.Core.Secrets;

namespace DssHarness.Tests;

/// <summary>
/// What each key spent, for the next step's choice of one (<c>balanceKeyUsage</c>, part 03's "A provider's keys, and
/// balancing them"): the record across runs written by the key's name and never its value, its week read, its lines
/// older than the week dropped when it is next written - and the order the keys run in, the least spent first, a tie
/// in the list's order.
/// </summary>
public sealed class AgentKeySpendTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>The spend record over a repository of a temporary directory, on the clock a test advances.</summary>
    private static (AgentKeySpend Spend, TempDirectory Temp, ManualClock Clock) Record()
    {
        var temp = new TempDirectory();
        var clock = new ManualClock();
        var harness = new HarnessFactory();
        var spend = new AgentKeySpend(harness.FileSystem, new OrchestrationStore(harness.FileSystem, harness.Output), clock);

        return (spend, temp, clock);
    }

    /// <summary>What a key spent is recorded by its name and never its value, and read back as the key's week.</summary>
    [Fact]
    public void WhatAKeySpent_IsRecordedByName_NeverByValue_AndReadAsItsWeek()
    {
        var (spend, temp, clock) = Record();
        var layout = new HarnessLayout(temp.Path, temp.Path);
        var sink = spend.RecordingIn(layout);

        sink.Record("anthropic", "ANTHROPIC_SPARE", "claude-opus-5-5", 0.03m);
        sink.Record("anthropic", "ANTHROPIC_KEY", "claude-fable-5-1", 0.01m);
        sink.Record("anthropic", "ANTHROPIC_SPARE", "claude-fable-5-1", 0.02m);
        sink.Record("anthropic", key: null, "claude-opus-5-5", 0.05m);

        var spent = spend.Spent(layout);

        Assert.Equal(0.05m, spent[("anthropic", "ANTHROPIC_SPARE")]);
        Assert.Equal(0.01m, spent[("anthropic", "ANTHROPIC_KEY")]);
        Assert.Equal(2, spent.Count);

        var written = File.ReadAllText(AgentKeySpend.FileOf(layout));

        Assert.DoesNotContain("sk-", written, StringComparison.Ordinal);
        Assert.Contains("\"key\":\"ANTHROPIC_SPARE\"", written, StringComparison.Ordinal);
        Assert.Equal(3, written.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length);
    }

    /// <summary>Lines older than the week are dropped when the record is next written; the week's own stay.</summary>
    [Fact]
    public void LinesOlderThanTheWeek_AreDroppedWhenTheRecordIsNextWritten()
    {
        var (spend, temp, clock) = Record();
        var layout = new HarnessLayout(temp.Path, temp.Path);
        Directory.CreateDirectory(layout.OrchestratorsDirectory);
        File.WriteAllText(
            AgentKeySpend.FileOf(layout),
            string.Join(
                Environment.NewLine,
                $"{{\"time\":\"{(clock.GetUtcNow() - AgentKeySpend.Week - TimeSpan.FromDays(1)).ToString("O", CultureInfo.InvariantCulture)}\",\"provider\":\"anthropic\",\"key\":\"ANTHROPIC_OLD\",\"model\":\"m\",\"cost\":9}}",
                $"{{\"time\":\"{clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture)}\",\"provider\":\"anthropic\",\"key\":\"ANTHROPIC_KEY\",\"model\":\"m\",\"cost\":1}}",
                "not a line"));

        spend.RecordingIn(layout).Record("anthropic", "ANTHROPIC_SPARE", "m", 0.01m);
        clock.Advance(TimeSpan.FromDays(1));

        var spent = spend.Spent(layout);

        Assert.Equal(0.01m, spent[("anthropic", "ANTHROPIC_SPARE")]);
        Assert.Equal(1m, spent[("anthropic", "ANTHROPIC_KEY")]);
        Assert.DoesNotContain(("anthropic", "ANTHROPIC_OLD"), spent.Keys);
        Assert.DoesNotContain("ANTHROPIC_OLD", File.ReadAllText(AgentKeySpend.FileOf(layout)), StringComparison.Ordinal);
    }

    /// <summary>The keys of one model run in the order of least spent, a tie in the list's order; the models' order is untouched.</summary>
    [Fact]
    public void TheKeysOfOneModel_RunInOrderOfLeastSpent_ATieInTheListsOrder_TheModelsUntouched()
    {
        var (providers, layout, keys, temp) = Order.Ready(["ANTHROPIC_KEY=sk-first", "ANTHROPIC_SPARE=sk-second", "ANTHROPIC_THIRD=sk-third", "CLAUDE_API_KEY=sk-api"]);
        var spent = new Dictionary<(string Provider, string Key), decimal>
        {
            [("anthropic", "ANTHROPIC_SPARE")] = 3,
            [("anthropic", "ANTHROPIC_KEY")] = 1,
        };

        var order = AgentStepOrder.Of(
            providers,
            new AgentResolution(
                new AgentModelEntry("claude-opus-5-5", "anthropic", "claude-api", "ANTHROPIC_KEY", null),
                [
                    new AgentModelEntry("claude-opus-5-5", "anthropic", "claude-api", "ANTHROPIC_SPARE", null),
                    new AgentModelEntry("claude-opus-5-5", "anthropic", "claude-api", "ANTHROPIC_THIRD", null),
                    new AgentModelEntry("claude-haiku-5-3", "anthropic-api", "claude-api", "CLAUDE_API_KEY", null),
                ],
                Tier: "best"),
            layout,
            keys,
            toolConfigDirectory: null,
            spent);

        // The third key spent nothing - the least - and the spare the most; a tie (the haiku's only key, its own
        // provider) keeps the list's order, and the models' order is untouched.
        Assert.Equal(
            ["ANTHROPIC_THIRD", "ANTHROPIC_KEY", "ANTHROPIC_SPARE", "CLAUDE_API_KEY"],
            order.Select(entry => entry.Key));
    }
}

/// <summary>The configuration and slot the balance's order is built over.</summary>
file static class Order
{
    public static (AgentProviders Providers, HarnessLayout Layout, AgentKeyStore Keys, TempDirectory Temp) Ready(string[] slot)
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
            TierConfig = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase) { ["best"] = ["claude-opus-5-5", "claude-haiku-5-3"] },
            Providers =
            [
                new AgentProviderConfig
                {
                    Name = "anthropic",
                    Type = "anthropic",
                    Api = "https://api.anthropic.com",
                    ApiKeyNames = ["ANTHROPIC_KEY", "ANTHROPIC_SPARE", "ANTHROPIC_THIRD"],
                    Client = "claude-api",
                    Models = [new AgentModelConfig { Code = "claude-opus-5-5", Name = "Opus", Tiers = ["best"], Efforts = ["high"], ContextTokens = 200000, MaxOutputTokens = 32000 }],
                },
                new AgentProviderConfig
                {
                    Name = "anthropic-api",
                    Type = "anthropic",
                    Api = "https://api.anthropic.com",
                    ApiKeyNames = ["CLAUDE_API_KEY"],
                    Client = "claude-api",
                    Models = [new AgentModelConfig { Code = "claude-haiku-5-3", Name = "Haiku", Tiers = ["best"], Efforts = ["high"], ContextTokens = 200000, MaxOutputTokens = 32000 }],
                },
            ],
        };

        var (providers, problems) = AgentProviders.Resolve(config);

        Assert.Empty(string.Join("; ", problems));
        Assert.NotNull(providers);

        return (providers!, layout, new AgentKeyStore(new PhysicalFileSystem(permissions), permissions, platform), temp);
    }
}
