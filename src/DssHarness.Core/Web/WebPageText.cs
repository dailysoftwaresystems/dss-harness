using System.Text;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;

namespace DssHarness.Core.Web;

/// <summary>
/// A page's HTML reduced to the text a reader of it sees: its title, then its body's text, a block a line of its own, a
/// preformatted block as it stands, what no reader sees - scripts, styles, templates, what is hidden - left out, nothing
/// of it run. Read by AngleSharp, which reads HTML as a browser does, malformed markup included, and fetches nothing it
/// names.
/// </summary>
public static class WebPageText
{
    /// <summary>The elements whose content no reader of a page sees as its text.</summary>
    private static readonly HashSet<string> Unseen = new(
        ["script", "style", "noscript", "template", "svg", "canvas", "iframe", "object", "embed", "head"],
        StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// How much of a field is read: 16 KiB - a title, a snippet and an error's words are each far shorter, and markup
    /// nested deep within more would take long to read.
    /// </summary>
    public const int MostFieldRead = 16 * 1024;

    /// <summary>How many of a page's nodes are walked between two looks at the reading's token.</summary>
    private const int NodesBetweenLooks = 1024;

    /// <summary>The headings of a page.</summary>
    private static readonly HashSet<string> Headings = new(["h1", "h2", "h3", "h4", "h5", "h6"], StringComparer.OrdinalIgnoreCase);

    /// <summary>The elements that stand as blocks: each begins a line of its own.</summary>
    private static readonly HashSet<string> Blocks = new(
        ["address", "article", "aside", "blockquote", "br", "dd", "details", "div", "dl", "dt", "fieldset", "figcaption", "figure",
         "footer", "form", "h1", "h2", "h3", "h4", "h5", "h6", "header", "hr", "li", "main", "nav", "ol", "p", "section",
         "summary", "table", "tbody", "tfoot", "thead", "tr", "ul"],
        StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The text of the page <paramref name="html"/> holds: its title on the first line where it has one, then its text.
    /// Its reading is stopped by <paramref name="cancellationToken"/>: markup nested deep takes the parser long.
    /// </summary>
    /// <param name="html">The page's markup, decoded.</param>
    /// <param name="cancellationToken">Stops the reading.</param>
    public static async Task<string> OfAsync(string html, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(html);

        var document = await new HtmlParser(new HtmlParserOptions { IsScripting = false }).ParseDocumentAsync(html, cancellationToken).ConfigureAwait(false);
        return Of(document, cancellationToken);
    }

    /// <summary>The text of <paramref name="document"/>, as <see cref="OfAsync"/> gives it; its walk stopped by <paramref name="cancellationToken"/>.</summary>
    internal static string Of(IHtmlDocument document, CancellationToken cancellationToken)
    {
        var title = Collapsed(document.Title ?? string.Empty);
        var text = BodyText(document, null, cancellationToken);

        return title.Length > 0 ? $"{title}\n\n{text}" : text;
    }

    /// <summary>
    /// The parts of the page <paramref name="html"/> holds that the harness's index of a site keeps: its title, the text a
    /// reader sees of each heading - one inside another a part of the outer's - and its body's text as
    /// <see cref="OfAsync"/> gives it, all read in the one walk of the page.
    /// </summary>
    /// <param name="html">The page's markup, decoded.</param>
    /// <param name="cancellationToken">Stops the reading.</param>
    public static async Task<WebPageParts> PartsAsync(string html, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(html);

        var document = await new HtmlParser(new HtmlParserOptions { IsScripting = false }).ParseDocumentAsync(html, cancellationToken).ConfigureAwait(false);
        var headings = new List<string>();
        var text = BodyText(document, headings, cancellationToken);

        return new WebPageParts(Collapsed(document.Title ?? string.Empty), headings, text);
    }

    /// <summary>
    /// A field that is markup - an excerpt's highlights, an entity, an element of its author's own - reduced to the text a
    /// reader of it sees, on one line, read no further than its most. Whether a field is markup is its source's word, never
    /// guessed: plain text that names an element, as <c>the &lt;canvas&gt; tag</c> does, is text (<see cref="OfText"/>).
    /// </summary>
    /// <param name="field">The field as the source answered it.</param>
    public static string OfMarkup(string field)
    {
        ArgumentNullException.ThrowIfNull(field);

        field = Output.TextCut.Head(field, MostFieldRead);

        if (field.IndexOfAny(['<', '&']) < 0)
        {
            return Collapsed(field);
        }

        var document = new HtmlParser(new HtmlParserOptions { IsScripting = false }).ParseDocument($"<body>{field}</body>");

        return string.Join(" ", BodyText(document, null, CancellationToken.None).Split('\n'));
    }

    /// <summary>
    /// A field that is text, never markup - an index's own text, a feed's text construct, a JSON source's field where it
    /// does not say it is markup - read no further than its most, its blanks made single, a '&lt;' or an '&amp;' in it as
    /// it stands.
    /// </summary>
    /// <param name="field">The field.</param>
    public static string OfText(string field)
    {
        ArgumentNullException.ThrowIfNull(field);

        return Collapsed(Output.TextCut.Head(field, MostFieldRead));
    }

    /// <summary>
    /// The text of <paramref name="document"/>'s body, a block a line, as <see cref="Of"/> gives it after the title; each
    /// heading's text added to <paramref name="headings"/>, where it is given.
    /// </summary>
    private static string BodyText(IHtmlDocument document, List<string>? headings, CancellationToken cancellationToken)
    {
        var pieces = new List<Piece> { new(new StringBuilder(), Preformatted: false) };

        if (document.Body is { } body)
        {
            Write(body, pieces, headings, cancellationToken);
        }

        return string.Join(
            "\n",
            pieces.Select(piece => piece.Preformatted ? piece.Text.ToString().Trim('\n', '\r') : Collapsed(piece.Text.ToString()))
                .Where(line => line.Length > 0));
    }

    /// <summary>
    /// Writes the text under <paramref name="root"/> into <paramref name="pieces"/>: a block a piece of its own, a
    /// preformatted block one as it stands, its own markup's text joined as it comes; what no reader sees passed by
    /// whole. Walked with a stack of its own, for a page nests as deep as its author likes and no thread's stack holds
    /// that, each node once, for a page holds as many as its author likes; the token looked at as it goes. Each
    /// outermost heading's text a reader sees is added to <paramref name="headings"/>, where it is given.
    /// </summary>
    private static void Write(INode root, List<Piece> pieces, List<string>? headings, CancellationToken cancellationToken)
    {
        var waiting = new Stack<(INode Node, bool Closing)>();
        var walked = 0;

        // How many preformatted blocks the walk is inside: there, every text is the block's as it stands.
        var preformatted = 0;

        // The text of the outermost heading the walk is inside, and how many headings it is inside.
        StringBuilder? heading = null;
        var inHeadings = 0;
        Push(root.ChildNodes);

        while (waiting.TryPop(out var at))
        {
            if (++walked % NodesBetweenLooks == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (at.Node is IText text)
            {
                pieces[^1].Text.Append(text.Data);
                heading?.Append(text.Data);
                continue;
            }

            if (at.Node is not IElement element || (!at.Closing && Unread(element)))
            {
                continue;
            }

            var pre = element.LocalName.Equals("pre", StringComparison.OrdinalIgnoreCase);

            // A reader sees a break between blocks and between cells: a heading's words are kept apart there too.
            if (heading is not null && (Blocks.Contains(element.LocalName) || element.LocalName is "td" or "th"))
            {
                heading.Append(' ');
            }

            if (at.Closing)
            {
                if (heading is not null && Headings.Contains(element.LocalName) && --inHeadings == 0)
                {
                    if (Collapsed(heading.ToString()) is { Length: > 0 } said)
                    {
                        headings!.Add(said);
                    }

                    heading = null;
                }

                if (pre)
                {
                    if (--preformatted == 0)
                    {
                        pieces.Add(new Piece(new StringBuilder(), Preformatted: false));
                    }
                }
                else if (preformatted > 0)
                {
                    // Markup inside a preformatted block adds nothing but its text.
                }
                else if (Blocks.Contains(element.LocalName))
                {
                    pieces.Add(new Piece(new StringBuilder(), Preformatted: false));
                }
                else if (element.LocalName is "td" or "th")
                {
                    pieces[^1].Text.Append('\t');
                }

                continue;
            }

            if (pre)
            {
                if (preformatted++ == 0)
                {
                    pieces.Add(new Piece(new StringBuilder(), Preformatted: true));
                }
            }
            else if (preformatted == 0 && Blocks.Contains(element.LocalName))
            {
                pieces.Add(new Piece(new StringBuilder(), Preformatted: false));
            }

            if (headings is not null && Headings.Contains(element.LocalName) && inHeadings++ == 0)
            {
                heading = new StringBuilder();
            }

            waiting.Push((element, true));
            Push(element.ChildNodes);
        }

        // Pushed last child first, so each is taken in the page's order.
        void Push(INodeList children)
        {
            for (var at = children.Length - 1; at >= 0; at--)
            {
                waiting.Push((children[at], false));
            }
        }
    }

    /// <summary>Whether <paramref name="element"/> is one no reader of the page sees: an unseen kind, or one marked hidden.</summary>
    private static bool Unread(IElement element) =>
        Unseen.Contains(element.LocalName)
        || element.HasAttribute("hidden")
        || string.Equals(element.GetAttribute("aria-hidden"), "true", StringComparison.Ordinal);

    /// <summary>A line with its runs of white space made one space, trimmed.</summary>
    private static string Collapsed(string line)
    {
        var builder = new StringBuilder(line.Length);
        var space = false;

        foreach (var character in line)
        {
            if (char.IsWhiteSpace(character))
            {
                space = builder.Length > 0;
                continue;
            }

            if (space)
            {
                builder.Append(' ');
                space = false;
            }

            builder.Append(character);
        }

        return builder.ToString();
    }

    /// <summary>A piece of a page's text: a line of flowing text, or a preformatted block kept as it stands.</summary>
    private sealed record Piece(StringBuilder Text, bool Preformatted);
}

/// <summary>What the harness's index of a site keeps of one page.</summary>
/// <param name="Title">Its title; empty where it has none.</param>
/// <param name="Headings">The text of each heading a reader sees, in the page's order.</param>
/// <param name="Text">Its body's text, a block a line.</param>
public sealed record WebPageParts(string Title, IReadOnlyList<string> Headings, string Text);
