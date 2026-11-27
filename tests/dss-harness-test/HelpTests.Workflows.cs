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
    /// The workflows topic says how a test step's filter is read as units, joined by the invocation's filterJoin, skipped
    /// where it reads none, switched by filterWhen and kept under its affected key for run.affectedTests, and what union
    /// joins; the orchestrators topic names the run's record of affected tests among its files.
    /// </summary>
    [Fact]
    public void WorkflowsTopic_SaysHowATestStepsUnitsAreRead_Kept_AndJoined()
    {
        var prose = Prose.Words(HelpText.Of("workflows").StandardOutput);

        Assert.Contains(
            "A test step's filter names the tests it runs as units - a list, or an expression read where it starts - each given after "
                + "the invocation's filterArg, all joined into one value by its filterJoin - by '|' for ctest, dotnet test and dart test "
                + "where it declares none; a filter that reads no unit runs no test, and the step ends skipped, saying its noTests. Its "
                + "filterWhen switches the filter: where it reads "
                + "false every test of its legs runs, as the shipped gate does where the run's input finalTests is all. Its affected keeps "
                + "the units it runs in the run's affected-tests.json under the key it fills, before the step runs: a later step under "
                + "that key replaces them, and one for a work item a plan update takes back, or one its when skips, removes them. "
                + "run.affectedTests reads every unit kept, each once: the shipped verify steps test each item on its own units alone, "
                + "and the gate on run.affectedTests.",
            prose,
            StringComparison.Ordinal);
        Assert.Contains("'union(list, list, ...)' for the lists joined, each value once in the order first read and a null read as none", prose, StringComparison.Ordinal);
        Assert.Contains(
            Prose.Words($"{OrchestratorLayout.AffectedTestsFileName} the run's record of affected tests: the units each test step that keeps them (affected) ran"),
            Prose.Words(HelpText.Of("orchestrators").StandardOutput),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The workflows topic says what a leg step's red entry carries of the units its build failed - failedUnits, which the
    /// output's schema declares - and that {run.red} says what the leg's line said of them, and what it carries of the
    /// errors its phase printed - digest, the first four distinct said for a model with the rest counted, the tail then
    /// holding only what no parser held.
    /// </summary>
    [Fact]
    public void WorkflowsTopic_SaysWhatARedLegsEntryCarriesOfItsUnits()
    {
        var prose = Prose.Words(HelpText.Of("workflows").StandardOutput);

        Assert.Contains(
            "keepGoing its builds of its legs go on past the units that fail to build, as --keep-going has them, so each red leg's "
                + "entry in the step's red names, in failedUnits, every unit its build failed - by source and step, with what its line "
                + "said of them - and {run.red} says that line's words",
            prose,
            StringComparison.Ordinal);
        Assert.Contains("a red leg, with what its line said of the units its build failed where its own tool failed it, a red check of CI,", prose, StringComparison.Ordinal);

        foreach (var type in new[] { "build", "test", "run" })
        {
            Assert.Contains("\"failedUnits\":{\"type\":[\"object\",\"null\"]", WorkflowStepTypes.Find(type)!.Output, StringComparison.Ordinal);
            Assert.Contains("\"digest\":", WorkflowStepTypes.Find(type)!.Output, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("failedUnits", WorkflowStepTypes.Find("check-mutations")!.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// The workflows topic says what a red leg's entry carries of the errors its phase printed: digest, said for a
    /// model - the first error whole with its stack rendered to its frames in the tree, the next distinct to four as
    /// one line each, the rest counted by code and by file - and every line reaching a model once: the lines no parser
    /// held said in the digest with how many it left out and no tail where it holds a problem, the tail whole and only
    /// those the tail does not hold where it holds none; the ledger's --json keeps the whole digest typed, its bounds said, a red leg's
    /// first error is said on {run.red}'s line and in a block below the text ledger's table, and the entry names the log
    /// its digest's ranges number.
    /// </summary>
    [Fact]
    public void WorkflowsTopic_SaysWhatARedLegsEntryCarriesOfItsErrors()
    {
        var prose = Prose.Words(HelpText.Of("workflows").StandardOutput);

        Assert.Contains(
            "Each red leg's entry also carries, in digest, what the phase that did not pass printed, typed: the first error whole - its "
                + "stack rendered to the frames inside the tree, its log's line range one read away - the next distinct errors to four as "
                + "one line each, every test failure of the four with its stack, and the rest - what the four do not say - counted by "
                + "code and by file, a count of none and an empty list left out; every line reaching a model once and none out of its reach: where the digest "
                + "holds an error or a failure and the phase's log is one read away, it says the lines no parser held - the last 50, "
                + "the nearest the failure, with how many it left out - and the entry's tail holds none, and otherwise - it holds "
                + "neither, or no log of the phase is on this machine - the tail holds the phase's last lines whole and the digest says "
                + "those lines no parser held that the tail does not; and in log, the log that phase wrote, whose lines the "
                + "digest's ranges number as the file numbers them - the copy kept in the step's directory in an agent's worktree, "
                + "else the run's own, a leg's a host ran brought back by its run as the leg ended - null only where a host's could "
                + "not be brought back; the digest of a leg a host ran, made there, travels on its "
                + "line. The ledger's --json keeps the digest typed for a reader that asks, its bounds said - the first 50 diagnostics "
                + "of each severity, the first 50 failures and the last 50 lines no parser held, the rest counted, a stack held to its "
                + "first 48 and last 16 frames with the cut counted - the text ledger says each red leg's first error in a block below "
                + "its table, and {run.red} says it on its line. Where the run keeps a codebase graph of the tree the step's legs ran "
                + "in, each red leg's digest is joined to it before the step's ledger is kept: each place inside the tree carries the "
                + "declaration the graph holds enclosing it - its kind, its qualified name, its handle - the first error's as its "
                + "symbol and each frame of a stack inside the tree with it in brackets after its line, where its file holds what its "
                + "leg built, as the graph is asked and once it has answered: a leg in a copy built the tree as the copy was made of "
                + "it, a leg in the tree itself the tree as it held it when the leg ended, and a place whose file was written since - "
                + "a fold landing while the legs worked - carries none; a plain build, test or run "
                + "command asks no graph, and its digest's symbols are null",
            prose,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The workflows topic says what the fold family's steps do - read their dry run's standing, ask with what it names,
    /// hold the apply to it and a fold to its ledger and its within, take others in turn - and the orchestrators topic
    /// names the files they keep, each from the code's own names.
    /// </summary>
    [Fact]
    public void TheHelp_SaysWhatTheFoldFamilysStepsDo_AndTheFilesTheyKeep()
    {
        var workflows = Prose.Words(HelpText.Of("workflows").StandardOutput);
        var orchestrators = Prose.Words(HelpText.Of("orchestrators").StandardOutput);

        Assert.Contains(
            "fold, delete-agent, refresh and rebase make their verb's dry run first and read what it stood, typed, never its "
                + "code: sound, they apply; a run holding the tree the apply takes is waited out, as a held "
                + $"lock is ({HarnessExit.Refused} after the retries), and one holding only a copy a deletion removes is run again, as an "
                + $"incomplete step is ({HarnessExit.Incomplete} after them);",
            workflows,
            StringComparison.Ordinal);

        // Only a fold and a deletion hand their verb what the dry run named: a refresh's and a rebase's requests take none.
        Assert.Contains(
            "A fold's apply, and a deletion's, is held to exactly what its dry run named: measured again under its hold and found "
                + "to write anything else - the trees moved between the two - it writes nothing; an apply of any of the four refused "
                + "under its hold so is weighed again, pass after pass, until it applies, or fails with what it said once its trees "
                + "come back to what a pass weighed before. A fold, and a deletion that "
                + $"folds, write each path they mean to write, with what the main tree holds there, to the run's ledger ({OrchestratorLayout.FoldsFileName}) "
                + "before the verb is called, and what they left after, written by the verb under the main tree's lock it wrote "
                + "under, before it lets that lock go",
            workflows,
            StringComparison.Ordinal);

        // What a fold left is recorded through the intent its request carries; the one read of the tree after is marked.
        Assert.NotNull(typeof(FoldAgentRequest).GetProperty(nameof(FoldAgentRequest.Intent)));
        Assert.NotNull(typeof(DeleteAgentRequest).GetProperty(nameof(DeleteAgentRequest.Intent)));
        Assert.Contains(
            "The one limit is a fold that dies inside its own hold, part way through its write: the next attempt reads what it left "
                + $"from the tree, under that lock, and marks it {JsonNamingPolicy.CamelCase.ConvertName(nameof(FlowFoldEntry.ReadFromTree))}",
            workflows,
            StringComparison.Ordinal);
        Assert.Contains(
            $"an intent an earlier release left unsettled, which does not say {JsonNamingPolicy.CamelCase.ConvertName(nameof(FlowFoldEntry.RecordsWrite))} - "
                + "its verb recorded nothing of its write - is read so too",
            workflows,
            StringComparison.Ordinal);
        Assert.Null(typeof(RefreshAgentRequest).GetProperty(nameof(FoldAgentRequest.Expected)));
        Assert.Null(typeof(RebaseAgentRequest).GetProperty(nameof(FoldAgentRequest.Expected)));
        Assert.NotNull(typeof(DeleteAgentRequest).GetProperty(nameof(FoldAgentRequest.Expected)));

        // Whether a fold is committed is the run's commits' and the main tree's to say, whoever committed it.
        Assert.Contains(
            "a run stopped with a fold in its ledger whose change no commit took yet - a path it changed that no commit step of the "
                + "run took, that git does not ignore and that the main tree still holds otherwise than its HEAD, whoever commits it - "
                + "is unfinished, as one with an agent open is. A deletion run again "
                + "once its agent is deleted - its ending cut off before it was recorded - is done, with what its directory kept of its apply.",
            workflows,
            StringComparison.Ordinal);
        Assert.Contains(
            $"recorded in the step's directory ({OrchestratorLayout.StepTargetsDirectoryName}/<agent>/), one closed before its turn "
                + "passed over; a target that fails ends its own turn, and the step ends done, or failed with the code of the first "
                + "that failed, once each has had its turn. Each step keeps what its dry run named and how its apply ended in its "
                + $"directory ({OrchestratorLayout.StepFoldFileName}).",
            workflows,
            StringComparison.Ordinal);
        // What a commit step does, held to the codes it ends with, the file it writes and the types that are checks.
        Assert.Contains(
            "commit commits the folds upstream of it in the flow - every visit and iteration of them, never another work item's - "
                + "and no other work: each path one of them changed that no commit of the run took yet, holding what the newest fold "
                + "that names it left there, so a registry two items' folds wrote is taken whole. A fold upstream whose intent "
                + "nobody settled - a crash cut it off, and its step has not run again - leaves what it wrote untold, and fails the "
                + $"step ({HarnessExit.Refused}) naming it, nothing staged. With none owed it is done, "
                + "committed false, and runs no git at all.",
            workflows,
            StringComparison.Ordinal);
        Assert.Contains(
            "Otherwise it works on the branch the run started on alone: the main checkout on another - a person checked one out "
                + $"while the run was paused - or its HEAD detached fails it ({HarnessExit.Refused}) naming both, nothing staged, as it "
                + "does push and pull-request. The index must be empty and each path hold what its fold left there, and where either "
                + $"does not - a path edited and not committed, or its fold's change undone - the step fails ({HarnessExit.Refused}) naming each, nothing staged",
            workflows,
            StringComparison.Ordinal);
        Assert.Contains(
            "told by the blob git would store against HEAD's and never by git's status, which does not look at a path marked "
                + "assume-unchanged or skip-worktree - was committed by hand, and is marked taken by HEAD; one left to stage that the "
                + $"index marks so fails it ({HarnessExit.Refused}) naming each with its mark, since git add would not stage it.",
            workflows,
            StringComparison.Ordinal);
        Assert.Contains(
            "The commit is held to the set over the commit the branch named before it: one that stands on anything but that commit "
                + "alone - a hook moved the branch to a commit of its own, its parent carrying anything - took more against it - a "
                + "hook staged another file - or left a path of the set out fails the step "
                + $"({HarnessExit.CommandFailed}) naming each, and is left as it is "
                + "for a person to keep or undo, the set staged in the ledger beside it. A set an attempt before left staged is "
                + "settled first: committed where HEAD moved and holds each of its paths as the set staged it, else unstaged and "
                + "staged again whole. It answers committed, sha, paths and ignored - each path its folds changed that git ignores, "
                + "folded and never committed, which what the run's end leaves for a person names too",
            workflows,
            StringComparison.Ordinal);
        // A commit's hooks run in the person's own locale, as every command git runs hooks for does, each named.
        Assert.Contains(
            "git's commit and the repository's commit hooks run inside the operating system's isolation of the agents' work "
                + "('help agent-clients'), in the person's own locale, as every git command git runs hooks for does (am, commit, "
                + "merge, pull, rebase, cherry-pick, revert, checkout, switch, clone, worktree add, push, receive-pack, send-email, "
                + "gc, p4 submit) while every other git of the tool's runs under LC_ALL=C, so a hook behaves as in their shell;",
            workflows,
            StringComparison.Ordinal);
        Assert.Contains(
            $"else the step's own message - none, or a blank one, fails it ({HarnessExit.ConfigInvalid}) - and the rest is written to the ledger",
            workflows,
            StringComparison.Ordinal);
        Assert.Contains(
            $"the message written to the step's directory ({OrchestratorLayout.StepCommitMessageFileName}), with -s where commit.signOff - "
                + $"and marked committed with each fold path it took; git refusing, a hook among the reasons, unstages it and fails the step ({HarnessExit.CommandFailed}).",
            workflows,
            StringComparison.Ordinal);
        Assert.Contains(
            $"The checks are the steps of type {string.Join(", ", WorkflowStepTypes.All.Where(type => type.Checks).Select(type => type.Type))} and a reason step marked independent, whatever each is named.",
            workflows,
            StringComparison.Ordinal);
        Assert.Contains($"{OrchestratorLayout.FoldsFileName} the ledger of the run's folds:", orchestrators, StringComparison.Ordinal);
        Assert.Contains("and each commit a commit step staged of them and what became of it", orchestrators, StringComparison.Ordinal);
        Assert.Contains($"<step>/{OrchestratorLayout.StepCommitMessageFileName} a commit step: the message it commits with", orchestrators, StringComparison.Ordinal);
        Assert.Contains($"<step>/{OrchestratorLayout.StepPullRequestBodyFileName} a pull-request step that opened one: the body it opened it with", orchestrators, StringComparison.Ordinal);

        // What push and pull-request do, held to the codes they end with, the file one writes and the closed list of
        // commands the publishing steps run, each with its own limit.
        Assert.Contains(
            "{run.leftover}, {run.assumptions} and {run.preExisting} - what the run's loops left, what its agents assumed (each "
                + "'<step>' assumed about <what>: <assumption> (<why>), joined by '; ') and the issues it found there before - say 'none' alike where they name nothing, "
                + "as do {run.closure}, {run.claims}, {run.hotspots}, {run.reopened} and {run.counts}: of each item, the class record "
                + "its newest class-closure check that ended done judged, whole, and each class's claim alone, the hotspots that check "
                + "named, the classes its reviews reopened with how many rounds, as its newest routing counted them, and its counts - "
                + "the rounds of review that routed it, each routing that ended done one round, its home audits, and of each class of "
                + "that record the sites consolidated into its home (reads-home) and those deleted, the cells its pin covers "
                + "(cellsPinned), the neighbours measured and those not, and how many rounds a review sent it back - a step known by "
                + "its type, so no other step's closure member is read as an item's record, and an item no check judged has none.",
            workflows,
            StringComparison.Ordinal);
        Assert.Equal(WorkflowExpressions.RunTexts.Order(StringComparer.Ordinal), ["assumptions", "claims", "closure", "counts", "hotspots", "leftover", "notRun", "preExisting", "ran", "red", "reopened"]);
        Assert.Contains(
            "push pushes the commit the run's commit steps recorded last, else the one the run started on - never what the branch "
                + "names by then, which a hook or a build working in the main checkout could have moved - to the branch of the name "
                + "the main checkout is on, on a remote - the step's remote, else "
                + "the one the run's branch goes to (the push step's, else the one the branch tracks, else origin, else the only one) - "
                + "as a fast-forward and never forced, and answers pushed",
            workflows,
            StringComparison.Ordinal);
        Assert.Contains(
            "Every push step that names a remote names the same one, the remote the run's branch goes to: a definition whose push "
                + "steps name two is refused. HEAD detached, the main checkout on another branch than the run started on, a run that "
                + "made no commit and started on none, no remote it can name and one the repository does not have each refuse it",
            workflows,
            StringComparison.Ordinal);
        Assert.Contains(
            $"each refuse it ({HarnessExit.Refused}), nothing pushed; the remote refusing the push - its branch moved on since - fails it "
                + $"({HarnessExit.CommandFailed}) in git's words. A checkpoint push runs only where the workflow says pushCheckpoints, and is skipped otherwise.",
            workflows,
            StringComparison.Ordinal);
        Assert.Contains(
            $"written to the step's directory ({OrchestratorLayout.StepPullRequestBodyFileName}) for gh to read. One already open from "
                + "that branch to that base is answered as it is, created false, and never edited: a step run again opens no second. "
                + "One open from a fork's branch of the same name is none of the run's, as gh says of each (isCrossRepository).",
            workflows,
            StringComparison.Ordinal);
        Assert.Contains(
            $"A title blank once filled fails it ({HarnessExit.ConfigInvalid}), as a body that cannot be read or reads nothing does; "
                + "HEAD detached, the main checkout on another branch than the run started on, no remote, no base it can tell and a "
                + $"base that is its own branch each refuse it ({HarnessExit.Refused}), nothing opened; gh not there fails it "
                + $"{HarnessExit.ToolMissing}, and gh saying no {HarnessExit.CommandFailed}.",
            workflows,
            StringComparison.Ordinal);
        Assert.Contains(
            "each one it opened or found is left for a person, by its address, in what the run's end says (leftForAPerson), "
                + "as is each path its folds changed that git ignores, never committed.",
            workflows,
            StringComparison.Ordinal);
        Assert.Contains(
            "The commands commit, push and pull-request run are a closed list - git --literal-pathspecs add -- (15 minutes); "
                + "git --literal-pathspecs rm --cached --ignore-unmatch -q -- (15 minutes); git --literal-pathspecs reset -q -- (15 minutes); "
                + "git commit --cleanup=whitespace -F (15 minutes); git push --porcelain (5 minutes); "
                + "gh pr list --state open --json number,url,isCrossRepository (5 minutes); gh pr create (5 minutes) - each given the step's "
                + $"timeoutMinutes where it says one, else its own; one past it is not done ({HarnessExit.Incomplete}), and the step runs "
                + "again, a commit's set left staged settled by its next attempt.",
            workflows,
            StringComparison.Ordinal);
        Assert.Contains($"<step>/{OrchestratorLayout.StepFoldFileName} a fold, delete-agent, refresh or rebase step:", orchestrators, StringComparison.Ordinal);
        Assert.Contains($"<step>/{OrchestratorLayout.StepTargetsDirectoryName}/<agent>/ a refresh or a rebase of others:", orchestrators, StringComparison.Ordinal);
        Assert.Contains($"<step>/{OrchestratorLayout.StepGraphFileName} a reason step that has a codebase graph: the line of its prompt - what its pack cost, or why none was given", orchestrators, StringComparison.Ordinal);
        Assert.Contains("one line for each call of the graph tool, and one for each escape of its narrowing of reads and each whole read that narrowing answered with the file's outline", orchestrators, StringComparison.Ordinal);
        Assert.Contains($"<step>/{OrchestratorLayout.StepWorkingSetFileName} a reason step that has a codebase graph: the nodes it edited, served, read and found", orchestrators, StringComparison.Ordinal);
    }

    /// <summary>
    /// The help, the command's own help, the README and the architecture say what a run's own file input is: a file
    /// inside the repository by its path from the root or an absolute one inside it, committed or not, decided as an
    /// agent's read of it is - refused with no run started where it names none, where it or a link on its way leads out
    /// of the repository, into a credential slot, a place the user keeps credentials in or a Claude Code directory, or where
    /// the configuration's restrictions refuse its read, and read through the file as it was opened - copied into the run's
    /// inputs as it starts, and read there - its copy's path - by every step of every session and every agent of the run,
    /// whatever became of the file since; the orchestrators topic and the layout name the place.
    /// </summary>
    [Fact]
    public async Task TheHelpAndTheDocuments_SayARunsFileInputIsCopiedAsItStarts_AndReadThereByEveryStepAndAgent()
    {
        Assert.Contains(
            "and an input of the kind file naming a file inside the repository by its path from the root or an absolute one "
                + $"inside it, committed or not, which the run copies into its own {HarnessLayout.OrchestratorInputsDirectoryName}/<input>/ as it starts, decided as an agent's "
                + "read of it is (one that names none, or that leads out of the repository, into a credential slot, a place the user "
                + "keeps credentials in - agentPolicy.hiddenPlaces among them - or a Claude Code directory, by its own path or a link on "
                + $"its way, or whose read agentPolicy.restrictions refuse, is refused, {HarnessExit.Refused}, no run started) and read through the file as it was opened, so a link put on its way "
                + "since reaches nothing: every step of every session reads that copy for it - {input.<name>} the copy's path - "
                + "whatever became of the file since, and every agent of the run reads it, wherever its own tree was cut from;",
            Prose.Words(HelpText.Of("workflows").StandardOutput),
            StringComparison.Ordinal);
        Assert.Contains(
            $"{HarnessLayout.OrchestratorInputsDirectoryName}/<input>/ the file one of the run's own file inputs named, copied as the run starts - committed or "
                + "not - and kept for its life: what every step of every session reads for that input, whatever became of the file "
                + "since, and every agent of the run reads; taken away with the run's directory",
            Prose.Words(HelpText.Of("orchestrators").StandardOutput),
            StringComparison.Ordinal);
        Assert.Contains(
            "a file input names a file inside the repository by its path from the root or an absolute one inside it, decided as "
                + "an agent's read of it is, which the run copies as it starts.",
            Prose.Words((await CliRunner.RunAsync([FlowService.RunCommand, "--help"], TestContext.Current.CancellationToken)).StandardOutput),
            StringComparison.Ordinal);
        Assert.Contains(
            "an input of the kind `file` names a file inside the repository by its path from the root, or an absolute one inside "
                + $"it - committed or not, decided as an agent's read of it is: refused ({HarnessExit.Refused}) with nothing made where it names none, "
                + "where it or a link on its way leads out of the repository, into a credential slot, a place the user keeps credentials "
                + "in (`agentPolicy.hiddenPlaces` among them) or a Claude Code directory, or where `agentPolicy.restrictions` refuse its "
                + "read, and read through the file "
                + $"as it was opened - which the run copies into its own `{HarnessLayout.OrchestratorInputsDirectoryName}/<input>/` as it starts",
            Prose.Document("README.md"),
            StringComparison.Ordinal);
        Assert.Contains(
            "every step of every session reads that copy for the input, whatever became of the file since, and every agent of "
                + "the run reads it.",
            Prose.Document("README.md"),
            StringComparison.Ordinal);
        Assert.Contains(
            "opens the file each of the run's own inputs of the kind `file` names where its value, given or its default, is not "
                + "empty - a path from the main checkout's root or an absolute one inside it, read from the main checkout as it "
                + "stands, committed or not, and decided as an agent's read of it is, since every agent of the run reads the copy - on "
                + "the ground the one builder of an agent's ground builds for it "
                + $"(`{nameof(DssHarness.Core.Agents.Policy.AgentStepGround)}.{nameof(DssHarness.Core.Agents.Policy.AgentStepGround.OfFileInputs)}`: an agent's read of the main checkout "
                + "under the configuration the run starts under, never a ground built by hand beside it): held to the main checkout by "
                + "where it leads, every link on its way followed - "
                + $"the file's own and a directory's above it (`{nameof(DssHarness.Core.Agents.Runtime.ConfinedPaths)}`) - kept from every place no agent's read "
                + "reaches - every credential slot of the tool, every Claude Code directory and every place the user keeps credentials "
                + "in, `agentPolicy.hiddenPlaces` among them - and from every read `agentPolicy.restrictions` refuse "
                + $"(`{nameof(DssHarness.Core.Agents.Policy.AgentPolicy)}`), and read through the file as it was opened, where it stands decided again before a byte "
                + $"passes (`{nameof(DssHarness.Core.Agents.Runtime.AgentFiles)}.{nameof(DssHarness.Core.Agents.Runtime.AgentFiles.OpenDecided)}`), so a link put on its way since reaches nothing; anything else "
                + $"refused ({HarnessExit.Refused}), naming the input and the path, no run started (`{nameof(FlowInputs)}.{nameof(FlowInputs.OpenFiles)}`) - makes the run's "
                + $"directory and copies each file into it, `{HarnessLayout.OrchestratorInputsDirectoryName}/<input>/<the file's name>` (`{nameof(OrchestratorLayout)}.{nameof(OrchestratorLayout.InputCopy)}`), "
                + "before it makes the run's record, its status and its owner",
            Prose.Document("docs", "architecture.md"),
            StringComparison.Ordinal);
        Assert.Contains(
            "What a step reads for such an input - `{input.<name>}` in every prompt and every expression, and a pull request's "
                + $"base - is the copy's full path (`{nameof(FlowInputs)}.{nameof(FlowInputs.AsRead)}`, the one owner of what a step reads for an input, "
                + "which every session hands the engine), so every step of every session, a resume's included, reads the file the run "
                + "was started with, whatever the person did to it since; the run's records keep the value as it was given. That the run "
                + $"keeps its copies is a fact of its own, recorded as it is made (`{nameof(FlowRunContext)}.{nameof(FlowRunContext.InputCopies)}`, `flow.json`'s "
                + "`inputCopies`) and read back by every session: a run an older build made kept none and its `flow.json` says nothing of "
                + "them, so its steps read each file input as it was given, as that build handed it to them, never the path of a copy that "
                + $"was never made. Every agent of the run reads the run's `{HarnessLayout.OrchestratorInputsDirectoryName}/` as a root of its own (`{nameof(DssHarness.Core.Agents.Policy.AgentStepGround)}`)",
            Prose.Document("docs", "architecture.md"),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The README and the architecture say that a start whose file input's copy cannot be made is refused, naming the
    /// input, with what the start had made of the run taken back, so no directory is left holding no run's record.
    /// </summary>
    [Fact]
    public void TheDocuments_SayAStartWhoseFileInputsCopyCannotBeMade_IsRefused_WhatItMadeTakenBack()
    {
        Assert.Contains(
            $"which the run copies into its own `{HarnessLayout.OrchestratorInputsDirectoryName}/<input>/` as it starts, a copy that cannot be made refusing the start "
                + $"({HarnessExit.Refused}) as well, what the start had made of the run taken back",
            Prose.Document("README.md"),
            StringComparison.Ordinal);
        Assert.Contains(
            "The file's bytes are first read there: a place of the run or a copy that cannot be made - a file on a share gone, a "
                + "locked range, a full disk - takes back what the start had made of the run, or says what it could not, and refuses "
                + $"the start ({HarnessExit.Refused}), naming the input and why and that no run was started (`{nameof(FlowInputFiles)}.{nameof(FlowInputFiles.Keep)}`, "
                + $"`{nameof(FlowSessions)}.{nameof(FlowSessions.CreateAsync)}`), so no directory is left holding no run's record, which a start of "
                + "that name would be refused over.",
            Prose.Document("docs", "architecture.md"),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The help, the input's name key, the README and the architecture say that a workflow two of whose inputs differ by
    /// case alone is refused, on every system alike, since a run keeps a file input's copy in a directory of the input's
    /// name and a file system that folds case holds one for both.
    /// </summary>
    [Fact]
    public void TheHelpAndTheDocuments_SayInputsWhoseNamesDifferByCaseAlone_AreRefused()
    {
        Assert.Contains(
            "A workflow declares inputs - the values a run is started with, each with a kind, a default and the options it takes, "
                + "two whose names differ by case alone refused on every system, since a file system that folds case holds one "
                + $"{HarnessLayout.OrchestratorInputsDirectoryName}/<input>/ for both, and an input of the kind file",
            Prose.Words(HelpText.Of("workflows").StandardOutput),
            StringComparison.Ordinal);
        Assert.Equal(
            "the input's name, as --input name=value spells it, apart from every other input's in more than case",
            WorkflowKeys.Input.Single(key => key.Name == "name").Meaning);
        Assert.Contains(
            $"(a workflow two of whose inputs differ by case alone is refused where it is read, on every system, since a file system that folds case holds one such directory for both)",
            Prose.Document("README.md"),
            StringComparison.Ordinal);
        Assert.Contains(
            "A definition declares `inputs` - the values a run is started with, each with a kind, a default and the options it "
                + "takes, two whose names differ by case alone refused on every system, since a run keeps a file input's copy in a "
                + "directory of the input's name and a file system that folds case holds one for both - `params`,",
            Prose.Document("docs", "architecture.md"),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The README and the architecture say what a reason step holds of the tree of the agent it works in - write, whatever
    /// its access, since the start of a step that works as an agent that only reads resets that tree and its work writes it,
    /// from the moment its own agent is named - and that of two steps that go on with one step's work, two visits of one
    /// that goes on with its own among them, the second starts once the first has ended or waits on a question, which holds
    /// nothing, while an advice holds the tree it works in - within the recovery, only visits for the same failure or issue
    /// share one agent, so visits for different issues run side by side; the step types' table says it in the claim it
    /// carries.
    /// </summary>
    [Fact]
    public void TheDocuments_SayAReasonStepHoldsWriteOnTheTreeOfTheAgentItWorksIn_WhateverItsAccess()
    {
        Assert.Equal(["read main", "write the tree of the agent it works in"], WorkflowStepTypes.Find("reason")!.ClaimsOf);
        Assert.Contains(
            "no two steps work in one agent's tree at once, for a reason step holds write on the tree of the agent it works in "
                + "whatever its access, and an advice of the run's advisor holds the tree it works in while it runs - of two that go "
                + "on with one step's work, two visits of one that goes on with its own among them, the second starts once the first "
                + "has ended, or waits on a question with no advice on it still running, for a step that waits holds nothing (the recovery's "
                + "visits go on with one another's work only for the same failure or issue: a visit for another issue diagnoses in an agent "
                + "of its own, beside it):",
            Prose.Document("README.md"),
            StringComparison.Ordinal);
        Assert.Contains(
            "A reason step holds write on the tree of the agent it works in whatever its access "
                + $"(`{nameof(FlowClaims)}.{nameof(FlowClaims.Of)}`, read through the step whose work it goes on with, `{nameof(FlowCoordinator)}.WorkingOf`): the start of a step "
                + "that works as an agent that only reads resets that tree and its work writes it, so of two steps that go on with one "
                + "reading step's work the second starts once the first has ended - two visits of one that goes on with its own, which "
                + "work in the agent its first visit made, claiming that agent by the declaration before either is given it "
                + $"(`{nameof(FlowCoordinator)}.ClaimedAgentOf`); within the recovery, by the declaration and the thing its visit puts right "
                + $"(`{nameof(FlowInstance)}.{nameof(FlowInstance.RecoveryThread)}`, `RecoveryThreadOf`), so visits for different issues each make an agent of their own "
                + "and diagnose side by side, each within the run's limits - and no step's start discards another's scratch under it. A reading step "
                + "that begins work of its own is given its agent as it starts, and holds write on that agent's tree from then "
                + $"(`{nameof(FlowCoordinator)}.ClaimsOf`, read again once `Start` names it), so what its attempt keeps held past its end names "
                + "the tree too. A step that waits on a question holds nothing, so the second may start while the first waits - and the "
                + "first's going on brings the tree there again; an advice of the run's advisor holds read on the tree it works in until "
                + $"its runner ends (`{nameof(FlowClaims)}.{nameof(FlowClaims.OfAdvice)}`, the tree named as the asking step's own claims name it, "
                + $"`{nameof(FlowCoordinator)}.AdvisedTreeOf`), so no step's start brings that tree elsewhere under the advice.",
            Prose.Document("docs", "architecture.md"),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The README, the help and the architecture say what an answer that goes on with an attempt finds of the tree of an
    /// agent that only reads: the tree as its attempt left it, unless another step's start brought it elsewhere while the
    /// attempt waited, or nothing tells who it was last brought for - then it is brought there again, and its agent told.
    /// </summary>
    [Fact]
    public void TheDocuments_SayAnAnswerThatGoesOnFindsItsTreeBroughtAgain_WhereAnotherStepBroughtItElsewhere()
    {
        Assert.Contains(
            "an answer that goes on with an attempt finds the tree as the attempt left it, unless another step working as the agent "
                + "brought it elsewhere while the attempt waited, or nothing tells who it was last brought for: it is then brought there "
                + "again as the attempt goes on, and its agent told that what it wrote there is gone;",
            Prose.Document("README.md"),
            StringComparison.Ordinal);
        Assert.Contains(
            "An attempt that goes on with an answer in its own conversation - a person's, a limit's `more` or `compact` - is the same "
                + "start going on, and finds the tree as its attempt left it, save where the record names another attempt - another step "
                + "working as the agent started while this one waited, for a step that waits on a question holds no claim - or none, as a "
                + "record an older tool wrote does: the tree is then brought there again as a start brings it, and the agent is told so at "
                + "the head of the message it goes on with, where the tree stands and that what it wrote there is gone "
                + $"(`{nameof(ReasonStepRunner)}.{nameof(ReasonStepRunner.TreeBroughtAgain)}`).",
            Prose.Document("docs", "architecture.md"),
            StringComparison.Ordinal);
        Assert.Contains(
            $"step attempt it was last brought for as `readiedFor` (`{nameof(AgentRecord)}.{nameof(AgentRecord.ReadiedFor)}`: the instance and the attempt,",
            Prose.Document("docs", "architecture.md"),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The workflows topic carries its worked examples - the two ways a step is raised by another step's ending, and
    /// the loops a repository can build its own work around - each printed as a whole workflow and each holding as it
    /// is printed, read by the same reader that reads a workflow's own file and checked by the same check.
    /// </summary>
    [Fact]
    public void TheWorkflowsTopic_CarriesItsWorkedExamples_EachHoldingAsPrinted()
    {
        var topic = HelpText.Of("workflows").StandardOutput;

        Assert.Contains(
            "Worked examples, each a whole workflow as a file holds it: the two ways a step is raised by another step's ending - a repair that goes back to what failed, and a check that runs whenever a builder ends - and two loops a repository can build its own work around. Each holds as it is printed, read by the same reader that reads a workflow's own file. "
                + "Each names the advisor init writes beside the shipped workflow, as every workflow a run is started of names one: every question its steps raise is recommended before it is asked.",
            Prose.Words(topic),
            StringComparison.Ordinal);

        // Each example is opened by a comment naming it and ends at the first line the file's own text takes; what
        // the topic prints is what the reader reads.
        var examples = new Dictionary<string, string>(StringComparer.Ordinal);
        string? open = null;
        var held = new List<string>();

        foreach (var line in topic.Split(["\r\n", "\n"], StringSplitOptions.None))
        {
            if (line.StartsWith("  # example: ", StringComparison.Ordinal))
            {
                if (open is not null)
                {
                    examples[open] = string.Join('\n', held);
                }

                open = line["  # example: ".Length..].Trim();
                held = [];
            }
            else if (open is not null && line.StartsWith("  ", StringComparison.Ordinal))
            {
                held.Add(line[2..]);
            }
            else if (open is not null && held.Count > 0)
            {
                examples[open] = string.Join('\n', held);
                open = null;
                held = [];
            }
        }

        Assert.Equal(["issues", "repair", "review", "smoke"], [.. examples.Keys.Order(StringComparer.Ordinal)]);

        // Each is read and then checked as validate-workflow checks a definition - every rule the yml alone can break; the
        // files it names are not printed, so none is read - and holds: an example that says a key the check refuses is
        // no example to copy.
        foreach (var (slug, yaml) in examples)
        {
            var definition = WorkflowFileParser.Parse($"workflows/example-{slug}/example-{slug}.yml", yaml + "\n");

            Assert.Equal($"example-{slug}", definition.Name);
            Assert.NotEmpty(definition.Steps);
            Assert.Equal(string.Empty, string.Join('\n', WorkflowValidator.Validate(definition, hasCommitTemplate: false)));
        }
    }

    /// <summary>
    /// The workflows topic says which check holds a round's items to the issues the run owes its next round, which checks
    /// take an item that settles one - every check whose items a round opens with, the plan's among them, and no
    /// recovery's - and what settles one - an item that ends done with a commit of its own made, never one a person
    /// skipped - and where a listed issue is shown.
    /// </summary>
    [Fact]
    public void TheWorkflowsTopic_SaysWhichCheckHoldsTheIssuesOwedTheNextRound_AndWhatSettlesOne()
    {
        Assert.Contains(
            "in next-round.json, which run.nextRound reads as the issues no work item has settled, and every check whose list the next "
                + "round opens with holds that list to: the check that opens a round - a validate-plan the forEach beside it expands from, "
                + "where that group runs in rounds, in an iteration of a repeat or as the group plan updates add a round to with no loop "
                + "around it - and the plan's while that round is the first, or the plan update under way, raised by the check that opens "
                + "it, joins it: each issue named in an item's settles, none left unsettled, the settling items listed and started first, built beside the rest, and the rounds going "
                + "on while one is left - never holding the round the issue came from. Each holds the answer of the reason step whose items "
                + "it reads to it within that step, so the planner and the self-audit settle an owed issue in their own conversation. Every "
                + "check whose items a round opens with - that one, and each whose items read what that one's items read: the plan's, so "
                + "the one a plan update sends the run back to - takes an item that settles one of those issues, and refuses one whose "
                + "settles names an issue the run does not owe; any other check - a recovery's - refuses every settles. A forEach item that "
                + "names an issue in settles settles "
                + "it once it ends done with a commit of its own made, whatever the run takes plan updates by, and next-round.json says "
                + "which item did; one that ends done with nothing committed - an item a person skipped - settles nothing, and the issue "
                + "stays owed. It lists the rest, proven, with a title and a description, in the run's pre-existing-issues.json, shown in "
                + "the pull request's body and the handoff.",
            Prose.Words(HelpText.Of("workflows").StandardOutput),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The workflows topic says what a workflow is, where it lives, what this build does with one and what it does not,
    /// and lists every key from the tables the reader itself reads - so what is listed is what is read.
    /// </summary>
    [Fact]
    public async Task TheWorkflowsTopic_SaysWhatAWorkflowIs_AndListsEveryKeyTheReaderTakes()
    {
        var result = HelpText.Of("workflows");


        var prose = Prose.Words(result.StandardOutput);

        Assert.Contains(
            "A workflow is a flow of typed steps that depend on one another by name, declared by name under workflows in config.json and defined by a yml file of its own:",
            prose,
            StringComparison.Ordinal);
        Assert.Contains(
            "This build checks a workflow's definition, with 'dssharness validate-workflow <name>', and starts and drives a run of one with "
                + "'dssharness run-orchestrator <name>' (see 'help orchestrators'). The steps served so far are orient, graphify-codebase, validate-plan, "
                + "create-agent, reason, build, test, run, check-mutations, anchor-balance, anchor-citations, class-closure, route-findings, ci-read, ci-wait, fold, delete-agent, "
                + "refresh, rebase, commit, push, pull-request, each below; a step of "
                + "any other type fails, saying no runner serves its type.",
            prose,
            StringComparison.Ordinal);
        Assert.Contains(
            "every path the yml names is relative to that directory and stays inside it, as an action's files are relative to its own directory",
            prose,
            StringComparison.Ordinal);
        Assert.Contains(
            "What the steps served so far do. orient reads the main checkout and the repository once, and answers: the branch (failed, refused, where HEAD is detached), baseBranch - the pull-request step's base, filled from the run's inputs and its name, else the default branch of the remote the run's branch goes to (the push step's remote, else the one the branch tracks, else origin, else the only one), null where there is none - startCommit, the commit the run started from;",
            prose,
            StringComparison.Ordinal);
        Assert.Contains(
            "validate-plan checks the work items its items yields and answers ok, problems and the items, never a failure: at least one item, and as many as the work needs - no count is refused, for a round runs its items in waves of the run's maxOpenAgents;",
            prose,
            StringComparison.Ordinal);
        Assert.Contains(
            "each thing it creates; and each item declaring the classes of the decisions it changes or saying in noClass why it "
                + "decides none, the classes held to what the tree and the item's own paths say of them: a class id used once in the "
                + "plan, each home and each site a path inside the tree as a path an item reuses is, the home among the item's own "
                + "paths and there in the tree where it exists, a site marked home exactly where it is the home by its path and "
                + "symbol, every other site - which changes, reading the home or deleted - the item's too - patching some of a "
                + "class's sites is never done - more than one site consolidated at the home, as one site that is not the home is, "
                + "each class searched by data its queries name again, its universe answering each axis in one of the two ways an axis "
                + "is answered - values with where they are declared and no why, or no value with why and nothing declared - and "
                + "counting the cells its axes that apply make - the "
                + "product of the counts of their values, 1 where none applies, the plan's schema holding in the step that answers it "
                + "that it answers every axis, in the one shape of an axis - and "
                + "one home belonging to one item, two classes of one item sharing one as they may; and each item naming the tests its change affects in "
                + "tests, one unit to an entry and none blank, or saying in noTests why it affects none - exactly one of the two.",
            prose,
            StringComparison.Ordinal);

        // A plan check's rules and a class closure's hold the answer they read within the step that gives it, the check
        // after it the record and the gate: said alike by the help and the README.
        var readme = Prose.Document("README.md");

        Assert.Contains(
            "fails, as a definition that does not hold does. Where its items read the output of a reason step of its own scope - by the "
                + "step's name, through latest of the work that step goes on with, or through previous in the iteration after - the same rules, "
                + "with what it would be handed then, hold that step's answer as it is given, and what they find goes back to the agent in its "
                + "own conversation until it answers clean or gives again what it was sent back with: a problem the planner can put right "
                + "costs no planning iteration, and the check, run after it as before, records and gates what is still wrong.",
            prose,
            StringComparison.Ordinal);

        foreach (var said in new[]
        {
            "the same rules, with what it would be handed then, hold that step's answer as it is given, and what they find goes back to the "
                + "agent in its own conversation until it answers clean or gives again what it was sent back with: a problem the planner can "
                + "put right costs no planning iteration, and the check, run after it as before, records and gates what is still wrong",
            "the same rules hold that step's answer as it is given, over the agent's work as it then stands, and what they find goes back to "
                + "the agent in its own conversation until it answers clean or gives again what it was sent back with,",
            "a missing home audit, and whatever only a later step's work shows - its tests, a fold - stay the check's, run after it as "
                + "before, the record and the gate",
        })
        {
            Assert.Contains(said, prose, StringComparison.Ordinal);
            Assert.Contains(said, readme, StringComparison.Ordinal);
        }
        Assert.Contains(
            "create-agent makes the agent the engine named for it - a<N>, one past the highest the run's agent directories and step records hold, never given twice - for the model of the first reason step that works in it, or the default tier's first model where none does, both written to the step's record before the agent is made.",
            prose,
            StringComparison.Ordinal);
        Assert.Contains(
            "reason works the step's agent: the one its in names or the work it goes on with (resume), or - where it names neither - the engine's own read-only agent, made before the step runs and deleted once no "
                + "step can go on with its work, its tree removed and nothing folded: a tree of its own, made as every agent's is, which the start of every step that works as it - each attempt of its first, and of each that goes on "
                + "with its work - brings to the main checkout's HEAD then, or to the commit the step's commit names - a template filled as the step starts, whose value names a commit of the repository or fails the step with "
                + "nothing changed, and a step that writes or works in an agent's worktree is refused one - whatever it wrote there before discarded, so the step reads exactly what that commit holds, starts in it, and may write "
                + "it and its work directory, never folded, and is served the graph's view of that tree; an answer that goes on with an attempt finds the tree as the attempt left it, "
                + "unless another step working as the agent brought it elsewhere while the attempt waited, or nothing tells who it was last brought for: it is then brought there again as the attempt goes on, and its agent told that what it wrote there is gone; "
                + "no two steps work in one agent's tree at once, for a reason step holds write on the tree of the agent it works in whatever its access, and an advice of the run's advisor holds the tree it works in while it runs - "
                + "of two that go on with one step's work, two visits of one that goes on with its own among them, the second starts once the first has ended, or waits on a question with no advice on it still running, for a step that waits holds nothing; "
                + "the step's model resolved with its agent definition below its own and its order built as entries of a model and one of its provider's keys, each entry's credential read from the slot "
                + "('help agent-clients'); its prompt file read and filled where the step starts, its writes given as an expression read there too - the names it yielded narrowing what it may write as a written-out list does - "
                + "and each skill it names looked up again and the folder handed over in the agent's work directory; the work it goes on with rendered as its continuation record, "
                + "and its going on by the client's kind - the stored conversation handed where the client goes on with one, a new launch rendered where it does not; and its answer, held to the schema its outputs names - one the engine reads, "
                + "refused otherwise where the workflow is checked and again before any client is asked - the step's output. Where the repository declares legs, a step whose agent runs commands (its capabilities hold run) is given the measure tool on every client: one call takes a leg, its "
                + "variant - the build configuration the leg's own configuration declares - a test filter and the cell of a universe being measured, runs those tests of that leg through the test service as a test step's legs are run, in the agent's worktree "
                + "where it has one - an agent that only reads measures its own tree, at the main checkout's HEAD or the commit its step names - and in a local copy of the main tree where it does not, under the agents' isolation and the step's own record of what its legs start, and answers what they showed - passed where the leg passed, failed where its "
                + "build or its tests failed, and unmeasured with why for anything else: a leg refused by another run's lock, skipped, stopped, a filter that matched no test, a leg not declared - the verdict as the ledger spells it, with the "
                + "run's directory and the call's own records (measures/<call id>/ of the step's directory: the ledger, and what the call answered, which is what class-closure reads of what it measured); the call is a tool call of "
                + "the step's stored stream, its id what a closure record's measuredBy cites. A failed "
                + "agent is tried again on its model - the step's retries, absent the workflow's maxRetries, waiting its backoffSeconds, absent the workflow's, twice as long before each later try - and then on each model of its order; a run that ended "
                + "on a key the provider refused moves to the next key at once, with no wait, and the run's pause ends the wait before a try and begins no "
                + "launch after one ended, the step handed off with no handoff, saying so; what its agents "
                + "assumed without asking is kept, read as the step's own assumptions fact and named in the run's. Where the step may ask (ask) its agent's questions pause it - askRounds times in one attempt, the answer or decide going on in the same "
                + "attempt as the agent's next message; a limit of its own (turns, budget, a truncated answer, a full context) asks, more offered only where a stored conversation of the attempt can be handed, and compact for a full context goes on "
                + "from the engine's reduction of it, the attempt's bounds counting still; every account at a limit, and a provider's service failing past the client's own tries, pause the run safely, and the resume runs that very step. What a "
                + "step spends is charged to the run's budget turn by turn as its agent works - every token of every kind, and its cost at the configuration's prices for the model serving it - and each launch is held to what is left of the "
                + "run's dollars before its reserve where that is less than its agent's own maxBudgetUsd, or where it names none: a launch that bound ended has reached the run's reserve, not the step's own budget, so the workflow's answers may "
                + "allow more and the step runs again given its handoff, or else the run pauses for its budget - and a launch whose share was reckoned before more was granted runs again given its handoff, its share reckoned anew. "
                + "A turn whose cost could not be counted leaves the run's dollars known only as at least what was counted: status, the report, flow.json and every --json say so, uncounted beside usd naming why; "
                + "in a run budgeted in dollars such a turn reaches the run's reserve for the grant that stands, so the run asks, and a grant of more lets it go on, each launch held to what is left as far as the dollars were counted. "
                + "A safe pause asks each reason step in flight for its handoff in its own conversation, all at once, and waits for the agent's answer within the "
                + "workflow's handoffSeconds; an agent that has not answered by then is ended. Where an "
                + "agent gave no handoff of its own - a limit ended its run, a crash cut the attempt off, its handoff turn passed its bound or failed - the engine writes one from the stored stream of the step, its first line saying why: the files the "
                + "attempt wrote, the last commands it ran and how each ended, its last messages word for word, and the steps before it that left notes, named by step.",
            prose,
            StringComparison.Ordinal);
        Assert.Contains(
            "its answer held to the engine's own schema - one of the question's answers, with a text where that answer takes one - bounded by its own definition alone "
                + "(maxTurns, maxBudgetUsd, maxMinutes), never by the run's budget or its reserve, so a question raised once the run is past its reserve is recommended as "
                + "any other is, and retried as every agent; "
                + "the question is open while the advice runs - an answer ends the asking, the question saying so, and the step that asked starts again only once the advice's runner has ended; "
                + "a pause never ends it - it finishes as a machine step the pause leaves running does, the session waiting for it, and its recommendation is kept, so a resume that "
                + "comes back to a pause pays for it once - and a stop ends it, one once the pause is complete among them, which starts no advice after it either, "
                + "the question saying the recommendation is sought at the resume, which seeks it, unless the advice it ended still returns its recommendation, "
                + "which the question keeps so the resume never pays for it again - "
                + "the recommendation written to the question with who gave it and where its records are, "
                + "and none obtained said so, a bar that could not be read among the reasons; what every advice spent, an ended one's among them, is counted against the run's budget "
                + "as it is taken, an advice that spends the run to its reserve pausing it at once, and one taken while the run pauses already asking no second pause; "
                + "and an advisor that obtains none - it failed past its retries among the reasons - leaves the question saying why, and the person is asked all the same. "
                + "A question raised while the run is pausing says its recommendation is deferred, and once the pause is complete - whatever asked for it - every open question "
                + "still owing its recommendation is advised before the session ends. "
                + "The budget's own question is recommended too, raised once the budget's pause is complete and advised before the session ends: the advisor is asked of the run as a whole - "
                + "its records under steps/run/advice/<n>/, {question.step} reading run, {question.record} the run's facts as ask-orchestrator-status gives them - held, as every "
                + "advice is, to its own definition's bounds alone, and what it spends is the run's own, kept in flow.json's "
                + "spent.advice and counted in the run's spend. Only an agent's own questions carry their recommendation within them and are not advised - a step's own agent's, "
                + "and a pause step's that shows an upstream step's questions - nor is one the run's input or its configuration answers, which no person is asked. "
                + "A person answers a question recommended: an answer to one still owing its recommendation that no advisor seeks now - between sessions, or while a pause defers it - "
                + "is refused (13), nothing answered, and abort is taken; resume-orchestrator seeks it first, and a pause once it is complete, unless a stop came after it. "
                + "The run's report, ask-orchestrator-status and every refusal of an answer name each open question's next step by one rule, over what the run's engine is doing: "
                + "for one still owing its recommendation, resume-orchestrator, which seeks it first, where no engine runs the run; its answer, taken now and ending the asking, "
                + "where a live engine's advisor seeks it or a live engine is starting, which seeks every recommendation the run owes; the pause's advice, then its answer, "
                + "where a live engine's pause defers it; and resume-orchestrator once the engine "
                + "has gone where it is stopping - abort taken now wherever one is owed - and answer-orchestrator for every other. "
                + "An answer handed over as a session starts, to a question owing the recommendation that start seeks, waits for it and is taken once it is sought, "
                + "ending the asking; only a start that seeks none for it, a stop or a pause asked as it began, refuses it - "
                + "answer-orchestrator's own answer so refused its refusal (13) with the run's why on every exit of the session, whatever the session "
                + "went on to do or met, the session's own end said after it and kept in --json's session (its summary and exitCode), and every other "
                + "said with what became of it - and a live engine's state says it is starting only while its start takes such an answer, until the "
                + "start's own end is written.",
            prose,
            StringComparison.Ordinal);
        Assert.Contains(
            "Every question the engine raises is recommended before a person is asked it, so every workflow a run is started of names an advisor - "
                + "validate-workflow and a run's start refuse one that names none, naming the block init writes as the fix, and its retired kinds is refused, "
                + "for every kind of question is recommended.",
            prose,
            StringComparison.Ordinal);
        Assert.Contains("params - its own knobs, defaulted in the file and read as param.<name>", prose, StringComparison.Ordinal);
        Assert.Contains(
            "A step gives a failure nothing handles to the workflow's recover by saying onFailure: recover, itself or through a group around it - never "
                + "a reasoning step, whose agent is tried again (retries, waiting backoffSeconds, twice as long each time) and on each model of its fallback, and which asks "
                + "where it still fails; where every account it could use is at its limit, or its provider's service fails past the client's own tries, the run pauses safely "
                + "instead and resumes that very step. The recovery's steps, written once, read the step that failed as source and see every step of the flow, find what "
                + "went wrong and put it right; then the run goes where its onSuccess says - failed-step, the step that gave it its failure, which runs again "
                + "from where it stopped - and a failure of its own work, past its steps' retries, does what its onFailure says: pause or stop; the recovery "
                + "never calls itself. A step that fails again goes to the recovery again by its own onFailure, until it passes, a person is asked, or maxVisits is met (absent, "
                + $"{FlowRecover.MostVisits} visits of one step's failures, or of one issue's, each counted on its own); past it the run asks (recoveryBound), with the advisor's recommendation of a fix: "
                + "more, one more recovery; direct, with the fix in words, for the next one; skip, where the step is skippable; or abort.",
            prose,
            StringComparison.Ordinal);

        // A type's required key is said to be required, from the one table the parser refuses its absence by.
        Assert.Contains("the prompt file the step is asked with; required", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("the predefined runner the step runs; required", result.StandardOutput, StringComparison.Ordinal);
        Assert.All(
            WorkflowStepTypes.All.SelectMany(type => type.Keys.Where(key => key.Required)),
            key => Assert.Contains($"{key.Meaning}; required", result.StandardOutput, StringComparison.Ordinal));

        // What init writes of the implementation workflow, and where: the one owner of that rule is InitService, held to it by
        // InitServiceTests, and this sentence says what those hold. The flow's own head and graph serving are said with it:
        // the build first, the graph after it, and each type of reason step's keys.
        Assert.Contains(
            "init writes one - the implementation workflow, from a request to a pull request whose checks are green - and declares it in "
                + "the config it creates. It builds first: the baseline is split in two - a build step, which fails red and is taken by the "
                + "recovery and built again - and the test step it was, which waits for the build - and the flow makes its codebase graph "
                + "after that build, beside the baseline's tests and the CI read, passed over where it cannot be made; every reason step "
                + "after it is served from the graph as the twenty-run measurement of 2026-11-11 set each type's value: the steps that work on "
                + "code keep the tool - the implementer and the pre-existing-issue triage say nothing and are given it "
                + "and their task's seeds by default, as the planner is, which the measurement left as it was, and the "
                + "reviewers, the self-audit, the diagnosis and the steps that verify and fix say graphFrom: changes - "
                + "the request triage, whose work is text the map does not light, asks none, the plan audit and the "
                + "item audit, the plan audit's agent on a later round's items, say graph: tools - the tool, for a "
                + "class's sites they search are what its sites answers, beside their own text search - the "
                + "prioritiser and a review's scope are given the map alone, and the scribe's steps, which write the "
                + "run's records, ask none of it; no step says graphPromptTokens, for a step with the tool is pushed no "
                + "map and no pack and asks the tool for them where it needs them. Where config.json declares "
                + $"{WorkflowTemplates.ShippedWorkflowName}, init writes each of its "
                + "files that is missing - the whole set where the repository has no definition of it yet, and where it has one only "
                + "the shipped files that definition names, the workflows it uses among them - a shipped one taken out whole read from "
                + "the copy init writes, so it comes back whole in one run - so a file taken out with its step stays out - and keeps each "
                + "one that is there, whatever it holds, so an init that stopped part way is "
                + "finished by the next - where a file is missing and the definition there does not read, or uses a workflow of the "
                + "repository's that does not resolve, init refuses (12, naming each problem; 13 where it "
                + "cannot be read), since which files it names cannot be told; nothing that exists is ever overwritten, and a config that "
                + "does not declare it gets none of it. A file such a definition names that this tool ships elsewhere now - one of the item "
                + "pipeline's, moved into its own folder - is not brought back, and init says so.",
            prose,
            StringComparison.Ordinal);

        // What an expression's reads are typed by, and what each operator, each key and the run's own standing take: the
        // statuses are the code's one list of them, so the sentence cannot name another.
        Assert.Contains(
            "every path it reads is typed by what holds it - a reason step's schema, the output a built-in type answers, the "
                + "engine's facts of a step, the run's own names - and every operator takes what it says: !, && and || a boolean, == and != "
                + "no list and no object, any a list and a test that yields a boolean, union lists. A when, an until and a blocking yield a boolean, and "
                + "a forEach and a key that takes a list a list; how the run stands without its always steps "
                + $"({string.Join(", ", WorkflowExpressions.RunStatuses.Select(status => $"run.{status}"))}) is read by an always step "
                + "alone. A placeholder in braces - '{step.member}' - in a template key reads the same names, typed the same way, as one in "
                + "a prompt does; one in any other key is refused, and a pull request's base reads nothing but the run's inputs and "
                + "run.name, the same for every pull request. A brace meant literally is written twice, {{ and }} alike, another "
                + "expander's ${...} is left as written, and a placeholder that names nothing is refused. Every template key of a step "
                + $"is filled where the step starts, and one that reads nothing fails it ({HarnessExit.ConfigInvalid}) before its runner is asked. "
                + "A prompt alone is filled further: a placeholder in it that names a step this run left no output - it was skipped, or "
                + "failed and was passed over - is filled with a sentence that says so and why, as (step 'ci' left no output: it failed "
                + "and was passed over: no run of the matrix was found), so the agent is told what it does not have; a member of a step "
                + "that did leave an output is not that case, and reads nothing as any other name does.",
            prose,
            StringComparison.Ordinal);

        // What the checks that run no legs do and answer, and what red does to them: each default and code is its owner's.
        Assert.Contains(
            "anchor-balance and anchor-citations run the checks of those names in the main checkout - the balance from its base, the "
                + $"citations of its subject ({string.Join(", ", AnchorCitationSubjects.Names)}; "
                + $"{AnchorCitationSubjects.Default} where it names none) - each held to its rows or its paths where it names them: the anchors "
                + "among those rows the change opened, closed and lost, a problem at a line of one of them and every problem of a registry as a "
                + "whole, or the citations in those files; a rows or paths expression that reads nothing names nothing. Each answers passed and "
                + "findings, each a line as the check prints it. class-closure runs before every fold of an item, judging the work of the agent "
                + "its in names: it judges the classes the item's plan declares against the closing record its builder answered - every planned "
                + "site accounted for and the planned home kept or its move said (homeMoved), a deleted site no longer declaring its symbol in "
                + "the worktree (the graph's view of it, synced where the step starts, or its text), a site that reads its home among the paths "
                + "the agent changed (what its fold would write and what its earlier folds of the run changed), every search a tool call of the "
                + "item's stored streams that ran, however its program exited - a search that found nothing exits 1 - and never one refused or "
                + "whose input did not read, one of whose input's texts holds its query, every neighbour measured by a measure call of the same "
                + "streams - whichever client called it, whatever it answered - whose own record (measures/<call id>/measure.json of the "
                + "directory of the step whose stream holds it) can be read, names the neighbour's cell and answered its result, failed for "
                + "failed-fixed - the newest call of that measurement, the same leg, variant, filter and cell, to measure anything answering "
                + "passed: the call cited or a later one for passed, one whose record was written after the failing call's for failed-fixed - "
                + "or reasoned why not, every test of the pin declared, the arm one of the registry its "
                + "worktree holds and mutating the home's own file or a reason the class has nothing to mutate, the universe holding every "
                + "axis the planned class's universe says applies and the claim naming each, the arms the builder's answer lists for the item's own sweep (the step's arms) "
                + "the arms the record names and no other, an id one arm however its case is written - and each home's touches since its "
                + "base, the item's own commits left out, a record "
                + "without a home audit refused where the home was touched by other work; a class's paths - the plan's and the record's, the "
                + "paths changed, an arm's site and the touches - are read as the plan check reads them, either separator, and compared as this "
                + "machine compares paths; a fold it cannot weigh fails it with the weighing's "
                + "own code. Its problems are the record's (1, which close-class repairs) apart from a missing home audit (2, which home-audit "
                + "makes - a home touched by other work, or a class the item's ledger says a review reopened a second time), and it answers "
                + "passed, problems, hotspots - the homes touched hotspot times or more - and touches. "
                + "The record's problems reach the builder sooner: where the record reads the output of a reason step of the check's own "
                + "scope - the builder's, or one that goes on with its work, through latest, close-class among them - the same rules hold that "
                + "step's answer as it is given, over the agent's work as it then stands, and what they find goes back to the agent in its own "
                + "conversation until it answers clean or gives again what it was sent back with, no close-class drawn for it; a missing home "
                + "audit, and whatever only a later step's work shows - its tests, a fold - stay the check's, run after it as before, the "
                + "record and the gate. "
                + "route-findings routes the review's findings by the class of each, keeping the item's ledger (closure-ledger.json in its "
                + "agent's directory) of the rounds that reopened each class, a round counted once by the routing that made it however often "
                + "that routing runs: every introduced finding - a defect the diff made - goes back in rework as itself, the reopened findings "
                + "of one class go back as one send-back naming the class (reopenedClass) and the rounds that reopened it (rounds), carrying "
                + "them all and graded by the worst of them, and a class reopened twice or more goes to homeAudit instead, for the next action "
                + "is the home audit; it answers rework, homeAudit and reopened, the rounds per class. A reopened finding names a class of the "
                + "item's - its classes' or its record's, exactly - and every finding is graded critical, high, medium, low, nit: each reviewer "
                + "whose answer it routes is held to that at its own answer and asked again where it does not hold, and an answer that still "
                + $"does not hold after its repairs fails the reviewer's step ({HarnessExit.CommandFailed}), as one its schema refuses does, "
                + $"nothing routed; findings that reach the route unheld fail it ({HarnessExit.ConfigInvalid}), nothing routed. "
                + "ci-read reads the legs check-ci-legs reads, of its branch - the one checked out "
                + "in the main checkout where it names none - and its newest limit runs, answering passed, branch, matrixRan, legs and red, each leg as "
                + "check-ci-legs --json gives it. ci-wait waits for every check run and commit status the forge lists for its commit - the "
                + $"commit the run last pushed where it names none, refused ({HarnessExit.Refused}) where it pushed none - less those its ignore "
                + "names - a job by its own name, as its workflow names it, a status by its context - written out or with * for any run of "
                + $"characters, reading them every pollSeconds ({CiChecksRequest.DefaultPollSeconds}): "
                + "a red one - one that concluded failure, a status of failure or error - ends it at once, answered with its name, url and, of a "
                + "job of a workflow run, the job, its workflow, the step that failed, its leg as check-ci-legs classifies it, whether it overran "
                + "its budget, and its whole log kept in the step's directory "
                + $"({OrchestratorLayout.StepLogsDirectoryName}/<job>.<id>{PhaseLogs.Extension}, the job's id telling two of one name apart) "
                + "given by reference in log, with how many lines it holds in lines and, in failing, the page of it where the job "
                + "failed - the one holding its last line GitHub marks an error (##[error]), else its last page - each line by its number, "
                + "both masked by the repository's runner secrets; in digest, what the check digested to, said for a model as a leg's "
                + "is: its annotations, read from the forge's check-runs API, each naming a file in its place, and its job's log, every "
                + "line numbered as the kept log numbers it and read by every form a CI log holds - each finding said once, a problem "
                + "matcher's line and its annotation one, a line the forge marked that no form read said among the lines no parser held - "
                + "every file a tool on the runner named placed against the job's workspace there, <work>/<repository>/<repository>, "
                + "read as the runner wrote it and named relative to it, a Go test's place - printed relative to its package's directory, "
                + "which no file of the job's tells - left as printed; every line reaching a model once, as a leg's entry says them: "
                + "where the digest holds an error or a failure, it says the lines no parser held and failing holds none, and where it "
                + "holds neither, failing is the page whole and the digest says those lines no parser held that the page does not; and in unread why "
                + "any of that could not be "
                + "read; every check ended green - success, neutral or skipped - on two reads running that found the same checks, at its "
                + "wait's end on the last alone, passes it; and no check within "
                + $"appearSeconds ({CiChecksRequest.DefaultAppearSeconds}) - or waitMinutes where that is shorter - checks still running at waitMinutes ({CiChecksRequest.DefaultWaitMinutes}) "
                + "or one ended neither green nor red asks (noChecks, checksPending, checksUnsettled), never failing it; answered accept, it "
                + "reads once more, waiting for nothing, and is done with the checks as they are, its passed true only where they are green. "
                + "A check that finds red is "
                + "red: under failOn red - every check's default but ci-read's, never - it fails with its check's code "
                + $"(an anchor finding {AnchorExit.Findings}, a red CI leg {CiExit.LegRed} and no matrix run {CiExit.MatrixDidNotRun}, a red check "
                + $"{CiExit.LegRed}), and under never it is done, its passed false. Every read of the forge is given the step's "
                + $"timeoutMinutes where it says one, else {ReportText.Limit(GhProcess.Limit)}, and never outlasts the step's attempt, which "
                + $"ends a read in flight with it: a ci-read's read past its limit ends the step incomplete ({HarnessExit.Incomplete}), which "
                + "runs it again; a ci-wait reads again where a read failed or passed its limit, saying each, for as long as its failure can "
                + "change - a dropped network, a server error and a rate limit however often they come, another failure while it is new - and "
                + "ends with that read's code where the same failure comes again, at once where no read changes it (the forge refusing the "
                + "credential, or saying what was asked is not there), or where its wait is spent.",
            prose,
            StringComparison.Ordinal);

        // What a leg step runs, answers, lets go and keeps: the names of its records are the layout's own.
        Assert.Contains(
            "build, test, run and check-mutations run their legs as the commands of those names do, in the worktree of the agent "
                + "their in names, else the main checkout: test with its filter, exclude and labels and run with its inputs - a filter, "
                + "an exclusion or a label that fills to nothing is not given - and check-mutations with its arms, every arm the registry "
                + "declares where it names none, or those its arms expression yielded - the item's own, from the record its builder "
                + "answered - and none where it yielded none, the step done and its swept saying so, no leg asked; and pending. A sweep "
                + "whose base names a commit - a template naming one, or 'item', the newest commit the run's ledger gives the item the "
                + "step is one of that a commit made - runs on a copy of that commit, made and taken away under the main tree's lock by "
                + "the one owner of such copies: a copy an earlier attempt left at its path - a stop, a pause, a crash - is taken away "
                + "before it is made again, one a stop cut off is taken away all the same, and its copy says the copy's path and what "
                + "could not be taken away (left); it holds no tree while it sweeps, so other items folding into the main tree never "
                + "move the bytes under it nor wait for it; an item whose ledger holds no commit that was made, and a base that named "
                + "nothing as the step started, are refused, nothing swept; and 'item' is refused where the file is read in a step no "
                + "forEach holds, a forEach that uses its file among them. Each answers passed, true where every leg passed; exitCode, the command's; "
                + "verdicts, each leg's own; red, the red legs alone - a leg that failed, with the phase that did not pass, the ledger's "
                + "line and, in tail, what it printed that its digest does not say, and of a sweep each arm that violated, survived or was unattributed, with its arm "
                + "and its line; runDirectory, null where the run made none; and isolation, what the work of its legs ran under. Each "
                + "red leg's entry also carries, in digest, what the phase that did not pass printed, typed: the first error whole - its "
                + "stack rendered to the frames inside the tree, its log's line range one read away - the next distinct errors to four "
                + "as one line each, every test failure of the four with its stack, and the rest - what the four do not say - counted "
                + "by code and by file, a count of none and an empty list left out; every line reaching a model once and none out of its reach: where the digest "
                + "holds an error or a failure and the phase's log is one read away, it says the lines no parser held - the last 50, "
                + "the nearest the failure, with how many it left out - and the entry's tail holds none, and otherwise - it holds "
                + "neither, or no log of the phase is on this machine - the tail holds the phase's last lines whole and the digest says "
                + "those lines no parser held that the tail does not; and in log, the log that phase wrote, whose lines the "
                + "digest's ranges number as the file numbers them - the copy kept in the step's directory in an agent's worktree, "
                + "else the run's own, a leg's a host ran brought back by its run as the leg ended - null only where a host's could "
                + "not be brought back; the digest of a leg a host ran, made there, travels on its "
                + "line. The ledger's --json keeps the digest typed for a reader that asks, its bounds said - the first 50 diagnostics "
                + "of each severity, the first 50 failures and the last 50 lines no parser held, the rest counted, a stack held to its "
                + "first 48 and last 16 frames with the cut counted - the text ledger says each red leg's first error in a block below "
                + "its table, and {run.red} says it on its line. Where the run keeps a codebase graph of the tree the step's legs ran "
                + "in, each red leg's digest is joined to it before the step's ledger is kept: each place inside the tree carries the "
                + "declaration the graph holds enclosing it - its kind, its qualified name, its handle - the first error's as its "
                + "symbol and each frame of a stack inside the tree with it in brackets after its line, where its file holds what its "
                + "leg built, as the graph is asked and once it has answered: a leg in a copy built the tree as the copy was made of "
                + "it, a leg in the tree itself the tree as it held it when the leg ended, and a place whose file was written since - "
                + "a fold landing while the legs worked - carries none; a plain build, test or run "
                + "command asks no graph, and its digest's symbols are null. What "
                + "its legs build, test and run is what the run's agents wrote, so the work of each leg this machine runs - every phase "
                + "it starts, a set-up's lines apart - runs inside the operating system's isolation of the agents' work ('help "
                + "agent-clients'); where this machine has none, agentPolicy.isolation required fails the step "
                + $"({HarnessExit.ToolMissing}) with nothing run, and preferred runs its legs outside, each leg's line saying so. "
                + "A leg that works in a copy - its own under .legs, or a "
                + "host's - lets the step's hold on its tree go once its copy is made, or once it ends without one, and a leg in the tree "
                + "itself once it ends: the step's record then says copied. A run whose runner, or a runner its run checks name, writes "
                + "the tree holds write on it, and its legs work in the tree itself. A leg run refused part way, cut off, or ended with a "
                + "leg it placed left with no line ends the step with the run's own code; one its legs ended - none could run, another "
                + "run owns its records - is read by their verdicts. The step keeps the ledger in its directory "
                + $"({OrchestratorLayout.StepLedgerFileName}) - written as its legs' run begins, naming the run and its records, and "
                + "again as each leg ends, so a crash while they work is read where their work stopped (a write of it the file system "
                + "refuses while they work is said once, and they work on: the write as their run ends is the one that must succeed) - "
                + "and, in an agent's worktree, "
                + "the log of each red leg's phase that did not pass, whole, as it is on this machine "
                + $"({OrchestratorLayout.StepLogsDirectoryName}/<leg>.<phase>{PhaseLogs.Extension}, the phase named as its log is), "
                + "each attempt's own.",
            prose,
            StringComparison.Ordinal);

        // The refusals' codes are the ones the command answers with, held here so the sentence cannot drift from them.
        Assert.Contains(
            "'dssharness validate-workflow' checks the definition - the shape of every key, every name an expression, a template key "
                + "or a prompt reads, every file the definition names, each step's schema one the engine reads (each problem naming "
                + "the file, the step and the place in the schema; a step's start holds it there again) - and refuses "
                + $"({HarnessExit.ConfigInvalid}) naming each problem, without running anything, as a "
                + "config.json that does not hold is refused. A schema may share a definition with the schema files beside it: a "
                + "$ref of the form plan.schema.json#/$defs/workItem names one, and where the schema is read - the check, a run, "
                + "a path typed by it - the definition is taken into the schema's own $defs, so the work-item shape the shipped "
                + "plan, self-audit and diagnosis schemas hold is one definition among them; a reference nothing beside the schema "
                + "answers is refused where the schema is checked, naming the file. Where config.json declares no workflow there is nothing to check, and it "
                + $"is refused ({HarnessExit.Refused}), saying that \"workflows\": [\"{WorkflowTemplates.ShippedWorkflowName}\"] "
                + "declares the one init writes.",
            prose,
            StringComparison.Ordinal);

        // Every key of the file, of every step and of every group, from the tables the reader reads.
        Assert.All(WorkflowKeys.File, key => Assert.Contains($"  {key.Name}", result.StandardOutput, StringComparison.Ordinal));
        Assert.All(WorkflowStepTypes.Common, key => Assert.Contains($"  {key.Name}", result.StandardOutput, StringComparison.Ordinal));
        Assert.All(WorkflowStepTypes.Group, key => Assert.Contains($"  {key.Name}", result.StandardOutput, StringComparison.Ordinal));
        Assert.All(WorkflowStepTypes.Running, key => Assert.Contains($"  {key.Name}", result.StandardOutput, StringComparison.Ordinal));
        Assert.All(WorkflowStepTypes.All, type => Assert.Contains($"  {type.Type}", result.StandardOutput, StringComparison.Ordinal));
        Assert.Contains(
            "What every step that runs takes, beyond the common keys - every typed step but pause, which only asks:",
            prose,
            StringComparison.Ordinal);

        // A step's time limit, from the bounds the validator and the coordinator hold.
        Assert.Contains(
            $"A step that runs may bound each of its attempts with timeoutMinutes, from 1 to {WorkflowStep.MostTimeoutMinutes.ToString("N0", CultureInfo.InvariantCulture)}: "
                + "the engine counts it from each start of the step - an attempt, or its run after a question of its own was answered - and "
                + "never while it waits on a question. Past it the step is stopped as a stop stops one and ends incomplete "
                + $"({HarnessExit.Incomplete}), saying it passed its timeoutMinutes: a reasoning step goes to what handles its failure, any other "
                + $"runs again as an incomplete step does; one whose runner does not end within {PointOfNoReturn.SafePauseDrain.TotalSeconds.ToString(CultureInfo.InvariantCulture)} s "
                + "of that stop fails all the same, goes to what handles its failure and is not retried - a step of others fails the target "
                + "whose turn it was and goes on with the next - and what it claimed stays held until that runner ends. A reasoning step's "
                + "handoff turn at a pause is bounded by handoffSeconds, not by this. A ci-wait's is always more than its waitMinutes (absent, "
                + $"{WorkflowStep.DefaultWaitMinutes}); a group and a pause step take none, running nothing of their own; a type whose step "
                + "has a limit of its own where it says none says so beside its name below.",
            prose,
            StringComparison.Ordinal);

        // The type that has one says it beside its name, from the table.
        Assert.Contains("graphify-codebase (timeoutMinutes 30 by default)", result.StandardOutput, StringComparison.Ordinal);
    }
}
