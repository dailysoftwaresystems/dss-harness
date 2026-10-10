using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using RepoHarness.Core.Execution;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Worktrees;

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

    /// <summary>
    /// How long a command past its point of no return (<see cref="PointOfNoReturn"/>) is given instead: as long as the
    /// command line gives one interrupted where it was typed, which goes on to finish what it began and, where it cannot,
    /// stops shortly before that time is up to say what is left. Cut off at <see cref="Unwind"/>, a deletion would be
    /// ended part way with nothing said of what remains.
    /// </summary>
    public TimeSpan Finishing { get; init; } = WorktreeService.DefaultInterruptionGrace;

    /// <summary>
    /// How long the agent's words to a machine that may no longer be reading are given to be written: a line nobody reads
    /// may never finish being written, and neither a cancellation nor the agent's end waits on one for longer.
    /// </summary>
    public TimeSpan Saying { get; init; } = TimeSpan.FromSeconds(2);
}

/// <summary>What a host's agent does when what it started will not stop.</summary>
public interface IHostAgentLastResort
{
    /// <summary>
    /// Ends every process this one started, each with everything it started, and says what that came to, as words that
    /// finish a sentence: what was ended, or why nothing could be. It never throws: the agent ends whatever this finds.
    /// </summary>
    Task<string> EndStartedAsync();

    /// <summary>Ends this process with <paramref name="exitCode"/>: it returns only where a test stands in for it.</summary>
    /// <param name="exitCode">The code this process ends with.</param>
    void End(int exitCode);
}

/// <summary>What ending the processes an agent started came to.</summary>
/// <param name="Ended">The id of each child ended, with everything it started, in the order of their ids.</param>
/// <param name="Left">The id of each child that could not be ended.</param>
/// <param name="Unlisted">Why the children could not be told from the table; <see langword="null"/> where they could.</param>
public sealed record LastResortOutcome(IReadOnlyList<int> Ended, IReadOnlyList<int> Left, string? Unlisted)
{
    /// <summary>What it came to, as words that finish a sentence about the agent.</summary>
    public string Said
    {
        get
        {
            if (Unlisted is not null)
            {
                return $"what it started could not be listed - {Unlisted.TrimEnd('.')} - so none of it was ended";
            }

            if (Ended.Count == 0 && Left.Count == 0)
            {
                return "nothing it started was still running";
            }

            static string Ids(IReadOnlyList<int> ids) => string.Join(", ", ids.Select(id => string.Create(CultureInfo.InvariantCulture, $"pid {id}")));

            var ended = Ended.Count switch
            {
                0 => null,
                1 => $"1 process it started was ended, with what that started ({Ids(Ended)})",
                _ => string.Create(CultureInfo.InvariantCulture, $"{Ended.Count} processes it started were ended, each with what it started ({Ids(Ended)})"),
            };
            var left = Left.Count == 0 ? null : string.Create(CultureInfo.InvariantCulture, $"{Left.Count} it started could not be ended ({Ids(Left)})");

            return string.Join("; ", new[] { ended, left }.OfType<string>());
        }
    }
}

/// <inheritdoc cref="IHostAgentLastResort"/>
/// <remarks>
/// A command cancelled here stops its children and reads what they wrote to the end, and that reading never ends where
/// something a child started keeps the pipe: a compiler's server, a daemon a build tool left behind. The machine that
/// asked has gone by then, so nothing is waiting for a tidy end, and a host held by a run nobody reads cannot be updated
/// or used.
/// </remarks>
/// <param name="processes">Reads what is running.</param>
/// <param name="platform">Says which machine this is.</param>
/// <param name="readingBudget">How long the process table is given to answer; <see cref="ReadingBudget"/> where none is given.</param>
public sealed class HostAgentLastResort(IProcessTable processes, IHostPlatform platform, TimeSpan? readingBudget = null) : IHostAgentLastResort
{
    /// <summary>How long the process table is given to answer: the agent ends whether or not it does.</summary>
    public static readonly TimeSpan ReadingBudget = TimeSpan.FromSeconds(15);

    /// <summary>The program Windows runs a console in, as the process table names it.</summary>
    private const string ConsoleHost = "conhost";

    private readonly IProcessTable _processes = processes;
    private readonly IHostPlatform _platform = platform;
    private readonly TimeSpan _readingBudget = readingBudget ?? ReadingBudget;

    /// <inheritdoc/>
    public async Task<string> EndStartedAsync()
    {
        using var budget = new CancellationTokenSource(_readingBudget);

        try
        {
            return (await EndStartedByAsync(_processes, _platform, Environment.ProcessId, budget.Token).ConfigureAwait(false)).Said;
        }
        catch (OperationCanceledException)
        {
            var seconds = Math.Ceiling(_readingBudget.TotalSeconds).ToString(CultureInfo.InvariantCulture);

            return new LastResortOutcome([], [], $"this machine's processes were not listed within {seconds} s").Said;
        }
        catch (Exception ex)
        {
            // Whatever the table raised - the program it is read through not starting among them: nothing listed is
            // nothing ended, said as that, and the agent ends all the same.
            return new LastResortOutcome([], [], ex.Message).Said;
        }
    }

    /// <inheritdoc/>
    public void End(int exitCode) => Environment.Exit(exitCode);

    /// <summary>
    /// Ends every process <paramref name="parent"/> started, each with everything it started, and says which.
    /// </summary>
    /// <param name="processes">Reads what is running.</param>
    /// <param name="platform">Says which machine this is.</param>
    /// <param name="parent">The process whose children go.</param>
    /// <param name="cancellationToken">Stops the reading.</param>
    /// <remarks>
    /// <para>
    /// A process listed as its child that started before it did is some earlier process's, which held the same id, and
    /// is left alone: Windows goes on naming a parent that has gone. Both starts are read from the one table, which
    /// dates every process by the same clock - on Linux one that counts from the machine's start, and is no time of
    /// day: held against this process's own start as the runtime tells it, every child there read as older than its
    /// parent, and none was ended. A table that could not be read whole names no parents, and says why instead.
    /// </para>
    /// <para>
    /// Nor is the console it runs in anything it started. Windows lists that - a <c>conhost</c> - as the child of a
    /// process started with a console of its own, and ends what runs in a console once that has ended: ended here, it
    /// was counted as a process the request had started, and the agent's last words and exit code were left to whether
    /// it got to them first. One started for what the agent started is that process's child, and goes with it.
    /// </para>
    /// <para>
    /// A child that had already ended is passed over, not counted: the program the table is read through is one, on
    /// Windows, in every reading.
    /// </para>
    /// </remarks>
    internal static async Task<LastResortOutcome> EndStartedByAsync(IProcessTable processes, IHostPlatform platform, int parent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(processes);
        ArgumentNullException.ThrowIfNull(platform);

        var reading = await processes.ReadAsync(cancellationToken).ConfigureAwait(false);

        if (reading.Degraded is { } degraded)
        {
            return new LastResortOutcome([], [], degraded);
        }

        var parentStarted = reading.Processes.FirstOrDefault(process => process.Id == parent)?.StartedUtc;
        var itsConsole = platform.Current == PlatformId.Windows;
        var ended = new List<int>();
        var left = new List<int>();

        foreach (var child in reading.Processes
            .Where(process => process.ParentId == parent && process.Id != parent && !(process.StartedUtc < parentStarted))
            .Where(process => !(itsConsole && string.Equals(process.Name, ConsoleHost, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(process => process.Id))
        {
            try
            {
                using var process = Process.GetProcessById(child.Id);

                if (process.HasExited)
                {
                    continue;
                }

                process.Kill(entireProcessTree: true);
                ended.Add(child.Id);
            }
            catch (ArgumentException)
            {
                // It ended meanwhile - the query that listed it among them.
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or AggregateException or NotSupportedException)
            {
                // It cannot be signalled, or something it started cannot: said, and the rest still go.
                left.Add(child.Id);
            }
        }

        return new LastResortOutcome(ended, left, null);
    }
}
