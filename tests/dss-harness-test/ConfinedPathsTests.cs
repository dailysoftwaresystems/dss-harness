using DssHarness.Core.Agents;
using DssHarness.Core.Agents.Policy;
using DssHarness.Core.Agents.Runtime;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Platform;

namespace DssHarness.Tests;

/// <summary>
/// Where a path a tool of the runtime is given may lead: a spelling no file system reads one way alone refused, the rest
/// read against the working directory, its links followed, held to the agent's roots - a write to its writable ones,
/// through no link below its root.
/// </summary>
public sealed class ConfinedPathsTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly string _worktree;
    private readonly string _main;
    private readonly string _outside;

    public ConfinedPathsTests()
    {
        // By their real paths, so a temporary root reached through a link (macOS's /var) says nothing of the links here.
        var fileSystem = new PhysicalFileSystem(FilePermissionsFactory.Create());
        _worktree = fileSystem.ResolveLinks(Directory.CreateDirectory(_temp.Combine("worktree")).FullName);
        _main = fileSystem.ResolveLinks(Directory.CreateDirectory(_temp.Combine("main")).FullName);
        _outside = fileSystem.ResolveLinks(Directory.CreateDirectory(_temp.Combine("outside")).FullName);
    }

    public void Dispose() => _temp.Dispose();

    /// <summary>A path is read against the working directory, and a read reaches any root, a write only a writable one.</summary>
    [Fact]
    public void APath_IsReadAgainstTheWorkingDirectory_AndHeldToTheRoots()
    {
        var paths = Paths();

        Assert.Equal(Path.Combine(_worktree, "src", "a.cs"), paths.Read(Path.Combine("src", "a.cs")).Full);
        Assert.Equal(Path.Combine(_main, "b.cs"), paths.Read(Path.Combine(_main, "b.cs")).Full);
        Assert.Equal(_worktree, paths.Read(".").Full);

        var outside = paths.Read(Path.Combine(_outside, "c.cs")).Denied;
        Assert.Equal(ConfinedPaths.RootsRule, outside?.Rule);
        Assert.StartsWith($"'{Path.Combine(_outside, "c.cs")}' is outside the roots of this agent: ", outside!.Reason, StringComparison.Ordinal);

        var parent = paths.Read(Path.Combine("..", "outside", "c.cs")).Denied;
        Assert.Equal(ConfinedPaths.RootsRule, parent?.Rule);

        Assert.Equal(Path.Combine(_worktree, "new.cs"), paths.Write("new.cs").Full);

        var readOnly = paths.Write(Path.Combine(_main, "b.cs")).Denied;
        Assert.Equal(ConfinedPaths.RootsRule, readOnly?.Rule);
        Assert.Contains("is outside the writable roots of this agent", readOnly!.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// A path whose links the file system cannot follow - a cycle, a directory this user may not search - is undecided, not
    /// misspelled; one too long to name is misspelled.
    /// </summary>
    [Fact]
    public void APathThatCannotBeFollowed_IsUndecided()
    {
        var paths = Paths(fileSystem: new Unfollowed(new PhysicalFileSystem(FilePermissionsFactory.Create())));

        var cycle = paths.Read("loop/x").Denied;
        Assert.Equal((AgentPolicy.UndecidedRule, "'loop/x' could not be followed to where it leads: Too many levels of symbolic links"), (cycle?.Rule, cycle?.Reason));
        Assert.Equal(AgentPolicy.UndecidedRule, paths.Write("locked/x").Denied?.Rule);
        Assert.Equal(ConfinedPaths.SpellingRule, paths.Read("long/x").Denied?.Rule);
    }

    /// <summary>A link inside a root that leads out of every root reaches nothing: a read of it is refused by where it leads.</summary>
    [Fact]
    public void ALinkThatLeadsOut_IsRefused_ByWhereItLeads()
    {
        TestLinks.DirectoryLink(Path.Combine(_worktree, "away"), _outside);

        var denied = Paths().Read(Path.Combine("away", "c.cs")).Denied;

        Assert.Equal(ConfinedPaths.RootsRule, denied?.Rule);
        Assert.Contains($"leads to '{Path.Combine(_outside, "c.cs")}', outside the roots of this agent", denied!.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// A file link inside a root that leads to a file out of every root - a tracked notes.md leading to a key under the home -
    /// reaches nothing: a read and a write of it are refused by where it leads. One that leads to a file inside a writable
    /// root is read, and written through by no tool.
    /// </summary>
    [Fact]
    public void AFileLink_IsRefused_ByWhereItLeads()
    {
        var key = Path.Combine(_outside, "id_rsa");
        var notes = Path.Combine(_worktree, "notes.md");
        var real = Path.Combine(_worktree, "real.md");
        var alias = Path.Combine(_worktree, "alias.md");
        File.WriteAllText(key, "secret");
        File.WriteAllText(real, "real");
        TestLinks.OrSkip(() =>
        {
            File.CreateSymbolicLink(notes, key);
            File.CreateSymbolicLink(alias, real);
        });

        var paths = Paths();
        var read = paths.Read("notes.md").Denied;
        var written = paths.Write("notes.md").Denied;
        var through = paths.Write("alias.md").Denied;

        Assert.Equal((ConfinedPaths.RootsRule, $"'{notes}' leads to '{key}', outside the roots of this agent: {_worktree}, {_main}"), (read?.Rule, read?.Reason));
        Assert.Equal((ConfinedPaths.RootsRule, $"'{notes}' leads to '{key}', outside the writable roots of this agent: {_worktree}"), (written?.Rule, written?.Reason));
        Assert.Equal(alias, paths.Read("alias.md").Full);
        Assert.Equal((ConfinedPaths.LinksRule, $"'{alias}' leads to '{real}' through a link: no tool writes through a link below its root"), (through?.Rule, through?.Reason));
    }

    /// <summary>A write through a link below its root is refused even where the link leads inside a writable root.</summary>
    [Fact]
    public void AWriteThroughALink_IsRefused_WhereverItLeads()
    {
        var target = Directory.CreateDirectory(Path.Combine(_worktree, "real")).FullName;
        TestLinks.DirectoryLink(Path.Combine(_worktree, "linked"), target);

        var paths = Paths();
        Assert.Equal(Path.Combine(_worktree, "linked", "x.cs"), paths.Read(Path.Combine("linked", "x.cs")).Full);

        var denied = paths.Write(Path.Combine("linked", "x.cs")).Denied;
        Assert.Equal(ConfinedPaths.LinksRule, denied?.Rule);
        Assert.Equal(
            $"'{Path.Combine(_worktree, "linked", "x.cs")}' leads to '{Path.Combine(target, "x.cs")}' through a link: no tool writes through a link below its root",
            denied!.Reason);

        var itself = paths.Write("linked").Denied;
        Assert.Equal($"'{Path.Combine(_worktree, "linked")}' leads to '{target}' through a link: no tool writes through a link below its root", itself?.Reason);
    }

    /// <summary>A path whose way goes on past what is there is decided up to what is there: a missing directory is no link.</summary>
    [Fact]
    public void AMissingWay_IsNoLink()
        => Assert.Equal(Path.Combine(_worktree, "a", "b", "c.cs"), Paths().Write(Path.Combine("a", "b", "c.cs")).Full);

    /// <summary>A spelling no file system reads one way alone is refused on every system: empty, a NUL, a leading ~.</summary>
    [Fact]
    public void ASpellingNoSystemReadsAlike_IsRefused()
    {
        var paths = Paths();

        foreach (var (path, why) in new[]
        {
            (string.Empty, "names no path"),
            ("  ", "names no path"),
            ("a\0b", "holds a NUL, which no file's name holds"),
            ("~", "begins with ~, which no shell expands here: give the path within the agent's roots"),
            ("~/x", "begins with ~, which no shell expands here: give the path within the agent's roots"),
            ("~user/x", "begins with ~, which no shell expands here: give the path within the agent's roots"),
        })
        {
            var denied = paths.Read(path).Denied;
            Assert.Equal(ConfinedPaths.SpellingRule, denied?.Rule);
            Assert.Equal($"'{path}' {why}", denied!.Reason);
            Assert.Equal(AgentPolicySource.Builtin, denied.Source);
        }
    }

    /// <summary>
    /// On Windows a share, a device's prefix, a drive's own directory, an alternate data stream and a device's name are
    /// refused, each by why; a name that only begins as a device's is a file's. WindowsSpellings_AreNamesElsewhere holds
    /// the other systems.
    /// </summary>
    [Fact]
    public void WindowsSpellings_AreRefusedOnWindows()
    {
        var windows = Paths(PlatformId.Windows);

        foreach (var (path, why) in new[]
        {
            (@"\\server\share\x", "names a share or a device by its prefix, which no tool of an agent reaches"),
            ("//server/share/x", "names a share or a device by its prefix, which no tool of an agent reaches"),
            (@"\\?\C:\x", "names a share or a device by its prefix, which no tool of an agent reaches"),
            ("C:x", "names a place relative to a drive's own directory, which the agent cannot see: give the drive's path whole"),
            ("a.txt:stream", "names an alternate data stream, or a drive where none stands: a ':' belongs only after a drive's letter"),
            (@"C:\x\a.txt:stream:$DATA", "names an alternate data stream, or a drive where none stands: a ':' belongs only after a drive's letter"),
            ("CON", "names the device 'CON', which Windows reaches by that name in every directory"),
            ("dir/nul.txt", "names the device 'nul', which Windows reaches by that name in every directory"),
            ("COM1.log", "names the device 'COM1', which Windows reaches by that name in every directory"),
            ("lpt9", "names the device 'lpt9', which Windows reaches by that name in every directory"),
            ("aux .txt", "names the device 'aux', which Windows reaches by that name in every directory"),
        })
        {
            var denied = windows.Read(path).Denied;
            Assert.Equal(ConfinedPaths.SpellingRule, denied?.Rule);
            Assert.Equal($"'{path}' {why}", denied!.Reason);
        }

        Assert.Null(windows.Read("console").Denied);
        Assert.Null(windows.Read("lpt10").Denied);
    }

    /// <summary>Off Windows a name Windows keeps for a device or a stream is a file's like any other.</summary>
    [Fact]
    public void WindowsSpellings_AreNamesElsewhere()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows reads these as its devices and streams: WindowsSpellings_AreRefusedOnWindows holds them there.");

        foreach (var name in new[] { "CON", "nul.txt", "COM1.log", "a.txt:stream", @"back\slash", "C:x" })
        {
            Assert.Equal(Path.Combine(_worktree, name), Paths().Read(name).Full);
        }
    }

    /// <summary>A root is told by its path as given and as it resolves; a path under it is no root.</summary>
    [Fact]
    public void ARoot_IsToldAsGivenAndAsItResolves()
    {
        TestLinks.DirectoryLink(Path.Combine(_outside, "to-main"), _main);
        var paths = Paths();

        Assert.True(paths.IsRoot(_worktree));
        Assert.True(paths.IsRoot(_main + Path.DirectorySeparatorChar));
        Assert.True(paths.IsRoot(Path.Combine(_outside, "to-main")));
        Assert.False(paths.IsRoot(Path.Combine(_worktree, "src")));
        Assert.False(paths.IsRoot(_outside));
    }

    /// <summary>A file system that cannot follow the links of three paths: one a cycle, one it may not search, one too long.</summary>
    private sealed class Unfollowed(IFileSystem inner) : PassThroughFileSystem(inner)
    {
        public override string ResolveLinks(string path)
        {
            // By a segment of the path, so no machine's own temporary directory is ever one of them.
            var segments = path.Split('/', '\\');

            return segments.Contains("loop") ? throw new IOException("Too many levels of symbolic links.")
                : segments.Contains("locked") ? throw new UnauthorizedAccessException("Access denied.")
                : segments.Contains("long") ? throw new PathTooLongException("The path is too long.")
                : base.ResolveLinks(path);
        }
    }

    private ConfinedPaths Paths(PlatformId? platform = null, IFileSystem? fileSystem = null)
        => new(
            fileSystem ?? new PhysicalFileSystem(FilePermissionsFactory.Create()),
            platform is { } id ? HostDoubles.Platform(id) : new HostPlatform(),
            PolicyGrounds.Of(_worktree, [_main], [_worktree], [], [], []));
}
