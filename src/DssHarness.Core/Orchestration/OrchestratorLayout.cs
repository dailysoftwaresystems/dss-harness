using System.Security.Cryptography;
using System.Text;
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
/// temp/&lt;launch&gt;/             every temporary directory a run's launch and its children keep: one directory a launch, held for the run's life
/// artifacts/                  the run's artifacts, kept for the run's life and copied wherever asked; both go with the run's directory
/// &lt;step&gt;/step.json            in either kind of step's directory: the step's own record
/// &lt;step&gt;/output.json          the step's output, validated
/// &lt;step&gt;/handoff.md           the handoff a stop cut the step off with, until its next attempt is given it
/// &lt;step&gt;/policy.json          a reason step: the policy it started under, the agents' configuration as each start read it
/// &lt;step&gt;/untracked-at-start.jsonl   a reason step whose writes narrow its worktree: the paths the worktree held untracked as the step first started, which tell its own making apart
/// &lt;step&gt;/ledger.json          a leg step: its legs' ledger, as --json gives it
/// &lt;step&gt;/logs/                a leg step in an agent's worktree: each red leg's phase log that did not pass; a ci-wait: each red job's log
/// &lt;step&gt;/fold.json            a fold family step: what its dry run named and stood, its question, each consent given, how its apply ended
/// &lt;step&gt;/commit-message.txt   a commit step: the message it commits with
/// &lt;step&gt;/pull-request-body.md  a pull-request step: the body it opens the pull request with, its file filled
/// &lt;step&gt;/graph.jsonl          a reason step that has a codebase graph: one line for each call of the graph tool; nothing writes it yet
/// &lt;step&gt;/graph-pages/         a reason step given the graph tool or read_result: the whole result of each graph ask of more than one page, and where each page of a call's whole result begins, which their later pages read
/// &lt;step&gt;/working-set.json     a reason step that has a codebase graph: the nodes it edited, served, read and found, which seed the packs of the steps after it; nothing writes it yet
/// &lt;step&gt;/targets/&lt;agent&gt;/     a refresh or a rebase of others: one target's fold.json
/// &lt;step&gt;/web.jsonl            a step whose agent asks the web: each address and each search source asked, decided before it was asked and what it answered; written by every client whose step has the web
/// &lt;step&gt;/denials.jsonl        a step whose agent's calls the engine decides, on every client: each call the policy refused, with its rule
/// &lt;step&gt;/tool-results/call-&lt;digest&gt;.txt   a step whose agent's calls the engine decides, on every client: an answer longer than a page, whole, named by the digest of the call's id, which read_result gives a page at a time
/// &lt;step&gt;/children/&lt;id&gt;.json   in either kind of step's directory, on Linux and macOS: a session a contained child of the step leads while it runs, or the process group a phase of its legs - a set-up's up line apart - its keepAwake command or what carries a leg to a host leads while anything is in it
/// flow.json                   a run's state, whole: the truth a resume goes on from
/// workflow.json               a run's status, for a person and a client to read
/// definition.json             the workflow's definition, frozen as the run began with it
/// questions.json              every question the run raised and every answer it was given
/// updates.json                every plan update the run has taken, and what became of it
/// folds.json                  the run's write-ahead ledger of its folds, and of each commit a commit step staged of them
/// pre-existing-issues.json    each pre-existing issue the run listed
/// affected-tests.json         the test units each test step that keeps them ran, under the key it names, written before its legs run
/// events.jsonl                the run's events, one JSON object to a line
/// report.md, report.json      the report of the run's last session
/// inbox.json, stop.json       an answer, and a pause or a stop, handed to the engine running the run, until it takes them
/// asks/                       one call of ask-orchestrator or ask-orchestrator-status: the facts it was given, the files of its reporter's attempt, ask.json; the engine never reads it
/// web/searches.jsonl          what each source of the web search found once for the run, and each query it answered: the same query again is answered from here; written by every client whose step searches
/// graph/graph.json            the run's codebase graph: the base's manifest, every file it keeps with its blob, state and extract
/// graph/symbols.jsonl         the base's link result, as lines: every node and edge, with the digest of the manifest it was made from
/// graph/map.md, map.json      the run's repository map of the steps no read rule narrows, made once from the base as it stood when the first of them was given its prompt, and what it was made from; read as it is by every one after
/// graph/map.2.md, and on      the map's pages after its first, written with it and never again, each a whole page of the same size
/// graph/map-&lt;reads&gt;.md, .json  the same, of the steps whose read rules are the same, named by their digest: no map holds a declaration of a file the steps given it may not read
/// graph/extracts/&lt;ab&gt;/&lt;name&gt;   one file's extract, written once, named by its blob, its path, its implementation and version
/// graph/sync.jsonl            one line for each sync of the graph
/// graph/views/&lt;agent&gt;.json    an agent's view of the graph: what its worktree holds that the base does not
/// ../&lt;run&gt;.owner.json        beside the run's directory: the process whose engine holds the run
/// ../.web-index/&lt;source&gt;/    beside every orchestrator: the harness's index of a sitemap source's site, its state, pages and fill's record
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

    /// <summary>The name of the file a run's issues owed its next round are kept in.</summary>
    public const string NextRoundFileName = "next-round.json";

    /// <summary>The name of the file an agent's item keeps its closure ledger in, under the agent's directory.</summary>
    public const string ClosureLedgerFileName = "closure-ledger.json";

    /// <summary>The name of the file a run's record of affected tests is kept in.</summary>
    public const string AffectedTestsFileName = "affected-tests.json";

    /// <summary>The name of the file the orchestrator entry is frozen in, as it ran.</summary>
    public const string DefinitionFileName = "definition.json";

    /// <summary>The name of the file a run's events are appended to, one JSON object to a line.</summary>
    public const string EventsFileName = "events.jsonl";

    /// <summary>The name of the file every question a run raised and every answer it was given is kept in, whole.</summary>
    public const string QuestionsFileName = "questions.json";

    /// <summary>The name of the file every plan update a run has taken is kept in, whole.</summary>
    public const string UpdatesFileName = "updates.json";

    /// <summary>The name of the file what another process asked of the running engine is kept in, until it is taken.</summary>
    public const string StopFileName = "stop.json";

    /// <summary>The name of the file what a person gave from another terminal is kept in, until the engine has written it where it belongs.</summary>
    public const string InboxFileName = "inbox.json";

    /// <summary>The name of the file a run's report is written in, at the end of every session.</summary>
    public const string ReportFileName = "report.md";

    /// <summary>The name of the file a run's report is written in as one JSON document, beside its markdown.</summary>
    public const string ReportJsonFileName = "report.json";

    /// <summary>
    /// The directory one call of <c>ask-orchestrator</c> and <c>ask-orchestrator-status</c> is kept in - the facts it was
    /// given, the files of its reporter's attempt, and <c>ask.json</c>. The engine never reads it.
    /// </summary>
    public const string AsksDirectoryName = "asks";

    /// <summary>
    /// The directory one call of <c>analyze-orchestrator-report</c> is kept in - the files of its analyst's attempt, and
    /// the analysis it answered with, beside the report it read. The engine never reads it.
    /// </summary>
    public const string AnalysisDirectoryName = "analysis";

    /// <summary>The name of the file an analysis is kept in, with the report it read named by its hash.</summary>
    public const string AnalysisFileName = "analysis.json";

    /// <summary>The name of the file an analysis is said in as Markdown.</summary>
    public const string AnalysisMarkdownFileName = "analysis.md";

    /// <summary>The name of the file an analysis keeps the report it read in: the very bytes whose hash it names.</summary>
    public const string AnalysisReportFileName = "report.json";

    /// <summary>The name of the file a run's live owner is kept in, beside the run's directory.</summary>
    public const string OwnerFileName = ".owner.json";

    /// <summary>The name of the file a step's own record is kept in, in its directory.</summary>
    public const string StepRecordFileName = "step.json";

    /// <summary>The step's policy as it started under it: the agents' configuration as resolved - agentPolicy, the web search it asks, what it runs on - read when the step starts, as its prompts are.</summary>
    public const string PolicyFileName = "policy.json";

    /// <summary>
    /// The name of the file a reason step whose <c>writes</c> narrow what it writes in its worktree keeps, in its directory,
    /// the worktree's untracked paths in as the step first started: what tells the step's own making apart, which it may
    /// delete whatever its writes say (<see cref="Agents.Policy.AgentStepStart"/>); written once, and read by every later
    /// attempt, answer and resume of the step.
    /// </summary>
    public const string StepUntrackedFileName = "untracked-at-start.jsonl";

    /// <summary>The name of the file a step's output is kept in, validated, in its directory.</summary>
    public const string StepOutputFileName = "output.json";

    /// <summary>
    /// The name of the file a reason step's notes are kept in, masked, in its directory: what its agent left for whoever
    /// goes on, as its answer's envelope said it.
    /// </summary>
    public const string StepNotesFileName = "notes.md";

    /// <summary>The name of the file a step's first user message is kept in, masked, in its directory: as sent, holding the parts it was given.</summary>
    public const string StepUserFileName = "user.md";

    /// <summary>The name of the file a leg step keeps its legs' ledger in, as <c>--json</c> gives it, in its directory.</summary>
    public const string StepLedgerFileName = "ledger.json";

    /// <summary>
    /// The name of the directory a leg step in an agent's worktree keeps, whole, the log of each red leg's phase that did
    /// not pass, as <c>&lt;leg&gt;.&lt;phase&gt;.log</c>, in its directory.
    /// </summary>
    public const string StepLogsDirectoryName = "logs";

    /// <summary>The name of the run's write-ahead ledger of its folds, and of each commit a commit step staged of them.</summary>
    public const string FoldsFileName = "folds.json";

    /// <summary>The name of the file a <c>commit</c> step writes the message it commits with to, in its directory, as <c>git commit -F</c> reads it.</summary>
    public const string StepCommitMessageFileName = "commit-message.txt";

    /// <summary>The name of the file a <c>pull-request</c> step writes its body to, filled, in its directory, as the forge's tool reads it.</summary>
    public const string StepPullRequestBodyFileName = "pull-request-body.md";

    /// <summary>
    /// The name of the file a fold family step keeps in its directory - or in its target's, for a step of <c>others</c>:
    /// what its dry run named and stood, the question it asked, each consent it was given and how its apply ended.
    /// </summary>
    public const string StepFoldFileName = "fold.json";

    /// <summary>The name of the file a reason step that has a codebase graph keeps a line for each call of the <c>graph</c> tool in, in its directory; nothing writes it yet.</summary>
    public const string StepGraphFileName = "graph.jsonl";

    /// <summary>
    /// The name of the directory a conversation keeps its kept answers in (<see cref="Pages.KeptAnswers{TKept}"/>) - a
    /// reason step's, in its own directory, and a side-task's, in its records: one file an ask - the whole result of a graph
    /// ask of more than one page, under the digest of the graph it was computed on, and where each page of a call's whole
    /// result begins, under the digest of its file - so a later page, a resumed conversation and one that goes on with the
    /// step's work read pages of that one result. The one spelling of the name.
    /// </summary>
    public const string StepPagesDirectoryName = "graph-pages";

    /// <summary>
    /// The name of the file a reason step that has a codebase graph keeps its working set in, in its directory: the nodes it
    /// edited, served, read and found, which seed the packs of the steps after it; nothing writes it yet.
    /// </summary>
    public const string StepWorkingSetFileName = "working-set.json";

    /// <summary>The name of the directory a refresh or a rebase of <c>others</c> keeps one directory for each target in, in its own.</summary>
    public const string StepTargetsDirectoryName = "targets";

    /// <summary>The directory a run's codebase graph is kept in: made from the tree, and made again whenever asked.</summary>
    public const string GraphDirectoryName = "graph";

    /// <summary>
    /// The name of the directory a run keeps the remote workflows it began with in, in the workflow cache's own shape: what
    /// its steps read as they run, so no pruning of the user's cache takes a file from a run that started.
    /// </summary>
    public const string RemoteWorkflowsDirectoryName = "remote-workflows";

    /// <summary>The name of the file the base's manifest is kept in, in the graph's directory.</summary>
    public const string GraphManifestFileName = "graph.json";

    /// <summary>The name of the file the base's link result is kept in, in the graph's directory.</summary>
    public const string GraphSymbolsFileName = "symbols.jsonl";

    /// <summary>The name of the file the run's repository map's first page is kept in, in the graph's directory, made once and read as it is.</summary>
    public const string GraphMapFileName = "map.md";

    /// <summary>The name of the file what the run's repository map was made from is kept in, in the graph's directory.</summary>
    public const string GraphMapMetaFileName = "map.json";

    /// <summary>The name of the directory every file's extract is kept in, once, in the graph's directory.</summary>
    public const string GraphExtractsDirectoryName = "extracts";

    /// <summary>The name of the file one line is appended to for each sync of the graph, in its directory.</summary>
    public const string GraphSyncFileName = "sync.jsonl";

    /// <summary>The name of the directory each agent's view of the graph is kept in, in the graph's directory.</summary>
    public const string GraphViewsDirectoryName = "views";

    /// <summary>The extension of a file's extract.</summary>
    public const string GraphExtractExtension = ".json";

    /// <summary>The directory, beside every orchestrator, of the harness's indexes of sites: a name no orchestrator has, for none begins with a dot.</summary>
    public const string WebIndexesDirectoryName = ".web-index";

    /// <summary>The directory a run keeps what its web search found in.</summary>
    public const string WebDirectoryName = "web";

    /// <summary>The name of the file, in the run's web directory, of what each source found once and each query it answered.</summary>
    public const string WebSearchesFileName = "searches.jsonl";

    /// <summary>
    /// What the web search of the run whose directory is <paramref name="runDirectory"/> keeps: what each source found once
    /// for the run, and each query it answered, so the same query again is answered from here with no request.
    /// </summary>
    /// <param name="runDirectory">The run's directory.</param>
    public static string WebSearchesFile(string runDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runDirectory);
        return Path.Combine(runDirectory, WebDirectoryName, WebSearchesFileName);
    }

    /// <summary>
    /// Where the harness's index of the site of the web-search source named <paramref name="source"/> is kept: under the
    /// repository's orchestrators, so every run of the repository searches it and a later run starts from it.
    /// </summary>
    /// <param name="layout">The repository.</param>
    /// <param name="source">The source's name.</param>
    /// <exception cref="ArgumentException"><paramref name="source"/> is no source's name, which a directory is named by.</exception>
    public static string WebIndexDirectory(HarnessLayout layout, string source)
    {
        ArgumentNullException.ThrowIfNull(layout);

        if (!Web.Search.WebSearchSource.IsName(source))
        {
            throw new ArgumentException($"'{source}' is no web-search source's name: {Web.Search.WebSearchSource.NameRule}.", nameof(source));
        }

        return Path.Combine(layout.OrchestratorsDirectory, WebIndexesDirectoryName, source);
    }

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
    public string AgentSessionDirectory(string agent, string id) => SessionDirectoryIn(AgentSessionsDirectory(agent), id);

    /// <summary>
    /// Where one conversation is kept among an agent's: its own directory in <paramref name="sessions"/>, named by
    /// <see cref="SessionDirectoryName"/> - the one owner of how a conversation's id names its directory, so the run that
    /// keeps a conversation and the one that goes on with it find the same directory.
    /// </summary>
    /// <param name="sessions">The agent's sessions directory (<see cref="AgentSessionsDirectory"/>).</param>
    /// <param name="id">The conversation's id: <see cref="IsSessionId"/>.</param>
    /// <exception cref="ArgumentException">The id is no conversation's id, and would name another directory.</exception>
    public static string SessionDirectoryIn(string sessions, string id)
    {
        ArgumentNullException.ThrowIfNull(sessions);

        return IsSessionId(id)
            ? Path.Combine(sessions, SessionDirectoryName(id))
            : throw new ArgumentException($"'{id}' is no conversation's id: {SessionIdShape}.", nameof(id));
    }

    /// <summary>
    /// The name of the directory a conversation is kept in: its id, where the id is no longer than a SHA-256 digest is
    /// written - every directory an earlier release made is so found where it made it - and otherwise the id's SHA-256
    /// digest, in lower-case hexadecimal. A provider's ids are its own and of any length, and a directory's name is held
    /// to what the file system takes; named by its digest, an id of any length names a directory every file system takes,
    /// one id always the same one.
    /// </summary>
    /// <param name="id">The conversation's id: <see cref="IsSessionId"/>.</param>
    public static string SessionDirectoryName(string id)
    {
        ArgumentNullException.ThrowIfNull(id);

        return id.Length <= SHA256.HashSizeInBytes * 2
            ? id
            : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(id)));
    }

    /// <summary>What a conversation's id is, as a refusal says it.</summary>
    public const string SessionIdShape = "letters, digits, '-' and '_'";

    /// <summary>
    /// Whether <paramref name="id"/> is a conversation's id - the engine's own, and a handed one alike: at least one
    /// character, each a letter, a digit, <c>-</c> or <c>_</c>, as many as it has. Its directory is named by
    /// <see cref="SessionDirectoryName"/>, so no length of the id is refused for the directory it names.
    /// </summary>
    /// <param name="id">The id.</param>
    public static bool IsSessionId(string? id)
        => id is { Length: > 0 } && id.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

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
    /// Where every temporary directory of the run's launches is kept (<see cref="HarnessLayout.OrchestratorTempDirectoryName"/>):
    /// one directory for each launch - its children's temporary directory, and Claude Code's own process's beside it -
    /// made with the launch and held for the run's life, so everything the run's children leave as temp is put away with
    /// the run and taken away only with its directory. The one owner of the shape: a launch names its own directory
    /// under it, and never spells the run's temp for itself.
    /// </summary>
    public string TempDirectory => Path.Combine(Directory, Repository.HarnessLayout.OrchestratorTempDirectoryName);

    /// <summary>Where a launch of the run at <paramref name="runDirectory"/> keeps its temporary directory: the name the
    /// launch is known by, in the run's temp.</summary>
    /// <param name="runDirectory">The run's own directory.</param>
    /// <param name="name">The name the launch's directory is known by in the run's temp.</param>
    public static string LaunchTemp(string runDirectory, string name)
        => Path.Combine(Path.Combine(runDirectory, Repository.HarnessLayout.OrchestratorTempDirectoryName), name);

    /// <summary>
    /// Where the run's artifacts are kept (<see cref="HarnessLayout.OrchestratorArtifactsDirectoryName"/>), for the run's
    /// life: made with the run, read and copied wherever they are asked for while it runs, and taken away with the run's
    /// directory - which is the whole of the mechanism, for what a run keeps as artifacts is in its directory until it goes.
    /// </summary>
    public string ArtifactsDirectory => Path.Combine(Directory, Repository.HarnessLayout.OrchestratorArtifactsDirectoryName);

    /// <summary>
    /// Where the run keeps the files its own inputs of the kind <c>file</c> were given (<see cref="HarnessLayout.OrchestratorInputsDirectoryName"/>):
    /// made with the run, each file copied there as the run starts and kept for its life, and taken away with the run's
    /// directory. The one owner of the shape: what a step reads for such an input is the copy's path here
    /// (<see cref="InputCopy"/>), and every agent of the run reads this directory (<see cref="InputsDirectoryOf"/>).
    /// </summary>
    public string InputsDirectory => InputsDirectoryOf(Directory);

    /// <summary>Where the run at <paramref name="runDirectory"/> keeps the files its own inputs of the kind <c>file</c> were given (<see cref="InputsDirectory"/>).</summary>
    /// <param name="runDirectory">The run's own directory.</param>
    public static string InputsDirectoryOf(string runDirectory)
        => Path.Combine(runDirectory, Repository.HarnessLayout.OrchestratorInputsDirectoryName);

    /// <summary>
    /// The copy the run keeps of the file its input <paramref name="input"/> was given as <paramref name="given"/>:
    /// <c>inputs/&lt;input&gt;/&lt;the file's own name&gt;</c>, a directory an input, so a step reads it by the name the person
    /// gave it.
    /// </summary>
    /// <param name="input">
    /// The input's name: a word of letters, digits and dashes, as every declared name is, and apart from every other input's
    /// in more than case, as the definition's check holds it - so two inputs never share one directory on a file system
    /// that folds case.
    /// </param>
    /// <param name="given">The path the input was given, relative to the repository's root.</param>
    public string InputCopy(string input, string given)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(given);

        return Path.Combine(InputsDirectory, input, Path.GetFileName(Path.TrimEndingDirectorySeparator(given)));
    }

    /// <summary>
    /// The run's state in one file: every step instance and what runs next. Shaped for a resume; written after the
    /// changed step's own files and before every other record of a change - the run's status, its issues and the event.
    /// </summary>
    public string FlowFile => Path.Combine(Directory, FlowFileName);

    /// <summary>The run's status, for a person and a client to read: every step of the definition with its status, times and instances, the run's own, and the progress of it.</summary>
    public string WorkflowStatusFile => Path.Combine(Directory, WorkflowStatusFileName);

    /// <summary>The issues the run found and left: each proven, with a title and a description.</summary>
    public string PreExistingIssuesFile => Path.Combine(Directory, PreExistingIssuesFileName);

    /// <summary>The issues the run owes its next round, with their size: what <c>run.nextRound</c> reads.</summary>
    public string NextRoundFile => Path.Combine(Directory, NextRoundFileName);

    /// <summary>The item's closure ledger, in its agent's directory: how often each of its classes a review reopened.</summary>
    /// <param name="agent">The agent whose item the ledger is of.</param>
    public string ClosureLedgerFile(string agent) => Path.Combine(AgentDirectory(agent), ClosureLedgerFileName);

    /// <summary>The test units each test step that keeps them ran, under the key it names: what <c>run.affectedTests</c> reads.</summary>
    public string AffectedTestsFile => Path.Combine(Directory, AffectedTestsFileName);

    /// <summary>The orchestrator entry as it ran, frozen: what a resume runs, never the file the command read.</summary>
    public string DefinitionFile => Path.Combine(Directory, DefinitionFileName);

    /// <summary>The run's events, append-only, one JSON object to a line.</summary>
    public string EventsFile => Path.Combine(Directory, EventsFileName);

    /// <summary>Every question the run raised and every answer it was given, the open ones among them: the one owner of what a question said.</summary>
    public string QuestionsFile => Path.Combine(Directory, QuestionsFileName);

    /// <summary>Every plan update the run has taken, with what raised it and where it stands: the one owner of what an update is.</summary>
    public string UpdatesFile => Path.Combine(Directory, UpdatesFileName);

    /// <summary>
    /// The run's write-ahead ledger of its folds: each path a fold means to write, the digest it held before and the one
    /// the fold left; and of each commit a commit step staged of them, and what became of it.
    /// </summary>
    public string FoldsFile => Path.Combine(Directory, FoldsFileName);

    /// <summary>The run's codebase graph: derived from the tree, so removed with the run and kept by nothing else.</summary>
    public string GraphDirectory => Path.Combine(Directory, GraphDirectoryName);

    /// <summary>
    /// The remote workflows the run began with, every file of each as the check that let it start read and hashed it, kept
    /// in the workflow cache's own shape beside its frozen definition: what its steps read a remote workflow's files from
    /// as they run, and what no pruning of the user's cache reaches. Removed with the run.
    /// </summary>
    public string RemoteWorkflowsDirectory => Path.Combine(Directory, RemoteWorkflowsDirectoryName);

    /// <summary>The base's manifest: every file the graph keeps, with its blob, its state and its extract's name.</summary>
    public string GraphManifestFile => Path.Combine(GraphDirectory, GraphManifestFileName);

    /// <summary>The base's link result: every node and edge, with the digest of the manifest it was made from.</summary>
    public string GraphSymbolsFile => Path.Combine(GraphDirectory, GraphSymbolsFileName);

    /// <summary>The run's repository map of the steps no read rule narrows, made once and read as it is by every one after.</summary>
    public string GraphMapFile => GraphMapFileOf(reads: null);

    /// <summary>
    /// The run's repository map of the steps whose read rules <paramref name="reads"/> names - their digest - made once
    /// and read as it is by every step of those rules: <see cref="GraphMapFileName"/> where it names none.
    /// </summary>
    /// <param name="reads">The digest of the steps' read rules; null for steps no read rule narrows.</param>
    public string GraphMapFileOf(string? reads) => GraphMapPageFileOf(reads, 1);

    /// <summary>
    /// The file page <paramref name="page"/> of the run's repository map of the steps whose read rules
    /// <paramref name="reads"/> names is kept in: the first in <see cref="GraphMapFileName"/> and every one after it
    /// beside it (<c>map.2.md</c> and on), all written when the map is, the first last.
    /// </summary>
    /// <param name="reads">The digest of the steps' read rules; null for steps no read rule narrows.</param>
    /// <param name="page">The page, from 1.</param>
    public string GraphMapPageFileOf(string? reads, int page)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        return Path.Combine(GraphDirectory, reads is null
            ? (page == 1 ? GraphMapFileName : $"map.{page.ToString(System.Globalization.CultureInfo.InvariantCulture)}.md")
            : (page == 1 ? $"map-{reads}.md" : $"map-{reads}.{page.ToString(System.Globalization.CultureInfo.InvariantCulture)}.md"));
    }

    /// <summary>What the map of the steps whose read rules <paramref name="reads"/> names was made from, kept beside it.</summary>
    /// <param name="reads">The digest of the steps' read rules; null for steps no read rule narrows.</param>
    public string GraphMapMetaFileOf(string? reads) => Path.Combine(GraphDirectory, reads is null ? GraphMapMetaFileName : $"map-{reads}.json");

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
    /// Where the calls of <c>ask-orchestrator</c> and <c>ask-orchestrator-status</c> are kept, one directory each - the
    /// facts it was given, the files of its reporter's attempt, and <c>ask.json</c>. The engine never reads them.
    /// </summary>
    public string AsksDirectory => Path.Combine(Directory, AsksDirectoryName);

    /// <summary>Where the run's analyses are kept, one directory a call, beside the reports they read. The engine never reads it.</summary>
    public string AnalysisDirectory => Path.Combine(Directory, AnalysisDirectoryName);

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

    /// <summary>
    /// The folder, in a step's own directory, of what the engine hands the step's agent to read: the skills its prompt
    /// names and the copies its continuation record names. The tool alone writes it, in the launch's own directory, which
    /// no command of the agent's writes, so nothing a command planted there leads a write of the tool's elsewhere; the
    /// agent reads it as a root of its own.
    /// </summary>
    public const string HandedDirectoryName = "handed";

    /// <summary>Where a step's agent is handed what it reads (<see cref="HandedDirectoryName"/>), in the step's own directory.</summary>
    /// <param name="stepDirectory">The step's own directory.</param>
    public static string HandedDirectory(string stepDirectory) => Path.Combine(stepDirectory, HandedDirectoryName);

    /// <summary>Where a refresh or a rebase of <c>others</c> kept at <paramref name="stepDirectory"/> keeps what it did to <paramref name="agent"/>.</summary>
    /// <param name="stepDirectory">The step's own directory.</param>
    /// <param name="agent">The target's name.</param>
    public static string TargetDirectory(string stepDirectory, string agent) => Path.Combine(stepDirectory, StepTargetsDirectoryName, agent);

    /// <summary>The message a <c>commit</c> step commits with, in its own directory: a commit step names no agent.</summary>
    /// <param name="instance">The step instance's id, as <see cref="Flow.FlowInstanceId"/> spells it.</param>
    public string InstanceCommitMessageFile(string instance) => Path.Combine(InstanceDirectory(instance, null), StepCommitMessageFileName);

    /// <summary>The body a <c>pull-request</c> step opens its pull request with, in its own directory: the step names no agent.</summary>
    /// <param name="instance">The step instance's id, as <see cref="Flow.FlowInstanceId"/> spells it.</param>
    public string InstancePullRequestBodyFile(string instance) => Path.Combine(InstanceDirectory(instance, null), StepPullRequestBodyFileName);

    /// <summary>The handoff of one step instance, in its own directory (<see cref="InstanceDirectory"/>).</summary>
    /// <param name="instance">The step instance's id, as <see cref="Flow.FlowInstanceId"/> spells it.</param>
    /// <param name="agent">The agent the step names; null where it names none.</param>
    public string InstanceHandoffFile(string instance, string? agent) => Path.Combine(InstanceDirectory(instance, agent), HandoffFileName);

    /// <summary>The record of one step the run keeps and no agent owns, in the step's own directory, named as the run spells an instance on disk.</summary>
    /// <param name="instance">The step instance's id, as <see cref="Flow.FlowInstanceId"/> spells it.</param>
    public string StepRecordFile(string instance) => Path.Combine(StepDirectory(Flow.FlowInstanceId.DirectoryOf(instance)), StepRecordFileName);

    /// <summary>The handoff of one step the run keeps and no agent owns, in the step's own directory.</summary>
    /// <param name="instance">The step instance's id, as <see cref="Flow.FlowInstanceId"/> spells it.</param>
    public string StepHandoffFile(string instance) => Path.Combine(StepDirectory(Flow.FlowInstanceId.DirectoryOf(instance)), HandoffFileName);

    /// <summary>The record of one step <paramref name="agent"/> runs, or that runs in or for its worktree, in the step's own directory.</summary>
    /// <param name="agent">The agent's name.</param>
    /// <param name="instance">The step instance's id, as <see cref="Flow.FlowInstanceId"/> spells it.</param>
    public string AgentStepRecordFile(string agent, string instance) => Path.Combine(AgentStepDirectory(agent, Flow.FlowInstanceId.DirectoryOf(instance)), StepRecordFileName);

    /// <summary>
    /// The places of a run's own directory, and of each agent's in it, whose writers keep bytes as they came - copied in,
    /// or written by an agent, its commands or a provider - never through the mask a session keeps the run's records
    /// through: each agent's Claude transcripts, its scratch and task files, the plans, the files the run's file inputs
    /// were given, the run's artifacts, the remote workflows' files as they were fetched; and of each agent the bytes of
    /// each file it was handed from the main tree, the anchor rows it files, what its worktree's evidence roots held, and
    /// its conversations with an API client, whose messages are the provider's own bytes. The one owner of the set, beside
    /// the places it is of (<see cref="AttemptPlacesKeptAsTheyCame"/> for the places of an attempt's directory).
    /// </summary>
    public static IReadOnlyList<RunPlace> RunPlacesKeptAsTheyCame { get; } =
    [
        new($"{LogsDirectoryName}/*", "each agent's Claude transcripts, copied as they are"),
        new(WorkDirectoryName, "each agent's scratch and task files"),
        new(PlansDirectoryName, "the plans, the orchestrator's and each agent's"),
        new(HarnessLayout.OrchestratorInputsDirectoryName, "the files the run's file inputs were given, copied"),
        new(HarnessLayout.OrchestratorArtifactsDirectoryName, "the run's artifacts"),
        new(RemoteWorkflowsDirectoryName, "the remote workflows' files, as they were fetched"),
        new($"{AgentsDirectoryName}/*/{SharedDirectoryName}", "the bytes of each file an agent was handed from the main tree"),
        new($"{AgentsDirectoryName}/*/{RowsDirectoryName}", "the anchor rows an agent files"),
        new($"{AgentsDirectoryName}/*/{EvidenceDirectoryName}", "what an agent's worktree's evidence roots held, copied"),
        new($"{AgentsDirectoryName}/*/{SessionsDirectoryName}", "an agent's conversations with an API client, the provider's messages as they came"),
    ];

    /// <summary>
    /// The directories of a run that each hold one attempt of an agent's or of the engine's work: every step's own
    /// directory - the run's, and each agent's - each advice of an asking step, and each call of <c>ask-orchestrator</c>
    /// and of <c>analyze-orchestrator-report</c>, each named below the run's directory, a <c>*</c> standing for any one
    /// name. What each holds as it came is <see cref="AttemptPlacesKeptAsTheyCame"/>.
    /// </summary>
    public static IReadOnlyList<string> AttemptDirectories { get; } =
    [
        $"{StepsDirectoryName}/*",
        $"{AgentsDirectoryName}/*/{StepsDirectoryName}/*",
        $"{StepsDirectoryName}/*/{Flow.AdvisorRunner.AdviceDirectoryName}/*",
        $"{AgentsDirectoryName}/*/{StepsDirectoryName}/*/{Flow.AdvisorRunner.AdviceDirectoryName}/*",
        $"{AsksDirectoryName}/*",
        $"{AnalysisDirectoryName}/*",
    ];

    /// <summary>
    /// The places of an attempt's directory (<see cref="AttemptDirectories"/>) whose writers keep bytes as they came: each
    /// red leg's phase log and each red CI job's log, copied - masked, where at all, by the runner's values alone, never by
    /// the session's - and each red leg's log a measure call kept; what the attempt's agent was handed to read, its skills
    /// and the copies its continuation record names, red legs' logs among them; a side agent's work directory and its
    /// conversations with an API client; and the continuation record itself, which quotes red legs' lines as they came.
    /// </summary>
    public static IReadOnlyList<RunPlace> AttemptPlacesKeptAsTheyCame { get; } =
    [
        new(StepLogsDirectoryName, "each red leg's phase log and each red CI job's log, copied"),
        new($"{Agents.AgentMeasureSupport.RecordsDirectoryName}/*/{StepLogsDirectoryName}", "each red leg's phase log a measure call kept"),
        new(HandedDirectoryName, "what the attempt's agent was handed to read: its skills, and the copies its continuation record names"),
        new(WorkDirectoryName, "a side agent's work directory"),
        new(SessionsDirectoryName, "a side agent's conversations with an API client, the provider's messages as they came"),
        new(
            $"{Agents.AgentCallRecords.ToolResultsDirectoryName}/{Path.GetFileName(Agents.AgentCallRecords.ResultFile(string.Empty, Agents.Runtime.KeptResults.ContinuationId))}",
            "the continuation record, which quotes red legs' lines as they came",
            IsFile: true),
    ];

    /// <summary>
    /// Whether <paramref name="relative"/>, a path below a run's directory spelled with forward separators, is a place whose
    /// writer keeps bytes as they came - one of <see cref="RunPlacesKeptAsTheyCame"/>, or one of
    /// <see cref="AttemptPlacesKeptAsTheyCame"/> in one of <see cref="AttemptDirectories"/> - a directory whole, or the one
    /// file a place names: the one question every reader of the run's directory that holds no session's mask asks.
    /// </summary>
    /// <param name="relative">The path below the run's directory.</param>
    /// <param name="isDirectory">Whether the path is a directory.</param>
    public static bool IsKeptAsItCame(string relative, bool isDirectory)
    {
        ArgumentNullException.ThrowIfNull(relative);

        return RunPlacesKeptAsTheyCame.Any(place => place.IsFile != isDirectory && RunPlace.Names(place.Path, relative))
            || AttemptDirectories.Any(attempt => AttemptPlacesKeptAsTheyCame.Any(place => place.IsFile != isDirectory && RunPlace.Names($"{attempt}/{place.Path}", relative)));
    }
}

/// <summary>A place of a run's directory, as <see cref="OrchestratorLayout"/> marks it.</summary>
/// <param name="Path">The place, name by name, joined by <c>/</c>, a <c>*</c> standing for any one name.</param>
/// <param name="Holds">What it holds, as a sentence says it.</param>
/// <param name="IsFile">Whether the place is one file; a directory, with all it holds, where it is not.</param>
public sealed record RunPlace(string Path, string Holds, bool IsFile = false)
{
    /// <summary>Whether <paramref name="relative"/> is the place <paramref name="place"/> names, name by name, a <c>*</c> standing for any one.</summary>
    /// <param name="place">The place's pattern.</param>
    /// <param name="relative">A path, spelled with forward separators.</param>
    public static bool Names(string place, string relative)
    {
        ArgumentNullException.ThrowIfNull(place);
        ArgumentNullException.ThrowIfNull(relative);

        var names = place.Split('/');
        var path = relative.Split('/');

        return names.Length == path.Length && names.Zip(path).All(pair => pair.First == "*" || string.Equals(pair.First, pair.Second, StringComparison.Ordinal));
    }
}
