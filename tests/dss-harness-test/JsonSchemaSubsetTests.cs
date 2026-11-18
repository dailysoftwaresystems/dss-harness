using System.Text.Json.Nodes;
using DssHarness.Core.Orchestration.Flow;
using DssHarness.Core.Schemas;

namespace DssHarness.Tests;

/// <summary>
/// The one schema validator: what a schema may say - the subset, refused where it says more, naming the place -
/// and whether a document answers one, every problem a short JSON path a model can act on.
/// </summary>
public sealed class JsonSchemaSubsetTests
{
    /// <summary>
    /// Every schema the shipped workflow holds is a schema this engine reads - each read whole, with the
    /// definitions its references share with the schema files beside it taken in, as the engine reads them where it
    /// loads one.
    /// </summary>
    [Fact]
    public void EveryShippedSchema_IsOneThisEngineReads()
    {
        var schemas = WorkflowTemplates.Files.Where(file => file.EndsWith(".schema.json", StringComparison.Ordinal)).ToList();

        Assert.NotEmpty(schemas);

        foreach (var file in schemas)
        {
            var text = WorkflowTemplates.TextOf(file);

            Assert.NotNull(text);

            var schema = JsonNode.Parse(text!);

            Assert.NotNull(schema);

            var whole = JsonSchemaSubset.Shared(
                (JsonObject)schema!,
                beside => file.LastIndexOf('/') is var at && at < 0
                    ? null
                    : JsonNode.Parse(WorkflowTemplates.TextOf($"{file[..at]}/{beside}") ?? "null") as JsonObject);

            Assert.Empty(JsonSchemaSubset.SubsetProblems(whole));
        }
    }

    /// <summary>
    /// A definition shared with the files beside the schema is taken into the schema's own definitions where it is
    /// read, and the reference is made the local one: the schema holds the definition's shape, a document is held
    /// to it as to one of its own, and the schema given is never changed.
    /// </summary>
    [Fact]
    public void ADefinitionSharedWithTheFilesBesideTheSchema_IsTakenInWhereItIsRead()
    {
        var plan = Schema("""
            {"type":"object","additionalProperties":false,"required":["name"],"properties":{"name":{"type":"string"}},"$defs":{"workItem":{"type":"object","additionalProperties":false,"required":["name"],"properties":{"name":{"type":"string"}}}}}
            """);
        var selfAudit = (JsonObject)Schema("""
            {"type":"object","additionalProperties":false,"required":["items"],"properties":{"items":{"type":"array","items":{"$ref":"plan.schema.json#/$defs/workItem"}}}}
            """);

        var whole = JsonSchemaSubset.Shared(selfAudit, beside => beside == "plan.schema.json" ? (JsonObject)plan : null);

        Assert.Empty(JsonSchemaSubset.SubsetProblems(whole));
        Assert.True(JsonNode.DeepEquals(plan["$defs"]!["workItem"], whole["$defs"]!["workItem"]));
        Assert.Equal("#/$defs/workItem", whole["properties"]!["items"]!["items"]!["$ref"]!.GetValue<string>());

        // The definition is read through, not copied aside: a document that misses what it requires is refused at
        // the document's own path under the shared shape, and closed as the definition closes.
        Assert.Equal(
            ["$.items[0]: 'name' is required and missing", "$.items[0].title: is said by no property of the schema"],
            JsonSchemaSubset.Problems(whole, Schema("""{"items":[{"title":"the report"}]}""")));

        // The schema given keeps its shared reference and gains nothing: what comes back is a copy.
        Assert.Equal("plan.schema.json#/$defs/workItem", selfAudit["properties"]!["items"]!["items"]!["$ref"]!.GetValue<string>());
        Assert.False(selfAudit.ContainsKey("$defs"));
    }

    /// <summary>
    /// What a shared definition reaches is taken with it: a definition of its own file, and one of a file beside
    /// that, each reference made local, however deep the reach.
    /// </summary>
    [Fact]
    public void WhatASharedDefinitionReaches_IsTakenWithIt()
    {
        var other = Schema("""
            {"type":"object","additionalProperties":false,"required":["cell"],"properties":{"cell":{"type":"string"}},"$defs":{"cell":{"type":"string"}}}
            """);
        var plan = Schema("""
            {"$defs":{"workItem":{"type":"object","additionalProperties":false,"required":["paths"],"properties":{"paths":{"$ref":"#/$defs/pathList"},"cell":{"$ref":"other.schema.json#/$defs/cell"}}},"pathList":{"type":"array","items":{"type":"string"}}}}
            """);
        var diagnosis = (JsonObject)Schema("""
            {"type":"object","additionalProperties":false,"required":["items"],"properties":{"items":{"type":"array","items":{"$ref":"plan.schema.json#/$defs/workItem"}}}}
            """);

        var whole = JsonSchemaSubset.Shared(diagnosis, beside => beside switch { "plan.schema.json" => (JsonObject)plan, "other.schema.json" => (JsonObject)other, _ => null });

        Assert.Empty(JsonSchemaSubset.SubsetProblems(whole));
        Assert.Equal(["workItem", "pathList", "cell"], [.. whole["$defs"]!.AsObject().Select(member => member.Key)]);
        Assert.Equal("#/$defs/pathList", whole["$defs"]!["workItem"]!["properties"]!["paths"]!["$ref"]!.GetValue<string>());
        Assert.Equal("#/$defs/cell", whole["$defs"]!["workItem"]!["properties"]!["cell"]!["$ref"]!.GetValue<string>());
        Assert.Equal(
            ["$.items[0].paths: \"src/**\" is not an array"],
            JsonSchemaSubset.Problems(whole, Schema("""{"items":[{"paths":"src/**","cell":"kind"}]}""")));
    }

    /// <summary>
    /// A shared reference nothing beside the schema answers is refused where the schema is checked, naming the file
    /// and what it lacks: a file not there, one that holds no such definition - and a reference that names a
    /// directory rather than a file beside the schema is refused as one that names no definition of it at all.
    /// </summary>
    [Fact]
    public void ASharedReferenceNothingBesideTheSchemaAnswers_IsRefused()
    {
        var plan = Schema("""{"$defs":{"other":{"type":"string"}}}""");
        var held = (JsonObject)Schema("""{"$ref":"plan.schema.json#/$defs/workItem"}""");

        Assert.Equal(
            ["$.$ref: 'plan.schema.json#/$defs/workItem' names a definition of the schema file 'plan.schema.json' beside this one, and it was not taken into this schema's own definitions - the file is not beside it, or holds no definition 'workItem', or this schema holds the name already, said otherwise"],
            JsonSchemaSubset.SubsetProblems(JsonSchemaSubset.Shared(held, _ => null)));

        Assert.Equal(
            ["$.$ref: 'plan.schema.json#/$defs/workItem' names a definition of the schema file 'plan.schema.json' beside this one, and it was not taken into this schema's own definitions - the file is not beside it, or holds no definition 'workItem', or this schema holds the name already, said otherwise"],
            JsonSchemaSubset.SubsetProblems(JsonSchemaSubset.Shared((JsonObject)held.DeepClone(), _ => (JsonObject?)plan)));

        Assert.Equal(
            ["$.$ref: 'schemas/plan.schema.json#/$defs/workItem' names no definition of this schema"],
            JsonSchemaSubset.SubsetProblems(Schema("""{"$ref":"schemas/plan.schema.json#/$defs/workItem"}""")));
    }

    /// <summary>
    /// A name the schema holds already is not taken over: a shared definition of the same name is taken only where
    /// it is the same definition, and one that says otherwise stands refused.
    /// </summary>
    [Fact]
    public void ANameTheSchemaHoldsAlready_IsTakenOnlyWhereItIsTheSameDefinition()
    {
        var plan = Schema("""
            {"$defs":{"workItem":{"type":"object","additionalProperties":false,"required":["name"],"properties":{"name":{"type":"string"}}}}}
            """);
        var same = Schema("""
            {"type":"object","additionalProperties":false,"required":["list"],"properties":{"list":{"type":"array","items":{"$ref":"plan.schema.json#/$defs/workItem"}}},"$defs":{"workItem":{"type":"object","additionalProperties":false,"required":["name"],"properties":{"name":{"type":"string"}}}}}
            """);
        var otherwise = (JsonObject)Schema("""
            {"type":"array","items":{"$ref":"plan.schema.json#/$defs/workItem"},"$defs":{"workItem":{"type":"string"}}}
            """);

        Assert.Empty(JsonSchemaSubset.SubsetProblems(JsonSchemaSubset.Shared((JsonObject)same, _ => (JsonObject?)plan)));
        Assert.True(JsonNode.DeepEquals(plan["$defs"]!["workItem"], JsonSchemaSubset.Shared((JsonObject)same, _ => (JsonObject?)plan)["$defs"]!["workItem"]));

        Assert.Equal(
            ["$.items.$ref: 'plan.schema.json#/$defs/workItem' names a definition of the schema file 'plan.schema.json' beside this one, and it was not taken into this schema's own definitions - the file is not beside it, or holds no definition 'workItem', or this schema holds the name already, said otherwise"],
            JsonSchemaSubset.SubsetProblems(JsonSchemaSubset.Shared(otherwise, _ => (JsonObject?)plan)));
    }

    /// <summary>A definition that reaches itself through the files beside the schema is refused as the recursion it is.</summary>
    [Fact]
    public void ASharedDefinitionThatReachesItselfAcrossFiles_IsRefusedAsARecursion()
    {
        var one = Schema("""{"$defs":{"a":{"$ref":"two.schema.json#/$defs/b"}}}""");
        var two = Schema("""{"$defs":{"b":{"$ref":"one.schema.json#/$defs/a"}}}""");
        var held = (JsonObject)Schema("""{"type":"array","items":{"$ref":"one.schema.json#/$defs/a"}}""");

        var whole = JsonSchemaSubset.Shared(held, beside => beside switch { "one.schema.json" => (JsonObject)one, "two.schema.json" => (JsonObject)two, _ => null });

        Assert.Equal(
            [
                "$.$defs.a.$ref: '#/$defs/b' reaches itself, and a definition never recurses",
                "$.$defs.b.$ref: '#/$defs/a' reaches itself, and a definition never recurses",
            ],
            JsonSchemaSubset.SubsetProblems(whole));
    }

    /// <summary>
    /// A reference a taken definition holds to a definition of its own file, written there as a local one, is never
    /// bound to the referring schema's own definition of that name: where the name cannot be taken - the schema holds
    /// it, said otherwise, or an earlier shared reference took it from another file - the reference is written naming
    /// the file it reaches into, and refused as a shared reference that was not taken. Left local, it would type and
    /// check the taken definition's members by a shape its file never gave them, and pass.
    /// </summary>
    [Fact]
    public void AReferenceATakenDefinitionHoldsToItsOwnFile_IsNeverBoundToTheSchemasOwnDefinitionOfThatName()
    {
        var plan = Schema("""
            {"$defs":{"workItem":{"type":"object","additionalProperties":false,"required":["classes"],"properties":{"classes":{"type":"array","items":{"$ref":"#/$defs/closureClass"}}}},"closureClass":{"type":"object","additionalProperties":false,"required":["id"],"properties":{"id":{"type":"string"}}}}}
            """);
        var other = Schema("""{"$defs":{"closureClass":{"type":"integer"}}}""");
        var holding = (JsonObject)Schema("""
            {"type":"object","additionalProperties":false,"required":["items","finding"],"properties":{"items":{"type":"array","items":{"$ref":"plan.schema.json#/$defs/workItem"}},"finding":{"$ref":"#/$defs/closureClass"}},"$defs":{"closureClass":{"type":"string"}}}
            """);
        var taking = (JsonObject)Schema("""
            {"type":"object","additionalProperties":false,"required":["cell","items"],"properties":{"cell":{"$ref":"other.schema.json#/$defs/closureClass"},"items":{"type":"array","items":{"$ref":"plan.schema.json#/$defs/workItem"}}}}
            """);
        JsonObject? Beside(string file) => file switch { "plan.schema.json" => (JsonObject)plan, "other.schema.json" => (JsonObject)other, _ => null };
        const string NotTaken = "$.$defs.workItem.properties.classes.items.$ref: 'plan.schema.json#/$defs/closureClass' names a definition of the schema file 'plan.schema.json' beside this one, and it was not taken into this schema's own definitions - the file is not beside it, or holds no definition 'closureClass', or this schema holds the name already, said otherwise";

        // The schema's own definition of the name stands as it wrote it, its own reference still finding it; the taken
        // definition's reference names its file, and is refused.
        var held = JsonSchemaSubset.Shared(holding, Beside);

        Assert.Equal("plan.schema.json#/$defs/closureClass", held["$defs"]!["workItem"]!["properties"]!["classes"]!["items"]!["$ref"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(Schema("""{"type":"string"}"""), held["$defs"]!["closureClass"]));
        Assert.Equal([NotTaken], JsonSchemaSubset.SubsetProblems(held));

        // The name taken already from another file is not the plan's either.
        var took = JsonSchemaSubset.Shared(taking, Beside);

        Assert.True(JsonNode.DeepEquals(other["$defs"]!["closureClass"], took["$defs"]!["closureClass"]));
        Assert.Equal([NotTaken], JsonSchemaSubset.SubsetProblems(took));
    }

    /// <summary>
    /// Definitions that are no mapping are never thrown on, and never replaced: a file beside the schema whose
    /// <c>$defs</c> is a list or a value answers no reference, which stands refused; and a schema whose own
    /// <c>$defs</c> is no mapping takes nothing in, so the subset check refuses both what it holds there and its
    /// shared reference, where a mapping put in its place would have passed it.
    /// </summary>
    [Theory]
    [InlineData("[]")]
    [InlineData("\"the definitions\"")]
    [InlineData("null")]
    public void DefinitionsThatAreNoMapping_AreNeverThrownOn_NorReplaced(string defs)
    {
        var plan = Schema("""
            {"$defs":{"workItem":{"type":"object","additionalProperties":false,"required":["name"],"properties":{"name":{"type":"string"}}}}}
            """);
        const string NotTaken = "$.items.$ref: 'plan.schema.json#/$defs/workItem' names a definition of the schema file 'plan.schema.json' beside this one, and it was not taken into this schema's own definitions - the file is not beside it, or holds no definition 'workItem', or this schema holds the name already, said otherwise";

        // The file beside the schema: no mapping answers nothing, whether the schema holds the name or not.
        var beside = (JsonObject)Schema($$"""{"$defs":{{defs}}}""");

        Assert.Equal(
            [NotTaken],
            JsonSchemaSubset.SubsetProblems(JsonSchemaSubset.Shared((JsonObject)Schema("""{"type":"array","items":{"$ref":"plan.schema.json#/$defs/workItem"}}"""), _ => beside)));
        Assert.Equal(
            [NotTaken],
            JsonSchemaSubset.SubsetProblems(JsonSchemaSubset.Shared((JsonObject)Schema("""{"type":"array","items":{"$ref":"plan.schema.json#/$defs/workItem"},"$defs":{"workItem":{"type":"string"}}}"""), _ => beside)));

        // The schema's own: left as it is written, with its shared reference.
        var holding = (JsonObject)Schema($$"""{"type":"array","items":{"$ref":"plan.schema.json#/$defs/workItem"},"$defs":{{defs}}}""");
        var whole = JsonSchemaSubset.Shared(holding, _ => (JsonObject)plan);

        Assert.True(JsonNode.DeepEquals(holding, whole));
        Assert.Equal([NotTaken, "$.$defs: is not a mapping of definitions to schemas"], JsonSchemaSubset.SubsetProblems(whole));
    }

    /// <summary>
    /// A schema's text is read one way, by whichever reader asks: text that is not JSON and JSON that is no object said
    /// at the root, with no schema to read; a schema that shares nothing read as its file wrote it; one that shares a
    /// definition read with it taken in where the files beside it are read, and refused for it where they are not - each
    /// problem the one check of a schema's text says, which is the reading's own.
    /// </summary>
    [Fact]
    public void ASchemasText_IsReadOneWay_WhereverItIsRead()
    {
        var plan = Schema("""{"$defs":{"workItem":{"type":"string"}}}""");
        const string Sharing = """{"type":"array","items":{"$ref":"plan.schema.json#/$defs/workItem"}}""";
        const string Own = """{"type":"array","items":{"type":"string"}}""";
        string unread;

        try
        {
            _ = JsonNode.Parse("{ not json");
            throw new InvalidOperationException("the text parsed");
        }
        catch (System.Text.Json.JsonException ex)
        {
            unread = ex.Message;
        }

        var notJson = JsonSchemaSubset.Whole("{ not json", _ => (JsonObject)plan);
        var noObject = JsonSchemaSubset.Whole("true");
        var own = JsonSchemaSubset.Whole(Own, _ => (JsonObject)plan);
        var shared = JsonSchemaSubset.Whole(Sharing, beside => beside == "plan.schema.json" ? (JsonObject)plan : null);
        var alone = JsonSchemaSubset.Whole(Sharing);

        Assert.Null(notJson.Schema);
        Assert.False(notJson.TookIn);
        Assert.Equal([$"$: is not JSON ({unread})"], notJson.Problems);
        Assert.Null(noObject.Schema);
        Assert.False(noObject.TookIn);
        Assert.Equal(["$: a schema is a JSON object"], noObject.Problems);

        Assert.True(JsonNode.DeepEquals(Schema(Own), own.Schema));
        Assert.Equal((false, 0), (own.TookIn, own.Problems.Count));

        Assert.True(JsonNode.DeepEquals(Schema("""{"type":"array","items":{"$ref":"#/$defs/workItem"},"$defs":{"workItem":{"type":"string"}}}"""), shared.Schema));
        Assert.Equal((true, 0), (shared.TookIn, shared.Problems.Count));

        Assert.True(JsonNode.DeepEquals(Schema(Sharing), alone.Schema));
        Assert.False(alone.TookIn);
        Assert.Equal(
            ["$.items.$ref: 'plan.schema.json#/$defs/workItem' names a definition of the schema file 'plan.schema.json' beside this one, and it was not taken into this schema's own definitions - the file is not beside it, or holds no definition 'workItem', or this schema holds the name already, said otherwise"],
            alone.Problems);

        // The check of a schema's text is the reading's problems, the files beside it read or not.
        Assert.Equal(alone.Problems, JsonSchemaSubset.TextProblems(Sharing));
        Assert.Empty(JsonSchemaSubset.TextProblems(Sharing, beside => beside == "plan.schema.json" ? (JsonObject)plan : null));
        Assert.Equal(notJson.Problems, JsonSchemaSubset.TextProblems("{ not json"));
    }

    /// <summary>A schema from its text, refused by the parse itself where the text is not JSON.</summary>
    private static JsonNode Schema(string text)
    {
        var node = JsonNode.Parse(text);

        Assert.NotNull(node);
        return node!;
    }

    /// <summary>A reference to the definition named.</summary>
    private static JsonObject Ref(string name) => new() { ["$ref"] = $"#/$defs/{name}" };

    /// <summary>
    /// A schema whose root refers to the first of <paramref name="depth"/> definitions, each made by
    /// <paramref name="step"/> from the name of the next, the last a string.
    /// </summary>
    private static JsonObject Chain(int depth, Func<string, JsonObject> step)
    {
        var definitions = new JsonObject();

        for (var index = 0; index < depth; index++)
        {
            definitions[$"d{index}"] = step($"d{index + 1}");
        }

        definitions[$"d{depth}"] = new JsonObject { ["type"] = "string" };

        return new JsonObject { ["$ref"] = "#/$defs/d0", ["$defs"] = definitions };
    }

    /// <summary>What the subset does not say is refused, naming the place in the schema that says it.</summary>
    [Fact]
    public void WhatTheSubsetDoesNotSay_IsRefusedNamingThePlace()
    {
        Assert.Equal(
            ["$.properties.findings.items.minimum: is said by no schema this engine reads"],
            JsonSchemaSubset.SubsetProblems(Schema("""
                {"type":"object","additionalProperties":false,"properties":{"findings":{"type":"array","items":{"type":"string","minimum":1}}}}
                """)));

        Assert.Equal(
            ["$.$ref: '#/$defs/nowhere' names no definition of this schema"],
            JsonSchemaSubset.SubsetProblems(Schema("""{"$ref":"#/$defs/nowhere"}""")));

        Assert.Equal(
            ["$.properties.answer.format: is not one of date-time, date, uuid"],
            JsonSchemaSubset.SubsetProblems(Schema("""
                {"type":"object","additionalProperties":false,"properties":{"answer":{"type":"string","format":"email"}}}
                """)));

        Assert.Equal(
            ["$: an object holds additionalProperties: false, saying every property is one it names"],
            JsonSchemaSubset.SubsetProblems(Schema("""{"type":"object","properties":{}}""")));

        Assert.Equal(
            ["$.required: names properties the schema does not say; name them under properties"],
            JsonSchemaSubset.SubsetProblems(Schema("""{"type":"object","additionalProperties":false,"required":["x"]}""")));
    }

    /// <summary><c>required</c> names only properties the schema says: one it does not is refused by name.</summary>
    [Fact]
    public void ARequiredNameNoPropertyHas_IsRefusedByName()
    {
        Assert.Equal(
            ["$.required: 'x' is named by no property"],
            JsonSchemaSubset.SubsetProblems(Schema("""
                {"type":"object","additionalProperties":false,"required":["x","y"],"properties":{"y":{"type":"string"}}}
                """)));
    }

    /// <summary>
    /// Only an array's items and length are said, and only a string is formatted or matched: each key said of
    /// another type is refused at its own path, and a string that may be null is still a string.
    /// </summary>
    [Fact]
    public void ItemsAndBoundsOnlyOnAnArray_FormatAndPatternOnlyOnAString()
    {
        Assert.Equal(
            [
                "$.items: only an array's items are said",
                "$.minItems: only an array's length is bounded",
                "$.maxItems: only an array's length is bounded",
            ],
            JsonSchemaSubset.SubsetProblems(Schema("""{"type":"string","items":{"type":"string"},"minItems":1,"maxItems":2}""")));

        Assert.Equal(
            ["$.format: only a string is formatted", "$.pattern: only a string is matched"],
            JsonSchemaSubset.SubsetProblems(Schema("""{"type":"integer","format":"date","pattern":"^1$"}""")));

        Assert.Empty(JsonSchemaSubset.SubsetProblems(Schema("""{"type":["string","null"],"format":"date","pattern":"^2"}""")));
    }

    /// <summary>
    /// An object's keys - properties, required and additionalProperties - are said of an object alone, as every
    /// other key is of its type, so a schema that names properties says an object and closes it, and one that
    /// does not is refused key by key: the document check never holds an answer to an object left open.
    /// </summary>
    [Fact]
    public void PropertiesRequiredAndAdditionalProperties_OnlyOnAnObject()
    {
        var untyped = Schema("""{"properties":{"verdict":{"enum":["pass","fail"]}},"required":["verdict"]}""");
        var answer = JsonNode.Parse("""{"verdict":"pass","notes":"x"}""");

        Assert.Equal(
            ["$.properties: only an object's properties are said", "$.required: only an object's properties are said"],
            JsonSchemaSubset.SubsetProblems(untyped));
        Assert.Throws<ArgumentException>("schema", () => JsonSchemaSubset.Problems(untyped, answer));

        Assert.Equal(
            ["$.additionalProperties: only an object's properties are said"],
            JsonSchemaSubset.SubsetProblems(Schema("""{"type":"string","additionalProperties":false}""")));

        var typed = Schema("""
            {"type":["object","null"],"additionalProperties":false,"properties":{"verdict":{"enum":["pass","fail"]}},"required":["verdict"]}
            """);

        Assert.Empty(JsonSchemaSubset.SubsetProblems(typed));
        Assert.Equal(["$.notes: is said by no property of the schema"], JsonSchemaSubset.Problems(typed, answer));
    }

    /// <summary>
    /// A definition reached through itself is refused at the reference that closes the loop - whether it names
    /// itself or comes back through another - and nowhere else.
    /// </summary>
    [Fact]
    public void ARefThatReachesItself_IsRefused()
    {
        Assert.Equal(
            ["$.$defs.node.properties.child.$ref: '#/$defs/node' reaches itself, and a definition never recurses"],
            JsonSchemaSubset.SubsetProblems(Schema("""
                {"type":"object","additionalProperties":false,"properties":{"child":{"$ref":"#/$defs/node"}},"$defs":{"node":{"type":"object","additionalProperties":false,"properties":{"child":{"$ref":"#/$defs/node"}}}}}
                """)));

        Assert.Equal(
            [
                "$.$defs.a.$ref: '#/$defs/b' reaches itself, and a definition never recurses",
                "$.$defs.b.$ref: '#/$defs/a' reaches itself, and a definition never recurses",
            ],
            JsonSchemaSubset.SubsetProblems(Schema("""
                {"$ref":"#/$defs/a","$defs":{"a":{"$ref":"#/$defs/b"},"b":{"$ref":"#/$defs/a"}}}
                """)));
    }

    /// <summary>
    /// A definition many references reach is walked once, where it is defined: its problem is said once, at its
    /// own path, never again at every reference to it.
    /// </summary>
    [Fact]
    public void ADefinitionReachedManyWays_IsWalkedOnce_WhereItIsDefined()
    {
        Assert.Equal(
            ["$.$defs.x: an object holds additionalProperties: false, saying every property is one it names"],
            JsonSchemaSubset.SubsetProblems(Schema("""
                {"type":"object","additionalProperties":false,"properties":{"a":{"$ref":"#/$defs/x"},"b":{"$ref":"#/$defs/x"}},"$defs":{"x":{"type":"object","properties":{}}}}
                """)));

        Assert.Empty(JsonSchemaSubset.SubsetProblems(Schema("""
            {"type":"object","additionalProperties":false,"properties":{"a":{"$ref":"#/$defs/x"},"b":{"$ref":"#/$defs/x"}},"$defs":{"x":{"type":"string"}}}
            """)));
    }

    /// <summary>
    /// Definitions that each refer to the next twice are walked in time: every one is walked once, never once
    /// for each of the two-to-the-depth ways of reaching it.
    /// </summary>
    [Fact]
    public async Task ADefinitionChainDoublingAtEveryStep_IsWalkedInTime()
    {
        var schema = Chain(32, next => new JsonObject
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["properties"] = new JsonObject { ["a"] = Ref(next), ["b"] = Ref(next) },
        });

        var problems = await Task.Run(() => JsonSchemaSubset.SubsetProblems(schema), TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Empty(problems);
    }

    /// <summary>
    /// A document is checked in time against definitions whose every <c>anyOf</c> offers the next one twice: a
    /// definition is read once at each place in the document, so a value no shape answers costs the depth, not
    /// two to the depth.
    /// </summary>
    [Fact]
    public async Task ADefinitionChainDoublingAtEveryStep_IsCheckedInTime()
    {
        var schema = Chain(32, next => new JsonObject { ["anyOf"] = new JsonArray(Ref(next), Ref(next)) });

        var problems = await Task.Run(() => JsonSchemaSubset.Problems(schema, JsonNode.Parse("7")), TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(["$: answers none of the 2 shapes its schema offers"], problems);
        Assert.Empty(JsonSchemaSubset.Problems(schema, JsonNode.Parse("\"x\"")));
    }

    /// <summary>
    /// A <c>$ref</c> stands alone: what is said beside it is refused key by key, since the drafts disagree on
    /// whether it is read, while words that say nothing of a document - and the root's definitions - stand there.
    /// </summary>
    [Fact]
    public void WhatIsSaidBesideARef_IsRefused()
    {
        Assert.Equal(
            [
                "$.properties.a.type: is said beside $ref, which stands alone; say it in the definition",
                "$.properties.a.minLength: is said beside $ref, which stands alone; say it in the definition",
            ],
            JsonSchemaSubset.SubsetProblems(Schema("""
                {"type":"object","additionalProperties":false,"properties":{"a":{"$ref":"#/$defs/x","type":"object","description":"kept","minLength":1}},"$defs":{"x":{"type":"string"}}}
                """)));

        Assert.Empty(JsonSchemaSubset.SubsetProblems(Schema("""
            {"$ref":"#/$defs/x","title":"t","description":"d","default":"z","$defs":{"x":{"type":"string"}}}
            """)));
    }

    /// <summary>Definitions are held at the root alone, where a <c>$ref</c> finds them; one held lower is refused.</summary>
    [Fact]
    public void DefinitionsHeldBelowTheRoot_AreRefused()
    {
        Assert.Equal(
            ["$.properties.a.$defs: definitions are held at the schema's root, where '#/$defs/name' finds them"],
            JsonSchemaSubset.SubsetProblems(Schema("""
                {"type":"object","additionalProperties":false,"properties":{"a":{"type":"string","$defs":{"x":{"type":"string"}}}}}
                """)));
    }

    /// <summary>
    /// The document check refuses a schema the subset check refuses - one that is no object, names a definition
    /// it lacks, or a format nothing checks - as its caller's defect, never passing a document by it.
    /// </summary>
    [Fact]
    public void ASchemaWithSubsetProblems_IsRefusedByTheDocumentCheck()
    {
        foreach (var (schema, problem) in (IReadOnlyList<(string, string)>)
            [
                ("true", "$: a schema is a JSON object"),
                ("""{"$ref":"#/$defs/nowhere"}""", "$.$ref: '#/$defs/nowhere' names no definition of this schema"),
                ("""{"type":"string","format":"email"}""", "$.format: is not one of date-time, date, uuid"),
            ])
        {
            var thrown = Assert.Throws<ArgumentException>("schema", () => JsonSchemaSubset.Problems(Schema(schema), JsonNode.Parse("\"x\"")));

            Assert.Equal($"The schema is not one this engine reads, so no document is held to it: {problem}. (Parameter 'schema')", thrown.Message);
        }
    }

    /// <summary>A malformed type is refused as a problem, never thrown: the walk answers over whatever was parsed.</summary>
    [Fact]
    public void AMalformedType_IsAProblem_NeverAThrow()
    {
        Assert.Equal(
            ["$.type: is not one of object, array, string, integer, number, boolean, null, or one of them with null"],
            JsonSchemaSubset.SubsetProblems(Schema("""{"type":[1,"null"]}""")));
    }

    /// <summary><c>const</c> holds null where it says null, and refuses everything else.</summary>
    [Fact]
    public void AConstOfNull_HoldsNullOnly()
    {
        var schema = Schema("""{"const":null}""");

        Assert.Empty(JsonSchemaSubset.SubsetProblems(schema));
        Assert.Empty(JsonSchemaSubset.Problems(schema, null));
        Assert.Equal(["$: 3 is not null"], JsonSchemaSubset.Problems(schema, JsonNode.Parse("3")));
    }

    /// <summary>
    /// What a schema says beside <c>anyOf</c> holds whether or not a shape answers: a type, an <c>enum</c>, each
    /// refuses a value a shape takes.
    /// </summary>
    [Fact]
    public void WhatASchemaSaysBesideAnyOf_IsCheckedToo()
    {
        var typed = Schema("""{"anyOf":[{"type":"string"},{"type":"integer"}],"type":"integer"}""");

        Assert.Empty(JsonSchemaSubset.SubsetProblems(typed));
        Assert.Empty(JsonSchemaSubset.Problems(typed, JsonNode.Parse("7")));
        Assert.Equal(["$: \"7\" is not an integer"], JsonSchemaSubset.Problems(typed, JsonNode.Parse("\"7\"")));
        Assert.Equal(
            ["$: answers none of the 2 shapes its schema offers", "$: true is not an integer"],
            JsonSchemaSubset.Problems(typed, JsonNode.Parse("true")));

        var listed = Schema("""{"anyOf":[{"type":"string"}],"enum":["a"]}""");

        Assert.Empty(JsonSchemaSubset.Problems(listed, JsonNode.Parse("\"a\"")));
        Assert.Equal(["$: \"z\" is not one of \"a\""], JsonSchemaSubset.Problems(listed, JsonNode.Parse("\"z\"")));
    }

    /// <summary>
    /// A schema that says nothing of an object's properties itself refuses none of them: the shape of its
    /// <c>anyOf</c> that names them speaks for them.
    /// </summary>
    [Fact]
    public void ASchemaSayingNothingOfProperties_LeavesThemToItsShapes()
    {
        var schema = Schema("""
            {"anyOf":[{"type":"object","additionalProperties":false,"properties":{"a":{"type":"integer"}}}]}
            """);

        Assert.Empty(JsonSchemaSubset.Problems(schema, JsonNode.Parse("""{"a":1}""")));
        Assert.Equal(
            ["$: answers none of the 1 shape its schema offers"],
            JsonSchemaSubset.Problems(schema, JsonNode.Parse("""{"a":"x"}""")));
    }

    /// <summary>Every break a document can make is named at the document's own path.</summary>
    [Fact]
    public void EveryBreakADocumentMakes_IsNamedAtItsOwnPath()
    {
        var schema = JsonNode.Parse("""
            {
                "type": "object",
                "additionalProperties": false,
                "required": ["findings"],
                "properties": {
                    "findings": {
                        "type": "array",
                        "minItems": 1,
                        "items": {
                            "type": "object",
                            "additionalProperties": false,
                            "required": ["severity"],
                            "properties": {
                                "severity": {"enum": ["critical", "high", "medium", "low", "nit"]},
                                "where": {"type": "string", "format": "uuid"}
                            }
                        }
                    },
                    "title": {"type": ["string", "null"]}
                }
            }
            """);

        var problems = JsonSchemaSubset.Problems(schema!, JsonNode.Parse("""
            {"findings": [{"severity": "urgent"}], "extra": 1}
            """));

        Assert.Equal(
            [
                "$.findings[0].severity: \"urgent\" is not one of \"critical\", \"high\", \"medium\", \"low\", \"nit\"",
                "$.extra: is said by no property of the schema",
            ],
            problems);

        Assert.Empty(JsonSchemaSubset.Problems(schema!, JsonNode.Parse("""
            {"findings": [{"severity": "low", "where": "2f8e5a6b-9c4d-4e8a-a3b2-1d0c9e8f7a6b"}], "title": null}
            """)));

        Assert.Equal(
            ["$.findings[0].where: \"not-a-uuid\" is not a uuid"],
            JsonSchemaSubset.Problems(schema!, JsonNode.Parse("""
                {"findings": [{"severity": "low", "where": "not-a-uuid"}]}
                """)));

        Assert.Equal(
            ["$.findings: holds 0 items and at least 1 is asked"],
            JsonSchemaSubset.Problems(schema!, JsonNode.Parse("""{"findings": []}""")));

        Assert.Equal(
            ["$: 'findings' is required and missing"],
            JsonSchemaSubset.Problems(schema!, JsonNode.Parse("""{"title": "x"}""")));

        Assert.Equal(
            [
                "$.findings: holds 0 items and at least 1 is asked",
                "$.title: 3 is not a string or null",
            ],
            JsonSchemaSubset.Problems(schema!, JsonNode.Parse("""{"findings": [], "title": 3}""")));
    }

    /// <summary>An array longer than its <c>maxItems</c> is refused, naming how many it holds and how many are taken.</summary>
    [Fact]
    public void AnArrayLongerThanItsMaxItems_IsRefused()
    {
        var schema = Schema("""{"type":"array","maxItems":2,"items":{"type":"integer"}}""");

        Assert.Empty(JsonSchemaSubset.Problems(schema, JsonNode.Parse("[1,2]")));
        Assert.Equal(["$: holds 3 items and at most 2 are taken"], JsonSchemaSubset.Problems(schema, JsonNode.Parse("[1,2,3]")));
        Assert.Equal(
            ["$: holds 2 items and at most 1 is taken"],
            JsonSchemaSubset.Problems(Schema("""{"type":"array","maxItems":1}"""), JsonNode.Parse("[1,2]")));
    }

    /// <summary>
    /// An integer is any number with no fraction, as JSON Schema means one: <c>7.0</c> is one, and so is a number written
    /// with an exponent that leaves no fraction, or one too large for any machine number; one with a fraction is refused,
    /// however far down its fraction lies.
    /// </summary>
    [Theory]
    [InlineData("7", true)]
    [InlineData("7.0", true)]
    [InlineData("-0.0", true)]
    [InlineData("1.5e1", true)]
    [InlineData("100E-2", true)]
    [InlineData("1e400", true)]
    [InlineData("123456789012345678901234567890", true)]
    [InlineData("7.5", false)]
    [InlineData("1.25e1", false)]
    [InlineData("150e-2", false)]
    [InlineData("1e-400", false)]
    [InlineData("1.0000000000000000000000000000001", false)]
    public void AnInteger_IsAnyNumberWithNoFraction(string number, bool holds)
    {
        var problems = JsonSchemaSubset.Problems(Schema("""{"type":"integer"}"""), JsonNode.Parse(number));

        Assert.Equal(holds ? [] : [$"$: {number} is not an integer"], problems);
    }

    /// <summary>
    /// A count - <c>minItems</c>, <c>maxItems</c> - is a whole number from 0 however it is written: <c>2.0</c> is two,
    /// both where the schema is checked and where a document is held to it, and a fraction or a number below 0 is
    /// refused where the schema says it.
    /// </summary>
    [Fact]
    public void ACount_IsAWholeNumberFromNought_HoweverItIsWritten()
    {
        var schema = Schema("""{"type":"array","minItems":2.0,"maxItems":3e0}""");

        Assert.Empty(JsonSchemaSubset.Problems(schema, JsonNode.Parse("[1,2]")));
        Assert.Equal(["$: holds 1 item and at least 2 are asked"], JsonSchemaSubset.Problems(schema, JsonNode.Parse("[1]")));
        Assert.Equal(["$: holds 4 items and at most 3 are taken"], JsonSchemaSubset.Problems(schema, JsonNode.Parse("[1,2,3,4]")));
        Assert.Equal(
            ["$.minItems: is not a whole number from 0", "$.maxItems: is not a whole number from 0"],
            JsonSchemaSubset.SubsetProblems(JsonNode.Parse("""{"type":"array","minItems":2.5,"maxItems":-1}""")!));
    }

    /// <summary>
    /// A date-time is RFC 3339's: the time to the second with any fraction of it, <c>Z</c> or an offset always,
    /// <c>T</c> and <c>Z</c> in either case, every part in its range, and a leap second only in the last minute
    /// of a UTC day.
    /// </summary>
    [Theory]
    [InlineData("2026-10-18T10:00:00.123Z", true)]
    [InlineData("2026-10-18T10:00:00Z", true)]
    [InlineData("2026-10-18t10:00:00.1234567z", true)]
    [InlineData("2026-10-18T10:00:00.123456789+02:00", true)]
    [InlineData("2024-02-29T00:00:00-03:30", true)]
    [InlineData("1998-12-31T23:59:60Z", true)]
    [InlineData("1998-12-31T15:59:60.123-08:00", true)]
    [InlineData("2026-10-18T10:00:00", false)]
    [InlineData("2026-10-18 10:00:00Z", false)]
    [InlineData("2026-10-18T10:00:00.Z", false)]
    [InlineData("2026-10-18T10:00Z", false)]
    [InlineData("2026-10-18T10:00:00+0200", false)]
    [InlineData("2026-10-18T10:00:00Z ", false)]
    [InlineData("2026-02-29T10:00:00Z", false)]
    [InlineData("2026-10-18T24:00:00Z", false)]
    [InlineData("2026-10-18T10:60:00Z", false)]
    [InlineData("2026-10-18T10:00:00+24:00", false)]
    [InlineData("1998-12-31T23:58:60Z", false)]
    public void ADateTime_IsRfc3339s(string text, bool holds)
    {
        var problems = JsonSchemaSubset.Problems(Schema("""{"type":"string","format":"date-time"}"""), JsonValue.Create(text));

        Assert.Equal(holds ? [] : [$"$: \"{text}\" is not a date-time"], problems);
    }

    /// <summary>
    /// A date is RFC 3339's full-date: four, two and two digits, on a day its month holds - from the year 0000,
    /// which RFC 3339 writes and .NET's dates do not.
    /// </summary>
    [Theory]
    [InlineData("2026-01-15", true)]
    [InlineData("2024-02-29", true)]
    [InlineData("0000-02-29", true)]
    [InlineData("2026-02-29", false)]
    [InlineData("2026-04-31", false)]
    [InlineData("2026-13-01", false)]
    [InlineData("2026-1-15", false)]
    [InlineData("01/15/2026", false)]
    [InlineData("2026-01-15T00:00:00Z", false)]
    public void ADate_IsRfc3339sFullDate(string text, bool holds)
    {
        var problems = JsonSchemaSubset.Problems(Schema("""{"type":"string","format":"date"}"""), JsonValue.Create(text));

        Assert.Equal(holds ? [] : [$"$: \"{text}\" is not a date"], problems);
    }

    /// <summary>A uuid is written hyphenated, 8-4-4-4-12, in either case; braces, parentheses and no hyphens are refused.</summary>
    [Theory]
    [InlineData("2f8e5a6b-9c4d-4e8a-a3b2-1d0c9e8f7a6b", true)]
    [InlineData("2F8E5A6B-9C4D-4E8A-A3B2-1D0C9E8F7A6B", true)]
    [InlineData("{2f8e5a6b-9c4d-4e8a-a3b2-1d0c9e8f7a6b}", false)]
    [InlineData("(2f8e5a6b-9c4d-4e8a-a3b2-1d0c9e8f7a6b)", false)]
    [InlineData("2f8e5a6b9c4d4e8aa3b21d0c9e8f7a6b", false)]
    [InlineData("2f8e5a6b-9c4d-4e8a-a3b2-1d0c9e8f7a6g", false)]
    [InlineData(" 2f8e5a6b-9c4d-4e8a-a3b2-1d0c9e8f7a6b", false)]
    public void AUuid_IsWrittenHyphenated(string text, bool holds)
    {
        var problems = JsonSchemaSubset.Problems(Schema("""{"type":"string","format":"uuid"}"""), JsonValue.Create(text));

        Assert.Equal(holds ? [] : [$"$: \"{text}\" is not a uuid"], problems);
    }

    /// <summary>
    /// A pattern is read as ECMA-262 reads it: <c>$</c> is the end of the text alone, never the place before a
    /// final line feed; <c>.</c> takes no line terminator; <c>\d</c> and <c>\w</c> are ASCII; an identity escape
    /// is its character; <c>\s</c> is ECMA-262's white space and <c>\S</c> its complement, in a class or out;
    /// within a class, or escaped, <c>$</c> and <c>.</c> are characters; and a class closes at its first
    /// <c>]</c>, so <c>[]</c> is the empty class nothing answers, <c>[^]</c> takes any one character, and
    /// <c>[a-z-[aeiou]]</c> is a class and a <c>]</c>, never .NET's subtraction.
    /// </summary>
    [Theory]
    [InlineData(@"^[a-z]+$", "abc", null)]
    [InlineData(@"^[a-z]+$", "abc\n", "\"abc\\n\"")]
    [InlineData(@"^.$", "a", null)]
    [InlineData(@"^.$", "\r", "\"\\r\"")]
    [InlineData(@"^\d+$", "٤", "\"٤\"")]
    [InlineData(@"^\w+$", "é", "\"é\"")]
    [InlineData(@"^\q$", "q", null)]
    [InlineData(@"^[$.]+$", "$.", null)]
    [InlineData(@"^a\$\.$", "a$.", null)]
    [InlineData(@"^a\$\.$", "a$x", "\"a$x\"")]
    [InlineData(@"[]a]", "]a", "\"]a\"")]
    [InlineData(@"^[^]$", "x", null)]
    [InlineData(@"^[^]$", "x\n", "\"x\\n\"")]
    [InlineData(@"^\s$", "\uFEFF", null)]
    [InlineData(@"^\s$", "\u2028", null)]
    [InlineData(@"^\S+$", "a\u00A0b", "\"a\\u00A0b\"")]
    [InlineData(@"^[\s]$", "\u00A0", null)]
    [InlineData(@"^[\S]+$", "a\u3000b", "\"a\\u3000b\"")]
    [InlineData(@"^[x\S]+$", "x-y", null)]
    [InlineData(@"^[x\S]+$", "x\u00A0", "\"x\\u00A0\"")]
    [InlineData(@"^[^\S]$", "\u00A0", null)]
    [InlineData(@"^[^a\S]$", "\u00A0", null)]
    [InlineData(@"^[^ \S]$", " ", "\" \"")]
    [InlineData(@"^[a-z-[aeiou]]$", "b]", null)]
    [InlineData(@"^[a-z-[aeiou]]$", "b", "\"b\"")]
    [InlineData(@"^[\A\Z\z\G]+$", "AZzG", null)]
    [InlineData(@"^a\<b$", "a<b", null)]
    [InlineData(@"^(a)(?<x>b)\1$", "aba", null)]
    [InlineData(@"^(?<x>a)\1\k<x>$", "aaa", null)]
    [InlineData(@"^(a)\2$", "a\u0002", null)]
    public void APattern_IsReadAsEcma262ReadsIt(string pattern, string text, string? refused)
    {
        var schema = new JsonObject { ["type"] = "string", ["pattern"] = pattern };
        IReadOnlyList<string> expected = refused is null ? [] : [$"$: {refused} does not match its pattern"];

        Assert.Equal(expected, JsonSchemaSubset.Problems(schema, JsonValue.Create(text)));
    }

    /// <summary>
    /// A pattern ECMA-262 does not write - an inline option, a comment, an atomic group - or one that cannot be
    /// read at all is refused where the schema says it, and a named group, which both dialects write, is read.
    /// </summary>
    [Theory]
    [InlineData("(?i)abc", "$.pattern: '(?i' is not ECMA-262, the dialect a pattern is written in")]
    [InlineData("(?#note)a$", "$.pattern: '(?#' is not ECMA-262, the dialect a pattern is written in")]
    [InlineData("(?>a)", "$.pattern: '(?>' is not ECMA-262, the dialect a pattern is written in")]
    [InlineData("(?<a-b>c)", "$.pattern: '(?<' is not ECMA-262, the dialect a pattern is written in")]
    [InlineData("(", "$.pattern: is not a pattern this engine can read")]
    [InlineData(@"\c$", "$.pattern: is not a pattern this engine can read")]
    [InlineData("(?<name>a)(?:b)(?=c)(?!d)(?<=e)(?<!f)", null)]
    public void APatternNotEcma262Writes_IsRefusedWhereTheSchemaIs(string pattern, string? problem)
    {
        var schema = new JsonObject { ["type"] = "string", ["pattern"] = pattern };
        IReadOnlyList<string> expected = problem is null ? [] : [problem];

        Assert.Equal(expected, JsonSchemaSubset.SubsetProblems(schema));
    }

    /// <summary>
    /// What .NET, which runs a pattern, reads one way and ECMA-262 another is refused where the schema says it, rather
    /// than read either way: .NET's anchors outside a class, its categories, bell and escape character in a class or
    /// out, its spellings of a backreference beside ECMA-262's <c>\k&lt;name&gt;</c>, and a backreference by number
    /// that reaches another group in each - .NET counting the named groups last - or that .NET reads as a group and a
    /// digit where ECMA-262 reads the character it codes. What both read alike is read
    /// (<see cref="APattern_IsReadAsEcma262ReadsIt"/>).
    /// </summary>
    [Theory]
    [InlineData(@"\Aabc", @"\A")]
    [InlineData(@"abc\Z", @"\Z")]
    [InlineData(@"abc\z", @"\z")]
    [InlineData(@"\Gabc", @"\G")]
    [InlineData(@"^\p{L}+$", @"\p")]
    [InlineData(@"^\P{L}+$", @"\P")]
    [InlineData(@"^[\p{L}]+$", @"\p")]
    [InlineData(@"^[^\P{Lu}]$", @"\P")]
    [InlineData(@"^\a$", @"\a")]
    [InlineData(@"^[\e]$", @"\e")]
    [InlineData(@"^(?<x>a)\k'x'$", @"\k'x'")]
    [InlineData(@"^(?<x>a)\<x>$", @"\<x>")]
    [InlineData(@"^(?<x>a)\'x'$", @"\'x'")]
    [InlineData(@"^(a)\k<1>$", @"\k<1>")]
    [InlineData(@"^(?<x>a)(b)\1$", @"\1")]
    [InlineData(@"^(a)\12$", @"\12")]
    [InlineData(@"^\2(?<x>a)(b)$", @"\2")]
    public void APatternDotNetReadsOtherwise_IsRefusedWhereTheSchemaIs(string pattern, string written)
    {
        var schema = new JsonObject { ["type"] = "string", ["pattern"] = pattern };

        Assert.Equal(
            [$"$.pattern: '{written}' means one thing to .NET and another to ECMA-262, the dialect a pattern is written in"],
            JsonSchemaSubset.SubsetProblems(schema));
    }

    /// <summary>
    /// A group's name given twice is refused where the schema says it - in one alternative or in two - since .NET reads
    /// it as one group and a provider refuses the schema outright, and the subset is what every client is sent. Names
    /// that differ are read.
    /// </summary>
    [Theory]
    [InlineData("^(?<x>a)(?<x>b)$", "x")]
    [InlineData("^(?<x>a)|(?<x>b)$", "x")]
    [InlineData("^(?<x>a)(?<y>b)(?:(?<y>c))$", "y")]
    public void AGroupNamedTwice_IsRefusedWhereTheSchemaIs(string pattern, string name)
    {
        var schema = new JsonObject { ["type"] = "string", ["pattern"] = pattern };

        Assert.Equal(
            [$"$.items.pattern: '(?<{name}>' names group '{name}' a second time, which a provider reading ECMA-262, the dialect a pattern is written in, refuses"],
            JsonSchemaSubset.SubsetProblems(new JsonObject { ["type"] = "array", ["items"] = schema }));
        Assert.Empty(JsonSchemaSubset.SubsetProblems(new JsonObject { ["type"] = "string", ["pattern"] = "^(?<x>a)(?<y>b)$" }));
    }

    /// <summary>
    /// A name no identifier spells is written in brackets, as JSON - in the schema's paths and the document's -
    /// so a name holding a dot is never read as two, and names and values are written as they were, escaping only
    /// what JSON itself must.
    /// </summary>
    [Fact]
    public void ANameNoIdentifierSpells_IsWrittenInBrackets()
    {
        Assert.Equal(
            ["$.properties[\"a.b\"]: an object holds additionalProperties: false, saying every property is one it names"],
            JsonSchemaSubset.SubsetProblems(Schema("""
                {"type":"object","additionalProperties":false,"properties":{"a.b":{"type":"object","properties":{}}}}
                """)));

        var schema = Schema("""
            {"type":"object","additionalProperties":false,"properties":{"a.b":{"type":"string"},"$x":{"enum":["a"]},"under_score1":{"type":"string"}}}
            """);

        Assert.Equal(
            [
                "$[\"a.b\"]: 3 is not a string",
                "$.$x: \"café\" is not one of \"a\"",
                "$.under_score1: 4 is not a string",
                "$[\"a\\\"b\"]: is said by no property of the schema",
                "$[\"café\"]: is said by no property of the schema",
                "$[\"1a\"]: is said by no property of the schema",
            ],
            JsonSchemaSubset.Problems(schema, JsonNode.Parse("""
                {"a.b":3,"$x":"café","under_score1":4,"a\"b":1,"café":2,"1a":5}
                """)));
    }

    /// <summary><c>anyOf</c> is answered by any one shape, and refused where none is.</summary>
    [Fact]
    public void AnyOfIsAnsweredByAnyOneShape()
    {
        var schema = JsonNode.Parse("""
            {"anyOf":[{"type":"string"},{"type":"integer"}]}
            """);

        Assert.Empty(JsonSchemaSubset.SubsetProblems(schema!));
        Assert.Empty(JsonSchemaSubset.Problems(schema!, JsonNode.Parse("\"7\"")));
        Assert.Empty(JsonSchemaSubset.Problems(schema!, JsonNode.Parse("7")));
        Assert.Equal(
            ["$: answers none of the 2 shapes its schema offers"],
            JsonSchemaSubset.Problems(schema!, JsonNode.Parse("true")));
    }

    /// <summary>A pattern is matched within one second: one that cannot be read says so, and never holds the run.</summary>
    [Fact]
    public void APatternIsReadWithinOneSecond()
    {
        var schema = JsonNode.Parse("""{"type":"string","pattern":"^(a+)+$"}""");

        Assert.Empty(JsonSchemaSubset.SubsetProblems(schema!));

        var problems = JsonSchemaSubset.Problems(schema!, JsonNode.Parse("\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaab\""));

        Assert.Equal(["$: its pattern could not be read within a second"], problems);
    }

    /// <summary>
    /// A schema is read for what it says of a document, as an expression's path is typed: the kinds a value may be -
    /// a number standing for a whole number too - what each member is, and what each item is, through a reference and
    /// through each shape of an anyOf. A member no closed object names is none, and where nothing says what a value is,
    /// it and its members and items may be anything.
    /// </summary>
    [Fact]
    public void AReading_SaysTheKindsMembersAndItemsASchemaAllows()
    {
        var reading = JsonSchemaSubset.Read(Schema("""
            {
              "type": "object",
              "additionalProperties": false,
              "properties": {
                "title": { "type": "string" },
                "count": { "type": ["integer", "null"] },
                "score": { "type": "number" },
                "items": { "type": "array", "items": { "$ref": "#/$defs/item" } },
                "loose": { "type": "array" },
                "either": { "anyOf": [ { "type": "string" }, { "$ref": "#/$defs/item" } ] },
                "level": { "enum": ["low", 3, null] },
                "free": { "description": "anything" }
              },
              "$defs": {
                "item": { "type": "object", "additionalProperties": false, "properties": { "ok": { "type": "boolean" } } }
              }
            }
            """));

        Assert.Equal(["object"], reading.Kinds);
        Assert.Equal(["string"], reading.Member("title")!.Kinds);
        Assert.Equal(["integer", "null"], reading.Member("count")!.Kinds);
        Assert.Equal(["integer", "number"], reading.Member("score")!.Kinds);
        Assert.Null(reading.Member("titel"));

        // Items through a reference, and a member of each.
        var item = reading.Member("items")!.Items!;

        Assert.Equal(["object"], item.Kinds);
        Assert.Equal(["boolean"], item.Member("ok")!.Kinds);
        Assert.Null(item.Member("oke"));
        Assert.Null(reading.Member("title")!.Items);
        Assert.Null(reading.Member("title")!.Member("x"));

        // A list whose items are not said holds items of any kind.
        Assert.Null(reading.Member("loose")!.Items!.Kinds);

        // Each shape of an anyOf is one way the value may be: a member is one of the shape that holds it.
        var either = reading.Member("either")!;

        Assert.Equal(["object", "string"], either.Kinds);
        Assert.Equal(["boolean"], either.Member("ok")!.Kinds);

        // An enum's values say its kinds; a schema that says no kind says any, and its members may be anything.
        Assert.Equal(["string", "integer", "null"], reading.Member("level")!.Kinds);
        Assert.Null(reading.Member("free")!.Kinds);
        Assert.Null(reading.Member("free")!.Member("anything")!.Kinds);
    }

    /// <summary>
    /// A reading is joined with another as either, has a kind taken out of it, and is made of kinds alone or as a list
    /// of what its items are - the readings an expression's operators yield. A schema the subset refuses types nothing:
    /// reading it is refused, as holding a document to it is.
    /// </summary>
    [Fact]
    public void AReading_IsJoinedNarrowedAndMadeOfKinds_AndNoneIsMadeOfASchemaTheSubsetRefuses()
    {
        var text = JsonSchemaSubset.Reading.Of("string", "null");
        var list = JsonSchemaSubset.Reading.ListOf(JsonSchemaSubset.Reading.Of("boolean"));

        Assert.Equal(["string", "null"], text.Kinds);
        Assert.Equal(["string"], text.Without("null").Kinds);
        Assert.Equal(["array", "string", "null"], list.Or(text).Kinds);
        Assert.Equal(["boolean"], list.Or(text).Items!.Kinds);
        Assert.True(text.MayBe("null"));
        Assert.False(text.MayBe("boolean"));
        Assert.True(JsonSchemaSubset.Reading.Anything.MayBe("boolean"));
        Assert.Null(JsonSchemaSubset.Reading.Anything.Kinds);
        Assert.Equal(["object", "array", "string", "integer", "number", "boolean"], JsonSchemaSubset.Reading.Anything.Without("null").Kinds);

        var refused = Assert.Throws<ArgumentException>("schema", () => JsonSchemaSubset.Read(Schema("""{"type":"object"}""")));

        Assert.Equal(
            "The schema is not one this engine reads, so nothing is typed by it: $: an object holds additionalProperties: false, saying every property is one it names. (Parameter 'schema')",
            refused.Message);
    }

    /// <summary>A document's problems are named from where it stands in a larger one: its root's path given, every path after it.</summary>
    [Fact]
    public void ADocumentsProblems_AreNamedFromWhereItStands()
    {
        var schema = Schema("""{"type":"object","additionalProperties":false,"required":["a"],"properties":{"a":{"type":"array","items":{"type":"string"}},"b c":{"type":"string"}}}""");

        Assert.Equal(
            ["$.output: 'a' is required and missing", "$.output[\"b c\"]: 1 is not a string"],
            JsonSchemaSubset.Problems(schema, JsonNode.Parse("""{"b c":1}"""), "$.output"));
        Assert.Equal(["$.output.a[1]: 2 is not a string"], JsonSchemaSubset.Problems(schema, JsonNode.Parse("""{"a":["x",2]}"""), "$.output"));
        Assert.Equal(JsonSchemaSubset.Problems(schema, JsonNode.Parse("{}"), "$"), JsonSchemaSubset.Problems(schema, JsonNode.Parse("{}")));
    }

    /// <summary>
    /// A text an enum or a const holds in another case is spelled as the schema spells it - through a reference, in a
    /// list's items, under the first shape of an anyOf that the spelled document answers - where exactly one value
    /// matches it so; every other value, and the document given, is left as it was.
    /// </summary>
    [Fact]
    public void AValueInAnotherCase_IsSpelledAsTheSchemaSpellsIt()
    {
        var schema = Schema("""
            {
              "type": "object", "additionalProperties": false,
              "properties": {
                "level": { "$ref": "#/$defs/level" },
                "levels": { "type": "array", "items": { "$ref": "#/$defs/level" } },
                "kind": { "const": "review" },
                "either": { "anyOf": [ { "type": "integer" }, { "enum": ["Yes", "no"] } ] },
                "alike": { "enum": ["ab", "AB"] },
                "count": { "enum": [1, 2] },
                "free": { "type": "string" }
              },
              "$defs": { "level": { "enum": ["high", "low"] } }
            }
            """);
        var given = JsonNode.Parse("""{"level":"HIGH","levels":["Low","high"],"kind":"REVIEW","either":"yes","alike":"AB","count":1,"free":"HIGH"}""");

        var spelled = JsonSchemaSubset.Respelled(schema, given);

        Assert.Equal("""{"level":"high","levels":["low","high"],"kind":"review","either":"Yes","alike":"AB","count":1,"free":"HIGH"}""", spelled!.ToJsonString());
        Assert.Equal("""{"level":"HIGH","levels":["Low","high"],"kind":"REVIEW","either":"yes","alike":"AB","count":1,"free":"HIGH"}""", given!.ToJsonString());
        Assert.Empty(JsonSchemaSubset.Problems(schema, spelled));
        Assert.Null(JsonSchemaSubset.Respelled(schema, null));
        Assert.Equal("""{"either":"maybe","alike":"Ab"}""", JsonSchemaSubset.Respelled(schema, JsonNode.Parse("""{"either":"maybe","alike":"Ab"}"""))!.ToJsonString());
        Assert.Throws<ArgumentException>("schema", () => JsonSchemaSubset.Respelled(Schema("""{"type":"object"}"""), given));
    }

    /// <summary>Every schema a schema holds is walked, itself first: its properties', its items', its anyOf's shapes and its definitions, each in its order.</summary>
    [Fact]
    public void EverySchemaASchemaHolds_IsWalked_ItselfFirst()
    {
        var schema = Schema("""
            {
              "title": "root", "type": "object", "additionalProperties": false,
              "properties": {
                "a": { "title": "a", "type": "array", "items": { "title": "a-item", "type": "string" } },
                "b": { "title": "b", "anyOf": [ { "title": "b-1", "type": "null" }, { "title": "b-2", "$ref": "#/$defs/d" } ] }
              },
              "$defs": { "d": { "title": "d", "type": "string" } }
            }
            """).AsObject();

        Assert.Equal(["root", "a", "a-item", "b", "b-1", "b-2", "d"], JsonSchemaSubset.SchemasIn(schema).Select(each => each["title"]!.GetValue<string>()));
    }
}
