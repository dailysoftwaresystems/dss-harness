using DssHarness.Core.Execution;
using DssHarness.Core.Hosts;
using DssHarness.Core.Output;
using DssHarness.Core.Platform;
using DssHarness.Core.Runs;

namespace DssHarness.Tests;

/// <summary>
/// Each event's text form is the line the member that caused it has always written, byte for byte, and the three kinds
/// of line a host's dispatcher reads back out of what a command wrote - the failure line, the run line, the note lines
/// on standard error - leave the renderer readable by their own readers.
/// </summary>
public sealed class RunEventTextTests
{
    /// <summary>A line the harness wrote through one of its members is that member's event, rendered as it always wrote it.</summary>
    [Fact]
    public void ALineTheHarnessWrote_IsItsEvent_RenderedAsTheMemberWroteIt()
    {
        var (output, standardOutput, standardError) = Create(verbose: true);

        output.Publish(new TextLine(LineKind.Ok, "init", "initialised"));
        output.Publish(new TextLine(LineKind.Fail, "push", "rejected"));
        output.Publish(new TextLine(LineKind.Warn, "push", "nothing to push"));
        output.Publish(new TextLine(LineKind.Info, "init", "created config.json"));
        output.Publish(new TextLine(LineKind.Detail, "build", "configuring"));
        output.Publish(new DataWritten("""{"done": true}"""));

        Assert.Equal(
            ["init: OK - initialised", "init: created config.json", "build: configuring", """{"done": true}"""],
            Lines(standardOutput));
        Assert.Equal(["push: FAIL - rejected", "push: WARN - nothing to push"], Lines(standardError));
    }

    /// <summary>Detail is shown only when verbose, as the member it came from is.</summary>
    [Fact]
    public void ADetailLine_IsShownOnlyWhenVerbose()
    {
        var (quiet, quietOutput, _) = Create(verbose: false);
        var (verbose, verboseOutput, _) = Create(verbose: true);

        quiet.Publish(new TextLine(LineKind.Detail, "build", "configuring"));
        verbose.Publish(new TextLine(LineKind.Detail, "build", "configuring"));

        Assert.Empty(Lines(quietOutput));
        Assert.Equal(["build: configuring"], Lines(verboseOutput));
    }

    /// <summary>
    /// A child's line is passed through as it came, prefixed with nothing and shortened by nothing - not even by the
    /// home shorthand on a host answering another machine, for that is what would break a note line's reader.
    /// </summary>
    [Fact]
    public void AChildsLine_IsPassedThroughAsItCame_NotEvenShortenedByTheHomeShorthand()
    {
        var (output, standardOutput, standardError) = Create(verbose: false, HomeShorthand.For(["/home/alice"], PlatformNames.Linux));

        output.Publish(new PassthroughLine(IsError: false, "compiling /home/alice/src/main.c"));
        output.Publish(new PassthroughLine(IsError: true, "note /home/alice/x"));

        Assert.Equal(["compiling /home/alice/src/main.c"], Lines(standardOutput));
        Assert.Equal(["note /home/alice/x"], Lines(standardError));
    }

    /// <summary>A record written says nothing to the terminal: the path is already in the lines that name what was done.</summary>
    [Fact]
    public void ARecordWritten_SaysNothingToTheTerminal()
    {
        var (output, standardOutput, standardError) = Create(verbose: true);

        output.Publish(new RecordWritten("run directory", "/tmp/here/.harness-config/runs/1"));

        Assert.Empty(Lines(standardOutput));
        Assert.Empty(Lines(standardError));
    }

    /// <summary>The failure line an event renders is the one spelling's, read back by the reader that has to find it.</summary>
    [Fact]
    public void TheFailureLineAnEventRenders_ReadsBackByItsOwnReader()
    {
        var (output, _, standardError) = Create(verbose: false);

        output.Publish(new TextLine(LineKind.Fail, "run", "git does not ignore this action's 'artifacts/'"));

        var line = Assert.Single(Lines(standardError));
        Assert.True(FailureLine.TryRead(line, "run", out var said));
        Assert.Equal("git does not ignore this action's 'artifacts/'", said);
        Assert.False(FailureLine.TryRead(line, "test", out _));
    }

    /// <summary>The run line an event renders reads back by its own reader, naming the run it named.</summary>
    [Fact]
    public void TheRunLineAnEventRenders_ReadsBackByItsOwnReader()
    {
        var (output, standardOutput, _) = Create(verbose: false);
        var run = RunId.New();

        output.Publish(new TextLine(LineKind.Info, "test", RunLine.Message(run)));

        Assert.True(RunLine.TryRead(Assert.Single(Lines(standardOutput)), "test", out var read));
        Assert.Equal(run, read);
    }

    /// <summary>
    /// What a ledger says arrives as the leg events, each carrying the line the ledger has always written - and a phase
    /// that prints is echoed tagged with its leg and phase, only when detail was asked for.
    /// </summary>
    [Fact]
    public void WhatALedgerSays_ArrivesAsTheLegEvents_EachWithTheLineItAlwaysWrote()
    {
        var sink = new RecordingSink();

        using (var output = new EventHarnessOutput(sink, OperationId.New()))
        {
            var ledger = new LegLedger(output, "test");

            ledger.Announce("2 legs, on 2 hosts");
            ledger.Starting("windows-x86_64-debug", HostId.Local, " (a copy of its own)");
            ledger.Transition("windows-x86_64-debug", "build: green");
            ledger.Record(Passed("windows-x86_64-debug"));
            output.Publish(new PhaseLine("windows-x86_64-debug", "build", "ninja: no work to do", IsError: false));
        }

        var forms = sink.Seen.SelectMany(@event => @event.TextForms().Select(form => form.Text)).ToList();

        Assert.Equal(
            [
                "2 legs, on 2 hosts",
                "windows-x86_64-debug: starting on local (a copy of its own)",
                "windows-x86_64-debug: build: green",
                "windows-x86_64-debug: passed",
                "windows-x86_64-debug/build: ninja: no work to do",
            ],
            forms);
        Assert.Contains(sink.Seen, @event => @event is LegFinished finished && finished.Entry.Leg == "windows-x86_64-debug");

        // The types are the pin: a ledger that wrote its lines through the members alone would read the same to a
        // terminal and tell a front end nothing.
        Assert.Collection(
            sink.Seen,
            @event => Assert.IsType<LegsAnnounced>(@event),
            @event => Assert.IsType<LegStarting>(@event),
            @event => Assert.IsType<LegProgressed>(@event),
            @event => Assert.IsType<LegFinished>(@event),
            @event => Assert.IsType<PhaseLine>(@event));
    }

    /// <summary>A ledger answering for a machine that dispatched its legs to this one says none of it.</summary>
    [Fact]
    public void ALedgerAnsweringForTheMachineThatDispatched_SaysNoneOfIt()
    {
        var sink = new RecordingSink();

        using (var output = new EventHarnessOutput(sink, OperationId.New()))
        {
            var ledger = new LegLedger(output, "test", dispatched: true);

            ledger.Announce("2 legs");
            ledger.Starting("windows-x86_64-debug", HostId.Local);
            ledger.Record(Passed("windows-x86_64-debug"));
        }

        Assert.DoesNotContain<RunEvent>(sink.Seen, @event => @event is LegsAnnounced or LegStarting or LegFinished);
    }

    private static LegEntry Passed(string leg)
        => new() { Leg = leg, Verdict = LegVerdict.Passed, Detail = string.Empty };

    private static (EventHarnessOutput Output, StringWriter StandardOutput, StringWriter StandardError) Create(
        bool verbose,
        HomeShorthand? home = null)
    {
        var standardOutput = new StringWriter();
        var standardError = new StringWriter();
        return (new EventHarnessOutput(new TextRunEvents(standardOutput, standardError, verbose, home)), standardOutput, standardError);
    }

    private static List<string> Lines(StringWriter writer)
        => [.. writer.ToString().Split(writer.NewLine, StringSplitOptions.RemoveEmptyEntries)];

    private sealed class RecordingSink : IRunEvents
    {
        public List<RunEvent> Seen { get; } = [];

        public void Emit(RunEvent @event) => Seen.Add(@event);
    }
}
