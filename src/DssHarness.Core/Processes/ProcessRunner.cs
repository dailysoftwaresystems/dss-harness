using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.ExceptionServices;
using System.Text;
using DssHarness.Core.Platform;
using Microsoft.Win32.SafeHandles;

namespace DssHarness.Core.Processes;

/// <inheritdoc cref="IProcessRunner"/>
public sealed class ProcessRunner(IHostPlatform platform, IFilePermissions filePermissions, string? containThrough = null)
    : IProcessRunner
{
    /// <summary>ENOENT on Linux and macOS, ERROR_FILE_NOT_FOUND on Windows.</summary>
    private const int ErrorFileNotFound = 2;

    /// <summary>How long what a contained child leaves on Linux and macOS is given to end on the signal that asks, before the one that makes it.</summary>
    internal static readonly TimeSpan SessionGrace = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long a contained child's output is read for once its container has been ended: what was in it has gone, and
    /// a process still holding the output is one that left the container, which nothing waits for.
    /// </summary>
    internal static readonly TimeSpan DrainBound = TimeSpan.FromSeconds(5);

    /// <summary>How long a step that has ended is given for a connection it made to its channel to be taken.</summary>
    private static readonly TimeSpan ReachGrace = TimeSpan.FromSeconds(1);

    /// <summary>How long a step that did not start its program is given to end of itself before it is made to.</summary>
    private static readonly TimeSpan StepEnding = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The program that puts a child where it is contained: this tool, run as
    /// <c>contain-exec</c> - or a program a test gives in its place, standing in for the tool the
    /// way the test assembly stands in for the commands it drives.
    /// </summary>
    private readonly string? _containThrough = containThrough;

    /// <summary>ERROR_PATH_NOT_FOUND on Windows. On Linux and macOS the same number means something else.</summary>
    private const int WindowsErrorPathNotFound = 3;

    private const int ReadBufferSize = 4096;

    /// <summary>
    /// How many characters of a stream kept as a <see cref="StreamKept.Tail"/> are kept: its end, which is where a program
    /// says what went wrong, and plenty for any message that quotes it.
    /// </summary>
    public const int TailLength = 64 * 1024;

    /// <summary>
    /// The most characters a line of a stream kept as a <see cref="StreamKept.Tail"/> is handed on in; a longer one arrives in
    /// pieces of at most this many, each a line of its own. As long as the longest command line Windows starts, longer than
    /// any line a compiler, a test runner or a linker prints, and short enough that a child writing gigabytes without a line
    /// feed is never held whole - and that each piece is an object the runtime collects as soon as it is dropped, rather than
    /// one of the large ones it collects only with everything else.
    /// </summary>
    public const int LongestLine = 32 * 1024;

    /// <summary>
    /// How child output is decoded, and child input encoded, on every platform. Left unset,
    /// Windows decodes redirected output with the console code page while Linux and macOS use
    /// UTF-8, so a path such as <c>C:\Users\João</c> printed by git, which always writes UTF-8,
    /// would reach the harness garbled on Windows alone.
    /// </summary>
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>How many bytes of a child's output are read at a time: what .NET's own reader of a child's output reads.</summary>
    private const int OutputBuffer = 4096;

    private readonly IHostPlatform _platform = platform;
    private readonly IFilePermissions _filePermissions = filePermissions;

    /// <summary>
    /// What a contained child's session is read from on Linux and macOS: the machine's own, read through this runner where
    /// the system is asked through a program - or a table a test gives in its place.
    /// </summary>
    private IProcessTable? _processTable;

    /// <summary>
    /// Makes the job object a contained child on Windows is held in, by the name the runner gave it: the system's own - or
    /// a maker a test gives in its place, which learns the name only here and can arrange, before it asks the system,
    /// what makes the system refuse the job.
    /// </summary>
    private readonly Func<string, SafeWaitHandle> _makeJob = Containment.CreateJob;

    /// <summary>What makes <see cref="_lifetime"/> once, whichever bound children start together.</summary>
    private readonly Lock _lifetimeGate = new();

    /// <summary>
    /// The job this runner's bound children are held in on Windows, by its name and its one handle, once the first of them
    /// has started: kept for as long as the runner, and closed by nothing else.
    /// </summary>
    private (string Name, SafeWaitHandle Job)? _lifetime;

    /// <summary>The variable that names where the compiler's program-database server listens.</summary>
    internal const string ProgramDatabaseEndpoint = "_MSPDBSRV_ENDPOINT_";

    /// <summary>The variable that keeps the build engine from leaving nodes for a later build to reuse.</summary>
    internal const string NodeReuseOff = "MSBUILDDISABLENODEREUSE";

    /// <summary>
    /// A runner reading <paramref name="processTable"/> in place of the machine's own table, or making its jobs with
    /// <paramref name="makeJob"/>: a test's. Not the public constructor's, because the container builds this runner from
    /// that one, and the machine's table is itself built over this runner - a table asked of the container there would be
    /// a runner waiting on itself.
    /// </summary>
    /// <param name="platform">The system this runs on.</param>
    /// <param name="filePermissions">How the system says a file may be run.</param>
    /// <param name="containThrough">The tool that starts a contained child, as the public constructor takes it.</param>
    /// <param name="processTable">The table a contained child's session is read from; <see langword="null"/> for the machine's own.</param>
    /// <param name="makeJob">What makes a contained child's job on Windows, by its name; <see langword="null"/> for the system's own.</param>
    internal ProcessRunner(
        IHostPlatform platform,
        IFilePermissions filePermissions,
        string? containThrough,
        IProcessTable? processTable,
        Func<string, SafeWaitHandle>? makeJob = null)
        : this(platform, filePermissions, containThrough)
    {
        _processTable = processTable;
        _makeJob = makeJob ?? Containment.CreateJob;
    }

    /// <summary>
    /// The variable a program is looked up on. Spelled once: on Windows the environment a child is
    /// given compares names without case, so this also finds the 'Path' Windows itself writes.
    /// </summary>
    internal const string PathVariable = "PATH";

    public async Task<ProcessResult> RunAsync(
        ProcessRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // A reader drops an encoding's own byte order mark from the start of what it reads, told to look
        // for none or not, so output read under such an encoding would lose bytes the caller counts.
        if (request.StandardOutputEncoding is { Preamble.Length: > 0 } marked)
        {
            throw new ArgumentException(
                $"A child's output is read under the encoding named byte for byte, and {marked.WebName} as named has a byte order mark of "
                + "its own, which a reader drops from the start: name it without one.",
                nameof(request));
        }

        // Checked before starting. On Linux and macOS a missing working directory fails with
        // the same error number as a missing executable, so it would otherwise be reported
        // as "git is not installed" when git is installed and it is the directory that is gone.
        // Raised as the program not starting, which it did not, so every reader names the cause:
        // a leg already running has failed, and a command ends as one whose program never ran.
        if (!string.IsNullOrEmpty(request.WorkingDirectory) && !Directory.Exists(request.WorkingDirectory))
        {
            throw new ProgramStartException(
                request.FileName,
                $"'{request.FileName}' could not be started: the working directory '{request.WorkingDirectory}' does not exist.");
        }

        var startInfo = new ProcessStartInfo
        {
            WorkingDirectory = request.WorkingDirectory ?? string.Empty,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = request.StandardOutputEncoding ?? Utf8NoBom,
            StandardErrorEncoding = Utf8NoBom,

            // Every child gets an input of its own, never this process's. A tool that reads its input would
            // otherwise consume what was meant for the harness. And on Windows a child that inherits an input
            // this process is reading at that moment can hang as it starts: the pending read holds the pipe,
            // and one of the first things a runtime such as git's does is ask that pipe what it is.
            RedirectStandardInput = true,
            StandardInputEncoding = Utf8NoBom,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var (key, value) in request.Environment)
        {
            // A name is one variable whatever case the configuration spelled it in, as on Windows,
            // where that rule comes from. On Linux and macOS the system tells spellings apart, and
            // programs differ in which they read - curl reads http_proxy, others HTTP_PROXY, all of
            // them PATH - so the value reaches every spelling this machine has, and the one written;
            // and a name removed is removed in every spelling. Written under only one, 'Path' set
            // beside 'PATH' changed nothing, and 'http_proxy' folded into 'HTTP_PROXY' hid the proxy
            // from curl.
            foreach (var name in Spellings(startInfo.Environment, key))
            {
                if (value is null)
                {
                    startInfo.Environment.Remove(name);
                }
                else
                {
                    startInfo.Environment[name] = value;
                }
            }
        }

        if (!request.ProgramDirectories.IsEmpty)
        {
            var current = startInfo.Environment.TryGetValue(PathVariable, out var inherited) ? inherited : null;
            var around = request.ProgramDirectories.Around(current, Path.PathSeparator);

            foreach (var name in Spellings(startInfo.Environment, PathVariable))
            {
                startInfo.Environment[name] = around;
            }
        }

        // Looked up on the PATH the child is given, not on this process's own. The two used to differ
        // whenever a request changed PATH, and then the program started was one the child's own
        // lookups could not see: cmake resolved from one PATH and the ninja it starts from another.
        var resolved = ResolveProgram(
            request.FileName,
            startInfo.Environment.TryGetValue(PathVariable, out var effective) ? effective : null);

        var windows = _platform.Current == PlatformId.Windows;

        // What an operation under a process scope starts - a run's machine step's legs - is apart from the terminal, and a
        // bound child of it recorded while it runs, as the scope says. A contained child has a session of its own already.
        var scoped = request.Contained ? null : ProcessScope.Current;
        var apart = request.ApartFromTerminal || scoped is not null;

        // Through this tool's own step where a container is asked for: every contained child, on Windows
        // a bound one, and on Linux and macOS a child apart from the terminal. On Windows a child apart
        // from the terminal starts as any other: started with no window, it has a console of its own,
        // which an interruption typed at this process's console never reaches. On Linux and macOS a
        // bound child under no scope starts as any other: nothing there ends a child with the process
        // that started it.
        var bound = request.Bound && !request.Contained && windows;
        var throughStep = request.Contained || (apart && !windows) || bound;

        // A child that leads a group of the step's making, on Linux and macOS: stopped by its group as well as its tree,
        // and its record kept while anything is left in the group.
        var grouped = apart && !windows && !request.Contained;

        if (throughStep && !File.Exists(resolved) && !Directory.Exists(resolved))
        {
            // Looked for before the step is started for it, as a start would look for it: what the
            // step starts first is this tool, which is there. Anything that is there, and still
            // cannot be started, the step says why of.
            throw StartFailure(request.FileName, resolved, new Win32Exception(ErrorFileNotFound));
        }

        // The job a contained run on Windows is held in, kept for as long as the run: terminated as
        // the child ends, it ends whatever the child left behind, and closed by this process's own
        // end it ends the child with it.
        var jobName = request.Contained && windows ? JobName() : null;
        using var job = jobName is null ? null : MadeJob(jobName, request.FileName, resolved);

        // The job a bound child joins: the runner's own, made at its first bound child and held for as long as the
        // runner lives, never terminated and never closed by a child's end.
        var joined = jobName ?? (bound ? LifetimeJob(request.FileName, resolved) : null);

        if (joined is not null)
        {
            OwnServers(startInfo.Environment, joined);
        }

        // The channel the step says whether the program started on, listened on before the step can
        // reach it.
        var statusName = throughStep ? StatusName(windows) : null;
        using var status = statusName is null ? null : OpenedStatus(statusName, request.FileName, resolved);

        if (throughStep)
        {
            // Placed before it runs a line of its own, through this tool's own step: the container
            // is joined and the program started from inside it, so no child is ever outside for an
            // instant, which a child put in after its start would be.
            var (self, leading) = Self();
            startInfo.FileName = self;

            foreach (var argument in leading)
            {
                startInfo.ArgumentList.Add(argument);
            }

            startInfo.ArgumentList.Add(ContainExec.CommandName);

            if (joined is not null)
            {
                startInfo.ArgumentList.Add(ContainExec.JobOption);
                startInfo.ArgumentList.Add(joined);
            }

            if (!request.Contained && !bound)
            {
                startInfo.ArgumentList.Add(ContainExec.GroupOption);
            }

            startInfo.ArgumentList.Add(ContainExec.StatusOption);
            startInfo.ArgumentList.Add(statusName!);
            startInfo.ArgumentList.Add(ContainExec.Separator);
            startInfo.ArgumentList.Add(resolved);

            foreach (var argument in request.Arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }
        }
        else
        {
            startInfo.FileName = resolved;

            foreach (var argument in request.Arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }
        }

        // A caller that has given up already starts nothing. Started only to be stopped at once, a short program can
        // finish first - a set-up's down script did, on Linux - and be reported as having run after its time had passed.
        cancellationToken.ThrowIfCancellationRequested();

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var stopwatch = Stopwatch.StartNew();

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var reaching = CancellationTokenSource.CreateLinkedTokenSource(timeoutSource.Token);

        // Waited for before the step starts, so a step however quick to say how its start went is
        // heard saying it.
        var reached = status?.WaitForConnectionAsync(reaching.Token);

        try
        {
            await StartAsync(process, cancellationToken).ConfigureAwait(false);

            // Before the readers below, so nothing the child says can arrive ahead of the fact that
            // it started. A child started through the step has started once the step says so.
            if (status is null)
            {
                request.OnStarted?.Invoke();
            }
        }
        catch (Exception ex) when (ex is Win32Exception or OperationCanceledException)
        {
            await GivenUpAsync(reaching, reached).ConfigureAwait(false);

            if (ex is Win32Exception failed)
            {
                throw StartFailure(request.FileName, startInfo.FileName, failed);
            }

            throw;
        }

        if (request.Timeout is { } budget)
        {
            timeoutSource.CancelAfter(budget);
        }

        var stopped = false;
        var placed = false;
        string? uncontained = null;

        if (status is not null)
        {
            try
            {
                var heard = await HeardAsync(status, reached!, reaching, process, timeoutSource.Token).ConfigureAwait(false);

                if (heard is not { Placed: true, Failure: null })
                {
                    throw await NotStartedAsync(request.FileName, resolved, heard, process).ConfigureAwait(false);
                }

                placed = true;
            }
            catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested)
            {
                stopped = true;
                uncontained = await StopAsync(request, grouped, job, process).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Nothing the step says can be read, so nothing it starts is run blind: it goes.
                KillTree(process);

                throw new ProgramStartException(
                    request.FileName,
                    $"'{resolved}' could not be started: what contain-exec said of its start could not be read: {ex.Message}",
                    ex);
            }
        }

        var recorded = placed && !windows
            ? await RecordedAsync(request, request.SessionRecord ?? (request.Bound ? scoped?.Record() : null), grouped, resolved, job, process).ConfigureAwait(false)
            : null;

        if (placed)
        {
            request.OnStarted?.Invoke();
        }

        // Both streams are read from the moment the child runs: a child that fills a pipe nobody is
        // reading blocks forever. Until the step has said the program started, nothing but the step
        // writes them, and it writes nothing there.
        var standardOutput = new StreamCapture(OutputOf(process, request), request.OnOutputLine, request.OutputKept, request.CutLine);
        var standardError = new StreamCapture(process.StandardError, request.OnErrorLine, request.ErrorKept, request.CutLine);

        // Written while both output streams are being read, so a child that answers as it reads
        // can never block this on a full output pipe, nor this block it on a full input pipe.
        using var exited = new CancellationTokenSource();
        var standardInput = WriteInputAsync(
            process.StandardInput,
            request.StandardInput,
            close: !request.HoldStandardInputOpen,
            request.HoldStandardInputOpen ? request.StandardInputBeat : null,
            exited.Token);

        if (!stopped)
        {
            try
            {
                await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Whether for the budget or because the caller gave up, the whole of it goes: a
                // descendant left running keeps the pipes, and whatever it was building, busy - and a
                // contained child is ended through its container, which reaches what detached itself
                // from its parent as surely as what did not.
                stopped = true;
                uncontained = await StopAsync(request, grouped, job, process).ConfigureAwait(false);
            }
        }

        if (stopped)
        {
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        else if (request.Contained)
        {
            // What a contained child starts is ended with it, at its own end as at a stop: a
            // leftover holding the output - a server started in the background, a build's node -
            // would otherwise keep the reading below from ever ending.
            uncontained = await EndContainerAsync(job, process).ConfigureAwait(false);
        }

        // The child has ended, and a contained child's session with it, at its own end or at a stop: its record has nothing
        // left to name - save a group that still holds what the child left running at its own end, which lives on as a
        // bound child's leftovers do until the process that started it ends, and is ended by the sweep that follows. A
        // stop ended the group.
        if (recorded is not null && !(grouped && !stopped && Containment.GroupHolds(process.Id)))
        {
            Cleared(recorded);
        }

        // No beat is written to a child that has gone.
        await exited.CancelAsync().ConfigureAwait(false);

        // The process has exited, but what it wrote just before exiting can still be in the
        // pipes. Reading both streams to their end is what guarantees none of it is lost - for a
        // contained child, within a bound, since all its container held has gone with it and what
        // still holds a pipe is a process that left the container and is no part of this run.
        var drained = Task.WhenAll(standardOutput.Reading, standardError.Reading);

        if (!request.Contained || await Task.WhenAny(drained, Task.Delay(DrainBound, CancellationToken.None)).ConfigureAwait(false) == drained)
        {
            await drained.ConfigureAwait(false);
        }

        var capturedOutput = standardOutput.Taken();
        var capturedError = standardError.Taken();
        var beatLost = await standardInput.ConfigureAwait(false);

        if (request.HoldStandardInputOpen)
        {
            // Held open while the child ran, so that its end could tell the child this process had
            // gone; closed now that the child has exited.
            CloseQuietly(process.StandardInput);
        }

        // What a line handler raised, now that the child has gone and every line it wrote has been read.
        RaiseHandlerFailures(capturedOutput.Failed, capturedError.Failed);

        stopwatch.Stop();

        if (stopped && cancellationToken.IsCancellationRequested)
        {
            // What the stop could not reach is said where the stop is: a caller that gave up is
            // handed no result to read it from.
            throw uncontained is null
                ? new OperationCanceledException(cancellationToken)
                : new OperationCanceledException($"The run was stopped, and {uncontained}.", cancellationToken);
        }

        return new ProcessResult(
            ExitCode: stopped ? -1 : process.ExitCode,
            StandardOutput: capturedOutput.Text,
            StandardError: capturedError.Text,
            Duration: stopwatch.Elapsed,
            TimedOut: stopped)
        {
            BeatLost = beatLost,
            Uncontained = uncontained,
        };
    }

    /// <summary>
    /// Stops a child as its request says: a contained one through its container - and any other,
    /// and a contained one whose container was asked for in the moment before it was joined, by its
    /// tree; a child that leads a group of its own, apart from the terminal on Linux and macOS, by its
    /// group as well, at once, which reaches what its tree handed on.
    /// </summary>
    /// <param name="request">The child's request, saying whether it was contained.</param>
    /// <param name="grouped">Whether the child leads a group of its own on Linux and macOS.</param>
    /// <param name="job">The job holding a contained child on Windows, or <see langword="null"/>.</param>
    /// <param name="process">The child.</param>
    /// <returns>What the container could not reach, or <see langword="null"/> where it reached all it holds.</returns>
    private async Task<string?> StopAsync(ProcessRequest request, bool grouped, SafeWaitHandle? job, Process process)
    {
        try
        {
            return request.Contained ? await EndContainerAsync(job, process).ConfigureAwait(false) : null;
        }
        finally
        {
            // The tree as well, whatever ending the container met: a stop that lands in the moment
            // before the child joined its container reaches nothing through the container, and the
            // child itself must still go, or the wait that follows would never end.
            KillTree(process);

            // Then the group, for what the tree handed on: walked first, while the child stands, the
            // tree still reaches what moved to a group of its own - a test under timeout - which the
            // child's end would have handed on, out of the tree's reach. No later process takes the
            // group's id while anything is left in it.
            if (grouped)
            {
                Containment.KillGroup(process.Id);
            }
        }
    }

    /// <summary>
    /// Ends everything a contained child's container holds: on Windows the job, all at once; on
    /// Linux and macOS the session the child leads, asked to end and, once the grace is spent,
    /// made to.
    /// </summary>
    /// <param name="job">The job holding the child on Windows, or <see langword="null"/> elsewhere.</param>
    /// <param name="process">The child, which leads its session on Linux and macOS under its own id.</param>
    /// <returns>What could not be reached, or <see langword="null"/> where all the container holds was.</returns>
    private Task<string?> EndContainerAsync(SafeWaitHandle? job, Process process)
    {
        if (job is not null)
        {
            Containment.Terminate(job);
            return Task.FromResult<string?>(null);
        }

        _processTable ??= ProcessTableFactory.Create(_platform, this);

        return UnreachedOf(Containment.EndSessionAsync(process.Id, _processTable.ReadAsync, SessionGrace));

        static async Task<string?> UnreachedOf(Task<HeldEnding> ending) => (await ending.ConfigureAwait(false)).Unreached;
    }

    /// <summary>
    /// Records a placed child's session or group, on Linux and macOS - where nothing ends either when the process that
    /// started it is killed - where its request or its operation's scope names a record, before the child is said to have
    /// started, so the next run of the tool over the same records ends what a killed one left: a contained child's session,
    /// and the group of a child apart from the terminal. A job ends with its last handle, so on Windows nothing is recorded,
    /// and this is never asked there.
    /// </summary>
    /// <param name="request">The child's request.</param>
    /// <param name="record">Its record: its request's, or its operation's scope's; <see langword="null"/> where it has none.</param>
    /// <param name="grouped">Whether the child leads a group of its own, apart from the terminal.</param>
    /// <param name="resolved">The program the child runs.</param>
    /// <param name="job">The job holding the child, or <see langword="null"/>.</param>
    /// <param name="process">The child, which leads its session or its group under its own id.</param>
    /// <returns>The record written, to be taken back once the child has ended; <see langword="null"/> where none was.</returns>
    /// <exception cref="ProgramStartException">The record could not be written: the child was ended, and never said to have started.</exception>
    private async Task<IContainedSessionRecord?> RecordedAsync(ProcessRequest request, IContainedSessionRecord? record, bool grouped, string resolved, SafeWaitHandle? job, Process process)
    {
        if (!(request.Contained || grouped) || record is null)
        {
            return null;
        }

        try
        {
            record.Placed(process.Id, resolved, group: grouped);
            return record;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A child no record names is one a killed tool would leave running unseen: it is not run, stopped as a stop
            // stops it.
            await StopAsync(request, grouped, job, process).ConfigureAwait(false);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);

            throw new ProgramStartException(
                request.FileName,
                $"'{resolved}' could not be started: its {LeftSessions.Noun(grouped)} could not be recorded: {ex.Message}",
                ex);
        }
    }

    /// <summary>
    /// Takes back the record of a child that has ended. One that cannot be taken back names a child that has ended, which
    /// the next sweep of the records removes without ending anything once this process has gone.
    /// </summary>
    /// <param name="record">The record written when the child was placed.</param>
    private static void Cleared(IContainedSessionRecord record)
    {
        try
        {
            record.Ended();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left for the sweep, as above.
        }
    }

    /// <summary>
    /// What the step said on its channel once it ended or closed it, or <see langword="null"/> where it
    /// ended without reaching it.
    /// </summary>
    /// <remarks>
    /// The channel closes as the step ends, or on Linux and macOS as the program is become; a step
    /// that ended before it reached the channel never closes it, so its end is watched for as well.
    /// </remarks>
    /// <param name="status">The channel.</param>
    /// <param name="reached">The wait for the step to reach the channel, begun before it started.</param>
    /// <param name="reaching">Gives that wait up.</param>
    /// <param name="process">The step.</param>
    /// <param name="cancellationToken">Stops the waiting: the run's bound, or its caller giving up.</param>
    private static async Task<ContainExec.StartHeard?> HeardAsync(
        NamedPipeServerStream status,
        Task reached,
        CancellationTokenSource reaching,
        Process process,
        CancellationToken cancellationToken)
    {
        var ended = process.WaitForExitAsync(cancellationToken);

        if (await Task.WhenAny(reached, ended).ConfigureAwait(false) == ended && !reached.IsCompleted)
        {
            // Raises the stop, where that is what ended the wait.
            await ended.ConfigureAwait(false);

            // A connection made just before the step ended is taken still; a step that made none
            // said nothing.
            if (await Task.WhenAny(reached, Task.Delay(ReachGrace, CancellationToken.None)).ConfigureAwait(false) != reached)
            {
                await GivenUpAsync(reaching, reached).ConfigureAwait(false);
                return null;
            }
        }

        await reached.ConfigureAwait(false);

        using var reader = new StreamReader(status, Utf8NoBom, detectEncodingFromByteOrderMarks: false, OutputBuffer, leaveOpen: true);

        return ContainExec.Heard(await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Gives up the wait for the step to reach its channel, where there is one, and lets it end.</summary>
    private static async Task GivenUpAsync(CancellationTokenSource reaching, Task? reached)
    {
        if (reached is null)
        {
            return;
        }

        await reaching.CancelAsync().ConfigureAwait(false);

        try
        {
            await reached.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // The wait for a connection that never came, given up.
        }
    }

    /// <summary>
    /// Why a program started through the step did not start, once the step has ended: what the step
    /// said failed, worded as a start that fails is, or the step's own end where it said nothing.
    /// </summary>
    /// <param name="requested">The program as the request names it.</param>
    /// <param name="resolved">The file the step was asked to start.</param>
    /// <param name="heard">What the step said, or <see langword="null"/> where it reached no channel.</param>
    /// <param name="process">The step.</param>
    private async Task<ProgramStartException> NotStartedAsync(string requested, string resolved, ContainExec.StartHeard? heard, Process process)
    {
        try
        {
            await process.WaitForExitAsync().WaitAsync(StepEnding).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            KillTree(process);
            await process.WaitForExitAsync().ConfigureAwait(false);
        }

        switch (heard)
        {
            case { Failure: ContainExec.Stage.Start }:
                return StartFailure(requested, resolved, new Win32Exception(heard.Code));

            case { Failure: { } stage }:
                return new ProgramStartException(
                    requested,
                    $"'{resolved}' could not be started: contain-exec could not {ContainExec.Doing(stage)}: {heard.Reason}");
        }

        // Its refusal of its own command line, or the failure of this tool's own start, is on its
        // error stream, which nothing but the step has written.
        var said = string.Empty;

        try
        {
            said = (await process.StandardError.ReadToEndAsync().WaitAsync(StepEnding).ConfigureAwait(false))
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault() ?? string.Empty;
        }
        catch (TimeoutException)
        {
            // Held open by something it started: what it said is not had.
        }

        return new ProgramStartException(
            requested,
            $"'{resolved}' could not be started: contain-exec ended with {process.ExitCode} before it started it{(said.Length == 0 ? "." : $": {said}")}");
    }

    /// <summary>The job a contained child on Windows is held in, or the reason it could not be made as the start's failure.</summary>
    private SafeWaitHandle MadeJob(string name, string requested, string resolved)
    {
        try
        {
            return _makeJob(name);
        }
        catch (Win32Exception ex)
        {
            throw new ProgramStartException(requested, $"'{resolved}' could not be started: the job object that would contain it could not be made: {ex.Message}", ex);
        }
    }

    /// <summary>The channel a child started through the step is said started on, or the reason it could not be opened as the start's failure.</summary>
    private static NamedPipeServerStream OpenedStatus(string name, string requested, string resolved)
    {
        try
        {
            return new NamedPipeServerStream(name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            throw new ProgramStartException(requested, $"'{resolved}' could not be started: the channel contain-exec says its start on could not be opened: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// A channel's name, unique to this child of this process: a name on Windows, and on Linux and
    /// macOS the whole path of the socket it is, in this process's temporary directory - the step
    /// reads its own from the environment the child is given, which a request may change.
    /// </summary>
    internal static string StatusName(bool windows)
    {
        var name = $"dssharness-{Guid.NewGuid():N}"[..27];

        return windows ? name : Path.Combine(Path.GetTempPath(), name);
    }

    /// <summary>How long a program that will not start for being written is started again before that is its failure.</summary>
    /// <remarks>
    /// Linux starts no program a descriptor still holds open for writing, and a process forked while the program was
    /// being written - by any thread of the process writing it, for a child of its own - holds that descriptor until it
    /// becomes the program it was forked for: a moment, in which a program written and closed a line earlier is "busy".
    /// Seen in this tool's own suite, which writes a script and starts it beside tests starting children of their own:
    /// one start failed so in a run of some five thousand tests.
    /// </remarks>
    private static readonly TimeSpan BusyGrace = TimeSpan.FromSeconds(2);

    /// <summary>How long between two tries of a program that is busy.</summary>
    private static readonly TimeSpan BusyPause = TimeSpan.FromMilliseconds(20);

    /// <summary>"Text file busy", as Linux and macOS number it.</summary>
    internal const int TextFileBusy = 26;

    /// <summary>Starts <paramref name="process"/>, again for as long as <see cref="BusyGrace"/> where it is being written.</summary>
    private static Task StartAsync(Process process, CancellationToken cancellationToken)
        => StartAsync(() => process.Start(), !OperatingSystem.IsWindows(), BusyGrace, cancellationToken);

    /// <summary>
    /// Runs <paramref name="start"/> as a start is run here, again for as long as <see cref="BusyGrace"/> where the program
    /// is being written: what <c>contain-exec</c> does to become the program it was started for, so a program started
    /// through it is given the same grace as one started directly.
    /// </summary>
    /// <param name="start">Starts the program, raising as <see cref="Process.Start()"/> does where it cannot.</param>
    internal static void StartWhileBusy(Action start)
        => StartAsync(start, !OperatingSystem.IsWindows(), BusyGrace, CancellationToken.None).GetAwaiter().GetResult();

    /// <summary>
    /// Runs <paramref name="start"/>, and again every <see cref="BusyPause"/> for as long as <paramref name="grace"/>
    /// where it fails as a program being written does and <paramref name="busyPasses"/> says that passes on this system.
    /// Any other failure, and that one once the grace is spent, is the start's own.
    /// </summary>
    /// <param name="start">Starts the program, raising as <see cref="Process.Start()"/> does where it cannot.</param>
    /// <param name="busyPasses">Whether a program held open for writing is refused here, and so worth trying again: not on Windows, where the number means something else.</param>
    /// <param name="grace">How long it is tried again.</param>
    /// <param name="cancellationToken">Stops the waiting between two tries.</param>
    internal static async Task StartAsync(Action start, bool busyPasses, TimeSpan grace, CancellationToken cancellationToken)
    {
        var tried = Stopwatch.StartNew();

        while (true)
        {
            try
            {
                start();
                return;
            }
            catch (Win32Exception ex) when (busyPasses && ex.NativeErrorCode == TextFileBusy && tried.Elapsed < grace)
            {
                await Task.Delay(BusyPause, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// How long a child whose input can no longer be written is given to be found exiting, before the beat is said to
    /// have been lost to a child still running: a child that exits takes its input with it a moment before it is seen
    /// to have gone.
    /// </summary>
    private static readonly TimeSpan GoingGrace = TimeSpan.FromSeconds(2);

    public string? FindExecutable(string command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);

        if (!IsPath(command))
        {
            return OnPath(command);
        }

        var path = ProgramAtPath(command, _platform.Current == PlatformId.Windows);
        return _filePermissions.IsExecutable(path) ? path : null;
    }

    /// <summary>
    /// The program that puts a child where it is contained: this tool, as it is run - or the
    /// program a test gave in this one's place.
    /// </summary>
    private (string Program, IReadOnlyList<string> Leading) Self()
        => _containThrough is { } given
            ? (given, [])
            : DetachedProcessLauncher.Self();

    /// <summary>A job object's name, unique to this contained child of this process.</summary>
    private static string JobName()
        => $"dssharness-contain-{Guid.NewGuid():N}";

    /// <summary>
    /// The name of the job this runner's bound children are held in on Windows, made at the first of them: held by this
    /// runner, never terminated and never closed but by the runner's own end - the process's, for the one the container
    /// builds - so its members end then, whatever ends it. A job that could not be made is the start's failure, and the
    /// next bound child asks for it again.
    /// </summary>
    private string LifetimeJob(string requested, string resolved)
    {
        lock (_lifetimeGate)
        {
            if (_lifetime is { } held)
            {
                return held.Name;
            }

            var name = JobName();
            _lifetime = (name, MadeJob(name, requested, resolved));

            return name;
        }
    }

    /// <summary>
    /// Gives a child held in a job on Windows build servers of its own, where its environment names none: the compiler's
    /// program-database server listens at an endpoint named for the job, and the build engine keeps no node for a later
    /// build. A server a member starts is the job's, and ends with it; shared with another process's builds - the
    /// compiler's server is one for all of a user's builds unless told otherwise, and the build engine's nodes wait to be
    /// reused by whichever build comes next - it would be taken from them mid-build as this job ended.
    /// </summary>
    /// <param name="environment">The child's environment, as its request left it.</param>
    /// <param name="job">The job the child is held in.</param>
    internal static void OwnServers(IDictionary<string, string?> environment, string job)
    {
        foreach (var (name, value) in new[] { (ProgramDatabaseEndpoint, job), (NodeReuseOff, "1") })
        {
            if (!environment.Keys.Any(existing => string.Equals(existing, name, StringComparison.OrdinalIgnoreCase)))
            {
                environment[name] = value;
            }
        }
    }

    /// <summary>
    /// Whether <paramref name="program"/> names a file by its path rather than a program to look up by
    /// name.
    /// </summary>
    /// <remarks>
    /// The one rule, read by everything that has to tell the two apart: the run that starts a program,
    /// the survey that looks for one first, the validator, and the policy that decides whether an
    /// action may name it. Read from the text alone, so a configuration means the same thing on every
    /// machine that reads it: either separator counts on every platform, and so does a drive, such as
    /// <c>C:tool</c>, which is no name a program is installed under.
    /// </remarks>
    /// <param name="program">The program as a configuration or a request names it.</param>
    internal static bool IsPath(string program)
        => program.Contains('/', StringComparison.Ordinal)
            || program.Contains('\\', StringComparison.Ordinal)
            || PlatformPaths.NamesADrive(program);

    /// <summary>
    /// Whether <paramref name="environment"/> sets the PATH a program is looked for on.
    /// </summary>
    /// <remarks>
    /// Compared without case, as a configuration's environment names are: a PATH set in any spelling
    /// is one the harness does not choose.
    /// </remarks>
    /// <param name="environment">An environment a configuration declares.</param>
    internal static bool SetsPath(IEnumerable<string> environment)
        => environment.Any(name => string.Equals(name, PathVariable, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The file a program named by its path is: made absolute, with the one extension Windows adds.
    /// </summary>
    /// <remarks>
    /// One spelling for the run that starts it and for every question asked about it beforehand, so a
    /// path a survey found is the file the run starts rather than a sibling without its extension.
    /// </remarks>
    /// <param name="program">A program named by its path.</param>
    /// <param name="windows">Whether the file is started on Windows.</param>
    /// <exception cref="ArgumentException">The path is not one this machine can express.</exception>
    internal static string ProgramAtPath(string program, bool windows)
        => Path.GetFullPath(windows ? WithWindowsExtension(program) : program);

    /// <summary>
    /// <paramref name="program"/> with a relative path read against <paramref name="directory"/>; a
    /// name, or a path that is already full, as given.
    /// </summary>
    /// <remarks>
    /// Left to the start, a relative path is read against whatever directory this process happens to
    /// be in, never the one the child is started in: the main checkout for a leg that works in a
    /// worktree, a subdirectory when the command was typed in one. Either way the file started is some
    /// other directory's, under the same name.
    /// </remarks>
    /// <param name="program">The program as a configuration names it.</param>
    /// <param name="directory">The directory the program starts in, which a relative path is written from.</param>
    internal static string Anchored(string program, string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(program);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        return IsPath(program) && !Path.IsPathFullyQualified(program)
            ? Path.GetFullPath(program, directory)
            : program;
    }

    /// <summary>
    /// Every spelling a value named <paramref name="name"/> is written under: each one
    /// <paramref name="environment"/> already has, ignoring case, the name as written, and PATH as
    /// every program spells it where the name is that one.
    /// </summary>
    private static List<string> Spellings(IDictionary<string, string?> environment, string name)
    {
        var spellings = environment.Keys
            .Where(existing => string.Equals(existing, name, StringComparison.OrdinalIgnoreCase))
            .Append(name);

        if (string.Equals(name, PathVariable, StringComparison.OrdinalIgnoreCase))
        {
            spellings = spellings.Append(PathVariable);
        }

        return [.. spellings.Distinct(StringComparer.Ordinal)];
    }

    /// <summary>
    /// The first file that would start as <paramref name="name"/> in the directories
    /// <paramref name="pathVariable"/> lists, or <see langword="null"/> when there is none. Nowhere else
    /// is looked in.
    /// </summary>
    internal static string? ProgramOnPath(string name, string? pathVariable, bool windows, Func<string, bool> isExecutable)
        => ProgramsOnPath(name, pathVariable, windows, isExecutable).FirstOrDefault();

    /// <summary>
    /// The first file that would start as one of <paramref name="names"/> in the directories
    /// <paramref name="pathVariable"/> lists - the earliest directory holding any of them, and of those it holds the first
    /// asked - or <see langword="null"/> when there is none. Nowhere else is looked in.
    /// </summary>
    internal static string? ProgramOnPath(IReadOnlyList<string> names, string? pathVariable, bool windows, Func<string, bool> isExecutable)
        => ProgramsOnPath(names, pathVariable, windows, isExecutable).FirstOrDefault();

    /// <summary>
    /// Every file that would start as <paramref name="name"/> in the directories <paramref name="pathVariable"/>
    /// lists, in the order it lists them: the first is the one <see cref="ProgramOnPath(string, string?, bool, Func{string, bool})"/> finds.
    /// </summary>
    internal static IEnumerable<string> ProgramsOnPath(string name, string? pathVariable, bool windows, Func<string, bool> isExecutable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return ProgramsOnPath([name], pathVariable, windows, isExecutable);
    }

    /// <summary>
    /// Every file that would start as one of <paramref name="names"/> in the directories <paramref name="pathVariable"/>
    /// lists: directory by directory in the order it lists them, and within one directory in the order of
    /// <paramref name="names"/> - so the first is the file of the earliest directory that holds any of them, and of the
    /// names it holds, the first asked.
    /// </summary>
    /// <remarks>
    /// The one walk of a PATH: how it is split, how a quoted entry is read and how a name becomes a file in each entry
    /// (<see cref="ProgramInDirectory"/>) are decided here and nowhere else, for a caller asking one name and for one
    /// that tells a program from the batch file beside it, as npm's finder does.
    /// </remarks>
    /// <param name="names">The names, in the order a directory is asked for them.</param>
    /// <param name="pathVariable">The PATH whose directories are walked; none where it is <see langword="null"/>.</param>
    /// <param name="windows">Whether the files are started on Windows.</param>
    /// <param name="isExecutable">Whether a candidate file is one this machine would start.</param>
    internal static IEnumerable<string> ProgramsOnPath(IReadOnlyList<string> names, string? pathVariable, bool windows, Func<string, bool> isExecutable)
    {
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(isExecutable);

        if (names.Count == 0 || names.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("A PATH is walked for at least one name, and every name is one.", nameof(names));
        }

        return Found(pathVariable ?? string.Empty);

        IEnumerable<string> Found(string path)
        {
            foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                foreach (var name in names)
                {
                    if (ProgramInDirectory(name, directory.Trim('"'), windows, isExecutable) is { } found)
                    {
                        yield return found;
                    }
                }
            }
        }
    }

    /// <summary>
    /// The file that would start as <paramref name="name"/> in <paramref name="directory"/>, or
    /// <see langword="null"/> when there is none.
    /// </summary>
    /// <remarks>
    /// The one rule for turning a name into a candidate file, used for every PATH entry and for every
    /// directory searched beyond the PATH, so a program found in one place is found the same way in
    /// the other. A relative directory is relative to the working directory, exactly as a shell treats
    /// a PATH entry; one that is not a path at all holds nothing. Joined rather than combined, so a
    /// name Windows reads as rooted, such as C:tool, cannot step out of the directory.
    /// </remarks>
    internal static string? ProgramInDirectory(string name, string directory, bool windows, Func<string, bool> isExecutable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(isExecutable);

        string candidate;

        try
        {
            candidate = Path.Join(Path.GetFullPath(directory), windows ? WithWindowsExtension(name) : name);
        }
        catch (ArgumentException)
        {
            return null;
        }

        return isExecutable(candidate) ? candidate : null;
    }

    /// <summary>
    /// The file to start for <paramref name="fileName"/>: a path is the file it names, and a name is looked
    /// up in the PATH directories and nowhere else.
    /// </summary>
    /// <remarks>
    /// Left to the runtime, a name is looked for beside the running executable and in the current directory
    /// before PATH: .NET does so on Linux and macOS, and CreateProcess does on Windows. The current directory
    /// is usually the repository the harness was pointed at, so a file named git or dotnet committed at its
    /// root would run in place of the real tool. A relative path is made absolute for the same reason: .NET
    /// would otherwise look for it beside its own executable first.
    /// </remarks>
    /// <param name="fileName">The program as the request names it.</param>
    /// <param name="pathVariable">The PATH the child is given.</param>
    /// <exception cref="ExecutableNotFoundException">A name is in none of the PATH directories.</exception>
    private string ResolveProgram(string fileName, string? pathVariable)
        => IsPath(fileName)
            ? ProgramAtPath(fileName, _platform.Current == PlatformId.Windows)
            : ProgramOnPath(fileName, pathVariable, _platform.Current == PlatformId.Windows, _filePermissions.IsExecutable)
                ?? throw new ExecutableNotFoundException(fileName);

    private string? OnPath(string name)
        => ProgramOnPath(name, Environment.GetEnvironmentVariable(PathVariable), _platform.Current == PlatformId.Windows, _filePermissions.IsExecutable);

    /// <summary>
    /// What a failure to start <paramref name="resolved"/> means. It is reported as not found only when the
    /// file really is missing: the operating system gives the same error for a script whose interpreter, or
    /// a program whose loader, is missing, and "not found" would send the reader after a file that is there.
    /// Anything else, such as a file that is not a program for this machine or may not be run, is reported
    /// with the reason the operating system gave.
    /// </summary>
    private ProgramStartException StartFailure(string requested, string resolved, Win32Exception exception)
    {
        var missing = exception.NativeErrorCode == ErrorFileNotFound
            || (exception.NativeErrorCode == WindowsErrorPathNotFound && _platform.Current == PlatformId.Windows);

        if (!missing)
        {
            return new ProgramStartException(requested, $"'{resolved}' could not be started: {exception.Message}", exception);
        }

        return File.Exists(resolved)
            ? new ProgramStartException(
                requested,
                $"'{resolved}' exists but could not be started: the system reports a file missing, which happens when the interpreter or loader it needs is missing.",
                exception)
            : new ExecutableNotFoundException(requested, exception);
    }

    /// <summary>
    /// The file Windows starts for <paramref name="name"/>: the name itself when it has an extension, and
    /// otherwise the name with <c>.exe</c>, the one extension CreateProcess adds. A batch file is never
    /// found for a bare name: cmd.exe would parse its arguments a second time, so they would not arrive as
    /// they were passed.
    /// </summary>
    internal static string WithWindowsExtension(string name) => Path.HasExtension(name) ? name : name + ".exe";

    /// <summary>
    /// One stream of a child, read to its end from the moment this is made, keeping the text exactly as the child wrote
    /// it - all of it, or its end where its <see cref="StreamKept"/> says so - and handing each complete line to its handler
    /// as soon as it arrives.
    /// </summary>
    /// <remarks>
    /// Text rebuilt from lines loses what separated them. git's NUL-separated output would
    /// gain a line break git never wrote, and every line ending would become the host's own,
    /// so one command would capture different text on Windows than on Linux.
    /// <para>
    /// A stream kept whole is held whole, and its lines are handed on whole: the caller asked for it as text. One kept as a
    /// tail is the caller's to keep line by line, and held nowhere else: a test that started an interactive interpreter with
    /// no console printed two gigabytes of the same traceback, ctest printed all of it, and the harness that had kept every
    /// character - in four copies - died out of memory with 34 GB of it free, because no string holds more than about a
    /// billion characters. So only its last <see cref="TailLength"/> characters are kept, and a line of it is handed on in
    /// pieces of at most <see cref="LongestLine"/>, since a line that never ends would otherwise be held whole until it did.
    /// </para>
    /// <para>
    /// A handler that fails is handed no more lines, and the stream is still read to its end: a child writing more than a
    /// pipe holds would otherwise block on a pipe nobody reads, and one whose input is held open - a host's agent, reading a
    /// file back - would never end. What it raised is kept for the caller to raise once the child has gone.
    /// </para>
    /// <para>
    /// What was read is taken when the caller takes it - once the stream has ended, or once the caller has stopped
    /// waiting for an end a process outside the child's container holds off. From then on nothing more is kept or handed
    /// on: the run that asked has returned, and a line arriving after it would be news to nobody.
    /// </para>
    /// </remarks>
    private sealed class StreamCapture
    {
        private readonly Lock _gate = new();
        private readonly Action<string>? _onLine;
        private readonly StringBuilder? _whole;
        private readonly StreamTail? _end;
        private readonly LineSplitter? _lines;
        private ExceptionDispatchInfo? _failed;
        private bool _taken;

        /// <summary>Starts reading <paramref name="reader"/>.</summary>
        /// <param name="reader">The stream.</param>
        /// <param name="onLine">Handed each line as it arrives, or <see langword="null"/>.</param>
        /// <param name="kept">How much of the text is kept.</param>
        /// <param name="cut">Where a long line of a tail is cut, or <see langword="null"/>.</param>
        public StreamCapture(StreamReader reader, Action<string>? onLine, StreamKept kept, Func<string, int>? cut)
        {
            var tail = kept != StreamKept.Whole;
            var cutting = kept == StreamKept.Tail;

            _onLine = onLine;
            _whole = tail ? null : new StringBuilder();
            _end = tail ? new StreamTail(TailLength) : null;
            _lines = onLine is null ? null : new LineSplitter(Hand, cutting ? LongestLine : null, cutting ? cut : null);
            Reading = ReadAsync(reader);
        }

        /// <summary>Ends when the stream does.</summary>
        public Task Reading { get; }

        /// <summary>What has been read, and what the handler raised; nothing is kept or handed on after this.</summary>
        public Captured Taken()
        {
            lock (_gate)
            {
                _taken = true;
                return new Captured(_whole?.ToString() ?? _end!.ToString(), _failed);
            }
        }

        private async Task ReadAsync(StreamReader reader)
        {
            var buffer = new char[ReadBufferSize];
            int read;

            while ((read = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) > 0)
            {
                lock (_gate)
                {
                    if (_taken)
                    {
                        return;
                    }

                    var arrived = buffer.AsSpan(0, read);

                    _whole?.Append(arrived);
                    _end?.Add(arrived);
                    _lines?.Add(arrived);
                }
            }

            lock (_gate)
            {
                // A last line with no line break after it is still a line.
                if (!_taken)
                {
                    _lines?.End();
                }
            }
        }

        private void Hand(string line)
        {
            if (_failed is not null)
            {
                return;
            }

            try
            {
                _onLine!(line);
            }
            catch (Exception ex)
            {
                _failed = ExceptionDispatchInfo.Capture(ex);
            }
        }
    }

    /// <summary>
    /// How the child's standard output is read: under the encoding the caller named, exactly, from its first byte - a reader
    /// .NET makes for a child takes bytes that begin as a byte order mark does for the mark, drops them and decodes the rest
    /// as the mark says, which turns the output of a caller that reads bytes through Latin-1 into other bytes. The encoding
    /// named has no mark of its own, refused otherwise, so none is dropped. Text read as UTF-8, the caller naming none, is
    /// read as .NET reads it.
    /// </summary>
    private static StreamReader OutputOf(Process process, ProcessRequest request)
        => request.StandardOutputEncoding is { } exact
            ? new StreamReader(process.StandardOutput.BaseStream, exact, detectEncodingFromByteOrderMarks: false, OutputBuffer)
            : process.StandardOutput;

    /// <summary>A stream as it was read: what is kept of it, and what its line handler raised, where it raised anything.</summary>
    private sealed record Captured(string Text, ExceptionDispatchInfo? Failed);

    /// <summary>
    /// Raises what the line handlers raised: nothing where neither failed; the failure where one did, or where both raised
    /// the same - of one type, saying one thing, as one reason met on each stream does; and both together where they
    /// differ, since one said and the other lost would send whoever reads it after half the trouble.
    /// </summary>
    /// <param name="output">What the output's handler raised, or <see langword="null"/>.</param>
    /// <param name="error">What the error output's handler raised, or <see langword="null"/>.</param>
    private static void RaiseHandlerFailures(ExceptionDispatchInfo? output, ExceptionDispatchInfo? error)
    {
        if (output is { SourceException: var first } && error is { SourceException: var second }
            && (first.GetType() != second.GetType() || !string.Equals(first.Message, second.Message, StringComparison.Ordinal)))
        {
            throw new AggregateException("The handlers of both the output and the error output failed.", first, second);
        }

        (output ?? error)?.Throw();
    }

    /// <summary>
    /// Writes a child's whole input, then closes it when <paramref name="close"/> is set, which is how the
    /// child learns there is no more to read.
    /// </summary>
    /// <remarks>
    /// Written on a thread of its own: a writer writes as it makes what it writes, for as long as the child takes to read
    /// it. An input that could not be written whole is closed whatever <paramref name="close"/> says - a child holding its
    /// input open waits for the rest of it, which would never come - and what stopped it, where that was not the child
    /// ceasing to read, is raised once the child has gone. An input held open is then written <paramref name="beat"/>,
    /// where one is given, until <paramref name="exited"/> says the child has gone.
    /// </remarks>
    /// <returns>
    /// Why the beat stopped while the child was still running, or <see langword="null"/> where it did not. The input is
    /// closed then, nothing more being written to it; and the reason is handed back, because a beat that fails without a
    /// word leaves whoever started the child with nothing to say why its far end gave this process up.
    /// </returns>
    private static async Task<string?> WriteInputAsync(StreamWriter writer, ChildInput? input, bool close, InputBeat? beat, CancellationToken exited)
    {
        var whole = false;
        string? beatLost = null;

        try
        {
            if (input is not null)
            {
                await Task.Run(() => input.WriteTo(writer.BaseStream, exited)).ConfigureAwait(false);
            }

            await writer.BaseStream.FlushAsync().ConfigureAwait(false);
            whole = true;

            if (!close && beat is not null)
            {
                beatLost = await BeatAsync(writer.BaseStream, beat, exited).ConfigureAwait(false);
            }
        }
        catch (IOException)
        {
            // The child stopped reading, typically by exiting without needing all of it. What it
            // did is reported by its exit code and its output, not by the pipe it left behind.
        }
        finally
        {
            if (close || !whole || beatLost is not null)
            {
                CloseQuietly(writer);
            }
        }

        return beatLost;
    }

    /// <summary>Writes <paramref name="beat"/> to <paramref name="input"/>, a line each time its interval passes, until <paramref name="exited"/>.</summary>
    /// <returns>Why a beat could not be written to a child that was still running, or <see langword="null"/> where every one was.</returns>
    private static async Task<string?> BeatAsync(Stream input, InputBeat beat, CancellationToken exited)
    {
        var line = Utf8NoBom.GetBytes(beat.Line + "\n");

        try
        {
            while (true)
            {
                await Task.Delay(beat.Every, exited).ConfigureAwait(false);

                try
                {
                    await input.WriteAsync(line, exited).ConfigureAwait(false);
                    await input.FlushAsync(exited).ConfigureAwait(false);
                }
                catch (IOException ex)
                {
                    // Its input no longer takes anything. A child that is exiting says the rest itself, and is seen
                    // to have gone within a moment; one that goes on has stopped hearing this process.
                    await Task.Delay(GoingGrace, exited).ConfigureAwait(false);

                    return ex.Message;
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            // The child has gone, and its input with it.
            return null;
        }
    }

    private static void CloseQuietly(StreamWriter writer)
    {
        try
        {
            writer.Close();
        }
        catch (IOException)
        {
            // Closing flushes, and the pipe can already be gone because the child exited.
        }
    }

    private static void KillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // The process exited between the check and the kill.
        }
        catch (Exception ex) when (ex is Win32Exception or AggregateException)
        {
            // A descendant could not be stopped: it was already exiting, or it runs as another user. The
            // process itself must still go, or the wait for it that follows would never end.
            try
            {
                process.Kill();
            }
            catch (Exception inner) when (inner is InvalidOperationException or Win32Exception)
            {
                // It exited meanwhile, or cannot be signalled either; waiting for it says which.
            }
        }
    }
}
