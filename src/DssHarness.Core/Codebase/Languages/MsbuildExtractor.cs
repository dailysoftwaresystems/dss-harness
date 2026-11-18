using System.Text.RegularExpressions;
using System.Xml;

namespace DssHarness.Core.Codebase.Languages;

/// <summary>
/// MSBuild's projects and solutions: a <c>.csproj</c> or <c>.slnx</c> is one <c>project</c> node over the whole file,
/// named by its file name, its header its root element's line and a project's summary the first sentence of its
/// <c>&lt;Description&gt;</c>, with a <c>projectReference</c> to each project it names - a project's
/// <c>&lt;ProjectReference Include&gt;</c>, a solution's <c>&lt;Project Path&gt;</c> - by that project's file name. Each
/// <c>$(Name)</c> an attribute's value or an element's text writes is a <c>uses</c> of the configuration key of that name
/// an XML file declares (<see cref="CodeQualifierKinds.Key"/>) - an imported <c>.props</c> or <c>.targets</c> file's
/// <c>PropertyGroup</c> property among them, which the xml extractor reads - and each <c>&lt;Import Project&gt;</c> an
/// import of the file it names. The file is read as XML as far as it parses, a comment never, the line it stops at marked
/// dynamic with the reader's reason; nothing MSBuild evaluates is evaluated.
/// </summary>
public sealed partial class MsbuildExtractor : ICodeGraphExtractor
{
    /// <summary>The language whose keys a <c>$(Name)</c> names: the xml extractor's, which reads a <c>.props</c>'s properties.</summary>
    private const string XmlKeys = "xml";

    /// <inheritdoc/>
    public string Language => "msbuild";

    /// <inheritdoc/>
    public int Version => 3;

    /// <inheritdoc/>
    public CodeGraphClaims Claims { get; } = CodeGraphClaims.OfExtensions(".csproj", ".slnx");

    /// <inheritdoc/>
    public bool Outlines => true;

    /// <inheritdoc/>
    public CodeGraphExtractorHelp Help { get; } = new(
        "MSBuild projects (.csproj) and solutions (.slnx): a project node for each, named by its file name, a project's summary from its Description, a projectReference to each project it names that the repository holds, each $(Name) an attribute or an element's text writes a uses of the key of that name an XML file declares - a .props or .targets PropertyGroup's property - and each Import Project an import of the file it names",
        [
            "what MSBuild evaluates: a reference under a condition is taken whatever the condition, and a property in a path is left as written",
            "a property the project itself declares in its own PropertyGroup: no key of it here - the xml extractor reads the imported .props and .targets files' as keys",
            "a $(Name) a comment writes, and a name no XML file declares as a key - a property of the SDK's, one only a C# member or a JSON key is named by: no use",
            "a project named by a wildcard, and two projects of one file name, which a reference names both of",
            "a solution in the older .sln form",
            "what follows the point a file stops reading as XML, a line its extract marks dynamic with the reader's reason",
        ]);

    /// <inheritdoc/>
    public FileExtract Extract(CodeFileInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var lines = input.Lines;
        var text = input.Text;
        var index = CodeFileText.LineIndex(text);
        var name = CodeGraphIds.FileName(input.Path);
        var solution = name.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase);
        var id = $"{Language}:{input.Path}";
        var references = new List<ExtractReference>();
        var imports = new List<ExtractImport>();
        var dynamic = new List<ExtractDynamic>();
        string? signature = null;
        string? summary = null;

        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, IgnoreComments = true, IgnoreProcessingInstructions = true };

        // Each $(Name) of the text between start and end as the file writes it - an entity left as written, so a match is
        // told by the line it stands on - a use of the key of that name an XML file declares.
        void Uses(int start, int end)
        {
            foreach (var match in Used().EnumerateMatches(text.AsSpan(start, end - start)))
            {
                // The match is $( the name ), its name what its brackets hold.
                references.Add(new ExtractReference(id, CodeEdgeKinds.Uses, text.Substring(start + match.Index + 2, match.Length - 3), index.LineAt(start + match.Index))
                {
                    Qualifier = new ExtractQualifier(CodeQualifierKinds.Key, XmlKeys),
                });
            }
        }

        try
        {
            using var reader = XmlReader.Create(new StringReader(text), settings);
            var at = (IXmlLineInfo)reader;
            var describing = false;

            while (reader.Read())
            {
                // An element's text and a CDATA section, each read where the file writes it: a comment, which the reader
                // passes over, is never among them.
                if (reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA && Offset(index, text, at) is { } written)
                {
                    Uses(written, Until(text, written, reader.NodeType == XmlNodeType.CDATA ? "]]>" : "<"));
                }

                if (reader.NodeType == XmlNodeType.Text && describing)
                {
                    summary ??= ExtractText.Flat(reader.Value) is { Length: > 0 } described ? ExtractText.Summary(described) : null;
                }

                describing = false;

                if (reader.NodeType != XmlNodeType.Element)
                {
                    continue;
                }

                var line = FileExtract.LineWithin(at.LineNumber, lines.Count);

                // The root element's line, as the project's header.
                signature ??= ExtractText.Signature(ExtractText.Flat(lines[line - 1]));
                describing = !solution && reader.LocalName == "Description" && !reader.IsEmptyElement;

                var named = (solution, reader.LocalName) switch
                {
                    (false, "ProjectReference") => reader.GetAttribute("Include"),
                    (true, "Project") => reader.GetAttribute("Path"),
                    _ => null,
                };

                foreach (var project in (named ?? string.Empty).Split(';').Select(ProjectName).OfType<string>())
                {
                    references.Add(new ExtractReference(id, CodeEdgeKinds.ProjectReference, project, line));
                }

                if (!solution && reader.LocalName == "Import" && reader.GetAttribute("Project") is { } imported && !string.IsNullOrWhiteSpace(imported))
                {
                    imports.Add(new ExtractImport(CodeImportForms.Relative, imported.Trim(), line));
                }

                // Each attribute's value, read where the file writes it: MSBuild writes a property into an attribute as
                // often as into an element's text.
                for (var more = reader.MoveToFirstAttribute(); more; more = reader.MoveToNextAttribute())
                {
                    if (Offset(index, text, at) is { } attribute && Value(text, attribute) is { } value)
                    {
                        Uses(value.Start, value.End);
                    }
                }

                reader.MoveToElement();
            }
        }
        catch (XmlException ex)
        {
            // What follows the first fault is not read: the file is graphed as far as it parses, and its extract says where
            // it stopped, so a reference missing after it is never taken for one the project does not make.
            dynamic.Add(new ExtractDynamic(FileExtract.LineWithin(ex.LineNumber, lines.Count), XmlExtractor.Stopped(ex, lines, refusesDtd: false)));
        }

        var node = new ExtractNode(id, CodeNodeKinds.Project, name, input.Path, 1, Math.Max(lines.Count, 1), signature ?? name)
        {
            Summary = summary,
        };

        return new FileExtract([node]) { References = references, Imports = imports, Dynamic = dynamic };
    }

    /// <summary>
    /// Where in <paramref name="text"/> the node the reader stands on is written - an attribute's name, a text's first
    /// character, a CDATA section's first after its opening - from the line and column the reader says, which count lines
    /// as the core does (<see cref="CodeFileText.Lines"/>); <see langword="null"/> where the reader says none.
    /// </summary>
    private static int? Offset(CodeLineIndex index, string text, IXmlLineInfo at)
    {
        if (!at.HasLineInfo() || at.LineNumber < 1 || at.LineNumber > index.Starts.Count || at.LinePosition < 1)
        {
            return null;
        }

        var offset = index.Starts[at.LineNumber - 1] + at.LinePosition - 1;
        return offset <= text.Length ? offset : null;
    }

    /// <summary>Where <paramref name="ending"/> first stands in <paramref name="text"/> from <paramref name="start"/> on, or the text's end.</summary>
    private static int Until(string text, int start, string ending)
        => text.IndexOf(ending, start, StringComparison.Ordinal) is var end and >= 0 ? end : text.Length;

    /// <summary>
    /// The written value of the attribute whose name begins at <paramref name="name"/>: what its quotes hold, past its
    /// <c>=</c>; <see langword="null"/> where the text there is no attribute so written.
    /// </summary>
    private static (int Start, int End)? Value(string text, int name)
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
    [GeneratedRegex(@"\$\([A-Za-z_][A-Za-z0-9_.\-]*\)", RegexOptions.CultureInvariant)]
    private static partial Regex Used();

    /// <summary>The file name of the project <paramref name="path"/> names, as its node is named; <see langword="null"/> where it names none, or names it by a wildcard.</summary>
    private static string? ProjectName(string path)
    {
        var name = CodeGraphIds.FileName(path.Trim().Replace('\\', '/'));

        return name.Length == 0 || name.Contains('*', StringComparison.Ordinal) || name.Contains('?', StringComparison.Ordinal) ? null : name;
    }
}
