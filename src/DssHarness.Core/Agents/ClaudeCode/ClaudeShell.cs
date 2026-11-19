using DssHarness.Core.Processes;

namespace DssHarness.Core.Agents.ClaudeCode;

/// <summary>
/// The names a Claude Code launch's shell commands were handed into the operating system's isolation by, when a launch was
/// given Claude Code's own shell: its shell prefix naming this tool and <see cref="IsolatedShell.Option"/>, the launch's
/// isolation file, its receipts' directory and Claude Code's own temporary directory beside the step's records.
/// </summary>
/// <remarks>
/// No launch is given Claude Code's own shell or file tools now: every command an agent runs on this client is the tool
/// runtime's <c>run_command</c>, run under the isolation of the agent's commands in the engine's own process, and nothing
/// is handed over through a prefix, written as a receipt or kept in a temporary directory of the launch's. What is left
/// here is read only where the isolation's own layers and help still name these places.
/// </remarks>
public static class ClaudeShell
{
    /// <summary>The directory, in the step's directory, each handed command's receipt was written in.</summary>
    public const string ReceiptsDirectoryName = "claude-receipts";

    /// <summary>The directory, in the step's directory, that was Claude Code's own temporary directory.</summary>
    public const string TempDirectoryName = "claude-tmp";

    /// <summary>The variable that tells Claude Code where its own temporary files are, its shell's working-directory file among them.</summary>
    public const string TempVariable = "CLAUDE_CODE_TMPDIR";

    /// <summary>The launch's isolation, as the isolated shell reads it, in the step's directory.</summary>
    public const string FileName = "claude-isolation.json";

    /// <summary>
    /// The places in <paramref name="stepDirectory"/> that Claude Code's own process and the isolated shell wrote, which no
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
}
