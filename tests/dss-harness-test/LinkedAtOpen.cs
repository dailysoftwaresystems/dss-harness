using DssHarness.Core.FileSystem;

namespace DssHarness.Tests;

/// <summary>
/// A file system that makes <paramref name="directory"/> a real link to <paramref name="target"/> - the directory moved
/// aside, to its name with <c>-was</c> after it - the moment a file under it is opened to be read or written, after its
/// path was decided: the link put on a file's way between the decision and the act, which only where the open file
/// stands can tell.
/// </summary>
/// <param name="inner">The file system every other member passes through to.</param>
/// <param name="directory">The directory made a link, absolute.</param>
/// <param name="target">The directory the link leads to, absolute.</param>
internal sealed class LinkedAtOpen(IFileSystem inner, string directory, string target) : PassThroughFileSystem(inner)
{
    public override Stream OpenRead(string path)
    {
        Link(path);
        return base.OpenRead(path);
    }

    public override HeldDirectory Hold(string anchor, string held, bool make, string? asked = null)
    {
        Link(asked ?? held);
        return base.Hold(anchor, held, make, asked);
    }

    private void Link(string path)
    {
        if (path.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !IsLink(directory))
        {
            Directory.Move(directory, directory + "-was");
            TestLinks.DirectoryLink(directory, target);
        }
    }
}
