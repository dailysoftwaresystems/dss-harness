using DssHarness.Core.FileSystem;

namespace DssHarness.Tests;

/// <summary>
/// The one way the tool reads, writes, copies or removes a file of its own inside a place an isolated process writes:
/// never through a link, whether it stands on the way when looked at or is put there after.
/// </summary>
public sealed class UnfollowedFilesTests
{
    /// <summary>
    /// A file reached through a link - its own, or a directory's on the way to it - is neither read, written, copied nor
    /// removed, each refused naming the link, and what the link leads to is untouched; a link at a written file's own place
    /// is replaced, never written through; a file reached through none is read, written and copied as it is.
    /// </summary>
    [Fact]
    public void AFileReachedThroughALink_IsNeitherReadWrittenNorRemoved_AndWhatTheLinkLeadsToIsUntouched()
    {
        using var root = new TempDirectory();
        using var outside = new TempDirectory();
        var fileSystem = new HarnessFactory().FileSystem;
        var hidden = outside.WriteFile("hidden.txt", "hidden\n");
        var report = root.Combine("out", "report.txt");
        var through = root.Combine("dir", "hidden.txt");
        Directory.CreateDirectory(root.Combine("out"));

        if (!TryLink(report, hidden, file: true) || !TryLink(root.Combine("dir"), outside.Path))
        {
            Assert.Skip("This machine does not allow symbolic links to be made.");
        }

        var read = Assert.Throws<IOException>(() => UnfollowedFiles.OpenRead(fileSystem, root.Path, report));
        Assert.Equal($"'{report}' is reached through the link '{report}', which the tool never follows in a place an isolated process writes", read.Message);
        var readThrough = Assert.Throws<IOException>(() => UnfollowedFiles.OpenRead(fileSystem, root.Path, through));
        Assert.Equal($"'{through}' is reached through the link '{root.Combine("dir")}', which the tool never follows in a place an isolated process writes", readThrough.Message);
        Assert.Throws<IOException>(() => UnfollowedFiles.Write(fileSystem, root.Path, through, stream => stream.Write("planted\n"u8)));
        Assert.Throws<IOException>(() => UnfollowedFiles.Copy(fileSystem, root.Path, report, root.Path, root.Combine("kept.txt")));
        Assert.Throws<IOException>(() => UnfollowedFiles.Delete(fileSystem, root.Path, through));
        Assert.Throws<IOException>(() => UnfollowedFiles.CreateDirectory(fileSystem, root.Path, root.Combine("dir", "made")));

        Assert.Equal("hidden\n", File.ReadAllText(hidden));
        Assert.Equal(["hidden.txt"], Directory.EnumerateFileSystemEntries(outside.Path).Select(Path.GetFileName));
        Assert.False(File.Exists(root.Combine("kept.txt")));

        UnfollowedFiles.Write(fileSystem, root.Path, report, stream => stream.Write("own\n"u8));

        Assert.Equal((PathKind.File, "own\n", "hidden\n"), (fileSystem.KindOf(report), File.ReadAllText(report), File.ReadAllText(hidden)));

        UnfollowedFiles.Copy(fileSystem, root.Path, report, root.Path, root.Combine("kept", "report.txt"));
        Assert.True(TryLink(root.Combine("out", "again.txt"), hidden, file: true));
        UnfollowedFiles.Delete(fileSystem, root.Path, root.Combine("out", "again.txt"));

        Assert.Equal("own\n", File.ReadAllText(root.Combine("kept", "report.txt")));
        Assert.Equal((PathKind.None, "hidden\n"), (fileSystem.KindOf(root.Combine("out", "again.txt")), File.ReadAllText(hidden)));
    }

    /// <summary>
    /// A link put on the way in the instant after the look - here, one the look was told is a directory - is caught by
    /// where the system names the open file: nothing read, and of a write, nothing written and the file begun removed.
    /// </summary>
    [Fact]
    public async Task ALinkPutOnTheWayAfterTheLook_IsCaughtWhereTheOpenFileStands()
    {
        using var root = new TempDirectory();
        using var outside = new TempDirectory();
        var hidden = outside.WriteFile("hidden.txt", "hidden\n");

        if (!TryLink(root.Combine("dir"), outside.Path))
        {
            Assert.Skip("This machine does not allow symbolic links to be made.");
        }

        var unlooked = new LinksLookLikeDirectories(new HarnessFactory().FileSystem);
        var through = root.Combine("dir", "hidden.txt");

        var read = Assert.Throws<IOException>(() => UnfollowedFiles.OpenRead(unlooked, root.Path, through));
        var written = Assert.Throws<IOException>(() => UnfollowedFiles.Write(unlooked, root.Path, root.Combine("dir", "planted.txt"), stream => stream.Write("planted\n"u8)));
        var writtenWhole = await Assert.ThrowsAsync<IOException>(
            () => UnfollowedFiles.WriteAsync(unlooked, root.Path, root.Combine("dir", "planted.txt"), "planted\n"u8.ToArray(), TestContext.Current.CancellationToken));

        Assert.StartsWith($"'{through}' was opened as '", read.Message, StringComparison.Ordinal);
        Assert.EndsWith("', where a link put on its way leads, which the tool never follows in a place an isolated process writes", read.Message, StringComparison.Ordinal);
        Assert.StartsWith($"'{root.Combine("dir", "planted.txt")}' was opened as '", written.Message, StringComparison.Ordinal);
        Assert.StartsWith($"'{root.Combine("dir", "planted.txt")}' was opened as '", writtenWhole.Message, StringComparison.Ordinal);
        Assert.Equal(["hidden.txt"], Directory.EnumerateFileSystemEntries(outside.Path).Select(Path.GetFileName));
        Assert.Equal("hidden\n", File.ReadAllText(hidden));
    }

    /// <summary>A file system whose look at a path takes a link for a directory: a link put there in the instant after the look.</summary>
    private sealed class LinksLookLikeDirectories(IFileSystem inner) : PassThroughFileSystem(inner)
    {
        public override PathKind KindOf(string path) => base.KindOf(path) is PathKind.Link ? PathKind.Directory : base.KindOf(path);
    }

    /// <summary>Makes a link, answering whether this machine allowed it.</summary>
    internal static bool TryLink(string path, string target, bool file = false)
    {
        try
        {
            if (file)
            {
                File.CreateSymbolicLink(path, target);
            }
            else
            {
                Directory.CreateSymbolicLink(path, target);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
