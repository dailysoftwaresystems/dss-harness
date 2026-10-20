using System.Text.Json;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Processes;

namespace DssHarness.Core.Agents.ClaudeCode;

/// <summary>What npm's global place holds of Claude Code, as its own package says it.</summary>
/// <param name="Version">The version the package's <c>package.json</c> names, where it names one.</param>
/// <param name="Program">The program its <c>bin.claude</c> names, under the package's directory.</param>
/// <param name="Problem">Why what is installed is none this tool can start, in its own words; null where it is one.</param>
public sealed record ClaudeCodePackage(string? Version, string? Program, string? Problem)
{
    /// <summary>Whether what is installed is a Claude Code this tool can start.</summary>
    public bool Startable => Problem is null && Program is not null;
}

/// <summary>
/// The Claude Code npm installs, as its package says it: the directory <c>@anthropic-ai/claude-code</c> under npm's
/// global packages, its <c>package.json</c> naming the version and, under <c>bin</c>, the one program this tool
/// starts - by its full path, never a launcher, and never a batch file.
/// </summary>
/// <param name="fileSystem">Reads the package's files.</param>
/// <param name="processRunner">Asks the program's own version.</param>
public sealed class ClaudeCodeInstall(IFileSystem fileSystem, IProcessRunner processRunner)
{
    /// <summary>The package npm installs, by its own name.</summary>
    public const string PackageId = "@anthropic-ai/claude-code";

    /// <summary>How long the program may take to answer its own version.</summary>
    public static readonly TimeSpan AskBudget = TimeSpan.FromSeconds(30);

    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly IProcessRunner _processRunner = processRunner;

    /// <summary>
    /// What <paramref name="root"/> - npm's global packages, as npm says it - holds of Claude Code: nothing, where the
    /// package is not there; the version and the program, where it is; or why it is none this tool can start, where
    /// its <c>package.json</c> cannot be read or names no program under <c>bin.claude</c>.
    /// </summary>
    public ClaudeCodePackage Read(string root, string needed)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        var package = Path.Combine(root, PackageId.Replace('/', Path.DirectorySeparatorChar));
        var manifest = Path.Combine(package, "package.json");

        if (!_fileSystem.FileExists(manifest))
        {
            return new ClaudeCodePackage(null, null, null);
        }

        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(_fileSystem.ReadAllText(manifest));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // A package.json that cannot be read says nothing certain of the program, and an installation this tool
            // cannot tell one of is refused as one it cannot start.
            return new ClaudeCodePackage(null, null, NotOne(manifest, needed));
        }

        using (document)
        {
            var version = document.RootElement.TryGetProperty("version", out var named) && named.ValueKind == JsonValueKind.String
                ? named.GetString()
                : null;
            var bin = document.RootElement.TryGetProperty("bin", out var bins)
                && bins.ValueKind == JsonValueKind.Object
                && bins.TryGetProperty("claude", out var program)
                && program.ValueKind == JsonValueKind.String
                    ? program.GetString()
                    : null;

            if (bin is not { Length: > 0 })
            {
                return new ClaudeCodePackage(version, null, NotOne(manifest, needed));
            }

            return new ClaudeCodePackage(version, Path.Combine(package, bin.Replace('/', Path.DirectorySeparatorChar)), null);
        }
    }

    /// <summary>
    /// What the program answers of its own version, bounded; null where it would not answer or could not be started -
    /// a program that is not there is as little answered as one that says nothing.
    /// </summary>
    public async Task<string?> VersionAsync(string program, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(program);

        try
        {
            var asked = await _processRunner.RunAsync(
                    new ProcessRequest { FileName = program, Arguments = ["--version"], Timeout = AskBudget },
                    cancellationToken)
                .ConfigureAwait(false);

            return asked.Succeeded ? asked.StandardOutput.Trim() : null;
        }
        catch (ProgramStartException)
        {
            return null;
        }
    }

    /// <summary>
    /// The refusal of an installation whose <c>package.json</c> names no program under <c>bin.claude</c> - one this
    /// tool can never start, whatever npm thinks of it.
    /// </summary>
    private static string NotOne(string manifest, string needed)
        => $"'{manifest}' names no program under bin.claude: this is not a Claude Code this tool can start. "
            + $"Install it again: npm install -g {PackageId}@{needed}";
}
