using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using DssHarness.Core.Platform;

namespace DssHarness.Tests;

/// <summary>
/// The permission checks that decide whether an ssh host is used, measured on real files. A stand-in
/// would only repeat what the check was expected to answer.
/// </summary>
public sealed class FilePermissionsTests
{
    [Theory]
    [InlineData(UnixFileMode.UserRead | UnixFileMode.UserWrite, true, false)]
    [InlineData(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead, false, false)]
    [InlineData(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupWrite, false, true)]
    [InlineData(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherWrite, false, true)]
    [UnsupportedOSPlatform("windows")]
    public void ModeBits_DecideWhetherAFileIsPrivate_AndWhetherOthersCanChangeIt(UnixFileMode mode, bool isPrivate, bool writableByOthers)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows has no mode bits; see the access rule test.");

        using var temp = new TempDirectory();
        var path = temp.WriteFile("file", "contents");
        File.SetUnixFileMode(path, mode);

        var permissions = FilePermissionsFactory.Create();

        Assert.Equal(isPrivate, permissions.IsPrivate(path));
        Assert.Equal(writableByOthers, permissions.IsWritableByOthers(path));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void AccessRules_DecideWhetherAFileIsPrivate_AndWhetherOthersCanChangeIt()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Linux and macOS use mode bits; see that test.");

        using var temp = new TempDirectory();
        var path = temp.WriteFile("key", "contents");
        var permissions = FilePermissionsFactory.Create();

        permissions.ProtectSecret(path);

        Assert.True(permissions.IsPrivate(path), "A protected file was not reported private.");
        Assert.False(permissions.IsWritableByOthers(path));

        Grant(path, FileSystemRights.ReadData);

        Assert.False(permissions.IsPrivate(path), "A file every user can read was reported private.");
        Assert.False(permissions.IsWritableByOthers(path));

        Grant(path, FileSystemRights.WriteData);

        Assert.True(permissions.IsWritableByOthers(path), "A file every user can change was not reported so.");
    }

    /// <summary>
    /// A file made one that can be run is so for whoever may read it, and for its owner whatever it held: no wider than
    /// the file already was. Made one that cannot, it is so for everyone; and nothing else of its mode moves.
    /// </summary>
    [Theory]
    [InlineData(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead, true,
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute)]
    [InlineData(UnixFileMode.UserRead | UnixFileMode.UserWrite, true, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute)]
    [InlineData(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite, true,
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute)]
    [InlineData(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute, false,
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead)]
    [InlineData(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead, false, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead)]
    [UnsupportedOSPlatform("windows")]
    public void ModeBits_AreSetForWhoeverMayRead_WhereAFileIsMadeOneThatCanBeRun_AndClearedForEveryoneWhereItIsNot(UnixFileMode mode, bool executable, UnixFileMode expected)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows has no mode bits.");

        using var temp = new TempDirectory();
        var path = temp.WriteFile("file", "contents");
        File.SetUnixFileMode(path, mode);

        var permissions = FilePermissionsFactory.Create();
        permissions.SetExecutable(path, executable);

        Assert.Equal(expected, File.GetUnixFileMode(path));
        Assert.Equal(executable, permissions.IsExecutable(path));
    }

    /// <summary>Windows keeps no such bit with a file: asked to set or clear one, nothing of the file changes.</summary>
    [Fact]
    public void OnWindows_MakingAFileOneThatCanBeRun_OrOneThatCannot_ChangesNothing()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Linux and macOS keep the bit; see that test.");

        using var temp = new TempDirectory();
        var path = temp.WriteFile("file.cmd", "contents");
        var attributes = File.GetAttributes(path);
        var permissions = FilePermissionsFactory.Create();

        permissions.SetExecutable(path, executable: false);
        permissions.SetExecutable(path, executable: true);

        Assert.Equal(attributes, File.GetAttributes(path));
        Assert.Equal("contents", File.ReadAllText(path));
        Assert.True(permissions.IsExecutable(path));
    }

    /// <summary>Allows <paramref name="rights"/> on <paramref name="path"/> to every user of the machine.</summary>
    [SupportedOSPlatform("windows")]
    internal static void Grant(string path, FileSystemRights rights)
    {
        var file = new FileInfo(path);
        var security = file.GetAccessControl();

        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            rights,
            AccessControlType.Allow));

        file.SetAccessControl(security);
    }
}
