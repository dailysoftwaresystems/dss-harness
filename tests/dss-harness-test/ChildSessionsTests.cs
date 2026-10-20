using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using DssHarness.Core.Application;
using DssHarness.Core.Composition;
using DssHarness.Core.Execution;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Output;
using DssHarness.Core.Platform;
using DssHarness.Core.Processes;
using DssHarness.Core.Results;
using DssHarness.Core.Worktrees;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace DssHarness.Tests;

/// <summary>
/// The sessions contained children of an orchestrator's steps lead: each recorded in its step's directory while it runs on
/// Linux and macOS, by the process runner through the tool's own records, and ended by the next command over the
/// orchestrator where the process that started it was killed before it could end it.
/// </summary>
public sealed class ChildSessionsTests
{
    /// <summary>The longest any run here is let take: a bound on a hang, never a measure of one.</summary>
    private static readonly TimeSpan LongBound = TimeSpan.FromMinutes(2);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// A contained child on Linux and macOS is recorded in its step's directory before it is said to have started - its
    /// session, its start, its program and the process that started it - and the record is taken back once its session
    /// is ended: at its own end, and at a stop alike.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AContainedChild_IsRecordedInItsStep_UntilItsSessionIsEnded(bool stopped)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "a contained child there is held in a job that ends with its starter, and nothing is recorded, which AContainedChild_IsRecordedOnlyWhereItsSessionWouldOutliveItsStarter holds.");

        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var step = temp.Combine("steps", "s1");
        var children = OrchestratorLayout.ChildrenDirectory(step);
        string[] atStart = [];
        var text = string.Empty;
        var leader = 0;
        string? started = null;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);

        var run = ContainingRunner().RunAsync(
            Sleeper(stopped ? "300000" : "1500") with
            {
                Contained = true,
                SessionRecord = harness.ChildSessions.For(step),
                OnStarted = () =>
                {
                    atStart = Directory.GetFiles(children);
                    text = File.ReadAllText(Assert.Single(atStart));
                },
                OnOutputLine = line =>
                {
                    if (Id(line, "pid=") is { } id)
                    {
                        leader = id;
                        started = harness.Identity.StampOf(id);

                        if (stopped)
                        {
                            stop.Cancel();
                        }
                    }
                },
            },
            stop.Token);

        var raised = await Record.ExceptionAsync(() => run.WaitAsync(LongBound, Token));

        if (stopped)
        {
            Assert.IsType<OperationCanceledException>(raised);
        }
        else
        {
            Assert.Null(raised);
            Assert.Equal(0, (await run).ExitCode);
        }

        Assert.Equal([OrchestratorLayout.ChildFile(step, leader)], atStart);
        Assert.NotNull(started);
        Assert.EndsWith("}\n", text, StringComparison.Ordinal);
        Assert.True(
            JsonNode.DeepEquals(
                new JsonObject
                {
                    ["session"] = leader,
                    ["started"] = started,
                    ["program"] = TestHost.AssemblyProgram,
                    ["owner"] = new JsonObject
                    {
                        ["machine"] = Environment.MachineName,
                        ["processId"] = Environment.ProcessId,
                        ["stamp"] = harness.Identity.Current,
                    },
                },
                JsonNode.Parse(text)),
            text);
        Assert.Empty(Directory.GetFiles(children));
    }

    /// <summary>
    /// The runner records a child's session only where the child is contained and its session would outlive the process
    /// that started it - on Linux and macOS, before the child is said to have started, and taken back after - and never a
    /// child started apart from the terminal, nor one on Windows, whose job ends with its starter.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AContainedChild_IsRecordedOnlyWhereItsSessionWouldOutliveItsStarter(bool contained)
    {
        var heard = new List<string>();
        var leader = 0;

        var result = await ContainingRunner().RunAsync(
            Sleeper("200") with
            {
                Contained = contained,
                ApartFromTerminal = !contained,
                SessionRecord = new Heard(heard),
                OnStarted = () => heard.Add("started"),
                OnOutputLine = line => leader = Id(line, "pid=") ?? leader,
            },
            Token).WaitAsync(LongBound, Token);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(
            contained && !OperatingSystem.IsWindows() ? [$"placed {leader} {TestHost.AssemblyProgram}", "started", "ended"] : ["started"],
            heard);
    }

    /// <summary>
    /// A contained child whose session cannot be recorded is not run: it is ended, never said to have started, and its
    /// start fails saying why - a child no record names is one a killed tool would leave running unseen.
    /// </summary>
    [Fact]
    public async Task AContainedChildWhoseSessionCannotBeRecorded_IsAStartThatFailed()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "nothing is recorded there, which AContainedChild_IsRecordedOnlyWhereItsSessionWouldOutliveItsStarter holds.");

        var refusing = new Refusing();
        var started = false;

        var raised = await Assert.ThrowsAsync<ProgramStartException>(() => ContainingRunner().RunAsync(
            Sleeper("300000") with { Contained = true, SessionRecord = refusing, OnStarted = () => started = true },
            Token).WaitAsync(LongBound, Token));

        try
        {
            Assert.Equal(TestHost.AssemblyProgram, raised.FileName);
            Assert.Equal($"'{TestHost.AssemblyProgram}' could not be started: its session could not be recorded: the disk is full", raised.Message);
            Assert.False(started, "a child whose session was not recorded was said to have started");
            Assert.True(refusing.Leader > 0, "the record was never asked for");
            Assert.True(AwaitGone(refusing.Leader), $"the child {refusing.Leader}, whose session was never recorded, was left running");
        }
        finally
        {
            TryEnding(refusing.Leader);
        }
    }

    /// <summary>
    /// The process that started a contained child, killed - so it never ended the child's session - leaves the session
    /// running and its record behind; the next command over the orchestrator ends the session first, removes the record,
    /// and says so in its lines and in its document.
    /// </summary>
    [Fact]
    public async Task AKilledProcessesContainedChild_IsEndedByTheNextCommandOverItsOrchestrator()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "a contained child there is held in a job, which ends it with the process killed, and nothing is recorded.");

        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var step = kit.Layout.StepDirectory("s1");
        var ids = new TaskCompletionSource<(int Owner, int Contained)>(TaskCreationOptions.RunContinuationsAsynchronously);
        (int Owner, int Contained) said = default;

        try
        {
            var owner = new ProcessRunner(new HostPlatform(), FilePermissionsFactory.Create()).RunAsync(
                TestHost.ChildRequest("contain-recorded-and-wait", CliRunner.CliProgram, step, "300000") with
                {
                    Timeout = LongBound,
                    OnOutputLine = line =>
                    {
                        var words = line.Split(' ').Select(word => word.Split('=')).ToDictionary(pair => pair[0], pair => int.Parse(pair[1], CultureInfo.InvariantCulture));
                        ids.TrySetResult((words["owner"], words["contained"]));
                    },
                },
                Token);

            said = await ids.Task.WaitAsync(LongBound, Token);
            var record = OrchestratorLayout.ChildFile(step, said.Contained);

            Assert.True(File.Exists(record), $"no record of session {said.Contained} at '{record}'");

            // Killed alone, as a crash or a kill -9 ends it: nothing of it ends the session it started.
            using (var killed = Process.GetProcessById(said.Owner))
            {
                killed.Kill(entireProcessTree: false);
            }

            await owner.WaitAsync(LongBound, Token);
            Assert.True(StillAlive(said.Contained), $"the contained child {said.Contained} ended with the process that started it");

            var listed = await ListAsync(kit);

            Assert.Equal(HarnessExit.Success, listed.Completion.ExitCode);
            Assert.True(AwaitGone(said.Contained), $"the contained child {said.Contained} outlived the command that found it left");
            Assert.False(File.Exists(record), $"the record of the ended session is still at '{record}'");
            Assert.Contains(
                $"list-orchestrator: step 's1' of 'o1' left session {said.Contained} ('{TestHost.AssemblyProgram}') running, and the process that started it (pid {said.Owner}) has gone; it was ended",
                Lines(listed.Error));
            Assert.True(
                JsonNode.DeepEquals(new JsonArray(Ended(null, "s1", said.Contained, said.Owner)), listed.Document[EndedChildSessions.Member]),
                listed.Output);
        }
        finally
        {
            TryEnding(said.Contained);
            TryEnding(said.Owner);
        }
    }

    /// <summary>
    /// An agent verb's document says each session finding the agent ended, as <c>ended</c>, and is otherwise the document
    /// it answers with when there is nothing to end - a dry run's made from its facts, and the stopped form of an agent
    /// that is not there alike - and a session left by an agent's step is named with its agent.
    /// </summary>
    [Fact]
    public async Task AnAgentVerbsDocument_SaysWhatFindingItsAgentEnded_AndOtherwiseWhatItAlwaysSaid()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "a contained child there is held in a job, and nothing is recorded.");

        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        await kit.CreateAgentAsync("a1");

        var foldBefore = (await FoldAsync(kit, "a1")).Document;
        var stoppedBefore = (await RefreshAsync(kit, "nobody")).Document;

        var (folded, foldLeader) = await LeftAsync(kit, kit.Layout.AgentStepDirectory("a1", "s2"));
        var foldAnswer = await FoldAsync(kit, "a1");
        var fold = foldAnswer.Document;

        var (refreshed, refreshLeader) = await LeftAsync(kit, kit.Layout.StepDirectory("s3"));
        var stopped = (await RefreshAsync(kit, "nobody")).Document;

        Assert.True(JsonNode.DeepEquals(new JsonArray(Ended("a1", "s2", foldLeader, Environment.ProcessId)), fold["ended"]), fold.ToJsonString());
        Assert.True(JsonNode.DeepEquals(new JsonArray(Ended(null, "s3", refreshLeader, Environment.ProcessId)), stopped["ended"]), stopped.ToJsonString());

        fold.Remove("ended");
        stopped.Remove("ended");

        Assert.True(JsonNode.DeepEquals(foldBefore, fold), $"{foldBefore.ToJsonString()}\n{fold.ToJsonString()}");
        Assert.True(JsonNode.DeepEquals(stoppedBefore, stopped), $"{stoppedBefore.ToJsonString()}\n{stopped.ToJsonString()}");
        Assert.False(foldBefore.ContainsKey("ended"));
        Assert.False(stoppedBefore.ContainsKey("ended"));

        Assert.Contains(
            $"fold-agent: step 's2' of agent 'a1' of 'o1' left session {foldLeader} ('{TestHost.AssemblyProgram}') running, and the process that started it (pid {Environment.ProcessId}) has gone; it was ended",
            Lines(foldAnswer.Error));

        // Each ended by the sweep, its run hears its end.
        await folded.WaitAsync(LongBound, Token);
        await refreshed.WaitAsync(LongBound, Token);
    }

    /// <summary>
    /// A command that stops after its sweep ended a session - here list-orchestrator, whose list of worktrees, read after
    /// the sweep, throws - answers with the stopped form of its document holding <c>ended</c> whole: what it ended is said
    /// on every exit, not only where the command went through.
    /// </summary>
    [Fact]
    public async Task ACommandThatStopsAfterItsSweepEndedASession_SaysWhatItEndedInItsStoppedDocument()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "a contained child there is held in a job, and nothing is recorded.");

        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var (left, leader) = await LeftAsync(kit, kit.Layout.AgentStepDirectory("a9", "s1"));
        var refusing = Substitute.For<IWorktreeService>();

        refusing.ListAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HarnessException(HarnessExit.CommandFailed, "git would not list the worktrees."));

        var listed = await ListAsync(kit, services => services.AddSingleton(refusing));

        var expected = JsonNode.Parse(OrchestrationReports.ListStopped(HarnessExit.CommandFailed, "git would not list the worktrees."))!.AsObject();
        expected[EndedChildSessions.Member] = new JsonArray(Ended("a9", "s1", leader, Environment.ProcessId));

        Assert.Equal(HarnessExit.CommandFailed, listed.Completion.ExitCode);
        Assert.True(JsonNode.DeepEquals(expected, listed.Document), listed.Output);
        Assert.Equal(
            [.. expected.Select(member => member.Key)],
            listed.Document.Select(member => member.Key));
        Assert.True(AwaitGone(leader), $"the session {leader} the sweep found left outlived it");

        await left.WaitAsync(LongBound, Token);
    }

    /// <summary>A session whose owner still runs is its owner's to end: the sweep leaves it, and its record, as they are.</summary>
    [Fact]
    public async Task ASessionWhoseOwnerRuns_IsLeftAsItIs()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var step = kit.Layout.StepDirectory("s1");
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var (sleeper, leader) = await SleeperAsync(stop.Token);

        try
        {
            kit.Harness.ChildSessions.For(step).Placed(leader, TestHost.AssemblyProgram);
            var record = OrchestratorLayout.ChildFile(step, leader);
            var written = File.ReadAllText(record);

            var listed = await ListAsync(kit);

            Assert.Equal(HarnessExit.Success, listed.Completion.ExitCode);
            Assert.Equal(written, File.ReadAllText(record));
            Assert.True(StillAlive(leader), $"the session {leader}, whose owner runs, was ended");
            Assert.False(listed.Document.ContainsKey(EndedChildSessions.Member), listed.Output);
            Assert.DoesNotContain("left session", listed.Error, StringComparison.Ordinal);
            Assert.DoesNotContain(ChildSessions.RecordKind, listed.Error, StringComparison.Ordinal);
        }
        finally
        {
            await stop.CancelAsync();
            await Record.ExceptionAsync(() => sleeper);
        }
    }

    /// <summary>
    /// A session whose owner has gone, recorded with a start its id's process now does not have, is led by another
    /// process given that id: nothing is ended, and the record, which names nothing any more, goes.
    /// </summary>
    [Fact]
    public async Task ASessionWhoseLeadersIdWasGivenToAnotherProcess_EndsNothing_AndItsRecordGoes()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var step = kit.Layout.StepDirectory("s1");
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var (sleeper, leader) = await SleeperAsync(stop.Token);

        try
        {
            Writer(kit, laterStart: true).For(step).Placed(leader, TestHost.AssemblyProgram);
            var record = OrchestratorLayout.ChildFile(step, leader);

            var listed = await ListAsync(kit);

            Assert.Equal(HarnessExit.Success, listed.Completion.ExitCode);
            Assert.False(File.Exists(record), $"the record naming a session nothing leads any more is still at '{record}'");
            Assert.True(StillAlive(leader), $"the process {leader}, given the recorded leader's id, was ended");
            Assert.False(listed.Document.ContainsKey(EndedChildSessions.Member), listed.Output);
            Assert.DoesNotContain("left session", listed.Error, StringComparison.Ordinal);
            Assert.DoesNotContain(ChildSessions.RecordKind, listed.Error, StringComparison.Ordinal);
        }
        finally
        {
            await stop.CancelAsync();
            await Record.ExceptionAsync(() => sleeper);
        }
    }

    /// <summary>
    /// Every command that works an orchestrator sweeps its records first, its own name on what the sweep says: here a record
    /// that does not read as one, which is said and left as it is, since no process is ended on a record not understood.
    /// </summary>
    [Theory]
    [InlineData(OrchestratorService.CreateCommand)]
    [InlineData(OrchestratorService.ListCommand)]
    [InlineData(OrchestratorService.DeleteCommand)]
    [InlineData(AgentService.CreateCommand)]
    [InlineData(AgentService.SeedCommand)]
    [InlineData(AgentService.RefreshCommand)]
    [InlineData(AgentService.RebaseCommand)]
    [InlineData(AgentService.FoldCommand)]
    [InlineData(AgentService.DeleteCommand)]
    public async Task EveryCommandOverAnOrchestrator_SweepsItsRecordsFirst(string command)
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var step = kit.Layout.StepDirectory("s1");
        var record = OrchestratorLayout.ChildFile(step, 7);

        Directory.CreateDirectory(OrchestratorLayout.ChildrenDirectory(step));
        File.WriteAllText(record, """{ "session": 0, "started": "1", "program": "p", "owner": { "machine": "m", "processId": 1 } }""");

        var outcome = command switch
        {
            OrchestratorService.CreateCommand => await kit.Harness.OrchestratorService.CreateAsync(kit.Main, OrchestrationKit.Orchestrator, "model-a", null, null, Token),
            OrchestratorService.ListCommand => await kit.Harness.OrchestratorService.ListAsync(kit.Main, OrchestrationKit.Orchestrator, false, false, json: false, Token),
            OrchestratorService.DeleteCommand => await kit.Harness.OrchestratorService.DeleteAsync(kit.Main, OrchestrationKit.Orchestrator, deleteEvidence: false, Token),
            AgentService.CreateCommand => await kit.Harness.AgentService.CreateAsync(kit.Main, OrchestrationKit.Orchestrator, "a1", "model-b", empty: false, session: null, Token),
            AgentService.SeedCommand => await kit.Harness.AgentService.SeedAsync(kit.Main, OrchestrationKit.Orchestrator, "nobody", empty: false, force: false, Token),
            AgentService.RefreshCommand => await kit.Harness.AgentService.RefreshAsync(kit.Main, OrchestrationKit.Orchestrator, "nobody", new RefreshRequest(), apply: false, Token),
            AgentService.RebaseCommand => await kit.Harness.AgentService.RebaseAsync(kit.Main, OrchestrationKit.Orchestrator, "nobody", new RebaseRequest(), apply: false, Token),
            AgentService.FoldCommand => await kit.Harness.AgentService.FoldAsync(kit.Main, OrchestrationKit.Orchestrator, "nobody", new FoldAllowances(), apply: false, Token),
            AgentService.DeleteCommand => await kit.Harness.AgentService.DeleteAsync(kit.Main, OrchestrationKit.Orchestrator, "nobody", new FoldAllowances(), apply: false, discardUncommitted: false, Token),
            _ => throw new ArgumentOutOfRangeException(nameof(command), command, "Not a command over an orchestrator."),
        };

        var warning = $"{command}: WARN - '{record}' is the record of a contained child's session, and does not read as one: "
            + "its session is 0, and a session is a process's id. It was left as it is, and nothing it names was ended.";

        Assert.Equal(warning, Lines(kit.Harness.StandardError).FirstOrDefault(line => line.StartsWith($"{command}: ", StringComparison.Ordinal)));
        Assert.True(command == OrchestratorService.DeleteCommand ? !outcome.Succeeded || !File.Exists(record) : File.Exists(record), OrchestrationKit.Describe(outcome));
    }

    /// <summary>
    /// A record a killed process's sweep finds on Windows - never written there, whose contained children end with their
    /// starter - names a session nothing there can end: said, and left as it is.
    /// </summary>
    [Fact]
    public async Task ARecordFoundOnWindows_IsSaidAndLeft()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "a record is written, and its session ended, on Linux and macOS, which the tests above hold.");

        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var step = kit.Layout.StepDirectory("s1");
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var (sleeper, leader) = await SleeperAsync(stop.Token);

        try
        {
            Writer(kit, laterStart: false).For(step).Placed(leader, TestHost.AssemblyProgram);
            var record = OrchestratorLayout.ChildFile(step, leader);

            var listed = await kit.Harness.OrchestratorService.ListAsync(kit.Main, OrchestrationKit.Orchestrator, false, false, json: false, Token);

            Assert.True(listed.Succeeded, OrchestrationKit.Describe(listed));
            Assert.True(File.Exists(record));
            Assert.True(StillAlive(leader));
            Assert.Contains(
                $"list-orchestrator: WARN - '{record}' is the record of a contained child's session, which this system never writes: session {leader} cannot be ended on Windows, "
                + "whose contained children are held in a job that ends them with the process that held it, so none is ever left running for a sweep to end. It was left as it is.",
                Lines(kit.Harness.StandardError));
        }
        finally
        {
            await stop.CancelAsync();
            await Record.ExceptionAsync(() => sleeper);
        }
    }

    /// <summary>The runner that starts contained children through the tool the build makes.</summary>
    private static ProcessRunner ContainingRunner()
        => new(new HostPlatform(), FilePermissionsFactory.Create(), CliRunner.CliProgram);

    /// <summary>A child that says its id and sleeps for <paramref name="milliseconds"/>, named by the whole path of its program.</summary>
    private static ProcessRequest Sleeper(string milliseconds)
        => TestHost.ChildRequest("say-pid-and-sleep", milliseconds) with { FileName = TestHost.AssemblyProgram, Arguments = [milliseconds] };

    /// <summary>A sleeper started contained, as a step's would be, its run and its id once it said it; stopped by <paramref name="stop"/>.</summary>
    private static async Task<(Task<ProcessResult> Run, int Leader)> SleeperAsync(CancellationToken stop)
    {
        var said = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = ContainingRunner().RunAsync(
            Sleeper("300000") with { Contained = true, OnOutputLine = line => _ = Id(line, "pid=") is { } id && said.TrySetResult(id) },
            stop);

        return (run, await said.Task.WaitAsync(LongBound, Token));
    }

    /// <summary>
    /// A contained child started here whose record names an owner that has gone, as a killed process leaves one, in
    /// <paramref name="step"/>: its run, which ends once the sweep ends it, and its id.
    /// </summary>
    private static async Task<(Task Run, int Leader)> LeftAsync(OrchestrationKit kit, string step)
    {
        var (run, leader) = await SleeperAsync(Token);

        Writer(kit, laterStart: false).For(step).Placed(leader, TestHost.AssemblyProgram);

        return (Record.ExceptionAsync(() => run).AsTask(), leader);
    }

    /// <summary>
    /// Records written by a process that has gone: this process's id under a start it does not have - and, where
    /// <paramref name="laterStart"/>, each leader recorded under a start its process does not have either, as if its id
    /// had been given to another since.
    /// </summary>
    private static ChildSessions Writer(OrchestrationKit kit, bool laterStart)
        => new(kit.Harness.FileSystem, new Gone(kit.Harness.Identity, laterStart), new LeftSessions(kit.Harness.Platform, kit.Harness.ProcessTable), kit.Harness.Output, new EndedChildSessions());

    /// <summary>list-orchestrator --json over orchestrator o1, as the command line runs it, over the services <paramref name="replace"/> leaves.</summary>
    private static Task<Answer> ListAsync(OrchestrationKit kit, Action<IServiceCollection>? replace = null)
        => CommandAsync(
            kit,
            OrchestratorService.ListCommand,
            (_, exitCode, summary) => OrchestrationReports.ListStopped(exitCode, summary),
            services => services.GetRequiredService<IOrchestratorService>().ListAsync(kit.Main, OrchestrationKit.Orchestrator, false, false, json: true, Token),
            replace);

    /// <summary>fold-agent --json's dry run of <paramref name="agent"/>, as the command line runs it.</summary>
    private static Task<Answer> FoldAsync(OrchestrationKit kit, string agent)
        => CommandAsync(
            kit,
            AgentService.FoldCommand,
            (_, exitCode, summary) => OrchestrationReports.Stopped(exitCode, summary),
            services => services.GetRequiredService<IAgentService>().FoldAsync(kit.Main, OrchestrationKit.Orchestrator, agent, new FoldAllowances(), apply: false, Token, json: true));

    /// <summary>refresh-agent --json's dry run of <paramref name="agent"/>, as the command line runs it.</summary>
    private static Task<Answer> RefreshAsync(OrchestrationKit kit, string agent)
        => CommandAsync(
            kit,
            AgentService.RefreshCommand,
            (_, exitCode, summary) => OrchestrationReports.Stopped(exitCode, summary),
            services => services.GetRequiredService<IAgentService>().RefreshAsync(kit.Main, OrchestrationKit.Orchestrator, agent, new RefreshRequest(), apply: false, Token, json: true));

    /// <summary>
    /// One operation asked for its document, run as the command line runs one - its session over the tool's own services,
    /// one set per operation, in the main checkout - writing to buffers of its own: what it answered, its lines, and how it
    /// ended.
    /// </summary>
    /// <param name="kit">The repository.</param>
    /// <param name="command">The command's name.</param>
    /// <param name="stopped">The stopped form of its document, as the command line registers it.</param>
    /// <param name="body">What the command does, over the operation's services.</param>
    /// <param name="replace">Puts a service in the place of the tool's own, for a command made to meet what it would not.</param>
    private static async Task<Answer> CommandAsync(
        OrchestrationKit kit,
        string command,
        Func<SessionContext, int, string, string> stopped,
        Func<IServiceProvider, Task<CommandOutcome>> body,
        Action<IServiceCollection>? replace = null)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var host = new DssHarnessHost((options, operation) =>
        {
            var services = new ServiceCollection().AddDssHarness(options, operation, output: _ => new ConsoleHarnessOutput(output, error, verbose: false, home: null));
            replace?.Invoke(services);
            return services.BuildServiceProvider();
        });

        var session = host.Begin(
            new SessionOptions { Command = command, Directory = kit.Main, Prompting = false, Document = DocumentKind.Own },
            new SessionFrontEnd(Stopped: stopped));

        await using (session)
        {
            var completion = await session.RunAsync((scope, _) => body(scope.Services), Token);
            return new Answer(completion, output.ToString(), error.ToString());
        }
    }

    /// <summary>A session ended, as a document's <c>ended</c> names it, of orchestrator o1, with nothing it could not reach.</summary>
    private static JsonObject Ended(string? agent, string step, int session, int owner)
        => new()
        {
            ["orchestrator"] = OrchestrationKit.Orchestrator,
            ["agent"] = agent,
            ["step"] = step,
            ["session"] = session,
            ["program"] = TestHost.AssemblyProgram,
            ["owner"] = owner,
            ["unreached"] = null,
        };

    /// <summary>What an output wrote, line by line.</summary>
    private static string[] Lines(StringWriter written) => Lines(written.ToString());

    /// <summary>What a stream held, line by line.</summary>
    private static string[] Lines(string written)
        => written.Split('\n').Select(line => line.TrimEnd('\r')).ToArray();

    /// <summary>The number after <paramref name="name"/> where <paramref name="line"/> is that and a number.</summary>
    private static int? Id(string line, string name)
        => line.StartsWith(name, StringComparison.Ordinal) && int.TryParse(line[name.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : null;

    /// <summary>Whether <paramref name="process"/> is gone, looking for as long as a session takes to end.</summary>
    private static bool AwaitGone(int process)
    {
        for (var waited = TimeSpan.Zero; waited < TimeSpan.FromSeconds(10); waited += TimeSpan.FromMilliseconds(100))
        {
            if (!StillAlive(process))
            {
                return true;
            }

            Thread.Sleep(TimeSpan.FromMilliseconds(100));
        }

        return false;
    }

    /// <summary>Whether <paramref name="process"/> is running now.</summary>
    private static bool StillAlive(int process)
    {
        try
        {
            using var found = Process.GetProcessById(process);
            return !found.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Ends <paramref name="process"/> where the test started it and it still runs.</summary>
    private static void TryEnding(int process)
    {
        if (process <= 0)
        {
            return;
        }

        try
        {
            using var found = Process.GetProcessById(process);
            found.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            // Already gone, or going.
        }
    }

    /// <summary>What one operation answered: how it ended, its standard output - the document alone - and its standard error.</summary>
    private sealed record Answer(CommandCompletion Completion, string Output, string Error)
    {
        /// <summary>The document, the whole of its standard output.</summary>
        public JsonObject Document => JsonNode.Parse(Output)!.AsObject();
    }

    /// <summary>A record that says, in order, what the runner asked of it.</summary>
    private sealed class Heard(List<string> heard) : IContainedSessionRecord
    {
        public void Placed(int leader, string program) => heard.Add($"placed {leader} {program}");

        public void Ended() => heard.Add("ended");
    }

    /// <summary>A record that cannot be written, keeping the leader it was asked to name.</summary>
    private sealed class Refusing : IContainedSessionRecord
    {
        public int Leader { get; private set; }

        public void Placed(int leader, string program)
        {
            Leader = leader;
            throw new IOException("the disk is full");
        }

        public void Ended() => throw new InvalidOperationException("a record never written was taken back");
    }

    /// <summary>
    /// This process as one that has gone would have recorded itself - its id under a start it does not have - and, where
    /// <paramref name="laterStart"/>, every other process under a start it does not have either.
    /// </summary>
    private sealed class Gone(IProcessIdentity real, bool laterStart) : IProcessIdentity
    {
        public int CurrentId => real.CurrentId;

        public string CurrentMachine => real.CurrentMachine;

        public string? Current => "a-start-no-process-had";

        public bool IsAlive(int processId, string? stamp) => real.IsAlive(processId, stamp);

        public string? StampOf(int processId) => laterStart ? "a-later-start" : real.StampOf(processId);

        public bool IsSame(int processId, string stamp) => real.IsSame(processId, stamp);
    }
}
