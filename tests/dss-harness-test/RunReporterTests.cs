using System.Text.Json.Nodes;
using DssHarness.Core.Agents;
using DssHarness.Core.Agents.Policy;
using DssHarness.Core.Configuration;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Orchestration.Flow;
using DssHarness.Core.Results;

namespace DssHarness.Tests;

/// <summary>
/// The run's reporter: one reason step that only reads, run by the reason step's own runner over the engine's facts,
/// its reads narrowed to the run's directory and nowhere else. Neither ask command writes anything under the run but
/// its own <c>asks/</c> record or takes a lock; its cost is the person's, never the run's; where no model words the
/// facts the status still answers with them, and the person's question is refused with the code of why.
/// </summary>
public sealed class RunReporterTests
{
    private const string Workflow = "flow";

    /// <summary>
    /// Every bound a test of these waits for: the same start path FlowServiceTests waits on (git spawns, the run's
    /// records) under the whole suite's load, and the same role as a deadlocked engine's end - 120 s, the suite's own
    /// "long running" threshold, for a spawn under the suite's storm has been measured taking over 30 s.
    /// </summary>
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(120);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>The agent runner as a fake, keeping every request it was asked and answering as a test says.</summary>
    private sealed class Agents : IAgentRunner
    {
        /// <summary>Every request the runner built, in order.</summary>
        public List<AgentStepRequest> Asked { get; } = [];

        /// <summary>How the run answers; a completed run with no answer unless a test changes it.</summary>
        public Func<AgentStepRequest, AgentRunResult> Answer { get; set; } = _ => new AgentRunResult(AgentRunStatus.Completed, FinalText: "All good.");

        /// <summary>The events the run says before it answers, as a client says them.</summary>
        public List<AgentEvent> Says { get; } = [];

        public async Task<AgentRunResult> RunAsync(AgentStepRequest step, IAgentEventSink? events, CancellationToken cancellationToken)
        {
            Asked.Add(step);

            foreach (var said in Says)
            {
                await events!.EmitAsync(said);
            }

            return Answer(step);
        }

        public async IAsyncEnumerable<AgentEvent> Stream(AgentStepRequest step, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var @event in One(RunAsync(step, null, cancellationToken)))
            {
                yield return @event;
            }

            static async IAsyncEnumerable<AgentEvent> One(Task<AgentRunResult> run)
            {
                _ = await run;
                yield break;
            }
        }
    }

    /// <summary>A runner whose pause step asks, and whose every other step passes.</summary>
    private sealed class AskingRunner : IFlowStepRunner
    {
        /// <inheritdoc/>
        public Task<FlowStepResult> RunAsync(FlowStepRun run, CancellationToken cancellationToken)
            => Task.FromResult(run.Step.Type == "pause"
                ? FlowStepResult.Asking(new FlowQuestion("asked", "pause", run.Instance, "what now?", ["stay", "go"]))
                : FlowStepResult.Ended(passed: true, 0));

        /// <inheritdoc/>
        public Task<string?> HandoffAsync(FlowStepRun run, CancellationToken cancellationToken)
            => Task.FromResult<string?>(null);
    }

    /// <summary>
    /// A repository with one workflow declared that names a reporter, and a run of it paused on its pause step - where
    /// <paramref name="claudeCode"/> is given, the slot naming it as the Claude Code to start and the service finding it.
    /// </summary>
    private static async Task<(HarnessFactory Harness, TempDirectory Tree, Agents Reporter, FlowService Service, string Run)> PausedAsync(bool reporter = true, string? claudeCode = null)
    {
        var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var agents = DssHarness.Core.Agents.AgentProviderTemplates.Read();

        temp.WriteFile("file.txt", "content\n");
        await harness.InitializeHarnessAsync(temp.Path, Token, new HarnessConfig
        {
            Workflows = [Workflow],
            Defaults = new() { AgentTier = agents.AgentTier },
            Providers = [.. agents.Providers],
            TierConfig = new Dictionary<string, List<string>>(agents.TierConfig, StringComparer.OrdinalIgnoreCase),
        });
        await harness.CommitAllAsync(temp.Path, "initial", Token);
        temp.WriteFile(
            $".harness-config/workflows/{Workflow}/{Workflow}.yml",
            "name: flow\n"
            + "steps:\n"
            + "  - name: gate\n"
            + "    type: pause\n"
            + "    question: what now?\n"
            + "    options: [stay, go]\n"
            + "  - name: build\n"
            + "    type: orient\n"
            + "    dependsOn: [gate]\n"
            + (reporter
                ? "reporter:\n  agent: reporter\n  statusPrompt: prompts/status.md\n  askPrompt: prompts/ask.md\n"
                : string.Empty));
        temp.WriteFile($".harness-config/workflows/{Workflow}/agents/reporter.md", "---\nname: reporter\ndescription: words the run's facts\nmaxTurns: 12\nmaxMinutes: 3\ncapabilities: [read]\n---\nYou word the run's facts.\n");
        temp.WriteFile($".harness-config/workflows/{Workflow}/prompts/status.md", "The facts:\n{status.facts}\nThe records:\n{status.records}\n");
        temp.WriteFile($".harness-config/workflows/{Workflow}/prompts/ask.md", "The question:\n{status.question}\nThe facts:\n{status.facts}\nThe records:\n{status.records}\n");
        OrchestrationKit.Write(temp.Path, Path.Combine(".harness-config", "workflows", ".env", "a.env"), "ZAI_API_KEY=sk-test-one\n" + (claudeCode is null ? string.Empty : $"CLAUDE_EXECUTABLE={claudeCode}\n"));
        harness.FilePermissions.ProtectSecret(Path.Combine(temp.Path, ".harness-config", "workflows", ".env", "a.env"));

        var reporterRunner = new Agents();
        var service = new FlowService(
            harness.ContextLoader,
            harness.Platform,
            harness.FileSystem,
            harness.OrchestrationStore,
            harness.Identity,
            new FlowSessions(harness.OrchestrationStore, harness.FileSystem, harness.Identity, harness.GitClient),
            new WorkflowValidationService(harness.ContextLoader, harness.FileSystem, harness.Platform, new DssHarness.Core.Secrets.AgentKeyStore(harness.FileSystem, harness.FilePermissions, harness.Platform), DssHarness.Core.Codebase.CodeGraphExtractors.OfThisBuild),
            new AskingRunner(),
            harness.OrchestrationLog,
            harness.ChildSessions,
            harness.PathBudget,
            harness.Output,
            harness.GitClient,
            harness.AgentService,
            reporterRunner,
            new DssHarness.Core.Secrets.AgentKeyStore(harness.FileSystem, harness.FilePermissions, harness.Platform),
            new TestIsolation(),
            updates: claudeCode is null
                ? null
                : new DssHarness.Core.Agents.ClaudeCode.ClaudeCodeUpdateService(
                    harness.ProcessTable,
                    harness.ProcessRunner,
                    harness.FileSystem,
                    harness.FilePermissions,
                    harness.Platform,
                    harness.Identity,
                    new DssHarness.Core.Secrets.AgentKeyStore(harness.FileSystem, harness.FilePermissions, harness.Platform)));

        var started = await service.RunAsync(temp.Path, Workflow, run: null, inputs: null, json: false, cancellationToken: Token).WaitAsync(Bound, Token);
        Assert.Equal(FlowExit.Paused, started.ExitCode);

        return (harness, temp, reporterRunner, service, "r1");
    }

    /// <summary>Every file under the run's directory, by its path and bytes - the run's state as it stands.</summary>
    private static Dictionary<string, byte[]> Snapshot(string runDirectory)
        => Directory.EnumerateFiles(runDirectory, "*", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}asks{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToDictionary(file => file, File.ReadAllBytes, StringComparer.Ordinal);

    /// <summary>
    /// The reporter words the facts, reading the run alone: its reads narrowed to the run's directory, no command, no
    /// write and no web, its answer masked, its prompt the facts and where the records are - and neither command
    /// writes anything under the run but its own <c>asks/</c> record, its cost the person's and never the run's.
    /// </summary>
    [Fact]
    public async Task TheReporterWordsTheFacts_ReadingTheRunAlone_NothingOfTheRunWritten()
    {
        var (harness, temp, reporter, service, run) = await PausedAsync();
        using var disposed = temp;
        var runDirectory = Path.Combine(temp.Path, ".orchestrators", run);
        var before = Snapshot(runDirectory);
        var startsIn = (Directory: (string?)null, There: false);
        reporter.Answer = step =>
        {
            startsIn = (step.Workspace.WorkingDirectory, Directory.Exists(step.Workspace.WorkingDirectory));
            return new AgentRunResult(AgentRunStatus.Completed, FinalText: "All good.");
        };

        var outcome = await service.AskStatusAsync(temp.Path, run, json: false, Token).WaitAsync(Bound, Token);

        Assert.Equal(HarnessExit.Success, outcome.ExitCode);
        Assert.StartsWith($"run '{run}' (workflow '{Workflow}'), as of ", outcome.Message, StringComparison.Ordinal);
        Assert.Equal("All good.", outcome.Details!.Single(line => line == "All good."));

        // Nothing of the run was written or locked: the run's files stand as they stood, but for the call's own asks/.
        Assert.Equal(before.Keys.Order(StringComparer.Ordinal), Snapshot(runDirectory).Keys.Order(StringComparer.Ordinal));
        Assert.All(before, file => Assert.Equal(file.Value, File.ReadAllBytes(file.Key)));

        var asked = reporter.Asked.Single();
        var asks = asked.RecordDirectory!;

        Assert.StartsWith(runDirectory, asks, StringComparison.Ordinal);
        Assert.Contains($"{Path.DirectorySeparatorChar}asks{Path.DirectorySeparatorChar}", asks, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(asks, "facts.md")), "the facts as given are kept in the call's own record");
        Assert.True(File.Exists(Path.Combine(asks, "ask.json")), "the call's own record holds ask.json");
        Assert.Contains($"Record: {asks}", outcome.Details!, StringComparer.Ordinal);

        // The reporter reads the run's directory and nowhere else: no command, no write and no web, whatever its file says.
        Assert.Equal(runDirectory, asked.Ground!.ReporterRoot);
        Assert.Equal(asks, asked.RecordDirectory);
        Assert.Equal(Path.Combine(asks, OrchestratorLayout.SessionsDirectoryName), asked.SessionsDirectory);
        Assert.Equal(runDirectory, asked.Workspace.RunDirectory);

        // The reporter starts in its own work directory under the call's asks/, which is there before its client is
        // handed it: a program is never started in a directory that is not there.
        Assert.Equal((Path.Combine(asks, "work"), true), startsIn);
        Assert.Null(asked.Output);
        Assert.Equal("reporter", asked.Agent);

        var policy = new AgentPolicy(harness.FileSystem, harness.Platform, asked.Ground!);

        Assert.Null(policy.Read(Path.Combine(runDirectory, "flow.json")).Refusal());
        Assert.Equal("the reporter's reads", Assert.IsType<AgentPolicyAnswer.Denied>(policy.Read(Path.Combine(temp.Path, "file.txt"))).Rule);
        Assert.Equal("the reporter's reads", Assert.IsType<AgentPolicyAnswer.Denied>(policy.Run(["git", "status"])).Rule);
        Assert.Equal("the reporter's reads", Assert.IsType<AgentPolicyAnswer.Denied>(policy.Write(Path.Combine(runDirectory, "x.md"))).Rule);
        Assert.Equal("the reporter's reads", Assert.IsType<AgentPolicyAnswer.Denied>(policy.Web("https://example.com/x")).Rule);
    }

    /// <summary>The reporter's prompt is the facts and where the records are - and, for the person's question, the question.</summary>
    [Fact]
    public async Task TheReportersPromptIsTheFactsTheQuestionAndTheRecords()
    {
        var (_, temp, reporter, service, run) = await PausedAsync();
        using var disposed = temp;

        _ = await service.AskStatusAsync(temp.Path, run, json: false, Token).WaitAsync(Bound, Token);
        _ = await service.AskAsync(temp.Path, run, "what did the gate ask?", json: false, Token).WaitAsync(Bound, Token);

        var status = reporter.Asked[0];
        var asked = reporter.Asked[1];

        Assert.StartsWith($"The facts:\nrun '{run}' (workflow '{Workflow}'), as of ", status.Prompt, StringComparison.Ordinal);
        Assert.Contains($"\nThe records:\n{Path.Combine(temp.Path, ".orchestrators", run)}\n", status.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("The question:", status.Prompt, StringComparison.Ordinal);
        Assert.StartsWith("The question:\nwhat did the gate ask?\nThe facts:\n", asked.Prompt, StringComparison.Ordinal);
        Assert.Contains($"\nThe records:\n{Path.Combine(temp.Path, ".orchestrators", run)}\n", asked.Prompt, StringComparison.Ordinal);
    }

    /// <summary>
    /// Where the workflow names no reporter the status still answers with the facts, says no model worded them and why,
    /// and exits 0 - and the person's question is refused with 12, both keeping the call's own record.
    /// </summary>
    [Fact]
    public async Task WhereNoModelIsGiven_TheStatusStillAnswersWithTheFacts_AndTheQuestionIsRefused()
    {
        var (_, temp, reporter, service, run) = await PausedAsync(reporter: false);
        using var disposed = temp;

        var status = await service.AskStatusAsync(temp.Path, run, json: false, Token).WaitAsync(Bound, Token);

        Assert.Equal(HarnessExit.Success, status.ExitCode);
        Assert.Equal("no model worded this: the workflow names no reporter, so no model words the run's facts", status.Details!.Single(line => line.StartsWith("no model worded this", StringComparison.Ordinal)));
        Assert.Contains("This answer cost nothing: no model worded it.", status.Details!, StringComparer.Ordinal);
        Assert.Empty(reporter.Asked);

        var asked = await service.AskAsync(temp.Path, run, "what is it doing?", json: false, Token).WaitAsync(Bound, Token);

        Assert.Equal(HarnessExit.ConfigInvalid, asked.ExitCode);
        Assert.StartsWith("ask-orchestrator was refused: the workflow names no reporter", asked.Message, StringComparison.Ordinal);
        Assert.True(Directory.EnumerateDirectories(Path.Combine(temp.Path, ".orchestrators", run, "asks")).Any(), "the refused call keeps its own record");
    }

    /// <summary>
    /// Where the model cannot answer - the key is not there, Claude Code is missing or too old, the model failed, the
    /// account is at its limit - the person's question is refused with the code of why, and the status still answers
    /// with the facts, saying why no model worded them, and exits 0.
    /// </summary>
    [Fact]
    public async Task WhereTheModelCannotAnswer_TheAskIsRefusedWithItsCode()
    {
        var (_, temp, reporter, service, run) = await PausedAsync();
        using var disposed = temp;

        foreach (var (result, code) in new (AgentRunResult, int)[]
        {
            (new AgentRunResult(AgentRunStatus.NotStarted, AgentNotStartedBecause.KeyMissing, Error: "no key"), HarnessExit.Refused),
            (new AgentRunResult(AgentRunStatus.NotStarted, AgentNotStartedBecause.ProgramMissing, Error: "no claude"), HarnessExit.ToolMissing),
            (new AgentRunResult(AgentRunStatus.Failed, Error: "it failed"), HarnessExit.CommandFailed),
            (new AgentRunResult(AgentRunStatus.RateLimited, Error: "the limit") { Limit = new AgentAccountLimit(false) }, HarnessExit.Incomplete),
        })
        {
            reporter.Answer = _ => result;

            var asked = await service.AskAsync(temp.Path, run, "what is it doing?", json: false, Token).WaitAsync(Bound, Token);

            Assert.Equal(code, asked.ExitCode);
            Assert.StartsWith("ask-orchestrator was refused: no model worded an answer:", asked.Message, StringComparison.Ordinal);

            var status = await service.AskStatusAsync(temp.Path, run, json: false, Token).WaitAsync(Bound, Token);

            Assert.Equal(HarnessExit.Success, status.ExitCode);
            Assert.Contains(status.Details!, line => line.StartsWith("no model worded this:", StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// The reporter's answer is masked as the run's records are: a secret the slot holds that its words repeat reaches
    /// neither the person's terminal nor the call's own record - and its ground keeps the tool's own Claude Code
    /// directory and the places the user keeps credentials in from it, whatever client it is on.
    /// </summary>
    [Fact]
    public async Task TheReportersAnswer_IsMaskedAsTheRunsRecordsAre()
    {
        var (harness, temp, reporter, service, run) = await PausedAsync();
        using var disposed = temp;
        reporter.Answer = _ => new AgentRunResult(AgentRunStatus.Completed, FinalText: "The key is sk-test-one.");

        var status = await service.AskStatusAsync(temp.Path, run, json: false, Token).WaitAsync(Bound, Token);

        Assert.Equal(HarnessExit.Success, status.ExitCode);
        Assert.Equal($"The key is {DssHarness.Core.Runners.ActionValues.Mask}.", status.Details!.Single(line => line.StartsWith("The key is", StringComparison.Ordinal)));
        Assert.All(
            Directory.EnumerateFiles(Path.Combine(temp.Path, ".orchestrators", run, "asks"), "*", SearchOption.AllDirectories),
            file => Assert.DoesNotContain("sk-test-one", File.ReadAllText(file), StringComparison.Ordinal));
        Assert.Equal([new ClaudeTranscripts(harness.FileSystem, harness.Platform).ConfigDirectory], reporter.Asked.Single().Ground!.ClaudeCodeDirectories);
        Assert.Contains(Path.Combine(harness.Platform.HomeDirectory, ".ssh"), reporter.Asked.Single().Ground!.CredentialPlaces.Select(place => place.Path));
    }

    /// <summary>
    /// Where the reporter's order holds the client that starts a program, the program is found as a run's step finds it
    /// - one the slot names read for what it is, nothing installed or brought up to date - and the request carries it and
    /// the version it answered, for the client to start what was found.
    /// </summary>
    [Fact]
    public async Task TheReportersClaudeCode_IsFoundAndCarriedOnItsRequest()
    {
        var named = await DotnetAsync(new HarnessFactory());
        var (_, temp, reporter, service, run) = await PausedAsync(claudeCode: named);
        using var disposed = temp;

        var status = await service.AskStatusAsync(temp.Path, run, json: false, Token).WaitAsync(Bound, Token);

        Assert.Equal(HarnessExit.Success, status.ExitCode);
        var asked = reporter.Asked.Single();
        Assert.Equal(named, asked.Program);
        Assert.NotNull(asked.ProgramVersion);
    }

    /// <summary>Where this machine's dotnet is, as the system finds it: a program that answers <c>--version</c>, standing in for Claude Code.</summary>
    /// <param name="harness">The harness whose process runner asks.</param>
    private static async Task<string> DotnetAsync(HarnessFactory harness)
    {
        var where = harness.Platform.Current == DssHarness.Core.Platform.PlatformId.Windows ? "where.exe" : "which";
        var found = await harness.ProcessRunner.RunAsync(new DssHarness.Core.Processes.ProcessRequest { FileName = where, Arguments = ["dotnet"] }, Token);

        return found.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)[0].Trim();
    }

    /// <summary>
    /// Whatever keeps the reporter from wording the facts - its own definition not reading among them - is why no model
    /// worded them, with the code it means: the status still answers with the facts and exits 0, keeping its record,
    /// and the person's question is refused with that code.
    /// </summary>
    [Fact]
    public async Task AReporterWhoseDefinitionDoesNotRead_LeavesTheStatusAnsweringWithTheFacts()
    {
        var (_, temp, reporter, service, run) = await PausedAsync();
        using var disposed = temp;
        temp.WriteFile($".harness-config/workflows/{Workflow}/agents/reporter.md", string.Empty);
        var why = $"agents/reporter.md does not read: {string.Join("; ", AgentDefinitionParser.Parse(string.Empty, "reporter").Problems)}";

        var status = await service.AskStatusAsync(temp.Path, run, json: false, Token).WaitAsync(Bound, Token);

        Assert.Equal(HarnessExit.Success, status.ExitCode);
        Assert.StartsWith($"run '{run}' (workflow '{Workflow}'), as of ", status.Message, StringComparison.Ordinal);
        Assert.Equal($"no model worded this: {why}", status.Details!.Single(line => line.StartsWith("no model worded this", StringComparison.Ordinal)));
        Assert.Empty(reporter.Asked);
        Assert.Single(Directory.EnumerateFiles(Path.Combine(temp.Path, ".orchestrators", run, "asks"), "ask.json", SearchOption.AllDirectories));

        var asked = await service.AskAsync(temp.Path, run, "what is it doing?", json: false, Token).WaitAsync(Bound, Token);

        Assert.Equal((HarnessExit.ConfigInvalid, $"ask-orchestrator was refused: no model worded an answer: {why}"), (asked.ExitCode, asked.Message));
    }

    /// <summary>
    /// The <c>--json</c> answer holds the facts and the answer apart, with the cost and the record - the cost counted as
    /// every agent run of the engine's is (<see cref="AgentRunSpend"/>): every token of every kind its turns used, at the
    /// configuration's prices.
    /// </summary>
    [Fact]
    public async Task TheJsonAnswerHoldsTheFactsAndTheAnswerApart()
    {
        var (harness, temp, reporter, service, run) = await PausedAsync();
        using var disposed = temp;
        var turn = new AgentUsage(1, 2, 3, 4, 5, 6);
        reporter.Says.Add(new UsageReported(turn, turn));
        reporter.Answer = step => new AgentRunResult(AgentRunStatus.Completed, FinalText: "All good.") { Model = step.Model, Usage = new AgentUsageReport(turn, []), };

        var outcome = await service.AskStatusAsync(temp.Path, run, json: true, Token).WaitAsync(Bound, Token);
        var document = JsonNode.Parse(Assert.Single(outcome.Data))!.AsObject();
        var served = reporter.Asked.Single().Model!;
        var priced = AgentProviders.Resolve(harness.ConfigStore.Load(HarnessFactory.ConfigPath(temp.Path)))!.Providers!.Of(served)
            ?? throw new InvalidOperationException($"'{served}' is no model the configuration lists");

        Assert.Equal(HarnessExit.Success, (int)document["exitCode"]!);
        Assert.True((bool)document["worded"]!);
        Assert.Equal("All good.", (string)document["answer"]!);
        Assert.StartsWith($"run '{run}' (workflow '{Workflow}'), as of ", (string)document["facts"]!, StringComparison.Ordinal);
        Assert.Equal(
            (served, AgentPrices.CostOf(priced.Provider, priced.Model, turn), turn.AllTokens),
            ((string)document["cost"]!["model"]!, (double?)document["cost"]!["usd"], (long)document["cost"]!["tokens"]!));
        Assert.Equal(15, turn.AllTokens);
        Assert.NotNull((string)document["record"]!);
    }

    /// <summary>
    /// A reporter's turn whose cost cannot be counted - its model has no price of a kind it used - is said in the answer,
    /// its <c>--json</c> and its record as what was counted and why the rest is not known, never a cost of nothing: the
    /// same fold the run's own spend goes through.
    /// </summary>
    [Fact]
    public async Task AReportersTurnWithoutAPrice_IsSaidAsAtLeastWhatWasCounted_AndWhy()
    {
        var (harness, temp, reporter, service, run) = await PausedAsync();
        using var disposed = temp;
        var config = HarnessFactory.ConfigPath(temp.Path);
        var written = JsonNode.Parse(File.ReadAllText(config))!;

        foreach (var model in written["providers"]!.AsArray().SelectMany(provider => provider!["models"]!.AsArray()))
        {
            _ = model!.AsObject().Remove("priceCacheWrite");
        }

        File.WriteAllText(config, written.ToJsonString());

        var turn = new AgentUsage(1, 2, 3, 4, 5, 6);
        reporter.Says.Add(new UsageReported(turn, turn));
        reporter.Answer = step => new AgentRunResult(AgentRunStatus.Completed, FinalText: "All good.") { Model = step.Model, Usage = new AgentUsageReport(turn, []), };

        var outcome = await service.AskStatusAsync(temp.Path, run, json: true, Token).WaitAsync(Bound, Token);
        var said = await service.AskStatusAsync(temp.Path, run, json: false, Token).WaitAsync(Bound, Token);
        var document = JsonNode.Parse(Assert.Single(outcome.Data))!.AsObject();
        var served = reporter.Asked[0].Model!;
        var priced = AgentProviders.Resolve(harness.ConfigStore.Load(config))!.Providers!.Of(served)
            ?? throw new InvalidOperationException($"'{served}' is no model the configuration lists");
        var why = AgentPrices.UnknownOf(priced.Provider, priced.Model, turn);
        var record = JsonNode.Parse(File.ReadAllText(Path.Combine((string)document["record"]!, "ask.json")))!;

        Assert.StartsWith("no price for ", why, StringComparison.Ordinal);
        Assert.Equal(
            (served, 0.0, 15L, why),
            ((string)document["cost"]!["model"]!, (double)document["cost"]!["usd"]!, (long)document["cost"]!["tokens"]!, (string)document["cost"]!["uncounted"]!));
        Assert.Equal((0.0, 15L, why), ((double)record["usd"]!, (long)record["tokens"]!, (string)record["uncounted"]!));
        Assert.Equal(
            $"This answer cost at least 0 USD (the rest is not known: {why}) ({served}, 15 tokens). It is not counted in the run's budget.",
            said.Details!.Single(line => line.StartsWith("This answer cost", StringComparison.Ordinal)));
    }

    /// <summary>
    /// A reporter's run that completed without a word worded nothing: the person's question is refused as a failed
    /// command - never with a code that says it passed - and the status still answers with the facts, saying why.
    /// </summary>
    [Fact]
    public async Task AReporterThatCompletedWithoutAWord_RefusesTheAskAsAFailedCommand()
    {
        var (_, temp, reporter, service, run) = await PausedAsync();
        using var disposed = temp;
        reporter.Answer = _ => new AgentRunResult(AgentRunStatus.Completed);

        var asked = await service.AskAsync(temp.Path, run, "what is it doing?", json: false, Token).WaitAsync(Bound, Token);

        Assert.Equal(
            (HarnessExit.CommandFailed, "ask-orchestrator was refused: no model worded an answer: the reporter's run completed without a word"),
            (asked.ExitCode, asked.Message));

        var status = await service.AskStatusAsync(temp.Path, run, json: false, Token).WaitAsync(Bound, Token);

        Assert.Equal(HarnessExit.Success, status.ExitCode);
        Assert.Equal(
            "no model worded this: the reporter's run completed without a word",
            status.Details!.Single(line => line.StartsWith("no model worded this", StringComparison.Ordinal)));
    }

    /// <summary>
    /// An orchestrator made by hand, which no flow runs, has no reporter and fewer facts: its status prints what
    /// list-orchestrator has of it and says so, and the person's question is refused (12).
    /// </summary>
    [Fact]
    public async Task AnOrchestratorMadeByHand_HasNoReporterAndFewerFacts()
    {
        var temp = new TempDirectory();
        var harness = new HarnessFactory();

        await harness.InitializeHarnessAsync(temp.Path, Token, new HarnessConfig { Workflows = [Workflow] });
        await harness.CommitAllAsync(temp.Path, "initial", Token);
        temp.WriteFile($".harness-config/workflows/{Workflow}/{Workflow}.yml", "name: flow\nsteps:\n  - name: one\n    type: orient\n");
        harness.OrchestrationStore.WriteOrchestrator(
            OrchestratorLayout.Of(new DssHarness.Core.Repository.HarnessLayout(temp.Path, temp.Path), "o1"),
            new OrchestratorRecord { Kind = OrchestratorRecord.KindName, Name = "o1", Model = "glm-5.3", Parallel = 4, CreatedAt = DateTimeOffset.UtcNow });
        using var disposed = temp;

        var service = new FlowService(
            harness.ContextLoader,
            harness.Platform,
            harness.FileSystem,
            harness.OrchestrationStore,
            harness.Identity,
            new FlowSessions(harness.OrchestrationStore, harness.FileSystem, harness.Identity, harness.GitClient),
            new WorkflowValidationService(harness.ContextLoader, harness.FileSystem, harness.Platform, new DssHarness.Core.Secrets.AgentKeyStore(harness.FileSystem, harness.FilePermissions, harness.Platform), DssHarness.Core.Codebase.CodeGraphExtractors.OfThisBuild),
            new AskingRunner(),
            harness.OrchestrationLog,
            harness.ChildSessions,
            harness.PathBudget,
            harness.Output,
            harness.GitClient,
            harness.AgentService,
            new Agents(),
            new DssHarness.Core.Secrets.AgentKeyStore(harness.FileSystem, harness.FilePermissions, harness.Platform),
            new TestIsolation());

        var status = await service.AskStatusAsync(temp.Path, "o1", json: false, Token).WaitAsync(Bound, Token);

        Assert.Equal(HarnessExit.Success, status.ExitCode);
        Assert.Equal("orchestrator 'o1' holds no run: it was made by hand, and these are list-orchestrator's facts of it", status.Message);
        Assert.Contains("model glm-5.3, at most 4 agents open", status.Details!, StringComparer.Ordinal);
        Assert.Equal("no agents", status.Details!.Single(line => line == "no agents"));

        var asked = await service.AskAsync(temp.Path, "o1", "what is it doing?", json: false, Token).WaitAsync(Bound, Token);

        Assert.Equal(HarnessExit.ConfigInvalid, asked.ExitCode);
    }
}
