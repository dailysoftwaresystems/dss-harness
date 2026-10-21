using System.Security.AccessControl;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Platform;
using DssHarness.Core.Secrets;

namespace DssHarness.Tests;

/// <summary>
/// The one rule for a directory of dotenv files, read as one set of names with any others read beside it: every file
/// merged, a name defined twice refused, a line that defines nothing reported with its line - and who may read a file
/// left to the reader that knows what its names hold. Measured on real files.
/// </summary>
public sealed class DotEnvDirectoriesTests
{
    private const string Twice = "nothing can say which value a run used.";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// Every file of a directory is read and merged into one set of names, each with the file it was defined in, and
    /// a comment or a blank line defines nothing.
    /// </summary>
    [Fact]
    public void EveryFileOfADirectory_IsMerged_EachNameWithTheFileItCameFrom()
    {
        using var temp = new TempDirectory();
        var first = temp.WriteFile(Path.Combine("env", "a.env"), "CORPUS_ROOT=corpus\n# a comment\n\nJOBS=8\n");
        var second = temp.WriteFile(Path.Combine("env", "b.env"), "TARGET=all\n");
        var directories = Directories();

        var values = directories.Read(temp.Combine("env"), Token);

        Assert.Equal(
            [("CORPUS_ROOT", "corpus"), ("JOBS", "8"), ("TARGET", "all")],
            values.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => (pair.Key, pair.Value)));
        Assert.Equal(
            [("CORPUS_ROOT", first), ("JOBS", first), ("TARGET", second)],
            directories.Origins.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => (pair.Key, pair.Value)));
        Assert.Empty(directories.Problems);
    }

    /// <summary>
    /// A name defined twice is refused, whichever comes later never winning: in two files, in one file twice, and in two
    /// directories read as one set - its case ignored, since a reader handing names to an environment on Windows would
    /// hold one where the files spell two. Each refusal names both files, in the order of their names, and the name
    /// keeps its first value.
    /// </summary>
    [Fact]
    public void ANameDefinedTwice_IsRefused_InTwoFiles_InOneFile_AndInTwoDirectoriesReadAsOneSet_ItsCaseIgnored()
    {
        using var temp = new TempDirectory();
        var first = temp.WriteFile(Path.Combine("env", "a.env"), "JOBS=8\n");
        var second = temp.WriteFile(Path.Combine("env", "b.env"), "jobs=4\nTOKEN=x\nTOKEN=y\n");
        var third = temp.WriteFile(Path.Combine("secrets", "c.env"), "Token=z\n");
        var directories = Directories();

        var values = directories.Read(temp.Combine("env"), Token);
        var secrets = directories.Read(temp.Combine("secrets"), Token);

        Assert.Equal(
            [
                $"'jobs' is defined in '{first}' and again in '{second}'; {Twice}",
                $"'TOKEN' is defined in '{second}' and again in '{second}'; {Twice}",
                $"'Token' is defined in '{second}' and again in '{third}'; {Twice}",
            ],
            directories.Problems);
        Assert.Equal(
            [("JOBS", "8"), ("TOKEN", "x")],
            values.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => (pair.Key, pair.Value)));
        Assert.Empty(secrets);
    }

    /// <summary>
    /// A file other users can read is read as any other, and nothing is said of it: which files must be private turns on
    /// what their names hold - the runner's plain values are no secret - so the reader asks it of the file each name came
    /// from. Refused here, a runner's values directory every account may read would stop every run that reads it.
    /// </summary>
    [Fact]
    public void AFileOtherUsersCanRead_IsReadAsAnyOther_ItsPrivacyLeftToTheReader()
    {
        using var temp = new TempDirectory();
        var file = temp.WriteFile(Path.Combine("env", "a.env"), "TOKEN=s3cret\n");

        if (OperatingSystem.IsWindows())
        {
            FilePermissionsTests.Grant(file, FileSystemRights.ReadData);
        }
        else
        {
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }

        // The file is one the system's own check calls readable by others, so the reading below is of such a file.
        Assert.False(FilePermissionsFactory.Create().IsPrivate(file), "The file was still private.");

        var directories = Directories();
        var values = directories.Read(temp.Combine("env"), Token);

        Assert.Equal([("TOKEN", "s3cret")], values.Select(pair => (pair.Key, pair.Value)));
        Assert.Equal([("TOKEN", file)], directories.Origins.Select(pair => (pair.Key, pair.Value)));
        Assert.Empty(directories.Problems);
    }

    /// <summary>
    /// A line that defines nothing - no '=', an empty name, a name holding a space - is a problem named by its file and
    /// line, and the lines around it are read.
    /// </summary>
    [Fact]
    public void ALineThatDefinesNothing_IsAProblemNamedByItsLine_AndTheRestIsRead()
    {
        using var temp = new TempDirectory();
        var file = temp.WriteFile(Path.Combine("env", "a.env"), "JOBS 8\n=nothing\nexport TOKEN=x\nTARGET=all\n");
        var directories = Directories();

        var values = directories.Read(temp.Combine("env"), Token);

        Assert.Equal(
            [
                $"'{file}' line 1: no '=', so nothing is defined here.",
                $"'{file}' line 2: the name before '=' is empty.",
                $"'{file}' line 3: 'export TOKEN' is not a name; an environment variable's name holds no spaces.",
            ],
            directories.Problems);
        Assert.Equal([("TARGET", "all")], values.Select(pair => (pair.Key, pair.Value)));
    }

    /// <summary>A directory that is not there defines nothing and is no problem: each such directory is gitignored, so a fresh clone has none.</summary>
    [Fact]
    public void ADirectoryThatIsNotThere_DefinesNothing()
    {
        using var temp = new TempDirectory();
        var directories = Directories();

        Assert.Empty(directories.Read(temp.Combine("env"), Token));
        Assert.Empty(directories.Origins);
        Assert.Empty(directories.Problems);
    }

    /// <summary>
    /// A file where the directory should be is a problem said by a sentence, and nothing of it is read: taken for a
    /// directory that is not there, its values would be lost without a word, and the reader would run on none.
    /// </summary>
    [Fact]
    public void AFileWhereTheDirectoryShouldBe_IsAProblem_AndNothingOfItIsRead()
    {
        using var temp = new TempDirectory();
        var file = temp.WriteFile("env", "JOBS=8\n");
        var directories = Directories();

        Assert.Empty(directories.Read(file, Token));
        Assert.Empty(directories.Origins);
        Assert.Empty(directories.Files);
        Assert.Equal(
            [$"'{file}' is a file, where a directory of dotenv files is read: nothing of it was read. Make it a directory and move the file into it, where every file is read and merged."],
            directories.Problems);
    }

    /// <summary>
    /// The files read are said in the order they were read - by name within a directory, and the directories in the
    /// order they were read - the file a reader skips by name not among them: the one list of a directory's files, so no
    /// reader counts them again with its own skip and order.
    /// </summary>
    [Fact]
    public void TheFilesRead_AreSaidInTheOrderTheyWereRead_TheSkippedFileNotAmongThem()
    {
        using var temp = new TempDirectory();
        var second = temp.WriteFile(Path.Combine("env", "b.env"), "TARGET=all\n");
        var first = temp.WriteFile(Path.Combine("env", "a.env"), "JOBS=8\n");
        temp.WriteFile(Path.Combine("env", ".gitkeep"), string.Empty);
        var third = temp.WriteFile(Path.Combine("secrets", "c.env"), "TOKEN=x\n");
        var directories = new DotEnvDirectories(new PhysicalFileSystem(FilePermissionsFactory.Create()), skipFile: ".gitkeep");

        directories.Read(temp.Combine("env"), Token);
        directories.Read(temp.Combine("secrets"), Token);

        Assert.Equal([first, second, third], directories.Files);
        Assert.Empty(directories.Problems);
    }

    private static DotEnvDirectories Directories() => new(new PhysicalFileSystem(FilePermissionsFactory.Create()));
}
