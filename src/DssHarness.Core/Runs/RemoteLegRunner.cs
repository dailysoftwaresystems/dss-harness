using System.Text.Json;
using System.Text.Json.Serialization;
using DssHarness.Core.Execution;
using DssHarness.Core.Hosts;
using DssHarness.Core.Output;
using DssHarness.Core.Processes;
using DssHarness.Core.Results;
using DssHarness.Core.Testing;

namespace DssHarness.Core.Runs;

/// <summary>
/// Runs one leg on the host it was placed on, by asking the DssHarness already installed there.
/// </summary>
/// <remarks>
/// A leg placed on a WSL distribution or an ssh host has to run there, or its verdict describes the
/// machine that typed the command rather than the machine the leg names — and a green result that
/// describes the wrong machine is the whole failure this tool exists to prevent. So the work is not
/// reproduced over a transport: the host's own DssHarness runs the same command in the copy sync put
/// there, and hands its ledger back as JSON. Both ends are already the same build, which the host
/// inspection established before anything started.
/// The request carries <c>--here</c> with the host's own name, so the host runs the leg on itself,
/// never dispatches it onward, and runs it with the settings this machine's configuration gives that
/// host. One hop, always, whatever its configuration says about other machines.
/// </remarks>
public sealed class RemoteLegRunner(IHostCommandRunner hostCommands, IHarnessOutput output)
{
    /// <summary>
    /// The option that tells a DssHarness to run every selected leg on the machine it is running on,
    /// as the host it names.
    /// </summary>
    /// <remarks>
    /// Hidden, because nobody types it: it exists so the host that was asked to run a leg cannot
    /// decide to ask a third machine, which would place the verdict one further hop from the reader
    /// and could not terminate by construction. It names the host as this machine knows it, because
    /// to itself the host is 'local', and 'local' in the configuration the two share is this machine.
    /// </remarks>
    public const string HereOption = "--here";

    /// <summary>
    /// The most lines a host's failure line is held with, until its command has said whether it ended there. A failure is
    /// the last thing a command says, and no failure of this tool's runs to this many lines; one followed by more was
    /// printed by the command's own work - a run of this tool inside a test suite prints one - and holding everything after
    /// it would hold the rest of what the run prints, which under --verbose is every line of every step.
    /// </summary>
    public const int MostHeldLines = 200;

    /// <summary>
    /// How many times a leg's command is started once more on its host where the DssHarness there failed as a defect
    /// before anything ran (<see cref="HostAnswer.FailedBeforeAnythingRan"/>): safe, since nothing ran, and enough for a
    /// tool or a runtime replaced there while it started - a consumer's leg met a DssHarness in a WSL distribution that
    /// could not load one of the runtime's own assemblies, once, and the next run was fine. Never once anything ran there.
    /// </summary>
    public const int MostRestarts = 1;

    private static readonly JsonSerializerOptions LedgerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly IHostCommandRunner _hostCommands = hostCommands;
    private readonly IHarnessOutput _output = output;

    /// <summary>
    /// Runs <paramref name="commandName"/> for one leg on its host, and returns its entry. A leg of <paramref name="run"/>
    /// asking the host's heavy-leg slots there is recorded under that run, so the legs of one command wait for each
    /// other's slots without that wait counting.
    /// </summary>
    /// <param name="commandName">The command to run there, which is the one running here.</param>
    /// <param name="leg">
    /// The placed leg, whose work another machine's process runs (<see cref="PlacedLeg.Dispatched"/>): its host, and the
    /// host's copy of its tree, which sync made, that it runs in.
    /// </param>
    /// <param name="arguments">
    /// The command's own options, then the leg's own - each text, or bytes that cross as their base64 text. Never
    /// <c>--legs</c>, <c>--json</c>, <c>--here</c> or <c>--verbose</c>, which this adds.
    /// </param>
    /// <param name="run">The run the leg is part of; <see langword="null"/> for a command that runs no leg of a run.</param>
    /// <param name="onNote">Told each whole note the host's command writes while it runs, as it arrives; never relayed; <see langword="null"/> where this reads none.</param>
    /// <param name="setUpsLeftRunning">
    /// Told each set-up the leg left running on the host where it ended there with no line to carry it, so this run
    /// says it as the host's did; <see langword="null"/> where nothing is told.
    /// </param>
    /// <param name="cancellationToken">Stops the command on the host as well as here.</param>
    /// <returns>
    /// The leg's line from the host's ledger; or, where the host's DssHarness failed as a defect with no line for the leg,
    /// a <c>poisoned</c> line naming the host, the code and what it said. A DssHarness there that failed so before
    /// anything ran is started once more first (<see cref="MostRestarts"/>), and the line says what each such start said
    /// (<see cref="LegEntry.Retried"/>), whatever the leg then reached.
    /// </returns>
    /// <exception cref="HarnessException">
    /// The host could not be reached - its transport would not start - or never reported how the
    /// command finished, or reported a ledger this build cannot read, or ended its command with no line
    /// for the leg and a code that is neither a refusal of the run nor a defect of its own. None is a
    /// verdict about the code, so none is reported as one. Or the command refused the whole run there
    /// with no line for the leg, which is raised as that refusal, with what the host said; one that
    /// came with the leg's line is carried on it (<see cref="LegEntry.EndsTheRun"/>), and ends the run
    /// once the line is recorded.
    /// </exception>
    public async Task<LegEntry> RunAsync(
        string commandName,
        PlacedLeg leg,
        IReadOnlyList<HostArgument> arguments,
        RunId? run = null,
        Action<string, string>? onNote = null,
        Action<IReadOnlyList<SetUps.SetUpFact>>? setUpsLeftRunning = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandName);
        ArgumentNullException.ThrowIfNull(leg);
        ArgumentNullException.ThrowIfNull(arguments);

        var session = leg.Host.Session ?? throw new HarnessException(
            HarnessExit.HostUnavailable,
            $"{leg.Host.Host} was not reached, so leg '{leg.Name}' cannot run there: "
            + $"{leg.Host.Reason ?? "no reason was recorded"}.");

        // What each start that failed before anything ran said, in order: said on the leg's line whatever it then reaches.
        var restarted = new List<string>();
        var answer = await AskAsync(commandName, leg, session, arguments, run, onNote, cancellationToken).ConfigureAwait(false);

        try
        {
            // Started once more only where the host's own DssHarness failed before anything ran there, which is safe to
            // repeat: a tool or a runtime replaced on the host while it started fails that way once. Never once anything
            // ran there, whatever then failed: a second start would run it twice.
            while (answer.FailedBeforeAnythingRan && restarted.Count < MostRestarts)
            {
                var said = answer.SaidAsItStarted(leg);

                restarted.Add(said);
                _output.Warn(commandName, $"{leg.Name}: {said}; starting the leg there once more");

                answer = await AskAsync(commandName, leg, session, arguments, run, onNote, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (HarnessException ex) when (restarted.Count > 0)
        {
            // What the earlier start said is not left behind by what ended the next one: a host not reached the second time
            // is still a host whose DssHarness failed the first.
            throw new HarnessException(ex.ExitCode, $"{ex.Message} (retried {Times(restarted.Count)}: {string.Join("; ", restarted)})", ex);
        }

        var entry = Read(answer, leg, session.Connection, commandName, setUpsLeftRunning);

        return restarted.Count == 0 ? entry : entry with { Retried = restarted };
    }

    /// <summary><c>once</c>, or how many times.</summary>
    private static string Times(int count) => count == 1 ? "once" : $"{count} times";

    /// <summary>
    /// Starts <paramref name="commandName"/> for one leg on its host once, relaying what it prints, and answers how it
    /// finished, what it said as it failed and what of its own it said on the way.
    /// </summary>
    /// <exception cref="HarnessException">
    /// The host never reported how the command finished, or reported a ledger this build cannot read.
    /// </exception>
    private async Task<HostAnswer> AskAsync(
        string commandName,
        PlacedLeg leg,
        HostSession session,
        IReadOnlyList<HostArgument> arguments,
        RunId? run,
        Action<string, string>? onNote,
        CancellationToken cancellationToken)
    {
        var nonce = HostAgentProtocol.NewNonce();

        // The leg's own command verbose where this one is: what -v shows - each step's output - is that command's to
        // write, and the agent's own --verbose below reaches the agent alone, so a leg on a host printed its verdict and
        // nothing else while a leg here streamed its steps. Written to the host's standard error under --json, it is
        // relayed from there like every line of it.
        string[] verbosity = _output.IsVerbose ? [HostAgentProtocol.VerboseOption] : [];

        var request = new HostAgentRequest
        {
            Kind = HostAgentRequestKind.Run,
            Directory = leg.HostTreeRoot,
            Arguments = [commandName, "--legs", leg.Name, "--json", HereOption, leg.Host.Host.ToString(), .. verbosity, .. arguments],
            Nonce = nonce,
            RunId = run?.Value,

            // The drive a WSL distribution's disk grows on, which the leg's build fills, and which only this machine sees.
            DiskImageDrive = leg.Host.Host.Kind == HostKind.Wsl ? leg.Host.DiskImageSpace?.Filesystem : null,

            // What this machine's settings file declares of its admission, which holds a WSL leg's build to its floor: the
            // distribution's own file is another machine's.
            MachineAdmission = leg.Host.Host.Kind == HostKind.Wsl ? leg.Host.MachineSettings?.Admission : null,
            // The distribution each WSL item read here names, which the leg's run lines may name there, where no item is.
            Distributions = leg.Distributions
                .Where(told => told.Value is { Length: > 0 })
                .ToDictionary(told => told.Key, told => told.Value!, StringComparer.OrdinalIgnoreCase),
        };

        var ledger = new System.Text.StringBuilder();
        var lines = new HostAgentLines(nonce, onNote);
        var failure = new List<string>();

        // The lines failure was read from, as they would be shown, held until the host has finished: see below.
        var held = new List<string>();

        // The host's own run, which its command names as it begins: said once, as this leg's, and never relayed.
        RunId? hostRun = null;

        // A transport that will not start leaves the host unavailable, raised as that by the runner
        // that starts it.
        var result = await _hostCommands.RunAsync(
                session.Connection,
                new HostCommand
                {
                    Program = session.ToolPath,
                    Arguments = _output.IsVerbose
                        ? [HostAgentProtocol.CommandName, HostAgentProtocol.VerboseOption]
                        : [HostAgentProtocol.CommandName],
                    StandardInput = HostAgentProtocol.Input(request),
                    HoldStandardInputOpen = true,
                    StandardInputBeat = HostAgentProtocol.BeatOf(request),

                    // Bound as a local leg's phase is: what reaches the host ends with this process on Windows, and
                    // under a run's machine step is out of reach of the engine's terminal and recorded while it runs,
                    // so a safe pause leaves the host's leg running and a killed engine leaves nothing unrecorded.
                    Bound = true,

                    // What the host's command prints is relayed line by line, under --verbose every line of
                    // every step, and the end of it is all the result keeps; its ledger arrives whole, a
                    // line at a time, and is kept below.
                    ErrorKept = StreamKept.Tail,

                    // Kept rather than echoed: the host writes its ledger to standard output, and
                    // this end reports one ledger for the whole run rather than one per machine.
                    OnOutputLine = line =>
                    {
                        if (lines.Output(line))
                        {
                            ledger.AppendLine(line);
                        }
                    },
                    OnErrorLine = line =>
                    {
                        if (!lines.Error(line))
                        {
                            return;
                        }

                        // Under the name the configuration declares, before it is either kept or shown: ssh
                        // names the address it dialled, one the host's name resolved to, and the kept copy is
                        // the one that reaches the leg's reason, the ledger and --json.
                        line = HostProbes.AsConfigured(line, session.Connection);

                        // The host's own run line, said here as this leg's - its leg before it, its host after it - and
                        // never relayed: one command says one run of its own, not two. Any later one is a program's own
                        // output - a suite that runs this tool prints one - and is relayed as it comes, below.
                        if (hostRun is null && RunLine.TryRead(line, commandName, out var begun))
                        {
                            hostRun = begun;
                            _output.Info(commandName, RunLine.OfLeg(leg.Name, begun, leg.Host.Host));
                            return;
                        }

                        // Kept, from the last failure line to the end: a command that refuses before any leg has a
                        // verdict leaves no entry, and its failure is then all it said about why - from its failure
                        // line on, because a message runs over several lines and git's own fix is on the last of them.
                        // The host's agent fails in the same form under its own name, when it could not start the
                        // command at all. Held rather than shown until it is known to be no conclusion of the host's:
                        // a failure line followed by another, which the command's own work printed, or one before a
                        // command that ended well, which ends without one.
                        if (FailureLine.TryRead(line, commandName, out var said)
                            || FailureLine.TryRead(line, HostAgentProtocol.CommandName, out said))
                        {
                            Show(held);
                            failure.Clear();
                            failure.Add(said);
                            held.Add(line);
                        }
                        else if (failure.Count > 0)
                        {
                            failure.Add(line);
                            held.Add(line);

                            // Past what any conclusion runs to, it is known to be none: shown after all, in
                            // order, and what follows is shown as it comes.
                            if (held.Count > MostHeldLines)
                            {
                                Show(held);
                                failure.Clear();
                            }
                        }
                        else
                        {
                            _output.RawError(line);
                        }
                    },
                },
                cancellationToken)
            .ConfigureAwait(false);

        // A command that ended badly ends on its failure line, with what follows it, and that is the host's conclusion:
        // this machine says how the leg ended itself - by the leg's own line where the host's ledger has an entry for
        // it, and in the refusal raised from what the host said where the host refused the run, with a line or with
        // none - so it is never shown. Shown as well, it was said twice, and a host's summary of its one leg read as
        // this run's. Anything else held is shown, late.
        if (lines.Finished is null or HarnessExit.Success)
        {
            Show(held);
        }

        if (HostProbes.BeatLost(result) is { } unheard)
        {
            _output.Warn(commandName, $"{leg.Host.Host}: {unheard}");
        }

        if (lines.Finished is not { } finished)
        {
            // Never a failed verdict: the command may not have run, or run only in part, and
            // reporting that as a red leg would blame the code for a connection.
            throw new HarnessException(
                HarnessExit.HostUnavailable,
                $"{leg.Host.Host}: {HostProbes.NeverFinished($"'{commandName}' for leg '{leg.Name}'", result, session.Connection)}");
        }

        return new HostAnswer(
            Parse(ledger.ToString(), leg, commandName),
            finished,
            failure.Count == 0 ? null : string.Join(Environment.NewLine, failure),
            hostRun,
            lines.Noted);
    }

    /// <summary>Shows each line <paramref name="held"/> holds, in order, and holds none after.</summary>
    private void Show(List<string> held)
    {
        foreach (var line in held)
        {
            _output.RawError(line);
        }

        held.Clear();
    }

    /// <summary>The ledger the host wrote, or <see langword="null"/> where it wrote none.</summary>
    /// <remarks>
    /// The whole of standard output is the document. The host was asked with <c>--json</c>, which
    /// sends its progress to standard error precisely so that nothing shares this channel: hunting
    /// for the first <c>{</c> instead would find the one inside a compiler message or a test name
    /// that a progress line had already carried here, and report a leg that reached a verdict as a
    /// host that could not be reached.
    /// </remarks>
    /// <param name="output">Everything the host wrote to standard output.</param>
    /// <param name="leg">The leg asked for.</param>
    /// <param name="commandName">The command the host ran.</param>
    /// <exception cref="HarnessException">The host wrote a ledger this build cannot read.</exception>
    private static RemoteLedger? Parse(string output, PlacedLeg leg, string commandName)
    {
        var document = output.Trim();

        if (document.Length == 0)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<RemoteLedger>(document, LedgerOptions);
        }
        catch (JsonException ex)
        {
            throw new HarnessException(
                HarnessExit.HostUnavailable,
                $"{leg.Host.Host} answered '{commandName}' with a ledger this build cannot read: {ex.Message}");
        }
    }

    /// <summary>Reads the one leg's entry out of what the host answered.</summary>
    /// <param name="answer">How the host's command finished, and what it wrote.</param>
    /// <param name="leg">The leg asked for.</param>
    /// <param name="connection">The connection the host answered over.</param>
    /// <param name="commandName">The command the host ran.</param>
    /// <param name="setUpsLeftRunning">Told each set-up the leg left running there where it ended with no line.</param>
    /// <exception cref="HarnessException">
    /// The host wrote no entry for the leg. A refusal of the whole run there is raised as that same
    /// refusal; anything else but a defect of the host's own DssHarness as a host that said nothing about
    /// the leg. A refusal that came with an entry is no exception here: the entry carries it, and so is a
    /// defect, which is the leg's <c>poisoned</c> line.
    /// </exception>
    private static LegEntry Read(
        HostAnswer answer,
        PlacedLeg leg,
        HostConnection connection,
        string commandName,
        Action<IReadOnlyList<SetUps.SetUpFact>>? setUpsLeftRunning = null)
    {
        var (ledger, exitCode, failure) = (answer.Ledger, answer.Finished, answer.Failure);
        var entry = ledger?.Legs?.FirstOrDefault();

        // A set-up the leg left running there, where it ended with no line to carry it: it runs on after both runs, so
        // this run says it as the host's did, whatever this machine makes of the rest.
        if (entry is null && ledger?.SetUpsLeftRunning?.Where(left => string.Equals(left.Leg, leg.Name, StringComparison.OrdinalIgnoreCase)).ToList() is { Count: > 0 } left)
        {
            setUpsLeftRunning?.Invoke([.. left.Select(setUp => new SetUps.SetUpFact(setUp.SetUp, setUp.Up ?? SetUps.SetUpFact.BroughtUp, SetUps.SetUpFact.Failed, setUp.Detail, setUp.Inputs))]);
        }

        if (entry is null)
        {
            // A refusal the host made is this run's refusal. A configuration, a command line or a
            // policy its copy cannot satisfy is the same fact there as here, and the same refusal on
            // this machine stops the run; read as a host that could not be reached, it became a
            // skipped leg and a run that merely looked incomplete, with its reason and its fix left
            // behind on the host's error stream.
            if (HarnessExit.RefusesTheRun(exitCode))
            {
                throw Refusal(leg, commandName, exitCode, failure);
            }

            // A defect of the host's own DssHarness is the harness failing, which is what poisoned means: read as a host
            // that could not be reached, it was a skip, and a gate that accepts an incomplete run passed a leg whose tool
            // broke there. Named with the host, the code and what it said, and with whether anything had run by then.
            if (exitCode == HarnessExit.InternalError)
            {
                return new LegEntry
                {
                    Leg = leg.Name,
                    Verdict = LegVerdict.Poisoned,
                    Detail = answer.FailedBeforeAnythingRan
                        ? $"{leg.Host.Host} ran '{commandName}' for leg '{leg.Name}', and its DssHarness failed as it started, "
                            + $"before anything ran there: it exited {exitCode}, {Saying(failure)}"
                        : $"{leg.Host.Host} ran '{commandName}' for leg '{leg.Name}', and its DssHarness failed once its work "
                            + $"there had begun: it exited {exitCode} without a ledger entry for it, {Saying(failure)}",
                    Emulated = leg.Emulated,
                };
            }

            // The exit code is named because it may be the only thing the host did say. A copy with
            // no configuration, a command line the host refused, a request its agent turned away
            // before starting the command: each ends with its own code and no line for this leg - a
            // ledger with no legs, or none at all - and without the code every one of them reads as
            // the same shrug.
            throw new HarnessException(
                HarnessExit.HostUnavailable,
                $"{leg.Host.Host} ran '{commandName}' for leg '{leg.Name}' and exited {exitCode} without "
                + "a ledger entry for it, "
                + (failure is null ? "so nothing there said what happened." : $"saying: {failure}"));
        }

        var verdict = Verdicts.Parse(entry.Verdict) ?? LegVerdict.Poisoned;

        // What the host's own work printed is shown under the name the configuration declares, as every line the host
        // writes is: a phase's last lines reach the reader on its standard error too, under --verbose, and say it there.
        var detail = HostProbes.AsConfigured(entry.Detail ?? string.Empty, connection);

        return new LegEntry
        {
            Leg = leg.Name,
            Verdict = verdict,

            // A refusal of the run the host made once the leg had its line - a sweep that had judged arms by then - is
            // this run's refusal as one with no line is: the line is this leg's, and the refusal ends the run after it,
            // in the host's own words. The host's document says which it is - written where something other than its
            // legs ended the run, it names no verdict of its own - and the code it ended with is then the refusal's. A
            // run that reported on its legs names its verdict, and a refusal's code there is that verdict's own: a leg
            // left for a lock held there, which is this leg's line and ends no run.
            EndsTheRun = ledger!.Verdict is null && HarnessExit.RefusesTheRun(exitCode) ? Refusal(leg, commandName, exitCode, failure) : null,

            // Why a leg did not run there is that host's reason, and is named by the host this
            // machine knows: the host places the leg on itself, and has no name for itself but
            // "this machine".
            Detail = detail.Length > 0 && (Verdicts.IsSkip(verdict) || verdict is LegVerdict.RefusedLocked or LegVerdict.LogHeld or LegVerdict.NotAdmitted)
                ? $"{leg.Host.Host}: {detail}"
                : detail,
            Duration = TimeSpan.FromSeconds(entry.DurationSeconds),
            CommandTime = TimeSpan.FromSeconds(entry.CommandSeconds),
            Emulated = leg.Emulated,
            TestCount = entry.TestCount,

            // What the count belongs to, as the host counted it: the host ran what it had, which
            // under --use-staged may name another test set than this machine's configuration now does.
            Project = entry.Project,
            TestSet = entry.TestSet,
            TimingNotes = [.. entry.TimingNotes ?? []],

            // The host ran the leg under a run of its own, whose records are there: named so the
            // caller is told where, as it is for a leg this machine ran.
            RunDirectory = ledger?.RunDirectory,

            // The run the host ran the leg under, as its command named it and its document says: a leg's reader cites
            // it as this run's own id is cited.
            HostRunId = ledger?.RunId is { } id && RunId.TryParse(id, out var under) ? under : null,

            // The leg's own records in that run, and each phase log in them, as the host spelled their paths: its home
            // as ~, for the host's ledger writes them through its own shorthand.
            Records = entry.Records,
            Logs = entry.Logs is null ? null : [.. entry.Logs.Select(log => new PhaseLogFile(log.Phase, log.File))],
            SkippedSteps = [.. entry.SkippedSteps ?? []],
            RanSteps = [.. entry.RanSteps ?? []],
            ManualSteps = [.. entry.ManualSteps ?? []],
            UnselectedSteps = [.. entry.UnselectedSteps ?? []],

            // What the phase that failed there printed last: its log stays on that host.
            LogTail = [.. (entry.LogTail ?? []).Select(line => HostProbes.AsConfigured(line, connection))],
            TailPhase = entry.TailPhase,
            Compilers = [.. entry.Compilers ?? []],
            DeveloperEnvironment = entry.DeveloperEnvironment,

            // What the leg did there with each set-up its work named: they came up and went down on that host.
            SetUps = [.. entry.SetUps ?? []],

            // How that host's machine took the leg, as it measured itself.
            Admission = entry.Admission,

            // Measured there, of the directory there: the host's own path and filesystem.
            Space = entry.Space,

            // Relative to the tree, which is the same path in this machine's tree: what sync --pull takes.
            KeptOutputs = [.. entry.KeptOutputs ?? []],

            // What the host's test invocation kept there when it did not pass, as the host's ledger wrote it: its
            // directory spelled as that host spells it, its home as ~, and each file it names.
            KeptOnFailure = entry.KeptOnFailure,

            // The units the host's build failed to build there, as its ledger named them: relative to the tree, which is the
            // same path in this machine's tree, and why which is not known named by the host this machine knows, as the
            // leg's detail is.
            FailedUnits = entry.FailedUnits is { } failed
                ? failed with { Units = [.. failed.Units ?? []], Unread = failed.Unread is { } unread ? HostProbes.AsConfigured(unread, connection) : null }
                : null,

            // Each arm the host's sweep was asked about, as it judged it there: its records stay on that host, named as
            // the host names them, and its detail named by the host this machine knows, as the leg's is.
            Arms = [.. (entry.Arms ?? []).Select(arm => new ArmEntry
            {
                Arm = arm.Arm ?? string.Empty,
                Verdict = Verdicts.Parse(arm.Verdict) ?? LegVerdict.Poisoned,
                Detail = HostProbes.AsConfigured(arm.Detail ?? string.Empty, connection),
                Duration = TimeSpan.FromSeconds(arm.DurationSeconds),
                Worker = arm.Worker,
                Cases = arm.Cases,
                DeclaredCases = arm.DeclaredCases,
                Reds = arm.Reds,
                DeclaredReds = [.. arm.DeclaredReds ?? []],
                Records = arm.Records,
                Key = arm.Key,
                Held = arm.Held,
                Pending = arm.Pending is { } pending ? pending with { Said = HostProbes.AsConfigured(pending.Said, connection) } : null,
            })],

            // What --pending made of the leg's arms there, as the host counted them.
            PendingSweep = entry.PendingSweep,

            // Each control of the host's sweep that did not pass, as it judged it there: its detail named by the host this
            // machine knows, as the leg's is, and its records named as the host names them, which stay on that host.
            Controls = [.. (entry.Controls ?? []).Select(control => new ControlEntry(
                control.Runner ?? string.Empty,
                control.Verdict ?? string.Empty,
                HostProbes.AsConfigured(control.Detail ?? string.Empty, connection),
                [.. control.Reds ?? []],
                control.Records ?? string.Empty,
                control.Ctest is { } ctest
                    ? new CtestControl(
                        ctest.Matched,
                        ctest.Unread is { } unread ? HostProbes.AsConfigured(unread, connection) : null,
                        [.. ctest.Tests ?? []])
                    : null))],
        };
    }

    /// <summary>A host's refusal of the whole run, as this run's: the code it ended with, and what it said.</summary>
    private static HarnessException Refusal(PlacedLeg leg, string commandName, int exitCode, string? failure)
        => new(
            exitCode,
            $"{leg.Host.Host} refused '{commandName}' for leg '{leg.Name}': " + (failure ?? $"it exited {exitCode} and said nothing more"));

    /// <summary>What a host's command said as it failed, closing a sentence: <c>saying: ...</c>, or that it said nothing more.</summary>
    private static string Saying(string? failure) => failure is null ? "and said nothing more" : $"saying: {failure}";

    /// <summary>How one start of a leg's command on its host finished, and what it wrote and said on the way.</summary>
    /// <param name="Ledger">The ledger it wrote, or <see langword="null"/> where it wrote none.</param>
    /// <param name="Finished">The code it ended with, as the agent's completion line carried it.</param>
    /// <param name="Failure">What its failure line said, and every line after it, where it wrote one.</param>
    /// <param name="HostRun">The run its command named as it began, where it named one.</param>
    /// <param name="Noted">Whether it wrote any note of its work.</param>
    private sealed record HostAnswer(RemoteLedger? Ledger, int Finished, string? Failure, RunId? HostRun, bool Noted)
    {
        /// <summary>
        /// Whether the host's DssHarness failed as a defect before anything ran there: its command ended with
        /// <see cref="HarnessExit.InternalError"/> having said nothing of its own work - no run named, no note written,
        /// and no ledger, or one with no line for any leg, no run, no directory of records and no set-up left running.
        /// What this machine reads of a host is all it can go by: a command that keeps runs names its run before anything
        /// of it can start, a leg whose work began there ends with its line, and a sweep's notes are written as its work goes.
        /// </summary>
        public bool FailedBeforeAnythingRan
            => Finished == HarnessExit.InternalError
                && HostRun is null
                && !Noted
                && Ledger is null or { Legs: null or { Count: 0 }, RunId: null, RunDirectory: null, SetUpsLeftRunning: null or { Count: 0 } };

        /// <summary>What a start that failed before anything ran said, as the leg's line and the warning before the next start say it.</summary>
        /// <param name="leg">The leg it was started for.</param>
        public string SaidAsItStarted(PlacedLeg leg)
            => $"{leg.Host.Host}'s DssHarness exited {Finished} as it started, before anything ran there, {Saying(Failure)}";
    }

    /// <summary>The shape a host's ledger arrives in, read back by name rather than by position.</summary>
    /// <param name="Legs">Each leg's line.</param>
    /// <param name="RunDirectory">Where the host's own run keeps its records, when it got that far.</param>
    /// <param name="RunId">The run the host's command named as it began, when it began one.</param>
    /// <param name="Verdict">
    /// The verdict of the host's run, which a run that reported on its legs names; <see langword="null"/> where
    /// something other than its legs ended it, as <see cref="LedgerReport"/> writes such a run.
    /// </param>
    /// <param name="SetUpsLeftRunning">Each set-up a leg of the host's run left running, where one did.</param>
    private sealed record RemoteLedger(
        [property: JsonPropertyName("legs")] IReadOnlyList<RemoteLedgerLeg>? Legs,
        [property: JsonPropertyName("runDirectory")] string? RunDirectory = null,
        [property: JsonPropertyName("runId")] string? RunId = null,
        [property: JsonPropertyName("verdict")] string? Verdict = null,
        [property: JsonPropertyName("setUpsLeftRunning")] IReadOnlyList<RemoteSetUpLeft>? SetUpsLeftRunning = null);

    /// <summary>A set-up a leg of the host's run left running, as the host's ledger names it.</summary>
    /// <param name="Leg">The leg.</param>
    /// <param name="SetUp">The set-up.</param>
    /// <param name="Up">How it came up for the leg.</param>
    /// <param name="Inputs">The values it was brought up with.</param>
    /// <param name="Detail">Why it did not come down.</param>
    private sealed record RemoteSetUpLeft(
        [property: JsonPropertyName("leg")] string Leg,
        [property: JsonPropertyName("setUp")] string SetUp,
        [property: JsonPropertyName("up")] string? Up = null,
        [property: JsonPropertyName("inputs")] IReadOnlyDictionary<string, string>? Inputs = null,
        [property: JsonPropertyName("detail")] string? Detail = null);

    /// <summary>One leg's line of a host's ledger.</summary>
    private sealed record RemoteLedgerLeg(
        string? Verdict,
        string? Detail,
        double DurationSeconds,
        double CommandSeconds,
        int? TestCount,
        IReadOnlyList<string>? TimingNotes,
        IReadOnlyList<string>? SkippedSteps = null,
        IReadOnlyList<Build.CompilerFact>? Compilers = null,
        DeveloperEnvironmentFact? DeveloperEnvironment = null,
        string? Project = null,
        string? TestSet = null,
        IReadOnlyList<string>? LogTail = null,
        IReadOnlyList<string>? ManualSteps = null,
        IReadOnlyList<string>? UnselectedSteps = null,
        IReadOnlyList<string>? RanSteps = null,
        BuildSpace? Space = null,
        IReadOnlyList<string>? KeptOutputs = null,
        KeptOnFailure? KeptOnFailure = null,
        AdmissionFact? Admission = null,
        IReadOnlyList<RemoteLedgerArm>? Arms = null,
        string? Records = null,
        IReadOnlyList<PhaseLogFile>? Logs = null,
        PendingSweep? PendingSweep = null,
        IReadOnlyList<RemoteLedgerControl>? Controls = null,
        IReadOnlyList<SetUps.SetUpFact>? SetUps = null,
        string? TailPhase = null,
        Build.FailedUnits? FailedUnits = null);

    /// <summary>One control of a host's sweep that did not pass, beneath its leg's line.</summary>
    private sealed record RemoteLedgerControl(
        string? Runner,
        string? Verdict,
        string? Detail,
        IReadOnlyList<string>? Reds = null,
        string? Records = null,
        RemoteCtestControl? Ctest = null);

    /// <summary>What ctest registered for a control's red cases, as the host read it.</summary>
    private sealed record RemoteCtestControl(
        string? Matched = null,
        string? Unread = null,
        IReadOnlyList<CtestTestLine>? Tests = null);

    /// <summary>One arm's line of a host's sweep, beneath its leg's.</summary>
    private sealed record RemoteLedgerArm(
        string? Arm,
        string? Verdict,
        string? Detail,
        double DurationSeconds,
        int? Worker,
        int? Cases,
        int DeclaredCases,
        IReadOnlyList<string>? Reds,
        IReadOnlyList<string>? DeclaredReds,
        string? Records,
        string? Key = null,
        ArmHeld? Held = null,
        ArmPending? Pending = null);
}
