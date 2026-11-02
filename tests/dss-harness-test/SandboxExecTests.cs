using DssHarness.Core.Platform;
using DssHarness.Core.Processes;
using DssHarness.Core.Repository;

namespace DssHarness.Tests;

/// <summary>
/// macOS's sandbox: the command line and the whole profile it is handed - its places at their real spellings, as
/// parameters - and every decision of its probe, each from doubles of the machine on every system; then the system's
/// sandbox run for real on macOS: the probe proves it once for the process, and a step's launch over a real repository
/// keeps a command inside from a credential slot's file, git's hooks, the tool's state, the launch's own directory and
/// every directory above them, while it writes its worktree and git stages there. The real tests are skipped where this
/// is not macOS, and where the sandbox does not prove itself - failed instead where the machine says it holds every
/// build tool, as the continuous integration runners do.
/// </summary>
public sealed class SandboxExecTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// The sandbox is handed the whole profile: every operation denied, what a build, a test and a shell do as the user
    /// allowed - every file read, no file written but the devices a shell writes - then the profile's own rules laid last:
    /// the launch's temporary directory written and its Unix sockets reached, each write rule in its order, each hidden
    /// place, each place it reopens, each anchor - a directory above a guarded place whose holder is written - kept from
    /// removal, and each Unix socket it reaches outside, every place at its real spelling, handed over as a parameter; then the profile's variables set by
    /// env inside, and the program's own words.
    /// </summary>
    [Fact]
    public void TheSandbox_IsHandedTheWholeProfile_ItsPlacesAtTheirRealSpellings()
    {
        var mechanism = new SandboxExec("15.3", Real);
        var profile = new IsolationProfile(
            [new IsolatedPath("/var/r/.harness-config/sshItems", Directory: true), new IsolatedPath("/var/home/.claude", Directory: true), new IsolatedPath("/var/r/.worktrees/a1/.env", Directory: false)],
            [
                new IsolatedWrite("/var/r/.git", Directory: true, Writable: true),
                new IsolatedWrite("/var/r/.worktrees/a1", Directory: true, Writable: true),
                new IsolatedWrite("/var/r/.git/hooks", Directory: true, Writable: false),
                new IsolatedWrite("/var/r/.worktrees/a1/.harness-config", Directory: true, Writable: false),
                new IsolatedWrite("/var/r/.worktrees/a1/.git", Directory: false, Writable: false),
                new IsolatedWrite("/var/r/.git/worktrees/a1/gitdir", Directory: false, Writable: false),
            ])
        {
            Reopened = ["/var/home/.claude/shell-snapshots"],
            Temp = "/var/t/x",
            Environment = [new("TMPDIR", "/private/var/t/x"), new("npm_config_cache", "/private/var/c/npm")],
            Sockets = ["/var/run/agent.sock"],
        };

        var (program, arguments) = mechanism.Command(profile, "/usr/bin/git", ["add", "a.txt"]);

        Assert.Equal(("/usr/bin/sandbox-exec", "sandbox-exec", "15.3"), (program, mechanism.Name, mechanism.Version));
        Assert.Equal(
            (true, false, (string?)null, (string?)null, false, true),
            (mechanism.HoldsEveryCase, mechanism.Scoped, mechanism.Leaves, mechanism.AnchorCost, mechanism.OwnTemp, mechanism.TakesHandedCommands));
        Assert.Equal(
            [
                "-p",
                """
                (version 1)
                (deny default)
                (allow process-exec)
                (allow process-fork)
                (allow signal (target same-sandbox))
                (allow process-info* (target same-sandbox))
                (allow sysctl-read
                  (sysctl-name "hw.activecpu")
                  (sysctl-name "hw.byteorder")
                  (sysctl-name "hw.cachelinesize_compat")
                  (sysctl-name "hw.cpufamily")
                  (sysctl-name "hw.cputype")
                  (sysctl-name "hw.l1dcachesize_compat")
                  (sysctl-name "hw.l2cachesize_compat")
                  (sysctl-name "hw.logicalcpu")
                  (sysctl-name "hw.logicalcpu_max")
                  (sysctl-name "hw.machine")
                  (sysctl-name "hw.memsize")
                  (sysctl-name "hw.model")
                  (sysctl-name "hw.ncpu")
                  (sysctl-name "hw.nperflevels")
                  (sysctl-name "hw.pagesize")
                  (sysctl-name "hw.pagesize_compat")
                  (sysctl-name "hw.physicalcpu")
                  (sysctl-name "hw.physicalcpu_max")
                  (sysctl-name "hw.tbfrequency_compat")
                  (sysctl-name "hw.vectorunit")
                  (sysctl-name "kern.argmax")
                  (sysctl-name "kern.hostname")
                  (sysctl-name "kern.maxfilesperproc")
                  (sysctl-name "kern.maxproc")
                  (sysctl-name "kern.osproductversion")
                  (sysctl-name "kern.osrelease")
                  (sysctl-name "kern.ostype")
                  (sysctl-name "kern.osvariant_status")
                  (sysctl-name "kern.osversion")
                  (sysctl-name "kern.secure_kernel")
                  (sysctl-name "kern.sysv.semmns")
                  (sysctl-name "kern.usrstack64")
                  (sysctl-name "kern.version")
                  (sysctl-name "machdep.cpu.brand_string")
                  (sysctl-name "sysctl.proc_cputype")
                  (sysctl-name "vm.loadavg")
                  (sysctl-name-prefix "hw.optional.")
                  (sysctl-name-prefix "hw.perflevel")
                  (sysctl-name-prefix "kern.proc.pgrp.")
                  (sysctl-name-prefix "kern.proc.pid.")
                  (sysctl-name-prefix "net.routetable."))
                (allow sysctl-write (sysctl-name "kern.grade_cputype"))
                (allow iokit-open (iokit-registry-entry-class "RootDomainUserClient"))
                (allow ipc-posix-sem)
                (allow ipc-posix-shm-read* ipc-posix-shm-write-create ipc-posix-shm-write-data ipc-posix-shm-write-unlink)
                (allow pseudo-tty)
                (allow file-ioctl (literal "/dev/ptmx") (regex #"^/dev/ttys[0-9]+"))
                (allow user-preference-read)
                (allow system-socket (require-all (socket-domain AF_SYSTEM) (socket-protocol 2)))
                (allow network-outbound (remote ip "*:*"))
                (allow network-inbound (local ip "*:*"))
                (allow network-bind (local ip "*:*"))
                (allow network-outbound (remote unix-socket (subpath "/private/var/run/mDNSResponder")))
                (allow mach-lookup
                  (global-name "com.apple.system.opendirectoryd.libinfo")
                  (global-name "com.apple.system.opendirectoryd.membership")
                  (global-name "com.apple.system.notification_center")
                  (global-name "com.apple.bsd.dirhelper")
                  (global-name "com.apple.PowerManagement.control")
                  (global-name "com.apple.FSEvents")
                  (global-name "com.apple.SystemConfiguration.DNSConfiguration")
                  (global-name "com.apple.SystemConfiguration.configd")
                  (global-name "com.apple.networkd")
                  (global-name "com.apple.SecurityServer")
                  (global-name "com.apple.ocspd")
                  (global-name "com.apple.trustd.agent")
                  (global-name "com.apple.TrustEvaluationAgent")
                  (global-name "com.apple.cfprefsd.daemon")
                  (global-name "com.apple.cfprefsd.agent")
                  (local-name "com.apple.cfprefsd.agent"))
                (deny mach-lookup (xpc-service-name-prefix ""))
                (allow file-read*)
                (allow file-write* (literal "/dev/null") (literal "/dev/zero") (literal "/dev/tty") (literal "/dev/dtracehelper") (literal "/dev/ptmx") (regex #"^/dev/fd/[0-9]+$") (regex #"^/dev/ttys[0-9]+$"))
                (allow file-write* (subpath (param "TEMP")))
                (allow network-bind (local unix-socket (subpath (param "TEMP"))))
                (allow network-outbound (remote unix-socket (subpath (param "TEMP"))))
                (allow file-write* (subpath (param "WRITE_0")))
                (allow file-write* (subpath (param "WRITE_1")))
                (deny file-write* (subpath (param "WRITE_2")))
                (deny file-write* (subpath (param "WRITE_3")))
                (deny file-write* (literal (param "WRITE_4")))
                (deny file-write* (literal (param "WRITE_5")))
                (deny file-read* file-write* (subpath (param "HIDDEN_0")))
                (deny file-read* file-write* (subpath (param "HIDDEN_1")))
                (deny file-read* file-write* (literal (param "HIDDEN_2")))
                (allow file-read* (subpath (param "REOPENED_0")))
                (deny file-write-unlink (require-all (vnode-type DIRECTORY) (literal (param "ANCHOR_0"))))
                (deny file-write-unlink (require-all (vnode-type DIRECTORY) (literal (param "ANCHOR_1"))))
                (allow network-outbound (remote unix-socket (literal (param "SOCKET_0"))))
                (deny system-fcntl (fcntl-command 80 110))

                """.ReplaceLineEndings("\n"),
                "-DTEMP=/private/var/t/x",
                "-DWRITE_0=/private/var/r/.git",
                "-DWRITE_1=/private/var/r/.worktrees/a1",
                "-DWRITE_2=/private/var/shared/hooks",
                "-DWRITE_3=/private/var/r/.worktrees/a1/.harness-config",
                "-DWRITE_4=/private/var/r/.worktrees/a1/.git",
                "-DWRITE_5=/private/var/r/.git/worktrees/a1/gitdir",
                "-DHIDDEN_0=/private/var/r/.harness-config/sshItems",
                "-DHIDDEN_1=/private/var/home/.claude",
                "-DHIDDEN_2=/private/var/r/.worktrees/a1/.env",
                "-DREOPENED_0=/private/var/home/.claude/shell-snapshots",
                "-DANCHOR_0=/private/var/r/.git/worktrees",
                "-DANCHOR_1=/private/var/r/.git/worktrees/a1",
                "-DSOCKET_0=/private/var/run/agent.sock",
                "--", "/usr/bin/env", "TMPDIR=/private/var/t/x", "npm_config_cache=/private/var/c/npm", "/usr/bin/git", "add", "a.txt",
            ],
            arguments);
        Assert.Equal(["--", "/bin/sh"], mechanism.Command(new IsolationProfile([], []), "/bin/sh", []).Arguments.TakeLast(2));

        // The real spellings as a Mac gives them: /var is a link to /private/var, and the repository's hooks a link to a
        // shared directory.
        static string Real(string path)
            => path == "/var/r/.git/hooks" ? "/private/var/shared/hooks"
                : path == "/var" || path.StartsWith("/var/", StringComparison.Ordinal) ? "/private" + path
                : path;
    }

    /// <summary>
    /// A file a child writes alone - the one a handed command ends by writing its working directory to, in a place written by
    /// nobody - is written in place: its data alone allowed, its removal and its making - a rename onto it among them -
    /// refused after, so nothing is put in its stead, a link among it.
    /// </summary>
    [Fact]
    public void AFileWrittenAlone_IsWrittenInPlace_NeverRemoved()
    {
        var mechanism = new SandboxExec("15.3", path => path);
        var profile = new IsolationProfile(
            [],
            [new IsolatedWrite("/r/s1", Directory: true, Writable: false), new IsolatedWrite("/r/s1/claude-tmp/claude-1a2b-cwd", Directory: false, Writable: true)]);

        var (text, parameters) = mechanism.Policy(profile);

        Assert.Equal(
            [
                "(deny file-write* (subpath (param \"WRITE_0\")))",
                "(allow file-write-data (literal (param \"WRITE_1\")))",
                "(deny file-write-unlink file-write-create (literal (param \"WRITE_1\")))",
                "(deny system-fcntl (fcntl-command 80 110))",
            ],
            text.TrimEnd('\n').Split('\n').TakeLast(4));
        Assert.Equal([("WRITE_0", "/r/s1"), ("WRITE_1", "/r/s1/claude-tmp/claude-1a2b-cwd")], parameters);
    }

    /// <summary>
    /// On macOS the probe proves the system's sandbox once for the process: its program at the one path it is taken from,
    /// this process inside no sandbox, and a trial run of the probe's command under the tree's runs directory - contained,
    /// inside the sandbox, a hidden place, an open place and a place written by nobody inside it in its profile, the
    /// canary and the sealed place each named again in another case and a file outside every open place - that holds;
    /// every later asker gets the same mechanism, and the probe's places are gone.
    /// </summary>
    [Fact]
    public async Task OnMacOs_TheProbe_ProvesTheSystemsSandbox_OnceForTheProcess()
    {
        using var tree = new TempDirectory();
        var runner = new ProbeRunner(Holding);
        var platform = new IsolationPlatform(new SaidPlatform(PlatformId.MacOs), runner, mac: Mac());

        var proved = await platform.ProbeAsync(tree.Path, Token);
        var again = await platform.ProbeAsync(tree.Path, Token);

        var sandbox = Assert.IsType<SandboxExec>(proved.Mechanism);
        var asked = Assert.Single(runner.Asked);

        Assert.Equal((SandboxExec.MechanismName, "15.3", (string?)null, (string?)null), (sandbox.Name, sandbox.Version, proved.Unavailable, proved.Remedy));
        Assert.Same(sandbox, again.Mechanism);
        Assert.Equal(("/bin/sh", true), (asked.FileName, asked.Contained));
        Assert.Same(sandbox, asked.Isolation!.Mechanism);
        Assert.Equal(["hidden"], asked.Isolation.Profile.Hidden.Select(place => Path.GetFileName(place.Path)));
        Assert.Equal([("open", true), ("sealed", false)], asked.Isolation.Profile.Writes.Select(write => (Path.GetFileName(write.Path), write.Writable)));
        Assert.StartsWith(Path.Combine(tree.Path, HarnessLayout.DirectoryName, HarnessLayout.RunsDirectoryName, "isolation-probe-"), asked.WorkingDirectory, StringComparison.Ordinal);
        Assert.Equal(
            [Path.Combine("hidden", "canary"), Path.Combine("HIDDEN", "canary"), Path.Combine("open", "sealed"), Path.Combine("open", "SEALED"), "open", "w"],
            asked.Arguments.Skip(3).Select(argument => Path.GetRelativePath(asked.WorkingDirectory!, argument)));
        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(tree.Path, HarnessLayout.DirectoryName, HarnessLayout.RunsDirectoryName)));
    }

    /// <summary>
    /// On macOS there is no sandbox where its program is not at its one path, nor where this process already runs inside a
    /// sandbox, which macOS starts no other inside - each said with what would give one, and nothing started.
    /// </summary>
    [Theory]
    [InlineData(false, false, "macOS's sandbox ('/usr/bin/sandbox-exec') is not on this machine", "set agentPolicy.isolation to \"preferred\" to run agents on the words policy alone, said at every launch")]
    [InlineData(true, true, "this process already runs inside a sandbox of macOS's, which starts no sandbox inside another", "run this tool outside every sandbox - not from inside another agent's or an application's, or set agentPolicy.isolation to \"preferred\" to run agents on the words policy alone, said at every launch")]
    public async Task OnMacOs_NoProgramOrASandboxAlready_IsNoMechanism_SayingWhyAndTheRemedy(bool there, bool inside, string why, string remedy)
    {
        var runner = new ProbeRunner(Holding);
        var platform = new IsolationPlatform(new SaidPlatform(PlatformId.MacOs), runner, mac: Mac(there, inside));

        var found = await platform.ProbeAsync(Path.Combine(Path.GetTempPath(), "dssharness-mac-tree"), Token);

        Assert.Equal(((IIsolationMechanism?)null, why, remedy), (found.Mechanism, found.Unavailable, found.Remedy));
        Assert.Empty(runner.Asked);
    }

    /// <summary>
    /// On macOS a trial run that does not hold is no mechanism, saying how it failed - its exit and the first line it said,
    /// or the places outside not holding what it said - with the remedy.
    /// </summary>
    [Fact]
    public async Task OnMacOs_ATrialThatDoesNotHold_IsNoMechanism_SayingHow()
    {
        const string Remedy = "check that the sandbox runs here ('/usr/bin/sandbox-exec -p \"(version 1)(allow default)\" /usr/bin/true'), or set agentPolicy.isolation to \"preferred\" to run agents on the words policy alone, said at every launch";

        using var tree = new TempDirectory();

        var read = await new IsolationPlatform(new SaidPlatform(PlatformId.MacOs), new ProbeRunner(_ => new ProcessResult(3, "the hidden canary was read\n", string.Empty, TimeSpan.Zero, TimedOut: false)), mac: Mac())
            .ProbeAsync(tree.Path, Token);
        var leaked = await new IsolationPlatform(new SaidPlatform(PlatformId.MacOs), new ProbeRunner(Leaking), mac: Mac())
            .ProbeAsync(tree.Path, Token);

        Assert.Equal(
            ((IIsolationMechanism?)null, "macOS 15.3's sandbox ('/usr/bin/sandbox-exec') did not prove itself: its probe ended 3: 'the hidden canary was read'", Remedy),
            (read.Mechanism, read.Unavailable, read.Remedy));
        Assert.Equal(
            ((IIsolationMechanism?)null, "macOS 15.3's sandbox ('/usr/bin/sandbox-exec') did not prove itself: its probe ended 0, and the places outside do not hold what it said", Remedy),
            (leaked.Mechanism, leaked.Unavailable, leaked.Remedy));
    }

    /// <summary>The probe proves the system's sandbox for real - its own name, the version of macOS - and answers every later asker from the one probe.</summary>
    [Fact]
    public async Task TheProbe_ProvesTheSystemsSandbox_ForReal()
    {
        using var temp = new TempDirectory();
        var platform = RealIsolation.Platform();

        var proved = await ProvedAsync(platform, temp.Path);
        var again = await platform.ProbeAsync(temp.Path, Token);

        var sandbox = Assert.IsType<SandboxExec>(proved);

        Assert.Equal((SandboxExec.MechanismName, Environment.OSVersion.Version.ToString()), (sandbox.Name, sandbox.Version));
        Assert.Same(proved, again.Mechanism);
    }

    /// <summary>
    /// A command inside a step's launch reads no file of a credential slot and writes neither git's hooks, the tool's state
    /// nor the launch's own directory, the sandbox refusing each; it writes its worktree, and git stages that file into the
    /// repository's index from inside.
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
        Assert.Contains("Operation not permitted", read.StandardError, StringComparison.Ordinal);

        foreach (var (place, writing) in wrote)
        {
            var result = await writing;

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("Operation not permitted", result.StandardError, StringComparison.Ordinal);
            Assert.False(File.Exists(place), place);
        }

        Assert.Equal((0, "made"), (worked.ExitCode, await File.ReadAllTextAsync(made, Token)));
        Assert.Equal(0, staged.ExitCode);
        Assert.Equal(["made.txt"], (await launch.Kit.Harness.GitClient.RunAsync(launch.Worktree, ["diff", "--cached", "--name-only"], cancellationToken: Token)).OutputLines);
    }

    /// <summary>
    /// A command inside moves no directory above a guarded place - neither the repository's git directory, whose hooks it
    /// may not write, nor the directory that holds the credential slots - for the sandbox meets a place by its path, and a
    /// place moved away would leave its rule behind and its name free to make again.
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
            Assert.Contains("Operation not permitted", moved.StandardError, StringComparison.Ordinal);
            Assert.True(Directory.Exists(anchor), anchor);
            Assert.False(Directory.Exists(anchor + "-moved"), anchor);
        }
    }

    /// <summary>
    /// A command handed over writes the one file it ends by writing its working directory to in place - its data alone - and
    /// nothing removes it, puts a link in its stead, by its name or by a rename onto it, or writes beside it in Claude Code's
    /// own temporary directory.
    /// </summary>
    [Fact]
    public async Task ACommandHandedOver_WritesItsEndFileInPlace_AndNothingElseThere()
    {
        using var temp = new TempDirectory();
        using var home = new TempDirectory();
        var platform = RealIsolation.Platform();
        await ProvedAsync(platform, temp.Path);

        await RealIsolation.EndFileWrittenInPlaceAsync(await RealIsolation.StepLaunchAsync(temp, home, platform));
    }

    /// <summary>The sandbox the probe proved: the test skipped where this is not macOS.</summary>
    private static Task<IIsolationMechanism> ProvedAsync(IsolationPlatform platform, string tree)
        => RealIsolation.ProvedAsync(platform, tree, OperatingSystem.IsMacOS(), "macOS's sandbox isolates an agent's commands on macOS alone.", "sandbox of macOS's");

    /// <summary>A Mac as a test gives it: the program there or not, this process inside a sandbox or not, macOS 15.3, every path its own real spelling.</summary>
    private static MacSandboxHost Mac(bool there = true, bool inside = false)
        => new(path => there && path == SandboxExec.ProgramPath, () => inside, () => "15.3", path => path);

    /// <summary>A trial run inside a sandbox that holds: the open place written, the others left as they were.</summary>
    private static ProcessResult Holding(ProcessRequest request)
    {
        File.WriteAllText(Path.Combine(request.Arguments[^2], "w"), string.Empty);

        return new ProcessResult(0, string.Empty, string.Empty, TimeSpan.Zero, TimedOut: false);
    }

    /// <summary>A trial run that says all is well and wrote the place written by nobody.</summary>
    private static ProcessResult Leaking(ProcessRequest request)
    {
        File.WriteAllText(Path.Combine(request.Arguments[^4], "w"), string.Empty);
        File.WriteAllText(Path.Combine(request.Arguments[^2], "w"), string.Empty);

        return new ProcessResult(0, string.Empty, string.Empty, TimeSpan.Zero, TimedOut: false);
    }

    /// <summary>A process runner that answers each request as a test says, keeping every request it was asked.</summary>
    private sealed class ProbeRunner(Func<ProcessRequest, ProcessResult> answer) : IProcessRunner
    {
        public List<ProcessRequest> Asked { get; } = [];

        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
        {
            Asked.Add(request);
            return Task.FromResult(answer(request));
        }

        public string? FindExecutable(string command) => null;
    }
}
