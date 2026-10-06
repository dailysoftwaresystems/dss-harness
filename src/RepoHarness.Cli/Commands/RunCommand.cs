using System.CommandLine;
using System.Diagnostics;
using RepoHarness.Core.Build;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.Execution;
using RepoHarness.Core.Legs;
using RepoHarness.Core.Output;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Results;
using RepoHarness.Core.Runners;
using RepoHarness.Core.Runs;

namespace RepoHarness.Cli.Commands;

/// <summary>Wires <c>DssHarness run</c>.</summary>
internal static class RunCommand
{
    internal const string Name = RunnerRunService.CommandName;

    private static readonly Argument<string> RunnerArgument = new("runner")
    {
        Description = "The predefined runner to run, as predefinedRunners names it.",
    };

    private static readonly Option<string[]> LegsOption = new("--legs")
    {
        Description = "Only these legs or leg sets: --legs a,b or --legs a b. Without it, the legs the runner declares.",
        AllowMultipleArgumentsPerToken = true,
    };

    private static readonly Option<bool> JsonOption = new("--json")
    {
        Description = "Write the per-leg ledger as JSON.",
    };

    private static readonly Option<bool> TimeOption = new("--time")
    {
        Description = "Pull runTimingRegex out of every step's output.",
    };

    private static readonly Option<bool> ForceLockOption = new("--force-lock")
    {
        Description = "Take a lock a run on another host holds. Always a human decision.",
    };

    private static readonly Option<bool> UseStagedOption = DispatchOptions.UseStaged("Run against");

    private static readonly Option<string[]> InputOption = new(CommandLineInputs.Option)
    {
        Description = "Give one of the action's inputs a value for this run: --input name=value, once per input. Over the runner's .env values and the input's default; never a secret.",
    };

    private static readonly Option<string[]> ManualStepOption = new(StepSelection.Option)
    {
        Description = "Run only these manual steps of the runner's action, each after the steps it needs: --manual-step a,b or --manual-step a --manual-step b. Without it, every step that is not manual, or the steps the runner names.",
        AllowMultipleArgumentsPerToken = true,
    };

    internal static Command Create()
    {
        var command = new Command(
            Name,
            "Run a predefined runner across the legs it declares, with the same isolation, locking, stall bounds, witnesses and reporting build and test get.");

        command.Arguments.Add(RunnerArgument);
        command.Options.Add(LegsOption);
        command.Options.Add(JsonOption);
        command.Options.Add(TimeOption);
        command.Options.Add(ForceLockOption);
        command.Options.Add(UseStagedOption);
        command.Options.Add(InputOption);
        command.Options.Add(ManualStepOption);
        command.Options.Add(DispatchOptions.Here);
        GlobalOptions.AddTo(command);

        command.SetAction(CommandRunner.Wrap(Name, async (context, cancellationToken) =>
        {
            var arguments = context.ParseResult;
            var runnerName = arguments.GetRequiredValue(RunnerArgument);

            IReadOnlyList<string>? named = arguments.GetResult(LegsOption) is { Implicit: false }
                ? arguments.GetValue(LegsOption) ?? []
                : null;

            // Read before anything else is: a pair that gives nothing is a mistake on this line
            // alone, and costs no configuration read to name. So is a --manual-step naming nothing.
            var inputs = CommandLineInputs.Parse(arguments.GetValue(InputOption) ?? []);
            var manualSteps = StepSelection.Names(arguments.GetResult(ManualStepOption) is { Implicit: false }
                ? arguments.GetValue(ManualStepOption) ?? []
                : null);

            var runners = context.Get<IRunnerRunService>();
            var builds = context.Get<IBuildService>();

            var harness = await context.Get<RepoHarness.Core.Repository.IHarnessContextLoader>()
                .LoadAsync(context.Directory, cancellationToken)
                .ConfigureAwait(false);

            var runner = Resolve(harness.Config, runnerName);

            // The steps a run of a runner runs, where it runs an action: before a leg is placed or a host is
            // measured, so that a mistyped action costs nothing and says so in the same terms 'legs' would have.
            async Task<SelectedSteps?> StepsAsync(string name, RunnerConfig declared, IReadOnlyList<string> manual)
            {
                if (declared.Action is not { Length: > 0 } action)
                {
                    return null;
                }

                ActionPath.RequireResolvable(
                    [new KeyValuePair<string, string>(name, action)],
                    harness.Layout.RunnerActionsDirectory,
                    context.Get<IFileSystem>(),
                    context.Get<IHostPlatform>().PathComparison);

                // And read here, not only once a leg is running it. Read per leg, a file carrying
                // one unrecognised key is refused once per leg, each time after that leg's run has
                // begun and its run directory exists: on an eight-leg gate that is eight started
                // runs and eight directories for one typo. The file is the same for every leg, so
                // the question is asked once, where nothing has been created yet.
                var read = await context.Get<IActionFileParser>()
                    .LoadAsync(harness.Layout.RunnerActionsDirectory, action, cancellationToken)
                    .ConfigureAwait(false);

                // Which of its steps this run runs, decided here for the same reason: a manual step
                // mistyped, or a runner naming a step the file lacks, is refused once, before any
                // leg's run has begun. Everything below reads only the steps chosen.
                return StepSelection.For(declared, manual).Apply(name, read);
            }

            var file = await StepsAsync(runnerName, runner, manualSteps).ConfigureAwait(false);

            if (file is null && manualSteps.Count > 0)
            {
                throw new HarnessException(
                    HarnessExit.UsageError,
                    $"Runner '{runnerName}' runs phases of its own, and only an action's steps can be manual, "
                    + $"so {StepSelection.Option} names nothing it could run.");
            }

            // And every value given goes to an input a step this run runs reads, asked here for the
            // same reason: once, before any leg's run has begun.
            CommandLineInputs.RequireRead(runnerName, file, inputs);

            // The runner's own legs when --legs was left out. Resolved here rather than left to the
            // default of every declared leg, because running a benchmark on hosts nobody meant to
            // measure is not what "no --legs" asks for. Every declared leg only where the runner
            // leaves its legs out: a list given empty was refused when the file was read, and one a caller
            // builds empty reads as left out.
            var selected = named ?? (runner.Legs is { Count: > 0 } declared ? declared : null);

            // A leg whose operating system no step runs on would run nothing and pass, and one on which
            // none of the steps this run named runs would run only what they need and pass. Refused here,
            // like a mistyped action, before a host is measured and naming every such leg at once,
            // rather than once per leg after each one's run has begun.
            var legs = LegSelection.Resolve(harness.Config, selected).Legs;
            var reached = legs.Select(leg => (leg.Name, leg.Leg.Os)).ToList();

            file?.File.RequireAStepOn(runnerName, reached);
            file?.RequireANamedStepOn(runnerName, reached);

            // The runners its expected exceptions' run checks name, which run within its legs, each with the steps a run of
            // it that names none runs: one among them that needs the build builds the legs first, since a check runs on the
            // leg as this runner left it, and a heavy one makes them heavy, as this runner would. Read only to know those:
            // one whose steps cannot be read counts heavy, said here, builds nothing, and is refused by the run check that
            // runs it, as it always was, rather than refusing a run whose checks may never run.
            var checks = new List<(string Name, RunnerConfig Runner, ActionFile? Action, bool Unread)>();

            foreach (var check in runner.ExpectedExceptions
                .SelectMany(entry => entry.RunChecks)
                .Select(check => check.PredefinedRunner)
                .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var checkRunner = Resolve(harness.Config, check);

                try
                {
                    checks.Add((check, checkRunner, (await StepsAsync(check, checkRunner, []).ConfigureAwait(false))?.File, false));
                }
                catch (HarnessException ex)
                {
                    context.Get<IHarnessOutput>().Warn(
                        Name,
                        $"run check '{check}' of runner '{runnerName}' could not be read to know how heavy it is or whether it "
                        + $"needs the build, so the legs of '{runnerName}' count as heavy, and are not built for it: {ex.Message}");
                    checks.Add((check, checkRunner, null, true));
                }
            }

            // Built only where the runner, or a runner its run checks name, requires the build or runs a step or
            // phase naming {product} or {buildDir}, and never tested: a host needs cmake for a runner that measures
            // a build product, and not for one that only runs a script. Decided once, here, for every leg: whether
            // a leg builds is this workload read for its system.
            var workload = LegWorkload.ForRunner(runner, file?.File, checks);

            // A leg it would build that cannot be built, and a {product} this runner's steps or phases name that no one
            // declared file fills in, refused here as a leg that would run nothing is: before a host is measured, naming
            // every such leg and what builds it.
            workload.RequireBuildable(harness.Config, runnerName, legs);

            return await context.Get<LegRunService>()
                .RunAsync(
                    Name,
                    new LegRunRequest(
                        context.Directory,
                        selected,
                        arguments.GetValue(ForceLockOption),
                        arguments.GetValue(JsonOption),
                        arguments.GetValue(UseStagedOption),
                        arguments.GetValue(TimeOption),
                        arguments.GetValue(DispatchOptions.Here),
                        RunnerRunService.RemoteArguments(runnerName, arguments.GetValue(TimeOption), inputs, manualSteps))
                    {
                        Workload = workload,
                    },
                    (work, token) => RunLegAsync(runners, builds, runnerName, inputs, manualSteps, workload, work, token),
                    cancellationToken)
                .ConfigureAwait(false);
        }, JsonOption));

        return command;
    }

    private static RunnerConfig Resolve(HarnessConfig config, string runnerName)
    {
        var declared = DeclaredName.In(config.PredefinedRunners.Keys, runnerName);

        return declared is null
            ? throw new HarnessException(
                HarnessExit.UsageError,
                $"'{runnerName}' is not declared under predefinedRunners. Declared: "
                + $"{string.Join(", ", config.PredefinedRunners.Keys.Order(StringComparer.Ordinal))}.")
            : config.PredefinedRunners[declared];
    }

    private static async Task<LegEntry> RunLegAsync(
        IRunnerRunService runners,
        IBuildService builds,
        string runnerName,
        IReadOnlyDictionary<string, string> inputs,
        IReadOnlyList<string> manualSteps,
        LegWorkload workload,
        LegWork work,
        CancellationToken cancellationToken)
    {
        var leg = work.Leg;
        var config = work.Context.Config;
        var runner = Resolve(config, runnerName);
        var started = Stopwatch.GetTimestamp();

        // Built before the runner starts where the workload says so for this leg's system: the runner,
        // or a runner its run checks name, requires the build or runs a step or phase naming what the
        // build makes - {product} or {buildDir} - on this leg's system, whether or not a check ever
        // runs. Unbuilt, such a step would read whatever was left there last time: a file that is not
        // there, or one an older commit built. Only a leg that builds names the compilers: one that
        // does not may never touch the build.
        IReadOnlyList<CompilerFact> compilers = [];

        // What the build says beyond its verdict, which the leg's line carries as the build's own does.
        IReadOnlyList<string> built = [];

        var building = workload.On(leg.Leg.Os).Build;

        if (building)
        {
            var build = await builds
                .BuildAsync(config, leg.BuildRequestFor(config, work.RunDirectory), cancellationToken)
                .ConfigureAwait(false);

            compilers = build.Compilers;
            built = build.Notes;

            if (build.Verdict.Verdict != LegVerdict.Passed)
            {
                return new LegEntry
                {
                    Leg = leg.Name,
                    Verdict = build.Verdict.Verdict,
                    Detail = build.Verdict.Detail,
                    Duration = Stopwatch.GetElapsedTime(started),
                    Emulated = leg.Emulated,
                    TimingNotes = built,
                    Compilers = compilers,
                    LogTail = build.Tail,
                };
            }
        }

        var result = await runners
            .RunAsync(
                config,
                RequestFor(work, runnerName, runner, building) with
                {
                    Time = work.Time,

                    // Only the runner the command line named: the values were checked against its
                    // action's inputs, and a runner a check starts reads its own. So were the manual
                    // steps, and a runner a check starts runs its own steps.
                    Inputs = inputs,
                    ManualSteps = manualSteps,
                    NamedOnCommandLine = true,

                    // One level deep by construction: the runner a check names carries no checks of
                    // its own, and this delegate reaches the service only for that one.
                    InvokeRunner = (name, token) => ConfirmAsync(runners, config, work, name, building, token),
                },
                cancellationToken)
            .ConfigureAwait(false);

        return result.Entry with
        {
            Duration = Stopwatch.GetElapsedTime(started),
            TimingNotes = [.. built, .. result.Entry.TimingNotes],
            Compilers = compilers,
        };
    }

    /// <summary>
    /// Runs the runner a check names, for the gate that confirms an expected exception.
    /// </summary>
    /// <remarks>
    /// One level deep, and enforced here as well as by the configuration: the runner this reaches is
    /// given no way to invoke another, so even a configuration that slipped past validation cannot
    /// make a check confirm itself. A runner reached this way never builds the leg: the runner
    /// carrying the check built it first where the check needs the build, and rebuilding it mid-run
    /// would replace the binaries the failure being explained came from.
    /// </remarks>
    private static async Task<RunOutcome> ConfirmAsync(
        IRunnerRunService runners,
        HarnessConfig config,
        LegWork work,
        string runnerName,
        bool built,
        CancellationToken cancellationToken)
    {
        var result = await runners
            .RunAsync(config, RequestFor(work, runnerName, Resolve(config, runnerName), built), cancellationToken)
            .ConfigureAwait(false);

        return result.Outcome;
    }

    /// <summary>
    /// A run of <paramref name="runner"/> on the leg <paramref name="work"/> carries, with what the
    /// leg's host declares for it and whether this run built the leg. Made once, for the runner a leg
    /// runs and for the one a check names, so what the host declares reaches both or neither, and so
    /// does whether the leg was built: a step reading the build is refused on a leg that was not.
    /// </summary>
    private static RunnerRunRequest RequestFor(LegWork work, string runnerName, RunnerConfig runner, bool built)
    {
        var leg = work.Leg;

        // Derived from the very directory this request carries, so a run line naming {product} and
        // one naming {buildDir} cannot come from two different tree roots. They do differ: a leg
        // placed on another machine re-invokes this there, where its own tree is the one that
        // resolves.
        var buildDirectory = leg.Variant.DirectoryUnder(leg.TreeRoot);
        var (product, productProblem) = leg.ProductFor(buildDirectory);

        return new RunnerRunRequest
        {
            ProgramDirectories = leg.Host.ProgramDirectories,
            HostEnvironment = leg.Environment,
            RunnerName = runnerName,
            Runner = runner,
            Leg = leg.Name,
            Layout = work.Context.Layout,
            RunId = work.RunId.Value,

            // A new segment each attempt, so resuming a run cannot record its work into the
            // attempt it is resuming.
            SegmentId = Guid.NewGuid().ToString("N")[..8],
            TreeRoot = leg.TreeRoot,
            WorkingDirectory = leg.TreeRoot,
            BuildDirectory = buildDirectory,
            Built = built,
            Identity = leg.IdentityFor(work.RunId.Value),
            Product = product,
            ProductProblem = productProblem,
            ResolvedLegs = [leg.Name],
            Emulated = leg.Emulated,
        };
    }
}
