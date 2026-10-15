using DssHarness.Core.FileSystem;

namespace DssHarness.Tests;

/// <summary>
/// The real file system, save that it finds <paramref name="root"/>, and a name <paramref name="root"/> holds, by its name
/// in another case only where <paramref name="folds"/> says so: a file system that folds case, or one that does not,
/// whatever this machine's does.
/// </summary>
/// <param name="inner">The file system every other member passes through to.</param>
/// <param name="root">The directory whose file system is asked how it compares names (<see cref="PathCase.In"/>).</param>
/// <param name="folds">Whether it finds the directory, and a name it holds, by its name in another case.</param>
internal sealed class CaseOf(IFileSystem inner, string root, bool folds) : PassThroughFileSystem(inner)
{
    public override bool DirectoryExists(string path)
        => path != root && string.Equals(path, root, StringComparison.OrdinalIgnoreCase) ? folds : base.DirectoryExists(path);

    public override PathKind KindOf(string path)
        => !folds && Path.GetDirectoryName(path) is { } parent && string.Equals(parent, root, StringComparison.OrdinalIgnoreCase) && !Holds(parent, Path.GetFileName(path))
            ? PathKind.None
            : base.KindOf(path);

    /// <summary>Whether <paramref name="directory"/> holds <paramref name="name"/> spelled so.</summary>
    private bool Holds(string directory, string name)
        => base.EnumerateDirectories(directory).Concat(base.EnumerateFiles(directory, recursive: false)).Any(held => Path.GetFileName(held) == name);
}
