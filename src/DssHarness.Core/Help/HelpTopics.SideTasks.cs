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
using DssHarness.Core.Worktrees;

namespace DssHarness.Core.Help;

public static partial class HelpTopics
{
    /// <summary>
    /// What a side-task's agent is held off while a run works over the tree, said from the lists the policy holds it by
    /// (<see cref="Agents.Policy.AgentPolicy.LiveRunHeld"/>, <see cref="Agents.Policy.AgentPolicy.GitStateForms"/>,
    /// <see cref="Agents.Policy.AgentPolicy.LiveRunOpen"/>) and the modes git reads as only reading
    /// (<see cref="Agents.Policy.CommandAnalysis.GitReadsOnly"/>), so the help names every command the policy does.
    /// </summary>
    internal static string SideTaskLiveRunHold
        => "While a run is working over the tree, the commands that write the git state it works over - its HEAD, index, "
            + "refs, stash, configuration or worktrees - are refused with the run's name: git's state-writing verbs ("
            + string.Join(", ", Agents.Policy.AgentPolicy.GitStateVerbs) + "), the verbs that write its index ("
            + string.Join(", ", Agents.Policy.AgentPolicy.GitIndexVerbs) + " - apply whole, for its --index and --cached "
            + "may stand anywhere among its words), gh's that write git's state ("
            + string.Join(", ", Agents.Policy.AgentPolicy.GhStateCommands) + ", each whole) and the forms of git's other "
            + "commands that write the same (" + string.Join(", ", Agents.Policy.AgentPolicy.GitStateForms) + ", "
            + GitStateFormsMet + "), each met as the worktree rules meet a "
            + "command - git's options before its verb seen through, git-commit read as git commit, a configuration set "
            + "where it runs (git -c alias.x=commit x) read as the git config it is, and a verb the shell expands held as "
            + "one that may be any - checked at each call, so a run that starts while the chat is open is held to from its "
            + "first step and one that ends releases it. Their forms that only read stay open: "
            + string.Join(", ", Agents.Policy.AgentPolicy.LiveRunOpen) + " (a trailing * any words after the form); and, "
            + "read as git reads their options wherever they stand among the words - an option's value never taken for an "
            + "option - git branch in a listing mode (" + string.Join(", ", Agents.Policy.CommandAnalysis.GitBranchListing)
            + ") or naming no branch, with no mode that writes; git tag in a listing mode ("
            + string.Join(", ", Agents.Policy.CommandAnalysis.GitTagListing) + ") or naming no tag, neither deleting nor "
            + "verifying; git config getting or listing (" + string.Join(", ", Agents.Policy.CommandAnalysis.GitConfigGets)
            + ", its verbs " + string.Join(" and ", Agents.Policy.CommandAnalysis.GitConfigReadVerbs) + ") or naming one key "
            + "alone; git symbolic-ref naming one ref, deleting none; git reflog alone, with an option first or by a verb that "
            + "reads (" + string.Join(", ", Agents.Policy.CommandAnalysis.GitReflogReads) + "); and git notes, after its "
            + "--ref, alone or by a verb that reads (" + string.Join(", ", Agents.Policy.CommandAnalysis.GitNotesReads)
            + "). An option of theirs this does not know holds the command - --no-list undoing a --list before it among "
            + "them - as does a word the shell expands among their words. The git that writes none of it stays open, and "
            + "the person may do any of it by hand.";

    /// <summary>
    /// The side-tasks topic: what a side-task is - an agent outside every workflow, a conversation between a person and
    /// one agent over the repository's main tree - the two commands, what the side-task's agent may do and may not, its
    /// model and effort, and where its records live.
    /// </summary>
    private static string RenderSideTasks()
    {
        var builder = new StringBuilder();
        var command = ToolPackage.Command;

        builder.AppendLine("SIDE-TASKS");
        builder.AppendLine();
        AppendWrapped(
            builder,
            "A side-task is an agent outside every workflow: a conversation between a person and one agent over the "
            + "repository's main tree, asked directly, with no step, no run and no limit of a run's. Every action the agent "
            + "asks for is still decided by the engine before it runs and recorded, as a step's is, and the user's hooks "
            + "stay off in its launches. No host serves either verb: a chat is the person's, on the machine the person "
            + "typed on.");
        builder.AppendLine();
        builder.AppendLine("Commands");
        builder.AppendLine($"  {command} {Agents.SideTasks.SideTaskService.RunCommand} [--prompt <text> | --prompt-file <path>] [--resume <task>] [--name <task>]");
        builder.AppendLine($"             [--model <code> | --tier <tier>] [--client <client>] [--effort <level> | --effort <code>=<level>...] [--json]");
        builder.AppendLine($"  {command} {Agents.SideTasks.SideTaskService.ListCommand} [--json]");
        builder.AppendLine();
        AppendWrapped(
            builder,
            $"In the terminal, by default, {Agents.SideTasks.SideTaskService.RunCommand} holds a chat: the person types a message "
            + "(a line ending in \\ goes on to the next); the agent's text streams as it comes and each action it takes is one "
            + "line as it is decided, a denial with its rule. Ctrl+C interrupts the agent's turn where it stands and leaves "
            + "the chat open, what was said kept - on a client that stores its conversations up to its last whole turn, what the "
            + "cut request used kept as the turn's; a second, /exit or the end of input ends it: a turn under way is cut and its result "
            + $"read - within {Agents.ClaudeCode.ClaudeCodeClient.InterruptBound.TotalSeconds:0} s - and the lines the agent had not taken are "
            + "dropped, so what the turn used, cost and spent is kept; a launch still waiting its turn at the gate - a limit "
            + "not lifted, its wait said in the chat - is never begun. Each interrupt says whether a turn of the agent's was "
            + "under way to cut, or none was and nothing is cut - the first while a launch waits at the gate among them, the "
            + "launch begun once the wait passes. /model <model> and /effort <level> - or "
            + "<code>=<level> for each model, or default for the model's own - change what the next message is sent with, "
            + "each checked as the command line's are, a new model keeping only the levels that name it and the one for "
            + "the rest; a launch at work gives way as the person leaving does, and the lines it never took go on to the "
            + "new one; /records prints where this side-task's records are; /help lists "
            + "these. With --prompt or --prompt-file it answers one message once, prints the agent's final text and exits; "
            + "Ctrl+C there cuts its turn - a Claude Code launch's at that turn's result, an API client's request left where it "
            + "stands, what the provider had said it used counted and a cost it had said nothing of unknown - what it used "
            + "kept, and it ends "
            + $"interrupted ({HarnessExit.Cancelled}), a launch still waiting its turn at the gate or to be tried again never "
            + "begun; "
            + "--json answers with one document instead - and --json without a prompt is a usage error "
            + $"({HarnessExit.UsageError}), for a chat is no document. Where no turn runs - the side-task being set up, its "
            + "Claude Code made ready, another model made ready by /model - an interrupt stops what is under way: in one "
            + "shot the first, in a chat the second, and SIGTERM is one like any other. A side-task the person does not "
            + "name is named by the date and time it began - with the first words of its message in one shot; a chat is "
            + "named before its first message is typed, by the date and time alone. --name gives it one, refused "
            + $"({HarnessExit.Refused}) where another side-task holds that name, whose records are named as that one's. --resume opens a side-task again, going on "
            + "with its stored conversation - or, where the model it now runs on is reached by a client that is handed none, "
            + "from the conversation so far - on what the person last set it to run on: its tier with the tier's later "
            + "models, or the model named outright, unless --model or --tier names another; an --effort or a --client given "
            + $"stands over what it last ran with. --resume is refused ({HarnessExit.Refused}) where the name names no "
            + "side-task and where another process holds it open, and --name beside it is a usage error, for a side-task "
            + "opened again keeps its name. list-side-tasks lists them, newest first: each by its name, when it began and "
            + "last spoke, its model, its turns and cost, whether it is open now. The records' directory is named on every "
            + "exit, in text and in --json - or said to be none where the command stopped before any were written. Exit "
            + "codes: 0 ended as asked, and a chat 0 however the person leaves it once it has begun; "
            + $"{HarnessExit.Cancelled} where interrupts stop it before it began - while it is set up or opened again; "
            + $"{HarnessExit.UsageError} usage, among them an effort a model does not take; {HarnessExit.ConfigInvalid} "
            + "the configuration - a model no provider serves, no model to run on, a web search that does not resolve, a "
            + "program the slot names that does not read, and in one shot an agent the configuration keeps from starting; "
            + $"{HarnessExit.Refused} refused - a name taken or no "
            + "name a directory takes, a --resume naming no side-task or one open in another process, a .side-tasks "
            + "git does not ignore, a side-task started under an agent or whose starting processes could not be read, an "
            + "update of Claude Code refused - a session of "
            + "it running, or the machine's processes unread - and in one shot a key missing or the tool running "
            + $"elevated; {HarnessExit.ToolMissing} a client that cannot start where the entry that would serve first is on it - no claude, no npm - or, under "
            + $"agentPolicy.isolation \"{Agents.Isolation.AgentIsolationMode.Required}\", a machine that cannot isolate the agent's commands; "
            + $"{HarnessExit.Incomplete} the install of Claude Code timed out or could not reach the registry; and in one "
            + $"shot besides, {HarnessExit.CommandFailed} the model failed past its retries and fallback, "
            + $"{HarnessExit.Incomplete} cut short by the account's limit, a stall or the clock, {HarnessExit.Cancelled} "
            + $"interrupted. {StoppedDocument}");
        builder.AppendLine();
        AppendWrapped(
            builder,
            "Its model is resolved as a step's: --model names one outright by its code, --tier a tier of tierConfig, "
            + "neither the configuration's defaults.agentTier; the provider, the keys and the client follow from the model, "
            + "and --client picks among the clients that reach it. On a tier the later models of its order are followed, and "
            + "said in the chat: a turn moves on at its first failure, a decline, a limit, a refused key or a model that "
            + "cannot be started. A one shot's turn, and each message of a chat whose client stores its conversations, whose "
            + "agent failed on every model it moved through is tried again - after "
            + $"{Agents.SideTasks.SideTaskService.RetryFirstWait.TotalSeconds:0} s, the wait doubled each time up to {Waiting.Waits.SaidEvery.TotalMinutes:0} "
            + "minutes, each wait said - for as long as it fails in a new way, and ends, said, once a try fails as an "
            + "earlier one did on the same model: a side-task has no limit of a run's, so no count of tries ends it, and an "
            + "interrupt ends a wait as it cuts a turn; a failure of the provider's service past its client's own tries, and "
            + "a refused key, are never tried again. A chat's launch held open is never tried again: it goes on from the "
            + "conversation so far, never from its first message, with the messages no turn answered sent on there. --effort is a step's effort on the "
            + "command line: one level for every "
            + "model, or code=level given again for each model that is to have its own, with all=<level> for the rest - "
            + "each checked against what the model takes. Its key is chosen as a step's is - the order of the list, or "
            + "least spent where balanceKeyUsage is on - and what it spends is counted against that key.");
        builder.AppendLine();
        AppendWrapped(
            builder,
            "What the side-task's agent may do is the person's own session's, over the main tree: every limit of a run's is "
            + "dropped - no restriction list (Web(...), Run(...), Write(...), Read(...)), no allow list of pages, the web "
            + "search over every configured source turned on with no maxPerStep, no cap on money, turns or time, all four "
            + "capabilities, the main tree its writable root, the worktree rules and the protected paths off - so it can "
            + "edit .harness-config/config.json to add a search source, commit, branch, and run this tool's own verbs that "
            + "read no credential slot and write none of its state as the person could. One write at a time: every write "
            + "tool and every command of the side-task's agent - the graph tool's edit among them, never its reads - holds "
            + "the main tree's lock for its duration, as the engine's folds and commits do - each side waits the other out, "
            + "a long command holding the folds back for as long as it runs - and the chat says when its command waited; a "
            + "shell command Claude Code is asked to run in the background is refused, saying to run it in the foreground, "
            + "for its end is never seen, and on every client so is a command that leaves a process running after it ends - "
            + "one the shell runs in the background (a lone &, never cmd's, which runs the next command in turn; a coproc; "
            + "a process substitution's body, <(...) or >(...); PowerShell's job &, or a command PowerShell reads given "
            + "-AsJob), or one run by "
            + string.Join(", ", Agents.Policy.CommandAnalysis.Detaching)
            + " (wherever a program of the command runs; a program by any path to it, the shells' and PowerShell's own "
            + "commands by the bare word alone - where PowerShell reads the command, its cmdlets by a module-qualified name "
            + "too, Microsoft.PowerShell.Management\\Start-Process - so ./bin/start, scripts\\start or start.cmd is the "
            + "repository's own script), in every "
            + "script the command runs, one it wrote and then runs among them; what a program detaches "
            + "by itself - a dev server's own daemon mode, an interpreter's code - is not read, for the refusal is of what the "
            + "command names - and a lock that cannot be taken refuses the write or command it was taken for, saying why, the chat "
            + "going on. " + SideTaskLiveRunHold + " An edit of "
            + "config.json - a search source added, a provider changed - reaches the run's steps that start after it and "
            + "never one under way, for the agents' configuration is read when each step starts, as its prompts are; the "
            + "chat says so where a run is live and a file tool - the runtime's or Claude Code's own - wrote it. Its web_search asks one more source beside the configured ones: native, the provider's "
            + "own search - on the Claude API a request of the harness's own whose only tool is the provider's search, asked "
            + "with the model and key serving the side-task when the agent asks and through that key's gate as every request "
            + "is, what it costs counted then to that model and key, a failure of it - a gate held past the longest wait "
            + "among them - one failed part of the search saying why; on Claude "
            + "Code its own WebSearch kept in the side-task's tools; on Z.AI none - and web_fetch takes any address. What "
            + "stays, and why: the built-in refusals - no root action, no agent while the tool runs elevated, no file "
            + "tool's read or write of a credential slot, a Claude Code directory or a place the user keeps credentials in, no command whose words name one or a "
            + $"directory that holds one to a program that may read it, other than its working directory and those above it ('{ToolPackage.Command} help agent-clients' says how a word is read), no "
            + "command that prints a credential, no git or gh command called by a name none of their own commands is, no "
            + "verb of this tool's that reads a credential slot or writes its state ('help agent-clients' names them) - "
            + "each refused to the agent and said in the chat for the person to do by hand; git's own files "
            + "(Write(.git/**)), written by git's verbs and this tool's, never by an agent's file tool or a command's "
            + "write; the tool's own state, written only by its verbs; each search source's own pace and rate limits; and "
            + "run-side-task itself, refused to every agent where a word of a command or of a script the policy reads "
            + "names it or a verb the shell expands may, and refusing itself where it is started under one, whose mark "
            + "(DSSHARNESS_UNDER_AGENT) a command that sets it, takes it away, may take it away or names it but as data is refused, or where "
            + "a process of this tool's is among the ones that started it - the process tree, walked parent by parent, a "
            + "process that took a freed id never read as a parent, the tool's own runtime told by the file it runs where the "
            + "table gives one (the installed tool, a build's program, the runtime running the tool's assembly) and by its "
            + "name only where it gives none, so a launcher of the tool's name - a script, another program's shim - is "
            + "none; one detached from the agent's tree (at, a tmux server, "
            + "a double fork) is told by the mark alone, and a tree that could not be read is refused saying why; each "
            + "refusal says which made the command an agent's (the mark, the process of this tool's by its name and id, so a "
            + "side-task any process of this tool's starts is refused, or why the tree could not be read) and that a person "
            + $"starts one from {Agents.AgentAncestryAnswer.APersonsShell} - a "
            + "side-task is a person's own conversation, and no agent starts another. The main tree is the side-task's "
            + "working directory and holds the credential slots: a command over it whole - named . or given no path, an "
            + "archive of it or a search - is not told from one over its sources, and reaches their files; and code an "
            + "interpreter runs (python -c, perl -e, awk's system) or a process apart from the agent's tree does what no "
            + "word the policy reads says. This is a policy of what a command names, not a sandbox; beneath it, where this "
            + "machine has one, the operating system's isolation ('help agent-clients') hides every credential slot, and "
            + "every place the user keeps credentials in, from "
            + "each command - one over the main tree whole finds each slot empty, or is refused it - and keeps it from git's code-running "
            + "files and the tool's state.");
        builder.AppendLine();
        AppendWrapped(
            builder,
            "It may use the codebase graph, unlimited: a graph of its own under its records (graph/), seeded from the "
            + "newest the repository holds - a run's or another side-task's - when it opens, then incremental, synced after "
            + "each of its agent's writes - by the paths a file tool wrote, the runtime's or Claude Code's own - and whole "
            + "after each of its commands, the graph tool answering from what each sync leaves. The graph tool is offered "
            + "among its agent's own with every operation its client serves - edit among them on the API clients, where "
            + "Claude Code edits with its own Edit - what it reads and what an edit writes asked of the side-task's own "
            + "policy, and no narrowing of its reads; the repository map is given with its first message, made once from the base as "
            + "it stands when it opens, and a context pack is made from each message the person types. --no-graph turns all "
            + "of it off for one side-task; a graph that cannot be made takes nothing away from the conversation, and the "
            + "side-task says once that it runs without its graph, and why - as the chat's first line, or beside a one "
            + "shot's records - its graph.jsonl naming why no pack is given.");
        builder.AppendLine();
        AppendWrapped(
            builder,
            "Its records live in .side-tasks/<task>/ at the repository's root, written by the tool alone and kept until a "
            + "person removes the directory: task.json (its turns, each written as it ends, with its client, provider, model "
            + "and effort, what it used and cost, and what the agent ran under), the stored stream (stream.jsonl) and the transcript as a "
            + "person reads it (transcript.md), each action decided (tools.json holds the offered table), each denial "
            + "(denials.jsonl) and each search and fetch (web.jsonl), the stored conversation under sessions/ where "
            + $"its client stores one, where it has its graph, graph/, and {OrchestratorLayout.StepPagesDirectoryName}/ - the whole result of each ask of more than one page its graph tool answered, and where each page of a call's whole result read_result reads begins, which the chat's later pages read. Beside the directory, <task>.owner.json names the process that holds the side-task "
            + "open, claimed before anything is written and task.json written inside the claim, so two commands never "
            + "take one name; a dead one is reclaimed and said. A side-task whose turn outgrew the model's context - a "
            + "chat's, or a one shot's - keeps no stored conversation to go on with, for handed again it would meet the "
            + "same wall: its next message, in the chat or in a --resume, begins a new conversation from the engine's "
            + "compaction of it, made as a step's conversation is - the whole conversation since the last compaction, the "
            + "person's messages as they were said - and said once, as the turn given it begins. The directory is on the "
            + "tree's floor: sync never carries it to a host's copy "
            + "and no agent's worktree is seeded with it.");

        return builder.ToString();
    }
}
