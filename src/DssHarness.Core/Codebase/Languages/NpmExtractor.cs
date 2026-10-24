using System.Text.Json;

namespace DssHarness.Core.Codebase.Languages;

/// <summary>
/// A Node package's <c>package.json</c>: one <c>project</c> node over the whole file, named by the package's <c>name</c> -
/// its header the line that names it, its summary the first sentence of its <c>description</c> - with a
/// <c>projectReference</c> to each package it depends on, by that package's name, which links where the repository holds a
/// package of that name - a workspace's - and the address roots that give a bare import its meaning: <c>&lt;name&gt;</c>
/// alone is its entry - each file its <c>exports</c> give <c>.</c>, then its <c>source</c>, <c>module</c>, <c>main</c>,
/// <c>types</c> and <c>typings</c>, then the usual <c>index</c> files, each tried as TypeScript and Node try a path and the
/// first the repository holds taken - each subpath its <c>exports</c> name is the file or directory it maps to, and,
/// where it has no <c>exports</c>, <c>&lt;name&gt;/</c> is the package's directory. Where the file is no JSON object, the
/// line it stops at is marked dynamic.
/// </summary>
public sealed class NpmExtractor : ICodeGraphExtractor
{
    /// <summary>The sections that name a package's dependencies.</summary>
    private static readonly string[] Sections = ["dependencies", "devDependencies", "peerDependencies", "optionalDependencies"];

    /// <summary>The fields that name a package's entry, after its <c>exports</c>, in the order they are tried.</summary>
    private static readonly string[] Entries = ["source", "module", "main", "types", "typings"];

    /// <summary>The files a package's entry is where none of its fields names one, as Node and the bundlers try them.</summary>
    private static readonly string[] Indexes = ["index.ts", "index.tsx", "index.js", "index.mjs", "index.cjs", "src/index.ts", "src/index.tsx", "src/index.js"];

    /// <inheritdoc/>
    public string Language => "npm";

    /// <inheritdoc/>
    public int Version => 1;

    /// <inheritdoc/>
    public CodeGraphClaims Claims { get; } = new([new("package.json")], []);

    /// <inheritdoc/>
    public bool Outlines => true;

    /// <inheritdoc/>
    public CodeGraphExtractorHelp Help { get; } = new(
        "Node packages (package.json): a project node for each, named by its name, its summary from its description, a projectReference to each package it depends on that the repository holds - a workspace's - and its address roots: <name> its entry (its exports' ., source, module, main, types, typings, else an index file, each tried as TypeScript and Node try a path, the first the repository holds), each subpath its exports name, and <name>/ its directory where it has no exports",
        [
            "a dependency from a registry, from git or from a URL: no project of the repository, unless one of the repository's packages has its name",
            "an export's conditions (import, require, node, browser, types and the rest): each is tried in the order the file writes them, the first file the repository holds taken",
            $"an exports subpath that is {ProjectRoots.Unread}: not read",
            "a workspace's members as references: each member's package.json is a project of its own, and what workspaces lists is not read",
            JsonOutline.Unread,
        ]);

    /// <inheritdoc/>
    public FileExtract Extract(CodeFileInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return PackageManifest.Read(Language, input, Sections, _ => true, Roots);
    }

    /// <summary>The address roots of the package <paramref name="name"/> the file <paramref name="root"/> is: its entry's files, each subpath its exports name, and its directory where they name none.</summary>
    private static IReadOnlyList<ExtractAddressRoot> Roots(string name, JsonOutlineValue root)
    {
        var roots = new List<ExtractAddressRoot>();
        var exports = root["exports"];
        var subpaths = exports is { Kind: JsonValueKind.Object } && exports.Properties!.Any(property => property.Name.StartsWith('.')) ? exports.Properties! : [];

        // "exports": "./x.js", ["./x.js", ...], { "import": ..., "require": ... } or { ".": ... }: the entry's own.
        var entry = exports is { Kind: JsonValueKind.String or JsonValueKind.Array } || (exports is { Kind: JsonValueKind.Object } && subpaths.Count == 0) ? exports : exports?["."];

        foreach (var file in Files(entry).Concat(Entries.Select(field => root[field]?.NonEmpty).OfType<string>()).Concat(Indexes))
        {
            ProjectRoots.AddFile(roots, name, file, local: false);
        }

        foreach (var subpath in subpaths.Where(property => property.Name.StartsWith("./", StringComparison.Ordinal)))
        {
            var written = subpath.Name[2..];

            // "./*": "./src/*" - a directory and every address under it; "./x": "./src/x.js" - one file.
            foreach (var file in Files(subpath.Value))
            {
                ProjectRoots.AddMapping(roots, $"{name}/{written}", file, ".", local: false);
            }
        }

        // Where a package names what it exports - "exports": null names none - nothing else of it is imported.
        if (exports is null or { Kind: JsonValueKind.Null })
        {
            ProjectRoots.AddDirectory(roots, $"{name}/", ".", local: false);
        }

        return roots;
    }

    /// <summary>Each file an export's target names, in the order the file writes them: a string itself, an object's conditions each in turn, an array's items.</summary>
    private static IEnumerable<string> Files(JsonOutlineValue? target)
    {
        switch (target?.Kind)
        {
            case JsonValueKind.String when target.NonEmpty is { } file:
                yield return file;
                break;

            case JsonValueKind.Object:
                foreach (var file in target.Properties!.SelectMany(property => Files(property.Value)))
                {
                    yield return file;
                }

                break;

            case JsonValueKind.Array:
                foreach (var file in target.Items!.SelectMany(Files))
                {
                    yield return file;
                }

                break;
        }
    }
}
