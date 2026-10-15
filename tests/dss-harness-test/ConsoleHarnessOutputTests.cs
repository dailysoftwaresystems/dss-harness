using RepoHarness.Core.Output;
using RepoHarness.Core.Platform;

namespace RepoHarness.Tests;

public sealed class ConsoleHarnessOutputTests
{
    [Fact]
    public void SuccessAndProgress_GoToStandardOutput()
    {
        var (output, standardOutput, standardError) = Create(verbose: false);

        output.Info("init", "created config.json");
        output.Ok("init", "initialised");

        Assert.Equal(["init: created config.json", "init: OK - initialised"], Lines(standardOutput));
        Assert.Empty(Lines(standardError));
    }

    /// <summary>
    /// What anything that would interrupt a document asks before it does. A password prompt written
    /// into standard output is read as part of the document a script is parsing; the same prompt is
    /// also the one thing that must not be skipped silently, so the answer has to be exact.
    /// </summary>
    [Fact]
    public void IsDataOnly_SaysWhetherADocumentIsBeingWritten_AndStopsSayingSoAfterwards()
    {
        var (output, _, _) = Create(verbose: false);

        Assert.False(output.IsDataOnly);

        using (output.DataOnly())
        {
            Assert.True(output.IsDataOnly);
        }

        Assert.False(output.IsDataOnly);
    }

    /// <summary>
    /// A scope opened inside another - a leg run inside a command that already answers with data -
    /// leaves the outer one standing when it closes: closed by the inner scope, the rest of the
    /// command's progress went to standard output, in front of the document.
    /// </summary>
    [Fact]
    public void AScopeInsideAnother_LeavesTheOuterOneStanding()
    {
        var (output, _, _) = Create(verbose: false);

        using (output.DataOnly())
        {
            using (output.DataOnly())
            {
                Assert.True(output.IsDataOnly);
            }

            Assert.True(output.IsDataOnly);
        }

        Assert.False(output.IsDataOnly);
    }

    [Fact]
    public void FailuresAndWarnings_GoToStandardError_SoOutputStaysPipeable()
    {
        var (output, standardOutput, standardError) = Create(verbose: false);

        output.Warn("push", "nothing to push");
        output.Fail("push", "rejected");

        Assert.Equal(["push: WARN - nothing to push", "push: FAIL - rejected"], Lines(standardError));
        Assert.Empty(Lines(standardOutput));
    }

    /// <summary>
    /// A failure line reads back as what it said, through the one spelling that wrote it: a host's
    /// refusal reaches the machine that dispatched its leg as exactly this line, and nothing but the
    /// failure line of the command asked about is taken for it.
    /// </summary>
    [Fact]
    public void AFailureLine_ReadsBackAsWhatItSaid_AndNothingElseDoes()
    {
        var line = FailureLine.For("run", "git does not ignore this action's 'artifacts/'");

        Assert.Equal("run: FAIL - git does not ignore this action's 'artifacts/'", line);
        Assert.True(FailureLine.TryRead(line, "run", out var said));
        Assert.Equal("git does not ignore this action's 'artifacts/'", said);
        Assert.False(FailureLine.TryRead(line, "test", out _));
        Assert.False(FailureLine.TryRead("run: WARN - git does not ignore it", "run", out _));
    }

    [Fact]
    public void Detail_IsShownOnlyWhenVerbose()
    {
        var (quiet, quietOutput, _) = Create(verbose: false);
        var (verbose, verboseOutput, _) = Create(verbose: true);

        quiet.Detail("build", "configuring");
        verbose.Detail("build", "configuring");

        Assert.False(quiet.IsVerbose);
        Assert.True(verbose.IsVerbose);
        Assert.Empty(Lines(quietOutput));
        Assert.Equal(["build: configuring"], Lines(verboseOutput));
    }

    [Fact]
    public void ChildProcessOutput_IsPassedThroughUnprefixed_OnItsOwnStream()
    {
        var (output, standardOutput, standardError) = Create(verbose: false);

        output.Raw("compiling main.c");
        output.RawError("warning: unused variable");

        Assert.Equal(["compiling main.c"], Lines(standardOutput));
        Assert.Equal(["warning: unused variable"], Lines(standardError));
    }

    [Fact]
    public void ACommandsResult_IsWrittenUnprefixed_ToStandardOutput()
    {
        // A listing or a JSON document must be readable by another program as it stands.
        var (output, standardOutput, standardError) = Create(verbose: false);

        output.Data("[\n  \"D-AREA-TOPIC-DETAIL\"\n]");

        Assert.Equal("[\n  \"D-AREA-TOPIC-DETAIL\"\n]" + standardOutput.NewLine, standardOutput.ToString());
        Assert.Empty(Lines(standardError));
    }

    [Fact]
    public void ConcurrentReports_NeverInterleaveWithinALine()
    {
        // Parallel legs report at the same moment, and half of one line inside another is
        // unreadable exactly when it matters.
        var (output, standardOutput, _) = Create(verbose: false);

        Parallel.For(0, 400, index => output.Info("leg", new string((char)('a' + (index % 26)), 64)));

        var lines = Lines(standardOutput);
        Assert.Equal(400, lines.Count);
        Assert.All(lines, line => Assert.Matches(@"^leg: ([a-z])\1{63}$", line));
    }

    /// <summary>
    /// A host answering another machine writes its home as <c>~</c> in every line of the harness's own, and
    /// tells a command's document the same way when asked: the machine that asked relays those lines, and
    /// keeps them wherever it keeps its output.
    /// </summary>
    [Fact]
    public void AHostAnsweringAnotherMachine_WritesItsHomeAsTilde_InEveryLineOfItsOwn()
    {
        var (output, standardOutput, standardError) = Create(verbose: true, HomeShorthand.For(["/home/alice"], PlatformNames.Linux));

        output.Info("test", "logs: /home/alice/src/repo/.harness-config/runs/1");
        output.Detail("test", "taking '/home/alice/src/repo'");
        output.Ok("clean", "removed '/home/alice/src/repo/build'");
        output.Warn("test", "the log path '/home/alice/src/repo/.harness-config/runs/1' was taken");
        output.Fail("sync-serve", "Access to the path '/home/alice/src/repo/a.txt' is denied.");

        Assert.Equal(
            ["test: logs: ~/src/repo/.harness-config/runs/1", "test: taking '~/src/repo'", "clean: OK - removed '~/src/repo/build'"],
            Lines(standardOutput));
        Assert.Equal(
            ["test: WARN - the log path '~/src/repo/.harness-config/runs/1' was taken", "sync-serve: FAIL - Access to the path '~/src/repo/a.txt' is denied."],
            Lines(standardError));
        Assert.Equal("~/src/repo", output.Shown("/home/alice/src/repo"));
    }

    /// <summary>
    /// What a program printed stays as it printed it, on a host answering another machine too: its output
    /// passed through, and a row quoting its last lines beneath a ledger. So does a document, which the
    /// command writing it tells field by field; and a command typed here writes every path as it is.
    /// </summary>
    [Fact]
    public void AProgramsWords_AndDocuments_AndACommandTypedHere_AreWrittenAsTheyAre()
    {
        var (serving, servingOutput, servingError) = Create(verbose: false, HomeShorthand.For(["/home/alice"], PlatformNames.Linux));
        var (typed, typedOutput, _) = Create(verbose: false);

        serving.Raw("/home/alice/src/repo/main.c:3: warning");
        serving.RawError("/home/alice/src/repo/main.c:4: error");
        serving.Info("test", QuotedLine.Of("/home/alice/src/repo/main.c:4: error"));
        serving.Data("""{"runDirectory": "/home/alice/src/repo/.harness-config/runs/1"}""");
        typed.Info("test", "logs: /home/alice/src/repo/.harness-config/runs/1");

        Assert.Equal(
            [
                "/home/alice/src/repo/main.c:3: warning",
                "test:   | /home/alice/src/repo/main.c:4: error",
                """{"runDirectory": "/home/alice/src/repo/.harness-config/runs/1"}""",
            ],
            Lines(servingOutput));
        Assert.Equal(["/home/alice/src/repo/main.c:4: error"], Lines(servingError));
        Assert.Equal(["test: logs: /home/alice/src/repo/.harness-config/runs/1"], Lines(typedOutput));
        Assert.Equal("/home/alice/src/repo", typed.Shown("/home/alice/src/repo"));
    }

    private static (ConsoleHarnessOutput Output, StringWriter StandardOutput, StringWriter StandardError) Create(bool verbose, HomeShorthand? home = null)
    {
        var standardOutput = new StringWriter();
        var standardError = new StringWriter();
        return (new ConsoleHarnessOutput(standardOutput, standardError, verbose, home), standardOutput, standardError);
    }

    private static List<string> Lines(StringWriter writer)
        => [.. writer.ToString().Split(writer.NewLine, StringSplitOptions.RemoveEmptyEntries)];
}
