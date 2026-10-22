namespace DssHarness.Core.Agents.ClaudeCode;

/// <summary>
/// Which of Claude Code's tools a launch is given: the tools of the agent's capabilities and no other, by one table -
/// what <c>--tools</c> names, what the launch's settings hold an ask rule for, what the engine decides a call of, and
/// what <c>help agent-clients</c> prints.
/// </summary>
/// <remarks>
/// The names are the ones Claude Code gives its tools, as the probe read them on every version it measured. A web tool is
/// named by no capability: the web reaches an agent on this client through the engine's own tool server, which serves
/// the harness's search and fetch, and until it does a launch has no web tool at all.
/// </remarks>
public static class ClaudeCodeTools
{
    /// <summary>
    /// Each capability with the tools it names, in the order a definition names the capabilities; each tool with the
    /// member of its input that names what a call of it is of - the path it reads or writes, the command it runs - and
    /// whether a call that names none is of the agent's working directory, as a search that names no path is.
    /// </summary>
    public static IReadOnlyList<(AgentCapability Capability, IReadOnlyList<ClaudeCodeTool> Tools)> Table { get; } =
    [
        (AgentCapability.Read, [new("Read", "file_path"), new("Grep", "path", WorkingDirectoryWhereNone: true), new("Glob", "path", WorkingDirectoryWhereNone: true)]),
        (AgentCapability.Write, [new("Write", "file_path"), new("Edit", "file_path"), new("NotebookEdit", "notebook_path")]),
        (AgentCapability.Run, [new("Bash", "command")]),
        (AgentCapability.Web, []),
    ];

    /// <summary>What the run's reporter is given, whose reads are narrowed: the tools that read, and no other.</summary>
    public static IReadOnlyList<string> Reporter { get; } = Of([AgentCapability.Read], reporter: false);

    /// <summary>
    /// Claude Code's own tool for an answer held to the launch's <c>--json-schema</c>: the way an answer arrives, never a
    /// call the engine is asked about, and no tool the table gives - so no call of it is counted or told as one.
    /// </summary>
    public const string StructuredOutput = "StructuredOutput";

    /// <summary>
    /// The tools no launch is given, whatever its capabilities: the one that starts sub-agents (an agent that started
    /// others would run more reasoning agents than the run counts), the one that loads skills (a skill reaches an agent
    /// through its prompt alone), the one that asks a person (an agent asks through its answer), and the plan mode's.
    /// </summary>
    public static IReadOnlyList<string> NeverOffered { get; } = ["Agent", "Task", "Skill", "AskUserQuestion", "EnterPlanMode", "ExitPlanMode"];

    /// <summary>The tools a launch is given, in the table's order, each once.</summary>
    /// <param name="capabilities">What the agent may do.</param>
    /// <param name="reporter">Whether the agent is the run's reporter, whose reads are narrowed and who is given nothing else.</param>
    public static IReadOnlyList<string> Of(IReadOnlyList<AgentCapability> capabilities, bool reporter)
    {
        ArgumentNullException.ThrowIfNull(capabilities);

        return reporter
            ? Reporter
            : [.. Table.Where(row => capabilities.Contains(row.Capability)).SelectMany(row => row.Tools).Select(tool => tool.Name).Distinct(StringComparer.Ordinal)];
    }

    /// <summary>The table's row of <paramref name="tool"/>: its capability and what its input names; null for a tool the table does not name.</summary>
    /// <param name="tool">The tool, as Claude Code names it.</param>
    public static (AgentCapability Capability, ClaudeCodeTool Tool)? Find(string tool)
    {
        foreach (var (capability, tools) in Table)
        {
            foreach (var named in tools.Where(named => string.Equals(named.Name, tool, StringComparison.Ordinal)))
            {
                return (capability, named);
            }
        }

        return null;
    }
}

/// <summary>One of Claude Code's tools the table gives, and what a call of it is of.</summary>
/// <param name="Name">The tool, as Claude Code names it.</param>
/// <param name="Subject">The member of a call's input that names the path or the command its action is decided by.</param>
/// <param name="WorkingDirectoryWhereNone">Whether a call that names none is of the agent's working directory.</param>
public sealed record ClaudeCodeTool(string Name, string Subject, bool WorkingDirectoryWhereNone = false);
