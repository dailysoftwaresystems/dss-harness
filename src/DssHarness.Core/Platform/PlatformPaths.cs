namespace DssHarness.Core.Platform;

/// <summary>
/// How a path reads on a platform, decided from its text alone.
/// </summary>
/// <remarks>
/// From the text rather than by asking this machine, so a configuration is judged the same wherever
/// it is read - including a path meant for a platform this machine is not. The machine that uses a
/// path reads the same text the same way, so the two cannot disagree about it.
/// </remarks>
public static class PlatformPaths
{
    /// <summary>
    /// How paths compare on <paramref name="platform"/>, for a collection keyed or ordered by them: case insensitive on
    /// Windows.
    /// </summary>
    /// <param name="platform">The platform.</param>
    public static StringComparer PathComparer(this IHostPlatform platform)
    {
        ArgumentNullException.ThrowIfNull(platform);

        return StringComparer.FromComparison(platform.PathComparison);
    }

    /// <summary>
    /// Whether <paramref name="path"/> is absolute on a <paramref name="platformKey"/> machine: a
    /// drive or a share on Windows, a leading <c>/</c> everywhere else.
    /// </summary>
    /// <param name="path">The path as a configuration spells it.</param>
    /// <param name="platformKey">The platform that reads it.</param>
    public static bool IsAbsoluteOn(string path, string? platformKey)
    {
        ArgumentNullException.ThrowIfNull(path);

        return string.Equals(platformKey, PlatformNames.Windows, StringComparison.OrdinalIgnoreCase)
            ? IsWindowsAbsolute(path)
            : path.StartsWith('/');
    }

    /// <summary>
    /// Whether <paramref name="path"/> is absolute on some platform: a leading <c>/</c>, or a drive
    /// or a share. What a setting read on any machine, for a machine it cannot tell, may be.
    /// </summary>
    /// <param name="path">The path as a configuration spells it.</param>
    public static bool IsAbsoluteOnAnyPlatform(string path)
        => IsAbsoluteOn(path, PlatformNames.Linux) || IsAbsoluteOn(path, PlatformNames.Windows);

    /// <summary>
    /// Whether <paramref name="path"/> is rooted on some platform: a leading separator of either kind,
    /// or a drive - <c>C:tools</c> among them, which Windows reads against that drive. What a setting
    /// that names a place inside the tree may never be, on whichever machine reads it.
    /// </summary>
    /// <param name="path">The path as a configuration spells it.</param>
    public static bool IsRootedOnAnyPlatform(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        return path.StartsWith('/') || path.StartsWith('\\') || NamesADrive(path);
    }

    /// <summary>
    /// Whether <paramref name="path"/> climbs out of the directory it is relative to: a <c>..</c> part, whichever slash
    /// separates it. The one test, which every setting and record naming a place inside a tree asks, on whichever machine
    /// reads it.
    /// </summary>
    /// <param name="path">The path as it is spelt.</param>
    public static bool ClimbsOut(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return path.Split('/', '\\').Any(part => part == "..");
    }

    /// <summary>
    /// Whether <paramref name="path"/> starts from the home directory of whoever reads it: <c>~/</c>,
    /// which each machine expands against its own.
    /// </summary>
    /// <param name="path">The path as a configuration spells it.</param>
    public static bool IsHomeRelative(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        return path.StartsWith("~/", StringComparison.Ordinal);
    }

    /// <summary>
    /// Whether <paramref name="path"/> is rooted at a drive - <c>C:\</c> or <c>C:/</c> - or names a
    /// share: what Windows reads as absolute.
    /// </summary>
    /// <param name="path">The path as a configuration spells it.</param>
    /// <remarks>
    /// <c>C:tools</c> is not: it is relative to whatever directory that drive was last in.
    /// </remarks>
    public static bool IsWindowsAbsolute(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        var drive = path.Length >= 3
            && char.IsAsciiLetter(path[0])
            && path[1] == ':'
            && path[2] is '\\' or '/';

        return drive || NamesAShare(path);
    }

    /// <summary>
    /// Whether <paramref name="path"/> begins with two separators, either kind - <c>\\server\share</c>, <c>//server</c>, a
    /// device's <c>\\?\</c> - which Windows reads as a share or a device, never a place on a drive.
    /// </summary>
    /// <param name="path">The text to read.</param>
    public static bool NamesAShare(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        return path.Length >= 2 && path[0] is '\\' or '/' && path[1] is '\\' or '/';
    }

    /// <summary>
    /// Whether <paramref name="path"/> starts with a drive letter, as <c>C:tools</c> and
    /// <c>C:\tools</c> both do. Windows reads either as naming a place on that drive.
    /// </summary>
    /// <param name="path">The text to read.</param>
    public static bool NamesADrive(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        return path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':';
    }

    /// <summary>
    /// Whether <paramref name="path"/>, read in the form its own text is written in, names a place strictly inside
    /// <paramref name="root"/>, read the same way: the answer every machine gives for the same two texts, whichever form
    /// either is written in and whichever machine reads them - so a path a tool wrote on one system and read on another
    /// is never placed by the reader's own rules.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A relative path is read against <paramref name="root"/>, inside it unless its parts climb out. A rooted one is
    /// inside only where it is of the root's own form - Windows' (a drive and a separator, or a share; either slash
    /// separates, and names are compared ignoring case, as Windows compares them) or the POSIX form (a leading <c>/</c>;
    /// <c>/</c> alone separates, and names are compared exactly) - and its parts, once <c>.</c> and <c>..</c> are
    /// resolved, begin with every part of the root's and go on. Two forms name no place in common: a Windows path is
    /// never inside a POSIX root, nor the other way.
    /// </para>
    /// <para>
    /// A <c>file:</c> URI is read as the path it names, its escapes decoded; a URI of any other scheme - <c>dart:core</c>,
    /// <c>node:internal/...</c>, <c>package:</c> - names no place on a disk, and is inside nothing. Where the place cannot
    /// be told from the text - a drive-relative <c>C:tools</c>, a root-relative <c>\src</c> with no drive - it is not
    /// inside: what cannot be told is never said to be.
    /// </para>
    /// </remarks>
    /// <param name="root">The directory, as the machine that holds it writes it.</param>
    /// <param name="path">The path, as whatever wrote it wrote it.</param>
    public static bool IsInsideAsWritten(string root, string path)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(path);

        if (AsAPath(path) is not { } named || named.Length == 0)
        {
            return false;
        }

        if (!IsRootedOnAnyPlatform(named))
        {
            return Resolved(named, windows: true, relative: true) is { Count: > 0 };
        }

        var form = FormOf(named);

        if (form is null || form != FormOf(root))
        {
            return false;
        }

        var windows = form == PathForm.Windows;

        if (Resolved(root, windows, relative: false) is not { } rootParts || Resolved(named, windows, relative: false) is not { } parts || parts.Count <= rootParts.Count)
        {
            return false;
        }

        var comparison = windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        return rootParts.Select((part, at) => string.Equals(part, parts[at], comparison)).All(same => same);
    }

    /// <summary>The two forms a rooted path is written in.</summary>
    private enum PathForm
    {
        /// <summary>A drive and a separator, or a share.</summary>
        Windows,

        /// <summary>A leading <c>/</c>.</summary>
        Posix,
    }

    /// <summary>
    /// The form a rooted path is written in, or <see langword="null"/> where its place cannot be told from its text: a
    /// drive with no separator after it, or a single backslash with no drive before it.
    /// </summary>
    private static PathForm? FormOf(string path)
        => IsWindowsAbsolute(path) ? PathForm.Windows
            : path.StartsWith('/') ? PathForm.Posix
            : null;

    /// <summary>
    /// The path <paramref name="text"/> names: itself where it is no URI, the path a <c>file:</c> URI names, or
    /// <see langword="null"/> for a URI of another scheme - a scheme being two or more of a URI scheme's characters before
    /// a colon that begins the text, so a drive's letter is never read as one.
    /// </summary>
    private static string? AsAPath(string text)
    {
        var colon = text.IndexOf(':', StringComparison.Ordinal);

        if (colon < 2 || !char.IsAsciiLetter(text[0]) || text.AsSpan(0, colon).ContainsAnyExcept(SchemeCharacters))
        {
            return text;
        }

        if (!text.AsSpan(0, colon).Equals("file", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // file:///C:/x and file:/C:/x name C:/x; file:///home/x and file:/home/x name /home/x; file://server/share/x names
        // the share.
        var named = Uri.UnescapeDataString(text[(colon + 1)..]);

        if (named.StartsWith("///", StringComparison.Ordinal))
        {
            named = named[2..];
        }

        return named.Length >= 3 && named[0] == '/' && NamesADrive(named[1..]) ? named[1..] : named;
    }

    /// <summary>The characters a URI's scheme is written in, past its first letter.</summary>
    private static readonly System.Buffers.SearchValues<char> SchemeCharacters =
        System.Buffers.SearchValues.Create("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789+-.");

    /// <summary>
    /// The parts of <paramref name="path"/>, <c>.</c> and empty ones dropped and each <c>..</c> taking the part before it
    /// away - at a rooted path's root, its drive or its share's server and share, as every form reads it, taking nothing -
    /// or <see langword="null"/> where a relative path's parts climb out of where it starts. Windows' form, and a relative
    /// path whose form its text cannot tell, are parted at either slash; the POSIX form at <c>/</c> alone.
    /// </summary>
    /// <param name="path">The path.</param>
    /// <param name="windows">Whether it is parted as Windows parts it.</param>
    /// <param name="relative">Whether it is relative to where it starts, which no <c>..</c> may climb out of.</param>
    private static List<string>? Resolved(string path, bool windows, bool relative)
    {
        var parts = new List<string>();
        var kept = !windows || relative ? 0 : NamesADrive(path) ? 1 : 2;

        foreach (var part in windows ? path.Split('/', '\\') : path.Split('/'))
        {
            if (part.Length == 0 || part == ".")
            {
                continue;
            }

            if (part != "..")
            {
                parts.Add(part);
            }
            else if (parts.Count > kept)
            {
                parts.RemoveAt(parts.Count - 1);
            }
            else if (relative)
            {
                return null;
            }
        }

        return parts;
    }
}
