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
    private static string RenderWorkflows()
    {
        var builder = new StringBuilder();

        builder.AppendLine("Workflows");
        builder.AppendLine();
        builder.AppendLine($"A workflow is a flow of typed steps that depend on one another by name, declared by");
        builder.AppendLine($"name under workflows in config.json and defined by a yml file of its own:");
        builder.AppendLine($"{HarnessLayout.WorkflowsDirectoryRelative}/<name>/<name>{HarnessLayout.WorkflowFileExtension}. This build checks a");
        builder.AppendLine($"workflow's definition, with '{ToolPackage.Command} validate-workflow <name>', and starts and drives a run");
        builder.AppendLine($"of one with '{ToolPackage.Command} {FlowService.RunCommand} <name>' (see 'help orchestrators'). The steps");
        builder.AppendLine($"served so far are {string.Join(", ", FlowStepRunners.Served)}, each below; a step of any other type");
        builder.AppendLine("fails, saying no runner serves its type.");
        builder.AppendLine();
        builder.AppendLine("The workflow's directory is the workflow's own - its agent files (agents/<name>.md),");
        builder.AppendLine("its prompts, its schemas, the bar it recommends by (recommendations.md), its texts");
        builder.AppendLine("(texts:, each name a markdown file a prompt reads as {text.<name>}) - and every path");
        builder.AppendLine("the yml names is relative to that directory and stays inside it, as an action's files");
        builder.AppendLine("are relative to its own directory (see 'help runners'). So two workflows never read");
        builder.AppendLine("each other's files, and a repository can hold as many as it has ways of working. init");
        builder.AppendLine("writes one - the implementation workflow, from a request to a pull request whose checks are");
        builder.AppendLine($"green - and declares it in the config it creates. It builds first: the baseline is split in two - a build");
        builder.AppendLine("step, which fails red and is taken by the recovery and built again - and the test step it was, which");
        builder.AppendLine("waits for the build - and the flow makes its codebase graph after that build, beside the baseline's");
        builder.AppendLine("tests and the CI read, passed over where it cannot be made; every reason step after it is served from");
        builder.AppendLine("the graph as the twenty-run measurement of 2026-11-11 set each type's value: the steps that work on");
        builder.AppendLine("code keep the tool - the implementer and the pre-existing-issue triage say nothing and are given it");
        builder.AppendLine("and their task's seeds by default, as the planner is, which the measurement left as it was, and the");
        builder.AppendLine("reviewers, the self-audit, the diagnosis and the steps that verify and fix say graphFrom: changes -");
        builder.AppendLine("the request triage, whose work is text the map does not light, asks none, the plan audit and the");
        builder.AppendLine("item audit, the plan audit's agent on a later round's items, say graph: tools with graphPromptTokens: 0");
        builder.AppendLine("- the tool, for a class's sites they search are what its sites answers, beside their own text search,");
        builder.AppendLine("and no pack, which lights none of a plan's text - the");
        builder.AppendLine("prioritiser and a review's scope are given the map alone, and the scribe's steps, which write the");
        builder.AppendLine("run's records, ask none of it. Where");
        builder.AppendLine($"config.json declares {WorkflowTemplates.ShippedWorkflowName},");
        builder.AppendLine("init writes each of its files that is missing - the whole set where the repository has no");
        builder.AppendLine("definition of it yet, and where it has one only the shipped files that definition names,");
        builder.AppendLine("the workflows it uses among them - a shipped one taken out whole read from the copy init");
        builder.AppendLine("writes, so it comes back whole in one run - so a file taken out with its step stays out -");
        builder.AppendLine("and keeps each one that is there,");
        builder.AppendLine("whatever it holds, so an init that stopped part way is finished by the next - where a");
        builder.AppendLine("file is missing and the definition there does not read, or uses a workflow of the");
        builder.AppendLine("repository's that does not resolve, init refuses (12, naming each problem; 13 where it");
        builder.AppendLine("cannot be read), since which files it names cannot be told; nothing that exists is ever");
        builder.AppendLine("overwritten, and a config that does not declare it gets none of it. A file such a definition");
        builder.AppendLine("names that this tool ships elsewhere now - one of the item pipeline's, moved into its own");
        builder.AppendLine("folder - is not brought back, and init says so.");
        builder.AppendLine("The name is the folder's, the file's and the name: key's alike, one word, and a");
        builder.AppendLine("disagreement among the three is refused when the file is read.");
        builder.AppendLine();
        builder.AppendLine($"'{ToolPackage.Command} {WorkflowExportService.CommandName} <name> --to <dir>' copies a workflow - its yml and every");
        builder.AppendLine("file it names, and no other - into <dir>/<name>/, a folder of its own that must not be");
        builder.AppendLine("there (13 where it is): a declared one once it holds (12 where it does not), byte for");
        builder.AppendLine($"byte, or, with --shipped, the {WorkflowTemplates.ShippedWorkflowName} workflow as this tool ships it, to make a");
        builder.AppendLine("version of your own. A file that cannot be written takes back what was written before it, or");
        builder.AppendLine("says why it could not. A used workflow's files go where it sits under the folder, and a");
        builder.AppendLine("workflow that uses one outside its folder, or a remote one, is refused (13), nothing written,");
        builder.AppendLine("for the folder alone would name a workflow it has not got; --from vendors a remote one.");
        builder.AppendLine();
        builder.AppendLine("A workflow declares inputs - the values a run is started with, each with a kind, a");
        builder.AppendLine("default and the options it takes; params - its own knobs, defaulted in the file and read");
        builder.AppendLine("as param.<name>; and outputs - what the whole workflow produces, each from a step of");
        builder.AppendLine("the flow. Its steps are of the types below, or groups holding steps of their own: a");
        builder.AppendLine("group runs its steps once for each work item a forEach yields, again and again until a");
        builder.AppendLine("repeat's bound is met, or once as a block that gathers its members' findings. Every");
        builder.AppendLine("step's name is a lower-case word with dashes within, unique among its siblings; steps");
        builder.AppendLine($"nest at most {WorkflowFileParser.MaxDepth} deep. A step raised by another step's ending says on - by the outcome");
        builder.AppendLine("(failure, success or end, a failure by exit code) - and sends the run back with then;");
        builder.AppendLine("the schedule never starts it. A step gives a failure nothing handles to the workflow's");
        builder.AppendLine("recover by saying onFailure: recover, itself or through a group around it - never a");
        builder.AppendLine("reasoning step, whose agent is tried again (retries, waiting backoffSeconds, twice as long");
        builder.AppendLine("each time) and on each model of its fallback, and which asks where it still fails; where");
        builder.AppendLine("every account it could use is at its limit, or its provider's service fails past the");
        builder.AppendLine("client's own tries, the run pauses safely instead and resumes that very step. The recovery's steps,");
        builder.AppendLine("written once, read the step that failed as source and see every step of the flow, find");
        builder.AppendLine("what went wrong and put it right; then the run goes where its onSuccess says - failed-step,");
        builder.AppendLine("the step that gave it its failure, which runs again from where it stopped - and a failure");
        builder.AppendLine("of its own work, past its steps' retries, does what its onFailure says: pause or stop; the");
        builder.AppendLine("recovery never calls itself. A step that fails again goes to the recovery again by its own");
        builder.AppendLine("onFailure, until it");
        builder.AppendLine($"passes, a person is asked, or maxVisits is met (absent, the engine's bound of {FlowRecover.MostVisits}");
        builder.AppendLine("visits); past it the run asks (recoveryBound), with the advisor's recommendation of a fix:");
        builder.AppendLine("more, one more recovery; direct, with the fix in words, for the next one; skip, where the");
        builder.AppendLine("step is skippable; or abort.");
        builder.AppendLine();
        builder.AppendLine("A step may be a whole workflow of its own: it says use - a path to the workflow's yml in a");
        builder.AppendLine("folder of its own, relative to the calling workflow's directory, written with forward");
        builder.AppendLine("slashes and no part that starts or ends with a space, and held inside the workflows folder");
        builder.AppendLine("once links are followed, as the run's own workflow is -");
        builder.AppendLine("and the workflow it names is read as a definition of its own and runs inside the run, its");
        builder.AppendLine("steps the step's own, its files read in its own directory and its declared outputs the");
        builder.AppendLine("step's once it ends - one that reads nothing, from a step its item skipped, absent, and one");
        builder.AppendLine("that cannot be read failing the use. Its with fills the workflow's inputs, each value read");
        builder.AppendLine("as the input it fills declares: an expression for a value, a template for a text, a path of");
        builder.AppendLine("the used workflow's directory for a file, and a name for a step - what a step of the used");
        builder.AppendLine("workflow reads as {input.<name>} whole in its onFailure or a handler's then, filled in the");
        builder.AppendLine("scope that declares it, however deep the failing step; the file a step is asked with may be");
        builder.AppendLine("read the same way, one whole input of the kind file naming its prompt, so one pipeline is");
        builder.AppendLine("asked with a different prompt by each workflow that uses it. An input the use leaves to its");
        builder.AppendLine("default is read the same way, where the use sits; a step input it fills with no name is");
        builder.AppendLine("refused where it is read, and a policy filled with recover is held to what recover written");
        builder.AppendLine("out is - a reasoning step is never recovered, and a recovery never calls itself. A step of");
        builder.AppendLine("the used workflow may also say a when that reads one input of it alone: its own gate, held");
        builder.AppendLine("off where a use fills that input so - the way the shipped flow's item pipeline holds its");
        builder.AppendLine("redo and undo off where its recovery uses it; only where the use's with value, or the");
        builder.AppendLine("input's default, closes the gate as written is the step free of waiting for the agent it");
        builder.AppendLine("works in, and one the use opens is held to it as any step is. The with values are read once");
        builder.AppendLine("for each scope its steps run in - the block, each item of a forEach, each iteration of a");
        builder.AppendLine("repeat, each raising of a handler - and kept in the use's record, so a resume takes them");
        builder.AppendLine("back and never reads them again; every item's are read before any item opens, and one that");
        builder.AppendLine("cannot be read fails the use, saying which input and for which item, with nothing of it");
        builder.AppendLine("opened - a step of it that runs all the same, an always step, is refused where it reads an");
        builder.AppendLine("input. A failure policy that reads nothing, or fills with no policy, is said in the step's");
        builder.AppendLine("error and record, and the failure goes where the top level sends what nothing else decides;");
        builder.AppendLine("a handler whose then cannot be read ends failed saying why. The steps of a used workflow");
        builder.AppendLine("read only its inputs, its params and its own steps, never the calling run's - nor the");
        builder.AppendLine("caller's work item, its iteration before, or what raised a handler of the caller, and a");
        builder.AppendLine("pull request's base inside it reads no input - every fact of the caller they need comes");
        builder.AppendLine("through an input; a recovery's steps read those of the workflow that declares the recovery,");
        builder.AppendLine("and {text.<name>} is the used workflow's own where it declares the name, else the next one");
        builder.AppendLine("out's, else the run's. A failure inside it that is given to a recovery is taken by the");
        builder.AppendLine("nearest recovery declared around the failing step, however many uses out, before the run's;");
        builder.AppendLine("that recovery's onFailure is pause or stop, as the run's own is. Its budget, reporter,");
        builder.AppendLine("pushCheckpoints and recommendations are never applied, nor - unless the use says");
        builder.AppendLine("useParentLimits: false - its advisor, amend, answers, limits and codebaseGraph, nor its");
        builder.AppendLine("codebaseGraph where the run's own workflow declares one: the calling run's own hold, and");
        builder.AppendLine("validate-workflow says so beside the fingerprint of what it read. With");
        builder.AppendLine("useParentLimits: false the workflow's own settings govern its steps key by key: a key is");
        builder.AppendLine("governed by the nearest such use around a step that declares it, in a recovery as anywhere,");
        builder.AppendLine("and a key no such use declares stays the run's, for a use cannot widen a run's limits by");
        builder.AppendLine("declaring nothing. Its maxReasoningAgents, maxParallelProcesses and maxOpenAgents gate its");
        builder.AppendLine("steps as pools of their own in the place of the run's, counted across every instance of the");
        builder.AppendLine("use; its maxInstances caps the instances its steps open, across every instance of the use");
        builder.AppendLine("and in the place of the run's own cap for them; its maxRetries and backoffSeconds are what");
        builder.AppendLine("its steps fall back to; its handoffSeconds bounds the handoff of its paused reasoning steps;");
        builder.AppendLine("and its advisor recommends for its steps' questions in the place of the run's - its files");
        builder.AppendLine("read in its own directory, its prompt handed the inputs the use filled and the texts its");
        builder.AppendLine("steps see, its bar the workflow's own texts.recommendations where it declares one, and each");
        builder.AppendLine("recommendation credited to it. A question of its steps is answered nearest first: the");
        builder.AppendLine("step's own answers, the groups around it within the workflow, the workflow's own answers,");
        builder.AppendLine("and then the use step's own, the caller's groups and the run's. The run's one codebase");
        builder.AppendLine("graph has one shape - the run's own workflow's, else the one its governing uses declare - and");
        builder.AppendLine("every sync, serving and graphify step is made under it. Each key such a use applies is held");
        builder.AppendLine("to every rule the run's own key of that name is - its limits, its maxInstances, its");
        builder.AppendLine("advisor, its answers, its graph's shape - for a use cannot declare what a run could not, and");
        builder.AppendLine("two such uses that shape the graph differently are refused where the run's own workflow");
        builder.AppendLine("declares no shape, for a run has one graph. Its budget and its reporter are carried and not");
        builder.AppendLine("applied: the run's budget counts its steps' spend, and the run's reporter words the run as a");
        builder.AppendLine("whole. An amend is refused there, said at validation, for an amend is a run's own, and");
        builder.AppendLine("preapproved is a run's input alone, whatever the key says. A workflow file that uses itself,");
        builder.AppendLine($"directly or through others, is refused with the circle of the files that close it, and steps nest at most {WorkflowFileParser.MaxDepth}");
        builder.AppendLine("deep in all, a used workflow's own counted below the step that uses it; a run records each");
        builder.AppendLine("use's resolution, and a resume is refused where what the called workflow says changed since");
        builder.AppendLine("the run began - the digest of its parsed definition, taken as the run's own definition's is,");
        builder.AppendLine("so a comment or line endings change nothing - as its own definition's would.");
        builder.AppendLine();
        AppendWrapped(builder, RemoteWorkflowsHelp);
        builder.AppendLine();
        builder.AppendLine($"{ToolPackage.Command} {WorkflowExportService.CommandName} --from <use> --to <dir> vendors a remote workflow into the");
        builder.AppendLine("repository at its resolved commit - no workflow named beside it, nor --shipped: every file");
        builder.AppendLine("of its directory, byte for byte, and source.md, a note of where it came from and how the");
        builder.AppendLine("commit came about where that is worth a word, a directory holding a source.md of its own");
        builder.AppendLine("refused - so a team can review what is written and use it as ./. A workflow named beside");
        builder.AppendLine($"it, --shipped, or a --from that names no remote workflow is a usage error ({HarnessExit.UsageError}), and the");
        builder.AppendLine($"folder is looked at before anything is fetched, one that is there refused ({HarnessExit.Refused}) with");
        builder.AppendLine("nothing written anywhere; a refusal after the fetch says what it fetched is kept in the");
        builder.AppendLine("workflow cache.");
        builder.AppendLine();
        AppendWrapped(builder, WorkflowCacheHelp);
        builder.AppendLine();
        AppendWrapped(
            builder,
            $"A step that runs may bound each of its attempts with timeoutMinutes, from 1 to {WorkflowStep.MostTimeoutMinutes.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)}: the engine counts "
            + "it from each start of the step - an attempt, or its run after a question of its own was answered - and never while it "
            + "waits on a question. Past it the step is stopped as a stop stops one and ends incomplete "
            + $"({HarnessExit.Incomplete}), saying it passed its timeoutMinutes: a reasoning step goes to what handles its failure, any "
            + $"other runs again as an incomplete step does; one whose runner does not end within {PointOfNoReturn.SafePauseDrain.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)} s "
            + "of that stop fails all the same, goes to what handles its failure and is not retried - a step of others fails the target "
            + "whose turn it was and goes on with the next - and what it claimed stays held until that runner ends. A reasoning step's "
            + "handoff turn at a pause is bounded by handoffSeconds, not by this. A ci-wait's is always more than its waitMinutes (absent, "
            + $"{WorkflowStep.DefaultWaitMinutes}); a group and a pause step take none, running nothing of their own; a type whose step "
            + "has a limit of its own where it says none says so beside its name below.");
        builder.AppendLine();
        builder.AppendLine("An issue a reasoning step meets that was there before the run, it reports in its answer");
        builder.AppendLine($"(preExisting) and never fixes itself; onPreExisting names the step of the flow's top level");
        builder.AppendLine($"that triages each one. That step says raised: {WorkflowStep.RaisedByPreExisting} - it is raised once per issue: a");
        builder.AppendLine("repeat of the same title at the same place raises nothing while a triage of it holds the issue, the");
        builder.AppendLine("run's records saying so, unless it stops the work of a step that triage will not run again, and one");
        builder.AppendLine("whose triage failed, or met again after its fix was done, is raised anew; and it is never scheduled, as the");
        builder.AppendLine("recovery is not - and onAddress: recover: it reproduces the issue on the");
        builder.AppendLine("run's base commit, gives the recovery one that is in a step's way, related to the work or");
        builder.AppendLine("critical, to fix at its root (a step it was in the way of runs again from where it stopped);");
        builder.AppendLine("sends a defect in code the run's change does not touch, that blocks nothing, to the run's next");
        builder.AppendLine("round - sized, by its files, what must be built and what measured first, in next-round.json,");
        builder.AppendLine("which run.nextRound reads and a round's plan check holds its items to: each issue named in an");
        builder.AppendLine("item's settles, none left unsettled, the settling items first, and the rounds going on while one");
        builder.AppendLine("is left - never holding the round the issue came from; and");
        builder.AppendLine("lists the rest, proven, with a title and a description, in the run's");
        builder.AppendLine("pre-existing-issues.json, given to the work sync at the end. Where a workflow has such a step, every other");
        builder.AppendLine("reasoning step names it, so no issue is lost.");
        builder.AppendLine();
        builder.AppendLine("A reasoning step names its tier or its model and its effort: one level for every model it");
        builder.AppendLine("may run on, or a map giving each its own - all for the rest, a model's code for one, so a");
        builder.AppendLine("step can run one model at xhigh and another at max, where that is the highest it takes. Each");
        builder.AppendLine("model's level is checked against what that model takes when the definition is checked");
        builder.AppendLine("against the configuration's providers ('help agent-clients'), and validate-workflow says");
        builder.AppendLine("every step's models and levels under the line that says it holds. A test step's filter names");
        builder.AppendLine("the tests it runs as units - a list, or an expression read where it starts - each given after");
        builder.AppendLine("the invocation's filterArg, all joined into one value by its filterJoin - by '|' for ctest,");
        builder.AppendLine("dotnet test and dart test where it declares none; a filter that reads no unit runs no test,");
        builder.AppendLine("and the step ends skipped, saying its noTests. Its filterWhen switches the filter: where it");
        builder.AppendLine("reads false every test of its legs runs, as the shipped gate does where the run's input");
        builder.AppendLine("finalTests is all. Its affected keeps the units it runs in the run's affected-tests.json");
        builder.AppendLine("under the key it fills, before the step runs: a later step under that key replaces them, and");
        builder.AppendLine("one for a work item a plan update takes back, or one its when skips, removes them.");
        builder.AppendLine("run.affectedTests reads every unit kept, each once: the shipped verify steps test each item");
        builder.AppendLine("on its own units alone, and the gate on run.affectedTests.");
        builder.AppendLine();
        AppendWrapped(
            builder,
            "validate-workflow holds a definition to the config.json it is checked under too: a run step whose runner, or a runner "
            + "its run checks name, writes the tree (writesTree) names with in the create-agent step whose worktree it works in, since "
            + "the run reads its definition in the main tree and folds into it; the agents a run makes, named "
            + $"'{OrchestrationRules.GeneratedAgentPrefix}' and a number that grows to the workflow's maxInstances, fit "
            + "worktrees.maxNameLength; and a commit step gives a value for every variable commit.template requires and none for one "
            + "it does not declare - or, where none is declared, a message and no variables.");
        builder.AppendLine();
        builder.AppendLine("The keys of the file and of every step are listed below, generated from the tables the");
        builder.AppendLine("reader itself reads, so what is listed is what is read.");
        builder.AppendLine();
        builder.AppendLine("The file's own keys:");
        builder.AppendLine();
        AppendKeys(builder, WorkflowKeys.File);
        builder.AppendLine();
        builder.AppendLine("What one declared input takes, and one declared param, and one declared output:");
        builder.AppendLine();
        AppendKeys(builder, WorkflowKeys.Input);
        AppendKeys(builder, WorkflowKeys.Param);
        AppendKeys(builder, WorkflowKeys.Output);
        builder.AppendLine();
        builder.AppendLine("What every step takes, group or typed step alike:");
        builder.AppendLine();
        AppendKeys(builder, WorkflowStepTypes.Common);
        builder.AppendLine();
        builder.AppendLine("What a group takes, beyond those:");
        builder.AppendLine();
        AppendKeys(builder, WorkflowStepTypes.Group);
        builder.AppendLine();
        builder.AppendLine($"What every step that runs takes, beyond the common keys - every typed step but {string.Join(" and ", WorkflowStepTypes.All.Where(type => type.Asks).Select(type => type.Type))},");
        builder.AppendLine("which only asks:");
        builder.AppendLine();
        AppendKeys(builder, WorkflowStepTypes.Running);
        builder.AppendLine();
        builder.AppendLine("The step types, and what each takes beyond those (a check's failOn says what a red");
        builder.AppendLine("verdict does to the step: red fails it, never records it; each type's default is in its");
        builder.AppendLine("list):");
        builder.AppendLine();

        foreach (var type in WorkflowStepTypes.All)
        {
            // orient takes nothing beyond the common keys: its line stands alone.
            var said = new[]
            {
                type.IsCheck ? $"a check; failOn {type.FailOnDefault} by default"
                    : type.ChecksAsOutput ? "a check, which answers in its output and never fails" : null,
                type.DefaultTimeoutMinutes is { } minutes ? $"timeoutMinutes {minutes.ToString(CultureInfo.InvariantCulture)} by default" : null,
            }.OfType<string>().ToList();

            builder.AppendLine($"  {type.Type}{(said.Count > 0 ? $" ({string.Join("; ", said)})" : string.Empty)}");
            builder.AppendLine();

            if (type.Keys.Count > 0)
            {
                AppendKeys(builder, type.Keys);
                builder.AppendLine();
            }
        }

        AppendWrapped(
            builder,
            "What the steps served so far do. orient reads the main checkout and the repository once, and answers: the branch "
            + "(failed, refused, where HEAD is detached), baseBranch - the pull-request step's base, filled from the run's inputs "
            + "and its name, else the default branch of the remote the run's branch goes to (the push step's remote, else the one "
            + "the branch tracks, else origin, else the only one), null where there is none - startCommit, the commit the run "
            + "started from; hasRemote; hasLegs and legCount, every declared leg; anchors, a citation root that names something once read, as "
            + "check-anchor-citations reads them; "
            + "ciConfigured, ci.legJobPattern, ci.buildStep and ci.testStep each set and a base branch to read CI on; hasMutations, "
            + "mutations.registry naming a file in the tree; tests, for each leg that tests, its name, the program, the filter's "
            + "option and the exclusions its test invocation applies on the leg's operating system (exclude, remoteExcludes; where "
            + "ctest is given -LE more than once, a test is left out only where it matches every one); "
            + "dirtyPaths, every path git status names; openAgents, each agent that counts against its run's open-agent limit, of every orchestrator of the "
            + "repository, as {run, agent}; and inFlight, who holds the main tree now, each as a held-now line says it. "
            + "graphify-codebase syncs the run's one codebase graph with the main checkout, reading only what changed, and answers "
            + "what it kept, read, reused and removed and what it could not read ('help codebase-graph'). "
            + "validate-plan checks the work items its items yields and answers ok, problems and the items, never a failure: at "
            + "least one item, and as many as the work needs - no count is refused, for a round runs its items in waves of the "
            + "run's maxOpenAgents; each with a name in lowercase letters, digits and single "
            + "hyphens, used once - and, where the list opens a round, no item the round builds but an owed one under the name "
            + "of a work item of an earlier round, for the run knows a work item by its name alone; a round added to a group "
            + "with no loop around it builds the owed work alone, its own items being those the round before built - "
            + "ownedPaths, forbiddenPaths and the lists reuses, creates and replaces - an item without them is of "
            + "the wrong shape; no path owned by two items, and none an item owns that it must not touch; the path budget of the "
            + "worktree each would get, with the copies and workers its legs keep inside it and the deepest file it owns; every "
            + "path it reuses or replaces there in the main tree, something under one of the three, and a why and a search for "
            + "each thing it creates; and each item declaring the classes of the decisions it changes or saying in noClass why it "
            + "decides none, the classes held to what the tree and the item's own paths say of them: a class id used once in the "
            + "plan, each home and each site a path inside the tree as a path an item reuses is, the home among the item's own "
            + "paths and there in the tree where it exists, a site marked home exactly where it is the home by its path and "
            + "symbol, every other site - which changes, reading the home or deleted - the item's too - patching some of a "
            + "class's sites is never done - more than one site consolidated at the home, as one site that is not the home is, "
            + "each class searched by data its queries name again, its universe holding the axes the repository declares, and "
            + "one home belonging to one item, two classes of one item sharing one as they may; and each item naming the tests its change affects in "
            + "tests, one unit to an entry and none blank, or saying in noTests why it affects none - exactly one of the two. A step given the pieces of a plan update (a scope that yields a list not empty) checks them "
            + "with the items, each problem among the rest: each piece of one of the four kinds (added, changed, removed, told) "
            + "and its texts; an added or a changed piece's item an item of the list, a removed piece's of a work item's shape; "
            + "no two pieces naming one work item; a changed or a removed piece naming a work item the plan had before the "
            + "update, and an added one naming none a round of the run carries - one that does such an item again is changed; "
            + "and the items the update places checked with those of the round it joins, as one list - among themselves "
            + "where the run has no round. One whose items yield no list, or whose scope yields neither a list nor nothing, "
            + "fails, as a definition that does not hold does. create-agent makes the agent the engine named for it - a<N>, one past the "
            + "highest the run's agent directories and step records hold, never given twice - for the model of the first reason "
            + "step that works in it, or the default tier's first model where none does, both written to the step's record before "
            + "the agent is made. Run again, it reads the agent first: none, it is made; whole, it is done; made part way with its "
            + "worktree's commit recorded, its seeding is finished; made part way without it, it is deleted, and closed - its "
            + "removal begun - its removal is finished; that, or deleted, the name is spent and the step fails, its next attempt "
            + "named anew. reason works the step's agent: the one its in names or the work it goes on with (resume), or - where it "
            + "names neither - the engine's own read-only agent, with no worktree, made before the step runs and deleted once no "
            + "step can go on with its work; the step's model resolved with its agent definition below its own and its order built "
            + "as entries of a model and one of its provider's keys, each entry's credential read from the slot "
            + "('help agent-clients'); its prompt file read and filled where the step starts, its writes given as an "
            + "expression read there too - the names it yielded narrowing what it may write as a written-out list does - "
            + "and each skill it names looked up again and "
            + "the folder handed over in the agent's work directory; the work it goes on with rendered as its continuation record, "
            + "and its going on by the client's kind - the stored conversation handed where the client goes on with one, a new "
            + "launch rendered where it does not; and its answer, held to the schema its outputs names - one the engine reads, "
            + "refused otherwise where the workflow is checked and again before any client is asked - the step's output. Where the "
            + "repository declares legs, a step whose agent runs commands (its capabilities hold run) is given the measure tool on "
            + "every client: one call takes a leg, its "
            + "variant - the build configuration the leg's own configuration declares - a test filter and the cell of a universe "
            + "being measured, runs those tests of that leg through the test service as a test step's legs are run, in the agent's worktree "
            + "where it has one and in a local copy of the main tree where it does not, under the agents' isolation and the step's "
            + "own record of what its legs start, and answers what they showed - passed where the leg passed, failed where its "
            + "build or its tests failed, and unmeasured with why for anything else: a leg refused by another run's lock, "
            + "skipped, stopped, a filter that matched no test, a leg not declared - the verdict as the ledger spells it, with the "
            + "run's directory and the call's own records (measures/<call id>/ of the step's directory: the ledger, and what the "
            + "call answered, which is what class-closure reads of what it measured); the call is a tool call of the step's "
            + "stored stream, its id what a closure record's measuredBy cites. A failed "
            + "agent is tried again on its model - the step's retries, absent the workflow's maxRetries, waiting its backoffSeconds, "
            + "absent the workflow's, twice as long before each later try - and then on each model of its order; a run that ended "
            + "on a key the provider refused moves to the next key at once, with no wait, and the run's pause ends the wait "
            + "before a try and begins no launch after one ended, the step handed off with no handoff, saying so; what its agents "
            + "assumed without asking is kept, read as the step's own assumptions fact and named in the run's. Where the step may "
            + "ask (ask) its agent's questions pause it - askRounds times in one attempt, the answer or decide going on in the same "
            + "attempt as the agent's next message; a limit of its own (turns, budget, a truncated answer, a full context) asks, "
            + "more offered only where a stored conversation of the attempt can be handed, and compact for a full context goes on "
            + "from the engine's reduction of it, the attempt's bounds counting still; every account at a limit, and a provider's "
            + "service failing past the client's own tries, pause the run safely, and the resume runs that very step. What a "
            + "step spends is charged to the run's budget turn by turn as its agent works - every token of every kind, and its "
            + "cost at the configuration's prices for the model serving it - and each launch is held to what is left of the "
            + "run's dollars before its reserve where that is less than its agent's own maxBudgetUsd, or where it names none: a "
            + "launch that bound ended has reached the run's reserve, not the step's own budget, so the workflow's answers may "
            + "allow more and the step runs again given its handoff, or else the run pauses for its budget - and a launch whose "
            + "share was reckoned before more was granted runs again given its handoff, its share reckoned anew. A turn whose cost "
            + "could not be counted leaves the run's dollars known only as at least what was counted: status, the report, "
            + "flow.json and every --json say so, uncounted beside usd naming why; in a run budgeted in dollars such a turn "
            + "reaches the run's reserve for the grant that stands, so the run asks, and a grant of more lets it go on, each "
            + "launch held to what is left as far as the dollars were counted. A safe pause asks each reason step in "
            + "flight for its handoff in its own conversation, all at once, and waits for the agent's answer within the "
            + "workflow's handoffSeconds; an agent that has not answered by then is ended. Where an "
            + "agent gave no handoff of its own - a limit ended its run, a crash cut the attempt off, its handoff turn passed its "
            + "bound or failed - the engine writes one from the stored stream of the step, its first line saying why: the files the "
            + "attempt wrote, the last commands it ran and how each ended, its last messages word for word, and the steps before it "
            + "that left notes, named by step. A pause "
            + "step asks and asks only: the engine fills its question as a template is filled, the questions an upstream step "
            + "wrote (questions) shown under it - each in the shape of a question, one that recommends outside its own choices "
            + "shown as it is with a line under it saying so - and the step's answer answers them together. What the person "
            + "gave the run answers first, where they gave it: the first preAnswer whose when holds answers at once, as a "
            + "configured answer would, and an entry that quotes words holds only where they are not empty and stand in the "
            + "input they are said to be of, every run of white space compared as one space - reported words the input does "
            + "not hold answer nothing, the record says so, and the person is asked. Before a person is left with a question "
            + "the engine raises itself, the orchestrator's advisor recommends on it: one reason step that only reads, run in "
            + "the place the asking step works in with its records and its own conversations under advice/<n>/ of the asking "
            + "step's own directory, numbered after those already there, its answer held to the engine's own schema - one of "
            + "the question's answers, with a text where that answer takes one - bounded by its definition and by what is left "
            + "of the run's dollars, and retried as every agent; the question is open while the advice runs - an answer ends "
            + "the asking, the question saying so, and the step that asked starts again only once the advice's runner has "
            + "ended; a pause or a stop ends it too, the question saying the recommendation is sought at the resume - the "
            + "recommendation written to the question with who gave it and where its records are, and none obtained said "
            + "so, a bar that could not be read among the reasons; what every advice spent, an ended one's among them, is "
            + "counted against the run's budget; and where the orchestrator names no advisor the question says so plainly. "
            + "The advisor takes no question of a step that may ask (its agent recommends within its own), nor a pause step's "
            + "that shows an upstream step's questions (they carry their recommendations), nor one the run's input answers, "
            + "nor the budget's own, nor a kind its kinds leave out, and while the run is pausing the question says the "
            + "recommendation is sought at the resume. "
            + "build, test, run and check-mutations run their legs as the commands of those names do, in the worktree "
            + "of the agent their in names, else the main checkout: test with its filter, exclude and labels and run with its "
            + "inputs - a filter, an exclusion or a label that fills to nothing is not given - and check-mutations with its arms, "
            + "every arm the registry declares where it names none, and pending. Each answers passed, true where every leg passed; "
            + "exitCode, the command's; verdicts, each leg's own; red, the red legs alone - a leg that failed, with the phase that "
            + "did not pass, the ledger's line and the last lines it printed, and of a sweep each arm that violated, survived or "
            + "was unattributed, with its arm and its line; runDirectory, null where the run made none; and isolation, what the "
            + "work of its legs ran under. What its legs build, test and run is what the run's agents wrote, so the work of each "
            + "leg this machine runs - every phase it starts, a set-up's lines apart - runs inside the operating system's "
            + "isolation of the agents' work ('help agent-clients'); where this machine has none, agentPolicy.isolation "
            + $"required fails the step ({HarnessExit.ToolMissing}) with nothing run, and preferred runs its legs outside, each "
            + "leg's line saying so. A leg that works in a "
            + "copy - its own under .legs, or a host's - lets the step's hold on its tree go once its copy is made, or once it ends "
            + "without one, and a leg in the tree itself once it ends: the step's record then says copied. A run whose runner, or "
            + "a runner its run checks name, writes the tree holds write on it, and its legs work in the tree itself. A leg run "
            + "refused part way, cut off, or ended with a leg it placed left with no line ends the step with the run's own code; "
            + "one its legs ended - none could run, another run owns its records - is read by their verdicts. The step keeps the "
            + $"ledger in its directory ({OrchestratorLayout.StepLedgerFileName}) and, in an agent's worktree, the log of each red "
            + $"leg's phase that did not pass, whole, where this machine ran the leg ({OrchestratorLayout.StepLogsDirectoryName}/<leg>.<phase>{PhaseLogs.Extension}, "
            + "the phase named as its log is), each attempt's own.");
        builder.AppendLine();
        AppendWrapped(
            builder,
            "anchor-balance and anchor-citations run the checks of those names in the main checkout - the balance from its "
            + "base, the citations of its subject "
            + $"({string.Join(", ", AnchorCitationSubjects.Names)}; {AnchorCitationSubjects.Default} where it names none) - each held "
            + "to its rows or its paths where it names them: the anchors among those rows the change opened, closed and lost, a "
            + "problem at a line of one of them and every problem of a registry as a whole, or the citations in those files; a rows "
            + "or paths expression that reads nothing names nothing. Each answers passed and findings, each a line as the check "
            + "prints it. class-closure runs before every fold of an item, judging the work of the agent its in names: it "
            + "judges the classes the item's plan declares against the closing record its builder answered - every planned "
            + "site accounted for and the planned home kept or its move said (homeMoved), a deleted site no longer declaring "
            + "its symbol in the worktree (the graph's view of it, synced where the step starts, or its text), a site that "
            + "reads its home among the paths the agent changed (what its fold would write and what its earlier folds of the "
            + "run changed), every search a tool call of the item's stored streams that ran, however its program exited - a "
            + "search that found nothing exits 1 - and never one refused or whose input did not read, one of whose input's "
            + "texts holds its query, every neighbour measured by a measure call of the same streams - whichever client called it, whatever it "
            + "answered - whose own record "
            + $"({Agents.AgentMeasureSupport.RecordsDirectoryName}/<call id>/{Agents.AgentMeasureSupport.RecordFileName} of the "
            + "directory of the step whose stream holds it) can be read, names the neighbour's cell and answered its result, "
            + "failed for failed-fixed - the newest call of that measurement, the same leg, variant, filter and cell, to measure "
            + "anything answering passed: the call cited or a later one for passed, one whose record was written after the "
            + "failing call's for failed-fixed - or reasoned why not, every test of the pin declared, the arm one of the registry its "
            + "worktree holds and mutating "
            + "the home's own file or a reason the class has nothing to mutate, the universe holding every planned axis and "
            + "the claim naming each - and each home's touches since its base, the item's own commits left out, a record "
            + "without a home audit refused where the home was touched by other work; a class's paths - the plan's and the "
            + "record's, the paths changed, an arm's site and the touches - are read as the plan check reads them, either "
            + "separator, and compared as this machine compares paths; a fold it cannot weigh fails it with "
            + "the weighing's own code. Its problems are the record's (1, which close-class repairs) apart from a missing "
            + "home audit (2, which home-audit makes - a home touched by other work, or a class the item's ledger says a "
            + "review reopened a second time), and it answers passed, problems, hotspots - the homes touched hotspot "
            + "times or more - and touches. route-findings routes the review's findings by the class of each, keeping "
            + "the item's ledger (closure-ledger.json in its agent's directory) of how often each class was reopened: "
            + "every introduced finding - a defect the diff made - goes back in rework as itself, the reopened findings "
            + "of one class go back as one send-back carrying them all and graded by the worst of them, and a class "
            + "reopened a second time goes to homeAudit instead, for the next action is the home audit; it answers "
            + "rework, homeAudit and reopened, the count per class. ci-read reads the legs check-ci-legs reads, of its branch - the one checked out in the main checkout where "
            + "it names none - and its newest limit runs, answering passed, branch, matrixRan, legs and red, each leg as check-ci-legs --json "
            + "gives it. ci-wait waits for every check run and commit status the forge lists for its commit - the commit the run last "
            + $"pushed where it names none, refused ({HarnessExit.Refused}) where it pushed none - less those its ignore names - a job "
            + "by its own name, as its workflow names it, a status by its context - written out or with * for any run of characters, "
            + "reading them every pollSeconds "
            + $"({CiChecksRequest.DefaultPollSeconds}): a red one - one that concluded failure, a status of failure or error - ends it "
            + "at once, answered with its name, url and, of a job of a workflow run, the job, its workflow, the step that failed, "
            + "its leg as check-ci-legs classifies it, whether it overran its budget, and its whole log kept in the step's directory "
            + $"({OrchestratorLayout.StepLogsDirectoryName}/<job>.<id>{PhaseLogs.Extension}, the job's id telling two of one name apart) "
            + "given by reference in log, with how many lines it holds in lines and, in failing, the page of it where the job "
            + "failed - the one holding its last line GitHub marks an error (##[error]), else its last page - each line by its number, "
            + "both masked by the repository's runner secrets, and in unread why any of that could not be "
            + "read; every check ended green - success, neutral or skipped - on two reads running that found the same checks, at "
            + "its wait's end on the last alone, passes it; and no check within "
            + $"appearSeconds ({CiChecksRequest.DefaultAppearSeconds}) - or waitMinutes where that is shorter - checks still running at waitMinutes "
            + $"({CiChecksRequest.DefaultWaitMinutes}) or one ended neither green nor red asks (noChecks, checksPending, "
            + "checksUnsettled), never failing it; answered accept, it reads once more, waiting for nothing, and is done with the "
            + "checks as they are, its passed true only where they are green. A check that finds red is red: under failOn red - every check's default but "
            + "ci-read's, never - it fails with its check's code "
            + $"(an anchor finding {AnchorExit.Findings}, a red CI leg {CiExit.LegRed} and no matrix run {CiExit.MatrixDidNotRun}, a red "
            + $"check {CiExit.LegRed}), and under never it is done, its passed false. Every read of the forge is given the "
            + $"step's timeoutMinutes where it says one, else {ReportText.Limit(GhProcess.Limit)}, and never outlasts the step's attempt, "
            + $"which ends a read in flight with it: a ci-read's read past its limit ends the step incomplete ({HarnessExit.Incomplete}), "
            + "which runs it again; a ci-wait reads again where a read failed or passed its limit, saying each, for as long as "
            + "its failure can change - a dropped network, a server error and a rate limit however often they come, another "
            + "failure while it is new - and ends with that read's code where the same failure comes again, at once where no "
            + "read changes it (the forge refusing the credential, or saying what was asked is not there), or where its wait "
            + "is spent.");
        builder.AppendLine();
        AppendWrapped(
            builder,
            "fold, delete-agent, refresh and rebase make their verb's dry run first and read what it stood, typed, never its "
            + "code: sound, they apply; a run holding the tree the apply takes is waited out, as a held "
            + $"lock is ({HarnessExit.Refused} after the retries), and one holding only a copy a deletion removes is run again, as an "
            + $"incomplete step is ({HarnessExit.Incomplete} after them); a consent it lacks asks the question of its kind - "
            + "foldNewRows, foldLostRows, foldOverCells, foldTwoChanges - one at a time, a person's first, with the rows, the cells "
            + "or the paths it is about; refused for paths both trees changed whose merges conflict, and nothing else, it asks "
            + $"foldConflicts with them; refused for anything else, it fails ({HarnessExit.Refused}). A fold's paths are held to its "
            + "within, patterns of the paths it may write or delete in the main tree - never the registry files its rows change, "
            + "nor a path it hands the agent - and one outside them asks foldOutside with the paths. Answered allow or merge, the "
            + "dry run is made again and the consent given by name only where it names exactly what the question showed - "
            + "otherwise the question is asked again with what it names now, and a consent given before the trees moved is never "
            + "passed; answered retry, the step runs again, a new attempt. A fold's apply, and a deletion's, is held to exactly "
            + "what its dry run named: measured again under its hold and found to write anything else - the trees moved between "
            + "the two - it writes nothing; an apply of any of the four refused under its hold so is weighed again, pass after "
            + "pass, until it applies, or fails with what it said once its trees come back to what a pass weighed before. A "
            + "fold, and a deletion that folds, write each "
            + $"path they mean to write, with what the main tree holds there, to the run's ledger ({OrchestratorLayout.FoldsFileName}) "
            + "before the verb is called, and what they left after, written by the verb under the main tree's lock it wrote "
            + "under, before it lets that lock go, so a side-task's write made once it let go is never taken for the fold's, "
            + "whatever comes after - the verb's answer, a throw, a crash; a stop before the write leaves each path as it was "
            + "found. The one limit is a fold that dies inside its own hold, part way through its write: the next attempt reads "
            + "what it left from the tree, under that lock, and marks it readFromTree, for a side-task's write made since cannot "
            + "be told from the fold's; an intent an earlier release left unsettled, which does not say recordsWrite - its verb "
            + "recorded nothing of its write - is read so too. Both answer written, deleted, registries and rows; a run stopped with a fold in its ledger whose "
            + "change no commit took yet - a path it changed that no commit step of the run took, that git does not ignore and "
            + "that the main tree still holds otherwise than its HEAD, whoever commits it - is unfinished, as one with an agent "
            + "open is. A deletion run again once its agent is deleted - its "
            + "ending cut off before it was recorded - is done, with what its directory kept of its apply. A refresh refused for conflicting merges alone hands every "
            + "other path first, then asks foldConflicts of those it left; its paths expression yielding none, it is done, having "
            + "weighed nothing. A rebase moves the base whole or not at all. A refresh or a rebase of others works on every "
            + "other open agent of the run, in the order of their names - a2 before a10 - one at a time, each admitted on its own "
            + "against its own tree, retried on its own and recorded in the step's directory "
            + $"({OrchestratorLayout.StepTargetsDirectoryName}/<agent>/), one closed before its turn passed over; a target that "
            + "fails ends its own turn, and the step ends done, or failed with the code of the first that failed, once each has "
            + $"had its turn. Each step keeps what its dry run named and how its apply ended in its directory ({OrchestratorLayout.StepFoldFileName}).");
        builder.AppendLine();
        AppendWrapped(
            builder,
            "commit commits the folds upstream of it in the flow - every visit and iteration of them, never another work item's - "
            + "and no other work: each path one of them changed that no commit of the run took yet, holding what the newest fold "
            + "that names it left there, so a registry two items' folds wrote is taken whole. A fold upstream whose intent "
            + "nobody settled - a crash cut it off, and its step has not run again - leaves what it wrote untold, and fails the "
            + $"step ({HarnessExit.Refused}) naming it, nothing staged. With none owed it is done, "
            + "committed false, and runs no git at all. Otherwise it works on the branch the run started on alone: the main "
            + "checkout on another - a person checked one out while the run was paused - or its HEAD detached fails it "
            + $"({HarnessExit.Refused}) naming both, nothing staged, as it does push and pull-request. The index must be empty and "
            + "each path hold what its fold left there, and where either does not - a path edited and not committed, or its "
            + $"fold's change undone - the step fails ({HarnessExit.Refused}) naming each, nothing staged, for a commit of the rest "
            + "would be half a fold. A path git ignores is left out and marked so in the ledger, git asked again each time, and "
            + "one HEAD holds as the tree does - what its fold left, or a person's later change of it, told by the blob git would "
            + "store against HEAD's and never by git's status, which does not look at a path marked assume-unchanged or "
            + "skip-worktree - was committed by hand, and is marked taken by HEAD; one left to stage that the index marks so fails "
            + $"it ({HarnessExit.Refused}) naming each with its mark, since git add would not stage it. With anything left, its message is the "
            + "configuration's commit.template, read by the one brace grammar, each {variable} filled with the step's value for "
            + "it, else its default, where one is declared, and else the step's own message - none, or a blank one, fails it "
            + $"({HarnessExit.ConfigInvalid}) - and the rest is written to the ledger "
            + "as one staged set before the first git add, staged by explicit path, never with -A, committed once - the message "
            + $"written to the step's directory ({OrchestratorLayout.StepCommitMessageFileName}), with -s where commit.signOff - "
            + "and marked committed with each fold path it took; git refusing, a hook among the reasons, unstages it and fails "
            + $"the step ({HarnessExit.CommandFailed}). The commit is held to the set over the commit the branch named before "
            + "it: one that stands on anything but that commit alone - a hook moved the branch to a commit of its own, its "
            + "parent carrying anything - took more against it - a hook staged another file - or left a path of the set out "
            + $"fails the step ({HarnessExit.CommandFailed}) naming each, and is left as it is for a person to keep or undo, the "
            + "set staged in the ledger beside it. A set an attempt before left staged is settled first: committed where HEAD moved and holds each "
            + "of its paths as the set staged it, else unstaged and staged again whole. It answers committed, sha, paths and "
            + "ignored - each path its folds changed that git ignores, folded and never committed, which what the run's end "
            + "leaves for a person names too - run again once it has committed, the commit it made - and, where it ran git's "
            + "commit, isolation: git's commit and the repository's commit hooks run inside the operating system's isolation "
            + "of the agents' work ('help agent-clients'), and where this machine has none agentPolicy.isolation required fails "
            + $"it ({HarnessExit.ToolMissing}) before anything is staged, preferred runs them outside. What a run checked "
            + "is text any template reads: {run.ran}, every check that ended so far, a line each with where it ran and its "
            + "verdict, each leg's for a step that runs legs; {run.notRun}, each check not run yet, by its step's name, and each "
            + "skipped or never reached, with why; and {run.red}, each red result not seen green since by a later visit of the "
            + "same step - a red leg, with what its line said of the units its build failed where its own tool failed it, a "
            + "red check of CI, an anchor check's findings, a plan's problems, a failure - each 'none' "
            + "where it names nothing. The checks are the steps of type "
            + $"{string.Join(", ", WorkflowStepTypes.All.Where(type => type.Checks).Select(type => type.Type))} and a reason step "
            + "marked independent, whatever each is named. A commit's message and variables read {carried.ran}, "
            + "{carried.notRun} and {carried.red} besides: the same three of the checks upstream of it that ended since the "
            + "newest commit upstream of it was done, a check whose failure was passed over among those that did not check. "
            + "{run.leftover}, {run.assumptions} and {run.preExisting} - what the run's loops left, what its agents assumed (each "
            + "'<step>' assumed about <what>: <assumption> (<why>), joined by '; ') and the issues it found there before - say 'none' alike where they name nothing.");
        builder.AppendLine();
        AppendWrapped(
            builder,
            "push pushes the commit the run's commit steps recorded last, else the one the run started on - never what the "
            + "branch names by then, which a hook or a build working in the main checkout could have moved - to the branch of "
            + "the name the main checkout is on, on a remote - the step's remote, "
            + "else the one the run's branch goes to (the push step's, else the one the branch tracks, else origin, else the only "
            + "one) - as a fast-forward and never forced, and answers pushed - false where the remote's branch named that commit "
            + "already, so a push run again is done - remote, branch and sha, the commit a ci-wait that names none waits for - "
            + "and isolation: git's push and the repository's push hooks run inside the operating system's isolation of the "
            + "agents' work ('help agent-clients'), and where this machine has none agentPolicy.isolation required fails it "
            + $"({HarnessExit.ToolMissing}) with nothing pushed, preferred runs them outside. Every "
            + "push step that names a remote names the same one, the remote the run's branch goes to: a definition whose push "
            + "steps name two is refused. HEAD detached, the main checkout on another branch than the run started on, a run "
            + "that made no commit and started on none, no remote it can name and one the repository does not have each refuse it "
            + $"({HarnessExit.Refused}), nothing pushed; the remote refusing the push - its branch moved on since - fails it "
            + $"({HarnessExit.CommandFailed}) in git's words. A checkpoint push runs only where the workflow says pushCheckpoints, "
            + "and is skipped otherwise. pull-request opens the pull request of the branch the main checkout is on, on the "
            + "repository the address of the remote the run's branch goes to names - its host, owner and name, the host of one "
            + "over ssh as ssh's configuration names it (ssh -G, an alias for one account among several), an address of no forge "
            + "or a host ssh cannot name leaving gh to read the remotes itself - against its base, filled from the run's inputs "
            + "and its name, else "
            + "the remote's default branch, with its title filled and its body the file it names in the workflow's directory, "
            + "read and filled where the step starts as a template is and written to the step's directory "
            + $"({OrchestratorLayout.StepPullRequestBodyFileName}) for gh to read. One already open from that branch to that base "
            + "is answered as it is, created false, and never edited: a step run again opens no second. One open from a fork's "
            + "branch of the same name is none of the run's, as gh says of each (isCrossRepository). It answers number, url "
            + $"and created. A title blank once filled fails it ({HarnessExit.ConfigInvalid}), as a body that cannot be read or reads "
            + "nothing does; HEAD detached, the main checkout on another branch than the run started on, no remote, no base it "
            + $"can tell and a base that is its own branch each refuse it ({HarnessExit.Refused}), nothing opened; gh not there fails "
            + $"it {HarnessExit.ToolMissing}, and gh saying no {HarnessExit.CommandFailed}. The run never merges a pull request: "
            + "each one it opened or found is left for a person, by its address, in what the run's end says (leftForAPerson), "
            + "as is each path its folds changed that git ignores, never committed. The commands commit, push and pull-request run are a "
            + $"closed list - {string.Join("; ", PublisherCommands.All.Select(command => $"{command.Shown} ({ReportText.Limit(command.Limit)})"))} - "
            + "each given the step's timeoutMinutes where it says one, else its own; one past it is not done "
            + $"({HarnessExit.Incomplete}), and the step runs again, a commit's set left staged settled by its next attempt.");
        builder.AppendLine();
        AppendWrapped(
            builder,
            $"An expression reads names joined by &&, ||, !, ==, != and ?? with ( ) to group, 'empty(x)' for whether a read holds nothing, "
            + "'any(list, test)' for whether any member meets a test written with 'it' for the member and 'union(list, list, ...)' for the "
            + "lists joined, each value once in the order first read and a null read as none: every step, input and param it "
            + "names must be declared before it, every path it reads is typed by what holds it - a reason step's schema, the output a "
            + "built-in type answers, the engine's facts of a step, the run's own names - and every operator takes what it says: !, && and "
            + "|| a boolean, == and != no list and no object, any a list and a test that yields a boolean, union lists. A when, an until and a blocking "
            + "yield a boolean, and a forEach and a key that takes a list a list; how the run stands without its always steps "
            + $"({string.Join(", ", WorkflowExpressions.RunStatuses.Select(status => $"run.{status}"))}) is read by an always step alone. A placeholder in braces - "
            + "'{step.member}' - in a template key reads the same names, typed the same way, as one in a prompt does; one in any other key "
            + "is refused, and a pull request's base reads nothing but the run's inputs and run.name, the same for every pull request. "
            + "A brace meant literally is written twice, {{ and }} alike, another expander's ${...} is left as written, and a "
            + "placeholder that names nothing is refused. "
            + "Every template key of a step is filled where the step starts, and one that reads nothing fails it (12) before its runner "
            + "is asked. A prompt alone is filled further: a placeholder in it that names a step this run left no output - it was "
            + "skipped, or failed and was passed over - is filled with a sentence that says so and why, as (step 'ci' left no output: "
            + "it failed and was passed over: no run of the matrix was found), so the agent is told what it does not have; a member "
            + "of a step that did leave an output is not that case, and reads nothing as any other name does. "
            + "What only a prompt reads stands where its agent is asked it: {recommendations} in a reason step's prompt and the "
            + "advisor's - the bar the repository wrote, the orchestrator's recommendations file or its folder's own "
            + "recommendations.md, read live each time it is given so a change holds from the next question on, and a "
            + "sentence saying none was written where the folder has no one - and {text.<name>} beside it, a workflow's "
            + "texts (texts:): each name a markdown file of its own directory, read live as the bar is, the used "
            + "workflow's own first where the reading step is one of its and the run's where none between them "
            + "declares the name - {question.<name>} in the advisor's alone, {status.<name>} in the reporter's two - {status.question} in its "
            + "askPrompt alone - {update.<name>} in the prompts of the step that plans an update and of the steps of its scope, and "
            + "{piece.<name>} in a step of the work-item group; the advisor's and the reporter's prompts read no step by name, and a pull "
            + "request's body, which the engine fills and no agent reads, reads what a template key reads and no skill. "
            + $"A question gives only the texts the engine names ({Under("question", WorkflowExpressions.QuestionTexts)}), and so do the "
            + $"status ({Under("status", WorkflowExpressions.StatusTexts)}), a plan update ({Under("update", WorkflowExpressions.UpdateTexts)}) "
            + $"and a piece ({Under("piece", WorkflowExpressions.PieceTexts)}); the bar is one text, and so is each run fact "
            + $"({string.Join(", ", WorkflowExpressions.RunFacts)}), which a template reads and an expression does not. "
            + $"'{ToolPackage.Command} validate-workflow' checks the definition - the shape of every key, every name an expression, a "
            + "template key or a prompt reads, every file the definition names, each step's schema one the engine reads (each "
            + "problem naming the file, the step and the place in the schema; a step's start holds it there again) - and "
            + $"refuses ({HarnessExit.ConfigInvalid}) naming each problem, without running anything, as a "
            + "config.json that does not hold is refused. A schema may share a definition with the schema files beside it: a "
            + "$ref of the form plan.schema.json#/$defs/workItem names one, and where the schema is read - the check, a run, "
            + "a path typed by it - the definition is taken into the schema's own $defs, so the work-item shape the shipped "
            + "plan, self-audit and diagnosis schemas hold is one definition among them; a reference nothing beside the schema "
            + "answers is refused where the schema is checked, naming the file. Where config.json declares no workflow there is nothing to check, and it is "
            + $"refused ({HarnessExit.Refused}), saying that \"workflows\": [\"{WorkflowTemplates.ShippedWorkflowName}\"] declares the one init writes.");
        builder.AppendLine();
        builder.AppendLine("Worked examples, each a whole workflow as a file holds it: the two ways a step is raised");
        builder.AppendLine("by another step's ending - a repair that goes back to what failed, and a check that runs");
        builder.AppendLine("whenever a builder ends - and two loops a repository can build its own work around. Each");
        builder.AppendLine("holds as it is printed, read by the same reader that reads a workflow's own file.");

        AppendExample(
            builder,
            """
            # example: repair
            name: example-repair
            steps:
              - name: builder
                type: create-agent
                agent: implementer
              - name: implement
                type: reason
                agent: implementer
                access: write
                in: builder
                dependsOn: [builder]
                prompt: prompts/implement.md
                outputs: schemas/implement.schema.json
              - name: build
                type: build
                in: builder
                dependsOn: [implement]
                onFailure: stop
              - name: fix
                type: reason
                resume: implement
                on:
                  step: build
                  outcome: failure
                  codes: [20]
                then: build
                maxVisits: 3
                answers:
                  visitBound: fail
                prompt: prompts/fix.md
                outputs: schemas/implement.schema.json
            """,
            "When the build fails because a leg is red (20), fix goes on with the implementer's work and the run goes",
            "back to build. After three such visits the configuration answers for the run: the build's failure stands,",
            "and its onFailure decides - stop ends the run. Without the answers line the run asks a person there.");

        AppendExample(
            builder,
            """
            # example: smoke
            name: example-smoke
            steps:
              - name: builder
                type: create-agent
                agent: implementer
              - name: implement
                type: reason
                agent: implementer
                access: write
                in: builder
                dependsOn: [builder]
                prompt: prompts/implement.md
                outputs: schemas/implement.schema.json
              - name: smoke
                type: test
                in: builder
                on:
                  step: implement
                  outcome: end
            """,
            "Whenever implement ends, done or failed, its tests are run in its worktree - a check on the work whatever",
            "became of it.");

        AppendExample(
            builder,
            """
            # example: issues
            name: example-issues
            maxOpenAgents: 1
            steps:
              - name: issues
                steps:
                  - name: find
                    type: reason
                    agent: prioritizer
                    tier: high
                    effort: high
                    prompt: prompts/find-issues.md
                    outputs: schemas/issues.schema.json
                  - name: each
                    dependsOn: [find]
                    forEach: find.issues
                    steps:
                      - name: builder
                        type: create-agent
                        agent: implementer
                      - name: fix
                        type: reason
                        agent: implementer
                        tier: balanced
                        effort: high
                        access: write
                        in: builder
                        dependsOn: [builder]
                        prompt: prompts/fix-issue.md
                        outputs: schemas/implement.schema.json
                      - name: test
                        type: test
                        in: builder
                        dependsOn: [fix]
                        onFailure: continue
                      - name: fold
                        type: fold
                        of: builder
                        dependsOn: [test]
                        when: test.outcome == 'done'
                      - name: commit
                        type: commit
                        dependsOn: [fold]
                        message: '{item.title}'
                      - name: done
                        type: delete-agent
                        of: builder
                        discardUncommitted: true
                        dependsOn: [commit]
            """,
            "find answers a list of issues, the most pressing first, from wherever its prompt sends it: a backlog",
            "file, the repository's tracker read with the forge's own command line, what is red. each runs its steps",
            "once for every one. The workflow's maxOpenAgents: 1 lets one worktree be open at a time, so the issues",
            "are taken one after another, in the order of the list; it is a limit of the whole run, and no group",
            "takes it. The agent's step is named builder, since work is a name the engine keeps for a run fact. An",
            "issue whose test stays red is not folded, its worktree discarded, and the next is taken: the run ends",
            "done and lists the one it passed over.");

        AppendExample(
            builder,
            """
            # example: review
            name: example-review
            steps:
              - name: builder
                type: create-agent
                agent: implementer
              - name: implement
                type: reason
                agent: implementer
                access: write
                in: builder
                dependsOn: [builder]
                prompt: prompts/implement.md
                outputs: schemas/implement.schema.json
              - name: polish
                dependsOn: [implement]
                repeat:
                  until: empty(review.findings)
                  maxIterations: 3
                  decideAfter: review
                  leftover: review.findings
                answers:
                  loopBound: accept
                steps:
                  - name: review
                    type: reason
                    agent: reviewer-correctness
                    tier: high
                    effort: high
                    independent: true
                    prompt: prompts/review.md
                    outputs: schemas/review.schema.json
                  - name: rework
                    type: reason
                    resume: implement
                    dependsOn: [review]
                    when: '!empty(review.findings)'
                    prompt: prompts/rework.md
                    outputs: schemas/implement.schema.json
                  - name: retest
                    type: test
                    in: builder
                    dependsOn: [rework]
                    when: '!empty(review.findings)'
                  - name: refold
                    type: fold
                    of: builder
                    dependsOn: [retest]
                    when: '!empty(review.findings)'
                  - name: recommit
                    type: commit
                    dependsOn: [refold]
                    when: '!empty(review.findings)'
                    message: 'review fixes'
            """,
            "Each iteration reviews what is in the tree; where the review found something, the author's agent",
            "reworks it, it is tested, folded and committed, and the next iteration reviews it again. The loop ends",
            "when a review finds nothing, and it decides where its review ended (decideAfter): the until and the",
            "bound are read on what that review found, before the rework acts on it, so the last rework of a loop",
            "that ends at its bound is reviewed by the round after it, and what such a loop leaves is what its last",
            "review found, none of it acted on. A loop that decides at a step reads, in its until, its blocking and its",
            "leftover, only that step and the steps it waits for, which have ended there, and every other step of the",
            "iteration runs before it or after it - here each step after the review waits for it - so none is left",
            "running when the loop ends at its decision. At three iterations the configuration ends it as it stands",
            "and what is left is the loop's leftover.");

        return builder.ToString();

        static string Under(string root, IReadOnlyList<string> texts) => string.Join(", ", texts.Select(text => $"{root}.{text}"));
    }

    /// <summary>
    /// One worked example of the workflows topic: its yml printed whole, indented as the topic's examples are and
    /// opened by a comment line naming it, and the sentences that say what it does - the example readable where it
    /// is printed and holdable by a test that reads it back.
    /// </summary>
    /// <param name="builder">The topic's text.</param>
    /// <param name="yaml">The example, as a workflow's own file holds it.</param>
    /// <param name="says">What it does, one sentence each.</param>
    private static void AppendExample(StringBuilder builder, string yaml, params string[] says)
    {
        builder.AppendLine();

        foreach (var line in yaml.ReplaceLineEndings("\n").TrimEnd('\n').Split('\n'))
        {
            builder.AppendLine($"  {line}");
        }

        builder.AppendLine();

        foreach (var said in says)
        {
            builder.AppendLine(said);
        }
    }
}
