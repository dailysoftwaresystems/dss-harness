using System.Text.Json;
using DssHarness.Core.Platform;

namespace DssHarness.Core.Processes;

/// <summary>
/// The step a program that runs outside the isolation hands each of its shell commands to, so the command runs inside it:
/// this tool, started as <c>dssharness --isolated-shell '&lt;command&gt;'</c>, reads the launch's isolation from the file
/// <see cref="FileVariable"/> names, and becomes the mechanism with the shell inside, the command its script - Landlock's
/// scope entered first where the mechanism asks it. Claude Code's own process stays outside, reading its own login,
/// while every shell command it starts - started through <c>CLAUDE_CODE_SHELL_PREFIX</c> - is inside, where the process
/// namespace keeps it from Claude Code's environment and the profile from its directories.
/// </summary>
/// <remarks>
/// Not a command anyone types: help does not list it. A file it cannot read, or that names no mechanism of this machine's,
/// runs nothing: the command fails, and why is said on the error stream.
/// </remarks>
public static class IsolatedShell
{
    /// <summary>The tool's own argument the prefix names, before the command.</summary>
    public const string Option = "--isolated-shell";

    /// <summary>The variable that names the launch's isolation file.</summary>
    public const string FileVariable = "DSSHARNESS_ISOLATED_SHELL";

    /// <summary>The shell a command runs in, by its one path: the one Claude Code is told to use, whose snapshots it sources.</summary>
    public const string Shell = "/bin/bash";

    /// <summary>The code a command ends with where it was not run inside the isolation.</summary>
    public const int NotRun = 126;

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>The isolation's file: the mechanism this machine proved, and the profile.</summary>
    /// <param name="isolation">The launch's isolation.</param>
    public static string Written(ProcessIsolation isolation)
    {
        ArgumentNullException.ThrowIfNull(isolation);

        var profile = isolation.Profile;
        var file = new IsolationFile(
            isolation.Mechanism.Name,
            isolation.Mechanism.Version,
            isolation.Mechanism is Bubblewrap bubblewrap ? bubblewrap.Program : null,
            isolation.Mechanism is Bubblewrap masking ? masking.Masked : [],
            isolation.Mechanism.Scoped,
            profile.Hidden,
            profile.Writes,
            profile.Reads,
            profile.Reopened,
            profile.Temp,
            [.. profile.Environment.Select(variable => new IsolationVariable(variable.Key, variable.Value))],
            profile.Sockets);

        return JsonSerializer.Serialize(file, Options);
    }

    /// <summary>The isolation <paramref name="text"/> holds, or why it holds none of this machine's.</summary>
    /// <param name="text">The file's text.</param>
    public static (ProcessIsolation? Isolation, string? Problem) Read(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        IsolationFile? file;

        try
        {
            file = JsonSerializer.Deserialize<IsolationFile>(text, Options);
        }
        catch (JsonException ex)
        {
            return (null, $"its isolation file does not read ({ex.Message})");
        }

        if (file is null)
        {
            return (null, "its isolation file is empty");
        }

        IIsolationMechanism? mechanism = file.Mechanism switch
        {
            Bubblewrap.MechanismName when file.Program is { Length: > 0 } program => new Bubblewrap(program, file.Version, file.Masked) { Scoped = file.Scoped },
            SandboxExec.MechanismName => new SandboxExec(file.Version, MacSandboxHost.ThisMachine.RealPath),
            _ => null,
        };

        if (mechanism is null)
        {
            return (null, $"its isolation file names '{file.Mechanism}', no mechanism of this tool's that takes a command handed to it");
        }

        var profile = new IsolationProfile(file.Hidden, file.Writes)
        {
            Reads = file.Reads,
            Reopened = file.Reopened,
            Temp = file.Temp,
            Environment = [.. file.Environment.Select(variable => new KeyValuePair<string, string>(variable.Name, variable.Value))],
            Sockets = file.Sockets ?? [],
        };

        return (new ProcessIsolation(mechanism, profile), null);
    }

    /// <summary>
    /// Runs <paramref name="command"/> in <see cref="Shell"/> inside the isolation the file <paramref name="file"/> names
    /// holds: becomes the mechanism, on Linux and macOS, and returns only where it could not.
    /// </summary>
    /// <param name="file">The isolation file, as <see cref="FileVariable"/> names it; null where it names none.</param>
    /// <param name="command">The command, as the program that hands it over gives it.</param>
    /// <param name="error">Where why it was not run is said.</param>
    /// <returns><see cref="NotRun"/> where the command was not run.</returns>
    public static int Run(string? file, string command, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(error);

        if (OperatingSystem.IsWindows())
        {
            error.WriteLine("dssharness: the command was not run: nothing is handed a command on Windows, where a launch runs whole inside MXC's container.");
            return NotRun;
        }

        if (string.IsNullOrEmpty(file) || !Path.IsPathFullyQualified(file) || !File.Exists(file))
        {
            error.WriteLine($"dssharness: the command was not run: '{FileVariable}' names no isolation file ('{file}').");
            return NotRun;
        }

        var (isolation, problem) = Read(File.ReadAllText(file));

        if (isolation is null)
        {
            error.WriteLine($"dssharness: the command was not run: {problem}.");
            return NotRun;
        }

        var (program, arguments) = isolation.Mechanism.Command(isolation.Profile, Shell, ["-c", command]);
        var refused = Containment.Become(isolation.Mechanism.Scoped, program, arguments);

        error.WriteLine($"dssharness: the command was not run: {isolation.Mechanism.Name} '{program}' could not be started ({new System.ComponentModel.Win32Exception(refused).Message}).");
        return NotRun;
    }

    /// <summary>The file's shape.</summary>
    private sealed record IsolationFile(
        string Mechanism,
        string Version,
        string? Program,
        IReadOnlyList<IsolatedPath> Masked,
        bool Scoped,
        IReadOnlyList<IsolatedPath> Hidden,
        IReadOnlyList<IsolatedWrite> Writes,
        IReadOnlyList<string> Reads,
        IReadOnlyList<string> Reopened,
        string? Temp,
        IReadOnlyList<IsolationVariable> Environment,
        IReadOnlyList<string>? Sockets);

    /// <summary>One variable the profile sets.</summary>
    private sealed record IsolationVariable(string Name, string Value);
}
