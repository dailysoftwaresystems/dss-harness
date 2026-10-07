using RepoHarness.Core.Execution;

namespace RepoHarness.Core.Mutations;

/// <summary>An arm no worker drove, and the verdict that says why.</summary>
/// <param name="Arm">The arm.</param>
/// <param name="Verdict">
/// <c>stopped</c>, saying why no worker drove it; <c>poisoned</c> where nothing explains it, a defect in the harness.
/// </param>
public sealed record UndrivenArm(MutationArm Arm, ReachedVerdict Verdict);

/// <summary>
/// The arms of one leg's sweep, dealt to its workers one at a time in the registry's order: a worker takes the next arm
/// once it has finished the one before, so a slow arm holds one worker and never the sweep.
/// </summary>
/// <remarks>
/// <para>
/// A worker that can no longer drive arms - its copy could not be made, or a site in it could not be put back as it was -
/// is retired: it takes no arm again, and the arm it held, where that arm reached no verdict of its own, goes back to the
/// front of the queue for the next worker free. A worker finding the queue empty while another still holds an arm waits
/// for that arm to finish, since it may come back.
/// </para>
/// <para>
/// What no worker drove is <c>stopped</c>, never passed over: every worker was retired, each saying why, or the sweep was
/// stopped before a worker reached it.
/// </para>
/// <para>Workers are numbered from 1, as their copies are.</para>
/// </remarks>
public sealed class ArmQueue
{
    private readonly Lock _gate = new();
    private readonly LinkedList<MutationArm> _pending;
    private readonly Dictionary<int, MutationArm> _held = [];
    private readonly SortedDictionary<int, string> _retired = [];
    private readonly int _workers;
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>A queue dealing <paramref name="arms"/>, in their order, to workers 1 to <paramref name="workers"/>.</summary>
    /// <param name="arms">The arms the sweep drives on the leg.</param>
    /// <param name="workers">How many workers drain the queue.</param>
    public ArmQueue(IEnumerable<MutationArm> arms, int workers)
    {
        ArgumentNullException.ThrowIfNull(arms);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(workers);

        _pending = new LinkedList<MutationArm>(arms);
        _workers = workers;
    }

    /// <summary>
    /// The next arm for <paramref name="worker"/> to drive, or <see langword="null"/> where there is none for it: it was
    /// retired, every arm was driven, or <paramref name="cancellationToken"/> stopped the sweep. While the queue is empty
    /// and another worker still holds an arm, it waits for that arm.
    /// </summary>
    /// <param name="worker">The worker asking, which holds no arm.</param>
    /// <param name="cancellationToken">Stops the sweep: no arm is dealt once it is cancelled.</param>
    public async Task<MutationArm?> TakeAsync(int worker, CancellationToken cancellationToken)
    {
        while (true)
        {
            Task changed;

            lock (_gate)
            {
                Check(worker);

                if (_held.TryGetValue(worker, out var holding))
                {
                    throw new InvalidOperationException($"Worker {worker} asked for an arm while it still holds '{holding.Id}'.");
                }

                if (cancellationToken.IsCancellationRequested || _retired.ContainsKey(worker))
                {
                    return null;
                }

                if (_pending.First is { } next)
                {
                    _pending.RemoveFirst();
                    _held[worker] = next.Value;

                    return next.Value;
                }

                if (_held.Count == 0)
                {
                    return null;
                }

                changed = _changed.Task;
            }

            try
            {
                await changed.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return null;
            }
        }
    }

    /// <summary><paramref name="worker"/> finished the arm it held: the arm reached its verdict.</summary>
    /// <param name="worker">The worker, which holds an arm.</param>
    public void Done(int worker)
    {
        lock (_gate)
        {
            Check(worker);

            if (!_held.Remove(worker))
            {
                throw new InvalidOperationException($"Worker {worker} finished an arm while it holds none.");
            }

            Changed();
        }
    }

    /// <summary>
    /// Retires <paramref name="worker"/>: it takes no arm again. The arm it holds, if any, goes back to the front of the
    /// queue where <paramref name="requeue"/> says it reached no verdict of its own, and is finished otherwise.
    /// </summary>
    /// <param name="worker">The worker.</param>
    /// <param name="why">Why it can drive no more arms, as a line says it.</param>
    /// <param name="requeue">Whether the arm it holds is dealt again, to another worker.</param>
    public void Retire(int worker, string why, bool requeue)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(why);

        lock (_gate)
        {
            Check(worker);

            if (!_retired.TryAdd(worker, why))
            {
                throw new InvalidOperationException($"Worker {worker} was retired already: {_retired[worker]}.");
            }

            if (_held.Remove(worker, out var held) && requeue)
            {
                _pending.AddFirst(held);
            }

            Changed();
        }
    }

    /// <summary>
    /// The arms no worker drove, each <c>stopped</c> saying why: every worker was retired, or
    /// <paramref name="cancellationToken"/> stopped the sweep first. Asked once every worker has stopped asking.
    /// </summary>
    /// <param name="cancellationToken">What stopped the sweep, if anything did.</param>
    public IReadOnlyList<UndrivenArm> Undriven(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_held.Count > 0)
            {
                throw new InvalidOperationException(
                    $"The undriven arms were asked for while {string.Join(", ", _held.Select(pair => $"worker {pair.Key} holds '{pair.Value.Id}'"))}.");
            }

            var verdict = _retired.Count == _workers
                ? ReachedVerdict.Of(
                    LegVerdict.Stopped,
                    "no worker was left to drive it: " + string.Join("; ", _retired.Select(pair => $"worker {pair.Key} was retired, {pair.Value}")))
                : cancellationToken.IsCancellationRequested
                    ? ReachedVerdict.Of(LegVerdict.Stopped, "the sweep was stopped before a worker drove it")
                    : ReachedVerdict.Of(LegVerdict.Poisoned, "no worker drove it, and nothing stopped the sweep; this is a defect in the harness");

            return [.. _pending.Select(arm => new UndrivenArm(arm, verdict))];
        }
    }

    /// <summary>Refuses a worker this queue was not made for.</summary>
    private void Check(int worker)
    {
        if (worker < 1 || worker > _workers)
        {
            throw new ArgumentOutOfRangeException(nameof(worker), worker, $"This queue's workers are numbered 1 to {_workers}.");
        }
    }

    /// <summary>Wakes every worker waiting for an arm another held, to look at the queue again.</summary>
    private void Changed()
    {
        var changed = _changed;

        _changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        changed.SetResult();
    }
}
