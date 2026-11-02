using Microsoft.Mxc.Sdk.V1;
using MxcContainment = Microsoft.Mxc.Sdk.V1.Containment;

namespace DssHarness.Core.Processes;

/// <summary>
/// Windows' mechanism: Microsoft's MXC process container, a container of the operating system's own that denies a process
/// every file it is not granted. The contained step starts this tool again as <c>isolate-exec</c> in the child's place -
/// inside the step's job, as every child it starts is - which starts the program inside a container of its own through
/// MXC's library and hands the program's streams and exit code back; the program and everything it starts are inside the
/// container, and in the job, from their first instant.
/// </summary>
/// <remarks>
/// <para>
/// The container grants nothing it is not told to (<see cref="Request"/>): every fixed drive is granted to read, each
/// writable root to write, each place written by nobody to read again inside a writable root, and each hidden place is
/// denied outright - read, written and listed - whole: what a profile reads again inside one
/// (<see cref="IsolationProfile.Reopened"/>) is not handed over, for the container grants nothing beneath a denial, and a
/// hidden directory denied entry by entry would leave an entry made after the start read. MXC meets a place by its path alone, so a directory renamed would carry the
/// places beneath it out from under their rules and leave its name free to make again; every directory above a guarded
/// place whose own directory may be written - and so whose name may be moved - is granted to read alone, each entry it
/// holds granted again as the profile has it, so nothing is renamed, made or removed directly inside it.
/// </para>
/// <para>
/// The container gets the child's environment as the step was given it, the profile's variables set over it - the
/// declared tool caches'; MXC gives it a temporary directory of its own, removed with it. Its user interface is the container's own - no clipboard, no input sent to another window - which the
/// programs that load the system's user interface library need to start at all; the network is the user's, the loopback
/// both ways among it.
/// </para>
/// </remarks>
/// <param name="version">The version of MXC's library.</param>
/// <param name="self">The program that is this tool, and what it is given before its own command line.</param>
public sealed class MxcProcessContainer(string version, (string Program, IReadOnlyList<string> Prefix) self) : IIsolationMechanism
{
    /// <summary>The mechanism's name, as a record says it.</summary>
    public const string MechanismName = "mxc";

    /// <summary>The step that starts a program inside a container, as the contained step starts it.</summary>
    public const string CommandName = "isolate-exec";

    /// <summary>The option naming a place neither read nor written.</summary>
    public const string HiddenOption = "--hidden";

    /// <summary>The option naming a place written by nobody.</summary>
    public const string SealedOption = "--sealed";

    /// <summary>The option naming a variable the profile sets in the child, as <c>NAME=VALUE</c>.</summary>
    public const string VariableOption = "--variable";

    /// <summary>The option naming a writable root.</summary>
    public const string WritableOption = "--writable";

    /// <summary>What separates the step's own options from the program and its arguments.</summary>
    public const string Separator = "--";

    /// <summary>What holding an anchor costs under MXC, as a record says it (<see cref="AnchorCost"/>).</summary>
    public const string AnchorReadAlone =
        "every directory above a guarded place whose own directory is written is read alone - nothing is made or removed "
        + "directly inside it - for MXC meets a place by its path and refuses no rename alone: git makes no lock file in a "
        + ".git, or a linked worktree's directory under it, that holds a sealed place, so git add, commit, fetch and push - and a run's commit and push steps - fail there";

    /// <inheritdoc/>
    public string Name => MechanismName;

    /// <inheritdoc/>
    public string Version { get; } = version ?? throw new ArgumentNullException(nameof(version));

    /// <summary>Windows finds a name in any case, and the container meets the place it names.</summary>
    public bool HoldsEveryCase => true;

    /// <summary>No scope of Landlock's: it is Linux's.</summary>
    public bool Scoped => false;

    /// <inheritdoc/>
    public string? Leaves => null;

    /// <inheritdoc/>
    public string? AnchorCost => AnchorReadAlone;

    /// <summary>MXC gives every container a temporary directory of its own, removed with it.</summary>
    public bool OwnTemp => true;

    /// <summary>
    /// Nothing on Windows becomes another program in its place, and every command Claude Code runs there is Git Bash's:
    /// a launch of it runs whole inside the container.
    /// </summary>
    public bool TakesHandedCommands => false;

    /// <inheritdoc/>
    public (string Program, IReadOnlyList<string> Arguments) Command(IsolationProfile profile, string program, IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(program);
        ArgumentNullException.ThrowIfNull(arguments);

        var line = new List<string>(self.Prefix) { CommandName };

        foreach (var hidden in profile.Hidden)
        {
            line.AddRange([HiddenOption, hidden.Path]);
        }

        foreach (var write in profile.Writes)
        {
            line.AddRange([write.Writable ? WritableOption : SealedOption, write.Path]);
        }

        foreach (var (name, value) in profile.Environment)
        {
            line.AddRange([VariableOption, $"{name}={value}"]);
        }

        line.AddRange([Separator, program, .. arguments]);

        return (self.Program, line);
    }

    /// <summary>
    /// The container request that starts <paramref name="program"/> with <paramref name="arguments"/> inside the places
    /// <paramref name="places"/> names: its command line as Windows reads one, its working directory and environment as
    /// given, the container's own user interface, the user's network, and the files as <see cref="Files"/> grants them.
    /// </summary>
    /// <param name="places">The profile's places, as the step was handed them.</param>
    /// <param name="program">The program, by its whole path.</param>
    /// <param name="arguments">Its arguments.</param>
    /// <param name="directory">The directory it starts in.</param>
    /// <param name="environment">Its environment, whole.</param>
    /// <param name="drives">Every fixed drive's root, each granted to read.</param>
    /// <param name="entries">Every entry a directory holds, by its whole path; none where it holds none or is not there.</param>
    public static ContainerRequest Request(
        ContainedPlaces places,
        string program,
        IReadOnlyList<string> arguments,
        string directory,
        IReadOnlyDictionary<string, string> environment,
        IReadOnlyList<string> drives,
        Func<string, IEnumerable<string>> entries)
    {
        ArgumentNullException.ThrowIfNull(places);
        ArgumentException.ThrowIfNullOrWhiteSpace(program);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(environment);

        // A path holds no quote, so the program's name is quoted as it is; its arguments are written as the C runtime
        // reads a command line back into words.
        var commandLine = arguments.Count == 0 ? $"\"{program}\"" : $"\"{program}\" {DetachedProcessLauncher.CommandLine(arguments)}";

        return new ContainerRequest(commandLine)
        {
            Containment = new MxcContainment.ProcessContainer
            {
                Ui = new ProcessContainerUiPolicy { Isolation = ProcessContainerUiIsolation.Container },
            },
            Ui = new UiPolicy { Disable = false, Clipboard = ClipboardPolicy.None, AllowInputInjection = false },
            Filesystem = Files(places, drives, entries),
            Network = new NetworkPolicy
            {
                Egress = new NetworkEgressPolicy { Default = NetworkAction.Allow },
                Ingress = new NetworkIngressPolicy { Default = NetworkAction.Allow, HostLoopback = NetworkAction.Allow },
            },
            WorkingDirectory = directory,
            Environment = new Dictionary<string, string>(environment, StringComparer.OrdinalIgnoreCase),
            InheritDefaultEnvironment = false,
        };
    }

    /// <summary>
    /// What the container grants of the files: every fixed drive to read, each writable root to write, each place written
    /// by nobody to read, each hidden place denied - and every anchor whose own directory may be written, granted to read
    /// alone with each entry it holds granted again as the profile has it, so its name is never moved and nothing is made
    /// or removed directly inside it. Each list in the order its places came, each place once.
    /// </summary>
    /// <param name="places">The profile's places.</param>
    /// <param name="drives">Every fixed drive's root.</param>
    /// <param name="entries">Every entry a directory holds, by its whole path.</param>
    public static FilesystemPolicy Files(ContainedPlaces places, IReadOnlyList<string> drives, Func<string, IEnumerable<string>> entries)
    {
        ArgumentNullException.ThrowIfNull(places);
        ArgumentNullException.ThrowIfNull(drives);
        ArgumentNullException.ThrowIfNull(entries);

        var comparer = StringComparer.OrdinalIgnoreCase;
        var readOnly = new List<string>(drives);
        var writable = new List<string>(places.Writable);
        var ruled = places.Hidden.Concat(places.Sealed).Concat(places.Writable).ToHashSet(comparer);

        readOnly.AddRange(places.Sealed);

        // Parent first, so whether the directory that holds an anchor is written is known before the anchor is laid.
        var held = new HashSet<string>(comparer);

        foreach (var anchor in IsolationProfile.AnchorsOf(places.Hidden.Concat(places.Sealed).Concat(places.Writable), places.Writable))
        {
            // A rename writes the directory that holds the name: an anchor whose holder is not written - read alone, or
            // itself an anchor held - is never moved.
            if (ContainedPlaces.Holder(anchor) is not { } holder || held.Contains(holder) || !places.Writes(holder))
            {
                continue;
            }

            held.Add(anchor);
            readOnly.Add(anchor);

            // Inside a place that is written, each entry the anchor holds is written again but those with rules of their
            // own: an anchor among the entries is written too, and never moved, for it is held by this one.
            if (places.Writes(anchor))
            {
                writable.AddRange(entries(anchor).Where(entry => !ruled.Contains(entry)));
            }
        }

        var policy = new FilesystemPolicy();
        policy.ReadonlyPaths.AddRange(readOnly.Distinct(comparer));
        policy.ReadwritePaths.AddRange(writable.Distinct(comparer));
        policy.DeniedPaths.AddRange(places.Hidden.Distinct(comparer));

        return policy;
    }
}

/// <summary>
/// The places a contained child's profile names, as the step that starts it inside MXC's container is handed them: each
/// hidden place, each place written by nobody and each writable root.
/// </summary>
/// <param name="Hidden">The places neither read nor written.</param>
/// <param name="Sealed">The places written by nobody.</param>
/// <param name="Writable">The writable roots.</param>
public sealed record ContainedPlaces(IReadOnlyList<string> Hidden, IReadOnlyList<string> Sealed, IReadOnlyList<string> Writable)
{
    /// <summary>
    /// Whether <paramref name="path"/> is written inside the container: the nearest place at or above it that a rule
    /// stands at is a writable root - none, a hidden place or a place written by nobody leaving it read alone.
    /// </summary>
    /// <param name="path">The place, whole.</param>
    public bool Writes(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var nearest = Hidden.Select(place => (place, writes: false))
            .Concat(Sealed.Select(place => (place, writes: false)))
            .Concat(Writable.Select(place => (place, writes: true)))
            .Where(rule => Within(path, rule.place))
            .OrderByDescending(rule => rule.place.TrimEnd('\\', '/').Length)
            .ThenBy(rule => rule.writes)
            .FirstOrDefault();

        return nearest.place is not null && nearest.writes;
    }

    /// <summary>
    /// The directory that holds <paramref name="path"/>, as Windows spells one - a drive's own root ending with its
    /// separator (<c>C:\</c>); null where nothing holds it.
    /// </summary>
    /// <param name="path">The place, whole.</param>
    public static string? Holder(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        var at = path.TrimEnd('\\', '/');
        var cut = at.LastIndexOfAny(['\\', '/']);

        return cut <= 0 ? null : at[..cut] is var holder && holder.EndsWith(':') ? holder + at[cut] : holder;
    }

    /// <summary>Whether <paramref name="path"/> is <paramref name="place"/> or beneath it, as Windows compares paths.</summary>
    private static bool Within(string path, string place)
    {
        var at = path.TrimEnd('\\', '/');
        var root = place.TrimEnd('\\', '/');

        return at.Equals(root, StringComparison.OrdinalIgnoreCase)
            || (at.StartsWith(root, StringComparison.OrdinalIgnoreCase) && at.Length > root.Length && at[root.Length] is '\\' or '/');
    }
}
