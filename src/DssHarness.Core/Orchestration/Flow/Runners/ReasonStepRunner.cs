using System.Text;
using System.Text.Json.Nodes;
using DssHarness.Core.Agents;
using DssHarness.Core.Agents.Policy;
using DssHarness.Core.Ci;
using DssHarness.Core.Configuration;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Platform;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;
using DssHarness.Core.Secrets;

namespace DssHarness.Core.Orchestration.Flow;

/// <summary>
/// The <c>reason</c> step: the agent a reason step is given, made where the step names none (a read-only agent, with no
/// worktree - <see cref="CreateAgentRunner.MakeAsync"/> over the one verb that makes agents), and its step worked
/// through <see cref="IAgentRunner"/> - the step's model resolved with its agent definition below its own, its order
/// built with each entry's credential read from the slot, its prompt read and filled and its skills handed over, the
/// work it goes on with rendered as its continuation record, and its going on by the client's kind (the stored
/// conversation handed, or a rendered launch: <see cref="AgentRunner"/> owns which).
/// </summary>
/// <param name="contextLoader">Finds the repository and its configuration.</param>
/// <param name="platform">This machine.</param>
/// <param name="store">Reads the agent's record.</param>
/// <param name="create">Makes the read-only agent a step that names none is given.</param>
/// <param name="runner">Works the step, whatever the client is.</param>
/// <param name="fileSystem">The file system the step's files are read on.</param>
/// <param name="keys">The slot the entry's keys are read from.</param>
/// <param name="gh">Runs <c>gh</c>, for a web-search source the machine's login reads.</param>
/// <param name="harnessOutput">Writes output the copies of a continuation record name.</param>
public sealed class ReasonStepRunner(
    IHarnessContextLoader contextLoader,
    IHostPlatform platform,
    OrchestrationStore store,
    CreateAgentRunner create,
    IAgentRunner runner,
    IFileSystem fileSystem,
    AgentKeyStore keys,
    GhProcess gh,
    Output.IHarnessOutput harnessOutput) : IFlowStepTypeRunner, IFlowHandoffRunner
{
    /// <inheritdoc/>
    public string Type => "reason";

    /// <summary>
    /// Works the step: the agent made where the step names none, the request resolved - the definition read, the model
    /// resolved, the order built, the prompt filled and its skills handed over - and the run's answer mapped to the
    /// step's own ending.
    /// </summary>
    /// <param name="run">The step, the instance, the run it belongs to and what the engine read for it.</param>
    /// <param name="cancellationToken">Stops the work.</param>
    public async Task<FlowStepResult> RunAsync(FlowStepRun run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);

        var (place, context) = await FlowStepRunners.PlaceOf(run, contextLoader, platform, cancellationToken).ConfigureAwait(false);
        var layout = OrchestratorLayout.Of(context.Layout, place.Run);
        var files = new WorkflowNamedFiles(fileSystem, platform.PathComparison);

        var promptFile = run.Step.Prompt;

        if (promptFile is null || run.Read?.TryGetValue("prompt", out var promptValue) is not true
            || promptValue.AsScalar("prompt") is not string readPrompt)
        {
            return FlowStepResult.Ended(passed: false, HarnessExit.InternalError, $"'{run.Instance}' is a reason step whose prompt file '{promptFile ?? "none"}' the engine did not read for it");
        }

        // The agent the step works as: the one its `in` names or the work it goes on with - or the engine's own
        // read-only one, made here where the step names none.
        string? working = run.WorksIn;

        if (run.Made is { } own && working is null)
        {
            var making = await create.MakeAsync(run, noWorktree: true, cancellationToken).ConfigureAwait(false);

            if (!making.Passed)
            {
                return making;
            }

            working = own.Name;
        }

        working ??= run.Made?.Name;

        if (working is not { } agent)
        {
            return FlowStepResult.Ended(passed: false, HarnessExit.InternalError, $"'{run.Instance}' is a reason step that names no agent, and the engine named none for it");
        }

        if (store.ReadAgent(layout, agent) is not { State: AgentStates.Live } agentRecord)
        {
            return FlowStepResult.Ended(passed: false, HarnessExit.Refused, $"agent '{agent}' of '{place.Run}' is not live, and the step cannot work as it");
        }

        if (AgentProviders.Resolve(context.Config) is not { Providers: { } providers })
        {
            return FlowStepResult.Ended(passed: false, HarnessExit.ConfigInvalid, "the configuration's agent providers do not resolve, and the step has no model to run on");
        }

        // The agent definition, read as the definition's check reads it: the system prompt sent as written, and the
        // level below the step's own for the model, the effort and the client. A step that goes on with another's work
        // inherits what the chain it resumes names, where it names anything and the step names none of its own.
        var resumed = run.Step.Resume is { } goesOnWith ? Resumed(place.Definition, goesOnWith) : null;
        var definition = DefinitionOf(files, context.Layout, place.Definition.Name, run.Step.Agent);
        var inherited = definition ?? (run.Step.Agent is null ? DefinitionOf(files, context.Layout, place.Definition.Name, resumed?.Agent) : null);

        var resolution = AgentResolution.Of(
            providers,
            run.Step.Tier ?? (run.Step.Model is null ? resumed?.Tier : null),
            run.Step.Model ?? (run.Step.Tier is null ? resumed?.Model : null),
            run.Step.Effort,
            run.Step.EffortByModel,
            run.Step.Client,
            inherited);

        if (resolution is null)
        {
            return FlowStepResult.Ended(
                passed: false,
                HarnessExit.ConfigInvalid,
                $"'{run.Instance}' has no model to run on: name a tier or a model on the step, or a tier under defaults.agentTier");
        }

        var order = AgentStepOrder.Of(providers, resolution, context.Layout, keys, new ClaudeTranscripts(fileSystem, platform).ConfigDirectory);
        var workspace = Workspace(agentRecord, context.Layout.MainCheckoutRoot, layout, agent);
        var webSearch = await WebSearchOf(context, providers, run.Step, cancellationToken).ConfigureAwait(false);
        var claude = new ClaudeTranscripts(fileSystem, platform).ConfigDirectory;
        var ground = AgentStepGround.Of(
            context.Config,
            definition,
            run.Step,
            workspace,
            webSearch is not null,
            [.. order.Select(entry => entry.Credential?.ConfigDirectory).OfType<string>().Append(claude).Distinct(StringComparer.Ordinal)],
            context.Layout.WorkflowsEnvSlot);

        // The prompt with each skill it names handed over: the folder copied into the agent's own work directory, the
        // block put in the prompt's place, and what cannot be handed refused here - the step-start half of the skill's
        // failing loud twice, held to the capabilities the agent's own ground gives it. The resume prompt, where the
        // step goes on with earlier work and names one, in its place.
        string? filled;

        if (run.Step.ResumePrompt is not null && GoesOnWithEarlierWork(run)
            && run.Read.TryGetValue("resumePrompt", out var resumeValue) && resumeValue.AsScalar("resumePrompt") is string resumePrompt)
        {
            filled = SkillBlocks(resumePrompt, run, agent, ground.Capabilities, workspace, resolution, out var refusedResume);

            if (filled is null)
            {
                return FlowStepResult.Ended(passed: false, HarnessExit.Refused, refusedResume!);
            }
        }
        else
        {
            filled = SkillBlocks(readPrompt, run, agent, ground.Capabilities, workspace, resolution, out var refused);

            if (filled is null)
            {
                return FlowStepResult.Ended(passed: false, HarnessExit.Refused, refused!);
            }
        }

        var request = new AgentStepRequest(
            place.Run,
            agent,
            run.Instance,
            run.Attempt,
            run.Step.Agent,
            Provider: null,
            resolution.Serving.Client,
            run.Step.Tier,
            resolution.Serving.Model,
            run.Step.Effort,
            Fallback: null,
            definition?.Prompt ?? string.Empty,
            filled,
            new Dictionary<string, string>(StringComparer.Ordinal),
            ResumeTemplate: null,
            workspace,
            OutputOf(files, context.Layout, place.Definition.Name, run.Step),
            Conversation: null,
            new AgentLimits(
                definition?.MaxTurns,
                definition?.MaxBudgetUsd,
                definition?.MaxMinutes is { } minutes ? TimeSpan.FromMinutes(minutes) : null,
                definition?.StallSeconds,
                place.Definition.HandoffSeconds),
            Mask: null)
        {
            Ground = ground,
            Credential = order.FirstOrDefault(entry => entry.Usable)?.Credential,
            Withheld = [.. providers.Appointed().Select(appointed => appointed.Name)],
            Order = order,
            Continuation = ContinuationOf(run, layout, agent, context),
            WebSearch = webSearch,
        };

        try
        {
            var result = await runner.RunAsync(request, events: null, cancellationToken).ConfigureAwait(false);

            return Ended(result, run.Instance);
        }
        catch (HarnessException refused)
        {
            return FlowStepResult.Ended(passed: false, refused.ExitCode, refused.Message);
        }
    }

    /// <summary>
    /// The handoff turn's answer of a step a pause stopped: what the agent answered where it answered, read of the
    /// step's own record - the runner's run, ended by the stop, holds it - and null where it gave none, for the engine
    /// to write the handoff itself.
    /// </summary>
    /// <param name="run">The step, the instance and its scope, as it was last asked.</param>
    /// <param name="cancellationToken">Cancels the turn.</param>
    public Task<string?> HandoffAsync(FlowStepRun run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);

        var agent = run.WorksIn ?? run.Made?.Name;

        if (run.Place is not { } place || agent is null)
        {
            return Task.FromResult<string?>(null);
        }

        var directory = OrchestratorLayout.Of(new HarnessLayout(place.Repository, place.Repository), place.Run).AgentStepDirectory(agent, run.Instance);
        var resultFile = Path.Combine(directory, AgentRunner.ResultFileName);

        if (!fileSystem.FileExists(resultFile))
        {
            return Task.FromResult<string?>(null);
        }

        var result = JsonStateFile.Read<AgentRunResult>(fileSystem, resultFile);

        return Task.FromResult(result?.Handoff is { Answered: true, Answer: { } answered } ? answered : null);
    }

    /// <summary>
    /// The step's ending as its run's maps it: an answer that read against its schema is the step's output; a stop is
    /// incomplete, whatever it ended; a limit of the step's own asks; and a failure or a refusal ends the step with its
    /// own words.
    /// </summary>
    /// <param name="result">How the agent's run ended.</param>
    /// <param name="instance">The step instance's name, for the words that name it.</param>
    private static FlowStepResult Ended(AgentRunResult result, string instance)
    {
        if (result.Problem() is { } wrong)
        {
            return FlowStepResult.Ended(passed: false, HarnessExit.InternalError, $"'{instance}' answered a result that does not hold together: {wrong}");
        }

        return result.Status switch
        {
            AgentRunStatus.Completed => FlowStepResult.Ended(
                passed: true,
                HarnessExit.Success,
                output: result.StructuredOutput is { Length: > 0 } json && JsonNode.Parse(json) is { } parsed ? parsed : null),
            AgentRunStatus.Asked => FlowStepResult.Ended(
                passed: false,
                HarnessExit.Refused,
                $"'{instance}'s agent asked the person, which this build does not yet serve"),
            AgentRunStatus.Cancelled or AgentRunStatus.HandedOff => FlowStepResult.Ended(passed: false, HarnessExit.Incomplete, result.Error ?? "the step was stopped before it ended"),
            AgentRunStatus.NotStarted => FlowStepResult.Ended(passed: false, HarnessExit.Refused, result.Error ?? "the step's agent was not started"),
            AgentRunStatus.Stalled or AgentRunStatus.TimedOut or AgentRunStatus.RateLimited or AgentRunStatus.Truncated
                => FlowStepResult.Ended(passed: false, HarnessExit.Incomplete, result.Error ?? "the step's agent ended incomplete"),
            AgentRunStatus.MaxTurns => FlowStepResult.AtLimit("turns", result.Error),
            AgentRunStatus.BudgetSpent => FlowStepResult.AtLimit("budget", result.Error),
            AgentRunStatus.ContextExhausted => FlowStepResult.AtLimit("context", result.Error),
            _ => FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, result.Error ?? $"'{instance}'s agent failed"),
        };
    }

    /// <summary>
    /// Where the run works: its agent's worktree where it has one - the working directory, which it writes - or, for a
    /// read-only agent, the main checkout, which it only reads; its own work directory either way, and the run's
    /// directory for the run's own records.
    /// </summary>
    /// <param name="record">The agent's record.</param>
    /// <param name="mainCheckout">The main checkout's root.</param>
    /// <param name="layout">The run's own paths.</param>
    /// <param name="agent">The agent's name.</param>
    private static AgentWorkspace Workspace(AgentRecord record, string mainCheckout, OrchestratorLayout layout, string agent)
    {
        var work = Path.Combine(layout.AgentDirectory(agent), OrchestratorLayout.WorkDirectoryName);

        return new AgentWorkspace(
            record.Reads ? null : record.Worktree,
            work,
            mainCheckout,
            [],
            record.Reads || record.Worktree is not { Length: > 0 } ? work : record.Worktree,
            layout.Directory);
    }

    /// <summary>
    /// The agent definition <paramref name="agent"/> names, read whole; null where none is named. The file that does
    /// read and holds problems, or is not there, is refused - the definition's check said its problems when the run
    /// began, and this says where they are again.
    /// </summary>
    /// <param name="files">Reads the files the workflow names.</param>
    /// <param name="layout">The repository's layout.</param>
    /// <param name="workflow">The workflow's name.</param>
    /// <param name="agent">The agent definition's name; null where the step names none.</param>
    private static AgentDefinition? DefinitionOf(WorkflowNamedFiles files, HarnessLayout layout, string workflow, string? agent)
    {
        if (agent is null)
        {
            return null;
        }

        var (definition, problems) = AgentDefinitionParser.Parse(files.ReadText(layout, workflow, $"agents/{agent}.md", "an agent definition"), agent);

        if (problems.Count > 0 || definition is null)
        {
            throw new HarnessException(HarnessExit.ConfigInvalid, $"agents/{agent}.md does not read: {string.Join("; ", problems)}");
        }

        return definition;
    }

    /// <summary>
    /// The output the step's answer is held to, where it names one: its schema file read whole, the step's own name the
    /// name its output is read by.
    /// </summary>
    /// <param name="files">Reads the files the workflow names.</param>
    /// <param name="layout">The repository's layout.</param>
    /// <param name="workflow">The workflow's name.</param>
    /// <param name="step">The step, whose name its output is read by and whose ask rounds are counted here.</param>
    private static AgentOutput? OutputOf(WorkflowNamedFiles files, HarnessLayout layout, string workflow, WorkflowStep step)
        => step.Outputs is { } named
            ? new AgentOutput(step.Name, files.ReadText(layout, workflow, named, "a schema"), MaxRepairs: 2, AskRounds: step.Ask == true ? step.AskRounds ?? 2 : null)
            : null;

    /// <summary>
    /// The web search the step's agent is given, where its configuration names sources and the slot answers their keys;
    /// null where the agent searches none.
    /// </summary>
    /// <param name="context">The repository's context, whose configuration names the sources.</param>
    /// <param name="providers">The configuration's providers and tiers, resolved, whose appointed names the slot holds.</param>
    /// <param name="step">The step, whose <c>search</c> picks its sources.</param>
    /// <param name="cancellationToken">Stops the reading of keys.</param>
    private async Task<AgentWebSearch?> WebSearchOf(HarnessContext context, AgentProviders providers, WorkflowStep step, CancellationToken cancellationToken)
    {
        var sources = Web.Search.WebSearchSources.Of(context.Config, Web.Search.WebSearchKinds.OfThisBuild);

        if (sources.Chosen(step.Search) is not [_, ..] chosen)
        {
            return null;
        }

        var read = await new Web.Search.WebSearchKeys(keys, gh).ReadAsync(context.Layout, providers.Appointed(), chosen, cancellationToken).ConfigureAwait(false);

        return AgentWebSearch.Of(sources, step.Search, read);
    }

    /// <summary>
    /// The continuation record of the work the step goes on with, where it goes on with any: the instances the engine
    /// named as its sources rendered whole, their copies put in the agent's own work directory. Null where the step
    /// begins its own work.
    /// </summary>
    /// <param name="run">The step, with the instances whose working sets its context pack is seeded from.</param>
    /// <param name="layout">The run's own paths.</param>
    /// <param name="agent">The agent the step works as.</param>
    /// <param name="context">The repository's context, whose configuration bounds the record.</param>
    private string? ContinuationOf(FlowStepRun run, OrchestratorLayout layout, string agent, HarnessContext context)
    {
        if (!GoesOnWithEarlierWork(run) || run.WorkedFrom is not [_, ..] worked)
        {
            return null;
        }

        var steps = worked.Select(source => new AgentContinuedStep(
                source.Instance,
                "done",
                source.Agent is { } sourceAgent ? layout.AgentStepDirectory(sourceAgent, source.Instance) : layout.StepDirectory(source.Instance)))
            .ToList();

        var work = new AgentContinuationWork(
            worked[0].Instance,
            steps,
            ChangedPaths: [],
            RedLegs: [],
            CutOff: run.Handoff is not null);

        return new AgentContinuation(fileSystem, harnessOutput).Render(
            work,
            Path.Combine(layout.AgentDirectory(agent), OrchestratorLayout.WorkDirectoryName),
            AgentContinuationBounds.Of(context.Config.AgentContext?.Continuation));
    }

    /// <summary>Whether the step goes on with earlier work: another step's, or its own attempt that a stop cut.</summary>
    /// <param name="run">The step.</param>
    private static bool GoesOnWithEarlierWork(FlowStepRun run)
        => run.Step.Resume is not null || run.Attempt > 1 || run.Handoff is not null;

    /// <summary>
    /// The prompt with each skill it names handed over: every <c>{skill.&lt;name&gt;}</c> read again where the step
    /// starts - the first place that has the folder deciding, one that is there and cannot be used refused, never
    /// fallen past - the folder copied whole into the agent's <c>skills/&lt;name&gt;/</c> in place of an earlier copy,
    /// and the block put where the placeholder stood, its tools held to the capabilities the agent's own ground gives
    /// it. Null, with why, where one could not be handed.
    /// </summary>
    /// <param name="prompt">The prompt, its other names already filled by the engine.</param>
    /// <param name="run">The step, whose instance names the skills and whose agent holds them.</param>
    /// <param name="agent">The agent the step works as.</param>
    /// <param name="capabilities">The capabilities the agent's own ground gives it, its tools held to them.</param>
    /// <param name="workspace">Where the run works, the skills copied under its work directory.</param>
    /// <param name="resolution">The step's resolution, whose effort the blocks are given.</param>
    /// <param name="problem">Why a skill could not be handed; null where every one was.</param>
    private string? SkillBlocks(
        string prompt,
        FlowStepRun run,
        string agent,
        IReadOnlyList<AgentCapability> capabilities,
        AgentWorkspace workspace,
        AgentResolution resolution,
        out string? problem)
    {
        var skills = new AgentSkills(fileSystem, platform, workspace.MainCheckout);
        var may = capabilities.Select(one => one.ToString().ToLowerInvariant()).ToList();
        var filled = new StringBuilder(prompt.Length);

        foreach (var piece in FlowTemplate.Pieces(prompt))
        {
            if (piece.Kind != FlowTemplate.PieceKind.Placeholder || !piece.Name.StartsWith("skill.", StringComparison.Ordinal))
            {
                filled.Append(piece.Text);
                continue;
            }

            var name = piece.Name["skill.".Length..];

            if (name.Length == 0)
            {
                problem = "the prompt names {skill.} for no skill";
                return null;
            }

            var found = skills.Find(name) ?? throw new HarnessException(HarnessExit.Refused, skills.Missing(name));

            if (found.ProblemsFor(run.Instance, agent, may) is [_, ..] refused)
            {
                problem = string.Join(" ", refused);
                return null;
            }

            var directory = skills.Copy(found, workspace.WorkDirectory);
            var block = found.Block(directory, workspace.Worktree ?? workspace.MainCheckout, run.Instance, resolution.Serving.Effort ?? string.Empty, out var unread);

            if (block is null)
            {
                problem = unread ?? $"skill '{name}' cannot be used";
                return null;
            }

            filled.Append(block);
        }

        problem = null;
        return filled.ToString();
    }

    /// <summary>
    /// What the chain of work <paramref name="name"/> resumes names, where its first naming step names it: each step
    /// the chain goes on with in turn - the resumed step, then the step that resumes, until one names a tier, a model
    /// or an agent - for a step that goes on with another's work inherits what that chain names, where the chain names
    /// anything and the step itself names none. Null where no step of the chain names anything.
    /// </summary>
    /// <param name="definition">The workflow the run runs.</param>
    /// <param name="name">The resumed step's name.</param>
    private static ResumedStep? Resumed(WorkflowDefinition definition, string name)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        while (seen.Add(name))
        {
            if (Declared(definition, name) is not { } step)
            {
                return null;
            }

            // The first step of the chain that names what a step that goes on with its work inherits: the chain stops
            // there, for the steps before it named nothing to inherit.
            if (step.Tier is not null || step.Model is not null || step.Agent is not null)
            {
                return new ResumedStep(step.Tier, step.Model, step.Agent);
            }

            if (step.Resume is not { } next || string.Equals(next, step.Name, StringComparison.Ordinal))
            {
                return null;
            }

            name = next;
        }

        return null;
    }

    /// <summary>The reason step named <paramref name="name"/>, as the definition declares it; null where none is.</summary>
    /// <param name="definition">The workflow the run runs.</param>
    /// <param name="name">The step's name.</param>
    private static WorkflowStep? Declared(WorkflowDefinition definition, string name)
    {
        WorkflowStep? found = null;

        void Walk(IReadOnlyList<WorkflowStep> steps)
        {
            foreach (var step in steps)
            {
                if (found is not null)
                {
                    return;
                }

                if (step.Type == "reason" && string.Equals(step.Name, name, StringComparison.Ordinal))
                {
                    found = step;
                    return;
                }

                Walk(step.Steps);
            }
        }

        Walk(definition.Steps);

        return found;
    }

    /// <summary>What a resumed step hands the step that goes on with its work, where that names none of its own.</summary>
    /// <param name="Tier">The tier the resumed step runs on; null where it names none.</param>
    /// <param name="Model">The model it names outright; null where it names none.</param>
    /// <param name="Agent">The agent definition it names, its model below the step's own; null where it names none.</param>
    private sealed record ResumedStep(string? Tier, string? Model, string? Agent);
}

/// <summary>The runner of a step type that can answer a stopped step's handoff turn; every other type's runner cannot, and the engine writes the handoff itself.</summary>
public interface IFlowHandoffRunner
{
    /// <summary>
    /// The handoff turn's answer of a step a pause stopped, where the agent gave one; null where it gave none, for the
    /// engine to write the handoff itself.
    /// </summary>
    /// <param name="run">The step, the instance and its scope, as it was last asked.</param>
    /// <param name="cancellationToken">Cancels the turn.</param>
    Task<string?> HandoffAsync(FlowStepRun run, CancellationToken cancellationToken);
}
