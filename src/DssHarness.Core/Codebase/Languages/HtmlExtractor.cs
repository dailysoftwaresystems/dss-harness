using System.Globalization;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace DssHarness.Core.Codebase.Languages;

/// <summary>
/// An HTML document - <c>.html</c> and <c>.htm</c> - read as the page it marks up: each landmark (<c>main</c>,
/// <c>nav</c>, <c>header</c>, <c>footer</c>, <c>aside</c>, <c>article</c>, <c>section</c>) and each element with an
/// <c>id</c> a <c>section</c> over its lines, the <c>body</c> one over all; each class the document writes a
/// <c>uses</c> of the rule <c>.name</c> and each id a <c>uses</c> of <c>#id</c>, looked for among rules alone; a
/// custom element (<c>&lt;my-widget&gt;</c>) an <c>instantiates</c> of a component of that name; a <c>script src</c>,
/// <c>link href</c>, <c>img src</c> and <c>a href</c> naming a local file by a relative path an import of it; an
/// <c>on*=</c> handler a call of the function it names; and an inline <c>&lt;script&gt;</c> read by the JavaScript
/// reader with the document's other lines blanked, so its declarations carry the lines they stand on. Read through
/// AngleSharp with its source references kept; an element the parser makes up - a <c>head</c> no document writes -
/// is no node.
/// </summary>
public sealed class HtmlExtractor : ICodeGraphExtractor
{
    /// <summary>The landmark elements: the regions a page is told by.</summary>
    private static readonly HashSet<string> Landmarks = new(StringComparer.Ordinal) { "main", "nav", "header", "footer", "aside", "article", "section" };

    /// <summary>The attributes a local file is named by, each with the element it belongs to.</summary>
    private static readonly (string Element, string Attribute)[] Linked = [("script", "src"), ("link", "href"), ("img", "src"), ("a", "href")];

    /// <inheritdoc/>
    public string Language => "html";

    /// <inheritdoc/>
    public int Version => 1;

    /// <inheritdoc/>
    public CodeGraphClaims Claims { get; } = CodeGraphClaims.OfExtensions(".html", ".htm");

    /// <inheritdoc/>
    public bool Outlines => true;

    /// <inheritdoc/>
    public CodeGraphExtractorHelp Help { get; } = new(
        "HTML documents (.html, .htm): each landmark and each element with an id as a section over its lines, each class and id as a use of the rule that styles it, a custom element as an instantiates of a component of that name, a script src, link href, img src and a href naming a local file as an import of it, an on*= handler as a call of the function it names, and an inline script read by the JavaScript reader with the document's other lines blanked",
        [
            "a style attribute's rules, and a style element's text: no rules of their own, said as the css extractor's",
            "an element with no id and no landmark tag: no node of its own, its lines its enclosing section's",
            "an href or src that names no file of the tree - another site's, an anchor, data: - no import of it",
            "a handler that names no one function - a call in a longer expression - is no call",
        ]);

    /// <inheritdoc/>
    public FileExtract Extract(CodeFileInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var lines = input.Lines;
        var nodes = new List<ExtractNode>();
        var references = new List<ExtractReference>();
        var imports = new List<ExtractImport>();
        var ids = new ExtractIds();
        var scripts = new List<(int Line, string Text)>();

        var document = new HtmlParser(new HtmlParserOptions { IsKeepingSourceReferences = true, IsScripting = false }).ParseDocument(input.Text);
        var sections = new List<(IElement Element, ExtractNode Node)>();

        foreach (var element in document.All.OfType<IElement>())
        {
            var line = element.SourceReference?.Position.Line ?? 0;

            // An element the parser makes up - a head no document writes - stands on no line of the file; the body of a
            // fragment is one all the same, for it holds what no landmark does.
            if (line < 1 && element.LocalName != "body")
            {
                continue;
            }

            var start = FileExtract.LineWithin(Math.Max(line, 1), lines.Count);
            var inside = sections.LastOrDefault(held => Contains(held.Element, element));

            if (element.LocalName == "body" || Landmarks.Contains(element.LocalName) || element.Id is { Length: > 0 })
            {
                var name = element.Id is { Length: > 0 } id ? id : element.LocalName;
                var node = new ExtractNode(ids.Next($"{Language}:{input.Path}#{name}"), CodeNodeKinds.Section, name, $"{input.Path}#{name}", start, Math.Max(start, SpanOf(element, lines.Count)), name)
                {
                    SubKind = element.LocalName == "body" ? "body" : Landmarks.Contains(element.LocalName) ? "landmark" : "id",
                    Parent = inside.Node?.Id,
                };
                nodes.Add(node);
                sections.Add((element, node));
                inside = (element, node);
            }

            var from = inside.Node?.Id ?? BodyId(input.Path);

            if (element.LocalName == "script" && element.GetAttribute("src") is null && element.TextContent.Trim().Length > 0)
            {
                scripts.Add((start, element.TextContent));
            }

            foreach (var name in (element.GetAttribute("class") ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                references.Add(new ExtractReference(from, CodeEdgeKinds.Uses, $".{name}", start) { Qualifier = Selector() });
            }

            if (element.Id is { Length: > 0 } byId)
            {
                references.Add(new ExtractReference(from, CodeEdgeKinds.Uses, $"#{byId}", start) { Qualifier = Selector() });
            }

            if (element.LocalName.Contains('-', StringComparison.Ordinal) && !Landmarks.Contains(element.LocalName))
            {
                references.Add(new ExtractReference(from, CodeEdgeKinds.Instantiates, element.LocalName, start));
            }

            foreach (var handler in element.Attributes.Where(attribute => attribute.Name.StartsWith("on", StringComparison.Ordinal) && !attribute.Name.Contains('-', StringComparison.Ordinal)))
            {
                if (Named(handler.Value) is not { } called)
                {
                    continue;
                }

                references.Add(new ExtractReference(from, CodeEdgeKinds.Calls, called, start));
            }

            if (Linked.FirstOrDefault(linked => linked.Element == element.LocalName) is ({ }, { } attribute)
                && element.GetAttribute(attribute) is { Length: > 0 } written
                && Local(written) is { } target)
            {
                imports.Add(new ExtractImport(CodeImportForms.Relative, target, start));
            }
        }

        var script = Scripted(input, scripts);
        nodes.AddRange(script.Nodes);
        references.AddRange(script.References);
        imports.AddRange(script.Imports);

        return new FileExtract([.. nodes])
        {
            References = references,
            Imports = imports,
            Literals = script.Literals,
            LiteralsBeyond = script.LiteralsBeyond,
        };
    }

    /// <summary>The qualifier of a selector: the name, written as a rule writes it, is looked for among rules alone.</summary>
    internal static ExtractQualifier Selector() => new(CodeQualifierKinds.Selector);

    /// <summary>The id of the body section, which holds what no other section does.</summary>
    private static string BodyId(string path) => $"html:{path}#body";

    /// <summary>The name an <c>on*=</c> handler names: one identifier alone, or one call of it, and nothing else.</summary>
    private static string? Named(string written)
    {
        var text = written.Trim();

        if (text.Length == 0 || (!char.IsLetter(text[0]) && text[0] is not '_' and not '$'))
        {
            return null;
        }

        var length = 0;

        while (length < text.Length && (char.IsLetterOrDigit(text[length]) || text[length] is '_' or '$'))
        {
            length++;
        }

        return length < text.Length && text[length] != '(' ? null : text[..length];
    }

    /// <summary>The local path <paramref name="written"/> names, its query and fragment taken off; null where it names no file of the tree's own - another site's, an absolute path.</summary>
    private static string? Local(string written)
    {
        var raw = written.Split(new[] { '?', '#' }, 2)[0];

        if (raw.Length < 2 || raw[0] == '/' || raw.Contains(':', StringComparison.Ordinal) || (raw[0] == '.' && raw.Length > 1 && raw[1] != '/'))
        {
            return null;
        }

        var target = CodeGraphIds.Clean(raw);

        return target is { Length: > 1 } && target[0] is not ('/' or '.') ? target : null;
    }

    /// <summary>Every inline script of the document read by the JavaScript reader, the document's other lines blanked so each declaration carries the line it stands on.</summary>
    private static FileExtract Scripted(CodeFileInput input, IReadOnlyList<(int Line, string Text)> scripts)
    {
        if (scripts.Count == 0)
        {
            return FileExtract.Empty;
        }

        var text = new System.Text.StringBuilder();
        var at = 1;

        foreach (var (line, body) in scripts)
        {
            while (at < line)
        {
                _ = text.Append('\n');
                at++;
            }

            _ = text.Append(body);
            at += body.Count(character => character == '\n');
        }

        return new JsReading("html", input.Path, text.ToString(), typescript: false).Read();
    }

    /// <summary>Whether <paramref name="outer"/>'s lines hold <paramref name="inner"/>: it is inside it in the document.</summary>
    private static bool Contains(IElement outer, IElement inner)
    {
        for (var at = inner.ParentElement; at is not null; at = at.ParentElement)
        {
            if (ReferenceEquals(at, outer))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The last line <paramref name="element"/> or anything inside it is written on: AngleSharp keeps where an element begins, not where it ends.</summary>
    private static int SpanOf(IElement element, int lineCount)
    {
        var last = element.SourceReference?.Position.Line ?? 1;

        foreach (var child in element.Children)
        {
            last = Math.Max(last, child.SourceReference?.Position.Line ?? 0);
            last = Math.Max(last, SpanOf(child, lineCount));
        }

        return FileExtract.LineWithin(last, Math.Max(Math.Max(lineCount, last), 1));
    }
}
