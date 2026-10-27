using System.Globalization;
using System.Text.RegularExpressions;
using DssHarness.Core.Configuration;

namespace DssHarness.Core.Agents;

/// <summary>
/// What the configuration says of an agent's commands, under <c>agentPolicy</c>: the names a child is given back, and how
/// long a command may run - where a call names no time of its own, and the most it may name.
/// </summary>
/// <param name="KeepEnvironment">The secret-shaped names a child keeps (<c>agentPolicy.keepEnvironment</c>); empty where none is given back.</param>
/// <param name="TimeoutSeconds">How long a command runs where its call names no time (<c>agentPolicy.commandTimeoutSeconds</c>).</param>
/// <param name="MaxTimeoutSeconds">The most a call may name (<c>agentPolicy.maxCommandTimeoutSeconds</c>).</param>
public sealed record AgentCommandSettings(IReadOnlyList<string> KeepEnvironment, int TimeoutSeconds, int MaxTimeoutSeconds)
{
    /// <summary>How long a command runs where neither its call nor the configuration names a time: two minutes.</summary>
    public const int DefaultTimeoutSeconds = 120;

    /// <summary>The most a call may name where the configuration names no bound: an hour.</summary>
    public const int DefaultMaxTimeoutSeconds = 3600;

    /// <summary>
    /// The most either time may say: what a timer of milliseconds holds - the runner's, and Claude Code's - about 24 days.
    /// A time past it would end no command, but fail the one it was set on once it had started.
    /// </summary>
    public const int MostTimeoutSeconds = int.MaxValue / 1000;

    /// <summary>What holds where the configuration says nothing: no name given back, and the two defaults.</summary>
    public static AgentCommandSettings Default { get; } = new([], DefaultTimeoutSeconds, DefaultMaxTimeoutSeconds);

    /// <summary>What <paramref name="policy"/> says, each key left out taking its default.</summary>
    /// <param name="policy">The configuration's <c>agentPolicy</c>; null where it has none.</param>
    public static AgentCommandSettings Of(AgentPolicyConfig? policy)
        => new(
            policy?.KeepEnvironment ?? [],
            policy?.CommandTimeoutSeconds ?? DefaultTimeoutSeconds,
            policy?.MaxCommandTimeoutSeconds ?? DefaultMaxTimeoutSeconds);
}

/// <summary>
/// The environment a child of an agent is given, on every client: a command of the tool runtime, and the <c>claude</c>
/// child, which loses the same before its own names are set. It loses every name the configuration appoints a value of
/// the slot for - a provider's keys, a client's settings, whether or not the tool's own environment holds them - and every
/// secret-shaped name (<see cref="SecretSuffixes"/>), less those <c>agentPolicy.keepEnvironment</c> gives back; an
/// appointed name is never given back. So another company's key or a deployment token is not handed to what an agent
/// runs. The one rule of it.
/// </summary>
public static partial class AgentChildEnvironment
{
    /// <summary>What a secret-shaped name ends with, in any case: such a name is taken from a child unless it is given back.</summary>
    public static IReadOnlyList<string> SecretSuffixes { get; } = ["_API_KEY", "_SECRET", "_SECRET_KEY", "_ACCESS_KEY", "_PRIVATE_KEY", "_PASSWORD", "_TOKEN"];

    /// <summary>The shape of a name <c>agentPolicy.keepEnvironment</c> may give: a variable's name, as every system spells one.</summary>
    public const string NameShape = "[A-Za-z_][A-Za-z0-9_]*";

    /// <summary>
    /// The tool's own environment, every name it holds with its value: what a child of an agent would inherit, before
    /// <see cref="Of"/> says what it loses. The one read of it for an agent's children, its names found as the system finds
    /// them: in any case on Windows, exactly elsewhere.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Own()
        => Read(System.Environment.GetEnvironmentVariables(), OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    /// <summary>
    /// <paramref name="variables"/>, each name with its value, its names compared by <paramref name="names"/>: a name the
    /// block holds twice in two cases - which a Windows block handed down may - is read once, the first.
    /// </summary>
    /// <param name="variables">An environment, as <see cref="System.Environment.GetEnvironmentVariables()"/> gives one.</param>
    /// <param name="names">How its names compare.</param>
    internal static IReadOnlyDictionary<string, string> Read(System.Collections.IDictionary variables, StringComparer names)
    {
        ArgumentNullException.ThrowIfNull(variables);

        var own = new Dictionary<string, string>(names);

        foreach (System.Collections.DictionaryEntry entry in variables)
        {
            own.TryAdd((string)entry.Key, entry.Value as string ?? string.Empty);
        }

        return own;
    }

    /// <summary>Whether <paramref name="name"/> is secret-shaped: it ends with one of <see cref="SecretSuffixes"/>, in any case.</summary>
    /// <param name="name">A variable's name.</param>
    public static bool SecretShaped(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return SecretSuffixes.Any(suffix => name.Length > suffix.Length && name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The overrides a child of an agent is started with, each name taken away given as null: every appointed name, and
    /// every secret-shaped name <paramref name="own"/> holds that <paramref name="kept"/> does not give back - a name told
    /// in any case, as the runner takes a name away in every spelling.
    /// </summary>
    /// <param name="own">The names the tool's own environment holds, which the child would otherwise inherit.</param>
    /// <param name="withheld">Every name the configuration appoints: never given, whatever <paramref name="kept"/> says.</param>
    /// <param name="kept">The secret-shaped names given back (<c>agentPolicy.keepEnvironment</c>).</param>
    public static Dictionary<string, string?> Of(IEnumerable<string> own, IReadOnlyList<string> withheld, IReadOnlyList<string> kept)
    {
        ArgumentNullException.ThrowIfNull(own);
        ArgumentNullException.ThrowIfNull(withheld);
        ArgumentNullException.ThrowIfNull(kept);

        var environment = new Dictionary<string, string?>(StringComparer.Ordinal);

        foreach (var name in own.Where(name => SecretShaped(name) && !kept.Contains(name, StringComparer.OrdinalIgnoreCase)))
        {
            environment[name] = null;
        }

        foreach (var name in withheld)
        {
            environment[name] = null;
        }

        return environment;
    }

    /// <summary>
    /// Every problem <paramref name="policy"/>'s command keys hold, each by its key: <c>keepEnvironment</c> given empty, or
    /// naming what is no variable's name, no secret-shaped name - which nothing takes away - or a name the configuration
    /// appoints, which is never given; a time below one second; a command's time past the most a call may name.
    /// </summary>
    /// <param name="policy">The configuration's <c>agentPolicy</c>; null where it has none.</param>
    /// <param name="appointed">Every name the configuration appoints.</param>
    public static IReadOnlyList<string> Problems(AgentPolicyConfig? policy, IReadOnlyList<string> appointed)
    {
        ArgumentNullException.ThrowIfNull(appointed);

        if (policy is null)
        {
            return [];
        }

        var problems = new List<string>();

        if (policy.KeepEnvironment is { Count: 0 })
        {
            problems.Add("agentPolicy.keepEnvironment is given empty: leave the key out to give no name back");
        }

        foreach (var (name, at) in (policy.KeepEnvironment ?? []).Select((name, at) => (name, at.ToString(CultureInfo.InvariantCulture))))
        {
            if (name is null || !Name().IsMatch(name))
            {
                problems.Add($"agentPolicy.keepEnvironment[{at}] '{name}' is no variable's name ({NameShape})");
            }
            else if (appointed.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                problems.Add($"agentPolicy.keepEnvironment[{at}] '{name}' is a name the configuration appoints a value of the slot for, which no child of an agent is ever given");
            }
            else if (!SecretShaped(name))
            {
                problems.Add($"agentPolicy.keepEnvironment[{at}] '{name}' gives back nothing: a child loses only the names the configuration appoints and those ending {string.Join(", ", SecretSuffixes)}");
            }
        }

        var commands = AgentCommandSettings.Of(policy);
        var timeout = commands.TimeoutSeconds;
        var most = commands.MaxTimeoutSeconds;

        if (timeout < 1)
        {
            problems.Add($"agentPolicy.commandTimeoutSeconds is {timeout.ToString(CultureInfo.InvariantCulture)}, and a command runs for one second at least");
        }

        if (most < 1)
        {
            problems.Add($"agentPolicy.maxCommandTimeoutSeconds is {most.ToString(CultureInfo.InvariantCulture)}, and a command runs for one second at least");
        }

        foreach (var (key, seconds) in new[] { ("commandTimeoutSeconds", timeout), ("maxCommandTimeoutSeconds", most) })
        {
            if (seconds > AgentCommandSettings.MostTimeoutSeconds)
            {
                problems.Add($"agentPolicy.{key} is {seconds.ToString(CultureInfo.InvariantCulture)}, past the {AgentCommandSettings.MostTimeoutSeconds.ToString(CultureInfo.InvariantCulture)} a command's timer holds");
            }
        }

        if (timeout >= 1 && most >= 1 && timeout > most)
        {
            problems.Add($"agentPolicy.commandTimeoutSeconds ({timeout.ToString(CultureInfo.InvariantCulture)}) is past agentPolicy.maxCommandTimeoutSeconds ({most.ToString(CultureInfo.InvariantCulture)}), the most a call may name");
        }

        return problems;
    }

    [GeneratedRegex("^" + NameShape + "$", RegexOptions.CultureInvariant)]
    private static partial Regex Name();
}
