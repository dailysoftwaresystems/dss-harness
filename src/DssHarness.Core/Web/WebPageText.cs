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

    /// <summary>How many of a page's nodes are walked between two looks at the reading's token.</summary>
    private const int NodesBetweenLooks = 1024;

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
        var pieces = new List<Piece> { new(new StringBuilder(), Preformatted: false) };

        if (document.Body is { } body)
        {
            Write(body, pieces, cancellationToken);
        }

        var text = string.Join(
            "\n",
            pieces.Select(piece => piece.Preformatted ? piece.Text.ToString().Trim('\n', '\r') : Collapsed(piece.Text.ToString()))
                .Where(line => line.Length > 0));

        return title.Length > 0 ? $"{title}\n\n{text}" : text;
    }

    /// <summary>
    /// Writes the text under <paramref name="root"/> into <paramref name="pieces"/>: a block a piece of its own, a
    /// preformatted block one as it stands, its own markup's text joined as it comes; what no reader sees passed by
    /// whole. Walked with a stack of its own, for a page nests as deep as its author likes and no thread's stack holds
    /// that, each node once, for a page holds as many as its author likes; the token looked at as it goes.
    /// </summary>
    private static void Write(INode root, List<Piece> pieces, CancellationToken cancellationToken)
    {
        var waiting = new Stack<(INode Node, bool Closing)>();
        var walked = 0;

        // How many preformatted blocks the walk is inside: there, every text is the block's as it stands.
        var preformatted = 0;
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
                continue;
            }

            if (at.Node is not IElement element || (!at.Closing && Unread(element)))
            {
                continue;
            }

            var pre = element.LocalName.Equals("pre", StringComparison.OrdinalIgnoreCase);

            if (at.Closing)
            {
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
