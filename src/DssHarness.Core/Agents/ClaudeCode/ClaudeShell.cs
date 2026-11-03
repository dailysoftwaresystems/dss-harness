using DssHarness.Core.Processes;

namespace DssHarness.Core.Agents.ClaudeCode;

/// <summary>
/// How a Claude Code launch's shell commands run inside the operating system's isolation, in a layer of their own apart
/// from Claude Code's own process: Claude Code starts each through its shell prefix (<c>CLAUDE_CODE_SHELL_PREFIX</c>), which
/// names this tool and <see cref="IsolatedShell.Option"/>, and this tool becomes the mechanism with the command inside
/// (<see cref="IsolatedShell"/>), the launch's isolation read from the file its variable names. Claude Code's own process
/// runs inside an outer layer of the same mechanism where it can be started again inside one (bubblewrap), and outside
/// every layer where it cannot (macOS's sandbox). Claude Code is told the one shell the command runs in, and its own
/// temporary directory is a directory of the launch's beside its records, which no command writes but the one file each
/// ends by writing its working directory to - so nothing Claude Code writes or reads there in its own process is a
/// command's to replace with a link. So Claude Code reads its login and its key in its own process while no command it
/// runs reaches either - its environment behind a process namespace of the command's own, its directories hidden but the
/// shell snapshots it sources.
/// </summary>
/// <remarks>
/// Each command the engine allows is given back to Claude Code with a receipt at its head (<see cref="IsolatedShell.Marked"/>),
/// which the isolated shell writes in the launch's receipts' directory before it runs the command, and takes off the
/// command before the command runs; a call that comes back with no receipt of its command was not handed over, and ends the
/// launch. A receipt shows a Claude Code that skipped its prefix only once the command has run, and a command that runs
/// without the prefix runs where Claude Code's own process does, where it can write a receipt of its own: the outer layer,
/// never the receipt, is what holds such a command.
/// </remarks>
public static class ClaudeShell
{
    /// <summary>The directory, in the step's directory, each command's receipt is written in.</summary>
    public const string ReceiptsDirectoryName = "claude-receipts";

    /// <summary>The directory, in the step's directory, that is Claude Code's own temporary directory, removed once the launch has ended.</summary>
    public const string TempDirectoryName = "claude-tmp";

    /// <summary>The variable Claude Code wraps every shell command it starts with.</summary>
    public const string PrefixVariable = "CLAUDE_CODE_SHELL_PREFIX";

    /// <summary>The variable that tells Claude Code which shell to run its commands in.</summary>
    public const string ShellVariable = "CLAUDE_CODE_SHELL";

    /// <summary>The variable that tells Claude Code where its own temporary files are, its shell's working-directory file among them.</summary>
    public const string TempVariable = "CLAUDE_CODE_TMPDIR";

    /// <summary>The launch's isolation, as the isolated shell reads it, in the step's directory.</summary>
    public const string FileName = "claude-isolation.json";

    /// <summary>
    /// The places in <paramref name="stepDirectory"/> that Claude Code's own process and the isolated shell write, which no
    /// command does: the receipts' directory, then Claude Code's own temporary directory.
    /// </summary>
    /// <param name="stepDirectory">The step's directory, the launch's own.</param>
    public static IReadOnlyList<string> PlacesIn(string stepDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stepDirectory);

        return [Path.Combine(stepDirectory, ReceiptsDirectoryName), Path.Combine(stepDirectory, TempDirectoryName)];
    }

    /// <summary>
    /// The prefix that hands a command to <paramref name="program"/> - this tool's own executable, before which nothing
    /// stands - as Claude Code splits it: the program, then the option, then the command; or why there is none.
    /// </summary>
    /// <param name="program">This tool's program, by its whole path.</param>
    /// <param name="leading">What the program is given before its own command line: a runtime hosting the tool is handed its assembly.</param>
    public static (string? Prefix, string? Problem) PrefixOf(string program, IReadOnlyList<string> leading)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(program);
        ArgumentNullException.ThrowIfNull(leading);

        if (leading.Count > 0)
        {
            return (null, $"this tool runs as '{program}' with its assembly handed to it, which Claude Code's shell prefix cannot name: run the installed tool");
        }

        // Claude Code reads the program as everything before the prefix's last " -", the option after it.
        return program.Contains(" -", StringComparison.Ordinal)
            ? (null, $"this tool's path '{program}' holds \" -\", which Claude Code's shell prefix reads as the start of an option: install the tool under a path without it")
            : ($"{program} {IsolatedShell.Option}", null);
    }

    /// <summary>
    /// The variables a launch whose commands run inside the isolation is given, in the order they are written - in its
    /// settings' <c>env</c> and its process's environment alike: the prefix, the isolation file, the shell and the
    /// temporary directory.
    /// </summary>
    /// <param name="prefix">The prefix (<see cref="PrefixOf"/>).</param>
    /// <param name="file">The launch's isolation file.</param>
    /// <param name="temp">Claude Code's own temporary directory, the launch's (<see cref="TempDirectoryName"/>).</param>
    public static IReadOnlyList<KeyValuePair<string, string>> Variables(string prefix, string file, string temp)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        ArgumentException.ThrowIfNullOrWhiteSpace(file);
        ArgumentException.ThrowIfNullOrWhiteSpace(temp);

        return
        [
            new(PrefixVariable, prefix),
            new(IsolatedShell.FileVariable, file),
            new(ShellVariable, IsolatedShell.Shell),
            new(TempVariable, temp),
        ];
    }
}
