using System.Text.Json;
using NSubstitute;
using DssHarness.Core.Configuration;
using DssHarness.Core.Execution;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Hosts;
using DssHarness.Core.Legs;
using DssHarness.Core.Mutations;
using DssHarness.Core.Output;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;
using DssHarness.Core.Runs;
using DssHarness.Core.Sync;
using DssHarness.Core.Worktrees;

namespace DssHarness.Tests;

/// <summary>
/// A copy per tree on each host: the main checkout's at the host's repositoryPath, each worktree's beside it, and a
/// worktree's copies removed from their hosts when it is deleted, where the harness made them.
/// </summary>
public sealed class HostCopiesTests
{
    private static readonly HostId Pi = HostId.Ssh("pi");

    private static readonly HostId Mac = HostId.Ssh("mac");

    /// <summary>
    /// A worktree's copy is kept beside the main checkout's copy, so that copy is the one it is of; any other copy - the
    /// main checkout's, or one named as no worktree's copy is - is its own.
    /// </summary>
    [Fact]
    public void TheMainCopyOfAWorktreesCopy_IsTheCopyBesideIt_AndOfAnyOtherCopy_ItsOwn()
    {
        var main = Path.Combine(TestHost.TemporaryRoot, "host", "repo");

        Assert.Equal(main, HostCopies.MainCopyOf(main + ".worktree-o1--xa"));
        Assert.Equal(main, HostCopies.MainCopyOf(main + ".worktree-feature" + Path.DirectorySeparatorChar));
        Assert.Equal(main, HostCopies.MainCopyOf(main));
        Assert.Equal(main + ".worktree-My_Feature", HostCopies.MainCopyOf(main + ".worktree-My_Feature"));
        Assert.Equal(main + ".worktree-", HostCopies.MainCopyOf(main + ".worktree-"));
    }

    /// <summary>
    /// A leg's own copy kept in a tree's .legs is held by that tree, the slot's name compared as the machine compares
    /// paths - one that folds case holds a '.LEGS' copy as its own '.legs' one - and a host's tree is of the main copy its
    /// holder, or the tree itself, is kept beside: the one rule a host finds its main copy by and a tree's repository is
    /// told by.
    /// </summary>
    [Fact]
    public void ALegsCopyInATreesLegs_IsHeldByThatTree_ItsSlotComparedAsTheMachineComparesPaths()
    {
        var main = Path.Combine(TestHost.TemporaryRoot, "host", "repo");
        var worktree = main + ".worktree-o1--xa";
        var inSlot = Path.Combine(worktree, HarnessLayout.LegsDirectoryName, "357e24c");
        var folded = Path.Combine(worktree, ".LEGS", "357e24c");

        Assert.Equal(worktree, HostCopies.HolderOf(inSlot, StringComparison.Ordinal));
        Assert.Equal(worktree, HostCopies.HolderOf(folded + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        Assert.Null(HostCopies.HolderOf(folded, StringComparison.Ordinal));
        Assert.Null(HostCopies.HolderOf(worktree, StringComparison.OrdinalIgnoreCase));

        Assert.Equal(main, HostCopies.MainCopyOfTree(inSlot, StringComparison.Ordinal));
        Assert.Equal(main, HostCopies.MainCopyOfTree(folded, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(folded, HostCopies.MainCopyOfTree(folded, StringComparison.Ordinal));
        Assert.Equal(main, HostCopies.MainCopyOfTree(worktree + Path.DirectorySeparatorChar, StringComparison.Ordinal));
    }

    /// <summary>
    /// The main checkout keeps the host's repositoryPath, and a worktree a copy of its own beside it, named for its
    /// directory as a worktree's name is spelt, whatever form the repositoryPath takes.
    /// </summary>
    [Theory]
    [InlineData("/home/dev/repo", "feature", "/home/dev/repo.worktree-feature")]
    [InlineData("/home/dev/repo/", "feature", "/home/dev/repo.worktree-feature")]
    [InlineData("~/src/repo", "fix-42", "~/src/repo.worktree-fix-42")]
    [InlineData(@"C:\build\repo\", "feature", @"C:\build\repo.worktree-feature")]
    [InlineData("/home/dev/repo", "My Feature_X", "/home/dev/repo.worktree-my-feature-x")]
    [InlineData("/home/dev/repo", "__", "/home/dev/repo.worktree-worktree")]
    public void AWorktreesCopy_IsBesideTheMainCheckouts_UnderItsDirectorysName(string repositoryPath, string directory, string expected)
    {
        using var temp = new TempDirectory();
        var layout = new HarnessLayout(temp.Path, temp.Path);

        var root = temp.Combine(".harness-config", "worktrees");

        Assert.Equal(repositoryPath, HostCopies.For(repositoryPath, layout, temp.Path + Path.DirectorySeparatorChar, root, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(expected, HostCopies.For(repositoryPath, layout, temp.Combine(".harness-config", "worktrees", directory), root, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Two orchestrators' agents of one name keep copies apart, each under its orchestrator's name and its own joined
    /// by two hyphens, which no worktree's own name can hold; a plain worktree keeps its name.
    /// </summary>
    [Fact]
    public void AnAgentsCopy_IsNamedForItsOrchestratorAndItself()
    {
        using var temp = new TempDirectory();
        var layout = new HarnessLayout(temp.Path, temp.Path);
        var root = temp.Combine(".worktrees");

        Assert.Equal("o1--api", HostCopies.NameOf(root, Path.Combine(root, "o1", "api"), StringComparison.OrdinalIgnoreCase));
        Assert.Equal("o2--api", HostCopies.NameOf(root, Path.Combine(root, "o2", "api"), StringComparison.OrdinalIgnoreCase));
        Assert.Equal("api", HostCopies.NameOf(root, Path.Combine(root, "api"), StringComparison.OrdinalIgnoreCase));
        Assert.Equal("/home/dev/repo.worktree-o1--api", HostCopies.For("/home/dev/repo", layout, Path.Combine(root, "o1", "api"), root, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A worktrees root kept on another disk through a link names an agent's copy alike whether the tree is spelt
    /// through the link, as a leg is configured, or where it leads, as git names the tree a command runs in.
    /// </summary>
    [Fact]
    public void AnAgentsCopy_IsNamedAlike_ThroughALinkedRootAndWhereItLeads()
    {
        using var temp = new TempDirectory();
        var target = Directory.CreateDirectory(temp.Combine("elsewhere", "trees", "o1", "api")).Parent!.Parent!.FullName;
        var root = temp.Combine(".worktrees");

        TestLinks.OrSkip(() => Directory.CreateSymbolicLink(root, target));

        Assert.Equal("o1--api", HostCopies.NameOf(root, Path.Combine(root, "o1", "api"), StringComparison.OrdinalIgnoreCase));
        Assert.Equal("o1--api", HostCopies.NameOf(root, Path.Combine(target, "o1", "api"), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A root reached through a link above it - not itself one - names an agent's copies by the agent's address all the same.</summary>
    [Fact]
    public void AnAgentsCopy_IsNamedForItsAddress_ThroughALinkAboveTheRoot()
    {
        using var temp = new TempDirectory();
        var target = Directory.CreateDirectory(temp.Combine("real-out", "wt", "o1", "api")).Parent!.Parent!.Parent!.FullName;
        var linked = temp.Combine("out");

        TestLinks.OrSkip(() => TestLinks.DirectoryLink(linked, target));

        var root = Path.Combine(linked, "wt");
        Assert.Equal("o1--api", HostCopies.NameOf(root, Path.Combine(target, "wt", "o1", "api"), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AHostDeclaringNoRepositoryPath_HasNowhereToKeepACopy()
    {
        var refusal = Assert.Throws<HarnessException>(() => HostCopies.RepositoryPathOf(new HarnessContext(new HarnessLayout("/repo", "/repo"), new HarnessConfig()), Pi));

        Assert.Equal(HarnessExit.ConfigInvalid, refusal.ExitCode);
        Assert.Contains("ssh pi declares no repositoryPath", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Where a host keeps this machine's copies is one answer: the REPOSITORY_PATH its item's .env sets, over config.json's
    /// repositoryPath, said as written with what set it; config.json's where the item sets none; and, where the item cannot
    /// be read, not known - the host unavailable, never config.json's in its place, where a sync would write and a clean or
    /// a removal delete what that item's user keeps there. A worktree's copy is beside whichever it is.
    /// </summary>
    [Theory]
    [InlineData("item", "~/mine/repo", "REPOSITORY_PATH in '.harness-config/sshItems/pi/.env'")]
    [InlineData("none", "/home/pi/repo", "repositoryPath in '.harness-config/config.json'")]
    [InlineData("unread", null, null)]
    public void WhereAHostKeepsItsCopies_IsTheItemsOverConfigJsons_AndNotKnownWhereTheItemCannotBeRead(string @case, string? path, string? from)
    {
        var secrets = Substitute.For<DssHarness.Core.Secrets.IHostSecretsStore>();
        secrets.ReadFacts(Arg.Any<HarnessLayout>(), Pi).Returns(@case switch
        {
            "item" => new DssHarness.Core.Secrets.ItemFacts { File = ".harness-config/sshItems/pi/.env", Present = true, RepositoryPath = "~/mine/repo" },
            "none" => new DssHarness.Core.Secrets.ItemFacts { File = ".harness-config/sshItems/pi/.env", Present = true },
            _ => new DssHarness.Core.Secrets.ItemFacts { File = ".harness-config/sshItems/pi/.env", Present = true, Problem = "other users can change '.harness-config/sshItems/pi/.env'" },
        });
        var root = Path.GetFullPath("/repo");
        var layout = new HarnessLayout(root, root);
        var context = new HarnessContext(
            layout,
            new HarnessConfig { Hosts = new HostsConfig { Ssh = { ["pi"] = new SshHostConfig { RepositoryPath = "/home/pi/repo" } } } })
        {
            HostItems = DssHarness.Core.Secrets.HostItems.Reading(secrets, layout),
        };

        if (path is null)
        {
            var unknown = Assert.Throws<HarnessException>(() => HostCopies.RepositoryOf(context, Pi));

            Assert.Equal(HarnessExit.HostUnavailable, unknown.ExitCode);
            Assert.Equal(
                "Where host ssh pi keeps its copies for this machine is not known, since its item cannot be read: "
                    + "other users can change '.harness-config/sshItems/pi/.env'.",
                unknown.Message);
            return;
        }

        var worktree = Path.Combine(layout.WorktreesDirectoryUnder(context.Config.Worktrees.Root), "feature");
        var place = HostCopyPlace.Of(context, Pi, worktree, StringComparison.Ordinal);

        Assert.Equal(new HostRepositoryPath(path, from!), HostCopies.RepositoryOf(context, Pi));
        Assert.Equal(path + ".worktree-feature", place.Copy);
        Assert.Equal($"keeps this tree's copy at '{path}.worktree-feature', beside '{path}', as {from} sets", place.Describe());
        Assert.Equal($"keeps this tree's copy at '{path}', as {from} sets", HostCopyPlace.Of(context, Pi, root, StringComparison.Ordinal).Describe());
    }

    /// <summary>
    /// The record keeps each copy once, answers for one name at a time, and forgets a copy once; it is kept in the main
    /// checkout in a directory that keeps itself out of git; and it is refused where it cannot be read, as the run
    /// lock is, rather than read as empty, which would forget every copy it held.
    /// </summary>
    [Fact]
    public async Task TheRecord_KeepsEachCopyOnce_InADirectoryGitNeverSees_AndIsRefusedWhereItCannotBeRead()
    {
        using var temp = new TempDirectory();
        var harness = await PrepareAsync(temp);
        var layout = await LayoutAsync(harness, temp);
        var record = Record(harness);
        var feature = Entry("/home/dev/repo.worktree-feature", Tree(temp, "feature"));
        var other = new HostCopyEntry("other", "wsl Example-Linux", "~/repo.worktree-other", Tree(temp, "other"));

        Assert.Null(record.Claim(layout, feature));
        Assert.Null(record.Claim(layout, feature));
        Assert.Null(record.Claim(layout, other));

        Assert.Equal([feature], record.Of(layout, "feature"));
        Assert.Equal(HarnessLayout.SelfIgnoreRule, File.ReadAllText(layout.HostCopiesIgnoreFile));
        Assert.Equal(temp.Combine(".harness-config", "host-copies", HostCopyRecord.FileName), HostCopyRecord.PathOf(layout));

        record.Forget(layout, feature);

        Assert.Empty(record.Of(layout, "feature"));
        Assert.Equal([other], record.Of(layout, "other"));

        File.WriteAllText(HostCopyRecord.PathOf(layout), "not json");

        var refusal = Assert.Throws<HarnessException>(() => record.Of(layout, "other"));
        Assert.Equal(HarnessExit.Refused, refusal.ExitCode);
        Assert.Contains("Delete it to forget the copies it records", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A copy is claimed for one tree: while another worktree kept under the same name still exists, a claim for the
    /// same copy is answered with that worktree and records nothing, as the two would sync over each other; once it
    /// is seen to be gone, its entry is taken over. One whose tree cannot be looked at - the directory it was in is not
    /// there either, as on a drive not mounted - is never taken over: the claim is answered with it, and why.
    /// </summary>
    [Fact]
    public async Task AClaim_IsRefusedWhileAnotherWorktreeOfTheNameExists_AndTakesOverOneThatIsGone()
    {
        using var temp = new TempDirectory();
        var harness = await PrepareAsync(temp);
        var layout = await LayoutAsync(harness, temp);
        var record = Record(harness);
        var first = Tree(temp, "one", "feature");
        var second = Tree(temp, "two", "feature");
        var unseen = temp.Combine("unmounted", "feature");
        var theFirsts = Entry("/home/dev/repo.worktree-feature", first);
        var theSeconds = Entry("/home/dev/repo.worktree-feature", second);

        Assert.Null(record.Claim(layout, theFirsts));
        Assert.Equal(new RecordedHolder(first, TreeLook.There), record.Claim(layout, theSeconds));
        Assert.Equal([theFirsts], record.Of(layout, "feature"));

        Directory.Delete(first);

        Assert.Null(record.Claim(layout, theSeconds));
        Assert.Equal([theSeconds], record.Of(layout, "feature"));

        // Written by a claim from a tree on a drive that is now gone from this machine, as a record shared by two sides is.
        File.WriteAllText(
            HostCopyRecord.PathOf(layout),
            JsonSerializer.Serialize(new { copies = new[] { Entry("/home/dev/repo.worktree-feature", unseen) } }, JsonStateFile.Options));

        Assert.Equal(
            new RecordedHolder(unseen, TreeLook.Untold($"'{temp.Combine("unmounted")}', which it was in, is not there either: a volume or a share it was on may not be mounted")),
            record.Claim(layout, theSeconds));
        Assert.Equal(unseen, Assert.Single(record.Of(layout, "feature")).Tree);
    }

    /// <summary>
    /// Legs of one worktree synced to several hosts at once record their copies at once, and none is lost: each
    /// change is made under the record's machine-wide lock, on what the one before it wrote.
    /// </summary>
    [Fact]
    public async Task CopiesRecordedAtOnce_AreAllKept()
    {
        using var temp = new TempDirectory();
        var harness = await PrepareAsync(temp);
        var layout = await LayoutAsync(harness, temp);
        var record = Record(harness);
        var tree = Tree(temp, "feature");
        var entries = Enumerable.Range(0, 8)
            .Select(index => new HostCopyEntry("feature", $"ssh host{index}", "/home/dev/repo.worktree-feature", tree))
            .ToList();

        await Task.WhenAll(entries.Select(entry => Task.Run(() => record.Claim(layout, entry), TestContext.Current.CancellationToken)));

        Assert.Equal(entries.ToHashSet(), record.Of(layout, "feature").ToHashSet());
    }

    /// <summary>
    /// The far side removes a whole copy only where the harness made it: one it took over was somebody's directory
    /// first, and one with no mark of the harness's is nothing it knows it may remove - unless it holds nothing at
    /// all, as a removal whose last step failed leaves it, when there is nothing in it anybody could lose.
    /// </summary>
    [Theory]
    [InlineData("made", CopyRemoval.Removed, false)]
    [InlineData("taken over", CopyRemoval.Adopted, true)]
    [InlineData("taken over, and finished", CopyRemoval.Adopted, true)]
    [InlineData("unmarked", CopyRemoval.NotACopy, true)]
    [InlineData("left empty", CopyRemoval.Removed, false)]
    [InlineData("absent", CopyRemoval.Absent, false)]
    public async Task TheFarSide_RemovesACopyOnlyWhereTheHarnessMadeIt(string what, CopyRemoval expected, bool stays)
    {
        using var hosts = new TempDirectory();
        var harness = new HarnessFactory();
        var transport = Local(harness);
        var cancellationToken = TestContext.Current.CancellationToken;
        var copy = hosts.Combine("repo.worktree-feature");

        switch (what)
        {
            case "made":
                await transport.CreateRootAsync(copy, CopyMark.Complete, cancellationToken);
                break;
            case "taken over":
                await transport.CreateRootAsync(copy, CopyMark.AdoptionStopped, cancellationToken);
                break;
            case "taken over, and finished":
                await transport.CreateRootAsync(copy, CopyMark.AdoptionStopped, cancellationToken);
                await transport.CreateRootAsync(copy, CopyMark.Complete, cancellationToken);
                break;
            case "unmarked":
                Directory.CreateDirectory(copy);
                File.WriteAllText(Path.Combine(copy, "work.txt"), "somebody's");
                break;
            case "left empty":
                Directory.CreateDirectory(Path.Combine(copy, HarnessLayout.DirectoryName));
                break;
        }

        Assert.Equal(expected, await transport.RemoveCopyAsync(copy, cancellationToken));
        Assert.Equal(stays, Directory.Exists(copy));
    }

    /// <summary>
    /// A removal that stops part way - here, a directory the disk will not let go of - fails by name, and leaves what
    /// is left still marked as the harness's, because the marker goes last: asking again, once nothing holds it,
    /// removes the rest, where a remainder left unmarked would have to be left in place as somebody's. However the
    /// host's configuration spells the copy: a listing spells what it holds as the root was spelt, where a path built
    /// from the root is spelt as the machine spells one.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ACopyWhoseRemovalStopsPartWay_StaysMarked_AndAskingAgainRemovesTheRest(bool speltOtherwise)
    {
        using var hosts = new TempDirectory();
        var harness = new HarnessFactory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var copy = hosts.Combine("repo.worktree-feature");
        await Local(harness).CreateRootAsync(copy, CopyMark.Complete, cancellationToken);
        Directory.CreateDirectory(Path.Combine(copy, "build"));
        File.WriteAllText(Path.Combine(copy, "build", "held.o"), "held open");
        File.WriteAllText(Path.Combine(copy, "main.c"), "int main;");

        var root = !speltOtherwise ? copy
            : OperatingSystem.IsWindows() ? copy.Replace('\\', '/')
            : copy.Insert(copy.LastIndexOf('/'), "/");

        var failed = await Assert.ThrowsAsync<HarnessException>(
            () => Local(harness, new HoldsOpen(harness.FileSystem, "build")).RemoveCopyAsync(root, cancellationToken));

        Assert.Equal(HarnessExit.CommandFailed, failed.ExitCode);
        Assert.Contains($"'{root}' could not be removed whole", failed.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(copy, HarnessLayout.DirectoryName, LocalSyncTransport.MarkerFileName)));

        Assert.Equal(CopyRemoval.Removed, await Local(harness).RemoveCopyAsync(root, cancellationToken));
        Assert.False(Directory.Exists(copy));
    }

    /// <summary>
    /// A removal is asked of the harness on the host by the copy's own path, from the home directory - which is there
    /// when the directory the copy was kept in is gone, so the host can answer that the copy is not there either - and
    /// one the host never answered is that host being unavailable, so the copy stays recorded: never read as removed,
    /// or as not there.
    /// </summary>
    [Fact]
    public async Task ARemovalRequest_NamesTheCopy_AndOneNeverAnswered_IsTheHostUnavailable()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        const string copy = "/home/dev/repo.worktree-feature";
        HostAgentRequest? asked = null;

        var answering = new ScriptedHostCommands((_, command) =>
        {
            asked = JsonSerializer.Deserialize<HostAgentRequest>(command.StandardInput.Read(), HostAgentProtocol.JsonOptions);
            command.OnOutputLine?.Invoke(SyncServe.Answer(new SyncRemoveAnswer(CopyRemoval.Removed)));
            return HostResults.Finished(command, HarnessExit.Success);
        });

        Assert.Equal(CopyRemoval.Removed, await Remote(answering).RemoveCopyAsync(copy, cancellationToken));
        Assert.Equal([SyncServe.CommandName, SyncServe.RemoveCopy, SyncServe.OperandsFollow, copy], asked?.Arguments);
        Assert.Equal("~", asked?.Directory);

        var silent = new ScriptedHostCommands((_, command) => HostResults.Finished(command, HarnessExit.Success));
        var refusal = await Assert.ThrowsAsync<HarnessException>(() => Remote(silent).RemoveCopyAsync(copy, cancellationToken));

        Assert.Equal(HarnessExit.HostUnavailable, refusal.ExitCode);
        Assert.Contains($"did not answer whether it removed '{copy}'", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>The far side's removal answers as sync-serve, the command a remote sync's agent starts, answers every request.</summary>
    [Fact]
    public async Task SyncServe_AnswersARemovalRequest()
    {
        using var hosts = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var copy = hosts.Combine("repo.worktree-feature");
        await Local(new HarnessFactory()).CreateRootAsync(copy, CopyMark.Complete, cancellationToken);

        var result = await CliRunner.RunAsync(["sync-serve", SyncServe.RemoveCopy, SyncServe.OperandsFollow, copy], cancellationToken);

        Assert.Equal(HarnessExit.Success, result.ExitCode);
        Assert.Equal(CopyRemoval.Removed, SyncServe.ReadAnswer<SyncRemoveAnswer>(result.StandardOutput.Trim())?.Removal);
        Assert.False(Directory.Exists(copy));
    }

    /// <summary>
    /// Deleting a worktree removes its copies from the hosts recorded as holding one, and says so; a copy that is not
    /// the harness's to remove is left, and forgotten; and a host that cannot be asked keeps its copy recorded, and the
    /// deletion fails, naming it, though the worktree is gone - so deleting it again, once the host answers, finishes
    /// the job.
    /// </summary>
    [Fact]
    public async Task DeletingAWorktree_RemovesItsCopiesFromItsHosts_AndAgainFinishesWhatAHostLeft()
    {
        using var temp = new TempDirectory();
        using var hosts = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        var layout = await LayoutAsync(harness, temp);
        var record = Record(harness);
        var answering = new HashSet<HostId> { Pi };

        var onPi = hosts.Combine("pi", "repo.worktree-feature");
        var onMac = hosts.Combine("mac", "repo.worktree-feature");
        var theirs = hosts.Combine("pi-before", "repo.worktree-feature");
        await Local(harness).CreateRootAsync(onPi, CopyMark.Complete, cancellationToken);
        await Local(harness).CreateRootAsync(onMac, CopyMark.Complete, cancellationToken);
        Directory.CreateDirectory(theirs);
        File.WriteAllText(Path.Combine(theirs, "work.txt"), "somebody's");

        var created = await harness.WorktreeService.CreateAsync(temp.Path, "feature", useRandomName: false, cancellationToken);
        Assert.True(created.Succeeded, created.Outcome.Message);
        record.Claim(layout, Entry(onPi, created.Path));
        record.Claim(layout, Entry(onMac, created.Path, Mac));
        record.Claim(layout, Entry(theirs, created.Path));

        var service = Service(harness, answering);
        var deleted = await service.DeleteAsync(temp.Path, "feature", force: false, deleteEvidence: false, cancellationToken: cancellationToken);

        Assert.Equal(HarnessExit.HostUnavailable, deleted.Outcome.ExitCode);
        Assert.Equal(
            "Worktree 'feature' was deleted, and 1 of its copies on hosts are not yet dealt with: run 'dssharness delete-worktree feature' "
            + "once what keeps each is gone.",
            deleted.Outcome.Message);
        Assert.False(Directory.Exists(created.Path));
        Assert.Equal(
            [
                created.Path,
                $"ssh pi: removed its copy at '{onPi}'",
                $"ssh mac: its copy at '{onMac}' stays, and is still recorded: the host could not be reached: ssh said ssh: connect to host 192.0.2.10 port 22: Connection timed out",
                $"ssh pi: left '{theirs}' in place: nothing there says the harness made it",
            ],
            deleted.Outcome.Details);
        Assert.False(Directory.Exists(onPi));
        Assert.True(Directory.Exists(theirs));
        Assert.Equal([Entry(onMac, created.Path, Mac)], record.Of(layout, "feature"));

        var again = await service.DeleteAsync(temp.Path, "feature", force: false, deleteEvidence: false, cancellationToken: cancellationToken);

        Assert.Equal(HarnessExit.HostUnavailable, again.Outcome.ExitCode);
        Assert.StartsWith("Worktree 'feature' is gone already, and 1 of its copies on hosts are not yet dealt with", again.Outcome.Message, StringComparison.Ordinal);

        answering.Add(Mac);
        var finished = await service.DeleteAsync(temp.Path, "feature", force: false, deleteEvidence: false, cancellationToken: cancellationToken);

        Assert.True(finished.Succeeded, finished.Outcome.Message);
        Assert.Equal("Worktree 'feature' is gone already; each copy of it left on a host is dealt with.", finished.Outcome.Message);
        Assert.Equal([$"ssh mac: removed its copy at '{onMac}'"], finished.Outcome.Details);
        Assert.False(Directory.Exists(onMac));
        Assert.Empty(record.Of(layout, "feature"));

        var nothing = await service.DeleteAsync(temp.Path, "feature", force: false, deleteEvidence: false, cancellationToken: cancellationToken);

        Assert.Equal(HarnessExit.Refused, nothing.Outcome.ExitCode);
        Assert.Equal("No worktree named 'feature'.", nothing.Outcome.Message);
    }

    /// <summary>
    /// Deleting a worktree, and with it its copy on a host, leaves the record of verdicts as it was: it is in the main
    /// checkout, whichever tree swept, so a worktree's verdicts outlive it.
    /// </summary>
    [Fact]
    public async Task ADeletedWorktree_AndARemovedHostCopy_LeaveTheRecordAsItWas()
    {
        using var temp = new TempDirectory();
        using var hosts = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        var layout = await LayoutAsync(harness, temp);
        var onPi = hosts.Combine("pi", "repo.worktree-feature");
        await Local(harness).CreateRootAsync(onPi, CopyMark.Complete, cancellationToken);

        var created = await harness.WorktreeService.CreateAsync(temp.Path, "feature", useRandomName: false, cancellationToken);
        Assert.True(created.Succeeded, created.Outcome.Message);
        Record(harness).Claim(layout, Entry(onPi, created.Path));

        var recorded = ArmVerdictRecordTests.Recorded(harness.FileSystem, new HarnessLayout(created.Path, temp.Path));
        var deleted = await Service(harness, new HashSet<HostId> { Pi }).DeleteAsync(temp.Path, "feature", force: false, deleteEvidence: false, cancellationToken: cancellationToken);

        Assert.True(deleted.Succeeded, deleted.Outcome.Message);
        Assert.False(Directory.Exists(created.Path));
        Assert.False(Directory.Exists(onPi));
        Assert.Equal(recorded, ArmVerdictRecordTests.Snapshot(layout));
    }

    /// <summary>
    /// The mutation workers a host keeps in a worktree's copy go with the copy, and its line says so - a directory under
    /// a worker's name that nothing says the harness made went with it as the rest of the copy; while a sweep still
    /// running there holds one, the copy stays and is still recorded, with nothing in it removed - they are asked about
    /// before any goes, as in the worktree itself - so deleting the worktree again removes all of it once the sweep has
    /// ended. A worker an earlier version of this tool kept beside a copy, or beside the worktree, is known to nothing
    /// here: it is never removed, or said.
    /// </summary>
    [Fact]
    public async Task DeletingAWorktree_RemovesTheWorkersInItsHostCopy_AndKeepsACopyASweepStillHolds()
    {
        using var temp = new TempDirectory();
        using var hosts = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        var layout = await LayoutAsync(harness, temp);
        var record = Record(harness);

        var onPi = hosts.Combine("pi", "repo.worktree-feature");
        var free = Worker(onPi, "357e24cw-1");
        var held = Worker(onPi, "357e24cw-2");
        var somebodys = Worker(onPi, "357e24cw-3");

        foreach (var made in new[] { onPi, free, held })
        {
            await Local(harness).CreateRootAsync(made, CopyMark.Complete, cancellationToken);
            File.WriteAllText(Path.Combine(made, "main.c"), "int main;");
        }

        Directory.CreateDirectory(somebodys);
        File.WriteAllText(Path.Combine(somebodys, "notes.txt"), "somebody's");

        // A copy that is gone itself, with a worker an earlier version of this tool kept beside where it was.
        var onMac = hosts.Combine("mac", "repo.worktree-feature");
        var oldOnMac = onMac + ".mutation-357e24cw-1";

        await Local(harness).CreateRootAsync(oldOnMac, CopyMark.Complete, cancellationToken);

        var created = await harness.WorktreeService.CreateAsync(temp.Path, "feature", useRandomName: false, cancellationToken);
        Assert.True(created.Succeeded, created.Outcome.Message);
        record.Claim(layout, Entry(onPi, created.Path));
        record.Claim(layout, Entry(onMac, created.Path, Mac));

        var oldHere = created.Path + ".mutation-357e24cw-1";

        await Local(harness).CreateRootAsync(oldHere, CopyMark.Complete, cancellationToken);

        var claims = new Claims { Held = { [held] = "pid 4242, run 20261007-101500-abcd" } };
        var service = Service(harness, new HashSet<HostId> { Pi, Mac }, claims: claims);
        var size = harness.FileSystem.DirectorySize(held) + harness.FileSystem.DirectorySize(free);

        var deleted = await service.DeleteAsync(temp.Path, "feature", force: false, deleteEvidence: false, cancellationToken: cancellationToken);

        Assert.Equal(HarnessExit.Refused, deleted.Outcome.ExitCode);
        Assert.False(Directory.Exists(created.Path));
        Assert.Equal(
            [
                created.Path,
                $"ssh pi: its copy at '{onPi}' stays, and is still recorded: a mutation worker kept in it is in use - '{held}': "
                + "a sweep still running holds it: pid 4242, run 20261007-101500-abcd",
                $"ssh mac: found nothing at '{onMac}' to remove",
            ],
            deleted.Outcome.Details);
        Assert.True(Directory.Exists(onPi));
        Assert.True(Directory.Exists(held));
        Assert.True(Directory.Exists(free), "a copy kept for a worker in use has lost nothing, a worker no sweep holds included");
        Assert.True(Directory.Exists(somebodys));
        Assert.Equal([Entry(onPi, created.Path)], record.Of(layout, "feature"));

        claims.Held.Clear();

        var again = await service.DeleteAsync(temp.Path, "feature", force: false, deleteEvidence: false, cancellationToken: cancellationToken);

        Assert.True(again.Succeeded, again.Outcome.Message);
        Assert.Equal("Worktree 'feature' is gone already; each copy of it left on a host is dealt with.", again.Outcome.Message);
        Assert.Equal(
            [
                $"ssh pi: removed its copy at '{onPi}', with the 2 mutation worker(s) kept in it, {DiskSpace.Size(size)}; "
                + $"'{somebodys}', named as a mutation worker though nothing says the harness made it, went with it",
            ],
            again.Outcome.Details);
        Assert.False(Directory.Exists(onPi));
        Assert.Empty(record.Of(layout, "feature"));
        Assert.True(Directory.Exists(oldOnMac), "a worker an earlier version kept beside a copy is a person's to delete");
        Assert.True(Directory.Exists(oldHere), "a worker an earlier version kept beside a worktree is a person's to delete");
    }

    /// <summary>
    /// A host's copy kept once its workers had begun to go - a sweep took one between the asking and the removal, one
    /// could not be removed, or the copy's own removal failed - says which workers went before that, since nothing else
    /// would, and says nothing of that where none had; and one whose worker cannot be told, its marker unreadable, is
    /// kept with nothing removed. Each stays recorded, so deleting the worktree again asks again.
    /// </summary>
    [Theory]
    [InlineData("a sweep took one meanwhile", HarnessExit.Refused)]
    [InlineData("a sweep took the only one meanwhile", HarnessExit.Refused)]
    [InlineData("a worker could not be removed", HarnessExit.CommandFailed)]
    [InlineData("the copy could not be removed", HarnessExit.CommandFailed)]
    [InlineData("a worker cannot be told", HarnessExit.CommandFailed)]
    public async Task AHostsCopyKeptOnceItsWorkersHadBegunToGo_SaysWhichWent(string keptBy, int code)
    {
        using var temp = new TempDirectory();
        using var hosts = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        var layout = await LayoutAsync(harness, temp);
        var record = Record(harness);
        var onPi = hosts.Combine("pi", "repo.worktree-feature");
        var free = Worker(onPi, "357e24cw-1");
        var other = Worker(onPi, "357e24cw-2");

        var alone = keptBy == "a sweep took the only one meanwhile";

        foreach (var made in alone ? [onPi, other] : new[] { onPi, free, other })
        {
            await Local(harness).CreateRootAsync(made, CopyMark.Complete, cancellationToken);
            File.WriteAllText(Path.Combine(made, "main.c"), "int main;");
        }

        var went = harness.FileSystem.DirectorySize(free);
        var asked = 0;
        Claims claims = null!;
        IFileSystem disk = harness.FileSystem;

        claims = new Claims
        {
            Asked = copy =>
            {
                // Taken by a sweep the second time it is asked about: between the asking and the removal.
                if (keptBy.StartsWith("a sweep took ", StringComparison.Ordinal) && Path.GetFullPath(copy) == other && ++asked == 2)
                {
                    claims.Held[other] = "pid 7, run 20261007-101500-abcd";
                }
            },
        };

        switch (keptBy)
        {
            case "a worker could not be removed":
                Directory.CreateDirectory(Path.Combine(other, "held-build"));
                disk = new HoldsOpen(harness.FileSystem, "held-build");
                break;

            case "the copy could not be removed":
                Directory.CreateDirectory(Path.Combine(onPi, "held-build"));
                disk = new HoldsOpen(harness.FileSystem, "held-build");
                went += harness.FileSystem.DirectorySize(other);
                break;

            case "a worker cannot be told":
                File.WriteAllText(Path.Combine(other, HarnessLayout.DirectoryName, LocalSyncTransport.MarkerFileName), "{}");
                break;
        }

        var created = await harness.WorktreeService.CreateAsync(temp.Path, "feature", useRandomName: false, cancellationToken);
        Assert.True(created.Succeeded, created.Outcome.Message);
        record.Claim(layout, Entry(onPi, created.Path));

        var deleted = await Service(harness, new HashSet<HostId> { Pi }, hostDisk: disk, claims: claims)
            .DeleteAsync(temp.Path, "feature", force: false, deleteEvidence: false, cancellationToken: cancellationToken);

        var line = Assert.Single(deleted.Outcome.Details ?? [], said => said.StartsWith("ssh pi: ", StringComparison.Ordinal));
        var stays = $"ssh pi: its copy at '{onPi}' stays, and is still recorded: ";
        var held = "a sweep still running holds it: pid 7, run 20261007-101500-abcd";
        var before = keptBy == "a worker cannot be told" || alone
            ? string.Empty
            : $"; {(keptBy == "the copy could not be removed" ? 2 : 1)} mutation worker(s) kept in it were removed before that, {DiskSpace.Size(went)}";

        Assert.Equal(code, deleted.Outcome.ExitCode);
        Assert.StartsWith(
            stays + keptBy switch
            {
                "a sweep took one meanwhile" or "a sweep took the only one meanwhile" => $"a mutation worker kept in it is in use - '{other}': {held}",
                "a worker could not be removed" => $"a mutation worker kept in it could not be removed - '{other}' could not be removed whole: ",
                "the copy could not be removed" => $"'{onPi}' could not be removed whole: ",
                _ => "a mutation worker kept in it could not be removed - its marker cannot be read: ",
            },
            line,
            StringComparison.Ordinal);
        Assert.EndsWith(before.Length > 0 ? before : alone ? held : "which '--adopt' can then take over after listing what it would cost", line, StringComparison.Ordinal);
        Assert.True(Directory.Exists(onPi));
        Assert.Equal(keptBy != "the copy could not be removed", Directory.Exists(other));
        Assert.Equal(keptBy == "a worker cannot be told", Directory.Exists(free));
        Assert.Equal([Entry(onPi, created.Path)], record.Of(layout, "feature"));
    }

    /// <summary>
    /// An interruption as the workers kept in a host's copy go is the command's interruption, and says which went
    /// first: the copy stays, still recorded, with the workers not yet reached, for asking again - and the one it was
    /// stopped in is named, by its path, as one whose removal asking again finishes.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnInterruptionAsAHostsWorkersGo_SaysWhichWent_AndLeavesTheCopyRecorded(bool partWay)
    {
        using var temp = new TempDirectory();
        using var hosts = new TempDirectory();
        using var interruption = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        var layout = await LayoutAsync(harness, temp);
        var record = Record(harness);
        var onPi = hosts.Combine("pi", "repo.worktree-feature");
        var free = Worker(onPi, "357e24cw-1");
        var other = Worker(onPi, "357e24cw-2");

        foreach (var made in new[] { onPi, free, other })
        {
            await Local(harness).CreateRootAsync(made, CopyMark.Complete, cancellationToken);
            File.WriteAllText(Path.Combine(made, "main.c"), "int main;");
        }

        // Stopped once the first is gone, between the two - or part way through the second, once a directory of it is.
        var stoppedIn = Path.Combine(other, "obj");
        Directory.CreateDirectory(stoppedIn);

        var went = harness.FileSystem.DirectorySize(free);
        var created = await harness.WorktreeService.CreateAsync(temp.Path, "feature", useRandomName: false, cancellationToken);
        Assert.True(created.Succeeded, created.Outcome.Message);
        record.Claim(layout, Entry(onPi, created.Path));

        var deleted = await Service(harness, new HashSet<HostId> { Pi }, hostDisk: new InterruptsOnceRemoved(harness.FileSystem, partWay ? stoppedIn : free, interruption))
            .DeleteAsync(temp.Path, "feature", force: false, deleteEvidence: false, cancellationToken: interruption.Token);

        Assert.Equal(HarnessExit.Cancelled, deleted.Outcome.ExitCode);
        Assert.Equal(
            "Worktree 'feature' was deleted, and the interruption came before every host holding a copy of it had been asked to remove it: "
            + "run 'dssharness delete-worktree feature' to ask the rest.",
            deleted.Outcome.Message);
        Assert.Contains(
            $"ssh pi: its copy at '{onPi}' stays, and is still recorded: "
            + (partWay
                ? $"a mutation worker kept in it could not be removed - '{other}': its removal was stopped part way, and asking again finishes it"
                : "the removal of the mutation workers kept in it was stopped before each had been dealt with")
            + $"; 1 mutation worker(s) kept in it were removed before that, {DiskSpace.Size(went)}",
            deleted.Outcome.Details ?? []);
        Assert.False(Directory.Exists(free));
        Assert.True(Directory.Exists(other));
        Assert.True(Directory.Exists(onPi));
        Assert.Equal([Entry(onPi, created.Path)], record.Of(layout, "feature"));
    }

    /// <summary>
    /// Copies are kept under a worktree's name, and a worktree made by hand, or by another tool, outside the worktrees
    /// root can be kept under the same one. While it still exists its copies are left for it - deleting a name with no
    /// worktree in the root takes nothing of it - and once it is gone, deleting the name removes what it left.
    /// </summary>
    [Fact]
    public async Task AWorktreeOfTheNameElsewhere_KeepsItsCopiesWhileItExists()
    {
        using var temp = new TempDirectory();
        using var hosts = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        var layout = await LayoutAsync(harness, temp);
        var elsewhere = Tree(temp, "elsewhere", "feature");
        var onPi = hosts.Combine("repo.worktree-feature");
        await Local(harness).CreateRootAsync(onPi, CopyMark.Complete, cancellationToken);
        Record(harness).Claim(layout, Entry(onPi, elsewhere));
        var service = Service(harness, new HashSet<HostId> { Pi });

        var kept = await service.DeleteAsync(temp.Path, "feature", force: false, deleteEvidence: false, cancellationToken: cancellationToken);

        Assert.Equal(HarnessExit.Refused, kept.Outcome.ExitCode);
        Assert.Equal(
            $"No worktree named 'feature' is here or in git's record. The copies kept under that name on hosts are the worktree's at '{elsewhere}', "
            + "which still exists, so they were left for it.",
            kept.Outcome.Message);
        Assert.True(Directory.Exists(onPi));

        Directory.Delete(elsewhere);
        var removed = await service.DeleteAsync(temp.Path, "feature", force: false, deleteEvidence: false, cancellationToken: cancellationToken);

        Assert.True(removed.Succeeded, removed.Outcome.Message);
        Assert.Equal([$"ssh pi: removed its copy at '{onPi}', of the worktree that was at '{elsewhere}'"], removed.Outcome.Details);
        Assert.False(Directory.Exists(onPi));
        Assert.Empty(Record(harness).Of(layout, "feature"));
    }

    /// <summary>
    /// A copy a run is building in is never removed from under it: the removal takes the lock a leg takes, and a copy
    /// held stays, recorded, with the run that holds it named - and the deletion ends with the lock's own code. Once
    /// the run lets go, deleting the worktree again removes it.
    /// </summary>
    [Fact]
    public async Task ACopyARunIsUsing_Stays_UntilTheRunLetsGo()
    {
        using var temp = new TempDirectory();
        using var hosts = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        var layout = await LayoutAsync(harness, temp);
        var onPi = hosts.Combine("repo.worktree-feature");
        await Local(harness).CreateRootAsync(onPi, CopyMark.Complete, cancellationToken);

        var created = await harness.WorktreeService.CreateAsync(temp.Path, "feature", useRandomName: false, cancellationToken);
        Assert.True(created.Succeeded, created.Outcome.Message);
        Record(harness).Claim(layout, Entry(onPi, created.Path));

        var building = await Lock(harness).TryAcquireAsync(
            layout,
            new LockRequest { Host = Pi.ToString(), Tree = onPi, Variant = "linux-x86_64-debug", Scope = LockScope.TreeShared, RunId = RunId.New(), Command = "test" },
            cancellationToken);
        Assert.NotNull(building.Handle);

        var service = Service(harness, new HashSet<HostId> { Pi });
        var deleted = await service.DeleteAsync(temp.Path, "feature", force: false, deleteEvidence: false, cancellationToken: cancellationToken);

        Assert.Equal(HarnessExit.Refused, deleted.Outcome.ExitCode);
        Assert.Contains(deleted.Outcome.Details ?? [], line => line.StartsWith($"ssh pi: its copy at '{onPi}' stays, and is still recorded: ", StringComparison.Ordinal)
            && line.Contains("running 'test'", StringComparison.Ordinal));
        Assert.True(Directory.Exists(onPi));

        await building.Handle.DisposeAsync();
        var again = await service.DeleteAsync(temp.Path, "feature", force: false, deleteEvidence: false, cancellationToken: cancellationToken);

        Assert.True(again.Succeeded, again.Outcome.Message);
        Assert.Equal([$"ssh pi: removed its copy at '{onPi}'"], again.Outcome.Details);
        Assert.False(Directory.Exists(onPi));
    }

    /// <summary>
    /// The keys a leg's copy is recorded, locked and removed under are the ones the leg itself uses: its tree's name
    /// as a worktree's is spelt, its host as the command line names it, and the copy LegRunPlan placed it in. A leg
    /// naming a worktree is synced as a run syncs it, a run's lock held as a run holds it, and deleting the worktree
    /// is refused that copy until the run lets go - then removes it, and forgets it.
    /// </summary>
    [Fact]
    public async Task ALegsCopy_IsRecordedLockedAndRemoved_UnderTheKeysTheLegItselfUses()
    {
        using var temp = new TempDirectory();
        using var hosts = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        var context = await harness.ContextLoader.LoadAsync(temp.Path, cancellationToken);
        var layout = context.Layout;
        var created = await harness.WorktreeService.CreateAsync(temp.Path, "feature", useRandomName: false, cancellationToken);
        Assert.True(created.Succeeded, created.Outcome.Message);

        var placing = new HarnessContext(layout, new HarnessConfig
        {
            Worktrees = context.Config.Worktrees,
            SshItems = { "pi" },
            Hosts = new HostsConfig { Ssh = { ["pi"] = new SshHostConfig { RepositoryPath = hosts.Combine("repo") } } },
        });
        var selected = new SelectedLeg("remote", new LegConfig { Os = "linux", Processor = "x86_64", Config = "debug", Worktree = "feature" });
        var report = new HostReport { Host = Pi, Os = "linux", Processor = "x86_64" };
        var leg = Assert.Single(LegRunPlan.From(
            placing, new LegsReport([new LegPlacement(selected, report, null)], [report], Named: false), harness.Platform, out _));

        await Sync(harness).SyncAsync(
            leg.TreeRoot, new RecordingTransport(Local(harness), reports: leg.Host.Host), leg.HostTreeRoot, new SyncOptions(), cancellationToken);

        var recorded = Assert.Single(Record(harness).Of(layout, "feature"));
        Assert.Equal((Pi.ToString(), leg.HostTreeRoot), (recorded.Host, recorded.Path));
        Assert.True(Record(harness).SameTree(leg.TreeRoot, recorded.Tree));

        var building = await Lock(harness).TryAcquireAsync(
            layout,
            new LockRequest
            {
                Host = leg.Host.Host.ToString(),
                Tree = leg.HostTreeRoot,
                Variant = leg.Variant.DirectoryName,
                Scope = LockScope.TreeShared,
                RunId = RunId.New(),
                Command = "test",
            },
            cancellationToken);
        Assert.NotNull(building.Handle);

        var service = Service(harness, new HashSet<HostId> { Pi });
        var refused = await service.DeleteAsync(temp.Path, "feature", force: false, deleteEvidence: false, cancellationToken: cancellationToken);

        Assert.Equal(HarnessExit.Refused, refused.Outcome.ExitCode);
        Assert.True(Directory.Exists(leg.HostTreeRoot));

        await building.Handle.DisposeAsync();
        var removed = await service.DeleteAsync(temp.Path, "feature", force: false, deleteEvidence: false, cancellationToken: cancellationToken);

        Assert.True(removed.Succeeded, removed.Outcome.Message);
        Assert.False(Directory.Exists(leg.HostTreeRoot));
        Assert.Empty(Record(harness).Of(layout, "feature"));
    }

    /// <summary>
    /// A host that fails to remove its copy keeps it recorded, and the deletion ends with the highest code a copy was
    /// left with: here a failure there, over a host that could not be asked.
    /// </summary>
    [Fact]
    public async Task AHostThatFailsToRemoveItsCopy_KeepsItRecorded_AndTheDeletionEndsWithTheHighestCode()
    {
        using var temp = new TempDirectory();
        using var hosts = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        var layout = await LayoutAsync(harness, temp);
        var record = Record(harness);
        var onPi = hosts.Combine("pi", "repo.worktree-feature");
        await Local(harness).CreateRootAsync(onPi, CopyMark.Complete, cancellationToken);
        Directory.CreateDirectory(Path.Combine(onPi, "build"));

        var created = await harness.WorktreeService.CreateAsync(temp.Path, "feature", useRandomName: false, cancellationToken);
        Assert.True(created.Succeeded, created.Outcome.Message);
        record.Claim(layout, Entry(hosts.Combine("mac", "repo.worktree-feature"), created.Path, Mac));
        record.Claim(layout, Entry(onPi, created.Path));

        var service = Service(harness, new HashSet<HostId> { Pi }, new HoldsOpen(harness.FileSystem, "build"));
        var deleted = await service.DeleteAsync(temp.Path, "feature", force: false, deleteEvidence: false, cancellationToken: cancellationToken);

        Assert.Equal(HarnessExit.CommandFailed, deleted.Outcome.ExitCode);
        Assert.Contains("2 of its copies on hosts are not yet dealt with", deleted.Outcome.Message, StringComparison.Ordinal);
        Assert.Contains(deleted.Outcome.Details ?? [], line => line.StartsWith($"ssh pi: its copy at '{onPi}' stays, and is still recorded: '{onPi}' could not be removed whole", StringComparison.Ordinal));
        Assert.Equal(2, record.Of(layout, "feature").Count);
        Assert.True(Directory.Exists(onPi));
    }

    /// <summary>
    /// A record that cannot be written as each copy goes loses nothing that was done: every copy removed is said to
    /// be, and to be still recorded, and the deletion fails with the record's refusal; asked again, each host finds
    /// nothing to remove, and the record is brought in step.
    /// </summary>
    [Fact]
    public async Task ARecordThatCannotBeWritten_LosesNoLineOfWhatWasDone()
    {
        using var temp = new TempDirectory();
        using var hosts = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        var layout = await LayoutAsync(harness, temp);
        var onPi = hosts.Combine("pi", "repo.worktree-feature");
        var onMac = hosts.Combine("mac", "repo.worktree-feature");
        await Local(harness).CreateRootAsync(onPi, CopyMark.Complete, cancellationToken);
        await Local(harness).CreateRootAsync(onMac, CopyMark.Complete, cancellationToken);

        var created = await harness.WorktreeService.CreateAsync(temp.Path, "feature", useRandomName: false, cancellationToken);
        Assert.True(created.Succeeded, created.Outcome.Message);
        Record(harness).Claim(layout, Entry(onPi, created.Path));
        Record(harness).Claim(layout, Entry(onMac, created.Path, Mac));

        var both = new HashSet<HostId> { Pi, Mac };
        var deleted = await Service(harness, both, record: new WritesNoRecord(harness.FileSystem)).DeleteAsync(
            temp.Path, "feature", force: false, deleteEvidence: false, cancellationToken: cancellationToken);

        Assert.Equal(HarnessExit.Refused, deleted.Outcome.ExitCode);
        Assert.Contains("2 of its copies on hosts are not yet dealt with", deleted.Outcome.Message, StringComparison.Ordinal);
        Assert.Contains(deleted.Outcome.Details ?? [], line => line.StartsWith($"ssh pi: removed its copy at '{onPi}', and it could not be forgotten here", StringComparison.Ordinal));
        Assert.Contains(deleted.Outcome.Details ?? [], line => line.StartsWith($"ssh mac: removed its copy at '{onMac}', and it could not be forgotten here", StringComparison.Ordinal));
        Assert.False(Directory.Exists(onPi));
        Assert.False(Directory.Exists(onMac));

        var again = await Service(harness, both).DeleteAsync(temp.Path, "feature", force: false, deleteEvidence: false, cancellationToken: cancellationToken);

        Assert.True(again.Succeeded, again.Outcome.Message);
        Assert.Equal([$"ssh pi: found nothing at '{onPi}' to remove", $"ssh mac: found nothing at '{onMac}' to remove"], again.Outcome.Details);
        Assert.Empty(Record(harness).Of(layout, "feature"));
    }

    /// <summary>
    /// A record that cannot be written where nothing is asked to remove a copy - a host nothing declares any more,
    /// whose entry is forgotten before any host is reached - leaves the copies after it dealt with all the same: the
    /// refusal is said as that one copy not yet dealt with, and counted among them, rather than ending the deletion
    /// over the copies it still had to ask about.
    /// </summary>
    [Fact]
    public async Task AFailureForOneCopy_LeavesTheRestDealtWith_AndIsSaidAsThatCopy()
    {
        using var temp = new TempDirectory();
        using var hosts = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        var layout = await LayoutAsync(harness, temp);
        var gone = HostId.Ssh("retired");
        const string onRetired = "/home/dev/repo.worktree-feature";
        var onPi = hosts.Combine("pi", "repo.worktree-feature");
        await Local(harness).CreateRootAsync(onPi, CopyMark.Complete, cancellationToken);

        var created = await harness.WorktreeService.CreateAsync(temp.Path, "feature", useRandomName: false, cancellationToken);
        Assert.True(created.Succeeded, created.Outcome.Message);

        // The one that fails is recorded first, so the copy still to be asked about is the one lost if its refusal ends the asking.
        Record(harness).Claim(layout, Entry(onRetired, created.Path, gone));
        Record(harness).Claim(layout, Entry(onPi, created.Path));

        var deleted = await Service(harness, new HashSet<HostId> { Pi }, record: new WritesNoRecord(harness.FileSystem)).DeleteAsync(
            temp.Path, "feature", force: false, deleteEvidence: false, cancellationToken: cancellationToken);

        Assert.Equal(HarnessExit.Refused, deleted.Outcome.ExitCode);
        Assert.Contains("2 of its copies on hosts are not yet dealt with", deleted.Outcome.Message, StringComparison.Ordinal);
        Assert.Contains(
            deleted.Outcome.Details ?? [],
            line => line.StartsWith($"ssh retired: its copy at '{onRetired}' is not yet dealt with: ", StringComparison.Ordinal));
        Assert.Contains(
            deleted.Outcome.Details ?? [],
            line => line.StartsWith($"ssh pi: removed its copy at '{onPi}', and it could not be forgotten here", StringComparison.Ordinal));
        Assert.False(Directory.Exists(onPi));
    }

    /// <summary>
    /// An interruption while the hosts are asked stops the asking, says what was done before it - a copy removed then
    /// is no longer recorded, and nothing else would say it went - and leaves the rest recorded for the next request.
    /// </summary>
    [Fact]
    public async Task AnInterruptionWhileHostsAreAsked_SaysWhatWasDone_AndLeavesTheRestRecorded()
    {
        using var temp = new TempDirectory();
        using var hosts = new TempDirectory();
        using var interruption = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var harness = await PrepareAsync(temp);
        var layout = await LayoutAsync(harness, temp);
        var record = Record(harness);
        var onPi = hosts.Combine("pi", "repo.worktree-feature");
        var onMac = hosts.Combine("mac", "repo.worktree-feature");
        await Local(harness).CreateRootAsync(onPi, CopyMark.Complete, interruption.Token);
        await Local(harness).CreateRootAsync(onMac, CopyMark.Complete, interruption.Token);

        var created = await harness.WorktreeService.CreateAsync(temp.Path, "feature", useRandomName: false, interruption.Token);
        Assert.True(created.Succeeded, created.Outcome.Message);
        record.Claim(layout, Entry(onPi, created.Path));
        record.Claim(layout, Entry(onMac, created.Path, Mac));

        var inspector = new RecordingInspector(host =>
        {
            if (host == Mac)
            {
                interruption.Cancel();
            }

            return Answering(host);
        });

        var deleted = await Service(harness, inspector).DeleteAsync(temp.Path, "feature", force: false, deleteEvidence: false, cancellationToken: interruption.Token);

        Assert.Equal(HarnessExit.Cancelled, deleted.Outcome.ExitCode);
        Assert.Equal(
            "Worktree 'feature' was deleted, and the interruption came before every host holding a copy of it had been asked to remove it: "
            + "run 'dssharness delete-worktree feature' to ask the rest.",
            deleted.Outcome.Message);
        Assert.Equal([created.Path, $"ssh pi: removed its copy at '{onPi}'"], deleted.Outcome.Details);
        Assert.Equal([Entry(onMac, created.Path, Mac)], record.Of(layout, "feature"));
        Assert.True(Directory.Exists(onMac));
    }

    /// <summary>
    /// A record is read, not trusted: a path that is not where a worktree's copy is kept - here the main checkout's
    /// own copy - is never asked to be removed, and is forgotten, said as that.
    /// </summary>
    [Fact]
    public async Task ARecordedPathThatIsNoWorktreesCopy_IsNeverRemoved()
    {
        using var temp = new TempDirectory();
        using var hosts = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        var layout = await LayoutAsync(harness, temp);
        var mainCopy = hosts.Combine("repo");
        await Local(harness).CreateRootAsync(mainCopy, CopyMark.Complete, cancellationToken);

        var created = await harness.WorktreeService.CreateAsync(temp.Path, "feature", useRandomName: false, cancellationToken);
        Assert.True(created.Succeeded, created.Outcome.Message);
        Record(harness).Claim(layout, Entry(mainCopy, created.Path));

        var deleted = await Service(harness, new HashSet<HostId> { Pi }).DeleteAsync(
            temp.Path, "feature", force: false, deleteEvidence: false, cancellationToken: cancellationToken);

        Assert.True(deleted.Succeeded, deleted.Outcome.Message);
        Assert.Contains(
            $"ssh pi: '{mainCopy}' is not where a worktree's copy is kept, so nothing was asked to remove it, and it is no longer recorded",
            deleted.Outcome.Details ?? []);
        Assert.True(Directory.Exists(mainCopy));
        Assert.Empty(Record(harness).Of(layout, "feature"));
    }

    /// <summary>
    /// A host neither the worktree's own configuration nor the one the command runs in declares cannot be reached, now
    /// or later, so its copy is not asked about: it is forgotten, and named as left there, rather than failing every
    /// deletion of the name for good.
    /// </summary>
    [Fact]
    public async Task AHostNoLongerDeclared_HasItsCopyForgotten_AndNamed()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        var layout = await LayoutAsync(harness, temp);
        var gone = HostId.Ssh("retired");
        const string copy = "/home/dev/repo.worktree-feature";

        var created = await harness.WorktreeService.CreateAsync(temp.Path, "feature", useRandomName: false, cancellationToken);
        Assert.True(created.Succeeded, created.Outcome.Message);
        Record(harness).Claim(layout, Entry(copy, created.Path, gone));

        var inspector = new RecordingInspector(Answering);
        var deleted = await Service(harness, inspector).DeleteAsync(temp.Path, "feature", force: false, deleteEvidence: false, cancellationToken: cancellationToken);

        Assert.True(deleted.Succeeded, deleted.Outcome.Message);
        Assert.Contains(
            $"ssh retired: no configuration here declares it any more, so nothing was asked to remove its copy at '{copy}', which "
            + "stays there for you to remove, and is no longer recorded",
            deleted.Outcome.Details ?? []);
        Assert.Empty(inspector.Inspected);
        Assert.Empty(Record(harness).Of(layout, "feature"));
    }

    /// <summary>
    /// A host only the worktree's own branch declares is reached through the worktree's configuration, read before the
    /// worktree goes: the configuration the command runs in need not declare it, and its copy there is not forgotten
    /// as a host nothing declares.
    /// </summary>
    [Fact]
    public async Task AHostOnlyTheWorktreesOwnConfigurationDeclares_IsAskedThroughIt()
    {
        using var temp = new TempDirectory();
        using var hosts = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        var layout = await LayoutAsync(harness, temp);
        var gpu = HostId.Ssh("gpu");
        var onGpu = hosts.Combine("repo.worktree-feature");
        await Local(harness).CreateRootAsync(onGpu, CopyMark.Complete, cancellationToken);

        var created = await harness.WorktreeService.CreateAsync(temp.Path, "feature", useRandomName: false, cancellationToken);
        Assert.True(created.Succeeded, created.Outcome.Message);
        harness.WriteConfig(created.Path, new HarnessConfig
        {
            Worktrees = new WorktreeSettings { Root = ".wt", PathBudgetReserve = 5, PathBudgetMargin = 2 },
            SshItems = { "pi", "mac", "gpu" },
            Hosts = new HostsConfig
            {
                Ssh =
                {
                    ["pi"] = new SshHostConfig { RepositoryPath = "/home/pi/repo" },
                    ["mac"] = new SshHostConfig { RepositoryPath = "/Users/dev/repo" },
                    ["gpu"] = new SshHostConfig { RepositoryPath = "/srv/repo" },
                },
            },
        });
        Record(harness).Claim(layout, Entry(onGpu, created.Path, gpu));

        var inspector = new RecordingInspector(Answering);
        var deleted = await Service(harness, inspector).DeleteAsync(temp.Path, "feature", force: true, deleteEvidence: false, cancellationToken: cancellationToken);

        Assert.True(deleted.Succeeded, deleted.Outcome.Message);
        Assert.Contains($"ssh gpu: removed its copy at '{onGpu}'", deleted.Outcome.Details ?? []);
        Assert.Equal([gpu], inspector.Inspected);
        Assert.False(Directory.Exists(onGpu));
    }

    /// <summary>
    /// The record is read again under each copy's lock: a worktree of the same name elsewhere can claim a copy while
    /// the hosts before it are asked - as its own sync may, once the deleted worktree is gone - and a copy it holds
    /// then is left for it, recorded as its own, and never removed from under it.
    /// </summary>
    [Fact]
    public async Task ACopyAnotherWorktreeClaimsWhileTheHostsAreAsked_IsLeftForIt()
    {
        using var temp = new TempDirectory();
        using var hosts = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        var layout = await LayoutAsync(harness, temp);
        var record = Record(harness);
        var onMac = hosts.Combine("mac", "repo.worktree-feature");
        var onPi = hosts.Combine("pi", "repo.worktree-feature");
        await Local(harness).CreateRootAsync(onMac, CopyMark.Complete, cancellationToken);
        await Local(harness).CreateRootAsync(onPi, CopyMark.Complete, cancellationToken);

        var created = await harness.WorktreeService.CreateAsync(temp.Path, "feature", useRandomName: false, cancellationToken);
        Assert.True(created.Succeeded, created.Outcome.Message);
        record.Claim(layout, Entry(onMac, created.Path, Mac));
        record.Claim(layout, Entry(onPi, created.Path));
        var elsewhere = Tree(temp, "elsewhere", "feature");

        var inspector = new RecordingInspector(host =>
        {
            if (host == Mac)
            {
                Assert.Null(Record(harness).Claim(layout, Entry(onPi, elsewhere)));
            }

            return Answering(host);
        });

        var deleted = await Service(harness, inspector).DeleteAsync(temp.Path, "feature", force: false, deleteEvidence: false, cancellationToken: cancellationToken);

        Assert.True(deleted.Succeeded, deleted.Outcome.Message);
        Assert.Contains($"ssh mac: removed its copy at '{onMac}'", deleted.Outcome.Details ?? []);
        Assert.Contains($"ssh pi: left '{onPi}' in place: the worktree at '{elsewhere}' has claimed it since", deleted.Outcome.Details ?? []);
        Assert.True(Directory.Exists(onPi));
        Assert.Equal([Entry(onPi, elsewhere)], record.Of(layout, "feature"));
    }

    /// <summary>
    /// A worktree whose directory was removed by hand, which git still records, has its copies removed when its
    /// record is cleared: the deletion's last part runs on that path as on the ordinary one.
    /// </summary>
    [Fact]
    public async Task AWorktreeRemovedByHand_HasItsCopiesRemoved_WithGitsRecordOfIt()
    {
        using var temp = new TempDirectory();
        using var hosts = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        var layout = await LayoutAsync(harness, temp);
        var onPi = hosts.Combine("repo.worktree-feature");
        await Local(harness).CreateRootAsync(onPi, CopyMark.Complete, cancellationToken);

        var created = await harness.WorktreeService.CreateAsync(temp.Path, "feature", useRandomName: false, cancellationToken);
        Assert.True(created.Succeeded, created.Outcome.Message);
        Record(harness).Claim(layout, Entry(onPi, created.Path));
        harness.FileSystem.DeleteDirectory(created.Path);

        var deleted = await Service(harness, new HashSet<HostId> { Pi }).DeleteAsync(
            temp.Path, "feature", force: false, deleteEvidence: false, cancellationToken: cancellationToken);

        Assert.True(deleted.Succeeded, deleted.Outcome.Message);
        Assert.Contains($"ssh pi: removed its copy at '{onPi}'", deleted.Outcome.Details ?? []);
        Assert.False(Directory.Exists(onPi));
        Assert.Empty(Record(harness).Of(layout, "feature"));
    }

    /// <summary>
    /// A record that cannot be read removes nothing, and says so: the worktree is still deleted, the command fails,
    /// and the record is left as it was. A name with nothing here or in git's record is then not said to be no
    /// worktree, since whether a host holds a copy of one cannot be told.
    /// </summary>
    [Fact]
    public async Task AnUnreadableRecord_RemovesNothing_AndSaysSo()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        var layout = await LayoutAsync(harness, temp);
        var created = await harness.WorktreeService.CreateAsync(temp.Path, "feature", useRandomName: false, cancellationToken);
        Assert.True(created.Succeeded, created.Outcome.Message);
        var path = HostCopyRecord.PathOf(layout);
        Directory.CreateDirectory(layout.HostCopiesDirectory);
        File.WriteAllText(path, "not json");
        var service = Service(harness, new HashSet<HostId> { Pi });

        var deleted = await service.DeleteAsync(temp.Path, "feature", force: false, deleteEvidence: false, cancellationToken: cancellationToken);

        Assert.Equal(HarnessExit.Refused, deleted.Outcome.ExitCode);
        Assert.StartsWith("Worktree 'feature' was deleted, and none of its copies on hosts was removed: ", deleted.Outcome.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(created.Path));

        var unknown = await service.DeleteAsync(temp.Path, "nothing", force: false, deleteEvidence: false, cancellationToken: cancellationToken);

        Assert.Equal(HarnessExit.Refused, unknown.Outcome.ExitCode);
        Assert.StartsWith(
            "No worktree named 'nothing' is here or in git's record, and whether a host holds a copy of one cannot be told: ",
            unknown.Outcome.Message,
            StringComparison.Ordinal);
        Assert.Equal("not json", File.ReadAllText(path));
    }

    /// <summary>A name with no worktree, nothing in git's record and no copy recorded is no worktree at all.</summary>
    [Fact]
    public async Task ANameWithNothingRecordedAnywhere_IsNoWorktree()
    {
        using var temp = new TempDirectory();
        var harness = await PrepareAsync(temp);

        var deleted = await Service(harness, new HashSet<HostId>()).DeleteAsync(
            temp.Path, "nothing", force: false, deleteEvidence: false, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.Refused, deleted.Outcome.ExitCode);
        Assert.Equal("No worktree named 'nothing'.", deleted.Outcome.Message);
    }

    /// <summary>
    /// The far side lists the worktree copies it keeps beside the main copy - each directory named for the main copy
    /// and a worktree's name - with what each one's marker says and how much its files hold, spelt from the
    /// repositoryPath it was asked about, as a sync records a copy, and nothing else that directory holds.
    /// </summary>
    [Fact]
    public async Task TheFarSide_ListsTheCopiesBesideItsMainCopy_WithWhatEachMarkerSays_AndHowMuchEachHolds()
    {
        using var hosts = new TempDirectory();
        var harness = new HarnessFactory();
        var transport = Local(harness);
        var cancellationToken = TestContext.Current.CancellationToken;
        var main = hosts.Combine("src", "repo");

        await transport.CreateRootAsync(main + ".worktree-alpha", CopyMark.Complete, cancellationToken);
        File.WriteAllText(Path.Combine(main + ".worktree-alpha", "main.c"), "int main;");
        await transport.CreateRootAsync(main + ".worktree-beta", CopyMark.AdoptionStopped, cancellationToken);
        Directory.CreateDirectory(main + ".worktree-gamma");
        File.WriteAllText(Path.Combine(main + ".worktree-gamma", "work.txt"), "somebody's");
        Directory.CreateDirectory(Path.Combine(main + ".worktree-delta", HarnessLayout.DirectoryName));
        File.WriteAllText(Path.Combine(main + ".worktree-delta", HarnessLayout.DirectoryName, LocalSyncTransport.MarkerFileName), "{}");
        Directory.CreateDirectory(main);
        Directory.CreateDirectory(main + ".worktree-");
        Directory.CreateDirectory(hosts.Combine("src", "other.worktree-alpha"));
        File.WriteAllText(main + ".worktree-file", "a file, not a copy");

        var repositoryPath = OperatingSystem.IsWindows() ? main.Replace('\\', '/') : main;
        var found = await transport.ListCopiesAsync(repositoryPath, cancellationToken);

        Assert.Equal(["alpha", "beta", "delta", "gamma"], found.Select(copy => copy.Name));
        Assert.Equal([.. found.Select(copy => HostCopies.ForWorktree(repositoryPath, copy.Name))], found.Select(copy => copy.Path));
        Assert.Equal([CopyOrigin.Made, CopyOrigin.TakenOver, CopyOrigin.Unreadable, CopyOrigin.Unmarked], found.Select(copy => copy.Origin));
        Assert.Equal([.. found.Select(copy => harness.FileSystem.DirectorySize(hosts.Combine("src", "repo" + HostCopies.WorktreeSuffix + copy.Name)))], found.Select(copy => copy.Bytes));
        Assert.True(found[0].Bytes > "int main;".Length);
        Assert.Equal(Environment.MachineName, found[0].CreatedBy);
        Assert.NotNull(found[0].CreatedUtc);
        Assert.Contains("this build cannot read it", found[2].Problem, StringComparison.Ordinal);
        Assert.Null(found[3].CreatedBy);

        Assert.Empty(await transport.ListCopiesAsync(hosts.Combine("nowhere", "repo"), cancellationToken));
    }

    /// <summary>
    /// Mutation workers are kept in the tree they copy, in its slot for them: listed from there, by the names they are
    /// kept under, and never among the worktree copies - nor a worktree copy among them, nor a worker an earlier version
    /// of this tool kept beside the tree.
    /// </summary>
    [Fact]
    public async Task MutationWorkers_AreKeptInTheirTreesSlot_ListedApartFromWorktreeCopies()
    {
        using var hosts = new TempDirectory();
        var harness = new HarnessFactory();
        var transport = Local(harness);
        var cancellationToken = TestContext.Current.CancellationToken;
        var tree = hosts.Combine("src", "repo");

        await transport.CreateRootAsync(Worker(tree, "357e24cw-1"), CopyMark.Complete, cancellationToken);
        await transport.CreateRootAsync(Worker(tree, "357e24cw-2"), CopyMark.Complete, cancellationToken);
        await transport.CreateRootAsync(HostCopies.ForWorktree(tree, "alpha"), CopyMark.Complete, cancellationToken);
        await transport.CreateRootAsync(tree + ".mutation-357e24cw-3", CopyMark.Complete, cancellationToken);

        var workers = await transport.ListInSlotAsync(tree, HarnessLayout.MutationsDirectoryName, _ => true, cancellationToken);
        var worktrees = await transport.ListCopiesAsync(tree, cancellationToken);

        Assert.Equal(["357e24cw-1", "357e24cw-2"], workers.Select(copy => copy.Name));
        Assert.Equal([Path.Combine(tree, ".m", "357e24cw-1"), Path.Combine(tree, ".m", "357e24cw-2")], workers.Select(copy => copy.Path));
        Assert.All(workers, copy => Assert.Equal(CopyOrigin.Made, copy.Origin));
        Assert.Equal(["alpha"], worktrees.Select(copy => copy.Name));
        Assert.Empty(await transport.ListInSlotAsync(hosts.Combine("src", "other"), HarnessLayout.MutationsDirectoryName, _ => true, cancellationToken));
    }

    /// <summary>
    /// A mutation worker an earlier version of this tool kept beside a worktree's copy is named on from that copy's
    /// name, and is no worktree's copy: the listing of the copies beside the main one never holds it - nor weighs it,
    /// which a build directory of gigabytes makes slow - nor anything else spelt as no copy's name is.
    /// </summary>
    [Fact]
    public async Task AWorkerBesideAWorktreesCopy_IsNeverListedAsAWorktreesCopy_NorWeighed()
    {
        using var hosts = new TempDirectory();
        var harness = new HarnessFactory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var main = hosts.Combine("src", "repo");

        await Local(harness).CreateRootAsync(main + ".worktree-alpha", CopyMark.Complete, cancellationToken);
        await Local(harness).CreateRootAsync(main + ".worktree-alpha.mutation-357e24cw-1", CopyMark.Complete, cancellationToken);
        await Local(harness).CreateRootAsync(main + ".worktree-lead--a1", CopyMark.Complete, cancellationToken);
        await Local(harness).CreateRootAsync(main + ".worktree-lead--a1.mutation-357e24cs-2", CopyMark.Complete, cancellationToken);
        Directory.CreateDirectory(main + ".worktree-Mine");
        Directory.CreateDirectory(main + ".worktree-a_b");

        var weighed = new Weighs(harness.FileSystem);
        var found = await Local(harness, weighed).ListCopiesAsync(main, cancellationToken);

        Assert.Equal(["alpha", "lead--a1"], found.Select(copy => copy.Name));
        Assert.Equal(["repo.worktree-alpha", "repo.worktree-lead--a1"], weighed.Weighed.Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    /// <summary>A copy's name is lower-case letters and digits joined by hyphens, as a worktree's and an agent's are spelt, and nothing else.</summary>
    [Theory]
    [InlineData("alpha", true)]
    [InlineData("a1", true)]
    [InlineData("my-tree-2", true)]
    [InlineData("lead--a1", true)]
    [InlineData("", false)]
    [InlineData("Alpha", false)]
    [InlineData("a_b", false)]
    [InlineData("a b", false)]
    [InlineData("-alpha", false)]
    [InlineData("alpha-", false)]
    [InlineData("alpha.mutation-357e24cw-1", false)]
    [InlineData("alpha.beta", false)]
    [InlineData("alpha\n", false)]
    public void ACopysName_IsSpeltAsAWorktreesIs(string name, bool expected)
        => Assert.Equal(expected, HostCopies.IsCopyName(name));

    /// <summary>
    /// The mutation workers kept in a tree go with it, of whichever variant, a self-test's among them, with what an
    /// unfinished removal of one left aside: each a sync made is removed and its claim forgotten; one a sweep still
    /// running holds is left, as held; a directory under a worker's name that nothing says the harness made is left, as
    /// somebody's - as is one the harness took over, which was somebody's first; one whose marker cannot be read is left
    /// as not removed, since what it is cannot be told; and what in the slot is named as no worker, another tree's
    /// workers, what is named as the aside of no worker, and a worker an earlier version of this tool kept beside the
    /// tree are never touched, nor said. Asked only to measure, it says the same and removes nothing.
    /// </summary>
    [Fact]
    public async Task TheWorkersInATree_AreRemoved_ButForOneInUse_AndOneNobodyMade()
    {
        using var hosts = new TempDirectory();
        var harness = new HarnessFactory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var tree = hosts.Combine("src", "repo.worktree-alpha");
        var other = hosts.Combine("src", "repo");
        var swept = Worker(tree, "357e24cw-1");
        var selfTest = Worker(tree, "357e24cs-1");
        var held = Worker(tree, "c41a8a5w-1");
        var somebodys = Worker(tree, "357e24cw-5");
        var notes = Worker(tree, "notes");
        var takenOver = Worker(tree, "357e24cw-3");
        var damaged = Worker(tree, "357e24cw-4");
        var aside = RemovalAside.Of(Worker(tree, "357e24cw-2"));
        var anothers = Worker(other, "357e24cw-1");
        var anothersAside = RemovalAside.Of(Worker(other, "357e24cw-2"));
        var nobodys = Path.Combine(tree, HarnessLayout.MutationsDirectoryName, "." + RemovalAside.Suffix);
        var strangers = RemovalAside.Of(Worker(tree, "notes"));
        var beside = tree + ".mutation-357e24cw-6";

        foreach (var made in new[] { swept, selfTest, held, anothers, beside })
        {
            await Local(harness).CreateRootAsync(made, CopyMark.Complete, cancellationToken);
            File.WriteAllText(Path.Combine(made, "main.c"), "int main;");
        }

        foreach (var whose in new[] { somebodys, notes })
        {
            Directory.CreateDirectory(whose);
            File.WriteAllText(Path.Combine(whose, "notes.txt"), "somebody's");
        }

        await Local(harness).CreateRootAsync(takenOver, CopyMark.AdoptionStopped, cancellationToken);
        File.WriteAllText(Path.Combine(takenOver, "work.txt"), "somebody's, before it was a copy");
        Directory.CreateDirectory(Path.Combine(damaged, HarnessLayout.DirectoryName));
        File.WriteAllText(Path.Combine(damaged, HarnessLayout.DirectoryName, LocalSyncTransport.MarkerFileName), "{}");

        foreach (var left in new[] { aside, anothersAside, nobodys, strangers })
        {
            Directory.CreateDirectory(left);
            File.WriteAllText(Path.Combine(left, "part.o"), "what a removal left");
        }

        var claims = new Claims { Held = { [held] = "run 20261007-101500-abcd, process 4242 on this machine" } };
        var transport = Local(harness, claims: claims);
        var sizes = new[] { selfTest, swept, aside }.Select(harness.FileSystem.DirectorySize).ToList();

        var expected = new[] { selfTest, swept, aside }.Zip(sizes, (path, bytes) => new WorkerRemoved(path, bytes)).ToList();
        var unreadable = Assert.Single(
            await transport.ListInSlotAsync(tree, HarnessLayout.MutationsDirectoryName, _ => true, cancellationToken),
            copy => copy.Origin == CopyOrigin.Unreadable);
        var left_ = new[]
        {
            new WorkerLeft(takenOver, "the harness took over a directory that was there, which is yours to remove", WorkerLeftAs.Somebodys),
            new WorkerLeft(damaged, $"its marker cannot be read: {unreadable.Problem!.TrimEnd('.')}", WorkerLeftAs.NotRemoved),
            new WorkerLeft(somebodys, "nothing there says the harness made it, so it is yours to remove", WorkerLeftAs.Somebodys),
            new WorkerLeft(held, "a sweep still running holds it: run 20261007-101500-abcd, process 4242 on this machine", WorkerLeftAs.Held),
        };

        var measured = await transport.RemoveWorkersAsync(tree, measureOnly: true, cancellationToken);

        Assert.Equal(expected, measured.Removed);
        Assert.Equal(left_, measured.Left);
        Assert.All(new[] { swept, selfTest, held, somebodys, takenOver, damaged, aside }, path => Assert.True(Directory.Exists(path), path));
        Assert.Empty(claims.Forgotten);

        var removed = await transport.RemoveWorkersAsync(tree, cancellationToken: cancellationToken);

        Assert.Equal(expected, removed.Removed);
        Assert.Equal(left_, removed.Left);
        Assert.Equal(sizes.Sum(), removed.Bytes);
        Assert.Equal(left_[3], removed.Kept);
        Assert.Equal(LegVerdict.RefusedLocked, removed.Verdict);
        Assert.False(removed.Interrupted);
        Assert.All(new[] { swept, selfTest, aside }, path => Assert.False(Directory.Exists(path), path));
        Assert.All(
            new[] { held, somebodys, notes, takenOver, damaged, anothers, anothersAside, nobodys, strangers, beside },
            path => Assert.True(Directory.Exists(path), path));
        Assert.Equal([selfTest, swept], claims.Forgotten);

        // No slot in a tree, or no tree: nothing to remove, and nothing said.
        Assert.Equal((0, 0), Counted(await transport.RemoveWorkersAsync(hosts.Combine("src", "another"), cancellationToken: cancellationToken)));
        Assert.Equal((0, 0), Counted(await transport.RemoveWorkersAsync(hosts.Combine("nowhere", "repo"), cancellationToken: cancellationToken)));

        static (int, int) Counted(WorkersRemoval removal) => (removal.Removed.Count, removal.Left.Count);
    }

    /// <summary>
    /// A worker whose marker says somebody's by the time it is removed - taken over since it was listed, or its marker
    /// gone - is left where it is and said as which: never said removed, nor its claim forgotten, for a directory that
    /// is still there.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AWorkerNoLongerTheHarnesssByTheTimeItIsRemoved_IsLeft_AndNeverSaidRemoved(bool takenOver)
    {
        using var hosts = new TempDirectory();
        var harness = new HarnessFactory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var tree = hosts.Combine("repo");
        var worker = Worker(tree, "357e24cw-1");
        var marker = Path.Combine(worker, HarnessLayout.DirectoryName, LocalSyncTransport.MarkerFileName);

        await Local(harness).CreateRootAsync(worker, CopyMark.Complete, cancellationToken);
        File.WriteAllText(Path.Combine(worker, "main.c"), "int main;");

        // Asked about between its listing and its removal, which is when its marker changes hands, or goes.
        var claims = new Claims
        {
            Asked = _ =>
            {
                if (takenOver)
                {
                    File.WriteAllText(marker, """{"CreatedUtc":"2026-10-07T12:00:00Z","CreatedBy":"builder","Adopted":true,"Completed":true}""");
                }
                else
                {
                    File.Delete(marker);
                }
            },
        };

        var removal = await Local(harness, claims: claims).RemoveWorkersAsync(tree, cancellationToken: cancellationToken);

        Assert.Empty(removal.Removed);
        Assert.Equal(
            [
                new WorkerLeft(
                    worker,
                    takenOver ? "the harness took over a directory that was there, which is yours to remove" : "nothing there says the harness made it, so it is yours to remove",
                    WorkerLeftAs.Somebodys),
            ],
            removal.Left);
        Assert.True(File.Exists(Path.Combine(worker, "main.c")));
        Assert.Empty(claims.Forgotten);
    }

    /// <summary>
    /// What an unfinished removal left aside that still cannot be removed - something holds it, or it is not this
    /// user's to remove - is left as not removed, saying why, and never as held by a sweep, which none holds it; it
    /// keeps no worker from going, and asking again removes it.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhatARemovalLeftAside_AndStillCannotRemove_IsLeftAsNotRemoved_AndTheWorkersGo(bool denied)
    {
        using var hosts = new TempDirectory();
        var harness = new HarnessFactory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var tree = hosts.Combine("repo");
        var free = Worker(tree, "357e24cw-1");
        var aside = RemovalAside.Of(Worker(tree, "357e24cw-2"));

        await Local(harness).CreateRootAsync(free, CopyMark.Complete, cancellationToken);
        Directory.CreateDirectory(aside);
        File.WriteAllText(Path.Combine(aside, "part.o"), "what a removal left");

        var removal = await Local(harness, new HoldsOpen(harness.FileSystem, Path.GetFileName(aside), denied)).RemoveWorkersAsync(tree, cancellationToken: cancellationToken);

        Assert.Equal([free], removal.Removed.Select(worker => worker.Path));

        var left = Assert.Single(removal.Left);

        Assert.Equal((aside, WorkerLeftAs.NotRemoved), (left.Path, left.As));
        Assert.Equal($"what a removal left aside could not be removed: The process cannot access '{aside}' because it is being used by another process.", left.Why);
        Assert.Equal(left, removal.Kept);
        Assert.Equal(LegVerdict.Failed, removal.Verdict);
        Assert.True(Directory.Exists(aside));

        Assert.Equal([aside], (await Local(harness).RemoveWorkersAsync(tree, cancellationToken: cancellationToken)).Removed.Select(worker => worker.Path));
        Assert.False(Directory.Exists(aside));
    }

    /// <summary>
    /// A tree named from this machine's home, as a host's repositoryPath is, has its workers' claims asked about where
    /// this machine keeps them, which is where a sweep claimed them: one a sweep still running holds is left, and one
    /// removed has its claim forgotten there - each still answered for as the caller spelt its tree.
    /// </summary>
    [Fact]
    public async Task AWorkerOfATreeNamedFromHome_IsAskedAboutWhereThisMachineKeepsIt()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        Assert.SkipWhen(home.Length == 0, "this account has no home, so no path is read from one");

        var harness = new HarnessFactory();
        var cancellationToken = TestContext.Current.CancellationToken;

        // Kept in the account's home, which is where a path from home is read from: the suite's temporary root is there
        // on most machines - told as this platform compares paths, since what is spelt from home below is cut from the
        // path by its length - and where it is not, a directory of this test's own is, removed with it. A home that
        // cannot be written to has nowhere to keep it, which is the machine's and nothing this test is about.
        using var hosts = InHome(home, harness.Platform.PathComparison);

        Assert.SkipWhen(hosts is null, $"nothing can be kept in this account's home, '{home}', so no path is read from one");
        var tree = hosts.Combine("src", "repo");
        var spelt = "~" + tree[home.Length..].Replace('\\', '/');
        var held = Worker(tree, "357e24cw-1");
        var free = Worker(tree, "357e24cw-2");

        foreach (var made in new[] { held, free })
        {
            await Local(harness).CreateRootAsync(made, CopyMark.Complete, cancellationToken);
            File.WriteAllText(Path.Combine(made, "main.c"), "int main;");
        }

        var bytes = harness.FileSystem.DirectorySize(free);
        var claims = new Claims { Held = { [held] = "run 20261007-101500-abcd, process 4242 on this machine" } };

        var removal = await Local(harness, claims: claims).RemoveWorkersAsync(spelt, cancellationToken: cancellationToken);

        Assert.StartsWith("~/", spelt, StringComparison.Ordinal);
        Assert.Equal([new WorkerRemoved(Worker(spelt, "357e24cw-2"), bytes)], removal.Removed);
        Assert.Equal(
            [new WorkerLeft(Worker(spelt, "357e24cw-1"), "a sweep still running holds it: run 20261007-101500-abcd, process 4242 on this machine", WorkerLeftAs.Held)],
            removal.Left);
        Assert.True(Directory.Exists(held), held);
        Assert.False(Directory.Exists(free), free);
        Assert.Equal([free], claims.Forgotten);
    }

    /// <summary>
    /// A directory of a test's own within <paramref name="home"/> - under the suite's temporary root where that is in
    /// it, as <paramref name="comparison"/> tells, and directly in it otherwise - or <see langword="null"/> where
    /// nothing can be made there.
    /// </summary>
    private static TempDirectory? InHome(string home, StringComparison comparison)
    {
        try
        {
            return PathContainment.IsStrictlyInside(home, TestHost.TemporaryRoot, comparison)
                ? new TempDirectory()
                : new TempDirectory(home, ".dssharness-test-");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// The transport this machine is reached through as a host knows the claims sweeps hold too: asked to remove a
    /// tree's workers, it leaves one a sweep still running holds.
    /// </summary>
    [Fact]
    public async Task ThisMachineReachedAsAHost_LeavesAWorkerASweepHolds()
    {
        using var hosts = new TempDirectory();
        var harness = new HarnessFactory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var tree = hosts.Combine("repo");
        var held = Worker(tree, "357e24cw-1");

        await Local(harness).CreateRootAsync(held, CopyMark.Complete, cancellationToken);

        var factory = new SyncTransportFactory(
            harness.FileSystem,
            new ManifestBuilder(harness.FileSystem, harness.Platform),
            harness.GitClient,
            Substitute.For<IHostCommandRunner>(),
            harness.Platform,
            harness.Output,
            new Claims { Held = { [held] = "pid 1, run r" } });

        var removal = await factory.For(new HostReport { Host = HostId.Local }).RemoveWorkersAsync(tree, cancellationToken: cancellationToken);

        Assert.Empty(removal.Removed);
        Assert.Equal([new WorkerLeft(held, "a sweep still running holds it: pid 1, run r", WorkerLeftAs.Held)], removal.Left);
        Assert.True(Directory.Exists(held));
    }

    /// <summary>
    /// The removal of the copies kept in a tree is one, for whichever slot it is given: it takes the slot and the
    /// family's own claims, removes each copy in that slot a sync made with what an earlier removal of one left aside,
    /// leaves one a run holds in the claims' own words and a directory nothing says a sync made as somebody's, and
    /// touches no copy in another slot - a mutation worker in the same tree stays, whatever the claims the transport was
    /// built with say - nor what was left aside under a name the family's claims do not take.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheRemovalOfASlotsCopies_TakesTheSlotAndItsClaims_AndNoOtherSlotsCopy(bool measureOnly)
    {
        using var hosts = new TempDirectory();
        var harness = new HarnessFactory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var tree = hosts.Combine("repo");
        string Spare(string name) => HarnessLayout.InSlot(tree, ".spares", name);
        var (free, held, worker, somebodys) = (Spare("1"), Spare("2"), Worker(tree, "357e24cw-1"), Spare("3"));
        var (aside, notOnes, workers) = (RemovalAside.Of(Spare("4")), RemovalAside.Of(Spare("notes")), RemovalAside.Of(Worker(tree, "357e24cw-2")));
        var ofWorkers = new Claims { Held = { [free] = "pid 9, run w" } };
        var spares = new SpareClaims { Held = { [held] = "pid 1, run r" } };
        var local = Local(harness, claims: ofWorkers);

        foreach (var copy in new[] { free, held, worker })
        {
            await local.CreateRootAsync(copy, CopyMark.Complete, cancellationToken);
        }

        foreach (var directory in new[] { somebodys, aside, notOnes, workers })
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "left.txt"), "seven-7");
        }

        var removal = await local.RemoveInSlotAsync(tree, ".spares", spares, measureOnly, cancellationToken);

        Assert.Equal([free, aside], removal.Removed.Select(copy => copy.Path));
        Assert.Equal(7, removal.Removed[1].Bytes);
        Assert.Equal(
            [(held, "a run still running holds it: pid 1, run r", WorkerLeftAs.Held), (somebodys, removal.Left[^1].Why, WorkerLeftAs.Somebodys)],
            removal.Left.Select(left => (left.Path, left.Why, left.As)));
        Assert.False(removal.Interrupted);

        // Measured, nothing goes; removed, the family's own go, and its claims alone are told so.
        Assert.Equal(measureOnly, Directory.Exists(free));
        Assert.Equal(measureOnly, Directory.Exists(aside));
        Assert.Equal(measureOnly ? [] : [Path.GetFullPath(free)], spares.Forgotten);
        Assert.Empty(ofWorkers.Forgotten);

        // The copy a run holds, somebody's directory, the worker of the other family and what a removal of one left
        // aside, and an aside spelt as no copy of this family, all stay.
        Assert.All(new[] { held, somebodys, worker, workers, notOnes }, kept => Assert.True(Directory.Exists(kept)));
    }

    /// <summary>
    /// A worker whose removal stops at something held is left as not removed, saying what stopped it, and keeps no
    /// other worker from going; asking again, once nothing holds it, removes the rest of it.
    /// </summary>
    [Fact]
    public async Task AWorkerWhoseRemovalStopsPartWay_IsLeftAsNotRemoved_AndTheOthersGo()
    {
        using var hosts = new TempDirectory();
        var harness = new HarnessFactory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var tree = hosts.Combine("repo");
        var stuck = Worker(tree, "357e24cw-1");
        var free = Worker(tree, "357e24cw-2");

        await Local(harness).CreateRootAsync(stuck, CopyMark.Complete, cancellationToken);
        Directory.CreateDirectory(Path.Combine(stuck, "build"));
        File.WriteAllText(Path.Combine(stuck, "build", "held.o"), "held open");
        await Local(harness).CreateRootAsync(free, CopyMark.Complete, cancellationToken);

        var claims = new Claims();
        var removal = await Local(harness, new HoldsOpen(harness.FileSystem, "build"), claims).RemoveWorkersAsync(tree, cancellationToken: cancellationToken);

        Assert.Equal([free], removal.Removed.Select(worker => worker.Path));

        var left = Assert.Single(removal.Left);

        Assert.Equal((stuck, WorkerLeftAs.NotRemoved), (left.Path, left.As));
        Assert.Contains($"'{stuck}' could not be removed whole", left.Why, StringComparison.Ordinal);
        Assert.Equal(LegVerdict.Failed, removal.Verdict);
        Assert.Equal([free], claims.Forgotten);
        Assert.True(Directory.Exists(stuck));

        Assert.Equal([stuck], (await Local(harness, claims: claims).RemoveWorkersAsync(tree, cancellationToken: cancellationToken)).Removed.Select(worker => worker.Path));
        Assert.False(Directory.Exists(stuck));
    }

    /// <summary>
    /// A worker gone by the time it is removed - another command took it meanwhile - is neither said removed nor
    /// counted: this removal took nothing of it. The claim on a copy that is gone is forgotten, as with one removed.
    /// </summary>
    [Fact]
    public async Task AWorkerGoneByTheTimeItIsRemoved_IsNeitherRemovedNorCounted()
    {
        using var hosts = new TempDirectory();
        var harness = new HarnessFactory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var tree = hosts.Combine("repo");
        var taken = Worker(tree, "357e24cw-1");
        var free = Worker(tree, "357e24cw-2");

        foreach (var worker in new[] { taken, free })
        {
            await Local(harness).CreateRootAsync(worker, CopyMark.Complete, cancellationToken);
            File.WriteAllText(Path.Combine(worker, "main.c"), "int main;");
        }

        var claims = new Claims { Asked = copy => { if (Path.GetFullPath(copy) == taken) { Directory.Delete(taken, recursive: true); } } };
        var removal = await Local(harness, claims: claims).RemoveWorkersAsync(tree, cancellationToken: cancellationToken);

        Assert.Equal([free], removal.Removed.Select(worker => worker.Path));
        Assert.Empty(removal.Left);
        Assert.False(removal.Interrupted);
        Assert.Equal([taken, free], claims.Forgotten);
    }

    /// <summary>
    /// A worker whose marker cannot be read by the time it is removed is that worker's alone to answer for: it is left
    /// as not removed, saying why, whole, with its claim, and the others go - never the end of the removal, which would
    /// lose what had gone by then.
    /// </summary>
    [Fact]
    public async Task AWorkerWhoseMarkerCannotBeReadAsItIsRemoved_IsLeftAsNotRemoved_AndTheOthersGo()
    {
        using var hosts = new TempDirectory();
        var harness = new HarnessFactory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var tree = hosts.Combine("repo");
        var first = Worker(tree, "357e24cw-1");
        var damaged = Worker(tree, "357e24cw-2");
        var last = Worker(tree, "357e24cw-3");
        var marker = Path.Combine(damaged, HarnessLayout.DirectoryName, LocalSyncTransport.MarkerFileName);

        foreach (var worker in new[] { first, damaged, last })
        {
            await Local(harness).CreateRootAsync(worker, CopyMark.Complete, cancellationToken);
            File.WriteAllText(Path.Combine(worker, "main.c"), "int main;");
        }

        var claims = new Claims { Asked = copy => { if (Path.GetFullPath(copy) == damaged) { File.WriteAllText(marker, "{}"); } } };
        var removal = await Local(harness, claims: claims).RemoveWorkersAsync(tree, cancellationToken: cancellationToken);

        Assert.Equal([first, last], removal.Removed.Select(worker => worker.Path));

        var left = Assert.Single(removal.Left);

        Assert.Equal((damaged, WorkerLeftAs.NotRemoved), (left.Path, left.As));
        Assert.Contains("this build cannot read it", left.Why, StringComparison.Ordinal);
        Assert.False(removal.Interrupted);
        Assert.True(File.Exists(Path.Combine(damaged, "main.c")));
        Assert.Equal([first, last], claims.Forgotten);
    }

    /// <summary>
    /// A removal stopped before every worker was dealt with answers, and never throws what it had done away: the workers
    /// gone by then are said removed, one it was stopped in is left as not removed - still marked, so asking again
    /// finishes it - and it says it was stopped, so the one not reached is not taken for dealt with.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ARemovalStoppedBeforeEveryWorkerWasDealtWith_AnswersWhatWent_AndThatItWasStopped(bool partWay)
    {
        using var hosts = new TempDirectory();
        var harness = new HarnessFactory();
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var tree = hosts.Combine("repo");
        var first = Worker(tree, "357e24cw-1");
        var second = Worker(tree, "357e24cw-2");

        foreach (var worker in new[] { first, second })
        {
            await Local(harness).CreateRootAsync(worker, CopyMark.Complete, TestContext.Current.CancellationToken);
            File.WriteAllText(Path.Combine(worker, "main.c"), "int main;");
        }

        // Stopped as the second is asked about, which is once the first is gone and before anything of the second is - or
        // as the first is gone, between the two.
        var claims = new Claims { Asked = copy => { if (partWay && Path.GetFullPath(copy) == second) { stopping.Cancel(); } } };
        var disk = partWay ? harness.FileSystem : new InterruptsOnceRemoved(harness.FileSystem, first, stopping);

        var removal = await Local(harness, disk, claims).RemoveWorkersAsync(tree, cancellationToken: stopping.Token);

        Assert.True(removal.Interrupted);
        Assert.Equal([first], removal.Removed.Select(worker => worker.Path));
        Assert.Equal(partWay ? [(second, WorkerLeftAs.NotRemoved)] : [], removal.Left.Select(worker => (worker.Path, worker.As)));
        Assert.All(removal.Left, worker => Assert.Equal("its removal was stopped part way, and asking again finishes it", worker.Why));
        Assert.True(File.Exists(Path.Combine(second, HarnessLayout.DirectoryName, LocalSyncTransport.MarkerFileName)));
        Assert.Equal([first], claims.Forgotten);
    }

    /// <summary>
    /// A worker the disk will not list as it is removed - its marker gone by then, so what it holds has to be looked
    /// through - is that worker's alone to answer for, as one the harness refuses is: left as not removed, saying what
    /// the disk said, with its claim, and the others go.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AWorkerTheDiskWillNotListAsItIsRemoved_IsLeftAsNotRemoved_AndTheOthersGo(bool denied)
    {
        using var hosts = new TempDirectory();
        var harness = new HarnessFactory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var tree = hosts.Combine("repo");
        var first = Worker(tree, "357e24cw-1");
        var unlisted = Worker(tree, "357e24cw-2");
        var last = Worker(tree, "357e24cw-3");
        var marker = Path.Combine(unlisted, HarnessLayout.DirectoryName, LocalSyncTransport.MarkerFileName);

        foreach (var worker in new[] { first, unlisted, last })
        {
            await Local(harness).CreateRootAsync(worker, CopyMark.Complete, cancellationToken);
            File.WriteAllText(Path.Combine(worker, "main.c"), "int main;");
        }

        var claims = new Claims { Asked = copy => { if (Path.GetFullPath(copy) == unlisted) { File.Delete(marker); } } };
        var removal = await Local(harness, new WillNotListFiles(harness.FileSystem, unlisted, denied), claims).RemoveWorkersAsync(tree, cancellationToken: cancellationToken);

        Assert.Equal([first, last], removal.Removed.Select(worker => worker.Path));

        var left = Assert.Single(removal.Left);

        Assert.Equal((unlisted, WorkerLeftAs.NotRemoved, "the disk would not say."), (left.Path, left.As, left.Why));
        Assert.False(removal.Interrupted);
        Assert.True(File.Exists(Path.Combine(unlisted, "main.c")));
        Assert.Equal([first, last], claims.Forgotten);
    }

    /// <summary>A disk that will not say which files one directory holds, at any depth.</summary>
    private sealed class WillNotListFiles(IFileSystem inner, string directory, bool denied) : PassThroughFileSystem(inner)
    {
        public override IEnumerable<string> EnumerateFiles(string path, bool recursive)
            => !recursive || !string.Equals(Path.GetFullPath(path), Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase) ? base.EnumerateFiles(path, recursive)
                : denied ? throw new UnauthorizedAccessException("the disk would not say.")
                : throw new IOException("the disk would not say.");
    }

    /// <summary>
    /// Everything is listed before anything goes: a removal that cannot list what an earlier one left aside has removed
    /// no worker by then, and fails naming the directory that could not be looked in - as a failure of the harness's,
    /// which is all that comes back from a host.
    /// </summary>
    [Fact]
    public async Task ARemovalThatCannotListWhatWasLeftAside_HasRemovedNothing_AndNamesWhereItLooked()
    {
        using var hosts = new TempDirectory();
        var harness = new HarnessFactory();
        var tree = hosts.Combine("repo");
        var worker = Worker(tree, "357e24cw-1");

        await Local(harness).CreateRootAsync(worker, CopyMark.Complete, TestContext.Current.CancellationToken);

        // The workers are listed, and the slot will not be read again for what was left aside.
        var slot = Path.Combine(tree, HarnessLayout.MutationsDirectoryName);
        var disk = new ListsOnce(harness.FileSystem, slot);

        var failure = await Assert.ThrowsAsync<HarnessException>(
            () => Local(harness, disk).RemoveWorkersAsync(tree, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(HarnessExit.CommandFailed, failure.ExitCode);
        Assert.Equal($"'{slot}' could not be looked in: the disk would not say", failure.Message);
        Assert.True(Directory.Exists(worker));
    }

    /// <summary>
    /// Everything is listed before anything goes in the copies kept in the tree's .legs too: where the slot of such a copy
    /// will not be read, the removal fails naming it, and the tree's own worker is still there - a host answers a failure
    /// alone, so a worker that went before it would be one nothing said went.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ARemovalThatCannotListTheSlotOfACopyInTheTreesLegs_HasRemovedNothing_AndNamesWhereItLooked(bool denied)
    {
        using var hosts = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = new HarnessFactory();
        var tree = hosts.Combine("repo");
        var copy = HarnessLayout.InSlot(tree, HarnessLayout.LegsDirectoryName, "357e24c");
        var own = Worker(tree, "357e24cw-1");
        var slot = Path.Combine(copy, HarnessLayout.MutationsDirectoryName);

        await Local(harness).CreateRootAsync(tree, CopyMark.Complete, cancellationToken);
        await Local(harness).CreateRootAsync(copy, CopyMark.Complete, cancellationToken);
        await Local(harness).CreateRootAsync(own, CopyMark.Complete, cancellationToken);
        Directory.CreateDirectory(slot);

        var failure = await Assert.ThrowsAsync<HarnessException>(
            () => Local(harness, new CannotList(harness.FileSystem, slot, denied)).RemoveWorkersAsync(tree, cancellationToken: cancellationToken));

        Assert.Equal((HarnessExit.CommandFailed, $"'{slot}' could not be looked in: the disk would not say"), (failure.ExitCode, failure.Message));
        Assert.True(Directory.Exists(own), "a worker went before every place was listed");
    }

    /// <summary>
    /// A host whose workers cannot be listed - the slot of its copy they are kept in will not be read - keeps its copy,
    /// still recorded, saying why, as any other failure there does: the deletion still says what it did.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AHostWhoseWorkersCannotBeListed_KeepsItsCopy_SayingWhy(bool denied)
    {
        using var temp = new TempDirectory();
        using var hosts = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        var layout = await LayoutAsync(harness, temp);
        var record = Record(harness);
        var onPi = hosts.Combine("pi", "repo.worktree-feature");
        var slot = Path.Combine(onPi, HarnessLayout.MutationsDirectoryName);

        await Local(harness).CreateRootAsync(onPi, CopyMark.Complete, cancellationToken);
        await Local(harness).CreateRootAsync(Worker(onPi, "357e24cw-1"), CopyMark.Complete, cancellationToken);

        var created = await harness.WorktreeService.CreateAsync(temp.Path, "feature", useRandomName: false, cancellationToken);
        Assert.True(created.Succeeded, created.Outcome.Message);
        record.Claim(layout, Entry(onPi, created.Path));

        var deleted = await Service(harness, new HashSet<HostId> { Pi }, hostDisk: new CannotList(harness.FileSystem, slot, denied))
            .DeleteAsync(temp.Path, "feature", force: false, deleteEvidence: false, cancellationToken: cancellationToken);

        Assert.Equal(HarnessExit.CommandFailed, deleted.Outcome.ExitCode);
        Assert.Equal(
            $"ssh pi: its copy at '{onPi}' stays, and is still recorded: '{slot}' could not be looked in: the disk would not say",
            Assert.Single(deleted.Outcome.Details ?? [], said => said.StartsWith("ssh pi: ", StringComparison.Ordinal)));
        Assert.False(Directory.Exists(created.Path));
        Assert.True(Directory.Exists(onPi));
        Assert.Equal([Entry(onPi, created.Path)], record.Of(layout, "feature"));
    }

    /// <summary>
    /// A removal stopped once its workers are gone, and before what an earlier one left aside is reached, leaves that
    /// aside for asking again, and answers that it was stopped.
    /// </summary>
    [Fact]
    public async Task ARemovalStoppedOnceItsWorkersAreGone_LeavesWhatAnEarlierOneLeftAside()
    {
        using var hosts = new TempDirectory();
        var harness = new HarnessFactory();
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var tree = hosts.Combine("repo");
        var worker = Worker(tree, "357e24cw-1");
        var aside = RemovalAside.Of(Worker(tree, "357e24cw-2"));

        await Local(harness).CreateRootAsync(worker, CopyMark.Complete, TestContext.Current.CancellationToken);
        Directory.CreateDirectory(aside);
        File.WriteAllText(Path.Combine(aside, "part.o"), "what a removal left");

        var removal = await Local(harness, new InterruptsOnceRemoved(harness.FileSystem, worker, stopping)).RemoveWorkersAsync(tree, cancellationToken: stopping.Token);

        Assert.True(removal.Interrupted);
        Assert.Equal([worker], removal.Removed.Select(removed => removed.Path));
        Assert.Empty(removal.Left);
        Assert.True(Directory.Exists(aside));
        Assert.Equal(LegVerdict.Stopped, removal.Verdict);
    }

    /// <summary>
    /// A transport is never built without the claims its workers' removal asks about: built without, it would remove a
    /// worker from under the sweep mutating it.
    /// </summary>
    [Fact]
    public void ATransport_IsNeverBuiltWithoutTheClaimsOnItsWorkers()
    {
        var harness = new HarnessFactory();

        Assert.Throws<ArgumentNullException>(() => new LocalSyncTransport(
            harness.FileSystem,
            new ManifestBuilder(harness.FileSystem, harness.Platform),
            harness.GitClient,
            harness.Platform,
            null!));
    }

    /// <summary>
    /// A host whose removal of a copy's workers was stopped there - by nothing this command did - keeps its copy, still
    /// recorded, saying which went: it is no interruption of this command, which ends as a copy not yet dealt with does.
    /// </summary>
    [Fact]
    public async Task AHostWhoseRemovalOfWorkersWasStoppedThere_KeepsItsCopy_AndIsNoInterruptionOfThisCommand()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        var layout = await LayoutAsync(harness, temp);
        var record = Record(harness);
        const string onPi = "/home/dev/repo.worktree-feature";
        var transport = Substitute.For<ISyncTransport>();
        var transports = Substitute.For<ISyncTransportFactory>();

        transport.RemoveWorkersAsync(onPi, true, Arg.Any<CancellationToken>()).Returns(WorkersRemoval.None);
        transport.RemoveWorkersAsync(onPi, false, Arg.Any<CancellationToken>())
            .Returns(new WorkersRemoval([new WorkerRemoved(Worker(onPi, "357e24cw-1"), 2048)], []) { Interrupted = true });
        transports.For(Arg.Any<HostReport>()).Returns(transport);

        var created = await harness.WorktreeService.CreateAsync(temp.Path, "feature", useRandomName: false, cancellationToken);
        Assert.True(created.Succeeded, created.Outcome.Message);
        record.Claim(layout, Entry(onPi, created.Path));

        var service = new WorktreeService(
            harness.ContextLoader,
            harness.GitClient,
            harness.FileSystem,
            harness.PathBudget,
            harness.Platform,
            harness.Output,
            new HostCopyRemover(new RecordingInspector(Answering), transports, Lock(harness), harness.FileSystem, harness.Platform),
            harness.LocalTransport,
            harness.RunLock,
            harness.ProcessContext);

        var deleted = await service.DeleteAsync(temp.Path, "feature", force: false, deleteEvidence: false, cancellationToken: cancellationToken);

        Assert.Equal(Verdicts.ExitCodeFor(LegVerdict.Stopped), deleted.Outcome.ExitCode);
        Assert.Equal(
            "Worktree 'feature' was deleted, and 1 of its copies on hosts are not yet dealt with: run 'dssharness delete-worktree feature' once "
            + "what keeps each is gone.",
            deleted.Outcome.Message);
        Assert.Contains(
            $"ssh pi: its copy at '{onPi}' stays, and is still recorded: the removal of the mutation workers kept in it was stopped "
            + $"before each had been dealt with; 1 mutation worker(s) kept in it were removed before that, {DiskSpace.Size(2048)}",
            deleted.Outcome.Details ?? []);
        await transport.DidNotReceive().RemoveCopyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        Assert.Equal([Entry(onPi, created.Path)], record.Of(layout, "feature"));
    }

    /// <summary>
    /// The removal of a tree's workers is asked of the harness on the host by the tree's own path, from the home
    /// directory - there when the tree and the directory it was kept in are gone - saying where it is only to measure;
    /// and one the host never answered is that host being unavailable, never read as no worker there.
    /// </summary>
    [Fact]
    public async Task ARemovalOfWorkers_NamesTheirTree_AndOneNeverAnswered_IsTheHostUnavailable()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        const string tree = "/home/dev/repo.worktree-feature";
        var asked = new List<HostAgentRequest?>();
        var answer = new WorkersRemoval(
            [new WorkerRemoved(Worker(tree, "357e24cw-1"), 42)],
            [new WorkerLeft(Worker(tree, "357e24cw-2"), "a sweep still running holds it: run r", WorkerLeftAs.Held)]);

        var answering = new ScriptedHostCommands((_, command) =>
        {
            asked.Add(JsonSerializer.Deserialize<HostAgentRequest>(command.StandardInput.Read(), HostAgentProtocol.JsonOptions));
            command.OnOutputLine?.Invoke(SyncServe.Answer(new SyncWorkersAnswer(answer)));
            return HostResults.Finished(command, HarnessExit.Success);
        });

        var removed = await Remote(answering).RemoveWorkersAsync(tree, cancellationToken: cancellationToken);
        var measured = await Remote(answering).RemoveWorkersAsync(tree, measureOnly: true, cancellationToken);

        Assert.All(new[] { removed, measured }, told =>
        {
            Assert.Equal(answer.Removed, told.Removed);
            Assert.Equal(answer.Left, told.Left);
        });
        Assert.Equal([SyncServe.CommandName, SyncServe.RemoveWorkers, SyncServe.OperandsFollow, tree], asked[0]?.Arguments);
        Assert.Equal([SyncServe.CommandName, SyncServe.RemoveWorkers, SyncServe.OperandsFollow, tree, SyncServe.MeasureOnly], asked[1]?.Arguments);
        Assert.All(asked, request => Assert.Equal("~", request?.Directory));

        var silent = new ScriptedHostCommands((_, command) => HostResults.Finished(command, HarnessExit.Success));
        var refusal = await Assert.ThrowsAsync<HarnessException>(() => Remote(silent).RemoveWorkersAsync(tree, cancellationToken: cancellationToken));

        Assert.Equal(HarnessExit.HostUnavailable, refusal.ExitCode);
        Assert.Contains($"did not answer which mutation workers in '{tree}' it removed", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The far side's removal of a tree's workers answers as sync-serve answers every request: it removes them, leaving
    /// one a sweep still running on that machine holds; measures where asked only to; and refuses what it does not know.
    /// </summary>
    [Fact]
    public async Task SyncServe_AnswersARemovalOfWorkers_LeavingOneASweepHolds()
    {
        using var hosts = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = new HarnessFactory();
        var tree = hosts.Combine("repo.worktree-feature");
        var free = Worker(tree, "357e24cw-1");
        var held = Worker(tree, "357e24cw-2");

        await Local(harness).CreateRootAsync(free, CopyMark.Complete, cancellationToken);
        await Local(harness).CreateRootAsync(held, CopyMark.Complete, cancellationToken);

        // Claimed by this very process, which the command line run within it finds still running.
        var sweep = RunId.New();
        var copies = new WorkerCopies(Sync(harness), Local(harness), harness.FileSystem, harness.Output, harness.Identity, "check-mutations");

        Assert.True(copies.Claim(held, sweep, force: false).Taken);

        var measured = await CliRunner.RunAsync(["sync-serve", SyncServe.RemoveWorkers, SyncServe.OperandsFollow, tree, SyncServe.MeasureOnly], cancellationToken);

        Assert.Equal(HarnessExit.Success, measured.ExitCode);
        Assert.Equal([free], SyncServe.ReadAnswer<SyncWorkersAnswer>(measured.StandardOutput.Trim())?.Workers.Removed.Select(worker => worker.Path));
        Assert.True(Directory.Exists(free));

        var result = await CliRunner.RunAsync(["sync-serve", SyncServe.RemoveWorkers, SyncServe.OperandsFollow, tree], cancellationToken);
        var answer = SyncServe.ReadAnswer<SyncWorkersAnswer>(result.StandardOutput.Trim())?.Workers;

        Assert.Equal(HarnessExit.Success, result.ExitCode);
        Assert.Equal([free], answer?.Removed.Select(worker => worker.Path));

        var left = Assert.Single(answer!.Left);

        Assert.Equal((held, WorkerLeftAs.Held), (left.Path, left.As));
        Assert.StartsWith("a sweep still running holds it: ", left.Why, StringComparison.Ordinal);
        Assert.Contains($"run {sweep.Value}", left.Why, StringComparison.Ordinal);
        Assert.False(Directory.Exists(free));
        Assert.True(Directory.Exists(held));

        copies.Release(held, sweep);

        var unknown = await CliRunner.RunAsync(["sync-serve", SyncServe.RemoveWorkers, SyncServe.OperandsFollow, tree, "everything"], cancellationToken);

        Assert.Equal(HarnessExit.UsageError, unknown.ExitCode);
        Assert.True(Directory.Exists(held));
    }

    /// <summary>
    /// A listing is asked of the harness on the host by the main copy's path, from the home directory, which is there
    /// when the directory the copies are kept in is not; and one the host never answered is that host being
    /// unavailable, never read as a host keeping nothing.
    /// </summary>
    [Fact]
    public async Task AListingRequest_NamesTheMainCopy_AndOneNeverAnswered_IsTheHostUnavailable()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        const string main = "/home/dev/repo";
        HostAgentRequest? asked = null;
        var kept = new HostCopyFound("feature", main + ".worktree-feature", CopyOrigin.Made, 42) { CreatedBy = "laptop", CreatedUtc = "2026-09-28T10:00:00Z" };

        var answering = new ScriptedHostCommands((_, command) =>
        {
            asked = JsonSerializer.Deserialize<HostAgentRequest>(command.StandardInput.Read(), HostAgentProtocol.JsonOptions);
            command.OnOutputLine?.Invoke(SyncServe.Answer(new SyncCopiesAnswer([kept])));
            return HostResults.Finished(command, HarnessExit.Success);
        });

        Assert.Equal([kept], await Remote(answering).ListCopiesAsync(main, cancellationToken));
        Assert.Equal([SyncServe.CommandName, SyncServe.ListCopies, SyncServe.OperandsFollow, main], asked?.Arguments);
        Assert.Equal("~", asked?.Directory);

        var silent = new ScriptedHostCommands((_, command) => HostResults.Finished(command, HarnessExit.Success));
        var refusal = await Assert.ThrowsAsync<HarnessException>(() => Remote(silent).ListCopiesAsync(main, cancellationToken));

        Assert.Equal(HarnessExit.HostUnavailable, refusal.ExitCode);
        Assert.Contains($"did not answer which copies it keeps beside '{main}'", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>The far side's listing answers as sync-serve, the command a remote sync's agent starts, answers every request.</summary>
    [Fact]
    public async Task SyncServe_AnswersAListingRequest()
    {
        using var hosts = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var main = hosts.Combine("repo");
        await Local(new HarnessFactory()).CreateRootAsync(main + ".worktree-feature", CopyMark.Complete, cancellationToken);

        var result = await CliRunner.RunAsync(["sync-serve", SyncServe.ListCopies, SyncServe.OperandsFollow, main], cancellationToken);

        Assert.Equal(HarnessExit.Success, result.ExitCode);

        var copy = Assert.Single(SyncServe.ReadAnswer<SyncCopiesAnswer>(result.StandardOutput.Trim())!.Copies);
        Assert.Equal(("feature", main + ".worktree-feature", CopyOrigin.Made), (copy.Name, copy.Path, copy.Origin));
    }

    /// <summary>
    /// Listing the worktrees sets the copies this machine records against the worktrees there are - a worktree's, a
    /// tree's still here outside the worktrees root, and those a worktree that is gone left, with the clean that removes
    /// them by their tree, and, where a worktree here bears the gone one's name now, that deleting it would take them too
    /// - and, asked, what each declared host keeps: a copy recorded, one the record does not hold, which nothing here
    /// reaches, said with the tree its marker names and whether that tree is here, and one recorded that is not there,
    /// with what forgets it by where its tree stands. A host that cannot be asked, and a copy on a host no configuration
    /// here declares, are named, and the listing fails with the highest code a host was left with.
    /// </summary>
    [Fact]
    public async Task TheListing_SetsWhatEachHostKeeps_AgainstTheRecord()
    {
        using var temp = new TempDirectory();
        using var hosts = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp, hosts.Path);
        var layout = await LayoutAsync(harness, temp);
        var record = Record(harness);
        var onPi = hosts.Combine("pi", "repo");
        var onMac = hosts.Combine("mac", "repo");

        var created = await harness.WorktreeService.CreateAsync(temp.Path, "alpha", useRandomName: false, cancellationToken);
        Assert.True(created.Succeeded, created.Outcome.Message);

        var alpha = harness.FileSystem.ResolveLinks(created.Path);
        var beta = Tree(temp, "elsewhere", "beta");
        var lfprobe = GoneTree(temp, "lfprobe");
        var gamma = GoneTree(temp, "gamma");
        var old = GoneTree(temp, "old");
        var formerAlpha = GoneTree(temp, "alpha");
        var zetaTree = GoneTree(temp, "zeta");
        var delta = Tree(temp, "elsewhere", "delta");

        foreach (var name in new[] { "alpha", "beta", "lfprobe" })
        {
            await Local(harness).CreateRootAsync(onPi + HostCopies.WorktreeSuffix + name, CopyMark.Complete, cancellationToken);
        }

        // Two copies no record here holds, each finished by a sync that named its tree: one gone, one here.
        await Local(harness).MarkFinishedAsync(onPi + ".worktree-zeta", new CopyReading(null, null, 0), zetaTree, cancellationToken);
        await Local(harness).MarkFinishedAsync(onPi + ".worktree-eta", new CopyReading(null, null, 0), beta, cancellationToken);

        record.Claim(layout, new HostCopyEntry("alpha", Pi.ToString(), onPi + ".worktree-alpha", alpha));

        // A gone tree that bore the name the worktree here bears now.
        record.Claim(layout, new HostCopyEntry("alpha", Mac.ToString(), onMac + ".worktree-alpha", formerAlpha));
        record.Claim(layout, new HostCopyEntry("beta", Pi.ToString(), onPi + ".worktree-beta", beta));
        record.Claim(layout, new HostCopyEntry("lfprobe", Pi.ToString(), onPi + ".worktree-lfprobe", lfprobe));
        record.Claim(layout, new HostCopyEntry("lfprobe", Mac.ToString(), onMac + ".worktree-lfprobe", lfprobe + Path.DirectorySeparatorChar));
        record.Claim(layout, new HostCopyEntry("gamma", Pi.ToString(), onPi + ".worktree-gamma", gamma));
        record.Claim(layout, new HostCopyEntry("old", "ssh old", "/home/old/repo.worktree-old", old));
        record.Claim(layout, new HostCopyEntry("delta", Pi.ToString(), onPi + ".worktree-delta", delta));

        var inspector = new RecordingInspector(host => host == Pi
            ? Answering(host)
            : new HostReport { Host = host, Reason = "the host could not be reached: ssh said ssh: connect to host 192.0.2.10 port 22: Connection timed out" });

        var worktrees = await harness.WorktreeService.ListAsync(temp.Path, cancellationToken);
        var listing = await Lister(harness, inspector).ListAsync(temp.Path, worktrees, askHosts: true, cancellationToken);

        Assert.Equal(["alpha"], worktrees.Select(worktree => worktree.Name));
        Assert.Equal([Mac, Pi], inspector.Inspected);
        Assert.Equal(["alpha"], listing.OfListed.Keys);
        Assert.Equal([(Pi.ToString(), onPi + ".worktree-alpha")], listing.OfListed["alpha"].Select(copy => (copy.Host, copy.Path)));
        Assert.Equal([("beta", beta), ("delta", delta)], listing.Elsewhere.Select(tree => (tree.Name, tree.Tree)));
        Assert.Equal(
            [("alpha", formerAlpha, 1, worktrees[0].Path), ("gamma", gamma, 1, null), ("lfprobe", lfprobe, 2, null), ("old", old, 1, null)],
            listing.Gone.Select(tree => (tree.Name, tree.Tree, tree.Copies.Count, tree.NameNowAt)));

        var mac = listing.Hosts![0];
        var pi = listing.Hosts[1];

        Assert.Equal(HarnessExit.HostUnavailable, mac.ExitCode);
        Assert.Empty(mac.Copies);
        Assert.Equal(HarnessExit.Success, pi.ExitCode);
        Assert.Equal(
            [
                ("alpha", CopyStanding.Listed, alpha, false),
                ("beta", CopyStanding.Elsewhere, beta, false),
                ("eta", CopyStanding.Unrecorded, beta, true),
                ("lfprobe", CopyStanding.Gone, lfprobe, false),
                ("zeta", CopyStanding.Unrecorded, zetaTree, false),
            ],
            pi.Copies.Select(copy => (copy.Found.Name, copy.Standing, copy.Tree, copy.TreeHere)));
        Assert.Equal([("gamma", CopyStanding.Gone), ("delta", CopyStanding.Elsewhere)], pi.Missing.Select(copy => (copy.Entry.Worktree, copy.Standing)));
        Assert.Equal(["ssh old"], listing.Undeclared.Select(copy => copy.Host));
        Assert.Equal(HarnessExit.HostUnavailable, listing.ExitCode);

        var report = WorktreeReports.List(worktrees, listing, json: false);
        string Size(string name) => DiskSpace.Size(pi.Copies.Single(copy => copy.Found.Name == name).Found.Bytes);
        string Made(string name) => pi.Copies.Single(copy => copy.Found.Name == name).Found.CreatedUtc!;

        Assert.Equal(HarnessExit.HostUnavailable, report.ExitCode);
        Assert.Equal(
            "1 worktree(s); 4 worktree(s) that are gone left copies on hosts, which 'dssharness clean --gone-copies' removes; "
            + "1 of 2 host(s) could not be asked",
            report.Message);
        Assert.Equal(
            [
                worktrees[0].ToString(),
                $"  ssh pi: {onPi}.worktree-alpha",
                $"beta, the worktree at '{beta}', outside the worktrees root",
                $"  ssh pi: {onPi}.worktree-beta",
                $"delta, the worktree at '{delta}', outside the worktrees root",
                $"  ssh pi: {onPi}.worktree-delta",
                $"alpha, gone from '{formerAlpha}': 'dssharness clean --gone-copies' removes the copies it left; 'alpha' is now the worktree at "
                    + $"'{worktrees[0].Path}', whose deletion would take them with it - the clean never reaches it",
                $"  ssh mac: {onMac}.worktree-alpha",
                $"gamma, gone from '{gamma}': 'dssharness clean --gone-copies' removes the copies it left",
                $"  ssh pi: {onPi}.worktree-gamma",
                $"lfprobe, gone from '{lfprobe}': 'dssharness clean --gone-copies' removes the copies it left",
                $"  ssh pi: {onPi}.worktree-lfprobe",
                $"  ssh mac: {onMac}.worktree-lfprobe",
                $"old, gone from '{old}': 'dssharness clean --gone-copies' removes the copies it left",
                "  ssh old: /home/old/repo.worktree-old",
                $"ssh mac: could not be asked about the copies beside '{onMac}': the host could not be reached: ssh said ssh: connect to host 192.0.2.10 port 22: Connection timed out",
                $"ssh pi keeps 5 worktree copies beside '{onPi}', {DiskSpace.Size(pi.Copies.Sum(copy => copy.Found.Bytes))} in all",
                $"  {onPi}.worktree-alpha  {Size("alpha")}  worktree 'alpha'",
                $"  {onPi}.worktree-beta  {Size("beta")}  the worktree at '{beta}'",
                $"  {onPi}.worktree-eta  {Size("eta")}  not recorded here, so nothing here reaches it; made by the harness at {Made("eta")}; made from "
                    + $"'{beta}', a tree on this machine",
                $"  {onPi}.worktree-lfprobe  {Size("lfprobe")}  gone from '{lfprobe}': 'dssharness clean --gone-copies' removes it",
                $"  {onPi}.worktree-zeta  {Size("zeta")}  not recorded here, so nothing here reaches it; made by the harness at {Made("zeta")}; made from "
                    + $"'{zetaTree}', which is not here: gone from this machine, or another machine's",
                $"  {onPi}.worktree-gamma  recorded here, and not there: gone from '{gamma}': 'dssharness clean --gone-copies' forgets it",
                $"  {onPi}.worktree-delta  recorded here, and not there: the next sync of the worktree at '{delta}' there makes it again",
                "ssh old: recorded as keeping '/home/old/repo.worktree-old', and no configuration here declares it, so it was not asked",
            ],
            report.Details);

        // As data: the clean that removes a gone tree's copies, where its name is a worktree's now, and what each copy
        // the record does not hold was made from.
        using var document = JsonDocument.Parse(Assert.Single(WorktreeReports.List(worktrees, listing, json: true).Data!));
        var formerly = document.RootElement.GetProperty("gone")[0];
        var copies = document.RootElement.GetProperty("hosts")[1].GetProperty("copies");

        Assert.Equal(("dssharness clean --gone-copies", worktrees[0].Path), (formerly.GetProperty("deletedBy").GetString(), formerly.GetProperty("nameNowAt").GetString()));
        Assert.Equal((beta, true), (copies[2].GetProperty("tree").GetString(), copies[2].GetProperty("treeHere").GetBoolean()));
        Assert.Equal((zetaTree, false), (copies[4].GetProperty("tree").GetString(), copies[4].GetProperty("treeHere").GetBoolean()));
        Assert.Equal(JsonValueKind.Null, copies[0].GetProperty("treeHere").ValueKind);
        Assert.Equal(
            ["gone", "elsewhere"],
            document.RootElement.GetProperty("hosts")[1].GetProperty("missing").EnumerateArray().Select(missing => missing.GetProperty("standing").GetString()));
    }

    /// <summary>
    /// A copy a gone worktree left that the harness took over, or that nothing marks as the harness's, is one deleting
    /// the name forgets and leaves in place; and one whose marker cannot be read, one it leaves recorded. Said as that,
    /// so nobody deletes a name expecting a copy to go that will not.
    /// </summary>
    [Theory]
    [InlineData(CopyOrigin.TakenOver, "forgets it and leaves it in place, as the harness took over a directory that was there, which is yours to remove")]
    [InlineData(CopyOrigin.Unmarked, "forgets it and leaves it in place, as nothing there says the harness made it, so it is yours to remove")]
    [InlineData(CopyOrigin.Unreadable, "leaves it recorded, as its marker cannot be read: it is damaged")]
    public void ACopyAGoneWorktreeLeft_IsSaidAsWhatDeletingItsNameDoesWithIt(CopyOrigin origin, string expected)
    {
        var found = new HostCopyFound("lfprobe", "/home/pi/repo.worktree-lfprobe", origin, 1024) { Problem = "it is damaged." };
        var listing = new HostCopyListing
        {
            Hosts = [new HostCopiesAnswer(Pi, "/home/pi/repo") { Copies = [new HostCopySeen(found, CopyStanding.Gone, "/gone/lfprobe")] }],
        };

        var report = WorktreeReports.List([], listing, json: false);

        Assert.Equal("no worktrees; asked 1 host(s)", report.Message);
        Assert.Contains("ssh pi keeps 1 worktree copy beside '/home/pi/repo', 1 KiB in all", report.Details ?? [], StringComparer.Ordinal);
        Assert.Contains(
            $"  /home/pi/repo.worktree-lfprobe  1 KiB  gone from '/gone/lfprobe': 'dssharness clean --gone-copies' {expected}",
            report.Details ?? [],
            StringComparer.Ordinal);
    }

    /// <summary>
    /// A host is asked about the copies it keeps where its item keeps this machine's - beside the REPOSITORY_PATH the item
    /// sets, never config.json's repositoryPath, whose copies are another machine's - and one whose item cannot be read is
    /// asked nothing and named with no path, in text and in --json: where it keeps its copies is not known, and config.json's
    /// path, which its item was written to replace, would send its reader to another directory.
    /// </summary>
    [Fact]
    public async Task AHostIsListedWhereItsItemKeepsItsCopies_AndOneWhoseItemCannotBeRead_WithNoPath()
    {
        using var temp = new TempDirectory();
        using var hosts = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp, hosts.Path);
        var mine = hosts.Combine("pi", "mine", "repo");

        foreach (var (item, env) in new[]
        {
            ("pi", $"ADDRESS=pi.invalid\nUSER=dev\nREPOSITORY_PATH={mine}\n"),
            ("mac", "ADDRESS=mac.invalid\nUSER=dev\nREPOSITORY_PAHT=/Users/dev/mine/repo\n"),
        })
        {
            temp.WriteFile(Path.Combine(".harness-config", "sshItems", item, ".env"), env);
            temp.WriteFile(Path.Combine(".harness-config", "sshItems", item, ".key"), "not a real key\n");
            temp.WriteFile(Path.Combine(".harness-config", "sshItems", item, "known_hosts"), $"{item}.invalid ssh-ed25519 AAAA\n");
        }

        // A copy beside each place config.json names, and one beside the item's: only the item's is this machine's.
        foreach (var copy in new[] { mine + ".worktree-alpha", hosts.Combine("pi", "repo") + ".worktree-beta", hosts.Combine("mac", "repo") + ".worktree-gamma" })
        {
            await Local(harness).CreateRootAsync(copy, CopyMark.Complete, cancellationToken);
        }

        var inspector = new RecordingInspector(Answering);
        var worktrees = await harness.WorktreeService.ListAsync(temp.Path, cancellationToken);
        var listing = await Lister(harness, inspector).ListAsync(temp.Path, worktrees, askHosts: true, cancellationToken);
        var context = await harness.ContextLoader.LoadAsync(temp.Path, cancellationToken);
        var unknown = Assert.Throws<HarnessException>(() => HostCopies.RepositoryOf(context, Mac)).Message;

        var mac = listing.Hosts![0];
        var pi = listing.Hosts[1];

        Assert.Equal([Pi], inspector.Inspected);
        Assert.Equal((Mac, null, unknown, HarnessExit.HostUnavailable), (mac.Host, mac.RepositoryPath, mac.Unasked, mac.ExitCode));
        Assert.Empty(mac.Copies);
        Assert.Equal((Pi, mine, HarnessExit.Success), (pi.Host, pi.RepositoryPath, pi.ExitCode));
        Assert.Equal([mine + ".worktree-alpha"], pi.Copies.Select(copy => copy.Found.Path));
        Assert.StartsWith(
            "Where host ssh mac keeps its copies for this machine is not known, since its item cannot be read: ",
            unknown,
            StringComparison.Ordinal);

        var report = WorktreeReports.List(worktrees, listing, json: false);
        var size = DiskSpace.Size(pi.Copies[0].Found.Bytes);

        // Its copy's own line says what the record makes of it, which is not this test's.
        Assert.Equal(3, report.Details!.Count);
        Assert.Equal(
            [$"ssh mac: could not be asked about its copies: {unknown.TrimEnd('.')}", $"ssh pi keeps 1 worktree copy beside '{mine}', {size} in all"],
            report.Details.Take(2));
        Assert.StartsWith($"  {mine}.worktree-alpha  {size}  ", report.Details[2], StringComparison.Ordinal);

        using var document = JsonDocument.Parse(Assert.Single(WorktreeReports.List(worktrees, listing, json: true).Data!));
        var asked = document.RootElement.GetProperty("hosts");

        Assert.Equal(JsonValueKind.Null, asked[0].GetProperty("repositoryPath").ValueKind);
        Assert.Equal(unknown, asked[0].GetProperty("unasked").GetString());
        Assert.Equal(mine, asked[1].GetProperty("repositoryPath").GetString());
    }

    /// <summary>
    /// A gone tree's copy is measured where the record says it is, as its removal removes it: at its own path, beside the
    /// repositoryPath it was kept beside - a copy recorded before the host's item set REPOSITORY_PATH elsewhere is measured
    /// beside config.json's, where it still is, and one kept beside the item's beside that - never only beside where the
    /// host keeps this machine's copies now, where the dry run found nothing and said the copy would only be forgotten
    /// while the clean it measured removed gigabytes. The listing finds each where it is, as the clean would remove it.
    /// </summary>
    [Fact]
    public async Task AGoneTreesCopy_IsMeasuredWhereItIsRecorded_AsItsRemovalRemovesIt()
    {
        using var temp = new TempDirectory();
        using var hosts = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp, hosts.Path);
        var declared = hosts.Combine("pi", "repo");
        var mine = hosts.Combine("pi", "mine", "repo");
        temp.WriteFile(Path.Combine(".harness-config", "sshItems", "pi", ".env"), $"ADDRESS=pi.invalid\nUSER=dev\nREPOSITORY_PATH={mine}\n");
        temp.WriteFile(Path.Combine(".harness-config", "sshItems", "pi", ".key"), "not a real key\n");
        temp.WriteFile(Path.Combine(".harness-config", "sshItems", "pi", "known_hosts"), "pi.invalid ssh-ed25519 AAAA\n");

        // A key other users can read is no item ssh could use, and leaves where the host keeps its copies unknown.
        harness.FileSystem.ProtectSecretFile(temp.Combine(".harness-config", "sshItems", "pi", ".key"));
        var context = await harness.ContextLoader.LoadAsync(temp.Path, cancellationToken);
        var record = Record(harness);
        var formerAlpha = GoneTree(temp, "alpha");
        var formerBeta = GoneTree(temp, "beta");
        var older = declared + ".worktree-alpha";
        var newer = mine + ".worktree-beta";

        foreach (var copy in new[] { older, newer })
        {
            await Local(harness).CreateRootAsync(copy, CopyMark.Complete, cancellationToken);
            File.WriteAllText(Path.Combine(copy, "built.o"), "made by a build");
        }

        // Recorded when config.json's repositoryPath was where the host kept this machine's copies, then the item's.
        record.Claim(context.Layout, new HostCopyEntry("alpha", Pi.ToString(), older, formerAlpha));
        record.Claim(context.Layout, new HostCopyEntry("beta", Pi.ToString(), newer, formerBeta));

        var inspector = new RecordingInspector(Answering);
        var worktrees = await harness.WorktreeService.ListAsync(temp.Path, cancellationToken);
        var listing = await Lister(harness, inspector).ListAsync(temp.Path, worktrees, askHosts: true, cancellationToken);
        var pi = listing.Hosts!.Single(host => host.Host == Pi);

        Assert.Null(pi.Unasked);
        Assert.Equal(mine, pi.RepositoryPath);
        Assert.Equal([("beta", CopyStanding.Gone)], pi.Copies.Select(copy => (copy.Found.Name, copy.Standing)));
        Assert.Equal([declared], pi.Earlier.Select(place => place.RepositoryPath));
        Assert.Equal([("alpha", CopyStanding.Gone)], pi.Earlier[0].Copies.Select(copy => (copy.Found.Name, copy.Standing)));
        Assert.Empty(pi.Missing);
        Assert.Contains(
            $"ssh pi keeps 1 worktree copy beside '{declared}', where this machine's record says it kept copies before, "
                + $"{DiskSpace.Size(pi.Earlier[0].Copies[0].Found.Bytes)} in all",
            WorktreeReports.List(worktrees, listing, json: false).Details ?? [],
            StringComparer.Ordinal);

        var remover = new HostCopyRemover(inspector, new LocalHosts(harness, harness.FileSystem), Lock(harness), harness.FileSystem, harness.Platform);
        var olderSize = DiskSpace.Size(harness.FileSystem.DirectorySize(older));
        var newerSize = DiskSpace.Size(harness.FileSystem.DirectorySize(newer));

        var measured = await remover.RemoveGoneAsync(context, Pi.ToString(), measureOnly: true, cancellationToken);

        Assert.Equal(
            [
                $"ssh pi: would remove its copy at '{older}', of the worktree that was at '{formerAlpha}', {olderSize}",
                $"ssh pi: would remove its copy at '{newer}', of the worktree that was at '{formerBeta}', {newerSize}",
            ],
            measured.Lines);
        Assert.Equal(HarnessExit.Success, measured.ExitCode);
        Assert.True(Directory.Exists(older));
        Assert.True(Directory.Exists(newer));
        Assert.Equal(2, record.All(context.Layout).Count);

        var removed = await remover.RemoveGoneAsync(context, Pi.ToString(), measureOnly: false, cancellationToken);

        // What the dry run promised is what the removal did, copy for copy.
        Assert.Equal(
            [
                $"ssh pi: removed its copy at '{older}', of the worktree that was at '{formerAlpha}'",
                $"ssh pi: removed its copy at '{newer}', of the worktree that was at '{formerBeta}'",
            ],
            removed.Lines);
        Assert.Equal(HarnessExit.Success, removed.ExitCode);
        Assert.False(Directory.Exists(older));
        Assert.False(Directory.Exists(newer));
        Assert.Empty(record.All(context.Layout));
    }

    /// <summary>
    /// A tree that cannot be looked at is never read as gone, so no copy of it is ever removed: one whose directory is not
    /// there either - a drive or a share not mounted - one spelt for the other system, as a checkout used from Windows and
    /// from WSL records, and one this user may not look at, each stays recorded on the host, refused, its line saying why,
    /// by the clean's dry run and the clean alike, and by a deletion of a worktree of its name; the listing says each with
    /// why and never names the clean for it. A tree seen to be gone is still removed, an orchestrator's agent's among them
    /// once its orchestrator's directory is gone too, the worktrees root there to look in.
    /// </summary>
    [Fact]
    public async Task ACopyWhoseTreeCannotBeLookedAt_IsNeverRemoved()
    {
        using var temp = new TempDirectory();
        using var hosts = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp, hosts.Path);
        var context = await harness.ContextLoader.LoadAsync(temp.Path, cancellationToken);
        var onPi = hosts.Combine("pi", "repo");
        var unmounted = temp.Combine("unmounted");
        var unseen = Path.Combine(unmounted, "alpha");
        var otherSystems = OperatingSystem.IsWindows() ? "/mnt/d/Source/repo/.wt/beta" : @"D:\Source\repo\.wt\beta";
        var forbidden = GoneTree(temp, "gamma");
        var agent = Path.Combine(Directory.CreateDirectory(temp.Combine(".wt")).FullName, "o1", "xa");
        var disk = new UnlookableAt(harness.FileSystem, forbidden);
        var record = new HostCopyRecord(disk, harness.Platform.PathComparison);
        var entries = new[]
        {
            new HostCopyEntry("alpha", Pi.ToString(), onPi + ".worktree-alpha", unseen),
            new HostCopyEntry("beta", Pi.ToString(), onPi + ".worktree-beta", otherSystems),
            new HostCopyEntry("gamma", Pi.ToString(), onPi + ".worktree-gamma", forbidden),
            new HostCopyEntry("o1--xa", Pi.ToString(), onPi + ".worktree-o1--xa", agent),
        };

        foreach (var entry in entries)
        {
            await Local(harness).CreateRootAsync(entry.Path, CopyMark.Complete, cancellationToken);
            record.Claim(context.Layout, entry);
        }

        string Untold(HostCopyEntry entry, string why)
            => $"ssh pi: its copy at '{entry.Path}' stays, and is still recorded: whether the worktree at '{entry.Tree}' is gone cannot be told - "
                + $"{why} - and a copy is removed only for a tree seen to be gone";

        var whys = new[]
        {
            $"'{unmounted}', which it was in, is not there either: a volume or a share it was on may not be mounted",
            $"'{otherSystems}' is no whole path on this machine, so it may be another system's spelling of a tree",
            $"'{forbidden}' cannot be looked at: Access to the path '{forbidden}' is denied",
        };
        var kept = entries[..3].Zip(whys, Untold).ToList();
        var inspector = new RecordingInspector(Answering);
        var lister = new HostCopyLister(harness.ContextLoader, inspector, new LocalHosts(harness, harness.FileSystem), disk, harness.Platform);
        var worktrees = await harness.WorktreeService.ListAsync(temp.Path, cancellationToken);
        var listing = await lister.ListAsync(temp.Path, worktrees, askHosts: true, cancellationToken);
        var report = WorktreeReports.List(worktrees, listing, json: false);

        Assert.Equal([("alpha", whys[0]), ("beta", whys[1]), ("gamma", whys[2])], listing.Untold.Select(tree => (tree.Name, tree.Why)));
        Assert.Equal(["o1--xa"], listing.Gone.Select(tree => tree.Name));
        Assert.Equal(
            [("alpha", CopyStanding.Untold), ("beta", CopyStanding.Untold), ("gamma", CopyStanding.Untold), ("o1--xa", CopyStanding.Gone)],
            listing.Hosts!.Single(host => host.Host == Pi).Copies.Select(copy => (copy.Found.Name, copy.Standing)));
        Assert.Equal(
            "no worktrees; 1 worktree(s) that are gone left copies on hosts, which 'dssharness clean --gone-copies' removes; 3 worktree(s) "
            + "with copies on hosts cannot be looked at here, so nothing here removes their copies; asked 2 host(s)",
            report.Message);
        Assert.Contains(
            $"alpha, the worktree at '{unseen}', which cannot be looked at here: {whys[0]}; nothing here removes or forgets its copies while that "
                + "is so, since it may still be there, using them",
            report.Details ?? [],
            StringComparer.Ordinal);

        var remover = new HostCopyRemover(inspector, new LocalHosts(harness, harness.FileSystem), Lock(harness), disk, harness.Platform);

        Assert.Equal(["ssh pi"], remover.HostsHoldingGone(context));

        var measured = await remover.RemoveGoneAsync(context, Pi.ToString(), measureOnly: true, cancellationToken);

        Assert.Equal(
            [.. kept, $"ssh pi: would remove its copy at '{entries[3].Path}', of the worktree that was at '{agent}', {DiskSpace.Size(harness.FileSystem.DirectorySize(entries[3].Path))}"],
            measured.Lines);
        Assert.Equal(HarnessExit.Refused, measured.ExitCode);

        var removed = await remover.RemoveGoneAsync(context, Pi.ToString(), measureOnly: false, cancellationToken);

        Assert.Equal([.. kept, $"ssh pi: removed its copy at '{entries[3].Path}', of the worktree that was at '{agent}'"], removed.Lines);
        Assert.Equal(HarnessExit.Refused, removed.ExitCode);
        Assert.Equal(entries[..3], record.All(context.Layout));
        Assert.All(entries[..3], entry => Assert.True(Directory.Exists(entry.Path), $"'{entry.Path}' was removed"));
        Assert.False(Directory.Exists(entries[3].Path));

        // A deletion of a worktree of the name leaves it too: it is another tree's, and may still be there.
        var deleting = await remover.RemoveAsync(context, "alpha", Tree(temp, "live", "alpha"), treeConfig: null, cancellationToken);

        Assert.Equal([kept[0]], deleting.Lines);
        Assert.Equal([HarnessExit.Refused], deleting.Unfinished);
        Assert.Empty(deleting.LeftFor);
        Assert.True(Directory.Exists(entries[0].Path));
        Assert.Equal(entries[..3], record.All(context.Layout));
    }

    /// <summary>
    /// The copies of gone trees a host keeps are removed by the tree this machine's record names, never by name: a gone
    /// tree's copy goes though a worktree here bears its name now - that worktree, not synced there, untouched - one of a
    /// tree still here stays, one the host no longer keeps is forgotten, and another host's are not asked about. Measured
    /// first, each is said with what removing it would do, and nothing is removed or forgotten. Every host the record names
    /// such a copy on is named, one no configuration here declares included, whose copy - which no clean could ever reach
    /// - is forgotten as deleting a worktree forgets it, left on the host and said.
    /// </summary>
    [Fact]
    public async Task TheCopiesOfGoneTrees_AreRemovedByTheirTree_NeverByTheirName()
    {
        using var temp = new TempDirectory();
        using var hosts = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp, hosts.Path);
        var context = await harness.ContextLoader.LoadAsync(temp.Path, cancellationToken);
        var record = Record(harness);
        var onPi = hosts.Combine("pi", "repo");
        var onMac = hosts.Combine("mac", "repo");

        // A live worktree of the name a gone tree's copy is kept under, never synced to the host.
        var created = await harness.WorktreeService.CreateAsync(temp.Path, "alpha", useRandomName: false, cancellationToken);
        Assert.True(created.Succeeded, created.Outcome.Message);

        var formerAlpha = GoneTree(temp, "alpha");
        var beta = Tree(temp, "elsewhere", "beta");
        var gamma = GoneTree(temp, "gamma");
        var delta = GoneTree(temp, "delta");

        foreach (var copy in new[] { onPi + ".worktree-alpha", onPi + ".worktree-beta", onMac + ".worktree-delta" })
        {
            await Local(harness).CreateRootAsync(copy, CopyMark.Complete, cancellationToken);
            File.WriteAllText(Path.Combine(copy, "built.o"), "made by a build");
        }

        record.Claim(context.Layout, new HostCopyEntry("alpha", Pi.ToString(), onPi + ".worktree-alpha", formerAlpha));
        record.Claim(context.Layout, new HostCopyEntry("beta", Pi.ToString(), onPi + ".worktree-beta", beta));
        record.Claim(context.Layout, new HostCopyEntry("gamma", Pi.ToString(), onPi + ".worktree-gamma", gamma));
        record.Claim(context.Layout, new HostCopyEntry("delta", Mac.ToString(), onMac + ".worktree-delta", delta));

        // On a host no configuration here declares any more: the record still names it, and nothing can ask it.
        record.Claim(context.Layout, new HostCopyEntry("old", "ssh retired", "/srv/repo.worktree-old", GoneTree(temp, "old")));

        var inspector = new RecordingInspector(Answering);
        var remover = new HostCopyRemover(inspector, new LocalHosts(harness, harness.FileSystem), Lock(harness), harness.FileSystem, harness.Platform);
        var size = DiskSpace.Size(harness.FileSystem.DirectorySize(onPi + ".worktree-alpha"));

        Assert.Equal(["ssh pi", "ssh mac", "ssh retired"], remover.HostsHoldingGone(context));

        var measured = await remover.RemoveGoneAsync(context, Pi.ToString(), measureOnly: true, cancellationToken);

        Assert.Equal(HarnessExit.Success, measured.ExitCode);
        Assert.Equal(
            [
                $"ssh pi: would remove its copy at '{onPi}.worktree-alpha', of the worktree that was at '{formerAlpha}', {size}",
                $"ssh pi: would find nothing at '{onPi}.worktree-gamma' to remove, and forget it",
            ],
            measured.Lines);
        Assert.True(Directory.Exists(onPi + ".worktree-alpha"));
        Assert.Equal(5, record.All(context.Layout).Count);

        var removed = await remover.RemoveGoneAsync(context, Pi.ToString(), measureOnly: false, cancellationToken);

        Assert.Equal(HarnessExit.Success, removed.ExitCode);
        Assert.Equal(
            [
                $"ssh pi: removed its copy at '{onPi}.worktree-alpha', of the worktree that was at '{formerAlpha}'",
                $"ssh pi: found nothing at '{onPi}.worktree-gamma' to remove",
            ],
            removed.Lines);
        Assert.False(Directory.Exists(onPi + ".worktree-alpha"));
        Assert.True(Directory.Exists(onPi + ".worktree-beta"));
        Assert.True(Directory.Exists(onMac + ".worktree-delta"));
        Assert.True(Directory.Exists(created.Path));
        Assert.Equal(["beta", "delta", "old"], record.All(context.Layout).Select(entry => entry.Worktree));
        Assert.DoesNotContain(Mac, inspector.Inspected);

        // A host nothing here declares cannot be asked, now or later: its copy is forgotten, and left on it for you.
        var retired = await remover.RemoveGoneAsync(context, "ssh retired", measureOnly: false, cancellationToken);

        Assert.Equal(
            ["ssh retired: no configuration here declares it any more, so nothing was asked to remove its copy at '/srv/repo.worktree-old', which stays "
                + "there for you to remove, and is no longer recorded"],
            retired.Lines);
        Assert.Equal(["ssh mac"], remover.HostsHoldingGone(context));
    }

    /// <summary>
    /// Listing without the hosts reads the record alone and asks no host; and a record that cannot be read is said, and
    /// refuses the listing of copies, rather than being read as one holding none - which would list every copy a host
    /// keeps as one nothing here made - while the worktrees are still listed.
    /// </summary>
    [Fact]
    public async Task TheListing_WithoutTheHosts_AsksNone_AndAnUnreadableRecordIsSaid()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        var layout = await LayoutAsync(harness, temp);
        var inspector = new RecordingInspector(Answering);
        var gone = GoneTree(temp, "feature");

        var created = await harness.WorktreeService.CreateAsync(temp.Path, "alpha", useRandomName: false, cancellationToken);
        Assert.True(created.Succeeded, created.Outcome.Message);
        Record(harness).Claim(layout, Entry("/home/pi/repo.worktree-feature", gone));

        var worktrees = await harness.WorktreeService.ListAsync(temp.Path, cancellationToken);
        var listing = await Lister(harness, inspector).ListAsync(temp.Path, worktrees, askHosts: false, cancellationToken);

        Assert.Null(listing.Hosts);
        Assert.Empty(inspector.Inspected);
        Assert.Equal([("feature", gone)], listing.Gone.Select(tree => (tree.Name, tree.Tree)));
        Assert.Equal(HarnessExit.Success, listing.ExitCode);

        File.WriteAllText(HostCopyRecord.PathOf(layout), "not json");

        var unreadable = await Lister(harness, inspector).ListAsync(temp.Path, worktrees, askHosts: true, cancellationToken);
        var report = WorktreeReports.List(worktrees, unreadable, json: false);

        Assert.Empty(inspector.Inspected);
        Assert.Equal(HarnessExit.Refused, report.ExitCode);
        Assert.StartsWith("1 worktree(s); the copies hosts keep of them cannot be listed: ", report.Message, StringComparison.Ordinal);
        Assert.Contains("Delete it to forget the copies it records", report.Message, StringComparison.Ordinal);
        Assert.Equal([worktrees[0].ToString()], report.Details);
    }

    /// <summary>
    /// Through the command line, list-worktree lists the copies a gone worktree left, with the deletion that deals with
    /// them, as lines and as one JSON document a program can read.
    /// </summary>
    [Fact]
    public async Task ListWorktree_ListsTheCopiesAGoneWorktreeLeft_AsLinesAndAsJson()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        var layout = await LayoutAsync(harness, temp);
        var gone = GoneTree(temp, "feature");
        Record(harness).Claim(layout, Entry("/home/pi/repo.worktree-feature", gone));

        var lines = await CliRunner.RunAsync(["list-worktree", "-C", temp.Path], cancellationToken);

        Assert.Equal(HarnessExit.Success, lines.ExitCode);
        Assert.Contains($"feature, gone from '{gone}': 'dssharness clean --gone-copies' removes the copies it left", lines.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("  ssh pi: /home/pi/repo.worktree-feature", lines.StandardOutput, StringComparison.Ordinal);

        var json = await CliRunner.RunAsync(["list-worktree", "--json", "-C", temp.Path], cancellationToken);

        Assert.Equal(HarnessExit.Success, json.ExitCode);

        using var document = JsonDocument.Parse(json.StandardOutput);
        var root = document.RootElement;
        var feature = Assert.Single(root.GetProperty("gone").EnumerateArray());

        Assert.Empty(root.GetProperty("worktrees").EnumerateArray());
        Assert.False(root.TryGetProperty("hosts", out _));
        Assert.Equal("feature", feature.GetProperty("name").GetString());
        Assert.Equal(gone, feature.GetProperty("tree").GetString());
        Assert.Equal("dssharness clean --gone-copies", feature.GetProperty("deletedBy").GetString());
        Assert.Equal("ssh pi", Assert.Single(feature.GetProperty("copies").EnumerateArray()).GetProperty("host").GetString());
    }

    /// <summary>
    /// Through the command line, --hosts asks every host the configuration declares, and a configuration declaring none
    /// is said to have had none asked, rather than listed as though every host kept nothing.
    /// </summary>
    [Fact]
    public async Task ListWorktree_WithHosts_SaysWhenNoHostIsDeclared()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        await new HarnessFactory().InitializeHarnessAsync(temp.Path, cancellationToken, new HarnessConfig());

        var lines = await CliRunner.RunAsync(["list-worktree", "--hosts", "-C", temp.Path], cancellationToken);

        Assert.Equal(HarnessExit.Success, lines.ExitCode);
        Assert.Contains("no worktrees; no host is declared, so none was asked", lines.StandardOutput + lines.StandardError, StringComparison.Ordinal);

        var json = await CliRunner.RunAsync(["list-worktree", "--hosts", "--json", "-C", temp.Path], cancellationToken);

        using var document = JsonDocument.Parse(json.StandardOutput);
        Assert.Empty(document.RootElement.GetProperty("hosts").EnumerateArray());
    }

    private static HostCopyEntry Entry(string path, string tree, HostId? host = null)
        => new("feature", (host ?? Pi).ToString(), path, tree);

    /// <summary>A directory standing for a worktree on this machine, made so that it exists.</summary>
    private static string Tree(TempDirectory temp, params string[] parts)
        => Directory.CreateDirectory(temp.Combine(["trees", .. parts])).FullName;

    /// <summary>
    /// Where a worktree on this machine was that is seen to be gone: nothing at it, in a directory that is there to look in.
    /// </summary>
    private static string GoneTree(TempDirectory temp, string name)
        => Path.Combine(Directory.CreateDirectory(temp.Combine("trees", "gone")).FullName, name);

    private static HostCopyRecord Record(HarnessFactory harness) => new(harness.FileSystem, harness.Platform.PathComparison);

    private static HostReport Answering(HostId host)
        => new() { Host = host, Os = "linux", Processor = "x86_64", Session = new HostSession(new HostConnection { Host = host }, "dssharness") };

    private static WorktreeService Service(HarnessFactory harness, ISet<HostId> answering, IFileSystem? hostDisk = null, IFileSystem? record = null, ICopyClaims? claims = null)
        => Service(
            harness,
            new RecordingInspector(host => answering.Contains(host)
                ? Answering(host)
                : new HostReport { Host = host, Reason = "the host could not be reached: ssh said ssh: connect to host 192.0.2.10 port 22: Connection timed out" }),
            hostDisk,
            record,
            claims);

    private static WorktreeService Service(HarnessFactory harness, IHostInspector inspector, IFileSystem? hostDisk = null, IFileSystem? record = null, ICopyClaims? claims = null)
        => new(
            harness.ContextLoader,
            harness.GitClient,
            harness.FileSystem,
            harness.PathBudget,
            harness.Platform,
            harness.Output,
            new HostCopyRemover(inspector, new LocalHosts(harness, hostDisk ?? harness.FileSystem, claims), Lock(harness), record ?? harness.FileSystem, harness.Platform),
            harness.LocalTransport,
            harness.RunLock,
            harness.ProcessContext);

    private static RunLock Lock(HarnessFactory harness) => new(harness.FileSystem, harness.Output, harness.Identity);

    private static HostCopyLister Lister(HarnessFactory harness, IHostInspector inspector)
        => new(harness.ContextLoader, inspector, new LocalHosts(harness, harness.FileSystem), harness.FileSystem, harness.Platform);

    private static SyncService Sync(HarnessFactory harness)
        => new(
            harness.ContextLoader,
            new ManifestBuilder(harness.FileSystem, harness.Platform),
            Local(harness),
            Substitute.For<ISyncTransportFactory>(),
            new LegsService(harness.ContextLoader, Substitute.For<IHostInspector>(), new KnownTrees(), harness.Platform, harness.Output),
            harness.GitClient,
            harness.FileSystem,
            harness.Platform,
            harness.Identity,
            harness.Output,
            new TestIsolation());

    private static RemoteSyncTransport Remote(IHostCommandRunner commands)
        => new(
            HostId.Ssh("vps"),
            new HostSession(new HostConnection { Host = HostId.Ssh("vps") }, "dssharness"),
            commands,
            new ConsoleHarnessOutput(new StringWriter(), new StringWriter(), verbose: false));

    /// <summary>
    /// A repository whose worktrees fit this machine's path budget however deep its temporary directory is, and which
    /// declares the hosts its tests keep copies on.
    /// </summary>
    /// <param name="temp">The repository.</param>
    /// <param name="keptUnder">
    /// Where the hosts keep their copies on this machine's disk, for a test that asks the hosts what they keep; left
    /// out, each keeps them where a real host would.
    /// </param>
    private static async Task<HarnessFactory> PrepareAsync(TempDirectory temp, string? keptUnder = null)
    {
        var harness = new HarnessFactory();
        await harness.InitializeHarnessAsync(
            temp.Path,
            TestContext.Current.CancellationToken,
            new HarnessConfig
            {
                Worktrees = new WorktreeSettings { Root = ".wt", PathBudgetReserve = 5, PathBudgetMargin = 2 },
                SshItems = { "pi", "mac" },
                Hosts = new HostsConfig
                {
                    Ssh =
                    {
                        ["pi"] = new SshHostConfig { RepositoryPath = keptUnder is null ? "/home/pi/repo" : Path.Combine(keptUnder, "pi", "repo") },
                        ["mac"] = new SshHostConfig { RepositoryPath = keptUnder is null ? "/Users/dev/repo" : Path.Combine(keptUnder, "mac", "repo") },
                    },
                },
            });
        return harness;
    }

    private static async Task<HarnessLayout> LayoutAsync(HarnessFactory harness, TempDirectory temp)
        => (await harness.ContextLoader.LoadAsync(temp.Path, TestContext.Current.CancellationToken)).Layout;

    /// <summary>
    /// The workers an earlier version of this tool kept beside a tree are removed as the workers kept in it are: a free
    /// one, and what a removal of one left aside, go; one a sweep of that version still claims, by the claim file beside
    /// it, is left and keeps the removal from passing; one no sync made is somebody's; another tree's are not looked at.
    /// </summary>
    [Fact]
    public async Task TheWorkersAnEarlierVersionKeptBesideATree_AreRemovedAsItsOwnAre()
    {
        using var hosts = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = new HarnessFactory();
        var tree = hosts.Combine("repo");
        string Former(string root, string name) => root + MutationWorkers.FormerFamily + name;
        var free = Former(tree, "357e24cw-1");
        var held = Former(tree, "357e24cw-2");
        var somebodys = Former(tree, "357e24cs-1");
        var aside = RemovalAside.Of(Former(tree, "357e24cw-3"));
        var anothers = Former(hosts.Combine("repo2"), "357e24cw-1");

        Directory.CreateDirectory(tree);

        foreach (var made in new[] { free, held, anothers })
        {
            await Local(harness).CreateRootAsync(made, CopyMark.Complete, cancellationToken);
            File.WriteAllText(Path.Combine(made, "main.c"), "int main;");
        }

        File.WriteAllText(Path.Combine(Directory.CreateDirectory(somebodys).FullName, "notes.txt"), "somebody's");
        File.WriteAllText(Path.Combine(Directory.CreateDirectory(aside).FullName, "part.o"), "what a removal left");

        var claims = new Claims { Held = { [held] = "run 20261007-101500-abcd, process 4242 on this machine" } };
        var removed = await Local(harness, claims: claims).RemoveFormerWorkersAsync([tree], cancellationToken: cancellationToken);

        Assert.Equal([free, aside], removed.Removed.Select(worker => worker.Path));
        Assert.Equal(
            [(somebodys, WorkerLeftAs.Somebodys), (held, WorkerLeftAs.Held)],
            removed.Left.Select(worker => (worker.Path, worker.As)));
        Assert.Equal(LegVerdict.RefusedLocked, removed.Verdict);
        Assert.All(new[] { free, aside }, path => Assert.False(Directory.Exists(path), path));
        Assert.All(new[] { held, somebodys, anothers }, path => Assert.True(Directory.Exists(path), path));
    }

    /// <summary>
    /// A sweep another machine sent here works in a copy of its own kept in the tree's .legs, its workers in that copy: a
    /// removal of the tree's workers meets them as the tree's own - a free one removed, one a live sweep claims left, which
    /// keeps the tree - and a copy no sync made is not looked in.
    /// </summary>
    [Fact]
    public async Task TheWorkersOfACopyKeptInTheTreesLegs_AreRemovedAsTheTreesOwn()
    {
        using var hosts = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = new HarnessFactory();
        var tree = hosts.Combine("repo");
        var copy = HarnessLayout.InSlot(tree, HarnessLayout.LegsDirectoryName, "357e24c");
        var somebodys = HarnessLayout.InSlot(tree, HarnessLayout.LegsDirectoryName, "1234567");
        var free = Worker(copy, "357e24cw-1");
        var held = Worker(copy, "357e24cw-2");
        var unlooked = Worker(somebodys, "1234567w-1");

        await Local(harness).CreateRootAsync(tree, CopyMark.Complete, cancellationToken);
        await Local(harness).CreateRootAsync(copy, CopyMark.Complete, cancellationToken);

        foreach (var made in new[] { free, held, unlooked })
        {
            await Local(harness).CreateRootAsync(made, CopyMark.Complete, cancellationToken);
            File.WriteAllText(Path.Combine(made, "main.c"), "int main;");
        }

        var claims = new Claims { Held = { [held] = "run 20261007-101500-abcd, process 4242 on this machine" } };
        var removed = await Local(harness, claims: claims).RemoveWorkersAsync(tree, cancellationToken: cancellationToken);

        Assert.Equal([free], removed.Removed.Select(worker => worker.Path));
        Assert.Equal(held, removed.Kept?.Path);
        Assert.Equal(LegVerdict.RefusedLocked, removed.Verdict);
        Assert.False(Directory.Exists(free));
        Assert.True(Directory.Exists(held));
        Assert.True(Directory.Exists(unlooked));
    }

    /// <summary>Where the mutation worker kept under <paramref name="name"/> in <paramref name="tree"/> is.</summary>
    private static string Worker(string tree, string name) => HarnessLayout.InSlot(tree, HarnessLayout.MutationsDirectoryName, name);

    private static LocalSyncTransport Local(HarnessFactory harness, IFileSystem? disk = null, ICopyClaims? claims = null)
        => new(disk ?? harness.FileSystem, new ManifestBuilder(harness.FileSystem, harness.Platform), harness.GitClient, harness.Platform, claims ?? new Claims());

    /// <summary>The real file system, except that what is at <paramref name="denied"/> cannot be asked, as a directory this user may not look in.</summary>
    private sealed class UnlookableAt(IFileSystem inner, string denied) : PassThroughFileSystem(inner)
    {
        public override PathKind KindOf(string path)
            => string.Equals(Path.GetFullPath(path), Path.GetFullPath(denied), StringComparison.Ordinal)
                ? throw new UnauthorizedAccessException($"Access to the path '{path}' is denied.")
                : base.KindOf(path);
    }

    /// <summary>Every host's copies kept on this machine's disk, reached as the host they stand for.</summary>
    private sealed class LocalHosts(HarnessFactory harness, IFileSystem disk, ICopyClaims? claims = null) : ISyncTransportFactory
    {
        public ISyncTransport For(HostReport host) => new RecordingTransport(Local(harness, disk, claims), reports: host.Host);
    }

    /// <summary>
    /// The claims on copies a test says stand, and those forgotten, in the order they were - each asked about by the
    /// copy's full path on this machine, as a sweep's claims are kept: a path spelt any other way names another file.
    /// </summary>
    private sealed class Claims : ICopyClaims
    {
        public Dictionary<string, string> Held { get; } = new(StringComparer.Ordinal);

        public List<string> Forgotten { get; } = [];

        /// <summary>What happens as a copy is asked about, where a test says: between its listing and its removal.</summary>
        public Action<string>? Asked { get; init; }

        public CopyHolder? HeldBy(string copy)
        {
            Asked?.Invoke(copy);

            return Held.GetValueOrDefault(Path.GetFullPath(copy)) is { } said ? new CopyHolder(said, Claim: null) : null;
        }

        public void Forget(string copy) => Forgotten.Add(Path.GetFullPath(copy));

        public bool Names(string name) => MutationWorkers.Named(name) is not null;

        public string Member => MutationWorkers.Member;

        public string SaidHeld(string holder) => MutationWorkers.SaidHeld(holder);
    }

    /// <summary>
    /// The claims of another family of copies kept in a slot of a tree - each named by a number alone, called a spare, and
    /// held by a run - so what the one removal takes from the family it is given is told from what it takes for workers.
    /// </summary>
    private sealed class SpareClaims : ICopyClaims
    {
        public Dictionary<string, string> Held { get; } = new(StringComparer.Ordinal);

        public List<string> Forgotten { get; } = [];

        public CopyHolder? HeldBy(string copy) => Held.GetValueOrDefault(Path.GetFullPath(copy)) is { } said ? new CopyHolder(said, Claim: null) : null;

        public void Forget(string copy) => Forgotten.Add(Path.GetFullPath(copy));

        public bool Names(string name) => name.Length > 0 && name.All(char.IsAsciiDigit);

        public string Member => "spare";

        public string SaidHeld(string holder) => $"a run still running holds it: {holder}";
    }

    /// <summary>
    /// What became of a tree's workers is one verdict, read by every command that removes them: refused-locked where a
    /// sweep still running holds one, failed where one could not be removed or told, stopped where the removal was
    /// stopped, and the most fundamental of them where several hold - with the worker that keeps the tree the first of
    /// that kind, and none where only somebody's directories were left, or the removal was stopped with none kept.
    /// </summary>
    [Theory]
    [InlineData("", false, "passed", null)]
    [InlineData("somebodys", false, "passed", null)]
    [InlineData("held", false, "refused-locked", "w0")]
    [InlineData("not-removed", false, "failed", "w0")]
    [InlineData("", true, "stopped", null)]
    [InlineData("somebodys", true, "stopped", null)]
    [InlineData("not-removed,held,held", false, "refused-locked", "w1")]
    [InlineData("somebodys,not-removed,not-removed", true, "failed", "w1")]
    [InlineData("held", true, "refused-locked", "w0")]
    public void WhatBecameOfATreesWorkers_IsOneVerdict_NamingTheWorkerThatKeepsTheTree(string left, bool interrupted, string verdict, string? kept)
    {
        var workers = new WorkersRemoval(
            [],
            [.. left.Split(',', StringSplitOptions.RemoveEmptyEntries).Select((kind, index) => new WorkerLeft(
                $"w{index}",
                kind,
                kind switch { "held" => WorkerLeftAs.Held, "not-removed" => WorkerLeftAs.NotRemoved, _ => WorkerLeftAs.Somebodys }))])
        {
            Interrupted = interrupted,
        };

        Assert.Equal(verdict, Verdicts.Display(workers.Verdict));
        Assert.Equal(kept, workers.Kept?.Path);
    }

    /// <summary>A disk that keeps which directories were weighed.</summary>
    private sealed class Weighs(IFileSystem inner) : PassThroughFileSystem(inner)
    {
        public List<string> Weighed { get; } = [];

        public override long DirectorySize(string path)
        {
            Weighed.Add(path);
            return base.DirectorySize(path);
        }
    }

    /// <summary>
    /// A disk on which a directory of one name is held open, so it cannot be deleted - or, where
    /// <paramref name="denied"/>, is not this user's to delete, which is raised as no kind of the other.
    /// </summary>
    private sealed class HoldsOpen(IFileSystem inner, string held, bool denied = false) : PassThroughFileSystem(inner)
    {
        public override void DeleteDirectory(string path)
        {
            if (string.Equals(Path.GetFileName(path), held, StringComparison.Ordinal))
            {
                var why = $"The process cannot access '{path}' because it is being used by another process.";

                throw denied ? new UnauthorizedAccessException(why) : new IOException(why);
            }

            base.DeleteDirectory(path);
        }
    }

    /// <summary>A disk on which the record of host copies can be read, and not written: a full disk, say.</summary>
    private sealed class WritesNoRecord(IFileSystem inner) : PassThroughFileSystem(inner)
    {
        public override void WriteAtomic(string path, Action<Stream> write)
        {
            if (string.Equals(Path.GetFileName(path), HostCopyRecord.FileName, StringComparison.Ordinal))
            {
                throw new IOException("There is not enough space on the disk.");
            }

            base.WriteAtomic(path, write);
        }
    }
}
