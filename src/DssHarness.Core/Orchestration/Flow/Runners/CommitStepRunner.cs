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
/// other work. A fold upstream whose intent nobody settled - a crash cut it off - leaves what it wrote untold, and fails
/// the step 13, nothing staged. With nothing upstream owed it is done, <c>committed</c> false, and runs no git at all.
/// Otherwise it
/// requires an empty index, and holds each path to what its fold left there:
/// one that holds anything else - edited and not committed, or its fold's change undone - fails the step 13 and nothing
/// is staged, for a commit of the rest would be half a fold. A path git ignores is marked so and left out, whatever it
/// holds; one that holds as HEAD does - what its fold left, or a person's later change of it - was committed by hand,
/// and is marked taken by HEAD. With anything left, its message is filled (<see cref="CommitTemplate"/>) - none fails it
/// 12 - and the rest is written to the ledger as one staged set before the first
/// <c>git add</c>, staged by explicit path, committed once (<c>-s</c> where <c>commit.signOff</c>), and marked committed;
/// git refusing - a hook among the reasons - unstages it and fails the step 20. A staged set an attempt before left is
/// settled first: committed where HEAD moved and holds it, else unstaged, its paths owed still. The folds upstream are
/// those the engine read where the step started (<c>folds</c>).
/// </summary>
/// <param name="contextLoader">Finds the repository and its configuration.</param>
/// <param name="platform">This machine.</param>
/// <param name="store">Reads the run's ledger of its folds.</param>
/// <param name="ledger">Writes the commit's staged set, and what became of it.</param>
/// <param name="git">Reads the main tree's status, HEAD and what git ignores.</param>
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

        if (await SettleAsync(run, layout, main, cancellationToken).ConfigureAwait(false) is { } settled)
        {
            return settled;
        }

        var owed = FoldLedger.Owed(store.ReadFolds(layout), FlowStepKeys.ReadAtStart(run, "folds"));

        if (owed.Unsettled.Count > 0)
        {
            return FlowStepResult.Ended(
                passed: false,
                HarnessExit.Refused,
                $"'{run.Instance}' commits the folds upstream of it, and {string.Join(", ", owed.Unsettled.Select(fold => $"'{fold.Instance}'"))} left a fold's intent nobody settled, so what it wrote cannot be told: run that step again, then resume. Nothing was staged.");
        }

        if (owed.Paths.Count == 0)
        {
            return MadeBefore(layout, run);
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

        var differing = FoldLedger.DifferingOf(status);
        var head = await git.ResolveCommitAsync(main, "HEAD", cancellationToken).ConfigureAwait(false);
        var staging = new List<(FoldOwedPath Path, string? Holds)>();
        var byHand = new List<(FoldOwedPath Path, string? Holds)>();
        var changed = new List<FoldOwedPath>();

        foreach (var path in owed.Paths.Where(path => !ignored.Contains(path.Path)))
        {
            var holds = await FileContentHash.OfFileAsync(fileSystem, Path.Combine(main, path.Path), cancellationToken).ConfigureAwait(false);
            var clean = head is not null && !differing.Contains(path.Path);

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

        if (byHand.Count > 0)
        {
            ledger.Committed(layout, ledger.Staging(layout, run.Instance, run.Attempt, head, CommitPathsOf(byHand), FoldsOf(byHand)), head!);
        }

        if (staging.Count == 0)
        {
            return MadeBefore(layout, run);
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
        string sha;

        try
        {
            await publisher.StageAsync(main, [.. staging.Where(path => path.Holds is not null).Select(path => path.Path.Path)], [.. staging.Where(path => path.Holds is null).Select(path => path.Path.Path)], cancellationToken).ConfigureAwait(false);
            sha = await publisher.CommitAsync(main, messageFile, context.Config.Commit.SignOff, cancellationToken).ConfigureAwait(false);
        }
        catch (HarnessException refused) when (refused.ExitCode == HarnessExit.CommandFailed)
        {
            // git said no - a hook among the reasons: what was staged goes back out of the index, owed still, and the step
            // fails with what git said. Where git will not unstage it either, the set stays staged for the next attempt to
            // settle, as a cancelled attempt's does.
            try
            {
                await publisher.UnstageAsync(main, paths, CancellationToken.None).ConfigureAwait(false);
            }
            catch (HarnessException unstaging) when (unstaging.ExitCode == HarnessExit.CommandFailed)
            {
                return FlowStepResult.Ended(
                    passed: false,
                    HarnessExit.CommandFailed,
                    $"'{run.Instance}': {refused.Message}; and what it staged could not be unstaged ({unstaging.Message}): fix what git reported, then resume, which settles it.");
            }

            ledger.Unstaged(layout, id);
            return FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, $"'{run.Instance}': {refused.Message}; what it staged was unstaged: fix what git reported, then resume.");
        }

        ledger.Committed(layout, id, sha);
        return Done(committed: true, sha, paths);
    }

    /// <summary>
    /// Settles each staged set an attempt of this step left - its commit made or not, a crash between telling which: where
    /// HEAD moved and holds each of its paths as the tree does, the commit took them, and the step is done with it; else
    /// no commit did, so what the index holds of it is unstaged and the set marked so, owed still, for this attempt to
    /// stage again whole.
    /// </summary>
    private async Task<FlowStepResult?> SettleAsync(FlowStepRun run, OrchestratorLayout layout, string main, CancellationToken cancellationToken)
    {
        foreach (var left in store.ReadFolds(layout)?.Commits.Where(commit => commit.State == FlowCommitEntry.Staged && commit.Instance == run.Instance).ToList() ?? [])
        {
            var head = await git.ResolveCommitAsync(main, "HEAD", cancellationToken).ConfigureAwait(false);
            var status = await git.ReadStatusAsync(main, cancellationToken).ConfigureAwait(false);
            var paths = left.Paths.Select(path => path.Path).ToList();

            if (head is not null && !string.Equals(head, left.Head, StringComparison.Ordinal) && !paths.Any(FoldLedger.DifferingOf(status).Contains))
            {
                ledger.Committed(layout, left.Id, head);
                return Done(committed: true, head, paths);
            }

            if (FoldLedger.DifferingOf(status.Where(entry => entry.IsStaged)).Intersect(paths, StringComparer.Ordinal).ToList() is { Count: > 0 } held)
            {
                await publisher.UnstageAsync(main, held, cancellationToken).ConfigureAwait(false);
            }

            ledger.Unstaged(layout, left.Id);
        }

        return null;
    }

    /// <summary>
    /// The step done with nothing left to commit: with the commit an attempt of it made before - its ending cut off before
    /// it was recorded - where one did, else having committed nothing. A set it marked taken by HEAD, which it found there,
    /// is no commit of its own.
    /// </summary>
    private FlowStepResult MadeBefore(OrchestratorLayout layout, FlowStepRun run)
        => store.ReadFolds(layout)?.Commits.LastOrDefault(commit => commit.Instance == run.Instance && commit.State == FlowCommitEntry.Committed && commit.Sha != commit.Head) is { } made
            ? Done(committed: true, made.Sha, [.. made.Paths.Select(path => path.Path)])
            : Done(committed: false, sha: null, []);

    /// <summary>The paths of a staged set, each with what the main tree holds there.</summary>
    private static IReadOnlyList<FlowCommitPath> CommitPathsOf(IEnumerable<(FoldOwedPath Path, string? Holds)> paths)
        => [.. paths.Select(path => new FlowCommitPath(path.Path.Path, path.Holds))];

    /// <summary>Every fold the paths settle, each once.</summary>
    private static IReadOnlyList<string> FoldsOf(IEnumerable<(FoldOwedPath Path, string? Holds)> paths)
        => [.. paths.SelectMany(path => path.Path.Folds).Distinct(StringComparer.Ordinal)];

    /// <summary>The step done: whether it committed, the commit, and the paths it took.</summary>
    private static FlowStepResult Done(bool committed, string? sha, IReadOnlyList<string> paths)
        => FlowStepResult.Ended(
            passed: true,
            HarnessExit.Success,
            output: new JsonObject
            {
                ["committed"] = committed,
                ["sha"] = sha,
                ["paths"] = new JsonArray([.. paths.Select(path => (JsonNode?)path)]),
            });
}
