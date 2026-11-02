namespace DssHarness.Core.Processes;

/// <summary>A place the operating system's isolation of a contained child guards, and whether it is a directory or a file.</summary>
/// <param name="Path">The place, absolute, as the file system names it - every link along it followed.</param>
/// <param name="Directory">Whether it is a directory; a file where false.</param>
public sealed record IsolatedPath(string Path, bool Directory);

/// <summary>A place whose writes the isolation decides: written, or written by nobody inside a place that is written.</summary>
/// <param name="Path">The place, absolute, as the file system names it - every link along it followed.</param>
/// <param name="Directory">Whether it is a directory; a file where false.</param>
/// <param name="Writable">Whether a write is allowed there; denied where false.</param>
public sealed record IsolatedWrite(string Path, bool Directory, bool Writable);

/// <summary>
/// What the operating system lets a contained child do beyond reading the machine, which it reads whole and writes
/// nowhere: every place in <see cref="Hidden"/> neither read nor written, the writes <see cref="Writes"/> decides - each
/// place's rule standing over the rule of any place above it, so a place written by nobody inside a writable root stays
/// so - the launch's own <see cref="Temp"/> written, and every directory of <see cref="Anchors"/> neither renamed nor
/// removed. The network is the user's.
/// </summary>
/// <param name="Hidden">The places neither read nor written - each seen empty, or refused, as the mechanism hides it.</param>
/// <param name="Writes">The places whose writes are decided, a place above another always before it: the order the rules are laid in.</param>
public sealed record IsolationProfile(IReadOnlyList<IsolatedPath> Hidden, IReadOnlyList<IsolatedWrite> Writes)
{
    /// <summary>The profile that writes nowhere and hides nothing: the machine read whole.</summary>
    public static IsolationProfile Nothing { get; } = new([], []);

    /// <summary>
    /// The places the child reads as they stand wherever the mechanism gives it a place of its own in their stead - on
    /// Linux the system's temporary directory, which the child sees as its launch's own: the trees and roots the agent
    /// works over, which may stand under it.
    /// </summary>
    public IReadOnlyList<string> Reads { get; init; } = [];

    /// <summary>
    /// The places - directories or files - read again inside a hidden place, each beneath one: what a program reads there by
    /// itself, Claude Code's shell snapshots and ssh's <c>known_hosts</c> and <c>config</c> among them.
    /// </summary>
    public IReadOnlyList<string> Reopened { get; init; } = [];

    /// <summary>
    /// The launch's own temporary directory, written and handed the child as <c>TMPDIR</c> - on Linux also the system's
    /// temporary directory inside, so nothing another process keeps there is reached; null where it has none.
    /// </summary>
    public string? Temp { get; init; }

    /// <summary>The variables set in the child, in order: its temporary directory and each declared tool cache's.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Environment { get; init; } = [];

    /// <summary>
    /// Whether the child is a program's outer layer, which starts the mechanism again inside it for each command it hands
    /// over (<see cref="IIsolationMechanism.Unnested"/>): bubblewrap leaves it a user namespace to make each command's own
    /// layer in, where every command's own layer allows none inside it. False for every other child.
    /// </summary>
    public bool Outer { get; init; }

    /// <summary>
    /// The Unix sockets outside the child reaches, each alone wherever it stands - inside a hidden place or the system's
    /// temporary directory among them: a push's SSH agent's. None for an agent's own commands.
    /// </summary>
    public IReadOnlyList<string> Sockets { get; init; } = [];

    /// <summary>
    /// The directories no child may rename or remove, parent first: of <see cref="AnchorsOf"/>, each whose holder a child
    /// writes - a directory whose own holder no child writes is moved by none, and is no anchor.
    /// </summary>
    public IReadOnlyList<string> Anchors
        => [.. AnchorsOf(Hidden.Select(place => place.Path).Concat(Writes.Select(write => write.Path)), Writes.Where(write => write.Writable).Select(write => write.Path))
            .Where(directory => Parent(directory) is { } holder && Writable(holder))];

    /// <summary>
    /// Every directory that would carry a rule away were it moved, parent first: every directory above a place a rule
    /// stands at, and each writable root itself. A rule stands at a place, so a directory above it moved away would take
    /// the guarded place out from under its rule and leave its name free for a child to make again, written as it likes -
    /// the tool reading it outside later; and a writable root moved away and put back as a link would lead the next
    /// launch's rules to wherever the link points. The root of the file system, and a drive's, is never one: no user
    /// renames it. Which of them a mechanism holds, and what holding one costs there, is the mechanism's
    /// (<see cref="IIsolationMechanism.AnchorCost"/>).
    /// </summary>
    /// <param name="places">Every place a rule stands at.</param>
    /// <param name="roots">The writable roots.</param>
    public static IReadOnlyList<string> AnchorsOf(IEnumerable<string> places, IEnumerable<string> roots)
    {
        ArgumentNullException.ThrowIfNull(places);
        ArgumentNullException.ThrowIfNull(roots);

        return [.. places
            .SelectMany(Above)
            .Concat(roots.Select(Trimmed))
            .Distinct(StringComparer.Ordinal)
            .Select((path, at) => (path, at))
            .OrderBy(pair => Depth(pair.path))
            .ThenBy(pair => pair.at)
            .Select(pair => pair.path)];
    }

    /// <summary>
    /// Whether a child writes at <paramref name="path"/>: as the nearest rule at it or above it says - the launch's
    /// <see cref="Temp"/> one that writes - and nowhere that no rule reaches, for the machine is read-only.
    /// </summary>
    /// <param name="path">The place, absolute, as the file system names it.</param>
    public bool Writable(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        var rules = Temp is null ? Writes : [.. Writes, new IsolatedWrite(Temp, Directory: true, Writable: true)];

        return rules
            .Where(rule => Covers(rule.Path, path))
            .MaxBy(rule => Trimmed(rule.Path).Length)?.Writable ?? false;
    }

    /// <summary>Whether <paramref name="place"/> is <paramref name="path"/> or a directory above it.</summary>
    /// <param name="place">The place a rule stands at.</param>
    /// <param name="path">The path asked of.</param>
    public static bool Covers(string place, string path)
    {
        ArgumentNullException.ThrowIfNull(place);
        ArgumentNullException.ThrowIfNull(path);

        var at = Trimmed(place);
        var asked = Trimmed(path);

        return asked == at || asked.StartsWith(at.EndsWith('/') || at.EndsWith('\\') ? at : at + Separator(at), StringComparison.Ordinal);
    }

    /// <summary>How deep a path stands: its separators, so a place above another is laid before it.</summary>
    /// <param name="path">The path.</param>
    public static int Depth(string path) => Trimmed(path ?? throw new ArgumentNullException(nameof(path))).Count(letter => letter is '/' or '\\');

    /// <summary>Every directory above <paramref name="path"/>, nearest first, short of the file system's root.</summary>
    private static IEnumerable<string> Above(string path)
    {
        var at = Trimmed(path);

        while (Parent(at) is { } parent)
        {
            yield return parent;
            at = parent;
        }
    }

    /// <summary>The directory that holds <paramref name="path"/>, short of the file system's root; null for one the root holds.</summary>
    private static string? Parent(string path)
        => path.LastIndexOfAny(['/', '\\']) is var cut and > 0 && path[..cut] is var parent && !parent.EndsWith(':') ? parent : null;

    /// <summary>The separator <paramref name="path"/> is spelled with.</summary>
    private static char Separator(string path) => path.Contains('\\', StringComparison.Ordinal) && !path.Contains('/', StringComparison.Ordinal) ? '\\' : '/';

    /// <summary><paramref name="path"/> without a separator at its end, the root's own kept.</summary>
    private static string Trimmed(string path) => path.Length > 1 ? path.TrimEnd('/', '\\') : path;
}

/// <summary>
/// The operating system's mechanism that runs a contained child inside an <see cref="IsolationProfile"/>: on Linux the
/// system's bubblewrap (<see cref="Bubblewrap"/>), on macOS the system's own sandbox (<see cref="SandboxExec"/>), on
/// Windows MXC's process container, which this tool starts again to make (<see cref="MxcProcessContainer"/>) - each a
/// program the child's own command line is handed to. Behind the platform seam that finds and proves it
/// (<see cref="Platform.IIsolationPlatform"/>).
/// </summary>
public interface IIsolationMechanism
{
    /// <summary>The mechanism's name, as a record and a notice say it.</summary>
    string Name { get; }

    /// <summary>The version the mechanism answered when it was found.</summary>
    string Version { get; }

    /// <summary>
    /// Whether a rule holds a place under every spelling a file system that ignores letter case reaches it by - the
    /// mechanism judging the place the file system names, never the spelling asked - so a guarded place on such a file
    /// system is held.
    /// </summary>
    bool HoldsEveryCase { get; }

    /// <summary>
    /// Whether the contained step closes the child to what lies outside every path before it becomes the mechanism - on
    /// Linux, Landlock's scope of abstract Unix sockets and signals, where the kernel has it.
    /// </summary>
    bool Scoped { get; }

    /// <summary>What the mechanism on this machine leaves the child reaching outside, as a record says it; null where nothing.</summary>
    string? Leaves { get; }

    /// <summary>
    /// What holding an anchor (<see cref="IsolationProfile.AnchorsOf"/>) costs a child under the mechanism beyond the
    /// anchor's own rename and removal, as a record says it; null where nothing - bubblewrap makes each a mount point,
    /// macOS's sandbox refuses a directory's removal alone, and what each holds is written as its rule says.
    /// </summary>
    string? AnchorCost { get; }

    /// <summary>
    /// Whether the mechanism holds a rule at a file that is not there, denying its creation while nothing stands at the
    /// path for a child to read, as it holds one at a file that is. A mechanism whose every rule is a mount cannot: a
    /// mount at a path that is not there either holds nothing or puts a file of its own there.
    /// </summary>
    bool HoldsAnAbsentFile { get; }

    /// <summary>
    /// Whether the mechanism gives the child a temporary directory of its own, removed with it - so a profile names none
    /// (<see cref="IsolationProfile.Temp"/>) and sets no variable for one.
    /// </summary>
    bool OwnTemp { get; }

    /// <summary>
    /// Whether a program may hand each shell command it runs to the mechanism through this tool's isolated shell
    /// (<see cref="IsolatedShell"/>), which becomes the mechanism in the command's place - so Claude Code's own process, its
    /// login and the key it is handed stay apart from every command it runs. Where false, a launch of Claude Code runs whole
    /// inside.
    /// </summary>
    bool TakesHandedCommands { get; }

    /// <summary>
    /// Why a program inside the mechanism cannot start it again, tighter, for each command it hands over - so a program that
    /// hands its commands over (a Claude Code launch) runs outside every layer, where a command it ran without handing it
    /// over would run with the user's rights; null where it can, and the program runs inside an outer layer of its own
    /// (<see cref="IsolationProfile.Outer"/>), so even such a command stays inside that one.
    /// </summary>
    string? Unnested { get; }

    /// <summary>
    /// The command line that runs <paramref name="program"/> with <paramref name="arguments"/> inside
    /// <paramref name="profile"/>: the program the contained step becomes in its place, and its arguments - the profile's
    /// own variables set in the child.
    /// </summary>
    /// <param name="profile">What the child is denied.</param>
    /// <param name="program">The program, by its resolved path.</param>
    /// <param name="arguments">The program's own arguments.</param>
    (string Program, IReadOnlyList<string> Arguments) Command(IsolationProfile profile, string program, IReadOnlyList<string> arguments);
}

/// <summary>A contained child's isolation: the mechanism it runs inside, and what that mechanism denies it.</summary>
/// <param name="Mechanism">The mechanism, found and proved on this machine.</param>
/// <param name="Profile">What the child is denied.</param>
public sealed record ProcessIsolation(IIsolationMechanism Mechanism, IsolationProfile Profile);
