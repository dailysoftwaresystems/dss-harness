using DssHarness.Core.Agents;
using DssHarness.Core.Agents.Isolation;
using DssHarness.Core.Agents.Policy;
using DssHarness.Core.Configuration;
using DssHarness.Core.Orchestration.Flow;
using DssHarness.Core.Platform;
using DssHarness.Core.Processes;
using DssHarness.Core.Repository;

namespace DssHarness.Tests;

/// <summary>
/// The system's bubblewrap, run for real on Linux: the probe proves it once for the process, and the profile a step's
/// launch is given over a real repository keeps a command inside from a credential slot's file, git's hooks, the tool's
/// state and the launch's own directory, while it writes its worktree and git stages there. Skipped where this machine's
/// bubblewrap does not prove itself, saying why and the remedy - and failed instead where the machine says it holds every
/// build tool, as the continuous integration runners do.
/// </summary>
public sealed class BubblewrapTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>The probe proves the system's bubblewrap - its own name, a version that takes every word the tool gives it - and answers every later asker from the one probe.</summary>
    [Fact]
    public async Task TheProbe_ProvesTheSystemsBubblewrap_OnceForTheProcess()
    {
        using var temp = new TempDirectory();
        var platform = Platform();

        var proved = await ProvedAsync(platform, temp.Path);
        var again = await platform.ProbeAsync(temp.Path, Token);

        var bubblewrap = Assert.IsType<Bubblewrap>(proved);

        Assert.Equal(Bubblewrap.MechanismName, bubblewrap.Name);
        Assert.True(Version.Parse(bubblewrap.Version) >= Bubblewrap.Oldest, bubblewrap.Version);
        Assert.Same(proved, again.Mechanism);
    }

    /// <summary>
    /// A command inside a step's launch reads no file of a credential slot - the slot is an empty place there - and writes
    /// neither git's hooks, the tool's state nor the launch's own directory, the system saying each is read-only; it
    /// writes its worktree, and git stages that file into the repository's index from inside.
    /// </summary>
    [Fact]
    public async Task ACommandInside_ReadsNoSlot_WritesNoHookStateOrLaunch_ButItsWorktree_WhereGitStages()
    {
        using var temp = new TempDirectory();
        using var home = new TempDirectory();
        var platform = Platform();
        await ProvedAsync(platform, temp.Path);
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("a1");
        var slot = HarnessLayout.SecretSlotDirectoriesUnder(kit.Main)[0];
        Directory.CreateDirectory(slot);
        var canary = Path.Combine(slot, "canary");
        await File.WriteAllTextAsync(canary, "secret", Token);
        var launch = Path.Combine(kit.Layout.Directory, "steps", "think");
        var config = new HarnessConfig { Worktrees = OrchestrationKit.Settings };
        var ground = AgentStepGround.Of(
            config,
            definition: null,
            new WorkflowStep { Name = "think", Type = "reason" },
            new AgentWorkspace(worktree, kit.Layout.WorkDirectory("a1"), kit.Main, [], worktree, kit.Layout.Directory),
            webSearchGiven: false,
            []);
        var isolated = await new AgentIsolation(platform, kit.Harness.GitClient, kit.Harness.FileSystem, new HomedPlatform(home.Path))
            .ForLaunchAsync(config, ground, kit.Main, launch, Token);
        var inside = Assert.IsType<ProcessIsolation>(isolated.ForCommands);
        var hook = Path.Combine(kit.Main, ".git", "hooks", "pre-commit");
        var state = Path.Combine(kit.Main, HarnessLayout.RunsDirectoryRelative.Replace('/', Path.DirectorySeparatorChar), "planted");
        var launched = Path.Combine(launch, "planted");
        var made = Path.Combine(worktree, "made.txt");

        var read = await InsideAsync(inside, worktree, "/bin/sh", "-c", "cat \"$1\"", "sh", canary);
        var wrote = new[] { hook, state, launched }.Select(place => (place, InsideAsync(inside, worktree, "/bin/sh", "-c", ": > \"$1\"", "sh", place))).ToList();
        var worked = await InsideAsync(inside, worktree, "/bin/sh", "-c", "printf made > \"$1\"", "sh", made);
        var staged = await InsideAsync(inside, worktree, "git", "add", "made.txt");

        Assert.Equal((1, string.Empty), (read.ExitCode, read.StandardOutput));
        Assert.Contains("No such file or directory", read.StandardError, StringComparison.Ordinal);

        foreach (var (place, writing) in wrote)
        {
            var result = await writing;

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("Read-only file system", result.StandardError, StringComparison.Ordinal);
            Assert.False(File.Exists(place), place);
        }

        Assert.Equal((0, "made"), (worked.ExitCode, await File.ReadAllTextAsync(made, Token)));
        Assert.Equal(0, staged.ExitCode);
        Assert.Equal(["made.txt"], (await kit.Harness.GitClient.RunAsync(worktree, ["diff", "--cached", "--name-only"], cancellationToken: Token)).OutputLines);
    }

    /// <summary>This machine's isolation, its probe made through a real process runner as every isolated child is started.</summary>
    private static IsolationPlatform Platform()
        => new(new HostPlatform(), Runner());

    /// <summary>A real process runner, its contained step this build's own.</summary>
    private static ProcessRunner Runner()
        => new(new HostPlatform(), FilePermissionsFactory.Create(), CliRunner.CliProgram);

    /// <summary>
    /// The mechanism the probe proved - the test skipped where this is not Linux, or where no bubblewrap proves itself here,
    /// saying why and the remedy, and failed instead where the machine says it holds every build tool.
    /// </summary>
    private static async Task<IIsolationMechanism> ProvedAsync(IsolationPlatform platform, string tree)
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Bubblewrap isolates an agent's commands on Linux alone.");

        var found = await platform.ProbeAsync(tree, Token);

        if (found.Mechanism is null)
        {
            BuildTools.Lacks($"This machine has no bubblewrap that proves itself: {found.Unavailable}; {found.Remedy}.");
        }

        return found.Mechanism;
    }

    /// <summary>Runs <paramref name="program"/> contained and inside <paramref name="isolation"/>, in <paramref name="directory"/>.</summary>
    private static Task<ProcessResult> InsideAsync(ProcessIsolation isolation, string directory, string program, params string[] arguments)
        => Runner().RunAsync(
            new ProcessRequest
            {
                FileName = program,
                Arguments = arguments,
                WorkingDirectory = directory,
                Timeout = TimeSpan.FromSeconds(60),
                Contained = true,
                Isolation = isolation,
            },
            Token);
}
