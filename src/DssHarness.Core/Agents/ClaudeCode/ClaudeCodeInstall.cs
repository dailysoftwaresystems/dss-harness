using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Hosts;
using DssHarness.Core.Platform;
using DssHarness.Core.Processes;

namespace DssHarness.Core.Agents.ClaudeCode;

/// <summary>What npm's global place holds of Claude Code, as its own package says it.</summary>
/// <param name="Installed">Whether the package is there at all: its <c>package.json</c> is.</param>
/// <param name="Version">The version the package's <c>package.json</c> names, where it names one this tool reads.</param>
/// <param name="Program">The program its <c>bin.claude</c> names, under the package's directory.</param>
/// <param name="Problem">Why what is installed is none this tool can start, in its own words; null where it is one, or where nothing is installed.</param>
public sealed record ClaudeCodePackage(bool Installed, SemanticVersion? Version, string? Program, string? Problem)
{
    /// <summary>No package: npm holds no Claude Code.</summary>
    public static ClaudeCodePackage None { get; } = new(false, null, null, null);
}

/// <summary>
/// The sessions of an npm installation of Claude Code among one reading of the machine's processes, as
/// <see cref="ClaudeCodeInstall.SessionsAsync"/> found them: each process an update of it counts, and why.
/// </summary>
public sealed class ClaudeCodeSessions
{
    private readonly IReadOnlyDictionary<SampledProcess, string?> _counted;

    /// <summary>The sessions found, each with why it counts where it was not shown to run from the installation.</summary>
    internal ClaudeCodeSessions(IReadOnlyDictionary<SampledProcess, string?> counted) => _counted = counted;

    /// <summary>Whether <paramref name="process"/>, of the reading these were found in, counts as a session.</summary>
    public bool Counts(SampledProcess process) => _counted.ContainsKey(process);

    /// <summary>
    /// One session as a refusal names it: by its id - and, where it counts without being shown to run from the
    /// installation, what it is called and why it counts.
    /// </summary>
    public string Said(SampledProcess process)
    {
        ArgumentNullException.ThrowIfNull(process);

        return string.Create(CultureInfo.InvariantCulture, $"pid {process.Id}")
            + (_counted.GetValueOrDefault(process) is { } why ? $" ({process.Name}, {why})" : string.Empty);
    }
}

/// <summary>What a Claude Code program answered of its own version: the version, or why it gave none.</summary>
/// <param name="Version">The version its <c>--version</c> line names; null where it named none.</param>
/// <param name="Problem">Where it named none, how asking it ended, as the end of a sentence; null where it named one.</param>
public sealed record ClaudeCodeVersion(SemanticVersion? Version, string? Problem);

/// <summary>
/// The Claude Code npm installs, as its package says it: the directory <c>@anthropic-ai/claude-code</c> under npm's
/// global packages, its <c>package.json</c> naming the version and, under <c>bin</c>, the one program this tool
/// starts - by its full path, never a launcher, and never a batch file.
/// </summary>
/// <param name="fileSystem">Reads the package's files, and follows the links a running program was started through.</param>
/// <param name="processRunner">Asks the program's own version.</param>
/// <param name="time">Measures <see cref="FollowBudget"/>; the system's clock where none is given.</param>
public sealed class ClaudeCodeInstall(IFileSystem fileSystem, IProcessRunner processRunner, TimeProvider? time = null)
{
    /// <summary>The package npm installs, by its own name.</summary>
    public const string PackageId = "@anthropic-ai/claude-code";

    /// <summary>The variable that stops Claude Code's own background update, which every <c>claude</c> this tool starts is given.</summary>
    public const string AutoUpdaterVariable = "DISABLE_AUTOUPDATER";

    /// <summary>How long the program may take to answer its own version.</summary>
    public static readonly TimeSpan AskBudget = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long following the links of one reading's paths may take: a path on a network mount that answers nothing
    /// never fails, and an update waits on it no longer than this.
    /// </summary>
    public static readonly TimeSpan FollowBudget = TimeSpan.FromSeconds(10);

    /// <summary>What Claude Code's program, and node running its script, call themselves.</summary>
    private const string Claude = "claude";

    /// <summary>The program that runs the script of an installation older than the native program.</summary>
    private const string Node = "node";

    /// <summary>
    /// What every <c>claude</c> this tool starts has in its environment, beside what it inherits: Claude Code's own
    /// background update turned off, so only this tool's update moves an installation - its <c>--version</c> asked here,
    /// and every launch the clients make, which take their environment from this.
    /// </summary>
    public static IReadOnlyDictionary<string, string?> EveryStart { get; } =
        new Dictionary<string, string?>(StringComparer.Ordinal) { [AutoUpdaterVariable] = "1" }.AsReadOnly();

    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly IProcessRunner _processRunner = processRunner;
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>The package's directory under <paramref name="root"/>, npm's global packages as npm says it.</summary>
    public static string PackageDirectory(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        return Path.Combine([root, .. PackageId.Split('/')]);
    }

    /// <summary>
    /// What <paramref name="root"/> - npm's global packages, as npm says it - holds of Claude Code: nothing, where the
    /// package is not there; the version and the program, where it is; or why it is none this tool can start, where
    /// its <c>package.json</c> cannot be read, names no version or names no program under <c>bin.claude</c>.
    /// </summary>
    /// <param name="root">npm's global packages, as npm says it.</param>
    /// <param name="needed">The version a refusal says to install again.</param>
    public ClaudeCodePackage Read(string root, string needed)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        var package = PackageDirectory(root);
        var manifest = Path.Combine(package, "package.json");

        if (!_fileSystem.FileExists(manifest))
        {
            return ClaudeCodePackage.None;
        }

        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(_fileSystem.ReadAllText(manifest));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // A package.json that cannot be read says nothing certain of the program, and an installation this tool
            // cannot tell one of is refused as one it cannot start - with what stopped the reading.
            return new ClaudeCodePackage(true, null, null, NotOne(manifest, $"could not be read ({ex.Message.TrimEnd('.')})", needed));
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new ClaudeCodePackage(true, null, null, NotOne(manifest, $"could not be read (it holds a JSON {document.RootElement.ValueKind.ToString().ToLowerInvariant()}, not an object)", needed));
            }

            var named = document.RootElement.TryGetProperty("version", out var said) && said.ValueKind == JsonValueKind.String
                ? said.GetString()
                : null;
            var bin = document.RootElement.TryGetProperty("bin", out var bins)
                && bins.ValueKind == JsonValueKind.Object
                && bins.TryGetProperty("claude", out var program)
                && program.ValueKind == JsonValueKind.String
                    ? program.GetString()
                    : null;

            if (!SemanticVersion.TryParse(named, out var version))
            {
                return new ClaudeCodePackage(true, null, null, NotOne(manifest, named is null ? "names no version" : $"names version '{named}', which is no version this tool reads", needed));
            }

            if (bin is not { Length: > 0 })
            {
                return new ClaudeCodePackage(true, version, null, NotOne(manifest, "names no program under bin.claude", needed));
            }

            return new ClaudeCodePackage(true, version, Path.Combine(package, bin.Replace('/', Path.DirectorySeparatorChar)), null);
        }
    }

    /// <summary>
    /// What the program answers of its own version, bounded: the first word of its answer that is a version - Claude
    /// Code answers <c>2.1.296 (Claude Code)</c> - or how asking it ended where it named none. A program that is not
    /// there, or cannot be started, is as little answered as one that says nothing, and the answer says which.
    /// </summary>
    public async Task<ClaudeCodeVersion> VersionAsync(string program, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(program);

        ProcessResult asked;

        try
        {
            asked = await _processRunner.RunAsync(
                    new ProcessRequest { FileName = program, Arguments = ["--version"], Environment = EveryStart, Timeout = AskBudget },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ProgramStartException ex)
        {
            return new ClaudeCodeVersion(null, ex.Message.TrimEnd('.'));
        }

        if (!asked.Succeeded)
        {
            return new ClaudeCodeVersion(null, HostProbes.Failure("asking it failed", asked));
        }

        return VersionIn(asked.StandardOutput) is { } version
            ? new ClaudeCodeVersion(version, null)
            : new ClaudeCodeVersion(
                null,
                HostProbes.Excerpt(asked.StandardOutput) is { Length: > 0 } said ? $"it printed '{said}', which names no version" : "it printed nothing");
    }

    /// <summary>
    /// The sessions of the package under <paramref name="root"/> among one reading's <paramref name="processes"/>, each
    /// with why it counts - the one rule of which processes an update of npm's Claude Code is refused by:
    /// <list type="bullet">
    /// <item>a process whose program, or a path on whose command line, lies under the package's directory - a native
    /// program the package holds and node running the package's script alike -, read as text first and, where the text
    /// does not say, with its links followed, as a program started through npm's link on Linux and macOS names the
    /// link;</item>
    /// <item>a process called <c>claude</c> whose program is node: Claude Code sets its process title, and on Linux and
    /// macOS node writes it over the process's name and its command line, so the script it runs is no longer named
    /// anywhere - and a claude node runs is npm's;</item>
    /// <item>a process called <c>claude</c> whose program could not be read - another user's on Linux, or one that
    /// ended -: it cannot be told apart from a session, and a change beside one takes its program from under it;</item>
    /// <item>a process that could be a session whose paths could not be followed within <see cref="FollowBudget"/>: a
    /// path on a network mount that answers nothing never fails, and an update waits on it no longer than that.</item>
    /// </list>
    /// A <c>claude</c> of any other installation whose program is read is none of these, whatever it is called.
    /// </summary>
    /// <param name="processes">The machine's processes, as one reading of its table holds them.</param>
    /// <param name="root">npm's global packages, as npm says it.</param>
    /// <param name="comparison">How this platform compares paths.</param>
    /// <param name="cancellationToken">Stops the wait on the links being followed.</param>
    /// <remarks>
    /// Links are followed only for a process that could be a session - one called <c>claude</c> or <c>node</c>, by its
    /// name or its program's - whose text did not already say, every path once per reading and the package's own first,
    /// off the caller's thread and within <see cref="FollowBudget"/>: a walk of the disk for every path of every process
    /// let one stale mount in an unrelated program hold the update for good.
    /// </remarks>
    public async Task<ClaudeCodeSessions> SessionsAsync(
        IReadOnlyList<SampledProcess> processes,
        string root,
        StringComparison comparison,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(processes);

        var package = PackageDirectory(root);
        var counted = new Dictionary<SampledProcess, string?>(ReferenceEqualityComparer.Instance);
        var unsettled = new List<(SampledProcess Process, IReadOnlyList<string> Paths)>();

        foreach (var process in processes)
        {
            var paths = PathsOf(process);

            if (paths.Any(path => PathContainment.IsStrictlyInside(package, path, comparison)))
            {
                counted[process] = null;
            }
            else if (Called(process, Claude) && process.Executable is { } program && HostProbes.ListsProcess(program, Node))
            {
                counted[process] = $"run by node '{program}'";
            }
            else if (Called(process, Claude) && process.Executable is null)
            {
                counted[process] = "whose program could not be read";
            }
            else if (Called(process, Claude) || Called(process, Node))
            {
                unsettled.Add((process, paths));
            }
        }

        if (unsettled.Count == 0)
        {
            return new ClaudeCodeSessions(counted);
        }

        var followed = await FollowAsync([package, .. unsettled.SelectMany(one => one.Paths).Distinct(StringComparer.Ordinal)], cancellationToken)
            .ConfigureAwait(false);
        var reached = followed.GetValueOrDefault(package);

        foreach (var (process, paths) in unsettled)
        {
            if (reached is not null && paths.Any(path => followed.GetValueOrDefault(path) is { } at && PathContainment.IsStrictlyInside(reached, at, comparison)))
            {
                counted[process] = null;
            }
            else if (!followed.ContainsKey(package) || paths.Any(path => !followed.ContainsKey(path)))
            {
                counted[process] = string.Create(
                    CultureInfo.InvariantCulture,
                    $"a path on whose command line could not be followed within {FollowBudget.TotalSeconds:0} seconds");
            }
        }

        return new ClaudeCodeSessions(counted);
    }

    /// <summary>
    /// <paramref name="paths"/> each made whole with every link along it followed, once each and in order, off the
    /// caller's thread: what was followed within <see cref="FollowBudget"/>, each path with where it leads - null where
    /// it names no path this machine reads -, and nothing for a path not reached in time. A following that never ends is
    /// left to end on its own; nothing waits on it.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, string?>> FollowAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken)
    {
        var followed = new ConcurrentDictionary<string, string?>(StringComparer.Ordinal);
        var following = Task.Run(
            () =>
            {
                foreach (var path in paths)
                {
                    followed[path] = Followed(path);
                }
            },
            CancellationToken.None);

        try
        {
            await following.WaitAsync(FollowBudget, _time, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // What was not followed in time is said by its absence from what is answered.
        }

        return new Dictionary<string, string?>(followed, StringComparer.Ordinal);
    }

    /// <summary>Whether a process is called <paramref name="name"/>, by the name it goes by or by its program's.</summary>
    private static bool Called(SampledProcess process, string name)
        => HostProbes.ListsProcess(process.Name, name) || (process.Executable is { } program && HostProbes.ListsProcess(program, name));

    /// <summary>The program a process runs, and every full path its command line names, in that order.</summary>
    private static IReadOnlyList<string> PathsOf(SampledProcess process)
    {
        var paths = new List<string>();

        if (process.Executable is { Length: > 0 } executable)
        {
            paths.Add(executable);
        }

        if (process.CommandLine is not { Length: > 0 } line)
        {
            return paths;
        }

        // A line with a quote left open is split at its spaces: each word is only a path to look at, never a program run.
        IReadOnlyList<string> words = CommandLineText.Split(line, out var split) is null
            ? split
            : line.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        paths.AddRange(words.Where(Path.IsPathFullyQualified));
        return paths;
    }

    /// <summary><paramref name="path"/> made whole with every link along it followed, or null where it names no path this machine reads.</summary>
    private string? Followed(string path)
    {
        try
        {
            return _fileSystem.ResolveLinks(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>The first word of <paramref name="output"/> that is a version, or null where none is.</summary>
    private static SemanticVersion? VersionIn(string output)
        => output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(word => SemanticVersion.TryParse(word, out var version) ? version : null)
            .FirstOrDefault(version => version is not null);

    /// <summary>
    /// The refusal of an installation whose <c>package.json</c> does not say what this tool starts - it could not be
    /// read, names no version, or names no program under <c>bin.claude</c> -: one this tool can never start, whatever
    /// npm thinks of it.
    /// </summary>
    private static string NotOne(string manifest, string why, string needed)
        => $"'{manifest}' {why}: this is not a Claude Code this tool can start. "
            + $"Install it again: {ClaudeCodeUpdateService.InstallCommand(needed)}";
}
