namespace DssHarness.Core.Processes;

/// <summary>
/// Linux's mechanism: the system's bubblewrap (<c>bwrap</c>), which the contained step becomes in the child's place, so
/// the child starts inside it and never outside for an instant. The whole machine is bound as it is, and the profile's
/// rules are laid over it in order - the last mount at a place wins: each write rule (<c>--ro-bind</c> where denied,
/// <c>--bind</c> where allowed again), then each hidden place (an empty read-only <c>tmpfs</c> over a directory,
/// <c>/dev/null</c> read-only over a file), then the machine's own escapes hidden the same way.
/// </summary>
/// <remarks>
/// <para>
/// A user namespace of its own with no further one allowed inside (<c>--unshare-user --disable-userns</c>), so no command
/// makes a namespace in which the masks could be undone; a process namespace of its own with its own <c>/proc</c>
/// (<c>--unshare-pid</c>), so no command reaches the files of a process outside through <c>/proc/&lt;id&gt;/root</c> or
/// <c>/fd</c>, where no mask stands; and the child ended with bubblewrap (<c>--die-with-parent</c>). No session of its
/// own is asked (<c>--new-session</c>): the contained step made one already, with no terminal, and a second would take
/// the child out of the session the tool ends at a stop. The network is the user's.
/// </para>
/// <para>
/// The machine's escapes (<see cref="Masked"/>) are places a process inside reaches what runs outside through: WSL's
/// interoperation sockets, which start a Windows program outside every mount, and the user's service manager and
/// message bus, which start a unit outside. <c>WSL_INTEROP</c> is taken from the child's environment as well.
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

    /// <summary>The oldest bubblewrap that takes <c>--disable-userns</c>.</summary>
    public static System.Version Oldest { get; } = new(0, 8, 0);

    /// <inheritdoc/>
    public string Name => MechanismName;

    /// <inheritdoc/>
    public (string Program, IReadOnlyList<string> Arguments) Command(IsolationProfile profile, string program, IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(program);
        ArgumentNullException.ThrowIfNull(arguments);

        var line = new List<string>
        {
            "--unshare-user", "--disable-userns", "--unshare-pid", "--die-with-parent",
            "--bind", "/", "/",
            "--dev-bind", "/dev", "/dev",
            "--proc", "/proc",
        };

        foreach (var write in profile.Writes)
        {
            line.AddRange([write.Writable ? "--bind" : "--ro-bind", write.Path, write.Path]);
        }

        foreach (var hidden in profile.Hidden.Concat(Masked))
        {
            line.AddRange(hidden.Directory
                ? ["--tmpfs", hidden.Path, "--remount-ro", hidden.Path]
                : ["--ro-bind", "/dev/null", hidden.Path]);
        }

        line.AddRange(["--unsetenv", WslInteropVariable, "--", program, .. arguments]);

        return (Program, line);
    }
}
