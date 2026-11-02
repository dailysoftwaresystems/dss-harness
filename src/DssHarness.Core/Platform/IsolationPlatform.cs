using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Processes;
using DssHarness.Core.Repository;

namespace DssHarness.Core.Platform;

/// <summary>
/// Whether this machine isolates a contained child: the mechanism found and proved, or why there is none and what would
/// give one.
/// </summary>
/// <param name="Mechanism">The mechanism, proved by the probe; null where there is none.</param>
/// <param name="Unavailable">Why there is none, as the opening of a sentence; null where there is one.</param>
/// <param name="Remedy">What would give one, where there is none; null where there is one.</param>
public sealed record IsolationAvailability(IIsolationMechanism? Mechanism, string? Unavailable, string? Remedy)
{
    /// <summary>The mechanism found and proved.</summary>
    /// <param name="mechanism">The mechanism.</param>
    public static IsolationAvailability Of(IIsolationMechanism mechanism)
        => new(mechanism ?? throw new ArgumentNullException(nameof(mechanism)), null, null);

    /// <summary>No mechanism, and why.</summary>
    /// <param name="why">Why there is none, as the opening of a sentence.</param>
    /// <param name="remedy">What would give one.</param>
    public static IsolationAvailability None(string why, string remedy)
        => new(null, why ?? throw new ArgumentNullException(nameof(why)), remedy ?? throw new ArgumentNullException(nameof(remedy)));
}

/// <summary>
/// The platform's seam for the operating system's isolation of an agent's commands: the mechanism this machine has,
/// found and proved by a probe once per process - through the real mechanism, on the tree's own file system: a canary in a
/// hidden place unread, by its own name and by another case of it, a place written by nobody unwritten by either, a write
/// in an open place done and one outside every open place refused - or why there is none.
/// </summary>
public interface IIsolationPlatform
{
    /// <summary>
    /// The mechanism, proved, or why there is none: the first call probes, and every later one answers what it found.
    /// </summary>
    /// <param name="tree">The tree the process works over: the probe's places stand on its file system, and a program found on the PATH inside it is never the mechanism.</param>
    /// <param name="cancellationToken">Stops the probe.</param>
    Task<IsolationAvailability> ProbeAsync(string tree, CancellationToken cancellationToken);
}

/// <summary>
/// The isolation this machine has, proved by the probe through the process runner's contained step, as every isolated
/// child is started. On Linux, WSL's included, the system's bubblewrap 0.8 or newer - the first <c>bwrap</c> in an
/// absolute directory of the PATH outside the tree, where Ubuntu's AppArmor profile for it attaches, refused where the user
/// could write the program or a directory above it, for the tool runs it outside before anything is isolated - with
/// Landlock's scope of abstract sockets and signals where the kernel has it. On macOS the system's own sandbox, <c>/usr/bin/sandbox-exec</c>, where this process is not already inside a sandbox, which macOS
/// would not start another inside. On Windows MXC's process container, where MXC's library says the machine has one that
/// denies a place outright, proved by the same canary trial and then by each of node, git and Git Bash's sh found here
/// started inside it and answering - the programs an agent's commands are made of, which a container that cannot run
/// them would leave nothing to run.
/// </summary>
/// <param name="platform">This machine.</param>
/// <param name="runner">Starts the version's question and the probe, the probe as a contained child is started.</param>
/// <param name="path">Reads the PATH bubblewrap, and on Windows node, git and Git Bash's sh, are looked for on; this process's own where left out.</param>
/// <param name="mac">What finds macOS's sandbox on this machine; this machine's own where left out.</param>
/// <param name="windows">What finds MXC's process container on this machine; this machine's own where left out.</param>
/// <param name="self">The program that is this tool, as Windows' mechanism starts it again; this process's own where left out.</param>
/// <param name="userWrites">Whether this user may write a place - a file, or a directory's entries; the system's own answer where left out.</param>
/// <param name="landlock">The kernel's version of Landlock; the kernel's own answer where left out.</param>
public sealed partial class IsolationPlatform(
    IHostPlatform platform,
    IProcessRunner runner,
    Func<string?>? path = null,
    MacSandboxHost? mac = null,
    WindowsContainerHost? windows = null,
    Func<(string Program, IReadOnlyList<string> Prefix)>? self = null,
    Func<string, bool>? userWrites = null,
    Func<int>? landlock = null) : IIsolationPlatform
{
    private readonly MacSandboxHost _mac = mac ?? MacSandboxHost.ThisMachine;
    private readonly WindowsContainerHost _windows = windows ?? WindowsContainerHost.ThisMachine;
    private readonly Func<(string Program, IReadOnlyList<string> Prefix)> _self = self ?? DetachedProcessLauncher.Self;
    private readonly Func<string, bool> _userWrites = userWrites ?? UserWrites;
    private readonly Func<int> _landlock = landlock ?? Landlock.Version;

    /// <summary>The program's name.</summary>
    public const string BubblewrapProgram = "bwrap";

    /// <summary>The sysctl Ubuntu restricts unprivileged user namespaces by, which bubblewrap needs.</summary>
    public const string RestrictedUserNamespaces = "/proc/sys/kernel/apparmor_restrict_unprivileged_userns";

    /// <summary>
    /// The container engines' sockets, each of which starts a container with the machine bound into it outside every
    /// mount: hidden from every child where they are there, by their real place.
    /// </summary>
    public static IReadOnlyList<string> ContainerSockets { get; } =
    [
        "/run/docker.sock", "/var/run/docker.sock", "/run/podman/podman.sock", "/run/containerd/containerd.sock",
        "/run/buildkit/buildkitd.sock", "/run/crio/crio.sock",
    ];

    /// <summary>The directories of the machine's own escapes, hidden whole where they are there: WSL's interoperation sockets and its shared directory, and screen's sessions.</summary>
    public static IReadOnlyList<string> EscapeDirectories { get; } = ["/run/WSL", "/mnt/wsl", "/run/screen"];

    /// <summary>
    /// What the probe's command says and ends with where everything was as it must be: <c>$1</c> the canary, <c>$2</c> it
    /// by another case of its directory's name, <c>$3</c> the place written by nobody, <c>$4</c> it by another case,
    /// <c>$5</c> the open place, <c>$6</c> a file outside every open place. Each write is <c>true</c>'s, a regular command,
    /// never <c>:</c>'s: a redirection that fails on a special builtin ends a POSIX shell - dash, and bash as macOS's
    /// <c>sh</c> - with 2, before it could say which place held.
    /// </summary>
    private const string ProbeScript =
        "if cat \"$1\" >/dev/null 2>&1; then echo 'the hidden canary was read'; exit 3; fi; "
        + "if cat \"$2\" >/dev/null 2>&1; then echo 'the hidden canary was read by another case of its name'; exit 4; fi; "
        + "if true 2>/dev/null > \"$3/w\"; then echo 'the sealed place was written'; exit 5; fi; "
        + "if true 2>/dev/null > \"$4/w\"; then echo 'the sealed place was written by another case of its name'; exit 6; fi; "
        + "if true 2>/dev/null > \"$6\"; then echo 'a place outside every open one was written'; exit 7; fi; "
        + "true > \"$5/w\" || { echo 'the open place was not written'; exit 8; }";

    /// <summary>What a probe that failed by another case of a name says, which its remedy is told by.</summary>
    private const string AnotherCase = "another case";

    /// <summary>How long the version's question and the probe may take.</summary>
    private static readonly TimeSpan ProbeBound = TimeSpan.FromSeconds(30);

    private readonly Func<string?> _path = path ?? (() => Environment.GetEnvironmentVariable("PATH"));
    private readonly Lock _gate = new();
    private Task<IsolationAvailability>? _probed;

    /// <summary>What every remedy of a machine with no mechanism ends with: the configuration's own way past it.</summary>
    public const string PreferredRemedy = "set agentPolicy.isolation to \"preferred\" to run agents on the words policy alone, said at every launch";

    /// <summary><see cref="PreferredRemedy"/> as the last of more than one remedy.</summary>
    private const string OrPreferred = ", or " + PreferredRemedy;

    /// <inheritdoc/>
    public Task<IsolationAvailability> ProbeAsync(string tree, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tree);

        lock (_gate)
        {
            // Once per process: a probe that was stopped is no answer, and the next asker probes again.
            if (_probed is null || _probed.IsCanceled || _probed.IsFaulted)
            {
                _probed = ProbedAsync(tree, cancellationToken);
            }

            return _probed;
        }
    }

    /// <summary>The probe itself, on this platform.</summary>
    private async Task<IsolationAvailability> ProbedAsync(string tree, CancellationToken cancellationToken)
    {
        switch (platform.Current)
        {
            case PlatformId.Windows:
                return await WindowsAsync(tree, cancellationToken).ConfigureAwait(false);

            case PlatformId.MacOs:
                return await MacOsAsync(tree, cancellationToken).ConfigureAwait(false);
        }

        if (OnPath(tree, BubblewrapProgram) is not { } program)
        {
            return IsolationAvailability.None(
                $"bubblewrap ('{BubblewrapProgram}') was found in no absolute directory of the PATH outside the tree",
                $"install bubblewrap {Bubblewrap.Oldest} or newer (sudo apt-get install bubblewrap)" + OrPreferred);
        }

        // Run outside before anything is isolated - its version asked, every isolated child started through it - so one a
        // command of an agent's could replace is no mechanism.
        if (Writable(program) is { } writable)
        {
            return IsolationAvailability.None(
                $"bubblewrap '{program}' is one this user may replace ('{writable}' is writable to them), and the tool runs it outside every isolation",
                "install bubblewrap where only the system writes it (sudo apt-get install bubblewrap puts it in /usr/bin) and put that directory first on the PATH" + OrPreferred);
        }

        var version = await VersionAsync(program, cancellationToken).ConfigureAwait(false);

        if (version.Problem is { } problem)
        {
            return IsolationAvailability.None(problem, $"install bubblewrap {Bubblewrap.Oldest} or newer (sudo apt-get install bubblewrap)" + OrPreferred);
        }

        var mechanism = new Bubblewrap(program, version.Said!, Masked()) { Scoped = _landlock() >= Landlock.ScopedVersion };

        return await ProvedAsync(mechanism, tree, LinkPaths.Resolve, how => Unproved(mechanism, how), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// macOS's sandbox: its program at the one path it is taken from, this process inside no sandbox already - macOS starts
    /// none inside another - and the probe through it.
    /// </summary>
    private async Task<IsolationAvailability> MacOsAsync(string tree, CancellationToken cancellationToken)
    {
        if (!_mac.FileThere(SandboxExec.ProgramPath))
        {
            return IsolationAvailability.None($"macOS's sandbox ('{SandboxExec.ProgramPath}') is not on this machine", PreferredRemedy);
        }

        if (_mac.InsideASandbox())
        {
            return IsolationAvailability.None(
                "this process already runs inside a sandbox of macOS's, which starts no sandbox inside another",
                "run this tool outside every sandbox - not from inside another agent's or an application's" + OrPreferred);
        }

        var mechanism = new SandboxExec(_mac.MacOsVersion(), _mac.RealPath);

        return await ProvedAsync(
            mechanism,
            tree,
            _mac.RealPath,
            how => IsolationAvailability.None(
                $"macOS {mechanism.Version}'s sandbox ('{SandboxExec.ProgramPath}') did not prove itself: its probe {how}",
                $"check that the sandbox runs here ('{SandboxExec.ProgramPath} -p \"(version 1)(allow default)\" /usr/bin/true')" + OrPreferred),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Windows' container: MXC's library says the machine has it, the canary trial holds inside it, and each of node, git
    /// and Git Bash's sh found here starts inside it and answers.
    /// </summary>
    private async Task<IsolationAvailability> WindowsAsync(string tree, CancellationToken cancellationToken)
    {
        var support = _windows.Support();

        if (support.Unavailable is { } unavailable)
        {
            return IsolationAvailability.None(unavailable, WindowsRemedy);
        }

        var mechanism = new MxcProcessContainer(support.Version!, _self());
        var proved = await ProvedAsync(
            mechanism,
            tree,
            LinkPaths.Resolve,
            how => IsolationAvailability.None($"MXC {mechanism.Version}'s process container did not prove itself: its probe {how}", WindowsRemedy),
            cancellationToken).ConfigureAwait(false);

        if (proved.Mechanism is null)
        {
            return proved;
        }

        foreach (var (name, program, arguments) in WindowsTools(tree))
        {
            if (await AnsweredInsideAsync(mechanism, program, arguments, cancellationToken).ConfigureAwait(false) is { } how)
            {
                return IsolationAvailability.None(
                    $"MXC {mechanism.Version}'s process container does not run {name} ('{program}') on this Windows: inside it, {name} {how}",
                    $"update Windows and this tool, for a later MXC or Windows may run it, or run the agents from WSL 2, where bubblewrap isolates them{OrPreferred}");
            }
        }

        return proved;
    }

    /// <summary>
    /// The programs an agent's commands are made of on Windows, each found here and asked to answer: node, git and Git
    /// Bash's sh - one on the PATH outside the tree, else the one beside the git found - each with the words it answers to.
    /// One not on this machine is not asked: no agent here runs it.
    /// </summary>
    private IEnumerable<(string Name, string Program, IReadOnlyList<string> Arguments)> WindowsTools(string tree)
    {
        if (OnPath(tree, "node.exe") is { } node)
        {
            yield return ("node", node, ["--version"]);
        }

        var git = OnPath(tree, "git.exe");

        if (git is not null)
        {
            yield return ("git", git, ["--version"]);
        }

        if ((OnPath(tree, "sh.exe") ?? BesideGit(git)) is { } sh)
        {
            yield return ("Git Bash's sh", sh, ["-c", "echo answered"]);
        }
    }

    /// <summary>
    /// Git Bash's sh beside <paramref name="git"/>, as Git for Windows lays it out: <c>usr\bin\sh.exe</c>, else
    /// <c>bin\sh.exe</c>, under the installation's root, which holds git in <c>cmd\</c>, <c>bin\</c> or <c>mingw64\bin\</c>;
    /// null where there is none.
    /// </summary>
    private static string? BesideGit(string? git)
    {
        var root = git is null ? null : Path.GetDirectoryName(Path.GetDirectoryName(git));

        if (root is not null && Path.GetFileName(root).Equals("mingw64", StringComparison.OrdinalIgnoreCase))
        {
            root = Path.GetDirectoryName(root);
        }

        return root is null
            ? null
            : new[] { Path.Combine(root, "usr", "bin", "sh.exe"), Path.Combine(root, "bin", "sh.exe") }.FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// How <paramref name="program"/> failed to answer inside the container - started contained and inside it with nothing
    /// denied but what the container denies of itself; null where it ended 0 having said something.
    /// </summary>
    private async Task<string?> AnsweredInsideAsync(IIsolationMechanism mechanism, string program, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var (result, failed) = await TrialRunAsync(
            new ProcessIsolation(mechanism, IsolationProfile.Nothing),
            Path.GetDirectoryName(program)!,
            program,
            arguments,
            cancellationToken).ConfigureAwait(false);

        return failed
            ?? (result!.ExitCode != 0 || result.StandardOutput.Trim().Length == 0
                ? $"ended {Code(result.ExitCode)}{(FirstLine(result.StandardError.Trim().Length > 0 ? result.StandardError : result.StandardOutput) is { Length: > 0 } said ? $": '{said}'" : " saying nothing")}"
                : null);
    }

    /// <summary>An exit code as Windows writes it: a status the system ended the program with in hexadecimal (<c>0xC0000142</c>), any other as it is.</summary>
    private static string Code(int code)
        => code < 0 ? $"0x{(uint)code:X8}" : code.ToString(CultureInfo.InvariantCulture);

    /// <summary>What every remedy of a Windows with no container of MXC's ends with.</summary>
    private const string WindowsRemedy =
        $"MXC's process container needs {WindowsContainerHost.Builds}; or run the agents from WSL 2, where bubblewrap "
        + "isolates them" + OrPreferred;

    /// <summary>The first file named <paramref name="name"/> in an absolute directory of the PATH outside <paramref name="tree"/>; null where none is.</summary>
    private string? OnPath(string tree, string name)
    {
        var inside = Path.GetFullPath(tree).TrimEnd('/', '\\') + Path.DirectorySeparatorChar;

        foreach (var directory in (_path() ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!Path.IsPathFullyQualified(directory))
            {
                continue;
            }

            var whole = Path.GetFullPath(directory).TrimEnd('/', '\\') + Path.DirectorySeparatorChar;

            if (whole.StartsWith(inside, platform.PathComparison))
            {
                continue;
            }

            var candidate = Path.Combine(whole, name);

            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>The version <paramref name="program"/> answers, or why it is not one that serves.</summary>
    private async Task<(string? Said, string? Problem)> VersionAsync(string program, CancellationToken cancellationToken)
    {
        ProcessResult answered;

        try
        {
            answered = await runner.RunAsync(new ProcessRequest { FileName = program, Arguments = ["--version"], Timeout = ProbeBound }, cancellationToken).ConfigureAwait(false);
        }
        catch (ProgramStartException ex)
        {
            return (null, $"bubblewrap '{program}' could not be started: {ex.Message.TrimEnd('.')}");
        }

        var line = answered.StandardOutput.Trim();

        if (answered.ExitCode != 0 || VersionShape().Match(line) is not { Success: true } matched || !System.Version.TryParse(matched.Groups[1].Value, out var version))
        {
            return (null, $"bubblewrap '{program}' answered no version to '--version' (exit {answered.ExitCode.ToString(CultureInfo.InvariantCulture)}: '{FirstLine(line.Length > 0 ? line : answered.StandardError)}')");
        }

        return version < Bubblewrap.Oldest
            ? (null, $"bubblewrap '{program}' is {version}, older than {Bubblewrap.Oldest}, the first to take '--disable-userns'")
            : (version.ToString(), null);
    }

    /// <summary>The program, or the first directory above it, that this user may write; null where none is.</summary>
    private string? Writable(string program)
    {
        for (var at = program; !string.IsNullOrEmpty(at); at = Path.GetDirectoryName(at))
        {
            if (_userWrites(at))
            {
                return at;
            }
        }

        return null;
    }

    /// <summary>
    /// The machine's escapes there are here, each at its real place: WSL's interoperation sockets and shared directory,
    /// screen's sessions, the user's runtime directory whole - their service manager, message bus and agents' sockets - and
    /// the container engines' sockets.
    /// </summary>
    private static IReadOnlyList<IsolatedPath> Masked()
    {
        var masked = new List<IsolatedPath>();

        foreach (var directory in EscapeDirectories.Append(RuntimeDirectory()).OfType<string>())
        {
            if (Directory.Exists(directory))
            {
                masked.Add(new IsolatedPath(LinkPaths.Resolve(directory), Directory: true));
            }
        }

        foreach (var socket in ContainerSockets)
        {
            if (Path.Exists(socket) && LinkPaths.Resolve(socket) is var real && !masked.Any(place => IsolationProfile.Covers(place.Path, real)))
            {
                masked.Add(new IsolatedPath(real, Directory: false));
            }
        }

        return [.. masked.DistinctBy(place => place.Path, StringComparer.Ordinal)];
    }

    /// <summary>The user's runtime directory: <c>XDG_RUNTIME_DIR</c>, else <c>/run/user/&lt;uid&gt;</c>; null where neither is there.</summary>
    private static string? RuntimeDirectory()
    {
        if (Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } named && Directory.Exists(named))
        {
            return named;
        }

        try
        {
            var uid = File.ReadLines("/proc/self/status")
                .Select(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                .FirstOrDefault(words => words is ["Uid:", ..])?[1];

            return uid is not null && Directory.Exists($"/run/user/{uid}") ? $"/run/user/{uid}" : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// The probe through the real mechanism, started as every isolated child is, on <paramref name="tree"/>'s own file
    /// system - under its runs directory, which no child writes: a canary in a hidden place unread by its name and by another
    /// case of it, a place written by nobody inside an open one unwritten by either, a place outside every open one
    /// unwritten, the open place written - each said by the command inside and checked outside - or what
    /// <paramref name="unproved"/> makes of how it failed.
    /// </summary>
    /// <param name="mechanism">The mechanism.</param>
    /// <param name="tree">The tree whose file system the probe stands on.</param>
    /// <param name="real">A place's real spelling, as the mechanism meets it.</param>
    /// <param name="unproved">The answer of a probe that failed, from how it failed.</param>
    /// <param name="cancellationToken">Stops the probe.</param>
    private async Task<IsolationAvailability> ProvedAsync(IIsolationMechanism mechanism, string tree, Func<string, string> real, Func<string, IsolationAvailability> unproved, CancellationToken cancellationToken)
    {
        var runs = Path.Combine(tree, HarnessLayout.RunsDirectoryRelative.Replace('/', Path.DirectorySeparatorChar));
        var probe = Path.Combine(runs, "isolation-probe-" + Guid.NewGuid().ToString("N")[..12]);

        try
        {
            Directory.CreateDirectory(probe);
            var hidden = Directory.CreateDirectory(Path.Combine(probe, "hidden")).FullName;
            var open = Directory.CreateDirectory(Path.Combine(probe, "open")).FullName;
            var sealedPlace = Directory.CreateDirectory(Path.Combine(open, "sealed")).FullName;
            var canary = Path.Combine(hidden, "canary");
            var outside = Path.Combine(probe, "w");
            await File.WriteAllTextAsync(canary, "canary", cancellationToken).ConfigureAwait(false);

            // The open place a writable root of its own and the place written by nobody inside it, as every guarded place a
            // child could otherwise write stands; the probe's own directory, like the machine, written by none.
            var isolation = new ProcessIsolation(
                mechanism,
                new IsolationProfile(
                    [new IsolatedPath(real(hidden), Directory: true)],
                    [new IsolatedWrite(real(open), Directory: true, Writable: true), new IsolatedWrite(real(sealedPlace), Directory: true, Writable: false)]));
            var places = new ProbePlaces(probe, canary, Path.Combine(probe, "HIDDEN", "canary"), sealedPlace, Path.Combine(open, "SEALED"), open, outside);
            var how = platform.Current == PlatformId.Windows
                ? await WindowsTrialAsync(isolation, places, cancellationToken).ConfigureAwait(false)
                : await PosixTrialAsync(isolation, places, cancellationToken).ConfigureAwait(false);

            if (how is not null)
            {
                return unproved(how);
            }

            if (File.ReadAllText(canary) != "canary" || File.Exists(Path.Combine(sealedPlace, "w")) || File.Exists(outside) || !File.Exists(Path.Combine(open, "w")))
            {
                return unproved("ended 0, and the places outside do not hold what it said");
            }

            return IsolationAvailability.Of(mechanism);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return unproved($"could not make its places under '{runs}': {ex.Message.TrimEnd('.')}");
        }
        finally
        {
            Removed(probe);
        }
    }

    /// <summary>
    /// The probe's places: its own directory, the canary and it by another case of its directory's name, the place written
    /// by nobody and it by another case, the open place, and a file outside every open place.
    /// </summary>
    private sealed record ProbePlaces(string Probe, string Canary, string CanaryOtherCase, string Sealed, string SealedOtherCase, string Open, string Outside);

    /// <summary>
    /// The trial on Linux and macOS: one shell inside, which reads the canary by both names, writes the place written by
    /// nobody by both, the place outside and the open place, and ends saying the first that went as it must not; null where
    /// it ended 0.
    /// </summary>
    private async Task<string?> PosixTrialAsync(ProcessIsolation isolation, ProbePlaces places, CancellationToken cancellationToken)
    {
        var (result, failed) = await TrialRunAsync(
            isolation,
            places.Probe,
            "/bin/sh",
            ["-c", ProbeScript, "sh", places.Canary, places.CanaryOtherCase, places.Sealed, places.SealedOtherCase, places.Open, places.Outside],
            cancellationToken).ConfigureAwait(false);

        return failed
            ?? (result!.ExitCode != 0
                ? $"ended {Code(result.ExitCode)}: '{FirstLine(result.StandardOutput.Trim().Length > 0 ? result.StandardOutput : result.StandardError)}'"
                : null);
    }

    /// <summary>
    /// The trial on Windows: the system's command interpreter inside, once for each - the canary read by both names, the
    /// place written by nobody written by both, the place outside written, the open place written - each run asked alone,
    /// for the interpreter takes no words of a script the way a shell does; null where each went as it must.
    /// </summary>
    private async Task<string?> WindowsTrialAsync(ProcessIsolation isolation, ProbePlaces places, CancellationToken cancellationToken)
    {
        var interpreter = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        IReadOnlyList<(IReadOnlyList<string> Arguments, string Refused)> refusals =
        [
            (["/d", "/c", "type", places.Canary], "read the hidden canary"),
            (["/d", "/c", "type", places.CanaryOtherCase], $"read the hidden canary by {AnotherCase} of its name"),
            (["/d", "/c", "type", "nul", ">", Path.Combine(places.Sealed, "w")], "wrote the place written by nobody"),
            (["/d", "/c", "type", "nul", ">", Path.Combine(places.SealedOtherCase, "w")], $"wrote the place written by nobody by {AnotherCase} of its name"),
            (["/d", "/c", "type", "nul", ">", places.Outside], "wrote a place outside every open one"),
        ];

        foreach (var (arguments, refused) in refusals)
        {
            var (result, failed) = await TrialRunAsync(isolation, places.Probe, interpreter, arguments, cancellationToken).ConfigureAwait(false);

            if (failed is not null || result!.ExitCode == 0)
            {
                return failed ?? refused;
            }
        }

        var (openWrite, openFailed) = await TrialRunAsync(isolation, places.Probe, interpreter, ["/d", "/c", "type", "nul", ">", Path.Combine(places.Open, "w")], cancellationToken).ConfigureAwait(false);

        return openFailed
            ?? (openWrite!.ExitCode != 0
                ? $"did not write the open place: it ended {Code(openWrite.ExitCode)}: '{FirstLine(openWrite.StandardError.Trim().Length > 0 ? openWrite.StandardError : openWrite.StandardOutput)}'"
                : null);
    }

    /// <summary>One run of a trial, contained and inside <paramref name="isolation"/>: what it ended with, or how it did not.</summary>
    private async Task<(ProcessResult? Result, string? Failed)> TrialRunAsync(ProcessIsolation isolation, string probe, string program, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        try
        {
            var result = await runner.RunAsync(
                new ProcessRequest
                {
                    FileName = program,
                    Arguments = arguments,
                    WorkingDirectory = probe,
                    Timeout = ProbeBound,
                    Contained = true,
                    Isolation = isolation,
                },
                cancellationToken).ConfigureAwait(false);

            return result.TimedOut
                ? (null, $"did not end within {ProbeBound.TotalSeconds.ToString(CultureInfo.InvariantCulture)} s")
                : (result, null);
        }
        catch (ProgramStartException ex)
        {
            return (null, $"could not be started: {ex.Message.TrimEnd('.')}");
        }
    }

    /// <summary>Removes the probe's places; any left behind under the tree's runs are harmless.</summary>
    private static void Removed(string probe)
    {
        try
        {
            if (Directory.Exists(probe))
            {
                Directory.Delete(probe, recursive: true);
            }
        }
        catch (IOException)
        {
            // Left behind: harmless.
        }
        catch (UnauthorizedAccessException)
        {
            // As above.
        }
    }

    /// <summary>A probe that did not prove the mechanism, said with the remedy this machine needs.</summary>
    private static IsolationAvailability Unproved(Bubblewrap mechanism, string how)
    {
        var restricted = File.Exists(RestrictedUserNamespaces) && File.ReadAllText(RestrictedUserNamespaces).Trim() == "1";

        return IsolationAvailability.None(
            $"bubblewrap {mechanism.Version} ('{mechanism.Program}') did not prove itself: its probe {how}",
            how.Contains(AnotherCase, StringComparison.Ordinal)
                ? "keep the tree on a file system that tells letter case apart - WSL's own, not a Windows drive under /mnt" + OrPreferred
                : restricted
                    ? RestrictedRemedy(mechanism.Program, File.Exists) + OrPreferred
                    : "check that unprivileged user namespaces are allowed on this machine and that bubblewrap runs ('bwrap --ro-bind / / true')" + OrPreferred);
    }

    /// <summary>The AppArmor profile that lets bubblewrap make its user namespace where Ubuntu restricts them.</summary>
    internal const string RestrictProfile = "/etc/apparmor.d/bwrap-userns-restrict";

    /// <summary>Where Ubuntu 24.04 keeps that profile until it is copied into place: the apparmor-profiles package's extra profiles.</summary>
    internal const string ExtraRestrictProfile = "/usr/share/apparmor/extra-profiles/bwrap-userns-restrict";

    /// <summary>
    /// The remedy where Ubuntu restricts unprivileged user namespaces, in the commands this machine needs: the profile
    /// loaded where the apparmor package ships it in place (Ubuntu 25.04 and later), copied from the extra profiles
    /// first where it is there (Ubuntu 24.04), and the package that carries it installed first where neither is.
    /// </summary>
    /// <param name="program">The bubblewrap that did not prove itself.</param>
    /// <param name="exists">Whether a file is at a path.</param>
    internal static string RestrictedRemedy(string program, Func<string, bool> exists)
    {
        ArgumentNullException.ThrowIfNull(exists);

        var load = $"sudo apparmor_parser -r {RestrictProfile}";
        var commands = exists(RestrictProfile)
            ? load
            : exists(ExtraRestrictProfile)
                ? $"sudo cp {ExtraRestrictProfile} /etc/apparmor.d/ and {load}"
                : $"sudo apt-get install apparmor-profiles, sudo cp {ExtraRestrictProfile} /etc/apparmor.d/ and {load}";

        return $"Ubuntu restricts unprivileged user namespaces here ({RestrictedUserNamespaces} = 1): load the bwrap-userns-restrict AppArmor profile for '{program}' ({commands})";
    }

    /// <summary>Whether this user may write <paramref name="place"/>, as the system answers <c>access(W_OK)</c>.</summary>
    private static bool UserWrites(string place)
    {
        try
        {
            return Access(place, WriteAccess) == 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    /// <summary>The first line of <paramref name="text"/>, trimmed.</summary>
    private static string FirstLine(string text)
        => text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? string.Empty;

    /// <summary>W_OK.</summary>
    private const int WriteAccess = 2;

    [DllImport("libc", EntryPoint = "access", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int Access([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int mode);

    /// <summary>What <c>bwrap --version</c> answers: <c>bubblewrap 0.9.0</c>.</summary>
    [GeneratedRegex(@"^bubblewrap\s+(\d+\.\d+(?:\.\d+)?)", RegexOptions.CultureInvariant)]
    private static partial Regex VersionShape();
}
