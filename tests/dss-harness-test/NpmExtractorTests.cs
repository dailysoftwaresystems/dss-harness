using DssHarness.Core.Codebase;
using DssHarness.Core.Codebase.Languages;

namespace DssHarness.Tests;

/// <summary>A Node package's package.json, held to the contract's kit, and the rules of its own the kit does not name.</summary>
public sealed class NpmExtractorTests : CodeGraphExtractorContractTests<NpmExtractor>
{
    private const string Workspace = """
        {
          "name": "workspace",
          "private": true,
          "workspaces": ["packages/*"],
          "devDependencies": {
            "@acme/shapes": "workspace:*",
            "typescript": "^5.6.0"
          }
        }
        """;

    private const string Shapes = """
        {
          "name": "@acme/shapes",
          "description": "Shapes to draw. And to measure.",
          "type": "module",
          "exports": {
            ".": {
              "import": "./src/index.ts",
              "require": "./dist/index.cjs"
            },
            "./geometry": "./src/geometry.ts",
            "./parts/*": "./src/parts/*",
            "./private/*": null
          },
          "dependencies": {
            "@acme/geometry": "workspace:*",
            "lodash": "^4.17.21"
          },
          "peerDependencies": {
            "react": ">=18"
          }
        }
        """;

    private const string Geometry = """
        {
          "name": "@acme/geometry",
          "main": "lib/index.js",
          "optionalDependencies": { "@acme/shapes": "*" }
        }
        """;

    /// <inheritdoc/>
    public override IReadOnlyList<CodeGraphFixture> Fixtures { get; } =
    [
        new("package.json", Workspace)
        {
            Nodes = [new("npm:package.json", CodeNodeKinds.Project, (1, 9))],
            Edges = [new("npm:package.json", "npm:packages/shapes/package.json", CodeEdgeKinds.ProjectReference, 6, CodeConfidences.Unique)],
        },
        new("packages/shapes/package.json", Shapes)
        {
            Nodes = [new("npm:packages/shapes/package.json", CodeNodeKinds.Project, (1, 21))],
            Edges = [new("npm:packages/shapes/package.json", "npm:packages/geometry/package.json", CodeEdgeKinds.ProjectReference, 15, CodeConfidences.Unique)],
        },
        new("packages/geometry/package.json", Geometry)
        {
            Nodes = [new("npm:packages/geometry/package.json", CodeNodeKinds.Project, (1, 5))],
            Edges = [new("npm:packages/geometry/package.json", "npm:packages/shapes/package.json", CodeEdgeKinds.ProjectReference, 4, CodeConfidences.Unique)],
        },
    ];

    /// <summary>
    /// A package is a project named by its name, its header the line that names it and its summary its description's first
    /// sentence; each package it depends on - in its dependencies, its dev, peer and optional ones - is a reference to that
    /// package by its name; its address roots are its entry - its exports' . conditions in the order written, then its
    /// source, module, main, types and typings, then the usual index files, each tried as TypeScript and Node try a path,
    /// TypeScript's first - and each subpath its exports name, a pattern's a directory's and a blocked one's none; a package that
    /// names its exports has no root of its directory.
    /// </summary>
    [Fact]
    public void APackage_IsNamedByItsName_ReferencesWhatItDependsOn_AndRootsItsAddresses()
    {
        var extract = Read("packages/shapes/package.json", Shapes);
        var node = Assert.Single(extract.Nodes);

        Assert.Equal(("@acme/shapes", "\"name\": \"@acme/shapes\",", "Shapes to draw."), (node.Name, node.Signature, node.Summary));
        Assert.Equal([("@acme/geometry", 15), ("lodash", 16), ("react", 19)], extract.References.Select(reference => (reference.Name, reference.Line)));
        Assert.All(extract.References, reference => Assert.Equal(CodeEdgeKinds.ProjectReference, reference.Kind));
        Assert.Equal(
            ["src/index.ts", "dist/index.cts", "dist/index.d.cts", "dist/index.cjs", .. Indexes.Except(["src/index.ts"])],
            extract.AddressRoots.Where(root => root.Prefix == "@acme/shapes").Select(root => root.Directory));
        Assert.All(extract.AddressRoots.Where(root => root.Prefix == "@acme/shapes"), root => Assert.True(root.Whole));
        Assert.Equal(
            [("@acme/shapes/geometry", "src/geometry.ts", true), ("@acme/shapes/parts/", "src/parts", false)],
            extract.AddressRoots.Where(root => root.Prefix != "@acme/shapes").Select(root => (root.Prefix, root.Directory, root.Whole)));
        Assert.All(extract.AddressRoots, root => Assert.False(root.Local));
        Assert.Empty(extract.Dynamic);
    }

    /// <summary>
    /// An entry is what its exports give whatever shape they take - a string, an array of fallbacks, an object of
    /// conditions with no subpath, nested conditions, a subpath's array - then its fields, each file once, each tried with
    /// TypeScript's source first; a subpath pattern whose * is not its last character, one mapped to no pattern or to a
    /// place whose * does not follow a / and end it, one with no * mapped to a pattern, and what lies outside ./ is no root.
    /// </summary>
    [Fact]
    public void AnEntry_IsWhatItsExportsGive_ThenItsFields_EachFileOnce()
    {
        string[] Whole(string text)
            => [.. Read("package.json", text).AddressRoots.Where(root => root.Whole && root.Prefix == "p").Select(root => root.Directory).Except(Indexes)];

        Assert.Equal(
            ["x.ts", "x.tsx", "x.d.ts", "x.js", "lib/main.ts", "lib/main.tsx", "lib/main.d.ts", "lib/main.js"],
            Whole("""{ "name": "p", "exports": "./x.js", "main": "lib/main.js", "module": "./x.js" }"""));
        var once = Read("package.json", """{ "name": "p", "exports": "./x.js", "module": "./x.js", "main": "x.js" }""").AddressRoots;
        Assert.Equal(once.Distinct(), once);
        Assert.Equal(["x.ts", "x.tsx", "x.d.ts", "x.js", .. Indexes], once.Select(root => root.Directory));
        Assert.Equal(["e.mts", "e.d.mts", "e.mjs", "f.json"], Whole("""{ "name": "p", "exports": ["./e.mjs", "./f.json"] }"""));
        Assert.Equal(
            ["esm/index.ts", "esm/index.tsx", "esm/index.d.ts", "esm/index.js", "types/index.d.ts", "cjs/index.ts", "cjs/index.tsx", "cjs/index.d.ts", "cjs/index.js"],
            Whole("""{ "name": "p", "exports": { "node": { "import": "./esm/index.js", "types": "./types/index.d.ts" }, "default": "./cjs/index.js" } }"""));
        Assert.Equal(["a.ts", "a.tsx", "a.d.ts", "a.js", "b.css"], Whole("""{ "name": "p", "exports": { ".": ["./a.js", { "default": "./b.css" }] } }"""));
        Assert.Equal(["src/p.ts", "m.ts", "m.tsx", "m.d.ts", "m.js", "t.d.ts"], Whole("""{ "name": "p", "source": "src/p.ts", "module": "m.js", "main": "", "typings": "t.d.ts" }"""));

        var subpaths = Read("package.json", """
            { "name": "p", "exports": { "./a/*/b": "./a/*/b.js", "./c": "./c/*", "./d/*": "./d.js", "./e/*.js": "./e/*.js", "./g/*": "./src/g*", "x": "./x.js", "./f*": "./src/f/*", "./h": null } }
            """);

        Assert.Equal([("p/f", "src/f")], subpaths.AddressRoots.Where(root => root.Prefix != "p").Select(root => (root.Prefix, root.Directory)));
        Assert.Equal(("p/", "."), Assert.Single(Read("package.json", """{ "name": "p" }""").AddressRoots, root => !root.Whole) is var directory ? (directory.Prefix, directory.Directory) : default);
        Assert.Single(Read("package.json", """{ "name": "p", "exports": null }""").AddressRoots, root => root is { Prefix: "p/", Whole: false });
    }

    /// <summary>
    /// A file that is no JSON object - one that does not parse, an array, nothing, one nested past the reader's bounds - is
    /// a project named by its file, nothing in it read, the line it stops at marked dynamic; one with no name declares no
    /// address root and marks nothing.
    /// </summary>
    [Fact]
    public void AFileThatIsNoObject_IsAProjectOfItsFileName_NothingReadAndTheLineItStopsAtMarked()
    {
        var deep = "{\n\"name\": \"p\",\n\"a\":\n" + string.Concat(Enumerable.Repeat("[\n", 100)) + string.Concat(Enumerable.Repeat("]\n", 100)) + "}";

        foreach (var (text, line) in new[]
        {
            ("{\n  \"name\": \"p\",\n  \"dependencies\": {\n    \"a\": 1,,\n", 4), ("{ \"name\": \"p\", // a comment\n}", 1), ("[\"name\"]", 1), ("", 1), (deep, 67), ("{ \"name\": \"p\" }\n}", 2),
        })
        {
            var extract = Read("package.json", text);

            Assert.Equal("package.json", Assert.Single(extract.Nodes).Name);
            Assert.Empty(extract.References);
            Assert.Empty(extract.AddressRoots);
            Assert.Equal(line, Assert.Single(extract.Dynamic).Line);
        }

        var unnamed = Read("tool/package.json", "{ \"description\": \"no name\", \"name\": \"\" }");
        Assert.Equal("package.json", Assert.Single(unnamed.Nodes).Name);
        Assert.Empty(unnamed.AddressRoots);
        Assert.Empty(unnamed.Dynamic);
    }

    /// <summary>
    /// A bare import finds the file a package's roots give it, whichever language imports it: the entry the first of its
    /// files the repository holds, a subpath the file it names, a pattern's address the file under its directory; a path a
    /// package that names its exports does not export, and a package no repository's package has the name of, are found in
    /// none.
    /// </summary>
    [Fact]
    public void ABareImport_FindsTheFileAPackagesRootsGiveIt()
    {
        var linked = Link(
            ("packages/shapes/package.json", Shapes),
            ("packages/shapes/dist/index.cjs", "module.exports = { draw() {} };"),
            ("packages/shapes/src/geometry.ts", "export function area() {}"),
            ("packages/shapes/src/parts/circle.ts", "export class Circle {}"),
            ("packages/shapes/README.js", "export const readme = 1;"),
            ("app/main.ts", """
                import { draw } from '@acme/shapes';
                import { area } from '@acme/shapes/geometry';
                import { Circle } from '@acme/shapes/parts/circle';
                import { readme } from '@acme/shapes/README.js';
                import { chunk } from 'lodash';
                export function go() { draw(); area(); return new Circle(); }
                """));

        Assert.Equal(
            [
                ("file:app/main.ts", "file:packages/shapes/dist/index.cjs", 1), ("file:app/main.ts", "file:packages/shapes/src/geometry.ts", 2),
                ("file:app/main.ts", "file:packages/shapes/src/parts/circle.ts", 3),
            ],
            linked.Edges.Where(edge => edge.Kind == CodeEdgeKinds.Imports).Select(edge => (edge.From, edge.To, edge.Line)));
        Assert.Contains(new CodeEdge("typescript:app/main.ts.go", "typescript:packages/shapes/src/geometry.ts.area", CodeEdgeKinds.Calls, 6, CodeConfidences.Unique), linked.Edges);
        Assert.Contains(new CodeEdge("typescript:app/main.ts.go", "typescript:packages/shapes/src/parts/circle.ts.Circle", CodeEdgeKinds.Instantiates, 6, CodeConfidences.Unique), linked.Edges);
    }

    /// <summary>
    /// An entry written as Node takes it - with no extension, or a directory - finds the file Node finds, and a package that
    /// names no exports has every path under its directory imported by its name.
    /// </summary>
    [Fact]
    public void AnEntryWrittenWithNoExtension_OrADirectory_FindsTheFileNodeFinds()
    {
        var linked = Link(
            ("packages/old/package.json", """{ "name": "old", "main": "lib/index" }"""),
            ("packages/old/lib/index.js", "exports.a = 1;"),
            ("packages/old/lib/deep.js", "exports.b = 1;"),
            ("packages/dir/package.json", """{ "name": "dir", "main": "./lib" }"""),
            ("packages/dir/lib/index.js", "exports.c = 1;"),
            ("packages/cjs/package.json", """{ "name": "cjs", "main": "lib/main" }"""),
            ("packages/cjs/lib/main.cjs", "exports.d = 1;"),
            ("app/x.js", "const old = require('old');\nconst deep = require('old/lib/deep');\nconst dir = require('dir');\nconst cjs = require('cjs');"));

        Assert.Equal(
            [
                ("file:app/x.js", "file:packages/old/lib/index.js", 1), ("file:app/x.js", "file:packages/old/lib/deep.js", 2), ("file:app/x.js", "file:packages/dir/lib/index.js", 3),
                ("file:app/x.js", "file:packages/cjs/lib/main.cjs", 4),
            ],
            linked.Edges.Where(edge => edge.Kind == CodeEdgeKinds.Imports).Select(edge => (edge.From, edge.To, edge.Line)));
    }

    /// <summary>A line is the one the core counts whatever the file encodes before it: a byte order mark, CR LF endings, characters of two and four bytes.</summary>
    [Fact]
    public void ALine_IsTheOneTheCoreCounts_WhateverTheFileEncodesBeforeIt()
    {
        var extract = Read("package.json", "\uFEFF{\r\n  \"description\": \"\u00FC\u00E9 \U0001F600" + new string('\u00E9', 40) + "\",\r\n  \"name\": \"p\",\r\n  \"dependencies\": {\r\n    \"\u00E5\U0001F600\": \"1\",\r\n    \"b\": \"2\"\r\n  }\r\n}");

        Assert.Equal([5, 6], extract.References.Select(reference => reference.Line));
        Assert.Equal("\"name\": \"p\",", Assert.Single(extract.Nodes).Signature);
        Assert.Equal([4], Read("package.json", "\uFEFF{\n\"name\": \"p\",\n\"dependencies\": {\n\"a\": \"1\"\n}\n}").References.Select(reference => reference.Line));
        Assert.Equal(4, Assert.Single(Read("package.json", "\uFEFF{\r\n\"a\": 1,\r\n\"\U0001F600\": [\r\n1,,\r\n]}").Dynamic).Line);
    }

    /// <summary>The usual index files a package's entry is tried as last, each as TypeScript and then Node try it: what a package that names no entry has as its entry.</summary>
    private static IReadOnlyList<string> Indexes { get; } =
        [.. new NpmExtractor().Extract(new CodeFileInput("package.json", """{ "name": "p" }""")).AddressRoots.Where(root => root.Whole).Select(root => root.Directory)];

    private static FileExtract Read(string path, string text) => new NpmExtractor().Extract(new CodeFileInput(path, text));

    /// <summary>The files linked together, each read by the implementation that claims it.</summary>
    internal static CodeGraphLinked Link(params (string Path, string Text)[] files)
        => CodeGraphLinker.Link([.. files.Select(file => CodeGraphExtractors.OfThisBuild.ClaimantsOf(file.Path)[0].Extractor)
            .Zip(files, (extractor, file) => new CodeGraphFile(file.Path, extractor.Language, extractor.Extract(new CodeFileInput(file.Path, file.Text))))]);
}
