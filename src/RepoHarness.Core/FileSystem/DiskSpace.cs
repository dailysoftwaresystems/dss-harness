using System.Globalization;

namespace RepoHarness.Core.FileSystem;

/// <summary>The room on one filesystem.</summary>
/// <param name="FreeBytes">What the user asking can still write there.</param>
/// <param name="TotalBytes">How large the filesystem is.</param>
/// <param name="Filesystem">Where it is mounted - its drive on Windows - as a reader would look it up.</param>
public sealed record DiskSpace(long FreeBytes, long TotalBytes, string Filesystem)
{
    /// <summary>A gibibyte: the unit a configuration says room in.</summary>
    public const long Gibibyte = 1L << 30;

    /// <summary>The room on the filesystem <paramref name="path"/> is on, or why it could not be measured.</summary>
    /// <param name="fileSystem">Measures it.</param>
    /// <param name="path">A path on the filesystem.</param>
    public static (DiskSpace? Space, string? Unmeasured) Measure(IFileSystem fileSystem, string path)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);

        try
        {
            return (fileSystem.SpaceAt(path), null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, ex.Message.TrimEnd('.'));
        }
    }

    /// <summary>The room as it is said: <c>12.3 GiB free of 48 GiB on '/'</c>.</summary>
    public string Describe() => $"{Size(FreeBytes)} free of {Size(TotalBytes)} on '{Filesystem}'";

    /// <summary>
    /// A need of this room as every line about room says it: the need first, then what is free here, so neither is read
    /// as part of the other - <c>this leg needs ~31 GiB, as its buildSpaceGiB, 31, declares; 40 GiB free on '/'</c>. Said
    /// the other way about, <c>~4.8 GiB of 149.8 GiB free</c> read to a consumer as a disk all but full.
    /// </summary>
    /// <param name="needs">The need, as <see cref="Needs"/> says one: who needs how much, and why it is thought to.</param>
    /// <param name="where">What this filesystem is to the need, said after its name; empty where nothing more is.</param>
    /// <param name="beside">What else is counted on it, said last; empty where nothing is.</param>
    public string Against(string needs, string where = "", string beside = "")
        => $"{needs}; {Size(FreeBytes)} free on '{Filesystem}'{where}{beside}";

    /// <summary>A need of room as a line says it: <c>this leg needs ~31 GiB, as its buildSpaceGiB, 31, declares</c>.</summary>
    /// <param name="who">What needs it, as the line names it: <c>this leg</c>.</param>
    /// <param name="bytes">How much it needs.</param>
    /// <param name="source">What says it needs that much, where something is said.</param>
    public static string Needs(string who, long bytes, string? source = null)
        => source is null ? $"{who} needs ~{Size(bytes)}" : $"{who} needs ~{Size(bytes)}, {source}";

    /// <summary>
    /// <paramref name="bytes"/> in the largest binary unit it reaches, to one place: <c>12.3 GiB</c>,
    /// <c>512 MiB</c>, <c>3 bytes</c>.
    /// </summary>
    /// <param name="bytes">The size.</param>
    public static string Size(long bytes)
    {
        string[] units = ["KiB", "MiB", "GiB", "TiB"];

        if (bytes < 1024)
        {
            return bytes == 1 ? "1 byte" : string.Create(CultureInfo.InvariantCulture, $"{bytes} bytes");
        }

        var value = bytes / 1024.0;
        var unit = 0;

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return string.Create(CultureInfo.InvariantCulture, $"{value:0.#} {units[unit]}");
    }
}
