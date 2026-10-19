using System.ComponentModel;
using System.Globalization;
using System.IO.Pipes;
using System.Text;
using DssHarness.Core.Results;

namespace DssHarness.Core.Processes;

/// <summary>
/// The step in starting a process that puts it where it is contained first: the tool runs itself as
/// <c>contain-exec</c>, which joins the container asked for and then becomes, or starts, the program -
/// so no child is ever outside its container for an instant, which a child put in after its start
/// would be.
/// </summary>
/// <remarks>
/// <para>
/// Not a command anyone uses: nobody types it, help does not list it, and no host is ever asked to
/// run it. The process runner starts it, as the one way it has of placing a contained child, and
/// reads the child through it as through any other: on Linux and macOS the process becomes the
/// program under the same id; on Windows it hands the streams it was given on to the program, waits,
/// and ends with the program's own code.
/// </para>
/// <para>
/// On Linux and macOS full containment is a session and group of one's own (<c>setsid</c>); on
/// Windows it is the job object the engine made and named. The group alone - apart from the
/// terminal, within the session - is the lesser level, asked for by name.
/// </para>
/// <para>
/// Whether the program started is said on a channel of the process runner's own, never on the
/// program's streams or in an exit code a program could end with too: the step connects to it,
/// says what failed where anything did, and says the program is placed just before it is become or
/// once it is started. The channel closes with the step's own end - or, on Linux and macOS, as the
/// program is become, the connection being one no program inherits - so a channel that said the
/// program was placed and closed with nothing more is a program that runs.
/// </para>
/// </remarks>
public static class ContainExec
{
    /// <summary>The step's name, as the process runner starts it by.</summary>
    public const string CommandName = "contain-exec";

    /// <summary>The option naming the Windows job object to join.</summary>
    public const string JobOption = "--job";

    /// <summary>The option asking for a process group of one's own, apart from the terminal.</summary>
    public const string GroupOption = "--group";

    /// <summary>The option naming the channel the step says whether the program started on.</summary>
    public const string StatusOption = "--status";

    /// <summary>What separates the step's own options from the program and its arguments.</summary>
    public const string Separator = "--";

    /// <summary>
    /// The code the step ends with where it did not start the program. Never read as the program's: the
    /// channel says why the program did not start, and a refusal of the step's own command line is said
    /// on the error stream.
    /// </summary>
    public const int Failed = HarnessExit.InternalError;

    /// <summary>The line that says the program is placed and about to be become, or started.</summary>
    private const string PlacedLine = "placed";

    /// <summary>The word a line that says what failed begins with.</summary>
    private const string FailedWord = "failed";

    /// <summary>How long the step waits to reach the channel, which the process runner opened before starting it.</summary>
    private static readonly TimeSpan ReachBound = TimeSpan.FromSeconds(30);

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>What the step was doing when it failed.</summary>
    internal enum Stage
    {
        /// <summary>Making a session and group of its own.</summary>
        Session,

        /// <summary>Joining a group of its own.</summary>
        Group,

        /// <summary>Joining the job object named.</summary>
        Job,

        /// <summary>Starting, or becoming, the program.</summary>
        Start,
    }

    /// <summary>
    /// Runs the step in this process: puts it where it is asked to be contained, then becomes, or
    /// starts and waits for, <paramref name="program"/>.
    /// </summary>
    /// <param name="job">The name of the Windows job object to join, for full containment there.</param>
    /// <param name="group">Whether a process group of one's own is the containment asked for, apart from the terminal alone.</param>
    /// <param name="status">The name of the channel the process runner reads the start from.</param>
    /// <param name="program">The program, by its resolved path.</param>
    /// <param name="arguments">The program's arguments.</param>
    /// <param name="error">Where a refusal of the step's own command line is said.</param>
    /// <returns>The program's own exit code, or <see cref="Failed"/> where the step did not start it.</returns>
    public static int Run(string? job, bool group, string? status, string program, IReadOnlyList<string> arguments, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(error);

        if (job is not null && group)
        {
            error.WriteLine($"contain-exec was asked for both '{JobOption}' and '{GroupOption}'; it takes one.");
            return Failed;
        }

        if (job is { Length: 0 })
        {
            error.WriteLine($"contain-exec was asked for a job with no name; '{JobOption}' takes the name the process runner gave it.");
            return Failed;
        }

        if (string.IsNullOrEmpty(status))
        {
            error.WriteLine($"contain-exec was given no '{StatusOption}'; it takes the channel the process runner reads the start from.");
            return Failed;
        }

        if (Containment.Refusal(job, group) is { } refused)
        {
            error.WriteLine(refused);
            return Failed;
        }

        StartStatus channel;

        try
        {
            channel = StartStatus.Reach(status);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
        {
            error.WriteLine($"contain-exec could not reach the channel '{status}' it says the start on: {ex.Message}");
            return Failed;
        }

        using (channel)
        {
            return Containment.Place(job, group, program, arguments, channel, error);
        }
    }

    /// <summary>What the step said on its channel, as the process runner reads it.</summary>
    /// <param name="text">Everything the channel carried, to its end.</param>
    internal static StartHeard Heard(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var placed = false;

        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (string.Equals(line, PlacedLine, StringComparison.Ordinal))
            {
                placed = true;
                continue;
            }

            var words = line.Split(' ');

            if (words is [FailedWord, var stage, var code]
                && Enum.TryParse<Stage>(stage, ignoreCase: true, out var failed)
                && int.TryParse(code, CultureInfo.InvariantCulture, out var number))
            {
                return new StartHeard(placed, failed, number);
            }
        }

        return new StartHeard(placed, null, 0);
    }

    /// <summary>What the step was doing at <paramref name="stage"/>, as a sentence that says it could not names it.</summary>
    internal static string Doing(Stage stage) => stage switch
    {
        Stage.Session => "start a session of its own",
        Stage.Group => "join a group of its own",
        Stage.Job => "join the job object that contains it",
        _ => "start it",
    };

    /// <summary>What a step said of a start: whether the program was placed, and what failed, where anything did.</summary>
    /// <param name="Placed">Whether the step said the program was placed.</param>
    /// <param name="Failure">What the step was doing when it failed, or <see langword="null"/> where it said no failure.</param>
    /// <param name="Code">The failure's number, as the system gave it to the step: an error number on Linux and macOS, a system error code on Windows.</param>
    internal sealed record StartHeard(bool Placed, Stage? Failure, int Code)
    {
        /// <summary>The failure as the system words its number, where there was one.</summary>
        public string Reason => new Win32Exception(Code).Message;
    }

    /// <summary>The step's end of the channel: one line said for each thing it has to say, and closed as it ends.</summary>
    internal sealed class StartStatus : IDisposable
    {
        private readonly NamedPipeClientStream _pipe;
        private bool _closed;

        private StartStatus(NamedPipeClientStream pipe)
        {
            _pipe = pipe;
        }

        /// <summary>Reaches the channel <paramref name="name"/> names, which only this user's processes can open.</summary>
        public static StartStatus Reach(string name)
        {
            var pipe = new NamedPipeClientStream(".", name, PipeDirection.Out, PipeOptions.CurrentUserOnly);

            try
            {
                pipe.Connect(ReachBound);
                return new StartStatus(pipe);
            }
            catch
            {
                pipe.Dispose();
                throw;
            }
        }

        /// <summary>Says the program is placed: about to be become, or started.</summary>
        public void Placed() => Say(PlacedLine);

        /// <summary>Says what failed, by the stage and the number the system gave.</summary>
        public void Failed(Stage stage, int code)
            => Say(string.Create(CultureInfo.InvariantCulture, $"{FailedWord} {stage.ToString().ToLowerInvariant()} {code}"));

        /// <summary>Closes the channel; closing it again changes nothing.</summary>
        public void Dispose()
        {
            if (_closed)
            {
                return;
            }

            _closed = true;
            _pipe.Dispose();
        }

        /// <summary>
        /// Writes one line and flushes it. A channel the process runner has stopped reading takes
        /// nothing: the step's own end is how it learns the rest.
        /// </summary>
        private void Say(string line)
        {
            if (_closed)
            {
                return;
            }

            try
            {
                var bytes = Utf8NoBom.GetBytes(line + "\n");
                _pipe.Write(bytes);
                _pipe.Flush();
            }
            catch (IOException)
            {
                // Read by nobody now: the process runner gave up on this start.
            }
        }
    }
}
