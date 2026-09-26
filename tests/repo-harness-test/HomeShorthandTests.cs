using NSubstitute;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Platform;

namespace RepoHarness.Tests;

/// <summary>
/// How a host answering another machine writes its home: as <c>~</c>, wherever it stands as a whole path
/// segment, with the rest of the text as it was - so a path still works in a shell there, and names nobody.
/// </summary>
public sealed class HomeShorthandTests
{
    /// <summary>
    /// The home, and every path under it, from <c>~</c>: in a path alone, in a line naming several, and in a
    /// list of paths, as every place the harness names one on a host writes it.
    /// </summary>
    [Theory]
    [InlineData("/home/alice", "~")]
    [InlineData("/home/alice/src/repo.worktree-x/.harness-config/runs/20260926-101500-0a1b2c3d", "~/src/repo.worktree-x/.harness-config/runs/20260926-101500-0a1b2c3d")]
    [InlineData("logs of arm on ssh vps: /home/alice/src/repo/.harness-config/runs/1", "logs of arm on ssh vps: ~/src/repo/.harness-config/runs/1")]
    [InlineData("another run owns '/home/alice/src/repo/.harness-config/runs/1': pid 4", "another run owns '~/src/repo/.harness-config/runs/1': pid 4")]
    [InlineData("30 GiB free of 48 GiB on '/home/alice'", "30 GiB free of 48 GiB on '~'")]
    [InlineData("copied '/home/alice/a' to \"/home/alice/b\" (/home/alice)", "copied '~/a' to \"~/b\" (~)")]
    [InlineData("/usr/bin:/home/alice/.dotnet:/home/alice/bin", "/usr/bin:~/.dotnet:~/bin")]
    [InlineData("HOME=/home/alice", "HOME=~")]
    [InlineData("/home/alice/", "~/")]
    public void TheHome_IsWrittenAsTilde_WhereverItStandsWhole(string text, string shown)
        => Assert.Equal(shown, Posix("/home/alice").Shown(text));

    /// <summary>
    /// Only a whole path segment is the home. A name that merely starts like it, the home inside another path,
    /// and a path outside it are somewhere else, and are written as they are - a space and a backslash
    /// included, which a name holds wherever it is no separator.
    /// </summary>
    [Theory]
    [InlineData("/home/al", "/home/alice/x")]
    [InlineData("/home/alice", "/data/home/alice/x")]
    [InlineData("/home/alice", "x/home/alice/y")]
    [InlineData("/home/alice", "//home/alice/y")]
    [InlineData("/home/alice", "/home/alice.old/x")]
    [InlineData("/home/alice", "/home/alice-2/x")]
    [InlineData("/home/alice", "/home/alice_b")]
    [InlineData("/home/alice", "/home/alice smith/x")]
    [InlineData("/home/alice", @"/home/alice\x")]
    [InlineData("/home/alice", "/home/Alice/x")]
    [InlineData("/home/alice", "/var/tmp/x")]
    [InlineData("/home/alice", "~/src/repo")]
    public void SomewhereElse_IsWrittenAsItIs(string home, string text)
        => Assert.Equal(text, Posix(home).Shown(text));

    /// <summary>
    /// A Windows home is found spelt with either separator and in any case, as Windows reads a path, and the
    /// separator after it stays as it was written: nothing after the home is rewritten.
    /// </summary>
    [Theory]
    [InlineData(@"C:\Users\alice\src\repo", @"~\src\repo")]
    [InlineData("C:/Users/alice/src/repo", "~/src/repo")]
    [InlineData(@"c:\users\ALICE\src", @"~\src")]
    [InlineData(@"'C:\Users\alice'", "'~'")]
    [InlineData(@"C:\Users\alice smith\src", @"C:\Users\alice smith\src")]
    [InlineData(@"C:\Users\alice2\src", @"C:\Users\alice2\src")]
    [InlineData(@"D:\Users\alice\src", @"D:\Users\alice\src")]
    public void AWindowsHome_IsFoundWithEitherSeparatorAndInAnyCase(string text, string shown)
        => Assert.Equal(shown, HomeShorthand.For([@"C:\Users\alice\"], PlatformNames.Windows).Shown(text));

    /// <summary>
    /// A home spelt two ways - as the platform names it, and through the link it is reached by - is written as
    /// <c>~</c> either way, the longer spelling never cut short by the shorter.
    /// </summary>
    [Theory]
    [InlineData("/var/home/alice/src/repo", "~/src/repo")]
    [InlineData("/home/alice/src/repo", "~/src/repo")]
    [InlineData("/var/home/alice2/src", "/var/home/alice2/src")]
    public void AHomeSpeltTwoWays_IsWrittenAsTildeEitherWay(string text, string shown)
        => Assert.Equal(shown, HomeShorthand.For(["/home/alice", "/var/home/alice"], PlatformNames.Linux).Shown(text));

    /// <summary>
    /// Where one spelling of the home holds another, the longer is taken wherever it stands, whichever order the
    /// spellings came in: taken first, the shorter one leaves the rest of the longer behind the <c>~</c>.
    /// </summary>
    [Fact]
    public void ALongerSpelling_IsNeverCutShortByAShorterOne()
    {
        var home = HomeShorthand.For(["/srv", "/srv/alice"], PlatformNames.Linux);

        Assert.Equal("'~/x' and '~/bob/x'", home.Shown("'/srv/alice/x' and '/srv/bob/x'"));
    }

    /// <summary>
    /// A home that cannot be shortened writes everything as it is: none at all, a relative one, and a
    /// filesystem's root, which as <c>~</c> would rewrite every path there is.
    /// </summary>
    [Theory]
    [InlineData(null, PlatformNames.Linux)]
    [InlineData("", PlatformNames.Linux)]
    [InlineData("/", PlatformNames.Linux)]
    [InlineData("home/alice", PlatformNames.Linux)]
    [InlineData(@"C:\", PlatformNames.Windows)]
    [InlineData(@"C:\Users\alice", PlatformNames.Linux)]
    public void AHomeThatCannotBeShortened_WritesEverythingAsItIs(string? home, string platform)
    {
        const string Text = @"'/home/alice/x' and 'C:\Users\alice' and 'home/alice/x' and '/usr/bin'";

        Assert.Equal(Text, HomeShorthand.For([home], platform).Shown(Text));
        Assert.Equal(Text, HomeShorthand.None.Shown(Text));
    }

    /// <summary>
    /// This machine's home is taken as the platform names it and as its links resolve: a repository's paths
    /// come from git, which resolves every link, and a path built from the home itself does not.
    /// </summary>
    [Fact]
    public void ThisMachinesHome_IsTakenAsNamed_AndAsItsLinksResolve()
    {
        var platform = HostDoubles.Platform(PlatformId.Linux, home: "/home/alice");
        var fileSystem = Substitute.For<IFileSystem>();
        fileSystem.ResolveLinks("/home/alice").Returns("/var/home/alice");

        var home = HomeShorthand.Of(platform, fileSystem);

        Assert.Equal("'~/src' and '~/bin'", home.Shown("'/var/home/alice/src' and '/home/alice/bin'"));
    }

    /// <summary>A home whose links cannot be read is written as the platform names it.</summary>
    [Fact]
    public void AHomeWhoseLinksCannotBeRead_IsWrittenAsNamed()
    {
        var platform = HostDoubles.Platform(PlatformId.Linux, home: "/home/alice");
        var fileSystem = Substitute.For<IFileSystem>();
        fileSystem.ResolveLinks(Arg.Any<string>()).Returns(_ => throw new IOException("More than 40 links along '/home/alice'."));

        Assert.Equal("~/src", HomeShorthand.Of(platform, fileSystem).Shown("/home/alice/src"));
    }

    private static HomeShorthand Posix(string home) => HomeShorthand.For([home], PlatformNames.Linux);
}
