using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using DssHarness.Core.Anchors;
using DssHarness.Core.Ci;
using DssHarness.Core.Agents.Policy;
using DssHarness.Core.Configuration;
using DssHarness.Core.Execution;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Git;
using DssHarness.Core.Help;
using DssHarness.Core.Hosts;
using DssHarness.Core.Legs;
using DssHarness.Core.Mutations;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Orchestration.Flow;
using DssHarness.Core.Output;
using DssHarness.Core.Platform;
using DssHarness.Core.Processes;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;
using DssHarness.Core.Runners;
using DssHarness.Core.Runs;
using DssHarness.Core.Sync;
using DssHarness.Core.Waiting;

namespace DssHarness.Tests;

public sealed partial class HelpTests
{
    /// <summary>
    /// --use-staged says, for each command that takes it, which copies end their legs inputs-moved: one part made, one
    /// that is not there, and one this tool did not make - of each copy, a host's as a leg's own alike.
    /// </summary>
    [Theory]
    [InlineData("build", "Build")]
    [InlineData("test", "Test")]
    [InlineData("run", "Run against")]
    [InlineData("check-mutations", "Sweep")]
    public async Task UseStaged_SaysWhichCopiesEndTheirLegsInputsMoved(string command, string verb)
    {
        var result = await CliRunner.RunAsync([command, "--help"], TestContext.Current.CancellationToken);

        Assert.Contains(
            $"{verb} what is already staged on each copy, without syncing again. Legs on a copy a sync or a takeover began and did "
                + "not finish, that is not there, or that holds no copy this tool made are inputs-moved.",
            Prose.Words(result.StandardOutput),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// --keep-going is taken by each command that builds its legs, and by no other, and its help says what each build tool
    /// is given - the arguments its adapter's way names - and what is refused, with the exits from the code.
    /// </summary>
    [Theory]
    [InlineData("build", true)]
    [InlineData("test", true)]
    [InlineData("run", true)]
    [InlineData("check-mutations", false)]
    public async Task KeepGoing_IsTakenByEachCommandThatBuildsItsLegs_SayingWhatEachToolIsGiven(string command, bool takes)
    {
        var help = Prose.Words((await CliRunner.RunAsync([command, "--help"], TestContext.Current.CancellationToken)).StandardOutput);

        if (!takes)
        {
            Assert.DoesNotContain(DssHarness.Core.Build.KeepGoing.Option, help, StringComparison.Ordinal);
            return;
        }

        var told = DssHarness.Core.Build.BuildAdapters.All
            .SelectMany(adapter => adapter.KeepGoingWays)
            .Where(way => way.How == DssHarness.Core.Build.KeepGoingHow.WhenTold)
            .Select(way => string.Join(' ', way.Arguments))
            .ToList();

        Assert.Equal(new[] { "-k 0", "-k" }, told);
        Assert.Contains(
            $"{DssHarness.Core.Build.KeepGoing.Option} Have each leg's build go on past the units that fail to build, so its line names "
                + "every one and counts them: ninja is given -k 0 and make -k; MSBuild, dotnet and dart go on by themselves; a leg "
                + "whose toolchain names no generator, or a generator with no known way, NMake's and Xcode's among them, is refused "
                + $"before anything runs ({HarnessExit.Refused.ToString(CultureInfo.InvariantCulture)}), and so is a run that builds "
                + $"none of its legs ({HarnessExit.UsageError.ToString(CultureInfo.InvariantCulture)}). help legs lists each generator's way.",
            help,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The legs topic says what a failed build names of the units it failed to build, and how --keep-going has each build
    /// tool go on past them - every way an adapter answers, as the adapter says it - and the README and the architecture
    /// say the same, the architecture naming each owner by the type that is it.
    /// </summary>
    [Fact]
    public void LegsTopic_SaysWhatAFailedBuildNamesOfItsUnits_AndEachToolsWayToGoOn()
    {
        var help = HelpText.Of("legs").StandardOutput;
        var prose = Prose.Words(help);

        Assert.Contains(
            "A leg whose build its own tool failed names each unit it failed to build: by its source, as ninja's manifest names "
                + "the source a failed step compiles - a step that links or runs a command by its output - or by the target make "
                + "says failed, which is the object it was building. Its line says how many, they are listed beneath the table, "
                + "and --json carries them as failedUnits: each unit's source and step, the count, the tool, whether its tool went on "
                + "past failing units, how many failing lines could not be told its own, and how far it got. Only the build's own "
                + "steps are units: ninja's FAILED line directly after a status line of its own - one printing what a build line of "
                + "its manifest prints, at a count no lower than the build stands at and no more than one higher, two after a console "
                + "step's FAILED line, whose end has no status, and any higher while a console step runs, for ninja holds back every "
                + "other step's lines until it ends and then writes only some of their statuses - and its last such status for how "
                + "far it got, one more where a console step's FAILED line or start came after it, and not said where a console step "
                + "that did not fail was followed by statuses of its own, which it may have ended before or after; samurai's failed "
                + "jobs whose commands a build line runs, and its last status of its own - written as each step starts, none for one "
                + "it starts while a console step runs - for how far it got, never one more for a console step's end, and not said "
                + "where that status is a console step's start before the last step it planned; make's failing step of a target's "
                + "own makefile, and a failure of CMake's driving makefiles that no deeper failure of its target explains, read by its "
                + "structure in any language - the makefile and line parted from the target as each catalog writes the colon, "
                + "French's ' : ' and Simplified Chinese's full-width one among them - and whatever signal ended it. A build nested "
                + "in a step - an external project's - is that step's unit, never its own lines: a failing line ninja wrote is a "
                + "nested build's only within a failed step's output, before the nested build's own end, and a failure make wrote "
                + "only where a shallower step known to be the build's own failed after it - its makefile read and none of the "
                + "driving ones, or, where no failure names a makefile, as make before 4 writes them, its target no driving "
                + "makefile's goal. One that cannot be told the build's own or a "
                + "nested build's - after a status line not its own, as every one is where NINJA_STATUS spells the status otherwise "
                + "- is counted on the line as perhaps a unit of its own, never read as clean; and check-mutations judges an arm's "
                + "build by the same reading: its own failing steps and every one that cannot be told apart, a nested build's alone "
                + "left out. Where which units failed is not read - MSBuild's, dotnet's and dart's output is not, nor a log that could "
                + "not be read back, nor one in which no line of the build's own named a failed unit - the line says so and why, "
                + "never that none failed. ninja and make stop at their first failures, so the line of a build of theirs not kept "
                + "going says that every unit it never reached was not built and says nothing of the code, and - where ninja's own "
                + "[run/planned] status says it - how many of the steps it had to run it ran: 'ran 22 of the 1093 steps it had to run'.",
            prose,
            StringComparison.Ordinal);
        Assert.Contains(
            "--keep-going, on build, test and run - and keepGoing on a workflow's build, test and run steps - has each leg's "
                + "build go on past its failing units, so its line names every one.",
            prose,
            StringComparison.Ordinal);
        Assert.Contains(
            "A toolchain that names no generator - CMake would pick its build tool as it configures - and a generator no way is "
                + "known for, Xcode's among them, refuse --keep-going for each leg they build before anything runs "
                + $"({HarnessExit.Refused.ToString(CultureInfo.InvariantCulture)}), naming every one; and a run that builds none of "
                + $"its legs refuses it too ({HarnessExit.UsageError.ToString(CultureInfo.InvariantCulture)}). A leg that builds in a "
                + "copy of its tree builds by the copy's configuration, so where the copy's gives its tool no way, its build is "
                + $"refused before it starts ({HarnessExit.Refused.ToString(CultureInfo.InvariantCulture)}): no tool is run without its "
                + "way while the build was asked for it. Whether a tool went on is read from what its build ran with, and a ninja told "
                + "to go on ended its build only where its last word says so - 'cannot make progress due to previously encountered "
                + "errors', or an error of its own - a failed step, ninja's FAILED line or samurai's failed job, never being its last "
                + "word: killed after a step failed, it says nothing, and the build is stopped, never one whose unrun steps need one "
                + "that failed. check-mutations takes none: an arm's build is judged by whether, and where, it fails.",
            prose,
            StringComparison.Ordinal);

        var ways = DssHarness.Core.Build.BuildAdapters.All.SelectMany(adapter => adapter.KeepGoingWays).ToList();
        var lines = help.ReplaceLineEndings("\n").Split('\n');

        Assert.Equal(6, ways.Count);
        Assert.All(ways, way =>
        {
            var how = way.How switch
            {
                DssHarness.Core.Build.KeepGoingHow.WhenTold => $"given {string.Join(' ', way.Arguments)}",
                DssHarness.Core.Build.KeepGoingHow.ByItself => "given nothing",
                _ => $"refused ({HarnessExit.Refused.ToString(CultureInfo.InvariantCulture)})",
            };

            Assert.Contains($"  {way.Builds}", lines);
            Assert.Contains($"{way.Builds} {how}: {way.Said}.", prose, StringComparison.Ordinal);
        });

        var readme = Prose.Document("README.md");

        Assert.Contains("| `build [--legs a,b] [--time] [--keep-going]` |", readme, StringComparison.Ordinal);
        Assert.Contains("| `test [--legs a,b] [--time] [--set-up] [--keep-going]` |", readme, StringComparison.Ordinal);
        Assert.Contains("| `run <runner> [--legs a,b] [--time] [--input name=value] [--set-up] [--keep-going]` |", readme, StringComparison.Ordinal);
        Assert.Contains(
            "`--keep-going`, on `build`, `test` and `run` - and `keepGoing` on a workflow's `build`, `test` and `run` steps - has "
                + "each leg's build go on past its failing units: ninja is given `-k 0` and make `-k`, MSBuild, dotnet and dart go on "
                + "by themselves and are given nothing, and a leg whose toolchain names no generator, or one no way is known for - "
                + $"NMake's and Xcode's - is refused ({HarnessExit.Refused.ToString(CultureInfo.InvariantCulture)}) before anything "
                + "runs, as is the option on a run that builds none of its legs "
                + $"({HarnessExit.UsageError.ToString(CultureInfo.InvariantCulture)}); a leg built in a copy whose configuration gives "
                + $"its tool no way is refused before its build starts ({HarnessExit.Refused.ToString(CultureInfo.InvariantCulture)}). "
                + "Whether a tool went on is read from what its build ran with, and a ninja told to go on that never said it ended its "
                + "build - a failed step, ninja's or samurai's, is no such word - was stopped from outside.",
            readme,
            StringComparison.Ordinal);
        Assert.Contains(
            "Only the build's own steps are units: a build nested in one of them, an external project's, is that step's unit, "
                + "never its own lines; a failing line ninja wrote that cannot be told the build's own or a nested build's - every "
                + "one, where `NINJA_STATUS` spells its status otherwise - is counted on the line as perhaps a unit of its own, never "
                + "read as clean, and `check-mutations` judges an arm's build by the same reading, a nested build's lines alone left "
                + "out; make's lines are read by their structure, in any language - French's space before a colon and Simplified "
                + "Chinese's full-width colon among them - and whatever signal ended a step.",
            readme,
            StringComparison.Ordinal);
        Assert.Contains(
            "ninja and make stop at their first failures, so a build of theirs says that every unit it never reached was not "
                + "built, and how many of the steps it had to run it ran where ninja's status says it - not where a step in the "
                + "console pool that did not fail, whose end ninja writes no status for, may have ended after the last, nor where "
                + "samurai's last status is a console step's start before the last step it planned, for samurai counts each step "
                + "as it starts and writes no status for one it starts while that runs.",
            readme,
            StringComparison.Ordinal);

        var architecture = Prose.Document("docs", "architecture.md");

        Assert.Contains(
            "and `--json` carries them on the leg as `failedUnits`: `tool`, `count`, `units` - each one's `source` and `step` - "
                + "`unread`, `keptGoing`, `stopsAtFirst`, `stepsRun`, `stepsPlanned`, `unattributed` and `said`, the line's own words "
                + "(`FailedUnits.Document`, which a workflow step's red entry carries too).",
            architecture,
            StringComparison.Ordinal);
        Assert.Contains(
            "So from a console step's start until its own `FAILED:` line, the count is held to from below alone.",
            architecture,
            StringComparison.Ordinal);
        Assert.Contains(
            "samurai counts otherwise: its `[%s/%t]` is written as each step starts, its count the steps it has started with that "
                + "one, and it writes none for a step it starts while a console step runs - so its last status of its own is how far "
                + "it got, a console step's start among them, never one more for that step's end, and is not said where it is a "
                + "console step's start and the build planned more, for what samurai started while that step ran is written nowhere.",
            architecture,
            StringComparison.Ordinal);
        Assert.Contains(
            "the makefile and line parted from the target as each catalog writes the colon: `: ` in English and most, ` : ` in "
                + "French, a full-width `：` in Simplified Chinese",
            architecture,
            StringComparison.Ordinal);
        Assert.Contains(
            "A failure is a nested build's where a step of this build at a shallower depth failed after it, the step whose make "
                + "ran that build - one known to be a step: its makefile read and none of the driving ones, or, where no failure "
                + "names a makefile, its target no driving makefile's goal.",
            architecture,
            StringComparison.Ordinal);
        Assert.Contains(
            "so a failed step is no end of its build - ninja's `FAILED:` line, or samurai's `job failed with status`, `job "
                + "terminated due to signal` or `job status unknown` under its name:",
            architecture,
            StringComparison.Ordinal);
        Assert.Contains(
            "`check-mutations` judges an arm's failed build by the same reading (`Ninja.FailedOutputs`, `OwnSteps.NotNested`): "
                + "its own failing steps and every one that cannot be told apart, so none that could be a step not depending on a "
                + "site is lost, and a nested build's alone left out - where nothing is nested, every failing line, as before.",
            architecture,
            StringComparison.Ordinal);

        foreach (var owner in new[]
        {
            $"{nameof(DssHarness.Core.Build.IBuildAdapter)}.{nameof(DssHarness.Core.Build.IBuildAdapter.FailedUnitsOf)}",
            $"{nameof(DssHarness.Core.Build.IBuildAdapter)}.{nameof(DssHarness.Core.Build.IBuildAdapter.KeepGoingFor)}",
            $"{nameof(DssHarness.Core.Build.IBuildAdapter)}.{nameof(DssHarness.Core.Build.IBuildAdapter.KeepGoingWays)}",
            nameof(DssHarness.Core.Build.FailedUnits),
            nameof(DssHarness.Core.Build.KeepGoingWay),
            $"{nameof(DssHarness.Core.Build.KeepGoing)}.{nameof(DssHarness.Core.Build.KeepGoing.Option)}",
            $"{nameof(DssHarness.Core.Build.KeepGoing)}.{nameof(DssHarness.Core.Build.KeepGoing.Require)}",
            $"{nameof(DssHarness.Core.Build.Ninja)}.{nameof(DssHarness.Core.Build.Ninja.Own)}",
            $"{nameof(DssHarness.Core.Build.Ninja)}.{nameof(DssHarness.Core.Build.Ninja.Stopped)}",
            $"{nameof(DssHarness.Core.Build.NinjaEdge)}.{nameof(DssHarness.Core.Build.NinjaEdge.Description)}",
            $"{nameof(DssHarness.Core.Build.KeepGoing)}.{nameof(DssHarness.Core.Build.KeepGoing.RequireFor)}",
            $"{nameof(DssHarness.Core.Build.KeepGoingWay)}.{nameof(DssHarness.Core.Build.KeepGoingWay.GivenIn)}",
            $"{nameof(DssHarness.Core.Build.CMakeAdapter)}.{nameof(DssHarness.Core.Build.CMakeAdapter.KeptGoing)}",
            $"{nameof(DssHarness.Core.Build.FailedUnits)}.{nameof(DssHarness.Core.Build.FailedUnits.WhyUnread)}",
            $"{nameof(DssHarness.Core.Build.FailedUnits)}.{nameof(DssHarness.Core.Build.FailedUnits.Document)}",
            $"{nameof(DssHarness.Core.Build.CMakeNinjaRule)}.{nameof(DssHarness.Core.Build.CMakeNinjaRule.Compiles)}",
            $"{nameof(DssHarness.Core.Build.BuildRequest)}.{nameof(DssHarness.Core.Build.BuildRequest.KeepGoing)}",
            $"{nameof(LegWork)}.{nameof(LegWork.BuildRequest)}",
            $"{nameof(DssHarness.Core.Build.Make)}.{nameof(DssHarness.Core.Build.Make.FailedTargets)}",
            $"{nameof(DssHarness.Core.Build.Ninja)}.{nameof(DssHarness.Core.Build.Ninja.FailedOutputs)}",
            $"{nameof(DssHarness.Core.Build.Ninja.OwnSteps)}.{nameof(DssHarness.Core.Build.Ninja.OwnSteps.NotNested)}",
            $"{nameof(DssHarness.Core.Build.FailedUnits)}.{nameof(DssHarness.Core.Build.FailedUnits.Unattributed)}",
        })
        {
            Assert.Contains($"`{owner}`", architecture, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The legs topic says that a host's DssHarness failing as a defect is a poisoned leg, that one failing so before
    /// anything ran is started once more for each cause not met before - never a count of starts - how the line, or what
    /// ends the leg with none, says it, and that a WSL distribution's check of a heavy leg before its slot is asked once
    /// more alike, a cause met again being the leg's poisoned line with no slot waited for, said on whatever line the leg
    /// then reaches.
    /// </summary>
    [Fact]
    public void LegsTopic_SaysAHostsDssHarnessFailingAsItStarts_IsStartedOnceMore_ItsCheckBeforeTheSlotAlike()
    {
        var words = Prose.Words(HelpText.Of("legs").StandardOutput);

        foreach (var text in new[]
        {
            $"A leg whose host's DssHarness fails as a defect (exit {HarnessExit.InternalError}) with no line for it is poisoned - "
                + "the harness failing, never a host that could not be reached - its line naming the host, the code and what the "
                + "host said.",
            "the leg is started there again, which is safe since nothing ran: a runtime or a tool replaced there while it "
                + "started fails that way once. It is started again for each cause not met before - the code and what the "
                + "failure line said - after a pause that begins at 1s and doubles, to 5 minutes at most, each pause said as it "
                + "begins; a start that fails of a cause met before ends the starts.",
            "A start that then answers is the leg's line, which says what each failed start said ('retried once: ...', and "
                + "retried in --json); one that fails of a cause met before is poisoned, saying it was retried; and one that "
                + "ends with no line for the leg says it in what ends it. Once anything ran there, the leg is never started again.",
            "A WSL distribution asked to check a heavy leg before this machine takes its slot is asked once more alike - a "
                + "check runs nothing - and one that fails of a cause met before is the leg's poisoned line, with no slot waited "
                + "for; what its failed starts said is said on whatever line the leg then reaches, before what its run's own "
                + "starts said.",
        })
        {
            Assert.Contains(text, words, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The legs topic says where a leg works - its tree, its host's copy, or its own in the tree's .legs - and the order
    /// of precedence in one sentence: the command's option, a runner's writesTree, the leg's own key, the default, the
    /// tree.
    /// </summary>
    [Fact]
    public async Task LegsTopic_SaysWhereALegWorks_AndTheOrderOfPrecedence()
    {
        var result = HelpText.Of("legs");

        Assert.Contains(
            "A leg a host runs works in that host's copy of its tree, whatever anything else says. A leg this machine runs "
                + "itself works where the first of these says anything: --local-copy or --in-place on the command, for that run "
                + "(build, test, run); a runner's writesTree, which is in place ('help runners'); the leg's own localCopy, true "
                + "or false; defaults.localCopy, a default for every leg of the repository; and, where none says, its tree itself.",
            Prose.Words(result.StandardOutput),
            StringComparison.Ordinal);
        Assert.Contains(
            "made from the run's one reading of the tree and kept warm between runs: a write the tree receives while the leg "
                + "runs - a fold, a commit, an edit - ends nothing of the leg's",
            Prose.Words(result.StandardOutput),
            StringComparison.Ordinal);
    }

    /// <summary>build, test and run each take --local-copy and --in-place, one description for the three.</summary>
    [Theory]
    [InlineData("build")]
    [InlineData("test")]
    [InlineData("run")]
    public async Task EachCommandThatRunsLegs_TakesLocalCopyAndInPlace(string command)
    {
        var result = await CliRunner.RunAsync([command, "--help"], TestContext.Current.CancellationToken);

        Assert.Contains(
            "Every leg this machine runs itself works in a copy of its tree kept in the tree's .legs, for this run: over the "
                + "leg's own localCopy and defaults.localCopy.",
            Prose.Words(result.StandardOutput),
            StringComparison.Ordinal);
        Assert.Contains(
            "Every leg this machine runs itself works in its tree itself, for this run: over the leg's own localCopy and "
                + "defaults.localCopy.",
            Prose.Words(result.StandardOutput),
            StringComparison.Ordinal);
    }

    /// <summary>Both given, --local-copy and --in-place are a usage error; neither is passed to a host.</summary>
    [Theory]
    [InlineData("build", "")]
    [InlineData("test", "")]
    [InlineData("run", "probe")]
    public async Task LocalCopyAndInPlaceTogether_AreAUsageError(string command, string argument)
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();

        await harness.InitializeHarnessAsync(
            temp.Path,
            TestContext.Current.CancellationToken,
            new HarnessConfig
            {
                PredefinedRunners = { ["probe"] = new RunnerConfig { Phases = [new RunnerPhase { Name = "p", Command = ["true"] }] } },
            });

        string[] arguments = command is "build" or "test"
            ? [command, "--local-copy", "--in-place", "--directory", temp.Path]
            : [command, argument, "--local-copy", "--in-place", "--directory", temp.Path];

        var outcome = await CliRunner.RunAsync([.. arguments], TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.UsageError, outcome.ExitCode);
        Assert.Contains(
            "--local-copy and --in-place say opposite things about where this run's own legs work. Give one.",
            outcome.StandardOutput + outcome.StandardError,
            StringComparison.Ordinal);
    }

    /// <summary>sync's --dry-run says it exits as the sync would, naming each refusal it would make.</summary>
    [Fact]
    public async Task SyncsDryRun_SaysItExitsAsTheSyncWould()
    {
        var result = await CliRunner.RunAsync(["sync", "--help"], TestContext.Current.CancellationToken);

        Assert.Contains(
            "List what would be written and deleted, change nothing, and exit as the sync would: 13 where it would refuse - a "
                + "directory it did not make, without --adopt; more deleted than sync.maxDeleteFraction allows; a worktree's copy "
                + "another worktree of the same name holds.",
            Prose.Words(result.StandardOutput),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The legs topic says a toolchain names its compiler, that a build directory is held to the
    /// compiler it was configured with by the file it starts, not by its name, and where a language
    /// only a subproject enables is identified.
    /// </summary>
    [Fact]
    public async Task LegsTopic_SaysAToolchainNamesItsCompiler_AndHowABuildDirectoryIsHeldToIt()
    {
        var result = HelpText.Of("legs");

        Assert.Contains("A toolchain CMake builds with also names its compiler - CC or CXX under env,", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("One only .NET or Dart builds with names none.", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("build directory is then held to the compiler it was configured with by the file that", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("and by its name only where that PATH holds", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("\"compilerId\": { \"C\": \"MSVC\", \"CXX\": \"MSVC\" }", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("A build CMake configured with another compiler fails before anything is built with", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("loaded: CMakeFiles/<version>/CMake<language>Compiler.cmake - where the record names", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains(
            "where the record names the compiler the answer names and no configure has failed since that answer - which the error "
            + "index CMake 4 writes in an answer's place says - since a configure that identified the compiler again and then failed "
            + "leaves a record of one nothing built with. Never by when either was written, which a clock that steps gets wrong; "
            + "before CMake 4 a failed configure leaves nothing of itself, and the record is read as it names it.",
            Prose.Words(result.StandardOutput),
            StringComparison.Ordinal);
        Assert.DoesNotContain("no newer than the answer", Prose.Words(result.StandardOutput), StringComparison.Ordinal);

        // And which ssh reaches a host, what it is given, and when it is given nothing.
        Assert.Contains("The ssh that runs is the first on the PATH. It is asked first what it would do, with", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("refused before ssh starts. Every call the run makes is then given the address it", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("the address. Each still goes to the address declared, so every Host block written for", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("where ssh reaches the host through a ProxyJump or a ProxyCommand, which do their own", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LegsTopic_DocumentsTheNamesTheRulesAndTheExitCodes_FromTheCode()
    {
        var result = HelpText.Of("legs");


        // Every name the validator accepts for a leg is listed, so the help never leaves out a name a leg can use.
        foreach (var name in PlatformNames.OperatingSystems.Concat(PlatformNames.Processors))
        {
            Assert.Contains(name, result.StandardOutput, StringComparison.Ordinal);
        }

        var expected = new[]
        {
            "--legs",
            "host-exec --ssh",
            "--wsl",
            ".harness-config/sshItems/<name>/",
            $".NET {ToolPackage.MinimumSdkMajor} SDK",
            "never downgraded",
            "witness",
        };

        foreach (var text in expected)
        {
            Assert.Contains(text, result.StandardOutput, StringComparison.Ordinal);
        }

        Assert.Contains($"{LegsExit.Unavailable,3}  legs, sync:", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains($"{HarnessExit.HostUnavailable,3}  host-exec:", result.StandardOutput, StringComparison.Ordinal);

        // What ends a run on its hosts, from the numbers both ends count by.
        var prose = Prose.Words(result.StandardOutput);

        Assert.Contains(
            "No leg, host-exec or sync's operation a host runs for this machine outlives the command that asked for it, whatever carries it there; "
            + "only what holds a host awake between commands (holdAwakeSeconds) is started to outlast one.",
            prose,
            StringComparison.Ordinal);

        // What a host's shell prints while it starts is dropped by the mark it writes first, before the command's fixed words.
        Assert.Contains(
            "The command line ssh hands a remote shell holds only fixed words, after a mark the shell writes first so that what its "
            + "startup files print is dropped, and no argument is ever reinterpreted by sh, cmd or PowerShell.",
            prose,
            StringComparison.Ordinal);

        // The silence a host lets pass, how often it looks for it, and so the longest a leg runs on unheard: 15, 8 and 4.
        Assert.Equal((15, 8, 4), (HostAgentProtocol.BeatSeconds, HostAgentProtocol.BeatsMissed, HostAgentProtocol.SilenceLooks));
        Assert.Contains(
            "when the beat this machine writes on it every 15 seconds has gone unheard for 120, which the host looks for every 30: a dispatcher "
            + "killed where nothing closes its end of the connection leaves a leg that is cancelled within 150 seconds of its last beat, on an ssh "
            + "host as in a WSL distribution",
            prose,
            StringComparison.Ordinal);
        Assert.Contains("On Linux and macOS a session hung up cancels it the same way.", prose, StringComparison.Ordinal);

        // What a cancelled command is waited for while: no time of the code's own, only what it started still running.
        Assert.Contains(
            "What the host started is then waited for while anything it started still runs, however long that takes to stop, and said every "
            + $"{Waits.SaidEvery.TotalMinutes} minutes where this machine would read it; once nothing it started runs and it still has not ended - a "
            + "command reading the output of something its child left running, or writing a line nobody reads - it is ended, and the host's "
            + "DssHarness with it, so the host is never held by a run nobody reads, as it is where the host's DssHarness is itself stopped "
            + "meanwhile. A command that finishes what it began - a deletion, a fold, a hand-over - is waited for to its end, as it is where it "
            + "is typed and interrupted. Where this machine can no longer write to the connection while a host works, it says so here, with why: the host hears no more of "
            + "it, and stops what it was asked. To end a run on its hosts, stop the command that dispatched it.",
            prose,
            StringComparison.Ordinal);

        // The one door this machine's own tool is updated through, from the names and the codes the command uses.
        Assert.Contains(
            "This machine's own DssHarness is updated through 'dssharness update-tool', never by a 'dotnet tool update' typed beside running "
            + "commands:",
            prose,
            StringComparison.Ordinal);
        Assert.Contains(
            $"only while no other DssHarness process runs on the machine: with one running it changes nothing and names each by its process and "
            + $"command ({HarnessExit.Refused}), and with --wait it waits for them to end, saying who it waits for as it starts and every "
            + $"{ToolUpdateService.SaidAgainEvery.TotalMinutes} minutes - but for a wait asked from inside a running DssHarness, which would never "
            + "end and is refused, as is one where the process table names no process's parent.",
            prose,
            StringComparison.Ordinal);
        Assert.Contains(
            "Two updates of one place never change it together: the change holds a machine-wide lock on the directory the tool is kept in, "
            + "and an update that finds it held - two run beside the installed tool are each the program dotnet, and neither counts the "
            + $"other - changes nothing and is refused ({HarnessExit.Refused}), with {ToolUpdateService.WaitOption} or without, since it "
            + "read the machine before the other changed it.",
            prose,
            StringComparison.Ordinal);
        Assert.Contains(
            "Once the update has begun this command does not stop it, interrupted or not, and reads back what it left: it says the update done only once "
            + $"the tool is listed as that release, and a failure with what dotnet said and what is listed since, or why that is not known "
            + $"({HarnessExit.CommandFailed}). dotnet is waited for to its end, however long it takes, and said every {Waits.SaidEvery.TotalMinutes} "
            + "minutes while it lasts.",
            prose,
            StringComparison.Ordinal);
        Assert.Contains(
            "Where nuget.org does not say which release is the newest it says why - no route to it, a refusal, an answer it cannot read - "
            + "and changes nothing, unless the copy asking is itself a release newer than the installed one, which it then moves to, saying so.",
            prose,
            StringComparison.Ordinal);
        Assert.Contains(
            "Windows replaces no program while it runs, and 'dssharness update-tool' is itself a DssHarness running: there it changes nothing and "
            + "says the command that does, with the options it was typed with - the door on every system, which is the newest release run beside "
            + "the installed one, without installing it, its exit code the update's. With --wait:",
            prose,
            StringComparison.Ordinal);
        Assert.Contains(
            "  dotnet tool exec DssHarness --yes --source https://api.nuget.org/v3/index.json -- update-tool --wait",
            result.StandardOutput,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The legs topic says a developer environment may hold its legs to one MSVC toolset with the optional toolsVersion -
    /// what it names, that it is optional and only its form is read, with no toolset built in - and how the survey and
    /// the setup hold a leg to it; the config topic names it beside developerEnvironments.
    /// </summary>
    [Fact]
    public void LegsTopic_SaysADeveloperEnvironmentMayHoldItsLegsToOneToolset()
    {
        var legs = Prose.Words(HelpText.Of("legs").StandardOutput);

        Assert.Contains(
            @"toolsVersion, optional, holds an environment's legs to one MSVC toolset, named as VC\Tools\MSVC names its toolsets: "
            + "by two numbers, for the newest of that line, or by all three, for that one - for a compiler such as a CUDA "
            + "toolkit's nvcc, which refuses an MSVC newer than it knows. Left out, vcvarsall.bat sets up the instance's default "
            + "toolset. Only its form is checked when the file is read: no toolset is built into this tool. The survey then "
            + "takes the newest instance holding a toolset it names, with the newest of those, and a host with none turns the "
            + "leg away as a tool missing, naming what each instance holds. vcvarsall.bat is run with -vcvars_ver= and that "
            + "toolset, and one that sets up another fails the leg; the leg's line names the MSVC set up. Two environments of "
            + "one kind keep two toolsets apart on one machine:",
            legs,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"developerEnvironments\": { \"visualStudio\": { \"kind\": \"visualStudio\" }, \"cuda\": { \"kind\": \"visualStudio\", "
            + "\"toolsVersion\": \"14.44\" } }",
            legs,
            StringComparison.Ordinal);
        Assert.Contains(
            "developerEnvironments what a toolchain's legs start in, set up on the host that runs them: visualStudio runs that "
            + "instance's vcvarsall.bat, for the MSVC toolset an optional toolsVersion names ('help legs')",
            Prose.Words(HelpText.Of("config").StandardOutput),
            StringComparison.Ordinal);

        // The README and the architecture say the same: optional, its form alone checked, and held to on both sides.
        Assert.Contains(
            "Left out, `vcvarsall.bat` sets up the instance's default toolset. Only its form is checked when `config.json` is "
            + "read: no toolset is built into the tool.",
            Prose.Document("README.md"),
            StringComparison.Ordinal);
        Assert.Contains(
            "**A developer environment may hold its legs to one MSVC toolset**, with the optional `toolsVersion`",
            Prose.Document("docs", "architecture.md"),
            StringComparison.Ordinal);
        Assert.Contains(
            "and fails the leg where `VCToolsVersion` comes out as anything else.",
            Prose.Document("docs", "architecture.md"),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The legs topic says what a wake window retries and what it never does, with the delay the code uses.
    /// </summary>
    [Fact]
    public async Task LegsTopic_SaysWhatAWakeWindowTriesAgain_AndWhatItNeverDoes()
    {
        var result = HelpText.Of("legs");
        var text = Prose.Words(result.StandardOutput);

        Assert.Contains("hosts.ssh.<name>.wakeWaitSeconds", text, StringComparison.Ordinal);
        Assert.Contains($"every {SshWakeWindow.DefaultPollDelay.TotalSeconds:0} seconds until that many seconds have passed", text, StringComparison.Ordinal);
        Assert.Contains("a key or a login the host refuses is never waited on", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The legs topic says that, for a host whose name is looked up here, no address its name resolved to is named,
    /// wherever ssh names one - and where the name is looked up again, and how far that reaches.
    /// </summary>
    [Fact]
    public async Task LegsTopic_SaysNoAddressAHostsNameResolvedToIsNamed()
    {
        var result = HelpText.Of("legs");
        var text = Prose.Words(result.StandardOutput);

        Assert.Contains(
            "For a host whose name is looked up here, no reason, relayed line or document names an address the name resolved "
            + "to: wherever ssh, or a program there, names one as a word, it reads as the address declared.",
            text,
            StringComparison.Ordinal);
        Assert.Contains(
            "The name is looked up again, afresh, before every call that lets ssh look it up itself - one never pinned, though "
            + "not through a ProxyJump or a ProxyCommand, or once its pin is dropped - so an address it has moved to is withheld "
            + "too, wherever this machine's own lookup finds it as well.",
            text,
            StringComparison.Ordinal);
    }
}
