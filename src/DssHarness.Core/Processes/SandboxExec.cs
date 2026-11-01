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
/// system's settings, a terminal, the whole network and the services that resolve names and trust certificates, every
/// file read and written - and lays the profile's own rules after, for the last rule an operation meets is the one that
/// holds: each write rule in the profile's order, parent first (denied where a place is written by nobody, allowed again
/// at a writable root), each hidden place neither read nor written, each anchor neither renamed nor removed, and last
/// the two file controls that change a file through a descriptor opened only to read it, past every write rule.
/// </para>
/// <para>
/// The sandbox meets a path as the file system names it, every link followed - <c>/var</c> is <c>/private/var</c> there
/// - so each place is ruled at its real spelling, and at its own entry beside it where the place is itself a link, so
/// the link is held too; the anchors are those of the spellings. Each spelling is handed over as a parameter
/// (<c>-D</c>), never written into the profile's text, so no character of a path is read as the profile's own. macOS
/// starts no sandbox inside another, so a process already inside one has none of this tool's.
/// </para>
/// </remarks>
/// <param name="version">The version of macOS the sandbox is the system's of.</param>
/// <param name="realPath">A place's real spelling: every link along it followed, as the file system names it.</param>
public sealed class SandboxExec(string version, Func<string, string> realPath) : IIsolationMechanism
{
    /// <summary>The mechanism's name, as a record says it.</summary>
    public const string MechanismName = "sandbox-exec";

    /// <summary>The program, by the one path it is taken from: never one the PATH names, which an agent's own may change.</summary>
    public const string ProgramPath = "/usr/bin/sandbox-exec";

    /// <summary>
    /// The profile's every rule but the place's own, in the order they are laid: every operation denied, then what a
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
        "(allow sysctl-read)",
        "(allow sysctl-write (sysctl-name \"kern.grade_cputype\"))",
        "(allow iokit-open (iokit-registry-entry-class \"RootDomainUserClient\"))",
        "(allow ipc-posix-sem)",
        "(allow ipc-posix-shm-read* ipc-posix-shm-write-create ipc-posix-shm-write-data ipc-posix-shm-write-unlink)",
        "(allow pseudo-tty)",
        "(allow file-ioctl (literal \"/dev/ptmx\") (regex #\"^/dev/ttys[0-9]+\"))",
        "(allow user-preference-read)",
        "(allow system-socket (require-all (socket-domain AF_SYSTEM) (socket-protocol 2)))",
        "(allow network-outbound)",
        "(allow network-inbound)",
        "(allow network-bind)",
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
        "(allow file-write*)",
    ];

    /// <summary>The file controls that change a file through a descriptor opened only to read it: F_MAKECOMPRESSED and F_TRANSFEREXTENTS.</summary>
    private const string ReadOnlyDescriptorWrites = "(deny system-fcntl (fcntl-command 80 110))";

    /// <inheritdoc/>
    public string Name => MechanismName;

    /// <summary>The version of macOS the sandbox is the system's of: <c>sandbox-exec</c> answers none of its own.</summary>
    public string Version { get; } = version ?? throw new ArgumentNullException(nameof(version));

    private readonly Func<string, string> _realPath = realPath ?? throw new ArgumentNullException(nameof(realPath));

    /// <inheritdoc/>
    public (string Program, IReadOnlyList<string> Arguments) Command(IsolationProfile profile, string program, IReadOnlyList<string> arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(program);
        ArgumentNullException.ThrowIfNull(arguments);

        var (text, parameters) = Policy(profile);

        return (ProgramPath, ["-p", text, .. parameters.Select(parameter => $"-D{parameter.Name}={parameter.Value}"), "--", program, .. arguments]);
    }

    /// <summary>
    /// The profile's text for <paramref name="profile"/> and the parameters its rules name, in the order they are handed
    /// over: each write rule's spellings, then each hidden place's, then each anchor's.
    /// </summary>
    /// <param name="profile">What the child is denied.</param>
    public (string Text, IReadOnlyList<(string Name, string Value)> Parameters) Policy(IsolationProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var lines = new List<string>(Baseline);
        var parameters = new List<(string Name, string Value)>();
        var roots = new List<string>();
        var places = new List<string>();

        foreach (var write in profile.Writes)
        {
            var spellings = Spellings(write.Path);
            places.AddRange(spellings);

            if (write.Writable)
            {
                roots.AddRange(spellings);
            }

            lines.Add($"({(write.Writable ? "allow" : "deny")} file-write* {Filters("WRITE", spellings, write.Directory, parameters)})");
        }

        foreach (var hidden in profile.Hidden)
        {
            var spellings = Spellings(hidden.Path);
            places.AddRange(spellings);
            lines.Add($"(deny file-read* file-write* {Filters("HIDDEN", spellings, hidden.Directory, parameters)})");
        }

        foreach (var anchor in IsolationProfile.AnchorsOf(places, roots))
        {
            lines.Add($"(deny file-write-unlink (require-all (vnode-type DIRECTORY) {Filters("ANCHOR", [anchor], beneath: false, parameters)}))");
        }

        lines.Add(ReadOnlyDescriptorWrites);

        return (string.Join("\n", lines) + "\n", parameters);
    }

    /// <summary>
    /// The filters of a rule at <paramref name="spellings"/> - each with all beneath it where <paramref name="beneath"/>
    /// says so, as a directory's rule is, else alone - each spelling handed over as a parameter of its own, named by
    /// <paramref name="kind"/> and the count of that kind so far.
    /// </summary>
    private static string Filters(string kind, IReadOnlyList<string> spellings, bool beneath, List<(string Name, string Value)> parameters)
    {
        var filters = new StringBuilder();

        foreach (var spelling in spellings)
        {
            var name = $"{kind}_{parameters.Count(parameter => parameter.Name.StartsWith(kind + "_", StringComparison.Ordinal))}";
            parameters.Add((name, spelling));
            filters.Append(filters.Length > 0 ? " " : string.Empty).Append($"({(beneath ? "subpath" : "literal")} (param \"{name}\"))");
        }

        return filters.ToString();
    }

    /// <summary>
    /// <paramref name="path"/>'s spellings the sandbox meets: its own entry - the directory above it real, its own name as
    /// it stands - and its real spelling, where that differs, as it does for a place that is itself a link.
    /// </summary>
    private IReadOnlyList<string> Spellings(string path)
    {
        var real = _realPath(path);
        var trimmed = path.Length > 1 ? path.TrimEnd('/') : path;
        var cut = trimmed.LastIndexOf('/');

        if (cut < 0 || cut == trimmed.Length - 1)
        {
            return [real];
        }

        var above = _realPath(cut == 0 ? "/" : trimmed[..cut]);
        var entry = (above == "/" ? string.Empty : above.TrimEnd('/')) + "/" + trimmed[(cut + 1)..];

        return entry == real ? [real] : [entry, real];
    }
}
