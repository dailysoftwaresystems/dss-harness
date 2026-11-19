using System.Globalization;
using System.Text;
using System.Text.Json;
using DssHarness.Core.Json;

namespace DssHarness.Tests;

/// <summary>JSON is read and written at any depth through the one owner of the options: no document is refused for its nesting alone.</summary>
public sealed class JsonDepthTests
{
    /// <summary>How deep the deep document nests.</summary>
    private const int Deep = 100_000;

    /// <summary>
    /// A document 100,000 deep is read whole through each option - the reader meets every token, the document and the
    /// serializer's element each hold every level, the node gives its children their own options - and each is written
    /// back as it was read. System.Text.Json's own options refuse it at its 65th level, and a writer's at its 1,001st. It
    /// is read in a child on a small stack, so a reading that recursed would end that child, never this run.
    /// </summary>
    [Fact]
    public async Task AHundredThousandDeepDocument_ParsesThroughEachOption()
    {
        var answer = await ChildRun.MethodAsync(typeof(JsonDepthTests), nameof(ReadDeep), TestContext.Current.CancellationToken);

        Assert.Equal(
            new ChildRunAnswer(
                0,
                "reader: 200000 tokens\n"
                + "document: 100000 deep, written back the same\n"
                + "serializer: 100000 deep, written back the same\n"
                + "node: its first child's options its own, written back the same\n",
                string.Empty),
            answer);
    }

    /// <summary>
    /// Every option is given the largest depth .NET takes, so nothing is refused for its depth: a finite depth, however
    /// large, is a limit a deeper document meets.
    /// </summary>
    [Fact]
    public void EveryOption_IsGivenTheLargestDepth()
    {
        Assert.Equal(int.MaxValue, JsonDepth.Unbounded);
        Assert.Equal(int.MaxValue, JsonDepth.Reader.MaxDepth);
        Assert.Equal(int.MaxValue, JsonDepth.Document.MaxDepth);
        Assert.Equal(int.MaxValue, JsonDepth.Writer.MaxDepth);
        Assert.Equal(int.MaxValue, JsonDepth.Serializer(JsonSerializerOptions.Default).MaxDepth);
    }

    /// <summary>
    /// The serializer's options are a copy of those given, every setting kept but the depth, and the options given are left
    /// as they were: the shared default among them, which is read-only and could not be changed in place.
    /// </summary>
    [Fact]
    public void TheSerializersOptions_AreACopy_LeavingThoseGivenAsTheyWere()
    {
        var given = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        var copy = JsonDepth.Serializer(given);
        var ofTheDefault = JsonDepth.Serializer(JsonSerializerOptions.Default);

        Assert.NotSame(given, copy);
        Assert.Equal((int.MaxValue, true, JsonNamingPolicy.CamelCase), (copy.MaxDepth, copy.WriteIndented, copy.PropertyNamingPolicy));
        Assert.Equal(0, given.MaxDepth);
        Assert.NotSame(JsonSerializerOptions.Default, ofTheDefault);
        Assert.Equal((int.MaxValue, 0), (ofTheDefault.MaxDepth, JsonSerializerOptions.Default.MaxDepth));
    }

    /// <summary>JSON's null is read as no node, and text that is no JSON is thrown on as .NET throws on it.</summary>
    [Fact]
    public void Null_IsNoNode_AndTextThatIsNoJson_IsThrownOn()
    {
        Assert.Null(JsonDepth.ParseNode("null"));
        Assert.ThrowsAny<JsonException>(() => JsonDepth.ParseNode("["));
        Assert.ThrowsAny<JsonException>(() => JsonDepth.ParseDocument("["));
    }

    /// <summary>
    /// The child of <see cref="AHundredThousandDeepDocument_ParsesThroughEachOption"/>: reads the deep document through
    /// each option on a small stack, and says what each read held.
    /// </summary>
    private static string ReadDeep() => SmallStack.Run(() =>
    {
        var text = new string('[', Deep) + new string(']', Deep);
        var said = new StringBuilder();

        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(text), JsonDepth.Reader);
        var tokens = 0;

        while (reader.Read())
        {
            tokens++;
        }

        said.Append(CultureInfo.InvariantCulture, $"reader: {tokens} tokens\n");

        using (var document = JsonDepth.ParseDocument(text))
        {
            said.Append(CultureInfo.InvariantCulture, $"document: {DepthOf(document.RootElement)} deep, written back {Compared(Written(document.RootElement), text)}\n");
        }

        var options = JsonDepth.Serializer(JsonSerializerOptions.Default);
        var element = JsonSerializer.Deserialize<JsonElement>(text, options);

        said.Append(CultureInfo.InvariantCulture, $"serializer: {DepthOf(element)} deep, written back {Compared(JsonSerializer.Serialize(element, options), text)}\n");

        // Going a node's whole depth down would only time .NET's making of each child; a child's own options are what
        // spare a deep walk the climb back up its parents, and writing the node back shows it holds every level.
        var node = JsonDepth.ParseNode(text)!;
        var first = node.AsArray()[0]!;

        said.Append($"node: its first child's options {(first.Options is null ? "asked of its parent" : "its own")}, written back {Compared(node.ToJsonString(options), text)}\n");

        return said.ToString();
    });

    /// <summary>How many arrays deep <paramref name="element"/> nests, each holding the next as its first item, gone down in a loop.</summary>
    private static int DepthOf(JsonElement element)
    {
        var depth = 1;

        for (var at = element; at.GetArrayLength() > 0; at = at[0])
        {
            depth++;
        }

        return depth;
    }

    /// <summary>The text <paramref name="element"/> is written as, through a writer at any depth.</summary>
    private static string Written(JsonElement element)
    {
        using var buffer = new MemoryStream();

        using (var writer = new Utf8JsonWriter(buffer, JsonDepth.Writer))
        {
            element.WriteTo(writer);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>Whether a text written back is the text read.</summary>
    private static string Compared(string written, string read)
        => string.Equals(written, read, StringComparison.Ordinal) ? "the same" : $"otherwise, {written.Length} characters";
}
