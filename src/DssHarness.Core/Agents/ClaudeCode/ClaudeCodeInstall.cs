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
public sealed class ClaudeCodeInstall(IFileSystem fileSystem, IProcessRunner processRunner)
{
    /// <summary>The package npm installs, by its own name.</summary>
    public const string PackageId = "@anthropic-ai/claude-code";

    /// <summary>The variable that stops Claude Code's own background update, which every <c>claude</c> this tool starts is given.</summary>
    public const string AutoUpdaterVariable = "DISABLE_AUTOUPDATER";

    /// <summary>How long the program may take to answer its own version.</summary>
    public static readonly TimeSpan AskBudget = TimeSpan.FromSeconds(30);

    /// <summary>
    /// What every <c>claude</c> this tool starts has in its environment, beside what it inherits: Claude Code's own
    /// background update turned off, so only this tool's update moves an installation - its <c>--version</c> asked here,
    /// and every launch the clients make, which take their environment from this.
    /// </summary>
    public static IReadOnlyDictionary<string, string?> EveryStart { get; } =
        new Dictionary<string, string?>(StringComparer.Ordinal) { [AutoUpdaterVariable] = "1" }.AsReadOnly();

    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly IProcessRunner _processRunner = processRunner;

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
    /// Whether a process runs a program of the package under <paramref name="root"/>: the program it runs, or a path its
    /// command line names - links followed, as a program started through npm's link on Linux and macOS names the link -
    /// lies under the package's directory. That is a native program the package holds and a <c>node</c> running the
    /// package's script alike; a <c>claude</c> of any other installation is not one, whatever it is called.
    /// </summary>
    /// <param name="root">npm's global packages, as npm says it.</param>
    /// <param name="comparison">How this platform compares paths.</param>
    /// <returns>The test, asked of each process of one reading of the table; the package's directory is followed once.</returns>
    public Func<SampledProcess, bool> RunsFrom(string root, StringComparison comparison)
    {
        var package = Followed(PackageDirectory(root));

        return process => package is not null
            && PathsOf(process).Any(path => Followed(path) is { } followed && PathContainment.IsStrictlyInside(package, followed, comparison));
    }

    /// <summary>
    /// Whether a process cannot be told apart from a session of the package: the table could not read the program it
    /// runs - another user's process, or a table read by names alone - and it is called <c>claude</c>. Counted as one,
    /// since a change made beside a session takes the program from under it, and refusing tells the person which.
    /// </summary>
    public static bool CannotBeToldApart(SampledProcess process)
    {
        ArgumentNullException.ThrowIfNull(process);

        return process.Executable is null && HostProbes.ListsProcess(process.Name, "claude");
    }

    /// <summary>The program a process runs, and every full path its command line names, in that order.</summary>
    private static IEnumerable<string> PathsOf(SampledProcess process)
    {
        if (process.Executable is { Length: > 0 } executable)
        {
            yield return executable;
        }

        if (process.CommandLine is not { Length: > 0 } line)
        {
            yield break;
        }

        // A line with a quote left open is split at its spaces: each word is only a path to look at, never a program run.
        IReadOnlyList<string> words = CommandLineText.Split(line, out var split) is null
            ? split
            : line.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        foreach (var word in words.Where(Path.IsPathFullyQualified))
        {
            yield return word;
        }
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
