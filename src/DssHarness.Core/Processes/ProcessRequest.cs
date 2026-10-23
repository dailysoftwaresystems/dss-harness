namespace DssHarness.Core.Processes;

/// <summary>
/// A child process to run. Arguments are passed as a list and never as a single
/// command line: the harness must behave identically regardless of what quoting
/// or escaping a host shell would have applied.
/// </summary>
public sealed record ProcessRequest
{
    /// <summary>
    /// The program: a name, looked up in the PATH directories and nowhere else, or a path, used as given.
    /// </summary>
    public required string FileName { get; init; }

    /// <summary>Arguments, one element per argument. Never pre-quoted.</summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>Working directory, or <see langword="null"/> to inherit the current one.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>
    /// Environment overrides applied on top of the inherited environment.
    /// A <see langword="null"/> value removes the variable.
    /// </summary>
    public IReadOnlyDictionary<string, string?> Environment { get; init; }
        = new Dictionary<string, string?>(StringComparer.Ordinal);

    /// <summary>
    /// Directories added around the child's PATH, after <see cref="Environment"/> has been applied: the raised
    /// ones ahead of it and every other after it (see <see cref="Processes.ProgramDirectories"/>). The program
    /// itself is looked up on that same PATH.
    /// </summary>
    public ProgramDirectories ProgramDirectories { get; init; } = ProgramDirectories.None;

    /// <summary>
    /// What is written to the child's standard input - text, or what a writer writes as it makes it - which is then closed
    /// so the child sees its end; <see langword="null"/> gives it an input that ends at once. A child is never connected
    /// to this process's own input.
    /// </summary>
    public ChildInput? StandardInput { get; init; }

    /// <summary>
    /// How the child's standard output is decoded, or <see langword="null"/> for UTF-8. Latin-1 turns
    /// each byte into one character, for output a caller has to split by a byte count - git's batch
    /// answers name each object's size in bytes, which UTF-8 text cannot be cut by. An encoding named
    /// here decodes every byte from the first, those that begin as a byte order mark does among them,
    /// so it must be one that writes no mark of its own - <see cref="System.Text.Encoding.Latin1"/>, or
    /// UTF-8 made without one: a reader drops an encoding's own mark from the start of what it reads,
    /// whatever it is told, and an encoding that has one is refused.
    /// </summary>
    public System.Text.Encoding? StandardOutputEncoding { get; init; }

    /// <summary>
    /// Keeps standard input open after <see cref="StandardInput"/> is written, until the child exits,
    /// instead of closing it at once. A child that acts on what it read and then watches for the end of
    /// its input learns from that end that this process has gone. A child that reads to the end before
    /// acting would wait forever, so only a child that watches this way is started with it.
    /// </summary>
    public bool HoldStandardInputOpen { get; init; }

    /// <summary>
    /// What is written to the child's standard input again and again once <see cref="StandardInput"/> is written, for as
    /// long as <see cref="HoldStandardInputOpen"/> holds it open; <see langword="null"/> to write nothing more. For a
    /// child that takes this process to have gone when the beats stop, as well as when its input ends.
    /// </summary>
    public InputBeat? StandardInputBeat { get; init; }

    /// <summary>
    /// Called once the child is running, before any of its output can arrive. What a caller bounding
    /// a phase by silence needs: the time spent starting a process is this tool's, not the child
    /// being quiet, and counting it as the child's is how a slow launch reads as a hung command.
    /// </summary>
    public Action? OnStarted { get; init; }

    /// <summary>
    /// Where a <see cref="Contained"/> child's session, or the group of a child <see cref="ApartFromTerminal"/>, is recorded
    /// while it runs - and a group, while anything it left is still in it - so a later run of the tool ends it where this
    /// process was killed and left it running; <see langword="null"/> where it is recorded nowhere - save a bound child's,
    /// which the operation's <see cref="ProcessScope"/> records where it runs under one. Written on Linux and macOS alone,
    /// where a session and a group outlive the process that started them: on Windows the job that holds the child ends it
    /// with this process, and nothing is recorded. A record that cannot be written is a start that failed.
    /// </summary>
    public IContainedSessionRecord? SessionRecord { get; init; }

    /// <summary>
    /// Invoked for each stdout line as it arrives, in addition to capture.
    /// Raised on background threads, so implementations must be thread safe. One that throws is handed no more lines,
    /// and what it threw is raised once the child has gone.
    /// </summary>
    public Action<string>? OnOutputLine { get; init; }

    /// <summary>Invoked for each stderr line as it arrives, in addition to capture, as <see cref="OnOutputLine"/> is.</summary>
    public Action<string>? OnErrorLine { get; init; }

    /// <summary>
    /// How much of stdout <see cref="ProcessResult.StandardOutput"/> keeps: all of it unless the caller, taking its lines
    /// through <see cref="OnOutputLine"/>, keeps what it needs of them itself.
    /// </summary>
    public StreamKept OutputKept { get; init; }

    /// <summary>How much of stderr <see cref="ProcessResult.StandardError"/> keeps, as <see cref="OutputKept"/> says of stdout.</summary>
    public StreamKept ErrorKept { get; init; }

    /// <summary>
    /// Where a line of a stream kept as a <see cref="StreamKept.Tail"/> is cut once it has run to
    /// <see cref="ProcessRunner.LongestLine"/> characters, given those characters, or <see langword="null"/> to cut it after all
    /// of them. What is left is the start of the next piece.
    /// </summary>
    /// <remarks>
    /// For a caller that reads each piece as a line of its own and needs something whole in one: a phase that masks the
    /// secrets it carries in each line it keeps cuts where none of them is parted, since a secret cut in two is masked in
    /// neither piece.
    /// </remarks>
    public Func<string, int>? CutLine { get; init; }

    /// <summary>Wall clock budget, or <see langword="null"/> for no limit.</summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>
    /// Whether the child is contained: what it starts is ended with it, at its own end as at a stop,
    /// and a signal to this process does not reach it. Held through a container it is in before it
    /// runs a line of its own - a session and group of its own on Linux and macOS, the job object
    /// this runner makes and keeps on Windows - by way of the <c>contain-exec</c> step this tool
    /// starts for it. On Windows everything born in the job ends; on Linux and macOS everything still
    /// in the child's session, and at a stop in its tree, is asked to end and then made to, and a
    /// process that made a session of its own away from the child's tree is not reached.
    /// </summary>
    public bool Contained { get; init; }

    /// <summary>
    /// Whether the child is bound to this process's life: on Windows it is placed, before it runs a line of its own, in the
    /// one job object its runner holds for as long as the runner lives - the life of the process, for the runner the
    /// container builds - which ends every member when its last handle closes. So this process ending in any way, a hard
    /// kill among them, ends the child and all it started at once, as a host ends a dispatched leg whose dispatcher has
    /// gone. Its own end and a stop are as any other child's: what it leaves running at its normal end lives on until this
    /// process ends. On Linux and macOS the child starts as any other: an interrupt or a termination signal this process
    /// is given ends it with this process's stop, its terminal's hang-up reaches it as it reaches this process, and a kill
    /// of this process alone does not reach it - save under an operation's <see cref="ProcessScope"/>, a run's machine
    /// step's, which starts it apart from the terminal and records its group while anything is in it, for the next run of
    /// the tool over the run to end once this process has gone. A <see cref="Contained"/> child is bound already, by its
    /// own job.
    /// </summary>
    public bool Bound { get; init; }

    /// <summary>
    /// Whether the child starts in a process group of its own on Linux and macOS, apart from the
    /// terminal: an interruption typed where this process runs is sent to a group, and that group is
    /// not this child's. On Windows the child starts as any other, whose console of its own already
    /// keeps an interruption typed at this one's from it. Either way it is stopped as any other child
    /// is, by its tree - and on Linux and macOS by its group as well, at once, which reaches what its
    /// tree handed on - and what it leaves at its own end is left; the session, and a container that
    /// ends what outlives it, are <see cref="Contained"/>'s to give. What an operation under a
    /// <see cref="ProcessScope"/> starts is apart from the terminal whatever this says.
    /// </summary>
    public bool ApartFromTerminal { get; init; }
}
