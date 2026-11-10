using DssHarness.Core.FileSystem;
using DssHarness.Core.Git;
using DssHarness.Core.Platform;
using DssHarness.Core.Results;

namespace DssHarness.Core.Orchestration.Flow;

/// <summary>What a commit owes, as <see cref="FoldLedger.Owed"/> reads it from the run's ledger.</summary>
/// <param name="Paths">Each path it owes, once, in the order the ledger first names it.</param>
/// <param name="Unsettled">Each fold it commits that nobody settled - an intent, or a write begun that recorded nothing: what they wrote cannot be told.</param>
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
/// The entry of the run's ledger of its folds a fold's <c>--apply</c> was intended under (<see cref="FoldLedger.IntendAsync"/>),
/// handed to the verb with its request, so that under the main tree's lock its write is made under - before it lets that
/// lock go - it records through the ledger that its write begins, and, once the write is done or has thrown part way,
/// what it left at each path.
/// </summary>
/// <param name="Ledger">The run's ledger of its folds.</param>
/// <param name="Layout">The run's orchestrator.</param>
/// <param name="Id">The entry's id.</param>
public sealed record FoldIntent(FoldLedger Ledger, OrchestratorLayout Layout, string Id)
{
    /// <summary>Records that the write begins (<see cref="FoldLedger.Writing"/>).</summary>
    /// <param name="tree">The hold of the main tree's lock the write is made under.</param>
    public void Writing(MachineWideHold tree) => Ledger.Writing(Layout, Id, tree);

    /// <summary>Records what the write left at each path (<see cref="FoldLedger.WrittenAsync"/>).</summary>
    /// <param name="main">The main tree's root.</param>
    /// <param name="tree">The hold of the main tree's lock the write was made under, still held.</param>
    public Task WrittenAsync(string main, MachineWideHold tree) => Ledger.WrittenAsync(Layout, Id, main, tree);
}

/// <summary>
/// The one writer of the run's ledger of its folds (<see cref="FlowFoldLedgerFile"/>): a fold's intent - every path it
/// means to write with what the main tree holds there - appended before the verb is called; then, by the verb, under the
/// main tree's lock its write is made under and before it lets that lock go (<see cref="FoldIntent"/>), that its write
/// begins and what it left at each path once the write is done - so nothing reaches the main tree unrecorded, and nothing
/// a side-task writes once the fold lets go is recorded as the fold's, whether the verb's answer, a throw or a crash comes
/// after; then what became of it once the verb answered. A crash or a throw that leaves an entry unsettled is settled by
/// the step - from what the verb recorded, where it did; as found, where it never began to write; and only for a crash
/// part way through the write itself, or an intent an earlier release wrote, whose verb recorded nothing of its write,
/// from the tree, read under that lock and marked so. And a commit's staged set,
/// written before its first <c>git add</c> and marked committed - with each fold path it took - or unstaged after, or
/// left staged with the commit made of it where that commit was not the set's alone.
/// </summary>
/// <param name="store">Reads and writes the ledger, inside the run's own section.</param>
/// <param name="fileSystem">Reads what the main tree holds at each path: before a fold, once its write is done, and after a crash part way through it.</param>
/// <param name="clock">Says when each entry was written.</param>
public sealed class FoldLedger(OrchestrationStore store, IFileSystem fileSystem, TimeProvider clock)
{
    /// <summary>
    /// Appends the intent of one fold: every path <paramref name="planned"/> writes, merges, removes, and every registry
    /// file its rows change, each with the digest the main tree holds there now - written before the verb is called, and
    /// saying that its verb records its own write (<see cref="FlowFoldEntry.RecordsWrite"/>).
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

        foreach (var (path, what) in PathsOf(planned))
        {
            paths.Add(new FlowFoldPath(path, what, await FileContentHash.OfFileAsync(fileSystem, Path.Combine(main, path), cancellationToken).ConfigureAwait(false)));
        }

        var written = store.UpdateFolds(layout, ledger => ledger with
        {
            Folds =
            [
                .. ledger.Folds,
                new FlowFoldEntry(FlowFoldEntry.IdOf(ledger.Folds.Count + 1), instance, attempt, type, agent, FlowFoldEntry.Intended, clock.GetUtcNow(), paths, planned.Rows, RecordsWrite: true),
            ],
        });

        return written.Folds[^1].Id;
    }

    /// <summary>
    /// Records, for the verb, that the write of the fold <paramref name="id"/> begins: written under
    /// <paramref name="tree"/>, the hold of the main tree's lock the write is made under, right before its first write - so
    /// an intent a crash leaves without it never began to write, and is settled as it found each path.
    /// </summary>
    /// <param name="layout">The run's orchestrator.</param>
    /// <param name="id">The entry's id, as <see cref="IntendAsync"/> gave it.</param>
    /// <param name="tree">The hold the write is made under.</param>
    /// <exception cref="InvalidOperationException"><paramref name="tree"/> was let go.</exception>
    /// <exception cref="HarnessException">The ledger holds no such intent: the engine's fault (<see cref="HarnessExit.InternalError"/>).</exception>
    public void Writing(OrchestratorLayout layout, string id, MachineWideHold tree)
    {
        ArgumentNullException.ThrowIfNull(layout);
        Held(tree);

        Replace(layout, Of(layout, id, FlowFoldEntry.Intended) with { State = FlowFoldEntry.Writing });
    }

    /// <summary>
    /// Records, for the verb, what the write of the fold <paramref name="id"/> left at each path its intent names - read
    /// from the main tree under <paramref name="tree"/>, the hold the write was made under, before it is let go, once the
    /// write is done or has thrown part way: a side-task's write that waited on the hold comes after, and whatever comes
    /// after - the verb's answer, a throw, a crash - the fold is settled by what it left. Never cancelled: the write is in.
    /// </summary>
    /// <param name="layout">The run's orchestrator.</param>
    /// <param name="id">The entry's id.</param>
    /// <param name="main">The main tree's root.</param>
    /// <param name="tree">The hold the write was made under, still held.</param>
    /// <exception cref="InvalidOperationException"><paramref name="tree"/> was let go: what is read outside the lock is not the fold's.</exception>
    /// <exception cref="HarnessException">The ledger holds no such write begun: the engine's fault (<see cref="HarnessExit.InternalError"/>).</exception>
    public async Task WrittenAsync(OrchestratorLayout layout, string id, string main, MachineWideHold tree)
    {
        ArgumentNullException.ThrowIfNull(layout);
        Held(tree);

        var entry = Of(layout, id, FlowFoldEntry.Writing);

        Replace(layout, entry with { State = FlowFoldEntry.Written, SettledAt = clock.GetUtcNow(), Paths = await HeldAsync(main, entry.Paths).ConfigureAwait(false) });
    }

    /// <summary>
    /// Records what became of the fold <paramref name="id"/> once its verb answered <paramref name="exitCode"/> - or was
    /// stopped before its write, which a stop's 21 says - reading nothing of the tree, where a side-task's write that
    /// waited on the fold's hold may stand by now. A fold whose write was recorded (<see cref="WrittenAsync"/>) is applied
    /// (0), stopped (21) or failed otherwise - a refusal's code among them, which would hide what it wrote - with what it
    /// recorded it left; one whose verb never began to write stands as it found each path: stopped (21), refused, settling
    /// nothing, for a refusal's code (<see cref="HarnessExit.RefusesTheRun"/>), or failed otherwise. Nothing stops it: what
    /// was written is recorded whatever stops the step.
    /// </summary>
    /// <param name="layout">The run's orchestrator.</param>
    /// <param name="id">The entry's id, as <see cref="IntendAsync"/> gave it.</param>
    /// <param name="exitCode">The code the verb answered with; 21 for a verb stopped before its write.</param>
    /// <returns>The entry as it was settled.</returns>
    /// <exception cref="HarnessException">
    /// The ledger holds no such fold unsettled, or none whose verb records its write, or its verb answered with its write
    /// begun and not recorded, or went through without beginning one: each the engine's fault
    /// (<see cref="HarnessExit.InternalError"/>), the entry left for the step's next attempt to settle.
    /// </exception>
    public FlowFoldEntry Settle(OrchestratorLayout layout, string id, int exitCode)
    {
        ArgumentNullException.ThrowIfNull(layout);

        var entry = store.ReadFolds(layout)?.Of(id) is { State: FlowFoldEntry.Intended or FlowFoldEntry.Written, RecordsWrite: true } found
            ? found
            : throw new HarnessException(
                HarnessExit.InternalError,
                $"the run's ledger of its folds holds no fold '{id}' whose verb records its write and answered with it recorded or never begun: what it wrote cannot be told, and the step's next attempt settles it");
        var wrote = entry.State == FlowFoldEntry.Written;

        if (!wrote && exitCode == HarnessExit.Success)
        {
            throw new HarnessException(HarnessExit.InternalError, $"fold '{id}' went through and recorded no write: what it wrote cannot be told, and the step's next attempt settles it");
        }

        var state = exitCode switch
        {
            HarnessExit.Success => "applied",
            HarnessExit.Incomplete => "stopped",
            _ when !wrote && HarnessExit.RefusesTheRun(exitCode) => FlowFoldEntry.Refused,
            _ => FlowFoldEntry.Failed,
        };
        var paths = wrote ? entry.Paths : state == FlowFoldEntry.Refused ? entry.Paths : Found(entry.Paths);
        var settled = entry with { State = state, SettledAt = clock.GetUtcNow(), Paths = paths };

        Replace(layout, settled);
        return settled;
    }

    /// <summary>
    /// Settles, as interrupted, every fold of <paramref name="instance"/> a crash or a throw out of its verb left unsettled:
    /// what the step reads before it folds anything again. One whose verb recorded what it left is settled by that, the
    /// tree unread, whatever a side-task wrote since; an intent whose verb records its write and never began one, as it
    /// found each path; and only what the tree alone can tell (<see cref="FlowFoldEntry.WrittenUnrecorded"/>) - a fold cut
    /// off part way through its write, which recorded that it began and nothing after, or an intent an earlier release
    /// wrote, whose verb recorded nothing of its write and may have written all of it - is read from the main tree, under
    /// the main tree's lock, so never a file a side-task's write or command is part way through, and marked so
    /// (<see cref="FlowFoldEntry.ReadFromTree"/>): what a side-task wrote between the crash and the read cannot be told from
    /// the fold's. The lock is taken only for such a read; its callers hold none, and nothing it calls takes one.
    /// </summary>
    /// <param name="layout">The run's orchestrator.</param>
    /// <param name="main">The main tree's root.</param>
    /// <param name="instance">The step instance.</param>
    /// <param name="cancellationToken">Ends the wait for the lock, each fold left as it was.</param>
    /// <returns>Each fold it settled, as it was settled.</returns>
    /// <exception cref="OperationCanceledException">The wait for the tree's lock was ended: nothing was settled.</exception>
    public async Task<IReadOnlyList<FlowFoldEntry>> SettleInterruptedAsync(OrchestratorLayout layout, string main, string instance, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(layout);

        var left = (store.ReadFolds(layout)?.Folds ?? [])
            .Where(entry => entry.State is (FlowFoldEntry.Intended or FlowFoldEntry.Writing or FlowFoldEntry.Written) && string.Equals(entry.Instance, instance, StringComparison.Ordinal))
            .ToList();
        var settled = new List<FlowFoldEntry>();

        using var tree = left.Any(entry => entry.WrittenUnrecorded)
            ? await MainTreeWriteLock.HoldAsync(main, cancellationToken).ConfigureAwait(false) ?? throw new OperationCanceledException(cancellationToken)
            : null;

        foreach (var entry in left)
        {
            var interrupted = entry with { State = FlowFoldEntry.Interrupted, SettledAt = clock.GetUtcNow() };

            settled.Add(
                entry.State == FlowFoldEntry.Written ? interrupted
                : entry.WrittenUnrecorded ? interrupted with { Paths = await HeldAsync(main, entry.Paths).ConfigureAwait(false), ReadFromTree = true }
                : interrupted with { Paths = Found(entry.Paths) });
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
        => [.. (ledger?.Folds ?? []).Where(entry => entry.Settled && entry.Graphed != true)];

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
    /// read, every path a commit owes is counted, for what cannot be told is never read as committed. A write begun that
    /// recorded nothing is counted as an intent is.
    /// </summary>
    /// <param name="ledger">The run's ledger; null where it holds none.</param>
    /// <param name="differing">
    /// The paths a commit owes (<see cref="OwedPaths"/>) the main tree holds otherwise than its HEAD, told by what each
    /// holds (<see cref="DifferingAsync"/>); null where it was not read.
    /// </param>
    public static IReadOnlyList<FlowFoldEntry> Uncommitted(FlowFoldLedgerFile? ledger, IReadOnlySet<string>? differing)
        => [.. (ledger?.Folds ?? []).Where(entry => entry.Unsettled
            || entry.Paths.Any(path => path.Owed && (differing is null || differing.Contains(path.Path))))];

    /// <summary>Whether <paramref name="ledger"/> holds a path a commit owes, which only the main tree can say was not committed by hand.</summary>
    /// <param name="ledger">The run's ledger; null where it holds none.</param>
    public static bool OwesAny(FlowFoldLedgerFile? ledger) => OwedPaths(ledger).Count > 0;

    /// <summary>
    /// Every path of <paramref name="ledger"/> a commit owes (<see cref="FlowFoldPath.Owed"/>), each once, in the order the
    /// ledger first names it: what the main tree is asked of (<see cref="DifferingAsync"/>) to tell whether the run is
    /// unfinished.
    /// </summary>
    /// <param name="ledger">The run's ledger; null where it holds none.</param>
    public static IReadOnlyList<string> OwedPaths(FlowFoldLedgerFile? ledger)
        => [.. (ledger?.Folds ?? []).SelectMany(entry => entry.Paths).Where(path => path.Owed).Select(path => path.Path).Distinct(StringComparer.Ordinal)];

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

        foreach (var path in ours.Where(entry => entry.Settled).SelectMany(entry => entry.Paths).Where(path => path.Uncommitted).Select(path => path.Path).Distinct(StringComparer.Ordinal))
        {
            var newest = folds.Last(entry => entry.Settled && entry.Paths.Any(named => named.Path == path && named.Changed is not null));
            var owing = folds.Where(entry => entry.Settled && entry.Paths.Any(named => named.Path == path && named.Uncommitted)).ToList();

            owed.Add(new FoldOwedPath(
                path,
                newest.Paths.First(named => named.Path == path).Left,
                owing[0].Paths.First(named => named.Path == path).Before,
                newest.Instance,
                [.. owing.Select(entry => entry.Id)]));
        }

        return new FoldOwed(owed, [.. ours.Where(entry => entry.Unsettled)]);
    }

    /// <summary>
    /// Each path a settled fold of the step instances <paramref name="upstream"/> changed that no commit of the run took
    /// and git ignored when last asked (<see cref="FlowFoldPath.Ignored"/>), once, in the order the ledger first names it:
    /// what a commit of their folds answers it left out - folded, ignored by git, never committed.
    /// </summary>
    /// <param name="ledger">The run's ledger; null where it holds none.</param>
    /// <param name="upstream">The instances of the <c>fold</c> and <c>delete-agent</c> steps upstream of the commit.</param>
    public static IReadOnlyList<string> IgnoredOf(FlowFoldLedgerFile? ledger, IReadOnlyCollection<string> upstream)
    {
        ArgumentNullException.ThrowIfNull(upstream);

        return
        [
            .. (ledger?.Folds ?? [])
                .Where(entry => entry.Settled && upstream.Contains(entry.Instance, StringComparer.Ordinal))
                .SelectMany(entry => entry.Paths)
                .Where(path => path.Uncommitted && path.Ignored == true)
                .Select(path => path.Path)
                .Distinct(StringComparer.Ordinal),
        ];
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

    /// <summary>
    /// Records that the commit made of the staged set <paramref name="id"/> is not the set's alone - a hook staged more, or
    /// git left a path of it out (<paramref name="stray"/>) - and leaves the set staged: the commit is a person's to keep or
    /// undo, and the step's next attempt settles the set from what HEAD and the tree hold by then.
    /// </summary>
    /// <param name="layout">The run's orchestrator.</param>
    /// <param name="id">The staged set's id, as <see cref="Staging"/> gave it.</param>
    /// <param name="stray">The commit made, what it took besides the set and what of the set it did not take.</param>
    public void Strayed(OrchestratorLayout layout, string id, FlowCommitStray stray)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(stray);

        store.UpdateFolds(layout, ledger =>
        {
            _ = StagedOf(ledger, id);

            return ledger with
            {
                Commits = [.. ledger.Commits.Select(commit => commit.Id == id ? commit with { Strayed = stray } : commit)],
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
    /// Which of <paramref name="paths"/> the main tree at <paramref name="main"/> holds otherwise than its HEAD does, told by
    /// what each holds: the blob git add would record for its file (<see cref="IGitClient.ListStagedIdsAsync"/> - its
    /// carriage returns kept where the index holds the path with them, as git keeps them) against the one HEAD holds at
    /// the path (<see cref="IGitClient.HeldAtAsync"/>), and no file against none - never by git's status, which does not
    /// look at a path marked assume-unchanged or skip-worktree, and would read its change as none. The one answer to
    /// whether HEAD holds a fold's path as the tree does: whether a run is unfinished (<see cref="Uncommitted"/>), and
    /// whether a commit step finds a path committed - by hand, or by an attempt of its own a crash cut off. Nothing is
    /// written.
    /// </summary>
    /// <param name="git">Reads HEAD, and what the files hold.</param>
    /// <param name="fileSystem">Tells which paths hold a file.</param>
    /// <param name="main">The main tree's root.</param>
    /// <param name="paths">The paths asked about, relative to the tree and spelt with forward slashes.</param>
    /// <param name="cancellationToken">Stops the reads.</param>
    /// <exception cref="HarnessException">git could not read HEAD, or what a file holds.</exception>
    public static async Task<IReadOnlySet<string>> DifferingAsync(
        IGitClient git,
        IFileSystem fileSystem,
        string main,
        IReadOnlyCollection<string> paths,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(git);
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(paths);

        var asked = paths.Distinct(StringComparer.Ordinal).ToList();
        var differing = new HashSet<string>(StringComparer.Ordinal);

        if (asked.Count == 0)
        {
            return differing;
        }

        var head = await git.ResolveCommitAsync(main, "HEAD", cancellationToken).ConfigureAwait(false);
        var held = head is null ? null : await git.HeldAtAsync(main, head, asked, cancellationToken).ConfigureAwait(false);
        var files = asked.Where(path => fileSystem.KindOf(Path.Combine(main, path)) == PathKind.File).ToList();
        var stored = await git.ListStagedIdsAsync(main, files, cancellationToken).ConfigureAwait(false);
        var now = files.Select((path, at) => (path, at)).ToDictionary(file => file.path, file => stored[file.at], StringComparer.Ordinal);

        foreach (var path in asked)
        {
            var then = held?.GetValueOrDefault(path) ?? GitHeld.Nothing;

            if (now.TryGetValue(path, out var blob) ? !string.Equals(blob, then.Blob, StringComparison.Ordinal) : !then.IsNothing)
            {
                differing.Add(path);
            }
        }

        return differing;
    }

    /// <summary>Every path <paramref name="status"/> names - both of a rename's - each once: each change status says the tree holds, staged or not.</summary>
    /// <param name="status">The main tree's status, as <see cref="IGitClient.ReadStatusAsync"/> reads it.</param>
    public static IReadOnlySet<string> DifferingOf(IEnumerable<GitStatusEntry> status)
    {
        ArgumentNullException.ThrowIfNull(status);

        return GitName.PathsOf(status.SelectMany(entry => entry.Paths));
    }

    /// <summary>
    /// Every path a fold's plan writes in the main tree, each once, with what it does to it: written, merged - the arm
    /// registry among them - removed, and each registry file its rows change, a path named twice taking the first.
    /// </summary>
    private static IEnumerable<(string Path, string What)> PathsOf(AgentPlanned planned)
        => planned.Written.Select(path => (path, "written"))
            .Concat(planned.Merged.Concat(planned.ArmRegistry is { } arms ? [arms] : []).Select(path => (path, "merged")))
            .Concat(planned.Deleted.Select(path => (path, "deleted")))
            .Concat(planned.Registries.Select(path => (path, "registry")))
            .DistinctBy(change => change.path, StringComparer.Ordinal);

    /// <summary>The entry <paramref name="id"/>, which must stand <paramref name="state"/>.</summary>
    /// <exception cref="HarnessException">It does not: the engine's fault (<see cref="HarnessExit.InternalError"/>).</exception>
    private FlowFoldEntry Of(OrchestratorLayout layout, string id, string state)
        => store.ReadFolds(layout)?.Of(id) is { RecordsWrite: true } entry && entry.State == state
            ? entry
            : throw new HarnessException(HarnessExit.InternalError, $"the run's ledger of its folds holds no fold '{id}' standing {state} whose verb records its write");

    /// <summary>That <paramref name="tree"/> is still held: what a fold's write records, it records under the lock it was made under.</summary>
    /// <exception cref="InvalidOperationException">It was let go.</exception>
    private static void Held(MachineWideHold tree)
    {
        ArgumentNullException.ThrowIfNull(tree);

        if (tree.Released)
        {
            throw new InvalidOperationException("a fold's write is recorded under the main tree's lock it was made under, and the hold it was given was let go");
        }
    }

    /// <summary>Each of <paramref name="paths"/> as the fold found it: it wrote nothing there.</summary>
    private static IReadOnlyList<FlowFoldPath> Found(IReadOnlyList<FlowFoldPath> paths)
        => [.. paths.Select(path => Left(path, path.Before))];

    /// <summary>Each of <paramref name="paths"/> with what the main tree holds there now - its caller holding the tree's lock - and whether that is not what it held before.</summary>
    private async Task<IReadOnlyList<FlowFoldPath>> HeldAsync(string main, IReadOnlyList<FlowFoldPath> paths)
    {
        var held = new List<FlowFoldPath>();

        foreach (var path in paths)
        {
            held.Add(Left(path, await FileContentHash.OfFileAsync(fileSystem, Path.Combine(main, path.Path), CancellationToken.None).ConfigureAwait(false)));
        }

        return held;
    }

    /// <summary><paramref name="path"/> settled as holding <paramref name="digest"/>, and whether that is not what it held before.</summary>
    private static FlowFoldPath Left(FlowFoldPath path, string? digest)
        => path with { Left = digest, Changed = !string.Equals(digest, path.Before, StringComparison.Ordinal) };

    /// <summary>Writes <paramref name="entry"/> over the one of its id, inside the run's own section.</summary>
    private void Replace(OrchestratorLayout layout, FlowFoldEntry entry)
        => store.UpdateFolds(layout, ledger => ledger with
        {
            Folds = [.. ledger.Folds.Select(fold => string.Equals(fold.Id, entry.Id, StringComparison.Ordinal) ? entry : fold)],
        });
}
