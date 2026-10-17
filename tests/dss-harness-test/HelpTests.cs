using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using DssHarness.Core.Anchors;
using DssHarness.Core.Configuration;
using DssHarness.Core.Execution;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Hosts;
using DssHarness.Core.Legs;
using DssHarness.Core.Mutations;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Orchestration.Flow;
using DssHarness.Core.Platform;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;
using DssHarness.Core.Runners;
using DssHarness.Core.Sync;

namespace DssHarness.Tests;

/// <summary>
/// Help is treated as documentation, so it is tested like documentation: every
/// command must appear in it, and every documented value must come from the code
/// that produces it rather than from prose someone has to remember to update.
/// </summary>
public sealed partial class HelpTests
{
    [Fact]
    public async Task EveryListedCommand_HasItsOwnWorkingHelp()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var listed = await CliRunner.CommandsAsync(cancellationToken);

        Assert.NotEmpty(listed);

        foreach (var command in listed)
        {
            var result = await CliRunner.RunAsync([command, "--help"], cancellationToken);

            Assert.Equal(0, result.ExitCode);
            Assert.Contains(command, result.StandardOutput, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The admission topic, under either name, names every key a section may declare with the value it takes when left
    /// out and its bounds, and the exit a leg not admitted ends with - each read from the code, so none can drift.
    /// </summary>
    [Theory]
    [InlineData("admission")]
    [InlineData("heavy")]
    public async Task TheAdmissionTopic_NamesEveryKeyWithItsDefault_FromTheCode(string topic)
    {
        var result = await CliRunner.RunAsync(["help", topic], TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);

        var keys = typeof(AdmissionSettings)
            .GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Select(property => JsonNamingPolicy.CamelCase.ConvertName(property.Name))
            .ToList();

        Assert.Equal(["heavyLegs", "maxMemoryPercent", "settleSeconds", "pollSeconds", "maxWaitMinutes", "minFreeGiB"], keys);

        foreach (var key in keys)
        {
            Assert.Contains($"  {key}", result.StandardOutput, StringComparison.Ordinal);
        }

        foreach (var value in new[]
        {
            $"({AdmissionSettings.DefaultHeavyLegs})",
            string.Create(CultureInfo.InvariantCulture, $"({AdmissionSettings.DefaultMaxMemoryPercent})"),
            $"([{string.Join(", ", AdmissionSettings.DefaultSettleSeconds)}])",
            $"({AdmissionSettings.DefaultPollSeconds})",
            string.Create(CultureInfo.InvariantCulture, $"({AdmissionSettings.DefaultMaxWaitMinutes})"),
            string.Create(CultureInfo.InvariantCulture, $"from 0 to {AdmissionSettings.MostMinFreeGiB}; 0 stops no build ({AdmissionSettings.DefaultMinFreeGiB})"),
            $"at most {AdmissionSettings.MostSeconds}",
            string.Create(CultureInfo.InvariantCulture, $"at most {AdmissionSettings.MostWaitMinutes} minutes"),
            $"exit {LegExit.NotAdmitted}",
        })
        {
            Assert.Contains(value, result.StandardOutput, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task EveryListedCommand_HasADescription()
    {
        var result = await CliRunner.RunAsync(["--help"], TestContext.Current.CancellationToken);

        var lines = CliRunner.CommandLines(result.StandardOutput).ToList();
        Assert.NotEmpty(lines);

        foreach (var line in lines)
        {
            // "  name   description" - a command with no description is undocumented.
            var described = CommandLinePattern().Match(line);

            Assert.True(
                described.Success && described.Groups["description"].Value.Trim().Length > 10,
                $"command line has no usable description: '{line.Trim()}'");
        }
    }

    /// <summary>The layout topic says each run is named however it ended, and one refused before it had a directory by its id alone.</summary>
    [Fact]
    public async Task LayoutTopic_SaysEachRunIsNamed_ARefusedOneByItsIdAlone()
    {
        var result = await CliRunner.RunAsync(["help", "layout"], TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.Success, result.ExitCode);
        Assert.Contains(
            "Each names its run too, in its first line, 'run <id>', and as runId in --json, however it ended: a run refused "
            + "before it had a directory keeps no records, and is cited by that id alone.",
            Prose.Words(result.StandardOutput),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The verdicts topic says what becomes of a build stopped from outside, which is stopped rather than failed - but for
    /// one the harness stopped for hanging, or under another build tool - and of a run killed before it finished, which
    /// reaches no verdict and says nothing itself: the next run in its tree on that machine says it was abandoned, and
    /// releases it. Each verdict a leg reaches without one of its own shows the code its run exits
    /// with, and its remedy.
    /// </summary>
    [Fact]
    public async Task VerdictsTopic_SaysAKilledRunIsReportedAbandoned_ByTheNextRun()
    {
        var result = await CliRunner.RunAsync(["help", "verdicts"], TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.Success, result.ExitCode);
        Assert.Contains(
            "A leg that never ran proves nothing about the code, and nor does a build something stopped from outside before it "
            + "finished, so counting either among the legs that passed reports evidence nobody gathered. A CMake build under "
            + "ninja that fails with ninja saying nothing of why - which it says whenever it ends a build itself - or saying it "
            + "was interrupted is stopped, not failed: run again, it finishes, where a failed one fails again. Read under the "
            + "name it says it under, which for samurai, run by CMake for ninja where it is installed, is its file's. A build "
            + "the harness stopped for hanging stays failed, saying it hung, and so does one under any other build tool, since "
            + "only ninja's lines are read.",
            Prose.Words(result.StandardOutput),
            StringComparison.Ordinal);
        Assert.Contains($" {HarnessExit.Incomplete}  stopped                    Find out what stopped its work, then run again", result.StandardOutput, StringComparison.Ordinal);

        // Each verdict a leg reaches without one of its own shows the code its run exits with, and a remedy.
        Assert.All(
            ["stopped", "skipped-unavailable", "skipped-tool-missing"],
            verdict => Assert.Contains($"  {verdict,-22} not a failure      exit {HarnessExit.Incomplete}", result.StandardOutput, StringComparison.Ordinal));
        Assert.Contains($" {HarnessExit.Incomplete}  skipped-unavailable        Make what its line names available, then run again", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains($" {HarnessExit.Incomplete}  skipped-tool-missing       Install or update the tool its line names,", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains(
            "A run killed, or stopped with its machine, before it finished says nothing more: the next build, test, run or "
            + "check-mutations in its tree on that machine to own its run directory says it was abandoned - its run id, process "
            + "and start, and where its records are - and releases its claim on them.",
            Prose.Words(result.StandardOutput),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The verdicts topic names the three verdicts only a mutation arm reaches, each with the code its run exits with and
    /// its remedy, and says an arm that was due and never driven is stopped, saying why, rather than any of them.
    /// </summary>
    [Fact]
    public async Task VerdictsTopic_NamesWhatAMutationArmReaches_WithItsCodeAndRemedy()
    {
        var result = await CliRunner.RunAsync(["help", "verdicts"], TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.Success, result.ExitCode);
        Assert.Contains($"{LegExit.Violated,3}  violated                   Fix the arm's declaration, or the code it guards", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains($"{LegExit.Survived,3}  survived                   Strengthen the test that should have failed", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains($"{LegExit.Unattributed,3}  unattributed               Contain the crash or hang in the case, or make", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains(new string(' ', 32) + "the runner write its report", result.StandardOutput, StringComparison.Ordinal);
        Assert.All(
            ["violated", "survived", "unattributed"],
            verdict => Assert.Contains($"  {verdict,-22} counts as failure ", result.StandardOutput, StringComparison.Ordinal));
        Assert.Contains(
            "An arm not driven to a verdict - its sweep stopped, or ended by a refusal of the run, while it was driven or before; its "
            + "own build, or its paired control's, stopped from outside; no worker left to run it; or the unmutated run of its test "
            + "binary not passing - is stopped, saying why ('help mutations').",
            Prose.Words(result.StandardOutput),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The mutations topic, under each of its names, lists every key the section takes, read from the code with the
    /// defaults the code gives, every row the registry reads, and the codes a sweep moving from a mutation harness of a
    /// repository's own reads in place of that harness's - each verdict's code read from the verdict table.
    /// </summary>
    [Theory]
    [InlineData("mutations")]
    [InlineData("check-mutations")]
    [InlineData("arms")]
    public async Task TheMutationsTopic_NamesEveryKey_EveryRow_AndTheCodesItMapsOnto(string topic)
    {
        var result = await CliRunner.RunAsync(["help", topic], TestContext.Current.CancellationToken);
        var keys = ConfigKeys.Of<MutationSettings>();

        Assert.Equal(HarnessExit.Success, result.ExitCode);
        Assert.Equal(["registry", "textDirectory", "workers", "reportArgs", "runTimeFactor", "runReads"], keys.Select(key => key.Name));
        Assert.All(keys, key => Assert.Contains($"  {key.Name}", result.StandardOutput, StringComparison.Ordinal));
        Assert.Contains(
            string.Create(CultureInfo.InvariantCulture, $"workers is {MutationSettings.DefaultWorkers} and runTimeFactor {MutationSettings.DefaultRunTimeFactor} where left out."),
            Prose.Words(result.StandardOutput),
            StringComparison.Ordinal);

        foreach (var row in new[]
        {
            "  A | arm | site | before | after | red kind | target | runner | cases | diag | why",
            "  C | arm | case | why ",
            "  G | arm | case | why ",
            "  B | arm | control before | control after | why",
            "  M | arm | site | before | after | why ",
            "  S | arm | legs | why ",
        })
        {
            Assert.Contains(row, result.StandardOutput, StringComparison.Ordinal);
        }

        foreach (var (theirs, verdict) in new[]
        {
            ("0  as declared", LegVerdict.Passed),
            ("1  a violation, a missing diagnostic included", LegVerdict.Violated),
            ("5  nothing reddened", LegVerdict.Survived),
            ("6  a red no case name could be read for", LegVerdict.Unattributed),
            ("7  a build that passed missed a declared unit", LegVerdict.Unwitnessed),
            ("8  the build failed upstream of the unit", LegVerdict.Failed),
            ("4  the harness broke", LegVerdict.Poisoned),
        })
        {
            Assert.Matches(
                $@"(?m)^  {Regex.Escape(theirs)} +{Verdicts.ExitCodeFor(verdict)}  {Regex.Escape(Verdicts.Display(verdict))}\r?$",
                result.StandardOutput);
        }

        Assert.Contains($"4  an arm not driven                               stopped: the run exits {HarnessExit.Incomplete}", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains($"a live owner                               {HarnessExit.Refused,3}  refused-locked", result.StandardOutput, StringComparison.Ordinal);
    }

    /// <summary>
    /// The mutations topic says what the code does where a reader would otherwise guess: where a failed build makes an
    /// arm failed, every verdict an arm can reach beside those its own judge decides, what a control that does not pass
    /// is, which arms the ARMS block names, what a worker's build is reckoned to come to, how a text is read, and which
    /// verdicts the fixture's arms are designed to.
    /// </summary>
    [Fact]
    public async Task TheMutationsTopic_SaysWhatTheCodeDoes_WhereAReaderWouldGuess()
    {
        var said = Prose.Words((await CliRunner.RunAsync(["help", "mutations"], TestContext.Current.CancellationToken)).StandardOutput);

        foreach (var text in new[]
        {
            "failed its build failed at a step that is no object depending on its site - a link, another object - or named no step that failed",
            "violated its declaration did not hold: a site or a cited text that is not there, or a site spelt otherwise than the tree "
            + "spells it, or that is no file the sweep's reading of the tree holds; its before-text not in its site exactly once, or "
            + "replaced by itself; its target or its runner not built, or no object they build depending on a site;",
            "stopped not driven to a verdict: the sweep was stopped, or ended by a refusal of the run, while it was driven or before; "
            + "its own build, or its paired control's, was stopped from outside; no worker was left to run it; or the unmutated run of "
            + "its binary did not pass",
            "a cited text that is not there, or that a sync withholds from every copy of the tree - one sync.neverTransfer, sync.exclude "
            + "or worktrees.root covers, or git ignores - as the registry itself is refused where git ignores it;",
            "An arm reaches others where nothing of its own decides: skipped-not-selected where --arms or its S row leaves it out of a "
            + "leg, or --pending leaves it out, its passed holding; skipped-unavailable, with every arm of its leg, where no worker fits the room left or the path limit; unmeasured "
            + "where ninja's log could not be read around its build, or the report its run wrote could not be read from its file, "
            + "which is no finding about the binary; whatever a guard of its build reaches, as a leg's build does - "
            + "inputs-moved, contended; and whatever a failure that ends its driving comes to, as a leg's does - skipped-tool-missing "
            + "for a tool that is not there, failed for a program that will not start.",
            "one that does not pass - a build that does not pass, a red case, no report, a failing exit, a hang - decides the leg's own verdict",
            "unattributed the run failed and nothing ties that to a case: no report, one that is no JUnit report - its line says why - a "
            + "failing exit whose report names no failing case, or a run past its bound, or silent for defaults.stallSeconds, stopped as hung",
            $"an unknown one is refused ({HarnessExit.UsageError}) - once the registry itself can be swept: its own problems are listed "
            + "first, every one of them, whatever --arms names.",
            "ARMS, below the table, names each arm selected that did not pass, and why;",
            "Each leg is swept in worker copies of its tree, kept in it as <tree>/.mutations/<key>w-<n>, numbered from 1:",
            "directory is named: its first worker adds 22 character(s) to its tree's path whatever the variant is called,",
            "Each is a repository of its own, as every copy a sync makes is, and init keeps .mutations in git and everything made in "
            + $"it out: a sweep in a tree where git does not ignore what it would make there is refused ({HarnessExit.Refused}), naming "
            + "init, with nothing made - asked on the machine that makes the workers, so a host answers for its own copy.",
            "A worker is claimed while a sweep uses it, in <worker>.claim.json beside it in .mutations,",
            "a worker a sweep still running holds keeps its worktree, or its host's copy, until the sweep has ended, and one that "
            + "cannot be removed keeps it as a failure until that is put right - or --force deletes the worktree with that worker in it. "
            + "A directory there under a worker's name that no sync made goes with the worktree, or the host's copy, as everything "
            + "else in it does, and is said. No copy of a tree carries its workers, and a sync into a host's copy never deletes that "
            + "copy's own. Workers an earlier version of this tool kept beside a tree, as <tree>.mutation-<key><mark>-<n>, are no "
            + "workers to a sweep: nothing counts one, and clean --former-workers alone removes them - beside each tree of the "
            + "repository on each machine a selected leg runs on, here and on hosts, and beside each one deleted since, a dry run "
            + "listing each with its size - keeping one a sweep of that version still claims by the claim file beside it. Each holds a .git of its own that git records "
            + "no worktree at, so until then one below the worktrees root is said as any such directory: "
            + "list-worktree warns of it, and delete-orchestrator, and delete-worktree of an orchestrator's directory with or "
            + $"without --force, refuse ({HarnessExit.Refused}) while one is below it.",
            "built whole before it drives an arm: that sync withholds the worker's own build directory itself, "
            + "whatever sync.neverTransfer says, and sync.maxDeleteFraction judges only what mirrors the tree the sweep read. "
            + "Of the tree itself a sweep reads every file and writes none: its workers, and "
            + "the claims on them, are in .mutations, and its run's records in .harness-config, as every run's are.",
            "Its workers are kept in the leg's own tree, as a sweep's are, in a family of their own - <tree>/.mutations/<key>s-<n>,",
            "What the sweep timed across a clock step or a host sleep is said on the leg's line, among why its timings are suspect, and "
            + "changes no verdict: an arm's run and the unmutated run that bounds it, however each ended, and what each of its builds says "
            + "of its own - a worker's rebuilt from clean with it.",
            "a build of the variant coming to the leg's buildSpaceGiB where it declares one, else to what the leg's own build last "
            + "recorded, else to the most any other tree of the repository on its machine recorded of it",
            "each read as its file holds it, less a UTF-8 byte order mark at its start and one line ending at its end,",
            "eight arms, one to each verdict an arm's design can reach on any machine - passed, violated, survived, unattributed and "
            + "failed - with a second that passes as the other red kind, a third whose mutation is coupled across two files and a fourth "
            + "whose mutation is two sites of one file",
            "A mutant that is several places of one file, apart from each other, is one arm: an M row naming a file its arm already "
            + "mutates - its own, or another M row's - adds a text to that file, as many as the mutant has, spelt as the arm's first row "
            + "for the file spells it. Every text of a file is replaced together, as one edit of it: each must stand exactly once in the "
            + "file as the tree holds it - never as another of them left it - and differ from what replaces it, the file is written once "
            + "and put back whole, checked once against the tree's,",
            "an M row naming a file its arm already mutates under another spelling, as the tree's own file system compares their names; "
            + "two texts of one arm that overlap in one file as the tree holds it - one lying across or within the other, or the same "
            + "text cited twice;",
            "it has exactly one B row, no C or G row, and no M row but for its own file, which its paired control rewrites whole.",
        })
        {
            Assert.Contains(text, said, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("upstream of every object", said, StringComparison.Ordinal);
    }

    /// <summary>
    /// The mutations topic says each arm carries the key of what it was judged on, what of the registry, the tree, the
    /// build, the configuration, what a runner reads at run time and the host that is and is not - and that a file read at
    /// run time and named nowhere moves no key - and that a self-test compares its workers' keys, failing where two differ
    /// or one cannot be read, and saying where a worker had none to compare with; the config topic names what runReads is.
    /// </summary>
    [Fact]
    public async Task TheMutationsTopic_SaysWhatAnArmsKeyIsMadeOf_AndThatASelfTestComparesItsWorkersKeys()
    {
        var said = Prose.Words((await CliRunner.RunAsync(["help", "mutations"], TestContext.Current.CancellationToken)).StandardOutput);
        var config = Prose.Words((await CliRunner.RunAsync(["help", "config"], TestContext.Current.CancellationToken)).StandardOutput);

        Assert.Contains("its test binaries' report, and what each runner reads at run time, part of each arm's key ('help mutations')", config, StringComparison.Ordinal);

        foreach (var text in new[]
        {
            "arm, verdict, failure, detail, durationSeconds, worker, cases, declaredCases, reds, declaredReds, records and key; "
            + "under --pending an arm it left out has held - the run and the time of the passed that holds - and an arm it swept "
            + "has pending - the first reason, its code and the words it is said in - and each leg has pendingSweep: how many "
            + "of its arms held, and how many were swept; and each control that did not pass is beneath the leg as controls: "
            + "its runner, verdict, detail, reds, records, and what ctest registered for its red cases - how they were "
            + "matched, and what of the tests it names a sweep does not apply.",
            "An arm's key is the SHA-256 of what it was judged on, read from the first worker made before it drove an arm: the judge's "
            + "revision; the rows the sweep and the judge read of it - never a why, a comment, a line's number, the letter case of its id "
            + "or its S row;",
            "what the sweep's reading of the tree holds of each text it cites and each site it mutates, found as the tree's file system "
            + "compares names;",
            "its build lines, every file of the tree they read, the dependency sources the worker was given, what the worker's configure "
            + "made and what they read outside the tree, the worker's own path told wherever it stands; the compilers CMake identified; ",
            "An arm a part of whose key could not be read has none.",
            "the leg's developer environment; what of the configuration the build, the run and the judge are given; what its runner "
            + "reads at run time, as runReads names it, each file by the path the reading spells, so one renamed by case alone "
            + "moves the key; and the host.",
            "runReads names, for each runner as the A rows name it, the files of the tree its runs read at run time - a data file, a "
            + "golden output, a plugin it opens - each a path, everything below a directory, or **/name at any depth: what the "
            + "sweep's reading of the tree holds of each is part of the key of every arm that runner runs.",
            "A runner given no file, an entry outside the tree, one holding a '*' anywhere but a leading '**/', and one naming the "
            + "whole tree ('.'), are refused; so is a runner no TEST-RED arm's A row names, when the registry is read.",
            "A file a test binary reads at run time that runReads does not name is read by no part of a key: editing it moves "
            + "none, and a recorded passed it would change still holds.",
            "a key two workers read otherwise, or one a worker could not read, fails the leg as violated, naming the arm, the workers and "
            + "the part that differs; a self-test with one worker says it compared its keys with no other's. A self-test's keys are on no "
            + "arm's line.",
        })
        {
            Assert.Contains(text, said, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The mutations topic says what holds and what is pending, and of the record of verdicts: what is recorded and what
    /// is not, where it is and what it keeps, that clean and the deletion of a worktree or a host's copy leave it, and
    /// what a sweep does with one it cannot read; the layout topic names its directory.
    /// </summary>
    [Fact]
    public async Task TheMutationsTopic_SaysWhatIsRecorded_WhereTheRecordIs_AndWhatLeavesIt()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var said = Prose.Words((await CliRunner.RunAsync(["help", "mutations"], cancellationToken)).StandardOutput);
        var layout = Prose.Words((await CliRunner.RunAsync(["help", "layout"], cancellationToken)).StandardOutput);

        foreach (var text in new[]
        {
            "What holds, and what is pending A sweep records each arm's verdict on each leg with what it was judged on. A recorded "
            + "passed holds where what the arm would be judged on now, computed in full on the leg's machine, is what the record "
            + "says; every other arm is pending.",
            "Never an arm its machine did not admit, one the sweep's own stop or a refusal of the run ended, one no worker reached, "
            + "a skipped one, or anything of a self-test",
            $"The record is {HarnessLayout.MutationRecordDirectoryRelative} in the main checkout, whichever tree swept: <leg>.json "
            + $"for each leg, and the tables its entries name in {ArmVerdictRecord.TablesDirectoryName}/<leg>/",
            "a host's copy keeps none, so a leg swept there tells this machine instead - each verdict as a "
            + "line on the host's standard error that this machine reads as the arm ends there and never relays, a sweep "
            + "stopped part way or cut off keeping what it had judged - and a sweep typed by hand in a copy the harness "
            + "synced records nothing, saying so once as a warning: under --pending it then holds nothing, and sweeps every "
            + "selected arm.",
            $"an arm keeps {ArmVerdictRecord.KeptPerArm} keys on a leg, the oldest by its place in the file going first, never by its date.",
            "clean, a deleted worktree and a removed host copy leave it as it was; deleting it is the one way to forget it, and "
            + "every arm is then pending.",
            $"A record that cannot be read, or that a newer {ToolPackage.Id} wrote, is never written: a sweep of that leg says so "
            + "once, as a warning naming the file and on the leg's line, and records none. A sweep this machine "
            + "records names the record once, in its lines and in --json, as a listing does.",
        })
        {
            Assert.Contains(text, said, StringComparison.Ordinal);
        }

        Assert.Contains(
            $"{HarnessLayout.MutationRecordDirectoryRelative}/ ignores itself; each leg's verdicts of its mutation arms, with what "
            + "each was judged on, in the main checkout ('help mutations')",
            layout,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The mutations topic says how a runner is started - by the sweep itself, never through ctest, with the test
    /// settings' working directory and environment and nothing else - what of a test's ctest entry a sweep does not
    /// apply, the red control's line that names it, and that a mutated build is a build of its own.
    /// </summary>
    [Fact]
    public async Task TheMutationsTopic_SaysHowARunnerIsStarted_AndThatAMutatedBuildIsItsOwn()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var said = Prose.Words((await CliRunner.RunAsync(["help", "mutations"], cancellationToken)).StandardOutput);

        foreach (var text in new[]
        {
            "A runner is started by the sweep itself, never by ctest. It is the program the runner's target builds, in the "
            + "worker's build directory, given mutations.reportArgs and nothing else.",
            "Nothing else of the test settings is applied: not the runner, its args, a filter, an exclusion, a label, the "
            + "core count or the success pattern. And nothing of a test's ctest entry is",
            "not ENVIRONMENT or ENVIRONMENT_MODIFICATION; not WORKING_DIRECTORY; not a "
            + "fixture (FIXTURES_REQUIRED, FIXTURES_SETUP, FIXTURES_CLEANUP), DEPENDS, REQUIRED_FILES, RESOURCE_LOCK or "
            + "RUN_SERIAL; not TIMEOUT",
            "Where the leg's tests run "
            + "through ctest, the binary's line then names what ctest registers for the red cases that a sweep does not "
            + "apply, read from ctest's own description (ctest --show-only=json-v1), bounded by two minutes",
            "A sweep starts the binary itself, never through ctest, and runs no fixture.",
            "A mutated build is a build of its own.",
            "and each control that did not pass is beneath the leg as controls: "
            + "its runner, verdict, detail, reds, records, and what ctest registered for its red cases",
        })
        {
            Assert.Contains(text, said, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The mutations topic says what a listing does and refuses, what it reads and what it cannot - so that unmoved here is
    /// never to say an arm holds - and what it exits with, each code read from the code; the command's own help names it.
    /// </summary>
    [Fact]
    public async Task TheMutationsTopic_SaysWhatAListingReads_WhatItCannot_AndWhatItExitsWith()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var said = Prose.Words((await CliRunner.RunAsync(["help", "mutations"], cancellationToken)).StandardOutput);
        var command = Prose.Words((await CliRunner.RunAsync(["check-mutations", "--help"], cancellationToken)).StandardOutput);

        foreach (var text in new[]
        {
            "--list sweeps nothing (see 'What holds, and what is pending'), so it is refused with --pending, --self-test, "
            + $"--force-lock, --use-staged, --here and the hidden --held ({HarnessExit.UsageError}), each in one sentence naming both; and --pending "
            + "is refused with --self-test, of which nothing is ever recorded.",
            "check-mutations --list says, without sweeping, each selected arm's last verdict on each selected leg its S row names, "
            + "and whether anything it was judged on that this machine reads has moved since.",
            "it asks no host, takes no lock, begins no run and writes nothing, and needs nothing only a sweep needs - report "
            + "arguments, a leg built with Ninja, a selection that drives an arm.",
            "the newest entry nothing read here moved for, else the newest",
            "the verdict shown is no passed, it was swept with no key, or the judge's revision, its rows, a text it cites, a site "
            + "it mutates, a file of the tree its targets are built from, a file its runner reads at run time, what of the "
            + "configuration its build, its run or its judge is given, or the host its leg names changed",
            "the row says the first that applies, in that order, and --json every one.",
            "A leg that names no host is read as though swept where its verdict was judged - which host a sweep would place it "
            + "on is not read here - so the environment its build is given is the one that host declares.",
            "Every other arm is unmoved here, which is never to say it holds: a listing reads the rows, the texts, the tree's "
            + "files and the settings, never how a target is built, a file its build makes, the dependency sources it fetched, "
            + "a file outside the tree it reads, the compilers or the developer environment - only a sweep on the leg's machine "
            + "reads those.",
            "A leg that cannot be swept is said once, above the rows, and never refused",
            $"A listing exits {HarnessExit.Success} whatever is pending - it is no gate - and otherwise as a sweep would: "
            + $"{HarnessExit.CommandFailed} where git could not say what a tree ignores, and {LegExit.InputsMoved} where a tree "
            + "changed while it was read.",
        })
        {
            Assert.Contains(text, said, StringComparison.Ordinal);
        }

        Assert.Contains("--list Sweep nothing: say each selected arm's last verdict on each leg", command, StringComparison.Ordinal);
    }

    /// <summary>
    /// The mutations topic says what --pending drives and what it leaves out: the first worker made alone and every key
    /// read before any arm is mutated, an arm held by a recorded passed of its key and how its line says so, why each
    /// other is pending, how many workers it makes, and what a leg with nothing pending is and exits with; and the
    /// option's own description says what it drives.
    /// </summary>
    [Fact]
    public async Task TheMutationsTopic_SaysWhatPendingDrives_WhatItHolds_AndWhatNothingPendingIs()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var said = Prose.Words((await CliRunner.RunAsync(["help", "mutations"], cancellationToken)).StandardOutput);
        var command = Prose.Words((await CliRunner.RunAsync(["check-mutations", "--help"], cancellationToken)).StandardOutput);

        foreach (var text in new[]
        {
            "check-mutations --pending drives, on each leg, only the arms with no recorded passed that holds",
            "the first worker is made alone - claimed, synced, built whole - and the key of every arm is read from its build before any arm is mutated.",
            "An arm whose key is a recorded passed's holds: it is not driven, and is skipped-not-selected on that leg, its detail naming the run whose passed holds and when",
            "in as many workers beyond the first as the pending arms need",
            "how a target is built, a file joined or left what it is built from, a file its build makes, the dependency sources it fetched or a file outside the tree it reads moved, its compiler or its developer environment changed, or what it would be judged on cannot be read now - and, where nothing else says how its key moved, that it moved.",
            "A host sweeping one leg of a run under --pending is given what the record holds of that leg's own arms - each arm's newest entry "
            + "and every recorded passed, as bytes in a hidden option of that leg alone - and decides held and pending "
            + "there, from its own reading of its copy and its own worker; given --pending and no record it holds nothing "
            + "and sweeps every selected arm.",
            "With nothing pending no other worker is made and the leg passes on its own verdict, saying nothing is pending",
            $"A sweep under --pending exits as a sweep does - {HarnessExit.Success} where nothing is pending",
            "or --pending leaves it out, its passed holding",
        })
        {
            Assert.Contains(text, said, StringComparison.Ordinal);
        }

        Assert.Contains("--pending Drive on each leg only the arms with no recorded passed that still holds", command, StringComparison.Ordinal);
    }

    /// <summary>
    /// The tools topic says a minVersion holds every copy a leg could start, the one that meets it raised ahead of the
    /// PATH and a leg with none turned away; that install-missing-tools judges a tool by the host's own survey, a shadowed
    /// one outdated and one it could not establish unknown, installing neither; and that the package manager an install
    /// runs is started where it was found.
    /// </summary>
    [Fact]
    public async Task TheToolsTopic_SaysAMinVersionHoldsEveryCopy_AndAManagerRunsWhereItWasFound()
    {
        var tools = Prose.Words((await CliRunner.RunAsync(["help", "tools"], TestContext.Current.CancellationToken)).StandardOutput);

        foreach (var text in new[]
        {
            "The package manager an install runs is started the same way, where the search found it - brew in /opt/homebrew/bin, "
                + "which no login-free PATH on macOS names - save one run through sudo, which sudo looks up on a PATH of its own.",
            "A minVersion holds every copy of the tool a leg could start: the first one - on the PATH, then in the searched "
                + "directories - whose version meets it is the one a leg there starts",
            "That tool is found where its first copy that meets it is, and that copy's directory goes ahead of the PATH of every "
                + "process every leg on that host starts - one whose environment, or developer environment, sets PATH included",
            "A leg the tool is declared for is turned away where every copy is older, naming each copy, what it said, and - where "
                + "the tool declares an install there - the install-missing-tools command that would install one; where the copy that "
                + "meets it is behind an older one in a "
                + "directory raised for another tool, naming both - only where no order of the raised directories, nor a further "
                + "copy of a tool in the way, keeps both on copies that meet them; and where no copy could be shown to meet it, as "
                + "unknown, 'skipped-unavailable'.",
            "Where the copy that meets it is behind an older one a directory raised for another tool holds, the tool is outdated, "
                + "naming both, and nothing is installed: an install would not change which copy a leg starts.",
            "Where none met it and a copy's version could not be read, or a place a copy could be could not be looked in, the "
                + "tool is unknown and nothing is installed: nothing says it is behind.",
            "Each host is asked about its tools by its own survey, the one 'legs' runs there, so a tool is installed exactly "
                + "where a leg would be turned away over it, and never a second time where a leg there finds it.",
            "a host whose survey cannot run - one a dry run finds with no SDK yet - has every tool unknown, saying why, and "
                + "nothing is installed there.",
            "install-missing-tools asks the same survey, on the host's own PATH and on the PATH each developer environment its "
                + "legs start in sets up there; a tool the survey could not establish is unknown rather than missing, and nothing "
                + "is installed for it.",
            "A minVersion is written major.minor.patch and needs a probe to read versions with; brew installs and updates with "
                + "'brew install'",
            "A leg in a developer environment holds the copy that environment's PATH names to it, once set up.",
            "A leg turned away for a missing program, or for one below its minVersion, is 'skipped-tool-missing'",
        })
        {
            Assert.Contains(text, tools, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Help says what a command exits with where no selected leg can run, wherever it speaks of it: 1 of a build, a test
    /// and a run, and of a sweep incomplete, whose 1 is an arm violated - each code read from the code.
    /// </summary>
    [Fact]
    public async Task Help_SaysWhatASweepGives_WhereNoSelectedLegCanRun()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var mutations = Prose.Words((await CliRunner.RunAsync(["help", "mutations"], cancellationToken)).StandardOutput);
        var tools = Prose.Words((await CliRunner.RunAsync(["help", "tools"], cancellationToken)).StandardOutput);

        Assert.Contains(
            $"A sweep none of whose selected legs can run is incomplete, exit {MutationService.NothingRuns}, saying why each cannot - never the "
            + $"{LegsExit.Unavailable} a build, a test and a run give there, which of a sweep is an arm violated.",
            mutations,
            StringComparison.Ordinal);
        Assert.Contains(
            $"and the run is incomplete, exit {HarnessExit.Incomplete} - or exit {LegsExit.Unavailable} when no selected leg can run at all, save of "
            + $"check-mutations, which stays {MutationService.NothingRuns}: its {LegsExit.Unavailable} is an arm violated.",
            tools,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The admission topic says a waiting leg says where it stands at least every five minutes, the minutes read from
    /// the code, and that a poll or a settle longer than that keeps to it.
    /// </summary>
    [Fact]
    public async Task TheAdmissionTopic_SaysAWaitNeverGoesSilent_WhateverThePollAndTheSettle()
    {
        var said = Prose.Words((await CliRunner.RunAsync(["help", "admission"], TestContext.Current.CancellationToken)).StandardOutput);

        Assert.Contains(
            $"It says so again, as it reads then and with how long it has waited, at least every {LegAdmission.SaidAgainEvery.TotalMinutes:0} minutes - "
            + "a poll longer than that is cut at it, and a settle longer than that waited whole in pieces of it - so a long wait never goes silent.",
            said,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The admission topic says a heavy leg's build is held to minFreeGiB - read every few seconds, the seconds read from
    /// the code, and again as a build or a write of its fails - and stopped under it, writing nothing where it starts under
    /// it; that WSL's page cache is dropped before a wait on the
    /// memory and as a WSL leg ends, a drop that could not be made said with why; that a holder's line never names its
    /// machine; and that a wait with only legs of its own run ahead of it does not count.
    /// </summary>
    [Fact]
    public async Task TheAdmissionTopic_SaysTheFloor_TheCacheDrop_TheHolderUnnamed_AndTheOwnRunsWait()
    {
        var said = Prose.Words((await CliRunner.RunAsync(["help", "admission"], TestContext.Current.CancellationToken)).StandardOutput);

        foreach (var expected in new[]
        {
            $"While a heavy leg builds, each filesystem its build fills is read again every {RoomFloorWatch.Every.TotalSeconds:0} seconds, and the "
                + $"build is stopped once one has less free than minFreeGiB: stopped, exit {HarnessExit.Incomplete}, naming what was free and where, "
                + "and what it built left for clean - and so is a build that failed with one under it, read again as it failed, since a full disk "
                + "says nothing of its code, and one whose own write failed then - a phase's log, its record. A build under it as it starts writes "
                + "nothing, its record included, and a record it has no room left for is said, the one it wrote as it began standing.",
            "So a leg about to wait on the memory has that cache dropped first, as root (sync, then drop_caches) in a running distribution a WSL "
                + "host of its repository reaches - at most once a minute, whichever leg asks - and reads the memory again once what was dropped "
                + "has come back, a minute later at the most, its line saying how much was dropped and the memory before and after, and goes on "
                + "from that reading as from any other.",
            "A WSL leg sent from a machine that admits drops it as it ends, without waiting, its line saying so - or that WSL listed none of its "
                + "distributions running, though the leg had just run in one.",
            "a drop that could not be made is said once - a WSL host whose item cannot be read, where no other reaches one, or a list of the "
                + "distributions running that cannot be read, among the reasons; that list is read in UTF-16 too, which an older wsl.exe writes "
                + "whatever WSL_UTF8 says.",
            "the distribution reaches through its mount there: /mnt/c, as /proc/mounts lists it by the drive, or by the path its options name "
                + "where an older WSL lists what it mounts as drvfs.",
            "process, run, and since when it asked, never the machine's name, which only the record keeps",
            "A wait with only legs of its own run ahead of it - holding slots, or in line first: a command's WSL leg behind its Windows legs, "
                + "asked for at once - is certain to end and does not count: its line says they are its own, and a refusal after it says how much "
                + "of the wait did not count. Another command's leg in line ahead makes it count, whoever holds the slots. On an ssh host, the "
                + "legs one command sends there ask under that command's run.",
        })
        {
            Assert.Contains(expected, said, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The verdicts topic says a heavy leg's build stopped for room is stopped, and that a shared tool working in a build
    /// directory of another tree of the repository is named as that tree's leg's, only what no tree accounts for being
    /// nobody's known.
    /// </summary>
    [Fact]
    public async Task TheVerdictsTopic_SaysABuildStoppedForRoomIsStopped_AndWhoseAToolInAnotherTreeIs()
    {
        var said = Prose.Words((await CliRunner.RunAsync(["help", "verdicts"], TestContext.Current.CancellationToken)).StandardOutput);

        Assert.Contains(
            "A heavy leg's build stopped for leaving less room than its machine's minFreeGiB is stopped too ('help admission').",
            said,
            StringComparison.Ordinal);
        Assert.Contains(
            "and one working in a build directory of another tree of the repository on that machine, a worktree's, an agent's or the main "
            + "checkout's, or a copy one of them keeps in its .legs, as that tree's leg's: 'worktree o1/xa's leg 'linux-debug'', or "
            + "'worktree o1/xa's leg copy's leg 'linux-debug''. Only what no tree there accounts for is nobody's known, and where the "
            + "other trees could not be listed the line says so.",
            said,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The space topic says a leg's need, where its own tree's copy recorded none, is the most any other copy of the
    /// repository on that host recorded, named in the line - its example the line as a leg says it.
    /// </summary>
    [Fact]
    public async Task TheSpaceTopic_SaysANeedIsTheMostAnyOtherCopyRecorded_NamingWhose()
    {
        var said = Prose.Words((await CliRunner.RunAsync(["help", "space"], TestContext.Current.CancellationToken)).StandardOutput);

        Assert.Contains(
            "in this tree's copy, or else the most any other copy of the repository on that host recorded - the main checkout's, a "
            + "worktree's, an agent's or a plain one, or a leg copy of any of them, named in the line - less what the directory already "
            + "holds.",
            said,
            StringComparison.Ordinal);
        Assert.Contains(
            "ssh vps: this leg needs ~11.4 GiB, what worktree o1/xa's copy of the same variant came to there; 3.2 GiB free on '/'",
            said,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExitCodeTopic_DocumentsEverySharedExitCode()
    {
        var result = await CliRunner.RunAsync(
            ["help", "exit-codes"],
            TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);

        // Generated from the constants, so a new exit code cannot ship undocumented.
        foreach (var description in HarnessExit.All)
        {
            Assert.Contains(description.Name, result.StandardOutput, StringComparison.Ordinal);
            Assert.Contains(
                description.Code.ToString(CultureInfo.InvariantCulture),
                result.StandardOutput,
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ConfigTopic_DocumentsCoresAndParallelLegs_WithTheRealDefault()
    {
        var result = await CliRunner.RunAsync(["help", "config"], TestContext.Current.CancellationToken);

        foreach (var setting in new[] { "buildCores", "testCores", "maxParallelLegs", "sanitizer", "hosts", "emulators", "developerEnvironments" })
        {
            Assert.Contains(setting, result.StandardOutput, StringComparison.Ordinal);
        }

        Assert.Contains(
            $"({HarnessDefaults.DefaultCores.ToString(CultureInfo.InvariantCulture)} each)",
            result.StandardOutput,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The config topic says a leg's inputs are watched only as deep as they lie, and the directories above them for
    /// the names in them alone, so a build in another tree below the worktrees root is never heard: on Linux however
    /// many directories they lie in, through one inotify instance for the process; on Windows and macOS up to the number
    /// of directories .NET's watcher takes, read from the code, past which some are watched all the way down - and where
    /// inotify cannot be called directly, that .NET's watcher stands in, said once.
    /// </summary>
    [Fact]
    public async Task ConfigTopic_SaysInputsAreWatchedOnlyAsDeepAsTheyLie()
    {
        var result = await CliRunner.RunAsync(["help", "config"], TestContext.Current.CancellationToken);

        Assert.Contains(
            "Its inputs are watched only as deep as they lie - each directory holding one, for its own files, and each directory "
                + "above one, the root among them, for the names of the directories in it alone - so a build in another tree below the "
                + "worktrees root, which init keeps in git by a placeholder, is never heard. On Linux every such directory is watched, "
                + "however many there are, through one inotify instance for the whole process. On Windows and "
                + $"macOS a watch takes at most {FileSystemWatcherKind.MostDirectoriesWatched} directories besides the root's own, those "
                + "holding inputs and those above them alike, so past them the top-level directories watching the most are watched all "
                + "the way down, until the rest fit, and a tree whose inputs lie in more top-level directories than that is watched "
                + "whole. Where inotify cannot be called directly on Linux, .NET's "
                + "watcher stands in, as on Windows and macOS, each directory taking an inotify instance of its own, and the process "
                + "says so once.",
            Prose.Words(result.StandardOutput),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The config topic says what a directory that holds inputs or lies above some going, arriving or changing its name
    /// counts - every input in or below it, though it be back when the work ends - and that a leg whose inputs cannot be
    /// watched runs none of its work.
    /// </summary>
    [Fact]
    public async Task ConfigTopic_SaysWhatADirectoryThatWentCounts_AndThatAnUnwatchedLegRunsNothing()
    {
        var said = Prose.Words((await CliRunner.RunAsync(["help", "config"], TestContext.Current.CancellationToken)).StandardOutput);

        Assert.Contains(
            "and the process says so once. A directory that holds inputs, or lies above some, going, arriving or changing its name "
                + "counts every input in or below it, under both names of a rename, though it be back when the work ends; one merely "
                + "written to counts none. "
                + "A leg whose inputs cannot be watched at all runs none of its work, and ends unmeasured at once, saying why ('help "
                + "verdicts').",
            said,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The verdicts topic says a leg whose inputs cannot be watched is unmeasured with none of its work run, in the words
    /// its line opens with, read from the code; and that a watch lost while the work ran ends it unmeasured as it ends.
    /// </summary>
    [Fact]
    public async Task VerdictsTopic_SaysALegWhoseInputsCannotBeWatched_IsUnmeasuredWithNoneOfItsWorkRun()
    {
        var said = Prose.Words((await CliRunner.RunAsync(["help", "verdicts"], TestContext.Current.CancellationToken)).StandardOutput);

        Assert.Contains(
            "A leg whose inputs cannot be watched - the machine refusing the watch as its work was to begin - is unmeasured with no "
                + $"phase of it run: '{InputsUnwatchedException.Opening.TrimEnd()}' and the machine's reason. Run first and called unmeasured "
                + "after, the whole of the work was spent to learn what was known before it began. A watch that started and was lost "
                + "while the work ran still leaves it unmeasured, as the work ends.",
            said,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The remedy for a leg whose inputs could not be watched is the machine's, never the tree's settling: the verdicts
    /// topic says so where it says why such a leg ran nothing, and again among the verdicts' remedies, and the exit codes
    /// topic beside the code it exits with.
    /// </summary>
    [Fact]
    public async Task TheRemedyForInputsThatCouldNotBeWatched_IsTheMachines_WhereverExitThreesRemedyIsSaid()
    {
        var verdicts = (await CliRunner.RunAsync(["help", "verdicts"], TestContext.Current.CancellationToken)).StandardOutput;
        var codes = (await CliRunner.RunAsync(["help", "exit-codes"], TestContext.Current.CancellationToken)).StandardOutput;

        Assert.Contains(
            "Its remedy is the machine's, not the tree's: free or raise what the reason names - end a program holding this user's "
                + "inotify instances or watches, or raise the limit - or put back the tree the watch was to be put on, then run again.",
            Prose.Words(verdicts),
            StringComparison.Ordinal);
        Assert.Contains(
            "  3  inputs-moved, unmeasured   Let the tree settle, then run again; where its" + Environment.NewLine
                + "                                inputs could not be watched, free or raise what" + Environment.NewLine
                + "                                the machine's reason names, or restore the tree" + Environment.NewLine,
            verdicts,
            StringComparison.Ordinal);
        Assert.Contains(
            $"    {LegExit.InputsMoved,3}  inputs-moved or unmeasured: let the tree settle, then run again; where the inputs" + Environment.NewLine
                + "         could not be watched, free or raise what the machine's reason names, or restore the tree" + Environment.NewLine,
            codes,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The verdicts topic names the two inotify limits a watch on Linux can meet, and says that the leg is said with the
    /// processes holding them and waits for neither.
    /// </summary>
    [Fact]
    public async Task VerdictsTopic_NamesTheTwoInotifyLimits_AndThatALegWaitsForNeither()
    {
        var said = Prose.Words((await CliRunner.RunAsync(["help", "verdicts"], TestContext.Current.CancellationToken)).StandardOutput);

        Assert.Contains(
            "On Linux the machine's reason can be this user's inotify instances all taken (fs.inotify.max_user_instances) - "
                + "a process of this tool holds one, however many legs it runs - or its inotify watches run out "
                + "(fs.inotify.max_user_watches), one for each directory the work watches - those its inputs lie in and those "
                + "above them; each is said with the limit and the "
                + "processes /proc shows holding it, the most first. The leg does not wait for either: what holds them is "
                + "another program, whose end nothing here can read.",
            said,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The config topic says a test count that differs is marked on its own, never as a timing, and
    /// how a set that differs on purpose is declared.
    /// </summary>
    [Fact]
    public async Task ConfigTopic_SaysATestCountThatDiffers_IsNoTimingMark_AndHowATestSetIsNamed()
    {
        var result = await CliRunner.RunAsync(["help", "config"], TestContext.Current.CancellationToken);

        foreach (var text in new[]
        {
            "and test set is marked on its own - 'test count differs', and testCountDiffers and",
            "testCountNote in --json - never as a timing, and never changes a verdict either. A",
            "\"windows\": { \"testSet\": \"windows\" }",
            "A leg's os names its section however it is spelled - Windows as windows - as every",
            "per-system setting is read.",
        })
        {
            Assert.Contains(text, result.StandardOutput, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The config topic says how test --filter, --exclude and --label reach a runner, what each is
    /// for ctest, that a label can now be chosen as well as left out, how several exclusions reach
    /// a runner that would not leave out each of them given apart, what a leg on a host's copy
    /// leaves out beside them, that a selection ctest could only find empty is refused before the
    /// build and how its values are judged, and which invocations are refused when the file is read.
    /// </summary>
    [Fact]
    public async Task ConfigTopic_SaysHowTheTestSelectionReachesTheRunner()
    {
        var result = await CliRunner.RunAsync(["help", "config"], TestContext.Current.CancellationToken);

        foreach (var text in new[]
        {
            "filterArg, excludeArg and labelArg, so one set of options serves every runner. For",
            "ctest, '-R' chooses tests by name, '-L' chooses them by label and '-LE' leaves a label",
            "apart - ctest leaves out only what every -LE matches - declares excludeJoin, and several",
            "one the sync made, or one it took over. Each sync makes its HEAD name the commit this",
            "tree is at, so what a step asks git about HEAD - which commit, what a file held there -",
            "untracked as added, and one git stores converted here - line endings, a clean filter -",
            "pack of git's own, less what the copy holds, and nothing crosses while its HEAD names",
            "about a commit behind it that the copy lacks fails there, in git's own words.",
            "\"sync\": { \"history\": \"full\" } gives the copy every commit behind it too, once, and",
            "each later one as it is made; a partial clone then has git fetch what it left out. A",
            "copy the sync took over has its HEAD moved where it names another commit - detached,",
            "reads it as a shallow repository from then on, said too, which 'git fetch --unshallow'",
            "sync makes its index hold the files it carried, so a build there fingerprints its",
            "                 how much git history a copy is given: 'head' (the commit HEAD names,",
            "remoteExcludes are given to every leg that works in a copy - a host's, or its own in",
            "reads there unless the args or a preset choose tests by name too - or beside a test",
            "preset that takes a union or cannot be read. So is a selection ctest could only find",
            "another leaves out by the same part of a test - its name, or a label - where either is",
            "only -LE and no test preset they name adds one. Values are judged as written: spelled",
            "has been built; where it still finds none, the detail of a leg that did not pass says",
            "so, unless --no-tests=ignore keeps ctest quiet. A label beside labels the args or a",
            "When the file is read, ctest is also refused an invocation that would pass an option",
            "'|', which ctest would read as part of one pattern that leaves out nothing.",
        })
        {
            Assert.Contains(text, result.StandardOutput, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The config topic says when a combination's build directory is kept and when it starts from
    /// clean, and what rebuildableFormats decides: a consumer found both only by reading the source.
    /// </summary>
    [Fact]
    public async Task ConfigTopic_SaysWhenABuildDirectoryIsKept_AndWhatCountsAsAnInput()
    {
        var result = await CliRunner.RunAsync(["help", "config"], TestContext.Current.CancellationToken);

        foreach (var text in new[]
        {
            "A combination's directory is kept between builds, and its build system decides",
            "  - a compiler CMake identified is not what is at its path now: CMake identifies a",
            "  - an input that changed since that build began is dated no later than the newest",
            "  - nothing can say: no record of what it was built from, the files git tracks",
            "DEPENDS is not remade. A project's rebuildableFormats says which files are inputs,",
        })
        {
            Assert.Contains(text, result.StandardOutput, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The verdicts topic says when a leg on a copy is inputs-moved with nothing run - the tree moving while it was read,
    /// a file to carry changing after, another command's sync of the copy in between or the copy gone or part made by
    /// then, and under --use-staged a copy part made, absent or unmarked - what its line and its --json say of the tree it
    /// tested, and the remedy a leg that worked in its tree itself is named, and where it is not.
    /// </summary>
    [Fact]
    public async Task VerdictsTopic_SaysWhenALegOnACopyIsInputsMoved_AndWhichTreeItTested()
    {
        var result = await CliRunner.RunAsync(["help", "verdicts"], TestContext.Current.CancellationToken);

        Assert.Contains(
            "A leg on a copy of its tree - a host's, or one of its own in the tree's .legs - is inputs-moved with nothing of it "
                + "run where the copy cannot be the tree the run read: a file written, appearing or going while the tree was read, "
                + "named; a file to carry changed after it was read; another command synced the copy again between this run's sync "
                + "and the leg taking it, naming that run, or by then the copy is not there, is part made, or holds no copy this "
                + "tool finished; or, under --use-staged, the copy is part made, is not there, or holds no copy this tool made. Each "
                + "leg on a copy says which tree it tested with its verdict - 'tree read at <commit>', with the uncommitted changes "
                + "the reading carried, or under --use-staged 'tree staged at <commit> by run <id>' as the copy records it - and "
                + "--json's 'tree' holds its path, the tree it is a copy of, and the commit, the count and the run. A leg that "
                + "worked in its tree itself and is inputs-moved names the remedy after its detail, where its command could have run "
                + "it on a copy: the leg's localCopy, defaults.localCopy, or --local-copy ('help legs'). A leg another machine sent "
                + "here, or one typed in a copy a sync made, is named none: it works in a copy already, and never in one of its own.",
            Prose.Words(result.StandardOutput),
            StringComparison.Ordinal);
    }

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
    /// The legs topic says where a leg works - its tree, its host's copy, or its own in the tree's .legs - and the order
    /// of precedence in one sentence: the command's option, a runner's writesTree, the leg's own key, the default, the
    /// tree.
    /// </summary>
    [Fact]
    public async Task LegsTopic_SaysWhereALegWorks_AndTheOrderOfPrecedence()
    {
        var result = await CliRunner.RunAsync(["help", "legs"], TestContext.Current.CancellationToken);

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

    /// <summary>
    /// The runners topic says what a writesTree runner does: its legs work in place whatever localCopy says, a leg a host
    /// would run is refused before any host is measured, and so is --local-copy.
    /// </summary>
    [Fact]
    public async Task RunnersTopic_SaysWhatARunnerThatWritesTheTreeDoes()
    {
        var result = await CliRunner.RunAsync(["help", "runners"], TestContext.Current.CancellationToken);

        Assert.Contains(
            "A runner whose steps change files of the tree says writesTree: its legs work in their trees themselves, "
                + "whatever the leg's own localCopy or defaults.localCopy says ('help legs'), for what it wrote in a copy "
                + "the next sync would make the tree again. A leg a host would run is refused before any host is measured, "
                + "and so is --local-copy on the command.",
            Prose.Words(result.StandardOutput),
            StringComparison.Ordinal);
        Assert.Contains(
            "A run check's runner runs within the leg's own work, so it is held to the same: one that writes the tree is "
                + "refused wherever the leg it checks would work in a copy, --local-copy among them.",
            Prose.Words(result.StandardOutput),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The layout topic says how a leg's records are named - the logs of line, and records and logs in --json - and how
    /// the run a host ran a leg under is said: once, by the dispatcher, and as hostRunId.
    /// </summary>
    [Fact]
    public async Task LayoutTopic_SaysHowALegsRecordsAndAHostsRunAreNamed()
    {
        var result = await CliRunner.RunAsync(["help", "layout"], TestContext.Current.CancellationToken);

        Assert.Contains(
            "Each leg that kept records has a line of its own naming its directory and the logs in it - 'logs of <leg>[ on <host>]' - as its verdict is said and in the run's closing lines, and as records and the phase logs as logs in --json, a host's paths with its home written as ~",
            Prose.Words(result.StandardOutput),
            StringComparison.Ordinal);
        Assert.Contains(
            "A host runs a leg under a run of its own: the dispatcher names it once, '<leg>: run <id> on <host>', never relaying the host's own line, and the leg's --json holds it as hostRunId.",
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

    /// <summary>
    /// Each command's own description says what it does to the copies a tree keeps, and list-orchestrator's --json the
    /// listing it prints - never the outcome of an --apply it does not have.
    /// </summary>
    [Theory]
    [InlineData("list-orchestrator", "--json Print the listing as one JSON document.")]
    [InlineData("fold-agent", "--json Print the outcome as one JSON document, the whole of standard output:")]
    [InlineData("delete-worktree", "and the copies kept in each - the mutation workers in .mutations, and on this machine the leg copies in .legs;")]
    [InlineData("clean", "and the copies of its tree it works in on this machine (.legs), wherever the leg runs,")]
    [InlineData("clean", "remove nothing, and exit as the clean would: 13 where a run holds the leg, a sweep its workers or a run its copy.")]
    public async Task EachCommandsOwnHelp_SaysWhatItDoesToCopies_AndWhatItsJsonPrints(string command, string expected)
    {
        var result = await CliRunner.RunAsync([command, "--help"], TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(expected, Prose.Words(result.StandardOutput), StringComparison.Ordinal);
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
    /// The runners topic says that a run check whose own runner's inputs cannot be watched was not run, and confirms
    /// nothing: the failure it was to excuse stays genuine.
    /// </summary>
    [Fact]
    public async Task RunnersTopic_SaysARunCheckWhoseWatchCannotStart_ConfirmsNothing()
    {
        var result = await CliRunner.RunAsync(["help", "runners"], TestContext.Current.CancellationToken);

        Assert.Contains(
            "A check whose own runner's watch cannot start was not run, and confirms nothing: the failure stays genuine, and the run fails.",
            Prose.Words(result.StandardOutput),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The runners topic names a step's successPattern, which line it is matched against, and which of
    /// that line's streams.
    /// </summary>
    [Fact]
    public async Task RunnersTopic_SaysWhatAStepsSuccessPatternIsMatchedAgainst()
    {
        var result = await CliRunner.RunAsync(["help", "runners"], TestContext.Current.CancellationToken);
        var words = Prose.Words(result.StandardOutput);

        foreach (var text in new[]
        {
            "successPattern: <regular expression> what its last run line must print, besides exiting 0",
            "matched against what that line printed on standard output and standard error, a line of output at a time - "
                + "^ and $ its start and end, whether it ends in CRLF or LF, as its log keeps it - after secrets are redacted.",
        })
        {
            Assert.Contains(text, words, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The runners topic lists every key a runner and an action file take, each indented to the depth
    /// it is nested at and with what it does, read from what the files are read with: a key cannot be
    /// taken without being listed here.
    /// </summary>
    [Fact]
    public async Task RunnersTopic_ListsEveryKeyARunnerAndAnActionFileTake()
    {
        var result = await CliRunner.RunAsync(["help", "runners"], TestContext.Current.CancellationToken);
        var lines = result.StandardOutput.ReplaceLineEndings("\n").Split('\n');

        foreach (var (key, depth) in Listed(ConfigKeys.Of<RunnerConfig>(), 0).Concat(Listed(ActionFileKeys.File, 0)))
        {
            var pattern = $"^ {{{2 + (2 * depth)}}}{Regex.Escape(key.Name)} +{Regex.Escape(key.Meaning)}{(key.Required ? "; required" : string.Empty)}$";

            Assert.True(lines.Any(line => Regex.IsMatch(line, pattern)), $"'{key.Name}' is not listed as: {pattern}");
        }
    }

    /// <summary>
    /// The runners topic says which stall bound a phase or step runs under, and where the order its
    /// environment is built in is given.
    /// </summary>
    [Fact]
    public async Task RunnersTopic_SaysWhichStallBoundApplies()
    {
        var result = await CliRunner.RunAsync(["help", "runners"], TestContext.Current.CancellationToken);
        var words = Prose.Words(result.StandardOutput);

        foreach (var text in new[]
        {
            "The stall bound a phase or step runs under is its own stallSeconds, else its runner's, else "
                + "defaults.stallSeconds: the first of them set, where 0 means none, as does setting none.",
            "'help config' gives the order its environment is built in.",
        })
        {
            Assert.Contains(text, words, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The config topic names the repository's own stall bound, and every layer a runner's phase or
    /// step is given in order, its action's inputs among them.
    /// </summary>
    [Fact]
    public async Task ConfigTopic_NamesTheDefaultStallBound_AndEveryLayerOfARunnersEnvironment()
    {
        var result = await CliRunner.RunAsync(["help", "config"], TestContext.Current.CancellationToken);
        var words = Prose.Words(result.StandardOutput);

        foreach (var text in new[]
        {
            "stallSeconds, the stall bound nothing more specific replaces ('help runners')",
            "run host env, developer environment, then the runner's values and secrets, its env, the action's "
                + $"inputs as INPUT_<NAME> where a {PredefinedActions.ReadInputs} step read them, the phase's or step's",
        })
        {
            Assert.Contains(text, words, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Wherever the runners topic shows how a step key is written, what it does is what 'Every key'
    /// says of it: one meaning, from one place, however many times the key is shown.
    /// </summary>
    [Fact]
    public async Task RunnersTopic_SaysWhatAStepKeyDoes_InTheWordsItsListUses()
    {
        var result = await CliRunner.RunAsync(["help", "runners"], TestContext.Current.CancellationToken);
        var meanings = ActionFileKeys.Step.ToDictionary(key => key.Name, key => key.Meaning, StringComparer.Ordinal);
        var shown = result.StandardOutput.ReplaceLineEndings("\n").Split('\n')
            .Select(line => SpeltKeyLine().Match(line))
            .Where(line => line.Success && meanings.ContainsKey(line.Groups["key"].Value))
            .ToList();

        Assert.NotEmpty(shown);

        foreach (var line in shown)
        {
            Assert.Equal(meanings[line.Groups["key"].Value], line.Groups["meaning"].Value);
        }
    }

    /// <summary>
    /// The runners topic says a step that performs a predefined action is refused with each key only a
    /// run block reads, naming every one of them from the list the parser refuses them by.
    /// </summary>
    [Fact]
    public async Task RunnersTopic_SaysAStepThatUsesAPredefinedAction_TakesNoKeyOnlyARunBlockReads()
    {
        var result = await CliRunner.RunAsync(["help", "runners"], TestContext.Current.CancellationToken);
        var words = Prose.Words(result.StandardOutput);

        Assert.Contains(
            $"A step that uses {string.Join(" or ", PredefinedActions.All)} runs no program, so it takes none of the keys "
                + $"only a run block reads, and is refused with any of them: '{string.Join("', '", KeyDescription.Names(ActionFileKeys.RunBlock))}'.",
            words,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The runners topic says what a step's outputs are relative to, and what its name names, by the
    /// name a run line uses for that directory, {stepBuild}: 'its own directory' read as the action's,
    /// which workingDirectoryRoot names.
    /// </summary>
    [Fact]
    public async Task RunnersTopic_SaysAStepsOutputsAreRelativeToItsBuildDirectory()
    {
        var result = await CliRunner.RunAsync(["help", "runners"], TestContext.Current.CancellationToken);
        var words = Prose.Words(result.StandardOutput);

        Assert.Contains("outputs: [<path>, ...] what it writes, relative to its build directory, {stepBuild}", words, StringComparison.Ordinal);
        Assert.Contains("name names its log file and its build directory, {stepBuild}; required", words, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WorktreesTopic_QuotesTheLimitsFromTheCode()
    {
        var result = await CliRunner.RunAsync(["help", "worktrees"], TestContext.Current.CancellationToken);

        Assert.Contains(
            WorktreeSettings.DefaultMaxNameLength.ToString(CultureInfo.InvariantCulture),
            result.StandardOutput,
            StringComparison.Ordinal);
        Assert.Contains(
            HostPlatform.WindowsMaxPath.ToString(CultureInfo.InvariantCulture),
            result.StandardOutput,
            StringComparison.Ordinal);

        foreach (var setting in new[] { "worktrees.maxNameLength", "worktrees.pathBudgetReserve", "worktrees.pathLimit" })
        {
            Assert.Contains(setting, result.StandardOutput, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The runners topic says what runOn takes - the systems quoted from the code - where a leg of
    /// another system says it left a step out, and that a leg left with no step is refused.
    /// </summary>
    [Fact]
    public async Task RunnersTopic_SaysWhatRunOnTakes_AndWhatALegLeftWithoutAStepGets()
    {
        var result = await CliRunner.RunAsync(["help", "runners"], TestContext.Current.CancellationToken);

        foreach (var text in new[] { "runOn: [<system>, ...]", $"of {string.Join(", ", PlatformNames.OperatingSystems)}; absent, all", "skippedSteps", "refused before anything starts" })
        {
            Assert.Contains(text, result.StandardOutput, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The runners topic says where an input's value comes from, in which order, that a secret never
    /// gives it one, what becomes of one with none, what --input refuses, and that a secret does not
    /// go there.
    /// </summary>
    [Fact]
    public async Task RunnersTopic_SaysWhereAnInputsValueComesFrom()
    {
        var result = await CliRunner.RunAsync(["help", "runners"], TestContext.Current.CancellationToken);
        var words = Prose.Words(result.StandardOutput);

        foreach (var text in new[]
        {
            "resolved from 'run --input <name>=<value>' first, .harness-config/runner/.env second and each "
                + "input's own 'default' last; .secrets never gives one a value.",
            "A required input with none of them is refused before the first step runs, and so is a step naming "
                + "one that is not required and has none, naming the input and how it can have one.",
            "default its value when neither --input nor the runner's .env gives one",
            "--input takes one name=value each time it is given",
            "an empty value and a name given twice",
            "a secret stays in",
        })
        {
            Assert.Contains(text, words, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The runners topic says what a name in braces is made of, that the runner's .env values are
    /// names too, that one this leg has nothing for is refused before the first program starts, and
    /// which names an input or a value may not have.
    /// </summary>
    [Fact]
    public async Task RunnersTopic_SaysWhatANameIs_AndWhichNamesAreRefused()
    {
        var result = await CliRunner.RunAsync(["help", "runners"], TestContext.Current.CancellationToken);
        var words = Prose.Words(result.StandardOutput);

        foreach (var text in new[]
        {
            "refused over the whole file before the first program starts, as is one this leg has nothing for.",
            $"A name {LegPathNames.NameRule}.",
            "<value name> any value the runner's .env holds, by its name",
            "An input or a .env value named like a name above is refused, since a run line naming it would get "
                + "this tool's value while the environment held its own; so is an input whose name a run line "
                + "cannot write.",
        })
        {
            Assert.Contains(text, words, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The runners topic names the two compiler names with what each is filled in with; says a line may start with one,
    /// that a step naming one builds its leg first, when one is refused - before anything starts, and once the leg is
    /// built - and that a test invocation may name one too. The tools and admission topics count a step naming one among
    /// those that build their leg.
    /// </summary>
    [Fact]
    public async Task RunnersTopic_SaysWhatACompilersNameIs_WhereItMayStand_AndWhenItIsRefused()
    {
        var token = TestContext.Current.CancellationToken;
        var written = (await CliRunner.RunAsync(["help", "runners"], token)).StandardOutput;
        var runners = Prose.Words(written);

        foreach (var text in new[]
        {
            $"{{{LegPathNames.CompilerC}}} {{{LegPathNames.CompilerCxx}}} the C and the C++ compiler the leg's build identified: the whole path of the program CMake built with, as this machine spells it",
            "The first token must be a program declared under tools, a path in the repository, or the name of one of the leg's compilers alone ({compiler_C}, below); anything else is refused before a single step runs.",
            "starts it, a step naming {product}, {buildDir} or a compiler has its leg built first (below).",
            "A step whose run line or workingDirectory names {buildDir}, {product}, {compiler_C} or {compiler_CXX} reads what the build made or identified, so every leg of a run that runs it is built first",
            "{compiler_C} and {compiler_CXX} name what built the leg, never what a toolchain declares: the program CMake's own record in the leg's build directory names for that language - gcc on one leg and cl on another, from one run line.",
            "A line may start with one, written alone: the leg's own compiler may run though nothing declares it under tools.",
            "A leg whose project CMake does not build is refused before anything starts, naming each leg and step, since no build of it identifies a compiler;",
            "a name the leg's build has nothing for is refused once the leg is built, before its first step, saying which case it is - no compiler identified for that language, a record of one that could not be read or that names no program, or one the build runs with words after it, a launcher such as ccache given its compiler or a compiler given options: the name is a program alone, so the program and the words are named and neither is filled in.",
            "A test invocation's args, coresArgs and workingDirectory may name one too: the check made before the build lets the name stand, and it is filled in, or refused, once the leg is built - under test --no-build, from what the build already in its directory identified.",
        })
        {
            Assert.Contains(text, runners, StringComparison.Ordinal);
        }

        // The rows of its tables of names are headed by the names this tool fills in, each of them and no other: one
        // added to the code cannot be left out, and one the code dropped cannot stay.
        var rows = written
            .Split('\n')
            .Select(line => Regex.Match(line, @"^  ((?:\{[A-Za-z_]+\} ?)+) +\S"))
            .Where(row => row.Success)
            .SelectMany(row => Regex.Matches(row.Groups[1].Value, @"\{([A-Za-z_]+)\}").Select(name => name.Groups[1].Value));

        Assert.Equal(LegPathNames.All.Order(StringComparer.Ordinal), rows.Order(StringComparer.Ordinal));

        Assert.Contains(
            "runs a step or phase naming {product}, {buildDir} or a compiler on that leg's system",
            Prose.Words((await CliRunner.RunAsync(["help", "tools"], token)).StandardOutput),
            StringComparison.Ordinal);
        Assert.Contains(
            "requires the build or runs a step or phase naming {product}, {buildDir} or a compiler, and is heavy, too,",
            Prose.Words((await CliRunner.RunAsync(["help", "admission"], token)).StandardOutput),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The tools topic says what --dry-run does, that init installs only when asked, how a tool
    /// narrows the legs that need it, and that a leg in a developer environment is told about a tool
    /// as the PATH it sets up on the leg's host holds it; the layout topic says init writes the tree it
    /// runs in and names a rule by git's own answer.
    /// </summary>
    [Fact]
    public async Task ToolsAndLayoutTopics_SayWhatInitAndInstallMissingToolsDo()
    {
        var tools = await CliRunner.RunAsync(["help", "tools"], TestContext.Current.CancellationToken);
        var layout = await CliRunner.RunAsync(["help", "layout"], TestContext.Current.CancellationToken);

        foreach (var text in new[]
        {
            "--install-tools' runs it too; plain init installs nothing and says how.",
            "--dry-run reaches and asks every host as a run does, and installs no tool",
            "\"toolchains\": [\"msvc\"]",
            "\"legs\": [\"win-arm\", \"gate\"]",
            "\"processors\": [\"arm64\"]",
            "\"emulators\": [\"qemu-arm64\"]",
            "a developer environment, on the PATH that environment sets up for its processor -",
            "set up on the leg's host for the look, by the DssHarness there, so cl is found where",
            "the environment could not be looked at or set up, a tool its legs need is unknown,",
            "covering no declared leg, is refused.",
            "  check-mutations",
            "          what build starts: each worker, and each arm, is built as the leg builds",
        })
        {
            Assert.Contains(text, tools.StandardOutput, StringComparison.Ordinal);
        }

        foreach (var text in new[]
        {
            "block on later runs and leaving every other rule untouched. It then asks git which",
            "overrules does nothing there - named only where taking it out would take from git",
            "configuration, a placeholder, an action's files, an anchor registry - and no",
            "the slot a placeholder is kept in is overruled for the slot's contents and needed",
            "init writes the tree it runs in, a worktree's own included",
            $"('{GitIgnoreManager.RetiredBeginMarker}') is the managed block too: init",
            "rewrites it in place under the current markers and says so, and none of its lines",
        })
        {
            Assert.Contains(text, layout.StandardOutput, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The layout topic says the worktrees root, the orchestrators directory and the slots a tree keeps its copies in
    /// are kept in git by their placeholders with everything made in them ignored, what that costs, and that sync
    /// withholds each.
    /// </summary>
    [Fact]
    public async Task LayoutTopic_SaysTheWorktreesRootTheOrchestratorsDirectoryAndEachSlotAreKeptInGit()
    {
        var result = await CliRunner.RunAsync(["help", "layout"], TestContext.Current.CancellationToken);
        var text = Prose.Words(result.StandardOutput);

        foreach (var said in new[]
        {
            ".worktrees/ contents ignored, .gitkeep tracked; the worktrees, at worktrees.root, which init writes as this",
            ".orchestrators/ contents ignored, .gitkeep tracked; what each orchestrator and its agents keep, in the main checkout, "
                + "and never sent to a host by sync",
            ".mutations/ contents ignored, .gitkeep tracked; the tree's mutation workers, in every tree, never sent to a host by "
                + "sync ('dssharness help mutations')",
            ".legs/ contents ignored, .gitkeep tracked; the copies of the tree its legs work in, in every tree, never sent to a "
                + "host by sync",
            "The worktrees root, the orchestrators directory, .mutations and .legs are kept in git by their placeholders, so each "
                + "reads as not ignored itself while everything made in it is, and none can be a link: init names one it finds, and "
                + "writes nothing through it. Sync withholds each by name. Each copy in them is a whole copy of the tree, so a "
                + "program of the repository's own that walks the tree from its root - a census, a guard over every file - must "
                + "leave out what the block ignores, as git does, or it reads every file once more for each copy kept there.",
        })
        {
            Assert.Contains(said, text, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The legs topic says a toolchain names its compiler, that a build directory is held to the
    /// compiler it was configured with by the file it starts, not by its name, and where a language
    /// only a subproject enables is identified.
    /// </summary>
    [Fact]
    public async Task LegsTopic_SaysAToolchainNamesItsCompiler_AndHowABuildDirectoryIsHeldToIt()
    {
        var result = await CliRunner.RunAsync(["help", "legs"], TestContext.Current.CancellationToken);

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
    public async Task WorktreesTopic_SaysWhenDeleteWorktreeRefuses_AndHowToForceIt()
    {
        var result = await CliRunner.RunAsync(["help", "worktrees"], TestContext.Current.CancellationToken);

        foreach (var text in new[] { "delete-worktree", "uncommitted changes", "skip-worktree", "locked", "--force", "--delete-evidence", "--discard-uncommitted", $"exits {HarnessExit.Refused}", $"exits {HarnessExit.CommandFailed}" })
        {
            Assert.Contains(text, result.StandardOutput, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The worktrees topic says what becomes of the mutation workers kept in a worktree that is deleted: asked about
    /// before anything of it is removed, so a worktree kept for one removes nothing; which code each one kept leaves it
    /// with; what an interruption says; that --force deletes every copy with the worktree; and that a host's copy is
    /// treated as the worktree is - the leg copies of the worktree among the families that go with it, never sent to a
    /// host.
    /// </summary>
    [Fact]
    public async Task WorktreesTopic_SaysWhatBecomesOfTheCopiesKeptInAWorktree()
    {
        var result = await CliRunner.RunAsync(["help", "worktrees"], TestContext.Current.CancellationToken);

        Assert.Contains(
            "The copies kept in a worktree go before it, of its two families: the mutation workers its sweeps made "
            + "('help mutations'), in .mutations, and the copies of it its legs work in ('help legs'), in .legs - never "
            + "sent to any host, so only this machine holds them. They are asked about before anything of it is removed or "
            + $"looked through: one a sweep still running holds - or a run holds a leg copy - keeps the worktree ({HarnessExit.Refused}), and one that cannot "
            + $"be removed, or told for the harness's, keeps it as a failure ({HarnessExit.CommandFailed}), as workers that cannot be looked "
            + "for do, with nothing removed. A run holds a leg copy however either spells it: the run lock tells a tree on this "
            + "machine by where it leads, every link followed, whichever way the run holding it spelled it - a build before this one's "
            + "included - so a run in a worktree kept on another disk through a linked worktrees root, or in a copy kept through a "
            + ".legs that is a link, keeps it from a deletion that reaches it the other way. A sweep that takes one meanwhile keeps the worktree as "
            + "well, and so does one whose removal "
            + "then fails, the others gone by then, which is said. An interruption as they go says which went, and that nothing else of "
            + $"the worktree was removed ({HarnessExit.Cancelled}). --force deletes the worktree with every copy in it, one held or one "
            + "that could not be removed among them, saying which went with it. A worktree whose own removal then fails, or is stopped, "
            + "says which copies went before it. In a host's copy the workers are asked about first as well, and a copy kept once some had "
            + "gone says which went.",
            Prose.Words(result.StandardOutput),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnchorsTopic_DocumentsEveryStatus_Command_AndDefault()
    {
        var result = await CliRunner.RunAsync(["help", "anchors"], TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.Success, result.ExitCode);

        // Each command as its list gives it, with what it takes: named in prose alone, one was missing
        // from the list a reader scans.
        var expected = AnchorStatus.Cells
            .Concat(["write-anchor", "set-anchor", "read-anchor", "read-anchors", "check-anchor-balance [--base REF]"])
            .Concat(["check-anchor-citations --current-commit|--current-tree|--current-pr", "--anchor-dry-run"])
            .Concat([AnchorSettings.DefaultPendingAnchorsPath, AnchorSettings.DefaultDoneAnchorsPath, AnchorRegistryDocument.TableHeader]);

        foreach (var text in expected)
        {
            Assert.Contains(text, result.StandardOutput, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The balance says what it measures from, what it does not credit and what it refuses - a base that moved on, an
    /// unfinished merge, the bookkeeping pair, the open copy of an anchor closed already, an id with two rows, a lost row
    /// and the one it only notes - where the anchors are described.
    /// </summary>
    [Fact]
    public async Task AnchorsTopic_StatesWhatTheBalanceMeasuresFrom_AndDoesNotCredit()
    {
        var result = await CliRunner.RunAsync(["help", "anchors"], TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.Success, result.ExitCode);
        var prose = Prose.Words(result.StandardOutput);

        Assert.Contains("A base that has moved on since HEAD left it is measured from where HEAD left it", prose, StringComparison.Ordinal);
        Assert.Contains(
            $"a closure whose Trigger opens with {AnchorStatus.ClosedMark}{AnchorStatus.BookkeepingMark} records work that already existed",
            prose,
            StringComparison.Ordinal);
        Assert.Contains("an anchor open or closed where the change began is in neither registry now", prose, StringComparison.Ordinal);
        Assert.Contains("a lost one that was open is not credited either", prose, StringComparison.Ordinal);
        Assert.Contains("Nor is it for taking away the open row of an anchor that had a closed row already", prose, StringComparison.Ordinal);
        Assert.Contains("an id has more than one row", prose, StringComparison.Ordinal);
        Assert.Contains("one whose Anchor cell named no id, or more than one, is noted instead", prose, StringComparison.Ordinal);
        Assert.Contains("during an unfinished merge, HEAD is taken merged with what the merge brings in", prose, StringComparison.Ordinal);
    }

    /// <summary>
    /// The citation check says what it cannot see - a break inside a segment, with no hyphen on either
    /// side - both where its command is described and in the topic that lists it.
    /// </summary>
    [Theory]
    [InlineData("help", "anchors")]
    [InlineData("check-anchor-citations", "--help")]
    public async Task TheCitationCheck_StatesTheOneWrapItCannotSee(string first, string second)
    {
        var result = await CliRunner.RunAsync([first, second], TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.Success, result.ExitCode);
        // Read as prose: a description is wrapped to the terminal, with its continuation indented.
        var prose = Prose.Words(result.StandardOutput);

        Assert.Contains("with no hyphen on either side", prose, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LegsTopic_DocumentsTheNamesTheRulesAndTheExitCodes_FromTheCode()
    {
        var result = await CliRunner.RunAsync(["help", "legs"], TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.Success, result.ExitCode);

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

        // What a cancelled command is given to stop in, from the patience a host is built with: one that finishes what
        // it began as long as the command line gives it where it is typed.
        Assert.Equal(
            (TimeSpan.FromSeconds(60), DssHarness.Core.Worktrees.WorktreeService.DefaultInterruptionGrace),
            (new HostAgentPatience().Unwind, new HostAgentPatience().Finishing));
        Assert.Contains(
            "What the host started that has not stopped 60 seconds after it was cancelled - a command reading the output of something its child "
            + "left running - is ended with its processes, and the host's DssHarness with it, so the host is never held by a run nobody reads; a "
            + "command that finishes what it began - a deletion, a fold, a hand-over - is given 120, as it is where it is typed and interrupted. "
            + "Where this machine can no longer write to the connection while a host works, it says so here, with why: the host hears no more of "
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
            "Once the update has begun this command does not stop it, interrupted or not, and reads back what it left: it says the update done only once "
            + $"the tool is listed as that release, and a failure with what dotnet said and what is listed since, or why that is not known "
            + $"({HarnessExit.CommandFailed}).",
            prose,
            StringComparison.Ordinal);
        Assert.Contains(
            "Where nuget.org does not say which release is the newest it says why - no route to it, no answer in time, an answer it cannot read - "
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

    /// <summary>The overview lists update-tool among the commands a machine is set up with.</summary>
    [Fact]
    public async Task TheOverview_ListsUpdateTool()
    {
        var result = await CliRunner.RunAsync(["help"], TestContext.Current.CancellationToken);

        Assert.Contains("  dssharness update-tool             Update this machine's DssHarness, while none runs here", result.StandardOutput, StringComparison.Ordinal);
    }

    /// <summary>
    /// The runner topic says a leg's line names what its steps kept, as the path sync --pull takes.
    /// </summary>
    [Fact]
    public async Task RunnerTopic_SaysALegsLineNamesWhatItKept_AsSyncPullTakesIt()
    {
        var result = await CliRunner.RunAsync(["help", "runner"], TestContext.Current.CancellationToken);
        var text = Prose.Words(result.StandardOutput);

        Assert.Contains("names each file its steps kept as keptOutputs, relative to the tree", text, StringComparison.Ordinal);
        Assert.Contains("sync --pull' takes to bring it back", text, StringComparison.Ordinal);
        Assert.Contains(
            "A path given to --pull that ends with '/' names a directory: every file below it is brought back, each checked as a file named is and "
            + "each named in the report, as the path a later command takes. The host that holds it lists it first, and a directory that is not "
            + "there, holds no file, holds more than 256 files or more than 1024 MiB together fails the pull before any file crosses",
            text,
            StringComparison.Ordinal);
        Assert.Contains("A link below it is never followed: it is named, and nothing is brought back for it.", text, StringComparison.Ordinal);
        Assert.Contains(
            "A pull that stops part way - a file gone, a host that stopped answering - fails naming each file it had brought back, which stays "
            + "where it was written, and each it had not.",
            text,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The runner topic names the largest file that can cross to a host or back, the one the refusal names: a file crosses
    /// whole, inside one request, and that request is one line of text a string must hold.
    /// </summary>
    [Fact]
    public async Task RunnerTopic_NamesTheLargestFileThatCanCross_AsTheRefusalDoes()
    {
        var result = await CliRunner.RunAsync(["help", "runner"], TestContext.Current.CancellationToken);
        var text = Prose.Words(result.StandardOutput);

        Assert.Contains(
            $"A file crosses whole, whichever way it goes - to a host inside one request, back in one answer: one larger than {SyncServe.LargestFile.ToString(CultureInfo.InvariantCulture)} bytes is refused by name before anything is sent.",
            text,
            StringComparison.Ordinal);
        Assert.Contains(SyncServe.LargestFile.ToString(CultureInfo.InvariantCulture), SyncServe.TooLargeToCarry(SyncServe.LargestFile + 1, "a.bin", "ssh vps"), StringComparison.Ordinal);
    }

    /// <summary>
    /// The worktrees topic says a Ninja build leaves its dead outputs out of the path budget's warning, and names
    /// the ninja command that removes them, removing nothing itself.
    /// </summary>
    [Fact]
    public async Task WorktreesTopic_SaysDeadOutputsAreLeftOutOfTheWarning_AndWhatRemovesThem()
    {
        var result = await CliRunner.RunAsync(["help", "worktrees"], TestContext.Current.CancellationToken);
        var text = Prose.Words(result.StandardOutput);

        Assert.Contains("A Ninja build leaves out of that warning the outputs ninja says no target of it produces any more", text, StringComparison.Ordinal);
        Assert.Contains("naming 'ninja -t cleandead', which removes them. The harness removes nothing.", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The worktrees topic says list-worktree shows the copies hosts keep and those a gone worktree left, what --hosts
    /// asks the hosts, and what a host that cannot be asked does to the command.
    /// </summary>
    [Fact]
    public async Task WorktreesTopic_SaysListWorktreeFindsTheCopiesAGoneWorktreeLeft()
    {
        var result = await CliRunner.RunAsync(["help", "worktrees"], TestContext.Current.CancellationToken);
        var text = Prose.Words(result.StandardOutput);

        foreach (var said in new[]
        {
            "list-worktree lists each worktree with the copies this machine records hosts keeping of it, and the copies "
                + "left by worktrees that are gone - removed by hand, or by another tool - each with the delete-worktree "
                + "that deals with them.",
            "With --hosts it also asks each declared host which worktree copies it keeps beside its repositoryPath, and "
                + "how large each is, and sets them against that record",
            $"{HarnessExit.HostUnavailable} where a host cannot be reached. --json prints the listing as one JSON document.",
        })
        {
            Assert.Contains(said, text, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The worktrees topic says where an orchestrator's agents' worktrees sit, that they are named by their address, that
    /// a directory holding worktrees is never deleted as one, and how their copies are named apart.
    /// </summary>
    [Fact]
    public async Task WorktreesTopic_SaysAgentsWorktreesAreNamedByTheirAddress()
    {
        var result = await CliRunner.RunAsync(["help", "worktrees"], TestContext.Current.CancellationToken);
        var text = Prose.Words(result.StandardOutput);

        Assert.Contains(
            "An orchestrator's agents' worktrees sit below the directory named for it, <worktrees.root>/<orchestrator>/<agent>, made by "
            + "create-agent with its records, never by create-worktree. list-worktree lists each as orchestrator/agent, and delete-worktree "
            + "takes that address. The directory named for an orchestrator is never deleted as one while worktrees are below it, or a "
            + "directory holding a .git of its own that git records no worktree at - what a removal left, a person's to look in and "
            + "delete, or a worker an earlier version of this tool kept beside an agent's worktree, which clean --former-workers "
            + "removes - --force "
            + $"or not ({HarnessExit.Refused}), nor any directory with no .git of its own while git cannot list its worktrees "
            + $"({HarnessExit.CommandFailed}); a worktree's own submodules are never taken for worktrees. A plain worktree cannot take an "
            + "orchestrator's name. An agent's copies on hosts are kept under orchestrator--agent, so two orchestrators' agents of one name "
            + "keep theirs apart.",
            text,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The worktrees topic says that on Windows each junction is removed as a link before git runs, what stops that,
    /// and that git worktree repair is named only while git still records the worktree.
    /// </summary>
    [Fact]
    public async Task WorktreesTopic_SaysJunctionsAreRemovedAsLinks_AndWhenRepairIsNamed()
    {
        var result = await CliRunner.RunAsync(["help", "worktrees"], TestContext.Current.CancellationToken);
        var text = Prose.Words(result.StandardOutput);

        foreach (var said in new[]
        {
            "On Windows, each directory junction in the worktree is removed first, as a link, never what it leads to: git leaves "
                + "every junction, and the directories above it, while reporting the worktree removed.",
            $"One that cannot be removed stops the deletion before git runs ({HarnessExit.CommandFailed}), and a volume mounted "
                + "on a directory is never unmounted.",
            "A directory under the root with no .git and no record of git's - what a removal that stopped part way leaves - is "
                + $"refused ({HarnessExit.Refused}), naming --force; git worktree repair is named only while git still records a worktree there.",
        })
        {
            Assert.Contains(said, text, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The worktrees topic says that on Windows a worktree something holds part of is refused whole, with nothing
    /// removed, what holds one and what does not, what to do where it is the command's own current directory, and
    /// that --force does not look.
    /// </summary>
    [Fact]
    public async Task WorktreesTopic_SaysAWorktreeSomethingHoldsIsRefusedWholeOnWindows()
    {
        var result = await CliRunner.RunAsync(["help", "worktrees"], TestContext.Current.CancellationToken);
        var text = Prose.Words(result.StandardOutput);

        foreach (var said in new[]
        {
            $"On Windows, without --force, a worktree something holds part of is refused ({HarnessExit.Refused}) whole once "
                + "every check has passed, with nothing removed, naming what is held: a directory that is a process's current "
                + "directory, this command's own among them, a file open without sharing its deletion, or one a program is "
                + "running from.",
            "git's removal would stop there part way, with the worktree's .git file and git's record of it already gone.",
            "Close what holds it and run delete-worktree again, from outside the worktree where it is this command's own.",
            "A watcher on a directory, as an editor keeps, holds nothing; --force looks for nothing, and goes as far as it can.",
            "or, on Windows, a directory in it cannot be looked through for what holds it, nothing is deleted and it exits "
                + $"{HarnessExit.CommandFailed}.",
        })
        {
            Assert.Contains(said, text, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The config topic says a hold between commands runs the host's keepAwake, ends when a command's own starts,
    /// and needs keepAwake.
    /// </summary>
    [Fact]
    public async Task ConfigTopic_SaysAHoldBetweenCommandsEndsWhenACommandsOwnKeepAwakeStarts()
    {
        var result = await CliRunner.RunAsync(["help", "config"], TestContext.Current.CancellationToken);
        var text = Prose.Words(result.StandardOutput);

        Assert.Contains("holdAwakeSeconds holds it awake between commands, until a command's own keepAwake takes over", text, StringComparison.Ordinal);
        Assert.Contains("The next command's own keepAwake ends it there", text, StringComparison.Ordinal);
        Assert.Contains("It needs keepAwake, which it runs", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The legs topic says what a wake window retries and what it never does, with the delay the code uses.
    /// </summary>
    [Fact]
    public async Task LegsTopic_SaysWhatAWakeWindowTriesAgain_AndWhatItNeverDoes()
    {
        var result = await CliRunner.RunAsync(["help", "legs"], TestContext.Current.CancellationToken);
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
        var result = await CliRunner.RunAsync(["help", "legs"], TestContext.Current.CancellationToken);
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

    /// <summary>
    /// The space topic says what clean leaves alone and why it frees a full disk: nothing is written first,
    /// a build of the leg holds it off, and a host behind this machine's build needs room to be updated.
    /// </summary>
    [Fact]
    public async Task SpaceTopic_SaysCleanWritesNothingFirst_AndWhatItLeavesAlone()
    {
        var result = await CliRunner.RunAsync(["help", "space"], TestContext.Current.CancellationToken);
        var text = Prose.Words(result.StandardOutput);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("It writes nothing where it removes before it has removed", text, StringComparison.Ordinal);
        Assert.Contains("refused-locked", text, StringComparison.Ordinal);
        Assert.Contains("--dry-run", text, StringComparison.Ordinal);
        Assert.Contains("A build directory that is a link is left alone", text, StringComparison.Ordinal);
        Assert.Contains("both full and behind has to be freed by hand once", text, StringComparison.Ordinal);
        Assert.Contains("A leg is placed only where its host has the room its build still needs", text, StringComparison.Ordinal);
        Assert.Contains("the leg's buildSpaceGiB", text, StringComparison.Ordinal);
        Assert.Contains("Commands that build nothing - sync, clean - need no room.", text, StringComparison.Ordinal);
        Assert.Contains(
            "all of it as each leg's 'space', with its mutation workers' as 'workerBytes' and its copies' as 'copyBytes': what "
                + "was removed of them - 0 where every one there was kept - or, in a dry run, what is there. A leg whose build "
                + "directory was not measured - a link, locked or not removable - carries a space saying its workers and copies "
                + "alone, where any is there.",
            text,
            StringComparison.Ordinal);
        Assert.Contains(
            "one a live sweep claims is kept, one that cannot be removed fails the leg, which still says what went, and a directory "
                + "under a worker's name that no sync made is said and left",
            text,
            StringComparison.Ordinal);
        Assert.Contains("The mutation workers a sweep of the leg keeps in that tree go with it", text, StringComparison.Ordinal);
        Assert.Contains(
            "The copies of the tree its legs work in, in the tree's .legs slot, go the same way - the leg's own variant's and "
                + "those of a variant no leg builds any more, whatever the configuration says now",
            text,
            StringComparison.Ordinal);
        Assert.Contains(
            "a leg a run is building, or whose workers a sweep holds, or whose copy a run holds, is refused-locked in the dry run "
                + "too",
            text,
            StringComparison.Ordinal);
        Assert.Contains("A host holding no copy of the tree holds nothing of it to remove.", text, StringComparison.Ordinal);

        Assert.Contains("legs -v' says the room on each host it measured", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// check-ci-legs assumes no workflow of its own, and its topic says what it reads a workflow by instead.
    /// </summary>
    [Fact]
    public async Task CiTopic_SaysCheckCiLegsReadsOnlyWhatTheSettingsDeclare()
    {
        var result = await CliRunner.RunAsync(["help", "ci"], TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.Success, result.ExitCode);
        Assert.Contains("It knows no workflow of its own", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("with {leg} standing for the leg's", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("A green leg past 80% of its", result.StandardOutput, StringComparison.Ordinal);

        // The example reads what it says it reads: a leg and its budget from a whole job name, and the leg from one
        // the forge cut short, which it would otherwise not read at all.
        var example = new Regex(
            JsonSerializer.Deserialize<string>(ExamplePattern().Match(result.StandardOutput).Groups["pattern"].Value)!,
            RegexOptions.None,
            TimeSpan.FromSeconds(1));
        var whole = example.Match("test (linux-gcc-debug, 45)");
        var cut = example.Match("test (linux-gcc-debug-with-a-name-long-enou");

        Assert.Equal(("linux-gcc-debug", "45"), (whole.Groups["leg"].Value, whole.Groups["budget"].Value));
        Assert.Equal(("linux-gcc-debug-with-a-name-long-enou", false), (cut.Groups["leg"].Value, cut.Groups["budget"].Success));
    }

    /// <summary>
    /// Every command the help tells a reader to type - in the overview's list, its examples and every topic - is spelt
    /// as the package installs it, lower case: a Linux filesystem is case-sensitive, and the product's name,
    /// capitalised, runs nothing there. The product's name in prose is no command, and stays as it is.
    /// </summary>
    /// <remarks>
    /// A line that stands in for a verb rather than naming one tells the reader to type the product's name just as
    /// surely: 'DssHarness &lt;command&gt;' is a command line, and was missed while only literal verbs were looked for.
    /// </remarks>
    [Fact]
    public async Task EveryCommandTheHelpSaysToType_IsSpeltAsTheToolInstallsIt()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var overview = await CliRunner.RunAsync(["help"], cancellationToken);
        var texts = new List<string> { overview.StandardOutput };

        foreach (Match topic in TopicPattern().Matches(overview.StandardOutput))
        {
            texts.Add((await CliRunner.RunAsync(["help", topic.Groups["topic"].Value], cancellationToken)).StandardOutput);
        }

        var commands = InstalledCommandPattern().Matches(overview.StandardOutput)
            .Select(match => match.Groups["verb"].Value)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains("legs", commands);
        Assert.Contains("help", commands);
        Assert.All(texts, text => Assert.DoesNotContain(
            ProductNamePattern().Matches(text).Select(match => match.Groups["verb"].Value),
            word => commands.Contains(word) || word.StartsWith('<')));
    }

    /// <summary>
    /// The usage line of a command's own help is spelt as the package installs it, as the help topics are.
    /// </summary>
    /// <remarks>
    /// Nothing here writes that line: the command-line library builds it from the executable's own name, which is
    /// why the assembly is named for the command and not for the product. One mechanism serves every command, so a
    /// few stand for all of them rather than starting a process per command.
    /// </remarks>
    [Theory]
    [InlineData()]
    [InlineData("init")]
    [InlineData("build")]
    [InlineData("help")]
    public async Task ACommandsOwnHelp_SpellsItsUsageAsTheToolInstallsIt(params string[] command)
    {
        var rendered = await CliRunner.RunAsync([.. command, "--help"], TestContext.Current.CancellationToken);

        var usage = CliRunner.UsageLine(rendered.StandardOutput);

        Assert.NotNull(usage);
        Assert.StartsWith(ToolPackage.Command + " ", usage, StringComparison.Ordinal);
    }

    /// <summary>
    /// The set-ups topic, asked for by any of its names, says how a set-up is declared, what up and down run and exit
    /// with, and what a run asked to set up does with one; config names setUp, and exec no longer.
    /// </summary>
    [Theory]
    [InlineData("setups")]
    [InlineData("set-up")]
    [InlineData("up")]
    [InlineData("down")]
    public async Task SetUpsTopic_SaysHowOneIsDeclared_RunByHand_AndAroundARun(string topic)
    {
        var token = TestContext.Current.CancellationToken;
        var result = await CliRunner.RunAsync(["help", topic], token);
        var text = Prose.Words(result.StandardOutput);

        Assert.Equal(0, result.ExitCode);

        foreach (var said in new[]
        {
            "config.json names each set-up under setUp, with an up and a down command line",
            "a double-quoted word kept whole, and no shell between",
            "dssharness up <name> run its up line at the root of the tree, on this machine",
            "dssharness down <name> run its down line there",
            "test and run bring them up only when given --set-up; without it a run runs as it did.",
            "so a line must be safe to run twice.",
            "the run exits 21 where nothing failed.",
            "--input name=value gives an input its value, once per input, over its default;",
            "\"projects\": [ { \"name\": \"core\", \"test\": { \"all\": { \"setUp\": [\"e2e\"] } } } ]",
            "and 13 where a line a leg would run could not run on its system",
        })
        {
            Assert.Contains(said, text, StringComparison.Ordinal);
        }

        var config = Prose.Words((await CliRunner.RunAsync(["help", "config"], token)).StandardOutput);

        Assert.Contains("setUp what a run brings up around its work and down after, by name", config, StringComparison.Ordinal);
        Assert.DoesNotContain(" exec ", config, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every topic the overview advertises renders, and none is missing from it: the overview, the topic argument and the
    /// answer to an unknown topic all read one list of topics, which this reads back through the overview.
    /// </summary>
    [Fact]
    public async Task EveryTopicTheOverviewAdvertises_Renders()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var overview = await CliRunner.RunAsync(["help"], cancellationToken);

        Assert.Equal(0, overview.ExitCode);

        var topics = TopicPattern().Matches(overview.StandardOutput).Select(match => match.Groups["topic"].Value).ToList();

        Assert.Contains("orchestrators", topics);
        Assert.Contains("ci", topics);

        foreach (var topic in topics)
        {
            var rendered = await CliRunner.RunAsync(["help", topic], cancellationToken);

            Assert.Equal(0, rendered.ExitCode);
            Assert.True(rendered.StandardOutput.Length > 200, $"topic '{topic}' produced little output");
            Assert.DoesNotContain("Unknown topic", rendered.StandardOutput, StringComparison.Ordinal);
        }

        var unknown = await CliRunner.RunAsync(["help", "not-a-topic"], cancellationToken);
        Assert.Contains($"Try: {string.Join(", ", topics)}.", unknown.StandardError, StringComparison.Ordinal);
    }

    /// <summary>
    /// The orchestrators topic names every orchestrator and agent command, what an orchestrator keeps where, and that
    /// deleting an agent is never forced.
    /// </summary>
    [Fact]
    public async Task OrchestratorsTopic_NamesEveryCommand_WhatIsKeptWhere_AndThatDeletingIsNeverForced()
    {
        var result = await CliRunner.RunAsync(["help", "agents"], TestContext.Current.CancellationToken);
        var text = Prose.Words(result.StandardOutput);

        Assert.Equal(0, result.ExitCode);

        foreach (var command in new[] { "create-orchestrator", "delete-orchestrator", "list-orchestrator", "create-agent", "seed-agent", "refresh-agent", "rebase-agent", "fold-agent", "delete-agent" })
        {
            Assert.Contains($"  {ToolPackage.Command} {command} ", result.StandardOutput, StringComparison.Ordinal);
        }

        Assert.Contains("agents/<agent>/seed.json", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("agents/<agent>/shared/<sha256>", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("the bytes of each file it shares, kept to merge from", text, StringComparison.Ordinal);
        Assert.Contains(
            "Every command that writes an agent's records names where they are on each way out once it has: its record, its seed, the directory "
            + "of the bytes it shares where it keeps any, and the rows its folds applied - each that is there.",
            text,
            StringComparison.Ordinal);
        Assert.Contains(
            "--settled <path> names a path you reconciled in the main tree by hand: once the rest is written, the main tree's copy - a file, or its "
            + "absence - is handed to the agent, over only what was weighed there, and recorded as shared, so its next fold starts from the "
            + "reconciliation.",
            text,
            StringComparison.Ordinal);
        Assert.Contains(
            "one only the agent changed is refused, since nothing was reconciled there and handing the main tree's copy would undo its change. It "
            + "is not a --force.",
            text,
            StringComparison.Ordinal);
        Assert.Contains("put back as the main tree's HEAD holds it included", text, StringComparison.Ordinal);
        Assert.Contains($"  {ToolPackage.Command} refresh-agent <o> <a> [<path>... | --all] [--except <path>]...", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("      [--stale <path>]... [--merge <path>]... [--apply]", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains(
            "--stale <path>, once for each, says its copy is not the agent's work and hands it the main tree's - taken only where the agent's copy, "
            + $"as git would store it, is a blob the main tree's history held at that path, among the last {TwoChanges.MostCommits} commits that "
            + "changed it, and refused otherwise, nothing copied, as is one naming a path the agent holds no file at. The refusal over the agent's "
            + "changes names each such copy with the commit whose bytes it holds, and the arguments that hand them; it says too where the search "
            + "stopped short of the main tree's history. It reaches a path the main tree did not move as well - a copy put back by hand to an older "
            + "version - and one naming a path the agent holds as the main tree does is let stand.",
            text,
            StringComparison.Ordinal);
        Assert.Contains(
            "A path the agent changed that the main tree moved too, each holding a file there, is weighed in full as a fold weighs one: the refusal "
            + "says beside it what a three-way merge of the two gives - from what the agent last shared, or its base - shows the conflicts, and names "
            + "the arguments that take the clean ones. Two such paths that differ in case alone, one file in the agent's worktree, are weighed once: "
            + "under the name the main tree's git gives the file, from the base's file found in any case, whatever core.ignorecase says. "
            + "--merge <path>, once for each, takes a clean merge: written into the agent's worktree as its "
            + "change, for its fold to write, and the path recorded as handed the main tree's copy, which the merge descends from. It is never taken "
            + "unasked, and a clean textual merge proves nothing about the program; one naming no path under the paths refreshed is refused as a "
            + "misspelling, and one naming a path with nothing left to merge is let stand. The arm registry both changed is merged by arm, unasked, "
            + "and taken where the result reads as a registry of the worktree as the refresh leaves it; one that does not, or that conflicts, is "
            + "among the agent's changes, and a --merge naming it or an anchor registry is refused. A path named by two of --except, --stale and "
            + $"--merge is a usage error, exit {HarnessExit.UsageError}.",
            text,
            StringComparison.Ordinal);
        Assert.Contains("the anchor registries' directory where none are, and anywhere in the tree with --all, which takes no path beside it", text, StringComparison.Ordinal);
        Assert.Contains(
            "A refresh refused over the agent's changes names every path it changed, and the arguments that leave them: --except <path>, once for "
            + "each, hands it every other path and leaves that one as the agent changed it, its own work for its fold to weigh against what the main "
            + "tree holds - said in the report, never silently; it is not a --force, nothing is left out that nobody named, and one naming a path "
            + "the refresh would not have refused is refused, nothing copied.",
            text,
            StringComparison.Ordinal);
        Assert.Contains("refuses the move, nothing changed, unless --settled <path> names it", text, StringComparison.Ordinal);
        Assert.Contains(
            $"a move that stopped part way, exit {HarnessExit.Incomplete}, is finished where it was going by running it again - the command its "
            + "last words name, with every option the move was given - what it wrote held as the new base holds it, and every other command "
            + "refuses the agent until then, naming that same command.",
            text,
            StringComparison.Ordinal);
        Assert.Contains($"  {ToolPackage.Command} rebase-agent <o> <a> [--apply] [--json] [--merge <path>]... [--settled <path>]...", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains(
            "Where a three-way merge of the two is clean - git's, from what the old base held, of the new base's version and the agent's - "
            + "--merge <path>, once for each, writes it into the agent's worktree as its own change on the new base, whether it can be run merged "
            + "with its content: it is never taken unasked, and a clean textual merge proves nothing about the program. Nothing is recorded for it: "
            + "its fold weighs it against the new base. The refusal says what a merge of each such path gives, and names the command that takes the "
            + "clean ones.",
            text,
            StringComparison.Ordinal);
        Assert.Contains("a file turned into a directory, or a directory into a file or a submodule's entry - and its own changes", text, StringComparison.Ordinal);
        Assert.Contains("never forced, and its evidence check kept, so a file written late stops it", text, StringComparison.Ordinal);
        Assert.Contains("a file changed or new since is work, left for you", text, StringComparison.Ordinal);
        Assert.Contains("one not found is said, and one found and not kept stops it before anything is closed", text, StringComparison.Ordinal);
        Assert.Contains("A path this process cannot look at is never read as absent: the fold fails, nothing written.", text, StringComparison.Ordinal);
        Assert.Contains("it removes the record last, so one that stops part way finishes when run again", text, StringComparison.Ordinal);
        Assert.Contains("Seeding again hands besides every path the main tree holds otherwise than the agent shares it, committed or not.", text, StringComparison.Ordinal);
        Assert.Contains(
            "a symbolic link the main tree committed is named and never handed, and so is a submodule's entry. A file the main tree turned into a "
            + "directory, or a directory it turned into a file, is handed as git holds it: what goes is removed first, then what comes is copied.",
            text,
            StringComparison.Ordinal);
        Assert.Contains(
            "and records them as handed to it, so its fold leaves them out; one the agent already holds as the main tree does is recorded as shared - "
            + "listed, and written only with --apply - so its record says what it holds;",
            text,
            StringComparison.Ordinal);
        Assert.Contains(
            "refuses the hand-over, forced or not, naming each: copied, it would write over them, or through the link out of its worktree. A file of "
            + "the agent's written after it was weighed is never written over or removed by either, forced or not: the hand-over stops there, exit "
            + $"{HarnessExit.Incomplete}, naming it, and run again weighs it anew. Neither "
            + "hands anything while a move of the agent's base stands stopped part way, or its HEAD is off its base. Both say when the agent's base "
            + "is not the main tree's HEAD. A path named otherwise than in UTF-8, committed or not, refuses any hand-over, naming it as git quotes it: "
            + "no file opens here by such a name.",
            text,
            StringComparison.Ordinal);
        Assert.Contains(
            "or anything of its own where the main tree committed a file, ignored or staged included - refuses the move",
            text,
            StringComparison.Ordinal);
        Assert.Contains(
            "A file it made that is the very file the main tree committed there - the same content as git compares it, and the same mode where git "
            + "trusts one - is no such change: it is listed as held as the new base holds it already, and nothing is written.",
            text,
            StringComparison.Ordinal);
        Assert.Contains(
            "a file or a link where the new base holds a directory, and what it keeps - its own, settled or shared - in a directory where the new "
            + "base holds a file, or a submodule's entry that would hide it from git",
            text,
            StringComparison.Ordinal);
        Assert.Contains(
            "Only such a move is ever finished, and only from its base or where it goes: a HEAD moved by hand - back, forward or beside its base - "
            + "or naming no commit is never taken for one, and each command refuses it, saying how to put it back; a commit made inside the agent "
            + "is said to be one, and where git cannot say which a HEAD is, each says so and fails, never guessing.",
            text,
            StringComparison.Ordinal);
        Assert.Contains(
            "deleted, removed first, with the directories that leaves empty, so a file it turned into a directory, or a directory into a file, "
            + "makes room for what replaced it",
            text,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The orchestrators topic says that delete-orchestrator refuses while a directory below the orchestrator's holds a
    /// .git of its own that git records no worktree at, as it does while a worktree is left there.
    /// </summary>
    [Fact]
    public async Task OrchestratorsTopic_SaysDeleteOrchestratorRefuses_WhileADirectoryGitRecordsNoWorktreeAtIsBelowIt()
    {
        var result = await CliRunner.RunAsync(["help", "agents"], TestContext.Current.CancellationToken);

        Assert.Contains(
            "delete-orchestrator deletes the orchestrator once every agent of it is deleted and no worktree is left below its "
                + "directory, nor any directory holding a .git of its own that git records no worktree at - until then it refuses "
                + $"({HarnessExit.Refused}), naming each - and the evidence and transcripts its agents kept only with --delete-evidence;",
            Prose.Words(result.StandardOutput),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The orchestrators topic says what a fold does with a path both trees changed: what a three-way merge of it gives
    /// is said, a clean one is taken only where --merge names it, what is never merged, and that whether a file can be
    /// run is merged with its content. The bound it names is the code's.
    /// </summary>
    [Fact]
    public async Task OrchestratorsTopic_SaysWhatAFoldMerges_WhatItNeverMerges_AndHowTheBitIsMerged()
    {
        var result = await CliRunner.RunAsync(["help", "agents"], TestContext.Current.CancellationToken);
        var text = Prose.Words(result.StandardOutput);

        Assert.Equal(0, result.ExitCode);

        foreach (var command in new[] { "fold-agent", "delete-agent" })
        {
            Assert.Contains($"  {ToolPackage.Command} {command} <o> <a> [--apply] [--json] [--merge <path>]... [--settled <path>]...", result.StandardOutput, StringComparison.Ordinal);
        }

        Assert.Contains(
            "already in the main tree; merged, where both trees changed it and --merge names it, or it is the arm registry, merged by arm unasked; "
            + "or settled",
            text,
            StringComparison.Ordinal);
        Assert.Contains(
            "its refusal says what a three-way merge of the two gives - git's merge, of the forms git stores, from what both held, which for a shared "
            + "path are the bytes kept beside the agent's seed, or found by their digest in the main tree's history where none were kept, and for any "
            + "other its base's - clean, with the hunks each side brought; conflicting, with the conflicts shown; or none offered, with why - and names "
            + "the command that takes the clean ones.",
            text,
            StringComparison.Ordinal);
        Assert.Contains(
            "--merge <path>, once for each, takes a clean merge: written into both trees, the main tree's first, and recorded as shared once both "
            + "hold it, so a fold that stopped between the two merges again to the same bytes. It is never taken unasked, a conflict never, and a "
            + "clean textual merge proves nothing about the program",
            text,
            StringComparison.Ordinal);
        Assert.Contains(
            "A --merge naming no path the fold weighs is refused as the misspelling it most likely is, and one naming a path with nothing left to "
            + "merge is let stand, so the command line of a fold that stopped part way runs again; a path named with both --merge and --settled is "
            + $"a usage error, exit {HarnessExit.UsageError}, and a --merge naming an anchor registry or the arm registry is refused.",
            text,
            StringComparison.Ordinal);
        Assert.Contains(
            "Never merged, whatever is named: a deletion against a change, a path both trees made, a version that is a link or is reached through "
            + "one, a file git reads as binary or will not merge, one "
            + $"whose merge attribute asks for another merge, a file above {DiskSpace.Size(TwoChanges.MostBytes)}, and an anchor registry; nor is the "
            + "arm registry ever merged by line.",
            text,
            StringComparison.Ordinal);
        Assert.Contains(
            "The arm registry (mutations.registry) both trees changed is merged by arm, unasked - each arm from the side that alone added, removed or "
            + "changed it, each version as its tree holds it, from nothing where neither held a registry before - and taken only where the merged text "
            + "reads as a registry of the tree as the command leaves it, as check-mutations reads one ('help mutations'); an arm the two changed "
            + "differently, a version that cannot be read as a registry, or a merged text that does not read as one refuses the path, each shown, and so "
            + "do shared bytes that cannot be found, since an arm one side removed would come back.",
            text,
            StringComparison.Ordinal);
        Assert.Contains(
            "The arm registry is merged by arm, unasked, as a fold merges it (below), from the old base's version, of the new base's as the worktree "
            + "checks it out and the agent's, and judged by the worktree as the move leaves it.",
            text,
            StringComparison.Ordinal);
        Assert.Contains(
            "Whether a file can be run is merged with its content: the merged file takes the bit of the side that changed it, and the bit alone never "
            + "conflicts - but a registry both trees made, one so that it can be run and one not, is offered no merge, since nothing either held "
            + "before says which is meant; Windows keeps no such bit, and none is read or set there.",
            text,
            StringComparison.Ordinal);
        Assert.DoesNotContain("mode is never merged", text, StringComparison.Ordinal);
        Assert.Contains(
            "A --merge naming a path the two commits hold alike is refused as the misspelling it most likely is, and one naming any other path "
            + "they changed is let stand, so the command that finishes a stopped move runs again; a path named with both --merge and --settled is "
            + $"a usage error, exit {HarnessExit.UsageError}; and a path one of whose versions is a link, or that the agent reaches through one, is "
            + "offered no merge.",
            text,
            StringComparison.Ordinal);
        Assert.Contains("a change, a new file, a deletion, a file renamed in case alone, a link, a submodule's entry,", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The orchestrators topic, the architecture and the README say alike that a file the new base renamed in case alone
    /// comes in only with its old name, never over the agent's copy or its deletion of it, that a file the agent's session
    /// wrote after it was weighed stops a move before git writes anything, that a fold refuses a rename of case alone the
    /// agent made, naming git mv -f for the main tree, and weighs it against the base's file under its old name, and that a
    /// refresh weighs two names of one file once.
    /// </summary>
    [Fact]
    public async Task OrchestratorsTopic_SaysWhatRenamesOfCaseAlone_AndASessionsWrites_DoToAMoveAFoldAndARefresh()
    {
        var result = await CliRunner.RunAsync(["help", "agents"], TestContext.Current.CancellationToken);
        var text = Prose.Words(result.StandardOutput);
        var architecture = Prose.Document("docs", "architecture.md");
        var readme = Prose.Document("README.md");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(
            "So does a file the new base renamed in case alone, where the agent's file system finds a name in any case, unless its old name comes in "
            + "with it: where the agent keeps that - its own change, settled or shared - writing the new name would write over it, and both are named, "
            + "a shared one it changed to be folded first, since no refresh hands it, and one it did not to be refreshed under the new name; where the "
            + "agent's worktree no longer holds it, the two are named as a rename and a deletion of one file - its old name to be put back and the move "
            + "run again, the new name deleted after only where the agent's session deleted the old - unless a move that stopped part way removed it, "
            + "as its record says, which running it again finishes. "
            + $"A file of the agent's written after it was weighed is never written over or removed: the move stops there, exit {HarnessExit.Incomplete}, "
            + "before git writes anything, naming it.",
            text,
            StringComparison.Ordinal);
        Assert.Contains(
            "A rename of case alone the agent made, where both trees find a name in any case and the main tree's git index names the file as before, is "
            + "refused by name: a fold writes files, and the main tree's git would go on naming the file as before - git mv -f there makes the rename, of "
            + "a file in a directory renamed in case too, and the fold run again finds the old name absent from both. The renamed file is weighed "
            + "against the base's under its old name, found in any case whatever core.ignorecase says: an edit made with the rename merges with the main "
            + "tree's, and the main tree's "
            + "removal of the file refuses it, never undone.",
            text,
            StringComparison.Ordinal);
        Assert.Contains(
            "It comes in only with its old name: where the agent keeps what it holds there - declared settled, shared with the main tree, or its own "
            + "change - the new name refuses the move, naming both, since writing it would write over that; a shared file the agent changed is to be "
            + "folded first, since no refresh hands a path the agent changed, and one it did not to be refreshed under the new name, which records the "
            + "file it holds as shared there. Where it holds nothing at the old name, the new name refuses the move as a rename and a deletion of one "
            + "file - its old name to be put back with `git checkout HEAD` and the move run again, the new name deleted after only where the agent's "
            + "session deleted the old, or, shared, never taken by a move and so the agent's deletion, folded and committed in the main tree - unless "
            + "the move being finished removed it, the new name written last, as its record says (`MovingRemoved`: the old names the agent's index no "
            + "longer holds once the git checking out what the move writes first ends, having written it all or stopped part way). git leaves the "
            + "index and the disk alike after that checkout and after a `git rm` of the agent's session; a removal of the move's its record missed - a "
            + "move killed between git and its record - is refused so, costing a refusal, never a file. The record names where the "
            + "move goes (`Moving`) before anything is written; then each path the move takes is asked to hold what was measured there "
            + "(`RebasePlan.Held`, `AgentFold.RequireTakenAsync`), so a file the agent's session wrote since stops the move, exit 21, before git "
            + "writes anything, never written over;",
            architecture,
            StringComparison.Ordinal);
        Assert.Contains(
            "The base's entry and HEAD's are found alike, as git spells the path and, where the main tree's file system finds a name in any case, in "
            + "any case, whatever `core.ignorecase` says, so a rename of case alone is the commit's that made it, and none other's.",
            architecture,
            StringComparison.Ordinal);
        Assert.Contains(
            "where its git index spells that file otherwise than its base does, found in any case, the agent renamed it in case alone, and each "
            + "path is read only where a tree spells the file so (`AgentFold.Spellings`) - as its git index does, where it holds the file under one "
            + "spelling, since a rename git makes in the index alone leaves a directory's name on disk as it was, and otherwise as its file system "
            + "does (`PathCase.Spelled`) - never as the other. Where the main tree's git index spells that file otherwise, each tree "
            + "holding it, and the main tree finds a name in any case too, a fold cannot write the rename - the main tree's git would go on naming the "
            + "file as before - so it is refused by name, saying to run `git mv -f` there, forced since git finds the new name taken where the "
            + "repository does not say it ignores case; once that is run, the old name is already in, absent from both. Every path the agent shares "
            + "nothing for is weighed against the base's entry found as git spells it, as `Came` finds it, so a file renamed in case alone is weighed "
            + "against the base's under its old name: an edit made with the rename merges with the main tree's, and a removal of the file in the main "
            + "tree refuses it, never undone.",
            architecture,
            StringComparison.Ordinal);
        Assert.Contains(
            "(`TwoChanges`, from what the seed records or the agent's base holds, found as git spells the path; the main tree's file the side merged "
            + "into) - two such paths that differ in case alone, one file in the agent's worktree where it finds a name in any case, once, under the "
            + "name the main tree's git index spells the file by, from the base's entry found in any case, the others - one left as the agent changed "
            + "it among them - no file of the main tree's, whatever its file system finds under them:",
            architecture,
            StringComparison.Ordinal);
        Assert.Contains("a path the agent already holds as the main tree does - the same file, or no file in either, a directory of the agent's own files there included -", architecture, StringComparison.Ordinal);
        Assert.Contains(
            "refused over anything of its own what comes in would go over, a file renamed in case alone over the agent's copy at its old name, or its "
            + "deletion of it, among it; a file the agent's session wrote after it was weighed stops it, exit 21, never written over;",
            readme,
            StringComparison.Ordinal);
        Assert.Contains(
            "a rename of case alone the agent made is refused, saying to `git mv -f` it in the main tree, and once made there is weighed against the "
            + "base's file under its old name;",
            readme,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The orchestrators topic says the one rule of what a dry run exits with, where its summary then stands, that a row
    /// an earlier fold applied is weighed cell by cell with --accept-over, and that a closed agent's husk is answered by
    /// its dry run as every --apply of it answers.
    /// </summary>
    [Fact]
    public async Task OrchestratorsTopic_SaysWhatADryRunExitsWith_AndWeighsRowsCellByCell()
    {
        var result = await CliRunner.RunAsync(["help", "agents"], TestContext.Current.CancellationToken);
        var text = Prose.Words(result.StandardOutput);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(
            "Dry runs. A dry run of refresh-agent, rebase-agent, fold-agent or delete-agent exits with what its --apply, typed "
            + "with the same arguments, would exit with at that moment: 0 only where that --apply would go through. Where a path, "
            + $"a row, or a consent nobody gave would refuse it, the dry run exits {HarnessExit.Refused} - {HarnessExit.UsageError} "
            + "where its arguments are a usage error, a path never handed to an agent among them - and where it would fail "
            + "before writing anything, with that failure's code - and either way it still prints all it weighed on standard "
            + "output, its summary last, on standard error. A run holding what the --apply takes refuses the dry run too: every "
            + "holder is named on a 'held now:' line, said with where its run's leg stands where it asked for a heavy-leg slot, "
            + $"and it exits {HarnessExit.Refused} - save where the dry "
            + "run already failed before writing anything, or met a usage error its --apply answers before it takes anything, "
            + "which keeps its code and names the holder still; "
            + $"delete-agent, whose --apply would go as far as the removal and leave it unfinished, exits {HarnessExit.Incomplete} "
            + "instead where only a copy of the worktree is held, by a run or a sweep; and where nothing refuses its --apply "
            + "before the removal - a tree held, a lock file that cannot be read, a path, a row or a consent - it exits "
            + $"{HarnessExit.Incomplete} too where the copies kept in the worktree cannot be removed as they stand, one that "
            + "cannot be told or removed or none that can be looked for, its request then refused; a directory there that no "
            + "sync made goes with the worktree, and is only said. A record of the copies hosts keep that cannot be read refuses "
            + $"delete-agent ({HarnessExit.Refused}), its dry run and its --apply alike, before anything is written. What an "
            + "--apply can only find by "
            + "writing - a file changed after it was weighed, a write "
            + "that fails - is not in a dry run, so 0 never promises that the --apply will finish. --json answers with one "
            + "document, the whole of standard output, whose request member says what is wanted of you - 'sound' (nothing; wait, "
            + "where heldNow names a holder), 'needs-consent' (name the cells the dry run shows) or 'refused' (put right what the "
            + "last line names) - and whose heldNow holds each holder that decides its code: each a 'held now:' line names, with "
            + "where its leg stands - null for a leg of this machine where this machine's record of heavy legs cannot be read - "
            + "and each holding a copy kept in the "
            + "worktree, said as its own line says it, a leg copy's holder with where its leg stands, a mutation worker's with "
            + "the process, the run and since when its sweep's claim records. An --apply refused at its hold answers the same "
            + "document, applied true: request 'sound', and the run it met in heldNow. Stopped before "
            + "anything is weighed - a command line the parser refuses, a --directory that is not there, a repository not "
            + "initialised, another usage error, or a refusal met before then, an agent that is not there among them - it "
            + "answers with the same document, empty: applied false, request and heldNow null, no lines.",
            text,
            StringComparison.Ordinal);
        Assert.Contains(
            "A dry run of the four asks with the very hold its --apply takes, so a run holding what it needs is named and the dry "
            + $"run refused itself, exit {HarnessExit.Refused}, saying that nothing in the request needs correcting; the refusal an "
            + "--apply gives names the holder in the words of the dry run's 'held now:' line, where its run's leg stands included. "
            + "A holder is a fact about one moment, so 0 from a dry run never promises the "
            + "tree will still be free when the --apply is typed.",
            text,
            StringComparison.Ordinal);
        Assert.Contains(
            "delete-agent alone names a copy a run holds, since it removes every copy with the worktree: a host's, a leg copy kept "
            + "in it, and a mutation worker, each in its own words.",
            text,
            StringComparison.Ordinal);
        Assert.Contains(
            "and --apply refuses the fold, writing nothing, until each is named - and so the dry run that shows it "
            + $"exits {HarnessExit.Refused} itself.",
            text,
            StringComparison.Ordinal);
        Assert.Contains(
            "One declared anew over it is weighed cell by cell - priority, status, trigger, closing, cross-refs - from what that "
            + "fold recorded as declared, against the registries as the write finds them: a cell only the agent changed is "
            + "written; one only the registries changed is kept, and said, and stays kept at each fold until the agent changes "
            + "it; one both changed alike is left; and one both changed otherwise is written over the registries' only where "
            + "--accept-over <ID>:<cell> names it, the dry run showing the three texts - what --accept-over consents to includes "
            + "what the registries' text loses. The fold never writes the agent's own row files: before it changes a kept cell, "
            + "the agent writes the registries' text into it, or its change is two changes of one cell.",
            text,
            StringComparison.Ordinal);
        Assert.Contains(
            "since every --apply of it ends so, its dry run exits " + HarnessExit.Incomplete + " too, naming the same command and keeping nothing.",
            text,
            StringComparison.Ordinal);
        Assert.Contains("delete-agent refuses to run from inside the worktree it removes, and so does its dry run,", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownTopic_SaysSoAndListsTheRealOnes()
    {
        var result = await CliRunner.RunAsync(
            ["help", "not-a-topic"],
            TestContext.Current.CancellationToken);

        // A mistyped topic is a usage error. Reporting it on stdout with exit 0 would
        // break both "zero always means success" and the pipeable-output rule.
        Assert.Equal(HarnessExit.UsageError, result.ExitCode);
        Assert.Contains("Unknown topic", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("exit-codes", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("Unknown topic", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task KnownTopic_Succeeds_AndWritesToStandardOutput()
    {
        var result = await CliRunner.RunAsync(
            ["help", "layout"],
            TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.Success, result.ExitCode);
        Assert.Contains(".harness-config", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Overview_ExplainsTheGlobalOptions()
    {
        var result = await CliRunner.RunAsync(["help"], TestContext.Current.CancellationToken);

        Assert.Contains("--directory", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("--verbose", result.StandardOutput, StringComparison.Ordinal);
    }

    private static IEnumerable<(KeyDescription Key, int Depth)> Listed(IReadOnlyList<KeyDescription> keys, int depth)
        => keys.SelectMany(key => Listed(key.Keys, depth + 1).Prepend((key, depth)));

    [GeneratedRegex(@"^\s{2}(?<name>\S+)(\s+\[?<\S+)*\s{2,}(?<description>.+)$")]
    private static partial Regex CommandLinePattern();

    [GeneratedRegex(ToolPackage.Command + @" help (?<topic>[a-z-]+)")]
    private static partial Regex TopicPattern();

    [GeneratedRegex(@"^\s{2}" + ToolPackage.Command + @" (?<verb>[a-z-]+)", RegexOptions.Multiline)]
    private static partial Regex InstalledCommandPattern();

    /// <summary>The product's name followed by a verb, or by something standing in for one.</summary>
    [GeneratedRegex(ToolPackage.Id + @" (?<verb><[a-z-]+>|[a-z-]+\b)")]
    private static partial Regex ProductNamePattern();

    [GeneratedRegex(@"""legJobPattern"": (?<pattern>""(?:[^""\\]|\\.)*"")")]
    private static partial Regex ExamplePattern();

    /// <summary>
    /// The workflows topic says what a workflow is, where it lives, what this build does with one and what it does not,
    /// and lists every key from the tables the reader itself reads - so what is listed is what is read.
    /// </summary>
    [Fact]
    public async Task TheWorkflowsTopic_SaysWhatAWorkflowIs_AndListsEveryKeyTheReaderTakes()
    {
        var result = await CliRunner.RunAsync(["help", "workflows"], TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.Success, result.ExitCode);

        var prose = Prose.Words(result.StandardOutput);

        Assert.Contains(
            "A workflow is a flow of typed steps that depend on one another by name, declared by name under workflows in config.json and defined by a yml file of its own:",
            prose,
            StringComparison.Ordinal);
        Assert.Contains(
            "This build checks a workflow's definition, with 'dssharness validate-workflow <name>'; it does not run one.",
            prose,
            StringComparison.Ordinal);
        Assert.Contains(
            "every path the yml names is relative to that directory and stays inside it, as an action's files are relative to its own directory",
            prose,
            StringComparison.Ordinal);
        Assert.Contains("params - its own knobs, defaulted in the file and read as param.<name>", prose, StringComparison.Ordinal);

        // The refusal's code is the one the command answers with, held here so the sentence cannot drift from it.
        Assert.Contains(
            "'dssharness validate-workflow' checks all of it - the shape of every key, every name an expression reads, every file the "
                + "definition names - and refuses (13) naming each problem, without running anything.",
            prose,
            StringComparison.Ordinal);

        // Every key of the file, of every step and of every group, from the tables the reader reads.
        Assert.All(WorkflowKeys.File, key => Assert.Contains($"  {key.Name}", result.StandardOutput, StringComparison.Ordinal));
        Assert.All(WorkflowStepTypes.Common, key => Assert.Contains($"  {key.Name}", result.StandardOutput, StringComparison.Ordinal));
        Assert.All(WorkflowStepTypes.Group, key => Assert.Contains($"  {key.Name}", result.StandardOutput, StringComparison.Ordinal));
        Assert.All(WorkflowStepTypes.All, type => Assert.Contains($"  {type.Type}", result.StandardOutput, StringComparison.Ordinal));
    }

    /// <summary>
    /// The config topic names the workflows key and the layout topic the workflows directory, each saying where the
    /// definition lives: never an entry in the one, always a file of its own under the other.
    /// </summary>
    [Fact]
    public async Task TheConfigAndLayoutTopics_NameTheWorkflowsKeyAndItsDirectory()
    {
        var config = await CliRunner.RunAsync(["help", "config"], TestContext.Current.CancellationToken);
        var layout = await CliRunner.RunAsync(["help", "layout"], TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.Success, config.ExitCode);
        Assert.Equal(HarnessExit.Success, layout.ExitCode);

        Assert.Contains(
            "  workflows      the workflows this repository runs, each defined by a yml file of its",
            config.StandardOutput,
            StringComparison.Ordinal);
        Assert.Contains(
            "  .harness-config/workflows/<name>/<name>.yml",
            layout.StandardOutput,
            StringComparison.Ordinal);
    }

    /// <summary>A step key shown as it is written, then what it does: '  outputs: [&lt;path&gt;, ...]  what it writes'.</summary>
    [GeneratedRegex(@"^  (?<key>[A-Za-z]+): \S.*?  +(?<meaning>\S.*)$")]
    private static partial Regex SpeltKeyLine();
}
