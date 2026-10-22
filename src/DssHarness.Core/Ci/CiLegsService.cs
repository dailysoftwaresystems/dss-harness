using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DssHarness.Core.Configuration;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Git;
using DssHarness.Core.Hosts;
using DssHarness.Core.Output;
using DssHarness.Core.Processes;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;

namespace DssHarness.Core.Ci;

/// <summary>What check-ci-legs was asked to read.</summary>
/// <param name="Branch">The branch, or null for the one checked out.</param>
/// <param name="Limit">How many runs of each workflow, newest first.</param>
/// <param name="Runs">Named runs, read in the order given, instead of the newest ones.</param>
public sealed record CiLegsRequest(string? Branch, int Limit, IReadOnlyList<long> Runs)
{
    /// <summary>Runs read when nothing says otherwise: the newest one.</summary>
    public const int DefaultLimit = 1;

    /// <summary>The longest each read of the forge may take; <see cref="GhProcess.DefaultLimit"/> where null.</summary>
    public TimeSpan? ReadLimit { get; init; }
}

/// <summary>Reads the CI verdict of each leg, and separates a real failure from a budget overrun.</summary>
public interface ICiLegsService
{
    /// <summary>Reads the runs <paramref name="request"/> names and reports each leg.</summary>
    /// <param name="startDirectory">A directory inside the repository.</param>
    /// <param name="request">Which branch, and which runs.</param>
    /// <param name="cancellationToken">Stops the child processes.</param>
    /// <exception cref="HarnessException">
    /// CI could not be read: the settings that say which jobs are legs are not all set, refused; no
    /// workflow is declared, the branch cannot be named, no run was found, or a run answered no job rows
    /// at all; or a pattern ran out of time, or a budget group captured something that is not a whole
    /// number of minutes, refused as configuration. None of these is a pass.
    /// </exception>
    Task<CiLegsReport> CheckAsync(
        string startDirectory,
        CiLegsRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads one run - the one a check of a commit is a job of - and classifies its legs as <see cref="CheckAsync"/>
    /// classifies every run it reads. A repository whose ci settings do not say which jobs are legs has none: the run's
    /// jobs are all there is, and none is classified.
    /// </summary>
    /// <param name="startDirectory">A directory inside the repository.</param>
    /// <param name="runId">The run.</param>
    /// <param name="readLimit">The longest each read of the forge may take; <see cref="GhProcess.DefaultLimit"/> where null.</param>
    /// <param name="cancellationToken">Stops the child processes.</param>
    /// <exception cref="HarnessException">
    /// The run could not be read; or a pattern ran out of time, or a budget group captured something that is not a whole
    /// number of minutes, refused as configuration.
    /// </exception>
    Task<CiRunReport> ClassifyRunAsync(
        string startDirectory,
        long runId,
        TimeSpan? readLimit,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="ICiLegsService"/>
/// <remarks>
/// <para>
/// Verdicts come from job metadata, one job at a time, and never from the run rollup: a run whose
/// matrix was skipped is red and says nothing whatever about the tree, and reading it as evidence is
/// the mistake this command exists to stop repeating.
/// </para>
/// <para>
/// Which jobs are legs, what a leg is called, and which steps build and test it are what the
/// repository's ci settings declare, and nothing else: no forge fixes a leg's job name or a step's,
/// and a command that assumed one workflow's would read every other as having no legs at all.
/// </para>
/// <para>
/// The budget has three sources, in this order: the job's own name, which the forge spells the matrix
/// values into and which is authoritative for the run that produced it; the workflow file in the
/// working tree, because the forge cuts a long job name short and the leg nearest its cap is exactly
/// the one whose name is longest; and <c>ci.legBudgetMinutes</c>, last so a run's own value always
/// wins. If none of the three answers, the leg is NOT classified: a discriminator that invents its
/// denominator is worse than one that says it has none.
/// </para>
/// </remarks>
public sealed class CiLegsService(
    IHarnessContextLoader contextLoader,
    ICiJobSource jobSource,
    IGitClient gitClient,
    IFileSystem fileSystem) : ICiLegsService
{
    /// <summary>The directory a repository keeps its workflows in when configuration names none.</summary>
    public const string WorkflowsDirectory = ".github/workflows";

    /// <summary>Fraction of its budget a green leg may reach before the budget is worth re-deriving.</summary>
    public const double WarningFraction = 0.8;

    /// <summary>How long one of the repository's patterns may take against one job's name or one workflow's text.</summary>
    private static readonly TimeSpan MatchBudget = TimeSpan.FromSeconds(1);

    /// <summary>A workflow's own name, which is what the forge lists its runs under.</summary>
    private static readonly Regex WorkflowName =
        new(@"^name:\s*(?<name>.+?)\s*$", RegexOptions.CultureInvariant | RegexOptions.Multiline);

    private readonly IHarnessContextLoader _contextLoader = contextLoader;
    private readonly ICiJobSource _jobSource = jobSource;
    private readonly IGitClient _gitClient = gitClient;
    private readonly IFileSystem _fileSystem = fileSystem;

    public async Task<CiLegsReport> CheckAsync(
        string startDirectory,
        CiLegsRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var context = await _contextLoader.LoadAsync(startDirectory, cancellationToken).ConfigureAwait(false);
        var root = context.Layout.RepositoryRoot;
        var conventions = Conventions.From(context.Config.Ci);

        var workflows = ReadWorkflows(root, context.Config.Ci);
        var branch = await ResolveBranchAsync(root, request.Branch, cancellationToken).ConfigureAwait(false);

        var reports = new List<CiRunReport>();

        foreach (var workflow in workflows)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var runs = request.Runs.Count > 0
                ? request.Runs
                : await _jobSource
                    .ListRunsAsync(root, workflow.Identifier, branch, request.Limit, request.ReadLimit, cancellationToken)
                    .ConfigureAwait(false);

            if (runs.Count == 0)
            {
                throw new HarnessException(
                    HarnessExit.Refused,
                    $"No run of '{workflow.Identifier}' was found for branch {branch}, so CI was NOT read. "
                    + "This is not a pass.");
            }

            foreach (var runId in runs)
            {
                var run = await _jobSource.ReadRunAsync(root, runId, request.ReadLimit, cancellationToken).ConfigureAwait(false);

                if (run.Jobs.Count == 0)
                {
                    // Fatal, never green. Zero job rows is indistinguishable from "every leg passed",
                    // and the two must never share an answer.
                    throw new HarnessException(
                        HarnessExit.CommandFailed,
                        $"Run {runId} answered no job rows at all, so nothing was verified. This is not a pass.");
                }

                reports.Add(Classify(run, workflow, context.Config.Ci, conventions));
            }

            if (request.Runs.Count > 0)
            {
                // Named runs are read once, not once per workflow: the caller already said which.
                break;
            }
        }

        return new CiLegsReport(branch, reports);
    }

    public async Task<CiRunReport> ClassifyRunAsync(
        string startDirectory,
        long runId,
        TimeSpan? readLimit,
        CancellationToken cancellationToken = default)
    {
        var context = await _contextLoader.LoadAsync(startDirectory, cancellationToken).ConfigureAwait(false);
        var root = context.Layout.RepositoryRoot;
        var settings = context.Config.Ci;
        var run = await _jobSource.ReadRunAsync(root, runId, readLimit, cancellationToken).ConfigureAwait(false);
        var name = run.Workflow ?? run.WorkflowPath ?? $"run {runId.ToString(CultureInfo.InvariantCulture)}";

        // Which jobs are legs is the repository's to say; where it says it in part or not at all, no job is one.
        if (settings.Unset().Count > 0)
        {
            return new CiRunReport(run, name, false, [.. run.Jobs.Select(job => $"{job.Name}={job.Conclusion ?? "?"}")], []);
        }

        // The run's own workflow file, where it is among the repository's, gives a leg a budget its job's name lost;
        // one that is not - a workflow ci.workflows leaves out - gives none, and the other sources answer alone.
        var workflow = WorkflowFiles(root, settings)
            .Where(path => _fileSystem.FileExists(path))
            .Select(path => ReadWorkflow(root, path))
            .FirstOrDefault(declared => string.Equals(declared.Path, run.WorkflowPath, StringComparison.Ordinal))
            ?? new CiWorkflow(name, string.Empty, run.WorkflowPath ?? string.Empty);

        return Classify(run, workflow, settings, Conventions.From(settings));
    }

    private static CiRunReport Classify(CiRun run, CiWorkflow workflow, CiSettings settings, Conventions conventions)
    {
        var legs = run.Jobs
            .Select(job => (Job: job, Recognized: conventions.Recognize(job.Name)))
            .Where(candidate => candidate.Recognized is not null)
            .Select(candidate => Classify(candidate.Job, candidate.Recognized!.Value, workflow, settings, conventions))
            .ToList();

        return new CiRunReport(
            run,
            workflow.Identifier,
            legs.Count > 0,
            [.. run.Jobs.Select(job => $"{job.Name}={job.Conclusion ?? "?"}")],
            legs);
    }

    private static CiLegOutcome Classify(
        CiJob job,
        (string Leg, int? BudgetMinutes) recognized,
        CiWorkflow workflow,
        CiSettings settings,
        Conventions conventions)
    {
        var (leg, fromName) = recognized;
        var (buildName, testName) = (conventions.BuildStep, conventions.TestStep);
        var build = job.Step(buildName);
        var test = job.Step(testName);

        // Only failure is failure. A cancelled, timed-out, neutral or skipped job is a different fact
        // with a different remedy, and calling it a failure sends a reader after code that never ran.
        var success = CiConclusions.Of(job.Conclusion) != CiCheckState.Red;

        var (budget, source) = Budget(fromName, leg, workflow, settings, conventions);
        var elapsed = test?.Elapsed?.TotalSeconds;

        var errors = new List<string>();
        var warnings = new List<string>();
        var overran = false;
        var unclassified = false;

        if (!success)
        {
            if (test is { } step && step.Failed)
            {
                if (elapsed is { } seconds && budget is { } cap)
                {
                    // At or over the cap the failure may be the budget rather than the code, so it is
                    // worded as an overrun. The leg stays red either way: the label says which
                    // question to ask about it, never that there is no question.
                    overran = seconds >= cap;

                    errors.Add(overran
                        ? $"the {testName} step failed at {Seconds(seconds)} of a {Seconds(cap)} budget ({source}): "
                        + "read this as a possible budget overrun, not a test failure. Re-derive the budget before raising it."
                        : $"the {testName} step failed after {Seconds(seconds)} of a {Seconds(cap)} budget ({source}), "
                        + "before reaching the cap: a real test failure. Fix it; do not raise the cap.");
                }
                else
                {
                    unclassified = true;
                    errors.Add(
                        $"the {testName} step failed, and this leg has no budget to measure it against, so it is not "
                        + "classified: a discriminator that invents its denominator is worse than one that says it has none.");
                }
            }

            if (build is { } buildStep && buildStep.Failed)
            {
                // A build failure is never an overrun: a budget is spent running tests, so it cannot
                // explain a failure that happened before any ran.
                errors.Add($"the {buildName} step failed, which no budget explains.");
            }

            if (errors.Count == 0)
            {
                errors.Add($"the job concluded '{job.Conclusion ?? "nothing"}' without either named step failing.");
            }
        }
        else if (test is { Conclusion: CiConclusions.Success }
                 && elapsed is { } green
                 && budget is { } cap
                 && green > cap * WarningFraction)
        {
            warnings.Add(
                $"green, but the {testName} step took {Seconds(green)} of a {Seconds(cap)} budget ({source}); "
                + "re-derive the budget before it reds.");
        }

        return new CiLegOutcome(leg, job.Name, success, errors, overran, unclassified, warnings, elapsed, budget, source);
    }

    private static (double? Seconds, string? Source) Budget(
        int? fromName,
        string leg,
        CiWorkflow workflow,
        CiSettings settings,
        Conventions conventions)
    {
        if (fromName is { } minutes)
        {
            return (minutes * 60.0, "job name");
        }

        if (conventions.WorkflowBudget is { } pattern && workflow.BudgetMinutes(leg, pattern) is { } fromFile)
        {
            return (fromFile * 60.0, "workflow");
        }

        return settings.LegBudgetMinutes > 0
            ? (settings.LegBudgetMinutes * 60.0, "ci.legBudgetMinutes")
            : (null, null);
    }

    private static string Seconds(double value) => $"{value.ToString("0", CultureInfo.InvariantCulture)}s";

    /// <summary>
    /// The workflows to read: the ones configuration names, or every file the conventional directory
    /// holds. An empty directory with nothing configured is a refusal, not an empty pass.
    /// </summary>
    private IReadOnlyList<CiWorkflow> ReadWorkflows(string root, CiSettings settings)
    {
        var workflows = new List<CiWorkflow>();

        foreach (var path in WorkflowFiles(root, settings))
        {
            if (!_fileSystem.FileExists(path))
            {
                throw new HarnessException(
                    HarnessExit.Refused,
                    $"ci.workflows names '{path}', which is not a file, so CI was NOT read. This is not a pass.");
            }

            workflows.Add(ReadWorkflow(root, path));
        }

        if (workflows.Count == 0)
        {
            throw new HarnessException(
                HarnessExit.Refused,
                $"No workflow was found: ci.workflows names none and '{WorkflowsDirectory}' holds none, so CI was "
                + "NOT read. This is not a pass.");
        }

        return workflows;
    }

    /// <summary>The workflow files the repository's CI is: the ones configuration names, or every file the conventional directory holds.</summary>
    private IReadOnlyList<string> WorkflowFiles(string root, CiSettings settings)
        => settings.Workflows is { Count: > 0 } named
            ? [.. named.Select(path => Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar)))]
            : Enumerate(Path.Combine(root, WorkflowsDirectory.Replace('/', Path.DirectorySeparatorChar)));

    /// <summary>One workflow file, read: its declared name, else its file name, its text and its path in the repository.</summary>
    private CiWorkflow ReadWorkflow(string root, string path)
    {
        // Each line as it reads, whatever ended it: a checkout that writes CRLF would otherwise keep a
        // budget pattern ending in $ from matching the line it describes.
        var text = LineText.Of(_fileSystem.ReadAllText(path));
        var declared = WorkflowName.Match(text);

        return new CiWorkflow(
            declared.Success ? declared.Groups["name"].Value.Trim('\'', '"') : Path.GetFileName(path),
            text,
            Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/'));
    }

    private IReadOnlyList<string> Enumerate(string directory)
        => _fileSystem.DirectoryExists(directory)
            ? [.. _fileSystem.EnumerateFiles(directory, recursive: false)
                .Where(path => path.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)
                    || path.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase))
                .Order(StringComparer.Ordinal)]
            : [];

    private async Task<string> ResolveBranchAsync(string root, string? given, CancellationToken cancellationToken)
    {
        if (given is { Length: > 0 })
        {
            return given;
        }

        if (await _gitClient.GetBranchAsync(root, cancellationToken).ConfigureAwait(false) is not { Length: > 0 } branch)
        {
            throw new HarnessException(
                HarnessExit.Refused,
                "There is no branch to ask about (a detached HEAD?), so CI was NOT read. Name one with --branch, "
                + "or name the runs with --run. This is not a pass.");
        }

        return branch;
    }

    /// <summary>What <paramref name="pattern"/> finds in <paramref name="text"/>, refused where it could not be evaluated.</summary>
    /// <param name="pattern">One of the repository's patterns.</param>
    /// <param name="text">A job's name, or a workflow's text.</param>
    /// <param name="setting">Where the pattern is set, as the refusal names it.</param>
    private static Match Matched(Regex pattern, string text, string setting)
    {
        try
        {
            return pattern.Match(text);
        }
        catch (RegexMatchTimeoutException ex)
        {
            // Never read as no match: a job left out because its pattern could not be evaluated would be a
            // leg missing from the answer, and a leg missing from the answer is how a red one reads as green.
            throw new HarnessException(
                HarnessExit.ConfigInvalid,
                $"{setting} took longer than {MatchBudget.TotalSeconds:0}s, so whether it matched is unknown: it is "
                + "written in a form that backtracks. CI was NOT read. This is not a pass.",
                ex);
        }
    }

    /// <summary>The whole number of minutes a budget group captured, or <see langword="null"/> where it captured nothing.</summary>
    /// <param name="group">The pattern's <c>budget</c> group.</param>
    /// <param name="setting">The setting the pattern is.</param>
    /// <param name="from">What it was matched against, as a refusal names it.</param>
    /// <exception cref="HarnessException">
    /// It captured something that is not a whole number of minutes: refused, never read as no budget, which would have
    /// the next source's answer for it without a word, and a pattern that reads every budget wrong pass unnoticed.
    /// </exception>
    private static int? Minutes(Group group, string setting, string from)
    {
        if (!group.Success || group.Value.Length == 0)
        {
            return null;
        }

        return int.TryParse(group.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var minutes)
            ? minutes
            : throw new HarnessException(
                HarnessExit.ConfigInvalid,
                $"{setting}'s budget group captured '{group.Value}' from {from}, which is not a whole number of minutes, "
                + "so no budget it reads can be trusted: make the group capture the digits alone. CI was NOT read. "
                + "This is not a pass.");
    }

    /// <summary>
    /// How this repository's workflows name their legs and their steps, as its ci settings declare them.
    /// </summary>
    /// <param name="LegJob">What a leg's job name matches: <see cref="CiSettings.LegJobPattern"/>.</param>
    /// <param name="BuildStep">The step a leg builds in.</param>
    /// <param name="TestStep">The step a leg tests in.</param>
    /// <param name="WorkflowBudget">Where a workflow gives a leg's budget, or <see langword="null"/>.</param>
    private sealed record Conventions(Regex LegJob, string BuildStep, string TestStep, string? WorkflowBudget)
    {
        /// <summary>The conventions <paramref name="settings"/> declare.</summary>
        /// <exception cref="HarnessException">One the command cannot do without is not set.</exception>
        public static Conventions From(CiSettings settings)
        {
            var missing = settings.Unset();

            if (missing.Count > 0)
            {
                throw new HarnessException(
                    HarnessExit.Refused,
                    "check-ci-legs reads legs by the names this repository's workflows give their jobs and steps, and "
                    + $"{string.Join(", ", missing)} {(missing.Count == 1 ? "is" : "are")} not set in config.json's ci "
                    + $"section, so CI was NOT read. This is not a pass. Run '{ToolPackage.Command} help ci' for what each says.");
            }

            return new Conventions(
                new Regex(settings.LegJobPattern!, RegexOptions.CultureInvariant, MatchBudget),
                settings.BuildStep!,
                settings.TestStep!,
                settings.WorkflowBudgetPattern);
        }

        /// <summary>
        /// The leg a job is, with the budget its name carries, or <see langword="null"/> for a job that is no
        /// leg: one the pattern does not match, or whose leg it captured empty - a label check, a job that
        /// gathers the others, another job entirely, whose verdict is about something else.
        /// </summary>
        /// <param name="jobName">The job's name, as the forge reports it.</param>
        public (string Leg, int? BudgetMinutes)? Recognize(string jobName)
            => Matched(LegJob, jobName, "ci.legJobPattern") is { Success: true } match
                && match.Groups["leg"].Value.Trim() is { Length: > 0 } leg
                    ? (leg, Minutes(match.Groups["budget"], "ci.legJobPattern", $"job '{jobName}'"))
                    : null;
    }

    /// <summary>One workflow file: what the forge lists its runs under, its own text, and where it is.</summary>
    /// <param name="Identifier">The workflow's declared name, or its file name when it declares none.</param>
    /// <param name="Text">The file, read once and searched for a leg's budget when a job name lost it.</param>
    /// <param name="Path">The file, relative to the repository with '/' between its parts, as the forge names a run's workflow.</param>
    private sealed record CiWorkflow(string Identifier, string Text, string Path)
    {
        /// <summary>
        /// The budget the file gives <paramref name="leg"/>, in minutes, as <paramref name="pattern"/> finds it, or null.
        /// </summary>
        /// <param name="leg">The leg's name, put in the pattern's <see cref="CiSettings.LegPlaceholder"/> as itself.</param>
        /// <param name="pattern">Where the workflow gives a leg's budget: <see cref="CiSettings.WorkflowBudgetPattern"/>.</param>
        /// <remarks>
        /// Read only because the forge cuts a long job name short, budget and all, and the leg with the
        /// longest name is the one running nearest its cap -- an instrument whose warning cannot fire on
        /// that leg is answering the adjacent question.
        /// </remarks>
        public int? BudgetMinutes(string leg, string pattern)
        {
            var named = new Regex(
                pattern.Replace(CiSettings.LegPlaceholder, Regex.Escape(leg), StringComparison.Ordinal),
                RegexOptions.CultureInvariant | RegexOptions.Multiline,
                MatchBudget);

            return Matched(named, Text, "ci.workflowBudgetPattern") is { Success: true } match
                ? Minutes(match.Groups["budget"], "ci.workflowBudgetPattern", $"workflow '{Identifier}'")
                : null;
        }
    }
}

/// <summary>Turns a CI legs report into what check-ci-legs prints.</summary>
public static class CiLegsReports
{
    /// <summary>What check-ci-legs reports.</summary>
    /// <param name="report">What was measured.</param>
    /// <param name="json">Whether to write the per-leg document as JSON instead of as lines.</param>
    public static CommandOutcome Render(CiLegsReport report, bool json)
    {
        ArgumentNullException.ThrowIfNull(report);

        IReadOnlyList<string> data = json ? [Document(report).ToJsonString(ReportJson.Options)] : Text(report);

        if (report.MatrixNeverRan)
        {
            // Its own answer, never a pass: the matrix is absent or skipped, so the run says nothing
            // about the tree, and reporting that as green is what this command exists to stop.
            return CommandOutcome.Failed(
                CiExit.MatrixDidNotRun,
                $"no job in {ReportText.Counted(report.Runs.Count, "run")} of branch {report.Branch} is a leg - the matrix did not run, or "
                + "ci.legJobPattern matches none of its jobs - so nothing was verified about the tree. This is not a pass.") with
            { Data = data };
        }

        if (report.Red.Count == 0)
        {
            return CommandOutcome.Ok($"every leg is green: {ReportText.Counted(report.Legs.Count, "leg")} over {ReportText.Counted(report.Runs.Count, "run")}") with
            { Data = data, Quiet = json };
        }

        return CommandOutcome.Failed(
            CiExit.LegRed,
            $"{ReportText.Counted(report.Red.Count, "leg")} red: {ReportText.Counted(report.RealFailures.Count, "real failure")} and "
            + ReportText.Counted(report.Overran.Count, "possible budget overrun")
            + (report.Unclassified.Count > 0
                ? $", besides {report.Unclassified.Count} whose test step failed with no budget to measure it against"
                : string.Empty)
            + ". The two have opposite remedies: fix the first, "
            + "re-derive the second. Reproduce a red leg locally; the logs behind these verdicts expire.") with
        { Data = data };
    }

    private static List<string> Text(CiLegsReport report)
    {
        var lines = new List<string>();

        foreach (var run in report.Runs)
        {
            lines.Add(
                $"run {run.Run.Id}  {run.Workflow}  {run.Run.Branch ?? report.Branch}  "
                + $"{Short(run.Run.HeadSha)}  {run.Run.CreatedAt}");

            if (!run.MatrixRan)
            {
                lines.Add("  no job here is a leg - the matrix did not run, or ci.legJobPattern matches none of these - so this run says nothing about the tree");
                lines.AddRange(run.JobsPresent.Select(job => $"    {job}"));
                continue;
            }

            foreach (var leg in run.Legs)
            {
                lines.Add($"  {leg.Leg,-26} {(leg.Success ? "green" : "RED"),-6} {Budget(leg)}");
                lines.AddRange(leg.Errors.Select(error => $"      {error}"));
                lines.AddRange(leg.Warnings.Select(warning => $"      {warning}"));
            }
        }

        return lines;
    }

    private static string Budget(CiLegOutcome leg)
    {
        var used = leg.TestSeconds is { } seconds ? $"{seconds.ToString("0", CultureInfo.InvariantCulture)}s" : "-";
        var cap = leg.BudgetSeconds is { } budget
            ? $"{budget.ToString("0", CultureInfo.InvariantCulture)}s cap ({leg.BudgetSource})"
            : "no budget";

        return $"{used} / {cap}";
    }

    private static string Short(string? sha) => sha is { Length: > 8 } ? sha[..8] : sha ?? "-";

    /// <summary>
    /// The document check-ci-legs answers with where it stops before it reads any run - a <c>--directory</c> that is not
    /// there, a usage error: its own, naming no branch and no run, after the code it exits with and the line it ends on.
    /// </summary>
    /// <param name="exitCode">The code the command exits with.</param>
    /// <param name="summary">The line it ends on.</param>
    public static string Stopped(int exitCode, string summary) => ReportJson.Stopped(exitCode, summary, Document(null));

    /// <summary>The one document of check-ci-legs --json: the branch and each run's legs; of no report, none of either.</summary>
    private static JsonObject Document(CiLegsReport? report)
    {
        var node = new JsonObject
        {
            ["branch"] = report?.Branch,
            ["runs"] = new JsonArray([.. (report?.Runs ?? []).Select(run => (JsonNode)new JsonObject
            {
                ["id"] = run.Run.Id,
                ["workflow"] = run.Workflow,
                ["headSha"] = run.Run.HeadSha,
                ["createdAt"] = run.Run.CreatedAt,
                ["matrixRan"] = run.MatrixRan,
                ["jobs"] = new JsonArray([.. run.JobsPresent.Select(job => (JsonNode?)JsonValue.Create(job))]),
                ["legs"] = new JsonArray([.. run.Legs.Select(leg => (JsonNode)LegDocument(leg))]),
            })]),
        };

        return node;
    }

    /// <summary>One leg as check-ci-legs --json gives it: its name and job, whether it succeeded, why not, and its budget.</summary>
    /// <param name="leg">What was found for the leg.</param>
    public static JsonObject LegDocument(CiLegOutcome leg)
    {
        ArgumentNullException.ThrowIfNull(leg);

        return new JsonObject
        {
            ["leg"] = leg.Leg,
            ["job"] = leg.Job,
            ["success"] = leg.Success,
            ["errors"] = new JsonArray([.. leg.Errors.Select(error => (JsonNode?)JsonValue.Create(error))]),
            ["overran"] = leg.Overran,
            ["unclassified"] = leg.Unclassified,
            ["warnings"] = new JsonArray([.. leg.Warnings.Select(warning => (JsonNode?)JsonValue.Create(warning))]),
            ["testSeconds"] = leg.TestSeconds,
            ["budgetSeconds"] = leg.BudgetSeconds,
            ["budgetSource"] = leg.BudgetSource,
        };
    }
}
