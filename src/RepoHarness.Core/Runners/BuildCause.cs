using RepoHarness.Core.Configuration;
using RepoHarness.Core.Execution;

namespace RepoHarness.Core.Runners;

/// <summary>What builds a run's legs first, and the operating systems whose legs it builds.</summary>
/// <remarks>
/// Made only as one of its three kinds - a runner requiring the build, or a step of its action or a phase of its own
/// naming what the build makes - and never changed once made, so a cause naming nothing, or a requirement naming a
/// step, cannot be made at all: each reads as a refusal of the run says it, through <see cref="Reason"/>.
/// </remarks>
public sealed record BuildCause
{
    private BuildCause(string? checkRunner, string? kind, string? step, IReadOnlyList<string> names, IReadOnlyList<string> runOn)
    {
        CheckRunner = checkRunner;
        Kind = kind;
        Step = step;
        Names = names;
        RunOn = runOn;
    }

    /// <summary>
    /// The runner whose need it is where a run check names it, or <see langword="null"/> where it is the run's own runner's.
    /// </summary>
    public string? CheckRunner { get; }

    /// <summary>
    /// What <see cref="Step"/> is - <c>step</c> for a step of an action, <c>phase</c> for a runner's own phase - or
    /// <see langword="null"/> where the runner requires the build.
    /// </summary>
    public string? Kind { get; }

    /// <summary>The step or phase naming what the build makes, or <see langword="null"/> where the runner requires the build.</summary>
    public string? Step { get; }

    /// <summary>
    /// What the step or phase names that the build makes - <c>product</c>, <c>buildDir</c> - in the order it names them;
    /// never empty for one, and empty where the runner requires the build.
    /// </summary>
    public IReadOnlyList<string> Names { get; }

    /// <summary>The operating systems whose legs it builds; empty, every one.</summary>
    public IReadOnlyList<string> RunOn { get; }

    /// <summary>What builds the legs, as a refusal of the run says it.</summary>
    public string Reason => (CheckRunner, Step) switch
    {
        (null, null) => "the runner requires the build",
        (null, { } step) => $"{Kind} '{step}' names {LegPathNames.Spelled(Names)}",
        ({ } check, null) => $"runner '{check}', which a run check names, requires the build",
        ({ } check, { } step) => $"{Kind} '{step}' of runner '{check}', which a run check names, names {LegPathNames.Spelled(Names)}",
    };

    /// <summary>
    /// What builds the legs of a run of <paramref name="runner"/> first: its requiring the build, and each phase of its own
    /// and each step of <paramref name="action"/> - the steps a run of it runs - naming what the build makes.
    /// </summary>
    /// <param name="runner">The runner.</param>
    /// <param name="action">The steps a run of it runs, or <see langword="null"/> where it runs phases of its own.</param>
    /// <param name="checkRunner">The runner's name where a run check names it, or <see langword="null"/> for the run's own.</param>
    /// <remarks>
    /// Every one, not only the first: a step naming <c>{product}</c> under a runner that requires the build is still
    /// a step whose product must be there. A runner declares phases or an action, never both, so one that runs an action
    /// has no phase here. The one place that says what builds a runner's legs: a run reads it, through
    /// <see cref="Legs.LegWorkload.ForRunner"/>, and so does the file's reader, which refuses a runner saying it is not heavy
    /// while it builds.
    /// </remarks>
    public static IReadOnlyList<BuildCause> AllOf(RunnerConfig runner, ActionFile? action, string? checkRunner = null)
    {
        ArgumentNullException.ThrowIfNull(runner);

        IEnumerable<BuildCause> required = runner.RequireBuild ? [Required(checkRunner)] : [];

        return
        [
            .. required,
            .. runner.Phases.Where(phase => phase.NeedsBuild).Select(phase => Of(phase, checkRunner)),
            .. (action?.Steps ?? []).Where(step => step.NeedsBuild).Select(step => Of(step, checkRunner)),
        ];
    }

    /// <summary>A runner requiring the build, which builds the legs of every system.</summary>
    /// <param name="checkRunner">The runner's name where a run check names it, or <see langword="null"/> for the run's own.</param>
    public static BuildCause Required(string? checkRunner = null) => new(checkRunner, null, null, [], []);

    /// <summary>A step of an action naming what the build makes, which builds the legs of the systems it runs on.</summary>
    /// <param name="step">The step.</param>
    /// <param name="checkRunner">The runner's name where a run check names it, or <see langword="null"/> for the run's own.</param>
    /// <exception cref="ArgumentException">The step names nothing the build makes.</exception>
    public static BuildCause Of(ActionStep step, string? checkRunner = null)
    {
        ArgumentNullException.ThrowIfNull(step);

        return step.NeedsBuild
            ? new(checkRunner, "step", step.Name, step.NamesOfTheBuild, step.RunOn)
            : throw new ArgumentException($"Step '{step.Name}' names nothing the build makes, so it builds nothing.", nameof(step));
    }

    /// <summary>A runner's own phase naming what the build makes, which builds the legs of every system.</summary>
    /// <param name="phase">The phase.</param>
    /// <param name="checkRunner">The runner's name where a run check names it, or <see langword="null"/> for the run's own.</param>
    /// <exception cref="ArgumentException">The phase names nothing the build makes.</exception>
    public static BuildCause Of(RunnerPhase phase, string? checkRunner = null)
    {
        ArgumentNullException.ThrowIfNull(phase);

        return phase.NeedsBuild
            ? new(checkRunner, "phase", phase.Name, phase.NamesOfTheBuild, [])
            : throw new ArgumentException($"Phase '{phase.Name}' names nothing the build makes, so it builds nothing.", nameof(phase));
    }
}
