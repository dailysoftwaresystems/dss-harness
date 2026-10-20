using System.Text.Json.Nodes;
using DssHarness.Core.Agents;
using DssHarness.Core.Results;
using DssHarness.Core.Secrets;
using Xunit;

namespace DssHarness.Tests;

public sealed class AgentProviderServiceTests
{
    [Fact]
    public async Task ListsTheProvidersKeysTiersAndClients_AsTheTableSaysThem()
    {
        using var repository = await RepositoryAsync();
        repository.Tree.WriteFile(".harness-config/workflows/.env/keys.env", "ZAI_API_KEY=one\n");
        var service = Service(repository.Harness);

        var outcome = await service.ListAsync(repository.Tree.Path, template: false, json: false, TestContext.Current.CancellationToken);

        Assert.Equal(0, outcome.ExitCode);
        Assert.Equal(
            [
                "provider   type       api                           client       keys, in order              models",
                "anthropic  anthropic  https://api.anthropic.com     claude-code  ANTHROPIC_API_KEY  missing  claude-fable-5-1, claude-opus-5-5, claude-sonnet-5-5, claude-haiku-5-5",
                "zai        zai        https://api.z.ai/api/paas/v4  glm          ZAI_API_KEY        present  glm-5.3",
                "keys are tried in each provider's order (balanceKeyUsage is off)",
                "prices of 'anthropic' (USD, read 2026-10-12): all 4 models priced",
                "prices of 'zai' (USD, read 2026-10-12): glm-5.3 priced",
                "tier best:     Claude Fable 5.1 (claude-fable-5-1), GLM 5.3 (glm-5.3)",
                "tier high:     Claude Opus 5.5 (claude-opus-5-5), GLM 5.3 (glm-5.3)",
                "tier balanced: Claude Sonnet 5.5 (claude-sonnet-5-5), GLM 5.3 (glm-5.3)",
                "tier fast:     Claude Haiku 5.5 (claude-haiku-5-5), GLM 5.3 (glm-5.3)",
                "client 'claude-code' signs in with the machine's login and reads no key",
                "keys are read from .harness-config/workflows/.env/ (1 file) and from nowhere else",
            ],
            outcome.Details);
    }

    [Fact]
    public async Task NamesTheNamesTheSlotHoldsThatNothingAppoints()
    {
        using var repository = await RepositoryAsync();
        repository.Tree.WriteFile(".harness-config/workflows/.env/keys.env", "ZAI_API_KEY=one\nZAI_APIKEY=two\n");
        var service = Service(repository.Harness);

        var outcome = await service.ListAsync(repository.Tree.Path, template: false, json: false, TestContext.Current.CancellationToken);

        Assert.Equal(0, outcome.ExitCode);
        Assert.Contains("declared in the slot and appointed by nothing: ZAI_APIKEY", outcome.Details!);
    }

    [Fact]
    public async Task AnswersOneJsonDocument_WithNoMemberForAValue()
    {
        using var repository = await RepositoryAsync();
        repository.Tree.WriteFile(".harness-config/workflows/.env/keys.env", "ZAI_API_KEY=one\n");
        var service = Service(repository.Harness);

        var outcome = await service.ListAsync(repository.Tree.Path, template: false, json: true, TestContext.Current.CancellationToken);

        Assert.Equal(0, outcome.ExitCode);
        var document = JsonNode.Parse(Assert.Single(outcome.Data!))!;

        Assert.Equal(".harness-config/workflows/.env/", (string?)document["slot"]);
        Assert.Equal(1, (int?)document["files"]);
        Assert.Equal(false, (bool?)document["balanceKeyUsage"]);
        Assert.Equal(["anthropic", "zai"], document["providers"]!.AsArray().Select(provider => (string?)provider!["name"]));
        Assert.Equal("https://api.anthropic.com", (string?)document["providers"]![0]!["api"]);
        Assert.Equal([false, true], document["providers"]!.AsArray().Select(provider => (bool?)provider!["keys"]![0]!["held"]));
        Assert.Equal(["glm-5.3"], document["providers"]![1]!["models"]!.AsArray().Select(model => (string?)model!["code"]));
        Assert.Equal(1000000, (int?)document["providers"]![1]!["models"]![0]!["contextTokens"]);
        Assert.Equal(["best", "high", "balanced", "fast"], document["tiers"]!.AsArray().Select(tier => (string?)tier!["name"]));
        Assert.Equal("claude-code", (string?)document["clients"]![0]!["name"]);
        Assert.Equal("the machine's login", (string?)document["clients"]![0]!["signIn"]);

        // The record the document answers for holds names and whether the slot holds them - never a value, so no
        // member anywhere in it could carry one.
        Assert.False(document.ToJsonString().Contains("value", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ListsAConfigurationWithNoProviders_ByNamingTheCommandThatPrintsThem()
    {
        using var repository = await RepositoryAsync(stripAgentSections: true);
        var service = Service(repository.Harness);

        var outcome = await service.ListAsync(repository.Tree.Path, template: false, json: false, TestContext.Current.CancellationToken);

        Assert.Equal(0, outcome.ExitCode);
        Assert.Equal(
            [
                "no providers are declared; dssharness list-agent-providers --template prints them as init writes them into a new config, to paste",
                "keys are read from .harness-config/workflows/.env/ (no file) and from nowhere else",
            ],
            outcome.Details);
    }

    [Fact]
    public async Task TheTemplate_IsTheAgentSectionsAsInitWroteThem()
    {
        using var repository = await RepositoryAsync();
        var service = Service(repository.Harness);

        var outcome = await service.ListAsync(repository.Tree.Path, template: true, json: false, TestContext.Current.CancellationToken);

        Assert.Equal(0, outcome.ExitCode);

        // The sections the template holds, serialized by the writer that writes a config: the same values the
        // config init wrote in this very repository holds, key for key and value for value.
        var printed = JsonNode.Parse(Assert.Single(outcome.Details!))!;
        var written = JsonNode.Parse(File.ReadAllText(Path.Combine(repository.Tree.Path, ".harness-config", "config.json")))!;

        Assert.Equal(written!["defaults"]!["agentTier"]!.ToJsonString(), printed["defaults"]!["agentTier"]!.ToJsonString());
        Assert.Equal(written!["providers"]!.ToJsonString(), printed["providers"]!.ToJsonString());
        Assert.Equal(written!["tierConfig"]!.ToJsonString(), printed["tierConfig"]!.ToJsonString());
        Assert.Equal(["defaults", "providers", "tierConfig"], ((JsonObject)printed).Select(pair => pair.Key));
    }

    [Fact]
    public async Task ANameDeclaredTwice_RefusesTheListing()
    {
        using var repository = await RepositoryAsync();
        repository.Tree.WriteFile(".harness-config/workflows/.env/keys.env", "ZAI_API_KEY=one\n");
        repository.Tree.WriteFile(".harness-config/workflows/.env/work.env", "ZAI_API_KEY=two\n");
        var service = Service(repository.Harness);

        var outcome = await service.ListAsync(repository.Tree.Path, template: false, json: false, TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.ConfigInvalid, outcome.ExitCode);
        var keys = Path.Combine(repository.Tree.Path, ".harness-config", "workflows", ".env", "keys.env");
        var work = Path.Combine(repository.Tree.Path, ".harness-config", "workflows", ".env", "work.env");
        Assert.Equal([$"'ZAI_API_KEY' is declared in '{keys}' and in '{work}': a name is declared once"], outcome.Details);
    }

    private static AgentProvidersService Service(HarnessFactory harness)
        => new(
            harness.ContextLoader,
            harness.Platform,
            new AgentKeyStore(harness.FileSystem, harness.FilePermissions, harness.Platform));

    /// <summary>
    /// A repository whose configuration is the one init writes, with the slot's files the test's to add; where the
    /// agent sections are stripped, a configuration that declares no providers.
    /// </summary>
    private static async Task<Repository> RepositoryAsync(bool stripAgentSections = false)
    {
        var harness = new HarnessFactory();
        var temp = new TempDirectory();
        await harness.InitializeHarnessAsync(temp.Path, TestContext.Current.CancellationToken);

        if (stripAgentSections)
        {
            var file = Path.Combine(temp.Path, ".harness-config", "config.json");
            var config = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
            Assert.True(config.Remove("providers") && config.Remove("tierConfig"));
            File.WriteAllText(file, config.ToJsonString(DssHarness.Core.Output.ReportJson.Options));
        }

        return new Repository(harness, temp);
    }

    /// <summary>The repository a listing ran in, disposable with its tree.</summary>
    private sealed record Repository(HarnessFactory Harness, TempDirectory Tree) : IDisposable
    {
        public void Dispose() => Tree.Dispose();
    }
}
