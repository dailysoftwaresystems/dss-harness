using RepoHarness.Core.Configuration;
using RepoHarness.Core.Legs;
using RepoHarness.Core.Results;

namespace RepoHarness.Core.Mutations;

/// <summary>An arm a leg does not drive, and why: <c>--arms</c> did not name it, or its S row does not name the leg.</summary>
/// <param name="Arm">The arm.</param>
/// <param name="Reason">Why, as its line says it.</param>
public sealed record UnselectedArm(MutationArm Arm, string Reason);

/// <summary>What one leg does with each arm of a registry.</summary>
/// <param name="Driven">The arms it drives, in the registry's order.</param>
/// <param name="Unselected">The rest, each <c>skipped-not-selected</c>, in the registry's order.</param>
public sealed record LegArms(IReadOnlyList<MutationArm> Driven, IReadOnlyList<UnselectedArm> Unselected);

/// <summary>
/// Which arms a sweep drives, and on which legs: those <c>--arms</c> names, or every arm, each on the legs its S row
/// names, or on every selected leg. Decided before any host is touched, so a typo costs nothing.
/// </summary>
public static class ArmSelection
{
    /// <summary>
    /// Resolves what <c>--arms</c> was given, as <c>--legs</c> is resolved: a value may hold several ids separated by
    /// commas, an id is matched ignoring case, and <c>--arms</c> left out, passed as <see langword="null"/>, selects
    /// every arm. In the registry's order, whatever order they were named in, so one sweep of a registry reads like any
    /// other.
    /// </summary>
    /// <param name="registry">The registry.</param>
    /// <param name="values">What <c>--arms</c> was given, or <see langword="null"/> where it was left out.</param>
    /// <exception cref="HarnessException">An id names no arm, or <c>--arms</c> was given none.</exception>
    public static IReadOnlyList<MutationArm> Resolve(MutationRegistry registry, IReadOnlyList<string>? values)
    {
        ArgumentNullException.ThrowIfNull(registry);

        if (values is null)
        {
            return registry.Arms;
        }

        var names = values
            .SelectMany(value => value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            .ToList();

        // Given without a name, as --arms "$ARMS" is when the variable was never set, it is not "every arm": a sweep of
        // one arm, its name lost, would drive a hundred.
        if (names.Count == 0)
        {
            throw new HarnessException(HarnessExit.UsageError, "--arms was given no arm id; leave it out to select every arm");
        }

        var unknown = names.Where(name => registry.Find(name) is null).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        if (unknown.Count > 0)
        {
            throw new HarnessException(
                HarnessExit.UsageError,
                $"--arms names {string.Join(", ", unknown.Select(name => $"'{name}'"))}, which no A row of the registry declares");
        }

        var named = new HashSet<string>(names.Select(name => registry.Find(name)!.Id), StringComparer.OrdinalIgnoreCase);

        return [.. registry.Arms.Where(arm => named.Contains(arm.Id))];
    }

    /// <summary>
    /// The legs each arm with an S row runs on, resolved as <c>--legs</c> resolves its names, keyed by the arm's id; an
    /// arm without one runs on every selected leg, and has no entry. Each name the configuration declares as neither a
    /// leg nor a leg set is a problem, with its line, rather than a refusal of its own: the registry's problems are
    /// reported together.
    /// </summary>
    /// <param name="config">The configuration declaring the legs and leg sets.</param>
    /// <param name="registry">The registry.</param>
    /// <param name="problems">Where a problem is added.</param>
    public static IReadOnlyDictionary<string, IReadOnlySet<string>> Scopes(HarnessConfig config, MutationRegistry registry, ICollection<string> problems)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(problems);

        var scopes = new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var arm in registry.Arms)
        {
            if (arm.Scope is not { } scope)
            {
                continue;
            }

            try
            {
                var legs = LegSelection.Resolve(config, scope.Legs, $"line {scope.Line}: the S row of arm '{arm.Id}'").Legs;

                scopes[arm.Id] = new HashSet<string>(legs.Select(leg => leg.Name), StringComparer.OrdinalIgnoreCase);
            }
            catch (HarnessException ex)
            {
                problems.Add(ex.Message);
            }
        }

        return scopes;
    }

    /// <summary>
    /// What <paramref name="leg"/> does with each arm: drives those <paramref name="selected"/> holds whose scope names
    /// it, and reports every other arm of the registry not selected, saying why.
    /// </summary>
    /// <param name="leg">The leg, as the configuration declares it.</param>
    /// <param name="registry">The registry.</param>
    /// <param name="selected">The arms <c>--arms</c> selected.</param>
    /// <param name="scopes">Each scoped arm's legs, from <see cref="Scopes"/>.</param>
    public static LegArms For(
        string leg,
        MutationRegistry registry,
        IReadOnlyCollection<MutationArm> selected,
        IReadOnlyDictionary<string, IReadOnlySet<string>> scopes)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(selected);
        ArgumentNullException.ThrowIfNull(scopes);

        var named = new HashSet<string>(selected.Select(arm => arm.Id), StringComparer.OrdinalIgnoreCase);
        var driven = new List<MutationArm>();
        var unselected = new List<UnselectedArm>();

        foreach (var arm in registry.Arms)
        {
            if (!named.Contains(arm.Id))
            {
                unselected.Add(new UnselectedArm(arm, "--arms did not name it"));
            }
            else if (!InScope(arm, leg, scopes))
            {
                unselected.Add(new UnselectedArm(arm, $"its S row, line {arm.Scope!.Line}, does not name this leg"));
            }
            else
            {
                driven.Add(arm);
            }
        }

        return new LegArms(driven, unselected);
    }

    /// <summary>
    /// The arms in <paramref name="selected"/> whose S row names none of <paramref name="legs"/>: arms the sweep was
    /// asked for and drives nowhere, which its closing line names rather than let them pass unseen.
    /// </summary>
    /// <param name="legs">Every leg the sweep selected.</param>
    /// <param name="selected">The arms <c>--arms</c> selected.</param>
    /// <param name="scopes">Each scoped arm's legs, from <see cref="Scopes"/>.</param>
    public static IReadOnlyList<MutationArm> DrivenNowhere(
        IReadOnlyCollection<string> legs,
        IReadOnlyCollection<MutationArm> selected,
        IReadOnlyDictionary<string, IReadOnlySet<string>> scopes)
    {
        ArgumentNullException.ThrowIfNull(legs);
        ArgumentNullException.ThrowIfNull(selected);
        ArgumentNullException.ThrowIfNull(scopes);

        return [.. selected.Where(arm => !legs.Any(leg => InScope(arm, leg, scopes)))];
    }

    private static bool InScope(MutationArm arm, string leg, IReadOnlyDictionary<string, IReadOnlySet<string>> scopes)
        => !scopes.TryGetValue(arm.Id, out var legs) || legs.Contains(leg);
}
