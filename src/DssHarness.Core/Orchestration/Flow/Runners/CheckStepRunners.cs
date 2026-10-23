using System.Globalization;
using System.Text.Json.Nodes;
using DssHarness.Core.Anchors;
using DssHarness.Core.Ci;
using DssHarness.Core.Execution;
using DssHarness.Core.FileSystem;
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

    /// <summary>The longest each read of the forge a step makes may take: its <c>timeoutMinutes</c>, else <see cref="GhProcess.DefaultLimit"/>.</summary>
    /// <param name="step">The step.</param>
    public static TimeSpan? ReadLimitOf(WorkflowStep step) => WorkflowStepTypes.TimeLimitOf(step) is { } minutes ? TimeSpan.FromMinutes(minutes) : null;
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

        var report = await legs.CheckAsync(
            tree,
            new CiLegsRequest(run.Step.Branch, run.Step.Limit ?? CiLegsRequest.DefaultLimit, []) { ReadLimit = CheckResults.ReadLimitOf(run.Step) },
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
        var step = run.Step;
        var request = new CiChecksRequest(commit)
        {
            Once = accepted,
            Appear = TimeSpan.FromSeconds(step.AppearSeconds ?? CiChecksRequest.DefaultAppearSeconds),
            Wait = TimeSpan.FromMinutes(step.WaitMinutes ?? CiChecksRequest.DefaultWaitMinutes),
            Poll = TimeSpan.FromSeconds(step.PollSeconds ?? CiChecksRequest.DefaultPollSeconds),
            Ignore = step.Ignore,
            ReadLimit = CheckResults.ReadLimitOf(step),
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
