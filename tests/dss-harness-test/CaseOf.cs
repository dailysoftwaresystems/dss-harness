using RepoHarness.Core.FileSystem;

namespace RepoHarness.Tests;

/// <summary>
/// The real file system, save that it finds <paramref name="root"/> by its name in another case only where
/// <paramref name="folds"/> says so: a file system that folds case, or one that does not, whatever this machine's does.
/// </summary>
/// <param name="inner">The file system every other member passes through to.</param>
/// <param name="root">The directory whose file system is asked how it compares names (<see cref="PathCase.In"/>).</param>
/// <param name="folds">Whether it finds the directory by its name in another case.</param>
internal sealed class CaseOf(IFileSystem inner, string root, bool folds) : PassThroughFileSystem(inner)
{
    public override bool DirectoryExists(string path)
        => path != root && string.Equals(path, root, StringComparison.OrdinalIgnoreCase) ? folds : base.DirectoryExists(path);
}
