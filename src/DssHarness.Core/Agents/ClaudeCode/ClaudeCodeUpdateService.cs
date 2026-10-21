using System.Diagnostics;
using System.Globalization;
using DssHarness.Core.Configuration;
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
/// <param name="Details">The lines under it: what npm holds since a change that failed, npm's own last lines.</param>
public sealed record ClaudeCodeAnswer(bool Ready, int ExitCode, string Message, IReadOnlyList<string> Details)
{
    /// <summary>One Claude Code a run may start, said in the line a run says when it starts: the version its program answered.</summary>
    public static ClaudeCodeAnswer ReadyAt(SemanticVersion version, string program)
    {
        ArgumentNullException.ThrowIfNull(version);

        return new(true, 0, $"claude {version}, installed with npm: '{program}'", []);
    }
}

/// <summary>
/// Keeps the Claude Code npm installs ready for a run that needs it: found by asking npm, started by the full path its
/// package names and never a launcher, installed where npm has none and brought up to the one version this tool needs
/// - never past it, never with sudo, never beside a session of that installation that is running, and never a program
/// a person named. The change itself is the shared <see cref="ProgramUpdate"/> sequence <c>update-tool</c> has: never
/// stopped once begun, bounded, under the machine-wide lock on npm's global place, and read back - after a change that
/// failed as after one that ran - so what npm holds since is always said.
/// </summary>
/// <param name="processes">The machine's processes, read before a change so no session is taken from under.</param>
/// <param name="processRunner">Runs npm and the program.</param>
/// <param name="fileSystem">Reads the package.</param>
/// <param name="filePermissions">Says whether a file the PATH names is one this machine starts, as npm is looked for.</param>
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
    IFilePermissions filePermissions,
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

    /// <summary>
    /// Whether installing and updating are on for a client: its entry's <c>update</c> key, and on where the entry says
    /// nothing or there is no entry - installing and updating are what the tool does unless a machine's people keep the
    /// version themselves.
    /// </summary>
    /// <param name="client">The configuration's entry for the client, under <c>agentClients</c>; null where it declares none.</param>
    public static bool UpdatesOn(AgentClientConfig? client) => client?.Update ?? true;

    /// <summary>The version needed, as the one comparison of versions reads it.</summary>
    private static readonly SemanticVersion NeededVersion = SemanticVersion.TryParse(Needed, out var needed)
        ? needed
        : throw new InvalidOperationException($"'{Needed}' is no version.");

    /// <summary>What every refusal of a change that ran and left no Claude Code this tool can start names as its causes.</summary>
    private const string Causes =
        "A Node.js older than 22, which the package asks for, and an installation whose step after the install did not run (npm install --ignore-scripts) are the causes.";

    private readonly IProcessTable _processes = processes;
    private readonly IHostPlatform _platform = platform;
    private readonly IProcessIdentity _process = process;
    private readonly AgentKeyStore? _keys = keys;
    private readonly Func<string, string?> _environment = environment ?? Environment.GetEnvironmentVariable;
    private readonly NpmClient _npm = npm ?? new NpmClient(processRunner, fileSystem, filePermissions, platform, environment);
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
    /// <param name="client">The configuration's entry for the client being made ready; null where it declares none (<see cref="UpdatesOn"/>).</param>
    /// <param name="steps">The steps that need the client, as a refusal names them; empty where the asker names none.</param>
    /// <param name="cancellationToken">Stops everything before the change; the change itself, once begun, is never stopped.</param>
    public async Task<ClaudeCodeAnswer> ReadyAsync(
        HarnessLayout? layout,
        IReadOnlyList<AppointedKey> appointed,
        AgentClientConfig? client,
        string steps,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(appointed);

        // A program a person named is that person's own choice: started as named, asked nothing of npm, and neither
        // installed, nor updated, nor second-guessed by this tool. The slot need hold no such name; where its files do
        // not read, or the name it holds cannot be taken from them, that is the configuration's problem - refused as
        // it is anywhere the slot is asked, and never passed over for npm's, which is a program the person did not name.
        if (layout is not null && _keys is not null)
        {
            var present = _keys.Present(layout);

            if (present.Problems is [var broken, ..])
            {
                return Refused(HarnessExit.ConfigInvalid, broken);
            }

            if (present.Holds(ExecutableVariable))
            {
                var named = _keys.Value(layout, appointed, ExecutableVariable);

                return named is { Problem: null, Value: { Length: > 0 } namedProgram }
                    ? await NamedAsync(namedProgram, cancellationToken).ConfigureAwait(false)
                    : Refused(HarnessExit.ConfigInvalid, named.Problem ?? $"the slot declares {ExecutableVariable} and gave no value for it");
            }
        }

        var found = await _npm.FindAsync(cancellationToken).ConfigureAwait(false);

        if (found.Npm is not { } npm)
        {
            return Refused(
                HarnessExit.ToolMissing,
                $"this run needs a Claude model through the client 'claude-code'{steps}, and {found.Missing}. "
                + "This tool installs Claude Code and brings it up to date with npm. Install Node.js 22 or later, which brings npm, and start the run again");
        }

        var asked = await _npm.RootAsync(npm, cancellationToken).ConfigureAwait(false);

        if (asked.Root is not { } root)
        {
            return Refused(
                HarnessExit.ToolMissing,
                $"npm was found but would not say where its global packages are ({asked.Problem}), so Claude Code could not be looked for. "
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
        if (held is { Version: { } version, Program: { } program } && AtLeast(version, NeededVersion))
        {
            // What the program answers of its own version has to agree with the package's: a pair that disagrees is
            // half an installation, and half an installation is refused as one that did not take.
            var answered = await _install.VersionAsync(program, cancellationToken).ConfigureAwait(false);

            if (answered.Version is not { } answer)
            {
                return Refused(
                    HarnessExit.ToolMissing,
                    $"'{program}' would not answer its own --version, so this tool cannot start it: {answered.Problem}. "
                    + $"Install it again: {InstallCommand()}; an installation whose step after the install did not run "
                    + "(npm install --ignore-scripts) is the usual cause.");
            }

            return SemanticVersion.Compare(answer, version) == 0
                ? ClaudeCodeAnswer.ReadyAt(answer, program)
                : Refused(
                    HarnessExit.ToolMissing,
                    $"'{program}' says it is claude {answer} and its package says {version}, and the two do not agree. "
                    + $"Install it again: {InstallCommand()}; an installation whose step after the install did not run "
                    + "(npm install --ignore-scripts) is the usual cause.");
        }

        if (!UpdatesOn(client) || _environment(UpdatesVariable) is { Length: > 0 })
        {
            var turnedOff = !UpdatesOn(client)
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
        // under the session. Which processes are sessions is ClaudeCodeInstall.SessionsAsync's one rule - a program of
        // this installation, node running its script, a claude node runs, and a claude whose program could not be
        // read, each said as what it is - and no other claude, which npm's change never touches. A table read by
        // names alone says nothing of what any process runs, so it is refused as the rule every updater shares says
        // (ProgramUpdate.Unread), never read as a machine running no session. An installation where there is none
        // meets no such process, and the table is not read. Every claude this tool starts is given
        // DISABLE_AUTOUPDATER=1 (ClaudeCodeInstall.EveryStart), so no session of the tool's own moves the installation
        // by itself either: only this sequence does.
        if (held.Installed)
        {
            var table = await _processes.ReadAsync(cancellationToken).ConfigureAwait(false);

            if (ProgramUpdate.Unread(table, "what each process on this machine runs") is { } unread)
            {
                return Refused(
                    HarnessExit.Refused,
                    $"{unread} so whether a session of Claude Code is running could not be told, and a change beside one takes its "
                    + $"program from under it. Nothing was changed; with every session of Claude Code ended, run it by hand: {InstallCommand()}");
            }

            var sessions = await _install.SessionsAsync(table.Processes, root, _platform.PathComparison, cancellationToken).ConfigureAwait(false);
            var running = _updates.Running(table, sessions.Counts, _process.CurrentId);

            if (running.Count > 0)
            {
                return Refused(
                    HarnessExit.Refused,
                    $"a session of Claude Code is running on this machine ({string.Join(", ", running.Select(sessions.Said))}); "
                    + "start the run again when they have ended.");
            }
        }

        using var hold = _updates.TryHold(root);

        if (hold is null)
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

        // What npm holds since is read from the root npm names since, as the root before was: the package and then
        // its program, whether the change ran or not - a change that failed part way can leave half an installation.
        ClaudeCodePackage? since = null;
        var moved = await _updates.ChangeAsync(
                hold,
                change,
                NpmClient.RootRequest(npm),
                listing =>
                {
                    if (listing.Trim() is not { Length: > 0 } rootSince)
                    {
                        return ListedRelease.Unreadable;
                    }

                    since = _install.Read(rootSince, Needed);

                    return since.Problem is not null ? ListedRelease.Unreadable : ListedRelease.Read(since.Version?.ToString());
                })
            .ConfigureAwait(false);
        var answeredSince = since is { Program: { } sinceProgram, Problem: null }
            ? await _install.VersionAsync(sinceProgram, CancellationToken.None).ConfigureAwait(false)
            : null;
        var output = OutputOf(moved.Ran);

        if (!moved.Ran.Succeeded)
        {
            var (code, why) = Failed(moved.Ran, output);

            return Refused(code, why, [HeldSince(moved, since, answeredSince), .. Said(output)]);
        }

        // The change is done only where the package and the program both say the version needed or a later one.
        var unlisted = NpmClient.ReadRoot(moved.Listed).Problem;

        if (unlisted is not null || since is null)
        {
            return Refused(
                HarnessExit.ToolMissing,
                $"npm ran, and what it holds since is not known: {unlisted ?? "its root was not read"}. The command that ran: '{InstallCommand()}'.",
                Said(output));
        }

        if (since.Problem is { } broke)
        {
            return Refused(HarnessExit.ToolMissing, broke, Said(output));
        }

        if (since is not { Version: { } release, Program: { } now } || !AtLeast(release, NeededVersion))
        {
            return Refused(
                HarnessExit.ToolMissing,
                $"npm ran, and Claude Code is still missing or older than this tool needs: "
                    + $"{(since.Version is { } stillHeld ? $"npm holds claude {stillHeld}" : "npm holds no Claude Code")}, and this tool needs {Needed}. "
                    + $"The command that ran: '{InstallCommand()}'. {Causes}",
                Said(output));
        }

        if (answeredSince is not { Version: { } said })
        {
            return Refused(
                HarnessExit.ToolMissing,
                $"npm ran, and the Claude Code it holds does not answer: its package says {release}, and its program '{now}' "
                    + $"would not answer its --version ({answeredSince?.Problem}). The command that ran: '{InstallCommand()}'. {Causes}",
                Said(output));
        }

        if (!AtLeast(said, NeededVersion))
        {
            return Refused(
                HarnessExit.ToolMissing,
                $"npm ran, and Claude Code is still older than this tool needs: its package says {release}, its program '{now}' "
                    + $"says {said}, and this tool needs {Needed}. The command that ran: '{InstallCommand()}'. {Causes}",
                Said(output));
        }

        return new ClaudeCodeAnswer(
            true,
            0,
            held.Version is { } older
                ? $"claude {older} is older than this tool needs ({Needed}): updated with '{InstallCommand()}'; it now says {said}"
                : $"npm has no Claude Code installed: installed {said} with '{InstallCommand()}'",
            []);
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

        if (answered.Version is not { } version)
        {
            return Refused(
                HarnessExit.ToolMissing,
                $"'{program}', the program the slot names, would not answer its --version: {answered.Problem}. "
                + $"Update it by hand, or take {ExecutableVariable} out of the slot");
        }

        return AtLeast(version, NeededVersion)
            ? new ClaudeCodeAnswer(true, 0, $"claude {version}, named by the slot: '{program}'", [])
            : Refused(
                HarnessExit.ToolMissing,
                $"'{program}' is claude {version}, older than this tool needs ({Needed}), and a program a person named is never updated. "
                + $"Update it by hand, or take {ExecutableVariable} out of the slot");
    }

    /// <summary>
    /// How a change that npm did not finish is refused, and with which code: not finished within its bound, the
    /// registry or the download not reached - each a change begun and not finished, which running it again can finish,
    /// and which exits as every program's update begun and not finished does (<see cref="ProgramUpdate.FailedExit"/>)
    /// - or anything else npm refused (14: the Claude Code a run needs could not be made ready), a machine that will
    /// not let it write among them, which the tool never runs sudo to reach past.
    /// </summary>
    private static (int Code, string Why) Failed(ProcessResult ran, string output)
    {
        var network = !ran.TimedOut
            && (output.Contains("ENOTFOUND", StringComparison.Ordinal)
                || output.Contains("ECONNRESET", StringComparison.Ordinal)
                || output.Contains("ETIMEDOUT", StringComparison.Ordinal)
                || output.Contains("network", StringComparison.Ordinal));
        var code = ProgramUpdate.FailedExit(ran, network, HarnessExit.ToolMissing);

        if (ran.TimedOut)
        {
            return (
                code,
                $"npm did not finish within {ToolPackage.ChangeBudget.TotalMinutes.ToString("0", CultureInfo.InvariantCulture)} minutes and was ended, "
                    + "part way through the download or the step after the install; running it again can help. "
                    + $"Run it by hand: {InstallCommand()}.");
        }

        var elevated = output.Contains("EACCES", StringComparison.Ordinal);

        return (
            code,
            (network
                ? $"npm reached neither the registry nor the download ({HostProbes.Failure("the install was not reached", ran)}); running it again can help"
                : $"npm could not change what it holds ({HostProbes.Failure("the install failed", ran)})")
            + (elevated ? "; this tool never runs sudo" : string.Empty)
            + $". Run it by hand: {InstallCommand()}.");
    }

    /// <summary>
    /// What npm holds since a change that did not finish, as a line under its refusal: nothing, the package and what its
    /// program answers - a package whose program does not answer being half an installation, said as that - or why it
    /// is not known.
    /// </summary>
    private static string HeldSince(ProgramChange moved, ClaudeCodePackage? since, ClaudeCodeVersion? answered)
    {
        var unlisted = NpmClient.ReadRoot(moved.Listed).Problem;

        if (unlisted is not null || since is null)
        {
            return $"What npm holds since is not known: {unlisted ?? "its root was not read"}.";
        }

        return since switch
        {
            { Installed: false } => "npm holds no Claude Code since.",
            { Problem: { } problem } => $"What npm holds since is not a Claude Code this tool can start: {problem}",
            { Version: { } version, Program: { } program } when answered is { Version: { } said }
                => $"npm holds claude {version} since, and its program '{program}' answers {said}.",
            { Version: { } version, Program: { } program }
                => $"npm holds claude {version} since, and its program '{program}' would not answer its --version ({answered?.Problem}): "
                    + "the change left half an installation.",
            _ => throw new UnreachableException("A package that reads names its version and its program."),
        };
    }

    /// <summary>Whether <paramref name="version"/> is <paramref name="needed"/> or a later one: the one comparison of versions.</summary>
    private static bool AtLeast(SemanticVersion version, SemanticVersion needed)
        => SemanticVersion.Compare(version, needed) >= 0;

    /// <summary>npm's own last lines, as one line under a refusal; none where it printed nothing.</summary>
    private static IReadOnlyList<string> Said(string output)
        => HostProbes.Excerpt(output) is { Length: > 0 } excerpt ? [excerpt] : [];

    private static ClaudeCodeAnswer Refused(int exitCode, string message, IReadOnlyList<string>? details = null)
        => new(false, exitCode, message, details ?? []);

    private static string OutputOf(ProcessResult result)
        => string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError;
}
