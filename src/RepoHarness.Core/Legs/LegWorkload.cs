using RepoHarness.Core.Build;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.Execution;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Processes;
using RepoHarness.Core.Results;
using RepoHarness.Core.Runners;

namespace RepoHarness.Core.Legs;

/// <summary>
/// What a command will have each leg do, which decides the programs a host must have before a leg is
/// placed on it.
/// </summary>
/// <param name="Build">
/// Whether the leg is built: its project's build program, ninja where its toolchain asks for the
/// Ninja generator, and the compilers its variant names. What builds a run's legs, and on which
/// systems, is <see cref="BuiltBy"/>.
/// </param>
/// <param name="Test">Whether the leg's tests are run: its test runner.</param>
/// <param name="Programs">
/// What else the command starts on every leg that its host must have, such as the steps of the
/// runner <c>run</c> was given; one started under an environment that sets PATH goes to
/// <see cref="UnderOwnPath"/> instead.
/// </param>
/// <remarks>
/// Said by each command rather than assumed for all of them. Asked what a leg needs in general, a
/// survey turns a build away for want of the test runner it never starts, and a copy for want of a
/// compiler, although a copy starts no program at all.
/// </remarks>
public sealed record LegWorkload(bool Build, bool Test, IReadOnlyList<string> Programs)
{
    /// <summary>
    /// What else the command starts on every leg under an environment that sets PATH: asked about,
    /// so the directory each is found in reaches that PATH like any other, and never required of a
    /// host - that PATH is where it is looked for when it starts, and no survey can see it.
    /// </summary>
    public IReadOnlyList<string> UnderOwnPath { get; init; } = [];

    /// <summary>
    /// What the command starts only on legs of some operating systems: the programs of an action's
    /// steps that name <c>runOn</c>. A leg of any other system never starts them, and is never turned
    /// away for want of one.
    /// </summary>
    public IReadOnlyList<OsScopedStart> OnlyOn { get; init; } = [];

    /// <summary>
    /// What builds a run's legs first, each with the operating systems whose legs it builds: its runner, or a runner its
    /// run checks name whose steps could be read, requiring the build, or a step or phase of one of them naming what the
    /// build makes. One limited by <c>runOn</c> builds the legs of those systems alone, so <see cref="Build"/> says only
    /// what builds every leg; read through <see cref="On"/>, which keeps what builds a leg of that system. A command that
    /// builds every leg as such - build, test - says so in <see cref="Build"/>, and names nothing here.
    /// </summary>
    public IReadOnlyList<BuildCause> BuiltBy { get; init; } = [];

    /// <summary>
    /// Whether the command starts any program on the leg's host. A copy starts none, so nothing has to
    /// be set up there for it - not even the developer environment the leg's toolchain names.
    /// </summary>
    public bool StartsPrograms => Build || BuiltBy.Count > 0 || Test || Programs.Count > 0 || UnderOwnPath.Count > 0 || OnlyOn.Count > 0;

    /// <summary>
    /// Whether what the command runs says its legs are heavy, where they build nothing: the runner, a step of it this run
    /// runs on every system, or a runner its expected exceptions' run checks name - saying so, running such a step, or
    /// one whose steps could not be read.
    /// </summary>
    public bool DeclaredHeavy { get; init; }

    /// <summary>
    /// The operating systems whose legs a heavy step limited by <c>runOn</c> makes heavy: a leg of any other system never
    /// runs it, and is as light as the rest of the command leaves it. Read through <see cref="On"/>.
    /// </summary>
    public IReadOnlyList<string> HeavyOnlyOn { get; init; } = [];

    /// <summary>
    /// Whether the command admits each unit of a leg's work on its own, rather than the leg whole: a sweep of mutation
    /// arms, each built and run in a worker of its own, asks its machine to take each arm as the arm starts. No slot is
    /// held for such a leg, whose units each hold one while they run - but where its machine admits the leg whole: one
    /// run in a WSL distribution, which cannot see the memory of the machine it runs on, is taken whole by the command
    /// that sent it there, and its sweep runs one worker.
    /// </summary>
    public bool AdmitsEachUnit { get; init; }

    /// <summary>
    /// Whether what the command builds is the leg's own tree - its build directory, or the first worker beside it of a
    /// sweep of its arms - whose room each host is asked about, and the leg placed by. A self-test of the sweep builds
    /// instead the fixture this tool carries, a few megabytes kept among this tool's own data on whichever machine runs
    /// the leg, which no host is asked about and no build of the leg says the size of: each of its workers is measured
    /// as it is planned, on that machine.
    /// </summary>
    public bool BuildsTheLegsTree { get; init; } = true;

    /// <summary>
    /// Whether a leg of this workload is heavy: it builds or tests, or its runner says it is - and the command does not
    /// admit each unit of its work instead (<see cref="AdmitsEachUnit"/>). A heavy leg takes one of its machine's
    /// heavy-leg slots before its work starts, where that machine declares admission; a repository guard that only reads
    /// the tree is light, and starts at once.
    /// </summary>
    public bool Heavy => (Build || Test || DeclaredHeavy) && !AdmitsEachUnit;

    /// <summary>Building and testing: what a leg is for, and what <c>legs</c> answers for.</summary>
    public static LegWorkload BuildAndTest { get; } = new(Build: true, Test: true, []);

    /// <summary>Building alone.</summary>
    public static LegWorkload BuildOnly { get; } = new(Build: true, Test: false, []);

    /// <summary>A copy of the tree, which starts no program on the host.</summary>
    public static LegWorkload Copy { get; } = new(Build: false, Test: false, []);

    /// <summary>
    /// What running <paramref name="runner"/> has a leg do: its steps, and a build first where it, or a
    /// runner its run checks name, requires one or runs a step or phase that needs one.
    /// </summary>
    /// <param name="runner">The runner.</param>
    /// <param name="action">Its action file, when it runs one rather than phases of its own.</param>
    /// <remarks>
    /// A step whose environment - its own or the runner's - sets PATH finds its program on that PATH,
    /// which no survey can see, so its program is the run's to find rather than a demand on the host,
    /// and is only asked about: see <see cref="UnderOwnPath"/>.
    /// <para>
    /// A leg is built first where the runner requires the build, or where a step or phase it runs names what the build
    /// makes, whatever the runner says: see <see cref="ActionStep.NeedsBuild"/>.
    /// </para>
    /// </remarks>
    /// <param name="checks">
    /// The runners its expected exceptions' run checks name, which run within its legs, each by name with the action
    /// steps a run of it runs, or whether those could not be read. One needing the build - requiring it, or by a step or
    /// phase naming <c>{product}</c>, <c>{buildDir}</c> or a compiler - builds its legs first, since a check runs on the leg as the
    /// runner carrying it left it, and is never built itself; one heavy, itself or by a step, makes its legs heavy, as
    /// the runner itself would. One whose steps could not be read makes them heavy, and builds nothing: the check that
    /// runs it is refused, as it always was, so a build made for it would be made for nothing, and a leg that build
    /// could not make would refuse a run whose checks may never run.
    /// </param>
    public static LegWorkload ForRunner(
        RunnerConfig runner,
        ActionFile? action,
        IEnumerable<(string Name, RunnerConfig Runner, ActionFile? Action, bool Unread)>? checks = null)
    {
        ArgumentNullException.ThrowIfNull(runner);

        var checking = (checks ?? []).ToList();

        var runnerPath = ProcessRunner.SetsPath(runner.Env.Keys);

        List<OsScopedStart> starts = action is null
            ? [.. runner.Phases
                .Where(phase => phase.Command.Count > 0)
                .Select(phase => new OsScopedStart(phase.Command[0], runnerPath || ProcessRunner.SetsPath(phase.Env.Keys), []))]
            : [.. action.Steps
                .SelectMany(step => step.Commands.Select(command =>
                    new OsScopedStart(command.Program, runnerPath || ProcessRunner.SetsPath(step.Env.Keys), step.RunOn)))];

        var everywhere = starts.Where(start => start.RunOn.Count == 0).ToList();
        var own = Heaviness(runner, action);
        var checked_ = checking.Select(check => (check.Unread, Steps: Heaviness(check.Runner, check.Action))).ToList();

        List<BuildCause> causes =
        [
            .. BuildCause.AllOf(runner, action),
            .. checking.Where(check => !check.Unread).SelectMany(check => BuildCause.AllOf(check.Runner, check.Action, check.Name)),
        ];

        return new LegWorkload(
            Build: causes.Any(cause => cause.RunOn.Count == 0),
            Test: false,
            [.. everywhere.Where(start => !start.OwnPath).Select(start => start.Program)])
        {
            UnderOwnPath = [.. everywhere.Where(start => start.OwnPath).Select(start => start.Program)],
            OnlyOn = [.. starts.Where(start => start.RunOn.Count > 0)],
            BuiltBy = causes,
            DeclaredHeavy = own.Everywhere || checked_.Any(check => check.Unread || check.Steps.Everywhere),
            HeavyOnlyOn = [.. own.OnlyOn.Concat(checked_.SelectMany(check => check.Steps.OnlyOn)).Distinct(StringComparer.OrdinalIgnoreCase)],
        };
    }

    /// <summary>
    /// Refuses, before any host is measured, each of <paramref name="legs"/> this workload builds first that cannot be
    /// built - it names no project, or no toolchain for its system - and each where a step or phase of the run's own
    /// runner names <c>{product}</c> and its project declares no one product for its system, or names one of the leg's
    /// compilers and no build of its project identifies one.
    /// </summary>
    /// <param name="config">The whole configuration.</param>
    /// <param name="runnerName">The runner the run was given, as the refusal names it.</param>
    /// <param name="legs">The legs the run reaches.</param>
    /// <exception cref="HarnessException">
    /// A leg is built first that cannot be built, or a step or phase of the run's own runner names a <c>{product}</c> no
    /// one declared file fills in, or a compiler on a leg no build identifies one for. One refusal names every such
    /// leg, of each kind: each that cannot be built with why and what builds it, and each product and compiler with the
    /// step or phase naming it.
    /// </exception>
    /// <remarks>
    /// Asked of the configuration alone, as a leg on whose system no step runs is, so nothing is synced, locked or
    /// built for a run that cannot happen. For a runner requiring the build, the first was found only where the leg's
    /// build began: it ended the whole run once its hosts were measured and its slots taken, without saying what built
    /// the leg; and the second was refused only after the build it had just cost, as the third would be - which leg has
    /// a compiler its build identifies is the configuration's to say (<see cref="CMakeToolchainReader.IdentifiesNone"/>),
    /// while which compiler that is, is the build's. A product or a compiler a run check's step names is refused by the
    /// check that runs it, when it runs: refused here, it would refuse a run whose checks may never run. The build a
    /// check needs is made before the run whether or not the check runs, so a leg it cannot build is refused here,
    /// naming the check.
    /// </remarks>
    public void RequireBuildable(HarnessConfig config, string runnerName, IEnumerable<SelectedLeg> legs)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrWhiteSpace(runnerName);
        ArgumentNullException.ThrowIfNull(legs);

        var unbuildable = new List<string>();
        var productless = new List<string>();
        var compilerless = new List<string>();

        foreach (var leg in legs)
        {
            var here = On(leg.Leg.Os);

            if (!here.Build)
            {
                continue;
            }

            var project = VariantKey.ProjectFor(config, leg.Leg);

            if (!VariantKey.For(config, leg.Leg, leg.Leg.Os).CanBuild(leg.Name, project, leg.Leg.Os, out var why))
            {
                // A workload that builds every leg as such - build, test - names nothing that builds it, and the
                // refusal says only why it cannot be built.
                unbuildable.Add(
                    here.BuiltBy.Count == 0
                        ? $"  - {why}"
                        : $"  - {why} The run builds it first because {string.Join("; ", here.BuiltBy.Select(cause => cause.Reason))}.");

                continue;
            }

            // The run's own steps and phases alone: what a run check's names is the check's to refuse, when it runs.
            var own = here.BuiltBy.Where(cause => cause.CheckRunner is null).ToList();

            if (project.Product(leg.Leg.Os).Problem is { } problem)
            {
                foreach (var cause in own.Where(cause => cause.Names.Contains(LegPathNames.Product, StringComparer.Ordinal)))
                {
                    productless.Add($"  - Leg '{leg.Name}': {cause.Kind} '{cause.Step}' names {{{LegPathNames.Product}}}, and {problem}.");
                }
            }

            if (CMakeToolchainReader.IdentifiesNone(project) is { } none)
            {
                foreach (var cause in own)
                {
                    var compilers = cause.Names.Where(name => LegPathNames.CompilerLanguage(name) is not null).ToList();

                    if (compilers.Count > 0)
                    {
                        compilerless.Add($"  - Leg '{leg.Name}': {cause.Kind} '{cause.Step}' names {LegPathNames.Spelled(compilers)}, and {none}.");
                    }
                }
            }
        }

        if (unbuildable.Count + productless.Count + compilerless.Count == 0)
        {
            return;
        }

        List<string> lines = [$"A run of runner '{runnerName}' cannot run every leg it reaches, so nothing was run."];

        if (unbuildable.Count > 0)
        {
            var one = unbuildable.Count == 1;

            lines.Add(
                $"It builds {(one ? "a leg" : $"{unbuildable.Count} legs")} first that cannot be built: leave {(one ? "it" : "them")} "
                + $"out of the run - with --legs, or from the runner's own legs - or give {(one ? "it" : "each")} a project and a "
                + "toolchain to build:");
            lines.AddRange(unbuildable);
        }

        if (productless.Count > 0)
        {
            lines.Add(
                $"It runs a step or phase naming {{{LegPathNames.Product}}} where no one product fills it in: leave the leg out, "
                + "or declare one build output for its system:");
            lines.AddRange(productless);
        }

        if (compilerless.Count > 0)
        {
            lines.Add(
                "It runs a step or phase naming a compiler where no build of the leg identifies one: leave the leg out, or take "
                + "the name out:");
            lines.AddRange(compilerless);
        }

        throw new HarnessException(HarnessExit.ConfigInvalid, string.Join(Environment.NewLine, lines));
    }

    /// <summary>
    /// How heavy <paramref name="runner"/>'s legs are by what it says: everywhere, where it says so or a step of
    /// <paramref name="action"/> - the steps a run of it runs - says so on every system; and the systems a heavy step
    /// limited by <c>runOn</c> makes heavy.
    /// </summary>
    private static (bool Everywhere, IReadOnlyList<string> OnlyOn) Heaviness(RunnerConfig runner, ActionFile? action)
    {
        var heavy = action?.Steps.Where(step => step.Heavy).ToList() ?? [];

        return (
            runner.Heavy == true || heavy.Any(step => step.RunOn.Count == 0),
            [.. heavy.SelectMany(step => step.RunOn).Distinct(StringComparer.OrdinalIgnoreCase)]);
    }

    /// <summary>
    /// What the command starts on a leg of <paramref name="os"/>: what it starts on every leg, and
    /// what the steps that system runs add - a build first among them, where one of those needs it.
    /// </summary>
    /// <param name="os">The leg's operating system.</param>
    public LegWorkload On(string os)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(os);

        // Read as every list of platforms is: ignoring case, as a leg's os is compared everywhere else.
        var here = OnlyOn.Where(start => PlatformScope.Applies(start.RunOn, os)).ToList();
        var built = BuiltBy.Where(cause => PlatformScope.Applies(cause.RunOn, os)).ToList();

        return this with
        {
            Build = Build || built.Count > 0,
            BuiltBy = built,
            Programs = [.. Programs, .. here.Where(start => !start.OwnPath).Select(start => start.Program)],
            UnderOwnPath = [.. UnderOwnPath, .. here.Where(start => start.OwnPath).Select(start => start.Program)],
            OnlyOn = [],
            DeclaredHeavy = DeclaredHeavy || HeavyOnlyOn.Contains(os, StringComparer.OrdinalIgnoreCase),
            HeavyOnlyOn = [],
        };
    }
}

/// <summary>A program a command starts, where, and whether under an environment that sets PATH.</summary>
/// <param name="Program">The program, as the line names it.</param>
/// <param name="OwnPath">Whether it starts under an environment that sets PATH.</param>
/// <param name="RunOn">The operating systems it starts on; empty, every one.</param>
public sealed record OsScopedStart(string Program, bool OwnPath, IReadOnlyList<string> RunOn);
