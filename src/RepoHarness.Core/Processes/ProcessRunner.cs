using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text;
using RepoHarness.Core.Platform;

namespace RepoHarness.Core.Processes;

/// <inheritdoc cref="IProcessRunner"/>
public sealed class ProcessRunner(IHostPlatform platform, IFilePermissions filePermissions) : IProcessRunner
{
    /// <summary>ENOENT on Linux and macOS, ERROR_FILE_NOT_FOUND on Windows.</summary>
    private const int ErrorFileNotFound = 2;

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

    private readonly IHostPlatform _platform = platform;
    private readonly IFilePermissions _filePermissions = filePermissions;

    /// <summary>
    /// The variable a program is looked up on. Spelled once: on Windows the environment a child is
    /// given compares names without case, so this also finds the 'Path' Windows itself writes.
    /// </summary>
    private const string PathVariable = "PATH";

    public async Task<ProcessResult> RunAsync(
        ProcessRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

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

        foreach (var argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

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

        if (request.AppendToPath.Count > 0)
        {
            var current = startInfo.Environment.TryGetValue(PathVariable, out var inherited) ? inherited : null;
            var appended = string.Join(
                Path.PathSeparator,
                new[] { current }.Concat(request.AppendToPath).Where(part => !string.IsNullOrEmpty(part)));

            foreach (var name in Spellings(startInfo.Environment, PathVariable))
            {
                startInfo.Environment[name] = appended;
            }
        }

        // Looked up on the PATH the child is given, not on this process's own. The two used to differ
        // whenever a request changed PATH, and then the program started was one the child's own
        // lookups could not see: cmake resolved from one PATH and the ninja it starts from another.
        startInfo.FileName = ResolveProgram(
            request.FileName,
            startInfo.Environment.TryGetValue(PathVariable, out var effective) ? effective : null);

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var stopwatch = Stopwatch.StartNew();

        try
        {
            process.Start();

            // Before the readers below, so nothing the child says can arrive ahead of the fact that
            // it started.
            request.OnStarted?.Invoke();
        }
        catch (Win32Exception ex)
        {
            throw StartFailure(request.FileName, startInfo.FileName, ex);
        }

        // Both streams are read from the moment the process starts: a child that fills a
        // pipe nobody is reading blocks forever.
        var standardOutput = CaptureAsync(process.StandardOutput, request.OnOutputLine, request.OutputKept, request.CutLine);
        var standardError = CaptureAsync(process.StandardError, request.OnErrorLine, request.ErrorKept, request.CutLine);

        // Written while both output streams are being read, so a child that answers as it reads
        // can never block this on a full output pipe, nor this block it on a full input pipe.
        using var exited = new CancellationTokenSource();
        var standardInput = WriteInputAsync(
            process.StandardInput,
            request.StandardInput,
            close: !request.HoldStandardInputOpen,
            request.HoldStandardInputOpen ? request.StandardInputBeat : null,
            exited.Token);

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (request.Timeout is { } budget)
        {
            timeoutSource.CancelAfter(budget);
        }

        var stopped = false;

        try
        {
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Whether for the budget or because the caller gave up, the whole tree goes: a
            // descendant left running keeps the pipes, and whatever it was building, busy.
            stopped = true;
            KillTree(process);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }

        // No beat is written to a child that has gone.
        await exited.CancelAsync().ConfigureAwait(false);

        // The process has exited, but what it wrote just before exiting can still be in the
        // pipes. Reading both streams to their end is what guarantees none of it is lost.
        var capturedOutput = await standardOutput.ConfigureAwait(false);
        var capturedError = await standardError.ConfigureAwait(false);
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
            throw new OperationCanceledException(cancellationToken);
        }

        return new ProcessResult(
            ExitCode: stopped ? -1 : process.ExitCode,
            StandardOutput: capturedOutput.Text,
            StandardError: capturedError.Text,
            Duration: stopwatch.Elapsed,
            TimedOut: stopped)
        {
            BeatLost = beatLost,
        };
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
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(isExecutable);

        if (string.IsNullOrEmpty(pathVariable))
        {
            return null;
        }

        foreach (var directory in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (ProgramInDirectory(name, directory.Trim('"'), windows, isExecutable) is { } found)
            {
                return found;
            }
        }

        return null;
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
    /// Reads one stream to its end, keeping the text exactly as the child wrote it - all of it, or its end where
    /// <paramref name="kept"/> says so - and hands each complete line to <paramref name="onLine"/> as soon as it arrives.
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
    /// </remarks>
    private static async Task<Captured> CaptureAsync(StreamReader reader, Action<string>? onLine, StreamKept kept, Func<string, int>? cut)
    {
        var tail = kept != StreamKept.Whole;
        var cutting = kept == StreamKept.Tail;
        var whole = tail ? null : new StringBuilder();
        var end = tail ? new StreamTail(TailLength) : null;
        ExceptionDispatchInfo? failed = null;
        var lines = onLine is null ? null : new LineSplitter(Hand, cutting ? LongestLine : null, cutting ? cut : null);
        var buffer = new char[ReadBufferSize];
        int read;

        while ((read = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) > 0)
        {
            var arrived = buffer.AsSpan(0, read);

            whole?.Append(arrived);
            end?.Add(arrived);
            lines?.Add(arrived);
        }

        // A last line with no line break after it is still a line.
        lines?.End();

        return new Captured(whole?.ToString() ?? end!.ToString(), failed);

        void Hand(string line)
        {
            if (failed is not null)
            {
                return;
            }

            try
            {
                onLine!(line);
            }
            catch (Exception ex)
            {
                failed = ExceptionDispatchInfo.Capture(ex);
            }
        }
    }

    /// <summary>A stream read to its end: what is kept of it, and what its line handler raised, where it raised anything.</summary>
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
    /// closed then: a beat that fails without a word leaves the child counting a silence, and whoever started it with
    /// nothing to say why the child gave up on it.
    /// </returns>
    private static async Task<string?> WriteInputAsync(StreamWriter writer, ChildInput? input, bool close, InputBeat? beat, CancellationToken exited)
    {
        var whole = false;
        string? beatLost = null;

        try
        {
            if (input is not null)
            {
                await Task.Run(() => input.WriteTo(writer.BaseStream)).ConfigureAwait(false);
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
