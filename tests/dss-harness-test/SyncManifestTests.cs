using System.Globalization;
using DssHarness.Core.Configuration;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Platform;
using DssHarness.Core.Sync;
using NSubstitute;

namespace DssHarness.Tests;

/// <summary>
/// A tree's manifest is read by one walk on a stack of its own, each directory once, however deep the tree is and however
/// many directories it holds - and the search for rooted sync.neverTransfer entries that match nothing is made as the
/// manifest is read, never by a walk of its own with a depth or a count it stops at.
/// </summary>
public sealed class SyncManifestTests
{
    /// <summary>How deep the trees these read go: past what a walk by a call for each directory holds on a small stack.</summary>
    private const int Deep = 5_000;

    /// <summary>
    /// A tree five thousand directories deep is in its manifest, the file at its bottom among it, read on a thread of a
    /// small stack, the settled reading's second look as well as its first. Walked by a call for each directory, a tree
    /// that deep overflowed the stack and ended the process.
    /// </summary>
    [Fact]
    public async Task ATreeThousandsDeep_IsInTheManifest()
        => Assert.Equal(
            new ChildRunAnswer(0, $"1 {Deep + 1}", string.Empty),
            await ChildRun.MethodAsync(typeof(SyncManifestTests), nameof(AManifestThousandsDeep), TestContext.Current.CancellationToken));

    /// <summary>How many files the manifest of a tree <see cref="Deep"/> directories deep holds, and how many parts its file's path has.</summary>
    internal static string AManifestThousandsDeep()
    {
        var tree = new VirtualTree();
        tree.File(tree.Chain("d", Deep), "deep.c", "int deep;");

        var manifest = SmallStack.Run(() => new ManifestBuilder(tree.FileSystem, new HostPlatform())
            .BuildSettledAsync(tree.Root, _ => false)
            .GetAwaiter()
            .GetResult());

        return string.Create(CultureInfo.InvariantCulture, $"{manifest.Entries.Count} {manifest.Entries.Keys.Single().Split('/').Length}");
    }

    /// <summary>
    /// The search for an absent name reads every directory the sync reads once - and those of the harness's own directory
    /// once besides - as the manifest is read: it finds a name five thousand directories down, past twenty-five thousand
    /// directories beside it, and the one in the harness's own directory the manifest withholds. Held to a depth of sixty
    /// four and to twenty thousand directories, it named neither, and said on every sync that it had stopped looking.
    /// </summary>
    [Fact]
    public async Task TheAbsentNameSearch_ReadsEveryDirectoryOnce()
        => Assert.Equal(
            new ChildRunAnswer(0, ".env,.secrets 1", string.Empty),
            await ChildRun.MethodAsync(typeof(SyncManifestTests), nameof(AnAbsentNameSearchOfAWideDeepTree), TestContext.Current.CancellationToken));

    /// <summary>
    /// The names the search finds in a tree <see cref="Deep"/> directories deep and twenty-five thousand wide, and the most
    /// times any directory of it was listed.
    /// </summary>
    internal static string AnAbsentNameSearchOfAWideDeepTree()
    {
        var tree = new VirtualTree();

        for (var wide = 0; wide < 25_000; wide++)
        {
            tree.Directory(tree.Root, string.Create(CultureInfo.InvariantCulture, $"s{wide:D5}"));
        }

        tree.File(tree.Chain("d", Deep), ".env", "KEY=value\n");
        tree.Directory(tree.Directory(tree.Directory(tree.Directory(tree.Root, ".harness-config"), "sshItems"), "vps"), ".secrets");

        var exclusions = new SyncExclusions(new SyncConfig { NeverTransfer = [".env", ".secrets"] }, worktreesRoot: ".worktrees");
        var search = exclusions.SearchRootedEntries(tree.FileSystem, tree.Root);

        _ = SmallStack.Run(() => new ManifestBuilder(tree.FileSystem, new HostPlatform())
            .BuildAsync(tree.Root, exclusions.IsWithheldFromTransfer, search)
            .GetAwaiter()
            .GetResult());

        var listed = tree.Listed.Values.Max();

        return $"{string.Join(",", search.Report().MatchingNothing)} {listed.ToString(CultureInfo.InvariantCulture)}";
    }

    /// <summary>
    /// A tree held in memory, answered through a file system it stands behind: as deep and as wide as a test makes it,
    /// with no path the disk refuses, and every listing of a directory counted.
    /// </summary>
    private sealed class VirtualTree
    {
        private readonly Dictionary<string, List<string>> _directories = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<string>> _files = new(StringComparer.Ordinal);
        private readonly Dictionary<string, byte[]> _contents = new(StringComparer.Ordinal);

        /// <summary>A tree holding nothing but its root, which no disk holds.</summary>
        public VirtualTree()
        {
            Root = Path.GetFullPath(Path.Combine(TestHost.TemporaryRoot, "virtual-" + Guid.NewGuid().ToString("N")));
            (_directories[Root], _files[Root]) = ([], []);

            var written = new DateTime(2026, 11, 19, 0, 0, 0, DateTimeKind.Utc);

            FileSystem.DirectoryExists(Arg.Any<string>()).Returns(call => _directories.ContainsKey(call.Arg<string>()));
            FileSystem.FileExists(Arg.Any<string>()).Returns(call => _contents.ContainsKey(call.Arg<string>()));
            FileSystem.ResolveLinks(Arg.Any<string>()).Returns(call => Path.GetFullPath(call.Arg<string>()));
            FileSystem.EnumerateFiles(Arg.Any<string>(), false).Returns(call => _files[call.Arg<string>()]);
            FileSystem.EnumerateDirectories(Arg.Any<string>()).Returns(call =>
            {
                var directory = call.Arg<string>();
                Listed[directory] = Listed.GetValueOrDefault(directory) + 1;
                return _directories[directory];
            });
            FileSystem.OpenRead(Arg.Any<string>()).Returns(call => new MemoryStream(_contents[call.Arg<string>()], writable: false));
            FileSystem.StandingOf(Arg.Any<string>()).Returns(call => new FileStanding(_contents[call.Arg<string>()].Length, written, written));
        }

        /// <summary>The tree's root.</summary>
        public string Root { get; }

        /// <summary>The file system the tree stands behind.</summary>
        public IFileSystem FileSystem { get; } = Substitute.For<IFileSystem>();

        /// <summary>How many times each directory was listed.</summary>
        public Dictionary<string, int> Listed { get; } = new(StringComparer.Ordinal);

        /// <summary>A directory <paramref name="name"/> in <paramref name="parent"/>, made.</summary>
        public string Directory(string parent, string name)
        {
            var path = Path.Combine(parent, name);

            (_directories[path], _files[path]) = ([], []);
            _directories[parent].Add(path);

            return path;
        }

        /// <summary>A chain of <paramref name="depth"/> directories named <paramref name="name"/>, one in the next, from the root; its deepest.</summary>
        public string Chain(string name, int depth)
        {
            var at = Root;

            for (var level = 0; level < depth; level++)
            {
                at = Directory(at, name);
            }

            return at;
        }

        /// <summary>A file <paramref name="name"/> in <paramref name="directory"/>, holding <paramref name="contents"/>.</summary>
        public void File(string directory, string name, string contents)
        {
            var path = Path.Combine(directory, name);

            _contents[path] = System.Text.Encoding.UTF8.GetBytes(contents);
            _files[directory].Add(path);
        }
    }
}
