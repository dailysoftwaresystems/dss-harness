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

    /// <summary>The limit each read was given, in order.</summary>
    public List<TimeSpan?> ReadLimits { get; } = [];

    /// <summary>Each run read, in order.</summary>
    public List<long> RunsRead { get; } = [];

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
        TimeSpan? readLimit,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            Branches.Add(branch);
            ReadLimits.Add(readLimit);
        }

        return Task.FromResult<IReadOnlyList<long>>([.. runs.Select(run => run.Id)]);
    }

    public Task<CiRun> ReadRunAsync(string repositoryRoot, long runId, TimeSpan? readLimit, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            RunsRead.Add(runId);
            ReadLimits.Add(readLimit);
        }

        return Task.FromResult(runs.First(run => run.Id == runId));
    }

    public Task<IReadOnlyList<CiCheckRun>> ListCheckRunsAsync(string repositoryRoot, string commit, TimeSpan? readLimit, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            ReadLimits.Add(readLimit);
            Reading?.Invoke(_readings);

            if (FailedReadings.TryGetValue(_readings, out var failure))
            {
                _readings++;
                return Task.FromException<IReadOnlyList<CiCheckRun>>(failure);
            }

            return Task.FromResult(Readings.Count == 0 ? [] : Readings[Math.Min(_readings, Readings.Count - 1)].Runs);
        }
    }

    public Task<IReadOnlyList<CiCommitStatus>> ListStatusesAsync(string repositoryRoot, string commit, TimeSpan? readLimit, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            ReadLimits.Add(readLimit);
            var statuses = Readings.Count == 0 ? [] : Readings[Math.Min(_readings, Readings.Count - 1)].Statuses;
            _readings++;
            return Task.FromResult(statuses);
        }
    }

    public Task<string> ReadJobLogAsync(string repositoryRoot, long jobId, TimeSpan? readLimit, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            ReadLimits.Add(readLimit);
        }

        return FailedLogs.TryGetValue(jobId, out var failure) ? Task.FromException<string>(failure) : Task.FromResult(Logs[jobId]);
    }
}
