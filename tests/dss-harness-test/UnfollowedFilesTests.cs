using DssHarness.Core.FileSystem;

namespace DssHarness.Tests;

/// <summary>
/// The one way the tool reads, writes, copies, moves or removes a file or a directory of its own inside a place an isolated
/// process writes: never through a link, whether it stands on the way when looked at or is swapped in while the tool acts.
/// </summary>
public sealed class UnfollowedFilesTests
{
    /// <summary>
    /// A file reached through a link - its own, or a directory's on the way to it - is neither read, written, copied, moved
    /// nor removed, each refused naming the link, and what the link leads to is untouched; a link at a written file's own
    /// place is replaced, never written through, and one removed is removed itself; a file reached through none is read,
    /// written, copied and moved as it is.
    /// </summary>
    [Fact]
    public void AFileReachedThroughALink_IsNeitherReadWrittenNorRemoved_AndWhatTheLinkLeadsToIsUntouched()
    {
        using var root = new TempDirectory();
        using var outside = new TempDirectory();
        var fileSystem = new HarnessFactory().FileSystem;
        var hidden = outside.WriteFile("hidden.txt", "hidden\n");
        var report = root.Combine("out", "report.txt");
        var dir = root.Combine("dir");
        var through = root.Combine("dir", "hidden.txt");
        Directory.CreateDirectory(root.Combine("out"));

        if (!TryLink(report, hidden, file: true) || !TryLink(dir, outside.Path))
        {
            Assert.Skip("This machine does not allow symbolic links to be made.");
        }

        string Refusal(string asked, string link) => $"'{asked}' is reached through the link '{link}', which the tool never follows in a place an isolated process writes";

        Assert.Equal(Refusal(report, report), Assert.Throws<LinkOnTheWayException>(() => UnfollowedFiles.OpenRead(fileSystem, root.Path, report)).Message);
        Assert.Equal(Refusal(through, dir), Assert.Throws<LinkOnTheWayException>(() => UnfollowedFiles.OpenRead(fileSystem, root.Path, through)).Message);
        Assert.Equal(dir, Assert.Throws<LinkOnTheWayException>(() => UnfollowedFiles.Write(fileSystem, root.Path, through, stream => stream.Write("planted\n"u8))).Link);
        Assert.Throws<LinkOnTheWayException>(() => UnfollowedFiles.Copy(fileSystem, root.Path, report, root.Path, root.Combine("kept.txt")));
        Assert.Throws<LinkOnTheWayException>(() => UnfollowedFiles.Delete(fileSystem, root.Path, through));
        Assert.Throws<LinkOnTheWayException>(() => UnfollowedFiles.DeleteDirectory(fileSystem, root.Path, root.Combine("dir", "sub")));
        Assert.Throws<LinkOnTheWayException>(() => UnfollowedFiles.CreateDirectory(fileSystem, root.Path, root.Combine("dir", "made")));
        Assert.Throws<LinkOnTheWayException>(() => UnfollowedFiles.Move(fileSystem, root.Path, through, root.Combine("taken.txt"), replace: false));

        Assert.Equal("hidden\n", File.ReadAllText(hidden));
        Assert.Equal(["hidden.txt"], Directory.EnumerateFileSystemEntries(outside.Path).Select(Path.GetFileName));
        Assert.False(File.Exists(root.Combine("kept.txt")));
        Assert.False(File.Exists(root.Combine("taken.txt")));

        UnfollowedFiles.Write(fileSystem, root.Path, report, stream => stream.Write("own\n"u8));

        Assert.Equal((PathKind.File, "own\n", "hidden\n"), (fileSystem.KindOf(report), File.ReadAllText(report), File.ReadAllText(hidden)));

        UnfollowedFiles.Copy(fileSystem, root.Path, report, root.Path, root.Combine("kept", "report.txt"));
        UnfollowedFiles.Move(fileSystem, root.Path, root.Combine("kept", "report.txt"), root.Combine("moved", "report.txt"), replace: false);
        Assert.True(TryLink(root.Combine("out", "again.txt"), hidden, file: true));
        UnfollowedFiles.Delete(fileSystem, root.Path, root.Combine("out", "again.txt"));

        Assert.Equal("own\n", File.ReadAllText(root.Combine("moved", "report.txt")));
        Assert.False(File.Exists(root.Combine("kept", "report.txt")));
        Assert.Equal((PathKind.None, "hidden\n"), (fileSystem.KindOf(root.Combine("out", "again.txt")), File.ReadAllText(hidden)));
    }

    /// <summary>
    /// A directory removed whole removes each link among what it holds as the link - a file link and a directory link alike -
    /// and never what one leads to; a link at the directory's own place is removed as the link.
    /// </summary>
    [Fact]
    public void ADirectoryRemovedWhole_RemovesEachLinkItHolds_AsItself()
    {
        using var root = new TempDirectory();
        using var outside = new TempDirectory();
        var fileSystem = new HarnessFactory().FileSystem;
        var hidden = outside.WriteFile("hidden.txt", "hidden\n");
        root.WriteFile(Path.Combine("tree", "own.txt"), "own\n");
        root.WriteFile(Path.Combine("tree", "deep", "own.txt"), "own\n");

        if (!TryLink(root.Combine("tree", "file-link.txt"), hidden, file: true) || !TryLink(root.Combine("tree", "deep", "dir-link"), outside.Path) || !TryLink(root.Combine("linked"), outside.Path))
        {
            Assert.Skip("This machine does not allow symbolic links to be made.");
        }

        UnfollowedFiles.DeleteDirectory(new HarnessFactory().FileSystem, root.Path, root.Combine("tree"));
        UnfollowedFiles.DeleteDirectory(fileSystem, root.Path, root.Combine("linked"));
        UnfollowedFiles.DeleteDirectory(fileSystem, root.Path, root.Combine("never-there"));

        Assert.Empty(Directory.EnumerateFileSystemEntries(root.Path));
        Assert.Equal(["hidden.txt"], Directory.EnumerateFileSystemEntries(outside.Path).Select(Path.GetFileName));
        Assert.Equal("hidden\n", File.ReadAllText(hidden));
    }

    /// <summary>
    /// A directory on the way swapped for a link to a place outside, back and forth, while the tool removes, writes, moves
    /// into and reads from it, leads nothing outside: each acts relative to the directory it holds, opened as itself, so
    /// the file outside stays as it was, nothing lands beside it, and every read gives the file inside.
    /// </summary>
    [Fact]
    public async Task ADirectorySwappedForALink_WhileTheToolActs_LeadsNothingOutside()
    {
        using var root = new TempDirectory();
        using var outside = new TempDirectory();
        var fileSystem = new HarnessFactory().FileSystem;
        var x = root.Combine("x");
        var real = root.Combine("x-dir");
        var link = root.Combine("x-link");
        var victim = outside.WriteFile("victim.txt", "the user's own file");
        File.WriteAllText(Path.Combine(outside.Path, "read.txt"), "hidden");
        Directory.CreateDirectory(real);
        File.WriteAllText(Path.Combine(real, "read.txt"), "inside");

        if (!TryLink(link, outside.Path))
        {
            Assert.Skip("This machine does not allow symbolic links to be made.");
        }

        using var stop = new CancellationTokenSource();
        var swapper = Task.Run(() => Swap(x, real, link, stop.Token), TestContext.Current.CancellationToken);
        var reads = new List<string>();

        try
        {
            for (var until = Environment.TickCount64 + 3000; Environment.TickCount64 < until && Directory.GetFiles(outside.Path).Length == 2;)
            {
                Quietly(() => File.WriteAllText(Path.Combine(real, "victim.txt"), "the tool's own"));
                Quietly(() => File.WriteAllText(root.Combine("payload.txt"), "payload"));
                Quietly(() => UnfollowedFiles.Delete(fileSystem, root.Path, Path.Combine(x, "victim.txt")));
                Quietly(() => UnfollowedFiles.Write(fileSystem, root.Path, Path.Combine(x, "written.txt"), stream => stream.Write("planted"u8)));
                Quietly(() => UnfollowedFiles.Move(fileSystem, root.Path, root.Combine("payload.txt"), Path.Combine(x, "landed.txt"), replace: true));
                Quietly(() =>
                {
                    using var reader = new StreamReader(UnfollowedFiles.OpenRead(fileSystem, root.Path, Path.Combine(x, "read.txt")));
                    reads.Add(reader.ReadToEnd());
                });
            }
        }
        finally
        {
            await stop.CancelAsync();
            await swapper;
        }

        Assert.Equal(["read.txt", "victim.txt"], Directory.GetFiles(outside.Path).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal(("the user's own file", "hidden"), (File.ReadAllText(victim), File.ReadAllText(Path.Combine(outside.Path, "read.txt"))));
        Assert.All(reads, read => Assert.Equal("inside", read));
    }

    /// <summary>A write over a file keeps the mode the file it replaces had, on Linux and macOS: a script stays one to run.</summary>
    [Fact]
    public void AWriteOverAFile_KeepsItsMode()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows keeps no execute bit with a file.");

        using var root = new TempDirectory();
        var harness = new HarnessFactory();
        var script = root.WriteFile("run.sh", "#!/bin/sh\n");
        var plain = root.WriteFile("plain.txt", "plain\n");
        harness.FilePermissions.SetExecutable(script, executable: true);

        UnfollowedFiles.Write(harness.FileSystem, root.Path, script, stream => stream.Write("#!/bin/sh\necho\n"u8));
        UnfollowedFiles.Write(harness.FileSystem, root.Path, plain, stream => stream.Write("plainer\n"u8));

        Assert.Equal(("#!/bin/sh\necho\n", true), (File.ReadAllText(script), harness.FilePermissions.IsExecutable(script)));
        Assert.Equal(("plainer\n", false), (File.ReadAllText(plain), harness.FilePermissions.IsExecutable(plain)));
    }

    /// <summary>A named pipe put where a file is read is refused before a byte is read, never waited on for a writer that never comes.</summary>
    [Fact]
    public async Task APipeWhereAFileIsRead_IsRefused_NeverWaitedOn()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows makes no named pipe in a directory.");

        using var root = new TempDirectory();
        var pipe = root.Combine("pipe");
        var fileSystem = new HarnessFactory().FileSystem;
        MakeFifo(pipe);

        var read = Task.Run(() => Assert.ThrowsAny<IOException>(() => UnfollowedFiles.OpenRead(fileSystem, root.Path, pipe)), TestContext.Current.CancellationToken);
        var opened = Task.Run(() => Assert.ThrowsAny<IOException>(() => fileSystem.OpenRead(pipe)), TestContext.Current.CancellationToken);

        // A read that waits for a writer would hold the test until it is stopped: none comes.
        await Task.WhenAll(read, opened).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    /// <summary>Swaps <paramref name="x"/> between the directory and the link, back and forth, until stopped.</summary>
    internal static void Swap(string x, string real, string link, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            Quietly(() => Directory.Move(real, x));
            Quietly(() => Directory.Move(x, real));
            Quietly(() => Directory.Move(link, x));
            Quietly(() => Directory.Move(x, link));

            // A directory the tool made at x while nothing stood there goes, so the swap can go on.
            Quietly(() =>
            {
                if (Directory.Exists(real) && Directory.Exists(link) && new DirectoryInfo(x) is { Exists: true, LinkTarget: null })
                {
                    Directory.Delete(x, recursive: true);
                }
            });
        }
    }

    /// <summary>Does what <paramref name="act"/> does; a refusal or a file held at that instant passes, as the swap makes them.</summary>
    private static void Quietly(Action act)
    {
        try
        {
            act();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Makes a named pipe at <paramref name="path"/>.</summary>
    private static void MakeFifo(string path)
    {
        using var mkfifo = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("mkfifo") { ArgumentList = { path }, UseShellExecute = false })!;
        mkfifo.WaitForExit();
        Assert.Equal(0, mkfifo.ExitCode);
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
                TestLinks.DirectoryLink(path, target);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
