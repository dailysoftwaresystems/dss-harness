using System.Text.Json;
using System.Text.Json.Serialization;
using DssHarness.Core.Execution;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Hosts;
using DssHarness.Core.Json;
using DssHarness.Core.Output;
using DssHarness.Core.Platform;
using DssHarness.Core.Processes;
using DssHarness.Core.Results;
using DssHarness.Core.Testing;
using DssHarness.Core.Waiting;

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
/// <param name="hostCommands">Runs the host's agent.</param>
/// <param name="output">Where what the host prints is relayed, and its warnings said.</param>
/// <param name="fileSystem">Where the lines after a host's failure line are held (<see cref="HeldLines"/>); the machine's own where none is given.</param>
/// <param name="clock">Times the pause before a start once more; the system's where none is given.</param>
public sealed class RemoteLegRunner(IHostCommandRunner hostCommands, IHarnessOutput output, IFileSystem? fileSystem = null, TimeProvider? clock = null)
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

    private readonly IHostCommandRunner _hostCommands = hostCommands;
    private readonly IHarnessOutput _output = output;
    private readonly IFileSystem _fileSystem = fileSystem ?? new PhysicalFileSystem(FilePermissionsFactory.Create());
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

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
    /// <param name="priority">
    /// The run's priority, which each unit the leg asks the host's dispatch queue for is charged at there
    /// (<see cref="HostAgentRequest.Priority"/>); <see langword="null"/> for a command that runs no leg of a run.
    /// </param>
    /// <param name="onNote">Told each whole note the host's command writes while it runs, as it arrives; never relayed; <see langword="null"/> where this reads none.</param>
    /// <param name="setUpsLeftRunning">
    /// Told each set-up the leg left running on the host where it ended there with no line to carry it, so this run
    /// says it as the host's did; <see langword="null"/> where nothing is told.
    /// </param>
    /// <param name="earlier">
    /// What each earlier start of the leg's work there that failed before anything ran said - its check's before this
    /// machine took its slot (<see cref="CheckAsync"/>) - said before this command's own; <see langword="null"/> where none did.
    /// </param>
    /// <param name="cancellationToken">Stops the command on the host as well as here.</param>
    /// <returns>
    /// The leg's line from the host's ledger; or, where the host's DssHarness failed as a defect with no line for the leg,
    /// a <c>poisoned</c> line naming the host, the code and what it said. A DssHarness there that failed so before
    /// anything ran is started once more first, for each cause not met before, and the line says what each such start said
    /// (<see cref="LegEntry.Retried"/>), <paramref name="earlier"/>'s first, whatever the leg then reached.
    /// </returns>
    /// <exception cref="HarnessException">
    /// The host could not be reached - its transport would not start - or never reported how the
    /// command finished, or reported a ledger this build cannot read, or ended its command with no line
    /// for the leg and a code that is neither a refusal of the run nor a defect of its own. None is a
    /// verdict about the code, so none is reported as one. Or the command refused the whole run there
    /// with no line for the leg, which is raised as that refusal, with what the host said; one that
    /// came with the leg's line is carried on it (<see cref="LegEntry.EndsTheRun"/>), and ends the run
    /// once the line is recorded. Each says what every start that failed before anything ran said, as the line would.
    /// </exception>
    public async Task<LegEntry> RunAsync(
        string commandName,
        PlacedLeg leg,
        IReadOnlyList<HostArgument> arguments,
        RunId? run = null,
        int? priority = null,
        Action<string, string>? onNote = null,
        Action<IReadOnlyList<SetUps.SetUpFact>>? setUpsLeftRunning = null,
        HostRestarts? earlier = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandName);
        ArgumentNullException.ThrowIfNull(leg);
        ArgumentNullException.ThrowIfNull(arguments);

        var session = SessionOf(leg);
        var (entry, restarts) = await StartedOnceMoreAsync(
                commandName,
                leg,
                session,
                [commandName, "--legs", leg.Name, "--json", HereOption, leg.Host.Host.ToString(), .. Verbosity, .. arguments],
                run,
                priority,
                onNote,
                writesLedger: true,
                again: "its start there once more",
                earlier ?? HostRestarts.None,
                answer => Read(answer, leg, session.Connection, commandName, setUpsLeftRunning),
                cancellationToken)
            .ConfigureAwait(false);

        return restarts.On(entry);
    }

    /// <summary>
    /// Asks the host of <paramref name="leg"/> to check there, in the copy sync put there and by the host's own values,
    /// what only it can before this machine admits the leg - <paramref name="commandName"/> as it would run the leg, given
    /// <paramref name="arguments"/> that have it check and run nothing - and answers whether it refused nothing.
    /// </summary>
    /// <param name="commandName">The command to check there, which is the one running here.</param>
    /// <param name="leg">The placed leg, whose work another machine's process runs.</param>
    /// <param name="arguments">The command's own options, then those that have it check and run nothing.</param>
    /// <param name="cancellationToken">Stops the check on the host as well as here.</param>
    /// <returns>
    /// What each start of the check that failed as a defect before anything ran said (<see cref="HostCheck.Restarts"/>),
    /// which the leg's line says whatever the leg then reaches; and, where the host's DssHarness failed so again, started
    /// once more as a run of the leg is, the leg's <c>poisoned</c> line, naming the host, the
    /// code and what each start said, as the leg's run there would have ended - none where the host refused nothing.
    /// </returns>
    /// <exception cref="HarnessException">
    /// The host refused - raised as the refusal of the leg's run there would have been, with the host's code and words, so
    /// the run ends as it would have once the leg ran - or it could not be reached, never said how the check finished, or
    /// ended it some other way, none of which is a verdict about the code. Each says what every start that failed before
    /// anything ran said.
    /// </exception>
    public async Task<HostCheck> CheckAsync(
        string commandName,
        PlacedLeg leg,
        IReadOnlyList<HostArgument> arguments,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandName);
        ArgumentNullException.ThrowIfNull(leg);
        ArgumentNullException.ThrowIfNull(arguments);

        var session = SessionOf(leg);

        // Answered by its exit code and, where it refused, its failure line: it keeps no run and writes no ledger, so what
        // it writes to standard output is never read as one. It runs nothing, so a start that failed is always one before
        // anything ran there, started once more as a run of the leg would be. Told what the leg's run there would be told
        // beside its command line - its distributions among it, which its run lines may name - so it refuses what that run
        // would, and nothing it would not.
        var (poisoned, restarts) = await StartedOnceMoreAsync(
                commandName,
                leg,
                session,
                [commandName, "--legs", leg.Name, HereOption, leg.Host.Host.ToString(), .. Verbosity, .. arguments],
                run: null,
                priority: null,
                onNote: null,
                writesLedger: false,
                again: "its check there once more",
                HostRestarts.None,
                answer => Checked(answer, leg, session.Connection, commandName),
                cancellationToken)
            .ConfigureAwait(false);

        return new HostCheck(poisoned is null ? null : restarts.On(poisoned), restarts);
    }

    /// <summary>
    /// What one start of a host's check of <paramref name="leg"/> came to: nothing where it refused nothing, and the leg's
    /// <c>poisoned</c> line where the host's DssHarness failed as a defect.
    /// </summary>
    /// <exception cref="HarnessException">The host refused the run, or ended the check some other way.</exception>
    private static LegEntry? Checked(HostAnswer answer, PlacedLeg leg, HostConnection connection, string commandName)
    {
        if (answer.Finished == HarnessExit.Success)
        {
            return null;
        }

        if (answer.Finished == HarnessExit.InternalError)
        {
            return Read(answer, leg, connection, commandName);
        }

        throw HarnessExit.RefusesTheRun(answer.Finished)
            ? Refusal(leg, commandName, answer.Finished, answer.Failure)
            : new HarnessException(
                HarnessExit.HostUnavailable,
                $"{leg.Host.Host} checked '{commandName}' for leg '{leg.Name}' and exited {answer.Finished}, "
                + (answer.Failure is null ? "so nothing there said what happened." : $"saying: {answer.Failure}"));
    }

    /// <summary>
    /// The leg's own command verbose where this one is: what -v shows - each step's output - is that command's to write,
    /// and the agent's own --verbose reaches the agent alone, so a leg on a host printed its verdict and nothing else while
    /// a leg here streamed its steps. Written to the host's standard error under --json, it is relayed from there like
    /// every line of it.
    /// </summary>
    private IReadOnlyList<HostArgument> Verbosity => _output.IsVerbose ? [HostAgentProtocol.VerboseOption] : [];

    /// <summary>The session the survey opened to <paramref name="leg"/>'s host.</summary>
    /// <exception cref="HarnessException">The host was not reached, so nothing of the leg can be asked of it.</exception>
    private static HostSession SessionOf(PlacedLeg leg)
        => leg.Host.Session ?? throw new HarnessException(
            HarnessExit.HostUnavailable,
            $"{leg.Host.Host} was not reached, so leg '{leg.Name}' cannot run there: "
            + $"{leg.Host.Reason ?? "no reason was recorded"}.");

    /// <summary>
    /// Has the host of <paramref name="leg"/> run <paramref name="commandLine"/> (<see cref="AskAsync"/>), starts it once
    /// more where the host's own DssHarness failed before anything ran there, for each cause not met before,
    /// telling each pause before it as a wait for <paramref name="again"/>, and reads the last
    /// start's answer by <paramref name="read"/>: the one start a leg's run
    /// and its check share, and the one place what each start that failed so said reaches whatever the leg came to.
    /// </summary>
    /// <param name="commandName">The command started there, which is the one running here.</param>
    /// <param name="leg">The placed leg.</param>
    /// <param name="session">The session to its host.</param>
    /// <param name="commandLine">The command line, as typed after <c>DssHarness</c>.</param>
    /// <param name="run">The run the leg is part of; <see langword="null"/> for a command that runs no leg of a run.</param>
    /// <param name="priority">The run's priority; <see langword="null"/> for a command that runs no leg of a run.</param>
    /// <param name="onNote">Told each whole note the command writes while it runs; <see langword="null"/> where this reads none.</param>
    /// <param name="writesLedger">Whether the command was asked for its ledger (<c>--json</c>), which its standard output then is.</param>
    /// <param name="again">What the warning before a start once more says is waited for: <c>its start there once more</c>.</param>
    /// <param name="earlier">What each earlier start of the leg's work there that failed so said, before these starts.</param>
    /// <param name="read">What the last start's answer comes to; what it raises says what each start that failed so said.</param>
    /// <param name="cancellationToken">Stops the command on the host as well as here.</param>
    /// <returns>What <paramref name="read"/> made of the answer, and what each start that failed so said, in order.</returns>
    /// <exception cref="HarnessException">
    /// As <see cref="AskAsync"/>, or as <paramref name="read"/> raises; saying what each start that failed so said, earlier
    /// ones first, wherever one did.
    /// </exception>
    private async Task<(T Read, HostRestarts Restarts)> StartedOnceMoreAsync<T>(
        string commandName,
        PlacedLeg leg,
        HostSession session,
        IReadOnlyList<HostArgument> commandLine,
        RunId? run,
        int? priority,
        Action<string, string>? onNote,
        bool writesLedger,
        string again,
        HostRestarts earlier,
        Func<HostAnswer, T> read,
        CancellationToken cancellationToken)
    {
        // What each start that failed before anything ran said, in order: said on whatever the leg then reaches - its line,
        // or what ended it with none.
        var restarts = earlier;
        HostAnswer? last = null;

        try
        {
            // Started once more only where the host's own DssHarness failed before anything ran there, which is safe to
            // repeat: a tool or a runtime replaced on the host while it started fails that way once. Never once anything
            // ran there, whatever then failed: a second start would run it twice. Started again for each cause not met
            // before, and no more once one is met again: the same cause twice is the host's DssHarness failing, which a
            // further start only meets a third time. Weighed for this command alone: a check that failed so before it
            // leaves the leg's run its own starts once more.
            var ended = await Retrying.RunAsync<HostAnswer>(
                    async (_, token) =>
                    {
                        if (last is not null)
                        {
                            restarts = restarts.Then(last.SaidAsItStarted(leg));
                        }

                        last = await AskAsync(commandName, leg, session, commandLine, run, priority, onNote, writesLedger, token).ConfigureAwait(false);

                        return last.FailedBeforeAnythingRan
                            ? new RetryTry<HostAnswer>.Failed(last.SaidAsItStarted(leg), last.Cause)
                            : new RetryTry<HostAnswer>.Done(last);
                    },
                    new RetryWatch(again, told => _output.Warn(commandName, $"{leg.Name}: {told.Line()}"), Waits.LooksAgainEvery, Bound: null, _clock),
                    cancellationToken)
                .ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();

            // Its answer, or the last start's, whose cause was met before: read as the leg's line it comes to.
            return (read(ended.Value ?? last!), restarts);
        }
        catch (HarnessException ex) when (restarts.Any)
        {
            throw restarts.On(ex);
        }
    }

    /// <summary>
    /// Starts <paramref name="commandLine"/> for one leg on its host once, in its copy of the leg's tree, relaying what it
    /// prints, and answers how it finished, what it said as it failed and what of its own it said on the way.
    /// </summary>
    /// <param name="commandName">The command started there, which is the one running here.</param>
    /// <param name="leg">The placed leg.</param>
    /// <param name="session">The session to its host.</param>
    /// <param name="commandLine">The command line, as typed after <c>DssHarness</c>.</param>
    /// <param name="run">The run the leg is part of; <see langword="null"/> for a command that runs no leg of a run.</param>
    /// <param name="priority">The run's priority; <see langword="null"/> for a command that runs no leg of a run.</param>
    /// <param name="onNote">Told each whole note the command writes while it runs; <see langword="null"/> where this reads none.</param>
    /// <param name="writesLedger">Whether the command was asked for its ledger (<c>--json</c>), which its standard output then is.</param>
    /// <param name="cancellationToken">Stops the command on the host as well as here.</param>
    /// <exception cref="HarnessException">
    /// The host never reported how the command finished, or reported a ledger this build cannot read.
    /// </exception>
    private async Task<HostAnswer> AskAsync(
        string commandName,
        PlacedLeg leg,
        HostSession session,
        IReadOnlyList<HostArgument> commandLine,
        RunId? run,
        int? priority,
        Action<string, string>? onNote,
        bool writesLedger,
        CancellationToken cancellationToken)
    {
        var nonce = HostAgentProtocol.NewNonce();

        var request = new HostAgentRequest
        {
            Kind = HostAgentRequestKind.Run,
            Directory = leg.HostTreeRoot,
            Arguments = [.. commandLine],
            Nonce = nonce,
            RunId = run?.Value,
            Priority = priority,

            // The drive a WSL distribution's disk grows on, which the leg's build fills, and which only this machine sees.
            DiskImageDrive = leg.Host.Host.Kind == HostKind.Wsl ? leg.Host.DiskImageSpace?.Filesystem : null,

            // What this machine's settings file declared of its admission as the leg's work began, which holds a WSL leg's
            // build to its floor: the one reading its slot was admitted by, and the distribution's own file another machine's.
            MachineAdmission = leg.Host.Host.Kind == HostKind.Wsl ? leg.AdmittedBy?.SettingsFile : null,

            // The distribution each WSL item read here names, which the leg's run lines may name there, where no item is.
            Distributions = leg.Distributions
                .Where(told => told.Value is { Length: > 0 })
                .ToDictionary(told => told.Key, told => told.Value!, StringComparer.OrdinalIgnoreCase),
        };

        var ledger = new System.Text.StringBuilder();
        var lines = new HostAgentLines(nonce, onNote);

        // The lines from the last failure line on, as they would be shown, held until the host has finished: see below.
        using var held = new HeldLines(_fileSystem, _output.RawError, why => _output.Warn(commandName, $"{leg.Name}: {why}"));

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
                        // command that ended well, which ends without one. Every line after it is held, however many:
                        // a failure line a step printed is followed by the rest of the run, and is known to be none
                        // only by what follows it, never by how much does.
                        if (FailureLine.TryRead(line, commandName, out var said)
                            || FailureLine.TryRead(line, HostAgentProtocol.CommandName, out said))
                        {
                            held.Begin(line, said);
                        }
                        else
                        {
                            held.Add(line);
                        }
                    },
                },
                cancellationToken)
            .SayingUnreached(leg.Name, line => _output.Warn(commandName, line))
            .ConfigureAwait(false);

        // A command that ended badly ends on its failure line, with what follows it, and that is the host's conclusion:
        // this machine says how the leg ended itself - by the leg's own line where the host's ledger has an entry for
        // it, and in the refusal raised from what the host said where the host refused the run, with a line or with
        // none - so it is never shown. Shown as well, it was said twice, and a host's summary of its one leg read as
        // this run's. Anything else held is shown, late, read back in order from where it was held.
        if (lines.Finished is null or HarnessExit.Success)
        {
            held.Show();
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
            writesLedger ? Parse(ledger.ToString(), leg, commandName) : null,
            finished,
            held.Failure(),
            hostRun,
            lines.Noted,
            held.Cause);
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
    private static LedgerDocument? Parse(string output, PlacedLeg leg, string commandName)
    {
        var document = output.Trim();

        if (document.Length == 0)
        {
            return null;
        }

        try
        {
            return LedgerDocument.Read(document);
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

        // The leg's line as the host's ledger wrote it, read by the ledger's own reader: every text its work printed or
        // its harness wrote about it named by the host this machine knows - a phase's last lines reach the reader on its
        // standard error too, under --verbose, and say it there - and its digest, made there, read as it was written.
        LegEntry line;

        try
        {
            line = entry.Entry(said => HostProbes.AsConfigured(said, connection));
        }
        catch (JsonException ex)
        {
            throw new HarnessException(
                HarnessExit.HostUnavailable,
                $"{leg.Host.Host} answered '{commandName}' with a ledger this build cannot read: its digest of leg '{leg.Name}': {ex.Message}");
        }

        return line with
        {
            Leg = leg.Name,

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
            Detail = line.Detail.Length > 0 && (Verdicts.IsSkip(line.Verdict) || line.Verdict is LegVerdict.RefusedLocked or LegVerdict.LogHeld or LegVerdict.NotAdmitted)
                ? $"{leg.Host.Host}: {line.Detail}"
                : line.Detail,
            Emulated = leg.Emulated,

            // The host ran the leg under a run of its own, whose records are there: named so the
            // caller is told where, as it is for a leg this machine ran.
            RunDirectory = ledger.RunDirectory,

            // The run the host ran the leg under, as its command named it and its document says: a leg's reader cites
            // it as this run's own id is cited.
            HostRunId = ledger.RunId is { } id && RunId.TryParse(id, out var under) ? under : null,
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
    /// <param name="Failure">
    /// What its failure line said, and every line after it, where it wrote one and it was not shown: its conclusion, whole.
    /// </param>
    /// <param name="HostRun">The run its command named as it began, where it named one.</param>
    /// <param name="Noted">Whether it wrote any note of its work.</param>
    /// <param name="Said">What its failure line itself said, the first line of <paramref name="Failure"/>; <see langword="null"/> where it wrote none.</param>
    private sealed record HostAnswer(LedgerDocument? Ledger, int Finished, string? Failure, RunId? HostRun, bool Noted, string? Said)
    {
        /// <summary>
        /// What the start failed of: the code it ended with and what its failure line said. Two starts that fail of the
        /// same cause are the same failure, whatever the lines after it - a stack's addresses, a dump's name - say.
        /// </summary>
        public string Cause => $"{Finished.ToString(System.Globalization.CultureInfo.InvariantCulture)}\n{Said}";

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
}
