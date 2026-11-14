using DssHarness.Core.Configuration;
using DssHarness.Core.Legs;
using DssHarness.Core.Results;

namespace DssHarness.Core.Build;

/// <summary>How a build tool goes on past a unit that fails to build.</summary>
public enum KeepGoingHow
{
    /// <summary>It stops at its first failures unless it is told otherwise, by the arguments its way names.</summary>
    WhenTold,

    /// <summary>It goes on past a failing unit by itself, so nothing is given it.</summary>
    ByItself,

    /// <summary>No way is known to have it go on past a failing unit, so <c>--keep-going</c> is refused for a leg it builds.</summary>
    Never,
}

/// <summary>
/// How one build tool a leg's build runs goes on past a unit that fails to build, as an adapter answers for it: the
/// arguments the tool is given, where it takes some; that it goes on by itself, where it does; or that no way is known,
/// where none is.
/// </summary>
/// <param name="Builds">What builds, as help names it: a CMake generator, or the tool of a project type.</param>
/// <param name="How">Whether the tool is told, goes on by itself, or cannot be made to.</param>
/// <param name="Arguments">
/// What the build tool itself is given to go on: for CMake, after <c>--</c> on <c>cmake --build</c>; empty where it is
/// given nothing.
/// </param>
/// <param name="Said">How it goes on, or why it cannot, as help and a refusal say it.</param>
public sealed record KeepGoingWay(string Builds, KeepGoingHow How, IReadOnlyList<string> Arguments, string Said)
{
    /// <summary>What a build hands its tool its way after: <c>cmake --build</c>'s own end, past which its tool reads the rest.</summary>
    public const string ToTheTool = "--";

    /// <summary>
    /// Whether a build phase run with <paramref name="arguments"/> had its tool go on past failing units: by itself, or
    /// given this way's arguments after <see cref="ToTheTool"/> - read from what the phase ran with, so a line never says a
    /// tool went on that was not told to.
    /// </summary>
    /// <param name="arguments">What the build phase ran its program with.</param>
    public bool GivenIn(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        if (How != KeepGoingHow.WhenTold)
        {
            return How == KeepGoingHow.ByItself;
        }

        var start = arguments.ToList().IndexOf(ToTheTool);

        return start >= 0 && arguments.Skip(start + 1).Take(Arguments.Count).SequenceEqual(Arguments, StringComparer.Ordinal);
    }
}

/// <summary>
/// <c>--keep-going</c>: a leg's build goes on past the units that fail to build, so its line names every one of them and
/// not only the first. The one owner of the option's name and of the refusal a run is given before any host is measured
/// where it cannot be honoured; how each build tool goes on is its adapter's to say (<see cref="IBuildAdapter.KeepGoingFor"/>).
/// </summary>
/// <remarks>
/// Asked of the configuration alone, as a run's other refusals of what it cannot build are: whether a tool goes on past
/// a failing unit, and how, is said by the generator a toolchain names or the project's type, so a run that cannot
/// honour the option is told so before anything is synced, locked or built, naming every leg at once.
/// </remarks>
public static class KeepGoing
{
    /// <summary>The option, as <c>build</c>, <c>test</c> and <c>run</c> take it and a host running one of their legs is given it.</summary>
    public const string Option = "--keep-going";

    /// <summary>
    /// Refuses a run asked to keep going where it cannot be: where it builds none of the legs it selects, or where the
    /// build tool of a leg it builds has no known way to go on past a failing unit - one refusal naming every such leg.
    /// </summary>
    /// <param name="config">The configuration.</param>
    /// <param name="legs">The legs the run selects.</param>
    /// <param name="workload">What the run has each leg do, which says whether it builds a leg of each system.</param>
    /// <exception cref="HarnessException">
    /// The run builds none of its legs (<see cref="HarnessExit.UsageError"/>), or a leg it builds is built by a tool
    /// with no known way to go on (<see cref="HarnessExit.Refused"/>).
    /// </exception>
    public static void Require(HarnessConfig config, IReadOnlyList<SelectedLeg> legs, LegWorkload workload)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(legs);
        ArgumentNullException.ThrowIfNull(workload);

        var built = legs.Where(leg => workload.On(leg.Leg.Os).Build).ToList();

        if (built.Count == 0)
        {
            throw new HarnessException(
                HarnessExit.UsageError,
                $"{Option} goes on past the units a leg's build fails, and this run builds none of the legs it selects. Leave {Option} out.");
        }

        var refused = new List<string>();

        foreach (var leg in built)
        {
            var project = VariantKey.ProjectFor(config, leg.Leg);
            var variant = VariantKey.For(config, leg.Leg, leg.Leg.Os);

            // A leg that cannot be built at all is refused for that by the run itself, naming what it lacks.
            if (!variant.CanBuild(leg.Name, project, leg.Leg.Os, out _) || BuildAdapters.Find(project.Type) is not { } adapter)
            {
                continue;
            }

            if (Unhonoured(config, adapter, variant.Toolchain) is { } why)
            {
                refused.Add($"  - Leg '{leg.Name}': {why}.");
            }
        }

        if (refused.Count == 0)
        {
            return;
        }

        throw new HarnessException(
            HarnessExit.Refused,
            string.Join(
                Environment.NewLine,
                [
                    $"{Option} cannot be honoured for {(refused.Count == 1 ? "a leg" : $"{refused.Count} legs")} this run builds, so nothing was run:",
                    .. refused,
                    $"Leave {Option} out, or leave {(refused.Count == 1 ? "the leg" : "those legs")} out of the run with --legs.",
                ]));
    }

    /// <summary>
    /// Refuses one leg's build asked to keep going where the configuration it builds by - a copy's own, which can differ
    /// from the tree's the run was refused by before anything ran - gives its build tool no known way: so its tool is never
    /// run without its way while the build was asked for it.
    /// </summary>
    /// <param name="config">The configuration the leg builds by.</param>
    /// <param name="request">The leg's build.</param>
    /// <param name="adapter">The adapter that builds it.</param>
    /// <exception cref="HarnessException">The leg's build tool has no known way to go on (<see cref="HarnessExit.Refused"/>).</exception>
    public static void RequireFor(HarnessConfig config, BuildRequest request, IBuildAdapter adapter)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(adapter);

        if (request.KeepGoing && Unhonoured(config, adapter, request.Variant.Toolchain) is { } why)
        {
            throw new HarnessException(
                HarnessExit.Refused,
                $"{Option} cannot be honoured for leg '{request.Leg}' by the configuration it builds by, so its build was not started: {why}. "
                + $"Leave {Option} out, or leave the leg out of the run with --legs.");
        }
    }

    /// <summary>Why a build by <paramref name="adapter"/> with <paramref name="toolchain"/> cannot go on past a failing unit; <see langword="null"/> where it can.</summary>
    private static string? Unhonoured(HarnessConfig config, IBuildAdapter adapter, string toolchain)
        => adapter.KeepGoingFor(config, toolchain) is { How: KeepGoingHow.Never } never ? never.Said : null;
}
