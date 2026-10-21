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
                "balanceKeyUsage is off: a run's steps are to take each provider's keys in order - no command runs a step in this build",
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

    /// <summary>
    /// The names the slot holds that nothing appoints are listed, where a misspelt one shows - and a name the tool reads
    /// itself, the program the claude-code client starts in place of npm's (CLAUDE_EXECUTABLE), is appointed by that
    /// client and never listed among them.
    /// </summary>
    [Fact]
    public async Task NamesTheNamesTheSlotHoldsThatNothingAppoints()
    {
        using var repository = await RepositoryAsync();
        repository.Tree.WriteFile(".harness-config/workflows/.env/keys.env", "ZAI_API_KEY=one\nZAI_APIKEY=two\nCLAUDE_EXECUTABLE=/opt/claude/bin/claude\n");
        var service = Service(repository.Harness);

        var outcome = await service.ListAsync(repository.Tree.Path, template: false, json: false, TestContext.Current.CancellationToken);

        Assert.Equal(0, outcome.ExitCode);
        Assert.Equal(
            ["declared in the slot and appointed by nothing: ZAI_APIKEY"],
            outcome.Details!.Where(line => line.StartsWith("declared in the slot", StringComparison.Ordinal)));
    }

    /// <summary>
    /// A provider with two keys lists both in the order they are tried, each present or missing as the slot holds it -
    /// and balanceKeyUsage on is said as what a run's steps are to do, no command of this build running one.
    /// </summary>
    [Fact]
    public async Task AProvidersTwoKeys_AreListedInOrder_EachPresentOrMissing()
    {
        using var repository = await RepositoryAsync();
        var file = Path.Combine(repository.Tree.Path, ".harness-config", "config.json");
        var config = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        config["providers"]![1]!["apiKeyNames"] = new JsonArray("ZAI_API_KEY", "ZAI_API_KEY_TWO");
        config["balanceKeyUsage"] = true;
        File.WriteAllText(file, config.ToJsonString(DssHarness.Core.Output.ReportJson.Options));
        repository.Tree.WriteFile(".harness-config/workflows/.env/keys.env", "ZAI_API_KEY_TWO=two\n");
        var service = Service(repository.Harness);

        var outcome = await service.ListAsync(repository.Tree.Path, template: false, json: false, TestContext.Current.CancellationToken);

        Assert.Equal(0, outcome.ExitCode);
        Assert.Equal(
            [
                "zai        zai        https://api.z.ai/api/paas/v4  glm          ZAI_API_KEY        missing, ZAI_API_KEY_TWO    present  glm-5.3",
                "balanceKeyUsage is on: a run's steps are to take the key that spent least - no command runs a step in this build",
            ],
            outcome.Details!.Where(line => line.StartsWith("zai ", StringComparison.Ordinal) || line.StartsWith("balanceKeyUsage", StringComparison.Ordinal)));
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

        // What the person pastes names each provider's keys as a list of names, the date its numbers were read and
        // Anthropic's price of a thousand searches.
        Assert.Equal(["ANTHROPIC_API_KEY"], printed["providers"]![0]!["apiKeyNames"]!.AsArray().Select(name => (string?)name));
        Assert.Equal(["ZAI_API_KEY"], printed["providers"]![1]!["apiKeyNames"]!.AsArray().Select(name => (string?)name));
        Assert.Equal(["2026-10-12", "2026-10-12"], printed["providers"]!.AsArray().Select(provider => (string?)provider!["readOn"]));
        Assert.Equal(10, (int?)printed["providers"]![0]!["pricePerThousandSearches"]);
    }

    /// <summary>
    /// The template is the sections as init writes them, and needs no repository to say so: it is answered outside one,
    /// and where the repository's own configuration does not load - the very configuration it is printed to repair.
    /// </summary>
    [Fact]
    public async Task TheTemplate_IsAnsweredOutsideARepository_AndWhereTheConfigurationDoesNotLoad()
    {
        using var outside = new TempDirectory();
        var harness = new HarnessFactory();

        var nowhere = await Service(harness).ListAsync(outside.Path, template: true, json: true, TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.Success, nowhere.ExitCode);
        var document = JsonNode.Parse(Assert.Single(nowhere.Data!))!.AsObject();

        Assert.Equal(["template"], document.Select(member => member.Key));
        Assert.Equal(["defaults", "providers", "tierConfig"], JsonNode.Parse((string)document["template"]!)!.AsObject().Select(member => member.Key));

        using var repository = await RepositoryAsync();
        var file = Path.Combine(repository.Tree.Path, ".harness-config", "config.json");
        var config = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        config["providers"]![0]!["api"] = "api.anthropic.com";
        File.WriteAllText(file, config.ToJsonString(DssHarness.Core.Output.ReportJson.Options));

        var broken = await Service(repository.Harness).ListAsync(repository.Tree.Path, template: true, json: false, TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.Success, broken.ExitCode);
        Assert.Equal(["defaults", "providers", "tierConfig"], JsonNode.Parse(Assert.Single(broken.Details!))!.AsObject().Select(member => member.Key));
    }

    /// <summary>
    /// --template asked for --json answers in its own document's shape on every exit, stopped ones included - a
    /// --directory that is not there, a command line the parser refuses - never in the listing's.
    /// </summary>
    [Fact]
    public async Task TheTemplateStopped_AnswersInItsOwnDocumentsShape()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;

        var missing = await CliRunner.RunAsync(["list-agent-providers", "--template", "--json", "--directory", temp.Combine("does-not-exist")], cancellationToken);
        var refused = await CliRunner.RunAsync(["list-agent-providers", "--template", "--json", "--no-such-option"], cancellationToken, workingDirectory: temp.Path);

        foreach (var result in new[] { missing, refused })
        {
            Assert.Equal(HarnessExit.UsageError, result.ExitCode);

            var document = JsonNode.Parse(result.StandardOutput.TrimEnd())!.AsObject();

            Assert.Equal(["exitCode", "summary", "template"], document.Select(member => member.Key));
            Assert.Equal(HarnessExit.UsageError, (int)document["exitCode"]!);
            Assert.Null(document["template"]);
        }
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
