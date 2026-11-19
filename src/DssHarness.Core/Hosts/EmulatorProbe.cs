using System.Text.RegularExpressions;
using DssHarness.Core.Configuration;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Output;
using DssHarness.Core.Platform;
using DssHarness.Core.Processes;
using DssHarness.Core.Text;

namespace DssHarness.Core.Hosts;

/// <summary>
/// Checks, on the machine it runs on, whether an emulator can carry legs there: the machine is one it
/// runs on, what it requires is present, and its witness proves it runs programs for its processor.
/// </summary>
/// <param name="platform">Says which platform and processor this machine is.</param>
/// <param name="processRunner">Runs the witness.</param>
/// <param name="fileSystem">Checks a requirement named by path.</param>
/// <param name="output">Where a witness that lasts is told; nowhere where none is given.</param>
/// <param name="clock">The clock that wait is measured on; the machine's own where none is given.</param>
public sealed class EmulatorProbe(
    IHostPlatform platform,
    IProcessRunner processRunner,
    IFileSystem fileSystem,
    IHarnessOutput? output = null,
    TimeProvider? clock = null)
{
    private readonly IHostPlatform _platform = platform;
    private readonly IProcessRunner _processRunner = processRunner;
    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly IHarnessOutput? _output = output;
    private readonly TimeProvider? _clock = clock;

    /// <summary>
    /// The programs <paramref name="emulator"/> needs found by name: what its requirements name, and
    /// the program its witness starts. What a search for them has to include.
    /// </summary>
    /// <param name="emulator">The emulator.</param>
    public static IEnumerable<string> ProgramsOf(EmulatorConfig emulator)
    {
        ArgumentNullException.ThrowIfNull(emulator);

        return WitnessCommand(emulator).Take(1)
            .Concat(emulator.Requires)
            .Where(program => !string.IsNullOrWhiteSpace(program) && !ProcessRunner.IsPath(program));
    }

    /// <summary>
    /// The programs of <see cref="ProgramsOf"/> a check of <paramref name="emulator"/> reads from the search: all of
    /// them, where its witness is looked for on the PATH the search saw, and none where its environment sets a PATH
    /// of its own, on which its programs are the witness's to find.
    /// </summary>
    /// <param name="emulator">The emulator.</param>
    /// <remarks>
    /// Also the ones a leg that runs through it is held to a declared tool's minimum version over (see
    /// <see cref="Legs.LegPlacement"/>): whether that minimum is the leg's is the tool's scope, which only the leg can
    /// say, so the check here takes a copy held to it as the one there, which the witness starts.
    /// </remarks>
    public static IEnumerable<string> ProgramsRead(EmulatorConfig emulator)
    {
        ArgumentNullException.ThrowIfNull(emulator);

        return ReadsSearch(emulator) ? ProgramsOf(emulator) : [];
    }

    /// <summary>Whether a check of <paramref name="emulator"/> reads its programs from the search (see <see cref="ProgramsRead"/>).</summary>
    private static bool ReadsSearch(EmulatorConfig emulator) => !ProcessRunner.SetsPath(emulator.Env.Keys);

    /// <summary>Checks <paramref name="emulator"/> on this machine.</summary>
    /// <param name="emulator">The emulator.</param>
    /// <param name="found">
    /// Where the search a leg here uses found this machine's programs, <see cref="ProgramsOf"/> among
    /// them. A requirement installed off the PATH is then present, and the witness is started with
    /// the PATH a leg's own run is given, rather than either being looked for on a PATH no leg uses.
    /// </param>
    /// <param name="cancellationToken">Stops the witness.</param>
    /// <exception cref="ArgumentException">
    /// A requirement named by name, or the program the witness starts, was not searched for.
    /// </exception>
    public async Task<EmulatorCheck> CheckAsync(
        EmulatorConfig emulator,
        ProgramSearch found,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(emulator);
        ArgumentNullException.ThrowIfNull(found);

        if (!Same(emulator.HostOs, _platform.PlatformKey) || !Same(emulator.HostProcessor, _platform.Processor))
        {
            return EmulatorCheck.Unavailable(
                $"it runs on {emulator.HostOs} {emulator.HostProcessor} hosts, and this one is {_platform.PlatformKey} {_platform.Processor}");
        }

        // A name is read from the search only where the witness is looked for on the PATH the search
        // saw. An emulator whose environment sets PATH is found on that PATH, which no search can
        // see: its programs are the witness's to find, and one the search missed is no missing one.
        var searched = ReadsSearch(emulator);

        foreach (var requirement in emulator.Requires.Where(requirement => searched || ProcessRunner.IsPath(requirement)))
        {
            if (Absence(requirement, found) is { } absent)
            {
                return EmulatorCheck.Unavailable(absent);
            }
        }

        var command = WitnessCommand(emulator);

        // The program the witness starts, when it is named rather than given by path, is read from
        // the search as a requirement is: one nobody could look for everywhere is unknown, and
        // starting it anyway would report it as not found.
        if (searched && !ProcessRunner.IsPath(command[0]) && Absence(command[0], found) is { } unstarted)
        {
            return EmulatorCheck.Unavailable(unstarted);
        }

        ProcessResult result;

        try
        {
            // Waited for to its end, and told while it lasts: an emulator's first start - a binfmt handler loading, a
            // sysroot read cold - is slow, and no failure; the person's stop ends it.
            result = await ProgramWait
                .ToItsEndAsync(
                    token => _processRunner.RunAsync(
                        new ProcessRequest
                        {
                            FileName = command[0],
                            Arguments = command[1..],
                            Environment = emulator.Env.ToDictionary(pair => pair.Key, pair => (string?)pair.Value, StringComparer.Ordinal),
                            ProgramDirectories = found.Directories,
                        },
                        token),
                    $"the witness '{command[0]}' to end",
                    ProgramWait.Told(_output, ToolPackage.Command),
                    _clock,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ExecutableNotFoundException ex)
        {
            return EmulatorCheck.Unavailable($"{ex.FileName} was not found");
        }
        catch (ProgramStartException ex)
        {
            // A witness that exists but cannot start is the expected result on a host where the emulation is
            // not installed: a program for another processor with no handler registered for it. It says this
            // emulator cannot serve the host, not that the host, or the check, is broken.
            return EmulatorCheck.Unavailable($"its witness could not start: {ex.Message}");
        }

        if (result.ExitCode != 0)
        {
            return EmulatorCheck.Unavailable(
                $"its witness exited {result.ExitCode}: {HostProbes.Quoted(result.StandardError + "\n" + result.StandardOutput)}");
        }

        // Each line as it reads, whatever ended it, so a witness pattern ending in $ means the same on Windows.
        var output = LineText.Of(result.TrimmedOutput);

        // Matched in time linear in what the witness printed, however the pattern nests: no match is cut off by a clock.
        if (!LinearRegex.TryCompile(emulator.Witness.Pattern, RegexOptions.Multiline, out var witness, out var refusal))
        {
            return EmulatorCheck.Unavailable($"its witness pattern cannot be matched: {refusal.TrimEnd('.')}");
        }

        return witness.IsMatch(output)
            ? new EmulatorCheck(true, null, HostProbes.Quoted(output))
            : EmulatorCheck.Unavailable(
                $"its witness printed '{HostProbes.Quoted(output)}', which does not match {emulator.Witness.Pattern}");
    }

    /// <summary>The command the witness runs: the launcher, when there is one, and then the witness's own.</summary>
    private static string[] WitnessCommand(EmulatorConfig emulator) => [.. emulator.Launcher ?? [], .. emulator.Witness.Command];

    /// <summary>
    /// Why <paramref name="requirement"/> is not there, or <see langword="null"/> when it is. One
    /// naming a path is checked where it points, since a sysroot is a directory rather than a
    /// program; one naming a program is read from where the search found it.
    /// </summary>
    private string? Absence(string requirement, ProgramSearch found)
    {
        if (ProcessRunner.IsPath(requirement))
        {
            return _fileSystem.FileExists(requirement) || _fileSystem.DirectoryExists(requirement)
                ? null
                : $"{requirement} is missing";
        }

        if (!found.Found.TryGetValue(requirement, out var location))
        {
            throw new ArgumentException($"'{requirement}' was not among the programs searched for.", nameof(found));
        }

        return location switch
        {
            // There: one held to a declared tool's minimum version is the copy a leg starts as well, and is turned
            // away by the minimum only for a leg it is declared for, when that leg is placed.
            { Present: true } or { HeldToFloor: true } => null,
            { Found: ProgramFound.Unreadable } => location.Unestablished(),
            _ => $"{requirement} is missing",
        };
    }

    private static bool Same(string first, string second) => string.Equals(first, second, StringComparison.OrdinalIgnoreCase);
}
