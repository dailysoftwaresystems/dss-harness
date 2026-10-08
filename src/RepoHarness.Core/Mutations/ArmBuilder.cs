using RepoHarness.Core.Build;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Processes;
using RepoHarness.Core.Results;

namespace RepoHarness.Core.Mutations;

/// <summary>The program a runner target builds, or why it builds none a sweep can run. Exactly one of the two.</summary>
internal sealed record WorkerProgram
{
    private WorkerProgram(string? path, string? problem)
    {
        Path = path;
        Problem = problem;
    }

    /// <summary>The program, relative to the build directory, or <see langword="null"/> where there is none.</summary>
    public string? Path { get; }

    /// <summary>Why there is none, as a line says it; <see langword="null"/> where there is one.</summary>
    public string? Problem { get; }

    /// <summary>The program <paramref name="path"/> is.</summary>
    /// <param name="path">The program, relative to the build directory.</param>
    public static WorkerProgram Of(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        return new(path, null);
    }

    /// <summary>No program, for <paramref name="problem"/>.</summary>
    /// <param name="problem">Why there is none, as a line says it.</param>
    public static WorkerProgram None(string problem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(problem);

        return new(null, problem);
    }
}

/// <summary>
/// What a worker's build says of how it builds each target, read from its own records once its baseline build is done: an
/// arm's pre-flight asks it what the arm's targets build and what depends on the arm's sites, and a failed build which of
/// its steps failed.
/// </summary>
internal interface IWorkerGraph
{
    /// <summary>
    /// The files <paramref name="target"/> builds, relative to the build directory: the one it names, or each a target
    /// standing for several does; none where no build line builds it.
    /// </summary>
    /// <param name="target">The target, as <c>--target</c> names it.</param>
    IReadOnlyList<string> OutputsOf(string target);

    /// <summary>The program <paramref name="target"/> builds, or why it builds none.</summary>
    /// <param name="target">The runner's target.</param>
    WorkerProgram ProgramOf(string target);

    /// <summary>The objects <paramref name="targets"/> build that depend on any of <paramref name="sites"/>, as <see cref="RebuildWitness.DependentObjects"/> gives them.</summary>
    /// <param name="targets">The targets a build builds.</param>
    /// <param name="sites">The files mutated, absolute.</param>
    IReadOnlyList<string> DependentObjects(IReadOnlyList<string> targets, IReadOnlyCollection<string> sites);

    /// <summary>The steps a build's <paramref name="lines"/> say failed, as <see cref="Ninja.FailedOutputs"/> names them.</summary>
    /// <param name="lines">The build phase's output, a line at a time.</param>
    IReadOnlyList<string> FailedOutputs(IEnumerable<string> lines);
}

/// <summary>Builds in a worker's copy, and reads what its builds recorded.</summary>
internal interface IArmBuilder
{
    /// <summary>Builds <paramref name="request"/>, with every guard a leg's build has.</summary>
    /// <param name="config">The whole configuration.</param>
    /// <param name="request">The build: the worker's copy, and the project or the targets an arm builds.</param>
    /// <param name="cancellationToken">Stops the build.</param>
    Task<BuildResult> BuildAsync(HarnessConfig config, BuildRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Reads how the build of <paramref name="request"/> builds each target: its manifest and its dependency records.
    /// </summary>
    /// <param name="config">The whole configuration.</param>
    /// <param name="request">The build, as it last ran in the worker.</param>
    /// <param name="cancellationToken">Stops ninja.</param>
    /// <exception cref="Results.HarnessException">
    /// ninja could not answer for the build's dependency records, or its manifest could not be read whole.
    /// </exception>
    Task<IWorkerGraph> ReadGraphAsync(HarnessConfig config, BuildRequest request, CancellationToken cancellationToken);

    /// <summary>What <paramref name="buildDirectory"/>'s <c>.ninja_log</c> says now, or <see langword="null"/> where it has none ninja wrote.</summary>
    /// <param name="buildDirectory">The worker's build directory.</param>
    NinjaLog? ReadLog(string buildDirectory);
}

/// <inheritdoc cref="IArmBuilder"/>
/// <remarks>
/// The build is the leg's own build service, so a worker's build is held to every guard a leg's is - its inputs held still,
/// its compilers the ones its toolchain names, nothing else in its directory - and records itself as a leg's does, so a
/// worker kept between sweeps rebuilds from clean whenever a change could be missed. The records are read as the
/// dependency check reads them.
/// </remarks>
internal sealed class ArmBuilder(
    IBuildService buildService,
    IProcessRunner processRunner,
    BuildDirectoryGuard buildDirectoryGuard,
    IFileSystem fileSystem) : IArmBuilder
{
    private readonly IBuildService _buildService = buildService;
    private readonly IProcessRunner _processRunner = processRunner;
    private readonly BuildDirectoryGuard _buildDirectoryGuard = buildDirectoryGuard;
    private readonly IFileSystem _fileSystem = fileSystem;

    /// <inheritdoc/>
    public Task<BuildResult> BuildAsync(HarnessConfig config, BuildRequest request, CancellationToken cancellationToken)
        => _buildService.BuildAsync(config, request, cancellationToken);

    /// <inheritdoc/>
    public async Task<IWorkerGraph> ReadGraphAsync(HarnessConfig config, BuildRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(request);

        var buildDirectory = request.Variant.DirectoryUnder(request.TreeRoot);
        var environment = BuildAdapters.EnvironmentFor(request.Variant.Overlay(config, request.Project), request);
        var program = _buildDirectoryGuard.Read(buildDirectory)?.MakeProgram;
        var records = await NinjaRebuildGraph
            .ReadRecordsAsync(_processRunner, buildDirectory, request.ProgramDirectories, program, environment, cancellationToken)
            .ConfigureAwait(false);
        var manifest = NinjaManifest.Read(_fileSystem, buildDirectory);

        if (manifest.PassedOver.Count > 0)
        {
            // Read with what was left, the manifest says the build builds less than it does: a target no line builds, a
            // site no object depends on - each an arm violated, the registry blamed for a file this could not read.
            throw new HarnessException(
                HarnessExit.CommandFailed,
                $"ninja's manifest in '{buildDirectory}' could not be read whole, so what the build there builds is not known: "
                + string.Join("; ", manifest.PassedOver.Select(file => $"'{file.File}' {file.Why}")) + ".");
        }

        return new NinjaWorkerGraph(
            new NinjaRebuildGraph(buildDirectory, manifest, records, _fileSystem, PathCase.In(_fileSystem, buildDirectory)),
            program);
    }

    /// <inheritdoc/>
    public NinjaLog? ReadLog(string buildDirectory)
    {
        var log = Path.Combine(buildDirectory, NinjaLog.FileName);

        if (!_fileSystem.FileExists(log))
        {
            return null;
        }

        using var reader = new StreamReader(_fileSystem.OpenRead(log));

        return NinjaLog.Read(Lines(reader));
    }

    private static IEnumerable<string> Lines(StreamReader reader)
    {
        while (reader.ReadLine() is { } line)
        {
            yield return line;
        }
    }
}

/// <summary>A worker's build as ninja's manifest and dependency records say it builds each target.</summary>
/// <param name="graph">The build directory's rebuild graph.</param>
/// <param name="program">The ninja CMake recorded the build runs, or <see langword="null"/>.</param>
internal sealed class NinjaWorkerGraph(NinjaRebuildGraph graph, string? program) : IWorkerGraph
{
    private readonly NinjaRebuildGraph _graph = graph;
    private readonly string? _program = program;

    /// <inheritdoc/>
    public IReadOnlyList<string> OutputsOf(string target)
    {
        var manifest = _graph.Manifest;

        if (manifest.ArtifactOf(target) is { } artifact)
        {
            return [artifact];
        }

        // A target standing for several files - a group of targets, or one that builds more than one - builds each.
        return manifest.EdgeFor(target) is { Rule: NinjaManifest.PhonyRule } phony
            ? [.. phony.Inputs.Select(manifest.ArtifactOf).OfType<string>().Distinct(StringComparer.Ordinal)]
            : [];
    }

    /// <inheritdoc/>
    public WorkerProgram ProgramOf(string target)
    {
        var manifest = _graph.Manifest;

        if (manifest.EdgeFor(target) is null)
        {
            return WorkerProgram.None($"runner '{target}' is built by no line of the leg's build");
        }

        if (manifest.ArtifactOf(target) is not { } artifact)
        {
            return WorkerProgram.None($"runner '{target}' stands for several files, so it names no one program to run");
        }

        return manifest.EdgeFor(artifact) is { } edge && CMakeNinjaRule.LinksAProgram(edge)
            ? WorkerProgram.Of(artifact)
            : WorkerProgram.None($"runner '{target}' builds '{artifact}', which its build makes as no program");
    }

    /// <inheritdoc/>
    public IReadOnlyList<string> DependentObjects(IReadOnlyList<string> targets, IReadOnlyCollection<string> sites)
        => RebuildWitness.DependentObjects(_graph, _graph.Manifest.Closure(targets), sites);

    /// <inheritdoc/>
    public IReadOnlyList<string> FailedOutputs(IEnumerable<string> lines) => Ninja.FailedOutputs(lines, _program, _graph.Manifest);
}
