using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DssHarness.Core.Platform;

namespace DssHarness.Core.Processes;

/// <summary>
/// The step a program apart from its commands' isolation hands each of its shell commands to, so the command runs inside
/// it: this tool, started as <c>dssharness --isolated-shell '&lt;command&gt;'</c>, reads the launch's isolation from the
/// file <see cref="FileVariable"/> names, and becomes the mechanism with the shell inside, the command its script -
/// Landlock's scope entered first where the mechanism asks it. Claude Code's own process reads its own login apart from
/// its commands - in an outer layer of the mechanism's on Linux, outside every layer on macOS - while every shell command
/// it starts through <c>CLAUDE_CODE_SHELL_PREFIX</c> is inside a layer of its own, where the process namespace keeps it
/// from Claude Code's environment and the profile from its directories.
/// </summary>
/// <remarks>
/// <para>
/// Where the file names how the program hands its commands over (<see cref="IsolatedShellHanding"/>), two things are done
/// before the shell is, where the program itself runs: the command's receipt is written - the receipt its program's host
/// put at the command's head (<see cref="Marked"/>), a file of that name in the receipts' directory, which no command
/// handed over writes - so the host knows each command it allowed was handed over, and ends a launch that ran one that was
/// not; and the one file the command ends by writing the working directory it ended in to, in the program's own temporary
/// directory, which no command writes either, is made empty and given the command alone, written in place, never removed
/// or replaced (<see cref="EndFileOf"/>) - so nothing of the program's own there is a command's to replace. A receipt says
/// a command was handed over; it keeps nothing from a command that was not, which ran where the program runs and could
/// write one of its own there.
/// </para>
/// <para>
/// Not a command anyone types: help does not list it. A file it cannot read, or that names no mechanism of this machine's,
/// runs nothing: the command fails, and why is said on the error stream - and so does a command whose receipt cannot be
/// written.
/// </para>
/// </remarks>
public static partial class IsolatedShell
{
    /// <summary>The tool's own argument the prefix names, before the command.</summary>
    public const string Option = "--isolated-shell";

    /// <summary>The variable that names the launch's isolation file.</summary>
    public const string FileVariable = "DSSHARNESS_ISOLATED_SHELL";

    /// <summary>The shell a command runs in, by its one path: the one Claude Code is told to use, whose snapshots it sources.</summary>
    public const string Shell = "/bin/bash";

    /// <summary>The code a command ends with where it was not run inside the isolation.</summary>
    public const int NotRun = 126;

    /// <summary>The word a receipt is named after at a command's head: <c>: dssharness-receipt &lt;receipt&gt;; &lt;command&gt;</c>.</summary>
    public const string ReceiptWord = "dssharness-receipt";

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>The isolation's file: the mechanism this machine proved, the profile, and how the program hands its commands over.</summary>
    /// <param name="isolation">The launch's isolation.</param>
    /// <param name="handing">Where the program's commands leave their receipts and their last working directory; null where nothing is asked of them.</param>
    public static string Written(ProcessIsolation isolation, IsolatedShellHanding? handing = null)
    {
        ArgumentNullException.ThrowIfNull(isolation);

        var profile = isolation.Profile;
        var file = new IsolationFile(
            isolation.Mechanism.Name,
            isolation.Mechanism.Version,
            isolation.Mechanism is Bubblewrap bubblewrap ? bubblewrap.Program : null,
            isolation.Mechanism is Bubblewrap masking ? masking.Masked : [],
            isolation.Mechanism.Scoped,
            profile.Hidden,
            profile.Writes,
            profile.Reads,
            profile.Reopened,
            profile.Temp,
            [.. profile.Environment.Select(variable => new IsolationVariable(variable.Key, variable.Value))],
            profile.Sockets,
            handing);

        return JsonSerializer.Serialize(file, Options);
    }

    /// <summary>The isolation <paramref name="text"/> holds and how its commands are handed over, or why it holds none of this machine's.</summary>
    /// <param name="text">The file's text.</param>
    public static (ProcessIsolation? Isolation, IsolatedShellHanding? Handing, string? Problem) Read(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        IsolationFile? file;

        try
        {
            file = JsonSerializer.Deserialize<IsolationFile>(text, Options);
        }
        catch (JsonException ex)
        {
            return (null, null, $"its isolation file does not read ({ex.Message})");
        }

        if (file is null)
        {
            return (null, null, "its isolation file is empty");
        }

        IIsolationMechanism? mechanism = file.Mechanism switch
        {
            Bubblewrap.MechanismName when file.Program is { Length: > 0 } program => new Bubblewrap(program, file.Version, file.Masked) { Scoped = file.Scoped },
            SandboxExec.MechanismName => new SandboxExec(file.Version, MacSandboxHost.ThisMachine.RealPath),
            _ => null,
        };

        if (mechanism is null)
        {
            return (null, null, $"its isolation file names '{file.Mechanism}', no mechanism of this tool's that takes a command handed to it");
        }

        var profile = new IsolationProfile(file.Hidden, file.Writes)
        {
            Reads = file.Reads,
            Reopened = file.Reopened,
            Temp = file.Temp,
            Environment = [.. file.Environment.Select(variable => new KeyValuePair<string, string>(variable.Name, variable.Value))],
            Sockets = file.Sockets ?? [],
        };

        return (new ProcessIsolation(mechanism, profile), file.Handing, null);
    }

    /// <summary>
    /// <paramref name="command"/> with <paramref name="receipt"/> at its head, as its program's host gives it back to be
    /// run: <c>: dssharness-receipt &lt;receipt&gt;; &lt;command&gt;</c> - the shell's no-op, so the command runs and ends as
    /// it was written.
    /// </summary>
    /// <param name="command">The command, as it was decided.</param>
    /// <param name="receipt">The receipt: 32 hexadecimal digits, new for each command.</param>
    public static string Marked(string command, string receipt)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!ReceiptShape().IsMatch(receipt ?? throw new ArgumentNullException(nameof(receipt))))
        {
            throw new ArgumentException("a receipt is 32 lowercase hexadecimal digits", nameof(receipt));
        }

        return $": {ReceiptWord} {receipt}; {command}";
    }

    /// <summary>A new receipt: 32 hexadecimal digits from the system's random source.</summary>
    public static string NewReceipt() => Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));

    /// <summary>
    /// The receipt at the head of the command <paramref name="handed"/> holds - the first <see cref="Marked"/> put there,
    /// wherever the program that handed it over quoted it; null where it holds none.
    /// </summary>
    /// <param name="handed">The whole command line the program handed over.</param>
    public static string? ReceiptOf(string handed)
    {
        ArgumentNullException.ThrowIfNull(handed);

        return MarkShape().Match(handed) is { Success: true } found ? found.Groups[1].Value : null;
    }

    /// <summary>
    /// The command line <paramref name="handed"/> as it is run, without the mark of its receipt (<see cref="ReceiptOf"/>) -
    /// the shell's no-op and its one following blank taken out, which leaves the command as it was written - so no command
    /// finds its own receipt in what it was started with; the line as it is where it holds none.
    /// </summary>
    /// <param name="handed">The whole command line the program handed over.</param>
    public static string Unmarked(string handed)
    {
        ArgumentNullException.ThrowIfNull(handed);

        if (MarkShape().Match(handed) is not { Success: true } found)
        {
            return handed;
        }

        var end = found.Index + found.Length;

        if (end < handed.Length && handed[end] == ' ')
        {
            end++;
        }

        return string.Concat(handed.AsSpan(0, found.Index), handed.AsSpan(end));
    }

    /// <summary>
    /// The one file the command <paramref name="handed"/> ends by writing the working directory it ended in to: what
    /// Claude Code ends every command it hands over with, <c>pwd -P &gt;| &lt;file&gt;</c>, the file named as the shell reads
    /// the word - quoted, escaped or bare - and named <c>claude-&lt;hex&gt;-cwd</c> directly in <paramref name="programTemp"/>.
    /// Null where the command ends no such way, or the word holds anything the shell would expand.
    /// </summary>
    /// <param name="handed">The whole command line the program handed over.</param>
    /// <param name="programTemp">The program's own temporary directory, which no command writes.</param>
    public static string? EndFileOf(string handed, string programTemp)
    {
        ArgumentNullException.ThrowIfNull(handed);
        ArgumentException.ThrowIfNullOrWhiteSpace(programTemp);

        const string Ending = "pwd -P >| ";
        var at = handed.LastIndexOf(Ending, StringComparison.Ordinal);

        if (at < 0 || Word(handed[(at + Ending.Length)..].TrimEnd()) is not { } file)
        {
            return null;
        }

        var directory = programTemp.Length > 1 ? programTemp.TrimEnd('/') : programTemp;

        return file.StartsWith(directory + "/", StringComparison.Ordinal) && EndFileShape().IsMatch(file[(directory.Length + 1)..]) ? file : null;
    }

    /// <summary>
    /// What <paramref name="isolation"/> runs <paramref name="command"/> under once it is handed over, its receipt written
    /// first: the profile with the one file the command ends by writing to given it, where the file names how commands are
    /// handed over; or why it is not run.
    /// </summary>
    /// <param name="isolation">The launch's isolation, as its file holds it.</param>
    /// <param name="handing">How the launch's commands are handed over; null where nothing is asked of them.</param>
    /// <param name="command">The command, as the program that hands it over gives it.</param>
    public static (ProcessIsolation? Isolation, string? Problem) Prepared(ProcessIsolation isolation, IsolatedShellHanding? handing, string command)
    {
        ArgumentNullException.ThrowIfNull(isolation);
        ArgumentNullException.ThrowIfNull(command);

        if (handing is null)
        {
            return (isolation, null);
        }

        if (ReceiptOf(command) is { } receipt
            && Made(Path.Combine(handing.Receipts, receipt), DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture) + "\n") is { } unwritten)
        {
            return (null, $"its receipt '{receipt}' could not be written in '{handing.Receipts}' ({unwritten})");
        }

        if (EndFileOf(command, handing.ProgramTemp) is not { } end)
        {
            return (isolation, null);
        }

        if (Made(end, string.Empty) is { } unmade)
        {
            return (null, $"the file it ends by writing its working directory to, '{end}', could not be made ({unmade})");
        }

        var profile = isolation.Profile with { Writes = [.. isolation.Profile.Writes, new IsolatedWrite(end, Directory: false, Writable: true)] };

        return (new ProcessIsolation(isolation.Mechanism, profile), null);
    }

    /// <summary>
    /// Makes the file <paramref name="path"/> holding <paramref name="text"/> - new, never through a link at its place; one
    /// that is there already as a file of its own, left by an earlier command, is kept as it is, for no command writes where
    /// it stands - or says why it could not.
    /// </summary>
    private static string? Made(string path, string text)
    {
        try
        {
            using var made = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            made.Write(Encoding.UTF8.GetBytes(text));
            return null;
        }
        catch (IOException) when (new FileInfo(path) is { Exists: true, LinkTarget: null })
        {
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message.TrimEnd('.');
        }
    }

    /// <summary>
    /// Runs <paramref name="command"/> in <see cref="Shell"/> inside the isolation the file <paramref name="file"/> names
    /// holds: becomes the mechanism, on Linux and macOS, and returns only where it could not. The command is run without
    /// the mark of its receipt (<see cref="Unmarked"/>), and handed no variable that hands it over
    /// (<see cref="IsolatedShellHanding.Variables"/>, <see cref="FileVariable"/> among them) - neither in its own
    /// environment nor in the mechanism's, which a process inside reads - so nothing it is started with names its receipt
    /// or where receipts are written.
    /// </summary>
    /// <param name="file">The isolation file, as <see cref="FileVariable"/> names it; null where it names none.</param>
    /// <param name="command">The command, as the program that hands it over gives it.</param>
    /// <param name="error">Where why it was not run is said.</param>
    /// <returns><see cref="NotRun"/> where the command was not run.</returns>
    public static int Run(string? file, string command, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(error);

        if (OperatingSystem.IsWindows())
        {
            error.WriteLine("dssharness: the command was not run: nothing is handed a command on Windows, where a launch runs whole inside MXC's container.");
            return NotRun;
        }

        if (string.IsNullOrEmpty(file) || !Path.IsPathFullyQualified(file) || !File.Exists(file))
        {
            error.WriteLine($"dssharness: the command was not run: '{FileVariable}' names no isolation file ('{file}').");
            return NotRun;
        }

        var (read, handing, problem) = Read(File.ReadAllText(file));
        var (isolation, unprepared) = read is null ? (null, problem) : Prepared(read, handing, command);

        if (isolation is null)
        {
            error.WriteLine($"dssharness: the command was not run: {unprepared}.");
            return NotRun;
        }

        var (program, arguments) = isolation.Mechanism.Command(isolation.Profile, Shell, ["-c", Unmarked(command)]);
        var refused = Containment.Become(isolation.Mechanism.Scoped, program, arguments, [FileVariable, .. handing?.Variables ?? []]);

        error.WriteLine($"dssharness: the command was not run: {isolation.Mechanism.Name} '{program}' could not be started ({new System.ComponentModel.Win32Exception(refused).Message}).");
        return NotRun;
    }

    /// <summary>
    /// One word as a POSIX shell reads it, the whole of <paramref name="text"/>: single quotes kept literally, double
    /// quotes with the backslash escaping only <c>$ ` " \</c>, and a backslash outside them escaping any letter. Null where
    /// the word holds anything the shell would expand or split on - a variable, a substitution, a pattern, a brace, a tilde
    /// at its head, a blank or an operator outside quotes - or an unclosed quote.
    /// </summary>
    private static string? Word(string text)
    {
        var word = new StringBuilder();

        for (var at = 0; at < text.Length; at++)
        {
            var letter = text[at];

            switch (letter)
            {
                case '\'':
                    var closing = text.IndexOf('\'', at + 1);

                    if (closing < 0)
                    {
                        return null;
                    }

                    word.Append(text, at + 1, closing - at - 1);
                    at = closing;
                    break;

                case '"':
                    for (at++; ; at++)
                    {
                        if (at >= text.Length || text[at] is '$' or '`')
                        {
                            return null;
                        }

                        if (text[at] == '"')
                        {
                            break;
                        }

                        if (text[at] == '\\' && at + 1 < text.Length && text[at + 1] is '$' or '`' or '"' or '\\')
                        {
                            at++;
                        }

                        word.Append(text[at]);
                    }

                    break;

                case '\\':
                    if (++at >= text.Length)
                    {
                        return null;
                    }

                    word.Append(text[at]);
                    break;

                case '~' when at == 0:
                    return null;

                default:
                    if (char.IsWhiteSpace(letter) || "$`*?[]{}|&;<>()".Contains(letter, StringComparison.Ordinal))
                    {
                        return null;
                    }

                    word.Append(letter);
                    break;
            }
        }

        return word.Length == 0 ? null : word.ToString();
    }

    [GeneratedRegex("^[0-9a-f]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex ReceiptShape();

    [GeneratedRegex(": " + ReceiptWord + " ([0-9a-f]{32});", RegexOptions.CultureInvariant)]
    private static partial Regex MarkShape();

    [GeneratedRegex("^claude-[0-9a-f]+-cwd$", RegexOptions.CultureInvariant)]
    private static partial Regex EndFileShape();

    /// <summary>The file's shape.</summary>
    private sealed record IsolationFile(
        string Mechanism,
        string Version,
        string? Program,
        IReadOnlyList<IsolatedPath> Masked,
        bool Scoped,
        IReadOnlyList<IsolatedPath> Hidden,
        IReadOnlyList<IsolatedWrite> Writes,
        IReadOnlyList<string> Reads,
        IReadOnlyList<string> Reopened,
        string? Temp,
        IReadOnlyList<IsolationVariable> Environment,
        IReadOnlyList<string>? Sockets,
        IsolatedShellHanding? Handing);

    /// <summary>One variable the profile sets.</summary>
    private sealed record IsolationVariable(string Name, string Value);
}

/// <summary>
/// How a program apart from its commands' isolation hands its commands over, as its isolation file says it: where each
/// command's receipt is written, the program's own temporary directory, where a command ends by writing the working
/// directory it ended in to the one file it is given there - no command writes either - and the variables that hand the
/// commands over, which no command is given.
/// </summary>
/// <param name="Receipts">The directory each command's receipt is written in, by the receipt at its head.</param>
/// <param name="ProgramTemp">The program's own temporary directory: Claude Code's <c>CLAUDE_CODE_TMPDIR</c>.</param>
public sealed record IsolatedShellHanding(string Receipts, string ProgramTemp)
{
    /// <summary>
    /// The variables in the program's environment that hand its commands over - the shell prefix, the isolation file, the
    /// shell and the program's own temporary directory - taken from each command's environment before it runs, so none
    /// names a command the places its receipts are written in; none where left out.
    /// </summary>
    public IReadOnlyList<string> Variables { get; init; } = [];
}
