using DssHarness.Core.Configuration;
using DssHarness.Core.Execution;
using DssHarness.Core.Hosts;
using DssHarness.Core.Legs;
using DssHarness.Core.Processes;
using DssHarness.Core.Results;
using DssHarness.Core.Runners;
using DssHarness.Core.Runs;
using DssHarness.Core.Testing;
using DssHarness.Core.Worktrees;

namespace DssHarness.Core.SetUps;

/// <summary>
/// The set-ups a leg brings up around its work, where its run is asked to set up (<see cref="Option"/>): the refusals
/// of a run that cannot have them, and the bringing up and down.
/// </summary>
/// <remarks>
/// <para>
/// A test invocation (<c>test</c>) or a runner (<c>run</c>) names its set-ups under <c>setUp</c>, and a run brings them
/// up only when it says <see cref="Option"/>: never otherwise, so a run that names one runs exactly as it did until it is
/// asked to. Which a leg brings up is <see cref="LegWorkload.SetUpsOf"/>'s to say.
/// </para>
/// <para>
/// Each runs on the machine that runs the leg's work, in the leg's own tree there - a copy, a host's copy, or the tree
/// itself - before its work starts and after it ends, whatever its verdict. Legs sharing a set-up share it
/// (<see cref="SetUpHolds"/>): the first brings it up, the last brings it down. Its lines run as a phase of the leg does,
/// with the leg's environment, its log among the leg's records.
/// </para>
/// </remarks>
/// <param name="setUps">Reads what a set-up's line starts.</param>
/// <param name="holds">Shares a set-up among the legs that use it.</param>
/// <param name="phases">Runs a set-up's line as a phase of the leg.</param>
public sealed class LegSetUps(ISetUpService setUps, SetUpHolds holds, PhaseRunner phases)
{
    /// <summary>The option that asks a run to set up.</summary>
    public const string Option = "--set-up";

    /// <summary>The commands that take <see cref="Option"/>: the ones whose legs run what names a set-up.</summary>
    public static IReadOnlySet<string> Commands { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        TestService.CommandName,
        RunnerRunService.CommandName,
    };

    private readonly ISetUpService _setUps = setUps;
    private readonly SetUpHolds _holds = holds;
    private readonly PhaseRunner _phases = phases;

    /// <summary>
    /// How long the command line waits for a run interrupted while its legs hold set-ups, as it waits for an interrupted
    /// deletion: what came up comes down within it (<see cref="PointOfNoReturn.StoppedAfter"/>). Tests shorten it.
    /// </summary>
    internal TimeSpan InterruptionGrace { get; init; } = WorktreeService.DefaultInterruptionGrace;

    /// <summary>
    /// Refuses a run asked to set up, before any host is measured, where nothing its legs run names a set-up, where a leg
    /// names one set-up twice with other values, or where a line a leg would run cannot start as written on its system:
    /// no line for it, or a program the configuration does not allow.
    /// </summary>
    /// <param name="config">The configuration.</param>
    /// <param name="treeRoot">The tree the run was asked from, whose root a program that is a path is held inside.</param>
    /// <param name="legs">The legs the run selected.</param>
    /// <param name="workload">What the command has each leg do.</param>
    /// <exception cref="HarnessException">
    /// Nothing names a set-up (<see cref="HarnessExit.UsageError"/>: the option would change nothing while the command
    /// line says it did), or a line cannot run (<see cref="HarnessExit.Refused"/>, naming every one): asked of every
    /// line a leg would run, both ways, so none is found out after its leg's tree was synced, its lock taken and its
    /// earlier set-ups brought up.
    /// </exception>
    public void Require(HarnessConfig config, string treeRoot, IReadOnlyList<SelectedLeg> legs, LegWorkload workload)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(legs);
        ArgumentNullException.ThrowIfNull(workload);

        var named = legs.Select(selected => (Selected: selected, Uses: workload.SetUpsOf(config, selected.Leg))).ToList();

        if (named.All(entry => entry.Uses.Count == 0))
        {
            throw new HarnessException(
                HarnessExit.UsageError,
                $"{Option} was given, but nothing the selected legs run names a set-up: a test invocation's or a runner's "
                + "setUp names the ones a leg brings up. Nothing was run.");
        }

        var problems = new List<string>();

        foreach (var (selected, uses) in named)
        {
            foreach (var twice in uses.GroupBy(use => use.Name, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1))
            {
                problems.Add($"leg '{selected.Name}' names set-up '{twice.Key}' with other values: {string.Join(" and ", twice.Select(use => $"'{use}'"))}");
            }

            foreach (var use in uses.DistinctBy(use => use.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (Values(config, use, out var values) is { } unvalued)
                {
                    problems.Add($"leg '{selected.Name}': {unvalued}");
                    continue;
                }

                foreach (var direction in Enum.GetValues<SetUpDirection>())
                {
                    try
                    {
                        _ = _setUps.Resolve(config, use.Name, values, direction, selected.Leg.Os, treeRoot);
                    }
                    catch (HarnessException ex)
                    {
                        problems.Add($"leg '{selected.Name}': {ex.Message.Replace(" Nothing was run.", string.Empty, StringComparison.Ordinal).TrimEnd('.')}");
                    }
                }
            }
        }

        if (problems.Count > 0)
        {
            throw new HarnessException(HarnessExit.Refused, $"{string.Join("; ", problems.Distinct(StringComparer.Ordinal))}. Nothing was run.");
        }
    }

    /// <summary>
    /// Brings each of <paramref name="uses"/> up for <paramref name="leg"/>, runs its <paramref name="work"/>, and lets
    /// each go again - bringing it down where no other leg still uses it - however the work ended: with a verdict, an
    /// exception, or stopped.
    /// </summary>
    /// <param name="config">The configuration of the tree the leg works in, which declares the set-ups.</param>
    /// <param name="leg">The leg, as it works on this machine.</param>
    /// <param name="uses">The set-ups it brings up, as <see cref="LegWorkload.SetUpsOf"/> names them.</param>
    /// <param name="runId">Its run.</param>
    /// <param name="runDirectory">Where its records are kept.</param>
    /// <param name="repository">
    /// The repository the leg's tree is of, as its main checkout is spelt on this machine: a set-up the machine shares is
    /// shared by the legs of that repository alone (<see cref="SetUpSharing"/>).
    /// </param>
    /// <param name="progress">Where what the leg does with its set-ups is said, as it does it.</param>
    /// <param name="failed">The leg's entry, ended with a verdict and why, where a set-up did not come up.</param>
    /// <param name="ended">
    /// Told what the leg did with each set-up it reached once it let every one go, whatever ended its work - so its
    /// ledger says it on the leg's line, or, where the leg ends with no line, in the run's own. A set-up held but never
    /// tried - one named after a set-up that did not come up - did nothing, and is not told.
    /// </param>
    /// <param name="work">The leg's work.</param>
    /// <param name="cancellationToken">
    /// Stops the work. What came up is brought down all the same, until a little before the command line stops waiting,
    /// counted from the interruption, so each set-up still coming down then is said as not brought down.
    /// </param>
    /// <returns>The work's entry, or the failure's.</returns>
    /// <remarks>
    /// Every set-up is held before any comes up, in the order of their names, then each comes up in the order named:
    /// two legs naming two set-ups in opposite orders, with other values, would otherwise each hold one and wait for the
    /// other for ever.
    /// </remarks>
    public async Task<LegEntry> AroundAsync(
        HarnessConfig config,
        PlacedLeg leg,
        IReadOnlyList<SetUpUse> uses,
        RunId runId,
        string runDirectory,
        string repository,
        Action<string> progress,
        Func<LegVerdict, string, LegEntry> failed,
        Action<IReadOnlyList<SetUpFact>> ended,
        Func<CancellationToken, Task<LegEntry>> work,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(leg);
        ArgumentNullException.ThrowIfNull(uses);
        ArgumentNullException.ThrowIfNull(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(repository);
        ArgumentNullException.ThrowIfNull(progress);
        ArgumentNullException.ThrowIfNull(failed);
        ArgumentNullException.ThrowIfNull(ended);
        ArgumentNullException.ThrowIfNull(work);

        var tree = leg.HostTreeRoot;

        // Each in the order named: what the leg did with it, filled as the leg reaches it, and its hold, as it is held.
        var facts = new SetUpFact?[uses.Count];
        var held = new Held?[uses.Count];

        // From the interruption on, not from when the work it stopped lets go: the command line counts from it too.
        using var stopping = new CancellationTokenSource();
        using var interrupted = cancellationToken.Register(() => stopping.CancelAfter(PointOfNoReturn.StoppedAfter(InterruptionGrace)));

        try
        {
            foreach (var index in Enumerable.Range(0, uses.Count).OrderBy(index => uses[index].Name, StringComparer.OrdinalIgnoreCase))
            {
                var use = uses[index];

                // The run was refused for any the configuration it was asked from cannot give; the one a leg's tree holds
                // of its own may be older.
                if (Values(config, use, out var values) is { } unvalued)
                {
                    return failed(LegVerdict.Failed, $"set-up '{use.Name}' was not brought up: {unvalued}");
                }

                var sharing = new SetUpSharing(repository, config.SetUp.GetValueOrDefault(use.Name)?.MachineWide ?? false);
                var turn = await _holds.TakeAsync(use.Name, values, tree, runId.Value, leg.Name, progress, cancellationToken, sharing).ConfigureAwait(false);

                // One joined is the leg's to let go of, and maybe to bring down, from now: its fact goes with it.
                facts[index] = turn == SetUpTurn.Joined ? new SetUpFact(use.Name, SetUpFact.Joined, Inputs: values) : null;
                held[index] = new Held(use.Name, values, index, Joined: turn == SetUpTurn.Joined) { Started = turn == SetUpTurn.Joined };
            }

            foreach (var taken in held.OfType<Held>())
            {
                if (taken.Joined)
                {
                    progress($"set-up '{taken.Name}'{SetUpValues.Said(taken.Values)} is up already, for another leg; using it");
                    continue;
                }

                // Its fact first, so whatever stops what follows - a stop, a line that will not start - leaves the leg
                // something to say of it as it lets it go.
                facts[taken.Fact] = new SetUpFact(taken.Name, SetUpFact.Failed, Inputs: taken.Values) { Detail = "stopped before it came up" };
                progress($"bringing set-up '{taken.Name}'{SetUpValues.Said(taken.Values)} up");

                var ran = await RunAsync(config, leg, taken, SetUpDirection.Up, runDirectory, () => taken.Started = true, cancellationToken).ConfigureAwait(false);

                if (ran is { } why)
                {
                    facts[taken.Fact] = facts[taken.Fact]! with { Detail = why };

                    return failed(LegVerdict.Failed, $"set-up '{taken.Name}' did not come up: {why}");
                }

                _holds.Stood(taken.Name, tree, runId.Value, leg.Name);
                facts[taken.Fact] = facts[taken.Fact]! with { Up = SetUpFact.BroughtUp, Detail = null };
            }

            return await work(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                await LetGoAsync(config, leg, runId, runDirectory, [.. held.OfType<Held>()], facts, progress, stopping.Token).ConfigureAwait(false);
            }
            finally
            {
                ended([.. facts.OfType<SetUpFact>()]);
            }
        }
    }

    /// <summary>
    /// Lets each of <paramref name="held"/> go, in the reverse of the order named - one set-up may stand on another -
    /// bringing down each this leg is the last to use and whose up line started; never throwing, so no set-up is left
    /// unreleased for one that could not be. A down line still running when <paramref name="stopping"/> ends is
    /// stopped, and its set-up said as not brought down.
    /// </summary>
    private async Task LetGoAsync(
        HarnessConfig config,
        PlacedLeg leg,
        RunId runId,
        string runDirectory,
        IReadOnlyList<Held> held,
        SetUpFact?[] facts,
        Action<string> progress,
        CancellationToken stopping)
    {
        var tree = leg.HostTreeRoot;

        foreach (var taken in Enumerable.Reverse(held))
        {
            // An up line that never started brought nothing up, and has nothing to bring down.
            if (!taken.Started)
            {
                _holds.Gone(taken.Name, tree, runId.Value, leg.Name);
                continue;
            }

            // Joined, or its up line reached: either way its fact was filled.
            var fact = facts[taken.Fact]!;

            bool last;

            try
            {
                last = _holds.Leave(taken.Name, tree, runId.Value, leg.Name);
            }
            catch (HarnessException ex)
            {
                // Whether another leg still uses it cannot be told, so it is not brought down under one that might.
                facts[taken.Fact] = fact with { Down = SetUpFact.Failed, Detail = Joined(fact.Detail, $"whether another leg still used it could not be read: {ex.Message}") };
                progress($"set-up '{taken.Name}' was not brought down: {ex.Message} {SetUpFact.Remedy(taken.Name, taken.Values)}");
                _holds.Gone(taken.Name, tree, runId.Value, leg.Name);
                continue;
            }

            if (!last)
            {
                progress($"leaving set-up '{taken.Name}' up for the other legs that use it");
                facts[taken.Fact] = fact with { Down = SetUpFact.LeftUp };
                continue;
            }

            progress($"bringing set-up '{taken.Name}' down");

            var why = await RunAsync(config, leg, taken, SetUpDirection.Down, runDirectory, started: null, stopping).ConfigureAwait(false);

            facts[taken.Fact] = why is null
                ? fact with { Down = SetUpFact.BroughtDown }
                : fact with { Down = SetUpFact.Failed, Detail = Joined(fact.Detail, why) };

            if (why is not null)
            {
                progress($"set-up '{taken.Name}' did not come down: {why}; what it brought up may still run - {SetUpFact.Remedy(taken.Name, taken.Values)}");
            }

            _holds.Gone(taken.Name, tree, runId.Value, leg.Name);
        }
    }

    /// <summary>
    /// Runs one of a set-up's lines as a phase of <paramref name="leg"/>: why it did not pass, or <see langword="null"/>
    /// where it did. A line that cannot be resolved or started is a reason, never an exception, and so is one stopped
    /// while it brings the set-up down; one stopped while it brings it up stops the leg. <c>started</c> is told just
    /// before the line's program is asked to start.
    /// </summary>
    private async Task<string?> RunAsync(
        HarnessConfig config,
        PlacedLeg leg,
        Held taken,
        SetUpDirection direction,
        string runDirectory,
        Action? started,
        CancellationToken cancellationToken)
    {
        SetUpCommandLine line;

        try
        {
            line = _setUps.Resolve(config, taken.Name, taken.Values, direction, leg.Os, leg.HostTreeRoot);
        }
        catch (HarnessException ex)
        {
            return ex.Message;
        }

        var phase = $"set-up {taken.Name} {SetUpService.CommandName(direction)}";
        var log = RunnerRunService.LogNameFor(phase);

        try
        {
            started?.Invoke();

            var result = await _phases
                .RunAsync(
                    new PhaseRequest
                    {
                        Leg = leg.Name,
                        Phase = phase,
                        FileName = line.FileName,
                        Arguments = line.Arguments,
                        LogFile = new LegRecords(runDirectory, leg.Name).PhaseLog(log),
                        WorkingDirectory = leg.HostTreeRoot,
                        Environment = PhaseEnvironment.Layered(leg.Environment),
                        AppendToPath = leg.Host.ProgramDirectories,
                        StallSeconds = config.Defaults.StallSeconds,
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            return result.Passed
                ? null
                : $"'{line.Line}' {(result.Stalled ? "stalled" : $"exited {result.ExitCode}")}; its log is '{log}{PhaseLogs.Extension}' among the leg's records";
        }
        catch (ProgramStartException ex)
        {
            // Nothing started, so nothing came up for this line to have brought.
            taken.Started = direction == SetUpDirection.Down && taken.Started;

            return $"'{line.Line}' could not start: {ex.Message}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HarnessException)
        {
            return $"'{line.Line}' could not run: {ex.Message}";
        }
        catch (OperationCanceledException) when (direction == SetUpDirection.Down)
        {
            return "stopped before it came down";
        }
    }

    /// <summary>
    /// The value each input of the set-up <paramref name="use"/> names takes - what it gives, else the default - and why
    /// they cannot be had, or <see langword="null"/> where they can. A set-up <paramref name="config"/> does not declare
    /// is given what the use gives, for its line to be refused by name.
    /// </summary>
    private static string? Values(HarnessConfig config, SetUpUse use, out IReadOnlyDictionary<string, string> values)
    {
        if (!config.SetUp.TryGetValue(use.Name, out var setUp))
        {
            values = use.With;
            return null;
        }

        values = SetUpValues.Resolve(use.Name, setUp, use.With, $"setUp '{use.Name}'", out var problems);

        return problems.Count == 0 ? null : string.Join("; ", problems);
    }

    private static string Joined(string? first, string then) => first is null ? then : $"{first}; {then}";

    /// <summary>
    /// A set-up this leg holds: which, with what values, its fact, whether another leg had it up already, and whether its
    /// up line started.
    /// </summary>
    private sealed record Held(string Name, IReadOnlyDictionary<string, string> Values, int Fact, bool Joined)
    {
        /// <summary>Whether a line of this leg's brought it up, or another leg's: so it is the leg's to bring down.</summary>
        public bool Started { get; set; }
    }
}

/// <summary>What a leg did with one set-up its work named: how it came up for the leg, and how the leg let go of it.</summary>
/// <param name="Name">The set-up, as declared.</param>
/// <param name="Up">How it came up: <see cref="BroughtUp"/>, <see cref="Joined"/> or <see cref="Failed"/>.</param>
/// <param name="Down">How the leg let go of it: <see cref="BroughtDown"/>, <see cref="LeftUp"/> or <see cref="Failed"/>.</param>
/// <param name="Detail">Why one of them failed, where one did.</param>
/// <param name="Inputs">The value each of its inputs was given.</param>
public sealed record SetUpFact(string Name, string Up, string? Down = null, string? Detail = null, IReadOnlyDictionary<string, string>? Inputs = null)
{
    /// <summary>The leg brought it up.</summary>
    public const string BroughtUp = "brought-up";

    /// <summary>Another leg had brought it up, and the leg used it.</summary>
    public const string Joined = "joined";

    /// <summary>Its line did not pass.</summary>
    public const string Failed = "failed";

    /// <summary>The leg was the last to use it, and brought it down.</summary>
    public const string BroughtDown = "brought-down";

    /// <summary>Another leg still used it, and brings it down.</summary>
    public const string LeftUp = "left-for-other-legs";

    /// <summary>Whether the leg brought it up, or used it, and did not bring it down: what it started may still run.</summary>
    public bool LeftRunning => Down == Failed;

    /// <summary>How a set-up left running is brought down, as every line saying so says it.</summary>
    /// <param name="name">The set-up.</param>
    /// <param name="inputs">The values it was brought up with, given again.</param>
    public static string Remedy(string name, IReadOnlyDictionary<string, string>? inputs)
    {
        var given = (inputs ?? new Dictionary<string, string>())
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => $"{pair.Key}={pair.Value}")
            .Select(pair => $" {CommandLineInputs.Option} {(pair.Any(char.IsWhiteSpace) ? $"\"{pair}\"" : pair)}");

        return $"'{ToolPackage.Command} {SetUpService.DownCommand} {name}{string.Concat(given)}', run in the tree the leg worked in, brings it down";
    }

    /// <summary>The fact as the leg's line says it.</summary>
    public string Describe()
        => $"set-up '{Name}'{SetUpValues.Said(Inputs ?? new Dictionary<string, string>())} {Up.Replace('-', ' ')}"
            + (Down is null ? string.Empty : Down == Failed ? ", not brought down" : $", {Down.Replace('-', ' ')}")
            + (Detail is null ? string.Empty : $": {Detail}");
}
