namespace DssHarness.Core.Codebase.Languages;

/// <summary>
/// An HTML document - <c>.html</c> and <c>.htm</c> - read as the page it marks up, through the markup reader every markup
/// file shares (<see cref="MarkupText"/>, <see cref="MarkupDialect.Html"/>): each landmark (<c>main</c>, <c>nav</c>,
/// <c>header</c>, <c>footer</c>, <c>aside</c>, <c>article</c>, <c>section</c>) and each element with an <c>id</c> a
/// <c>section</c> from its start tag to its end tag - or to what closed it - and the <c>body</c> one that holds what
/// no other does, a <c>&lt;template&gt;</c>'s content among them; each class the document writes a <c>uses</c> of the
/// rule <c>.name</c> and each id a <c>uses</c> of <c>#id</c>, looked for among rules alone; a custom element
/// (<c>&lt;my-widget&gt;</c>) an <c>instantiates</c> of a component of that name; a <c>script src</c>, <c>link href</c>,
/// <c>img src</c> and <c>a href</c> naming a local file by a relative path - one that climbs among them - an import of
/// it; an <c>on*=</c> handler a call of the function it names; each inline <c>&lt;script&gt;</c> of JavaScript read by
/// the JavaScript reader as its own program - a module where its type says - and each <c>&lt;style&gt;</c> by the style
/// sheet's, in the syntax its <c>lang</c> or <c>type</c> names (<c>text/less</c>, <c>text/x-scss</c>), each embedded as
/// every markup embeds code (<see cref="EmbeddedCode"/>), so its declarations carry the lines
/// they stand on and nothing its reader saw is lost.
/// </summary>
public sealed class HtmlExtractor : ICodeGraphExtractor
{
    /// <summary>The landmark elements: the regions a page is told by.</summary>
    private static readonly HashSet<string> Landmarks = new(StringComparer.Ordinal) { "main", "nav", "header", "footer", "aside", "article", "section" };

    /// <summary>The attributes a local file is named by, each with the element it belongs to.</summary>
    private static readonly (string Element, string Attribute)[] Linked = [("script", "src"), ("link", "href"), ("img", "src"), ("a", "href")];

    /// <summary>The types a script of JavaScript is written with, as HTML runs one: none, and the JavaScript MIME types.</summary>
    private static readonly HashSet<string> JavaScriptTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "", "module", "application/ecmascript", "application/javascript", "application/x-ecmascript", "application/x-javascript", "text/ecmascript",
        "text/javascript", "text/javascript1.0", "text/javascript1.1", "text/javascript1.2", "text/javascript1.3", "text/javascript1.4",
        "text/javascript1.5", "text/jscript", "text/livescript", "text/x-ecmascript", "text/x-javascript",
    };

    /// <inheritdoc/>
    public string Language => "html";

    /// <inheritdoc/>
    public int Version => 2;

    /// <inheritdoc/>
    public CodeGraphClaims Claims { get; } = CodeGraphClaims.OfExtensions(".html", ".htm");

    /// <inheritdoc/>
    public bool Outlines => true;

    /// <inheritdoc/>
    public CodeGraphExtractorHelp Help { get; } = new(
        "HTML documents (.html, .htm): each landmark and each element with an id as a section from its start tag to its end tag, a template's content among them, each class and id as a use of the rule that styles it, a custom element as an instantiates of a component of that name, a script src, link href, img src and a href naming a local file by a relative path as an import of it, an on*= handler as a call of the function it names, each inline script of JavaScript read by the JavaScript reader as a program of its own - a module where its type says - and each style element by the style sheet's reader, in the syntax its lang or type names (CSS where it names none, text/less, text/scss, text/x-scss, text/sass), on the lines they stand on",
        [
            "a style attribute's declarations: no rule of their own",
            "a script of another type - a JSON block, an import map, a template a framework reads: its text read as no code",
            "a style element of another syntax (lang=\"stylus\", a template's type): its text read as no style sheet",
            "an element with no id and no landmark tag: no node of its own, its lines its enclosing section's",
            "an href or src that names no file of the tree - another site's, a rooted path, an anchor, data: - no import of it",
            "a handler that names no one function - a longer expression - is a use of the name it begins with, and no call",
        ]);

    /// <inheritdoc/>
    public FileExtract Extract(CodeFileInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var markup = MarkupText.Read(EmbeddedCode.LineFeeds(input.Text), MarkupDialect.Html.ClosesAsHtml);
        var elements = markup.Elements;
        var lineCount = input.Lines.Count;
        var ids = new ExtractIds();
        var nodes = new List<ExtractNode>();
        var references = new List<ExtractReference>();
        var imports = new List<ExtractImport>();
        var embedded = new List<FileExtract>();

        // The last line of each element, held to hold every element written inside it: what a section ends on.
        var ends = new int[elements.Count];

        for (var at = elements.Count - 1; at >= 0; at--)
        {
            ends[at] = Math.Max(ends[at], FileExtract.LineWithin(markup.EndLine(elements[at]), lineCount));

            if (elements[at].Parent >= 0)
            {
                ends[elements[at].Parent] = Math.Max(ends[elements[at].Parent], ends[at]);
            }
        }

        // The body first, for it holds what no other section does - what stands before it among it - and the id it is
        // given is the one every reference made outside a section is made from. A body no document writes is the file.
        var bodyAt = Enumerable.Range(0, elements.Count).FirstOrDefault(at => elements[at].Name == "body", -1);
        var bodyName = bodyAt >= 0 ? SectionName(elements[bodyAt]) : "body";
        var body = new ExtractNode(ids.Next($"{Language}:{input.Path}#{bodyName}"), CodeNodeKinds.Section, bodyName, $"{input.Path}#{bodyName}",
            bodyAt >= 0 ? FileExtract.LineWithin(elements[bodyAt].Line, lineCount) : 1, bodyAt >= 0 ? ends[bodyAt] : Math.Max(lineCount, 1), bodyName)
        {
            SubKind = "body",
        };

        // The section each element is inside - itself, where it is one - by its index: its parent's, held once.
        var sectionOf = new ExtractNode?[elements.Count];

        if (bodyAt < 0)
        {
            nodes.Add(body);
        }

        for (var at = 0; at < elements.Count; at++)
        {
            var element = elements[at];
            var inside = element.Parent >= 0 ? sectionOf[element.Parent] : bodyAt < 0 ? body : null;

            if (at == bodyAt)
            {
                body = body with { Parent = inside?.Id };
                nodes.Add(body);
                inside = body;
            }
            else if (Landmarks.Contains(element.Name) || element.Attribute("id") is { Length: > 0 })
            {
                var name = SectionName(element);
                var start = FileExtract.LineWithin(element.Line, lineCount);
                var node = new ExtractNode(ids.Next($"{Language}:{input.Path}#{name}"), CodeNodeKinds.Section, name, $"{input.Path}#{name}", start, Math.Max(start, ends[at]), name)
                {
                    SubKind = Landmarks.Contains(element.Name) ? "landmark" : "id",
                    Parent = inside?.Id,
                };
                nodes.Add(node);
                inside = node;
            }

            sectionOf[at] = inside;
            var from = (inside ?? body).Id;

            MarkupDialect.Html.Read(element, from, members: null, references);

            if (Linked.FirstOrDefault(linked => linked.Element == element.Name) is ({ }, { } named)
                && element.Attributes.FirstOrDefault(attribute => attribute.Name == named) is { Value: { Length: > 0 } written } attribute
                && Local(written) is { } target)
            {
                imports.Add(new ExtractImport(CodeImportForms.Relative, target, FileExtract.LineWithin(attribute.Line, lineCount)));
            }

            if (Embedded(markup, element, input.Path) is { } code)
            {
                embedded.Add(code);
            }
        }

        return EmbeddedCode.Merged(new FileExtract([.. nodes]) { References = references, Imports = imports }, embedded);
    }

    /// <summary>The name a section is given: its id, where it has one; its tag otherwise.</summary>
    private static string SectionName(MarkupElement element)
        => element.Attribute("id") is { Length: > 0 } id && ExtractText.Whole(id.Trim()) is { Length: > 0 } whole ? whole : element.Name;

    /// <summary>
    /// The code <paramref name="element"/> holds, read by its language's reader on the lines it stands on: an inline script
    /// of JavaScript - a module where its type is <c>module</c> - and a style element in the syntax it names;
    /// <see langword="null"/> for anything else, a script or a style of another type among them.
    /// </summary>
    private FileExtract? Embedded(MarkupText markup, MarkupElement element, string path)
    {
        if (element.Close <= element.OpenEnd || markup.Text.AsSpan(element.OpenEnd, element.Close - element.OpenEnd).IsWhiteSpace())
        {
            return null;
        }

        var kept = EmbeddedCode.Kept(markup.Text, [(element.OpenEnd, element.Close)]);

        if (element.Name == "script" && element.Attribute("src") is null && JavaScriptTypes.Contains(ScriptType(element)))
        {
            return new JsReading(Language, path, kept, typescript: false, module: string.Equals(ScriptType(element), "module", StringComparison.OrdinalIgnoreCase)).Read();
        }

        return element.Name == "style" ? CssExtractor.Read(Language, new CodeFileInput(path, kept), element.Attribute("lang"), element.Attribute("type")) : null;
    }

    /// <summary>The type a script is written with: its <c>type</c>, else what a <c>language</c> names, as HTML reads them; empty where it says none.</summary>
    private static string ScriptType(MarkupElement element)
        => element.Attribute("type") is { } type ? type.Trim()
            : element.Attribute("language") is { Length: > 0 } language ? $"text/{language.Trim()}"
            : string.Empty;

    /// <summary>
    /// The local path <paramref name="written"/> names, its query and fragment taken off and its escapes read; null where it
    /// names no file of the tree's own - another site's (a scheme, or <c>//</c>), a rooted path, or none at all.
    /// </summary>
    private static string? Local(string written)
    {
        var raw = written.Trim().Split(['?', '#'], 2)[0];

        if (raw.Length == 0 || raw[0] is '/' or '\\' || (raw.IndexOf(':', StringComparison.Ordinal) is var colon and >= 0 && (raw.IndexOf('/', StringComparison.Ordinal) is var slash && (slash < 0 || colon < slash))))
        {
            return null;
        }

        var target = CodeGraphIds.Clean(Uri.UnescapeDataString(raw));

        return target is "." or ".." || target.EndsWith("/..", StringComparison.Ordinal) ? null : ExtractText.Whole(target);
    }
}
