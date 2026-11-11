using DssHarness.Core.FileSystem;
using DssHarness.Core.Legs;
using DssHarness.Core.Platform;
using DssHarness.Core.Repository;
using DssHarness.Core.Runners;

namespace DssHarness.Core.Application.Legs;

/// <summary>
/// Shows where each leg can run, or why it cannot: the use case the <c>legs</c> command answers, over the
/// measuring every command that places legs shares.
/// </summary>
public interface IShowLegs : IUseCase
{
    /// <summary>Checks the legs <paramref name="legNames"/> selects, or every declared leg where it is <see langword="null"/>.</summary>
    /// <param name="directory">A directory in the repository.</param>
    /// <param name="legNames">What <c>--legs</c> was given, or <see langword="null"/> for every declared leg.</param>
    /// <param name="cancellationToken">Stops the measuring.</param>
    /// <returns>Where each leg runs, and every host measured.</returns>
    Task<LegsReport> ShowAsync(string directory, IReadOnlyList<string>? legNames, CancellationToken cancellationToken = default);
}

/// <summary>
/// Shows where each leg can run: the pre-flight the command held - that every action a runner names resolves in
/// this tree, so an action nobody can read is an answer of no given now, rather than left for the run that finds
/// out - and the measuring, as one use case.
/// </summary>
public sealed class ShowLegs(
    LegsService legs,
    IHarnessContextLoader contextLoader,
    IFileSystem fileSystem,
    IHostPlatform platform) : IShowLegs
{
    private readonly LegsService _legs = legs;
    private readonly IHarnessContextLoader _contextLoader = contextLoader;
    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly IHostPlatform _platform = platform;

    /// <inheritdoc/>
    public async Task<LegsReport> ShowAsync(string directory, IReadOnlyList<string>? legNames, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        // Checked here, and again when a runner is finally invoked. 'legs' answers whether this
        // repository can run what it declares, so an action nobody can read is an answer of no
        // — given now, rather than left for the run that finds out.
        var harness = await _contextLoader
            .LoadAsync(directory, cancellationToken)
            .ConfigureAwait(false);

        ActionPath.RequireResolvable(
            harness.Config.ActionsByRunner(),
            harness.Layout.RunnerActionsDirectory,
            _fileSystem,
            _platform.PathComparison);

        return await _legs
            .CheckAsync(directory, legNames, LegWorkload.BuildAndTest, here: null, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }
}
