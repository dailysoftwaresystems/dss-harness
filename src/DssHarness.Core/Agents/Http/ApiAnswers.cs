using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DssHarness.Core.Agents.Loop;
using DssHarness.Core.Json;

namespace DssHarness.Core.Agents.Http;

/// <summary>
/// How an API provider reads a request's answer into the loop's parts (<see cref="IChatProvider.SendAsync"/>), and what
/// befalls every API alike: the request posted, each event the connection yields read by the provider into its parts as
/// it comes - an event of no part heard all the same - and the answer ending in a turn or a failure, else failing as cut.
/// </summary>
public static class ApiAnswers
{
    /// <summary>How much of a refused body the reading of an API's error reads at a time, more where one of its values is longer.</summary>
    private const int ReadPiece = 16 * 1024;

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
    /// <paramref name="refused"/> classes it, a piece of a refused body still coming heard - and told as a notice every
    /// time it is to be told - and a network that failed and a stream cut each failing for now.
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
            ApiEvent.Refusing { Told: true } each => [new ChatPart.Notice(StillRefusing(each.Status, each.Bytes))],
            ApiEvent.Refusing => [new ChatPart.Heard()],
            ApiEvent.Refused each => [new ChatPart.Failed(refused(each))],
            ApiEvent.Unreached each => [new ChatPart.Failed(Unreached(each.Why))],
            ApiEvent.Cut each => [new ChatPart.Failed(Cut(each.Why))],
            _ => throw new InvalidOperationException($"'{happened.GetType().Name}' is no event a connection yields."),
        };
    }

    /// <summary>What is told of a refused body still coming: its status, and how much of it is kept so far.</summary>
    /// <param name="status">The status the API answered with.</param>
    /// <param name="bytes">How many bytes of its body are kept so far.</param>
    public static string StillRefusing(int status, long bytes)
        => $"the API answered {status.ToString(CultureInfo.InvariantCulture)} and its body is still coming: {bytes.ToString("N0", CultureInfo.InvariantCulture)} bytes of it kept so far";

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

    /// <summary>
    /// What an error the API sent part way through an answer's stream is said as: the error's kind and message - the data
    /// whole, on one line, where it holds none - and the request the API names.
    /// </summary>
    /// <param name="kind">The error's type or code; null where it names none.</param>
    /// <param name="message">The error's message; null where it holds none.</param>
    /// <param name="data">The data that holds the error.</param>
    /// <param name="request">The request the API names; null where it names none.</param>
    public static string StreamFailure(string? kind, string? message, string data, string? request = null)
        => $"the API's stream failed: {kind ?? "an error"}: {message ?? OneLine(data)}{Named(request)}";

    /// <summary>The object <paramref name="json"/> is; null where it reads as no JSON object.</summary>
    /// <param name="json">The text.</param>
    public static JsonObject? ObjectOf(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        try
        {
            return JsonDepth.ParseNode(json) as JsonObject;
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

    /// <summary>
    /// What a refusal whose body is no error the API wrote is said as: its status, and the body - nothing said, where it
    /// holds no byte; where it is kept in the request's records, how many bytes it holds and the file that keeps them
    /// whole, for a page of any size is no sentence; where it is held, the whole of it, on one line.
    /// </summary>
    /// <param name="status">The status.</param>
    /// <param name="body">The body, as it is kept.</param>
    public static string Answered(int status, ApiRefusedBody body)
    {
        ArgumentNullException.ThrowIfNull(body);

        var answered = $"the API answered {status.ToString(CultureInfo.InvariantCulture)}";

        return body.Bytes == 0 ? $"{answered} with nothing said"
            : body.File is { } file ? $"{answered} with {body.Bytes.ToString("N0", CultureInfo.InvariantCulture)} bytes that are no error of the API's own, kept whole in '{file}'"
            : $"{answered}: {OneLine(body.HeldText!)}";
    }

    /// <summary>A text on one line, whole: each run of line breaks a space, and the ends trimmed.</summary>
    /// <param name="text">The text.</param>
    public static string OneLine(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        return string.Join(' ', text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    /// <summary>
    /// The text of each member <paramref name="paths"/> names in the JSON object <paramref name="json"/> holds - a string's
    /// text, or a number as it is written - read as the JSON streams, never holding it whole: what an API's error says of
    /// itself, from a body of any size. A member of another kind is left out; so is every member where the body is no JSON
    /// object, or no JSON at all - a page of a proxy's - which says nothing of an API's error.
    /// </summary>
    /// <param name="json">The body, as a stream.</param>
    /// <param name="paths">Each member by its path of names from the object: <c>["error", "type"]</c>.</param>
    /// <returns>Each member found, by its path's names joined with dots.</returns>
    public static IReadOnlyDictionary<string, string> Members(Stream json, IReadOnlyList<IReadOnlyList<string>> paths)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(paths);

        var wanted = paths.Select(path => string.Join('.', path)).ToHashSet(StringComparer.Ordinal);
        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        var names = new List<string>();
        string? named = null;
        var began = false;
        var buffer = new byte[ReadPiece];
        var filled = 0;
        var final = false;
        var state = new JsonReaderState(JsonDepth.Reader);

        try
        {
            while (true)
            {
                if (!final)
                {
                    var read = json.Read(buffer, filled, buffer.Length - filled);
                    filled += read;
                    final = read == 0;
                }

                var reader = new Utf8JsonReader(buffer.AsSpan(0, filled), final, state);

                while (reader.Read())
                {
                    switch (reader.TokenType)
                    {
                        case JsonTokenType.StartObject or JsonTokenType.StartArray when !began:
                            if (reader.TokenType != JsonTokenType.StartObject)
                            {
                                return new Dictionary<string, string>(StringComparer.Ordinal);
                            }

                            began = true;
                            break;

                        case JsonTokenType.StartObject or JsonTokenType.StartArray:
                            // An array's items are named by no name a path holds, so nothing beneath one is ever wanted.
                            names.Add(named ?? "[]");
                            named = null;
                            break;

                        case JsonTokenType.EndObject or JsonTokenType.EndArray:
                            // The object's own end is all of it read: whatever follows it is no part of the error.
                            if (names.Count == 0)
                            {
                                return found;
                            }

                            names.RemoveAt(names.Count - 1);
                            break;

                        case JsonTokenType.PropertyName:
                            named = reader.GetString();
                            break;

                        case JsonTokenType.String or JsonTokenType.Number when named is not null:
                            var path = names.Count == 0 ? named : $"{string.Join('.', names)}.{named}";

                            if (wanted.Contains(path))
                            {
                                found[path] = reader.TokenType == JsonTokenType.String ? reader.GetString()! : Encoding.UTF8.GetString(reader.ValueSpan);
                            }

                            named = null;
                            break;

                        default:
                            if (!began)
                            {
                                return new Dictionary<string, string>(StringComparer.Ordinal);
                            }

                            named = null;
                            break;
                    }
                }

                if (final)
                {
                    // A body that ended before its object did says nothing whole of an error.
                    return new Dictionary<string, string>(StringComparer.Ordinal);
                }

                // What the reader did not take is a token that goes on in the next piece: kept at the buffer's head, and the
                // buffer grown where that token fills it, so a value of any length is read whole.
                var taken = (int)reader.BytesConsumed;
                state = reader.CurrentState;
                Buffer.BlockCopy(buffer, taken, buffer, 0, filled - taken);
                filled -= taken;

                if (filled == buffer.Length)
                {
                    Array.Resize(ref buffer, buffer.Length * 2);
                }
            }
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
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
