using RepoHarness.Core.FileSystem;

namespace RepoHarness.Tests;

/// <summary>
/// The real file system, but for one file that cannot be deleted: one marked read-only, or held open by a scanner,
/// fails that way.
/// </summary>
/// <param name="inner">The file system everything else passes through to.</param>
/// <param name="file">The file whose deletion fails.</param>
internal sealed class UndeletableFile(IFileSystem inner, string file) : PassThroughFileSystem(inner)
{
    public override void DeleteFile(string path)
    {
        if (string.Equals(Path.GetFullPath(path), Path.GetFullPath(file), StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException("Access to the path is denied.");
        }

        base.DeleteFile(path);
    }
}
