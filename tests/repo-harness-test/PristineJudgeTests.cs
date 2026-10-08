using RepoHarness.Core.Execution;
using RepoHarness.Core.Mutations;

namespace RepoHarness.Tests;

/// <summary>
/// The judge of a test binary's unmutated control: a control that does not pass decides the leg's own verdict and stops
/// every arm of the binary, saying why; one that passes bounds every mutated run of the binary by how long it took.
/// </summary>
public sealed class PristineJudgeTests
{
    private static readonly ArmBuild Built = new(ReachedVerdict.Of(LegVerdict.Passed, "built"), [], []);

    /// <summary>
    /// The unmutated build not passing is the leg's verdict - failed, stopped, or a guard's - and no run of the binary is
    /// looked at.
    /// </summary>
    [Theory]
    [InlineData(LegVerdict.Failed)]
    [InlineData(LegVerdict.Stopped)]
    [InlineData(LegVerdict.InputsMoved)]
    public void AnUnmutatedBuildThatDidNotPass_IsTheLegsVerdict(LegVerdict verdict)
    {
        var outcome = PristineJudge.Judge("fixture_tests", new ArmBuild(ReachedVerdict.Of(verdict, "exited 1"), [], []), null, TimeSpan.Zero, 10);

        Assert.Equal(ReachedVerdict.Of(verdict, "the unmutated build of fixture_tests: exited 1"), outcome.Leg);
        Assert.Equal("the unmutated build of fixture_tests did not pass", outcome.Stops);
    }

    /// <summary>
    /// An unmutated run that wrote no report, or one that is no report, or failed with no failing case, is
    /// unattributed; one with a red case is failed, naming each: no mutation of a binary that is red unmutated proves
    /// anything. One whose report was written and could not be read from its file is unmeasured, saying why: that is
    /// no finding about the binary.
    /// </summary>
    [Fact]
    public void AnUnmutatedRunThatDidNotPass_IsTheLegsVerdict_AndStopsTheBinarysArms()
    {
        var nothing = PristineJudge.Judge("fixture_tests", Built, ArmRun.Exited(3, RunReport.None), TimeSpan.FromSeconds(4), 10);
        var unreadable = PristineJudge.Judge(
            "fixture_tests",
            Built,
            ArmRun.Exited(0, RunReport.NoReport("its root is 'html', and a JUnit report's is 'testsuites' or 'testsuite'")),
            TimeSpan.FromSeconds(4),
            10);
        var unsaid = PristineJudge.Judge("fixture_tests", Built, ArmRun.Exited(0, RunReport.NoReport(null)), TimeSpan.FromSeconds(4), 10);
        var unread = PristineJudge.Judge(
            "fixture_tests",
            Built,
            ArmRun.Exited(0, RunReport.NotRead("'report.xml' is held by another process")),
            TimeSpan.FromSeconds(4),
            10);
        var red = PristineJudge.Judge("fixture_tests", Built, Ran(1, "<failure/>", "<error/>"), TimeSpan.FromSeconds(4), 10);
        var oneRed = PristineJudge.Judge("fixture_tests", Built, Ran(0, string.Empty, "<failure/>"), TimeSpan.FromSeconds(4), 10);
        var crashed = PristineJudge.Judge("fixture_tests", Built, Ran(1, string.Empty, string.Empty), TimeSpan.FromSeconds(4), 10);

        Assert.Equal(
            (ReachedVerdict.Of(LegVerdict.Unattributed, "the unmutated fixture_tests exited 3 and wrote no report"), "the unmutated fixture_tests left no report to judge its arms' runs against"),
            (nothing.Leg, nothing.Stops));
        Assert.Equal(
            (ReachedVerdict.Of(
                LegVerdict.Unattributed,
                "the report of the unmutated fixture_tests is no JUnit report this reads, after it exited 0: its root is 'html', and a JUnit report's is 'testsuites' or 'testsuite'"),
                "the unmutated fixture_tests left no report to judge its arms' runs against"),
            (unreadable.Leg, unreadable.Stops));
        Assert.Equal(ReachedVerdict.Of(LegVerdict.Unattributed, "the report of the unmutated fixture_tests is no JUnit report this reads, after it exited 0"), unsaid.Leg);
        Assert.Equal(
            (ReachedVerdict.Of(
                LegVerdict.Unmeasured,
                "the report of the unmutated fixture_tests was written and could not be read, after it exited 0: 'report.xml' is held by another process"),
                "the report of the unmutated fixture_tests could not be read, so nothing says its cases pass unmutated"),
            (unread.Leg, unread.Stops));
        Assert.Equal(
            (ReachedVerdict.Of(LegVerdict.Failed, "the unmutated fixture_tests has 2 red: Fixture.A, Fixture.B"), "the unmutated fixture_tests has 2 red, so no mutation of it proves anything"),
            (red.Leg, red.Stops));
        Assert.Equal(ReachedVerdict.Of(LegVerdict.Failed, "the unmutated fixture_tests has 1 red: Fixture.B"), oneRed.Leg);
        Assert.Equal(
            (ReachedVerdict.Of(LegVerdict.Unattributed, "the unmutated fixture_tests exited 1, and its report names no failing case"), "the unmutated fixture_tests fails with no case to show for it"),
            (crashed.Leg, crashed.Stops));
    }

    /// <summary>
    /// An unmutated run stopped as hung - silent for <c>defaults.stallSeconds</c> - is unattributed, and stops the
    /// binary's arms: no hang of a mutated run could be told from the binary's own.
    /// </summary>
    [Fact]
    public void AnUnmutatedRunStoppedAsHung_IsUnattributed_AndStopsTheBinarysArms()
    {
        var hung = PristineJudge.Judge("fixture_tests", Built, ArmRun.Hung(120), TimeSpan.FromMinutes(2), 10);

        Assert.Equal(
            (ReachedVerdict.Of(LegVerdict.Unattributed, "the unmutated fixture_tests printed nothing for 120s, and was stopped as hung"),
                "the unmutated fixture_tests hangs, so no run of a mutation of it can be told from a hang the mutation caused"),
            (hung.Leg, hung.Stops));
    }

    /// <summary>An unmutated run that passed decides nothing, stops nothing, and bounds the binary's mutated runs.</summary>
    [Fact]
    public void AnUnmutatedRunThatPassed_BoundsTheMutatedRuns()
    {
        var outcome = PristineJudge.Judge("fixture_tests", Built, Ran(0, string.Empty, string.Empty), TimeSpan.FromSeconds(30), 10);

        Assert.Equal((null, null, TimeSpan.FromSeconds(300)), (outcome.Leg, outcome.Stops, outcome.Bound?.Limit));
        Assert.Equal(PristineOutcome.Passed(PristineJudge.Bound(TimeSpan.FromSeconds(30), 10)), outcome);
    }

    /// <summary>
    /// A control's outcome is one of three, and built no other way: it passed, with a bound that is some time; it did
    /// not, with the leg's verdict and why its arms are stopped; or there was nothing to control, which says nothing.
    /// </summary>
    [Fact]
    public void AControlsOutcome_IsOneOfThree()
    {
        var bound = new RunBound(TimeSpan.FromSeconds(61), "10x the unmutated run");
        var stopped = PristineOutcome.Stopped(ReachedVerdict.Of(LegVerdict.Failed, "the unmutated fixture_tests has 1 red: Fixture.A"), "it has 1 red");

        Assert.Equal((null, null, bound), (PristineOutcome.Passed(bound).Leg, PristineOutcome.Passed(bound).Stops, PristineOutcome.Passed(bound).Bound));
        Assert.Equal((LegVerdict.Failed, "it has 1 red", null), (stopped.Leg?.Verdict, stopped.Stops, stopped.Bound));
        Assert.Equal((null, null, null), (PristineOutcome.NothingToControl.Leg, PristineOutcome.NothingToControl.Stops, PristineOutcome.NothingToControl.Bound));
        Assert.Throws<ArgumentOutOfRangeException>(() => PristineOutcome.Passed(new RunBound(TimeSpan.Zero, "10x the unmutated run")));
        Assert.Throws<ArgumentNullException>(() => PristineOutcome.Passed(null!));
        Assert.Throws<ArgumentException>(() => PristineOutcome.Stopped(ReachedVerdict.Of(LegVerdict.Failed, "red"), " "));
        Assert.Throws<ArgumentNullException>(() => PristineOutcome.Stopped(null!, "it has 1 red"));
    }

    /// <summary>
    /// A run is one of three, and built no other way: it exited, leaving what it left as its report; it ran past its
    /// bound; or it hung - and a run that was stopped has no exit code and no report anybody reads.
    /// </summary>
    [Fact]
    public void ARun_IsOneOfThree()
    {
        var bound = new RunBound(TimeSpan.FromSeconds(61), "10x the unmutated run");
        var exited = ArmRun.Exited(3, RunReport.NotRead("held"), diagnosticSaid: true);
        var past = ArmRun.StoppedAt(bound);
        var hung = ArmRun.Hung(45);

        Assert.Equal((3, null, null, "held", true), (exited.ExitCode, exited.PastBound, exited.StalledAfterSeconds, exited.Report.Unread, exited.DiagnosticSaid));
        Assert.Equal((null, bound, null, RunReport.None, false), (past.ExitCode, past.PastBound, past.StalledAfterSeconds, past.Report, past.DiagnosticSaid));
        Assert.Equal((null, null, 45, RunReport.None, false), (hung.ExitCode, hung.PastBound, hung.StalledAfterSeconds, hung.Report, hung.DiagnosticSaid));
        Assert.Throws<ArgumentOutOfRangeException>(() => ArmRun.Hung(0));
        Assert.Throws<ArgumentNullException>(() => ArmRun.Exited(0, null!));
        Assert.Throws<ArgumentNullException>(() => ArmRun.StoppedAt(null!));

        // And what it left as its report, one of four.
        var read = JUnitReport.Read("<testsuites><testsuite><testcase classname=\"Fixture\" name=\"A\"/></testsuite></testsuites>")!;

        Assert.Equal((false, null, null, null), (RunReport.None.Written, RunReport.None.Read, RunReport.None.Problem, RunReport.None.Unread));
        Assert.Equal((true, read, null, null), (RunReport.Of(read).Written, RunReport.Of(read).Read, RunReport.Of(read).Problem, RunReport.Of(read).Unread));
        Assert.Equal((true, null, "no XML", null), (RunReport.NoReport("no XML").Written, RunReport.NoReport("no XML").Read, RunReport.NoReport("no XML").Problem, RunReport.NoReport("no XML").Unread));
        Assert.Equal((true, null, null, "held"), (RunReport.NotRead("held").Written, RunReport.NotRead("held").Read, RunReport.NotRead("held").Problem, RunReport.NotRead("held").Unread));
        Assert.Throws<ArgumentException>(() => RunReport.NotRead(" "));
        Assert.Throws<ArgumentNullException>(() => RunReport.Of(null!));
    }

    /// <summary>
    /// The bound is the factor times the unmutated run, and never less than the unmutated run and a minute - a quick
    /// binary is never stopped for a pause a busy machine makes - nor so large it cannot be waited for.
    /// </summary>
    [Theory]
    [InlineData(30, 10, 300)]
    [InlineData(2, 10, 62)]
    [InlineData(0, 10, 60)]
    [InlineData(100, 1.5, 160)]
    [InlineData(1000, 1.5, 1500)]
    public void TheBound_IsTheFactorTimesTheRun_AndNeverLessThanTheRunAndAMinute(double seconds, double factor, double bound)
        => Assert.Equal(TimeSpan.FromSeconds(bound), PristineJudge.Bound(TimeSpan.FromSeconds(seconds), factor).Limit);

    /// <summary>
    /// A bound says what set it, as the verdict of a run stopped at it then says: the factor times the unmutated run
    /// where that is what it is; the unmutated run and a minute more where the factor would give less - every binary
    /// whose unmutated run is under a minute over the factor less one, 6.7 seconds at a factor of 10; and the longest a
    /// run is waited for where either is past it. Never the factor where the factor is not what stopped the run.
    /// </summary>
    [Theory]
    [InlineData(30, 10, "10x the unmutated run")]
    [InlineData(1, 10, "the unmutated run and 1m00s more")]
    [InlineData(6, 10, "the unmutated run and 1m00s more")]
    [InlineData(7, 10, "10x the unmutated run")]
    [InlineData(100, 1.5, "the unmutated run and 1m00s more")]
    [InlineData(120, 1.5, "1.5x the unmutated run")]
    [InlineData(1000, 1.5, "1.5x the unmutated run")]
    [InlineData(40 * 86400, 10, "the longest a run is waited for")]
    [InlineData(60 * 86400, 1.5, "the longest a run is waited for")]
    public void ABound_SaysWhatSetIt(double seconds, double factor, string setBy)
        => Assert.Equal(setBy, PristineJudge.Bound(TimeSpan.FromSeconds(seconds), factor).SetBy);

    /// <summary>
    /// A bound past the longest a timer can wait - a factor so large it would overflow, or a run so long its bound would
    /// pass it - is the longest a bound can be, some 49 days, which a timer still waits for; and so is one a factor that
    /// is no number would give, which is no bound a run could be held to.
    /// </summary>
    [Fact]
    public void ABoundPastTheLongestATimerWaits_IsTheLongestABoundCanBe()
    {
        using var timer = new CancellationTokenSource();

        Assert.Equal(PristineJudge.LongestBound, PristineJudge.Bound(TimeSpan.FromDays(1), 1e300).Limit);
        Assert.Equal(PristineJudge.LongestBound, PristineJudge.Bound(TimeSpan.FromDays(40), 10).Limit);
        Assert.Equal(PristineJudge.LongestBound, PristineJudge.Bound(TimeSpan.FromDays(60), 1.5).Limit);
        Assert.Equal(new RunBound(PristineJudge.LongestBound, "the longest a run is waited for"), PristineJudge.Bound(TimeSpan.FromSeconds(30), double.NaN));
        Assert.InRange(PristineJudge.LongestBound, TimeSpan.FromDays(49), TimeSpan.FromDays(50));
        timer.CancelAfter(PristineJudge.LongestBound);
        Assert.Throws<ArgumentOutOfRangeException>(() => timer.CancelAfter(PristineJudge.LongestBound + TimeSpan.FromMilliseconds(2)));
    }

    /// <summary>A run that exited <paramref name="exitCode"/>, its two cases holding <paramref name="first"/> and <paramref name="second"/>.</summary>
    private static ArmRun Ran(int exitCode, string first, string second)
        => ArmRun.Exited(
            exitCode,
            RunReport.Of(JUnitReport.Read(
                $"<testsuites><testsuite><testcase classname=\"Fixture\" name=\"A\">{first}</testcase><testcase classname=\"Fixture\" name=\"B\">{second}</testcase></testsuite></testsuites>")!));
}
