using DssHarness.Core.Agents.ClaudeCode;
using DssHarness.Core.Processes;
using DssHarness.Core.Repository;

namespace DssHarness.Tests;

/// <summary>
/// The system's bubblewrap: from doubles of the machine on every system, the probe refuses one the user could replace and
/// proves one only the system writes, scoped by Landlock where the kernel has it; then run for real on Linux, the probe
/// proves it once for the process, and the profile a step's launch is given over a real repository keeps a command inside
/// from a credential slot's file, every place the user keeps credentials in but ssh's known_hosts and config, git's hooks,
/// the tool's state, the launch's own directory and every place outside its roots, and from moving any directory above
/// them, while it writes its worktree, its own temporary directory and its caches and git stages there; and a push reaches
/// the user's SSH agent inside ssh's hidden directory, where its key stays unread. The real tests are skipped where this machine's bubblewrap does not prove itself, saying
/// why and the remedy - and failed instead where the machine says it holds every build tool, as the continuous
/// integration runners do.
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
            Assert.True(result.StandardError.Contains("Read-only file system", StringComparison.Ordinal), $"{place}: {result.StandardError}");
            Assert.False(File.Exists(place), place);
        }

        Assert.Equal((0, "made"), (worked.ExitCode, await File.ReadAllTextAsync(made, Token)));
        Assert.Equal(0, staged.ExitCode);
        Assert.Equal(["made.txt"], (await launch.Kit.Harness.GitClient.RunAsync(launch.Worktree, ["diff", "--cached", "--name-only"], cancellationToken: Token)).OutputLines);
    }

    /// <summary>
    /// A Claude Code launch's outer layer holds its process alone: a command run inside it reads no file of a credential
    /// slot and writes neither git's hooks nor anything of the launch's own records, which stay sealed, but Claude Code's
    /// own directory and its own temporary directory beside its commands' in the run's temp; no layer is started inside
    /// it, for every layer allows no further namespace.
    /// </summary>
    [Fact]
    public async Task AClaudeCodeLaunchsOuterLayer_HoldsItsProcessAlone_WithItsOwnTemporaryDirectory()
    {
        using var temp = new TempDirectory();
        using var home = new TempDirectory();
        var platform = RealIsolation.Platform();
        await ProvedAsync(platform, temp.Path);
        var launch = await RealIsolation.StepLaunchAsync(temp, home, platform);
        var outer = Assert.IsType<ProcessIsolation>(launch.ClaudeCode);
        var claudeTemp = outer.Profile.Temp!;
        var hook = Path.Combine(launch.Kit.Main, ".git", "hooks", "pre-commit");
        var settings = Path.Combine(launch.Directory, ClaudeLaunch.SettingsFileName);
        var written = Path.Combine(launch.Claude, "written");
        var planted = Path.Combine(launch.Directory, "planted");
        var inTemp = Path.Combine(claudeTemp, "w");

        // Run as Claude Code's own process runs: in the outer layer itself.
        var read = await RealIsolation.InsideAsync(outer, launch.Worktree, "/bin/sh", "-c", "cat \"$1\"", "sh", launch.Canary);
        var sealedWrites = new[] { hook, settings, planted }.Select(place => (place, RealIsolation.InsideAsync(outer, launch.Worktree, "/bin/sh", "-c", ": > \"$1\"", "sh", place))).ToList();
        var own = await RealIsolation.InsideAsync(outer, launch.Worktree, "/bin/sh", "-c", ": > \"$1\" && : > \"$2\"", "sh", written, inTemp);

        Assert.Equal((1, string.Empty), (read.ExitCode, read.StandardOutput));
        Assert.Contains("No such file or directory", read.StandardError, StringComparison.Ordinal);

        foreach (var (place, writing) in sealedWrites)
        {
            var result = await writing;

            Assert.NotEqual(0, result.ExitCode);
            Assert.True(result.StandardError.Contains("Read-only file system", StringComparison.Ordinal), $"{place}: {result.StandardError}");
            Assert.False(File.Exists(place), place);
        }

        Assert.Equal((0, true, true), (own.ExitCode, File.Exists(written), File.Exists(inTemp)));
    }

    /// <summary>
    /// A command inside moves no directory above a guarded place, for a place moved away would take its rule with it and
    /// leave its name free to make again: the repository's git directory and the directory that holds the credential slots
    /// stand in the main checkout, which the work never writes, and the repository of the agent's own - its .git written
    /// whole, its code-running files sealed inside it - is a mount point of its own - the read-only machine and the mount
    /// each keep them where they are.
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
        var own = Path.Combine(launch.Worktree, ".git");

        foreach (var (anchor, said) in new[] { (git, "Read-only file system"), (config, "Read-only file system"), (own, "Device or resource busy") })
        {
            var moved = await RealIsolation.InsideAsync(launch.Commands, launch.Worktree, "/bin/sh", "-c", "mv \"$1\" \"$1-moved\"", "sh", anchor);

            Assert.NotEqual(0, moved.ExitCode);
            Assert.True(moved.StandardError.Contains(said, StringComparison.Ordinal), $"{anchor}: {moved.StandardError}");
            Assert.True(Directory.Exists(anchor), anchor);
            Assert.False(Directory.Exists(anchor + "-moved"), anchor);
        }
    }

    /// <summary>
    /// A command inside a step's launch over an agent's repository writes that repository's own git and nothing of the main
    /// checkout's: git update-ref makes a ref of its own there and git add stages a file into its index, while writing the
    /// main checkout's own refs, the repository's alternates - which lend it the main checkout's objects - and a
    /// hard link from the main checkout's object store into the repository each fails, the link across the mounts bubblewrap
    /// made (EXDEV) - so nothing the agent runs writes or gains a second name on a file of the main checkout's, whose refs
    /// stand exactly as they stood and whose store fsck finds clean. This is the hard-link canary of Linux's bubblewrap:
    /// where it fails, bubblewrap allows the link, and the profile must deny it.
    /// </summary>
    [Fact]
    public async Task ACommandInsideAnAgentsRepository_WritesItsOwnGitAlone_TheMainCheckoutStayingAsItWas()
    {
        using var temp = new TempDirectory();
        using var home = new TempDirectory();
        var platform = RealIsolation.Platform();
        await ProvedAsync(platform, temp.Path);
        var launch = await RealIsolation.StepLaunchAsync(temp, home, platform);
        var main = launch.Kit.Main;
        var git = Path.Combine(main, ".git");
        var own = Path.Combine(launch.Worktree, ".git");
        var head = (await launch.Kit.Harness.RunGitAsync(main, ["rev-parse", "HEAD"], Token)).StandardOutput.Trim();
        var branch = (await launch.Kit.Harness.RunGitAsync(main, ["symbolic-ref", "--short", "HEAD"], Token)).StandardOutput.Trim();
        var branchFile = Path.Combine(git, "refs", "heads", branch);
        var refs = (await launch.Kit.Harness.GitClient.RunAsync(main, ["for-each-ref"], cancellationToken: Token)).StandardOutput;

        // A packed object of the main checkout's store, for the hard-link canary.
        await launch.Kit.Harness.RunGitAsync(main, ["repack", "--quiet", "-ad"], Token);
        var pack = Assert.Single(Directory.GetFiles(Path.Combine(git, "objects", "pack"), "*.pack"));

        var updated = await RealIsolation.InsideAsync(launch.Commands, launch.Worktree, "git", "update-ref", "refs/heads/made", head);
        var made = Path.Combine(launch.Worktree, "made.txt");
        await File.WriteAllTextAsync(made, "made", Token);
        var staged = await RealIsolation.InsideAsync(launch.Commands, launch.Worktree, "git", "add", "made.txt");
        var mainsRef = await RealIsolation.InsideAsync(launch.Commands, launch.Worktree, "/bin/sh", "-c", ": > \"$1\"", "sh", branchFile);
        var alternates = await RealIsolation.InsideAsync(launch.Commands, launch.Worktree, "/bin/sh", "-c", ": > \"$1\"", "sh", Path.Combine(own, "objects", "info", "alternates"));
        var linked = await RealIsolation.InsideAsync(launch.Commands, launch.Worktree, "/bin/ln", pack, Path.Combine(launch.Worktree, "hard"));

        Assert.Equal((0, 0), (updated.ExitCode, staged.ExitCode));
        Assert.Equal([$"refs/heads/made {head}"], (await launch.Kit.Harness.GitClient.RunAsync(launch.Worktree, ["for-each-ref", "--format=%(refname) %(objectname)"], cancellationToken: Token)).OutputLines);
        Assert.Equal(["made.txt"], (await launch.Kit.Harness.GitClient.RunAsync(launch.Worktree, ["diff", "--cached", "--name-only"], cancellationToken: Token)).OutputLines);
        Assert.NotEqual(0, mainsRef.ExitCode);
        Assert.True(mainsRef.StandardError.Contains("Read-only file system", StringComparison.Ordinal), mainsRef.StandardError);
        Assert.NotEqual(0, alternates.ExitCode);
        Assert.True(alternates.StandardError.Contains("Read-only file system", StringComparison.Ordinal), alternates.StandardError);
        Assert.NotEqual(0, linked.ExitCode);
        Assert.True(linked.StandardError.Contains("Invalid cross-device link", StringComparison.Ordinal), linked.StandardError);
        Assert.False(File.Exists(Path.Combine(launch.Worktree, "hard")));
        Assert.Equal(refs, (await launch.Kit.Harness.GitClient.RunAsync(main, ["for-each-ref"], cancellationToken: Token)).StandardOutput);
        Assert.Equal(0, (await launch.Kit.Harness.GitClient.RunAsync(main, ["fsck", "--no-progress"], cancellationToken: Token)).ExitCode);
    }

    /// <summary>
    /// A command inside writes nowhere on the machine but its roots: a place beside the repository is read-only; the system's
    /// temporary directory is the launch's own, so what it writes there lands in that one and never in the machine's; and
    /// each declared tool cache is named to it inside the isolation's own.
    /// </summary>
    [Fact]
    public async Task ACommandInside_WritesNothingOutsideItsRoots_ItsTempItsOwn_ItsCachesNamed()
    {
        using var temp = new TempDirectory();
        using var home = new TempDirectory();
        var platform = RealIsolation.Platform();
        await ProvedAsync(platform, temp.Path);
        var launch = await RealIsolation.StepLaunchAsync(temp, home, platform);
        var beside = Path.Combine(temp.Path, "planted");
        var name = "dssharness-" + Guid.NewGuid().ToString("N");
        var profile = launch.Commands.Profile;

        var outside = await RealIsolation.InsideAsync(launch.Commands, launch.Worktree, "/bin/sh", "-c", ": > \"$1\"", "sh", beside);
        var temporary = await RealIsolation.InsideAsync(launch.Commands, launch.Worktree, "/bin/sh", "-c", ": > \"/tmp/$1\"", "sh", name);
        var cache = await RealIsolation.InsideAsync(launch.Commands, launch.Worktree, "/bin/sh", "-c", "printf %s \"$npm_config_cache\"");

        Assert.NotEqual(0, outside.ExitCode);
        Assert.Contains("Read-only file system", outside.StandardError, StringComparison.Ordinal);
        Assert.False(File.Exists(beside), beside);
        Assert.Equal(0, temporary.ExitCode);
        Assert.Equal((false, true), (File.Exists(Path.Combine("/tmp", name)), File.Exists(Path.Combine(profile.Temp!, name))));
        Assert.Equal(profile.Environment.Single(variable => variable.Key == "npm_config_cache").Value, cache.StandardOutput);
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
            .ForExecutionAsync(config, Core.Agents.Isolation.ExecutionGrounds.OfHooks(config, kit.Main, kit.Main, sshAgent: false), kit.Main, Token);
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

    /// <summary>
    /// A package an agent's command plants in its caches is not in the caches the repository's hooks over the main
    /// checkout are given - another write scope's, which the agent's command can neither find by its variable nor write.
    /// </summary>
    [Fact]
    public async Task APackageAnAgentPlantsInItsCaches_IsNotInTheCachesOfTheHooksOverTheMainCheckout()
    {
        using var temp = new TempDirectory();
        using var home = new TempDirectory();
        var platform = RealIsolation.Platform();
        await ProvedAsync(platform, temp.Path);
        var launch = await RealIsolation.StepLaunchAsync(temp, home, platform);
        var config = new Core.Configuration.HarnessConfig { Worktrees = OrchestrationKit.Settings };
        using var hooks = await new Core.Agents.Isolation.AgentIsolation(platform, launch.Kit.Harness.GitClient, launch.Kit.Harness.FileSystem, new HomedPlatform(home.Path))
            .ForExecutionAsync(config, Core.Agents.Isolation.ExecutionGrounds.OfHooks(config, launch.Kit.Main, launch.Kit.Main, sshAgent: false), launch.Kit.Main, Token);

        var planted = await RealIsolation.InsideAsync(launch.Commands, launch.Worktree, "/bin/sh", "-c", "mkdir -p \"$NUGET_PACKAGES\" && printf planted > \"$NUGET_PACKAGES/planted.targets\" && printf %s \"$NUGET_PACKAGES\"");
        var found = await RealIsolation.InsideAsync(hooks.ForProcess!, launch.Kit.Main, "/bin/sh", "-c", "test -e \"$NUGET_PACKAGES/planted.targets\"");
        var written = await RealIsolation.InsideAsync(hooks.ForProcess!, launch.Kit.Main, "/bin/sh", "-c", ": > \"$1/more.targets\"", "sh", planted.StandardOutput);

        Assert.Equal(0, planted.ExitCode);
        Assert.Equal("planted", await File.ReadAllTextAsync(Path.Combine(planted.StandardOutput, "planted.targets"), Token));
        Assert.Equal(1, found.ExitCode);
        Assert.NotEqual(0, written.ExitCode);
        Assert.False(File.Exists(Path.Combine(planted.StandardOutput, "more.targets")), "the hooks wrote the agent's caches");
    }

    /// <summary>
    /// A run's commit and push, whose hooks write the main checkout's git directory whole, lead no git elsewhere: a command
    /// inside neither rewrites nor removes its commondir - made naming that directory itself, which git takes as no pointer
    /// - makes no worktree's git directory under its worktrees/, and rewrites no .git file of a linked worktree standing in
    /// the main checkout; and git still stages and reads its status there from inside.
    /// </summary>
    [Fact]
    public async Task TheHooksInTheMainCheckout_LeadNoGitElsewhere_AndGitStillWorksThere()
    {
        using var temp = new TempDirectory();
        using var home = new TempDirectory();
        var platform = RealIsolation.Platform();
        await ProvedAsync(platform, temp.Path);
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var inside = Path.Combine(kit.Main, "inside");
        await kit.Harness.RunGitAsync(kit.Main, ["worktree", "add", "--quiet", inside, "-b", "inside"], Token);
        var config = new Core.Configuration.HarnessConfig { Worktrees = OrchestrationKit.Settings };
        using var isolated = await new Core.Agents.Isolation.AgentIsolation(platform, kit.Harness.GitClient, kit.Harness.FileSystem, new HomedPlatform(home.Path))
            .ForExecutionAsync(config, Core.Agents.Isolation.ExecutionGrounds.OfHooks(config, kit.Main, kit.Main, sshAgent: false), kit.Main, Token);
        var git = Path.Combine(kit.Main, ".git");
        var commondir = Path.Combine(git, Core.Agents.Isolation.AgentIsolation.CommonDirectoryFile);
        var planted = Path.Combine(git, "worktrees", "planted");
        var dotGit = Path.Combine(inside, ".git");
        var pointing = await File.ReadAllTextAsync(dotGit, Token);
        await File.WriteAllTextAsync(Path.Combine(kit.Main, "made.txt"), "made", Token);

        var rewritten = await RealIsolation.InsideAsync(isolated.ForProcess!, kit.Main, "/bin/sh", "-c", "echo /elsewhere > \"$1\"", "sh", commondir);
        var removed = await RealIsolation.InsideAsync(isolated.ForProcess!, kit.Main, "/bin/sh", "-c", "rm -f \"$1\"", "sh", commondir);
        var made = await RealIsolation.InsideAsync(isolated.ForProcess!, kit.Main, "/bin/sh", "-c", "mkdir \"$1\"", "sh", planted);
        var pointed = await RealIsolation.InsideAsync(isolated.ForProcess!, kit.Main, "/bin/sh", "-c", "echo gitdir: /elsewhere > \"$1\"", "sh", dotGit);
        var staged = await RealIsolation.InsideAsync(isolated.ForProcess!, kit.Main, "git", "add", "made.txt");
        var status = await RealIsolation.InsideAsync(isolated.ForProcess!, kit.Main, "git", "status", "--porcelain");

        Assert.All(new[] { rewritten, removed, made, pointed }, refused => Assert.NotEqual(0, refused.ExitCode));
        Assert.Equal(
            (Core.Agents.Isolation.AgentIsolation.ItsOwnDirectory, false, pointing),
            (await File.ReadAllTextAsync(commondir, Token), Directory.Exists(planted), await File.ReadAllTextAsync(dotGit, Token)));
        Assert.Equal((0, 0), (staged.ExitCode, status.ExitCode));
        Assert.Equal(["made.txt"], (await kit.Harness.GitClient.RunAsync(kit.Main, ["diff", "--cached", "--name-only"], cancellationToken: Token)).OutputLines);
        Assert.Null(await isolated.AfterwardAsync(Token));
    }

    /// <summary>
    /// A command inside a step's launch reads no place the user keeps credentials in: ssh's directory is an empty place
    /// that holds its known_hosts and config alone, read and never written; AWS's directory is an empty place; and the
    /// logins curl and git read are the null device, which nothing opens there - every one of them left as it was outside.
    /// </summary>
    [Fact]
    public async Task ACommandInside_ReadsNoPlaceTheUserKeepsCredentialsIn_ButSshsKnownHostsAndConfig()
    {
        using var temp = new TempDirectory();
        using var home = RealIsolation.Home();
        var platform = RealIsolation.Platform();
        await ProvedAsync(platform, temp.Path);
        var credentials = await RealIsolation.CredentialsAsync(home);
        var launch = await RealIsolation.StepLaunchAsync(temp, home, platform);

        var listed = await RealIsolation.InsideAsync(launch.Commands, launch.Worktree, "/bin/sh", "-c", "ls -A \"$1\"", "sh", Path.GetDirectoryName(credentials.Key)!);
        var hidden = new[] { credentials.Key, credentials.Aws }.Select(place => (place, RealIsolation.InsideAsync(launch.Commands, launch.Worktree, "/bin/sh", "-c", "cat \"$1\"", "sh", place))).ToList();
        var netrc = await RealIsolation.InsideAsync(launch.Commands, launch.Worktree, "/bin/sh", "-c", "cat \"$1\"", "sh", credentials.Netrc);
        var read = await RealIsolation.InsideAsync(launch.Commands, launch.Worktree, "/bin/sh", "-c", "cat \"$1\" \"$2\"", "sh", credentials.KnownHosts, credentials.Config);
        var written = await RealIsolation.InsideAsync(launch.Commands, launch.Worktree, "/bin/sh", "-c", ": > \"$1\"", "sh", credentials.KnownHosts);

        Assert.Equal(0, listed.ExitCode);
        Assert.Equal(["config", "known_hosts"], listed.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries));

        foreach (var (place, reading) in hidden)
        {
            var result = await reading;

            Assert.Equal((1, string.Empty), (result.ExitCode, result.StandardOutput));
            Assert.True(result.StandardError.Contains("No such file or directory", StringComparison.Ordinal), $"{place}: {result.StandardError}");
        }

        Assert.Equal((1, string.Empty), (netrc.ExitCode, netrc.StandardOutput));
        Assert.Contains("Permission denied", netrc.StandardError, StringComparison.Ordinal);
        Assert.Equal((0, "knownHost *"), (read.ExitCode, read.StandardOutput));
        Assert.NotEqual(0, written.ExitCode);
        Assert.Contains("Read-only file system", written.StandardError, StringComparison.Ordinal);
        Assert.Equal(
            ["secret", "known", "Host *", "secret", "secret"],
            await Task.WhenAll(new[] { credentials.Key, credentials.KnownHosts, credentials.Config, credentials.Aws, credentials.Netrc }.Select(file => File.ReadAllTextAsync(file, Token))));
    }

    /// <summary>
    /// A push over ssh reaches the user's SSH agent inside ssh's hidden directory - its socket there, or a link there to
    /// one elsewhere, named to the push at its real spelling - while the key beside it stays unread; a commit, which reaches
    /// no agent, reaches neither socket by any name.
    /// </summary>
    [Fact]
    public async Task APushInside_ReachesTheUsersSshAgent_InsideSshsHiddenDirectory_AndNoKeyThere()
    {
        using var temp = new TempDirectory();
        using var home = RealIsolation.Home();
        var platform = RealIsolation.Platform();
        await ProvedAsync(platform, temp.Path);
        var runner = RealIsolation.Runner();
        BuildTools.Need(runner, "a push's SSH agent", "ssh-agent", "ssh-add");
        var credentials = await RealIsolation.CredentialsAsync(home);
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var config = new Core.Configuration.HarnessConfig { Worktrees = OrchestrationKit.Settings };
        var inside = home.Combine(".ssh", "agent");
        var elsewhere = temp.Combine("agent");
        var linked = home.Combine(".ssh", "linked");
        File.CreateSymbolicLink(linked, elsewhere);
        var agents = new List<int>();

        try
        {
            foreach (var socket in new[] { inside, elsewhere })
            {
                var started = await runner.RunAsync(new ProcessRequest { FileName = "ssh-agent", Arguments = ["-s", "-a", socket], Timeout = TimeSpan.FromSeconds(30) }, Token);

                Assert.Equal(0, started.ExitCode);
                agents.Add(int.Parse(System.Text.RegularExpressions.Regex.Match(started.StandardOutput, "SSH_AGENT_PID=([0-9]+);").Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture));
            }

            foreach (var named in new[] { inside, linked })
            {
                var isolation = new Core.Agents.Isolation.AgentIsolation(platform, kit.Harness.GitClient, kit.Harness.FileSystem, new HomedPlatform(home.Path), name => name == Core.Agents.Isolation.AgentIsolation.SshAgentSocketVariable ? named : null);
                using var push = await isolation.ForExecutionAsync(config, Core.Agents.Isolation.ExecutionGrounds.OfHooks(config, kit.Main, kit.Main, sshAgent: true), kit.Main, Token);
                using var commit = await isolation.ForExecutionAsync(config, Core.Agents.Isolation.ExecutionGrounds.OfHooks(config, kit.Main, kit.Main, sshAgent: false), kit.Main, Token);

                var reached = await RealIsolation.InsideAsync(push.ForProcess!, kit.Main, "ssh-add", "-l");
                var key = await RealIsolation.InsideAsync(push.ForProcess!, kit.Main, "/bin/sh", "-c", "cat \"$1\"", "sh", credentials.Key);
                var unreached = await RealIsolation.InsideAsync(commit.ForProcess!, kit.Main, "/bin/sh", "-c", "SSH_AUTH_SOCK=\"$1\" ssh-add -l", "sh", named);

                Assert.Equal((named, 1, "The agent has no identities."), (named, reached.ExitCode, reached.StandardOutput.Trim()));
                Assert.Equal((named, 1, string.Empty), (named, key.ExitCode, key.StandardOutput));
                Assert.Contains("No such file or directory", key.StandardError, StringComparison.Ordinal);
                Assert.Equal((named, 2), (named, unreached.ExitCode));
            }
        }
        finally
        {
            foreach (var agent in agents)
            {
                try
                {
                    using var process = System.Diagnostics.Process.GetProcessById(agent);
                    process.Kill();
                }
                catch (ArgumentException)
                {
                    // The agent has ended already.
                }
            }
        }
    }

    /// <summary>
    /// A sealed file that is not there is held by no rule of bubblewrap's: a bind at a place that is not there is skipped,
    /// so a command inside makes the file - which is why a ground that needs one held, a reftable repository's commondir,
    /// is refused under bubblewrap rather than run with the path open.
    /// </summary>
    [Fact]
    public async Task ASealedFileThatIsNotThere_IsHeldByNoRule_ACommandInsideMakesIt()
    {
        using var temp = new TempDirectory();
        var platform = RealIsolation.Platform();
        var bubblewrap = Assert.IsType<Bubblewrap>(await ProvedAsync(platform, temp.Path));
        var root = Directory.CreateDirectory(Path.Combine(temp.Path, "writable")).FullName;
        var absent = Path.Combine(root, "sealed");
        var isolation = new ProcessIsolation(bubblewrap, new IsolationProfile([], [new IsolatedWrite(root, Directory: true, Writable: true), new IsolatedWrite(absent, Directory: false, Writable: false)]));

        var made = await RealIsolation.InsideAsync(isolation, root, "/bin/sh", "-c", ": > \"$1\"", "sh", absent);

        Assert.Equal((0, true), (made.ExitCode, File.Exists(absent)));
    }

    /// <summary>
    /// The probe refuses a bubblewrap this user could replace - the program, or a directory above it, writable to them - for
    /// the tool runs it outside before anything is isolated: no mechanism, saying which and the remedy, and nothing asked.
    /// </summary>
    [Fact]
    public async Task ABubblewrapTheUserCouldReplace_IsNoMechanism_AndIsAskedNothing()
    {
        using var tools = new TempDirectory();
        using var tree = new TempDirectory();
        var program = Path.Combine(tools.Path, "bwrap");
        await File.WriteAllTextAsync(program, string.Empty, Token);
        var runner = new AnsweringRunner(_ => Ended(0, string.Empty));
        var platform = new Core.Platform.IsolationPlatform(new SaidPlatform(Core.Platform.PlatformId.Linux), runner, path: () => tools.Path, userWrites: place => place == tools.Path, landlock: () => 6);

        var found = await platform.ProbeAsync(tree.Path, Token);

        Assert.Equal(
            ((IIsolationMechanism?)null,
                $"bubblewrap '{program}' is one this user may replace ('{tools.Path}' is writable to them), and the tool runs it outside every isolation",
                "install bubblewrap where only the system writes it (sudo apt-get install bubblewrap puts it in /usr/bin) and put that directory first on the PATH, or set agentPolicy.isolation to \"preferred\" to run agents on the words policy alone, said at every launch"),
            (found.Mechanism, found.Unavailable, found.Remedy));
        Assert.Empty(runner.Asked);
    }

    /// <summary>
    /// A bubblewrap only the system writes is asked its version and proved by the trial, contained and inside it - scoped by
    /// Landlock where the kernel has its scope, and its record saying what it leaves reached where the kernel has not.
    /// </summary>
    [Theory]
    [InlineData(6, true)]
    [InlineData(5, false)]
    public async Task ABubblewrapOnlyTheSystemWrites_IsProved_ScopedWhereTheKernelHasLandlocksScope(int landlock, bool scoped)
    {
        using var tools = new TempDirectory();
        using var tree = new TempDirectory();
        var program = Path.Combine(tools.Path, "bwrap");
        await File.WriteAllTextAsync(program, string.Empty, Token);
        var runner = Answering();
        var platform = new Core.Platform.IsolationPlatform(new SaidPlatform(Core.Platform.PlatformId.Linux), runner, path: () => tools.Path, userWrites: _ => false, landlock: () => landlock);

        var found = await platform.ProbeAsync(tree.Path, Token);

        var bubblewrap = Assert.IsType<Bubblewrap>(found.Mechanism);

        Assert.Equal((program, "0.9.0", scoped, scoped ? null : Bubblewrap.Unscoped, (string?)null), (bubblewrap.Program, bubblewrap.Version, bubblewrap.Scoped, bubblewrap.Leaves, bubblewrap.Unnested));
        Assert.Equal([(program, false), ("/bin/sh", true)], runner.Asked.Select(asked => (asked.FileName, asked.Contained)));
        Assert.Equal(bubblewrap, (Bubblewrap)runner.Asked[1].Isolation!.Mechanism);
    }

    /// <summary>
    /// A process runner that answers the probe as a bubblewrap would: its version, and the trial writing its open place.
    /// </summary>
    private static AnsweringRunner Answering()
        => new(request =>
        {
            if (request.Arguments is ["--version"])
            {
                return Ended(0, "bubblewrap 0.9.0\n");
            }

            File.WriteAllText(Path.Combine(request.Arguments[^2], "w"), string.Empty);
            return Ended(0, string.Empty);
        });

    /// <summary>A process's end as a test gives it.</summary>
    private static ProcessResult Ended(int code, string output) => new(code, output, string.Empty, TimeSpan.Zero, TimedOut: false);

    /// <summary>A process runner that answers each request as a test says, keeping every request it was asked.</summary>
    private sealed class AnsweringRunner(Func<ProcessRequest, ProcessResult> answer) : IProcessRunner
    {
        public List<ProcessRequest> Asked { get; } = [];

        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
        {
            Asked.Add(request);
            return Task.FromResult(answer(request));
        }

        public string? FindExecutable(string command) => null;
    }

    /// <summary>The bubblewrap the probe proved: the test skipped where this is not Linux.</summary>
    private static Task<IIsolationMechanism> ProvedAsync(Core.Platform.IsolationPlatform platform, string tree)
        => RealIsolation.ProvedAsync(platform, tree, OperatingSystem.IsLinux(), "Bubblewrap isolates an agent's commands on Linux alone.", "bubblewrap");
}
