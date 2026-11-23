using DssHarness.Core.Platform;
using DssHarness.Core.Processes;

namespace DssHarness.Tests;

/// <summary>
/// How a path reads on each platform, from its text alone: one reading, which the validator and the
/// survey ask and the machine that starts the program shares, so none of them can come to disagree
/// about a path.
/// </summary>
public sealed class PlatformPathsTests
{
    [Theory]
    [InlineData("/opt/homebrew/bin/cmake", "macos", true)]
    [InlineData("/usr/bin/gcc-13", "linux", true)]
    [InlineData(@"C:\tools\cmake.exe", "windows", true)]
    [InlineData("C:/tools/cmake.exe", "windows", true)]
    [InlineData(@"\\server\share\cmake.exe", "windows", true)]
    [InlineData("//server/share/cmake.exe", "windows", true)]
    [InlineData(@"C:\tools\cmake.exe", "linux", false)]
    [InlineData("/opt/tools/cmake", "windows", false)]
    [InlineData("C:tools", "windows", false)]
    [InlineData("tools/cmake", "linux", false)]
    [InlineData("./cmake", "macos", false)]
    public void APath_IsAbsoluteOnlyWhereItsPlatformReadsItSo(string path, string platform, bool absolute)
        => Assert.Equal(absolute, PlatformPaths.IsAbsoluteOn(path, platform));

    /// <summary>
    /// Absolute somewhere is absolute on one platform or the other: what a setting read on any
    /// machine, for a machine it cannot tell, may be. '~/' is the home of whoever reads it, and only
    /// with its slash.
    /// </summary>
    [Theory]
    [InlineData("/opt/tools", true, false)]
    [InlineData(@"C:\tools", true, false)]
    [InlineData(@"\\server\share", true, false)]
    [InlineData("C:tools", false, false)]
    [InlineData("tools", false, false)]
    [InlineData("~/bin", false, true)]
    [InlineData("~bin", false, false)]
    [InlineData("~", false, false)]
    public void APath_IsAbsoluteSomewhere_OrFromTheHome_ByItsTextAlone(string path, bool absolute, bool home)
    {
        Assert.Equal(absolute, PlatformPaths.IsAbsoluteOnAnyPlatform(path));
        Assert.Equal(home, PlatformPaths.IsHomeRelative(path));
    }

    /// <summary>
    /// A program carrying a separator or a drive is a path, started where it points and looked for
    /// nowhere else; anything else is a name the PATH is asked about.
    /// </summary>
    [Theory]
    [InlineData("cmake", false)]
    [InlineData("cmake.exe", false)]
    [InlineData("./cmake", true)]
    [InlineData("tools/cmake", true)]
    [InlineData(@"tools\cmake", true)]
    [InlineData("C:cmake", true)]
    [InlineData(@"C:\tools\cmake.exe", true)]
    public void AProgramIsAPath_WhenItCarriesASeparatorOrADrive(string program, bool path)
        => Assert.Equal(path, ProcessRunner.IsPath(program));

    /// <summary>
    /// A path a tool wrote is placed in a tree by its own text, read in the form it is written in, and the same answer
    /// comes on every machine: both forms of path against both forms of root, on whichever system the suite runs - a
    /// Windows path is never read as relative where a POSIX machine reads it, and never joined to that machine's own
    /// directory. Windows' form ignores case and takes either slash; the POSIX form compares exactly and parts at '/'
    /// alone; '..' resolves by the text, a relative path inside unless it climbs out; a file URI is the path it names, and
    /// a URI of another scheme - a runtime's own module - is in no tree; a place the text cannot tell is in none.
    /// </summary>
    [Theory]
    [InlineData(@"C:\work\tree", @"C:\work\tree\src\a.cs", true)]
    [InlineData(@"C:\work\tree", "c:/WORK/Tree/src/a.cs", true)]
    [InlineData(@"C:\work\tree", @"C:\work\tree", false)]
    [InlineData(@"C:\work\tree", @"C:\work\treehouse\a.cs", false)]
    [InlineData(@"C:\work\tree", @"C:\work\tree\..\other\a.cs", false)]
    [InlineData(@"C:\work\tree", @"C:\work\other\..\tree\a.cs", true)]
    [InlineData(@"C:\work\tree", @"D:\work\tree\a.cs", false)]
    [InlineData(@"C:\work\tree", "/work/tree/a.cs", false)]
    [InlineData(@"C:\work\tree", "C:a.cs", false)]
    [InlineData(@"C:\work\tree", @"\work\tree\a.cs", false)]
    [InlineData(@"C:\work\tree", "file:///C:/work/tree/src/a%20b.cs", true)]
    [InlineData(@"\\server\share\tree", "//server/share/tree/a.cs", true)]
    [InlineData(@"\\server\share\tree", @"\\server\share\..\tree\a.cs", true)]
    [InlineData("/home/u/tree", "/home/u/tree/src/a.c", true)]
    [InlineData("/home/u/tree", "/home/u/Tree/src/a.c", false)]
    [InlineData("/home/u/tree", "/home/u/tree/../other/a.c", false)]
    [InlineData("/home/u/tree", "/../home/u/tree/a.c", true)]
    [InlineData("/home/u/tree", "C:/home/u/tree/a.c", false)]
    [InlineData("/home/u/tree", @"C:\src\a.c", false)]
    [InlineData("/home/u/tree", "d:/src/test/charge_test.dart", false)]
    [InlineData("/home/u/tree", "file:///home/u/tree/test/a.dart", true)]
    [InlineData("/home/u/tree", "file:///d:/src/test/a.dart", false)]
    [InlineData("/home/u/tree", "dart:isolate-patch/isolate_patch.dart", false)]
    [InlineData("/home/u/tree", "node:internal/process/task_queues", false)]
    [InlineData("/home/u/tree", "src/a.c", true)]
    [InlineData(@"C:\work\tree", @"src\a.cs", true)]
    [InlineData("/home/u/tree", "../a.c", false)]
    [InlineData(@"C:\work\tree", @"src\..\..\a.cs", false)]
    [InlineData("/home/u/tree", "./", false)]
    public void APathAToolWrote_IsInsideATree_ByItsOwnForm_OnEveryMachine(string root, string path, bool inside)
        => Assert.Equal(inside, PlatformPaths.IsInsideAsWritten(root, path));

    /// <summary>
    /// A path inside a tree is named below its root by '/' on every machine, whichever form and separators it was written
    /// in; a relative path is read from the directory the tool that wrote it ran in - a compiler in the build directory
    /// writing <c>../../src/a.c</c> names the tree's <c>src/a.c</c> - and from the root where none is given; a path not
    /// strictly inside is named nothing.
    /// </summary>
    [Theory]
    [InlineData(@"C:\work\tree", @"C:\work\tree\src\a.cs", null, "src/a.cs")]
    [InlineData(@"C:\work\tree", "c:/WORK/Tree/src/a.cs", null, "src/a.cs")]
    [InlineData(@"C:\work\tree", @"..\..\src\a.cs", @"C:\work\tree\build\debug", "src/a.cs")]
    [InlineData(@"C:\work\tree", @"..\..\..\a.cs", @"C:\work\tree\build\debug", null)]
    [InlineData(@"C:\work\tree", @"src\a.cs", null, "src/a.cs")]
    [InlineData("/home/u/tree", "../../src/a.c", "/home/u/tree/build/debug", "src/a.c")]
    [InlineData("/home/u/tree", "a.c", "/home/u/elsewhere", null)]
    [InlineData("/home/u/tree", "/home/u/tree/src/./a.c", "/home/u/tree/build", "src/a.c")]
    [InlineData("/home/u/tree", "file:///home/u/tree/test/a%20b.dart", null, "test/a b.dart")]
    [InlineData("/home/u/tree", "/home/u/tree", null, null)]
    public void APathInsideATree_IsNamedBelowItsRootBySlashes_ARelativeOneFromWhereItsToolRan(string root, string path, string? from, string? named)
        => Assert.Equal(named, PlatformPaths.RelativeAsWritten(root, path, from));
}
