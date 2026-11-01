namespace DssHarness.Core.Processes;

/// <summary>A place the operating system's isolation of a contained child guards, and whether it is a directory or a file.</summary>
/// <param name="Path">The place, absolute, as this machine spells it.</param>
/// <param name="Directory">Whether it is a directory; a file where false.</param>
public sealed record IsolatedPath(string Path, bool Directory);

/// <summary>A place whose writes the isolation decides: denied, or allowed again inside a place where they are denied.</summary>
/// <param name="Path">The place, absolute, as this machine spells it.</param>
/// <param name="Directory">Whether it is a directory; a file where false.</param>
/// <param name="Writable">Whether a write is allowed there again; denied where false.</param>
public sealed record IsolatedWrite(string Path, bool Directory, bool Writable);

/// <summary>
/// What the operating system denies a contained child beyond what its user may do: every place in <see cref="Hidden"/>
/// neither read nor written, the writes <see cref="Writes"/> decides - each place's rule standing over the rule of any
/// place above it, so a writable root inside a place written by nobody is written again - and every directory of
/// <see cref="Anchors"/> neither renamed nor removed. Everything else is as the user: the rest of the machine read and
/// written, the network open.
/// </summary>
/// <param name="Hidden">The places neither read nor written - each seen empty, or refused, as the mechanism hides it.</param>
/// <param name="Writes">The places whose writes are decided, a place above another always before it: the order the rules are laid in.</param>
public sealed record IsolationProfile(IReadOnlyList<IsolatedPath> Hidden, IReadOnlyList<IsolatedWrite> Writes)
{
    /// <summary>The profile that denies nothing, as the probe's outside runs are given none.</summary>
    public static IsolationProfile Nothing { get; } = new([], []);

    /// <summary>
    /// The directories no child may rename or remove, parent first: every directory above a place the profile names, and
    /// each writable root itself. A rule stands at a place, so a directory above it moved away would take the guarded
    /// place out from under its rule and leave its name free for a child to make again, written as it likes - the tool
    /// reading it outside later; and a writable root moved away and put back as a link would lead the next launch's
    /// rules to wherever the link points. The root of the file system is never one: no user renames it.
    /// </summary>
    public IReadOnlyList<string> Anchors
        => AnchorsOf(Hidden.Select(place => place.Path).Concat(Writes.Select(write => write.Path)), Writes.Where(write => write.Writable).Select(write => write.Path));

    /// <summary>
    /// The anchors of <paramref name="places"/> and <paramref name="roots"/> (<see cref="Anchors"/>), as a mechanism
    /// whose rules stand at each place's real spelling asks them of those spellings: every directory above a place, and
    /// each root itself, parent first.
    /// </summary>
    /// <param name="places">Every place a rule stands at.</param>
    /// <param name="roots">The writable roots.</param>
    public static IReadOnlyList<string> AnchorsOf(IEnumerable<string> places, IEnumerable<string> roots)
        => [.. places
            .SelectMany(Above)
            .Concat(roots.Select(Trimmed))
            .Distinct(StringComparer.Ordinal)
            .Select((path, at) => (path, at))
            .OrderBy(pair => pair.path.Count(letter => letter is '/' or '\\'))
            .ThenBy(pair => pair.at)
            .Select(pair => pair.path)];

    /// <summary>Every directory above <paramref name="path"/>, nearest first, short of the file system's root.</summary>
    private static IEnumerable<string> Above(string path)
    {
        var at = Trimmed(path);

        while (at.LastIndexOfAny(['/', '\\']) is var cut and > 0 && at[..cut] is var parent && !parent.EndsWith(':'))
        {
            yield return parent;
            at = parent;
        }
    }

    /// <summary><paramref name="path"/> without a separator at its end, the root's own kept.</summary>
    private static string Trimmed(string path) => path.Length > 1 ? path.TrimEnd('/', '\\') : path;
}

/// <summary>
/// The operating system's mechanism that runs a contained child inside an <see cref="IsolationProfile"/>: on Linux the
/// system's bubblewrap (<see cref="Bubblewrap"/>), on macOS the system's own sandbox (<see cref="SandboxExec"/>) - each a
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
    /// The command line that runs <paramref name="program"/> with <paramref name="arguments"/> inside
    /// <paramref name="profile"/>: the program the contained step becomes in its place, and its arguments.
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
