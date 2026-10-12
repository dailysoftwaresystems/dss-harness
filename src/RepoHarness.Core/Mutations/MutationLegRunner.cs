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
    /// <summary>The tree read once for every worker: the leg's own, or for a self-test the fixture's.</summary>
    public required string TreeRoot { get; init; }

    /// <summary>
    /// Where the workers are kept: the family of copies beside the leg's own tree its variant's sweeps keep, or its
    /// self-test's.
    /// </summary>
    public required WorkerFamily Workers { get; init; }

    /// <summary>The project each worker builds, as the leg's variant builds it.</summary>
    public required ProjectConfig Project { get; init; }

    /// <summary>
    /// The dependency sources the leg's own build fetched, which each worker is given its own copy of and configured
    /// with; none for a self-test, whose fixture fetches nothing.
    /// </summary>
    public FetchedSet Fetched { get; init; } = FetchedSet.None;

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
/// <para>
/// Whatever fails is kept to what it failed in: a worker that cannot be made is retired alone, an arm whose driving ends
/// in a failure is given the verdict that failure comes to, and a binary whose control cannot be had stops its own arms.
/// Once its machine refuses one arm, the sweep asks it for no other: each arm left is <c>not-admitted</c> at once. A
/// sweep stopped part way, or ended by a refusal of the run, still answers with the leg's line - each arm judged by then,
/// and every other <c>stopped</c> - once every site is back.
/// </para>
/// <para>
/// What it timed that its clock makes suspect is said on the leg's line, beside the verdict and never in it: what each
/// of its builds says of its own timings, and each run - an arm's, or the unmutated one that bounds it - whose clocks
/// disagreed past <c>defaults.clockStepToleranceMilliseconds</c>.
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

    /// <summary>Reads which compilers a worker's build identified, for a test invocation that names one.</summary>
    private readonly CMakeToolchainReader _toolchains = new(fileSystem);

    /// <summary>Sweeps <paramref name="subject"/> on <paramref name="work"/>'s leg, and returns the leg's line.</summary>
    /// <param name="subject">What the leg sweeps.</param>
    /// <param name="work">The leg, its run and its context, and how it asks its machine to take each unit.</param>
    /// <param name="cancellationToken">
    /// Stops the sweep: each site is put back, and the leg's line says each arm judged by then, and every other as
    /// <c>stopped</c>. Stopped before any worker was planned, it reports nothing of the leg, as nothing of it was measured.
    /// </param>
    /// <returns>
    /// The leg's line. Where a refusal of the run ended the sweep, the line names it as what
    /// <see cref="LegEntry.EndsTheRun">ends the run</see>, with each arm judged before it.
    /// </returns>
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

    /// <summary>The arm a sweep's machine did not admit, after which the sweep asks it for nothing more.</summary>
    /// <param name="Arm">The arm.</param>
    /// <param name="Refusal">Why it was not admitted, as its line says it.</param>
    private sealed record RefusedArm(string Arm, string Refusal);

    /// <summary>What driving one arm in a worker came to.</summary>
    /// <param name="Entry">The arm's line.</param>
    /// <param name="Retired">Why its worker drives no other arm, or <see langword="null"/> where it goes on.</param>
    /// <param name="Refusal">The refusal of the run that ended the arm, raised once its line is kept; otherwise <see langword="null"/>.</param>
    private sealed record Driven(ArmEntry Entry, string? Retired = null, HarnessException? Refusal = null);

    /// <summary>What observing one arm came to, its sites put back or said not to be.</summary>
    /// <param name="Verdict">The arm's verdict.</param>
    /// <param name="Report">The report its run wrote, where it ran and wrote one.</param>
    /// <param name="Retired">Why its worker drives no other arm, or <see langword="null"/> where it goes on.</param>
    /// <param name="Refusal">The refusal of the run that ended the arm; otherwise <see langword="null"/>.</param>
    private sealed record Observed(ReachedVerdict Verdict, JUnitReport? Report = null, string? Retired = null, HarnessException? Refusal = null);

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

        /// <summary>Why what the sweep timed is suspect, each kept for the leg's line by whichever worker met it.</summary>
        private readonly ConcurrentQueue<string> _timing = new();
        private SyncSource _reading = null!;

        /// <summary>The dependencies each worker is configured with the sources of: what the leg's build fetched, but for what its project points at itself.</summary>
        private FetchedSet _given = FetchedSet.None;

        /// <summary>The sources each worker is given its own copy of, as the sweep read them, once.</summary>
        private IReadOnlyList<FetchedReading> _fetched = [];

        /// <summary>Where each worker has the sources its own copy of the tree holds, by their dependency's name, relative to the worker.</summary>
        private IReadOnlyDictionary<string, string> _inTree = new Dictionary<string, string>(StringComparer.Ordinal);

        private ArmQueue _queue = null!;
        private CancellationTokenSource _unasked = null!;
        private RefusedArm? _refused;
        private Exception? _escaped;
        private int _asked;

        public async Task<LegEntry> RunAsync(CancellationToken cancellationToken)
        {
            try
            {
                return await SweepAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                // What was packed of the tree's history for the workers goes with the sweep's reading of it.
                _reading?.Dispose();
            }
        }

        private async Task<LegEntry> SweepAsync(CancellationToken cancellationToken)
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

            var unlinked = await ReadFetchedAsync(cancellationToken).ConfigureAwait(false);
            var (plan, needs) = await PlanAsync(driven.Count, cancellationToken).ConfigureAwait(false);

            if (plan.RunsNone)
            {
                // No room for even one worker, which turns the leg away as a leg whose build does not fit is turned away.
                var turned = ReachedVerdict.Of(LegVerdict.SkippedUnavailable, plan.Fewer);

                return Line([turned], [.. driven.Select(arm => (arm, Undriven(arm, turned))), .. unselected], []);
            }

            var notes = new List<string>(unlinked);

            foreach (var note in unlinked)
            {
                _runner._output.Warn(_runner._commandName, $"{_leg.Name}: {note}");
            }

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
            using (_unasked = new CancellationTokenSource())
            {
                await Task.WhenAll(Enumerable.Range(1, plan.Count).Select(number => Task.Run(() => WorkerAsync(number, needs[number - 1], stop), CancellationToken.None)))
                    .ConfigureAwait(false);

                // Every worker has put its sites back and stopped asking, whatever ended the sweep: what none of them drove
                // is said, never passed over - by a sweep stopped part way, or ended by a refusal, as by one that ran out
                // of workers.
                foreach (var undriven in _queue.Undriven(stop.Token))
                {
                    _entries[undriven.Arm.Id] = Undriven(undriven.Arm, undriven.Verdict);
                }
            }

            // A worker that could not be made is the leg's own failure only where no worker was made: one made beside it drove
            // the arms, and the leg says the sweep ran with fewer.
            var own = _own.ToList();

            // What ended the sweep from outside any arm's or worker's own handling: a refusal of the run, which the line
            // carries to be raised once it is recorded; or a failure nobody named, which is the leg's own.
            var refusal = _escaped is HarnessException raised && HarnessExit.RefusesTheRun(raised.ExitCode) ? raised : null;

            if (_escaped is { } escaped && refusal is null)
            {
                own.Add(Failure(escaped, "the sweep ended in a failure nobody named"));
            }

            if (_unmade.Count == plan.Count)
            {
                own.AddRange(_unmade.OrderBy(pair => pair.Key).Select(pair => pair.Value));
            }
            else
            {
                notes.AddRange(_unmade.OrderBy(pair => pair.Key).Select(pair => $"{pair.Value.Detail.TrimEnd('.')}, so it drove no arm"));
            }

            return Line(own, [.. driven.Select(arm => (arm, _entries[arm.Id])), .. unselected], notes) with { EndsTheRun = refusal };
        }

        /// <summary>
        /// Reads, once for every worker, the dependency sources the leg's own build fetched: each the tree's reading does
        /// not carry, which every worker is given its own copy of. Those it does carry are each worker's already, in its
        /// copy of the tree, and what the project points at itself is the project's to say, in a worker as in the leg.
        /// What is to be said of them on the leg's line: the links among them, which no copy carries.
        /// </summary>
        private async Task<IReadOnlyList<string>> ReadFetchedAsync(CancellationToken cancellationToken)
        {
            var given = _subject.Fetched.Found
                .Where(source => !_subject.Project.CacheVars.ContainsKey(BuildDirectoryGuard.FetchContentSource + source.Name))
                .ToList();

            // A name of the configuration compares ignoring case, and the build system reads a cache variable by its
            // exact spelling: a dependency the project names in another case is left to it all the same, and that name
            // points the dependency nowhere - the worker fetches it as the leg's build did. Fetching is turned off only
            // where every dependency is one the worker has the sources of by a name that is read.
            var pointed = _subject.Fetched.Found.Count(source => _subject.Project.CacheVars.Keys.Contains(BuildDirectoryGuard.FetchContentSource + source.Name, StringComparer.Ordinal));

            _given = new FetchedSet(given, _subject.Fetched.Every && given.Count + pointed == _subject.Fetched.Found.Count);

            var names = PathCase.In(_runner._fileSystem, _subject.TreeRoot);
            var inTree = new Dictionary<string, string>(StringComparer.Ordinal);
            var fetched = new List<FetchedReading>();
            var unlinked = new List<string>();

            foreach (var source in given)
            {
                if (FetchedSources.CarriedAt(source, _subject.TreeRoot, _reading.Files, names) is { } within)
                {
                    inTree[source.Name] = within;
                    continue;
                }

                _work.Progress($"reading the sources of '{source.Name}' the leg's build fetched, '{source.Directory}', once for every worker");

                var read = new FetchedReading(source, await _runner._copies.ReadFetchedAsync(source.Directory, cancellationToken).ConfigureAwait(false));

                fetched.Add(read);

                if (read.Files.Links.Count > 0)
                {
                    unlinked.Add(
                        $"the sources of '{source.Name}' the leg's build fetched hold {read.Files.Links.Count} link(s), which no worker is given: "
                        + ReportText.Listed(read.Files.Links));
                }
            }

            _inTree = inTree;
            _fetched = fetched;

            return unlinked;
        }

        /// <summary>Where <paramref name="worker"/> has the sources of <paramref name="source"/>: in its own copy of the tree, or among what it is given.</summary>
        private string Kept(string worker, FetchedSource source)
            => _inTree.TryGetValue(source.Name, out var within)
                ? Path.Combine(worker, within.Replace('/', Path.DirectorySeparatorChar))
                : FetchedSources.KeptIn(worker, source.Name);

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

            foreach (var beyond in (await _runner._copies.ListAsync(_subject.Workers, cancellationToken).ConfigureAwait(false))
                .Where(copy => copy.Number > cap))
            {
                await RemoveBeyondAsync(beyond, cap, cancellationToken).ConfigureAwait(false);
            }

            // Within this machine's path limit, as a worktree is kept: a worker whose build passes it fails as compile errors
            // in files nobody touched. A worker's path grows only with its number, so the workers that fit come first.
            var fit = Enumerable.Range(1, wanted).TakeWhile(number => Budget(number).IsWithinBudget).Count();
            var tooLong = fit < wanted ? TooLong(fit + 1) : null;

            if (fit == 0)
            {
                return (WorkerRoom.Plan([], wanted, tooLong, room: null, unmeasured: null, source: null), []);
            }

            // A worker's copy is the tree and the dependency sources it is given, as the sweep's readings count them, and
            // what git keeps there of the tree's history: asked only where a worker is yet to be made, since it is packed
            // to be counted - once, for every worker this reading makes, and before the room is measured, so the pack
            // itself is within what is measured.
            var history = Enumerable.Range(1, fit).All(number => _runner._fileSystem.DirectoryExists(Worker(number)))
                ? 0
                : await _runner._copies.HistoryBytesAsync(_reading, cancellationToken).ConfigureAwait(false);
            var copy = _reading.Files.Entries.Values.Sum(entry => entry.Size) + _fetched.Sum(read => read.Bytes) + history;
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

            // Worded by the one owner of the plan, against the workers the sweep wanted: the room and the path limit both.
            return (WorkerRoom.Plan(needs, wanted, tooLong, room, unmeasured, _subject.ExpectedBuildBytes is null ? null : _subject.ExpectedBuildSource), needs);
        }

        /// <summary>
        /// Whether worker <paramref name="number"/>'s build stays within this machine's path limit, reckoned as a worktree's
        /// is: the worker, its build directory below it, and below that the longest path a build of the project makes -
        /// <c>worktrees.pathBudgetReserve</c>, or the subject's own - with <c>worktrees.pathBudgetMargin</c> to spare. The
        /// dependency sources it is given are within the limit too, and reckon it where the longest path among them is the
        /// longer.
        /// </summary>
        private PathBudgetResult Budget(int number)
        {
            var worker = Worker(number);
            var settings = _config.Worktrees;

            return _runner._pathBudget.Check(worker, Math.Max(BuildBelow(worker), GivenBelow(worker)), settings.PathBudgetMargin, settings.PathLimit);
        }

        /// <summary>
        /// The longest path <paramref name="worker"/>'s build makes below it: its build directory, the separator before it
        /// counted - what a worktree's check counts for the longest variant this machine builds - and below that the
        /// longest path a build of the project makes.
        /// </summary>
        private int BuildBelow(string worker)
            => _leg.Variant.DirectoryUnder(worker).Length - Path.TrimEndingDirectorySeparator(worker).Length
                + (_subject.PathReserve ?? _config.Worktrees.PathBudgetReserve);

        /// <summary>
        /// The longest path below <paramref name="worker"/> among the dependency sources it is given, the separator
        /// before it counted; nothing where it is given none.
        /// </summary>
        /// <remarks>
        /// Sources FetchContent fetched into the leg's build directory are kept in a worker under no longer a path than
        /// its own build directory would keep them under, so the build's longest covers them; sources the leg was pointed
        /// at elsewhere are as deep as whoever keeps them made them.
        /// </remarks>
        private int GivenBelow(string worker)
            => _fetched
                .Select(read => FetchedSources.KeptIn(worker, read.Source.Name).Length - Path.TrimEndingDirectorySeparator(worker).Length
                    + 1 + read.Files.Entries.Keys.Select(path => path.Length).DefaultIfEmpty(0).Max())
                .DefaultIfEmpty(0)
                .Max();

        /// <summary>Why worker <paramref name="number"/> is not run: its build would pass this machine's path limit.</summary>
        private string TooLong(int number)
        {
            var budget = Budget(number);
            var reckoned = GivenBelow(Worker(number)) > BuildBelow(Worker(number)) ? "as the longest path among the dependency sources it is given and worktrees.pathBudgetMargin reckon them"
                : _subject.PathReserve is { } reserve ? $"as the {reserve} its project's build makes below its build directory and worktrees.pathBudgetMargin reckon them"
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
                // Stopped: by the run, or because another worker ended the sweep. Its arms are said by the leg's line.
            }
            catch (Exception ex)
            {
                // Ends the sweep, whose other workers put their sites back and stop: a refusal of the run, or a failure
                // nothing nearer to it could name. Kept for the leg's line, which says it once every worker has stopped.
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
                    Driven driven;

                    try
                    {
                        driven = await DriveArmAsync(number, arm, graph, cancellationToken).ConfigureAwait(false);
                    }
                    catch
                    {
                        // Taken and not driven: the sweep was stopped, or a refusal of the run ended it, while the arm
                        // waited for its machine or for its binary's control. Given back, so it is said stopped with the
                        // arms no worker drove.
                        _queue.GiveBack(number);
                        throw;
                    }

                    _entries[arm.Id] = driven.Entry;

                    // A worker retired is dealt no other arm, and so ends here.
                    if (driven.Retired is { } retired)
                    {
                        _queue.Retire(number, retired, requeue: false);
                    }
                    else
                    {
                        _queue.Done(number);
                    }

                    if (driven.Refusal is { } refusal)
                    {
                        // Raised once the arm's line is kept and its sites are back: it ends the sweep, as it ends the run.
                        ExceptionDispatchInfo.Throw(refusal);
                    }
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
        /// where it could not be made, and it is retired saying why - whatever kept it from being made, short of a stop or a
        /// refusal of the run.
        /// </summary>
        private async Task<IWorkerGraph?> MakeAsync(int number, long need, string worker, CancellationToken cancellationToken)
        {
            Admission? admitted = null;

            try
            {
                var copied = _fetched.Count == 0 ? "its copy of the tree" : "its copy of the tree, and of the dependency sources the leg's build fetched";
                var room = need > 0
                    ? new RoomNeed(
                        need,
                        _subject.ExpectedBuildBytes is null ? copied : $"{copied}, and its build {_subject.ExpectedBuildSource}",
                        worker,
                        string.Empty)
                    : null;

                (admitted, var refusal) = await AskAsync(new UnitAdmission($"worker-{number}", room, First()), arm: null, cancellationToken).ConfigureAwait(false);

                if (refusal is not null)
                {
                    Unmade(number, LegVerdict.NotAdmitted, refusal);
                    return null;
                }

                _work.Progress($"worker {number}: making '{worker}' the tree as it was read");
                await _runner._copies.SyncAsync(_reading, worker, cancellationToken).ConfigureAwait(false);

                // What it keeps of dependency sources is made what this sweep read, as its copy of the tree was: each given
                // again by content, and what an earlier sweep left of one no longer given removed.
                if (_fetched.Count > 0)
                {
                    _work.Progress($"worker {number}: giving it the dependency sources the leg's build fetched");
                }

                await _runner._copies.SyncFetchedAsync(_fetched, worker, cancellationToken).ConfigureAwait(false);

                _work.Progress($"worker {number}: building it whole");
                var request = Request(number, worker, $"{MutationRecords.WorkersDirectory}/{number}", _subject.Project);
                var baseline = Noted($"worker {number}'s build of the unmutated tree", await _runner._builder.BuildAsync(_config, request, cancellationToken).ConfigureAwait(false));

                if (baseline.Verdict.Verdict != LegVerdict.Passed)
                {
                    Unmade(number, baseline.Verdict.Verdict, $"its build of the unmutated tree: {baseline.Verdict.Detail}");
                    return null;
                }

                return await _runner._builder.ReadGraphAsync(_config, request, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (!Stops(ex, cancellationToken) && !EndsTheRun(ex))
            {
                // This worker's alone, whatever it is: the disk full as its copy is written or its build recorded is no
                // reason to lose what the workers beside it judge.
                var failure = Failure(ex, "it could not be made");

                Unmade(number, failure.Verdict, failure.Detail);
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
        /// Asks this machine to take <paramref name="unit"/>, unless it has refused an arm of this sweep: then nothing is
        /// asked, and a unit still waiting stops waiting, each refused as that arm was. What was taken, held until
        /// disposed, or why the unit was not.
        /// </summary>
        /// <remarks>
        /// A refusal comes once the machine has kept a unit waiting as long as it allows. Each arm after one refused would
        /// wait as long again to be told the same: a sweep of a hundred arms would take days to say what its first refusal
        /// said. A worker's own unit refused ends nothing but that worker - it claims room, which no arm does.
        /// </remarks>
        /// <param name="unit">The unit asking.</param>
        /// <param name="arm">The arm the unit is, or <see langword="null"/> for a worker's.</param>
        /// <param name="cancellationToken">Stops the wait.</param>
        private async Task<(Admission? Taken, string? Refusal)> AskAsync(UnitAdmission unit, MutationArm? arm, CancellationToken cancellationToken)
        {
            if (_refused is { } before)
            {
                return (null, Unasked(before));
            }

            using var asking = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _unasked.Token);
            Admission? admitted;

            try
            {
                admitted = await _work.AdmitUnit(unit, asking.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_refused is { } meanwhile)
            {
                return (null, Unasked(meanwhile));
            }

            if (admitted is not { Refusal: { } refusal })
            {
                return (admitted, null);
            }

            if (arm is not null && Interlocked.CompareExchange(ref _refused, new RefusedArm(arm.Id, refusal), null) is null)
            {
                await _unasked.CancelAsync().ConfigureAwait(false);
            }

            return (null, refusal);
        }

        /// <summary>Why a unit is not admitted once the sweep's machine has refused <paramref name="refused"/>.</summary>
        private static string Unasked(RefusedArm refused)
            => $"the sweep stopped asking its machine once arm '{refused.Arm}' was not admitted: {refused.Refusal}";

        /// <summary>
        /// Drives <paramref name="arm"/> in worker <paramref name="number"/>, under a unit of admission of its own: its
        /// line, why the worker is retired where it drives no other, and the refusal of the run that ended the arm where
        /// one did. A failure outside the arm's own build and run - in its machine's answer, its binary's control, its
        /// record - is its verdict as one inside is, and never the sweep's end.
        /// </summary>
        /// <exception cref="OperationCanceledException">The sweep was stopped before the arm was observed: nothing of it was written.</exception>
        /// <exception cref="HarnessException">A refusal of the run was raised before the arm was observed: nothing of it was written.</exception>
        private async Task<Driven> DriveArmAsync(int number, MutationArm arm, IWorkerGraph graph, CancellationToken cancellationToken)
        {
            var started = Stopwatch.GetTimestamp();
            var records = Path.Combine(_work.RunDirectory, _leg.Name, MutationRecords.ArmsDirectory, arm.Id);

            try
            {
                var (admitted, refusal) = await AskAsync(new UnitAdmission(arm.Id, null, First()), arm, cancellationToken).ConfigureAwait(false);

                using (admitted)
                {
                    if (refusal is not null)
                    {
                        return new Driven(Record(arm, ReachedVerdict.Of(LegVerdict.NotAdmitted, refusal), number, started, null, records));
                    }

                    _work.Progress($"arm {arm.Id}, on worker {number}");

                    RunBound? bound = null;

                    if (arm.Kind == RedKind.TestRed)
                    {
                        var control = await ControlAsync(number, arm.Runner, graph, cancellationToken).ConfigureAwait(false);

                        if (control.Stops is { } stops)
                        {
                            return new Driven(Undriven(arm, ReachedVerdict.Of(LegVerdict.Stopped, stops)));
                        }

                        bound = control.Bound;
                    }

                    var observed = await ObserveAsync(number, arm, graph, records, bound, cancellationToken).ConfigureAwait(false);

                    return new Driven(Record(arm, observed.Verdict, number, started, observed.Report, records), observed.Retired, observed.Refusal);
                }
            }
            catch (Exception ex) when (!Stops(ex, cancellationToken) && !EndsTheRun(ex))
            {
                var failure = Failure(ex, "the sweep could not drive this arm");

                return new Driven(Record(arm, failure, number, started, null, records), Defect(arm, failure));
            }
        }

        /// <summary>
        /// Everything an arm's verdict is decided from: its pre-flight, its mutation and the build of it, and its run or its
        /// paired control - every site put back as it was, whatever happens, and checked against the reading of the tree.
        /// </summary>
        private async Task<Observed> ObserveAsync(
            int number,
            MutationArm arm,
            IWorkerGraph graph,
            string records,
            RunBound? bound,
            CancellationToken cancellationToken)
        {
            var worker = Worker(number);
            var buildDirectory = _leg.Variant.DirectoryUnder(worker);
            List<SiteState> sites;
            (ArmPreflight Preflight, byte[]? Control, string? Diagnostic, IReadOnlyList<string> Builds, IReadOnlyList<string> Outputs) flight;

            try
            {
                sites =
                [
                    .. arm.Sites.Select(declared => new SiteState(
                        declared,
                        InWorker(worker, declared.Site),
                        _runner._site.Read(InWorker(worker, declared.Site)),
                        _reading.Files.Entries.GetValueOrDefault(declared.Site))),
                ];
                flight = Preflight(arm, worker, sites, graph);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Before anything was built or written: the arm alone is lost, and its worker's copy is as it was.
                return new Observed(ReachedVerdict.Of(
                    LegVerdict.Poisoned,
                    $"a file its pre-flight reads could not be read, so nothing of the arm was built or written: {ex.Message.TrimEnd('.')}"));
            }

            var (preflight, control, diagnostic, builds, outputs) = flight;
            var observation = new ArmObservation(preflight);

            if (ArmJudge.Judge(arm, observation) is { } refused)
            {
                return new Observed(Held(arm, refused));
            }

            ReachedVerdict? failure = null;
            HarnessException? refusal = null;
            JUnitReport? report = null;
            string? restored;

            try
            {
                var project = _subject.Project.Retargeted(builds, outputs);
                var before = _runner._builder.ReadLog(buildDirectory);

                await WriteAsync([.. sites.Select(state => (state, state.Mutated!))], buildDirectory, cancellationToken).ConfigureAwait(false);

                var build = Noted(
                    $"the build of arm '{arm.Id}'",
                    await _runner._builder
                        .BuildAsync(_config, Request(number, worker, $"{MutationRecords.ArmsDirectory}/{arm.Id}", project), cancellationToken)
                        .ConfigureAwait(false));

                observation = observation with { Build = Observe(build, before, buildDirectory, preflight.Dependents, graph) };

                if (ArmJudge.Judge(arm, observation) is null && ArmJudge.Next(arm, observation) == ArmStep.Control)
                {
                    // The paired control replaces a text of the site as it was, never as the mutation left it.
                    var controlBefore = _runner._builder.ReadLog(buildDirectory);

                    await WriteAsync([(sites[0], control!)], buildDirectory, cancellationToken).ConfigureAwait(false);

                    var controlBuild = Noted(
                        $"the build of the paired control of arm '{arm.Id}'",
                        await _runner._builder
                            .BuildAsync(_config, Request(number, worker, $"{MutationRecords.ArmsDirectory}/{arm.Id}/{MutationRecords.PairedControlDirectory}", project), cancellationToken)
                            .ConfigureAwait(false));

                    observation = observation with { Control = Observe(controlBuild, controlBefore, buildDirectory, preflight.Dependents, graph) };
                }
                else if (ArmJudge.Judge(arm, observation) is null)
                {
                    // An arm whose runner builds no program is refused by its pre-flight, so one that reaches its run has
                    // the unmutated run that bounds it. Were the two ever to disagree, the arm says so, as a defect:
                    // never a run with no bound, nor one stopped the moment it starts.
                    var within = bound
                        ?? throw new InvalidOperationException($"no unmutated run of '{arm.Runner}' bounds the run of arm '{arm.Id}'");

                    var run = Noted(
                        $"the run of arm '{arm.Id}'",
                        "which its bound is held against",
                        await _runner._tests
                            .RunAsync(RunRequest($"{_leg.Name}/{MutationRecords.ArmsDirectory}/{arm.Id}", worker, buildDirectory, graph.ProgramOf(arm.Runner).Path!, records, within, diagnostic), cancellationToken)
                            .ConfigureAwait(false));

                    report = run.Run.Report.Read;
                    observation = observation with { Run = run.Run };
                }
            }
            catch (Exception ex) when (Stops(ex, cancellationToken))
            {
                failure = ReachedVerdict.Of(LegVerdict.Stopped, "the sweep was stopped while it was driven, and each site was put back as it was");
            }
            catch (HarnessException ex) when (EndsTheRun(ex))
            {
                // Ends the sweep, as it ends the run, once the arm's sites are back and its line is kept.
                refusal = ex;
                failure = ReachedVerdict.Of(
                    LegVerdict.Stopped,
                    $"a refusal of the run ended the sweep while it was driven, and each site was put back as it was: {ex.Message.TrimEnd('.')}");
            }
            catch (Exception ex)
            {
                // The arm's own: the verdict a refusal that ends nothing names, failed where its cause can be named, and
                // otherwise poisoned - with its worker, whose copy nothing then vouches for, driving no other.
                failure = Failure(ex, "the sweep could not judge this arm");
            }
            finally
            {
                restored = await RestoreAsync(sites, buildDirectory).ConfigureAwait(false);
            }

            // Before anything else the arm came to: stopping a sweep, or ending it, never hides a copy it left mutated.
            if (restored is not null)
            {
                return new Observed(
                    ReachedVerdict.Of(LegVerdict.Poisoned, $"a site could not be put back as it was, so its worker drives no other arm: {restored}"),
                    report,
                    $"a site of arm '{arm.Id}' could not be put back as it was: {restored}",
                    refusal);
            }

            if (failure is not null)
            {
                return new Observed(failure, report, Defect(arm, failure), refusal);
            }

            return new Observed(
                ArmJudge.Judge(arm, observation) is { } judged ? Held(arm, judged) : ReachedVerdict.OrPoisoned(null, $"{_leg.Name}/{arm.Id}"),
                report);
        }

        /// <summary>
        /// Why the worker that drove <paramref name="arm"/> drives no other, where the arm ended <c>poisoned</c> by a
        /// failure nobody named: nothing then vouches for its copy. <see langword="null"/> for any other end.
        /// </summary>
        private static string? Defect(MutationArm arm, ReachedVerdict failure)
            => failure.Verdict == LegVerdict.Poisoned ? $"arm '{arm.Id}' ended in a defect: {failure.Detail}" : null;

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
                // Every text of the site, replaced together as one edit of it: each read, and looked for in the site as
                // the worker's copy holds it, whichever of them another would move.
                var declared = state.Declared.Texts;
                var texts = declared.Select(text => (Before: Text(text.Before, before: true), After: Text(text.After, before: false))).ToList();

                if (state.Pristine is not { } pristine || texts.Any(text => text.Before is null || text.After is null))
                {
                    continue;
                }

                var edits = SiteEdit.ApplyAll(pristine, [.. texts.Select(text => (text.Before!, text.After!))]);

                state.Mutated = edits.Edited;

                for (var index = 0; index < declared.Count; index++)
                {
                    counts.Add(new TextCount(declared[index].Before, state.Declared.Site, edits.Texts[index].Occurrences));

                    if (edits.Texts[index].ChangesNothing)
                    {
                        unchanged.Add(new UnchangedSite(declared[index].Before, declared[index].After, state.Declared.Site));
                    }
                }

                problems.AddRange(edits.Overlapping.Select(pair =>
                    $"the text in '{declared[pair.Later].Before}' overlaps the text in '{declared[pair.Earlier].Before}' in '{state.Declared.Site}', "
                    + "where the texts of one file are replaced together and no two share a byte of it"));
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
                return PristineOutcome.NothingToControl;
            }

            _work.Progress($"the unmutated {runner}, on worker {number}");

            try
            {
                var build = Noted(
                    $"the build of the unmutated {runner}",
                    await _runner._builder
                        .BuildAsync(_config, Request(number, worker, $"{MutationRecords.ControlsDirectory}/{runner}", _subject.Project.Retargeted([runner], graph.OutputsOf(runner))), cancellationToken)
                        .ConfigureAwait(false));
                var observed = new ArmBuild(build.Verdict, [], []);

                if (build.Verdict.Verdict != LegVerdict.Passed)
                {
                    return Decided(PristineJudge.Judge(runner, observed, null, TimeSpan.Zero, _subject.Settings.RunTimeFactor));
                }

                var run = Noted(
                    $"the run of the unmutated {runner}",
                    "which bounds the run of each of its arms",
                    await _runner._tests
                        .RunAsync(RunRequest($"{_leg.Name}/{MutationRecords.ControlsDirectory}/{runner}", worker, buildDirectory, path, records, null, null), cancellationToken)
                        .ConfigureAwait(false));

                return Decided(PristineJudge.Judge(runner, observed, run.Run, run.Duration, _subject.Settings.RunTimeFactor));
            }
            catch (Exception ex) when (!Stops(ex, cancellationToken) && !EndsTheRun(ex))
            {
                // The leg's own, as a build of the leg that could not run would be, whatever kept it: only this binary's
                // arms are stopped, and every other binary's are still driven.
                var could = $"the unmutated {runner} could not be built and run";
                var failure = Failure(ex, could);

                return Decided(PristineOutcome.Stopped(
                    failure.Verdict == LegVerdict.Poisoned ? failure : failure with { Detail = $"{could}: {failure.Detail}" },
                    $"{could}, so nothing could tell what a mutation of it changed"));
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
        /// <see langword="null"/> where nothing is. Never stopped: a sweep stopped part way still puts its sites back. Each
        /// site is put back and checked on its own, so one that cannot be written or read keeps no other from it.
        /// </summary>
        /// <param name="sites">The sites of an arm past its pre-flight: each a file the worker held, as the reading spells it.</param>
        /// <param name="buildDirectory">The worker's build directory.</param>
        private async Task<string?> RestoreAsync(IReadOnlyList<SiteState> sites, string buildDirectory)
        {
            var problems = new List<string>();
            var touched = sites.Where(state => state.Touched).ToList();

            if (touched.Count > 0)
            {
                var stamp = _runner._site.Stamp(buildDirectory);

                foreach (var state in touched)
                {
                    try
                    {
                        await _runner._site.WriteAsync(state.Path, state.Pristine!, stamp, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HarnessException)
                    {
                        problems.Add($"writing '{state.Declared.Site}' back failed: {ex.Message.TrimEnd('.')}");
                    }
                }
            }

            foreach (var state in sites)
            {
                byte[]? held;

                try
                {
                    held = _runner._site.Read(state.Path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    problems.Add($"'{state.Declared.Site}' could not be read back: {ex.Message.TrimEnd('.')}");
                    continue;
                }

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
        /// <remarks>
        /// Configured with the dependency sources the worker has, beneath what the project sets itself: never those in the
        /// leg's own build directory, which a clean of the leg removes and a build of it may fetch again meanwhile. What an
        /// earlier sweep's configure gave the worker's build directory is removed from its cache first, so it holds
        /// nothing a sweep no longer gives.
        /// </remarks>
        private BuildRequest Request(int number, string worker, string records, ProjectConfig project)
            => _leg.BuildRequestFor(
                _config,
                _work.RunDirectory,
                project.WithCacheVarsBeneath(FetchedSources.CacheVarsFor(_given, source => Kept(worker, source))),
                _work.Time) with
            {
                Leg = $"{_leg.Name}/{records}",
                TreeRoot = worker,
                UnsetFirst = FetchedSources.Unset,
            };

        /// <summary>
        /// A whole run of <paramref name="program"/>, built in <paramref name="buildDirectory"/>, as the subject's tests start:
        /// the leg's, or where the subject has none, in the worker with what the leg's host gives it.
        /// </summary>
        private ArmRunRequest RunRequest(string named, string worker, string buildDirectory, string program, string records, RunBound? bound, string? diagnostic)
        {
            var invocation = _subject.Tests is { } settings ? TestInvocationResolver.InvocationFor(settings, _leg.Host.Os ?? _leg.Leg.Os) : null;

            // What the worker's own build identified, where the invocation names a compiler: the worker is what runs.
            var paths = new LegPaths(worker, buildDirectory)
            {
                Identity = _leg.IdentityFor(_work.RunId.Value),
                Compilers = _runner._toolchains.NamedFor(_subject.Project, buildDirectory),
            };

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
                Diagnostic = diagnostic,
            };
        }

        /// <summary>Worker <paramref name="number"/>'s copy.</summary>
        private string Worker(int number) => _subject.Workers.PathOf(number);

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

            // Beside the verdict and never in it, as a build's and a test's are; in one order, whichever worker met each.
            return _leg.Entry(verdict, string.Join("; ", detail)) with { Arms = ordered, TimingNotes = [.. _timing.Order(StringComparer.Ordinal)] };
        }

        /// <summary>
        /// <paramref name="build"/>, what it says of its own timings kept for the leg's line as <paramref name="of"/>'s: the
        /// same notes whichever command built (<see cref="BuildResult.Notes"/>) - rebuilt from clean, a phase that spanned
        /// a clock step.
        /// </summary>
        private BuildResult Noted(string of, BuildResult build)
        {
            foreach (var note in build.Notes)
            {
                _timing.Enqueue($"{of}: {note}");
            }

            return build;
        }

        /// <summary>
        /// <paramref name="run"/>, kept for the leg's line as <paramref name="of"/> where its clocks disagreed past the
        /// tolerance: its duration is then suspect, and <paramref name="resting"/> says what rests on that duration.
        /// </summary>
        private ArmRunResult Noted(string of, string resting, ArmRunResult run)
        {
            if (run.SteppedBy is { } drift)
            {
                _timing.Enqueue($"{of} spanned a clock step or a host sleep (wall and monotonic time disagreed by {drift}), so its duration, {resting}, is suspect");
            }

            return run;
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

    /// <summary>Whether <paramref name="exception"/> is the sweep being stopped: a cancellation, with <paramref name="cancellationToken"/> cancelled.</summary>
    private static bool Stops(Exception exception, CancellationToken cancellationToken)
        => exception is OperationCanceledException && cancellationToken.IsCancellationRequested;

    /// <summary>Whether <paramref name="exception"/> is a refusal of the run, which ends the sweep as it ends any leg's work.</summary>
    private static bool EndsTheRun(Exception exception)
        => exception is HarnessException refusal && HarnessExit.RefusesTheRun(refusal.ExitCode);

    /// <summary>
    /// The verdict a failure that neither stops the sweep nor refuses the run comes to, read where a leg whose own work
    /// ends in it reads it (<see cref="Verdicts.ForFailure"/>): the verdict its refusal names, <c>unmeasured</c> where a
    /// phase's output could not be read back, <c>failed</c> where its cause is one this build can name; and otherwise
    /// <c>poisoned</c> - nobody named it - saying <paramref name="unnamed"/>, then its type and what it said.
    /// </summary>
    /// <param name="exception">What was raised.</param>
    /// <param name="unnamed">What the sweep could not do, as a line begins where nothing named why.</param>
    private static ReachedVerdict Failure(Exception exception, string unnamed)
        => Verdicts.ForFailure(exception)
            ?? ReachedVerdict.Of(LegVerdict.Poisoned, $"{unnamed}, {exception.GetType().Name}: {exception.Message.TrimEnd('.')}");

    /// <summary>Where <paramref name="cited"/>, as a row spells it, is in <paramref name="worker"/>'s copy.</summary>
    private static string InWorker(string worker, string cited) => Path.Combine(worker, cited.Replace('/', Path.DirectorySeparatorChar));
}
