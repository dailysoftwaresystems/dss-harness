using System.Text.Json;

namespace DssHarness.Core.Codebase.Languages;

/// <summary>
/// A JSON data file - <c>.json</c>, <c>.jsonc</c> and <c>.json5</c>, comments and trailing commas allowed - read as the
/// data it holds, its nodes made as every data file's are (<see cref="DataNodes"/>): each member that holds others a
/// <c>section</c> over its lines inside the section that holds it, and each scalar key a <c>member</c> SubKind <c>key</c>
/// named by its path (<c>targets.x64.callingConvention</c>, an item by its place <c>limits[1].value</c>, a name that is
/// empty or white space in brackets <c>packages[""].name</c>), its value as its summary - every one, however deep. A <c>$schema</c> or a <c>$ref</c> that names another file by a path relative to this one - its <c>#</c> pointer
/// into that file left out - is an import of it; a pointer into this file alone, an address with a scheme and a rooted path
/// name none. The named manifests - <c>package.json</c>, <c>tsconfig.json</c>, <c>jsconfig.json</c>, <c>composer.json</c> -
/// keep their own extractors, an exact name winning over an extension; where the file stops reading as JSON, nothing of it
/// is read and the line it stops at is marked dynamic, with the reader's reason.
/// </summary>
public sealed class JsonExtractor : ICodeGraphExtractor
{
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
        "JSON data files (.json, .jsonc, .json5, comments and trailing commas allowed): each scalar key anywhere in the file, however deep, as a key node named by its path (an item by its place, [0]; a name empty or of white space in brackets, [\"\"]) with its value as its summary, each member that holds others as a section over its lines inside the one that holds it, and a $schema or a $ref naming another file by a relative path - its # pointer left out - as an import of it",
        [
            ("json5", "what JSON5 writes that JSON does not - an unquoted name, a single-quoted string, a hexadecimal number: where the reader stops, nothing in it is read, the line it stops at marked dynamic with the reader's reason"),
            ("external-refs", "a $ref of a pointer into this file, of an address with a scheme (https:, urn:) or of a rooted path: no import"),
            ("manifests", "the named manifests - package.json, tsconfig.json, jsconfig.json, composer.json - which their own extractors read"),
        ]);

    /// <inheritdoc/>
    public FileExtract Extract(CodeFileInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var lines = input.Lines;
        var root = JsonOutline.LoadAny(input.Text, lenient: true, out var stop);
        var nodes = new DataNodes(Language, input.Path, lines.Count);
        var imports = new List<ExtractImport>();

        if (root is not null)
        {
            nodes.Walk(new Value(root));
            Imports(root, lines.Count, imports);
        }

        if (stop is not null)
        {
            nodes.Stopped(stop.Line, stop.Detail is { } detail ? $"the file stops reading as JSON here ({detail}), and nothing of it is read" : stop.Why);
        }

        return new FileExtract([.. nodes.Nodes])
        {
            Imports = imports,
            Dynamic = nodes.Dynamic,
        };
    }

    /// <summary>
    /// Each <c>$schema</c> and <c>$ref</c> anywhere in <paramref name="root"/> that names another file by a path relative to
    /// this one, as an import of it: its <c>#</c> pointer into that file and its <c>?</c> query left out; one that is a
    /// pointer into this file alone, an address with a scheme, a rooted path or a protocol-relative one names none. Read in
    /// the order the file writes them, however deep, on a stack of its own.
    /// </summary>
    private static void Imports(JsonOutlineValue root, int lineCount, List<ExtractImport> imports)
    {
        // Each value, then what it holds in turn before the value after it: the first written read first, so pushed last.
        // A property's own import comes as its value is reached, before what that value holds.
        var pending = new Stack<(string? Name, JsonOutlineValue Value)>([(null, root)]);

        while (pending.TryPop(out var at))
        {
            if (at.Name is "$schema" or "$ref" && at.Value.NonEmpty is { } written && Local(written) is { } target)
            {
                imports.Add(new ExtractImport(CodeImportForms.Relative, target, FileExtract.LineWithin(at.Value.Line, lineCount)));
            }

            var held = at.Value.Properties?.Select(property => ((string?)property.Name, property.Value))
                ?? (at.Value.Items ?? []).Select(item => ((string?)null, item));

            foreach (var next in held.Reverse())
            {
                pending.Push(next);
            }
        }
    }

    /// <summary>The file <paramref name="written"/> names by a path relative to this one, its pointer and query left out; <see langword="null"/> where it names none.</summary>
    private static string? Local(string written)
    {
        var path = written.Split('#', 2)[0].Split('?', 2)[0];

        return path.Length == 0 || path[0] is '/' or '\\' || path.Contains(':', StringComparison.Ordinal) ? null : path;
    }

    /// <summary>A JSON value as a walk of its keys reads it (<see cref="IDataValue"/>).</summary>
    private sealed class Value(JsonOutlineValue value) : IDataValue
    {
        public bool Holds => value.Kind is JsonValueKind.Object or JsonValueKind.Array;

        public string? Text => value.Text;

        public int End => Math.Max(value.Line, value.End);

        public IEnumerable<(DataPlace Place, int Line, IDataValue Value)> Members
            => value.Kind == JsonValueKind.Object
                ? value.Properties!.Select(property => (DataPlace.Named(property.Name), property.Line, (IDataValue)new Value(property.Value)))
                : (value.Items ?? []).Select((item, at) => (DataPlace.At(at), item.Line, (IDataValue)new Value(item)));
    }
}
