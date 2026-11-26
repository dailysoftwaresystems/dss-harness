namespace DssHarness.Core.Processes;

/// <summary>
/// A run its caller stopped once its child had started, as the process runner throws it when the child has gone: an
/// <see cref="OperationCanceledException"/>, so every catch of one holds, that carries what the stop could not reach or
/// end - the child's container, a process of its tree - (<see cref="Uncontained"/>), as
/// <see cref="ProcessResult.Uncontained"/> carries it at a run's own end, since a caller that gave up is handed no result
/// to read it from - said once, as that is, by the caller that owns the operation's output (<see cref="UnreachedSaying"/>).
/// </summary>
public sealed class ProcessStoppedException : OperationCanceledException
{
    /// <summary>A stop that reached and ended all it was to: said as any cancellation is.</summary>
    /// <param name="token">The caller's token that stopped the run.</param>
    public ProcessStoppedException(CancellationToken token)
        : base(token)
    {
    }

    /// <summary>A stop that could not reach or end all it was to, said in its message as well.</summary>
    /// <param name="uncontained">What it could not reach or end, and why.</param>
    /// <param name="token">The caller's token that stopped the run.</param>
    public ProcessStoppedException(string uncontained, CancellationToken token)
        : base($"The run was stopped, and {uncontained}.", token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uncontained);

        Uncontained = uncontained;
    }

    /// <summary>
    /// What the stop could not reach or end, and why (<see cref="ProcessResult.Uncontained"/>); <see langword="null"/>
    /// where it reached and ended all it was to.
    /// </summary>
    public string? Uncontained { get; }
}
