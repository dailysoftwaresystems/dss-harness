using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using DssHarness.Core.Agents;
using DssHarness.Core.Agents.Loop;
using DssHarness.Core.Agents.Runtime;

namespace DssHarness.Tests;

/// <summary>
/// A provider of the loop's that answers as a test scripts it, in process: each request is taken from the queue of
/// replies in order, and every request it was sent is kept. Its messages are its own small form - a user message
/// <c>{"role":"user","content":...}</c>, an answer <c>{"role":"tool","id":...,"ok":...,"content":...}</c> - so a test reads
/// what the loop sent as it was sent.
/// </summary>
internal sealed class FakeChat : IChatProvider
{
    /// <summary>The model every turn of a script is written by unless it says another.</summary>
    public const string Model = "claude-opus-5-5";

    private readonly Queue<Func<ChatRequest, CancellationToken, IAsyncEnumerable<ChatPart>>> _replies = new();
    private readonly List<ChatRequest> _requests = [];
    private readonly Lock _gate = new();

    /// <summary>Every request the loop sent, in order.</summary>
    public IReadOnlyList<ChatRequest> Requests
    {
        get
        {
            lock (_gate)
            {
                return [.. _requests];
            }
        }
    }

    /// <summary>Answers the next request with <paramref name="turn"/>: begun by its model, its text streamed first.</summary>
    public FakeChat Answer(ChatTurn turn)
        => Reply([new ChatPart.Began(turn.Model), .. turn.Text.Length > 0 ? [new ChatPart.Text(turn.Text)] : Array.Empty<ChatPart>(), .. turn.Calls.Select(call => new ChatPart.CallBegan(call.Id, call.Name)), new ChatPart.Answered(turn)]);

    /// <summary>Answers the next request with <paramref name="failure"/>, after <paramref name="written"/> where it gives some.</summary>
    public FakeChat Fail(ChatFailure failure, string? written = null)
        => Reply([.. written is null ? Array.Empty<ChatPart>() : [new ChatPart.Began(Model), new ChatPart.Text(written)], new ChatPart.Failed(failure)]);

    /// <summary>Answers the next request with <paramref name="parts"/>, as they are.</summary>
    public FakeChat Reply(params ChatPart[] parts) => Reply((_, cancellationToken) => Streamed(parts, cancellationToken));

    /// <summary>Answers the next request as <paramref name="reply"/> does.</summary>
    public FakeChat Reply(Func<ChatRequest, CancellationToken, IAsyncEnumerable<ChatPart>> reply)
    {
        lock (_gate)
        {
            _replies.Enqueue(reply);
        }

        return this;
    }

    /// <summary>Answers the next request with <paramref name="written"/>, then nothing more until the request is left.</summary>
    public FakeChat Hang(string? written = null, Action? whenHanging = null) => Reply((_, cancellationToken) => Hanging(written, whenHanging, cancellationToken));

    public string Tools(IReadOnlyList<AgentToolDefinition> tools)
        => new JsonArray([.. tools.Select(tool => (JsonNode)new JsonObject { ["name"] = tool.Name })]).ToJsonString();

    public string UserMessage(string text) => User(text);

    public IReadOnlyList<string> Answers(ChatTurn turn, IReadOnlyList<ChatAnswer> answers, IReadOnlyList<string> messages)
        => [.. answers.Select(answer => new JsonObject { ["role"] = "tool", ["id"] = answer.Id, ["ok"] = answer.Ok, ["content"] = answer.Text }.ToJsonString()), .. messages.Select(User)];

    public IAsyncEnumerable<ChatPart> SendAsync(ChatRequest request, CancellationToken cancellationToken)
    {
        Func<ChatRequest, CancellationToken, IAsyncEnumerable<ChatPart>> reply;

        lock (_gate)
        {
            _requests.Add(request);
            reply = _replies.Count > 0 ? _replies.Dequeue() : throw new InvalidOperationException($"request {_requests.Count} was sent, and no reply to it was scripted");
        }

        return reply(request, cancellationToken);
    }

    /// <summary>A user message, in the fake's form.</summary>
    public static string User(string text) => new JsonObject { ["role"] = "user", ["content"] = text }.ToJsonString();

    /// <summary>An agent's message, in the fake's form.</summary>
    public static string Assistant(string text, IReadOnlyList<ChatCall> calls)
        => new JsonObject
        {
            ["role"] = "assistant",
            ["content"] = text,
            ["calls"] = new JsonArray([.. calls.Select(call => (JsonNode)new JsonObject { ["id"] = call.Id, ["name"] = call.Name, ["input"] = JsonNode.Parse(call.Input) })]),
        }.ToJsonString();

    /// <summary>An answer of the agent's that calls no tool.</summary>
    public static ChatTurn Said(string text, AgentUsage? usage = null, string model = Model, ChatStop stop = ChatStop.Done, string? category = null)
        => new(stop, Assistant(text, []), model, text, null, [], usage ?? Used(), stop.ToString(), category);

    /// <summary>A turn of the agent's that calls <paramref name="calls"/>.</summary>
    public static ChatTurn Calling(params ChatCall[] calls) => Calling(null, calls);

    /// <summary>A turn of the agent's that calls <paramref name="calls"/>, using <paramref name="usage"/>.</summary>
    public static ChatTurn Calling(AgentUsage? usage, params ChatCall[] calls)
        => new(ChatStop.Tools, Assistant(string.Empty, calls), Model, string.Empty, null, calls, usage ?? Used(), "tool_use");

    /// <summary>A turn cut off at its length, calling <paramref name="calls"/> where it gives some.</summary>
    public static ChatTurn CutOff(string text, params ChatCall[] calls) => CutOff(Used(), text, calls);

    /// <summary>A turn cut off at its length, using <paramref name="usage"/>, calling <paramref name="calls"/> where it gives some.</summary>
    public static ChatTurn CutOff(AgentUsage usage, string text, params ChatCall[] calls)
        => new(ChatStop.CutOff, Assistant(text, calls), Model, text, null, calls, usage, "max_tokens");

    /// <summary>A call of <paramref name="tool"/> with <paramref name="input"/>.</summary>
    public static ChatCall Call(string id, string tool, params (string Name, JsonNode? Value)[] input)
        => new(id, tool, new JsonObject([.. input.Select(member => KeyValuePair.Create(member.Name, member.Value))]).ToJsonString());

    /// <summary>What a request used: <paramref name="input"/> tokens in, <paramref name="output"/> out.</summary>
    public static AgentUsage Used(long input = 100, long output = 10) => new(input, output, 0, 0, 0, 0);

    /// <summary>The text of a message in the fake's form.</summary>
    public static string ContentOf(string message) => JsonNode.Parse(message)!["content"]!.GetValue<string>();

    private static async IAsyncEnumerable<ChatPart> Streamed(ChatPart[] parts, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var part in parts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return part;
        }
    }

    private static async IAsyncEnumerable<ChatPart> Hanging(string? written, Action? whenHanging, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (written is not null)
        {
            yield return new ChatPart.Text(written);
        }

        whenHanging?.Invoke();
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        yield break;
    }
}
