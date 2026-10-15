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

    /// <summary>
    /// A name directly in the root spelled otherwise than the root holds it: the entry held in another case
    /// where the file system folds, and nothing where it does not; any other path as the real file system answers.
    /// </summary>
    public override PathKind KindOf(string path)
    {
        if (Path.GetDirectoryName(path) is not { } parent || !string.Equals(parent, root, StringComparison.OrdinalIgnoreCase))
        {
            return base.KindOf(path);
        }

        var name = Path.GetFileName(path);
        var held = base.EnumerateDirectories(root).Concat(base.EnumerateFiles(root, recursive: false)).Select(Path.GetFileName).ToList();

        if (held.Contains(name, StringComparer.Ordinal))
        {
            return base.KindOf(Path.Combine(root, name));
        }

        return folds && held.Find(other => string.Equals(other, name, StringComparison.OrdinalIgnoreCase)) is { } spelled
            ? base.KindOf(Path.Combine(root, spelled))
            : PathKind.None;
    }
}
