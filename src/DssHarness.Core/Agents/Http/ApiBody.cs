using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DssHarness.Core.Agents.Http;

/// <summary>A member of a request's body the tool sends itself or never sends, by its path: what no client's <c>requestExtras</c> may set, and why.</summary>
/// <param name="Path">The member's path from the body's top, one name for each level.</param>
/// <param name="Why">Why it is refused, as the refusal says it after <c>is refused: </c>.</param>
public sealed record OwnedMember(IReadOnlyList<string> Path, string Why = OwnedMember.SentByTheTool)
{
    /// <summary>Why a member the tool sends itself is refused.</summary>
    public const string SentByTheTool = "the tool sends it itself";
}

/// <summary>
/// How an API client writes a request's body (part 03, "How the two API clients talk to their APIs"): compact JSON, the
/// conversation's tools and messages written as they were stored, byte for byte, and the client's <c>requestExtras</c>
/// merged in - the one owner of what <c>requestExtras</c> may set, each client naming the members it owns.
/// </summary>
public static class ApiBody
{
    /// <summary>The member of a body written as the conversation's stored tools.</summary>
    public const string Tools = "tools";

    /// <summary>The member of a body written as the conversation's messages.</summary>
    public const string Messages = "messages";

    /// <summary>How the clients write JSON: compact, and escaping only what JSON must, for none of it is put in a page.</summary>
    public static JsonSerializerOptions Json { get; } = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>
    /// What is wrong with a client's <c>requestExtras</c>, each said in full: an object, whose members set nothing the
    /// client owns, and whose member under one the client sends as an object is an object too.
    /// </summary>
    /// <param name="extras">The client's <c>requestExtras</c>, as the configuration holds it.</param>
    /// <param name="where">Where it is, as a refusal names it: <c>agentClients 'claude-api' requestExtras</c>.</param>
    /// <param name="owned">Every member the client sends itself or never sends.</param>
    public static IEnumerable<string> ExtrasProblems(JsonElement extras, string where, IReadOnlyList<OwnedMember> owned)
    {
        ArgumentNullException.ThrowIfNull(owned);

        if (extras.ValueKind != JsonValueKind.Object)
        {
            yield return $"{where} is {Said(extras.ValueKind)}, and the fields merged into every request's body are an object's members";
            yield break;
        }

        foreach (var problem in MemberProblems(extras, [], where, owned))
        {
            yield return problem;
        }
    }

    /// <summary>
    /// The body <paramref name="body"/> says, as JSON: each of its members in order, its <see cref="Tools"/> member written
    /// as <paramref name="tools"/> and its <see cref="Messages"/> member as <paramref name="messages"/>, each byte for byte,
    /// and <paramref name="extras"/> merged into it member by member.
    /// </summary>
    /// <param name="body">The members the client sends, a <see cref="Tools"/> or <see cref="Messages"/> member standing in its place.</param>
    /// <param name="tools">The conversation's tools as stored; read only where the body holds a <see cref="Tools"/> member.</param>
    /// <param name="messages">The messages, each one JSON value as it is sent.</param>
    /// <param name="extras">The client's <c>requestExtras</c>; null where it adds none.</param>
    public static byte[] Write(JsonObject body, string? tools, IReadOnlyList<string> messages, JsonElement? extras)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(messages);

        if (extras is { } added)
        {
            Merge(body, added, []);
        }

        using var stream = new MemoryStream();

        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = Json.Encoder }))
        {
            writer.WriteStartObject();

            foreach (var (name, node) in body)
            {
                writer.WritePropertyName(name);

                switch (name)
                {
                    case Tools:
                        writer.WriteRawValue(tools ?? throw new InvalidOperationException("A body that sends the conversation's tools was given none."));
                        break;
                    case Messages:
                        writer.WriteStartArray();

                        foreach (var message in messages)
                        {
                            writer.WriteRawValue(message);
                        }

                        writer.WriteEndArray();
                        break;
                    default:
                        // A member requestExtras gives as null is sent as null.
                        if (node is null)
                        {
                            writer.WriteNullValue();
                        }
                        else
                        {
                            node.WriteTo(writer);
                        }

                        break;
                }
            }

            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    /// <summary>Whether <paramref name="json"/> is the empty list: a conversation that has no tools.</summary>
    /// <param name="json">The tools, as stored.</param>
    public static bool IsEmptyList(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        using var document = JsonDocument.Parse(json);

        return document.RootElement is { ValueKind: JsonValueKind.Array } list && list.GetArrayLength() == 0;
    }

    /// <summary>
    /// Merges <paramref name="extras"/> into <paramref name="body"/>, member by member: an object under one the body holds
    /// as an object merged into it, any other added. One the body holds already is refused where the configuration is
    /// read (<see cref="ExtrasProblems"/>), so meeting one here is a defect.
    /// </summary>
    private static void Merge(JsonObject body, JsonElement extras, IReadOnlyList<string> path)
    {
        foreach (var member in extras.EnumerateObject())
        {
            if (body[member.Name] is JsonObject inner && member.Value.ValueKind == JsonValueKind.Object)
            {
                Merge(inner, member.Value, [.. path, member.Name]);
            }
            else if (body.ContainsKey(member.Name))
            {
                throw new InvalidOperationException($"requestExtras.{string.Join(".", path.Append(member.Name))} sets what the request sends itself, which the configuration's reader refuses.");
            }
            else
            {
                // From the value as it was read, never its text: config.json may hold comments and trailing commas within it.
                body[member.Name] = JsonSerializer.SerializeToNode(member.Value);
            }
        }
    }

    /// <summary>Each member of <paramref name="extras"/>, under <paramref name="path"/>, that sets what the client owns.</summary>
    private static IEnumerable<string> MemberProblems(JsonElement extras, IReadOnlyList<string> path, string where, IReadOnlyList<OwnedMember> owned)
    {
        foreach (var member in extras.EnumerateObject())
        {
            IReadOnlyList<string> at = [.. path, member.Name];
            var named = $"{where}.{string.Join(".", at)}";

            if (owned.FirstOrDefault(each => each.Path.SequenceEqual(at, StringComparer.Ordinal)) is { } refused)
            {
                yield return $"{named} is refused: {refused.Why}";
            }
            else if (owned.Any(each => each.Path.Count > at.Count && each.Path.Take(at.Count).SequenceEqual(at, StringComparer.Ordinal)))
            {
                if (member.Value.ValueKind != JsonValueKind.Object)
                {
                    yield return $"{named} is {Said(member.Value.ValueKind)}, and the tool sends an object there, which its members are merged into";
                    continue;
                }

                foreach (var problem in MemberProblems(member.Value, at, where, owned))
                {
                    yield return problem;
                }
            }
        }
    }

    /// <summary>A JSON value's kind, as a refusal says it.</summary>
    private static string Said(JsonValueKind kind)
        => kind switch
        {
            JsonValueKind.Array => "a list",
            JsonValueKind.String => "a string",
            JsonValueKind.Number => "a number",
            JsonValueKind.True or JsonValueKind.False => "true or false",
            JsonValueKind.Null => "null",
            _ => "an object",
        };
}
