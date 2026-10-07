using RepoHarness.Core.Execution;
using RepoHarness.Core.Mutations;

namespace RepoHarness.Tests;

/// <summary>
/// The judge of one arm: each row of its table reaching its own verdict, in the order the rows are read, the first that
/// applies deciding - a finding before what follows from it, and nothing about the mutation read from a build or a run
/// that says nothing about it.
/// </summary>
public sealed class ArmJudgeTests
{
    private const string Object = "CMakeFiles/fixture.dir/src/fixture.cpp.o";
    private const string HeaderObject = "CMakeFiles/fixture.dir/src/budget.cpp.o";
    private const string Upstream = "CMakeFiles/upstream.dir/src/support.cpp.o";

    /// <summary>A TEST-RED arm: three cases, one of them red, one a neighbour that must stay green.</summary>
    private static readonly MutationArm TestRed = new()
    {
        Id = "charge-bound",
        Line = 3,
        Own = new MutationSite("src/fixture.cpp", "texts/charge.before", "texts/charge.after", 3),
        Kind = RedKind.TestRed,
        Target = "fixture",
        Runner = "fixture_tests",
        Cases = 3,
        Diagnostic = "texts/charge.diag",
        Why = "the charge is pinned",
        Reds = ["Fixture.Charge"],
        Greens = ["Fixture.Depth"],
    };

    /// <summary>A BUILD-RED arm, with its paired positive control.</summary>
    private static readonly MutationArm BuildRed = new()
    {
        Id = "depth-type",
        Line = 7,
        Own = new MutationSite("src/budget.hpp", "texts/depth.before", "texts/depth.after", 7),
        Kind = RedKind.BuildRed,
        Target = "fixture",
        Runner = MutationRegistryParser.NoRunner,
        Cases = 0,
        Diagnostic = MutationRegistryParser.PairedControlToken,
        Why = "a depth that is no integer does not compile",
        Control = new PairedControl("texts/depth.control-before", "texts/depth.control-after", 8),
    };

    /// <summary>A pre-flight that finds nothing wrong: the site there, its text once, two objects depending on it.</summary>
    private static readonly ArmPreflight Ready = new()
    {
        Counts = [new TextCount("texts/charge.before", "src/fixture.cpp", 1)],
        Dependents = [Object, HeaderObject],
    };

    /// <summary>Each refusal of the pre-flight, by its row and what it found.</summary>
    private static readonly Dictionary<string, (ArmPreflight Preflight, string Detail)> PreflightRefusals = new()
    {
        ["1 a missing site"] = (
            Ready with { MissingSites = ["src/fixture.cpp"] },
            "site 'src/fixture.cpp' is not a file in the worker's copy of the tree"),
        ["2 a text not there"] = (
            Ready with { Counts = [new TextCount("texts/charge.before", "src/fixture.cpp", 1), new TextCount("texts/m.before", "src/budget.hpp", 0)] },
            "the text in 'texts/m.before' occurs 0 time(s) in 'src/budget.hpp', where it must occur exactly once"),
        ["2 a text there twice"] = (
            Ready with { Counts = [new TextCount("texts/charge.before", "src/fixture.cpp", 2)] },
            "the text in 'texts/charge.before' occurs 2 time(s) in 'src/fixture.cpp', where it must occur exactly once"),
        ["3 a target not built"] = (
            Ready with { TargetBuilt = false },
            "target 'fixture' is built by no line of the leg's build"),
        ["3 a runner building no program"] = (
            Ready with { RunnerProblem = "runner 'fixture_tests' builds no program" },
            "runner 'fixture_tests' builds no program"),
        ["4 no object depending on the site"] = (
            Ready with { Dependents = [] },
            "no object target 'fixture' builds depends on 'src/fixture.cpp'"),
    };

    /// <summary>Each row of a TEST-RED arm's run, by its row: the run, and the verdict it reaches saying what it found.</summary>
    private static readonly Dictionary<string, (ArmRun Run, LegVerdict Verdict, string Detail)> RunRows = new()
    {
        ["11 past its bound"] = (
            new ArmRun { StoppedAtBound = true, Bound = TimeSpan.FromSeconds(100), Factor = 10, ReportWritten = true, Report = Report(["Fixture.Charge"], ["Fixture.Depth", "Fixture.Other"]) },
            LegVerdict.Unattributed,
            "ran past 10x the unmutated run, 1m40s, and was stopped"),
        ["12 no report"] = (
            new ArmRun { ExitCode = -1073741819, DiagnosticSaid = true },
            LegVerdict.Unattributed,
            "exited -1073741819 and wrote no report"),
        ["12 a report that cannot be read"] = (
            new ArmRun { ExitCode = 1, ReportWritten = true, DiagnosticSaid = true },
            LegVerdict.Unattributed,
            "its report could not be read, after it exited 1"),
        ["13 a failing exit and no failing case"] = (
            Ran(1, Report([], ["Fixture.Charge", "Fixture.Depth", "Fixture.Other"])),
            LegVerdict.Unattributed,
            "exited 1, and its report names no failing case"),
        ["14 another number of cases"] = (
            Ran(1, Report(["Fixture.Charge"], ["Fixture.Depth"], notRun: ["Fixture.Other"])),
            LegVerdict.Violated,
            "ran 2 case(s), and the arm declares 3"),
        ["15 no case red"] = (
            Ran(0, Report([], ["Fixture.Charge", "Fixture.Depth", "Fixture.Other"])),
            LegVerdict.Survived,
            "ran 3 case(s), and none failed"),
        ["16 a declared red that is not, and a red not declared"] = (
            Ran(1, Report(["Fixture.Other"], ["Fixture.Charge", "Fixture.Depth"])),
            LegVerdict.Violated,
            "the cases that failed are not those declared: declared red and not, Fixture.Charge; red and not declared, Fixture.Other"),
        ["16 a red not declared beside the declared one"] = (
            Ran(1, Report(["Fixture.Charge", "Fixture.Other"], ["Fixture.Depth"])),
            LegVerdict.Violated,
            "the cases that failed are not those declared: red and not declared, Fixture.Other"),
        ["17 a neighbour that did not run"] = (
            Ran(1, Report(["Fixture.Charge"], ["Fixture.Other", "Fixture.Third"], notRun: ["Fixture.Depth"])),
            LegVerdict.Violated,
            "neighbour 'Fixture.Depth' did not run, so it is no neighbour that stayed green"),
        ["18 the diagnostic not said"] = (
            Ran(1, Report(["Fixture.Charge"], ["Fixture.Depth", "Fixture.Other"]), said: false),
            LegVerdict.Violated,
            "the run did not say its diagnostic, the text in 'texts/charge.diag'"),
        ["19 as declared"] = (
            Ran(1, Report(["Fixture.Charge"], ["Fixture.Depth", "Fixture.Other"])),
            LegVerdict.Passed,
            "ran 3 case(s), 1 red as declared, and said its diagnostic"),
        ["19 as declared, the binary exiting 0 all the same"] = (
            Ran(0, Report(["Fixture.Charge"], ["Fixture.Depth", "Fixture.Other"])),
            LegVerdict.Passed,
            "ran 3 case(s), 1 red as declared, and said its diagnostic"),
    };

    /// <summary>Each row of the pre-flight's, by name.</summary>
    public static TheoryData<string> PreflightRows() => new(PreflightRefusals.Keys);

    /// <summary>Each row of the run's, by name.</summary>
    public static TheoryData<string> RunRowNames() => new(RunRows.Keys);

    /// <summary>Each refusal of the pre-flight is <c>violated</c>, saying what it found, whatever was built after it.</summary>
    [Theory]
    [MemberData(nameof(PreflightRows))]
    public void EachRefusalOfThePreflight_IsViolated(string row)
    {
        var (preflight, detail) = PreflightRefusals[row];

        var verdict = ArmJudge.Judge(TestRed, new ArmObservation(preflight) { Build = Built() });

        Assert.Equal((LegVerdict.Violated, detail), (verdict?.Verdict, verdict?.Detail));
    }

    /// <summary>The pre-flight's rows are read in order: a missing site before a miscount, before the target, before the dependents.</summary>
    [Fact]
    public void ThePreflightsRows_AreReadInOrder()
    {
        var everything = new ArmPreflight
        {
            MissingSites = ["src/gone.cpp"],
            Counts = [new TextCount("texts/charge.before", "src/fixture.cpp", 0)],
            TargetBuilt = false,
            RunnerProblem = "runner 'fixture_tests' builds no program",
        };

        Assert.StartsWith("site 'src/gone.cpp'", Detail(TestRed, everything), StringComparison.Ordinal);
        Assert.StartsWith("the text in", Detail(TestRed, everything with { MissingSites = [] }), StringComparison.Ordinal);
        Assert.StartsWith("target 'fixture'", Detail(TestRed, everything with { MissingSites = [], Counts = [] }), StringComparison.Ordinal);
        Assert.StartsWith("runner", Detail(TestRed, everything with { MissingSites = [], Counts = [], TargetBuilt = true }), StringComparison.Ordinal);
        Assert.Equal(
            "no object target 'fixture' builds depends on 'src/fixture.cpp', 'src/budget.hpp'",
            Detail(TestRed with { Coupled = [new MutationSite("src/budget.hpp", "texts/m.before", "texts/m.after", 4)] }, new ArmPreflight()));

        static string Detail(MutationArm arm, ArmPreflight preflight) => ArmJudge.Judge(arm, new ArmObservation(preflight))!.Detail;
    }

    /// <summary>
    /// Nothing is reached before what decides it is observed - the build, then a TEST-RED arm's run or a BUILD-RED arm's
    /// paired control - and each time the judge says which to observe next.
    /// </summary>
    [Fact]
    public void NothingIsReached_BeforeWhatDecidesItIsObserved()
    {
        var nothing = new ArmObservation(Ready);
        var built = nothing with { Build = Built() };
        var negative = nothing with { Build = FailedAt(Object) };

        Assert.Null(ArmJudge.Judge(TestRed, nothing));
        Assert.Equal(ArmStep.Build, ArmJudge.Next(TestRed, nothing));
        Assert.Null(ArmJudge.Judge(TestRed, built));
        Assert.Equal(ArmStep.Run, ArmJudge.Next(TestRed, built));
        Assert.Null(ArmJudge.Judge(BuildRed, negative));
        Assert.Equal(ArmStep.Control, ArmJudge.Next(BuildRed, negative));
        Assert.Equal(ArmStep.Build, ArmJudge.Next(BuildRed, nothing));
    }

    /// <summary>
    /// A build that was stopped, or reached a guard's verdict, decides the arm with that verdict - the mutated build's or
    /// the paired control's - whatever it says failed or was not rebuilt, which a build that did not finish never proves.
    /// </summary>
    [Theory]
    [InlineData(LegVerdict.Stopped)]
    [InlineData(LegVerdict.InputsMoved)]
    [InlineData(LegVerdict.Contended)]
    [InlineData(LegVerdict.Unmeasured)]
    [InlineData(LegVerdict.Unwitnessed)]
    public void ABuildThatDidNotFinish_DecidesWithItsOwnVerdict(LegVerdict verdict)
    {
        var unfinished = new ArmBuild(ReachedVerdict.Of(verdict, "why it did not finish"), [Upstream], [Object]);

        var mutated = ArmJudge.Judge(TestRed, new ArmObservation(Ready) { Build = unfinished });
        var control = ArmJudge.Judge(BuildRed, new ArmObservation(Ready) { Build = FailedAt(Object), Control = unfinished });

        Assert.Equal((verdict, "the mutated build: why it did not finish"), (mutated?.Verdict, mutated?.Detail));
        Assert.Equal((verdict, "the paired control's build: why it did not finish"), (control?.Verdict, control?.Detail));
    }

    /// <summary>
    /// A mutated build that failed at a step no object depending on the site is - or named no step - failed upstream of
    /// the mutation, for either kind of arm; one that failed only at such objects is the mutation not compiling, which a
    /// TEST-RED arm never declares.
    /// </summary>
    [Fact]
    public void AFailedBuild_IsJudgedByWhereItFailed()
    {
        Assert.Equal(
            (LegVerdict.Failed, $"the mutated build failed at {Upstream}, which is no object that depends on the site"),
            Reached(TestRed, FailedAt(Object, Upstream)));
        Assert.Equal(
            (LegVerdict.Failed, $"the mutated build failed at {Upstream}, which is no object that depends on the site"),
            Reached(BuildRed, FailedAt(Upstream)));
        Assert.Equal(
            (LegVerdict.Failed, "the mutated build failed, and named no step that failed: exited 1"),
            Reached(BuildRed, FailedAt()));
        Assert.Equal(
            (LegVerdict.Violated, $"declared TEST-RED, and the mutation does not compile: {Object}, {HeaderObject}"),
            Reached(TestRed, FailedAt(Object, HeaderObject)));

        static (LegVerdict?, string?) Reached(MutationArm arm, ArmBuild build)
        {
            var verdict = ArmJudge.Judge(arm, new ArmObservation(Ready) { Build = build });

            return (verdict?.Verdict, verdict?.Detail);
        }
    }

    /// <summary>
    /// A mutated build that built, with an object depending on the site ninja's log shows it did not rebuild, is
    /// unwitnessed, for either kind of arm: what it built is no evidence of the mutation.
    /// </summary>
    [Fact]
    public void ABuildThatDidNotRebuildADependent_IsUnwitnessed()
    {
        var one = ArmJudge.Judge(TestRed, new ArmObservation(Ready) { Build = Built(notRebuilt: [Object]) });
        var two = ArmJudge.Judge(BuildRed, new ArmObservation(Ready) { Build = Built(notRebuilt: [Object, HeaderObject]) });

        Assert.Equal((LegVerdict.Unwitnessed, $"{Object} depends on the site, and the mutated build did not rebuild it"), (one?.Verdict, one?.Detail));
        Assert.Equal(
            (LegVerdict.Unwitnessed, $"{Object}, {HeaderObject} depend on the site, and the mutated build did not rebuild them"),
            (two?.Verdict, two?.Detail));
    }

    /// <summary>
    /// A BUILD-RED arm: its mutation building is a violation; its paired control is judged once the mutation failed where
    /// it should - not building a violation, not rebuilding a dependent unwitnessed, and building passed.
    /// </summary>
    [Fact]
    public void ABuildRedArm_IsJudgedByItsMutationAndItsPairedControl()
    {
        var negative = FailedAt(Object);

        Assert.Equal(
            (LegVerdict.Violated, "declared BUILD-RED, and the mutation built"),
            Reached(new ArmObservation(Ready) { Build = Built() }));
        Assert.Equal(
            (LegVerdict.Violated, $"the paired positive control did not build: {Object}"),
            Reached(new ArmObservation(Ready) { Build = negative, Control = FailedAt(Object) }));
        Assert.Equal(
            (LegVerdict.Violated, "the paired positive control did not build"),
            Reached(new ArmObservation(Ready) { Build = negative, Control = FailedAt() }));
        Assert.Equal(
            (LegVerdict.Unwitnessed, $"{HeaderObject} depends on the site, and the paired control's build did not rebuild it"),
            Reached(new ArmObservation(Ready) { Build = negative, Control = Built(notRebuilt: [HeaderObject]) }));
        Assert.Equal(
            (LegVerdict.Passed, $"the mutation stops the build at {Object}, and its paired control builds"),
            Reached(new ArmObservation(Ready) { Build = negative, Control = Built() }));

        static (LegVerdict?, string?) Reached(ArmObservation observation)
        {
            var verdict = ArmJudge.Judge(BuildRed, observation);

            return (verdict?.Verdict, verdict?.Detail);
        }
    }

    /// <summary>Each row of a TEST-RED arm's run reaches its own verdict, saying what it found.</summary>
    [Theory]
    [MemberData(nameof(RunRowNames))]
    public void EachRowOfTheRun_ReachesItsVerdict(string row)
    {
        var (run, verdict, detail) = RunRows[row];

        var reached = ArmJudge.Judge(TestRed, new ArmObservation(Ready) { Build = Built(), Run = run });

        Assert.Equal((verdict, detail), (reached?.Verdict, reached?.Detail));
    }

    /// <summary>
    /// The run's rows are read in order: a hang before the report it never wrote; a failing exit with no failing case
    /// before the count; a count that differs before no case red, so a run that skipped the guarded case never reads as
    /// one that survived; the red set before the neighbours, and the neighbours before the diagnostic.
    /// </summary>
    [Fact]
    public void TheRunsRows_AreReadInOrder()
    {
        Assert.StartsWith("ran past", Detail(new ArmRun { StoppedAtBound = true, Bound = TimeSpan.FromMinutes(2), Factor = 10 }), StringComparison.Ordinal);
        Assert.StartsWith("exited 1, and", Detail(Ran(1, Report([], ["Fixture.Depth"]))), StringComparison.Ordinal);
        Assert.StartsWith("ran 1 case(s), and the arm declares", Detail(Ran(0, Report([], ["Fixture.Depth"]))), StringComparison.Ordinal);
        Assert.StartsWith(
            "the cases that failed",
            Detail(Ran(1, Report(["Fixture.Other"], ["Fixture.Charge", "Fixture.Third"], notRun: ["Fixture.Depth"]), said: false)),
            StringComparison.Ordinal);
        Assert.StartsWith(
            "neighbour",
            Detail(Ran(1, Report(["Fixture.Charge"], ["Fixture.Other", "Fixture.Third"], notRun: ["Fixture.Depth"]), said: false)),
            StringComparison.Ordinal);

        static string? Detail(ArmRun run) => ArmJudge.Judge(TestRed, new ArmObservation(Ready) { Build = Built(), Run = run })?.Detail;
    }

    /// <summary>A build that passed, with every dependent rebuilt where <paramref name="notRebuilt"/> names none.</summary>
    private static ArmBuild Built(IReadOnlyList<string>? notRebuilt = null) => new(ReachedVerdict.Of(LegVerdict.Passed, "built"), [], notRebuilt ?? []);

    /// <summary>A build that failed, saying <paramref name="steps"/> failed.</summary>
    private static ArmBuild FailedAt(params string[] steps) => new(ReachedVerdict.Of(LegVerdict.Failed, "exited 1"), steps, []);

    /// <summary>A run that exited <paramref name="exitCode"/> and wrote <paramref name="report"/>.</summary>
    private static ArmRun Ran(int exitCode, JUnitReport report, bool said = true)
        => new() { ExitCode = exitCode, ReportWritten = true, Report = report, DiagnosticSaid = said, Bound = TimeSpan.FromMinutes(2), Factor = 10 };

    /// <summary>A report as GoogleTest writes one: the red cases, the green ones, and those it did not run.</summary>
    private static JUnitReport Report(string[] reds, string[] greens, string[]? notRun = null)
    {
        static string Case(string id, string status, string body)
        {
            var dot = id.IndexOf('.', StringComparison.Ordinal);

            return $"<testcase classname=\"{id[..dot]}\" name=\"{id[(dot + 1)..]}\" status=\"{status}\">{body}</testcase>";
        }

        var xml = "<testsuites><testsuite name=\"Fixture\">"
            + string.Concat(reds.Select(id => Case(id, "run", "<failure message=\"red\"/>")))
            + string.Concat(greens.Select(id => Case(id, "run", string.Empty)))
            + string.Concat((notRun ?? []).Select(id => Case(id, "notrun", string.Empty)))
            + "</testsuite></testsuites>";

        return JUnitReport.Read(xml) ?? throw new InvalidOperationException($"No report: {xml}");
    }
}
