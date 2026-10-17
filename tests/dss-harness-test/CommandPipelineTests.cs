using System.Text.Json.Nodes;
using DssHarness.Core.Application;
using DssHarness.Core.Application.Legs;
using DssHarness.Core.Composition;
using DssHarness.Core.Execution;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Hosts;
using DssHarness.Core.Output;
using DssHarness.Core.Platform;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;
using DssHarness.Core.Secrets;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace DssHarness.Tests;

/// <summary>
/// The lifecycle every command shares, in the one place that owns it: the document the whole of standard output from
/// the first line, the run named before anything could refuse it, what the body answered rendered as it has always
/// been, the end that runs however the command ended - and the ends an operation of its own leaves to it, each even
/// where the one before it threw.
/// </summary>
public sealed class CommandPipelineTests : IDisposable
{
    private readonly TempDirectory _tree = new();

    /// <summary>A directory refused before the body runs ends with the usage code, its document the whole of standard output.</summary>
    [Fact]
    public async Task ARefusedDirectory_EndsWithTheUsageCode_AndTheStoppedFormOfItsDocument()
    {
        var kit = Begin(
            directory: Path.Combine(_tree.Path, "not-there"),
            document: DocumentKind.Own,
            stopped: (_, exitCode, summary) => $"stopped {exitCode}: {summary}");

        var completion = await kit.Session.RunAsync((scope, _) => Task.FromResult(CommandOutcome.Ok("never reached")), CancellationToken.None);

        Assert.Equal(HarnessExit.UsageError, completion.ExitCode);
        Assert.Null(completion.Outcome);
        Assert.Equal(
            $"stopped {HarnessExit.UsageError}: Directory '{Path.Combine(_tree.Path, "not-there")}' does not exist." + kit.Output.NewLine,
            kit.Output.ToString());
        Assert.Equal(
            [$"test: FAIL - Directory '{Path.Combine(_tree.Path, "not-there")}' does not exist."],
            Lines(kit.Error));
    }

    /// <summary>
    /// The run is named in the command's first line, before anything could refuse it - and read back by the one reader
    /// that has to find it again, wherever the command went on to fail.
    /// </summary>
    [Fact]
    public async Task TheRunIsNamedInTheFirstLine_BeforeAnythingCouldRefuseIt()
    {
        var kit = Begin(directory: Path.Combine(_tree.Path, "not-there"), beginsRun: true);

        var completion = await kit.Session.RunAsync((scope, _) => Task.FromResult(CommandOutcome.Ok("never reached")), CancellationToken.None);

        Assert.Equal(HarnessExit.UsageError, completion.ExitCode);
        Assert.NotNull(completion.Run);
        Assert.True(RunLine.TryRead(Assert.Single(Lines(kit.Output)), "test", out var run));
        Assert.Equal(completion.Run, run);
    }

    /// <summary>A command line that begins no run names none, and its body is handed none.</summary>
    [Fact]
    public async Task ACommandLineThatBeginsNoRun_NamesNone()
    {
        var kit = Begin();
        RunId? seen = RunId.New();

        var completion = await kit.Session.RunAsync(
            (scope, _) =>
            {
                seen = scope.Run;
                return Task.FromResult(CommandOutcome.Ok("done"));
            },
            CancellationToken.None);

        Assert.Null(seen);
        Assert.Null(completion.Run);
        Assert.Equal(["test: OK - done"], Lines(kit.Output));
    }

    /// <summary>What the body answered is rendered as it has always been: its document, its lines, and the line it ends on.</summary>
    [Fact]
    public async Task WhatTheBodyAnswered_IsRenderedAsItAlwaysWas()
    {
        var kit = Begin();

        var completion = await kit.Session.RunAsync(
            (_, _) => Task.FromResult(new CommandOutcome(HarnessExit.Success, "done") with { Data = ["one", "two"], Details = ["a detail"] }),
            CancellationToken.None);

        Assert.Equal(HarnessExit.Success, completion.ExitCode);
        Assert.Equal(["one", "two", "test: a detail", "test: OK - done"], Lines(kit.Output));
        Assert.Empty(Lines(kit.Error));
    }

    /// <summary>A body that answered with a refusal renders its failure line, as it always has.</summary>
    [Fact]
    public async Task ARefusalTheBodyReturned_RendersItsFailureLine()
    {
        var kit = Begin();

        var completion = await kit.Session.RunAsync((_, _) => Task.FromResult(CommandOutcome.Refused("nothing was changed")), CancellationToken.None);

        Assert.Equal(HarnessExit.Refused, completion.ExitCode);
        Assert.Empty(Lines(kit.Output));
        Assert.Equal(["test: FAIL - nothing was changed"], Lines(kit.Error));
    }

    /// <summary>
    /// A body that answered without a document answers with the stopped form of the one it was asked for, so a reader
    /// of the document reads one shape whatever happened.
    /// </summary>
    [Fact]
    public async Task ABodyThatAnsweredWithoutADocument_AnswersWithTheStoppedForm()
    {
        var kit = Begin(document: DocumentKind.Own, stopped: (_, exitCode, summary) => $"stopped {exitCode}: {summary}");

        var completion = await kit.Session.RunAsync((_, _) => Task.FromResult(CommandOutcome.Refused("nothing was changed")), CancellationToken.None);

        Assert.Equal(HarnessExit.Refused, completion.ExitCode);
        Assert.Equal([$"stopped {HarnessExit.Refused}: nothing was changed"], Lines(kit.Output));
    }

    /// <summary>
    /// What the command leaves is said however it ended - the body answering, an exception, a cancellation - each in the
    /// command's own end.
    /// </summary>
    [Theory]
    [InlineData("answered")]
    [InlineData("threw")]
    [InlineData("cancelled")]
    public async Task TheCommandEnd_RunsHoweverTheCommandEnded(string ended)
    {
        var kit = Begin();
        kit.Refusals.Refused();

        var completion = ended switch
        {
            "answered" => await kit.Session.RunAsync((_, _) => Task.FromResult(CommandOutcome.Ok("done")), CancellationToken.None),
            "threw" => await kit.Session.RunAsync((_, _) => throw new HarnessException(HarnessExit.Refused, "refused"), CancellationToken.None),
            _ => await kit.Session.RunAsync((_, _) => throw new OperationCanceledException(), CancellationToken.None),
        };

        Assert.Equal(ended switch
        {
            "answered" => HarnessExit.Success,
            "threw" => HarnessExit.Refused,
            _ => HarnessExit.Cancelled,
        }, completion.ExitCode);
        Assert.Contains(Lines(kit.Error), line => line.Contains($"test: WARN - {HostConnector.SyncedCopyNotice}.", StringComparison.Ordinal));
    }

    /// <summary>
    /// Every end an operation of its own handed to this one is run in its own end, however it ended - its body answering,
    /// throwing or cancelled - each even where the one before it threw; and an end that met what an end is expected to
    /// meet, a host gone, is said as a warning and changes no exit code.
    /// </summary>
    [Theory]
    [InlineData("answered")]
    [InlineData("threw")]
    [InlineData("cancelled")]
    public async Task EveryDeferredEnd_IsRunByTheOuterSession_HoweverItEnded(string ended)
    {
        var kit = Begin();
        var ran = new List<string>();

        var completion = await kit.Session.RunAsync(
            (scope, _) =>
            {
                kit.Host.Defer(kit.Session.Id, () => { ran.Add("one"); return Task.CompletedTask; });
                kit.Host.Defer(kit.Session.Id, () => throw new IOException("the host is gone"));
                kit.Host.Defer(kit.Session.Id, () => { ran.Add("two"); return Task.CompletedTask; });

                return ended switch
                {
                    "answered" => Task.FromResult(CommandOutcome.Ok("done")),
                    "threw" => throw new HarnessException(HarnessExit.Refused, "refused"),
                    _ => throw new OperationCanceledException(),
                };
            },
            CancellationToken.None);

        Assert.Equal(
            ended switch
            {
                "answered" => HarnessExit.Success,
                "threw" => HarnessExit.Refused,
                _ => HarnessExit.Cancelled,
            },
            completion.ExitCode);
        Assert.Equal(["one", "two"], ran);
        Assert.Equal(
            [
                .. ended switch
                {
                    "answered" => Array.Empty<string>(),
                    "threw" => ["test: FAIL - refused"],
                    _ => ["test: FAIL - Interrupted before completion."],
                },
                "test: WARN - an end this operation was handed could not run: Unexpected IOException: the host is gone",
            ],
            Lines(kit.Error));
    }

    /// <summary>
    /// The operation's own end that cannot even be built - a registration missing - is a defect of this tool, and is not
    /// swallowed: said as a failure, the operation ends with the code a defect has whatever its body answered, and the
    /// ends handed to it still run.
    /// </summary>
    [Fact]
    public async Task AnOwnEndThatCannotBeBuilt_IsADefect_EndingTheOperationWithItsCode_AndTheEndsHandedToItStillRun()
    {
        var kit = Begin();
        var ran = new List<string>();
        kit.Services.Remove(typeof(CommandEnd));

        var completion = await kit.Session.RunAsync(
            (_, _) =>
            {
                kit.Host.Defer(kit.Session.Id, () => { ran.Add("handed"); return Task.CompletedTask; });
                return Task.FromResult(CommandOutcome.Ok("done"));
            },
            CancellationToken.None);

        Assert.Equal(HarnessExit.InternalError, completion.ExitCode);
        Assert.Equal(["handed"], ran);
        Assert.Equal(["test: OK - done"], Lines(kit.Output));
        Assert.Equal(
            [$"test: FAIL - this operation's own end could not run: Unexpected InvalidOperationException: No service for type '{typeof(CommandEnd).FullName}' has been registered."],
            Lines(kit.Error));
    }

    /// <summary>
    /// A document says the code the process exits with, a defect its end meets included: where a document is asked for,
    /// the end runs before it is written - the answered document's code made the defect's, its other members as the body
    /// gave them, and a stopped one's too - and the event that says the operation ended still comes last.
    /// </summary>
    [Fact]
    public async Task ADocument_SaysTheCodeTheProcessExitsWith_ADefectAtItsEndIncluded()
    {
        static string Stopped(SessionContext context, int code, string summary)
            => new JsonObject { ["exitCode"] = code, ["summary"] = summary, ["legs"] = new JsonArray() }.ToJsonString();

        var answered = Begin(document: DocumentKind.Own, stopped: Stopped);
        var answeredEnded = await answered.Session.RunAsync(
            (_, _) =>
            {
                answered.Host.Defer(answered.Session.Id, () => throw new InvalidOperationException("boom"));
                return Task.FromResult(CommandOutcome.Ok("done") with { Data = ["""{"exitCode":0,"summary":"done","legs":["a"]}"""], Quiet = true });
            },
            CancellationToken.None);

        var failed = Begin(document: DocumentKind.Own, stopped: Stopped);
        var failedEnded = await failed.Session.RunAsync(
            (_, _) =>
            {
                failed.Host.Defer(failed.Session.Id, () => throw new InvalidOperationException("boom"));
                throw new HarnessException(HarnessExit.Refused, "refused");
            },
            CancellationToken.None);

        var answeredDocument = JsonNode.Parse(answered.Output.ToString())!.AsObject();
        var failedDocument = JsonNode.Parse(failed.Output.ToString())!.AsObject();

        Assert.Equal((HarnessExit.InternalError, HarnessExit.InternalError), (answeredEnded.ExitCode, failedEnded.ExitCode));
        Assert.Equal(
            (HarnessExit.InternalError, "done", "a"),
            (answeredDocument["exitCode"]!.GetValue<int>(), answeredDocument["summary"]!.GetValue<string>(), answeredDocument["legs"]![0]!.GetValue<string>()));
        Assert.Equal(
            (HarnessExit.InternalError, "refused"),
            (failedDocument["exitCode"]!.GetValue<int>(), failedDocument["summary"]!.GetValue<string>()));
    }

    /// <summary>
    /// A handed end that fails for a defect of this tool ends the operation with the code a defect has, and shows the
    /// defect's trace under verbose, as a command's own failure does, and only there: a warning and a code of success
    /// would leave a defect nobody looks for.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AHandedEndsDefect_EndsTheOperationAsADefect_ItsTraceOnlyUnderVerbose(bool verbose)
    {
        var kit = Begin(verbose: verbose);

        var completion = await kit.Session.RunAsync(
            (_, _) =>
            {
                kit.Host.Defer(kit.Session.Id, () => throw new InvalidOperationException("boom"));
                return Task.FromResult(CommandOutcome.Ok("done"));
            },
            CancellationToken.None);

        var lines = Lines(kit.Error);

        Assert.Equal(HarnessExit.InternalError, completion.ExitCode);
        Assert.Equal("test: FAIL - an end this operation was handed could not run: Unexpected InvalidOperationException: boom", lines[0]);
        Assert.Equal(verbose, lines.Any(line => line.StartsWith("   at ", StringComparison.Ordinal)));
        Assert.Equal(verbose ? "System.InvalidOperationException: boom" : null, lines.ElementAtOrDefault(1));
    }

    /// <summary>
    /// An end that failed for several hosts at once says each failure, each as what it is - a host gone a warning, a
    /// defect a failure - never the first alone.
    /// </summary>
    [Fact]
    public async Task AnEndThatFailedForSeveralHosts_SaysEachFailure()
    {
        var kit = Begin();

        var completion = await kit.Session.RunAsync(
            (_, _) =>
            {
                kit.Host.Defer(kit.Session.Id, () => throw new AggregateException(new IOException("mac went"), new InvalidOperationException("mini broke")));
                return Task.FromResult(CommandOutcome.Ok("done"));
            },
            CancellationToken.None);

        Assert.Equal(HarnessExit.InternalError, completion.ExitCode);
        Assert.Equal(
            [
                "test: WARN - an end this operation was handed could not run: Unexpected IOException: mac went",
                "test: FAIL - an end this operation was handed could not run: Unexpected InvalidOperationException: mini broke",
            ],
            Lines(kit.Error));
    }

    /// <summary>
    /// A step whose end, handed to its run, meets a defect says it on its own output, once, and the run ends with the code
    /// a defect has: the run's own lines say nothing more of it, and nothing of it is lost because the step had finished.
    /// </summary>
    [Fact]
    public async Task AStepsEndThatMeetsADefect_EndsItsRunAsADefect_SaidOnceByTheStep()
    {
        var kit = Begin();
        Kit? step = null;

        var completion = await kit.Session.RunAsync(
            async (_, _) =>
            {
                step = kit.Step(kit.Session.Id);
                step.Services.Remove(typeof(CommandEnd));

                var stepEnded = await step.Session.RunAsync((_, _) => Task.FromResult(CommandOutcome.Ok("the step's answer")), CancellationToken.None);
                Assert.Equal(HarnessExit.Success, stepEnded.ExitCode);

                return CommandOutcome.Ok("the run's answer");
            },
            CancellationToken.None);

        Assert.Equal(HarnessExit.InternalError, completion.ExitCode);
        Assert.Equal(
            [$"step: FAIL - this operation's own end could not run: Unexpected InvalidOperationException: No service for type '{typeof(CommandEnd).FullName}' has been registered."],
            Lines(step!.Error));
        Assert.Empty(Lines(kit.Error));
    }

    /// <summary>
    /// A session begun and never run still runs, when it is disposed, every end handed to it - none is lost because the
    /// operation that would have taken them never ran - and its services are disposed after them.
    /// </summary>
    [Fact]
    public async Task ASessionNeverRun_RunsTheEndsHandedToIt_WhenItIsDisposed()
    {
        var kit = Begin();
        var ran = new List<string>();

        kit.Host.Defer(kit.Session.Id, () => { ran.Add(kit.Services.Disposed ? "after its services went" : "handed"); return Task.CompletedTask; });
        kit.Host.Defer(kit.Session.Id, () => throw new IOException("the host is gone"));

        await kit.Session.DisposeAsync();

        Assert.Equal(["handed"], ran);
        Assert.True(kit.Services.Disposed);
        Assert.Equal(["test: WARN - an end this operation was handed could not run: Unexpected IOException: the host is gone"], Lines(kit.Error));
    }

    /// <summary>
    /// What a front end is told of an operation that did not answer - its body threw, it was cancelled, or its directory
    /// was refused - is one sequence of events, each stamped with the operation and in order: it began, the run it named
    /// where it named one, how it failed with the code it exits with and the stopped form of its document, and that it
    /// ended.
    /// </summary>
    [Theory]
    [InlineData("threw")]
    [InlineData("cancelled")]
    [InlineData("refused its directory")]
    public async Task TheEventsOfAnOperationThatDidNotAnswer_SayHowItEnded_InOrder(string ended)
    {
        var sink = new RecordingSink();
        var missing = Path.Combine(_tree.Path, "not-there");
        var kit = Begin(
            directory: ended == "refused its directory" ? missing : null,
            beginsRun: ended == "refused its directory",
            document: DocumentKind.Own,
            stopped: (_, exitCode, summary) => $"stopped {exitCode}: {summary}",
            sink: sink);

        var completion = await kit.Session.RunAsync(
            (_, _) => ended == "threw" ? throw new HarnessException(HarnessExit.Refused, "refused") : throw new OperationCanceledException(),
            CancellationToken.None);
        await kit.Session.DisposeAsync();

        var (exitCode, message) = ended switch
        {
            "threw" => (HarnessExit.Refused, "refused"),
            "cancelled" => (HarnessExit.Cancelled, "Interrupted before completion."),
            _ => (HarnessExit.UsageError, $"Directory '{missing}' does not exist."),
        };

        RunEvent[] expected =
        [
            new CommandBegan("test"),
            .. completion.Run is { } run ? [new RunBegan("test", run)] : Array.Empty<RunEvent>(),
            new CommandFailed("test", exitCode, message, Defect: false, StoppedDocument: $"stopped {exitCode}: {message}"),
            new CommandEnded("test"),
        ];

        Assert.Equal(exitCode, completion.ExitCode);
        Assert.Equal(ended == "refused its directory", completion.Run is not null);
        Assert.Equal(
            expected.Select((@event, index) => @event with { Operation = kit.Session.Id, Sequence = index + 1 }),
            sink.Seen.Select(@event => @event with { At = default }));
    }

    /// <summary>
    /// Every event an operation's output publishes is stamped with that operation's own id and the operation it belongs
    /// to: the id is the session's before its services are built, so the output the tool's own registrations build for
    /// it - the command line's - carries it, and a front end never meets an event that names no operation.
    /// </summary>
    [Fact]
    public async Task EveryEvent_IsStampedWithItsOwnOperation_AndTheOneItBelongsTo()
    {
        var parent = OperationId.New();
        var session = ServiceRegistration.Host().Begin(new SessionOptions { Command = "test", Directory = _tree.Path, Prompting = false, Parent = parent });
        ConsoleHarnessOutput? output = null;

        await using (session.ConfigureAwait(false))
        {
            var completion = await session.RunAsync(
                (scope, _) =>
                {
                    output = Assert.IsType<ConsoleHarnessOutput>(scope.Services.GetRequiredService<IHarnessOutput>());
                    return Task.FromResult(CommandOutcome.Ok("done") with { Quiet = true });
                },
                CancellationToken.None);

            Assert.Equal(HarnessExit.Success, completion.ExitCode);
        }

        Assert.Equal(session.Id, output!.Operation);
        Assert.Equal(parent, output.Parent);
    }

    /// <summary>
    /// The current directory is read only where it is needed: a directory asked for whole is acted on without it, so a
    /// shell standing in a directory since removed - whose current directory Linux and macOS cannot read - still runs a
    /// command given one; and one asked for relative to it reads it.
    /// </summary>
    [Fact]
    public async Task ADirectoryAskedForWhole_IsActedOn_WithoutReadingTheCurrentDirectory()
    {
        var whole = Begin(directory: _tree.Path);
        whole.Services.Set(typeof(IProcessContext), new UnreadableProcessContext());
        var relative = Begin(directory: ".");
        relative.Services.Set(typeof(IProcessContext), new UnreadableProcessContext());
        string? actedOn = null;

        var wholeEnded = await whole.Session.RunAsync(
            (scope, _) =>
            {
                actedOn = scope.Directory;
                return Task.FromResult(CommandOutcome.Ok("done"));
            },
            CancellationToken.None);
        var relativeEnded = await relative.Session.RunAsync((_, _) => Task.FromResult(CommandOutcome.Ok("never reached")), CancellationToken.None);

        Assert.Equal(HarnessExit.Success, wholeEnded.ExitCode);
        Assert.Equal(_tree.Path, actedOn);
        Assert.Equal(HarnessExit.InternalError, relativeEnded.ExitCode);
        Assert.Equal([$"test: FAIL - Unexpected FileNotFoundException: {UnreadableProcessContext.Why}"], Lines(relative.Error));
    }

    /// <summary>
    /// A step whose run has already taken its ends - or that names no operation this host began - runs its own end at
    /// once: handed to nobody, it would never run at all.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AStepWhoseRunTookItsEndsAlready_RunsItsOwnAtOnce(bool parentRan)
    {
        var kit = Begin();

        if (parentRan)
        {
            await kit.Session.RunAsync((_, _) => Task.FromResult(CommandOutcome.Ok("the run's answer")), CancellationToken.None);
        }

        var step = kit.Step(parentRan ? kit.Session.Id : OperationId.New());
        step.Refusals.Refused();

        var stepEnded = await step.Session.RunAsync((_, _) => Task.FromResult(CommandOutcome.Ok("the step's answer")), CancellationToken.None);

        Assert.Equal(HarnessExit.Success, stepEnded.ExitCode);
        Assert.Equal([$"step: WARN - {HostConnector.SyncedCopyNotice}."], Lines(step.Error));
    }

    /// <summary>
    /// A step's end runs on its own services, though its session is disposed as real code disposes it: they are kept for
    /// that end, and disposed once it has run - so what it says reaches the step's own output, never one already closed.
    /// </summary>
    [Fact]
    public async Task AStepsHandedEnd_RunsOnItsOwnServices_DisposedOnlyOnceItHasRun()
    {
        var kit = Begin();
        var sink = new RecordingSink();
        var stepOutput = new EventHarnessOutput(sink, OperationId.New());
        var refusals = new SyncedCopyRefusals(stepOutput);
        refusals.Refused();
        var stepServices = new Services(new Dictionary<Type, object?>
        {
            [typeof(IHarnessOutput)] = stepOutput,
            [typeof(IProcessContext)] = new FixedProcessContext(),
            [typeof(IFileSystem)] = new PhysicalFileSystem(FilePermissionsFactory.Create()),
            [typeof(CommandEnd)] = new CommandEnd(refusals, new HoldAwakeRegistry(Substitute.For<IHostCommandRunner>(), stepOutput)),
        });
        var keptWhileTheRunWent = false;

        var completion = await kit.Session.RunAsync(
            async (_, _) =>
            {
                NextServices = _ => stepServices;
                var step = kit.Host.Begin(new SessionOptions { Command = "step", Parent = kit.Session.Id, DefersCommandEnd = true });

                await using (step.ConfigureAwait(false))
                {
                    await step.RunAsync((_, _) => Task.FromResult(CommandOutcome.Ok("the step's answer")), CancellationToken.None);
                }

                keptWhileTheRunWent = !stepServices.Disposed;
                return CommandOutcome.Ok("the run's answer");
            },
            CancellationToken.None);

        Assert.Equal(HarnessExit.Success, completion.ExitCode);
        Assert.True(keptWhileTheRunWent, "the step's services were disposed with its session, before its end had run on them");
        Assert.True(stepServices.Disposed);
        Assert.Equal(
            [typeof(CommandBegan), typeof(CommandReported), typeof(TextLine), typeof(CommandEnded)],
            sink.Seen.Select(@event => @event.GetType()));
        Assert.Equal(new TextLine(LineKind.Warn, "step", HostConnector.SyncedCopyNotice + "."), sink.Seen[2] with { Operation = null, Sequence = 0, At = default });
    }

    /// <summary>
    /// A session that would ignore what it is asked, or could not keep it, is refused before any service is built for it:
    /// an end left to nobody, a document with no stopped form, a stopped form for an operation that answers with no
    /// document - which would answer a stop with a document nobody asked for - and the options and parts of a front end this
    /// build does not honour yet - each said, never run without a word.
    /// </summary>
    [Fact]
    public void ASessionThatWouldIgnoreWhatItIsAsked_IsRefused_BeforeAnyServiceIsBuilt()
    {
        var built = 0;
        var host = new DssHarnessHost((_, _) =>
        {
            built++;
            return new Services([]);
        });

        Assert.Equal(
            "'test' leaves its end to the operation it is a step of, and names none (Parent): nobody would ever run it.",
            Assert.Throws<InvalidOperationException>(() => host.Begin(new SessionOptions { Command = "test", DefersCommandEnd = true })).Message);
        Assert.Equal(
            "'test' answers with a document of its own, and its front end supplies no stopped form (Stopped) to answer a stop with.",
            Assert.Throws<InvalidOperationException>(() => host.Begin(new SessionOptions { Command = "test", Document = DocumentKind.Own })).Message);
        Assert.Equal(
            "'test' answers with no document (DocumentKind.None), and its front end supplies a stopped form (Stopped) it would answer a stop with "
                + "all the same: a document nobody asked for.",
            Assert.Throws<InvalidOperationException>(() => host.Begin(
                new SessionOptions { Command = "test" },
                new SessionFrontEnd(Stopped: (_, exitCode, _) => $"stopped {exitCode}"))).Message);
        Assert.Equal(
            "PausesOnInterrupt is honoured by the run verbs of an orchestrator run, which this build does not have yet: every interrupt still cancels.",
            Assert.Throws<NotSupportedException>(() => host.Begin(new SessionOptions { Command = "test", PausesOnInterrupt = true })).Message);
        Assert.Equal(
            "ApartFromTerminal is honoured once a process can be started apart from the terminal (ProcessRequest), which this build cannot do "
                + "yet: what an operation starts is in reach of an interrupt.",
            Assert.Throws<NotSupportedException>(() => host.Begin(new SessionOptions { Command = "test", ApartFromTerminal = true })).Message);
        Assert.Equal(
            "A front end's own Output is honoured once a front end other than the command line builds an operation's services, which this "
                + "build does not have yet: an operation writes and asks through the services its host builds.",
            Assert.Throws<NotSupportedException>(() => host.Begin(
                new SessionOptions { Command = "test" },
                new SessionFrontEnd(Output: _ => new EventHarnessOutput(new TextRunEvents(TextWriter.Null, TextWriter.Null, verbose: false))))).Message);
        Assert.Equal(
            "A front end's own Prompt is honoured once a front end other than the command line builds an operation's services, which this "
                + "build does not have yet: an operation writes and asks through the services its host builds.",
            Assert.Throws<NotSupportedException>(() => host.Begin(
                new SessionOptions { Command = "test" },
                new SessionFrontEnd(Prompt: _ => Substitute.For<ISuperuserPrompt>()))).Message);
        Assert.Equal(0, built);
    }

    /// <summary>
    /// A use case its operation's services do not build is a defect said by name - which registration is missing - never
    /// a null reference nobody can trace.
    /// </summary>
    [Fact]
    public async Task AUseCaseNotRegistered_IsADefectSaidByName()
    {
        var kit = Begin();

        var completion = await kit.Session.RunAsync((scope, _) => Task.FromResult(scope.Use<IBuildLegs>() is null ? CommandOutcome.Ok("none") : CommandOutcome.Ok("one")), CancellationToken.None);

        Assert.Equal(HarnessExit.InternalError, completion.ExitCode);
        Assert.Equal(
            [$"test: FAIL - Unexpected InvalidOperationException: No service for type '{typeof(IBuildLegs).FullName}' has been registered."],
            Lines(kit.Error));
    }

    /// <summary>
    /// A machine this build cannot run on is told why in the failure line, which the machine that asked a host reads:
    /// the output that line is written through is built whatever the machine is, so the session begins, its body's
    /// failure is reported, and its end that cannot run either is said, as the failure it is - never a trace the command
    /// line prints and an exit code that collides with verify-git's.
    /// </summary>
    [Fact]
    public async Task AMachineThisBuildCannotRunOn_IsToldWhy_InTheFailureLine()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var options = new SessionOptions { Command = HostAgentProtocol.CommandName, Prompting = false, Origin = CommandOrigin.Agent };
        var host = new DssHarnessHost(
            (_, operation) => new ServiceCollection()
                .AddDssHarness(options, operation, output: provider => new ConsoleHarnessOutput(output, error, verbose: false, provider.GetRequiredService<HomeShorthand>()))
                .AddSingleton<IHostPlatform>(_ => throw new PlatformNotSupportedException("DssHarness supports Windows, Linux and macOS only."))
                .BuildServiceProvider());

        var session = host.Begin(options);

        await using (session.ConfigureAwait(false))
        {
            var completion = await session.RunAsync(
                (scope, _) => Task.FromResult(scope.Services.GetRequiredService<HostAgentService>() is null ? CommandOutcome.Ok("none") : CommandOutcome.Ok("served")),
                CancellationToken.None);

            Assert.Equal(HarnessExit.InternalError, completion.ExitCode);
        }

        Assert.Equal(
            [
                $"{HostAgentProtocol.CommandName}: FAIL - Unexpected PlatformNotSupportedException: DssHarness supports Windows, Linux and macOS only.",
                $"{HostAgentProtocol.CommandName}: FAIL - this operation's own end could not run: Unexpected PlatformNotSupportedException: DssHarness supports Windows, Linux and macOS only.",
            ],
            Lines(error));
        Assert.Empty(Lines(output));
    }

    /// <summary>
    /// An operation begun as a step of a run leaves its end to that run: the step's own end - what it says and leaves
    /// on the hosts it reached - runs in the run's end, never cut short by the step finishing.
    /// </summary>
    [Fact]
    public async Task AStepOfARun_LeavesItsEndToTheRun_ToAwaitHoweverTheRunEnded()
    {
        var kit = Begin();
        Kit? step = null;

        var completion = await kit.Session.RunAsync(
            async (scope, _) =>
            {
                step = kit.Step(kit.Session.Id);
                step.Refusals.Refused();

                var stepEnded = await step.Session.RunAsync((_, _) => Task.FromResult(CommandOutcome.Ok("the step's answer")), CancellationToken.None);
                Assert.Equal(HarnessExit.Success, stepEnded.ExitCode);

                // The step has finished; its end has not run - nothing of it has been said yet.
                Assert.Empty(Lines(step.Error));

                return CommandOutcome.Ok("the run's answer");
            },
            CancellationToken.None);

        Assert.Equal(HarnessExit.Success, completion.ExitCode);

        // The run's own end ran the step's, after its own.
        Assert.True(
            Lines(step!.Error).Any(line => line.Contains($"step: WARN - {HostConnector.SyncedCopyNotice}.", StringComparison.Ordinal)),
            $"step said: [{string.Join(" | ", Lines(step.Error))}]; run said: [{string.Join(" | ", Lines(kit.Error))}]");
    }

    /// <summary>Two sessions share no document scope and no output: one's document never holds the other's progress.</summary>
    [Fact]
    public async Task TwoSessions_NeverShareADocumentScope_OrAnOutput()
    {
        var first = Begin(document: DocumentKind.Own, stopped: (_, exitCode, _) => $"first {exitCode}");
        var second = Begin();

        await first.Session.RunAsync((scope, _) =>
        {
            Output(scope).Info("test", "progress under a document goes to standard error");
            return Task.FromResult(CommandOutcome.Ok("done"));
        }, CancellationToken.None);

        await second.Session.RunAsync((scope, _) =>
        {
            Output(scope).Info("test", "progress without a document goes to standard output");
            return Task.FromResult(CommandOutcome.Ok("done"));
        }, CancellationToken.None);

        Assert.Equal([$"first {HarnessExit.Success}"], Lines(first.Output));
        Assert.Equal(
            ["test: progress under a document goes to standard error", "test: OK - done"],
            Lines(first.Error));
        Assert.Equal(["test: progress without a document goes to standard output", "test: OK - done"], Lines(second.Output));
        Assert.Empty(Lines(second.Error));
    }

    /// <summary>A defect's trace is there under verbose, where someone is actually diagnosing it, and nowhere else.</summary>
    [Fact]
    public async Task TheDefectTrace_ShowsOnlyUnderVerbose()
    {
        var quiet = Begin(verbose: false);
        var verbose = Begin(verbose: true);

        var quietEnded = await quiet.Session.RunAsync((_, _) => throw new InvalidOperationException("boom"), CancellationToken.None);
        var verboseEnded = await verbose.Session.RunAsync((_, _) => throw new InvalidOperationException("boom"), CancellationToken.None);

        Assert.Equal(HarnessExit.InternalError, quietEnded.ExitCode);
        Assert.Equal(HarnessExit.InternalError, verboseEnded.ExitCode);
        Assert.Equal(["test: FAIL - Unexpected InvalidOperationException: boom"], Lines(quiet.Error));
        Assert.Contains(Lines(verbose.Error), line => line.StartsWith("   at ", StringComparison.Ordinal));
    }

    /// <summary>The services built for an operation are disposed with it, whatever it ended by.</summary>
    [Fact]
    public async Task TheServicesBuiltForAnOperation_AreDisposedWithIt()
    {
        var kit = Begin();
        Assert.False(kit.Services.Disposed);

        await kit.Session.RunAsync((_, _) => Task.FromResult(CommandOutcome.Ok("done")), CancellationToken.None);
        Assert.False(kit.Services.Disposed);

        await kit.Session.DisposeAsync();
        Assert.True(kit.Services.Disposed);
    }

    /// <inheritdoc/>
    public void Dispose() => _tree.Dispose();

    /// <summary>The services the shared host's next session is built over, given its id, set by the kit being begun.</summary>
    internal Func<OperationId, IServiceProvider>? NextServices { get; set; }

    private static List<string> Lines(StringWriter writer)
        => [.. writer.ToString().Split(writer.NewLine, StringSplitOptions.RemoveEmptyEntries)];

    private static IHarnessOutput Output(CommandScope scope)
        => (IHarnessOutput)scope.Services.GetService(typeof(IHarnessOutput))!;

    /// <summary>Begins one operation over hand-built services: the host, its session, its writers and its end's pieces.</summary>
    private Kit Begin(
        string? directory = null,
        bool beginsRun = false,
        bool verbose = false,
        DocumentKind document = DocumentKind.None,
        Func<SessionContext, int, string, string>? stopped = null,
        string command = "test",
        RecordingSink? sink = null)
        => new(command, defers: false, parent: null, verbose, directory, beginsRun, document, stopped, this, sharedHost: null, sink);

    /// <summary>
    /// One operation over hand-built services: real output, a real file system in the test's own tree, and a real end -
    /// its notice said once, which is how a test sees that the end ran. The host is shared with the operation a step
    /// belongs to, as one process shares one host; the services are built per session, as the command line's own root
    /// builds them - its output, where a sink is given, the one a front end of its own would have, stamped with the id
    /// the host gave the operation.
    /// </summary>
    private sealed class Kit
    {
        internal Kit(
            string command,
            bool defers,
            OperationId? parent,
            bool verbose,
            string? directory,
            bool beginsRun,
            DocumentKind document,
            Func<SessionContext, int, string, string>? stopped,
            CommandPipelineTests owner,
            DssHarnessHost? sharedHost,
            RecordingSink? sink = null)
        {
            Output = new StringWriter();
            Error = new StringWriter();
            Refusals = new SyncedCopyRefusals(new EventHarnessOutput(new TextRunEvents(Output, Error, verbose)));
            Services = new Services(new Dictionary<Type, object?>
            {
                [typeof(IHarnessOutput)] = new EventHarnessOutput(new TextRunEvents(Output, Error, verbose)),
                [typeof(IProcessContext)] = new FixedProcessContext(),
                [typeof(IFileSystem)] = new PhysicalFileSystem(FilePermissionsFactory.Create()),
                [typeof(CommandEnd)] = new CommandEnd(Refusals, new HoldAwakeRegistry(Substitute.For<IHostCommandRunner>(), new EventHarnessOutput(new TextRunEvents(Output, Error, verbose)))),
            });
            Host = sharedHost ?? new DssHarnessHost((_, operation) => owner.NextServices!(operation));
            owner.NextServices = operation =>
            {
                if (sink is not null)
                {
                    Services.Set(typeof(IHarnessOutput), new EventHarnessOutput(sink, operation, parent));
                }

                return Services;
            };
            _owner = owner;
            Session = Host.Begin(
                new SessionOptions
                {
                    Command = command,
                    Directory = directory,
                    Verbose = verbose,
                    BeginsRun = beginsRun,
                    Document = document,
                    Parent = parent,
                    DefersCommandEnd = defers,
                },
                new SessionFrontEnd(Stopped: stopped));
        }

        internal DssHarnessHost Host { get; }

        internal CommandSession Session { get; }

        internal Services Services { get; }

        internal SyncedCopyRefusals Refusals { get; }

        internal StringWriter Output { get; }

        internal StringWriter Error { get; }

        private readonly CommandPipelineTests _owner;

        /// <summary>Begins an operation of this kit's own host: a step of the operation the kit's session runs.</summary>
        internal Kit Step(OperationId parent, string command = "step")
            => new(command, defers: true, parent: parent, verbose: false, directory: null, beginsRun: false, document: DocumentKind.None, stopped: null, owner: _owner, sharedHost: Host);
    }

    /// <summary>
    /// An operation's services, built by hand: each asked for by its type, and on disposal each that is disposable
    /// disposed, as a provider disposes the singletons it built.
    /// </summary>
    private sealed class Services(Dictionary<Type, object?> singletons) : IServiceProvider, IAsyncDisposable
    {
        private readonly Dictionary<Type, object?> _singletons = singletons;

        public bool Disposed { get; private set; }

        public object? GetService(Type serviceType) => _singletons.TryGetValue(serviceType, out var service) ? service : null;

        /// <summary>Takes a service away, as a registration missing from the root would leave it.</summary>
        public void Remove(Type serviceType) => _singletons.Remove(serviceType);

        /// <summary>Puts a service in the place of the one registered for its type.</summary>
        public void Set(Type serviceType, object service) => _singletons[serviceType] = service;

        public ValueTask DisposeAsync()
        {
            Disposed = true;

            foreach (var disposable in _singletons.Values.OfType<IDisposable>())
            {
                disposable.Dispose();
            }

            return ValueTask.CompletedTask;
        }
    }

    /// <summary>A front end's sink of events, keeping each as it is given.</summary>
    private sealed class RecordingSink : IRunEvents
    {
        public List<RunEvent> Seen { get; } = [];

        public void Emit(RunEvent @event) => Seen.Add(@event);
    }

    /// <summary>
    /// The current directory of a process standing in a directory since removed, as Linux and macOS answer it: it cannot
    /// be read at all.
    /// </summary>
    private sealed class UnreadableProcessContext : IProcessContext
    {
        /// <summary>Why it cannot be read.</summary>
        public const string Why = "the directory this process stands in was removed";

        public string CurrentDirectory => throw new FileNotFoundException(Why);
    }
}
