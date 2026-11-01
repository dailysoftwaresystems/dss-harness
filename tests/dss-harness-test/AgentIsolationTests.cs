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
    /// work directory, the git directories of the worktree it writes - its own and the common one - and the isolation's
    /// tool caches, with its own temporary directory; hidden every credential slot there is and the Claude Code
    /// directory and its .claude.json, the shell snapshots read again; written by nobody, inside what it writes, the
    /// worktree's .git pointer and configuration directory, the tool's state in the worktree, git's code-running files and
    /// pointers of the common directory and every linked worktree. A place not there inside what it writes is made first;
    /// the launch's own directory, which no command writes, is not. Its records say the mechanism, its pins hold the
    /// linked worktree's git directories, and its disposal takes the temporary directory away.
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
            new(git, Directory: true, Writable: true),
            new(caches, Directory: true, Writable: true),
            new(Path.Combine(worktree, ".git"), Directory: false, Writable: false),
            new(settings, Directory: true, Writable: false),
            .. ground.StateDirectories
                .Where(state => IsolationProfile.Covers(worktree, state) && !IsolationProfile.Covers(settings, state) && Directory.Exists(state))
                .Select(state => new IsolatedWrite(state, Directory: true, Writable: false)),
            new(Path.Combine(git, "hooks"), Directory: true, Writable: false),
            new(Path.Combine(git, "config"), Directory: false, Writable: false),
            new(Path.Combine(git, "config.worktree"), Directory: false, Writable: false),
            new(Path.Combine(git, "info"), Directory: true, Writable: false),
            new(Path.Combine(linked, "config.worktree"), Directory: false, Writable: false),
            new(Path.Combine(linked, "commondir"), Directory: false, Writable: false),
            new(Path.Combine(linked, "gitdir"), Directory: false, Writable: false),
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
        Assert.True(File.Exists(Path.Combine(git, "config.worktree")) && Directory.Exists(commands.Temp) && Directory.Exists(caches));
        Assert.False(Directory.Exists(launch), launch);
        Assert.Equal((new GitDirectoryPin(worktree, linked, git), (GitDirectoryPin?)null), (pins.Of(worktree), pins.Of(kit.Main)));
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
    /// A side-task works over the main checkout, which it writes with its git directory: inside it, its git's code-running
    /// files, its configuration directory and the tool's state are written by nobody, its slots hidden. Its records'
    /// directory, beneath the sealed side-tasks directory, is never made - so a claim of its name finds none there - and a
    /// state file that is not there is never made, for the tool reads it as meaning something.
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

        using var isolated = await isolation.ForLaunchAsync(config, ground, kit.Main, records, Token);

        var writes = isolated.Commands!.Writes;

        Assert.Equal(new IsolatedWrite(kit.Main, Directory: true, Writable: true), writes[0]);
        Assert.Contains(new IsolatedWrite(Path.Combine(kit.Main, ".git"), Directory: true, Writable: true), writes);
        Assert.Contains(new IsolatedWrite(Path.Combine(kit.Main, ".git", "hooks"), Directory: true, Writable: false), writes);
        Assert.Contains(new IsolatedWrite(Path.Combine(kit.Main, HarnessLayout.DirectoryName), Directory: true, Writable: false), writes);
        Assert.Contains(new IsolatedWrite(Path.Combine(kit.Main, HarnessLayout.SideTasksDirectoryName), Directory: true, Writable: false), writes);
        Assert.DoesNotContain(writes, write => write.Path == records || Path.GetFileName(write.Path) == HarnessLayout.SyncedCopyMarkerName);
        Assert.False(Directory.Exists(records), records);
        Assert.False(File.Exists(Path.Combine(kit.Main, HarnessLayout.DirectoryName, HarnessLayout.SyncedCopyMarkerName)));
        Assert.Equal(AgentIsolationMode.Preferred, isolated.Record.Mode);
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
