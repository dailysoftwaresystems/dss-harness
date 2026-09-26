using System.Text;
using RepoHarness.Core.FileSystem;

namespace RepoHarness.Core.Platform;

/// <summary>
/// A machine's home directory written as <c>~</c>, in what DssHarness writes for another machine to read.
/// </summary>
/// <remarks>
/// A host answering another machine writes into that machine's output: its terminal, its <c>--json</c>, and
/// every transcript and file that keeps them. A path under the host's home names the account the host was
/// reached as, which is the host's own business and not the run's. Written from <c>~</c> the path still
/// works in a shell there - bash, zsh and PowerShell all read a leading <c>~</c> as the home - and names
/// nobody. Only the home is replaced, and the rest is left as it was written, separators included: a path
/// inside a message has no end anybody can find, so nothing after the home can be rewritten safely.
/// <para>
/// The home is only ever a whole path segment. <c>/home/al</c> is not the start of <c>/home/alice/x</c>, and
/// <c>/home/alice</c> is not the home inside <c>/data/home/alice/x</c>: a separator, or a character that
/// continues a directory's name, just before the home or just after it means the text names somewhere else.
/// After it, a space continues the name too: a Windows profile is often named for a person, and
/// <c>C:\Users\alice smith</c> is not <c>C:\Users\alice</c>.
/// </para>
/// </remarks>
public sealed class HomeShorthand
{
    /// <summary>What continues a directory's name, besides a letter or a digit.</summary>
    private const string NameCharacters = "._-~@+$%#";

    private static readonly char[] WindowsSeparators = ['/', '\\'];

    private static readonly char[] Separators = ['/'];

    /// <summary>Every spelling of the home, the longest first, so a longer spelling is never cut short by a shorter one.</summary>
    private readonly IReadOnlyList<string> _homes;

    /// <summary>Whether the home is a Windows one: either separator, and case ignored.</summary>
    private readonly bool _windows;

    private HomeShorthand(IReadOnlyList<string> homes, bool windows)
    {
        _homes = homes;
        _windows = windows;
    }

    /// <summary>
    /// Writes every text as it is: for a command somebody typed on the machine it runs on, whose own paths
    /// are theirs to see.
    /// </summary>
    public static HomeShorthand None { get; } = new([], windows: false);

    /// <summary>
    /// This machine's home: as the platform names it, and with every link along it resolved.
    /// </summary>
    /// <param name="platform">The machine, whose home it is.</param>
    /// <param name="fileSystem">Resolves the links along the home.</param>
    /// <remarks>
    /// Both, because git names a repository by its path with every link resolved: on a machine whose home is
    /// reached through a link - <c>/home</c> linked to <c>/var/home</c> - every path under a repository is spelt
    /// the second way, and a path built from the home itself the first. A home whose links cannot be read is
    /// written as the platform names it.
    /// </remarks>
    public static HomeShorthand Of(IHostPlatform platform, IFileSystem fileSystem)
    {
        ArgumentNullException.ThrowIfNull(platform);
        ArgumentNullException.ThrowIfNull(fileSystem);

        var home = platform.HomeDirectory;
        var homes = new List<string> { home };

        if (!string.IsNullOrWhiteSpace(home))
        {
            try
            {
                homes.Add(fileSystem.ResolveLinks(home));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // Written as the platform names it: a link nobody may read is still where the home is.
            }
        }

        return For(homes, platform.PlatformKey);
    }

    /// <summary>
    /// <paramref name="homes"/>, each a spelling of one home directory, on a machine of
    /// <paramref name="platformKey"/>.
    /// </summary>
    /// <param name="homes">
    /// The home's spellings. One that is empty, is not absolute on that platform, or is a filesystem's root is
    /// left out: shortening it would rewrite text that names no home, or every path there is.
    /// </param>
    /// <param name="platformKey">
    /// The platform the home is on, which decides how its text is read (see <see cref="PlatformPaths"/>): on
    /// Windows either separator, and case ignored; elsewhere <c>/</c> alone, and case kept.
    /// </param>
    public static HomeShorthand For(IEnumerable<string?> homes, string? platformKey)
    {
        ArgumentNullException.ThrowIfNull(homes);

        var windows = string.Equals(platformKey, PlatformNames.Windows, StringComparison.OrdinalIgnoreCase);

        var usable = homes
            .OfType<string>()
            .Where(home => PlatformPaths.IsAbsoluteOn(home, platformKey))
            .Select(home => home.TrimEnd(windows ? WindowsSeparators : Separators))
            .Where(home => home.Length > 0 && !(windows && home.Length == 2 && PlatformPaths.NamesADrive(home)))
            .Distinct(windows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .OrderByDescending(home => home.Length)
            .ToList();

        return usable.Count == 0 ? None : new HomeShorthand(usable, windows);
    }

    /// <summary>
    /// <paramref name="text"/> with the home written as <c>~</c> wherever it stands as a whole path segment,
    /// and everything else as it was.
    /// </summary>
    /// <param name="text">A path, or a line naming any number of them.</param>
    public string Shown(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (_homes.Count == 0)
        {
            return text;
        }

        StringBuilder? shown = null;
        var copied = 0;

        for (var index = 0; index < text.Length; index++)
        {
            if (HomeAt(text, index) is not { } length)
            {
                continue;
            }

            shown ??= new StringBuilder(text.Length);
            shown.Append(text, copied, index - copied).Append('~');
            copied = index + length;
            index = copied - 1;
        }

        return shown is null ? text : shown.Append(text, copied, text.Length - copied).ToString();
    }

    /// <summary>
    /// The length of the home as <paramref name="text"/> spells it from <paramref name="index"/> on, where it
    /// stands there as a whole path segment; <see langword="null"/> where it does not.
    /// </summary>
    private int? HomeAt(string text, int index)
    {
        if (index > 0 && (IsSeparator(text[index - 1]) || ContinuesAName(text[index - 1])))
        {
            return null;
        }

        foreach (var home in _homes)
        {
            var end = index + home.Length;

            if (end <= text.Length
                && Spells(text, index, home)
                && (end == text.Length || IsSeparator(text[end]) || !(ContinuesAName(text[end]) || text[end] == ' ')))
            {
                return home.Length;
            }
        }

        return null;
    }

    /// <summary>Whether <paramref name="text"/> spells <paramref name="home"/> from <paramref name="index"/> on.</summary>
    private bool Spells(string text, int index, string home)
    {
        for (var offset = 0; offset < home.Length; offset++)
        {
            var written = text[index + offset];
            var expected = home[offset];

            var same = _windows
                ? (IsSeparator(written) && IsSeparator(expected)) || char.ToUpperInvariant(written) == char.ToUpperInvariant(expected)
                : written == expected;

            if (!same)
            {
                return false;
            }
        }

        return true;
    }

    private bool IsSeparator(char character) => character == '/' || (_windows && character == '\\');

    /// <summary>
    /// Whether <paramref name="character"/> can be part of the name of a directory it stands beside. A
    /// backslash is, where it is no separator: a file name elsewhere may hold one.
    /// </summary>
    private bool ContinuesAName(char character)
        => char.IsLetterOrDigit(character) || NameCharacters.Contains(character, StringComparison.Ordinal) || (!_windows && character == '\\');
}
