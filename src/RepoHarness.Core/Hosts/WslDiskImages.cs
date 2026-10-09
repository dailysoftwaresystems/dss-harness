using System.Text.RegularExpressions;
using Microsoft.Win32;
using RepoHarness.Core.FileSystem;

namespace RepoHarness.Core.Hosts;

/// <summary>Where WSL keeps a distribution's disk on this machine.</summary>
public interface IWslDiskImages
{
    /// <summary>
    /// The directory on this machine holding <paramref name="distribution"/>'s disk, or <see langword="null"/>
    /// where WSL names none: this machine is not Windows, or no distribution of that name is registered.
    /// </summary>
    /// <param name="distribution">The distribution, by name, in whatever case.</param>
    string? DirectoryOf(string distribution);
}

/// <summary>Reads where WSL keeps each distribution's disk from what WSL registers for this user.</summary>
/// <remarks>
/// A WSL 2 distribution's filesystem is a virtual disk file that grows on the Windows drive holding it, and
/// what the distribution measures is the virtual disk's own room - a terabyte, by default - whatever that drive
/// has left. WSL registers each distribution under the user's <c>Lxss</c> key, its name as
/// <c>DistributionName</c> and the directory holding its disk as <c>BasePath</c>.
/// </remarks>
public sealed class WslDiskImages : IWslDiskImages
{
    private const string LxssKey = @"Software\Microsoft\Windows\CurrentVersion\Lxss";

    public string? DirectoryOf(string distribution)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(distribution);

        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        using var lxss = Registry.CurrentUser.OpenSubKey(LxssKey);

        foreach (var id in lxss?.GetSubKeyNames() ?? [])
        {
            using var entry = lxss!.OpenSubKey(id);

            if (string.Equals(entry?.GetValue("DistributionName") as string, distribution, StringComparison.OrdinalIgnoreCase)
                && entry?.GetValue("BasePath") is string { Length: > 0 } basePath)
            {
                return Plain(basePath);
            }
        }

        return null;
    }

    /// <summary><paramref name="basePath"/> without the <c>\\?\</c> WSL writes before some of them, which no drive lookup takes.</summary>
    /// <param name="basePath">A <c>BasePath</c> as WSL registered it.</param>
    public static string Plain(string basePath)
    {
        ArgumentNullException.ThrowIfNull(basePath);

        return basePath.StartsWith(@"\\?\", StringComparison.Ordinal) ? basePath[4..] : basePath;
    }
}

/// <summary>Where a WSL distribution reaches the drives of the Windows machine it runs on: each one's mount there.</summary>
/// <remarks>
/// WSL mounts each drive as a filesystem of its own - <c>C:\</c> at <c>/mnt/c</c>, by default - whose room is the drive's,
/// where the distribution's own root is its virtual disk's: measured, a terabyte where the drive had 477 GiB free.
/// </remarks>
public static partial class WindowsDriveMounts
{
    /// <summary>Where the system lists what is mounted, as <c>mount</c> reads it.</summary>
    public const string MountsFile = "/proc/mounts";

    /// <summary>
    /// Where <paramref name="drive"/> is mounted here - <c>/mnt/c</c> for <c>C:\</c> - or <see langword="null"/> where it is
    /// mounted nowhere, with why.
    /// </summary>
    /// <param name="fileSystem">Reads the mounts.</param>
    /// <param name="drive">The drive, as Windows names it: <c>C:\</c>.</param>
    public static (string? Mount, string? Why) Of(IFileSystem fileSystem, string drive)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentException.ThrowIfNullOrWhiteSpace(drive);

        string mounts;

        try
        {
            mounts = fileSystem.ReadAllText(MountsFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, $"what is mounted here could not be read from '{MountsFile}': {ex.Message.TrimEnd('.')}");
        }

        return In(mounts, drive) is { } mount ? (mount, null) : (null, $"'{MountsFile}' lists no mount of it");
    }

    /// <summary>
    /// Where <paramref name="drive"/> is mounted, by <paramref name="mounts"/> - the lines of <see cref="MountsFile"/>, each
    /// what is mounted, where, as what and how, a space or a backslash in any of them written as three octal digits after a
    /// backslash - or <see langword="null"/> where no line mounts it. A line names the drive as what it mounts, or, from an
    /// older WSL, which writes what it mounts as <c>drvfs</c>, by the <c>path=</c> its options give.
    /// </summary>
    /// <param name="mounts">What <see cref="MountsFile"/> holds.</param>
    /// <param name="drive">
    /// The drive, as Windows names it: <c>C:\</c>, in either case, with or without its backslash. Anything else is mounted
    /// nowhere: what else a line names as mounted - <c>none</c>, a device - is no drive.
    /// </param>
    public static string? In(string mounts, string drive)
    {
        ArgumentNullException.ThrowIfNull(mounts);
        ArgumentException.ThrowIfNullOrWhiteSpace(drive);

        var wanted = drive.TrimEnd('\\', '/');

        if (wanted is not [var letter, ':'] || !char.IsAsciiLetter(letter))
        {
            return null;
        }

        foreach (var line in mounts.Split('\n'))
        {
            var fields = line.Split(' ');

            if (fields.Length > 1
                && (Names(fields[0]) || (fields.Length > 3 && fields[3].Split(',', ';').Any(option => option.StartsWith("path=", StringComparison.Ordinal) && Names(option[5..])))))
            {
                return Unescaped(fields[1]);
            }
        }

        return null;

        bool Names(string field) => string.Equals(Unescaped(field).TrimEnd('\\', '/'), wanted, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary><paramref name="field"/> as it is, each three octal digits after a backslash read back into what they stand for.</summary>
    private static string Unescaped(string field)
        => Escaped().Replace(field, match => ((char)Convert.ToInt32(match.Groups[1].Value, 8)).ToString());

    [GeneratedRegex(@"\\([0-7]{3})", RegexOptions.CultureInvariant)]
    private static partial Regex Escaped();
}
