using DssHarness.Core.FileSystem;
using DssHarness.Core.Git;
using DssHarness.Core.Results;

namespace DssHarness.Core.Orchestration.Flow;

/// <summary>What a commit owes, as <see cref="FoldLedger.Owed"/> reads it from the run's ledger.</summary>
/// <param name="Paths">Each path it owes, once, in the order the ledger first names it.</param>
/// <param name="Unsettled">Each intent of the folds it commits that nobody settled: what they wrote cannot be told.</param>
public sealed record FoldOwed(IReadOnlyList<FoldOwedPath> Paths, IReadOnlyList<FlowFoldEntry> Unsettled);

/// <summary>
/// One path a commit owes: what the main tree must hold there, what it held before the folds that owe it, the step whose
/// fold left it, and every fold the commit settles by it.
/// </summary>
/// <param name="Path">The path, relative to the tree and spelt with forward slashes.</param>
/// <param name="Digest">What the newest settled fold that names it left there; null where it left no file - a deletion.</param>
/// <param name="Before">What the main tree held there before the oldest fold that owes it; null where it held no file.</param>
/// <param name="LeftBy">The instance of the newest fold's step.</param>
/// <param name="Folds">Every fold of the ledger, of any step, that leaves it uncommitted, by id: a commit that takes the path settles it for each.</param>
public sealed record FoldOwedPath(string Path, string? Digest, string? Before, string LeftBy, IReadOnlyList<string> Folds);

/// <summary>
/// The one writer of the run's ledger of its folds (<see cref="FlowFoldLedgerFile"/>): a fold's intent - every path it
/// means to write with what the main tree holds there - appended before the verb is called, and what it left there
/// recorded after, so nothing reaches the main tree unrecorded, and a crash between the two leaves an intent the next
/// attempt of the step settles from what the tree holds; and a commit's staged set, written before its first
/// <c>git add</c> and marked committed - with each fold path it took - or unstaged after.
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
    /// path of which a commit owes (<see cref="FlowFoldPath.Owed"/>) the main tree still holds otherwise than its HEAD
    /// does, as <paramref name="differing"/> says; a path a commit step of the run took, one git ignores, and one
    /// committed by hand since or put back as HEAD holds it, holds nothing a commit would take. Where the main tree was not
    /// read, every path a commit owes is counted, for what cannot be told is never read as committed.
    /// </summary>
    /// <param name="ledger">The run's ledger; null where it holds none.</param>
    /// <param name="differing">
    /// Every path the main tree holds otherwise than its HEAD - modified, staged, deleted or untracked
    /// (<see cref="DifferingAsync"/>); null where it was not read.
    /// </param>
    public static IReadOnlyList<FlowFoldEntry> Uncommitted(FlowFoldLedgerFile? ledger, IReadOnlySet<string>? differing)
        => [.. (ledger?.Folds ?? []).Where(entry => entry.State == FlowFoldEntry.Intended
            || entry.Paths.Any(path => path.Owed && (differing is null || differing.Contains(path.Path))))];

    /// <summary>Whether <paramref name="ledger"/> holds a path a commit owes, which only the main tree can say was not committed by hand.</summary>
    /// <param name="ledger">The run's ledger; null where it holds none.</param>
    public static bool OwesAny(FlowFoldLedgerFile? ledger)
        => (ledger?.Folds ?? []).Any(entry => entry.Paths.Any(path => path.Owed));

    /// <summary>
    /// What a commit of the folds of the step instances <paramref name="upstream"/> weighs: each path one of their settled
    /// folds changed that no commit of the run took - one git ignored when last asked among them, which the commit asks
    /// again - once, in the order the ledger first names it, with what the newest settled fold that names it, of any step,
    /// left there, so a registry two work items' folds both wrote is taken whole, as the newer left it, and settled for
    /// each, and what the oldest that owes it found there; and each intent of theirs nobody settled.
    /// Nothing is read but the ledger.
    /// </summary>
    /// <param name="ledger">The run's ledger; null where it holds none.</param>
    /// <param name="upstream">The instances of the <c>fold</c> and <c>delete-agent</c> steps upstream of the commit.</param>
    public static FoldOwed Owed(FlowFoldLedgerFile? ledger, IReadOnlyCollection<string> upstream)
    {
        ArgumentNullException.ThrowIfNull(upstream);

        var folds = ledger?.Folds ?? [];
        var ours = folds.Where(entry => upstream.Contains(entry.Instance, StringComparer.Ordinal)).ToList();
        var owed = new List<FoldOwedPath>();

        foreach (var path in ours.Where(Settled).SelectMany(entry => entry.Paths).Where(path => path.Uncommitted).Select(path => path.Path).Distinct(StringComparer.Ordinal))
        {
            var newest = folds.Last(entry => Settled(entry) && entry.Paths.Any(named => named.Path == path && named.Changed is not null));
            var owing = folds.Where(entry => Settled(entry) && entry.Paths.Any(named => named.Path == path && named.Uncommitted)).ToList();

            owed.Add(new FoldOwedPath(
                path,
                newest.Paths.First(named => named.Path == path).Left,
                owing[0].Paths.First(named => named.Path == path).Before,
                newest.Instance,
                [.. owing.Select(entry => entry.Id)]));
        }

        return new FoldOwed(owed, [.. ours.Where(entry => entry.State == FlowFoldEntry.Intended)]);

        // A fold whose ending was recorded with what it left: an intent left nobody knows what, and a refusal wrote nothing.
        static bool Settled(FlowFoldEntry entry) => entry.State is not (FlowFoldEntry.Intended or FlowFoldEntry.Refused);
    }

    /// <summary>
    /// Appends a commit's staged set - the paths it stages with what the main tree holds at each, the folds they settle
    /// and the HEAD it commits on - written before the first <c>git add</c>, so a crash after it leaves an index the next
    /// attempt can tell is its own.
    /// </summary>
    /// <param name="layout">The run's orchestrator.</param>
    /// <param name="instance">The commit step's instance.</param>
    /// <param name="attempt">Its attempt.</param>
    /// <param name="head">The commit HEAD names; null where the branch has none yet.</param>
    /// <param name="paths">Each path it stages, with what the main tree holds there.</param>
    /// <param name="folds">The folds those paths settle, by id.</param>
    /// <returns>The id of the entry written.</returns>
    public string Staging(OrchestratorLayout layout, string instance, int attempt, string? head, IReadOnlyList<FlowCommitPath> paths, IReadOnlyList<string> folds)
    {
        ArgumentNullException.ThrowIfNull(layout);

        var written = store.UpdateFolds(layout, ledger => ledger with
        {
            Commits =
            [
                .. ledger.Commits,
                new FlowCommitEntry(FlowCommitEntry.IdOf(ledger.Commits.Count + 1), instance, attempt, FlowCommitEntry.Staged, head, paths, folds, clock.GetUtcNow()),
            ],
        });

        return written.Commits[^1].Id;
    }

    /// <summary>
    /// Records that the commit <paramref name="sha"/> took the staged set <paramref name="id"/>: the entry is committed,
    /// and each of its paths is marked taken by it in every fold it names that owes it.
    /// </summary>
    /// <param name="layout">The run's orchestrator.</param>
    /// <param name="id">The staged set's id, as <see cref="Staging"/> gave it.</param>
    /// <param name="sha">The commit that took its paths.</param>
    public void Committed(OrchestratorLayout layout, string id, string sha)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentException.ThrowIfNullOrWhiteSpace(sha);

        store.UpdateFolds(layout, ledger =>
        {
            var entry = StagedOf(ledger, id);
            var paths = entry.Paths.Select(path => path.Path).ToHashSet(StringComparer.Ordinal);

            return ledger with
            {
                Folds = [.. ledger.Folds.Select(fold => entry.Folds.Contains(fold.Id, StringComparer.Ordinal)
                    ? fold with { Paths = [.. fold.Paths.Select(path => path.Owed && paths.Contains(path.Path) ? path with { Committed = id } : path)] }
                    : fold)],
                Commits = [.. ledger.Commits.Select(commit => commit.Id == id ? commit with { State = FlowCommitEntry.Committed, SettledAt = clock.GetUtcNow(), Sha = sha } : commit)],
            };
        });
    }

    /// <summary>Records that the staged set <paramref name="id"/> was unstaged, no commit having taken it: what it would have taken is owed still.</summary>
    /// <param name="layout">The run's orchestrator.</param>
    /// <param name="id">The staged set's id, as <see cref="Staging"/> gave it.</param>
    public void Unstaged(OrchestratorLayout layout, string id)
    {
        ArgumentNullException.ThrowIfNull(layout);

        store.UpdateFolds(layout, ledger =>
        {
            _ = StagedOf(ledger, id);

            return ledger with
            {
                Commits = [.. ledger.Commits.Select(commit => commit.Id == id ? commit with { State = FlowCommitEntry.Unstaged, SettledAt = clock.GetUtcNow() } : commit)],
            };
        });
    }

    /// <summary>
    /// Records what git answered of the uncommitted paths <paramref name="asked"/>: each of <paramref name="ignored"/> is
    /// marked ignored in every fold that leaves it uncommitted - no commit of the run takes it, and it holds the run
    /// unfinished no more - and each other is marked so no longer, as the rules may have changed since git was last asked.
    /// </summary>
    /// <param name="layout">The run's orchestrator.</param>
    /// <param name="asked">The paths git was asked about.</param>
    /// <param name="ignored">Those git ignores.</param>
    public void Ignored(OrchestratorLayout layout, IReadOnlyCollection<string> asked, IReadOnlySet<string> ignored)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(asked);
        ArgumentNullException.ThrowIfNull(ignored);

        var file = store.ReadFolds(layout);

        if (file is null || !file.Folds.SelectMany(fold => fold.Paths).Any(path => path.Uncommitted && asked.Contains(path.Path) && (path.Ignored == true) != ignored.Contains(path.Path)))
        {
            return;
        }

        store.UpdateFolds(layout, ledger => ledger with
        {
            Folds = [.. ledger.Folds.Select(fold => fold with
            {
                Paths = [.. fold.Paths.Select(path => path.Uncommitted && asked.Contains(path.Path) ? path with { Ignored = ignored.Contains(path.Path) ? true : null } : path)],
            })],
        });
    }

    /// <summary>The staged set <paramref name="id"/>, which a commit settles once.</summary>
    private static FlowCommitEntry StagedOf(FlowFoldLedgerFile ledger, string id)
        => ledger.Commits.FirstOrDefault(commit => commit.Id == id) is { State: FlowCommitEntry.Staged } staged
            ? staged
            : throw new HarnessException(HarnessExit.InternalError, $"the run's ledger of its folds holds no staged commit '{id}' to settle");

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

        return DifferingOf(await git.ReadStatusAsync(main, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Every path <paramref name="status"/> names - both of a rename's - each once: what the tree holds otherwise than its HEAD.</summary>
    /// <param name="status">The main tree's status, as <see cref="IGitClient.ReadStatusAsync"/> reads it.</param>
    public static IReadOnlySet<string> DifferingOf(IEnumerable<GitStatusEntry> status)
    {
        ArgumentNullException.ThrowIfNull(status);

        return GitName.PathsOf(status.SelectMany(entry => entry.Paths));
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
