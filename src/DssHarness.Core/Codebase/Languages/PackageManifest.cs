using System.Text.Json;

namespace DssHarness.Core.Codebase.Languages;

/// <summary>
/// What a package manager's JSON manifest - a Node package's <c>package.json</c>, a Composer package's
/// <c>composer.json</c> - declares alike: one <c>project</c> node over the whole file, named by the package's <c>name</c>
/// (the file's name where it has none), its header the line that names it, its summary the first sentence of its
/// <c>description</c>; a <c>projectReference</c> to each package its sections of dependencies name, by that package's
/// name, which links where the repository holds a package of that name; and, where the file is no JSON object, the line
/// it stops at marked dynamic.
/// </summary>
internal static class PackageManifest
{
    /// <summary>Reads the manifest <paramref name="input"/> as <paramref name="language"/>'s.</summary>
    /// <param name="language">The implementation's name, which prefixes the node's id.</param>
    /// <param name="input">The file.</param>
    /// <param name="sections">The sections that name the package's dependencies.</param>
    /// <param name="depends">Whether a name a section lists is a package's, which a reference is made to.</param>
    /// <param name="roots">The address roots the package of a name declares, where its manager's imports resolve by them.</param>
    public static FileExtract Read(
        string language,
        CodeFileInput input,
        IReadOnlyList<string> sections,
        Func<string, bool> depends,
        Func<string, JsonOutlineValue, IReadOnlyList<ExtractAddressRoot>>? roots = null)
    {
        var lines = input.Lines;
        var id = $"{language}:{input.Path}";
        var root = JsonOutline.Load(input.Text, lenient: false, out var stop);
        var named = root?.Properties!.LastOrDefault(property => property.Name == "name" && property.Value.NonEmpty is not null);
        var name = named?.Value.Text;
        var references = new List<ExtractReference>();

        foreach (var section in sections)
        {
            foreach (var dependency in root?[section] is { Kind: JsonValueKind.Object } dependencies ? dependencies.Properties! : [])
            {
                if (dependency.Name.Length > 0 && depends(dependency.Name))
                {
                    references.Add(new ExtractReference(id, CodeEdgeKinds.ProjectReference, dependency.Name, FileExtract.LineWithin(dependency.Line, lines.Count)));
                }
            }
        }

        var header = named is not null ? ExtractText.Signature(ExtractText.Flat(lines[FileExtract.LineWithin(named.Line, lines.Count) - 1])) : string.Empty;

        var node = new ExtractNode(id, CodeNodeKinds.Project, name ?? CodeGraphIds.FileName(input.Path), input.Path, 1, Math.Max(lines.Count, 1), header.Length > 0 ? header : CodeGraphIds.FileName(input.Path))
        {
            Summary = root?["description"]?.NonEmpty is { } description && ExtractText.Flat(description) is { Length: > 0 } flat ? ExtractText.Summary(flat) : null,
        };

        return new FileExtract([node])
        {
            References = references,
            AddressRoots = name is not null && roots is not null ? roots(name, root!) : [],
            Dynamic = stop is null ? [] : [new ExtractDynamic(FileExtract.LineWithin(stop.Line, lines.Count), stop.Why)],
        };
    }
}
