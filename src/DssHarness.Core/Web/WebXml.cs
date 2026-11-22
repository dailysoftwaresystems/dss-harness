using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace DssHarness.Core.Web;

/// <summary>
/// XML read from the web - a feed, an OpenSearch description, a sitemap - the one way it is read: a document type passed
/// over and never acted on, so no entity it declares is expanded and nothing it names is fetched - a setting of security,
/// not of size - and elements nested as deep as the document nests them. The reader is iterative, and what is read of an
/// element is read through <see cref="Text"/>, a walk on a stack of its own: no depth of a document reaches past a thread's
/// stack, so a document is read whole however deep it nests.
/// </summary>
internal static class WebXml
{
    private static readonly XmlReaderSettings Settings = new() { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, CloseInput = true };

    /// <summary>Reads <paramref name="text"/> as XML.</summary>
    /// <exception cref="XmlException">It is no XML.</exception>
    public static XDocument Read(string text)
    {
        using var reader = Stream(text);
        return XDocument.Load(reader);
    }

    /// <summary>
    /// A reader of <paramref name="text"/> as XML, for a reader that takes of it only what it reads - a sitemap, as large as
    /// its protocol lets one be - in one pass that keeps nothing it has passed.
    /// </summary>
    public static XmlReader Stream(string text) => XmlReader.Create(new StringReader(text), Settings);

    /// <summary>
    /// The text <paramref name="element"/> holds, every text and CDATA section beneath it in the document's order, as
    /// <see cref="XElement.Value"/> gives it - but walked on a stack of its own, where <see cref="XElement.Value"/>
    /// recurses once a level.
    /// </summary>
    /// <param name="element">The element.</param>
    public static string Text(XElement element)
    {
        ArgumentNullException.ThrowIfNull(element);

        var text = new StringBuilder();

        // DescendantNodes walks by the nodes' parent links, never by a recursion: every text beneath, in order.
        foreach (var node in element.DescendantNodes())
        {
            if (node is XText piece)
            {
                text.Append(piece.Value);
            }
        }

        return text.ToString();
    }

    /// <summary>The text of <paramref name="element"/>, where it is given (<see cref="Text"/>); <see langword="null"/> where it is not.</summary>
    /// <param name="element">The element, or none.</param>
    public static string? TextOf(XElement? element) => element is null ? null : Text(element);
}
