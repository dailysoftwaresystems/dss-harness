using System.Globalization;
using System.Text.Json.Nodes;
using DssHarness.Core.Anchors;
using DssHarness.Core.Application;
using DssHarness.Core.Ci;
using DssHarness.Core.Codebase;
using DssHarness.Core.Execution;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Git;
using DssHarness.Core.Output;
using DssHarness.Core.Platform;
using DssHarness.Core.Processes;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;
using DssHarness.Core.Runners;

namespace DssHarness.Core.Orchestration.Flow;

/// <summary>
/// What every check that runs no legs answers with: its output, whatever it found; done where it passed; and where it
/// found red, red with the code its check gives what it found, which <c>failOn</c> then reads.
/// </summary>
internal static class CheckResults
{
    /// <summary>A check that reached its verdict.</summary>
    /// <param name="passed">Whether it found nothing red.</param>
    /// <param name="redCode">The code its check gives what it found red.</param>
    /// <param name="red">What it found red, in a line; null where it passed.</param>
    /// <param name="output">Its output.</param>
    public static FlowStepResult Checked(bool passed, int redCode, string? red, JsonObject output)
        => new()
        {
            Asked = null,
            Passed = true,
            Red = !passed,
            Exit = passed ? HarnessExit.Success : redCode,
            Error = passed ? null : red,
            Output = output,
        };
}

/// <summary>
/// Runs an <c>anchor-balance</c> step: the balance <c>check-anchor-balance</c> measures, from the step's <c>base</c>, in
/// the main checkout - what a fold changed is there - held to its <c>rows</c> where it names them, answering
/// <c>{passed, findings}</c>, each finding a line as the check prints it.
/// </summary>
/// <param name="contextLoader">Finds the repository and its configuration.</param>
/// <param name="platform">This machine.</param>
/// <param name="balance">Measures the balance.</param>
public sealed class AnchorBalanceStepRunner(IHarnessContextLoader contextLoader, IHostPlatform platform, IAnchorBalanceService balance) : IFlowStepTypeRunner
{
    /// <inheritdoc/>
    public string Type => "anchor-balance";

    /// <inheritdoc/>
    public async Task<FlowStepResult> RunAsync(FlowStepRun run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);

        var (place, _) = await FlowStepRunners.PlaceOf(run, contextLoader, platform, cancellationToken).ConfigureAwait(false);
        var tree = place.Repository;

        var report = await balance.CheckAsync(tree, run.Step.Base, cancellationToken).ConfigureAwait(false);
        var held = FlowStepKeys.NamesOf(run.Step.Rows, run, "rows") is { } rows ? report.HeldTo(rows) : report;
        var findings = AnchorReports.BalanceFindings(held);

        return CheckResults.Checked(
            held.Passed,
            AnchorExit.Findings,
            $"the anchor balance found {ReportText.Counted(findings.Count, "finding")}",
            new JsonObject { ["passed"] = held.Passed, ["findings"] = new JsonArray([.. findings.Select(line => (JsonNode?)line)]) });
    }
}

/// <summary>
/// Runs an <c>anchor-citations</c> step: the check <c>check-anchor-citations</c> makes, of the step's <c>subject</c>, in
/// the main checkout - held to its <c>paths</c> where it names them - answering <c>{passed, findings}</c>, each citation
/// that resolves to no row a line as the check prints it.
/// </summary>
/// <param name="contextLoader">Finds the repository and its configuration.</param>
/// <param name="platform">This machine.</param>
/// <param name="citations">Checks the citations.</param>
public sealed class AnchorCitationsStepRunner(IHarnessContextLoader contextLoader, IHostPlatform platform, IAnchorCitationService citations) : IFlowStepTypeRunner
{
    /// <inheritdoc/>
    public string Type => "anchor-citations";

    /// <inheritdoc/>
    public async Task<FlowStepResult> RunAsync(FlowStepRun run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);

        var (place, _) = await FlowStepRunners.PlaceOf(run, contextLoader, platform, cancellationToken).ConfigureAwait(false);
        var tree = place.Repository;

        var report = await citations.CheckAsync(tree, AnchorCitationSubjects.Of(run.Step.Subject), cancellationToken).ConfigureAwait(false);
        var held = FlowStepKeys.NamesOf(run.Step.AnchorPaths, run, "paths") is { } paths ? report.HeldTo(paths) : report;

        return CheckResults.Checked(
            held.Passed,
            AnchorExit.Findings,
            $"{ReportText.Counted(held.Unresolved.Count, "citation")} of {held.SubjectDescription} resolve to no row",
            new JsonObject
            {
                ["passed"] = held.Passed,
                ["findings"] = new JsonArray([.. held.Unresolved.Select(citation => (JsonNode?)AnchorCitationReports.Line(citation))]),
            });
    }
}

/// <summary>
/// Runs a <c>class-closure</c> step: the classes the item's plan declares and the closing record its builder answered,
/// judged by <see cref="ClassClosureRules"/> before the fold - nothing is folded without a record that closes its
/// classes. What the record is judged against the runner gathers: the paths the agent changed - what the fold's own dry
/// weighing of its worktree would write, remove or merge, and what the run's ledger of its folds says its earlier folds
/// of the agent changed, so a site folded before a review is still the agent's work after it; every tool call of the
/// agent's stored streams, by the one reading of a stream's calls (<see cref="Agents.AgentStreamCalls"/>), and what each
/// of their measure calls measured, by its own record in the directory of the step whose stream holds it
/// (<see cref="ClassClosureMeasure"/>); what its
/// worktree still declares, through the graph's view of its tree - synced where the step starts - or text; the arms the
/// registry of its worktree declares, where an arm its work adds stands before the fold merges it in; and each home's
/// touches since the base the step names, the item's own commits left out by the run's fold ledger. A dry weighing that
/// could not be made fails the step with its own code and words, for nothing it would have answered is read as none.
/// Its answer is an output - <c>{passed, problems, hotspots, touches}</c> - and a failure of its own kind: the record's
/// problems (1), which <c>close-class</c> repairs, or a home audit it requires and lacks (2), which <c>home-audit</c>
/// makes.
/// </summary>
/// <param name="contextLoader">Finds the repository and its configuration, and the agent's worktree's.</param>
/// <param name="platform">This machine.</param>
/// <param name="store">Reads the agent's record, the item's stored streams and the run's fold ledger.</param>
/// <param name="fileSystem">Reads a worktree's files where the graph lists the file only.</param>
/// <param name="agents">Weighs the fold, for the paths the agent changed.</param>
/// <param name="graphs">The run's codebase graph, for the view of the agent's tree.</param>
/// <param name="gitClient">Counts each home's touches.</param>
/// <param name="listings">The one reading of the arms registry.</param>
public sealed class ClassClosureStepRunner(
    IHarnessContextLoader contextLoader,
    IHostPlatform platform,
    OrchestrationStore store,
    IFileSystem fileSystem,
    IAgentService agents,
    ICodebaseGraphService graphs,
    IGitClient gitClient,
    Mutations.ArmListingReader listings) : IFlowStepTypeRunner
{
    /// <inheritdoc/>
    public string Type => "class-closure";

    /// <inheritdoc/>
    public async Task<FlowStepResult> RunAsync(FlowStepRun run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);

        var (place, context) = await FlowStepRunners.PlaceOf(run, contextLoader, platform, cancellationToken).ConfigureAwait(false);

        if ((run.WorksIn ?? run.Target) is not { } agent)
        {
            return FlowStepResult.Ended(passed: false, HarnessExit.InternalError, $"'{run.Instance}' is a {Type} step, and the engine named no agent whose work it judges");
        }

        if (run.Read is not { } read || !read.TryGetValue("classes", out var given) || given.IsMissing || given.ToNode() is not JsonArray declared)
        {
            return FlowStepResult.Ended(passed: false, HarnessExit.ConfigInvalid, $"'{run.Instance}' checks the classes its classes yields, and it yields no list");
        }

        if (!read.TryGetValue("record", out var answered) || answered.IsMissing || answered.ToNode() is not JsonArray record)
        {
            record = [];
        }

        List<PlannedClass> planned = [.. declared.Select(PlannedClass.In).OfType<PlannedClass>()];
        List<ClassClosureRecord> entries = [.. record.Select(ClassClosureRecord.In).OfType<ClassClosureRecord>()];
        var layout = OrchestratorLayout.Of(context.Layout, place.Run);
        var worksOn = store.ReadAgent(layout, agent);
        var worktree = worksOn?.WorktreePath(context.Layout);

        // The paths the agent changed: what the fold itself would write now - one owner of that - and what the run's
        // earlier folds of the agent changed, which the fold no longer writes once the main tree holds them as it does.
        var weighed = await WeighedAsync(place, agent, cancellationToken).ConfigureAwait(false);

        if (weighed.Failed is { } failed)
        {
            return failed;
        }

        IReadOnlyList<string> changed =
        [
            .. weighed.Planned!.WouldWrite
                .Concat(weighed.Planned.Conflicting)
                .Concat(FoldLedger.Changed((store.ReadFolds(layout)?.Folds ?? []).Where(entry => entry.Settled && string.Equals(entry.Agent, agent, StringComparison.Ordinal))))
                .Distinct(StringComparer.Ordinal),
        ];

        // Every tool call of the agent's stored streams - each step of its own directory - by the one reading of a stream's
        // calls: paired with its answer by its id, its tool by the runtime's name, whichever client made it. Each measure
        // call, whatever it answered, with what its own record - kept in the directory of the step whose stream holds it -
        // says it measured.
        var calls = new Dictionary<string, Agents.AgentStreamCall>(StringComparer.Ordinal);
        var measures = new Dictionary<string, ClassClosureMeasure>(StringComparer.Ordinal);

        if (fileSystem.DirectoryExists(layout.AgentStepsDirectory(agent)))
        {
            foreach (var step in fileSystem.EnumerateDirectories(layout.AgentStepsDirectory(agent)))
            {
                foreach (var call in Agents.AgentStreamCalls.Answered(store.ReadStepStream(step)))
                {
                    if (call.Ok)
                    {
                        calls[call.Id] = call;
                    }

                    if (call.Tool == Agents.AgentMeasureSupport.ToolName)
                    {
                        measures[call.Id] = ClassClosureMeasure.Of(fileSystem, step, call.Id);
                    }
                }
            }
        }

        // The graph's view of the agent's tree, where the run made one - synced where the step started, so it is the tree
        // as the work left it: what a site's file still declares is read from it, or from the worktree's text where the
        // graph lists the file only.
        var view = await graphs.ReadOnlyAsync(layout, agent, cancellationToken).ConfigureAwait(false);
        var queries = view is null ? null : new CodebaseGraphQueries(view);

        bool Declares(string path, string symbol)
        {
            if (queries?.OutlineFile(path) is { } outlined && queries.UnsureOfFile(path) is null)
            {
                return outlined
                    .Where(card => CodeNodeKinds.Declarations.Contains(card.Kind, StringComparer.Ordinal))
                    .Any(card => string.Equals(card.Name, symbol, StringComparison.Ordinal)
                        || string.Equals(card.Qualified, symbol, StringComparison.Ordinal)
                        || card.Qualified.EndsWith("." + symbol, StringComparison.Ordinal));
            }

            return worktree is null
                ? false
                : fileSystem.FileExists(Path.Combine(worktree, path))
                    && FileText(Path.Combine(worktree, path)).Contains(symbol, StringComparison.Ordinal);
        }

        bool TestDeclared(string name)
        {
            if (queries?.Resolve(name) is GraphResolve.Found or GraphResolve.Candidates)
            {
                return true;
            }

            return worktree is not null
                && entries.SelectMany(entry => new[] { entry.HomePath }.Concat(entry.Sites.Select(site => site.Path)))
                    .Concat(changed)
                    .Distinct(StringComparer.Ordinal)
                    .Any(path => fileSystem.FileExists(Path.Combine(worktree, path)) && FileText(Path.Combine(worktree, path)).Contains(name, StringComparison.Ordinal));
        }

        string FileText(string path)
        {
            using var stream = fileSystem.OpenRead(path);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }

        // The arms the registry of the agent's worktree declares, where its configuration declares one: an arm the work
        // adds is there, and reaches the main tree's only through the fold - the registry alone, no leg's tree read.
        var tree = worktree is null ? context : await contextLoader.LoadAsync(worktree, cancellationToken).ConfigureAwait(false);
        var arms = await listings.DeclaredArmsAsync(tree, Type, cancellationToken).ConfigureAwait(false);

        List<string> homes = [.. entries.Select(entry => entry.HomePath).Distinct(StringComparer.Ordinal)];
        var touches = await TouchesAsync(place, layout, store, run, homes, cancellationToken).ConfigureAwait(false);

        var judged = ClassClosureRules.Judge(planned, entries, new ClassClosureProof(
            changed,
            calls,
            measures,
            Declares,
            TestDeclared,
            arms,
            TouchesByOthers: touches.ByOthers,
            TouchesAll: touches.All,
            TouchesRequired: run.Step.Touches ?? WorkflowStepTypes.DefaultTouches,
            HotspotAt: run.Step.Hotspot ?? WorkflowStepTypes.DefaultHotspot));

        var output = new JsonObject
        {
            ["passed"] = judged.Passed,
            ["problems"] = new JsonArray([.. judged.Problems.Concat(judged.Audits).Select(problem => (JsonNode?)problem)]),
            ["hotspots"] = new JsonArray([.. judged.Hotspots.Select(hotspot => new JsonObject
            {
                ["class"] = hotspot.Class,
                ["path"] = hotspot.Path,
                ["touches"] = hotspot.Touches,
            })]),
            ["touches"] = new JsonArray([.. judged.Touches.Select(touch => new JsonObject
            {
                ["class"] = touch.Class,
                ["path"] = touch.Path,
                ["byOthers"] = touch.ByOthers,
                ["all"] = touch.All,
            })]),
        };

        if (judged.Problems.Count > 0)
        {
            return CheckResults.Checked(false, ClassClosureExit.Findings, $"the class closure found {ReportText.Counted(judged.Problems.Count, "problem")}", output);
        }

        return judged.Audits.Count > 0
            ? CheckResults.Checked(false, ClassClosureExit.HomeAudit, $"the class closure requires {ReportText.Counted(judged.Audits.Count, "home audit")}", output)
            : CheckResults.Checked(true, ClassClosureExit.Findings, string.Empty, output);
    }

    /// <summary>
    /// The fold's own dry weighing of the agent's worktree, a step of a run asking it: what it plans, or - where it could not
    /// be weighed, its worktree gone or a tree it cannot read - the step's failure, with the weighing's own code and words.
    /// The check never reads a weighing it could not make as one that changes nothing: every site that reads its home would
    /// pass unchecked.
    /// </summary>
    private async Task<(AgentPlanned? Planned, FlowStepResult? Failed)> WeighedAsync(FlowRunPlace place, string agent, CancellationToken cancellationToken)
    {
        UseCaseResult<AgentVerbFacts> weighed;

        try
        {
            weighed = await agents.FoldAsync(
                new FoldAgentRequest(place.Repository, place.Run, agent, new FoldAllowances(), Apply: false) { ByRun = true },
                cancellationToken).ConfigureAwait(false);
        }
        catch (HarnessException refused)
        {
            return (null, FlowStepResult.Ended(passed: false, refused.ExitCode, $"the paths agent '{agent}' changed could not be weighed: {refused.Message}"));
        }

        return weighed.Facts?.Planned is { } planned
            ? (planned, null)
            : (null, FlowStepResult.Failed(
                weighed.Outcome.Succeeded ? new CommandOutcome(HarnessExit.InternalError, weighed.Outcome.Message) : weighed.Outcome,
                $"the paths agent '{agent}' changed could not be weighed: {weighed.Outcome.Message}"));
    }

    /// <summary>
    /// Each home's touches since the base: every commit the range holds counted, and the item's own commits - those the
    /// run's fold ledger attributes to instances of this item - left out of the count by others.
    /// </summary>
    private async Task<(IReadOnlyDictionary<string, int> ByOthers, IReadOnlyDictionary<string, int> All)> TouchesAsync(
        FlowRunPlace place,
        OrchestratorLayout layout,
        OrchestrationStore store,
        FlowStepRun run,
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken)
    {
        if (paths.Count == 0 || string.IsNullOrWhiteSpace(run.Step.Base))
        {
            return (new Dictionary<string, int>(), new Dictionary<string, int>());
        }

        var touches = await gitClient.CountTouchesAsync(place.Repository, run.Step.Base.Trim(), "HEAD", paths, cancellationToken).ConfigureAwait(false);
        var ledger = store.ReadFolds(layout);
        var item = FlowInstanceId.ItemOf(run.Instance);

        bool Own(string commit)
            => ledger?.Commits.FirstOrDefault(entry => string.Equals(entry.Sha, commit, StringComparison.Ordinal)) is { } made
                && made.Folds.Any(fold => ledger.Of(fold) is { } attributed && FlowInstanceId.ItemOf(attributed.Instance) == item);

        var byOthers = new Dictionary<string, int>(StringComparer.Ordinal);
        var all = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var touch in touches)
        {
            all[touch.Path] = all.TryGetValue(touch.Path, out var counted) ? counted + 1 : 1;

            if (!Own(touch.Commit))
            {
                byOthers[touch.Path] = byOthers.TryGetValue(touch.Path, out var others) ? others + 1 : 1;
            }
        }

        return (byOthers, all);
    }
}

/// <summary>
/// Runs a <c>ci-read</c> step: the legs <c>check-ci-legs</c> reads, of the step's <c>branch</c> - the one checked out in
/// the main checkout where it names none - and its newest <c>limit</c> runs, answering
/// <c>{passed, branch, matrixRan, legs, red}</c>, each leg as <c>check-ci-legs --json</c> gives it.
/// </summary>
/// <param name="contextLoader">Finds the repository and its configuration.</param>
/// <param name="platform">This machine.</param>
/// <param name="legs">Reads CI's legs.</param>
public sealed class CiReadStepRunner(IHarnessContextLoader contextLoader, IHostPlatform platform, ICiLegsService legs) : IFlowStepTypeRunner
{
    /// <inheritdoc/>
    public string Type => "ci-read";

    /// <inheritdoc/>
    public async Task<FlowStepResult> RunAsync(FlowStepRun run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);

        var (place, _) = await FlowStepRunners.PlaceOf(run, contextLoader, platform, cancellationToken).ConfigureAwait(false);
        var tree = place.Repository;

        // Each read of the forge is given the step's time limit where it says one, else gh's own; a read past it ends the
        // attempt incomplete, which runs it again.
        var report = await legs.CheckAsync(
            tree,
            new CiLegsRequest(run.Step.Branch, run.Step.Limit ?? CiLegsRequest.DefaultLimit, []) { ReadTimeLimit = WorkflowStepTypes.CommandLimitOf(run.Step) },
            cancellationToken).ConfigureAwait(false);
        var passed = !report.MatrixNeverRan && report.Red.Count == 0;

        return CheckResults.Checked(
            passed,
            report.MatrixNeverRan ? CiExit.MatrixDidNotRun : CiExit.LegRed,
            CiLegsReports.Render(report, json: false).Message,
            new JsonObject
            {
                ["passed"] = passed,
                ["branch"] = report.Branch,
                ["matrixRan"] = !report.MatrixNeverRan,
                ["legs"] = new JsonArray([.. report.Legs.Select(leg => (JsonNode)CiLegsReports.LegDocument(leg))]),
                ["red"] = new JsonArray([.. report.Red.Select(leg => (JsonNode)CiLegsReports.LegDocument(leg))]),
            });
    }
}

/// <summary>
/// Runs a <c>ci-wait</c> step: waits for the checks the forge runs on its <c>commit</c> - the one the run last pushed
/// where it names none - and says how each ended, as <see cref="ICiChecksService"/> reads them. None appearing,
/// some still running at its time, or one ended neither green nor red, asks; a red one is answered with where it failed,
/// and of a job its whole log kept in the step's directory and its last lines in <c>red[].tail</c>, both masked.
/// </summary>
/// <param name="contextLoader">Finds the repository and its configuration.</param>
/// <param name="platform">This machine.</param>
/// <param name="fileSystem">Keeps each red job's log in the step's directory.</param>
/// <param name="values">Reads the repository's runner secrets, which the logs are masked by.</param>
/// <param name="checks">Waits for the checks.</param>
public sealed class CiWaitStepRunner(
    IHarnessContextLoader contextLoader,
    IHostPlatform platform,
    IFileSystem fileSystem,
    ActionValuesReader values,
    ICiChecksService checks) : IFlowStepTypeRunner
{
    /// <inheritdoc/>
    public string Type => "ci-wait";

    /// <inheritdoc/>
    public async Task<FlowStepResult> RunAsync(FlowStepRun run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);

        var (place, context) = await FlowStepRunners.PlaceOf(run, contextLoader, platform, cancellationToken).ConfigureAwait(false);
        var tree = place.Repository;

        var commit = run.Step.Commit
            ?? (run.Read is { } read && read.TryGetValue("commit", out var pushed) ? pushed.ToNode()?.GetValue<string>() : null);

        if (commit is null)
        {
            return FlowStepResult.Ended(passed: false, HarnessExit.Refused, $"'{run.Instance}' names no commit, and the run has pushed none whose checks it could wait for. Nothing was read.");
        }

        // What it found is taken as it stands, where a person accepted it - no check, checks still running, one unsettled:
        // the checks are read once more, waiting for none, and the step is done with what they are now, its passed true
        // only where they are green; a red one is red all the same.
        var accepted = run.Answers.Count > 0 && run.Answers[^1].Answer == "accept";
        // Each read of the forge is given the step's time limit where it says one, else gh's own: one past it is read
        // again at the next poll, until CiChecksService.FailedReadsInARow fail running or the wait is spent; and a read
        // never outlasts the attempt, whose limit ends one in flight with it.
        var step = run.Step;
        var request = new CiChecksRequest(commit, Type)
        {
            ReadTimeLimit = WorkflowStepTypes.CommandLimitOf(step),
            Once = accepted,
            Appear = TimeSpan.FromSeconds(step.AppearSeconds ?? CiChecksRequest.DefaultAppearSeconds),
            Wait = TimeSpan.FromMinutes(step.WaitMinutes ?? CiChecksRequest.DefaultWaitMinutes),
            Poll = TimeSpan.FromSeconds(step.PollSeconds ?? CiChecksRequest.DefaultPollSeconds),
            Ignore = step.Ignore,
        };
        var reading = await checks.WaitAsync(tree, request, cancellationToken).ConfigureAwait(false);

        if (accepted && reading.Found != CiChecksFound.Red)
        {
            return FlowStepResult.Ended(passed: true, HarnessExit.Success, output: Output(reading.Found == CiChecksFound.Green, reading, []));
        }

        switch (reading.Found)
        {
            case CiChecksFound.None:
                return FlowStepResult.Found("none", $"no check appeared on {commit} within {Seconds(request.NoneAfter)}");
            case CiChecksFound.Pending:
                return FlowStepResult.Found("pending", $"{Named(reading.Pending)} still running on {commit} after {Seconds(request.Wait)}");
            case CiChecksFound.Unsettled:
                return FlowStepResult.Found("unsettled", $"{Named(reading.Unsettled)} on {commit} ended neither green nor red");
            case CiChecksFound.Green:
                return FlowStepResult.Ended(passed: true, HarnessExit.Success, output: Output(passed: true, reading, []));
        }

        var mask = await values.ReadAsync(context.Layout.RunnerEnvDirectory, context.Layout.RunnerSecretsDirectory, cancellationToken).ConfigureAwait(false);
        var logs = Path.Combine(OrchestratorLayout.Of(context.Layout, place.Run).InstanceDirectory(run.Instance, run.WorksIn), OrchestratorLayout.StepLogsDirectoryName);
        var red = new JsonArray();

        // The logs of this attempt's red checks alone: an attempt before it kept its own, of checks that may be green now.
        fileSystem.DeleteDirectory(logs);

        foreach (var check in reading.Red)
        {
            string? kept = null;
            IReadOnlyList<string> tail = [];

            // Named for its job and the job's id: two red jobs of one name - in two workflows, or named apart only by
            // case or by a character no path carries - would otherwise keep one file, each the other's log.
            if (check.Log is { } log && check.Check.JobId is { } jobId)
            {
                var masked = mask.Redact(log);
                kept = Path.Combine(logs, $"{PhaseLogs.NameFor(check.Job ?? check.Check.Name)}.{jobId.ToString(CultureInfo.InvariantCulture)}{PhaseLogs.Extension}");
                fileSystem.CreateDirectory(logs);
                fileSystem.WriteAllTextAtomic(kept, masked);
                tail = [.. LineText.Of(masked).TrimEnd('\n').Split('\n').TakeLast(PhaseResult.TailLines)];
            }

            red.Add(new JsonObject
            {
                ["name"] = check.Check.Name,
                ["job"] = check.Job,
                ["workflow"] = check.Workflow,
                ["step"] = check.Step,
                ["leg"] = check.Leg,
                ["overran"] = check.Overran,
                ["url"] = check.Check.Url,
                ["tail"] = new JsonArray([.. tail.Select(line => (JsonNode?)line)]),
                ["log"] = kept,
                ["conclusion"] = check.Check.Conclusion,
                ["description"] = check.Check.Description,
                ["unread"] = check.Unread,
            });
        }

        return CheckResults.Checked(
            passed: false,
            CiExit.LegRed,
            $"{Named(reading.Red.Select(check => check.Check).ToList())} red on {commit}",
            Output(passed: false, reading, red));
    }

    /// <summary>The step's output: whether its checks passed, the commit, each check, the red ones, and those still pending or unsettled.</summary>
    private static JsonObject Output(bool passed, CiChecksReading reading, JsonArray red)
        => new()
        {
            ["passed"] = passed,
            ["commit"] = reading.Commit,
            ["checks"] = new JsonArray([.. reading.Checks.Select(check => (JsonNode)new JsonObject
            {
                ["name"] = check.Name,
                ["state"] = State(check.State),
                ["conclusion"] = check.Conclusion,
                ["url"] = check.Url,
            })]),
            ["red"] = red,
            ["pending"] = new JsonArray([.. reading.Pending.Select(check => (JsonNode?)check.Name)]),
            ["unsettled"] = new JsonArray([.. reading.Unsettled.Select(check => (JsonNode?)check.Name)]),
        };

    /// <summary>A check's state as the output spells it.</summary>
    private static string State(CiCheckState state)
        => state switch
        {
            CiCheckState.Green => "green",
            CiCheckState.Red => "red",
            CiCheckState.Unsettled => "unsettled",
            _ => "pending",
        };

    /// <summary>Checks named in a line: their count, then each name.</summary>
    private static string Named(IReadOnlyList<CiCheck> checks)
        => $"{ReportText.Counted(checks.Count, "check")} ({string.Join(", ", checks.Select(check => check.Name))})";

    private static string Seconds(TimeSpan span) => $"{span.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)} s";
}
