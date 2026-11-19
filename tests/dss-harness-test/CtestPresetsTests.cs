using System.Text;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Platform;
using DssHarness.Core.Testing;
using NSubstitute;

namespace DssHarness.Tests;

/// <summary>
/// A test preset is read as ctest reads it - every preset it inherits, however far along, across every file the preset
/// files include - and each preset and each file is read once: a diamond of inherits, or of includes, is answered once
/// and its answer reused, never walked again for each way it is reached.
/// </summary>
public sealed class CtestPresetsTests
{
    /// <summary>
    /// Forty diamonds of inherits, one under the next, are read once each: the filter the deepest sets is found at once.
    /// Walked again for each way a preset is reached, the forty were two to the fortieth walks, and never ended.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task ADiamondOfPresets_IsReadOnce()
    {
        const int Diamonds = 40;
        using var temp = new TempDirectory();
        var presets = new List<string>();

        for (var level = 0; level < Diamonds; level++)
        {
            presets.Add($$"""{ "name": "d{{level}}", "inherits": ["l{{level}}", "r{{level}}"] }""");
            presets.Add($$"""{ "name": "l{{level}}", "inherits": "d{{level + 1}}" }""");
            presets.Add($$"""{ "name": "r{{level}}", "inherits": ["d{{level + 1}}"] }""");
        }

        presets.Add($$"""{ "name": "d{{Diamonds}}", "filter": { "include": { "name": "lex" }, "exclude": { "label": "" } } }""");
        temp.WriteFile("CMakePresets.json", $$"""{ "version": 6, "testPresets": [ {{string.Join(", ", presets)}} ] }""");

        var (filters, unread) = await Task.Run(() => CtestPresets.FiltersOf(new PhysicalFileSystem(FilePermissionsFactory.Create()), temp.Path, "d0"), TestContext.Current.CancellationToken);

        Assert.Null(unread);
        Assert.Equal(["include.name"], filters!);
    }

    /// <summary>
    /// A chain of a hundred thousand inherits is read whole, on a thread of a small stack: the walk keeps a stack of its
    /// own. Read by a call for each preset along it, a chain that long overflowed the stack and ended the process.
    /// </summary>
    [Fact]
    public async Task AChainOfAHundredThousandPresets_IsReadWhole()
        => Assert.Equal(
            new ChildRunAnswer(0, "exclude.name", string.Empty),
            await ChildRun.MethodAsync(typeof(CtestPresetsTests), nameof(ChainOfAHundredThousand), TestContext.Current.CancellationToken));

    /// <summary>The filters a preset at the head of a chain of a hundred thousand inherits sets, read on a small stack.</summary>
    internal static string ChainOfAHundredThousand()
    {
        const int Chain = 100_000;
        using var temp = new TempDirectory();
        var json = new StringBuilder("""{ "version": 6, "testPresets": [ """);

        for (var link = 0; link < Chain; link++)
        {
            json.Append($$"""{ "name": "p{{link}}", "inherits": "p{{link + 1}}" }, """);
        }

        json.Append($$"""{ "name": "p{{Chain}}", "filter": { "exclude": { "name": "slow" } } } ] }""");
        temp.WriteFile("CMakePresets.json", json.ToString());

        var (filters, unread) = SmallStack.Run(() => CtestPresets.FiltersOf(new PhysicalFileSystem(FilePermissionsFactory.Create()), temp.Path, "p0"));

        return unread ?? string.Join(",", filters!.Order(StringComparer.Ordinal));
    }
    /// <summary>
    /// A file two files include is read once, and a file that includes itself, through any chain, is read once and read
    /// no further: the include is ctest's to refuse, and nothing here walks it again.
    /// </summary>
    [Fact]
    public void AFileIncludedTwice_IsReadOnce()
    {
        var root = Path.GetFullPath(Path.Combine(TestHost.TemporaryRoot, "presets-" + Guid.NewGuid().ToString("N")));
        var files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [Path.Combine(root, "CMakePresets.json")] = """{ "version": 6, "include": ["a.json", "b.json"], "testPresets": [ { "name": "ci", "inherits": "shared" } ] }""",
            [Path.Combine(root, "a.json")] = """{ "version": 6, "include": ["shared.json"] }""",
            [Path.Combine(root, "b.json")] = """{ "version": 6, "include": ["shared.json", "CMakePresets.json"] }""",
            [Path.Combine(root, "shared.json")] = """{ "version": 6, "testPresets": [ { "name": "shared", "filter": { "include": { "label": "fast" } } } ] }""",
        };
        var reads = new Dictionary<string, int>(StringComparer.Ordinal);
        var fileSystem = Substitute.For<IFileSystem>();

        fileSystem.FileExists(Arg.Any<string>()).Returns(call => files.ContainsKey(call.Arg<string>()));
        fileSystem.ReadAllText(Arg.Any<string>()).Returns(call =>
        {
            var path = call.Arg<string>();
            reads[path] = reads.GetValueOrDefault(path) + 1;
            return files[path];
        });

        var (filters, unread) = CtestPresets.FiltersOf(fileSystem, root, "ci");

        Assert.Null(unread);
        Assert.Equal(["include.label"], filters!);
        Assert.All(reads, read => Assert.Equal(1, read.Value));
        Assert.Equal(4, reads.Count);
    }
}
