using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using RepoHarness.Core.Build;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.Execution;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Hosts;
using RepoHarness.Core.Legs;
using RepoHarness.Core.Output;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Results;
using RepoHarness.Core.Runs;
using RepoHarness.Core.Sync;
using RepoHarness.Core.Testing;
using RepoHarness.Core.Worktrees;

namespace RepoHarness.Core.Mutations;

/// <summary>
/// What one leg's sweep sweeps: the tree it reads, the project its workers build, its arms and its settings - the leg's
/// own, or for a self-test the fixture this tool carries, built the leg's way.
/// </summary>
internal sealed record MutationSubject
{
    /// <summary>The tree read once for every worker, beside which the workers are kept.</summary>
    public required string TreeRoot { get; init; }

    /// <summary>The project each worker builds, as the leg's variant builds it.</summary>
    public required ProjectConfig Project { get; init; }

    /// <summary>
    /// The test settings each binary's run starts by - in the directory the leg's tests start in, with their environment
    /// over its host's - or <see langword="null"/> where it starts in the worker with what the leg's host gives it.
    /// </summary>
    public TestConfig? Tests { get; init; }

    /// <summary>
    /// The longest path a build of the project makes below its build directory, which each worker's paths are reckoned
    /// with; <see langword="null"/> for <c>worktrees.pathBudgetReserve</c>, the leg's own project's.
    /// </summary>
    public int? PathReserve { get; init; }

    /// <summary>
    /// What a self-test makes of each arm's verdict as the judge reached it, given the arm's id: the arm held to the
    /// verdict it is designed to reach. <see langword="null"/> where each arm's verdict is the judge's own.
    /// </summary>
    public Func<string, ReachedVerdict, ReachedVerdict>? Hold { get; init; }

    /// <summary>The arms the leg drives, and those it does not, with why.</summary>
    public required LegArms Arms { get; init; }

    /// <summary>How the sweep runs: its workers, its report arguments, its bound.</summary>
    public required MutationSettings Settings { get; init; }

    /// <summary>What a worker's build is expected to come to, or <see langword="null"/> where nothing says.</summary>
    public long? ExpectedBuildBytes { get; init; }

    /// <summary>What said so, as a line says it.</summary>
    public string ExpectedBuildSource { get; init; } = string.Empty;

    /// <summary>Whether to take a worker a live sweep holds, as <c>--force-lock</c> takes a lock.</summary>
    public bool Force { get; init; }
}

/// <summary>
/// Sweeps one leg's arms: reads its tree once, makes the workers that fit beside it, controls each test binary unmutated,
/// and drives every arm through its mutation, its build, the rebuild's witness and its run or its paired control, putting
/// each site back as it was - and returns the leg's line, its arms beneath it.
/// </summary>
/// <remarks>
/// <para>
/// The work a <c>check-mutations</c> leg does once its lock is taken. Every worker is synced from the one reading of the
/// tree, so every arm measures the same tree however long the sweep takes; a worker kept from an earlier sweep is synced
/// again by content, which puts back a site a sweep killed part way left mutated, and its build directory stays warm.
/// Each worker is claimed while the sweep uses it, made under a unit of admission claiming the room it still needs, and
/// built whole as the leg builds before it drives an arm.
/// </para>
/// <para>
/// Each test binary's arms wait for its pristine control, built and run once on the leg in whichever worker first needs
/// it: one that does not pass stops them, and decides the leg's own verdict. Workers drain one queue of arms, each arm
/// admitted as a unit of its own; its records are written as it ends, in <c>&lt;run&gt;/&lt;leg&gt;/arms/&lt;arm&gt;</c>.
/// A site that cannot be put back as it was makes the arm <c>poisoned</c> and retires its worker; the others go on, and
/// what no worker drove is <c>stopped</c>, saying why.
/// </para>
/// </remarks>
internal sealed class MutationLegRunner(
    IMutationSource source,
    IWorkerCopies copies,
    IWorkerSite site,
    IArmBuilder builder,
    IArmTestRunner tests,
    IPathBudget pathBudget,
    IFileSystem fileSystem,
    IHarnessOutput output,
    string commandName)
{
    private readonly IMutationSource _source = source;
    private readonly IWorkerCopies _copies = copies;
    private readonly IWorkerSite _site = site;
    private readonly IArmBuilder _builder = builder;
    private readonly IArmTestRunner _tests = tests;
    private readonly IPathBudget _pathBudget = pathBudget;
    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly IHarnessOutput _output = output;
    private readonly string _commandName = commandName;

    /// <summary>Sweeps <paramref name="subject"/> on <paramref name="work"/>'s leg, and returns the leg's line.</summary>
    /// <param name="subject">What the leg sweeps.</param>
    /// <param name="work">The leg, its run and its context, and how it asks its machine to take each unit.</param>
    /// <param name="cancellationToken">Stops the sweep: each site is put back, and nothing is reported of the leg.</param>
    public async Task<LegEntry> RunAsync(MutationSubject subject, LegWork work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(work);

        var started = Stopwatch.GetTimestamp();
        var entry = await new Sweep(this, subject, work).RunAsync(cancellationToken).ConfigureAwait(false);

        return entry with { Duration = Stopwatch.GetElapsedTime(started) };
    }

    /// <summary>One site of an arm in a worker's copy: where it is, what it held, and what the mutation makes it.</summary>
    /// <param name="Declared">The site as the registry declares it.</param>
    /// <param name="Path">Where it is in the worker's copy.</param>
    /// <param name="Pristine">What it holds unmutated, or <see langword="null"/> where it is no file.</param>
    /// <param name="Read">
    /// The file as the sweep's reading of the tree holds it, under the very spelling the registry names it by, which its
    /// putting back is checked against; <see langword="null"/> where the reading holds none so spelt.
    /// </param>
    private sealed record SiteState(MutationSite Declared, string Path, byte[]? Pristine, SyncEntry? Read)
    {
        /// <summary>What the mutation makes it, where its before-text occurs exactly once.</summary>
        public byte[]? Mutated { get; set; }

        /// <summary>Whether anything was written to it, so it must be put back.</summary>
        public bool Touched { get; set; }
    }

    /// <summary>One leg's sweep, while it runs.</summary>
    private sealed class Sweep(MutationLegRunner runner, MutationSubject subject, LegWork work)
    {
        private readonly MutationLegRunner _runner = runner;
        private readonly MutationSubject _subject = subject;
        private readonly LegWork _work = work;
        private readonly PlacedLeg _leg = work.Leg;
        private readonly HarnessConfig _config = work.Context.Config;
        private readonly ConcurrentDictionary<string, ArmEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, Lazy<Task<PristineOutcome>>> _controls = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<int, ReachedVerdict> _unmade = new();
        private readonly ConcurrentQueue<ReachedVerdict> _own = new();
        private SyncSource _reading = null!;
        private ArmQueue _queue = null!;
        private Exception? _escaped;
        private int _asked;

        public async Task<LegEntry> RunAsync(CancellationToken cancellationToken)
        {
            var unselected = _subject.Arms.Unselected
                .Select(arm => (arm.Arm, Entry: Undriven(arm.Arm, ReachedVerdict.Of(LegVerdict.SkippedNotSelected, arm.Reason))))
                .ToList();
            var driven = _subject.Arms.Driven;

            if (driven.Count == 0)
            {
                return Line([ReachedVerdict.Of(LegVerdict.SkippedNotSelected, "the sweep drives no arm on this leg")], unselected, []);
            }

            _work.Progress($"reading '{_subject.TreeRoot}', once for every worker");
            _reading = await _runner._source.ReadAsync(_subject.TreeRoot, cancellationToken).ConfigureAwait(false);

            var (plan, needs) = await PlanAsync(driven.Count, cancellationToken).ConfigureAwait(false);

            if (plan.Count == 0)
            {
                // No room for even one worker, which turns the leg away as a leg whose build does not fit is turned away.
                var turned = ReachedVerdict.Of(LegVerdict.SkippedUnavailable, plan.Fewer!);

                return Line([turned], [.. driven.Select(arm => (arm, Undriven(arm, turned))), .. unselected], []);
            }

            var notes = new List<string>();

            if (plan.Fewer is { } fewer)
            {
                notes.Add(fewer);
            }

            if (plan.Unchecked is { } unchecked_)
            {
                _runner._output.Warn(_runner._commandName, $"{_leg.Name}: {unchecked_}");
                notes.Add(unchecked_);
            }

            _queue = new ArmQueue(driven, plan.Count);

            using (var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                await Task.WhenAll(Enumerable.Range(1, plan.Count).Select(number => Task.Run(() => WorkerAsync(number, needs[number - 1], stop), CancellationToken.None)))
                    .ConfigureAwait(false);

                if (_escaped is { } escaped)
                {
                    ExceptionDispatchInfo.Throw(escaped);
                }

                cancellationToken.ThrowIfCancellationRequested();

                foreach (var undriven in _queue.Undriven(stop.Token))
                {
                    _entries[undriven.Arm.Id] = Undriven(undriven.Arm, undriven.Verdict);
                }
            }

            // A worker that could not be made is the leg's own failure only where no worker was made: one made beside it drove
            // the arms, and the leg says the sweep ran with fewer.
            var own = _own.ToList();

            if (_unmade.Count == plan.Count)
            {
                own.AddRange(_unmade.OrderBy(pair => pair.Key).Select(pair => pair.Value));
            }
            else
            {
                notes.AddRange(_unmade.OrderBy(pair => pair.Key).Select(pair => $"{pair.Value.Detail}, so it drove no arm"));
            }

            return Line(own, [.. driven.Select(arm => (arm, _entries[arm.Id])), .. unselected], notes);
        }

        /// <summary>
        /// How many workers the sweep runs, and what each still needs of the room: removing first each worker an earlier
        /// sweep left beyond <c>mutations.workers</c>, so lowering it frees the room they held; and running none whose build
        /// would pass this machine's path limit.
        /// </summary>
        private async Task<(WorkerPlan Plan, IReadOnlyList<long> Needs)> PlanAsync(int arms, CancellationToken cancellationToken)
        {
            var wsl = _leg.Named.Kind == HostKind.Wsl;
            var cap = WorkerRoom.Wanted(_subject.Settings.Workers, int.MaxValue, wsl);
            var wanted = WorkerRoom.Wanted(_subject.Settings.Workers, arms, wsl);

            foreach (var beyond in (await _runner._copies.ListAsync(_subject.TreeRoot, _leg.Variant, cancellationToken).ConfigureAwait(false))
                .Where(copy => copy.Number > cap))
            {
                await RemoveBeyondAsync(beyond, cap, cancellationToken).ConfigureAwait(false);
            }

            // Within this machine's path limit, as a worktree is kept: a worker whose build passes it fails as compile errors
            // in files nobody touched. A worker's path grows only with its number, so the workers that fit come first.
            var fit = Enumerable.Range(1, wanted).TakeWhile(number => Budget(number).IsWithinBudget).Count();

            if (fit == 0)
            {
                return (new WorkerPlan(0, wanted, TooLong(1), null), []);
            }

            var copy = _reading.Files.Entries.Values.Sum(entry => entry.Size);
            var needs = Enumerable.Range(1, fit)
                .Select(number =>
                {
                    var worker = Worker(number);
                    var build = _leg.Variant.DirectoryUnder(worker);

                    return WorkerRoom.Need(
                        copy,
                        _runner._fileSystem.DirectoryExists(worker),
                        _subject.ExpectedBuildBytes,
                        _runner._fileSystem.DirectoryExists(build),
                        BuildRecord.BytesIn(_runner._fileSystem, build));
                })
                .ToList();
            var (room, unmeasured) = DiskSpace.Measure(_runner._fileSystem, Worker(1));
            var plan = WorkerRoom.Plan(needs, room, unmeasured, _subject.ExpectedBuildBytes is null ? null : _subject.ExpectedBuildSource);

            if (fit < wanted)
            {
                var tooLong = TooLong(fit + 1);

                plan = plan with { Wanted = wanted, Fewer = plan.Fewer is { } fewer ? $"{fewer}; and {tooLong}" : $"{fit} of {wanted} workers: {tooLong}" };
            }

            return (plan, needs);
        }

        /// <summary>
        /// Whether worker <paramref name="number"/>'s build stays within this machine's path limit, reckoned as a worktree's
        /// is: the worker, its build directory below it, and below that the longest path a build of the project makes -
        /// <c>worktrees.pathBudgetReserve</c>, or the subject's own - with <c>worktrees.pathBudgetMargin</c> to spare.
        /// </summary>
        private PathBudgetResult Budget(int number)
        {
            var worker = Worker(number);
            var settings = _config.Worktrees;

            // The build directory below the worker, the separator before it counted: what a worktree's check counts for the
            // longest variant this machine builds.
            var below = _leg.Variant.DirectoryUnder(worker).Length - Path.TrimEndingDirectorySeparator(worker).Length;

            return _runner._pathBudget.Check(worker, below + (_subject.PathReserve ?? settings.PathBudgetReserve), settings.PathBudgetMargin, settings.PathLimit);
        }

        /// <summary>Why worker <paramref name="number"/> is not run: its build would pass this machine's path limit.</summary>
        private string TooLong(int number)
        {
            var budget = Budget(number);
            var reckoned = _subject.PathReserve is { } reserve
                ? $"as the {reserve} its project's build makes below its build directory and worktrees.pathBudgetMargin reckon them"
                : "as worktrees.pathBudgetReserve and pathBudgetMargin reckon them";

            return $"worker {number} would be kept at '{Worker(number)}', where its build needs paths of {budget.RequiredLength} "
                + $"characters, {reckoned}, and every path must stay under {budget.Limit}: keep the tree at a shorter path, or set "
                + "worktrees.pathLimit where every tool its build runs takes longer ones";
        }

        /// <summary>
        /// Removes a worker an earlier sweep left beyond the cap, where a sync made it and no live sweep holds it; a worker
        /// that cannot be removed is said and left, and the sweep goes on without it.
        /// </summary>
        private async Task RemoveBeyondAsync(WorkerCopy beyond, int cap, CancellationToken cancellationToken)
        {
            var named = $"{_leg.Name}: worker {beyond.Number}, '{beyond.Path}', is beyond the {cap} worker(s) a sweep runs";

            if (!beyond.Made)
            {
                _runner._output.Warn(_runner._commandName, $"{named}, and was left where it is: {WorktreeReports.Origin(beyond.Found)}");
                return;
            }

            _runner._copies.ReleaseAbandoned(beyond.Path);

            if (_runner._copies.HeldBy(beyond.Path) is { } holder)
            {
                _runner._output.Warn(_runner._commandName, $"{named}, and was left: {holder}");
                return;
            }

            CopyRemoval removal;

            try
            {
                removal = await _runner._copies.RemoveAsync(beyond.Path, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                || (ex is HarnessException harness && !HarnessExit.RefusesTheRun(harness.ExitCode)))
            {
                _runner._output.Warn(_runner._commandName, $"{named}, and could not be removed: {ex.Message}");
                return;
            }

            if (removal == CopyRemoval.Removed)
            {
                _runner._output.Info(
                    _runner._commandName,
                    $"{_leg.Name}: removed worker {beyond.Number}, '{beyond.Path}', {DiskSpace.Size(beyond.Bytes)}, beyond the {cap} worker(s) a sweep runs");
            }
            else if (removal != CopyRemoval.Absent)
            {
                _runner._output.Warn(
                    _runner._commandName,
                    $"{_leg.Name}: '{beyond.Path}' is named as worker {beyond.Number} but is no copy a sweep made, so it was left where it is");
            }
        }

        /// <summary>One worker: claimed, made, and draining the queue until no arm is left for it.</summary>
        private async Task WorkerAsync(int number, long need, CancellationTokenSource stop)
        {
            try
            {
                await DriveWorkerAsync(number, need, stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                // Stopped: by the run, which says so for the leg, or because another worker ended the sweep, which says why.
            }
            catch (Exception ex)
            {
                // Ends the sweep, whose other workers put their sites back and stop: raised once they have, as the leg's end.
                Interlocked.CompareExchange(ref _escaped, ex, null);
                await stop.CancelAsync().ConfigureAwait(false);
            }
        }

        private async Task DriveWorkerAsync(int number, long need, CancellationToken cancellationToken)
        {
            var worker = Worker(number);

            _runner._copies.ReleaseAbandoned(worker);

            var claim = _runner._copies.Claim(worker, _work.RunId, _subject.Force);

            if (!claim.Taken)
            {
                Unmade(number, LegVerdict.RefusedLocked, $"'{worker}' is held by another sweep: {claim.HeldBy}");
                return;
            }

            try
            {
                if (await MakeAsync(number, need, worker, cancellationToken).ConfigureAwait(false) is not { } graph)
                {
                    return;
                }

                while (await _queue.TakeAsync(number, cancellationToken).ConfigureAwait(false) is { } arm)
                {
                    var (entry, retired) = await DriveArmAsync(number, arm, graph, cancellationToken).ConfigureAwait(false);

                    _entries[arm.Id] = entry;

                    if (retired is not null)
                    {
                        _queue.Retire(number, retired, requeue: false);
                        return;
                    }

                    _queue.Done(number);
                }
            }
            finally
            {
                _runner._copies.Release(worker, _work.RunId);
            }
        }

        /// <summary>
        /// Makes worker <paramref name="number"/> under a unit of admission claiming the room it still needs: its copy synced
        /// from the sweep's reading, and built whole as the leg builds. The worker's build graph, or <see langword="null"/>
        /// where it could not be made, and it is retired saying why.
        /// </summary>
        private async Task<IWorkerGraph?> MakeAsync(int number, long need, string worker, CancellationToken cancellationToken)
        {
            Admission? admitted = null;

            try
            {
                var room = need > 0
                    ? new RoomNeed(
                        need,
                        _subject.ExpectedBuildBytes is null ? "its copy of the tree" : $"its copy of the tree, and its build {_subject.ExpectedBuildSource}",
                        worker,
                        string.Empty)
                    : null;

                admitted = await _work.AdmitUnit(new UnitAdmission($"worker-{number}", room, First()), cancellationToken).ConfigureAwait(false);

                if (admitted is { Refusal: { } refusal })
                {
                    Unmade(number, LegVerdict.NotAdmitted, refusal);
                    return null;
                }

                _work.Progress($"worker {number}: making '{worker}' the tree as it was read");
                await _runner._copies.SyncAsync(_reading, worker, cancellationToken).ConfigureAwait(false);

                _work.Progress($"worker {number}: building it whole");
                var request = Request(number, worker, $"{MutationRecords.WorkersDirectory}/{number}", _subject.Project);
                var baseline = await _runner._builder.BuildAsync(_config, request, cancellationToken).ConfigureAwait(false);

                if (baseline.Verdict.Verdict != LegVerdict.Passed)
                {
                    Unmade(number, baseline.Verdict.Verdict, $"its build of the unmutated tree: {baseline.Verdict.Detail}");
                    return null;
                }

                return await _runner._builder.ReadGraphAsync(_config, request, cancellationToken).ConfigureAwait(false);
            }
            catch (HarnessException ex) when (!HarnessExit.RefusesTheRun(ex.ExitCode))
            {
                Unmade(number, Verdicts.ForRefusal(ex.ExitCode), ex.Message);
                return null;
            }
            catch (Exception ex) when (KnownCauses.Names(ex))
            {
                Unmade(number, LegVerdict.Failed, ex.Message);
                return null;
            }
            finally
            {
                // The room a worker's making claims is given back once it is made: its copy and its build are then on disk,
                // and what each arm rebuilds in it takes no more.
                admitted?.Dispose();
            }
        }

        /// <summary>
        /// Drives <paramref name="arm"/> in worker <paramref name="number"/>, under a unit of admission of its own: its
        /// line, and why the worker is retired where a site could not be put back.
        /// </summary>
        private async Task<(ArmEntry Entry, string? Retired)> DriveArmAsync(int number, MutationArm arm, IWorkerGraph graph, CancellationToken cancellationToken)
        {
            var started = Stopwatch.GetTimestamp();
            var records = Path.Combine(_work.RunDirectory, _leg.Name, MutationRecords.ArmsDirectory, arm.Id);

            using var admitted = await _work.AdmitUnit(new UnitAdmission(arm.Id, null, First()), cancellationToken).ConfigureAwait(false);

            if (admitted is { Refusal: { } refusal })
            {
                return (Record(arm, ReachedVerdict.Of(LegVerdict.NotAdmitted, refusal), number, started, null, records), null);
            }

            _work.Progress($"arm {arm.Id}, on worker {number}");

            var bound = TimeSpan.Zero;

            if (arm.Kind == RedKind.TestRed)
            {
                var control = await ControlAsync(number, arm.Runner, graph, cancellationToken).ConfigureAwait(false);

                if (control.Stops is { } stops)
                {
                    return (Undriven(arm, ReachedVerdict.Of(LegVerdict.Stopped, stops)), null);
                }

                bound = control.Bound;
            }

            var observed = await ObserveAsync(number, arm, graph, records, bound, cancellationToken).ConfigureAwait(false);

            return (Record(arm, observed.Verdict, number, started, observed.Report, records), observed.Retired);
        }

        /// <summary>
        /// Everything an arm's verdict is decided from: its pre-flight, its mutation and the build of it, and its run or its
        /// paired control - every site put back as it was, whatever happens, and checked against the reading of the tree.
        /// </summary>
        private async Task<(ReachedVerdict Verdict, JUnitReport? Report, string? Retired)> ObserveAsync(
            int number,
            MutationArm arm,
            IWorkerGraph graph,
            string records,
            TimeSpan bound,
            CancellationToken cancellationToken)
        {
            var worker = Worker(number);
            var buildDirectory = _leg.Variant.DirectoryUnder(worker);
            var sites = arm.Sites
                .Select(declared => new SiteState(
                    declared,
                    InWorker(worker, declared.Site),
                    _runner._site.Read(InWorker(worker, declared.Site)),
                    _reading.Files.Entries.GetValueOrDefault(declared.Site)))
                .ToList();
            var (preflight, control, diagnostic, builds, outputs) = Preflight(arm, worker, sites, graph);
            var observation = new ArmObservation(preflight);

            if (ArmJudge.Judge(arm, observation) is { } refused)
            {
                return (Held(arm, refused), null, null);
            }

            ReachedVerdict? failure = null;
            JUnitReport? report = null;
            string? restored;

            try
            {
                var project = _subject.Project.Retargeted(builds, outputs);
                var before = _runner._builder.ReadLog(buildDirectory);

                await WriteAsync([.. sites.Select(state => (state, state.Mutated!))], buildDirectory, cancellationToken).ConfigureAwait(false);

                var build = await _runner._builder
                    .BuildAsync(_config, Request(number, worker, $"{MutationRecords.ArmsDirectory}/{arm.Id}", project), cancellationToken)
                    .ConfigureAwait(false);

                observation = observation with { Build = Observe(build, before, buildDirectory, preflight.Dependents, graph) };

                if (ArmJudge.Judge(arm, observation) is null && ArmJudge.Next(arm, observation) == ArmStep.Control)
                {
                    // The paired control replaces a text of the site as it was, never as the mutation left it.
                    var controlBefore = _runner._builder.ReadLog(buildDirectory);

                    await WriteAsync([(sites[0], control!)], buildDirectory, cancellationToken).ConfigureAwait(false);

                    var controlBuild = await _runner._builder
                        .BuildAsync(_config, Request(number, worker, $"{MutationRecords.ArmsDirectory}/{arm.Id}/{MutationRecords.PairedControlDirectory}", project), cancellationToken)
                        .ConfigureAwait(false);

                    observation = observation with { Control = Observe(controlBuild, controlBefore, buildDirectory, preflight.Dependents, graph) };
                }
                else if (ArmJudge.Judge(arm, observation) is null)
                {
                    var run = await _runner._tests
                        .RunAsync(RunRequest($"{_leg.Name}/{MutationRecords.ArmsDirectory}/{arm.Id}", worker, buildDirectory, graph.ProgramOf(arm.Runner).Path!, records, bound, diagnostic), cancellationToken)
                        .ConfigureAwait(false);

                    report = run.Run.Report;
                    observation = observation with { Run = run.Run };
                }
            }
            catch (HarnessException ex) when (!HarnessExit.RefusesTheRun(ex.ExitCode))
            {
                failure = ReachedVerdict.Of(Verdicts.ForRefusal(ex.ExitCode), ex.Message);
            }
            catch (Exception ex) when (KnownCauses.Names(ex))
            {
                failure = ReachedVerdict.Of(LegVerdict.Failed, ex.Message);
            }
            catch (Exception ex) when (ex is not (OperationCanceledException or HarnessException))
            {
                // A defect of this tool's, in one arm: the arm is poisoned, and the worker, whose copy nothing now vouches
                // for, drives no other.
                failure = ReachedVerdict.Of(LegVerdict.Poisoned, $"the sweep could not judge this arm, {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                restored = await RestoreAsync(sites, buildDirectory).ConfigureAwait(false);
            }

            if (restored is not null)
            {
                return (
                    ReachedVerdict.Of(LegVerdict.Poisoned, $"a site could not be put back as it was, so its worker drives no other arm: {restored}"),
                    report,
                    $"a site of arm '{arm.Id}' could not be put back as it was: {restored}");
            }

            if (failure is not null)
            {
                return (failure, report, failure.Verdict == LegVerdict.Poisoned ? $"arm '{arm.Id}' ended in a defect: {failure.Detail}" : null);
            }

            return (
                ArmJudge.Judge(arm, observation) is { } judged ? Held(arm, judged) : ReachedVerdict.OrPoisoned(null, $"{_leg.Name}/{arm.Id}"),
                report,
                null);
        }

        /// <summary>
        /// <paramref name="judged"/>, the verdict the judge reached for <paramref name="arm"/>, as the sweep reports it: held
        /// to the arm's designed verdict in a self-test, and as reached otherwise. Only the judge's own verdict is held: one
        /// a sweep ended the arm with - stopped, not admitted, a defect - says nothing of how the judge read it.
        /// </summary>
        private ReachedVerdict Held(MutationArm arm, ReachedVerdict judged)
            => _subject.Hold is { } hold ? hold(arm.Id, judged) : judged;

        /// <summary>
        /// What an arm's pre-flight reads of the worker's copy and its build, with what the arm writes once it passes: the
        /// paired control's site, the diagnostic, and the targets and files its build builds.
        /// </summary>
        private (ArmPreflight Preflight, byte[]? Control, string? Diagnostic, IReadOnlyList<string> Builds, IReadOnlyList<string> Outputs) Preflight(
            MutationArm arm,
            string worker,
            IReadOnlyList<SiteState> sites,
            IWorkerGraph graph)
        {
            var problems = new List<string>();
            var counts = new List<TextCount>();
            var unchanged = new List<UnchangedSite>();

            byte[]? Text(string cited, bool before)
            {
                var bytes = _runner._site.Read(InWorker(worker, cited));

                if (bytes is null)
                {
                    problems.Add($"text '{cited}' is not a file in the worker's copy of the tree");
                    return null;
                }

                var text = SiteEdit.Text(bytes);

                if (before && text.Length == 0)
                {
                    problems.Add($"the text in '{cited}' holds nothing, and a text holding nothing occurs everywhere");
                    return null;
                }

                return text;
            }

            foreach (var state in sites)
            {
                var before = Text(state.Declared.Before, before: true);
                var after = Text(state.Declared.After, before: false);

                if (state.Pristine is not { } pristine || before is null || after is null)
                {
                    continue;
                }

                var edit = SiteEdit.Apply(pristine, before, after);

                state.Mutated = edit.Edited;
                counts.Add(new TextCount(state.Declared.Before, state.Declared.Site, edit.Occurrences));

                if (edit.ChangesNothing)
                {
                    unchanged.Add(new UnchangedSite(state.Declared.Before, state.Declared.After, state.Declared.Site));
                }
            }

            byte[]? control = null;

            if (arm.Control is { } paired && sites[0].Pristine is { } own)
            {
                var before = Text(paired.Before, before: true);
                var after = Text(paired.After, before: false);

                if (before is not null && after is not null)
                {
                    var edit = SiteEdit.Apply(own, before, after);

                    control = edit.Edited;
                    counts.Add(new TextCount(paired.Before, arm.Own.Site, edit.Occurrences));

                    if (edit.ChangesNothing)
                    {
                        unchanged.Add(new UnchangedSite(paired.Before, paired.After, arm.Own.Site));
                    }
                }
            }

            string? diagnostic = null;

            if (arm.Kind == RedKind.TestRed && Text(arm.Diagnostic, before: true) is { } said)
            {
                diagnostic = System.Text.Encoding.UTF8.GetString(said);
            }

            var builds = ArmJudge.Builds(arm);
            var outputs = builds.SelectMany(graph.OutputsOf).Distinct(StringComparer.Ordinal).ToList();

            // A site the reading does not hold as the row spells it, by how the reading does spell it where only the case
            // of its letters differs: found there by a file system that folds case, and by no other.
            var unread = sites
                .Where(state => state.Read is null)
                .Select(state => (State: state, Tree: _reading.Files.Paths.FirstOrDefault(path => string.Equals(path, state.Declared.Site, StringComparison.OrdinalIgnoreCase))))
                .ToList();

            return (
                new ArmPreflight
                {
                    MisspeltSites = [.. unread.Where(site => site.Tree is not null).Select(site => new SiteSpelling(site.State.Declared.Site, site.Tree!))],
                    MissingSites = [.. sites.Where(state => state.Pristine is null).Select(state => state.Declared.Site)],
                    UnreadSites = [.. unread.Select(site => site.State.Declared.Site)],
                    TextProblems = problems,
                    Counts = counts,
                    Unchanged = unchanged,
                    TargetBuilt = graph.OutputsOf(arm.Target).Count > 0,
                    RunnerProblem = arm.Kind == RedKind.TestRed ? graph.ProgramOf(arm.Runner).Problem : null,
                    Dependents = graph.DependentObjects(builds, [.. sites.Select(state => state.Path)]),
                },
                control,
                diagnostic,
                builds,
                outputs);
        }

        /// <summary>
        /// The pristine control of <paramref name="runner"/> on this leg: built and run in worker <paramref name="number"/>
        /// the first time any worker needs it, and shared by every arm of the binary.
        /// </summary>
        private Task<PristineOutcome> ControlAsync(int number, string runner, IWorkerGraph graph, CancellationToken cancellationToken)
            => _controls
                .GetOrAdd(runner, _ => new Lazy<Task<PristineOutcome>>(() => RunControlAsync(number, runner, graph, cancellationToken)))
                .Value;

        private async Task<PristineOutcome> RunControlAsync(int number, string runner, IWorkerGraph graph, CancellationToken cancellationToken)
        {
            var worker = Worker(number);
            var buildDirectory = _leg.Variant.DirectoryUnder(worker);
            var records = Path.Combine(_work.RunDirectory, _leg.Name, MutationRecords.ControlsDirectory, runner);
            var program = graph.ProgramOf(runner);

            // A runner that builds no program is each of its arms' pre-flight to say, as violated: there is nothing to control.
            if (program.Path is not { } path)
            {
                return new PristineOutcome(null, null, TimeSpan.Zero);
            }

            _work.Progress($"the unmutated {runner}, on worker {number}");

            try
            {
                var build = await _runner._builder
                    .BuildAsync(_config, Request(number, worker, $"{MutationRecords.ControlsDirectory}/{runner}", _subject.Project.Retargeted([runner], graph.OutputsOf(runner))), cancellationToken)
                    .ConfigureAwait(false);
                var observed = new ArmBuild(build.Verdict, [], []);

                if (build.Verdict.Verdict != LegVerdict.Passed)
                {
                    return Decided(PristineJudge.Judge(runner, observed, null, TimeSpan.Zero, _subject.Settings.RunTimeFactor));
                }

                var run = await _runner._tests
                    .RunAsync(RunRequest($"{_leg.Name}/{MutationRecords.ControlsDirectory}/{runner}", worker, buildDirectory, path, records, null, null), cancellationToken)
                    .ConfigureAwait(false);

                return Decided(PristineJudge.Judge(runner, observed, run.Run, run.Duration, _subject.Settings.RunTimeFactor));
            }
            catch (Exception ex) when ((ex is HarnessException harness && !HarnessExit.RefusesTheRun(harness.ExitCode)) || KnownCauses.Names(ex))
            {
                // The leg's own, as a build of the leg that could not run would be: only this binary's arms are stopped, and
                // every other binary's are still driven.
                var verdict = ex is HarnessException refused ? Verdicts.ForRefusal(refused.ExitCode) : LegVerdict.Failed;

                return Decided(new PristineOutcome(
                    ReachedVerdict.Of(verdict, $"the unmutated {runner} could not be built and run: {ex.Message}"),
                    $"the unmutated {runner} could not be built and run, so nothing could tell what a mutation of it changed",
                    TimeSpan.Zero));
            }
        }

        /// <summary><paramref name="outcome"/>, its verdict for the leg counted among the leg's own.</summary>
        private PristineOutcome Decided(PristineOutcome outcome)
        {
            if (outcome.Leg is { } leg)
            {
                _own.Enqueue(leg);
            }

            return outcome;
        }

        /// <summary>
        /// One build of an arm as the judge reads it: the steps a failed build said failed, and the objects depending on a
        /// site that ninja's log shows no step run for where it passed.
        /// </summary>
        private ArmBuild Observe(BuildResult build, NinjaLog? before, string buildDirectory, IReadOnlyList<string> dependents, IWorkerGraph graph)
        {
            if (build.Verdict.Verdict == LegVerdict.Failed)
            {
                var failed = build.Phases.LastOrDefault(phase => !phase.Passed && string.Equals(phase.Phase, CMakeAdapter.BuildPhase, StringComparison.Ordinal));

                return new ArmBuild(build.Verdict, failed is null ? [] : graph.FailedOutputs(failed.Output.Lines()), []);
            }

            if (build.Verdict.Verdict != LegVerdict.Passed)
            {
                return new ArmBuild(build.Verdict, [], []);
            }

            if (before is null || _runner._builder.ReadLog(buildDirectory) is not { } after)
            {
                return new ArmBuild(
                    ReachedVerdict.Of(
                        LegVerdict.Unmeasured,
                        $"ninja's log in '{buildDirectory}' could not be read {(before is null ? "before" : "after")} the build, so nothing witnessed "
                        + "that each object depending on the site was rebuilt"),
                    [],
                    []);
            }

            var rebuilt = NinjaLog.Rebuilt(before, after, dependents);

            return new ArmBuild(build.Verdict, [], [.. dependents.Except(rebuilt, StringComparer.Ordinal)]);
        }

        /// <summary>
        /// Writes each site's new bytes dated forward of what the worker's last build left, then waits until the clock is
        /// past that, so the build that follows sees every one changed and dates nothing it writes before them.
        /// </summary>
        private async Task WriteAsync(IReadOnlyList<(SiteState Site, byte[] Bytes)> writes, string buildDirectory, CancellationToken cancellationToken)
        {
            var stamp = _runner._site.Stamp(buildDirectory);

            foreach (var (state, bytes) in writes)
            {
                state.Touched = true;
                await _runner._site.WriteAsync(state.Path, bytes, stamp, cancellationToken).ConfigureAwait(false);
            }

            await _runner._site.UntilPastAsync(stamp, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Puts each site written back as the worker held it, dated forward so the next build rebuilds what the mutation
        /// built, and checks every site against the sweep's reading of the tree by its hash; what is wrong, or
        /// <see langword="null"/> where nothing is. Never stopped: a sweep stopped part way still puts its sites back.
        /// </summary>
        /// <param name="sites">The sites of an arm past its pre-flight: each a file the worker held, as the reading spells it.</param>
        /// <param name="buildDirectory">The worker's build directory.</param>
        private async Task<string?> RestoreAsync(IReadOnlyList<SiteState> sites, string buildDirectory)
        {
            var problems = new List<string>();
            var touched = sites.Where(state => state.Touched).ToList();

            if (touched.Count > 0)
            {
                try
                {
                    var stamp = _runner._site.Stamp(buildDirectory);

                    foreach (var state in touched)
                    {
                        await _runner._site.WriteAsync(state.Path, state.Pristine!, stamp, CancellationToken.None).ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HarnessException)
                {
                    problems.Add($"writing it back failed: {ex.Message.TrimEnd('.')}");
                }
            }

            foreach (var state in sites)
            {
                var held = _runner._site.Read(state.Path);

                if (held is null)
                {
                    problems.Add($"'{state.Declared.Site}' is gone");
                }
                else if (!string.Equals(FileContentHash.Of(held), state.Read!.ContentHash, StringComparison.Ordinal))
                {
                    problems.Add($"'{state.Declared.Site}' does not hold what the tree held when the sweep read it");
                }
            }

            return problems.Count == 0 ? null : string.Join("; ", problems);
        }

        /// <summary>A build in worker <paramref name="number"/>, logged under <paramref name="records"/> within the leg's records.</summary>
        private BuildRequest Request(int number, string worker, string records, ProjectConfig project)
            => _leg.BuildRequestFor(_config, _work.RunDirectory, project, _work.Time) with
            {
                Leg = $"{_leg.Name}/{records}",
                TreeRoot = worker,
            };

        /// <summary>
        /// A whole run of <paramref name="program"/>, built in <paramref name="buildDirectory"/>, as the subject's tests start:
        /// the leg's, or where the subject has none, in the worker with what the leg's host gives it.
        /// </summary>
        private ArmRunRequest RunRequest(string named, string worker, string buildDirectory, string program, string records, TimeSpan? bound, string? diagnostic)
        {
            var invocation = _subject.Tests is { } settings ? TestInvocationResolver.InvocationFor(settings, _leg.Host.Os ?? _leg.Leg.Os) : null;
            var paths = new LegPaths(worker, buildDirectory) { Identity = _leg.IdentityFor(_work.RunId.Value) };

            return new ArmRunRequest
            {
                Leg = named,
                Program = Path.Combine(buildDirectory, program),
                ReportArgs = _subject.Settings.ReportArgs ?? [],
                RecordDirectory = records,
                WorkingDirectory = TestInvocationResolver.StartDirectory(invocation is null ? null : TestInvocationResolver.WorkingDirectoryFor(invocation, paths), worker),
                Environment = TestInvocationResolver.EnvironmentFor(_leg.Environment, invocation?.Env ?? new Dictionary<string, string>()),
                AppendToPath = _leg.Host.ProgramDirectories,
                StallSeconds = _config.Defaults.StallSeconds,
                ClockStepToleranceMilliseconds = _config.Defaults.ClockStepToleranceMilliseconds,
                Bound = bound,
                Factor = _subject.Settings.RunTimeFactor,
                Diagnostic = diagnostic,
            };
        }

        /// <summary>Worker <paramref name="number"/>'s copy.</summary>
        private string Worker(int number) => MutationWorkers.PathOf(_subject.TreeRoot, _leg.Variant, number);

        /// <summary>Whether this is the sweep's first ask of its machine, the one ask that settles.</summary>
        private bool First() => Interlocked.Exchange(ref _asked, 1) == 0;

        /// <summary>Retires worker <paramref name="number"/>, which could not be made, saying why.</summary>
        private void Unmade(int number, LegVerdict verdict, string why)
        {
            _unmade[number] = ReachedVerdict.Of(verdict, $"worker {number}: {why}");
            _queue.Retire(number, why, requeue: false);
        }

        /// <summary>
        /// <paramref name="arm"/>'s line where it reached <paramref name="verdict"/> driven by worker <paramref name="number"/>,
        /// its record written beside its logs.
        /// </summary>
        private ArmEntry Record(MutationArm arm, ReachedVerdict verdict, int number, long started, JUnitReport? report, string records)
        {
            var entry = new ArmEntry
            {
                Arm = arm.Id,
                Verdict = verdict.Verdict,
                Detail = verdict.Detail,
                Duration = Stopwatch.GetElapsedTime(started),
                Worker = number,
                Cases = report?.Ran,
                DeclaredCases = arm.Cases,
                Reds = report?.Reds,
                DeclaredReds = arm.Reds,
                Records = records,
            };

            try
            {
                _runner._fileSystem.CreateDirectory(records);
                _runner._fileSystem.WriteAllTextAtomic(Path.Combine(records, MutationRecords.ArmRecordFileName), LedgerReport.ArmJson(entry) + "\n");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _runner._output.Warn(_runner._commandName, $"{_leg.Name}: the record of arm '{arm.Id}' could not be written to '{records}': {ex.Message}");
            }

            return entry;
        }

        /// <summary>The leg's line: the worst of its own verdicts and its arms', with how many arms reached what.</summary>
        private LegEntry Line(IReadOnlyList<ReachedVerdict> own, IReadOnlyList<(MutationArm Arm, ArmEntry Entry)> arms, IReadOnlyList<string> notes)
        {
            var ordered = arms.OrderBy(pair => pair.Arm.Line).Select(pair => pair.Entry).ToList();
            var verdict = Verdicts.Worst([.. own.Select(reached => reached.Verdict), .. ordered.Select(arm => arm.Verdict)]);
            var counts = ordered
                .GroupBy(arm => arm.Verdict)
                .OrderBy(group => Verdicts.Rank(group.Key))
                .Select(group => $"{group.Count()} {Verdicts.Display(group.Key)}");
            var detail = new List<string>(own.Where(reached => reached.Detail.Length > 0).Select(reached => reached.Detail))
            {
                $"{ordered.Count} arm(s): {string.Join(", ", counts)}",
            };

            detail.AddRange(notes);

            return _leg.Entry(verdict, string.Join("; ", detail)) with { Arms = ordered };
        }
    }

    /// <summary><paramref name="arm"/>'s line where no worker drove it, reaching <paramref name="verdict"/>.</summary>
    private static ArmEntry Undriven(MutationArm arm, ReachedVerdict verdict) => new()
    {
        Arm = arm.Id,
        Verdict = verdict.Verdict,
        Detail = verdict.Detail,
        DeclaredCases = arm.Cases,
        DeclaredReds = arm.Reds,
    };

    /// <summary>Where <paramref name="cited"/>, as a row spells it, is in <paramref name="worker"/>'s copy.</summary>
    private static string InWorker(string worker, string cited) => Path.Combine(worker, cited.Replace('/', Path.DirectorySeparatorChar));
}
