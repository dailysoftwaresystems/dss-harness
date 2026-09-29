using RepoHarness.Core.Repository;

namespace RepoHarness.Core.Orchestration;

/// <summary>
/// Where one orchestrator keeps everything it and its agents hold, under <c>.orchestrators/&lt;name&gt;</c> in the main
/// checkout. Every path is spelt here and nowhere else.
/// </summary>
/// <remarks>
/// <code>
/// agent.json                  the orchestrator's own record
/// logs/&lt;name&gt;.jsonl          one line for each run that changed the orchestrator or one agent, or tried to
/// logs/&lt;agent&gt;/              the agent's Claude transcripts, kept when it is deleted
/// work/&lt;agent&gt;/              the agent's scratch and task files, outside its worktree
/// plans/&lt;name&gt;/              plans, the orchestrator's own and one directory for each agent
/// agents/&lt;agent&gt;/agent.json   the agent's record
/// agents/&lt;agent&gt;/seed.json    what it was handed when it began, and each path's digest
/// agents/&lt;agent&gt;/rows/        the anchor rows it files, one directory for each
/// agents/&lt;agent&gt;/evidence/    what its worktree's evidence roots held when it was deleted
/// </code>
/// The orchestrator's name and each agent's are the directories they are kept in, and an agent is never named as its
/// orchestrator, since the two would share a log and a plans directory.
/// </remarks>
/// <param name="Name">The orchestrator's name.</param>
/// <param name="Directory">Its directory.</param>
public sealed record OrchestratorLayout(string Name, string Directory)
{
    /// <summary>The name of the file every record is kept in: the orchestrator's, and each agent's.</summary>
    public const string RecordFileName = "agent.json";

    /// <summary>The directory an orchestrator's logs are kept in.</summary>
    public const string LogsDirectoryName = "logs";

    /// <summary>The directory an orchestrator's agents' scratch and task files are kept in.</summary>
    public const string WorkDirectoryName = "work";

    /// <summary>The directory an orchestrator's plans, and its agents', are kept in.</summary>
    public const string PlansDirectoryName = "plans";

    /// <summary>The directory an orchestrator's agents are kept in.</summary>
    public const string AgentsDirectoryName = "agents";

    /// <summary>The name of the file an agent's seed is kept in.</summary>
    public const string SeedFileName = "seed.json";

    /// <summary>The directory an agent files its anchor rows in.</summary>
    public const string RowsDirectoryName = "rows";

    /// <summary>The directory an agent's evidence is kept in when it is deleted.</summary>
    public const string EvidenceDirectoryName = "evidence";

    /// <summary>The extension of a log: one JSON object to a line.</summary>
    public const string LogExtension = ".jsonl";

    /// <summary>Where the orchestrator named <paramref name="name"/> of the repository at <paramref name="layout"/> is kept.</summary>
    /// <param name="layout">The repository.</param>
    /// <param name="name">The orchestrator's name, already checked.</param>
    public static OrchestratorLayout Of(HarnessLayout layout, string name)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return new OrchestratorLayout(name, Path.Combine(layout.OrchestratorsDirectory, name));
    }

    /// <summary>The orchestrator's own record.</summary>
    public string RecordFile => Path.Combine(Directory, RecordFileName);

    /// <summary>The orchestrator's own record, relative to the repository and spelt with forward slashes, as git is asked about it.</summary>
    public string RelativeRecordFile => $"{HarnessLayout.OrchestratorsDirectoryName}/{Name}/{RecordFileName}";

    /// <summary>The directory its agents' scratch and task files are kept in, one directory for each.</summary>
    public string WorkRoot => Path.Combine(Directory, WorkDirectoryName);

    /// <summary>The directory its logs are kept in.</summary>
    public string LogsDirectory => Path.Combine(Directory, LogsDirectoryName);

    /// <summary>The log of what the tool did to <paramref name="subject"/>: the orchestrator, or one of its agents.</summary>
    /// <param name="subject">The orchestrator's name, or an agent's.</param>
    public string LogFile(string subject) => Path.Combine(LogsDirectory, subject + LogExtension);

    /// <summary>Where the Claude transcripts of <paramref name="agent"/> are kept.</summary>
    /// <param name="agent">The agent's name.</param>
    public string TranscriptsDirectory(string agent) => Path.Combine(LogsDirectory, agent);

    /// <summary>Where <paramref name="agent"/> keeps its scratch and task files.</summary>
    /// <param name="agent">The agent's name.</param>
    public string WorkDirectory(string agent) => Path.Combine(WorkRoot, agent);

    /// <summary>Where the plans of <paramref name="subject"/> are kept: the orchestrator's, or one of its agents'.</summary>
    /// <param name="subject">The orchestrator's name, or an agent's.</param>
    public string PlansDirectory(string subject) => Path.Combine(Directory, PlansDirectoryName, subject);

    /// <summary>The directory its agents are kept in.</summary>
    public string AgentsDirectory => Path.Combine(Directory, AgentsDirectoryName);

    /// <summary>Where <paramref name="agent"/> is kept.</summary>
    /// <param name="agent">The agent's name.</param>
    public string AgentDirectory(string agent) => Path.Combine(AgentsDirectory, agent);

    /// <summary>The record of <paramref name="agent"/>.</summary>
    /// <param name="agent">The agent's name.</param>
    public string AgentRecordFile(string agent) => Path.Combine(AgentDirectory(agent), RecordFileName);

    /// <summary>The seed of <paramref name="agent"/>.</summary>
    /// <param name="agent">The agent's name.</param>
    public string SeedFile(string agent) => Path.Combine(AgentDirectory(agent), SeedFileName);

    /// <summary>Where <paramref name="agent"/> files its anchor rows.</summary>
    /// <param name="agent">The agent's name.</param>
    public string RowsDirectory(string agent) => Path.Combine(AgentDirectory(agent), RowsDirectoryName);

    /// <summary>Where the evidence of <paramref name="agent"/> is kept when it is deleted.</summary>
    /// <param name="agent">The agent's name.</param>
    public string EvidenceDirectory(string agent) => Path.Combine(AgentDirectory(agent), EvidenceDirectoryName);

    /// <summary>One keeping of the evidence of <paramref name="agent"/>, named <paramref name="keeping"/>.</summary>
    /// <param name="agent">The agent's name.</param>
    /// <param name="keeping">The keeping's name, as <see cref="KeptEvidence(string)"/> gives it or the directory is called.</param>
    public string KeptEvidence(string agent, string keeping) => Path.Combine(AgentDirectory(agent), keeping.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>A keeping of evidence named <paramref name="name"/>, relative to its agent's directory, as records and listings name it.</summary>
    /// <param name="name">The keeping's own name: the moment it was made.</param>
    public static string KeptEvidence(string name) => $"{EvidenceDirectoryName}/{name}";
}
