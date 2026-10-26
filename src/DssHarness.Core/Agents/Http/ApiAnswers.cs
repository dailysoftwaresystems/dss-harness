using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using DssHarness.Core.Agents.Loop;

namespace DssHarness.Core.Agents.Http;

/// <summary>
/// How an API provider reads a request's answer into the loop's parts (<see cref="IChatProvider.SendAsync"/>), and what
/// befalls every API alike: the request posted, each event the connection yields read by the provider into its parts as
/// it comes - an event of no part heard all the same - and the answer ending in a turn or a failure, else failing as cut.
/// </summary>
public static class ApiAnswers
{
    /// <summary>The most of a body that is no error the API wrote said in a failure: the start of what came.</summary>
    public const int MostSaid = 300;

    /// <summary>Posts <paramref name="post"/> and streams the parts <paramref name="read"/> makes of each event, until one ends the answer.</summary>
    /// <param name="connection">How the request is posted and its stream read.</param>
    /// <param name="post">The request.</param>
    /// <param name="read">The parts an event comes to; a turn or a failure, which ends the answer, last.</param>
    /// <param name="cancellationToken">Ends the request where it stands.</param>
    public static async IAsyncEnumerable<ChatPart> ReadAsync(ApiConnection connection, ApiPost post, Func<ApiEvent, IEnumerable<ChatPart>> read, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(post);
        ArgumentNullException.ThrowIfNull(read);

        await foreach (var happened in connection.PostAsync(post, cancellationToken).ConfigureAwait(false))
        {
            var heard = false;

            foreach (var part in read(happened))
            {
                heard = true;
                yield return part;

                if (part is ChatPart.Answered or ChatPart.Failed)
                {
                    yield break;
                }
            }

            // An event of no part - a keep-alive, a block's end - is the answer not silent all the same.
            if (!heard)
            {
                yield return new ChatPart.Heard();
            }
        }

        yield return new ChatPart.Failed(new ChatFailure(ChatFailureKind.Transient, "the answer's stream ended before its message did"));
    }

    /// <summary>
    /// The parts an event of an answer comes to: an item's as <paramref name="item"/> reads it, a refusal's failure as
    /// <paramref name="refused"/> classes it, and a network that failed and a stream cut each failing for now.
    /// </summary>
    /// <param name="happened">The event.</param>
    /// <param name="item">The parts an item comes to.</param>
    /// <param name="refused">What a refusal is, as the loop acts on it.</param>
    public static IEnumerable<ChatPart> Parts(ApiEvent happened, Func<ApiEvent.Item, IEnumerable<ChatPart>> item, Func<ApiEvent.Refused, ChatFailure> refused)
    {
        ArgumentNullException.ThrowIfNull(happened);
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(refused);

        return happened switch
        {
            ApiEvent.Item each => item(each),
            ApiEvent.Refused each => [new ChatPart.Failed(refused(each))],
            ApiEvent.Unreached each => [new ChatPart.Failed(Unreached(each.Why))],
            ApiEvent.Cut each => [new ChatPart.Failed(Cut(each.Why))],
            _ => throw new InvalidOperationException($"'{happened.GetType().Name}' is no event a connection yields."),
        };
    }

    /// <summary>An answer failing, as the loop's part.</summary>
    /// <param name="kind">What the failure is to the loop.</param>
    /// <param name="message">What it is, in full.</param>
    public static ChatPart.Failed Failed(ChatFailureKind kind, string message) => new(new ChatFailure(kind, message));

    /// <summary>An answer whose stream began a call with no id or no tool named: failing part way, asked again.</summary>
    public static ChatPart.Failed CallUnnamed() => Failed(ChatFailureKind.Transient, "the answer's stream began a call with no id or no tool named");

    /// <summary>An answer whose stream ended before it began a message: failing part way, asked again.</summary>
    public static ChatPart.Failed NeverBegan() => Failed(ChatFailureKind.Transient, "the answer's stream ended a message it never began");

    /// <summary>An answer ended for a reason this tool does not know: nothing it could do with it mends it.</summary>
    /// <param name="reason">The API's word for why it ended; null where it said none.</param>
    public static ChatPart.Failed EndedUnknown(string? reason) => Failed(ChatFailureKind.Fatal, $"the answer ended for '{reason ?? "no reason"}', which this tool does not know");

    /// <summary>
    /// The part a turn put back together comes to, its end held to its calls for every API alike: a turn that holds calls
    /// and says it is done ended to call them, and is a turn of calls - stored as one with no answer to its calls, it would
    /// be a history no provider takes; one that ended to call tools and holds none, or holds a call whose input the stream
    /// left no JSON object, failed part way, and is asked again, never run. A turn cut off at its length is thrown away by
    /// the loop itself, its calls never run.
    /// </summary>
    /// <param name="turn">The turn, its end as the API's word for it says.</param>
    public static ChatPart Settled(ChatTurn turn)
    {
        ArgumentNullException.ThrowIfNull(turn);

        var stop = turn.Stop == ChatStop.Done && turn.Calls.Count > 0 ? ChatStop.Tools : turn.Stop;

        if (stop == ChatStop.Tools && turn.Calls.Count == 0)
        {
            return Failed(ChatFailureKind.Transient, "the answer ended to call tools and called none");
        }

        if (stop == ChatStop.Tools && turn.Calls.FirstOrDefault(call => ObjectOf(call.Input) is null) is { } broken)
        {
            return Failed(ChatFailureKind.Transient, $"the answer's call {broken.Id} of '{broken.Name}' came with an input that is no JSON object");
        }

        return new ChatPart.Answered(stop == turn.Stop ? turn : turn with { Stop = stop });
    }

    /// <summary>What a refusal whose body is an error the API wrote is said as: its status, the error's kind and message, and the request the API names.</summary>
    /// <param name="status">The status.</param>
    /// <param name="kind">The error's type or code.</param>
    /// <param name="message">The error's message.</param>
    /// <param name="request">The request the API names; null where it names none.</param>
    public static string Refusal(int status, string kind, string? message, string? request = null)
        => $"the API answered {status.ToString(CultureInfo.InvariantCulture)} {kind}: {message}{Named(request)}";

    /// <summary>What an error the API sent part way through an answer's stream is said as: the error's kind and message, the start of the data where it holds none, and the request the API names.</summary>
    /// <param name="kind">The error's type or code; null where it names none.</param>
    /// <param name="message">The error's message; null where it holds none.</param>
    /// <param name="data">The data that holds the error.</param>
    /// <param name="request">The request the API names; null where it names none.</param>
    public static string StreamFailure(string? kind, string? message, string data, string? request = null)
        => $"the API's stream failed: {kind ?? "an error"}: {message ?? Start(data)}{Named(request)}";

    /// <summary>The object <paramref name="json"/> is; null where it reads as no JSON object.</summary>
    /// <param name="json">The text.</param>
    public static JsonObject? ObjectOf(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        try
        {
            return JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>A request that never reached the API: the network failed, which is the service failing for now.</summary>
    /// <param name="why">What the network said.</param>
    public static ChatFailure Unreached(string why) => new(ChatFailureKind.Transient, $"the API could not be reached: {why}");

    /// <summary>An answer whose stream was cut part way: the service failing for now.</summary>
    /// <param name="why">What the network said.</param>
    public static ChatFailure Cut(string why) => new(ChatFailureKind.Transient, $"the answer's stream was cut: {why}");

    /// <summary>Whether a status is the service failing for now - a request timed out, a conflict, a server's error - whatever the API's error says.</summary>
    /// <param name="status">The status.</param>
    public static bool FailsForNow(int status) => status is 408 or 409 or >= 500;

    /// <summary>What a refusal whose body is no error the API wrote is said as: its status, and the start of what came.</summary>
    /// <param name="status">The status.</param>
    /// <param name="body">The body.</param>
    public static string Answered(int status, string body)
    {
        ArgumentNullException.ThrowIfNull(body);

        return $"the API answered {status.ToString(CultureInfo.InvariantCulture)}{(body.Length > 0 ? $": {Start(body)}" : " with nothing said")}";
    }

    /// <summary>The start of a body that is no error the API wrote, on one line.</summary>
    /// <param name="body">The body.</param>
    public static string Start(string body)
    {
        ArgumentNullException.ThrowIfNull(body);

        var line = string.Join(' ', body.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        return line.Length <= MostSaid ? line : $"{line[..MostSaid]}...";
    }

    /// <summary>The request a failure names, as it is said after it.</summary>
    private static string Named(string? request) => request is null ? string.Empty : $" (request {request})";

    /// <summary>The text <paramref name="value"/> holds as <paramref name="name"/>; null where it holds none.</summary>
    /// <param name="value">An object, or any value, which holds none.</param>
    /// <param name="name">The member's name.</param>
    public static string? Text(JsonElement value, string name)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var member) && member.ValueKind == JsonValueKind.String ? member.GetString() : null;

    /// <summary>The count <paramref name="value"/> holds as <paramref name="name"/>; null where it holds none.</summary>
    /// <param name="value">An object, or any value, which holds none.</param>
    /// <param name="name">The member's name.</param>
    public static long? Count(JsonElement value, string name)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var member) && member.ValueKind == JsonValueKind.Number && member.TryGetInt64(out var count) ? count : null;

    /// <summary>The object <paramref name="value"/> holds as <paramref name="name"/>; null where it holds none.</summary>
    /// <param name="value">An object, or any value, which holds none.</param>
    /// <param name="name">The member's name.</param>
    public static JsonElement? Member(JsonElement value, string name)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var member) && member.ValueKind == JsonValueKind.Object ? member : null;
}
