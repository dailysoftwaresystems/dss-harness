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
    /// What the claude-code client does with a launch, its tools from the one table that gives them, the names it sets
    /// from the one list of them, and its bounds from the types that hold them.
    /// </summary>
    private static string ClaudeCodeClientProse()
    {
        var tools = string.Join(
            "; ",
            Agents.ClaudeCode.ClaudeCodeTools.Table.Select(row =>
                $"{row.Capability.ToString().ToLowerInvariant()}: {string.Join(", ", Agents.ClaudeCode.ClaudeCodeTools.ServedOf(row.Capability))}"))
             + $"; and a step that has a codebase graph (graph: tools) takes {Agents.ClaudeCode.ClaudeCodeTools.ServedName(Agents.AgentGraphSupport.ToolName)}, only where its step is given one"
             + $", and a step whose agent runs commands, of a repository that declares legs, takes {Agents.ClaudeCode.ClaudeCodeTools.ServedName(Agents.AgentMeasureSupport.ToolName)}, which measures one cell of the universe a class of the item names on one of the run's legs";
        var own = string.Join(", ", Agents.ClaudeCode.ClaudeCodeTools.Table.SelectMany(row => row.Tools).Select(tool => tool.Name));
        var ownWeb = string.Join(" and ", Agents.ClaudeCode.ClaudeCodeTools.OwnWeb.Select(tool => tool.Name));
        var launched = string.Join(", ", Agents.Policy.ClaudeSettings.Launched.Select(name => $"{name.Key}={name.Value}"));

        return "The claude-code client starts Claude Code once for each launch, a new session every time: never resumed "
            + "and never continued, keeping no session file (one a launch leaves all the same is removed once it has ended, "
            + "however it ended, and said), streamed "
            + "both ways - the step's prompt is the first message on its input, never an argument - in the agent's working "
            + "directory, with every other root it reaches added. It is given its model and no fallback, the step's effort, "
            + "turns and budget, and the tools of the agent's capabilities and no other, each served by the engine's tool server - "
            + tools
            + $" - and never {string.Join(", ", Agents.ClaudeCode.ClaudeCodeTools.NeverOffered)}. Its turns are the model's answers, as on "
            + "the API clients and as Claude Code counts the turns its --max-turns holds the step's maxTurns to: each response a launch "
            + "writes - the lines of one response sharing its message id, a line that names none an answer of its own, a message Claude "
            + "Code writes itself none - counted as it arrives, over every launch of the run, so a launch cut before its turn's result "
            + "still counts what it answered; the run's result, a side-task's record and a step's measure say them, never the engine's "
            + "messages to the launch. None of Claude Code's own tools "
            + $"is given: --tools names none (--tools \"\"), so its shell and its file tools ({own}) reach no launch, and a side-task "
            + $"alone keeps Claude Code's own {ownWeb} beside the engine's; each call is asked of the engine and run by the tool "
            + $"runtime below as on the API clients - a command by {Agents.Runtime.AgentTools.RunCommand}, under the isolation of the agent's commands, its output kept "
            + $"whole and paged - and every launch is given {Agents.Policy.ClaudeSettings.McpOutputVariable}={Agents.Policy.ClaudeSettings.McpOutputTokens}, "
            + $"three times the {Pages.PageRoom.ModelAnswer.ToString(CultureInfo.InvariantCulture)} characters of a page of a tool's answer: a page "
            + "reaches Claude Code as JSON, escaped, inside its answer's envelope, and a token is never less than one character, "
            + "so Claude Code never cuts an answer the engine paged. The run's reporter is given "
            + $"{string.Join(", ", Agents.ClaudeCode.ClaudeCodeTools.Reporter)} alone. A step with the web takes the harness's own "
            + $"{Agents.Runtime.AgentTools.WebFetch} - and {Agents.Runtime.AgentTools.WebSearch}, where it has a source to search - from the engine's tool server, and a step that has "
            + $"a codebase graph (graph: tools) takes its one tool, {Agents.ClaudeCode.ClaudeCodeTools.ServedName(Agents.AgentGraphSupport.ToolName)}, from the same: the one MCP "
            + $"server, of type sdk, named {Agents.ClaudeCode.ClaudeCodeTools.Server}, that the launch's --mcp-config names beside --strict-mcp-config (the step's "
            + $"{Agents.ClaudeCode.ClaudeLaunch.McpFileName}), served in the engine's own process over the launch's control channel - its initialize, "
            + "its tool list and its calls, our own code with no MCP library; each call of it is asked of the engine first, by its ask "
            + "rule, and run by the tool runtime below as on the API clients, each address decided and recorded, the graph's calls "
            + "answered by the same owner that answers them on the API clients - its edit operation among them - and each recorded "
            + "as a line of the step's graph - a step whose agent runs commands, of a repository that declares legs, takes the "
            + "measure tool from the same server, by the one rule the tool runtime offers it by, its calls answered and recorded "
            + "as every tool call is. A step's first user message holds the run's repository map first, then its context pack, then "
            + "its task - every launch a conversation of its own, it is given them as every launch is, and the order puts "
            + "them at the head of the message so every turn after the first reads them from Claude Code's own cache; the "
            + "engine sets no mark of its own. A launch whose init "
            + "does not show that server connected with its tools taken, or that connects another, never started; a call of it the "
            + "engine was not asked about and did not allow runs nothing and fails the step. Every call its own deny rules do not "
            + "refuse first is asked of the engine before it runs and answered from the policy above: a denial carries its rule's sentence, which the model "
            + $"reads, and is a line of the step's {Agents.AgentCallRecords.DenialsFileName}; a tool the launch was not given, or whose calls the engine "
            + "cannot read, is denied by name. A step whose reads are narrowed (graphReads: narrow, 'help codebase-graph') is "
            + $"denied a whole {Agents.Runtime.AgentTools.ViewFile} - one with no start_line and no end_line - of a file its graph holds parsed over the narrowing bound, "
            + "by the tool runtime as on the API clients and with the same words, a range passing and the same "
            + "two escapes counted and recorded as the graph's lines. An answer sent back (below) and a message the engine gives an agent at work are "
            + "written to the same launch, and a result written before the launch took it is not the answer; a handoff asked "
            + "interrupts the turn and asks the agent for its handoff in the same launch once every message it was given was "
            + "taken or dropped, no tool running in that turn, a "
            + "launch that does not take the interrupt ended whole. Every message is held to the model asked for, a date "
            + "after its id the same model: one another model wrote ends the step. Its environment holds nothing of a "
            + $"Claude Code session this tool was started in ({string.Join(", ", Agents.ClaudeCode.ClaudeLaunch.SessionNames.SkipLast(1))} and {Agents.ClaudeCode.ClaudeLaunch.SessionNames[^1]}, and every name beginning "
            + $"{string.Join(", ", Agents.ClaudeCode.ClaudeLaunch.SessionPrefixes.SkipLast(1))} or {Agents.ClaudeCode.ClaudeLaunch.SessionPrefixes[^1]} but "
            + $"{string.Join(", ", Agents.ClaudeCode.ClaudeLaunch.KeptNames)}, which says only where a program is, and a name agentPolicy.keepEnvironment "
            + "gives back), none of what every child "
            + "of an agent loses (the tool runtime below), no credential but the "
            + $"one its auth gives, and the names the engine sets: {launched}. A key Claude Code reports invalid is passed "
            + "over, and while the step has done no work the next key is given a new launch with the step's prompt; the "
            + $"control messages both ways are kept in the step's {Agents.ClaudeCode.ClaudeLaunch.ControlFileName} beside its stored stream. "
            + $"Its own settings outrank the user's, which it still reads: the user's {Agents.ClaudeCode.ClaudeUserSettings.FileName} in its Claude Code directory and the "
            + "user's CLAUDE.md reach every agent on this client, but no hook of theirs runs - every launch's settings say "
            + "disableAllHooks - and each launch's start names that settings file and its SHA-256 and says the hooks are off. A "
            + $"settings file that would give a launch a credential ({string.Join(", ", Agents.ClaudeCode.ClaudeLaunch.IdentityNames)} in its env, in any "
            + $"case, or {Agents.ClaudeCode.ClaudeUserSettings.KeyHelper}) refuses the run before any launch, naming the file: a credential is read from the "
            + $"slot alone - take it out of that file, or give the client a directory of its own in the slot: {Orchestration.ClaudeTranscripts.ConfigDirectoryVariable}, "
            + $"or the name agentClients.claude-code.{Agents.AgentClientSetting.ConfigDirKey} gives. "
            + "A launch is waited on, never timed by the client: its answer to the host's initialize request, however long it "
            + "takes, and its own end once its input was closed, each told every "
            + $"{Waiting.Waits.SaidEvery.TotalMinutes:0} minutes while it lasts, as every wait of the harness is, and "
            + "ended only by the step's own time, its stop, or an ending that ends the launch at once. "
            + "The client holds every launch to the protocol itself, so a version on which it does not hold names itself: a "
            + "launch that works before its init says "
            + "what it runs as, whose init offers a tool it was not "
            + "given or names a credential its auth did not give, or that refuses by itself a call the engine allows - as one "
            + "started without --permission-prompt-tool stdio does - never started; a call it refuses by itself that the engine "
            + "refuses too - by its own deny rules, told by its permission_denied line or by its result alone - is a denial of Claude Code's, counted "
            + "and recorded once; a call it ran that the engine was never asked "
            + "about, or refused, fails the step by the tool's name (before Claude Code 2.1.303 a call that ran unasked and failed "
            + "as Claude Code fails a call whose input did not read cannot be told from one, and is not caught); and each is "
            + "ended at once. A message the engine writes that no turn takes within the step's stall bound - counted from "
            + "when the launch answered its initialize request, and from the turn's end for one written while a turn ran - ends "
            + $"it {nameof(Agents.AgentRunStatus.Stalled)}. A request the model declines and an account at its limit - a rate limit event "
            + "that says rejected, or a turn Claude Code ends on rate_limit or billing_error after its own tries - each move "
            + "the step to its next entry, a new launch - an account at its limit to the same model's next key first, a "
            + $"declined request past the model's other keys to the next model - ending it {nameof(Agents.AgentRunStatus.Refused)} and "
            + $"{nameof(Agents.AgentRunStatus.RateLimited)} where none is left, with what the declined request was of where the "
            + "provider names it and when the limit lifts where it says; an overloaded service is no limit of the account's.";
    }
}
