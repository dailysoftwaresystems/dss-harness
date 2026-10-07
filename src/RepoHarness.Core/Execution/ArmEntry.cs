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

    /// <summary>The worker that drove it, by number, or <see langword="null"/> where none did.</summary>
    public int? Worker { get; init; }

    /// <summary>How many cases its run ran, skipped ones included, or <see langword="null"/> where its report said none.</summary>
    public int? Cases { get; init; }

    /// <summary>How many cases it declares its run runs.</summary>
    public int DeclaredCases { get; init; }

    /// <summary>The cases its run reddened, or <see langword="null"/> where its report said none.</summary>
    public IReadOnlyList<string>? Reds { get; init; }

    /// <summary>The cases it declares its mutation reddens.</summary>
    public IReadOnlyList<string> DeclaredReds { get; init; } = [];

    /// <summary>
    /// Where its records are - its builds' logs, its run's log and report, and its <c>arm.json</c> - or
    /// <see langword="null"/> where it has none: no worker drove it.
    /// </summary>
    public string? Records { get; init; }
}
