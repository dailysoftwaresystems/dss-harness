using System.Globalization;
using System.Text;
using System.Text.Json;

namespace DssHarness.Core.Codebase.Languages;

/// <summary>
/// A JSON data file - <c>.json</c>, <c>.jsonc</c> and <c>.json5</c>, comments and trailing commas allowed - read as the
/// data it holds: each top-level member that holds others a <c>section</c> over its lines, and each scalar key anywhere
/// down to <see cref="FileExtract.MostKeys"/> of them a <c>member</c> SubKind <c>key</c> named by its path
/// (<c>targets.x64.callingConvention</c>), its value as its summary. A <c>$schema</c> or an external <c>$ref</c> that
/// names a file by a relative path is an import of it. The named manifests - <c>package.json</c>,
/// <c>tsconfig.json</c>, <c>jsconfig.json</c>, <c>composer.json</c> - keep their own extractors, an exact name winning
/// over an extension; where the file stops reading as JSON, the line it stops at is marked dynamic.
/// </summary>
public sealed class JsonExtractor : ICodeGraphExtractor
{
    /// <summary>How deep a file's keys are read: no data file nests past it.</summary>
    private const int MostDepth = 8;

    /// <inheritdoc/>
    public string Language => "json";

    /// <inheritdoc/>
    public int Version => 1;

    /// <inheritdoc/>
    public CodeGraphClaims Claims { get; } = CodeGraphClaims.OfExtensions(".json", ".jsonc", ".json5");

    /// <inheritdoc/>
    public bool Outlines => true;

    /// <inheritdoc/>
    public CodeGraphExtractorHelp Help { get; } = new(
        "JSON data files (.json, .jsonc, .json5, comments and trailing commas allowed): each scalar key anywhere in the file as a key node named by its path with its value as its summary, each top-level member that holds others as a section over its lines, and a $schema or an external $ref naming a file by a relative path as an import of it",
        [
            "what JSON5 writes that JSON does not - an unquoted name, a single-quoted string, a hexadecimal number: where the reader stops, nothing in it is read, the line it stops at marked dynamic",
            $"a key nested past 8 levels, and a file of more than {FileExtract.MostKeys.ToString(CultureInfo.InvariantCulture)} keys: the first {FileExtract.MostKeys.ToString(CultureInfo.InvariantCulture)} are kept, the rest counted",
            "a key's value past the cut of a summary, and a secret a key holds: neither is masked here",
            "the named manifests - package.json, tsconfig.json, jsconfig.json, composer.json - which their own extractors read",
        ]);

    /// <inheritdoc/>
    public FileExtract Extract(CodeFileInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var lines = input.Lines;
        var root = JsonOutline.LoadAny(input.Text, lenient: true, out var stop);
        var nodes = new List<ExtractNode>();
        var imports = new List<ExtractImport>();
        var ids = new ExtractIds();
        var keys = 0;
        var beyond = 0;

        if (root is not null)
        {
            Walk(input.Path, lines.Count, root, [], null, ids, nodes, imports, ref keys, ref beyond);
        }

        // A top-level member's section runs to the line before the next one's, or to the file's end: the file is walked
        // in its own order, so every key of a section stands before the next one begins.
        var sections = nodes.Select((node, at) => (node, at)).Where(held => held.node.Kind == CodeNodeKinds.Section).ToList();

        for (var held = 0; held < sections.Count; held++)
        {
            var start = sections[held].node.Start;
            var end = held + 1 < sections.Count ? sections[held + 1].node.Start - 1 : Math.Max(lines.Count, 1);
            nodes[sections[held].at] = sections[held].node with { End = Math.Max(start, end) };
        }

        return new FileExtract([.. nodes])
        {
            Imports = imports,
            KeysBeyond = beyond,
            Dynamic = stop is null ? [] : [new ExtractDynamic(FileExtract.LineWithin(stop.Line, lines.Count), stop.Why)],
        };
    }

    /// <summary>
    /// Walks <paramref name="value"/> under the key path <paramref name="path"/>: a scalar key a key node named by its
    /// path; an object or array at the top a section over its lines, and each of its members walked in turn; an array
    /// item's path holding its place.
    /// </summary>
    private void Walk(
        string file,
        int lineCount,
        JsonOutlineValue value,
        IReadOnlyList<string> path,
        string? section,
        ExtractIds ids,
        List<ExtractNode> nodes,
        List<ExtractImport> imports,
        ref int keys,
        ref int beyond)
    {
        switch (value.Kind)
        {
            case JsonValueKind.Object:
                foreach (var property in value.Properties!)
                {
                    Member(file, lineCount, property, [.. path, property.Name], section, ids, nodes, imports, ref keys, ref beyond);
                }

                break;

            case JsonValueKind.Array:
                for (var at = 0; at < value.Items!.Count; at++)
                {
                    var place = $"[{at.ToString(CultureInfo.InvariantCulture)}]";
                    Member(file, lineCount, new JsonOutlineProperty(place, value.Items[at].Line, value.Items[at]), [.. path, place], section, ids, nodes, imports, ref keys, ref beyond);
                }

                break;
        }
    }

    /// <summary>One property or item of a walk: a scalar a key, a container at the top a section, and a container below walked on.</summary>
    private void Member(
        string file,
        int lineCount,
        JsonOutlineProperty property,
        IReadOnlyList<string> path,
        string? section,
        ExtractIds ids,
        List<ExtractNode> nodes,
        List<ExtractImport> imports,
        ref int keys,
        ref int beyond)
    {
        var name = property.Name;

        if (property.Value.Kind is JsonValueKind.Object or JsonValueKind.Array)
        {
            if (path.Count == 1)
            {
                var id = ids.Next($"{Language}:{file}#{name}");
                nodes.Add(new ExtractNode(id, CodeNodeKinds.Section, name, $"{file}#{name}", FileExtract.LineWithin(property.Value.Line, lineCount), Math.Max(lineCount, 1), name)
                {
                    Parent = section,
                });
                section = id;
            }

            if (path.Count < MostDepth)
            {
                Walk(file, lineCount, property.Value, path, section, ids, nodes, imports, ref keys, ref beyond);
            }

            return;
        }

        if (name is "$schema" or "$ref" && property.Value.Text is { Length: > 0 } written && written[0] != '#' && !written.Contains("://", StringComparison.Ordinal))
        {
            imports.Add(new ExtractImport(CodeImportForms.Relative, written, FileExtract.LineWithin(property.Value.Line, lineCount)));
        }

        if (keys >= FileExtract.MostKeys)
        {
            beyond++;
            return;
        }

        keys++;
        var line = FileExtract.LineWithin(property.Line, lineCount);
        var key = KeyPath(path);

        nodes.Add(new ExtractNode(ids.Next($"{Language}:{file}#{key}"), CodeNodeKinds.Member, name, key, line, line, key)
        {
            SubKind = "key",
            Parent = section,
            Summary = property.Value.Text is { Length: > 0 } text ? ExtractText.Summary(text) : null,
        });
    }

    /// <summary>The key path of <paramref name="parts"/>: each name joined by a dot, an array item's place held to the part before it.</summary>
    internal static string KeyPath(IReadOnlyList<string> parts)
    {
        var path = new StringBuilder();

        foreach (var part in parts)
        {
            if (part[0] == '[')
            {
                _ = path.Append(part);
            }
            else
            {
                _ = path.Append(path.Length == 0 ? part : $".{part}");
            }
        }

        return path.ToString();
    }
}
