using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;

namespace DssHarness.Core.Codebase.Languages;

/// <summary>
/// An XML data file - <c>.xml</c>, <c>.config</c>, <c>.props</c>, <c>.targets</c>, <c>.resx</c>, <c>.xaml</c>,
/// <c>.manifest</c> and <c>.nuspec</c>, but an MSBuild project or solution, which its own extractor reads - read as the
/// data it holds, its nodes made as every data file's are (<see cref="DataNodes"/>): the root element a <c>section</c>
/// over the file, each element with an id-like attribute - <c>name</c>, <c>key</c>, <c>id</c>, <c>x:Name</c> - a
/// <c>member</c> SubKind <c>element</c> named by its path and that attribute, a <c>.resx</c>'s <c>data name=</c> and a
/// <c>.props</c> or <c>.targets</c>'s <c>PropertyGroup</c> children members SubKind <c>key</c> named by the key they are,
/// and a <c>.props</c> or <c>.targets</c>'s <c>Import Project=</c> an import of the file it names. A <c>.xaml</c> root's
/// <c>x:Class</c> makes the root a <c>type</c> marked a part of that C# type, which the linker joins to it. Nothing nested
/// deeper than <see cref="DataNodes.MostNesting"/> elements is read; a file with a DTD is refused, nothing in it read;
/// where a file stops reading as XML, what was read before is kept and the line it stops at is marked dynamic, with the
/// reader's reason.
/// </summary>
public sealed partial class XmlExtractor : ICodeGraphExtractor
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
        "XML data files (.xml, .config, .props, .targets, .resx, .xaml, .manifest, .nuspec): the root element as a section, each element with a name, key, id or x:Name attribute as an element node named by its path and that attribute, a .resx's data name= and a .props or .targets's PropertyGroup children as keys named by the key they are, a .props or .targets's Import Project= as an import of the file it names, and a .xaml root's x:Class as a part of that C# type",
        [
            "MSBuild projects and solutions (.csproj, .slnx), which the msbuild extractor reads",
            "a file with a DTD: refused, nothing in it read",
            "what follows the point a file stops reading as XML, a line its extract marks dynamic with the reader's reason",
            $"what a file nests past {DataNodes.MostNesting.ToString(CultureInfo.InvariantCulture)} elements: not read, the line marked dynamic; and a file of more than {FileExtract.MostKeys.ToString(CultureInfo.InvariantCulture)} keys and elements: the first {FileExtract.MostKeys.ToString(CultureInfo.InvariantCulture)} are kept, the rest counted, and an answer about the file says how many",
            "an attribute named otherwise than name, key, id or x:Name, and an element with none of them: no node of its own, its lines its parent's",
            "a key that is no .resx's data or .props's property - an app.config's <add key=\"x\" value=\"y\"/> among them: an element node, which the keys' join does not reach",
            "an element a XAML view names by x:Name, as its code-behind names the field the build generates for it: no reference of the code reaches the element, which code reaches by no name alone",
            "an Import Project= of any file but a .props or a .targets: no import",
            "an element's text past the cut of a summary; a secret its text holds is kept as the file writes it, as reading the file shows it, and an answer masks only the run's own secrets, as every tool's answer does",
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
        var nodes = new DataNodes(Language, input.Path, lines.Count);
        var imports = new List<ExtractImport>();
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, IgnoreComments = true, IgnoreProcessingInstructions = true };
        var stack = new Stack<Frame>();
        var deep = false;

        try
        {
            using var reader = XmlReader.Create(new StringReader(input.Text), settings);
            var at = (IXmlLineInfo)reader;

            for (var more = reader.Read(); more;)
            {
                var line = FileExtract.LineWithin(at.LineNumber, lines.Count);
                stack.TryPeek(out var parent);

                switch (reader.NodeType)
                {
                    case XmlNodeType.Text:
                        // The text an element holds is its node's summary, cut: a key's value, a header's description.
                        if (parent?.Nearest is { } holder)
                        {
                            nodes.Summarize(holder, reader.Value);
                        }

                        break;

                    case XmlNodeType.EndElement:
                        if (stack.TryPop(out var closed) && closed.Node is { } ended)
                        {
                            nodes.Ends(ended, line);
                        }

                        break;

                    case XmlNodeType.Element when stack.Count >= DataNodes.MostNesting:
                        // A file made to nest without end is read no deeper than any data file is: what this element holds is
                        // passed over whole, and the first place it is said.
                        if (!deep)
                        {
                            deep = true;
                            nodes.Stopped(line, $"the file nests more than {DataNodes.MostNesting.ToString(CultureInfo.InvariantCulture)} elements deep here, and nothing deeper is read");
                        }

                        reader.Skip();
                        more = !reader.EOF;
                        continue;

                    case XmlNodeType.Element:
                        var frame = Open(reader, kind, parent, line, nodes, imports);

                        if (reader.IsEmptyElement)
                        {
                            if (frame.Node is { } empty)
                            {
                                nodes.Ends(empty, line);
                            }
                        }
                        else
                        {
                            stack.Push(frame);
                        }

                        break;
                }

                more = reader.Read();
            }
        }
        catch (XmlException ex)
        {
            // What follows the first fault is not read: the file is graphed as far as it parses, and its extract says where
            // it stopped, so a reference missing after it is never taken for one the file does not make.
            nodes.Stopped(ex.LineNumber, Stopped(ex, lines, refusesDtd: true));
        }

        // Whatever is still open - a file that stopped reading among them - ends at the file's last line: a parent holds
        // the children it read before it stopped.
        while (stack.TryPop(out var open))
        {
            if (open.Node is { } id)
            {
                nodes.Ends(id, Math.Max(lines.Count, 1));
            }
        }

        return new FileExtract([.. nodes.Nodes])
        {
            Imports = imports,
            KeysBeyond = nodes.Beyond,
            Dynamic = nodes.Dynamic,
        };
    }

    /// <summary>
    /// Why a reader of XML stopped at <paramref name="exception"/>, as an extract marks it: a DTD the file declares, where
    /// the reader refuses one, or the reader's own reason, without the place it says - the mark's line says it.
    /// </summary>
    /// <param name="exception">What the reader threw.</param>
    /// <param name="lines">The file's lines.</param>
    /// <param name="refusesDtd">Whether the reader refuses a DTD, so one on the line it stopped at is why.</param>
    internal static string Stopped(XmlException exception, IReadOnlyList<string> lines, bool refusesDtd)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentNullException.ThrowIfNull(lines);

        var line = FileExtract.LineWithin(exception.LineNumber, lines.Count);

        return refusesDtd && lines.Count > 0 && lines[line - 1].Contains("<!DOCTYPE", StringComparison.Ordinal)
            ? "the file declares a DTD, which is never read, and nothing after it is read"
            : $"the file stops reading as XML here ({Place().Replace(exception.Message, string.Empty).Trim().TrimEnd('.')}), and nothing after it is read";
    }

    /// <summary>The place a reader of XML writes at the end of its message.</summary>
    [GeneratedRegex(@"\s*Line \d+, position \d+\.?\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex Place();

    /// <summary>
    /// The element the reader stands on, opened: its node where it is one - the root's section or a XAML view's type, a key,
    /// an element named by an id-like attribute - and an import of what a <c>.props</c>'s <c>Import</c> names; its frame,
    /// its path and the node nearest that holds it carried down from <paramref name="parent"/>.
    /// </summary>
    private Frame Open(XmlReader reader, string? kind, Frame? parent, int line, DataNodes nodes, List<ExtractImport> imports)
    {
        var element = reader.LocalName;
        var path = parent is null ? element : $"{parent.Path}/{element}";
        var holder = parent?.Nearest;

        // The root's x:Class makes the root a part of the C# type it names; every other root - one whose x:Class names no
        // type, ending in a dot among them - is a section.
        var xClass = parent is null && kind == "xaml" && Attribute(reader, "x:Class") is { } named && !named.EndsWith('.') ? named : null;
        string? node;

        if (xClass is not null)
        {
            node = nodes.Node(new ExtractNode($"{Language}:{xClass}", CodeNodeKinds.Type, xClass[(xClass.LastIndexOf('.') + 1)..], xClass, line, line, xClass) { SubKind = "view", Part = true });
        }
        else if (parent is null)
        {
            node = nodes.Section(element, element, line, line, null);
        }
        else if (element == "data" && kind == "resx" && Attribute(reader, "name") is { } data)
        {
            node = nodes.Key(data, data, line, null, holder);
        }
        else if (parent.Name == "PropertyGroup" && kind == "props")
        {
            node = nodes.Key(element, element, line, null, holder);
        }
        else if ((Attribute(reader, Named) ?? Attribute(reader, "x:Name")) is { } id)
        {
            node = nodes.Element(id, $"{path}/{id}", line, holder);
        }
        else
        {
            node = null;
        }

        if (kind == "props" && element == "Import" && Attribute(reader, "Project") is { } project)
        {
            imports.Add(new ExtractImport(CodeImportForms.Relative, project, line));
        }

        return new Frame(element, path, node, node ?? holder);
    }

    /// <summary>One open element: its name, its path from the root, its node among the extract's, and the nearest node that holds what is inside it.</summary>
    private sealed record Frame(string Name, string Path, string? Node, string? Nearest);

    /// <summary>
    /// The attribute <paramref name="name"/> names, trimmed, or null where the element has none, or one of white space alone,
    /// which names nothing a node or an import could be.
    /// </summary>
    private static string? Attribute(XmlReader reader, string name)
        => reader.GetAttribute(name) is { } value && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

    /// <summary>The first of <paramref name="names"/> the element holds (<see cref="Attribute(XmlReader, string)"/>), or null where it holds none.</summary>
    private static string? Attribute(XmlReader reader, string[] names)
        => names.Select(name => Attribute(reader, name)).FirstOrDefault(value => value is not null);
}
