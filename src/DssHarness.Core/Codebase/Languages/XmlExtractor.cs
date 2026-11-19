using System.Text.RegularExpressions;
using System.Xml;

namespace DssHarness.Core.Codebase.Languages;

/// <summary>
/// An XML data file - <c>.xml</c>, <c>.config</c>, <c>.props</c>, <c>.targets</c>, <c>.resx</c>, <c>.xaml</c>,
/// <c>.manifest</c> and <c>.nuspec</c>, but an MSBuild project or solution, which its own extractor reads - read as the
/// data it holds, its nodes made as every data file's are (<see cref="DataNodes"/>): the root element a <c>section</c>
/// over the file, each element with an id-like attribute - <c>name</c>, <c>key</c>, <c>id</c>, <c>x:Name</c> - a
/// <c>member</c> SubKind <c>element</c> named by its path and that attribute, a <c>.resx</c>'s <c>data name=</c>, a
/// <c>.props</c> or <c>.targets</c>'s <c>PropertyGroup</c> children and a <c>.config</c>'s <c>appSettings</c>
/// <c>&lt;add key&gt;</c> and <c>connectionStrings</c> <c>&lt;add name&gt;</c> members SubKind <c>key</c> named by the key
/// they are, and a <c>.props</c> or <c>.targets</c>'s <c>Import Project=</c> an import of the file it names. A
/// <c>.xaml</c> root's <c>x:Class</c> makes the root a <c>type</c> marked a part of that C# type, which the linker joins
/// to it, and each element it names by <c>x:Name</c> a field of that type, of the element's type, as the build generates
/// one - which its code-behind reaches by the field's name, as it reaches any field of its own. Every element is read,
/// however deep; a file with a DTD is refused, nothing in it read; where a file stops reading as XML, what was read before
/// is kept and the line it stops at is marked dynamic, with the reader's reason.
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
        "XML data files (.xml, .config, .props, .targets, .resx, .xaml, .manifest, .nuspec), every element however deep: the root element as a section, each element with a name, key, id or x:Name attribute as an element node named by its path and that attribute, a .resx's data name=, a .props or .targets's PropertyGroup children and a .config's appSettings <add key> and connectionStrings <add name> as keys named by the key they are (appSettings.Timeout, connectionStrings.Main), their value or connection string as the summary, a .props or .targets's Import Project= as an import of the file it names, a .xaml root's x:Class as a part of that C# type, and each element the view names by x:Name as a field of that type, of the element's type, which its code-behind reaches by the field's name",
        [
            "MSBuild projects and solutions (.csproj, .slnx), which the msbuild extractor reads",
            "a file with a DTD: refused, nothing in it read",
            "what follows the point a file stops reading as XML, a line its extract marks dynamic with the reader's reason",
            "an attribute named otherwise than name, key, id or x:Name, and an element with none of them: no node of its own, its lines its parent's",
            "a key that is no .resx's data, .props's property or .config's appSettings or connectionStrings entry - an <add key> of a custom configuration section among them: an element node, which the keys' join does not reach",
            "an element a XAML view names by Name alone, without the x: prefix, and an x:Name of a view with no x:Class: an element node, which no code reaches",
            "an Import Project= of any file but a .props or a .targets: no import",
            "an element's text past the cut of a summary",
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
            : name.EndsWith(".config", StringComparison.OrdinalIgnoreCase) ? "config"
            : null;
        var nodes = new DataNodes(Language, input.Path, lines.Count);
        var imports = new List<ExtractImport>();
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, IgnoreComments = true, IgnoreProcessingInstructions = true };
        var stack = new Stack<Frame>();

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
    /// a field of the view's type, an element named by an id-like attribute - and an import of what a <c>.props</c>'s
    /// <c>Import</c> names; its frame, the node nearest that holds it and the view's type carried down from
    /// <paramref name="parent"/>.
    /// </summary>
    private Frame Open(XmlReader reader, string? kind, Frame? parent, int line, DataNodes nodes, List<ExtractImport> imports)
    {
        var element = reader.LocalName;
        var holder = parent?.Nearest;

        // The root's x:Class makes the root a part of the C# type it names; every other root - one whose x:Class names no
        // type, ending in a dot among them - is a section.
        var xClass = parent is null && kind == "xaml" && Attribute(reader, "x:Class") is { } named && !named.EndsWith('.') ? named : null;
        var view = parent is null ? null : parent.View;
        string? node;

        if (xClass is not null)
        {
            node = nodes.Node(new ExtractNode($"{Language}:{xClass}", CodeNodeKinds.Type, xClass[(xClass.LastIndexOf('.') + 1)..], xClass, line, line, xClass) { SubKind = "view", Part = true });
            view = (node, xClass);
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
        else if (kind == "config" && element == "add" && Setting(reader, parent.Name) is { } setting)
        {
            // .NET's configuration names a setting by its key alone - AppSettings["Timeout"], ConnectionStrings["Main"] - and
            // the section it is read from by its own name.
            node = nodes.Key(setting.Key, $"{setting.Section}.{setting.Key}", line, setting.Value, holder);
        }
        else if (view is { } type && Attribute(reader, "x:Name") is { } field)
        {
            // The build makes each element a view names a field of the view's type, of the element's type: its code-behind
            // reaches it by the field's name, as it reaches any member of its own.
            node = nodes.Node(new ExtractNode($"{Language}:{type.Class}.{field}", CodeNodeKinds.Member, field, $"{type.Class}.{field}", line, line, $"{element} {field}")
            {
                SubKind = "field",
                Scope = type.Class,
                Parent = type.Node,
                ValueType = element,
            });
        }
        else if ((Attribute(reader, Named) ?? Attribute(reader, "x:Name")) is { } id)
        {
            node = nodes.Element(id, $"{PathOf(parent, element)}/{id}", line, holder);
        }
        else
        {
            node = null;
        }

        if (kind == "props" && element == "Import" && Attribute(reader, "Project") is { } project)
        {
            imports.Add(new ExtractImport(CodeImportForms.Relative, project, line));
        }

        return new Frame(element, parent, node, node ?? holder, view);
    }

    /// <summary>
    /// The setting a <c>.config</c>'s <c>&lt;add&gt;</c> is inside the element <paramref name="section"/>: an
    /// <c>appSettings</c> one's key and value, a <c>connectionStrings</c> one's name and connection string; <see langword="null"/>
    /// in any other section, or where it names none.
    /// </summary>
    private static (string Section, string Key, string? Value)? Setting(XmlReader reader, string section) => section switch
    {
        "appSettings" when Attribute(reader, "key") is { } key => (section, key, Attribute(reader, "value")),
        "connectionStrings" when Attribute(reader, "name") is { } name => (section, name, Attribute(reader, "connectionString")),
        _ => null,
    };

    /// <summary>
    /// The path of the element <paramref name="element"/> opened inside <paramref name="parent"/>: each element's name from
    /// the root, parted by a slash - told only where a node is named by it, from the open elements up, so no element is
    /// given a path it is not named by.
    /// </summary>
    private static string PathOf(Frame? parent, string element)
    {
        var names = new List<string> { element };

        for (var open = parent; open is not null; open = open.Parent)
        {
            names.Add(open.Name);
        }

        names.Reverse();
        return string.Join('/', names);
    }

    /// <summary>
    /// One open element: its name, the element it is open inside, its node among the extract's, the nearest node that holds
    /// what is inside it, and the XAML view it is inside - its type's node and the C# type it names - where it is in one.
    /// </summary>
    private sealed class Frame(string name, Frame? parent, string? node, string? nearest, (string Node, string Class)? view)
    {
        public string Name => name;

        public Frame? Parent => parent;

        public string? Node => node;

        public string? Nearest => nearest;

        public (string Node, string Class)? View => view;
    }

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
