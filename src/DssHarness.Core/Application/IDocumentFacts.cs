using System.Text.Json.Nodes;

namespace DssHarness.Core.Application;

/// <summary>
/// What an operation did that every document it answers with says, however it ends: written into the document by its
/// session (<see cref="CommandSession"/>) - the body's own document, and the stopped form of it where the body answered
/// without one, stopped, or threw - never by the command, so no command can answer a document without it, one added
/// later included.
/// </summary>
/// <remarks>
/// Registered once per operation, as everything of an operation is: what it holds is that operation's alone. Where it
/// holds nothing, every document is left exactly as its command wrote it.
/// </remarks>
public interface IDocumentFacts
{
    /// <summary>Whether this operation did anything a document is to say.</summary>
    bool HasAny { get; }

    /// <summary>Writes what this operation did into <paramref name="document"/>'s root, as its own members.</summary>
    /// <param name="document">The root of a document the operation answers with.</param>
    void WriteInto(JsonObject document);
}
