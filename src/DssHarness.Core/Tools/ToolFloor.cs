using DssHarness.Core.Configuration;
using DssHarness.Core.Hosts;
using DssHarness.Core.Platform;
using DssHarness.Core.Processes;

namespace DssHarness.Core.Tools;

/// <summary>
/// A declared tool's <c>minVersion</c>, as a host holds the copies of the tool it finds to it: what to run to
/// read a copy's version, how to read it, and on which platforms it applies. Sent to a host whole with a
/// survey, so the host decides for itself which copy its legs start.
/// </summary>
/// <remarks>
/// Without one, a copy is found by the tool's name alone - the first on the PATH, then the first in a searched
/// directory - and its version is nobody's business; with one, the first copy that meets it is the one, and a
/// host where every copy said an older version is said to be below it. A tool that declares no probe has no
/// version to read, and the configuration refuses a minVersion without one.
/// </remarks>
public sealed record ToolFloor
{
    /// <summary>The tool's name, as a program is started by it.</summary>
    public required string Name { get; init; }

    /// <summary>The lowest version any copy may be.</summary>
    public required string MinVersion { get; init; }

    /// <summary>The arguments that make a copy print its version.</summary>
    public List<string> Args { get; init; } = [];

    /// <summary>The regular expression whose first group is the version, or <see langword="null"/> for the last word of the first line.</summary>
    public string? Regex { get; init; }

    /// <summary>The platforms the tool is declared for, or <see langword="null"/> for every one.</summary>
    public List<string>? Platforms { get; init; }

    /// <summary>The floor of every tool among <paramref name="tools"/> that declares a minVersion and a probe to read it with.</summary>
    public static List<ToolFloor> Of(IEnumerable<ToolConfig> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);

        return [.. tools.Select(For).OfType<ToolFloor>()];
    }

    /// <summary>The floor <paramref name="tool"/> declares, or <see langword="null"/> where it declares no minVersion.</summary>
    /// <remarks>A minVersion with no probe is one the configuration refuses, and so is no floor here either.</remarks>
    public static ToolFloor? For(ToolConfig tool)
    {
        ArgumentNullException.ThrowIfNull(tool);

        return tool is { MinVersion: { Length: > 0 } minVersion, Probe: { } probe }
            ? new ToolFloor
            {
                Name = tool.Name,
                MinVersion = minVersion,
                Args = [.. probe.Args],
                Regex = probe.Regex,
                Platforms = tool.Platforms is { Count: > 0 } platforms ? [.. platforms] : null,
            }
            : null;
    }

    /// <summary>Whether this floor applies on the platform <paramref name="platformKey"/>.</summary>
    public bool AppliesOn(string? platformKey) => PlatformScope.Applies(Platforms, platformKey);

    /// <summary>
    /// Asks each of <paramref name="copies"/> its version, in order, until one meets this floor: the copies
    /// after it are never started.
    /// </summary>
    /// <param name="copies">Each copy, by its path, and where a copy could be that was not looked in.</param>
    /// <param name="probe">Starts one copy with <see cref="Args"/>.</param>
    /// <param name="cancellationToken">Stops the probes.</param>
    public async Task<FloorSearch> SearchAsync(
        ProgramCopies copies,
        Func<string, CancellationToken, Task<ProcessResult>> probe,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(copies);
        ArgumentNullException.ThrowIfNull(probe);

        var probed = new List<FloorCopy>();

        foreach (var copy in copies.Copies)
        {
            var judged = Judge(copy, await probe(copy, cancellationToken).ConfigureAwait(false));
            probed.Add(judged);

            if (judged.Meets)
            {
                break;
            }
        }

        return new FloorSearch(this, probed, copies.Unsearched);
    }

    /// <summary>What one copy said of its version, held to this floor, on the machine that started it.</summary>
    /// <param name="copy">The copy, as a reason names it.</param>
    /// <param name="result">What starting it with <see cref="Args"/> did.</param>
    public FloorCopy Judge(string copy, ProcessResult result)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(copy);
        ArgumentNullException.ThrowIfNull(result);

        var (version, problem) = ToolVersion.Of(Name, Regex, result, through: null);

        return problem is not null
            ? new FloorCopy(copy, version, problem)
            : ToolVersion.Meets(version, MinVersion) is { } meets
                ? new FloorCopy(copy, version, null, meets)
                : new FloorCopy(copy, version, ToolVersion.Uncomparable(version, MinVersion));
    }
}

/// <summary>One copy of a tool, and what asking it its version found.</summary>
/// <param name="Copy">The copy, as it was started.</param>
/// <param name="Version">The version it reported, where one was read.</param>
/// <param name="Problem">Why it could not be held to the floor, where it could not.</param>
/// <param name="Meets">Whether it is at least the floor.</param>
public sealed record FloorCopy(string Copy, string? Version, string? Problem, bool Meets = false)
{
    /// <summary>What it was found to be, as a reason names it.</summary>
    public string Said => Problem is { } problem ? $"{Copy}: {problem}" : $"{Copy} is {Version}";

    /// <summary>
    /// That this copy, which meets the floor, is the one a leg starts ahead of <paramref name="first"/>, the copy the
    /// PATH names first: older or unread, or kept from going first by a directory raised for another tool.
    /// </summary>
    /// <param name="first">The copy the PATH names first.</param>
    public string StartedAheadOf(FloorCopy first)
    {
        ArgumentNullException.ThrowIfNull(first);

        var passedOver = first.Problem is null ? $"{first.Copy} ({first.Version})" : $"{first.Copy}, whose version could not be read";

        return $"{Said}, which a leg there starts ahead of {passedOver}";
    }
}

/// <summary>The copies of one tool a host asked their version, in the order they were asked, and the one that meets its floor.</summary>
/// <param name="Floor">The floor.</param>
/// <param name="Probed">Every copy asked, the one that meets the floor last where one does.</param>
/// <param name="Unsearched">
/// Why a place a copy could be was not looked in, or <see langword="null"/> where every one was: a copy nobody saw
/// may be the one that meets the floor.
/// </param>
/// <remarks>
/// Three answers, kept apart as a program's search keeps "not there" from "could not look": a copy meets the floor;
/// every copy said an older version, which an install can settle; or neither is shown - a copy's version could not be
/// read, or a place could not be looked in - which nothing installed would settle, and no refusal may call a tool
/// below its floor over. Every sentence said about the floor is said here, so the survey and
/// <c>install-missing-tools</c> say each the same way.
/// </remarks>
public sealed record FloorSearch(ToolFloor Floor, IReadOnlyList<FloorCopy> Probed, string? Unsearched = null)
{
    /// <summary>The first copy that meets the floor, or <see langword="null"/> where none does.</summary>
    public FloorCopy? Chosen => Probed.FirstOrDefault(copy => copy.Meets);

    /// <summary>Whether every copy asked said a version below the floor, and every place a copy could be was looked in.</summary>
    public bool Below => Chosen is null && Probed.Count > 0 && Probed.All(copy => copy.Problem is null) && Unsearched is null;

    /// <summary>
    /// Why no copy will do, naming each one asked and what it said, where it is <see cref="Below"/>: the reason a leg
    /// that starts the tool is refused for, and <c>install-missing-tools</c> updates it over.
    /// </summary>
    public string Refusal()
        => $"no copy of '{Floor.Name}' there is at least {Floor.MinVersion}, the minVersion it is declared with: {Said()}";

    /// <summary>
    /// Why whether a copy meets the floor could not be established, naming each copy asked and what it said, and
    /// each place not looked in, where it is neither met nor <see cref="Below"/>.
    /// </summary>
    public string Unestablished()
        => $"whether a copy of '{Floor.Name}' there is at least {Floor.MinVersion}, the minVersion it is declared with, could "
            + $"not be established: {HostProbes.Joined(Said(), Unsearched)}";

    /// <summary>
    /// Why <paramref name="started"/> - the copy a leg would start, a directory raised for another tool holding it ahead
    /// of the <see cref="Chosen"/> one - will not do, and what to do about it.
    /// </summary>
    /// <param name="started">The copy a leg would start.</param>
    public string Shadowed(FloorCopy started)
    {
        ArgumentNullException.ThrowIfNull(started);

        var why = started.Problem is null
            ? $"{started.Said}, below the minVersion {Floor.MinVersion} '{Floor.Name}' is declared with"
            : $"{started.Said}, so it is not known to be at least the minVersion {Floor.MinVersion} '{Floor.Name}' is declared with";

        return $"{why}, and a directory raised ahead of the PATH for another tool puts it ahead of {Chosen?.Copy}, which is "
            + $"{Chosen?.Version}; update or remove it, so the copy that meets it is the one a leg starts";
    }


    private string Said() => string.Join("; ", Probed.Select(copy => copy.Said));
}
