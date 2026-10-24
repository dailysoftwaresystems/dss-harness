using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using DssHarness.Core.Output;
using DssHarness.Core.Results;

namespace DssHarness.Core.Ci;

/// <summary>Reads CI run and job metadata, and the checks of a commit, for the repository a command is standing in.</summary>
/// <remarks>
/// Every read may take <see cref="GhProcess.Limit"/>, and a read past it is not done (21), which a caller may ask
/// again.
/// </remarks>
public interface ICiJobSource
{
    /// <summary>The newest run ids of <paramref name="workflow"/> on <paramref name="branch"/>.</summary>
    /// <param name="repositoryRoot">The repository to ask about.</param>
    /// <param name="workflow">The workflow's name or file name.</param>
    /// <param name="branch">The branch.</param>
    /// <param name="limit">How many runs, newest first.</param>
    /// <param name="cancellationToken">Stops the child process.</param>
    /// <exception cref="HarnessException">The forge could not be asked, so CI was not read.</exception>
    Task<IReadOnlyList<long>> ListRunsAsync(
        string repositoryRoot,
        string workflow,
        string branch,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>One run, with every job and step it recorded.</summary>
    /// <param name="repositoryRoot">The repository to ask about.</param>
    /// <param name="runId">The run.</param>
    /// <param name="cancellationToken">Stops the child processes.</param>
    /// <exception cref="HarnessException">The run could not be read, so CI was not read.</exception>
    Task<CiRun> ReadRunAsync(string repositoryRoot, long runId, CancellationToken cancellationToken = default);

    /// <summary>Every check run the forge lists for <paramref name="commit"/>, the latest of each.</summary>
    /// <param name="repositoryRoot">The repository to ask about.</param>
    /// <param name="commit">The commit.</param>
    /// <param name="cancellationToken">Stops the child processes.</param>
    /// <exception cref="HarnessException">The checks could not be read, so CI was not read.</exception>
    Task<IReadOnlyList<CiCheckRun>> ListCheckRunsAsync(string repositoryRoot, string commit, CancellationToken cancellationToken = default);

    /// <summary>Every commit status the forge lists for <paramref name="commit"/>, the newest of each context.</summary>
    /// <param name="repositoryRoot">The repository to ask about.</param>
    /// <param name="commit">The commit.</param>
    /// <param name="cancellationToken">Stops the child processes.</param>
    /// <exception cref="HarnessException">The statuses could not be read, so CI was not read.</exception>
    Task<IReadOnlyList<CiCommitStatus>> ListStatusesAsync(string repositoryRoot, string commit, CancellationToken cancellationToken = default);

    /// <summary>The whole log of one job, as the forge keeps it.</summary>
    /// <param name="repositoryRoot">The repository to ask about.</param>
    /// <param name="jobId">The job.</param>
    /// <param name="cancellationToken">Stops the child process.</param>
    /// <exception cref="HarnessException">The log could not be read.</exception>
    Task<string> ReadJobLogAsync(string repositoryRoot, long jobId, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="ICiJobSource"/>
/// <remarks>
/// Read-only by construction: this issues GET requests through the forge's own command line, by
/// <see cref="GhProcess"/>, and nothing else. It never labels, re-runs or pushes, because re-triggering CI is a
/// person's call and a leg reported red is a stop to read rather than something to retry until it passes.
/// </remarks>
/// <param name="gh">Runs the forge's tool.</param>
public sealed class GhCiJobSource(GhProcess gh) : ICiJobSource
{
    /// <summary>What a read that did not answer leaves undone, as each of its refusals ends.</summary>
    private const string NotRead = "so CI was NOT read (this is not a pass)";

    /// <summary>How many entries one request asks for, which is the most the API will return at once.</summary>
    private const int PageSize = 100;

    /// <summary>Where a check that is a job of a workflow run points: the run's id, then the job's.</summary>
    private static readonly Regex JobAddress = new(@"/actions/runs/(?<run>\d+)/job/(?<job>\d+)", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private readonly GhProcess _gh = gh;

    public async Task<IReadOnlyList<long>> ListRunsAsync(
        string repositoryRoot,
        string workflow,
        string branch,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var output = await RunAsync(
            repositoryRoot,
            [
                "run", "list",
                "--workflow", workflow,
                "--branch", branch,
                "--limit", limit.ToString(CultureInfo.InvariantCulture),
                "--json", "databaseId",
            ],
            cancellationToken).ConfigureAwait(false);

        using var document = Parse(output, $"the run list for '{workflow}' on {branch}");

        return [.. document.RootElement
            .EnumerateArray()
            .Select(run => run.TryGetProperty("databaseId", out var id) && id.TryGetInt64(out var value) ? value : 0)
            .Where(id => id != 0)];
    }

    public async Task<CiRun> ReadRunAsync(
        string repositoryRoot,
        long runId,
        CancellationToken cancellationToken = default)
    {
        var id = runId.ToString(CultureInfo.InvariantCulture);

        var runOutput = await RunAsync(
            repositoryRoot,
            ["api", $"repos/:owner/:repo/actions/runs/{id}"],
            cancellationToken).ConfigureAwait(false);

        using var run = Parse(runOutput, $"run {id}");

        var jobs = await ReadAllAsync(
            repositoryRoot,
            $"repos/:owner/:repo/actions/runs/{id}/jobs",
            "jobs",
            $"the jobs of run {id}",
            "job",
            ReadJob,
            cancellationToken).ConfigureAwait(false);

        return new CiRun(
            runId,
            Text(run.RootElement, "head_sha"),
            Text(run.RootElement, "head_branch"),
            Text(run.RootElement, "created_at"),
            Text(run.RootElement, "conclusion"),
            jobs)
        {
            Workflow = Text(run.RootElement, "name"),
            WorkflowPath = Text(run.RootElement, "path"),
        };
    }

    public Task<IReadOnlyList<CiCheckRun>> ListCheckRunsAsync(
        string repositoryRoot,
        string commit,
        CancellationToken cancellationToken = default)
        => ReadAllAsync(
            repositoryRoot,
            $"repos/:owner/:repo/commits/{Uri.EscapeDataString(commit)}/check-runs",
            "check_runs",
            $"the check runs of {commit}",
            "check run",
            ReadCheckRun,
            cancellationToken);

    public Task<IReadOnlyList<CiCommitStatus>> ListStatusesAsync(
        string repositoryRoot,
        string commit,
        CancellationToken cancellationToken = default)
        => ReadAllAsync(
            repositoryRoot,
            $"repos/:owner/:repo/commits/{Uri.EscapeDataString(commit)}/status",
            "statuses",
            $"the statuses of {commit}",
            "status",
            ReadStatus,
            cancellationToken);

    public Task<string> ReadJobLogAsync(
        string repositoryRoot,
        long jobId,
        CancellationToken cancellationToken = default)
        => _gh.RunAsync(
            repositoryRoot,
            ["api", $"repos/:owner/:repo/actions/jobs/{jobId.ToString(CultureInfo.InvariantCulture)}/logs"],
            $"so the log of job {jobId.ToString(CultureInfo.InvariantCulture)} was NOT read",
            cancellationToken);

    /// <summary>
    /// Every entry of a listing, following the pages until the count the listing itself reports is reached.
    /// </summary>
    /// <param name="repositoryRoot">The repository to ask from.</param>
    /// <param name="address">The listing, without its page.</param>
    /// <param name="member">The member of each page that holds its entries.</param>
    /// <param name="subject">What is read, as a refusal names it.</param>
    /// <param name="entry">What one entry is, as a refusal counts them.</param>
    /// <param name="read">Reads one entry.</param>
    /// <param name="cancellationToken">Stops the requests.</param>
    /// <exception cref="HarnessException">
    /// Fewer entries came back than the listing says it has, or a page holds no list of them. A page that was never
    /// fetched is a leg, or a check, that cannot be red: a listing wider than one page would quietly lose the entries past
    /// its end, and the gate would report every one it did see as green; a page of another shape read as empty would
    /// say there were none.
    /// </exception>
    private async Task<IReadOnlyList<T>> ReadAllAsync<T>(
        string repositoryRoot,
        string address,
        string member,
        string subject,
        string entry,
        Func<JsonElement, T> read,
        CancellationToken cancellationToken)
    {
        var collected = new List<T>();
        var total = 0;

        for (var page = 1; ; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var output = await RunAsync(
                repositoryRoot,
                ["api", $"{address}?per_page={PageSize}&page={page}"],
                cancellationToken).ConfigureAwait(false);

            using var listed = Parse(output, subject);

            total = Count(listed.RootElement) ?? total;

            if (!listed.RootElement.TryGetProperty(member, out var array) || array.ValueKind != JsonValueKind.Array)
            {
                throw new HarnessException(
                    HarnessExit.CommandFailed,
                    $"{GhProcess.Executable} answered no '{member}' list for {subject}, {NotRead}.");
            }

            var entries = array.EnumerateArray().Select(read).ToList();

            collected.AddRange(entries);

            if (entries.Count < PageSize)
            {
                break;
            }
        }

        // Checked rather than assumed. The count the listing reports is the only statement of how many there were,
        // and a gate reading fewer than that is reading a subset it cannot name.
        if (total > collected.Count)
        {
            throw new HarnessException(
                HarnessExit.CommandFailed,
                $"{char.ToUpperInvariant(subject[0])}{subject[1..]} report {ReportText.Counted(total, entry)} and only {collected.Count} came back, so some were "
                + "never read. This is not a pass.");
        }

        return collected;
    }

    /// <summary>How many entries a page says the listing has in total.</summary>
    private static int? Count(JsonElement root)
        => root.TryGetProperty("total_count", out var count) && count.TryGetInt32(out var value) ? value : null;

    private static CiJob ReadJob(JsonElement job)
        => new(Text(job, "name") ?? string.Empty, Text(job, "conclusion"), ReadSteps(job))
        {
            Id = job.TryGetProperty("id", out var id) && id.TryGetInt64(out var value) ? value : null,
        };

    private static IReadOnlyList<CiStep> ReadSteps(JsonElement job)
    {
        if (!job.TryGetProperty("steps", out var steps) || steps.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return [.. steps.EnumerateArray().Select(step => new CiStep(
            Text(step, "name") ?? string.Empty,
            Text(step, "conclusion"),
            Moment(step, "started_at"),
            Moment(step, "completed_at")))];
    }

    /// <summary>
    /// One check run: a job of a workflow run where the address it points at says which - the forge's own jobs point at
    /// <c>/actions/runs/&lt;run&gt;/job/&lt;job&gt;</c> - and a check of its own otherwise.
    /// </summary>
    private static CiCheckRun ReadCheckRun(JsonElement check)
    {
        var url = Text(check, "html_url") ?? Text(check, "details_url") ?? string.Empty;
        var job = JobAddress.Match(Text(check, "details_url") ?? url);

        return new CiCheckRun(
            Text(check, "name") ?? string.Empty,
            Text(check, "status") ?? string.Empty,
            Text(check, "conclusion"),
            url,
            job.Success ? long.Parse(job.Groups["run"].Value, CultureInfo.InvariantCulture) : null,
            job.Success ? long.Parse(job.Groups["job"].Value, CultureInfo.InvariantCulture) : null);
    }

    private static CiCommitStatus ReadStatus(JsonElement status)
        => new(
            Text(status, "context") ?? string.Empty,
            Text(status, "state") ?? string.Empty,
            Text(status, "target_url") ?? string.Empty,
            Text(status, "description"));

    private static string? Text(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static DateTimeOffset? Moment(JsonElement element, string name)
        => Text(element, name) is { Length: > 0 } text
            && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var moment)
                ? moment
                : null;

    private static JsonDocument Parse(string output, string subject)
    {
        try
        {
            return JsonDocument.Parse(output);
        }
        catch (JsonException ex)
        {
            throw new HarnessException(
                HarnessExit.CommandFailed,
                $"{GhProcess.Executable} answered something that is not JSON for {subject}, so CI was NOT read "
                + $"(this is not a pass): {ex.Message}");
        }
    }

    private Task<string> RunAsync(string repositoryRoot, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        => _gh.RunAsync(repositoryRoot, arguments, NotRead, cancellationToken);
}
