using DssHarness.Core.Processes;
using DssHarness.Core.Repository;

namespace DssHarness.Tests;

/// <summary>
/// The system's bubblewrap, run for real on Linux: the probe proves it once for the process, and the profile a step's
/// launch is given over a real repository keeps a command inside from a credential slot's file, git's hooks, the tool's
/// state and the launch's own directory, and from moving any directory above them, while it writes its worktree and git
/// stages there. Skipped where this machine's bubblewrap does not prove itself, saying why and the remedy - and failed
/// instead where the machine says it holds every build tool, as the continuous integration runners do.
/// </summary>
public sealed class BubblewrapTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>The probe proves the system's bubblewrap - its own name, a version that takes every word the tool gives it - and answers every later asker from the one probe.</summary>
    [Fact]
    public async Task TheProbe_ProvesTheSystemsBubblewrap_OnceForTheProcess()
    {
        using var temp = new TempDirectory();
        var platform = RealIsolation.Platform();

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
        var platform = RealIsolation.Platform();
        await ProvedAsync(platform, temp.Path);
        var launch = await RealIsolation.StepLaunchAsync(temp, home, platform);
        var hook = Path.Combine(launch.Kit.Main, ".git", "hooks", "pre-commit");
        var state = Path.Combine(launch.Kit.Main, HarnessLayout.RunsDirectoryRelative.Replace('/', Path.DirectorySeparatorChar), "planted");
        var launched = Path.Combine(launch.Directory, "planted");
        var made = Path.Combine(launch.Worktree, "made.txt");

        var read = await RealIsolation.InsideAsync(launch.Commands, launch.Worktree, "/bin/sh", "-c", "cat \"$1\"", "sh", launch.Canary);
        var wrote = new[] { hook, state, launched }.Select(place => (place, RealIsolation.InsideAsync(launch.Commands, launch.Worktree, "/bin/sh", "-c", ": > \"$1\"", "sh", place))).ToList();
        var worked = await RealIsolation.InsideAsync(launch.Commands, launch.Worktree, "/bin/sh", "-c", "printf made > \"$1\"", "sh", made);
        var staged = await RealIsolation.InsideAsync(launch.Commands, launch.Worktree, "git", "add", "made.txt");

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
        Assert.Equal(["made.txt"], (await launch.Kit.Harness.GitClient.RunAsync(launch.Worktree, ["diff", "--cached", "--name-only"], cancellationToken: Token)).OutputLines);
    }

    /// <summary>
    /// A command inside moves no directory above a guarded place - neither the repository's git directory, whose hooks it
    /// may not write, nor the directory that holds the credential slots - for a place moved away would take its rule with
    /// it and leave its name free to make again: each is a mount point inside, which the system keeps from being renamed.
    /// </summary>
    [Fact]
    public async Task ACommandInside_MovesNoDirectoryAboveAGuardedPlace()
    {
        using var temp = new TempDirectory();
        using var home = new TempDirectory();
        var platform = RealIsolation.Platform();
        await ProvedAsync(platform, temp.Path);
        var launch = await RealIsolation.StepLaunchAsync(temp, home, platform);
        var git = Path.Combine(launch.Kit.Main, ".git");
        var config = Path.Combine(launch.Kit.Main, HarnessLayout.DirectoryName);

        foreach (var anchor in new[] { git, config })
        {
            var moved = await RealIsolation.InsideAsync(launch.Commands, launch.Worktree, "/bin/sh", "-c", "mv \"$1\" \"$1-moved\"", "sh", anchor);

            Assert.NotEqual(0, moved.ExitCode);
            Assert.Contains("Device or resource busy", moved.StandardError, StringComparison.Ordinal);
            Assert.True(Directory.Exists(anchor), anchor);
            Assert.False(Directory.Exists(anchor + "-moved"), anchor);
        }
    }

    /// <summary>
    /// A run's commit runs the repository's hooks inside the isolation of the agents' work: the hook runs and reads no file
    /// of a credential slot - the slot is an empty place there - and git commits from inside, writing its index, its
    /// objects and its refs.
    /// </summary>
    [Fact]
    public async Task ACommitsHooks_RunInside_ReadingNoSlot_AndGitCommitsFromThere()
    {
        using var temp = new TempDirectory();
        using var home = new TempDirectory();
        var platform = RealIsolation.Platform();
        await ProvedAsync(platform, temp.Path);
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var slot = HarnessLayout.SecretSlotDirectoriesUnder(kit.Main)[0];
        Directory.CreateDirectory(slot);
        var canary = Path.Combine(slot, "canary");
        await File.WriteAllTextAsync(canary, "secret", Token);
        var config = new Core.Configuration.HarnessConfig { Worktrees = OrchestrationKit.Settings };
        var isolated = await new Core.Agents.Isolation.AgentIsolation(platform, kit.Harness.GitClient, kit.Harness.FileSystem, new HomedPlatform(home.Path))
            .ForExecutionAsync(config, Core.Agents.Isolation.ExecutionGrounds.OfHooks(config, kit.Main, kit.Main), kit.Main, Token);
        var hook = Path.Combine(kit.Main, ".git", "hooks", "pre-commit");
        await File.WriteAllTextAsync(hook, $"#!/bin/sh\ncat '{canary}' > hook-read.txt 2>&1\nexit 0\n", Token);

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        await File.WriteAllTextAsync(Path.Combine(kit.Main, "made.txt"), "made", Token);
        await kit.Harness.RunGitAsync(kit.Main, ["add", "made.txt"], Token);
        var before = await kit.Harness.GitClient.ResolveCommitAsync(kit.Main, "HEAD", Token);

        var committed = await kit.Harness.GitClient.RunWithHooksAsync(kit.Main, ["commit", "--quiet", "-m", "inside"], TimeSpan.FromMinutes(1), isolated.ForProcess, null, Token);

        Assert.True(committed.Succeeded, committed.FailureMessage);
        Assert.NotEqual(before, await kit.Harness.GitClient.ResolveCommitAsync(kit.Main, "HEAD", Token));
        Assert.Contains("No such file or directory", await File.ReadAllTextAsync(Path.Combine(kit.Main, "hook-read.txt"), Token), StringComparison.Ordinal);
        Assert.Equal("secret", await File.ReadAllTextAsync(canary, Token));
    }

    /// <summary>The bubblewrap the probe proved: the test skipped where this is not Linux.</summary>
    private static Task<IIsolationMechanism> ProvedAsync(Core.Platform.IsolationPlatform platform, string tree)
        => RealIsolation.ProvedAsync(platform, tree, OperatingSystem.IsLinux(), "Bubblewrap isolates an agent's commands on Linux alone.", "bubblewrap");
}
