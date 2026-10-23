using System.Text.Json;
using DssHarness.Core.Configuration;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Help;
using DssHarness.Core.Orchestration.Flow;
using DssHarness.Core.Results;
using DssHarness.Core.Runners;

namespace DssHarness.Tests;

/// <summary>
/// What only the repository can answer about the workflows it declares: that each definition file is there, reads,
/// holds, and names files that are there - the one command that reads a workflow's whole definition and its files
/// together, changing nothing.
/// </summary>
public sealed class WorkflowValidationServiceTests
{
    /// <summary>
    /// The implementation workflow, initialized into a repository and declared in its config, passes its check: the one
    /// line says what it is, and under it each reason step's models and the level each runs at - the plan's own among
    /// them, xhigh on Fable 5.1 and max on GLM 5.3 - and the outcome says every one checked.
    /// </summary>
    [Fact]
    public async Task TheImplementationWorkflow_InitializedIntoARepository_PassesItsCheck()
    {
        using var repository = await RepositoryAsync(config: null);
        var cancellationToken = TestContext.Current.CancellationToken;

        var report = await Service(repository).CheckAsync(repository.Path, named: null, cancellationToken);

        Assert.True(report.Passed);
        Assert.Equal($"ok {Shown(Default)} (80 steps, 46 recovering, 46 reasoning)", report.Lines[0]);
        Assert.Contains("step 'planning.plan' (best): claude-fable-5-1 at xhigh, glm-5.3 at max", report.Lines);
        Assert.Contains("step 'intake.triage' (high): claude-opus-5-5 at high, glm-5.3 at high", report.Lines);

        // Whether each model a step may run on has a price, and when its provider's numbers were read: every model of
        // the configuration init writes is priced, each provider's line naming its date.
        Assert.Equal(
            [
                "prices of 'anthropic' (USD, read 2026-10-12): all 4 models priced",
                "prices of 'zai' (USD, read 2026-10-12): glm-5.3 priced",
            ],
            report.Lines.Where(line => line.StartsWith("prices of", StringComparison.Ordinal)));

        // The slot holds no key in a repository just initialized. Every step's first model is Anthropic's, reached through
        // claude-code, which signs in with the machine's login and reads no key: nothing is said of Anthropic's. GLM is a
        // later model of each tier, reached through glm, which signs each request with Z.AI's key - named once, with
        // every step that would read it.
        var missingKeys = report.Lines.Where(line => line.StartsWith("no file under", StringComparison.Ordinal)).ToList();
        var missing = Assert.Single(missingKeys);

        Assert.StartsWith(
            $"no file under '{DssHarness.Core.Repository.HarnessLayout.WorkflowsEnvSlotRelative}/' declares 'ZAI_API_KEY', the name provider 'zai' appoints for its key (its apiKeyNames); "
                + "add ZAI_API_KEY= to a file there, holding the Z.AI API key (it is read from this slot and nowhere else). "
                + "The steps that would read it: step 'intake.priority' (a later model of its tier), step 'intake.triage' (a later model of its tier), step 'planning.plan' (a later model of its tier)",
            missing);
        Assert.EndsWith(", advisor (a later model of its tier), reporter (a later model of its tier)", missing);
        Assert.DoesNotContain(report.Lines, line => line.Contains("ANTHROPIC_API_KEY", StringComparison.Ordinal));

        var outcome = WorkflowValidationReports.Render(report, json: false);

        Assert.Equal(HarnessExit.Success, outcome.ExitCode);
        Assert.Equal("1 workflow(s) checked, all valid", outcome.Message);
    }

    /// <summary>
    /// A slot that holds a provider's key takes its missing-key line away, and the check passes either way: which names
    /// the slot holds is no problem of the definition, so a machine that only checks a configuration needs no key.
    /// </summary>
    [Fact]
    public async Task AProviderWhoseKeyTheSlotHolds_IsNotNamedAsMissing()
    {
        using var repository = await RepositoryAsync(config: null);
        File.WriteAllText(Path.Combine(repository.Path, ".harness-config", "workflows", ".env", "keys.env"), "ZAI_API_KEY=one\n");

        var report = await Service(repository).CheckAsync(repository.Path, named: null, TestContext.Current.CancellationToken);

        Assert.True(report.Passed);
        Assert.DoesNotContain(report.Lines, line => line.StartsWith("no file under", StringComparison.Ordinal));
    }

    /// <summary>
    /// What a step's model needs of the slot is decided by the client that reaches it - the step's own where it names
    /// one, else its provider's: a step on claude-api reads its provider's key, and a step on claude-code, which signs in
    /// with the machine's login, reads none, so it is never said to need one.
    /// </summary>
    [Fact]
    public async Task AStepOnTheClaudeApiClient_ReadsItsProvidersKey_WhereOneOnClaudeCodeReadsNone()
    {
        using var repository = await RepositoryAsync(new HarnessConfig { Workflows = ["small"] });
        var cancellationToken = TestContext.Current.CancellationToken;

        await WriteAsync(
            repository,
            "small",
            """
            name: small
            steps:
              - name: think
                type: reason
                agent: worker
                model: claude-opus-5-5
                client: claude-api
                prompt: prompts/work.md
                outputs: schemas/work.json
              - name: write
                type: reason
                agent: worker
                model: claude-sonnet-5-5
                prompt: prompts/work.md
                outputs: schemas/work.json
            """,
            cancellationToken,
            "agents/worker.md",
            "prompts/work.md",
            "schemas/work.json");

        var report = await Service(repository).CheckAsync(repository.Path, named: null, cancellationToken);

        Assert.True(report.Passed);
        Assert.Equal(
            [
                $"ok {Shown("small")} (2 steps, 2 reasoning)",
                "step 'think': claude-opus-5-5 at its own default",
                "step 'write': claude-sonnet-5-5 at its own default",
                "prices of 'anthropic' (USD, read 2026-10-12): claude-opus-5-5, claude-sonnet-5-5 priced",
                $"no file under '{DssHarness.Core.Repository.HarnessLayout.WorkflowsEnvSlotRelative}/' declares 'ANTHROPIC_API_KEY', the name provider 'anthropic' appoints for its key (its apiKeyNames); "
                    + "add ANTHROPIC_API_KEY= to a file there, holding the Anthropic API key (it is read from this slot and nowhere else). The steps that would read it: step 'think'",
            ],
            report.Lines);
    }

    /// <summary>
    /// Every key of a provider the slot does not hold is named, the ones beside a key it holds among them - each name
    /// by the one builder of the missing-key sentence, ending with the names the slot holds that nothing appoints.
    /// </summary>
    [Fact]
    public async Task EveryKeyTheSlotLacks_IsNamed_BesideAKeyItHolds()
    {
        var zai = new AgentProviderConfig
        {
            Name = "zai",
            Type = "zai",
            Api = "https://api.z.ai/api/paas/v4",
            ApiKeyNames = ["ZAI_API_KEY", "ZAI_API_KEY_TWO"],
            Client = "glm",
            Models = [new AgentModelConfig { Code = "glm-5.3", Name = "GLM 5.3", Tiers = ["balanced"], Efforts = ["low", "high", "max"], ContextTokens = 1_000_000, MaxOutputTokens = 131_072 }],
        };

        using var repository = await RepositoryAsync(new HarnessConfig
        {
            Workflows = ["small"],
            Providers = [zai],
            TierConfig = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase) { ["balanced"] = ["glm-5.3"] },
        });
        var cancellationToken = TestContext.Current.CancellationToken;

        await WriteAsync(
            repository,
            "small",
            """
            name: small
            steps:
              - name: write
                type: reason
                agent: worker
                prompt: prompts/work.md
                outputs: schemas/work.json
            """,
            cancellationToken,
            "agents/worker.md",
            "prompts/work.md",
            "schemas/work.json");

        var slot = Path.Combine(repository.Path, ".harness-config", "workflows", ".env", "keys.env");
        var prefix = $"no file under '{DssHarness.Core.Repository.HarnessLayout.WorkflowsEnvSlotRelative}/' declares";

        File.WriteAllText(slot, "ZAI_API_KEY=one\nZAI_APIKEY=two\n");
        var one = await Service(repository).CheckAsync(repository.Path, named: null, cancellationToken);

        Assert.True(one.Passed);
        Assert.Equal(
            [
                $"{prefix} 'ZAI_API_KEY_TWO', the name provider 'zai' appoints for its key (its apiKeyNames); add ZAI_API_KEY_TWO= to a file there, "
                    + "holding the Z.AI API key (it is read from this slot and nowhere else). The steps that would read it: step 'write'. "
                    + "Declared there and appointed by nothing: ZAI_APIKEY",
            ],
            one.Lines.Where(line => line.StartsWith("no file under", StringComparison.Ordinal)));

        // A model with no price is said so, with the run's cost of it unknown, beside the levels.
        Assert.Contains("prices of 'zai': none; the cost of glm-5.3 is unknown", one.Lines);

        File.Delete(slot);
        var none = await Service(repository).CheckAsync(repository.Path, named: null, cancellationToken);

        Assert.True(none.Passed);
        Assert.Equal(
            [
                $"{prefix} 'ZAI_API_KEY' or 'ZAI_API_KEY_TWO', the names provider 'zai' appoints for its keys (its apiKeyNames); add ZAI_API_KEY= or ZAI_API_KEY_TWO= "
                    + "to a file there, holding the Z.AI API key (it is read from this slot and nowhere else). The steps that would read one of them: step 'write'",
            ],
            none.Lines.Where(line => line.StartsWith("no file under", StringComparison.Ordinal)));
    }

    /// <summary>
    /// A workflow whose file is not there is said so, naming where it was looked for and what writes it; and refused as a
    /// configuration that does not hold is (12), counting the one workflow with problems.
    /// </summary>
    [Fact]
    public async Task AWorkflowWhoseFileIsNotThere_IsSaidSo_AndRefusedAsConfigurationThatDoesNotHold()
    {
        using var repository = await RepositoryAsync(config: null);
        File.Delete(Path.Combine(repository.Path, ".harness-config", "workflows", Default, $"{Default}.yml"));

        var report = await Service(repository).CheckAsync(repository.Path, named: null, TestContext.Current.CancellationToken);

        Assert.False(report.Passed);
        Assert.Equal(
            $"{Shown(Default)} is not there; a workflow is defined by that file, written by 'dssharness init' where it is the implementation one",
            Assert.Single(report.Lines));

        var outcome = WorkflowValidationReports.Render(report, json: false);

        Assert.Equal(HarnessExit.ConfigInvalid, outcome.ExitCode);
        Assert.Equal("0 workflow(s) valid, 1 with problems. Nothing was run.", outcome.Message);
    }

    /// <summary>
    /// A configuration that declares no workflow - every one written before workflows were - has nothing to check: that
    /// is said, with what declares the one init writes, and refused (13), never passed as an empty check, nor counted as
    /// "0 valid, 0 with problems" with no reason.
    /// </summary>
    [Fact]
    public async Task AConfigurationDeclaringNoWorkflow_IsRefused_SayingHowToDeclareTheImplementationOne()
    {
        using var repository = await RepositoryAsync(new HarnessConfig());

        var report = await Service(repository).CheckAsync(repository.Path, named: null, TestContext.Current.CancellationToken);

        Assert.False(report.Passed);
        Assert.Empty(report.Workflows);

        var outcome = WorkflowValidationReports.Render(report, json: true);

        Assert.Equal(HarnessExit.Refused, outcome.ExitCode);
        Assert.Equal(
            $"config.json declares no workflow, so there is none to check; \"workflows\": [\"{Default}\"] declares the one "
                + "'dssharness init' writes, and init then writes its files. Nothing was run.",
            outcome.Message);

        using var document = JsonDocument.Parse(Assert.Single(outcome.Data));

        Assert.Equal("""{"workflows":[],"passed":false}""", JsonSerializer.Serialize(document.RootElement));
    }

    /// <summary>
    /// The summary counts workflows, never problems: a workflow with three problems beside one that holds is one valid and
    /// one with problems, however many lines its problems take.
    /// </summary>
    [Fact]
    public async Task TheSummary_CountsWorkflows_NotTheirProblems()
    {
        using var repository = await RepositoryAsync(new HarnessConfig { Workflows = [Default, "small"] });
        var cancellationToken = TestContext.Current.CancellationToken;

        await WriteAsync(
            repository,
            "small",
            """
            name: small
            steps:
              - name: make
                type: create-agent
                agent: implementer
              - name: draft
                type: reason
                agent: worker
                prompt: prompts/work.md
                outputs: schemas/work.json
                dependsOn: [make]
            """,
            cancellationToken);

        var report = await Service(repository).CheckAsync(repository.Path, named: null, cancellationToken);

        Assert.Equal($"ok {Shown(Default)} (80 steps, 46 recovering, 46 reasoning)", report.Lines[0]);
        Assert.All(
            report.Lines.Skip(1).TakeWhile(line => !line.StartsWith(Shown("small"), StringComparison.Ordinal)),
            line => Assert.True(
                line.StartsWith("step ", StringComparison.Ordinal)
                    || line.StartsWith("advisor ", StringComparison.Ordinal)
                    || line.StartsWith("reporter ", StringComparison.Ordinal)
                    || line.StartsWith("prices of", StringComparison.Ordinal)
                    || line.StartsWith("no file under", StringComparison.Ordinal),
                $"a level, price or missing-key line: {line}"));
        Assert.Equal(
            [
                $"{Shown("small")}: 'agents/implementer.md', step 'make''s agent, is not at '.harness-config/workflows/small/agents/implementer.md'; write it there, beside the definition that names it",
                $"{Shown("small")}: 'agents/worker.md', step 'draft''s agent, is not at '.harness-config/workflows/small/agents/worker.md'; write it there, beside the definition that names it",
                $"{Shown("small")}: 'prompts/work.md', step 'draft''s prompt, is not at '.harness-config/workflows/small/prompts/work.md'; write it there, beside the definition that names it",
                $"{Shown("small")}: 'schemas/work.json', step 'draft''s outputs, is not at '.harness-config/workflows/small/schemas/work.json'; write it there, beside the definition that names it",
            ],
            report.Lines.Skip(1).SkipWhile(line =>
                line.StartsWith("step ", StringComparison.Ordinal)
                    || line.StartsWith("advisor ", StringComparison.Ordinal)
                    || line.StartsWith("reporter ", StringComparison.Ordinal)
                    || line.StartsWith("prices of", StringComparison.Ordinal)
                    || line.StartsWith("no file under", StringComparison.Ordinal)));
        Assert.Equal([true, false], report.Workflows.Select(workflow => workflow.Holds));

        var outcome = WorkflowValidationReports.Render(report, json: false);

        Assert.Equal(HarnessExit.ConfigInvalid, outcome.ExitCode);
        Assert.Equal("1 workflow(s) valid, 1 with problems. Nothing was run.", outcome.Message);
    }

    /// <summary>A workflow nobody declared is a usage error, naming what is declared and where the file would be.</summary>
    [Fact]
    public async Task AWorkflowNobodyDeclared_IsAUsageError()
    {
        using var repository = await RepositoryAsync(config: null);
        var refusal = await Assert.ThrowsAsync<HarnessException>(
            () => Service(repository).CheckAsync(repository.Path, "release", TestContext.Current.CancellationToken));

        Assert.Equal(HarnessExit.UsageError, refusal.ExitCode);
        Assert.Equal(
            $"'release' is not declared under workflows. Declared: {Default}. A workflow is defined by '.harness-config/workflows/release/release.yml'.",
            refusal.Message);
    }

    /// <summary>
    /// A file that names itself what its directory and its file are not is refused: a copied directory must not pass as
    /// the workflow it was copied from.
    /// </summary>
    [Fact]
    public async Task AFileNamingAnotherName_IsRefused()
    {
        using var repository = await RepositoryAsync(config: null);
        var file = Path.Combine(repository.Path, ".harness-config", "workflows", Default, $"{Default}.yml");
        var read = await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken);

        await File.WriteAllTextAsync(
            file,
            read.Replace($"name: {Default}", "name: release", StringComparison.Ordinal),
            TestContext.Current.CancellationToken);

        var report = await Service(repository).CheckAsync(repository.Path, named: null, TestContext.Current.CancellationToken);

        Assert.False(report.Passed);
        Assert.Equal(
            $"{Shown(Default)} names itself 'release'; a workflow's name is its directory's and its file's, said again in the file so a copied directory cannot pass as the workflow it was copied from",
            Assert.Single(report.Lines));
    }

    /// <summary>
    /// A file the definition names that is not there is said, beside the definition that names it - its agent, its
    /// prompt, its schema - and a runner or a leg no configuration declares is said the same way.
    /// </summary>
    [Fact]
    public async Task FilesTheDefinitionNames_AndRunnersAndLegsNobodyDeclares_AreEachSaid()
    {
        using var repository = await RepositoryAsync(new HarnessConfig { Workflows = ["small"] });
        var cancellationToken = TestContext.Current.CancellationToken;

        await WriteAsync(
            repository,
            "small",
            """
            name: small
            steps:
              - name: build
                type: build
                legs: [ghost]
              - name: make
                type: create-agent
                agent: implementer
              - name: draft
                type: reason
                agent: worker
                prompt: prompts/work.md
                outputs: schemas/work.json
                dependsOn: [make]
              - name: corpus
                type: run
                runner: missing-runner
            """,
            cancellationToken);

        var report = await Service(repository).CheckAsync(repository.Path, "small", cancellationToken);

        Assert.False(report.Passed);
        Assert.Equal(
            [
                ".harness-config/workflows/small/small.yml: step 'build' names 'ghost', which is neither a leg nor a leg set: 'ghost' is not declared under legs or legSets. Nothing is declared there.",
                ".harness-config/workflows/small/small.yml: 'agents/implementer.md', step 'make''s agent, is not at '.harness-config/workflows/small/agents/implementer.md'; write it there, beside the definition that names it",
                ".harness-config/workflows/small/small.yml: 'agents/worker.md', step 'draft''s agent, is not at '.harness-config/workflows/small/agents/worker.md'; write it there, beside the definition that names it",
                ".harness-config/workflows/small/small.yml: 'prompts/work.md', step 'draft''s prompt, is not at '.harness-config/workflows/small/prompts/work.md'; write it there, beside the definition that names it",
                ".harness-config/workflows/small/small.yml: 'schemas/work.json', step 'draft''s outputs, is not at '.harness-config/workflows/small/schemas/work.json'; write it there, beside the definition that names it",
                ".harness-config/workflows/small/small.yml: step 'corpus' runs 'missing-runner': 'missing-runner' is not declared under predefinedRunners. Nothing is declared there.",
            ],
            report.Lines);
    }

    /// <summary>
    /// The recovery's steps name files as the flow's do, and each is held to the workflow's directory the same way, said
    /// under the recovery's own path.
    /// </summary>
    [Fact]
    public async Task FilesTheRecoveryNames_AreEachSaid_AsTheFlowsAre()
    {
        using var repository = await RepositoryAsync(new HarnessConfig { Workflows = ["small"] });
        var cancellationToken = TestContext.Current.CancellationToken;

        await WriteAsync(
            repository,
            "small",
            """
            name: small
            steps:
              - name: orient
                type: orient
                onFailure: recover
            recover:
              onSuccess: failed-step
              onFailure: pause
              steps:
                - name: diagnose
                  type: reason
                  agent: diagnostician
                  prompt: prompts/diagnose.md
                  outputs: schemas/diagnosis.json
            """,
            cancellationToken);

        var report = await Service(repository).CheckAsync(repository.Path, "small", cancellationToken);

        Assert.False(report.Passed);
        Assert.Equal(
            [
                ".harness-config/workflows/small/small.yml: 'agents/diagnostician.md', step 'recover.diagnose''s agent, is not at '.harness-config/workflows/small/agents/diagnostician.md'; write it there, beside the definition that names it",
                ".harness-config/workflows/small/small.yml: 'prompts/diagnose.md', step 'recover.diagnose''s prompt, is not at '.harness-config/workflows/small/prompts/diagnose.md'; write it there, beside the definition that names it",
                ".harness-config/workflows/small/small.yml: 'schemas/diagnosis.json', step 'recover.diagnose''s outputs, is not at '.harness-config/workflows/small/schemas/diagnosis.json'; write it there, beside the definition that names it",
            ],
            report.Lines);
    }

    /// <summary>
    /// Every file a definition names beyond its steps' agents, prompts and schemas is held to the workflow's directory the
    /// same way: the bar for recommendations, the advisor's agent and prompt, the reporter's agent and both its prompts,
    /// a step's resume prompt and a pull request's body - each said where it is not there.
    /// </summary>
    [Fact]
    public async Task TheBarTheAdvisorTheReporterAResumePromptAndABody_AreEachHeldToTheWorkflowsDirectory()
    {
        using var repository = await RepositoryAsync(new HarnessConfig { Workflows = ["small"] });
        var cancellationToken = TestContext.Current.CancellationToken;

        await WriteAsync(
            repository,
            "small",
            """
            name: small
            recommendations: bar.md
            advisor:
              agent: counsel
              prompt: prompts/counsel.md
            reporter:
              agent: teller
              statusPrompt: prompts/status.md
              askPrompt: prompts/ask.md
            steps:
              - name: drafts
                repeat:
                  until: 'draft.done'
                  maxIterations: 2
                steps:
                  - name: draft
                    type: reason
                    agent: worker
                    resume: draft
                    prompt: prompts/work.md
                    resumePrompt: prompts/again.md
                    outputs: schemas/work.json
              - name: pr
                type: pull-request
                dependsOn: [drafts]
                title: 'work'
                body: prompts/body.md
            """,
            cancellationToken,
            "agents/worker.md",
            "prompts/work.md",
            "schemas/work.json");

        var report = await Service(repository).CheckAsync(repository.Path, "small", cancellationToken);

        Assert.Equal(
            [
                .. new[]
                {
                    "'bar.md', the bar for recommendations, is not at '.harness-config/workflows/small/bar.md'",
                    "'agents/counsel.md', the agent the advisor runs, is not at '.harness-config/workflows/small/agents/counsel.md'",
                    "'prompts/counsel.md', the advisor's prompt, is not at '.harness-config/workflows/small/prompts/counsel.md'",
                    "'agents/teller.md', the agent the reporter's status prompt runs, is not at '.harness-config/workflows/small/agents/teller.md'",
                    "'prompts/status.md', the reporter's status prompt's prompt, is not at '.harness-config/workflows/small/prompts/status.md'",
                    "'agents/teller.md', the agent the reporter's ask prompt runs, is not at '.harness-config/workflows/small/agents/teller.md'",
                    "'prompts/ask.md', the reporter's ask prompt's prompt, is not at '.harness-config/workflows/small/prompts/ask.md'",
                    "'prompts/again.md', step 'drafts.draft''s resume prompt, is not at '.harness-config/workflows/small/prompts/again.md'",
                    "'prompts/body.md', step 'pr''s body, is not at '.harness-config/workflows/small/prompts/body.md'",
                }.Select(problem => $"{Shown("small")}: {problem}; write it there, beside the definition that names it"),
            ],
            report.Lines);
    }

    /// <summary>
    /// A path a definition names that is not spelt as one inside the workflow's directory is refused before anything is
    /// looked at: rooted by any platform's rules - a drive as well as a leading slash, whichever machine reads it - or
    /// holding '..', '.' or an empty part, as an action's path is.
    /// </summary>
    [Fact]
    public async Task APathNotSpeltInsideTheWorkflowsDirectory_IsRefused_OnEveryPlatformAlike()
    {
        using var repository = await RepositoryAsync(new HarnessConfig { Workflows = ["small"] });
        var cancellationToken = TestContext.Current.CancellationToken;

        await WriteAsync(
            repository,
            "small",
            """
            name: small
            steps:
              - name: rooted
                type: reason
                agent: worker
                prompt: /prompts/work.md
                outputs: schemas/work.json
              - name: drive
                type: reason
                agent: worker
                prompt: C:/prompts/work.md
                outputs: schemas/work.json
              - name: climbs
                type: reason
                agent: worker
                prompt: ../default/prompts/work.md
                outputs: schemas/work.json
              - name: here
                type: reason
                agent: worker
                prompt: ./prompts/work.md
                outputs: schemas/work.json
              - name: gap
                type: reason
                agent: worker
                prompt: prompts//work.md
                outputs: schemas/work.json
            """,
            cancellationToken,
            "agents/worker.md",
            "prompts/work.md",
            "schemas/work.json");

        var report = await Service(repository).CheckAsync(repository.Path, "small", cancellationToken);
        var relative = "'.harness-config/workflows/small/'";

        Assert.Equal(
            [
                $"{Shown("small")}: '/prompts/work.md', step 'rooted''s prompt, is an absolute path; every file a workflow names is relative to {relative}, as an action's are to its own directory",
                $"{Shown("small")}: 'C:/prompts/work.md', step 'drive''s prompt, is an absolute path; every file a workflow names is relative to {relative}, as an action's are to its own directory",
                $"{Shown("small")}: '../default/prompts/work.md', step 'climbs''s prompt, does not name a file inside the workflow's directory; every file a workflow names is relative to {relative}, without '.', '..' or an empty part, as an action's are to its own directory",
                $"{Shown("small")}: './prompts/work.md', step 'here''s prompt, does not name a file inside the workflow's directory; every file a workflow names is relative to {relative}, without '.', '..' or an empty part, as an action's are to its own directory",
                $"{Shown("small")}: 'prompts//work.md', step 'gap''s prompt, does not name a file inside the workflow's directory; every file a workflow names is relative to {relative}, without '.', '..' or an empty part, as an action's are to its own directory",
            ],
            report.Lines);
    }

    /// <summary>
    /// A schema is a JSON object: one that does not read as JSON is said with the reader's reason, and one that reads as
    /// another value - a list - is said to hold no object.
    /// </summary>
    [Fact]
    public async Task ASchemaThatIsNoJsonObject_IsSaid()
    {
        using var repository = await RepositoryAsync(new HarnessConfig { Workflows = ["small"] });
        var cancellationToken = TestContext.Current.CancellationToken;

        await WriteAsync(
            repository,
            "small",
            """
            name: small
            steps:
              - name: broken
                type: reason
                agent: worker
                prompt: prompts/work.md
                outputs: schemas/broken.json
              - name: listed
                type: reason
                agent: worker
                prompt: prompts/work.md
                outputs: schemas/listed.json
            """,
            cancellationToken,
            "agents/worker.md",
            "prompts/work.md",
            "schemas/broken.json",
            "schemas/listed.json");
        await File.WriteAllTextAsync(Path.Combine(repository.Path, ".harness-config", "workflows", "small", "schemas", "broken.json"), "{ not json", cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(repository.Path, ".harness-config", "workflows", "small", "schemas", "listed.json"), "[]", cancellationToken);

        var report = await Service(repository).CheckAsync(repository.Path, "small", cancellationToken);

        Assert.Equal(
            [
                $"{Shown("small")}: 'schemas/broken.json', step 'broken''s outputs, is not valid JSON ({JsonReason("{ not json")})",
                $"{Shown("small")}: 'schemas/listed.json', step 'listed''s outputs, holds no JSON object; a schema is a mapping of properties",
            ],
            report.Lines);
    }

    /// <summary>
    /// A definition the disk will not give up - held by another process, or not this user's to read - is that workflow's
    /// problem, named with the disk's reason, and every other declared workflow is still checked: raised, it ended the
    /// whole check as the tool's own failure (70).
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ADefinitionThatCannotBeRead_IsThatWorkflowsProblem_AndEveryOtherIsStillChecked(bool denied)
    {
        using var repository = await RepositoryAsync(new HarnessConfig { Workflows = ["small", Default] });
        var cancellationToken = TestContext.Current.CancellationToken;

        await WriteAsync(repository, "small", "name: small\nsteps: []\n", cancellationToken);

        var held = new Held(repository.Harness.FileSystem, Path.Combine(repository.Path, ".harness-config", "workflows", "small", "small.yml"), denied);
        var report = await Service(repository, held).CheckAsync(repository.Path, named: null, cancellationToken);

        Assert.Equal($"{Shown("small")} could not be read: {Held.Why}", report.Lines[0]);
        Assert.Equal($"ok {Shown(Default)} (80 steps, 46 recovering, 46 reasoning)", report.Lines[1]);
        Assert.All(
            report.Lines.Skip(2),
            line => Assert.True(
                line.StartsWith("step ", StringComparison.Ordinal)
                    || line.StartsWith("advisor ", StringComparison.Ordinal)
                    || line.StartsWith("reporter ", StringComparison.Ordinal)
                    || line.StartsWith("prices of", StringComparison.Ordinal)
                    || line.StartsWith("no file under", StringComparison.Ordinal),
                $"a level, price or missing-key line: {line}"));

        // Z.AI's key alone: Anthropic's models are reached through claude-code, which reads none.
        Assert.Equal(1, report.Lines.Count(line => line.StartsWith("no file under", StringComparison.Ordinal)));
    }

    /// <summary>A schema the disk will not give up is a problem naming it, with the disk's reason, as a missing one is.</summary>
    [Fact]
    public async Task ASchemaThatCannotBeRead_IsAProblemNamingIt()
    {
        using var repository = await RepositoryAsync(new HarnessConfig { Workflows = ["small"] });
        var cancellationToken = TestContext.Current.CancellationToken;

        await WriteAsync(
            repository,
            "small",
            """
            name: small
            steps:
              - name: draft
                type: reason
                agent: worker
                prompt: prompts/work.md
                outputs: schemas/work.json
            """,
            cancellationToken,
            "agents/worker.md",
            "prompts/work.md",
            "schemas/work.json");

        var held = new Held(repository.Harness.FileSystem, Path.Combine(repository.Path, ".harness-config", "workflows", "small", "schemas", "work.json"));
        var report = await Service(repository, held).CheckAsync(repository.Path, "small", cancellationToken);

        Assert.Equal(
            [$"{Shown("small")}: 'schemas/work.json', step 'draft''s outputs, could not be read: {Held.Why}"],
            report.Lines);
    }

    /// <summary>
    /// A commit step writes the message the configuration leaves it: the same definition holds where config.json declares a
    /// commit.template, and is refused where it declares none - the service hands the validator what the configuration says.
    /// </summary>
    [Fact]
    public async Task ACommitStepsMessage_IsJudgedByTheConfigurationsCommitTemplate()
    {
        const string Definition = """
            name: small
            steps:
              - name: make
                type: create-agent
                agent: worker
              - name: save
                type: commit
                dependsOn: [make]
            """;

        using var templated = await RepositoryAsync(new HarnessConfig { Workflows = ["small"], Commit = new CommitConfig { Template = "work" } });
        using var plain = await RepositoryAsync(new HarnessConfig { Workflows = ["small"] });
        var cancellationToken = TestContext.Current.CancellationToken;

        await WriteAsync(templated, "small", Definition, cancellationToken, "agents/worker.md");
        await WriteAsync(plain, "small", Definition, cancellationToken, "agents/worker.md");

        Assert.Equal(
            [$"ok {Shown("small")} (2 steps, 0 reasoning)"],
            (await Service(templated).CheckAsync(templated.Path, "small", cancellationToken)).Lines);
        Assert.Equal(
            [$"{Shown("small")}: step 'save' names no message and the configuration declares no commit.template; one of the two writes the commit's message"],
            (await Service(plain).CheckAsync(plain.Path, "small", cancellationToken)).Lines);
    }

    /// <summary>
    /// A run step whose runner writes the tree names no 'in': it would work in the main tree, which a runner that
    /// changes files never does.
    /// </summary>
    [Fact]
    public async Task ARunStepWhoseRunnerWritesTheTree_NamesAnAgentToWorkIn()
    {
        using var repository = await RepositoryAsync(
            new HarnessConfig
            {
                Workflows = ["small"],
                PredefinedRunners = new Dictionary<string, RunnerConfig>(StringComparer.OrdinalIgnoreCase)
                {
                    ["formatter"] = new RunnerConfig
                    {
                        WritesTree = true,
                        Phases = [new RunnerPhase { Name = "format", Command = ["tool"] }],
                    },
                },
            });

        var cancellationToken = TestContext.Current.CancellationToken;

        await WriteAsync(
            repository,
            "small",
            """
            name: small
            steps:
              - name: format
                type: run
                runner: formatter
            """,
            cancellationToken);

        var report = await Service(repository).CheckAsync(repository.Path, "small", cancellationToken);

        Assert.False(report.Passed);
        Assert.Equal(
            [".harness-config/workflows/small/small.yml: step 'format' runs 'formatter', which writes the tree (writesTree), on the main tree: the run reads its definition there and folds into it, so a runner that writes works in an agent's worktree. Name the create-agent step with 'in'"],
            report.Lines);
    }

    /// <summary>
    /// V24: a runner a run step's runner names in its run checks runs within the step's legs, so one that writes the tree
    /// holds the step to an agent's worktree as the step's own runner would; named with in, the step holds.
    /// </summary>
    [Fact]
    public async Task ARunStepWhoseRunnersRunCheckWritesTheTree_NamesAnAgentToWorkIn()
    {
        using var repository = await RepositoryAsync(
            new HarnessConfig
            {
                Workflows = ["small"],
                PredefinedRunners = new Dictionary<string, RunnerConfig>(StringComparer.OrdinalIgnoreCase)
                {
                    ["formatter"] = new RunnerConfig
                    {
                        WritesTree = true,
                        Phases = [new RunnerPhase { Name = "format", Command = ["tool"] }],
                    },
                    ["lint"] = new RunnerConfig
                    {
                        Phases = [new RunnerPhase { Name = "lint", Command = ["tool"] }],
                        ExpectedExceptions =
                        [
                            new ExpectedException
                            {
                                ExceptionType = "Flake",
                                Messages = ["it flaked"],
                                Message = "excused: the formatter was mid-write",
                                EarnedOn = "2026-10-13",
                                EarnedAt = "local",
                                Mechanism = "the formatter rewrites what lint reads",
                                Anchor = "D-AREA-TOPIC-ONE",
                                RunChecks = [new RunCheck { PredefinedRunner = "formatter" }],
                            },
                        ],
                    },
                },
            });

        var cancellationToken = TestContext.Current.CancellationToken;

        await WriteAsync(
            repository,
            "small",
            """
            name: small
            steps:
              - name: make
                type: create-agent
                agent: worker
              - name: lint
                type: run
                runner: lint
              - name: lint-in-place
                type: run
                runner: lint
                in: make
                dependsOn: [make]
            """,
            cancellationToken,
            "agents/worker.md");

        var report = await Service(repository).CheckAsync(repository.Path, "small", cancellationToken);

        Assert.Equal(
            [".harness-config/workflows/small/small.yml: step 'lint' runs 'lint', whose run check 'formatter' writes the tree (writesTree), on the main tree: the run reads its definition there and folds into it, so a runner that writes works in an agent's worktree. Name the create-agent step with 'in'"],
            report.Lines);
    }

    /// <summary>
    /// V27: the agents a run makes are named 'a' and a number that grows to the workflow's most step instances, each
    /// agent made by one at the most, and every name fits worktrees.maxNameLength as an agent's must when it is made. A
    /// workflow that makes no agent, or whose bound keeps the number short enough, holds.
    /// </summary>
    [Fact]
    public async Task TheAgentsARunMakes_AreNamedWithinTheWorktreesBound()
    {
        using var repository = await RepositoryAsync(new HarnessConfig { Workflows = ["small"], Worktrees = new WorktreeSettings { MaxNameLength = 4 } });
        var cancellationToken = TestContext.Current.CancellationToken;
        const string Makes = """
            name: small
            steps:
              - name: make
                type: create-agent
                agent: worker
            """;

        await WriteAsync(repository, "small", Makes, cancellationToken, "agents/worker.md");

        Assert.Equal(
            [$"{Shown("small")}: the workflow creates agents, whose generated names ('a' and up to 4 digits) are 5 characters, and worktrees.maxNameLength is 4"],
            (await Service(repository).CheckAsync(repository.Path, "small", cancellationToken)).Lines);

        // A bound of 999 instances numbers no agent past a999.
        await WriteAsync(repository, "small", Makes.Replace("name: small\n", "name: small\nmaxInstances: 999\n", StringComparison.Ordinal), cancellationToken, "agents/worker.md");

        Assert.Equal(
            [$"ok {Shown("small")} (1 step, 0 reasoning)"],
            (await Service(repository).CheckAsync(repository.Path, "small", cancellationToken)).Lines);

        await WriteAsync(repository, "small", "name: small\nsteps:\n  - name: orient\n    type: orient\n", cancellationToken);

        Assert.Equal(
            [$"ok {Shown("small")} (1 step, 0 reasoning)"],
            (await Service(repository).CheckAsync(repository.Path, "small", cancellationToken)).Lines);
    }

    /// <summary>
    /// V30, where config.json declares a commit.template: a commit step gives a value for every variable the template
    /// requires, and none for a variable it does not declare, which nothing would fill in; one that gives each it
    /// requires holds.
    /// </summary>
    [Fact]
    public async Task ACommitStepsVariables_AreTheOnesTheCommitTemplateDeclares()
    {
        using var repository = await RepositoryAsync(
            new HarnessConfig
            {
                Workflows = ["small"],
                Commit = new CommitConfig
                {
                    Template = "{type}: {summary}",
                    Variables = new Dictionary<string, CommitVariable>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["type"] = new CommitVariable { Required = true },
                        ["summary"] = new CommitVariable { Default = "work" },
                    },
                },
            });

        var cancellationToken = TestContext.Current.CancellationToken;

        await WriteAsync(
            repository,
            "small",
            """
            name: small
            steps:
              - name: save
                type: commit
                variables:
                  summary: done
                  scope: core
              - name: whole
                type: commit
                dependsOn: [save]
                variables:
                  type: feat
            """,
            cancellationToken);

        var report = await Service(repository).CheckAsync(repository.Path, "small", cancellationToken);

        Assert.Equal(
            [
                $"{Shown("small")}: step 'save' is a commit step and gives no value for 'type', which commit.template requires: add it under the step's variables",
                $"{Shown("small")}: step 'save' is a commit step and gives a value for 'scope', which commit.variables does not declare: the template is filled with the variables it declares, and this one would be read by nothing",
            ],
            report.Lines);
    }

    /// <summary>
    /// The help, the README and the architecture say what validate-workflow holds a definition to of the configuration it
    /// is checked under: a runner that writes the tree in an agent's worktree, the agents' names within the worktrees'
    /// bound, and a commit step's variables against the commit template.
    /// </summary>
    [Fact]
    public void TheHelpTheReadmeAndTheArchitecture_SayWhatADefinitionIsHeldToOfTheConfiguration()
    {
        var help = Prose.Words(HelpTopics.Render("workflows"));
        var readme = Prose.Document("README.md");
        var architecture = Prose.Document("docs", "architecture.md");

        Assert.Contains(
            "validate-workflow holds a definition to the config.json it is checked under too: a run step whose runner, or a runner its run checks "
                + "name, writes the tree (writesTree) names with in the create-agent step whose worktree it works in, since the run reads its "
                + "definition in the main tree and folds into it; the agents a run makes, named 'a' and a number that grows to the workflow's "
                + "maxInstances, fit worktrees.maxNameLength; and a commit step gives a value for every variable commit.template requires and none "
                + "for one it does not declare - or, where none is declared, a message and no variables.",
            help,
            StringComparison.Ordinal);
        Assert.Contains(
            "It holds a definition to the configuration too: a `run` step whose runner, or a runner its run checks name, writes the tree "
                + "(`writesTree`) names with `in` the create-agent step whose worktree it works in; the agents a run makes, `a` and a number up to "
                + "the workflow's `maxInstances`, fit `worktrees.maxNameLength`; and a commit step gives a value for every variable "
                + "`commit.template` requires and none for one it does not declare - with no template, a `message` and no `variables`.",
            readme,
            StringComparison.Ordinal);
        Assert.Contains(
            "A definition is held to the `config.json` it is checked under as well: a `run` step whose runner - or a runner its run checks "
                + "name - says `writesTree` names with `in` the create-agent step whose worktree it works in, since the run reads its definition "
                + "in the main tree and folds into it; the agents a run makes, named `a` and a number that grows to the workflow's "
                + "`maxInstances`, fit `worktrees.maxNameLength`; and a commit step gives a value for every variable `commit.template` requires "
                + "and none for one it does not declare - or, with no template, a `message` and no `variables`.",
            architecture,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The help, the README and the architecture say where what only a prompt reads stands - each name in the prompts of
    /// the agent asked it - that the advisor's and the reporter's prompts read no step by name, and that a pull request's
    /// body reads what a template key reads.
    /// </summary>
    [Fact]
    public void TheHelpTheReadmeAndTheArchitecture_SayWhereWhatOnlyAPromptReadsStands()
    {
        var help = Prose.Words(HelpTopics.Render("workflows"));
        var readme = Prose.Document("README.md");
        var architecture = Prose.Document("docs", "architecture.md");

        Assert.Contains(
            "What only a prompt reads stands where its agent is asked it: {recommendations} in a reason step's prompt and the advisor's, "
                + "{question.<name>} in the advisor's alone, {status.<name>} in the reporter's two - {status.question} in its askPrompt alone - "
                + "{update.<name>} in the prompts of the step that plans an update and of the steps of its scope, and {piece.<name>} in a step of "
                + "the work-item group; the advisor's and the reporter's prompts read no step by name, and a pull request's body, which the "
                + "engine fills and no agent reads, reads what a template key reads and no skill.",
            help,
            StringComparison.Ordinal);
        Assert.Contains(
            "What only a prompt reads stands where its agent is asked it: `{recommendations}` in a reason step's prompt and the advisor's, "
                + "`{question.*}` in the advisor's alone, `{status.*}` in the reporter's two - `{status.question}` in its `askPrompt` alone - "
                + "`{update.*}` in the prompts of the step that plans an update and of the steps of its scope, and `{piece.*}` in a step of the "
                + "work-item group; the advisor's and the reporter's prompts read no step by name, and a pull request's `body`, which the engine "
                + "fills and no agent reads, reads what a template key reads and no skill.",
            readme,
            StringComparison.Ordinal);
        Assert.Contains(
            "What only a prompt reads stands where its agent is asked it: `{recommendations}` in a reason step's prompt and the advisor's, "
                + "`{question.*}` in the advisor's alone, `{status.*}` in the reporter's two - `{status.question}` in its `askPrompt` alone - "
                + "`{update.*}` in the prompts of the step that plans an update and of the steps of its scope, and `{piece.*}` in a step of the "
                + "work-item group; the advisor's and the reporter's prompts are read too, and read no step by name, and a pull request's "
                + "`body`, which the engine fills and no agent reads, reads what a template key reads and no skill.",
            architecture,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The help, the README and the architecture say what the engine gives under a question, the status, a plan update
    /// and a piece - the help from the lists the typing reads - and that the bar and each run fact are one text, which
    /// no expression reads.
    /// </summary>
    [Fact]
    public void TheHelpTheReadmeAndTheArchitecture_SayWhatTextsTheEngineGivesAPrompt()
    {
        var help = Prose.Words(HelpTopics.Render("workflows"));
        var readme = Prose.Document("README.md");
        var architecture = Prose.Document("docs", "architecture.md");

        Assert.Contains(
            "A question gives only the texts the engine names (question.step, question.kind, question.problem, question.answers, "
                + "question.record), and so do the status (status.facts, status.question, status.records), a plan update (update.cause, "
                + "update.since, update.work) and a piece (piece.kind, piece.changed, piece.keep, piece.undo, piece.instead, piece.cause, "
                + "piece.before); the bar is one text, and so is each run fact (worktree, work, plans, rows, orchestrator, agent, step, os), "
                + "which a template reads and an expression does not.",
            help,
            StringComparison.Ordinal);
        Assert.Contains(
            "A question gives only the texts the engine names (`step`, `kind`, `problem`, `answers`, `record`), and so do the status "
                + "(`facts`, `question`, `records`), a plan update (`cause`, `since`, `work`) and a piece (`kind`, `changed`, `keep`, `undo`, "
                + "`instead`, `cause`, `before`); the bar is one text, and so is each run fact (`{worktree}`, `{os}` and the rest), which a "
                + "template reads and an expression does not.",
            readme,
            StringComparison.Ordinal);
        Assert.Contains(
            "A question gives only the texts the engine names (`step`, `kind`, `problem`, `answers`, `record`), and so do the status "
                + "(`facts`, `question`, `records`), a plan update (`cause`, `since`, `work`) and a piece (`kind`, `changed`, `keep`, `undo`, "
                + "`instead`, `cause`, `before`) - each list kept once, in `WorkflowExpressions` beside the run's own texts, and read by the "
                + "typing; the bar is one text, and so is each run fact (`WorkflowExpressions.RunFacts`), which a template reads and an "
                + "expression does not.",
            architecture,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A workflow's directory that is a link out of the repository is refused before a word of it is read, though every
    /// file it names is beside its definition there: the files a definition names are held to its directory, so the
    /// directory is held to the workflows directory, once its links are followed, as an action's file is.
    /// </summary>
    [Fact]
    public async Task AWorkflowsDirectoryLinkingOutOfTheRepository_IsRefused()
    {
        using var repository = await RepositoryAsync(new HarnessConfig { Workflows = ["small"] });
        using var elsewhere = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var link = Path.Combine(repository.Path, ".harness-config", "workflows", "small");

        elsewhere.WriteFile("small.yml", "name: small\nsteps: []\n");
        TestLinks.OrSkip(() => TestLinks.DirectoryLink(link, elsewhere.Path));

        var report = await Service(repository).CheckAsync(repository.Path, "small", cancellationToken);
        var fileSystem = repository.Harness.FileSystem;

        Assert.Equal(
            [
                $"{Shown("small")}: its directory leads to '{Path.TrimEndingDirectorySeparator(fileSystem.ResolveLinks(elsewhere.Path))}', which is outside "
                    + $"'{Path.TrimEndingDirectorySeparator(fileSystem.ResolveLinks(Path.Combine(repository.Path, ".harness-config", "workflows")))}'; "
                    + "a workflow's directory is the repository's own, so that what a reviewer reads there is what the workflow runs",
            ],
            report.Lines);
    }

    /// <summary>
    /// A definition file that is a link out of its workflow's directory is refused, as a file the definition names is: what
    /// a reviewer reads in the directory is what the workflow runs. Skipped where this machine does not let this user make
    /// a symbolic link to a file.
    /// </summary>
    [Fact]
    public async Task ADefinitionLinkingOutOfItsDirectory_IsRefused()
    {
        using var repository = await RepositoryAsync(new HarnessConfig { Workflows = ["small"] });
        using var elsewhere = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Path.Combine(repository.Path, ".harness-config", "workflows", "small");
        var target = elsewhere.WriteFile("small.yml", "name: small\nsteps: []\n");

        Directory.CreateDirectory(directory);
        TestLinks.OrSkip(() => File.CreateSymbolicLink(Path.Combine(directory, "small.yml"), target));

        var report = await Service(repository).CheckAsync(repository.Path, "small", cancellationToken);
        var fileSystem = repository.Harness.FileSystem;

        Assert.Equal(
            [
                $"{Shown("small")} leads to '{fileSystem.ResolveLinks(target)}', which is outside "
                    + $"'{Path.TrimEndingDirectorySeparator(fileSystem.ResolveLinks(directory))}'; "
                    + "a workflow's definition is a file of its own directory, so that what a reviewer reads there is what the workflow runs",
            ],
            report.Lines);
    }

    /// <summary>
    /// A file the definition names that is a link out of the workflow's directory is refused, as the definition is:
    /// what a reviewer reads in the directory is what the workflow runs. Skipped where this machine does not let this
    /// user make a symbolic link to a file.
    /// </summary>
    [Fact]
    public async Task ANamedFileLinkingOutOfItsDirectory_IsRefused()
    {
        using var repository = await RepositoryAsync(new HarnessConfig { Workflows = ["small"] });
        using var elsewhere = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Path.Combine(repository.Path, ".harness-config", "workflows", "small");
        var target = elsewhere.WriteFile("work.md", "elsewhere\n");

        await WriteAsync(repository, "small", Work, cancellationToken, "agents/worker.md", "schemas/work.json");
        Directory.CreateDirectory(Path.Combine(directory, "prompts"));
        TestLinks.OrSkip(() => File.CreateSymbolicLink(Path.Combine(directory, "prompts", "work.md"), target));

        var report = await Service(repository).CheckAsync(repository.Path, "small", cancellationToken);
        var fileSystem = repository.Harness.FileSystem;

        Assert.Equal(
            [
                $"{Shown("small")}: 'prompts/work.md', step 'draft''s prompt, leads to '{fileSystem.ResolveLinks(target)}', which is outside "
                    + $"'{Path.TrimEndingDirectorySeparator(fileSystem.ResolveLinks(directory))}'; "
                    + "every file a workflow names is relative to '.harness-config/workflows/small/', as an action's are to its own directory",
            ],
            report.Lines);
    }

    /// <summary>
    /// A file the file system would not look at - the definition, or one it names - is said so, with why, and never as
    /// a file that is not there: the person is not sent to write one that may be there.
    /// </summary>
    [Fact]
    public async Task AFileThatCouldNotBeLookedAt_IsSaidSo_NeverAsNotThere()
    {
        using var repository = await RepositoryAsync(new HarnessConfig { Workflows = ["small"] });
        var cancellationToken = TestContext.Current.CancellationToken;

        await WriteAsync(repository, "small", Work, cancellationToken, "agents/worker.md", "prompts/work.md", "schemas/work.json");

        var prompt = await Service(repository, new Unlooked(repository.Harness.FileSystem, "work.md")).CheckAsync(repository.Path, "small", cancellationToken);
        var definition = await Service(repository, new Unlooked(repository.Harness.FileSystem, "small.yml")).CheckAsync(repository.Path, "small", cancellationToken);

        Assert.Equal(
            [$"{Shown("small")}: 'prompts/work.md', step 'draft''s prompt, could not be looked at: the disk would not say"],
            prompt.Lines);
        Assert.Equal([$"{Shown("small")} could not be looked at: the disk would not say"], definition.Lines);
    }

    /// <summary>
    /// A step's legs may name a leg set as --legs does; a name that is neither is refused, naming every leg and every set
    /// the configuration declares.
    /// </summary>
    [Fact]
    public async Task AStepsLegs_MayNameALegSet_AndANameThatIsNeither_IsRefused()
    {
        using var repository = await RepositoryAsync(
            new HarnessConfig
            {
                Workflows = ["small"],
                BuildConfigs = { ["debug"] = new BuildConfiguration() },
                Legs = { ["linux-x64"] = HostDoubles.Leg("linux", "x86_64") },
                LegSets = { ["linux"] = ["linux-x64"] },
            });

        var cancellationToken = TestContext.Current.CancellationToken;

        await WriteAsync(
            repository,
            "small",
            """
            name: small
            steps:
              - name: build
                type: build
                legs: [linux, linux-x64, ghost]
            """,
            cancellationToken);

        var report = await Service(repository).CheckAsync(repository.Path, "small", cancellationToken);

        Assert.Equal(
            [
                $"{Shown("small")}: step 'build' names 'ghost', which is neither a leg nor a leg set: 'ghost' is not declared under legs or legSets. "
                    + "Declared: linux, linux-x64.",
            ],
            report.Lines);
    }

    /// <summary>
    /// The README and the architecture say the codes validate-workflow answers with - a definition that does not hold as
    /// configuration that does not, and none declared as a request with nothing to check - held to the constants.
    /// </summary>
    [Fact]
    public void TheReadmeAndTheArchitecture_SayTheCodesValidateWorkflowAnswersWith()
    {
        var readme = Prose.Document("README.md");
        var architecture = Prose.Document("docs", "architecture.md");

        Assert.Contains(
            $"and refuses ({HarnessExit.ConfigInvalid}) naming each problem, without running anything, as a `config.json` that does not hold is "
                + $"refused. A `config.json` that declares no workflow has nothing to check and is refused ({HarnessExit.Refused}), saying how to "
                + "declare the implementation one.",
            readme,
            StringComparison.Ordinal);
        Assert.Contains(
            $"Every problem is collected and reported together ({HarnessExit.ConfigInvalid}), without running anything: a definition that does "
                + "not hold is configuration that does not, refused with the code `config.json`, an action file or the parser itself is. A "
                + $"`config.json` that declares no workflow has nothing to check, and is refused ({HarnessExit.Refused}), saying that "
                + $"`\"workflows\": [\"{Default}\"]` declares the one `init` writes; an empty answer is never read as a pass.",
            architecture,
            StringComparison.Ordinal);
    }

    /// <summary>The workflow this tool ships, by the one name the code gives it.</summary>
    private static string Default => WorkflowTemplates.ShippedWorkflowName;

    /// <summary>A workflow's definition, as a line names it.</summary>
    private static string Shown(string name) => $".harness-config/workflows/{name}/{name}.yml";

    /// <summary>Why the JSON reader refuses <paramref name="text"/>, in its own words, as a problem line quotes it.</summary>
    private static string JsonReason(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
        }
        catch (JsonException ex)
        {
            return ex.Message;
        }

        throw new InvalidOperationException($"'{text}' read as JSON.");
    }

    /// <summary>
    /// The files a definition names are read for what the definition reads: a reason step's path is typed by the schema
    /// file the step names, and its prompt's placeholders are read as the step reads, each said at its line.
    /// </summary>
    [Fact]
    public async Task ASchemaAndAPrompt_AreReadForWhatTheDefinitionReads()
    {
        using var repository = await RepositoryAsync(new HarnessConfig { Workflows = ["small"] });
        var cancellationToken = TestContext.Current.CancellationToken;

        await WriteAsync(
            repository,
            "small",
            """
            name: small
            steps:
              - name: draft
                type: reason
                agent: worker
                prompt: prompts/work.md
                outputs: schemas/work.json
              - name: after
                type: orient
                when: 'draft.okk'
            """,
            cancellationToken,
            "agents/worker.md");

        var directory = Path.Combine(repository.Path, ".harness-config", "workflows", "small");

        Directory.CreateDirectory(Path.Combine(directory, "prompts"));
        Directory.CreateDirectory(Path.Combine(directory, "schemas"));
        await File.WriteAllTextAsync(Path.Combine(directory, "prompts", "work.md"), "Work on {ghost.title}.\n", cancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(directory, "schemas", "work.json"),
            """{"type":"object","additionalProperties":false,"properties":{"ok":{"type":"boolean"}}}""",
            cancellationToken);

        var report = await Service(repository).CheckAsync(repository.Path, "small", cancellationToken);

        Assert.Equal(
            [
                $"{Shown("small")}: step 'draft' prompt 'prompts/work.md' line 1 reads 'ghost.title', which no step before 'draft', no input and no param provides",
                $"{Shown("small")}: 'after.when' reads 'draft.okk', and the schema 'schemas/work.json' of step 'draft' has no property 'okk'",
            ],
            report.Lines);
    }

    /// <summary>Writes the definition of <paramref name="name"/> and each of <paramref name="files"/> beside it, as their own paths.</summary>
    private static async Task WriteAsync(Repository repository, string name, string definition, CancellationToken cancellationToken, params string[] files)
    {
        var directory = Path.Combine(repository.Path, ".harness-config", "workflows", name);

        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, $"{name}.yml"), definition, cancellationToken);

        foreach (var file in files)
        {
            var path = Path.Combine([directory, .. file.Split('/')]);

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, file.EndsWith(".json", StringComparison.Ordinal) ? "{}\n" : "text\n", cancellationToken);
        }
    }

    /// <summary>A workflow of one reasoning step, whose agent, prompt and schema are files of its own.</summary>
    private const string Work = """
        name: small
        steps:
          - name: draft
            type: reason
            agent: worker
            prompt: prompts/work.md
            outputs: schemas/work.json
        """;

    /// <summary>The real file system, but one file by its name is a file it would not look at, as a disk that refuses says.</summary>
    private sealed class Unlooked(IFileSystem inner, string name) : PassThroughFileSystem(inner)
    {
        public override bool FileExists(string path) => Path.GetFileName(path) != name && base.FileExists(path);

        public override PathKind KindOf(string path)
            => Path.GetFileName(path) == name ? throw new UnauthorizedAccessException("the disk would not say.") : base.KindOf(path);
    }

    private static WorkflowValidationService Service(Repository repository, IFileSystem? fileSystem = null)
        => new(
            repository.Harness.ContextLoader,
            fileSystem ?? repository.Harness.FileSystem,
            repository.Harness.Platform,
            new DssHarness.Core.Secrets.AgentKeyStore(fileSystem ?? repository.Harness.FileSystem, repository.Harness.FilePermissions, repository.Harness.Platform),
            DssHarness.Core.Codebase.CodeGraphExtractors.OfThisBuild);

    /// <summary>
    /// A repository of one commit whose config declares the workflows asked for, with the default's files written where
    /// it is declared.
    /// </summary>
    private static async Task<Repository> RepositoryAsync(HarnessConfig? config)
    {
        var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var cancellationToken = TestContext.Current.CancellationToken;

        temp.WriteFile("file.txt", "content\n");

        // The agent sections the workflows resolve their tiers against: as init writes them, so every check runs
        // under a configuration the caller chose. A test that names its own providers keeps them.
        var agents = DssHarness.Core.Agents.AgentProviderTemplates.Read();
        var given = config ?? new HarnessConfig { Workflows = [Default] };
        var withAgents = new HarnessConfig
        {
            Version = given.Version,
            Defaults = new HarnessDefaults
            {
                BuildCores = given.Defaults.BuildCores,
                TestCores = given.Defaults.TestCores,
                MaxParallelLegs = given.Defaults.MaxParallelLegs,
                MaxParallelLegsTotal = given.Defaults.MaxParallelLegsTotal,
                Project = given.Defaults.Project,
                Admission = given.Defaults.Admission,
                StallSeconds = given.Defaults.StallSeconds,
                DurationWarningFactor = given.Defaults.DurationWarningFactor,
                ClockStepToleranceMilliseconds = given.Defaults.ClockStepToleranceMilliseconds,
                ProcessSampleSeconds = given.Defaults.ProcessSampleSeconds,
                LocalCopy = given.Defaults.LocalCopy,
                AgentTier = given.Defaults.AgentTier ?? agents.AgentTier,
            },
            Toolchains = given.Toolchains,
            Sanitizers = given.Sanitizers,
            BuildConfigs = given.BuildConfigs,
            Projects = given.Projects,
            Hosts = given.Hosts,
            Emulators = given.Emulators,
            DeveloperEnvironments = given.DeveloperEnvironments,
            Legs = given.Legs,
            LegSets = given.LegSets,
            Tools = given.Tools,
            ToolSearchDirectories = given.ToolSearchDirectories,
            PredefinedRunners = given.PredefinedRunners,
            Workflows = given.Workflows,
            SetUp = given.SetUp,
            Commit = given.Commit,
            Sync = given.Sync,
            Contention = given.Contention,
            Worktrees = given.Worktrees,
            Anchors = given.Anchors,
            Mutations = given.Mutations,
            SshItems = given.SshItems,
            WslDistros = given.WslDistros,
            BuildTimingRegex = given.BuildTimingRegex,
            RunTimingRegex = given.RunTimingRegex,
            TestTimingRegex = given.TestTimingRegex,
            LineEndings = given.LineEndings,
            Ci = given.Ci,
            Providers = given.Providers.Count == 0 ? [.. agents.Providers] : given.Providers,
            TierConfig = given.TierConfig.Count == 0 ? new Dictionary<string, List<string>>(agents.TierConfig, StringComparer.OrdinalIgnoreCase) : given.TierConfig,
        };
        await harness.InitializeHarnessAsync(temp.Path, cancellationToken, withAgents);
        await harness.CommitAllAsync(temp.Path, "initial", cancellationToken);

        if ((withAgents.Workflows ?? []).Contains(Default))
        {
            WorkflowTemplates.EnsureShipped(Path.Combine(temp.Path, ".harness-config", "workflows"), harness.FileSystem);
        }

        return new Repository(harness, temp);
    }

    /// <summary>The repository a check ran in, disposable with its tree.</summary>
    private sealed record Repository(HarnessFactory Harness, TempDirectory Tree) : IDisposable
    {
        public string Path => Tree.Path;

        public void Dispose() => Tree.Dispose();
    }
}
