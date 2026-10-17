using System.Diagnostics;
using DssHarness.Core.Build;
using DssHarness.Core.Execution;
using DssHarness.Core.Hosts;
using DssHarness.Core.Legs;
using DssHarness.Core.Output;
using DssHarness.Core.Runs;

namespace DssHarness.Core.Application.Legs;

/// <summary>
/// What every command that runs legs was asked, as its command line gave it: the directory, the legs, the lock, the
/// staging, the timing, the host dispatching here - and the two options that say where this run's own legs work, whose
/// rule (<see cref="DssHarness.Core.Legs.LegTrees.Asked(bool, bool)"/>) is asked in the core: by a build and a test before
/// any configuration is read, and by a run of a runner once the runner it names is read, since whether that runner writes
/// the tree is part of where its legs work.
/// </summary>
public abstract record LegCommandRequest
{
    /// <summary>The directory to act on, absolute and already confirmed to exist.</summary>
    public required string Directory { get; init; }

    /// <summary>Only these legs or leg sets, as they were named; <see langword="null"/> for every declared leg.</summary>
    public IReadOnlyList<string>? LegNames { get; init; }

    /// <summary>Take a lock another run holds, on this host or another. Always a human decision.</summary>
    public bool ForceLock { get; init; }

    /// <summary>Whether the per-leg ledger is asked for as data.</summary>
    public bool Json { get; init; }

    /// <summary>Run on what a previous sync staged, making nothing.</summary>
    public bool UseStaged { get; init; }

    /// <summary>Report the profile timing.</summary>
    public bool Time { get; init; }

    /// <summary>
    /// The host this machine is to the machine that dispatched a leg here, or <see langword="null"/> where this
    /// machine placed it itself.
    /// </summary>
    public HostId? Here { get; init; }

    /// <summary>Whether each leg brings up the set-ups its work names before that work, and down after it.</summary>
    public bool SetUp { get; init; }

    /// <summary>Whether <c>--local-copy</c> was given.</summary>
    public bool LocalCopy { get; init; }

    /// <summary>Whether <c>--in-place</c> was given.</summary>
    public bool InPlace { get; init; }
}

/// <summary>
/// Builds every selected leg: the use case the <c>build</c> command runs, and a step of an orchestrator run will call,
/// over the leg machinery both share.
/// </summary>
public interface IBuildLegs : IUseCase
{
    /// <summary>Builds every selected leg and answers what the run reached.</summary>
    /// <param name="request">What was asked.</param>
    /// <param name="run">The run, begun before anything could refuse it.</param>
    /// <param name="cancellationToken">Stops the run.</param>
    /// <returns>What the run reached, in the terms its caller answers in.</returns>
    Task<LegRunResult> RunAsync(BuildLegsRequest request, RunId run, CancellationToken cancellationToken = default);
}

/// <summary>What a build was asked: the legs, and nothing beyond what every command that runs legs takes.</summary>
public sealed record BuildLegsRequest : LegCommandRequest;

/// <summary>
/// Builds every selected leg, each in its own variant-keyed build directory, and checks that it produced what the
/// project declares.
/// </summary>
public sealed class BuildLegs(IBuildService builds, LegRunService runs) : IBuildLegs
{
    private readonly IBuildService _builds = builds;
    private readonly LegRunService _runs = runs;

    /// <inheritdoc/>
    public async Task<LegRunResult> RunAsync(BuildLegsRequest request, RunId run, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(run);

        return await _runs.ExecuteAsync(
            BuildService.CommandName,
            run,
            new LegRunRequest(
                request.Directory,
                request.LegNames,
                request.ForceLock,
                request.Json,
                request.UseStaged,
                request.Time,
                request.Here,
                RemoteArguments(request))
            {
                Workload = LegWorkload.BuildOnly,
                LocalTrees = LegTrees.Asked(request.LocalCopy, request.InPlace),
            },
            (work, token) => BuildLegAsync(_builds, work, token),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The options a host running one of this run's legs is given, so it runs the command this
    /// machine was asked to run rather than a bare one.
    /// </summary>
    /// <remarks>
    /// <c>--legs</c> and <c>--json</c> are supplied by the dispatch itself, and the lock and the
    /// staging are this machine's decisions about its own state: a host reached for one leg takes
    /// its own lock and acts on the copy sync just wrote.
    /// </remarks>
    private static IReadOnlyList<string> RemoteArguments(BuildLegsRequest request)
        => request.Time ? ["--time"] : [];

    private static async Task<LegEntry> BuildLegAsync(
        IBuildService builds,
        LegWork work,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var leg = work.Leg;

        var result = await builds
            .BuildAsync(work.Context.Config, leg.BuildRequestFor(work.Context.Config, work.RunDirectory, work.Time), cancellationToken)
            .ConfigureAwait(false);

        return new LegEntry
        {
            Leg = leg.Name,
            Verdict = result.Verdict.Verdict,
            Detail = Detail(result),
            Duration = Stopwatch.GetElapsedTime(started),
            CommandTime = result.Phases.Aggregate(TimeSpan.Zero, (total, phase) => total + phase.Duration),
            Emulated = leg.Emulated,
            Phases = [.. result.Phases.Select(phase => new PhaseRecord(phase.Phase, phase.Duration, phase.ClockStepped))],
            TimingNotes = result.Notes,
            Timings = [.. result.Phases.SelectMany(phase =>
                phase.Timings.Select(timing => new TimingMark(phase.Phase, timing.Text, timing.Value)))],
            Compilers = result.Compilers,
            LogTail = result.Tail,
        };
    }

    private static string Detail(BuildResult result)
        => result.Verdict.Detail.Length > 0
            ? result.Verdict.Detail
            : result.Dependencies is { } dependencies && dependencies.Excused.Count > 0
                ? $"{ReportText.Counted(dependencies.ObjectsRead, "object")} read, {dependencies.Excused.Count} excused"
                : string.Empty;
}
