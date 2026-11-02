using DssHarness.Core.Agents;
using DssHarness.Core.Agents.Isolation;
using DssHarness.Core.Agents.Policy;
using DssHarness.Core.Agents.SideTasks;
using DssHarness.Core.Configuration;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Git;
using DssHarness.Core.Orchestration.Flow;
using DssHarness.Core.Platform;
using DssHarness.Core.Processes;
using DssHarness.Core.Repository;

namespace DssHarness.Tests;

/// <summary>
/// The operating system's isolation of agents' commands: the mechanism's command line, the profile's anchors and order,
/// the profile each ground's launch is given over a real repository - its places exact - the places made before a launch
/// and those never made, what keeps a launch from any isolation, and the mode's refusal and notice.
/// </summary>
public sealed class AgentIsolationTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// Bubblewrap binds the machine read-only with a device directory and a /proc of its own, puts the launch's temporary
    /// directory in the system's one's place and binds back each place read under it, then lays each write rule, the
    /// temporary directory and each anchor among them, parent first, every one skipped where its place has gone; then
    /// hides each hidden place - a directory under an empty tmpfs with what it reopens and each socket the child reaches
    /// there bound into it before it is made read-only, a file under /dev/null - and the machine's own escapes, binds every
    /// other socket it reaches, takes WSL's interoperation variable away, sets the profile's variables, and hands the
    /// program its own words after '--'. It asks no end with its parent.
    /// </summary>
    [Fact]
    public void Bubblewrap_BindsTheMachineReadOnly_ItsTempPrivate_LaysEachRuleAndAnchorInOrder_AndHidesLast()
    {
        var mechanism = new Bubblewrap("/usr/bin/bwrap", "0.9.0", [new IsolatedPath("/run/WSL", Directory: true), new IsolatedPath("/run/docker.sock", Directory: false), new IsolatedPath("/run/user/1000", Directory: true)]);
        var profile = new IsolationProfile(
            [new IsolatedPath("/home/me/.claude", Directory: true), new IsolatedPath("/r/w/c/slot", Directory: false)],
            [new IsolatedWrite("/r/w", Directory: true, Writable: true), new IsolatedWrite("/r/w/a/b/sealed", Directory: true, Writable: false)])
        {
            Temp = "/t/x",
            Reads = ["/tmp/repo", "/r", "/tmp/repo/wt"],
            Reopened = ["/home/me/.claude/shell-snapshots"],
            Environment = [new("TMPDIR", "/t/x"), new("npm_config_cache", "/c/npm")],
            Sockets = ["/tmp/ssh-x/agent.1", "/run/user/1000/keyring/ssh"],
        };

        var (program, arguments) = mechanism.Command(profile, "/usr/bin/git", ["add", "a.txt"]);

        Assert.Equal("/usr/bin/bwrap", program);
        Assert.Equal(
            [
                "--unshare-user", "--disable-userns", "--unshare-pid",
                "--ro-bind", "/", "/",
                "--dev", "/dev",
                "--proc", "/proc",
                "--bind", "/t/x", "/tmp",
                "--ro-bind-try", "/tmp/repo", "/tmp/repo",
                "--ro-bind-try", "/tmp/repo/wt", "/tmp/repo/wt",
                "--bind-try", "/r/w", "/r/w",
                "--bind-try", "/t/x", "/t/x",
                "--bind-try", "/r/w/c", "/r/w/c",
                "--bind-try", "/r/w/a", "/r/w/a",
                "--bind-try", "/r/w/a/b", "/r/w/a/b",
                "--ro-bind-try", "/r/w/a/b/sealed", "/r/w/a/b/sealed",
                "--tmpfs", "/home/me/.claude", "--ro-bind-try", "/home/me/.claude/shell-snapshots", "/home/me/.claude/shell-snapshots", "--remount-ro", "/home/me/.claude",
                "--ro-bind", "/dev/null", "/r/w/c/slot",
                "--tmpfs", "/run/WSL", "--remount-ro", "/run/WSL",
                "--ro-bind", "/dev/null", "/run/docker.sock",
                "--tmpfs", "/run/user/1000", "--bind-try", "/run/user/1000/keyring/ssh", "/run/user/1000/keyring/ssh", "--remount-ro", "/run/user/1000",
                "--bind-try", "/tmp/ssh-x/agent.1", "/tmp/ssh-x/agent.1",
                "--unsetenv", "WSL_INTEROP",
                "--setenv", "TMPDIR", "/t/x",
                "--setenv", "npm_config_cache", "/c/npm",
                "--", "/usr/bin/git", "add", "a.txt",
            ],
            arguments);
        Assert.Equal(("bubblewrap", "0.9.0", false, false, Bubblewrap.Unscoped), (mechanism.Name, mechanism.Version, mechanism.HoldsEveryCase, mechanism.Scoped, mechanism.Leaves));
        Assert.Equal(((string?)null, false, true), (mechanism.AnchorCost, mechanism.OwnTemp, mechanism.TakesHandedCommands));
        Assert.Null((mechanism with { Scoped = true }).Leaves);
    }

    /// <summary>
    /// A profile's anchors are the directories above any place a rule stands at - a place written by nobody above a
    /// writable root inside it among them - and the writable roots themselves, each only where the directory holding it is
    /// written, parent first; the machine is written nowhere a rule does not write, the nearest rule above a place deciding
    /// it and the launch's temporary directory among the rules.
    /// </summary>
    [Fact]
    public void TheAnchors_AreTheDirectoriesAboveAGuardedPlaceWhoseHolderIsWritten_TheMachineReadOnly()
    {
        var profile = new IsolationProfile(
            [new IsolatedPath("/home/me/.claude", Directory: true), new IsolatedPath("/r/w/c/slot", Directory: false)],
            [new IsolatedWrite("/r/w", Directory: true, Writable: true), new IsolatedWrite("/r/w/a/b/sealed", Directory: true, Writable: false), new IsolatedWrite("/r/w/a/b/sealed/root", Directory: true, Writable: true)])
        {
            Temp = "/t/x",
        };

        Assert.Equal(["/r/w/c", "/r/w/a", "/r/w/a/b", "/r/w/a/b/sealed"], profile.Anchors);
        Assert.Equal(
            (false, true, true, false, true, true, false),
            (profile.Writable("/r"), profile.Writable("/r/w"), profile.Writable("/r/w/a/b"), profile.Writable("/r/w/a/b/sealed/x"), profile.Writable("/r/w/a/b/sealed/root/x"), profile.Writable("/t/x/y"), profile.Writable("/t/xy")));
        Assert.Empty(IsolationProfile.Nothing.Anchors);
        Assert.False(IsolationProfile.Nothing.Writable("/"));
    }

    /// <summary>
    /// The write rules are laid with every place above another before it, whatever list it came from, so a place sealed
    /// inside a writable root stays sealed and a writable root inside a sealed place is written again; a place both sealed
    /// and writable stays sealed.
    /// </summary>
    [Fact]
    public void TheRules_LayEachPlaceAboveAnotherFirst_ASealedPlaceStayingSealed()
    {
        IReadOnlyList<IsolatedPath> sealedPlaces =
        [
            new IsolatedPath("/r/.worktrees/a1/.harness-config", Directory: true),
            new IsolatedPath("/r/.git/config", Directory: false),
            new IsolatedPath("/r/.orchestrators/o1/work/a1", Directory: true),
        ];

        Assert.Equal(
            [
                new IsolatedWrite("/r/.git/config", Directory: false, Writable: false),
                new IsolatedWrite("/r/.worktrees/a1", Directory: true, Writable: true),
                new IsolatedWrite("/r/.worktrees/a1/.harness-config", Directory: true, Writable: false),
                new IsolatedWrite("/r/.orchestrators/o1/work/a1", Directory: true, Writable: false),
            ],
            IsolationProfiles.Ordered(sealedPlaces, ["/r/.worktrees/a1", "/r/.orchestrators/o1/work/a1"], StringComparer.Ordinal));
    }

    /// <summary>
    /// A step working in its worktree is given, by its launch's profile: the machine read-only; written its worktree, its
    /// work directory, what git writes of a linked worktree - its own git directory, and the common directory's objects,
    /// refs and logs, never the common directory itself - and the isolation's tool caches, with its own temporary
    /// directory; hidden every credential slot there is and the Claude Code directory and its .claude.json, the shell
    /// snapshots read again; written by nobody, inside what it writes, the worktree's .git pointer and configuration
    /// directory, the tool's state in the worktree, its git directory's pointers, configuration and submodules, the common
    /// directory's replacements and alternates. A place not there inside what it writes is made first; the launch's own
    /// directory, which no command writes, is not. Its records say the mechanism, its pins hold the git directories of the
    /// worktree and of the main checkout alike, and its disposal takes the temporary directory away.
    /// </summary>
    [Fact]
    public async Task AStepInItsWorktree_IsGivenTheMachineReadOnly_ItsRootsAndGitWritten_ItsGuardedPlacesSealed()
    {
        using var temp = new TempDirectory();
        using var home = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("a1");
        var claude = Directory.CreateDirectory(Path.Combine(home.Path, ".claude")).FullName;
        await File.WriteAllTextAsync(claude + ".json", "{}", Token);
        var work = kit.Layout.WorkDirectory("a1");
        Directory.CreateDirectory(work);
        var launch = Path.Combine(kit.Layout.Directory, "steps", "think");
        var platform = new FixedIsolationPlatform(IsolationAvailability.Of(new PassThroughMechanism()));
        var pins = new GitDirectoryPins();
        var isolation = new AgentIsolation(platform, kit.Harness.GitClient, kit.Harness.FileSystem, new HomedPlatform(home.Path), _ => null, pins);
        var config = new HarnessConfig { Worktrees = OrchestrationKit.Settings };
        var ground = AgentStepGround.Of(
            config,
            definition: null,
            new WorkflowStep { Name = "think", Type = "reason" },
            new AgentWorkspace(worktree, work, kit.Main, [], worktree, kit.Layout.Directory),
            webSearchGiven: false,
            [claude]);

        var isolated = await isolation.ForLaunchAsync(config, ground, kit.Main, launch, Token);

        var git = Path.Combine(kit.Main, ".git");
        var linked = Assert.Single(Directory.GetDirectories(Path.Combine(git, "worktrees")));
        var root = IsolationRoot(home.Path);
        var caches = Path.Combine(root, IsolationCaches.Directory);
        var settings = Path.Combine(worktree, HarnessLayout.DirectoryName);
        var commands = isolated.Commands!;
        IReadOnlyList<IsolatedWrite> writes =
        [
            new(worktree, Directory: true, Writable: true),
            new(work, Directory: true, Writable: true),
            new(linked, Directory: true, Writable: true),
            new(Path.Combine(git, "objects"), Directory: true, Writable: true),
            new(Path.Combine(git, "refs"), Directory: true, Writable: true),
            new(Path.Combine(git, "logs"), Directory: true, Writable: true),
            new(caches, Directory: true, Writable: true),
            new(Path.Combine(worktree, ".git"), Directory: false, Writable: false),
            new(settings, Directory: true, Writable: false),
            .. ground.StateDirectories
                .Where(state => IsolationProfile.Covers(worktree, state) && !IsolationProfile.Covers(settings, state) && Directory.Exists(state))
                .Select(state => new IsolatedWrite(state, Directory: true, Writable: false)),
            new(Path.Combine(git, "objects", "info"), Directory: true, Writable: false),
            new(Path.Combine(git, "refs", "replace"), Directory: true, Writable: false),
            new(Path.Combine(linked, "config.worktree"), Directory: false, Writable: false),
            new(Path.Combine(linked, "commondir"), Directory: false, Writable: false),
            new(Path.Combine(linked, "gitdir"), Directory: false, Writable: false),
            new(Path.Combine(linked, "modules"), Directory: true, Writable: false),
        ];

        Assert.Equal((AgentIsolationMode.Required, false, null, null), (isolated.Mode, isolated.Refused, isolated.Notice, isolated.Refusal("'think'")));
        Assert.Equal([kit.Main], platform.Probed);
        Assert.Equal(
            [
                .. HarnessLayout.SecretSlotDirectoriesUnder(kit.Main).Where(Directory.Exists).Select(slot => new IsolatedPath(slot, Directory: true)),
                new IsolatedPath(claude, Directory: true),
                new IsolatedPath(claude + ".json", Directory: false),
            ],
            commands.Hidden);
        Assert.Equal(writes.OrderBy(write => write.Path, StringComparer.Ordinal), commands.Writes.OrderBy(write => write.Path, StringComparer.Ordinal));
        Assert.Equal([worktree, kit.Main, work], commands.Reads);
        Assert.Equal([Path.Combine(claude, AgentIsolation.ShellSnapshots)], commands.Reopened);
        Assert.Equal(Path.Combine(root, IsolationCaches.TempDirectory), Path.GetDirectoryName(commands.Temp));
        Assert.Equal([new("TMPDIR", commands.Temp!), .. IsolationCaches.Environment(caches)], commands.Environment);
        Assert.True(Directory.Exists(Path.Combine(linked, "modules")) && Directory.Exists(Path.Combine(git, "refs", "replace")) && Directory.Exists(commands.Temp) && Directory.Exists(caches));
        Assert.False(File.Exists(Path.Combine(git, "config.worktree")) || Directory.Exists(Path.Combine(git, "modules")), "a place in the common directory itself, which no command writes, is made");
        Assert.False(Directory.Exists(launch), launch);
        Assert.Equal((new GitDirectoryPin(worktree, linked, git), new GitDirectoryPin(kit.Main, git, git)), (pins.Of(worktree), pins.Of(kit.Main)));
        Assert.Equal(new AgentIsolationRecord(AgentIsolationMode.Required) { Mechanism = "test", Version = "1.0" }, isolated.Record);
        Assert.Null(isolated.ForClaudeCode);

        var again = await isolation.ForLaunchAsync(config, ground, kit.Main, launch, Token);

        Assert.Equal(commands.Temp, again.Commands!.Temp);

        isolated.Dispose();

        Assert.False(Directory.Exists(commands.Temp), commands.Temp);
    }

    /// <summary>
    /// Under a mechanism that gives the child a temporary directory of its own and takes no command handed to it, a launch
    /// names no temporary directory and makes none, its variables the declared caches' alone; a Claude Code launch's whole
    /// process is given the commands' profile with the Claude Code directory and its file read, the slots still hidden; and
    /// the records say what holding an anchor costs there.
    /// </summary>
    [Fact]
    public async Task AMechanismWithItsOwnTemp_TakingNoHandedCommand_GivesClaudeCodeWholeTheProfile_ItsDirectoriesRead()
    {
        using var temp = new TempDirectory();
        using var home = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var claude = Directory.CreateDirectory(Path.Combine(home.Path, ".claude")).FullName;
        await File.WriteAllTextAsync(claude + ".json", "{}", Token);
        var mechanism = new PassThroughMechanism { OwnTemp = true, TakesHandedCommands = false, AnchorCost = "read alone" };
        var isolation = new AgentIsolation(new FixedIsolationPlatform(IsolationAvailability.Of(mechanism)), kit.Harness.GitClient, kit.Harness.FileSystem, new HomedPlatform(home.Path), _ => null);
        var config = new HarnessConfig { Worktrees = OrchestrationKit.Settings };
        var layout = new HarnessLayout(kit.Main, kit.Main);

        using var isolated = await isolation.ForLaunchAsync(config, SideTaskPolicy.Ground(layout, config, [claude]), kit.Main, SideTaskLayout.Of(layout, "one").Directory, Token);

        var root = IsolationRoot(home.Path);
        var slots = HarnessLayout.SecretSlotDirectoriesUnder(kit.Main).Where(Directory.Exists).Select(slot => new IsolatedPath(slot, Directory: true)).ToList();
        var whole = isolated.ForClaudeCode!;

        Assert.Equal((null, false), (isolated.Commands!.Temp, Directory.Exists(Path.Combine(root, IsolationCaches.TempDirectory))));
        Assert.Equal(IsolationCaches.Environment(Path.Combine(root, IsolationCaches.Directory)), isolated.Commands.Environment);
        Assert.Equal([.. slots, new IsolatedPath(claude, Directory: true), new IsolatedPath(claude + ".json", Directory: false)], isolated.Commands.Hidden);
        Assert.Same(mechanism, whole.Mechanism);
        Assert.Equal(slots, whole.Profile.Hidden);
        Assert.Equal((isolated.Commands.Writes, isolated.Commands.Environment), (whole.Profile.Writes, whole.Profile.Environment));
        Assert.Empty(whole.Profile.Reopened);
        Assert.Equal("read alone", isolated.Record.AnchorCost);
    }

    /// <summary>
    /// A side-task works over the main checkout, which it writes with its git directory whole: inside it, its git's
    /// code-running files - the hooks core.hooksPath names and the git directory's own beside them - its submodules, its
    /// replacements, its alternates and a file its configuration includes only on another branch with the one that file
    /// includes in turn, its configuration
    /// directory and the tool's state are written by nobody, its slots hidden. Its records' directory, beneath the sealed
    /// side-tasks directory, is never made - so a claim of its name finds none there - and a state file that is not there
    /// is never made, for the tool reads it as meaning something.
    /// </summary>
    [Fact]
    public async Task ASideTask_WritesTheMainTree_ButItsGitsCodeItsConfigurationAndTheState_ItsRecordsNeverMade()
    {
        using var temp = new TempDirectory();
        using var home = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var isolation = new AgentIsolation(new FixedIsolationPlatform(IsolationAvailability.Of(new PassThroughMechanism())), kit.Harness.GitClient, kit.Harness.FileSystem, new HomedPlatform(home.Path), _ => null);
        var config = new HarnessConfig { Worktrees = OrchestrationKit.Settings, AgentPolicy = new AgentPolicyConfig { Isolation = AgentIsolationMode.Preferred } };
        var layout = new HarnessLayout(kit.Main, kit.Main);
        var ground = SideTaskPolicy.Ground(layout, config, []);
        var records = SideTaskLayout.Of(layout, "one").Directory;

        // Hooks run from elsewhere, and a file included only on another branch, which includes another in turn: the common
        // directory's own hooks and both files are held all the same.
        await kit.Harness.RunGitAsync(kit.Main, ["config", "core.hooksPath", ".husky"], Token);
        await kit.Harness.RunGitAsync(kit.Main, ["config", "includeIf.onbranch:elsewhere.path", "shared.config"], Token);
        await File.WriteAllTextAsync(Path.Combine(kit.Main, ".git", "shared.config"), "[include]\n\tpath = deeper.config\n", Token);
        await File.WriteAllTextAsync(Path.Combine(kit.Main, ".git", "deeper.config"), string.Empty, Token);

        using var isolated = await isolation.ForLaunchAsync(config, ground, kit.Main, records, Token);

        var writes = isolated.Commands!.Writes;

        Assert.Equal(new IsolatedWrite(kit.Main, Directory: true, Writable: true), writes[0]);
        Assert.Contains(new IsolatedWrite(Path.Combine(kit.Main, ".git"), Directory: true, Writable: true), writes);
        Assert.Contains(new IsolatedWrite(Path.Combine(kit.Main, ".git", "hooks"), Directory: true, Writable: false), writes);
        Assert.Contains(new IsolatedWrite(Path.Combine(kit.Main, ".husky"), Directory: true, Writable: false), writes);
        Assert.Contains(new IsolatedWrite(Path.Combine(kit.Main, ".git", "modules"), Directory: true, Writable: false), writes);
        Assert.Contains(new IsolatedWrite(Path.Combine(kit.Main, ".git", "refs", "replace"), Directory: true, Writable: false), writes);
        Assert.Contains(new IsolatedWrite(Path.Combine(kit.Main, ".git", "objects", "info"), Directory: true, Writable: false), writes);
        Assert.Contains(new IsolatedWrite(Path.Combine(kit.Main, ".git", "shared.config"), Directory: false, Writable: false), writes);
        Assert.Contains(new IsolatedWrite(Path.Combine(kit.Main, ".git", "deeper.config"), Directory: false, Writable: false), writes);
        Assert.Contains(new IsolatedWrite(Path.Combine(kit.Main, HarnessLayout.DirectoryName), Directory: true, Writable: false), writes);
        Assert.Contains(new IsolatedWrite(Path.Combine(kit.Main, HarnessLayout.SideTasksDirectoryName), Directory: true, Writable: false), writes);
        Assert.DoesNotContain(writes, write => write.Path == records || Path.GetFileName(write.Path) == HarnessLayout.SyncedCopyMarkerName);
        Assert.False(Directory.Exists(records), records);
        Assert.False(File.Exists(Path.Combine(kit.Main, HarnessLayout.DirectoryName, HarnessLayout.SyncedCopyMarkerName)));
        Assert.Equal(AgentIsolationMode.Preferred, isolated.Record.Mode);
    }

    /// <summary>
    /// A main checkout's git directory, written whole, holds its commondir and its worktrees/ from the work: the commondir
    /// made naming that directory itself, which git takes as no pointer - its common directory is its git directory still -
    /// and a later launch takes it as it stands; one that leads elsewhere keeps a launch from any isolation, with its own
    /// remedy, and a gitdir there, which git never follows from a main checkout, keeps nothing from it.
    /// </summary>
    [Fact]
    public async Task AMainCheckoutsCommondir_IsMadeNamingItselfAndHeld_AndOneLeadingElsewhere_LeavesNoIsolation()
    {
        using var temp = new TempDirectory();
        using var home = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var config = new HarnessConfig { Worktrees = OrchestrationKit.Settings };
        var layout = new HarnessLayout(kit.Main, kit.Main);
        var ground = SideTaskPolicy.Ground(layout, config, []);
        var records = SideTaskLayout.Of(layout, "one").Directory;
        var git = Path.Combine(kit.Main, ".git");
        var pointer = Path.Combine(git, AgentIsolation.CommonDirectoryFile);
        AgentIsolation Isolation() => new(new FixedIsolationPlatform(IsolationAvailability.Of(new PassThroughMechanism())), kit.Harness.GitClient, kit.Harness.FileSystem, new HomedPlatform(home.Path), _ => null);

        using var first = await Isolation().ForLaunchAsync(config, ground, kit.Main, records, Token);

        Assert.Equal(AgentIsolation.ItsOwnDirectory, await File.ReadAllTextAsync(pointer, Token));
        Assert.Contains(new IsolatedWrite(pointer, Directory: false, Writable: false), first.Commands!.Writes);
        Assert.Contains(new IsolatedWrite(Path.Combine(git, "worktrees"), Directory: true, Writable: false), first.Commands.Writes);

        var named = (await kit.Harness.GitClient.RunAsync(kit.Main, ["rev-parse", "--path-format=absolute", "--git-dir", "--git-common-dir"], cancellationToken: Token)).OutputLines;

        Assert.Equal(2, named.Count);
        Assert.All(named, line => PathAssert.Same(git, Path.GetFullPath(line.Trim())));

        using var again = await Isolation().ForLaunchAsync(config, ground, kit.Main, records, Token);

        Assert.Equal((false, "test"), (again.Refused, again.Mechanism?.Name));

        await File.WriteAllTextAsync(pointer, "/elsewhere/.git", Token);
        using var planted = await Isolation().ForLaunchAsync(config, ground, kit.Main, records, Token);

        Assert.Equal(
            (true, (string?)null, $"'think' is refused: agentPolicy.isolation is \"required\" and '{pointer}' leads git to another repository's own files, where a main checkout's git directory holds none but one naming itself - something made it, so no agent that may run a command starts here; look at where '{pointer}' leads, and remove it"),
            (planted.Refused, planted.Mechanism?.Name, planted.Refusal("'think'")));

        File.Delete(pointer);
        await File.WriteAllTextAsync(Path.Combine(git, "gitdir"), "/elsewhere/.git", Token);
        using var beside = await Isolation().ForLaunchAsync(config, ground, kit.Main, records, Token);

        Assert.Equal((false, "test"), (beside.Refused, beside.Mechanism?.Name));
    }

    /// <summary>
    /// A worktree added while a launch ran - beside a step in another worktree, or beside a side-task in the main checkout,
    /// by the tool or by a person - fails nothing once it has: the work writes no worktree's git directory but its own, so
    /// nothing made there can be its; and every linked worktree's .git file standing inside a place the work writes is held.
    /// </summary>
    [Fact]
    public async Task AWorktreeAddedWhileALaunchRan_FailsNothing_AndEveryLinkedWorktreesGitFileWhereTheWorkWrites_IsHeld()
    {
        using var temp = new TempDirectory();
        using var home = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("a1");
        var inside = Path.Combine(kit.Main, "inside");
        await kit.Harness.RunGitAsync(kit.Main, ["worktree", "add", "--quiet", inside, "-b", "inside"], Token);
        var config = new HarnessConfig { Worktrees = OrchestrationKit.Settings };
        var layout = new HarnessLayout(kit.Main, kit.Main);
        var isolation = new AgentIsolation(new FixedIsolationPlatform(IsolationAvailability.Of(new PassThroughMechanism())), kit.Harness.GitClient, kit.Harness.FileSystem, new HomedPlatform(home.Path), _ => null);
        var step = AgentStepGround.Of(
            config,
            definition: null,
            new WorkflowStep { Name = "think", Type = "reason" },
            new AgentWorkspace(worktree, kit.Layout.WorkDirectory("a1"), kit.Main, [], worktree, kit.Layout.Directory),
            webSearchGiven: false,
            []);

        using var sideTask = await isolation.ForLaunchAsync(config, SideTaskPolicy.Ground(layout, config, []), kit.Main, SideTaskLayout.Of(layout, "one").Directory, Token);
        using var inWorktree = await isolation.ForLaunchAsync(config, step, kit.Main, Path.Combine(kit.Layout.Directory, "steps", "think"), Token);

        _ = await kit.CreateAgentAsync("a2");
        await kit.Harness.RunGitAsync(kit.Main, ["worktree", "add", "--quiet", Path.Combine(temp.Path, "by-hand"), "-b", "by-hand"], Token);

        Assert.Equal(((string?)null, (string?)null), (await sideTask.AfterwardAsync(Token), await inWorktree.AfterwardAsync(Token)));
        Assert.Contains(new IsolatedWrite(Path.Combine(inside, ".git"), Directory: false, Writable: false), sideTask.Commands!.Writes);
        Assert.DoesNotContain(inWorktree.Commands!.Writes, write => write.Path == Path.Combine(inside, ".git"));
    }

    /// <summary>
    /// The state of an operation under way made in a git directory the work wrote - a main checkout's, a worktree's own -
    /// or changed there while it ran is what its decision answers once it has, naming it and the remedy: remove it, never
    /// continue or abort it; one there before and left as it was, and one in a git directory the work did not write, are
    /// not.
    /// </summary>
    [Fact]
    public async Task AnOperationsStateMadeOrChangedWhereTheWorkWrote_IsAnsweredAfterward_ButOneLeftAsItWasIsNot()
    {
        using var temp = new TempDirectory();
        using var home = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("a1");
        var config = new HarnessConfig { Worktrees = OrchestrationKit.Settings };
        var layout = new HarnessLayout(kit.Main, kit.Main);
        var git = Path.Combine(kit.Main, ".git");
        var linked = Assert.Single(Directory.GetDirectories(Path.Combine(git, "worktrees")));
        var isolation = new AgentIsolation(new FixedIsolationPlatform(IsolationAvailability.Of(new PassThroughMechanism())), kit.Harness.GitClient, kit.Harness.FileSystem, new HomedPlatform(home.Path), _ => null);
        var step = AgentStepGround.Of(
            config,
            definition: null,
            new WorkflowStep { Name = "think", Type = "reason" },
            new AgentWorkspace(worktree, kit.Layout.WorkDirectory("a1"), kit.Main, [], worktree, kit.Layout.Directory),
            webSearchGiven: false,
            []);
        static string Said(string place, string how) => $"'{place}' {how} while it ran: the state of an operation under way, which git's next continue outside - the user's, an editor's - goes on with as it lies, running each exec line its todo holds - so nothing goes on over the trees; look at what it holds, and remove it - never continue the operation, nor abort it, which puts the branch where the state says";

        using var hooks = await isolation.ForExecutionAsync(config, ExecutionGrounds.OfHooks(config, kit.Main, kit.Main, sshAgent: false), kit.Main, Token);
        using var inWorktree = await isolation.ForLaunchAsync(config, step, kit.Main, Path.Combine(kit.Layout.Directory, "steps", "think"), Token);
        var rebase = Directory.CreateDirectory(Path.Combine(git, "rebase-merge")).FullName;
        await File.WriteAllTextAsync(Path.Combine(rebase, "git-rebase-todo"), "exec touch ran\n", Token);
        var applying = Directory.CreateDirectory(Path.Combine(linked, "rebase-apply")).FullName;

        Assert.Equal(Said(rebase, "was made"), await hooks.AfterwardAsync(Token));
        Assert.Equal(Said(applying, "was made"), await inWorktree.AfterwardAsync(Token));

        Directory.Delete(applying);
        using var leftAsItWas = await isolation.ForExecutionAsync(config, ExecutionGrounds.OfHooks(config, kit.Main, kit.Main, sshAgent: false), kit.Main, Token);
        using var stillInWorktree = await isolation.ForLaunchAsync(config, step, kit.Main, Path.Combine(kit.Layout.Directory, "steps", "think"), Token);

        Assert.Equal(((string?)null, (string?)null), (await leftAsItWas.AfterwardAsync(Token), await stillInWorktree.AfterwardAsync(Token)));

        await File.AppendAllTextAsync(Path.Combine(rebase, "git-rebase-todo"), "exec touch more\n", Token);

        Assert.Equal(Said(rebase, "changed"), await leftAsItWas.AfterwardAsync(Token));
        Assert.Null(await stillInWorktree.AfterwardAsync(Token));
    }

    /// <summary>
    /// A replacement made or moved in a repository whose refs the work wrote is what its decision answers once it has -
    /// as git lists the replacements, loose or packed, so the tool opens no ref file the work wrote itself - and one there
    /// before and left as it was is not.
    /// </summary>
    [Fact]
    public async Task AReplacementMadeOrMoved_IsAnsweredAfterward_AsGitListsIt_NoRefFileOpenedByTheTool()
    {
        using var temp = new TempDirectory();
        using var home = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var config = new HarnessConfig { Worktrees = OrchestrationKit.Settings };
        var layout = new HarnessLayout(kit.Main, kit.Main);
        var ground = SideTaskPolicy.Ground(layout, config, []);
        var records = SideTaskLayout.Of(layout, "one").Directory;
        var git = Path.Combine(kit.Main, ".git");
        var opened = new OpeningFileSystem(kit.Harness.FileSystem);
        var isolation = new AgentIsolation(new FixedIsolationPlatform(IsolationAvailability.Of(new PassThroughMechanism())), kit.Harness.GitClient, opened, new HomedPlatform(home.Path), _ => null);
        var replaced = (await kit.Harness.RunGitAsync(kit.Main, ["rev-parse", "HEAD"], Token)).StandardOutput.Trim();
        await kit.Harness.RunGitAsync(kit.Main, ["commit", "--quiet", "--allow-empty", "-m", "in its place"], Token);
        var inItsPlace = (await kit.Harness.RunGitAsync(kit.Main, ["rev-parse", "HEAD"], Token)).StandardOutput.Trim();
        await kit.Harness.RunGitAsync(kit.Main, ["commit", "--quiet", "--allow-empty", "-m", "moved to"], Token);
        var movedTo = (await kit.Harness.RunGitAsync(kit.Main, ["rev-parse", "HEAD"], Token)).StandardOutput.Trim();
        var name = $"refs/replace/{replaced}";
        var said = $"'{git}' came to hold {name} while it ran: a replacement, which puts other content in place of an object for every git started outside that reads it - so nothing goes on over the trees; look at what it puts in place, and remove it with git update-ref -d {name}";

        using var making = await isolation.ForLaunchAsync(config, ground, kit.Main, records, Token);
        await kit.Harness.RunGitAsync(kit.Main, ["replace", replaced, inItsPlace], Token);

        Assert.Equal(said, await making.AfterwardAsync(Token));

        await kit.Harness.RunGitAsync(kit.Main, ["pack-refs", "--all"], Token);
        using var moving = await isolation.ForLaunchAsync(config, ground, kit.Main, records, Token);

        Assert.Null(await moving.AfterwardAsync(Token));

        await kit.Harness.RunGitAsync(kit.Main, ["replace", "-f", replaced, movedTo], Token);

        Assert.Equal(said, await moving.AfterwardAsync(Token));
        Assert.DoesNotContain(opened.Opened, path => Path.GetFileName(path) == "packed-refs" || path.Contains(Path.Combine(".git", "refs"), StringComparison.Ordinal));
    }

    /// <summary>
    /// In a repository that keeps its refs in a reftable, a step in a linked worktree writes the common directory's
    /// reftable/ - where git writes every ref of it, its refs/ a stub - and a replacement made there, where no file under
    /// refs/replace/ and no packed-refs line names it, is what its decision answers once it has.
    /// </summary>
    [Fact]
    public async Task InAReftableRepository_AWorktreeWritesItsRefsWhereGitKeepsThem_AndAReplacementThereIsAnswered()
    {
        using var temp = new TempDirectory();
        using var home = new TempDirectory();
        var harness = new HarnessFactory();
        var main = Directory.CreateDirectory(Path.Combine(temp.Path, "main")).FullName;
        var made = await harness.GitClient.RunAsync(main, ["init", "--quiet", "--ref-format=reftable", "-b", "main", "."], cancellationToken: Token);

        Assert.SkipUnless(made.Succeeded, $"this git keeps no refs in a reftable: {made.FailureMessage}");

        await harness.RunGitAsync(main, ["config", "user.email", "test@example.com"], Token);
        await harness.RunGitAsync(main, ["config", "user.name", "Test"], Token);
        await harness.RunGitAsync(main, ["commit", "--quiet", "--allow-empty", "-m", "base"], Token);
        var linkedTree = Path.Combine(temp.Path, "linked");
        await harness.RunGitAsync(main, ["worktree", "add", "--quiet", linkedTree, "-b", "linked"], Token);
        var git = Path.Combine(main, ".git");
        var config = new HarnessConfig();
        var isolation = new AgentIsolation(new FixedIsolationPlatform(IsolationAvailability.Of(new PassThroughMechanism())), harness.GitClient, harness.FileSystem, new HomedPlatform(home.Path), _ => null);

        using var legs = await isolation.ForExecutionAsync(config, ExecutionGrounds.OfLegs(config, main, linkedTree), main, Token);
        var replaced = (await harness.RunGitAsync(main, ["rev-parse", "HEAD"], Token)).StandardOutput.Trim();
        await harness.RunGitAsync(linkedTree, ["commit", "--quiet", "--allow-empty", "-m", "in its place"], Token);
        await harness.RunGitAsync(linkedTree, ["replace", replaced, "HEAD"], Token);

        Assert.Contains(new IsolatedWrite(Path.Combine(git, "reftable"), Directory: true, Writable: true), legs.Profile!.Writes);
        Assert.Contains(new IsolatedWrite(Path.Combine(git, "refs"), Directory: true, Writable: true), legs.Profile.Writes);
        Assert.Equal(
            $"'{git}' came to hold refs/replace/{replaced} while it ran: a replacement, which puts other content in place of an object for every git started outside that reads it - so nothing goes on over the trees; look at what it puts in place, and remove it with git update-ref -d refs/replace/{replaced}",
            await legs.AfterwardAsync(Token));
    }

    /// <summary>
    /// The Claude Code directories an agent's launch and the tool's executions of its work are kept from are one list:
    /// Claude Code's own default in the home, the tool's own, and the one the slot holds for the claude-code client's
    /// logins - each hidden from a leg's work as from an agent's commands.
    /// </summary>
    [Fact]
    public async Task EveryClaudeCodeDirectory_IsHiddenFromALaunchAndAnExecutionAlike()
    {
        using var temp = new TempDirectory();
        using var home = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var dot = Directory.CreateDirectory(Path.Combine(home.Path, ".claude")).FullName;
        var own = Directory.CreateDirectory(Path.Combine(home.Path, "tool-claude")).FullName;
        var login = Directory.CreateDirectory(Path.Combine(home.Path, "another-login")).FullName;
        var slot = Path.Combine(kit.Main, ".harness-config", "workflows", ".env", "claude.env");
        OrchestrationKit.Write(kit.Main, Path.Combine(".harness-config", "workflows", ".env", "claude.env"), $"CLAUDE_CONFIG_DIR={login}\n");
        kit.Harness.FilePermissions.ProtectSecret(slot);
        var platform = new HomedPlatform(home.Path);
        var keys = new Core.Secrets.AgentKeyStore(kit.Harness.FileSystem, kit.Harness.FilePermissions, platform);
        var directories = new ClaudeCodeDirectories(kit.Harness.FileSystem, platform, keys, name => name == "CLAUDE_CONFIG_DIR" ? own : null);
        var config = new HarnessConfig { Worktrees = OrchestrationKit.Settings };
        var isolation = new AgentIsolation(new FixedIsolationPlatform(IsolationAvailability.Of(new PassThroughMechanism())), kit.Harness.GitClient, kit.Harness.FileSystem, platform, _ => null, claudeCode: directories);

        using var legs = await isolation.ForExecutionAsync(config, ExecutionGrounds.OfLegs(config, kit.Main, kit.Main), kit.Main, Token);

        Assert.Equal([dot, own, login], directories.Of(config, kit.Main));
        Assert.Equal([dot, own, login], legs.Profile!.Hidden.Where(place => !IsolationProfile.Covers(kit.Main, place.Path)).Select(place => place.Path));
        Assert.Contains(login, new ReasonLaunch(kit.Harness.FileSystem, platform, keys, null, isolation).ClaudeDirectories(config, new HarnessLayout(kit.Main, kit.Main)));
    }

    /// <summary>
    /// Every package manager's store a build fills is moved into the isolation's own - pnpm's, Yarn 2 and later's, Bun's
    /// and pub's among npm's, Yarn 1's, pip's and the rest - and the README names each as the code declares it.
    /// </summary>
    [Fact]
    public void EveryStoreABuildFills_IsDeclared_AndTheReadmeNamesEach()
    {
        var declared = IsolationCaches.Declared.Select(cache => cache.Variable).ToList();
        var readme = Prose.Document("README.md");

        Assert.Superset(new HashSet<string> { "npm_config_store_dir", "YARN_GLOBAL_FOLDER", "BUN_INSTALL_CACHE_DIR", "PUB_CACHE", "npm_config_cache", "YARN_CACHE_FOLDER" }, declared.ToHashSet());
        Assert.All(declared, variable => Assert.Contains($"`{variable}`", readme, StringComparison.Ordinal));
    }

    /// <summary>
    /// On Linux, where <c>XDG_CACHE_HOME</c> places Playwright's browsers and the isolation moves it, the browsers the user
    /// installed are read where they are, a launch's commands told so; not where the variable names others already, nor
    /// where there are none, nor on another system, whose Playwright keeps them where nothing is moved.
    /// </summary>
    [Fact]
    public async Task PlaywrightsBrowsers_AreReadWhereTheUserInstalledThem_OnLinux()
    {
        var home = Path.GetFullPath("/h");
        var xdg = Path.GetFullPath("/x");
        var browsers = Path.Combine(home, ".cache", "ms-playwright");
        bool There(string path) => path == browsers || path == Path.Combine(xdg, "ms-playwright");

        Assert.Equal([new KeyValuePair<string, string>(IsolationCaches.PlaywrightBrowsersVariable, browsers)], IsolationCaches.Installed(home, PlatformId.Linux, _ => null, There));
        Assert.Equal([new KeyValuePair<string, string>(IsolationCaches.PlaywrightBrowsersVariable, Path.Combine(xdg, "ms-playwright"))], IsolationCaches.Installed(home, PlatformId.Linux, name => name == "XDG_CACHE_HOME" ? xdg : null, There));
        Assert.Empty(IsolationCaches.Installed(home, PlatformId.Linux, name => name == IsolationCaches.PlaywrightBrowsersVariable ? "/elsewhere" : null, There));
        Assert.Empty(IsolationCaches.Installed(home, PlatformId.Linux, _ => null, _ => false));
        Assert.Empty(IsolationCaches.Installed(home, PlatformId.MacOs, _ => null, There));

        using var temp = new TempDirectory();
        using var user = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var installed = Directory.CreateDirectory(Path.Combine(user.Path, ".cache", "ms-playwright")).FullName;
        var config = new HarnessConfig { Worktrees = OrchestrationKit.Settings };
        var layout = new HarnessLayout(kit.Main, kit.Main);
        var isolation = new AgentIsolation(new FixedIsolationPlatform(IsolationAvailability.Of(new PassThroughMechanism())), kit.Harness.GitClient, kit.Harness.FileSystem, HostDoubles.Platform(PlatformId.Linux, home: user.Path), _ => null);

        using var launched = await isolation.ForLaunchAsync(config, SideTaskPolicy.Ground(layout, config, []), kit.Main, SideTaskLayout.Of(layout, "one").Directory, Token);

        Assert.Equal(new KeyValuePair<string, string>(IsolationCaches.PlaywrightBrowsersVariable, installed), launched.Commands!.Environment[^1]);
    }

    /// <summary>
    /// A file git's configuration includes that is not there, inside a place the agent writes, keeps the launch from any
    /// isolation - a command could make it and every git started outside would read it - saying which and the remedy; one
    /// outside every written place is none of its concern.
    /// </summary>
    [Fact]
    public async Task AnIncludedFileNotThere_WhereTheAgentWrites_LeavesTheLaunchWithNoIsolation()
    {
        using var temp = new TempDirectory();
        using var home = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var config = new HarnessConfig { Worktrees = OrchestrationKit.Settings };
        var layout = new HarnessLayout(kit.Main, kit.Main);
        var ground = SideTaskPolicy.Ground(layout, config, []);
        var isolation = new AgentIsolation(new FixedIsolationPlatform(IsolationAvailability.Of(new PassThroughMechanism())), kit.Harness.GitClient, kit.Harness.FileSystem, new HomedPlatform(home.Path), _ => null);
        await kit.Harness.RunGitAsync(kit.Main, ["config", "include.path", "~/not-there.config"], Token);
        await kit.Harness.RunGitAsync(kit.Main, ["config", "includeIf.onbranch:elsewhere.path", "../shared.config"], Token);
        var target = Path.Combine(kit.Main, "shared.config");

        using var isolated = await isolation.ForLaunchAsync(config, ground, kit.Main, SideTaskLayout.Of(layout, "one").Directory, Token);

        Assert.Equal(
            $"'side-task' is refused: agentPolicy.isolation is \"required\" and git's configuration includes '{target}', which is not there, inside a place the agent writes: a command could make it, and every git started outside would read it, so no agent that may run a command starts here; make '{target}' - an empty file includes nothing - or take its include out of git's configuration",
            isolated.Refusal("'side-task'"));
        Assert.False(File.Exists(target));
    }

    /// <summary>
    /// The files git's configuration names to include, whether the condition holds or not: <c>~/</c> in the home, a relative
    /// path against the directory of the file that names it, a whole path as it is; one under another's home, under git's
    /// own install, or named relatively by no file, none.
    /// </summary>
    [Fact]
    public void IncludeTargets_AreEveryIncludesFile_AsGitResolvesIt()
    {
        var tree = Path.GetFullPath("/r");
        var home = Path.GetFullPath("/h");
        var whole = Path.GetFullPath("/etc/extra.config");
        var answered = string.Join('\0', [
            "file:.git/config", "include.path\nshared.config",
            "file:.git/config", "includeif.onbranch:main.path\n../team.config",
            "file:" + Path.Combine(home, ".gitconfig"), "include.path\n~/work.config",
            "file:" + Path.Combine(home, ".gitconfig"), "includeif.gitdir:~/src/.path\n" + whole,
            "file:.git/config", "core.hookspath\n.husky",
            "file:.git/config", "include.path\n~other/x.config",
            "file:.git/config", "include.path\n%(prefix)/etc/x.config",
            "command line:", "include.path\nrelative.config",
            string.Empty,
        ]);

        Assert.Equal(
            [Path.Combine(tree, ".git", "shared.config"), Path.Combine(tree, "team.config"), Path.Combine(home, "work.config"), whole],
            AgentIsolation.IncludeTargets(answered, tree, home));
    }

    /// <summary>
    /// A link inside a place the agent writes, on the way to a guarded place, keeps the launch from any isolation - a
    /// command could put another in its place, leading the tool elsewhere - saying which and the remedy.
    /// </summary>
    [Fact]
    public async Task ALinkWhereTheAgentWritesOnTheWayToAGuardedPlace_LeavesTheLaunchWithNoIsolation()
    {
        using var temp = new TempDirectory();
        using var home = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var layout = new HarnessLayout(kit.Main, kit.Main);
        var config = new HarnessConfig { Worktrees = OrchestrationKit.Settings };
        var elsewhere = Directory.CreateDirectory(Path.Combine(temp.Path, "elsewhere")).FullName;
        var linked = Path.Combine(kit.Main, HarnessLayout.SideTasksDirectoryName);
        Directory.Delete(linked, recursive: true);
        TestLinks.DirectoryLink(linked, elsewhere);
        var isolation = new AgentIsolation(new FixedIsolationPlatform(IsolationAvailability.Of(new PassThroughMechanism())), kit.Harness.GitClient, kit.Harness.FileSystem, new HomedPlatform(home.Path), _ => null);

        using var isolated = await isolation.ForLaunchAsync(config, SideTaskPolicy.Ground(layout, config, []), kit.Main, SideTaskLayout.Of(layout, "one").Directory, Token);

        Assert.Equal(
            (true, $"'{linked}' is a link inside a place the agent writes, on the way to '{linked}', which a command could replace with one leading elsewhere", $"replace the link '{linked}' with the directory or file it leads to"),
            (isolated.Refused, isolated.Record.None, isolated.Remedy));
        Assert.Empty(Directory.GetDirectories(Path.Combine(IsolationRoot(home.Path), IsolationCaches.TempDirectory)));
    }

    /// <summary>
    /// A guarded place on a file system that finds a name in any case keeps the launch from any isolation where the
    /// mechanism holds the one spelling a rule is made at, saying where and the remedy; one that holds every spelling is
    /// given its profile.
    /// </summary>
    [Fact]
    public async Task AGuardedPlaceOnAFileSystemThatIgnoresCase_LeavesTheLaunchWithNoIsolation_UnlessTheMechanismHoldsEveryCase()
    {
        using var temp = new TempDirectory();
        using var home = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var layout = new HarnessLayout(kit.Main, kit.Main);
        var config = new HarnessConfig { Worktrees = OrchestrationKit.Settings };
        var folding = new FoldingFileSystem(kit.Harness.FileSystem);

        AgentIsolation IsolationFor(bool holdsEveryCase)
            => new(new FixedIsolationPlatform(IsolationAvailability.Of(new PassThroughMechanism("test", "1.0", holdsEveryCase))), kit.Harness.GitClient, folding, new HomedPlatform(home.Path), _ => null);

        using var one = await IsolationFor(holdsEveryCase: false).ForLaunchAsync(config, SideTaskPolicy.Ground(layout, config, []), kit.Main, SideTaskLayout.Of(layout, "one").Directory, Token);
        using var every = await IsolationFor(holdsEveryCase: true).ForLaunchAsync(config, SideTaskPolicy.Ground(layout, config, []), kit.Main, SideTaskLayout.Of(layout, "one").Directory, Token);

        Assert.True(one.Refused);
        Assert.Matches("^'.+' stands on a file system that ignores letter case, where test's rules hold the one spelling they are made at$", one.Record.None);
        Assert.Equal("keep the trees and every guarded place on a file system that tells letter case apart - WSL's own, not a Windows drive under /mnt", one.Remedy);
        Assert.NotNull(every.Commands);
    }

    /// <summary>
    /// git's answer of where a tree keeps its files is read only where every line is a whole path - git before 2.31
    /// answers its own option back as a line - and the configuration files git reads are the origins of its list, whole.
    /// </summary>
    [Fact]
    public void GitsAnswers_AreReadWholeOnly_AndItsConfigurationFilesByTheirOrigins()
    {
        var tree = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "tree"));
        var whole = Path.Combine(tree, ".git");

        Assert.Equal("--path-format=absolute", AgentIsolation.Unwhole(["--path-format=absolute", whole, whole]));
        Assert.Null(AgentIsolation.Unwhole([whole, $" {whole} "]));
        Assert.Equal(
            [Path.Combine(tree, ".git", "config"), Path.Combine(tree, "shared.gitconfig")],
            AgentIsolation.ConfigurationFiles($"file:.git/config\0core.bare\nfalse\0command line:\0a.b\nc\0file:{Path.Combine(tree, "shared.gitconfig")}\0x.y\nz\0file:.git/config\0core.x\ny\0", tree));
    }

    /// <summary>An agent that may run no command - the run's reporter - needs no isolation: none is probed, and its record says why.</summary>
    [Fact]
    public async Task AnAgentThatMayRunNoCommand_NeedsNone_AndNoProbeIsMade()
    {
        using var temp = new TempDirectory();
        var platform = new FixedIsolationPlatform(IsolationAvailability.None("never asked", "nothing"));
        var isolation = new AgentIsolation(platform, new HarnessFactory().GitClient, new HarnessFactory().FileSystem, new HomedPlatform(temp.Path), _ => null);
        var ground = new AgentPolicyGround(temp.Path, [temp.Path], [temp.Path], [], [], [], AgentPolicyGround.EveryCapability, null, null, ReporterRoot: temp.Path, WorktreeRules: true, ProtectedPaths: true, []);

        var isolated = await isolation.ForLaunchAsync(new HarnessConfig(), ground, temp.Path, temp.Path, Token);

        Assert.Equal((false, null, null), (isolated.Refused, isolated.Notice, isolated.ForCommands));
        Assert.Equal(new AgentIsolationRecord(AgentIsolationMode.Required) { None = LaunchIsolation.NoCommands }, isolated.Record);
        Assert.Empty(platform.Probed);
    }

    /// <summary>
    /// With no mechanism on the machine, required refuses the launch - why, then the remedy - and so does any mode but
    /// preferred, the one word that lets it start; preferred runs it on the words policy alone, its notice saying the
    /// same, its record why there was none.
    /// </summary>
    [Fact]
    public async Task WithNoMechanism_AnyModeButPreferredRefuses_PreferredSaysItAtTheLaunchAndInTheRecord()
    {
        using var temp = new TempDirectory();
        var isolation = new AgentIsolation(new FixedIsolationPlatform(IsolationAvailability.None("bubblewrap is not here", "install it")), new HarnessFactory().GitClient, new HarnessFactory().FileSystem, new HomedPlatform(temp.Path), _ => null);
        var ground = new AgentPolicyGround(temp.Path, [temp.Path], [temp.Path], [], [], [], AgentPolicyGround.EveryCapability, null, null, null, WorktreeRules: true, ProtectedPaths: true, []);

        var required = await isolation.ForLaunchAsync(new HarnessConfig(), ground, temp.Path, temp.Path, Token);
        var preferred = await isolation.ForLaunchAsync(new HarnessConfig { AgentPolicy = new AgentPolicyConfig { Isolation = AgentIsolationMode.Preferred } }, ground, temp.Path, temp.Path, Token);

        Assert.Equal(
            (true, "'think' is refused: agentPolicy.isolation is \"required\" and bubblewrap is not here, so no agent that may run a command starts here; install it", (string?)null),
            (required.Refused, required.Refusal("'think'"), required.Notice));
        Assert.Equal(
            (false, (string?)null, "agentPolicy.isolation is \"preferred\" and bubblewrap is not here, so the agent's commands run on the words policy alone, with no isolation of the operating system's; install it"),
            (preferred.Refused, preferred.Refusal("'think'"), preferred.Notice));
        Assert.Equal(new AgentIsolationRecord(AgentIsolationMode.Preferred) { None = "bubblewrap is not here" }, preferred.Record);
        Assert.Null(preferred.ForCommands);
        Assert.True(new LaunchIsolation("Preferred", null, null, "none here", "install one").Refused);
    }

    /// <summary>The mode is required where nothing is said, and any word but the two it takes is a problem of the configuration.</summary>
    [Fact]
    public void TheMode_IsRequiredWhereNothingIsSaid_AndAnotherWordIsAProblem()
    {
        Assert.Equal(AgentIsolationMode.Required, AgentIsolationMode.Of(new HarnessConfig()));
        Assert.Equal(AgentIsolationMode.Preferred, AgentIsolationMode.Of(new HarnessConfig { AgentPolicy = new AgentPolicyConfig { Isolation = "preferred" } }));
        Assert.Null(AgentIsolationMode.Problem(new AgentPolicyConfig { Isolation = "required" }));
        Assert.Equal("agentPolicy.isolation 'Required' is none of: required, preferred", AgentIsolationMode.Problem(new AgentPolicyConfig { Isolation = "Required" }));
        Assert.Equal(
            ["agentPolicy.isolation 'off' is none of: required, preferred"],
            AgentPolicy.RuleProblems(new HarnessConfig { AgentPolicy = new AgentPolicyConfig { Isolation = "off" } }));
    }

    /// <summary>
    /// A child given an isolation is started by the mechanism's command line in its place - here one that adds a word -
    /// contained or not: one that is not starts through the step all the same, which becomes the mechanism.
    /// </summary>
    [Fact]
    public async Task AnIsolatedChild_IsStartedByTheMechanismsCommandLine_ContainedOrNot()
    {
        var runner = new ProcessRunner(new HostPlatform(), FilePermissionsFactory.Create(), CliRunner.CliProgram);
        var mechanism = new AddingMechanism();
        var request = TestHost.ChildRequest("echo-args", "a") with { Contained = true, Isolation = new ProcessIsolation(mechanism, IsolationProfile.Nothing) };

        var contained = await runner.RunAsync(request, Token);
        var notContained = await runner.RunAsync(request with { Contained = false }, Token);

        Assert.Equal((0, "[a]\n[inside]\n"), (contained.ExitCode, contained.StandardOutput.ReplaceLineEndings("\n")));
        Assert.Equal((0, "[a]\n[inside]\n"), (notContained.ExitCode, notContained.StandardOutput.ReplaceLineEndings("\n")));
    }

    /// <summary>
    /// A child of a mechanism that asks Landlock's scope is placed by a contained step told to enter it before it becomes
    /// the mechanism: on a Linux kernel without the scope the step refuses, saying it could not enter it, so no child runs
    /// with abstract sockets and signals open where the mechanism said they were scoped.
    /// </summary>
    [Fact]
    public async Task AScopedMechanismsChild_IsPlacedByAStepThatEntersLandlocksScope()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Landlock is Linux's.");

        var runner = new ProcessRunner(new HostPlatform(), FilePermissionsFactory.Create(), CliRunner.CliProgram);
        var request = TestHost.ChildRequest("echo-args", "a") with { Contained = true, Isolation = new ProcessIsolation(new AddingMechanism { Scoped = true }, IsolationProfile.Nothing) };

        if (Landlock.Version() >= Landlock.ScopedVersion)
        {
            Assert.Equal(0, (await runner.RunAsync(request, Token)).ExitCode);
            return;
        }

        var unscoped = await Assert.ThrowsAsync<ProgramStartException>(() => runner.RunAsync(request, Token));

        Assert.Contains("enter Landlock's scope of abstract sockets and signals", unscoped.Message, StringComparison.Ordinal);
    }

    /// <summary>The isolation's own directory under a test's home, as this machine places it.</summary>
    private static string IsolationRoot(string home) => IsolationCaches.Root(home, new HostPlatform().Current, _ => null);

    /// <summary>A mechanism that hands the program its own words and one more.</summary>
    private sealed class AddingMechanism : IIsolationMechanism
    {
        public string Name => "adding";

        public string Version => "1";

        public bool HoldsEveryCase => true;

        public bool Scoped { get; init; }

        public string? Leaves => null;

        public string? AnchorCost => null;

        public bool OwnTemp => false;

        public bool TakesHandedCommands => true;

        public (string Program, IReadOnlyList<string> Arguments) Command(IsolationProfile profile, string program, IReadOnlyList<string> arguments)
            => (program, [.. arguments, "inside"]);
    }

    /// <summary>The machine's file system, keeping every path it was asked to open for reading.</summary>
    private sealed class OpeningFileSystem(IFileSystem inner) : PassThroughFileSystem(inner)
    {
        /// <summary>Every path opened for reading, in order.</summary>
        public List<string> Opened { get; } = [];

        public override Stream OpenRead(string path)
        {
            Opened.Add(path);
            return base.OpenRead(path);
        }
    }

    /// <summary>A file system that finds every name in any case, as a Windows drive under WSL does.</summary>
    private sealed class FoldingFileSystem(IFileSystem inner) : PassThroughFileSystem(inner)
    {
        public override bool DirectoryExists(string path) => base.DirectoryExists(path) || base.DirectoryExists(Turned(path));

        public override bool FileExists(string path) => base.FileExists(path) || base.FileExists(Turned(path));

        /// <summary>The path with every letter of its last name's case turned.</summary>
        private static string Turned(string path)
            => Path.Combine(Path.GetDirectoryName(path) ?? string.Empty, new string([.. Path.GetFileName(path).Select(letter => char.IsUpper(letter) ? char.ToLowerInvariant(letter) : char.ToUpperInvariant(letter))]));
    }
}
