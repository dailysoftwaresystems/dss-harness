using System.Globalization;
using System.Text;
using AngleSharp.Html;
using AngleSharp.Html.Parser;
using AngleSharp.Text;

namespace DssHarness.Core.Codebase.Languages;

/// <summary>
/// Markup as the graph reads it - an HTML page, a Vue component, a Razor file's markup - through AngleSharp's HTML
/// tokenizer, so a tag, an attribute, a comment and an element whose text is raw (a script, a style) are read as a
/// browser reads them, and no tag a comment holds is an element: each element with its tag as written, its attributes
/// and the lines they stand on, the element it is inside, and where its end tag - or what closed it - stands. Its
/// elements are matched by one walk over the tokens, a stack and no recursion, however deep the markup nests. A page's
/// elements close as HTML closes them where their end tags are left out - a <c>p</c> at the next block, an <c>li</c> at
/// the next item; a component's are matched as written, <c>/&gt;</c> closing any element, as Vue's and Razor's compilers
/// read them, so no tag is dropped as a browser drops one it finds out of place, and a component named as an element
/// whose text is raw - a <c>Title</c>, a <c>Textarea</c> - or as one no end tag closes - a <c>Link</c>, an
/// <c>Input</c> - holds the markup it is written around.
/// </summary>
internal sealed class MarkupText
{
    /// <summary>The elements no end tag closes, as HTML has them.</summary>
    private static readonly HashSet<string> Void = new(StringComparer.Ordinal)
    {
        "area", "base", "basefont", "bgsound", "br", "col", "embed", "frame", "hr", "img", "input", "keygen", "link", "meta", "param", "source",
        "track", "wbr",
    };

    /// <summary>The elements whose text is raw, each with how the tokenizer reads it.</summary>
    private static readonly Dictionary<string, HtmlParseMode> Raw = new(StringComparer.Ordinal)
    {
        ["script"] = HtmlParseMode.Script,
        ["style"] = HtmlParseMode.Rawtext,
        ["xmp"] = HtmlParseMode.Rawtext,
        ["iframe"] = HtmlParseMode.Rawtext,
        ["noembed"] = HtmlParseMode.Rawtext,
        ["noframes"] = HtmlParseMode.Rawtext,
        ["textarea"] = HtmlParseMode.RCData,
        ["title"] = HtmlParseMode.RCData,
        ["plaintext"] = HtmlParseMode.Plaintext,
    };

    /// <summary>
    /// Whether the tag <paramref name="written"/>, as written, is one no end tag closes where elements are matched as
    /// written - an HTML element's, in its own case: <c>input</c>, and no <c>Input</c> component - the one rule this
    /// reader closes them by, and a Razor file's reader finds where its markup ends by.
    /// </summary>
    public static bool IsVoid(string written) => Void.Contains(written);

    /// <summary>
    /// Whether the tag <paramref name="written"/>, as written, is one whose text is raw - an HTML element's, in its own
    /// case: <c>script</c>, and no <c>Title</c> component - its text no markup.
    /// </summary>
    public static bool IsRaw(string written) => Raw.ContainsKey(written);

    /// <summary>The elements whose start tag closes a paragraph left open, as HTML closes it.</summary>
    private static readonly HashSet<string> ClosesParagraph = new(StringComparer.Ordinal)
    {
        "address", "article", "aside", "blockquote", "center", "details", "dialog", "dir", "div", "dl", "fieldset", "figcaption", "figure",
        "footer", "form", "h1", "h2", "h3", "h4", "h5", "h6", "header", "hgroup", "hr", "main", "menu", "nav", "ol", "p", "pre", "listing",
        "search", "section", "summary", "table", "ul", "plaintext", "xmp", "li", "dd", "dt",
    };

    /// <summary>The elements a paragraph's closing by another start tag looks no further than, as HTML's button scope has them.</summary>
    private static readonly HashSet<string> ButtonScope = new(StringComparer.Ordinal)
    {
        "applet", "caption", "html", "table", "td", "th", "marquee", "object", "template", "button", "svg", "math",
    };

    /// <summary>The elements an item's closing by the next item stops at, as HTML's special elements are, but those an item may be written in.</summary>
    private static readonly HashSet<string> Special = new(StringComparer.Ordinal)
    {
        "applet", "area", "article", "aside", "base", "basefont", "bgsound", "blockquote", "body", "br", "button", "caption", "center", "col",
        "colgroup", "details", "dir", "dl", "embed", "fieldset", "figcaption", "figure", "footer", "form", "frame", "frameset", "h1", "h2", "h3",
        "h4", "h5", "h6", "head", "header", "hgroup", "hr", "html", "iframe", "img", "input", "keygen", "link", "listing", "main", "marquee",
        "menu", "meta", "nav", "noembed", "noframes", "noscript", "object", "ol", "param", "plaintext", "pre", "script", "search", "section",
        "select", "source", "style", "summary", "table", "tbody", "td", "template", "textarea", "tfoot", "th", "thead", "title", "tr", "track",
        "ul", "wbr", "xmp",
    };

    /// <summary>The elements a page's head holds, before which no other start tag leaves the head open.</summary>
    private static readonly HashSet<string> HeadContent = new(StringComparer.Ordinal)
    {
        "base", "basefont", "bgsound", "link", "meta", "noframes", "script", "style", "template", "title", "noscript",
    };

    private readonly List<MarkupElement> _elements = [];
    private readonly List<(long Start, long End)> _texts = [];
    private readonly List<int> _open = [];
    private readonly Dictionary<string, int> _openNames = new(StringComparer.Ordinal);
    private readonly bool _closesAsHtml;

    /// <summary>How many foreign elements - an <c>svg</c>, a <c>math</c> - are open, in which <c>/&gt;</c> closes any element.</summary>
    private int _foreign;

    private MarkupText(CodeText text, bool closesAsHtml)
    {
        Text = text;
        Lines = text.LineIndex;
        _closesAsHtml = closesAsHtml;
    }

    /// <summary>
    /// The most characters a markup file's text holds for AngleSharp's tokenizer to read it: the tokenizer counts a position
    /// in an Int32 (TextPosition.Index), and its source keeps what it decodes in one StringBuilder, which an Int32 counts.
    /// </summary>
    public const long MostCharacters = int.MaxValue;

    /// <summary>The text read, its line endings each one line feed.</summary>
    public CodeText Text { get; }

    /// <summary>Where its lines begin.</summary>
    public CodeLineIndex Lines { get; }

    /// <summary>Its elements, in the order their start tags are written; each one's parent is before it.</summary>
    public IReadOnlyList<MarkupElement> Elements => _elements;

    /// <summary>Its runs of text - what no tag and no comment is - each from its first character to past its last.</summary>
    public IReadOnlyList<(long Start, long End)> Texts => _texts;

    /// <summary>
    /// The extract of a markup file whose text, <paramref name="text"/> - its line endings each one line feed, as it is read -
    /// holds more characters than <see cref="MostCharacters"/>: nothing in it read, the line the tokenizer's count ends on
    /// marked dynamic, with why; <see langword="null"/> for any other, which is read.
    /// </summary>
    /// <param name="text">The text as it would be read.</param>
    public static FileExtract? Unread(CodeText text)
    {
        ArgumentNullException.ThrowIfNull(text);

        return text.Length > MostCharacters
            ? new FileExtract([]) { Dynamic = [new ExtractDynamic(text.LineIndex.LineAt(MostCharacters), string.Create(CultureInfo.InvariantCulture, $"the file holds {text.Length:N0} characters, past the {MostCharacters:N0} the HTML tokenizer (AngleSharp) counts a position in, so it is not read"))] }
            : null;
    }

    /// <summary>
    /// Reads <paramref name="text"/>: its elements closing as HTML closes a page's where <paramref name="closesAsHtml"/>,
    /// else as written.
    /// </summary>
    /// <param name="text">The markup, its line endings each one line feed (<see cref="CodeText.LineFed"/>).</param>
    /// <param name="closesAsHtml">Whether it is a page, whose elements HTML closes where their end tags are left out.</param>
    public static MarkupText Read(CodeText text, bool closesAsHtml)
    {
        ArgumentNullException.ThrowIfNull(text);

        var markup = new MarkupText(text, closesAsHtml);
        markup.Walk();
        return markup;
    }

    /// <summary>The line, from 1, that <paramref name="offset"/> stands on.</summary>
    public int LineAt(long offset) => Lines.LineAt(Math.Clamp(offset, 0, Math.Max(Text.Length - 1, 0)));

    /// <summary>
    /// The last line <paramref name="element"/> stands on: its end tag's, where one closes it; else the last line its
    /// content is written on before what closed it - the next block, its parent's end tag, the end of the text.
    /// </summary>
    public int EndLine(MarkupElement element)
    {
        ArgumentNullException.ThrowIfNull(element);

        if (element.End > element.Close)
        {
            return LineAt(element.End - 1);
        }

        var last = element.Close - 1;

        while (last >= element.OpenEnd && char.IsWhiteSpace(Text[last]))
        {
            last--;
        }

        return Math.Max(element.Line, LineAt(Math.Max(last, element.OpenEnd - 1)));
    }

    /// <summary>Whether <paramref name="inner"/> is written inside <paramref name="outer"/>: its descendant.</summary>
    public bool Inside(MarkupElement inner, MarkupElement outer)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(outer);

        return inner.Start > outer.Start && inner.Start < Math.Max(outer.Close, outer.OpenEnd);
    }

    /// <summary>The one walk over the tokens: each element opened, matched to the end tag that closes it, and closed.</summary>
    private void Walk()
    {
        // AngleSharp's tokenizer reads the text as a stream of its bytes: its source keeps what it decodes (its Buffered
        // mode, the one its tokenizer is given), but no string of the whole text is made here.
        using var bytes = Text.Utf8();
        using var source = new TextSource(bytes, Encoding.UTF8);
        var tokenizer = new HtmlTokenizer(source, HtmlEntityProvider.Resolver);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        // A tag ends where the next token begins: what the last start tag's and end tag's ends wait for.
        MarkupElement? opened = null;
        MarkupElement? closed = null;
        var empty = false;
        var textStart = -1L;

        while (true)
        {
            var token = tokenizer.Get();
            var at = Math.Clamp((long)token.Position.Index, 0, Text.Length);

            if (token.Type == HtmlTokenType.EndOfFile)
            {
                at = Text.Length;
            }

            if (opened is not null)
            {
                opened.OpenEnd = Math.Max(at, opened.Start);

                if (empty)
                {
                    opened.Close = opened.End = opened.OpenEnd;
                }

                opened = null;
            }

            if (closed is not null)
            {
                closed.End = Math.Max(at, closed.Close);
                closed = null;
            }

            if (token.Type != HtmlTokenType.Character && textStart >= 0)
            {
                _texts.Add((textStart, at));
                textStart = -1;
            }

            switch (token.Type)
            {
                case HtmlTokenType.Character:
                    textStart = textStart < 0 ? at : textStart;
                    break;

                case HtmlTokenType.StartTag:
                {
                    var tag = token.AsTag();
                    var name = tag.Name;

                    // A page has one html, one head and one body: a second start tag of one opens nothing.
                    if (_closesAsHtml && name is "html" or "head" or "body" && !seen.Add(name))
                    {
                        break;
                    }

                    if (_closesAsHtml)
                    {
                        Imply(name, at);
                    }

                    var element = new MarkupElement(Written(at, name), name, at, LineAt(at), _open.Count > 0 ? _open[^1] : -1,
                        [.. tag.Attributes.Select(attribute => new MarkupAttribute(attribute.Name, attribute.Value, LineAt(attribute.Position.Index)) { NameAt = Math.Clamp((long)attribute.Position.Index, 0, Text.Length), ValueAt = ValueAt(attribute.Position.Index, attribute.Name.Length) })]);

                    _elements.Add(element);
                    opened = element;

                    // A void element holds nothing - a component's tag is matched as written, so a component named as one,
                    // a Link, an Input, holds what it is written around - and a component's tag closed by /> holds nothing,
                    // as a page's foreign one does.
                    empty = (_closesAsHtml ? Void.Contains(name) : IsVoid(element.Tag)) || (tag.IsSelfClosing && (!_closesAsHtml || _foreign > 0));

                    if (empty)
                    {
                        break;
                    }

                    _open.Add(_elements.Count - 1);
                    _openNames[name] = _openNames.GetValueOrDefault(name) + 1;
                    _foreign += name is "svg" or "math" ? 1 : 0;
                    tokenizer.IsAcceptingCharacterData = _foreign > 0;

                    // A component's tag is matched as written, so a component named as an element whose text is raw -
                    // a Title, a Textarea - holds markup as any component does.
                    if (_foreign == 0 && (_closesAsHtml || element.Tag == name) && Raw.TryGetValue(name, out var mode))
                    {
                        tokenizer.State = mode;
                    }

                    break;
                }

                case HtmlTokenType.EndTag:
                {
                    var name = token.Name;

                    // An end tag no open element answers closes nothing.
                    if (_openNames.GetValueOrDefault(name) == 0)
                    {
                        break;
                    }

                    while (_open.Count > 0)
                    {
                        var top = Pop(at);

                        if (top.Name == name)
                        {
                            closed = top;
                            break;
                        }
                    }

                    tokenizer.IsAcceptingCharacterData = _foreign > 0;
                    break;
                }

                default:
                    break;
            }

            if (token.Type == HtmlTokenType.EndOfFile)
            {
                break;
            }
        }

        while (_open.Count > 0)
        {
            Pop(Text.Length);
        }
    }

    /// <summary>The element open last, closed where <paramref name="at"/> stands: an end tag's, or what closes it in its place.</summary>
    private MarkupElement Pop(long at)
    {
        var top = _elements[_open[^1]];
        _open.RemoveAt(_open.Count - 1);
        _openNames[top.Name]--;
        _foreign -= top.Name is "svg" or "math" && _foreign > 0 ? 1 : 0;
        top.Close = Math.Max(at, top.OpenEnd);
        top.End = top.Close;
        return top;
    }

    /// <summary>What a page's start tag of <paramref name="name"/> at <paramref name="at"/> closes before it opens, as HTML closes an element whose end tag is left out.</summary>
    private void Imply(string name, long at)
    {
        // A head is left at the first thing it does not hold, a body's start tag among them.
        if (_open.Count > 0 && _elements[_open[^1]].Name == "head" && !HeadContent.Contains(name))
        {
            Pop(at);
        }

        switch (name)
        {
            case "li":
                CloseItem(at, "li");
                break;

            case "dd" or "dt":
                CloseItem(at, "dd", "dt");
                break;

            case "option":
                CloseTop(at, "option");
                break;

            case "optgroup":
                CloseTop(at, "option");
                CloseTop(at, "optgroup");
                break;

            case "tr":
                CloseWithin(at, "table", "tr");
                break;

            case "td" or "th":
                CloseWithin(at, "tr", "td", "th");
                break;

            case "thead" or "tbody" or "tfoot":
                CloseWithin(at, "table", "thead", "tbody", "tfoot");
                break;

            default:
                break;
        }

        if (ClosesParagraph.Contains(name) && IsOpen("p"))
        {
            for (var index = _open.Count - 1; index >= 0; index--)
            {
                var held = _elements[_open[index]].Name;

                if (held == "p")
                {
                    PopTo(at, index);
                    break;
                }

                if (ButtonScope.Contains(held))
                {
                    break;
                }
            }
        }

        // A heading closes a heading left open.
        if (name is "h1" or "h2" or "h3" or "h4" or "h5" or "h6" && _open.Count > 0 && _elements[_open[^1]].Name is "h1" or "h2" or "h3" or "h4" or "h5" or "h6")
        {
            Pop(at);
        }
    }

    /// <summary>An item of <paramref name="names"/> left open closed by the next, unless an element of its own stands between.</summary>
    private void CloseItem(long at, params string[] names)
    {
        if (!IsOpen(names))
        {
            return;
        }

        for (var index = _open.Count - 1; index >= 0; index--)
        {
            var held = _elements[_open[index]].Name;

            if (names.Contains(held, StringComparer.Ordinal))
            {
                PopTo(at, index);
                return;
            }

            if (Special.Contains(held) && held is not ("address" or "div" or "p"))
            {
                return;
            }
        }
    }

    /// <summary>Whether an element of one of <paramref name="names"/> is open: what a closing by implication looks for, never walked for where none is.</summary>
    private bool IsOpen(params string[] names) => names.Any(name => _openNames.GetValueOrDefault(name) > 0);

    /// <summary>The element open last closed where it is <paramref name="name"/>.</summary>
    private void CloseTop(long at, string name)
    {
        if (_open.Count > 0 && _elements[_open[^1]].Name == name)
        {
            Pop(at);
        }
    }

    /// <summary>The nearest element of <paramref name="names"/> open inside the nearest <paramref name="boundary"/>, closed with what it holds.</summary>
    private void CloseWithin(long at, string boundary, params string[] names)
    {
        if (!IsOpen(names))
        {
            return;
        }

        for (var index = _open.Count - 1; index >= 0; index--)
        {
            var held = _elements[_open[index]].Name;

            if (names.Contains(held, StringComparer.Ordinal))
            {
                PopTo(at, index);
                return;
            }

            if (held == boundary || held is "table" or "template" or "html")
            {
                return;
            }
        }
    }

    /// <summary>Every element open from the last back to the one at <paramref name="index"/> of the open ones, closed.</summary>
    private void PopTo(long at, int index)
    {
        while (_open.Count > index)
        {
            Pop(at);
        }
    }

    /// <summary>
    /// Where the value of the attribute whose name of <paramref name="length"/> characters begins at
    /// <paramref name="name"/> begins: past its name, its <c>=</c> and its opening quote; where its name ends, for one
    /// written with no value.
    /// </summary>
    private long ValueAt(long name, int length)
    {
        var at = Math.Clamp(name + length, 0, Text.Length);
        var after = at;

        while (after < Text.Length && char.IsWhiteSpace(Text[after]))
        {
            after++;
        }

        if (after >= Text.Length || Text[after] != '=')
        {
            return at;
        }

        for (after++; after < Text.Length && char.IsWhiteSpace(Text[after]); after++)
        {
        }

        return after < Text.Length && Text[after] is '"' or '\'' ? after + 1 : after;
    }

    /// <summary>The tag at <paramref name="at"/> as the text writes it - <c>MyButton</c>, where the tokenizer says <c>mybutton</c>.</summary>
    private string Written(long at, string name)
    {
        var start = at + 1;

        if (start + name.Length > Text.Length || Text[at] != '<')
        {
            return name;
        }

        var written = Text.Substring(start, name.Length);
        return string.Equals(written, name, StringComparison.OrdinalIgnoreCase) ? ExtractText.Whole(written) : name;
    }
}

/// <summary>One element of markup, as <see cref="MarkupText"/> reads it.</summary>
internal sealed class MarkupElement(string tag, string name, long start, int line, int parent, IReadOnlyList<MarkupAttribute> attributes)
{
    /// <summary>Its tag as the text writes it: <c>MyButton</c>, <c>child-item</c>.</summary>
    public string Tag { get; } = tag;

    /// <summary>Its tag as HTML names it: lower case.</summary>
    public string Name { get; } = name;

    /// <summary>Where its start tag begins: its <c>&lt;</c>.</summary>
    public long Start { get; } = start;

    /// <summary>The line its start tag begins on.</summary>
    public int Line { get; } = line;

    /// <summary>The index of the element it is written inside, among the text's elements; -1 at the top.</summary>
    public int Parent { get; } = parent;

    /// <summary>Its attributes, in the order its start tag writes them, each name in lower case as HTML names it.</summary>
    public IReadOnlyList<MarkupAttribute> Attributes { get; } = attributes;

    /// <summary>Where its start tag ends: where what it holds begins.</summary>
    public long OpenEnd { get; internal set; }

    /// <summary>Where what it holds ends: where its end tag begins, or what closed it in its place.</summary>
    public long Close { get; internal set; }

    /// <summary>Where it ends: past its end tag, or at <see cref="Close"/> where no end tag of its own closed it.</summary>
    public long End { get; internal set; }

    /// <summary>The value of its attribute <paramref name="name"/>, the first it writes; <see langword="null"/> where it writes none.</summary>
    public string? Attribute(string name)
        => Attributes.FirstOrDefault(attribute => string.Equals(attribute.Name, name, StringComparison.Ordinal))?.Value;
}

/// <summary>One attribute of an element.</summary>
/// <param name="Name">Its name, in lower case as HTML names it.</param>
/// <param name="Value">Its value, character references read.</param>
/// <param name="Line">The line its name stands on.</param>
internal sealed record MarkupAttribute(string Name, string Value, int Line)
{
    /// <summary>Where its name begins in the text, which writes it as written - a dynamic argument's code in its case.</summary>
    public long NameAt { get; init; }

    /// <summary>Where its value begins in the text: past its <c>=</c> and its quote - where its code stands, for a reader of code given it there.</summary>
    public long ValueAt { get; init; }
}
