using System.ComponentModel;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Git;
using RepoHarness.Core.Hosts;
using RepoHarness.Core.Processes;
using RepoHarness.Core.Results;
using RepoHarness.Core.Worktrees;

namespace RepoHarness.Tests;

/// <summary>
/// How delete-worktree removes a worktree once it has decided to: through a linked worktrees
/// directory, and when a delete, an interruption or git's record-keeping goes wrong part way.
/// </summary>
public sealed class WorktreeRemovalTests
{
    private static readonly WorktreeSettings Relaxed = new() { PathBudgetReserve = 5, PathBudgetMargin = 2 };

    /// <summary>ERROR_SHARING_VIOLATION, which Windows answers an entry another program holds with.</summary>
    private const int SharingViolation = 32;

    /// <summary>What a double says Windows said of each entry it holds.</summary>
    private const string HeldReason = "The process cannot access the file because it is being used by another process.";

    /// <summary>What a double says stopped it looking through a worktree.</summary>
    private const string UnreadableReason = "Access to the path 'locked' is denied.";

    [Fact]
    public async Task AWorktreeReachedThroughALinkedWorktreesDirectory_IsDeletedWithoutForce()
    {
        // git reports the worktree's paths with the link resolved, so a check comparing them with
        // the path the harness spelled would refuse every clean worktree here.
        using var temp = new TempDirectory();
        using var elsewhere = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        var worktrees = Path.GetDirectoryName(HarnessFactory.WorktreePath(temp.Path, "linked"))!;
        harness.FileSystem.DeleteDirectory(worktrees);
        TestLinks.DirectoryLink(worktrees, elsewhere.Path);

        var created = await harness.WorktreeService.CreateAsync(temp.Path, "linked", useRandomName: false, cancellationToken);
        Assert.True(created.Succeeded, created.Outcome.Message);

        var outcome = await harness.WorktreeService.DeleteAsync(temp.Path, "linked", force: false, deleteEvidence: false, cancellationToken: cancellationToken);

        Assert.True(outcome.Succeeded, outcome.Outcome.Message);
        Assert.False(Directory.Exists(Path.Combine(elsewhere.Path, "linked")));
        Assert.Single(await harness.GitClient.ListWorktreesAsync(temp.Path, cancellationToken));
    }

    [Fact]
    public async Task AFallbackDeleteThatCannotFinish_FailsWithWhatToDoNext()
    {
        // A file another program holds open is no defect in the tool, and part of the worktree may
        // already be gone, which the message has to say.
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        var path = await CreateAsync(harness, temp, "held");

        // Without its .git file git cannot remove the worktree, which leaves the directory to the fallback.
        File.Delete(Path.Combine(path, ".git"));
        var service = Service(harness, harness.GitClient, new UndeletableFileSystem(harness.FileSystem));

        var outcome = await service.DeleteAsync(temp.Path, "held", force: true, deleteEvidence: false, cancellationToken: cancellationToken);

        Assert.Equal(HarnessExit.CommandFailed, outcome.Outcome.ExitCode);
        Assert.StartsWith("Could not finish deleting '", outcome.Outcome.Message, StringComparison.Ordinal);
        Assert.Contains("; part of it may already be gone: ", outcome.Outcome.Message, StringComparison.Ordinal);
        Assert.Contains("delete-worktree held --force", outcome.Outcome.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnInterruptionAfterTheChecks_DeletesNothing()
    {
        // Every check completes, and the interruption arrives just after them: removal must not start.
        using var temp = new TempDirectory();
        using var interruption = new CancellationTokenSource();
        var harness = await PrepareAsync(temp);
        var path = await CreateAsync(harness, temp, "stopped");
        var git = new InterceptingGitClient(harness.GitClient) { IgnoreCancellation = true, BeforeEveryCall = interruption.Cancel };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Service(harness, git).DeleteAsync(temp.Path, "stopped", force: false, deleteEvidence: false, cancellationToken: interruption.Token));

        Assert.True(Directory.Exists(path));
        Assert.DoesNotContain(git.Runs, run => run.Arguments is ["worktree", "remove", ..]);
        Assert.Equal(2, (await harness.GitClient.ListWorktreesAsync(temp.Path, TestContext.Current.CancellationToken)).Count);
    }

    [Fact]
    public async Task AnInterruptionDuringRemoval_DoesNotStopIt()
    {
        // git stopped halfway would leave the files and git's record partly gone.
        using var temp = new TempDirectory();
        using var interruption = new CancellationTokenSource();
        var harness = await PrepareAsync(temp);
        var path = await CreateAsync(harness, temp, "finished");
        var git = new InterceptingGitClient(harness.GitClient)
        {
            BeforeRun = arguments =>
            {
                if (arguments is ["worktree", "remove", ..])
                {
                    interruption.Cancel();
                }
            },
        };

        var outcome = await Service(harness, git).DeleteAsync(temp.Path, "finished", force: false, deleteEvidence: false, cancellationToken: interruption.Token);

        Assert.True(outcome.Succeeded, outcome.Outcome.Message);
        Assert.False(Directory.Exists(path));
        Assert.Single(git.Runs, run => run.Arguments is ["worktree", "remove", ..]);
    }

    [Fact]
    public async Task UncommittedChangesBeingDiscarded_AreRemovedPastGitsOwnCheck_ForcedOnce()
    {
        // git's check would refuse the very changes being discarded. Forced once, git still keeps a lock.
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        var path = await CreateAsync(harness, temp, "discarded");
        File.WriteAllText(Path.Combine(path, "notes.txt"), "never committed");
        var git = new InterceptingGitClient(harness.GitClient);

        var outcome = await Service(harness, git).DeleteAsync(
            temp.Path, "discarded", force: false, deleteEvidence: false, discardUncommitted: true, cancellationToken: cancellationToken);

        Assert.True(outcome.Succeeded, outcome.Outcome.Message);
        Assert.False(Directory.Exists(path));
        var removal = Assert.Single(git.Runs, run => run.Arguments is ["worktree", "remove", ..]);
        Assert.True(removal.Arguments is ["worktree", "remove", "--force", var removed] && PathAssert.AreSame(path, removed), string.Join(' ', removal.Arguments));
    }

    [Fact]
    public async Task NothingToDiscard_LeavesGitsOwnCheckInPlace()
    {
        // Asked to discard what a clean worktree does not hold, the removal is the plain one, so git
        // still catches a file changed since the check.
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        var path = await CreateAsync(harness, temp, "clean");
        var git = new InterceptingGitClient(harness.GitClient);

        var outcome = await Service(harness, git).DeleteAsync(
            temp.Path, "clean", force: false, deleteEvidence: false, discardUncommitted: true, cancellationToken: cancellationToken);

        Assert.True(outcome.Succeeded, outcome.Outcome.Message);
        var removal = Assert.Single(git.Runs, run => run.Arguments is ["worktree", "remove", ..]);
        Assert.True(removal.Arguments is ["worktree", "remove", var removed] && PathAssert.AreSame(path, removed), string.Join(' ', removal.Arguments));
    }

    [Fact]
    public async Task AFailureReportedWhileClearingTheRecord_IsCheckedAgainstTheRecordItself()
    {
        // With the directory gone, git clears the record; what decides the outcome is whether the
        // record is gone, not what git's exit code said.
        using var temp = new TempDirectory();
        var harness = await PrepareAsync(temp);
        var path = await CreateAsync(harness, temp, "cleared");
        File.Delete(Path.Combine(path, ".git"));
        var removals = 0;
        var git = new InterceptingGitClient(harness.GitClient)
        {
            AfterRun = (arguments, result) => arguments is ["worktree", "remove", ..] && ++removals == 2
                ? new GitCommandResult(1, string.Empty, "error: reported failure after clearing the record")
                : result,
        };

        var outcome = await Service(harness, git).DeleteAsync(temp.Path, "cleared", force: true, deleteEvidence: false, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(outcome.Succeeded, outcome.Outcome.Message);
        Assert.Equal(2, removals);
        Assert.False(Directory.Exists(path));
    }

    [Fact]
    public async Task ARecordThatSurvivesTheDeletion_IsReported()
    {
        using var temp = new TempDirectory();
        var harness = await PrepareAsync(temp);
        var path = await CreateAsync(harness, temp, "kept");
        File.Delete(Path.Combine(path, ".git"));
        var removals = 0;
        var git = new InterceptingGitClient(harness.GitClient)
        {
            InsteadOfRun = arguments => arguments is ["worktree", "remove", ..] && ++removals == 2
                ? new GitCommandResult(1, string.Empty, "error: could not clear the record")
                : null,
        };

        var outcome = await Service(harness, git).DeleteAsync(temp.Path, "kept", force: true, deleteEvidence: false, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.CommandFailed, outcome.Outcome.ExitCode);
        Assert.Contains("git still has it registered", outcome.Outcome.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(path));
    }

    [Fact]
    public async Task ARecordLeftThroughALinkedWorktreesDirectory_IsCleared_SoTheNameCanBeUsedAgain()
    {
        // git lists the record by the link's target; matched against the path as spelled, it
        // would never be found, and the name would stay unusable.
        using var temp = new TempDirectory();
        using var elsewhere = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        await LinkWorktreesDirectoryAsync(harness, temp, elsewhere);
        var path = await CreateAsync(harness, temp, "linked");
        harness.FileSystem.DeleteDirectory(path);

        var outcome = await harness.WorktreeService.DeleteAsync(temp.Path, "linked", force: false, deleteEvidence: false, cancellationToken: cancellationToken);

        Assert.True(outcome.Succeeded, outcome.Outcome.Message);
        Assert.Single(await harness.GitClient.ListWorktreesAsync(temp.Path, cancellationToken));

        var recreated = await harness.WorktreeService.CreateAsync(temp.Path, "linked", useRandomName: false, cancellationToken);
        Assert.True(recreated.Succeeded, recreated.Outcome.Message);
    }

    [Fact]
    public async Task AForcedDeleteThroughALinkedWorktreesDirectory_ReportsARecordThatSurvives_WithGitsReason()
    {
        // With the .git file gone, git cannot name the record's directory, so the record is looked
        // for in git's list, which names it by the link's target.
        using var temp = new TempDirectory();
        using var elsewhere = new TempDirectory();
        var harness = await PrepareAsync(temp);
        await LinkWorktreesDirectoryAsync(harness, temp, elsewhere);
        var path = await CreateAsync(harness, temp, "linked");
        File.Delete(Path.Combine(path, ".git"));
        var removals = 0;
        var git = new InterceptingGitClient(harness.GitClient)
        {
            InsteadOfRun = arguments => arguments is ["worktree", "remove", ..] && ++removals == 2
                ? new GitCommandResult(1, string.Empty, "error: could not clear the record")
                : null,
        };

        var outcome = await Service(harness, git).DeleteAsync(temp.Path, "linked", force: true, deleteEvidence: false, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.CommandFailed, outcome.Outcome.ExitCode);
        Assert.Contains("git still has it registered", outcome.Outcome.Message, StringComparison.Ordinal);
        Assert.Contains("error: could not clear the record", outcome.Outcome.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACheckedRemovalGitAbandonsPartWay_SaysWhatIsGone_AndForceFinishesIt()
    {
        // git deletes the .git file and its record before failing on a file it cannot delete, and
        // after that no check can run again.
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        var path = await CreateAsync(harness, temp, "partial");
        var gitDirectory = await GitDirectoryAsync(harness, path);
        var git = new InterceptingGitClient(harness.GitClient)
        {
            InsteadOfRun = arguments =>
            {
                if (arguments is not ["worktree", "remove", ..])
                {
                    return null;
                }

                File.Delete(Path.Combine(path, ".git"));
                harness.FileSystem.DeleteDirectory(gitDirectory);
                return new GitCommandResult(255, string.Empty, "error: failed to delete 'build.log': Permission denied");
            },
        };

        var failed = await Service(harness, git).DeleteAsync(temp.Path, "partial", force: false, deleteEvidence: false, cancellationToken: cancellationToken);

        Assert.Equal(HarnessExit.CommandFailed, failed.Outcome.ExitCode);
        Assert.StartsWith("git could not remove worktree 'partial': error: failed to delete 'build.log'", failed.Outcome.Message, StringComparison.Ordinal);
        Assert.Contains("its .git file and git's record of it are already gone", failed.Outcome.Message, StringComparison.Ordinal);

        // Here the checks did run, so finishing with --force is safe, and says so.
        Assert.Contains("Every check passed before removal began", failed.Outcome.Message, StringComparison.Ordinal);
        Assert.Contains("delete-worktree partial --force", failed.Outcome.Message, StringComparison.Ordinal);

        var forced = await harness.WorktreeService.DeleteAsync(temp.Path, "partial", force: true, deleteEvidence: false, cancellationToken: cancellationToken);

        Assert.True(forced.Succeeded, forced.Outcome.Message);
        Assert.False(Directory.Exists(path));
    }

    /// <summary>
    /// On Windows, a worktree holding directory junctions is deleted whole, and nothing a junction leads to goes with
    /// it. git for Windows leaves every junction, and every directory above one, while reporting the worktree removed
    /// with its .git file and its record already gone: a junction in an ignored directory and one at the top, leading
    /// out of the worktree, both used to leave it part way.
    /// </summary>
    [Fact]
    public async Task AWorktreeHoldingJunctions_IsDeletedWhole_AndNothingTheyLeadToGoes()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows has directory junctions, which git leaves.");

        using var temp = new TempDirectory();
        using var outside = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        await IgnoreAsync(harness, temp, "build/");
        var path = await CreateAsync(harness, temp, "joined");
        var canary = outside.WriteFile("canary.txt", "kept");
        Directory.CreateDirectory(Path.Combine(path, "build"));
        TestLinks.DirectoryLink(Path.Combine(path, "build", "out"), outside.Path);
        TestLinks.DirectoryLink(Path.Combine(path, "linkdir"), outside.Path);

        var outcome = await harness.WorktreeService.DeleteAsync(
            temp.Path, "joined", force: false, deleteEvidence: false, discardUncommitted: true, cancellationToken: cancellationToken);

        Assert.True(outcome.Succeeded, outcome.Outcome.Message);
        Assert.False(Directory.Exists(path));
        Assert.Single(await harness.GitClient.ListWorktreesAsync(temp.Path, cancellationToken));
        Assert.True(File.Exists(canary), "Deleting the worktree deleted what a junction in it led to.");
        Assert.Contains(outcome.Outcome.Details ?? [], detail => detail.StartsWith("removed 2 directory junction(s) first, as links", StringComparison.Ordinal));
    }

    /// <summary>
    /// A junction that cannot be removed stops the deletion before git is run, where git would have left the worktree
    /// part way: its .git file and git's record of it stay, and the failure names the junction, what Windows said, and
    /// the junctions removed before it.
    /// </summary>
    [Fact]
    public async Task AJunctionThatCannotBeRemoved_StopsTheDelete_BeforeGitRemovesAnything()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        var path = await CreateAsync(harness, temp, "stuck");
        var gitDirectory = await GitDirectoryAsync(harness, path);
        var git = new InterceptingGitClient(harness.GitClient);
        var fileSystem = new JunctionsAnswered(
            harness.FileSystem,
            directory => throw new JunctionRemovalException(Path.Combine(directory, "b"), "Access is denied.", [Path.Combine(directory, "a")]));

        var outcome = await Service(harness, git, fileSystem).DeleteAsync(temp.Path, "stuck", force: false, deleteEvidence: false, cancellationToken: cancellationToken);

        Assert.Equal(HarnessExit.CommandFailed, outcome.Outcome.ExitCode);
        Assert.Equal(
            $"Worktree 'stuck' was not deleted: its directory junction '{Path.Combine(path, "b")}' could not be removed as a link: Access is denied. "
            + $"Only the 1 junction(s) before it were removed, as links, and what each led to is untouched: {Path.Combine(path, "a")}; git was not run. "
            + $"Deal with that junction, then run '{ToolPackage.Command} delete-worktree stuck' again.",
            outcome.Outcome.Message);
        Assert.DoesNotContain(git.Runs, run => run.Arguments is ["worktree", "remove", ..]);
        Assert.True(File.Exists(Path.Combine(path, ".git")));
        Assert.True(Directory.Exists(gitDirectory));
    }

    /// <summary>
    /// Junctions are removed only once every check has passed, what another program holds included, and before git is
    /// asked to remove anything; a worktree a check refuses keeps every junction it holds.
    /// </summary>
    [Fact]
    public async Task JunctionsAreRemovedOnceEveryCheckHasPassed_AndBeforeGit()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        var path = await CreateAsync(harness, temp, "ordered");
        var events = new List<string>();
        var git = new InterceptingGitClient(harness.GitClient)
        {
            BeforeRun = arguments =>
            {
                if (arguments is ["worktree", "remove", ..])
                {
                    events.Add("git removes");
                }
            },
        };
        var fileSystem = new JunctionsAnswered(harness.FileSystem, _ => { events.Add("junctions"); return []; }, () => events.Add("holds"));
        File.WriteAllText(Path.Combine(path, "notes.txt"), "never committed");

        var refused = await Service(harness, git, fileSystem).DeleteAsync(temp.Path, "ordered", force: false, deleteEvidence: false, cancellationToken: cancellationToken);

        Assert.Equal(HarnessExit.Refused, refused.Outcome.ExitCode);
        Assert.Empty(events);

        File.Delete(Path.Combine(path, "notes.txt"));
        var deleted = await Service(harness, git, fileSystem).DeleteAsync(temp.Path, "ordered", force: false, deleteEvidence: false, cancellationToken: cancellationToken);

        Assert.True(deleted.Succeeded, deleted.Outcome.Message);
        Assert.Equal(["holds", "junctions", "git removes"], events);
    }

    /// <summary>
    /// git reporting the worktree removed while its directory is still there - as git for Windows does around a
    /// junction - says what is already gone and how to finish, where it used to say only that the directory remained.
    /// </summary>
    [Fact]
    public async Task GitReportingTheWorktreeRemoved_WhileItsDirectoryRemains_SaysWhatIsGoneAndHowToFinish()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        var path = await CreateAsync(harness, temp, "left");
        var gitDirectory = await GitDirectoryAsync(harness, path);
        var git = new InterceptingGitClient(harness.GitClient)
        {
            InsteadOfRun = arguments =>
            {
                if (arguments is not ["worktree", "remove", ..])
                {
                    return null;
                }

                File.Delete(Path.Combine(path, ".git"));
                harness.FileSystem.DeleteDirectory(gitDirectory);
                return new GitCommandResult(0, string.Empty, string.Empty);
            },
        };

        var failed = await Service(harness, git).DeleteAsync(temp.Path, "left", force: false, deleteEvidence: false, cancellationToken: cancellationToken);

        Assert.Equal(HarnessExit.CommandFailed, failed.Outcome.ExitCode);
        Assert.Equal(
            $"git reported worktree 'left' removed, but '{path}' still exists. It stopped part way: its .git file and git's record of it are already gone. "
            + $"Every check passed before removal began, so run '{ToolPackage.Command} delete-worktree left --force' to finish deleting it.",
            failed.Outcome.Message);

        var forced = await harness.WorktreeService.DeleteAsync(temp.Path, "left", force: true, deleteEvidence: false, cancellationToken: cancellationToken);

        Assert.True(forced.Succeeded, forced.Outcome.Message);
        Assert.False(Directory.Exists(path));
    }

    /// <summary>
    /// On Windows, a worktree with a file another program holds open is refused whole before git is asked to remove
    /// anything, where git would have stopped part way with its .git file and git's record of it gone; once the
    /// file is closed, it deletes.
    /// </summary>
    [Fact]
    public async Task AnIgnoredFileHeldOpen_RefusesACheckedRemovalWhole_AndOnceClosedItDeletes()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows refuses to delete a file another program holds open.");

        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        await IgnoreAsync(harness, temp, "*.log");
        var path = await CreateAsync(harness, temp, "held");
        var gitDirectory = await GitDirectoryAsync(harness, path);
        var held = Path.Combine(path, "build.log");
        File.WriteAllText(held, "held open by a build server");
        var git = new InterceptingGitClient(harness.GitClient);

        WorktreeOutcome refused;

        using (new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            refused = await Service(harness, git).DeleteAsync(temp.Path, "held", force: false, deleteEvidence: false, cancellationToken: cancellationToken);
        }

        Assert.Equal(HarnessExit.Refused, refused.Outcome.ExitCode);
        Assert.StartsWith("Worktree 'held' was not deleted, and nothing of it was removed: something holds '", refused.Outcome.Message, StringComparison.Ordinal);
        Assert.Contains("build.log'", refused.Outcome.Message, StringComparison.Ordinal);
        Assert.EndsWith($"Windows said: {new Win32Exception(SharingViolation).Message}", refused.Outcome.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(git.Runs, run => run.Arguments is ["worktree", "remove", ..]);
        Assert.True(File.Exists(held));
        Assert.True(File.Exists(Path.Combine(path, ".git")));
        Assert.True(Directory.Exists(gitDirectory));

        var deleted = await harness.WorktreeService.DeleteAsync(temp.Path, "held", force: false, deleteEvidence: false, cancellationToken: cancellationToken);

        Assert.True(deleted.Succeeded, deleted.Outcome.Message);
        Assert.False(Directory.Exists(path));
    }

    /// <summary>
    /// On Windows, a watcher on a directory in the worktree, as an editor or a language server keeps, holds nothing
    /// git's removal cannot go through, and is no reason to refuse it.
    /// </summary>
    [Fact]
    public async Task AWatcherOnADirectoryInIt_DoesNotStopACheckedRemoval()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows refuses to delete what another program holds.");

        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        await IgnoreAsync(harness, temp, "scratch/");
        var path = await CreateAsync(harness, temp, "watched");
        var scratch = Path.Combine(path, "scratch");
        Directory.CreateDirectory(scratch);
        File.WriteAllText(Path.Combine(scratch, "notes.txt"), "watched by an editor");

        WorktreeOutcome outcome;

        using (new FileSystemWatcher(scratch) { EnableRaisingEvents = true })
        {
            outcome = await harness.WorktreeService.DeleteAsync(temp.Path, "watched", force: false, deleteEvidence: false, cancellationToken: cancellationToken);
        }

        Assert.True(outcome.Succeeded, outcome.Outcome.Message);
        Assert.False(Directory.Exists(path));
    }

    /// <summary>
    /// A worktree another program holds part of is refused whole, once every check has passed and before git is
    /// asked to remove anything: its directory, its .git file and git's record of it all stay, and the refusal names
    /// what is held and says what Windows said.
    /// </summary>
    [Fact]
    public async Task AWorktreeAnotherProgramHolds_IsRefusedWhole_BeforeGitRemovesAnything()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        var path = await CreateAsync(harness, temp, "held");
        var gitDirectory = await GitDirectoryAsync(harness, path);
        var git = new InterceptingGitClient(harness.GitClient);
        var fileSystem = new HoldsAnswered(harness.FileSystem, FourHeld);

        var outcome = await Service(harness, git, fileSystem).DeleteAsync(temp.Path, "held", force: false, deleteEvidence: false, cancellationToken: cancellationToken);

        var asked = Assert.Single(fileSystem.AskedAbout);

        Assert.Equal(HarnessExit.Refused, outcome.Outcome.ExitCode);
        Assert.Equal(
            $"Worktree 'held' was not deleted, and nothing of it was removed: something holds '{Path.Combine(asked, "a")}', "
            + $"'{Path.Combine(asked, "b")}', '{Path.Combine(asked, "c")}' and 1 more - a process whose current directory is in the "
            + "worktree, a program with a file of it open, or a program running from it. Windows would not let git delete what is "
            + "held, so git would stop part way, with the worktree's .git file and git's record of it already gone. Close what holds "
            + $"it, then run '{ToolPackage.Command} delete-worktree held' again. Windows said: {HeldReason}",
            outcome.Outcome.Message);
        PathAssert.Same(path, asked);
        Assert.DoesNotContain(git.Runs, run => run.Arguments is ["worktree", "remove", ..]);
        Assert.True(File.Exists(Path.Combine(path, ".git")));
        Assert.True(Directory.Exists(gitDirectory));
        Assert.Equal(2, (await harness.GitClient.ListWorktreesAsync(temp.Path, cancellationToken)).Count);
    }

    /// <summary>
    /// Where what holds the worktree is this command's own current directory, as when it runs inside the worktree, the
    /// refusal says so and says to run it from outside, which nothing another program closes would let go; and where
    /// something else holds part of it too, that that is to be closed as well.
    /// </summary>
    [Fact]
    public async Task AWorktreeThisCommandRunsIn_IsRefused_SayingToRunItFromOutside()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        var path = await CreateAsync(harness, temp, "inside");
        var here = new HeldEntry(Environment.CurrentDirectory, HeldReason);

        var alone = await Service(harness, harness.GitClient, new HoldsAnswered(harness.FileSystem, _ => [here]))
            .DeleteAsync(temp.Path, "inside", force: false, deleteEvidence: false, cancellationToken: cancellationToken);
        var withOthers = await Service(harness, harness.GitClient, new HoldsAnswered(harness.FileSystem, directory => [.. FourHeld(directory), here]))
            .DeleteAsync(temp.Path, "inside", force: false, deleteEvidence: false, cancellationToken: cancellationToken);

        Assert.Equal(HarnessExit.Refused, alone.Outcome.ExitCode);
        Assert.Contains(
            $"already gone. It is this command's own current directory: run '{ToolPackage.Command} delete-worktree inside' again from outside the worktree. Windows said: ",
            alone.Outcome.Message,
            StringComparison.Ordinal);
        Assert.Equal(HarnessExit.Refused, withOthers.Outcome.ExitCode);
        Assert.Contains(
            $"already gone. One is this command's own current directory: close what holds the rest, then run '{ToolPackage.Command} delete-worktree inside' "
            + "again from outside the worktree. Windows said: ",
            withOthers.Outcome.Message,
            StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(path, ".git")));
    }

    /// <summary>
    /// An interruption while the worktree is looked through for what holds it stops the deletion before anything is
    /// removed, as one during the checks does: git is never asked to remove it.
    /// </summary>
    [Fact]
    public async Task AnInterruptionWhileLookingForWhatHoldsIt_DeletesNothing()
    {
        using var temp = new TempDirectory();
        using var interruption = new CancellationTokenSource();
        var harness = await PrepareAsync(temp);
        var path = await CreateAsync(harness, temp, "looking");
        var git = new InterceptingGitClient(harness.GitClient);
        var fileSystem = new HoldsAnswered(harness.FileSystem, _ =>
        {
            interruption.Cancel();
            return [];
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Service(harness, git, fileSystem).DeleteAsync(temp.Path, "looking", force: false, deleteEvidence: false, cancellationToken: interruption.Token));

        // The looking itself was given the interruption, so a long walk stops between one entry and the next.
        Assert.Equal(interruption.Token, fileSystem.LastToken);
        Assert.DoesNotContain(git.Runs, run => run.Arguments is ["worktree", "remove", ..]);
        Assert.True(File.Exists(Path.Combine(path, ".git")));
    }

    /// <summary>
    /// A worktree that cannot be looked through for what another program holds is not deleted: nothing was removed,
    /// and what stopped the looking is said.
    /// </summary>
    [Fact]
    public async Task AWorktreeThatCannotBeLookedThrough_IsNotDeleted_AndSaysWhy()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        var path = await CreateAsync(harness, temp, "unread");
        var git = new InterceptingGitClient(harness.GitClient);
        var fileSystem = new HoldsAnswered(harness.FileSystem, _ => throw new UnauthorizedAccessException(UnreadableReason));

        var outcome = await Service(harness, git, fileSystem).DeleteAsync(temp.Path, "unread", force: false, deleteEvidence: false, cancellationToken: cancellationToken);

        Assert.Equal(HarnessExit.CommandFailed, outcome.Outcome.ExitCode);
        Assert.StartsWith(
            $"'{Assert.Single(fileSystem.AskedAbout)}' could not be looked through for what another program holds in it: {UnreadableReason} "
            + "Nothing was deleted",
            outcome.Outcome.Message,
            StringComparison.Ordinal);
        Assert.DoesNotContain(git.Runs, run => run.Arguments is ["worktree", "remove", ..]);
        Assert.True(Directory.Exists(path));
    }

    /// <summary>Forced, nothing is asked about what holds the worktree: the removal goes ahead, as far as it can.</summary>
    [Fact]
    public async Task AForcedDelete_NeverAsksWhatHoldsTheWorktree()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        var path = await CreateAsync(harness, temp, "forced");
        var fileSystem = new HoldsAnswered(harness.FileSystem, FourHeld);

        var outcome = await Service(harness, harness.GitClient, fileSystem).DeleteAsync(temp.Path, "forced", force: true, deleteEvidence: false, cancellationToken: cancellationToken);

        Assert.True(outcome.Succeeded, outcome.Outcome.Message);
        Assert.False(Directory.Exists(path));
        Assert.Empty(fileSystem.AskedAbout);
    }

    /// <summary>
    /// On Windows, forced, a file another program holds open still leaves the removal part way, saying so, and
    /// forcing again once it is closed finishes it.
    /// </summary>
    [Fact]
    public async Task AnIgnoredFileHeldOpen_LeavesAForcedRemovalPartWay_AndForceFinishesIt()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows refuses to delete a file another program holds open.");

        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        await IgnoreAsync(harness, temp, "*.log");
        var path = await CreateAsync(harness, temp, "held");
        var held = Path.Combine(path, "build.log");
        File.WriteAllText(held, "held open by a build server");

        WorktreeOutcome failed;

        using (new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            failed = await harness.WorktreeService.DeleteAsync(temp.Path, "held", force: true, deleteEvidence: false, cancellationToken: cancellationToken);
        }

        Assert.Equal(HarnessExit.CommandFailed, failed.Outcome.ExitCode);
        Assert.StartsWith("Could not finish deleting '", failed.Outcome.Message, StringComparison.Ordinal);
        Assert.Contains("delete-worktree held --force", failed.Outcome.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(path, ".git")));

        var forced = await harness.WorktreeService.DeleteAsync(temp.Path, "held", force: true, deleteEvidence: false, cancellationToken: cancellationToken);

        Assert.True(forced.Succeeded, forced.Outcome.Message);
        Assert.False(Directory.Exists(path));
    }

    [Fact]
    public async Task AnInterruptionDuringRemoval_SaysAtOnceThatTheDeletionIsUnderWay()
    {
        using var temp = new TempDirectory();
        using var interruption = new CancellationTokenSource();
        var harness = await PrepareAsync(temp);
        var path = await CreateAsync(harness, temp, "noticed");
        string? warnedBeforeGitRan = null;
        var git = new InterceptingGitClient(harness.GitClient)
        {
            BeforeRun = arguments =>
            {
                if (arguments is ["worktree", "remove", ..])
                {
                    interruption.Cancel();
                    warnedBeforeGitRan = harness.StandardError.ToString();
                }
            },
        };

        var outcome = await Service(harness, git).DeleteAsync(temp.Path, "noticed", force: false, deleteEvidence: false, cancellationToken: interruption.Token);

        Assert.True(outcome.Succeeded, outcome.Outcome.Message);
        Assert.False(Directory.Exists(path));
        Assert.Contains("delete-worktree: WARN - Deleting worktree 'noticed' is under way and may be left half done", warnedBeforeGitRan, StringComparison.Ordinal);
        Assert.Contains("delete-worktree noticed --force", warnedBeforeGitRan, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AForcedDeleteThatCannotLookUpARecord_SaysNothingWasDeleted_WithoutOfferingForce()
    {
        using var temp = new TempDirectory();
        var harness = await PrepareAsync(temp);
        var path = await CreateAsync(harness, temp, "lookup");
        harness.FileSystem.DeleteDirectory(path);
        var git = new InterceptingGitClient(harness.GitClient)
        {
            ListWorktreesFailure = new HarnessException(HarnessExit.CommandFailed, "Could not list the worktrees: simulated failure"),
        };

        var outcome = await Service(harness, git).DeleteAsync(temp.Path, "lookup", force: true, deleteEvidence: false, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.CommandFailed, outcome.Outcome.ExitCode);
        Assert.StartsWith("Could not list the worktrees: simulated failure", outcome.Outcome.Message, StringComparison.Ordinal);
        Assert.Contains("Nothing was deleted", outcome.Outcome.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("--force", outcome.Outcome.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AListingThatFailsAfterTheDeletion_SaysTheWorktreeIsAlreadyDeleted()
    {
        using var temp = new TempDirectory();
        var harness = await PrepareAsync(temp);
        var path = await CreateAsync(harness, temp, "unlisted");

        // Without its .git file, git cannot name the record's directory, so git's list is asked, and fails once the
        // directory is gone.
        File.Delete(Path.Combine(path, ".git"));
        var git = new InterceptingGitClient(harness.GitClient)
        {
            ListWorktreesFailure = new HarnessException(HarnessExit.CommandFailed, "Could not list the worktrees: simulated failure"),
            ListWorktreesFailsWhen = () => !Directory.Exists(path),
        };

        var outcome = await Service(harness, git).DeleteAsync(temp.Path, "unlisted", force: true, deleteEvidence: false, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.CommandFailed, outcome.Outcome.ExitCode);
        Assert.StartsWith("Worktree 'unlisted' is deleted, but whether git still has it registered could not be confirmed", outcome.Outcome.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(path));
    }

    /// <summary>
    /// A directory with no .git of its own may hold worktrees below it, which git's list names: where git cannot give
    /// it, nothing is deleted, --force or not, since deleting the directory could delete them.
    /// </summary>
    [Fact]
    public async Task AListingThatFailsBeforeTheDeletion_OfADirectoryWithNoGit_DeletesNothing()
    {
        using var temp = new TempDirectory();
        var harness = await PrepareAsync(temp);
        var path = await CreateAsync(harness, temp, "unlisted");
        File.Delete(Path.Combine(path, ".git"));
        var git = new InterceptingGitClient(harness.GitClient)
        {
            ListWorktreesFailure = new HarnessException(HarnessExit.CommandFailed, "Could not list the worktrees: simulated failure"),
        };

        var outcome = await Service(harness, git).DeleteAsync(temp.Path, "unlisted", force: true, deleteEvidence: false, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.CommandFailed, outcome.Outcome.ExitCode);
        Assert.StartsWith("Could not list the worktrees: simulated failure Nothing was deleted: whether", outcome.Outcome.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("--force", outcome.Outcome.Message, StringComparison.Ordinal);
        Assert.True(Directory.Exists(path));
    }

    [Fact]
    public async Task ForcingALockedWorktreeWhoseGitFileIsGone_RemovesTheDirectoryAndTheRecord()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        var path = await CreateAsync(harness, temp, "stuck");
        await harness.RunGitAsync(temp.Path, ["worktree", "lock", "--reason", "on a USB disk", path], cancellationToken);
        File.Delete(Path.Combine(path, ".git"));

        var outcome = await harness.WorktreeService.DeleteAsync(temp.Path, "stuck", force: true, deleteEvidence: false, cancellationToken: cancellationToken);

        Assert.True(outcome.Succeeded, outcome.Outcome.Message);
        Assert.False(Directory.Exists(path));
        Assert.Single(await harness.GitClient.ListWorktreesAsync(temp.Path, cancellationToken));
    }

    [Fact]
    public async Task ACheckedRemovalThatFailsWithEverythingStillThere_NeverClaimsNothingWasDeleted()
    {
        // git can delete files before it fails, and those would now read as changes.
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        var path = await CreateAsync(harness, temp, "remains");
        var git = new InterceptingGitClient(harness.GitClient)
        {
            InsteadOfRun = arguments =>
            {
                if (arguments is not ["worktree", "remove", ..])
                {
                    return null;
                }

                File.Delete(Path.Combine(path, "README.md"));
                return new GitCommandResult(255, string.Empty, "error: failed to delete 'build.log': Permission denied");
            },
        };

        var failed = await Service(harness, git).DeleteAsync(temp.Path, "remains", force: false, deleteEvidence: false, cancellationToken: cancellationToken);

        Assert.Equal(HarnessExit.CommandFailed, failed.Outcome.ExitCode);
        Assert.DoesNotContain("nothing was deleted", failed.Outcome.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("git may already have deleted some files", failed.Outcome.Message, StringComparison.Ordinal);
        Assert.Contains("delete-worktree remains --force", failed.Outcome.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARemovalStillRunningAsTheGraceRunsOut_IsStopped_AndSaysWhatIsLeft()
    {
        // Left to the command line, the process would end mid-deletion without a word.
        using var temp = new TempDirectory();
        using var interruption = new CancellationTokenSource();
        var harness = await PrepareAsync(temp);
        var path = await CreateAsync(harness, temp, "hung");
        var stopped = false;
        var git = new InterceptingGitClient(harness.GitClient)
        {
            BeforeRun = arguments =>
            {
                if (arguments is ["worktree", "remove", ..])
                {
                    interruption.Cancel();
                }
            },
            RunInstead = async (arguments, token) =>
            {
                if (arguments is not ["worktree", "remove", ..])
                {
                    return null;
                }

                // A git that hangs until it is stopped.
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
                catch (OperationCanceledException)
                {
                    stopped = true;
                    throw;
                }

                return null;
            },
        };
        var service = new WorktreeService(harness.ContextLoader, git, harness.FileSystem, harness.PathBudget, harness.Platform, harness.Output, harness.HostCopies, harness.LocalTransport)
        {
            InterruptionGrace = TimeSpan.FromMilliseconds(400),
        };

        var outcome = await service.DeleteAsync(temp.Path, "hung", force: false, deleteEvidence: false, cancellationToken: interruption.Token);

        Assert.True(stopped, "git was not stopped before the grace ran out.");
        Assert.Equal(HarnessExit.Cancelled, outcome.Outcome.ExitCode);
        Assert.StartsWith("Deleting worktree 'hung' was stopped part way", outcome.Outcome.Message, StringComparison.Ordinal);
        Assert.Contains("delete-worktree hung --force", outcome.Outcome.Message, StringComparison.Ordinal);
        Assert.True(Directory.Exists(path));
    }

    [Fact]
    public async Task ARecordWhoseDirectoryCannotBeFound_FailsBeforeAnythingIsDeleted()
    {
        // Without the record's own directory, which HEAD is this worktree's, and which submodule
        // repositories clearing it deletes, cannot be known.
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        var path = await CreateAsync(harness, temp, "unfound");
        harness.FileSystem.DeleteDirectory(path);

        var outcome = await Service(harness, harness.GitClient, new RecordHidingFileSystem(harness.FileSystem))
            .DeleteAsync(temp.Path, "unfound", force: false, deleteEvidence: false, cancellationToken: cancellationToken);

        Assert.Equal(HarnessExit.CommandFailed, outcome.Outcome.ExitCode);
        Assert.Contains("the directory holding its record could not be found", outcome.Outcome.Message, StringComparison.Ordinal);
        Assert.Contains("Nothing was deleted", outcome.Outcome.Message, StringComparison.Ordinal);
        Assert.Equal(2, (await harness.GitClient.ListWorktreesAsync(temp.Path, cancellationToken)).Count);
    }

    [Fact]
    public async Task ALinkLoopInTheWorktreesPath_FailsWithAClearReason()
    {
        using var temp = new TempDirectory();
        var harness = await PrepareAsync(temp);
        var worktrees = Path.GetDirectoryName(HarnessFactory.WorktreePath(temp.Path, "any"))!;
        var loop = Path.Combine(Path.GetDirectoryName(worktrees)!, "loop");
        harness.FileSystem.DeleteDirectory(worktrees);

        // Each link leads to the other. A junction is made to a directory that exists, so the loop
        // is closed only once both links are there.
        Directory.CreateDirectory(loop);
        TestLinks.DirectoryLink(worktrees, loop);
        Directory.Delete(loop);
        TestLinks.DirectoryLink(loop, worktrees);

        var outcome = await harness.WorktreeService.DeleteAsync(temp.Path, "looped", force: false, deleteEvidence: false, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.CommandFailed, outcome.Outcome.ExitCode);
        Assert.StartsWith("Could not follow the links in the worktree's path", outcome.Outcome.Message, StringComparison.Ordinal);
        Assert.Contains("Nothing was deleted", outcome.Outcome.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnInterruptedForcedRemoval_ClaimsNothingAboutChecks()
    {
        // A forced deletion made no check, so telling the reader every check passed would read as
        // reassurance that nothing valuable was at risk.
        using var temp = new TempDirectory();
        using var interruption = new CancellationTokenSource();
        var harness = await PrepareAsync(temp);
        var path = await CreateAsync(harness, temp, "forced");

        // Without its .git file git leaves the directory, so the stop finds part of it gone.
        File.Delete(Path.Combine(path, ".git"));
        var git = new InterceptingGitClient(harness.GitClient)
        {
            BeforeRun = arguments =>
            {
                if (arguments is ["worktree", "remove", ..])
                {
                    interruption.Cancel();
                }
            },
            RunInstead = async (arguments, token) =>
            {
                if (arguments is not ["worktree", "remove", ..])
                {
                    return null;
                }

                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return null;
            },
        };
        var service = new WorktreeService(harness.ContextLoader, git, harness.FileSystem, harness.PathBudget, harness.Platform, harness.Output, harness.HostCopies, harness.LocalTransport)
        {
            InterruptionGrace = TimeSpan.FromMilliseconds(400),
        };

        var outcome = await service.DeleteAsync(temp.Path, "forced", force: true, deleteEvidence: false, cancellationToken: interruption.Token);

        Assert.Equal(HarnessExit.Cancelled, outcome.Outcome.ExitCode);
        Assert.Contains("already gone", outcome.Outcome.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Every check passed", outcome.Outcome.Message, StringComparison.Ordinal);
        Assert.Contains("delete-worktree forced --force", outcome.Outcome.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStopWhileTheRecordIsVerified_ReportsTheDeletionAsDone()
    {
        // Verification deletes nothing. Stopping it would report a deletion that finished as one
        // left half done, and the rerun it advises would answer that there is no such worktree.
        using var temp = new TempDirectory();
        using var interruption = new CancellationTokenSource();
        var harness = await PrepareAsync(temp);
        var path = await CreateAsync(harness, temp, "verified");

        // Without its .git file git cannot name the record's directory, so git's list is asked.
        File.Delete(Path.Combine(path, ".git"));
        var removals = 0;
        var git = new InterceptingGitClient(harness.GitClient)
        {
            AfterRun = (arguments, result) =>
            {
                if (arguments is ["worktree", "remove", ..] && ++removals == 2)
                {
                    // The interruption arrives once the record is cleared, just before verification.
                    interruption.Cancel();
                    Thread.Sleep(100);
                }

                return result;
            },
        };
        var service = new WorktreeService(harness.ContextLoader, git, harness.FileSystem, harness.PathBudget, harness.Platform, harness.Output, harness.HostCopies, harness.LocalTransport)
        {
            InterruptionGrace = TimeSpan.Zero,
        };

        var outcome = await service.DeleteAsync(temp.Path, "verified", force: true, deleteEvidence: false, cancellationToken: interruption.Token);

        // The warning proves the interruption was delivered, which is what arms the stop, so
        // verification ran with the stop already fired rather than before it.
        Assert.Contains("is under way", harness.StandardError.ToString(), StringComparison.Ordinal);
        Assert.True(outcome.Succeeded, outcome.Outcome.Message);
        Assert.False(Directory.Exists(path));
        Assert.Single(await harness.GitClient.ListWorktreesAsync(temp.Path, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// The real file system, except that what another program holds is answered by <paramref name="answer"/>, and
    /// each directory asked about is recorded.
    /// </summary>
    private sealed class HoldsAnswered(IFileSystem inner, Func<string, IReadOnlyList<HeldEntry>> answer) : PassThroughFileSystem(inner)
    {
        private readonly List<string> _askedAbout = [];

        /// <summary>Each directory it was asked about, in order.</summary>
        public IReadOnlyList<string> AskedAbout => _askedAbout;

        /// <summary>The interruption the last asking was given.</summary>
        public CancellationToken LastToken { get; private set; }

        public override IReadOnlyList<HeldEntry> FindHeld(string path, CancellationToken cancellationToken = default)
        {
            _askedAbout.Add(path);
            LastToken = cancellationToken;
            return answer(path);
        }
    }

    /// <summary>
    /// The real file system, except that removing the junctions under a directory is answered by
    /// <paramref name="removeJunctions"/>, and each look for what another program holds is reported to
    /// <paramref name="lookingForHolds"/> before it is answered as the real one answers.
    /// </summary>
    private sealed class JunctionsAnswered(
        IFileSystem inner,
        Func<string, IReadOnlyList<string>> removeJunctions,
        Action? lookingForHolds = null) : PassThroughFileSystem(inner)
    {
        public override IReadOnlyList<string> RemoveJunctions(string path) => removeJunctions(path);

        public override IReadOnlyList<HeldEntry> FindHeld(string path, CancellationToken cancellationToken = default)
        {
            lookingForHolds?.Invoke();
            return base.FindHeld(path, cancellationToken);
        }
    }

    /// <summary>Four entries of <paramref name="directory"/>, each held as Windows says one is.</summary>
    private static IReadOnlyList<HeldEntry> FourHeld(string directory)
        => [.. new[] { "a", "b", "c", "d" }.Select(name => new HeldEntry(Path.Combine(directory, name), HeldReason))];

    /// <summary>Makes the main checkout ignore <paramref name="pattern"/>, committed, so a worktree made from it does too.</summary>
    private static async Task IgnoreAsync(HarnessFactory harness, TempDirectory temp, string pattern)
    {
        File.AppendAllText(temp.Combine(".gitignore"), $"\n{pattern}\n");
        await harness.CommitAllAsync(temp.Path, $"ignore {pattern}", TestContext.Current.CancellationToken);
    }

    /// <summary>The git directory of the worktree at <paramref name="path"/>, which is git's record of it.</summary>
    private static async Task<string> GitDirectoryAsync(HarnessFactory harness, string path)
        => (await harness.RunGitAsync(path, ["rev-parse", "--absolute-git-dir"], TestContext.Current.CancellationToken)).StandardOutput.Trim();

    /// <summary>The real file system, except that git's worktree records cannot be listed.</summary>
    private sealed class RecordHidingFileSystem(IFileSystem inner) : PassThroughFileSystem(inner)
    {
        private static readonly string Records = Path.Combine(".git", "worktrees");

        public override IEnumerable<string> EnumerateDirectories(string path)
            => Path.TrimEndingDirectorySeparator(path).EndsWith(Records, StringComparison.OrdinalIgnoreCase)
                ? []
                : base.EnumerateDirectories(path);
    }

    /// <summary>Replaces the worktrees directory of <paramref name="temp"/> with a link to <paramref name="target"/>.</summary>
    private static async Task LinkWorktreesDirectoryAsync(HarnessFactory harness, TempDirectory temp, TempDirectory target)
    {
        var worktrees = Path.GetDirectoryName(HarnessFactory.WorktreePath(temp.Path, "any"))!;
        harness.FileSystem.DeleteDirectory(worktrees);
        TestLinks.DirectoryLink(worktrees, target.Path);
    }

    /// <summary>
    /// What a removal leaves of a plain worktree - no .git of its own - holding a submodule's .git is deleted with --force:
    /// a worktree's submodules are its own contents, never worktrees below it.
    /// </summary>
    [Fact]
    public async Task AHuskHoldingASubmodulesGit_IsDeletedWithForce()
    {
        using var temp = new TempDirectory();
        var harness = await PrepareAsync(temp);
        var path = await CreateAsync(harness, temp, "plain");
        Directory.CreateDirectory(Path.Combine(path, "vendor"));
        File.WriteAllText(Path.Combine(path, "vendor", ".git"), "gitdir: ../.git/modules/vendor\n");
        File.Delete(Path.Combine(path, ".git"));

        var outcome = await harness.WorktreeService.DeleteAsync(temp.Path, "plain", force: true, deleteEvidence: false, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(outcome.Succeeded, outcome.Outcome.Message);
        Assert.False(Directory.Exists(path));
    }

    /// <summary>A directory under the worktrees root that cannot be looked in is said, and the worktrees git records are still listed.</summary>
    [Fact]
    public async Task ADirectoryUnderTheRootThatCannotBeLookedIn_IsSaid_AndTheRestListed()
    {
        using var temp = new TempDirectory();
        var harness = await PrepareAsync(temp);
        await CreateAsync(harness, temp, "real");
        var odd = Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(HarnessFactory.WorktreePath(temp.Path, "real"))!, "odd")).FullName;
        var service = Service(harness, harness.GitClient, new UnlistableFileSystem(harness.FileSystem, odd));

        var listed = await service.ListAsync(temp.Path, TestContext.Current.CancellationToken);

        Assert.Equal("real", Assert.Single(listed).Name);
        Assert.Contains($"'odd' under the worktrees root, '{odd}', could not be looked in", harness.StandardError.ToString(), StringComparison.Ordinal);
    }

    private static WorktreeService Service(HarnessFactory harness, IGitClient git, IFileSystem? fileSystem = null)
        => new(harness.ContextLoader, git, fileSystem ?? harness.FileSystem, harness.PathBudget, harness.Platform, harness.Output, harness.HostCopies, harness.Local(fileSystem ?? harness.FileSystem));

    private static async Task<HarnessFactory> PrepareAsync(TempDirectory temp)
    {
        var harness = new HarnessFactory();

        await harness.InitializeHarnessAsync(
            temp.Path,
            TestContext.Current.CancellationToken,
            new HarnessConfig { Worktrees = Relaxed });

        return harness;
    }

    private static async Task<string> CreateAsync(HarnessFactory harness, TempDirectory temp, string name)
    {
        var outcome = await harness.WorktreeService.CreateAsync(
            temp.Path, name, useRandomName: false, TestContext.Current.CancellationToken);

        Assert.True(outcome.Succeeded, outcome.Outcome.Message);
        return HarnessFactory.WorktreePath(temp.Path, name);
    }

}

/// <summary>The real file system, except that a directory cannot be deleted, as while another program holds a file in it open.</summary>
internal sealed class UndeletableFileSystem(IFileSystem inner) : PassThroughFileSystem(inner)
{
    public override void DeleteDirectory(string path)
        => throw new IOException("The process cannot access the file because it is being used by another process.");
}

/// <summary>
/// The real git client, with hooks a test uses to interrupt a command at a chosen moment, to change
/// what git answers, and to see what git was asked and whether that call could still be cancelled.
/// </summary>
internal sealed class InterceptingGitClient(IGitClient inner) : IGitClient
{
    private readonly List<(string[] Arguments, bool Cancellable)> _runs = [];

    /// <summary>Runs at the start of every call.</summary>
    public Action? BeforeEveryCall { get; init; }

    /// <summary>Runs before each arbitrary git command, with its arguments.</summary>
    public Action<IReadOnlyList<string>>? BeforeRun { get; init; }

    /// <summary>An answer to give instead of running a command, or <see langword="null"/> to run it.</summary>
    public Func<IReadOnlyList<string>, GitCommandResult?>? InsteadOfRun { get; init; }

    /// <summary>Replaces the result of each arbitrary git command once it has run.</summary>
    public Func<IReadOnlyList<string>, GitCommandResult, GitCommandResult>? AfterRun { get; init; }

    /// <summary>Whether calls reach git with their cancellation taken away.</summary>
    public bool IgnoreCancellation { get; init; }

    /// <summary>A failure a worktree listing throws instead of asking git: every listing, or each <see cref="ListWorktreesFailsWhen"/> picks.</summary>
    public HarnessException? ListWorktreesFailure { get; init; }

    /// <summary>Whether a listing throws <see cref="ListWorktreesFailure"/>, asked at each; every one does where it is not given.</summary>
    public Func<bool>? ListWorktreesFailsWhen { get; init; }

    /// <summary>Replaces what git said decides each path, once it has said it, given the directory asked in.</summary>
    public Func<string, IReadOnlyList<IgnoreDecision>, IReadOnlyList<IgnoreDecision>>? AfterExplainIgnored { get; init; }

    public Task<int> CountRepositoryCommitsAsync(
        string gitDirectory,
        IReadOnlyList<string> revisions,
        CancellationToken cancellationToken = default)
        => Call(() => inner.CountRepositoryCommitsAsync(gitDirectory, revisions, Token(cancellationToken)));

    public Task<bool> HasStashAsync(string gitDirectory, CancellationToken cancellationToken = default)
        => Call(() => inner.HasStashAsync(gitDirectory, Token(cancellationToken)));

    /// <summary>Every arbitrary git command, and whether its call could still be cancelled.</summary>
    public IReadOnlyList<(string[] Arguments, bool Cancellable)> Runs
    {
        get
        {
            lock (_runs)
            {
                return [.. _runs];
            }
        }
    }

    public bool IsInstalled() => inner.IsInstalled();

    public Task<bool> IsRepositoryAsync(string directory, CancellationToken cancellationToken = default)
        => Call(() => inner.IsRepositoryAsync(directory, Token(cancellationToken)));

    public Task<string?> GetRepositoryRootAsync(string directory, CancellationToken cancellationToken = default)
        => Call(() => inner.GetRepositoryRootAsync(directory, Token(cancellationToken)));

    public Task<GitWorktree?> GetMainWorktreeAsync(string directory, CancellationToken cancellationToken = default)
        => Call(() => inner.GetMainWorktreeAsync(directory, Token(cancellationToken)));

    public Task<bool> IsDirtyAsync(string directory, CancellationToken cancellationToken = default)
        => Call(() => inner.IsDirtyAsync(directory, Token(cancellationToken)));

    public Task<IReadOnlyList<GitStatusEntry>> GetStatusAsync(string directory, CancellationToken cancellationToken = default)
        => Call(() => inner.GetStatusAsync(directory, Token(cancellationToken)));

    public Task<IReadOnlyList<GitStatusEntry>> ReadStatusAsync(string directory, CancellationToken cancellationToken = default)
        => Call(() => inner.ReadStatusAsync(directory, Token(cancellationToken)));

    public Task<IReadOnlySet<string>> ListChangedSinceAsync(string directory, string commit, CancellationToken cancellationToken = default)
        => Call(() => inner.ListChangedSinceAsync(directory, commit, Token(cancellationToken)));

    public Task<IReadOnlyDictionary<string, string?>> BlobIdsAtAsync(string directory, string commit, IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
        => Call(() => inner.BlobIdsAtAsync(directory, commit, paths, Token(cancellationToken)));

    public Task<IReadOnlyList<GitWorktree>> ListWorktreesAsync(string directory, CancellationToken cancellationToken = default)
        => ListWorktreesFailure is { } failure && (ListWorktreesFailsWhen?.Invoke() ?? true)
            ? Task.FromException<IReadOnlyList<GitWorktree>>(failure)
            : Call(() => inner.ListWorktreesAsync(directory, Token(cancellationToken)));

    public Task<GitLocation?> GetLocationAsync(string directory, CancellationToken cancellationToken = default)
        => Call(() => inner.GetLocationAsync(directory, Token(cancellationToken)));

    public Task<IReadOnlyList<GitIndexEntry>> ListIndexAsync(string directory, CancellationToken cancellationToken = default)
        => Call(() => inner.ListIndexAsync(directory, Token(cancellationToken)));

    public Task IndexExactlyAsync(string directory, IReadOnlyCollection<string> paths, CancellationToken cancellationToken = default)
    {
        BeforeEveryCall?.Invoke();
        return inner.IndexExactlyAsync(directory, paths, Token(cancellationToken));
    }

    public Task<string> GetIndexFileAsync(string directory, CancellationToken cancellationToken = default)
        => Call(() => inner.GetIndexFileAsync(directory, Token(cancellationToken)));

    public Task<IReadOnlyList<string>> FindEditedFilesAsync(
        string directory,
        string indexCopy,
        IReadOnlyList<string> assumedUnchanged,
        IReadOnlyList<string> skipWorktree,
        CancellationToken cancellationToken = default)
        => Call(() => inner.FindEditedFilesAsync(directory, indexCopy, assumedUnchanged, skipWorktree, Token(cancellationToken)));

    public Task<string?> ResolveGitDirectoryAsync(string directory, string path, CancellationToken cancellationToken = default)
        => Call(() => inner.ResolveGitDirectoryAsync(directory, path, Token(cancellationToken)));

    public Task<int> CountCommitsAsync(
        string directory,
        IReadOnlyList<string> revisions,
        CancellationToken cancellationToken = default)
        => Call(() => inner.CountCommitsAsync(directory, revisions, Token(cancellationToken)));

    public Task<bool> IsIgnoredAsync(string directory, string path, CancellationToken cancellationToken = default)
        => Call(() => inner.IsIgnoredAsync(directory, path, Token(cancellationToken)));

    public Task<IReadOnlyList<IgnoreDecision>> ExplainIgnoredAsync(
        string directory,
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken = default)
        => Call(async () =>
        {
            var decisions = await inner.ExplainIgnoredAsync(directory, paths, Token(cancellationToken));

            return AfterExplainIgnored is { } replace ? replace(directory, decisions) : decisions;
        });

    public Task<IReadOnlySet<string>> FindIgnoredAsync(string directory, IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
        => Call(() => inner.FindIgnoredAsync(directory, paths, Token(cancellationToken)));

    public Task<string?> ResolveCommitAsync(string directory, string reference, CancellationToken cancellationToken = default)
        => Call(() => inner.ResolveCommitAsync(directory, reference, Token(cancellationToken)));

    public Task<string?> MergeBaseAsync(string directory, string first, IReadOnlyList<string> others, CancellationToken cancellationToken = default)
        => Call(() => inner.MergeBaseAsync(directory, first, others, Token(cancellationToken)));

    public Task<IReadOnlyList<string>> ListMergeHeadsAsync(string directory, CancellationToken cancellationToken = default)
        => Call(() => inner.ListMergeHeadsAsync(directory, Token(cancellationToken)));

    public Task<bool?> IsShallowAsync(string directory, CancellationToken cancellationToken = default)
        => Call(() => inner.IsShallowAsync(directory, Token(cancellationToken)));

    public Task<string?> ReadFileAtCommitAsync(
        string directory,
        string commit,
        string relativePath,
        CancellationToken cancellationToken = default)
        => Call(() => inner.ReadFileAtCommitAsync(directory, commit, relativePath, Token(cancellationToken)));

    public Task<IReadOnlyDictionary<string, string?>> ReadFilesAtCommitAsync(
        string directory,
        string commit,
        IReadOnlyList<string> relativePaths,
        CancellationToken cancellationToken = default)
        => Call(() => inner.ReadFilesAtCommitAsync(directory, commit, relativePaths, Token(cancellationToken)));

    public Task<IReadOnlyList<GitName>> ListFilesAtCommitAsync(
        string directory,
        string commit,
        CancellationToken cancellationToken = default)
        => Call(() => inner.ListFilesAtCommitAsync(directory, commit, Token(cancellationToken)));

    public Task<IReadOnlyList<GitName>> ListNamesAsync(
        string directory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default)
        => Call(() => inner.ListNamesAsync(directory, arguments, Token(cancellationToken)));

    /// <summary>
    /// An answer to give once the call's own token is in hand, or <see langword="null"/> to run the
    /// command: a stand-in for a git that hangs until it is stopped.
    /// </summary>
    public Func<IReadOnlyList<string>, CancellationToken, Task<GitCommandResult?>>? RunInstead { get; init; }

    public async Task<GitCommandResult> RunAsync(
        string directory,
        IReadOnlyList<string> arguments,
        bool echoOutput = false,
        CancellationToken cancellationToken = default)
    {
        BeforeEveryCall?.Invoke();
        BeforeRun?.Invoke(arguments);

        lock (_runs)
        {
            _runs.Add(([.. arguments], cancellationToken.CanBeCanceled));
        }

        if (InsteadOfRun?.Invoke(arguments) is { } answer)
        {
            return answer;
        }

        if (RunInstead is not null && await RunInstead(arguments, cancellationToken) is { } scripted)
        {
            return scripted;
        }

        var result = await inner.RunAsync(directory, arguments, echoOutput, Token(cancellationToken));
        return AfterRun is null ? result : AfterRun(arguments, result);
    }

    private CancellationToken Token(CancellationToken cancellationToken)
        => IgnoreCancellation ? CancellationToken.None : cancellationToken;

    private Task<T> Call<T>(Func<Task<T>> call)
    {
        BeforeEveryCall?.Invoke();
        return call();
    }
}

/// <summary>The real file system, except that one directory cannot be listed, as one this user may not read.</summary>
internal sealed class UnlistableFileSystem(IFileSystem inner, string unlistable) : PassThroughFileSystem(inner)
{
    public override IEnumerable<string> EnumerateDirectories(string path)
        => string.Equals(Path.GetFullPath(path), Path.GetFullPath(unlistable), StringComparison.OrdinalIgnoreCase)
            ? throw new UnauthorizedAccessException($"Access to the path '{path}' is denied.")
            : base.EnumerateDirectories(path);
}
