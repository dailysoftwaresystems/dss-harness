using DssHarness.Core.Ci;
using DssHarness.Core.Results;

namespace DssHarness.Tests;

/// <summary>
/// A CI job's digest is made of what the forge keeps of it - its annotations and its log - and every path in it is placed
/// against the job's own workspace on its runner, read in the form the runner wrote it: never against this machine's
/// tree, and never by this machine's rules, so the same job reads the same on every machine.
/// </summary>
public sealed class CiDigestTests
{
    /// <summary>
    /// A frame a test on a runner printed is in the tree where its file lies under the job's workspace - the runner's
    /// <c>&lt;work&gt;/&lt;repository&gt;/&lt;repository&gt;</c>, a Windows runner's or a Linux one's alike - and is named by
    /// its path relative to it, which a reader opens in its own copy of the tree; the runtime's own frames, a file of
    /// another repository's workspace and a frame naming no file are in none. The answer is the same on every machine
    /// that reads it.
    /// </summary>
    [Fact]
    public void AJobsFrames_AreInTheTree_WhereTheyLieUnderItsWorkspace_ReadAsTheRunnerWroteThem()
    {
        var log = string.Join(
            "\n",
            "2026-11-23T00:40:01.0000000Z System.InvalidOperationException: the rate is wrong",
            @"2026-11-23T00:40:01.0000000Z    at App.Tests.RatesTests.Holds() in D:\a\app\app\tests\RatesTests.cs:line 45",
            "2026-11-23T00:40:01.0000000Z    at System.RuntimeMethodHandle.InvokeMethod(Object target)",
            "2026-11-23T00:40:01.0000000Z    at App.Core.Rates.Of() in /home/runner/work/app/app/src/Rates.cs:line 9",
            "2026-11-23T00:40:01.0000000Z    at Other.Lib.Call() in /home/runner/work/other/other/src/Lib.cs:line 3",
            "2026-11-23T00:40:01.0000000Z    at System.Private.CoreLib.Run() in /usr/share/dotnet/src/Run.cs:line 1") + "\n";

        var digest = CiDigest.Of(log, [], "app", said => said, [])!;

        var failure = Assert.Single(digest.Failures);
        var frames = failure.Exception!.Stack;

        Assert.Equal(
            [("tests/RatesTests.cs", true), (null, false), ("src/Rates.cs", true), ("/home/runner/work/other/other/src/Lib.cs", false), ("/usr/share/dotnet/src/Run.cs", false)],
            frames.Select(frame => (frame.File, frame.InTree)));

        // The failure's place is named as a reader opens it too.
        Assert.Equal("tests/RatesTests.cs", failure.File);
    }

    /// <summary>
    /// Where the job's repository is not known, no rooted path is placed in its tree - the runner's workspace cannot be
    /// told - and a path a tool wrote relative to the tree it ran in still is.
    /// </summary>
    [Fact]
    public void WhereTheRepositoryIsNotKnown_OnlyARelativePathIsInTheTree()
    {
        var log = string.Join(
            "\n",
            "System.InvalidOperationException: the rate is wrong",
            "   at App.Core.Rates.Of() in /home/runner/work/app/app/src/Rates.cs:line 9",
            "   at App.Core.Rates.Read() in src/Read.cs:line 4") + "\n";

        var frames = CiDigest.Of(log, [], repository: null, said => said, [])!.Failures[0].Exception!.Stack;

        Assert.Equal([("/home/runner/work/app/app/src/Rates.cs", false), ("src/Read.cs", true)], frames.Select(frame => (frame.File, frame.InTree)));
    }

    /// <summary>
    /// A job's log is read as GitHub writes it, every line numbered as the kept file numbers it: the byte order mark of
    /// its first line, each line's timestamp, a group marker's line - its title with it - the runner's echo of a step's
    /// script, an <c>::error::</c> it echoes among it, and an annotation's marker taken off, a group's line and an echoed
    /// one left blank rather than dropped, so a line after it keeps its number.
    /// </summary>
    [Fact]
    public void AJobsLog_IsReadAsGitHubWritesIt_EveryLineKeepingItsNumber()
    {
        var log = "\uFEFF" + string.Join(
            "\n",
            "2026-11-23T16:48:03.9764568Z Current runner version: '2.338.2'",
            "2026-11-23T16:48:04.1883660Z ##[group]Run set -euo pipefail",
            "2026-11-23T16:48:04.1909614Z \u001b[36;1m  echo \"::error::A fork cannot post the status.\"\u001b[0m",
            "2026-11-23T16:48:04.2255994Z ##[endgroup]",
            "2026-11-23T16:48:05.1483402Z ##[error]ci-check is failing: test failure.",
            "2026-11-23T16:48:05.1504036Z ##[warning]Process completed with exit code 1.") + "\n";

        Assert.Equal(
            [
                (1, "Current runner version: '2.338.2'", false),
                (2, string.Empty, false),
                (3, string.Empty, false),
                (4, string.Empty, false),
                (5, "ci-check is failing: test failure.", true),
                (6, "Process completed with exit code 1.", true),
            ],
            CiLog.Of(log).Select(line => (line.Line.Number, line.Line.Text, line.Marked)));
    }

    /// <summary>
    /// The line the runner ends a failed step with, under its error marker, says the code the step's process exited with;
    /// no other line says one - the same sentence under another marker, or a program's own echo of it with none.
    /// </summary>
    [Fact]
    public void TheLineTheRunnerEndsAFailedStepWith_SaysTheCodeItsProcessExitedWith()
    {
        var log = string.Join(
            "\n",
            "2026-11-23T16:48:04.2436858Z test: failure, package: skipped, so ci-check is failure",
            "2026-11-23T16:48:05.1483402Z ##[error]ci-check is failing: test failure, package skipped.",
            "2026-11-23T16:48:05.1504036Z ##[error]Process completed with exit code 1.",
            "2026-11-23T16:48:05.1504036Z ##[error]Process completed with exit code 127.",
            "2026-11-23T16:48:05.1504036Z ##[warning]Process completed with exit code 1.",
            "2026-11-23T16:48:05.1504036Z Process completed with exit code 1.",
            "2026-11-23T16:48:05.1696373Z Cleaning up orphan processes") + "\n";

        Assert.Equal(
            [(1, (int?)null), (2, null), (3, 1), (4, 127), (5, null), (6, null), (7, null)],
            CiLog.Of(log).Select(line => (line.Line.Number, line.StepExit)));
    }

    /// <summary>
    /// A Python module a job's step ran, that died of an error through its top level, has crashed its program though the
    /// runner's line that the step failed follows its traceback: that line is the forge's, saying how the step's process
    /// ended - its code 1 - and never a line the program printed on. The traceback is Python 3.12's own output, in the
    /// step GitHub's runner wrote around it.
    /// </summary>
    [Fact]
    public void APythonCrashInAJobsStep_HasCrashedItsProgram_ThoughTheRunnersLineFollowsIt()
    {
        var log = string.Join(
            "\n",
            "2026-11-23T16:48:04.1883660Z ##[group]Run python -m pkg.run",
            "2026-11-23T16:48:04.1885064Z \u001b[36;1mpython -m pkg.run\u001b[0m",
            "2026-11-23T16:48:04.2243133Z shell: /usr/bin/bash -e {0}",
            "2026-11-23T16:48:04.2255994Z ##[endgroup]",
            "2026-11-23T16:48:04.4312876Z Traceback (most recent call last):",
            "2026-11-23T16:48:04.4313542Z   File \"<frozen runpy>\", line 198, in _run_module_as_main",
            "2026-11-23T16:48:04.4313921Z   File \"<frozen runpy>\", line 88, in _run_code",
            "2026-11-23T16:48:04.4314288Z   File \"/home/runner/work/app/app/pkg/run.py\", line 3, in <module>",
            "2026-11-23T16:48:04.4314640Z     main()",
            "2026-11-23T16:48:04.4314990Z   File \"/home/runner/work/app/app/pkg/run.py\", line 2, in main",
            "2026-11-23T16:48:04.4315344Z     raise RuntimeError(\"run failed\")",
            "2026-11-23T16:48:04.4315701Z RuntimeError: run failed",
            "2026-11-23T16:48:04.4512036Z ##[error]Process completed with exit code 1.",
            "2026-11-23T16:48:04.4696373Z Cleaning up orphan processes") + "\n";

        var digest = CiDigest.Of(log, [], "app", said => said, [])!;

        var failure = Assert.Single(digest.Failures);

        Assert.Equal(("RuntimeError: run failed", "crashed", (5, 12)), (failure.Id, failure.Outcome, failure.Log!.Value));
        Assert.Equal(("pkg/run.py", 2), (failure.File, failure.Line));
        Assert.Equal(["Process completed with exit code 1."], digest.Unparsed);
    }

    /// <summary>
    /// The sentences the forge marked in a log that no form reads - the runner's that a step failed, a workflow's
    /// <c>::error::</c> with no file - are said among the lines no parser held, each once, never as an error ahead of a
    /// tool's: the job's own error is the first.
    /// </summary>
    [Fact]
    public void TheForgesOwnSentences_AreSaidAmongTheLinesNoParserHeld_NeverAsTheFirstError()
    {
        var log = string.Join(
            "\n",
            "/home/runner/work/app/app/src/a.c:3:5: error: 'x' undeclared",
            "##[error]Tests are failing in shard 2",
            "##[error]Process completed with exit code 1.",
            "##[error]Process completed with exit code 1.") + "\n";

        var digest = CiDigest.Of(log, [], "app", said => said, [])!;

        Assert.Equal(("src/a.c", 3), (digest.First!.File, digest.First.Line));
        Assert.Equal(["Tests are failing in shard 2", "Process completed with exit code 1."], digest.Unparsed);
    }

    /// <summary>
    /// What an annotation says is masked as the log is: a secret a workflow wrote into its annotation never reaches the
    /// digest.
    /// </summary>
    [Fact]
    public void WhatAnAnnotationSays_IsMaskedAsTheLogIs()
    {
        CiAnnotation[] annotations = [new("src/a.cs", 2, 2, null, null, "failure", "token s3cret", "refused s3cret", "s3cret again")];

        var digest = CiDigest.Of(null, annotations, "app", said => said.Replace("s3cret", "***", StringComparison.Ordinal), [])!;

        Assert.Equal("token ***: refused ***\n*** again", Assert.Single(digest.Diagnostics).Message);
    }
}
