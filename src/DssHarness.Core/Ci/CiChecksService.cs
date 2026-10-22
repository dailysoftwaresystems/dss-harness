using System.Text.RegularExpressions;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;

namespace DssHarness.Core.Ci;

/// <summary>What a wait for the checks of a commit is asked.</summary>
/// <param name="Commit">The commit whose checks it reads.</param>
public sealed record CiChecksRequest(string Commit)
{
    /// <summary>How long a wait waits for the first check to appear where it is told not: three minutes.</summary>
    public const int DefaultAppearSeconds = 180;

    /// <summary>How long a wait waits, from its start, for every check to end where it is told not: an hour.</summary>
    public const int DefaultWaitMinutes = 60;

    /// <summary>How often a wait reads the checks where it is told not: every thirty seconds.</summary>
    public const int DefaultPollSeconds = 30;

    /// <summary>How long it waits for the first check to appear.</summary>
    public TimeSpan Appear { get; init; } = TimeSpan.FromSeconds(DefaultAppearSeconds);

    /// <summary>How long it waits, from its start, for every check to end.</summary>
    public TimeSpan Wait { get; init; } = TimeSpan.FromMinutes(DefaultWaitMinutes);

    /// <summary>How often it reads the checks while it waits.</summary>
    public TimeSpan Poll { get; init; } = TimeSpan.FromSeconds(DefaultPollSeconds);

    /// <summary>How long it waits for the first check before it answers none: <see cref="Appear"/>, or <see cref="Wait"/> where that is shorter.</summary>
    public TimeSpan NoneAfter => Appear < Wait ? Appear : Wait;

    /// <summary>The checks it does not count, by name, each written out or with <c>*</c> for any run of characters.</summary>
    public IReadOnlyList<string> Ignore { get; init; } = [];

    /// <summary>The longest each read of the forge may take; <see cref="GhProcess.DefaultLimit"/> where null.</summary>
    public TimeSpan? ReadLimit { get; init; }

    /// <summary>
    /// Whether it reads the checks once, waiting for none to appear or end - what was reached taken as it stands, as a
    /// person's <c>accept</c> asks - and answers what they are then, each settled on that read alone. A read that fails is
    /// read again at the next poll as in any wait, up to <see cref="CiChecksService.FailedReadsInARow"/> running.
    /// </summary>
    public bool Once { get; init; }

    /// <summary>
    /// Whether <paramref name="name"/> is one of the checks <see cref="Ignore"/> leaves out: equal to one of its names,
    /// ordinally, or matching one written with <c>*</c>, which stands for any run of characters, none among them.
    /// </summary>
    /// <param name="name">
    /// The check's name: a job's own, as its workflow names it - never <c>workflow / job (event)</c>, as a pull request
    /// shows it - or a status's context.
    /// </param>
    public bool Ignores(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        return Ignore.Any(pattern => pattern.Contains('*', StringComparison.Ordinal)
            ? Regex.IsMatch(
                name,
                "^" + Regex.Escape(pattern).Replace(@"\*", ".*", StringComparison.Ordinal) + "$",
                RegexOptions.CultureInvariant | RegexOptions.Singleline,
                TimeSpan.FromSeconds(1))
            : string.Equals(pattern, name, StringComparison.Ordinal));
    }
}

/// <summary>What the wait found.</summary>
public enum CiChecksFound
{
    /// <summary>
    /// Every check ended, at least one, none red and none unsettled, on two reads running that found the same checks - at
    /// the end of the wait, or on a wait that reads once, on its last read alone.
    /// </summary>
    Green,

    /// <summary>A check is red.</summary>
    Red,

    /// <summary>No check appeared in the time it waits for the first, or in its whole wait where that is shorter.</summary>
    None,

    /// <summary>Checks were still running when its time to wait was spent.</summary>
    Pending,

    /// <summary>
    /// Every check ended, none red, and one ended neither green nor red, on two reads running that found the same checks -
    /// at the end of the wait, or on a wait that reads once, on its last read alone.
    /// </summary>
    Unsettled,
}

/// <summary>One check of a commit, a check run or a commit status, as the wait reads it.</summary>
/// <param name="Name">Its name: a check run's, or a status's context.</param>
/// <param name="State">What it came to.</param>
/// <param name="Conclusion">How it ended - a check run's conclusion, a status's state - or null where it has not.</param>
/// <param name="Url">Its page, or empty where it names none.</param>
/// <param name="RunId">The workflow run it is a job of, or null.</param>
/// <param name="JobId">The job it is, or null.</param>
/// <param name="Description">The text the forge shows for a status, or null.</param>
public sealed record CiCheck(string Name, CiCheckState State, string? Conclusion, string Url, long? RunId, long? JobId, string? Description);

/// <summary>A red check, with what the forge says of where it failed.</summary>
/// <param name="Check">The check.</param>
/// <param name="Job">The job it is, where it is a job of a workflow run; null otherwise.</param>
/// <param name="Workflow">The workflow that run is of, or null.</param>
/// <param name="Step">The step of the job that failed, or null where none says so.</param>
/// <param name="Leg">The leg the job is, as <see cref="ICiLegsService"/> classifies it, or null where it is none.</param>
/// <param name="Overran">Whether that leg failed at or past its budget - a possible overrun, not a test failure.</param>
/// <param name="Log">The job's whole log, as the forge keeps it, or null where the check is no job or it could not be read.</param>
/// <param name="Unread">
/// Why some of where it failed is not there - what a read of its run or of its log answered that failed - or null where
/// every read answered. The check is red all the same: its verdict never hangs on what is read of it afterwards.
/// </param>
public sealed record CiRedCheck(CiCheck Check, string? Job, string? Workflow, string? Step, string? Leg, bool Overran, string? Log, string? Unread);

/// <summary>What a wait for the checks of a commit found.</summary>
/// <param name="Commit">The commit.</param>
/// <param name="Found">What it came to.</param>
/// <param name="Checks">Every check it counted, as it last read them.</param>
/// <param name="Red">Each red check, with where it failed.</param>
public sealed record CiChecksReading(string Commit, CiChecksFound Found, IReadOnlyList<CiCheck> Checks, IReadOnlyList<CiRedCheck> Red)
{
    /// <summary>The checks that had not ended when it last read them.</summary>
    public IReadOnlyList<CiCheck> Pending => [.. Checks.Where(check => check.State == CiCheckState.Pending)];

    /// <summary>The checks that ended neither green nor red.</summary>
    public IReadOnlyList<CiCheck> Unsettled => [.. Checks.Where(check => check.State == CiCheckState.Unsettled)];
}

/// <summary>Waits for the checks the forge runs on a commit, and says how each ended.</summary>
/// <remarks>
/// It starts nothing: it never dispatches a workflow and never runs one again, for a commit pushed is the only thing
/// that starts checks, and re-running one is a person's call.
/// </remarks>
public interface ICiChecksService
{
    /// <summary>
    /// Reads every check run and commit status the forge lists for the commit, less those ignored, until one is red,
    /// every one has ended, none appeared in time, or the time to wait is spent.
    /// </summary>
    /// <param name="startDirectory">A directory inside the repository.</param>
    /// <param name="request">The commit, and how long to wait.</param>
    /// <param name="cancellationToken">Stops the wait.</param>
    /// <exception cref="HarnessException">
    /// The forge could not be read: <see cref="CiChecksService.FailedReadsInARow"/> reads running failed (20) or passed
    /// their time limit (21), or one did as the time to wait was spent; <c>gh</c> is not there (14), or another
    /// repository is named for it (13).
    /// </exception>
    Task<CiChecksReading> WaitAsync(string startDirectory, CiChecksRequest request, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="ICiChecksService"/>
/// <param name="contextLoader">Finds the repository.</param>
/// <param name="jobSource">Reads the forge.</param>
/// <param name="legs">Classifies the legs of the run a red check is a job of, as <c>check-ci-legs</c> does.</param>
/// <param name="clock">Times the wait.</param>
public sealed class CiChecksService(
    IHarnessContextLoader contextLoader,
    ICiJobSource jobSource,
    ICiLegsService legs,
    TimeProvider clock) : ICiChecksService
{
    /// <summary>How many reads running may fail before the wait ends with the last one's failure: three.</summary>
    public const int FailedReadsInARow = 3;

    public async Task<CiChecksReading> WaitAsync(string startDirectory, CiChecksRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var root = (await contextLoader.LoadAsync(startDirectory, cancellationToken).ConfigureAwait(false)).Layout.RepositoryRoot;

        // Timed by the clock's ticks, never its time of day: a clock set again, or a machine woken from sleep, would move
        // a wait's end by what it moved.
        var started = clock.GetTimestamp();
        IReadOnlyList<CiCheck>? before = null;
        var failed = 0;

        while (true)
        {
            IReadOnlyList<CiCheck> checks;

            try
            {
                checks = await ReadAsync(root, request, cancellationToken).ConfigureAwait(false);
                failed = 0;
            }
            catch (HarnessException ex) when (ex.ExitCode is HarnessExit.CommandFailed or HarnessExit.Incomplete)
            {
                // A read the forge failed - a server error, a rate limit, a network that dropped - says nothing of the
                // checks, and the next may answer: an hour's wait is never ended by one, nor a person's accept. It ends
                // with the failure only where reads failed so many times running, or the time of a wait is spent.
                var elapsed = clock.GetElapsedTime(started);

                if (++failed >= FailedReadsInARow || !request.Once && elapsed >= request.Wait)
                {
                    throw;
                }

                await Task.Delay(Next(request, elapsed), clock, cancellationToken).ConfigureAwait(false);
                continue;
            }

            var waited = clock.GetElapsedTime(started);
            var spent = request.Once || waited >= request.Wait;

            // A red check is red whatever the others come to: the step that mends it need not wait for them.
            if (checks.Any(check => check.State == CiCheckState.Red))
            {
                return new CiChecksReading(request.Commit, CiChecksFound.Red, checks, await RedAsync(startDirectory, root, checks, request.ReadLimit, cancellationToken).ConfigureAwait(false));
            }

            if (checks.Count == 0)
            {
                // None by the time it waits for the first - or by the end of its whole wait, where that comes first - is none.
                if (request.Once || waited >= request.NoneAfter)
                {
                    return new CiChecksReading(request.Commit, CiChecksFound.None, checks, []);
                }
            }
            else if (checks.All(check => check.State != CiCheckState.Pending) && (Same(before, checks) || spent))
            {
                // Every check ended is a verdict only once a second read finds the same checks: one that ended fast - a
                // status a bot posts at once - may be all there is to read before the forge has listed the workflows'
                // jobs, which may come to red. At the end of the wait, or on the one read of a wait that reads once, what
                // was read is taken as it stands.
                return new CiChecksReading(
                    request.Commit,
                    checks.Any(check => check.State == CiCheckState.Unsettled) ? CiChecksFound.Unsettled : CiChecksFound.Green,
                    checks,
                    []);
            }

            if (spent)
            {
                return new CiChecksReading(request.Commit, CiChecksFound.Pending, checks, []);
            }

            before = checks;
            await Task.Delay(Next(request, waited), clock, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// How long it waits before it reads again, <paramref name="waited"/> into the wait: a poll, cut to the end of a wait
    /// that ends sooner, so its last read is made at its end and never a poll past it.
    /// </summary>
    private static TimeSpan Next(CiChecksRequest request, TimeSpan waited)
        => request.Once || request.Wait - waited >= request.Poll ? request.Poll : request.Wait - waited;

    /// <summary>Whether two reads found the same checks, by name.</summary>
    private static bool Same(IReadOnlyList<CiCheck>? before, IReadOnlyList<CiCheck> checks)
        => before is not null
            && before.Select(check => check.Name).Order(StringComparer.Ordinal)
                .SequenceEqual(checks.Select(check => check.Name).Order(StringComparer.Ordinal), StringComparer.Ordinal);

    /// <summary>Every check run and commit status of the commit, as one list of checks, less those the request ignores.</summary>
    private async Task<IReadOnlyList<CiCheck>> ReadAsync(string root, CiChecksRequest request, CancellationToken cancellationToken)
    {
        var runs = await jobSource.ListCheckRunsAsync(root, request.Commit, request.ReadLimit, cancellationToken).ConfigureAwait(false);
        var statuses = await jobSource.ListStatusesAsync(root, request.Commit, request.ReadLimit, cancellationToken).ConfigureAwait(false);

        IEnumerable<CiCheck> read =
        [
            .. runs.Select(run => new CiCheck(run.Name, run.State, run.Conclusion, run.Url, run.RunId, run.JobId, null)),
            .. statuses.Select(status => new CiCheck(
                status.Context,
                status.Reading,
                status.Reading == CiCheckState.Pending ? null : status.State,
                status.Url,
                null,
                null,
                status.Description)),
        ];

        // A check ignored by name is not counted at all: it decides nothing, and is not said.
        return [.. read.Where(check => !request.Ignores(check.Name))];
    }

    /// <summary>
    /// Each red check, with where it failed: of a job of a workflow run, the run read and its legs classified as
    /// <c>check-ci-legs</c> classifies them - each run once - the job's step that failed, its leg and the job's whole log.
    /// A read of these that fails leaves out what it would have said, and says why: the check is red all the same.
    /// </summary>
    private async Task<IReadOnlyList<CiRedCheck>> RedAsync(
        string startDirectory,
        string root,
        IReadOnlyList<CiCheck> checks,
        TimeSpan? readLimit,
        CancellationToken cancellationToken)
    {
        var runs = new Dictionary<long, (CiRunReport? Report, string? Unread)>();
        var red = new List<CiRedCheck>();

        foreach (var check in checks.Where(check => check.State == CiCheckState.Red))
        {
            if (check.RunId is not { } runId || check.JobId is not { } jobId)
            {
                red.Add(new CiRedCheck(check, null, null, null, null, false, null, null));
                continue;
            }

            if (!runs.TryGetValue(runId, out var classified))
            {
                try
                {
                    classified = (await legs.ClassifyRunAsync(startDirectory, runId, readLimit, cancellationToken).ConfigureAwait(false), null);
                }
                catch (HarnessException ex)
                {
                    classified = (null, $"Its run could not be read or classified: {ex.Message}");
                }

                runs[runId] = classified;
            }

            var (report, unread) = classified;
            var job = report?.Run.Jobs.FirstOrDefault(job => job.Id == jobId)
                ?? report?.Run.Jobs.FirstOrDefault(job => string.Equals(job.Name, check.Name, StringComparison.Ordinal));
            var leg = job is null ? null : report!.Legs.FirstOrDefault(leg => string.Equals(leg.Job, job.Name, StringComparison.Ordinal));
            string? log = null;

            try
            {
                log = await jobSource.ReadJobLogAsync(root, jobId, readLimit, cancellationToken).ConfigureAwait(false);
            }
            catch (HarnessException ex)
            {
                unread = unread is null ? ex.Message : $"{unread} {ex.Message}";
            }

            red.Add(new CiRedCheck(
                check,
                job?.Name ?? check.Name,
                report?.Workflow,
                job?.Steps.FirstOrDefault(step => step.Failed)?.Name,
                leg?.Leg,
                leg?.Overran ?? false,
                log,
                unread));
        }

        return red;
    }
}
