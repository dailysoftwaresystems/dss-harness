using System.Diagnostics;

namespace RepoHarness.Tests;

/// <summary>The links tests make, each made one way.</summary>
internal static class TestLinks
{
    /// <summary>Makes <paramref name="link"/> a directory junction leading to <paramref name="target"/>: Windows only, where it needs no privilege.</summary>
    /// <param name="link">Where the junction goes.</param>
    /// <param name="target">The directory it leads to.</param>
    public static void Junction(string link, string target)
    {
        using var mklink = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
        {
            ArgumentList = { "/c", "mklink", "/J", link, target },
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;

        var error = mklink.StandardError.ReadToEnd();
        mklink.WaitForExit();

        Assert.True(mklink.ExitCode == 0, $"mklink /J failed: {error}");
    }

    /// <summary>
    /// Makes <paramref name="link"/> lead to the directory <paramref name="target"/>: a junction on Windows, which needs no
    /// privilege there, and a symbolic link elsewhere.
    /// </summary>
    /// <param name="link">Where the link goes.</param>
    /// <param name="target">The directory it leads to.</param>
    public static void DirectoryLink(string link, string target)
    {
        if (OperatingSystem.IsWindows())
        {
            Junction(link, target);
        }
        else
        {
            Directory.CreateSymbolicLink(link, target);
        }
    }

    /// <summary>
    /// Makes the links <paramref name="make"/> makes, or skips the test where this machine does not let this user make a
    /// symbolic link - Windows without developer mode.
    /// </summary>
    /// <param name="make">Makes them.</param>
    public static void OrSkip(Action make)
    {
        ArgumentNullException.ThrowIfNull(make);

        try
        {
            make();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Skip($"This machine does not allow creating symbolic links: {ex.Message}");
        }
    }

    /// <summary>Makes the links <paramref name="make"/> makes, where this machine lets this user; whether it did.</summary>
    /// <param name="make">Makes them.</param>
    public static bool Try(Action make)
    {
        ArgumentNullException.ThrowIfNull(make);

        try
        {
            make();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
