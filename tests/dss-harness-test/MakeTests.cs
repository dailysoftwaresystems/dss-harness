using DssHarness.Core.Build;

namespace DssHarness.Tests;

/// <summary>What make is to a build CMake generates for it, read as make says it.</summary>
public sealed class MakeTests
{
    /// <summary>
    /// The targets a build's lines say failed of its own are each step of a target's own makefile that failed, and each
    /// failure of CMake's driving makefiles no earlier, deeper failure of the same target explains - each once, in the order
    /// said - read along each chain of makes, never over the whole build. Read as GNU make 4 says them, the makefile and
    /// line before the target, from mingw32-make 4.4 under -k as it was measured, and as GNU make 3.81 - macOS's own -
    /// names the target alone; a failure make was told to ignore is none. Read by the structure make writes in every
    /// language and whatever signal ended a step - German's Fehler, a Killed compiler, a segmentation fault - so no unit is
    /// lost for the words after it. A target whose make named no failing step - make found no rule for a header, and
    /// stopped - is kept by its driving makefile's failure, whether another target's step failed before it or after; and
    /// a build nested in a step, an external project's under $(MAKE), is that step's failure, its own deeper failures -
    /// relative to its own directory, named as this build's are - left out.
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
    [InlineData(
        "make[2]: *** [CMakeFiles/app.dir/build.make:76: CMakeFiles/app.dir/a.c.o] Fehler 1\n"
        + "make[1]: *** [CMakeFiles/Makefile2:83: CMakeFiles/app.dir/all] Fehler 2\n"
        + "make: *** [Makefile:91: all] Fehler 2\n",
        new[] { "CMakeFiles/app.dir/a.c.o" })]
    [InlineData(
        "make[2]: *** [CMakeFiles/app.dir/build.make:76: CMakeFiles/app.dir/a.cpp.o] Killed\n"
        + "make[2]: *** [CMakeFiles/app.dir/build.make:90: CMakeFiles/app.dir/b.cpp.o] Segmentation fault (core dumped)\n"
        + "make[1]: *** [CMakeFiles/Makefile2:83: CMakeFiles/app.dir/all] Error 2\n",
        new[] { "CMakeFiles/app.dir/a.cpp.o", "CMakeFiles/app.dir/b.cpp.o" })]
    [InlineData(
        "make[2]: *** No rule to make target 'missing.h', needed by 'CMakeFiles/A.dir/a.c.o'.  Stop.\n"
        + "make[1]: *** [CMakeFiles/Makefile2:83: CMakeFiles/A.dir/all] Error 2\n"
        + "make[2]: *** [CMakeFiles/B.dir/build.make:76: CMakeFiles/B.dir/b.c.o] Error 1\n"
        + "make[1]: *** [CMakeFiles/Makefile2:110: CMakeFiles/B.dir/all] Error 2\n"
        + "make: *** [Makefile:91: all] Error 2\n",
        new[] { "CMakeFiles/A.dir/all", "CMakeFiles/B.dir/b.c.o" })]
    [InlineData(
        "make[2]: *** No rule to make target 'missing.h', needed by 'CMakeFiles/A.dir/a.c.o'.  Stop.\n"
        + "make[2]: *** [CMakeFiles/B.dir/build.make:76: CMakeFiles/B.dir/b.c.o] Error 1\n"
        + "make[1]: *** [CMakeFiles/Makefile2:83: CMakeFiles/A.dir/all] Error 2\n"
        + "make[1]: *** [CMakeFiles/Makefile2:110: CMakeFiles/B.dir/all] Error 2\n",
        new[] { "CMakeFiles/B.dir/b.c.o", "CMakeFiles/A.dir/all" })]
    [InlineData(
        "make[2]: *** [CMakeFiles/A.dir/build.make:76: CMakeFiles/A.dir/a.c.o] Error 1\n"
        + "make[2]: *** [CMakeFiles/B.dir/build.make:76: CMakeFiles/B.dir/b.c.o] Error 1\n"
        + "make[1]: *** [CMakeFiles/Makefile2:83: CMakeFiles/A.dir/all] Error 2\n"
        + "make[1]: *** [CMakeFiles/Makefile2:110: CMakeFiles/B.dir/all] Error 2\n"
        + "make: *** [Makefile:91: all] Error 2\n",
        new[] { "CMakeFiles/A.dir/a.c.o", "CMakeFiles/B.dir/b.c.o" })]
    [InlineData(
        "make[2]: *** [CMakeFiles/probe.dir/build.make:76: CMakeFiles/probe.dir/b.c.o] Error 1\n"
        + "make[5]: *** [CMakeFiles/probe.dir/build.make:76: CMakeFiles/probe.dir/a.c.o] Error 1\n"
        + "make[4]: *** [CMakeFiles/Makefile2:83: CMakeFiles/probe.dir/all] Error 2\n"
        + "make[3]: *** [Makefile:91: all] Error 2\n"
        + "make[2]: *** [CMakeFiles/ext.dir/build.make:120: ext-prefix/src/ext-stamp/ext-build] Error 2\n"
        + "make[1]: *** [CMakeFiles/Makefile2:110: CMakeFiles/ext.dir/all] Error 2\n"
        + "make[1]: *** [CMakeFiles/Makefile2:83: CMakeFiles/probe.dir/all] Error 2\n"
        + "make: *** [Makefile:91: all] Error 2\n",
        new[] { "CMakeFiles/probe.dir/b.c.o", "ext-prefix/src/ext-stamp/ext-build" })]
    public void FailedTargets_AreEachChainsOwnFailures(string output, string[] expected)
        => Assert.Equal(expected, Make.FailedTargets(output.Split('\n')));

    /// <summary>
    /// The makefile and line before the target are read as each of GNU make's catalogs parts them from it: French writes a
    /// space before the colon, Simplified Chinese a full-width colon - so a translated build's driving makefiles are its
    /// drivers, and its failing object its unit, never left out as a nested build's.
    /// </summary>
    [Theory]
    [InlineData(
        "make[2]: *** [CMakeFiles/app.dir/build.make:76 : CMakeFiles/app.dir/a.c.o] Erreur 1\n"
        + "make[1]: *** [CMakeFiles/Makefile2:83 : CMakeFiles/app.dir/all] Erreur 2\n"
        + "make: *** [Makefile:91 : all] Erreur 2\n")]
    [InlineData(
        "make[2]: *** [CMakeFiles/app.dir/build.make:76：CMakeFiles/app.dir/a.c.o] 错误 1\n"
        + "make[1]: *** [CMakeFiles/Makefile2:83：CMakeFiles/app.dir/all] 错误 2\n"
        + "make: *** [Makefile:91：all] 错误 2\n")]
    public void FailedTargets_AreReadAsEachCatalogPartsTheMakefileFromTheTarget(string output)
        => Assert.Equal(["CMakeFiles/app.dir/a.c.o"], Make.FailedTargets(output.Split('\n')));

    /// <summary>
    /// A shallower failure proves a deeper one a nested build's only where it is known to be a step of this build's own:
    /// its makefile read and none of CMake's driving ones, or - where no failure names a makefile, as GNU make before 4
    /// writes them - its target no driving makefile's goal. A makefile named in a form not read, or no makefile among
    /// failures that name theirs, proves nothing: each is kept as make said it, and no unit that failed is left out.
    /// </summary>
    [Theory]
    [InlineData(
        "make[2]: *** [CMakeFiles/app.dir/build.make:76 - CMakeFiles/app.dir/a.c.o] Error 1\n"
        + "make[1]: *** [CMakeFiles/Makefile2:83 - CMakeFiles/app.dir/all] Error 2\n"
        + "make: *** [Makefile:91 - all] Error 2\n",
        new[] { "CMakeFiles/app.dir/build.make:76 - CMakeFiles/app.dir/a.c.o", "CMakeFiles/Makefile2:83 - CMakeFiles/app.dir/all", "Makefile:91 - all" })]
    [InlineData(
        "make[3]: *** [CMakeFiles/app.dir/build.make:76: CMakeFiles/app.dir/a.c.o] Error 1\nmake[2]: *** [CMakeFiles/app.dir/b.c.o] Error 1\n",
        new[] { "CMakeFiles/app.dir/a.c.o", "CMakeFiles/app.dir/b.c.o" })]
    [InlineData(
        "make[5]: *** [CMakeFiles/probe.dir/a.c.o] Error 1\nmake[4]: *** [CMakeFiles/probe.dir/all] Error 2\nmake[3]: *** [all] Error 2\n"
        + "make[2]: *** [ext-prefix/src/ext-stamp/ext-build] Error 2\nmake[1]: *** [CMakeFiles/ext.dir/all] Error 2\nmake: *** [all] Error 2\n",
        new[] { "ext-prefix/src/ext-stamp/ext-build" })]
    public void ANestedBuildsFailure_IsProvedOnlyByAStepKnownToBeThisBuildsOwn(string output, string[] expected)
        => Assert.Equal(expected, Make.FailedTargets(output.Split('\n')));

    /// <summary>
    /// GNU make before 4 names no makefile, so a failure of CMake's driving makefiles is known by its target - the whole
    /// build's, a target's own goal, or one the build was asked for, whose rule the top makefiles name - and a step tied to
    /// a target by the directory its target lies under: a failing object is its target's unit, and so is a link named as
    /// the target it builds.
    /// </summary>
    [Theory]
    [InlineData(
        "make[3]: *** [CMakeFiles/app.dir/a.c.o] Error 1\nmake[2]: *** [CMakeFiles/app.dir/all] Error 2\nmake[1]: *** [CMakeFiles/app.dir/rule] Error 2\nmake: *** [app] Error 2\n",
        new[] { "CMakeFiles/app.dir/a.c.o" })]
    [InlineData(
        "make[3]: *** [app] Error 1\nmake[2]: *** [CMakeFiles/app.dir/all] Error 2\nmake[1]: *** [CMakeFiles/app.dir/rule] Error 2\nmake: *** [app] Error 2\n",
        new[] { "app" })]
    public void BeforeMake4_DrivingFailuresAreKnownByTheirTargets(string output, string[] expected)
        => Assert.Equal(expected, Make.FailedTargets(output.Split('\n'), ["app"]));

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
