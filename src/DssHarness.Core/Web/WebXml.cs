using System.Xml;
using System.Xml.Linq;

namespace DssHarness.Core.Web;

/// <summary>
/// XML read from the web - a feed, an OpenSearch description, a sitemap - the one way it is read: a document type passed
/// over and never acted on, so no entity it declares is expanded and nothing it names is fetched; and elements nested no
/// deeper than <see cref="MostDepth"/>, for a value read of a deeper one is read through every level beneath it.
/// </summary>
internal static class WebXml
{
    /// <summary>How deep elements may nest: far past any feed's, description's or sitemap's.</summary>
    public const int MostDepth = 64;

    private static readonly XmlReaderSettings Settings = new() { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, CloseInput = true };

    /// <summary>Reads <paramref name="text"/> as XML.</summary>
    /// <exception cref="XmlException">It is no XML, or nests deeper than <see cref="MostDepth"/>.</exception>
    public static XDocument Read(string text)
    {
        using var reader = Stream(text);
        return XDocument.Load(reader);
    }

    /// <summary>
    /// A reader of <paramref name="text"/> as XML, for a reader that takes of it only what it reads - a sitemap, as large as
    /// its protocol lets one be, read no further than its most entries - the whole text already held to be XML no deeper
    /// than <see cref="MostDepth"/>, in a pass that keeps nothing of it.
    /// </summary>
    /// <exception cref="XmlException">It is no XML, or nests deeper than <see cref="MostDepth"/>.</exception>
    public static XmlReader Stream(string text)
    {
        using (var scan = XmlReader.Create(new StringReader(text), Settings))
        {
            while (scan.Read())
            {
                if (scan.Depth > MostDepth)
                {
                    throw new XmlException($"its elements nest deeper than {MostDepth}, which no feed, description or sitemap does");
                }
            }
        }

        return XmlReader.Create(new StringReader(text), Settings);
    }
}
