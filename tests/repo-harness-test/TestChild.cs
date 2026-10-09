using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace RepoHarness.Tests;

/// <summary>
/// Behaviours this assembly performs when started as a child process. They give the
/// process tests a program whose timing, output and descendants are exactly known,
/// without depending on a shell or on any other tool being installed.
/// </summary>
internal static class TestChild
{
    /// <summary>
    /// Output is written as UTF-8 explicitly, as git writes it. The console's own encoding
    /// on Windows is a legacy code page, which would make a test of the runner's decoding
    /// measure this child instead.
    /// </summary>
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    internal static int Run(string mode, string[] arguments)
    {
        using var standardOutput = new StreamWriter(Console.OpenStandardOutput(), Utf8NoBom) { AutoFlush = true };
        using var standardError = new StreamWriter(Console.OpenStandardError(), Utf8NoBom) { AutoFlush = true };

        return mode switch
        {
            "echo-args" => EchoArguments(standardOutput, arguments),
            "echo-command-line" => EchoCommandLine(standardOutput, arguments),
            "echo-crlf" => EchoLinesEndedWithCrlf(standardOutput, arguments),
            "echo-stdin" => EchoStandardInput(standardOutput),
            "stdin-to-file" => StandardInputToFile(arguments),
            "read-line-then-watch" => ReadLineThenWatch(standardOutput, arguments),
            "sleep" => Sleep(arguments),
            "stream" => Stream(standardOutput, standardError, arguments),
            "flood" => Flood(standardOutput, arguments),
            "flood-error" => Flood(standardError, arguments),
            "flood-both" => Flood(standardOutput, arguments) + Flood(standardError, arguments),
            "print-file" => PrintFile(standardOutput, arguments),
            "spawn-grandchild" => SpawnGrandchild(arguments),
            "print-env" => PrintEnvironment(standardOutput, arguments),
            "write-file" => WriteFile(arguments),
            "watch-process" => WatchProcess(arguments),
            "link-directory" => LinkDirectory(arguments),
            "exit" => int.Parse(arguments[0], CultureInfo.InvariantCulture),
            _ => 99,
        };
    }

    /// <summary>
    /// Writes <c>arguments[1]</c> into the file <c>arguments[0]</c> names, for a step that has to
    /// actually produce something.
    /// </summary>
    /// <remarks>
    /// Exits zero either way. A step that declares an output and does not write it is the case the
    /// harness has to notice by looking, so this child must be able to succeed at doing nothing.
    /// </remarks>
    private static int WriteFile(string[] arguments)
    {
        if (arguments.Length >= 2)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(arguments[0]) ?? ".");
            File.WriteAllText(arguments[0], arguments[1]);
        }

        // An optional exit code, so a step can produce exactly what it declared and still fail.
        // That is the case a caller has to tell apart from a step that produced nothing.
        return arguments.Length >= 3 ? int.Parse(arguments[2], CultureInfo.InvariantCulture) : 0;
    }

    /// <summary>
    /// Makes <c>arguments[0]</c> a directory holding one file and a link, <c>latest</c>, to the
    /// directory <c>arguments[1]</c>: what a step that keeps a directory of runs with a pointer to the
    /// newest one produces.
    /// </summary>
    private static int LinkDirectory(string[] arguments)
    {
        Directory.CreateDirectory(arguments[0]);
        File.WriteAllText(Path.Combine(arguments[0], "kept.txt"), "kept");
        Directory.CreateSymbolicLink(Path.Combine(arguments[0], "latest"), arguments[1]);
        return 0;
    }

    /// <summary>Writes each argument as a line of its own ended with CRLF, as a Windows program writes one.</summary>
    private static int EchoLinesEndedWithCrlf(TextWriter output, string[] arguments)
    {
        foreach (var argument in arguments)
        {
            output.Write(argument + "\r\n");
        }

        return 0;
    }

    /// <summary>Writes each argument on its own line between brackets, so an empty one is visible.</summary>
    private static int EchoArguments(TextWriter output, string[] arguments)
    {
        foreach (var argument in arguments)
        {
            output.Write("[" + argument + "]\n");
        }

        return 0;
    }

    /// <summary>
    /// Writes the arguments on one line, each between brackets and a space after the one before: what a
    /// witness, read a line at a time, needs to see that two arguments came side by side.
    /// </summary>
    private static int EchoCommandLine(TextWriter output, string[] arguments)
    {
        output.Write(string.Join(' ', arguments.Select(argument => "[" + argument + "]")) + "\n");
        return 0;
    }

    /// <summary>
    /// Reads standard input to its end and writes it back between brackets. Read as UTF-8 bytes,
    /// for the reason output is written that way, and as they came: a byte order mark it began with
    /// is written back, never taken for how to read the rest, as a child reading its input as bytes
    /// would read it as text.
    /// </summary>
    private static int EchoStandardInput(TextWriter output)
    {
        using var input = new StreamReader(Console.OpenStandardInput(), Utf8NoBom, detectEncodingFromByteOrderMarks: false);
        output.Write("[" + input.ReadToEnd() + "]\n");
        return 0;
    }

    /// <summary>
    /// Reads standard input to its end and writes it into the file <c>arguments[0]</c> names: what a child that saw the end
    /// of its input leaves behind, where its output would not be read.
    /// </summary>
    private static int StandardInputToFile(string[] arguments)
    {
        using var input = new StreamReader(Console.OpenStandardInput(), Utf8NoBom);
        File.WriteAllText(arguments[0], input.ReadToEnd(), Utf8NoBom);
        return 0;
    }

    /// <summary>
    /// Reads one line and writes it back, then waits the given milliseconds for the end of its input:
    /// "ended" when the parent closed it, "held" when it was still open after the wait. The reader is
    /// deliberately left undisposed, since a read may still be pending when this process exits.
    /// </summary>
    private static int ReadLineThenWatch(TextWriter output, string[] arguments)
    {
        var input = new StreamReader(Console.OpenStandardInput(), Utf8NoBom);
        output.Write("[" + input.ReadLine() + "]\n");

        var reading = Task.Run(input.Read);
        var ended = reading.Wait(int.Parse(arguments[0], CultureInfo.InvariantCulture)) && reading.Result < 0;

        output.Write(ended ? "ended\n" : "held\n");
        return 0;
    }

    /// <summary>
    /// Writes <c>started</c> and the process <c>arguments[1]</c> names into the file <c>arguments[0]</c> names,
    /// then waits for that process to end and adds <c>ended</c>: what caffeinate -w does with the process it is
    /// given, for a keepAwake that holds a machine awake exactly as long as that process lives.
    /// </summary>
    private static int WatchProcess(string[] arguments)
    {
        var id = int.Parse(arguments[1], CultureInfo.InvariantCulture);

        Directory.CreateDirectory(Path.GetDirectoryName(arguments[0]) ?? ".");
        File.WriteAllText(arguments[0], $"started {id}\n");

        for (var waited = 0; waited < 1200 && Running(id); waited++)
        {
            Thread.Sleep(100);
        }

        File.AppendAllText(arguments[0], "ended\n");
        return 0;

        static bool Running(int id)
        {
            try
            {
                using var process = System.Diagnostics.Process.GetProcessById(id);
                return !process.HasExited;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }
    }

    private static int Sleep(string[] arguments)
    {
        Thread.Sleep(int.Parse(arguments[0], CultureInfo.InvariantCulture));
        return 0;
    }

    /// <summary>
    /// Writes one line, then waits for the parent to prove it received that line while
    /// this process is still running. If output were delivered only at exit, the signal
    /// would never arrive, and the exit code says so.
    /// </summary>
    private static int Stream(TextWriter output, TextWriter error, string[] arguments)
    {
        var signal = arguments[0];

        output.Write("first\n");

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!File.Exists(signal))
        {
            if (DateTime.UtcNow > deadline)
            {
                return 3;
            }

            Thread.Sleep(20);
        }

        error.Write("problem\n");
        output.Write("second\n");
        return 0;
    }

    /// <summary>
    /// Writes <c>arguments[0]</c> lines of <c>arguments[1]</c> characters each, each <see cref="FloodLine"/>, then - where
    /// <c>arguments[2]</c> is given - one line of that many characters, <see cref="GiantLine"/>, with no line feed after it:
    /// what a child that floods its output writes, as fast as its pipe takes it. A test can then say what every line should
    /// be without holding any of them.
    /// </summary>
    private static int Flood(TextWriter output, string[] arguments)
    {
        var count = long.Parse(arguments[0], CultureInfo.InvariantCulture);
        var length = int.Parse(arguments[1], CultureInfo.InvariantCulture);
        var giant = arguments.Length > 2 ? long.Parse(arguments[2], CultureInfo.InvariantCulture) : 0;

        // Written a megabyte at a time: the writer flushes every write, and a write per line would measure this
        // process's system calls rather than the reader's.
        var chunk = new StringBuilder(FloodChunk + length + 1);

        for (var index = 0L; index < count; index++)
        {
            chunk.Append(FloodLine(index, length)).Append('\n');

            if (chunk.Length >= FloodChunk)
            {
                output.Write(chunk);
                chunk.Clear();
            }
        }

        output.Write(chunk);

        for (var written = 0L; written < giant; written += FloodChunk)
        {
            output.Write(GiantLine((int)Math.Min(FloodChunk, giant - written)));
        }

        return 0;
    }

    /// <summary>Writes the text of the file <c>arguments[0]</c> names, as it is: output longer than a command line can carry.</summary>
    private static int PrintFile(TextWriter output, string[] arguments)
    {
        output.Write(File.ReadAllText(arguments[0], Utf8NoBom));
        return 0;
    }

    /// <summary>How many characters <see cref="Flood"/> writes at a time.</summary>
    private const int FloodChunk = 1024 * 1024;

    /// <summary>Line <paramref name="index"/> of a flood: its number, then as many dots as make it <paramref name="length"/> long.</summary>
    internal static string FloodLine(long index, int length)
    {
        var number = index.ToString("D12", CultureInfo.InvariantCulture);

        return number + new string('.', Math.Max(0, length - number.Length));
    }

    /// <summary><paramref name="length"/> characters of the one line a flood ends with, which never ends.</summary>
    internal static string GiantLine(int length) => new('g', length);

    /// <summary>Starts a sleeping grandchild, records its process id, then sleeps as well.</summary>
    private static int SpawnGrandchild(string[] arguments)
    {
        var processIdFile = arguments[0];
        var sleepMilliseconds = arguments[1];

        var start = new ProcessStartInfo(TestHost.DotnetExecutable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        start.ArgumentList.Add("exec");
        start.ArgumentList.Add(TestHost.AssemblyPath);
        start.ArgumentList.Add(sleepMilliseconds);
        start.Environment[TestHost.ChildModeVariable] = "sleep";

        using var grandchild = Process.Start(start)
            ?? throw new InvalidOperationException("The grandchild process did not start.");

        // Written to a temporary name and renamed, so the parent never reads a half-written id.
        var partial = processIdFile + ".partial";
        File.WriteAllText(partial, grandchild.Id.ToString(CultureInfo.InvariantCulture));
        File.Move(partial, processIdFile);

        Thread.Sleep(int.Parse(sleepMilliseconds, CultureInfo.InvariantCulture));
        return 0;
    }

    private static int PrintEnvironment(TextWriter output, string[] arguments)
    {
        output.Write((Environment.GetEnvironmentVariable(arguments[0]) ?? "<unset>") + "\n");
        return 0;
    }
}
