namespace DssHarness.Core.Output;

/// <summary>
/// One operation of this tool, by id: a command today, and later a step of an orchestrator run, which names the run it
/// belongs to as its parent.
/// </summary>
/// <param name="Value">The id, unique within whatever issued it.</param>
public readonly record struct OperationId(Guid Value)
{
    /// <summary>A new id, for an operation that is beginning.</summary>
    public static OperationId New() => new(Guid.NewGuid());

    /// <inheritdoc/>
    public override string ToString() => Value.ToString("N");
}
