using System.Text.Json.Serialization;

namespace DssHarness.Core.Agents.Runtime;

/// <summary>
/// One line of a step's <c>graph.jsonl</c>, in its directory beside its stored stream: what the step's codebase graph was
/// asked and gave, a line for each, appended whole as it happens. A call of the <c>graph</c> tool and an escape of its
/// narrowing of reads are lines; a step that has a graph opens the file with the line of its prompt, the pack it was
/// given or why none.
/// </summary>
/// <remarks>
/// Read back by the options that refuse a member the type does not name and one it needs that the line leaves out
/// (<see cref="FileSystem.JsonStateFile.LineOptions"/>), so a line is exactly what was written or is refused.
/// </remarks>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(GraphCallLine), "call")]
[JsonDerivedType(typeof(GraphPromptLine), "prompt")]
[JsonDerivedType(typeof(GraphEscapeLine), "escape")]
[JsonDerivedType(typeof(GraphDeniedLine), "deniedRead")]
public abstract record GraphStepLine
{
    /// <summary>Why the line does not read as one, or null where it does.</summary>
    public abstract string? Problem();
}

/// <summary>
/// The step's prompt, the first line of its <c>graph.jsonl</c>: what the map and the pack it was given cost - the map's
/// tokens, the pack's seeds, nodes and tokens, and what was left out - or why no pack was given, where the run's graph
/// was never built.
/// </summary>
/// <param name="MapTokens">The map's estimated tokens; 0 where none was given - the map is the run's, made once (part 08).</param>
/// <param name="Seeds">How many nodes the pack was seeded from.</param>
/// <param name="Nodes">How many nodes the pack lists.</param>
/// <param name="Tokens">The pack's estimated tokens.</param>
/// <param name="LeftOut">How many of the pack's lines did not fit its cap.</param>
/// <param name="Why">Why no pack was given, where none was - the sentence for a step that left no output; null where one was.</param>
public sealed record GraphPromptLine(int MapTokens, int Seeds, int Nodes, int Tokens, int LeftOut, string? Why = null) : GraphStepLine
{
    /// <inheritdoc/>
    public override string? Problem()
        => MapTokens < 0 || Seeds < 0 || Nodes < 0 || Tokens < 0 || LeftOut < 0 ? "a count is never below 0"
            : Why is null && Nodes == 0 && Tokens == 0 && Seeds == 0 && MapTokens == 0 ? "a prompt line names a pack or why none was given"
            : Why is not null && (Seeds > 0 || Nodes > 0 || Tokens > 0 || LeftOut > 0 || MapTokens > 0) ? "a prompt line that says why no pack was given names no pack"
            : null;
}

/// <summary>
/// One whole read let past the narrowing (<c>graphReads: narrow</c>): the file, its lines and tokens, and the escape that
/// let it - <c>lowConfidence</c>, the graph not sure of the file, with why - or <c>stated</c>, the agent's reason with
/// <c>wholeFile</c>.
/// </summary>
/// <param name="Path">The file, relative to the tree with forward separators.</param>
/// <param name="Lines">How many lines the file holds.</param>
/// <param name="Tokens">The file's estimated tokens.</param>
/// <param name="Escape">The escape: <see cref="LowConfidence"/> or <see cref="Stated"/>.</param>
/// <param name="Why">Why it let the read: the graph's own words where it is not sure, the agent's reason where it stated one.</param>
public sealed record GraphEscapeLine(string Path, int Lines, int Tokens, string Escape, string Why) : GraphStepLine
{
    /// <summary>The escape of a file the graph is not sure of: allowed without a word.</summary>
    public const string LowConfidence = "lowConfidence";

    /// <summary>The escape of a reason the agent gave with <c>graph</c>'s <c>wholeFile</c>.</summary>
    public const string Stated = "stated";

    /// <inheritdoc/>
    public override string? Problem()
        => string.IsNullOrWhiteSpace(Path) ? "an escape names the file it let by"
            : Lines < 1 || Tokens < 0 ? "an escape's file holds lines, and its tokens are never below 0"
            : Escape is not (LowConfidence or Stated) ? $"'{Escape}' is not an escape: {LowConfidence} or {Stated}"
            : string.IsNullOrWhiteSpace(Why) ? "an escape says why the read was let by"
            : null;
}

/// <summary>One whole read the narrowing (<c>graphReads: narrow</c>) refused: the file, its lines and its tokens, denied with the way to the graph.</summary>
/// <param name="Path">The file, relative to the tree with forward separators.</param>
/// <param name="Lines">How many lines the file holds.</param>
/// <param name="Tokens">The file's estimated tokens.</param>
public sealed record GraphDeniedLine(string Path, int Lines, int Tokens) : GraphStepLine
{
    /// <inheritdoc/>
    public override string? Problem()
        => string.IsNullOrWhiteSpace(Path) ? "a denied read names the file it refused"
            : Lines < 1 || Tokens < 0 ? "a denied read's file holds lines, and its tokens are never below 0"
            : null;
}

/// <summary>One call of the <c>graph</c> tool: its operation, how it ended, the nodes it asked about, served and edited, and what the answer cost.</summary>
/// <param name="Op">The operation it asked, as the call named it; empty where a misused call named none.</param>
/// <param name="Outcome">How it ended.</param>
/// <param name="Asked">The nodes it asked about by name - <c>node</c>, <c>related</c>, <c>body</c> - by id.</param>
/// <param name="Served">The nodes a line of which it gave, by id.</param>
/// <param name="Edited">The node it edited, by the id it had; none where it wrote none.</param>
/// <param name="Wrote">The lines it wrote, where it wrote; null where it wrote none.</param>
/// <param name="Tokens">The answer's estimated tokens - its bytes over four, the one bound every answer is held to.</param>
/// <param name="Hits">How many text hits the answer added where the graph was not sure.</param>
public sealed record GraphCallLine(string Op, GraphToolOutcome Outcome, IReadOnlyList<string> Asked, IReadOnlyList<string> Served, IReadOnlyList<string> Edited, GraphWrittenLines? Wrote = null, int Tokens = 0, int Hits = 0) : GraphStepLine
{
    /// <summary>The line of the call <paramref name="op"/> named, answered by <paramref name="answer"/>.</summary>
    /// <param name="op">The operation the call named; null where it named none.</param>
    /// <param name="answer">What the tool answered.</param>
    public static GraphCallLine Of(string? op, GraphToolAnswer answer)
    {
        ArgumentNullException.ThrowIfNull(answer);

        return new GraphCallLine(op ?? string.Empty, answer.Outcome, answer.Facts.Asked, answer.Facts.Served, answer.Facts.Edited, answer.Facts.Wrote, Codebase.EstimatedTokens.Of(answer.Text), answer.Facts.Hits);
    }

    /// <inheritdoc/>
    public override string? Problem()
        => string.IsNullOrWhiteSpace(Op) && Outcome != GraphToolOutcome.Usage ? "a call names its operation, unless it was refused as misused"
            : !Enum.IsDefined(Outcome) ? $"'{Outcome}' is no way a call ends"
            : Asked.Concat(Served).Concat(Edited).Any(string.IsNullOrWhiteSpace) ? "a node is named by its id"
            : Tokens < 0 || Hits < 0 ? "a call's tokens and hits are never below 0"
            : (Outcome == GraphToolOutcome.Wrote) != (Edited.Count == 1) || Edited.Count > 1 || (Outcome == GraphToolOutcome.Wrote) != (Wrote is not null)
                ? "a call that wrote edited one node and names the lines it wrote, and one that wrote nothing neither"
            : Wrote?.Problem();
}
