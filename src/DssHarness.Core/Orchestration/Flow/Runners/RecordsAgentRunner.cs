using System.Text.Json;
using System.Text.Json.Nodes;
using DssHarness.Core.Agents;
using DssHarness.Core.Agents.ClaudeCode;
using DssHarness.Core.Agents.Policy;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Json;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Output;
using DssHarness.Core.Platform;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;
using DssHarness.Core.Schemas;

namespace DssHarness.Core.Orchestration.Flow;

/// <summary>
/// The one runner of the agents a person calls on a run's records - the reporter (<c>ask-orchestrator</c>,
/// <c>ask-orchestrator-status</c>) and the analyst (<c>analyze-orchestrator-report</c>): one reason step that only reads,
/// over the run's records alone - its reads narrowed to the run's directory and nowhere else, offered no command, no
/// write and no web, its answer masked as the run's records are - worked under a directory of its own inside the run's
/// that the engine never reads. Nothing of the run is written or locked, nothing of the machine is changed - a Claude
/// Code it needs is only found, never installed or brought up to date - and what it costs is the person's, never the
/// run's. Each is this runner given its agent, its prompt, its schema, its record directory and what its answer is held
/// to beyond the schema (<see cref="AgentOutput.Holds"/>, which the structured-output gate asks again for in the
/// agent's own conversation, under the schema's count of repairs); it is tried again where its agent failed as the
/// run's reasoning steps are (its own retries where it says them, the workflow's <c>maxRetries</c> and
/// <c>backoffSeconds</c> where it does not). Whatever keeps it from answering is why no model answered, with the code it
/// means.
/// </summary>
/// <param name="contextLoader">Finds the repository and its configuration.</param>
/// <param name="platform">This machine.</param>
/// <param name="runner">Works the agent, whatever the client is.</param>
/// <param name="fileSystem">The file system the workflow's files are read on.</param>
/// <param name="keys">The slot the entry's keys are read from.</param>
/// <param name="isolation">Decides the operating system's isolation the agent runs under: none, for it runs no command.</param>
/// <param name="updates">Finds the Claude Code the agent's order starts, where it holds the client that starts one; null where nothing does.</param>
public sealed class RecordsAgentRunner(
    IHarnessContextLoader contextLoader,
    IHostPlatform platform,
    IAgentRunner runner,
    IFileSystem fileSystem,
    Secrets.AgentKeyStore keys,
    Agents.Isolation.IAgentIsolation isolation,
    ClaudeCodeUpdateService? updates = null) : IFlowReporterRunner, IFlowAnalystRunner
{
    /// <summary>How often an answer that does not read against its schema, or the rules beyond it, is asked again for.</summary>
    private const int Repairs = 2;

    private readonly IHarnessContextLoader _contextLoader = contextLoader;
    private readonly IHostPlatform _platform = platform;
    private readonly IAgentRunner _runner = runner;
    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly ReasonLaunch _launch = new(fileSystem, platform, keys, updates, isolation);

    /// <inheritdoc/>
    public async Task<FlowReported> ReportAsync(FlowReporterCall call, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(call);

        var files = new WorkflowNamedFiles(_fileSystem, _platform.PathComparison);
        var ran = await RunAsync(
            new RecordsAgent(
                "reporter",
                "the reporter",
                call.Run,
                call.Stamp,
                call.Place,
                // The reporter's own definition and prompt, read as the definition's check reads them - the files the
                // orchestrator names, which the check holds to be there - for they are the repository's.
                layout => new RecordsAgentFiles(
                    call.Reporter.Agent,
                    files.ReadText(layout, call.Workflow, $"agents/{call.Reporter.Agent}.md", "an agent definition"),
                    ReporterPromptOf(files, layout, call),
                    Schema: null,
                    HarnessExit.ConfigInvalid),
                new RecordsAgentModel(
                    call.Reporter.Tier,
                    call.Reporter.Model,
                    call.Reporter.Effort,
                    call.Reporter.EffortByModel,
                    call.Reporter.Client,
                    _ => "the reporter has no model to run on: name a tier or a model on it, or a tier under defaults.agentTier"),
                layout => Path.Combine(layout.AsksDirectory, call.Stamp),
                Holds: null,
                call.Reporter.Retries,
                call.Reporter.BackoffSeconds),
            cancellationToken).ConfigureAwait(false);

        // A run that completed without a word worded nothing, and failed: the asking it was for is refused as a failed
        // command, never answered as one that passed.
        return ran.WhyNot is not null || ran.Text is not { Length: > 0 } worded
            ? Reported(answer: null, ran.WhyNot ?? "the reporter's run completed without a word", ran.ExitCode ?? HarnessExit.CommandFailed, ran)
            : Reported(worded, whyNot: null, exitCode: null, ran);
    }

    /// <inheritdoc/>
    public async Task<FlowAnalysed> AnalyzeAsync(FlowAnalystCall call, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(call);

        var ran = await RunAsync(
            new RecordsAgent(
                WorkflowTemplates.AnalystAgent,
                "the analyst",
                call.Run,
                call.Stamp,
                call.Place,
                // The analyst is the tool's own: its agent, its prompt and its schema are the ones this build ships, read
                // from the build itself - never a repository's, so an analysis of one repository's run is the same analyst
                // every repository's gets.
                _ => new RecordsAgentFiles(
                    WorkflowTemplates.AnalystAgent,
                    WorkflowTemplates.TextOf($"agents/{WorkflowTemplates.AnalystAgent}.md") ?? throw Unshipped(),
                    AnalystPromptOf(call),
                    WorkflowTemplates.TextOf($"schemas/{WorkflowTemplates.AnalystSchema}.schema.json") ?? throw Unshipped(),
                    HarnessExit.InternalError),
                // The analyst runs on the tier 'reporting' names in tierConfig, and on nothing else - init seeds it as
                // it seeds every tier, the person's to change: where the configuration defines no such tier, the verb is
                // refused naming the key to set, for an analysis is a paid run no other tier is taken for.
                new RecordsAgentModel(
                    "reporting",
                    Model: null,
                    Effort: null,
                    EffortByModel: null,
                    Client: null,
                    providers => $"tierConfig names no 'reporting' tier for the analyst to run on (defined: {(string.Join(", ", providers.Tiers) is { Length: > 0 } defined ? defined : "none")}); add one - \"reporting\": [a model to word the analyses] - under tierConfig"),
                layout => Path.Combine(layout.AnalysisDirectory, call.Stamp),
                // Its answer is held to the report it read beyond the schema: every citation the report's own, every step
                // it names a row of the report's, its impact a template of its citations, and no member of it but the
                // recommendation, which states the values it proposes and is not read, with a number of its own.
                Holds: answer => FlowAnalysisEvidence.Problems(answer, call.Report),
                Retries: null,
                BackoffSeconds: null),
            cancellationToken).ConfigureAwait(false);

        if (ran.WhyNot is { } whyNot)
        {
            return Analysed(analysis: null, whyNot, ran.ExitCode, ran);
        }

        if (ran.Output is not { } output)
        {
            return Analysed(analysis: null, "the analyst's run completed without an answer", HarnessExit.CommandFailed, ran);
        }

        // The impacts are written by the tool from the templates the gate held: each placeholder the cited value in the
        // report's unit. An answer the report does not hold is no analysis, and what its run spent is still said and kept.
        return FlowAnalysisEvidence.Rendered(output, call.Report, out var problems) is { } rendered && rendered.Deserialize<FlowAnalysis>(JsonStateFile.Options) is { } analysis
            ? Analysed(analysis, whyNot: null, exitCode: null, ran)
            : Analysed(analysis: null, problems.Count > 0 ? $"the analyst's answer is not held by the report it read: {string.Join("; ", problems)}" : "the analyst's run completed without an answer", HarnessExit.CommandFailed, ran);

        static HarnessException Unshipped()
            => new(HarnessExit.InternalError, "this build ships no report analyst: its agent, prompt and schema are incomplete in it");
    }

    /// <summary>
    /// One agent's run over a run's records: its files read, its model resolved, its launch built and its answer held to
    /// its schema and what it is held to beyond it - or why it was not, with the code it means and what a run that was
    /// made cost.
    /// </summary>
    /// <param name="agent">The agent and how it is run.</param>
    /// <param name="cancellationToken">Ends the run.</param>
    private async Task<Ran> RunAsync(RecordsAgent agent, CancellationToken cancellationToken)
    {
        // What a run that was begun spent, and the model it was resolved to, held where a throw cannot lose them: a
        // client that fails after paid turns, or an interrupt that stops it, is why no model answered, and what it spent
        // is still said and kept - never lost with the throw that kept its result from being read.
        var begun = new BegunRun();

        try
        {
            return await RanAsync(agent, begun, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception failed) when (failed is not OperationCanceledException || begun.Spend is not null)
        {
            // Whatever kept the agent from answering - its files not reading, the slot or the configuration refusing,
            // its client failing, an interrupt once its run was begun - is why no model answered, with the code it means
            // by the one mapping every command's failure is read by. An interrupt before the run was begun stopped nothing
            // paid, and ends the command as an interrupt ends every command.
            var meaning = KnownCauses.Meaning(failed);

            return new Ran(meaning.Message, meaning.ExitCode, begun.Served, begun.Spend);
        }
    }

    /// <summary>What a run of an agent on records was begun on, held where a throw cannot lose it.</summary>
    private sealed class BegunRun
    {
        /// <summary>The model the agent was resolved to run on; null until it was.</summary>
        public string? Model { get; set; }

        /// <summary>What the run spends, as its turns are reported; null until the run was made.</summary>
        public AgentRunSpend? Spend { get; set; }

        /// <summary>
        /// The model that served the run, as its spend's own account says - the one every turn it reported was priced at,
        /// a fallback's where one moved it - and the model it was resolved to where no run was begun.
        /// </summary>
        public string? Served => Spend?.Account().Model ?? Model;
    }

    /// <inheritdoc cref="RunAsync"/>
    private async Task<Ran> RanAsync(RecordsAgent agent, BegunRun begun, CancellationToken cancellationToken)
    {
        var (place, context) = await FlowStepRunners.PlaceOf(
            new FlowStepRun(
                new WorkflowStep { Name = agent.Name, Type = "reason" },
                agent.Stamp,
                Attempt: 1,
                FlowValue.Missing,
                null,
                null,
                [],
                FlowRunEvents.None,
                Handoff: null,
                Issue: null,
                Visit: 1,
                agent.Place),
            _contextLoader,
            _platform,
            cancellationToken).ConfigureAwait(false);
        var layout = OrchestratorLayout.Of(context.Layout, place.Run);
        var files = agent.Files(context.Layout);
        var (definition, problems) = AgentDefinitionParser.Parse(files.Definition, files.Agent);

        if (problems.Count > 0 || definition is null)
        {
            return new Ran($"agents/{files.Agent}.md does not read: {string.Join("; ", problems)}", files.Unreadable);
        }

        if (files.Schema is { } schemaText && JsonSchemaSubset.Whole(schemaText).Problems is [_, ..] unreadable)
        {
            return new Ran($"{agent.Label}'s schema does not read: {string.Join("; ", unreadable)}", files.Unreadable);
        }

        if (AgentProviders.Resolve(context.Config) is not { Providers: { } providers })
        {
            return new Ran($"the configuration's agent providers do not resolve, so {agent.Label} has no model to run on", HarnessExit.ConfigInvalid);
        }

        var model = agent.Model;
        var resolution = AgentResolution.Of(providers, model.Tier, model.Model, model.Effort, model.EffortByModel, model.Client, definition);

        if (resolution is null)
        {
            return new Ran(model.NoModel(providers), HarnessExit.ConfigInvalid);
        }

        begun.Model = resolution.Serving.Model;

        var order = _launch.Order(providers, resolution, context.Layout);

        // The agent only reads, so a Claude Code its order starts is only found: one that is not there, or is too old,
        // is why no model answered where the entry that would serve first needs it - and only passed over where a later
        // entry does - and installing or updating it is a run's step's or a side-task's to do.
        var program = await _launch.ProgramAsync(context.Layout, providers, order, ClaudeCodeAsker.Run($" (the run's {agent.Label["the ".Length..]})"), change: false, cancellationToken).ConfigureAwait(false);

        if (program.Refusal is { } notFound)
        {
            return new Ran(notFound.Message, notFound.ExitCode);
        }

        order = program.Order;

        // The agent works over the run's records alone: its work directory, its records and the conversations a client
        // stores under its own directory, which is inside the run's and which the engine never reads - never under an
        // agent's of the run - and its reads narrowed to the run's directory by the policy's one rule for it.
        var directory = agent.Directory(layout);
        var workspace = _launch.Workspace(record: null, context.Layout, Path.Combine(directory, OrchestratorLayout.WorkDirectoryName), layout.Directory);
        var ground = AgentStepGround.Of(
            context.Config,
            definition,
            new WorkflowStep { Name = agent.Name, Type = "reason" },
            workspace,
            webSearchGiven: false,
            _launch.ClaudeDirectories(context.Config, context.Layout),
            _launch.CredentialPlaces(context.Config),
            readsOnly: true) with
        {
            ReporterRoot = layout.Directory,
        };
        var mask = _launch.Mask(context.Layout, providers);

        // The agent runs no command, so it needs no isolation of the operating system's; its records say so.
        var isolated = await _launch.IsolationAsync(context.Config, ground, context.Layout, directory, cancellationToken).ConfigureAwait(false);

        if (isolated.Refusal(agent.Label) is { } unisolated)
        {
            return new Ran(unisolated, HarnessExit.ToolMissing);
        }

        var request = new AgentStepRequest(
            agent.Run,
            files.Agent,
            agent.Stamp,
            Attempt: 1,
            files.Agent,
            Provider: null,
            resolution.Serving.Client,
            model.Tier,
            resolution.Serving.Model,
            model.Effort,
            Fallback: null,
            definition.Prompt,
            files.Prompt,
            ResumeTemplate: null,
            workspace,
            files.Schema is { } schema ? new AgentOutput(files.Agent, schema, MaxRepairs: Repairs) { Holds = agent.Holds } : null,
            Conversation: null,
            new AgentLimits(definition.MaxTurns, definition.MaxBudgetUsd, definition.MaxMinutes is { } minutes ? TimeSpan.FromMinutes(minutes) : null, definition.StallSeconds, place.Definition.HandoffSeconds),
            mask)
        {
            Ground = ground,
            Isolation = isolated,
            Credential = order.FirstOrDefault(entry => entry.Usable)?.Credential,
            Withheld = [.. providers.Appointed().Select(appointed => appointed.Name)],
            Order = order,

            // Tried again where its agent failed as the run's reasoning steps are: its own retries where it says them,
            // the workflow's where it does not.
            Retries = place.Definition.RetriesOf(new WorkflowStep { Name = agent.Name, Type = "reason", Retries = agent.Retries }),
            BackoffSeconds = agent.BackoffSeconds ?? place.Definition.BackoffSeconds,
            RecordDirectory = directory,
            SessionsDirectory = Path.Combine(directory, OrchestratorLayout.SessionsDirectoryName),
            Program = program.Program,
            ProgramVersion = program.Version,
        };

        // What the agent spends is counted as every agent run of the engine's is, from its turns as they come: every
        // token of every kind, at the configuration's prices - over every repair of its answer, for it is one paid run.
        var spend = new AgentRunSpend(providers, resolution.Serving.Model);
        begun.Spend = spend;
        var result = await _runner.RunAsync(request, spend, cancellationToken).ConfigureAwait(false);

        // The answer is masked as the run's records are: whatever the agent read or was told, no secret the slot holds
        // reaches the person's terminal or the call's record in its words.
        string? Masked(string? text) => text is null ? null : mask?.Redact(text) ?? text;

        // A run that never started - its key not there, its program not found - asked no model, and is no run made.
        return result.Problem() is { } wrong
            ? new Ran($"{agent.Label} answered a result that does not hold together: {wrong}", HarnessExit.CommandFailed, result.Model, spend)
            : result.Status == AgentRunStatus.NotStarted
            ? new Ran(Masked(result.Error) ?? $"{agent.Label}'s run did not start", result.ToOutcome().ExitCode, result.Model)
            : result.Status != AgentRunStatus.Completed
            ? new Ran(Masked(result.Error) ?? $"{agent.Label}'s run ended {result.Status}", result.ToOutcome().ExitCode, result.Model, spend)
            : new Ran(WhyNot: null, ExitCode: null, result.Model, spend)
            {
                Text = Masked(result.FinalText),
                Output = result.StructuredOutput is { Length: > 0 } structured ? JsonDepth.ParseNode(structured) : null,
            };
    }

    /// <summary>The reporter's answer, or why no model worded it, with what its run cost where one was made.</summary>
    private static FlowReported Reported(string? answer, string? whyNot, int? exitCode, Ran ran)
        => new(answer, whyNot, exitCode, ran.Model, ran.Spend?.Spent?.Dollars.Counted, ran.Spend?.Spent?.Tokens, ran.Spend?.Spent?.Dollars.Uncounted);

    /// <summary>The analysis, or why none was given, with what the analyst's run cost and whether one was made.</summary>
    private static FlowAnalysed Analysed(FlowAnalysis? analysis, string? whyNot, int? exitCode, Ran ran)
        => new(analysis, whyNot, exitCode, ran.Model, ran.Spend?.Spent?.Dollars.Counted, ran.Spend?.Spent?.Tokens, ran.Spend?.Spent?.Dollars.Uncounted) { Ran = ran.Spend is not null };

    /// <summary>
    /// The reporter's prompt, filled with what it reads: the facts as text, the person's question where one was typed,
    /// and where the run's records are - the only three names its prompts may read.
    /// </summary>
    private static string ReporterPromptOf(WorkflowNamedFiles files, HarnessLayout layout, FlowReporterCall call)
        => FlowTemplate.Fill(
            files.ReadText(layout, call.Workflow, call.Question is null ? call.Reporter.StatusPrompt : call.Reporter.AskPrompt, call.Question is null ? "a reporter status prompt" : "a reporter ask prompt"),
            path => path.Root switch
            {
                "status" => new(FlowValue.Of(new JsonObject
                {
                    ["facts"] = call.Facts,
                    ["question"] = call.Question ?? string.Empty,
                    ["records"] = call.Records,
                }), 0),
                _ => new(FlowValue.Missing, 0),
            },
            out var problem)
            ?? throw new HarnessException(HarnessExit.ConfigInvalid, problem!);

    /// <summary>
    /// The analyst's prompt, filled with what it reads: the report whole, as JSON, and where the run's records are - the
    /// only two names its prompt reads.
    /// </summary>
    private static string AnalystPromptOf(FlowAnalystCall call)
        => FlowTemplate.Fill(
            WorkflowTemplates.TextOf($"prompts/{WorkflowTemplates.AnalystPrompt}.md")
                ?? throw new HarnessException(HarnessExit.InternalError, "this build ships no report analyst: its agent, prompt and schema are incomplete in it"),
            path => path.Root switch
            {
                "analysis" => new(FlowValue.Of(new JsonObject
                {
                    ["report"] = call.Report.ToJsonString(ReportJson.Options),
                    ["records"] = call.Records,
                }), 0),
                _ => new(FlowValue.Missing, 0),
            },
            out var problem)
            ?? throw new HarnessException(HarnessExit.InternalError, problem!);

    /// <summary>One agent a person calls on a run's records, and how it is run.</summary>
    /// <param name="Name">The step's name its run is recorded under.</param>
    /// <param name="Label">How the agent is said: <c>the reporter</c>, <c>the analyst</c>.</param>
    /// <param name="Run">The run's name.</param>
    /// <param name="Stamp">The call's own stamp, its directory's name.</param>
    /// <param name="Place">The run's place, for loading its repository's context.</param>
    /// <param name="Files">Its agent, its prompt and its schema, read where the repository's layout is known.</param>
    /// <param name="Model">The model it runs on, and what says it has none.</param>
    /// <param name="Directory">Where its records are kept, under the run's directory.</param>
    /// <param name="Holds">What its answer's output is held to beyond its schema; null where nothing is.</param>
    /// <param name="Retries">Its own retries, where it says them.</param>
    /// <param name="BackoffSeconds">Its own seconds before the first retry, where it says them.</param>
    private sealed record RecordsAgent(
        string Name,
        string Label,
        string Run,
        string Stamp,
        FlowRunPlace? Place,
        Func<HarnessLayout, RecordsAgentFiles> Files,
        RecordsAgentModel Model,
        Func<OrchestratorLayout, string> Directory,
        Func<JsonNode, IReadOnlyList<string>>? Holds,
        int? Retries,
        int? BackoffSeconds);

    /// <summary>An agent's files.</summary>
    /// <param name="Agent">The agent's name.</param>
    /// <param name="Definition">Its definition's text.</param>
    /// <param name="Prompt">Its prompt, filled.</param>
    /// <param name="Schema">The schema its answer is held to; null where it answers in words.</param>
    /// <param name="Unreadable">The code a file of it that does not read means: the repository's to fix, or this build's.</param>
    private sealed record RecordsAgentFiles(string Agent, string Definition, string Prompt, string? Schema, int Unreadable);

    /// <summary>The model an agent runs on, as its block names it, and why it has none where the configuration gives it none.</summary>
    private sealed record RecordsAgentModel(
        string? Tier,
        string? Model,
        string? Effort,
        IReadOnlyDictionary<string, string>? EffortByModel,
        string? Client,
        Func<AgentProviders, string> NoModel);

    /// <summary>How an agent's run went: why it gave no answer, or its words and its output; its model and its count where a run was made.</summary>
    private sealed record Ran(string? WhyNot, int? ExitCode, string? Model = null, AgentRunSpend? Spend = null)
    {
        /// <summary>Its final words, masked.</summary>
        public string? Text { get; init; }

        /// <summary>Its output, held to its schema and to what it is held to beyond it.</summary>
        public JsonNode? Output { get; init; }
    }
}
