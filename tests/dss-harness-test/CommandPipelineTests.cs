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
    /// Every end an operation of its own handed to this one is run in its own end, however it ended - each even where
    /// the one before it threw, and a throwing end is said as a warning and changes no exit code.
    /// </summary>
    [Theory]
    [InlineData("answered")]
    [InlineData("threw")]
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

                return ended == "answered"
                    ? Task.FromResult(CommandOutcome.Ok("done"))
                    : throw new HarnessException(HarnessExit.Refused, "refused");
            },
            CancellationToken.None);

        Assert.Equal(ended == "answered" ? HarnessExit.Success : HarnessExit.Refused, completion.ExitCode);
        Assert.Equal(["one", "two"], ran);
        Assert.Contains("test: WARN - an end this operation was handed could not run: Unexpected IOException: the host is gone", Lines(kit.Error));
    }

    /// <summary>
    /// The operation's own end that cannot run is said as a warning, as a handed end that cannot is: the ends handed to
    /// it still run, the operation still ends with what its body answered, and nothing escapes its lifecycle.
    /// </summary>
    [Fact]
    public async Task AnOwnEndThatCannotRun_IsSaidAsAWarning_AndTheEndsHandedToItStillRun()
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

        Assert.Equal(HarnessExit.Success, completion.ExitCode);
        Assert.Equal(["handed"], ran);
        Assert.Equal(
            [$"test: WARN - this operation's own end could not run: Unexpected InvalidOperationException: No service for type '{typeof(CommandEnd).FullName}' has been registered."],
            Lines(kit.Error));
    }

    /// <summary>
    /// A handed end that fails for a defect of this tool shows the defect's trace under verbose, as a command's own failure
    /// does, and only there: a warning that hid it would leave a defect nobody could diagnose.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AHandedEndsDefect_ShowsItsTraceOnlyUnderVerbose(bool verbose)
    {
        var kit = Begin(verbose: verbose);

        await kit.Session.RunAsync(
            (_, _) =>
            {
                kit.Host.Defer(kit.Session.Id, () => throw new InvalidOperationException("boom"));
                return Task.FromResult(CommandOutcome.Ok("done"));
            },
            CancellationToken.None);

        var lines = Lines(kit.Error);

        Assert.Equal("test: WARN - an end this operation was handed could not run: Unexpected InvalidOperationException: boom", lines[0]);
        Assert.Equal(verbose, lines.Any(line => line.StartsWith("   at ", StringComparison.Ordinal)));
        Assert.Equal(verbose ? "System.InvalidOperationException: boom" : null, lines.ElementAtOrDefault(1));
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
                NextServices = stepServices;
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
    /// an end left to nobody, a document with no stopped form, and the options and parts of a front end a later slice
    /// gives their meaning - each said, never run without a word.
    /// </summary>
    [Fact]
    public void ASessionThatWouldIgnoreWhatItIsAsked_IsRefused_BeforeAnyServiceIsBuilt()
    {
        var built = 0;
        var host = new DssHarnessHost(_ =>
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
    /// failure is reported, and its end that cannot run either is said - never a trace the command line prints and an
    /// exit code that collides with verify-git's.
    /// </summary>
    [Fact]
    public async Task AMachineThisBuildCannotRunOn_IsToldWhy_InTheFailureLine()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var options = new SessionOptions { Command = HostAgentProtocol.CommandName, Prompting = false, Origin = CommandOrigin.Agent };
        var host = new DssHarnessHost(
            _ => new ServiceCollection()
                .AddDssHarness(options, output: provider => new ConsoleHarnessOutput(output, error, verbose: false, provider.GetRequiredService<HomeShorthand>()))
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
                $"{HostAgentProtocol.CommandName}: WARN - this operation's own end could not run: Unexpected PlatformNotSupportedException: DssHarness supports Windows, Linux and macOS only.",
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

    /// <summary>The services the shared host's next session is built over, set by the kit being begun.</summary>
    internal object? NextServices { get; set; }

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
        string command = "test")
        => new(directory, beginsRun, verbose, document, stopped, command, this, sharedHost: null);

    /// <summary>
    /// One operation over hand-built services: real output, a real file system in the test's own tree, and a real end -
    /// its notice said once, which is how a test sees that the end ran. The host is shared with the operation a step
    /// belongs to, as one process shares one host; the services are built per session, as the command line's own root
    /// builds them.
    /// </summary>
    private sealed class Kit
    {
        internal Kit(
            string? directory,
            bool beginsRun,
            bool verbose,
            DocumentKind document,
            Func<SessionContext, int, string, string>? stopped,
            string command,
            CommandPipelineTests owner,
            DssHarnessHost? sharedHost)
            : this(command, defers: false, parent: null, verbose, directory, beginsRun, document, stopped, owner, sharedHost)
        {
        }

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
            DssHarnessHost? sharedHost)
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
            Host = sharedHost ?? new DssHarnessHost(_ => (IServiceProvider)owner.NextServices!);
            owner.NextServices = Services;
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
}
