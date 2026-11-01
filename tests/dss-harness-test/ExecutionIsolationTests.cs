using DssHarness.Core.Agents.Isolation;
using DssHarness.Core.Ci;
using DssHarness.Core.Configuration;
using DssHarness.Core.Execution;
using DssHarness.Core.Git;
using DssHarness.Core.Platform;
using DssHarness.Core.Processes;
using DssHarness.Core.Repository;
using NSubstitute;

namespace DssHarness.Tests;

/// <summary>
/// The operating system's isolation of the tool's own executions of the agents' work: the profile a leg's work is given -
/// its places exact - the refusal and the notice its mode turns a missing mechanism into, a phase held inside its leg's
/// isolation, the repository's hooks run by none of the tool's git but a commit's and a push's, and a push's credential
/// handed in as values.
/// </summary>
public sealed class ExecutionIsolationTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// A leg's work in its copy inside an agent's worktree is given, by its profile, the machine read-only: hidden every
    /// credential slot of the main checkout there is; written the copy it works in, the run's records, what else it writes
    /// beside them and the isolation's tool caches, with a temporary directory of its own; inside the copy - no repository
    /// of its own, so the worktree's git stays read-only - its configuration directory and the tool's state written by
    /// nobody. The probe is asked for the main checkout, and the temporary directory goes with the decision.
    /// </summary>
    [Fact]
    public async Task ALegsWork_IsDeniedTheSlotsGitsCodeAndTheState_ItsCopyItsRecordsAndItsWorkersWrittenAgain()
    {
        using var temp = new TempDirectory();
        using var home = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("a1");
        var copy = Path.Combine(worktree, HarnessLayout.LegsDirectoryName, "linux-x64-debug");
        var workers = Path.Combine(copy, HarnessLayout.MutationsDirectoryName);
        var records = Path.Combine(worktree, HarnessLayout.DirectoryName, "runs", "r1");
        Directory.CreateDirectory(copy);
        var platform = new FixedIsolationPlatform(IsolationAvailability.Of(new PassThroughMechanism()));
        var isolation = new AgentIsolation(platform, kit.Harness.GitClient, kit.Harness.FileSystem, new HomedPlatform(home.Path), _ => null);
        var config = new HarnessConfig { Worktrees = OrchestrationKit.Settings };
        var ground = ExecutionGrounds.OfLeg(config, kit.Main, worktree, copy, records, [workers]);

        var isolated = await isolation.ForExecutionAsync(config, ground, kit.Main, Token);

        var caches = Path.Combine(IsolationCaches.Root(home.Path, new HostPlatform().Current, _ => null), IsolationCaches.Directory);
        IReadOnlyList<IsolatedWrite> writes =
        [
            new(copy, Directory: true, Writable: true),
            new(records, Directory: true, Writable: true),
            new(workers, Directory: true, Writable: true),
            new(caches, Directory: true, Writable: true),
            new(Path.Combine(copy, HarnessLayout.DirectoryName), Directory: true, Writable: false),
            .. ground.StateDirectories
                .Select(Path.GetFullPath)
                .Where(state => IsolationProfile.Covers(copy, state) && !IsolationProfile.Covers(Path.Combine(copy, HarnessLayout.DirectoryName), state) && !HarnessLayout.StateFileNames.Contains(Path.GetFileName(state)))
                .Select(state => new IsolatedWrite(state, Directory: true, Writable: false)),
        ];

        Assert.Equal(IsolatedExecution.Legs, ground.Kind);
        Assert.Equal([kit.Main, worktree, copy], ground.Trees);
        Assert.Equal([copy, records, workers], ground.WritableRoots);
        Assert.Equal(
            [.. ground.Trees.SelectMany(tree => HarnessLayout.StatePathsUnder(tree, OrchestrationKit.Settings.Root!)).Distinct(StringComparer.OrdinalIgnoreCase).Where(state => state != workers)],
            ground.StateDirectories);
        Assert.Contains(workers, HarnessLayout.StatePathsUnder(copy, OrchestrationKit.Settings.Root!));
        Assert.Equal((false, null, null), (isolated.Refused, isolated.Notice, isolated.Refusal("'build'")));
        Assert.Equal([kit.Main], platform.Probed);
        Assert.Equal(
            [.. HarnessLayout.SecretSlotDirectoriesUnder(kit.Main).Where(Directory.Exists).Select(slot => new IsolatedPath(slot, Directory: true))],
            isolated.Profile!.Hidden);
        Assert.Equal(writes.OrderBy(write => write.Path, StringComparer.Ordinal), isolated.Profile.Writes.OrderBy(write => write.Path, StringComparer.Ordinal));
        Assert.Equal([new("TMPDIR", isolated.Profile.Temp!), .. IsolationCaches.Environment(caches)], isolated.Profile.Environment);
        Assert.Empty(isolated.Profile.Sockets);
        Assert.Same(isolated.Profile, isolated.ForProcess!.Profile);
        Assert.Equal(new AgentIsolationRecord(AgentIsolationMode.Required) { Mechanism = "test", Version = "1.0" }, isolated.Record);
        Assert.True(Directory.Exists(isolated.Profile.Temp));

        isolated.Dispose();

        Assert.False(Directory.Exists(isolated.Profile.Temp));
    }

    /// <summary>
    /// A commit's or a push's hooks reach the user's SSH agent's socket alone, at its real place - the one a push over ssh
    /// signs with - while a leg's work reaches none; no agent's own command is given one.
    /// </summary>
    [Fact]
    public async Task AHooksExecution_ReachesTheUsersSshAgentAlone_ALegsWorkNone()
    {
        using var temp = new TempDirectory();
        using var home = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var socket = Path.Combine(home.Path, "agent.sock");
        await File.WriteAllTextAsync(socket, string.Empty, Token);
        var isolation = new AgentIsolation(
            new FixedIsolationPlatform(IsolationAvailability.Of(new PassThroughMechanism())),
            kit.Harness.GitClient,
            kit.Harness.FileSystem,
            new HomedPlatform(home.Path),
            name => name == AgentIsolation.SshAgentSocketVariable ? socket : null);
        var config = new HarnessConfig { Worktrees = OrchestrationKit.Settings };

        using var hooks = await isolation.ForExecutionAsync(config, ExecutionGrounds.OfHooks(config, kit.Main, kit.Main), kit.Main, Token);
        using var legs = await isolation.ForExecutionAsync(config, ExecutionGrounds.OfLegs(config, kit.Main, kit.Main), kit.Main, Token);

        Assert.Equal([socket], hooks.Profile!.Sockets);
        Assert.Empty(legs.Profile!.Sockets);
    }

    /// <summary>
    /// A commit's or a push's git works over the main checkout, which it writes - its work tree, its index, its objects and
    /// its refs - but for git's code-running files and the tool's state under it.
    /// </summary>
    [Fact]
    public void AHooksGround_WritesTheTreeItWorksIn_AndSealsItsState()
    {
        var config = new HarnessConfig { Worktrees = OrchestrationKit.Settings };
        var main = Path.Combine(Path.GetTempPath(), "repo");

        var ground = ExecutionGrounds.OfHooks(config, main, main);

        Assert.Equal(IsolatedExecution.Hooks, ground.Kind);
        Assert.Equal([main], ground.Trees);
        Assert.Equal([main], ground.WritableRoots);
        Assert.Equal(HarnessLayout.SecretSlotDirectoriesUnder(main), ground.SlotDirectories);
        Assert.Equal(HarnessLayout.StatePathsUnder(main, OrchestrationKit.Settings.Root!), ground.StateDirectories);
        Assert.Equal([main], ExecutionGrounds.OfLegs(config, main, main).WritableRoots);
    }

    /// <summary>
    /// With no mechanism on the machine, an execution of the agents' work is refused under required - why, what it keeps
    /// from running, then the remedy - and runs under preferred, its notice saying the same, its record why there was none.
    /// </summary>
    [Fact]
    public async Task WithNoMechanism_AnExecutionIsRefusedUnderRequired_AndSaidUnderPreferred()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var isolation = new AgentIsolation(new FixedIsolationPlatform(IsolationAvailability.None("bubblewrap is not here", "install it")), harness.GitClient, harness.FileSystem, new HomedPlatform(temp.Path), _ => null);
        var preferring = new HarnessConfig { AgentPolicy = new AgentPolicyConfig { Isolation = AgentIsolationMode.Preferred } };

        var legs = await isolation.ForExecutionAsync(new HarnessConfig(), ExecutionGrounds.OfLegs(new HarnessConfig(), temp.Path, temp.Path), temp.Path, Token);
        var hooks = await isolation.ForExecutionAsync(new HarnessConfig(), ExecutionGrounds.OfHooks(new HarnessConfig(), temp.Path, temp.Path), temp.Path, Token);
        var preferredLegs = await isolation.ForExecutionAsync(preferring, ExecutionGrounds.OfLegs(preferring, temp.Path, temp.Path), temp.Path, Token);
        var preferredHooks = await isolation.ForExecutionAsync(preferring, ExecutionGrounds.OfHooks(preferring, temp.Path, temp.Path), temp.Path, Token);

        Assert.Equal(
            (true, "'build' is refused: agentPolicy.isolation is \"required\" and bubblewrap is not here, so nothing the agents wrote is built, tested or run here; install it", (string?)null),
            (legs.Refused, legs.Refusal("'build'"), legs.Notice));
        Assert.Equal(
            "'commit' is refused: agentPolicy.isolation is \"required\" and bubblewrap is not here, so no hook of the repository's runs over the agents' work here; install it",
            hooks.Refusal("'commit'"));
        Assert.Equal(
            (false, (string?)null, "agentPolicy.isolation is \"preferred\" and bubblewrap is not here, so what the agents wrote is built, tested and run here with no isolation of the operating system's; install it"),
            (preferredLegs.Refused, preferredLegs.Refusal("'build'"), preferredLegs.Notice));
        Assert.Equal(
            "agentPolicy.isolation is \"preferred\" and bubblewrap is not here, so the repository's hooks run over the agents' work with no isolation of the operating system's; install it",
            preferredHooks.Notice);
        Assert.Equal(new AgentIsolationRecord(AgentIsolationMode.Preferred) { None = "bubblewrap is not here" }, preferredHooks.Record);
        Assert.Equal("""{"mode":"preferred","none":"bubblewrap is not here"}""", preferredHooks.Record.ToOutput().ToJsonString());
        Assert.Null(preferredLegs.ForProcess);
    }

    /// <summary>
    /// A phase started while its leg's work holds an isolation runs inside it - the mechanism handed its profile - and one
    /// left running, which outlives everything inside, and one started where none is held, run as they are.
    /// </summary>
    [Fact]
    public async Task APhase_RunsInsideTheIsolationItsLegsWorkHolds_ButOneLeftRunningAndOneOutsideRunAsTheyAre()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var phases = new PhaseRunner(harness.ProcessRunner, harness.FileSystem, harness.Output);
        var mechanism = new PassThroughMechanism();
        var profile = new IsolationProfile([], [new IsolatedWrite(temp.Path, Directory: true, Writable: true)]);
        var child = TestHost.ChildRequest("echo-args", "a");

        PhaseRequest Phase(string name, bool leftRunning = false) => new()
        {
            Leg = "native",
            Phase = name,
            FileName = child.FileName,
            Arguments = child.Arguments,
            Environment = child.Environment,
            LogFile = temp.Combine($"{name}.log"),
            LeftRunning = leftRunning,
        };

        PhaseResult inside, leftRunning;

        using (PhaseIsolation.Enter(new ProcessIsolation(mechanism, profile)))
        {
            inside = await phases.RunAsync(Phase("build"), Token);
            leftRunning = await phases.RunAsync(Phase("up", leftRunning: true), Token);
        }

        var outside = await phases.RunAsync(Phase("test"), Token);

        Assert.Equal((0, 0, 0), (inside.ExitCode, leftRunning.ExitCode, outside.ExitCode));
        Assert.Equal([profile], mechanism.Profiles);
        Assert.Null(PhaseIsolation.Current);
    }

    /// <summary>
    /// None of the repository's hooks runs under the tool's own git - those git's own directory holds, and those
    /// core.hooksPath points into the tree for, as a husky directory - whatever the command: a checkout of a file and a
    /// commit run none. A command that runs them on purpose runs them, inside the isolation it is given: the mechanism is
    /// handed its profile. And git still answers where the hooks are, as it runs them for everyone else.
    /// </summary>
    [Fact]
    public async Task TheToolsOwnGit_RunsNoHook_ButACommandThatRunsThemOnPurpose_RunsThemInsideItsIsolation()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var tree = temp.Path;

        await harness.RunGitAsync(tree, ["init", "--quiet", "."], Token);
        await harness.RunGitAsync(tree, ["config", "user.name", "t"], Token);
        await harness.RunGitAsync(tree, ["config", "user.email", "t@t"], Token);
        File.WriteAllText(temp.Combine("a.txt"), "a\n");
        await harness.RunGitAsync(tree, ["add", "a.txt"], Token);
        await harness.RunGitAsync(tree, ["commit", "--quiet", "-m", "first"], Token);
        Hook(Path.Combine(tree, ".git", "hooks"), "post-checkout");
        Hook(Path.Combine(tree, ".git", "hooks"), "pre-commit");

        File.WriteAllText(temp.Combine("a.txt"), "changed\n");
        var checkedOut = await harness.GitClient.RunAsync(tree, ["checkout", "HEAD", "--", "a.txt"], cancellationToken: Token);
        File.WriteAllText(temp.Combine("b.txt"), "b\n");
        await harness.RunGitAsync(tree, ["add", "b.txt"], Token);
        var committed = await harness.GitClient.RunAsync(tree, ["commit", "--quiet", "-m", "second"], cancellationToken: Token);

        Assert.True(checkedOut.Succeeded && committed.Succeeded, checkedOut.FailureMessage + committed.FailureMessage);
        Assert.Equal([], Ran(tree));

        await harness.RunGitAsync(tree, ["config", "core.hooksPath", ".husky"], Token);
        Hook(Path.Combine(tree, ".husky"), "pre-commit");
        var hooks = await harness.GitClient.RunAsync(tree, ["rev-parse", "--path-format=absolute", "--git-path", "hooks"], cancellationToken: Token);
        File.WriteAllText(temp.Combine("c.txt"), "c\n");
        await harness.RunGitAsync(tree, ["add", "c.txt"], Token);
        var unhooked = await harness.GitClient.RunAsync(tree, ["commit", "--quiet", "-m", "third"], cancellationToken: Token);

        Assert.True(unhooked.Succeeded, unhooked.FailureMessage);
        Assert.Equal([], Ran(tree));
        Assert.Equal(Path.GetFullPath(Path.Combine(tree, ".husky")), Path.GetFullPath(hooks.OutputLines[0].Trim()));

        var mechanism = new PassThroughMechanism();
        var profile = new IsolationProfile([], [new IsolatedWrite(tree, Directory: true, Writable: true)]);
        File.WriteAllText(temp.Combine("d.txt"), "d\n");
        await harness.RunGitAsync(tree, ["add", "d.txt"], Token);
        var hooked = await harness.GitClient.RunWithHooksAsync(tree, ["commit", "--quiet", "-m", "fourth"], TimeSpan.FromMinutes(1), new ProcessIsolation(mechanism, profile), null, Token);

        Assert.True(hooked.Succeeded, hooked.FailureMessage);
        Assert.Equal(["pre-commit"], Ran(tree));
        Assert.Equal([profile], mechanism.Profiles);
    }

    /// <summary>
    /// The credential git's helpers give for an address is asked of them as git asks it; handed to a git as values, the one
    /// helper its command line names answers git with them, in place of every helper its configuration names.
    /// </summary>
    [Fact]
    public async Task ACredential_IsAskedOfGitsHelpers_AndHandedInAsValues_InPlaceOfEveryHelper()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        await harness.RunGitAsync(temp.Path, ["init", "--quiet", "."], Token);
        // The machine's own helpers cleared first, so the repository's is the one asked.
        await harness.RunGitAsync(temp.Path, ["config", "--add", "credential.helper", string.Empty], Token);
        await harness.RunGitAsync(temp.Path, ["config", "--add", "credential.helper", "!f() { echo username=configured; echo password=secret-of-the-helper; }; f"], Token);

        var asked = await harness.GitClient.CredentialAsync(temp.Path, "https://example.com/owner/repo.git", Token);
        var handed = new GitCredential("handed", "token-of-the-values");
        var answered = await harness.ProcessRunner.RunAsync(
            new ProcessRequest
            {
                FileName = "git",
                Arguments = [.. GitCredentialValues.Arguments(handed), "credential", "fill"],
                WorkingDirectory = temp.Path,
                Environment = GitCredentialValues.Values(handed)!.ToDictionary(pair => pair.Key, pair => (string?)pair.Value, StringComparer.Ordinal),
                StandardInput = ChildInput.Of("url=https://example.com/owner/repo.git\n\n"),
            },
            Token);

        Assert.Equal(new GitCredential("configured", "secret-of-the-helper"), asked);
        Assert.Equal("GitCredential { Username = configured }", asked!.ToString());
        Assert.Equal(
            ["protocol=https", "host=example.com", "username=handed", "password=token-of-the-values"],
            answered.StandardOutput.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.Equal([], GitCredentialValues.Arguments(null));
        Assert.Null(GitCredentialValues.Values(null));
    }

    /// <summary>
    /// A push inside an isolation is handed the credential git's helpers give for its remote's address, where it is reached
    /// over https - its helper on its command line, the values in its environment, the isolation with it; one reached over
    /// ssh, and one with no isolation, ask for none and are handed none.
    /// </summary>
    [Fact]
    public async Task APushInsideAnIsolation_IsHandedTheCredential_OnlyOverHttp()
    {
        var git = Substitute.For<IGitClient>();
        var url = "https://example.com/owner/repo.git";
        git.RunAsync("/tree", Arg.Is<IReadOnlyList<string>>(arguments => arguments.SequenceEqual(new[] { "remote", "get-url", "--push", "origin" })), false, Token)
            .Returns(_ => Task.FromResult(new GitCommandResult(0, url + "\n", string.Empty)));
        git.CredentialAsync("/tree", default!, Token).ReturnsForAnyArgs(Task.FromResult<GitCredential?>(new GitCredential("u", "p")));
        var calls = new List<(IReadOnlyList<string> Arguments, ProcessIsolation? Isolation, IReadOnlyDictionary<string, string>? Values)>();
        git.RunWithHooksAsync(default!, default!, default, default, default, Token).ReturnsForAnyArgs(call =>
        {
            calls.Add((call.ArgAt<IReadOnlyList<string>>(1), call.ArgAt<ProcessIsolation?>(3), call.ArgAt<IReadOnlyDictionary<string, string>?>(4)));
            return Task.FromResult(new GitCommandResult(0, "To remote\n*\tabc:refs/heads/work\t[new branch]\nDone\n", string.Empty));
        });
        var publisher = new GitPublisher(git, new GhProcess(Substitute.For<IProcessRunner>(), GitLocalVariables.Fixed([]), _ => null));
        var isolation = new ProcessIsolation(new PassThroughMechanism(), IsolationProfile.Nothing);

        _ = await publisher.PushAsync("/tree", "origin", "abc", "work", null, isolation, Token);
        _ = await publisher.PushAsync("/tree", "origin", "abc", "work", null, null, Token);
        url = "git@example.com:owner/repo.git";
        _ = await publisher.PushAsync("/tree", "origin", "abc", "work", null, isolation, Token);

        Assert.Equal(
            ["-c", "credential.helper=", "-c", "credential.helper=" + GitCredentialValues.Helper, "push", "--porcelain", "origin", "abc:refs/heads/work"],
            calls[0].Arguments);
        Assert.Same(isolation, calls[0].Isolation);
        Assert.Equal(new Dictionary<string, string> { [GitCredentialValues.UsernameVariable] = "u", [GitCredentialValues.PasswordVariable] = "p" }, calls[0].Values);
        Assert.Equal(["push", "--porcelain", "origin", "abc:refs/heads/work"], calls[1].Arguments);
        Assert.Equal(((ProcessIsolation?)null, (IReadOnlyDictionary<string, string>?)null), (calls[1].Isolation, calls[1].Values));
        Assert.Equal(["push", "--porcelain", "origin", "abc:refs/heads/work"], calls[2].Arguments);
        Assert.Null(calls[2].Values);
        await git.ReceivedWithAnyArgs(1).CredentialAsync(default!, default!, Token);
    }

    /// <summary>Writes a hook named <paramref name="name"/> in <paramref name="directory"/> that marks, in the tree it runs in, that it ran.</summary>
    private static void Hook(string directory, string name)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, $"#!/bin/sh\necho ran > \"ran-{name}\"\n");

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    /// <summary>The hooks that marked they ran in <paramref name="tree"/>, by name, in order.</summary>
    private static IReadOnlyList<string> Ran(string tree)
        => [.. Directory.GetFiles(tree, "ran-*").Select(file => Path.GetFileName(file)["ran-".Length..]).Order(StringComparer.Ordinal)];
}
