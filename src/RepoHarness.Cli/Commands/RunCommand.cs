using System.CommandLine;
using System.Diagnostics;
using RepoHarness.Core.Build;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.Execution;
using RepoHarness.Core.Legs;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Hosts;
using RepoHarness.Core.Output;
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
        Description = "Take a lock another run holds, on this host or another. Always a human decision.",
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

    private static readonly Option<bool> LocalCopyOption = DispatchOptions.LocalCopy;

    private static readonly Option<bool> InPlaceOption = DispatchOptions.InPlace;

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
        command.Options.Add(LocalCopyOption);
        command.Options.Add(InPlaceOption);
        command.Options.Add(DispatchOptions.Here);
        GlobalOptions.AddTo(command);

        command.SetAction(CommandRunner.Wrap(Name, async (context, run, cancellationToken) =>
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
            // phase naming what the build makes - {product}, {buildDir} or one of the leg's compilers - and never
            // tested: a host needs cmake for a runner that measures a build product, and not for one that only runs
            // a script. Decided once, here, for every leg: whether a leg builds is this workload read for its system.
            var workload = LegWorkload.ForRunner(runner, file?.File, checks);

            // A leg it would build that cannot be built, a {product} this runner's steps or phases name that no one
            // declared file fills in, and a compiler they name on a leg no build identifies one for, refused here as
            // a leg that would run nothing is: before a host is measured, naming every such leg and what builds it.
            workload.RequireBuildable(harness.Config, runnerName, legs);

            var toolchains = context.Get<CMakeToolchainReader>();

            // Where this run's own legs work, as the command's two options say - and as the runner says too: one that
            // writes the tree works in place whatever the leg's own key or the repository's default says, for what it
            // wrote in a copy the next sync would make the tree again. Both refusals are made here, as a leg that
            // would run nothing is refused: before a host is measured, naming the leg it refuses. A run check's runner
            // runs within the leg's own work, so it writes wherever the leg works and is held to the key's contract
            // the same way.
            var where = DispatchOptions.WhereWorkRuns(arguments);
            var platform = context.Get<IHostPlatform>();
            var here = arguments.GetValue(DispatchOptions.Here) is not null;

            if (runner.WritesTree)
            {
                if (where == LocalTrees.Copy)
                {
                    throw new HarnessException(
                        HarnessExit.UsageError,
                        $"Runner '{runnerName}' writes the tree (writesTree), which --local-copy would have it write "
                        + "in a copy. Leave --local-copy out.");
                }

                foreach (var leg in legs.Where(leg => !RunsHere(platform, harness.Config, leg.Leg, here)))
                {
                    if (WhereItWouldRun(harness.Config, leg.Leg) is not { } on)
                    {
                        continue;
                    }

                    throw new HarnessException(
                        HarnessExit.Refused,
                        $"Runner '{runnerName}' writes the tree (writesTree), and leg '{leg.Name}' would work in a copy of "
                        + $"it on {on}, which the next sync makes the tree again. Nothing was run. "
                        + "Run it on a leg this machine runs itself.");
                }

                where = LocalTrees.InPlace;
            }

            foreach (var check in checks.Where(check => check.Runner.WritesTree))
            {
                var who = $"Run check '{check.Name}' of runner '{runnerName}'";

                if (where == LocalTrees.Copy)
                {
                    throw new HarnessException(
                        HarnessExit.UsageError,
                        $"{who} writes the tree (writesTree), which --local-copy would have it write in a copy. "
                        + "Leave --local-copy out.");
                }

                foreach (var leg in legs.Where(leg => !RunsHere(platform, harness.Config, leg.Leg, here)))
                {
                    if (WhereItWouldRun(harness.Config, leg.Leg) is not { } on)
                    {
                        continue;
                    }

                    throw new HarnessException(
                        HarnessExit.Refused,
                        $"{who} writes the tree (writesTree), and leg '{leg.Name}' would work in a copy of it on "
                        + $"{on}, which the next sync makes the tree again. Nothing was run. "
                        + "Run it on a leg this machine runs itself.");
                }

                // The key alone does not decide where a copy would be made: only a leg this machine runs itself works
                // in one, and only where the placement's own rule says so - LegTrees.InLocalCopy, the rule LegTrees.For
                // places a leg by, asked whole so the two cannot differ. A leg typed in a synced copy, or sent here by
                // another machine, works in the tree itself whatever its key says, so the check is not refused there;
                // and a leg no host can run works nowhere, so the copy this refusal exists to keep the check's writes
                // out of is never made - the run says so its own way, not over its localCopy key.
                foreach (var leg in legs.Where(leg => RunsHere(platform, harness.Config, leg.Leg, here)
                        && LegTrees.InLocalCopy(harness, leg.Leg, arguments.GetValue(DispatchOptions.Here), where)))
                {
                    throw new HarnessException(
                        HarnessExit.Refused,
                        $"{who} writes the tree (writesTree), and leg '{leg.Name}' would work in a copy of it, as its own "
                        + "localCopy key or the repository's default says. Nothing was run. Run it with --in-place.");
                }
            }

            return await context.Get<LegRunService>()
                .RunAsync(
                    Name,
                    run,
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
                        LocalTrees = where,
                    },
                    (work, token) => RunLegAsync(runners, builds, toolchains, runnerName, inputs, manualSteps, workload, work, token),
                    cancellationToken)
                .ConfigureAwait(false);
        }, JsonAnswer.Ledger(JsonOption)));
        return command;
    }

    /// <summary>
    /// Whether this machine would run <paramref name="leg"/> itself, as the placement's own rule answers it: a leg
    /// pinned to a host is that host's alone, and of the rest this machine takes one whose system and processor are its
    /// own. An emulator's check is the placement's to ask of the machine after this refusal, so an emulated leg of this
    /// machine's system is not refused here.
    /// </summary>
    private static bool RunsHere(IHostPlatform platform, HarnessConfig config, LegConfig leg, bool here)
    {
        if (!LegPlacement.Candidates(config, leg, here).Contains(HostId.Local))
        {
            return false;
        }

        return leg.Emulator is not null
            ? string.Equals(leg.Os, platform.PlatformKey, StringComparison.OrdinalIgnoreCase)
            : LegPlacement.PlatformObstacle(
                leg,
                new HostReport { Host = HostId.Local, Os = platform.PlatformKey, Processor = platform.Processor })
                is null;
    }

    /// <summary>
    /// The first host that would run <paramref name="leg"/> where this machine cannot, as a line names it, or
    /// <see langword="null"/> where the configuration names none: a leg nothing can run is not refused over where it
    /// would work, for the run says so its own way.
    /// </summary>
    private static string? WhereItWouldRun(HarnessConfig config, LegConfig leg)
        => LegPlacement.Candidates(config, leg).FirstOrDefault(host => host != HostId.Local)?.ToString();

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
        CMakeToolchainReader toolchains,
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
        // build makes - {product}, {buildDir} or one of the leg's compilers - on this leg's system,
        // whether or not a check ever runs. Unbuilt, such a step would read whatever was left there last time: a file that is not
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
                RequestFor(work, runnerName, runner, building, toolchains) with
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
                    InvokeRunner = (name, token) => ConfirmAsync(runners, toolchains, config, work, name, building, token),
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
        CMakeToolchainReader toolchains,
        HarnessConfig config,
        LegWork work,
        string runnerName,
        bool built,
        CancellationToken cancellationToken)
    {
        var result = await runners
            .RunAsync(config, RequestFor(work, runnerName, Resolve(config, runnerName), built, toolchains), cancellationToken)
            .ConfigureAwait(false);

        return result.Outcome;
    }

    /// <summary>
    /// A run of <paramref name="runner"/> on the leg <paramref name="work"/> carries, with what the
    /// leg's host declares for it and whether this run built the leg. Made once, for the runner a leg
    /// runs and for the one a check names, so what the host declares reaches both or neither, and so
    /// does whether the leg was built: a step reading the build is refused on a leg that was not.
    /// </summary>
    private static RunnerRunRequest RequestFor(LegWork work, string runnerName, RunnerConfig runner, bool built, CMakeToolchainReader toolchains)
    {
        var leg = work.Leg;

        // Derived from the very directory this request carries, so a run line naming {product} and
        // one naming {buildDir} cannot come from two different tree roots. They do differ: a leg
        // placed on another machine re-invokes this there, where its own tree is the one that
        // resolves.
        var buildDirectory = leg.Variant.DirectoryUnder(leg.HostTreeRoot);
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
            RunDirectory = work.RunDirectory,

            // A new segment each attempt, so resuming a run cannot record its work into the
            // attempt it is resuming.
            SegmentId = Guid.NewGuid().ToString("N")[..8],
            TreeRoot = leg.HostTreeRoot,
            WorkingDirectory = leg.HostTreeRoot,
            BuildDirectory = buildDirectory,
            Beside = leg.Beside,
            Built = built,
            Identity = leg.IdentityFor(work.RunId.Value),
            Product = product,
            ProductProblem = productProblem,

            // From that same directory, and read only where a run line names a compiler: what the build this run made
            // there identified, for the runner the command line named and for each a run check starts alike.
            Compilers = toolchains.NamedFor(leg.Project, buildDirectory),
            ResolvedLegs = [leg.Name],
            Emulated = leg.Emulated,
        };
    }
}
