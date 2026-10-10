using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.Execution;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Processes;
using RepoHarness.Core.Results;

namespace RepoHarness.Core.Hosts;

/// <summary>
/// What one DssHarness asks the DssHarness on a host, and what it answers. A request travels as one
/// line of JSON on the host's standard input, so no argument ever passes through a shell. The machine that
/// asked then holds that input open until the host has finished: its end means the machine that asked has
/// gone, and the host cancels whatever the request started.
/// </summary>
public static class HostAgentProtocol
{
    /// <summary>The hidden command a host serves requests with.</summary>
    public const string CommandName = "host-agent";

    /// <summary>
    /// The option that has a host report a defect of its own with its stack trace, and that a leg's command run there
    /// takes to show its steps' output, as <c>-v</c> does here.
    /// </summary>
    public const string VerboseOption = "--verbose";

    /// <summary>
    /// The protocol version. Both ends are the same build by the time a run request is sent, so a
    /// difference is a defect rather than something to negotiate.
    /// </summary>
    /// <remarks>
    /// Raised whenever a request or an answer changes shape. A host reads the version before anything
    /// else, so one on another build refuses a request as coming from another protocol, naming both
    /// and its own version. With the number left as it was, the same host refuses the request over
    /// whichever field it happens not to know, which says nothing about why.
    /// </remarks>
    public const int Version = 8;

    /// <summary>
    /// How often, in seconds, the machine that asked writes <see cref="BeatLine"/> on the input it holds open while a run
    /// request is served: what every request says of itself, unless it says otherwise.
    /// </summary>
    public const int BeatSeconds = 15;

    /// <summary>
    /// How many beats in a row a host lets pass unheard before it takes the machine that asked to have gone, and cancels
    /// what the request started: enough that a connection stalled for a moment, or a machine busy for one, loses no run.
    /// </summary>
    public const int BeatsMissed = 8;

    /// <summary>What a beat writes: anything read on the input counts as one, and this says what it is to whoever reads a capture.</summary>
    public const string BeatLine = CommandName + ": beat";

    /// <summary>
    /// The beat the machine that asked writes for <paramref name="request"/> while it holds its input open, as the request
    /// says it will; <see langword="null"/> where the request says it writes none.
    /// </summary>
    /// <param name="request">The request.</param>
    public static InputBeat? BeatOf(HostAgentRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return request.BeatSeconds > 0 ? new InputBeat(TimeSpan.FromSeconds(request.BeatSeconds), BeatLine) : null;
    }

    /// <summary>
    /// How requests and answers are written. Dictionaries and lists are read with the converters
    /// configuration is read with, so an emulator arrives on a host as config.json declared it: its
    /// names compared ignoring case, and no list holding a null.
    /// </summary>
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        RespectNullableAnnotations = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters =
        {
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase),
            new CaseInsensitiveDictionaryConverter(),
            new NonNullListConverter(),
        },
    };

    /// <summary>
    /// Commands a host is never asked to run. A host that passed the work on to another host would leave
    /// the machine that asked unable to say where anything ran.
    /// </summary>
    public static IReadOnlyList<string> NotForwardable { get; } = [CommandName, HostExecService.CommandName, Execution.HoldAwakeService.CommandName];

    /// <summary>Whether <paramref name="command"/> is one a host is never asked to run, in whatever case it is typed.</summary>
    public static bool IsNotForwardable(string command) => NotForwardable.Contains(command, StringComparer.OrdinalIgnoreCase);

    /// <summary>A new value for <see cref="HostAgentRequest.Nonce"/>: random, so no output of a command holds it by chance.</summary>
    public static string NewNonce() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    /// <summary>
    /// The line a host writes to standard error last, once the command a run request named has finished,
    /// carrying that command's exit code. The machine that asked takes the exit code from this line rather
    /// than from the transport: ssh and wsl.exe exit with codes of their own when a connection fails, and a
    /// line that never arrives is how such a failure is told apart from the command's own result.
    /// </summary>
    public static string CompletionLine(string nonce, int exitCode)
        => $"{CommandName}: finished {nonce} {exitCode.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>
    /// The line a host writes on each stream first, before it serves a run request, so that the machine that
    /// asked can tell what the agent says from what the host's login shell said before it.
    /// </summary>
    /// <remarks>
    /// A login shell writes to the same streams the command does, and whatever it writes arrives first. One
    /// consumer's Mac sources emsdk's environment script on every session, which prints the account's home
    /// layout - the user's name among it - and a machine relaying a leg's output published it into a ledger,
    /// a CI log and a chat transcript. What a host's profile says is not this run's output and does not
    /// belong in it; the agent's own words, from here on, are.
    /// </remarks>
    public static string StartedLine(string nonce)
        => $"{CommandName}: serving {nonce}";

    /// <summary>Whether <paramref name="line"/> carries the started line of the request that sent <paramref name="nonce"/>.</summary>
    /// <remarks>
    /// Matched as the end of the line rather than the whole of it. A login profile whose last write has no
    /// trailing newline - a prompt, an escape sequence, an <c>echo -n</c> - glues its bytes onto the first
    /// line the agent writes, which is this one. Held to the whole line, such a host would never open the
    /// gate at all, and every run on it would report as one that never said how it finished.
    /// </remarks>
    public static bool IsStartedLine(string line, string nonce)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentException.ThrowIfNullOrWhiteSpace(nonce);

        return line.TrimEnd().EndsWith(StartedLine(nonce), StringComparison.Ordinal);
    }

    /// <summary>
    /// <paramref name="text"/> from the agent's started line on, or the whole of it where that line is not
    /// in it: what a message quotes from a whole captured stream, rather than the host's login shell too.
    /// </summary>
    /// <param name="text">Everything a stream carried.</param>
    /// <param name="nonce">The request's nonce.</param>
    /// <remarks>
    /// The capture is kept whole for the reader who asks for it, and trimmed wherever it is quoted into
    /// something the harness says: a host that never reached its agent has nothing else to show, so there
    /// the whole of it is the answer. One consumer's profile prints the account's home layout on every
    /// session, and an excerpt of a short stream is otherwise nothing but that.
    /// </remarks>
    public static string SinceServing(string text, string nonce)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentException.ThrowIfNullOrWhiteSpace(nonce);

        var started = text.LastIndexOf(StartedLine(nonce), StringComparison.Ordinal);

        return started < 0 ? text : text[(started + StartedLine(nonce).Length)..].TrimStart('\r', '\n');
    }

    /// <summary>
    /// Whether <paramref name="line"/> is one the agent itself wrote under its own name, which is relayed
    /// even before the started line: a request refused before it could be read carries no nonce to mark.
    /// </summary>
    /// <param name="line">A line the host wrote.</param>
    public static bool IsAgentsOwnLine(string line)
    {
        ArgumentNullException.ThrowIfNull(line);

        return line.TrimStart().StartsWith(CommandName + ": ", StringComparison.Ordinal);
    }

    /// <summary>Reads <paramref name="line"/> as the completion line of the request that carried <paramref name="nonce"/>.</summary>
    public static bool TryReadCompletionLine(string line, string nonce, out int exitCode)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentException.ThrowIfNullOrWhiteSpace(nonce);

        exitCode = 0;
        var prefix = $"{CommandName}: finished {nonce} ";

        return line.StartsWith(prefix, StringComparison.Ordinal)
            && int.TryParse(line.AsSpan(prefix.Length), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out exitCode);
    }

    /// <summary>
    /// How many bytes are encoded as base64 at a time, whichever way they cross: an argument carrying bytes to a host
    /// (<see cref="HostArgument.Carrying"/>), and each line a file read there follows its answer back with
    /// (<see cref="Sync.SyncServe.ContentLines"/>). A multiple of three, so each piece is whole base64, and small enough that
    /// its text is an object the runtime collects as soon as it is dropped, rather than one of the large ones it collects
    /// only with everything else.
    /// </summary>
    public const int CarriedPiece = 24 * 1024;

    /// <summary>How much of a request is gathered before it is written on: what a piece of what it carries encodes to, twice.</summary>
    private const int Gathered = CarriedPiece / 3 * 4 * 2;

    /// <summary>
    /// <paramref name="request"/> as a host reads it: one line of JSON on its standard input, written as it is encoded, so
    /// that nothing the size of what it carries - a batch of files a sync writes there - is ever held here as text. Written
    /// again, from the start, where a call over ssh that failed before any session began is made again.
    /// </summary>
    /// <param name="request">The request.</param>
    public static ChildInput Input(HostAgentRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return ChildInput.WrittenBy(stream =>
        {
            var gathered = new GatheredWrites(stream);

            using (var writer = new Utf8JsonWriter(gathered, new JsonWriterOptions { Encoder = JsonOptions.Encoder }))
            {
                JsonSerializer.Serialize(writer, request, JsonOptions);
            }

            gathered.Line();
        });
    }

    /// <summary>
    /// What a request is written into: a buffer of its own, written on to the stream whenever the writer needs more room
    /// than is left, so the request is never held whole. Grown only for one value longer than the buffer, which an argument
    /// carrying bytes never is: it is written a piece at a time.
    /// </summary>
    private sealed class GatheredWrites(Stream stream) : IBufferWriter<byte>
    {
        private byte[] _buffer = new byte[Gathered];
        private int _written;

        public void Advance(int count) => _written += count;

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            MakeRoom(sizeHint);
            return _buffer.AsMemory(_written);
        }

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            MakeRoom(sizeHint);
            return _buffer.AsSpan(_written);
        }

        /// <summary>Ends the request's line, and writes on whatever is gathered.</summary>
        public void Line()
        {
            GetSpan(1)[0] = (byte)'\n';
            Advance(1);
            WriteOn();
            stream.Flush();
        }

        /// <summary>Leaves at least <paramref name="sizeHint"/> bytes of room after what is gathered.</summary>
        private void MakeRoom(int sizeHint)
        {
            var needed = Math.Max(sizeHint, 1);

            if (_buffer.Length - _written >= needed)
            {
                return;
            }

            WriteOn();

            if (_buffer.Length < needed)
            {
                _buffer = new byte[needed];
            }
        }

        private void WriteOn()
        {
            stream.Write(_buffer, 0, _written);
            _written = 0;
        }
    }
}

/// <summary>What a request asks for.</summary>
public enum HostAgentRequestKind
{
    /// <summary>Which build answers, what the host is, and which emulators work there.</summary>
    Info,

    /// <summary>Run one DssHarness command in a directory on the host.</summary>
    Run,

    /// <summary>
    /// Hold the host awake until the next command's own keepAwake takes over, or the hold's seconds are up:
    /// started, detached, and answered at once.
    /// </summary>
    Hold,
}

/// <summary>A request to the DssHarness on a host.</summary>
public sealed class HostAgentRequest
{
    /// <summary>The protocol the request is written in.</summary>
    public int Protocol { get; init; } = HostAgentProtocol.Version;

    /// <summary>What is asked.</summary>
    public required HostAgentRequestKind Kind { get; init; }

    /// <summary>The emulators to check, by name. Info only.</summary>
    public Dictionary<string, EmulatorConfig> Emulators { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The developer environments to look for, by name. Info only.</summary>
    public Dictionary<string, DeveloperEnvironmentConfig> DeveloperEnvironments { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The programs to find there, the way a leg on that host will start them. Info only.
    /// </summary>
    public List<string> Programs { get; init; } = [];

    /// <summary>
    /// The repository's <c>toolSearchDirectories</c>, of which the host takes its own platform's.
    /// Info only.
    /// </summary>
    /// <remarks>
    /// Sent whole rather than chosen here, because only the host knows which platform it is until it
    /// has answered, and asking twice would be a second round trip for one question.
    /// </remarks>
    public Dictionary<string, List<string>> ToolSearchDirectories { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Where to measure the room on the host's filesystem - where its copies of the repository are kept -
    /// absolute or from the home directory; <see langword="null"/> to measure none. Info only.
    /// </summary>
    public string? SpaceAt { get; init; }

    /// <summary>
    /// The build directories there, absolute or from the home directory, whose room to measure and whose
    /// record to read: each selected leg's own, and every other copy's there of the same variant - the main checkout's and
    /// each worktree's. Info only.
    /// </summary>
    public List<string> Builds { get; init; } = [];

    /// <summary>
    /// How long a hold keeps the host awake, at most, in seconds; zero ends the hold that stands there, with
    /// nothing held after it. Hold only.
    /// </summary>
    public int HoldAwakeSeconds { get; init; }

    /// <summary>
    /// The command that keeps the host awake while this request is served, as the configuration declares it
    /// for that host, or empty where it declares none: for a <see cref="HostAgentRequestKind.Run"/>, and the
    /// command a <see cref="HostAgentRequestKind.Hold"/> holds the host with.
    /// </summary>
    /// <remarks>
    /// Carried rather than read there, because the host's copy has no configuration until a first sync has
    /// put one in it - and a first sync is the longest one, the one that most needs the host to stay awake.
    /// Measured by a consumer: a first sync of a worktree's copy to a Mac ran past the host's wake and the
    /// legs were left unavailable. The command's <c>{pid}</c> becomes the agent's own process there, so it
    /// ends with the request whatever happens to this end of the connection.
    /// </remarks>
    public List<string> KeepAwake { get; init; } = [];

    /// <summary>
    /// What the host declares under <c>env</c> for itself, which the <see cref="KeepAwake"/> command starts
    /// under, as a leg's own work does. Empty where the host declares none.
    /// </summary>
    /// <remarks>
    /// Carried with the command, for the same reason the command is: the host's copy has no configuration to
    /// read until a first sync has put one there. Without it a command that starts for a leg - because a leg
    /// supplies the host's environment - would not start here.
    /// </remarks>
    public Dictionary<string, string> KeepAwakeEnvironment { get; init; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Where the survey found this host's programs, which the <see cref="KeepAwake"/> command is looked for
    /// in, as a leg's own programs are. Empty where nothing was surveyed.
    /// </summary>
    public List<string> KeepAwakeDirectories { get; init; } = [];

    /// <summary>
    /// The directory the command starts in on the host, absolute or from the home directory: the host's copy of the
    /// tree it runs in, or, for a sync's own operations, the directory that copy is kept in, which is there before the
    /// copy is. Run only.
    /// </summary>
    public string? Directory { get; init; }

    /// <summary>
    /// The command and its arguments, exactly as they would be typed after <c>DssHarness</c>, an argument carrying bytes
    /// as their base64 text. Run only.
    /// </summary>
    public List<HostArgument> Arguments { get; init; } = [];

    /// <summary>
    /// The run of the machine that asked, where the command runs a leg of it: what a leg there asking the host's heavy-leg
    /// slots is recorded under, so the legs of one command wait for each other's slots without that wait counting against
    /// the host's limit. Run only; <see langword="null"/> where the command runs no leg of a run, as a sync's operations.
    /// </summary>
    /// <remarks>
    /// Carried beside the command rather than in its arguments: nothing on a command line a host runs may say who asked
    /// for it, or the command would answer to an argument its own user never typed.
    /// </remarks>
    public string? RunId { get; init; }

    /// <summary>
    /// For a leg of a WSL distribution, the drive of the machine that asked where WSL keeps the distribution's disk, as that
    /// machine names it - <c>C:\</c> - which the leg's build fills as the disk grows, and which the distribution's own room
    /// does not show: the leg holds its build to the floor on it through the drive's mount there. Run only;
    /// <see langword="null"/> for any other leg, or where that machine could not measure the drive.
    /// </summary>
    public string? DiskImageDrive { get; init; }

    /// <summary>
    /// How often, in seconds, the machine that asked writes a beat on the input it holds open while the request is
    /// served; zero where it writes none. Run only: a host that hears nothing for
    /// <see cref="HostAgentProtocol.BeatsMissed"/> beats takes that machine to have gone, as it does when the input ends,
    /// and cancels what the request started.
    /// </summary>
    /// <remarks>
    /// The end of the input is how a host learns the machine that asked has gone, and it is only as good as whatever
    /// carries the input: a consumer's leg ran on to its end on a host reached over ssh, with nobody reading it, after its
    /// dispatcher was killed, where the same leg on a host reached through WSL was cancelled at once. A beat that stops
    /// says so on every carriage.
    /// </remarks>
    public int BeatSeconds { get; init; } = HostAgentProtocol.BeatSeconds;

    /// <summary>
    /// A value the machine that asked chose for this request, repeated in the host's completion line so
    /// that nothing the command prints can be taken for that line, and in the line that marks where the
    /// host's own answer begins.
    /// </summary>
    /// <remarks>
    /// A run request is refused without one, because how its command finished could not then be reported.
    /// An info request answers without one, from a build that sent none: only the marker is lost, and with
    /// it the trimming of whatever the host's login shell printed first.
    /// </remarks>
    public string? Nonce { get; init; }
}

/// <summary>A host's answer to an info request.</summary>
/// <remarks>
/// A record, so the answer the machine that asked is told can be this one with the fields it tells otherwise,
/// and every other field kept as it is.
/// </remarks>
public sealed record HostAgentInfo
{
    /// <summary>The version of DssHarness that answered.</summary>
    public required string Version { get; init; }

    /// <summary>The SHA-256 of the assembly that answered.</summary>
    public required string AssemblySha256 { get; init; }

    /// <summary>The host's operating system, in configuration's words.</summary>
    public required string Os { get; init; }

    /// <summary>The host's processor, in configuration's words.</summary>
    public required string Processor { get; init; }

    /// <summary>What checking each requested emulator found.</summary>
    public Dictionary<string, EmulatorCheck> Emulators { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>What looking for each requested developer environment found.</summary>
    public Dictionary<string, DeveloperEnvironmentCheck> DeveloperEnvironments { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Where each requested program is there, each carrying the name it was asked for.</summary>
    /// <remarks>
    /// A list rather than a map keyed by name. A name compares exactly - cmake and CMake are two files
    /// on Linux, and a repository can ask about both - while a map in this protocol is read back
    /// ignoring case, as configuration's names are, and refuses an answer holding both.
    /// </remarks>
    public List<ProgramLocation> Programs { get; init; } = [];

    /// <summary>
    /// The directories a program asked for by name was found in there, on the PATH or off it, in the
    /// order the search looked: what a leg there appends to the PATH of every process it starts.
    /// </summary>
    public List<string> ProgramDirectories { get; init; } = [];

    /// <summary>
    /// The room on the filesystem the request's <see cref="HostAgentRequest.SpaceAt"/> is on; <see langword="null"/>
    /// where none was asked about, or it could not be measured.
    /// </summary>
    public DiskSpace? Space { get; init; }

    /// <summary>Why the room at <see cref="HostAgentRequest.SpaceAt"/> could not be measured, where it could not.</summary>
    public string? SpaceUnmeasured { get; init; }

    /// <summary>
    /// Each build directory the request asked about, as it asked: a list rather than a map, because a path
    /// compares exactly on Linux, and a map in this protocol is read back ignoring case.
    /// </summary>
    public List<BuildDirectoryRoom> Builds { get; init; } = [];
}

/// <summary>
/// What the machine that asked a host to run a command says of it beside the command line - never on it, since nothing on
/// a command line a host runs may say who asked for it.
/// </summary>
public sealed record Dispatch
{
    private Dispatch(RunId? runId, string? diskImageDrive)
    {
        RunId = runId;
        DiskImageDrive = diskImageDrive;
    }

    /// <summary>The run of that machine the command runs a leg of; <see langword="null"/> where it runs none.</summary>
    public RunId? RunId { get; }

    /// <summary>
    /// For a leg of a WSL distribution, the drive of that machine where WSL keeps the distribution's disk - see
    /// <see cref="HostAgentRequest.DiskImageDrive"/> - and never a blank one; <see langword="null"/> where none was named.
    /// </summary>
    public string? DiskImageDrive { get; }

    /// <summary>
    /// What <paramref name="request"/> says of the command it asks for, read as strictly as anywhere else: its run as a run
    /// id, which a leg here records its heavy-leg slot under, and its drive as one named, which a leg here holds its build
    /// to the floor on.
    /// </summary>
    /// <param name="request">A run request.</param>
    /// <exception cref="HarnessException">
    /// The request names its run by what is no run id, or names a blank drive: refused, and nothing it asks for runs.
    /// </exception>
    public static Dispatch Of(HostAgentRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        RunId? run = null;

        if (request.RunId is { } named)
        {
            run = Execution.RunId.TryParse(named, out var parsed)
                ? parsed
                : throw new HarnessException(HarnessExit.UsageError, $"the request names its run as '{named}', which is not a run id");
        }

        return request.DiskImageDrive is { } drive && string.IsNullOrWhiteSpace(drive)
            ? throw new HarnessException(HarnessExit.UsageError, "the request names a blank drive as where WSL keeps the distribution's disk")
            : new Dispatch(run, request.DiskImageDrive);
    }
}

/// <summary>What a build directory on a host holds, as its record says, and the room where it is.</summary>
/// <param name="Path">The directory, as it was asked about.</param>
/// <param name="Exists">Whether it is there.</param>
/// <param name="RecordedBytes">
/// What its files held together when a build last finished there, as that build recorded it; <see langword="null"/>
/// where no build recorded it - one that never finished, or an earlier version.
/// </param>
/// <param name="Disk">The room on its filesystem - where it will be, where it is not there - or <see langword="null"/>.</param>
/// <param name="Unmeasured">Why the room could not be measured, where it could not.</param>
public sealed record BuildDirectoryRoom(string Path, bool Exists, long? RecordedBytes, DiskSpace? Disk, string? Unmeasured);

/// <summary>Whether an emulator works on a host.</summary>
/// <param name="Available">Whether legs can run through it there.</param>
/// <param name="Reason">Why they cannot, when they cannot.</param>
/// <param name="Witnessed">What its witness printed, when it worked.</param>
public sealed record EmulatorCheck(bool Available, string? Reason, string? Witnessed)
{
    /// <summary>An emulator legs cannot run through, and why.</summary>
    public static EmulatorCheck Unavailable(string reason) => new(false, reason, null);
}
