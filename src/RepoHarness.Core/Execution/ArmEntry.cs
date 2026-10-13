namespace RepoHarness.Core.Execution;

/// <summary>
/// One mutation arm's line beneath its leg's: the verdict the arm reached on that leg, why, and what its run measured
/// against what it declares.
/// </summary>
/// <remarks>
/// A leg's verdict is the worst of its own and its arms', so the leg's line says the sweep's worst; this says which arm,
/// and why. Each arm a leg was asked about has one, an arm the sweep did not select among them, so a sweep that left an
/// arm out is never read as having driven it.
/// </remarks>
public sealed record ArmEntry
{
    /// <summary>The arm, as the registry names it.</summary>
    public required string Arm { get; init; }

    /// <summary>The verdict it reached.</summary>
    public required LegVerdict Verdict { get; init; }

    /// <summary>Why, in the words its line shows.</summary>
    public string Detail { get; init; } = string.Empty;

    /// <summary>How long it took, from its admission to its record; zero for an arm no worker drove.</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>
    /// The worker it was given to, by number - one that drove it, or one whose machine did not admit it - or
    /// <see langword="null"/> where none drove it or was refused it: an arm no worker reached, or one a worker took and
    /// then stopped for its binary's control, which did not pass.
    /// </summary>
    public int? Worker { get; init; }

    /// <summary>
    /// How many cases its run ran, skipped ones included, or <see langword="null"/> where no report of a run was read:
    /// an arm that ran nothing, or one whose report is missing or cannot be read.
    /// </summary>
    public int? Cases { get; init; }

    /// <summary>How many cases it declares its run runs.</summary>
    public int DeclaredCases { get; init; }

    /// <summary>
    /// The cases its run reddened - none, where its report names none - or <see langword="null"/> where no report of a
    /// run was read.
    /// </summary>
    public IReadOnlyList<string>? Reds { get; init; }

    /// <summary>The cases it declares its mutation reddens.</summary>
    public IReadOnlyList<string> DeclaredReds { get; init; } = [];

    /// <summary>
    /// Where its records are - its builds' logs, its run's log and report, and its <c>arm.json</c> - or
    /// <see langword="null"/> where it has none: no worker reached it, or the one that took it stopped it for its
    /// binary's control before anything of its own was built or run.
    /// </summary>
    public string? Records { get; init; }

    /// <summary>
    /// The digest of what it was judged on (<see cref="Mutations.JudgedOn"/>), as its leg's sweep read that from the tree
    /// and from the build of the first worker made; <see langword="null"/> where no worker was made, where a part of it
    /// could not be read, where nothing of the arm was judged, and for every arm of a self-test, which nothing keeps.
    /// </summary>
    public string? Key { get; init; }

    /// <summary>
    /// The recorded <c>passed</c> that holds, for an arm <c>--pending</c> left out because its key is that passed's key: the
    /// run that reached it and when. <see langword="null"/> for every other arm.
    /// </summary>
    public ArmHeld? Held { get; init; }

    /// <summary>
    /// Why <c>--pending</c> swept the arm - the first reason it was pending, as data names it and as a line says it - or
    /// <see langword="null"/> for an arm a sweep without it drove, one it held, and one it never weighed.
    /// </summary>
    public ArmPending? Pending { get; init; }
}

/// <summary>The recorded <c>passed</c> an arm <c>--pending</c> left out holds by: the run that reached it, and when.</summary>
/// <param name="Run">The run's id.</param>
/// <param name="At">When its arm ended, as the clock of the machine that swept it said.</param>
public sealed record ArmHeld(string Run, DateTimeOffset At);

/// <summary>Why <c>--pending</c> swept an arm: the first reason it was pending.</summary>
/// <param name="Code">The reason, as data names it: <c>files-moved</c>.</param>
/// <param name="Said">The reason, as a line says it.</param>
public sealed record ArmPending(string Code, string Said);
