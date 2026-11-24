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
    /// The orchestrators topic names the five run verbs as they are typed, the one-unfinished-run rule, the codes a session
    /// ends with, and what each of the two interrupts does - the first a safe pause that starts no clock - from the code's
    /// own names and bounds.
    /// </summary>
    [Fact]
    public void OrchestratorsTopic_NamesTheRunVerbs_AndWhatEachInterruptDoes()
    {
        var result = HelpText.Of("orchestrators");
        var prose = Prose.Words(result.StandardOutput);

        foreach (var command in new[] { FlowService.RunCommand, FlowService.ResumeCommand, FlowService.AnswerCommand, FlowService.PauseCommand, FlowService.StopCommand })
        {
            Assert.Contains($"  {ToolPackage.Command} {command} ", result.StandardOutput, StringComparison.Ordinal);
        }

        Assert.Contains($"  {ToolPackage.Command} list-orchestrator [<o>] [--steps] [--questions] [--json]", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains(
            "Only one run of a repository is unfinished with work in the tree at once: a start, or a session of another run, is refused (13) "
                + "while one is, and so it is where an agent's record of another run cannot be read.",
            prose,
            StringComparison.Ordinal);
        Assert.Contains(
            "done (0), failed (20, or 21 where every failure that ended it was incomplete after its retries), paused on a question (1), or "
                + "paused or stopped on request (130)",
            prose,
            StringComparison.Ordinal);
        Assert.Contains(
            "In the terminal a session runs in, the first Ctrl+C - and off Windows SIGTERM or a hang-up - asks the safe pause, which starts no "
                + "clock of its own: each agent is asked for its handoff in its own conversation and answers within the workflow's handoffSeconds, "
                + "and a build may take as long as it takes. The second stops the run where it stands - a step whose "
                + "runner does not end within 90 s is recorded stopped all the same - and the command ends within 120 s whatever is left;",
            prose,
            StringComparison.Ordinal);
        Assert.Contains("which it reads every 2 s", prose, StringComparison.Ordinal);
        Assert.Contains(
            $"waits while the engine runs, for as long as that is, saying so every {DssHarness.Core.Waiting.Waits.SaidEvery.TotalMinutes.ToString(CultureInfo.InvariantCulture)} minutes - its own Ctrl+C ends the wait, the stop staying asked - "
                + "and --now ends its process: only on this machine, and only the process its owner names by its start.",
            prose,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Each command's own description says what it does to the copies a tree keeps, and list-orchestrator's --json the
    /// listing it prints - never the outcome of an --apply it does not have.
    /// </summary>
    [Theory]
    [InlineData("list-orchestrator", "--json Print the listing as one JSON document.")]
    [InlineData("fold-agent", "--json Print the outcome as one JSON document, the whole of standard output:")]
    [InlineData("delete-worktree", "and the copies kept in each - the mutation workers in .m and those an earlier version kept in .mutations, and on this machine the leg copies in .legs;")]
    [InlineData("clean", "and the copies of its tree it works in on this machine (.legs), wherever the leg runs,")]
    [InlineData("clean", "remove nothing, and exit as the clean would: 13 where a run holds the leg, a sweep its workers or a run its copy.")]
    public async Task EachCommandsOwnHelp_SaysWhatItDoesToCopies_AndWhatItsJsonPrints(string command, string expected)
    {
        var result = await CliRunner.RunAsync([command, "--help"], TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(expected, Prose.Words(result.StandardOutput), StringComparison.Ordinal);
    }

    /// <summary>stop-orchestrator's --now says it ends what the engine's steps left running with the engine, by kind.</summary>
    [Fact]
    public async Task StopOrchestratorsNow_SaysWhatItsStepsLeftRunningIsEndedWithIt()
    {
        var result = await CliRunner.RunAsync([FlowService.StopCommand, "--help"], TestContext.Current.CancellationToken);

        Assert.Contains(
            "End the engine's process and all it started, without asking it - only an engine of this machine, and only the process "
                + "its owner names by its start: for an engine that no longer answers. What its steps left running - a contained "
                + "child's session, the process group of what its legs started - is ended with it.",
            Prose.Words(result.StandardOutput),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The orchestrators topic names every orchestrator and agent command, what an orchestrator keeps where, and that
    /// deleting an agent is never forced.
    /// </summary>
    [Fact]
    public async Task OrchestratorsTopic_NamesEveryCommand_WhatIsKeptWhere_AndThatDeletingIsNeverForced()
    {
        var result = HelpText.Of("agents");
        var text = Prose.Words(result.StandardOutput);


        foreach (var command in new[] { "create-orchestrator", "delete-orchestrator", "list-orchestrator", "create-agent", "seed-agent", "refresh-agent", "rebase-agent", "fold-agent", "delete-agent" })
        {
            Assert.Contains($"  {ToolPackage.Command} {command} ", result.StandardOutput, StringComparison.Ordinal);
        }

        Assert.Contains("agents/<agent>/seed.json", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("agents/<agent>/shared/<sha256>", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("the bytes of each file it shares, kept to merge from", text, StringComparison.Ordinal);
        Assert.Contains(
            "temp/<launch>/ every temporary directory a run's launch and its children keep, one directory a launch - its children's and Claude "
                + "Code's own process's beside it - made with the launch, held for the run's life and never in the user's own places; taken away "
                + "whole with the run's directory",
            Prose.Words(result.StandardOutput),
            StringComparison.Ordinal);
        Assert.Contains(
            "artifacts/ the run's artifacts, made with the run and kept for its life: read and copied wherever they are asked for while it "
                + "runs, and taken away with the run's directory",
            Prose.Words(result.StandardOutput),
            StringComparison.Ordinal);
        Assert.Contains(
            "Every command that writes an agent's records names where they are on each way out once it has: its record, its seed, the directory "
            + "of the bytes it shares where it keeps any, and the rows its folds applied - each that is there.",
            text,
            StringComparison.Ordinal);
        Assert.Contains(
            "--settled <path> names a path you reconciled in the main tree by hand: once the rest is written, the main tree's copy - a file, or its "
            + "absence - is handed to the agent, over only what was weighed there, and recorded as shared, so its next fold starts from the "
            + "reconciliation.",
            text,
            StringComparison.Ordinal);
        Assert.Contains(
            "one only the agent changed is refused, since nothing was reconciled there and handing the main tree's copy would undo its change. It "
            + "is not a --force.",
            text,
            StringComparison.Ordinal);
        Assert.Contains("put back as the main tree's HEAD holds it included", text, StringComparison.Ordinal);
        Assert.Contains($"  {ToolPackage.Command} refresh-agent <o> <a> [<path>... | --all] [--except <path>]...", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("      [--stale <path>]... [--merge <path>]... [--apply]", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains(
            "--stale <path>, once for each, says its copy is not the agent's work and hands it the main tree's - taken only where the agent's copy, "
            + "as git would store it, is a blob the main tree's history held at that path, in any commit of its whole history that "
            + "changed it, and refused otherwise, nothing copied, as is one naming a path the agent holds no file at. The refusal over the agent's "
            + "changes names each such copy with the commit whose bytes it holds, and the arguments that hand them. It reaches a path the main tree did not move as well - a copy put back by hand to an older "
            + "version - and one naming a path the agent holds as the main tree does is let stand.",
            text,
            StringComparison.Ordinal);
        Assert.Contains(
            "A path the agent changed that the main tree moved too, each holding a file there, is weighed in full as a fold weighs one: the refusal "
            + "says beside it what a three-way merge of the two gives - from what the agent last shared, or its base - shows the conflicts, and names "
            + "the arguments that take the clean ones. Two such paths that differ in case alone, one file in the agent's worktree, are weighed once: "
            + "under the name the main tree's git gives the file, from the base's file found in any case, whatever core.ignorecase says. "
            + "--merge <path>, once for each, takes a clean merge: written into the agent's worktree as its "
            + "change, for its fold to write, and the path recorded as handed the main tree's copy, which the merge descends from. It is never taken "
            + "unasked, and a clean textual merge proves nothing about the program; one naming no path under the paths refreshed is refused as a "
            + "misspelling, and one naming a path with nothing left to merge is let stand. The arm registry both changed is merged by arm, unasked, "
            + "and taken where the result reads as a registry of the worktree as the refresh leaves it; one that does not, or that conflicts, is "
            + "among the agent's changes, and a --merge naming it or an anchor registry is refused. A path named by two of --except, --stale and "
            + $"--merge is a usage error, exit {HarnessExit.UsageError}.",
            text,
            StringComparison.Ordinal);
        Assert.Contains("the anchor registries' directory where none are, and anywhere in the tree with --all, which takes no path beside it", text, StringComparison.Ordinal);
        Assert.Contains(
            "A refresh refused over the agent's changes names every path it changed, and the arguments that leave them: --except <path>, once for "
            + "each, hands it every other path and leaves that one as the agent changed it, its own work for its fold to weigh against what the main "
            + "tree holds - said in the report, never silently; it is not a --force, nothing is left out that nobody named, and one naming a path "
            + "the refresh would not have refused is refused, nothing copied.",
            text,
            StringComparison.Ordinal);
        Assert.Contains("refuses the move, nothing changed, unless --settled <path> names it", text, StringComparison.Ordinal);
        Assert.Contains(
            $"a move that stopped part way, exit {HarnessExit.Incomplete}, is finished where it was going by running it again - the command its "
            + "last words name, with every option the move was given - what it wrote held as the new base holds it, and every other command "
            + "refuses the agent until then, naming that same command.",
            text,
            StringComparison.Ordinal);
        Assert.Contains($"  {ToolPackage.Command} rebase-agent <o> <a> [--apply] [--json] [--merge <path>]... [--settled <path>]...", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains(
            "Where a three-way merge of the two is clean - git's, from what the old base held, of the new base's version and the agent's - "
            + "--merge <path>, once for each, writes it into the agent's worktree as its own change on the new base, whether it can be run merged "
            + "with its content: it is never taken unasked, and a clean textual merge proves nothing about the program. Nothing is recorded for it: "
            + "its fold weighs it against the new base. The refusal says what a merge of each such path gives, and names the command that takes the "
            + "clean ones.",
            text,
            StringComparison.Ordinal);
        Assert.Contains("a file turned into a directory, or a directory into a file or a submodule's entry - and its own changes", text, StringComparison.Ordinal);
        Assert.Contains("never forced, and its evidence check kept, so a file written late stops it", text, StringComparison.Ordinal);
        Assert.Contains("a file changed or new since is work, left for you", text, StringComparison.Ordinal);
        Assert.Contains("one not found is said, and one found and not kept stops it before anything is closed", text, StringComparison.Ordinal);
        Assert.Contains("A path this process cannot look at is never read as absent: the fold fails, nothing written.", text, StringComparison.Ordinal);
        Assert.Contains("It removes the record last, so one that stops part way finishes when run again.", text, StringComparison.Ordinal);
        Assert.Contains("Seeding again hands besides every path the main tree holds otherwise than the agent shares it, committed or not.", text, StringComparison.Ordinal);
        Assert.Contains(
            "a symbolic link the main tree committed is named and never handed, and so is a submodule's entry. A file the main tree turned into a "
            + "directory, or a directory it turned into a file, is handed as git holds it: what goes is removed first, then what comes is copied.",
            text,
            StringComparison.Ordinal);
        Assert.Contains(
            "and records them as handed to it, so its fold leaves them out; one the agent already holds as the main tree does is recorded as shared - "
            + "listed, and written only with --apply - so its record says what it holds;",
            text,
            StringComparison.Ordinal);
        Assert.Contains(
            "refuses the hand-over, forced or not, naming each: copied, it would write over them, or through the link out of its worktree. A file of "
            + "the agent's written after it was weighed is never written over or removed by either, forced or not: the hand-over stops there, exit "
            + $"{HarnessExit.Incomplete}, naming it, and run again weighs it anew. Neither "
            + "hands anything while a move of the agent's base stands stopped part way, or its HEAD is off its base. Both say when the agent's base "
            + "is not the main tree's HEAD. A path named otherwise than in UTF-8, committed or not, refuses any hand-over, naming it as git quotes it: "
            + "no file opens here by such a name.",
            text,
            StringComparison.Ordinal);
        Assert.Contains(
            "or anything of its own where the main tree committed a file, ignored or staged included - refuses the move",
            text,
            StringComparison.Ordinal);
        Assert.Contains(
            "A file it made that is the very file the main tree committed there - the same content as git compares it, and the same mode where git "
            + "trusts one - is no such change: it is listed as held as the new base holds it already, and nothing is written.",
            text,
            StringComparison.Ordinal);
        Assert.Contains(
            "a file or a link where the new base holds a directory, and what it keeps - its own, settled or shared - in a directory where the new "
            + "base holds a file, or a submodule's entry that would hide it from git",
            text,
            StringComparison.Ordinal);
        Assert.Contains(
            "Only such a move is ever finished, and only from its base or where it goes: a HEAD moved by hand - back, forward or beside its base - "
            + "or naming no commit is never taken for one, and each command refuses it, saying how to put it back; a commit made inside the agent "
            + "is said to be one, and where git cannot say which a HEAD is, each says so and fails, never guessing.",
            text,
            StringComparison.Ordinal);
        Assert.Contains(
            "deleted, removed first, with the directories that leaves empty, so a file it turned into a directory, or a directory into a file, "
            + "makes room for what replaced it",
            text,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The orchestrators topic says that delete-orchestrator refuses while a directory below the orchestrator's holds a
    /// .git of its own that git records no worktree at, as it does while a worktree is left there, and that an agent with
    /// no worktree never stands in its way.
    /// </summary>
    [Fact]
    public async Task OrchestratorsTopic_SaysDeleteOrchestratorRefuses_WhileADirectoryGitRecordsNoWorktreeAtIsBelowIt()
    {
        var result = HelpText.Of("agents");

        Assert.Contains(
            "delete-orchestrator deletes the orchestrator once every agent of it with a worktree is deleted and no worktree is left "
                + "below its directory, nor any directory holding a .git of its own that git records no worktree at - until then it "
                + $"refuses ({HarnessExit.Refused}), naming each - and the evidence, transcripts, steps and sessions its agents kept, "
                + "whatever their kind or state, only with --delete-evidence; an agent with no worktree holds nothing but records, "
                + "and goes with it, deleted or not, and a run's codebase graph - made from the tree, and made again whenever asked - "
                + "goes with it and is never counted among what --delete-evidence guards, as does a copy of a commit a run's sweep made "
                + "in its directory and was cut off before taking away, git's record of it with it.",
            Prose.Words(result.StandardOutput),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The orchestrators topic says what an agent with no worktree is to the verbs a person types: never open, so the
    /// limit leaves it out; nothing recorded of it when create-agent runs again; no session, --session beside
    /// --no-worktree refused; and a deletion that is one write, saying what has nothing to act on.
    /// </summary>
    [Fact]
    public async Task OrchestratorsTopic_SaysWhatTheVerbsDoWithAnAgentWithNoWorktree()
    {
        var text = Prose.Words(HelpText.Of("agents").StandardOutput);

        foreach (var said in new[]
        {
            "It records no session - --session beside --no-worktree is a usage error - and delete-agent marks it deleted in one "
                + "write, folding nothing, removing no worktree and looking for no transcript; --discard-uncommitted and the "
                + "allowances have nothing to act on there, and it says so.",
            $"--parallel, {OrchestratorRecord.DefaultParallel} unless given, is the most open agents at once - every agent with a "
                + "worktree not yet deleted, and every worktree git lists below the orchestrator's directory: create-agent refuses "
                + "the next, naming them, and list-orchestrator counts them. An agent with no worktree is never open. Run again, "
                + "create-orchestrator changes only the limit and the session, and create-agent records only the session, and "
                + "nothing of an agent with no worktree;",
        })
        {
            Assert.Contains(said, text, StringComparison.Ordinal);
        }
    }

    /// <summary>The orchestrators topic says where a run starts from, and which steps are served so far.</summary>
    [Fact]
    public void OrchestratorsTopic_SaysWhereARunStartsFrom_AndWhichStepsAreServed()
    {
        var orchestrators = Prose.Words(HelpText.Of("orchestrators").StandardOutput);

        Assert.Contains(
            "A run starts from the main checkout's HEAD on a branch of its own, and records that commit and branch: it is refused (13), having made nothing, where HEAD is detached, where the checkout is on the branch the run's pull request is opened against - the pull-request step's base, filled from the run's inputs and its name, else the remote's default branch - where a merge is under way, and where changes are staged. Where the repository has a remote and neither base can be told, the start says so, its branch held to none.",
            orchestrators,
            StringComparison.Ordinal);
        Assert.Contains(
            "The steps served so far are orient, graphify-codebase, validate-plan, create-agent, reason, build, test, run, check-mutations, anchor-balance, anchor-citations, class-closure, route-findings, ci-read, ci-wait, fold, delete-agent, refresh, rebase, commit, push, pull-request ('dssharness help workflows'): a step of any other type fails, saying no runner serves its type.",
            orchestrators,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The orchestrators topic says what a run's report says of the run itself - its sessions and each of the times the
    /// report computes, the wall, the active and the paused, beside its wait for a person and its budget - so a reader
    /// knows which times the report holds and what each one counts.
    /// </summary>
    [Fact]
    public void OrchestratorsTopic_SaysTheRunsTimesTheReportHolds()
    {
        Assert.Contains(
            $"The report of a run. {FlowService.ReadReportCommand} answers it from the run's records alone - the same bytes for "
                + "the same records, whatever a model might say - and never asks one: what the run was and how it stands, with its "
                + "sessions, its wall time, its active time (the union of every attempt's runs), its pause (the time between its "
                + "sessions), its wait for a person and its budget; the totals of its tokens by kind",
            Prose.Words(HelpText.Of("orchestrators").StandardOutput),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The orchestrators topic says a step's wait on the machine's dispatch queue keeps the class of it and each tenant
    /// waited behind - in the step's measure and in the report's row of it - so a reader knows a wait behind another
    /// repository's units can be told from one behind its own run's.
    /// </summary>
    [Fact]
    public void OrchestratorsTopic_SaysAQueueWaitKeepsItsClassAndTheTenantsWaitedBehind()
    {
        var orchestrators = Prose.Words(HelpText.Of("orchestrators").StandardOutput);

        foreach (var said in new[]
        {
            "its waits by cause whole across sessions - the machine holding every leg of it still to run among them, a wait on its dispatch queue keeping the class of it "
                + "and each tenant waited behind - its run time",
            "one row per step instance, in the order the run made them, with its state, its attempts, its waits by cause - a wait on the machine's dispatch queue naming the "
                + "class of it and each tenant waited behind, so a reader can tell its own run's units from another repository's - its run time,",
        })
        {
            Assert.Contains(said, orchestrators, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The orchestrators topic says the error rows of a run's report read each failed attempt's own first error and the
    /// question its ending raised, from its measure entry - never the last attempt's error nor the step's newest
    /// question - so a reader knows an attempt before the last keeps what it broke on and asked.
    /// </summary>
    [Fact]
    public void OrchestratorsTopic_SaysEachFailedAttemptKeepsItsOwnFirstErrorAndItsOwnQuestion()
    {
        Assert.Contains(
            "every attempt that failed, with why it ended, what it threw, the first error its digests read and the question "
                + "its ending raised - each the attempt's own, kept by its measure entry, never the last attempt's",
            Prose.Words(HelpText.Of("orchestrators").StandardOutput),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The orchestrators topic says where the resume completes a cut-off attempt from, by the kind of step: a reason
    /// step's stored stream, charging only what its record had not, and a machine step's ledger in its directory, its
    /// legs and where its work stopped - so a reader knows what is read for each.
    /// </summary>
    [Fact]
    public void OrchestratorsTopic_SaysWhereTheResumeCompletesACutAttemptFrom_ByTheKindOfStep()
    {
        Assert.Contains(
            "an attempt a crash cut off is completed at the resume from the step's own records: a reason step's stored "
                + "stream, charging only what its record had not, and a machine step's ledger in its directory, its legs and "
                + "where its work stopped",
            Prose.Words(HelpText.Of("orchestrators").StandardOutput),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The orchestrators topic says what an analysis's impact is held to: every number it says one of its citations',
    /// read in the unit the report holds the cited member in - bare as written, joined to its unit converted exactly into
    /// the member's, a kind no member is never - whatever wraps it, and a word that reads as no one number refused, as the
    /// evidence check holds it.
    /// </summary>
    [Fact]
    public void OrchestratorsTopic_SaysEveryNumberAnImpactSays_WithItsUnitOrNot_IsACitationsValue()
    {
        Assert.Contains(
            "and every number its impact says is one of its citations', read in the unit the report holds the cited member in (a "
                + "time in seconds, the budget's reasoning in minutes, a cost in dollars, a cache share in percent, the rest a "
                + "count): a number written bare is the cited value as written, one joined to its unit (3600s, 1.5h, 12k tokens, "
                + "$0.02, 28%) is held where a cited member of its unit's kind holds it, converted exactly into that member's "
                + "unit - 1.5h is a budget's 90 minutes, and 60 hours no wait's 60 seconds - and one of a kind no member is (2x, "
                + "a size, another currency) never; a number is read whatever wraps it (Markdown, a comparison, a currency's "
                + "sign), and a word that starts as a number and reads as no one number (a date, a range) is refused. An "
                + "analysis cannot invent a measurement.",
            Prose.Words(HelpText.Of("orchestrators").StandardOutput),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The orchestrators topic says what a run's report says its reviews converged to: the counts by which a repository
    /// sees whether its rounds converge, of each item and of the run.
    /// </summary>
    [Fact]
    public void OrchestratorsTopic_SaysWhatTheReportSaysTheReviewsConvergedTo()
    {
        Assert.Contains(
            "what its reviews converged to - the classes closed, each item's counts (its rounds of review and home audits, and of "
                + "each class the sites consolidated and deleted, the cells pinned, the neighbours measured and not, and how often a "
                + "review sent it back), the classes reopened, the hotspots, the issues carried to a later round, fixed there or "
                + "left, and what its loops left - and the signals a reader starts from",
            Prose.Words(HelpText.Of("orchestrators").StandardOutput),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The orchestrators and layout topics say where a step's contained child's session, and the group of its legs' work, is recorded, and that every command
    /// over an orchestrator first ends what a killed process left of them - and on which systems - from the layout's own names.
    /// </summary>
    [Fact]
    public void OrchestratorsTopic_SaysWhatAKilledProcessLeftRunning_IsEndedByTheNextCommand()
    {
        var orchestrators = Prose.Words(HelpText.Of("orchestrators").StandardOutput);
        var layout = Prose.Words(HelpText.Of("layout").StandardOutput);
        var record = $"<step>/{OrchestratorLayout.ChildrenDirectoryName}/<session>{OrchestratorLayout.ChildExtension}";

        Assert.Contains($"{record} in either kind of step, on Linux and macOS: a session a contained child of the step leads while it runs, or the process group a phase of its legs - a set-up's up line apart - its keepAwake command or what carries a leg to a host leads while anything is in it", orchestrators, StringComparison.Ordinal);
        Assert.Contains(
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
            + "that started it.",
            orchestrators,
            StringComparison.Ordinal);
        Assert.Contains(
            $"{HarnessLayout.OrchestratorsDirectoryName}/<o>/.../<instance>/{OrchestratorLayout.ChildrenDirectoryName}/<session>{OrchestratorLayout.ChildExtension} "
            + "ignored; in either kind of step, on Linux and macOS: a session a contained child of the step leads while it "
            + "runs, or the process group a phase of its legs - a set-up's up line apart - its keepAwake command or what carries a "
            + "leg to a host leads while anything is in it, ended by the next command over the orchestrator - and by "
            + "stop-orchestrator --now - once its starter has gone",
            layout,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The orchestrators topic says what a fold does with a path both trees changed: what a three-way merge of it gives
    /// is said, a clean one is taken only where --merge names it, what is never merged, and that whether a file can be
    /// run is merged with its content. The bound it names is the code's.
    /// </summary>
    [Fact]
    public async Task OrchestratorsTopic_SaysWhatAFoldMerges_WhatItNeverMerges_AndHowTheBitIsMerged()
    {
        var result = HelpText.Of("agents");
        var text = Prose.Words(result.StandardOutput);


        foreach (var command in new[] { "fold-agent", "delete-agent" })
        {
            Assert.Contains($"  {ToolPackage.Command} {command} <o> <a> [--apply] [--json] [--merge <path>]... [--settled <path>]...", result.StandardOutput, StringComparison.Ordinal);
        }

        Assert.Contains(
            "already in the main tree; merged, where both trees changed it and --merge names it, or it is the arm registry, merged by arm unasked; "
            + "or settled",
            text,
            StringComparison.Ordinal);
        Assert.Contains(
            "its refusal says what a three-way merge of the two gives - git's merge, of the forms git stores, from what both held, which for a shared "
            + "path are the bytes kept beside the agent's seed, or found by their digest in the main tree's history where none were kept, and for any "
            + "other its base's - clean, with the hunks each side brought; conflicting, with the conflicts shown; or none offered, with why - and names "
            + "the command that takes the clean ones.",
            text,
            StringComparison.Ordinal);
        Assert.Contains(
            "--merge <path>, once for each, takes a clean merge: written into both trees, the main tree's first, and recorded as shared once both "
            + "hold it, so a fold that stopped between the two merges again to the same bytes. It is never taken unasked, a conflict never, and a "
            + "clean textual merge proves nothing about the program",
            text,
            StringComparison.Ordinal);
        Assert.Contains(
            "A --merge naming no path the fold weighs is refused as the misspelling it most likely is, and one naming a path with nothing left to "
            + "merge is let stand, so the command line of a fold that stopped part way runs again; a path named with both --merge and --settled is "
            + $"a usage error, exit {HarnessExit.UsageError}, and a --merge naming an anchor registry or the arm registry is refused.",
            text,
            StringComparison.Ordinal);
        Assert.Contains(
            "Never merged, whatever is named: a deletion against a change, a path both trees made, a version that is a link or is reached through "
            + "one, a file git reads as binary or will not merge, one "
            + "whose merge attribute asks for another merge, and an anchor registry; nor is the "
            + "arm registry ever merged by line.",
            text,
            StringComparison.Ordinal);
        Assert.Contains(
            "The arm registry (mutations.registry) both trees changed is merged by arm, unasked - each arm, and each section's heading (the comments a "
            + "blank line parts from the row below them), from the side that alone added, removed or "
            + "changed it, each version as its tree holds it, from nothing where neither held a registry before - and taken only where the merged text "
            + "reads as a registry of the tree as the command leaves it, as check-mutations reads one ('help mutations'); an arm or a heading the two "
            + "changed differently, a heading no row of the merge would stand under, a version that cannot be read as a registry, or a merged text that "
            + "does not read as one refuses the path, each shown, and so "
            + "do shared bytes that cannot be found, since an arm one side removed would come back.",
            text,
            StringComparison.Ordinal);
        Assert.Contains(
            "The arm registry is merged by arm, unasked, as a fold merges it (below), from the old base's version, of the new base's as the worktree "
            + "checks it out and the agent's, and judged by the worktree as the move leaves it.",
            text,
            StringComparison.Ordinal);
        Assert.Contains(
            "Whether a file can be run is merged with its content: the merged file takes the bit of the side that changed it, and the bit alone never "
            + "conflicts - but a registry both trees made, one so that it can be run and one not, is offered no merge, since nothing either held "
            + "before says which is meant; Windows keeps no such bit, and none is read or set there.",
            text,
            StringComparison.Ordinal);
        Assert.DoesNotContain("mode is never merged", text, StringComparison.Ordinal);
        Assert.Contains(
            "A --merge naming a path the two commits hold alike is refused as the misspelling it most likely is, and one naming any other path "
            + "they changed is let stand, so the command that finishes a stopped move runs again; a path named with both --merge and --settled is "
            + $"a usage error, exit {HarnessExit.UsageError}; and a path one of whose versions is a link, or that the agent reaches through one, is "
            + "offered no merge.",
            text,
            StringComparison.Ordinal);
        Assert.Contains("a change, a new file, a deletion, a file renamed in case alone, a link, a submodule's entry,", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The orchestrators topic, the architecture and the README say alike that a file the new base renamed in case alone
    /// comes in only with its old name, never over the agent's copy or its deletion of it, that a file the agent's session
    /// wrote after it was weighed stops a move before git writes anything, that a fold refuses a rename of case alone the
    /// agent made, naming git mv -f for the main tree, and weighs it against the base's file under its old name, and that a
    /// refresh weighs two names of one file once.
    /// </summary>
    [Fact]
    public async Task OrchestratorsTopic_SaysWhatRenamesOfCaseAlone_AndASessionsWrites_DoToAMoveAFoldAndARefresh()
    {
        var result = HelpText.Of("agents");
        var text = Prose.Words(result.StandardOutput);
        var architecture = Prose.Document("docs", "architecture.md");
        var readme = Prose.Document("README.md");

        Assert.Contains(
            "So does a file the new base renamed in case alone, where the agent's file system finds a name in any case, unless its old name comes in "
            + "with it: where the agent keeps that - its own change, settled or shared - writing the new name would write over it, and both are named, "
            + "a shared one it changed to be folded first, since no refresh hands it, and one it did not to be refreshed under the new name; where the "
            + "agent's worktree no longer holds it, the two are named as a rename and a deletion of one file - its old name to be put back and the move "
            + "run again, the new name deleted after only where the agent's session deleted the old - unless a move that stopped part way removed it, "
            + "as its record says, which running it again finishes. "
            + $"A file of the agent's written after it was weighed is never written over or removed: the move stops there, exit {HarnessExit.Incomplete}, "
            + "before git writes anything, naming it.",
            text,
            StringComparison.Ordinal);
        Assert.Contains(
            "A rename of case alone the agent made, where both trees find a name in any case and the main tree's git index names the file as before, is "
            + "refused by name: a fold writes files, and the main tree's git would go on naming the file as before - git mv -f there makes the rename, of "
            + "a file in a directory renamed in case too, and the fold run again finds the old name absent from both. The renamed file is weighed "
            + "against the base's under its old name, found in any case whatever core.ignorecase says: an edit made with the rename merges with the main "
            + "tree's, and the main tree's "
            + "removal of the file refuses it, never undone.",
            text,
            StringComparison.Ordinal);
        Assert.Contains(
            "It comes in only with its old name: where the agent keeps what it holds there - declared settled, shared with the main tree, or its own "
            + "change - the new name refuses the move, naming both, since writing it would write over that; a shared file the agent changed is to be "
            + "folded first, since no refresh hands a path the agent changed, and one it did not to be refreshed under the new name, which records the "
            + "file it holds as shared there. Where it holds nothing at the old name, the new name refuses the move as a rename and a deletion of one "
            + "file - its old name to be put back with `git checkout HEAD` and the move run again, the new name deleted after only where the agent's "
            + "session deleted the old, or, shared, never taken by a move and so the agent's deletion, folded and committed in the main tree - unless "
            + "the move being finished removed it, the new name written last, as its record says (`MovingRemoved`: the old names the agent's index no "
            + "longer holds once the git checking out what the move writes first ends, having written it all or stopped part way). git leaves the "
            + "index and the disk alike after that checkout and after a `git rm` of the agent's session; a removal of the move's its record missed - a "
            + "move killed between git and its record - is refused so, costing a refusal, never a file. The main checkout keeps the commit the "
            + "move goes to alive (`refs/harness/worktree-moving/<orchestrator>/<agent>`, exit 20 and nothing changed where it cannot) and the record "
            + "names where the move goes (`Moving`) before anything is written; then each path the move takes is asked to hold what was measured there "
            + "(`RebasePlan.Held`, `AgentFold.RequireTakenAsync`), so a file the agent's session wrote since stops the move, exit 21, before git "
            + "writes anything, never written over;",
            architecture,
            StringComparison.Ordinal);
        Assert.Contains(
            "The base's entry and HEAD's are found alike, as git spells the path and, where the main tree's file system finds a name in any case, in "
            + "any case, whatever `core.ignorecase` says, so a rename of case alone is the commit's that made it, and none other's.",
            architecture,
            StringComparison.Ordinal);
        Assert.Contains(
            "where its git index spells that file otherwise than its base does, found in any case, the agent renamed it in case alone, and each "
            + "path is read only where a tree spells the file so (`AgentFold.Spellings`) - as its git index does, where it holds the file under one "
            + "spelling, since a rename git makes in the index alone leaves a directory's name on disk as it was, and otherwise as its file system "
            + "does (`PathCase.Spelled`) - never as the other. Where the main tree's git index spells that file otherwise, each tree "
            + "holding it, and the main tree finds a name in any case too, a fold cannot write the rename - the main tree's git would go on naming the "
            + "file as before - so it is refused by name, saying to run `git mv -f` there, forced since git finds the new name taken where the "
            + "repository does not say it ignores case; once that is run, the old name is already in, absent from both. Every path the agent shares "
            + "nothing for is weighed against the base's entry found as git spells it, as `Came` finds it, so a file renamed in case alone is weighed "
            + "against the base's under its old name: an edit made with the rename merges with the main tree's, and a removal of the file in the main "
            + "tree refuses it, never undone.",
            architecture,
            StringComparison.Ordinal);
        Assert.Contains(
            "(`TwoChanges`, from what the seed records or the agent's base holds, found as git spells the path; the main tree's file the side merged "
            + "into) - two such paths that differ in case alone, one file in the agent's worktree where it finds a name in any case, once, under the "
            + "name the main tree's git index spells the file by, from the base's entry found in any case, the others - one left as the agent changed "
            + "it among them - no file of the main tree's, whatever its file system finds under them:",
            architecture,
            StringComparison.Ordinal);
        Assert.Contains("a path the agent already holds as the main tree does - the same file, or no file in either, a directory of the agent's own files there included -", architecture, StringComparison.Ordinal);
        Assert.Contains(
            "refused over anything of its own what comes in would go over, a file renamed in case alone over the agent's copy at its old name, or its "
            + "deletion of it, among it; a file the agent's session wrote after it was weighed stops it, exit 21, never written over;",
            readme,
            StringComparison.Ordinal);
        Assert.Contains(
            "a rename of case alone the agent made is refused, saying to `git mv -f` it in the main tree, and once made there is weighed against the "
            + "base's file under its old name;",
            readme,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The orchestrators topic says the one rule of what a dry run exits with, where its summary then stands, that a row
    /// an earlier fold applied is weighed cell by cell with --accept-over, and that a closed agent's husk is answered by
    /// its dry run as every --apply of it answers.
    /// </summary>
    [Fact]
    public async Task OrchestratorsTopic_SaysWhatADryRunExitsWith_AndWeighsRowsCellByCell()
    {
        var result = HelpText.Of("agents");
        var text = Prose.Words(result.StandardOutput);

        Assert.Contains(
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
            + "sync made goes with the worktree, and is only said. A record of the copies hosts keep that cannot be read refuses "
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
            + "request lacks - 'fold-new-rows' (--new), 'fold-lost-rows' (--accept-lost), 'fold-over-cells' (--accept-over) "
            + "or 'fold-two-changes' (--merge) - and refusals each reason it is refused for, 'conflict' where a path both "
            + "trees changed has a merge that conflicts, 'closure' where a row whose status moves to closed holds no closing "
            + "record that closes its classes - under anchors.closure: required, of a lane folded by hand, its searches and "
            + "measurements read as given - and 'other' for anything else, so a request refused for conflicting "
            + "merges alone says 'conflict' and nothing else; folded says what the --apply of a fold and of a deletion did, gone "
            + "through or stopped part way after writing some of it - what it wrote, deleted, merged and handed, the arm registry "
            + "merged by arm, the registries and rows it applied, and the cells --accept-over took - and is null for every other "
            + "answer and where it did nothing at all; planned says what the plan of the verb names, its dry run's and its "
            + "--apply's alike - what a rebase takes and keeps, what a refresh or a fold hands, what each merges, what a fold, a "
            + "deletion's among them, writes and deletes, the registry files its rows change, as git names them, and the rows it "
            + "applies - and what each consent it lacks and each conflict it meets is about: mergeable, the paths both trees "
            + "changed whose clean merge nobody asked for; conflicting, those whose merge conflicts; newRows, lostCells and "
            + "overCells; null where no plan was measured, a deletion that folds nothing among them. A refresh or a rebase over a "
            + "path both trees changed whose merge is clean lacks --merge as a fold does - needs-consent, its code 13 still - and "
            + "one over conflicting paths alone is refused for a conflict. An --apply refused at its hold answers the same "
            + "document, applied true: request 'sound', and the run it met in heldNow. Stopped before "
            + "anything is weighed - a command line the parser refuses, a --directory that is not there, a repository not "
            + "initialised, another usage error, or a refusal met before then, an agent that is not there among them - it "
            + "answers with the same document, empty: applied false, request and heldNow null, no lines, no needs, no "
            + "refusals, and folded and planned null.",
            text,
            StringComparison.Ordinal);
        Assert.Contains(
            "A dry run of the four asks with the very hold its --apply takes, so a run holding what it needs is named and the dry "
            + $"run refused itself, exit {HarnessExit.Refused}, saying that nothing in the request needs correcting; the refusal an "
            + "--apply gives names the holder in the words of the dry run's 'held now:' line, where its run's leg stands included. "
            + "A holder is a fact about one moment, so 0 from a dry run never promises the "
            + "tree will still be free when the --apply is typed.",
            text,
            StringComparison.Ordinal);
        Assert.Contains(
            "delete-agent alone names a copy a run holds, since it removes every copy with the worktree: a host's, a leg copy kept "
            + "in it, and a mutation worker, each in its own words.",
            text,
            StringComparison.Ordinal);
        Assert.Contains(
            "and --apply refuses the fold, writing nothing, until each is named - and so the dry run that shows it "
            + $"exits {HarnessExit.Refused} itself.",
            text,
            StringComparison.Ordinal);
        Assert.Contains(
            "One declared anew over it is weighed cell by cell - priority, status, trigger, closing, cross-refs - from what that "
            + "fold recorded as declared, against the registries as the write finds them: a cell only the agent changed is "
            + "written; one only the registries changed is kept, and said, and stays kept at each fold until the agent changes "
            + "it; one both changed alike is left; and one both changed otherwise is written over the registries' only where "
            + "--accept-over <ID>:<cell> names it, the dry run showing the three texts - what --accept-over consents to includes "
            + "what the registries' text loses. The fold never writes the agent's own row files: before it changes a kept cell, "
            + "the agent writes the registries' text into it, or its change is two changes of one cell.",
            text,
            StringComparison.Ordinal);
        Assert.Contains(
            "The verdict is one decision: where anchors.triggerCarriesVerdict holds, a row's status and its trigger state it "
            + "together, so where the registries changed one of the two and the agent the other, and the row taken one from each "
            + "would state two verdicts though the agent's states one - a row the main tree reopened that the agent closes again "
            + "with its trigger, its status file closed as it was, or a row the main tree closed that the agent keeps open - both "
            + "are weighed as both changed, each line of the dry run saying why, and written, the agent's together, only where "
            + "--accept-over <ID>:status and --accept-over <ID>:trigger name them; until both are named nothing of the row is "
            + "written, and the registries' verdict stands. A row the agent declares with two verdicts itself is refused for "
            + "them before any cell of it is weighed, whatever the row taken one from each would state, no consent asked. Where "
            + "the setting does not hold, the status alone carries the verdict and is weighed as "
            + "any cell is: one the agent's file holds as it declared it then is kept, whatever its trigger says.",
            text,
            StringComparison.Ordinal);
        Assert.Contains(
            "since every --apply of it ends so, its dry run exits " + HarnessExit.Incomplete + " too, naming the same command and keeping nothing.",
            text,
            StringComparison.Ordinal);
        Assert.Contains("delete-agent refuses to run from inside the worktree it removes, and so does its dry run,", text, StringComparison.Ordinal);
    }
}
