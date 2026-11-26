using DssHarness.Core.Codebase;
using DssHarness.Core.Codebase.Languages;
using DssHarness.Core.Output;

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
        new("appsettings.Development.json", Development)
        {
            Nodes =
            [
                new("json:appsettings.Development.json#Logging.LogLevel[\"Microsoft.AspNetCore\"]", CodeNodeKinds.Member, (5, 5)),
                new("json:appsettings.Development.json#Logging.LogLevel.Microsoft.AspNetCore", CodeNodeKinds.Member, (7, 7)),
                new("json:appsettings.Development.json#Logging.LogLevel.Microsoft", CodeNodeKinds.Section, (6, 8)),
                new("json:appsettings.Development.json#[\"Logging:LogLevel:System.Net.Http\"]", CodeNodeKinds.Member, (11, 11)),
            ],
        },
        new(".vscode/settings.json", Editor)
        {
            Nodes =
            [
                new("json:.vscode/settings.json#[\"[typescript]\"]", CodeNodeKinds.Section, (3, 5)),
                new("json:.vscode/settings.json#[\"[typescript]\"][\"editor.defaultFormatter\"]", CodeNodeKinds.Member, (4, 4)),
                new("json:.vscode/settings.json#[\"files.exclude\"][\"[0]\"]", CodeNodeKinds.Member, (8, 8)),
            ],
        },
    ];

    /// <summary>
    /// An appsettings file whose logging levels name one key with a dot of its own beside keys nested by the same dots, and a
    /// flat key .NET's configuration reads by its colons: the path of each is written so it reads back as the one it is.
    /// </summary>
    private const string Development = """
        {
          "Logging": {
            "LogLevel": {
              "Default": "Information",
              "Microsoft.AspNetCore": "Warning",
              "Microsoft": {
                "AspNetCore": "Error"
              }
            }
          },
          "Logging:LogLevel:System.Net.Http": "Error"
        }
        """;

    /// <summary>A VS Code settings file, whose names hold brackets and dots of their own.</summary>
    private const string Editor = """
        {
          "editor.formatOnSave": true,
          "[typescript]": {
            "editor.defaultFormatter": "esbenp.prettier-vscode"
          },
          "files.exclude": {
            "**/.git": true,
            "[0]": false
          }
        }
        """;

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
    /// with its value as its summary; each member that holds others is a section over its own lines, to its closing
    /// bracket - the last one's never the file's - inside the section that holds it; and a <c>$schema</c> or an external
    /// <c>$ref</c> naming a file by a relative path is an import of it.
    /// </summary>
    [Fact]
    public void EachScalarKeyIsAKeyNodeNamedByItsPath_AndEachHolderASectionInsideItsOwn()
    {
        var extract = Read("appsettings.json", AppSettings);

        Assert.Equal(
            [
                ("targets", null, 4, 9, null),
                ("x64", null, 5, 8, "json:appsettings.json#targets"),
                ("limits", null, 10, 13, null),
                ("[0]", null, 11, 11, "json:appsettings.json#limits"),
                ("[1]", null, 12, 12, "json:appsettings.json#limits"),
                ("logging", null, 14, 16, null),
            ],
            extract.Nodes.Where(node => node.Kind == CodeNodeKinds.Section).Select(node => (node.Name, node.SubKind, node.Start, node.End, node.Parent?.Whole())));

        var key = Assert.Single(extract.Nodes, node => node.Id == "json:appsettings.json#targets.x64.callingConvention");

        Assert.Equal(("callingConvention", "targets.x64.callingConvention", "cdecl"), (key.Name, key.Qualified, key.Summary));
        Assert.Equal("json:appsettings.json#targets.x64", key.Parent!.Whole());

        var inner = Assert.Single(extract.Nodes, node => node.Id == "json:appsettings.json#limits[1].value");

        Assert.Equal(("value", "4", "json:appsettings.json#limits[1]"), (inner.Name, inner.Summary, inner.Parent));
        Assert.Equal([("appsettings.schema.json", 2)], extract.Imports.Select(import => (CodeGraphIds.FileName(import.Target), import.Line)));
        Assert.All(extract.Imports, import => Assert.Equal(CodeImportForms.Relative, import.Form));
        Assert.Empty(extract.Dynamic);
    }

    /// <summary>A local <c>$ref</c> pointer names no file, and neither does a URL: neither is an import, but each is a key like any other.</summary>
    [Fact]
    public void ALocalRefPointerAndAUrl_NameNoFileAndMakeNoImport()
    {
        var extract = Read("defs.json", """{ "$ref": "#/definitions/x", "remote": { "$ref": "https://example.com/other.json" } }""");

        Assert.Empty(extract.Imports);
        Assert.Equal([("$ref", "#/definitions/x"), ("$ref", "https://example.com/other.json")],
            extract.Nodes.Where(node => node.Kind == CodeNodeKinds.Member).OrderBy(node => node.Id.Whole()).Select(node => (node.Name, node.Summary)));
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
                new ExtractDynamic(2, "the file stops reading as JSON here (',' is an invalid start of a property name. Expected a '\"'), and nothing of it is read", DataNodes.Stop),
                new ExtractDynamic(2, "the file stops reading as JSON here ('2' is an invalid end of a number. Expected a delimiter), and nothing of it is read", DataNodes.Stop),
                new ExtractDynamic(1, "the file stops reading as JSON here ('{' is invalid after a single JSON value. Expected end of data), and nothing of it is read", DataNodes.Stop),
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
                ("json:package-lock.json#packages[\"\"]", "[\"\"]", "package-lock.json#packages[\"\"]", null, "json:package-lock.json#packages"),
                ("json:package-lock.json#packages[\"\"].name", "name", "packages[\"\"].name", "billing", "json:package-lock.json#packages[\"\"]"),
                ("json:package-lock.json#packages[\"\"].usage", "usage", "packages[\"\"].usage", "run: make", "json:package-lock.json#packages[\"\"]"),
                ("json:package-lock.json#[\"\"]", "[\"\"]", "package-lock.json#[\"\"]", null, null),
                ("json:package-lock.json#[\"\"].a", "a", "[\"\"].a", "1", "json:package-lock.json#[\"\"]"),
            ],
            extract.Nodes.Select(node => (node.Id.Whole(), node.Name, node.Qualified.Whole(), node.Summary, node.Parent?.Whole())));
        Assert.Null(extract.WithDigests(Lock).Problem("json", CodeFileText.Lines(Lock).Count));

        var name = new string('k', 12_000);
        var text = $"{{ \"{name}\": \"v\" }}";
        var keyed = Read("long.json", text);
        var key = Assert.Single(keyed.Nodes);

        Assert.Equal(($"json:long.json#{name}", name), (key.Id, key.Qualified));
        Assert.Equal(name, key.Signature);
        Assert.Null(keyed.WithDigests(text).Problem("json", 1));
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
            extract.Nodes.Select(node => (node.Id.Whole(), node.Name, node.Qualified.Whole())));
        Assert.Null(extract.WithDigests(Text).Problem("json", CodeFileText.Lines(Text).Count));
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
            extract.Nodes.Select(node => (node.Id.Whole(), node.Start, node.End)));
        Assert.Null(extract.WithDigests(Text).Problem("json", CodeFileText.Lines(Text).Count));

        // A list of records is a section and a key a record, however many: none is left out, and each key is inside its
        // own record's section.
        var records = Read("records.json", $"[{string.Join(",\n", Enumerable.Range(0, 12_000).Select(at => $"{{\"id\": {at}}}"))}]");

        Assert.Equal(
            [.. Enumerable.Range(0, 12_000).SelectMany(at => new[] { ($"json:records.json#[{at}]", (string?)null), ($"json:records.json#[{at}].id", $"json:records.json#[{at}]") })],
            records.Nodes.Select(node => (node.Id.Whole(), node.Parent?.Whole())));
    }

    /// <summary>
    /// A file that nests thousands of levels deep is read whole, each level a section inside the one before and the key at
    /// the bottom named by its whole path - on a thread whose stack is a fraction of what reading it by recursion would
    /// take, so nothing of it is read by the call's own.
    /// </summary>
    [Fact]
    public void AFileNestedThousandsDeep_IsReadWhole_OnAStackOfItsOwn()
    {
        const int Depth = 3000;
        var text = string.Concat(Enumerable.Repeat("{\"a\":", Depth)) + "1" + new string('}', Depth);
        FileExtract? extract = null;
        var thread = new Thread(() => extract = Read("deep.json", text), 256 * 1024);

        thread.Start();
        thread.Join();

        Assert.Equal(Depth, extract!.Nodes.Count);
        Assert.Equal(Depth - 1, extract.Nodes.Count(node => node.Kind == CodeNodeKinds.Section));
        Assert.Equal((string.Join('.', Enumerable.Repeat("a", Depth)), "1"), (extract.Nodes[^1].Qualified, extract.Nodes[^1].Summary));
        Assert.Equal(extract.Nodes[^2].Id, extract.Nodes[^1].Parent);
        Assert.Empty(extract.Dynamic);
        Assert.Null(extract.WithDigests(text).Problem("json", 1));
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

    /// <summary>
    /// A key whose name holds a dot or a bracket is one place of its path, quoted in brackets as <see cref="ReportText.Quoted"/>
    /// writes it, and is named by its own name: <c>Logging.LogLevel["Microsoft.AspNetCore"]</c> is one key and the path of
    /// the nested <c>Logging.LogLevel.Microsoft.AspNetCore</c> another, where both were written alike and their ids told
    /// apart by a count. Every reader of data files writes a key's path through the one owner of the join.
    /// </summary>
    [Fact]
    public void AKeyWhoseNameHoldsADotOrABracket_IsOnePlaceQuotedInBrackets_NamedAsItsFileWritesIt()
    {
        var development = Read("appsettings.Development.json", Development);
        var editor = Read(".vscode/settings.json", Editor);

        Assert.Equal(
            [
                ("json:appsettings.Development.json#Logging", "Logging", "appsettings.Development.json#Logging", (string?)null),
                ("json:appsettings.Development.json#Logging.LogLevel", "LogLevel", "appsettings.Development.json#Logging.LogLevel", null),
                ("json:appsettings.Development.json#Logging.LogLevel.Default", "Default", "Logging.LogLevel.Default", "Information"),
                ("json:appsettings.Development.json#Logging.LogLevel[\"Microsoft.AspNetCore\"]", "Microsoft.AspNetCore", "Logging.LogLevel[\"Microsoft.AspNetCore\"]", "Warning"),
                ("json:appsettings.Development.json#Logging.LogLevel.Microsoft", "Microsoft", "appsettings.Development.json#Logging.LogLevel.Microsoft", null),
                ("json:appsettings.Development.json#Logging.LogLevel.Microsoft.AspNetCore", "AspNetCore", "Logging.LogLevel.Microsoft.AspNetCore", "Error"),
                ("json:appsettings.Development.json#[\"Logging:LogLevel:System.Net.Http\"]", "Logging:LogLevel:System.Net.Http", "[\"Logging:LogLevel:System.Net.Http\"]", "Error"),
            ],
            development.Nodes.Select(node => (node.Id.Whole(), node.Name, node.Qualified.Whole(), node.Summary)));

        Assert.Equal(
            [
                ("json:.vscode/settings.json#[\"editor.formatOnSave\"]", "editor.formatOnSave", "[\"editor.formatOnSave\"]", "true"),
                ("json:.vscode/settings.json#[\"[typescript]\"]", "[typescript]", ".vscode/settings.json#[\"[typescript]\"]", null),
                ("json:.vscode/settings.json#[\"[typescript]\"][\"editor.defaultFormatter\"]", "editor.defaultFormatter", "[\"[typescript]\"][\"editor.defaultFormatter\"]", "esbenp.prettier-vscode"),
                ("json:.vscode/settings.json#[\"files.exclude\"]", "files.exclude", ".vscode/settings.json#[\"files.exclude\"]", null),
                ("json:.vscode/settings.json#[\"files.exclude\"][\"**/.git\"]", "**/.git", "[\"files.exclude\"][\"**/.git\"]", "true"),
                ("json:.vscode/settings.json#[\"files.exclude\"][\"[0]\"]", "[0]", "[\"files.exclude\"][\"[0]\"]", "false"),
            ],
            editor.Nodes.Select(node => (node.Id.Whole(), node.Name, node.Qualified.Whole(), node.Summary)));

        Assert.Null(development.WithDigests(Development).Problem("json", CodeFileText.Lines(Development).Count));
        Assert.Null(editor.WithDigests(Editor).Problem("json", CodeFileText.Lines(Editor).Count));
    }

    /// <summary>
    /// The help says every spelling a key's path takes: an item by its place, a name that is empty, white space, or holds
    /// a control character, a dot or a bracket quoted in brackets, and a key named by its own name - the forms its extract
    /// holds, so a later wording change to them is held as the extract is.
    /// </summary>
    [Fact]
    public void TheHelp_SaysEverySpellingAKeysPathTakes()
    {
        Assert.Contains(
            "(an item by its place, [0]; a name that is empty or white space, holds a control character, a dot or a bracket "
                + "quoted in brackets, [\"\"], Logging.LogLevel[\"Microsoft.AspNetCore\"]; the key named by its own name, "
                + "Microsoft.AspNetCore)",
            new JsonExtractor().Help.Reads,
            StringComparison.Ordinal);
    }

    private static FileExtract Read(string path, string text) => new JsonExtractor().Extract(new CodeFileInput(path, text));

    private static string OwnerOf(FileExtract extract, int line)
        => extract.Nodes.FirstOrDefault(node => node.Start <= line && line <= node.End)?.Id.Whole() ?? "file:";
}
