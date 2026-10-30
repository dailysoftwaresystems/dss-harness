namespace DssHarness.Core.Agents;

/// <summary>
/// One entry of the order a step runs by, as the engine resolved it for running: a model with one of its provider's
/// keys (value 424) - the first entry serving, each later model of the tier with each of its keys in turn, each
/// model's keys before the next model - the effort the step gives that model resolved with it, and whether it can be
/// used on this machine.
/// </summary>
/// <param name="Model">The model's code.</param>
/// <param name="Provider">The provider that lists it.</param>
/// <param name="Client">The client it is reached through: the step's, its definition's, or the provider's own.</param>
/// <param name="Key">The name the key is read under; null where its client signs in with the machine's login.</param>
/// <param name="Effort">The level this model runs at, resolved through the four sources; null for the model's own default.</param>
/// <param name="Usable">Whether it can be used on this machine.</param>
/// <param name="WhyNot">Why it cannot, where it cannot; null where it can.</param>
public sealed record AgentRunEntry(string Model, string Provider, string Client, string? Key, string? Effort, bool Usable, string? WhyNot = null)
{
    /// <summary>The configuration its client runs it by - the provider's, the model's and the client's entries; null where it cannot be used.</summary>
    public AgentEntries? Entries { get; init; }

    /// <summary>How it signs in, where its client does; null for the machine's login and where it cannot be used.</summary>
    public AgentCredential? Credential { get; init; }
}

/// <summary>
/// The rules the engine's fallback walks a step's order by (part 03, "The engine's fallback"): what a limit covers,
/// which entry a cause moves to, and which decline moves nowhere. One owner, read by the loop for its own provider's
/// entries and by the runner for every entry of the order.
/// </summary>
public static class AgentFallback
{
    /// <summary>What a decline the provider marks <c>reasoning_extraction</c> is of: what the prompt asks, which no other model would be asked differently.</summary>
    public const string ReasoningExtraction = "reasoning_extraction";

    /// <summary>How a move within the same provider and client is told: the request sent again, with the model or the key changed.</summary>
    public const string SentAgain = "the request sent again";

    /// <summary>How a move to another client, and every move on Claude Code, is told: a new conversation with the step's prompt.</summary>
    public const string NewConversation = "a new conversation with the step's prompt";

    /// <summary>
    /// Whether <paramref name="limit"/> - met while the step was on <paramref name="limitClient"/> - covers
    /// <paramref name="entry"/>: a limit of a model on a key covers that model on that key alone; a limit of a key
    /// covers every entry on that key; a limit of a client's login covers every entry on that login.
    /// </summary>
    /// <param name="limit">The limit, as the ending named it.</param>
    /// <param name="limitClient">The client whose entry met it.</param>
    /// <param name="entry">The entry asked of.</param>
    public static bool Covers(AgentAccountLimit limit, string limitClient, AgentRunEntry entry)
    {
        ArgumentNullException.ThrowIfNull(limit);
        ArgumentNullException.ThrowIfNull(entry);

        return limit.Key switch
        {
            // A client's login: every entry that login signs - the same client, signing in with the machine's login.
            null => string.Equals(entry.Client, limitClient, StringComparison.Ordinal) && string.IsNullOrWhiteSpace(entry.Key),
            // A key's whole: every entry on that key, whichever client signs with it.
            { } key when limit.Model is null => string.Equals(entry.Key, key, StringComparison.Ordinal),
            // A model's on a key: that model on that key alone.
            { } key => string.Equals(entry.Key, key, StringComparison.Ordinal) && string.Equals(entry.Model, limit.Model, StringComparison.Ordinal),
        };
    }

    /// <summary>
    /// The entry a cause moves the step to, and why each entry passed over before it: a decline takes the next
    /// model's entry, past the model's other keys - another account of the same model is asked the same and answers
    /// the same - and a limit and a refused key take the next entry they do not cover, another key of the same model
    /// first, for a limit covers an account and an account is a key or a login. A limit remembered no longer covers
    /// what it covered once <paramref name="now"/> is past when it lifts. Null where none is left.
    /// </summary>
    /// <param name="order">The step's order.</param>
    /// <param name="after">The place to look past: the last entry that served.</param>
    /// <param name="decline">Whether the cause is a decline, which passes the model's other keys.</param>
    /// <param name="refused">The keys the providers refused, by name, passed over for the rest of the run.</param>
    /// <param name="limits">The limits the run remembers, each with the client whose entry met it.</param>
    /// <param name="now">The time the move is made on the clock.</param>
    public static (int At, IReadOnlyList<(string Model, string Why)> PassedOver)? Next(
        IReadOnlyList<AgentRunEntry> order,
        int after,
        bool decline,
        IReadOnlyCollection<string> refused,
        IReadOnlyList<(AgentAccountLimit Limit, string Client)> limits,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(refused);
        ArgumentNullException.ThrowIfNull(limits);

        var serving = after < order.Count ? order[after].Model : null;
        var passed = new List<(string Model, string Why)>();

        for (var at = after + 1; at < order.Count; at++)
        {
            if (decline && order[at].Model == serving)
            {
                passed.Add((order[at].Model, "a decline passes over the model's other keys"));
                continue;
            }

            if (Taken(order[at], refused, limits, now) is { } why)
            {
                passed.Add((order[at].Model, why));
                continue;
            }

            return (at, passed);
        }

        return null;
    }

    /// <summary>
    /// Why <paramref name="entry"/> cannot be run on now - it cannot be used here, its key was refused, or a limit the
    /// run remembers covers it and has not lifted; null where it can.
    /// </summary>
    /// <param name="entry">The entry asked of.</param>
    /// <param name="refused">The keys the providers refused, by name.</param>
    /// <param name="limits">The limits the run remembers, each with the client whose entry met it.</param>
    /// <param name="now">The time the entry is asked of on the clock.</param>
    public static string? Taken(AgentRunEntry entry, IReadOnlyCollection<string> refused, IReadOnlyList<(AgentAccountLimit Limit, string Client)> limits, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(refused);
        ArgumentNullException.ThrowIfNull(limits);

        if (!entry.Usable)
        {
            return entry.WhyNot ?? "it cannot be used on this machine";
        }

        if (entry.Key is { Length: > 0 } key && refused.Contains(key))
        {
            return $"its key {key} was refused";
        }

        if (limits.FirstOrDefault(limit => Covers(limit.Limit, limit.Client, entry) && (limit.Limit.Lifts is null || limit.Limit.Lifts > now)) is { Limit: { } covers })
        {
            return $"the limit covers it{(covers.Lifts is { } lifts ? $" until {lifts:u}" : string.Empty)}";
        }

        return null;
    }
}
