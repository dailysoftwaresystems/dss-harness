using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using DssHarness.Core.Agents;
using DssHarness.Core.Agents.ClaudeApi;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Orchestration.Flow;
using DssHarness.Core.Schemas;

namespace DssHarness.Tests;

/// <summary>
/// The envelope every client asks for where a step names an output (<see cref="AgentEnvelope"/>), the gate that reads
/// every answer of it on all three clients (<see cref="StructuredOutputGate"/>), and the copy of it the Claude API is
/// sent (<see cref="ClaudeApiRequest.Sent"/>): check here, strip on send.
/// </summary>
public sealed class StructuredOutputTests
{
    private const string Step = """
        {
          "type": "object", "additionalProperties": false, "required": ["severity", "findings"],
          "properties": {
            "severity": { "type": "string", "enum": ["high", "low", "High-ish"] },
            "kind": { "const": "review" },
            "findings": { "type": "array", "minItems": 2, "maxItems": 4, "description": "What was found.", "items": { "$ref": "#/$defs/finding" } }
          },
          "$defs": { "finding": { "type": "object", "additionalProperties": false, "required": ["at"], "properties": { "at": { "type": "string" } } } }
        }
        """;

    private static readonly JsonObject Output = JsonNode.Parse("""{"severity":"high","findings":[{"at":"a.cs"},{"at":"b.cs"}]}""")!.AsObject();

    private readonly List<AgentEvent> _events = [];
    private readonly List<string> _sentBack = [];

    /// <summary>The shipped workflow's schemas, each by its file.</summary>
    public static TheoryData<string> ShippedSchemas() => [.. WorkflowTemplates.Files.Where(file => file.StartsWith("schemas/", StringComparison.Ordinal))];

    /// <summary>
    /// The envelope holds the step's schema whole as its output, null beside it, the step's definitions at its root where
    /// its references find them; its notes and assumptions are required, and questions offered only while the step may
    /// ask and has rounds left. Every envelope of every shipped schema is one the subset reads.
    /// </summary>
    [Theory]
    [MemberData(nameof(ShippedSchemas))]
    public void TheEnvelope_HoldsTheStepsSchemaWhole_AndIsOneTheSubsetReads(string file)
    {
        var schema = WorkflowTemplates.TextOf(file)!;

        foreach (var rounds in new int?[] { null, 0, 2 })
        {
            var envelope = JsonNode.Parse(AgentEnvelope.Schema(new AgentOutput("o", schema, AskRounds: rounds)))!.AsObject();
            var step = JsonNode.Parse(schema)!.AsObject();
            var definitions = step["$defs"]?.DeepClone();
            step.Remove("$defs");

            Assert.Empty(JsonSchemaSubset.SubsetProblems(envelope));
            Assert.Equal(["notes", "output", "assumptions"], envelope["required"]!.AsArray().Select(name => name!.GetValue<string>()));
            Assert.True(JsonNode.DeepEquals(new JsonArray(step, new JsonObject { ["type"] = "null" }), envelope["properties"]!["output"]!["anyOf"]));
            Assert.True(JsonNode.DeepEquals(definitions, envelope["$defs"]));
            Assert.Equal(rounds > 0, envelope["properties"]!.AsObject().ContainsKey("questions"));
        }
    }

    /// <summary>An answer that reads completes the run: its output, its notes and what it assumed, each masked; no repair is sent.</summary>
    [Fact]
    public async Task AnAnswerThatReads_CompletesTheRun_WithItsNotesAndAssumptions()
    {
        var answer = Envelopes.Of(Output, "kept sk-1 out of it", [Envelopes.Assumption("the key sk-1", "unused", "nothing reads it")]);

        var ended = await Checked(Gate(), answer, "the text sk-1");

        Assert.NotNull(ended);
        Assert.Equal((AgentRunStatus.Completed, """{"severity":"high","findings":[{"at":"a.cs"},{"at":"b.cs"}]}""", "kept *** out of it", "the text ***"), (ended.Status, ended.StructuredOutput, ended.Notes, ended.FinalText));
        Assert.Equal([new AgentAssumption("the key ***", "unused", "nothing reads it")], ended.Assumptions);
        Assert.Null(ended.Problem());
        Assert.Empty(_events);
        Assert.Empty(_sentBack);
    }

    /// <summary>
    /// What does not read is said by its path in the envelope: no object at all, a member the envelope does not have, the
    /// notes empty or missing, an assumption out of its shape, and the output's own problems under <c>$.output</c> - a bound
    /// of the definition's among them, whatever a client was sent.
    /// </summary>
    [Theory]
    [InlineData("\"text\"", new[] { StructuredOutputGate.NoObject })]
    [InlineData("""{"notes":"n","output":{"severity":"high","findings":[{"at":"a"},{"at":"b"}]},"assumptions":[],"extra":1}""", new[] { "$.extra: is said by no property of the schema" })]
    [InlineData("""{"notes":" ","output":{"severity":"high","findings":[{"at":"a"},{"at":"b"}]},"assumptions":[]}""", new[] { StructuredOutputGate.NotesEmpty })]
    [InlineData("""{"output":{"severity":"high","findings":[{"at":"a"},{"at":"b"}]},"assumptions":[]}""", new[] { "$: 'notes' is required and missing" })]
    [InlineData("""{"notes":"n","output":{"severity":"high","findings":[{"at":"a"},{"at":"b"}]},"assumptions":[{"about":"x"}]}""", new[] { "$.assumptions[0]: 'assumed' is required and missing", "$.assumptions[0]: 'why' is required and missing" })]
    [InlineData("""{"notes":"n","output":{"severity":"urgent","findings":[{"at":"a"}]},"assumptions":[]}""", new[] { "$.output.severity: \"urgent\" is not one of \"high\", \"low\", \"High-ish\"", "$.output.findings: holds 1 item and at least 2 are asked" })]
    [InlineData("""{"notes":"n","output":{"severity":"high","findings":[{"at":"a"},{"at":"b"},{"at":"c"},{"at":"d"},{"at":"e"}]},"assumptions":[]}""", new[] { "$.output.findings: holds 5 items and at most 4 are taken" })]
    [InlineData("""{"notes":"n","output":{"severity":"high","findings":[{"at":"a"},{"nope":1}]},"assumptions":[]}""", new[] { "$.output.findings[1]: 'at' is required and missing", "$.output.findings[1].nope: is said by no property of the schema" })]
    public void WhatDoesNotRead_IsSaidByItsPathInTheEnvelope(string answer, string[] problems)
        => Assert.Equal(problems, Gate().Problems(JsonNode.Parse(answer)));

    /// <summary>
    /// The output is null only where the answer asks: one that asks nothing is sent back, told whether it could have
    /// asked; and an answer with both an output and questions is sent back too.
    /// </summary>
    [Fact]
    public void ANullOutput_IsAnAnswerThatAsks_AndNoOther()
    {
        Assert.Equal(["$.output: is null; answer with the output, as its schema says"], Gate().Problems(Envelopes.Of(null)));
        Assert.Equal(
            ["$.output: is null, and the answer asks nothing; answer with the output, as its schema says, or ask the person under questions"],
            Gate(rounds: 1).Problems(Envelopes.Of(null, questions: [])));
        Assert.Equal([StructuredOutputGate.AskOrAnswer], Gate(rounds: 1).Problems(Envelopes.Of(Output, questions: [Envelopes.Question()])));
        Assert.Empty(Gate(rounds: 1).Problems(Envelopes.Of(Output, questions: [])));
    }

    /// <summary>
    /// An answer that asks where the step may not, or has asked as often as it may, is told so - to write what it would
    /// have asked under its assumptions - and its questions are never read.
    /// </summary>
    [Fact]
    public void AnAnswerThatAsks_WhereNoneCanBeAsked_IsToldSo()
    {
        var asking = Envelopes.Of(null, questions: [new JsonObject { ["question"] = 1 }]);

        Assert.Equal([StructuredOutputGate.NobodyAnswers, "$.output: is null; answer with the output, as its schema says"], Gate().Problems(asking));
        Assert.Equal([StructuredOutputGate.AskedAsOftenAsItMay, "$.output: is null; answer with the output, as its schema says"], Gate(rounds: 0).Problems(asking));
        Assert.Equal([StructuredOutputGate.NobodyAnswers], Gate().Problems(Envelopes.Of(Output, questions: [])));
    }

    /// <summary>
    /// A question is held to its shape: every member, at least two options, and a recommendation that is one of them,
    /// word for word.
    /// </summary>
    [Fact]
    public void AQuestion_IsHeldToItsShape()
    {
        var gate = Gate(rounds: 2);

        Assert.Equal(
            ["$.questions[1].recommended: \"use the cache\" is none of the 2 options; recommend one of them, word for word"],
            gate.Problems(Envelopes.Of(null, questions: [Envelopes.Question(), Envelopes.Question(recommended: "use the cache", options: ["Use the cache", "build again"])])));
        Assert.Equal(
            ["$.questions[0].options: holds 1 item and at least 2 are asked"],
            gate.Problems(Envelopes.Of(null, questions: [Envelopes.Question(options: ["only"])])));
        Assert.Equal(
            ["$.questions[0]: 'why' is required and missing"],
            gate.Problems(Envelopes.Of(null, questions: [new JsonObject(Envelopes.Question().Where(member => member.Key != "why").Select(member => KeyValuePair.Create(member.Key, member.Value?.DeepClone())))])));
        Assert.Equal(
            ["$.questions[0]: \"Which cache?\" is not an object"],
            gate.Problems(Envelopes.Of(null, questions: [JsonValue.Create("Which cache?")])));
    }

    /// <summary>
    /// An assumption's members and a question's are their records' as every record that keeps them is written, in order:
    /// an answer reads as the record it becomes.
    /// </summary>
    [Fact]
    public void TheMembersOfAnAssumptionAndAQuestion_AreTheirRecordsAsWritten()
    {
        static string[] Members(JsonObject shape) => [.. shape["properties"]!.AsObject().Select(member => member.Key)];
        static string[] Written(object record) => [.. JsonSerializer.SerializeToNode(record, record.GetType(), JsonStateFile.Options)!.AsObject().Select(member => member.Key)];

        Assert.Equal(["about", "assumed", "why"], Members(AgentEnvelope.Assumption()));
        Assert.Equal(Written(new AgentAssumption("a", "b", "c")), Members(AgentEnvelope.Assumption()));
        Assert.Equal(["question", "problem", "tried", "means", "options", "recommended", "why"], Members(AgentEnvelope.Question()));
        Assert.Equal(Written(new AgentQuestion("q", "p", "t", "m", ["a", "b"], "a", "w")), Members(AgentEnvelope.Question()));
    }

    /// <summary>An answer that asks ends the run <see cref="AgentRunStatus.Asked"/>, with its questions masked, its notes and its assumptions, said by a <see cref="PersonAsked"/>.</summary>
    [Fact]
    public async Task AnAnswerThatAsks_EndsTheRunAsked_AndSaysItAsksThePerson()
    {
        var answer = Envelopes.Of(null, "stopped before the build sk-1", [Envelopes.Assumption()], [Envelopes.Question("Which cache, sk-1?")]);

        var ended = await Checked(Gate(rounds: 1), answer, "asked");

        Assert.NotNull(ended);
        Assert.Equal((AgentRunStatus.Asked, null, "stopped before the build ***"), (ended.Status, ended.StructuredOutput, ended.Notes));
        var question = Assert.Single(ended.Questions);
        Assert.Equal(("Which cache, ***?", "use the cache"), (question.Question, question.Recommended));
        Assert.Equal(["use the cache", "build again"], question.Options);
        Assert.Single(ended.Assumptions);
        Assert.Null(ended.Problem());
        Assert.Equal(ended.Questions, Assert.IsType<PersonAsked>(Assert.Single(_events)).Questions);
    }

    /// <summary>
    /// A secret the output holds is masked where it stands, before the output is written as JSON: written first, a secret
    /// JSON escapes - a plus, a quote, a letter past ASCII - would pass a mask that reads the written text.
    /// </summary>
    [Fact]
    public async Task ASecretTheOutputHolds_IsMasked_HoweverJsonWritesIt()
    {
        var gate = new StructuredOutputGate(new AgentOutput("o", Step), new Masking("p+ss\"wörd"));

        var ended = await Checked(gate, Envelopes.Of(JsonNode.Parse("""{"severity":"high","findings":[{"at":"p+ss\"wörd"},{"at":"b"}]}""")), "t");

        Assert.Equal("""{"severity":"high","findings":[{"at":"***"},{"at":"b"}]}""", ended!.StructuredOutput);
    }

    /// <summary>
    /// A problem that quotes a secret the answer holds is masked in the repair the run records, and in the error of the
    /// run that ends invalid; the agent, which wrote it, is told it as it was.
    /// </summary>
    [Fact]
    public async Task AProblemThatQuotesASecret_IsMaskedInWhatTheRunRecords()
    {
        var gate = new StructuredOutputGate(new AgentOutput("o", Step, MaxRepairs: 1), new Masking("sk-1"));
        var answer = Envelopes.Of(JsonNode.Parse("""{"severity":"sk-1","findings":[{"at":"a"},{"at":"b"}]}"""));

        Assert.Null(await Checked(gate, answer, "t"));
        var ended = await Checked(gate, answer, "t");

        Assert.Equal(["$.output.severity: \"***\" is not one of \"high\", \"low\", \"High-ish\""], Assert.IsType<OutputRepair>(Assert.Single(_events)).Problems);
        Assert.Equal("the answer did not read against its schema after 1 repairs: $.output.severity: \"***\" is not one of \"high\", \"low\", \"High-ish\"", ended!.Error);
        Assert.Contains("\"sk-1\"", Assert.Single(_sentBack), StringComparison.Ordinal);
    }

    /// <summary>A text an enum or a const holds in another case reads as the schema spells it; one two values match alike is left to the check.</summary>
    [Fact]
    public async Task AValueInAnotherCase_ReadsAsTheSchemaSpellsIt()
    {
        var spelled = await Checked(Gate(), Envelopes.Of(JsonNode.Parse("""{"severity":"HIGH-ISH","kind":"Review","findings":[{"at":"a"},{"at":"b"}]}""")), "t");

        Assert.Equal("""{"severity":"High-ish","kind":"review","findings":[{"at":"a"},{"at":"b"}]}""", spelled!.StructuredOutput);

        const string Alike = """{"type":"object","additionalProperties":false,"required":["a"],"properties":{"a":{"enum":["x","X"]}}}""";
        Assert.Equal(["$.output.a: \"x \" is not one of \"x\", \"X\""], new StructuredOutputGate(new AgentOutput("o", Alike), new Masking()).Problems(Envelopes.Of(JsonNode.Parse("""{"a":"x "}"""))));
        Assert.Equal(JsonNode.Parse("""{"a":"x"}"""), JsonSchemaSubset.Respelled(JsonNode.Parse(Alike)!, JsonNode.Parse("""{"a":"x"}""")), JsonNode.DeepEquals);
    }

    /// <summary>An answer that does not read is sent back with its problems, at most as often as the output says, and the run then ends InvalidOutput.</summary>
    [Fact]
    public async Task AnAnswerThatDoesNotRead_IsSentBack_ThenTheOutputIsInvalid()
    {
        var gate = new StructuredOutputGate(new AgentOutput("o", Step, MaxRepairs: 1), new Masking());

        Assert.Null(await Checked(gate, JsonNode.Parse("[]"), "t"));
        var ended = await Checked(gate, JsonNode.Parse("[]"), "t");

        Assert.Equal((AgentRunStatus.InvalidOutput, $"the answer did not read against its schema after 1 repairs: {StructuredOutputGate.NoObject}"), (ended!.Status, ended.Error));
        Assert.Equal([StructuredOutputGate.Repair([StructuredOutputGate.NoObject])], _sentBack);
        Assert.Equal(1, Assert.IsType<OutputRepair>(Assert.Single(_events)).Attempt);
    }

    /// <summary>
    /// The copy the Claude API is sent moves each array bound structured outputs do not take - a minItems above one, and
    /// every maxItems - into that array's description in words, keeping a minItems of one; every other word stays.
    /// </summary>
    [Theory]
    [InlineData("""{"type":"array","minItems":2,"description":"What was found."}""", """{"type":"array","description":"What was found. At least 2 items."}""")]
    [InlineData("""{"type":"array","maxItems":1}""", """{"type":"array","description":"At most 1 item."}""")]
    [InlineData("""{"type":"array","minItems":2,"maxItems":4,"description":""}""", """{"type":"array","description":"At least 2 and at most 4 items."}""")]
    [InlineData("""{"type":"array","minItems":1,"maxItems":3}""", """{"type":"array","minItems":1,"description":"At most 3 items."}""")]
    [InlineData("""{"type":"array","minItems":0}""", """{"type":"array","minItems":0}""")]
    [InlineData(
        """{"type":"object","additionalProperties":false,"properties":{"a":{"anyOf":[{"type":"array","minItems":3},{"type":"null"}]},"b":{"$ref":"#/$defs/b"}},"$defs":{"b":{"type":"array","items":{"type":"array","maxItems":2}}}}""",
        """{"type":"object","additionalProperties":false,"properties":{"a":{"anyOf":[{"type":"array","description":"At least 3 items."},{"type":"null"}]},"b":{"$ref":"#/$defs/b"}},"$defs":{"b":{"type":"array","items":{"type":"array","description":"At most 2 items."}}}}""")]
    public void TheCopySentToTheClaudeApi_MovesEveryBoundItDoesNotTake_IntoTheDescription(string schema, string sent)
        => Assert.Equal(sent, ClaudeApiRequest.Sent(schema).ToJsonString());

    /// <summary>
    /// Pinned for every shipped schema: the copy of its envelope the Claude API is sent holds no bound the API refuses,
    /// and differs from the envelope by the bounds it moved alone - each said in its array's description - so the gate
    /// still holds the answer to the whole.
    /// </summary>
    [Theory]
    [MemberData(nameof(ShippedSchemas))]
    public void TheCopySentOfEachShippedSchema_DiffersByTheBoundsItMoved(string file)
    {
        var envelope = JsonNode.Parse(AgentEnvelope.Schema(new AgentOutput("o", WorkflowTemplates.TextOf(file)!, AskRounds: 2)))!.AsObject();
        var sent = ClaudeApiRequest.Sent(envelope.ToJsonString());

        Assert.Empty(JsonSchemaSubset.SubsetProblems(sent));

        foreach (var (whole, copy) in JsonSchemaSubset.SchemasIn(envelope).Zip(JsonSchemaSubset.SchemasIn(sent)))
        {
            var least = JsonSchemaSubset.Count(whole["minItems"]);
            var most = JsonSchemaSubset.Count(whole["maxItems"]);
            var moved = least > ClaudeApiRequest.MostLeastItems || most is not null;

            Assert.False(copy.ContainsKey("maxItems"));
            Assert.False(JsonSchemaSubset.Count(copy["minItems"]) > ClaudeApiRequest.MostLeastItems);

            if (moved)
            {
                Assert.EndsWith(" items.", copy["description"]!.GetValue<string>(), StringComparison.Ordinal);
                Assert.StartsWith(whole["description"]?.GetValue<string>() ?? string.Empty, copy["description"]!.GetValue<string>(), StringComparison.Ordinal);
            }

            var (bare, bareCopy) = (Bare(whole), Bare(copy));
            Assert.True(JsonNode.DeepEquals(bare, bareCopy), $"{file}: {bare.ToJsonString()} was sent as {bareCopy.ToJsonString()}");
        }
    }

    /// <summary>
    /// The bounds the shipped schemas say, which structured outputs do not take, are each moved: the triage's and the
    /// priority's two options of a stop, the diagnosis's four causes - each in an envelope that offers no question, so the
    /// bound moved is the schema's own - and the two options of a question the envelope asks.
    /// </summary>
    [Fact]
    public void TheShippedBounds_AreEachMovedIntoWords()
    {
        static string Sent(string file, int? rounds = null) => ClaudeApiRequest.Sent(AgentEnvelope.Schema(new AgentOutput("o", WorkflowTemplates.TextOf($"schemas/{file}.schema.json")!, AskRounds: rounds))).ToJsonString(new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

        Assert.Contains("The choices the person has, at least two, each a sentence that says what it leads to. At least 2 items.", Sent("triage"), StringComparison.Ordinal);
        Assert.Contains("The choices the person has, at least two, each a sentence that says what it leads to. At least 2 items.", Sent("priority"), StringComparison.Ordinal);
        Assert.Contains("Each is shaped as a plan's item and sized to one builder's cycle. At most 4 items.", Sent("diagnosis"), StringComparison.Ordinal);
        Assert.Contains($"The choices the person has, at least {AgentEnvelope.FewestOptions}, each a sentence that says what it leads to. At least 2 items.", Sent("summary", rounds: 1), StringComparison.Ordinal);
        Assert.DoesNotContain("maxItems", Sent("diagnosis"), StringComparison.Ordinal);
        Assert.DoesNotContain("\"minItems\":2", Sent("triage"), StringComparison.Ordinal);
    }

    /// <summary>
    /// The plan's work item is said once, as the definition its items and a scope piece's item both refer to: said twice
    /// inline, it made a grammar the Claude API refused as too large (live, 2026-10-26), and the two copies had drifted.
    /// </summary>
    [Fact]
    public void ThePlansWorkItem_IsOneDefinition_ItsTwoPlacesReferTo()
    {
        var plan = JsonNode.Parse(WorkflowTemplates.TextOf("schemas/plan.schema.json")!)!;
        var reference = new JsonObject { ["$ref"] = "#/$defs/workItem" };

        Assert.True(JsonNode.DeepEquals(reference, plan["properties"]!["items"]!["items"]));
        Assert.True(JsonNode.DeepEquals(new JsonArray(reference.DeepClone(), new JsonObject { ["type"] = "null" }), plan["properties"]!["scope"]!["items"]!["properties"]!["item"]!["anyOf"]));
        Assert.Equal("object", plan["$defs"]!["workItem"]!["type"]!.GetValue<string>());
    }

    /// <summary>The envelope said in words, for a client whose wire holds no schema, holds the schema whole.</summary>
    [Fact]
    public void TheEnvelopeInWords_HoldsTheSchemaWhole()
    {
        var schema = AgentEnvelope.Schema(new AgentOutput("o", Step));

        Assert.Equal(
            $"When your work is done, answer with one JSON object and nothing else - no text around it, no code fence - that reads against this JSON schema:\n{schema}",
            AgentEnvelope.InWords(schema));
    }

    /// <summary>A schema with the bounds and the descriptions taken off, and the schemas it holds left out: what the copy sent keeps of it.</summary>
    private static JsonObject Bare(JsonObject schema)
    {
        var bare = new JsonObject();

        foreach (var (name, value) in schema)
        {
            if (name is not ("minItems" or "maxItems" or "description" or "properties" or "items" or "anyOf" or "$defs"))
            {
                bare[name] = value?.DeepClone();
            }
        }

        return bare;
    }

    private static StructuredOutputGate Gate(int? rounds = null) => new(new AgentOutput("o", Step, AskRounds: rounds), new Masking("sk-1"));

    private async Task<AgentRunResult?> Checked(StructuredOutputGate gate, JsonNode? answer, string text)
        => await gate.CheckedAsync(answer, text, new Listening(_events), repair =>
        {
            _sentBack.Add(repair);
            return Task.CompletedTask;
        });

    private sealed class Listening(List<AgentEvent> events) : IAgentEventSink
    {
        public ValueTask EmitAsync(AgentEvent @event)
        {
            events.Add(@event);
            return ValueTask.CompletedTask;
        }
    }
}
