using System.Threading.Channels;
using DssHarness.Core.Execution;

namespace DssHarness.Core.Agents;

/// <summary>
/// One client that works an agent's step: Claude Code one day, a provider's API another. Everything the engine knows
/// of a client it knows through this - its name, what it can do, and the one way of running a step.
/// </summary>
/// <remarks>
/// A client never touches the console, the exit code or a file of the engine's: it is given its run, it emits events,
/// and it answers with a result. What its wire becomes is <see cref="AgentEvent"/>s, and the stored stream is written
/// by the runner's sink, not by the client, which is handed no writer of it: what it keeps of its own - the control
/// messages, a result too large for its event - goes beside the stream, in <see cref="AgentRun.StepDirectory"/>. The
/// two ends of a run, <see cref="RunStarted"/> and <see cref="RunFinished"/>, are the runner's, and a client that emits
/// either is refused.
/// </remarks>
public interface IAgentClient
{
    /// <summary>Its key under <c>agentClients</c>.</summary>
    string Name { get; }

    /// <summary>What it can do, as the definition checks and the capabilities table state them.</summary>
    AgentClientCapabilities Capabilities { get; }

    /// <summary>Works the run, emitting what happens to <paramref name="events"/> in the order it happens.</summary>
    /// <param name="run">The run, resolved: everything the client may act on.</param>
    /// <param name="events">Where what happens is emitted; called serially, in order.</param>
    /// <param name="cancellationToken">
    /// The hard stop: the caller's, or the runner's own where the step's record could not be written, for a run whose
    /// record is lost is not one to go on with.
    /// </param>
    /// <returns>How the run ended, and everything it knows beyond its text.</returns>
    Task<AgentRunResult> RunAsync(AgentRun run, IAgentEventSink events, CancellationToken cancellationToken);

    /// <summary>
    /// A token of each kind a run of this client on <paramref name="entries"/> is billed by: the prices a run budgeted in
    /// dollars needs before the client is taken (<see cref="AgentPrices.BudgetRefusal"/>). Its kind's, given the client's
    /// own entry (<see cref="AgentProviders.BilledBy"/>) - the one fact the definition's check reads too - and what every
    /// request of every provider may be billed by for a client of no kind the tool builds in.
    /// </summary>
    /// <param name="entries">The entries the run would work on: its provider, its model, the client's own.</param>
    AgentUsage BilledBy(AgentEntries entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        return AgentProviders.BilledBy(Name, entries.Client);
    }
}

/// <summary>
/// Where the events of one run go. Called serially, in <see cref="AgentEvent.Seq"/> order, by the one pump a run has,
/// after the step's stored stream has written each; a sink that throws gets no more events, and its exception is raised
/// once the run has ended.
/// </summary>
public interface IAgentEventSink
{
    /// <summary>Takes one event, in order.</summary>
    /// <param name="event">The event, numbered and stamped.</param>
    ValueTask EmitAsync(AgentEvent @event);

    /// <summary>
    /// The run a client works has begun its conversation: from now on it never ends
    /// <see cref="AgentRunStatus.NotStarted"/>. A client tells it once, where its own rule of a run never started stops
    /// holding - the loop as the provider begins its first answer with the model asked, Claude Code as a call of it runs
    /// or a turn of it ends - in order with its events; the runner says the step's compaction then. A sink that keeps
    /// nothing of it does nothing.
    /// </summary>
    ValueTask ConversationBegunAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// What the engine, the command line and a graphical front end call to work an agent's step: one way in, events as
/// they happen, and a result at the end.
/// </summary>
public interface IAgentRunner : Application.IUseCase
{
    /// <summary>Works the step, from its request to its result, emitting what happens to <paramref name="events"/>.</summary>
    /// <param name="step">The step, as the engine resolved it.</param>
    /// <param name="events">A sink beside the stored stream, which is always attached; null where only the record is wanted.</param>
    /// <param name="cancellationToken">The hard stop.</param>
    /// <returns>How the run ended.</returns>
    Task<AgentRunResult> RunAsync(AgentStepRequest step, IAgentEventSink? events, CancellationToken cancellationToken);

    /// <summary>Works the step, yielding what happens as it happens, the stored stream attached as always.</summary>
    /// <param name="step">The step, as the engine resolved it.</param>
    /// <param name="cancellationToken">The hard stop.</param>
    /// <returns>Each event, in order, until the run has ended.</returns>
    IAsyncEnumerable<AgentEvent> Stream(AgentStepRequest step, CancellationToken cancellationToken);
}

/// <summary>What a client can do, as the capabilities table states it and a definition is checked against.</summary>
/// <param name="Streaming">Whether the answer is streamed as it is written.</param>
/// <param name="Thinking">Whether the agent's thinking is shown.</param>
/// <param name="WritableWhileWorking">Whether it can be written to while it works: an interrupt, a further message.</param>
/// <param name="GoesOnWith">How it goes on with the conversation the engine stored.</param>
/// <param name="SchemaEnforcedOutput">Whether its output is enforced against the schema by the provider.</param>
/// <param name="FileAndCommandTools">Whose file and command tools it runs.</param>
/// <param name="Web">Whose web tools it runs.</param>
/// <param name="ReportsCost">Whether it says what its work cost.</param>
/// <param name="EffortLevels">The effort levels it takes.</param>
/// <param name="ProviderType">The type of provider whose models it reaches.</param>
/// <param name="ToldModelBy">How it is told which model to run.</param>
/// <param name="PacesRequests">
/// Whether it goes through the gate's lane for each request it sends itself (<see cref="AgentRun.Gates"/>), as the loop
/// the API clients share does; the runner holds the lane around each run of a client that does not, whose requests it
/// cannot see one by one.
/// </param>
public sealed record AgentClientCapabilities(
    bool Streaming,
    bool Thinking,
    bool WritableWhileWorking,
    AgentConversationHandling GoesOnWith,
    bool SchemaEnforcedOutput,
    AgentToolsOwner FileAndCommandTools,
    AgentToolsOwner Web,
    bool ReportsCost,
    IReadOnlyList<string> EffortLevels,
    string ProviderType,
    string ToldModelBy,
    bool PacesRequests = false);

/// <summary>How a client goes on with the conversation the engine stored.</summary>
public enum AgentConversationHandling
{
    /// <summary>Rendered: every launch is new, given the continuation record.</summary>
    Rendered,

    /// <summary>Handed: it appends to the stored conversation.</summary>
    Handed,
}

/// <summary>Whose tools a client runs: its own, or this harness's.</summary>
public enum AgentToolsOwner
{
    /// <summary>The client's own.</summary>
    Client,

    /// <summary>This harness's.</summary>
    Harness,
}

/// <summary>
/// One root a run may read, beside its working places: the main checkout for an agent that reads it, an extra root a
/// definition names.
/// </summary>
/// <param name="Path">The root's path.</param>
/// <param name="Writable">Whether the run may write below it.</param>
public sealed record AgentRoot(string Path, bool Writable);

/// <summary>
/// Where a run works: its agent's worktree where it has one - else the main checkout, which it only reads - its work
/// directory either way, the extra roots its definition names, and the working directory it starts from.
/// </summary>
/// <param name="Worktree">Its agent's worktree; null for an agent with none, which reads the main tree.</param>
/// <param name="WorkDirectory">The agent's own <c>work/&lt;agent&gt;/</c>, which it reads and writes either way.</param>
/// <param name="MainCheckout">The main checkout.</param>
/// <param name="ExtraRoots">The extra roots, each readable, writable where its definition says.</param>
/// <param name="WorkingDirectory">Where the client starts the agent: the worktree, else the work directory.</param>
/// <param name="RunDirectory">The run's directory: the reporter's, and where a step's web search keeps the run's record, <c>web/searches.jsonl</c>.</param>
public sealed record AgentWorkspace(
    string? Worktree,
    string WorkDirectory,
    string MainCheckout,
    IReadOnlyList<AgentRoot> ExtraRoots,
    string WorkingDirectory,
    string RunDirectory);

/// <summary>
/// The output a run is to produce, where its step names one: what its answer's envelope holds as its output
/// (<see cref="AgentEnvelope"/>), and whether the envelope offers the agent questions for the person.
/// </summary>
/// <param name="Name">Its name, as the step's outputs name it.</param>
/// <param name="Schema">The JSON schema it must read against.</param>
/// <param name="MaxRepairs">How many times a schema problem is sent back; two unless given, and never below naught.</param>
/// <param name="AskRounds">
/// How many more times the agent may ask the person in this attempt - the step's <c>askRounds</c> less the times it has
/// asked - offered questions only while some are left; zero where it has asked as often as it may, and null where the
/// step may not ask: an answer that asks all the same is sent back, told so. Never below naught: a count below it would
/// read as asked out, which it is not.
/// </param>
public sealed record AgentOutput(string Name, string Schema, int MaxRepairs = 2, int? AskRounds = null)
{
    /// <inheritdoc cref="AgentOutput" path="/param[@name='MaxRepairs']"/>
    public int MaxRepairs { get; init => field = Counted(value, nameof(MaxRepairs)); } = Counted(MaxRepairs, nameof(MaxRepairs));

    /// <inheritdoc cref="AgentOutput" path="/param[@name='AskRounds']"/>
    public int? AskRounds { get; init => field = value is { } rounds ? Counted(rounds, nameof(AskRounds)) : null; } = AskRounds is { } rounds ? Counted(rounds, nameof(AskRounds)) : null;

    /// <summary>
    /// Why <see cref="Schema"/> is no schema an answer can be held to, as <see cref="Schemas.JsonSchemaSubset.TextProblems"/>
    /// says it, or nothing where it is one: checked before any client is taken, so no model is paid for an answer
    /// nothing could read.
    /// </summary>
    public IReadOnlyList<string> SchemaProblems() => Schemas.JsonSchemaSubset.TextProblems(Schema);

    /// <summary>A count, as given; refused below naught, which no count of repairs or rounds is.</summary>
    private static int Counted(int value, string name)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value, name);

        return value;
    }
}

/// <summary>A stored conversation a run appends to, where its client is handed one.</summary>
/// <param name="Client">The client's key.</param>
/// <param name="Provider">The provider's name.</param>
/// <param name="Id">The conversation's id.</param>
public sealed record AgentSessionRef(string Client, string Provider, string Id);

/// <summary>The bounds a run is held to.</summary>
/// <param name="MaxTurns">The turn bound; null for none.</param>
/// <param name="MaxBudgetUsd">The budget bound, in United States dollars; null for none.</param>
/// <param name="MaxDuration">The duration bound; null for none.</param>
/// <param name="StallSeconds">How long without progress is a stall; null for none.</param>
/// <param name="HandoffSeconds">
/// How long the agent has to answer a handoff, which the definition's <c>handoffSeconds</c> names where it does;
/// null where it does not, and the default of 180 seconds holds.
/// </param>
public sealed record AgentLimits(int? MaxTurns, decimal? MaxBudgetUsd, TimeSpan? MaxDuration, int? StallSeconds, int? HandoffSeconds = null)
{
    /// <summary>How long the agent has to answer a handoff, where nothing names it: three minutes.</summary>
    public static readonly TimeSpan DefaultHandoffBound = TimeSpan.FromSeconds(180);
}

/// <summary>One skill the step's prompt named, as the run was given it - what the step's stream names where its session begins.</summary>
/// <param name="Name">The skill's name: its folder's.</param>
/// <param name="Place">The place it was read, as the prompt's block names it.</param>
/// <param name="File">The <c>SKILL.md</c> it was read from.</param>
/// <param name="Digest">The SHA-256 of its <c>SKILL.md</c>, lowercase hex.</param>
/// <param name="FolderDigest">The SHA-256 of its folder, over each file's path and content in order.</param>
/// <param name="Folder">The folder handed to the step.</param>
public sealed record AgentSkill(string Name, string Place, string File, string Digest, string FolderDigest, string Folder);

/// <summary>
/// The signal the engine raises when a run is pausing, beside the cancellation token, which stays the hard stop: the
/// agent is asked for a handoff, and given the bound it has to answer in.
/// </summary>
/// <param name="Bound">How long the agent has to answer once asked.</param>
/// <param name="MaxAnswers">The most answers one asking may take; three unless given.</param>
public sealed class AgentHandoffSignal(TimeSpan Bound, int MaxAnswers = 3)
{
    /// <summary>The prompt every handoff turn is asked with, on every client: one text for every kind of agent.</summary>
    public const string Prompt =
        "The run is being paused. Stop the task where it stands: run nothing more and change nothing more.\n"
        + "Answer with a handoff for whoever goes on with this work - you, later, or a session that has only this text and the tree.\n"
        + "- Done: what you finished and what you established, naming the files you changed or relied on.\n"
        + "- Left: what the task still needs, in the order you would do it.\n"
        + "- In the middle of: anything started and not finished - a command that was cut off, a file part edited, a check you were about to run - and what to check before going on.\n"
        + "Say only what you know. This answer is the whole handoff.";

    /// <summary>The rule every call asked in a handoff turn is refused by, on every client.</summary>
    public const string TurnRule = "the handoff turn";

    /// <summary>What every call asked in a handoff turn is answered: the turn is for the handoff alone.</summary>
    public const string ToolRefusal = "no tool runs in a handoff turn; answer with the handoff";

    /// <summary>Why a run has no handoff when its bound passed before the agent answered.</summary>
    public const string BoundPassed = "the bound passed before the agent answered";

    /// <summary>Why a run has no handoff when the handoff turn itself ended <paramref name="status"/>.</summary>
    /// <param name="status">How the turn ended.</param>
    /// <param name="why">Why, as that ending says it.</param>
    public static string TurnEnded(AgentRunStatus status, string? why) => $"the handoff turn ended {status}: {why}";

    /// <summary>The question a handoff turn asks: what the agent is told first, where anything is, then the prompt.</summary>
    /// <param name="lead">What the agent is told before it is asked; null where the question stands alone.</param>
    public static string Question(string? lead) => lead is { Length: > 0 } told ? $"{told}\n\n{Prompt}" : Prompt;

    /// <summary>Why a run has no handoff when its agent gave none in <paramref name="answers"/> answers.</summary>
    /// <param name="answers">How many answers it gave.</param>
    public static string NoneIn(int answers) => $"the agent gave no handoff in {answers} answers";

    /// <summary>
    /// The handoff an answer gives: its notes where it answered with an envelope that holds them
    /// (<see cref="AgentEnvelope.Notes"/>), else the whole answer held to the schema, else its text; null where that says
    /// nothing, and the agent gave no handoff.
    /// </summary>
    /// <param name="structured">The answer held to the run's schema, where it carries one.</param>
    /// <param name="text">The answer's text.</param>
    public static string? AnswerOf(System.Text.Json.Nodes.JsonNode? structured, string? text)
    {
        var answer = structured is System.Text.Json.Nodes.JsonObject held && held[AgentEnvelope.Notes] is System.Text.Json.Nodes.JsonValue notes && notes.TryGetValue<string>(out var said)
            ? said
            : structured?.ToJsonString() ?? text;

        return string.IsNullOrWhiteSpace(answer) ? null : answer;
    }

    private readonly TaskCompletionSource<AgentHandoffAsked> _asked = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>How long the agent has to answer once asked.</summary>
    public TimeSpan Bound { get; } = Bound;

    /// <summary>The most answers one asking may take.</summary>
    public int MaxAnswers { get; } = MaxAnswers;

    /// <summary>Whether the engine has raised the handoff.</summary>
    public bool Asked => _asked.Task.IsCompleted;

    /// <summary>Completes once the engine raises the handoff, with why and what is told before the question.</summary>
    public Task<AgentHandoffAsked> WhenAsked => _asked.Task;

    /// <summary>Raises the handoff: the run is pausing, and the agent is asked what it leaves. The first asking stands.</summary>
    /// <param name="why">Why the run is pausing, as the record says it.</param>
    /// <param name="lead">
    /// What the agent is told before it is asked - a plan update that changes or withdraws its work item, whole - or
    /// null where the question stands alone.
    /// </param>
    public void Ask(string why = "the run is pausing", string? lead = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(why);
        _asked.TrySetResult(new AgentHandoffAsked(why, lead));
    }
}

/// <summary>Why a handoff was asked, and what the agent is told before the question.</summary>
/// <param name="Why">Why the run is pausing.</param>
/// <param name="Lead">What the agent is told before it is asked; null where the question stands alone.</param>
public sealed record AgentHandoffAsked(string Why, string? Lead);

/// <summary>What an agent may do, as its definition's <c>capabilities</c> name it: each gives it the tools of its kind.</summary>
public enum AgentCapability
{
    /// <summary>Read files, list and search them.</summary>
    Read,

    /// <summary>Write and edit files.</summary>
    Write,

    /// <summary>Run commands.</summary>
    Run,

    /// <summary>Search the web and fetch its pages.</summary>
    Web,
}

/// <summary>
/// The credential a run signs in with, where its client signs in: how (<see cref="AgentAuth"/>), each value the slot
/// gave for it in the order they are tried - the provider's keys under <c>apiKey</c>, the one token under a token's way,
/// none under the machine's login - and the Claude Code directory whose login and settings a launch reads.
/// </summary>
/// <param name="Auth">How it signs in: one of <see cref="AgentAuth.All"/>.</param>
/// <param name="Keys">The values it may sign in with, in order; empty for the machine's login.</param>
/// <param name="Api">The provider's address, which a launch that signs in with a key or a token is sent to; null for the machine's login.</param>
/// <param name="ConfigDirectory">
/// The Claude Code directory a launch reads - what the slot holds under the name the client's <c>configDir</c> gives
/// (<c>CLAUDE_CONFIG_DIR</c> where it gives none), else the tool's own, else none, which is Claude Code's default - and
/// where a session file it may leave is looked for.
/// </param>
public sealed record AgentCredential(string Auth, IReadOnlyList<AgentKey> Keys, string? Api = null, string? ConfigDirectory = null);

/// <summary>
/// The web search a step's agent is given: the sources its <c>web_search</c> asks, in the order its answer gives them and
/// those turned on alone, its bounds, and each source's key where it names one, every form of which it is sent in the
/// run's mask holds (<see cref="Web.Search.WebSearchCredential.Forms"/>).
/// </summary>
/// <param name="Sources">The step's sources, as its <c>search</c> picks them (<see cref="Web.Search.WebSearchSources.Chosen"/>).</param>
/// <param name="ResultsPerSource">The most results a source gives one search.</param>
/// <param name="MaxPerStep">The most requests of sources the step makes.</param>
/// <param name="Keys">Each source's key, by its name, as <see cref="Web.Search.WebSearchKeys"/> read it.</param>
public sealed record AgentWebSearch(
    IReadOnlyList<Web.Search.WebSearchSource> Sources,
    int ResultsPerSource,
    int MaxPerStep,
    IReadOnlyDictionary<string, Web.Search.WebSearchKey> Keys)
{
    /// <summary>The search a step whose <c>search</c> is <paramref name="search"/> is given of <paramref name="sources"/>, with <paramref name="keys"/>.</summary>
    /// <param name="sources">The configuration's web search.</param>
    /// <param name="search">The step's <c>search</c>; <see langword="null"/> where it names none.</param>
    /// <param name="keys">Each source's key, by its name.</param>
    public static AgentWebSearch Of(Web.Search.WebSearchSources sources, IReadOnlyList<string>? search, IReadOnlyDictionary<string, Web.Search.WebSearchKey> keys)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(keys);

        return new(sources.Chosen(search), sources.ResultsPerSource, sources.MaxPerStep, keys);
    }
}

/// <summary>One value a run may sign in with, by the name the slot holds it under.</summary>
/// <param name="Name">The variable's name, which records say.</param>
/// <param name="Value">The value, which no record says: the run's mask holds it.</param>
public sealed record AgentKey(string Name, string Value)
{
    /// <summary>The key by its name alone, so no text made of it holds the value.</summary>
    public override string ToString() => Name;
}

/// <summary>
/// What the engine has to tell the agent while it works: a further message, a note that no tool runs now, what a plan
/// update changes for its work. Posted by the engine, read by the client as its wire allows.
/// </summary>
public sealed class AgentMessages
{
    private readonly Channel<string> _messages = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true });

    private readonly TaskCompletionSource _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Completes once the messages are ended (<see cref="Complete"/>), whatever is still unread: for a chat, the person
    /// leaving - which ends every stage of its run before a client is given it, as the run's pause does.
    /// </summary>
    public Task Ended => _ended.Task;

    /// <summary>Tells the agent <paramref name="message"/>.</summary>
    /// <param name="message">The message, whole.</param>
    /// <returns>Whether the channel holds it for the agent: false where the run's messages were ended, so nothing posted to an ended run is lost unsaid.</returns>
    public bool Post(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        return _messages.Writer.TryWrite(message);
    }

    /// <summary>
    /// Takes the oldest message posted and not yet taken, without waiting: for a client that gives the agent what it was
    /// told at the points its loop reaches, rather than as each is posted (<see cref="ReadAllAsync"/>).
    /// </summary>
    /// <param name="message">The message; null where none waits.</param>
    /// <returns>Whether one waited.</returns>
    public bool TryTake([System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out string message) => _messages.Reader.TryRead(out message);

    /// <summary>Each message the engine has told the agent, as it is told, until the run ends.</summary>
    /// <param name="cancellationToken">Stops waiting.</param>
    /// <returns>The messages, in the order they were posted.</returns>
    public IAsyncEnumerable<string> ReadAllAsync(CancellationToken cancellationToken)
        => _messages.Reader.ReadAllAsync(cancellationToken);

    /// <summary>Ends the messages: no more will be posted, and the reader's wait ends.</summary>
    public void Complete()
    {
        _messages.Writer.TryComplete();
        _ended.TrySetResult();
    }
}

/// <summary>
/// The signal the engine raises to interrupt the agent's current turn alone, leaving the conversation open: what a
/// person's Ctrl+C asks of a side-task. The turn is cut where it stands, and the next message goes on from the
/// conversation as its client keeps it.
/// </summary>
/// <remarks>
/// No bound is kept and nothing is asked of the agent: an interrupt is not a handoff, which asks what the agent leaves.
/// Every client the tool builds in honours it, what the cut turn used kept: Claude Code interrupts its launch's turn and
/// reads its result - a chat's launch held open after it, a one shot's run ended <see cref="AgentRunStatus.Cancelled"/>
/// at it - and the loop of the API clients leaves its request where it stands and cuts the calls running, its run ended
/// Cancelled with what its provider said the request used counted and a cost it said nothing of unknown, its
/// conversation stored whole up to its last whole turn and the cut answer thrown away. Raised before a client is given
/// the run - while it waits at the gate or to be tried again - it ends a run that is no chat's there, no launch begun,
/// and cuts nothing of a chat's, whose launch is never handed it (<see cref="AgentRunner"/>).
/// </remarks>
public sealed class AgentTurnInterrupt
{
    /// <summary>How a run whose turn the interrupt cut ends, as its error says it.</summary>
    public const string Cut = "the turn was interrupted where it stood";

    private readonly TaskCompletionSource _asked = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Interrupts the agent's current turn.</summary>
    public void Interrupt() => _asked.TrySetResult();

    /// <summary>Completes once <see cref="Interrupt"/> is raised.</summary>
    public Task Asked => _asked.Task;
}

/// <summary>
/// A step an agent is to work, as the engine resolved it: everything a run needs, before a client is chosen - the work it
/// goes on with among it, as its continuation record (<see cref="Continuation"/>) and the compaction it begins from
/// (<see cref="Compaction"/>).
/// </summary>
/// <param name="Run">The orchestrator run's name.</param>
/// <param name="Agent">The agent the step works as.</param>
/// <param name="StepInstance">The step instance's name.</param>
/// <param name="Attempt">
/// Which attempt of the step instance this is, from one: a step stopped and run again is the same instance, and its
/// next attempt appends to the same stored stream, beginning with a <see cref="RunStarted"/> that names it.
/// </param>
/// <param name="AgentDefinition">The agent definition's name, where the step names one.</param>
/// <param name="Provider">The step's own provider, where it names one.</param>
/// <param name="Client">The step's own client, where it names one.</param>
/// <param name="Tier">The step's own tier, where it names one.</param>
/// <param name="Model">The step's own model, where it names one.</param>
/// <param name="Effort">The step's own effort, where it names one.</param>
/// <param name="Fallback">The step's own fallback, in order, where it names one.</param>
/// <param name="SystemPrompt">The system prompt, filled from the definition's body.</param>
/// <param name="Prompt">
/// The prompt, whole as whoever built the request filled it: sent as it is and never read as a template again, so a
/// brace a filled value holds stays a brace.
/// </param>
/// <param name="ResumeTemplate">The resume template, where the step goes on with earlier work.</param>
/// <param name="Workspace">Where the run works.</param>
/// <param name="Output">The output it is to produce, where its step names one.</param>
/// <param name="Conversation">The stored conversation to append to, where the client is handed one.</param>
/// <param name="Limits">The bounds it is held to.</param>
/// <param name="Mask">What masks the secrets its lines may carry; null where the step carries none.</param>
public sealed record AgentStepRequest(
    string Run,
    string Agent,
    string StepInstance,
    int Attempt,
    string? AgentDefinition,
    string? Provider,
    string? Client,
    string? Tier,
    string? Model,
    string? Effort,
    IReadOnlyList<string>? Fallback,
    string SystemPrompt,
    string Prompt,
    string? ResumeTemplate,
    AgentWorkspace Workspace,
    AgentOutput? Output,
    AgentSessionRef? Conversation,
    AgentLimits Limits,
    ILineMask? Mask)
{
    /// <summary>
    /// What the agent's policy is decided against - its restrictions and its capabilities among it; null where the step
    /// was given none, and nothing then runs.
    /// </summary>
    public Policy.AgentPolicyGround? Ground { get; init; }

    /// <summary>
    /// The operating system's isolation the launch's agent runs under - the mechanism and what it denies each command, or
    /// why there is none - as the engine decided it from the ground; null where none was decided, and every command then
    /// runs on the words policy alone.
    /// </summary>
    public Isolation.LaunchIsolation? Isolation { get; init; }

    /// <summary>The credential the run signs in with, where its client signs in; null for the machine's login.</summary>
    public AgentCredential? Credential { get; init; }

    /// <summary>The program a client that starts one starts, as the engine found it ready; null for a client that starts none.</summary>
    public string? Program { get; init; }

    /// <summary>The version that program answered when the engine found it ready, as a refusal names it; null where it was not asked.</summary>
    public string? ProgramVersion { get; init; }

    /// <summary>The skills the prompt holds, each as the step was given it: what the step's stream names where its session begins.</summary>
    public IReadOnlyList<AgentSkill> Skills { get; init; } = [];

    /// <summary>
    /// The id the session the step's first launch begins is given, made before the prompt was written - a skill's
    /// <c>${CLAUDE_SESSION_ID}</c> is it - where the step begins a conversation of its own; null where it goes on with a
    /// stored one, or the client makes the id. A later launch of the same attempt - a retry, a move to another model -
    /// begins a session of its own.
    /// </summary>
    public string? SessionId { get; init; }

    /// <summary>
    /// The names a child the run starts never inherits, whatever the tool's own environment holds: every name the
    /// configuration appoints a value of the slot for, so no other company's key and no token reaches it.
    /// </summary>
    public IReadOnlyList<string> Withheld { get; init; } = [];

    /// <summary>What the configuration says of the agent's commands: the names a child is given back, and their times.</summary>
    public AgentCommandSettings Commands { get; init; } = AgentCommandSettings.Default;

    /// <summary>
    /// How many times a failed agent is asked again on its model before the step moves to the next model of its order,
    /// and how long to wait before the first ask, doubling each time: the step's own where it says them, the workflow's
    /// where it does not. Null where the caller gives no rule, and none is retried.
    /// </summary>
    public int? Retries { get; init; }

    /// <summary>How long to wait before the first retry of a failed agent, in seconds, doubling each time; null with <see cref="Retries"/>.</summary>
    public int? BackoffSeconds { get; init; }

    /// <summary>The web search the agent is given: its sources, bounds and keys; <see langword="null"/> where it searches none.</summary>
    public AgentWebSearch? WebSearch { get; init; }

    /// <summary>The signal the engine raises when the run is pausing; one of the runner's own where the engine gives none.</summary>
    public AgentHandoffSignal? Handoff { get; init; }

    /// <summary>What the engine tells the agent while it works; one of the runner's own where the engine gives none.</summary>
    public AgentMessages? Messages { get; init; }

    /// <summary>The configuration's entries the step's model is reached by, as the engine resolved them; null where it gave none.</summary>
    public AgentEntries? Entries { get; init; }

    /// <summary>
    /// The step's whole order as the engine resolved it for running - the entry that serves first and each entry the
    /// engine's fallback may move it to - and null where the engine gave none, for a step worked as one run on one
    /// client with no move. An entry the machine cannot use is said and passed over when its turn comes; where none of
    /// the first model's keys is in the slot the run is refused before it starts.
    /// </summary>
    public IReadOnlyList<AgentRunEntry>? Order { get; init; }

    /// <summary>The run context, the block of the run's facts the engine gives every step; null where it gave none.</summary>
    public string? RunContext { get; init; }

    /// <summary>Each plan update that told this work something, with what raised it; null where the engine gave none.</summary>
    public string? PlanUpdates { get; init; }

    /// <summary>The continuation record of the work the step goes on with; null where the step begins its own.</summary>
    public string? Continuation { get; init; }

    /// <summary>
    /// The engine's compaction of the conversation the run begins from, where it compacted one: the record the new
    /// conversation's first message is - or, carried before a task of its own (<see cref="AgentCompacted.BeforeItsTask"/>),
    /// the run's continuation - and what it kept and reduced, said as the run's <see cref="Compacted"/> event and counted
    /// in its compactions once a client is given it; null where it did not. A run given one begins a new conversation.
    /// </summary>
    public AgentCompacted? Compaction { get; init; }

    /// <summary>Where the run's every entry records what its key spent, for the next step's choice of one; null where nothing is counted.</summary>
    public IAgentSpendSink? Spend { get; init; }

    /// <summary>
    /// Where this run's own records are kept: a reason step's own directory as the one owner of where a step instance is
    /// kept spells it (<see cref="Orchestration.OrchestratorLayout.InstanceDirectory"/>), an advice's under the asking
    /// step's <c>advice/&lt;n&gt;/</c>, the run's reporter's under the run's <c>asks/&lt;stamp&gt;/</c>. Null where the
    /// caller names none, and the runner keeps them where that owner says of the request's agent and instance.
    /// </summary>
    public string? RecordDirectory { get; init; }

    /// <summary>
    /// Where a client handed a conversation stores it, where not under the request's agent's own sessions: a side-task
    /// keeps its conversation under its own records, an advice under its own directory, whatever agent - if any - the
    /// step it was asked for names, and the run's reporter under its own <c>asks/&lt;stamp&gt;/</c>. Null where the
    /// agent's own sessions hold it.
    /// </summary>
    public string? SessionsDirectory { get; init; }

    /// <summary>
    /// The signal that interrupts the agent's current turn alone, where the engine gave one: a side-task's chat, whose
    /// Ctrl+C cuts the turn and leaves the conversation open. Null where nothing raises it.
    /// </summary>
    public AgentTurnInterrupt? Interrupt { get; init; }

    /// <summary>
    /// Whether this run is a side-task's chat, whose client holds its turn open between the person's messages: a client
    /// that renders its conversations keeps its launch open and reads the person's later messages from its messages
    /// channel, ending only when they end. False for every step of a run.
    /// </summary>
    public bool Chat { get; init; }

    /// <summary>
    /// The move that brought a chat to the first entry of its <see cref="Order"/>, where an earlier run's walk handed it
    /// back (<see cref="AgentRunResult.GoesOnBy"/>): the walk goes on from it as from its own, so a first entry that
    /// cannot be started is passed over as one a move brought it to, and the chat handed the entries after it. Null where
    /// the run's order is its own.
    /// </summary>
    public AgentMove? Moved { get; init; }
}

/// <summary>
/// A run, resolved: what a client gets and all a client gets. The prompt is filled, the places are absolute, the
/// step's directory is its own, and the bounds are the run's own - the step's definition has said its last word by here.
/// </summary>
/// <param name="SystemPrompt">The system prompt, as sent.</param>
/// <param name="UserPrompt">The prompt, filled with its values.</param>
/// <param name="Workspace">Where the run works.</param>
/// <param name="Output">The output it is to produce, where its step names one.</param>
/// <param name="Conversation">The stored conversation to append to; null for a new one.</param>
/// <param name="Limits">The bounds it is held to.</param>
/// <param name="Provider">The provider the step's model is of.</param>
/// <param name="Model">The model it runs on, where the step named one; null where it did not, before the resolution that names one.</param>
/// <param name="Effort">The effort it was asked for, where one is.</param>
/// <param name="Fallback">The entries it may go on with, in order: the step's later entries, of every client, each usable or said and passed over.</param>
/// <param name="Files">The files the run was handed, by path.</param>
/// <param name="Skills">The skills its prompt named, as it was given them.</param>
/// <param name="Mask">What masks the secrets its lines may carry; never null, masking nothing where the step carries none.</param>
/// <param name="StepDirectory">
/// The step's own directory, where the client keeps what it records of its own beside the stored stream: the control
/// messages, a result too large for its event, what the child said on its standard error. The stream itself is
/// written by the runner from the events the client emits, and by nothing else. A child the client starts contained
/// records its session here too, by the request's <see cref="Processes.ProcessRequest.SessionRecord"/> being
/// <see cref="Orchestration.ChildSessions.For"/> this directory, so a process killed before it ended the child leaves
/// the child for the next command over the orchestrator to end.
/// </param>
/// <param name="Handoff">The signal the engine raises when the run is pausing.</param>
/// <param name="Messages">What the engine has to tell the agent while it works.</param>
/// <param name="RunContext">The run context, the block of the run's facts the engine gives every step; null where it gave none.</param>
/// <param name="PlanUpdates">Each plan update that told this work something, with what raised it; null where the engine gave none.</param>
/// <param name="Continuation">The continuation record of the work the step goes on with; null where the step begins its own.</param>
/// <param name="Compaction">The compaction record of the conversation this run begins, where the engine compacted one; null where it did not.</param>
public sealed record AgentRun(
    string SystemPrompt,
    string UserPrompt,
    AgentWorkspace Workspace,
    AgentOutput? Output,
    AgentSessionRef? Conversation,
    AgentLimits Limits,
    string? Provider,
    string? Model,
    string? Effort,
    IReadOnlyList<AgentRunEntry> Fallback,
    IReadOnlyList<string> Files,
    IReadOnlyList<AgentSkill> Skills,
    ILineMask Mask,
    string StepDirectory,
    AgentHandoffSignal Handoff,
    AgentMessages Messages,
    string? RunContext = null,
    string? PlanUpdates = null,
    string? Continuation = null,
    string? Compaction = null)
{
    /// <summary>
    /// What the agent's policy is decided against - its capabilities among it, which name the tools a launch offers; null
    /// where the step was given none, and a client that asks it runs nothing.
    /// </summary>
    public Policy.AgentPolicyGround? Ground { get; init; }

    /// <summary>
    /// The operating system's isolation the run's processes are started under (<see cref="Agents.Isolation.LaunchIsolation.Processes"/>):
    /// a command of the API clients' runtime, and each shell command a Claude Code launch hands over through its shell
    /// prefix, Claude Code's own process outside it - or that whole process inside, where the mechanism takes no command
    /// handed to it; null where none was decided.
    /// </summary>
    public Isolation.LaunchProcesses? Isolation { get; init; }

    /// <summary>
    /// The credential the run signs in with, where its client signs in; null for the machine's login. Its keys are the
    /// run's model's keys in the order they are tried - the one the run starts on first - and each client moves through
    /// them its own way before the model changes: the loop the API clients share on a key the provider refuses and at an
    /// account's limit, sending the same request again; the claude-code client only on a key refused before its launch
    /// did any work, with a new launch of the step's prompt - a key refused later, and every limit, end the launch, and
    /// the runner moves the step.
    /// </summary>
    public AgentCredential? Credential { get; init; }

    /// <summary>The program a client that starts one starts, as the engine found it ready; null for a client that starts none.</summary>
    public string? Program { get; init; }

    /// <summary>The version that program answered when the engine found it ready, as a refusal names it; null where it was not asked.</summary>
    public string? ProgramVersion { get; init; }

    /// <summary>
    /// The id the session this launch begins is given, where the engine made it before the prompt was written - the
    /// step's first launch of a conversation of its own; null where the client makes its own, or goes on with a stored
    /// conversation, whose id it keeps.
    /// </summary>
    public string? SessionId { get; init; }

    /// <summary>The names a child the run starts never inherits: every name the configuration appoints a value of the slot for.</summary>
    public IReadOnlyList<string> Withheld { get; init; } = [];

    /// <summary>What the configuration says of the agent's commands: the names a child is given back, and their times.</summary>
    public AgentCommandSettings Commands { get; init; } = AgentCommandSettings.Default;

    /// <summary>The web search the agent is given: its sources, bounds and keys; <see langword="null"/> where it searches none.</summary>
    public AgentWebSearch? WebSearch { get; init; }

    /// <summary>
    /// The agent's <c>sessions/</c> directory, where a client handed a conversation keeps it, each in its own
    /// <c>sessions/&lt;id&gt;/</c> (<see cref="Orchestration.OrchestratorLayout.SessionDirectoryIn"/>); null where the run was
    /// given none, and such a client is not started.
    /// </summary>
    public string? SessionsDirectory { get; init; }

    /// <summary>
    /// The configuration's entries the run's model is reached by - its provider's, its own and its client's - as the engine
    /// resolved them; null where it gave none, and a client that builds every request from them is not started.
    /// </summary>
    public AgentEntries? Entries { get; init; }

    /// <summary>
    /// Whether this run is a side-task's chat, whose client holds its turn open between the person's messages: a client
    /// that renders its conversations keeps its launch open and reads the person's later messages from
    /// <see cref="Messages"/>, ending only when they end; every other client ends at each turn, the conversation stored
    /// and handed back. False for every step of a run.
    /// </summary>
    public bool Chat { get; init; }

    /// <summary>
    /// The signal that interrupts the agent's current turn alone: a chat's launch held open stays open for the next
    /// message, and any other run ends <see cref="AgentRunStatus.Cancelled"/> with what it used
    /// (<see cref="AgentTurnInterrupt.Cut"/>); null where the engine gave none.
    /// </summary>
    public AgentTurnInterrupt? Interrupt { get; init; }

    /// <summary>
    /// The provider's own web search, which a side-task's <c>web_search</c> asks beside the configured sources - its
    /// <c>native</c> part, asked by the client's loop as the run stands when the agent asks it, with the entry serving the
    /// run then; null where this run's provider runs none - every client but the Claude API's - and for every step of a
    /// run, whose web is the harness's own alone.
    /// </summary>
    public IAgentNativeSearch? NativeSearch { get; init; }

    /// <summary>
    /// The gate's lanes, which a client that paces its own requests (<see cref="AgentClientCapabilities.PacesRequests"/>)
    /// enters for each request it sends - the lane of the run's provider and the key the request is signed with - and
    /// sets the cool-down of on every 429; null where the runner holds no gate, or holds the lane around the whole run
    /// itself.
    /// </summary>
    public ClientGates? Gates { get; init; }
}

/// <summary>
/// The provider's own web search, as its wire asks it: one query of the model and the key it is given, answered as a
/// source's part of the same search, with what the request used - its tokens, the results billed as input among them,
/// and its searches - and the failure it met. Which entry it asks, the gate it goes through and the part of the run's
/// usage it is counted to are the asking loop's, as they are for every request of the conversation.
/// </summary>
public interface IAgentNativeSearch
{
    /// <summary>The part's source name, as the answer and the records name it: <c>native</c>.</summary>
    string Source { get; }

    /// <summary>The tokens a search of <paramref name="query"/> on <paramref name="model"/> sends, as the gate counts a request's input as it enters.</summary>
    /// <param name="query">The query, as the agent wrote it.</param>
    /// <param name="model">The model the search is asked of.</param>
    long InputTokens(string query, string model);

    /// <summary>Searches <paramref name="query"/> with the provider's own search, asked of <paramref name="model"/> and signed with <paramref name="key"/>.</summary>
    /// <param name="query">The query, as the agent wrote it.</param>
    /// <param name="model">The model the search is asked of: the one serving the run when the agent asks.</param>
    /// <param name="key">The key the request is signed with: the one serving the run when the agent asks.</param>
    /// <param name="cancellationToken">Stops the search.</param>
    /// <returns>The part it came to - failed, saying why, where the provider did not answer with a search - what its request used, and the failure it met.</returns>
    Task<AgentNativeSearched> SearchAsync(string query, string model, AgentKey key, CancellationToken cancellationToken);

    /// <summary>The part of a search of <paramref name="query"/> that failed for <paramref name="why"/>: the one shape every failed native part takes.</summary>
    /// <param name="query">The query, as the agent wrote it.</param>
    /// <param name="why">Why it failed, as the part says it.</param>
    Web.Search.WebSearchPart Failed(string query, string why)
        => new(Source, Source, Version: 1, query, Web.Search.WebSearchAnswer.Failed(why), Withheld: 0, Cached: false, KeyMissing: null);
}

/// <summary>What one request of the provider's own search came to.</summary>
/// <param name="Part">The part it came to, failed where the provider did not answer with a search.</param>
/// <param name="Used">What the request used.</param>
/// <param name="Failure">The failure the request met, read as every failure of the provider's is; null where it answered.</param>
public sealed record AgentNativeSearched(Web.Search.WebSearchPart Part, AgentUsage Used, Loop.ChatFailure? Failure = null);

/// <summary>
/// Asks a run's native search for its tool runtime, as the run stands when the agent asks it: the client's loop, which
/// knows the entry serving the run then, goes through its gate and counts what the search used.
/// </summary>
/// <param name="query">The query, as the agent wrote it.</param>
/// <param name="cancellationToken">Stops the search: the run's stop and the turn's cut.</param>
/// <returns>The part the search came to.</returns>
public delegate Task<Web.Search.WebSearchPart> AgentNativeAsk(string query, CancellationToken cancellationToken);

/// <summary>
/// The configuration's entries a run's model is reached by: its provider - the API's address and the currency of its
/// prices - the model's own - what it holds and costs - and its client's, where the configuration declares one.
/// </summary>
/// <param name="Provider">The provider's entry.</param>
/// <param name="Model">The model's entry, one of the provider's.</param>
/// <param name="Client">The client's entry under <c>agentClients</c>; null where the configuration declares none, and every key is its default.</param>
public sealed record AgentEntries(Configuration.AgentProviderConfig Provider, Configuration.AgentModelConfig Model, Configuration.AgentClientConfig? Client);
