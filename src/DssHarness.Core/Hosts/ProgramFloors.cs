using DssHarness.Core.Platform;
using DssHarness.Core.Processes;
using DssHarness.Core.Tools;

namespace DssHarness.Core.Hosts;

/// <summary>
/// Finds programs on this machine the way a leg on it will start them, holding each declared tool that has a
/// minimum version to it: the first copy that meets it is the one a leg starts, its directory raised ahead of
/// the PATH where an older copy comes first; and a tool no copy of which is shown to meet it is said to be
/// below it, or not known to meet it, as the copies asked said.
/// </summary>
/// <remarks>
/// <para>
/// Run by the process that will run the leg, as <see cref="LocalProgramResolver"/> is, and with it: the survey
/// and the run cannot disagree about which copy a leg starts, and the run cannot start one the survey held to
/// nothing. Asked only of the tools the survey asks about, on the platforms they are declared for, and only for
/// as many copies as it takes: one where the first meets the floor, which is every host but one whose PATH
/// names an older copy first.
/// </para>
/// <para>
/// The directories are raised in the order the floors are declared, and a directory raised for one tool can hold
/// an older copy of another, which a leg would then start: that tool is said so (see
/// <see cref="ProgramFound.FloorShadowed"/>) rather than started, and no other order is tried. A host whose copies
/// fall so is rare, and a refusal that names both copies says what to update.
/// </para>
/// </remarks>
/// <param name="resolver">Finds programs, and every copy of one.</param>
/// <param name="processRunner">Starts a copy to ask its version.</param>
/// <param name="platform">This machine.</param>
public sealed class ProgramFloors(LocalProgramResolver resolver, IProcessRunner processRunner, IHostPlatform platform)
{
    private readonly LocalProgramResolver _programs = resolver;
    private readonly IProcessRunner _processRunner = processRunner;
    private readonly IHostPlatform _platform = platform;

    /// <summary>Longest one copy is given to say its version: a probe that prints a line, which a hung copy must not hold a survey on.</summary>
    public static readonly TimeSpan ProbeBudget = TimeSpan.FromSeconds(30);

    /// <summary>
    /// What a survey that carries <paramref name="floors"/> tools' floors allows for them, beyond the rest of its work:
    /// two copies of each, which is what a host asks where an older copy comes first - the first, and the one that
    /// meets it. A ceiling for a copy that hangs, not a measure: a floor the host passes over asks nothing, and most
    /// copies answer in a moment.
    /// </summary>
    /// <param name="floors">How many floors the survey carries.</param>
    public static TimeSpan Allowance(int floors) => ProbeBudget * 2 * floors;

    /// <summary>Finds each of <paramref name="programs"/>, holding the ones <paramref name="floors"/> names to their floor.</summary>
    /// <param name="programs">The names, or paths, to find.</param>
    /// <param name="directories">Where to look when the PATH does not name one; <c>~</c> is this machine's home.</param>
    /// <param name="floors">The declared tools' minimum versions; the ones for another platform, or a program not asked about, are passed over.</param>
    /// <param name="cancellationToken">Stops the probes.</param>
    public async Task<ProgramSearch> ResolveAsync(
        IEnumerable<string> programs,
        IReadOnlyList<string> directories,
        IReadOnlyList<ToolFloor> floors,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(programs);
        ArgumentNullException.ThrowIfNull(directories);
        ArgumentNullException.ThrowIfNull(floors);

        var names = programs.Where(program => !string.IsNullOrWhiteSpace(program)).Distinct(StringComparer.Ordinal).ToList();
        var searches = new Dictionary<string, FloorSearch>(StringComparer.Ordinal);
        var raised = new List<string>();
        var comparer = _platform.PathComparer();

        foreach (var floor in Applying(floors, names))
        {
            var copies = _programs.Copies(floor.Name, directories);

            if (copies.Copies.Count == 0)
            {
                // Found nowhere, or not looked for everywhere: the search below says which, as for any program.
                continue;
            }

            var search = await floor.SearchAsync(copies, ProbeAsync(floor), null, cancellationToken).ConfigureAwait(false);
            searches[floor.Name] = search;

            // The first copy is the one the PATH starts already; a later one has to be raised to be started.
            if (search.Chosen is { } chosen
                && !comparer.Equals(chosen.Copy, copies.Copies[0])
                && Path.GetDirectoryName(chosen.Copy) is { Length: > 0 } directory
                && !raised.Contains(directory, comparer))
            {
                raised.Add(directory);
            }
        }

        var found = _programs.Resolve(names, directories, raised);

        if (searches.Count == 0)
        {
            return found;
        }

        // Held to the floor as it is found with every raised directory ahead: a directory raised for one tool can
        // hold an older copy of another, which a leg would then start.
        var located = new Dictionary<string, ProgramLocation>(found.Found, StringComparer.Ordinal);

        foreach (var (name, search) in searches)
        {
            if (located[name] is not { Present: true, Path: { } at } location)
            {
                continue;
            }

            var copy = search.Probed.FirstOrDefault(probed => comparer.Equals(probed.Copy, at))
                ?? await JudgeAsync(search.Floor, at, cancellationToken).ConfigureAwait(false);

            located[name] = Held(location, copy, search.Probed.Contains(copy) ? search : search with { Probed = [.. search.Probed, copy] });
        }

        return found with { Found = located };
    }

    /// <summary>
    /// <paramref name="found"/>, with each program a floor names held to it where it was found: the one copy found is
    /// the one asked, as where a PATH a developer environment set up names a program, which is the copy a leg there
    /// starts and no other.
    /// </summary>
    /// <param name="found">Where each program was found, by the name it was asked for.</param>
    /// <param name="floors">The declared tools' minimum versions; the ones for another platform, or a program not asked about, are passed over.</param>
    /// <param name="cancellationToken">Stops the probes.</param>
    public async Task<IReadOnlyDictionary<string, ProgramLocation>> HoldAsync(
        IReadOnlyDictionary<string, ProgramLocation> found,
        IReadOnlyList<ToolFloor> floors,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(found);
        ArgumentNullException.ThrowIfNull(floors);

        var held = new Dictionary<string, ProgramLocation>(found, StringComparer.Ordinal);

        foreach (var floor in Applying(floors, [.. found.Keys]))
        {
            if (held[floor.Name] is { Present: true, Path: { } at } location)
            {
                var copy = await JudgeAsync(floor, at, cancellationToken).ConfigureAwait(false);
                held[floor.Name] = Held(location, copy, new FloorSearch(floor, [copy]));
            }
        }

        return held;
    }

    /// <summary>
    /// <paramref name="location"/> as a leg starts <paramref name="copy"/>, the copy it names: as it is where that copy
    /// meets the floor, and otherwise shadowed, below, or not known to meet it, as <paramref name="search"/> says.
    /// </summary>
    private static ProgramLocation Held(ProgramLocation location, FloorCopy copy, FloorSearch search)
        => copy.Meets ? location
            : search.Chosen is not null ? location with { Found = ProgramFound.FloorShadowed, Reason = search.Shadowed(copy) }
            : search.Below ? location with { Found = ProgramFound.BelowFloor, Reason = search.Refusal() }
            : location with { Found = ProgramFound.FloorUnknown, Reason = search.Unestablished() };

    /// <summary>The floors among <paramref name="floors"/> that apply on this machine to a program among <paramref name="names"/>.</summary>
    private IEnumerable<ToolFloor> Applying(IReadOnlyList<ToolFloor> floors, IReadOnlyList<string> names)
        => floors.Where(floor => floor.AppliesOn(_platform.PlatformKey) && names.Contains(floor.Name, StringComparer.Ordinal));

    /// <summary>What one copy of <paramref name="floor"/>'s tool says of its version, held to it.</summary>
    private async Task<FloorCopy> JudgeAsync(ToolFloor floor, string copy, CancellationToken cancellationToken)
        => floor.Judge(copy, await ProbeAsync(floor)(copy, cancellationToken).ConfigureAwait(false), null);

    /// <summary>Starts one copy of <paramref name="floor"/>'s tool, by its path, to ask its version.</summary>
    private Func<string, CancellationToken, Task<ProcessResult>> ProbeAsync(ToolFloor floor)
        => async (copy, cancellationToken) =>
        {
            try
            {
                return await _processRunner.RunAsync(
                    new ProcessRequest
                    {
                        FileName = copy,
                        Arguments = floor.Args,
                        Timeout = ProbeBudget,
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            catch (ProgramStartException ex)
            {
                return ProcessResult.NotRun(ex.Message);
            }
        };
}
