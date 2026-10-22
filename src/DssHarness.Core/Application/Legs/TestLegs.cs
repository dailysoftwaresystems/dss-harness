using System.Diagnostics;
using DssHarness.Core.Build;
using DssHarness.Core.Execution;
using DssHarness.Core.Legs;
using DssHarness.Core.Runs;
using DssHarness.Core.Testing;

namespace DssHarness.Core.Application.Legs;

/// <summary>
/// Builds and tests every selected leg: the use case the <c>test</c> command runs, and a step of an orchestrator run will
/// call.
/// </summary>
public interface ITestLegs : IUseCase
{
    /// <summary>Builds and tests every selected leg and answers what the run reached.</summary>
    /// <param name="request">What was asked.</param>
    /// <param name="run">The run, begun before anything could refuse it.</param>
    /// <param name="cancellationToken">Stops the run.</param>
    /// <returns>What the run reached, in the terms its caller answers in.</returns>
    Task<LegRunResult> RunAsync(TestLegsRequest request, RunId run, CancellationToken cancellationToken = default);
}

/// <summary>What a test run was asked: the legs, and which of their tests.</summary>
public sealed record TestLegsRequest : LegWorkWithSetUpsRequest
{
    /// <summary>The tests to run, as the runner's own filter takes it; <see langword="null"/> for all of them.</summary>
    public string? Filter { get; init; }

    /// <summary>The tests to leave out, by name.</summary>
    public IReadOnlyList<string> Excludes { get; init; } = [];

    /// <summary>Only the tests these labels name.</summary>
    public IReadOnlyList<string> Labels { get; init; } = [];

    /// <summary>Test what is already built, without building first.</summary>
    public bool SkipBuild { get; init; }
}

/// <summary>
/// Builds and tests every selected leg, with a witness for each verdict: a zero exit code with no match, a tree that
/// moved, or another run in the build directory each report as themselves.
/// </summary>
public sealed class TestLegs(IBuildService builds, ITestService tests, CMakeToolchainReader toolchains, LegRunService runs) : ITestLegs
{
    private readonly IBuildService _builds = builds;
    private readonly ITestService _tests = tests;
    private readonly CMakeToolchainReader _toolchains = toolchains;
    private readonly LegRunService _runs = runs;

    /// <inheritdoc/>
    public async Task<LegRunResult> RunAsync(TestLegsRequest request, RunId run, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(run);

        // Read once, for the legs run here and the hosts given theirs alike: which tests run is one
        // choice, and a host told something else would report another suite under the same leg.
        var filter = request.Filter;

        return await _runs.ExecuteAsync(
            TestService.CommandName,
            run,
            new LegRunRequest(
                request.Directory,
                request.LegNames,
                request.ForceLock,
                request.Json,
                request.UseStaged,
                request.Time,
                request.Here,
                TestService.RemoteArguments(filter, request.Excludes, request.Labels, request.SkipBuild, request.Time))
            {
                // Built first unless told not to, and tested either way.
                Workload = new LegWorkload(Build: !request.SkipBuild, Test: true, []) { SetUp = request.SetUp },
                LocalTrees = LegTrees.Asked(request.LocalCopy, request.InPlace),
                Watch = request.Watch,
            },
            (work, token) => RunLegAsync(_builds, _tests, _toolchains, work, filter, request.Excludes, request.Labels, request.SkipBuild, token),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Builds the leg, then tests it. The order is fixed, and a build that did not pass ends the leg
    /// there: tests run against whatever the build left behind, so testing after a failed build
    /// reports on a binary nobody can name.
    /// </summary>
    private static async Task<LegEntry> RunLegAsync(
        IBuildService builds,
        ITestService tests,
        CMakeToolchainReader toolchains,
        LegWork work,
        string? filter,
        IReadOnlyList<string> excludes,
        IReadOnlyList<string> labels,
        bool skipBuild,
        CancellationToken cancellationToken)
    {
        var leg = work.Leg;
        var config = work.Context.Config;
        var started = Stopwatch.GetTimestamp();

        // Derived once from the placed leg, the same way the runner derives it.
        var (testProduct, testProductProblem) = leg.ProductFor(leg.BuildDirectory);

        var request = new TestRequest
        {
            Leg = leg.Name,
            ProgramDirectories = leg.Host.ProgramDirectories,
            TreeRoot = leg.HostTreeRoot,
            BuildDirectory = leg.BuildDirectory,
            RunDirectory = work.RunDirectory,
            LegSettings = leg.Leg,
            Project = leg.Project,
            PlatformKey = leg.Host.Os ?? string.Empty,
            Beside = leg.Beside,
            Identity = leg.IdentityFor(work.RunId.Value),
            Product = testProduct,
            ProductProblem = testProductProblem,

            // Read only once the leg is built, and only where the invocation names a compiler: under --no-build, from
            // the build already in its directory, which is what made the binaries it tests.
            Compilers = toolchains.NamedFor(leg.Project, leg.BuildDirectory),
            HostTestCores = leg.HostSettings.TestCores,
            HostEnvironment = leg.Environment,
            Filter = filter,
            Excludes = excludes,

            // By the host the dispatching machine named, where this machine runs a leg for another: a host
            // reached through a transport runs it in a copy of its own.
            InCopy = leg.InCopy,
            Labels = labels,
            Emulated = leg.Emulated,
            Time = work.Time,
        };

        // Refused before the build rather than after it: everything the command is made from is known now.
        tests.Check(config, request);

        // The compilers the binaries under test were built with: this build's, or - where the leg
        // tests a build it does not make - what its directory was last configured with.
        IReadOnlyList<CompilerFact> compilers;

        // What the build says beyond its verdict, which the leg's line carries as the build's own does.
        IReadOnlyList<string> built = [];

        if (!skipBuild)
        {
            var build = await builds
                .BuildAsync(config, leg.BuildRequestFor(config, work.RunDirectory), cancellationToken)
                .ConfigureAwait(false);

            compilers = build.Compilers;
            built = build.Notes;

            if (build.Verdict.Verdict != LegVerdict.Passed)
            {
                return new LegEntry
                {
                    Leg = leg.Name,
                    Verdict = build.Verdict.Verdict,
                    Detail = build.Verdict.Detail,
                    Duration = Stopwatch.GetElapsedTime(started),
                    Emulated = leg.Emulated,
                    TimingNotes = built,
                    Compilers = compilers,
                    LogTail = build.Tail,
                    TailPhase = build.TailPhase,
                };
            }
        }
        else
        {
            // Held to the toolchain as the build that made the directory is: binaries a compiler nobody
            // chose produced are no more tested than they would have been built, and a declared compiler
            // nothing established is unwitnessed here as there.
            var reading = toolchains.Configured(leg.Project, leg.BuildDirectory);
            compilers = reading?.Compilers ?? [];

            if (reading is not null && CompilerFacts.HeldTo(config, leg.Variant.Toolchain, reading) is { } held)
            {
                return new LegEntry
                {
                    Leg = leg.Name,
                    Verdict = held.Verdict,
                    Detail = held.Detail,
                    Duration = Stopwatch.GetElapsedTime(started),
                    Emulated = leg.Emulated,
                    Compilers = compilers,
                };
            }
        }

        var result = await tests.RunAsync(config, request, cancellationToken).ConfigureAwait(false);

        // The leg's whole duration, not the runner's: the build, the fingerprints and the sampling
        // are what the ledger reports as overhead, and leaving them out would hide them.
        return result.Entry with
        {
            Duration = Stopwatch.GetElapsedTime(started),
            TimingNotes = [.. built, .. result.Entry.TimingNotes],
            Compilers = compilers,
        };
    }
}
