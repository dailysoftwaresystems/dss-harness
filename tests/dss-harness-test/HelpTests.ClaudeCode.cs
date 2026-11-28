using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using DssHarness.Core.Anchors;
using DssHarness.Core.Ci;
using DssHarness.Core.Agents.Policy;
using DssHarness.Core.Configuration;
using DssHarness.Core.Execution;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Git;
using DssHarness.Core.Help;
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
using DssHarness.Core.Sync;

namespace DssHarness.Tests;

public sealed partial class HelpTests
{
    /// <summary>
    /// The agent-clients topic says what the claude-code client does with a launch - a new session each time, streamed
    /// both ways, its turns the model's answers, every tool the engine's tool server's and none of Claude Code's own, the page an answer of it holds, every
    /// call asked of the engine, the repair, the message at work, the handoff, the model held, the environment, a key passed
    /// over - its tools and names read from the types that hold them.
    /// </summary>
    [Fact]
    public async Task AgentClientsTopic_SaysWhatTheClaudeCodeClientDoesWithALaunch()
    {
        var prose = Prose.Words(HelpText.Of("agent-clients").StandardOutput);

        Assert.Contains(
            "The claude-code client starts Claude Code once for each launch, a new session every time: never resumed and never continued, "
                + "keeping no session file (one a launch leaves all the same is removed once it has ended, however it ended, and said), "
                + "streamed both ways - the step's prompt is "
                + "the first message on its input, never an argument -",
            prose,
            StringComparison.Ordinal);
        Assert.Contains(
            "the tools of the agent's capabilities and no other, each served by the engine's tool server - read: mcp__dssharness__view_file, "
                + "mcp__dssharness__find_files, mcp__dssharness__search_files, mcp__dssharness__read_result; write: mcp__dssharness__create_file, "
                + "mcp__dssharness__replace_in_file, mcp__dssharness__insert_in_file, mcp__dssharness__create_directory, mcp__dssharness__delete_file, "
                + "mcp__dssharness__delete_directory, mcp__dssharness__move_path; run: mcp__dssharness__run_command; web: mcp__dssharness__web_fetch, "
                + "mcp__dssharness__web_search; and a step that has a codebase graph (graph: tools) takes mcp__dssharness__graph, only where its "
                + "step is given one, and a step whose agent runs commands, of a repository that declares legs, takes mcp__dssharness__measure, which measures one cell of the universe "
                + "a class of the item names on one of the run's legs "
                + "- and never Agent, Task, Skill, AskUserQuestion, EnterPlanMode, ExitPlanMode.",
            prose,
            StringComparison.Ordinal);
        Assert.Contains(
            "- and never Agent, Task, Skill, AskUserQuestion, EnterPlanMode, ExitPlanMode. Its turns are the model's answers, as on the API clients and as "
                + "Claude Code counts the turns its --max-turns holds the step's maxTurns to: each response a launch writes - the lines of one response "
                + "sharing its message id, a line that names none an answer of its own, a message Claude Code writes itself none - counted as it "
                + "arrives, over every launch of the run, so a launch cut before its turn's result still counts what it answered; the run's result, "
                + "a side-task's record and a step's measure say them, never the engine's messages to the launch. None of Claude Code's own tools",
            prose,
            StringComparison.Ordinal);
        Assert.Contains(
            "None of Claude Code's own tools is given: --tools names none (--tools \"\"), so its shell and its file tools (Read, Grep, Glob, "
                + "Write, Edit, NotebookEdit, Bash) reach no launch, and a side-task alone keeps Claude Code's own WebFetch and WebSearch beside the "
                + "engine's; each call is asked of the engine and run by the tool runtime below as on the API clients - a command by run_command, "
                + "under the isolation of the agent's commands, its output kept whole and paged - and every launch is given "
                + "MAX_MCP_OUTPUT_TOKENS=98304, three times the 32768 characters of a page of a tool's answer: a page reaches Claude Code as JSON, "
                + "escaped, inside its answer's envelope, and a token is never less than one character, so Claude Code never cuts an answer the "
                + "engine paged.",
            prose,
            StringComparison.Ordinal);
        Assert.Contains(
            "A step with the web takes the harness's own web_fetch - and web_search, where it has a source to search - from the engine's tool "
                + "server, and a step that has a codebase graph (graph: tools) takes its one tool, mcp__dssharness__graph, from the same: the one MCP server, of type sdk, named dssharness, that the launch's --mcp-config names beside --strict-mcp-config (the "
                + "step's claude-mcp.json), served in the engine's own process over the launch's control channel - its initialize, its tool list "
                + "and its calls, our own code with no MCP library; each call of it is asked of the engine first, by its ask rule, and run by the "
                + "tool runtime below as on the API clients, each address decided and recorded, the graph's calls answered by the same owner that "
                + "answers them on the API clients - its edit operation among them - and each recorded as a line of the step's graph - a step whose "
                + "agent runs commands, of a repository that "
                + "declares legs, takes the measure tool from the same server, by the one rule the tool runtime offers it by, its calls answered and "
                + "recorded as every tool call is. A step's first user message holds what of the graph it is pushed - the run's "
                + "repository map first, then its context pack, a step at graph: map both and one with the tool neither, unless its graphPromptTokens pushes its pack - "
                + "then its task - every launch a conversation of its own, it is given them as every "
                + "launch is, and the order puts them at the head of the message so every turn after the first reads them from Claude Code's own "
                + "cache; the engine sets no mark of its own. A launch whose init does not show that server "
                + "connected with its tools taken, or that connects another, never started;",
            prose,
            StringComparison.Ordinal);
        Assert.Contains(
            "A step whose reads are narrowed (graphReads: narrow, 'help codebase-graph') is answered a whole view_file - one with no start_line "
                + "and no end_line - of a file its graph holds parsed over the narrowing bound with the file's outline in its place, by the tool "
                + "runtime as on the API clients and with the same words, a range passing and the same two escapes counted and recorded as the "
                + "graph's lines; its search_files hits and its ranges answer from the graph as they do there.",
            prose,
            StringComparison.Ordinal);
        Assert.DoesNotContain("its edit operation is not given on this client", prose, StringComparison.Ordinal);

        Assert.Contains("a call of it the engine was not asked about and did not allow runs nothing and fails the step.", prose, StringComparison.Ordinal);
        Assert.Contains(
            "The run's reporter is given mcp__dssharness__view_file, mcp__dssharness__find_files, mcp__dssharness__search_files, mcp__dssharness__read_result alone.",
            prose,
            StringComparison.Ordinal);
        Assert.Contains("Every call its own deny rules do not refuse first is asked of the engine before it runs and answered from the policy above", prose, StringComparison.Ordinal);
        Assert.Contains("An answer sent back (below) and a message the engine gives an agent at work are written to the same launch, and a result written before the launch took it is not the answer;", prose, StringComparison.Ordinal);
        Assert.DoesNotContain("each problem by its path, up to", prose, StringComparison.Ordinal);
        Assert.Contains("a handoff asked interrupts the turn and asks the agent for its handoff in the same launch", prose, StringComparison.Ordinal);
        Assert.Contains("Every message is held to the model asked for, a date after its id the same model", prose, StringComparison.Ordinal);
        Assert.Contains("(CLAUDECODE, CLAUDE_PID and CLAUDE_EFFORT, and every name beginning CLAUDE_CODE_, CLAUDE_AGENT_SDK_, CLAUDE_PREVIEW_ or MCP_ but CLAUDE_CODE_GIT_BASH_PATH, which says only where a program is, and a name agentPolicy.keepEnvironment gives back), none of what every child of an agent loses (the tool runtime below)", prose, StringComparison.Ordinal);
        Assert.Contains(
            "and the names the engine sets: DISABLE_COMPACT=1, DISABLE_AUTOUPDATER=1, CLAUDE_CODE_SUBPROCESS_ENV_SCRUB=1, CLAUDE_CODE_RETRY_WATCHDOG=0, "
                + "MAX_MCP_OUTPUT_TOKENS=98304, FORCE_PROMPT_CACHING_5M=0, DISABLE_PROMPT_CACHING=0, DISABLE_PROMPT_CACHING_HAIKU=0, "
                + "DISABLE_PROMPT_CACHING_SONNET=0, DISABLE_PROMPT_CACHING_OPUS=0, DISABLE_PROMPT_CACHING_FABLE=0, DISABLE_PROMPT_CACHING_MYTHOS=0, "
                + "and CLAUDE_CODE_PROMPT_CACHE_TTL and CLAUDE_CODE_SUBAGENT_PROMPT_CACHE_TTL at agentClients.claude-code.cacheTtl "
                + "(5m where nothing is said), so cacheTtl alone says how a launch caches whatever the person's environment or settings hold. "
                + "A key Claude Code reports invalid is passed over",
            prose,
            StringComparison.Ordinal);
        Assert.DoesNotContain("BASH_DEFAULT_TIMEOUT_MS", prose, StringComparison.Ordinal);
        Assert.DoesNotContain("CLAUDE_BASH_MAINTAIN_PROJECT_WORKING_DIR", prose, StringComparison.Ordinal);
        Assert.Contains("A key Claude Code reports invalid is passed over, and while the step has done no work the next key is given a new launch", prose, StringComparison.Ordinal);
        Assert.Contains(
            "the claude-code client below asks it of every call, the tool runtime below decides by it each call it runs - every call of the claude-code client's tools, "
                + "served through the engine's tool server, and every call of the API clients, the claude-api and glm clients below - for every agent a command of this tool runs: a run's reasoning steps, its advisor and its reporter, and a side-task.",
            prose,
            StringComparison.Ordinal);
        Assert.Contains("a second layer, for the engine is asked about every other call and answers from this policy in its own process, and a call those rules refuse first is counted and recorded as Claude Code's own denial.", prose, StringComparison.Ordinal);
        Assert.Contains(
            "a call it refuses by itself that the engine refuses too - by its own deny rules, told by its permission_denied line or by its result alone - is a denial of Claude Code's, counted and recorded once;",
            prose,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The agent-clients topic says what the claude-code client holds a launch to: the user's settings and CLAUDE.md read
    /// and their hooks off, a credential those settings would give refused by the file and the directory key, a launch
    /// waited on and told, never timed, the checks of the protocol that name a version where it does not hold, the stall,
    /// a decline and an account at its limit - its names and its bound read from the types that hold them.
    /// </summary>
    [Fact]
    public async Task AgentClientsTopic_SaysWhatTheClaudeCodeClientHoldsALaunchTo()
    {
        var prose = Prose.Words(HelpText.Of("agent-clients").StandardOutput);

        Assert.Contains(
            "the user's settings.json in its Claude Code directory and the user's CLAUDE.md reach every agent on this client, but no hook of theirs runs - every launch's settings say disableAllHooks -",
            prose,
            StringComparison.Ordinal);
        Assert.Contains("each launch's start names that settings file and its SHA-256 and says the hooks are off", prose, StringComparison.Ordinal);
        Assert.Contains(
            "(ANTHROPIC_API_KEY, ANTHROPIC_AUTH_TOKEN, CLAUDE_CODE_OAUTH_TOKEN, ANTHROPIC_BASE_URL in its env, in any case, or apiKeyHelper) refuses the run before any launch",
            prose,
            StringComparison.Ordinal);
        Assert.Contains("give the client a directory of its own in the slot: CLAUDE_CONFIG_DIR, or the name agentClients.claude-code.configDir gives.", prose, StringComparison.Ordinal);
        Assert.Contains(
            "A launch is waited on, never timed by the client: its answer to the host's initialize request, however long it takes, and its own "
                + "end once its input was closed, each told every 5 minutes while it lasts, as every wait of the harness is, and ended only by the "
                + "step's own time, its stop, or an ending that ends the launch at once.",
            prose,
            StringComparison.Ordinal);
        Assert.Contains("The client holds every launch to the protocol itself, so a version on which it does not hold names itself: a launch that works before its init says what it runs as,", prose, StringComparison.Ordinal);
        Assert.DoesNotContain("within 30 s", prose, StringComparison.Ordinal);
        Assert.Contains("a call it ran that the engine was never asked about, or refused, fails the step by the tool's name", prose, StringComparison.Ordinal);
        Assert.Contains("within the step's stall bound - counted from when the launch answered its initialize request, and from the turn's end for one written while a turn ran - ends it Stalled", prose, StringComparison.Ordinal);
        Assert.Contains("A request the model declines and an account at its limit - a rate limit event", prose, StringComparison.Ordinal);
        Assert.Contains("that says rejected, or a turn Claude Code ends on rate_limit or billing_error after its own tries - each move", prose, StringComparison.Ordinal);
        Assert.Contains("the step to its next entry, a new launch - an account at its limit to the same model's next key first, a declined request past the model's other keys to the next model - ending it Refused and", prose, StringComparison.Ordinal);
        Assert.Contains("RateLimited where none is left, with what the declined request was of where the", prose, StringComparison.Ordinal);
        Assert.Contains("CLAUDE_EXECUTABLE, or the name agentClients.claude-code.executable gives", prose, StringComparison.Ordinal);
    }

    /// <summary>
    /// The README and the architecture say the claude-code client - what its turns are among it - and how it signs in as
    /// the help does, and the architecture names its owners by the types that are them.
    /// </summary>
    [Fact]
    public async Task TheDocumentsSayTheClaudeCodeClient()
    {
        var readme = Prose.Document("README.md");

        Assert.Contains("signs in by the client's `auth` (`agentClients.claude-code.auth`): `login`, the machine's login,", readme, StringComparison.Ordinal);
        Assert.Contains("The `claude-code` client starts Claude Code once for each launch, a new session every time", readme, StringComparison.Ordinal);
        Assert.Contains("Every call its own deny rules do not refuse first is asked of the engine before it runs and answered from the policy", readme, StringComparison.Ordinal);
        Assert.Contains("but no hook of theirs runs: every launch's settings say `disableAllHooks`", readme, StringComparison.Ordinal);
        Assert.Contains("The client holds every launch to the protocol itself", readme, StringComparison.Ordinal);
        Assert.Contains("Its `configDir` and `executable` name the variables of the slot that hold a specific login's Claude Code directory", readme, StringComparison.Ordinal);
        Assert.Contains("A program the slot names (`CLAUDE_EXECUTABLE`, or the name the client's `executable` gives)", readme, StringComparison.Ordinal);
        Assert.Contains("and for each setting a client reads from the slot and each web-search source's key `present` or `absent` (none is needed)", readme, StringComparison.Ordinal);
        Assert.Contains(
            "A step with the web takes the harness's own `web_fetch` - and `web_search`, where it has a source to search - from the engine's tool server: "
                + "the one MCP server, of type `sdk`, named `dssharness`, that the launch's `--mcp-config` names beside `--strict-mcp-config` (the step's `claude-mcp.json`)",
            readme,
            StringComparison.Ordinal);
        Assert.Contains("a call of it the engine did not allow runs nothing and fails the step.", readme, StringComparison.Ordinal);
        Assert.Contains("a call those rules refuse before the engine is asked is counted and recorded as Claude Code's own denial.", readme, StringComparison.Ordinal);
        Assert.Contains(
            "the tool runtime below decides by it each call it runs - every call of the `claude-code` client's tools, served through the engine's tool server, "
                + "and every call of the API clients, the `claude-api` and `glm` clients below - for every agent a command of this tool runs: a run's reasoning steps, its advisor and its reporter, and a side-task.",
            readme,
            StringComparison.Ordinal);
        Assert.Contains(
            "the tools of the agent's capabilities and no other, each served by the engine's tool server (`read`: `mcp__dssharness__view_file`, "
                + "`mcp__dssharness__find_files`, `mcp__dssharness__search_files`, `mcp__dssharness__read_result`; `write`: `mcp__dssharness__create_file`, "
                + "`mcp__dssharness__replace_in_file`, `mcp__dssharness__insert_in_file`, `mcp__dssharness__create_directory`, `mcp__dssharness__delete_file`, "
                + "`mcp__dssharness__delete_directory`, `mcp__dssharness__move_path`; `run`: `mcp__dssharness__run_command`; `web`: `mcp__dssharness__web_fetch`, "
                + "`mcp__dssharness__web_search`), never one that starts agents, loads skills, asks a person or plans.",
            readme,
            StringComparison.Ordinal);
        Assert.Contains(
            "Its turns are the model's answers, as on the API clients and as Claude Code counts the turns its `--max-turns` holds the step's "
                + "`maxTurns` to: each response a launch writes - the lines of one response sharing its message id, a line that names none an answer of "
                + "its own, a message Claude Code writes itself none - counted as it arrives, over every launch of the run, so a launch cut before its "
                + "turn's result still counts what it answered; the run's result, a side-task's record and a step's measure say them, never the "
                + "engine's messages to the launch.",
            readme,
            StringComparison.Ordinal);
        Assert.Contains(
            "None of Claude Code's own tools is given: `--tools` names none (`--tools \"\"`), so its shell and its file tools reach no launch, and a "
                + "side-task alone keeps Claude Code's own `WebFetch` and `WebSearch` beside the engine's; each call is asked of the engine and run by the tool "
                + "runtime below as on the API clients - a command by `run_command`, under the isolation of the agent's commands, its output kept whole and "
                + "paged - and every launch is given `MAX_MCP_OUTPUT_TOKENS=98304`, three times the 32768 characters of a page of a tool's answer, so Claude "
                + "Code never cuts an answer the engine paged.",
            readme,
            StringComparison.Ordinal);
        Assert.Contains(
            "On Linux and macOS a `claude-code` launch's own process runs apart from its agent's commands, reading its login and the key it is handed "
                + "there, and runs none of them: it is given no shell and no file tool of its own - `--tools` names none (`--tools \"\"`), a side-task's own "
                + "`WebFetch` and `WebSearch` alone - and the engine's tool server serves the tool runtime's in their place, each command started by "
                + "`run_command` inside a layer of its own, as on the API clients, so no command reaches Claude Code's directories, its login or its environment.",
            readme,
            StringComparison.Ordinal);
        Assert.Contains(
            "On Linux Claude Code's own process runs inside an outer layer of bubblewrap, which holds its process alone: the machine "
                + "read-only, every credential slot hidden, git's code-running files and the tool's state sealed as for its commands, and written "
                + "but its writable roots, its own temporary directory beside its commands' - in the run's temp where the launch "
                + "belongs to a run, in the launch's own records for a side-task's - named to it by `CLAUDE_CODE_TMPDIR`, so its "
                + "temp is put away with the run and shared with no command's layer - the declared caches, Claude Code's own directories and `~/.claude.json`.",
            readme,
            StringComparison.Ordinal);
        Assert.Contains(
            "On macOS, whose sandbox starts none inside another, Claude Code's own process runs outside every layer, and runs nothing of the "
                + "agent's there. In or out of a layer, Claude Code opens no file of the agent's in its own process:",
            readme,
            StringComparison.Ordinal);
        Assert.Contains(
            "On Windows the launch runs whole inside MXC's container with its directories read alone: a login whose token has run out is not renewed "
                + "inside until a `claude` run outside renews it - written, they would let a command plant what Claude Code runs outside later. Where there "
                + "is no isolation at all - `preferred`, on a machine with none - the runtime's tools run outside, the words policy alone between them.",
            readme,
            StringComparison.Ordinal);

        foreach (var gone in new[] { "claude-receipts", "dssharness-receipt", "CLAUDE_CODE_SHELL_PREFIX", "`--isolated-shell`", "its file tools are given" })
        {
            Assert.DoesNotContain(gone, readme, StringComparison.Ordinal);
        }
        Assert.Contains(
            "served in the engine's own process over the launch's control channel; each call of it is asked of the engine first, by its ask "
                + "rule, and run by the tool runtime below as on the API clients. A launch whose init does not show that server connected with its "
                + "tools taken, or that connects another, never started,",
            readme,
            StringComparison.Ordinal);
        Assert.Contains("which the same query again is answered from; written by every client whose step searches", readme, StringComparison.Ordinal);
        Assert.Contains(
            "A launch is waited on, never timed by the client: its answer to the host's `initialize` request and its own end once its input was closed, "
                + "each told every 5 minutes while it lasts and ended only by the step's own time, its stop, or an ending that ends the launch at once. "
                + "The client holds every launch to the protocol itself: one that works before its init says what it runs as,",
            readme,
            StringComparison.Ordinal);
        Assert.Contains(
            "a call it refuses by itself that the engine refuses too - by its own deny rules, told by its `permission_denied` line or by its result alone - is a denial of Claude Code's, counted and recorded once;",
            readme,
            StringComparison.Ordinal);

        var architecture = Prose.Document("docs", "architecture.md");

        Assert.Contains($"The first client that asks it is `Agents\\ClaudeCode\\{nameof(DssHarness.Core.Agents.ClaudeCode.ClaudeCodeClient)}`", architecture, StringComparison.Ordinal);

        foreach (var owner in new[]
        {
            nameof(DssHarness.Core.Agents.ClaudeCode.ClaudeLaunch),
            nameof(DssHarness.Core.Agents.ClaudeCode.ClaudeLine),
            nameof(DssHarness.Core.Agents.ClaudeCode.ClaudeToolDecider),
            nameof(DssHarness.Core.Agents.ClaudeCode.ClaudeCodeTools),
            nameof(DssHarness.Core.Agents.StructuredOutputGate),
            nameof(DssHarness.Core.Agents.AgentHandoffSignal),
            nameof(DssHarness.Core.Agents.AgentCredential),
            nameof(DssHarness.Core.Agents.AgentAuth),
            nameof(DssHarness.Core.Agents.ClaudeCode.ClaudeUserSettings),
            nameof(DssHarness.Core.Agents.AgentAccountLimit),
            $"{nameof(DssHarness.Core.Agents.ClaudeCode.ClaudeLaunch)}.{nameof(DssHarness.Core.Agents.ClaudeCode.ClaudeLaunch.McpConfiguration)}",
            $"{nameof(DssHarness.Core.Agents.ClaudeCode.ClaudeCodeTools)}.{nameof(DssHarness.Core.Agents.ClaudeCode.ClaudeCodeTools.ServerTools)}",
            $"{nameof(DssHarness.Core.Agents.ClaudeCode.ClaudeCodeTools)}.{nameof(DssHarness.Core.Agents.ClaudeCode.ClaudeCodeTools.ServedName)}",
            $"{nameof(DssHarness.Core.Agents.AgentToolRunner)}.{nameof(DssHarness.Core.Agents.AgentToolRunner.Harness)}",
            $"{nameof(DssHarness.Core.Agents.ClaudeCode.ClaudeLine)}.{nameof(DssHarness.Core.Agents.ClaudeCode.ClaudeLine.CallResult)}.{nameof(DssHarness.Core.Agents.ClaudeCode.ClaudeLine.CallResult.RefusedByRule)}",

            // What the run's turns are on this client, and who reads them: each response counted by its id, the exchange's
            // end and the run's end saying them, the measure reading them.
            $"{nameof(DssHarness.Core.Agents.ClaudeCode.ClaudeLine)}.{nameof(DssHarness.Core.Agents.ClaudeCode.ClaudeLine.Assistant)}.{nameof(DssHarness.Core.Agents.ClaudeCode.ClaudeLine.Assistant.Id)}",
            $"{nameof(DssHarness.Core.Agents.AgentRunResult)}.{nameof(DssHarness.Core.Agents.AgentRunResult.Turns)}",
            $"{nameof(DssHarness.Core.Agents.TurnFinished)}.{nameof(DssHarness.Core.Agents.TurnFinished.Answers)}",
            $"{nameof(DssHarness.Core.Agents.RunFinished)}.{nameof(DssHarness.Core.Agents.RunFinished.Turns)}",
            $"{nameof(AgentRunSpend)}.{nameof(AgentRunSpend.TurnsTaken)}",
        })
        {
            Assert.Contains($"`{owner}`", architecture, StringComparison.Ordinal);
        }
    }
}
