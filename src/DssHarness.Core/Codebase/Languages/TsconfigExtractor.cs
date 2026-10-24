using System.Text.Json;

namespace DssHarness.Core.Codebase.Languages;

/// <summary>
/// A TypeScript project's <c>tsconfig.json</c>, or a JavaScript one's <c>jsconfig.json</c> - comments and trailing commas
/// allowed, as TypeScript reads them: one <c>project</c> node over the whole file, named by its path, with a
/// <c>projectReference</c> to each project its <c>references</c> name - a directory's <c>tsconfig.json</c>, or the file
/// named - and local address roots for its <c>compilerOptions.paths</c>, from its <c>baseUrl</c>, which the files in its
/// directory and below resolve by before any other root: <c>@app/*</c> mapped to <c>src/*</c> a directory and every
/// address under it, a pattern with no <c>*</c> one file - tried as TypeScript and Node try a path, with their
/// extensions and as a directory's <c>index</c> - each place a pattern lists tried in turn. Where the file is no JSON object, the line it
/// stops at is marked dynamic.
/// </summary>
public sealed class TsconfigExtractor : ICodeGraphExtractor
{
    /// <inheritdoc/>
    public string Language => "tsconfig";

    /// <inheritdoc/>
    public int Version => 1;

    /// <inheritdoc/>
    public CodeGraphClaims Claims { get; } = new([new("tsconfig.json"), new("jsconfig.json")], []);

    /// <inheritdoc/>
    public bool Outlines => true;

    /// <inheritdoc/>
    public CodeGraphExtractorHelp Help { get; } = new(
        "TypeScript and JavaScript projects (tsconfig.json, jsconfig.json, comments and trailing commas allowed): a project node for each, named by its path, a projectReference to each project its references name, and address roots for its compilerOptions.paths from its baseUrl, which the files in its directory and below resolve a bare import by before any other root, each place a pattern lists tried in turn",
        [
            "what a configuration takes by extends - its baseUrl, its paths - and a configuration named otherwise (tsconfig.app.json, tsconfig.base.json): not read, so a mapping is taken from the file's own baseUrl or its directory, and the paths such a file declares are no roots",
            "the configuration that holds a file by its include, files or exclude: a file resolves by the mappings of every configuration in whose directory it is, the nearest first",
            "a baseUrl with no paths: a bare import is not looked for under it",
            $"a path mapping that is {ProjectRoots.Unread}, and the catch-all *: not read",
            JsonOutline.Unread,
        ]);

    /// <inheritdoc/>
    public FileExtract Extract(CodeFileInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var lines = input.Lines;
        var id = $"{Language}:{input.Path}";
        var root = JsonOutline.Load(input.Text, lenient: true, out var stop);
        var references = new List<ExtractReference>();

        foreach (var reference in root?["references"] is { Kind: JsonValueKind.Array } listed ? listed.Items! : [])
        {
            if (reference["path"]?.NonEmpty is { } path && Referenced(input.Path, path) is { } project)
            {
                references.Add(new ExtractReference(id, CodeEdgeKinds.ProjectReference, project, FileExtract.LineWithin(reference.Line, lines.Count)));
            }
        }

        var options = root?["compilerOptions"];
        var baseUrl = options?["baseUrl"]?.NonEmpty ?? ".";
        var roots = new List<ExtractAddressRoot>();

        foreach (var pattern in options?["paths"] is { Kind: JsonValueKind.Object } paths ? paths.Properties! : [])
        {
            foreach (var place in pattern.Value is { Kind: JsonValueKind.Array } places ? places.Items!.Select(item => item.NonEmpty).OfType<string>() : [])
            {
                ProjectRoots.AddMapping(roots, pattern.Name, place, baseUrl, local: true);
            }
        }

        var node = new ExtractNode(id, CodeNodeKinds.Project, input.Path, input.Path, 1, Math.Max(lines.Count, 1), CodeGraphIds.FileName(input.Path));

        return new FileExtract([node])
        {
            References = references,
            AddressRoots = roots,
            Dynamic = stop is null ? [] : [new ExtractDynamic(FileExtract.LineWithin(stop.Line, lines.Count), stop.Why)],
        };
    }

    /// <summary>
    /// The path of the project a reference of the file at <paramref name="path"/> names by <paramref name="written"/>, as
    /// its node is named: the file named, or the <c>tsconfig.json</c> of the directory named; <see langword="null"/> where it
    /// climbs out of the tree.
    /// </summary>
    private static string? Referenced(string path, string written)
        => CodeGraphIds.Beside(path, written.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? written : $"{written.TrimEnd('/')}/tsconfig.json");
}
