using System.Globalization;
using DssHarness.Core.Execution;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Orchestration.Flow;
using DssHarness.Core.Platform;
using DssHarness.Core.Results;

namespace DssHarness.Core.Agents.SideTasks;

/// <summary>
/// A side-task's live owner, <c>.side-tasks/&lt;task&gt;.owner.json</c>: the process whose chat or one shot holds the
/// side-task open, by the one rule of a live owner (<see cref="ProcessOwner"/>) - a live owner refuses another
/// <c>run-side-task</c> of it (13), a dead one is reclaimed and said, and the owner is read strictly. Every claim and
/// release of the repository's side-tasks is taken under one machine-wide lock (<see cref="ClaimsLock"/>), for no section
/// of the repository guards a side-task as the repository's one section guards a run: inside it a new side-task's name is
/// found free, claimed and given its record in one step, and a side-task opened again is claimed with its record read.
/// </summary>
/// <param name="fileSystem">Makes the side-task's directory.</param>
/// <param name="store">Reads and writes the owner file, strictly.</param>
/// <param name="identity">This process, and how another's liveness is told.</param>
/// <param name="paths">This machine's limit on a path's length, which a new side-task's records are held to.</param>
/// <param name="clock">When the side-task was taken.</param>
public sealed class SideTaskOwner(IFileSystem fileSystem, OrchestrationStore store, IProcessIdentity identity, IPathBudget paths, TimeProvider? clock = null)
{
    /// <summary>What a side-task's owner file is, as a refusal names it.</summary>
    public const string OwnerKind = "a side-task's owner";

    /// <summary>
    /// The name, in <c>.side-tasks/</c>, of the path every claim and release of a repository's side-tasks is taken under:
    /// it names the machine-wide lock, and nothing is written there.
    /// </summary>
    public const string ClaimsLockName = ".claims.lock";

    private readonly IFileSystem _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
    private readonly IPathBudget _paths = paths ?? throw new ArgumentNullException(nameof(paths));
    private readonly SideTaskStore _records = new(fileSystem);
    private readonly ProcessOwner _owner = new(store, identity, clock);

    /// <summary>The path naming the lock every claim and release of the side-tasks under <paramref name="root"/> is taken under.</summary>
    /// <param name="root">The repository's <c>.side-tasks/</c> directory.</param>
    public static string ClaimsLock(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        return Path.Combine(root, ClaimsLockName);
    }

    /// <summary>
    /// Claims a side-task to open it again: its record read inside the claim, refused (13) where it holds none or a live
    /// owner holds it - a chat open in another process - and a dead owner reclaimed and said.
    /// </summary>
    /// <param name="layout">The side-task's layout.</param>
    /// <returns>The side-task's record, and what is said of a dead owner's record reclaimed; null where none was recorded.</returns>
    /// <exception cref="HarnessException">The side-task holds no record, a live owner holds it (<see cref="HarnessExit.Refused"/>), or its owner or record does not read as one.</exception>
    public (SideTaskRecord Record, string? Reclaimed) ClaimAgain(SideTaskLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);

        _fileSystem.CreateDirectory(layout.Root);

        return MachineWideFile.Update(ClaimsLock(layout.Root), MachineWideFile.Window, () =>
        {
            var record = _records.Read(layout)
                ?? throw new HarnessException(HarnessExit.Refused, $"side-task '{layout.Name}' names no side-task of this repository: list-side-tasks lists them. Nothing was changed.");

            return (record, Claim(layout));
        });
    }

    /// <summary>
    /// Claims a new side-task and writes its first record inside the claim, so no side-task is ever left without one: the
    /// name the person gave, refused (13) where a side-task holds it; or the name derived from when it began, given the
    /// smallest free suffix (<c>-2</c>, <c>-3</c>, ...) where others began in the same second, however many did. A name
    /// whose records this machine's paths cannot hold - its owner file or its record past the machine's limit on a path's
    /// length (<see cref="IPathBudget"/>), or a name the file system refuses as one - is refused (13) saying so, nothing
    /// written.
    /// </summary>
    /// <param name="root">The repository's <c>.side-tasks/</c> directory.</param>
    /// <param name="name">The name asked for.</param>
    /// <param name="derived">Whether the name was derived rather than given, so a suffix may free it.</param>
    /// <param name="record">The side-task's first record, from the name it takes.</param>
    /// <returns>The side-task's layout and first record, and what is said of a dead owner's record reclaimed; null where none was recorded.</returns>
    /// <exception cref="HarnessException">The name is taken, or its records do not fit this machine's paths (<see cref="HarnessExit.Refused"/>).</exception>
    public (SideTaskLayout Layout, SideTaskRecord Record, string? Reclaimed) ClaimNew(string root, string name, bool derived, Func<string, SideTaskRecord> record)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(record);

        _fileSystem.CreateDirectory(root);

        return MachineWideFile.Update(ClaimsLock(root), MachineWideFile.Window, () =>
        {
            var layout = derived ? Free(root, name) : new SideTaskLayout(root, name);

            if (_records.Read(layout) is not null)
            {
                throw new HarnessException(HarnessExit.Refused, $"side-task '{name}' is taken: --resume {name} opens it again, or --name gives a new one another name. Nothing was changed.");
            }

            if (Unfit(layout) is { } unfit)
            {
                throw new HarnessException(HarnessExit.Refused, $"side-task '{layout.Name}' cannot keep its records on this machine: {unfit} --name gives it a shorter name. Nothing was changed.");
            }

            string? reclaimed;

            try
            {
                reclaimed = Claim(layout);
            }
            catch (IOException ex)
            {
                // The file system's own limit on a name - its length among them - is its own to say: the owner file, the
                // longest name the side-task's records take, is the first it refuses, and nothing was written before it.
                throw new HarnessException(HarnessExit.Refused, $"side-task '{layout.Name}' cannot keep its records on this machine: its owner file '{layout.OwnerFile}' could not be written ({ex.Message.TrimEnd('.')}); --name gives it another name. Nothing was changed.", ex);
            }

            var first = record(layout.Name);

            _fileSystem.CreateDirectory(layout.Directory);
            _records.Write(layout, first);

            return (layout, first, reclaimed);
        });
    }

    /// <summary>
    /// Writes the side-task's record under the claims' lock, where this process holds the side-task: what each turn and
    /// its end leave is never written over a claim another process took after this one was taken for gone.
    /// </summary>
    /// <param name="layout">The side-task's layout.</param>
    /// <param name="record">The record, whole.</param>
    /// <returns>Whether it was written: false where the side-task is another process's now.</returns>
    public bool Keep(SideTaskLayout layout, SideTaskRecord record)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(record);

        return MachineWideFile.Update(ClaimsLock(layout.Root), MachineWideFile.Window, () =>
        {
            if (!_owner.Holds(layout.OwnerFile, OwnerKind))
            {
                return false;
            }

            _records.Write(layout, record);
            return true;
        });
    }

    /// <summary>
    /// Releases the side-task where this process holds it - read again inside the claims' lock, so an owner another
    /// process took after this one was taken for gone is left as it is - its owner removed, so the next chat claims it
    /// afresh.
    /// </summary>
    /// <param name="layout">The side-task's layout.</param>
    public void Release(SideTaskLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);

        MachineWideFile.Update(ClaimsLock(layout.Root), MachineWideFile.Window, () =>
        {
            _owner.Release(layout.OwnerFile, OwnerKind);
            return true;
        });
    }

    /// <summary>Whether a live owner holds the side-task: an open chat, this process's or another's.</summary>
    /// <param name="layout">The side-task's layout.</param>
    /// <exception cref="HarnessException">The owner does not read as one (<see cref="HarnessExit.Refused"/>).</exception>
    public bool Open(SideTaskLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        return _owner.Live(layout.OwnerFile, OwnerKind) is not null;
    }

    /// <summary>Claims <paramref name="layout"/>'s owner for this process, inside the claims' lock.</summary>
    private string? Claim(SideTaskLayout layout)
        => _owner.Claim(
            layout.OwnerFile,
            OwnerKind,
            $"side-task '{layout.Name}'",
            held => $"side-task '{layout.Name}' is open in another process ({held}); end its chat there, or wait for it to leave. Nothing was changed.");

    /// <summary>
    /// The first of <paramref name="name"/>, <c>name-2</c>, <c>name-3</c>, ... no entry of <paramref name="root"/> holds in
    /// any way - no directory, file or link of that name, no owner file beside it - found from one listing of the
    /// directory: the names it holds are a set, and the first name past them is free, however many there are. Names are
    /// told apart in any case, so on a file system that folds case a new side-task is never given the directory of one
    /// named in another.
    /// </summary>
    private SideTaskLayout Free(string root, string name)
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in _fileSystem.EnumerateDirectories(root).Concat(_fileSystem.EnumerateFiles(root, recursive: false)))
        {
            var held = Path.GetFileName(entry);
            taken.Add(held);

            if (held.EndsWith(SideTaskLayout.OwnerSuffix, StringComparison.OrdinalIgnoreCase))
            {
                taken.Add(held[..^SideTaskLayout.OwnerSuffix.Length]);
            }
        }

        for (var suffix = 1L; ; suffix++)
        {
            var candidate = suffix == 1 ? name : $"{name}-{suffix.ToString(CultureInfo.InvariantCulture)}";

            if (!taken.Contains(candidate))
            {
                return new SideTaskLayout(root, candidate);
            }
        }
    }

    /// <summary>
    /// Why this machine's paths cannot hold <paramref name="layout"/>'s records, as its limit on a path's length says it
    /// (<see cref="PathBudgetResult.Describe"/>): the owner file beside the side-task's directory, and the record inside it,
    /// are what the claim writes; null where both fit, or the machine sets no limit.
    /// </summary>
    private string? Unfit(SideTaskLayout layout)
    {
        foreach (var (path, reserve) in new[] { (layout.OwnerFile, 0), (layout.Directory, SideTaskLayout.TaskFileName.Length) })
        {
            if (_paths.Check(path, reserve, margin: 0) is { IsWithinBudget: false } exceeded)
            {
                return exceeded.Describe(path);
            }
        }

        return null;
    }
}
