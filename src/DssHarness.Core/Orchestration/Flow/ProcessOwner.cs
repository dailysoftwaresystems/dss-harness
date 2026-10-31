using System.Globalization;
using DssHarness.Core.Execution;
using DssHarness.Core.Results;

namespace DssHarness.Core.Orchestration.Flow;

/// <summary>
/// The live owner of one thing a process holds open - a run's engine, a side-task's chat - recorded in one owner file:
/// who holds it, whether that process still does, the claim that refuses a live one and reclaims a dead one, and the
/// release that removes only this process's own. The one rule of a live owner, parameterised by the file and the words
/// the thing is named by; the owner is read strictly (<see cref="OrchestrationStore.ReadOwner(string, string)"/>), so a
/// file that cannot be read, holds null or does not hold together is refused, never read as no owner. The caller holds
/// whatever keeps a second claim of the same thing out while one is made.
/// </summary>
/// <param name="store">Reads and writes the owner file, strictly.</param>
/// <param name="identity">This process, and how another's liveness is told.</param>
/// <param name="clock">When the owner took the thing.</param>
public sealed class ProcessOwner(OrchestrationStore store, IProcessIdentity identity, TimeProvider? clock = null)
{
    private readonly OrchestrationStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly IProcessIdentity _identity = identity ?? throw new ArgumentNullException(nameof(identity));
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>The owner <paramref name="ownerFile"/> records, where one is live; null where none is recorded or the recorded one has gone.</summary>
    /// <param name="ownerFile">The owner file.</param>
    /// <param name="what">What the file is, as a refusal names it.</param>
    public RunOwner? Live(string ownerFile, string what)
        => _store.ReadOwner(ownerFile, what) is { } held && Stands(held) ? held : null;

    /// <summary>Whether the process <paramref name="held"/> names still holds what it owns.</summary>
    /// <param name="held">The owner recorded.</param>
    public bool Stands(RunOwner held)
    {
        ArgumentNullException.ThrowIfNull(held);
        return _identity.Stands(held.Machine, held.ProcessId, held.Stamp);
    }

    /// <summary>Whether the process <paramref name="held"/> names runs on this machine.</summary>
    /// <param name="held">The owner recorded.</param>
    public bool IsHere(RunOwner held)
    {
        ArgumentNullException.ThrowIfNull(held);
        return _identity.IsHere(held.Machine);
    }

    /// <summary>The process <paramref name="held"/> names, as a line names it: its id, and that it runs on another machine where it does - never that machine's name.</summary>
    /// <param name="held">The owner recorded.</param>
    public string Named(RunOwner held)
    {
        ArgumentNullException.ThrowIfNull(held);
        return $"process {held.ProcessId.ToString(CultureInfo.InvariantCulture)}{_identity.Elsewhere(held.Machine)}";
    }

    /// <summary>
    /// Claims what <paramref name="ownerFile"/> records the owner of for this process: refused (13) where a live owner
    /// holds it, in the words <paramref name="refusal"/> gives from the owner's name; a dead one is reclaimed and said.
    /// </summary>
    /// <param name="ownerFile">The owner file.</param>
    /// <param name="what">What the file is, as a refusal names it.</param>
    /// <param name="thing">What is owned, as a line names it: <c>run 'r1'</c>, <c>side-task 'fix-it'</c>.</param>
    /// <param name="refusal">The refusal of a claim a live owner holds, from that owner's name, whole.</param>
    /// <returns>What is said of a dead owner's record reclaimed; null where none was recorded.</returns>
    /// <exception cref="HarnessException">A live owner holds it (<see cref="HarnessExit.Refused"/>), or the owner does not read as one.</exception>
    public string? Claim(string ownerFile, string what, string thing, Func<string, string> refusal)
    {
        ArgumentNullException.ThrowIfNull(thing);
        ArgumentNullException.ThrowIfNull(refusal);

        var held = _store.ReadOwner(ownerFile, what);

        if (held is not null && Stands(held))
        {
            throw new HarnessException(HarnessExit.Refused, refusal(Named(held)));
        }

        _store.WriteOwner(ownerFile, what, new RunOwner(_identity.CurrentMachine, _identity.CurrentId, _clock.GetUtcNow(), _identity.Current));

        return held is null
            ? null
            : $"the owner recorded for {thing} ({Named(held)}) has gone; it was reclaimed";
    }

    /// <summary>
    /// Releases what <paramref name="ownerFile"/> records the owner of, where this process holds it: the owner removed, so
    /// the next claim takes it afresh. An owner that names another process - one that reclaimed it after this one was
    /// taken for gone - is left as it is.
    /// </summary>
    /// <param name="ownerFile">The owner file.</param>
    /// <param name="what">What the file is, as a refusal names it.</param>
    public void Release(string ownerFile, string what)
    {
        if (Holds(ownerFile, what))
        {
            _store.RemoveOwner(ownerFile);
        }
    }

    /// <summary>Whether this process is the owner <paramref name="ownerFile"/> records.</summary>
    /// <param name="ownerFile">The owner file.</param>
    /// <param name="what">What the file is, as a refusal names it.</param>
    public bool Holds(string ownerFile, string what)
        => _store.ReadOwner(ownerFile, what) is { } held && IsHere(held) && held.ProcessId == _identity.CurrentId;

    /// <summary>Removes an owner record whose process has gone, or was ended.</summary>
    /// <param name="ownerFile">The owner file.</param>
    /// <param name="what">What the file is, as a refusal names it.</param>
    public void RemoveGone(string ownerFile, string what)
    {
        if (_store.ReadOwner(ownerFile, what) is { } held && !Stands(held))
        {
            _store.RemoveOwner(ownerFile);
        }
    }
}
