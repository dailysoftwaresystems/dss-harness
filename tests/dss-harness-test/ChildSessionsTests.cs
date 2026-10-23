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
/// The sessions contained children of an orchestrator's steps lead, and the process groups of what its machine steps' legs
/// start: each recorded in its step's directory on Linux and macOS while it runs - a group while anything is left in it -
/// by the process runner through the tool's own records, and ended by the next command over the orchestrator once the
/// process that started it has gone.
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
                    if (LearnedProcess.In(line, "pid=") is { } id)
                    {
                        leader = id.Id;
                        started = harness.Identity.StampOf(id.Id);

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
            Assert.IsType<ProcessStoppedException>(raised);
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
    /// The runner records a child's session, or its group, only where it would outlive the process that started it - on
    /// Linux and macOS, before the child is said to have started, and taken back after: a contained child's session, and
    /// the group of a child apart from the terminal, which its request names a record for. Never one on Windows, whose
    /// job ends with its starter.
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
                OnOutputLine = line => leader = LearnedProcess.In(line, "pid=")?.Id ?? leader,
            },
            Token).WaitAsync(LongBound, Token);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(
            OperatingSystem.IsWindows() ? ["started"] : [$"placed {leader} {TestHost.AssemblyProgram} group={!contained} started=True", "started", "ended"],
            heard);
    }

    /// <summary>
    /// What is started under a process scope - as a machine step's legs start - is apart from the terminal on Linux and
    /// macOS, leading a process group of its own within its starter's session, and a bound child of it - each phase - is
    /// recorded through the scope as a group before it is said to have started, the record taken back after; on Windows
    /// the job the process holds binds a bound child and nothing is recorded. Outside a scope a child is recorded nowhere, and on
    /// Linux and macOS starts in its starter's group, as any other child.
    /// </summary>
    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task ABoundChildUnderAProcessScope_IsApartFromTheTerminalAndRecordedAsAGroup(bool scoped, bool bound)
    {
        var heard = new List<string>();
        var ids = string.Empty;
        var request = TestHost.ChildRequest("print-ids") with
        {
            FileName = TestHost.AssemblyProgram,
            Arguments = [],
            Bound = bound,
            OnStarted = () => heard.Add("started"),
            OnOutputLine = line => ids = line,
        };
        Task<ProcessResult> run;

        using (scoped ? ProcessScope.Enter(new ProcessScope(() => new Heard(heard))) : null)
        {
            run = ContainingRunner().RunAsync(request, Token);
        }

        Assert.Null(ProcessScope.Current);

        var result = await run.WaitAsync(LongBound, Token);

        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(98, result.ExitCode);
            Assert.Equal(["started"], heard);
            return;
        }

        Assert.Equal(0, result.ExitCode);

        var said = ids.Split(' ').Select(word => word.Split('=')).ToDictionary(pair => pair[0], pair => int.Parse(pair[1], CultureInfo.InvariantCulture));

        Assert.Equal(TestChild.SessionOf(0), said["sid"]);

        if (scoped)
        {
            Assert.Equal(said["pid"], said["pgid"]);
            Assert.Equal(bound ? [$"placed {said["pid"]} {TestHost.AssemblyProgram} group=True started=True", "started", "ended"] : ["started"], heard);
        }
        else
        {
            Assert.Equal(TestChild.GroupOf(0), said["pgid"]);
            Assert.Equal(["started"], heard);
        }
    }

    /// <summary>
    /// A bound child under a process scope that is stopped is ended by its tree first, at once - which reaches what moved to
    /// a group of its own while still the child's, as a test under <c>timeout</c> does, holding the child's output, and
    /// kills it where it stands, never asking it to end and waiting - and the run ends, nothing left holding what it reads.
    /// </summary>
    [Fact]
    public async Task AScopedChildStopped_IsEndedWithWhatOfItsTreeLeftItsGroup()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "a bound child there is held in the job the process holds, which ends what it holds with the process.");

        var heard = new List<string>();
        var held = new TaskCompletionSource<LearnedProcess>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        using var temp = new TempDirectory();
        var asked = temp.Combine("asked.txt");
        Task<ProcessResult> run;
        var holder = LearnedProcess.None;

        using (ProcessScope.Enter(new ProcessScope(() => new Heard(heard))))
        {
            run = ContainingRunner().RunAsync(
                TestHost.ChildRequest("spawn-holder", "sleep", "300000", "own-group-note-term-and-sleep", asked, "300000") with
                {
                    Bound = true,
                    OnOutputLine = line => _ = LearnedProcess.In(line, "holder=") is { } id && held.TrySetResult(id),
                },
                stop.Token);
        }

        try
        {
            holder = await held.Task.WaitAsync(LongBound, Token);

            await stop.CancelAsync();

            // Reached by the child's tree, walked while the child stood - its group, ended after, never holds it - and
            // killed there, never asked first: a holder asked to end and waited for would have said so.
            Assert.IsType<ProcessStoppedException>(await Record.ExceptionAsync(() => run.WaitAsync(LongBound, Token)));
            Assert.True(holder.AwaitGone(), $"the holder {holder}, in a group of its own under the stopped child, outlived the stop");
            Assert.False(File.Exists(asked), $"the holder {holder} was asked to end and waited for, rather than killed with the child's tree");
            Assert.Equal("ended", heard[^1]);
        }
        finally
        {
            await stop.CancelAsync();
            holder.TryEnding();
        }
    }

    /// <summary>
    /// A bound child under a process scope that is stopped is ended with its group, at once, as well as its tree: what its
    /// tree handed on - no one's child, still in the group - goes with it, and its record is taken back.
    /// </summary>
    [Fact]
    public async Task AScopedChildStopped_IsEndedWithWhatItsTreeHandedOnInItsGroup()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "a bound child there is held in the job the process holds, which ends what it holds with the process.");

        var heard = new List<string>();
        var orphan = new TaskCompletionSource<LearnedProcess>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        Task<ProcessResult> run;
        var handedOn = LearnedProcess.None;

        using (ProcessScope.Enter(new ProcessScope(() => new Heard(heard))))
        {
            run = ContainingRunner().RunAsync(
                TestHost.ChildRequest("spawn-orphan-and-sleep", "300000") with
                {
                    Bound = true,
                    OnOutputLine = line => _ = LearnedProcess.In(line, "orphan=") is { } id && orphan.TrySetResult(id),
                },
                stop.Token);
        }

        try
        {
            handedOn = await orphan.Task.WaitAsync(LongBound, Token);
            Assert.True(handedOn.Running, $"the process {handedOn} the child's tree handed on had ended before the stop");

            await stop.CancelAsync();

            Assert.IsType<ProcessStoppedException>(await Record.ExceptionAsync(() => run.WaitAsync(LongBound, Token)));
            Assert.True(handedOn.AwaitGone(), $"the process {handedOn} the stopped child's tree handed on, in its group, outlived the stop");
            Assert.Equal("ended", heard[^1]);
        }
        finally
        {
            await stop.CancelAsync();
            handedOn.TryEnding();
        }
    }

    /// <summary>
    /// A bound child under a process scope that ends on its own, leaving a process in its group - a server its test left -
    /// keeps its record: what it left lives on while the process that started it does, as a bound child's leftovers do,
    /// and once that process has ended the next command over the orchestrator ends it, its leader long gone, and says so
    /// as a process group.
    /// </summary>
    [Fact]
    public async Task WhatAScopedChildLeftInItsGroup_IsEndedByTheNextCommandOnceItsStarterHasEnded()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "a bound child there is held in the job the process holds, which ends what it left with the process, and nothing is recorded.");

        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var step = kit.Layout.StepDirectory("s1");
        var said = new TaskCompletionSource<(LearnedProcess Owner, LearnedProcess Orphan)>(TaskCreationOptions.RunContinuationsAsynchronously);
        (LearnedProcess Owner, LearnedProcess Orphan) ids = default;

        try
        {
            var owner = await new ProcessRunner(new HostPlatform(), FilePermissionsFactory.Create(), CliRunner.CliProgram).RunAsync(
                TestHost.ChildRequest("scoped-recorded-and-wait", CliRunner.CliProgram, step, "spawn-orphan", "300000") with
                {
                    Timeout = LongBound,
                    OnOutputLine = line =>
                    {
                        var words = line.Split(' ').Select(word => word.Split('=')).ToDictionary(pair => pair[0], pair => int.Parse(pair[1], CultureInfo.InvariantCulture));
                        said.TrySetResult((LearnedProcess.Of(words["owner"]), LearnedProcess.Of(words["orphan"])));
                    },
                },
                Token).WaitAsync(LongBound, Token);

            ids = await said.Task.WaitAsync(LongBound, Token);

            Assert.Equal(0, owner.ExitCode);
            Assert.True(ids.Orphan.Running, $"what the scoped child left, {ids.Orphan}, ended with it");

            var record = Assert.Single(Directory.GetFiles(OrchestratorLayout.ChildrenDirectory(step)));
            var leader = JsonNode.Parse(File.ReadAllText(record))!["session"]!.GetValue<int>();

            var listed = await ListAsync(kit);

            Assert.Equal(HarnessExit.Success, listed.Completion.ExitCode);
            Assert.True(ids.Orphan.AwaitGone(), $"what the scoped child left in its group, {ids.Orphan}, outlived the command that found it");
            Assert.False(File.Exists(record), $"the record of the ended group is still at '{record}'");
            Assert.Contains(
                $"list-orchestrator: step 's1' of 'o1' left process group {leader} ('{TestHost.AssemblyProgram}') running, and the process that started it (pid {ids.Owner}) has gone; it was ended",
                Lines(listed.Error));
        }
        finally
        {
            ids.Orphan.TryEnding();
        }
    }

    /// <summary>
    /// A process group a leg's phase leads, ended as a sweep ends one its starter left: everything in the group, and while
    /// its leader stands what of its tree left the group - as a test under <c>timeout</c> moves itself to a group of its
    /// own - and nothing else of the session it shares with its starter.
    /// </summary>
    [Fact]
    public async Task AGroupLeftRunning_IsEndedWithWhatOfItsTreeLeftTheGroup_AndNothingElseOfItsSession()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "a bound child there is held in a job, and nothing is recorded or swept.");

        var harness = new HarnessFactory();
        var heard = new List<string>();
        var held = new TaskCompletionSource<LearnedProcess>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var bystander = Process.Start(TestHost.StartOf(TestHost.ChildRequest("sleep", "300000")))!;
        Task<ProcessResult> run;
        LearnedProcess holder = default;
        LearnedProcess leader = default;

        using (ProcessScope.Enter(new ProcessScope(() => new Heard(heard))))
        {
            run = ContainingRunner().RunAsync(
                TestHost.ChildRequest("spawn-holder", "sleep", "300000", "own-group-and-sleep", "300000") with
                {
                    Bound = true,
                    OnOutputLine = line => _ = LearnedProcess.In(line, "holder=") is { } id && held.TrySetResult(id),
                },
                Token);
        }

        try
        {
            holder = await held.Task.WaitAsync(LongBound, Token);
            leader = LearnedProcess.Of(int.Parse(heard[0].Split(' ')[1], CultureInfo.InvariantCulture));

            var ending = await new LeftSessions(harness.Platform, harness.ProcessTable).EndAsync(leader.Id, group: true).WaitAsync(LongBound, Token);

            Assert.Equal(new HeldEnding(Found: true, null), ending);
            Assert.True(leader.AwaitGone(), $"the group's leader {leader} outlived its end");
            Assert.True(holder.AwaitGone(), $"the holder {holder}, in a group of its own under the leader, outlived the group's end");
            Assert.False(bystander.HasExited, "a process of the starter's own session was ended with the group");

            await Record.ExceptionAsync(() => run.WaitAsync(LongBound, Token));
        }
        finally
        {
            holder.TryEnding();
            leader.TryEnding();
            bystander.Kill();
        }
    }

    /// <summary>
    /// The process that started a bound child under a process scope - an engine running a machine step's leg - killed, so
    /// it never ended the child: on Linux and macOS the child's group runs on, a holder it put in a group of its own with
    /// it, and its record is left behind; the next command over the orchestrator ends the group and its tree, removes the
    /// record and says so as a process group. On Windows the job that bound the child ends it, and all it started, with
    /// the process killed, and nothing was ever recorded.
    /// </summary>
    [Fact]
    public async Task AKilledProcessesScopedChild_IsEndedWithItOrByTheNextCommandOverItsOrchestrator()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var step = kit.Layout.StepDirectory("s1");
        var ids = new TaskCompletionSource<(LearnedProcess Owner, LearnedProcess Holder)>(TaskCreationOptions.RunContinuationsAsynchronously);
        (LearnedProcess Owner, LearnedProcess Holder) said = default;
        LearnedProcess child = default;

        try
        {
            var owner = new ProcessRunner(new HostPlatform(), FilePermissionsFactory.Create(), CliRunner.CliProgram).RunAsync(
                TestHost.ChildRequest(
                    "scoped-recorded-and-wait",
                    CliRunner.CliProgram,
                    step,
                    "spawn-holder",
                    "sleep",
                    "300000",
                    OperatingSystem.IsWindows() ? "sleep" : "own-group-and-sleep",
                    "300000") with
                {
                    Timeout = LongBound,
                    OnOutputLine = line =>
                    {
                        var words = line.Split(' ').Select(word => word.Split('=')).ToDictionary(pair => pair[0], pair => int.Parse(pair[1], CultureInfo.InvariantCulture));
                        ids.TrySetResult((LearnedProcess.Of(words["owner"]), LearnedProcess.Of(words["holder"])));
                    },
                },
                Token);

            said = await ids.Task.WaitAsync(LongBound, Token);

            if (!OperatingSystem.IsWindows())
            {
                child = LearnedProcess.Of(JsonNode.Parse(File.ReadAllText(Assert.Single(Directory.GetFiles(OrchestratorLayout.ChildrenDirectory(step)))))!["session"]!.GetValue<int>());
            }

            // Killed alone, as a crash or a kill -9 ends it.
            using (var killed = Process.GetProcessById(said.Owner.Id))
            {
                killed.Kill(entireProcessTree: false);
            }

            await owner.WaitAsync(LongBound, Token);

            if (OperatingSystem.IsWindows())
            {
                Assert.True(said.Holder.AwaitGone(), $"the bound child's holder {said.Holder} outlived the process whose job held it");
                Assert.False(Directory.Exists(OrchestratorLayout.ChildrenDirectory(step)), "a child bound by a job was recorded");
                return;
            }

            var record = OrchestratorLayout.ChildFile(step, child.Id);

            Assert.True(child.Running, $"the scoped child {child} ended with the process that started it");
            Assert.True(said.Holder.Running, $"the scoped child's holder {said.Holder} ended with the process that started it");
            Assert.True(
                JsonNode.DeepEquals(JsonValue.Create(true), JsonNode.Parse(File.ReadAllText(record))!["group"]),
                $"the record at '{record}' does not name a group");

            var listed = await ListAsync(kit);

            Assert.Equal(HarnessExit.Success, listed.Completion.ExitCode);
            Assert.True(child.AwaitGone(), $"the scoped child {child} outlived the command that found it left");
            Assert.True(said.Holder.AwaitGone(), $"the holder {said.Holder}, in a group of its own under the scoped child, outlived the command that found it left");
            Assert.False(File.Exists(record), $"the record of the ended group is still at '{record}'");
            Assert.Contains(
                $"list-orchestrator: step 's1' of 'o1' left process group {child} ('{TestHost.AssemblyProgram}') running, and the process that started it (pid {said.Owner}) has gone; it was ended",
                Lines(listed.Error));
            Assert.True(
                JsonNode.DeepEquals(new JsonArray(Ended(null, "s1", child.Id, said.Owner.Id, group: true)), listed.Document[EndedChildSessions.Member]),
                listed.Output);
        }
        finally
        {
            said.Holder.TryEnding();
            child.TryEnding();
            said.Owner.TryEnding();
        }
    }

    /// <summary>
    /// A contained child whose session cannot be recorded is not run - nor a bound one under a process scope whose group
    /// cannot be: it is ended, never said to have started, and its start fails saying why - a child no record names is one
    /// a killed tool would leave running unseen.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AContainedChildWhoseSessionCannotBeRecorded_IsAStartThatFailed(bool contained)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "nothing is recorded there, which AContainedChild_IsRecordedOnlyWhereItsSessionWouldOutliveItsStarter holds.");

        var refusing = new Refusing();
        var started = false;
        Task<ProcessResult> run;

        using (contained ? null : ProcessScope.Enter(new ProcessScope(() => refusing)))
        {
            run = ContainingRunner().RunAsync(
                Sleeper("300000") with { Contained = contained, Bound = !contained, SessionRecord = contained ? refusing : null, OnStarted = () => started = true },
                Token);
        }

        var raised = await Assert.ThrowsAsync<ProgramStartException>(() => run.WaitAsync(LongBound, Token));

        try
        {
            Assert.Equal(TestHost.AssemblyProgram, raised.FileName);
            Assert.Equal($"'{TestHost.AssemblyProgram}' could not be started: its {(contained ? "session" : "process group")} could not be recorded: the disk is full", raised.Message);
            Assert.False(started, "a child whose session was not recorded was said to have started");
            Assert.True(refusing.Leader.Id > 0, "the record was never asked for");
            Assert.True(refusing.Leader.AwaitGone(), $"the child {refusing.Leader}, whose session was never recorded, was left running");
        }
        finally
        {
            refusing.Leader.TryEnding();
        }
    }

    /// <summary>
    /// A child whose record a defect keeps from being written is not left running either: it is ended, and the defect is
    /// raised as it is, never taken for a start that failed.
    /// </summary>
    [Fact]
    public async Task AChildWhoseRecordADefectRefuses_IsEnded_AndTheDefectRaised()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "nothing is recorded there, which AContainedChild_IsRecordedOnlyWhereItsSessionWouldOutliveItsStarter holds.");

        var refusing = new Refusing(() => new InvalidOperationException("a defect of the record's"));
        var started = false;

        var run = ContainingRunner().RunAsync(
            Sleeper("300000") with { Contained = true, SessionRecord = refusing, OnStarted = () => started = true },
            Token);

        var raised = await Assert.ThrowsAsync<InvalidOperationException>(() => run.WaitAsync(LongBound, Token));

        try
        {
            Assert.Equal("a defect of the record's", raised.Message);
            Assert.False(started, "a child whose session was not recorded was said to have started");
            Assert.True(refusing.Leader.AwaitGone(), $"the child {refusing.Leader}, whose session a defect left unrecorded, was left running");
        }
        finally
        {
            refusing.Leader.TryEnding();
        }
    }

    /// <summary>
    /// A contained child's record is given the start its step said of itself before it became the program, which is the
    /// child's own: the record never has to read it of a child that may already have ended.
    /// </summary>
    [Fact]
    public async Task AContainedChildsRecord_IsGivenTheStartItsStepSaid_WhichIsTheChildsOwn()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "nothing is recorded there, which AContainedChild_IsRecordedOnlyWhereItsSessionWouldOutliveItsStarter holds.");

        var capturing = new Capturing();
        var said = new TaskCompletionSource<LearnedProcess>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);

        var run = ContainingRunner().RunAsync(
            Sleeper("300000") with { Contained = true, SessionRecord = capturing, OnOutputLine = line => _ = LearnedProcess.In(line, "pid=") is { } id && said.TrySetResult(id) },
            stop.Token);

        try
        {
            var leader = await said.Task.WaitAsync(LongBound, Token);

            Assert.Equal(leader.Id, capturing.Leader);
            Assert.NotNull(capturing.Started);
            Assert.Equal(new ProcessIdentity(new HostPlatform()).StampOf(leader.Id), capturing.Started);
        }
        finally
        {
            await stop.CancelAsync();
            await Record.ExceptionAsync(() => run);
        }
    }

    /// <summary>
    /// A record is written under the start the step that placed its leader said - which the program it became keeps - so
    /// a leader that has already ended, leaving what it started in its group, is recorded all the same.
    /// </summary>
    [Fact]
    public async Task ARecord_IsWrittenUnderTheStartItsStepSaid_HoweverSoonItsLeaderEnded()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var step = kit.Layout.StepDirectory("s1");

        kit.Harness.ChildSessions.For(step).Placed(NoProcess, "the-step's-own-start", TestHost.AssemblyProgram, group: true);

        var record = JsonNode.Parse(File.ReadAllText(OrchestratorLayout.ChildFile(step, NoProcess)))!;

        Assert.Equal(
            (NoProcess, "the-step's-own-start", true, TestHost.AssemblyProgram, Environment.ProcessId),
            (record["session"]!.GetValue<int>(), record["started"]!.GetValue<string>(), record["group"]!.GetValue<bool>(), record["program"]!.GetValue<string>(), record["owner"]!["processId"]!.GetValue<int>()));
    }

    /// <summary>
    /// A record whose leader's start nothing said and nothing can read - the leader gone - is never written, and never
    /// dropped unsaid: the writer raises why, which the runner makes a start that failed, the child ended.
    /// </summary>
    [Fact]
    public async Task ARecordWhoseLeadersStartCannotBeRead_IsNeverWritten_AndSaysWhy()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var step = kit.Layout.StepDirectory("s1");

        var raised = Assert.Throws<IOException>(() => kit.Harness.ChildSessions.For(step).Placed(NoProcess, started: null, TestHost.AssemblyProgram, group: true));

        Assert.Equal($"the start of process {NoProcess}, which leads it, could not be read, so nothing could tell it from a later process given its id", raised.Message);
        Assert.False(File.Exists(OrchestratorLayout.ChildFile(step, NoProcess)));
    }

    /// <summary>A record that would not read back is never written: refused as the defect it is, as every record writer refuses one.</summary>
    [Fact]
    public async Task ARecordThatWouldNotReadBack_IsNeverWritten()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var step = kit.Layout.StepDirectory("s1");

        var raised = Assert.Throws<InvalidOperationException>(() => kit.Harness.ChildSessions.For(step).Placed(0, "a-start", TestHost.AssemblyProgram, group: false));

        Assert.Equal($"Refusing to write '{OrchestratorLayout.ChildFile(step, 0)}', which would not read back: its session is 0, and a session is a process's id.", raised.Message);
        Assert.False(File.Exists(OrchestratorLayout.ChildFile(step, 0)));
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
        var ids = new TaskCompletionSource<(LearnedProcess Owner, LearnedProcess Contained)>(TaskCreationOptions.RunContinuationsAsynchronously);
        (LearnedProcess Owner, LearnedProcess Contained) said = default;

        try
        {
            var owner = new ProcessRunner(new HostPlatform(), FilePermissionsFactory.Create(), CliRunner.CliProgram).RunAsync(
                TestHost.ChildRequest("contain-recorded-and-wait", CliRunner.CliProgram, step, "300000") with
                {
                    Timeout = LongBound,
                    OnOutputLine = line =>
                    {
                        var words = line.Split(' ').Select(word => word.Split('=')).ToDictionary(pair => pair[0], pair => int.Parse(pair[1], CultureInfo.InvariantCulture));
                        ids.TrySetResult((LearnedProcess.Of(words["owner"]), LearnedProcess.Of(words["child"])));
                    },
                },
                Token);

            said = await ids.Task.WaitAsync(LongBound, Token);
            var record = OrchestratorLayout.ChildFile(step, said.Contained.Id);

            Assert.True(File.Exists(record), $"no record of session {said.Contained} at '{record}'");

            // Killed alone, as a crash or a kill -9 ends it: nothing of it ends the session it started.
            using (var killed = Process.GetProcessById(said.Owner.Id))
            {
                killed.Kill(entireProcessTree: false);
            }

            await owner.WaitAsync(LongBound, Token);
            Assert.True(said.Contained.Running, $"the contained child {said.Contained} ended with the process that started it");

            var listed = await ListAsync(kit);

            Assert.Equal(HarnessExit.Success, listed.Completion.ExitCode);
            Assert.True(said.Contained.AwaitGone(), $"the contained child {said.Contained} outlived the command that found it left");
            Assert.False(File.Exists(record), $"the record of the ended session is still at '{record}'");
            Assert.Contains(
                $"list-orchestrator: step 's1' of 'o1' left session {said.Contained} ('{TestHost.AssemblyProgram}') running, and the process that started it (pid {said.Owner}) has gone; it was ended",
                Lines(listed.Error));
            Assert.True(
                JsonNode.DeepEquals(new JsonArray(Ended(null, "s1", said.Contained.Id, said.Owner.Id)), listed.Document[EndedChildSessions.Member]),
                listed.Output);
        }
        finally
        {
            said.Contained.TryEnding();
            said.Owner.TryEnding();
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

        Assert.True(JsonNode.DeepEquals(new JsonArray(Ended("a1", "s2", foldLeader.Id, Environment.ProcessId)), fold["ended"]), fold.ToJsonString());
        Assert.True(JsonNode.DeepEquals(new JsonArray(Ended(null, "s3", refreshLeader.Id, Environment.ProcessId)), stopped["ended"]), stopped.ToJsonString());

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
        expected[EndedChildSessions.Member] = new JsonArray(Ended("a9", "s1", leader.Id, Environment.ProcessId));

        Assert.Equal(HarnessExit.CommandFailed, listed.Completion.ExitCode);
        Assert.True(JsonNode.DeepEquals(expected, listed.Document), listed.Output);
        Assert.Equal(
            [.. expected.Select(member => member.Key)],
            listed.Document.Select(member => member.Key));
        Assert.True(leader.AwaitGone(), $"the session {leader} the sweep found left outlived it");

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
            kit.Harness.ChildSessions.For(step).Placed(leader.Id, started: null, TestHost.AssemblyProgram, group: false);
            var record = OrchestratorLayout.ChildFile(step, leader.Id);
            var written = File.ReadAllText(record);

            var listed = await ListAsync(kit);

            Assert.Equal(HarnessExit.Success, listed.Completion.ExitCode);
            Assert.Equal(written, File.ReadAllText(record));
            Assert.True(leader.Running, $"the session {leader}, whose owner runs, was ended");
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
            Writer(kit, laterStart: true).For(step).Placed(leader.Id, started: null, TestHost.AssemblyProgram, group: false);
            var record = OrchestratorLayout.ChildFile(step, leader.Id);

            var listed = await ListAsync(kit);

            Assert.Equal(HarnessExit.Success, listed.Completion.ExitCode);
            Assert.False(File.Exists(record), $"the record naming a session nothing leads any more is still at '{record}'");
            Assert.True(leader.Running, $"the process {leader}, given the recorded leader's id, was ended");
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
    /// A group whose owner and leader have both gone, with nothing left in it, ends nothing: its record, which names nothing
    /// any more, goes, and nothing is said of it.
    /// </summary>
    [Fact]
    public async Task AGroupWithNothingLeftInIt_EndsNothing_AndItsRecordGoes()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "nothing there ends a recorded group: a record found is said and left, which ARecordFoundOnWindows_IsSaidAndLeft holds.");

        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var step = kit.Layout.StepDirectory("s1");
        using var ended = Process.Start(TestHost.StartOf(TestHost.ChildRequest("exit", "0")))!;

        ended.WaitForExit();

        var record = OrchestratorLayout.ChildFile(step, ended.Id);

        Directory.CreateDirectory(OrchestratorLayout.ChildrenDirectory(step));
        File.WriteAllText(record, new JsonObject
        {
            ["session"] = ended.Id,
            ["group"] = true,
            ["started"] = "a-start-it-had",
            ["program"] = TestHost.AssemblyProgram,
            ["owner"] = new JsonObject { ["machine"] = Environment.MachineName, ["processId"] = ended.Id, ["stamp"] = "a-start-it-had" },
        }.ToJsonString() + "\n");

        var listed = await ListAsync(kit);

        Assert.Equal(HarnessExit.Success, listed.Completion.ExitCode);
        Assert.False(File.Exists(record), $"the record naming a group nothing is left in is still at '{record}'");
        Assert.False(listed.Document.ContainsKey(EndedChildSessions.Member), listed.Output);
        Assert.DoesNotContain("left process group", listed.Error, StringComparison.Ordinal);
    }

    /// <summary>What a sweep ended is counted by kind in one line - the sessions, then the process groups - naming only the kinds it ended.</summary>
    [Fact]
    public void WhatASweepEnded_IsCountedByKind()
    {
        static EndedChildSession Of(bool group) => new("o1", null, "s1", 7, "p", 1, null, group);

        Assert.Equal("1 process group(s)", EndedChildSessions.Counted([Of(group: true)]));
        Assert.Equal("1 session(s)", EndedChildSessions.Counted([Of(group: false)]));
        Assert.Equal("2 session(s), 1 process group(s)", EndedChildSessions.Counted([Of(group: true), Of(group: false), Of(group: false)]));
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

        var warning = $"{command}: WARN - '{record}' is the record of a step's child's session or process group, and does not read as one: "
            + "its session is 0, and a session is a process's id. It was left as it is, and nothing it names was ended.";

        Assert.Equal(warning, Lines(kit.Harness.StandardError).FirstOrDefault(line => line.StartsWith($"{command}: ", StringComparison.Ordinal)));
        Assert.True(command == OrchestratorService.DeleteCommand ? !outcome.Succeeded || !File.Exists(record) : File.Exists(record), OrchestrationKit.Describe(outcome));
    }

    /// <summary>
    /// A record a killed process's sweep finds on Windows - never written there, whose bound and contained children end
    /// with their starter - names a session or a process group nothing there can end: said as what it names, and left as
    /// it is.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ARecordFoundOnWindows_IsSaidAndLeft(bool group)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "a record is written, and its session ended, on Linux and macOS, which the tests above hold.");

        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var step = kit.Layout.StepDirectory("s1");
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var (sleeper, leader) = await SleeperAsync(stop.Token);

        try
        {
            Writer(kit, laterStart: false).For(step).Placed(leader.Id, started: null, TestHost.AssemblyProgram, group);
            var record = OrchestratorLayout.ChildFile(step, leader.Id);

            var listed = await kit.Harness.OrchestratorService.ListAsync(kit.Main, OrchestrationKit.Orchestrator, false, false, json: false, Token);

            Assert.True(listed.Succeeded, OrchestrationKit.Describe(listed));
            Assert.True(File.Exists(record));
            Assert.True(leader.Running);
            Assert.Contains(
                $"list-orchestrator: WARN - '{record}' is the record of a step's child's session or process group, which this system never writes: {(group ? "process group" : "session")} {leader} cannot be ended on Windows, "
                + "whose bound and contained children are held in a job that ends them with the process that held it, so none is ever left running for a sweep to end. It was left as it is.",
                Lines(kit.Harness.StandardError));
        }
        finally
        {
            await stop.CancelAsync();
            await Record.ExceptionAsync(() => sleeper);
        }
    }

    /// <summary>A process id no process of this machine has.</summary>
    private const int NoProcess = int.MaxValue - 3;

    /// <summary>The runner that starts contained children through the tool the build makes.</summary>
    private static ProcessRunner ContainingRunner()
        => new(new HostPlatform(), FilePermissionsFactory.Create(), CliRunner.CliProgram);

    /// <summary>A child that says its id and sleeps for <paramref name="milliseconds"/>, named by the whole path of its program.</summary>
    private static ProcessRequest Sleeper(string milliseconds)
        => TestHost.ChildRequest("say-pid-and-sleep", milliseconds) with { FileName = TestHost.AssemblyProgram, Arguments = [milliseconds] };

    /// <summary>A sleeper started contained, as a step's would be, its run and its id once it said it; stopped by <paramref name="stop"/>.</summary>
    private static async Task<(Task<ProcessResult> Run, LearnedProcess Leader)> SleeperAsync(CancellationToken stop)
    {
        var said = new TaskCompletionSource<LearnedProcess>(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = ContainingRunner().RunAsync(
            Sleeper("300000") with { Contained = true, OnOutputLine = line => _ = LearnedProcess.In(line, "pid=") is { } id && said.TrySetResult(id) },
            stop);

        return (run, await said.Task.WaitAsync(LongBound, Token));
    }

    /// <summary>
    /// A contained child started here whose record names an owner that has gone, as a killed process leaves one, in
    /// <paramref name="step"/>: its run, which ends once the sweep ends it, and its id.
    /// </summary>
    private static async Task<(Task Run, LearnedProcess Leader)> LeftAsync(OrchestrationKit kit, string step)
    {
        var (run, leader) = await SleeperAsync(Token);

        Writer(kit, laterStart: false).For(step).Placed(leader.Id, started: null, TestHost.AssemblyProgram, group: false);

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

    /// <summary>A session or a group ended, as a document's <c>ended</c> names it, of orchestrator o1, with nothing it could not reach.</summary>
    private static JsonObject Ended(string? agent, string step, int session, int owner, bool group = false)
        => new()
        {
            ["orchestrator"] = OrchestrationKit.Orchestrator,
            ["agent"] = agent,
            ["step"] = step,
            ["session"] = session,
            ["group"] = group,
            ["program"] = TestHost.AssemblyProgram,
            ["owner"] = owner,
            ["unreached"] = null,
        };

    /// <summary>What an output wrote, line by line.</summary>
    private static string[] Lines(StringWriter written) => Lines(written.ToString());

    /// <summary>What a stream held, line by line.</summary>
    private static string[] Lines(string written)
        => written.Split('\n').Select(line => line.TrimEnd('\r')).ToArray();

    /// <summary>What one operation answered: how it ended, its standard output - the document alone - and its standard error.</summary>
    private sealed record Answer(CommandCompletion Completion, string Output, string Error)
    {
        /// <summary>The document, the whole of its standard output.</summary>
        public JsonObject Document => JsonNode.Parse(Output)!.AsObject();
    }

    /// <summary>A record that says, in order, what the runner asked of it.</summary>
    private sealed class Heard(List<string> heard) : IContainedSessionRecord
    {
        public void Placed(int leader, string? started, string program, bool group) => heard.Add($"placed {leader} {program} group={group} started={started is { Length: > 0 }}");

        public void Ended() => heard.Add("ended");
    }

    /// <summary>A record that keeps what the runner gave it: the leader and its start.</summary>
    private sealed class Capturing : IContainedSessionRecord
    {
        public volatile string? Started;

        public int Leader { get; private set; }

        public void Placed(int leader, string? started, string program, bool group)
        {
            Leader = leader;
            Started = started;
        }

        public void Ended()
        {
        }
    }

    /// <summary>A record that cannot be written - as a full disk refuses it, unless told otherwise - keeping the leader it was asked to name.</summary>
    private sealed class Refusing(Func<Exception>? fault = null) : IContainedSessionRecord
    {
        public LearnedProcess Leader { get; private set; }

        public void Placed(int leader, string? started, string program, bool group)
        {
            Leader = LearnedProcess.Of(leader);
            throw fault?.Invoke() ?? new IOException("the disk is full");
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
