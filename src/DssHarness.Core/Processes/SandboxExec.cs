using System.Text;

namespace DssHarness.Core.Processes;

/// <summary>
/// macOS's mechanism: the system's own sandbox, started by <c>/usr/bin/sandbox-exec</c> with a profile written for the
/// child, which the contained step becomes in the child's place - so the child starts inside it and never outside for an
/// instant, and every process it starts is inside it too, none able to leave it.
/// </summary>
/// <remarks>
/// <para>
/// The profile denies every operation but those it allows, never the reverse: one that allowed everything but a list
/// would leave open the system's services that start a program outside every sandbox, LaunchServices' among them. It
/// allows what a build, a test and a shell do as the user - starting and signalling its own processes, reading the
/// system's settings by the names a build asks (never another process's arguments or environment), a terminal, the
/// network by address, the services that resolve names and trust certificates, every file read - and writes nowhere but
/// the devices a shell writes to; then lays the profile's own rules after, for the last rule an operation meets is the one
/// that holds: the launch's temporary directory written, each write rule in the profile's order, parent first (written
/// at a writable root, denied where a place inside it is written by nobody), each hidden place neither read nor written,
/// each place it reopens read again, each anchor neither renamed nor removed, and last the two file controls that change
/// a file through a descriptor opened only to read it, past every write rule.
/// </para>
/// <para>
/// No Unix socket is reached but the system's name resolver, those under the launch's temporary directory and the
/// profile's own (<see cref="IsolationProfile.Sockets"/>: a commit's or a push's SSH agent's), so no command reaches a
/// terminal multiplexer, an editor's server or a container engine that runs what it is asked outside.
/// The sandbox judges the place the file system names, every link followed - the profile's places come at their real
/// spelling (<c>/var</c> is <c>/private/var</c> there) - so a rule holds a place under every spelling a file system that
/// ignores letter case reaches it by. Each place is handed over as a parameter (<c>-D</c>), never written into the
/// profile's text, so no character of a path is read as the profile's own; the profile's variables are set in the child
/// by <c>/usr/bin/env</c>, inside. macOS starts no sandbox inside another, so a process already inside one has none of this
/// tool's.
/// </para>
/// </remarks>
/// <param name="version">The version of macOS the sandbox is the system's of.</param>
/// <param name="realPath">A place's real spelling, as the file system names it: every link followed, each name in the case the disk holds it.</param>
public sealed class SandboxExec(string version, Func<string, string> realPath) : IIsolationMechanism
{
    /// <summary>The mechanism's name, as a record says it.</summary>
    public const string MechanismName = "sandbox-exec";

    /// <summary>The program, by the one path it is taken from: never one the PATH names, which an agent's own may change.</summary>
    public const string ProgramPath = "/usr/bin/sandbox-exec";

    /// <summary>The program that sets the profile's variables in the child, inside the sandbox, by its one path.</summary>
    public const string EnvironmentProgram = "/usr/bin/env";

    /// <summary>
    /// The profile's every rule but the places' own, in the order they are laid: every operation denied, then what a
    /// build, a test and a shell do as the user allowed.
    /// </summary>
    private static readonly string[] Baseline =
    [
        "(version 1)",
        "(deny default)",
        "(allow process-exec)",
        "(allow process-fork)",
        "(allow signal (target same-sandbox))",
        "(allow process-info* (target same-sandbox))",
        "(allow sysctl-read",
        "  (sysctl-name \"hw.activecpu\")",
        "  (sysctl-name \"hw.byteorder\")",
        "  (sysctl-name \"hw.cachelinesize_compat\")",
        "  (sysctl-name \"hw.cpufamily\")",
        "  (sysctl-name \"hw.cputype\")",
        "  (sysctl-name \"hw.l1dcachesize_compat\")",
        "  (sysctl-name \"hw.l2cachesize_compat\")",
        "  (sysctl-name \"hw.logicalcpu\")",
        "  (sysctl-name \"hw.logicalcpu_max\")",
        "  (sysctl-name \"hw.machine\")",
        "  (sysctl-name \"hw.memsize\")",
        "  (sysctl-name \"hw.model\")",
        "  (sysctl-name \"hw.ncpu\")",
        "  (sysctl-name \"hw.nperflevels\")",
        "  (sysctl-name \"hw.pagesize\")",
        "  (sysctl-name \"hw.pagesize_compat\")",
        "  (sysctl-name \"hw.physicalcpu\")",
        "  (sysctl-name \"hw.physicalcpu_max\")",
        "  (sysctl-name \"hw.tbfrequency_compat\")",
        "  (sysctl-name \"hw.vectorunit\")",
        "  (sysctl-name \"kern.argmax\")",
        "  (sysctl-name \"kern.hostname\")",
        "  (sysctl-name \"kern.maxfilesperproc\")",
        "  (sysctl-name \"kern.maxproc\")",
        "  (sysctl-name \"kern.osproductversion\")",
        "  (sysctl-name \"kern.osrelease\")",
        "  (sysctl-name \"kern.ostype\")",
        "  (sysctl-name \"kern.osvariant_status\")",
        "  (sysctl-name \"kern.osversion\")",
        "  (sysctl-name \"kern.secure_kernel\")",
        "  (sysctl-name \"kern.sysv.semmns\")",
        "  (sysctl-name \"kern.usrstack64\")",
        "  (sysctl-name \"kern.version\")",
        "  (sysctl-name \"machdep.cpu.brand_string\")",
        "  (sysctl-name \"sysctl.proc_cputype\")",
        "  (sysctl-name \"vm.loadavg\")",
        "  (sysctl-name-prefix \"hw.optional.\")",
        "  (sysctl-name-prefix \"hw.perflevel\")",
        "  (sysctl-name-prefix \"kern.proc.pgrp.\")",
        "  (sysctl-name-prefix \"kern.proc.pid.\")",
        "  (sysctl-name-prefix \"net.routetable.\"))",
        "(allow sysctl-write (sysctl-name \"kern.grade_cputype\"))",
        "(allow iokit-open (iokit-registry-entry-class \"RootDomainUserClient\"))",
        "(allow ipc-posix-sem)",
        "(allow ipc-posix-shm-read* ipc-posix-shm-write-create ipc-posix-shm-write-data ipc-posix-shm-write-unlink)",
        "(allow pseudo-tty)",
        "(allow file-ioctl (literal \"/dev/ptmx\") (regex #\"^/dev/ttys[0-9]+\"))",
        "(allow user-preference-read)",
        "(allow system-socket (require-all (socket-domain AF_SYSTEM) (socket-protocol 2)))",
        "(allow network-outbound (remote ip \"*:*\"))",
        "(allow network-inbound (local ip \"*:*\"))",
        "(allow network-bind (local ip \"*:*\"))",
        "(allow network-outbound (remote unix-socket (subpath \"/private/var/run/mDNSResponder\")))",
        "(allow mach-lookup",
        "  (global-name \"com.apple.system.opendirectoryd.libinfo\")",
        "  (global-name \"com.apple.system.opendirectoryd.membership\")",
        "  (global-name \"com.apple.system.notification_center\")",
        "  (global-name \"com.apple.bsd.dirhelper\")",
        "  (global-name \"com.apple.PowerManagement.control\")",
        "  (global-name \"com.apple.FSEvents\")",
        "  (global-name \"com.apple.SystemConfiguration.DNSConfiguration\")",
        "  (global-name \"com.apple.SystemConfiguration.configd\")",
        "  (global-name \"com.apple.networkd\")",
        "  (global-name \"com.apple.SecurityServer\")",
        "  (global-name \"com.apple.ocspd\")",
        "  (global-name \"com.apple.trustd.agent\")",
        "  (global-name \"com.apple.TrustEvaluationAgent\")",
        "  (global-name \"com.apple.cfprefsd.daemon\")",
        "  (global-name \"com.apple.cfprefsd.agent\")",
        "  (local-name \"com.apple.cfprefsd.agent\"))",
        "(deny mach-lookup (xpc-service-name-prefix \"\"))",
        "(allow file-read*)",
        "(allow file-write* (literal \"/dev/null\") (literal \"/dev/zero\") (literal \"/dev/tty\") (literal \"/dev/dtracehelper\") (literal \"/dev/ptmx\") (regex #\"^/dev/fd/[0-9]+$\") (regex #\"^/dev/ttys[0-9]+$\"))",
    ];

    /// <summary>The file controls that change a file through a descriptor opened only to read it: F_MAKECOMPRESSED and F_TRANSFEREXTENTS.</summary>
    private const string ReadOnlyDescriptorWrites = "(deny system-fcntl (fcntl-command 80 110))";

    private readonly Func<string, string> _realPath = realPath ?? throw new ArgumentNullException(nameof(realPath));

    /// <inheritdoc/>
    public string Name => MechanismName;

    /// <summary>The version of macOS the sandbox is the system's of: <c>sandbox-exec</c> answers none of its own.</summary>
    public string Version { get; } = version ?? throw new ArgumentNullException(nameof(version));

    /// <summary>The sandbox judges the place the file system names, so a rule holds it under every spelling: proved by the probe on the tree's own file system.</summary>
    public bool HoldsEveryCase => true;

    /// <summary>macOS has no abstract sockets, and the sandbox signals only its own processes.</summary>
    public bool Scoped => false;

    /// <inheritdoc/>
    public string? Leaves => null;

    /// <summary>Nothing: each anchor's own removal alone is refused - a rename removes its name - and what it holds is written as its rule says.</summary>
    public string? AnchorCost => null;

    /// <summary>The launch's own temporary directory is written and named to the child; the system's stays read-only.</summary>
    public bool OwnTemp => false;

    /// <summary>The isolated shell becomes the sandbox in a command's place.</summary>
    public bool TakesHandedCommands => true;

    /// <inheritdoc/>
    public (string Program, IReadOnlyList<string> Arguments) Command(IsolationProfile profile, string program, IReadOnlyList<string> arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(program);
        ArgumentNullException.ThrowIfNull(arguments);

        var (text, parameters) = Policy(profile);
        IReadOnlyList<string> started = profile.Environment.Count == 0
            ? [program, .. arguments]
            : [EnvironmentProgram, .. profile.Environment.Select(variable => $"{variable.Key}={variable.Value}"), program, .. arguments];

        return (ProgramPath, ["-p", text, .. parameters.Select(parameter => $"-D{parameter.Name}={parameter.Value}"), "--", .. started]);
    }

    /// <summary>
    /// The profile's text for <paramref name="profile"/> and the parameters its rules name, in the order they are handed
    /// over: the temporary directory, then each write rule's place, each hidden place's, each reopened one's and each
    /// anchor's.
    /// </summary>
    /// <param name="profile">What the child is denied.</param>
    public (string Text, IReadOnlyList<(string Name, string Value)> Parameters) Policy(IsolationProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var lines = new List<string>(Baseline);
        var parameters = new List<(string Name, string Value)>();

        if (profile.Temp is { } temp)
        {
            parameters.Add(("TEMP", _realPath(temp)));
            lines.Add("(allow file-write* (subpath (param \"TEMP\")))");
            lines.Add("(allow network-bind (local unix-socket (subpath (param \"TEMP\"))))");
            lines.Add("(allow network-outbound (remote unix-socket (subpath (param \"TEMP\"))))");
        }

        foreach (var write in profile.Writes)
        {
            var filter = Filter("WRITE", write.Path, write.Directory, parameters);

            // A file written alone is written in place: its data alone, never removed, made or renamed onto, so nothing -
            // a link among it - is put in its stead.
            if (write.Writable && !write.Directory)
            {
                lines.Add($"(allow file-write-data {filter})");
                lines.Add($"(deny file-write-unlink file-write-create {filter})");
                continue;
            }

            lines.Add($"({(write.Writable ? "allow" : "deny")} file-write* {filter})");
        }

        foreach (var hidden in profile.Hidden)
        {
            lines.Add($"(deny file-read* file-write* {Filter("HIDDEN", hidden.Path, hidden.Directory, parameters)})");
        }

        foreach (var reopened in profile.Reopened)
        {
            lines.Add($"(allow file-read* {Filter("REOPENED", reopened, beneath: true, parameters)})");
        }

        foreach (var anchor in profile.Anchors)
        {
            lines.Add($"(deny file-write-unlink (require-all (vnode-type DIRECTORY) {Filter("ANCHOR", anchor, beneath: false, parameters)}))");
        }

        foreach (var socket in profile.Sockets)
        {
            lines.Add($"(allow network-outbound (remote unix-socket {Filter("SOCKET", socket, beneath: false, parameters)}))");
        }

        lines.Add(ReadOnlyDescriptorWrites);

        return (string.Join("\n", lines) + "\n", parameters);
    }

    /// <summary>
    /// The filter of a rule at <paramref name="place"/> - with all beneath it where <paramref name="beneath"/> says so, as a
    /// directory's rule is, else alone - at its real spelling, handed over as a parameter of its own, named by
    /// <paramref name="kind"/> and the count of that kind so far.
    /// </summary>
    private string Filter(string kind, string place, bool beneath, List<(string Name, string Value)> parameters)
    {
        var name = new StringBuilder(kind).Append('_').Append(parameters.Count(parameter => parameter.Name.StartsWith(kind + "_", StringComparison.Ordinal))).ToString();
        parameters.Add((name, _realPath(place)));

        return $"({(beneath ? "subpath" : "literal")} (param \"{name}\"))";
    }
}
