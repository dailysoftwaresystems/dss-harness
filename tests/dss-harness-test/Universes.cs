using System.Text.Json.Nodes;
using DssHarness.Core.Orchestration.Flow;

namespace DssHarness.Tests;

/// <summary>
/// A class's universe in the shape the shipped plan schema holds it (<c>plan.schema.json</c>'s <c>universe</c>), so a
/// test writes a planned class as a planner answers one: every axis the schema lists answered - those a test names
/// applying, with their values, and the rest said not to apply - each other axis a test names among the further ones, and
/// the cells the product of the counts of the values that apply.
/// </summary>
internal static class Universes
{
    /// <summary>Why an axis a test does not name does not apply, as the universe says it.</summary>
    public const string DoesNotApply = "the decision does not range over it";

    /// <summary>Where the values of an axis a test names are declared.</summary>
    public const string DeclaredIn = "closure.md";

    /// <summary>
    /// The axes the shipped plan schema's universe answers, each by its member, in the schema's order - read from the
    /// schema itself, so a test never holds a list of its own beside it.
    /// </summary>
    public static IReadOnlyList<string> Shipped { get; } =
    [
        .. ((JsonObject)Schema()["properties"]!)
            .Where(member => member.Value?["anyOf"] is not null)
            .Select(member => member.Key),
    ];

    /// <summary>The shipped plan schema's definition of a class's universe.</summary>
    public static JsonObject Schema()
        => (JsonObject)JsonNode.Parse(WorkflowTemplates.TextOf("schemas/plan.schema.json")!)!["$defs"]!["closureClass"]!["properties"]!["universe"]!;

    /// <summary>
    /// A universe whose axes <paramref name="applying"/> name apply, with their values - a shipped axis by its member,
    /// any other among the further axes - every other shipped axis said not to apply, and its cells the product.
    /// </summary>
    /// <param name="applying">The axes that apply, each with its values.</param>
    public static JsonObject Of(params (string Axis, string[] Values)[] applying)
    {
        var universe = new JsonObject();

        foreach (var axis in Shipped)
        {
            universe[axis] = applying.FirstOrDefault(given => string.Equals(given.Axis, axis, StringComparison.Ordinal)) is { Axis: not null } named
                ? Applies(named.Values)
                : NotApplying();
        }

        universe["further"] = new JsonArray(
        [
            .. applying.Where(given => !Shipped.Contains(given.Axis, StringComparer.Ordinal))
                .Select(given => (JsonNode)new JsonObject { ["name"] = given.Axis, ["answer"] = Applies(given.Values) }),
        ]);
        universe["cells"] = applying.Aggregate(1, (cells, given) => cells * given.Values.Length);

        return universe;
    }

    /// <summary>An axis's answer where it applies: its values, and where they are declared.</summary>
    /// <param name="values">Its values.</param>
    public static JsonObject Applies(params string[] values)
        => new() { ["values"] = new JsonArray([.. values.Select(value => (JsonNode)value)]), ["declaredIn"] = DeclaredIn };

    /// <summary>An axis's answer where it does not apply, and why.</summary>
    /// <param name="why">Why it does not.</param>
    public static JsonObject NotApplying(string why = DoesNotApply) => new() { ["why"] = why };
}
