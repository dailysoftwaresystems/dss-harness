using DssHarness.Core.Ci;

namespace DssHarness.Tests;

/// <summary>
/// What the forge answers, as a test supplies it. The forge is not reachable from a test, and reading CI needs the network
/// and an authenticated tool: a test that needed either would red for a property of the machine rather than of the code.
/// </summary>
/// <param name="runs">The runs it holds, each listed for every workflow and branch.</param>
internal sealed class StubCiJobSource(IReadOnlyList<CiRun> runs) : ICiJobSource
{
    private readonly Lock _gate = new();
    private int _readings;

    /// <summary>The branch each listing of runs was asked about, in order.</summary>
    public List<string> Branches { get; } = [];


    /// <summary>Each run read, in order.</summary>
    public List<long> RunsRead { get; } = [];

    /// <summary>
    /// The limit each read was made under, in order: the one the source it was made through was given
    /// (<see cref="ICiJobSource.Within"/>), null for a read made through this one, which was given none.
    /// </summary>
    public List<TimeSpan?> Limits { get; } = [];

    /// <inheritdoc/>
    public ICiJobSource Within(TimeSpan? limit) => new Limited(this, limit);

    /// <summary>
    /// What each reading of a commit's checks answers - its check runs, then its statuses - in turn, the last again once
    /// they run out; none where none is given.
    /// </summary>
    public IReadOnlyList<(IReadOnlyList<CiCheckRun> Runs, IReadOnlyList<CiCommitStatus> Statuses)> Readings { get; init; } = [];

    /// <summary>Each job's log, by the job's id.</summary>
    public IReadOnlyDictionary<long, string> Logs { get; init; } = new Dictionary<long, string>();

    /// <summary>The readings of a commit's checks that fail, by their place in turn from 0, with what each throws.</summary>
    public IReadOnlyDictionary<int, Exception> FailedReadings { get; init; } = new Dictionary<int, Exception>();

    /// <summary>The logs that cannot be read, by the job's id, with what each read throws.</summary>
    public IReadOnlyDictionary<long, Exception> FailedLogs { get; init; } = new Dictionary<long, Exception>();

    /// <summary>Told each reading of a commit's checks as it starts, by its place in turn from 0.</summary>
    public Action<int>? Reading { get; init; }

    /// <summary>How many times the checks were read.</summary>
    public int ChecksRead
    {
        get
        {
            lock (_gate)
            {
                return _readings;
            }
        }
    }

    public Task<IReadOnlyList<long>> ListRunsAsync(
        string repositoryRoot,
        string workflow,
        string branch,
        int limit,
        CancellationToken cancellationToken = default)
        => ListRuns(null, branch);

    public Task<CiRun> ReadRunAsync(string repositoryRoot, long runId, CancellationToken cancellationToken = default) => ReadRun(null, runId);

    public Task<IReadOnlyList<CiCheckRun>> ListCheckRunsAsync(string repositoryRoot, string commit, CancellationToken cancellationToken = default) => ListCheckRuns(null);

    public Task<IReadOnlyList<CiCommitStatus>> ListStatusesAsync(string repositoryRoot, string commit, CancellationToken cancellationToken = default) => ListStatuses(null);

    public Task<string> ReadJobLogAsync(string repositoryRoot, long jobId, CancellationToken cancellationToken = default) => ReadJobLog(null, jobId);

    private Task<IReadOnlyList<long>> ListRuns(TimeSpan? within, string branch)
    {
        lock (_gate)
        {
            Limits.Add(within);
            Branches.Add(branch);
        }

        return Task.FromResult<IReadOnlyList<long>>([.. runs.Select(run => run.Id)]);
    }

    private Task<CiRun> ReadRun(TimeSpan? within, long runId)
    {
        lock (_gate)
        {
            Limits.Add(within);
            RunsRead.Add(runId);
        }

        return Task.FromResult(runs.First(run => run.Id == runId));
    }

    private Task<IReadOnlyList<CiCheckRun>> ListCheckRuns(TimeSpan? within)
    {
        lock (_gate)
        {
            Limits.Add(within);
            Reading?.Invoke(_readings);

            if (FailedReadings.TryGetValue(_readings, out var failure))
            {
                _readings++;
                return Task.FromException<IReadOnlyList<CiCheckRun>>(failure);
            }

            return Task.FromResult(Readings.Count == 0 ? [] : Readings[Math.Min(_readings, Readings.Count - 1)].Runs);
        }
    }

    private Task<IReadOnlyList<CiCommitStatus>> ListStatuses(TimeSpan? within)
    {
        lock (_gate)
        {
            Limits.Add(within);
            var statuses = Readings.Count == 0 ? [] : Readings[Math.Min(_readings, Readings.Count - 1)].Statuses;
            _readings++;
            return Task.FromResult(statuses);
        }
    }

    private Task<string> ReadJobLog(TimeSpan? within, long jobId)
    {
        lock (_gate)
        {
            Limits.Add(within);
        }

        return FailedLogs.TryGetValue(jobId, out var failure) ? Task.FromException<string>(failure) : Task.FromResult(Logs[jobId]);
    }

    /// <summary>The source given a limit: what it reads is the stub's, each read recorded under its limit.</summary>
    private sealed class Limited(StubCiJobSource stub, TimeSpan? limit) : ICiJobSource
    {
        public ICiJobSource Within(TimeSpan? other) => new Limited(stub, other);

        public Task<IReadOnlyList<long>> ListRunsAsync(string repositoryRoot, string workflow, string branch, int count, CancellationToken cancellationToken = default)
            => stub.ListRuns(limit, branch);

        public Task<CiRun> ReadRunAsync(string repositoryRoot, long runId, CancellationToken cancellationToken = default) => stub.ReadRun(limit, runId);

        public Task<IReadOnlyList<CiCheckRun>> ListCheckRunsAsync(string repositoryRoot, string commit, CancellationToken cancellationToken = default) => stub.ListCheckRuns(limit);

        public Task<IReadOnlyList<CiCommitStatus>> ListStatusesAsync(string repositoryRoot, string commit, CancellationToken cancellationToken = default) => stub.ListStatuses(limit);

        public Task<string> ReadJobLogAsync(string repositoryRoot, long jobId, CancellationToken cancellationToken = default) => stub.ReadJobLog(limit, jobId);
    }
}
