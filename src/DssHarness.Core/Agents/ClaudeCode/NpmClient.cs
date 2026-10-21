using DssHarness.Core.FileSystem;
using DssHarness.Core.Hosts;
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
/// <remarks>
/// Every program of one finding is looked for on one PATH - the one <paramref name="environment"/> answers - and by the
/// tool's one walk of a PATH (<see cref="ProcessRunner.ProgramsOnPath(IReadOnlyList{string}, string?, bool, Func{string, bool})"/>),
/// so npm and the node that starts it are found the way the tool finds any program, and on the same PATH.
/// </remarks>
/// <param name="processRunner">Runs npm and node.</param>
/// <param name="fileSystem">Looks at the scripts npm is made of.</param>
/// <param name="filePermissions">Says whether a file the PATH names is one this machine starts, as the runner asks it.</param>
/// <param name="platform">The system this is.</param>
/// <param name="environment">Reads an environment variable - the PATH; the process's own where none is given.</param>
public sealed class NpmClient(
    IProcessRunner processRunner,
    IFileSystem fileSystem,
    IFilePermissions filePermissions,
    IHostPlatform platform,
    Func<string, string?>? environment = null)
{
    /// <summary>How long asking npm anything may take, prefix and root alike.</summary>
    public static readonly TimeSpan AskBudget = TimeSpan.FromSeconds(30);

    private readonly IProcessRunner _processRunner = processRunner;
    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly IFilePermissions _filePermissions = filePermissions;
    private readonly IHostPlatform _platform = platform;
    private readonly Func<string, string?> _environment = environment ?? Environment.GetEnvironmentVariable;

    /// <summary>
    /// One npm to start: the program, and the arguments that lead every command - the <c>npm-cli.js</c> a batch file
    /// would have started, where npm was found as one.
    /// </summary>
    public sealed record NpmProgram(string Program, IReadOnlyList<string> Leading);

    /// <summary>What looking for npm found: the npm to start, or why there is none, with what was looked for.</summary>
    /// <param name="Npm">The npm to start; null where none can be.</param>
    /// <param name="Missing">
    /// Where none can be, the end of a sentence saying so - what stands on the PATH and what was looked for, on this
    /// system - such as <c>no npm is on the PATH. Looked for: npm</c>; null where npm was found.
    /// </param>
    public sealed record NpmFound(NpmProgram? Npm, string? Missing);

    /// <summary>Where npm keeps its global packages, as npm itself says, or why it would not say.</summary>
    /// <param name="Root">The directory; null where npm did not name one.</param>
    /// <param name="Problem">Where it did not, how asking it ended, in npm's own words; null where it named one.</param>
    public sealed record NpmRoot(string? Root, string? Problem);

    /// <summary>The request that asks npm where its global packages are, as every reader of the root asks it.</summary>
    public static ProcessRequest RootRequest(NpmProgram npm)
    {
        ArgumentNullException.ThrowIfNull(npm);

        return new ProcessRequest { FileName = npm.Program, Arguments = [.. npm.Leading, "root", "-g"], Timeout = AskBudget };
    }

    /// <summary>
    /// What a run that needs npm and finds none was looking for, on this system: <c>npm</c> on Linux and macOS, and on
    /// Windows the program, the batch file and what that file starts.
    /// </summary>
    public string LookedFor
        => _platform.Current == PlatformId.Windows
            ? "npm.exe and npm.cmd, and the node.exe and npm-cli.js npm.cmd starts"
            : "npm";

    /// <summary>Finds npm: what to start it as, or why there is nothing to start.</summary>
    public async Task<NpmFound> FindAsync(CancellationToken cancellationToken = default)
    {
        var path = _environment(ProcessRunner.PathVariable);

        if (_platform.Current != PlatformId.Windows)
        {
            // A script the system starts: found for its name and started for it.
            return ProcessRunner.ProgramOnPath("npm", path, windows: false, _filePermissions.IsExecutable) is { } program
                ? new NpmFound(new NpmProgram(program, []), null)
                : new NpmFound(null, $"no npm is on the PATH. Looked for: {LookedFor}");
        }

        // Within each directory npm.exe - a program - beats npm.cmd; the first of either the PATH names is the one taken,
        // as the system itself resolves a name. Where the PATH gives only the batch file, the tool does what that file
        // does, so the batch file is never started.
        var found = ProcessRunner.ProgramOnPath(["npm.exe", "npm.cmd"], path, windows: true, _filePermissions.IsExecutable);

        return found switch
        {
            null => new NpmFound(null, $"no npm is on the PATH. Looked for: {LookedFor}"),
            _ when string.Equals(Path.GetExtension(found), ".cmd", StringComparison.OrdinalIgnoreCase)
                => await BesideCmdAsync(found, path, cancellationToken).ConfigureAwait(false),
            _ => new NpmFound(new NpmProgram(found, []), null),
        };
    }

    /// <summary>
    /// What <c>npm.cmd</c> at <paramref name="cmd"/> starts, done as the tool does it: the <c>node.exe</c> beside it, or
    /// <c>node</c> from <paramref name="path"/> - the PATH <c>npm.cmd</c> was found on -, starting the <c>npm-cli.js</c>
    /// of the prefix npm names - the npm a person installed over the one Node.js brought - or the one beside
    /// <c>npm.cmd</c> where the prefix holds none, as <c>npm.cmd</c> itself passes it over. Where node is nowhere, or
    /// the <c>npm-cli.js</c> it would start is not there, nothing can be started, and the answer says which.
    /// </summary>
    private async Task<NpmFound> BesideCmdAsync(string cmd, string? path, CancellationToken cancellationToken)
    {
        var beside = Path.GetDirectoryName(cmd) ?? ".";
        var node = Path.Combine(beside, "node.exe");

        if (!_filePermissions.IsExecutable(node))
        {
            if (ProcessRunner.ProgramOnPath("node", path, windows: true, _filePermissions.IsExecutable) is not { } onPath)
            {
                return new NpmFound(
                    null,
                    $"'{cmd}' is on the PATH, but no node.exe is beside it or on the PATH to start what it starts. Looked for: {LookedFor}");
            }

            node = onPath;
        }

        var asked = Path.Combine(beside, "node_modules", "npm", "bin", "npm-prefix.js");
        var prefix = await AskAsync(node, [asked], cancellationToken).ConfigureAwait(false);

        if (prefix is { Length: > 0 })
        {
            var installed = Path.Combine(prefix, "node_modules", "npm", "bin", "npm-cli.js");

            if (_fileSystem.FileExists(installed))
            {
                return new NpmFound(new NpmProgram(node, [installed]), null);
            }
        }

        var own = Path.Combine(beside, "node_modules", "npm", "bin", "npm-cli.js");

        return _fileSystem.FileExists(own)
            ? new NpmFound(new NpmProgram(node, [own]), null)
            : new NpmFound(
                null,
                $"'{cmd}' is on the PATH, but the npm-cli.js it starts is in neither the prefix npm names nor beside it ('{own}'). Looked for: {LookedFor}");
    }

    /// <summary>Where npm's global packages are, as npm itself says (<c>npm root -g</c>), or how asking it ended.</summary>
    public async Task<NpmRoot> RootAsync(NpmProgram npm, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(npm);

        ProcessResult asked;

        try
        {
            asked = await _processRunner.RunAsync(RootRequest(npm), cancellationToken).ConfigureAwait(false);
        }
        catch (ProgramStartException ex)
        {
            return new NpmRoot(null, ex.Message);
        }

        return ReadRoot(asked);
    }

    /// <summary>
    /// The directory a run of <see cref="RootRequest"/> named, or how it ended where it named none: the one reading of
    /// npm's answer, for the root asked before a change and the one asked since.
    /// </summary>
    public static NpmRoot ReadRoot(ProcessResult asked)
    {
        ArgumentNullException.ThrowIfNull(asked);

        if (!asked.Succeeded)
        {
            return new NpmRoot(null, HostProbes.Failure("'npm root -g' failed", asked));
        }

        return asked.StandardOutput.Trim() is { Length: > 0 } said
            ? new NpmRoot(said, null)
            : new NpmRoot(null, "'npm root -g' printed nothing");
    }

    /// <summary>
    /// One bounded question of node, its answer trimmed; null where it would not answer - which <c>npm.cmd</c> itself
    /// passes over for the <c>npm-cli.js</c> beside it, as this does.
    /// </summary>
    private async Task<string?> AskAsync(string program, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        try
        {
            var asked = await _processRunner.RunAsync(
                    new ProcessRequest { FileName = program, Arguments = arguments, Timeout = AskBudget },
                    cancellationToken)
                .ConfigureAwait(false);

            return asked.Succeeded ? asked.StandardOutput.Trim() : null;
        }
        catch (ProgramStartException)
        {
            return null;
        }
    }
}
