using System.Text.Json.Serialization;
using DssHarness.Core.Execution;

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

    /// <summary>A nudge to go on with an answer the provider cut off at its length.</summary>
    Continue,

    /// <summary>The step's envelope said in words, once the provider refused it as a schema.</summary>
    EnvelopeInWords,
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

/// <summary>
/// One question an agent asks the person or a step's output writes, in the shape every question holds (part 04): what is
/// asked, what stands in the way, what was tried, what depends on it, the choices, the one recommended and why.
/// </summary>
/// <param name="Question">What is asked.</param>
/// <param name="Problem">What is open or stands in the way.</param>
/// <param name="Tried">What was looked at or tried before asking.</param>
/// <param name="Means">What depends on the answer.</param>
/// <param name="Options">The choices, at least two, each a sentence that says what it leads to.</param>
/// <param name="Recommended">The one recommended, word for word as it stands among the choices.</param>
/// <param name="Why">Why, by the bar the agent was given.</param>
public sealed record AgentQuestion(string Question, string Problem, string Tried, string Means, IReadOnlyList<string> Options, string Recommended, string Why)
{
    /// <summary>This question with every text of it masked.</summary>
    /// <param name="mask">The run's mask.</param>
    public AgentQuestion MaskedBy(ILineMask mask)
    {
        ArgumentNullException.ThrowIfNull(mask);

        return new(mask.Redact(Question), mask.Redact(Problem), mask.Redact(Tried), mask.Redact(Means), [.. Options.Select(mask.Redact)], mask.Redact(Recommended), mask.Redact(Why));
    }

    /// <summary>
    /// What is wrong with this question, each problem named by the member it is in as the envelope names it -
    /// <c>options[1]: ...</c>, for the question's own path to go before - and empty where nothing is: every text written, at
    /// least <see cref="AgentEnvelope.FewestOptions"/> options, no two alike, and the one recommended among them, word for
    /// word. The one owner of what no schema says of a question, which the envelope's gate, a run's result and a run's
    /// questions hold it to alike.
    /// </summary>
    public IReadOnlyList<string> Problems()
    {
        // A record read from a file may lack what its type says it holds: what is not there is empty.
        var options = Options ?? [];
        var problems = new List<string>();

        foreach (var (member, text) in new[] { (nameof(Question), Question), (nameof(Problem), Problem), (nameof(Tried), Tried), (nameof(Means), Means) })
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                problems.Add(AgentEnvelope.Empty(member));
            }
        }

        var named = AgentEnvelope.Member(nameof(Options));

        if (options.Count < AgentEnvelope.FewestOptions)
        {
            problems.Add($"{named}: holds {Counted(options.Count)}, and a question offers at least {AgentEnvelope.FewestOptions}");
        }

        for (var at = 0; at < options.Count; at++)
        {
            if (string.IsNullOrWhiteSpace(options[at]))
            {
                problems.Add($"{named}[{at}]: is empty; each option is a sentence that says what it leads to");
            }
            else if (Enumerable.Range(0, at).FirstOrDefault(before => string.Equals(options[before], options[at], StringComparison.Ordinal), -1) is >= 0 and var first)
            {
                problems.Add($"{named}[{at}]: {AgentEnvelope.Quoted(options[at])} stands at {named}[{first}] already; each option is a choice of its own");
            }
        }

        if (string.IsNullOrWhiteSpace(Recommended))
        {
            problems.Add(AgentEnvelope.Empty(nameof(Recommended)));
        }
        else if (!options.Contains(Recommended, StringComparer.Ordinal))
        {
            problems.Add($"{AgentEnvelope.Member(nameof(Recommended))}: {AgentEnvelope.Quoted(Recommended)} is none of the {Counted(options.Count)}; recommend one of them, word for word");
        }

        if (string.IsNullOrWhiteSpace(Why))
        {
            problems.Add(AgentEnvelope.Empty(nameof(Why)));
        }

        return problems;
    }

    /// <summary>
    /// The first thing wrong with any of <paramref name="questions"/>, said by its place in them - <c>questions[1].why: ...</c>
    /// - or null where every one of them holds together.
    /// </summary>
    /// <param name="questions">The questions, in order.</param>
    public static string? ProblemIn(IReadOnlyList<AgentQuestion?> questions)
        => AgentEnvelope.ProblemIn(AgentEnvelope.Questions, questions, question => question.Problems());

    private static string Counted(int options) => $"{options} option{(options == 1 ? string.Empty : "s")}";
}

/// <summary>One thing an agent assumed where a person could have told it: what was open, what it took, and why.</summary>
/// <param name="About">What was open.</param>
/// <param name="Assumed">What it took it to be.</param>
/// <param name="Why">Why it took that.</param>
public sealed record AgentAssumption(string About, string Assumed, string Why)
{
    /// <summary>This assumption with every text of it masked.</summary>
    /// <param name="mask">The run's mask.</param>
    public AgentAssumption MaskedBy(ILineMask mask)
    {
        ArgumentNullException.ThrowIfNull(mask);

        return new(mask.Redact(About), mask.Redact(Assumed), mask.Redact(Why));
    }

    /// <summary>
    /// What is wrong with this assumption, each problem named by the member it is in as the envelope names it, for the
    /// assumption's own path to go before; empty where nothing is: every text written. The one owner of what no schema says
    /// of an assumption, which the envelope's gate and a run's result hold it to alike.
    /// </summary>
    public IReadOnlyList<string> Problems()
        => [.. new[] { (nameof(About), About), (nameof(Assumed), Assumed), (nameof(Why), Why) }
            .Where(member => string.IsNullOrWhiteSpace(member.Item2))
            .Select(member => AgentEnvelope.Empty(member.Item1))];

    /// <summary>
    /// The first thing wrong with any of <paramref name="assumptions"/>, said by its place in them -
    /// <c>assumptions[1].why: ...</c> - or null where every one of them holds together.
    /// </summary>
    /// <param name="assumptions">The assumptions, in order.</param>
    public static string? ProblemIn(IReadOnlyList<AgentAssumption?> assumptions)
        => AgentEnvelope.ProblemIn(AgentEnvelope.Assumptions, assumptions, assumption => assumption.Problems());
}

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
/// <param name="Hooks">Claude Code: whether the user's hooks run in the launch - <c>off</c> in every launch this tool makes.</param>
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
    string? SettingsDigest = null,
    string? Hooks = null) : AgentEvent;

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

/// <summary>A partial turn was thrown away - what it streamed is none of the run's - and the turn begins again unless the run ends.</summary>
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
/// <param name="FromKey">The name of the key the serving entry signed in with, where it signed in with one; never its value.</param>
/// <param name="ToKey">The name of the key the entry that takes over signs in with, where it signs in with one.</param>
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
    bool CreditAsked,
    string? FromKey = null,
    string? ToKey = null) : AgentEvent
{
    /// <summary>
    /// The fallback a refused key makes, on every client: the same model of the same provider and client, on the next of
    /// the run's keys, each named and never valued.
    /// </summary>
    /// <param name="model">The model.</param>
    /// <param name="provider">Its provider.</param>
    /// <param name="client">Its client.</param>
    /// <param name="refused">The name of the key refused.</param>
    /// <param name="next">The name of the key that takes over.</param>
    /// <param name="said">What the refusal said, masked.</param>
    /// <param name="how">How the run goes on with the next key.</param>
    /// <param name="turn">The turn it happened on.</param>
    /// <param name="threwAway">Whether a part of an answer was thrown away.</param>
    public static FallbackStarted KeyRefused(string model, string provider, string client, string refused, string next, string said, string how, int turn, bool threwAway)
        => new(model, provider, client, model, provider, client, $"key refused: {said}", how, turn, threwAway, CreditAsked: false, refused, next);
}

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
public sealed record HandoffAnswered(bool Answered, string? WhyNot = null) : AgentEvent
{
    /// <summary>What a run's <paramref name="handoff"/> tells as the event that ends its handoff turn, on every client.</summary>
    /// <param name="handoff">The run's handoff.</param>
    public static HandoffAnswered Of(AgentHandoff handoff)
    {
        ArgumentNullException.ThrowIfNull(handoff);

        return new(handoff.Answered, handoff.WhyNone);
    }
}

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
