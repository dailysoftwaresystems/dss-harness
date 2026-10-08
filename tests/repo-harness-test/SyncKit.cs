using NSubstitute;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Git;
using RepoHarness.Core.Hosts;
using RepoHarness.Core.Legs;
using RepoHarness.Core.Mutations;
using RepoHarness.Core.Repository;
using RepoHarness.Core.Sync;

namespace RepoHarness.Tests;

/// <summary>
/// Builds the sync a test runs: this machine's side real, or read through the file system the test gives, and
/// whatever reaches a host what the test gives.
/// </summary>
internal static class SyncKit
{
    /// <summary>
    /// Reads <paramref name="sourceRoot"/> and syncs it at once, as the sync command does for one host: the tree as it
    /// stands, for a test about anything but what moves between the reading and the carrying.
    /// </summary>
    public static async Task<SyncResult> SyncAsync(
        this ISyncService service,
        string sourceRoot,
        ISyncTransport transport,
        string destinationRoot,
        SyncOptions options,
        CancellationToken cancellationToken = default)
        => await service.SyncAsync(
            await service.ReadSourceAsync(sourceRoot, cancellationToken),
            transport,
            destinationRoot,
            options,
            cancellationToken);

    /// <summary>
    /// A path beside <paramref name="temp"/>'s tree, never in it, for a copy a test syncs into: a copy inside the tree
    /// would be part of the tree the next reading of it takes.
    /// </summary>
    public static string CopyPath(TempDirectory temp)
        => Path.GetFullPath(Path.Combine(temp.Path, "..", "copy-" + Guid.NewGuid().ToString("N")[..8]));

    /// <summary>Removes a copy a test synced into, warning rather than failing where it cannot.</summary>
    public static void DeleteIfPresent(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                new HarnessFactory().FileSystem.DeleteDirectory(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TestContext.Current.AddWarning($"The sync copy at '{path}' could not be deleted: {ex.Message}");
        }
    }

    /// <summary>
    /// What a sync to <paramref name="host"/> raises where <paramref name="path"/> <paramref name="what"/> after the tree
    /// was read, before it was carried to <paramref name="copy"/>.
    /// </summary>
    public static string Moved(HostId host, string path, string what, string copy)
        => $"{host}: '{path}' {what} after the tree was read for this command, before it was carried to '{copy}', so "
            + "that copy cannot be made the tree that was read. It is left part made, and marked so: nothing runs against it "
            + "until a sync finishes it. Let the tree settle, then run again.";

    /// <summary>
    /// The transport this machine reads its own tree through - the same a host runs on its own side - over
    /// <paramref name="fileSystem"/>, or the real one.
    /// </summary>
    public static LocalSyncTransport Transport(HarnessFactory harness, IFileSystem? fileSystem = null)
    {
        var files = fileSystem ?? harness.FileSystem;

        return new LocalSyncTransport(
            files,
            new ManifestBuilder(files, harness.Platform),
            harness.GitClient,
            harness.Platform,
            MutationWorkers.CopyClaims(files, harness.Output, harness.Identity));
    }

    /// <summary>
    /// A sync service over this machine's side, read through <paramref name="fileSystem"/> or the real one: its
    /// configuration read by <paramref name="loader"/>, its hosts surveyed by <paramref name="inspector"/> and reached
    /// through <paramref name="transports"/> - by default, nothing that could reach a host by accident - and its own
    /// tree's files read for carrying through <paramref name="local"/> where the test gives one; asking
    /// <paramref name="git"/> of the tree, or the harness's own.
    /// </summary>
    public static SyncService Service(
        HarnessFactory harness,
        IHarnessContextLoader? loader = null,
        IHostInspector? inspector = null,
        ISyncTransportFactory? transports = null,
        ISyncTransport? local = null,
        IFileSystem? fileSystem = null,
        IGitClient? git = null)
    {
        var files = fileSystem ?? harness.FileSystem;
        var contexts = loader ?? harness.ContextLoader;

        return new SyncService(
            contexts,
            new ManifestBuilder(files, harness.Platform),
            local ?? Transport(harness, files),
            transports ?? Substitute.For<ISyncTransportFactory>(),
            new LegsService(contexts, inspector ?? Substitute.For<IHostInspector>(), harness.Platform, harness.Output),
            git ?? harness.GitClient,
            files,
            harness.Platform,
            harness.Output);
    }
}
