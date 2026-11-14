using DssHarness.Core.Build;

namespace DssHarness.Tests;

/// <summary>What make is to a build CMake generates for it, read as make says it.</summary>
public sealed class MakeTests
{
    /// <summary>
    /// The targets a build's lines say failed are those the deepest make said failed - the make that ran each target's own
    /// recipe - each once, in the order said: never the makes above it, which failed only because a make they started did.
    /// Read as GNU make 4 says them, the makefile and line before the target, from mingw32-make 4.4 under -k as it was
    /// measured, and as GNU make 3.81 - macOS's own - names the target alone; a failure make was told to ignore is none.
    /// </summary>
    [Theory]
    [InlineData(
        "[ 14%] Building C object CMakeFiles/first.dir/a.c.obj\n"
        + "a.c:1:26: error: 'undeclared_in_a' undeclared (first use in this function)\n"
        + "mingw32-make[2]: *** [CMakeFiles\\first.dir\\build.make:78: CMakeFiles/first.dir/a.c.obj] Error 1\n"
        + "[ 28%] Building C object CMakeFiles/first.dir/ok.c.obj\n"
        + "mingw32-make[2]: Target 'CMakeFiles/first.dir/build' not remade because of errors.\n"
        + "mingw32-make[1]: *** [CMakeFiles\\Makefile2:92: CMakeFiles/first.dir/all] Error 2\n"
        + "[ 42%] Building C object CMakeFiles/second.dir/b.c.obj\n"
        + "mingw32-make[2]: *** [CMakeFiles\\second.dir\\build.make:78: CMakeFiles/second.dir/b.c.obj] Error 1\n"
        + "mingw32-make[2]: Target 'CMakeFiles/second.dir/build' not remade because of errors.\n"
        + "mingw32-make[1]: *** [CMakeFiles\\Makefile2:124: CMakeFiles/second.dir/all] Error 2\n"
        + "mingw32-make[1]: Target 'all' not remade because of errors.\n"
        + "mingw32-make: *** [Makefile:90: all] Error 2\n"
        + "mingw32-make: Target 'default_target' not remade because of errors.\n",
        new[] { "CMakeFiles/first.dir/a.c.obj", "CMakeFiles/second.dir/b.c.obj" })]
    [InlineData(
        "make[2]: *** [CMakeFiles/app.dir/src/a.c.o] Error 1\r\nmake[1]: *** [CMakeFiles/app.dir/all] Error 2\r\nmake: *** [all] Error 2\r\n",
        new[] { "CMakeFiles/app.dir/src/a.c.o" })]
    [InlineData(
        "/usr/bin/make[2]: *** [/home/u/b/CMakeFiles/x.dir/build.make:76: x.o] Error 1\nmake[2]: *** [x.o] Error 1\n",
        new[] { "x.o" })]
    [InlineData("make: *** [all] Error 2\n", new[] { "all" })]
    [InlineData("make[2]: [CMakeFiles/app.dir/a.o] Error 1 (ignored)\nmake[1]: *** No rule to make target 'b.c'.  Stop.\n", new string[0])]
    [InlineData("[100%] Built target app\n", new string[0])]
    public void FailedTargets_AreTheDeepestMakesFailures(string output, string[] expected)
        => Assert.Equal(expected, Make.FailedTargets(output.Split('\n')));

    /// <summary>make's generators, as configuration or CMake's cache names them, in any case; no other - ninja's among them.</summary>
    [Theory]
    [InlineData("Unix Makefiles", true)]
    [InlineData("MinGW Makefiles", true)]
    [InlineData("MSYS Makefiles", true)]
    [InlineData("unix makefiles", true)]
    [InlineData("NMake Makefiles", false)]
    [InlineData("Watcom WMake", false)]
    [InlineData("Ninja", false)]
    [InlineData(null, false)]
    public void Generates_NamesMakesGeneratorsAlone(string? generator, bool make)
        => Assert.Equal(make, Make.Generates(generator));
}
