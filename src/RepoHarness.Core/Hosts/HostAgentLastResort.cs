using System.ComponentModel;
using System.Diagnostics;
using RepoHarness.Core.Platform;

namespace RepoHarness.Core.Hosts;

/// <summary>How long a host's agent waits on the machine that asked, and on what it started for that machine.</summary>
public sealed record HostAgentPatience
{
    /// <summary>What a request's beat seconds are counted in: a second.</summary>
    public TimeSpan BeatUnit { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The clock the silence on a request's input is measured by: the machine's own, and another only where a test moves
    /// one by hand - measured by the machine's, a test of a beat that goes on is a race between two timers, lost
    /// whenever the machine is busy for longer than the silence it counts.
    /// </summary>
    public TimeProvider Clock { get; init; } = TimeProvider.System;

    /// <summary>
    /// How the time between two looks at that silence passes: a delay, and otherwise only where a test steps
    /// <see cref="Clock"/> itself.
    /// </summary>
    public Func<TimeSpan, CancellationToken, Task> Pause { get; init; } = Task.Delay;

    /// <summary>
    /// How long what a request started is given to stop once it is cancelled, before the agent ends it and itself: long
    /// enough for a build tool to be stopped and its output read to the end, and short beside any leg.
    /// </summary>
    public TimeSpan Unwind { get; init; } = TimeSpan.FromSeconds(60);
}

/// <summary>What a host's agent does when what it started will not stop.</summary>
public interface IHostAgentLastResort
{
    /// <summary>
    /// Ends every process this one started, and everything those started, then this process itself, with
    /// <paramref name="exitCode"/>: it returns only where a test stands in for it.
    /// </summary>
    /// <param name="exitCode">The code this process ends with.</param>
    Task EndAsync(int exitCode);
}

/// <inheritdoc cref="IHostAgentLastResort"/>
/// <remarks>
/// A command cancelled here stops its children and reads what they wrote to the end, and that reading never ends where
/// something a child started keeps the pipe: a compiler's server, a daemon a build tool left behind. The machine that
/// asked has gone by then, so nothing is waiting for a tidy end, and a host held by a run nobody reads cannot be updated
/// or used.
/// </remarks>
public sealed class HostAgentLastResort(IProcessTable processes) : IHostAgentLastResort
{
    /// <summary>How long the process table is given to answer: the agent ends whether or not it does.</summary>
    private static readonly TimeSpan ReadingBudget = TimeSpan.FromSeconds(15);

    private readonly IProcessTable _processes = processes;

    /// <inheritdoc/>
    public async Task EndAsync(int exitCode)
    {
        using var budget = new CancellationTokenSource(ReadingBudget);

        try
        {
            using var self = Process.GetCurrentProcess();

            await EndStartedByAsync(_processes, self.Id, self.StartTime.ToUniversalTime(), budget.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or Win32Exception or IOException)
        {
            // What could not be listed could not be ended, and this process ends all the same.
        }

        Environment.Exit(exitCode);
    }

    /// <summary>
    /// Ends every process <paramref name="parent"/> started, each with everything it started, and says which.
    /// </summary>
    /// <param name="processes">Reads what is running.</param>
    /// <param name="parent">The process whose children go.</param>
    /// <param name="parentStarted">
    /// When it started: a process listed as its child that started before it is some earlier process's, which held the
    /// same id, and is left alone. Windows goes on naming a parent that has gone.
    /// </param>
    /// <param name="cancellationToken">Stops the reading.</param>
    /// <returns>The id of each child ended, in the order of their ids.</returns>
    internal static async Task<IReadOnlyList<int>> EndStartedByAsync(
        IProcessTable processes,
        int parent,
        DateTimeOffset parentStarted,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(processes);

        var reading = await processes.ReadAsync(cancellationToken).ConfigureAwait(false);
        var ended = new List<int>();

        foreach (var child in reading.Processes
            .Where(process => process.ParentId == parent && process.Id != parent && !(process.StartedUtc < parentStarted))
            .OrderBy(process => process.Id))
        {
            try
            {
                using var process = Process.GetProcessById(child.Id);

                process.Kill(entireProcessTree: true);
                ended.Add(child.Id);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception or AggregateException or NotSupportedException)
            {
                // It ended meanwhile - the query that listed it among them - or it cannot be signalled; the rest still go.
            }
        }

        return ended;
    }
}
