using DssHarness.Core.Configuration;

namespace DssHarness.Tests;

/// <summary>
/// A script a set-up's lines run in a test: after waiting as many seconds as its third argument says, if it says any, it
/// adds its first argument as a line to <c>setups.log</c> in the directory it runs in, and exits with its second, or
/// zero - a <c>.cmd</c> on Windows and a <c>.sh</c> elsewhere, each named by the line for its platform, as a repository
/// whose scripts differ by platform names them.
/// </summary>
public static class SetUpScripts
{
    /// <summary>The file each script adds its line to, in the directory it runs in.</summary>
    public const string LogName = "setups.log";

    /// <summary>Writes both scripts under <paramref name="root"/>'s <c>scripts</c>, the POSIX one executable.</summary>
    /// <param name="root">The tree the lines run in.</param>
    public static void Write(string root)
    {
        var scripts = Path.Combine(root, "scripts");

        Directory.CreateDirectory(scripts);
        File.WriteAllText(Path.Combine(scripts, "mark.cmd"), "@echo off\r\nif not \"%~3\"==\"\" ping -n %~3 127.0.0.1 >nul\r\n>>" + LogName + " echo %~1\r\nexit /b %2\r\n");

        var shell = Path.Combine(scripts, "mark.sh");

        File.WriteAllText(shell, "#!/bin/sh\n[ -n \"$3\" ] && sleep \"$3\"\necho \"$1\" >> " + LogName + "\nexit \"${2:-0}\"\n");

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(shell, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    /// <summary>The line that runs the script with <paramref name="arguments"/>, the script its platform's.</summary>
    /// <param name="arguments">What the line gives the script, as written.</param>
    public static SetUpLine Line(string arguments)
        => SetUpLine.Keyed([new("windows", $"scripts/mark.cmd {arguments}"), new("all", $"scripts/mark.sh {arguments}")]);

    /// <summary>
    /// A set-up named <paramref name="name"/> whose up line logs <c>up-&lt;name&gt;</c> and whose down line logs
    /// <c>down-&lt;name&gt;</c>, each exiting as asked.
    /// </summary>
    /// <param name="name">The set-up's name.</param>
    /// <param name="upExit">What its up line exits with.</param>
    /// <param name="downExit">What its down line exits with.</param>
    /// <param name="downSeconds">How long its down line waits before it logs, where it is to take a while.</param>
    /// <param name="upSeconds">How long its up line waits before it logs, where it is to take a while.</param>
    public static SetUpConfig SetUp(string name, int upExit = 0, int downExit = 0, int? downSeconds = null, int? upSeconds = null)
        => new() { Up = Line($"up-{name} {upExit} {upSeconds}".TrimEnd()), Down = Line($"down-{name} {downExit} {downSeconds}".TrimEnd()) };

    /// <summary>The lines the scripts logged in <paramref name="root"/>, in order; none where none ran.</summary>
    /// <param name="root">The directory they ran in.</param>
    public static IReadOnlyList<string> Logged(string root)
    {
        var log = Path.Combine(root, LogName);

        return File.Exists(log) ? [.. File.ReadAllLines(log).Select(line => line.Trim()).Where(line => line.Length > 0)] : [];
    }
}
