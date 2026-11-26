using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DssHarness.Core.Agents;
using DssHarness.Core.Agents.ClaudeCode;
using DssHarness.Core.Agents.Policy;
using DssHarness.Core.Application.Legs;
using DssHarness.Core.Ci;
using DssHarness.Core.Configuration;
using DssHarness.Core.Execution;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Json;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Platform;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;
using DssHarness.Core.Runs;
using DssHarness.Core.Secrets;

namespace DssHarness.Core.Orchestration.Flow;

/// <summary>
/// The <c>reason</c> step: the agent a reason step is given, made where the step names none (a read-only agent, its tree
/// made by the one making of every agent's - <see cref="CreateAgentRunner.MakeAsync"/> over the one verb that makes agents -
/// and brought to the main checkout's HEAD, or to the commit the step names, as each step that works as it starts,
/// <see cref="CreateAgentRunner.ReadyAsync"/>), and its step worked
/// through <see cref="IAgentRunner"/> - the definition it works by read where the chain of work it goes on with began
/// (<see cref="ResumeChain.Root"/>), the step's model resolved with that definition below its own, its order, workspace,
/// ground and mask built as every launch builds them (<see cref="ReasonLaunch"/>), its prompt as the engine filled it
/// with its skills handed over, the work it goes on with rendered as its continuation record, and its going on by the
/// client's kind (the stored conversation handed, or a rendered launch: <see cref="AgentRunner"/> owns which).
/// </summary>
/// <param name="contextLoader">Finds the repository and its configuration.</param>
/// <param name="platform">This machine.</param>
/// <param name="store">Reads the agent's record, and guards the repository's key-spend record across runs.</param>
/// <param name="create">Makes the read-only agent a step that names none is given, and brings its tree to the main checkout's HEAD, or to the commit the step names, as each step that works as it starts.</param>
/// <param name="runner">Works the step, whatever the client is.</param>
/// <param name="fileSystem">The file system the step's files are read on.</param>
/// <param name="keys">The slot the entry's keys are read from.</param>
/// <param name="gh">Runs <c>gh</c>, for a web-search source the machine's login reads.</param>
/// <param name="harnessOutput">Writes output the copies of a continuation record name, and says a write of a measure call's ledger the file system refused as its legs' run went.</param>
/// <param name="clock">Tells the week a balanced key's spend is counted for, and when the machine's gh login's expiry has come.</param>
/// <param name="isolation">Decides the operating system's isolation the step's agent runs under.</param>
/// <param name="gitClient">
/// Asks git, for the paths an agent's worktree holds that no fold has taken, and for what it holds untracked as a step whose
/// writes narrow it first starts (<see cref="AgentStepStart"/>).
/// </param>
/// <param name="updates">Makes the Claude Code a run needs ready, where the step's order holds the client that starts one.</param>
/// <param name="graphService">The one graph service every graph is read and written by, for the step's serving of the run's graph; null where none is given, and no step is served.</param>
/// <param name="graphClock">Times the graph tool's searches; the machine's clock where none is given.</param>
/// <param name="graphSyncs">
/// The engine's own syncs of a run's graph, for the view of a reader's own tree once it is brought to the main checkout's
/// HEAD or the commit its step names, or found as the attempt an answer goes on with left it
/// (<see cref="FlowGraphSyncs.ReadiedAsync"/>); null where none is given, and no such view is synced.
/// </param>
/// <param name="tests">Tests the legs, for the measure tool the step's agent is given where the repository declares legs and the agent runs commands.</param>
/// <param name="children">Records in the step's directory the group of what a measure call's legs start bound, while anything is in it.</param>
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
    Application.Legs.ITestLegs tests,
    ChildSessions children,
    Git.IGitClient gitClient,
    ClaudeCodeUpdateService? updates = null,
    Codebase.ICodebaseGraphService? graphService = null,
    TimeProvider? graphClock = null,
    FlowGraphSyncs? graphSyncs = null) : IFlowStepTypeRunner, IFlowHandoffRunner
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
        var files = WorkflowNamedFiles.OfRun(fileSystem, platform.PathComparison, layout);
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
            var making = await create.MakeAsync(run, readsOnly: true, cancellationToken).ConfigureAwait(false);

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

        // An agent that only reads works in a tree of its own that is exactly what the main checkout committed as the step
        // starts - or the commit the step's commit names - each attempt of its first step and of every one that goes on with
        // its work: brought to the main checkout's HEAD, or to that commit, now, whatever it wrote there before discarded,
        // and made where it is not there, before anything of the step reads it. An attempt that goes on with an answer in
        // its own conversation - a person's, a limit's more or compact - is the same start going on, and finds the tree as
        // its attempt left it: save where the agent's record names another attempt as the one its tree was last brought
        // for - another step working as the agent started while this one waited, for a step that waits on its answer holds
        // no tree - or names none, as a record an older tool wrote does. Then the tree is brought there again, as the start
        // brings it, and the agent is told so in the message it goes on with: what it wrote there is gone. A start whose
        // record names this very attempt finds its tree made for it a moment ago, at the main checkout's HEAD, by the making
        // above (CreateAgentRunner.MakeAsync) - an attempt starts once - and brings it nowhere again.
        var goingOn = GoingOnOf(run);
        string? broughtAgain = null;

        if (agentRecord.Reads && agentRecord.ReadiedFor != CreateAgentRunner.ReadiedFor(run))
        {
            if (await create.ReadyAsync(run, agent, cancellationToken).ConfigureAwait(false) is { } unready)
            {
                return unready;
            }

            agentRecord = store.ReadAgent(layout, agent)!;

            if (goingOn is not null)
            {
                broughtAgain = TreeBroughtAgain(run.Step.Commit, agentRecord.Base!);
            }
        }

        // A reader's tree holds what the commit it was brought to holds, not what the main checkout does - and, for an attempt
        // an answer goes on with, what that attempt wrote there since: its own view is the one the step is served, synced now
        // that the tree stands where the step reads it - the step's start came before.
        if (graphSyncs is not null && FlowGraphSyncs.ViewsOwnTree(agentRecord, run.Step))
        {
            await graphSyncs.ReadiedAsync(run, agent, cancellationToken).ConfigureAwait(false);
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
        var root = ResumeChain.Root(place.Definition, run.DeclarationPath, run.Step);
        var definition = DefinitionOf(files, context.Layout, root.WorkflowOf ?? place.Definition.Name, root.Agent);
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
        // program not ready refuses the step where the entry that would serve first needs it, and where only a
        // fallback does, the order the step runs by passes that fallback over, the finder's words as why.
        var program = await launch.ProgramAsync(context.Layout, providers, order, ClaudeCodeAsker.Run($" (step '{run.Instance}')"), change: true, cancellationToken).ConfigureAwait(false);

        if (program.Refusal is { } notReady)
        {
            return FlowStepResult.Ended(passed: false, notReady.ExitCode, notReady.Message);
        }

        order = program.Order;

        var workspace = launch.Workspace(agentRecord, context.Layout, layout.WorkDirectory(agent), layout.Directory);
        var sources = Web.Search.WebSearchSources.Of(context.Config, Web.Search.WebSearchKinds.OfThisBuild);

        // The step's search names what it asks: an edit of the configuration that takes a source it names away refuses
        // the step's start with V55's words, as a configuration that no longer reads does - the step does not start on
        // it, and the run goes on once the configuration names the source again or the step's search names what reads.
        if (sources.ListProblems(root.Search, $"the search of '{run.Instance}'", $"'{run.Instance}' searches") is [_, ..] noLongerReads)
        {
            return FlowStepResult.Ended(passed: false, HarnessExit.ConfigInvalid, string.Join("; ", noLongerReads));
        }

        var (webSearch, webMask, logins) = await WebSearchOf(context, providers, sources, root, cancellationToken).ConfigureAwait(false);

        // What the step's agent is handed to read - its skills, its continuation record's copies - the tool writes in the
        // launch's own directory, which no command of the agent's writes, and the agent reads as a root of its own.
        var handed = OrchestratorLayout.HandedDirectory(StepDirectoryOf(layout, run.Instance, agent));
        var ground = AgentStepGround.Of(
            context.Config,
            definition,
            step,
            workspace,
            webSearch is not null,
            launch.ClaudeDirectories(context.Config, context.Layout),
            launch.CredentialPlaces(context.Config),
            handed: handed);

        // What the step's worktree held untracked as the step first started, where its writes narrow what it writes there:
        // read from the step's directory where its first start wrote it, else taken now, before its agent is started - so a
        // path its own commands made since, and only such a path, is one it may take back whatever its writes say.
        try
        {
            ground = ground with
            {
                StepStart = await AgentStepStart.OfAsync(
                    fileSystem,
                    gitClient,
                    ground,
                    workspace.Worktree,
                    StepDirectoryOf(layout, run.Instance, agent),
                    platform.PathComparison,
                    cancellationToken).ConfigureAwait(false),
            };
        }
        catch (HarnessException untold)
        {
            return FlowStepResult.Ended(passed: false, untold.ExitCode, $"'{run.Instance}' does not start: what its worktree held untracked as the step began cannot be told, so nothing its commands make could be told apart: {untold.Message}");
        }

        // The operating system's isolation the agent's commands run under, decided before anything of the step is handed
        // over: where agentPolicy.isolation is required and this machine has none, the step does not start, saying why and
        // what would give it one.
        using var isolated = await launch.IsolationAsync(context.Config, ground, context.Layout, StepDirectoryOf(layout, run.Instance, agent), cancellationToken).ConfigureAwait(false);

        if (isolated.Refusal($"'{run.Instance}'") is { } unisolated)
        {
            return FlowStepResult.Ended(passed: false, HarnessExit.ToolMissing, unisolated);
        }

        // The policy the step runs under, kept in its records as the configuration stood when it started: the agents'
        // configuration is read when each step starts, as its prompts are, so an edit of config.json - a side-task's
        // among them - reaches the steps that start after it and never one under way.
        KeptPolicy(fileSystem, layout, run.Instance, agent, ground, webSearch, order);

        // The prompt with each skill it names handed over: the folder copied into what the step's agent is handed, the
        // block put in the prompt's place, and what cannot be handed refused here - the step-start half of the skill's
        // failing loud twice, held to the capabilities the agent's own ground gives it. The resume prompt, where the
        // step goes on with earlier work and names one, in its place. An attempt that goes on with an answer - a
        // person's answer to what its agent asked, a limit's `more` or `compact` - sends the answer as its message in
        // the attempt's own conversation instead, and no skill is handed again.

        // The prompt the attempt sends: an answer that goes on with this attempt sends its own message - the person's
        // words, or what a granted bound tells the agent - in the attempt's own conversation; a compaction sends the
        // engine's record as the run's first message and the filled prompt stands for the files that read it; every
        // other start fills the step's prompt, the resume prompt - what a step that goes on with earlier work is asked
        // with when it runs again - only where earlier work there is: its own attempt before this one, the work its
        // conversation goes on with (FlowStepRun.GoesOnWith), or a handoff. A step that resumes its own work is asked with
        // its prompt the first time it runs, whatever steps it depends on.
        IReadOnlyList<FlowTemplate.Piece>? resume = null;

        if (goingOn is null && run.Step.ResumePrompt is not null && (run.Attempt > 1 || run.Handoff is not null || run.GoesOnWith is [_, ..])
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
        var prompted = goingOn is { Compact: true } || resume is null ? "prompt" : "resumePrompt";

        if (goingOn is { Prompt: { } message })
        {
            filled = message;
        }
        else
        {
            session = Guid.NewGuid().ToString("D");

            var given = SkillBlocks(resume is null || prompted == "prompt" ? readPrompt : resume, run, root.Agent ?? agent, ground.Capabilities, workspace, handed, resolution, session);

            if (given.Refused is { } refusedSkill)
            {
                return FlowStepResult.Ended(passed: false, HarnessExit.ConfigInvalid, refusedSkill);
            }

            (filled, skills) = (given.Prompt!, given.Skills);
        }

        // An attempt that goes on in a tree brought there again since it waited is told so first, before what it goes on
        // with: whatever it remembers writing there is gone.
        if (broughtAgain is not null)
        {
            filled = $"{broughtAgain}\n\n{filled}";
        }

        // The handoff the attempt is given - a stop wrote it, or a limit the attempt before this one reached - stands
        // at the head of its prompt, and is spent once the prompt holds it.
        filled = WithHandoff(run.Handoff, filled);

        // The step's serving of the run's codebase graph: its pack made from what the run handed the prompt, the graph tool
        // offered where its step says tools - which asks the tool's map and pack for their pages, pushed neither unless its
        // graphPromptTokens says a size for its pack - and a step at map, which has no tool, pushed the map and the pack as
        // the <repository-map> and <codebase> parts of the first message of each launch that begins a conversation of its
        // own; its view the one the engine syncs at the step's start - or, for a reader's tree brought to the commit its
        // step names, the one synced above once it stood there. A step whose mode is off is served nothing, and one
        // whose graph cannot be served goes on without it. The pack is seeded from the
        // values the prompt was filled with - the request, the work item, the upstream outputs it reads - with the
        // handoff, or from the words an answer sends, never from the template's own words: every run of the workflow is
        // given those alike, and a graph of a repository that holds a workflow's text would match them first.
        var seeds = goingOn is { Prompt: { } answered }
            ? answered
            : string.Join("\n\n", (run.Handoff is { Length: > 0 } handoff ? [handoff] : Array.Empty<string>()).Concat(run.PromptValues?.GetValueOrDefault(prompted) ?? []));
        var began = launch.Mask(context.Layout, providers, webMask);
        Execution.ILineMask? mask = began;

        // A step whose search sends the machine's gh login keeps its mask open to the login read again while it runs: a
        // token read for a host that refused the one the step began with, or for an expiry that came, is held by the mask
        // every record of the step is kept through before it is sent.
        if (webSearch is not null && logins is not null)
        {
            (webSearch, mask) = webSearch.Renewing(began, logins, context.Layout);
        }

        var stepGraph = graphService is null
            ? null
            : await FlowStepGraph.OfAsync(
                graphService,
                store,
                fileSystem,
                platform,
                gitClient,
                graphClock ?? clock,
                harnessOutput,
                run,
                place,
                context,
                definition,
                root,
                agent,
                agentRecord,
                ground,
                seeds,
                mask,
                cancellationToken).ConfigureAwait(false);

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
        var ownContinuation = goingOn is null || conversation is not null ? null : await OwnContinuationOfAsync(layout, agent, run, handed, context, cancellationToken).ConfigureAwait(false);
        var continuation = ownContinuation ?? await ContinuationOfAsync(run, layout, agent, handed, context, cancellationToken).ConfigureAwait(false);
        var compaction = goingOn is { Compact: true } ? CompactedFrom(stream, run.Attempt, context) : null;

        // The changes of the codebase the conversation this attempt goes on with is told: what its view changed since
        // what that conversation was told of it - its pack and every delta after it - and the nodes of its own pack it
        // was not given - in the one new user message, never a map or a pack again whole, for the conversation's prefix
        // is never changed.
        var codebaseChanges = conversation is null ? null : stepGraph?.ChangesSince(CodebaseToldOf(stream));

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
            OutputOf(files, context.Layout, run.Step.WorkflowOf ?? place.Definition.Name, run.Step, run),
            Conversation: conversation,
            new AgentLimits(
                definition?.MaxTurns,
                FlowRunBudget.Bound(definition?.MaxBudgetUsd, run.BudgetLeftUsd),
                definition?.MaxMinutes is { } minutes ? TimeSpan.FromMinutes(minutes) : null,
                definition?.StallSeconds,
                place.Definition.HandoffSeconds),
            Mask: mask)
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

            // What the step falls back to where it says neither: the governing use's where one declares the key, else
            // the run's - the one rule the engine's retry table reads too.
            Retries = FlowUse.RetriesOf(place.Definition, run.DeclarationPath, run.Step),
            BackoffSeconds = FlowUse.BackoffSecondsOf(place.Definition, run.DeclarationPath, run.Step),
            Program = program.Program,
            ProgramVersion = program.Version,
            Skills = skills,
            SessionId = conversation is null ? session : null,

            // The step's codebase graph, all of it carried: whether a launch's conversation is handed is decided entry by
            // entry, so a launch that goes on with the stored conversation is told what changed and one that begins its own
            // - another client's, another provider's - is pushed what its step pushes, the map ahead of the pack
            // (AgentPromptParts.CodebaseOf), each launch's line written as it is given its first message; the graph tool
            // offered with the operations and the narrowing of reads its step says.
            Codebase = stepGraph?.Section,
            CodebaseChanges = codebaseChanges,
            Map = stepGraph?.Map,
            Graph = stepGraph?.Support,
            CodebaseGiven = stepGraph is null ? null : stepGraph.Given,

            // The measure tool, where the repository declares legs and the step's agent runs commands: what it measures a
            // neighbour of a class with, served on every client and answered by the test service as a leg step's legs are.
            Measure = Measured(context, place, workspace, ground, run.Instance, StepDirectoryOf(layout, run.Instance, agent)),

            // What the engine tells the attempt's agent while it works - a plan update's message into the
            // conversation, its handoff where one ends the step in order - and what a plan update that told this
            // work said, in the part of its own the attempt's first message holds.
            Handoff = run.Telling?.Handoff,
            Messages = run.Telling?.Messages,
            PlanUpdates = run.PlanTold,
        };

        try
        {
            // What the attempt spends is charged into its measure and the run's budget turn by turn, as its agent works,
            // at the prices of the model serving it then: the run's reserve is reached while a long attempt runs, not only
            // once it ends. The count also keeps the run's own account - what served it, the turns, the tools, the
            // compactions - which the engine adds to the attempt's entry when this run of it ends.
            var counter = new AgentRunSpend(providers, resolution.Serving.Model, run.Events, resolution.Serving.Effort);
            var result = await runner.RunAsync(request, counter, cancellationToken).ConfigureAwait(false);

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

            // The step's graph, where it was given one, ends with the step: its working set written beside its output,
            // the seeds of the packs of the steps after it, and its figures gathered for its record. A step done whose
            // work no later step's conversation goes on with puts away the pages its conversation kept - its graph answers'
            // and its call results': none is asked again.
            var graphUse = stepGraph?.Ended(graphService!, fileSystem, platform.PathComparison, layout, cancellationToken);

            if (ended is { Passed: true, Asked: null } && !ResumeChain.GoneOnWith(place.Definition, run.DeclarationPath, run.Step))
            {
                FlowStepGraph.PutAwayPages(fileSystem, harnessOutput, StepDirectoryOf(layout, run.Instance, agent), run.Instance);
            }

            return result.Status == AgentRunStatus.NotStarted ? ended : ended with { Skills = skills, Graph = graphUse, Attempt = counter.Account() };
        }
        catch (HarnessException refused)
        {
            return FlowStepResult.Ended(passed: false, refused.ExitCode, refused.Message);
        }
    }

    /// <summary>
    /// The step's measure tool, where the repository declares legs and the step's agent runs commands
    /// (<see cref="AgentMeasureSupport.OfferedOn"/>, the one rule the tool runtime and the Claude Code client serve it by
    /// too): each call answered through the test service, as a leg step's legs are run (<see cref="MeasuredAsync"/>).
    /// Null where the repository declares no leg - there is nothing to measure on - where the agent may run no command,
    /// and for a run held in memory alone.
    /// </summary>
    /// <param name="context">The repository and its configuration.</param>
    /// <param name="place">The run's place; null for a run held in memory alone.</param>
    /// <param name="workspace">Where the step's agent works.</param>
    /// <param name="ground">What the step's agent is decided against, whose capabilities say whether it runs commands.</param>
    /// <param name="instance">The step's instance, as a refusal of a call names it.</param>
    /// <param name="stepDirectory">The step's own directory, where what a call's legs start bound is recorded.</param>
    private AgentMeasureSupport? Measured(HarnessContext context, FlowRunPlace? place, AgentWorkspace workspace, Agents.Policy.AgentPolicyGround ground, string instance, string stepDirectory)
        => place is null || context.Config.Legs.Count == 0 || !AgentMeasureSupport.OfferedOn(ground)
            ? null
            : new((call, token) => MeasuredAsync(context, place, workspace, instance, stepDirectory, call, token));

    /// <summary>
    /// One measure call answered: the leg it names, built as the variant it names - the build configuration the leg's
    /// own configuration declares, the only one a measurement of the leg runs - tested with the filter it names through
    /// the test service, in the tree the agent works in where it has one of its own - an agent that only reads has its tree
    /// at the main checkout's HEAD, or at the commit its step names, which the start of its next step takes back with
    /// whatever the measurement built - else
    /// in a local copy of the main tree the leg makes as a leg's local copy is, so no fold in flight changes what it
    /// measures; run as every step
    /// of the engine runs legs (<see cref="FlowStepLegs"/>) - the isolation decided first, under the step's process scope,
    /// the ledger kept in the call's own records. What it showed is read by the one owner of what a leg's verdict means
    /// (<see cref="FlowLegVerdicts.ClassOf"/>): a red leg - its build or its tests failed - is measured and failed, a leg
    /// that passed is measured and passed, and every other verdict - a leg that reached none of its own (refused by a
    /// lock, skipped, not admitted, stopped, its inputs moved) or a failure that is no finding about the code (a filter
    /// that matched no test, a log another run's, no verdict reached) - measured nothing of the cell, and is answered
    /// unmeasured with the leg's own words as why. A leg that is not declared, a configuration the leg does not build, a
    /// run its isolation or its use case refused, or one that ended before its leg said anything, is answered unmeasured
    /// too, with the reason.
    /// </summary>
    /// <param name="context">The repository and its configuration.</param>
    /// <param name="place">The run's place.</param>
    /// <param name="workspace">Where the step's agent works.</param>
    /// <param name="instance">The step's instance, as a refusal of the call names it.</param>
    /// <param name="stepDirectory">The step's own directory, where what the call's legs start bound is recorded.</param>
    /// <param name="call">The call: its id, leg, variant, filter and cell, and where it keeps its records.</param>
    /// <param name="cancellationToken">The hard stop.</param>
    private async Task<AgentMeasureAnswer> MeasuredAsync(HarnessContext context, FlowRunPlace place, AgentWorkspace workspace, string instance, string stepDirectory, AgentMeasureCall call, CancellationToken cancellationToken)
    {
        if (!context.Config.Legs.TryGetValue(call.Leg, out var leg))
        {
            return Unmeasured(context.Config.Legs.Count == 0
                ? "this repository declares no leg"
                : $"no leg '{call.Leg}' is declared: the run's legs are {string.Join(", ", context.Config.Legs.Keys.Order(StringComparer.Ordinal))}");
        }

        if (!string.Equals(call.Variant, leg.Config, StringComparison.Ordinal))
        {
            return Unmeasured($"the leg '{call.Leg}' builds the configuration '{leg.Config}'");
        }

        var tree = workspace.Worktree ?? place.Repository;
        FlowStepLegsRan ran;

        try
        {
            ran = await new FlowStepLegs(fileSystem, harnessOutput, children, isolation).RunAsync(
                new FlowStepLegsRequest(context.Config, context.Layout.MainCheckoutRoot, tree, stepDirectory, call.Records, RedLogs: workspace.Worktree is not null, $"the measure call '{call.Id}' of '{instance}'", Type),
                watch: null,
                (legIsolation, kept) => tests.RunAsync(
                    new TestLegsRequest
                    {
                        Directory = tree,
                        LegNames = [call.Leg],
                        Filters = call.Filter.Length > 0 ? [call.Filter] : [],
                        LocalCopy = workspace.Worktree is null,
                        Watch = kept,
                        Isolation = legIsolation,
                    },
                    RunId.New(),
                    cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        catch (HarnessException refused)
        {
            return Unmeasured(refused.Message);
        }

        if (ran.Result is not { } result)
        {
            return Unmeasured(ran.Refusal!);
        }

        var line = result.Report.Lines.FirstOrDefault(one => one.Leg == call.Leg);

        if (FlowStepLegs.EndedOutsideItsLegs(result, [call.Leg]) || line is null)
        {
            return Unmeasured(result.Message) with { RunDirectory = result.RunDirectory };
        }

        var verdict = Verdicts.Display(line.Verdict);
        var detail = line.LogTail is { Count: > 0 } tail ? $"{line.Detail}\n{string.Join("\n", tail)}" : line.Detail;

        return (FlowLegVerdicts.ClassOf(line.Verdict) switch
        {
            FlowLegClass.Red => new AgentMeasureAnswer(Measured: true, Passed: false, verdict, detail, Reason: null),
            FlowLegClass.None when line.Verdict == LegVerdict.Passed => new AgentMeasureAnswer(Measured: true, Passed: true, verdict, detail, Reason: null),
            _ => new AgentMeasureAnswer(
                Measured: false,
                Passed: false,
                verdict,
                detail,
                $"the leg '{call.Leg}' came to {verdict}, which measures nothing of the cell{(line.Detail is { Length: > 0 } said ? $": {said}" : string.Empty)}"),
        }) with { RunDirectory = result.RunDirectory };

        static AgentMeasureAnswer Unmeasured(string why) => new(Measured: false, Passed: false, Verdict: string.Empty, Detail: null, Reason: why);
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

        // The steps before it that left notes: each step of the work its conversation goes on with whose directory holds
        // them, named by step and never by path, for the next attempt has their notes in its continuation record.
        var before = run.GoesOnWith
            .Where(source => fileSystem.KindOf(Path.Combine(DirectoryOf(layout, source), OrchestratorLayout.StepNotesFileName)) == PathKind.File)
            .Select(source => new AgentContinuedStep(source.Instance, "done", string.Empty))
            .ToList();

        // Its last messages word for word, as many as the compaction keeps of its last iterations: every message is in
        // the continuation record whole, which the account is a part of.
        return AgentStreamAccount.Of(stream, before, AgentCompactionBounds.LastIterations);
    }

    /// <summary>
    /// The account of the attempt the step's record says it was on (<see cref="FlowStepRun.Attempt"/>), where a crash cut
    /// it off before the engine read its answer: that attempt's events - every run of it the stream holds, to the end the
    /// crash left (<see cref="AgentAttempt.Of"/>) - replayed through the engine's own count of what a run spends
    /// (<see cref="AgentRunSpend"/>), which prices each turn as it is reported, copies the price and tracks what served
    /// it. Every turn is handed back with its spend: the engine charges only those past what its record holds charged.
    /// Null where the step made no agent, or its stream holds no run of that attempt - a crash before the attempt's first
    /// event, with nothing more spent than its record holds; the account says why where the stream cannot be read whole,
    /// or priced, for then what it spent past its record is not known.
    /// </summary>
    /// <param name="run">The step, the instance and its scope, as it was last asked.</param>
    /// <param name="cancellationToken">Cancels the reading of the repository.</param>
    async Task<FlowCutAttempt?> IFlowHandoffRunner.AttemptOf(FlowStepRun run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);

        var agent = run.WorksIn ?? run.Made?.Name;

        if (agent is null)
        {
            return null;
        }

        var (place, context) = await FlowStepRunners.PlaceOf(run, contextLoader, platform, cancellationToken).ConfigureAwait(false);
        var layout = OrchestratorLayout.Of(new HarnessLayout(place.Repository, place.Repository), place.Run);
        IReadOnlyList<AgentEvent> stream;

        try
        {
            stream = StreamOf(layout, agent, run.Instance);
        }
        catch (HarnessException unread)
        {
            return FlowCutAttempt.NotRead($"its stream does not read whole, so what it spent past its record is not known: {unread.Message.TrimEnd('.')}");
        }

        var window = AgentAttempt.Of(stream, run.Attempt);

        if (window is not [RunStarted { Attempt: var attempt }, ..] || attempt != run.Attempt)
        {
            return null;
        }

        if (AgentProviders.Resolve(context.Config) is not { Providers: { } providers })
        {
            return FlowCutAttempt.NotRead("the configuration's agent providers do not resolve, so what it spent past its record cannot be priced");
        }

        var spend = new AgentRunSpend(providers, serving: null);

        foreach (var happened in window)
        {
            _ = spend.EmitAsync(happened);
        }

        return new FlowCutAttempt(spend.Account(), spend.Turns, window[^1].At);
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
        var output = result.Status == AgentRunStatus.Completed && result.StructuredOutput is { Length: > 0 } json ? JsonDepth.ParseNode(json) : null;

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
    /// What an agent that only reads is told as its attempt goes on after an answer in a tree brought there again - another
    /// step worked as the agent while the attempt waited, or nothing told who the tree was last brought for: where the tree
    /// now stands, and that what the agent wrote there is gone.
    /// </summary>
    /// <param name="named">The commit the step's <c>commit</c> names, as the engine filled it; null where it names none, and the tree stands on the main checkout's HEAD.</param>
    /// <param name="commit">The commit the tree was brought to, exactly as committed.</param>
    public static string TreeBroughtAgain(string? named, string commit)
        => $"While this step waited for its answer, your tree was brought to {(named is null ? "the main checkout's HEAD" : "the commit your step names")} "
            + $"({commit}) again, for another step may have worked in it meanwhile: whatever you wrote there is gone, and the tree holds exactly what that commit holds.";

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
    /// The output the step's answer is held to, where it names one: its schema file read whole - the definitions its
    /// references share with the schema files beside it taken in (<see cref="Schemas.JsonSchemaSubset.Shared"/>), so
    /// the schema the client is sent and the schema the answer is held to are one and hold - the step's own name the
    /// name its output is read by, and - where the step may ask - the rounds it may ask in one attempt, less what its
    /// answers already spent: after the last the agent is offered no questions and told so; what a step of the flow
    /// that reads its answer holds it to beyond the schema (<see cref="FlowStepRun.AnswerHolds"/>); and what the checks that
    /// read its answer find wrong with it, for the agent to put right in its own conversation
    /// (<see cref="FlowStepRun.AnswerCorrections"/>). A schema no answer could be held to is refused (13) naming why: the
    /// definition's check said its problems when the run began, and this says where they are again.
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
                SchemaWholeOf(files, layout, workflow, step, named),
                MaxRepairs: 2,
                AskRounds: step.Ask == true ? Math.Max(0, (step.AskRounds ?? 2) - run.Answers.Count(given => given.Kind == "ask" && given.Attempt == run.Attempt)) : null)
            {
                Holds = run.AnswerHolds,
                Corrections = run.AnswerCorrections,
            }
            : null;

    /// <summary>
    /// The step's output schema read whole, by the one reading of a schema's text
    /// (<see cref="Schemas.JsonSchemaSubset.Whole"/>): where its references name definitions the schema files beside it
    /// share, they are taken in - so the schema the client is sent and the schema the answer is held to are one, and
    /// hold - and where nothing is taken in, the schema stands as its file wrote it, judged by the check every client is
    /// held to before it is taken (<see cref="AgentOutput.SchemaProblems"/>), not here. Text that is no schema at all, and
    /// a whole made here that the subset does not read, are refused (13) naming why: the definition's check said it when
    /// the run began, and this says where it is again.
    /// </summary>
    /// <param name="files">Reads the files the workflow names, beside the schema among them.</param>
    /// <param name="layout">The repository's layout.</param>
    /// <param name="workflow">The workflow's name.</param>
    /// <param name="step">The step the schema is the output of.</param>
    /// <param name="named">The schema, as the step names it.</param>
    private static string SchemaWholeOf(WorkflowNamedFiles files, HarnessLayout layout, string workflow, WorkflowStep step, string named)
    {
        var text = files.ReadText(layout, workflow, named, "a schema");
        var read = Schemas.JsonSchemaSubset.Whole(
            text,
            beside => WorkflowFiles.RootOf(files.ReadTextIfThere(layout, workflow, WorkflowFiles.Beside(named, beside), "a schema beside it")));

        if (read.Schema is not null && !read.TookIn)
        {
            return text;
        }

        return read.Schema is not { } whole || read.Problems.Count > 0
            ? throw new HarnessException(HarnessExit.ConfigInvalid, $"'{named}', the output schema of step '{step.Name}', is not a schema the engine reads: {string.Join("; ", read.Problems)}")
            : whole.ToJsonString(AgentEnvelope.Written);
    }

    /// <summary>
    /// The web search the step's agent is given, where its configuration names sources and the slot answers their keys;
    /// null where the agent searches none.
    /// </summary>
    /// <param name="context">The repository's context, whose configuration names the sources.</param>
    /// <param name="providers">The configuration's providers and tiers, resolved, whose appointed names the slot holds.</param>
    /// <param name="sources">The configuration's web search, already read and held to V55's names by the caller.</param>
    /// <param name="step">The step at the root of the step's chain of work (<see cref="ResumeChain.Root"/>), whose <c>search</c> picks its sources.</param>
    /// <param name="cancellationToken">Stops the reading of keys.</param>
    private async Task<(AgentWebSearch? Search, Runners.ActionValues? Mask, Ci.GhLogins? Logins)> WebSearchOf(HarnessContext context, AgentProviders providers, Web.Search.WebSearchSources sources, WorkflowStep step, CancellationToken cancellationToken)
    {
        if (sources.Chosen(step.Search) is not [_, ..] chosen)
        {
            return (null, null, null);
        }

        // The machine's gh logins as the session holds them, read once in it: the keys are read of them, and the step's
        // search renews by them.
        var reader = new Web.Search.WebSearchKeys(keys, gh, clock);
        var logins = reader.Logins();
        var read = await reader.ReadAsync(context.Layout, providers.Appointed(), chosen, cancellationToken, logins).ConfigureAwait(false);

        return (AgentWebSearch.Of(sources, step.Search, read), Web.Search.WebSearchKeys.Mask(chosen, read), logins);
    }

    /// <summary>
    /// The step's policy kept in its records (<see cref="OrchestratorLayout.PolicyFileName"/>), as the configuration stood
    /// when the step started under it: the ground's restrictions from every source at once, each with where it stands,
    /// and whether the worktree rules and the protected paths are on; the web search's sources and its results per
    /// source; and what it runs on, each entry of its order with the key's name alone - never a key's value.
    /// </summary>
    /// <param name="fileSystem">The file system the records are written with.</param>
    /// <param name="layout">The run's layout.</param>
    /// <param name="instance">The step's instance.</param>
    /// <param name="agent">The agent the step works as.</param>
    /// <param name="ground">The ground the step's agent is decided against.</param>
    /// <param name="webSearch">The web search the step's agent is given; null where it searches none.</param>
    /// <param name="order">The running order the step starts on.</param>
    private static void KeptPolicy(IFileSystem fileSystem, OrchestratorLayout layout, string instance, string agent, Agents.Policy.AgentPolicyGround ground, AgentWebSearch? webSearch, IReadOnlyList<AgentRunEntry> order)
    {
        var policy = new JsonObject
        {
            ["agentPolicy"] = new JsonObject
            {
                ["restrictions"] = new JsonArray([.. ground.Restrictions.Select(one => (JsonNode?)$"{one.Text} ({one.Source.ToString().ToLowerInvariant()})")]),
                ["worktreeRules"] = ground.WorktreeRules,
                ["protectedPaths"] = ground.ProtectedPaths,
            },
            ["agentWebSearch"] = webSearch is null ? null : new JsonObject
            {
                ["sources"] = new JsonArray([.. webSearch.Sources.Select(source => (JsonNode?)source.Name)]),
                ["maxPerStep"] = webSearch.MaxPerStep,
            },
            ["runsOn"] = new JsonArray([.. order.Select(entry => (JsonNode?)new JsonObject
            {
                ["provider"] = entry.Provider,
                ["model"] = entry.Model,
                ["client"] = entry.Client,
                ["key"] = entry.Key,
                ["effort"] = entry.Effort,
            })]),
        };

        fileSystem.WriteAllTextAtomic(Path.Combine(StepDirectoryOf(layout, instance, agent), OrchestratorLayout.PolicyFileName), policy.ToJsonString(JsonStateFile.Options));
    }

    /// <summary>
    /// The continuation record of the work the step's conversation goes on with (<see cref="FlowStepRun.GoesOnWith"/>),
    /// where it goes on with any: that work's instances rendered whole, oldest first - each as its own record says it
    /// ended, the newest's last iterations word for word - the paths this agent's folds wrote and its worktree holds
    /// unfolded, and the red legs of its newest red build or test, their copies put in what the step's agent is handed.
    /// Null where the step begins its own work, whatever steps it depends on.
    /// </summary>
    /// <param name="run">The step, with the work its conversation goes on with.</param>
    /// <param name="layout">The run's own paths.</param>
    /// <param name="agent">The agent the step works as.</param>
    /// <param name="handed">Where the step's agent is handed what it reads, the copies put under it.</param>
    /// <param name="context">The repository's context.</param>
    /// <param name="cancellationToken">Stops the reading of the worktree.</param>
    private async Task<string?> ContinuationOfAsync(FlowStepRun run, OrchestratorLayout layout, string agent, string handed, HarnessContext context, CancellationToken cancellationToken)
    {
        if (run.GoesOnWith is not [_, ..] worked)
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

        return await new AgentContinuation(fileSystem, harnessOutput).RenderAsync(
            work,
            handed,
            StepDirectoryOf(layout, run.Instance, agent),
            cancellationToken).ConfigureAwait(false);
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
    /// <param name="handed">Where the step's agent is handed what it reads, the copies put under it.</param>
    /// <param name="context">The repository's context.</param>
    /// <param name="cancellationToken">Stops the reading of the worktree.</param>
    private async Task<string?> OwnContinuationOfAsync(OrchestratorLayout layout, string agent, FlowStepRun run, string handed, HarnessContext context, CancellationToken cancellationToken)
    {
        var work = new AgentContinuationWork(
            run.Instance,
            [new AgentContinuedStep(run.Instance, "done", StepDirectoryOf(layout, run.Instance, agent))],
            await ChangedPathsOfAsync(layout, agent, context, cancellationToken).ConfigureAwait(false),
            RedLegsOf(layout, agent),
            CutOff: false);

        return await new AgentContinuation(fileSystem, harnessOutput).RenderAsync(
            work,
            handed,
            StepDirectoryOf(layout, run.Instance, agent),
            cancellationToken).ConfigureAwait(false);
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
    /// the agent would weigh, by the fold's own rule - an agent that only reads having changed none: nothing of its tree
    /// is ever folded, and what it wrote there is discarded as each of its steps starts.
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
        if (store.ReadAgent(layout, agent) is not { Reads: false } record || record.WorktreePath(context.Layout) is not { } worktree)
        {
            return folded;
        }

        var held = await new AgentFold(gitClient, fileSystem, FilePermissionsFactory.Create(), platform)
            .WeighedAsync(worktree, store.ReadSeed(layout, agent), TreeFloor.OfAgentMoves(context.Config.Worktrees.Root), cancellationToken)
            .ConfigureAwait(false);

        return [.. folded.Concat(held).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
    }

    /// <summary>
    /// The red legs of the agent's newest build or test that was red, as the record says them: each red leg, its phase,
    /// the ledger's line for it, what the phase printed digested - so the step that goes on starts from the first error -
    /// the raw lines the entry carries beside it, and the log the phase wrote as the entry names it, which the record
    /// copies. Empty where none was red.
    /// </summary>
    /// <param name="layout">The run's own paths.</param>
    /// <param name="agent">The agent the step works as, whose steps ran them.</param>
    private IReadOnlyList<AgentRedLeg> RedLegsOf(OrchestratorLayout layout, string agent)
    {
        if (store.ReadFlow(layout)?.Steps is not [_, ..] steps)
        {
            return [];
        }

        // A step whose output the engine wrote of the legs it ran, never one a model answered: the log a red entry names is
        // copied for the record, and a path a model wrote there would hand the agent a file its policy never let it read.
        foreach (var step in steps
            .Where(step => string.Equals(step.Agent, agent, StringComparison.Ordinal)
                && step.FinishedAt is not null
                && step.Type is { } type && WorkflowStepTypes.Find(type) is { AnswersLegs: true }
                && step.Output?["red"] is JsonArray { Count: > 0 })
            .OrderByDescending(step => step.FinishedAt))
        {
            var legs = new List<AgentRedLeg>();

            foreach (var red in step.Output!["red"]!.AsArray().OfType<JsonObject>())
            {
                if (red["leg"] is JsonValue leg && leg.GetValueKind() == JsonValueKind.String
                    && red["detail"] is JsonValue detail && detail.GetValueKind() == JsonValueKind.String)
                {
                    var phase = red["phase"] is JsonValue named && named.GetValueKind() == JsonValueKind.String ? named.GetValue<string>() : null;

                    // What a red result printed, typed and said for a model, so the step that goes on starts from the
                    // first error - written as every JSON an agent reads is (AgentEnvelope.Written), so what the tool
                    // printed reads as it printed it; the raw lines beside it, so a leg no parser read is never said by
                    // its line alone.
                    var digest = red["digest"] is { } typed && typed.GetValueKind() == JsonValueKind.Object ? typed.ToJsonString(AgentEnvelope.Written) : null;
                    IReadOnlyList<string> tail = red["tail"] is JsonArray lines
                        ? [.. lines.OfType<JsonValue>().Where(line => line.GetValueKind() == JsonValueKind.String).Select(line => line.GetValue<string>())]
                        : [];

                    // Its log, whose lines the digest's ranges number, as the entry names it - the copy the step kept beside
                    // its ledger in an agent's worktree; the record copies it, so it points into no run's records.
                    var log = red["log"] is JsonValue file && file.GetValueKind() == JsonValueKind.String ? file.GetValue<string>() : null;

                    legs.Add(new AgentRedLeg(leg.GetValue<string>(), phase ?? string.Empty, detail.GetValue<string>(), digest, tail, log));
                }
            }

            if (legs is [_, ..])
            {
                return legs;
            }
        }

        return [];
    }

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
    /// The codebase parts the conversation the step's own stream last began holds, in order - the pack its first message
    /// was given, then each delta a launch that went on with it was told - read from the stream's first messages as they
    /// were sent; empty where it was given none. Every launch that begins a conversation of its own begins what a
    /// conversation holds of the codebase afresh, whether or not its first message holds a pack - a pack that held
    /// nothing, a graph not served, a compaction's record - so nothing an earlier conversation was told counts.
    /// </summary>
    /// <remarks>
    /// A first message goes on with the conversation only where the launch it begins said so: a session that was handed the
    /// stored conversation, started since the first message before it. The API clients say their session before their
    /// first message, and Claude Code, whose every launch begins a conversation of its own, after it.
    /// </remarks>
    /// <param name="stream">The step's own events, every attempt's.</param>
    private static IReadOnlyList<string> CodebaseToldOf(IReadOnlyList<AgentEvent> stream)
    {
        var told = new List<string>();
        var handed = false;

        foreach (var happened in stream)
        {
            switch (happened)
            {
                case SessionStarted started:
                    handed = started.HandedConversation;
                    break;

                case MessageSent { Which: AgentMessageKind.FirstPrompt } sent when handed:
                    if (AgentPromptParts.PartOf(sent.Text, AgentPromptParts.CodebaseChangesTag) is { Length: > 0 } changes)
                    {
                        told.Add(changes);
                    }

                    handed = false;
                    break;

                case MessageSent { Which: AgentMessageKind.FirstPrompt } sent:
                    told.Clear();

                    if (AgentPromptParts.PartOf(sent.Text, AgentPromptParts.CodebaseTag) is { Length: > 0 } pack)
                    {
                        told.Add(pack);
                    }

                    break;
            }
        }

        return told;
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

        return window.Count == 0 ? null : AgentCompaction.Reduce(window, AgentCompactionBounds.Of(context.Config.AgentContext?.Compaction));
    }

    /// <summary>
    /// The prompt with each skill it names handed over, from the pieces the engine filled around them - its own text
    /// joined as the engine filled it, never read as a template again, so a value's braces stay as the value holds
    /// them: every <c>{skill.&lt;name&gt;}</c> read again where the step starts - its name by the one rule of a skill's
    /// name, the first place that has the folder deciding, one that is there and cannot be used refused, never fallen
    /// past - the folder copied whole into <c>skills/&lt;name&gt;/</c> of what the step's agent is handed, in place of an earlier copy, and the
    /// block put where the placeholder stood, its tools held to the capabilities the agent's own ground gives it and
    /// its <c>${CLAUDE_SESSION_ID}</c> the session the run begins as. Every skill that could not be handed is the
    /// prompt's refusal, said whole; the skills handed are what the step's stream records.
    /// </summary>
    /// <param name="pieces">The prompt as the engine filled it around its skills.</param>
    /// <param name="run">The step, whose instance names the skills.</param>
    /// <param name="agent">The agent a refusal names: the definition the step works by, else the agent it works as.</param>
    /// <param name="capabilities">The capabilities the agent's own ground gives it, its tools held to them.</param>
    /// <param name="workspace">Where the run works.</param>
    /// <param name="handed">Where the step's agent is handed what it reads, the skills copied under it.</param>
    /// <param name="resolution">The step's resolution, whose effort the blocks are given.</param>
    /// <param name="session">The id the run's first session begins as.</param>
    private HandedSkills SkillBlocks(
        IReadOnlyList<FlowTemplate.Piece> pieces,
        FlowStepRun run,
        string agent,
        IReadOnlyList<AgentCapability> capabilities,
        AgentWorkspace workspace,
        string handed,
        AgentResolution resolution,
        string session)
    {
        var skills = new AgentSkills(fileSystem, platform, workspace.MainCheckout);
        var may = capabilities.Select(one => one.ToString().ToLowerInvariant()).ToList();
        var filled = new StringBuilder();
        var given = new List<AgentSkill>();

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

            var directory = skills.Copy(found, handed);

            if (found.Block(directory, workspace.Worktree ?? workspace.MainCheckout, session, resolution.Serving.Effort ?? string.Empty, out var unread) is not { } block)
            {
                return HandedSkills.RefusedFor(unread ?? $"skill '{name}' ('{found.File}') cannot be used");
            }

            filled.Append(block);
            given.Add(new AgentSkill(found.Name, found.From, found.File, found.Digest!, found.FolderDigest!, directory));
        }

        return new HandedSkills(filled.ToString(), given, Refused: null);
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

    /// <summary>
    /// The account of the attempt the step's record says it was on, where a crash cut it off before the engine read its
    /// answer: what served it and what it called, and every turn of its stored stream with its spend, counted at the
    /// configuration's prices as they stand now, and when its last event was written (<see cref="FlowCutAttempt"/>).
    /// Null where its stream holds no run of that attempt; why, where the stream cannot be read whole or priced.
    /// </summary>
    /// <param name="run">The step, the instance and its scope, as it was last asked.</param>
    /// <param name="cancellationToken">Cancels the reading of the repository.</param>
    Task<FlowCutAttempt?> AttemptOf(FlowStepRun run, CancellationToken cancellationToken);
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

    /// <summary>
    /// The account of the attempt the step's record says it was on, where a crash cut it off before the engine read its
    /// answer (<see cref="IFlowHandoffRunner.AttemptOf"/>); null where the runner's type keeps no stream.
    /// </summary>
    /// <param name="run">The step, the instance and its scope, as it was last asked.</param>
    /// <param name="cancellationToken">Cancels the reading of the repository.</param>
    Task<FlowCutAttempt?> AttemptOf(FlowStepRun run, CancellationToken cancellationToken);
}
