using DssHarness.Core.Codebase;
using DssHarness.Core.Codebase.Languages;

namespace DssHarness.Tests;

/// <summary>A JSON data file, held to the contract's kit, and the rules of its own the kit does not name.</summary>
public sealed class JsonExtractorTests : CodeGraphExtractorContractTests<JsonExtractor>
{
    private const string AppSettings = """
        {
          "$schema": "./schemas/appsettings.schema.json",
          "name": "billing",
          "targets": {
            "x64": {
              "callingConvention": "cdecl",
              "framework": "net9.0"
            }
          },
          "limits": [
            { "kind": "depth", "value": 8 },
            { "kind": "width", "value": 4 }
          ],
          "logging": {
            "level": "info"
          }
        }
        """;

    private const string Schema = """{ "type": "object" }""";

    private const string Comments = """
        // A file with comments and a trailing comma, as .jsonc writes them.
        {
          "theme": "dark", // the chosen one
          "tabs": 4,
        }
        """;

    private const string Json5 = """
        {
          quoted: "a name with no quotes",
        }
        """;

    /// <inheritdoc/>
    public override IReadOnlyList<CodeGraphFixture> Fixtures { get; } =
    [
        new("appsettings.json", AppSettings)
        {
            Nodes =
            [
                new("json:appsettings.json#name", CodeNodeKinds.Member, (3, 3)),
                new("json:appsettings.json#targets.x64.callingConvention", CodeNodeKinds.Member, (6, 6)),
                new("json:appsettings.json#limits[1].value", CodeNodeKinds.Member, (12, 12)),
                new("json:appsettings.json#targets", CodeNodeKinds.Section, (4, 9)),
                new("json:appsettings.json#logging", CodeNodeKinds.Section, (14, 17)),
            ],
            Edges = [new("file:appsettings.json", "file:schemas/appsettings.schema.json", CodeEdgeKinds.Imports, 2, CodeConfidences.Exact)],
        },
        new("schemas/appsettings.schema.json", Schema),
        new("settings.jsonc", Comments)
        {
            Nodes = [new("json:settings.jsonc#theme", CodeNodeKinds.Member, (3, 3))],
        },
        new("quoted.json5", Json5)
        {
            Nodes = [],
        },
    ];

    /// <summary>
    /// Each scalar key anywhere in the file is a key node named by its path - an array item's path holding its place -
    /// with its value as its summary; each top-level member that holds others is a section over the lines to the next
    /// one's; and a <c>$schema</c> or an external <c>$ref</c> naming a file by a relative path is an import of it.
    /// </summary>
    [Fact]
    public void EachScalarKeyIsAKeyNodeNamedByItsPath_AndEachTopLevelHolderASection()
    {
        var extract = Read("appsettings.json", AppSettings);

        Assert.Equal(
            [
                ("targets", null, 4, 9),
                ("limits", null, 10, 13),
                ("logging", null, 14, 17),
            ],
            extract.Nodes.Where(node => node.Kind == CodeNodeKinds.Section).Select(node => (node.Name, node.SubKind, node.Start, node.End)));

        var key = Assert.Single(extract.Nodes, node => node.Id == "json:appsettings.json#targets.x64.callingConvention");

        Assert.Equal(("callingConvention", "targets.x64.callingConvention", "cdecl"), (key.Name, key.Qualified, key.Summary));
        Assert.Equal("json:appsettings.json#targets", key.Parent);

        var inner = Assert.Single(extract.Nodes, node => node.Id == "json:appsettings.json#limits[1].value");

        Assert.Equal(("value", "4"), (inner.Name, inner.Summary));
        Assert.Equal([("appsettings.schema.json", 2)], extract.Imports.Select(import => (CodeGraphIds.FileName(import.Target), import.Line)));
        Assert.All(extract.Imports, import => Assert.Equal(CodeImportForms.Relative, import.Form));
        Assert.Empty(extract.Dynamic);
        Assert.Equal(0, extract.KeysBeyond);
    }

    /// <summary>A local <c>$ref</c> pointer names no file, and neither does a URL: neither is an import, but each is a key like any other.</summary>
    [Fact]
    public void ALocalRefPointerAndAUrl_NameNoFileAndMakeNoImport()
    {
        var extract = Read("defs.json", """{ "$ref": "#/definitions/x", "remote": { "$ref": "https://example.com/other.json" } }""");

        Assert.Empty(extract.Imports);
        Assert.Equal([("$ref", "#/definitions/x"), ("$ref", "https://example.com/other.json")],
            extract.Nodes.Where(node => node.Kind == CodeNodeKinds.Member).OrderBy(node => node.Id).Select(node => (node.Name, node.Summary)));
    }

    /// <summary>Where the file stops reading as JSON - a JSON5 name with no quotes among them - nothing in it is read, and the line it stops at is marked dynamic.</summary>
    [Fact]
    public void WhereTheFileStopsReading_NothingIsRead_AndTheLineIsMarkedDynamic()
    {
        foreach (var (text, line) in new[] { ("{ \"a\": 1,, }", 1), ("[1, 2", 1), ("", 1) })
        {
            var extract = Read("broken.json", text);

            Assert.Empty(extract.Nodes);
            Assert.Equal(line, Assert.Single(extract.Dynamic).Line);
        }

        var unreadable = Read("quoted.json5", Json5);

        Assert.Empty(unreadable.Nodes);
        Assert.Single(unreadable.Dynamic);
    }

    /// <summary>The named manifests keep their own extractors: an exact name wins over the json extension.</summary>
    [Fact]
    public void TheNamedManifests_KeepTheirOwnExtractors()
    {
        var registry = CodeGraphExtractors.OfThisBuild;

        Assert.Equal("npm", registry.Route("package.json", string.Empty).Extractor?.Language);
        Assert.Equal("tsconfig", registry.Route("tsconfig.json", string.Empty).Extractor?.Language);
        Assert.Equal("composer", registry.Route("composer.json", string.Empty).Extractor?.Language);
        Assert.Equal("json", registry.Route("appsettings.json", string.Empty).Extractor?.Language);
    }

    private static FileExtract Read(string path, string text) => new JsonExtractor().Extract(new CodeFileInput(path, text));

    private static string OwnerOf(FileExtract extract, int line)
        => extract.Nodes.FirstOrDefault(node => node.Start <= line && line <= node.End)?.Id ?? "file:";
}
