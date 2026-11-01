using System.Globalization;
using System.Text.RegularExpressions;
using DssHarness.Core.Processes;

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
/// found and proved by a probe once per process - a canary in a hidden place unread, a canary in a place written by
/// nobody unwritten, and a write in an open place done, through the real mechanism - or why there is none.
/// </summary>
public interface IIsolationPlatform
{
    /// <summary>
    /// The mechanism, proved, or why there is none: the first call probes, and every later one answers what it found.
    /// </summary>
    /// <param name="tree">The tree the process works over: a program found on the PATH inside it is never the mechanism.</param>
    /// <param name="cancellationToken">Stops the probe.</param>
    Task<IsolationAvailability> ProbeAsync(string tree, CancellationToken cancellationToken);
}

/// <summary>
/// The isolation this machine has, proved by the probe through the process runner's contained step, as every isolated
/// child is started. On Linux, WSL's included, the system's bubblewrap 0.8 or newer - the first <c>bwrap</c> in an
/// absolute directory of the PATH outside the tree, where Ubuntu's AppArmor profile for it attaches. On macOS the
/// system's own sandbox, <c>/usr/bin/sandbox-exec</c>, where this process is not already inside a sandbox, which macOS
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
public sealed partial class IsolationPlatform(
    IHostPlatform platform,
    IProcessRunner runner,
    Func<string?>? path = null,
    MacSandboxHost? mac = null,
    WindowsContainerHost? windows = null,
    Func<(string Program, IReadOnlyList<string> Prefix)>? self = null) : IIsolationPlatform
{
    private readonly MacSandboxHost _mac = mac ?? MacSandboxHost.ThisMachine;
    private readonly WindowsContainerHost _windows = windows ?? WindowsContainerHost.ThisMachine;
    private readonly Func<(string Program, IReadOnlyList<string> Prefix)> _self = self ?? DetachedProcessLauncher.Self;

    /// <summary>The program's name.</summary>
    public const string BubblewrapProgram = "bwrap";

    /// <summary>The sysctl Ubuntu restricts unprivileged user namespaces by, which bubblewrap needs.</summary>
    public const string RestrictedUserNamespaces = "/proc/sys/kernel/apparmor_restrict_unprivileged_userns";

    /// <summary>What the probe's command says and ends with where everything was as it must be.</summary>
    private const string ProbeScript =
        "if cat \"$1\" >/dev/null 2>&1; then echo 'the hidden canary was read'; exit 3; fi; "
        + "if : > \"$2/w\" 2>/dev/null; then echo 'the sealed place was written'; exit 4; fi; "
        + ": > \"$3/w\" || { echo 'the open place was not written'; exit 5; }";

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
                return await MacOsAsync(cancellationToken).ConfigureAwait(false);
        }

        if (OnPath(tree, BubblewrapProgram) is not { } program)
        {
            return IsolationAvailability.None(
                $"bubblewrap ('{BubblewrapProgram}') was found in no absolute directory of the PATH outside the tree",
                $"install bubblewrap {Bubblewrap.Oldest} or newer (sudo apt-get install bubblewrap)" + OrPreferred);
        }

        var version = await VersionAsync(program, cancellationToken).ConfigureAwait(false);

        if (version.Problem is { } problem)
        {
            return IsolationAvailability.None(problem, $"install bubblewrap {Bubblewrap.Oldest} or newer (sudo apt-get install bubblewrap)" + OrPreferred);
        }

        var mechanism = new Bubblewrap(program, version.Said!, Masked());

        return await ProvedAsync(mechanism, how => Unproved(mechanism, how), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// macOS's sandbox: its program at the one path it is taken from, this process inside no sandbox already - macOS starts
    /// none inside another - and the probe through it.
    /// </summary>
    private async Task<IsolationAvailability> MacOsAsync(CancellationToken cancellationToken)
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

    /// <summary>The machine's escapes there are here: WSL's interoperation sockets, the user's service manager and message bus.</summary>
    private static IReadOnlyList<IsolatedPath> Masked()
    {
        var masked = new List<IsolatedPath>();

        if (Directory.Exists("/run/WSL"))
        {
            masked.Add(new IsolatedPath("/run/WSL", Directory: true));
        }

        if (RuntimeDirectory() is { } runtime)
        {
            var manager = Path.Combine(runtime, "systemd");
            var bus = Path.Combine(runtime, "bus");

            if (Directory.Exists(manager))
            {
                masked.Add(new IsolatedPath(manager, Directory: true));
            }

            if (File.Exists(bus) || Path.Exists(bus))
            {
                masked.Add(new IsolatedPath(bus, Directory: false));
            }
        }

        return masked;
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
    /// The probe through the real mechanism, started as every isolated child is: a canary in a hidden place unread, a
    /// place written by nobody unwritten, an open place written - each said by the command inside and checked outside -
    /// or what <paramref name="unproved"/> makes of how it failed.
    /// </summary>
    private async Task<IsolationAvailability> ProvedAsync(IIsolationMechanism mechanism, Func<string, IsolationAvailability> unproved, CancellationToken cancellationToken)
    {
        var probe = Directory.CreateTempSubdirectory("dssharness-isolation-").FullName;

        try
        {
            var hidden = Directory.CreateDirectory(Path.Combine(probe, "hidden")).FullName;
            var sealedPlace = Directory.CreateDirectory(Path.Combine(probe, "sealed")).FullName;
            var open = Directory.CreateDirectory(Path.Combine(probe, "open")).FullName;
            var canary = Path.Combine(hidden, "canary");
            await File.WriteAllTextAsync(canary, "canary", cancellationToken).ConfigureAwait(false);

            // The open place a writable root of its own, as every place a child writes is under a mechanism that grants
            // nothing it is not told to.
            var isolation = new ProcessIsolation(
                mechanism,
                new IsolationProfile(
                    [new IsolatedPath(hidden, Directory: true)],
                    [new IsolatedWrite(sealedPlace, Directory: true, Writable: false), new IsolatedWrite(open, Directory: true, Writable: true)]));
            var how = platform.Current == PlatformId.Windows
                ? await WindowsTrialAsync(isolation, probe, canary, sealedPlace, open, cancellationToken).ConfigureAwait(false)
                : await PosixTrialAsync(isolation, probe, canary, sealedPlace, open, cancellationToken).ConfigureAwait(false);

            if (how is not null)
            {
                return unproved(how);
            }

            if (File.ReadAllText(canary) != "canary" || File.Exists(Path.Combine(sealedPlace, "w")) || !File.Exists(Path.Combine(open, "w")))
            {
                return unproved("ended 0, and the places outside do not hold what it said");
            }

            return IsolationAvailability.Of(mechanism);
        }
        finally
        {
            try
            {
                Directory.Delete(probe, recursive: true);
            }
            catch (IOException)
            {
                // A probe directory left behind in the temporary directory is harmless.
            }
            catch (UnauthorizedAccessException)
            {
                // As above.
            }
        }
    }

    /// <summary>
    /// The trial on Linux and macOS: one shell inside, which reads the canary, writes the place written by nobody and the
    /// open place, and ends saying the first that went as it must not; null where it ended 0.
    /// </summary>
    private async Task<string?> PosixTrialAsync(ProcessIsolation isolation, string probe, string canary, string sealedPlace, string open, CancellationToken cancellationToken)
    {
        var (result, failed) = await TrialRunAsync(isolation, probe, "/bin/sh", ["-c", ProbeScript, "sh", canary, sealedPlace, open], cancellationToken).ConfigureAwait(false);

        return failed
            ?? (result!.ExitCode != 0
                ? $"ended {Code(result.ExitCode)}: '{FirstLine(result.StandardOutput.Trim().Length > 0 ? result.StandardOutput : result.StandardError)}'"
                : null);
    }

    /// <summary>
    /// The trial on Windows: the system's command interpreter inside, once for each of the three - the canary read, the
    /// place written by nobody written, the open place written - each run asked alone, for the interpreter takes no words
    /// of a script the way a shell does; null where each went as it must.
    /// </summary>
    private async Task<string?> WindowsTrialAsync(ProcessIsolation isolation, string probe, string canary, string sealedPlace, string open, CancellationToken cancellationToken)
    {
        var interpreter = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        var (read, readFailed) = await TrialRunAsync(isolation, probe, interpreter, ["/d", "/c", "type", canary], cancellationToken).ConfigureAwait(false);

        if (readFailed is not null || read!.ExitCode == 0)
        {
            return readFailed ?? "read the hidden canary";
        }

        var (sealedWrite, sealedFailed) = await TrialRunAsync(isolation, probe, interpreter, ["/d", "/c", "type", "nul", ">", Path.Combine(sealedPlace, "w")], cancellationToken).ConfigureAwait(false);

        if (sealedFailed is not null || sealedWrite!.ExitCode == 0)
        {
            return sealedFailed ?? "wrote the place written by nobody";
        }

        var (openWrite, openFailed) = await TrialRunAsync(isolation, probe, interpreter, ["/d", "/c", "type", "nul", ">", Path.Combine(open, "w")], cancellationToken).ConfigureAwait(false);

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

    /// <summary>A probe that did not prove the mechanism, said with the remedy this machine needs.</summary>
    private static IsolationAvailability Unproved(Bubblewrap mechanism, string how)
    {
        var restricted = File.Exists(RestrictedUserNamespaces) && File.ReadAllText(RestrictedUserNamespaces).Trim() == "1";

        return IsolationAvailability.None(
            $"bubblewrap {mechanism.Version} ('{mechanism.Program}') did not prove itself: its probe {how}",
            restricted
                ? $"Ubuntu restricts unprivileged user namespaces here ({RestrictedUserNamespaces} = 1): load the bwrap-userns-restrict AppArmor profile for '{mechanism.Program}' (on Ubuntu 24.04 from the apparmor-profiles package: sudo cp /usr/share/apparmor/extra-profiles/bwrap-userns-restrict /etc/apparmor.d/ and sudo apparmor_parser -r /etc/apparmor.d/bwrap-userns-restrict)" + OrPreferred
                : "check that unprivileged user namespaces are allowed on this machine and that bubblewrap runs ('bwrap --bind / / true')" + OrPreferred);
    }

    /// <summary>The first line of <paramref name="text"/>, trimmed.</summary>
    private static string FirstLine(string text)
        => text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? string.Empty;

    /// <summary>What <c>bwrap --version</c> answers: <c>bubblewrap 0.9.0</c>.</summary>
    [GeneratedRegex(@"^bubblewrap\s+(\d+\.\d+(?:\.\d+)?)", RegexOptions.CultureInvariant)]
    private static partial Regex VersionShape();
}
