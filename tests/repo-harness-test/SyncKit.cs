using NSubstitute;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Hosts;
using RepoHarness.Core.Legs;
using RepoHarness.Core.Repository;
using RepoHarness.Core.Sync;

namespace RepoHarness.Tests;

/// <summary>Builds the sync a test runs: this machine's side real, and whatever reaches a host what the test gives.</summary>
internal static class SyncKit
{
    /// <summary>
    /// The transport this machine reads its own tree through - the same a host runs on its own side - over
    /// <paramref name="fileSystem"/>, or the real one.
    /// </summary>
    public static LocalSyncTransport Transport(HarnessFactory harness, IFileSystem? fileSystem = null)
    {
        var files = fileSystem ?? harness.FileSystem;

        return new LocalSyncTransport(files, new ManifestBuilder(files, harness.Platform), harness.GitClient, harness.Platform);
    }

    /// <summary>
    /// A sync service over this machine's side, read through <paramref name="fileSystem"/> or the real one: its
    /// configuration read by <paramref name="loader"/>, its hosts surveyed by <paramref name="inspector"/> and reached
    /// through <paramref name="transports"/> - by default, nothing that could reach a host by accident.
    /// </summary>
    public static SyncService Service(
        HarnessFactory harness,
        IHarnessContextLoader? loader = null,
        IHostInspector? inspector = null,
        ISyncTransportFactory? transports = null,
        ISyncTransport? local = null,
        IFileSystem? fileSystem = null)
    {
        var files = fileSystem ?? harness.FileSystem;
        var contexts = loader ?? harness.ContextLoader;

        return new SyncService(
            contexts,
            new ManifestBuilder(files, harness.Platform),
            local ?? Transport(harness, files),
            transports ?? Substitute.For<ISyncTransportFactory>(),
            new LegsService(contexts, inspector ?? Substitute.For<IHostInspector>(), harness.Platform, harness.Output),
            harness.GitClient,
            files,
            harness.Platform,
            harness.Output);
    }
}
