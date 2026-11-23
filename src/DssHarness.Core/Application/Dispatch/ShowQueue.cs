using DssHarness.Core.Configuration;
using DssHarness.Core.Execution;
using DssHarness.Core.Hosts;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;

namespace DssHarness.Core.Application.Dispatch;

/// <summary>
/// Shows this machine's dispatch queue: the use case the <c>queue</c> command answers - each class of unit, its
/// capacity, its holders and its waiting units, by tenant - read from the queue's own records, changing nothing.
/// </summary>
public interface IShowQueue : IUseCase
{
    /// <summary>
    /// The queue as it stands: each class, its capacity, who holds and who waits, the capacities by the rule a heavy leg of
    /// the tree <paramref name="directory"/> is in would be admitted by - the machine's settings file, then that tree's
    /// config.json - or by the settings file alone where it is in no initialised repository.
    /// </summary>
    /// <param name="directory">The directory the command was typed in.</param>
    /// <param name="cancellationToken">Stops the reading.</param>
    /// <returns>The queue's classes, in the order the queue keeps them.</returns>
    /// <exception cref="HarnessException">
    /// The queue's records, this machine's settings file or the tree's config.json could not be read.
    /// </exception>
    Task<IReadOnlyList<DispatchClass>> ShowAsync(string directory, CancellationToken cancellationToken = default);
}

/// <summary>
/// Shows this machine's dispatch queue as its one owner reads it (<see cref="MachineDispatchQueue.Queue"/>), with the rule
/// in force read through its own owners - which sections, in which order (<see cref="AdmissionSection.For"/>), and each
/// field's value (<see cref="AdmissionSettings.InForce"/>) - so <c>queue</c> and <c>legs</c> never say two rules: a front
/// end asks the use case, never the owner that admits units by the same records.
/// </summary>
/// <param name="queue">The machine's dispatch queue.</param>
/// <param name="settings">This machine's settings file.</param>
/// <param name="locator">Finds the repository the directory is in, where it is in one.</param>
/// <param name="contextLoader">Loads that repository's configuration.</param>
public sealed class ShowQueue(
    MachineDispatchQueue queue,
    MachineSettingsFile settings,
    IRepositoryLocator locator,
    IHarnessContextLoader contextLoader) : IShowQueue
{
    private readonly MachineDispatchQueue _queue = queue;
    private readonly MachineSettingsFile _settings = settings;
    private readonly IRepositoryLocator _locator = locator;
    private readonly IHarnessContextLoader _contextLoader = contextLoader;

    /// <inheritdoc/>
    public async Task<IReadOnlyList<DispatchClass>> ShowAsync(string directory, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        var config = await _locator.LocateAsync(directory, cancellationToken).ConfigureAwait(false) is null
            ? null
            : await ConfigAsync(directory, cancellationToken).ConfigureAwait(false);

        AdmissionInForce? inForce;

        try
        {
            inForce = AdmissionSettings.InForce(
                config is null
                    ? [AdmissionSection.SettingsFile(_settings.Read().Admission, _settings.Location ?? "nowhere")]
                    : AdmissionSection.For(HostId.Local, here: null, () => _settings.Answer(path => path), dispatched: null, config));
        }
        catch (ConfigException ex)
        {
            throw new HarnessException(HarnessExit.ConfigInvalid, ex.Message, ex);
        }

        return _queue.Queue(inForce?.Rule);
    }

    /// <summary>The configuration of the repository <paramref name="directory"/> is in; none where it was never initialised, which declares nothing.</summary>
    private async Task<HarnessConfig?> ConfigAsync(string directory, CancellationToken cancellationToken)
    {
        try
        {
            return (await _contextLoader.LoadAsync(directory, cancellationToken).ConfigureAwait(false)).Config;
        }
        catch (HarnessException ex) when (ex.ExitCode == HarnessExit.NotInitialized)
        {
            return null;
        }
    }
}
