namespace DssHarness.Core.FileSystem;

/// <summary>How a file system compares the names of what it holds, as it answers for itself.</summary>
public static class PathCase
{
    /// <summary>
    /// How paths compare where <paramref name="directory"/> is: ignoring case where the file system
    /// finds that directory by its name in another case - Windows', and macOS's by default - and
    /// exactly where it does not.
    /// </summary>
    /// <remarks>
    /// Asked of the file system rather than decided by the operating system: macOS's default volume
    /// finds a file by its name in any case where one of its case-sensitive volumes does not, and a
    /// directory on Windows - or on Linux, folded - can be made case-sensitive, or not, apart from the
    /// folders above it. So the directory itself is asked, by a name it holds, in another case, that
    /// it does not hold so spelled as well; only where it holds no such name is its own path asked of
    /// the folder above it. A path with no letter whose case can change compares exactly.
    /// </remarks>
    /// <param name="fileSystem">The file system to ask.</param>
    /// <param name="directory">A directory that exists, whose file system is asked.</param>
    public static StringComparer In(IFileSystem fileSystem, string directory)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(directory);

        if (Asked(fileSystem, directory) is { } inside)
        {
            return inside ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        }

        return OtherCase(directory) is { } otherCase && fileSystem.DirectoryExists(otherCase)
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
    }

    /// <summary>
    /// Whether <paramref name="directory"/> finds a name it holds in another case, asked by one it holds whose other case
    /// it does not hold as well - a directory holding both spellings tells them apart; null where it holds no name to ask
    /// by, or cannot be listed.
    /// </summary>
    private static bool? Asked(IFileSystem fileSystem, string directory)
    {
        List<string> names;

        try
        {
            if (!fileSystem.DirectoryExists(directory))
            {
                return null;
            }

            names = [.. fileSystem.EnumerateDirectories(directory).Concat(fileSystem.EnumerateFiles(directory, recursive: false)).Select(Path.GetFileName).OfType<string>()];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var held = names.ToHashSet(StringComparer.Ordinal);

        foreach (var name in names)
        {
            if (OtherCase(name) is not { } other)
            {
                continue;
            }

            if (held.Contains(other))
            {
                return false;
            }

            return fileSystem.KindOf(Path.Combine(directory, other)) != PathKind.None;
        }

        return null;
    }

    /// <summary><paramref name="name"/> in upper case, or in lower case where it is all upper already; null where it has no letter whose case can change.</summary>
    internal static string? OtherCase(string name)
    {
        var other = name.ToUpperInvariant();

        if (other == name)
        {
            other = name.ToLowerInvariant();
        }

        return other == name ? null : other;
    }

    /// <summary>
    /// <paramref name="path"/> as the tree at <paramref name="root"/> spells it, part by part as each directory lists its
    /// names - the very spelling where the tree holds one so spelled, and otherwise, where its file system finds a name in
    /// any case, the one it holds in another case - or null where it holds nothing there.
    /// </summary>
    /// <param name="fileSystem">The file system to ask.</param>
    /// <param name="root">The tree's root, a directory that exists.</param>
    /// <param name="path">A path relative to it, with forward separators.</param>
    public static string? Spelled(IFileSystem fileSystem, string root, string path)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(path);

        var names = In(fileSystem, root);
        var parts = path.Split('/');
        var directory = root;

        for (var index = 0; index < parts.Length; index++)
        {
            if (!fileSystem.DirectoryExists(directory))
            {
                return null;
            }

            // What a directory along the path holds is a directory; the path itself may be anything.
            var held = fileSystem.EnumerateDirectories(directory);
            var listed = (index == parts.Length - 1 ? held.Concat(fileSystem.EnumerateFiles(directory, recursive: false)) : held).Select(Path.GetFileName).ToList();
            var name = listed.Find(name => string.Equals(name, parts[index], StringComparison.Ordinal)) ?? listed.Find(name => names.Equals(name, parts[index]));

            if (name is null)
            {
                return null;
            }

            parts[index] = name;
            directory = Path.Combine(directory, name);
        }

        return string.Join('/', parts);
    }
}
