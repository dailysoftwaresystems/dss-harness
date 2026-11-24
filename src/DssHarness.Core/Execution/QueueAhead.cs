namespace DssHarness.Core.Execution;

/// <summary>
/// Who stands ahead in one class of the machine's dispatch queue, as a wait on that queue is told: the class, and each
/// tenant with a unit ahead in it, in the order they stand. A record of the queue's own standing
/// (<see cref="DispatchOrder.Reading.StandingOf"/>), carried by the admission's telling (<see cref="AdmissionTold"/>)
/// into the run's measure, so a wait behind another repository's units says whose they were.
/// </summary>
/// <param name="Class">The class whose line is waited on.</param>
/// <param name="Tenants">Each tenant with a unit ahead in the class, in the order they stand, first first.</param>
public sealed record QueueAhead(DispatchClassName Class, IReadOnlyList<string> Tenants)
{
    /// <summary>Whether <paramref name="other"/> is the same standing: the class, and the same tenants in the same order.</summary>
    /// <param name="other">The standing, or <see langword="null"/>.</param>
    public bool Equals(QueueAhead? other)
        => other is not null
            && Class == other.Class
            && Tenants.SequenceEqual(other.Tenants, StringComparer.Ordinal);

    /// <summary>The standing's hash, over its class and its tenants.</summary>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Class);

        foreach (var tenant in Tenants)
        {
            hash.Add(tenant, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }

    /// <summary>
    /// The standings of <paramref name="ahead"/> as one, each class once: every tenant that stands ahead in it, in the
    /// order each first stands. The one owner of that union, wherever a wait has met more than one standing.
    /// </summary>
    /// <param name="ahead">The standings to join, as each was told.</param>
    public static IReadOnlyList<QueueAhead> Joined(IReadOnlyList<QueueAhead> ahead)
    {
        ArgumentNullException.ThrowIfNull(ahead);

        var classes = new Dictionary<DispatchClassName, List<string>>();

        foreach (var standing in ahead)
        {
            if (!classes.TryGetValue(standing.Class, out var tenants))
            {
                classes[standing.Class] = tenants = [];
            }

            foreach (var tenant in standing.Tenants.Where(tenant => !tenants.Contains(tenant, StringComparer.Ordinal)))
            {
                tenants.Add(tenant);
            }
        }

        return [.. classes.Select(joined => new QueueAhead(joined.Key, joined.Value))];
    }
}
