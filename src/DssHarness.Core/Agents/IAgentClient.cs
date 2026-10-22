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
    string ToldModelBy);

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
/// <param name="RunDirectory">The run's directory, for the reporter.</param>
public sealed record AgentWorkspace(
    string? Worktree,
    string WorkDirectory,
    string MainCheckout,
    IReadOnlyList<AgentRoot> ExtraRoots,
    string WorkingDirectory,
    string RunDirectory);

/// <summary>The output a run is to produce, where its step names one.</summary>
/// <param name="Name">Its name, as the step's outputs name it.</param>
/// <param name="Schema">The JSON schema it must read against.</param>
/// <param name="MaxRepairs">How many times a schema problem is sent back; two unless given.</param>
public sealed record AgentOutput(string Name, string Schema, int MaxRepairs = 2);

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

/// <summary>One model the step goes on with, in the fallback's order.</summary>
/// <param name="Model">The model.</param>
/// <param name="Provider">Its provider.</param>
/// <param name="Client">Its client.</param>
/// <param name="Effort">The effort it was asked for, where one is.</param>
/// <param name="UsableHere">Whether it can be used on this machine.</param>
public sealed record AgentFallbackModel(string Model, string Provider, string? Client, string? Effort, bool UsableHere);

/// <summary>One skill the step's prompt named, as the run was given it.</summary>
/// <param name="Name">The skill's name.</param>
/// <param name="Where">Where it was read.</param>
/// <param name="Digest">Its digest.</param>
/// <param name="Folder">The folder handed to the step.</param>
public sealed record AgentSkill(string Name, string Where, string Digest, string Folder);

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
/// The Claude Code directory a launch reads - the slot's <c>CLAUDE_CONFIG_DIR</c>, else the tool's own, else none,
/// which is Claude Code's default - and where a session file it may leave is looked for.
/// </param>
public sealed record AgentCredential(string Auth, IReadOnlyList<AgentKey> Keys, string? Api = null, string? ConfigDirectory = null);

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

    /// <summary>Tells the agent <paramref name="message"/>.</summary>
    /// <param name="message">The message, whole.</param>
    public void Post(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        _messages.Writer.TryWrite(message);
    }

    /// <summary>Each message the engine has told the agent, as it is told, until the run ends.</summary>
    /// <param name="cancellationToken">Stops waiting.</param>
    /// <returns>The messages, in the order they were posted.</returns>
    public IAsyncEnumerable<string> ReadAllAsync(CancellationToken cancellationToken)
        => _messages.Reader.ReadAllAsync(cancellationToken);

    /// <summary>Ends the messages: no more will be posted, and the reader's wait ends.</summary>
    public void Complete() => _messages.Writer.TryComplete();
}

/// <summary>
/// A step an agent is to work, as the engine resolved it: everything a run needs, before a client is chosen. The work
/// it goes on with is not here yet - the continuation record comes with its own slice, and joins this request then.
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
/// <param name="PromptTemplate">The prompt's template, before its values are filled.</param>
/// <param name="PromptValues">The template's values.</param>
/// <param name="ResumeTemplate">The resume template, where the step goes on with earlier work.</param>
/// <param name="Workspace">Where the run works.</param>
/// <param name="Output">The output it is to produce, where its step names one.</param>
/// <param name="Conversation">The stored conversation to append to, where the client is handed one.</param>
/// <param name="Limits">The bounds it is held to.</param>
/// <param name="MayAsk">Whether it may ask the person.</param>
/// <param name="AskRounds">How many times it may ask.</param>
/// <param name="Restrictions">The extra restrictions its step or definition names.</param>
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
    string PromptTemplate,
    IReadOnlyDictionary<string, string> PromptValues,
    string? ResumeTemplate,
    AgentWorkspace Workspace,
    AgentOutput? Output,
    AgentSessionRef? Conversation,
    AgentLimits Limits,
    bool MayAsk,
    int AskRounds,
    IReadOnlyList<string> Restrictions,
    ILineMask? Mask)
{
    /// <summary>What the agent's policy is decided against; null where the step was given none, and nothing then runs.</summary>
    public Policy.AgentPolicyGround? Ground { get; init; }

    /// <summary>What the agent may do, as its definition's <c>capabilities</c> name it; all four unless given.</summary>
    public IReadOnlyList<AgentCapability> Capabilities { get; init; } = AllCapabilities;

    /// <summary>The credential the run signs in with, where its client signs in; null for the machine's login.</summary>
    public AgentCredential? Credential { get; init; }

    /// <summary>The program a client that starts one starts, as the engine found it ready; null for a client that starts none.</summary>
    public string? Program { get; init; }

    /// <summary>
    /// The names a child the run starts never inherits, whatever the tool's own environment holds: every name the
    /// configuration appoints a value of the slot for, so no other company's key and no token reaches it.
    /// </summary>
    public IReadOnlyList<string> Withheld { get; init; } = [];

    /// <summary>The signal the engine raises when the run is pausing; one of the runner's own where the engine gives none.</summary>
    public AgentHandoffSignal? Handoff { get; init; }

    /// <summary>What the engine tells the agent while it works; one of the runner's own where the engine gives none.</summary>
    public AgentMessages? Messages { get; init; }

    /// <summary>Every capability, in the order a definition names them.</summary>
    public static IReadOnlyList<AgentCapability> AllCapabilities { get; } = [AgentCapability.Read, AgentCapability.Write, AgentCapability.Run, AgentCapability.Web];
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
/// <param name="Fallback">The models it goes on with, in order.</param>
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
    IReadOnlyList<AgentFallbackModel> Fallback,
    IReadOnlyList<string> Files,
    IReadOnlyList<AgentSkill> Skills,
    ILineMask Mask,
    string StepDirectory,
    AgentHandoffSignal Handoff,
    AgentMessages Messages)
{
    /// <summary>What the agent's policy is decided against; null where the step was given none, and a client that asks it runs nothing.</summary>
    public Policy.AgentPolicyGround? Ground { get; init; }

    /// <summary>What the agent may do, as its definition's <c>capabilities</c> name it.</summary>
    public IReadOnlyList<AgentCapability> Capabilities { get; init; } = AgentStepRequest.AllCapabilities;

    /// <summary>The credential the run signs in with, where its client signs in; null for the machine's login.</summary>
    public AgentCredential? Credential { get; init; }

    /// <summary>The program a client that starts one starts, as the engine found it ready; null for a client that starts none.</summary>
    public string? Program { get; init; }

    /// <summary>The names a child the run starts never inherits: every name the configuration appoints a value of the slot for.</summary>
    public IReadOnlyList<string> Withheld { get; init; } = [];
}
