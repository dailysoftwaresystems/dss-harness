using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using DssHarness.Core.Agents;
using DssHarness.Core.Agents.Policy;
using DssHarness.Core.Configuration;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Platform;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;
using DssHarness.Core.Secrets;

namespace DssHarness.Core.Orchestration.Flow;

/// <summary>
/// The engine's advisor (part 04, "A question the engine raises has no model behind it"): one reason step that only
/// reads, independent of every other conversation, worked wherever the run works - the asking step's records under
/// <c>advice/&lt;n&gt;/</c> of its own directory - and held to the engine's own schema for an advice, whose
/// <c>answer</c> the schema itself refuses unless it is one of the question's.
/// </summary>
/// <param name="contextLoader">Finds the repository and its configuration.</param>
/// <param name="platform">This machine.</param>
/// <param name="store">Reads the asking agent's record, for the place the advice works in.</param>
/// <param name="runner">Works the advice, whatever the client is.</param>
/// <param name="fileSystem">The file system the workflow's files are read on.</param>
/// <param name="keys">The slot the entry's keys are read from.</param>
/// <param name="harnessOutput">Writes output the copies of a continuation record name.</param>
public sealed class AdvisorRunner(
    IHarnessContextLoader contextLoader,
    IHostPlatform platform,
    OrchestrationStore store,
    IAgentRunner runner,
    IFileSystem fileSystem,
    AgentKeyStore keys,
    Output.IHarnessOutput harnessOutput) : IFlowAdvisorRunner
{
    /// <inheritdoc/>
    public async Task<FlowAdvice> AdviseAsync(FlowAdvisorCall call, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(call);

        var (place, context) = await FlowStepRunners.PlaceOf(
            new FlowStepRun(
                new WorkflowStep { Name = "advisor", Type = "reason" },
                call.Step,
                Attempt: 1,
                FlowValue.Missing,
                null,
                null,
                [],
                new NoEvents(),
                Handoff: null,
                Issue: null,
                Visit: 1,
                call.Place),
            contextLoader,
            platform,
            cancellationToken).ConfigureAwait(false);
        var layout = OrchestratorLayout.Of(context.Layout, place.Run);
        var files = new WorkflowNamedFiles(fileSystem, platform.PathComparison);

        // The advisor's own definition and prompt, read as the definition's check reads them - the file the
        // orchestrator names, which the check holds to be there.
        var (definition, problems) = AgentDefinitionParser.Parse(files.ReadText(context.Layout, call.Workflow, $"agents/{call.Advisor.Agent}.md", "an agent definition"), call.Advisor.Agent);

        if (problems.Count > 0 || definition is null)
        {
            throw new HarnessException(HarnessExit.ConfigInvalid, $"agents/{call.Advisor.Agent}.md does not read: {string.Join("; ", problems)}");
        }

        if (AgentProviders.Resolve(context.Config) is not { Providers: { } providers })
        {
            return NotObtained("the configuration's agent providers do not resolve, so the advisor has no model to run on");
        }

        var resolution = AgentResolution.Of(providers, call.Advisor.Tier, call.Advisor.Model, call.Advisor.Effort, call.Advisor.EffortByModel, call.Advisor.Client, definition);

        if (resolution is null)
        {
            return NotObtained($"the advisor has no model to run on: name a tier or a model on it, or a tier under defaults.agentTier");
        }

        // The asking step's agent decides where the advice works: its worktree where it has one, else the main tree -
        // and the advice's records sit under the asking step's own directory, whichever agent it names.
        var working = call.Agent;
        var agentRecord = working is null ? null : store.ReadAgent(layout, working);
        var workspace = Workspace(agentRecord, context.Layout.MainCheckoutRoot, layout, call);
        var order = AgentStepOrder.Of(providers, resolution, context.Layout, keys, new ClaudeTranscripts(fileSystem, platform).ConfigDirectory, spent: null);
        var ground = AgentStepGround.Of(
            context.Config,
            definition,
            new WorkflowStep { Name = "advisor", Type = "reason" },
            workspace,
            webSearchGiven: false,
            [.. order.Select(entry => entry.Credential?.ConfigDirectory).OfType<string>()],
            context.Layout.WorkflowsEnvSlot);

        // What the run stored of the asking step, rendered as a continuation record renders a step and within the
        // same bounds - the question's own record, read by {question.record}.
        call = call with { Record = call.Record ?? RecordOf(layout, context, call) };

        var prompt = PromptOf(files, context.Layout, call);

        var request = new AgentStepRequest(
            call.Run,
            working ?? "advisor",
            AdviceInstance(call),
            Attempt: 1,
            call.Advisor.Agent,
            Provider: null,
            resolution.Serving.Client,
            call.Advisor.Tier,
            resolution.Serving.Model,
            call.Advisor.Effort,
            Fallback: null,
            definition.Prompt,
            prompt,
            new Dictionary<string, string>(StringComparer.Ordinal),
            ResumeTemplate: null,
            workspace,
            OutputOf(call),
            Conversation: null,
            new AgentLimits(definition.MaxTurns, definition.MaxBudgetUsd, definition.MaxMinutes is { } minutes ? TimeSpan.FromMinutes(minutes) : null, definition.StallSeconds, place.Definition.HandoffSeconds),
            Mask: null)
        {
            Ground = ground,
            Credential = order.FirstOrDefault(entry => entry.Usable)?.Credential,
            Withheld = [.. providers.Appointed().Select(appointed => appointed.Name)],
            Order = order,
            Retries = call.Advisor.Retries,
            BackoffSeconds = call.Advisor.BackoffSeconds,
        };

        try
        {
            var result = await runner.RunAsync(request, events: null, cancellationToken).ConfigureAwait(false);

            if (result.Problem() is { } wrong)
            {
                return NotObtained($"the advisor answered a result that does not hold together: {wrong}", request, result);
            }

            var advice = AdviceOf(result, call);

            return advice is null
                ? NotObtained(result.Error ?? "the advisor's run ended without an answer", request, result)
                : advice with { Model = result.Model, Directory = StepDirectoryOf(layout, call), Usd = (double?)result.Usage?.Cost?.Usd, Tokens = result.Usage?.Total.OutputTokens };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HarnessException refused)
        {
            return NotObtained(refused.Message, request, result: null);
        }
        catch (Exception failed)
        {
            return NotObtained(failed.Message, request, result: null);
        }

        FlowAdvice NotObtained(string why, AgentStepRequest? asked = null, AgentRunResult? result = null)
            => new(
                Answer: null,
                Why: null,
                Text: null,
                Model: result?.Model,
                Directory: asked is null ? null : StepDirectoryOf(layout, call),
                NotObtained: why,
                Usd: (double?)result?.Usage?.Cost?.Usd,
                Tokens: result?.Usage?.Total.OutputTokens);
    }

    /// <summary>Where the advice's records are: under the asking step's own directory, whichever agent it names.</summary>
    /// <param name="layout">The run's own paths.</param>
    /// <param name="call">The asking.</param>
    private static string StepDirectoryOf(OrchestratorLayout layout, FlowAdvisorCall call)
        => call.Agent is { } agent
            ? layout.AgentStepDirectory(agent, AdviceInstance(call))
            : Path.Combine(layout.StepDirectory(call.Instance), "advice", call.Advice.ToString(CultureInfo.InvariantCulture));

    /// <summary>
    /// What the run stored of the asking step, rendered as a continuation record renders a step and within the same
    /// bounds - the question's own record, read by <c>{question.record}</c>.
    /// </summary>
    /// <param name="layout">The run's own paths.</param>
    /// <param name="context">The repository's context, whose configuration bounds the record.</param>
    /// <param name="call">The asking.</param>
    private string? RecordOf(OrchestratorLayout layout, HarnessContext context, FlowAdvisorCall call)
    {
        var asking = call.Agent is { } agent ? layout.AgentStepDirectory(agent, call.Instance) : layout.StepDirectory(call.Instance);
        var work = new AgentContinuationWork(
            call.Instance,
            [new AgentContinuedStep(call.Instance, "done", asking)],
            ChangedPaths: [],
            RedLegs: [],
            CutOff: false);

        return new AgentContinuation(fileSystem, harnessOutput).Render(
            work,
            Path.Combine(StepDirectoryOf(layout, call), "work"),
            AgentContinuationBounds.Of(context.Config.AgentContext?.Continuation));
    }

    /// <summary>The advice's step instance: the asking step's own, with the advice's directory under it.</summary>
    /// <param name="call">The asking.</param>
    private static string AdviceInstance(FlowAdvisorCall call)
        => Path.Combine(call.Instance, "advice", call.Advice.ToString(CultureInfo.InvariantCulture));

    /// <summary>
    /// The advice the run's answer holds: its <c>answer</c> one of the question's - the schema refuses any other - with
    /// why and the text where the answer takes one.
    /// </summary>
    /// <param name="result">How the advisor's run ended.</param>
    /// <param name="call">The asking, whose answers the schema named.</param>
    private static FlowAdvice? AdviceOf(AgentRunResult result, FlowAdvisorCall call)
    {
        if (result.Status != AgentRunStatus.Completed || result.StructuredOutput is not { Length: > 0 } json || JsonNode.Parse(json) is not JsonObject read)
        {
            return null;
        }

        string? Member(string name)
            => read[name] is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 0 ? text : null;

        return new FlowAdvice(Member("answer"), Member("why"), Member("text"));
    }

    /// <summary>
    /// The output the advice is held to: the engine's own schema, its <c>answer</c> the question's own answers - the
    /// schema itself refusing any other - and a <c>text</c> where the answer takes one.
    /// </summary>
    /// <param name="call">The asking, whose answers name the enum and whose options say which takes a text.</param>
    private static AgentOutput OutputOf(FlowAdvisorCall call)
    {
        var answers = string.Join(", ", call.Answers.Select(option => JsonSerializer.Serialize(option.Answer, JsonStateFile.LineOptions)));
        var takes = string.Join(", ", call.Answers.Where(option => option.Text).Select(option => JsonSerializer.Serialize(option.Answer, JsonStateFile.LineOptions)));
        var schema = "{\n"
            + "  \"type\": \"object\",\n"
            + "  \"properties\": {\n"
            + "    \"answer\": { \"type\": \"string\", \"enum\": [" + answers + "] },\n"
            + "    \"why\": { \"type\": \"string\" },\n"
            + "    \"text\": " + (call.Answers.Any(option => option.Text) ? "{ \"type\": \"string\" }" : "null") + "\n"
            + "  },\n"
            + "  \"required\": [\"answer\", \"why\"]"
            + (takes.Length > 0
                ? ",\n  \"allOf\": [{ \"if\": { \"properties\": { \"answer\": { \"enum\": [" + takes + "] } } }, \"then\": { \"required\": [\"text\"] } }]"
                : string.Empty)
            + "\n}";

        return new AgentOutput(call.QuestionId, schema);
    }

    /// <summary>
    /// The advisor's prompt, filled with what it reads: the question - the step that asks, its kind, the problem as the
    /// engine states it, each answer with what it does, and what the run stored of the asking step - the run's inputs,
    /// and the bar.
    /// </summary>
    /// <param name="files">Reads the files the workflow names.</param>
    /// <param name="layout">The repository's layout.</param>
    /// <param name="call">The asking.</param>
    private static string PromptOf(WorkflowNamedFiles files, HarnessLayout layout, FlowAdvisorCall call)
    {
        var template = files.ReadText(layout, call.Workflow, call.Advisor.Prompt, "an advisor prompt");

        return FlowTemplate.Fill(
            template,
            path => path.Root switch
            {
                "question" => new(QuestionOf(call), 0),
                "input" => new(FlowValue.Of(call.Inputs), 0),
                "recommendations" => new(FlowValue.Of(call.Bar), 0),
                _ => new(FlowValue.Missing, 0),
            },
            out var problem)
            ?? throw new HarnessException(HarnessExit.ConfigInvalid, problem!);
    }

    /// <summary>
    /// One of the question's texts, as the advisor's prompt reads it: the step that asks, its kind, the problem as the
    /// engine states it with what was tried and what the answer changes, each answer with what it does, and what the
    /// run stored of the asking step - the path's own walker reading each member.
    /// </summary>
    /// <param name="call">The asking.</param>
    private static FlowValue QuestionOf(FlowAdvisorCall call)
        => FlowValue.Of(new JsonObject
        {
            ["step"] = call.Step,
            ["kind"] = call.Kind,
            ["problem"] = call.Problem + (call.Tried is { Length: > 0 } tried ? $"\nTried: {tried}" : string.Empty) + (call.Means is { Length: > 0 } means ? $"\nMeans: {means}" : string.Empty),
            ["answers"] = string.Join("\n", call.Answers.Select(option => $"- {option.Answer}: {option.Does}{(option.Text ? " (takes a text)" : string.Empty)}")),
            ["record"] = call.Record ?? string.Empty,
        });

    /// <summary>
    /// Where the advice works: the asking agent's worktree where it has one - which it writes - or, for a step that
    /// names no agent or an agent that only reads, the main checkout; its own work directory under the asking step's
    /// advice directory either way.
    /// </summary>
    /// <param name="record">The asking agent's record; null where the step names no agent.</param>
    /// <param name="mainCheckout">The main checkout's root.</param>
    /// <param name="layout">The run's own paths.</param>
    /// <param name="call">The asking.</param>
    private static AgentWorkspace Workspace(AgentRecord? record, string mainCheckout, OrchestratorLayout layout, FlowAdvisorCall call)
    {
        var directory = StepDirectoryOf(layout, call);
        var work = Path.Combine(directory, "work");

        return new AgentWorkspace(
            record is { Reads: false } ? record.Worktree : null,
            work,
            mainCheckout,
            [],
            record is { Reads: false } && record.Worktree is { Length: > 0 } ? record.Worktree : work,
            layout.Directory);
    }

    /// <summary>The events of an advice, which tells the coordinator nothing while it runs - its answer is all it says.</summary>
    private sealed class NoEvents : IFlowRunEvents
    {
        public void LegPlaced(string leg, bool inACopy) { }

        public void EveryLegPlaced() { }

        public void LegCopyMade(string leg) { }

        public void LegEnded(string leg) { }

        public void Used(long tokens, double usd) { }

        public void HandoffTaken() { }
    }
}
