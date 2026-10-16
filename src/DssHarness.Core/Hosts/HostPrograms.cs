using DssHarness.Core.Platform;
using DssHarness.Core.Processes;

namespace DssHarness.Core.Hosts;

/// <summary>
/// What looking for a program on a host established. "Not found" and "could not look" are separate
/// because a refusal that says a tool is not installed sends somebody to install one that is already
/// there, and the remedies differ.
/// </summary>
public enum ProgramFound
{
    /// <summary>The host was never asked about it.</summary>
    Unknown,

    /// <summary>
    /// The PATH of a command run without a login shell names it, so its bare name starts it - or,
    /// for a program named by its path, that file is there.
    /// </summary>
    OnPath,

    /// <summary>It is installed, in a directory that PATH does not name, so only its absolute path starts it.</summary>
    OffPath,

    /// <summary>
    /// The host answered, and it is neither on that PATH nor in any directory that was searched - or,
    /// for a program named by its path, nothing is at that path.
    /// </summary>
    Nowhere,

    /// <summary>
    /// The host could not be asked, or could not look everywhere it was told to, so nothing is known
    /// and no refusal may claim it is missing.
    /// </summary>
    Unreadable,

    /// <summary>
    /// A declared tool is there, and every copy a leg could start said a version below the
    /// <c>minVersion</c> it is declared with. The location's path is the copy a leg would start, and its
    /// reason names each copy asked and what it said.
    /// </summary>
    BelowFloor,

    /// <summary>
    /// A declared tool is there with a copy at least its <c>minVersion</c>, and a directory raised ahead of
    /// the PATH for another tool holds an older copy of it, which a leg would start in its place. The
    /// location's path is that older copy, and its reason names both.
    /// </summary>
    FloorShadowed,

    /// <summary>
    /// A declared tool is there, and whether any copy a leg could start is at least its <c>minVersion</c>
    /// could not be established: no copy asked was shown to be, and a copy's version could not be read, or
    /// a place a copy could be could not be looked in. Nothing says it is below, and no refusal may claim
    /// it is. The location's path is the copy a leg would start, and its reason says what each copy said.
    /// </summary>
    FloorUnknown,
}

/// <summary>Every copy of one program a search found, in the order a leg looks, and where it could not look.</summary>
/// <param name="Copies">Each copy, by its path.</param>
/// <param name="Unsearched">
/// Why a place a copy could be was not looked in, or <see langword="null"/> where every one was: a copy no
/// search saw may be the one that meets a tool's minimum version.
/// </param>
public sealed record ProgramCopies(IReadOnlyList<string> Copies, string? Unsearched = null);

/// <summary>Where one program is on one host.</summary>
/// <param name="Program">The name it was asked for.</param>
/// <param name="Found">What the search established.</param>
/// <param name="Path">Where it is, when it was found; <see langword="null"/> otherwise.</param>
/// <param name="Reason">
/// Why nothing could be established, when the search could not look where it was told to: a name no
/// command line there can carry, a directory it could not name, or a shell that gave it no way to
/// look. <see langword="null"/> when the host did not answer.
/// </param>
public sealed record ProgramLocation(string Program, ProgramFound Found, string? Path = null, string? Reason = null)
{
    /// <summary>
    /// Whether the host has a copy a leg there starts, wherever it is: not one held below a declared tool's minimum
    /// version, which is there too (see <see cref="HeldToFloor"/>).
    /// </summary>
    public bool Present => Found is ProgramFound.OnPath or ProgramFound.OffPath;

    /// <summary>
    /// Whether a declared tool's minimum version turned the copy a leg would start down: it is there, and was not
    /// shown to meet it. Its reason says why.
    /// </summary>
    public bool HeldToFloor => Found is ProgramFound.BelowFloor or ProgramFound.FloorShadowed or ProgramFound.FloorUnknown;

    /// <summary>
    /// What the copy a leg starts said of its version, held to a declared tool's minimum version, where one asked it;
    /// <see langword="null"/> where none did.
    /// </summary>
    public Tools.FloorCopy? Judged { get; init; }

    /// <summary>
    /// The copy the PATH names first - the one a terminal there starts - where a minimum version raised the
    /// <see cref="Judged"/> one ahead of it; <see langword="null"/> where the copy a leg starts is that one.
    /// </summary>
    public Tools.FloorCopy? PassedOver { get; init; }

    /// <summary>
    /// That the copy a leg starts meets its minimum ahead of an older or unread first copy, or <see langword="null"/>
    /// where it is the first: the copy a terminal on the host starts is not the one a leg there does.
    /// </summary>
    public string? StartedAhead() => Judged is { Meets: true } judged && PassedOver is { } first ? judged.StartedAheadOf(first) : null;

    /// <summary>
    /// Why whether it is there could not be established: the search's own reason, where it could not
    /// look where it was told to, and the host not answering otherwise.
    /// </summary>
    public string WhyUnestablished() => Reason ?? $"the host did not answer when asked where '{Program}' is";

    /// <summary>That whether it is there could not be established, and why: the sentence a refusal about one program says it in.</summary>
    public string Unestablished() => $"whether '{Program}' is there could not be established: {WhyUnestablished()}";
}

/// <summary>
/// Finds, once per connection, where each program a host is asked to run actually is.
/// </summary>
/// <remarks>
/// A login shell's PATH is not the PATH a command sees. Measured: on macOS <c>/opt/homebrew/bin</c> is
/// absent from the PATH of a command run over ssh, and in a WSL distribution <c>~/.dotnet</c> is absent
/// from the PATH of a command run with <c>wsl.exe --exec</c>, which is exactly where the .NET install
/// script puts the SDK. Either way the leg fails saying the tool is not installed, when it is.
/// </remarks>
public interface IHostProgramResolver
{
    /// <summary>
    /// Measures where <paramref name="programs"/> are on the host and returns the connection carrying
    /// what it found. A program already measured on <paramref name="connection"/> is not measured again,
    /// so a caller that needs one more program pays for that one only.
    /// </summary>
    /// <param name="connection">The connection to measure on.</param>
    /// <param name="programs">The program names, as a command would name them.</param>
    /// <param name="directories">
    /// Where to look when the PATH does not name one, <c>~</c> being the host's home: the built-in
    /// list for the harness's own SDK, and the repository's <c>toolSearchDirectories</c> for its
    /// declared tools, each one the host's platform names - see <see cref="ToolSearchDirectories.For"/>.
    /// A directory given that cannot be looked in leaves a program found nowhere else unknown,
    /// never missing.
    /// </param>
    /// <param name="budget">Longest one lookup may take.</param>
    /// <param name="cancellationToken">Stops the lookups.</param>
    Task<HostConnection> ResolveAsync(
        HostConnection connection,
        IReadOnlyList<string> programs,
        IReadOnlyList<string> directories,
        TimeSpan budget,
        CancellationToken cancellationToken = default);

}

/// <inheritdoc cref="IHostProgramResolver"/>
public sealed class HostProgramResolver(LocalProgramResolver local, IHostCommandRunner hostCommands) : IHostProgramResolver
{
    private readonly LocalProgramResolver _local = local;
    private readonly IHostCommandRunner _hostCommands = hostCommands;

    public async Task<HostConnection> ResolveAsync(
        HostConnection connection,
        IReadOnlyList<string> programs,
        IReadOnlyList<string> directories,
        TimeSpan budget,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(programs);
        ArgumentNullException.ThrowIfNull(directories);

        var found = new Dictionary<string, ProgramLocation>(connection.Programs, StringComparer.Ordinal);
        var home = new Lazy<Task<Home>>(() => HomeAsync(connection, budget, cancellationToken));

        foreach (var program in programs.Distinct(StringComparer.Ordinal).Where(program => !found.ContainsKey(program)))
        {
            // This machine is asked the way a leg on it will be: the same function the survey and the
            // run use.
            found[program] = connection.Host.Kind == HostKind.Local
                ? _local.Find(program, directories)
                : await ThereAsync(connection, program, directories, home, budget, cancellationToken).ConfigureAwait(false);
        }

        // The home, where a search asked the host for it, kept with what it found: a program found under it is
        // named from there. Only a whole POSIX path is a home: PowerShell answers pwd with a table.
        var measured = home.IsValueCreated ? (await home.Value.ConfigureAwait(false)).Directory : null;

        return connection with
        {
            Programs = found,
            Home = measured is not null && PlatformPaths.IsAbsoluteOn(measured, PlatformNames.Linux) ? measured : connection.Home,
        };
    }

    /// <summary>Whether <paramref name="path"/> can be named in a command line sent over <paramref name="connection"/>.</summary>
    /// <remarks>wsl.exe --exec hands each argument over as it is, with no shell to split it.</remarks>
    private static bool Speakable(string path, HostConnection connection)
        => connection.Host.Kind == HostKind.Wsl || RemoteCommandLine.IsLiteral(path, connection.Shell);

    /// <summary>Why a path holding a space is not sent: a command line an ssh server hands to a shell carries only literal words.</summary>
    internal const string SpaceUnspeakable = "a path holding a space cannot be named in a command line sent there";

    /// <summary>
    /// Windows's program that lists where a name is found. Named with its extension, because PowerShell reads a bare
    /// <c>where</c> as its own Where-Object, which finds nothing and says nothing.
    /// </summary>
    private const string WhereProgram = "where.exe";

    /// <summary>Why a Windows shell's host cannot be looked in beyond its PATH.</summary>
    private static string NoSearchedDirectories(RemoteShell shell)
        => $"its shell, {(shell == RemoteShell.Cmd ? "cmd" : "PowerShell")}, gives this no way to look in the directories searched for programs";

    /// <summary>Why a name no command line sent there can carry is not looked for.</summary>
    private static string Unspeakable(string program)
        => $"'{program}' holds characters a command line sent there cannot carry, so it could not be looked for";

    /// <summary>
    /// What one <c>ls</c> of <paramref name="candidates"/> says: each one it printed, in the order given, or why what it
    /// printed says nothing.
    /// </summary>
    /// <remarks>
    /// One listing for every candidate at once: a separate command for each would open a separate ssh connection for
    /// each, and a host that is merely far away would take a minute to answer. A POSIX <c>ls</c> given files prints each
    /// one that exists as it was given; a candidate it printed is there, whatever else it printed beside it - the
    /// contents of one that is a directory, say. Anything else it printed and no candidate is some other shell's
    /// listing - PowerShell's is a table - and reading it as "none of them exist" would call every program there
    /// missing.
    /// </remarks>
    private async Task<Listing> ListAsync(HostConnection connection, IReadOnlyList<string> candidates, TimeSpan budget, CancellationToken cancellationToken)
    {
        var listing = await RunAsync(connection, "ls", candidates, budget, cancellationToken).ConfigureAwait(false);

        if (!HostProbes.Answered(listing))
        {
            return new Listing([], Answered: false, "the host did not answer when they were listed");
        }

        var lines = listing.StandardOutput
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToList();
        var existing = candidates.Where(lines.Contains).ToList();

        return existing.Count == 0 && lines.Count > 0
            ? new Listing([], Answered: true, "what its shell printed for the directories searched for programs is not a listing this can read")
            : new Listing(existing, Answered: true, null);
    }

    /// <summary>What one listing of candidate paths said.</summary>
    /// <param name="Existing">Each candidate it printed, in the order given.</param>
    /// <param name="Answered">Whether the host answered at all.</param>
    /// <param name="Unread">Why what it said could not be read, or <see langword="null"/> where it could.</param>
    private sealed record Listing(IReadOnlyList<string> Existing, bool Answered, string? Unread);

    /// <summary>Each line of <paramref name="output"/> that names a path, trimmed, in order.</summary>
    private static IEnumerable<string> Paths(string output)
        => output
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Contains('/', StringComparison.Ordinal) || line.Contains('\\', StringComparison.Ordinal));

    private async Task<ProgramLocation> ThereAsync(
        HostConnection connection,
        string program,
        IReadOnlyList<string> directories,
        Lazy<Task<Home>> home,
        TimeSpan budget,
        CancellationToken cancellationToken)
    {
        // The name travels inside a command line an ssh server hands to a shell, so a name no shell
        // reads literally cannot be looked up at all, and saying so beats guessing at it.
        if (!RemoteCommandLine.IsLiteral(program, connection.Shell))
        {
            return new ProgramLocation(program, ProgramFound.Unreadable, Reason: Unspeakable(program));
        }

        var onPath = await OnPathAsync(connection, program, budget, cancellationToken).ConfigureAwait(false);

        if (onPath.Found != ProgramFound.Nowhere)
        {
            return onPath;
        }

        // Every directory given is one this host was told to look in: its caller chose them for the
        // host's platform. Nothing to look in, and the PATH was everywhere there was to look.
        if (directories.Count == 0)
        {
            return onPath;
        }

        // cmd has no portable way to test one absolute path, and PowerShell's listing is a table. Its host
        // was told where to look, and saying the program is not there would be a claim about directories
        // nobody looked in.
        if (RemoteCommandLine.IsWindows(connection.Shell))
        {
            return Unlooked(program, NoSearchedDirectories(connection.Shell));
        }

        var answered = await home.Value.ConfigureAwait(false);

        // A home directory the host never said, because it did not answer, is no reason of the
        // search's own: that is the host not answering, which running again may well change.
        if (answered.Unanswered && directories.Any(PlatformPaths.IsHomeRelative))
        {
            return new ProgramLocation(program, ProgramFound.Unreadable);
        }

        var (candidates, unsearched) = Candidates(program, directories, answered.Directory, connection);

        if (candidates.Count > 0)
        {
            var listing = await ListAsync(connection, candidates, budget, cancellationToken).ConfigureAwait(false);

            if (!listing.Answered)
            {
                return new ProgramLocation(program, ProgramFound.Unreadable);
            }

            if (listing.Existing is [var path, ..])
            {
                return new ProgramLocation(program, ProgramFound.OffPath, path);
            }

            if (listing.Unread is { } unread)
            {
                return Unlooked(program, unread);
            }
        }

        return unsearched is { } reason ? Unlooked(program, reason) : new ProgramLocation(program, ProgramFound.Nowhere);
    }

    /// <summary>A program that is not on the PATH, with directories it could be in that were not looked in.</summary>
    private static ProgramLocation Unlooked(string program, string why)
        => new(program, ProgramFound.Unreadable, Reason: $"'{program}' is not on the PATH there, and {why}");

    /// <summary>
    /// Whether the PATH of a command run without a login shell names <paramref name="program"/>.
    /// <c>command -v</c> is the POSIX way, and <c>where.exe</c> the Windows one, for cmd and PowerShell.
    /// </summary>
    private async Task<ProgramLocation> OnPathAsync(
        HostConnection connection,
        string program,
        TimeSpan budget,
        CancellationToken cancellationToken)
    {
        var answered = false;

        foreach (var lookup in Lookups(connection, program))
        {
            var result = await RunAsync(connection, lookup.Program, lookup.Arguments, budget, cancellationToken)
                .ConfigureAwait(false);

            answered |= HostProbes.Answered(result);

            // The answer is not used as the path: a Windows one holds spaces, which no command line
            // built here may carry, and a name on PATH needs no path anyway.
            if (result.Succeeded && FirstPath(result.StandardOutput) is { } path)
            {
                return new ProgramLocation(program, ProgramFound.OnPath, path);
            }
        }

        return new ProgramLocation(program, answered ? ProgramFound.Nowhere : ProgramFound.Unreadable);
    }

    /// <summary>The host's home directory, when it said one, and whether it answered at all.</summary>
    /// <remarks>
    /// Both transports start a command there: <c>wsl.exe</c> is given <c>--cd ~</c>, and an ssh server
    /// starts a command in the user's home directory.
    /// </remarks>
    private async Task<Home> HomeAsync(HostConnection connection, TimeSpan budget, CancellationToken cancellationToken)
    {
        var result = await RunAsync(connection, "pwd", [], budget, cancellationToken).ConfigureAwait(false);

        return new Home(
            result.Succeeded && result.TrimmedOutput.Length > 0 ? result.TrimmedOutput : null,
            Unanswered: !HostProbes.Answered(result));
    }

    /// <summary>A host's home directory, when it said one.</summary>
    /// <param name="Directory">What it said, or <see langword="null"/> when it said none.</param>
    /// <param name="Unanswered">Whether the host did not answer at all, which says nothing about it.</param>
    private sealed record Home(string? Directory, bool Unanswered);

    /// <summary>The lookups tried, in order, for one host.</summary>
    private static IEnumerable<(string Program, IReadOnlyList<string> Arguments)> Lookups(HostConnection connection, string program)
    {
        // wsl.exe --exec starts a program rather than a shell, so the shell that owns command -v has to
        // be started explicitly. Its argument never reaches a second shell, so a space in it is safe.
        if (connection.Host.Kind == HostKind.Wsl)
        {
            yield return ("sh", new[] { "-c", $"command -v {program}" });
            yield break;
        }

        if (RemoteCommandLine.IsWindows(connection.Shell))
        {
            yield return (WhereProgram, new[] { program });
            yield break;
        }

        // where after it, for a POSIX shell with no command builtin: tcsh's own where answers the same question.
        yield return ("command", new[] { "-v", program });
        yield return ("where", new[] { program });
    }

    /// <summary>
    /// The candidate paths for one program, in the order they are preferred, and why any directory
    /// that should have been looked in cannot be.
    /// </summary>
    private static (IReadOnlyList<string> Candidates, string? Unsearched) Candidates(
        string program,
        IReadOnlyList<string> directories,
        string? home,
        HostConnection connection)
    {
        var candidates = new List<string>();
        var homeless = new List<string>();
        var foreign = new List<string>();
        var unspeakable = new List<string>();

        foreach (var directory in directories)
        {
            if (PlatformPaths.IsHomeRelative(directory) && home is null)
            {
                homeless.Add(directory);
                continue;
            }

            // Listed with a POSIX 'ls', which a Windows directory given to a host whose shell is not
            // cmd - PowerShell - cannot be.
            if (!ToolSearchDirectories.Names(directory, PlatformNames.Linux))
            {
                foreign.Add(directory);
                continue;
            }

            var path = (PlatformPaths.IsHomeRelative(directory) ? home!.TrimEnd('/') + directory[1..] : directory)
                + "/" + program;

            // A path holding a space cannot travel in a command line an ssh server hands to a shell,
            // so it is not sent in a form the shell would split - and is not reported as looked in
            // either.
            if (Speakable(path, connection))
            {
                candidates.Add(path);
            }
            else
            {
                unspeakable.Add(directory);
            }
        }

        var reasons = new List<string>();

        if (homeless.Count > 0)
        {
            reasons.Add($"{Quoted(homeless)} could not be looked in, because its home directory could not be read");
        }

        if (foreign.Count > 0)
        {
            reasons.Add($"{Quoted(foreign)} could not be looked in, because only a POSIX path can be listed there");
        }

        if (unspeakable.Count > 0)
        {
            reasons.Add($"{Quoted(unspeakable)} could not be looked in, because {SpaceUnspeakable}");
        }

        return (candidates, reasons.Count == 0 ? null : string.Join("; ", reasons));
    }

    private static string Quoted(IEnumerable<string> directories) => string.Join(", ", directories.Select(directory => $"'{directory}'"));

    private Task<ProcessResult> RunAsync(
        HostConnection connection,
        string program,
        IReadOnlyList<string> arguments,
        TimeSpan budget,
        CancellationToken cancellationToken)
        => _hostCommands.RunAsync(
            connection,
            new HostCommand { Program = program, Arguments = arguments, Timeout = budget },
            cancellationToken);

    private static string? FirstPath(string output) => Paths(output).FirstOrDefault();
}
