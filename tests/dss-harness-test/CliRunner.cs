using System.Reflection;
using DssHarness.Core.Hosts;
using DssHarness.Core.Platform;
using DssHarness.Core.Processes;

namespace DssHarness.Tests;

/// <summary>
/// Runs the real <c>DssHarness</c> CLI. End-to-end tests exercise the built program
/// rather than calling services directly, so argument parsing, dependency wiring and exit
/// codes are all covered by the same assertion.
/// </summary>
public static class CliRunner
{
    /// <summary>
    /// Longest a single CLI invocation may run before the test fails as hung: a guard against a hang, well above the
    /// slowest real run. The Windows arm64 runner's speed wanders threefold between runs - a check-mutations sweep of
    /// the fixture that took under a minute there ran past five, with nothing hung - and the suite's own notices name
    /// a test still running every two minutes, so a hang is named long before this ends it.
    /// </summary>
    private static readonly TimeSpan Budget = TimeSpan.FromMinutes(15);

    private static readonly Lazy<string> CliAssembly = new(LocateCliAssembly, LazyThreadSafetyMode.PublicationOnly);

    /// <summary>The variables a test names a home, or a user's data, of its own by: one of them given, its environment stands whole.</summary>
    private static readonly string[] UserDataVariables = ["HOME", "XDG_DATA_HOME", "LOCALAPPDATA"];

    private static readonly Lazy<(string Variable, string Value, string State)> OwnUserData = new(MakeOwnUserData, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// The directory every CLI this test process starts keeps this tool's own state in (<see cref="UserState.Directory"/>) -
    /// this machine's settings file, its dispatch queue and the other records it keeps for its user - unless a test names a
    /// home or a user's data of its own: a directory of this process's own, below the suite's temporary root, given in the
    /// one variable each system reads that data from (<see cref="WithOwnUserData"/>). The machine's own user keeps a
    /// settings file and records every command of theirs shares, and they are no test's: read, the count of heavy legs a
    /// developer's machine admits would put every CLI of every test process on it in one queue, beside the developer's own
    /// runs, and write each test repository's history into the developer's records - where a machine with no settings
    /// file admits every test's legs at once.
    /// </summary>
    public static string UserStateDirectory => OwnUserData.Value.State;

    /// <summary>The CLI assembly the tests run: the build a host's DssHarness is compared with.</summary>
    public static string CliAssemblyPath => CliAssembly.Value;

    /// <summary>
    /// The program that starts that assembly, beside it, as a host starts its DssHarness: what a host whose agent is this
    /// build runs, where a test reaches one on this machine.
    /// </summary>
    public static string CliProgram => Path.ChangeExtension(CliAssembly.Value, OperatingSystem.IsWindows() ? ".exe" : null);

    /// <summary>Runs the CLI and returns its result.</summary>
    /// <param name="arguments">The command line after <c>DssHarness</c>.</param>
    /// <param name="cancellationToken">Stops the run.</param>
    /// <param name="workingDirectory">The directory the CLI starts in, or the test's own.</param>
    /// <param name="standardInput">Text the CLI reads on standard input, as a host's DssHarness reads a request.</param>
    /// <param name="environment">
    /// Variables to set for the CLI, or to remove where the value is null; the user's data of this process's own beside
    /// them, where none of them names a home or a user's data of its own (<see cref="UserStateDirectory"/>).
    /// </param>
    public static async Task<ProcessResult> RunAsync(
        string[] arguments,
        CancellationToken cancellationToken,
        string? workingDirectory = null,
        string? standardInput = null,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        var runner = new ProcessRunner(new HostPlatform(), FilePermissionsFactory.Create());

        var result = await runner.RunAsync(
            new ProcessRequest
            {
                FileName = TestHost.DotnetExecutable,
                Arguments = ["exec", CliAssembly.Value, .. arguments],
                WorkingDirectory = workingDirectory,
                StandardInput = standardInput,
                Environment = WithOwnUserData(environment),

                // Held open as the machine that reaches a host holds it: the end of a host's input is how the
                // host learns that machine has gone, so input closed at once would cancel the request.
                HoldStandardInputOpen = standardInput is not null,
                Timeout = Budget,
            },
            cancellationToken);

        // A hang reported as "exit code -1 was not 0" would send the reader after the
        // wrong failure.
        Assert.False(
            result.TimedOut,
            $"'{ToolPackage.Command} {string.Join(' ', arguments)}' did not finish within {Budget}.");

        return result;
    }

    /// <summary>
    /// <paramref name="environment"/>, with the user's data of this process's own (<see cref="UserStateDirectory"/>) where it
    /// names no home and no user's data of its own: one that does - as a test that keeps a hold, or a cache, of its own
    /// does - stands as it was given.
    /// </summary>
    /// <param name="environment">The variables a test gives the CLI; none where it gives none.</param>
    internal static Dictionary<string, string?> WithOwnUserData(IReadOnlyDictionary<string, string?>? environment)
    {
        var given = new Dictionary<string, string?>(environment ?? new Dictionary<string, string?>(), StringComparer.Ordinal);

        if (!UserDataVariables.Any(given.ContainsKey))
        {
            var (variable, value, _) = OwnUserData.Value;

            given[variable] = value;
        }

        return given;
    }

    /// <summary>
    /// Makes this process's own user data, once: the variable each system reads a user's data from - LOCALAPPDATA on
    /// Windows, HOME on macOS, whose data is below it in Library/Application Support, and XDG_DATA_HOME on Linux - its
    /// value, and this tool's own directory below it, as <see cref="UserState"/> reads them. Removed as the process ends.
    /// </summary>
    private static (string Variable, string Value, string State) MakeOwnUserData()
    {
        var root = Path.Combine(TestHost.TemporaryRoot, $"u{Environment.ProcessId}-{Guid.NewGuid().ToString("N")[..8]}");

        Directory.CreateDirectory(root);
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A CLI still ending holds a file of it: the temporary root keeps it, as it keeps what any test left.
            }
        };

        var (variable, data) = OperatingSystem.IsWindows() ? ("LOCALAPPDATA", root)
            : OperatingSystem.IsMacOS() ? ("HOME", Path.Combine(root, "Library", "Application Support"))
            : ("XDG_DATA_HOME", root);

        return (variable, root, Path.Combine(data, ToolPackage.Command));
    }

    /// <summary>The names of the commands the root help lists, in its order.</summary>
    /// <param name="cancellationToken">Stops the run.</param>
    public static async Task<IReadOnlyList<string>> CommandsAsync(CancellationToken cancellationToken)
    {
        var result = await RunAsync(["--help"], cancellationToken);

        return [.. CommandLines(result.StandardOutput)
            .Select(line => line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)[0])
            .Where(name => name.Length > 0)];
    }

    /// <summary>The lines of the root help listing that describe a command.</summary>
    /// <param name="helpOutput">What the root help printed.</param>
    public static IEnumerable<string> CommandLines(string helpOutput)
    {
        ArgumentNullException.ThrowIfNull(helpOutput);

        var inCommands = false;

        foreach (var line in helpOutput.Split('\n').Select(l => l.TrimEnd('\r')))
        {
            if (line.StartsWith("Commands:", StringComparison.Ordinal))
            {
                inCommands = true;
                continue;
            }

            if (!inCommands)
            {
                continue;
            }

            if (line.Trim().Length == 0)
            {
                break;
            }

            yield return line;
        }
    }

    /// <summary>The usage line of a command's own help - the first line under <c>Usage:</c> - trimmed; none where there is none.</summary>
    /// <param name="helpOutput">What the command's own help printed.</param>
    public static string? UsageLine(string helpOutput)
    {
        ArgumentNullException.ThrowIfNull(helpOutput);

        return helpOutput
            .Split('\n')
            .SkipWhile(line => !line.StartsWith("Usage:", StringComparison.Ordinal))
            .Skip(1)
            .FirstOrDefault(line => line.Trim().Length > 0)
            ?.Trim();
    }

    /// <summary>
    /// The arguments a command takes, required or not, in order, each as the usage line of its own help writes it:
    /// <c>&lt;name&gt;</c>, <c>[&lt;name&gt;]</c>, <c>&lt;names&gt;...</c>.
    /// </summary>
    /// <param name="helpOutput">What the command's own help printed.</param>
    public static IReadOnlyList<string> Arguments(string helpOutput)
        => [.. (UsageLine(helpOutput) ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(token => token.StartsWith('<') || token.StartsWith("[<", StringComparison.Ordinal))];

    /// <summary>How many arguments a command requires, as the usage line of its own help names them.</summary>
    /// <param name="helpOutput">What the command's own help printed.</param>
    public static int RequiredArguments(string helpOutput) => Arguments(helpOutput).Count(token => token.StartsWith('<'));

    /// <summary>
    /// Reads the CLI assembly's path from this assembly's metadata, where the build put it
    /// by asking the CLI project for its own output path. Guessing it from directory names
    /// breaks as soon as the output layout changes.
    /// </summary>
    private static string LocateCliAssembly()
    {
        var path = typeof(CliRunner).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "DssHarnessCliPath")
            ?.Value;

        Assert.False(
            string.IsNullOrEmpty(path),
            "This test assembly records no DssHarnessCliPath; the EmbedCliPath build target did not run.");

        Assert.True(File.Exists(path), $"The CLI assembly is not at '{path}'. Build the solution first.");
        return path;
    }
}
