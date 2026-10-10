using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.Execution;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Output;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Processes;
using RepoHarness.Core.Results;

namespace RepoHarness.Core.Hosts;

/// <summary>
/// Serves what another DssHarness asks of this machine: which build this is and what the machine
/// is, or to run one of this build's own commands in a directory here. Reached only through
/// <see cref="HostAgentProtocol.CommandName"/>, with the request on standard input.
/// </summary>
/// <remarks>
/// What a run request starts never outlives the machine that asked for it. That machine has gone when the input it holds
/// open ends, when that input cannot be read, or when it stops writing the beat its request said it would write - the
/// one sign that needs nothing of whatever carries the input in between. What the request started is then cancelled, and
/// where it has not stopped within <see cref="HostAgentPatience.Unwind"/> the agent ends its processes and itself.
/// </remarks>
public sealed class HostAgentService(
    IHostPlatform platform,
    IToolIdentityProvider identity,
    EmulatorProbe emulatorProbe,
    DeveloperEnvironmentProbe developerEnvironmentProbe,
    IFileSystem fileSystem,
    LocalProgramResolver programs,
    KeepAwake keepAwake,
    HoldAwakeStore holds,
    IDetachedProcessLauncher launcher,
    HomeShorthand home,
    IHostAgentLastResort lastResort,
    HostAgentPatience? patience = null)
{
    private readonly IHostPlatform _platform = platform;
    private readonly IToolIdentityProvider _identity = identity;
    private readonly EmulatorProbe _emulatorProbe = emulatorProbe;
    private readonly DeveloperEnvironmentProbe _developerEnvironmentProbe = developerEnvironmentProbe;
    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly LocalProgramResolver _programs = programs;
    private readonly KeepAwake _keepAwake = keepAwake;
    private readonly HoldAwakeStore _holds = holds;
    private readonly IDetachedProcessLauncher _launcher = launcher;
    private readonly HomeShorthand _home = home;
    private readonly IHostAgentLastResort _lastResort = lastResort;
    private readonly HostAgentPatience _patience = patience ?? new HostAgentPatience();

    /// <summary>How long the agent's last words are given to be written, where nobody may be reading them.</summary>
    private static readonly TimeSpan SayingBudget = TimeSpan.FromSeconds(2);

    /// <summary>Reads one request from <paramref name="input"/> and serves it.</summary>
    /// <param name="input">
    /// Where the request is read from: one line, after which the input stays open while the request is
    /// served. Its end means the machine that asked has gone, and cancels what the request started; so does a run
    /// request's beat falling silent on it.
    /// </param>
    /// <param name="output">Where an info answer is written.</param>
    /// <param name="error">Where a refused request is explained, and where a run request's completion line is written.</param>
    /// <param name="run">
    /// Runs a DssHarness command line with the given directory as the current one, with what the machine that asked
    /// says of it beside the line, until it finishes or the token is cancelled, and returns its exit code. Supplied by
    /// the program, which is the only place that holds the command line parser.
    /// </param>
    /// <param name="cancellationToken">Stops serving.</param>
    /// <returns>The exit code this process reports.</returns>
    public async Task<int> ServeAsync(
        TextReader input,
        TextWriter output,
        TextWriter error,
        Func<string, string[], Dispatch, CancellationToken, Task<int>> run,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        ArgumentNullException.ThrowIfNull(run);

        var line = await input.ReadLineAsync(cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(line))
        {
            return await RefuseAsync(error, HarnessExit.UsageError, "the request is empty").ConfigureAwait(false);
        }

        // The protocol is read on its own first, so a request from a build that shapes requests differently is
        // refused as exactly that, rather than as whichever of its fields this build happens not to know.
        if (!TryReadProtocol(line, out var protocol, out var problem))
        {
            return await RefuseAsync(error, HarnessExit.UsageError, $"the request is not valid: {problem}").ConfigureAwait(false);
        }

        if (protocol != HostAgentProtocol.Version)
        {
            return await RefuseAsync(
                error,
                HarnessExit.UsageError,
                $"the request speaks protocol {protocol}, and {ToolPackage.Id} {_identity.Current.Version} on this host speaks {HostAgentProtocol.Version}").ConfigureAwait(false);
        }

        HostAgentRequest? request;

        try
        {
            request = JsonSerializer.Deserialize<HostAgentRequest>(line, HostAgentProtocol.JsonOptions);
        }
        catch (JsonException ex)
        {
            return await RefuseAsync(error, HarnessExit.UsageError, $"the request is not valid: {ex.Message}").ConfigureAwait(false);
        }

        if (request is null)
        {
            return await RefuseAsync(error, HarnessExit.UsageError, "the request is empty").ConfigureAwait(false);
        }

        // The machine that asked holds its end open while the request is served, so the end of the input means
        // it has gone: its ssh or wsl.exe was stopped, or the connection dropped. What the request started is
        // then cancelled, rather than left running where nobody waits for it. An input that never ends says
        // nothing, so a run request is cancelled as well once the beat it said it would write has stopped.
        using var abandoned = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var served = new CancellationTokenSource();
        var gone = new Gone(abandoned, request.Kind == HostAgentRequestKind.Run ? error : null);
        var heard = new StrongBox<long>(Stopwatch.GetTimestamp());

        _ = WatchInputAsync(input, heard, gone);

        if (request is { Kind: HostAgentRequestKind.Run, BeatSeconds: > 0 })
        {
            _ = WatchBeatAsync(_patience.BeatUnit * ((long)request.BeatSeconds * HostAgentProtocol.BeatsMissed), heard, gone, served.Token);
        }

        try
        {
            return await ServeAsync(request, output, error, run, gone, abandoned.Token, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Nothing is said, or cancelled, for a request already answered.
            gone.Served();
            await served.CancelAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Serves <paramref name="request"/>, once it is read and its input is watched.</summary>
    private async Task<int> ServeAsync(
        HostAgentRequest request,
        TextWriter output,
        TextWriter error,
        Func<string, string[], Dispatch, CancellationToken, Task<int>> run,
        Gone gone,
        CancellationToken abandoned,
        CancellationToken cancellationToken)
    {
        if (request.Kind == HostAgentRequestKind.Info)
        {
            // Marked as a run request's output is, and for the same reason: an inspection that fails quotes
            // what the host said into that host's reason, which reaches the reader, --json and every leg
            // reported unavailable. Without the marker that quotation is whatever the login shell printed
            // first. A request from a build that sends no nonce is answered all the same, unmarked.
            if (!string.IsNullOrWhiteSpace(request.Nonce))
            {
                await WriteStartedAsync(output, error, request.Nonce).ConfigureAwait(false);
            }

            var info = await DescribeAsync(
                    request.Emulators,
                    request.DeveloperEnvironments,
                    request.Programs,
                    request.ToolSearchDirectories,
                    new RoomQuestions(request.SpaceAt, request.Builds),
                    abandoned)
                .ConfigureAwait(false);
            await output.WriteLineAsync(JsonSerializer.Serialize(Told(info), HostAgentProtocol.JsonOptions)).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            return HarnessExit.Success;
        }

        if (request.Kind == HostAgentRequestKind.Hold)
        {
            return await HoldAsync(request, output, error).ConfigureAwait(false);
        }

        return await WithinTheUnwindAsync(RunAsync(request, output, error, run, abandoned), request, error, gone, abandoned).ConfigureAwait(false);
    }

    /// <summary>
    /// What <paramref name="serving"/> answers, where it answers before it is cancelled or within
    /// <see cref="HostAgentPatience.Unwind"/> of that; and otherwise the end of everything it started, and of this
    /// process, by <see cref="IHostAgentLastResort"/>.
    /// </summary>
    private async Task<int> WithinTheUnwindAsync(Task<int> serving, HostAgentRequest request, TextWriter error, Gone gone, CancellationToken abandoned)
    {
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using (abandoned.Register(() => cancelled.TrySetResult()).ConfigureAwait(false))
        {
            if (await Task.WhenAny(serving, cancelled.Task).ConfigureAwait(false) == serving)
            {
                return await serving.ConfigureAwait(false);
            }
        }

        if (await Task.WhenAny(serving, Task.Delay(_patience.Unwind, CancellationToken.None)).ConfigureAwait(false) == serving)
        {
            return await serving.ConfigureAwait(false);
        }

        // Said as any other end is, and best read by nobody: the machine that asked has gone, or is not reading.
        var seconds = Math.Ceiling(_patience.Unwind.TotalSeconds).ToString(CultureInfo.InvariantCulture);

        var said = Gone.SayAsync(
            error,
            [
                FailureLine.For(
                    HostAgentProtocol.CommandName,
                    $"what the request started here was cancelled{(gone.Why is { } why ? $" - {why}" : string.Empty)} - and had not stopped {seconds} s later, so "
                    + "its processes are ended, and this one with them"),
                .. request.Nonce is { Length: > 0 } nonce ? [HostAgentProtocol.CompletionLine(nonce, HarnessExit.Cancelled)] : Array.Empty<string>(),
            ]);

        // Never waited for long: a line nobody reads may never finish being written, and the end does not wait on it.
        await Task.WhenAny(said, Task.Delay(SayingBudget, CancellationToken.None)).ConfigureAwait(false);
        await _lastResort.EndAsync(HarnessExit.Cancelled).ConfigureAwait(false);

        return HarnessExit.Cancelled;
    }

    /// <summary>
    /// Serves a hold request: makes it the hold that stands on this machine, replacing any before it, starts the
    /// process that holds the machine awake - detached, so it goes on once this request's connection has ended -
    /// and answers at once, with the completion line last, as a run request answers.
    /// </summary>
    private async Task<int> HoldAsync(HostAgentRequest request, TextWriter output, TextWriter error)
    {
        if (string.IsNullOrWhiteSpace(request.Nonce))
        {
            return await RefuseAsync(error, HarnessExit.UsageError, "the hold request carries no nonce to mark its answer with").ConfigureAwait(false);
        }

        await WriteStartedAsync(output, error, request.Nonce).ConfigureAwait(false);

        var exitCode = await ServeHoldAsync(request, error).ConfigureAwait(false);

        await error.WriteLineAsync(HostAgentProtocol.CompletionLine(request.Nonce, exitCode)).ConfigureAwait(false);
        await error.FlushAsync(CancellationToken.None).ConfigureAwait(false);

        return exitCode;
    }

    private async Task<int> ServeHoldAsync(HostAgentRequest request, TextWriter error)
    {
        // Asked of a host whose DssHarness is to be updated: a hold is a DssHarness running there too, and one
        // that stands would keep the update off until its seconds ran out. It stops as it sees its state gone.
        if (request.HoldAwakeSeconds == 0)
        {
            try
            {
                _holds.End();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return await RefuseAsync(error, HarnessExit.HostUnavailable, $"the hold here could not be ended: {ex.Message.TrimEnd('.')}").ConfigureAwait(false);
            }

            return HarnessExit.Success;
        }

        if (request.KeepAwake.Count == 0 || request.HoldAwakeSeconds < 1)
        {
            return await RefuseAsync(error, HarnessExit.UsageError, "the hold request names no keepAwake command to hold this host with, or no seconds to hold it for").ConfigureAwait(false);
        }

        var generation = HostAgentProtocol.NewNonce();

        try
        {
            _holds.Write(new HoldAwakeState(
                generation,
                DateTimeOffset.UtcNow.AddSeconds(request.HoldAwakeSeconds),
                [.. request.KeepAwake],
                new Dictionary<string, string>(request.KeepAwakeEnvironment, StringComparer.OrdinalIgnoreCase),
                [.. request.KeepAwakeDirectories]));

            _launcher.StartSelf([HoldAwakeService.CommandName, generation]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ProgramStartException)
        {
            // Nothing holds the host: a state written for a process that never started is ended with it.
            _holds.End();

            return await RefuseAsync(error, HarnessExit.HostUnavailable, $"this host could not be held awake: {ex.Message.TrimEnd('.')}").ConfigureAwait(false);
        }

        return HarnessExit.Success;
    }

    /// <summary>
    /// Which build this is, what this machine is, which of <paramref name="emulators"/> work here, and
    /// which of <paramref name="developerEnvironments"/> can be set up here.
    /// </summary>
    /// <param name="emulators">The emulators to check, by name.</param>
    /// <param name="developerEnvironments">The developer environments to look for, by name.</param>
    /// <param name="programs">The programs to find, the way a leg here will start them.</param>
    /// <param name="searchDirectories">The repository's <c>toolSearchDirectories</c>, of which this machine takes its own platform's.</param>
    /// <param name="room">Where to measure the room here, and which build directories to measure and read the record of.</param>
    /// <param name="cancellationToken">Stops the checks.</param>
    public async Task<HostAgentInfo> DescribeAsync(
        IReadOnlyDictionary<string, EmulatorConfig> emulators,
        IReadOnlyDictionary<string, DeveloperEnvironmentConfig> developerEnvironments,
        IReadOnlyList<string> programs,
        IReadOnlyDictionary<string, List<string>> searchDirectories,
        RoomQuestions room,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(emulators);
        ArgumentNullException.ThrowIfNull(developerEnvironments);
        ArgumentNullException.ThrowIfNull(programs);
        ArgumentNullException.ThrowIfNull(searchDirectories);
        ArgumentNullException.ThrowIfNull(room);

        // By the function the leg's own run uses, on the machine that will run it. Answered from
        // anywhere else this would be a second opinion about this machine's PATH, and a second
        // opinion is how a survey came to call a leg runnable whose build tool could not be found.
        // The emulators' own programs are found by the same search, in the same pass, so a launcher
        // installed off the PATH is found - and handed on - exactly as a build tool would be.
        var found = _programs.Resolve(
            programs.Concat(emulators.Values.SelectMany(EmulatorProbe.ProgramsOf)),
            ToolSearchDirectories.For(searchDirectories, _platform.PlatformKey));

        var checks = new Dictionary<string, EmulatorCheck>(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, emulator) in emulators)
        {
            checks[name] = await _emulatorProbe.CheckAsync(emulator, found, cancellationToken).ConfigureAwait(false);
        }

        // Looked for, never set up: the survey has to be cheap, and setting one up is the run's work,
        // on the machine that runs the leg.
        var environments = new Dictionary<string, DeveloperEnvironmentCheck>(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, environment) in developerEnvironments)
        {
            environments[name] = await _developerEnvironmentProbe.CheckAsync(environment, cancellationToken).ConfigureAwait(false);
        }

        var current = _identity.Current;

        // Measured here, where the builds will write, and cheaply: the room is the filesystem's own count,
        // and what a build directory holds is what the build that last finished there recorded, never a walk.
        var (space, unmeasured) = room.SpaceAt is { Length: > 0 } spaceAt ? DiskSpace.Measure(_fileSystem, ResolveDirectory(spaceAt)) : (null, null);

        return new HostAgentInfo
        {
            Version = current.Version,
            AssemblySha256 = current.AssemblySha256,
            Os = _platform.PlatformKey,
            Processor = _platform.Processor,
            Emulators = checks,
            DeveloperEnvironments = environments,
            Programs = [.. found.Found.Values],
            ProgramDirectories = [.. found.Directories],
            Space = space,
            SpaceUnmeasured = unmeasured,
            Builds = [.. room.Builds.Distinct(StringComparer.Ordinal).Select(BuildRoom)],
        };
    }

    /// <summary>
    /// <paramref name="info"/> as the machine that asked is told it: every reason, and every place the answer
    /// only describes - where each program is, where a filesystem is mounted, the instance a developer
    /// environment would be set up from - with this host's home as <c>~</c>.
    /// </summary>
    /// <remarks>
    /// Told so, except for what the machine that asked uses as it is. The directories the programs were found
    /// in are written in full, because it hands them back for a hold here to look for its command in; and each
    /// build directory as it was asked about, because it matches the answer to its question by that text. What
    /// an emulator's witness printed where it worked is the witness's own words, and stays as printed; a reason
    /// quoting a witness that failed is this host's own line, told like every other.
    /// <see cref="DescribeAsync"/> itself answers as it found, because this machine sets its own developer
    /// environments up from what it answers there.
    /// </remarks>
    private HostAgentInfo Told(HostAgentInfo info) => info with
    {
        Emulators = info.Emulators.ToDictionary(
            pair => pair.Key,
            pair => pair.Value with { Reason = Told(pair.Value.Reason) },
            StringComparer.OrdinalIgnoreCase),
        DeveloperEnvironments = info.DeveloperEnvironments.ToDictionary(
            pair => pair.Key,
            pair => pair.Value with { Reason = Told(pair.Value.Reason), InstallationPath = Told(pair.Value.InstallationPath) },
            StringComparer.OrdinalIgnoreCase),
        Programs = [.. info.Programs.Select(program => program with { Path = Told(program.Path), Reason = Told(program.Reason) })],
        Space = Told(info.Space),
        SpaceUnmeasured = Told(info.SpaceUnmeasured),
        Builds = [.. info.Builds.Select(build => build with { Disk = Told(build.Disk), Unmeasured = Told(build.Unmeasured) })],
    };

    private string? Told(string? text) => text is null ? null : _home.Shown(text);

    private DiskSpace? Told(DiskSpace? space) => space is null ? null : space with { Filesystem = _home.Shown(space.Filesystem) };

    /// <summary>
    /// What the build directory <paramref name="asked"/> holds, as the build that last finished there recorded
    /// it, and the room where it is: a record that cannot be read records nothing.
    /// </summary>
    private BuildDirectoryRoom BuildRoom(string asked)
    {
        var directory = ResolveDirectory(asked);
        var (space, unmeasured) = DiskSpace.Measure(_fileSystem, directory);

        return new BuildDirectoryRoom(asked, _fileSystem.DirectoryExists(directory), Build.BuildRecord.BytesIn(_fileSystem, directory), space, unmeasured);
    }

    /// <summary>Marks where this request's own output begins, on each stream that carries any of it.</summary>
    /// <param name="output">Where a command's own standard output is forwarded.</param>
    /// <param name="error">Where a command's own standard error, and the completion line, are forwarded.</param>
    /// <param name="nonce">The request's nonce, which the marker carries so no other output is taken for it.</param>
    /// <remarks>
    /// Written whatever has been cancelled since, as the completion line is: the machine that asked relays
    /// nothing until it has seen this, so a marker withheld because the input had already ended would lose
    /// the whole of what the request then says about itself.
    /// </remarks>
    private static async Task WriteStartedAsync(TextWriter output, TextWriter error, string nonce)
    {
        var started = HostAgentProtocol.StartedLine(nonce);

        await output.WriteLineAsync(started).ConfigureAwait(false);
        await output.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        await error.WriteLineAsync(started).ConfigureAwait(false);
        await error.FlushAsync(CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// Serves a run request, then writes its completion line last. The machine that asked reads the command's
    /// exit code from that line, and a line that never arrives tells it the connection failed first.
    /// </summary>
    private async Task<int> RunAsync(
        HostAgentRequest request,
        TextWriter output,
        TextWriter error,
        Func<string, string[], Dispatch, CancellationToken, Task<int>> run,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Nonce))
        {
            return await RefuseAsync(
                error,
                HarnessExit.UsageError,
                "the run request carries no nonce, so how its command finished could not be reported").ConfigureAwait(false);
        }

        // Written on both streams before anything this request says, so the machine that asked can drop
        // whatever the host's login shell wrote to either of them before the agent ever ran. A profile is the
        // host's business and not this run's output: one consumer's printed the account's home layout on
        // every session, and a relayed line published it.
        await WriteStartedAsync(output, error, request.Nonce).ConfigureAwait(false);

        var exitCode = await ServeRunAsync(request, error, run, cancellationToken).ConfigureAwait(false);

        await error.WriteLineAsync(HostAgentProtocol.CompletionLine(request.Nonce, exitCode)).ConfigureAwait(false);
        await error.FlushAsync(CancellationToken.None).ConfigureAwait(false);

        return exitCode;
    }

    private async Task<int> ServeRunAsync(
        HostAgentRequest request,
        TextWriter error,
        Func<string, string[], Dispatch, CancellationToken, Task<int>> run,
        CancellationToken cancellationToken)
    {
        if (request.Arguments.Count == 0)
        {
            return await RefuseAsync(error, HarnessExit.UsageError, "the request names no command to run").ConfigureAwait(false);
        }

        if (HostAgentProtocol.IsNotForwardable(request.Arguments[0].Text))
        {
            return await RefuseAsync(
                error,
                HarnessExit.UsageError,
                $"'{request.Arguments[0]}' cannot be run on a host; run it on the machine that reaches the hosts").ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(request.Directory))
        {
            return await RefuseAsync(error, HarnessExit.UsageError, "the request names no directory to run in").ConfigureAwait(false);
        }

        // A run a leg here records its heavy-leg slot under, and a drive it holds its build to the floor on.
        Dispatch dispatch;

        try
        {
            dispatch = Dispatch.Of(request);
        }
        catch (HarnessException ex)
        {
            return await RefuseAsync(error, ex.ExitCode, ex.Message).ConfigureAwait(false);
        }

        var directory = ResolveDirectory(request.Directory);

        if (!_fileSystem.DirectoryExists(directory))
        {
            return await RefuseAsync(
                error,
                HarnessExit.HostUnavailable,
                $"this host has no copy of the repository at '{directory}'").ConfigureAwait(false);
        }

        try
        {
            // Held for as long as this request is served, by the command the machine that asked carries for
            // this host, under the environment and the program directories it carries with it: a host's copy
            // has no configuration to read until a first sync has put one there, and a keepAwake program
            // found only through the host's own env or a searched directory would not start without them.
            // A sync is many requests, and its first to a fresh copy is the longest work a host does with no
            // leg of its own running there - which is the only thing that used to hold it awake. The command
            // names this process, so it ends with the request however the connection ends.
            //
            // Inside the try, because filling the command's placeholders refuses a request that names one
            // this build cannot fill, and a refusal raised outside it would be reported as a request that
            // never said how it finished rather than as the configuration error it is.
            await using var awake = _keepAwake.Hold(
                HostAgentProtocol.CommandName,
                request.Arguments[0].Text,
                new LocalHostConfig { KeepAwake = [.. request.KeepAwake], Env = new(request.KeepAwakeEnvironment, StringComparer.OrdinalIgnoreCase) },
                [.. request.KeepAwakeDirectories],
                cancellationToken);

            return await run(directory, [.. request.Arguments.Select(argument => argument.Text)], dispatch, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // A command reports its own failures as exit codes, so what escapes here is entering the copy: a
            // directory this user may not enter exists all the same.
            return await RefuseAsync(
                error,
                HarnessExit.HostUnavailable,
                $"this host's copy of the repository at '{directory}' could not be entered: {ex.Message}").ConfigureAwait(false);
        }
        catch (HarnessException ex)
        {
            // What the request itself asked for could not be done - a keepAwake command naming a placeholder
            // this build cannot fill, above all. Said as the refusal it is, under this host's name.
            return await RefuseAsync(error, ex.ExitCode, ex.Message).ConfigureAwait(false);
        }
    }

    /// <summary>Expands a leading <c>~</c> to this user's home directory, the way host configuration writes a path.</summary>
    private string ResolveDirectory(string directory)
    {
        if (directory == "~")
        {
            return _platform.HomeDirectory;
        }

        return Platform.PlatformPaths.IsHomeRelative(directory)
            ? Path.GetFullPath(Path.Combine(_platform.HomeDirectory, directory[2..]))
            : directory;
    }

    /// <summary>Reads the protocol a request speaks, and nothing else of it. A request that names none speaks this build's.</summary>
    private static bool TryReadProtocol(string line, out int protocol, out string problem)
    {
        protocol = HostAgentProtocol.Version;
        problem = string.Empty;

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                problem = "it is not a JSON object";
                return false;
            }

            if (root.TryGetProperty("protocol", out var value)
                && (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out protocol)))
            {
                problem = "its protocol is not a whole number";
                return false;
            }

            return true;
        }
        catch (JsonException ex)
        {
            problem = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Reads <paramref name="input"/> to its end, noting in <paramref name="heard"/> when anything was last read on it -
    /// whatever follows the request is a beat, read as nothing else - and says the machine that asked has gone once the
    /// input ends, or cannot be read.
    /// </summary>
    private static async Task WatchInputAsync(TextReader input, StrongBox<long> heard, Gone gone)
    {
        var buffer = new char[256];
        string why;

        try
        {
            while (await input.ReadAsync(buffer.AsMemory(), CancellationToken.None).ConfigureAwait(false) > 0)
            {
                Volatile.Write(ref heard.Value, Stopwatch.GetTimestamp());
            }

            why = "the input it holds open here ended";
        }
        catch (Exception ex)
        {
            // Whatever the failure: input that can no longer be read has ended as surely as input that was closed, and one
            // nobody caught would leave what the request started running with nothing left to end it.
            why = $"the input it holds open here could not be read: {ex.Message.TrimEnd('.')}";
        }

        // Said to nobody: a machine whose end of the input is gone reads nothing this one writes.
        await gone.BecauseAsync(why, say: false).ConfigureAwait(false);
    }

    /// <summary>
    /// Says the machine that asked has gone once nothing has been read on its input for <paramref name="silence"/>:
    /// looked at four times in that long, until <paramref name="served"/>.
    /// </summary>
    private static async Task WatchBeatAsync(TimeSpan silence, StrongBox<long> heard, Gone gone, CancellationToken served)
    {
        try
        {
            while (Stopwatch.GetElapsedTime(Volatile.Read(ref heard.Value)) < silence)
            {
                await Task.Delay(silence / 4, served).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            return;
        }

        // Said, because this is the one sign that may be wrong: a machine still there, whose beats were held up, reads why
        // its run was stopped.
        await gone.BecauseAsync(
            $"it wrote nothing on the input it holds open here for {Math.Ceiling(silence.TotalSeconds).ToString(CultureInfo.InvariantCulture)} s, where it "
            + "writes a beat while it is there",
            say: true).ConfigureAwait(false);
    }

    /// <summary>
    /// That the machine that asked has gone, said once, by whichever sign of it comes first: what the request started is
    /// cancelled, and why is written where that machine would read it were it still there.
    /// </summary>
    /// <param name="abandoned">Cancels what the request started.</param>
    /// <param name="error">Where why is written; <see langword="null"/> to say nothing, as for a request that starts no command.</param>
    private sealed class Gone(CancellationTokenSource abandoned, TextWriter? error)
    {
        private int _settled;

        /// <summary>Why the machine that asked was taken to have gone; <see langword="null"/> where it was not.</summary>
        public string? Why { get; private set; }

        /// <summary>The request was answered: no sign that comes after it says or cancels anything.</summary>
        public void Served() => Interlocked.Exchange(ref _settled, 1);

        /// <summary>
        /// The machine that asked has gone, <paramref name="why"/> saying how that was told, and <paramref name="say"/>
        /// whether it is written where that machine would read it.
        /// </summary>
        public async Task BecauseAsync(string why, bool say)
        {
            if (Interlocked.Exchange(ref _settled, 1) != 0)
            {
                return;
            }

            Why = why;

            // Said before anything is cancelled, so that it stands before whatever the command says as it stops, and its
            // completion line; and never waited for long, since a line nobody reads may never finish being written.
            if (say && error is not null)
            {
                var said = SayAsync(
                    error,
                    [FailureLine.For(HostAgentProtocol.CommandName, $"the machine that asked has gone - {why} - so what it started here is stopped")]);

                await Task.WhenAny(said, Task.Delay(SayingBudget, CancellationToken.None)).ConfigureAwait(false);
            }

            try
            {
                await abandoned.CancelAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                // The request was served, and its token disposed, as this was told.
            }
        }

        /// <summary>Writes <paramref name="lines"/> where the machine that asked reads, which may no longer be there to.</summary>
        public static async Task SayAsync(TextWriter error, IReadOnlyList<string> lines)
        {
            try
            {
                foreach (var line in lines)
                {
                    await error.WriteLineAsync(line).ConfigureAwait(false);
                }

                await error.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // Nobody is reading.
            }
        }
    }

    /// <summary>Refuses the request, saying why as the machine that asked is told it.</summary>
    private async Task<int> RefuseAsync(TextWriter error, int exitCode, string message)
    {
        await error.WriteLineAsync(FailureLine.For(HostAgentProtocol.CommandName, _home.Shown(message))).ConfigureAwait(false);
        return exitCode;
    }
}
