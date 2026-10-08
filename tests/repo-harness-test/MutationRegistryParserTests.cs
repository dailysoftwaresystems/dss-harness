using RepoHarness.Core.Mutations;

namespace RepoHarness.Tests;

/// <summary>
/// The arms registry's grammar, golden: each shape it reads, read whole, and each it refuses, refused with its line and
/// a sentence saying why - every problem at once, since a registry is fixed by reading what is wrong with it.
/// </summary>
public sealed class MutationRegistryParserTests
{
    private const string TestRedArm = "A | charge | src/charge.cpp | texts/charge.before | texts/charge.after | TEST-RED | fixture | fixture | 5 | texts/charge.diag | the charge is what the count says";

    private const string BuildRedArm = "A | private-ctor | src/cost.hpp | texts/ctor.before | texts/ctor.after | BUILD-RED | fixture | - | 0 | PAIRED-CONTROL | only the mint builds one";

    /// <summary>
    /// A registry's text is its lines however each ends - a line feed, a carriage return and a line feed, or a carriage
    /// return alone - so a line number names the line an editor shows; the empty one after a last line ending is a
    /// blank line, read as nothing.
    /// </summary>
    [Fact]
    public void ARegistrysText_IsItsLines_HoweverEachEnds()
    {
        Assert.Equal(["# arms", TestRedArm, "C | charge | Fixture.Charge | reddens", ""], MutationRegistryParser.Lines($"# arms\r\n{TestRedArm}\rC | charge | Fixture.Charge | reddens\n"));

        var reading = MutationRegistryParser.Parse(MutationRegistryParser.Lines($"# arms\r{TestRedArm}\r\nC | charge | Fixture.Charge | reddens\n"), null);

        Assert.True(reading.Valid, string.Join("; ", reading.Problems));
        Assert.Equal(2, Assert.Single(reading.Registry.Arms).Line);
    }

    /// <summary>
    /// Every row it reads, in one registry: comments and blank lines passed over, each field trimmed, a why holding a
    /// '|' or a '#' kept whole, since the last field takes the rest of the line, and each arm's rows gathered under it,
    /// in the order they are declared.
    /// </summary>
    [Fact]
    public void EveryRowItReads_IsReadWhole_UnderItsArm()
    {
        var reading = Parse(
            "# the arms of a small library",
            "",
            "   " + TestRedArm + "   ",
            "C | charge | Fixture.TheChargeMatchesTheCount | the charge reddens | and says so",
            "C|charge|Fixture.TheChargeIsReadTwice|the second reader # not a comment",
            "G | charge | Neighbour.StaysGreen | untouched",
            "M | charge | src/charge.hpp | texts/charge-header.before | texts/charge-header.after | the declaration moves with it",
            "S | charge | linux-gcc, win-msvc gate | where the charge is built",
            BuildRedArm,
            "B | private-ctor | texts/ctor-control.before | texts/ctor-control.after | the public spelling builds");

        Assert.Empty(reading.Problems);
        Assert.True(reading.Valid);

        var charge = Assert.Single(reading.Registry.Arms, arm => arm.Id == "charge");

        Assert.Equal(3, charge.Line);
        Assert.Equal(new MutationSite("src/charge.cpp", "texts/charge.before", "texts/charge.after", 3), charge.Own);
        Assert.Equal(RedKind.TestRed, charge.Kind);
        Assert.Equal(("fixture", "fixture", 5, "texts/charge.diag"), (charge.Target, charge.Runner, charge.Cases, charge.Diagnostic));
        Assert.Equal("the charge is what the count says", charge.Why);
        Assert.Equal(["Fixture.TheChargeMatchesTheCount", "Fixture.TheChargeIsReadTwice"], charge.Reds);
        Assert.Equal(["Neighbour.StaysGreen"], charge.Greens);
        Assert.Equal([new MutationSite("src/charge.hpp", "texts/charge-header.before", "texts/charge-header.after", 7)], charge.Coupled);
        Assert.Equal(["src/charge.cpp", "src/charge.hpp"], charge.Sites.Select(site => site.Site));
        Assert.Equal(["linux-gcc", "win-msvc", "gate"], charge.Scope?.Legs);
        Assert.Equal(8, charge.Scope?.Line);
        Assert.Null(charge.Control);

        var ctor = Assert.Single(reading.Registry.Arms, arm => arm.Id == "private-ctor");

        Assert.Equal(RedKind.BuildRed, ctor.Kind);
        Assert.Equal((MutationRegistryParser.NoRunner, 0, MutationRegistryParser.PairedControlToken), (ctor.Runner, ctor.Cases, ctor.Diagnostic));
        Assert.Equal(new PairedControl("texts/ctor-control.before", "texts/ctor-control.after", 10), ctor.Control);
        Assert.Empty(ctor.Reds);
        Assert.Null(ctor.Scope);
        Assert.Equal(["charge", "private-ctor"], reading.Registry.Arms.Select(arm => arm.Id));
    }

    /// <summary>A row's last field takes the rest of its line, '|' and all.</summary>
    [Fact]
    public void TheLastField_TakesTheRestOfTheLine()
    {
        var reading = Parse(TestRedArm + " | and more | still the why", "C | charge | Fixture.A | why");

        Assert.Empty(reading.Problems);
        Assert.Equal("the charge is what the count says | and more | still the why", reading.Registry.Arms[0].Why);
    }

    /// <summary>
    /// Each way one row is wrong, refused with its line and why. The rows a mutation harness of a repository's own once
    /// needed and this derives are each refused naming what took their place.
    /// </summary>
    [Theory]
    [InlineData("Z | charge | x", "line 3: 'Z' is no row this registry reads, which are A, C, G, B, M and S")]
    [InlineData("R | . | . | arms | Release | texts | why", "line 3: an R row is not read: each leg is swept in copies of its own tree")]
    [InlineData("X | .git | why", "line 3: an X row is not read: a worker copy carries what sync carries")]
    [InlineData("I | CMAKE_BUILD_TYPE | build | why", "line 3: an I row is not read: a worker configures as its leg's variant does")]
    [InlineData("F | googletest | build/_deps | why", "line 3: an F row is not read: the dependency sources the leg's own build fetched are read from its CMake cache")]
    [InlineData("T | charge | charge.cpp | why", "line 3: a T row is not read: what must rebuild is read from ninja's records")]
    [InlineData("C | charge | Fixture.A", "line 3: a C row has 3 field(s), expected 4: C | arm | case | why")]
    [InlineData("C | charge |  | why", "line 3: a C row's case is empty")]
    [InlineData("C | nobody | Fixture.A | why", "line 3: a C row names arm 'nobody', which no A row above it declares")]
    [InlineData("C | charge | Fixture.Case\u0007 | why", "line 3: case 'Fixture.Case\u0007' holds a control character")]
    [InlineData("C | charge | Fixture.TheChargeMatchesTheCount | again", "line 3: arm 'charge' declares case 'Fixture.TheChargeMatchesTheCount' red twice")]
    [InlineData("G | charge | Fixture.TheChargeMatchesTheCount | why", "line 3: arm 'charge' declares case 'Fixture.TheChargeMatchesTheCount' both red and green")]
    [InlineData("B | charge | texts/b.before | texts/b.after | why", "line 3: arm 'charge' is TEST-RED and carries a B row: a paired positive control belongs to a BUILD-RED arm")]
    [InlineData("M | charge | src/charge.cpp | texts/m.before | texts/m.after | why", "line 3: arm 'charge' already mutates 'src/charge.cpp'")]
    [InlineData("M | charge | /etc/passwd | texts/m.before | texts/m.after | why", "line 3: site '/etc/passwd' is absolute")]
    [InlineData("S | charge | , , | why", "line 3: the S row of arm 'charge' names no leg or leg set")]
    public void ARowThatCannotBeRead_IsRefusedWithItsLine(string row, string expected)
    {
        var reading = Parse(TestRedArm, "C | charge | Fixture.TheChargeMatchesTheCount | why", row);

        Assert.False(reading.Valid);
        Assert.Contains(reading.Problems, problem => problem.StartsWith(expected, StringComparison.Ordinal));
    }

    /// <summary>Each way an A row is wrong, refused with its line and why.</summary>
    [Theory]
    [InlineData("A | x | src/a.cpp | t/a.b | t/a.a | TEST-RED | fx | fx | 1 | t/a.d", "line 1: an A row has 10 field(s), expected 11")]
    [InlineData("A | x | src/a.cpp | t/a.b | t/a.a | TEST-RED | fx | fx | 1 | t/a.d |  ", "line 1: an A row's why is empty")]
    [InlineData("A | x y | src/a.cpp | t/a.b | t/a.a | TEST-RED | fx | fx | 1 | t/a.d | why", "line 1: arm id 'x y' holds a character outside [A-Za-z0-9_-]")]
    [InlineData("A | x | src/a b.cpp | t/a.b | t/a.a | TEST-RED | fx | fx | 1 | t/a.d | why", "line 1: site 'src/a b.cpp' holds a character outside [A-Za-z0-9_./-]")]
    [InlineData("A | x | /src/a.cpp | t/a.b | t/a.a | TEST-RED | fx | fx | 1 | t/a.d | why", "line 1: site '/src/a.cpp' is absolute")]
    [InlineData("A | x | src/../a.cpp | t/a.b | t/a.a | TEST-RED | fx | fx | 1 | t/a.d | why", "line 1: site 'src/../a.cpp' climbs out with '..'")]
    [InlineData("A | x | src/ | t/a.b | t/a.a | TEST-RED | fx | fx | 1 | t/a.d | why", "line 1: site 'src/' ends in a slash")]
    [InlineData("A | x | src/./a.cpp | t/a.b | t/a.a | TEST-RED | fx | fx | 1 | t/a.d | why", "line 1: site 'src/./a.cpp' holds an empty or '.' segment")]
    [InlineData("A | x | src//a.cpp | t/a.b | t/a.a | TEST-RED | fx | fx | 1 | t/a.d | why", "line 1: site 'src//a.cpp' holds an empty or '.' segment")]
    [InlineData("A | x | src/a.cpp | ../a.b | t/a.a | TEST-RED | fx | fx | 1 | t/a.d | why", "line 1: before '../a.b' climbs out with '..'")]
    [InlineData("A | x | src/a.cpp | t/a.b | t/a.a | TEST-RED | fx | fx | 1 | t/a d | why", "line 1: diag 't/a d' holds a character")]
    [InlineData("A | x | src/a.cpp | t/a.b | t/a.a | test-red | fx | fx | 1 | t/a.d | why", "line 1: red kind 'test-red' is neither TEST-RED nor BUILD-RED")]
    [InlineData("A | x | src/a.cpp | t/a.b | t/a.a | TEST-RED | f x | fx | 1 | t/a.d | why", "line 1: target 'f x' holds a character a CMake target cannot")]
    [InlineData("A | x | src/a.cpp | t/a.b | t/a.a | TEST-RED | fx | f/x | 1 | t/a.d | why", "line 1: runner 'f/x' holds a character a CMake target cannot")]
    [InlineData("A | x | src/a.cpp | t/a.b | t/a.a | TEST-RED | fx | fx | -1 | t/a.d | why", "line 1: case count '-1' is not a whole number")]
    [InlineData("A | x | src/a.cpp | t/a.b | t/a.a | TEST-RED | fx | fx | 99999999999 | t/a.d | why", "line 1: case count '99999999999' is not a whole number")]
    [InlineData("A | x | src/a.cpp | t/a.b | t/a.a | TEST-RED | fx | fx | 1 | PAIRED-CONTROL | why", "line 1: arm 'x' is TEST-RED and its diag is PAIRED-CONTROL")]
    [InlineData("A | x | src/a.cpp | t/a.b | t/a.a | TEST-RED | fx | fx | 0 | t/a.d | why", "line 1: arm 'x' is TEST-RED and declares 0 cases")]
    [InlineData("A | x | src/a.cpp | t/a.b | t/a.a | TEST-RED | fx | - | 1 | t/a.d | why", "line 1: arm 'x' is TEST-RED and names no runner")]
    [InlineData("A | x | src/a.cpp | t/a.b | t/a.a | TEST-RED | fx | fx | 1 | t/a.d | why", "line 1: arm 'x' is TEST-RED and declares no C row")]
    [InlineData("A | x | src/a.cpp | t/a.b | t/a.a | BUILD-RED | fx | - | 0 | PAIRED-CONTROL | why", "line 1: arm 'x' is BUILD-RED and has no B row")]
    [InlineData("A | x | src/a.cpp | t/a.b | t/a.a | BUILD-RED | fx | - | 0 | t/a.d | why", "line 1: arm 'x' is BUILD-RED and its diag is not PAIRED-CONTROL")]
    [InlineData("A | x | src/a.cpp | t/a.b | t/a.a | BUILD-RED | fx | - | 3 | PAIRED-CONTROL | why", "line 1: arm 'x' is BUILD-RED and declares 3 case(s): nothing runs")]
    [InlineData("A | x | src/a.cpp | t/a.b | t/a.a | BUILD-RED | fx | fx | 0 | PAIRED-CONTROL | why", "line 1: arm 'x' is BUILD-RED and names runner 'fx': nothing runs, so its runner is '-'")]
    public void AnArmThatCannotBeRead_IsRefusedWithItsLine(string row, string expected)
    {
        var reading = Parse(row);

        Assert.False(reading.Valid);
        Assert.Contains(reading.Problems, problem => problem.StartsWith(expected, StringComparison.Ordinal));
    }

    /// <summary>What a BUILD-RED arm cannot carry - a C, G, M or second B row - is refused at the row, with its line.</summary>
    [Theory]
    [InlineData("C | private-ctor | Fixture.A | why", "line 3: arm 'private-ctor' is BUILD-RED and carries a C row: nothing runs, so no case can redden")]
    [InlineData("G | private-ctor | Fixture.A | why", "line 3: arm 'private-ctor' is BUILD-RED and carries a G row: nothing runs, so no case can stay green")]
    [InlineData("M | private-ctor | src/other.hpp | t/m.before | t/m.after | why", "line 3: arm 'private-ctor' is BUILD-RED and carries an M row")]
    [InlineData("B | private-ctor | t/c2.before | t/c2.after | why", "line 3: arm 'private-ctor' has a second B row, the first at line 2")]
    public void WhatABuildRedArmCannotCarry_IsRefusedAtTheRow(string row, string expected)
    {
        var reading = Parse(BuildRedArm, "B | private-ctor | t/c.before | t/c.after | why", row);

        Assert.Contains(reading.Problems, problem => problem.StartsWith(expected, StringComparison.Ordinal));
    }

    /// <summary>
    /// An id is unique ignoring case, since an arm's records are kept under it and two ids one file system reads as one
    /// would write each other's; a second S row, a second M row on one file, and a row above its arm's are refused too.
    /// </summary>
    [Fact]
    public void AnIdDeclaredTwice_IgnoringCase_AndRowsThatRepeatOrPrecede_AreRefused()
    {
        var reading = Parse(
            "C | charge | Fixture.Early | above its arm",
            TestRedArm,
            "C | charge | Fixture.A | why",
            "S | charge | linux-gcc | why",
            "S | charge | win-msvc | why",
            "M | charge | src/other.cpp | t/m.before | t/m.after | why",
            "M | charge | src/other.cpp | t/m2.before | t/m2.after | why",
            TestRedArm.Replace("A | charge", "A | CHARGE", StringComparison.Ordinal));

        Assert.Equal(
            [
                "line 1: a C row names arm 'charge', which no A row above it declares: a row for an arm nobody declared is refused, never passed over",
                "line 5: arm 'charge' has a second S row, the first at line 4: one row names every leg it runs on",
                "line 7: arm 'charge' already mutates 'src/other.cpp': two edits to one file would be taken and put back over each other, so a coupled site is another file",
                "line 8: arm 'CHARGE' is declared twice, first at line 2 as 'charge'",
            ],
            reading.Problems);
    }

    /// <summary>
    /// An M row's site is compared with the sites its arm already mutates as the tree's own file system compares names: one
    /// differing only in the case of its letters is the same file where that folds case - refused, naming the spelling the
    /// arm already mutates it under and its line - and another file where it does not.
    /// </summary>
    [Fact]
    public void AnMRowsSite_IsComparedAsTheTreesFileSystemComparesNames()
    {
        string[] rows =
        [
            TestRedArm,
            "C | charge | Fixture.A | why",
            "M | charge | SRC/Charge.cpp | t/m.before | t/m.after | why",
            "M | charge | src/other.cpp | t/m2.before | t/m2.after | why",
            "M | charge | src/Other.cpp | t/m3.before | t/m3.after | why",
        ];

        var folding = MutationRegistryParser.Parse(rows, texts: null, StringComparer.OrdinalIgnoreCase);
        var exact = MutationRegistryParser.Parse(rows, texts: null, StringComparer.Ordinal);

        Assert.Equal(
            [
                "line 3: arm 'charge' already mutates 'SRC/Charge.cpp', as 'src/charge.cpp' at line 1: two edits to one file would be taken and put back over each other, so a coupled site is another file",
                "line 5: arm 'charge' already mutates 'src/Other.cpp', as 'src/other.cpp' at line 4: two edits to one file would be taken and put back over each other, so a coupled site is another file",
            ],
            folding.Problems);
        Assert.Equal(["src/charge.cpp", "src/other.cpp"], folding.Registry.Arms[0].Sites.Select(site => site.Site));
        Assert.Empty(exact.Problems);
        Assert.Equal(["src/charge.cpp", "SRC/Charge.cpp", "src/other.cpp", "src/Other.cpp"], exact.Registry.Arms[0].Sites.Select(site => site.Site));
        Assert.Empty(MutationRegistryParser.Parse(rows, texts: null).Problems);
    }

    /// <summary>
    /// The rows of an arm whose A row was refused are passed over, not refused again for naming no arm: one mistake is
    /// one problem.
    /// </summary>
    [Fact]
    public void TheRowsOfARefusedArm_AreNotRefusedAgain()
    {
        var reading = Parse(
            "A | bad id | src/a.cpp | t/a.b | t/a.a | TEST-RED | fx | fx | 1 | t/a.d | why",
            "C | bad id | Fixture.A | why",
            "A | blank | src/a.cpp | t/a.b | t/a.a | TEST-RED | fx | fx | 1 | t/a.d | ",
            "C | blank | Fixture.A | why");

        Assert.Equal(
            ["line 1: arm id 'bad id' holds a character outside [A-Za-z0-9_-]", "line 3: an A row's why is empty"],
            reading.Problems);
    }

    /// <summary>A registry declaring no arm is refused: an empty one would report every arm behaving as declared while holding none.</summary>
    [Fact]
    public void ARegistryOfNoArm_IsRefused()
    {
        var reading = Parse("# nothing yet", "");

        Assert.Equal(
            ["the registry declares no A row: an empty one would report every arm behaving as declared while holding none"],
            reading.Problems);
    }

    /// <summary>
    /// Every file in the text directory must be cited by some row - before, after, a diag or a control's texts - and one
    /// that is not is named: a mutation text nobody drives. A text directory that is not there, or holds nothing, is
    /// refused too; with none configured, nothing is checked.
    /// </summary>
    [Fact]
    public void EveryFileInTheTextDirectory_MustBeCited()
    {
        string[] rows =
        [
            "A | charge | src/charge.cpp | texts/charge.before | texts/charge.after | TEST-RED | fixture | fixture | 5 | texts/charge.diag | why",
            "C | charge | Fixture.A | why",
            BuildRedArm,
            "B | private-ctor | texts/control.before | texts/control.after | why",
        ];

        var cited = MutationRegistryParser.Parse(
            rows,
            new TextDirectoryListing("texts", ["charge.before", "charge.after", "charge.diag", "ctor.before", "ctor.after", "control.before", "control.after"]));
        var uncited = MutationRegistryParser.Parse(rows, new TextDirectoryListing("texts", ["charge.before", "orphan.after", "Charge.diag"]));

        Assert.Empty(cited.Problems);
        Assert.Equal(
            [
                "'texts/Charge.diag' is in the text directory and no row cites it: a mutation text nobody drives",
                "'texts/orphan.after' is in the text directory and no row cites it: a mutation text nobody drives",
            ],
            uncited.Problems);
        Assert.Equal(
            ["the text directory 'texts' is not a directory, so whether every text in it is cited cannot be read"],
            MutationRegistryParser.Parse(rows, new TextDirectoryListing("texts", null)).Problems);
        Assert.Equal(["the text directory 'texts' holds no file"], MutationRegistryParser.Parse(rows, new TextDirectoryListing("texts", [])).Problems);
        Assert.Empty(MutationRegistryParser.Parse(rows, null).Problems);
    }

    /// <summary>Every problem is reported at once, in the order a reader meets them, each with its line.</summary>
    [Fact]
    public void EveryProblem_IsReportedAtOnce()
    {
        var reading = Parse(
            "A | x | /a.cpp | t/a.b | t/a.a | TEST-RED | fx | fx | many | t/a.d | why",
            "Q | x",
            "C | y | Fixture.A | why");

        Assert.Equal(
            [
                "line 1: site '/a.cpp' is absolute: a row names a path relative to the repository root",
                "line 1: case count 'many' is not a whole number",
                "line 2: 'Q' is no row this registry reads, which are A, C, G, B, M and S",
                "line 3: a C row names arm 'y', which no A row above it declares: a row for an arm nobody declared is refused, never passed over",
                "line 1: arm 'x' is TEST-RED and declares no C row: an arm names the cases its mutation must redden",
            ],
            reading.Problems);
    }

    /// <summary>A path a row names is a file relative to the repository root, spelled one way everywhere.</summary>
    [Theory]
    [InlineData("src/a.cpp", null)]
    [InlineData(".harness-config/runner/actions/m/texts/a.before", null)]
    [InlineData("a", null)]
    [InlineData("src\\a.cpp", "holds a character outside [A-Za-z0-9_./-]")]
    [InlineData("C:/a.cpp", "holds a character outside [A-Za-z0-9_./-]")]
    [InlineData("/a.cpp", "is absolute: a row names a path relative to the repository root")]
    [InlineData("src/", "ends in a slash: a row names a file, never a directory")]
    [InlineData("..", "climbs out with '..': a row never names anything outside the repository")]
    [InlineData("./a.cpp", "holds an empty or '.' segment: a row spells each file one way, as the text directory's listing does")]
    public void PathProblem_SaysWhatIsWrongWithAPath(string path, string? expected)
        => Assert.Equal(expected, MutationRegistryParser.PathProblem(path));

    private static MutationRegistryReading Parse(params string[] lines) => MutationRegistryParser.Parse(lines, texts: null);
}
