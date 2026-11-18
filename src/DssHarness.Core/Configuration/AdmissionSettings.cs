using DssHarness.Core.FileSystem;
using DssHarness.Core.Output;

namespace DssHarness.Core.Configuration;

/// <summary>
/// How heavy legs are admitted onto one physical machine: at most <see cref="HeavyLegs"/> at once across every
/// DssHarness this user runs there, each started only once the machine's memory in use is below
/// <see cref="MaxMemoryPercent"/> and, where its build's need is known, only where that need fits beside the room the
/// other admitted legs claim. Declared in the machine's own settings file (<see cref="MachineSettingsFile"/>), under
/// <c>defaults</c>, or under a host that is a machine of its own - this one, or an ssh host - each field taken from the
/// first of them that declares it (<see cref="InForce"/>). A machine where none declares one admits every leg at once.
/// </summary>
/// <remarks>
/// For a machine that several separate commands build on at once - worktrees each running a gate of their own -
/// where <c>maxParallelLegs</c>, which one command counts, cannot see the others: four such builds drove one
/// machine's committed memory to 81 of 113.7 GiB, and the process that had started them died. What a command's own
/// configuration declares is the rule its legs are admitted by where the machine's settings file says nothing: a
/// repository that declares none never joins the line, and where the commands of repositories that declare different
/// counts share a machine, each leg starts only while every count up to it in line allows it. config.json is tracked, so
/// each worktree holds the copy of its last commit: a consumer's main checkout raised its machine's count to 3 while
/// agents' worktrees made before still said 2, and every leg of the main checkout waited on "one of this machine's 2
/// heavy-leg slot(s)". What a machine can hold is the machine's, so its settings file wins over every tree's.
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

    /// <summary>The room, in GiB, a heavy leg's build leaves free on each filesystem it fills when a section says nothing.</summary>
    public const double DefaultMinFreeGiB = 2;

    /// <summary>The most room, in GiB, a section may keep free: a tebibyte.</summary>
    public const double MostMinFreeGiB = 1024;

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
    /// The room, in GiB, a heavy leg's build leaves free on each filesystem it fills: read again every 15 seconds while it
    /// builds, and the build stopped once one has less - <c>stopped</c>, what it built left for clean - rather than let it
    /// fill a disk other legs build and test on. 0 stops no build.
    /// </summary>
    public double? MinFreeGiB { get; init; }

    /// <summary>
    /// Whether a heavy leg short of room - waiting for it as it is admitted, or under <see cref="MinFreeGiB"/> as it
    /// builds - first removes the build directories other trees of its repository keep on the same filesystem of its
    /// machine that no run or sweep holds, the least recently built first: none where all of them would not cover what the
    /// disk lacks, and else as many as that takes, one leg at a time on a filesystem. Off where nothing says.
    /// </summary>
    public bool? ReclaimIdleBuilds { get; init; }

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
        => InForce(new AdmissionSection(machine, "the machine's own section"), new AdmissionSection(defaults, "defaults.admission"))?.Rule;

    /// <summary>
    /// The rule a machine admits heavy legs by, and where each of its fields came from: each field from the first of
    /// <paramref name="sections"/> that declares it, or else the built-in value; <see langword="null"/> where none of them
    /// is declared, and every leg is admitted at once.
    /// </summary>
    /// <param name="sections">The sections it may be declared in, the one that wins first.</param>
    /// <exception cref="ConfigException">
    /// The rule is one no machine could admit by: checked by the rules a configuration file is, so a rule built from
    /// sections no file was read into is refused rather than waited by.
    /// </exception>
    public static AdmissionInForce? InForce(params IReadOnlyList<AdmissionSection> sections)
    {
        ArgumentNullException.ThrowIfNull(sections);

        if (sections.All(section => section.Settings is null))
        {
            return null;
        }

        var fields = new List<AdmissionField>();

        // Each field from the first section declaring it, saying which; the built-in value where none does.
        T Taken<T>(string key, Func<AdmissionSettings, T?> read, T builtIn, Func<T, string> said)
            where T : class
        {
            var from = sections.FirstOrDefault(section => section.Settings is { } settings && read(settings) is not null);
            var value = from is null ? builtIn : read(from.Settings!)!;

            fields.Add(new AdmissionField(key, said(value), from?.From ?? BuiltIn));
            return value;
        }

        TValue Value<TValue>(string key, Func<AdmissionSettings, TValue?> read, TValue builtIn, Func<TValue, string> said)
            where TValue : struct
        {
            var from = sections.FirstOrDefault(section => section.Settings is { } settings && read(settings) is not null);
            var value = from is null ? builtIn : read(from.Settings!)!.Value;

            fields.Add(new AdmissionField(key, said(value), from?.From ?? BuiltIn));
            return value;
        }

        var merged = new AdmissionSettings
        {
            HeavyLegs = Value("heavyLegs", settings => settings.HeavyLegs, DefaultHeavyLegs, count => Number(count)),
            MaxMemoryPercent = Value("maxMemoryPercent", settings => settings.MaxMemoryPercent, DefaultMaxMemoryPercent, Number),
            SettleSeconds = Taken("settleSeconds", settings => settings.SettleSeconds, [.. DefaultSettleSeconds], settle => $"[{string.Join(", ", settle)}]"),
            PollSeconds = Value("pollSeconds", settings => settings.PollSeconds, DefaultPollSeconds, seconds => Number(seconds)),
            MaxWaitMinutes = Value("maxWaitMinutes", settings => settings.MaxWaitMinutes, DefaultMaxWaitMinutes, Number),
            MinFreeGiB = Value("minFreeGiB", settings => settings.MinFreeGiB, DefaultMinFreeGiB, Number),
            ReclaimIdleBuilds = Value("reclaimIdleBuilds", settings => settings.ReclaimIdleBuilds, false, reclaim => reclaim ? "true" : "false"),
        };

        var problems = new List<string>();

        HarnessConfigValidator.ValidateAdmission(merged, "admission", problems);

        if (problems.Count > 0)
        {
            throw new ConfigException($"The heavy-leg admission rule has {ReportText.Counted(problems.Count, "problem")}: {string.Join("; ", problems)}");
        }

        return new AdmissionInForce(
            new AdmissionRule(
                merged.HeavyLegs.Value,
                merged.MaxMemoryPercent.Value,
                TimeSpan.FromSeconds(merged.SettleSeconds[0]),
                TimeSpan.FromSeconds(merged.SettleSeconds[1]),
                TimeSpan.FromSeconds(merged.PollSeconds.Value),
                TimeSpan.FromMinutes(merged.MaxWaitMinutes.Value),
                (long)Math.Round(merged.MinFreeGiB.Value * DiskSpace.Gibibyte))
            {
                ReclaimIdleBuilds = merged.ReclaimIdleBuilds.Value,
            },
            fields);
    }

    /// <summary>Where a field no section declares comes from, as a line says it.</summary>
    public const string BuiltIn = "built in";

    /// <summary>A number as a line says it, the same on every machine.</summary>
    private static string Number(double value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>A section a machine's admission may be declared in, and where it is, as a line says it.</summary>
/// <param name="Settings">The section, or <see langword="null"/> where it is not declared.</param>
/// <param name="From">Where it is: <c>config.json's defaults.admission</c>.</param>
public sealed record AdmissionSection(AdmissionSettings? Settings, string From)
{
    /// <summary>The section a machine's settings file at <paramref name="path"/> declares.</summary>
    /// <param name="settings">Its <c>admission</c>, or <see langword="null"/>.</param>
    /// <param name="path">The file, as a line shows it.</param>
    public static AdmissionSection SettingsFile(AdmissionSettings? settings, string path) => new(settings, $"the machine's settings file '{path}'");

    /// <summary>The section config.json declares for the machine <paramref name="host"/> runs on.</summary>
    /// <param name="settings">Its <c>admission</c>, or <see langword="null"/>.</param>
    /// <param name="host">
    /// The host as the command line names it: an ssh host's own section, and for this machine and its WSL distributions,
    /// which run on it, <c>hosts.local</c>.
    /// </param>
    public static AdmissionSection Host(AdmissionSettings? settings, Hosts.HostId host)
    {
        ArgumentNullException.ThrowIfNull(host);

        return new(settings, host.Kind == Hosts.HostKind.Ssh ? $"config.json's hosts.ssh '{host.Name}' admission" : "config.json's hosts.local admission");
    }

    /// <summary>The section config.json declares for every machine.</summary>
    /// <param name="settings">Its <c>admission</c>, or <see langword="null"/>.</param>
    public static AdmissionSection Defaults(AdmissionSettings? settings) => new(settings, "config.json's defaults.admission");

    /// <summary>
    /// The sections the rule the legs placed on <paramref name="host"/> are admitted by is read from, the one that wins
    /// first, as a process to which the machine that sent it legs knows it as <paramref name="here"/> reads them: the
    /// settings file of the machine that admits them - the one <paramref name="settings"/> answers, or, for a distribution's
    /// own legs a machine sent it, that machine's, carried with them (<paramref name="dispatched"/>), since a distribution's
    /// own file is another machine's to them - then the section config.json declares for that machine
    /// (<see cref="Host"/>), then <c>defaults.admission</c>. The one owner of which sections, in which order, for which host:
    /// what <c>legs</c> says is the rule a run's legs are admitted by.
    /// </summary>
    /// <param name="host">The host the legs were placed on, as this process names it.</param>
    /// <param name="here">The host this process is to the machine that sent it legs, or <see langword="null"/>.</param>
    /// <param name="settings">
    /// Reads what the settings file of the machine that runs the legs says - asked only where that file is the one they are
    /// admitted by.
    /// </param>
    /// <param name="dispatched">What the machine that sent this process its legs carried beside them, where one did.</param>
    /// <param name="config">The configuration, whose sections follow the settings file's.</param>
    /// <exception cref="ConfigException">The settings file the legs are admitted by could not be read: refused, with its words.</exception>
    public static IReadOnlyList<AdmissionSection> For(
        Hosts.HostId host,
        Hosts.HostId? here,
        Func<MachineSettingsAnswer> settings,
        Hosts.Dispatch? dispatched,
        HarnessConfig config)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(config);

        // Read under the name the machine that sent the legs knows this one by, as their settings are: 'local' in the
        // configuration the two share is that machine.
        var named = host.Kind == Hosts.HostKind.Local && here is not null ? here : host;

        var file = here is { Kind: Hosts.HostKind.Wsl } && host.Kind == Hosts.HostKind.Local
            ? new AdmissionSection(dispatched?.MachineAdmission, "the settings file of the machine that sent the leg")
            : settings() is var answer && answer.Problem is { } problem
                ? throw new ConfigException(problem)
                : SettingsFile(answer.Admission, answer.Path ?? "nowhere");

        return
        [
            file,

            // This machine's section for it and for its WSL distributions, which run on it; an ssh host's own for that host.
            Host((named.Kind == Hosts.HostKind.Wsl ? config.Hosts.Local : config.Hosts.SettingsFor(named)).Admission, named),
            Defaults(config.Defaults.Admission),
        ];
    }
}

/// <summary>
/// The rule the machine that runs a leg's work admits it by, read once as that work begins and carried with the leg
/// (<see cref="Runs.PlacedLeg.AdmittedBy"/>): its slot, its floor - in a WSL distribution too, where the leg carries what
/// it was admitted by - and the page cache dropped as it ends all answer to that one reading, never to one taken after the
/// leg's work, which a settings file edited meanwhile would turn into a failure of a leg that had run.
/// </summary>
/// <param name="InForce">
/// The rule, and where each field came from; <see langword="null"/> where no section declares one, or where it could not be
/// read.
/// </param>
/// <param name="SettingsFile">What the settings file the leg is admitted by declared of admission, as it was read.</param>
/// <param name="Unread">
/// Why the rule could not be read, where it could not: a heavy leg is refused then, as its admission asks for the rule, and a
/// light one, which asks for none, runs.
/// </param>
public sealed record AdmissionReading(AdmissionInForce? InForce, AdmissionSettings? SettingsFile, string? Unread)
{
    /// <summary>The rule read from <paramref name="sections"/>, or why it could not be.</summary>
    /// <param name="sections">Names the sections, the one that wins first (<see cref="AdmissionSection.For"/>).</param>
    public static AdmissionReading Of(Func<IReadOnlyList<AdmissionSection>> sections)
    {
        ArgumentNullException.ThrowIfNull(sections);

        try
        {
            var read = sections();

            return new AdmissionReading(AdmissionSettings.InForce(read), read.Count > 0 ? read[0].Settings : null, null);
        }
        catch (ConfigException ex)
        {
            return new AdmissionReading(null, null, ex.Message);
        }
    }

    /// <summary>The rule, or <see langword="null"/> where no section declares one, and every leg is admitted at once.</summary>
    /// <exception cref="Results.HarnessException">
    /// The rule could not be read as the leg's work began: refused with the words it was refused in then, as a configuration
    /// that is wrong - the same fact for every heavy leg of the machine, so it ends the run rather than one leg's verdict.
    /// </exception>
    public AdmissionRule? RuleInForce() => Unread is { } why ? throw new Results.HarnessException(Results.HarnessExit.ConfigInvalid, why) : InForce?.Rule;
}

/// <summary>One field of the rule a machine admits heavy legs by, and where it came from.</summary>
/// <param name="Key">The key, as a section spells it: <c>heavyLegs</c>.</param>
/// <param name="Value">Its value, as a section spells it.</param>
/// <param name="From">The section it came from, or <see cref="AdmissionSettings.BuiltIn"/>.</param>
public sealed record AdmissionField(string Key, string Value, string From);

/// <summary>The rule a machine admits heavy legs by, and where each of its fields came from.</summary>
/// <param name="Rule">The rule.</param>
/// <param name="Fields">Each field, in the order a section's keys are listed.</param>
public sealed record AdmissionInForce(AdmissionRule Rule, IReadOnlyList<AdmissionField> Fields)
{
    /// <summary>
    /// The rule as a line says it, each field with its value, grouped by where they came from in the order each first
    /// does: <c>heavyLegs 3 from the machine's settings file '...'; minFreeGiB 4 from config.json's defaults.admission;
    /// maxMemoryPercent 76, ... built in</c>.
    /// </summary>
    public string Describe()
        => string.Join(
            "; ",
            Fields
                .GroupBy(field => field.From, StringComparer.Ordinal)
                .Select(group => $"{string.Join(", ", group.Select(field => $"{field.Key} {field.Value}"))} "
                    + (group.Key == AdmissionSettings.BuiltIn ? AdmissionSettings.BuiltIn : $"from {group.Key}")));
}

/// <summary>The rule one machine admits heavy legs by, every field decided and within its bounds.</summary>
/// <param name="HeavyLegs">Heavy legs the machine runs at once, across every command.</param>
/// <param name="MaxMemoryPercent">The memory in use a leg holding a slot starts below.</param>
/// <param name="SettleLeast">The least a settle lasts.</param>
/// <param name="SettleMost">The most a settle lasts.</param>
/// <param name="Poll">The time between looks while a leg waits.</param>
/// <param name="MaxWait">How long a leg waits before it is not admitted.</param>
/// <param name="MinFreeBytes">The room a heavy leg's build leaves free on each filesystem it fills; 0 stops no build.</param>
public sealed record AdmissionRule(
    int HeavyLegs,
    double MaxMemoryPercent,
    TimeSpan SettleLeast,
    TimeSpan SettleMost,
    TimeSpan Poll,
    TimeSpan MaxWait,
    long MinFreeBytes)
{
    /// <summary>
    /// Whether a heavy leg short of room first removes other trees' idle builds on the same filesystem
    /// (<see cref="AdmissionSettings.ReclaimIdleBuilds"/>).
    /// </summary>
    public bool ReclaimIdleBuilds { get; init; }
}
