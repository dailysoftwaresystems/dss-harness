using DssHarness.Core.Processes;

namespace DssHarness.Core.Agents.ClaudeCode;

/// <summary>
/// How a Claude Code launch's shell commands run inside the operating system's isolation while Claude Code's own process
/// stays outside it: Claude Code starts each through its shell prefix (<c>CLAUDE_CODE_SHELL_PREFIX</c>), which names this
/// tool and <see cref="IsolatedShell.Option"/>, and this tool becomes the mechanism with the command inside
/// (<see cref="IsolatedShell"/>), the launch's isolation read from the file its variable names. Claude Code is told the one
/// shell the command runs in, and its own temporary directory is the launch's, written inside. So Claude Code reads its
/// login and its key in its own process while no command it runs reaches either - its environment behind a process
/// namespace of the command's own, its directories hidden but the shell snapshots it sources.
/// </summary>
public static class ClaudeShell
{
    /// <summary>The variable Claude Code wraps every shell command it starts with.</summary>
    public const string PrefixVariable = "CLAUDE_CODE_SHELL_PREFIX";

    /// <summary>The variable that tells Claude Code which shell to run its commands in.</summary>
    public const string ShellVariable = "CLAUDE_CODE_SHELL";

    /// <summary>The variable that tells Claude Code where its own temporary files are, its shell's working-directory file among them.</summary>
    public const string TempVariable = "CLAUDE_CODE_TMPDIR";

    /// <summary>The launch's isolation, as the isolated shell reads it, in the step's directory.</summary>
    public const string FileName = "claude-isolation.json";

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
    /// <param name="temp">The launch's temporary directory.</param>
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
