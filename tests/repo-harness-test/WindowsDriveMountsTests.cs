using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Hosts;

namespace RepoHarness.Tests;

/// <summary>Where a WSL distribution reaches the drives of the Windows machine it runs on.</summary>
public sealed class WindowsDriveMountsTests
{
    /// <summary>What a distribution lists as mounted, as one measured: the drive's line among the distribution's own.</summary>
    private const string Mounts =
        "none /mnt/wsl tmpfs rw,relatime 0 0\n"
        + "drivers /usr/lib/wsl/drivers 9p ro,nosuid,nodev,noatime,aname=drivers;fmask=222;dmask=222 0 0\n"
        + "C:\\134 /mnt/c 9p rw,noatime,aname=drvfs;path=C:\\;uid=1000;gid=1000;symlinkroot=/mnt/ 0 0\n"
        + "D:\\134 /mnt/data\\040drive 9p rw,noatime,aname=drvfs;path=D:\\ 0 0\n";

    /// <summary>
    /// A drive is found by the line that mounts it - named as Windows names it, in either case, with or without its
    /// backslash - and its mount read back as it is, a space in it written as the system writes one; one no line mounts is
    /// found nowhere.
    /// </summary>
    [Theory]
    [InlineData("C:\\", "/mnt/c")]
    [InlineData("c:", "/mnt/c")]
    [InlineData("D:\\", "/mnt/data drive")]
    [InlineData("E:\\", null)]
    [InlineData("none", null)]
    public void ADrive_IsFoundByTheLineThatMountsIt(string drive, string? mount)
        => Assert.Equal(mount, WindowsDriveMounts.In(Mounts, drive));

    /// <summary>What is mounted, read from where the system lists it; a list that cannot be read is said, with why.</summary>
    [Fact]
    public void TheMounts_AreReadWhereTheSystemListsThem_AndAListThatCannotBeReadIsSaid()
    {
        var harness = new HarnessFactory();

        Assert.Equal(("/mnt/c", null), WindowsDriveMounts.Of(new Listing(harness.FileSystem, () => Mounts), "C:\\"));
        Assert.Equal((null, "'/proc/mounts' lists no mount of it"), WindowsDriveMounts.Of(new Listing(harness.FileSystem, () => Mounts), "E:\\"));
        Assert.Equal(
            (null, "what is mounted here could not be read from '/proc/mounts': Access to the path is denied"),
            WindowsDriveMounts.Of(new Listing(harness.FileSystem, () => throw new UnauthorizedAccessException("Access to the path is denied.")), "C:\\"));
    }

    /// <summary>The real file system, except that what is mounted reads as <paramref name="mounts"/> answers.</summary>
    private sealed class Listing(IFileSystem inner, Func<string> mounts) : PassThroughFileSystem(inner)
    {
        public override string ReadAllText(string path)
            => path == WindowsDriveMounts.MountsFile ? mounts() : base.ReadAllText(path);
    }
}
