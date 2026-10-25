using DssHarness.Core.Agents;
using DssHarness.Core.Configuration;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Platform;

namespace DssHarness.Tests;

/// <summary>
/// The environment every child of an agent is given - a command of the tool runtime, and the claude child - with the
/// configuration's keys that shape it and bound how long a command runs.
/// </summary>
public sealed class AgentChildEnvironmentTests
{
    /// <summary>
    /// A child of an agent loses every appointed name and every secret-shaped name, in any case, but those given back; an
    /// appointed name is never given back, and a name of no secret's shape is kept.
    /// </summary>
    [Fact]
    public void AChild_LosesTheAppointedAndTheSecretShapedNames_ButThoseGivenBack()
    {
        var environment = AgentChildEnvironment.Of(
            ["PATH", "GITHUB_TOKEN", "npm_token", "DB_PASSWORD", "AWS_SECRET_ACCESS_KEY", "AWS_ACCESS_KEY_ID", "SIGNING_PRIVATE_KEY", "MY_SECRET", "OPENAI_API_KEY", "ZAI_KEY", "TOKEN", "HOME"],
            ["ZAI_KEY", "OPENAI_API_KEY"],
            ["github_token", "OPENAI_API_KEY"]);

        Assert.Equal(
            new Dictionary<string, string?>
            {
                ["npm_token"] = null,
                ["DB_PASSWORD"] = null,
                ["AWS_SECRET_ACCESS_KEY"] = null,
                ["SIGNING_PRIVATE_KEY"] = null,
                ["MY_SECRET"] = null,
                ["OPENAI_API_KEY"] = null,
                ["ZAI_KEY"] = null,
            },
            environment);
    }

    /// <summary>The tool's own environment is read whole, each name with its value, found as the system finds it: in any case on Windows.</summary>
    [Fact]
    public void TheToolsOwnEnvironment_IsReadWhole()
    {
        var own = AgentChildEnvironment.Own();
        var path = Environment.GetEnvironmentVariables().Keys.Cast<string>().First(name => name.Equals("PATH", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(Environment.GetEnvironmentVariable(path), own[path]);
        Assert.Equal(OperatingSystem.IsWindows(), own.ContainsKey(path.ToLowerInvariant()) && own.ContainsKey(path.ToUpperInvariant()));

        // A name a block holds twice in two cases is read once, the first, where names compare in any case.
        var twice = new System.Collections.Specialized.OrderedDictionary(StringComparer.Ordinal) { ["Path"] = "first", ["PATH"] = "second", ["Other"] = "o" };
        Assert.Equal(
            [new("Path", "first"), new("Other", "o")],
            AgentChildEnvironment.Read(twice, StringComparer.OrdinalIgnoreCase).ToList<KeyValuePair<string, string>>());
        Assert.Equal(3, AgentChildEnvironment.Read(twice, StringComparer.Ordinal).Count);
    }

    /// <summary>The command keys are refused where they say nothing, name what is no name, name an appointed key, or bound past their bound.</summary>
    [Fact]
    public void TheCommandKeys_AreRefused_EachByItsKey()
    {
        Assert.Empty(AgentChildEnvironment.Problems(null, []));
        Assert.Empty(AgentChildEnvironment.Problems(new AgentPolicyConfig { KeepEnvironment = ["GITHUB_TOKEN"], CommandTimeoutSeconds = 60, MaxCommandTimeoutSeconds = 60 }, []));

        Assert.Equal(
            [
                "agentPolicy.keepEnvironment[0] 'NOT A NAME' is no variable's name ([A-Za-z_][A-Za-z0-9_]*)",
                "agentPolicy.keepEnvironment[1] 'ZAI_API_KEY' is a name the configuration appoints a value of the slot for, which no child of an agent is ever given",
                "agentPolicy.keepEnvironment[2] 'PATH' gives back nothing: a child loses only the names the configuration appoints and those ending _API_KEY, _SECRET, _SECRET_KEY, _ACCESS_KEY, _PRIVATE_KEY, _PASSWORD, _TOKEN",
                "agentPolicy.commandTimeoutSeconds (4000) is past agentPolicy.maxCommandTimeoutSeconds (3600), the most a call may name",
            ],
            AgentChildEnvironment.Problems(new AgentPolicyConfig { KeepEnvironment = ["NOT A NAME", "ZAI_API_KEY", "PATH"], CommandTimeoutSeconds = 4000 }, ["ZAI_API_KEY"]));

        Assert.Equal(
            [
                "agentPolicy.keepEnvironment is given empty: leave the key out to give no name back",
                "agentPolicy.commandTimeoutSeconds is 0, and a command runs for one second at least",
                "agentPolicy.maxCommandTimeoutSeconds is -1, and a command runs for one second at least",
            ],
            AgentChildEnvironment.Problems(new AgentPolicyConfig { KeepEnvironment = [], CommandTimeoutSeconds = 0, MaxCommandTimeoutSeconds = -1 }, []));

        Assert.Equal(
            [
                $"agentPolicy.maxCommandTimeoutSeconds is {AgentCommandSettings.MostTimeoutSeconds + 1}, past the 2147483 a command's timer holds",
            ],
            AgentChildEnvironment.Problems(new AgentPolicyConfig { MaxCommandTimeoutSeconds = AgentCommandSettings.MostTimeoutSeconds + 1 }, []));
        Assert.Empty(AgentChildEnvironment.Problems(new AgentPolicyConfig { CommandTimeoutSeconds = AgentCommandSettings.MostTimeoutSeconds, MaxCommandTimeoutSeconds = AgentCommandSettings.MostTimeoutSeconds }, []));
    }

    /// <summary>
    /// The configuration's reader refuses the command keys' problems where the file is read: a name of no secret's shape,
    /// and a name the configuration appoints for a provider's key, each by its whole sentence.
    /// </summary>
    [Fact]
    public void TheConfigurationsReader_RefusesTheCommandKeysProblems()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, ".harness-config", "config.json");
        var store = new JsonConfigStore(new PassThroughFileSystem(new PhysicalFileSystem(FilePermissionsFactory.Create())));
        store.Save(path, new HarnessConfig
        {
            AgentPolicy = new AgentPolicyConfig { KeepEnvironment = ["PATH", "ZAI_API_KEY"], CommandTimeoutSeconds = 0 },
            Defaults = new HarnessDefaults { AgentTier = "balanced" },
            Providers =
            [
                new()
                {
                    Name = "zai", Type = "zai", Api = "https://api.z.ai", ApiKeyNames = ["ZAI_API_KEY"], Client = "glm",
                    Currency = "USD", ReadOn = "2026-10-12",
                    Models = [new() { Code = "glm-5.3", Name = "GLM 5.3", Tiers = ["balanced"], Efforts = ["low", "high", "max"], ContextTokens = 1_000_000, MaxOutputTokens = 131_072 }],
                },
            ],
            TierConfig = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase) { ["balanced"] = ["glm-5.3"] },
        });

        var failure = Assert.Throws<ConfigException>(() => store.Load(path));

        Assert.Contains(
            "agentPolicy.keepEnvironment[0] 'PATH' gives back nothing: a child loses only the names the configuration appoints and those ending "
                + string.Join(", ", AgentChildEnvironment.SecretSuffixes),
            failure.Message,
            StringComparison.Ordinal);
        Assert.Contains(
            "agentPolicy.keepEnvironment[1] 'ZAI_API_KEY' is a name the configuration appoints a value of the slot for, which no child of an agent is ever given",
            failure.Message,
            StringComparison.Ordinal);
        Assert.Contains("agentPolicy.commandTimeoutSeconds is 0, and a command runs for one second at least", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>What the configuration says of an agent's commands, each key left out taking its default.</summary>
    [Fact]
    public void TheCommandSettings_TakeTheirDefaults()
    {
        Assert.Equal(AgentCommandSettings.Default, AgentCommandSettings.Of(null) with { KeepEnvironment = AgentCommandSettings.Default.KeepEnvironment });
        Assert.Empty(AgentCommandSettings.Of(null).KeepEnvironment);
        Assert.Equal(120, AgentCommandSettings.Of(new AgentPolicyConfig()).TimeoutSeconds);
        Assert.Equal(3600, AgentCommandSettings.Of(new AgentPolicyConfig()).MaxTimeoutSeconds);

        var given = AgentCommandSettings.Of(new AgentPolicyConfig { KeepEnvironment = ["A_TOKEN"], CommandTimeoutSeconds = 5, MaxCommandTimeoutSeconds = 9 });
        Assert.Equal(["A_TOKEN"], given.KeepEnvironment);
        Assert.Equal(5, given.TimeoutSeconds);
        Assert.Equal(9, given.MaxTimeoutSeconds);
    }
}
