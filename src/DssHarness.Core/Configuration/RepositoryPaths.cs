using DssHarness.Core.Platform;

namespace DssHarness.Core.Configuration;

/// <summary>
/// Where a host may keep its copy of the main checkout: the one rule a host's <c>repositoryPath</c> in config.json and the
/// <c>REPOSITORY_PATH</c> its item's .env sets over it are both held to.
/// </summary>
/// <remarks>
/// It is named absolutely, or from the home directory of the user the host is reached as: a relative path would resolve
/// against whatever directory a transport happens to start in. A home or root directory itself is refused, because the copy
/// is kept identical to the repository, and everything else in the directory would have to go.
/// </remarks>
public static class RepositoryPaths
{
    /// <summary>
    /// What is wrong with <paramref name="path"/> as where a host keeps its copy, completing a sentence that names it -
    /// <c>repositoryPath '~/..' has a '.' or '..' segment; name the directory directly</c> - or <see langword="null"/>
    /// where nothing is.
    /// </summary>
    /// <param name="path">The path, as it is written.</param>
    /// <param name="allowWindowsPaths">Whether the host may run Windows, so a drive or a share names a directory too: an ssh host's, never a WSL distribution's.</param>
    public static string? Problem(string path, bool allowWindowsPaths)
    {
        ArgumentNullException.ThrowIfNull(path);

        var trimmed = path.TrimEnd('/', '\\');
        var isHome = trimmed == "~";
        var isRoot = trimmed.Length == 0 || (trimmed.Length == 2 && PlatformPaths.NamesADrive(trimmed));

        // A POSIX path, or one from the home directory - or, where the host may run Windows, a drive
        // or a share.
        var isAbsolute = PlatformPaths.IsHomeRelative(path)
            || PlatformPaths.IsAbsoluteOn(path, PlatformNames.Linux)
            || (allowWindowsPaths && PlatformPaths.IsWindowsAbsolute(path));

        if (string.IsNullOrWhiteSpace(path) || (!isAbsolute && !isHome))
        {
            return "must be absolute, or start with ~/ for the home directory" + (allowWindowsPaths ? string.Empty : " inside the distribution");
        }

        // Compared as written, a path that steps back up, such as ~/src/.., could still name the home or root directory the
        // next check refuses.
        if (path.Split('/', '\\').Any(segment => segment is "." or ".."))
        {
            return "has a '.' or '..' segment; name the directory directly";
        }

        return isHome || isRoot ? "is a home or root directory; name a directory of its own for the copy" : null;
    }
}
