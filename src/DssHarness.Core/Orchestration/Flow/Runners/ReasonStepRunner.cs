using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DssHarness.Core.Agents;
using DssHarness.Core.Agents.ClaudeCode;
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
/// <param name="store">Reads the agent's record, and guards the repository's key-spend record across runs.</param>
/// <param name="create">Makes the read-only agent a step that names none is given.</param>
/// <param name="runner">Works the step, whatever the client is.</param>
/// <param name="fileSystem">The file system the step's files are read on.</param>
/// <param name="keys">The slot the entry's keys are read from.</param>
/// <param name="gh">Runs <c>gh</c>, for a web-search source the machine's login reads.</param>
/// <param name="harnessOutput">Writes output the copies of a continuation record name.</param>
/// <param name="clock">Tells the week a balanced key's spend is counted for.</param>
/// <param name="gitClient">Asks git, for the paths an agent's worktree holds that no fold has taken.</param>
/// <param name="configStore">Reads the repository's configuration, for the bounds of the engine's own account of a stream.</param>
/// <param name="updates">Makes the Claude Code a run needs ready, where the step's order holds the client that starts one.</param>
public sealed class ReasonStepRunner(
    IHarnessContextLoader contextLoader,
    IHostPlatform platform,
    OrchestrationStore store,
    CreateAgentRunner create,
    IAgentRunner runner,
    IFileSystem fileSystem,
    AgentKeyStore keys,
    GhProcess gh,
    Output.IHarnessOutput harnessOutput,
    TimeProvider clock,
    Git.IGitClient? gitClient = null,
    Configuration.IConfigStore? configStore = null,
    ClaudeCodeUpdateService? updates = null) : IFlowStepTypeRunner, IFlowHandoffRunner
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
        // read-only one, made here where the step names none. The engine names that one for the step before it runs and
        // stores it as the agent it works in, so its name on the run says which agent is to be made - and an agent
        // already live (an earlier attempt of this visit made it, or a visit before it) is never made again, the making
        // answering it as already made.
        string? working = run.WorksIn;

        if (run.Made is { } own && (working is null || string.Equals(working, own.Name, StringComparison.Ordinal)))
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

        // The running order: the resolution's entries with their credentials, and - where the configuration balances
        // a provider's keys by what each spent - each model's keys in the order of least spent in the last seven days,
        // whatever tier, model, step, run or side-task spent it, ties in the list's order. The key is chosen when the
        // step starts and kept for it; what each entry spends is recorded for the next step's choice.
        var spend = new AgentKeySpend(fileSystem, store, clock);
        var spent = context.Config.BalanceKeyUsage == true ? spend.Spent(context.Layout) : null;
        var order = AgentStepOrder.Of(providers, resolution, context.Layout, keys, new ClaudeTranscripts(fileSystem, platform).ConfigDirectory, spent);

        // Where the order holds the client that starts a program, the program is found ready here - installed and
        // brought up to date where it must be - and the run starts what was found, never a launcher of its own: a
        // program not ready refuses the step with the finder's own words and code.
        string? program = null;
        string? programVersion = null;

        if (updates is not null && order.Any(entry => string.Equals(entry.Client, "claude-code", StringComparison.Ordinal)))
        {
            var ready = await updates.ReadyAsync(
                context.Layout,
                providers.Appointed(),
                providers.ClientOf("claude-code"),
                steps: $"'{run.Instance}'",
                cancellationToken).ConfigureAwait(false);

            if (!ready.Ready)
            {
                return FlowStepResult.Ended(passed: false, ready.ExitCode, ready.Message);
            }

            program = ready.Program;
            programVersion = ready.Version;
        }

        var workspace = Workspace(agentRecord, context.Layout.MainCheckoutRoot, layout, agent);
        var (webSearch, webMask) = await WebSearchOf(context, providers, run.Step, cancellationToken).ConfigureAwait(false);
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
        // step goes on with earlier work and names one, in its place. An attempt that goes on with an answer - a
        // person's answer to what its agent asked, a limit's `more` or `compact` - sends the answer as its message in
        // the attempt's own conversation instead, and no skill is handed again.
        var goingOn = GoingOnOf(run);

        // The prompt the attempt sends: an answer that goes on with this attempt sends its own message - the person's
        // words, or what a granted bound tells the agent - in the attempt's own conversation; a compaction sends the
        // engine's record as the run's first message and the filled prompt stands for the files that read it; every
        // other start fills the step's prompt, the resume prompt where the step goes on with earlier work and names one.
        string? resume = null;

        if (goingOn is null && run.Step.ResumePrompt is not null && GoesOnWithEarlierWork(run)
            && run.Read.TryGetValue("resumePrompt", out var resumeValue) && resumeValue.AsScalar("resumePrompt") is string readResume)
        {
            resume = readResume;
        }

        string? filled;
        string? refusedSkill = null;

        if (goingOn is { Prompt: { } message })
        {
            filled = message;
        }
        else
        {
            filled = SkillBlocks(goingOn is { Compact: true } || resume is null ? readPrompt : resume!, run, agent, ground.Capabilities, workspace, resolution, out refusedSkill);
        }

        if (filled is null)
        {
            return FlowStepResult.Ended(passed: false, HarnessExit.Refused, refusedSkill!);
        }

        // The handoff the attempt is given - a stop wrote it, or a limit the attempt before this one reached - stands
        // at the head of its prompt, and is spent once the prompt holds it.
        filled = WithHandoff(run.Handoff, filled);

        // The conversation this attempt goes on with, where its answers gave it one to go on with: the session the
        // step's own stream names last, there for the handing only where the engine stored one. A client that renders
        // its conversations - Claude Code - is handed none, and the attempt's own continuation record stands in its
        // place. A compaction is the engine's reduction of the conversation so far, which every client begins a NEW
        // conversation from - the old one stays whole and is never sent again.
        var stream = goingOn is null ? [] : StreamOf(layout, agent, run.Instance);
        var conversation = goingOn is null or { Compact: true } ? null : SessionIn(stream, layout, agent);
        var ownContinuation = goingOn is null || conversation is not null ? null : await OwnContinuationOfAsync(layout, agent, run, context, cancellationToken).ConfigureAwait(false);
        var continuation = ownContinuation ?? await ContinuationOfAsync(run, layout, agent, context, cancellationToken).ConfigureAwait(false);
        var compaction = goingOn is { Compact: true } ? CompactedFrom(stream, run.Attempt, context) : null;

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
            OutputOf(files, context.Layout, place.Definition.Name, run.Step, run),
            Conversation: conversation,
            new AgentLimits(
                definition?.MaxTurns,
                definition?.MaxBudgetUsd,
                definition?.MaxMinutes is { } minutes ? TimeSpan.FromMinutes(minutes) : null,
                definition?.StallSeconds,
                place.Definition.HandoffSeconds),
            Mask: webMask)
        {
            Ground = ground,
            Credential = order.FirstOrDefault(entry => entry.Usable)?.Credential,
            Withheld = [.. providers.Appointed().Select(appointed => appointed.Name)],
            Order = order,
            Continuation = continuation,
            WebSearch = webSearch,
            Spend = spend.RecordingIn(context.Layout),
            Compaction = compaction,
            Retries = run.Step.Retries ?? place.Definition.MaxRetries ?? 3,
            BackoffSeconds = run.Step.BackoffSeconds ?? place.Definition.BackoffSeconds,
            Program = program,
            ProgramVersion = programVersion,
        };

        try
        {
            var result = await runner.RunAsync(request, events: null, cancellationToken).ConfigureAwait(false);

            // A limit's question says whether a stored conversation of the attempt can be handed, which is where the
            // answer `more` is offered; no other ending reads it.
            var stored = result.Status is AgentRunStatus.MaxTurns or AgentRunStatus.BudgetSpent or AgentRunStatus.ContextExhausted
                ? SessionIn(StreamOf(layout, agent, run.Instance), layout, agent) is not null
                : false;

            if (run.Handoff is not null && result.Status != AgentRunStatus.NotStarted)
            {
                // The prompt that holds the handoff is on the disk now, so the handoff is spent - never between the
                // two. A run that never began leaves it standing for the retry that may follow.
                run.Events.HandoffTaken();
            }

            return Ended(result, run.Instance, stored);
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
    /// The engine's account of the step's stored stream - the body of the handoff it writes where the agent gave none:
    /// what the newest attempt's events show (the files it wrote, the last commands it ran and how each ended, its last
    /// messages word for word, as many as the continuation record keeps) beside the steps before it that left notes,
    /// named by step and not by path. Null where the stream holds nothing to say.
    /// </summary>
    /// <param name="run">The step, the instance and its scope, as it was last asked.</param>
    string? IFlowHandoffRunner.Account(FlowStepRun run)
    {
        ArgumentNullException.ThrowIfNull(run);

        var agent = run.WorksIn ?? run.Made?.Name;

        if (run.Place is not { } place || agent is null)
        {
            return null;
        }

        var layout = OrchestratorLayout.Of(new HarnessLayout(place.Repository, place.Repository), place.Run);
        var stream = StreamOf(layout, agent, run.Instance);

        if (stream.Count == 0)
        {
            return null;
        }

        // The steps before it that left notes: each source of the work its directory holds a record of, named by step
        // and never by path, for the next attempt has their notes in what it is given.
        var before = (run.WorkedFrom ?? [])
            .Where(source => fileSystem.KindOf(Path.Combine(
                source.Agent is { } sourceAgent ? layout.AgentStepDirectory(sourceAgent, source.Instance) : layout.StepDirectory(source.Instance),
                OrchestratorLayout.StepNotesFileName)) == PathKind.File)
            .Select(source => new AgentContinuedStep(source.Instance, "done", string.Empty))
            .ToList();

        return AgentStreamAccount.Of(stream, before, BoundsOf(place).LastIterations);
    }

    /// <summary>The repository's bounds of a continuation record, read where the step starts; the defaults where its configuration cannot be read, for the engine's account never refuses a pause.</summary>
    /// <param name="place">The run's place, whose repository holds the configuration.</param>
    private AgentContinuationBounds BoundsOf(FlowRunPlace place)
    {
        try
        {
            return AgentContinuationBounds.Of(configStore?.Load(new HarnessLayout(place.Repository, place.Repository).ConfigFile).AgentContext?.Continuation);
        }
        catch (Configuration.ConfigException)
        {
            return new AgentContinuationBounds();
        }
    }

    /// <summary>
    /// The step's ending as its run's maps it: an answer that read against its schema is the step's output; a stop is
    /// incomplete, whatever it ended; the person asked is the step's question; a limit of the step's own asks, saying
    /// whether a stored conversation of the attempt can be handed, which is where the answer <c>more</c> is offered;
    /// every account at its limit, or the provider's service failing past the client's own tries, pauses the run safely
    /// and resumes this very step; and a failure or a refusal ends the step with its own words.
    /// </summary>
    /// <param name="result">How the agent's run ended.</param>
    /// <param name="instance">The step instance's name, for the words that name it.</param>
    /// <param name="storedConversation">Whether a stored conversation of the attempt exists for a client to be handed.</param>
    private static FlowStepResult Ended(AgentRunResult result, string instance, bool storedConversation)
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
                output: result.StructuredOutput is { Length: > 0 } json && JsonNode.Parse(json) is { } parsed ? parsed : null)
                with { Assumptions = result.Assumptions },
            AgentRunStatus.Asked => FlowStepResult.Asking(
                new FlowQuestion(FlowQuestions.RunsOwn, "ask", instance, AskedProblemOf(result, instance), FlowQuestions.OptionsOf("ask", step: null))
                {
                    AgentQuestions = result.Questions,
                    RaisedBy = new FlowQuestionRaisedBy("step", instance),
                    Recommendation = new FlowRecommendation("given", Why: "each of its questions names the answer the agent recommends", By: "agent"),
                })
                with { Assumptions = result.Assumptions },
            AgentRunStatus.Cancelled or AgentRunStatus.HandedOff => FlowStepResult.Ended(passed: false, HarnessExit.Incomplete, result.Error ?? "the step was stopped before it ended") with { Assumptions = result.Assumptions },
            AgentRunStatus.NotStarted => FlowStepResult.Ended(passed: false, HarnessExit.Refused, result.Error ?? "the step's agent was not started"),
            AgentRunStatus.RateLimited => FlowStepResult.SafelyPausing(result.Error ?? $"'{instance}'s every account is at its limit"),
            AgentRunStatus.Failed or AgentRunStatus.Stalled when result.ServiceFailed
                => FlowStepResult.SafelyPausing(result.Error ?? $"'{instance}'s provider's service failed"),
            AgentRunStatus.Stalled or AgentRunStatus.TimedOut or AgentRunStatus.Truncated
                => FlowStepResult.Ended(passed: false, HarnessExit.Incomplete, result.Error ?? "the step's agent ended incomplete"),
            AgentRunStatus.MaxTurns => AtLimit(result, "turns", instance, storedConversation),
            AgentRunStatus.BudgetSpent => AtLimit(result, "budget", instance, storedConversation),
            AgentRunStatus.ContextExhausted => AtLimit(result, "context", instance, storedConversation),
            _ => FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, result.Error ?? $"'{instance}'s agent failed") with { Assumptions = result.Assumptions },
        };

        static FlowStepResult AtLimit(AgentRunResult result, string limit, string instance, bool stored)
            => FlowStepResult.AtLimit(limit, result.Error) with { StoredConversation = stored };
    }

    /// <summary>
    /// What the step's asking says, written from its records: that its agent asked the person, and each question it
    /// asked, each with its own problem and the answer its agent recommends.
    /// </summary>
    /// <param name="result">How the agent's run ended: asking.</param>
    /// <param name="instance">The step instance's name, for the words that name it.</param>
    private static string AskedProblemOf(AgentRunResult result, string instance)
        => $"'{instance}'s agent asked the person {result.Questions.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)} question{(result.Questions.Count == 1 ? string.Empty : "s")}: "
            + string.Join(" ", result.Questions.Select(asked => $"'{asked.Question}' (it recommends '{asked.Recommended}')"));

    /// <summary>
    /// The handoff the attempt is given, at the head of its prompt: a part of its own, so what goes on with the prompt
    /// reads the agent's account first and the task after it.
    /// </summary>
    /// <param name="handoff">The handoff's text; null where the attempt is given none.</param>
    /// <param name="prompt">The filled prompt.</param>
    private static string WithHandoff(string? handoff, string prompt)
        => handoff is { Length: > 0 } ? $"<{HandoffTag}>{Environment.NewLine}{handoff.TrimEnd()}{Environment.NewLine}</{HandoffTag}>{Environment.NewLine}{Environment.NewLine}{prompt}" : prompt;

    /// <summary>The tag the handoff's part of a prompt is wrapped in, as the first user message wraps its own parts.</summary>
    public const string HandoffTag = "handoff";

    /// <summary>
    /// What this attempt goes on with, where its answers gave it something to: the last answer of this attempt's
    /// questions - a person's answer to what its agent asked, read as the attempt's next message; a limit answered
    /// <c>more</c>, which hands the attempt its own conversation and grants the bound once again; a full context
    /// answered <c>compact</c>, which hands it the engine's reduction of the conversation so far, its last iterations
    /// word for word. Null where the answers gave it nothing, and the attempt is a start like any other; an earlier
    /// attempt's answers are never served, for each attempt is its own.
    /// </summary>
    /// <param name="run">The step, with the answers its questions were given.</param>
    private GoingOn? GoingOnOf(FlowStepRun run)
    {
        foreach (var given in run.Answers.Reverse().Where(given => given.Attempt == run.Attempt))
        {
            switch (given.Kind, given.Answer)
            {
                case ("ask", "answer"):
                    return new(given.Text is { Length: > 0 } ? $"The person answered:{Environment.NewLine}{given.Text}" : "The person answered with nothing: decide for yourself and finish the task.", Compact: false);
                case ("ask", "decide"):
                    return new("The person leaves the questions to you: go on with what you recommended for each, record each under your assumptions, and finish the task.", Compact: false);
                default:
                    if (given.Answer == "more" && IsLimit(given.Kind))
                    {
                        return new($"The run's person allowed the step's {WordOf(given.Kind)} once again. Go on with the task.", Compact: false);
                    }

                    if (given.Answer == "compact" && given.Kind == "contextLimit")
                    {
                        return new(Prompt: null, Compact: true);
                    }

                    break;
            }
        }

        return null;

        static bool IsLimit(string? kind)
            => kind is "turnLimit" or "stepBudget" or "truncated" or "contextLimit";

        static string WordOf(string? kind)
            => kind switch
            {
                "turnLimit" => "turn bound",
                "stepBudget" => "budget",
                "truncated" => "answer",
                "contextLimit" => "context",
                _ => "bound",
            };
    }

    /// <summary>What one attempt goes on with from its answers: the message it sends instead of its prompt, and whether it begins from the engine's compaction of the conversation so far.</summary>
    /// <param name="Prompt">The attempt's message; null where it sends its own filled prompt.</param>
    /// <param name="Compact">Whether the answer was <c>compact</c>, and the attempt begins from the engine's reduction of the conversation so far.</param>
    private sealed record GoingOn(string? Prompt, bool Compact);

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
        var work = layout.WorkDirectory(agent);

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
    /// name its output is read by, and - where the step may ask - the rounds it may ask in one attempt, less what its
    /// answers already spent: after the last the agent is offered no questions and told so.
    /// </summary>
    /// <param name="files">Reads the files the workflow names.</param>
    /// <param name="layout">The repository's layout.</param>
    /// <param name="workflow">The workflow's name.</param>
    /// <param name="step">The step, whose name its output is read by and whose ask rounds are counted here.</param>
    /// <param name="run">The step run, whose answers spent the rounds it was given.</param>
    private static AgentOutput? OutputOf(WorkflowNamedFiles files, HarnessLayout layout, string workflow, WorkflowStep step, FlowStepRun run)
        => step.Outputs is { } named
            ? new AgentOutput(
                step.Name,
                files.ReadText(layout, workflow, named, "a schema"),
                MaxRepairs: 2,
                AskRounds: step.Ask == true ? Math.Max(0, (step.AskRounds ?? 2) - run.Answers.Count(given => given.Kind == "ask" && given.Attempt == run.Attempt)) : null)
            : null;

    /// <summary>
    /// The web search the step's agent is given, where its configuration names sources and the slot answers their keys;
    /// null where the agent searches none.
    /// </summary>
    /// <param name="context">The repository's context, whose configuration names the sources.</param>
    /// <param name="providers">The configuration's providers and tiers, resolved, whose appointed names the slot holds.</param>
    /// <param name="step">The step, whose <c>search</c> picks its sources.</param>
    /// <param name="cancellationToken">Stops the reading of keys.</param>
    private async Task<(AgentWebSearch? Search, Runners.ActionValues? Mask)> WebSearchOf(HarnessContext context, AgentProviders providers, WorkflowStep step, CancellationToken cancellationToken)
    {
        var sources = Web.Search.WebSearchSources.Of(context.Config, Web.Search.WebSearchKinds.OfThisBuild);

        if (sources.Chosen(step.Search) is not [_, ..] chosen)
        {
            return (null, null);
        }

        var read = await new Web.Search.WebSearchKeys(keys, gh).ReadAsync(context.Layout, providers.Appointed(), chosen, cancellationToken).ConfigureAwait(false);

        // The step's mask holds every form each key it was given is sent in, so no record of the step's - its stream,
        // its result, its notes - writes one: the search itself refuses a mask that does not, and this builds the mask
        // that does.
        var forms = chosen
            .Where(source => read.TryGetValue(source.Name, out var key) && key.Value is { Length: > 0 })
            .SelectMany(source => source.Credential?.Forms(read[source.Name].Value!) ?? [read[source.Name].Value!])
            .Where(form => form is { Length: > 0 })
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return (AgentWebSearch.Of(sources, step.Search, read), forms is [_, ..]
            ? new Runners.ActionValues(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), forms.ToDictionary(form => form, form => form, StringComparer.OrdinalIgnoreCase))
            : null);
    }

    /// <summary>
    /// The continuation record of the work the step goes on with, where it goes on with any: the instances the engine
    /// named as its sources rendered whole - each as its own record says it ended - the paths this agent's folds wrote
    /// and its worktree holds unfolded, and the red legs of its newest red build or test, their copies put in the
    /// agent's own work directory. Null where the step begins its own work.
    /// </summary>
    /// <param name="run">The step, with the instances whose working sets its context pack is seeded from.</param>
    /// <param name="layout">The run's own paths.</param>
    /// <param name="agent">The agent the step works as.</param>
    /// <param name="context">The repository's context, whose configuration bounds the record.</param>
    /// <param name="cancellationToken">Stops the reading of the worktree.</param>
    private async Task<string?> ContinuationOfAsync(FlowStepRun run, OrchestratorLayout layout, string agent, HarnessContext context, CancellationToken cancellationToken)
    {
        if (!GoesOnWithEarlierWork(run) || run.WorkedFrom is not [_, ..] worked)
        {
            return null;
        }

        var steps = worked.Select(source => new AgentContinuedStep(
                source.Instance,
                EndedOf(RecordOf(layout, source)),
                DirectoryOf(layout, source)))
            .ToList();

        var work = new AgentContinuationWork(
            worked[0].Instance,
            steps,
            await ChangedPathsOfAsync(layout, agent, context, cancellationToken).ConfigureAwait(false),
            RedLegsOf(layout, agent),
            CutOff: run.Handoff is not null);

        return new AgentContinuation(fileSystem, harnessOutput).Render(
            work,
            layout.WorkDirectory(agent),
            AgentContinuationBounds.Of(context.Config.AgentContext?.Continuation));
    }

    /// <summary>
    /// The continuation record of the step's own work, for an answer that goes on with it on a client no conversation
    /// can be handed to: what the step's own records hold - its iterations last, the agent's questions among them word
    /// for word - with the paths this agent's folds wrote and its worktree holds unfolded, and the red legs of its
    /// newest red build or test, rendered as a continuation record renders a step.
    /// </summary>
    /// <param name="layout">The run's own paths.</param>
    /// <param name="agent">The agent the step works as.</param>
    /// <param name="run">The step.</param>
    /// <param name="context">The repository's context, whose configuration bounds the record.</param>
    /// <param name="cancellationToken">Stops the reading of the worktree.</param>
    private async Task<string?> OwnContinuationOfAsync(OrchestratorLayout layout, string agent, FlowStepRun run, HarnessContext context, CancellationToken cancellationToken)
    {
        var work = new AgentContinuationWork(
            run.Instance,
            [new AgentContinuedStep(run.Instance, "done", layout.AgentStepDirectory(agent, run.Instance))],
            await ChangedPathsOfAsync(layout, agent, context, cancellationToken).ConfigureAwait(false),
            RedLegsOf(layout, agent),
            CutOff: false);

        return new AgentContinuation(fileSystem, harnessOutput).Render(
            work,
            layout.WorkDirectory(agent),
            AgentContinuationBounds.Of(context.Config.AgentContext?.Continuation));
    }

    /// <summary>Where one source of the work the step goes on with keeps its records.</summary>
    /// <param name="layout">The run's own paths.</param>
    /// <param name="source">The source, as the engine named it.</param>
    private static string DirectoryOf(OrchestratorLayout layout, FlowWorkedFrom source)
        => source.Agent is { } sourceAgent ? layout.AgentStepDirectory(sourceAgent, source.Instance) : layout.StepDirectory(source.Instance);

    /// <summary>One source's own record, where its directory holds one; null where none was written.</summary>
    /// <param name="layout">The run's own paths.</param>
    /// <param name="source">The source, as the engine named it.</param>
    private FlowStepRecord? RecordOf(OrchestratorLayout layout, FlowWorkedFrom source)
    {
        var record = Path.Combine(DirectoryOf(layout, source), OrchestratorLayout.StepRecordFileName);

        return fileSystem.KindOf(record) == PathKind.File ? store.ReadStepRecord(record) : null;
    }

    /// <summary>How a step's own record says it ended, in the record's own words; <c>done</c> where no record was written.</summary>
    /// <param name="record">The step's own record; null where none was written.</param>
    private static string EndedOf(FlowStepRecord? record)
        => record?.State switch
        {
            nameof(FlowInstanceState.Done) => "done",
            nameof(FlowInstanceState.Stopped) => "stopped by a pause",
            nameof(FlowInstanceState.Running) => "cut off",
            nameof(FlowInstanceState.Paused) => "stopped on a question",
            nameof(FlowInstanceState.Failed) => "failed",
            null => "done",
            _ => "ended",
        };

    /// <summary>
    /// The paths this agent changed, as the continuation record lists them: the paths its folds wrote into the main
    /// tree (the run's ledger of its folds) and the paths its worktree holds that no fold has taken - what a fold of
    /// the agent would weigh, by the fold's own rule - an agent with no worktree having changed none beyond its folds.
    /// </summary>
    /// <param name="layout">The run's own paths.</param>
    /// <param name="agent">The agent the step works as.</param>
    /// <param name="context">The repository's context, whose configuration names the worktrees root.</param>
    /// <param name="cancellationToken">Stops the reading of the worktree.</param>
    private async Task<IReadOnlyList<string>> ChangedPathsOfAsync(OrchestratorLayout layout, string agent, HarnessContext context, CancellationToken cancellationToken)
    {
        var folded = FoldLedger.Changed(
            (store.ReadFolds(layout)?.Folds ?? []).Where(entry => string.Equals(entry.Agent, agent, StringComparison.Ordinal)));

        // The worktree as the repository holds it, resolved from the record's path under the worktrees root: the paths
        // it holds that no fold has taken are what a fold of the agent would weigh.
        if (store.ReadAgent(layout, agent) is not { } record || record.WorktreePath(context.Layout) is not { } worktree || gitClient is null)
        {
            return folded;
        }

        var held = await new AgentFold(gitClient, fileSystem, FilePermissionsFactory.Create(), platform)
            .WeighedAsync(worktree, store.ReadSeed(layout, agent), TreeFloor.OfAgentMoves(context.Config.Worktrees.Root), cancellationToken)
            .ConfigureAwait(false);

        return [.. folded.Concat(held).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
    }

    /// <summary>
    /// The red legs of the agent's newest build or test that was red, as the record says them: each red leg, its phase
    /// and the ledger's line for it, and where the copied ledger and its logs are. Empty where none was red.
    /// </summary>
    /// <param name="layout">The run's own paths.</param>
    /// <param name="agent">The agent the step works as, whose steps ran them.</param>
    private IReadOnlyList<AgentRedLeg> RedLegsOf(OrchestratorLayout layout, string agent)
    {
        if (store.ReadFlow(layout)?.Steps is not [_, ..] steps)
        {
            return [];
        }

        foreach (var step in steps
            .Where(step => string.Equals(step.Agent, agent, StringComparison.Ordinal)
                && step.FinishedAt is not null
                && step.Output?["red"] is JsonArray { Count: > 0 })
            .OrderByDescending(step => step.FinishedAt))
        {
            var directory = layout.InstanceDirectory(step.Instance, step.Agent);
            var legs = new List<AgentRedLeg>();

            foreach (var red in step.Output!["red"]!.AsArray().OfType<JsonObject>())
            {
                if (red["leg"] is JsonValue leg && leg.GetValueKind() == JsonValueKind.String
                    && red["detail"] is JsonValue detail && detail.GetValueKind() == JsonValueKind.String)
                {
                    var phase = red["phase"] is JsonValue named && named.GetValueKind() == JsonValueKind.String ? named.GetValue<string>() : null;
                    legs.Add(new AgentRedLeg(leg.GetValue<string>(), phase ?? string.Empty, detail.GetValue<string>(), directory));
                }
            }

            if (legs is [_, ..])
            {
                return legs;
            }
        }

        return [];
    }

    /// <summary>Whether the step goes on with earlier work: another step's, or its own attempt that a stop cut.</summary>
    /// <param name="run">The step.</param>
    private static bool GoesOnWithEarlierWork(FlowStepRun run)
        => run.Step.Resume is not null || run.Attempt > 1 || run.Handoff is not null;

    /// <summary>
    /// The events the step's own stream holds, masked as it holds them: the step's transcript, from which the engine
    /// reads what no agent can say any more - the session a conversation goes on as, and the conversation a compaction
    /// reduces. Empty where the step streamed nothing yet.
    /// </summary>
    /// <param name="layout">The run's own paths.</param>
    /// <param name="agent">The agent the step works as, whose directory keeps the step's records.</param>
    /// <param name="instance">The step instance's name.</param>
    private IReadOnlyList<AgentEvent> StreamOf(OrchestratorLayout layout, string agent, string instance)
    {
        var file = Path.Combine(layout.AgentStepDirectory(agent, instance), StoredStream.FileName);

        if (!fileSystem.FileExists(file))
        {
            return [];
        }

        var events = new List<AgentEvent>();

        foreach (var line in fileSystem.ReadAllText(file).Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                if (JsonSerializer.Deserialize<AgentEvent>(line.TrimEnd('\r'), JsonStateFile.LineOptions) is { } read)
                {
                    events.Add(read);
                }
            }
            catch (JsonException)
            {
                // A torn line a killed writer left takes itself back; the whole ones before it stand.
            }
        }

        return events;
    }

    /// <summary>
    /// The stored conversation the step's own stream names last, where the engine holds one it can hand: the session's
    /// id with the client and provider that began it, and only where its <c>conversation.jsonl</c> is there - a client
    /// that renders its conversations, Claude Code among them, names a session of its own that no conversation can be
    /// handed to. Null where none is.
    /// </summary>
    /// <param name="stream">The step's own events.</param>
    /// <param name="layout">The run's own paths.</param>
    /// <param name="agent">The agent the step works as, whose sessions directory keeps its conversations.</param>
    private AgentSessionRef? SessionIn(IReadOnlyList<AgentEvent> stream, OrchestratorLayout layout, string agent)
    {
        var last = stream.OfType<SessionStarted>().LastOrDefault(started => started.SessionId is { Length: > 0 });

        if (last?.SessionId is not { } id)
        {
            return null;
        }

        return fileSystem.FileExists(Path.Combine(OrchestratorLayout.SessionDirectoryIn(layout.AgentSessionsDirectory(agent), id), "conversation.jsonl"))
            ? new AgentSessionRef(last.Client, last.Provider ?? string.Empty, id)
            : null;
    }

    /// <summary>
    /// The engine's compaction of the conversation the step's own stream holds, its last iterations word for word -
    /// what the answer <c>compact</c> goes on from, a new conversation that begins with it. The window is the
    /// attempt's whole conversation: every engine run of this attempt, from its first message - the task - to its
    /// last, for the conversation one attempt goes on with spans every answer it was given.
    /// </summary>
    /// <param name="stream">The step's own events.</param>
    /// <param name="attempt">The attempt the compaction answers.</param>
    /// <param name="context">The repository's context, whose configuration bounds the record and the reduction.</param>
    private static string? CompactedFrom(IReadOnlyList<AgentEvent> stream, int attempt, HarnessContext context)
    {
        var window = AttemptOf(stream, attempt);

        return window.Count == 0 ? null : AgentCompaction.Reduce(
            window,
            AgentContinuationBounds.Of(context.Config.AgentContext?.Continuation),
            AgentCompactionBounds.Of(context.Config.AgentContext?.Compaction)).Record;
    }

    /// <summary>The events of <paramref name="attempt"/> alone: everything after the last run an earlier attempt began; every event where none did.</summary>
    /// <param name="stream">The step's own events.</param>
    /// <param name="attempt">The attempt whose events are asked for, from 1.</param>
    private static IReadOnlyList<AgentEvent> AttemptOf(IReadOnlyList<AgentEvent> stream, int attempt)
    {
        // The newest run of an earlier attempt ends the window; every event after it is this attempt's, whatever run
        // of this attempt began it.
        var lastEarlier = -1;

        for (var index = 0; index < stream.Count; index++)
        {
            if (stream[index] is RunStarted begun && begun.Attempt != attempt)
            {
                lastEarlier = index;
            }
        }

        return lastEarlier < 0 ? stream : [.. stream.Skip(lastEarlier + 1)];
    }

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

    /// <summary>
    /// The engine's account of the step's stored stream - the body of the handoff it writes where the agent gave none
    /// - or null where the stream holds nothing to say, for the handoff to stand on its first line alone.
    /// </summary>
    /// <param name="run">The step, the instance and its scope, as it was last asked.</param>
    string? Account(FlowStepRun run);
}

/// <summary>
/// Reads the engine's account of a stopped or cut-off step's stored stream: the body of the handoff it writes where
/// the agent gave none. The coordinator asks it through the run's one runner, which hands the step to the runner of
/// its type; null where that runner's type keeps no stream.
/// </summary>
public interface IFlowStreamAccount
{
    /// <summary>
    /// The engine's account of the step's stored stream, or null where it holds nothing to say: the files the newest
    /// attempt wrote, the last commands it ran and how each ended, its last messages word for word, and the steps
    /// before it that left notes.
    /// </summary>
    /// <param name="run">The step, the instance and its scope, as it was last asked.</param>
    string? Account(FlowStepRun run);
}
