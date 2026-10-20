using DssHarness.Core.FileSystem;
using DssHarness.Core.Platform;
using DssHarness.Core.Processes;

namespace DssHarness.Core.Agents.ClaudeCode;

/// <summary>
/// The npm one command is started through, as this tool finds it: on Linux and macOS a script the system starts; on
/// Windows a program - never <c>npm.cmd</c>, which is a batch file whose arguments cmd.exe would parse a second time -
/// and where the PATH gives only the batch file, the tool does exactly what that file does: it starts the
/// <c>node.exe</c> beside it (or <c>node</c> from the PATH), asks npm for its prefix, and starts the
/// <c>npm-cli.js</c> of the npm a person installed over the one Node.js brought, where there is one, and the one
/// beside <c>npm.cmd</c> where there is not.
/// </summary>
/// <param name="processRunner">Runs npm and finds programs on the PATH.</param>
/// <param name="fileSystem">Looks at what the PATH names.</param>
/// <param name="platform">The system this is.</param>
/// <param name="environment">Reads an environment variable; the process's own where none is given.</param>
public sealed class NpmClient(
    IProcessRunner processRunner,
    IFileSystem fileSystem,
    IHostPlatform platform,
    Func<string, string?>? environment = null)
{
    /// <summary>How long asking npm anything may take, prefix and root alike.</summary>
    public static readonly TimeSpan AskBudget = TimeSpan.FromSeconds(30);

    private readonly IProcessRunner _processRunner = processRunner;
    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly IHostPlatform _platform = platform;
    private readonly Func<string, string?> _environment = environment ?? Environment.GetEnvironmentVariable;

    /// <summary>What was looked for, for the refusal of a run that needs npm and finds none.</summary>
    public const string LookedFor =
        "npm.exe and npm.cmd, and the node.exe and npm-cli.js npm.cmd starts. This tool installs Claude Code and brings "
        + "it up to date with npm. Install Node.js 22 or later, which brings npm, and start the run again";

    /// <summary>
    /// One npm to start: the program, and the arguments that lead every command - the <c>npm-cli.js</c> a batch file
    /// would have started, where npm was found as one.
    /// </summary>
    public sealed record NpmProgram(string Program, IReadOnlyList<string> Leading);

    /// <summary>Finds npm: where it is, as what to start it; or <see langword="null"/>, and nothing was found.</summary>
    public async Task<NpmProgram?> FindAsync(CancellationToken cancellationToken = default)
    {
        if (_platform.Current != PlatformId.Windows)
        {
            // A script the system starts: found for its name and started for it.
            return _processRunner.FindExecutable("npm") is { } program ? new NpmProgram(program, []) : null;
        }

        // Within each directory, npm.exe - a program - beats npm.cmd; the first of either the PATH names is the one
        // taken, as the system itself resolves a name. Where the PATH gives only the batch file, the tool does what
        // that file does, so the batch file is never started.
        foreach (var directory in PathDirectories())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var exe = Path.Combine(directory, "npm.exe");

            if (_fileSystem.FileExists(exe))
            {
                return new NpmProgram(exe, []);
            }

            var cmd = Path.Combine(directory, "npm.cmd");

            if (_fileSystem.FileExists(cmd))
            {
                return await BesideCmdAsync(cmd, cancellationToken).ConfigureAwait(false);
            }
        }

        return null;
    }

    /// <summary>
    /// What <c>npm.cmd</c> at <paramref name="cmd"/> starts, done as the tool does it: the <c>node.exe</c> beside it,
    /// or <c>node</c> from the PATH, starting the <c>npm-cli.js</c> of the prefix npm names - the npm a person
    /// installed over the one Node.js brought - or the one beside <c>npm.cmd</c> where the prefix holds none. Where
    /// node is nowhere, nothing can be started: null, and the refusal of a run that needs npm says what was looked for.
    /// </summary>
    private async Task<NpmProgram?> BesideCmdAsync(string cmd, CancellationToken cancellationToken)
    {
        var beside = Path.GetDirectoryName(cmd) ?? ".";
        var node = Path.Combine(beside, "node.exe");

        if (!_fileSystem.FileExists(node))
        {
            node = _processRunner.FindExecutable("node") ?? node;

            if (!_fileSystem.FileExists(node))
            {
                return null;
            }
        }

        var asked = Path.Combine(beside, "node_modules", "npm", "bin", "npm-prefix.js");
        var prefix = await AskAsync(node, [asked], cancellationToken).ConfigureAwait(false);

        if (prefix is { Length: > 0 })
        {
            var installed = Path.Combine(prefix, "node_modules", "npm", "bin", "npm-cli.js");

            if (_fileSystem.FileExists(installed))
            {
                return new NpmProgram(node, [installed]);
            }
        }

        return new NpmProgram(node, [Path.Combine(beside, "node_modules", "npm", "bin", "npm-cli.js")]);
    }

    /// <summary>Where npm's global packages are, as npm itself says (<c>npm root -g</c>); null where npm would not say.</summary>
    public async Task<string?> RootAsync(NpmProgram npm, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(npm);

        var asked = await RunAsync(npm, ["root", "-g"], cancellationToken).ConfigureAwait(false);
        var said = asked.Succeeded ? asked.StandardOutput.Trim() : null;

        return said is { Length: > 0 } ? said : null;
    }

    /// <summary>Runs one npm command, bounded by <see cref="AskBudget"/>.</summary>
    public Task<ProcessResult> RunAsync(NpmProgram npm, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(npm);
        ArgumentNullException.ThrowIfNull(arguments);

        return _processRunner.RunAsync(
            new ProcessRequest
            {
                FileName = npm.Program,
                Arguments = [.. npm.Leading, .. arguments],
                Timeout = AskBudget,
            },
            cancellationToken);
    }

    /// <summary>One bounded question of node, its answer trimmed; null where it would not answer.</summary>
    private async Task<string?> AskAsync(string program, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var asked = await _processRunner.RunAsync(
                new ProcessRequest { FileName = program, Arguments = arguments, Timeout = AskBudget },
                cancellationToken)
            .ConfigureAwait(false);

        return asked.Succeeded ? asked.StandardOutput.Trim() : null;
    }

    private IEnumerable<string> PathDirectories()
    {
        var path = _environment("PATH") ?? string.Empty;

        return path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(directory => directory.Trim('"'));
    }
}
