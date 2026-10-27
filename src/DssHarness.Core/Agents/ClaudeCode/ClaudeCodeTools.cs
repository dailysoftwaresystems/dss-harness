namespace DssHarness.Core.Agents.ClaudeCode;

/// <summary>
/// Which of Claude Code's tools a launch is given: the tools of the agent's capabilities and no other, by one table -
/// what <c>--tools</c> names, what the launch's settings hold an ask rule for, what the engine decides a call of, and
/// what <c>help agent-clients</c> prints.
/// </summary>
/// <remarks>
/// The names are the ones Claude Code gives its tools, as the probe read them on every version it measured. A web tool of
/// Claude Code's own is named by no capability: the web reaches an agent on this client through the engine's tool server
/// (<see cref="ServerTools"/>), which serves the harness's own fetch and search beside <c>--tools</c>.
/// </remarks>
public static class ClaudeCodeTools
{
    /// <summary>Claude Code's tool that reads a file's lines.</summary>
    public const string Read = "Read";

    /// <summary>Claude Code's tool that writes a file whole.</summary>
    public const string Write = "Write";

    /// <summary>Claude Code's tool that replaces a string in a file.</summary>
    public const string Edit = "Edit";

    /// <summary>
    /// Each capability with the tools it names, in the order a definition names the capabilities; each tool with the
    /// member of its input that names what a call of it is of - the path it reads or writes, the command it runs - and
    /// whether it searches, reading what lies beneath the directory it names - the agent's working directory where it
    /// names none.
    /// </summary>
    public static IReadOnlyList<(AgentCapability Capability, IReadOnlyList<ClaudeCodeTool> Tools)> Table { get; } =
    [
        (AgentCapability.Read, [new(Read, "file_path"), new("Grep", "path", Searches: true), new("Glob", "path", Searches: true)]),
        (AgentCapability.Write, [new(Write, "file_path"), new(Edit, "file_path"), new("NotebookEdit", "notebook_path")]),
        (AgentCapability.Run, [new("Bash", "command")]),
        (AgentCapability.Web, []),
    ];

    /// <summary>The engine's tool server, as <c>--mcp-config</c> names it and as Claude Code begins the names of its tools.</summary>
    public const string Server = Runtime.AgentToolServer.Name;

    /// <summary>
    /// The runtime's tools a launch takes from the engine's tool server, by the runtime's names: every tool of the runtime
    /// whose capability names none of Claude Code's own in <see cref="Table"/> - the web's, the harness's own fetch and
    /// search, on a step the runtime offers them to - each beside <c>--tools</c> and never named in it, with an ask rule as
    /// every tool has. Claude Code's own <c>WebFetch</c> and <c>WebSearch</c> are given no launch.
    /// </summary>
    public static IReadOnlyList<string> ServerTools { get; } =
        [.. Runtime.AgentTools.All(AgentCommandSettings.Default).Where(tool => Table.Any(row => row.Capability == tool.Needs && row.Tools.Count == 0)).Select(tool => tool.Name)];

    /// <summary>The tools of the engine's tool server <paramref name="capability"/> gives, as Claude Code names them.</summary>
    /// <param name="capability">The capability.</param>
    public static IReadOnlyList<string> ServedOf(AgentCapability capability)
        => [.. Runtime.AgentTools.All(AgentCommandSettings.Default).Where(tool => tool.Needs == capability && ServerTools.Contains(tool.Name, StringComparer.Ordinal)).Select(tool => ServedName(tool.Name))];

    /// <summary>What Claude Code calls a tool of the engine's tool server: <c>mcp__dssharness__web_search</c>.</summary>
    /// <param name="tool">The tool, by the runtime's name.</param>
    public static string ServedName(string tool)
    {
        ArgumentNullException.ThrowIfNull(tool);

        return ServedPrefix + tool;
    }

    /// <summary>The runtime's name of the tool of the engine's tool server Claude Code calls <paramref name="name"/>; null for any other tool.</summary>
    /// <param name="name">The tool, as Claude Code names it.</param>
    public static string? ServedAs(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        return name.StartsWith(ServedPrefix, StringComparison.Ordinal) && ServerTools.Contains(name[ServedPrefix.Length..], StringComparer.Ordinal)
            ? name[ServedPrefix.Length..]
            : null;
    }

    /// <summary>The tools of the engine's tool server a launch takes, by the runtime's names: those the runtime offers its step (<see cref="Runtime.AgentTools.Offered"/>) that the server serves.</summary>
    /// <param name="offered">What the runtime offers the step.</param>
    public static IReadOnlyList<string> Served(IReadOnlyList<Runtime.AgentToolDefinition> offered)
    {
        ArgumentNullException.ThrowIfNull(offered);

        return [.. offered.Select(tool => tool.Name).Where(name => ServerTools.Contains(name, StringComparer.Ordinal))];
    }

    /// <summary>What Claude Code begins the name of each tool of the engine's tool server with.</summary>
    private const string ServedPrefix = "mcp__" + Server + "__";

    /// <summary>What the run's reporter is given, whose reads are narrowed: the tools of <see cref="Policy.AgentPolicyGround.ReporterCapabilities"/>.</summary>
    public static IReadOnlyList<string> Reporter { get; } = Of(Policy.AgentPolicyGround.ReporterCapabilities);

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
    /// <param name="capabilities">What its tools are given by: the agent's own, or the reporter's (<see cref="Policy.AgentPolicyGround.Given"/>).</param>
    public static IReadOnlyList<string> Of(IReadOnlyList<AgentCapability> capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);

        return [.. Table.Where(row => capabilities.Contains(row.Capability)).SelectMany(row => row.Tools).Select(tool => tool.Name).Distinct(StringComparer.Ordinal)];
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
/// <param name="Searches">
/// Whether the tool searches: a call of it reads what lies beneath the directory it names, and one that names none
/// searches the agent's working directory.
/// </param>
public sealed record ClaudeCodeTool(string Name, string Subject, bool Searches = false);
