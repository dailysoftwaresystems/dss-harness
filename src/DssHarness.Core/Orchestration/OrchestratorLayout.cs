using DssHarness.Core.Repository;

namespace DssHarness.Core.Orchestration;

/// <summary>
/// Where one orchestrator keeps everything it and its agents hold, under <c>.orchestrators/&lt;name&gt;</c> in the main
/// checkout. Every path is spelt here and nowhere else.
/// </summary>
/// <remarks>
/// <code>
/// agent.json                  the orchestrator's own record
/// logs/&lt;name&gt;.jsonl          one line for each run that reached the orchestrator or one agent, refusals included
/// logs/&lt;agent&gt;/              the agent's Claude transcripts, kept when it is deleted
/// work/&lt;agent&gt;/              the agent's scratch and task files, outside its worktree
/// plans/&lt;name&gt;/              plans, the orchestrator's own and one directory for each agent
/// agents/&lt;agent&gt;/agent.json   the agent's record
/// agents/&lt;agent&gt;/seed.json    what it shares with the main tree: each path handed to it or folded, with its digest
/// agents/&lt;agent&gt;/shared/&lt;sha256&gt;   the bytes of each file it shares, kept to merge from
/// agents/&lt;agent&gt;/rows/        the anchor rows it files, one directory for each
/// agents/&lt;agent&gt;/applied-rows.json   the rows its folds applied, each as declared then
/// agents/&lt;agent&gt;/evidence/    what its worktree's evidence roots held when it was deleted
/// agents/&lt;agent&gt;/sessions/&lt;id&gt;/   API clients only: one conversation with the provider, in the provider's own form
/// agents/&lt;agent&gt;/steps/&lt;instance&gt;/   one step the agent runs, or that runs in or for its worktree, everything of the step in it
/// steps/&lt;instance&gt;/          one step of the run that no agent owns, the create-agent that makes an agent included
/// graph/graph.json            the run's codebase graph: the base's manifest, every file it keeps with its blob, state and extract
/// graph/symbols.jsonl         the base's link result, as lines: every node and edge, with the digest of the manifest it was made from
/// graph/extracts/&lt;ab&gt;/&lt;name&gt;   one file's extract, written once, named by its blob, its path, its implementation and version
/// graph/sync.jsonl            one line for each sync of the graph
/// &lt;step&gt;/children/&lt;id&gt;.json   in either kind of step's directory, on Linux and macOS: a session a contained child of the step leads while it runs, or the process group a phase of its legs - a set-up's up line apart - its keepAwake command or what carries a leg to a host leads while anything is in it
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

    /// <summary>The directory the bytes of the files an agent shares with the main tree are kept in.</summary>
    public const string SharedDirectoryName = "shared";

    /// <summary>The directory an agent files its anchor rows in.</summary>
    public const string RowsDirectoryName = "rows";

    /// <summary>The name of the file the rows an agent's folds applied are kept in.</summary>
    public const string AppliedRowsFileName = "applied-rows.json";

    /// <summary>The directory an agent's conversations with an API client are kept in.</summary>
    public const string SessionsDirectoryName = "sessions";

    /// <summary>The directory steps are kept in, under the run and inside each agent's directory.</summary>
    public const string StepsDirectoryName = "steps";

    /// <summary>The name of the file a step's handoff is kept in.</summary>
    public const string HandoffFileName = "handoff.md";

    /// <summary>The directory an agent's evidence is kept in when it is deleted.</summary>
    public const string EvidenceDirectoryName = "evidence";

    /// <summary>The extension of a log: one JSON object to a line.</summary>
    public const string LogExtension = ".jsonl";

    /// <summary>The directory, in a step's own, where each session a contained child of the step leads, and each group its legs' work leads, is recorded while it runs.</summary>
    public const string ChildrenDirectoryName = "children";

    /// <summary>The extension of the record of a step's child's session or process group.</summary>
    public const string ChildExtension = ".json";

    /// <summary>The name of the file a run's state is kept in, whole.</summary>
    public const string FlowFileName = "flow.json";

    /// <summary>The name of the file a run's status is kept in, for a person and a client to read.</summary>
    public const string WorkflowStatusFileName = "workflow.json";

    /// <summary>The name of the file a run's pre-existing issues are kept in.</summary>
    public const string PreExistingIssuesFileName = "pre-existing-issues.json";

    /// <summary>The name of the file the orchestrator entry is frozen in, as it ran.</summary>
    public const string DefinitionFileName = "definition.json";

    /// <summary>The name of the file a run's events are appended to, one JSON object to a line.</summary>
    public const string EventsFileName = "events.jsonl";

    /// <summary>The name of the file every question a run raised and every answer it was given is kept in, whole.</summary>
    public const string QuestionsFileName = "questions.json";

    /// <summary>The name of the file what another process asked of the running engine is kept in, until it is taken.</summary>
    public const string StopFileName = "stop.json";

    /// <summary>The name of the file what a person gave from another terminal is kept in, until the engine has written it where it belongs.</summary>
    public const string InboxFileName = "inbox.json";

    /// <summary>The name of the file a run's report is written in, at the end of every session.</summary>
    public const string ReportFileName = "report.md";

    /// <summary>The name of the file a run's report is written in as one JSON document, beside its markdown.</summary>
    public const string ReportJsonFileName = "report.json";

    /// <summary>
    /// The directory one call of the planned <c>ask-orchestrator</c> and <c>ask-orchestrator-status</c> will be kept in -
    /// neither command is built yet, so nothing writes it. The engine never reads it.
    /// </summary>
    public const string AsksDirectoryName = "asks";

    /// <summary>The name of the file a run's live owner is kept in, beside the run's directory.</summary>
    public const string OwnerFileName = ".owner.json";

    /// <summary>The name of the file a step's own record is kept in, in its directory.</summary>
    public const string StepRecordFileName = "step.json";

    /// <summary>The name of the file a step's output is kept in, validated, in its directory.</summary>
    public const string StepOutputFileName = "output.json";

    /// <summary>The name of the file a leg step keeps its legs' ledger in, as <c>--json</c> gives it, in its directory.</summary>
    public const string StepLedgerFileName = "ledger.json";

    /// <summary>
    /// The name of the directory a leg step in an agent's worktree keeps, whole, the log of each red leg's phase that did
    /// not pass, as <c>&lt;leg&gt;.&lt;phase&gt;.log</c>, in its directory.
    /// </summary>
    public const string StepLogsDirectoryName = "logs";

    /// <summary>The name of the run's write-ahead ledger of its folds.</summary>
    public const string FoldsFileName = "folds.json";

    /// <summary>
    /// The name of the file a fold family step keeps in its directory - or in its target's, for a step of <c>others</c>:
    /// what its dry run named and stood, the question it asked, each consent it was given and how its apply ended.
    /// </summary>
    public const string StepFoldFileName = "fold.json";

    /// <summary>The name of the directory a refresh or a rebase of <c>others</c> keeps one directory for each target in, in its own.</summary>
    public const string StepTargetsDirectoryName = "targets";

    /// <summary>The directory a run's codebase graph is kept in: made from the tree, and made again whenever asked.</summary>
    public const string GraphDirectoryName = "graph";

    /// <summary>The name of the file the base's manifest is kept in, in the graph's directory.</summary>
    public const string GraphManifestFileName = "graph.json";

    /// <summary>The name of the file the base's link result is kept in, in the graph's directory.</summary>
    public const string GraphSymbolsFileName = "symbols.jsonl";

    /// <summary>The name of the directory every file's extract is kept in, once, in the graph's directory.</summary>
    public const string GraphExtractsDirectoryName = "extracts";

    /// <summary>The name of the file one line is appended to for each sync of the graph, in its directory.</summary>
    public const string GraphSyncFileName = "sync.jsonl";

    /// <summary>The name of the directory each agent's view of the graph is kept in, in the graph's directory.</summary>
    public const string GraphViewsDirectoryName = "views";

    /// <summary>The extension of a file's extract.</summary>
    public const string GraphExtractExtension = ".json";

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

    /// <summary>Where the bytes of the files <paramref name="agent"/> shares with the main tree are kept, one file for each digest its seed records.</summary>
    /// <param name="agent">The agent's name.</param>
    public string SharedDirectory(string agent) => Path.Combine(AgentDirectory(agent), SharedDirectoryName);

    /// <summary>The kept bytes of a file <paramref name="agent"/> shares: a file named by their SHA-256, as its seed records it.</summary>
    /// <param name="agent">The agent's name.</param>
    /// <param name="digest">The SHA-256 its seed records for the file.</param>
    public string SharedFile(string agent, string digest) => Path.Combine(SharedDirectory(agent), digest);

    /// <summary>Where <paramref name="agent"/> files its anchor rows.</summary>
    /// <param name="agent">The agent's name.</param>
    public string RowsDirectory(string agent) => Path.Combine(AgentDirectory(agent), RowsDirectoryName);

    /// <summary>The rows the folds of <paramref name="agent"/> applied, each as declared then.</summary>
    /// <param name="agent">The agent's name.</param>
    public string AppliedRowsFile(string agent) => Path.Combine(AgentDirectory(agent), AppliedRowsFileName);

    /// <summary>
    /// Where the steps of the run are kept that no agent owns - the <c>create-agent</c> that makes one included, which
    /// stays under the run because the agent it makes is not there yet.
    /// </summary>
    public string StepsDirectory => Path.Combine(Directory, StepsDirectoryName);

    /// <summary>Where one step of the run is kept, which no agent owns.</summary>
    /// <param name="instance">The step instance's name.</param>
    public string StepDirectory(string instance) => Path.Combine(StepsDirectory, instance);

    /// <summary>
    /// Where the steps <paramref name="agent"/> runs are kept - every reason step, and every step that names it with
    /// <c>in</c> or <c>of</c> - one directory for each.
    /// </summary>
    /// <param name="agent">The agent's name.</param>
    public string AgentStepsDirectory(string agent) => Path.Combine(AgentDirectory(agent), StepsDirectoryName);

    /// <summary>Where one step <paramref name="agent"/> runs, or that runs in or for its worktree, is kept.</summary>
    /// <param name="agent">The agent's name.</param>
    /// <param name="instance">The step instance's name.</param>
    public string AgentStepDirectory(string agent, string instance) => Path.Combine(AgentStepsDirectory(agent), instance);

    /// <summary>
    /// Where the conversations <paramref name="agent"/> holds with an API client are kept, one directory for each. A
    /// Claude Code launch has none: a conversation cannot be handed to it.
    /// </summary>
    /// <param name="agent">The agent's name.</param>
    public string AgentSessionsDirectory(string agent) => Path.Combine(AgentDirectory(agent), SessionsDirectoryName);

    /// <summary>Where one conversation <paramref name="agent"/> holds with an API client is kept.</summary>
    /// <param name="agent">The agent's name.</param>
    /// <param name="id">The conversation's id.</param>
    public string AgentSessionDirectory(string agent, string id) => Path.Combine(AgentSessionsDirectory(agent), id);

    /// <summary>
    /// The handoff of one step <paramref name="agent"/> ran, kept in the step's own directory: what its agent said when
    /// a pause or a limit stopped it while it reasoned, or what the engine wrote where it could not answer.
    /// </summary>
    /// <param name="agent">The agent's name.</param>
    /// <param name="instance">The step instance's name.</param>
    public string HandoffFile(string agent, string instance) => Path.Combine(AgentStepDirectory(agent, instance), HandoffFileName);

    /// <summary>
    /// Where the sessions the contained children of the step kept at <paramref name="stepDirectory"/> lead, and the groups
    /// its legs' work leads, are recorded while they run: a step of the run's or of an agent's alike.
    /// </summary>
    /// <param name="stepDirectory">The step's own directory.</param>
    public static string ChildrenDirectory(string stepDirectory) => Path.Combine(stepDirectory, ChildrenDirectoryName);

    /// <summary>The record of the session or the group <paramref name="session"/>, which a child of the step at <paramref name="stepDirectory"/> leads.</summary>
    /// <param name="stepDirectory">The step's own directory.</param>
    /// <param name="session">The id of the session or the group: its leader's process id.</param>
    public static string ChildFile(string stepDirectory, int session)
        => Path.Combine(ChildrenDirectory(stepDirectory), session.ToString(System.Globalization.CultureInfo.InvariantCulture) + ChildExtension);

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

    /// <summary>
    /// The run's state in one file: every step instance and what runs next. Shaped for a resume; written after the
    /// changed step's own files and before every other record of a change - the run's status, its issues and the event.
    /// </summary>
    public string FlowFile => Path.Combine(Directory, FlowFileName);

    /// <summary>The run's status, for a person and a client to read: every step of the definition with its status, times and instances, the run's own, and the progress of it.</summary>
    public string WorkflowStatusFile => Path.Combine(Directory, WorkflowStatusFileName);

    /// <summary>The issues the run found and left: each proven, with a title and a description.</summary>
    public string PreExistingIssuesFile => Path.Combine(Directory, PreExistingIssuesFileName);

    /// <summary>The orchestrator entry as it ran, frozen: what a resume runs, never the file the command read.</summary>
    public string DefinitionFile => Path.Combine(Directory, DefinitionFileName);

    /// <summary>The run's events, append-only, one JSON object to a line.</summary>
    public string EventsFile => Path.Combine(Directory, EventsFileName);

    /// <summary>Every question the run raised and every answer it was given, the open ones among them: the one owner of what a question said.</summary>
    public string QuestionsFile => Path.Combine(Directory, QuestionsFileName);

    /// <summary>The run's write-ahead ledger of its folds: each path a fold means to write, the digest it held before and the one the fold left.</summary>
    public string FoldsFile => Path.Combine(Directory, FoldsFileName);

    /// <summary>The run's codebase graph: derived from the tree, so removed with the run and kept by nothing else.</summary>
    public string GraphDirectory => Path.Combine(Directory, GraphDirectoryName);

    /// <summary>The base's manifest: every file the graph keeps, with its blob, its state and its extract's name.</summary>
    public string GraphManifestFile => Path.Combine(GraphDirectory, GraphManifestFileName);

    /// <summary>The base's link result: every node and edge, with the digest of the manifest it was made from.</summary>
    public string GraphSymbolsFile => Path.Combine(GraphDirectory, GraphSymbolsFileName);

    /// <summary>Where every file's extract is kept, once, each named by <see cref="DssHarness.Core.Codebase.CodeGraphIds.ExtractName"/>.</summary>
    public string GraphExtractsDirectory => Path.Combine(GraphDirectory, GraphExtractsDirectoryName);

    /// <summary>One line for each sync of the graph: when, which view, why, and what it listed, read and reused.</summary>
    public string GraphSyncFile => Path.Combine(GraphDirectory, GraphSyncFileName);

    /// <summary>Where each agent's view of the graph is kept: what its worktree holds that the base does not.</summary>
    public string GraphViewsDirectory => Path.Combine(GraphDirectory, GraphViewsDirectoryName);

    /// <summary>The view of the graph of the agent <paramref name="agent"/>'s worktree.</summary>
    /// <param name="agent">The agent's name.</param>
    public string GraphViewFile(string agent)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agent);
        return Path.Combine(GraphViewsDirectory, agent + GraphExtractExtension);
    }

    /// <summary>The extract named <paramref name="name"/> (<see cref="DssHarness.Core.Codebase.CodeGraphIds.ExtractName"/>).</summary>
    /// <param name="name">Its name, under the extracts directory.</param>
    public string GraphExtractFile(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return Path.Combine(GraphExtractsDirectory, name.Replace('/', Path.DirectorySeparatorChar));
    }

    /// <summary>What another process asked of the running engine: a safe pause, or a stop. Read by the engine at its poll, and taken.</summary>
    public string StopFile => Path.Combine(Directory, StopFileName);

    /// <summary>What a person gave from another terminal while the engine runs, read as <see cref="StopFile"/> is, and emptied of each thing once the engine has written it where it belongs.</summary>
    public string InboxFile => Path.Combine(Directory, InboxFileName);

    /// <summary>The run's report, written at the end of every session, however it ended.</summary>
    public string ReportFile => Path.Combine(Directory, ReportFileName);

    /// <summary>The run's report as one JSON document, beside its markdown.</summary>
    public string ReportJsonFile => Path.Combine(Directory, ReportJsonFileName);

    /// <summary>
    /// Where the calls of the planned <c>ask-orchestrator</c> and <c>ask-orchestrator-status</c> will be kept, one
    /// directory each - neither command is built yet, so nothing writes it. The engine never reads them.
    /// </summary>
    public string AsksDirectory => Path.Combine(Directory, AsksDirectoryName);

    /// <summary>
    /// The run's live owner: its process stamp, beside the run's directory. A live owner refuses a session; a dead one
    /// is reclaimed and said.
    /// </summary>
    public string OwnerFile => Path.Combine(Path.GetDirectoryName(Directory) ?? Directory, Name + OwnerFileName);

    /// <summary>
    /// Where one step instance's own directory is - the one owner of where a step is kept: inside the agent it names
    /// with <c>in</c> or <c>of</c>, a reason step's too, and under the run for every step that names none.
    /// </summary>
    /// <param name="instance">The step instance's id, as <see cref="Flow.FlowInstanceId"/> spells it.</param>
    /// <param name="agent">The agent the step names; null where it names none.</param>
    public string InstanceDirectory(string instance, string? agent)
        => agent is null
            ? StepDirectory(Flow.FlowInstanceId.DirectoryOf(instance))
            : AgentStepDirectory(agent, Flow.FlowInstanceId.DirectoryOf(instance));

    /// <summary>Where a refresh or a rebase of <c>others</c> kept at <paramref name="stepDirectory"/> keeps what it did to <paramref name="agent"/>.</summary>
    /// <param name="stepDirectory">The step's own directory.</param>
    /// <param name="agent">The target's name.</param>
    public static string TargetDirectory(string stepDirectory, string agent) => Path.Combine(stepDirectory, StepTargetsDirectoryName, agent);

    /// <summary>The handoff of one step instance, in its own directory (<see cref="InstanceDirectory"/>).</summary>
    /// <param name="instance">The step instance's id, as <see cref="Flow.FlowInstanceId"/> spells it.</param>
    /// <param name="agent">The agent the step names; null where it names none.</param>
    public string InstanceHandoffFile(string instance, string? agent) => Path.Combine(InstanceDirectory(instance, agent), HandoffFileName);

    /// <summary>The record of one step the run keeps and no agent owns, in the step's own directory, named as the run spells an instance on disk.</summary>
    /// <param name="instance">The step instance's id, as <see cref="Flow.FlowInstanceId"/> spells it.</param>
    public string StepRecordFile(string instance) => Path.Combine(StepDirectory(Flow.FlowInstanceId.DirectoryOf(instance)), StepRecordFileName);

    /// <summary>The output of one step the run keeps and no agent owns, in the step's own directory.</summary>
    /// <param name="instance">The step instance's id, as <see cref="Flow.FlowInstanceId"/> spells it.</param>
    public string StepOutputFile(string instance) => Path.Combine(StepDirectory(Flow.FlowInstanceId.DirectoryOf(instance)), StepOutputFileName);

    /// <summary>The handoff of one step the run keeps and no agent owns, in the step's own directory.</summary>
    /// <param name="instance">The step instance's id, as <see cref="Flow.FlowInstanceId"/> spells it.</param>
    public string StepHandoffFile(string instance) => Path.Combine(StepDirectory(Flow.FlowInstanceId.DirectoryOf(instance)), HandoffFileName);

    /// <summary>The record of one step <paramref name="agent"/> runs, or that runs in or for its worktree, in the step's own directory.</summary>
    /// <param name="agent">The agent's name.</param>
    /// <param name="instance">The step instance's id, as <see cref="Flow.FlowInstanceId"/> spells it.</param>
    public string AgentStepRecordFile(string agent, string instance) => Path.Combine(AgentStepDirectory(agent, Flow.FlowInstanceId.DirectoryOf(instance)), StepRecordFileName);

    /// <summary>The output of one step <paramref name="agent"/> runs, in the step's own directory.</summary>
    /// <param name="agent">The agent's name.</param>
    /// <param name="instance">The step instance's id, as <see cref="Flow.FlowInstanceId"/> spells it.</param>
    public string AgentStepOutputFile(string agent, string instance) => Path.Combine(AgentStepDirectory(agent, Flow.FlowInstanceId.DirectoryOf(instance)), StepOutputFileName);
}
