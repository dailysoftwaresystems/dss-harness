using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using DssHarness.Core.Configuration;
using DssHarness.Core.Execution;
using DssHarness.Core.Results;
using DssHarness.Core.Runs;

namespace DssHarness.Tests;

/// <summary>
/// <c>stop-run</c>: a running build, test, run or mutation sweep stopped from another terminal - asked of its dispatcher
/// through a request beside its records, waited for, or its process ended where it stands - and the dispatcher's watch for
/// the request.
/// </summary>
public sealed class RunStopTests
{
    private const string Run = "20261022-035935-e6571255";

    /// <summary>Every member of the command's document, on every exit, in order.</summary>
    private static readonly string[] Members = ["exitCode", "summary", "run", "runDirectory", "dispatcher", "stopped", "records"];

    /// <summary>What no run is named as is refused before anything is read, as a usage error, its document saying nothing stopped.</summary>
    [Fact]
    public async Task WhatNoRunIsNamedAs_IsAUsageError()
    {
        using var temp = new TempDirectory();
        var (service, _, _) = Service(temp);

        var outcome = await service.StopAsync(temp.Path, "r1", now: false, json: true, TestContext.Current.CancellationToken);

        using var document = JsonDocument.Parse(Assert.Single(outcome.Data));
        var root = document.RootElement;

        Assert.Equal(HarnessExit.UsageError, outcome.ExitCode);
        Assert.Equal("'r1' is no run: a run is named as the line its command began with names it, 'run 20261022-035935-e6571255'", outcome.Message);
        Assert.Equal(Members, root.EnumerateObject().Select(member => member.Name));
        Assert.Equal((JsonValueKind.Null, false, 0), (root.GetProperty("run").ValueKind, root.GetProperty("stopped").GetBoolean(), root.GetProperty("records").EnumerateObject().Count()));
    }

    /// <summary>The document the command answers with where it stops before reaching a run has every member its own document has.</summary>
    [Fact]
    public void TheStoppedDocument_HasTheMembersOfTheCommandsOwn()
    {
        using var document = JsonDocument.Parse(RunStopReports.Stopped(HarnessExit.Cancelled, "cancelled"));

        Assert.Equal(Members, document.RootElement.EnumerateObject().Select(member => member.Name));
    }

    /// <summary>
    /// A run never begun in the tree, and one that has ended - nothing holds its records - is nothing to stop, refused
    /// naming where its records are looked for; a stop left beside an ended run goes with it, and nothing is written.
    /// </summary>
    [Fact]
    public async Task ARunNeverBegunHere_OrEnded_IsNothingToStop()
    {
        using var temp = new TempDirectory();
        var (service, stops, layout) = Service(temp);

        var never = await service.StopAsync(temp.Path, Run, now: false, json: false, TestContext.Current.CancellationToken);

        Directory.CreateDirectory(layout.RunDirectory(Run));
        stops.Ask(layout.RunDirectory(Run));
        var ended = await service.StopAsync(temp.Path, Run, now: false, json: false, TestContext.Current.CancellationToken);

        Assert.Equal(
            (HarnessExit.Refused, $"no run {Run} was begun in '{layout.RunsDirectory}': a run's records are kept in the tree it ran in, so stop it from there ('--directory')"),
            (never.ExitCode, never.Message));
        Assert.Equal((HarnessExit.Refused, $"run {Run} has ended: nothing holds '{layout.RunDirectory(Run)}', so there is nothing to stop"), (ended.ExitCode, ended.Message));
        Assert.False(File.Exists(RunStops.FileOf(layout.RunDirectory(Run))), "the stop left beside the ended run stayed");
    }

    /// <summary>
    /// A run whose dispatcher ended without giving its records up - killed - is nothing to stop either: its claim is
    /// released there, as the next run in its tree would release it, with any stop asked of it.
    /// </summary>
    [Fact]
    public async Task ARunWhoseDispatcherWasKilled_IsNothingToStop_AndItsClaimIsReleased()
    {
        using var temp = new TempDirectory();
        var (service, stops, layout) = Service(temp);
        var directory = layout.RunDirectory(Run);

        Directory.CreateDirectory(directory);
        Owned(directory, Environment.MachineName, int.MaxValue - 1, "a-process-that-has-gone");
        stops.Ask(directory);

        var outcome = await service.StopAsync(temp.Path, Run, now: false, json: false, TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.Refused, outcome.ExitCode);
        Assert.StartsWith($"run {Run}'s dispatcher (pid {int.MaxValue - 1}, run {Run}, since 2026-10-22 03:59:35Z) has ended without giving up its records", outcome.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(LogOwnership.OwnerFile(directory)));
        Assert.False(File.Exists(RunStops.FileOf(directory)));
    }

    /// <summary>
    /// The stop is asked of a live dispatcher and waited for: once it has taken the request and given its records up, the
    /// run is said stopped, exit 0, naming its records and the run's record of the stop in text and document, and the
    /// request is gone.
    /// </summary>
    [Fact]
    public async Task AStopAskedOfALiveDispatcher_IsWaitedFor_UntilItGivesItsRecordsUp()
    {
        using var temp = new TempDirectory();
        var (service, stops, layout) = Service(temp);
        var harness = new HarnessFactory();
        var directory = layout.RunDirectory(Run);
        var ownership = new LogOwnership(harness.FileSystem, harness.Output, harness.Identity);

        Assert.True((await ownership.ClaimAsync(directory, RunId.Parse(Run), cancellationToken: TestContext.Current.CancellationToken)).Taken);

        // The dispatcher: takes the request at a look of its own, then gives its records up.
        var dispatcher = Task.Run(
            async () =>
            {
                while (stops.Take(directory, "test") is null)
                {
                    await Task.Delay(20, TestContext.Current.CancellationToken);
                }

                await ownership.ReleaseAsync(directory, RunId.Parse(Run), TestContext.Current.CancellationToken);
            },
            TestContext.Current.CancellationToken);

        var outcome = await service.StopAsync(temp.Path, Run, now: false, json: true, TestContext.Current.CancellationToken);
        await dispatcher;

        using var document = JsonDocument.Parse(Assert.Single(outcome.Data));
        var root = document.RootElement;

        Assert.Equal(HarnessExit.Success, outcome.ExitCode);
        Assert.StartsWith($"run {Run} stopped: its dispatcher (pid {Environment.ProcessId}, run {Run}, since ", outcome.Message, StringComparison.Ordinal);
        Assert.EndsWith(") took the stop and gave up its records", outcome.Message, StringComparison.Ordinal);
        Assert.Equal([$"logs: {directory}", $"stop: {RunStops.TakenFileOf(directory)}"], outcome.Details);
        Assert.Equal(Members, root.EnumerateObject().Select(member => member.Name));
        Assert.Equal(
            (Run, directory, outcome.Message[$"run {Run} stopped: its dispatcher (".Length..outcome.Message.IndexOf(") took the stop", StringComparison.Ordinal)]),
            (root.GetProperty("run").GetString(), root.GetProperty("runDirectory").GetString(), root.GetProperty("dispatcher").GetString()));
        Assert.True(root.GetProperty("stopped").GetBoolean());
        Assert.Equal(RunStops.TakenFileOf(directory), root.GetProperty("records").GetProperty("stop").GetString());
        Assert.False(File.Exists(RunStops.FileOf(directory)), "the request was left behind the stopped run");
        Assert.True(File.Exists(RunStops.TakenFileOf(directory)), "the run's record of the stop it took was not kept");
    }

    /// <summary>
    /// Two terminals ask the same run to stop at once: its dispatcher takes one request, and both hear that the run was
    /// stopped - the stop taken is the run's own record, never one asker's to read and remove.
    /// </summary>
    [Fact]
    public async Task TwoAskingAtOnce_BothHearTheRunStopped()
    {
        using var temp = new TempDirectory();
        var (service, stops, layout) = Service(temp);
        var harness = new HarnessFactory();
        var directory = layout.RunDirectory(Run);
        var ownership = new LogOwnership(harness.FileSystem, harness.Output, harness.Identity);

        Assert.True((await ownership.ClaimAsync(directory, RunId.Parse(Run), cancellationToken: TestContext.Current.CancellationToken)).Taken);

        var first = service.StopAsync(temp.Path, Run, now: false, json: false, TestContext.Current.CancellationToken);
        var second = service.StopAsync(temp.Path, Run, now: false, json: false, TestContext.Current.CancellationToken);

        while (stops.Take(directory, "test") is null)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        await ownership.ReleaseAsync(directory, RunId.Parse(Run), TestContext.Current.CancellationToken);
        var outcomes = await Task.WhenAll(first, second);

        Assert.All(outcomes, outcome => Assert.Equal(HarnessExit.Success, outcome.ExitCode));
        Assert.All(outcomes, outcome => Assert.EndsWith(") took the stop and gave up its records", outcome.Message, StringComparison.Ordinal));
        Assert.False(File.Exists(RunStops.FileOf(directory)), "a request was left behind the stopped run");
    }

    /// <summary>
    /// A run that ends on its own before its dispatcher takes the stop is never said to have been stopped: its dispatcher's
    /// end takes back the request it never took, and the command, finding it untaken, says nothing was stopped (13).
    /// </summary>
    [Fact]
    public async Task ARunThatEndsBeforeItTakesTheStop_IsNotSaidStopped()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();

        // The dispatcher's clock never moves: it looks once, at its start, and never again.
        var dispatcherStops = new RunStops(harness.FileSystem, harness.Identity, harness.Output, new LogOwnership(harness.FileSystem, harness.Output, harness.Identity), new TimerClock());
        var (service, _, layout) = Service(temp);
        var directory = layout.RunDirectory(Run);
        var ownership = new LogOwnership(harness.FileSystem, harness.Output, harness.Identity);

        Assert.True((await ownership.ClaimAsync(directory, RunId.Parse(Run), cancellationToken: TestContext.Current.CancellationToken)).Taken);
        var watch = dispatcherStops.Watch(directory, "test", TestContext.Current.CancellationToken);

        // The dispatcher ends, its work done, once the stop is asked and before it looks again.
        var dispatcher = Task.Run(
            async () =>
            {
                while (!File.Exists(RunStops.FileOf(directory)))
                {
                    await Task.Delay(20, TestContext.Current.CancellationToken);
                }

                await watch.EndThenAsync(() => ownership.ReleaseAsync(directory, RunId.Parse(Run), TestContext.Current.CancellationToken));
            },
            TestContext.Current.CancellationToken);

        var outcome = await service.StopAsync(temp.Path, Run, now: false, json: true, TestContext.Current.CancellationToken);
        await dispatcher;

        using var document = JsonDocument.Parse(Assert.Single(outcome.Data));

        Assert.Equal(HarnessExit.Refused, outcome.ExitCode);
        Assert.StartsWith($"run {Run} ended before its dispatcher (pid {Environment.ProcessId}, run {Run}, since ", outcome.Message, StringComparison.Ordinal);
        Assert.EndsWith(") took the stop, so nothing was stopped; the stop was taken back", outcome.Message, StringComparison.Ordinal);
        Assert.Equal([$"logs: {directory}", $"stop: {RunStops.FileOf(directory)}"], outcome.Details);
        Assert.Equal(RunStops.FileOf(directory), document.RootElement.GetProperty("records").GetProperty("stop").GetString());
        Assert.False(document.RootElement.GetProperty("stopped").GetBoolean());
        Assert.False(watch.Stopping.IsCancellationRequested, "the run that ended was stopped");
        Assert.False(File.Exists(RunStops.FileOf(directory)));
    }

    /// <summary>
    /// A dispatcher that has not taken the stop within the wait leaves it asked, refused naming --now and the request; one
    /// that took it and is still stopping - a set-up's down line among what it waits for - is said to be stopping, naming
    /// the run's record of the stop and that --now would end it part way through.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ADispatcherThatDoesNotStopInTime_IsSaidAsItStands(bool tookTheStop)
    {
        using var temp = new TempDirectory();
        var (service, _, layout) = Service(temp, new JumpingClock());
        var directory = layout.RunDirectory(Run);

        Directory.CreateDirectory(directory);
        Owned(directory, Environment.MachineName, Environment.ProcessId, new ProcessIdentity(new Core.Platform.HostPlatform()).Current);

        if (tookTheStop)
        {
            // What its dispatcher wrote as it took the stop: the run's record of it.
            File.WriteAllText(RunStops.TakenFileOf(directory), "{}");
        }

        var outcome = await service.StopAsync(temp.Path, Run, now: false, json: true, TestContext.Current.CancellationToken);

        using var document = JsonDocument.Parse(Assert.Single(outcome.Data));
        var record = tookTheStop ? RunStops.TakenFileOf(directory) : RunStops.FileOf(directory);

        Assert.Equal(HarnessExit.Refused, outcome.ExitCode);
        Assert.EndsWith(tookTheStop ? ") took the stop and is still stopping after 60 s" : ") did not take the stop within 60 s; the stop stays asked", outcome.Message, StringComparison.Ordinal);
        Assert.Equal(
            [tookTheStop ? $"stop-run {Run} --now ends its process where it stands, part way through its stop" : $"stop-run {Run} --now ends its process where it stands", $"stop: {record}"],
            outcome.Details);
        Assert.Equal(record, document.RootElement.GetProperty("records").GetProperty("stop").GetString());
    }

    /// <summary>
    /// A wait the caller interrupts ends cancelled (130), said as the stop stands: asked still, for the dispatcher's next
    /// look, naming the request; or taken, the run stopping, naming the run's record of it.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AWaitTheCallerInterrupts_EndsCancelled_SaidAsTheStopStands(bool tookTheStop)
    {
        using var temp = new TempDirectory();
        var (service, _, layout) = Service(temp);
        var directory = layout.RunDirectory(Run);
        using var interrupt = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        Directory.CreateDirectory(directory);
        Owned(directory, Environment.MachineName, Environment.ProcessId, new ProcessIdentity(new Core.Platform.HostPlatform()).Current);

        if (tookTheStop)
        {
            File.WriteAllText(RunStops.TakenFileOf(directory), "{}");
        }

        interrupt.CancelAfter(TimeSpan.FromMilliseconds(500));

        var outcome = await service.StopAsync(temp.Path, Run, now: false, json: true, interrupt.Token);

        using var document = JsonDocument.Parse(Assert.Single(outcome.Data));
        var record = tookTheStop ? RunStops.TakenFileOf(directory) : RunStops.FileOf(directory);

        Assert.Equal(
            (HarnessExit.Cancelled, tookTheStop
                ? $"waiting for run {Run} to stop was cancelled; its dispatcher took the stop and is stopping"
                : $"waiting for run {Run} to stop was cancelled; the stop stays asked, and its dispatcher takes it at its next look"),
            (outcome.ExitCode, outcome.Message));
        Assert.Equal([$"stop: {record}"], outcome.Details);
        Assert.Equal(record, document.RootElement.GetProperty("records").GetProperty("stop").GetString());
    }

    /// <summary>
    /// --now ends no process of another machine, and never this one: each refused, nothing changed.
    /// </summary>
    [Fact]
    public async Task Now_EndsNoProcessOfAnotherMachine_NorThisOne()
    {
        using var temp = new TempDirectory();
        var (service, _, layout) = Service(temp);
        var directory = layout.RunDirectory(Run);

        Directory.CreateDirectory(directory);
        Owned(directory, Environment.MachineName, Environment.ProcessId, new ProcessIdentity(new Core.Platform.HostPlatform()).Current);
        var itself = await service.StopAsync(temp.Path, Run, now: true, json: false, TestContext.Current.CancellationToken);

        Owned(directory, "another-machine", 4242, "a-stamp");
        var elsewhere = await service.StopAsync(temp.Path, Run, now: true, json: false, TestContext.Current.CancellationToken);

        Assert.Equal((HarnessExit.Refused, $"run {Run}'s dispatcher is this very process; --now ends another process. Nothing was changed."), (itself.ExitCode, itself.Message));
        Assert.Equal(HarnessExit.Refused, elsewhere.ExitCode);
        Assert.StartsWith($"run {Run}'s dispatcher runs on another machine (pid 4242 on another machine, run {Run}, since ", elsewhere.Message, StringComparison.Ordinal);
        Assert.EndsWith($"; --now ends only a process of this machine. stop-run {Run} without --now asks it to stop, or end it on its own machine. Nothing was changed.", elsewhere.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(LogOwnership.OwnerFile(directory)));
    }

    /// <summary>
    /// --now never ends a process it cannot tell is the dispatcher - one whose owner file holds no start to tell it by -
    /// however alive it is: refused, the process and the claim left as they were.
    /// </summary>
    [Fact]
    public async Task Now_NeverEndsAProcessItCannotTellIsTheDispatcher()
    {
        using var temp = new TempDirectory();
        var (service, _, layout) = Service(temp);
        var directory = layout.RunDirectory(Run);
        using var sleeper = Sleeper();

        try
        {
            Directory.CreateDirectory(directory);
            Owned(directory, Environment.MachineName, sleeper.Id, stamp: null);

            var outcome = await service.StopAsync(temp.Path, Run, now: true, json: false, TestContext.Current.CancellationToken);

            Assert.Equal(HarnessExit.Refused, outcome.ExitCode);
            Assert.EndsWith(
                $") cannot be told to be run {Run}'s dispatcher - its start cannot be read beside the one its owner file recorded - so it was not ended. Nothing was changed.",
                outcome.Message,
                StringComparison.Ordinal);
            Assert.False(sleeper.HasExited, "a process nobody could tell was the dispatcher was ended");
            Assert.True(File.Exists(LogOwnership.OwnerFile(directory)));
        }
        finally
        {
            Ended(sleeper);
        }
    }

    /// <summary>
    /// --now ends the dispatcher its owner file names by its start where it stands, with all it started, releases its
    /// claim and takes back any stop asked of it.
    /// </summary>
    [Fact]
    public async Task Now_EndsTheDispatcherWhereItStands_AndReleasesItsClaim()
    {
        using var temp = new TempDirectory();
        var (service, stops, layout) = Service(temp);
        var directory = layout.RunDirectory(Run);
        var identity = new ProcessIdentity(new Core.Platform.HostPlatform());
        using var dispatcher = Sleeper();

        try
        {
            Directory.CreateDirectory(directory);
            Owned(directory, Environment.MachineName, dispatcher.Id, identity.StampOf(dispatcher.Id)!);
            stops.Ask(directory);

            var outcome = await service.StopAsync(temp.Path, Run, now: true, json: true, TestContext.Current.CancellationToken);

            using var document = JsonDocument.Parse(Assert.Single(outcome.Data));

            Assert.Equal(HarnessExit.Success, outcome.ExitCode);
            Assert.StartsWith($"ended run {Run}'s dispatcher (pid {dispatcher.Id}, run {Run}, since ", outcome.Message, StringComparison.Ordinal);
            Assert.True(document.RootElement.GetProperty("stopped").GetBoolean());
            Assert.True(dispatcher.WaitForExit(TimeSpan.FromSeconds(30)), "the dispatcher was not ended");
            Assert.False(File.Exists(LogOwnership.OwnerFile(directory)));
            Assert.False(File.Exists(RunStops.FileOf(directory)), "the stop asked of the ended dispatcher stayed");
        }
        finally
        {
            Ended(dispatcher);
        }
    }

    /// <summary>
    /// A dispatcher found gone by --now itself - it ended between the command's look at its owner and the end - is the
    /// abandoned run it is: nothing to stop (13), its claim released.
    /// </summary>
    [Fact]
    public async Task Now_ADispatcherThatHadAlreadyGone_IsNothingToStop()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var identity = new GoneOnceLookedAt(harness.Identity);
        var stops = new RunStops(harness.FileSystem, identity, harness.Output, new LogOwnership(harness.FileSystem, harness.Output, identity));
        var service = new RunStopService(
            HostDoubles.Loader(new HarnessConfig(), temp.Path),
            new LogOwnership(harness.FileSystem, harness.Output, identity),
            stops,
            identity,
            harness.FileSystem);
        var layout = new Core.Repository.HarnessLayout(temp.Path, temp.Path);
        var directory = layout.RunDirectory(Run);

        Directory.CreateDirectory(directory);
        Owned(directory, Environment.MachineName, int.MaxValue - 1, "a-stamp");

        var outcome = await service.StopAsync(temp.Path, Run, now: true, json: false, TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.Refused, outcome.ExitCode);
        Assert.EndsWith(") has ended without giving up its records, so there is nothing to stop; its claim was released", outcome.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(LogOwnership.OwnerFile(directory)));
    }

    /// <summary>
    /// Ending a recorded process where it stands decides alone what every stop's --now may end: another machine's, this
    /// very one, a process that cannot be told by its start and one that has gone are each left as they are; the one the
    /// record names is ended and waited for; and a caller that has given up ends nothing, its interrupt thrown as one.
    /// </summary>
    [Fact]
    public async Task EndingWhereItStands_EndsOnlyTheRecordedProcessOfThisMachine_AndAnInterruptIsTheCallers()
    {
        var identity = new ProcessIdentity(new Core.Platform.HostPlatform());
        var wait = TimeSpan.FromSeconds(30);
        var token = TestContext.Current.CancellationToken;
        using var told = Sleeper();
        using var untold = Sleeper();
        using var interrupted = Sleeper();

        try
        {
            Assert.Equal(ProcessEnded.Elsewhere, (await identity.EndWhereItStandsAsync("another-machine", told.Id, "a-stamp", wait, token)).How);
            Assert.Equal(ProcessEnded.Itself, (await identity.EndWhereItStandsAsync(Environment.MachineName, Environment.ProcessId, identity.Current, wait, token)).How);
            Assert.Equal(ProcessEnded.CannotTell, (await identity.EndWhereItStandsAsync(Environment.MachineName, untold.Id, null, wait, token)).How);
            Assert.Equal(ProcessEnded.AlreadyGone, (await identity.EndWhereItStandsAsync(Environment.MachineName, int.MaxValue - 1, "a-stamp", wait, token)).How);
            Assert.False(untold.HasExited);

            Assert.Equal(ProcessEnded.Ended, (await identity.EndWhereItStandsAsync(Environment.MachineName, told.Id, identity.StampOf(told.Id), wait, token)).How);
            Assert.True(told.HasExited);

            using var interrupt = new CancellationTokenSource();
            await interrupt.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => identity.EndWhereItStandsAsync(Environment.MachineName, interrupted.Id, identity.StampOf(interrupted.Id), wait, interrupt.Token));
            Assert.False(interrupted.HasExited, "a process was ended for a caller that had given up");
        }
        finally
        {
            Ended(told);
            Ended(untold);
            Ended(interrupted);
        }
    }

    /// <summary>What every stop's --now answers where ending the process left it nothing more to do, worded once for each.</summary>
    [Fact]
    public void WhatAStopsNowAnswers_WhereThereIsNothingMoreToDo_IsWordedOnce()
    {
        CommandOutcome? Of(ProcessEnded how, string? why = null)
            => new ProcessEnding(how, why).NothingMoreToDo("the holder", "pid 7", "owner file", "stop-it x", TimeSpan.FromSeconds(60));

        Assert.Equal(
            (HarnessExit.Refused, "the holder runs on another machine (pid 7); --now ends only a process of this machine. stop-it x without --now asks it to stop, or end it on its own machine. Nothing was changed."),
            (Of(ProcessEnded.Elsewhere)!.ExitCode, Of(ProcessEnded.Elsewhere)!.Message));
        Assert.Equal((HarnessExit.Refused, "the holder is this very process; --now ends another process. Nothing was changed."), (Of(ProcessEnded.Itself)!.ExitCode, Of(ProcessEnded.Itself)!.Message));
        Assert.Equal(
            (HarnessExit.Refused, "pid 7 cannot be told to be the holder - its start cannot be read beside the one its owner file recorded - so it was not ended. Nothing was changed."),
            (Of(ProcessEnded.CannotTell)!.ExitCode, Of(ProcessEnded.CannotTell)!.Message));
        Assert.Equal(
            (HarnessExit.CommandFailed, "pid 7, the holder, was ended and had not gone within 60 s; its owner file is kept"),
            (Of(ProcessEnded.NotGone)!.ExitCode, Of(ProcessEnded.NotGone)!.Message));
        Assert.Equal(
            (HarnessExit.CommandFailed, "pid 7, the holder, could not be ended: access is denied; its owner file is kept"),
            (Of(ProcessEnded.CouldNotEnd, "access is denied")!.ExitCode, Of(ProcessEnded.CouldNotEnd, "access is denied")!.Message));
        Assert.Null(Of(ProcessEnded.Ended));
        Assert.Null(Of(ProcessEnded.AlreadyGone));
    }

    /// <summary>
    /// The dispatcher's watch takes the request at its look - recorded taken among the run's records, the request removed,
    /// said who asked - and stops the run by its token. A stop asked again once it took one is taken back by its end, and
    /// every process that asked reads the run's record as taken, however many read it.
    /// </summary>
    [Fact]
    public async Task TheWatch_TakesTheRequest_AndRecordsItTakenForEveryProcessThatAsked()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var clock = new TimerClock();
        var stops = new RunStops(harness.FileSystem, harness.Identity, harness.Output, new LogOwnership(harness.FileSystem, harness.Output, harness.Identity), clock);
        var directory = temp.Combine("runs", Run);
        Directory.CreateDirectory(directory);

        var watch = stops.Watch(directory, "test", TestContext.Current.CancellationToken);

        Assert.False(watch.Stopping.IsCancellationRequested);

        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using (watch.Stopping.Register(() => stopped.TrySetResult()))
        {
            stops.Ask(directory);
            await clock.WaitingAsync().WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            clock.Advance(StopCadence.PollEvery);

            await stopped.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        }

        Assert.Equal(Environment.ProcessId, watch.Taken!.ProcessId);
        Assert.NotNull(watch.Taken.TakenUtc);
        Assert.Contains($"test: WARN - pid {Environment.ProcessId} asked this run to stop (stop-run)", harness.StandardError.ToString(), StringComparison.Ordinal);
        Assert.False(File.Exists(RunStops.FileOf(directory)), "the request a run took stayed asked");

        // Asked again while the run stops: nothing takes it, and the run's end takes it back.
        stops.Ask(directory);
        await watch.EndThenAsync(() => Task.CompletedTask);

        Assert.False(File.Exists(RunStops.FileOf(directory)), "a request the run never took stayed behind it");
        Assert.True(stops.TakenBack(directory, "test"), "the first process that asked did not read the stop as taken");
        Assert.True(stops.TakenBack(directory, "test"), "a second process that asked did not read the stop as taken");
        Assert.True(File.Exists(RunStops.TakenFileOf(directory)), "reading the run's record of the stop removed it");
    }

    /// <summary>
    /// A stop whose taking cannot be recorded among the run's records is said, and the run stops all the same: its token
    /// is cancelled, and the request is still removed, for the run has taken it.
    /// </summary>
    [Fact]
    public void AStopWhoseTakingCannotBeRecorded_IsSaid_AndTheRunStopsAllTheSame()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var stops = new RunStops(harness.FileSystem, harness.Identity, harness.Output, new LogOwnership(harness.FileSystem, harness.Output, harness.Identity), new TimerClock());
        var directory = temp.Combine("runs", Run);

        // Something that is no file stands where the record goes.
        Directory.CreateDirectory(RunStops.TakenFileOf(directory));
        File.WriteAllText(Path.Combine(RunStops.TakenFileOf(directory), "in-the-way"), "x");
        stops.Ask(directory);

        var taken = stops.Take(directory, "test");

        Assert.NotNull(taken);
        Assert.False(File.Exists(RunStops.FileOf(directory)));
        Assert.Contains($"'{RunStops.TakenFileOf(directory)}'", harness.StandardError.ToString(), StringComparison.Ordinal);
        Assert.Contains("The run stops all the same, and a process that asked hears that nothing was stopped.", harness.StandardError.ToString(), StringComparison.Ordinal);
        Assert.False(stops.TakenBack(directory, "test"));
    }

    /// <summary>
    /// Ended, the watch takes back a request it never took before the run's directory is given up - so none stays behind
    /// the run, and none asked as the directory is given up is ever taken by the run that ended.
    /// </summary>
    [Fact]
    public async Task TheWatchsEnd_TakesBackWhatNothingWillRead_BeforeTheDirectoryIsGivenUp()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var stops = new RunStops(harness.FileSystem, harness.Identity, harness.Output, new LogOwnership(harness.FileSystem, harness.Output, harness.Identity), new TimerClock());
        var directory = temp.Combine("runs", Run);
        Directory.CreateDirectory(Path.GetDirectoryName(directory)!);

        var untaken = stops.Watch(directory, "test", TestContext.Current.CancellationToken);
        stops.Ask(directory);
        bool? leftAtGivingUp = null;

        await untaken.EndThenAsync(() =>
        {
            leftAtGivingUp = File.Exists(RunStops.FileOf(directory));

            // Asked as the directory is given up: the run that ended never takes it.
            stops.Ask(directory);
            return Task.CompletedTask;
        });

        Assert.False(leftAtGivingUp, "a request the run never took was left at its directory's giving up");
        Assert.False(untaken.Stopping.IsCancellationRequested);
        Assert.False(stops.TakenBack(directory, "test"), "a stop asked as the directory was given up was taken by the run that ended");
    }

    /// <summary>A request that does not read as one is said, removed, and stops nothing: no run is stopped on a record nobody can say who wrote.</summary>
    [Theory]
    [InlineData("{ \"schema\": 1 }", "")]
    [InlineData("{ \"schema\": 2, \"askedUtc\": \"2026-10-22T03:59:35+00:00\", \"machine\": \"m\", \"processId\": 7, \"processStamp\": null, \"takenUtc\": null }", "its schema is 2, and this build reads 1")]
    [InlineData("{ \"schema\": 1, \"askedUtc\": \"2026-10-22T03:59:35+00:00\", \"machine\": \" \", \"processId\": 7, \"processStamp\": null, \"takenUtc\": null }", "it names no process as the one that asked")]
    [InlineData("{ \"schema\": 1, \"askedUtc\": \"2026-10-22T03:59:35+00:00\", \"machine\": \"m\", \"processId\": 0, \"processStamp\": null, \"takenUtc\": null }", "it names no process as the one that asked")]
    public void ARequestThatDoesNotReadAsOne_IsSaidAndRemoved_AndStopsNothing(string text, string problem)
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var stops = new RunStops(harness.FileSystem, harness.Identity, harness.Output, new LogOwnership(harness.FileSystem, harness.Output, harness.Identity));
        var directory = temp.Combine("runs", Run);

        Directory.CreateDirectory(Path.GetDirectoryName(directory)!);
        File.WriteAllText(RunStops.FileOf(directory), text);

        Assert.Null(stops.Take(directory, "test"));
        Assert.False(File.Exists(RunStops.FileOf(directory)));
        Assert.Contains($"test: WARN - '{RunStops.FileOf(directory)}' is the stop asked of a run, and does not read as one: {problem}", harness.StandardError.ToString(), StringComparison.Ordinal);
        Assert.Contains("It was removed, and stops nothing.", harness.StandardError.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// A dispatcher that ends without giving its records up while the stop is waited for - killed - has its claim released
    /// there, as the next run in its tree would release it: stopped (0) where it took the stop first, saying it ended
    /// without giving its records up; nothing to stop (13) where it never took it, its untaken request taken back.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ADispatcherKilledWhileTheStopIsWaitedFor_HasItsClaimReleased(bool tookTheStop)
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var identity = new StandsUntilTold(harness.Identity, Recorded);
        var (service, stops, layout) = Service(temp, identity: identity);
        var directory = layout.RunDirectory(Run);

        Directory.CreateDirectory(directory);
        Owned(directory, Environment.MachineName, Recorded, "a-stamp");

        var stopping = service.StopAsync(temp.Path, Run, now: false, json: true, TestContext.Current.CancellationToken);

        while (!File.Exists(RunStops.FileOf(directory)))
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        if (tookTheStop)
        {
            Assert.NotNull(stops.Take(directory, "test"));
        }

        identity.Gone = true;
        var outcome = await stopping.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        using var document = JsonDocument.Parse(Assert.Single(outcome.Data));

        if (tookTheStop)
        {
            Assert.Equal(HarnessExit.Success, outcome.ExitCode);
            Assert.EndsWith(") took the stop and ended without giving up its records; its claim was released", outcome.Message, StringComparison.Ordinal);
            Assert.True(document.RootElement.GetProperty("stopped").GetBoolean());
            Assert.Equal(RunStops.TakenFileOf(directory), document.RootElement.GetProperty("records").GetProperty("stop").GetString());
        }
        else
        {
            Assert.Equal(HarnessExit.Refused, outcome.ExitCode);
            Assert.EndsWith(") has ended without giving up its records, so there is nothing to stop; its claim was released", outcome.Message, StringComparison.Ordinal);
            Assert.Equal([$"stop: {RunStops.FileOf(directory)}"], outcome.Details);
            Assert.Equal(RunStops.FileOf(directory), document.RootElement.GetProperty("records").GetProperty("stop").GetString());
            Assert.False(document.RootElement.GetProperty("stopped").GetBoolean());
        }

        Assert.False(File.Exists(LogOwnership.OwnerFile(directory)), "the killed dispatcher's claim was left");
        Assert.False(File.Exists(RunStops.FileOf(directory)), "a request was left behind the killed dispatcher's run");
    }

    /// <summary>Who asked a stop is named as every holder is: by its process id, and one of another machine said to be, never naming it.</summary>
    [Fact]
    public void WhoAskedAStop_IsNamedAsEveryHolderIs_NeverNamingAnotherMachine()
    {
        var harness = new HarnessFactory();
        var stops = new RunStops(harness.FileSystem, harness.Identity, harness.Output, new LogOwnership(harness.FileSystem, harness.Output, harness.Identity));

        Assert.Equal("pid 7", stops.Asker(new RunStopRequest(RunStopRequest.Version, DateTimeOffset.UnixEpoch, Environment.MachineName, 7)));
        Assert.Equal("pid 7 on another machine", stops.Asker(new RunStopRequest(RunStopRequest.Version, DateTimeOffset.UnixEpoch, "another-machine", 7)));
    }

    /// <summary>
    /// A watch ended twice ends once, and gives the run's directory up each time; a request it cannot take back is said and
    /// never fails the run's end; and it gives the directory up however its end went, a fault nothing expected among them,
    /// which is the caller's to hear.
    /// </summary>
    [Fact]
    public async Task AWatchsEnd_GivesTheDirectoryUpHoweverItWent_AndEndsOnce()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var files = new FailingDeletes(harness.FileSystem);
        var stops = new RunStops(files, harness.Identity, harness.Output, new LogOwnership(files, harness.Output, harness.Identity), new TimerClock());
        var directory = temp.Combine("runs", Run);
        Directory.CreateDirectory(directory);
        var givenUp = 0;

        Task GiveUp()
        {
            givenUp++;
            return Task.CompletedTask;
        }

        var twice = stops.Watch(directory, "test", TestContext.Current.CancellationToken);
        await twice.EndThenAsync(GiveUp);
        await twice.EndThenAsync(GiveUp);

        Assert.Equal(2, givenUp);

        var locked = stops.Watch(directory, "test", TestContext.Current.CancellationToken);
        files.Failing = (RunStops.FileOf(directory), () => new IOException("it is in use"));
        await locked.EndThenAsync(GiveUp);

        Assert.Equal(3, givenUp);
        Assert.Contains($"test: WARN - '{RunStops.FileOf(directory)}', the stop asked of a run, could not be removed: it is in use.", harness.StandardError.ToString(), StringComparison.Ordinal);

        var faulted = stops.Watch(directory, "test", TestContext.Current.CancellationToken);
        files.Failing = (RunStops.FileOf(directory), () => new InvalidOperationException("nothing expected this"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => faulted.EndThenAsync(GiveUp));
        Assert.Equal(4, givenUp);
    }

    /// <summary>
    /// A claim that cannot be released - its owner file in use - is never said released: the command says it could not be,
    /// beside the line that says why.
    /// </summary>
    [Fact]
    public async Task AClaimThatCannotBeReleased_IsNeverSaidReleased()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var files = new FailingDeletes(harness.FileSystem);
        var claims = new LogOwnership(files, harness.Output, harness.Identity);
        var stops = new RunStops(files, harness.Identity, harness.Output, claims);
        var layout = new Core.Repository.HarnessLayout(temp.Path, temp.Path);
        var service = new RunStopService(HostDoubles.Loader(new HarnessConfig(), temp.Path), claims, stops, harness.Identity, files);
        var directory = layout.RunDirectory(Run);

        Directory.CreateDirectory(directory);
        Owned(directory, Environment.MachineName, int.MaxValue - 1, "a-process-that-has-gone");
        files.Failing = (LogOwnership.OwnerFile(directory), () => new IOException("it is in use"));

        var outcome = await service.StopAsync(temp.Path, Run, now: false, json: false, TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.Refused, outcome.ExitCode);
        Assert.EndsWith(") has ended without giving up its records, so there is nothing to stop; its claim could not be released, as said above", outcome.Message, StringComparison.Ordinal);
        Assert.Contains($"Its owner file '{LogOwnership.OwnerFile(directory)}' could not be removed: it is in use", harness.StandardError.ToString(), StringComparison.Ordinal);
        Assert.True(File.Exists(LogOwnership.OwnerFile(directory)));
    }

    /// <summary>
    /// --now waits for the process it ended to go no longer than its bound - not gone, its record kept - says a process it
    /// could not end, takes one gone meanwhile as ended, and leaves an interrupt that comes while it waits the caller's own.
    /// </summary>
    [Fact]
    public async Task Now_WaitsForTheProcessNoLongerThanItsBound_AndLeavesAnInterruptTheCallers()
    {
        var harness = new HarnessFactory();
        var identity = new StandsUntilTold(harness.Identity, Recorded);
        var token = TestContext.Current.CancellationToken;

        Task<ProcessEnding> EndAsync(Func<int, CancellationToken, Task> killAndWait, TimeSpan wait, CancellationToken cancellation)
            => identity.EndWhereItStandsWithAsync(Environment.MachineName, Recorded, "a-stamp", wait, killAndWait, cancellation);

        var notGone = await EndAsync((_, bound) => Task.Delay(Timeout.InfiniteTimeSpan, bound), TimeSpan.FromMilliseconds(50), token);
        var refused = await EndAsync((_, _) => throw new System.ComponentModel.Win32Exception(5, "access is denied."), TimeSpan.FromSeconds(60), token);
        var goneMeanwhile = await EndAsync((_, _) => throw new ArgumentException("no such process"), TimeSpan.FromSeconds(60), token);

        Assert.Equal(ProcessEnded.NotGone, notGone.How);
        Assert.Equal((ProcessEnded.CouldNotEnd, "access is denied"), (refused.How, refused.Why));
        Assert.Equal(ProcessEnded.Ended, goneMeanwhile.How);

        using var interrupt = CancellationTokenSource.CreateLinkedTokenSource(token);
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ending = EndAsync(
            (_, bound) =>
            {
                waiting.TrySetResult();
                return Task.Delay(Timeout.InfiniteTimeSpan, bound);
            },
            TimeSpan.FromMinutes(5),
            interrupt.Token);

        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(30), token);
        await interrupt.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ending);
    }

    /// <summary>The process id a test records as a dispatcher of this machine that no process has.</summary>
    private const int Recorded = int.MaxValue - 2;

    /// <summary>The service over a tree at <paramref name="temp"/>, with the requests it asks and the tree's layout.</summary>
    private static (RunStopService Service, RunStops Stops, Core.Repository.HarnessLayout Layout) Service(TempDirectory temp, TimeProvider? clock = null, IProcessIdentity? identity = null)
    {
        var harness = new HarnessFactory();
        var claims = new LogOwnership(harness.FileSystem, harness.Output, harness.Identity);
        var stops = new RunStops(harness.FileSystem, harness.Identity, harness.Output, claims);

        return (
            new RunStopService(
                HostDoubles.Loader(new HarnessConfig(), temp.Path),
                claims,
                stops,
                identity ?? harness.Identity,
                harness.FileSystem,
                clock),
            stops,
            new Core.Repository.HarnessLayout(temp.Path, temp.Path));
    }

    /// <summary>Writes the owner file of the run at <paramref name="directory"/> naming a process, as a dispatcher writes it.</summary>
    private static void Owned(string directory, string machine, int processId, string? stamp)
        => File.WriteAllText(
            LogOwnership.OwnerFile(directory),
            JsonSerializer.Serialize(
                new LogOwner(machine, processId, Run, new DateTimeOffset(2026, 10, 22, 3, 59, 35, TimeSpan.Zero), stamp),
                Core.FileSystem.JsonStateFile.Options));

    /// <summary>A process of this test's own that sleeps five minutes: a dispatcher to end, or one to leave alone.</summary>
    private static Process Sleeper() => Process.Start(TestHost.StartOf(TestHost.ChildRequest("sleep", "300000")))!;

    /// <summary>Ends a sleeper of this test's own, wherever its test left it.</summary>
    private static void Ended(Process sleeper)
    {
        if (!sleeper.HasExited)
        {
            sleeper.Kill(entireProcessTree: true);
            sleeper.WaitForExit(TimeSpan.FromSeconds(30));
        }
    }

    /// <summary>
    /// This process's identity, but for one recorded process, told to be the one its record names, which stands until the
    /// test says it has gone: a dispatcher killed while it is waited for.
    /// </summary>
    private sealed class StandsUntilTold(IProcessIdentity inner, int recorded) : IProcessIdentity
    {
        public volatile bool Gone;

        public int CurrentId => inner.CurrentId;

        public string CurrentMachine => inner.CurrentMachine;

        public string? Current => inner.Current;

        public bool IsAlive(int processId, string? stamp) => processId == recorded ? !Gone : inner.IsAlive(processId, stamp);

        public string? StampOf(int processId) => processId == recorded ? "a-stamp" : inner.StampOf(processId);

        public bool IsSame(int processId, string stamp) => processId == recorded ? !Gone : inner.IsSame(processId, stamp);
    }

    /// <summary>The file system, but that removing the path <see cref="Failing"/> names, once it names one, fails with what it makes.</summary>
    private sealed class FailingDeletes(Core.FileSystem.IFileSystem inner) : PassThroughFileSystem(inner)
    {
        public (string Path, Func<Exception> Fault)? Failing;

        public override void DeleteFile(string path)
        {
            if (Failing is { } failing && path == failing.Path)
            {
                throw failing.Fault();
            }

            base.DeleteFile(path);
        }
    }

    /// <summary>
    /// This process's identity, but for a recorded process that stands once - when the command first looks at the owner -
    /// and has gone at every look after: a dispatcher that ends between the command's look and its end.
    /// </summary>
    private sealed class GoneOnceLookedAt(IProcessIdentity inner) : IProcessIdentity
    {
        private int _looks;

        public int CurrentId => inner.CurrentId;

        public string CurrentMachine => inner.CurrentMachine;

        public string? Current => inner.Current;

        public bool IsAlive(int processId, string? stamp) => processId == inner.CurrentId ? inner.IsAlive(processId, stamp) : Interlocked.Increment(ref _looks) == 1;

        public string? StampOf(int processId) => inner.StampOf(processId);

        public bool IsSame(int processId, string stamp) => processId == inner.CurrentId && inner.IsSame(processId, stamp);
    }
}
