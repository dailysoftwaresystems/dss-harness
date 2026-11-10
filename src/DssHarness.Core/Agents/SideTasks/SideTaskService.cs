using System.Globalization;
using DssHarness.Core.Agents.ClaudeCode;
using DssHarness.Core.Agents.Policy;
using DssHarness.Core.Configuration;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Git;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Orchestration.Flow;
using DssHarness.Core.Platform;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;
using DssHarness.Core.Secrets;
using DssHarness.Core.Web.Search;

using DssHarness.Core.Execution;

namespace DssHarness.Core.Agents.SideTasks;

/// <summary>Runs and lists the side-tasks of a repository: the two commands' use case.</summary>
public interface ISideTaskService : DssHarness.Core.Application.IUseCase
{
    /// <summary>Begins a side-task - a chat in the terminal, or one message with <c>--prompt</c> - or opens one again.</summary>
    /// <param name="call">What the command line said and how its chat is driven.</param>
    /// <param name="cancellationToken">Stops the command.</param>
    /// <returns>How the side-task ended, its records named on every exit.</returns>
    Task<CommandOutcome> RunAsync(SideTaskCall call, CancellationToken cancellationToken);

    /// <summary>Lists the repository's side-tasks, newest first.</summary>
    /// <param name="startDirectory">The directory the command was typed in.</param>
    /// <param name="json">Whether the answer is a document.</param>
    /// <param name="cancellationToken">Stops the command.</param>
    Task<CommandOutcome> ListAsync(string startDirectory, bool json, CancellationToken cancellationToken);
}

/// <summary>
/// One side-task asked of the tool: what the command line said, how its chat is driven, and the count of its interrupts.
/// A chat is driven by a front end that reads the person's lines; a one-shot names none.
/// </summary>
/// <param name="StartDirectory">The directory the command was typed in.</param>
/// <param name="Prompt">The one message of a one-shot side-task; null for a chat.</param>
/// <param name="PromptFile">A file holding the one message of a one-shot side-task, read relative to the start directory, in the place of the prompt.</param>
/// <param name="Resume">The side-task to open again; null to begin one.</param>
/// <param name="Name">The side-task's own name, where the person gives one.</param>
/// <param name="Model">The model to run on, by its code.</param>
/// <param name="Tier">The tier whose first model runs.</param>
/// <param name="Client">The client to reach the model through.</param>
/// <param name="Efforts">The effort levels asked: one level, or <c>code=level</c> entries with <c>all=level</c> for the rest.</param>
/// <param name="Json">Whether the one-shot answers with a document.</param>
/// <param name="Chat">The chat's front end; null for a one-shot.</param>
public sealed record SideTaskCall(
    string StartDirectory,
    string? Prompt = null,
    string? PromptFile = null,
    string? Resume = null,
    string? Name = null,
    string? Model = null,
    string? Tier = null,
    string? Client = null,
    IReadOnlyList<string>? Efforts = null,
    bool Json = false,
    SideTaskChat? Chat = null)
{
    /// <summary>
    /// The side-task's interrupts, whoever sends them: the terminal's (<see cref="SideTaskTerminal"/>), or a front end's
    /// own. The first cuts the agent's turn, a second ends a chat.
    /// </summary>
    public SideTaskInterrupts Interrupts { get; init; } = new();

    /// <summary>
    /// Whether the side-task is given no codebase graph - no tool, no repository map, no context pack, nothing synced -
    /// for one side-task, where the person turned it off with <c>--no-graph</c>.
    /// </summary>
    public bool NoGraph { get; init; }
}

/// <summary>
/// What a side-task runs on, as the service set it up from what the person asked: the model resolved, the running order
/// and the key it starts on, the Claude Code made ready where the order holds it, and the ground its agent works under.
/// Set up at the start, and again by the chat's <c>/model</c> and <c>/effort</c>.
/// </summary>
/// <param name="Resolution">The model resolved, as a step's is.</param>
/// <param name="Order">The running order the turns walk, as a step's.</param>
/// <param name="Program">The Claude Code made ready, where the order holds that client.</param>
/// <param name="ProgramVersion">Its version, where it was made ready.</param>
/// <param name="Ground">What the agent works under.</param>
/// <param name="RunsOn">What the person asked, which a <c>--resume</c> goes on with.</param>
/// <param name="Isolation">The operating system's isolation the agent's commands run under, or why none.</param>
internal sealed record SideTaskSetUp(
    AgentResolution Resolution,
    IReadOnlyList<AgentRunEntry> Order,
    string? Program,
    string? ProgramVersion,
    AgentPolicyGround Ground,
    SideTaskRunsOn RunsOn,
    Isolation.LaunchIsolation Isolation);

/// <summary>
/// Runs and lists the side-tasks of a repository: an agent outside every workflow, a conversation between a person and one
/// agent over the main tree, with no step and no limit of a run's - every action still decided by the engine before it
/// runs and recorded, the built-in refusals kept. No second loop: each turn is an <see cref="AgentStepRequest"/> handed to
/// <see cref="IAgentRunner"/> as the engine hands it one, so the clients, the tool runtime, the stored stream, compaction
/// and the fallback are the ones every step uses.
/// </summary>
/// <param name="contextLoader">Finds the repository and its configuration.</param>
/// <param name="runner">Works each turn, whatever the client is.</param>
/// <param name="clients">The clients registered, whose kind tells the chat how it goes on between messages.</param>
/// <param name="platform">This machine.</param>
/// <param name="identity">This process, and how another's liveness is told.</param>
/// <param name="store">Reads and writes the side-tasks' owners, strictly, and guards the repository's key-spend record.</param>
/// <param name="keys">The slot the entry's keys are read from.</param>
/// <param name="webKeys">Each web-search source's key, read as a step's is.</param>
/// <param name="updates">Makes the Claude Code a side-task needs ready, where its order holds the client that starts one.</param>
/// <param name="fileSystem">The file system the side-task's records are kept on.</param>
/// <param name="output">Says what each side-task's turns and refusals say.</param>
/// <param name="git">Asks git whether the side-tasks' directory is ignored before anything is written there.</param>
/// <param name="ancestry">Tells, by the process tree, whether a process of this tool's started this one - the engine an agent's child runs under.</param>
/// <param name="isolation">Decides the operating system's isolation the side-task's agent runs under.</param>
/// <param name="graph">The one graph service every codebase graph is read and written by, the side-task's own among them.</param>
/// <param name="clock">Tells when each turn began and what the balancing of keys reads.</param>
/// <param name="environment">Reads this process's environment, where the mark of an agent's child is looked for.</param>
public sealed class SideTaskService(
    IHarnessContextLoader contextLoader,
    IAgentRunner runner,
    IEnumerable<IAgentClient> clients,
    IHostPlatform platform,
    IProcessIdentity identity,
    OrchestrationStore store,
    AgentKeyStore keys,
    WebSearchKeys webKeys,
    ClaudeCodeUpdateService? updates,
    IFileSystem fileSystem,
    Output.IHarnessOutput output,
    IGitClient git,
    AgentAncestry ancestry,
    Isolation.IAgentIsolation isolation,
    Codebase.ICodebaseGraphService graph,
    TimeProvider? clock = null,
    Func<string, string?>? environment = null) : ISideTaskService
{
    /// <summary>The command that begins a side-task.</summary>
    public const string RunCommand = "run-side-task";

    /// <summary>The command that lists them.</summary>
    public const string ListCommand = "list-side-tasks";

    /// <summary>
    /// Why no agent starts a side-task, as every refusal of it says: the agent's policy, and the command itself where it
    /// is started under an agent - told by the agent's mark (<see cref="AgentChildEnvironment.AgentMarker"/>) and by the
    /// process tree (<see cref="AgentAncestry"/>).
    /// </summary>
    public const string NoAgentStartsOne = "a side-task is a person's own conversation in the person's terminal, with no limit of a run's, and no agent starts one";

    /// <summary>How many words of the first message name a side-task the person did not name.</summary>
    public const int NameWords = 5;

    /// <summary>The longest a derived name's words take.</summary>
    public const int MostNameCharacters = 40;

    /// <summary>The longest a side-task's name is.</summary>
    public const int MostNameLength = 80;

    /// <summary>What a failed agent is asked again before the side-task gives the turn up, as a workflow's own default.</summary>
    public const int DefaultRetries = 3;

    /// <summary>How long a failed agent's first retry waits, in seconds, as a workflow's own default.</summary>
    public const int DefaultBackoffSeconds = 30;

    /// <summary>
    /// The system prompt every side-task's agent runs under: no workflow, no step, no output envelope - a conversation
    /// with a person over the tree.
    /// </summary>
    public const string AgentSystemPrompt =
        "You are the agent of a DssHarness side-task: a conversation with one person over this repository's main tree. "
        + "Answer the person directly, and read, change and run what the task needs, as the person's own session could. "
        + "There is no workflow, no step and no output shape: your answer is text for the person. "
        + "Every action you ask for is decided and recorded by DssHarness.";

    /// <summary>The rule a side-task's name follows, as a refusal says it.</summary>
    public const string NameRule = "letters, digits, '-' and '_' only, at most 80 characters, beginning with a letter or a digit";

    /// <summary>The line every exit before the side-task was claimed says of its records: none were written.</summary>
    public const string NoRecords = "Records: none were written";

    private readonly IHarnessContextLoader _contextLoader = contextLoader ?? throw new ArgumentNullException(nameof(contextLoader));
    private readonly IAgentRunner _runner = runner ?? throw new ArgumentNullException(nameof(runner));
    private readonly IReadOnlyList<IAgentClient> _clients = [.. (clients ?? throw new ArgumentNullException(nameof(clients)))];
    private readonly IProcessIdentity _identity = identity ?? throw new ArgumentNullException(nameof(identity));
    private readonly OrchestrationStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly WebSearchKeys _webKeys = webKeys ?? throw new ArgumentNullException(nameof(webKeys));
    private readonly IFileSystem _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));

    // What a side-task is launched with beyond its model - its order, the Claude Code directories its ground keeps it
    // from, the mask, the program - built as a step's launch builds them, by the one builder of each.
    private readonly ReasonLaunch _launch = new(
        fileSystem ?? throw new ArgumentNullException(nameof(fileSystem)),
        platform ?? throw new ArgumentNullException(nameof(platform)),
        keys ?? throw new ArgumentNullException(nameof(keys)),
        updates,
        isolation ?? throw new ArgumentNullException(nameof(isolation)));
    private readonly Output.IHarnessOutput _output = output ?? throw new ArgumentNullException(nameof(output));
    private readonly IGitClient _git = git ?? throw new ArgumentNullException(nameof(git));
    private readonly AgentAncestry _ancestry = ancestry ?? throw new ArgumentNullException(nameof(ancestry));
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Func<string, string?> _environment = environment ?? Environment.GetEnvironmentVariable;

    /// <inheritdoc/>
    public async Task<CommandOutcome> RunAsync(SideTaskCall call, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(call);

        // The first line of the rule every agent's policy holds as well: a side-task started under an agent - by this
        // verb or by any other name of the program - refuses itself. The mark is read by its presence, whatever it holds:
        // a child that cleared its value is still an agent's.
        if (_environment(AgentChildEnvironment.AgentMarker) is not null)
        {
            return Unwritten(CommandOutcome.Refused($"'{RunCommand}' is refused: {NoAgentStartsOne}. Nothing was started."));
        }

        // The second, which no form of taking the mark away reaches: a process of this tool's among the ones that started
        // this one is the engine an agent's child runs under. A tree that could not be read is said and refused, never
        // read as one with no agent in it; a process detached from the agent's tree is told by the mark alone.
        var tree = await _ancestry.ReadAsync(cancellationToken).ConfigureAwait(false);

        if (tree.Unread is { } unread)
        {
            return Unwritten(CommandOutcome.Refused($"'{RunCommand}' is refused: {unread} so whether an agent started it could not be told, and {NoAgentStartsOne}. Nothing was started."));
        }

        if (tree.Tool is not null)
        {
            return Unwritten(CommandOutcome.Refused($"'{RunCommand}' is refused: {NoAgentStartsOne}. Nothing was started."));
        }

        // The side-task's own stop: the command's, and the person's interrupts where no turn takes them - a one shot's
        // first, a chat's second - wherever it stands: being set up or opened again, its Claude Code made ready.
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var stopping = call.Interrupts.StopsOn(stop, chat: call.Prompt is null && call.PromptFile is null);

        try
        {
            return await StartAsync(call, stop.Token, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            return Unwritten(CommandOutcome.Failed(HarnessExit.Cancelled, $"'{RunCommand}' was stopped before the side-task began. Nothing was started."));
        }
    }

    /// <summary>Begins the side-task <paramref name="call"/> asks for, or opens it again, until <paramref name="cancellationToken"/> stops it.</summary>
    /// <param name="call">What the command line said and how its chat is driven.</param>
    /// <param name="cancellationToken">The side-task's stop: the command's, and the interrupts where no turn takes them.</param>
    /// <param name="command">The command's own stop, which a launch held open for a chat runs under.</param>
    private async Task<CommandOutcome> StartAsync(SideTaskCall call, CancellationToken cancellationToken, CancellationToken command)
    {
        if (call.Resume is not null && call.Name is not null)
        {
            return Unwritten(CommandOutcome.Usage("--name with --resume is a usage error: a side-task opened again keeps the name it has. Give --resume alone to open it, or --name alone to begin a new one."));
        }

        var prompt = ReadPrompt(call);

        if (prompt is { Problem: { } problem })
        {
            return Unwritten(CommandOutcome.Usage(problem));
        }

        if (call.Json && prompt is null)
        {
            return Unwritten(CommandOutcome.Usage("--json without a prompt is a usage error: a chat is no document. Give --prompt or --prompt-file for one message."));
        }

        if (prompt is null && call.Chat is null)
        {
            return Unwritten(CommandOutcome.Usage("a side-task is a chat in the terminal or one message with --prompt; neither was given."));
        }

        var (effort, effortByModel, effortProblem) = SideTaskEffort.Parse(call.Efforts ?? []);

        if (effortProblem is { } refused)
        {
            return Unwritten(CommandOutcome.Usage(refused));
        }

        HarnessContext context;

        try
        {
            context = await _contextLoader.LoadAsync(call.StartDirectory, cancellationToken).ConfigureAwait(false);
        }
        catch (HarnessException ex)
        {
            return Unwritten(new CommandOutcome(ex.ExitCode, ex.Message));
        }

        if (AgentProviders.Resolve(context.Config) is not { Providers: { } providers })
        {
            return Unwritten(CommandOutcome.Failed(HarnessExit.ConfigInvalid, "the configuration's agent providers do not resolve; list-agent-providers --template prints what init writes into a new config"));
        }

        // The side-task's name: the one opened again, the person's, or the date and time it began - with the first words
        // of its one message in one shot; a chat is claimed before its first message is typed - a suffix freeing it
        // inside the claim where another began in the same second.
        var name = call.Resume ?? call.Name ?? Named(prompt?.Text, _clock.GetUtcNow().LocalDateTime);

        if (!IsName(name))
        {
            return Unwritten(CommandOutcome.Refused($"the side-task's name '{name}' is no name a side-task's directory takes: {NameRule}. Nothing was started."));
        }

        try
        {
            // Everything a side-task keeps is machine-local: a record, a stream, a transcript committed by `git add -A`
            // would travel to every clone. init keeps the directory in git and everything made in it out.
            if (await SlotIgnored.RefusalAsync(_git, context.Layout.MainCheckoutRoot, $"{HarnessLayout.SideTasksDirectoryName}/{name}/{SideTaskLayout.TaskFileName}", $"what side-task '{name}' keeps", cancellationToken).ConfigureAwait(false) is { } unignored)
            {
                return Unwritten(CommandOutcome.Refused($"{unignored} Nothing was started."));
            }
        }
        catch (HarnessException ex)
        {
            return Unwritten(new CommandOutcome(ex.ExitCode, ex.Message));
        }

        // What the side-task keeps going on its web search: every source turned on, each key read as a step's is; the mask
        // its records and its answers are held to, a step's - every secret the configuration appoints that the slot
        // answers, and every form each search key is sent in (ReasonLaunch.Mask); and the names the configuration
        // appoints withheld from the agent's children.
        var sources = WebSearchSources.Of(context.Config, WebSearchKinds.OfThisBuild);

        if (sources.Problems is [var wrong, ..])
        {
            return Unwritten(CommandOutcome.Failed(HarnessExit.ConfigInvalid, $"the configuration's web search does not resolve: {wrong}"));
        }

        var appointed = providers.Appointed();
        var read = await _webKeys.ReadAsync(context.Layout, appointed, [.. sources.On], cancellationToken).ConfigureAwait(false);
        var common = new SideTaskCommon(
            context,
            providers,
            sources.On is [_, ..] ? SideTaskPolicy.WebSearch(sources, read) : null,
            _launch.Mask(context.Layout, providers, WebSearchKeys.Mask(sources.On, read)),
            [.. appointed.Select(one => one.Name)]);
        var owner = new SideTaskOwner(_fileSystem, _store, _identity, _clock);
        var asked = new SideTaskRunsOn(call.Tier, call.Model, effort, effortByModel, call.Client);

        return call.Resume is { } again
            ? await AgainAsync(call, common, owner, SideTaskLayout.Of(context.Layout, again), asked, prompt?.Text, cancellationToken, command).ConfigureAwait(false)
            : await NewAsync(call, common, owner, name, derived: call.Name is null, asked, prompt?.Text, cancellationToken, command).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<CommandOutcome> ListAsync(string startDirectory, bool json, CancellationToken cancellationToken)
    {
        HarnessContext context;

        try
        {
            context = await _contextLoader.LoadAsync(startDirectory, cancellationToken).ConfigureAwait(false);
        }
        catch (HarnessException ex)
        {
            return new CommandOutcome(ex.ExitCode, ex.Message);
        }

        var root = context.Layout.SideTasksDirectory;
        var owner = new SideTaskOwner(_fileSystem, _store, _identity, _clock);

        try
        {
            var tasks = new SideTaskStore(_fileSystem).List(root)
                .Select(listed => (listed.Layout, listed.Record, Open: owner.Open(listed.Layout)))
                .ToList();

            return SideTaskReports.List(root, tasks, json);
        }
        catch (HarnessException ex)
        {
            return new CommandOutcome(ex.ExitCode, ex.Message, [$"Records: {root}"]);
        }
    }

    /// <summary>
    /// A new side-task: set up from what the person asked, then claimed with its first record written inside the claim,
    /// and worked; released however it ends.
    /// </summary>
    private async Task<CommandOutcome> NewAsync(SideTaskCall call, SideTaskCommon common, SideTaskOwner owner, string name, bool derived, SideTaskRunsOn asked, string? prompt, CancellationToken cancellationToken, CancellationToken command)
    {
        var (setUp, refused) = await SetUpAsync(common, asked, name, "--effort", cancellationToken).ConfigureAwait(false);

        if (setUp is null)
        {
            return Unwritten(refused!);
        }

        SideTaskLayout layout;
        SideTaskRecord record;

        try
        {
            string? reclaimed;
            (layout, record, reclaimed) = owner.ClaimNew(common.Context.Layout.SideTasksDirectory, name, derived, taken => new SideTaskRecord(taken, _clock.GetUtcNow(), RunsOn: asked));
            Said(reclaimed);
        }
        catch (HarnessException ex)
        {
            // The claim refused - the name taken, or held by another process - wrote nothing of this command's: the
            // records the name holds are the other side-task's, and are named as its.
            var other = SideTaskLayout.Of(common.Context.Layout, name);
            var refusal = Unwritten(new CommandOutcome(ex.ExitCode, ex.Message));

            return _fileSystem.KindOf(other.Directory) == PathKind.Directory
                ? refusal with { Details = [.. refusal.Details ?? [], $"Side-task '{name}' keeps its own records in {other.Directory}"] }
                : refusal;
        }

        return await WorkAsync(call, common, owner, layout, record, setUp, prompt, cancellationToken, command).ConfigureAwait(false);
    }

    /// <summary>
    /// A side-task opened again: claimed with its record read inside the claim, then set up from what it ran on as the
    /// person last set it - where the command line names no model and no tier - and worked; released however it ends.
    /// </summary>
    private async Task<CommandOutcome> AgainAsync(SideTaskCall call, SideTaskCommon common, SideTaskOwner owner, SideTaskLayout layout, SideTaskRunsOn asked, string? prompt, CancellationToken cancellationToken, CancellationToken command)
    {
        SideTaskRecord record;

        try
        {
            string? reclaimed;
            (record, reclaimed) = owner.ClaimAgain(layout);
            Said(reclaimed);
        }
        catch (HarnessException ex)
        {
            return Told(new CommandOutcome(ex.ExitCode, ex.Message), call.Json, layout);
        }

        (SideTaskSetUp? SetUp, CommandOutcome? Refused) setting;

        try
        {
            setting = await SetUpAsync(common, GoesOnWith(record.RunsOn, asked, call), layout.Name, "--effort", cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            owner.Release(layout);
            return Told(CommandOutcome.Failed(HarnessExit.Cancelled, $"side-task '{layout.Name}' was stopped before it was opened again. Nothing of it was changed; --resume {layout.Name} opens it again."), call.Json, layout);
        }
        catch
        {
            owner.Release(layout);
            throw;
        }

        if (setting.SetUp is not { } setUp)
        {
            owner.Release(layout);
            return Told(setting.Refused!, call.Json, layout);
        }

        return await WorkAsync(call, common, owner, layout, record, setUp, prompt, cancellationToken, command).ConfigureAwait(false);
    }

    /// <summary>
    /// What a side-task opened again runs on: what the command line names where it names a model or a tier - the tier's
    /// later models its order, as a step's - and what the side-task ran on as the person last set it where it names
    /// neither, the tier the record holds keeping its later models. An effort or a client the command line names stands
    /// over the record's; the record's is kept only with the record's own model or tier.
    /// </summary>
    private static SideTaskRunsOn GoesOnWith(SideTaskRunsOn? last, SideTaskRunsOn asked, SideTaskCall call)
    {
        if (last is null || call.Model is not null || call.Tier is not null)
        {
            return asked;
        }

        var effortAsked = call.Efforts is { Count: > 0 };

        return last with
        {
            Effort = effortAsked ? asked.Effort : last.Effort,
            EffortByModel = effortAsked ? asked.EffortByModel : last.EffortByModel,
            Client = asked.Client ?? last.Client,
        };
    }

    /// <summary>One side-task's work, its owner released however it ends.</summary>
    private async Task<CommandOutcome> WorkAsync(SideTaskCall call, SideTaskCommon common, SideTaskOwner owner, SideTaskLayout layout, SideTaskRecord record, SideTaskSetUp setUp, string? prompt, CancellationToken cancellationToken, CancellationToken command)
    {
        try
        {
            var spend = new AgentKeySpend(_fileSystem, _store, _clock);

            // The side-task's own graph, opened unless the person turned it off: synced whole - seeded from the newest
            // graph of the repository - before the first message is sent, its map made once from the base as it stands.
            // A graph that cannot be made takes nothing away from the conversation: the side-task goes on without one,
            // and says so once, with why - in its chat or its one shot's answer, and in its graph's record.
            string? withoutGraph = null;
            var own = new SideTaskGraph(
                graph,
                _fileSystem,
                platform,
                git,
                _store,
                _clock,
                common.Context.Layout,
                layout,
                common.Context.Config.Worktrees?.Root ?? WorktreeSettings.DefaultRoot);

            if (!call.NoGraph)
            {
                try
                {
                    await own.OpenAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is HarnessException or IOException or UnauthorizedAccessException
                    || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
                {
                    own = null;
                    withoutGraph = WithoutGraph(layout, ex);
                }
            }
            else
            {
                own = null;
            }

            var working = new Working(
                _runner,
                _clients,
                _fileSystem,
                _output,
                _clock,
                call,
                common,
                layout,
                owner,
                record,
                setUp,
                (runsOn, form, token) => SetUpAsync(common, runsOn, layout.Name, form, token),
                spend.RecordingIn(common.Context.Layout),
                own,
                withoutGraph);

            return prompt is null
                ? await working.ChatAsync(cancellationToken, command).ConfigureAwait(false)
                : await working.OneShotAsync(prompt, call.Json, cancellationToken).ConfigureAwait(false);
        }
        catch (HarnessException ex)
        {
            return Told(new CommandOutcome(ex.ExitCode, ex.Message), call.Json, layout);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Told(CommandOutcome.Failed(HarnessExit.Cancelled, $"side-task '{layout.Name}' was stopped where it stood; its records hold what it did, and --resume {layout.Name} opens it again."), call.Json, layout);
        }
        finally
        {
            // Every launch of the side-task shares the one temporary directory its records' directory names: taken away
            // once its work is over.
            setUp.Isolation.Dispose();
            owner.Release(layout);
        }
    }

    /// <summary>
    /// Sets a side-task up to run on what <paramref name="runsOn"/> asks: the model resolved as a step's, each model's
    /// effort checked by the one check of it (<see cref="AgentEffortRules"/>), the running order and the key it starts on,
    /// the Claude Code made ready where the order holds it, and the ground with every Claude Code directory the order uses.
    /// The one builder of all of it, at the start and for the chat's <c>/model</c> and <c>/effort</c>.
    /// </summary>
    /// <param name="common">What every set-up of the side-task shares.</param>
    /// <param name="runsOn">What the person asked.</param>
    /// <param name="task">The side-task's name, as Claude Code's readiness names what needs it.</param>
    /// <param name="form">How the person gives a level of a model's own, as a refusal's remedy says it: <c>--effort</c>, or the chat's <c>/effort</c>.</param>
    /// <param name="cancellationToken">Stops the setting up.</param>
    /// <returns>The set-up, or why there is none: a usage error (10), the configuration (12), an update of the program refused (13), the program missing or no isolation where it is required (14).</returns>
    private async Task<(SideTaskSetUp? SetUp, CommandOutcome? Refused)> SetUpAsync(SideTaskCommon common, SideTaskRunsOn runsOn, string task, string form, CancellationToken cancellationToken)
    {
        try
        {
            return await SetUpOrThrowAsync(common, runsOn, task, form, cancellationToken).ConfigureAwait(false);
        }
        catch (HarnessException ex)
        {
            return (null, new CommandOutcome(ex.ExitCode, ex.Message));
        }
    }

    /// <inheritdoc cref="SetUpAsync"/>
    private async Task<(SideTaskSetUp? SetUp, CommandOutcome? Refused)> SetUpOrThrowAsync(SideTaskCommon common, SideTaskRunsOn runsOn, string task, string form, CancellationToken cancellationToken)
    {
        var providers = common.Providers;
        var layout = common.Context.Layout;

        if (runsOn.Model is { } named && providers.Of(named) is null)
        {
            return (null, CommandOutcome.Failed(HarnessExit.ConfigInvalid, $"the side-task names model '{named}', which no provider of the configuration serves; list-agent-providers lists them"));
        }

        if (AgentResolution.Of(providers, runsOn.Tier, runsOn.Model, runsOn.Effort, runsOn.EffortByModel, runsOn.Client, definition: null) is not { } resolution)
        {
            return (null, CommandOutcome.Failed(HarnessExit.ConfigInvalid, "the side-task has no model to run on: name a tier or a model on it, or a tier under defaults.agentTier"));
        }

        // Each model the side-task may run on must take the level it is given, and every model an effort names must be
        // one it runs on, as a step's are checked.
        var tier = runsOn.Model is null ? runsOn.Tier ?? providers.DefaultTier : null;
        IReadOnlyList<(AgentProviderConfig Provider, AgentModelConfig Model)> models = runsOn.Model is { } model
            ? [providers.Of(model)!.Value]
            : tier is not null ? providers.ModelsOf(tier) : [];

        if (AgentEffortRules.Problems(
                models,
                tier,
                runsOn.Effort,
                runsOn.EffortByModel,
                definition: null,
                "the side-task",
                (code, nearest, _) => $"Give it a level of its own: {form} {code}={nearest}, with all=<level> for the rest") is [_, ..] problems)
        {
            return (null, CommandOutcome.Usage(string.Join("; ", problems)));
        }

        // The running order and the key it starts on, as a step's is; what it spends is counted against that key.
        var spent = common.Context.Config.BalanceKeyUsage == true ? new AgentKeySpend(_fileSystem, _store, _clock).Spent(layout) : null;
        var order = _launch.Order(providers, resolution, layout, spent);

        // Where the order holds the client that starts a program, it is made ready here - installed and brought up to
        // date where it must be - as a step's is: one not ready refuses the side-task with the finder's own words and
        // code - the configuration (12), an update refused (13), a program missing (14) - where the entry that would
        // serve first needs it, and where only a later entry does, the side-task runs by an order that passes it over.
        var program = await _launch.ProgramAsync(layout, providers, order, ClaudeCodeAsker.SideTask(task), change: true, cancellationToken).ConfigureAwait(false);

        if (program.Refusal is { } notReady)
        {
            return (null, new CommandOutcome(notReady.ExitCode, notReady.Message, notReady.Details));
        }

        order = program.Order;

        var ground = SideTaskPolicy.Ground(
            layout,
            common.Context.Config,
            _launch.ClaudeDirectories(common.Context.Config, layout),
            _launch.CredentialPlaces(common.Context.Config),
            liveRun: () => LiveRunOf(layout));

        // The operating system's isolation the agent's commands run under: where agentPolicy.isolation is required and this
        // machine has none, the side-task is refused (14), saying why and what would give it one.
        var isolated = await _launch.IsolationAsync(common.Context.Config, ground, layout, SideTaskLayout.Of(layout, task).Directory, cancellationToken).ConfigureAwait(false);

        if (isolated.Refusal($"'{RunCommand}'") is { } unisolated)
        {
            return (null, CommandOutcome.Failed(HarnessExit.ToolMissing, $"{unisolated}. Nothing was started."));
        }

        return (new SideTaskSetUp(resolution, order, program.Program, program.Version, ground, runsOn, isolated), null);
    }

    /// <summary>
    /// The name of the run whose owner is live over <paramref name="layout"/>'s main tree, or null where none is: what a
    /// side-task's agent is held off the git state the run works over by (<see cref="AgentPolicy.LiveRunHeld"/>), asked
    /// at each call that would write it. An owner on another machine stands until somebody takes it over, as every owner
    /// does.
    /// </summary>
    /// <param name="layout">The repository's layout.</param>
    private string? LiveRunOf(Repository.HarnessLayout layout)
        => _store.Orchestrators(layout)
            .FirstOrDefault(other => new FlowRunOwner(_store, _identity, _clock).Live(other) is not null)
            ?.Name;

    /// <summary>Says what a claim said of a dead owner's record reclaimed, where it said anything.</summary>
    /// <summary>
    /// What a side-task whose graph could not be opened says of it, once: that it runs without one, and why. Its graph's
    /// record says the same, as a prompt line that names why no pack is given; where even that cannot be written, the
    /// sentence it returns still says it.
    /// </summary>
    /// <param name="layout">The side-task's layout.</param>
    /// <param name="failure">What opening the graph met.</param>
    private string WithoutGraph(SideTaskLayout layout, Exception failure)
    {
        var why = failure is OperationCanceledException ? "its opening was stopped" : failure.Message.TrimEnd('.');

        try
        {
            _store.AppendGraphStepLine(layout.Directory, new Runtime.GraphPromptLine(0, 0, 0, 0, 0, $"the side-task's graph could not be opened: {why}"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HarnessException)
        {
            // The sentence returned says it all the same.
        }

        return $"side-task '{layout.Name}' runs without its codebase graph, which could not be opened: {why}";
    }

    private void Said(string? reclaimed)
    {
        if (reclaimed is { Length: > 0 })
        {
            _output.Warn(RunCommand, reclaimed);
        }
    }

    /// <summary>An exit before the side-task was claimed: nothing was written, which it says.</summary>
    private static CommandOutcome Unwritten(CommandOutcome outcome)
        => outcome with { Details = [.. outcome.Details ?? [], NoRecords] };

    /// <summary>
    /// An exit once the side-task is known: its records named where its directory holds them - in text, and in its
    /// document where <c>--json</c> asked for one - and said to be none where it holds none.
    /// </summary>
    private CommandOutcome Told(CommandOutcome outcome, bool json, SideTaskLayout layout)
    {
        var written = _fileSystem.KindOf(layout.Directory) == PathKind.Directory;
        var told = outcome with { Details = [.. outcome.Details ?? [], written ? $"Records: {layout.Directory}" : NoRecords] };

        return json && written
            ? told with { Data = [SideTaskReports.Stopped(outcome.ExitCode, outcome.Message, layout.Name, layout.Directory)] }
            : told;
    }

    /// <summary>The one message read, or why it could not be.</summary>
    private sealed record PromptRead(string? Text, string? Problem);

    /// <summary>Reads the one message of a one-shot side-task, where its command line names one.</summary>
    private PromptRead? ReadPrompt(SideTaskCall call)
    {
        if (call.Prompt is { Length: > 0 } prompt)
        {
            return new PromptRead(prompt, null);
        }

        if (call.PromptFile is not { Length: > 0 } file)
        {
            return null;
        }

        try
        {
            var text = _fileSystem.ReadAllText(Path.GetFullPath(file, Path.GetFullPath(call.StartDirectory)));
            return new PromptRead(text, text.Length == 0 ? "--prompt-file is empty: one message is asked" : null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new PromptRead(null, $"--prompt-file '{file}' could not be read: {ex.Message}");
        }
    }

    /// <summary>Whether <paramref name="name"/> is a name a side-task's directory may take (<see cref="NameRule"/>).</summary>
    /// <param name="name">The name.</param>
    public static bool IsName(string name)
        => name is { Length: > 0 and <= MostNameLength }
            && char.IsAsciiLetterOrDigit(name[0])
            && name.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    /// <summary>
    /// The name a side-task takes where the person gave none: the date and time it began, to the second, with the first
    /// words of its message kebabed where it began with one - a one shot's. A chat is claimed, and named, before its first
    /// message is typed, so its name is the date and time alone.
    /// </summary>
    /// <param name="message">The one message of a one shot; null for a chat.</param>
    /// <param name="began">When the side-task began, locally.</param>
    public static string Named(string? message, DateTime began)
    {
        var words = (message ?? string.Empty)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Take(NameWords)
            .Select(word => new string([.. word.Select(char.ToLowerInvariant).Where(char.IsAsciiLetterOrDigit)]))
            .Where(word => word.Length > 0)
            .ToList();

        var stem = string.Join("-", words);

        if (stem.Length > MostNameCharacters)
        {
            stem = stem[..MostNameCharacters].TrimEnd('-');
        }

        return began.ToString("yyyy-MM-dd-HHmmss", CultureInfo.InvariantCulture) + (stem.Length == 0 ? string.Empty : "-" + stem);
    }
}

/// <summary>
/// What every set-up of one side-task shares: the repository and its providers, the web search, the mask its records and
/// answers are held to, and the names the configuration appoints, withheld from the agent's children.
/// </summary>
/// <param name="Context">The repository and its configuration.</param>
/// <param name="Providers">The configuration's providers, resolved.</param>
/// <param name="Search">The web search of every source turned on; null where none is.</param>
/// <param name="Mask">
/// The mask the side-task's records and answers are held to, a step's (<see cref="ReasonLaunch.Mask"/>): every secret the
/// configuration appoints that the slot answers - each provider's keys, each client's token - and every form of each
/// search source's key; null where nothing is masked.
/// </param>
/// <param name="Withheld">Every name the configuration appoints a value of the slot for.</param>
internal sealed record SideTaskCommon(
    HarnessContext Context,
    AgentProviders Providers,
    AgentWebSearch? Search,
    ILineMask? Mask,
    IReadOnlyList<string> Withheld);
