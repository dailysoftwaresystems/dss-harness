using DssHarness.Core.Yaml;
using YamlDotNet.RepresentationModel;

namespace DssHarness.Core.Codebase.Languages;

/// <summary>
/// A Dart package's <c>pubspec.yaml</c>: one <c>project</c> node over the whole file, named by the package's <c>name</c> -
/// its header the line that names it, its summary the first sentence of its <c>description</c> - with a
/// <c>projectReference</c> to each package it depends on by a <c>path:</c>, by that package's name, and the address root
/// that gives Dart's imports their meaning: <c>package:&lt;name&gt;/</c> is the <c>lib/</c> directory beside it. The file
/// is read through the repository's YAML reader; where it is no mapping of keys, the line it stops at is marked dynamic.
/// </summary>
public sealed class PubspecExtractor : ICodeGraphExtractor
{
    /// <summary>What a pubspec is held to before it is read into nodes.</summary>
    private static readonly YamlBounds Bounds = new(
        MaxDepth: 32,
        NoAlias: "a pubspec writes each entry out, so what it says is what it reads",
        TooDeep: "no pubspec does");

    /// <summary>The sections that name a package's dependencies.</summary>
    private static readonly string[] Sections = ["dependencies", "dev_dependencies", "dependency_overrides"];

    /// <inheritdoc/>
    public string Language => "pubspec";

    /// <inheritdoc/>
    public int Version => 1;

    /// <inheritdoc/>
    public CodeGraphClaims Claims { get; } = new([new("pubspec.yaml")], []);

    /// <inheritdoc/>
    public bool Outlines => true;

    /// <inheritdoc/>
    public CodeGraphExtractorHelp Help { get; } = new(
        "Dart packages (pubspec.yaml): a project node for each, named by its name, its summary from its description, a projectReference to each package it depends on by a path: that the repository holds, and the address root package:<name>/ its lib/ directory is",
        [
            "a dependency from a package host, from git or from an SDK: no project of the repository",
            "a workspace's members (workspace:), and resolution: workspace: what a root pubspec lists is not read as references",
            "a file that is no mapping of keys - one that does not parse, holds an alias, is empty, holds several documents or nests past the reader's bounds: nothing in it is read, the line it stops at marked dynamic",
        ]);

    /// <inheritdoc/>
    public FileExtract Extract(CodeFileInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var lines = input.Lines;
        var id = $"{Language}:{input.Path}";
        var root = YamlReading.Load(input.Text, "a pubspec is one document", Bounds, out var stop);
        var name = root is not null && Value(root, "name") is { } named ? named : null;
        var references = new List<ExtractReference>();

        foreach (var section in Sections)
        {
            if (root is null || !root.Children.TryGetValue(new YamlScalarNode(section), out var listed) || listed is not YamlMappingNode dependencies)
            {
                continue;
            }

            foreach (var (key, value) in dependencies.Children)
            {
                if (YamlReading.ScalarOf(key) is { Length: > 0 } package && value is YamlMappingNode source && Value(source, "path") is not null)
                {
                    references.Add(new ExtractReference(id, CodeEdgeKinds.ProjectReference, package, Line(key, lines.Count)));
                }
            }
        }

        var header = root is not null && root.Children.Keys.FirstOrDefault(key => YamlReading.ScalarOf(key) == "name") is { } nameKey
            ? ExtractText.Signature(ExtractText.Flat(lines[Line(nameKey, lines.Count) - 1]))
            : CodeGraphIds.FileName(input.Path);

        var node = new ExtractNode(id, CodeNodeKinds.Project, name ?? CodeGraphIds.FileName(input.Path), input.Path, 1, Math.Max(lines.Count, 1), header.Length > 0 ? header : CodeGraphIds.FileName(input.Path))
        {
            Summary = root is not null && Value(root, "description") is { } description && ExtractText.Flat(description) is { Length: > 0 } flat ? ExtractText.Summary(flat) : null,
        };

        return new FileExtract([node])
        {
            References = references,
            AddressRoots = name is not null ? [new ExtractAddressRoot($"package:{name}/", "lib")] : [],
            Dynamic = stop is null ? [] : [new ExtractDynamic(Math.Clamp(stop.Line, 1, Math.Max(lines.Count, 1)), $"the file stops reading here: {stop.Why}")],
        };
    }

    /// <summary>The scalar <paramref name="mapping"/> holds under <paramref name="key"/>, where it holds one that is not empty.</summary>
    private static string? Value(YamlMappingNode mapping, string key)
        => mapping.Children.TryGetValue(new YamlScalarNode(key), out var value) && YamlReading.ScalarOf(value) is { Length: > 0 } scalar ? scalar : null;

    /// <summary>The line <paramref name="node"/> begins on, within the file's <paramref name="lineCount"/> lines.</summary>
    private static int Line(YamlNode node, int lineCount) => (int)Math.Clamp(node.Start.Line, 1, Math.Max(lineCount, 1));
}
