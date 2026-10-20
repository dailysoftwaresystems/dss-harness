using DssHarness.Core.Execution;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Hosts;
using DssHarness.Core.Platform;
using DssHarness.Core.Processes;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;
using DssHarness.Core.Secrets;

namespace DssHarness.Core.Agents.ClaudeCode;

/// <summary>
/// What asking about one Claude Code found: whether it is one this tool can run, the one line that says what it is,
/// and - where it is not - the refusal in the tool's usual form, with the code to exit with.
/// </summary>
/// <param name="Ready">Whether a run that needs it may start.</param>
/// <param name="ExitCode">The code a run refused by it exits with; 0 where it is ready.</param>
/// <param name="Message">The one line: what it is and where, or the refusal.</param>
/// <param name="Details">The lines under it: each process by its id, npm's own last lines, what was looked for.</param>
public sealed record ClaudeCodeAnswer(bool Ready, int ExitCode, string Message, IReadOnlyList<string> Details)
{
    /// <summary>One Claude Code a run may start, said in the line a run says when it starts.</summary>
    public static ClaudeCodeAnswer ReadyAt(string version, string program)
        => new(true, 0, $"claude {version}, installed with npm: '{program}'", []);
}

/// <summary>
/// Keeps the Claude Code npm installs ready for a run that needs it: found by asking npm, started by the full path its
/// package names and never a launcher, installed where npm has none and brought up to the one version this tool needs
/// - never past it, never with sudo, never beside a session that is running, and never a program a person named. The
/// change itself is the shared <see cref="ProgramUpdate"/> sequence <c>update-tool</c> has: never stopped once begun,
/// bounded, under the machine-wide lock on npm's global place, read back before it is said done.
/// </summary>
/// <param name="processes">The machine's processes, read before a change so no session is taken from under.</param>
/// <param name="processRunner">Runs npm and the program.</param>
/// <param name="fileSystem">Reads the package.</param>
/// <param name="platform">The system this is.</param>
/// <param name="process">The process asking, never one of the running sessions.</param>
/// <param name="keys">The agent clients' key slot, where a program a person named is appointed.</param>
/// <param name="environment">Reads an environment variable; the process's own where none is given.</param>
/// <param name="npm">How npm is found and run; one of its own where none is given.</param>
/// <param name="install">How the package is read; one of its own where none is given.</param>
public sealed class ClaudeCodeUpdateService(
    IProcessTable processes,
    IProcessRunner processRunner,
    IFileSystem fileSystem,
    IHostPlatform platform,
    IProcessIdentity process,
    AgentKeyStore? keys = null,
    Func<string, string?>? environment = null,
    NpmClient? npm = null,
    ClaudeCodeInstall? install = null)
{
    /// <summary>The version this tool needs: the stable channel's on the day the client was built, by its number and never a channel's name.</summary>
    public const string Needed = "2.1.287";

    /// <summary>The name in the tool's own environment that turns installing and updating off.</summary>
    public const string UpdatesVariable = "DISABLE_UPDATES";

    /// <summary>The name the slot holds a program a person named under.</summary>
    public const string ExecutableVariable = "CLAUDE_EXECUTABLE";

    /// <summary>The command that installs Claude Code and brings one up to date, as a refusal says it and a person runs it.</summary>
    public static string InstallCommand(string version = Needed)
        => $"npm install -g {ClaudeCodeInstall.PackageId}@{version}";

    private readonly IProcessTable _processes = processes;
    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly IProcessIdentity _process = process;
    private readonly AgentKeyStore? _keys = keys;
    private readonly Func<string, string?> _environment = environment ?? Environment.GetEnvironmentVariable;
    private readonly NpmClient _npm = npm ?? new NpmClient(processRunner, fileSystem, platform, environment);
    private readonly ClaudeCodeInstall _install = install ?? new ClaudeCodeInstall(fileSystem, processRunner);
    private readonly ProgramUpdate _updates = new(processRunner, platform);

    /// <summary>What a program a person names is refused with, when it is not one this tool can start.</summary>
    private static readonly string[] BatchExtensions = [".cmd", ".bat", ".ps1"];

    private static bool IsBatch(string program)
        => BatchExtensions.Contains(Path.GetExtension(program), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Makes the Claude Code a run needs ready, or the refusal that says why it cannot be: a program a person named
    /// is only read, never moved; where installing and updating are turned off, what is installed is only said; where
    /// they are on, npm is asked what it holds and the change is run where it must be - never beside a session.
    /// </summary>
    /// <param name="layout">The repository's layout, where the slot is; null where no slot is to be asked.</param>
    /// <param name="appointed">Every name the configuration appoints, of providers and clients both.</param>
    /// <param name="update">The client's <c>update</c> key: on where nothing is said.</param>
    /// <param name="steps">The steps that need the client, as a refusal names them; empty where the asker names none.</param>
    /// <param name="cancellationToken">Stops everything before the change; the change itself, once begun, is never stopped.</param>
    public async Task<ClaudeCodeAnswer> ReadyAsync(
        HarnessLayout? layout,
        IReadOnlyList<AppointedKey> appointed,
        bool update,
        string steps,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(appointed);

        // A program a person named is that person's own choice: started as named, asked nothing of npm, and neither
        // installed, nor updated, nor second-guessed by this tool. The slot need hold no such name; where its files do
        // not read, that is the configuration's problem, said here as it is said anywhere the slot is asked.
        if (layout is not null && _keys is not null)
        {
            var present = _keys.Present(layout);

            if (present.Problems is [var broken, ..])
            {
                return Refused(HarnessExit.ConfigInvalid, broken);
            }

            if (present.Holds(ExecutableVariable)
                && _keys.Value(layout, appointed, ExecutableVariable) is { Problem: null, Value: { Length: > 0 } named })
            {
                return await NamedAsync(named, cancellationToken).ConfigureAwait(false);
            }
        }

        var npm = await _npm.FindAsync(cancellationToken).ConfigureAwait(false);

        if (npm is null)
        {
            return Refused(
                HarnessExit.ToolMissing,
                $"this run needs a Claude model through the client 'claude-code'{steps}, and no npm is on the PATH. "
                + $"Looked for: {NpmClient.LookedFor}");
        }

        var root = await _npm.RootAsync(npm, cancellationToken).ConfigureAwait(false);

        if (root is null)
        {
            return Refused(
                HarnessExit.ToolMissing,
                $"npm was found but would not say where its global packages are, so Claude Code could not be looked for. "
                + $"{InstallCommand()} by hand installs the one this tool needs ({Needed}).");
        }

        var held = _install.Read(root, Needed);

        if (held.Problem is { } problem)
        {
            return Refused(HarnessExit.ToolMissing, problem);
        }

        // Nothing installed, or one older than the tool needs: what the change is for. Everything at or above the
        // version needed is left alone - the tool never moves a Claude Code that serves, and never one past what it
        // needs.
        if (held is { Version: { } version } && AtLeast(version, Needed))
        {
            // What the program answers of its own version has to agree with the package's: a pair that disagrees is
            // half an installation, and half an installation is refused as one that did not take.
            var answered = await _install.VersionAsync(held.Program!, cancellationToken).ConfigureAwait(false);

            if (answered is not { Length: > 0 })
            {
                return Refused(
                    HarnessExit.ToolMissing,
                    $"'{held.Program}' would not answer its own --version, so this tool cannot start it. "
                    + $"Install it again: {InstallCommand()}; an installation whose step after the install did not run "
                    + "(npm install --ignore-scripts) is the usual cause.");
            }

            return answered == version
                ? ClaudeCodeAnswer.ReadyAt(answered, held.Program!)
                : Refused(
                    HarnessExit.ToolMissing,
                    $"'{held.Program}' says it is claude {answered} and its package says {version}, and the two do not agree. "
                    + $"Install it again: {InstallCommand()}; an installation whose step after the install did not run "
                    + "(npm install --ignore-scripts) is the usual cause.");
        }

        if (!update || _environment(UpdatesVariable) is { Length: > 0 })
        {
            var turnedOff = !update
                ? "the client's update key is false"
                : $"{UpdatesVariable} is set in this tool's own environment";

            return Refused(
                HarnessExit.ToolMissing,
                $"installing and updating Claude Code are turned off ({turnedOff}); "
                    + (held.Version is { } installed ? $"npm holds claude {installed}" : "npm holds no Claude Code")
                    + $", and this tool needs {Needed}. Run it by hand: {InstallCommand()}");
        }

        // A session reads parts of its executable from disk as it works, and npm deletes the previous binary when it
        // installs over it: on Windows the change fails on the locked file, and elsewhere it takes the program from
        // under the session. Every claude this tool starts has DISABLE_AUTOUPDATER=1, so no session of the tool's own
        // moves the installation by itself either: only this sequence does.
        var table = await _processes.ReadAsync(cancellationToken).ConfigureAwait(false);
        var running = _updates.Running(table, one => HostProbes.ListsProcess(one.Name, "claude"), _process.CurrentId);

        if (running.Count > 0)
        {
            return Refused(
                HarnessExit.Refused,
                $"a session of Claude Code is running on this machine ({string.Join(", ", running.Select(one => $"pid {one.Id}"))}); "
                + "start the run again when they have ended.");
        }

        using var held_ = _updates.TryHold(root);

        if (held_ is null)
        {
            return Refused(
                HarnessExit.Refused,
                $"another update is changing npm's '{root}' on this machine; run again once it has ended.");
        }

        var change = new ProcessRequest
        {
            FileName = npm.Program,
            Arguments = [.. npm.Leading, "install", "-g", $"{ClaudeCodeInstall.PackageId}@{Needed}"],
            Timeout = ToolPackage.ChangeBudget,
        };
        var listSince = new ProcessRequest
        {
            FileName = npm.Program,
            Arguments = [.. npm.Leading, "root", "-g"],
            Timeout = NpmClient.AskBudget,
        };

        var moved = await _updates.ChangeAsync(held_, change, listSince, _ => ListedRelease.Read(null)).ConfigureAwait(false);

        if (!moved.Ran.Succeeded)
        {
            // The registry or the download not reached is the network's, and trying again can help; no right to write
            // npm's place is the machine's, and the tool never runs sudo to reach past it.
            var output = OutputOf(moved.Ran);
            var elevated = output.Contains("EACCES", StringComparison.Ordinal);
            var network = moved.Ran.TimedOut
                || output.Contains("ENOTFOUND", StringComparison.Ordinal)
                || output.Contains("ECONNRESET", StringComparison.Ordinal)
                || output.Contains("ETIMEDOUT", StringComparison.Ordinal)
                || output.Contains("network", StringComparison.Ordinal);

            return Refused(
                network ? HarnessExit.Incomplete : HarnessExit.ToolMissing,
                (network
                        ? $"npm reached neither the registry nor the download ({HostProbes.Failure("the install was not reached", moved.Ran)}); running it again can help"
                        : $"npm could not change what it holds ({HostProbes.Failure("the install failed", moved.Ran)})")
                    + (elevated ? "; this tool never runs sudo" : string.Empty)
                    + $". Run it by hand: {InstallCommand()}.");
        }

        // The change is done only where the package and the program both say the version needed or a later one.
        var now = _install.Read(root, Needed);

        if (now is { Version: { } release } && AtLeast(release, Needed))
        {
            var answered = await _install.VersionAsync(now.Program!, CancellationToken.None).ConfigureAwait(false);

            if (answered is { Length: > 0 } && AtLeast(answered, Needed))
            {
                return new ClaudeCodeAnswer(
                    true,
                    0,
                    held.Version is { } older
                        ? $"claude {older} is older than this tool needs ({Needed}): updated with '{InstallCommand()}'; it now says {answered}"
                        : $"npm has no Claude Code installed: installed {answered} with '{InstallCommand()}'",
                    []);
            }
        }

        return Refused(
            HarnessExit.ToolMissing,
            $"npm ran, and Claude Code is still missing or older than this tool needs: "
                + $"{(now.Version is { } found ? $"npm holds claude {found}" : "npm holds no Claude Code")}, and this tool needs {Needed}. "
                + $"The command that ran: '{InstallCommand()}'. "
                + $"A Node.js older than 22, which the package asks for, and an installation whose step after the install did not run (npm install --ignore-scripts) are the causes.",
            [HostProbes.Excerpt(OutputOf(moved.Ran))]);
    }

    /// <summary>A program a person named: read for what it is, and refused where it is not one this tool can start.</summary>
    private async Task<ClaudeCodeAnswer> NamedAsync(string program, CancellationToken cancellationToken)
    {
        if (IsBatch(program))
        {
            return Refused(
                HarnessExit.ToolMissing,
                $"'{program}' is a batch file, which is never started (its arguments would be parsed twice); name the program it starts");
        }

        var answered = await _install.VersionAsync(program, cancellationToken).ConfigureAwait(false);

        if (answered is not { Length: > 0 })
        {
            return Refused(
                HarnessExit.ToolMissing,
                $"'{program}', the program the slot names, would not answer its --version. "
                + $"Update it by hand, or take {ExecutableVariable} out of the slot");
        }

        return AtLeast(answered, Needed)
            ? new ClaudeCodeAnswer(true, 0, $"claude {answered}, named by the slot: '{program}'", [])
            : Refused(
                HarnessExit.ToolMissing,
                $"'{program}' is claude {answered}, older than this tool needs ({Needed}), and a program a person named is never updated. "
                + $"Update it by hand, or take {ExecutableVariable} out of the slot");
    }

    /// <summary>Whether <paramref name="version"/> is <paramref name="needed"/> or a later one.</summary>
    private static bool AtLeast(string version, string needed)
        => SemanticVersion.TryParse(version, out var held)
            && SemanticVersion.TryParse(needed, out var floor)
            && SemanticVersion.Compare(held, floor) >= 0;

    private static ClaudeCodeAnswer Refused(int exitCode, string message, IReadOnlyList<string>? details = null)
        => new(false, exitCode, message, details ?? []);

    private static string OutputOf(ProcessResult result)
        => string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError;
}
