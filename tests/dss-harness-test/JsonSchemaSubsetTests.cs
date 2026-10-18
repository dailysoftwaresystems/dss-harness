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
    /// <summary>Every schema the shipped workflow holds is a schema this engine reads.</summary>
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
            Assert.Empty(JsonSchemaSubset.SubsetProblems(schema!));
        }
    }

    /// <summary>A schema from its text, refused by the parse itself where the text is not JSON.</summary>
    private static JsonNode Schema(string text)
    {
        var node = JsonNode.Parse(text);

        Assert.NotNull(node);
        return node!;
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

    /// <summary>A definition a schema reaches through itself is refused, and one it reaches once is read.</summary>
    [Fact]
    public void ARefThatReachesItself_IsRefused()
    {
        var recursive = Schema("""
            {"type":"object","additionalProperties":false,"properties":{"child":{"$ref":"#/$defs/node"}},"$defs":{"node":{"type":"object","additionalProperties":false,"properties":{"child":{"$ref":"#/$defs/node"}}}}}
            """);

        // A definition that names itself in its own body is refused where it is declared.
        Assert.Contains(
            JsonSchemaSubset.SubsetProblems(recursive!),
            problem => problem.EndsWith("reaches itself, and a definition never recurses", StringComparison.Ordinal));
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

    /// <summary>What a schema says beside <c>anyOf</c> is checked too: a shape unanswered, and a type unmet.</summary>
    [Fact]
    public void WhatASchemaSaysBesideAnyOf_IsCheckedToo()
    {
        var schema = Schema("""{"anyOf":[{"type":"string"},{"type":"integer"}],"type":"integer"}""");

        Assert.Empty(JsonSchemaSubset.SubsetProblems(schema));
        Assert.Empty(JsonSchemaSubset.Problems(schema, JsonNode.Parse("7")));
        Assert.Equal(
            ["$: answers none of the 2 shapes its schema offers", "$: true is not a integer"],
            JsonSchemaSubset.Problems(schema, JsonNode.Parse("true")));
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

        // A date is ISO 8601 alone: a day written as another culture spells it is refused.
        var dated = Schema("""{"type":"string","format":"date"}""");

        Assert.Empty(JsonSchemaSubset.Problems(dated, JsonNode.Parse("\"2026-01-15\"")));
        Assert.Equal(["$: \"01/15/2026\" is not a date"], JsonSchemaSubset.Problems(dated, JsonNode.Parse("\"01/15/2026\"")));

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
}
