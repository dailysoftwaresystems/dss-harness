using RepoHarness.Core.Mutations;

namespace RepoHarness.Tests;

/// <summary>
/// ninja's own record of what it ran, read as ninja writes it, and compared before a build and after it: the witness
/// that a step was run, whatever the clock said and however ninja rewrote the file in between.
/// </summary>
public sealed class NinjaLogTests
{
    /// <summary>Lines ninja 1.12 wrote for one build, its output paths as it canonicalizes them.</summary>
    private static readonly string[] Built =
    [
        "# ninja log v6",
        "19\t108\t8130962805535916\tCMakeFiles/upstream.dir/src/support.cpp.obj\tfea968c7fef01e8a",
        "24\t137\t8130962805590141\tCMakeFiles/fixture.dir/src/fixture.cpp.obj\t22d687c2358e3fa2",
        "109\t243\t8130962806438304\tlibupstream.a\tfa4650919145905e",
        "243\t365\t8130962807754494\tbin/fixture.exe\tfcf4acbd3c550c30",
    ];

    /// <summary>Versions 5 to 7 are read, the last line for each output kept, whatever ends the lines; a line not five fields long is passed over.</summary>
    [Theory]
    [InlineData("# ninja log v5")]
    [InlineData("# ninja log v6\r")]
    [InlineData("# ninja log v7")]
    public void ALogOfAReadVersion_KeepsTheLastLineForEachOutput(string header)
    {
        var log = NinjaLog.Read(
        [
            header,
            "1\t2\t3\tCMakeFiles/a.dir/a.cpp.o\taa\r",
            "4\t5\t6\tCMakeFiles\\b.dir\\./b.cpp.o\tbb",
            "7\t8\t9\tCMakeFiles/a.dir/a.cpp.o\tcc",
            "10\t11\ttruncated by a killed build",
            "",
        ]);

        Assert.NotNull(log);
        Assert.Equal(int.Parse(header.TrimEnd('\r')[^1..], System.Globalization.CultureInfo.InvariantCulture), log.Version);
        Assert.Equal(["CMakeFiles/a.dir/a.cpp.o", "CMakeFiles/b.dir/b.cpp.o"], log.Entries.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(new NinjaLogEntry("CMakeFiles/a.dir/a.cpp.o", "7", "8", "9", "cc"), log.Entries["CMakeFiles/a.dir/a.cpp.o"]);
    }

    /// <summary>A log of another version, or with no header, or none at all, is not read rather than read wrong.</summary>
    [Theory]
    [InlineData("# ninja log v4")]
    [InlineData("# ninja log v8")]
    [InlineData("# ninja log vX")]
    [InlineData("1\t2\t3\ta.o\taa")]
    [InlineData("")]
    public void ALogOfAnotherVersion_IsNotRead(string first)
        => Assert.Null(NinjaLog.Read(first.Length == 0 ? [] : [first, "1\t2\t3\ta.o\taa"]));

    /// <summary>
    /// An output was rebuilt exactly when its last line differs after the build from before it, or is there only
    /// after: an output whose line is unchanged was not, and one asked about that ninja never ran is not either.
    /// </summary>
    [Fact]
    public void Rebuilt_IsALastLineThatChanged_OrAppeared()
    {
        var before = NinjaLog.Read(Built)!;
        var after = NinjaLog.Read(
        [
            .. Built,
            "4\t104\t8130962942572511\tCMakeFiles/upstream.dir/src/support.cpp.obj\tfea968c7fef01e8a",
            "11\t111\t8130962942655631\tCMakeFiles/new.dir/new.cpp.obj\t1234",
        ])!;

        Assert.Equal(
            ["CMakeFiles/upstream.dir/src/support.cpp.obj", "CMakeFiles/new.dir/new.cpp.obj"],
            NinjaLog.Rebuilt(
                before,
                after,
                ["CMakeFiles\\upstream.dir\\src\\support.cpp.obj", "CMakeFiles/fixture.dir/src/fixture.cpp.obj", "CMakeFiles/new.dir/new.cpp.obj", "never/built.o"]));
    }

    /// <summary>
    /// A log ninja rewrote between the two readings - keeping only each output's last line, in another order - says
    /// nothing was rebuilt that was not: rewriting keeps every last line as it was.
    /// </summary>
    [Fact]
    public void ALogRewrittenInBetween_SaysNothingWasRebuiltThatWasNot()
    {
        var before = NinjaLog.Read(
        [
            .. Built,
            "4\t104\t8130962942572511\tCMakeFiles/upstream.dir/src/support.cpp.obj\tfea968c7fef01e8a",
        ])!;
        var rewritten = NinjaLog.Read(
        [
            "# ninja log v6",
            "243\t365\t8130962807754494\tbin/fixture.exe\tfcf4acbd3c550c30",
            "4\t104\t8130962942572511\tCMakeFiles/upstream.dir/src/support.cpp.obj\tfea968c7fef01e8a",
            "24\t137\t8130962805590141\tCMakeFiles/fixture.dir/src/fixture.cpp.obj\t22d687c2358e3fa2",
            "109\t243\t8130962806438304\tlibupstream.a\tfa4650919145905e",
        ])!;

        Assert.Empty(NinjaLog.Rebuilt(before, rewritten, before.Entries.Keys));
    }

    /// <summary>
    /// A step run again whose line came out the same in every field but its time is rebuilt: the time ninja records for the
    /// output is the file's own, and a rewritten file has a new one, whatever the start and end of the build say.
    /// </summary>
    [Fact]
    public void AStepRunAgain_IsRebuilt_ThoughItStartedAndEndedAtTheSameMoments()
    {
        var before = NinjaLog.Read(["# ninja log v6", "4\t104\t100\ta.o\taa"])!;
        var after = NinjaLog.Read(["# ninja log v6", "4\t104\t100\ta.o\taa", "4\t104\t200\ta.o\taa"])!;

        Assert.Equal(["a.o"], NinjaLog.Rebuilt(before, after, ["a.o"]));
        Assert.Empty(NinjaLog.Rebuilt(before, before, ["a.o"]));
    }
}
