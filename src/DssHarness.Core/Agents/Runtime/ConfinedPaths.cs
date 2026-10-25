using DssHarness.Core.Agents.Policy;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Platform;

namespace DssHarness.Core.Agents.Runtime;

/// <summary>
/// Where a path a tool of the harness's runtime is given may lead, before the policy is asked of it: a spelling no file
/// system reads one way alone is refused - empty, a NUL, a <c>~</c> no shell expands here, and on Windows a share, a
/// device's prefix, a device's name, a drive's own directory (<c>C:x</c>) and an alternate data stream - and the rest is read against the agent's working
/// directory, its links followed, and held to the agent's roots: a read inside one of them, a write inside a writable
/// one and through no link below it. The one rule of where the runtime's tools reach; what the policy refuses inside
/// the roots is the policy's.
/// </summary>
/// <remarks>
/// Each root is compared as its own file system names it (<see cref="PathCase.In"/>), by its real path - the place a link
/// to it, or a link along the path asked, leads - so no spelling reaches past a root. Known limit, said in help: a link
/// put in place between this check and the tool's own act.
/// </remarks>
public sealed class ConfinedPaths
{
    /// <summary>The rule a path is refused by for its spelling.</summary>
    public const string SpellingRule = "the path's spelling";

    /// <summary>The rule a path is refused by for leading outside the agent's roots.</summary>
    public const string RootsRule = "the agent's roots";

    /// <summary>The rule a write is refused by for a link on its way below its root.</summary>
    public const string LinksRule = "the links on a write's way";

    /// <summary>The names Windows gives its devices, which a file of that name, with any extension, reaches in every directory.</summary>
    public static IReadOnlyList<string> DeviceNames { get; } =
        ["CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
         .. Enumerable.Range(0, 10).Select(digit => $"COM{digit}"), "COM¹", "COM²", "COM³",
         .. Enumerable.Range(0, 10).Select(digit => $"LPT{digit}"), "LPT¹", "LPT²", "LPT³"];

    private readonly IFileSystem _fileSystem;
    private readonly IHostPlatform _platform;
    private readonly string _workingDirectory;
    private readonly IReadOnlyList<Root> _roots;

    /// <summary>Reads the ground's roots once, each by its real path and as its file system compares names.</summary>
    /// <param name="fileSystem">Resolves the links, and says how each root compares names.</param>
    /// <param name="platform">The platform, whose spellings are refused.</param>
    /// <param name="ground">The agent's ground: its working directory and its roots.</param>
    public ConfinedPaths(IFileSystem fileSystem, IHostPlatform platform, AgentPolicyGround ground)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(platform);
        ArgumentNullException.ThrowIfNull(ground);

        _fileSystem = fileSystem;
        _platform = platform;
        _workingDirectory = Whole(ground.WorkingDirectory);

        var comparer = StringComparer.FromComparison(platform.PathComparison);
        var writable = ground.WritableRoots.Select(Whole).ToHashSet(comparer);

        _roots =
        [
            .. ground.Roots(platform.PathComparison).Select(Whole).Distinct(comparer).Select(root =>
            {
                var real = Whole(fileSystem.ResolveLinks(root));
                return new Root(root, real, ComparisonIn(real), writable.Contains(root));
            }),
        ];
    }

    /// <summary>
    /// The place <paramref name="path"/> names for a read - a file read, a directory listed or searched - as the tool acts
    /// on it: absolute, against the agent's working directory; or why it may not be reached.
    /// </summary>
    /// <param name="path">The path as the agent gave it.</param>
    public ConfinedPath Read(string path) => Confined(path, write: false);

    /// <summary>
    /// The place <paramref name="path"/> names for a write - a file written, made, deleted or moved - as the tool acts on
    /// it; or why it may not be: outside every writable root, or through a link below its root.
    /// </summary>
    /// <param name="path">The path as the agent gave it.</param>
    public ConfinedPath Write(string path) => Confined(path, write: true);

    /// <summary>Whether <paramref name="path"/>, absolute, is one of the agent's roots itself, as asked or as it resolves.</summary>
    /// <param name="path">The path, absolute.</param>
    public bool IsRoot(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        var whole = Whole(path);
        var real = Whole(_fileSystem.ResolveLinks(whole));

        return _roots.Any(root => PathContainment.AreSame(root.Given, whole, root.Comparison) || PathContainment.AreSame(root.Real, real, root.Comparison));
    }

    /// <summary>The path decided, read or write.</summary>
    private ConfinedPath Confined(string path, bool write)
    {
        ArgumentNullException.ThrowIfNull(path);

        if (Misspelled(path) is { } why)
        {
            return ConfinedPath.Refused(SpellingRule, $"'{path}' {why}");
        }

        try
        {
            var full = Whole(Path.GetFullPath(path, _workingDirectory));
            var real = Whole(_fileSystem.ResolveLinks(full));
            var roots = _roots.Where(root => !write || root.Writable).ToList();

            if (roots.Find(root => PathContainment.IsInsideOrSame(root.Real, real, root.Comparison)) is not { } within)
            {
                var named = string.Join(", ", roots.Select(root => root.Given));

                return ConfinedPath.Refused(
                    RootsRule,
                    real == full
                        ? $"'{full}' is outside the {(write ? "writable roots" : "roots")} of this agent: {named}"
                        : $"'{full}' leads to '{real}', outside the {(write ? "writable roots" : "roots")} of this agent: {named}");
            }

            if (write && ThroughALink(full, real, within) is { } reached)
            {
                return ConfinedPath.Refused(LinksRule, $"'{full}' leads to '{reached}' through a link: no tool writes through a link below its root");
            }

            return ConfinedPath.Of(full);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return ConfinedPath.Refused(SpellingRule, $"'{path}' names no place this tool can tell ({ex.Message.TrimEnd('.')})");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Spelled well, but its links could not be followed - a cycle, a directory this user may not search, a share
            // gone: where it leads is not known, and so not decided, as the policy calls what it cannot decide.
            return ConfinedPath.Refused(AgentPolicy.UndecidedRule, $"'{path}' could not be followed to where it leads: {ex.Message.TrimEnd('.')}");
        }
    }

    /// <summary>
    /// Where <paramref name="full"/> leads through a link below <paramref name="root"/> - itself one among them - by the one
    /// test of it (<see cref="LinkPaths.LeadsElsewhere"/>); null where no link lies on its way. A path that reaches its root
    /// only through a link above it is spelled by none of the root's names, and leads to <paramref name="real"/>.
    /// </summary>
    private string? ThroughALink(string full, string real, Root root)
    {
        var start = PathContainment.IsInsideOrSame(root.Given, full, root.Comparison) ? root.Given
            : PathContainment.IsInsideOrSame(root.Real, full, root.Comparison) ? root.Real
            : null;

        return start is null ? real
            : LinkPaths.LeadsElsewhere(_fileSystem, start, root.Real, Path.GetRelativePath(start, full), root.Comparison);
    }

    /// <summary>Why <paramref name="path"/>'s spelling is refused on this platform; null where it is not.</summary>
    private string? Misspelled(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "names no path";
        }

        if (path.Contains('\0', StringComparison.Ordinal))
        {
            return "holds a NUL, which no file's name holds";
        }

        if (path[0] == '~')
        {
            return "begins with ~, which no shell expands here: give the path within the agent's roots";
        }

        if (_platform.Current is not PlatformId.Windows)
        {
            return null;
        }

        if (PlatformPaths.NamesAShare(path))
        {
            return "names a share or a device by its prefix, which no tool of an agent reaches";
        }

        var drive = PlatformPaths.NamesADrive(path);

        if (path.IndexOf(':', drive ? 2 : 0) >= 0)
        {
            return "names an alternate data stream, or a drive where none stands: a ':' belongs only after a drive's letter";
        }

        if (drive && !PlatformPaths.IsWindowsAbsolute(path))
        {
            return "names a place relative to a drive's own directory, which the agent cannot see: give the drive's path whole";
        }

        foreach (var part in path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries))
        {
            var name = part.Split('.')[0].TrimEnd(' ');

            if (DeviceNames.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                return $"names the device '{name}', which Windows reaches by that name in every directory";
            }
        }

        return null;
    }

    /// <summary>How names compare under <paramref name="directory"/>, as its own file system says; the platform's where it is not there yet.</summary>
    /// <param name="directory">The directory, absolute.</param>
    internal StringComparison ComparisonIn(string directory)
        => !_fileSystem.DirectoryExists(directory) ? _platform.PathComparison
            : ReferenceEquals(PathCase.In(_fileSystem, directory), StringComparer.OrdinalIgnoreCase) ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    /// <summary>The path made absolute as the runtime makes it, its trailing separator gone.</summary>
    private static string Whole(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    /// <summary>One root: as given, where it resolves, how its names compare, and whether a write may fall in it.</summary>
    private sealed record Root(string Given, string Real, StringComparison Comparison, bool Writable);
}

/// <summary>Where a path given a tool leads, absolute, or why it may not be reached - one or the other.</summary>
public sealed record ConfinedPath
{
    private ConfinedPath(string? full, AgentPolicyAnswer.Denied? denied)
    {
        Full = full;
        Denied = denied;
    }

    /// <summary>The place, absolute, as the tool acts on it; null where it is refused.</summary>
    public string? Full { get; }

    /// <summary>Why it is refused; null where it is not.</summary>
    public AgentPolicyAnswer.Denied? Denied { get; }

    /// <summary>A place the tool may act on.</summary>
    internal static ConfinedPath Of(string full) => new(full, null);

    /// <summary>A place refused by <paramref name="rule"/>, for <paramref name="reason"/>.</summary>
    internal static ConfinedPath Refused(string rule, string reason) => new(null, new AgentPolicyAnswer.Denied(rule, AgentPolicySource.Builtin, reason));
}
