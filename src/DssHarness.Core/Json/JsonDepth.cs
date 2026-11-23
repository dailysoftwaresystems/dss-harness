using System.Text.Json;
using System.Text.Json.Nodes;

namespace DssHarness.Core.Json;

/// <summary>
/// The one owner of how deep JSON is read and written: at any depth. Left to itself, System.Text.Json reads 64 levels of
/// nesting and writes 1,000, and refuses a document past either for its depth alone - a number no standard sets (RFC 8259
/// §9 leaves it to the reader) - so an agent's answer, a page from the web or a record nested deeper would be refused, or
/// thrown on, though nothing in it is wrong. Every reading and every writing of JSON takes its options from here.
/// </summary>
/// <remarks>
/// <para>
/// What .NET 10 does with these options, measured of documents 100,000 deep on a thread of a megabyte's stack. The
/// <see cref="Utf8JsonReader"/> reads one in time linear in its length, keeping its nesting on the heap. A
/// <see cref="JsonDocument"/> is parsed on the heap too, never the stack, but it finds where each array or object it closes
/// began by looking back over every row after that start, so a document nested n deep is parsed in time quadratic in n:
/// seconds at 100,000 deep. Going down its <see cref="JsonElement"/>s, their raw text, their clone, and their writing
/// through a <see cref="Utf8JsonWriter"/> of <see cref="Writer"/> are iterative; <see cref="JsonElement.DeepEquals"/> goes
/// one call per level and throws an <see cref="InsufficientExecutionStackException"/> on such a document, so it is kept
/// out of the deep path.
/// </para>
/// <para>
/// A <see cref="JsonNode"/> read from text stands on a <see cref="JsonDocument"/> and makes each node's children only when
/// they are first asked for. A node made with no options asks its parent for them, and that parent its own, one call per
/// level up, so going down a document so read overflows the stack at 100,000 deep: <see cref="ParseNode"/> gives every
/// node its options, and no node asks. Each child .NET makes is checked for a cycle by a walk up its parents, so making
/// the nodes of a chain n deep takes time quadratic in n, though no stack. <see cref="JsonNode.GetPath"/>,
/// <see cref="JsonNode.DeepEquals"/>, and <see cref="JsonNode.DeepClone"/>, <see cref="JsonNode.ToJsonString"/> and
/// <see cref="JsonNode.WriteTo"/> of a node whose children have been made, go one call per level and end the process with
/// a stack overflow at 100,000 deep - no catch stops one - so they too are kept out of the deep path: a place's path is
/// spelt by <see cref="JsonPlace.Path"/>, which walks up in a loop, and a node whose children were never made is written
/// from its document, iteratively.
/// </para>
/// <para>
/// The serializer reads a <see cref="JsonElement"/>, a <see cref="JsonDocument"/> or a <see cref="JsonNode"/> as those
/// read themselves, and writes with the depth of its options. A typed shape it reads one call per level, unguarded: a shape
/// nested 100,000 deep ends the process with a stack overflow whatever its options say, so a document whose depth is not
/// its type's own is read as a <see cref="JsonElement"/> or a <see cref="JsonNode"/> and gone down by
/// <see cref="JsonWalk"/>.
/// </para>
/// </remarks>
public static class JsonDepth
{
    /// <summary>
    /// The depth every option here is given: the largest .NET takes, so no document is refused for its depth - a nesting
    /// as deep would need more text than one string or document can hold.
    /// </summary>
    public const int Unbounded = int.MaxValue;

    /// <summary>The nodes <see cref="ParseNode"/> makes are given options of their own, so none asks its parent for them.</summary>
    private static readonly JsonNodeOptions Nodes = new();

    /// <summary>
    /// How a <see cref="Utf8JsonReader"/> reads: at any depth. Each use is a copy of its own, so a reader that also skips
    /// comments says so with <c>JsonDepth.Reader with { CommentHandling = JsonCommentHandling.Skip }</c>.
    /// </summary>
    public static JsonReaderOptions Reader => new() { MaxDepth = Unbounded };

    /// <summary>How a <see cref="JsonDocument"/> and a <see cref="JsonNode"/> are read from text: at any depth, each use a copy of its own.</summary>
    public static JsonDocumentOptions Document => new() { MaxDepth = Unbounded };

    /// <summary>
    /// How a <see cref="Utf8JsonWriter"/> writes: at any depth, each use a copy of its own - a writer left to itself
    /// refuses to open the 1,001st level, so a document read whole could not be written back.
    /// </summary>
    public static JsonWriterOptions Writer => new() { MaxDepth = Unbounded };

    /// <summary>
    /// A copy of <paramref name="options"/> that reads and writes at any depth. The options given are left as they are:
    /// they may be read-only already, or shared, as <see cref="JsonSerializerOptions.Default"/> is.
    /// </summary>
    /// <param name="options">The options to copy: every setting kept but the depth.</param>
    /// <remarks>
    /// A copy keeps its own cache of what it learns of each type, so a caller makes it once and holds it - a static field -
    /// rather than once a call.
    /// </remarks>
    public static JsonSerializerOptions Serializer(JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return new JsonSerializerOptions(options) { MaxDepth = Unbounded };
    }

    /// <summary>The document <paramref name="json"/> is, read at any depth.</summary>
    /// <param name="json">The JSON text.</param>
    /// <exception cref="JsonException"><paramref name="json"/> is no JSON.</exception>
    public static JsonDocument ParseDocument(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        return JsonDocument.Parse(json, Document);
    }

    /// <summary>
    /// The node <paramref name="json"/> is, read at any depth, or <see langword="null"/> where it is JSON's null. Every node
    /// it holds is given its options as it is made, so going down it never asks back up its parents.
    /// </summary>
    /// <param name="json">The JSON text.</param>
    /// <exception cref="JsonException"><paramref name="json"/> is no JSON.</exception>
    public static JsonNode? ParseNode(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        return JsonNode.Parse(json, Nodes, Document);
    }

    /// <summary>
    /// The node the UTF-8 JSON <paramref name="utf8Json"/> holds, read from the stream at any depth - a file's whole
    /// document, never first read into a string - or <see langword="null"/> where it is JSON's null.
    /// </summary>
    /// <param name="utf8Json">The stream, at the document's start.</param>
    /// <exception cref="JsonException">The stream holds no JSON.</exception>
    public static JsonNode? ParseStream(Stream utf8Json)
    {
        ArgumentNullException.ThrowIfNull(utf8Json);

        return JsonNode.Parse(utf8Json, Nodes, Document);
    }
}
