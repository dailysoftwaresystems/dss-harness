namespace RepoHarness.Core.Configuration;

/// <summary>
/// How heavy legs are admitted onto one physical machine: at most <see cref="HeavyLegs"/> at once across every
/// DssHarness this user runs there, each started only once the machine's memory in use is below
/// <see cref="MaxMemoryPercent"/> and, where its build's need is known, only where that need fits beside the room the
/// other admitted legs claim. Declared under <c>defaults</c>, or under a host that is a machine of its own - this one, or
/// an ssh host - whose fields replace the defaults' one by one. A machine where neither declares one admits every leg at
/// once.
/// </summary>
/// <remarks>
/// For a machine that several separate commands build on at once - worktrees each running a gate of their own -
/// where <c>maxParallelLegs</c>, which one command counts, cannot see the others: four such builds drove one
/// machine's committed memory to 81 of 113.7 GiB, and the process that had started them died. What a command's own
/// configuration declares is the rule its legs are admitted by: a repository that declares none never joins the line,
/// and where the commands of repositories that declare different counts share a machine, each leg starts only while
/// every count up to it in line allows it.
/// </remarks>
public sealed class AdmissionSettings
{
    /// <summary>Heavy legs a machine runs at once when a section says nothing.</summary>
    public const int DefaultHeavyLegs = 2;

    /// <summary>The memory in use, in percent, a heavy leg starts below when a section says nothing.</summary>
    public const double DefaultMaxMemoryPercent = 76;

    /// <summary>The least seconds a settle lasts when a section says nothing.</summary>
    public const int DefaultSettleLeastSeconds = 15;

    /// <summary>The most seconds a settle lasts when a section says nothing.</summary>
    public const int DefaultSettleMostSeconds = 90;

    /// <summary>The seconds a settle lasts when a section says nothing: at least the first, at most the second.</summary>
    public static IReadOnlyList<int> DefaultSettleSeconds { get; } = [DefaultSettleLeastSeconds, DefaultSettleMostSeconds];

    /// <summary>Seconds between looks at the slots, the memory and the room when a section says nothing.</summary>
    public const int DefaultPollSeconds = 30;

    /// <summary>Minutes a leg waits to be admitted, when a section says nothing, before it is not.</summary>
    public const double DefaultMaxWaitMinutes = 60;

    /// <summary>The most seconds a settle or a poll may last: an hour, which no machine's wait needs more than.</summary>
    public const int MostSeconds = 3600;

    /// <summary>The most minutes a leg may wait to be admitted: a week.</summary>
    public const double MostWaitMinutes = 10080;

    /// <summary>
    /// Heavy legs the machine runs at once, across every command this user runs there, at least one: each takes a slot
    /// before it starts, in the order they asked, and gives it back when its work ends.
    /// </summary>
    public int? HeavyLegs { get; init; }

    /// <summary>
    /// The memory in use, in percent of what the machine can give, a leg holding a slot starts below - above 0, and at
    /// most 100: on Windows its commit charge against its commit limit, on Linux what the kernel counts as not
    /// available, and on macOS what it counts as not free.
    /// </summary>
    public double? MaxMemoryPercent { get; init; }

    /// <summary>
    /// The least and the most seconds, as <c>[least, most]</c>, a leg waits once the memory is below the limit - a
    /// time picked at random between them - before it looks again and starts only if it still is; so two legs taking
    /// their slots together do not both start on one reading. Skipped where no other leg holds a slot there, and by
    /// <c>[0, 0]</c>.
    /// </summary>
    public List<int>? SettleSeconds { get; init; }

    /// <summary>Seconds between looks at the slots, the memory and the room while a leg waits, at least one.</summary>
    public int? PollSeconds { get; init; }

    /// <summary>
    /// Minutes a leg waits - for a slot, then the memory, then room for its build where its need is known - before it is
    /// reported <c>not-admitted</c>, naming what held the slots, the memory in use it waited on, or the room and the legs
    /// that claimed it, and nothing of it runs.
    /// </summary>
    public double? MaxWaitMinutes { get; init; }

    /// <summary>
    /// The rule a machine admits heavy legs by: each field its own section declares, or else the defaults' section's,
    /// or else the built-in value; <see langword="null"/> where neither section is declared, and every leg is
    /// admitted at once.
    /// </summary>
    /// <param name="machine">The machine's own section, or <see langword="null"/>.</param>
    /// <param name="defaults">The <c>defaults</c> section, or <see langword="null"/>.</param>
    /// <exception cref="ConfigException">
    /// The rule is one no machine could admit by: checked by the rules a configuration file is, so a rule built from
    /// sections no file was read into is refused rather than waited by.
    /// </exception>
    public static AdmissionRule? RuleFor(AdmissionSettings? machine, AdmissionSettings? defaults)
    {
        if (machine is null && defaults is null)
        {
            return null;
        }

        var merged = new AdmissionSettings
        {
            HeavyLegs = machine?.HeavyLegs ?? defaults?.HeavyLegs ?? DefaultHeavyLegs,
            MaxMemoryPercent = machine?.MaxMemoryPercent ?? defaults?.MaxMemoryPercent ?? DefaultMaxMemoryPercent,
            SettleSeconds = machine?.SettleSeconds ?? defaults?.SettleSeconds ?? [.. DefaultSettleSeconds],
            PollSeconds = machine?.PollSeconds ?? defaults?.PollSeconds ?? DefaultPollSeconds,
            MaxWaitMinutes = machine?.MaxWaitMinutes ?? defaults?.MaxWaitMinutes ?? DefaultMaxWaitMinutes,
        };

        var problems = new List<string>();

        HarnessConfigValidator.ValidateAdmission(merged, "admission", problems);

        if (problems.Count > 0)
        {
            throw new ConfigException($"The heavy-leg admission rule has {problems.Count} problem(s): {string.Join("; ", problems)}");
        }

        return new AdmissionRule(
            merged.HeavyLegs.Value,
            merged.MaxMemoryPercent.Value,
            TimeSpan.FromSeconds(merged.SettleSeconds[0]),
            TimeSpan.FromSeconds(merged.SettleSeconds[1]),
            TimeSpan.FromSeconds(merged.PollSeconds.Value),
            TimeSpan.FromMinutes(merged.MaxWaitMinutes.Value));
    }
}

/// <summary>The rule one machine admits heavy legs by, every field decided and within its bounds.</summary>
/// <param name="HeavyLegs">Heavy legs the machine runs at once, across every command.</param>
/// <param name="MaxMemoryPercent">The memory in use a leg holding a slot starts below.</param>
/// <param name="SettleLeast">The least a settle lasts.</param>
/// <param name="SettleMost">The most a settle lasts.</param>
/// <param name="Poll">The time between looks while a leg waits.</param>
/// <param name="MaxWait">How long a leg waits before it is not admitted.</param>
public sealed record AdmissionRule(
    int HeavyLegs,
    double MaxMemoryPercent,
    TimeSpan SettleLeast,
    TimeSpan SettleMost,
    TimeSpan Poll,
    TimeSpan MaxWait);
