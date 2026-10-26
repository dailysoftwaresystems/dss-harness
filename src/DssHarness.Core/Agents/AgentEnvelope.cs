using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using DssHarness.Core.FileSystem;

namespace DssHarness.Core.Agents;

/// <summary>
/// The one owner of the envelope every client is asked for where a step names an output (part 03, "Structured output"):
/// one object of four members - the agent's notes for whoever goes on, the step's own output, what it assumed where a
/// person could have told it, and what it asks the person where the step may - its schema built round the step's own,
/// and said in words for a client whose wire holds no answer to a schema.
/// </summary>
/// <remarks>
/// The step's schema goes in whole as <see cref="Output"/>, with null beside it for the two answers that have no output -
/// a handoff, and an answer that asks - and its definitions at the envelope's root, where its references find them.
/// <see cref="Questions"/> is offered only where the step may ask and has rounds left. An assumption's members and a
/// question's are their records' (<see cref="AgentAssumption"/>, <see cref="AgentQuestion"/>), named as the records are
/// written wherever they are kept, so an answer reads as the record it becomes. What no schema can say - notes
/// that are not empty, a null output only where the answer asks, a recommendation among its options - is the gate's
/// (<see cref="StructuredOutputGate"/>).
/// </remarks>
public static class AgentEnvelope
{
    /// <summary>The member that holds what the agent leaves for whoever goes on: kept as the step's notes.</summary>
    public const string Notes = "notes";

    /// <summary>The member that holds the step's own output, held to its schema.</summary>
    public const string Output = "output";

    /// <summary>The member that holds what the agent assumed where a person could have told it.</summary>
    public const string Assumptions = "assumptions";

    /// <summary>The member that holds what the agent asks the person, offered only where the step may ask and has rounds left.</summary>
    public const string Questions = "questions";

    /// <summary>The fewest choices a question offers.</summary>
    public const int FewestOptions = 2;

    /// <summary>How the envelope is written: as JSON, escaping only what JSON itself must, so a model reads it as it was written.</summary>
    private static readonly JsonSerializerOptions Written = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>The schema of one assumption: what was open, what was taken, and why.</summary>
    public static JsonObject Assumption() => Closed(
        (Member(nameof(AgentAssumption.About)), "What was open: what a person could have told you."),
        (Member(nameof(AgentAssumption.Assumed)), "What you took it to be."),
        (Member(nameof(AgentAssumption.Why)), "Why you took that."));

    /// <summary>
    /// The schema of one question, in the shape every question holds (part 04): what is asked, what stands in the way,
    /// what was tried, what depends on it, the choices, the one recommended and why.
    /// </summary>
    public static JsonObject Question()
    {
        var question = Closed(
            (Member(nameof(AgentQuestion.Question)), "What you ask the person to decide, in one sentence."),
            (Member(nameof(AgentQuestion.Problem)), "What stands in the way: what happened or what is missing, in words a person who was not there can follow."),
            (Member(nameof(AgentQuestion.Tried)), "What you looked at or tried before asking, and why nothing you can do settles it."),
            (Member(nameof(AgentQuestion.Means)), "What depends on the answer: what cannot be done, or cannot be trusted, until it is given."),
            (Member(nameof(AgentQuestion.Options)), string.Empty),
            (Member(nameof(AgentQuestion.Recommended)), "The choice you recommend, word for word as it stands among the options."),
            (Member(nameof(AgentQuestion.Why)), "Why that one: what it costs and what it risks."));

        question["properties"]![Member(nameof(AgentQuestion.Options))] = new JsonObject
        {
            ["type"] = "array",
            ["minItems"] = FewestOptions,
            ["description"] = $"The choices the person has, at least {FewestOptions}, each a sentence that says what it leads to.",
            ["items"] = new JsonObject { ["type"] = "string" },
        };

        return question;
    }

    /// <summary>
    /// The envelope <paramref name="output"/>'s answer is asked in, as JSON: the step's schema as its output, null beside
    /// it, its definitions at the root; the questions offered where the step may ask and has rounds left.
    /// </summary>
    /// <param name="output">The output the run is to produce.</param>
    public static string Schema(AgentOutput output) => Envelope(output, whole: true).ToJsonString(Written);

    /// <summary>
    /// The envelope as the gate checks its members: every member as <see cref="Schema"/> says it but the output, which
    /// is checked against the step's own schema apart, so each of its problems names its own path.
    /// </summary>
    /// <param name="output">The output the run is to produce.</param>
    internal static JsonObject Members(AgentOutput output) => Envelope(output, whole: false);

    /// <summary>
    /// The name a member of an assumption or a question has in the envelope: its record's <paramref name="property"/>, as
    /// every record that keeps it is written (<see cref="JsonStateFile.Options"/>).
    /// </summary>
    /// <param name="property">The record's property.</param>
    public static string Member(string property) => JsonStateFile.Options.PropertyNamingPolicy!.ConvertName(property);

    /// <summary>Whether the envelope offers <see cref="Questions"/>: the step may ask, and has rounds left.</summary>
    /// <param name="output">The output the run is to produce.</param>
    public static bool Offers(AgentOutput output)
    {
        ArgumentNullException.ThrowIfNull(output);

        return output.AskRounds > 0;
    }

    /// <summary>
    /// The envelope said in words, for a client whose wire holds no answer to a schema: what the agent answers with once
    /// its work is done, and the schema whole.
    /// </summary>
    /// <param name="schema">The envelope, as <see cref="Schema"/> wrote it.</param>
    public static string InWords(string schema)
        => "When your work is done, answer with one JSON object and nothing else - no text around it, no code fence - that "
            + $"reads against this JSON schema:\n{schema}";

    private static JsonObject Envelope(AgentOutput output, bool whole)
    {
        ArgumentNullException.ThrowIfNull(output);

        var step = JsonNode.Parse(output.Schema) as JsonObject ?? throw new ArgumentException("The output's schema is no JSON object.", nameof(output));
        var definitions = step["$defs"];
        step.Remove("$defs");

        var offered = Offers(output);
        var properties = new JsonObject
        {
            [Notes] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "What you leave for whoever goes on with this work - you, later, or a session that has only this "
                    + "text and the tree: what was done and why, what was found out that would otherwise have to be found again, "
                    + "what was tried and did not work, what was not done and why, and what is left. Never empty.",
            },
            [Output] = new JsonObject
            {
                ["description"] = offered
                    ? "The step's answer, as its schema says; null only in a handoff, or where you ask the person under questions."
                    : "The step's answer, as its schema says; null only in a handoff.",
            },
            [Assumptions] = new JsonObject
            {
                ["type"] = "array",
                ["description"] = "What you assumed where a person could have told you"
                    + (offered ? string.Empty : " - and what you would have asked, had anyone been there to answer")
                    + ": each what was open, what you took, and why. Empty where you assumed nothing.",
                ["items"] = Assumption(),
            },
        };

        if (whole)
        {
            properties[Output]!["anyOf"] = new JsonArray(step, new JsonObject { ["type"] = "null" });
        }

        if (offered)
        {
            properties[Questions] = new JsonObject
            {
                ["type"] = "array",
                ["description"] = "What you ask the person, where only a person can settle it: asking, your output is null, and the "
                    + "work waits for the answer. Leave it out, or empty, where you ask nothing.",
                ["items"] = Question(),
            };
        }

        var envelope = new JsonObject
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["required"] = new JsonArray(Notes, Output, Assumptions),
            ["properties"] = properties,
        };

        if (whole && definitions is not null)
        {
            envelope["$defs"] = definitions.DeepClone();
        }

        return envelope;
    }

    /// <summary>A closed object of text members, each required and described.</summary>
    private static JsonObject Closed(params (string Name, string Description)[] members)
    {
        var properties = new JsonObject();

        foreach (var (name, description) in members)
        {
            properties[name] = new JsonObject { ["type"] = "string", ["description"] = description };
        }

        return new JsonObject
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["required"] = new JsonArray([.. members.Select(member => (JsonNode)member.Name)]),
            ["properties"] = properties,
        };
    }
}
