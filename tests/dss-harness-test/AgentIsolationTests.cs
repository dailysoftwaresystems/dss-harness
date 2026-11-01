using DssHarness.Core.Agents;
using DssHarness.Core.Agents.Isolation;
using DssHarness.Core.Agents.Policy;
using DssHarness.Core.Agents.SideTasks;
using DssHarness.Core.Configuration;
using DssHarness.Core.Orchestration.Flow;
using DssHarness.Core.Platform;
using DssHarness.Core.Processes;
using DssHarness.Core.Repository;

namespace DssHarness.Tests;

/// <summary>
/// The operating system's isolation of agents' commands: the mechanism's command line, the profile's order, the profile
/// each ground's launch is given - its places exact - the places made before a launch, and the mode's refusal and notice.
/// </summary>
public sealed class AgentIsolationTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// Bubblewrap binds the machine whole, then lays each write rule in the profile's order, then hides each hidden place
    /// and the machine's own escapes - a directory under an empty read-only tmpfs, a file under /dev/null - takes WSL's
    /// interoperation variable away, and hands the program its own words after '--'.
    /// </summary>
    [Fact]
    public void Bubblewrap_BindsTheMachine_LaysEachRuleInOrder_AndHidesLast()
    {
        var mechanism = new Bubblewrap("/usr/bin/bwrap", "0.9.0", [new IsolatedPath("/run/WSL", Directory: true), new IsolatedPath("/run/user/1000/bus", Directory: false)]);
        var profile = new IsolationProfile(
            [new IsolatedPath("/r/.harness-config/sshItems", Directory: true), new IsolatedPath("/r/.harness-config/runner/.env", Directory: false)],
            [new IsolatedWrite("/r/.worktrees", Directory: true, Writable: false), new IsolatedWrite("/r/.worktrees/a1", Directory: true, Writable: true), new IsolatedWrite("/r/.git/config", Directory: false, Writable: false)]);

        var (program, arguments) = mechanism.Command(profile, "/usr/bin/git", ["add", "a.txt"]);

        Assert.Equal("/usr/bin/bwrap", program);
        Assert.Equal(
            [
                "--unshare-user", "--disable-userns", "--unshare-pid", "--die-with-parent",
                "--bind", "/", "/",
                "--dev-bind", "/dev", "/dev",
                "--proc", "/proc",
                "--ro-bind", "/r/.worktrees", "/r/.worktrees",
                "--bind", "/r/.worktrees/a1", "/r/.worktrees/a1",
                "--ro-bind", "/r/.git/config", "/r/.git/config",
                "--tmpfs", "/r/.harness-config/sshItems", "--remount-ro", "/r/.harness-config/sshItems",
                "--ro-bind", "/dev/null", "/r/.harness-config/runner/.env",
                "--tmpfs", "/run/WSL", "--remount-ro", "/run/WSL",
                "--ro-bind", "/dev/null", "/run/user/1000/bus",
                "--unsetenv", "WSL_INTEROP",
                "--", "/usr/bin/git", "add", "a.txt",
            ],
            arguments);
        Assert.Equal(("bubblewrap", "0.9.0"), (mechanism.Name, mechanism.Version));
    }

    /// <summary>
    /// The profile lays every place above another before it, whatever list it came from, so a writable root inside a
    /// sealed place is written again and a sealed place inside a writable root stays sealed; a place both sealed and
    /// writable stays sealed; and the Claude Code directories are hidden from a command and open to a whole Claude Code.
    /// </summary>
    [Fact]
    public void TheProfile_LaysEachPlaceAboveAnotherFirst_ASealedPlaceStayingSealed()
    {
        IReadOnlyList<IsolatedPath> hidden = [new IsolatedPath("/r/.harness-config/sshItems", Directory: true)];
        IReadOnlyList<IsolatedPath> claude = [new IsolatedPath("/home/me/.claude", Directory: true)];
        IReadOnlyList<IsolatedPath> sealedPlaces =
        [
            new IsolatedPath("/r/.worktrees/a1/.harness-config/runs", Directory: true),
            new IsolatedPath("/r/.worktrees", Directory: true),
            new IsolatedPath("/r/.orchestrators/o1/work/a1", Directory: true),
            new IsolatedPath("/r/.git/config", Directory: false),
        ];

        var command = IsolationProfiles.Of(hidden, claude, sealedPlaces, ["/r/.worktrees/a1", "/r/.orchestrators/o1/work/a1"], hideClaudeCodeDirectories: true, StringComparison.Ordinal);
        var whole = IsolationProfiles.Of(hidden, claude, sealedPlaces, ["/r/.worktrees/a1"], hideClaudeCodeDirectories: false, StringComparison.Ordinal);

        Assert.Equal([new IsolatedPath("/r/.harness-config/sshItems", Directory: true), new IsolatedPath("/home/me/.claude", Directory: true)], command.Hidden);
        Assert.Equal(
            [
                new IsolatedWrite("/r/.worktrees", Directory: true, Writable: false),
                new IsolatedWrite("/r/.git/config", Directory: false, Writable: false),
                new IsolatedWrite("/r/.worktrees/a1", Directory: true, Writable: true),
                new IsolatedWrite("/r/.worktrees/a1/.harness-config/runs", Directory: true, Writable: false),
                new IsolatedWrite("/r/.orchestrators/o1/work/a1", Directory: true, Writable: false),
            ],
            command.Writes);
        Assert.Equal([new IsolatedPath("/r/.harness-config/sshItems", Directory: true)], whole.Hidden);
    }

    /// <summary>
    /// A step working in its worktree is denied, by its launch's profile: every credential slot of the main checkout and
    /// the Claude Code directories, hidden - those open to a whole Claude Code; git's code-running files and the user's
    /// own git configuration, the tool's state under both trees and the launch's own directory, sealed; its worktree and
    /// work directory written again. A place not there is made first; the probe is asked for the main checkout.
    /// </summary>
    [Fact]
    public async Task AStepInItsWorktree_IsDeniedTheSlotsGitsCodeTheStateAndItsLaunch_ItsRootsWrittenAgain()
    {
        using var temp = new TempDirectory();
        using var home = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("a1");
        var claude = Path.Combine(home.Path, ".claude");
        var work = kit.Layout.WorkDirectory("a1");
        var launch = Path.Combine(kit.Layout.Directory, "steps", "think");
        var platform = new FixedIsolationPlatform(IsolationAvailability.Of(new PassThroughMechanism()));
        var isolation = new AgentIsolation(platform, kit.Harness.GitClient, kit.Harness.FileSystem, new HomedPlatform(home.Path), _ => null);
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
        IReadOnlyList<IsolatedPath> slots = [.. HarnessLayout.SecretSlotDirectoriesUnder(kit.Main).Select(slot => new IsolatedPath(slot, Directory: true))];
        IReadOnlyList<IsolatedWrite> writes =
        [
            new(Path.Combine(git, "hooks"), Directory: true, Writable: false),
            new(Path.Combine(git, "config"), Directory: false, Writable: false),
            new(Path.Combine(git, "config.worktree"), Directory: false, Writable: false),
            new(Path.Combine(git, "info"), Directory: true, Writable: false),
            new(Path.Combine(linked, "config.worktree"), Directory: false, Writable: false),
            new(Path.Combine(home.Path, ".gitconfig"), Directory: false, Writable: false),
            new(Path.Combine(home.Path, ".config", "git"), Directory: true, Writable: false),
            .. ground.StateDirectories
                .Where(state => !HarnessLayout.StateFileNames.Contains(Path.GetFileName(state)))
                .Select(state => new IsolatedWrite(state, Directory: true, Writable: false)),
            .. ground.StateDirectories
                .Where(state => HarnessLayout.StateFileNames.Contains(Path.GetFileName(state)) && File.Exists(state))
                .Select(state => new IsolatedWrite(state, Directory: false, Writable: false)),
            new(launch, Directory: true, Writable: false),
            new(worktree, Directory: true, Writable: true),
            new(work, Directory: true, Writable: true),
        ];

        Assert.Equal((AgentIsolationMode.Required, false, null, null), (isolated.Mode, isolated.Refused, isolated.Notice, isolated.Refusal("'think'")));
        Assert.Equal([kit.Main], platform.Probed);
        Assert.Equal([.. slots, new IsolatedPath(claude, Directory: true)], isolated.Commands!.Hidden);
        Assert.Equal(slots, isolated.ClaudeCode!.Hidden);
        Assert.Equal(writes.OrderBy(write => write.Path, StringComparer.Ordinal), isolated.Commands.Writes.OrderBy(write => write.Path, StringComparer.Ordinal));
        Assert.Equal(isolated.Commands.Writes, isolated.ClaudeCode.Writes);
        Assert.True(Directory.Exists(claude) && File.Exists(Path.Combine(home.Path, ".gitconfig")) && File.Exists(Path.Combine(git, "config.worktree")) && Directory.Exists(launch));
        Assert.Equal(
            new AgentIsolationRecord(AgentIsolationMode.Required) { Mechanism = "test", Version = "1.0" },
            isolated.Record);
    }

    /// <summary>
    /// A side-task works over the main checkout, which it writes: its git's code-running files and the tool's state under
    /// it are sealed inside that root, its slots hidden, its own records' directory sealed. A state file that is not there
    /// is never made - the tool reads it as meaning something - and the words policy alone guards it.
    /// </summary>
    [Fact]
    public async Task ASideTask_WritesTheMainTree_ButItsGitsCodeTheStateAndItsRecords()
    {
        using var temp = new TempDirectory();
        using var home = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var isolation = new AgentIsolation(new FixedIsolationPlatform(IsolationAvailability.Of(new PassThroughMechanism())), kit.Harness.GitClient, kit.Harness.FileSystem, new HomedPlatform(home.Path), _ => null);
        var config = new HarnessConfig { Worktrees = OrchestrationKit.Settings, AgentPolicy = new AgentPolicyConfig { Isolation = AgentIsolationMode.Preferred } };
        var layout = new HarnessLayout(kit.Main, kit.Main);
        var ground = SideTaskPolicy.Ground(layout, config, []);
        var records = SideTaskLayout.Of(layout, "one").Directory;

        var isolated = await isolation.ForLaunchAsync(config, ground, kit.Main, records, Token);

        var writes = isolated.Commands!.Writes;

        Assert.Equal(new IsolatedWrite(kit.Main, Directory: true, Writable: true), writes[0]);
        Assert.Contains(new IsolatedWrite(Path.Combine(kit.Main, ".git", "hooks"), Directory: true, Writable: false), writes);
        Assert.Contains(new IsolatedWrite(records, Directory: true, Writable: false), writes);
        Assert.Contains(new IsolatedWrite(Path.Combine(kit.Main, HarnessLayout.SideTasksDirectoryName), Directory: true, Writable: false), writes);
        Assert.DoesNotContain(writes, write => Path.GetFileName(write.Path) == HarnessLayout.SyncedCopyMarkerName);
        Assert.False(File.Exists(Path.Combine(kit.Main, HarnessLayout.DirectoryName, HarnessLayout.SyncedCopyMarkerName)));
        Assert.Equal(AgentIsolationMode.Preferred, isolated.Record.Mode);
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
    /// With no mechanism on the machine, required refuses the launch - why, then the remedy - and preferred runs it on the
    /// words policy alone, its notice saying the same, its record why there was none.
    /// </summary>
    [Fact]
    public async Task WithNoMechanism_RequiredRefuses_PreferredSaysItAtTheLaunchAndInTheRecord()
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
        Assert.Equal((null, null), (preferred.ForCommands, preferred.ForClaudeCode));
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
    /// A contained child given an isolation is started by the mechanism's command line in its place - here one that adds a
    /// word - and an isolation without containment is refused, for the contained step places it.
    /// </summary>
    [Fact]
    public async Task AnIsolatedChild_IsStartedByTheMechanismsCommandLine_AndOnlyContained()
    {
        var runner = new ProcessRunner(new HostPlatform(), FilePermissionsFactory.Create(), CliRunner.CliProgram);
        var mechanism = new AddingMechanism();
        var request = TestHost.ChildRequest("echo-args", "a") with { Contained = true, Isolation = new ProcessIsolation(mechanism, IsolationProfile.Nothing) };

        var result = await runner.RunAsync(request, Token);

        Assert.Equal((0, "[a]\n[inside]\n"), (result.ExitCode, result.StandardOutput.ReplaceLineEndings("\n")));

        var refused = await Assert.ThrowsAsync<ArgumentException>(() => runner.RunAsync(request with { Contained = false }, Token));

        Assert.Equal("An isolated child is placed by the contained step: give Isolation only with Contained. (Parameter 'request')", refused.Message);
    }

    /// <summary>A mechanism that hands the program its own words and one more.</summary>
    private sealed class AddingMechanism : IIsolationMechanism
    {
        public string Name => "adding";

        public string Version => "1";

        public (string Program, IReadOnlyList<string> Arguments) Command(IsolationProfile profile, string program, IReadOnlyList<string> arguments)
            => (program, [.. arguments, "inside"]);
    }
}
