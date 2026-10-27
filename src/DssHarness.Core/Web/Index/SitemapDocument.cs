using System.Xml;
using System.Xml.Linq;

namespace DssHarness.Core.Web.Index;

/// <summary>
/// One sitemap read, as the sitemaps protocol writes one: a <c>urlset</c>, each page's address and <c>lastmod</c>; a
/// <c>sitemapindex</c>, the sitemaps it names; or a text file of addresses, one a line. Or why it is none of them. Read as
/// it streams, no further than its most entries: a sitemap may be 50 MiB, and what is past its most is never held.
/// </summary>
/// <param name="Pages">The pages it lists, each address with its <c>lastmod</c> as written, or none.</param>
/// <param name="Sitemaps">The sitemaps it names, where it is an index.</param>
/// <param name="Problem">Why it is no sitemap; <see langword="null"/> where it is one.</param>
public sealed record SitemapDocument(IReadOnlyList<(string Address, string? LastModified)> Pages, IReadOnlyList<string> Sitemaps, string? Problem)
{
    /// <summary>The most entries a sitemap is read for: 50,000, the sitemaps protocol's most; past them, none is.</summary>
    public const int MostEntries = 50_000;

    /// <summary>The most bytes a sitemap is, once decompressed: the protocol's 50 MiB.</summary>
    public const long MostBytes = 50L * 1024 * 1024;

    /// <summary>The longest lastmod read: a W3C date and time, a fraction of a second among it; a longer one is none said.</summary>
    public const int MostLastModified = 40;

    /// <summary>Reads <paramref name="text"/>, a sitemap, its addresses read against <paramref name="address"/>, its own.</summary>
    /// <param name="text">The sitemap's text.</param>
    /// <param name="address">The sitemap's own address.</param>
    public static SitemapDocument Read(string text, string address)
    {
        ArgumentNullException.ThrowIfNull(text);

        string root;
        List<XElement> entries;

        try
        {
            using var reader = WebXml.Stream(text);
            reader.MoveToContent();
            root = reader.LocalName;
            entries = root switch
            {
                "urlset" => Entries(reader, "url"),
                "sitemapindex" => Entries(reader, "sitemap"),
                _ => [],
            };
        }
        catch (XmlException ex)
        {
            return Lines(text, address) ?? new([], [], $"it is no XML ({ex.Message.TrimEnd('.')}), and no list of addresses");
        }

        static string? Loc(XElement element) => element.Elements().FirstOrDefault(child => child.Name.LocalName == "loc")?.Value.Trim();

        switch (root)
        {
            case "urlset":
                var pages = new List<(string, string?)>();

                foreach (var url in entries)
                {
                    if (WebAddresses.Absolute(Loc(url), address) is { } page)
                    {
                        var lastModified = url.Elements().FirstOrDefault(child => child.Name.LocalName == "lastmod")?.Value.Trim();
                        pages.Add((page, string.IsNullOrEmpty(lastModified) || lastModified.Length > MostLastModified ? null : lastModified));
                    }
                }

                return new(pages, [], null);

            case "sitemapindex":
                return new([], [.. entries.Select(sitemap => WebAddresses.Absolute(Loc(sitemap), address)).OfType<string>()], null);

            default:
                return new([], [], $"its root is <{root}>, where a sitemap's is <urlset> or <sitemapindex>");
        }
    }

    /// <summary>
    /// The root's children named <paramref name="name"/>, <paramref name="reader"/> standing on the root: each read whole,
    /// no more than <see cref="MostEntries"/>, any other child passed over unkept, and nothing past the last read.
    /// </summary>
    private static List<XElement> Entries(XmlReader reader, string name)
    {
        var entries = new List<XElement>();

        if (reader.IsEmptyElement)
        {
            return entries;
        }

        reader.Read();

        while (entries.Count < MostEntries && !reader.EOF && !(reader.NodeType == XmlNodeType.EndElement && reader.Depth == 0))
        {
            if (reader.NodeType != XmlNodeType.Element)
            {
                reader.Read();
            }
            else if (reader.LocalName == name)
            {
                entries.Add((XElement)XNode.ReadFrom(reader));
            }
            else
            {
                reader.Skip();
            }
        }

        return entries;
    }

    /// <summary>
    /// A text sitemap: every line that is not blank an address, a line ending at a CR, an LF or both; <see langword="null"/>
    /// where one is not - said at the first that is not, each line read as it comes.
    /// </summary>
    private static SitemapDocument? Lines(string text, string address)
    {
        var pages = new List<(string, string?)>();
        using var lines = new StringReader(text);

        while (pages.Count < MostEntries && lines.ReadLine() is { } read)
        {
            var line = read.Trim();

            if (line.Length == 0)
            {
                continue;
            }

            if (!WebAddresses.IsWeb(line) || WebAddresses.Absolute(line, address) is not { } page)
            {
                return null;
            }

            pages.Add((page, null));
        }

        return pages.Count > 0 ? new(pages, [], null) : null;
    }
}
