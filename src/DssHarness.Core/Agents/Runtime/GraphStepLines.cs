using System.Text.Json.Serialization;

namespace DssHarness.Core.Agents.Runtime;

/// <summary>
/// One line of a step's <c>graph.jsonl</c>, in its directory beside its stored stream: what the step's codebase graph was
/// asked and gave, a line for each, appended whole as it happens. A call of the <c>graph</c> tool is the line there is
/// today; slice 27d adds the prompt's line and each escape's when it gives a step its graph.
/// </summary>
/// <remarks>
/// Read back by the options that refuse a member the type does not name and one it needs that the line leaves out
/// (<see cref="FileSystem.JsonStateFile.LineOptions"/>), so a line is exactly what was written or is refused.
/// </remarks>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(GraphCallLine), "call")]
public abstract record GraphStepLine
{
    /// <summary>Why the line does not read as one, or null where it does.</summary>
    public abstract string? Problem();
}

/// <summary>One call of the <c>graph</c> tool: its operation, how it ended, and the nodes it asked about, served and edited.</summary>
/// <param name="Op">The operation it asked, as the call named it; empty where a misused call named none.</param>
/// <param name="Outcome">How it ended.</param>
/// <param name="Asked">The nodes it asked about by name - <c>node</c>, <c>related</c>, <c>body</c> - by id.</param>
/// <param name="Served">The nodes a line of which it gave, by id.</param>
/// <param name="Edited">The node it edited, by the id it had; none where it wrote none.</param>
/// <param name="Wrote">The lines it wrote, where it wrote; null where it wrote none.</param>
public sealed record GraphCallLine(string Op, GraphToolOutcome Outcome, IReadOnlyList<string> Asked, IReadOnlyList<string> Served, IReadOnlyList<string> Edited, GraphWrittenLines? Wrote = null) : GraphStepLine
{
    /// <summary>The line of the call <paramref name="op"/> named, answered by <paramref name="answer"/>.</summary>
    /// <param name="op">The operation the call named; null where it named none.</param>
    /// <param name="answer">What the tool answered.</param>
    public static GraphCallLine Of(string? op, GraphToolAnswer answer)
    {
        ArgumentNullException.ThrowIfNull(answer);

        return new GraphCallLine(op ?? string.Empty, answer.Outcome, answer.Facts.Asked, answer.Facts.Served, answer.Facts.Edited, answer.Facts.Wrote);
    }

    /// <inheritdoc/>
    public override string? Problem()
        => string.IsNullOrWhiteSpace(Op) && Outcome != GraphToolOutcome.Usage ? "a call names its operation, unless it was refused as misused"
            : !Enum.IsDefined(Outcome) ? $"'{Outcome}' is no way a call ends"
            : Asked.Concat(Served).Concat(Edited).Any(string.IsNullOrWhiteSpace) ? "a node is named by its id"
            : (Outcome == GraphToolOutcome.Wrote) != (Edited.Count == 1) || Edited.Count > 1 || (Outcome == GraphToolOutcome.Wrote) != (Wrote is not null)
                ? "a call that wrote edited one node and names the lines it wrote, and one that wrote nothing neither"
            : Wrote?.Problem();
}
