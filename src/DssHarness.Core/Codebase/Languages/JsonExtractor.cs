using System.Globalization;
using System.Text.Json;

namespace DssHarness.Core.Codebase.Languages;

/// <summary>
/// A JSON data file - <c>.json</c>, <c>.jsonc</c> and <c>.json5</c>, comments and trailing commas allowed - read as the
/// data it holds, its nodes made as every data file's are (<see cref="DataNodes"/>): each top-level member that holds
/// others a <c>section</c> over its lines, and each scalar key anywhere down to <see cref="DataNodes.MostDepth"/> places of
/// its path a <c>member</c> SubKind <c>key</c> named by its path (<c>targets.x64.callingConvention</c>, an item by its place
/// <c>limits[1].value</c>, a name that is empty or white space in brackets <c>packages[""].name</c>), its value as its
/// summary. A <c>$schema</c> or a <c>$ref</c> that names another file by a path relative to this one - its <c>#</c> pointer
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
        "JSON data files (.json, .jsonc, .json5, comments and trailing commas allowed): each scalar key anywhere in the file as a key node named by its path (an item by its place, [0]; a name empty or of white space in brackets, [\"\"]) with its value as its summary, each top-level member that holds others as a section over its lines, and a $schema or a $ref naming another file by a relative path - its # pointer left out - as an import of it",
        [
            "what JSON5 writes that JSON does not - an unquoted name, a single-quoted string, a hexadecimal number: where the reader stops, nothing in it is read, the line it stops at marked dynamic with the reader's reason",
            $"a key nested past {DataNodes.MostDepth.ToString(CultureInfo.InvariantCulture)} places of its path, and a file of more than {FileExtract.MostKeys.ToString(CultureInfo.InvariantCulture)} keys and sections: the first {FileExtract.MostKeys.ToString(CultureInfo.InvariantCulture)} are kept, the rest counted, and an answer about the file says how many",
            "a key's value past the cut of a summary; a secret a key holds is kept as the file writes it, as reading the file shows it, and an answer masks only the run's own secrets, as every tool's answer does",
            "a $ref of a pointer into this file, of an address with a scheme (https:, urn:) or of a rooted path: no import",
            "the named manifests - package.json, tsconfig.json, jsconfig.json, composer.json - which their own extractors read",
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
            var value = new Value(root);
            nodes.Walk(value);
            Imports(root, lines.Count, imports);
        }

        if (stop is not null)
        {
            nodes.Stopped(stop.Line, stop.Detail is { } detail ? $"the file stops reading as JSON here ({detail}), and nothing of it is read" : stop.Why);
        }

        return new FileExtract([.. nodes.Nodes])
        {
            Imports = imports,
            KeysBeyond = nodes.Beyond,
            Dynamic = nodes.Dynamic,
        };
    }

    /// <summary>
    /// Each <c>$schema</c> and <c>$ref</c> anywhere in <paramref name="value"/> that names another file by a path relative to
    /// this one, as an import of it: its <c>#</c> pointer into that file and its <c>?</c> query left out; one that is a
    /// pointer into this file alone, an address with a scheme, a rooted path or a protocol-relative one names none.
    /// </summary>
    private static void Imports(JsonOutlineValue value, int lineCount, List<ExtractImport> imports)
    {
        foreach (var property in value.Properties ?? [])
        {
            if (property.Name is "$schema" or "$ref" && property.Value.NonEmpty is { } written && Local(written) is { } target)
            {
                imports.Add(new ExtractImport(CodeImportForms.Relative, target, FileExtract.LineWithin(property.Value.Line, lineCount)));
            }

            Imports(property.Value, lineCount, imports);
        }

        foreach (var item in value.Items ?? [])
        {
            Imports(item, lineCount, imports);
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
        private long? _scalars;

        public bool Holds => value.Kind is JsonValueKind.Object or JsonValueKind.Array;

        public string? Text => value.Text;

        public int End => Math.Max(value.Line, value.End);

        public bool Repeated => false;

        public long Scalars => _scalars ??= Holds ? Members.Sum(member => member.Value.Scalars) : 1;

        public IEnumerable<(DataPlace Place, int Line, IDataValue Value)> Members
            => value.Kind == JsonValueKind.Object
                ? value.Properties!.Select(property => (DataPlace.Named(property.Name), property.Line, (IDataValue)new Value(property.Value)))
                : (value.Items ?? []).Select((item, at) => (DataPlace.At(at), item.Line, (IDataValue)new Value(item)));
    }
}
