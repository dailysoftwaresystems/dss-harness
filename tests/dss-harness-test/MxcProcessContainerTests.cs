using DssHarness.Core.Agents.Isolation;
using DssHarness.Core.Agents.SideTasks;
using DssHarness.Core.Configuration;
using DssHarness.Core.Platform;
using DssHarness.Core.Processes;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;
using Microsoft.Mxc.Sdk.V1;
using MxcContainment = Microsoft.Mxc.Sdk.V1.Containment;

namespace DssHarness.Tests;

/// <summary>
/// Windows' mechanism, MXC's process container: the command line the contained step is handed, the container request and
/// every grant of its files - an anchor whose holder is written read alone, its entries written again - and every decision
/// of its probe, each from doubles of the machine on every system; then the container run for real on Windows where MXC's
/// library says the machine has it: the probe's decision as node, git and Git Bash's sh really behave inside it, and a
/// launch's commands kept from a credential slot's file, git's hooks, the tool's state and the launch's own directory, its
/// git directory and its harness directory never moved. The real tests are skipped where MXC's container is not on the
/// machine - no runner image is meant to hold it.
/// </summary>
public sealed class MxcProcessContainerTests
{
    private const string OrPreferred = ", or set agentPolicy.isolation to \"preferred\" to run agents on the words policy alone, said at every launch";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// The contained step is handed this tool again, as <c>isolate-exec</c>, with each hidden place, each place written by
    /// nobody and each writable root in the profile's order, each of the profile's variables, then the program's own words
    /// after '--'. The container meets every case of a name, gives the child a temporary directory of its own, takes no
    /// command handed to it one by one, and holds an anchor by reading it alone, which its records say.
    /// </summary>
    [Fact]
    public void TheContainer_IsHandedTheProfilesPlaces_AndTheProgramsWords()
    {
        var mechanism = new MxcProcessContainer("1.0.1", (@"C:\tools\dotnet.exe", ["exec", @"C:\tools\dssharness.dll"]));
        var profile = new IsolationProfile(
            [new IsolatedPath(@"C:\r\.harness-config\sshItems", Directory: true), new IsolatedPath(@"C:\r\.harness-config\runner\.env", Directory: false)],
            [new IsolatedWrite(@"C:\r\.worktrees", Directory: true, Writable: false), new IsolatedWrite(@"C:\r\.worktrees\a1", Directory: true, Writable: true), new IsolatedWrite(@"C:\r\.git\config", Directory: false, Writable: false)])
        {
            Environment = [new("npm_config_cache", @"C:\c\npm"), new("CARGO_HOME", @"C:\c\cargo")],
        };

        var (program, arguments) = mechanism.Command(profile, @"C:\Program Files\Git\cmd\git.exe", ["add", "a.txt"]);

        Assert.Equal((@"C:\tools\dotnet.exe", "mxc", "1.0.1"), (program, mechanism.Name, mechanism.Version));
        Assert.Equal(
            [
                "exec", @"C:\tools\dssharness.dll", "isolate-exec",
                "--hidden", @"C:\r\.harness-config\sshItems",
                "--hidden", @"C:\r\.harness-config\runner\.env",
                "--sealed", @"C:\r\.worktrees",
                "--writable", @"C:\r\.worktrees\a1",
                "--sealed", @"C:\r\.git\config",
                "--variable", @"npm_config_cache=C:\c\npm",
                "--variable", @"CARGO_HOME=C:\c\cargo",
                "--", @"C:\Program Files\Git\cmd\git.exe", "add", "a.txt",
            ],
            arguments);
        Assert.Equal(
            (true, false, (string?)null, MxcProcessContainer.AnchorReadAlone, true, false),
            (mechanism.HoldsEveryCase, mechanism.Scoped, mechanism.Leaves, mechanism.AnchorCost, mechanism.OwnTemp, mechanism.TakesHandedCommands));
    }

    /// <summary>
    /// The container request runs the program's command line as the C runtime reads it back into words, in the directory
    /// and with the environment given and nothing of the machine's own; its user interface the container's own, with no
    /// clipboard and no input sent elsewhere; the network the user's, the loopback both ways; every fixed drive read, each
    /// writable root written, each place written by nobody read, each hidden place denied.
    /// </summary>
    [Fact]
    public void TheRequest_ReadsTheDrives_WritesEachRoot_AndDeniesEachHiddenPlace()
    {
        var places = new ContainedPlaces([@"C:\r\.harness-config\sshItems"], [@"C:\r\.worktrees"], [@"C:\r\.worktrees\a1"]);
        var environment = new Dictionary<string, string> { ["PATH"] = @"C:\bin", ["MARK"] = "1" };
        var listed = new List<string>();

        var request = MxcProcessContainer.Request(
            places,
            @"C:\Program Files\nodejs\node.exe",
            ["-e", "say \"hi\"", "a b", @"C:\dir\"],
            @"C:\r\.worktrees\a1",
            environment,
            [@"C:\", @"D:\"],
            directory =>
            {
                listed.Add(directory);
                return [];
            });

        Assert.Equal("\"C:\\Program Files\\nodejs\\node.exe\" -e \"say \\\"hi\\\"\" \"a b\" C:\\dir\\", request.Command);
        Assert.Equal((@"C:\r\.worktrees\a1", false), (request.WorkingDirectory, request.InheritDefaultEnvironment));
        Assert.Equal(environment, request.Environment);
        Assert.Equal(ProcessContainerUiIsolation.Container, Assert.IsType<MxcContainment.ProcessContainer>(request.Containment).Ui!.Isolation);
        Assert.Equal((false, ClipboardPolicy.None, false), (request.Ui!.Disable, request.Ui.Clipboard, request.Ui.AllowInputInjection));
        Assert.Equal(
            ((NetworkAction?)NetworkAction.Allow, (NetworkAction?)NetworkAction.Allow, (NetworkAction?)NetworkAction.Allow),
            (request.Network!.Egress!.Default, request.Network.Ingress!.Default, request.Network.Ingress.HostLoopback));
        Assert.Equal([@"C:\", @"D:\", @"C:\r\.worktrees"], request.Filesystem!.ReadonlyPaths);
        Assert.Equal([@"C:\r\.worktrees\a1"], request.Filesystem.ReadwritePaths);
        Assert.Equal([@"C:\r\.harness-config\sshItems"], request.Filesystem.DeniedPaths);
        Assert.Empty(listed);
    }

    /// <summary>
    /// A directory above a guarded place whose own holder is written - one a command could rename, carrying the guarded
    /// place out from under its rule - is read alone, and each entry it holds is written again but those with rules of
    /// their own; an anchor held by one read alone is never moved, and is written as an entry; an anchor one more level
    /// down, whose holder is written again, is held in turn.
    /// </summary>
    [Fact]
    public void EveryAnchorWhoseHolderIsWritten_IsReadAlone_ItsEntriesWrittenAgain_ButThoseWithRulesOfTheirOwn()
    {
        var places = new ContainedPlaces(
            [@"C:\repo\.harness-config\sshItems"],
            [@"C:\repo\.git\hooks", @"C:\repo\.git\config", @"C:\repo\.git\worktrees\a1\config.worktree", @"C:\repo\.side-tasks\one"],
            [@"C:\repo"]);
        var entries = new Dictionary<string, string[]>
        {
            [@"C:\repo\.harness-config"] = [@"C:\repo\.harness-config\sshItems", @"C:\repo\.harness-config\config.json", @"C:\repo\.harness-config\workflows"],
            [@"C:\repo\.git"] = [@"C:\repo\.git\hooks", @"C:\repo\.git\config", @"C:\repo\.git\objects", @"C:\repo\.git\HEAD", @"C:\repo\.git\worktrees"],
            [@"C:\repo\.side-tasks"] = [@"C:\repo\.side-tasks\one", @"C:\repo\.side-tasks\two"],
            [@"C:\repo\.git\worktrees"] = [@"C:\repo\.git\worktrees\a1"],
            [@"C:\repo\.git\worktrees\a1"] = [@"C:\repo\.git\worktrees\a1\config.worktree", @"C:\repo\.git\worktrees\a1\index", @"C:\repo\.git\worktrees\a1\HEAD"],
        };
        var listed = new List<string>();

        var files = MxcProcessContainer.Files(places, [@"C:\"], directory =>
        {
            listed.Add(directory);
            return entries.GetValueOrDefault(directory, []);
        });

        Assert.Equal(
            [
                @"C:\",
                @"C:\repo\.git\hooks", @"C:\repo\.git\config", @"C:\repo\.git\worktrees\a1\config.worktree", @"C:\repo\.side-tasks\one",
                @"C:\repo\.harness-config", @"C:\repo\.git", @"C:\repo\.side-tasks", @"C:\repo\.git\worktrees\a1",
            ],
            files.ReadonlyPaths);
        Assert.Equal(
            [
                @"C:\repo",
                @"C:\repo\.harness-config\config.json", @"C:\repo\.harness-config\workflows",
                @"C:\repo\.git\objects", @"C:\repo\.git\HEAD", @"C:\repo\.git\worktrees",
                @"C:\repo\.side-tasks\two",
                @"C:\repo\.git\worktrees\a1\index", @"C:\repo\.git\worktrees\a1\HEAD",
            ],
            files.ReadwritePaths);
        Assert.Equal([@"C:\repo\.harness-config\sshItems"], files.DeniedPaths);
        Assert.Equal([@"C:\repo\.harness-config", @"C:\repo\.git", @"C:\repo\.side-tasks", @"C:\repo\.git\worktrees\a1"], listed);
    }

    /// <summary>
    /// On Windows the probe proves MXC's container once for the process, under the tree's runs directory: the library says
    /// the machine has it, the interpreter inside reads no hidden canary by its name or another case of it, writes no place
    /// written by nobody by either, writes no place outside every open one and writes the open place - each run alone -
    /// then node, git and Git Bash's sh found here each start inside it and answer; every later asker gets the same
    /// mechanism, and the probe's places are gone.
    /// </summary>
    [Fact]
    public async Task OnWindows_TheProbe_ProvesTheContainer_ThenEachToolFoundHere_OnceForTheProcess()
    {
        using var tools = new TempDirectory();
        using var tree = new TempDirectory();
        var (path, node, git, sh) = Tools(tools.Path);
        var runner = new WindowsRunner(_ => (0, "answered"));
        var platform = Windows(runner, path);

        var proved = await platform.ProbeAsync(tree.Path, Token);
        var again = await platform.ProbeAsync(tree.Path, Token);

        var container = Assert.IsType<MxcProcessContainer>(proved.Mechanism);

        Assert.Equal((MxcProcessContainer.MechanismName, "1.0.1"), (container.Name, container.Version));
        Assert.Same(container, again.Mechanism);
        Assert.Equal(
            [
                ("cmd.exe", "/d /c type canary"),
                ("cmd.exe", "/d /c type canary"),
                ("cmd.exe", "/d /c type nul > w"),
                ("cmd.exe", "/d /c type nul > w"),
                ("cmd.exe", "/d /c type nul > w"),
                ("cmd.exe", "/d /c type nul > w"),
                (Path.GetFileName(node), "--version"),
                (Path.GetFileName(git), "--version"),
                (Path.GetFileName(sh), "-c echo answered"),
            ],
            runner.Asked.Select(asked => (Path.GetFileName(asked.FileName), string.Join(' ', asked.Arguments.Select(word => word.Length > 2 && word.Contains(Path.DirectorySeparatorChar) ? Path.GetFileName(word) : word)))));
        Assert.All(runner.Asked, asked => Assert.Equal((true, (IIsolationMechanism)container), (asked.Contained, asked.Isolation!.Mechanism)));
        Assert.All(runner.Asked.Take(6), asked =>
        {
            Assert.Equal(["hidden"], asked.Isolation!.Profile.Hidden.Select(place => Path.GetFileName(place.Path)));
            Assert.Equal([("open", true), ("sealed", false)], asked.Isolation.Profile.Writes.Select(write => (Path.GetFileName(write.Path), write.Writable)));
            Assert.StartsWith(Path.Combine(tree.Path, HarnessLayout.DirectoryName, HarnessLayout.RunsDirectoryName, "isolation-probe-"), asked.WorkingDirectory, StringComparison.Ordinal);
        });
        Assert.Equal(
            [@"hidden\canary", @"HIDDEN\canary", @"open\sealed\w", @"open\SEALED\w", "w", @"open\w"],
            runner.Asked.Take(6).Select(asked => Path.GetRelativePath(asked.WorkingDirectory!, asked.Arguments[^1])));
        Assert.All(runner.Asked.Skip(6), asked => Assert.Same(IsolationProfile.Nothing, asked.Isolation!.Profile));
        Assert.Equal([node, git, sh], runner.Asked.Skip(6).Select(asked => asked.FileName));
        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(tree.Path, HarnessLayout.DirectoryName, HarnessLayout.RunsDirectoryName)));
    }

    /// <summary>On Windows there is no mechanism where MXC's library says the machine has no container - its words, then the remedy - and nothing is started.</summary>
    [Fact]
    public async Task OnWindows_NoContainer_IsNoMechanism_SayingWhyAndTheRemedy()
    {
        var runner = new WindowsRunner(_ => (0, "answered"));
        var platform = new IsolationPlatform(
            new SaidPlatform(PlatformId.Windows),
            runner,
            path: () => string.Empty,
            windows: new WindowsContainerHost(() => WindowsContainerSupport.None("MXC's process container is not available on this Windows: the build is too old")),
            self: Self);

        var found = await platform.ProbeAsync(Path.Combine(Path.GetTempPath(), "dssharness-mxc-tree"), Token);

        Assert.Equal(
            ((IIsolationMechanism?)null, "MXC's process container is not available on this Windows: the build is too old", "MXC's process container needs Windows 11 24H2 build 26100.9278 or later, or 26H2 build 26300.9550 or later; or run the agents from WSL 2, where bubblewrap isolates them" + OrPreferred),
            (found.Mechanism, found.Unavailable, found.Remedy));
        Assert.Empty(runner.Asked);
    }

    /// <summary>
    /// On Windows a trial that does not hold is no mechanism, saying how: the hidden canary read by its name or another case
    /// of it, the place written by nobody written by either, a place outside every open one written, or the open place not
    /// written - with how the interpreter ended - and no tool is asked after it.
    /// </summary>
    [Theory]
    [InlineData("read", "MXC 1.0.1's process container did not prove itself: its probe read the hidden canary")]
    [InlineData("case-read", "MXC 1.0.1's process container did not prove itself: its probe read the hidden canary by another case of its name")]
    [InlineData("sealed", "MXC 1.0.1's process container did not prove itself: its probe wrote the place written by nobody")]
    [InlineData("case-sealed", "MXC 1.0.1's process container did not prove itself: its probe wrote the place written by nobody by another case of its name")]
    [InlineData("outside", "MXC 1.0.1's process container did not prove itself: its probe wrote a place outside every open one")]
    [InlineData("open", "MXC 1.0.1's process container did not prove itself: its probe did not write the open place: it ended 1: 'Access is denied.'")]
    public async Task OnWindows_ATrialThatDoesNotHold_IsNoMechanism_SayingHow(string broken, string why)
    {
        using var tools = new TempDirectory();
        using var tree = new TempDirectory();
        var (path, _, _, _) = Tools(tools.Path);
        var runner = new WindowsRunner(_ => (0, "answered"), broken);

        var found = await Windows(runner, path).ProbeAsync(tree.Path, Token);

        Assert.Equal(
            ((IIsolationMechanism?)null, why, "MXC's process container needs Windows 11 24H2 build 26100.9278 or later, or 26H2 build 26300.9550 or later; or run the agents from WSL 2, where bubblewrap isolates them" + OrPreferred),
            (found.Mechanism, found.Unavailable, found.Remedy));
        Assert.All(runner.Asked, asked => Assert.Equal("cmd.exe", Path.GetFileName(asked.FileName)));
    }

    /// <summary>
    /// On Windows a tool found here that does not answer inside the container - Git Bash's sh ending with the status
    /// Windows gives a program whose libraries would not start, as MXC 1.0.1's container ends it - is no mechanism, naming
    /// the tool, where it is and how it ended; a tool not on the machine is not asked.
    /// </summary>
    [Fact]
    public async Task OnWindows_AToolThatDoesNotAnswerInside_IsNoMechanism_NamingIt_AndOneNotHereIsNotAsked()
    {
        using var tools = new TempDirectory();
        var (_, node, git, sh) = Tools(tools.Path);
        var runner = new WindowsRunner(request => Path.GetFileName(request.FileName) == "sh.exe" ? (-1073741502, string.Empty) : (0, "answered"));
        var withoutNode = string.Join(Path.PathSeparator, Path.GetDirectoryName(git)!);

        using var tree = new TempDirectory();

        var found = await Windows(runner, withoutNode).ProbeAsync(tree.Path, Token);

        Assert.Equal(
            ((IIsolationMechanism?)null,
                $"MXC 1.0.1's process container does not run Git Bash's sh ('{sh}') on this Windows: inside it, Git Bash's sh ended 0xC0000142 saying nothing",
                "update Windows and this tool, for a later MXC or Windows may run it, or run the agents from WSL 2, where bubblewrap isolates them" + OrPreferred),
            (found.Mechanism, found.Unavailable, found.Remedy));
        Assert.Equal([git, sh], runner.Asked.Skip(6).Select(asked => asked.FileName));
        Assert.DoesNotContain(runner.Asked, asked => asked.FileName == node);
    }

    /// <summary>
    /// The step itself refuses a command line it cannot run, each refusal said whole on its error stream: no program after
    /// '--' is a usage error; a program by no whole path, or any program off Windows, ends with the step's own code. The
    /// root help does not list it - nobody types it.
    /// </summary>
    [Fact]
    public async Task TheStepItself_IsRefused_WhereItsCommandLineCannotBeRun()
    {
        var empty = await CliRunner.RunAsync([MxcProcessContainer.CommandName], Token);

        Assert.Equal((HarnessExit.UsageError, $"isolate-exec was given no program after '--'.{Environment.NewLine}"), (empty.ExitCode, empty.StandardError));

        var named = await CliRunner.RunAsync([MxcProcessContainer.CommandName, MxcProcessContainer.Separator, "dotnet", "--version"], Token);

        Assert.Equal(
            (ContainExec.Failed,
                (OperatingSystem.IsWindows()
                    ? "isolate-exec was given 'dotnet', which is no whole path; the process runner names every program by its whole path."
                    : "isolate-exec starts a program inside MXC's process container, which is Windows' alone.") + Environment.NewLine,
                string.Empty),
            (named.ExitCode, named.StandardError, named.StandardOutput));

        var help = await CliRunner.RunAsync(["--help"], Token);

        Assert.DoesNotContain(MxcProcessContainer.CommandName, help.StandardOutput, StringComparison.Ordinal);
    }

    /// <summary>
    /// A program inside MXC's container for real is given the step's input, its environment and its working directory, and
    /// its output and its exit code come back to the step as the program gave them.
    /// </summary>
    [Fact]
    public async Task OnWindows_AProgramInside_HasTheStepsInputEnvironmentAndDirectory_ItsOutputAndExitCodeHandedBack_ForReal()
    {
        var version = Supported();
        using var temp = new TempDirectory();
        var isolation = new ProcessIsolation(new MxcProcessContainer(version, (CliRunner.CliProgram, [])), IsolationProfile.Nothing);

        var said = await RealIsolation.Runner().RunAsync(
            new ProcessRequest
            {
                FileName = Interpreter,
                Arguments = ["/d", "/c", "cd", "&", "echo", "%DSS_MXC_GIVEN%&", "exit", "/b", "7"],
                WorkingDirectory = temp.Path,
                Environment = new Dictionary<string, string?>(StringComparer.Ordinal) { ["DSS_MXC_GIVEN"] = "given" },
                Timeout = TimeSpan.FromSeconds(60),
                Contained = true,
                Isolation = isolation,
            },
            Token);
        var sorted = await RealIsolation.Runner().RunAsync(
            new ProcessRequest
            {
                FileName = Path.Combine(Environment.SystemDirectory, "sort.exe"),
                StandardInput = ChildInput.Of("b\r\na\r\n"),
                WorkingDirectory = temp.Path,
                Timeout = TimeSpan.FromSeconds(60),
                Contained = true,
                Isolation = isolation,
            },
            Token);

        Assert.Equal((7, $"{temp.Path}|given"), (said.ExitCode, Lines(said.StandardOutput)));
        Assert.Equal((0, "a|b"), (sorted.ExitCode, Lines(sorted.StandardOutput)));
    }

    /// <summary>
    /// The probe's decision on this machine for real, as node, git and Git Bash's sh really behave inside MXC's container
    /// here: where each found answers inside it, the probe proves the container; where one does not, the probe says there is
    /// none, naming the first that did not - so what the probe says is what the container does.
    /// </summary>
    [Fact]
    public async Task OnWindows_TheProbe_SaysWhatTheContainerRunsHere_ForReal()
    {
        var version = Supported();
        using var temp = new TempDirectory();
        var container = new MxcProcessContainer(version, (CliRunner.CliProgram, []));
        var git = Where("git.exe");
        var found = new (string Name, string? Program, string[] Arguments)[]
        {
            ("node", Where("node.exe"), ["--version"]),
            ("git", git, ["--version"]),
            ("Git Bash's sh", Where("sh.exe") ?? GitBashBeside(git), ["-c", "echo answered"]),
        };
        string? failing = null;

        foreach (var (name, program, arguments) in found.Where(tool => tool.Program is not null))
        {
            var ran = await RealIsolation.Runner().RunAsync(
                new ProcessRequest { FileName = program!, Arguments = arguments, Timeout = TimeSpan.FromSeconds(60), Contained = true, Isolation = new ProcessIsolation(container, IsolationProfile.Nothing) },
                Token);

            if (ran.ExitCode != 0 || ran.StandardOutput.Trim().Length == 0)
            {
                failing = name;
                break;
            }
        }

        var probed = await RealIsolation.Platform().ProbeAsync(temp.Path, Token);

        if (failing is null)
        {
            Assert.Equal((MxcProcessContainer.MechanismName, version), (Assert.IsType<MxcProcessContainer>(probed.Mechanism).Name, probed.Mechanism.Version));
        }
        else
        {
            Assert.Null(probed.Mechanism);
            Assert.StartsWith($"MXC {version}'s process container does not run {failing} (", probed.Unavailable, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// A command inside MXC's container in a step's launch reads no file of a credential slot and writes neither git's
    /// hooks, the tool's state nor the launch's own directory - the container refusing each - and writes its worktree.
    /// </summary>
    [Fact]
    public async Task OnWindows_ACommandInside_ReadsNoSlot_WritesNoHookStateOrLaunch_ButItsWorktree_ForReal()
    {
        var version = Supported();
        using var temp = new TempDirectory();
        using var home = new TempDirectory();
        var platform = new FixedIsolationPlatform(IsolationAvailability.Of(new MxcProcessContainer(version, (CliRunner.CliProgram, []))));
        var launch = await RealIsolation.StepLaunchAsync(temp, home, platform);
        var hook = Path.Combine(launch.Kit.Main, ".git", "hooks", "pre-commit");
        var state = Path.Combine(launch.Kit.Main, HarnessLayout.RunsDirectoryRelative.Replace('/', Path.DirectorySeparatorChar), "planted");
        var launched = Path.Combine(launch.Directory, "planted");
        var made = Path.Combine(launch.Worktree, "made.txt");

        var read = await RealIsolation.InsideAsync(launch.Commands, launch.Worktree, Interpreter, "/d", "/c", "type", launch.Canary);
        var wrote = new List<(string Place, ProcessResult Result)>();

        foreach (var place in new[] { hook, state, launched })
        {
            wrote.Add((place, await RealIsolation.InsideAsync(launch.Commands, launch.Worktree, Interpreter, "/d", "/c", "type", "nul", ">", place)));
        }

        var worked = await RealIsolation.InsideAsync(launch.Commands, launch.Worktree, Interpreter, "/d", "/c", "echo", "made", ">", made);

        Assert.NotEqual(0, read.ExitCode);
        Assert.DoesNotContain("secret", read.StandardOutput, StringComparison.Ordinal);

        foreach (var (place, result) in wrote)
        {
            Assert.NotEqual(0, result.ExitCode);
            Assert.False(File.Exists(place), place);
        }

        Assert.Equal((0, "made"), (worked.ExitCode, (await File.ReadAllTextAsync(made, Token)).Trim()));
    }

    /// <summary>
    /// A command inside MXC's container over a side-task's main tree, which it writes, moves neither the tree's git
    /// directory, whose hooks it may not write, nor the directory that holds the credential slots - MXC meets a place by its
    /// path, and a directory moved away would carry its guarded places out from under their rules.
    /// </summary>
    [Fact]
    public async Task OnWindows_ACommandInside_MovesNoDirectoryAboveAGuardedPlace_ForReal()
    {
        var version = Supported();
        using var temp = new TempDirectory();
        using var home = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var container = new MxcProcessContainer(version, (CliRunner.CliProgram, []));
        var isolation = new AgentIsolation(new FixedIsolationPlatform(IsolationAvailability.Of(container)), kit.Harness.GitClient, kit.Harness.FileSystem, new HomedPlatform(home.Path), _ => null);
        var config = new HarnessConfig { Worktrees = OrchestrationKit.Settings, AgentPolicy = new AgentPolicyConfig { Isolation = AgentIsolationMode.Required } };
        var layout = new HarnessLayout(kit.Main, kit.Main);
        var launched = await isolation.ForLaunchAsync(config, SideTaskPolicy.Ground(layout, config, []), kit.Main, SideTaskLayout.Of(layout, "one").Directory, Token);
        var commands = Assert.IsType<ProcessIsolation>(launched.ForCommands);

        foreach (var anchor in new[] { Path.Combine(kit.Main, ".git"), Path.Combine(kit.Main, HarnessLayout.DirectoryName) })
        {
            var moved = await RealIsolation.InsideAsync(commands, kit.Main, Interpreter, "/d", "/c", "move", anchor, anchor + "-moved");

            Assert.True(Directory.Exists(anchor), anchor);
            Assert.False(Directory.Exists(anchor + "-moved"), anchor);
            Assert.NotEqual(0, moved.ExitCode);
        }
    }

    /// <summary>The system's command interpreter.</summary>
    private static string Interpreter => Path.Combine(Environment.SystemDirectory, "cmd.exe");

    /// <summary>This tool's program, as a test gives it.</summary>
    private static (string Program, IReadOnlyList<string> Prefix) Self() => (@"C:\tools\dssharness.exe", []);

    /// <summary>A Windows machine as a test gives it: MXC 1.0.1's container there, the tools on <paramref name="path"/>.</summary>
    private static IsolationPlatform Windows(WindowsRunner runner, string path)
        => new(
            new SaidPlatform(PlatformId.Windows),
            runner,
            path: () => path,
            windows: new WindowsContainerHost(() => new WindowsContainerSupport("1.0.1", null)),
            self: Self);

    /// <summary>
    /// node on a PATH of its own, and git in a Git for Windows layout - <c>cmd\git.exe</c>, its sh at <c>usr\bin\sh.exe</c>
    /// - each a file under <paramref name="root"/>: the PATH that finds them, and where each is.
    /// </summary>
    private static (string Path, string Node, string Git, string Sh) Tools(string root)
    {
        var node = Made(Path.Combine(root, "nodejs", "node.exe"));
        var git = Made(Path.Combine(root, "Git", "cmd", "git.exe"));
        var sh = Made(Path.Combine(root, "Git", "usr", "bin", "sh.exe"));

        return (string.Join(Path.PathSeparator, Path.GetDirectoryName(node), Path.GetDirectoryName(git)), node, git, sh);

        static string Made(string file)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, string.Empty);
            return file;
        }
    }

    /// <summary>
    /// MXC's version on this machine, its container there: the test skipped where this is not Windows, or where MXC's
    /// library says the machine has no container - never failed, for no runner image is meant to hold it.
    /// </summary>
    private static string Supported()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "MXC's process container isolates an agent's commands on Windows alone.");

        var support = WindowsContainerHost.ThisMachine.Support();

        Assert.SkipWhen(support.Unavailable is not null, $"This machine has no container of MXC's: {support.Unavailable}.");

        return support.Version!;
    }

    /// <summary>Each line <paramref name="output"/> holds, none blank, joined by a bar.</summary>
    private static string Lines(string output) => string.Join('|', output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    /// <summary>The first <paramref name="name"/> on this process's PATH; null where none is.</summary>
    private static string? Where(string name)
        => (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(Path.IsPathFullyQualified)
            .Select(directory => Path.Combine(directory, name))
            .FirstOrDefault(File.Exists);

    /// <summary>Git Bash's sh beside <paramref name="git"/>, as Git for Windows lays it out; null where there is none.</summary>
    private static string? GitBashBeside(string? git)
        => git is null || Path.GetDirectoryName(Path.GetDirectoryName(git)) is not { } root
            ? null
            : new[] { Path.Combine(root, "usr", "bin", "sh.exe"), Path.Combine(root, "bin", "sh.exe") }.FirstOrDefault(File.Exists);

    /// <summary>
    /// A process runner standing in for Windows inside MXC's container: the interpreter's trial runs as the container
    /// keeps them - the canary unread by either name, the place written by nobody unwritten by either, the place outside
    /// unwritten, the open place written - but the one <paramref name="broken"/> names, and every other program answering
    /// as <paramref name="answer"/> says.
    /// </summary>
    private sealed class WindowsRunner(Func<ProcessRequest, (int Code, string Said)> answer, string? broken = null) : IProcessRunner
    {
        public List<ProcessRequest> Asked { get; } = [];

        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
        {
            Asked.Add(request);

            if (Path.GetFileName(request.FileName) != "cmd.exe")
            {
                var (code, said) = answer(request);
                return Task.FromResult(new ProcessResult(code, said, string.Empty, TimeSpan.Zero, TimedOut: false));
            }

            if (request.Arguments is [_, _, "type", var read])
            {
                var otherCase = Path.GetFileName(Path.GetDirectoryName(read)) == "HIDDEN";

                return Task.FromResult(broken == (otherCase ? "case-read" : "read") ? Ended(0, string.Empty) : Ended(1, "Access is denied."));
            }

            var target = request.Arguments[^1];
            var place = Path.GetFileName(Path.GetDirectoryName(target));

            if ((place == "open" && broken != "open")
                || (place == "sealed" && broken == "sealed")
                || (place == "SEALED" && broken == "case-sealed")
                || (place!.StartsWith("isolation-probe-", StringComparison.Ordinal) && broken == "outside"))
            {
                if (place == "SEALED")
                {
                    return Task.FromResult(Ended(0, string.Empty));
                }

                File.WriteAllText(target, string.Empty);
                return Task.FromResult(Ended(0, string.Empty));
            }

            return Task.FromResult(Ended(1, "Access is denied."));
        }

        public string? FindExecutable(string command) => null;

        private static ProcessResult Ended(int code, string error) => new(code, string.Empty, error, TimeSpan.Zero, TimedOut: false);
    }
}
