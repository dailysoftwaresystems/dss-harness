namespace DssHarness.Core.Processes;

/// <summary>
/// A run its caller stopped once its child had started, as the process runner throws it when the child has gone: an
/// <see cref="OperationCanceledException"/>, so every catch of one holds, that carries what the child's container could
/// not reach when it was ended (<see cref="Uncontained"/>) - as <see cref="ProcessResult.Uncontained"/> carries it at a
/// run's own end, since a caller that gave up is handed no result to read it from.
/// </summary>
public sealed class ProcessStoppedException : OperationCanceledException
{
    /// <summary>A stop whose container reached all it holds, or of a child that had none: said as any cancellation is.</summary>
    /// <param name="token">The caller's token that stopped the run.</param>
    public ProcessStoppedException(CancellationToken token)
        : base(token)
    {
    }

    /// <summary>A stop whose container could not reach all it holds, said in its message as well.</summary>
    /// <param name="uncontained">What the container could not reach, and why.</param>
    /// <param name="token">The caller's token that stopped the run.</param>
    public ProcessStoppedException(string uncontained, CancellationToken token)
        : base($"The run was stopped, and {uncontained}.", token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uncontained);

        Uncontained = uncontained;
    }

    /// <summary>
    /// What the child's container could not reach when it was ended, and why; <see langword="null"/> where it reached all
    /// it holds, or the child had none.
    /// </summary>
    public string? Uncontained { get; }
}
