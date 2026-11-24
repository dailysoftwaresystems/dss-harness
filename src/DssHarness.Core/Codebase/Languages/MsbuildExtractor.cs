using System.Text.RegularExpressions;
using DssHarness.Core.Text;
using System.Xml;

namespace DssHarness.Core.Codebase.Languages;

/// <summary>
/// MSBuild's projects and solutions: a <c>.csproj</c> or <c>.slnx</c> is one <c>project</c> node over the whole file,
/// named by its file name, its header its root element's line and a project's summary the first sentence of its
/// <c>&lt;Description&gt;</c>, with a <c>projectReference</c> to each project it names - a project's
/// <c>&lt;ProjectReference Include&gt;</c>, a solution's <c>&lt;Project Path&gt;</c> - by that project's file name. Each
/// property a project's own <c>PropertyGroup</c> declares is a key of the project's alone, its value as its summary, in a
/// closed scope no other file sees. Each <c>$(Name)</c> an attribute's value or an element's text writes is a <c>uses</c>
/// of the property it reads (<see cref="CodeQualifierKinds.Key"/>): the project's own of that name - those declared before
/// it, where it is read as the properties are, in a <c>PropertyGroup</c> or an <c>Import</c>, and every one elsewhere,
/// read once all are - else the key of that name an XML file declares, an imported <c>.props</c> or <c>.targets</c>
/// file's <c>PropertyGroup</c> property among them, which the xml extractor reads. Each <c>&lt;Import Project&gt;</c> is
/// an import of the file it names. The file is read as XML as far as it parses, a comment never, the line it stops at
/// marked dynamic with the reader's reason; nothing MSBuild evaluates is evaluated.
/// </summary>
public sealed class MsbuildExtractor : ICodeGraphExtractor
{
    /// <summary>The language whose keys a <c>$(Name)</c> names where the project declares no property of the name it reads: the xml extractor's, which reads a <c>.props</c>'s properties.</summary>
    private const string XmlKeys = "xml";

    /// <summary>The closed scope a project's own properties are declared in: the project's alone.</summary>
    private const string OwnProperties = "PropertyGroup";

    /// <inheritdoc/>
    public string Language => "msbuild";

    /// <inheritdoc/>
    public int Version => 5;

    /// <inheritdoc/>
    public CodeGraphClaims Claims { get; } = CodeGraphClaims.OfExtensions(".csproj", ".slnx");

    /// <inheritdoc/>
    public bool Outlines => true;

    /// <inheritdoc/>
    public CodeGraphExtractorHelp Help { get; } = new(
        "MSBuild projects (.csproj) and solutions (.slnx): a project node for each, named by its file name, a project's summary from its Description, a projectReference to each project it names that the repository holds, each property its own PropertyGroup declares a key of the project's alone with its value as its summary, each $(Name) an attribute or an element's text writes a uses of the property it reads - the project's own of that name, those declared before it where it is read as properties are (in a PropertyGroup or an Import), every one elsewhere, else the key of that name an XML file declares, a .props or .targets PropertyGroup's property - and each Import Project an import of the file it names",
        [
            ("evaluation", "what MSBuild evaluates: a reference under a condition is taken whatever the condition, a property in a path is left as written, and a property declared under several conditions is an edge of candidates to each declaration a use may read"),
            ("foreign-properties", "a project's own property from any other file - another project, a C# literal of its name: it is the project's alone, and the keys' join never reaches it"),
            ("unkeyed-names", "a $(Name) a comment writes, and a name neither the project nor an XML file declares as a key - a property of the SDK's, one only a C# member or a JSON key is named by: no use"),
            ("wildcard-projects", "a project named by a wildcard, and two projects of one file name, which a reference names both of"),
            ("sln-files", "a solution in the older .sln form"),
            new CodeGraphLimit("broken-xml", "what follows the point a file stops reading as XML, a line its extract marks dynamic with the reader's reason") { Signs = [DataNodes.Stop] },
        ]);

    /// <inheritdoc/>
    public FileExtract Extract(CodeFileInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var lines = input.Lines;
        var text = input.Text;
        var index = text.LineIndex;
        var name = CodeGraphIds.FileName(input.Path);
        var solution = name.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase);
        var id = $"{Language}:{input.Path}";
        var references = new List<ExtractReference>();
        var imports = new List<ExtractImport>();
        var dynamic = new List<ExtractDynamic>();
        string? signature = null;
        string? summary = null;

        // The project's own properties, each as its key; those of each name declared so far, the declaration closed; and
        // each reference in the order written, a use of a property with the declarations it reads where it reads those
        // declared before it - every one of its name otherwise, told once the whole file is read.
        var ids = new ExtractIds();
        var properties = new List<ExtractNode>();
        var declared = new Dictionary<string, List<QualifiedName>>(StringComparer.Ordinal);
        var written = new List<(ExtractReference Reference, IReadOnlyList<QualifiedName>? Reads)>();
        var open = new List<string>();
        (QualifiedName Id, string Name, int At)? property = null;

        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, IgnoreComments = true, IgnoreProcessingInstructions = true };

        // Each $(Name) of the text between start and end as the file writes it - an entity left as written, so a match is
        // told by the line it stands on - a use of the property of that name it reads: in a PropertyGroup or an Import,
        // read as properties are, of those declared before it; anywhere else, read once every property is, of any.
        void Uses(long start, long end, IReadOnlyList<string> elements)
        {
            var inOrder = elements.Count > 1 && elements[1] is "PropertyGroup" or "Import" or "ImportGroup";
            var between = text.Slice(start, end);

            foreach (var match in Used.EnumerateMatches(between))
            {
                // The match is $( the name ), its name what its brackets hold.
                var name = between.Substring(match.Index + 2, match.Length - 3);
                written.Add((new ExtractReference(id, CodeEdgeKinds.Uses, name, index.LineAt(start + match.Index)), inOrder ? [.. declared.GetValueOrDefault(name) ?? []] : null));
            }
        }

        // The property open, declared once it closes: a use inside it reads the value before it.
        void Declared()
        {
            if (property is { } closing && open.Count == closing.At)
            {
                (declared.TryGetValue(closing.Name, out var held) ? held : declared[closing.Name] = []).Add(closing.Id);
                property = null;
            }
        }

        try
        {
            using var reader = XmlReader.Create(text.Reader(), settings);
            var at = (IXmlLineInfo)reader;
            var describing = false;

            while (reader.Read())
            {
                // An element's text and a CDATA section, each read where the file writes it: a comment, which the reader
                // passes over, is never among them.
                if (reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA && Offset(index, text, at) is { } offset)
                {
                    Uses(offset, Until(text, offset, reader.NodeType == XmlNodeType.CDATA ? "]]>" : "<"), open);
                }

                if (reader.NodeType == XmlNodeType.Text && describing)
                {
                    summary ??= ExtractText.Flat(reader.Value) is { Length: > 0 } described ? ExtractText.Summary(described) : null;
                }

                // A property's value is its key's summary: its text on one line, whole.
                if (reader.NodeType == XmlNodeType.Text && property is not null && properties[^1].Summary is null && ExtractText.Value(reader.Value) is { } value)
                {
                    properties[^1] = properties[^1] with { Summary = value };
                }

                describing = false;

                if (reader.NodeType == XmlNodeType.EndElement)
                {
                    open.RemoveAt(open.Count - 1);
                    Declared();
                }

                if (reader.NodeType != XmlNodeType.Element)
                {
                    continue;
                }

                var line = FileExtract.LineWithin(at.LineNumber, lines.Count);
                var element = reader.LocalName;
                var empty = reader.IsEmptyElement;

                // A property a project's own PropertyGroup declares - one of the root Project's - is a key of the project's alone.
                if (!solution && open is ["Project", "PropertyGroup"])
                {
                    var place = DataPlace.Named(element);
                    var path = ExtractText.Whole(place.Written);
                    var key = new ExtractNode(ids.Next($"{id}#{path}"), CodeNodeKinds.Member, place.NodeName, path, line, line, ExtractText.Signature(path))
                    {
                        SubKind = CodeSubKinds.Key,
                        Scope = OwnProperties,
                        Parent = id,
                    };
                    properties.Add(key);
                    property = (key.Id, element, open.Count);
                }

                // The root element's line, as the project's header.
                signature ??= ExtractText.Signature(ExtractText.Flat(lines[line - 1]));
                describing = !solution && reader.LocalName == "Description" && !reader.IsEmptyElement;

                var named = (solution, element) switch
                {
                    (false, "ProjectReference") => reader.GetAttribute("Include"),
                    (true, "Project") => reader.GetAttribute("Path"),
                    _ => null,
                };

                foreach (var project in (named ?? string.Empty).Split(';').Select(ProjectName).OfType<string>())
                {
                    written.Add((new ExtractReference(id, CodeEdgeKinds.ProjectReference, project, line), null));
                }

                if (!solution && reader.LocalName == "Import" && reader.GetAttribute("Project") is { } imported && !string.IsNullOrWhiteSpace(imported))
                {
                    imports.Add(new ExtractImport(CodeImportForms.Relative, imported.Trim(), line));
                }

                // Each attribute's value, read where the file writes it: MSBuild writes a property into an attribute as
                // often as into an element's text - read as the element it is written on is.
                List<string> within = [.. open, element];

                for (var more = reader.MoveToFirstAttribute(); more; more = reader.MoveToNextAttribute())
                {
                    if (Offset(index, text, at) is { } attribute && Value(text, attribute) is { } attributeValue)
                    {
                        Uses(attributeValue.Start, attributeValue.End, within);
                    }
                }

                reader.MoveToElement();

                if (!empty)
                {
                    open.Add(element);
                }
                else
                {
                    Declared();
                }
            }
        }
        catch (XmlException ex)
        {
            // What follows the first fault is not read: the file is graphed as far as it parses, and its extract says where
            // it stopped, so a reference missing after it is never taken for one the project does not make.
            var stopped = XmlExtractor.Stopped(ex, lines, refusesDtd: false);
            dynamic.Add(new ExtractDynamic(FileExtract.LineWithin(ex.LineNumber, lines.Count), stopped.Why, stopped.Kind));
        }

        var node = new ExtractNode(id, CodeNodeKinds.Project, name, input.Path, 1, Math.Max(lines.Count, 1), signature ?? name)
        {
            Summary = summary,
        };

        // Each use, in the order written: of the project's own property where it reads one - every declaration of the name
        // by its name in the project's scope, those before it alone by each - else of the key an XML file declares.
        var own = properties.GroupBy(key => key.Name, StringComparer.Ordinal).ToDictionary(named => named.Key, named => named.Count(), StringComparer.Ordinal);

        foreach (var (reference, reads) in written)
        {
            if (reference.Kind != CodeEdgeKinds.Uses)
            {
                references.Add(reference);
            }
            else if (!own.TryGetValue(reference.Name, out var count) || reads is { Count: 0 })
            {
                references.Add(reference with { Qualifier = new ExtractQualifier(CodeQualifierKinds.Key, XmlKeys) });
            }
            else if (reads is null || reads.Count == count)
            {
                references.Add(reference with { Qualifier = new ExtractQualifier(CodeQualifierKinds.Key, Language) });
            }
            else
            {
                references.AddRange(reads.Select(read => reference with { Qualifier = new ExtractQualifier(CodeQualifierKinds.Own, read) }));
            }
        }

        return new FileExtract([node, .. properties])
        {
            Scopes = properties.Count > 0 ? [new ExtractScope(OwnProperties, Open: false)] : [],
            References = references,
            Imports = imports,
            Dynamic = dynamic,
        };
    }

    /// <summary>
    /// Where in <paramref name="text"/> the node the reader stands on is written - an attribute's name, a text's first
    /// character, a CDATA section's first after its opening - from the line and column the reader says, which count lines
    /// as the core does (<see cref="CodeFileText.Lines"/>); <see langword="null"/> where the reader says none.
    /// </summary>
    private static long? Offset(CodeLineIndex index, CodeText text, IXmlLineInfo at)
    {
        if (!at.HasLineInfo() || at.LineNumber < 1 || at.LineNumber > index.Count || at.LinePosition < 1)
        {
            return null;
        }

        var offset = index.StartOf(at.LineNumber) + at.LinePosition - 1;
        return offset <= text.Length ? offset : null;
    }

    /// <summary>Where <paramref name="ending"/> first stands in <paramref name="text"/> from <paramref name="start"/> on, or the text's end.</summary>
    private static long Until(CodeText text, long start, string ending)
        => text.IndexOf(ending, start) is var end and >= 0 ? end : text.Length;

    /// <summary>
    /// The written value of the attribute whose name begins at <paramref name="name"/>: what its quotes hold, past its
    /// <c>=</c>; <see langword="null"/> where the text there is no attribute so written.
    /// </summary>
    private static (long Start, long End)? Value(CodeText text, long name)
    {
        var equals = text.IndexOf('=', name);

        if (equals < 0)
        {
            return null;
        }

        var quote = equals + 1;

        while (quote < text.Length && char.IsWhiteSpace(text[quote]))
        {
            quote++;
        }

        if (quote >= text.Length || text[quote] is not ('"' or '\''))
        {
            return null;
        }

        var close = text.IndexOf(text[quote], quote + 1);
        return close < 0 ? null : (quote + 1, close);
    }

    /// <summary>A <c>$(Name)</c> as MSBuild writes it: a property's name, never a property expression's opening bracket.</summary>
    private static readonly Regex Used = LinearRegex.Compile(@"\$\([A-Za-z_][A-Za-z0-9_.\-]*\)");

    /// <summary>The file name of the project <paramref name="path"/> names, as its node is named; <see langword="null"/> where it names none, or names it by a wildcard.</summary>
    private static string? ProjectName(string path)
    {
        var name = CodeGraphIds.FileName(path.Trim().Replace('\\', '/'));

        return name.Length == 0 || name.Contains('*', StringComparison.Ordinal) || name.Contains('?', StringComparison.Ordinal) ? null : name;
    }
}
