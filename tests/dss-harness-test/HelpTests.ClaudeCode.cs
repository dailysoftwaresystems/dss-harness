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
    /// both ways, the tools of the one table and the ones never given, every call asked of the engine, the repair, the
    /// message at work, the handoff, the model held, the environment, a key passed over - its tools and names read from
    /// the types that hold them.
    /// </summary>
    [Fact]
    public async Task AgentClientsTopic_SaysWhatTheClaudeCodeClientDoesWithALaunch()
    {
        var prose = Prose.Words(HelpText.Of("agent-clients").StandardOutput);

        Assert.Contains(
            "where its own process runs apart from the isolation it hands its commands to (above), the engine's tool server serves read's "
                + "mcp__dssharness__view_file, mcp__dssharness__find_files, mcp__dssharness__search_files, mcp__dssharness__read_result and write's mcp__dssharness__create_file, "
                + "mcp__dssharness__replace_in_file, mcp__dssharness__insert_in_file, mcp__dssharness__create_directory, mcp__dssharness__delete_file, "
                + "mcp__dssharness__delete_directory, mcp__dssharness__move_path in place of Claude Code's own, each call asked of the engine and run by the "
                + "tool runtime below as on the API clients.",
            prose,
            StringComparison.Ordinal);

        Assert.Contains(
            "The claude-code client starts Claude Code once for each launch, a new session every time: never resumed and never continued, "
                + "keeping no session file (one a launch leaves all the same is removed once it has ended, however it ended, and said), "
                + "streamed both ways - the step's prompt is "
                + "the first message on its input, never an argument -",
            prose,
            StringComparison.Ordinal);
        Assert.Contains(
            "the tools of the agent's capabilities and no other - read: Read, Grep, Glob; write: Write, Edit, NotebookEdit; run: Bash; web: "
                + "none of Claude Code's, but mcp__dssharness__web_fetch and mcp__dssharness__web_search of the engine's tool server beside them; and "
                + "a step that has a codebase graph (graph: tools) takes mcp__dssharness__graph of the engine's tool server beside them, only where its "
                + "step is given one, and a step whose agent runs commands, of a repository that declares legs, takes mcp__dssharness__measure, which measures one cell of the universe "
                + "a class of the item names on one of the run's legs "
                + "- and never Agent, Task, Skill, AskUserQuestion, EnterPlanMode, ExitPlanMode;",
            prose,
            StringComparison.Ordinal);
        Assert.Contains(
            "A step with the web takes the harness's own web_fetch - and web_search, where it has a source to search - from the engine's tool "
                + "server, and a step that has a codebase graph (graph: tools) takes its one tool, mcp__dssharness__graph, from the same: the one MCP server, of type sdk, named dssharness, that the launch's --mcp-config names beside --strict-mcp-config (the "
                + "step's claude-mcp.json), served in the engine's own process over the launch's control channel - its initialize, its tool list "
                + "and its calls, our own code with no MCP library; each call of it is asked of the engine first, by its ask rule, and run by the "
                + "tool runtime below as on the API clients, each address decided and recorded, the graph's calls answered by the same owner that "
                + "answers them on the API clients and each recorded as a line of the step's graph - a step whose agent runs commands, of a repository that "
                + "declares legs, takes the measure tool from the same server, by the one rule the tool runtime offers it by, its calls answered and "
                + "recorded as every tool call is - but its edit operation is not given on this "
                + "client: the probe measured an Edit taken after a ranged Read, inside the range and outside it, on every version taken, so an "
                + "agent that means to change a body reads its lines with Read and Edits them. A step's first user message holds the run's "
                + "repository map first, then its context pack, then its task - every launch a conversation of its own, it is given them as every "
                + "launch is, and the order puts them at the head of the message so every turn after the first reads them from Claude Code's own "
                + "cache; the engine sets no mark of its own. A launch whose init does not show that server "
                + "connected with its tools taken, or that connects another, never started;",
            prose,
            StringComparison.Ordinal);
        Assert.Contains(
            "A step whose reads are narrowed (graphReads: narrow, 'help codebase-graph') is denied a whole Read - one with no offset and no "
                + "limit - of a file its graph holds parsed over the narrowing bound, by the same owner that narrows view_file on the API clients "
                + "and with the same words, a range passing and the same two escapes counted and recorded as the graph's lines.",
            prose,
            StringComparison.Ordinal);

        Assert.Contains("a call of it the engine was not asked about and did not allow runs nothing and fails the step.", prose, StringComparison.Ordinal);
        Assert.Contains("The run's reporter is given Read, Grep, Glob alone.", prose, StringComparison.Ordinal);
        Assert.Contains("Every call its own deny rules do not refuse first is asked of the engine before it runs and answered from the policy above", prose, StringComparison.Ordinal);
        Assert.Contains("An answer sent back (below) and a message the engine gives an agent at work are written to the same launch, and a result written before the launch took it is not the answer;", prose, StringComparison.Ordinal);
        Assert.DoesNotContain("each problem by its path, up to", prose, StringComparison.Ordinal);
        Assert.Contains("a handoff asked interrupts the turn and asks the agent for its handoff in the same launch", prose, StringComparison.Ordinal);
        Assert.Contains("Every message is held to the model asked for, a date after its id the same model", prose, StringComparison.Ordinal);
        Assert.Contains("(CLAUDECODE, CLAUDE_PID and CLAUDE_EFFORT, and every name beginning CLAUDE_CODE_, CLAUDE_AGENT_SDK_, CLAUDE_PREVIEW_ or MCP_ but CLAUDE_CODE_GIT_BASH_PATH, which says only where a program is, and a name agentPolicy.keepEnvironment gives back), none of what every child of an agent loses (the tool runtime below)", prose, StringComparison.Ordinal);
        Assert.Contains(
            "the names the engine sets: DISABLE_COMPACT=1, DISABLE_AUTOUPDATER=1, CLAUDE_CODE_SUBPROCESS_ENV_SCRUB=1, CLAUDE_CODE_RETRY_WATCHDOG=0, "
                + "CLAUDE_BASH_MAINTAIN_PROJECT_WORKING_DIR=1, and BASH_DEFAULT_TIMEOUT_MS and BASH_MAX_TIMEOUT_MS as agentPolicy.commandTimeoutSeconds "
                + "and maxCommandTimeoutSeconds say, in milliseconds, so a shell command it runs is held to the times of every command an agent runs.",
            prose,
            StringComparison.Ordinal);
        Assert.Contains("A key Claude Code reports invalid is passed over, and while the step has done no work the next key is given a new launch", prose, StringComparison.Ordinal);
        Assert.Contains(
            "the claude-code client below asks it of every call, the tool runtime below decides by it each call it runs - the web's of the claude-code client, and its file tools' "
                + "where Claude Code's own process runs apart from the isolation, served through the engine's tool server, and every call of the API clients, the claude-api and glm clients below - for every agent a command of this tool runs: a run's reasoning steps, its advisor and its reporter, and a side-task.",
            prose,
            StringComparison.Ordinal);
        Assert.Contains("a second layer, for the engine is asked about every other call and answers from this policy in its own process, and a call those rules refuse first is counted and recorded as Claude Code's own denial.", prose, StringComparison.Ordinal);
        Assert.Contains(
            "a call it refuses by itself that the engine refuses too - by its own deny rules, a file tool's told by its result alone - is a denial of Claude Code's, counted and recorded once;",
            prose,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The agent-clients topic says what the claude-code client holds a launch to: the user's settings and CLAUDE.md read
    /// and their hooks off, a credential those settings would give refused by the file and the directory key, the checks
    /// of the protocol that name a version where it does not hold, the stall, a decline and an account at its limit - its
    /// names and its bound read from the types that hold them.
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
        Assert.Contains("a launch that does not answer the host's initialize request within 30 s, that works before its init says what it runs as,", prose, StringComparison.Ordinal);
        Assert.Contains("a call it ran that the engine was never asked about, or refused, fails the step by the tool's name", prose, StringComparison.Ordinal);
        Assert.Contains("within the step's stall bound - counted from when the launch answered its initialize request, and from the turn's end for one written while a turn ran - ends it Stalled", prose, StringComparison.Ordinal);
        Assert.Contains("A request the model declines and an account at its limit - a rate limit event", prose, StringComparison.Ordinal);
        Assert.Contains("that says rejected, or a turn Claude Code ends on rate_limit or billing_error after its own tries - each move", prose, StringComparison.Ordinal);
        Assert.Contains("the step to its next entry, a new launch - an account at its limit to the same model's next key first, a declined request past the model's other keys to the next model - ending it Refused and", prose, StringComparison.Ordinal);
        Assert.Contains("RateLimited where none is left, with what the declined request was of where the", prose, StringComparison.Ordinal);
        Assert.Contains("CLAUDE_EXECUTABLE, or the name agentClients.claude-code.executable gives", prose, StringComparison.Ordinal);
    }

    /// <summary>
    /// The README and the architecture say the claude-code client and how it signs in as the help does, and the
    /// architecture names its owners by the types that are them.
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
            "the tool runtime below decides by it each call it runs - the web's of the `claude-code` client, and its file tools' where Claude Code's own process runs "
                + "apart from the isolation, served through the engine's tool server, and every call of the API clients, the `claude-api` and `glm` clients below - for every agent a command of this tool runs: a run's reasoning steps, its advisor and its reporter, and a side-task.",
            readme,
            StringComparison.Ordinal);
        Assert.Contains(
            "`web`: none of Claude Code's, but `mcp__dssharness__web_fetch` and `mcp__dssharness__web_search` of the engine's tool server beside them)",
            readme,
            StringComparison.Ordinal);
        Assert.Contains(
            "where its own process runs apart from the isolation it hands its commands to (above), the engine's tool server serves the tool runtime's file tools in place "
                + "of Claude Code's own - `read`: `mcp__dssharness__view_file`, `mcp__dssharness__find_files`, `mcp__dssharness__search_files`, `mcp__dssharness__read_result`; `write`: "
                + "`mcp__dssharness__create_file`, `mcp__dssharness__replace_in_file`, `mcp__dssharness__insert_in_file`, `mcp__dssharness__create_directory`, "
                + "`mcp__dssharness__delete_file`, `mcp__dssharness__delete_directory`, `mcp__dssharness__move_path` - each call asked of the engine and run by the "
                + "tool runtime below as on the API clients.",
            readme,
            StringComparison.Ordinal);
        Assert.Contains(
            "On Linux Claude Code's own process runs inside an outer layer of bubblewrap: the machine read-only, every credential slot hidden, git's "
                + "code-running files and the tool's state sealed as for its commands, and written but its writable roots, its temporary directory, the declared "
                + "caches, Claude Code's own directories and `~/.claude.json` - without which it fails every shell command - and the step's `claude-receipts` and "
                + "`claude-tmp`; the outer layer alone is left a user namespace to make each command's layer in, and the mounts it laid are locked into every "
                + "namespace made inside it, so none is undone there. A command Claude Code ran without its prefix therefore still runs inside the outer layer, "
                + "never with the user's rights",
            readme,
            StringComparison.Ordinal);
        Assert.Contains(
            "and Claude Code's own process runs outside every layer, as on macOS, whose sandbox starts none inside another; there a command Claude Code ran "
                + "without its prefix runs outside with the user's rights, and each launch says so as it starts. In or out of a layer, Claude Code opens no file "
                + "of the agent's in its own process: its file tools are not given, `--tools` names its shell alone,",
            readme,
            StringComparison.Ordinal);
        Assert.Contains(
            "Each shell command the engine allows is given back with a receipt at its head (`: dssharness-receipt <32 hexadecimal digits>;`, the shell's no-op), "
                + "which `--isolated-shell` writes in the step's `claude-receipts` directory, where no command handed over writes, before it runs the command - the "
                + "command itself run without that mark, and without the variables that hand it over, so nothing it is started with names its receipt or where "
                + "receipts are written. A call that comes back with none - a Claude Code that skipped its prefix - ends the launch failed at once, and one run in "
                + "the background is waited for within 20 s; however a launch ends - at a turn's result, Claude Code gone or its output cut - each allowed call "
                + "whose receipt is not there yet is waited for within 20 s before anything of it is ended, one with none by then ending it failed, and where the "
                + "launch was stopped or ended at once, each is said. A receipt proves only that Claude Code handed the command over, and only once it has run: it "
                + "stops nothing, and a command run without the prefix runs where Claude Code's own process runs, where it could write a receipt of its own - so "
                + "what holds such a command is the outer layer on Linux, and nothing on macOS or where bubblewrap starts none inside itself. Claude Code's own "
                + "temporary directory is the step's `claude-tmp`,",
            readme,
            StringComparison.Ordinal);
        Assert.Contains(
            "There its own file tools are given, for they open each file inside the container, which holds every place they reach as it holds the commands, "
                + "and its temporary directory is MXC's own inside it; nothing is handed over, so no receipt is asked.",
            readme,
            StringComparison.Ordinal);
        Assert.Contains(
            "served in the engine's own process over the launch's control channel; each call of it is asked of the engine first, by its ask "
                + "rule, and run by the tool runtime below as on the API clients. A launch whose init does not show that server connected with its "
                + "tools taken, or that connects another, never started,",
            readme,
            StringComparison.Ordinal);
        Assert.Contains("which the same query again is answered from; written by every client whose step searches", readme, StringComparison.Ordinal);
        Assert.Contains(
            "a call it refuses by itself that the engine refuses too - by its own deny rules, a file tool's told by its result alone - is a denial of Claude Code's, counted and recorded once;",
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
        })
        {
            Assert.Contains($"`{owner}`", architecture, StringComparison.Ordinal);
        }
    }
}
