using System.Globalization;
using System.Text;
using DssHarness.Core.Ci;
using DssHarness.Core.Anchors;
using DssHarness.Core.Agents.Runtime;
using DssHarness.Core.Codebase;
using DssHarness.Core.Build;
using DssHarness.Core.Configuration;
using DssHarness.Core.Execution;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Git;
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
using DssHarness.Core.Secrets;
using DssHarness.Core.SetUps;
using DssHarness.Core.Sync;
using DssHarness.Core.Tools;
using DssHarness.Core.Worktrees;

namespace DssHarness.Core.Help;

public static partial class HelpTopics
{
    private static string RenderMutations()
    {
        var builder = new StringBuilder();

        builder.AppendLine("Mutation testing");
        builder.AppendLine();
        AppendWrapped(
            builder,
            "check-mutations proves a repository's tests can fail. For each selected leg and each arm its registry "
            + "declares, it mutates exactly the text the arm names, in a worker copy of the leg's tree and never in the tree "
            + "itself; builds the arm's target; proves every object that depends on the site was rebuilt; runs the arm's test "
            + "binary whole; judges what failed against what the arm declares, as an exact set; and puts the site back, "
            + "checked by its hash. A BUILD-RED arm's mutation must instead stop the build at an object that depends on its "
            + "site, and its paired positive control must build.");
        builder.AppendLine();
        builder.AppendLine("  \"mutations\": {");
        builder.AppendLine("    \"registry\": \"tests/mutations/arms.registry\",");
        builder.AppendLine("    \"textDirectory\": \"tests/mutations/texts\",");
        builder.AppendLine($"    \"reportArgs\": [\"--gtest_output=xml:{{{MutationReport.Placeholder}}}\"]");
        builder.AppendLine("  }");
        builder.AppendLine();
        AppendKeys(builder, ConfigKeys.Of<MutationSettings>());
        builder.AppendLine();
        AppendWrapped(
            builder,
            $"workers is {MutationSettings.DefaultWorkers} and runTimeFactor {MutationSettings.DefaultRunTimeFactor.ToString(CultureInfo.InvariantCulture)} "
            + "where left out. The registry and textDirectory are relative to the repository root, and carried by sync like "
            + "the rest of the tree: one inside the harness's own directory, which sync never carries but for its runner "
            + "actions, is refused, and so is one sync.neverTransfer, sync.exclude or worktrees.root keeps a sync from "
            + $"carrying - a worker is a copy a sync makes, and a host sweeps its own. reportArgs makes a test binary write a JUnit XML report to {{{MutationReport.Placeholder}}}, "
            + "a new file for every run: [\"--reporter\", \"JUnit::out={report}\"] for Catch2, [\"--reporters=junit\", "
            + "\"--out={report}\"] for doctest, [\"--logger=JUNIT,all,{report}\"] for Boost.Test. It is required where an arm runs "
            + "its binary, and only {report} may stand in braces. "
            + "runReads names, for each runner as the A rows name it, the files of the tree its runs read at run time - a data "
            + "file, a golden output, a plugin it opens - each a path, everything below a directory, or **/name at any depth: what "
            + "the sweep's reading of the tree holds of each is part of the key of every arm that runner runs. A runner given no "
            + "file, an entry outside the tree, one holding a '*' anywhere but a leading '**/', and one naming the whole tree "
            + "('.'), are refused; so is a runner no TEST-RED arm's A row names, when the registry is read.");
        builder.AppendLine();
        builder.AppendLine("The registry");
        builder.AppendLine();
        AppendWrapped(
            builder,
            "The repository's own file: one row to a line, its fields separated by '|' and trimmed, a line whose first "
            + "character that is not blank is '#' a comment, nothing escaped, and the last field taking the rest of the line, "
            + "so a why may hold a '|'.");
        builder.AppendLine();
        builder.AppendLine("  A | arm | site | before | after | red kind | target | runner | cases | diag | why");
        builder.AppendLine("  C | arm | case | why                          a case the mutation must redden");
        builder.AppendLine("  G | arm | case | why                          a neighbour that must run and stay green");
        builder.AppendLine("  B | arm | control before | control after | why");
        builder.AppendLine("                                                a BUILD-RED arm's paired control");
        builder.AppendLine("  M | arm | site | before | after | why         another site, mutated, put back and");
        builder.AppendLine("                                                checked with the arm's own: another");
        builder.AppendLine("                                                file, or a further text of a file");
        builder.AppendLine("                                                the arm already mutates");
        builder.AppendLine("  S | arm | legs | why                          the legs it runs on, named as --legs");
        builder.AppendLine("                                                names them; without one, every leg");
        builder.AppendLine();
        AppendWrapped(
            builder,
            $"site is the file mutated. before, after, diag and a control's texts name files holding the text, so a mutation "
            + "may span lines: each read as its file holds it, less a UTF-8 byte order mark at its start and one line ending "
            + "at its end, and where the site ends its lines otherwise, given the site's line endings, so one registry serves "
            + "a checkout with either. red kind is "
            + $"{MutationRegistryParser.TestRed} or {MutationRegistryParser.BuildRed}, declared and never inferred. target is the "
            + "build target the mutation is built as, and runner the test target whose binary runs, found through the build's "
            + "own manifest; cases is how many cases it runs, skipped ones included. A case is named as its report names it, "
            + "classname.name: Suite.Case for GoogleTest. A BUILD-RED arm runs nothing: its runner is "
            + $"'{MutationRegistryParser.NoRunner}', its cases 0 and its diag {MutationRegistryParser.PairedControlToken}, and "
            + "it has exactly one B row, no C or G row, and no M row but for its own file, which its paired control rewrites "
            + "whole. A TEST-RED arm has at least one C row and no B row.");
        builder.AppendLine();
        AppendWrapped(
            builder,
            "A mutant that is several places of one file, apart from each other, is one arm: an M row naming a file its arm "
            + "already mutates - its own, or another M row's - adds a text to that file, as many as the mutant has, spelt as "
            + "the arm's first row for the file spells it. Every text of a file is replaced together, as one edit of it: each "
            + "must stand exactly once in the file as the tree holds it - never as another of them left it - and differ from "
            + "what replaces it, the file is written once and put back whole, checked once against the tree's, and what "
            + "depends on it is witnessed rebuilt as for one text. Declared as one text spanning every site, such a mutant "
            + "would break at any edit of a line between them. A further site in another file stays an M row naming that file.");
        builder.AppendLine();
        AppendWrapped(
            builder,
            "The registry is read as UTF-8 and nothing else - by a sweep, a listing and check-anchor-arms alike, as its merge and "
            + "its history are - a byte order mark at its start no part of its first row; one that is not UTF-8, or a line of which "
            + $"opens with a byte order mark, is refused ({HarnessExit.ConfigInvalid}), naming why. "
            + $"The whole registry is read before any host is touched, and every problem is listed with its line ({HarnessExit.ConfigInvalid}): "
            + "a row of no kind it reads; an arm id outside [A-Za-z0-9_-], or one another reads as ignoring case; a path outside "
            + "[A-Za-z0-9_./-], absolute, climbing out with '..', ending in '/', or with an empty or '.' segment; a target or "
            + "runner outside [A-Za-z0-9_.+-]; a row naming an arm no A row above it declares; a case both red and green, or "
            + "declared twice; an M row naming a file its arm already mutates under another spelling, as the tree's own file "
            + "system compares their names; two texts of one arm that overlap in one file as the tree holds it - one "
            + "lying across or within the other, or the same text cited twice; an S row naming neither a leg nor a leg set; "
            + "a text no row cites in textDirectory, a mutation nobody drives - but for a file there a sync withholds, which "
            + "no copy of the tree holds; a cited text that is not there, or that a "
            + "sync withholds from every copy of the tree - one sync.neverTransfer, sync.exclude or worktrees.root "
            + "covers, or git ignores - as the registry itself is refused where git ignores it; and a "
            + "before-text, a control's before-text or a diagnostic holding nothing, which would match everywhere, or be said "
            + "by every run. Rows R, X, I, F and T are refused, each naming what "
            + "took its place: the leg's own tree, project and variant (R); sync's exclusions (X); the variant's configure "
            + "(I); the dependency sources the leg's own build fetched, read from its CMake cache (F); and ninja's records of "
            + "every object that depends on a site (T).");
        builder.AppendLine();
        AppendWrapped(
            builder,
            $"--arms names the arms to drive, as --legs names legs, and an unknown one is refused ({HarnessExit.UsageError}) - "
            + "once the registry itself can be swept: its own problems are listed first, every one of them, whatever --arms "
            + "names. "
            + "On each leg the arms its S row does not name, and those --arms does not, are skipped-not-selected; an arm "
            + "selected whose S row names no selected leg is named in a warning before the sweep starts, and a sweep whose "
            + $"every selected arm is such a one is refused ({HarnessExit.UsageError}), naming each and where it runs: it would drive "
            + "no arm, and pass having proved nothing. Only a leg built by CMake with the Ninja generator can be swept: "
            + "ninja's own records say which objects a mutation rebuilt. "
            + "--list sweeps nothing (see 'What holds, and what is pending'), so it is refused with --pending, --self-test, "
            + $"--force-lock, --use-staged, --here and the hidden --held ({HarnessExit.UsageError}), each in one sentence naming both; and --pending "
            + "is refused with --self-test, of which nothing is ever recorded.");
        builder.AppendLine();
        builder.AppendLine("Workers");
        builder.AppendLine();
        AppendWrapped(
            builder,
            $"Each leg is swept in worker copies of its tree, kept in it as <tree>/{HarnessLayout.MutationsDirectoryName}/<key>{WorkerFamily.OwnMark}-<n>, numbered "
            + "from 1: as many as workers, never more than the leg has arms to drive, in a WSL distribution as anywhere, each made "
            + "and each arm driven under a unit of the leg's admission, so as many run as the machine's room and slots allow. The key "
            + $"is the first {VariantKey.CopyKeyLength} hexadecimal digits of the SHA-256 of the leg's variant, as its build "
            + $"directory is named, and a worker builds in <worker>/{VariantKey.BuildRootName}/<key>, never in the directory "
            + "the variant's name gives the tree's own build: its first worker adds "
            + $"{ReportText.Counted(WorkerFamily.AddedToItsTreesPath, "character")} to its tree's path, and its build directory "
            + $"{ReportText.Counted(WorkerFamily.BuildBelowItsWorker, "character")} below that, "
            + "whatever the variant is called, since a worker is built as deep as the tree and a machine whose paths are bounded "
            + "has no more to spare. Which variant a worker builds is what its build records, and a build of one variant where "
            + "that record names another - two whose names come to one key share their workers - starts from clean; the lines "
            + "of a sweep and of a clean say which leg, and which variant, a worker is. Each is a repository "
            + $"of its own, as every copy a sync makes is, and init keeps {HarnessLayout.MutationsDirectoryName} in git and "
            + "everything made in it out: a sweep in a tree where git does not ignore what it would make there is refused "
            + $"({HarnessExit.Refused}), naming init, with nothing made - asked on the machine that makes the workers, so a "
            + "host answers for its own copy. Before every "
            + "sweep each is synced again from one reading of the tree, by content - so its build stays warm, and a site a "
            + "killed sweep left mutated is put back - and built whole before it drives an arm: that sync withholds the "
            + $"worker's own build directory, {VariantKey.BuildRootName}/<key>, itself, whatever sync.neverTransfer says, and sync.maxDeleteFraction judges "
            + "only what mirrors the tree the sweep read. Of the tree itself a sweep "
            + $"reads every file and writes none: its workers, and the claims on them, are in {HarnessLayout.MutationsDirectoryName}, "
            + $"and its run's records in {HarnessLayout.DirectoryName}, as every run's are. On this machine a sweep makes no copy "
            + "of its own even where the leg's localCopy "
            + $"or the repository's default ({HarnessLayout.LegsDirectoryName}) says one - its workers are already copies of "
            + "one reading - and reads the dependency sources the leg's build fetched from where the configuration places "
            + "that build: the copy's build directory, where the leg works in one. On another machine it works in a copy of "
            + $"its own there, <host's copy>/{HarnessLayout.LegsDirectoryName}/<key>, with its workers in it (see the locks below).");
        builder.AppendLine();
        AppendWrapped(
            builder,
            "A worker builds the dependency sources the leg's own build fetched, read from its CMake cache "
            + $"({BuildDirectoryGuard.FetchContentSource}<NAME>), and never fetches where every one was found. They are read "
            + "once, as the tree is, and each worker is given its own copy by content, kept in its "
            + $"{HarnessLayout.DependencySourcesDirectoryRelative}/<name> - their files, never a clone's own .git, nor a link - "
            + "and configured with it: so a clean of the leg, or a build of it that fetches again, while a sweep runs "
            + "changes nothing a worker builds. Sources the tree itself holds are the worker's own copy of them, and what "
            + "the project's cacheVars point at is the project's to say. A dependency not found is fetched by the worker "
            + "as the leg would fetch it.");
        builder.AppendLine();
        AppendWrapped(
            builder,
            "A sweep runs the workers whose copy and build fit the room left - a copy being the tree's files and what git "
            + "keeps there of its history, and a build of the variant coming to the leg's "
            + "buildSpaceGiB where it declares one, else to what the leg's own build last recorded, else to the most any "
            + "other tree of the repository on its machine recorded of it - and whose build stays within the path limit, "
            + "reckoned as a worktree's is; where not even the first does, the leg is skipped-unavailable, saying why, and "
            + "one that runs fewer says how many of those it wanted, and what keeps each of the rest out. A worker is claimed while a sweep uses it, in <worker>.claim.json beside it in "
            + $"{HarnessLayout.MutationsDirectoryName}, and "
            + "a claim whose sweep died is released and said. Workers an earlier sweep left beyond the count workers allows "
            + "are removed before the sweep plans, so lowering it frees their room, and a directory under a worker's name "
            + "that no sync made is said and left.");
        builder.AppendLine();
        AppendWrapped(
            builder,
            "The workers go with their tree. clean removes a leg's with its build directory - its self-test's too, and "
            + "those of a variant no leg of that host and tree builds any more. delete-worktree and delete-agent "
            + "remove the workers kept in the worktree first, here and in each host's copy of it; a worker a "
            + "sweep still running holds keeps its worktree, or its host's copy, until the sweep has ended, and one that "
            + "cannot be removed keeps it as a failure until that is put right - or --force "
            + "deletes the worktree with that worker in it. A directory there under a worker's name that no sync made goes "
            + "with the worktree, or the host's copy, as everything else in it does, and is said. No copy of a tree carries "
            + "its workers, and a sync into a host's "
            + "copy never deletes that copy's own. Workers an earlier version of this tool kept beside a tree, as "
            + $"<tree>.mutation-<key><mark>-<n>, or in it, as <tree>/{HarnessLayout.FormerMutationsDirectoryName}/<key><mark>-<n> - "
            + $"which init keeps out of git whole, with no placeholder, and no sync sends or deletes - are no workers to a "
            + "sweep: nothing counts one, and clean --former-workers removes them - beside and in each tree of the repository "
            + "on each machine a selected leg runs on, here and on hosts, and beside each one deleted since, a dry run listing "
            + "each with its size - keeping one a sweep of that version still claims by the claim file beside it; those in a "
            + "worktree, or a host's copy, also go with it as its workers do. Each beside a tree holds a .git of its own that "
            + "git records no worktree at, so until then one below the "
            + "worktrees root is said as any such directory: list-worktree warns of it, and delete-orchestrator, and "
            + $"delete-worktree of an orchestrator's directory with or without --force, refuse ({HarnessExit.Refused}) while one "
            + "is below it.");
        builder.AppendLine();
        builder.AppendLine("An arm's turn");
        builder.AppendLine();
        AppendWrapped(
            builder,
            "Each test binary's arms wait for its unmutated control, built and run once on the leg: one that does not pass "
            + "- a build that does not pass, a red case, no report, a failing exit, a hang - decides the leg's own verdict "
            + "and stops each of its arms. "
            + "Its run bounds theirs: a mutated run may take runTimeFactor times as long, never less than it and its start "
            + "and exit again - what it took beyond the time its report says its cases took, measured on this machine, the "
            + "whole run where the report says nothing of their time - nor than "
            + $"{PristineJudge.LeastBound.TotalSeconds.ToString(CultureInfo.InvariantCulture)} seconds, the least a run is "
            + "waited for, past what a busy machine holds a short run back by; and is stopped as unattributed past that, "
            + "naming which of the three set its bound. Each arm is admitted as a unit of its own where its machine "
            + "declares admission ('help admission'), pre-flighted, mutated - its sites dated past the worker's last build "
            + "and the clock by the granularity the file system there dates files at, measured there, and the build waiting, "
            + "told, until the clock has passed that date, however far ahead of the clock the last build's files are "
            + "- built with its runner beside its target, witnessed rebuilt from ninja's log, then run whole or paired with "
            + "its control. Every site is put back and checked by its hash; a site that cannot be put back poisons the arm "
            + "and retires its worker, and the other workers go on. What the sweep timed across a clock step or a host sleep "
            + "is said on the leg's line, among why its timings are suspect, and changes no verdict: an arm's run and the "
            + "unmutated run that bounds it, however each ended, and what each of its builds says of its own - a worker's "
            + "rebuilt from clean with it.");
        builder.AppendLine();
        AppendWrapped(
            builder,
            "Whatever fails is kept to what it failed in. A worker that cannot be made is retired alone, saying why, and "
            + "the other workers drive every arm. An arm whose driving ends in a failure is given the verdict that "
            + "failure comes to - poisoned where nobody named it - and a binary whose control cannot be built and run stops "
            + "its own arms and no other. Once its machine does not admit one arm, the sweep asks it for no other: every "
            + "arm left is not-admitted at once, naming the arm refused, rather than each waiting as long again.");
        builder.AppendLine();
        AppendWrapped(
            builder,
            "A sweep stopped part way puts every site back and still reports the leg: each arm judged by then, and the "
            + $"arms it was driving and those no worker reached stopped. The run ends interrupted ({HarnessExit.Cancelled}). A refusal "
            + "of the run raised inside a sweep ends the run with its own exit code, after the leg's line.");
        builder.AppendLine();
        AppendWrapped(
            builder,
            $"A sweep none of whose selected legs can run is incomplete, exit {MutationService.NothingRuns}, saying why each cannot - "
            + $"never the {LegsExit.Unavailable} a build, a test and a run give there, which of a sweep is an arm violated.");
        builder.AppendLine();
        AppendWrapped(
            builder,
            "A sweep of a leg takes a lock of its own, keyed by its workers: it refuses another sweep of the leg's variant, "
            + "refused-locked, and never holds off a build, test or sync of the leg. --force-lock takes it, and a worker "
            + "another live sweep claims.");
        builder.AppendLine();
        AppendWrapped(
            builder,
            "On another machine a sweep never shares the host's copy of the tree, which every command whose leg lands there "
            + "syncs, and a test holds for as long as it runs: it works in a copy of its own, kept in that copy's "
            + $"{HarnessLayout.LegsDirectoryName} as <host's copy>/{HarnessLayout.LegsDirectoryName}/<key>, synced from this machine's reading of "
            + "the tree as the host's copy is, and holds it whole. So a test or build of the host's copy never holds a sweep "
            + "off, nor a sweep them, in either direction; a second sweep of the variant there is refused-locked before its "
            + "sync rewrites the copy; and where the host's copy is not there yet, or a sync of it did not finish, the tree is synced to it first, as any "
            + "command would, so every command finds the tree there - once a run, refused-locked while another run syncs it. "
            + "Its first sweep builds the variant there from clean. clean of the leg "
            + "removes the copy with its workers, and is refused-locked here while a sweep of this machine's holds it; "
            + "delete-worktree and delete-agent remove it with the host's copy, a worker a live sweep claims keeping that copy.");
        builder.AppendLine();
        builder.AppendLine("How a runner is started");
        builder.AppendLine();
        AppendWrapped(
            builder,
            "A runner is started by the sweep itself, never by ctest. It is the program the runner's target builds, in the "
            + "worker's build directory, given mutations.reportArgs and nothing else. It starts in the directory the leg's "
            + "tests start in - test.workingDirectory, its names filled in for the worker, or the worker's root where the "
            + "settings name none - with the environment they start with: what the leg's host declares, the developer "
            + "environment its toolchain names, and test.env over both, the host's program directories added to its PATH.");
        builder.AppendLine();
        AppendWrapped(
            builder,
            "Nothing else of the test settings is applied: not the runner, its args, a filter, an exclusion, a label, the "
            + "core count or the success pattern. And nothing of a test's ctest entry is: not the add_test command line, "
            + "its launcher or its arguments; not ENVIRONMENT or ENVIRONMENT_MODIFICATION; not WORKING_DIRECTORY; not a "
            + "fixture (FIXTURES_REQUIRED, FIXTURES_SETUP, FIXTURES_CLEANUP), DEPENDS, REQUIRED_FILES, RESOURCE_LOCK or "
            + "RUN_SERIAL; not TIMEOUT - a mutated run is bounded by runTimeFactor, an unmutated one by "
            + "defaults.stallSeconds alone; and not what ctest reads a result by - WILL_FAIL, "
            + "PASS_REGULAR_EXPRESSION, FAIL_REGULAR_EXPRESSION, SKIP_REGULAR_EXPRESSION, SKIP_RETURN_CODE, DISABLED. A "
            + "case is red where the binary's own report says so.");
        builder.AppendLine();
        AppendWrapped(
            builder,
            "So a test that is green under ctest for what ctest sets up around it can be red in its binary's unmutated "
            + "run, which stops every arm of that binary, each arm's line naming the run's log. Where the leg's tests run "
            + "through ctest, the binary's line then names what ctest registers for the red cases that a sweep does not "
            + "apply, read from ctest's own description (ctest --show-only=json-v1), waited for to its end and said every five minutes it lasts; what ctest "
            + "could not say is said as that and changes no verdict, and where the leg's tests run through no ctest "
            + "nothing is asked. A sweep starts the binary itself, never through ctest, and runs no fixture.");
        builder.AppendLine();
        AppendWrapped(
            builder,
            "A mutated build is a build of its own. Each mutation is configured and built again in its worker, so "
            + "whatever a project derives from its sources as it builds - a stamp that digests them, a version header, a "
            + "cache named for either - is another in every mutant than in the unmutated build. A case that depends on "
            + "such state is red under every arm of its runner, and no arm declares it.");
        builder.AppendLine();
        builder.AppendLine("What holds, and what is pending");
        builder.AppendLine();
        AppendWrapped(
            builder,
            "A sweep records each arm's verdict on each leg with what it was judged on. A recorded passed holds where what "
            + "the arm would be judged on now, computed in full on the leg's machine, is what the record says; every other "
            + "arm is pending.");
        builder.AppendLine();
        AppendWrapped(
            builder,
            "An arm's key is the SHA-256 of what it was judged on, read from the first worker made before it drove an arm: "
            + "the judge's revision; the rows the sweep and the judge read of it - never a why, a comment, a line's number, "
            + "the letter case of its id or its S row; what the sweep's reading of the tree holds of each text it cites "
            + "and each site it mutates, found as the tree's file system compares names; what each target its build builds "
            + "is built from - its build lines, every file of "
            + "the tree they read, the dependency sources the worker was given, what the worker's configure made and what "
            + "they read outside the tree, the worker's own path told wherever it stands; the compilers CMake identified; "
            + "the leg's developer environment; what of the configuration the build, the run and the judge are given; "
            + "what its runner reads at run time, as runReads names it, each file by the path the reading spells, so one "
            + "renamed by case alone moves the key; and the host. No part is a commit, a date, a run, a "
            + "worker or a tree: the same content judged again, in another worker or another tree, comes to the same key. An "
            + "arm a part of whose key could not be read has none. A file a test binary reads at run time that runReads does "
            + "not name is read by no part of a key: editing it moves none, and a recorded passed it would change still holds. "
            + "Every TEST-RED arm is judged on what its test binary is built from, so after most changes under a large test "
            + "binary's sources every arm it runs is pending. What a target is built from is what ninja's own records name, "
            + "so two changes move no key until the object that reads them is compiled again: a file that stands earlier on "
            + "a search path than the one an unchanged #include found, and a file a command reads without declaring it. "
            + "ninja rebuilds nothing for either, and a sweep's workers are built by the same records, so a sweep judges the "
            + "stale object as build and test would; a worker built from clean - after a clean of the leg - sees both.");
        builder.AppendLine();
        AppendWrapped(
            builder,
            "Each verdict is recorded as its arm ends - one reached by observing the arm, and an arm stopped because its "
            + "runner's unmutated control did not pass - so a sweep stopped part way keeps what it had judged. Never an arm "
            + "its machine did not admit, one the sweep's own stop or a refusal of the run ended, one no worker reached, a "
            + "skipped one, or anything of a self-test: nothing was observed of those, and the record keeps what it held. "
            + $"The record is {HarnessLayout.MutationRecordDirectoryRelative} in the main checkout, whichever tree swept: "
            + "<leg>.jsonl for each leg, a log each verdict is appended to as a line, and the tables its entries name in "
            + $"{ArmVerdictRecord.TablesDirectoryName}/<leg>/, each written once under its digest and kept. The <leg>.json an "
            + "earlier version kept a leg's verdicts in is read once, where no log is there yet, carried into the log whole, "
            + "and never written again. It ignores itself, and no sync "
            + "carries it: a host's copy keeps none, so a leg swept there tells this machine instead - each verdict as a "
            + "line on the host's standard error that this machine reads as the arm ends there and never relays, a sweep "
            + "stopped part way or cut off keeping what it had judged - and a sweep typed by hand in a copy the harness "
            + "synced records nothing, saying so once as a warning: under --pending it then holds nothing, and sweeps every "
            + "selected arm. A later verdict on a "
            + "key replaces the arm's entry on that key, whatever either was, and a verdict with no key the arm's entry with "
            + "none, by its place in the log, never by its date; an arm keeps every key it was judged on, however many "
            + "sweeps there were, each a change that appends what it adds and reads only what was appended since. Two sweeps "
            + "recording at once lose neither's verdicts. clean, a deleted worktree and a "
            + "removed host copy leave it as it was; deleting it is the one way to forget it, and every arm is then pending. "
            + $"A record that cannot be read, or that a newer {ToolPackage.Id} wrote, is never written: a sweep of that leg "
            + "says so once, as a warning naming the file and on the leg's line, and records none. A sweep this machine "
            + "records names the record once, in its lines and in --json, as a listing does.");
        builder.AppendLine();
        AppendWrapped(
            builder,
            "check-mutations --list says, without sweeping, each selected arm's last verdict on each selected leg its S row "
            + "names, and whether anything it was judged on that this machine reads has moved since. It reads and verifies "
            + $"the registry as a sweep does, every problem listed ({HarnessExit.ConfigInvalid}), resolves --legs and --arms as a "
            + $"sweep does ({HarnessExit.UsageError}), and reads each leg's tree once and each leg's record; it asks no host, takes "
            + "no lock, begins no run and writes nothing, and needs nothing only a sweep needs - report arguments, a leg "
            + "built with Ninja, a selection that drives an arm. A row gives the verdict shown - the newest entry nothing read "
            + "here moved for, else the newest - its run, when, and what the arm is now. It is pending where its record "
            + $"cannot be read or a newer {ToolPackage.Id} wrote it, its leg cannot be swept, it was never swept on the leg, the "
            + "verdict shown is no passed, it was swept with no key, or the judge's revision, its rows, a text it cites, a site "
            + "it mutates, a file of the tree its targets are built from, a file its runner reads at run time, what of the "
            + "configuration its build, its run or its "
            + "judge is given, or the host its leg names changed: the row says the first that applies, in that order, and "
            + "--json every one. A leg that names no host is read as though swept where its verdict was judged - which host "
            + "a sweep would place it on is not read here - so the environment its build is given is the one that host "
            + "declares. Every other arm is unmoved here, which is never to say it holds: a listing reads the rows, "
            + "the texts, the tree's files and the settings, never how a target is built, a file its build makes, the "
            + "dependency sources it fetched, a file outside the tree it reads, the compilers or the developer environment - "
            + "only a sweep on the leg's machine reads those. An arm unmoved here whose build is generated from a file that "
            + "moved since, or whose configure the configuration now gives otherwise, says so on its row. A leg that cannot "
            + "be swept is said once, above the rows, and never refused; a record that "
            + $"cannot be read is said as a warning. A listing exits {HarnessExit.Success} whatever is pending - it is no gate - "
            + $"and otherwise as a sweep would: {HarnessExit.CommandFailed} where git could not say what a tree ignores, and "
            + $"{LegExit.InputsMoved} where a tree changed while it was read. --json prints the listing as one JSON document. "
            + StoppedDocument);
        builder.AppendLine();
        AppendWrapped(
            builder,
            "check-mutations --pending drives, on each leg, only the arms with no recorded passed that holds; --arms narrows "
            + "the arms it weighs, and without --pending every selected arm is driven, as ever, and recorded. On the leg's "
            + "machine the first worker is made alone - claimed, synced, built whole - and the key of every arm is read from "
            + "its build before any arm is mutated. An arm whose key is a recorded passed's holds: it is not driven, and is "
            + "skipped-not-selected on that leg, its detail naming the run whose passed holds and when, and the record notes "
            + "that this run verified it. Every other arm is pending, and is swept as ever, in as many workers beyond the "
            + "first as the pending arms need; its progress and --json say the first reason it was pending - a listing's, or "
            + "one only a sweep reads: how a target is built, a file joined or left what it is built from, a file its build "
            + "makes, the dependency sources it fetched or a file outside the tree it reads moved, its compiler or its "
            + "developer environment changed, or what it would be judged on cannot be read now - and, where nothing else "
            + "says how its key moved, that it moved. A host sweeping one leg "
            + "of a run under --pending is given what the record holds of that leg's own arms - each arm's newest entry "
            + "and every recorded passed, as bytes in a hidden option of that leg alone - and decides held and pending "
            + "there, from its own reading of its copy and its own worker; given --pending and no record it holds nothing "
            + "and sweeps every selected arm. The leg's line counts the "
            + "arms swept by verdict, those that held, and those not selected. With nothing pending no other worker is made "
            + "and the leg passes on its own verdict, saying nothing is pending: each passed was verified there, and the tree "
            + $"asks nothing more of it. A sweep under --pending exits as a sweep does - {HarnessExit.Success} where nothing is "
            + "pending - and a first worker that cannot be made is the leg's failure, as any worker's is where none is made, "
            + "and verifies nothing.");
        builder.AppendLine();
        builder.AppendLine("Records");
        builder.AppendLine();
        AppendWrapped(
            builder,
            $"Each arm's records are in <run>/<leg>/{MutationRecords.ArmsDirectory}/<arm>/: {MutationRecords.ArmRecordFileName}, its verdict as "
            + "the ledger carries it, beside the logs of its build and its run, and of a paired control's build in "
            + $"{MutationRecords.PairedControlDirectory}/. Each control's are in <run>/<leg>/{MutationRecords.ControlsDirectory}/<runner>/, and "
            + $"each worker's whole build in <run>/<leg>/{MutationRecords.WorkersDirectory}/<n>/. A leg's line counts its arms by verdict; "
            + "ARMS, below the table, names each arm selected that did not pass, and why; --json carries every arm beneath "
            + "its leg: "
            + "arm, verdict, failure, detail, durationSeconds, worker, cases, declaredCases, reds, declaredReds, records and key; "
            + "under --pending an arm it left out has held - the run and the time of the passed that holds - and an arm it swept "
            + "has pending - the first reason, its code and the words it is said in - and each leg has pendingSweep: how many "
            + "of its arms held, and how many were swept; and each control that did not pass is beneath the leg as controls: "
            + "its runner, verdict, detail, reds, records, and what ctest registered for its red cases - how they were "
            + "matched, and what of the tests it names a sweep does not apply.");
        builder.AppendLine();
        builder.AppendLine("Self-test");
        builder.AppendLine();
        AppendWrapped(
            builder,
            "check-mutations --self-test sweeps the fixture this tool carries, in place of the repository's registry, "
            + "which it does not need: a CMake library, a test binary that writes its own JUnit report, and eight arms, one "
            + "to each verdict an arm's design can reach on any machine - passed, violated, survived, unattributed and "
            + "failed - with a second that passes as the other red kind, a third whose mutation is coupled across two "
            + "files and a fourth whose mutation is two sites of one file. It is built as each selected leg builds, with the leg's toolchain, "
            + "configuration and sanitizer, and each arm is held to the verdict it is designed to reach: one that reaches "
            + "it passed, saying so, and one that reaches another is violated, naming both - a defect of this tool's with "
            + "that compiler, never the fixture's. --arms names the fixture's arms.");
        builder.AppendLine();
        AppendWrapped(
            builder,
            "Every worker a self-test makes reads each arm's key from its own build, and the self-test compares them arm by "
            + "arm, so it proves on the leg's own toolchain that two workers come to one key: a key two workers read "
            + "otherwise, or one a worker could not read, fails the leg as violated, naming the arm, the workers and the "
            + "part that differs; a self-test with one worker says it compared its keys with no other's. A self-test's "
            + "keys are on no arm's line.");
        builder.AppendLine();
        AppendWrapped(
            builder,
            $"The fixture is written where this tool keeps its own data, <user data>/{ToolPackage.Command}/{MutationFixture.DirectoryName} - "
            + $"{MutationFixture.DirectoryName} in the directory {UserState.Variable} names, where it names one - only where it differs. "
            + "Its workers are kept in the leg's own tree, as a sweep's are, in a family of "
            + $"their own - <tree>/{HarnessLayout.MutationsDirectoryName}/<key>{WorkerFamily.SelfTestMark}-<n>, a few megabytes each - so they are "
            + "that repository's: counted by its room, built by its toolchain, and removed by a clean of the leg, and "
            + "with its worktree or its host's copy. A self-test is placed by no room a build of the leg's tree needs, "
            + "its workers' paths are reckoned by the fixture's own longest, and a leg on a host is self-tested there "
            + "with the fixture that host's DssHarness carries.");
        builder.AppendLine();
        builder.AppendLine("Verdicts");
        builder.AppendLine();
        builder.AppendLine("Each arm reaches one, and a leg's is the worst of its own and its arms':");
        builder.AppendLine();
        builder.AppendLine("  passed        the arm held as declared");
        builder.AppendLine("  failed        its build failed at a step that is no object depending on its site - a");
        builder.AppendLine("                link, another object - or named no step that failed");
        builder.AppendLine("  violated      its declaration did not hold: a site or a cited text that is not there, or");
        builder.AppendLine("                a site spelt otherwise than the tree spells it, or that is no file the");
        builder.AppendLine("                sweep's reading of the tree holds; its before-text not in its");
        builder.AppendLine("                site exactly once, or replaced by itself; its target or its runner not");
        builder.AppendLine("                built, or no object they build depending on a site; other cases red than");
        builder.AppendLine("                its C rows, another number of cases run, a G row's case not run, or its");
        builder.AppendLine("                diagnostic not said; a TEST-RED mutation that does not compile; or a");
        builder.AppendLine("                BUILD-RED one that does, or whose control does not");
        builder.AppendLine("  survived      the mutation built and ran, and no case failed");
        builder.AppendLine("  unattributed  the run failed and nothing ties that to a case: no report, one that is no");
        builder.AppendLine("                JUnit report - its line says why - a failing exit whose report names no");
        builder.AppendLine("                failing case, or a run past its bound, or silent for");
        builder.AppendLine("                defaults.stallSeconds, stopped as hung");
        builder.AppendLine("  unwitnessed   the build passed and an object that depends on a site was not rebuilt");
        builder.AppendLine("  stopped       not driven to a verdict: the sweep was stopped, or ended by a refusal of the");
        builder.AppendLine("                run, while it was driven or before; its own build, or its paired control's,");
        builder.AppendLine("                was stopped from outside; no worker was left to run it; or the unmutated");
        builder.AppendLine("                run of its binary did not pass");
        builder.AppendLine("  not-admitted  its machine did not admit it, or an arm of the sweep before it");
        builder.AppendLine("  poisoned      a site could not be put back as it was, or a failure nobody named ended");
        builder.AppendLine("                its driving");
        builder.AppendLine();
        AppendWrapped(
            builder,
            "An arm reaches others where nothing of its own decides: skipped-not-selected where --arms or its S row leaves "
            + "it out of a leg, or --pending leaves it out, its passed holding; skipped-unavailable, with every arm of its leg, where no worker fits the room left or the "
            + "path limit; unmeasured where ninja's log could not be read around its build, or the report its run wrote "
            + "could not be read from its file, which is no finding about the binary; whatever a guard of its "
            + "build reaches, as a leg's build does - inputs-moved, contended; and whatever a failure that ends its driving "
            + "comes to, as a leg's does - skipped-tool-missing for a tool that is not there, failed for a program that "
            + "will not start.");
        builder.AppendLine();
        AppendWrapped(
            builder,
            "A repository moving its sweep from a mutation harness of its own, which exited with the codes on the left, "
            + "reads these instead:");
        builder.AppendLine();
        builder.AppendLine($"  0  as declared                                {HarnessExit.Success,3}  passed");
        builder.AppendLine($"  1  a violation, a missing diagnostic included {LegExit.Violated,3}  violated");
        builder.AppendLine($"  5  nothing reddened                           {LegExit.Survived,3}  survived");
        builder.AppendLine($"  6  a red no case name could be read for       {LegExit.Unattributed,3}  unattributed");
        builder.AppendLine($"  7  a build that passed missed a declared unit {LegExit.Unwitnessed,3}  unwitnessed");
        builder.AppendLine($"  8  the build failed upstream of the unit      {HarnessExit.CommandFailed,3}  failed");
        builder.AppendLine($"  4  the harness broke                          {HarnessExit.InternalError,3}  poisoned");
        builder.AppendLine($"  4  an arm not driven                               stopped: the run exits {HarnessExit.Incomplete}");
        builder.AppendLine("                                                      unless something failed");
        builder.AppendLine($"  2  usage, or the registry not valid           {HarnessExit.UsageError,3}  usage, or {HarnessExit.ConfigInvalid} not valid");
        builder.AppendLine($"     a live owner                               {HarnessExit.Refused,3}  refused-locked");

        return builder.ToString();
    }
}
