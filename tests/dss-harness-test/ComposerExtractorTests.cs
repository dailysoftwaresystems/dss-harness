using DssHarness.Core.Codebase;
using DssHarness.Core.Codebase.Languages;

namespace DssHarness.Tests;

/// <summary>A Composer package's composer.json, held to the contract's kit, and the rules of its own the kit does not name.</summary>
public sealed class ComposerExtractorTests : CodeGraphExtractorContractTests<ComposerExtractor>
{
    private const string App = """
        {
          "name": "acme/app",
          "description": "The application. It bills.",
          "type": "project",
          "repositories": [
            { "type": "path", "url": "packages/billing" }
          ],
          "require": {
            "php": "^8.2",
            "ext-json": "*",
            "acme/billing": "*",
            "laravel/framework": "^11.0"
          },
          "require-dev": {
            "phpunit/phpunit": "^11.0"
          }
        }
        """;

    private const string Billing = """
        {
          "name": "acme/billing",
          "require": { "php": ">=8.1", "lib-curl": "*", "composer-plugin-api": "^2.0" }
        }
        """;

    /// <inheritdoc/>
    public override IReadOnlyList<CodeGraphFixture> Fixtures { get; } =
    [
        new("composer.json", App)
        {
            Nodes = [new("composer:composer.json", CodeNodeKinds.Project, (1, 17))],
            Edges = [new("composer:composer.json", "composer:packages/billing/composer.json", CodeEdgeKinds.ProjectReference, 11, CodeConfidences.Unique)],
        },
        new("packages/billing/composer.json", Billing)
        {
            Nodes = [new("composer:packages/billing/composer.json", CodeNodeKinds.Project, (1, 4))],
        },
    ];

    /// <summary>
    /// A package is a project named by its name, its header the line that names it and its summary its description's first
    /// sentence; each package it requires - in require and require-dev - is a reference to that package by its name, and
    /// PHP's platform - php, an extension, a library, composer's own API - none; it declares no address root.
    /// </summary>
    [Fact]
    public void APackage_IsNamedByItsName_AndReferencesEachPackageItRequires_ThePlatformNone()
    {
        var extract = Read("composer.json", App);
        var node = Assert.Single(extract.Nodes);

        Assert.Equal(("acme/app", "\"name\": \"acme/app\",", "The application."), (node.Name, node.Signature, node.Summary));
        Assert.Equal([("acme/billing", 11), ("laravel/framework", 12), ("phpunit/phpunit", 15)], extract.References.Select(reference => (reference.Name, reference.Line)));
        Assert.All(extract.References, reference => Assert.Equal(CodeEdgeKinds.ProjectReference, reference.Kind));
        Assert.Empty(Read("packages/billing/composer.json", Billing).References);
        Assert.Empty(extract.AddressRoots);
        Assert.Empty(extract.Dynamic);

        var others = Read("composer.json", """
            { "name": "a/b", "replace": { "a/old": "*" }, "provide": { "x/p": "*" }, "conflict": { "x/c": "*" }, "suggest": { "x/s": "why" }, "autoload": { "psr-4": { "A\\": "src/" } } }
            """);

        Assert.Empty(others.References);

        var unnamed = Read("tools/composer.json", """{ "require": { "c/d": "*" }, "require-dev": [] }""");

        Assert.Equal(("composer.json", "composer.json"), (Assert.Single(unnamed.Nodes).Name, unnamed.Nodes[0].Signature));
        Assert.Equal(["c/d"], unnamed.References.Select(reference => reference.Name));
        Assert.Empty(unnamed.Dynamic);
    }

    /// <summary>A file that is no JSON object is a project named by its file, nothing in it read, the line it stops at marked dynamic.</summary>
    [Fact]
    public void AFileThatIsNoObject_IsAProjectOfItsFileName_NothingReadAndTheLineItStopsAtMarked()
    {
        foreach (var (text, line) in new[] { ("{\n  \"name\": \"a/b\",\n  \"require\": {\n    \"c/d\": 1,,\n", 4), ("[\"name\"]", 1), ("", 1) })
        {
            var extract = Read("composer.json", text);

            Assert.Equal("composer.json", Assert.Single(extract.Nodes).Name);
            Assert.Empty(extract.References);
            Assert.Equal(line, Assert.Single(extract.Dynamic).Line);
        }
    }

    private static FileExtract Read(string path, string text) => new ComposerExtractor().Extract(new CodeFileInput(path, text));
}
