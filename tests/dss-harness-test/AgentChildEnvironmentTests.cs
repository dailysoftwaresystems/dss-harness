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
    /// appointed name is never given back, and a name of no secret's shape is kept. Every child is marked as an agent's,
    /// which <c>run-side-task</c> refuses itself by however it is named.
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
                [AgentChildEnvironment.AgentMarker] = "1",
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
        Assert.Empty(AgentChildEnvironment.Problems(new AgentPolicyConfig { KeepEnvironment = ["GITHUB_TOKEN"], CommandTimeoutSeconds = 60 }, []));

        // A time past the hour once the most a call could name is a time a command runs for, as any a timer holds.
        Assert.Equal(
            [
                "agentPolicy.keepEnvironment[0] 'NOT A NAME' is no variable's name ([A-Za-z_][A-Za-z0-9_]*)",
                "agentPolicy.keepEnvironment[1] 'ZAI_API_KEY' is a name the configuration appoints a value of the slot for, which no child of an agent is ever given",
                "agentPolicy.keepEnvironment[2] 'PATH' gives back nothing: a child loses only the names the configuration appoints and those ending _API_KEY, _SECRET, _SECRET_KEY, _ACCESS_KEY, _PRIVATE_KEY, _PASSWORD, _TOKEN",
            ],
            AgentChildEnvironment.Problems(new AgentPolicyConfig { KeepEnvironment = ["NOT A NAME", "ZAI_API_KEY", "PATH"], CommandTimeoutSeconds = 4000 }, ["ZAI_API_KEY"]));

        Assert.Equal(
            [
                "agentPolicy.keepEnvironment is given empty: leave the key out to give no name back",
                "agentPolicy.commandTimeoutSeconds is 0, and a command runs for one second at least",
            ],
            AgentChildEnvironment.Problems(new AgentPolicyConfig { KeepEnvironment = [], CommandTimeoutSeconds = 0 }, []));

        Assert.Equal(
            [
                $"agentPolicy.commandTimeoutSeconds is {AgentCommandSettings.MostTimeoutSeconds + 1}, past the 2147483 a command's timer holds",
            ],
            AgentChildEnvironment.Problems(new AgentPolicyConfig { CommandTimeoutSeconds = AgentCommandSettings.MostTimeoutSeconds + 1 }, []));
        Assert.Empty(AgentChildEnvironment.Problems(new AgentPolicyConfig { CommandTimeoutSeconds = AgentCommandSettings.MostTimeoutSeconds }, []));
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

    /// <summary>
    /// What the configuration says of an agent's commands: no time of a command's where it names none - no default stands
    /// in - and the time it names where it does.
    /// </summary>
    [Fact]
    public void TheCommandSettings_HoldNoTimeOfTheirOwn()
    {
        Assert.Equal(AgentCommandSettings.Default, AgentCommandSettings.Of(null) with { KeepEnvironment = AgentCommandSettings.Default.KeepEnvironment });
        Assert.Empty(AgentCommandSettings.Of(null).KeepEnvironment);
        Assert.Null(AgentCommandSettings.Of(new AgentPolicyConfig()).CommandTimeoutSeconds);

        var given = AgentCommandSettings.Of(new AgentPolicyConfig { KeepEnvironment = ["A_TOKEN"], CommandTimeoutSeconds = 5 });
        Assert.Equal(["A_TOKEN"], given.KeepEnvironment);
        Assert.Equal(5, given.CommandTimeoutSeconds);
    }

    /// <summary>
    /// agentPolicy.maxCommandTimeoutSeconds is retired: a configuration that names it is refused, the refusal naming what
    /// bounds a command now - a call's timeout_seconds up to what a timer holds, commandTimeoutSeconds, the step's
    /// stallSeconds and its own bounds - and nothing of it is read.
    /// </summary>
    [Fact]
    public void MaxCommandTimeoutSeconds_IsRefusedNamingWhatBoundsACommand()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, ".harness-config", "config.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """{ "agentPolicy": { "commandTimeoutSeconds": 60, "maxCommandTimeoutSeconds": 3600 } }""");
        var store = new JsonConfigStore(new PassThroughFileSystem(new PhysicalFileSystem(FilePermissionsFactory.Create())));

        var failure = Assert.Throws<ConfigException>(() => store.Load(path));

        Assert.Contains(
            "agentPolicy.maxCommandTimeoutSeconds is no longer read: a call's timeout_seconds names any time a command's timer holds (2147483 s at most), "
                + "and a command whose call names none runs for agentPolicy.commandTimeoutSeconds where it is given, else until it ends - stopped where it "
                + "prints nothing for the step's stallSeconds, and by the step's own bounds, its timeoutMinutes and its budget",
            failure.Message,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// agentContext.continuation is retired, every key of it: a configuration that names it is refused, the refusal naming
    /// what replaces its bounds - the record holding everything, its pages by read_result.
    /// </summary>
    [Fact]
    public void TheContinuationKeys_AreRefusedNamingTheirReplacement()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, ".harness-config", "config.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var store = new JsonConfigStore(new PassThroughFileSystem(new PhysicalFileSystem(FilePermissionsFactory.Create())));

        foreach (var keys in new[] { """{ "recordChars": 60000 }""", """{ "lastIterations": 3, "iterationChars": 120000 }""", "{}" })
        {
            File.WriteAllText(path, $$"""{ "agentContext": { "continuation": {{keys}} } }""");

            var failure = Assert.Throws<ConfigException>(() => store.Load(path));

            Assert.Contains(
                "agentContext.continuation is no longer read: the continuation record holds every step, every changed path and every iteration of the work "
                    + "it goes on with, the last iterations first, nothing of it cut - its first page in the step's first message and every page of it by "
                    + $"read_result with id '{DssHarness.Core.Agents.Runtime.KeptResults.ContinuationId}'",
                failure.Message,
                StringComparison.Ordinal);
        }
    }
}
