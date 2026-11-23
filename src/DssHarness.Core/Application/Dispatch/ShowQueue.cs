using DssHarness.Core.Execution;

namespace DssHarness.Core.Application.Dispatch;

/// <summary>
/// Shows this machine's dispatch queue: the use case the <c>queue</c> command answers - each class of unit, its
/// capacity, its holders and its waiting units, by tenant - read from the queue's own record, changing nothing.
/// </summary>
public interface IShowQueue : IUseCase
{
    /// <summary>The queue as it stands: each class, its capacity, who holds and who waits.</summary>
    /// <returns>The queue's classes, in the order the queue keeps them.</returns>
    /// <exception cref="Results.HarnessException">The queue's record or this machine's settings file could not be read.</exception>
    IReadOnlyList<DispatchClass> Show();
}

/// <summary>
/// Shows this machine's dispatch queue as its one owner reads it (<see cref="MachineDispatchQueue.Queue"/>): a front end
/// asks the use case, never the owner that admits units by the same record.
/// </summary>
/// <param name="queue">The machine's dispatch queue.</param>
public sealed class ShowQueue(MachineDispatchQueue queue) : IShowQueue
{
    private readonly MachineDispatchQueue _queue = queue;

    /// <inheritdoc/>
    public IReadOnlyList<DispatchClass> Show() => _queue.Queue();
}
