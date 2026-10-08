using RepoHarness.Core.Mutations;

namespace RepoHarness.Tests;

/// <summary>
/// A test binary's JUnit report, read one way for the four runners that write one: each case named as an arm declares
/// it, whether it ran - a skipped case did, a disabled one did not - and whether it failed, by a failure or an error.
/// </summary>
public sealed class JUnitReportTests
{
    /// <summary>As GoogleTest writes it with --gtest_output=xml: a parameterized suite, a skipped case and a disabled one.</summary>
    private const string GoogleTest = """
        <?xml version="1.0" encoding="UTF-8"?>
        <testsuites tests="5" failures="1" disabled="1" errors="0" time="0.003" timestamp="2026-10-07T12:00:00.000" name="AllTests">
          <testsuite name="Fixture" tests="4" failures="1" disabled="1" skipped="1" errors="0" time="0.002" timestamp="2026-10-07T12:00:00.000">
            <testcase name="TheChargeMatchesTheStoredCount" file="fixture.cpp" line="10" status="run" result="completed" time="0" timestamp="2026-10-07T12:00:00.000" classname="Fixture">
              <failure message="fixture.cpp:12&#x0A;Expected equality of these values:&#x0A;  charge(2)&#x0A;    Which is: 4&#x0A;  3&#x0A;" type=""><![CDATA[fixture.cpp:12
        Expected equality of these values:
          charge(2)
            Which is: 4
          3
        ]]></failure>
            </testcase>
            <testcase name="TheDepthBoundIsPinned" file="fixture.cpp" line="20" status="run" result="completed" time="0" timestamp="2026-10-07T12:00:00.000" classname="Fixture" />
            <testcase name="SkippedOnThisMachine" file="fixture.cpp" line="30" status="run" result="skipped" time="0" timestamp="2026-10-07T12:00:00.000" classname="Fixture">
              <skipped message="fixture.cpp:31&#x0A;"><![CDATA[fixture.cpp:31
        ]]></skipped>
            </testcase>
            <testcase name="DISABLED_NotYet" file="fixture.cpp" line="40" status="notrun" result="suppressed" time="0" timestamp="2026-10-07T12:00:00.000" classname="Fixture" />
          </testsuite>
          <testsuite name="Range/Param" tests="1" failures="0" disabled="0" skipped="0" errors="0" time="0" timestamp="2026-10-07T12:00:00.000">
            <testcase name="Holds/0" value_param="3" file="fixture.cpp" line="50" status="run" result="completed" time="0" timestamp="2026-10-07T12:00:00.000" classname="Range/Param" />
          </testsuite>
        </testsuites>
        """;

    /// <summary>As Catch2 3 writes it with --reporter JUnit: a failed assertion, an exception, and a section.</summary>
    private const string Catch2 = """
        <?xml version="1.0" encoding="UTF-8"?>
        <testsuites>
          <testsuite name="fixture" errors="1" failures="1" skipped="0" tests="3" hostname="tbd" time="0.001" timestamp="2026-10-07T12:00:00Z">
            <properties>
              <property name="random-seed" value="1"/>
            </properties>
            <testcase classname="fixture.global" name="charge matches the count" time="0.000" status="run">
              <failure message="charge(2) == 3" type="REQUIRE">
        FAILED:
          REQUIRE( charge(2) == 3 )
        with expansion:
          4 == 3
        at fixture.cpp:12
              </failure>
            </testcase>
            <testcase classname="fixture.global" name="depth bound is pinned" time="0.000" status="run"/>
            <testcase classname="fixture.global" name="throws/the section" time="0.000" status="run">
              <error message="TEST_CASE( throws )" type="TEST_CASE">
        FAILED:
        due to unexpected exception with message:
          boom
        at fixture.cpp:20
              </error>
            </testcase>
            <system-out/>
            <system-err/>
          </testsuite>
        </testsuites>
        """;

    /// <summary>As doctest writes it with --reporters=junit.</summary>
    private const string Doctest = """
        <?xml version="1.0" encoding="UTF-8"?>
        <testsuites>
          <testsuite name="fixture" errors="0" failures="1" tests="2">
            <testcase classname="fixture.cpp" name="charge matches the count" status="run" time="0.000">
              <failure message="4 == 3" type="CHECK">
        fixture.cpp(12):
        CHECK( charge(2) == 3 ) is NOT correct!
          values: CHECK( 4 == 3 )

              </failure>
            </testcase>
            <testcase classname="fixture.cpp" name="depth bound is pinned" status="run" time="0.000"/>
          </testsuite>
        </testsuites>
        """;

    /// <summary>As Boost.Test writes it with --logger=JUNIT: one suite at the root, a nested one, and a disabled case.</summary>
    private const string BoostTest = """
        <?xml version="1.0" encoding="UTF-8"?>
        <testsuite tests="4" skipped="1" errors="0" failures="1" id="0" name="Master_Test_Suite" time="0.001">
        <testcase assertions="1" classname="Master_Test_Suite" name="charge_matches_the_count" time="0">
        <failure message="failure" type="assertion error"><![CDATA[
        ASSERTION FAILURE:
        - file   : fixture.cpp
        - line   : 12
        - message: check charge(2) == 3 has failed [4 != 3]
        ]]></failure>
        <system-out><![CDATA[INFO:
        - file   : boost.test framework
        - line   : 0
        - message: Test case Master_Test_Suite/charge_matches_the_count did not run
        ]]></system-out>
        </testcase>
        <testcase assertions="1" classname="Master_Test_Suite" name="depth_bound_is_pinned" time="0">
        </testcase>
        <testcase assertions="0" classname="Master_Test_Suite" name="disabled_case" time="0">
        <skipped/>
        </testcase>
        <testsuite tests="1" skipped="0" errors="0" failures="0" id="1" name="Inner" time="0">
        <testcase assertions="1" classname="Master_Test_Suite/Inner" name="nested_case" time="0"/>
        </testsuite>
        </testsuite>
        """;

    /// <summary>
    /// GoogleTest's report: <c>Suite.Case</c>, and <c>Prefix/Suite.Case/0</c> for a parameterized case; a skipped case ran,
    /// a disabled one did not; the failure is the one red.
    /// </summary>
    [Fact]
    public void GoogleTestsReport_NamesSuiteDotCase_AndADisabledCaseDidNotRun()
    {
        var report = JUnitReport.Read(GoogleTest);

        Assert.NotNull(report);
        Assert.Equal(
            [
                new JUnitCase("Fixture.TheChargeMatchesTheStoredCount", true, true),
                new JUnitCase("Fixture.TheDepthBoundIsPinned", true, false),
                new JUnitCase("Fixture.SkippedOnThisMachine", true, false),
                new JUnitCase("Fixture.DISABLED_NotYet", false, false),
                new JUnitCase("Range/Param.Holds/0", true, false),
            ],
            report.Cases);
        Assert.Equal(4, report.Ran);
        Assert.Equal(["Fixture.TheChargeMatchesTheStoredCount"], report.Reds);
        Assert.DoesNotContain("Fixture.DISABLED_NotYet", report.RanCases);
        Assert.Contains("Range/Param.Holds/0", report.RanCases);
    }

    /// <summary>Catch2's report: a case's class and its name, a section's path in the name, and an exception's error as red as a failure.</summary>
    [Fact]
    public void Catch2sReport_CountsAnErrorAsRed()
    {
        var report = JUnitReport.Read(Catch2);

        Assert.NotNull(report);
        Assert.Equal(3, report.Ran);
        Assert.Equal(["fixture.global.charge matches the count", "fixture.global.throws/the section"], report.Reds);
        Assert.Contains("fixture.global.depth bound is pinned", report.RanCases);
    }

    /// <summary>doctest's report, its class the file the case is in.</summary>
    [Fact]
    public void DoctestsReport_IsReadAlike()
    {
        var report = JUnitReport.Read(Doctest);

        Assert.NotNull(report);
        Assert.Equal(2, report.Ran);
        Assert.Equal(["fixture.cpp.charge matches the count"], report.Reds);
    }

    /// <summary>
    /// Boost.Test's report, a suite at its root and suites nested in it: every case read at whatever depth, and a case
    /// skipped - which it writes with no status - counted as run, as a skipped case is.
    /// </summary>
    [Fact]
    public void BoostTestsReport_IsReadAtEveryDepth()
    {
        var report = JUnitReport.Read(BoostTest);

        Assert.NotNull(report);
        Assert.Equal(
            ["Master_Test_Suite.charge_matches_the_count", "Master_Test_Suite.depth_bound_is_pinned", "Master_Test_Suite.disabled_case", "Master_Test_Suite/Inner.nested_case"],
            report.Cases.Select(@case => @case.Id));
        Assert.Equal(4, report.Ran);
        Assert.Equal(["Master_Test_Suite.charge_matches_the_count"], report.Reds);
    }

    /// <summary>A case with no class is named by its name alone.</summary>
    [Fact]
    public void ACaseWithNoClass_IsNamedByItsName()
        => Assert.Equal(
            ["lonely", "Suite.Case"],
            JUnitReport.Read("""<testsuite><testcase name="lonely"/><testcase classname="Suite" name="Case"/></testsuite>""")!.Cases.Select(@case => @case.Id));

    /// <summary>
    /// What is no report is not read as one, and why is said: nothing, a report a crash cut short and text that is no
    /// XML, each with what the reader made of it; XML that is no JUnit report, naming the root it has; and one declaring
    /// a document type - whose entities could reach for other files, or expand without end - even where the type
    /// declares nothing at all, wherever in its prolog it stands. A document type named only inside a comment declares
    /// none.
    /// </summary>
    [Theory]
    [InlineData("", "it is no XML: ")]
    [InlineData("<testsuites><testsuite name=\"Fixture\"><testcase name=\"A\" classname=\"Fixture\">", "it is no XML: ")]
    [InlineData("[==========] 5 tests from 1 test suite ran.", "it is no XML: ")]
    [InlineData("<testsuites><!-- <!DOCTYPE testsuites> --><testsuite>", "it is no XML: ")]
    [InlineData("<html><body>not a report</body></html>", "its root is 'html', and a JUnit report's is 'testsuites' or 'testsuite'")]
    [InlineData(
        "<?xml version=\"1.0\"?><!DOCTYPE testsuites [<!ENTITY secret SYSTEM \"file:///etc/passwd\">]><testsuites><testsuite><testcase name=\"&secret;\"/></testsuite></testsuites>",
        "it declares a document type, which no report needs and this never reads")]
    [InlineData(
        "<?xml version=\"1.0\"?><!DOCTYPE testsuites [<!ENTITY a \"aaaa\"><!ENTITY b \"&a;&a;&a;&a;\">]><testsuites><testsuite><testcase name=\"&b;\"/></testsuite></testsuites>",
        "it declares a document type, which no report needs and this never reads")]
    [InlineData(
        "<?xml version=\"1.0\"?><!DOCTYPE testsuites><testsuites><testsuite><testcase name=\"A\"/></testsuite></testsuites>",
        "it declares a document type, which no report needs and this never reads")]
    [InlineData(
        "  <?xml-stylesheet href=\"a.xsl\"?>\n<!-- written by a runner -->\n<!DOCTYPE testsuites><testsuites/>",
        "it declares a document type, which no report needs and this never reads")]
    public void WhatIsNoReport_IsNotReadAsOne_AndWhyIsSaid(string text, string why)
    {
        Assert.Null(JUnitReport.Read(text, out var problem));
        Assert.StartsWith(why, problem, StringComparison.Ordinal);
        Assert.True(problem!.Length > "it is no XML: ".Length, problem);
        Assert.Null(JUnitReport.Read(text));
    }

    /// <summary>
    /// What is no XML is said with what the reader made of it - which element a crash left open, and where the text
    /// stops - since that is what tells a report cut short from a runner writing another format.
    /// </summary>
    [Fact]
    public void WhatIsNoXml_SaysWhatTheReaderMadeOfIt()
    {
        const string CutShort = "<testsuites><testsuite name=\"Fixture\"><testcase name=\"A\" classname=\"Fixture\">";

        var made = Assert.ThrowsAny<System.Xml.XmlException>(() => System.Xml.Linq.XDocument.Parse(CutShort));

        Assert.Null(JUnitReport.Read(CutShort, out var problem));
        Assert.Equal($"it is no XML: {made.Message}", problem);
        Assert.Contains("testcase", problem, StringComparison.Ordinal);
    }

    /// <summary>A report that is read has nothing said against it.</summary>
    [Fact]
    public void AReportThatIsRead_HasNoProblem()
    {
        Assert.NotNull(JUnitReport.Read(GoogleTest, out var problem));
        Assert.Null(problem);
    }
}
