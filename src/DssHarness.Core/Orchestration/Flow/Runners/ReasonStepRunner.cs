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
/// through <see cref="IAgentRunner"/> - the definition it works by read where the chain of work it goes on with began
/// (<see cref="ResumeChain.Root"/>), the step's model resolved with that definition below its own, its order, workspace,
/// ground and mask built as every launch builds them (<see cref="ReasonLaunch"/>), its prompt as the engine filled it
/// with its skills handed over, the work it goes on with rendered as its continuation record, and its going on by the
/// client's kind (the stored conversation handed, or a rendered launch: <see cref="AgentRunner"/> owns which).
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
/// <param name="isolation">Decides the operating system's isolation the step's agent runs under.</param>
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
    Agents.Isolation.IAgentIsolation isolation,
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
        var launch = new ReasonLaunch(fileSystem, platform, keys, updates, isolation);

        var promptFile = run.Step.Prompt;

        if (promptFile is null || run.Prompts?.TryGetValue("prompt", out var readPrompt) is not true)
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

        // The step the work it goes on with began with - itself, where it begins its own - whose agent definition, client,
        // tier, model, writes and web search it works with: a step that goes on with another's work is the same agent
        // going on, and names none of them. The definition, read as the definition's check reads it, is the one the step
        // works by everywhere: its system prompt sent as written, its capabilities, restrictions and writes on the
        // ground, its limits, and the level below the step's own for the model, the effort and the client.
        var root = ResumeChain.Root(place.Definition, FlowInstanceId.PathOf(run.Instance), run.Step);
        var definition = DefinitionOf(files, context.Layout, place.Definition.Name, root.Agent);
        var step = ReferenceEquals(root, run.Step) ? run.Step : run.Step with { Writes = root.Writes };

        var resolution = AgentResolution.Of(providers, root.Tier, root.Model, run.Step.Effort, run.Step.EffortByModel, root.Client, definition);

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
        var order = launch.Order(providers, resolution, context.Layout, spent);

        // Where the order holds the client that starts a program, the program is found ready here - installed and
        // brought up to date where it must be - and the run starts what was found, never a launcher of its own: a
        // program not ready refuses the step with the finder's own words and code.
        var program = await launch.ProgramAsync(context.Layout, providers, order, ClaudeCodeAsker.Run($" (step '{run.Instance}')"), change: true, cancellationToken).ConfigureAwait(false);

        if (program is { Ready: false })
        {
            return FlowStepResult.Ended(passed: false, program.ExitCode, program.Message);
        }

        var workspace = launch.Workspace(agentRecord, context.Layout, layout.WorkDirectory(agent), layout.Directory);
        var (webSearch, webMask) = await WebSearchOf(context, providers, root, cancellationToken).ConfigureAwait(false);
        var ground = AgentStepGround.Of(
            context.Config,
            definition,
            step,
            workspace,
            webSearch is not null,
            launch.ClaudeDirectories(order));

        // The operating system's isolation the agent's commands run under, decided before anything of the step is handed
        // over: where agentPolicy.isolation is required and this machine has none, the step does not start, saying why and
        // what would give it one.
        var isolated = await launch.IsolationAsync(context.Config, ground, context.Layout, StepDirectoryOf(layout, run.Instance, agent), cancellationToken).ConfigureAwait(false);

        if (isolated.Refusal($"'{run.Instance}'") is { } unisolated)
        {
            return FlowStepResult.Ended(passed: false, HarnessExit.ToolMissing, unisolated);
        }

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
        IReadOnlyList<FlowTemplate.Piece>? resume = null;

        if (goingOn is null && run.Step.ResumePrompt is not null && GoesOnWithEarlierWork(run)
            && run.Prompts.TryGetValue("resumePrompt", out var readResume))
        {
            resume = readResume;
        }

        // A run that begins a conversation of its own - a start, or a compaction's new conversation - is given the id
        // its first session begins as here, before the prompt is written, so a skill's ${CLAUDE_SESSION_ID} is that
        // session's; an answer that goes on with the attempt's own conversation keeps that conversation's.
        string filled;
        string? session = null;
        IReadOnlyList<AgentSkill> skills = [];

        if (goingOn is { Prompt: { } message })
        {
            filled = message;
        }
        else
        {
            session = Guid.NewGuid().ToString("D");

            var handed = SkillBlocks(goingOn is { Compact: true } || resume is null ? readPrompt : resume, run, root.Agent ?? agent, ground.Capabilities, workspace, resolution, session);

            if (handed.Refused is { } refusedSkill)
            {
                return FlowStepResult.Ended(passed: false, HarnessExit.ConfigInvalid, refusedSkill);
            }

            (filled, skills) = (handed.Prompt!, handed.Skills);
        }

        // The handoff the attempt is given - a stop wrote it, or a limit the attempt before this one reached - stands
        // at the head of its prompt, and is spent once the prompt holds it.
        filled = WithHandoff(run.Handoff, filled);

        // The conversation this attempt goes on with, where its answers gave it one to go on with: the session the
        // step's own stream names last, there for the handing only where the engine stored one. A client that renders
        // its conversations - Claude Code - is handed none, and the attempt's own continuation record stands in its
        // place. A compaction is the engine's reduction of the conversation so far, which every client begins a NEW
        // conversation from - the old one stays whole and is never sent again.
        IReadOnlyList<AgentEvent> stream;

        try
        {
            stream = goingOn is null ? [] : StreamOf(layout, agent, run.Instance);
        }
        catch (HarnessException unread)
        {
            return FlowStepResult.Ended(passed: false, unread.ExitCode, unread.Message);
        }

        var conversation = goingOn is null or { Compact: true } ? null : SessionIn(stream, layout, agent);
        var ownContinuation = goingOn is null || conversation is not null ? null : await OwnContinuationOfAsync(layout, agent, run, context, cancellationToken).ConfigureAwait(false);
        var continuation = ownContinuation ?? await ContinuationOfAsync(run, layout, agent, context, cancellationToken).ConfigureAwait(false);
        var compaction = goingOn is { Compact: true } ? CompactedFrom(stream, run.Attempt, context) : null;

        var request = new AgentStepRequest(
            place.Run,
            agent,
            run.Instance,
            run.Attempt,
            root.Agent,
            Provider: null,
            resolution.Serving.Client,
            run.Step.Tier,
            resolution.Serving.Model,
            run.Step.Effort,
            Fallback: null,
            definition?.Prompt ?? string.Empty,
            filled,
            ResumeTemplate: null,
            workspace,
            OutputOf(files, context.Layout, place.Definition.Name, run.Step, run),
            Conversation: conversation,
            new AgentLimits(
                definition?.MaxTurns,
                FlowRunBudget.Bound(definition?.MaxBudgetUsd, run.BudgetLeftUsd),
                definition?.MaxMinutes is { } minutes ? TimeSpan.FromMinutes(minutes) : null,
                definition?.StallSeconds,
                place.Definition.HandoffSeconds),
            Mask: launch.Mask(context.Layout, providers, webMask))
        {
            RecordDirectory = StepDirectoryOf(layout, run.Instance, agent),
            Ground = ground,
            Isolation = isolated,
            Credential = order.FirstOrDefault(entry => entry.Usable)?.Credential,
            Withheld = [.. providers.Appointed().Select(appointed => appointed.Name)],
            Order = order,
            Continuation = continuation,
            WebSearch = webSearch,
            Spend = spend.RecordingIn(context.Layout),
            Compaction = compaction,
            Retries = place.Definition.RetriesOf(run.Step),
            BackoffSeconds = run.Step.BackoffSeconds ?? place.Definition.BackoffSeconds,
            Program = program?.Program,
            ProgramVersion = program?.Version,
            Skills = skills,
            SessionId = conversation is null ? session : null,

            // What the engine tells the attempt's agent while it works - a plan update's message into the
            // conversation, its handoff where one ends the step in order - and what a plan update that told this
            // work said, in the part of its own the attempt's first message holds.
            Handoff = run.Telling?.Handoff,
            Messages = run.Telling?.Messages,
            PlanUpdates = run.PlanTold,
        };

        try
        {
            // What the attempt spends is charged to the run's budget turn by turn, as its agent works, at the prices of
            // the model serving it then: the run's reserve is reached while a long attempt runs, not only once it ends.
            var result = await runner.RunAsync(request, new AgentRunSpend(providers, resolution.Serving.Model, run.Events), cancellationToken).ConfigureAwait(false);

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

            // The skills the attempt's agent was given are the step's record's, however the attempt ended - but for an
            // agent that never started, which was given nothing.
            var ended = Ended(result, run.Instance, stored, FlowRunBudget.RunsShareBinds(definition?.MaxBudgetUsd, run.BudgetLeftUsd));

            return result.Status == AgentRunStatus.NotStarted ? ended : ended with { Skills = skills };
        }
        catch (HarnessException refused)
        {
            return FlowStepResult.Ended(passed: false, refused.ExitCode, refused.Message);
        }
    }

    /// <summary>
    /// The handoff turn's answer of a step a pause stopped: what the agent answered where it answered, read of the
    /// step's own record - the engine raised the handoff in the attempt's own conversation and waited for the runner,
    /// whose run, ended by the turn, holds it - and null where it gave none, for the engine to write the handoff itself.
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

        var directory = StepDirectoryOf(OrchestratorLayout.Of(new HarnessLayout(place.Repository, place.Repository), place.Run), run.Instance, agent);
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
    /// named by step and not by path. Null where the stream holds nothing to say, or a whole line of it does not read.
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
        IReadOnlyList<AgentEvent> stream;

        try
        {
            stream = StreamOf(layout, agent, run.Instance);
        }
        catch (HarnessException)
        {
            // A stream whose whole lines do not all read gives no account: one written from part of it would say less
            // than the attempt did as though it were all, and the handoff stands on its first line alone.
            return null;
        }

        if (stream.Count == 0)
        {
            return null;
        }

        // The steps before it that left notes: each source of the work its directory holds a record of, named by step
        // and never by path, for the next attempt has their notes in what it is given.
        var before = (run.WorkedFrom ?? [])
            .Where(source => fileSystem.KindOf(Path.Combine(DirectoryOf(layout, source), OrchestratorLayout.StepNotesFileName)) == PathKind.File)
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
    /// The step's ending as its run's maps it: an answer that read against its schema is the step's output, with the
    /// issues it reports that were there before the run (<see cref="FlowPreExistingReport.In"/>); a stop is
    /// incomplete, whatever it ended; the person asked is the step's question; a limit of the step's own asks, saying
    /// whether a stored conversation of the attempt can be handed, which is where the answer <c>more</c> is offered - save
    /// a budget the run's share of its own budget bounded, which is the run's reserve reached and pauses the run for its
    /// budget; every account at its limit, or the provider's service failing past the client's own tries, pauses the run
    /// safely and resumes this very step; and a failure or a refusal ends the step with its own words.
    /// </summary>
    /// <param name="result">How the agent's run ended.</param>
    /// <param name="instance">The step instance's name, for the words that name it.</param>
    /// <param name="storedConversation">Whether a stored conversation of the attempt exists for a client to be handed.</param>
    /// <param name="runsShare">Whether the agent's budget was the run's share before its reserve, and not its own bound (<see cref="FlowRunBudget.RunsShareBinds"/>).</param>
    private static FlowStepResult Ended(AgentRunResult result, string instance, bool storedConversation, bool runsShare)
    {
        if (result.Problem() is { } wrong)
        {
            return FlowStepResult.Ended(passed: false, HarnessExit.InternalError, $"'{instance}' answered a result that does not hold together: {wrong}");
        }

        // The output an answer read against its schema, and the issues it reports there that were before the run - each
        // raising the triage the step's onPreExisting names.
        var output = result.Status == AgentRunStatus.Completed && result.StructuredOutput is { Length: > 0 } json ? JsonNode.Parse(json) : null;

        return result.Status switch
        {
            AgentRunStatus.Completed => FlowStepResult.Ended(
                passed: true,
                HarnessExit.Success,
                output: output,
                preExisting: FlowPreExistingReport.In(output))
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
            AgentRunStatus.BudgetSpent when runsShare => FlowStepResult.AtTheRunsReserve($"'{instance}' spent what was left of the run's budget before its reserve{(result.Error is { } spent ? $" ({spent})" : string.Empty)}"),
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
    /// reads the agent's account first and the task after it - its lines ended as every part of a prompt's are, by a
    /// line feed alone, so a prompt is the same bytes on every system.
    /// </summary>
    /// <param name="handoff">The handoff's text; null where the attempt is given none.</param>
    /// <param name="prompt">The filled prompt.</param>
    private static string WithHandoff(string? handoff, string prompt)
        => handoff is { Length: > 0 } ? $"<{HandoffTag}>\n{handoff.TrimEnd()}\n</{HandoffTag}>\n\n{prompt}" : prompt;

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
                    return new(given.Text is { Length: > 0 } ? $"The person answered:\n{given.Text}" : "The person answered with nothing: decide for yourself and finish the task.", Compact: false);
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
    /// <param name="step">The step at the root of the step's chain of work (<see cref="ResumeChain.Root"/>), whose <c>search</c> picks its sources.</param>
    /// <param name="cancellationToken">Stops the reading of keys.</param>
    private async Task<(AgentWebSearch? Search, Runners.ActionValues? Mask)> WebSearchOf(HarnessContext context, AgentProviders providers, WorkflowStep step, CancellationToken cancellationToken)
    {
        var sources = Web.Search.WebSearchSources.Of(context.Config, Web.Search.WebSearchKinds.OfThisBuild);

        if (sources.Chosen(step.Search) is not [_, ..] chosen)
        {
            return (null, null);
        }

        var read = await new Web.Search.WebSearchKeys(keys, gh).ReadAsync(context.Layout, providers.Appointed(), chosen, cancellationToken).ConfigureAwait(false);

        return (AgentWebSearch.Of(sources, step.Search, read), Web.Search.WebSearchKeys.Mask(chosen, read));
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
            [new AgentContinuedStep(run.Instance, "done", StepDirectoryOf(layout, run.Instance, agent))],
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
        => layout.InstanceDirectory(source.Instance, source.Agent);

    /// <summary>
    /// Where the step keeps its records - its stream, its result, its first message, its notes - as the one owner of
    /// where a step instance is kept spells it (<see cref="OrchestratorLayout.InstanceDirectory"/>): the runner's request
    /// names it, and every read of what the step recorded reads it there.
    /// </summary>
    /// <param name="layout">The run's own paths.</param>
    /// <param name="instance">The step instance's id, as <see cref="FlowInstanceId"/> spells it.</param>
    /// <param name="agent">The agent the step works as, whose directory keeps the step's records.</param>
    private static string StepDirectoryOf(OrchestratorLayout layout, string instance, string agent)
        => layout.InstanceDirectory(instance, agent);

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
    /// The events the step's own stream holds, masked as it holds them, read by the store's one reader of it: the step's
    /// transcript, from which the engine reads what no agent can say any more - the session a conversation goes on as,
    /// and the conversation a compaction reduces. A torn last line a killed writer left is set aside; a whole line that
    /// does not read refuses (<see cref="OrchestrationStore.ReadStepStream"/>). Empty where the step streamed nothing yet.
    /// </summary>
    /// <param name="layout">The run's own paths.</param>
    /// <param name="agent">The agent the step works as, whose directory keeps the step's records.</param>
    /// <param name="instance">The step instance's name.</param>
    private IReadOnlyList<AgentEvent> StreamOf(OrchestratorLayout layout, string agent, string instance)
        => store.ReadStepStream(StepDirectoryOf(layout, instance, agent));

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
    /// what the answer <c>compact</c> goes on from, a new conversation that begins with it - with what it kept and
    /// reduced and the conversation it leaves, which the run says and counts. The window is the
    /// attempt's whole conversation (<see cref="AgentAttempt.Of"/>): every engine run of this attempt, from its first
    /// message - the task - to its last, for the conversation one attempt goes on with spans every answer it was given.
    /// </summary>
    /// <param name="stream">The step's own events.</param>
    /// <param name="attempt">The attempt the compaction answers.</param>
    /// <param name="context">The repository's context, whose configuration bounds the record and the reduction.</param>
    private static AgentCompacted? CompactedFrom(IReadOnlyList<AgentEvent> stream, int attempt, HarnessContext context)
    {
        var window = AgentAttempt.Of(stream, attempt);

        return window.Count == 0 ? null : AgentCompaction.Reduce(
            window,
            AgentContinuationBounds.Of(context.Config.AgentContext?.Continuation),
            AgentCompactionBounds.Of(context.Config.AgentContext?.Compaction));
    }

    /// <summary>
    /// The prompt with each skill it names handed over, from the pieces the engine filled around them - its own text
    /// joined as the engine filled it, never read as a template again, so a value's braces stay as the value holds
    /// them: every <c>{skill.&lt;name&gt;}</c> read again where the step starts - its name by the one rule of a skill's
    /// name, the first place that has the folder deciding, one that is there and cannot be used refused, never fallen
    /// past - the folder copied whole into the agent's <c>skills/&lt;name&gt;/</c> in place of an earlier copy, and the
    /// block put where the placeholder stood, its tools held to the capabilities the agent's own ground gives it and
    /// its <c>${CLAUDE_SESSION_ID}</c> the session the run begins as. Every skill that could not be handed is the
    /// prompt's refusal, said whole; the skills handed are what the step's stream records.
    /// </summary>
    /// <param name="pieces">The prompt as the engine filled it around its skills.</param>
    /// <param name="run">The step, whose instance names the skills.</param>
    /// <param name="agent">The agent a refusal names: the definition the step works by, else the agent it works as.</param>
    /// <param name="capabilities">The capabilities the agent's own ground gives it, its tools held to them.</param>
    /// <param name="workspace">Where the run works, the skills copied under its work directory.</param>
    /// <param name="resolution">The step's resolution, whose effort the blocks are given.</param>
    /// <param name="session">The id the run's first session begins as.</param>
    private HandedSkills SkillBlocks(
        IReadOnlyList<FlowTemplate.Piece> pieces,
        FlowStepRun run,
        string agent,
        IReadOnlyList<AgentCapability> capabilities,
        AgentWorkspace workspace,
        AgentResolution resolution,
        string session)
    {
        var skills = new AgentSkills(fileSystem, platform, workspace.MainCheckout);
        var may = capabilities.Select(one => one.ToString().ToLowerInvariant()).ToList();
        var filled = new StringBuilder();
        var handed = new List<AgentSkill>();

        foreach (var piece in pieces)
        {
            if (piece.Kind != FlowTemplate.PieceKind.Placeholder)
            {
                filled.Append(piece.Text);
                continue;
            }

            if (AgentSkills.NameIn(piece.Name, out var unnamed) is not { } name)
            {
                return HandedSkills.RefusedFor($"'{run.Instance}' prompt names {unnamed}");
            }

            if (skills.Find(name) is not { } found)
            {
                return HandedSkills.RefusedFor($"'{run.Instance}' prompt names {{{piece.Name}}}, and {skills.Missing(name)}");
            }

            if (found.ProblemsFor(run.Instance, agent, may) is [_, ..] refused)
            {
                return HandedSkills.RefusedFor(string.Join(" ", refused));
            }

            var directory = skills.Copy(found, workspace.WorkDirectory);

            if (found.Block(directory, workspace.Worktree ?? workspace.MainCheckout, session, resolution.Serving.Effort ?? string.Empty, out var unread) is not { } block)
            {
                return HandedSkills.RefusedFor(unread ?? $"skill '{name}' ('{found.File}') cannot be used");
            }

            filled.Append(block);
            handed.Add(new AgentSkill(found.Name, found.From, found.File, found.Digest!, found.FolderDigest!, directory));
        }

        return new HandedSkills(filled.ToString(), handed, Refused: null);
    }

    /// <summary>The prompt with its skills handed over, or why one could not be.</summary>
    /// <param name="Prompt">The prompt the run sends; null where a skill was refused.</param>
    /// <param name="Skills">Each skill handed over, as the step's stream records it.</param>
    /// <param name="Refused">Why a skill could not be handed, said whole; null where every one was.</param>
    private sealed record HandedSkills(string? Prompt, IReadOnlyList<AgentSkill> Skills, string? Refused)
    {
        /// <summary>The answer of a prompt one of whose skills could not be handed.</summary>
        /// <param name="why">Why, said whole.</param>
        public static HandedSkills RefusedFor(string why) => new(Prompt: null, [], why);
    }
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
