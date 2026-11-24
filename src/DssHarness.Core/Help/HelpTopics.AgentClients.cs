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
    /// The agent sections of config.json: the companies whose models the tool reaches, their keys in the git-ignored
    /// slot, the tiers a step runs on and the effort forms, the agents' policy and the claude-code client - said where a
    /// person reads them.
    /// </summary>
    private static string RenderAgentClients()
    {
        var builder = new StringBuilder();

        builder.AppendLine("Agent clients");
        builder.AppendLine();
        builder.AppendLine("The companies whose models this tool reaches are declared in config.json, under");
        builder.AppendLine("providers, and nothing of a model is in the tool's code: a model's entry says what it");
        builder.AppendLine("takes (its efforts), what it holds (its context and its output) and what it costs (its");
        builder.AppendLine("prices, with the date they were read); a model's code is listed once across every");
        builder.AppendLine("provider, so naming a model is enough - its code says its provider, its API's address,");
        builder.AppendLine("its keys' names and its client.");
        builder.AppendLine();
        AppendWrapped(
            builder,
            "A provider's keys are a list, apiKeyNames: the names of the variables that hold its keys in the git-ignored "
            + $"slot {HarnessLayout.WorkflowsDirectoryRelative}/.env/, at least one, in the order they are tried - a name and never a key, "
            + "and a second key of one company is a second name in the list. claude-code signs in by its entry's auth "
            + $"(agentClients.claude-code.auth, one of {string.Join(", ", Agents.AgentAuth.All)}): {Agents.AgentAuth.Login}, the machine's "
            + $"login, where nothing is said, reading no key at all; {Agents.AgentAuth.ApiKey}, its provider's keys in their order; "
            + $"{Agents.AgentAuth.OAuthToken} or {Agents.AgentAuth.AuthToken}, the token the slot holds under the entry's token "
            + $"({Agents.AgentAuth.TokenVariable(Agents.AgentAuth.OAuthToken)} or {Agents.AgentAuth.TokenVariable(Agents.AgentAuth.AuthToken)} "
            + "where it names none) - a name and never a token, appointed once beside every provider's keys. Its "
            + $"{Agents.AgentClientSetting.ConfigDirKey} and {Agents.AgentClientSetting.ExecutableKey} name the variables of the slot that hold a specific login's Claude Code directory and "
            + $"a program to start in place of npm's ({Orchestration.ClaudeTranscripts.ConfigDirectoryVariable} and {Agents.ClaudeCode.ClaudeCodeUpdateService.ExecutableVariable} where they name none), appointed once "
            + "the same way and never a key; a launch is to sign in from that directory where the slot holds it, else from the "
            + $"tool's own {Orchestration.ClaudeTranscripts.ConfigDirectoryVariable}, else from Claude Code's own. A launch is "
            + "given that credential and no other, whatever the tool's own environment holds; claude-api and glm sign each "
            + "request with their provider's keys. Choosing a key is the engine's, where it runs a run's steps: a step "
            + "starts on the first key of its model's provider the slot holds and no remembered limit covers, or - where "
            + "balanceKeyUsage is on (off unless turned on) - on the one that has spent least in the last seven days, "
            + "counted back from the moment it starts, keys that spent alike in the list's order, whatever tier, model, "
            + "step, run or side-task spent it; the key is kept for the step, its conversation staying with one account, "
            + "and a limit or a rejected key moves it to the next in the same order. What a key spent is what the engine "
            + $"counts for each request served with it, at the configuration's prices - or, for a Claude Code launch, what the launch reports - recorded by the key's name (never its value) in {HarnessLayout.OrchestratorsDirectoryName}/{DssHarness.Core.Agents.AgentKeySpend.FileName}, "
            + "one line for each key and model of each entry of a step's order that ran, so a run that moved keys while it "
            + "worked books what each key spent to that key, read for a key's week and its lines older than seven "
            + "days dropped when it is next written; what the same key spends elsewhere - another repository, another "
            + "machine, another tool - is not seen. Each request an API client sends, and each Claude Code launch, goes "
            + "through the gate of its provider and key (below), beside the orchestrator's maxReasoningAgents.");
        builder.AppendLine();
        builder.AppendLine("The slot itself is a directory of dotenv files, " + HarnessLayout.WorkflowsEnvSlotRelative + "/:");
        builder.AppendLine("every file in it is read and merged, a name declared in two of them is refused, a line whose");
        builder.AppendLine($"name is no variable's name ({Secrets.AgentKeyStore.VariableNameShape}) is refused by its line number alone, for it");
        builder.AppendLine("may be part of a key, and no key is ever read from the process environment - a CI machine");
        builder.AppendLine("writes the file. A sample for the providers init declares, none filled in:");
        builder.AppendLine();

        foreach (var line in SlotSample())
        {
            builder.AppendLine(line);
        }

        builder.AppendLine();
        builder.AppendLine($"'{ToolPackage.Command} list-agent-providers' says what your configuration appoints and");
        builder.AppendLine("whether the slot holds it - each key and token present or missing, each setting a client");
        builder.AppendLine("reads and each web-search source's key present or absent, for none is needed - never a");
        builder.AppendLine("value; 'list-agent-providers --template' prints the agent sections as init writes them");
        builder.AppendLine("into a new config, to paste into an existing one.");
        builder.AppendLine();
        builder.AppendLine("A provider's name and each tier's are held to the orchestration name rule - lower-case");
        builder.AppendLine("letters and digits, single hyphens - and a tier is one name in one case everywhere; a");
        builder.AppendLine("model's code, what is sent to the API, is one word, and its name is the one a person reads.");
        builder.AppendLine();
        builder.AppendLine("The tiers a step runs on are tierConfig's: each tier its models in the order they are");
        builder.AppendLine("tried, the first serving a step on the tier and the rest its fallback - a step's order as");
        builder.AppendLine("entries of a model and one of its provider's keys, each model's keys before the next");
        builder.AppendLine("model, and a model named outright, on the step or in its agent definition, running on that");
        builder.AppendLine("model with none after it ('help agent-definitions'). The engine's fallback walks that order:");
        builder.AppendLine("an entry that cannot be used on this machine is said and passed over when its turn comes");
        builder.AppendLine("and never refuses a run, a declined request, an account at its limit and a key the provider");
        builder.AppendLine("refused move the step to its next entry by the cause's rule - inside the one conversation");
        builder.AppendLine("where the provider and client are the same (the loop's own walk, below), a new conversation");
        builder.AppendLine("with the step's prompt everywhere else - a limit the run met is remembered until its end, an");
        builder.AppendLine("entry a move reaches that cannot be started is said and passed over as one the machine cannot");
        builder.AppendLine("use is, and where the slot holds none of the keys of the step's first model the step's agent");
        builder.AppendLine($"is never started and the step fails refused (exit {HarnessExit.Refused}); a later model's missing key is");
        builder.AppendLine("only said and passed over. A step's effort is its own - one");
        builder.AppendLine("level for every model of its tier, or a map giving each its own: all for the rest, a");
        builder.AppendLine("model's code for one - and where the step gives a model none, its agent definition's map");
        builder.AppendLine("or level is read the same way, and a model neither names runs at its own default. Each");
        builder.AppendLine("model's level is checked against what that model takes when the definition is checked,");
        builder.AppendLine("a level it does not take refused naming the level to give it instead; validate-workflow");
        builder.AppendLine("says, under the line that says a definition holds, every step's models and levels, each");
        builder.AppendLine("provider's prices for the models its steps may run on and the date they were read, and");
        builder.AppendLine("each key a step's model would read that the slot lacks, with the steps that would read");
        builder.AppendLine("it.");
        builder.AppendLine();
        AppendWrapped(
            builder,
            $"One tier is read by name rather than by a step: 'reporting', the tier {Orchestration.Flow.FlowService.AnalyzeReportCommand} "
            + "runs its analyst on ('help orchestrators'). init seeds no such tier - which model a paid analysis runs on is a "
            + "person's choice - so the command is refused (12) until tierConfig names one, saying which key to set.");
        builder.AppendLine();
        AppendWrapped(
            builder,
            "A step that goes on with earlier work is given the engine's record of it: the continuation record, text put "
            + "together from the step directories of the agent - what it is, the last iterations of the newest attempt word "
            + "for word from its stored stream, the newest first and every one of them, every run of the attempt numbered as "
            + "one, each message the engine sent the agent in its place, then the handoff, the last red build or test, the "
            + "steps so far, the newest first, with each one's notes and output whole, and every file changed - nothing in it "
            + "written or shortened by a model, and nothing of it cut. It is kept whole among the results of the conversation "
            + "it begins and laid in pages as every long answer is: the step's first message holds its first page, an index "
            + $"of the pages each part of it is on, and how {Agents.Runtime.AgentTools.ReadResult} with id "
            + $"'{Agents.Runtime.KeptResults.ContinuationId}' gives every page of it; what it reads is copied to the step's own "
            + $"{Orchestration.OrchestratorLayout.HandedDirectoryName}/{Agents.AgentContinuation.CopiesFolder}/, "
            + "which the tool alone writes and the agent reads, never where its commands write. The keys of "
            + "agentContext.continuation that once bounded it are retired and refused, naming what replaces them - and the "
            + "first user message holds the parts in their order: the run context, the plan updates, the continuation record "
            + "and the step's prompt as the task. The record is sent only where the conversation is a new one - every launch "
            + "on claude-code, and on the API clients a conversation that cannot be gone on with - for a handed conversation "
            + $"goes on with one more user message alone; the message as sent is kept in the step's {OrchestratorLayout.StepUserFileName}, masked.");
        builder.AppendLine();
        AppendWrapped(
            builder,
            "A step whose conversation has filled its model's context ends ContextExhausted, and where its limit's question "
            + "is answered compact the engine compacts the conversation - a side-task compacts its own by itself - "
            + "and no model does: a reduction worked out from the stored stream of the attempt, every run of it, the same "
            + "input always the same record - the run context and the task whole as they were first sent, the last "
            + $"{Agents.AgentCompactionBounds.LastIterations} iterations word for word, every earlier message of the agent its text whole "
            + "with its thinking left out, every earlier call one line (the tool, its input cut at callInputChars, how it "
            + "ended, the path where its input names one), every earlier result left out, and every message the engine sent "
            + "since the first whole. The reduced part is at most agentContext.compaction.reducedChars "
            + $"({Agents.AgentCompactionBounds.DefaultReducedChars}); a call's input is cut at callInputChars "
            + $"({Agents.AgentCompactionBounds.DefaultCallInputChars}); where the reduced part is more, the oldest messages of "
            + "the agent are given by reference instead, oldest first, until it fits - each whole in the stored stream, by the "
            + $"id {Agents.Runtime.AgentTools.ReadResult} gives it by - and the record says how many were; nothing of one is cut. "
            + "It begins a new conversation, on every client - the "
            + "record is the first message of one and the old is kept whole, never edited or sent again - said in the step's "
            + "stream as Compacted and counted in its result once, as the first launch given it begins the conversation - an "
            + "API client's first answer begun, a claude-code call run or turn ended, or the launch ending any way but "
            + "NotStarted - so a run never started (its client missing, its model unpriced, the client refusing it, a model the "
            + "plan does not serve) or held at the gate compacted nothing, and a retry or a move is "
            + "given the same record and counts no other; a side-task's compaction is said and counted the same way, its "
            + "record carried before the person's next message; an attempt compacted again is reduced whole again, its earlier "
            + "records never copied into the next, and a later launch's first message is read as its task alone, never the "
            + "record it carried; a claude-code child is started "
            + "with DISABLE_COMPACT=1, so it never compacts by itself and a full context ends the step ContextExhausted.");
        builder.AppendLine();
        AppendWrapped(
            builder,
            "The requests of one provider and one key go through one lane of the gate: at most "
            + $"agentClients.<client>.maxParallel of them under way at once ({string.Join(", ", Agents.ClientGateSettings.BuiltIn.Select(bound => $"{bound.Key} {bound.Value}"))} "
            + "where the entry says none; a client that signs in with a login is one lane by its client, and two keys of one "
            + "provider two lanes, keyed by a hash of the key). The API clients enter the lane for each request they send, "
            + $"its input counted as it enters (estimated, its bytes over {Codebase.EstimatedTokens.BytesPerToken}); claude-code, "
            + "whose requests the engine cannot see one by one, is held in the lane for each launch. Beside it the API "
            + "clients' optional buckets pace a lane's minute - requestsPerMinute, inputTokensPerMinute (a request more "
            + "than the bucket holds goes in alone once the bucket is empty) and outputTokensPerMinute (an answer's counted "
            + "once it is had), each waited out until the oldest of the minute is a minute old - and a 429's cool-down, set "
            + "by every rate limit and account limit a request or a launch meets from when the provider says it lifts, "
            + "holding what its failure covers: a model's rate limit holds that model's requests on the lane, an account's "
            + "limit every request of it, a later or longer one holding past a sooner one. The claude-api client's own web "
            + "search enters the lane too. A wait at the gate is said as it is waited, and the run's pause ends it; a "
            + "cool-down past the client's maxRetryWaitSeconds is waited by no client - an API client meets it as the limit "
            + "it is, which moves the step as a 429 naming that wait does, and a claude-code launch it holds is not begun, "
            + "the step moved to an entry the cool-down does not hold. Left out, the provider's answers pace the "
            + "client; the orchestrator's maxReasoningAgents bounds agents beside the gate, and both apply.");
        builder.AppendLine();
        builder.AppendLine("This build checks configurations and definitions against these sections and writes them with");
        builder.AppendLine($"init (and '{ToolPackage.Command} init' writes the prices it holds, as read on their date).");
        builder.AppendLine();
        builder.AppendLine("The Claude Code this tool uses is the one npm installs, and no other: npm's is the one");
        builder.AppendLine("that is installed, brought up to date and driven from a command line, and a native");
        builder.AppendLine("launcher, a Homebrew or WinGet package and any other claude on the PATH is never started,");
        builder.AppendLine("updated or fallen back to. The tool asks npm where its global packages are and starts the");
        builder.AppendLine("program the package names, by its full path and never a launcher; on Windows npm.cmd is a");
        builder.AppendLine("batch file, so the tool does what that file does and starts npm's own npm-cli.js with node.");
        builder.AppendLine("A run that needs a Claude model through the claude-code client needs npm, and no other run");
        builder.AppendLine("does: where npm has no Claude Code, or one older than the tool needs, a run that needs it");
        builder.AppendLine("will install or update it first, with");
        builder.AppendLine($"'npm install -g @anthropic-ai/claude-code@{Agents.ClaudeCode.ClaudeCodeUpdateService.Needed}' -");
        builder.AppendLine("never with sudo, never 'npm update -g', never past the version needed, never beside a");
        builder.AppendLine("session of that installation that is running (its program, node running its script, a");
        builder.AppendLine("claude node runs, or a claude whose program the machine will not say, such as another");
        builder.AppendLine("user's on Linux; a claude installed any other way whose program it names does not count,");
        builder.AppendLine("and a process table read by names alone refuses the change), and never a program the slot");
        builder.AppendLine($"names ({Agents.ClaudeCode.ClaudeCodeUpdateService.ExecutableVariable}, or the name agentClients.claude-code.{Agents.AgentClientSetting.ExecutableKey} gives: a");
        builder.AppendLine("person's own choice, started as named; a slot that cannot give it is refused, never passed");
        builder.AppendLine("over for npm's).");
        builder.AppendLine("After a change, whether it ran or failed, what npm holds since is said. A client's update");
        builder.AppendLine("key (agentClients.<client>.update, true where nothing is said) and DISABLE_UPDATES in the");
        builder.AppendLine("tool's own environment (a setting of the person's, never a key) turn installing and");
        builder.AppendLine("updating off, and a run that then needs one that is missing or too old is refused with the");
        builder.AppendLine("command to run by hand. A run readies Claude Code so before a reasoning step that reaches it");
        builder.AppendLine("starts its agent, and run-side-task before its chat. The run's advisor and its reporter only");
        builder.AppendLine("read, so they only find Claude Code and never install or update it: where it is missing or");
        builder.AppendLine("too old, no advice is obtained - the question says why - and no model words the reporter's");
        builder.AppendLine("answer, the status answering with the facts alone and ask-orchestrator refused.");
        builder.AppendLine();
        builder.AppendLine("What an agent may do is one policy, the same on every client: everything is allowed except");
        builder.AppendLine("the root actions, the places no agent reaches, and the restrictions - which only ever");
        builder.AppendLine("refuse, from every source at once, and never remove one another's. A restriction is one");
        builder.AppendLine("string, Run(git push *), Write(.github/**), Read(secrets/**) or Web(pastebin.com): Run a");
        builder.AppendLine("command prefix, its trailing * any arguments and the bare command too; Write and Read");
        builder.AppendLine("path patterns as gitignore reads them, matched against the path as it stands under each");
        builder.AppendLine("of the agent's roots; Web one domain, the domain and every name under it, or Web(*) the");
        builder.AppendLine("web whole. Declared under agentPolicy.restrictions in config.json, and a workflow's");
        builder.AppendLine("definitions and steps add their own to them, all applied together. agentPolicy.webDomains");
        builder.AppendLine("is the allow list beside them: left out, any address, the machine's own and its network's");
        builder.AppendLine("among them; given, only those domains and what is under them, for the pages an agent reads");
        builder.AppendLine("and the results a search gives - a search source's own requests are held to the Web(...)");
        builder.AppendLine("rules alone. An address whose slashes after its scheme readers read apart - any scheme but");
        builder.AppendLine("its two slashes and a host after them");
        builder.AppendLine($"(http:/\\host, gopher:/host, http:///host), or no slash after an {string.Join(", ", Agents.Policy.AgentPolicy.SlashedSchemes.SkipLast(1))} or");
        builder.AppendLine($"{Agents.Policy.AgentPolicy.SlashedSchemes[^1]} scheme (https:host) - or whose host is spelled beyond ASCII (its xn-- form is read");
        builder.AppendLine("as written), names no host this can decide, and is denied. A command that runs a");
        builder.AppendLine("program which fetches from the network - curl, wget, Invoke-WebRequest, Invoke-RestMethod,");
        builder.AppendLine("iwr, irm, http, https, gh api - is decided as a web request too, by each address it names,");
        builder.AppendLine("a proxy, a curl --connect-to's or --resolve's target and the proxy its environment names");
        builder.AppendLine("among them, and a step without the web is refused it; one that reads what it reaches from");
        builder.AppendLine("a file (curl --config, wget --input-file or --execute) is refused where the web is held to");
        builder.AppendLine("any rule, and what a script does inside stays unseen, as help says above. Refused always,");
        builder.AppendLine("not configurable: the root actions (sudo, su, doas, pkexec, run0, gsudo, sudoedit, runas,");
        builder.AppendLine("psexec, PowerShell's -Verb RunAs, wsl -u root, systemd-run, schtasks with /ru, wherever a");
        builder.AppendLine("command names one), writes outside an agent's writable roots, every read and write of a");
        builder.AppendLine("credential slot or a Claude Code directory of the run's, or of a place the user keeps");
        builder.AppendLine("credentials in (the isolation's list, below), and every search over one, any");
        builder.AppendLine("command naming one - by ~ or $HOME, as a redirect's file, an option's value after an =");
        builder.AppendLine("or a PowerShell parameter's :, a value glued to a short option (ssh -i./key), the file an");
        builder.AppendLine("@ or a < makes curl, httpie or gh read (-d @x, -F f=@x;type=y), a word of a script, a");
        builder.AppendLine("variable the command string sets read with each value it gives it (p=../..; cat $p/x), a");
        builder.AppendLine("pattern's segment that may match .. (.?, .[.], .*) read as .. too - and any word that");
        builder.AppendLine("names a directory holding one whole (.harness-config, .., ~, /, or a pattern that may");
        builder.AppendLine("match one) to a program that may read what it holds, for a program handed a directory may");
        builder.AppendLine("walk it: but the words of a program that only names, tests or lists the paths it is given (echo,");
        builder.AppendLine("printf, test, ls, du, df, stat, realpath, mkdir, which, a find that runs, deletes and");
        builder.AppendLine("writes nothing, and their kind), a word that moves a command (a cd's, git -C's, tar -C's,");
        builder.AppendLine("make -C's), which reads nothing where it goes, a word that names where a program writes,");
        builder.AppendLine("which the write rules decide, and the agent's working directory and those above it, which");
        builder.AppendLine("reach the slots only through it - every command over the tree names it (.) and every");
        builder.AppendLine("program given no path works over it. A recursive removal or a move of a directory that");
        builder.AppendLine("holds a tree's .git, the tool's state, a slot, a Claude Code directory or a place the user");
        builder.AppendLine("keeps credentials in (rm -rf .,");
        builder.AppendLine("Remove-Item -Recurse, mv, rsync --delete, git clean -dx), the working directory among");
        builder.AppendLine("them, is refused, and so is a find or fd walk that may remove or move one or what one");
        builder.AppendLine("holds - its -delete, a remover its -exec, -execdir or -ok runs, fd's -x and -X - read as");
        builder.AppendLine("the walk reaches it: its depth, prunes, excludes and name, path, regex, kind and");
        builder.AppendLine("emptiness filters read, what a guarded directory holds read as it stands, so");
        builder.AppendLine("find . -name '*.pyc' -delete runs. This tool's own verbs and run-side-task, run by any program the policy reads or");
        builder.AppendLine("from a response file, or by a verb the shell expands (run-side-$T, dssharness $V) - its");
        builder.AppendLine("verb the first word after its name, for it takes no option before one - are refused, and so is a");
        builder.AppendLine("command that sets or takes away DSSHARNESS_UNDER_AGENT, the mark of every program an agent");
        builder.AppendLine("starts - its assignment, empty or not, unset, env -u, export -n and declare and their");
        builder.AppendLine("kind, a name reference to it, cmd's set, csh's setenv, setx, the name read, mapfile,");
        builder.AppendLine("printf -v, getopts, wait -p, vared, a for or a select sets, one an arithmetic assigns");
        builder.AppendLine("(let, ((...))), a PowerShell item cmdlet's env: item, its Environment:: path among them,");
        builder.AppendLine("or a start with no environment (env -i, env -, exec -c) - and one that may: any of those");
        builder.AppendLine("places given a name the shell expands (unset ${v}AGENT, env -u \"$v\", unset");
        builder.AppendLine("$(env | grep ...), zsh's unset -m pattern), a PowerShell item cmdlet given a path it");
        builder.AppendLine("computes, a wildcard that may match it (Env:DSSHARNESS_*, a relative one where the string");
        builder.AppendLine("names env:) or none where the string names env:; and any other word that names it, but a");
        builder.AppendLine("word of data - a program that runs none of its words (grep -rn DSSHARNESS_UNDER_AGENT");
        builder.AppendLine("src, echo \"$DSSHARNESS_UNDER_AGENT\"), a git command that reads only what git tracks");
        builder.AppendLine("(git log -S), a word holding a blank - which takes nothing away -");
        builder.AppendLine("whatever any switch says: an agent does not move its own run, nor leave its step's limits -");
        builder.AppendLine("nor do the gh commands that hand its work to another agent or install what the next launch");
        builder.AppendLine($"loads ({string.Join(", ", Agents.Policy.AgentPolicy.GhOutOfStepCommands)}), by gh's own aliases too -");
        builder.AppendLine("nor does any command print a credential, whatever the worktree rules say");
        builder.AppendLine($"({string.Join(", ", Agents.Policy.AgentPolicy.CredentialPrintingCommands)}; a * any helper's name, and");
        builder.AppendLine("the words after a command met anywhere after it, options before or between them, and a word");
        builder.AppendLine("the shell expands among them, wherever the expansion stands in it, held as one that may be them):");
        builder.AppendLine("a credential leaves its slot only by the key store that holds it, never through an");
        builder.AppendLine("agent's words. Nor is git or gh called by a name none of its own commands is - an alias,");
        builder.AppendLine("an extension - on any ground, as the reading of a command below says.");
        builder.AppendLine("No agent starts while this tool itself runs elevated: its step is recorded never started,");
        builder.AppendLine("saying so, before any client is taken. A path is decided as asked and as resolved, and a");
        builder.AppendLine("refusal's places compare in any case on every system. On Windows each segment of a path is");
        builder.AppendLine("read as the name Windows reaches by it before any refusal compares it - in a command's words,");
        builder.AppendLine("its moves, its writes and its removals and in a file tool's path alike: its trailing dots and");
        builder.AppendLine("spaces dropped (.git., k. .), an NTFS stream's or attribute's suffix dropped");
        builder.AppendLine("(.git::$INDEX_ALLOCATION, k::$DATA), and a short name (GIT~1, harnes~1, in any case) read as");
        builder.AppendLine("its long name where the path is there - the system's own answer, whatever short names the");
        builder.AppendLine("volume gives - and, where it is not, as each guarded name it could shorten as well, for a");
        builder.AppendLine("command may make the place before the word is read (git init sub and then sub/GIT~1/hooks);");
        builder.AppendLine("HEAD~1 and their kind, which shorten no guarded name, are the words they are.");
        builder.AppendLine("On Windows a device prefix (\\\\?\\, \\\\.\\, \\??\\) is dropped from a path and from what");
        builder.AppendLine("the runtime makes of it - one naming no drive and no share, a volume by its id or a pipe, is");
        builder.AppendLine("a place it cannot tell, and denied - and a shell's spelling of a drive (/c/x, /cygdrive/c/x,");
        builder.AppendLine("/mnt/c/x) is read both as the drive (C:\\x) and as the directory it names on the current");
        builder.AppendLine("drive, an action allowed only where both are; a cd is followed the same way - pushd,");
        builder.AppendLine("PowerShell's Set-Location and Push-Location, the directory env -C or pwsh");
        builder.AppendLine("-WorkingDirectory starts its command in, and the one git -C, tar -C or --directory, make -C,");
        builder.AppendLine("ninja -C and patch -d work in, in every spelling, glued or apart, each word it may go to; a");
        builder.AppendLine("cd to a name under a CDPATH - the one the agent's commands start with or the one the");
        builder.AppendLine("string sets - read in each of its directories too, a cd given no directory going to the");
        builder.AppendLine("HOME the command or the string sets, cd - back to the OLDPWD the string sets or a place a");
        builder.AppendLine("move of the string's left, and one whose place this does not know - cd - with neither, a");
        builder.AppendLine("CDPATH, OLDPWD or HOME the string computes - denied - from every place the command");
        builder.AppendLine("reached before it, a command after && standing only where the one before it held and one");
        builder.AppendLine("after || only where it failed, to every place it reaches, however many, none denied for their number.");
        AppendWrapped(builder, ShellReadingProse());
        builder.AppendLine("The protected paths (agentPolicy.protectedPaths, true where nothing is said) are the");
        builder.AppendLine("writes Claude Code guards, refused on every client so a role is not stopped on one where");
        builder.AppendLine("it passes on another; the list is Claude Code's own for the oldest version this tool");
        builder.AppendLine("takes, written out once as rules, each said whole: Write(.git/**), Write(.config/git/**),");
        builder.AppendLine("Write(.claude/**), Write(.vscode/**), Write(.idea/**), Write(.husky/**), Write(.cargo/**),");
        builder.AppendLine("Write(.devcontainer/**), Write(.yarn/**), Write(.mvn/**), Write(.ssh/**),");
        builder.AppendLine("Write(.mcp.json), Write(.claude.json), the shell files Write(.bashrc),");
        builder.AppendLine("Write(.bash_profile), Write(.profile), Write(.zshrc), Write(.zprofile), Write(.zshenv),");
        builder.AppendLine("Write(.config/fish/config.fish), and the package managers' Write(package.json),");
        builder.AppendLine("Write(package-lock.json), Write(yarn.lock), Write(pnpm-lock.yaml), Write(bun.lock),");
        builder.AppendLine("Write(bun.lockb), Write(Cargo.toml), Write(Cargo.lock), Write(go.mod), Write(go.sum),");
        builder.AppendLine("Write(pom.xml), Write(requirements.txt), Write(pyproject.toml), Write(Gemfile),");
        builder.AppendLine("Write(Gemfile.lock), Write(composer.json), Write(composer.lock). Turning them off");
        builder.AppendLine("takes this tool's refusal away; Claude Code's own guard stays Claude Code's. A step's");
        builder.AppendLine("writes is the one source that narrows: where a step has them, a write under its");
        builder.AppendLine("worktree that matches none of their patterns is denied, and the reporter's reads are");
        builder.AppendLine("narrowed to the run's directory alone, with no command, no write and no web at all. For");
        builder.AppendLine("Claude Code the same policy is rendered, never re-decided, into the launch's settings: an");
        builder.AppendLine("ask rule for every tool the launch has, and the restriction rules as deny rules, absolute");
        builder.AppendLine("once for each root in the POSIX form Claude Code matches (//c/x for C:\\x), a Write rule as");
        builder.AppendLine("the Edit rule Claude Code holds its file tools' writes to, a read of each credential slot");
        builder.AppendLine("and Claude Code directory beside them -");
        builder.AppendLine("a second layer, for the engine is asked about every other call");
        builder.AppendLine("and answers from this policy in its own process, and a call those rules refuse first is");
        builder.AppendLine("counted and recorded as Claude Code's own denial. This is policy, not a sandbox: it");
        builder.AppendLine("decides what a call names, never what a program reads by itself - a program an agent");
        builder.AppendLine("runs opens files as the user it runs as, and one that walks its working directory unnamed");
        builder.AppendLine("(an archive of ., a search given no path) reaches a credential slot's files where that");
        builder.AppendLine("directory holds one, as a side-task's main tree does; what it is never given is a key in");
        builder.AppendLine("its environment, and what no file tool and no command's word the policy reads names is the");
        builder.AppendLine("slot itself, in any root, or a directory that holds one to a program that may read it,");
        builder.AppendLine("but that working directory and those above it. The words it does not read - a path a");
        builder.AppendLine("command computes, the code an interpreter is given, a script file's inside, what a process");
        builder.AppendLine("apart from the agent's tree does - are where a policy of words ends.");
        builder.AppendLine("Where this tool cannot decide a");
        builder.AppendLine("call it denies it, never allows: nothing runs while the host that would answer is");
        builder.AppendLine("silent or gone. The policy is built and holds every action behind it; the claude-code");
        builder.AppendLine("client below asks it of every call, the tool runtime below decides by it each call it");
        builder.AppendLine("runs - every call of the claude-code client's tools, served through the engine's tool server, and");
        builder.AppendLine("every call of the API clients, the claude-api and glm clients below - for every agent a");
        builder.AppendLine("command of this tool runs: a run's reasoning steps, its advisor and its reporter, and a");
        builder.AppendLine("side-task.");
        builder.AppendLine();
        AppendWrapped(builder, IsolationProse());
        builder.AppendLine();
        AppendWrapped(builder, ExecutionIsolationProse());
        builder.AppendLine();
        AppendWrapped(builder, ClaudeCodeClientProse());
        builder.AppendLine();
        AppendWrapped(builder, ToolRuntimeProse());
        builder.AppendLine();
        AppendWrapped(builder, ToolLoopProse());
        builder.AppendLine();
        AppendWrapped(builder, ClaudeApiClientProse());
        builder.AppendLine();
        AppendWrapped(builder, ZaiApiClientProse());
        builder.AppendLine();
        AppendWrapped(builder, StructuredOutputProse());

        return builder.ToString();
    }

    /// <summary>
    /// The sample file for the slot, from the names the providers the configuration init writes appoint: one comment
    /// naming the file, then each appointed name with nothing after its <c>=</c> but a comment saying what its value
    /// is - the shape a person copies, holding no value.
    /// </summary>
    private static IReadOnlyList<string> SlotSample()
    {
        return
        [
            "  # " + HarnessLayout.WorkflowsEnvSlotRelative + "/keys.env",
            .. Agents.AgentProviderTemplates.Read().Providers.SelectMany(provider =>
                provider.ApiKeyNames.Select(name => $"  {name}= # {DssHarness.Core.Secrets.AgentKeyStore.WhatHolds(provider)}")),
        ];
    }
}
