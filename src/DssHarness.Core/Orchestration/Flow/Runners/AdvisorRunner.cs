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
/// The engine's advisor, the model behind a question the engine raises: one reason step that only reads, independent of every other conversation, worked wherever the run works - its records and its own
/// conversations under <c>advice/&lt;n&gt;/</c> of the asking step's own directory, numbered after the advices already
/// there, whatever agent the asking step names or none - and held to the engine's own schema for an advice, in the
/// subset every answer is held to, whose <c>answer</c> the schema itself refuses unless it is one of the question's and
/// whose <c>text</c> it asks for exactly where the answer takes one.
/// </summary>
/// <param name="contextLoader">Finds the repository and its configuration.</param>
/// <param name="platform">This machine.</param>
/// <param name="store">Reads the asking agent's record, for the place the advice works in.</param>
/// <param name="runner">Works the advice, whatever the client is.</param>
/// <param name="fileSystem">The file system the workflow's files are read on.</param>
/// <param name="keys">The slot the entry's keys are read from.</param>
/// <param name="harnessOutput">Writes output the copies of a continuation record name.</param>
/// <param name="isolation">Decides the operating system's isolation the advice's agent runs under.</param>
/// <param name="updates">Finds the Claude Code the advice's order starts, where it holds the client that starts one; null where nothing does.</param>
/// <param name="cache">The hash cache of remote workflows, where the advisor the call names is a remote workflow's; null where none is read.</param>
public sealed class AdvisorRunner(
    IHarnessContextLoader contextLoader,
    IHostPlatform platform,
    OrchestrationStore store,
    IAgentRunner runner,
    IFileSystem fileSystem,
    AgentKeyStore keys,
    Output.IHarnessOutput harnessOutput,
    Agents.Isolation.IAgentIsolation isolation,
    Agents.ClaudeCode.ClaudeCodeUpdateService? updates = null,
    WorkflowCache? cache = null) : IFlowAdvisorRunner
{
    /// <summary>The directory under the asking step's own that holds its advices, one numbered directory for each.</summary>
    public const string AdviceDirectoryName = "advice";

    /// <summary>Held while an advice's number is chosen and its directory made, so two askings at once never take one number.</summary>
    private readonly Lock _numbering = new();

    /// <inheritdoc/>
    public async Task<FlowAdvice> AdviseAsync(FlowAdvisorCall call, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(call);

        // What the asking spends is counted from its turns as they come, so an asking a person's answer or a stop ends
        // part way still says what it had spent: that was spent, and the run's budget is charged with it.
        AgentRunSpend? spend = null;
        string? directory = null;

        try
        {
            return await AdviseAsync(call, made => spend = made, made => directory = made, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return SpentBy(
                new FlowAdvice(Answer: null, Why: null, Text: null, Directory: directory, NotObtained: "the asking was ended before the advisor answered"),
                spend);
        }
    }

    /// <summary>
    /// <paramref name="advice"/> with what the asking spent, as its count says it: the dollars counted, why the rest is not
    /// known where a turn's cost could not be counted, and the tokens - nothing where no turn was reported.
    /// </summary>
    /// <param name="advice">The advice.</param>
    /// <param name="spend">The asking's count; null where none was made.</param>
    private static FlowAdvice SpentBy(FlowAdvice advice, AgentRunSpend? spend)
        => spend?.Spent is { } spent
            ? advice with { Usd = spent.Dollars.Counted, Tokens = spent.Tokens, Uncounted = spent.Dollars.Uncounted }
            : advice;

    /// <summary>The asking itself, saying the count of its spend and its directory as each is made, for an ending part way to read them.</summary>
    /// <param name="call">The asking.</param>
    /// <param name="counting">Told the count of what the asking spends, once it is made.</param>
    /// <param name="made">Told the advice's own directory, once it is made.</param>
    /// <param name="cancellationToken">Ends the asking.</param>
    private async Task<FlowAdvice> AdviseAsync(FlowAdvisorCall call, Action<AgentRunSpend> counting, Action<string> made, CancellationToken cancellationToken)
    {
        var (place, context) = await FlowStepRunners.PlaceOf(
            new FlowStepRun(
                new WorkflowStep { Name = "advisor", Type = "reason" },
                call.Instance,
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
        var files = new WorkflowNamedFiles(fileSystem, platform.PathComparison, cache);

        // The advisor's own definition and prompt, read as the definition's check reads them - the file the
        // orchestrator names, which the check holds to be there.
        var (definition, problems) = AgentDefinitionParser.Parse(files.ReadText(context.Layout, call.Workflow, $"agents/{call.Advisor.Agent}.md", "an agent definition"), call.Advisor.Agent);

        if (problems.Count > 0 || definition is null)
        {
            throw new HarnessException(HarnessExit.ConfigInvalid, $"agents/{call.Advisor.Agent}.md does not read: {string.Join("; ", problems)}");
        }

        if (AgentProviders.Resolve(context.Config) is not { Providers: { } providers })
        {
            return Unasked("the configuration's agent providers do not resolve, so the advisor has no model to run on");
        }

        var resolution = AgentResolution.Of(providers, call.Advisor.Tier, call.Advisor.Model, call.Advisor.Effort, call.Advisor.EffortByModel, call.Advisor.Client, definition);

        if (resolution is null)
        {
            return Unasked("the advisor has no model to run on: name a tier or a model on it, or a tier under defaults.agentTier");
        }

        var launch = new ReasonLaunch(fileSystem, platform, keys, updates, isolation);
        var order = launch.Order(providers, resolution, context.Layout);

        // The advisor only reads, so a Claude Code its order starts is only found: one that is not there, or is too old,
        // is why no advice was obtained where the entry that would serve first needs it - and only passed over where a
        // later entry does - and installing or updating it is a run's step's or a side-task's to do.
        var program = await launch.ProgramAsync(context.Layout, providers, order, Agents.ClaudeCode.ClaudeCodeAsker.Run(" (the run's advisor)"), change: false, cancellationToken).ConfigureAwait(false);

        if (program.Refusal is { } notFound)
        {
            return Unasked(notFound.Message);
        }

        order = program.Order;

        // The asking step's agent decides where the advice works: its worktree where it has one - at the path its record
        // resolves, as every launch's is - else the main tree; the advice only reads either way, its own work directory
        // the one place it writes. Its records sit under the asking step's own directory, whichever agent it names, in
        // the next number after the advices that directory holds already: a session before this one may have left some.
        var working = call.Agent;
        var agentRecord = working is null ? null : store.ReadAgent(layout, working);
        var directory = NextAdviceDirectory(layout, call);
        made(directory);
        var workspace = launch.Workspace(agentRecord, context.Layout, Path.Combine(directory, "work"), layout.Directory);
        var ground = AgentStepGround.Of(
            context.Config,
            definition,
            new WorkflowStep { Name = "advisor", Type = "reason" },
            workspace,
            webSearchGiven: false,
            launch.ClaudeDirectories(context.Config, context.Layout),
            launch.CredentialPlaces(context.Config),
            readsOnly: true);

        // The advice's agent may run commands that only read: where agentPolicy.isolation is required and this machine has
        // no isolation of them, no advice is obtained, and why is said.
        using var isolated = await launch.IsolationAsync(context.Config, ground, context.Layout, directory, cancellationToken).ConfigureAwait(false);

        if (isolated.Refusal("the advisor") is { } unisolated)
        {
            return Unasked(unisolated);
        }

        // What the run stored of the asking step, rendered as a continuation record renders a step and within the
        // same bounds - the question's own record, read by {question.record}.
        call = call with { Record = call.Record ?? RecordOf(layout, context, call, directory) };

        var prompt = PromptOf(files, context.Layout, call);

        // The advice's records and its own conversations are its directory's, named on the request, so no path is
        // spelt of an agent the asking step does not name: the request's agent is the asking step's, or the advisor's
        // own definition where the step names none, and names no directory either way.
        var request = new AgentStepRequest(
            call.Run,
            working ?? call.Advisor.Agent,
            $"{call.Instance}/{AdviceDirectoryName}/{Path.GetFileName(directory)}",
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
            ResumeTemplate: null,
            workspace,
            OutputOf(call),
            Conversation: null,
            new AgentLimits(
                definition.MaxTurns,
                FlowRunBudget.Bound(definition.MaxBudgetUsd, call.BudgetLeftUsd),
                definition.MaxMinutes is { } minutes ? TimeSpan.FromMinutes(minutes) : null,
                definition.StallSeconds,
                place.Definition.HandoffSeconds),
            Mask: launch.Mask(context.Layout, providers))
        {
            Ground = ground,
            Isolation = isolated,
            Credential = order.FirstOrDefault(entry => entry.Usable)?.Credential,
            Withheld = [.. providers.Appointed().Select(appointed => appointed.Name)],
            Order = order,
            Retries = call.Advisor.Retries,
            BackoffSeconds = call.Advisor.BackoffSeconds,
            Program = program.Program,
            ProgramVersion = program.Version,
            RecordDirectory = directory,
            SessionsDirectory = Path.Combine(directory, OrchestratorLayout.SessionsDirectoryName),
        };

        var spend = new AgentRunSpend(providers, resolution.Serving.Model);
        counting(spend);

        try
        {
            var result = await runner.RunAsync(request, spend, cancellationToken).ConfigureAwait(false);

            if (result.Problem() is { } wrong)
            {
                return NotObtained($"the advisor answered a result that does not hold together: {wrong}", result);
            }

            var advice = AdviceOf(result, call);

            return advice is null
                ? NotObtained(result.Error ?? "the advisor's run ended without an answer", result)
                : SpentBy(advice with { Model = result.Model, Directory = directory }, spend);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HarnessException refused)
        {
            return NotObtained(refused.Message, result: null);
        }
        catch (Exception failed)
        {
            return NotObtained(failed.Message, result: null);
        }

        FlowAdvice NotObtained(string why, AgentRunResult? result)
            => SpentBy(new(Answer: null, Why: null, Text: null, Model: result?.Model, Directory: directory, NotObtained: why), spend);

        // Before the advice has a directory or a model, nothing was made and nothing spent.
        static FlowAdvice Unasked(string why)
            => new(Answer: null, Why: null, Text: null, NotObtained: why);
    }

    /// <summary>
    /// The directory of a new advice, made: <c>advice/&lt;n&gt;/</c> under the asking step's own directory - spelt by the
    /// one owner of where a step instance is kept, its item's brackets as the run's store writes them - numbered one
    /// past the highest number already there, so an advice a session before this one left, or another step's question
    /// of the same step, is never written over.
    /// </summary>
    /// <param name="layout">The run's own paths.</param>
    /// <param name="call">The asking.</param>
    private string NextAdviceDirectory(OrchestratorLayout layout, FlowAdvisorCall call)
    {
        var advices = Path.Combine(layout.InstanceDirectory(call.Instance, call.Agent), AdviceDirectoryName);

        lock (_numbering)
        {
            var highest = fileSystem.DirectoryExists(advices)
                ? fileSystem.EnumerateDirectories(advices)
                    .Select(taken => int.TryParse(Path.GetFileName(taken), NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : 0)
                    .DefaultIfEmpty(0)
                    .Max()
                : 0;
            var directory = Path.Combine(advices, (highest + 1).ToString(CultureInfo.InvariantCulture));

            fileSystem.CreateDirectory(directory);
            return directory;
        }
    }

    /// <summary>
    /// What the run stored of the asking step, rendered as a continuation record renders a step and within the same
    /// bounds - the question's own record, read by <c>{question.record}</c>.
    /// </summary>
    /// <param name="layout">The run's own paths.</param>
    /// <param name="context">The repository's context, whose configuration bounds the record.</param>
    /// <param name="call">The asking.</param>
    /// <param name="directory">The advice's own directory, whose work directory holds the record's copies.</param>
    private string? RecordOf(OrchestratorLayout layout, HarnessContext context, FlowAdvisorCall call, string directory)
    {
        var work = new AgentContinuationWork(
            call.Instance,
            [new AgentContinuedStep(call.Instance, "done", layout.InstanceDirectory(call.Instance, call.Agent))],
            ChangedPaths: [],
            RedLegs: [],
            CutOff: false);

        return new AgentContinuation(fileSystem, harnessOutput).Render(
            work,
            Path.Combine(directory, "work"),
            AgentContinuationBounds.Of(context.Config.AgentContext?.Continuation));
    }

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
    /// The output the advice is held to: the engine's own schema, in the subset every answer is held to - its
    /// <c>answer</c> one of the question's own, the schema itself refusing any other, with <c>why</c> - one closed
    /// shape for the answers that take a text, which it requires, and one for those that take none, which has no
    /// <c>text</c> at all; <c>anyOf</c> the two where the question has both kinds, the one shape where it has one.
    /// </summary>
    /// <param name="call">The asking, whose answers name the enum and whose options say which takes a text.</param>
    private static AgentOutput OutputOf(FlowAdvisorCall call)
    {
        var shapes = call.Answers
            .GroupBy(option => option.Text)
            .OrderByDescending(group => group.Key)
            .Select(group => ShapeOf([.. group.Select(option => option.Answer)], takesText: group.Key))
            .ToList();
        var schema = shapes.Count == 1 ? shapes[0] : new JsonObject { ["anyOf"] = new JsonArray([.. shapes]) };

        return new AgentOutput(call.QuestionId, schema.ToJsonString(JsonStateFile.LineOptions));

        static JsonObject ShapeOf(IReadOnlyList<string> answers, bool takesText)
        {
            var properties = new JsonObject
            {
                ["answer"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray([.. answers.Select(answer => (JsonNode?)answer)]) },
                ["why"] = new JsonObject { ["type"] = "string" },
            };
            var required = new JsonArray("answer", "why");

            if (takesText)
            {
                properties["text"] = new JsonObject { ["type"] = "string" };
                required.Add("text");
            }

            return new JsonObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["required"] = required,
                ["additionalProperties"] = false,
            };
        }
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
                "text" => path.Steps is [{ Key: { } named }] && call.Texts?.TryGetValue(named, out var file) is true
                    ? new(FlowValue.Of(files.ReadText(layout, call.Workflow, file, $"the text '{named}'")), 1)
                    : new(FlowValue.Missing, 1),
                "run" => new(call.RunValues is { } run ? FlowValue.Of(run) : FlowValue.Missing, 0),
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
            ["step"] = call.Instance,
            ["kind"] = call.Kind,
            ["problem"] = call.Problem + (call.Tried is { Length: > 0 } tried ? $"\nTried: {tried}" : string.Empty) + (call.Means is { Length: > 0 } means ? $"\nMeans: {means}" : string.Empty),
            ["answers"] = string.Join("\n", call.Answers.Select(option => $"- {option.Answer}: {option.Does}{(option.Text ? " (takes a text)" : string.Empty)}")),
            ["record"] = call.Record ?? string.Empty,
        });

    /// <summary>The events of an advice, which tells the coordinator nothing while it runs - its answer is all it says.</summary>
    private sealed class NoEvents : IFlowRunEvents
    {
        public void LegPlaced(string leg, bool inACopy) { }

        public void EveryLegPlaced() { }

        public void LegCopyMade(string leg) { }

        public void LegEnded(string leg) { }

        public void Used(long tokens, double? usd, string? uncounted = null) { }

        public void HandoffTaken() { }
    }
}
