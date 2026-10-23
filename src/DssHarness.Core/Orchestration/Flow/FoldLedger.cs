using DssHarness.Core.FileSystem;
using DssHarness.Core.Git;
using DssHarness.Core.Results;

namespace DssHarness.Core.Orchestration.Flow;

/// <summary>
/// The one writer of the run's ledger of its folds (<see cref="FlowFoldLedgerFile"/>): a fold's intent - every path it
/// means to write with what the main tree holds there - appended before the verb is called, and what it left there
/// recorded after, so nothing reaches the main tree unrecorded, and a crash between the two leaves an intent the next
/// attempt of the step settles from what the tree holds.
/// </summary>
/// <param name="store">Reads and writes the ledger, inside the run's own section.</param>
/// <param name="fileSystem">Reads what the main tree holds at each path.</param>
/// <param name="clock">Says when each entry was written.</param>
public sealed class FoldLedger(OrchestrationStore store, IFileSystem fileSystem, TimeProvider clock)
{
    /// <summary>
    /// Appends the intent of one fold: every path <paramref name="planned"/> writes, merges, removes, and every registry
    /// file its rows change, each with the digest the main tree holds there now - written before the verb is called.
    /// </summary>
    /// <param name="layout">The run's orchestrator.</param>
    /// <param name="main">The main tree's root.</param>
    /// <param name="instance">The step instance that folds.</param>
    /// <param name="attempt">Its attempt.</param>
    /// <param name="type">The step's type: <c>fold</c> or <c>delete-agent</c>.</param>
    /// <param name="agent">The agent whose work it folds.</param>
    /// <param name="planned">What its dry run named, which its apply is held to.</param>
    /// <param name="cancellationToken">Cancels the reads before anything is written.</param>
    /// <returns>The id of the entry written.</returns>
    public async Task<string> IntendAsync(
        OrchestratorLayout layout,
        string main,
        string instance,
        int attempt,
        string type,
        string agent,
        AgentPlanned planned,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(planned);

        var paths = new List<FlowFoldPath>();
        var named = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (path, what) in PathsOf(planned))
        {
            if (named.Add(path))
            {
                paths.Add(new FlowFoldPath(path, what, await FileContentHash.OfFileAsync(fileSystem, Path.Combine(main, path), cancellationToken).ConfigureAwait(false)));
            }
        }

        var written = store.UpdateFolds(layout, ledger => ledger with
        {
            Folds =
            [
                .. ledger.Folds,
                new FlowFoldEntry(FlowFoldEntry.IdOf(ledger.Folds.Count + 1), instance, attempt, type, agent, FlowFoldEntry.Intended, clock.GetUtcNow(), paths, planned.Rows),
            ],
        });

        return written.Folds[^1].Id;
    }

    /// <summary>
    /// Records what became of the fold <paramref name="id"/> once its verb answered <paramref name="exitCode"/>: applied
    /// (0), stopped part way (21), or failed otherwise - after it may have written part of it - each with what the main
    /// tree holds at each path now; refused, having written nothing, only for a refusal's code
    /// (<see cref="HarnessExit.RefusesTheRun"/>), which no verb answers after writing. Never cancelled: what was written is
    /// recorded whatever stops the step.
    /// </summary>
    /// <param name="layout">The run's orchestrator.</param>
    /// <param name="main">The main tree's root.</param>
    /// <param name="id">The entry's id, as <see cref="IntendAsync"/> gave it.</param>
    /// <param name="exitCode">The code the verb answered with.</param>
    /// <returns>The entry as it was settled.</returns>
    public async Task<FlowFoldEntry> SettleAsync(OrchestratorLayout layout, string main, string id, int exitCode)
    {
        ArgumentNullException.ThrowIfNull(layout);

        var state = exitCode switch
        {
            HarnessExit.Success => "applied",
            HarnessExit.Incomplete => "stopped",
            _ when HarnessExit.RefusesTheRun(exitCode) => FlowFoldEntry.Refused,
            _ => FlowFoldEntry.Failed,
        };

        var entry = store.ReadFolds(layout)?.Of(id) ?? throw new HarnessException(HarnessExit.InternalError, $"the run's ledger of its folds holds no fold '{id}' to settle");
        var paths = state == FlowFoldEntry.Refused ? entry.Paths : await LeftAsync(main, entry.Paths).ConfigureAwait(false);
        var settled = entry with { State = state, SettledAt = clock.GetUtcNow(), Paths = paths };

        Replace(layout, settled);
        return settled;
    }

    /// <summary>
    /// Settles every intent of <paramref name="instance"/> a crash left - its verb called and nothing recorded after - as
    /// interrupted, with what the main tree holds at each path now: what the step's next attempt reads before it folds
    /// anything again.
    /// </summary>
    /// <param name="layout">The run's orchestrator.</param>
    /// <param name="main">The main tree's root.</param>
    /// <param name="instance">The step instance.</param>
    /// <returns>Each intent it settled, as it was settled.</returns>
    public async Task<IReadOnlyList<FlowFoldEntry>> SettleInterruptedAsync(OrchestratorLayout layout, string main, string instance)
    {
        ArgumentNullException.ThrowIfNull(layout);

        var left = (store.ReadFolds(layout)?.Folds ?? [])
            .Where(entry => entry.State == FlowFoldEntry.Intended && string.Equals(entry.Instance, instance, StringComparison.Ordinal))
            .ToList();
        var settled = new List<FlowFoldEntry>();

        foreach (var entry in left)
        {
            settled.Add(entry with { State = FlowFoldEntry.Interrupted, SettledAt = clock.GetUtcNow(), Paths = await LeftAsync(main, entry.Paths).ConfigureAwait(false) });
            Replace(layout, settled[^1]);
        }

        return settled;
    }

    /// <summary>Every path <paramref name="entries"/> changed in the main tree, each once: what a fold left otherwise than it found.</summary>
    /// <param name="entries">Settled entries of the ledger.</param>
    public static IReadOnlyList<string> Changed(IEnumerable<FlowFoldEntry> entries)
        => [.. entries.SelectMany(entry => entry.Paths).Where(path => path.Changed == true).Select(path => path.Path).Distinct(StringComparer.Ordinal)];

    /// <summary>
    /// The settled folds of <paramref name="ledger"/> whose changed paths the run's codebase graph has not taken yet - a
    /// sync of them failed, was stopped, or was never reached for a crash or a throw: what the next sync of the folds takes.
    /// </summary>
    /// <param name="ledger">The run's ledger; null where it holds none.</param>
    public static IReadOnlyList<FlowFoldEntry> Ungraphed(FlowFoldLedgerFile? ledger)
        => [.. (ledger?.Folds ?? []).Where(entry => entry.State is not (FlowFoldEntry.Intended or FlowFoldEntry.Refused) && entry.Graphed != true)];

    /// <summary>Records that the run's codebase graph took the paths each fold of <paramref name="ids"/> changed.</summary>
    /// <param name="layout">The run's orchestrator.</param>
    /// <param name="ids">The folds' ids.</param>
    public void Graphed(OrchestratorLayout layout, IReadOnlyCollection<string> ids)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(ids);

        store.UpdateFolds(layout, ledger => ledger with
        {
            Folds = [.. ledger.Folds.Select(fold => ids.Contains(fold.Id, StringComparer.Ordinal) ? fold with { Graphed = true } : fold)],
        });
    }

    /// <summary>
    /// The folds of <paramref name="ledger"/> that hold what no commit has taken yet, in the order intended: what makes a
    /// run that stopped unfinished. An intent nobody settled is one - what it wrote cannot be told - and so is a fold a
    /// path of which it changed the main tree still holds otherwise than its HEAD does, as <paramref name="differing"/>
    /// says; a path committed since, by a step of the run or by hand, or put back as HEAD holds it, holds nothing a commit
    /// would take. Where the main tree was not read, every path a fold changed is counted, for what cannot be told is
    /// never read as committed.
    /// </summary>
    /// <param name="ledger">The run's ledger; null where it holds none.</param>
    /// <param name="differing">
    /// Every path the main tree holds otherwise than its HEAD - modified, staged, deleted or untracked
    /// (<see cref="DifferingAsync"/>); null where it was not read.
    /// </param>
    public static IReadOnlyList<FlowFoldEntry> Uncommitted(FlowFoldLedgerFile? ledger, IReadOnlySet<string>? differing)
        => [.. (ledger?.Folds ?? []).Where(entry => entry.State == FlowFoldEntry.Intended
            || entry.Paths.Any(path => path.Changed == true && (differing is null || differing.Contains(path.Path))))];

    /// <summary>Whether <paramref name="ledger"/> holds a path a fold changed, whose commit only the main tree can say.</summary>
    /// <param name="ledger">The run's ledger; null where it holds none.</param>
    public static bool ChangedAny(FlowFoldLedgerFile? ledger)
        => (ledger?.Folds ?? []).Any(entry => entry.Paths.Any(path => path.Changed == true));

    /// <summary>
    /// Every path the main tree at <paramref name="main"/> holds otherwise than its HEAD does, as git's status names it -
    /// modified, staged, deleted, and each file of an untracked directory - for <see cref="Uncommitted"/> to hold a
    /// fold's changed paths to. Nothing is written.
    /// </summary>
    /// <param name="git">Reads the main tree's status.</param>
    /// <param name="main">The main tree's root.</param>
    /// <param name="cancellationToken">Stops the read.</param>
    /// <exception cref="HarnessException">git could not read the status.</exception>
    public static async Task<IReadOnlySet<string>> DifferingAsync(IGitClient git, string main, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(git);

        return GitName.PathsOf((await git.ReadStatusAsync(main, cancellationToken).ConfigureAwait(false)).SelectMany(entry => entry.Paths));
    }

    /// <summary>
    /// Every path a fold's plan writes in the main tree, with what it does to each: written, merged - the arm registry
    /// among them - removed, and each registry file its rows change.
    /// </summary>
    private static IEnumerable<(string Path, string What)> PathsOf(AgentPlanned planned)
        => planned.Written.Select(path => (path, "written"))
            .Concat(planned.Merged.Concat(planned.ArmRegistry is { } arms ? [arms] : []).Select(path => (path, "merged")))
            .Concat(planned.Deleted.Select(path => (path, "deleted")))
            .Concat(planned.Registries.Select(path => (path, "registry")));

    /// <summary>Each of <paramref name="paths"/> with what the main tree holds there now, and whether that is not what it held before.</summary>
    private async Task<IReadOnlyList<FlowFoldPath>> LeftAsync(string main, IReadOnlyList<FlowFoldPath> paths)
    {
        var left = new List<FlowFoldPath>();

        foreach (var path in paths)
        {
            var digest = await FileContentHash.OfFileAsync(fileSystem, Path.Combine(main, path.Path), CancellationToken.None).ConfigureAwait(false);
            left.Add(path with { Left = digest, Changed = !string.Equals(digest, path.Before, StringComparison.Ordinal) });
        }

        return left;
    }

    /// <summary>Writes <paramref name="entry"/> over the one of its id, inside the run's own section.</summary>
    private void Replace(OrchestratorLayout layout, FlowFoldEntry entry)
        => store.UpdateFolds(layout, ledger => ledger with
        {
            Folds = [.. ledger.Folds.Select(fold => string.Equals(fold.Id, entry.Id, StringComparison.Ordinal) ? entry : fold)],
        });
}
