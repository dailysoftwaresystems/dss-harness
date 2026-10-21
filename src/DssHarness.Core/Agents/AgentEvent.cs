using System.Text.Json.Serialization;

namespace DssHarness.Core.Agents;

/// <summary>
/// One thing that happened while an agent worked a step, typed: what every client's wire becomes, in this tool's own
/// form and no provider's. Numbered by the pump that delivers it, never by its maker, and stamped when it was.
/// </summary>
/// <remarks>
/// <para>
/// The deltas (<see cref="TextDelta"/>, <see cref="ThinkingDelta"/>, <see cref="ToolCallInputDelta"/>) are what a
/// front end shows while a message is being written; the whole message that follows them holds everything they held,
/// so nothing is lost by a keeper of records leaving them out - which the stored stream does.
/// </para>
/// <para>
/// A member that may be absent is one whose parameter has a default: a line is written without its null members and read
/// back by the options that refuse a member the type needs and the line leaves out, so a nullable member with no default
/// would make the line it was null in unreadable.
/// </para>
/// <para>
/// Each member that carries text a person or a model wrote carries it whole: a record read "as best it can be" is how
/// a continuation ends up carrying the wrong words. Where text is kept it is masked by the keeper, never here.
/// </para>
/// </remarks>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(SessionStarted), "sessionStarted")]
[JsonDerivedType(typeof(MessageSent), "messageSent")]
[JsonDerivedType(typeof(TurnStarted), "turnStarted")]
[JsonDerivedType(typeof(TurnFinished), "turnFinished")]
[JsonDerivedType(typeof(TextDelta), "textDelta")]
[JsonDerivedType(typeof(ThinkingDelta), "thinkingDelta")]
[JsonDerivedType(typeof(AssistantMessage), "assistantMessage")]
[JsonDerivedType(typeof(ToolCallStarted), "toolCallStarted")]
[JsonDerivedType(typeof(ToolCallInputDelta), "toolCallInputDelta")]
[JsonDerivedType(typeof(ToolCallFinished), "toolCallFinished")]
[JsonDerivedType(typeof(PermissionDenied), "permissionDenied")]
[JsonDerivedType(typeof(WebRequested), "webRequested")]
[JsonDerivedType(typeof(UsageReported), "usageReported")]
[JsonDerivedType(typeof(WaitingToRetry), "waitingToRetry")]
[JsonDerivedType(typeof(TurnRestarted), "turnRestarted")]
[JsonDerivedType(typeof(FallbackStarted), "fallbackStarted")]
[JsonDerivedType(typeof(FallbackPassedOver), "fallbackPassedOver")]
[JsonDerivedType(typeof(OutputRepair), "outputRepair")]
[JsonDerivedType(typeof(Compacted), "compacted")]
[JsonDerivedType(typeof(HandoffAsked), "handoffAsked")]
[JsonDerivedType(typeof(HandoffAnswered), "handoffAnswered")]
[JsonDerivedType(typeof(PersonAsked), "personAsked")]
[JsonDerivedType(typeof(PersonAnswered), "personAnswered")]
[JsonDerivedType(typeof(Notice), "notice")]
[JsonDerivedType(typeof(RunStarted), "runStarted")]
[JsonDerivedType(typeof(RunFinished), "runFinished")]
public abstract record AgentEvent
{
    /// <summary>When the event was published.</summary>
    public DateTimeOffset At { get; init; }

    /// <summary>Where this event falls among the events of its run: assigned by the pump, never by its maker.</summary>
    public long Seq { get; init; }
}

/// <summary>
/// Where a launch's credential came from, as the first event names it: a variable of the key slot's file, or the
/// person's login. A value is never named.
/// </summary>
/// <param name="Kind">Which of the two: <c>EnvFile</c> or <c>Login</c>.</param>
/// <param name="Variable">The variable's name, where the kind is <c>EnvFile</c>; null where it is <c>Login</c>.</param>
public sealed record AgentCredentialSource(string Kind, string? Variable = null)
{
    /// <summary>The credential was a variable of the key slot's file.</summary>
    public const string EnvironmentFile = "EnvFile";

    /// <summary>The credential was the person's login.</summary>
    public const string Login = "Login";
}

/// <summary>Which of the messages the engine gives an agent while it works, as <see cref="MessageSent"/> names it.</summary>
public enum AgentMessageKind
{
    /// <summary>The first prompt of the run.</summary>
    FirstPrompt,

    /// <summary>A repair: the output's schema problems sent back.</summary>
    Repair,

    /// <summary>The handoff turn's question.</summary>
    HandoffQuestion,

    /// <summary>A note that no tool runs now.</summary>
    ToolsOffNote,

    /// <summary>What a plan update changes for its work.</summary>
    PlanUpdate,
}

/// <summary>Who ran a tool call, as the call's events name it.</summary>
public enum AgentToolRunner
{
    /// <summary>Claude Code's own tool.</summary>
    ClaudeCode,

    /// <summary>The provider's server.</summary>
    Provider,

    /// <summary>This harness.</summary>
    Harness,
}

/// <summary>Where the rule that denied a permission came from.</summary>
public enum AgentPermissionSource
{
    /// <summary>This tool's built-in rules.</summary>
    Builtin,

    /// <summary>The repository's configuration.</summary>
    Config,

    /// <summary>The agent's own definition.</summary>
    Agent,

    /// <summary>Claude Code's own guard.</summary>
    ClaudeCode,
}

/// <summary>What a turn or a run used, as reported or counted: tokens and searches, never money.</summary>
/// <param name="InputTokens">Input tokens not read from a cache and written to none.</param>
/// <param name="OutputTokens">Output tokens.</param>
/// <param name="CacheReadTokens">Tokens read from the provider's cache.</param>
/// <param name="CacheWriteTokens">Tokens written to the provider's cache to be kept five minutes - all of them where the provider bills cache writes alike.</param>
/// <param name="CacheWrite1hTokens">Tokens written to the provider's cache to be kept an hour, where it bills them apart: the Claude API's <c>cache_creation.ephemeral_1h_input_tokens</c>.</param>
/// <param name="Searches">The searches the provider ran.</param>
public sealed record AgentUsage(long InputTokens, long OutputTokens, long CacheReadTokens, long CacheWriteTokens, long CacheWrite1hTokens, long Searches)
{
    /// <summary>Nothing used.</summary>
    public static AgentUsage None { get; } = new(0, 0, 0, 0, 0, 0);

    /// <summary><paramref name="other"/> added to this, member by member.</summary>
    /// <param name="other">Another usage.</param>
    public AgentUsage Plus(AgentUsage other)
        => new(
            InputTokens + other.InputTokens,
            OutputTokens + other.OutputTokens,
            CacheReadTokens + other.CacheReadTokens,
            CacheWriteTokens + other.CacheWriteTokens,
            CacheWrite1hTokens + other.CacheWrite1hTokens,
            Searches + other.Searches);
}

/// <summary>One call an assistant message made, by its id.</summary>
/// <param name="Id">The call's id.</param>
/// <param name="Name">The tool's name.</param>
public sealed record AgentToolCall(string Id, string Name);

/// <summary>One question the agent asks the person.</summary>
/// <param name="Text">The question.</param>
/// <param name="Choices">The answers there are to pick from, where the question offers them.</param>
/// <param name="Recommended">The one it recommends, where it does.</param>
public sealed record AgentQuestion(string Text, IReadOnlyList<string> Choices, string? Recommended = null);

/// <summary>The launch began: what it runs as, where its credential came from, and what it was given.</summary>
/// <param name="Client">The client's key, as the configuration names it.</param>
/// <param name="Provider">The provider the step's model is of, where one is resolved.</param>
/// <param name="Tier">The tier the model came by, where it came by one.</param>
/// <param name="Model">The model the launch runs on.</param>
/// <param name="Effort">The effort it was asked for, where one is.</param>
/// <param name="SessionId">The session's own id, where the client names one.</param>
/// <param name="HandedConversation">Whether it was handed the stored conversation to append to, rather than beginning one.</param>
/// <param name="Credential">Where the launch's credential came from; a value is never named.</param>
/// <param name="SystemPromptHash">The digest of the system prompt as sent.</param>
/// <param name="Tools">The tools the launch was given, as it names them.</param>
/// <param name="Skills">The skills its prompt holds.</param>
/// <param name="ApiKeySource">Claude Code: the credential source its <c>system/init</c> named.</param>
/// <param name="SettingsFile">Claude Code: the user's settings file the launch read.</param>
/// <param name="SettingsDigest">Claude Code: the digest of that file.</param>
public sealed record SessionStarted(
    string Client,
    string Model,
    string SystemPromptHash,
    IReadOnlyList<string> Tools,
    IReadOnlyList<string> Skills,
    bool HandedConversation,
    string? Provider = null,
    string? Tier = null,
    string? Effort = null,
    string? SessionId = null,
    AgentCredentialSource? Credential = null,
    string? ApiKeySource = null,
    string? SettingsFile = null,
    string? SettingsDigest = null) : AgentEvent;

/// <summary>A message the engine gave the agent, whole: which of them it is beside it.</summary>
/// <param name="Text">The message.</param>
/// <param name="Which">Which of the messages the engine gives an agent while it works.</param>
public sealed record MessageSent(string Text, AgentMessageKind Which) : AgentEvent;

/// <summary>A turn of the agent's work began.</summary>
/// <param name="Turn">The turn's number, from one.</param>
public sealed record TurnStarted(int Turn) : AgentEvent;

/// <summary>A turn of the agent's work ended.</summary>
/// <param name="Turn">The turn's number.</param>
/// <param name="Model">The model that served it, as its own message named it.</param>
/// <param name="Usage">What the turn used.</param>
/// <param name="StopReason">Why it ended, as the client says it; null where it does not say.</param>
public sealed record TurnFinished(int Turn, string Model, AgentUsage Usage, string? StopReason = null) : AgentEvent;

/// <summary>A fragment of the answer's text as it was written. Shown while it is written; the whole message that follows holds it.</summary>
/// <param name="Text">The fragment.</param>
public sealed record TextDelta(string Text) : AgentEvent;

/// <summary>A fragment of the agent's thinking as it was written. Shown while it is written; the whole message that follows holds it.</summary>
/// <param name="Text">The fragment.</param>
public sealed record ThinkingDelta(string Text) : AgentEvent;

/// <summary>The agent's message, whole, when it has ended.</summary>
/// <param name="Text">Its text.</param>
/// <param name="Calls">Each call it made, by id.</param>
/// <param name="Thinking">Its thinking, as far as the provider shows it; null where it shows none.</param>
public sealed record AssistantMessage(string Text, IReadOnlyList<AgentToolCall> Calls, string? Thinking = null) : AgentEvent;

/// <summary>A tool call began.</summary>
/// <param name="Id">The call's id.</param>
/// <param name="Tool">The tool's name.</param>
/// <param name="Runner">Who runs it.</param>
public sealed record ToolCallStarted(string Id, string Tool, AgentToolRunner Runner) : AgentEvent;

/// <summary>A fragment of a tool call's input as it was written. Shown while it is written; the finished call holds it whole.</summary>
/// <param name="Id">The call's id.</param>
/// <param name="Fragment">The fragment.</param>
public sealed record ToolCallInputDelta(string Id, string Fragment) : AgentEvent;

/// <summary>A tool call ended.</summary>
/// <param name="Id">The call's id.</param>
/// <param name="Ok">Whether it succeeded.</param>
/// <param name="Duration">How long it ran.</param>
/// <param name="Input">Its input, whole.</param>
/// <param name="Result">Its result, masked and capped; the cap's overflow is <paramref name="ResultFile"/>.</param>
/// <param name="ResultFile">Where a result too large for the event is kept whole; null where it was not.</param>
public sealed record ToolCallFinished(string Id, bool Ok, TimeSpan Duration, string Input, string Result, string? ResultFile = null) : AgentEvent;

/// <summary>A permission the agent asked for was denied.</summary>
/// <param name="Tool">The tool it asked to run.</param>
/// <param name="Subject">What it asked to run it on.</param>
/// <param name="Rule">The rule that denied it.</param>
/// <param name="Source">Where that rule came from.</param>
/// <param name="Reason">Why it was denied.</param>
public sealed record PermissionDenied(string Tool, string Subject, string Rule, AgentPermissionSource Source, string Reason) : AgentEvent;

/// <summary>A search or a fetch the agent asked the web for.</summary>
/// <param name="QueryOrAddress">The search's query, or the fetch's address.</param>
/// <param name="Runner">Who ran it.</param>
/// <param name="Allowed">Whether it was allowed.</param>
/// <param name="Status">The status of what came back; null where nothing did.</param>
/// <param name="Size">The size of what came back; null where nothing did.</param>
public sealed record WebRequested(string QueryOrAddress, AgentToolRunner Runner, bool Allowed, int? Status = null, long? Size = null) : AgentEvent;

/// <summary>What the run has used, as the client reported it.</summary>
/// <param name="Turn">This turn's usage.</param>
/// <param name="Cumulative">The run's, as it stands.</param>
public sealed record UsageReported(AgentUsage Turn, AgentUsage Cumulative) : AgentEvent;

/// <summary>A failure that will be retried, after a delay.</summary>
/// <param name="Cause">What failed.</param>
/// <param name="Attempt">Which attempt this is.</param>
/// <param name="Of">Of how many.</param>
/// <param name="Delay">How long the wait is.</param>
/// <param name="Until">When the wait ends.</param>
public sealed record WaitingToRetry(string Cause, int Attempt, int Of, TimeSpan Delay, DateTimeOffset Until) : AgentEvent;

/// <summary>A partial turn was thrown away, and the turn begins again.</summary>
/// <param name="Why">Why it was thrown away.</param>
public sealed record TurnRestarted(string Why) : AgentEvent;

/// <summary>A fallback was taken: another model goes on with the work.</summary>
/// <param name="FromModel">The model that was serving.</param>
/// <param name="FromProvider">Its provider.</param>
/// <param name="FromClient">Its client.</param>
/// <param name="ToModel">The model that takes over.</param>
/// <param name="ToProvider">Its provider.</param>
/// <param name="ToClient">Its client.</param>
/// <param name="Why">Why: declined and the category, or the limit and when it lifts.</param>
/// <param name="How">How: the request sent again, or a new conversation from the record.</param>
/// <param name="Turn">The turn it happened on.</param>
/// <param name="ThrewAway">Whether a part of an answer was thrown away.</param>
/// <param name="CreditAsked">Whether a credit was asked for.</param>
public sealed record FallbackStarted(
    string FromModel,
    string FromProvider,
    string FromClient,
    string ToModel,
    string ToProvider,
    string ToClient,
    string Why,
    string How,
    int Turn,
    bool ThrewAway,
    bool CreditAsked) : AgentEvent;

/// <summary>An entry of the fallback that was not taken.</summary>
/// <param name="Model">The model not taken.</param>
/// <param name="Why">Why: it cannot be used on this machine, or the limit that was met covers it.</param>
public sealed record FallbackPassedOver(string Model, string Why) : AgentEvent;

/// <summary>The output did not read against its schema, and was sent back to be repaired.</summary>
/// <param name="Attempt">Which repair attempt this is.</param>
/// <param name="Problems">The schema problems sent back.</param>
public sealed record OutputRepair(int Attempt, IReadOnlyList<string> Problems) : AgentEvent;

/// <summary>The conversation was compacted.</summary>
/// <param name="Why">Why it was compacted.</param>
/// <param name="Left">The conversation it left.</param>
/// <param name="Began">The one it began.</param>
/// <param name="KeptWordForWord">How many iterations were kept word for word.</param>
/// <param name="Reduced">How many were reduced.</param>
/// <param name="SizeBefore">Its size before.</param>
/// <param name="SizeAfter">Its size after.</param>
public sealed record Compacted(string Why, string Left, string Began, int KeptWordForWord, int Reduced, long SizeBefore, long SizeAfter) : AgentEvent;

/// <summary>The engine raised the handoff: the run is pausing, and the agent is asked what it leaves.</summary>
/// <param name="Why">Why the run is pausing.</param>
/// <param name="Bound">How long the agent has to answer.</param>
public sealed record HandoffAsked(string Why, TimeSpan Bound) : AgentEvent;

/// <summary>The handoff turn ended.</summary>
/// <param name="Answered">Whether the agent answered.</param>
/// <param name="WhyNot">Why it did not, where it did not; null where it answered.</param>
public sealed record HandoffAnswered(bool Answered, string? WhyNot = null) : AgentEvent;

/// <summary>The agent asked the person.</summary>
/// <param name="Questions">Its questions, each with its choices and the one it recommends.</param>
public sealed record PersonAsked(IReadOnlyList<AgentQuestion> Questions) : AgentEvent;

/// <summary>The person answered.</summary>
/// <param name="Answer">The answer as it was given, or that the person said to decide.</param>
public sealed record PersonAnswered(string Answer) : AgentEvent;

/// <summary>Something said that is none of the above's: worth a record, worth no shape of its own.</summary>
/// <param name="Text">What was said.</param>
public sealed record Notice(string Text) : AgentEvent;

/// <summary>
/// The run of a step began: the first event of every run, written by the runner before a client is chosen - never by a
/// client - as <see cref="RunFinished"/> is its last.
/// </summary>
/// <remarks>
/// Every attempt of a step appends to the step's one stored stream, and its numbering begins again at one, so this is
/// where an attempt's own events begin: the newest attempt's stream, which a continuation and a handoff are written
/// from, is what follows the last of these, whether the attempt before it ended or was killed.
/// </remarks>
/// <param name="Attempt">Which attempt of the step the run is, from one.</param>
public sealed record RunStarted(int Attempt) : AgentEvent;

/// <summary>
/// The run of a step ended: the last event of every run, whatever the end - written by the runner, never by a client,
/// once the client has returned or thrown, and where no client could be taken.
/// </summary>
/// <param name="Status">How it ended, as its result says.</param>
/// <param name="Error">Why it failed, where it did; null where it did not.</param>
public sealed record RunFinished(AgentRunStatus Status, string? Error = null) : AgentEvent;
