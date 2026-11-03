namespace DssHarness.Core.Processes;

/// <summary>
/// Linux's mechanism: the system's bubblewrap (<c>bwrap</c>), which the contained step becomes in the child's place, so
/// the child starts inside it and never outside for an instant. The whole machine is bound read-only, a device directory
/// and a <c>/proc</c> of its own laid over it, the system's temporary directory replaced by the launch's own where it has
/// one, and the profile's rules laid over that in order - the last mount at a place wins: each place read as it stands
/// under that temporary directory (<c>--ro-bind-try</c>), each write rule (<c>--bind-try</c> where written,
/// <c>--ro-bind-try</c> where written by nobody) and each anchor among them bound onto itself as the rule above it leaves
/// it, so it is a mount point the child can neither rename nor remove, all parent first; then each hidden place (an empty
/// read-only <c>tmpfs</c> over a directory, what it reopens bound into it first; <c>/dev/null</c> read-only over a
/// file), then the machine's own escapes hidden the same way.
/// </summary>
/// <remarks>
/// <para>
/// A rule's place may be gone by the time a later command of the launch starts - another worktree removed meanwhile - so
/// every bind is one bubblewrap skips where its place is not there, never a refusal of every later command.
/// </para>
/// <para>
/// A user namespace of its own with no further one allowed inside (<c>--unshare-user --disable-userns</c>), so no command
/// makes a namespace in which to lay mounts of its own; a program's outer layer (<see cref="IsolationProfile.Outer"/>)
/// alone is left one to make, for each command it hands over is started inside it in a layer of its own, and the mounts
/// the outer layer laid are locked into every namespace made inside it, so none is undone there - no hidden place shown,
/// no read-only one written. A process namespace of its own with its own <c>/proc</c>
/// (<c>--unshare-pid</c>), so no command reaches a process outside - its environment, its files through
/// <c>/proc/&lt;id&gt;/root</c> - where no mount stands. Bubblewrap is not asked to end with its parent: the parent it
/// would watch is the thread of the tool that started the contained step, whose end would kill a long launch at random;
/// the tool's stop ends the child's group, the child first asked to end. No session of its own is asked
/// (<c>--new-session</c>): the contained step made one already, with no terminal, and a second would take the child out of
/// the session the tool ends at a stop. The network is the user's.
/// </para>
/// <para>
/// The machine's escapes (<see cref="Masked"/>) are places a process inside reaches what runs outside through: WSL's
/// interoperation sockets and shared directory, which start a Windows program outside every mount, the user's runtime
/// directory - their service manager, their message bus, their agents' sockets - and the container engines' sockets, which
/// start a container with the machine bound into it. <c>WSL_INTEROP</c> is taken from the child's environment as well.
/// The profile's own sockets (<see cref="IsolationProfile.Sockets"/>: a commit's or a push's SSH agent's) are each bound
/// alone at its own path, inside a hidden place or the launch's <c>/tmp</c> among them, so the one socket is reached and
/// nothing beside it.
/// </para>
/// </remarks>
/// <param name="Program">The bubblewrap found, by its whole path.</param>
/// <param name="Version">The version it answered.</param>
/// <param name="Masked">The machine's escapes there are on this machine, hidden from every child as the profile's hidden places are.</param>
public sealed record Bubblewrap(string Program, string Version, IReadOnlyList<IsolatedPath> Masked) : IIsolationMechanism
{
    /// <summary>The mechanism's name, as a record says it.</summary>
    public const string MechanismName = "bubblewrap";

    /// <summary>The variable WSL names its interoperation socket by, taken from every child inside.</summary>
    public const string WslInteropVariable = "WSL_INTEROP";

    /// <summary>The system's temporary directory, which a child with a temporary directory of its launch's own sees as that one.</summary>
    public const string SystemTemp = "/tmp";

    /// <summary>What a record says the mechanism leaves reached where the kernel has no Landlock scope.</summary>
    public const string Unscoped = "abstract Unix sockets and signals of processes outside, for this kernel has no Landlock scope (Linux 6.12 or newer)";

    /// <summary>The oldest bubblewrap that takes <c>--disable-userns</c>.</summary>
    public static System.Version Oldest { get; } = new(0, 8, 0);

    /// <inheritdoc/>
    public string Name => MechanismName;

    /// <summary>A mount holds the spelling it is made at alone: on a file system that ignores letter case another spelling passes it.</summary>
    public bool HoldsEveryCase => false;

    /// <summary>Whether the kernel has Landlock's scope of abstract Unix sockets and signals, which the contained step sets before it becomes bubblewrap.</summary>
    public bool Scoped { get; init; }

    /// <inheritdoc/>
    public string? Leaves => Scoped ? null : Unscoped;

    /// <summary>Nothing: each anchor is a mount point, which no child renames or removes, and what it holds is written as its rule says.</summary>
    public string? AnchorCost => null;

    /// <summary>A rule is a mount alone: one whose place is not there is skipped, and a mount made over such a path puts a file of bubblewrap's own there, so an absent file is held by no rule.</summary>
    public bool HoldsAnAbsentFile => false;

    /// <summary>The launch's own temporary directory is bound in the system's one's place.</summary>
    public bool OwnTemp => false;

    /// <summary>The isolated shell becomes bubblewrap in a command's place.</summary>
    public bool TakesHandedCommands => true;

    /// <summary>
    /// Why bubblewrap cannot be started again inside a layer of its own on this machine, as its probe found - an AppArmor
    /// profile that denies bubblewrap's children every capability, as Ubuntu's <c>bwrap-userns-restrict</c> does; null
    /// where it can.
    /// </summary>
    public string? Unnested { get; init; }

    /// <inheritdoc/>
    public (string Program, IReadOnlyList<string> Arguments) Command(IsolationProfile profile, string program, IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(program);
        ArgumentNullException.ThrowIfNull(arguments);

        // A program's outer layer is left a user namespace to make, for each command it hands over is given a layer of its own.
        List<string> line = profile.Outer ? ["--unshare-user", "--unshare-pid"] : ["--unshare-user", "--disable-userns", "--unshare-pid"];
        line.AddRange(
        [
            "--ro-bind", "/", "/",
            "--dev", "/dev",
            "--proc", "/proc",
        ]);

        if (profile.Temp is { } temp)
        {
            line.AddRange(["--bind", temp, SystemTemp]);

            foreach (var read in profile.Reads.Where(read => IsolationProfile.Covers(SystemTemp, read)).OrderBy(IsolationProfile.Depth))
            {
                line.AddRange(["--ro-bind-try", read, read]);
            }
        }

        foreach (var write in Laid(profile))
        {
            line.AddRange([write.Writable ? "--bind-try" : "--ro-bind-try", write.Path, write.Path]);
        }

        var hiddenDirectories = new List<string>();

        foreach (var hidden in profile.Hidden.Concat(Masked))
        {
            if (!hidden.Directory)
            {
                line.AddRange(["--ro-bind", "/dev/null", hidden.Path]);
                continue;
            }

            hiddenDirectories.Add(hidden.Path);
            line.AddRange(["--tmpfs", hidden.Path]);

            foreach (var reopened in profile.Reopened.Where(reopened => reopened != hidden.Path && IsolationProfile.Covers(hidden.Path, reopened)))
            {
                line.AddRange(["--ro-bind-try", reopened, reopened]);
            }

            // A socket the child reaches inside a hidden place is bound into the empty one before it is made read-only.
            foreach (var socket in profile.Sockets.Where(socket => IsolationProfile.Covers(hidden.Path, socket)))
            {
                line.AddRange(["--bind-try", socket, socket]);
            }

            line.AddRange(["--remount-ro", hidden.Path]);
        }

        // Every other socket it reaches - in the system's temporary directory, which is the launch's own inside - bound last.
        foreach (var socket in profile.Sockets.Where(socket => !hiddenDirectories.Any(directory => IsolationProfile.Covers(directory, socket))))
        {
            line.AddRange(["--bind-try", socket, socket]);
        }

        line.AddRange(["--unsetenv", WslInteropVariable]);

        foreach (var (name, value) in profile.Environment)
        {
            line.AddRange(["--setenv", name, value]);
        }

        line.AddRange(["--", program, .. arguments]);

        return (Program, line);
    }

    /// <summary>
    /// The binds laid, parent first: each write rule, the launch's temporary directory where it stands, and each anchor
    /// that is none bound onto itself as the rule above it leaves it - written where the nearest rule above it is written,
    /// read-only where it is not - so it is a mount point inside, which no child renames or removes, and every rule beneath
    /// it is laid after it: a bind takes the machine's own view of its place, which would cover a rule laid before it.
    /// </summary>
    private static IEnumerable<IsolatedWrite> Laid(IsolationProfile profile)
    {
        var rules = profile.Temp is { } temp ? [.. profile.Writes, new IsolatedWrite(temp, Directory: true, Writable: true)] : profile.Writes;
        var ruled = rules.Select(write => write.Path).ToHashSet(StringComparer.Ordinal);

        return rules
            .Concat(profile.Anchors.Where(anchor => !ruled.Contains(anchor)).Select(anchor => new IsolatedWrite(anchor, Directory: true, Writable: profile.Writable(anchor))))
            .Select((write, at) => (write, at))
            .OrderBy(pair => IsolationProfile.Depth(pair.write.Path))
            .ThenBy(pair => pair.at)
            .Select(pair => pair.write);
    }
}
