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
/// neither read nor written, and the writes <see cref="Writes"/> decides - each place's rule standing over the rule of
/// any place above it, so a writable root inside a place written by nobody is written again. Everything else is as the
/// user: the rest of the machine read and written, the network open.
/// </summary>
/// <param name="Hidden">The places neither read nor written - each seen empty, a directory as an empty directory and a file as an empty file.</param>
/// <param name="Writes">The places whose writes are decided, a place above another always before it: the order the rules are laid in.</param>
public sealed record IsolationProfile(IReadOnlyList<IsolatedPath> Hidden, IReadOnlyList<IsolatedWrite> Writes)
{
    /// <summary>The profile that denies nothing, as the probe's outside runs are given none.</summary>
    public static IsolationProfile Nothing { get; } = new([], []);
}

/// <summary>
/// The operating system's mechanism that runs a contained child inside an <see cref="IsolationProfile"/>: on Linux the
/// system's bubblewrap (<see cref="Bubblewrap"/>), a program the child's own command line is handed to. Behind the platform
/// seam that finds and proves it (<see cref="Platform.IIsolationPlatform"/>).
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
