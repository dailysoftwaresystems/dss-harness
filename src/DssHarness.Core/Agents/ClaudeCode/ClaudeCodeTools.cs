namespace DssHarness.Core.Agents.ClaudeCode;

/// <summary>
/// Which tools a Claude Code launch is given: the engine's tool server's, by the agent's capabilities, and of Claude Code's
/// own a side-task's web tools alone - what <c>--tools</c> names, what the launch's settings hold an ask rule for, what the
/// engine decides a call of, and what <c>help agent-clients</c> prints.
/// </summary>
/// <remarks>
/// <para>
/// Every tool the tool runtime offers an agent - its file tools, its command tool <c>run_command</c> and the web's - is
/// served to a launch by the engine's tool server (<see cref="ServerTools"/>), beside the run's graph and measure tools, so
/// each call is run by the runtime as on the API clients: a command under the isolation of the agent's commands, its
/// output kept whole and paged, a file opened and decided again by where the system names it. Claude Code's own shell and
/// file tools are given no launch: <c>--tools</c> names a side-task's own <c>WebFetch</c> and <c>WebSearch</c>, or nothing.
/// </para>
/// <para>
/// The names of Claude Code's own tools are the ones it gives them, as the probe read them on every version it measured.
/// Their table (<see cref="Table"/>) reads what a call of one is of - a call in a stream stored before, a skill's allowed
/// tools, a side-task's own web call - though no launch is given one of its file or shell tools.
/// </para>
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
    /// Each capability with the tools of Claude Code's own it names, in the order a definition names the capabilities;
    /// each tool with the member of its input that names what a call of it is of - the path it reads or writes, the
    /// command it runs - and whether it searches, reading what lies beneath the directory it names - the agent's working
    /// directory where it names none. No launch is given one of them: the engine's tool server serves the runtime's in
    /// their place.
    /// </summary>
    public static IReadOnlyList<(AgentCapability Capability, IReadOnlyList<ClaudeCodeTool> Tools)> Table { get; } =
    [
        (AgentCapability.Read, [new(Read, "file_path"), new("Grep", "path", Searches: true), new("Glob", "path", Searches: true)]),
        (AgentCapability.Write, [new(Write, "file_path"), new(Edit, "file_path"), new("NotebookEdit", "notebook_path")]),
        (AgentCapability.Run, [new("Bash", "command")]),
        (AgentCapability.Web, []),
    ];

    /// <summary>
    /// Claude Code's own web tools, kept in a side-task's <c>--tools</c> beside the engine's tool server:
    /// its <c>WebSearch</c> is the side-task's <c>native</c> search, and its <c>WebFetch</c> another way to read a page.
    /// A step's launch is given neither - the web reaches a step through the harness's own tools alone.
    /// </summary>
    public static IReadOnlyList<ClaudeCodeTool> OwnWeb { get; } = [new("WebFetch", "url"), new("WebSearch", "query")];

    /// <summary>The engine's tool server, as <c>--mcp-config</c> names it and as Claude Code begins the names of its tools.</summary>
    public const string Server = Runtime.AgentToolServer.Name;

    /// <summary>
    /// The tools the engine's tool server serves, by the runtime's names: every tool of the tool runtime - its file tools,
    /// its command tool and the web's, each to a launch whose step the runtime offers it to - and the run's graph and
    /// measure tools, offered where its run is given one (<c>graph: tools</c>; a repository that declares legs), each with
    /// an ask rule as every tool has.
    /// </summary>
    public static IReadOnlyList<string> ServerTools { get; } =
        [.. Runtime.AgentTools.All(AgentCommandSettings.Default)
            .Select(tool => tool.Name)
            .Concat([AgentGraphSupport.ToolName, AgentMeasureSupport.ToolName])];

    /// <summary>
    /// The runtime's file tools, by the runtime's names, in its table's order: what the engine's tool server serves every
    /// launch of an agent that reads or writes. Claude Code would open each file by a path the engine decided earlier, in
    /// its own process - a link a command put on the way in between reaching what the isolation keeps from every command;
    /// the runtime opens each and decides the open file again by where the system names it.
    /// </summary>
    public static IReadOnlyList<string> FileTools { get; } =
        [.. Runtime.AgentTools.All(AgentCommandSettings.Default)
            .Where(tool => tool.Needs is AgentCapability.Read or AgentCapability.Write)
            .Select(tool => tool.Name)];

    /// <summary>The tools of the engine's tool server <paramref name="capability"/> gives, as Claude Code names them, in the runtime's table's order.</summary>
    /// <param name="capability">The capability.</param>
    public static IReadOnlyList<string> ServedOf(AgentCapability capability)
        => [.. Runtime.AgentTools.All(AgentCommandSettings.Default).Where(tool => tool.Needs == capability).Select(tool => ServedName(tool.Name))];

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

    /// <summary>The tools of the engine's tool server a launch takes, by the runtime's names: every one the runtime offers its step (<see cref="Runtime.AgentTools.Offered"/>).</summary>
    /// <param name="offered">What the runtime offers the step.</param>
    public static IReadOnlyList<string> Served(IReadOnlyList<Runtime.AgentToolDefinition> offered)
    {
        ArgumentNullException.ThrowIfNull(offered);

        return [.. offered.Select(tool => tool.Name).Where(name => ServerTools.Contains(name, StringComparer.Ordinal))];
    }

    /// <summary>What Claude Code begins the name of each tool of the engine's tool server with.</summary>
    private const string ServedPrefix = "mcp__" + Server + "__";

    /// <summary>
    /// What the run's reporter is given, whose reads are narrowed: the engine's tool server's tools of
    /// <see cref="Policy.AgentPolicyGround.ReporterCapabilities"/>, as Claude Code names them.
    /// </summary>
    public static IReadOnlyList<string> Reporter { get; } = [.. Policy.AgentPolicyGround.ReporterCapabilities.SelectMany(capability => ServedOf(capability))];

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

    /// <summary>
    /// The tools of Claude Code's own a launch is given, as <c>--tools</c> names them: its own web tools where the agent has
    /// the web and the launch is a side-task's (<see cref="OwnWeb"/>), and none otherwise - every other tool of the launch's
    /// is the engine's tool server's.
    /// </summary>
    /// <param name="capabilities">What its tools are given by: the agent's own, or the reporter's (<see cref="Policy.AgentPolicyGround.Given"/>).</param>
    /// <param name="ownWeb">Whether Claude Code's own web tools are given, as a side-task's launch alone is.</param>
    public static IReadOnlyList<string> Of(IReadOnlyList<AgentCapability> capabilities, bool ownWeb = false)
    {
        ArgumentNullException.ThrowIfNull(capabilities);

        return ownWeb && capabilities.Contains(AgentCapability.Web) ? [.. OwnWeb.Select(tool => tool.Name)] : [];
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

        foreach (var named in OwnWeb.Where(named => string.Equals(named.Name, tool, StringComparison.Ordinal)))
        {
            return (AgentCapability.Web, named);
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
