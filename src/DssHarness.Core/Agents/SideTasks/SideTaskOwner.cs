using DssHarness.Core.Execution;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Orchestration.Flow;
using DssHarness.Core.Platform;
using DssHarness.Core.Results;

namespace DssHarness.Core.Agents.SideTasks;

/// <summary>
/// A side-task's live owner, <c>.side-tasks/&lt;task&gt;.owner.json</c>: the process whose chat holds the side-task open.
/// A live owner refuses another <c>run-side-task --resume</c> (13); a dead one is reclaimed and said. The claim is taken
/// under the machine-wide file lock, for no section of the repository guards a side-task as the repository's one section
/// guards a run.
/// </summary>
/// <param name="fileSystem">Writes the owner file.</param>
/// <param name="identity">This process, and how another's liveness is told.</param>
/// <param name="clock">When the chat took the side-task.</param>
public sealed class SideTaskOwner(IFileSystem fileSystem, IProcessIdentity identity, TimeProvider? clock = null)
{
    private readonly IFileSystem _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
    private readonly IProcessIdentity _identity = identity ?? throw new ArgumentNullException(nameof(identity));
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>
    /// Claims the side-task for this process: refused (13) where a live owner holds it - a chat open in another process -
    /// and a dead one is reclaimed and said.
    /// </summary>
    /// <param name="layout">The side-task's layout.</param>
    /// <returns>What is said of a dead owner's record reclaimed; null where none was recorded.</returns>
    /// <exception cref="HarnessException">A live owner holds the side-task (<see cref="HarnessExit.Refused"/>).</exception>
    public string? Claim(SideTaskLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);

        _fileSystem.CreateDirectory(layout.Root);

        return MachineWideFile.Update(layout.OwnerFile, MachineWideFile.Window, () =>
        {
            var held = Read(layout);

            if (held is not null && _identity.Stands(held.Machine, held.ProcessId, held.Stamp))
            {
                throw new HarnessException(
                    HarnessExit.Refused,
                    $"side-task '{layout.Name}' is open in another process ({Named(held)}); end its chat there, or wait for it to leave. Nothing was changed.");
            }

            JsonStateFile.Write(_fileSystem, layout.OwnerFile, new RunOwner(_identity.CurrentMachine, _identity.CurrentId, _clock.GetUtcNow(), _identity.Current));

            return held is null
                ? null
                : $"the owner recorded for side-task '{layout.Name}' ({Named(held)}) has gone; it was reclaimed";
        });
    }

    /// <summary>
    /// Releases the side-task where this process holds it: its owner removed, so the next chat claims it afresh. An owner
    /// that names another process - one that reclaimed the side-task after this one was taken for gone - is left as it is.
    /// </summary>
    /// <param name="layout">The side-task's layout.</param>
    public void Release(SideTaskLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);

        if (Read(layout) is { } held && _identity.IsHere(held.Machine) && held.ProcessId == _identity.CurrentId)
        {
            MachineWideFile.Update<bool>(layout.OwnerFile, MachineWideFile.Window, () =>
            {
                if (_fileSystem.KindOf(layout.OwnerFile) == PathKind.File)
                {
                    _fileSystem.DeleteFile(layout.OwnerFile);
                }

                return true;
            });
        }
    }

    /// <summary>Whether a live owner holds the side-task: an open chat, this process's or another's.</summary>
    /// <param name="layout">The side-task's layout.</param>
    public bool Open(SideTaskLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);

        return Read(layout) is { } held && _identity.Stands(held.Machine, held.ProcessId, held.Stamp);
    }

    private RunOwner? Read(SideTaskLayout layout)
        => _fileSystem.KindOf(layout.OwnerFile) == PathKind.File
            ? JsonStateFile.Read<RunOwner>(_fileSystem, layout.OwnerFile)
            : null;

    private string Named(RunOwner held)
        => $"process {held.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)}{_identity.Elsewhere(held.Machine)}";
}
