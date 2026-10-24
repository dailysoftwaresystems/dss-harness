using System.Text.Json.Nodes;
using DssHarness.Core.Schemas;

namespace DssHarness.Core.Agents;

/// <summary>
/// The one owner of an answer held to its schema, on every client: whether it reads, and the message that sends it back
/// with what did not. A client's own enforcement comes first where it has one; this says only what the harness's
/// validator still refuses after it.
/// </summary>
public static class StructuredOutputGate
{
    /// <summary>Why <paramref name="answer"/> does not read against the output's schema; empty where it does.</summary>
    /// <param name="output">The output the run is to produce.</param>
    /// <param name="answer">The answer, as the client received it; null where it gave none.</param>
    public static IReadOnlyList<string> Problems(AgentOutput output, JsonNode? answer)
    {
        ArgumentNullException.ThrowIfNull(output);

        return answer is null
            ? ["$: the answer holds no output held to the schema; answer with one"]
            : JsonSchemaSubset.Problems(JsonNode.Parse(output.Schema) ?? throw new ArgumentException("The output's schema is JSON null.", nameof(output)), answer);
    }

    /// <summary>The message that sends an answer back, naming each problem by its path in the answer.</summary>
    /// <param name="problems">What does not read, each one a path and what is wrong there.</param>
    public static string Repair(IReadOnlyList<string> problems)
    {
        ArgumentNullException.ThrowIfNull(problems);

        return "Your answer does not read against the schema it was asked for:\n"
            + string.Concat(problems.Select(problem => $"- {problem}\n"))
            + "Answer again, whole, as the schema says.";
    }
}
