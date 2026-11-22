using System.Diagnostics;
using DssHarness.Core.Agents.Policy;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Platform;

namespace DssHarness.Tests;

/// <summary>
/// A place named by its Windows short name is the place its long name names: no refusal of it is passed by its other
/// spelling, for a path is spelled whole as the runtime spells a full path.
/// </summary>
public sealed class AgentPolicyShortNamesTests
{
    [Fact]
    public void OnWindows_ASlotNamedByItsShortName_IsStillTheSlot()
    {
        using var temp = new TempDirectory();
        var (longName, shortName) = ShortNamed(temp);
        var worktree = Directory.CreateDirectory(temp.Combine("tree")).FullName;
        var slot = Path.Combine(longName, "dssharness-slot-" + Guid.NewGuid().ToString("N"), ".env");
        var named = Path.Combine(shortName, Path.GetFileName(Path.GetDirectoryName(slot)!), ".env", "keys.env");

        var policy = new AgentPolicy(
            new PassThroughFileSystem(new PhysicalFileSystem(FilePermissionsFactory.Create())),
            HostDoubles.Platform(PlatformId.Windows),
            PolicyGrounds.Of(worktree, [worktree], [worktree], [slot], [], []));

        Assert.Equal("the credential slots", Assert.IsType<AgentPolicyAnswer.Denied>(policy.Read(named)).Rule);
        Assert.Equal("the credential slots", Assert.IsType<AgentPolicyAnswer.Denied>(policy.Run($"type \"{named}\"")).Rule);
    }

    /// <summary>
    /// A path of ten short-name components, each of which may shorten two guarded names, is read as every place it may
    /// reach - 3^10 of them, the path itself first - one at a time, as the pattern of the names each component may be.
    /// Red with the 64 caps (<c>MaxShortNameReadings</c>, <c>MaxDotReadings</c>), which denied it as undecided.
    /// </summary>
    [Fact]
    public void TenShortNameComponents_AreDecided()
    {
        var path = "/r" + string.Concat(Enumerable.Repeat("/GIT~1", 10)) + "/x";
        var readings = WindowsNames.Lengthened(path, [".git", ".github"]);

        Assert.Equal(path, readings.First());
        Assert.Equal(59_049, readings.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains("/r" + string.Concat(Enumerable.Repeat("/.git", 10)) + "/x", readings);
    }

    /// <summary>
    /// A directory Windows keeps a short name for, by both its names: one made here where the volume makes short names,
    /// else the program files directory, which keeps one where any does; the test is skipped where there is neither.
    /// </summary>
    private static (string Long, string Short) ShortNamed(TempDirectory temp)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows names a place by a short name.");

        foreach (var directory in new[] { Directory.CreateDirectory(temp.Combine("a-directory-named-long")).FullName, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) })
        {
            if (directory.Length > 0 && ShortOf(directory) is { } shortName && shortName.Contains('~', StringComparison.Ordinal))
            {
                return (directory, shortName);
            }
        }

        Assert.Skip("This machine keeps no short names.");
        return default;
    }

    /// <summary>The short name Windows keeps for <paramref name="path"/>, as cmd spells it; null where it cannot be asked.</summary>
    private static string? ShortOf(string path)
    {
        // cmd reads its command line as it is written, quotes and all: the line is handed whole, never quoted again.
        var start = new ProcessStartInfo("cmd.exe", $"/d /c for %I in (\"{path}\") do @echo %~sI")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };

        using var process = Process.Start(start);

        if (process is null)
        {
            return null;
        }

        var text = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();

        return process.ExitCode == 0 && text.Length > 0 ? text : null;
    }
}
