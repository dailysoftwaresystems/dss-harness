using DssHarness.Core.Codebase;
using DssHarness.Core.Codebase.Languages;

namespace DssHarness.Tests;

/// <summary>A Dart package's pubspec, held to the contract's kit, and the rules of its own the kit does not name.</summary>
public sealed class PubspecExtractorTests : CodeGraphExtractorContractTests<PubspecExtractor>
{
    private const string App = """
        name: app
        description: The app. It draws shapes.
        environment:
          sdk: ^3.5.0

        dependencies:
          shapes:
            path: ../shapes
          http: ^1.2.0
          tools:
            git: https://example.com/tools.git

        dev_dependencies:
          testing:
            path: ../../testing

        dependency_overrides:
          geometry:
            path: ../geometry
        """;

    private const string Shapes = """
        name: shapes
        dependencies:
          geometry: {path: ../geometry}
        """;

    private const string Geometry = """
        # A package of geometry.
        name: geometry
        """;

    private const string Testing = """
        name: testing
        """;

    /// <inheritdoc/>
    public override IReadOnlyList<CodeGraphFixture> Fixtures { get; } =
    [
        new("packages/app/pubspec.yaml", App)
        {
            Nodes = [new("pubspec:packages/app/pubspec.yaml", CodeNodeKinds.Project, (1, 19))],
            Edges =
            [
                new("pubspec:packages/app/pubspec.yaml", "pubspec:packages/shapes/pubspec.yaml", CodeEdgeKinds.ProjectReference, 7, CodeConfidences.Unique),
                new("pubspec:packages/app/pubspec.yaml", "pubspec:testing/pubspec.yaml", CodeEdgeKinds.ProjectReference, 14, CodeConfidences.Unique),
                new("pubspec:packages/app/pubspec.yaml", "pubspec:packages/geometry/pubspec.yaml", CodeEdgeKinds.ProjectReference, 18, CodeConfidences.Unique),
            ],
        },
        new("packages/shapes/pubspec.yaml", Shapes)
        {
            Nodes = [new("pubspec:packages/shapes/pubspec.yaml", CodeNodeKinds.Project, (1, 3))],
            Edges = [new("pubspec:packages/shapes/pubspec.yaml", "pubspec:packages/geometry/pubspec.yaml", CodeEdgeKinds.ProjectReference, 3, CodeConfidences.Unique)],
        },
        new("packages/geometry/pubspec.yaml", Geometry) { Nodes = [new("pubspec:packages/geometry/pubspec.yaml", CodeNodeKinds.Project, (1, 2))] },
        new("testing/pubspec.yaml", Testing) { Nodes = [new("pubspec:testing/pubspec.yaml", CodeNodeKinds.Project, (1, 1))] },
    ];

    /// <summary>
    /// A package is a project named by its name, its header the line that names it and its summary its description's first
    /// sentence; each package it depends on by a path - in its dependencies, its dev dependencies and its overrides - is a
    /// reference to that package by its name, and one from a host or from git is none; its address root is its lib/.
    /// </summary>
    [Fact]
    public void APackage_IsNamedByItsName_ReferencesWhatItDependsOnByAPath_AndRootsItsAddressesAtItsLib()
    {
        var extract = Read("packages/app/pubspec.yaml", App);
        var node = Assert.Single(extract.Nodes);

        Assert.Equal(("app", "name: app", "The app."), (node.Name, node.Signature, node.Summary));
        Assert.Equal(
            [("shapes", 7), ("testing", 14), ("geometry", 18)],
            extract.References.Select(reference => (reference.Name, reference.Line)));
        Assert.All(extract.References, reference => Assert.Equal(CodeEdgeKinds.ProjectReference, reference.Kind));
        Assert.Equal([new ExtractAddressRoot("package:app/", "lib")], extract.AddressRoots);
        Assert.Empty(extract.Dynamic);
    }

    /// <summary>
    /// A file that is no mapping of keys - one that does not parse, one that holds an alias, a list - is a project named
    /// by its file, nothing in it read, the line it stops at marked dynamic; one with no name declares no address root.
    /// </summary>
    [Fact]
    public void AFileThatIsNoMapping_IsAProjectOfItsFileName_NothingReadAndTheLineItStopsAtMarked()
    {
        var deep = "name: app\n" + string.Concat(Enumerable.Range(0, 40).Select(level => new string(' ', 2 * level) + $"k{level}:\n")) + new string(' ', 80) + "v: 1\n";

        foreach (var (text, line) in new[]
        {
            ("name: app\ndependencies: [\n", 2), ("base: &b\n  path: x\ndependencies:\n  a: *b\n", 4), ("- a\n- b\n", 1), ("", 1), ("name: a\n---\nname: b\n", 1), (deep, 34),
        })
        {
            var extract = Read("pubspec.yaml", text);

            Assert.Equal("pubspec.yaml", Assert.Single(extract.Nodes).Name);
            Assert.Empty(extract.References);
            Assert.Empty(extract.AddressRoots);
            Assert.Equal(line, Assert.Single(extract.Dynamic).Line);
        }

        var unnamed = Read("tool/pubspec.yaml", "description: no name\n");
        Assert.Empty(unnamed.AddressRoots);
        Assert.Empty(unnamed.Dynamic);
    }

    private static FileExtract Read(string path, string text) => new PubspecExtractor().Extract(new CodeFileInput(path, text));
}
