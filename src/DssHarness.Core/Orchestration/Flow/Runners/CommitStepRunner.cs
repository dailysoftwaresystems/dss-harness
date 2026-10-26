using System.Text.Json.Nodes;
using DssHarness.Core.Configuration;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Git;
using DssHarness.Core.Output;
using DssHarness.Core.Platform;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;

namespace DssHarness.Core.Orchestration.Flow;

/// <summary>
/// A <c>commit</c> step (part 04, "Fold then commit"): commits the folds upstream of it in the flow - every visit and
/// iteration of them, never a sibling's - from the run's ledger of its folds (<see cref="FoldLedger.Owed"/>), and no
/// other work. With nothing upstream owed it is done, <c>committed</c> false, and runs no git at all. Otherwise it works
/// on the branch the run started on alone (<see cref="FlowRunPlace.OffItsBranch"/>) - the main checkout on another fails
/// it 13, nothing staged. A fold upstream whose intent nobody settled - a crash cut it off - leaves what it wrote untold,
/// and fails the step 13, nothing staged. It requires an empty index, and holds each path to what its fold left there:
/// one that holds anything else - edited and not committed, or its fold's change undone - fails the step 13 and nothing
/// is staged, for a commit of the rest would be half a fold. A path git ignores is marked so and left out, whatever it
/// holds, and answered under <c>ignored</c>; one HEAD holds as the tree does (<see cref="FoldLedger.DifferingAsync"/>) -
/// what its fold left, or a person's later change of it - was committed by hand, and is marked taken by HEAD. One left to
/// stage that the index marks assume-unchanged or skip-worktree, which <c>git add</c> would not stage, fails it 13. With
/// anything left, its message is filled (<see cref="CommitTemplate"/>) - none fails it 12 - and the rest is written to the
/// ledger as one staged set before the first <c>git add</c>, staged by explicit path, committed once (<c>-s</c> where
/// <c>commit.signOff</c>), and marked committed where the commit holds the set and nothing else; git refusing - a hook
/// among the reasons - unstages it and fails the step 20, and a commit that is not the set's alone - a hook staged more -
/// is left as it is for a person, the set left staged with it in the ledger, and fails the step 20. A staged set an attempt
/// before left is settled first: committed where HEAD moved and holds each of its paths as the set staged it, else
/// unstaged, its paths owed still. The folds upstream are those the engine read where the step started (<c>folds</c>).
/// </summary>
/// <param name="contextLoader">Finds the repository and its configuration.</param>
/// <param name="platform">This machine.</param>
/// <param name="store">Reads the run's ledger of its folds.</param>
/// <param name="ledger">Writes the commit's staged set, and what became of it.</param>
/// <param name="git">Reads the main tree's branch, status, index, HEAD, what it holds and what git ignores, and what a commit took.</param>
/// <param name="publisher">Stages, commits and unstages.</param>
/// <param name="fileSystem">Reads what the main tree holds at each path, and writes the message the commit is given.</param>
public sealed class CommitStepRunner(
    IHarnessContextLoader contextLoader,
    IHostPlatform platform,
    OrchestrationStore store,
    FoldLedger ledger,
    IGitClient git,
    IGitPublisher publisher,
    IFileSystem fileSystem) : IFlowStepTypeRunner
{
    /// <inheritdoc/>
    public string Type => "commit";

    /// <inheritdoc/>
    public async Task<FlowStepResult> RunAsync(FlowStepRun run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);

        var (place, context) = await FlowStepRunners.PlaceOf(run, contextLoader, platform, cancellationToken).ConfigureAwait(false);
        var layout = OrchestratorLayout.Of(context.Layout, place.Run);
        var main = place.Repository;
        var upstream = FlowStepKeys.ReadAtStart(run, "folds");
        var owed = FoldLedger.Owed(store.ReadFolds(layout), upstream);

        // Nothing owed and no intent untold: there is nothing to commit, nor a set an attempt before left staged, for a set
        // stages paths owed until a commit takes them.
        if (owed.Paths.Count == 0 && owed.Unsettled.Count == 0)
        {
            return MadeBefore(layout, run, upstream);
        }

        // The run's work is committed on the branch it started on, or not at all.
        if (place.OffItsBranch(run.Instance, await git.GetBranchAsync(main, cancellationToken).ConfigureAwait(false), "staged") is { } off)
        {
            return FlowStepResult.Ended(passed: false, HarnessExit.Refused, off);
        }

        if (await SettleAsync(run, layout, main, upstream, cancellationToken).ConfigureAwait(false) is { } settled)
        {
            return settled;
        }

        if (owed.Unsettled.Count > 0)
        {
            return FlowStepResult.Ended(
                passed: false,
                HarnessExit.Refused,
                $"'{run.Instance}' commits the folds upstream of it, and {string.Join(", ", owed.Unsettled.Select(fold => $"'{fold.Instance}'"))} left a fold's intent nobody settled, so what it wrote cannot be told: run that step again, then resume. Nothing was staged.");
        }

        // The index is the person's until the commit stages: anything in it would be swept into the commit.
        var status = await git.ReadStatusAsync(main, cancellationToken).ConfigureAwait(false);

        if (status.Where(entry => entry.IsStaged).ToList() is { Count: > 0 } held)
        {
            return FlowStepResult.Ended(
                passed: false,
                HarnessExit.Refused,
                $"'{run.Instance}' commits only what the run's folds wrote, and the index holds {ReportText.Listed([.. held.Select(entry => entry.Line)])}: commit or unstage it, then resume. Nothing was staged.");
        }

        // A path git ignores no commit takes, whatever it holds: it is marked so, and left out - asked again each time, as
        // the rules may have changed.
        var asked = owed.Paths.Select(path => path.Path).ToList();
        var ignored = await git.FindIgnoredAsync(main, asked, cancellationToken).ConfigureAwait(false);
        ledger.Ignored(layout, asked, ignored);

        var weighed = owed.Paths.Where(path => !ignored.Contains(path.Path)).ToList();
        var head = await git.ResolveCommitAsync(main, "HEAD", cancellationToken).ConfigureAwait(false);
        var differing = head is null ? null : await FoldLedger.DifferingAsync(git, main, [.. weighed.Select(path => path.Path)], cancellationToken).ConfigureAwait(false);
        var staging = new List<(FoldOwedPath Path, string? Holds)>();
        var byHand = new List<(FoldOwedPath Path, string? Holds)>();
        var changed = new List<FoldOwedPath>();

        foreach (var path in weighed)
        {
            var holds = await FileContentHash.OfFileAsync(fileSystem, Path.Combine(main, path.Path), cancellationToken).ConfigureAwait(false);
            var clean = differing?.Contains(path.Path) == false;

            // What its fold left, still to commit or committed by hand; else, held as HEAD holds it and not what the folds
            // found there, a commit made by hand of the path as a person changed it after; else changed since - edited and
            // not committed, or the fold's change undone - which a commit of the rest would leave half a fold.
            if (string.Equals(holds, path.Digest, StringComparison.Ordinal) || (clean && !string.Equals(holds, path.Before, StringComparison.Ordinal)))
            {
                (clean ? byHand : staging).Add((path, holds));
            }
            else
            {
                changed.Add(path);
            }
        }

        if (changed.Count > 0)
        {
            return FlowStepResult.Ended(
                passed: false,
                HarnessExit.Refused,
                $"{ReportText.Listed([.. changed.Select(path => $"'{path.Path}' (folded by '{path.LeftBy}')")])} {(changed.Count == 1 ? "has" : "have")} changed since; "
                + $"commit or restore {(changed.Count == 1 ? "it" : "them")}, then resume. Nothing was staged.");
        }

        // git add stages nothing of a path the index marks assume-unchanged, and refuses one it marks skip-worktree: a
        // commit of the rest would be half a fold.
        if (await MarkedAsync(main, [.. staging.Select(path => path.Path.Path)], cancellationToken).ConfigureAwait(false) is { Count: > 0 } marked)
        {
            return FlowStepResult.Ended(
                passed: false,
                HarnessExit.Refused,
                $"'{run.Instance}' commits what its folds left at {ReportText.Listed(marked)}, which git add would not stage: clear each mark with git update-index "
                + "--no-assume-unchanged -- <path>, or --no-skip-worktree, then resume. Nothing was staged.");
        }

        if (byHand.Count > 0)
        {
            ledger.Committed(layout, ledger.Staging(layout, run.Instance, run.Attempt, head, CommitPathsOf(byHand), FoldsOf(byHand)), head!);
        }

        if (staging.Count == 0)
        {
            return MadeBefore(layout, run, upstream);
        }

        // The message is filled only where there is something to commit, and before anything is staged.
        if (CommitTemplate.Message(context.Config.Commit, run.Step.Message, run.Step.Variables, out var unwritten) is not { } message)
        {
            return FlowStepResult.Ended(passed: false, HarnessExit.ConfigInvalid, $"'{run.Instance}' {unwritten}. Nothing was staged.");
        }

        var messageFile = layout.InstanceCommitMessageFile(run.Instance);
        fileSystem.CreateDirectory(Path.GetDirectoryName(messageFile)!);
        fileSystem.WriteAllTextAtomic(messageFile, message);

        var id = ledger.Staging(layout, run.Instance, run.Attempt, head, CommitPathsOf(staging), FoldsOf(staging));
        var paths = staging.Select(path => path.Path.Path).ToList();
        var limit = WorkflowStepTypes.CommandLimitOf(run.Step);
        string sha;

        // A command that did not finish in time (21) leaves the set staged as it stands, for the next attempt to settle.
        try
        {
            await publisher.StageAsync(main, [.. staging.Where(path => path.Holds is not null).Select(path => path.Path.Path)], [.. staging.Where(path => path.Holds is null).Select(path => path.Path.Path)], limit, cancellationToken).ConfigureAwait(false);
            sha = await publisher.CommitAsync(main, messageFile, context.Config.Commit.SignOff, limit, cancellationToken).ConfigureAwait(false);
        }
        catch (HarnessException refused) when (refused.ExitCode == HarnessExit.CommandFailed)
        {
            // git said no - a hook among the reasons: what was staged goes back out of the index, owed still, and the step
            // fails with what git said. Where git will not unstage it either, the set stays staged for the next attempt to
            // settle, as a cancelled attempt's does.
            try
            {
                await publisher.UnstageAsync(main, paths, limit, CancellationToken.None).ConfigureAwait(false);
            }
            catch (HarnessException unstaging) when (unstaging.ExitCode is HarnessExit.CommandFailed or HarnessExit.Incomplete)
            {
                return FlowStepResult.Ended(
                    passed: false,
                    HarnessExit.CommandFailed,
                    $"'{run.Instance}': {refused.Message}; and what it staged could not be unstaged ({unstaging.Message}): fix what git reported, then resume, which settles it.");
            }

            ledger.Unstaged(layout, id);
            return FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, $"'{run.Instance}': {refused.Message}; what it staged was unstaged: fix what git reported, then resume.");
        }

        // The commit is held to the set: a hook may stage more into it, and git may leave a path of it out. One that is not
        // the set's alone is never undone - history is a person's to rewrite - and the set stays staged beside it, for the
        // next attempt to settle from what the person made of it.
        var took = await TookAsync(main, sha, cancellationToken).ConfigureAwait(false);
        var besides = took.Where(path => !paths.Contains(path, StringComparer.Ordinal)).ToList();
        var without = paths.Where(path => !took.Contains(path, StringComparer.Ordinal)).ToList();

        if (besides.Count > 0 || without.Count > 0)
        {
            ledger.Strayed(layout, id, new FlowCommitStray(sha, besides, without));

            return FlowStepResult.Ended(
                passed: false,
                HarnessExit.CommandFailed,
                $"'{run.Instance}' made {ReportText.Commit(sha)}, and it is not what its folds left alone: "
                + string.Join(", and ", [.. besides.Count > 0 ? [$"it took {Quoted(besides)} besides"] : Array.Empty<string>(), .. without.Count > 0 ? [$"it left {Quoted(without)} out"] : Array.Empty<string>()])
                + " - a hook, or git, made it so. The commit is left as it is: keep it, or undo it with git reset --soft HEAD^, then resume, which settles it.");
        }

        ledger.Committed(layout, id, sha);
        return Done(committed: true, sha, paths, FoldLedger.IgnoredOf(store.ReadFolds(layout), upstream));
    }

    /// <summary>
    /// Settles each staged set an attempt of this step left - its commit made or not, a crash between telling which: where
    /// HEAD moved, the tree holds at each of its paths what the set staged there, and HEAD holds it so
    /// (<see cref="FoldLedger.DifferingAsync"/>), the commit took them, and the step is done with it; else no commit did -
    /// a HEAD another commit moved, its paths put back, reads clean in git's status all the same - so what the index holds
    /// of it is unstaged and the set marked so, owed still, for this attempt to stage again whole.
    /// </summary>
    private async Task<FlowStepResult?> SettleAsync(FlowStepRun run, OrchestratorLayout layout, string main, IReadOnlyList<string> upstream, CancellationToken cancellationToken)
    {
        foreach (var left in store.ReadFolds(layout)?.Commits.Where(commit => commit.State == FlowCommitEntry.Staged && commit.Instance == run.Instance).ToList() ?? [])
        {
            var head = await git.ResolveCommitAsync(main, "HEAD", cancellationToken).ConfigureAwait(false);
            var paths = left.Paths.Select(path => path.Path).ToList();

            if (head is not null
                && !string.Equals(head, left.Head, StringComparison.Ordinal)
                && await AsStagedAsync(main, left.Paths, cancellationToken).ConfigureAwait(false)
                && (await FoldLedger.DifferingAsync(git, main, paths, cancellationToken).ConfigureAwait(false)).Count == 0)
            {
                ledger.Committed(layout, left.Id, head);
                return Done(committed: true, head, paths, FoldLedger.IgnoredOf(store.ReadFolds(layout), upstream));
            }

            var status = await git.ReadStatusAsync(main, cancellationToken).ConfigureAwait(false);

            if (FoldLedger.DifferingOf(status.Where(entry => entry.IsStaged)).Intersect(paths, StringComparer.Ordinal).ToList() is { Count: > 0 } held)
            {
                await publisher.UnstageAsync(main, held, WorkflowStepTypes.CommandLimitOf(run.Step), cancellationToken).ConfigureAwait(false);
            }

            ledger.Unstaged(layout, left.Id);
        }

        return null;
    }

    /// <summary>Whether the main tree holds at each of <paramref name="paths"/> what a staged set staged there: its digest, or no file for a deletion.</summary>
    private async Task<bool> AsStagedAsync(string main, IReadOnlyList<FlowCommitPath> paths, CancellationToken cancellationToken)
    {
        foreach (var path in paths)
        {
            if (!string.Equals(await FileContentHash.OfFileAsync(fileSystem, Path.Combine(main, path.Path), cancellationToken).ConfigureAwait(false), path.Digest, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Each of <paramref name="paths"/> the index marks assume-unchanged or skip-worktree, quoted with its mark: git add
    /// stages nothing of the one, and refuses the other.
    /// </summary>
    private async Task<IReadOnlyList<string>> MarkedAsync(string main, IReadOnlyList<string> paths, CancellationToken cancellationToken)
    {
        if (paths.Count == 0)
        {
            return [];
        }

        return
        [
            .. (await git.ListIndexAsync(main, paths, cancellationToken).ConfigureAwait(false))
                .Where(entry => entry.IsAssumedUnchanged || entry.IsSkipWorktree)
                .DistinctBy(entry => entry.Name.Quoted, StringComparer.Ordinal)
                .Select(entry => $"'{entry.Path}' ({string.Join(", ", [.. entry.IsAssumedUnchanged ? ["assume-unchanged"] : Array.Empty<string>(), .. entry.IsSkipWorktree ? ["skip-worktree"] : Array.Empty<string>()])})"),
        ];
    }

    /// <summary>
    /// The paths the commit <paramref name="sha"/> took: each it changes against its parent, or each it holds where it has
    /// none - a name that is not UTF-8 as git quotes it.
    /// </summary>
    private async Task<IReadOnlyList<string>> TookAsync(string main, string sha, CancellationToken cancellationToken)
    {
        var parent = await git.ResolveCommitAsync(main, $"{sha}^", cancellationToken).ConfigureAwait(false);
        var names = parent is null
            ? await git.ListFilesAtCommitAsync(main, sha, cancellationToken).ConfigureAwait(false)
            : await git.ListNamesChangedBetweenAsync(main, parent, sha, cancellationToken).ConfigureAwait(false);

        return [.. names.Select(name => name.IsUtf8 ? name.Text : name.Quoted).Distinct(StringComparer.Ordinal)];
    }

    /// <summary>
    /// The step done with nothing left to commit: with the commit an attempt of it made before - its ending cut off before
    /// it was recorded - where one did, else having committed nothing. A set it marked taken by HEAD, which it found there,
    /// is no commit of its own.
    /// </summary>
    private FlowStepResult MadeBefore(OrchestratorLayout layout, FlowStepRun run, IReadOnlyList<string> upstream)
    {
        var file = store.ReadFolds(layout);
        var ignored = FoldLedger.IgnoredOf(file, upstream);

        return file?.Commits.LastOrDefault(commit => commit.Instance == run.Instance && commit.State == FlowCommitEntry.Committed && commit.Sha != commit.Head) is { } made
            ? Done(committed: true, made.Sha, [.. made.Paths.Select(path => path.Path)], ignored)
            : Done(committed: false, sha: null, [], ignored);
    }

    /// <summary>The paths of a staged set, each with what the main tree holds there.</summary>
    private static IReadOnlyList<FlowCommitPath> CommitPathsOf(IEnumerable<(FoldOwedPath Path, string? Holds)> paths)
        => [.. paths.Select(path => new FlowCommitPath(path.Path.Path, path.Holds))];

    /// <summary>Every fold the paths settle, each once.</summary>
    private static IReadOnlyList<string> FoldsOf(IEnumerable<(FoldOwedPath Path, string? Holds)> paths)
        => [.. paths.SelectMany(path => path.Path.Folds).Distinct(StringComparer.Ordinal)];

    /// <summary>Paths as a refusal names them: each in single quotes, as many as a line names.</summary>
    private static string Quoted(IReadOnlyList<string> paths) => ReportText.Listed([.. paths.Select(path => $"'{path}'")]);

    /// <summary>The step done: whether it committed, the commit, the paths it took, and those its folds changed that git ignores.</summary>
    private static FlowStepResult Done(bool committed, string? sha, IReadOnlyList<string> paths, IReadOnlyList<string> ignored)
        => FlowStepResult.Ended(
            passed: true,
            HarnessExit.Success,
            output: new JsonObject
            {
                ["committed"] = committed,
                ["sha"] = sha,
                ["paths"] = new JsonArray([.. paths.Select(path => (JsonNode?)path)]),
                ["ignored"] = new JsonArray([.. ignored.Select(path => (JsonNode?)path)]),
            });
}
