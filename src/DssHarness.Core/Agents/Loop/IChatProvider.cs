using DssHarness.Core.Agents.Runtime;

namespace DssHarness.Core.Agents.Loop;

/// <summary>
/// One provider's side of the loop the two API clients share (<see cref="ToolLoop"/>): the request's body, the stream it
/// answers with and how its parts become a turn, its own form of a conversation's messages and tools, and what each of its
/// failures is. The loop owns everything else - the budgets, the policy and the tools, the events, the stored
/// conversation, the retries and the stops - so a provider knows nothing of a step.
/// </summary>
/// <remarks>
/// Every message a provider makes or hands back is one line of JSON in its own form, holding no line break: the loop
/// stores it as it is and sends it back as it is, byte for byte, with every later request of the conversation.
/// </remarks>
public interface IChatProvider
{
    /// <summary>
    /// A token of each kind every request of this provider is billed by - its input, its output, the cache it reads and
    /// the cache it writes - as one of each: what a step held to a budget needs a price for before it starts.
    /// </summary>
    AgentUsage Uses { get; }

    /// <summary>The tools a conversation begins with, in the provider's own form and in the order given: what every request of it sends.</summary>
    /// <param name="tools">The tools, as the runtime offers them: by name.</param>
    string Tools(IReadOnlyList<AgentToolDefinition> tools);

    /// <summary>A message of the engine's to the agent, in the provider's own form.</summary>
    /// <param name="text">The message, whole.</param>
    string UserMessage(string text);

    /// <summary>
    /// The messages that answer <paramref name="turn"/>, in the provider's own form: each of its calls' answers, in the
    /// order the calls were made, and then each message of the engine's, in the order it was given - the agent reads
    /// them all before it goes on.
    /// </summary>
    /// <param name="turn">The turn whose calls are answered.</param>
    /// <param name="answers">Each call's answer, one for each of the turn's calls, in their order.</param>
    /// <param name="messages">The engine's messages to the agent, given with the answers; empty where there are none.</param>
    IReadOnlyList<string> Answers(ChatTurn turn, IReadOnlyList<ChatAnswer> answers, IReadOnlyList<string> messages);

    /// <summary>
    /// Sends one request and streams what it answers, as it comes: its parts, then a turn (<see cref="ChatPart.Answered"/>)
    /// or a failure (<see cref="ChatPart.Failed"/>), which is the last part. A provider throws for nothing it can tell:
    /// what it raises is a defect of its own, which fails the run.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">Ends the request where it stands: the loop has left it.</param>
    IAsyncEnumerable<ChatPart> SendAsync(ChatRequest request, CancellationToken cancellationToken);
}

/// <summary>One request of a conversation, whole: everything a provider sends.</summary>
/// <param name="Model">The model's code, as it is sent.</param>
/// <param name="Effort">The effort the step asked for, where it asked for one.</param>
/// <param name="System">The system prompt the conversation began with.</param>
/// <param name="Tools">The tools the conversation began with, in the provider's own form.</param>
/// <param name="Messages">Every message of the conversation, in order, each as it was stored: what was sent and received.</param>
/// <param name="MaxOutputTokens">The most the answer may hold.</param>
/// <param name="Key">The key the request is signed with; null where the run was given none.</param>
/// <param name="Output">The output the run is to produce, where its step names one.</param>
/// <param name="Credit">
/// Whether the step has a model after this one to go on with, so a request the model declines is to leave it a credit,
/// where the provider gives one (<see cref="ChatTurn.Credit"/>).
/// </param>
/// <param name="ShortenedBefore">
/// Where the provider began sending the conversation shortened (<see cref="ChatPart.Shortened"/>): the messages before
/// this one go out in the provider's short form, as every request of the conversation since sent them; null where it
/// never began.
/// </param>
public sealed record ChatRequest(
    string Model,
    string? Effort,
    string System,
    string Tools,
    IReadOnlyList<string> Messages,
    long MaxOutputTokens,
    AgentKey? Key,
    AgentOutput? Output,
    bool Credit = false,
    int? ShortenedBefore = null);

/// <summary>One part of what a request answers, in the order it came.</summary>
public abstract record ChatPart
{
    /// <summary>
    /// Something of the answer came that is none of its parts - the provider's keep-alive, a block's end, an event of no
    /// part - so the answer is not silent: the stall bound starts again from it.
    /// </summary>
    public sealed record Heard : ChatPart;

    /// <summary>
    /// What the request has used so far, whole, as the provider last said it: an answer that began has read its input,
    /// which is billed whether or not the request ends in a turn - so the loop counts it where the request ends without
    /// one, failed, gone silent or left. A turn carries its own.
    /// </summary>
    /// <param name="SoFar">The usage, as it stands.</param>
    public sealed record Used(AgentUsage SoFar) : ChatPart;

    /// <summary>The answer began, written by <paramref name="Model"/>: held to the model asked for at once.</summary>
    /// <param name="Model">The model the provider says writes it.</param>
    public sealed record Began(string Model) : ChatPart;

    /// <summary>Something the provider says of the request that is none of the answer's: what it cleared of the context, what it sent shortened.</summary>
    /// <param name="Message">What it says.</param>
    public sealed record Notice(string Message) : ChatPart;

    /// <summary>
    /// The provider sends the messages before <paramref name="Before"/> shortened from this request on, to keep the
    /// conversation within its model's context: the loop stores the point with the conversation and gives it with every
    /// later request of it (<see cref="ChatRequest.ShortenedBefore"/>), a step that goes on in it among them, so each
    /// sends what the last did and the cache misses once. Nothing stored is changed: only what is sent is shortened.
    /// </summary>
    /// <param name="Before">The first message sent whole, by its place in the conversation.</param>
    public sealed record Shortened(int Before) : ChatPart;

    /// <summary>A fragment of the answer's text.</summary>
    /// <param name="Fragment">The fragment.</param>
    public sealed record Text(string Fragment) : ChatPart;

    /// <summary>A fragment of the model's thinking.</summary>
    /// <param name="Fragment">The fragment.</param>
    public sealed record Thinking(string Fragment) : ChatPart;

    /// <summary>A call of a tool began.</summary>
    /// <param name="Id">The call's id.</param>
    /// <param name="Name">The tool's name.</param>
    public sealed record CallBegan(string Id, string Name) : ChatPart;

    /// <summary>A fragment of a call's input.</summary>
    /// <param name="Id">The call's id.</param>
    /// <param name="Fragment">The fragment.</param>
    public sealed record CallInput(string Id, string Fragment) : ChatPart;

    /// <summary>The request was answered: the turn, whole. The last part.</summary>
    /// <param name="Turn">The turn.</param>
    public sealed record Answered(ChatTurn Turn) : ChatPart;

    /// <summary>The request failed. The last part.</summary>
    /// <param name="Failure">What failed, as the provider classes it.</param>
    public sealed record Failed(ChatFailure Failure) : ChatPart;
}

/// <summary>How a turn ended, as the loop acts on it.</summary>
public enum ChatStop
{
    /// <summary>The agent answered: its message calls no tool.</summary>
    Done,

    /// <summary>The agent called tools, which run before it goes on.</summary>
    Tools,

    /// <summary>The answer was cut off at its most tokens.</summary>
    CutOff,

    /// <summary>
    /// The provider paused the turn, to be sent again with nothing added: its message is sent back as the last, and stored
    /// with what goes on from it.
    /// </summary>
    Paused,

    /// <summary>The model declined to answer.</summary>
    Declined,

    /// <summary>The model's context could hold no more.</summary>
    ContextFull,
}

/// <summary>One turn of the agent's, as a request answered it.</summary>
/// <param name="Stop">How it ended, as the loop acts on it.</param>
/// <param name="Message">
/// The agent's message, in the provider's own form, as it was received: what the conversation stores and every later
/// request sends back, byte for byte.
/// </param>
/// <param name="Model">The model that wrote it, as the answer named it.</param>
/// <param name="Text">Its text.</param>
/// <param name="Thinking">Its thinking, as far as the provider shows it; null where it shows none.</param>
/// <param name="Calls">Each call it made, in order - a call cut off part way among them, which the loop never runs and never stores.</param>
/// <param name="Usage">What the request used.</param>
/// <param name="StopReason">Why it ended, in the provider's own word.</param>
/// <param name="Category">What a declined request was of, where the provider names it; null otherwise.</param>
/// <param name="Credit">The credit a declined request left the step's next model, where the request asked for one and the provider gave it; null otherwise.</param>
public sealed record ChatTurn(
    ChatStop Stop,
    string Message,
    string Model,
    string Text,
    string? Thinking,
    IReadOnlyList<ChatCall> Calls,
    AgentUsage Usage,
    string StopReason,
    string? Category = null,
    AgentFallbackCredit? Credit = null);

/// <summary>One call a turn made.</summary>
/// <param name="Id">The call's id, as the provider named it.</param>
/// <param name="Name">The tool it calls.</param>
/// <param name="Input">Its input, as JSON.</param>
public sealed record ChatCall(string Id, string Name, string Input);

/// <summary>A call's answer, as the agent is given it.</summary>
/// <param name="Id">The call's id.</param>
/// <param name="Ok">Whether it did what it was asked; false for a refusal and a failure, whose text says why.</param>
/// <param name="Text">What the agent reads.</param>
public sealed record ChatAnswer(string Id, bool Ok, string Text);

/// <summary>What a request's failure is, as the loop acts on it.</summary>
public enum ChatFailureKind
{
    /// <summary>The service failed for now - the network, a server's error, an overload, a stream cut - and is asked again.</summary>
    Transient,

    /// <summary>
    /// A rate limit the client waits out, within its tries and its longest wait; past them, the account is at its limit -
    /// the limit of the request's model on the request's key.
    /// </summary>
    RateLimited,

    /// <summary>The account is at its limit at once: what it may use is used, or its balance is spent - the limit of the request's key, whatever model it signs.</summary>
    AccountLimit,

    /// <summary>The provider refused the key the request was signed with.</summary>
    KeyRefused,

    /// <summary>The model declined the request.</summary>
    Declined,

    /// <summary>The model's context could hold no more.</summary>
    ContextFull,

    /// <summary>
    /// The provider will not serve the request's model to the key's account - one it does not give that account, one the
    /// account is not set up for - which no try mends and nothing of the request's would: where the run had no answer yet,
    /// it never began.
    /// </summary>
    Unserved,

    /// <summary>A failure no try mends, said in the provider's own words: a plan that does not cover the call, a history the provider will not read, an answer this tool cannot put back together.</summary>
    Fatal,
}

/// <summary>A request's failure, as the provider classes it.</summary>
/// <param name="Kind">What it is.</param>
/// <param name="Message">What the provider said, in its own words.</param>
/// <param name="RetryAfter">How long the provider said to wait before asking again, where it said.</param>
/// <param name="Lifts">When the account's limit lifts, where the provider said.</param>
/// <param name="BalanceSpent">Whether what is spent is the account's balance, which time alone does not give back.</param>
/// <param name="Category">What a declined request was of, where the provider names it.</param>
public sealed record ChatFailure(
    ChatFailureKind Kind,
    string Message,
    TimeSpan? RetryAfter = null,
    DateTimeOffset? Lifts = null,
    bool BalanceSpent = false,
    string? Category = null);
