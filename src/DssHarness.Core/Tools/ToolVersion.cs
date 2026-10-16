using System.Text.RegularExpressions;
using DssHarness.Core.Hosts;
using DssHarness.Core.Processes;

namespace DssHarness.Core.Tools;

/// <summary>
/// How a declared tool's version is read from what its probe printed, and held to its <c>minVersion</c>:
/// one reading for every place that asks - <c>install-missing-tools</c> on a host, and a host's own survey of
/// the copies a leg there would start.
/// </summary>
public static class ToolVersion
{
    /// <summary>Longest a probe's regex may take over what the probe printed.</summary>
    private static readonly TimeSpan PatternBudget = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The version a tool's probe reported, read the way the tool's configuration says to read it, or why none could
    /// be: the probe failed, or what it printed holds none.
    /// </summary>
    /// <param name="tool">The tool's name, as a reason names it.</param>
    /// <param name="regex">The probe's regex, or <see langword="null"/> for the last word of the first line.</param>
    /// <param name="result">What running the probe did.</param>
    /// <param name="through">
    /// The connection it ran over, whose host what it printed is quoted as declared, and whose failure to connect is
    /// said as that; none for this machine.
    /// </param>
    /// <returns>The version, and why it could not be read when it could not.</returns>
    public static (string? Version, string? Problem) Of(string tool, string? regex, ProcessResult result, HostConnection? through = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tool);
        ArgumentNullException.ThrowIfNull(result);

        return result.Succeeded
            ? Read(tool, regex, result, through)
            : (null, HostProbes.Failure($"'{tool}' would not report its version there", result, through));
    }

    /// <summary>Reads the version a probe that succeeded printed.</summary>
    private static (string? Version, string? Problem) Read(string tool, string? regex, ProcessResult result, HostConnection? through)
    {
        var text = result.StandardOutput.Length > 0 ? result.StandardOutput : result.StandardError;

        if (regex is not { Length: > 0 } pattern)
        {
            // With no pattern, the last word of the first line is the version a tool usually prints:
            // "ninja version 1.12.1", "git version 2.47.0".
            return (text.Split('\n').FirstOrDefault()?.Trim().Split(' ').LastOrDefault(), null);
        }

        try
        {
            var match = Regex.Match(text, pattern, RegexOptions.CultureInvariant, PatternBudget);

            return match.Success
                ? (match.Groups.Count > 1 ? match.Groups[1].Value : match.Value, null)
                : (null, $"the probe regex of '{tool}' matched nothing in what it printed: {HostProbes.Excerpt(HostProbes.AsConfigured(text, through))}");
        }
        catch (Exception ex) when (ex is ArgumentException or RegexMatchTimeoutException)
        {
            return (null, $"the probe regex of '{tool}' could not be used: {ex.Message}");
        }
    }

    /// <summary>
    /// Whether <paramref name="version"/> is at least <paramref name="minimum"/>: <see langword="null"/> where
    /// either cannot be compared, which <see cref="Uncomparable"/> says why.
    /// </summary>
    public static bool? Meets(string? version, string minimum)
        => SemanticVersion.TryParse(version, out var found) && SemanticVersion.TryParse(minimum, out var least)
            ? SemanticVersion.Compare(found, least) >= 0
            : null;

    /// <summary>
    /// Why a version and a minimum could not be compared, naming whichever of them failed to parse.
    /// </summary>
    /// <remarks>
    /// The two fail for unrelated reasons and have unrelated remedies: what the host reported is
    /// the probe's business, and the minimum is a string in this repository's own configuration.
    /// One message for both sent every reader of a two-component minVersion — which this parser
    /// refuses, because the field is a semantic version — to adjust a probe regex that had just
    /// worked perfectly.
    /// </remarks>
    /// <param name="version">What the probe read from the host, if anything.</param>
    /// <param name="minimum">The configured minVersion.</param>
    public static string Uncomparable(string? version, string? minimum)
    {
        var foundParses = SemanticVersion.TryParse(version, out _);
        var leastParses = SemanticVersion.TryParse(minimum, out _);

        return (foundParses, leastParses) switch
        {
            (true, false) =>
                $"the minVersion '{minimum}' is not a version this can compare; "
                + "write it as major.minor.patch, such as '1.2.0'",
            (false, true) =>
                $"'{version ?? string.Empty}' is not a version this can compare with the minVersion "
                + $"'{minimum}'; give the tool's probe a regex whose first group is the version",
            _ =>
                $"neither '{version ?? string.Empty}' nor the minVersion '{minimum}' is a version this "
                + "can compare; the minVersion is written as major.minor.patch, and the probe's regex "
                + "captures the version in its first group",
        };
    }
}
