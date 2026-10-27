using System.Text.RegularExpressions;

namespace DssHarness.Core.Configuration;

/// <summary>The kinds of developer environment this build knows how to set up.</summary>
public static class DeveloperEnvironmentKinds
{
    /// <summary>
    /// A Visual Studio instance's own environment: the one <c>vcvarsall.bat</c> sets for the leg's
    /// processor, holding <c>cl</c>, the linker, the Windows SDK and their <c>INCLUDE</c> and
    /// <c>LIB</c>.
    /// </summary>
    public const string VisualStudio = "visualStudio";

    /// <summary>Every kind, in the order a refusal lists them.</summary>
    public static IReadOnlyList<string> All { get; } = [VisualStudio];
}

/// <summary>
/// An environment a toolchain's builds need set up on the host that runs them, before anything is
/// started there - declared once, and named by each toolchain that needs it.
/// </summary>
/// <remarks>
/// Set up by the harness on the machine doing the work, never written into the configuration. A
/// machine's own <c>INCLUDE</c>, <c>LIB</c> and <c>PATH</c> committed under a host's <c>env</c> would
/// hold every machine to one machine's Visual Studio and SDK, and break on the next update of either.
/// </remarks>
public sealed class DeveloperEnvironmentConfig
{
    /// <summary>The component Visual Studio's own installer lists for the C++ build tools on x86 and x64.</summary>
    public const string DefaultVisualStudioComponent = "Microsoft.VisualStudio.Component.VC.Tools.x86.x64";

    /// <summary>What sets it up: one of <see cref="DeveloperEnvironmentKinds.All"/>.</summary>
    public required string Kind { get; init; }

    /// <summary>
    /// The component an instance must have to be chosen, as Visual Studio's installer names it; the
    /// C++ build tools by default. The newest instance that has it - and the toolset
    /// <see cref="ToolsVersion"/> names, where it names one - is the one set up.
    /// </summary>
    public string RequiresComponent { get; init; } = DefaultVisualStudioComponent;

    /// <summary>
    /// The MSVC toolset its legs build with, named as an instance's <c>VC\Tools\MSVC</c> directory
    /// names its toolsets: by their first two numbers, for the newest of that line, or by all three,
    /// for that one (<see cref="VisualStudioToolsets"/>). Left out, the environment is the one
    /// <c>vcvarsall.bat</c> sets up when given no toolset: its instance's default.
    /// </summary>
    /// <remarks>
    /// Optional, and nothing in this build names a toolset of its own: which one a build needs is
    /// the repository's to say, so only its form is checked when the file is read. A compiler that
    /// accepts only some MSVC releases - a CUDA toolkit's <c>nvcc</c> refuses one newer than it
    /// knows - needs its legs held to an older toolset while the instance's default builds every
    /// other leg; two environments of one kind, one naming the toolset, keep both on one machine. The
    /// survey chooses the newest instance holding a toolset it names, and a setup that comes out with
    /// another is refused.
    /// </remarks>
    public string? ToolsVersion { get; init; }
}

/// <summary>
/// The MSVC toolsets of a Visual Studio instance, as its <c>VC\Tools\MSVC</c> directory names them -
/// three numbers joined by dots - and which of them a developer environment's
/// <see cref="DeveloperEnvironmentConfig.ToolsVersion"/> names: the one rule the configuration is
/// read by, the survey chooses an instance and a toolset by, and nothing else spells.
/// </summary>
/// <remarks>
/// Held to the names <c>VC\Tools\MSVC</c> gives and <c>VCToolsVersion</c> reports, rather than to every
/// form <c>vcvarsall.bat</c>'s <c>-vcvars_ver</c> accepts: it reads a version of four numbers as a
/// side-by-side component's, falls back with a warning to another toolset where that is missing, and
/// matches a shorter one anywhere inside a directory's name - so <c>14.4</c> takes <c>14.44.35207</c>.
/// A survey could not show an instance holds what those forms name, nor a setup be held to it.
/// </remarks>
internal static partial class VisualStudioToolsets
{
    /// <summary>
    /// Whether <paramref name="toolsVersion"/> can name a toolset: two numbers, naming the newest of
    /// their line, or three, naming one - each of ASCII digits, joined by dots.
    /// </summary>
    /// <param name="toolsVersion">What a developer environment declares.</param>
    public static bool IsToolsVersion(string toolsVersion) => ToolsVersionPattern().IsMatch(toolsVersion);

    /// <summary>Whether <paramref name="name"/> is a toolset's, as <c>VC\Tools\MSVC</c> names one: three numbers of ASCII digits, joined by dots.</summary>
    /// <param name="name">A directory's name.</param>
    public static bool IsToolset(string name) => ToolsetPattern().IsMatch(name);

    /// <summary>
    /// Whether <paramref name="toolset"/>, a directory under <c>VC\Tools\MSVC</c>, is one
    /// <paramref name="toolsVersion"/> names: a toolset's name, the same as it, or of its line - its
    /// numbers, then a dot.
    /// </summary>
    /// <param name="toolsVersion">What a developer environment declares.</param>
    /// <param name="toolset">The directory's name.</param>
    public static bool Names(string toolsVersion, string toolset)
        => IsToolset(toolset)
            && (string.Equals(toolset, toolsVersion, StringComparison.Ordinal)
                || toolset.StartsWith(toolsVersion + ".", StringComparison.Ordinal));

    /// <summary>
    /// The newest of <paramref name="toolsets"/> that <paramref name="toolsVersion"/> names, by their
    /// numbers, or <see langword="null"/> where it names none of them.
    /// </summary>
    /// <param name="toolsVersion">What a developer environment declares.</param>
    /// <param name="toolsets">The names of the directories under an instance's <c>VC\Tools\MSVC</c>.</param>
    public static string? Newest(string toolsVersion, IEnumerable<string> toolsets)
        => toolsets.Where(toolset => Names(toolsVersion, toolset)).OrderDescending(Order).FirstOrDefault();

    /// <summary>The names among <paramref name="directories"/> that are a toolset's, newest first.</summary>
    /// <param name="directories">The names of the directories under an instance's <c>VC\Tools\MSVC</c>.</param>
    public static IReadOnlyList<string> Of(IEnumerable<string> directories)
        => [.. directories.Where(IsToolset).OrderDescending(Order)];

    /// <summary>
    /// Toolsets by their numbers, oldest first: each number in turn, compared as a number rather than as
    /// text, so <c>14.44.9999</c> comes before <c>14.44.35207</c>.
    /// </summary>
    public static IComparer<string> Order { get; } = Comparer<string>.Create(Compare);

    private static int Compare(string? left, string? right)
    {
        var lefts = (left ?? string.Empty).Split('.');
        var rights = (right ?? string.Empty).Split('.');

        for (var index = 0; index < Math.Min(lefts.Length, rights.Length); index++)
        {
            // Digits alone, so a number is longer exactly when it is larger, its leading zeros set aside.
            var leftNumber = lefts[index].TrimStart('0');
            var rightNumber = rights[index].TrimStart('0');
            var order = leftNumber.Length != rightNumber.Length
                ? leftNumber.Length.CompareTo(rightNumber.Length)
                : string.CompareOrdinal(leftNumber, rightNumber);

            if (order != 0)
            {
                return order;
            }
        }

        return lefts.Length.CompareTo(rights.Length);
    }

    /// <summary>What a developer environment may declare: two or three numbers of ASCII digits, joined by dots.</summary>
    [GeneratedRegex(@"^[0-9]+\.[0-9]+(\.[0-9]+)?\z", RegexOptions.CultureInvariant)]
    private static partial Regex ToolsVersionPattern();

    /// <summary>A toolset's directory under <c>VC\Tools\MSVC</c>: three numbers of ASCII digits, joined by dots.</summary>
    [GeneratedRegex(@"^[0-9]+\.[0-9]+\.[0-9]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex ToolsetPattern();
}
