using System.Text.Json.Serialization;
using DssHarness.Core.Platform;

namespace DssHarness.Core.Configuration;

/// <summary>
/// One file a build must produce for the build to be witnessed, named per platform where the
/// platforms disagree.
/// </summary>
/// <remarks>
/// <para>
/// The same target is not the same file everywhere: a program CMake calls <c>app</c> is
/// <c>app.exe</c> on Windows, and a static library differs by prefix as well as suffix
/// (<c>libfoo.a</c> against <c>foo.lib</c>). A single path is therefore wrong on one platform or
/// the other, which left a mixed leg set with no way to witness a build at all.
/// </para>
/// <para>
/// Written as a bare string it applies everywhere, which is what every existing configuration
/// means and why none of them has to change. Written as a map it is keyed by platform, or by
/// <c>all</c> for the general case: the shape <see cref="PerPlatformText"/> owns.
/// </para>
/// <para>
/// Adding a suffix automatically was the alternative and is not enough: it would have to guess
/// which entries name programs, and it cannot express a name that differs by more than its suffix.
/// </para>
/// </remarks>
[JsonConverter(typeof(BuildOutputConverter))]
public sealed class BuildOutput : PerPlatformText
{
    private BuildOutput(IEnumerable<KeyValuePair<string, string>> paths, string? plain)
        : base(paths, plain)
    {
    }

    /// <summary>An entry naming one path on every platform.</summary>
    /// <param name="path">The path, relative to the build directory.</param>
    /// <exception cref="ArgumentException">The path is blank; an entry naming nothing witnesses nothing.</exception>
    public static BuildOutput Everywhere(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        return new BuildOutput([new(PlatformScope.Every, path)], path);
    }

    /// <summary>An entry naming one path per platform.</summary>
    /// <param name="paths">The paths, keyed by platform name or <see cref="PlatformScope.Every"/>.</param>
    public static BuildOutput Keyed(IEnumerable<KeyValuePair<string, string>> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        return new BuildOutput(paths, plain: null);
    }

    /// <summary>A bare path, which applies on every platform.</summary>
    /// <remarks>
    /// A string is a legal entry in the file, so it is a legal entry in code: a configuration built
    /// in memory should read the same way as the one it round-trips to.
    /// </remarks>
    /// <param name="path">The path, relative to the build directory.</param>
    public static implicit operator BuildOutput(string path) => Everywhere(path);
}

/// <summary>
/// Reads a <see cref="BuildOutput"/> written either as a bare string or as a map of platform to
/// path, and writes it back in the shape it was read.
/// </summary>
public sealed class BuildOutputConverter : PerPlatformTextConverter<BuildOutput>
{
    /// <inheritdoc />
    protected override PerPlatformWords Words { get; } = new(
        "A buildOutputs entry",
        "path",
        "file",
        "an entry that witnesses nothing is the same as not declaring it, which is refused for the same reason.");

    /// <inheritdoc />
    protected override BuildOutput Everywhere(string text) => BuildOutput.Everywhere(text);

    /// <inheritdoc />
    protected override BuildOutput Keyed(Dictionary<string, string> values) => BuildOutput.Keyed(values);
}
