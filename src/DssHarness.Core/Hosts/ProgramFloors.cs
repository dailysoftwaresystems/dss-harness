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
/// A directory raised for one tool can hold an older copy of another, which a leg would then start. So the
/// directories are raised in an order that keeps every tool a leg starts from one on a copy that meets its floor:
/// a directory goes up only once each tool it holds a copy of that meets nothing it is held to is started from a
/// directory raised ahead of it, and a tool whose first copy meets its floor has that copy's directory raised too
/// where that is what keeps another directory from starting an older copy of it. Where no directory holding a copy
/// that meets can go up so, the tools in the way are asked about their further copies, one at a time, for one that
/// can. A tool that no order settles is said so (see <see cref="ProgramFound.FloorShadowed"/>) rather than
/// started, its directory raised in the order the floors are declared, as a refusal that names both copies says
/// what to update. Nothing beyond what a leg would start is asked where nothing is in the way, and every directory
/// raised is the one some tool is started from.
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
    /// meets it. A ceiling for a copy that hangs, not a measure: a floor the host passes over asks nothing, most
    /// copies answer in a moment, and so do the further ones a host asks only where raising one tool's directory would
    /// start an older copy of another.
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
        var comparer = _platform.PathComparer();
        var tools = new List<Floored>();

        foreach (var floor in Applying(floors, names))
        {
            var copies = _programs.Copies(floor.Name, directories);

            if (copies.Copies.Count == 0)
            {
                // Found nowhere, or not looked for everywhere: the search below says which, as for any program.
                continue;
            }

            tools.Add(new Floored(floor, copies.Copies, await floor.SearchAsync(copies, ProbeAsync(floor), cancellationToken).ConfigureAwait(false), comparer));
        }

        var found = _programs.Resolve(names, directories, tools.Count == 0 ? [] : await RaisedAsync(tools, comparer, cancellationToken).ConfigureAwait(false));

        if (tools.Count == 0)
        {
            return found;
        }

        // Held to the floor as it is found with every raised directory ahead: the copy a leg starts.
        var located = new Dictionary<string, ProgramLocation>(found.Found, StringComparer.Ordinal);

        foreach (var tool in tools)
        {
            if (located[tool.Floor.Name] is { Present: true, Path: { } at } location)
            {
                var copy = await AskAsync(tool, at, cancellationToken).ConfigureAwait(false);
                var first = tool.Known(tool.Copies[0]);

                located[tool.Floor.Name] = Held(location, copy, tool.Search) with
                {
                    PassedOver = copy.Meets && !comparer.Equals(copy.Copy, tool.Copies[0]) ? first : null,
                };
            }
        }

        return found with { Found = located };
    }

    /// <summary>
    /// The directories to raise ahead of the PATH, in the order they go: one for each tool whose first copy is below its
    /// floor and another meets it, ordered so that none starts an older copy of a tool raised after it (see the remarks
    /// on <see cref="ProgramFloors"/>).
    /// </summary>
    /// <remarks>
    /// Built from the front, a directory at a time, by one rule: a directory may go up once every tool it holds a copy of
    /// that does not meet the tool's floor - and that has a copy that does - is started from one ahead of it already. A
    /// directory that may go up stays one that may as more go up ahead of it, so taking any such directory never stops
    /// another from going up later, and the order found settles every tool any order would.
    /// </remarks>
    private async Task<List<string>> RaisedAsync(List<Floored> tools, StringComparer comparer, CancellationToken cancellationToken)
    {
        var raised = new List<string>();

        // Only a tool whose first copy is below its floor needs a directory raised; the PATH starts the first copy as it is.
        var raising = tools.Where(tool => tool.Search.Chosen is { } chosen && !comparer.Equals(chosen.Copy, tool.Copies[0])).ToList();
        var wanted = new List<Floored>(raising);

        bool Covered(Floored tool) => raised.Any(directory => tool.In(directory) is not null);

        while (raising.Any(tool => !Covered(tool)))
        {
            if (await RaiseOneAsync() || InTheWay() || await AskFurtherAsync())
            {
                continue;
            }

            break;
        }

        // What no order settles: the directory of each tool's first copy that meets its floor goes up as declared, and a
        // tool one of them starts an older copy of is said to be shadowed.
        foreach (var tool in raising.Where(tool => !Covered(tool)))
        {
            Add(DirectoryOf(tool.Search.Chosen!));
        }

        return raised;

        // Raises the first directory, of a copy that meets the floor of a tool wanted and not yet started from one, that
        // may go up.
        async Task<bool> RaiseOneAsync()
        {
            foreach (var tool in wanted.Where(tool => !Covered(tool)))
            {
                foreach (var copy in tool.Meeting)
                {
                    if (await MayGoUpAsync(DirectoryOf(copy)).ConfigureAwait(false))
                    {
                        Add(DirectoryOf(copy));
                        return true;
                    }
                }
            }

            return false;
        }

        // Whether every tool not yet started from a raised directory, with a copy that meets its floor somewhere, is
        // started from one in this directory that meets it too, if it has a copy there.
        async Task<bool> MayGoUpAsync(string directory)
        {
            foreach (var tool in tools.Where(tool => tool.Matters && !Covered(tool)))
            {
                if (tool.In(directory) is { } copy && !(await AskAsync(tool, copy, cancellationToken).ConfigureAwait(false)).Meets)
                {
                    return false;
                }
            }

            return true;
        }

        // Wants each tool an older copy of which keeps a directory a wanted tool needs from going up: started from a
        // directory raised ahead of that one, it is in nobody's way.
        bool InTheWay()
        {
            var before = wanted.Count;

            foreach (var tool in wanted.Where(tool => !Covered(tool)).ToList())
            {
                foreach (var directory in tool.Meeting.Select(DirectoryOf))
                {
                    wanted.AddRange(tools.Where(other => other.Matters
                        && !Covered(other)
                        && !wanted.Contains(other)
                        && other.In(directory) is { } copy
                        && other.Known(copy) is { Meets: false }));
                }
            }

            wanted.Sort((left, right) => tools.IndexOf(left).CompareTo(tools.IndexOf(right)));
            return wanted.Count > before;
        }

        // Asks one further copy of the first wanted tool that has one left, for a copy that meets its floor elsewhere.
        async Task<bool> AskFurtherAsync()
        {
            foreach (var tool in wanted.Where(tool => !Covered(tool)))
            {
                if (tool.NextUnasked is { } copy)
                {
                    await AskAsync(tool, copy, cancellationToken).ConfigureAwait(false);
                    return true;
                }
            }

            return false;
        }

        void Add(string directory)
        {
            if (!raised.Contains(directory, comparer))
            {
                raised.Add(directory);
            }
        }

        static string DirectoryOf(FloorCopy copy) => Path.GetDirectoryName(copy.Copy)!;
    }

    /// <summary>What <paramref name="copy"/> of <paramref name="tool"/> says of its version, held to its floor: asked once, whatever asks.</summary>
    private async Task<FloorCopy> AskAsync(Floored tool, string copy, CancellationToken cancellationToken)
        => tool.Known(copy) ?? tool.Asked(await JudgeAsync(tool.Floor, copy, cancellationToken).ConfigureAwait(false));

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
    /// Finds each of <paramref name="programs"/> where a process started as <paramref name="search"/> says looks for it,
    /// each a floor names held to it there as <see cref="HoldAsync"/> holds it: the PATH a developer environment sets
    /// up, which no survey sees until it is set up, and the copy a leg there starts.
    /// </summary>
    /// <param name="search">Where such a process looks.</param>
    /// <param name="programs">The names, or paths, to find.</param>
    /// <param name="floors">The declared tools' minimum versions, as <see cref="HoldAsync"/> is given them.</param>
    /// <param name="cancellationToken">Stops the probes.</param>
    public Task<IReadOnlyDictionary<string, ProgramLocation>> HoldOnAsync(
        PathSearch search,
        IReadOnlyList<string> programs,
        IReadOnlyList<ToolFloor> floors,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(search);
        ArgumentNullException.ThrowIfNull(programs);

        return HoldAsync(
            programs.Distinct(StringComparer.Ordinal).ToDictionary(program => program, program => _programs.Find(search, program), StringComparer.Ordinal),
            floors,
            cancellationToken);
    }

    /// <summary>
    /// <paramref name="location"/> as a leg starts <paramref name="copy"/>, the copy it names: as it is where that copy
    /// meets the floor, and otherwise shadowed, below, or not known to meet it, as <paramref name="search"/> says.
    /// </summary>
    private static ProgramLocation Held(ProgramLocation location, FloorCopy copy, FloorSearch search)
        => (copy.Meets ? location
            : search.Chosen is not null ? location with { Found = ProgramFound.FloorShadowed, Reason = search.Shadowed(copy) }
            : search.Below ? location with { Found = ProgramFound.BelowFloor, Reason = search.Refusal() }
            : location with { Found = ProgramFound.FloorUnknown, Reason = search.Unestablished() }) with { Judged = copy };

    /// <summary>
    /// One declared tool held to its floor on this machine: every copy of it, in the order a search looks, and what each
    /// copy asked said - asked at most once, whichever question asked it.
    /// </summary>
    /// <param name="floor">Its floor.</param>
    /// <param name="copies">Every copy, in the order a search looks.</param>
    /// <param name="first">The search that asked the copies in order until one met the floor.</param>
    /// <param name="comparer">How this machine compares paths.</param>
    private sealed class Floored(ToolFloor floor, IReadOnlyList<string> copies, FloorSearch first, StringComparer comparer)
    {
        private readonly FloorSearch _first = first;
        private readonly StringComparer _comparer = comparer;
        private readonly Dictionary<string, FloorCopy> _asked = first.Probed.ToDictionary(copy => copy.Copy, comparer);

        public ToolFloor Floor { get; } = floor;

        public IReadOnlyList<string> Copies { get; } = copies;

        /// <summary>The copies asked, in the order a search looks: the first that meets the floor is the chosen one.</summary>
        public FloorSearch Search => _first with { Probed = [.. Copies.Where(_asked.ContainsKey).Select(copy => _asked[copy])] };

        /// <summary>
        /// Whether a copy meets the floor: the first search asked every copy until one did, so a tool none of whose copies
        /// it asked does is one none of whose copies does, and which copy a leg starts cannot settle.
        /// </summary>
        public bool Matters => _first.Chosen is not null;

        /// <summary>The copies asked that meet the floor, in the order a search looks.</summary>
        public IEnumerable<FloorCopy> Meeting => Search.Probed.Where(copy => copy.Meets);

        /// <summary>The first copy not yet asked, or <see langword="null"/> where every one was.</summary>
        public string? NextUnasked => Copies.FirstOrDefault(copy => !_asked.ContainsKey(copy));

        /// <summary>The copy in <paramref name="directory"/>, or <see langword="null"/> where it holds none.</summary>
        public string? In(string directory) => Copies.FirstOrDefault(copy => _comparer.Equals(Path.GetDirectoryName(copy), directory));

        /// <summary>What <paramref name="copy"/> said, where it was asked.</summary>
        public FloorCopy? Known(string copy) => _asked.GetValueOrDefault(copy);

        /// <summary>Keeps what one copy said.</summary>
        public FloorCopy Asked(FloorCopy copy) => _asked[copy.Copy] = copy;
    }

    /// <summary>The floors among <paramref name="floors"/> that apply on this machine to a program among <paramref name="names"/>.</summary>
    private IEnumerable<ToolFloor> Applying(IReadOnlyList<ToolFloor> floors, IReadOnlyList<string> names)
        => floors.Where(floor => floor.AppliesOn(_platform.PlatformKey) && names.Contains(floor.Name, StringComparer.Ordinal));

    /// <summary>What one copy of <paramref name="floor"/>'s tool says of its version, held to it.</summary>
    private async Task<FloorCopy> JudgeAsync(ToolFloor floor, string copy, CancellationToken cancellationToken)
        => floor.Judge(copy, await ProbeAsync(floor)(copy, cancellationToken).ConfigureAwait(false));

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
