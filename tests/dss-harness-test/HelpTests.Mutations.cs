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

namespace DssHarness.Tests;

public sealed partial class HelpTests
{
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
        var result = HelpText.Of(topic);
        var keys = ConfigKeys.Of<MutationSettings>();

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
        var said = Prose.Words((HelpText.Of("mutations")).StandardOutput);

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
            "Each leg is swept in worker copies of its tree, kept in it as <tree>/.m/<key>w-<n>, numbered from 1:",
            "directory is named, and a worker builds in <worker>/build/<key>, never in the directory the variant's name gives the "
            + "tree's own build: its first worker adds 14 character(s) to its tree's path, and its build directory 14 character(s) "
            + "below that, whatever the variant is called,",
            "Which variant a worker builds is what its build records, and a build of one variant where that record names another - "
            + "two whose names come to one key share their workers - starts from clean; the lines of a sweep and of a clean say which "
            + "leg, and which variant, a worker is.",
            "Each is a repository of its own, as every copy a sync makes is, and init keeps .m in git and everything made in "
            + $"it out: a sweep in a tree where git does not ignore what it would make there is refused ({HarnessExit.Refused}), naming "
            + "init, with nothing made - asked on the machine that makes the workers, so a host answers for its own copy.",
            "A worker is claimed while a sweep uses it, in <worker>.claim.json beside it in .m,",
            "a worker a sweep still running holds keeps its worktree, or its host's copy, until the sweep has ended, and one that "
            + "cannot be removed keeps it as a failure until that is put right - or --force deletes the worktree with that worker in it. "
            + "A directory there under a worker's name that no sync made goes with the worktree, or the host's copy, as everything "
            + "else in it does, and is said. No copy of a tree carries its workers, and a sync into a host's copy never deletes that "
            + "copy's own. Workers an earlier version of this tool kept beside a tree, as <tree>.mutation-<key><mark>-<n>, or in "
            + "it, as <tree>/.mutations/<key><mark>-<n> - which init keeps out of git whole, with no placeholder, and no sync sends "
            + "or deletes - are no workers to a sweep: nothing counts one, and clean --former-workers removes them - beside and in "
            + "each tree of the repository on each machine a selected leg runs on, here and on hosts, and beside each one deleted "
            + "since, a dry run listing each with its size - keeping one a sweep of that version still claims by the claim file "
            + "beside it; those in a worktree, or a host's copy, also go with it as its workers do. Each beside a tree holds a .git "
            + "of its own that git records no worktree at, so until then one below the worktrees root is said as any such directory: "
            + "list-worktree warns of it, and delete-orchestrator, and delete-worktree of an orchestrator's directory with or "
            + $"without --force, refuse ({HarnessExit.Refused}) while one is below it.",
            "built whole before it drives an arm: that sync withholds the worker's own build directory, build/<key>, itself, "
            + "whatever sync.neverTransfer says, and sync.maxDeleteFraction judges only what mirrors the tree the sweep read. "
            + "Of the tree itself a sweep reads every file and writes none: its workers, and "
            + "the claims on them, are in .m, and its run's records in .harness-config, as every run's are.",
            "Its workers are kept in the leg's own tree, as a sweep's are, in a family of their own - <tree>/.m/<key>s-<n>,",
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
        var said = Prose.Words((HelpText.Of("mutations")).StandardOutput);
        var config = Prose.Words((HelpText.Of("config")).StandardOutput);

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
        var said = Prose.Words((HelpText.Of("mutations")).StandardOutput);
        var layout = Prose.Words((HelpText.Of("layout")).StandardOutput);

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
        var said = Prose.Words((HelpText.Of("mutations")).StandardOutput);

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
        var said = Prose.Words((HelpText.Of("mutations")).StandardOutput);
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
        var said = Prose.Words((HelpText.Of("mutations")).StandardOutput);
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
    /// Help says what a command exits with where no selected leg can run, wherever it speaks of it: 1 of a build, a test
    /// and a run, and of a sweep incomplete, whose 1 is an arm violated - each code read from the code.
    /// </summary>
    [Fact]
    public async Task Help_SaysWhatASweepGives_WhereNoSelectedLegCanRun()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var mutations = Prose.Words((HelpText.Of("mutations")).StandardOutput);
        var tools = Prose.Words((HelpText.Of("tools")).StandardOutput);

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
}
