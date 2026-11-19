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
    /// The side-tasks topic says every code its set-up ends on - the install of Claude Code not finished among them, which
    /// a script reading the help would otherwise take for a model cut short - how the second interrupt ends a chat, that
    /// a chat moved to a later model goes on from the conversation so far, and that a side-task that outgrew its context
    /// goes on from a compaction of it, a resume among the ways.
    /// </summary>
    [Fact]
    public void TheSideTasksTopic_SaysEveryCodeItsSetUpEndsOn_AndHowAChatEndsAndMoves()
    {
        var prose = Prose.Words(HelpText.Of("side-tasks").StandardOutput);

        // What the main tree's lock refuses a side-task, on every client, its programs read from the command reader's own list.
        Assert.Contains(
            "and the chat says when its command waited; on every client a command that leaves a process running after it ends is refused - one the shell runs in the "
                + "background (a lone &, never cmd's, which runs the next command in turn; a coproc; a process substitution's "
                + "body, <(...) or >(...); PowerShell's job &, or a command PowerShell reads given -AsJob), or one run by "
                + $"{string.Join(", ", DssHarness.Core.Agents.Policy.CommandAnalysis.Detaching)} (wherever a program of the command "
                + "runs; a program by any path to it, the shells' and PowerShell's own commands by the bare word alone - where "
                + "PowerShell reads the command, its cmdlets by a module-qualified name too, "
                + "Microsoft.PowerShell.Management\\Start-Process - so ./bin/start, scripts\\start or start.cmd is the "
                + "repository's own script), in every script the command runs, one it wrote and "
                + "then runs among them; what a program detaches by itself - a dev server's own "
                + "daemon mode, an interpreter's code - is not read, for the refusal is of what the command names - and a lock "
                + "that cannot be taken refuses the write or command it was taken for, saying why, the chat going on.",
            prose,
            StringComparison.Ordinal);

        Assert.Contains(
            $"{HarnessExit.ToolMissing} a client that cannot start where the entry that would serve first is on it - no claude, no npm - or, under agentPolicy.isolation \"required\", a "
                + $"machine that cannot isolate the agent's commands; {HarnessExit.Incomplete} the install of "
                + $"Claude Code could not reach the registry - an install is waited for to its end, told every {DssHarness.Core.Waiting.Waits.SaidEvery.TotalMinutes} minutes; and in one shot besides, {HarnessExit.CommandFailed} "
                + "the model failed past its retries and fallback, "
                + $"{HarnessExit.Incomplete} cut short by the account's limit, a stall or the clock, {HarnessExit.Cancelled} interrupted.",
            prose,
            StringComparison.Ordinal);
        Assert.Contains(
            "a second, /exit or the end of input ends it: a turn under way is cut and its result read - within "
                + $"{DssHarness.Core.Agents.ClaudeCode.ClaudeCodeClient.InterruptBound.TotalSeconds:0} s - and the lines the agent had not "
                + "taken are dropped, so what the turn used, cost and spent is kept; a launch still waiting its turn at the gate - a "
                + "limit not lifted, its wait said in the chat - is never begun. Each interrupt says whether a turn of the agent's was "
                + "under way to cut, or none was and nothing is cut - the first while a launch waits at the gate among them, the launch "
                + "begun once the wait passes.",
            prose,
            StringComparison.Ordinal);
        Assert.Contains(
            "a launch at work gives way as the person leaving does, and the lines it never took go on to the new one;",
            prose,
            StringComparison.Ordinal);
        Assert.Contains(
            "Ctrl+C there cuts its turn - a Claude Code launch's at that turn's result, an API client's request left where it "
                + "stands, what the provider had said it used counted and a cost it had said nothing of unknown - what it used "
                + $"kept, and it ends interrupted ({HarnessExit.Cancelled}), a launch still waiting its turn at the gate or to be "
                + "tried again never begun;",
            prose,
            StringComparison.Ordinal);
        Assert.Contains(
            "Ctrl+C interrupts the agent's turn where it stands and leaves the chat open, what was said kept - on a client that "
                + "stores its conversations up to its last whole turn, what the cut request used kept as the turn's;",
            prose,
            StringComparison.Ordinal);
        Assert.Contains(
            "On a tier the later models of its order are followed, and said in the chat: a turn moves on at its first failure, a "
                + "decline, a limit, a refused key or a model that cannot be started. A one shot's turn, and each message of a chat "
                + "whose client stores its conversations, whose agent failed on every model it moved through is tried again - after "
                + $"{DssHarness.Core.Agents.SideTasks.SideTaskService.RetryFirstWait.TotalSeconds:0} s, the wait doubled each time up "
                + $"to {DssHarness.Core.Waiting.Waits.SaidEvery.TotalMinutes:0} minutes, each wait said - for as long as it fails in a new way, and ends, said, once a try fails as an "
                + "earlier one did on the same model: a side-task has no limit of a run's, so no count of tries ends it, and an "
                + "interrupt ends a wait as it cuts a turn; a failure of the provider's service past its client's own tries, and a "
                + "refused key, are never tried again. A chat's launch held open is never tried again: it goes on from the "
                + "conversation so far, never from its first message, with the messages no turn answered sent on there.",
            prose,
            StringComparison.Ordinal);
        Assert.Contains(
            "A side-task whose turn outgrew the model's context - a chat's, or a one shot's - keeps no stored conversation to go "
                + "on with, for handed again it would meet the same wall: its next message, in the chat or in a --resume, begins a "
                + "new conversation from the engine's compaction of it, made as a step's conversation is - the whole conversation "
                + "since the last compaction, the person's messages as they were said - and said once, as the turn given it begins.",
            prose,
            StringComparison.Ordinal);
    }
}
