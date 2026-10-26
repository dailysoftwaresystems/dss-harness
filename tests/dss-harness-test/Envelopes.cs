using System.Text.Json.Nodes;
using DssHarness.Core.Agents;

namespace DssHarness.Tests;

/// <summary>
/// Answers in the envelope every client is asked for where a step names an output (<see cref="AgentEnvelope"/>): the
/// notes, the output, the assumptions and, where given, the questions - so a test writes what an agent answers as it
/// answers it.
/// </summary>
internal static class Envelopes
{
    /// <summary>The notes an answer leaves unless a test says other ones.</summary>
    public const string Notes = "Read what the step asked for, and answered it.";

    /// <summary>An envelope holding <paramref name="output"/>, with notes and what was assumed.</summary>
    /// <param name="output">The output; null for an answer that has none.</param>
    /// <param name="notes">The notes.</param>
    /// <param name="assumptions">What was assumed; none unless given.</param>
    /// <param name="questions">What is asked; left out unless given.</param>
    public static JsonObject Of(JsonNode? output, string notes = Notes, JsonArray? assumptions = null, JsonArray? questions = null)
    {
        var envelope = new JsonObject
        {
            [AgentEnvelope.Notes] = notes,
            [AgentEnvelope.Output] = output?.DeepClone(),
            [AgentEnvelope.Assumptions] = assumptions ?? [],
        };

        if (questions is not null)
        {
            envelope[AgentEnvelope.Questions] = questions;
        }

        return envelope;
    }

    /// <summary>An envelope holding the output <paramref name="output"/> is, as JSON text.</summary>
    /// <param name="output">The output, as JSON text.</param>
    public static string Text(string output) => Of(JsonNode.Parse(output)).ToJsonString();

    /// <summary>One assumption, as the envelope holds it.</summary>
    public static JsonObject Assumption(string about = "the file's encoding", string assumed = "UTF-8", string why = "every other file of the tree is")
        => new() { ["about"] = about, ["assumed"] = assumed, ["why"] = why };

    /// <summary>One question, as the envelope holds it: two options, the first recommended unless said otherwise.</summary>
    public static JsonObject Question(string question = "Which cache is meant?", string? recommended = null, params string[] options)
    {
        string[] offered = options.Length > 0 ? options : ["use the cache", "build again"];

        return new()
        {
            ["question"] = question,
            ["problem"] = "Two caches hold the build, and the task names neither.",
            ["tried"] = "Read the task and the build's scripts; neither says.",
            ["means"] = "The step cannot say which build it measured.",
            ["options"] = new JsonArray([.. offered.Select(option => (JsonNode)option)]),
            ["recommended"] = recommended ?? offered[0],
            ["why"] = "It is the one the last run used.",
        };
    }
}
