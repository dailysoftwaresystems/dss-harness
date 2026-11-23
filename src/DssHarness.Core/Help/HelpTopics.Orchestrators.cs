using System.Globalization;
using System.Text;
using DssHarness.Core.Ci;
using DssHarness.Core.Anchors;
using DssHarness.Core.Agents.Runtime;
using DssHarness.Core.Codebase;
using DssHarness.Core.Build;
using DssHarness.Core.Configuration;
using DssHarness.Core.Execution;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Git;
using DssHarness.Core.Hosts;
using DssHarness.Core.Legs;
using DssHarness.Core.Mutations;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Orchestration.Flow;
using DssHarness.Core.Output;
using DssHarness.Core.Platform;
using DssHarness.Core.Processes;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;
using DssHarness.Core.Runners;
using DssHarness.Core.Runs;
using DssHarness.Core.Secrets;
using DssHarness.Core.SetUps;
using DssHarness.Core.Sync;
using DssHarness.Core.Tools;
using DssHarness.Core.Waiting;
using DssHarness.Core.Worktrees;

namespace DssHarness.Core.Help;

public static partial class HelpTopics
{
    private static string RenderOrchestrators()
    {
        var builder = new StringBuilder();
        var command = ToolPackage.Command;

        builder.AppendLine("Orchestrators and their agents");
        builder.AppendLine();
        AppendWrapped(
            builder,
            "An orchestrator is a session that runs agents side by side, each in a worktree of its own. Everything it and its "
            + $"agents keep is in the main checkout, under {HarnessLayout.OrchestratorsDirectoryName}/<orchestrator>/, never in a "
            + "worktree, so another session - on another account, or after this one ends - takes the work over by reading it:");
        builder.AppendLine();
        AppendColumns(
            builder,
            [
                ($"  {OrchestratorLayout.RecordFileName}", "its record: name, model, createdAt, parallel, session"),
                ($"  {OrchestratorLayout.LogsDirectoryName}/<name>{OrchestratorLayout.LogExtension}", "a JSON line for each run that reached it or one of its agents, refusals included; a dry run writes none"),
                ($"  {OrchestratorLayout.LogsDirectoryName}/<agent>/", "the agent's Claude transcripts, kept when it is deleted"),
                ($"  {OrchestratorLayout.WorkDirectoryName}/<agent>/", "the agent's scratch and task files"),
                ($"  {OrchestratorLayout.PlansDirectoryName}/<name>/", "plans: its own, and a directory for each agent"),
                ($"  {OrchestratorLayout.AgentsDirectoryName}/<agent>/{OrchestratorLayout.RecordFileName}", "the agent's record: name, model, createdAt, base, state, closing"),
                ($"  {OrchestratorLayout.AgentsDirectoryName}/<agent>/{OrchestratorLayout.SeedFileName}", "what it shares with the main tree: each path handed or folded"),
                ($"  {OrchestratorLayout.AgentsDirectoryName}/<agent>/{OrchestratorLayout.SharedDirectoryName}/<sha256>", "the bytes of each file it shares, kept to merge from"),
                ($"  {OrchestratorLayout.AgentsDirectoryName}/<agent>/{OrchestratorLayout.RowsDirectoryName}/<ID>/", "an anchor row it files, a file for each cell"),
                ($"  {OrchestratorLayout.AgentsDirectoryName}/<agent>/{OrchestratorLayout.AppliedRowsFileName}", "the rows its folds applied, each as declared then"),
                ($"  {OrchestratorLayout.AgentsDirectoryName}/<agent>/{OrchestratorLayout.EvidenceDirectoryName}/", "its evidence roots' files, kept when it is deleted"),
                ($"  {OrchestratorLayout.AgentsDirectoryName}/<agent>/{OrchestratorLayout.SessionsDirectoryName}/<id>/", "API clients only: one conversation with the provider, in the provider's own form"),
                ($"  {OrchestratorLayout.AgentsDirectoryName}/<agent>/{OrchestratorLayout.StepsDirectoryName}/<instance>/", "one step the agent runs, or that runs in or for its worktree: everything of the step, its handoff.md where one stopped it"),
                ($"  {OrchestratorLayout.StepsDirectoryName}/<instance>/", "one step of the run that no agent owns, the create-agent that makes an agent included; every step's own record holds what its work measured (`measure` in step.json and flow.json: when it was ready, its waits by cause whole across sessions, its run time, its pause, and one entry per attempt - what served it, the price copied as its cost was counted, the tokens by kind, the dollars, the turns, the tools, the compactions - with the advisor's spend beside them; the run's `spent` is the sum of its steps'), and an attempt a crash cut off has its measure completed at the resume from the step's stored stream"),
                ($"  {HarnessLayout.OrchestratorTempDirectoryName}/<launch>/", "every temporary directory a run's launch and its children keep, one directory a launch - its children's and Claude Code's own process's beside it - made with the launch, held for the run's life and never in the user's own places; taken away whole with the run's directory"),
                ($"  {HarnessLayout.OrchestratorArtifactsDirectoryName}/", "the run's artifacts, made with the run and kept for its life: read and copied wherever they are asked for while it runs, and taken away with the run's directory"),
                ($"  <step>/{OrchestratorLayout.ChildrenDirectoryName}/<session>{OrchestratorLayout.ChildExtension}", "in either kind of step, on Linux and macOS: a session a contained child of the step leads while it runs, or the process group a phase of its legs - a set-up's up line apart - its keepAwake command or what carries a leg to a host leads while anything is in it"),
                ($"  {OrchestratorLayout.FlowFileName}", "a run's state in one file - the truth a resume goes on from"),
                ($"  {OrchestratorLayout.WorkflowStatusFileName}", "a run's status for a person: each step of its definition, its instances, progress and what it waits on"),
                ($"  {OrchestratorLayout.DefinitionFileName}", "the workflow's definition as the run began with it"),
                ($"  {OrchestratorLayout.QuestionsFileName}", "every question the run raised, its answers, and who gave each"),
                ($"  {OrchestratorLayout.FoldsFileName}", "the ledger of the run's folds: each path a fold means to write, what the main tree held there before it and what it left, and each commit a commit step staged of them and what became of it"),
                ($"  <step>/{OrchestratorLayout.StepCommitMessageFileName}", "a commit step: the message it commits with"),
                ($"  <step>/{OrchestratorLayout.StepPullRequestBodyFileName}", "a pull-request step that opened one: the body it opened it with"),
                ($"  <step>/{OrchestratorLayout.StepFoldFileName}", "a fold, delete-agent, refresh or rebase step: what its dry run named and stood, the question it asked, each consent given by name, how its apply ended"),
                ($"  <step>/{OrchestratorLayout.StepTargetsDirectoryName}/<agent>/", "a refresh or a rebase of others: what it did to each target, as above"),
                ($"  <step>/{OrchestratorLayout.StepGraphFileName}", "a reason step that has a codebase graph: the line of its prompt - what its pack cost, or why none was given - one line for each call of the graph tool, and one for each escape of its narrowing of reads and each whole read that narrowing refused"),
                ($"  <step>/{OrchestratorLayout.StepWorkingSetFileName}", "a reason step that has a codebase graph: the nodes it edited, served, read and found, which seed the packs of the steps after it ('help codebase-graph')"),
                ($"  <step>/{Agents.AgentCallRecords.WebFileName}", "a step whose agent asks the web: each address its web_fetch and web_search decided before it was asked, what each answered, and each search source's part ('help web-search'); written by every client whose step has the web"),
                ($"  <step>/{Agents.AgentCallRecords.DenialsFileName}", "a step whose agent's calls the engine decides, on every client: each call the policy refused, with its rule"),
                ($"  <step>/{Agents.AgentCallRecords.ToolResultsDirectoryName}/{Pages.PageKey.CallPrefix}<digest>.txt", "a step whose agent's calls the engine decides, on every client: an answer longer than a page, whole, named by the digest of the call's id, which read_result gives a page at a time"),
                ($"  {OrchestratorLayout.WebDirectoryName}/{OrchestratorLayout.WebSearchesFileName}", "the run's web search: what each source's lookups found, once for the run, and each query a source answered, which the same query again is answered from; written by every client whose step searches"),
                ($"  {OrchestratorLayout.GraphDirectoryName}/{OrchestratorLayout.GraphManifestFileName}", "the run's codebase graph: the main checkout's manifest, every file it keeps with its blob, state and extract ('help codebase-graph')"),
                ($"  {OrchestratorLayout.GraphDirectoryName}/{OrchestratorLayout.GraphSymbolsFileName}", "the graph's link result: every node and edge, with the digest of the manifest it was made from"),
                ($"  {OrchestratorLayout.GraphDirectoryName}/{OrchestratorLayout.GraphExtractsDirectoryName}/<ab>/<name>", "one file's extract, written once, named by its blob, its path, its implementation and version"),
                ($"  {OrchestratorLayout.GraphDirectoryName}/{OrchestratorLayout.GraphSyncFileName}", "one line for each sync of the graph"),
                ($"  {OrchestratorLayout.GraphDirectoryName}/{OrchestratorLayout.GraphViewsDirectoryName}/<agent>{OrchestratorLayout.GraphExtractExtension}", "an agent's view of the graph: what its worktree holds that the base does not"),
                ($"  {OrchestratorLayout.PreExistingIssuesFileName}", "each pre-existing issue the run listed, proven"),
                ($"  {OrchestratorLayout.AffectedTestsFileName}", "the run's record of affected tests: the units each test step that keeps them (affected) ran, under the key it filled, written before the step runs - which run.affectedTests reads"),
                ($"  {OrchestratorLayout.EventsFileName}", "the run's history, one event to a line, written after the state it tells of"),
                ($"  {OrchestratorLayout.ReportFileName}, {OrchestratorLayout.ReportJsonFileName}", "the report of the run's last session"),
                ($"  {OrchestratorLayout.InboxFileName}, {OrchestratorLayout.StopFileName}", "an answer, and a pause or a stop, handed to the engine running the run"),
                ($"  ../<run>{OrchestratorLayout.OwnerFileName}", "beside the run's directory: the process whose engine holds the run"),
                ($"  ../{OrchestratorLayout.WebIndexesDirectoryName}/<source>/", "beside every run: the repository's index of a sitemap search source's site - its state, pages and fill's record"),
            ]);
        builder.AppendLine();
        AppendWrapped(
            builder,
            $"A row's cells are {string.Join(", ", AgentRows.RequiredCells.Select(cell => cell + AgentRows.CellExtension))}, and "
            + $"{AnchorCellNames.Priority}{AgentRows.CellExtension} where it declares one, each read as write-anchor reads a cell file. "
            + "An agent's worktree is <worktrees.root>/<orchestrator>/<agent>, a git repository of its own that borrows the main "
            + "checkout's objects ('help worktrees'), which list-worktree and delete-worktree name "
            + "orchestrator/agent; the copies hosts keep of it are named orchestrator--agent. An agent can also be made with no "
            + $"worktree ({AgentService.CreateCommand} --no-worktree), reading the main tree and writing only its scratch: the engine "
            + "makes one for a read-only reason step, and a person has no use for one by hand. It records no session - --session "
            + "beside --no-worktree is a usage error - and delete-agent marks it deleted in one write, folding nothing, removing no "
            + "worktree and looking for no transcript; --discard-uncommitted and the allowances have nothing to act on there, and "
            + "it says so. Agents write their handoffs, pause "
            + "and stop points as .md files in their own directory. Every command that writes an agent's records names where they are "
            + "on each way out once it has: its record, its seed, the directory of the bytes it shares where it keeps any, and the rows "
            + "its folds applied - each that is there.");
        builder.AppendLine();
        builder.AppendLine("Commands");
        builder.AppendLine($"  {command} {OrchestratorService.CreateCommand} <o> --model <id> [--parallel <n>] [--session <id>]");
        builder.AppendLine($"  {command} {AgentService.CreateCommand} <o> <a> --model <id> [--empty | --no-worktree] [--session <id>]");
        builder.AppendLine($"  {command} {AgentService.SeedCommand} <o> <a> [--empty] [--force]");
        builder.AppendLine($"  {command} {AgentService.RefreshCommand} <o> <a> [<path>... | {RefreshRequest.AllOption}] [{RefreshRequest.ExceptOption} <path>]...");
        builder.AppendLine($"      [{RefreshRequest.StaleOption} <path>]... [{FoldAllowances.MergeOption} <path>]... [--apply] [--json]");
        var paths = $"[{FoldAllowances.MergeOption} <path>]... [{FoldAllowances.SettledOption} <path>]...";
        builder.AppendLine($"  {command} {AgentService.RebaseCommand} <o> <a> [--apply] [--json] {paths}");
        var rows = $"[{AnchorBatchRequest.NewOption} <ID>]... [{AnchorBatchRequest.AcceptLostOption} {AnchorRowCell.Form}]...";
        var over = $"[{AnchorBatchRequest.AcceptOverOption} {AnchorRowCell.Form}]...";
        builder.AppendLine($"  {command} {AgentService.FoldCommand} <o> <a> [--apply] [--json] {paths}");
        builder.AppendLine($"      {rows} {over}");
        builder.AppendLine($"  {command} {AgentService.DeleteCommand} <o> <a> [--apply] [--json] {paths}");
        builder.AppendLine($"      {rows} {over}");
        builder.AppendLine("      [--discard-uncommitted]");
        builder.AppendLine($"  {command} {OrchestratorService.ListCommand} [<o>] [--steps] [--questions] [--json]");
        builder.AppendLine($"  {command} {OrchestratorService.DeleteCommand} <o> [--delete-evidence]");
        builder.AppendLine($"  {command} {FlowService.RunCommand} <workflow> [--run <name>] [--input <k=v>]... [--json]");
        builder.AppendLine($"  {command} {FlowService.ResumeCommand} <run> [--json]");
        builder.AppendLine($"  {command} {FlowService.AnswerCommand} <run> <answer> [--question <id>] [--text <text> | --text-file <file>]");
        builder.AppendLine("      [--again] [--json]");
        builder.AppendLine($"  {command} {FlowService.PauseCommand} <run> [--all] [--json]");
        builder.AppendLine($"  {command} {FlowService.StopCommand} <run> [--now] [--json]");
        builder.AppendLine($"  {command} {FlowService.ReadReportCommand} <run> [--steps <instance>...] [--by step|agent|model|provider|type]");
        builder.AppendLine("      [--top <n>] [--markdown] [--json]");
        builder.AppendLine();
        AppendWrapped(
            builder,
            $"{OrchestratorService.ListCommand} lists the orchestrators, or the one named, their agents and where each stands, "
            + "and the worktrees below an orchestrator's directory that no agent records; for a run, its own lines from flow.json "
            + "and what its engine is doing - none, running, pausing while its handoffs are written, recording the machine steps a "
            + "complete pause left running, or gone, an owner recorded whose process has ended - with each step instance (--steps: "
            + "its state, visit, attempt, agent and directory) and each question and what became of it (--questions). --json prints "
            + $"the listing as one JSON document. {StoppedDocument}");
        builder.AppendLine();
        AppendWrapped(
            builder,
            $"A run is an orchestrator a workflow's engine drives. {FlowService.RunCommand} validates the workflow and makes the run - "
            + "r<N>, one more than the highest there, or the name --run gives, held to worktrees.maxNameLength and refused where it is "
            + "taken - with its record, its state (flow.json), its status (workflow.json), its questions (questions.json), its "
            + "plan updates (updates.json), its pre-existing issues and its events, and claims it for its process (<run>.owner.json beside the run's directory). "
            + "A run starts from the main checkout's HEAD on a branch of its own, and records that commit and branch: it is refused "
            + $"({HarnessExit.Refused}), having made nothing, where HEAD is detached, where the checkout is on the branch the run's pull "
            + "request is opened against - the pull-request step's base, filled from the run's inputs and its name, else the "
            + "remote's default branch - where a merge is under way, and where changes are staged. Where the repository has a "
            + "remote and neither base can be told, the start says so, its branch held to none. "
            + "Only one run of a repository is unfinished with work in the tree at once: a start, or a session of another run, is "
            + $"refused ({HarnessExit.Refused}) while one is, and so it is where an agent's record of another run cannot be read. Each "
            + $"session drives the run until nothing more can run: done ({HarnessExit.Success}), failed ({HarnessExit.CommandFailed}, or "
            + $"{HarnessExit.Incomplete} where every failure that ended it was incomplete after its retries), paused on a question "
            + $"({FlowExit.Paused}), or paused or stopped on request ({HarnessExit.Cancelled}); it writes the run's report (report.md and "
            + "report.json) and names each record it wrote, in text and in --json, whichever way it ends. "
            + $"{FlowService.ResumeCommand} goes on with a run from its records alone, where it stood, under the definition it began "
            + "with - refused where the repository's entry for it changed since, which is read in the main checkout whichever tree "
            + "the command was typed in, as a start reads it - reclaiming the owner of an engine that has gone "
            + "and saying so: what a stop or a crash cut off, and a failure that ended the run, run again as new attempts, save a "
            + "failure an answer fail ended and a step a plan update ended in order, which stays ended; what that failure kept "
            + "from running is drawn again; what the run owed - a failure or "
            + "an ending a pause recorded, the triage of an issue, a redirect - is made first; and every question and answer the run "
            + "has is kept. It goes on beside a question still open, and is refused, having written nothing, only where nothing "
            + "can run until one is answered - a session an answer began, or one whose budget question is open, pauses on it "
            + "instead. A record the run is made with that is missing is refused by name. "
            + $"{FlowService.AnswerCommand} answers a question: handed to the engine running the "
            + $"run through inbox.json, which it reads every {FlowTimings.Default.PollEvery.TotalSeconds.ToString(CultureInfo.InvariantCulture)} s, "
            + "or else a session of its own; with --again it changes the last answer a person gave - the one set the command shows - "
            + "within the minutes the workflow's amend.changedAnswerMinutes names and once, a change handed to a running engine "
            + "taken at the poll that first reads it: both answers are kept, each with when it "
            + $"was given, in questions.json, and a pause step whose answer changed is visited again done at once with the new one. "
            + $"{FlowService.AmendCommand} amends the request a run was started with: handed to the engine running it through "
            + "inbox.json and taken at the poll that first reads it - weighed against the run as it stands then, never only when "
            + "the drive ends - or else recorded in updates.json as a plan update queued, taken first when the run is next gone on with - "
            + "the command starts nothing; the request as it was given is never rewritten, and from the taking on the input "
            + "amend.input names reads as the value as given then each amendment under a line that says when it was made, in "
            + "{input.<name>} and every "
            + "expression alike, while a preAnswer's quote is looked for in the value as given and never in an amendment. Where the "
            + "run has begun the scope its plan updates are planned in, the change is taken by raising the step its amend.step "
            + "names - a new visit of it in the newest instance of that scope, ahead of everything else - and the run is held "
            + "while it plans: nothing starts outside that scope and what is in flight is untouched, the step's prompts read what "
            + "raised the change ({update.cause}, {update.since} and {update.work}), and its amend.then runs the check it names "
            + "again by the redirect's machinery, until that scope has come to rest and the plan stands - or the step fails and "
            + "its onFailure gives the update up, no plan holding the change. Once the plan stands, the pieces its amend.scope "
            + "expression reads are placed: an added piece makes one more work item of the work-item group, and a changed or a "
            + "removed piece finds the instance that carries its work item - given the piece's item in the place of its own "
            + "where it waits, or withdrawn before it began; brought to rest, nothing more started in it, and visited again in "
            + "its place with its agent where it has begun - the instance it ends stays ended, its record saying so, no step "
            + "of it, at any depth of a group nested in it, is given a new attempt - one that fails, asks, passes its time "
            + "limit, or a pause or a stop stopped, ends stopped for the update, as each nested group does once the instance "
            + "comes to rest - and no session after runs it again; one more work item of the group, with new agents, where it has "
            + "ended - and where the round it joins has not opened, the piece is owed to it and joins at the check that opens "
            + "the round, over that round's own items and the owed ones as one list. Each item's instance is of one kind - "
            + "added, changed or removed - and the step its kind names runs in the place of the one amend.whenAddScope names, "
            + "under its name, the other two skipped saying why; the steps of the group read {piece.kind}, {piece.changed}, "
            + "{piece.keep}, {piece.undo}, {piece.instead} and {piece.before}, the engine's account of the work a piece that "
            + "runs again or takes back starts from. A validate-plan step checks the pieces with the items where its scope "
            + "names them, so a piece that cannot be placed comes back to the planner as a problem of the plan. A told piece "
            + "tells: a reason step in flight that one names - its own instance, or a scope around it - is sent one message "
            + "into the conversation it is in, which lands when its calls have finished and never cuts its turn; one that "
            + "waits on a question of its own is told with the answer; one that starts or goes on later in work a piece "
            + "names reads the same in a part of its own prompt; a reason step in flight that no piece names is sent nothing "
            + "and listed as not concerned, and a piece that names nothing under way is not delivered and recorded with why - "
            + "as who was told what is, in updates.json and the report alike. A reason step ended in order is told its piece "
            + "first, as the lead of its handoff question. Where the round a piece joins has ended, the work is owed to the "
            + "loop's next iteration, which runs whatever its until says - a loop that ended done runs again for it; only where "
            + "that loop is at its own bound is one iteration added for the update. With no loop around the work-item group, "
            + "a round of the group is added for the work: the group runs once more, through the check that opens a round and "
            + "every step between it and the group - the check handed the owed work with its own items as one list - and the "
            + "round carries the owed work alone; a change given while a round added waits to open, before the check that "
            + "opens it has started, joins that round, and one given after it is refused until the round has opened, which "
            + "it then joins. What an update done owes is carried at the next session's start where a crash came first; work "
            + "owed to a loop at its bound where the run has none left to add is left to the question its bound asks, which "
            + "the loop reaches whatever its until says, the update's entry saying so once that question is asked, and no "
            + "later session runs again a loop its answer ended. An iteration "
            + "or a round is added once per update and at most amend.maxAddedIterations over the run, published and said in "
            + "the report and list-orchestrator - as is a loop's bound asked for the work - with every piece, where it was "
            + "carried out and how that ended - done, failed, "
            + "skipped or never run, or that no instance carried it; where the run has none left to add, or a group around the "
            + "work-item group ended otherwise than done and draws nothing more, an update whose work only an added round would "
            + "carry is given up with why, its pieces never carried out. Updates are "
            + "taken one at a time: one that arrives "
            + "while another is under way is queued behind it, in the order given, and one the run can no longer take at its "
            + "turn is recorded refused with why. Both are "
            + "refused, nothing changed, where the run is done, past the step its amend.before names, or past its work items - "
            + "the last round it can run ended, of a loop at its bound with every iteration amend.maxAddedIterations allows, or "
            + "of a group with no loop around it that runs no round more, none waiting to open, or a round added waiting to "
            + "open whose check has run - and "
            + "where the definition the run began with takes no such change. "
            + $"{FlowService.PauseCommand} asks that engine for the safe pause through stop.json - its agents hand off, the machine "
            + "steps finish unless --all, then the run is paused - and waits until it is complete; "
            + $"{FlowService.StopCommand} asks it to stop where it stands and waits while the engine runs, for as long as that is, "
            + $"saying so every {Waits.SaidEvery.TotalMinutes.ToString(CultureInfo.InvariantCulture)} minutes - its own Ctrl+C ends the wait, the stop staying asked - "
            + "and --now ends its process: only on this machine, and only the process its owner names by its start. "
            + $"{OrchestratorService.DeleteCommand} refuses a run a live engine owns, and removes a gone engine's owner with the run. In the terminal "
            + "a session runs in, the first Ctrl+C - and off Windows SIGTERM or a hang-up - asks the safe pause, which starts no "
            + "clock of its own: each agent is asked for its handoff in its own conversation and answers within the workflow's "
            + "handoffSeconds, and a build may take as long as it takes. The second stops the run where it stands - a "
            + $"step whose runner does not end within {PointOfNoReturn.SafePauseDrain.TotalSeconds.ToString(CultureInfo.InvariantCulture)} s is recorded stopped all the same - and "
            + $"the command ends within {PointOfNoReturn.HardStopBound.TotalSeconds.ToString(CultureInfo.InvariantCulture)} s whatever is left; "
            + $"what is left then is what a crash leaves, and {FlowService.ResumeCommand} goes on from the records. The steps served so "
            + $"far are {string.Join(", ", FlowStepRunners.Served)} ('{ToolPackage.Command} help workflows'): a step of any other type "
            + "fails, saying no runner serves its type.");
        builder.AppendLine();
        AppendWrapped(
            builder,
            $"Asking a run about itself. {FlowService.AskStatusCommand} asks the tool's own question of a run - what is happening now, "
            + "what is done, what is left, what it waits for, and when it may finish - and "
            + $"{FlowService.AskCommand} a person's, both answered with the engine's facts, computed from the run's records and never "
            + "guessed: how the run stands and for how long, its engine and its pause; each step instance in progress with its type, "
            + "its agent, a reason step's model, its attempt and how long it has run; what it waits for - each open question with "
            + "since when and the command that answers it, a plan update under way or queued, an instance waiting on another, on "
            + "the resume or on room - a claim, the run to be quiet, a counter of its limits - a ci-wait reading the checks of the "
            + "commit the run last pushed; what is done, the failures a handler took and what was spent of the budget; what is left "
            + "at least and at most - a stopped instance among it, for it runs again at the resume - with the bound that gave the "
            + "most, by the engine's own bounds, so the most is a bound: each member of a scope still open not drawn yet, a loop's "
            + "iterations to its bound past the ones it made, a handler's visits to its maxVisits in each scope its source can "
            + "still end in with the steps its then sends the run back over at each visit, and the recovery's visits to its "
            + "maxVisits for each step whose failure it can take with the steps it sends the run back over, each widened by every "
            + "more its bound was answered and every iteration a plan update added, and each piece of a plan update not carried "
            + "out; what no numeric bound holds - a forEach not yet expanded, a repeat with no numeric bound, a step whose "
            + "pre-existing issues each raise a triage, a handler that sends the run back outside its own group, a plan update "
            + "still to be planned - and a question open at a bound, whose more can grant past it, named - "
            + "and when it may finish, as a range that rests on this run alone: of each step of the definition that works, the "
            + "shortest and the longest time an instance of it took to end Done - a group's own span is its members' time, never "
            + "counted beside it - the lower end laying what is left along what depends on what, as the engine orders it, so work "
            + "that runs side by side is counted once, the upper end summing every instance that is left and everything the run "
            + "may still make, one after another; a step no instance of which has ended Done in this run is counted and named "
            + "and in neither end, the time a person takes to answer and a wait for room are left out and said to be, and where "
            + "nothing has ended or the run has ended - done or failed; a stopped run goes on at its resume - there is no "
            + "estimate, and the facts say which. Where "
            + "the workflow's reporter names an agent, its words follow the facts - it is the run's own reporter, offered the tools "
            + "that read and no other, reading the run's directory and nowhere else, its answer masked and what it costs the "
            + "person's, never the run's; each call keeps its own record under the run's asks/, the facts as given, the files of its "
            + "reporter's attempt, the conversations its client stores (sessions/) and ask.json, a directory the engine never reads. Both commands only read: no state file, no "
            + "step, no agent, no budget, no lock an engine needs - they take no lock at all - and they work while a run goes on, is "
            + "paused, waits, or has ended. Where no model words them the status still prints the facts, says no model worded this "
            + "and why, and exits 0: the facts are the answer, and the words were a service; the person's question is refused - 12 "
            + "where the workflow names no reporter or the reporter's own files do not read, 13 where the key is not there, 14 "
            + "where npm or Claude Code is missing or too "
            + "old, 20 where the model failed, 21 where it could not be reached or its account is at its limit - and neither "
            + "command installs Claude Code or brings one up to date: that is a run's to do, and these only read. An orchestrator "
            + "made by hand, which no flow runs, has no reporter and fewer facts: its status prints what list-orchestrator has of it "
            + "and says so, and the person's question is refused (12).");
        builder.AppendLine();
        AppendWrapped(
            builder,
            $"The report of a run. {FlowService.ReadReportCommand} answers it from the run's records alone - the same bytes for "
            + "the same records, whatever a model might say - and never asks one: what the run was and how it stands, with its "
            + "sessions and its times and its budget; the totals of its tokens by kind and its dollars by model; one row per step "
            + "instance with its state, its attempts, its waits by cause, its run time, its model, its tokens and its dollars; the "
            + "critical path, the chain of steps a faster run must shorten, each with what it waited for; every attempt that "
            + "failed; every question a person answered, how long each waited and what was recommended; what its reviews "
            + "converged to - the classes closed and reopened, the hotspots, what its loops left - and the signals a reader "
            + "starts from: the slowest and most expensive steps, the most retried, every fallback, the longest wait for a "
            + "person, queue waits over a share of a step's own time, a cache share of input under the bound on an attempt "
            + "whose input reaches past it, a review at its bound, a class reopened twice, a hotspot, spend that could not be "
            + "counted. Their thresholds are keys of defaults.report in config.json. The rows can be kept to named instances "
            + "(--steps), the dollars regrouped (--by step, agent, model, provider or type) and the signal lists bounded "
            + "(--top). A run still going is answered too, said so at the time of its last record and marked partial; the "
            + "command only reads, and the report the session's end writes (report.md, report.json) is this same report. "
            + "--markdown answers the same as Markdown for a pull request, a ticket or the clipboard, and --json as one "
            + "JSON document.");
        builder.AppendLine();
        AppendWrapped(
            builder,
            "What a killed process left running. On Linux and macOS a contained child of a step leads a session of its own, and "
            + "what a machine step's legs, and a reason step's measure call's, start a process group of its own, apart from the engine's terminal; nothing ends either "
            + "when the process that started it is killed, so each session, and the group of each phase - a set-up's up line apart - "
            + $"keepAwake command and ssh that carries a leg to a host, is recorded in its step's {OrchestratorLayout.ChildrenDirectoryName}/ "
            + "while it runs, and the record taken back once nothing is left in it: a group still holding what its child left at its "
            + "own end keeps it. Every command over an orchestrator - the three "
            + "orchestrator commands, each agent command, and each run verb over a run already made - first ends each recorded "
            + "session or group whose starter has gone, its leader standing or not (SIGTERM, then SIGKILL five seconds later), "
            + "removes its record, and says so in a line, and in the document's "
            + "ended where it answers with --json; stop-orchestrator --now does it again once it has ended the engine and its "
            + "tree, and says what it ended. A session or group whose starter still runs is left; one whose id a later process "
            + "carries ends nothing, and its record goes; a record that does not read as one is said and left; a starter on another "
            + "machine is never taken to have gone. Windows records nothing: a job ends a contained or bound child with the process "
            + "that started it.");
        builder.AppendLine();
        AppendWrapped(
            builder,
            $"--parallel, {OrchestratorRecord.DefaultParallel} unless given, is the most open agents at once - every agent with a "
            + "worktree not yet deleted, and every worktree git lists below the orchestrator's directory: create-agent refuses the "
            + "next, naming them, and list-orchestrator counts them. An agent with no worktree is never open. Run again, "
            + "create-orchestrator changes only the limit and the session, and create-agent records only the session, and nothing "
            + "of an agent with no worktree; for an agent whose making stopped part way, it says so and names what finishes it. "
            + "An agent's name is never used twice: a deleted agent's directory stays as its history.");
        builder.AppendLine();
        AppendWrapped(
            builder,
            "Seeding. create-agent hands the agent the main tree's uncommitted state - every path git status lists, off the "
            + $"worktrees root, .git, .orchestrators, {HarnessLayout.MutationsDirectoryName}, .legs and .side-tasks - and "
            + $"{HarnessLayout.FormerMutationsDirectoryName}, where an earlier version kept its mutation workers: each changed file copied into its worktree, each deletion made there - and "
            + "records what it was handed, each file with its digest. An untracked directory git will not look into, a repository "
            + "of its own, is named and not handed; --empty hands it nothing. A symbolic link among it is refused before the "
            + "worktree is made, so nothing is left behind. seed-agent does it again, refused over changes of the agent's own - a "
            + "copy it was handed and left alone is not one - unless --force; --empty hands it nothing more, keeping what it was "
            + "handed before. Seeding again hands besides every path the main tree holds otherwise than the agent shares it, "
            + "committed or not. refresh-agent hands a live agent every such path under the paths given - the anchor registries' "
            + $"directory where none are, and anywhere in the tree with {RefreshRequest.AllOption}, which takes no path beside it: one "
            + "whose main-tree copy is not what the agent was last handed or folded, where it was, "
            + "put back as the main tree's HEAD holds it included, and otherwise one the main tree holds otherwise than the agent's "
            + "base, committed since or not. It is a dry run until --apply, refused, copying nothing, where the agent changed or "
            + "deleted one, and records them as handed to it, so its fold leaves them out; one the agent already holds as the main "
            + "tree does is recorded as shared - listed, and written only with --apply - so its record says what it holds; a "
            + "symbolic link the main tree committed "
            + "is named and never handed, and so is a submodule's entry. A file the main tree turned into a directory, or a "
            + "directory it turned into a file, is handed as git holds it: what goes is removed first, then what comes is copied. "
            + "A path in a submodule the agent's worktree records - where the main tree turned one into files of its own - refuses "
            + "create-agent before its worktree is made, and seed-agent and refresh-agent, writing nothing: a hand-over never "
            + "writes into another repository, and rebase-agent brings in what the main tree committed there. "
            + "A refresh refused over the agent's changes names every path it changed, and the arguments that leave them: "
            + $"{RefreshRequest.ExceptOption} <path>, once for each, hands it every other path and leaves that one as the agent "
            + "changed it, its own work for its fold to weigh against what the main tree holds - said in the report, never "
            + "silently; it is not a --force, nothing is left out that nobody named, and one naming a path the refresh would not "
            + $"have refused is refused, nothing copied. {RefreshRequest.StaleOption} <path>, once for each, says its copy is not the agent's "
            + "work and hands it the main tree's - taken only where the agent's copy, as git would store it, is a blob the main "
            + "tree's history held at that path, in any commit of its whole history that changed it, and refused "
            + "otherwise, nothing copied, as is one naming a path the agent holds no file at. The refusal over the agent's changes "
            + "names each such copy with the commit whose bytes it holds, and the arguments that hand them. It reaches a path the main tree did not move as well - a copy put "
            + "back by hand to an older version - and one naming a path the agent holds as the main tree does is let stand. A path "
            + "the agent changed that the main tree moved too, each holding a file there, is weighed in full as a fold weighs one: "
            + "the refusal says beside it what a three-way merge of the two gives - from what the agent last shared, or its base - "
            + "shows the conflicts, and names the arguments that take the clean ones. Two such paths that differ in case alone, one "
            + "file in the agent's worktree, are weighed once: under the name the main tree's git gives the file, from the base's "
            + "file found in any case, whatever core.ignorecase says. "
            + $"{FoldAllowances.MergeOption} <path>, once for each, takes a clean merge: written into the agent's worktree as its "
            + "change, for its fold to write, and the path recorded as handed the main tree's copy, which the merge descends from. "
            + "It is never taken unasked, and a clean textual merge proves nothing about the program; one naming no path under the "
            + "paths refreshed is refused as a misspelling, and one naming a path with nothing left to merge is let stand. The arm "
            + "registry both changed is merged by arm, unasked, and taken where the result reads as a registry of the worktree as "
            + "the refresh leaves it; one that does not, or that conflicts, is among the agent's changes, and a --merge naming it "
            + "or an anchor registry is refused. A path named by two of "
            + $"{RefreshRequest.ExceptOption}, {RefreshRequest.StaleOption} and {FoldAllowances.MergeOption} is a usage error, exit "
            + $"{HarnessExit.UsageError}. "
            + "What the agent holds of its own where what it is handed needs room - a file or a link where a directory goes, or "
            + "files of its own in a directory a file replaces - refuses the hand-over, forced or not, naming each: copied, it "
            + "would write over them, or through the link out of its worktree. A file of the agent's written after it was weighed "
            + $"is never written over or removed by either, forced or not: the hand-over stops there, exit {HarnessExit.Incomplete}, "
            + "naming it, and run again weighs it anew. Neither hands anything while a move of the agent's "
            + "base stands stopped part way, or its HEAD is off its base. Both say when the agent's base is not the main tree's "
            + "HEAD. A path named otherwise than in UTF-8, committed or not, refuses any hand-over, naming it as git quotes it: no "
            + "file opens here by such a name.");
        builder.AppendLine();
        AppendWrapped(
            builder,
            "Rebasing. rebase-agent moves a live agent's base to the main tree's HEAD, a dry run until --apply. Each path the "
            + "two commits hold differently that the agent holds as its base does comes into its worktree as git holds it - a "
            + "change, a new file, a deletion, a file renamed in case alone, a link, a submodule's entry, a file turned into a "
            + "directory, or a directory into a file or a submodule's entry - and its own changes, and what it was handed, stay: its seed still says what it shares, "
            + "and refresh-agent hands it any of those the main tree moved since. A path it changed that the main tree committed a "
            + "change to since - an edit, a deletion, or anything of its own where the main tree committed a file, ignored or "
            + "staged included - refuses the move, nothing changed, unless --settled <path> names it: you reconciled its copy by "
            + "hand, and it stays as its own change on the new base. Where a three-way merge of the two is clean - git's, from what "
            + $"the old base held, of the new base's version and the agent's - {FoldAllowances.MergeOption} <path>, once for each, "
            + "writes it into the agent's worktree as its own change on the new base, whether it can be run merged with its "
            + "content: it is never taken unasked, and a clean textual merge proves nothing about the program. Nothing is recorded "
            + "for it: its fold weighs it against the new base. The refusal says what a merge of each such path gives, and names "
            + $"the command that takes the clean ones. A {FoldAllowances.MergeOption} naming a path the two commits hold alike is "
            + "refused as the misspelling it most likely is, and one naming any other path they changed is let stand, so the "
            + $"command that finishes a stopped move runs again; a path named with both {FoldAllowances.MergeOption} and "
            + $"{FoldAllowances.SettledOption} is a usage error, exit {HarnessExit.UsageError}; and a path one of whose versions is "
            + "a link, or that the agent reaches through one, is offered no merge. The arm registry is merged by arm, unasked, as a fold merges it (below), "
            + "from the old base's version, of the new base's as the worktree checks it out and the agent's, and judged by the "
            + "worktree as the move leaves it. A file it made that is the very file the main tree "
            + "committed there - the same content as git compares it, and the same mode where git trusts one - is no such "
            + "change: it is listed as held as the new base holds it already, and nothing is written. Whatever of the agent's "
            + "what comes in would go over refuses the move too: a file or a link where the new base holds a directory, and what it keeps - its own, settled "
            + "or shared - in a directory where the new base holds a file, or a submodule's entry that would hide it from git; each "
            + "is named, with what to do. So does a file the new base renamed in case alone, where the agent's file system finds a "
            + "name in any case, unless its old name comes in with it: where the agent keeps that - its own change, settled or "
            + "shared - writing the new name would write over it, and both are named, a shared one it changed to be folded first, "
            + "since no refresh hands it, and one it did not to be refreshed under the new name; where the agent's worktree no "
            + "longer holds it, the two are named as a rename and a deletion of one file - its old name to be put back and the "
            + "move run again, the new name deleted after only where the agent's session deleted the old - unless a move that "
            + "stopped part way removed it, as its record says, which running it again finishes. A file of the agent's written "
            + "after it was "
            + $"weighed is never written over or removed: the move stops there, exit {HarnessExit.Incomplete}, before git writes "
            + "anything, naming it. Its record names where the move goes before anything is written, then the new base's "
            + "paths are written, HEAD and the index move, and its record names the new base: a move that stopped part way, exit "
            + $"{HarnessExit.Incomplete}, is finished where it was going by running it again - the command its last words name, "
            + "with every option the move was given - what it wrote held as the new base holds it, and every other command refuses "
            + "the agent until then, naming that same command. Only such a move is ever finished, and "
            + "only from its base or where it goes: a HEAD moved by hand - back, forward or beside its base - or naming no commit "
            + "is never taken for one, and each command refuses it, saying how to put it back; a commit made inside the agent is "
            + "said to be one, and where git cannot say which a HEAD is, each says so and fails, never guessing.");
        builder.AppendLine();
        AppendWrapped(
            builder,
            "Dry runs. A dry run of refresh-agent, rebase-agent, fold-agent or delete-agent exits with what its --apply, typed "
            + "with the same arguments, would exit with at that moment: 0 only where that --apply would go through. Where a path, "
            + $"a row, or a consent nobody gave would refuse it, the dry run exits {HarnessExit.Refused} - {HarnessExit.UsageError} "
            + "where its arguments are a usage error, a path never handed to an agent among them - and where it would fail "
            + "before writing anything, with that failure's code - and either way it still prints all it weighed on standard "
            + "output, its summary last, on standard error. A run holding what the --apply takes refuses the dry run too: every "
            + "holder is named on a 'held now:' line, said with where its run's leg stands where it asked for a heavy-leg slot, "
            + $"and it exits {HarnessExit.Refused} - save where the dry "
            + "run already failed before writing anything, or met a usage error its --apply answers before it takes anything, "
            + "which keeps its code and names the holder still; "
            + $"delete-agent, whose --apply would go as far as the removal and leave it unfinished, exits {HarnessExit.Incomplete} "
            + "instead where only a copy of the worktree is held, by a run or a sweep; and where nothing refuses its --apply "
            + "before the removal - a tree held, a lock file that cannot be read, a path, a row or a consent - it exits "
            + $"{HarnessExit.Incomplete} too where the copies kept in the worktree cannot be removed as they stand, one that "
            + "cannot be told or removed or none that can be looked for, its request then refused; a directory there that no "
            + $"sync made goes with the worktree, and is only said. A record of the copies hosts keep that cannot be read refuses "
            + $"delete-agent ({HarnessExit.Refused}), its dry run and its --apply alike, before anything is written. What an "
            + "--apply can only find by "
            + "writing - a file changed after it was weighed, a write "
            + "that fails - is not in a dry run, so 0 never promises that the --apply will finish. --json answers with one "
            + "document, the whole of standard output, whose request member says what is wanted of you - 'sound' (nothing; wait, "
            + "where heldNow names a holder), 'needs-consent' (give what the dry run names: cells, rows or paths) or 'refused' (put right what the "
            + "last line names) - and whose heldNow holds each holder that decides its code: each a 'held now:' line names, with "
            + "where its leg stands - null for a leg of this machine where this machine's record of heavy legs cannot be read - "
            + "and each holding a copy kept in the "
            + "worktree, said as its own line says it, a leg copy's holder with where its leg stands, a mutation worker's with "
            + "the process, the run and since when its sweep's claim records. Beside them, needs names each consent the "
            + $"request lacks - '{OrchestrationReports.Named(ConsentKind.FoldNewRows)}' ({AnchorBatchRequest.NewOption}), "
            + $"'{OrchestrationReports.Named(ConsentKind.FoldLostRows)}' ({AnchorBatchRequest.AcceptLostOption}), "
            + $"'{OrchestrationReports.Named(ConsentKind.FoldOverCells)}' ({AnchorBatchRequest.AcceptOverOption}) "
            + $"or '{OrchestrationReports.Named(ConsentKind.FoldTwoChanges)}' ({FoldAllowances.MergeOption}) - and refusals each "
            + $"reason it is refused for, '{OrchestrationReports.Named(RefusalKind.Conflict)}' where a path both trees changed has "
            + $"a merge that conflicts, '{OrchestrationReports.Named(RefusalKind.Closure)}' where a row whose status moves to "
            + "closed holds no closing record that closes its classes - under anchors.closure: required, of a lane folded by "
            + $"hand, its searches and measurements read as given - and '{OrchestrationReports.Named(RefusalKind.Other)}' for anything else, so a request "
            + $"refused for conflicting merges alone says '{OrchestrationReports.Named(RefusalKind.Conflict)}' and nothing else; "
            + "folded says what the --apply of a fold and of a deletion did, gone through or stopped part way after writing some "
            + "of it - what it wrote, deleted, merged and handed, the arm registry merged by arm, the registries and rows it "
            + "applied, and the cells --accept-over took - and is null for every other answer and where it did nothing at all; "
            + "planned says what the plan of the verb names, its dry run's and its --apply's alike - what a rebase takes and keeps, "
            + "what a refresh or a fold hands, what each merges, what a fold, a deletion's among them, writes and deletes, the "
            + "registry files its rows change, as git names them, and the rows it applies - and what each consent it lacks and each "
            + "conflict it meets is about: mergeable, the paths both trees changed whose clean merge nobody asked for; conflicting, "
            + "those whose merge conflicts; newRows, lostCells and overCells; null where no plan was measured, a deletion that "
            + "folds nothing among them. A refresh or a rebase over a path both trees changed whose merge is clean lacks "
            + $"{FoldAllowances.MergeOption} as a fold does - needs-consent, its code {HarnessExit.Refused} still - and one over "
            + "conflicting paths alone is refused for a conflict. An --apply refused at its hold answers the same "
            + "document, applied true: request 'sound', and the run it met in heldNow. Stopped before "
            + "anything is weighed - a command line the parser refuses, a --directory that is not there, a repository not "
            + "initialised, another usage error, or a refusal met before then, an agent that is not there among them - it "
            + "answers with the same document, empty: applied false, request and heldNow null, no lines, no needs, no "
            + "refusals, and folded and planned null.");
        builder.AppendLine();
        AppendWrapped(
            builder,
            "Folding. fold-agent writes an agent's own work into the main tree, a dry run until --apply, and never removes its "
            + "worktree. Every path its status lists and every path it shares with the main tree - handed to it, or written or "
            + "removed by an earlier fold - goes in one list: its own, written; deleted, removed first, with the directories that "
            + "leaves empty, so a file it turned into a directory, or a directory into a file, makes room for what replaced it; "
            + $"inherited, shared and unchanged; already in the main tree; merged, where both trees changed it and {FoldAllowances.MergeOption} "
            + "names it, or it is the arm registry, merged by arm unasked; or settled. A shared path is compared with what "
            + "both trees held, any other with the blob at the agent's own base as git status compares, so a line-ending "
            + "conversion is no change, a changed mode is one, and a sibling's committed fold is never written over. What a fold "
            + "writes is recorded as shared: when a review sends the agent back, its later change of such a path - putting it back "
            + "as it was included - is its work to fold like any other. The whole fold is refused, and nothing written, where the "
            + "main tree changed a path the agent shares after the agent was last handed it or folded, or any other of its paths "
            + "since the agent's base - made, changed, made a directory of or removed by a commit, by an uncommitted edit or by "
            + "both, which the message says - where the "
            + "agent's HEAD is off its base, a commit made inside it among the ways, or a move of its base stands stopped part way, "
            + "for a link, a repository of its own, a path in a submodule of the main tree's - which the message says it made by a "
            + "commit or staged, and which --settled leaves out - or a path leading out of the main tree, "
            + "where what the main tree holds is in "
            + "the way of what it writes - a file where a directory goes, or files the fold does not remove in a directory a file "
            + "replaces, each said made or changed since the agent's base where the main tree did either, as any other path is, "
            + "or ignored by git - "
            + "and for an anchor registry the agent changed as a file: its rows go in through its rows directory only. "
            + "A rename of case alone the agent made, where both trees find a name in any case and the main tree's git index names "
            + "the file as before, is refused by name: a fold writes files, and the main tree's git would go on naming the file as "
            + "before - git mv -f there makes the rename, of a file in a directory renamed in case too, and the fold run again "
            + "finds the old name absent from both. The renamed file is weighed against the base's under its old name, found in "
            + "any case whatever core.ignorecase says: an edit made with the rename merges with the main tree's, and the main "
            + "tree's removal of the file refuses it, never undone. A file a tree spells on disk otherwise than its git index, "
            + "where the tree finds a name in any case, is one file whatever core.ignorecase says, weighed under the index's name - "
            + "save where the part spelled otherwise holds a character outside ASCII and core.ignorecase is false: such a name "
            + "may be composed otherwise in Unicode on disk, so it is listed under both spellings, and a fold of it stops at the "
            + "second. "
            + "A path both trees changed, each holding a file there that is not what both held, is still weighed in full: its "
            + "refusal says what a three-way merge of the two gives - git's merge, of the forms git stores, from what both held, "
            + "which for a shared path are the bytes kept beside the agent's seed, or found by their digest in the main tree's "
            + "history where none were kept, and for any other its base's - clean, with the hunks each side brought; conflicting, "
            + "with the conflicts shown; or none offered, with why - and names the command that takes the clean ones. "
            + $"{FoldAllowances.MergeOption} <path>, once for each, takes a clean merge: written into both trees, the main tree's "
            + "first, and recorded as shared once both hold it, so a fold that stopped between the two merges again to the same "
            + "bytes. It is never taken unasked, a conflict never, and a clean textual merge proves nothing about the program: "
            + "build and test the tree before you commit it, and have the agent's session read the file again before it writes "
            + $"it. A {FoldAllowances.MergeOption} naming no path the fold weighs is refused as the misspelling it most likely is, and one "
            + "naming a path with nothing left to merge is let stand, so the command line of a fold that stopped part way runs again; "
            + $"a path named with both {FoldAllowances.MergeOption} and {FoldAllowances.SettledOption} is a usage error, exit "
            + $"{HarnessExit.UsageError}, and a {FoldAllowances.MergeOption} naming an anchor registry or the arm registry is refused. "
            + "Never merged, whatever is named: a deletion against a change, a path both trees made, a version that is a link "
            + "or is reached through one, a file git reads as "
            + "binary or will not merge, one whose merge attribute asks for another merge, and an anchor "
            + "registry; nor is the arm registry ever merged by line. The arm registry (mutations.registry) both trees changed is "
            + "merged by arm, unasked - each arm, and each section's heading (the comments a blank line parts from the row below "
            + "them), from the side that alone added, removed or changed it, each version as its tree "
            + "holds it, from nothing where neither held a registry before - and taken only where the merged text reads as a "
            + "registry of the tree as the command leaves it, as check-mutations reads one ('help mutations'); an arm or a heading "
            + "the two changed differently, a heading no row of the merge would stand under, a version that cannot be read as a "
            + "registry, or a merged text that does not read as one "
            + "refuses the path, each shown, and so do shared bytes that cannot be found, since an arm one side removed would come "
            + "back. Whether a file can be run is merged with its content: the "
            + "merged file takes the bit of the side that "
            + "changed it, and the bit alone never conflicts - but a registry both trees made, one so that it can be run and one "
            + "not, is offered no merge, since nothing either held before says which is meant; Windows keeps no such bit, and "
            + "none is read or set there. "
            + "--settled <path> names a path you reconciled in the main tree by hand: once the rest is written, the main tree's copy - "
            + "a file, or its absence - is handed to the agent, over only what was weighed there, and recorded as shared, so its next "
            + "fold starts from the reconciliation. A path both trees already hold alike is recorded, one both trees and the seed "
            + "agree on is let stand, so the line that ran once runs again, and one reached through a link, or a directory git will "
            + "not look into, is left out and said to be; one only the agent changed is refused, since nothing was reconciled there "
            + "and handing the main tree's copy would undo its change. It is not a --force. A path this process "
            + "cannot look at is never read as absent: the fold fails, nothing written. A file of either tree that changed after "
            + $"it was weighed is never written over: the fold stops there, exit {HarnessExit.Incomplete}, and run again weighs it "
            + "anew. Then the rows it declares anew are applied, all or nothing, each held to what write-anchor and set-anchor "
            + "refuse - a cell they would store cut, a citation of a row neither registry nor the fold's own rows hold - and to two "
            + "rules of its own. A row no registry holds is made only where --new <ID> names it, since a typo in an existing row's "
            + "id would make it a second row; and a cell of an existing row whose stored text the agent's does not keep word for "
            + "word - neither respaced, nor kept whole in an addendum, nor filling an empty cell - is written only where "
            + "--accept-lost <ID>:<cell> names it: the dry run shows what each such cell loses, word by word, and the command that "
            + "writes it, on its line 'to write them:', and --apply refuses the fold, writing nothing, until each is named - and so "
            + $"the dry run that shows it exits {HarnessExit.Refused} itself. Every "
            + "refusal of every row is named in one run. A --new or an --accept-lost naming a row the agent did not file is "
            + "refused, and so is an --accept-lost naming a cell that keeps its stored text, and a --new naming a row a registry "
            + "holds other than as declared; one naming a row an earlier fold of the agent applied is let stand, so the command "
            + "line of that fold runs again. An existing row is changed in the cells that differ, and one already as declared left "
            + "alone; a write that fails puts both registries back byte for byte. A row an earlier fold applied, declared as it was "
            + "then, is never applied again, so a change the registries took since stands. One declared anew over it is weighed "
            + "cell by cell - priority, status, trigger, closing, cross-refs - from what that fold recorded as declared, against "
            + "the registries as the write finds them: a cell only the agent changed is written; one only the registries changed "
            + "is kept, and said, and stays kept at each fold until the agent changes it; one both changed alike is left; and one "
            + "both changed otherwise is written over the registries' only where --accept-over <ID>:<cell> names it, the dry run "
            + "showing the three texts - what --accept-over consents to includes what the registries' text loses. The fold never "
            + "writes the agent's own row files: before it changes a kept cell, the agent writes the registries' text into it, or "
            + "its change is two changes of one cell. The verdict is one decision: where anchors.triggerCarriesVerdict holds, a "
            + "row's status and its trigger state it together, so where the registries changed one of the two and the agent the "
            + "other, and the row taken one from each would state two verdicts though the agent's states one - a row the main tree "
            + "reopened that the agent closes again with its trigger, its status file closed as it was, or a row the main tree "
            + "closed that the agent keeps open - both are weighed as both changed, each line of the dry run saying why, and "
            + "written, the agent's together, only where --accept-over <ID>:status and --accept-over <ID>:trigger name them; "
            + "until both are named nothing of the row is written, and the registries' verdict stands. A row the agent declares "
            + "with two verdicts itself is refused for them before any cell of it is weighed, whatever the row taken one from each "
            + "would state, no consent asked. Where the setting does not hold, the status alone "
            + "carries the verdict and is weighed as any cell is: one the agent's file holds as it declared it then is kept, "
            + "whatever its trigger says. An --accept-over naming a cell not both changed is refused, as is one naming "
            + "a row the agent did not file, and one naming a row declared as its earlier fold recorded it is let stand; a row an "
            + "earlier fold applied that the registries no longer hold is refused, to be set by hand.");
        builder.AppendLine();
        AppendWrapped(
            builder,
            "Deleting. delete-agent folds what is left and applies the rows, proves nothing is left to fold, copies the agent's "
            + $"Claude transcripts, found by session id under {ClaudeTranscripts.ConfigDirectoryVariable} or ~/.claude, into "
            + $"{OrchestratorLayout.LogsDirectoryName}/<agent>/ - one not found is said, and one found and not kept stops it before "
            + "anything is closed - and keeps what the evidence roots named before the fold and after it hold, each copy proved, in "
            + "a directory named for the run; a root that is or passes through a link, and a link under one, is neither kept nor "
            + "counted. It then records the agent closed, "
            + "deletes the evidence files still holding what was kept - one that cannot be deleted is named and left, and stops "
            + "the removal - and removes the worktree and its copies on hosts through delete-worktree - never forced, and its "
            + "evidence check kept, so a file written late stops it. It says "
            + "deleted only once the directory, git's record of it and every recorded copy are gone. --discard-uncommitted "
            + "abandons an agent: nothing folded and no rows applied, its evidence and transcripts still kept.");
        builder.AppendLine();
        AppendWrapped(
            builder,
            "A closed agent is never folded again. Run again, delete-agent compares its worktree with what closing it recorded, "
            + $"never with the main tree: a file changed or new since is work, left for you, and it exits {HarnessExit.Incomplete}; "
            + "a file gone since is the debris of a removal that stopped part way. A directory at its path with no .git of its "
            + "own is never forced: its evidence is kept again, and the delete-worktree --force that removes it is named for you; "
            + $"since every --apply of it ends so, its dry run exits {HarnessExit.Incomplete} too, naming the same command and "
            + "keeping nothing. A worktree made at its path since is not its to remove.");
        builder.AppendLine();
        AppendWrapped(
            builder,
            "Every command that writes the main tree or an agent's worktree holds it, as a sync holds a copy - fold-agent, "
            + "seed-agent, refresh-agent and delete-agent hold the agent's worktree as well as the main tree, and rebase-agent "
            + "holds its worktree - so no leg builds in either meanwhile, and once one has written, an interruption waits for it "
            + "to finish. A dry run of the four asks with the very hold its --apply takes, so a run holding what it needs is "
            + $"named and the dry run refused itself, exit {HarnessExit.Refused}, saying that nothing in the request needs "
            + "correcting; the refusal an --apply gives names the holder in the words of the dry run's 'held now:' line, where "
            + "its run's leg stands included. A holder is a fact about one moment, so 0 "
            + "from a dry run never promises the tree will still be free when the --apply is typed. A leg that works in a copy "
            + "of the tree holds only the copy ('help legs'): a fold and such a leg never "
            + "refuse each other, and a fold that lands while the run is still reading the tree ends its legs inputs-moved with "
            + "nothing run - seconds, at the run's start - while the same run started again reads the folded tree. "
            + "delete-agent alone names a copy a run holds, since it removes every copy with the worktree: a host's, a leg copy "
            + "kept in it, and a mutation worker, each in its own words. delete-agent refuses to run from "
            + "inside the worktree it removes, and so does its dry run, "
            + "and an agent made under a worktrees root the configuration no longer names is refused with nothing touched. "
            + "delete-orchestrator deletes the orchestrator once every agent of it with a worktree is deleted and no worktree is "
            + "left below its directory, nor any directory holding a .git of its own that git records no worktree at - until then "
            + $"it refuses ({HarnessExit.Refused}), naming each - and the evidence, transcripts, steps and sessions its agents "
            + "kept, whatever their kind or state, only with --delete-evidence; an agent with no worktree holds nothing but "
            + "records, and goes with it, deleted or not, and a run's codebase graph - made from the tree, and made again whenever "
            + "asked - goes with it and is never counted among what --delete-evidence guards. It "
            + "removes the record last, so one that stops part way finishes when run again.");

        return builder.ToString();
    }
}
