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
/// would not start another inside. On Windows the mechanism is not built yet, and it says so.
/// </summary>
/// <param name="platform">This machine.</param>
/// <param name="runner">Starts the version's question and the probe, the probe as a contained child is started.</param>
/// <param name="path">Reads the PATH bubblewrap is looked for on; this process's own where left out.</param>
/// <param name="mac">What finds macOS's sandbox on this machine; this machine's own where left out.</param>
public sealed partial class IsolationPlatform(IHostPlatform platform, IProcessRunner runner, Func<string?>? path = null, MacSandboxHost? mac = null) : IIsolationPlatform
{
    private readonly MacSandboxHost _mac = mac ?? MacSandboxHost.ThisMachine;

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
                return IsolationAvailability.None(
                    "the operating system's isolation of an agent's commands is not built for Windows yet",
                    "run the agents from WSL 2, where bubblewrap isolates them" + OrPreferred);

            case PlatformId.MacOs:
                return await MacOsAsync(cancellationToken).ConfigureAwait(false);
        }

        if (Found(tree) is not { } program)
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

    /// <summary>The first <c>bwrap</c> in an absolute directory of the PATH outside <paramref name="tree"/>; null where none is.</summary>
    private string? Found(string tree)
    {
        var inside = Path.GetFullPath(tree).TrimEnd('/') + "/";

        foreach (var directory in (_path() ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!Path.IsPathFullyQualified(directory))
            {
                continue;
            }

            var whole = Path.GetFullPath(directory).TrimEnd('/') + "/";

            if (whole.StartsWith(inside, StringComparison.Ordinal))
            {
                continue;
            }

            var candidate = Path.Combine(whole, BubblewrapProgram);

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

            var profile = new IsolationProfile(
                [new IsolatedPath(hidden, Directory: true)],
                [new IsolatedWrite(sealedPlace, Directory: true, Writable: false)]);

            ProcessResult result;

            try
            {
                result = await runner.RunAsync(
                    new ProcessRequest
                    {
                        FileName = "/bin/sh",
                        Arguments = ["-c", ProbeScript, "sh", canary, sealedPlace, open],
                        WorkingDirectory = probe,
                        Timeout = ProbeBound,
                        Contained = true,
                        Isolation = new ProcessIsolation(mechanism, profile),
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            catch (ProgramStartException ex)
            {
                return unproved($"could not be started: {ex.Message.TrimEnd('.')}");
            }

            if (result.ExitCode != 0 || result.TimedOut)
            {
                var said = FirstLine(result.StandardOutput.Trim().Length > 0 ? result.StandardOutput : result.StandardError);

                return unproved(result.TimedOut
                    ? $"did not end within {ProbeBound.TotalSeconds.ToString(CultureInfo.InvariantCulture)} s"
                    : $"ended {result.ExitCode.ToString(CultureInfo.InvariantCulture)}: '{said}'");
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
