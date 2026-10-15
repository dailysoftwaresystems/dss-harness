using RepoHarness.Core.FileSystem;

namespace RepoHarness.Tests;

/// <summary>
/// The real file system, but for one directory whose files cannot be listed: a share that dropped, or one this user may
/// not read, fails that way.
/// </summary>
/// <param name="inner">The file system everything else passes through to.</param>
/// <param name="directory">The directory whose listing fails.</param>
/// <param name="failure">What the listing throws.</param>
internal sealed class UnlistableDirectory(IFileSystem inner, string directory, Func<Exception> failure) : PassThroughFileSystem(inner)
{
    public override IEnumerable<string> EnumerateFiles(string path, bool recursive)
        => string.Equals(Path.GetFullPath(path), Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase)
            ? throw failure()
            : base.EnumerateFiles(path, recursive);
}
