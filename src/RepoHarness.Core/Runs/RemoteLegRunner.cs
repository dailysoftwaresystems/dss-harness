using System.Text.Json;
using System.Text.Json.Serialization;
using RepoHarness.Core.Execution;
using RepoHarness.Core.Hosts;
using RepoHarness.Core.Output;
using RepoHarness.Core.Results;

namespace RepoHarness.Core.Runs;

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

    private static readonly JsonSerializerOptions LedgerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly IHostCommandRunner _hostCommands = hostCommands;
    private readonly IHarnessOutput _output = output;

    /// <summary>Runs <paramref name="commandName"/> for one leg on its host, and returns its entry.</summary>
    /// <param name="commandName">The command to run there, which is the one running here.</param>
    /// <param name="leg">The placed leg: its host, and the host's copy of its tree, which sync made, that it runs in.</param>
    /// <param name="arguments">The command's own options, without <c>--legs</c>, <c>--json</c>, <c>--here</c> or <c>--verbose</c>, which this adds.</param>
    /// <param name="cancellationToken">Stops the command on the host as well as here.</param>
    /// <exception cref="HarnessException">
    /// The host could not be reached - its transport would not start - or never reported how the
    /// command finished, or reported a ledger this build cannot read. None is a verdict about the
    /// code, so none is reported as one. Or the command refused the whole run there, which is raised
    /// as that refusal, with what the host said.
    /// </exception>
    public async Task<LegEntry> RunAsync(
        string commandName,
        PlacedLeg leg,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandName);
        ArgumentNullException.ThrowIfNull(leg);
        ArgumentNullException.ThrowIfNull(arguments);

        var session = leg.Host.Session ?? throw new HarnessException(
            HarnessExit.HostUnavailable,
            $"{leg.Host.Host} was not reached, so leg '{leg.Name}' cannot run there: "
            + $"{leg.Host.Reason ?? "no reason was recorded"}.");

        var nonce = HostAgentProtocol.NewNonce();

        // The leg's own command verbose where this one is: what -v shows - each step's output - is that command's to
        // write, and the agent's own --verbose below reaches the agent alone, so a leg on a host printed its verdict and
        // nothing else while a leg here streamed its steps. Written to the host's standard error under --json, it is
        // relayed from there like every line of it.
        string[] verbosity = _output.IsVerbose ? [HostAgentProtocol.VerboseOption] : [];

        var request = JsonSerializer.Serialize(
            new HostAgentRequest
            {
                Kind = HostAgentRequestKind.Run,
                Directory = leg.HostTreeRoot,
                Arguments = [commandName, "--legs", leg.Name, "--json", HereOption, leg.Host.Host.ToString(), .. verbosity, .. arguments],
                Nonce = nonce,
            },
            HostAgentProtocol.JsonOptions);

        var ledger = new System.Text.StringBuilder();
        var lines = new HostAgentLines(nonce);
        var failure = new List<string>();

        // The lines failure was read from, as they would be shown, held until the host has finished: see below.
        var held = new List<string>();

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
                    StandardInput = request + "\n",
                    HoldStandardInputOpen = true,

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
        // it, and otherwise in the refusal raised from what the host said - so it is never shown. Shown as well, it was
        // said twice, and a host's summary of its one leg read as this run's. Anything else held is shown, late.
        if (lines.Finished is null or HarnessExit.Success)
        {
            Show(held);
        }

        if (lines.Finished is not { } finished)
        {
            // Never a failed verdict: the command may not have run, or run only in part, and
            // reporting that as a red leg would blame the code for a connection.
            throw new HarnessException(
                HarnessExit.HostUnavailable,
                $"{leg.Host.Host}: {HostProbes.NeverFinished($"'{commandName}' for leg '{leg.Name}'", result, session.Connection)}");
        }

        return Read(ledger.ToString(), leg, session.Connection, commandName, finished, failure.Count == 0 ? null : string.Join(Environment.NewLine, failure));
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

    /// <summary>Reads the one leg's entry out of the ledger the host wrote.</summary>
    /// <remarks>
    /// The whole of standard output is the document. The host was asked with <c>--json</c>, which
    /// sends its progress to standard error precisely so that nothing shares this channel: hunting
    /// for the first <c>{</c> instead would find the one inside a compiler message or a test name
    /// that a progress line had already carried here, and report a leg that reached a verdict as a
    /// host that could not be reached.
    /// </remarks>
    /// <param name="output">Everything the host wrote to standard output.</param>
    /// <param name="leg">The leg asked for.</param>
    /// <param name="connection">The connection the host answered over.</param>
    /// <param name="commandName">The command the host ran.</param>
    /// <param name="exitCode">How that command finished.</param>
    /// <param name="failure">What its failure line said, and every line after it, when it wrote one.</param>
    /// <exception cref="HarnessException">
    /// The host wrote no entry for the leg. A refusal of the whole run there is raised as that same
    /// refusal; anything else as a host that said nothing about the leg.
    /// </exception>
    private static LegEntry Read(string output, PlacedLeg leg, HostConnection connection, string commandName, int exitCode, string? failure)
    {
        var document = output.Trim();

        RemoteLedger? ledger = null;

        if (document.Length > 0)
        {
            try
            {
                ledger = JsonSerializer.Deserialize<RemoteLedger>(document, LedgerOptions);
            }
            catch (JsonException ex)
            {
                throw new HarnessException(
                    HarnessExit.HostUnavailable,
                    $"{leg.Host.Host} answered '{commandName}' with a ledger this build cannot read: {ex.Message}");
            }
        }

        var entry = ledger?.Legs?.FirstOrDefault();

        if (entry is null)
        {
            // A refusal the host made is this run's refusal. A configuration, a command line or a
            // policy its copy cannot satisfy is the same fact there as here, and the same refusal on
            // this machine stops the run; read as a host that could not be reached, it became a
            // skipped leg and a run that merely looked incomplete, with its reason and its fix left
            // behind on the host's error stream.
            if (HarnessExit.RefusesTheRun(exitCode))
            {
                throw new HarnessException(
                    exitCode,
                    $"{leg.Host.Host} refused '{commandName}' for leg '{leg.Name}': "
                    + (failure ?? $"it exited {exitCode} and said nothing more"));
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
            SkippedSteps = [.. entry.SkippedSteps ?? []],
            RanSteps = [.. entry.RanSteps ?? []],
            ManualSteps = [.. entry.ManualSteps ?? []],
            UnselectedSteps = [.. entry.UnselectedSteps ?? []],

            // What the phase that failed there printed last: its log stays on that host.
            LogTail = [.. (entry.LogTail ?? []).Select(line => HostProbes.AsConfigured(line, connection))],
            Compilers = [.. entry.Compilers ?? []],
            DeveloperEnvironment = entry.DeveloperEnvironment,

            // How that host's machine took the leg, as it measured itself.
            Admission = entry.Admission,

            // Measured there, of the directory there: the host's own path and filesystem.
            Space = entry.Space,

            // Relative to the tree, which is the same path in this machine's tree: what sync --pull takes.
            KeptOutputs = [.. entry.KeptOutputs ?? []],
        };
    }

    /// <summary>The shape a host's ledger arrives in, read back by name rather than by position.</summary>
    /// <param name="Legs">Each leg's line.</param>
    /// <param name="RunDirectory">Where the host's own run keeps its records, when it got that far.</param>
    private sealed record RemoteLedger(
        [property: JsonPropertyName("legs")] IReadOnlyList<RemoteLedgerLeg>? Legs,
        [property: JsonPropertyName("runDirectory")] string? RunDirectory = null);

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
        AdmissionFact? Admission = null);
}
