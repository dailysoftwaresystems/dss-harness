using System.Diagnostics;
using System.Text.RegularExpressions;
using DssHarness.Core.Output;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;
using DssHarness.Core.Text;
using DssHarness.Core.Waiting;

namespace DssHarness.Core.Ci;

/// <summary>What a wait for the checks of a commit is asked.</summary>
/// <param name="Commit">The commit whose checks it reads.</param>
/// <param name="Command">The command or step waiting, which the lines the wait says name.</param>
public sealed record CiChecksRequest(string Commit, string Command)
{
    /// <summary>How long a wait waits for the first check to appear where it is told not: three minutes.</summary>
    public const int DefaultAppearSeconds = 180;

    /// <summary>How long a wait waits, from its start, for every check to end where it is told not: an hour.</summary>
    public const int DefaultWaitMinutes = 60;

    /// <summary>How often a wait reads the checks where it is told not: every thirty seconds.</summary>
    public const int DefaultPollSeconds = 30;

    /// <summary>How long it waits for the first check to appear.</summary>
    public TimeSpan Appear { get; init; } = TimeSpan.FromSeconds(DefaultAppearSeconds);

    /// <summary>How long each read of the forge may take: a step's <c>timeoutMinutes</c>, where it says one; null for the tool's own.</summary>
    public TimeSpan? ReadTimeLimit { get; init; }

    /// <summary>How long it waits, from its start, for every check to end.</summary>
    public TimeSpan Wait { get; init; } = TimeSpan.FromMinutes(DefaultWaitMinutes);

    /// <summary>How often it reads the checks while it waits.</summary>
    public TimeSpan Poll { get; init; } = TimeSpan.FromSeconds(DefaultPollSeconds);

    /// <summary>How long it waits for the first check before it answers none: <see cref="Appear"/>, or <see cref="Wait"/> where that is shorter.</summary>
    public TimeSpan NoneAfter => Appear < Wait ? Appear : Wait;

    /// <summary>The checks it does not count, by name, each written out or with <c>*</c> for any run of characters.</summary>
    public IReadOnlyList<string> Ignore { get; init; } = [];


    /// <summary>
    /// Whether it reads the checks once, waiting for none to appear or end - what was reached taken as it stands, as a
    /// person's <c>accept</c> asks - and answers what they are then, each settled on that read alone. A read that fails
    /// is said and read again at the next poll for as long as its failure can change, however short the wait its step
    /// says: its reads are the whole of it, so no time of the wait's own ends them.
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
            ? LinearRegex.Compile("^" + Regex.Escape(pattern).Replace(@"\*", ".*", StringComparison.Ordinal) + "$", RegexOptions.Singleline).IsMatch(name)
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
public sealed record CiCheck(string Name, CiCheckState State, string? Conclusion, string Url, long? RunId, long? JobId, string? Description)
{
    /// <summary>The check run it is, by the forge's id, which its annotations are read by; null for a status.</summary>
    public long? CheckRunId { get; init; }

    /// <summary>How many annotations the forge says it holds; none for a status.</summary>
    public int Annotations { get; init; }

    /// <summary>The repository its workflow run is of, by its name alone (<see cref="CiCheckRun.Repository"/>); null otherwise.</summary>
    public string? Repository { get; init; }
}

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
public sealed record CiRedCheck(CiCheck Check, string? Job, string? Workflow, string? Step, string? Leg, bool Overran, string? Log, string? Unread)
{
    /// <summary>
    /// The check run's annotations, as the forge keeps them, in its order - each with its place where it names one; none
    /// where it holds none, or they could not be read, which <see cref="Unread"/> then says.
    /// </summary>
    public IReadOnlyList<CiAnnotation> Annotations { get; init; } = [];
}

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
    /// The forge could not be read: a read failed in a way no read again changes - the credential refused, the thing asked
    /// not there - or as an earlier one had (20), or a read failed (20) or passed its time limit (21) as the time to wait
    /// was spent; <c>gh</c> is not there (14), or another repository is named for it (13).
    /// </exception>
    Task<CiChecksReading> WaitAsync(string startDirectory, CiChecksRequest request, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="ICiChecksService"/>
/// <param name="contextLoader">Finds the repository.</param>
/// <param name="jobSource">Reads the forge.</param>
/// <param name="legs">Classifies the legs of the run a red check is a job of, as <c>check-ci-legs</c> does.</param>
/// <param name="output">Says each read that failed and is read again.</param>
/// <param name="clock">Times the wait.</param>
public sealed class CiChecksService(
    IHarnessContextLoader contextLoader,
    ICiJobSource jobSource,
    ICiLegsService legs,
    IHarnessOutput output,
    TimeProvider clock) : ICiChecksService
{
    public async Task<CiChecksReading> WaitAsync(string startDirectory, CiChecksRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var root = (await contextLoader.LoadAsync(startDirectory, cancellationToken).ConfigureAwait(false)).Layout.RepositoryRoot;

        // Timed by the clock's ticks, never its time of day: a clock set again, or a machine woken from sleep, would move
        // a wait's end by what it moved.
        var started = clock.GetTimestamp();
        IReadOnlyList<CiCheck>? before = null;

        while (true)
        {
            var checks = await ReadUntilAnsweredAsync(root, request, clock.GetElapsedTime(started), cancellationToken).ConfigureAwait(false);
            var waited = clock.GetElapsedTime(started);
            var spent = request.Once || waited >= request.Wait;

            // A red check is red whatever the others come to: the step that mends it need not wait for them.
            if (checks.Any(check => check.State == CiCheckState.Red))
            {
                return new CiChecksReading(request.Commit, CiChecksFound.Red, checks, await RedAsync(startDirectory, root, checks, request.ReadTimeLimit, cancellationToken).ConfigureAwait(false));
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
    /// The checks as one read of them answers, read again for as long as a read that failed can change
    /// (<see cref="Retrying"/>): a network that dropped, a read that did not answer in time, a server error and a rate
    /// limit pass by nature and are read again however often they come; a failure of another kind is read again while it
    /// is new, and ends the wait where the same one comes again; one no read changes - the forge refusing the credential
    /// (401) or answering that the thing asked is not there (404) - ends it at once. Each read again is said as it is
    /// waited for, the first a poll after the failure and each later one twice as long up to five minutes. The time of the
    /// wait, the planned waitMinutes, still ends it: where it is spent before the next read, the wait ends with the last
    /// failure's own code - but never a wait that reads once, whose reads go on at the poll for as long as they fail,
    /// however short the wait its step says. A refusal of the command's own - another repository named, gh not there -
    /// is raised at once.
    /// </summary>
    /// <param name="root">The repository the forge is asked about.</param>
    /// <param name="request">The commit, and how long to wait.</param>
    /// <param name="waited">How long the wait has lasted so far, which the time left to it is measured from.</param>
    /// <param name="cancellationToken">Stops the wait.</param>
    /// <exception cref="HarnessException">The reads ended on a failure, raised as the read raised it.</exception>
    private async Task<IReadOnlyList<CiCheck>> ReadUntilAnsweredAsync(string root, CiChecksRequest request, TimeSpan waited, CancellationToken cancellationToken)
    {
        HarnessException? last = null;
        var left = request.Wait - waited;
        var poll = request.Poll > TimeSpan.Zero ? request.Poll : Waits.LooksAgainEvery;
        var ended = await Retrying.RunAsync(
                async (_, token) =>
                {
                    try
                    {
                        return new RetryTry<IReadOnlyList<CiCheck>>.Done(await ReadAsync(root, request, token).ConfigureAwait(false));
                    }
                    catch (HarnessException ex) when (ex.ExitCode is HarnessExit.CommandFailed or HarnessExit.Incomplete)
                    {
                        last = ex;
                        var again = GhProcess.Try<IReadOnlyList<CiCheck>>(ex);

                        // A wait that reads once has no time of the wait's own to cut a read's again to: each read
                        // that failed is read again at the next poll, however short the wait its step says.
                        return request.Once && again is RetryTry<IReadOnlyList<CiCheck>>.Passing { After: null } passing
                            ? new RetryTry<IReadOnlyList<CiCheck>>.Passing(passing.Cause, poll)
                            : again;
                    }
                },
                new RetryWatch(
                    $"a read of the checks of {request.Commit}",
                    told => output.Warn(request.Command, told.Line()),

                    // A poll, cut to the end of a wait that ends sooner, as every read of the wait's is - but never
                    // cut where the wait reads once, whose reads are the whole of it.
                    !request.Once && left > TimeSpan.Zero && left < poll ? left : poll,
                    request.Once ? null : left > TimeSpan.Zero ? left : TimeSpan.Zero,
                    clock),
                cancellationToken)
            .ConfigureAwait(false);

        if (ended.Kind == RetryEndKind.Done)
        {
            return ended.Value!;
        }

        cancellationToken.ThrowIfCancellationRequested();

        // The wait's time spent between two reads: its last read is made at its end, after a read that failed as after one
        // that answered, and what that read raises is the failure the wait ends on.
        if (ended.Kind == RetryEndKind.Bound && left > TimeSpan.Zero)
        {
            return await ReadAsync(root, request, cancellationToken).ConfigureAwait(false);
        }

        // The failure the reads ended on, as the read raised it: its code says what it was, and nothing here rewords it.
        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(last!).Throw();
        throw new UnreachableException("a captured failure was thrown");
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
        var source = jobSource.Within(request.ReadTimeLimit);
        var runs = await source.ListCheckRunsAsync(root, request.Commit, cancellationToken).ConfigureAwait(false);
        var statuses = await source.ListStatusesAsync(root, request.Commit, cancellationToken).ConfigureAwait(false);

        IEnumerable<CiCheck> read =
        [
            .. runs.Select(run => new CiCheck(run.Name, run.State, run.Conclusion, run.Url, run.RunId, run.JobId, null)
            {
                CheckRunId = run.Id,
                Annotations = run.Annotations,
                Repository = run.Repository,
            }),
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
    /// Each red check, with where it failed: of a check run, its annotations - each with its place where it names one;
    /// of a job of a workflow run, the run read and its legs classified as <c>check-ci-legs</c> classifies them - each run
    /// once - the job's step that failed, its leg and the job's whole log. A read of these that fails leaves out what it
    /// would have said, and says why: the check is red all the same.
    /// </summary>
    private async Task<IReadOnlyList<CiRedCheck>> RedAsync(
        string startDirectory,
        string root,
        IReadOnlyList<CiCheck> checks,
        TimeSpan? readTimeLimit,
        CancellationToken cancellationToken)
    {
        var runs = new Dictionary<long, (CiRunReport? Report, string? Unread)>();
        var red = new List<CiRedCheck>();

        foreach (var check in checks.Where(check => check.State == CiCheckState.Red))
        {
            IReadOnlyList<CiAnnotation> annotations = [];
            string? annotationsUnread = null;

            // Read only where the forge counts any: a check run that holds none is not asked.
            if (check.CheckRunId is { } checkRunId && check.Annotations > 0)
            {
                try
                {
                    annotations = await jobSource.Within(readTimeLimit).ReadAnnotationsAsync(root, checkRunId, check.Annotations, cancellationToken).ConfigureAwait(false);
                }
                catch (HarnessException ex)
                {
                    annotationsUnread = $"Its annotations could not be read: {ex.Message}";
                }
            }

            if (check.RunId is not { } runId || check.JobId is not { } jobId)
            {
                red.Add(new CiRedCheck(check, null, null, null, null, false, null, annotationsUnread) { Annotations = annotations });
                continue;
            }

            if (!runs.TryGetValue(runId, out var classified))
            {
                try
                {
                    classified = (await legs.ClassifyRunAsync(startDirectory, runId, readTimeLimit, cancellationToken).ConfigureAwait(false), null);
                }
                catch (HarnessException ex)
                {
                    classified = (null, $"Its run could not be read or classified: {ex.Message}");
                }

                runs[runId] = classified;
            }

            var (report, unread) = classified;
            unread = annotationsUnread is null ? unread : unread is null ? annotationsUnread : $"{unread} {annotationsUnread}";
            var job = report?.Run.Jobs.FirstOrDefault(job => job.Id == jobId)
                ?? report?.Run.Jobs.FirstOrDefault(job => string.Equals(job.Name, check.Name, StringComparison.Ordinal));
            var leg = job is null ? null : report!.Legs.FirstOrDefault(leg => string.Equals(leg.Job, job.Name, StringComparison.Ordinal));
            string? log = null;

            try
            {
                log = await jobSource.Within(readTimeLimit).ReadJobLogAsync(root, jobId, cancellationToken).ConfigureAwait(false);
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
                unread)
            {
                Annotations = annotations,
            });
        }

        return red;
    }
}
