using System.Xml;

namespace DssHarness.Core.Codebase.Languages;

/// <summary>
/// An XML data file - <c>.xml</c>, <c>.config</c>, <c>.props</c>, <c>.targets</c>, <c>.resx</c>, <c>.xaml</c>,
/// <c>.manifest</c> and <c>.nuspec</c>, but an MSBuild project or solution, which its own extractor reads - read as the
/// data it holds: the root element a <c>section</c> over the file, each element with an id-like attribute - <c>name</c>,
/// <c>key</c>, <c>id</c>, <c>x:Name</c> - a <c>member</c> SubKind <c>element</c> named by it, a <c>.resx</c>'s
/// <c>data name=</c> and a <c>.props</c> or <c>.targets</c>'s <c>PropertyGroup</c> children members SubKind
/// <c>key</c> named by the key they are, and a <c>.props</c> or <c>.targets</c>'s <c>Import Project=</c> an import of the
/// file it names. A <c>.xaml</c> root's <c>x:Class</c> makes the root a <c>type</c> marked a part of that C# type, which
/// the linker joins to it. A file with a DTD is refused, nothing in it read; where a file stops reading as XML, the line
/// it stops at is marked dynamic.
/// </summary>
public sealed class XmlExtractor : ICodeGraphExtractor
{
    /// <summary>The attributes an element is named by, in the order they are looked for.</summary>
    private static readonly string[] Named = ["name", "key", "id"];

    /// <inheritdoc/>
    public string Language => "xml";

    /// <inheritdoc/>
    public int Version => 1;

    /// <inheritdoc/>
    public CodeGraphClaims Claims { get; } = CodeGraphClaims.OfExtensions(".xml", ".config", ".props", ".targets", ".resx", ".xaml", ".manifest", ".nuspec");

    /// <inheritdoc/>
    public bool Outlines => true;

    /// <inheritdoc/>
    public CodeGraphExtractorHelp Help { get; } = new(
        "XML data files (.xml, .config, .props, .targets, .resx, .xaml, .manifest, .nuspec): the root element as a section, each element with a name, key, id or x:Name attribute as an element node named by it, a .resx's data name= and a .props or .targets's PropertyGroup children as keys named by the key they are, an Import Project= as an import of the file it names, and a .xaml root's x:Class as a part of that C# type",
        [
            "MSBuild projects and solutions (.csproj, .slnx), which the msbuild extractor reads",
            "a file with a DTD: refused, nothing in it read",
            "what follows the point a file stops reading as XML, a line its extract marks dynamic",
            "an attribute named otherwise than name, key, id or x:Name, and an element with none of them: no node of its own, its lines its parent's",
        ]);

    /// <inheritdoc/>
    public FileExtract Extract(CodeFileInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var lines = input.Lines;
        var name = CodeGraphIds.FileName(input.Path);
        var kind = name.EndsWith(".props", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".targets", StringComparison.OrdinalIgnoreCase) ? "props"
            : name.EndsWith(".resx", StringComparison.OrdinalIgnoreCase) ? "resx"
            : name.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase) ? "xaml"
            : null;
        var nodes = new List<ExtractNode>();
        var imports = new List<ExtractImport>();
        var ids = new ExtractIds();
        var keys = 0;
        var beyond = 0;
        ExtractDynamic? stopped = null;

        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, IgnoreComments = true, IgnoreProcessingInstructions = true };
        var stack = new List<Frame>();

        try
        {
            using var reader = XmlReader.Create(new StringReader(input.Text), settings);
            var at = (IXmlLineInfo)reader;

            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Text)
                {
                    // The text an element holds is its node's summary, cut: a key's value, a header's description.
                    var holder = stack.Where(frame => frame.Node >= 0).Cast<Frame?>().LastOrDefault();

                    if (holder is { } held && nodes[held.Node].Summary is null && ExtractText.Flat(reader.Value) is { Length: > 0 } text)
                    {
                        nodes[held.Node] = nodes[held.Node] with { Summary = ExtractText.Summary(ExtractText.Flat(text)) };
                    }

                    continue;
                }

                if (reader.NodeType == XmlNodeType.EndElement)
                {
                    if (stack.Count > 0)
                    {
                        Close(stack, nodes, FileExtract.LineWithin(at.LineNumber, lines.Count));
                    }

                    continue;
                }

                if (reader.NodeType != XmlNodeType.Element)
                {
                    continue;
                }

                var line = FileExtract.LineWithin(at.LineNumber, lines.Count);
                var element = reader.LocalName;
                var place = string.Join('/', stack.Select(frame => frame.Name).Append(element));
                var parent = stack.Where(frame => frame.Node >= 0).Cast<Frame?>().LastOrDefault();

                // The root's x:Class makes the root a part of the C# type it names; every other root is a section.
                var xClass = stack.Count == 0 && kind == "xaml" ? Attribute(reader, "x:Class") : null;
                var named = xClass is null && stack.Count > 0 ? Attribute(reader, Named) ?? Attribute(reader, "x:Name") : null;
                var key = stack.Count > 0 && stack[^1].Name == "PropertyGroup" && kind == "props" ? element : null;
                var data = element == "data" && kind == "resx" ? Attribute(reader, "name") : null;

                int node = -1;

                if (xClass is not null)
                {
                    node = nodes.Count;
                    nodes.Add(new ExtractNode(ids.Next($"{Language}:{xClass}"), CodeNodeKinds.Type, xClass[(xClass.LastIndexOf('.') + 1)..], xClass, line, line, xClass)
                    {
                        SubKind = "view",
                        Part = true,
                    });
                }
                else if (stack.Count == 0)
                {
                    node = nodes.Count;
                    nodes.Add(new ExtractNode(ids.Next($"{Language}:{input.Path}#{element}"), CodeNodeKinds.Section, element, $"{input.Path}#{element}", line, line, element));
                }
                else if (data is { Length: > 0 })
                {
                    if (keys >= FileExtract.MostKeys)
                    {
                        beyond++;
                    }
                    else
                    {
                        keys++;
                        node = nodes.Count;
                        nodes.Add(new ExtractNode(ids.Next($"{Language}:{input.Path}#{data}"), CodeNodeKinds.Member, data, data, line, line, data)
                        {
                            SubKind = "key",
                            Parent = parent?.Node is { } holder ? nodes[holder].Id : null,
                        });
                    }
                }
                else if (key is not null)
                {
                    if (keys >= FileExtract.MostKeys)
                    {
                        beyond++;
                    }
                    else
                    {
                        keys++;
                        node = nodes.Count;
                        nodes.Add(new ExtractNode(ids.Next($"{Language}:{input.Path}#{element}"), CodeNodeKinds.Member, element, element, line, line, element)
                        {
                            SubKind = "key",
                            Parent = parent?.Node is { } holder ? nodes[holder].Id : null,
                        });
                    }
                }
                else if (named is { Length: > 0 })
                {
                    node = nodes.Count;
                    nodes.Add(new ExtractNode(ids.Next($"{Language}:{input.Path}#{place}/{named}"), CodeNodeKinds.Member, named, $"{place}/{named}", line, line, $"{place}/{named}")
                    {
                        SubKind = "element",
                        Parent = parent?.Node is { } holder ? nodes[holder].Id : null,
                    });
                }

                if (kind == "props" && element == "Import" && Attribute(reader, "Project") is { Length: > 0 } project)
                {
                    imports.Add(new ExtractImport(CodeImportForms.Relative, project, line));
                }

                stack.Add(new Frame(element, node, line));

                if (reader.IsEmptyElement)
                {
                    Close(stack, nodes, line);
                }
            }
        }
        catch (XmlException ex)
        {
            // What follows the first fault is not read: the file is graphed as far as it parses, and its extract says where
            // it stopped, so a reference missing after it is never taken for one the file does not make.
            stopped = new ExtractDynamic(FileExtract.LineWithin(ex.LineNumber, lines.Count), "the file stops reading as XML here, and nothing after it is read");
        }

        // Whatever is still open - a file that stopped reading among them - ends at the file's last line: a parent holds
        // the children it read before it stopped.
        while (stack.Count > 0)
        {
            Close(stack, nodes, Math.Max(lines.Count, 1));
        }

        return new FileExtract([.. nodes])
        {
            Imports = imports,
            KeysBeyond = beyond,
            Dynamic = stopped is null ? [] : [stopped],
        };
    }

    /// <summary>One open element: its name, its node among the extract's, and where the node begins.</summary>
    private readonly record struct Frame(string Name, int Node, int Line);

    /// <summary>Closes the innermost open element: its node's lines end here.</summary>
    private static void Close(List<Frame> stack, List<ExtractNode> nodes, int line)
    {
        var frame = stack[^1];
        stack.RemoveAt(stack.Count - 1);

        if (frame.Node >= 0)
        {
            nodes[frame.Node] = nodes[frame.Node] with { End = Math.Max(frame.Line, line) };
        }
    }

    /// <summary>The attribute <paramref name="name"/> names, or null where the element has none.</summary>
    private static string? Attribute(XmlReader reader, string name)
        => reader.GetAttribute(name) is { Length: > 0 } value ? value : null;

    /// <summary>The first of <paramref name="names"/> the element holds, or null where it holds none.</summary>
    private static string? Attribute(XmlReader reader, string[] names)
    {
        foreach (var name in names)
        {
            if (reader.GetAttribute(name) is { Length: > 0 } value)
            {
                return value;
            }
        }

        return null;
    }
}
