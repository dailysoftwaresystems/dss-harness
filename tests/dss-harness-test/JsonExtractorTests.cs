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
                new("json:appsettings.json#logging", CodeNodeKinds.Section, (14, 16)),
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
        new("package-lock.json", Lock)
        {
            Nodes =
            [
                new("json:package-lock.json#packages", CodeNodeKinds.Section, (3, 8)),
                new("json:package-lock.json#packages[\"\"].name", CodeNodeKinds.Member, (5, 5)),
                new("json:package-lock.json#[\"\"]", CodeNodeKinds.Section, (9, 9)),
                new("json:package-lock.json#[\"\"].a", CodeNodeKinds.Member, (9, 9)),
            ],
        },
    ];

    /// <summary>A lockfile of npm's - its root package named by the empty name - and a member of the empty name at the top.</summary>
    private const string Lock = """
        {
          "lockfileVersion": 3,
          "packages": {
            "": {
              "name": "billing",
              "usage": "run:\n  make"
            }
          },
          "": { "a": 1 }
        }
        """;

    /// <summary>
    /// Each scalar key anywhere in the file is a key node named by its path - an array item's path holding its place -
    /// with its value as its summary; each top-level member that holds others is a section over its own lines, to its
    /// closing bracket - the last one's never the file's; and a <c>$schema</c> or an external <c>$ref</c> naming a file by
    /// a relative path is an import of it.
    /// </summary>
    [Fact]
    public void EachScalarKeyIsAKeyNodeNamedByItsPath_AndEachTopLevelHolderASection()
    {
        var extract = Read("appsettings.json", AppSettings);

        Assert.Equal(
            [
                ("targets", null, 4, 9),
                ("limits", null, 10, 13),
                ("logging", null, 14, 16),
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

    /// <summary>
    /// Where the file stops reading as JSON - a JSON5 name with no quotes among them - nothing in it is read, and the line
    /// it stops at is marked dynamic with the reader's own reason; a file that holds no value - empty, white space, comments
    /// alone - stopped nowhere, and is marked nowhere.
    /// </summary>
    [Fact]
    public void WhereTheFileStopsReading_NothingIsRead_AndTheLineIsMarkedDynamic()
    {
        var broken = new[] { "{ \"a\": 1,\n  ,} }", "[1,\n 2", "{} {}" }.Select(text => Read("broken.json", text)).ToList();

        Assert.All(broken, extract => Assert.Empty(extract.Nodes));
        Assert.Equal(
            [
                new ExtractDynamic(2, "the file stops reading as JSON here (',' is an invalid start of a property name. Expected a '\"'), and nothing of it is read"),
                new ExtractDynamic(2, "the file stops reading as JSON here ('2' is an invalid end of a number. Expected a delimiter), and nothing of it is read"),
                new ExtractDynamic(1, "the file stops reading as JSON here ('{' is invalid after a single JSON value. Expected end of data), and nothing of it is read"),
            ],
            broken.SelectMany(extract => extract.Dynamic));

        foreach (var text in new[] { string.Empty, "  \n\t\n", "// a file of comments\n/* and a block\n of them */\n" })
        {
            var empty = Read("empty.jsonc", text);

            Assert.Empty(empty.Nodes);
            Assert.Empty(empty.Dynamic);
        }

        var unreadable = Read("quoted.json5", Json5);

        Assert.Empty(unreadable.Nodes);
        Assert.Single(unreadable.Dynamic);
    }

    /// <summary>
    /// A name that is empty is a place a path holds in brackets, as an npm lockfile's root package is named, and no
    /// extract the core refuses: a value over several lines is a summary on one line, a key longer than a signature holds
    /// keeps its whole path in its id and its qualified name and is cut in its signature alone.
    /// </summary>
    [Fact]
    public void AnEmptyName_AValueOverLines_AndALongKey_AreEachAnExtractTheCoreHolds()
    {
        var extract = Read("package-lock.json", Lock);

        Assert.Equal(
            [
                ("json:package-lock.json#lockfileVersion", "lockfileVersion", "lockfileVersion", "3", (string?)null),
                ("json:package-lock.json#packages", "packages", "package-lock.json#packages", null, null),
                ("json:package-lock.json#packages[\"\"].name", "name", "packages[\"\"].name", "billing", "json:package-lock.json#packages"),
                ("json:package-lock.json#packages[\"\"].usage", "usage", "packages[\"\"].usage", "run: make", "json:package-lock.json#packages"),
                ("json:package-lock.json#[\"\"]", "[\"\"]", "package-lock.json#[\"\"]", null, null),
                ("json:package-lock.json#[\"\"].a", "a", "[\"\"].a", "1", "json:package-lock.json#[\"\"]"),
            ],
            extract.Nodes.Select(node => (node.Id, node.Name, node.Qualified, node.Summary, node.Parent)));
        Assert.Null(extract.WithDigests(CodeFileText.Lines(Lock)).Problem("json", CodeFileText.Lines(Lock).Count));

        var name = new string('k', FileExtract.MostSignature + 60);
        var text = $"{{ \"{name}\": \"v\" }}";
        var keyed = Read("long.json", text);
        var key = Assert.Single(keyed.Nodes);

        Assert.Equal(($"json:long.json#{name}", name), (key.Id, key.Qualified));
        Assert.True(key.Signature.Length <= FileExtract.MostSignature, $"the signature is {key.Signature.Length} characters");
        Assert.Null(keyed.WithDigests(CodeFileText.Lines(text)).Problem("json", 1));
    }

    /// <summary>
    /// A key that holds a line break - a translation file keyed by its source strings - is a place a path holds in
    /// brackets, quoted with each control character escaped, so its node's name is one line and its extract one the core
    /// holds; a key that holds none is named as it is.
    /// </summary>
    [Fact]
    public void AKeyThatHoldsALineBreak_IsNamedOnOneLine_InBrackets()
    {
        const string Text = "{\n  \"messages\": {\n    \"Are you sure?\\nThis cannot be undone.\": \"Sure?\",\n    \"Tab\\there\": \"t\",\n    \"plain\": \"p\"\n  }\n}\n";
        var extract = Read("i18n/en.json", Text);

        Assert.Equal(
            [
                ("json:i18n/en.json#messages", "messages", "i18n/en.json#messages"),
                ("json:i18n/en.json#messages[\"Are you sure?\\nThis cannot be undone.\"]", "[\"Are you sure?\\nThis cannot be undone.\"]", "messages[\"Are you sure?\\nThis cannot be undone.\"]"),
                ("json:i18n/en.json#messages[\"Tab\\there\"]", "[\"Tab\\there\"]", "messages[\"Tab\\there\"]"),
                ("json:i18n/en.json#messages.plain", "plain", "messages.plain"),
            ],
            extract.Nodes.Select(node => (node.Id, node.Name, node.Qualified)));
        Assert.Null(extract.WithDigests(CodeFileText.Lines(Text)).Problem("json", CodeFileText.Lines(Text).Count));
    }

    /// <summary>
    /// A <c>$ref</c> into another file by a relative path is an import of that file, its pointer and query left out; a
    /// pointer into this file, an address with a scheme, a rooted path and a protocol-relative one name none.
    /// </summary>
    [Fact]
    public void ARefIntoAnotherFile_IsAnImportOfIt_ItsPointerLeftOut()
    {
        var extract = Read("schemas/order.json", """
            {
              "address": { "$ref": "common.schema.json#/definitions/address" },
              "item": { "$ref": "../shared/item.json?v=2#/definitions/item" },
              "self": { "$ref": "#/definitions/self" },
              "remote": { "$ref": "urn:example:schema" },
              "rooted": { "$ref": "/schemas/rooted.json" },
              "far": { "$ref": "//example.com/far.json" }
            }
            """);

        Assert.Equal(
            [(CodeImportForms.Relative, "common.schema.json", 2), (CodeImportForms.Relative, "../shared/item.json", 3)],
            extract.Imports.Select(import => (import.Form, import.Target, import.Line)));
    }

    /// <summary>
    /// Each section ends where its own value does - its closing bracket - so two that share a line hold each key they
    /// hold, and a list at the top places each of its items by its place: no extract the core refuses.
    /// </summary>
    [Fact]
    public void ASectionEndsWhereItsValueDoes_TwoOnOneLineAmongThem()
    {
        const string Text = """
            [
              {"a": 1,
               "b": 2}, {"a": 3,
               "b": 4},
              5
            ]
            """;

        var extract = Read("list.json", Text);

        Assert.Equal(
            [
                ("json:list.json#[0]", 2, 3),
                ("json:list.json#[0].a", 2, 2),
                ("json:list.json#[0].b", 3, 3),
                ("json:list.json#[1]", 3, 4),
                ("json:list.json#[1].a", 3, 3),
                ("json:list.json#[1].b", 4, 4),
                ("json:list.json#[2]", 5, 5),
            ],
            extract.Nodes.Select(node => (node.Id, node.Start, node.End)));
        Assert.Null(extract.WithDigests(CodeFileText.Lines(Text)).Problem("json", CodeFileText.Lines(Text).Count));

        // A list of records is a section and a key a record while the extract has room for both, sections counted against
        // FileExtract.MostKeys as keys are, and every key past them counted.
        var records = Read("records.json", $"[{string.Join(",\n", Enumerable.Range(0, FileExtract.MostKeys + 10).Select(at => $"{{\"id\": {at}}}"))}]");

        Assert.Equal((FileExtract.MostKeys / 2, FileExtract.MostKeys / 2), (records.Nodes.Count(node => node.Kind == CodeNodeKinds.Section), records.Nodes.Count(node => node.Kind == CodeNodeKinds.Member)));
        Assert.Equal(FileExtract.MostKeys + 10 - (FileExtract.MostKeys / 2), records.KeysBeyond);
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
