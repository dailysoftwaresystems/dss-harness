using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DssHarness.Core.Json;
using DssHarness.Core.Schemas;

namespace DssHarness.Tests;

/// <summary>
/// Every place of a JSON document is walked in document order on a stack of the walk's own, each with its path spelt as
/// the repository spells a document's path - at any depth, an element's and a node's alike.
/// </summary>
public sealed class JsonWalkTests
{
    /// <summary>How deep the deep document nests: objects and arrays by turns, a number at the bottom.</summary>
    private const int Deep = 100_000;

    /// <summary>A document of every kind of place, and of names an identifier spells and names it does not.</summary>
    private const string Small = """{"a":1,"b c":[true,{"d":null}],"$ref":"x","_9":[],"9a":{},"é":[[]],"q\"t":2}""";

    /// <summary>
    /// A document 100,000 deep - objects and arrays by turns - is walked whole, as an element and as a node: every place
    /// once, the deepest at its depth, its path spelt from the root down, holding the number at the bottom. It is walked in
    /// a child on a small stack, so a walk that recursed would end that child, never this run.
    /// </summary>
    [Fact]
    public async Task Descendants_OfAHundredThousandDeepDocument()
    {
        var answer = await ChildRun.MethodAsync(typeof(JsonWalkTests), nameof(WalkDeep), TestContext.Current.CancellationToken);

        Assert.Equal(
            new ChildRunAnswer(
                0,
                "element: 100001 places, the deepest 100000 deep at the path built, holding 0\n"
                + "node: 100001 places, the deepest 100000 deep at the path built, holding 0\n",
                string.Empty),
            answer);
    }

    /// <summary>
    /// An element's places come root first, then each member or item in the order the document holds them, each before the
    /// places under it, each at its depth and its path, holding the element written there.
    /// </summary>
    [Fact]
    public void Descendants_OfAnElement_AreInDocumentOrder_EachAtItsPath()
    {
        using var document = JsonDepth.ParseDocument(Small);

        Assert.Equal(
            [
                ("$", 0, Small),
                ("$.a", 1, "1"),
                ("""$["b c"]""", 1, """[true,{"d":null}]"""),
                ("""$["b c"][0]""", 2, "true"),
                ("""$["b c"][1]""", 2, """{"d":null}"""),
                ("""$["b c"][1].d""", 3, "null"),
                ("$.$ref", 1, "\"x\""),
                ("$._9", 1, "[]"),
                ("""$["9a"]""", 1, "{}"),
                ("""$["é"]""", 1, "[[]]"),
                ("""$["é"][0]""", 2, "[]"),
                ("""$["q\"t"]""", 1, "2"),
            ],
            JsonWalk.Descendants(document.RootElement).Select(each => (each.Place.Path(), each.Place.Depth, each.Element.GetRawText())));
    }

    /// <summary>
    /// A node's places come in the same order, at the same depths and paths, each holding the very node at that place - a
    /// member whose value is JSON's null with no node.
    /// </summary>
    [Fact]
    public void Descendants_OfANode_AreInDocumentOrder_EachAtItsPath()
    {
        var root = JsonDepth.ParseNode(Small)!;
        var walked = JsonWalk.Descendants(root).ToList();

        Assert.Equal(
            [
                ("$", 0),
                ("$.a", 1),
                ("""$["b c"]""", 1),
                ("""$["b c"][0]""", 2),
                ("""$["b c"][1]""", 2),
                ("""$["b c"][1].d""", 3),
                ("$.$ref", 1),
                ("$._9", 1),
                ("""$["9a"]""", 1),
                ("""$["é"]""", 1),
                ("""$["é"][0]""", 2),
                ("""$["q\"t"]""", 1),
            ],
            walked.Select(each => (each.Place.Path(), each.Place.Depth)));
        Assert.Equal(
            [
                root,
                root["a"],
                root["b c"],
                root["b c"]![0],
                root["b c"]![1],
                null,
                root["$ref"],
                root["_9"],
                root["9a"],
                root["é"],
                root["é"]![0],
                root["q\"t"],
            ],
            walked.Select(each => each.Node),
            ReferenceEqualityComparer.Instance);
    }

    /// <summary>A document that is one value - a number, or JSON's null - is its root alone, with nothing under it.</summary>
    [Fact]
    public void Descendants_OfAScalarOrNull_IsItsRootAlone()
    {
        using var number = JsonDepth.ParseDocument("5");

        Assert.Equal([("$", "5")], JsonWalk.Descendants(number.RootElement).Select(each => (each.Place.Path(), each.Element.GetRawText())));
        Assert.Equal([("$", (JsonNode?)null)], JsonWalk.Descendants((JsonNode?)null).Select(each => (each.Place.Path(), each.Node)));
    }

    /// <summary>
    /// A place says what holds it, its name or index and its depth, and is spelt from the root down: <c>.name</c> for a
    /// name an ASCII identifier spells - <c>$</c> and <c>_</c> among its letters - brackets for any other, written as JSON
    /// writes a string and escaping only what JSON must, and <c>[index]</c> for an item.
    /// </summary>
    [Fact]
    public void APlace_IsSpeltFromTheRootDown()
    {
        var place = JsonPlace.Root.Member("a").Item(2).Member("b c");

        Assert.Equal(("$", 0, null, null, null), (JsonPlace.Root.Path(), JsonPlace.Root.Depth, JsonPlace.Root.Parent, JsonPlace.Root.Name, JsonPlace.Root.Index));
        Assert.Equal(("""$.a[2]["b c"]""", 3, "b c", (int?)null), (place.Path(), place.Depth, place.Name, place.Index));
        Assert.Equal((2, (string?)null, 2), (place.Parent!.Index, place.Parent.Name, place.Parent.Depth));
        Assert.Equal(place.Path(), place.ToString());
        Assert.Equal(
            [".a", "._", ".$ref", ".A9_$", """["9"]""", """[""]""", """["a-b"]""", """["a.b"]""", """["é"]""", """["a\"b\\c"]""", """["a\nb"]""", "[0]", "[12]"],
            [
                JsonPlace.Segment("a"), JsonPlace.Segment("_"), JsonPlace.Segment("$ref"), JsonPlace.Segment("A9_$"), JsonPlace.Segment("9"), JsonPlace.Segment(string.Empty),
                JsonPlace.Segment("a-b"), JsonPlace.Segment("a.b"), JsonPlace.Segment("é"), JsonPlace.Segment("a\"b\\c"), JsonPlace.Segment("a\nb"), JsonPlace.Segment(0), JsonPlace.Segment(12),
            ]);
        Assert.Throws<ArgumentOutOfRangeException>("index", () => JsonPlace.Root.Item(-1));
        Assert.Throws<ArgumentOutOfRangeException>("index", () => JsonPlace.Segment(-1));
        Assert.Throws<ArgumentNullException>("name", () => JsonPlace.Root.Member(null!));
    }

    /// <summary>
    /// A place's path is spelt as the schema check spells the document's own path in its problems, so a walk's place and a
    /// problem the check finds there name it alike while each writes its own.
    /// </summary>
    [Fact]
    public void APlacesPath_IsSpeltAsASchemaProblemSpellsIt()
    {
        var schema = JsonNode.Parse("""{"type":"object","additionalProperties":false,"properties":{}}""")!;
        var document = JsonDepth.ParseNode("""{"a.b":3,"$x":"café","under_score1":4,"a\"b":1,"café":2,"1a":5}""");
        string[] problems =
        [
            "$[\"a.b\"]: is said by no property of the schema",
            "$.$x: is said by no property of the schema",
            "$.under_score1: is said by no property of the schema",
            "$[\"a\\\"b\"]: is said by no property of the schema",
            "$[\"café\"]: is said by no property of the schema",
            "$[\"1a\"]: is said by no property of the schema",
        ];

        Assert.Equal(problems, JsonSchemaSubset.Problems(schema, document));
        Assert.Equal(problems, JsonWalk.Descendants(document).Skip(1).Select(each => $"{each.Place.Path()}: is said by no property of the schema"));
    }

    /// <summary>
    /// The child of <see cref="Descendants_OfAHundredThousandDeepDocument"/>: walks the deep document as an element read
    /// from text and as a node built from the bottom up, on a small stack, and says what each walk met.
    /// </summary>
    private static string WalkDeep() => SmallStack.Run(() =>
    {
        var text = new StringBuilder();
        var path = new StringBuilder("$");

        for (var level = 0; level < Deep; level++)
        {
            text.Append(level % 2 == 0 ? "{\"a\":" : "[");
            path.Append(level % 2 == 0 ? ".a" : "[0]");
        }

        text.Append('0');

        for (var level = Deep - 1; level >= 0; level--)
        {
            text.Append(level % 2 == 0 ? '}' : ']');
        }

        // Built from the bottom up, each node is given its parent while that parent has none, so the building is linear;
        // a node read from text would time only .NET's making of each child, which checks for a cycle up the parents.
        JsonNode built = JsonValue.Create(0);

        for (var level = Deep - 1; level >= 0; level--)
        {
            built = level % 2 == 0 ? new JsonObject { ["a"] = built } : new JsonArray(built);
        }

        using var document = JsonDepth.ParseDocument(text.ToString());
        var deepestPath = path.ToString();

        var elements = Met(JsonWalk.Descendants(document.RootElement).Select(each => (each.Place, Holding: each.Element.ValueKind == JsonValueKind.Number ? each.Element.GetRawText() : null)), deepestPath);
        var nodes = Met(JsonWalk.Descendants(built).Select(each => (each.Place, Holding: each.Node is JsonValue value ? value.ToJsonString() : null)), deepestPath);

        return $"element: {elements}\nnode: {nodes}\n";
    });

    /// <summary>
    /// A node is copied whole, held by nothing, and compared as JSON compares: an object's members by name in any order, an
    /// array's items in order, values as .NET compares them, and two kinds never alike.
    /// </summary>
    [Fact]
    public void ACopy_IsTheSameJson_HeldByNothing()
    {
        var root = JsonDepth.ParseNode(Small)!;
        var copy = JsonWalk.Copy(root["b c"]);

        Assert.Null(copy!.Parent);
        Assert.NotSame(root["b c"], copy);
        Assert.Equal("""[true,{"d":null}]""", copy.ToJsonString());
        Assert.True(JsonWalk.Same(root, JsonWalk.Copy(root)));
        Assert.Null(JsonWalk.Copy(null));

        Assert.True(JsonWalk.Same(JsonNode.Parse("""{"a":1,"b":[1,2]}"""), JsonNode.Parse("""{"b":[1,2],"a":1}""")));
        Assert.False(JsonWalk.Same(JsonNode.Parse("[1,2]"), JsonNode.Parse("[2,1]")));
        Assert.False(JsonWalk.Same(JsonNode.Parse("""{"a":1}"""), JsonNode.Parse("""{"a":1,"b":2}""")));
        Assert.False(JsonWalk.Same(JsonNode.Parse("[]"), JsonNode.Parse("{}")));
        Assert.False(JsonWalk.Same(JsonNode.Parse("\"1\""), JsonNode.Parse("1")));
        Assert.True(JsonWalk.Same(null, null));
        Assert.False(JsonWalk.Same(null, JsonNode.Parse("0")));
    }

    /// <summary>
    /// A node 100,000 deep is copied and compared whole: the copy the same JSON, and a node whose bottom differs not. Done in
    /// a child on a small stack, so a copy or a comparison that recursed would end that child, never this run.
    /// </summary>
    [Fact]
    public async Task ACopyAndAComparison_OfAHundredThousandDeepNode()
    {
        var answer = await ChildRun.MethodAsync(typeof(JsonWalkTests), nameof(CopyDeep), TestContext.Current.CancellationToken);

        Assert.Equal(new ChildRunAnswer(0, "copied: the same; changed at the bottom: another\n", string.Empty), answer);
    }

    /// <summary><see cref="ACopyAndAComparison_OfAHundredThousandDeepNode"/>'s child.</summary>
    private static string CopyDeep() => SmallStack.Run(() =>
    {
        var original = JsonDepth.ParseNode(string.Concat(Enumerable.Repeat("[", Deep)) + "0" + new string(']', Deep));
        var copy = JsonWalk.Copy(original);
        var changed = JsonDepth.ParseNode(string.Concat(Enumerable.Repeat("[", Deep)) + "1" + new string(']', Deep));

        return $"copied: {(JsonWalk.Same(original, copy) ? "the same" : "another")}; changed at the bottom: {(JsonWalk.Same(original, changed) ? "the same" : "another")}\n";
    });

    /// <summary>What a walk met: how many places, the deepest one's depth, whether its path is <paramref name="path"/>, and what it holds.</summary>
    private static string Met(IEnumerable<(JsonPlace Place, string? Holding)> walked, string path)
    {
        var places = 0;
        var deepest = JsonPlace.Root;
        string? holding = null;

        foreach (var (place, held) in walked)
        {
            places++;

            if (place.Depth > deepest.Depth)
            {
                (deepest, holding) = (place, held);
            }
        }

        var at = string.Equals(deepest.Path(), path, StringComparison.Ordinal) ? "the path built" : $"another path, {deepest.Path().Length} characters";

        return string.Create(CultureInfo.InvariantCulture, $"{places} places, the deepest {deepest.Depth} deep at {at}, holding {holding}");
    }
}
