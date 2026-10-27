using DssHarness.Core.Codebase;
using DssHarness.Core.Codebase.Languages;

namespace DssHarness.Tests;

/// <summary>A TypeScript project's tsconfig.json, held to the contract's kit, and the rules of its own the kit does not name.</summary>
public sealed class TsconfigExtractorTests : CodeGraphExtractorContractTests<TsconfigExtractor>
{
    private const string Root = """
        // The repository's projects.
        {
          "compilerOptions": {
            "baseUrl": "./",
            /* Where a bare import is looked for. */
            "paths": {
              "@app/*": ["src/*", "./generated/*",],
              "@config": ["config/index.ts"],
              "*": ["types/*"],
              "@bad/*/x": ["bad/*/x"],
            },
          },
          "references": [
            { "path": "./packages/shapes" },
            { "path": "web/jsconfig.json" },
            { "path": "../outside" },
          ],
        }
        """;

    private const string Shapes = """
        {
          "extends": "../../tsconfig.json",
          "compilerOptions": { "composite": true }
        }
        """;

    private const string Web = """
        { "compilerOptions": { "checkJs": true } }
        """;

    /// <inheritdoc/>
    public override IReadOnlyList<CodeGraphFixture> Fixtures { get; } =
    [
        new("tsconfig.json", Root)
        {
            Nodes = [new("tsconfig:tsconfig.json", CodeNodeKinds.Project, (1, 18))],
            Edges =
            [
                new("tsconfig:tsconfig.json", "tsconfig:packages/shapes/tsconfig.json", CodeEdgeKinds.ProjectReference, 14, CodeConfidences.Unique),
                new("tsconfig:tsconfig.json", "tsconfig:web/jsconfig.json", CodeEdgeKinds.ProjectReference, 15, CodeConfidences.Unique),
            ],
        },
        new("packages/shapes/tsconfig.json", Shapes) { Nodes = [new("tsconfig:packages/shapes/tsconfig.json", CodeNodeKinds.Project, (1, 4))] },
        new("web/jsconfig.json", Web) { Nodes = [new("tsconfig:web/jsconfig.json", CodeNodeKinds.Project, (1, 1))] },
    ];

    /// <summary>
    /// A project is named by its path, comments and trailing commas read as TypeScript reads them; each project its
    /// references name is a reference by that project's path - a directory's tsconfig.json or the file named - one out of
    /// the tree none; each path mapping is a local root from its baseUrl, each place in turn - a pattern whose * ends it a
    /// directory's, one with no * a file's, the catch-all and any other pattern none.
    /// </summary>
    [Fact]
    public void AProject_ReferencesWhatItsReferencesName_AndRootsEachPathMapping()
    {
        var extract = Read("tsconfig.json", Root);
        var node = Assert.Single(extract.Nodes);

        Assert.Equal(("tsconfig.json", "tsconfig.json"), (node.Name, node.Signature));
        Assert.Equal(
            [("packages/shapes/tsconfig.json", 14), ("web/jsconfig.json", 15)],
            extract.References.Select(reference => (reference.Name, reference.Line)));
        Assert.Equal(
            [("@app/", "src", false), ("@app/", "generated", false), ("@config", "config/index.ts", true)],
            extract.AddressRoots.Select(root => (root.Prefix, root.Directory, root.Whole)));
        Assert.All(extract.AddressRoots, root => Assert.True(root.Local));
        Assert.Empty(extract.Dynamic);
    }

    /// <summary>
    /// A pattern whose * ends it maps every address it begins - with or without a / before its * - to a place whose *
    /// follows a / and ends it, and no other, nor a pattern and a place of which one has a * and the other none; a pattern
    /// with no * maps its one address to each path TypeScript and Node try its place as: as written, with each of their
    /// extensions, then as a directory's index with each.
    /// </summary>
    [Fact]
    public void APathMapping_IsADirectorysWhereItsStarEndsIt_AndAFilesTriedAsTypeScriptAndNodeTryAPath()
    {
        var extract = Read("tsconfig.json", """{ "compilerOptions": { "paths": { "@lib*": ["lib/*"], "@bad/*": ["src/bad*"], "@icons/*": ["src/icons/*.svg"], "@mid/*": ["src/*/index"], "@one": ["src/*"], "@many/*": ["src/one.ts"], "@mid*/x": ["src/*"], "@cfg": ["src/config"] } } }""");

        Assert.Equal([("@lib", "lib")], extract.AddressRoots.Where(root => !root.Whole).Select(root => (root.Prefix, root.Directory)));
        Assert.Equal(
            [
                "src/config", "src/config.ts", "src/config.tsx", "src/config.d.ts", "src/config.js", "src/config.jsx", "src/config.mjs", "src/config.cjs",
                "src/config.json", "src/config/index.ts", "src/config/index.tsx", "src/config/index.d.ts", "src/config/index.js", "src/config/index.jsx",
                "src/config/index.mjs", "src/config/index.cjs", "src/config/index.json",
            ],
            extract.AddressRoots.Where(root => root.Whole).Select(root => root.Directory));
    }

    /// <summary>A baseUrl the mappings are from - one written with ./, one with a trailing separator, one with a backslash, a parent's - joins each place to one clean path, a place written with a backslash too.</summary>
    [Fact]
    public void ABaseUrl_JoinsEachPlaceToOneCleanPath()
    {
        foreach (var (baseUrl, place, directory) in new[]
        {
            ("./", "./src/*", "src"), (".", "src/*", "src"), ("lib/", "./src/*", "lib/src"), ("lib\\\\", "src/*", "lib/src"), ("..", "shared/*", "../shared"), ("./", "*", "."), (".", "src\\\\*", "src"),
        })
        {
            var extract = Read("app/tsconfig.json", $$"""{ "compilerOptions": { "baseUrl": "{{baseUrl}}", "paths": { "@x/*": ["{{place}}"] } } }""");

            Assert.Equal(directory, Assert.Single(extract.AddressRoots).Directory);
        }
    }

    /// <summary>
    /// A file that is no JSON object - one that does not parse, an array, nothing - is a project named by its path, nothing
    /// in it read, the line it stops at marked dynamic.
    /// </summary>
    [Fact]
    public void AFileThatIsNoObject_IsAProject_NothingReadAndTheLineItStopsAtMarked()
    {
        foreach (var (text, line) in new[] { ("{\n  \"references\": [\n    { \"path\": \"a\" \n    { \"path\": \"b\" }\n", 4), ("[{ \"path\": \"a\" }]", 1), ("", 1) })
        {
            var extract = Read("tsconfig.json", text);

            Assert.Equal("tsconfig.json", Assert.Single(extract.Nodes).Name);
            Assert.Empty(extract.References);
            Assert.Empty(extract.AddressRoots);
            Assert.Equal(line, Assert.Single(extract.Dynamic).Line);
        }
    }

    /// <summary>
    /// A bare import a path mapping gives finds its file: each place the mapping lists tried in turn until one holds it, a
    /// file's mapping the file it names - as TypeScript and Node try it, with an extension before as a directory's index -
    /// and nothing under it, from the directory the configuration is in.
    /// </summary>
    [Fact]
    public void ABareImportAPathMappingGives_FindsTheFirstPlaceThatHoldsIt()
    {
        var linked = NpmExtractorTests.Link(
            ("web/tsconfig.json", """{ "compilerOptions": { "paths": { "@app/*": ["src/*", "generated/*"], "@config": ["config/index.ts"], "@settings": ["settings"], "jquery": ["vendor/jquery/dist/jquery"], "@data": ["data"] } } }"""),
            ("web/data.mjs", "export const data = 1;"),
            ("web/data/index.ts", "export const data = 2;"),
            ("web/settings/index.ts", "export const settings = 1;"),
            ("web/vendor/jquery/dist/jquery.js", "var $ = 1;"),
            ("web/src/util.ts", "export function util() {}"),
            ("web/generated/util.ts", "export function util() {}"),
            ("web/generated/api.ts", "export function api() {}"),
            ("web/config/index.ts", "export const config = 1;"),
            ("web/config/more.ts", "export const more = 1;"),
            ("web/main.ts", """
                import { util } from '@app/util';
                import { api } from '@app/api';
                import { config } from '@config';
                import { more } from '@config/more';
                export function go() { util(); api(); }
                import { settings } from '@settings';
                import $ from 'jquery';
                import { data } from '@data';
                """));

        Assert.Equal(
            [
                ("file:web/main.ts", "file:web/src/util.ts", 1), ("file:web/main.ts", "file:web/generated/api.ts", 2), ("file:web/main.ts", "file:web/config/index.ts", 3),
                ("file:web/main.ts", "file:web/settings/index.ts", 6), ("file:web/main.ts", "file:web/vendor/jquery/dist/jquery.js", 7),
                ("file:web/main.ts", "file:web/data.mjs", 8),
            ],
            linked.Edges.Where(edge => edge.Kind == CodeEdgeKinds.Imports).Select(edge => (edge.From, edge.To, edge.Line)));
        Assert.Contains(new CodeEdge("typescript:web/main.ts.go", "typescript:web/src/util.ts.util", CodeEdgeKinds.Calls, 5, CodeConfidences.Unique), linked.Edges);
        Assert.Contains(new CodeEdge("typescript:web/main.ts.go", "typescript:web/generated/api.ts.api", CodeEdgeKinds.Calls, 5, CodeConfidences.Unique), linked.Edges);
    }

    /// <summary>
    /// A project's path mappings are its own files': two apps mapping one pattern each find their own file, a file in no
    /// project's directory finds none by them, the nearest project's mapping comes before a farther one's, and a mapping
    /// before a package of the repository by the same name.
    /// </summary>
    [Fact]
    public void AProjectsPathMappings_AreItsOwnFiles_TheNearestFirst_BeforeAPackage()
    {
        var linked = NpmExtractorTests.Link(
            ("apps/a/tsconfig.json", """{ "compilerOptions": { "paths": { "@/*": ["./src/*"] } } }"""),
            ("apps/a/src/components/Button.tsx", "export function Button() {}"),
            ("apps/b/tsconfig.json", """{ "compilerOptions": { "paths": { "@/*": ["./src/*"] } } }"""),
            ("apps/b/src/components/Button.tsx", "export function Button() {}"),
            ("apps/b/src/page.tsx", "import { Button } from '@/components/Button';"),
            ("tools/run.ts", "import { Button } from '@/components/Button';"),
            ("libs/tsconfig.json", """{ "compilerOptions": { "paths": { "@acme/ui": ["shim/ui.ts"], "@x/*": ["outer/*"] } } }"""),
            ("libs/shim/ui.ts", "export const ui = 1;"),
            ("libs/outer/y.ts", "export const y = 1;"),
            ("libs/inner/tsconfig.json", """{ "compilerOptions": { "paths": { "@x/*": ["near/*"] } } }"""),
            ("libs/inner/near/y.ts", "export const y = 1;"),
            ("libs/inner/use.ts", "import { y } from '@x/y';\nimport { ui } from '@acme/ui';"),
            ("packages/ui/package.json", """{ "name": "@acme/ui", "main": "index.ts" }"""),
            ("packages/ui/index.ts", "export const ui = 2;"));

        Assert.Equal(
            [
                ("file:apps/b/src/page.tsx", "file:apps/b/src/components/Button.tsx", 1), ("file:libs/inner/use.ts", "file:libs/inner/near/y.ts", 1),
                ("file:libs/inner/use.ts", "file:libs/shim/ui.ts", 2),
            ],
            linked.Edges.Where(edge => edge.Kind == CodeEdgeKinds.Imports).Select(edge => (edge.From, edge.To, edge.Line)));
    }

    /// <summary>A configuration at the tree's top maps the bare imports of every file in the tree.</summary>
    [Fact]
    public void ATopProjectsPathMappings_AreEveryFiles()
    {
        var linked = NpmExtractorTests.Link(
            ("tsconfig.json", """{ "compilerOptions": { "paths": { "@/*": ["src/*"] } } }"""),
            ("src/util.ts", "export function util() {}"),
            ("tools/run.ts", "import { util } from '@/util';"));

        Assert.Equal(
            [("file:tools/run.ts", "file:src/util.ts", 1)],
            linked.Edges.Where(edge => edge.Kind == CodeEdgeKinds.Imports).Select(edge => (edge.From, edge.To, edge.Line)));
    }

    private static FileExtract Read(string path, string text) => new TsconfigExtractor().Extract(new CodeFileInput(path, text));
}
