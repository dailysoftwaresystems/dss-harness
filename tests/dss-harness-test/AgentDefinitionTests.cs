using DssHarness.Core.Agents;
using DssHarness.Core.Configuration;
using DssHarness.Core.Orchestration.Flow;

namespace DssHarness.Tests;

/// <summary>
/// An agent definition, read by its parser, and a step's model resolved over the configuration with its definition
/// below it: the definition's keys each on their own, its retired keys refused as a step's are, its effort read
/// through after the step's, and the order a step goes on by the entries of the tier's models and their provider's
/// keys (value 424).
/// </summary>
public sealed class AgentDefinitionTests
{
    [Fact]
    public void ADefinitionFile_ReadsItsKeysAndItsPromptVerbatim()
    {
        var (definition, problems) = AgentDefinitionParser.Parse(
            """
            ---
            name: worker
            description: does the work the steps ask of it
            maxTurns: 40
            maxBudgetUsd: 2.50
            maxMinutes: 30
            stallSeconds: 90
            capabilities: [read, run]
            writes: false
            restrictions:
              - Run(git push *)
            ---
            You do the work.

            ## How you work

            Start small.
            """,
            "worker");

        Assert.Empty(problems);
        Assert.Equal("worker", definition?.Name);
        Assert.Equal("does the work the steps ask of it", definition?.Description);
        Assert.Equal("You do the work.\n\n## How you work\n\nStart small.", definition?.Prompt);
        Assert.Null(definition?.Tier);
        Assert.Null(definition?.Model);
        Assert.Null(definition?.Effort);
        Assert.Equal(40, definition?.MaxTurns);
        Assert.Equal(2.50m, definition?.MaxBudgetUsd);
        Assert.Equal(30, definition?.MaxMinutes);
        Assert.Equal(90, definition?.StallSeconds);
        Assert.Equal(["read", "run"], definition?.Capabilities);
        Assert.False(definition?.Writes);
        Assert.Equal(["Run(git push *)"], definition?.AddedRules);
    }

    [Fact]
    public void ADefinitionMayNameATierAModelAnEffortAndAClient()
    {
        var (definition, problems) = AgentDefinitionParser.Parse(
            """
            ---
            name: worker
            description: does the work
            tier: balanced
            effort:
              all: high
              glm-5.3: max
            client: glm
            ---
            You do the work.
            """,
            "worker");

        Assert.Empty(problems);
        Assert.Equal("balanced", definition?.Tier);
        Assert.Null(definition?.Model);
        Assert.Null(definition?.Effort);
        Assert.Equal(new Dictionary<string, string>(StringComparer.Ordinal) { ["all"] = "high", ["glm-5.3"] = "max" }, definition?.EffortByModel);
        Assert.Equal("glm", definition?.Client);
    }

    /// <summary>Every problem the file has is said at once, each with the line of the file it stands on.</summary>
    [Fact]
    public void AFileWithManyProblems_SaysThemAllWithTheirLines()
    {
        var (definition, problems) = AgentDefinitionParser.Parse(
            """
            ---
            name: worker.md
            tier: balanced
            model: glm-5.3
            fallback: [claude-haiku-5-5]
            ---
            You do the work.
            """,
            "worker");

        Assert.Null(definition);
        Assert.Equal(
            [
                "line 5, column 1: 'fallback' is not a key of an agent; a fallback is the order of the tier's models in tierConfig.",
                $"name 'worker.md': {DssHarness.Core.Worktrees.WorktreeName.ValidateFormat("worker.md").Error}",
                "'description' is named nowhere; it is required.",
                "'tier' and 'model' are named together: a definition says one - the tier's models in tierConfig's order, or the model outright",
            ],
            problems);
    }

    [Fact]
    public void AFileWithNoFrontmatterOrNoPrompt_IsRefused()
    {
        var (none, noFrontmatter) = AgentDefinitionParser.Parse("You do the work.\n", "worker");
        var (empty, noPrompt) = AgentDefinitionParser.Parse("---\nname: worker\ndescription: does the work\n---\n", "worker");

        Assert.Null(none);
        Assert.Equal(["the file holds no frontmatter: '---' on the first line, the agent's keys, '---' again, and the system prompt after it"], noFrontmatter);
        Assert.Null(empty);
        Assert.Equal(["the file holds no prompt: the body after its frontmatter is the agent's system prompt, verbatim"], noPrompt);
    }

    /// <summary>A key of another tool's agent files names the key of this tool's that says it.</summary>
    [Theory]
    [InlineData("tools", "an agent's tools follow from its capabilities; say those")]
    [InlineData("disallowedTools", "an agent's restrictions say what it may not run; agentPolicy.restrictions and a step's own add to them")]
    [InlineData("permissionMode", "every call an agent makes is decided by this tool's policy; restrictions add to it")]
    [InlineData("skills", "a step's prompt names one: {skill.<name>}")]
    [InlineData("mcpServers", "the engine serves an agent its tools itself; none is named in a definition")]
    [InlineData("hooks", "no agent of this tool takes hooks")]
    public void AKeyOfAnotherToolsAgentFiles_IsRefusedNamingWhatToUseInstead(string key, string replacement)
    {
        var (definition, problems) = AgentDefinitionParser.Parse($"---\nname: worker\ndescription: does the work\n{key}: x\n---\nYou do the work.\n", "worker");

        Assert.Null(definition);
        Assert.Equal($"line 4, column 1: '{key}' is not a key of an agent; {replacement}.", Assert.Single(problems));
    }

    [Fact]
    public void AProviderKeyIsRefusedAsAStepsIs()
    {
        var (_, problems) = AgentDefinitionParser.Parse("---\nname: worker\ndescription: does the work\nprovider: zai\n---\nYou do the work.\n", "worker");

        Assert.Equal("line 4, column 1: 'provider' is not a key of an agent; a model's code is listed by one provider, which it names.", Assert.Single(problems));
    }

    [Fact]
    public void LimitsAndCapabilitiesAndNames_AreHeldToTheirShapes()
    {
        var (definition, problems) = AgentDefinitionParser.Parse(
            """
            ---
            name: worker
            description: does the work
            maxTurns: 0
            maxBudgetUsd: cheap
            capabilities: [read, fly, read]
            restrictions:
              - ""
            ---
            You do the work.
            """,
            "worker");

        Assert.Null(definition);
        Assert.Equal(
            [
                "line 4, column 11: 'maxTurns' is 0; a limit is at least 1",
                "line 5, column 15: 'maxBudgetUsd' is not a number of dollars; a budget is",
                "line 6, column 22: 'capabilities' names 'fly'. Known: read, write, run, web",
                "line 6, column 27: 'capabilities' names 'read' more than once",
                "line 8, column 5: 'restrictions' holds a name that is blank",
            ],
            problems);
    }

    /// <summary>The sample agent files name no model, tier or effort, and no shipped one does: they are a step's configuration.</summary>
    [Fact]
    public void TheShippedAgentFiles_ReadWholeAndNameNoModelTierOrEffort()
    {
        foreach (var file in WorkflowTemplates.Files.Where(file => file.StartsWith("agents/", StringComparison.Ordinal)))
        {
            var text = WorkflowTemplates.TextOf(file);

            Assert.NotNull(text);

            var (definition, problems) = AgentDefinitionParser.Parse(text!, file["agents/".Length..][..^".md".Length]);

            Assert.True(problems.Count == 0, $"{file}: {string.Join("; ", problems)}");
            Assert.Null(definition?.Tier);
            Assert.Null(definition?.Model);
            Assert.Null(definition?.Effort);
            Assert.Null(definition?.EffortByModel);
            Assert.Null(definition?.Client);
        }
    }

    /// <summary>
    /// The configuration the resolution is checked against: zai's glm-5.3 with two keys on tier 'balanced', and a
    /// second company's model after it, so the entries of a tier's order show each model's keys before the next model.
    /// </summary>
    private static AgentProviders Providers(string? defaultTier = null)
    {
        var zai = new AgentProviderConfig
        {
            Name = "zai",
            Type = "zai",
            Api = "https://api.z.ai",
            ApiKeyNames = ["ZAI_API_KEY", "ZAI_BACKUP_KEY"],
            Client = "glm",
            Models = [new AgentModelConfig { Code = "glm-5.3", Name = "GLM 5.3", Tiers = ["balanced"], Efforts = ["low", "high", "max"], ContextTokens = 200000, MaxOutputTokens = 32000 }],
        };
        var other = new AgentProviderConfig
        {
            Name = "anthropic",
            Type = "anthropic",
            Api = "https://api.anthropic.com",
            ApiKeyNames = ["ANTHROPIC_API_KEY"],
            Client = "claude-api",
            Models = [new AgentModelConfig { Code = "claude-fable-5-1", Name = "Claude Fable 5.1", Tiers = ["balanced"], Efforts = ["low", "medium", "high", "xhigh", "max"], ContextTokens = 1000000, MaxOutputTokens = 64000 }],
        };
        var config = new HarnessConfig
        {
            Providers = [zai, other],
            TierConfig = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase) { ["balanced"] = ["glm-5.3", "claude-fable-5-1"] },
            Defaults = new HarnessDefaults { AgentTier = defaultTier },
        };

        return AgentProviders.Resolve(config).Providers ?? throw new InvalidOperationException("the fixture did not resolve");
    }

    [Fact]
    public void AStepOnATier_StartsOnItsFirstModel_ItsOrderEachModelsKeysBeforeTheNext()
    {
        var resolved = AgentResolution.Of(Providers(), tier: "balanced", model: null, effort: "high", effortByModel: null, client: null, definition: null);

        Assert.NotNull(resolved);
        Assert.Equal(("glm-5.3", "zai", "glm", "ZAI_API_KEY", "high"), (resolved.Serving.Model, resolved.Serving.Provider, resolved.Serving.Client, resolved.Serving.Key, resolved.Serving.Effort));
        Assert.Equal("balanced", resolved.Tier);
        Assert.Equal(
            [
                ("glm-5.3", "ZAI_BACKUP_KEY", "high"),
                ("claude-fable-5-1", "ANTHROPIC_API_KEY", "high"),
            ],
            resolved.After.Select(entry => (entry.Model, entry.Key, entry.Effort)));
    }

    [Fact]
    public void AModelNamedOutright_ByAStepOrByItsDefinition_HasItsKeysAndNothingAfterThem()
    {
        var providers = Providers();

        var byStep = AgentResolution.Of(providers, tier: null, model: "claude-fable-5-1", effort: null, effortByModel: null, client: null, definition: null);
        var byDefinition = AgentResolution.Of(
            providers,
            tier: null,
            model: null,
            effort: null,
            effortByModel: null,
            client: null,
            definition: new AgentDefinition("worker", "does the work", "You do the work.", Model: "glm-5.3"));

        Assert.Equal(("claude-fable-5-1", "anthropic", "claude-api", "ANTHROPIC_API_KEY"), (byStep?.Serving.Model, byStep?.Serving.Provider, byStep?.Serving.Client, byStep?.Serving.Key));
        Assert.Null(byStep?.Tier);
        Assert.Empty(byStep?.After!);

        // One key is all claude-api appoints, so nothing follows it; glm's second key does.
        Assert.Equal("glm-5.3", byDefinition?.Serving.Model);
        Assert.Equal(("glm-5.3", "ZAI_BACKUP_KEY"), (byDefinition?.After.Single().Model, byDefinition?.After.Single().Key));
        Assert.Null(byDefinition?.Tier);
    }

    [Fact]
    public void AStepsOwnNames_WinOverItsDefinitions_EachOnItsOwn()
    {
        var resolved = AgentResolution.Of(
            Providers(),
            tier: "balanced",
            model: null,
            effort: null,
            effortByModel: null,
            client: null,
            definition: new AgentDefinition("worker", "does the work", "You do the work.", Model: "claude-fable-5-1"));

        // The step's tier over the definition's model (part 03), and its order the tier's.
        Assert.Equal("glm-5.3", resolved?.Serving.Model);
        Assert.Equal("balanced", resolved?.Tier);
        Assert.Equal(2, resolved?.After.Count);
    }

    [Fact]
    public void ADefinitionThatNamesNeither_LeavesTheDefaultTierToServe()
    {
        var resolved = AgentResolution.Of(
            Providers(defaultTier: "balanced"),
            tier: null,
            model: null,
            effort: null,
            effortByModel: null,
            client: null,
            definition: new AgentDefinition("worker", "does the work", "You do the work."));

        Assert.Equal("glm-5.3", resolved?.Serving.Model);
        Assert.Equal("balanced", resolved?.Tier);
    }

    /// <summary>Each model's level is resolved on its own: the step's entry, its all or one level, the definition's entry, its all or one level, else none.</summary>
    [Fact]
    public void AModelsEffort_IsResolvedThroughTheFourSources()
    {
        var definition = new AgentDefinition(
            "worker",
            "does the work",
            "You do the work.",
            EffortByModel: new Dictionary<string, string>(StringComparer.Ordinal) { ["all"] = "low", ["claude-fable-5-1"] = "max" });

        string? Level(string? effort = null, IReadOnlyDictionary<string, string>? map = null)
            => AgentResolution.LevelFor("claude-fable-5-1", effort, map, definition.Effort, definition.EffortByModel)?.Level;

        // The step's own entry, then its all, both stand before anything of the definition's.
        Assert.Equal("xhigh", Level(map: new Dictionary<string, string>(StringComparer.Ordinal) { ["all"] = "high", ["claude-fable-5-1"] = "xhigh" }));
        Assert.Equal("high", Level(map: new Dictionary<string, string>(StringComparer.Ordinal) { ["all"] = "high" }));
        Assert.Equal("high", Level(effort: "high"));

        // Where the step's map names neither this model nor all, the definition's entry stands.
        Assert.Equal("max", Level(map: new Dictionary<string, string>(StringComparer.Ordinal) { ["glm-5.3"] = "low" }));
        Assert.Equal("max", Level());
        Assert.Equal(
            ("low", LevelSource.DefinitionAll),
            AgentResolution.LevelFor("glm-5.3", null, null, definition.Effort, definition.EffortByModel));

        var one = new AgentDefinition("worker", "does the work", "You do the work.", Effort: "low");

        Assert.Equal(("low", LevelSource.DefinitionOne), AgentResolution.LevelFor("glm-5.3", null, null, one.Effort, one.EffortByModel));
        Assert.Null(AgentResolution.LevelFor("glm-5.3", null, null, null, null));
    }

    [Fact]
    public void AClientThatSignsInWithTheMachinesLogin_ServesWithNoKeyOfItsOwn()
    {
        // TwoModels-style providers, with anthropic reached through claude-code, which signs in and reads no key:
        // its model is still the tier's serving entry, its login the entry, and the tier's order keeps it where
        // tierConfig puts it.
        var providers = Providers();
        var config = new HarnessConfig
        {
            Providers =
            [
                new AgentProviderConfig
                {
                    Name = "anthropic", Type = "anthropic", Api = "https://api.anthropic.com", ApiKeyNames = ["ANTHROPIC_API_KEY"], Client = "claude-code",
                    Models = [new AgentModelConfig { Code = "claude-fable-5-1", Name = "Claude Fable 5.1", Tiers = ["best"], Efforts = ["low", "high"], ContextTokens = 1000000, MaxOutputTokens = 64000 }],
                },
                new AgentProviderConfig
                {
                    Name = "zai", Type = "zai", Api = "https://api.z.ai", ApiKeyNames = ["ZAI_API_KEY"], Client = "glm",
                    Models = [new AgentModelConfig { Code = "glm-5.3", Name = "GLM 5.3", Tiers = ["best"], Efforts = ["low", "high", "max"], ContextTokens = 200000, MaxOutputTokens = 32000 }],
                },
            ],
            TierConfig = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase) { ["best"] = ["claude-fable-5-1", "glm-5.3"] },
        };
        var resolved = AgentProviders.Resolve(config).Providers;

        Assert.NotNull(resolved);

        var order = AgentResolution.Of(resolved!, "best", null, null, null, null, null);

        Assert.Equal(("claude-fable-5-1", string.Empty), (order?.Serving.Model, order?.Serving.Key));
        Assert.Equal(["glm-5.3"], order?.After.Select(entry => entry.Model));
    }

    [Fact]
    public void AClientIsResolved_TheStepsItsDefinitions_ElseTheProvidersOwn()
    {
        var providers = Providers();
        var byTier = AgentResolution.Of(providers, "balanced", null, null, null, null, null);
        var throughDefinition = AgentResolution.Of(providers, "balanced", null, null, null, null, new AgentDefinition("worker", "does the work", "You do the work.", Client: "claude-api"));
        var named = AgentResolution.Of(providers, null, "claude-fable-5-1", null, null, null, new AgentDefinition("worker", "does the work", "You do the work.", Client: "claude-api"));

        Assert.Equal(("glm", "zai", "glm-5.3", "ZAI_API_KEY"), (byTier?.Serving.Client, byTier?.Serving.Provider, byTier?.Serving.Model, byTier?.Serving.Key));
        Assert.Equal(("claude-api", "glm-5.3"), (throughDefinition?.Serving.Client, throughDefinition?.Serving.Model));
        Assert.Equal(("claude-api", "ANTHROPIC_API_KEY"), (named?.Serving.Client, named?.Serving.Key));
    }
}
