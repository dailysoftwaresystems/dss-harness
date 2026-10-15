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
    /// <summary>Longest a single CLI invocation may run before the test fails as hung.</summary>
    private static readonly TimeSpan Budget = TimeSpan.FromMinutes(2);

    private static readonly Lazy<string> CliAssembly = new(LocateCliAssembly, LazyThreadSafetyMode.PublicationOnly);

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
    /// <param name="environment">Variables to set for the CLI, or to remove where the value is null.</param>
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
                Environment = environment ?? new Dictionary<string, string?>(StringComparer.Ordinal),

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
