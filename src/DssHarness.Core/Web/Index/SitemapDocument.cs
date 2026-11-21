using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using DssHarness.Core.Text;

namespace DssHarness.Core.Web.Index;

/// <summary>
/// One sitemap read, as the sitemaps protocol writes one: a <c>urlset</c>, each page's address and <c>lastmod</c>; a
/// <c>sitemapindex</c>, the sitemaps it names; or a text file of addresses, one a line. Or why it is none of them. Read as
/// it streams, an entry at a time. The protocol's own bounds are held as its facts and never cut unsaid: a sitemap holds at
/// most <see cref="MostEntries"/> entries and <see cref="MostBytes"/>, and a <c>&lt;loc&gt;</c> is under
/// <see cref="WebAddresses.SitemapLocLength"/> characters - an entry past them is named (<see cref="Refused"/>,
/// <see cref="Unread"/>), never passed over in silence.
/// </summary>
/// <param name="Pages">The pages it lists, each address with its <c>lastmod</c> as written, or none.</param>
/// <param name="Sitemaps">The sitemaps it names, where it is an index.</param>
/// <param name="Problem">Why it is no sitemap; <see langword="null"/> where it is one.</param>
public sealed record SitemapDocument(IReadOnlyList<(string Address, string? LastModified)> Pages, IReadOnlyList<string> Sitemaps, string? Problem)
{
    /// <summary>The most entries a sitemap holds: 50,000, the sitemaps protocol's most - a fact of the protocol's.</summary>
    public const int MostEntries = 50_000;

    /// <summary>The most bytes a sitemap is, once decompressed: the protocol's 50 MiB - a fact of the protocol's.</summary>
    public const long MostBytes = 50L * 1024 * 1024;

    /// <summary>What a fetch of a sitemap says of <see cref="MostBytes"/>, after the size, where one is past it.</summary>
    public const string MostBytesSaid = "the sitemaps protocol lets a sitemap be";

    /// <summary>
    /// A date and time as W3C Datetime writes it, which the sitemaps protocol names for <c>lastmod</c>: a year, then its
    /// month, its day, and a time to the minute, the second or any fraction of one, with its zone - by its grammar, a
    /// fraction of any length among it.
    /// </summary>
    private static readonly Regex W3cDatetime = LinearRegex.Compile(@"^[0-9]{4}(-[0-9]{2}(-[0-9]{2}(T[0-9]{2}:[0-9]{2}(:[0-9]{2}(\.[0-9]+)?)?(Z|[+-][0-9]{2}:[0-9]{2}))?)?)?$");

    /// <summary>
    /// The entries it lists that the protocol refuses - a <c>&lt;loc&gt;</c> of <see cref="WebAddresses.SitemapLocLength"/>
    /// characters or more - each as written, with why: named where the fill records them, and listed as no page.
    /// </summary>
    public IReadOnlyList<(string Loc, string Why)> Refused { get; init; } = [];

    /// <summary>
    /// How many entries it lists past the protocol's <see cref="MostEntries"/>, which were not read: the listing is then
    /// not whole, and is said so; none where it holds no more.
    /// </summary>
    public int Unread { get; init; }

    /// <summary>Whether <paramref name="lastModified"/> is a date and time by W3C Datetime's grammar, as a sitemap's <c>lastmod</c> is written.</summary>
    /// <param name="lastModified">A <c>lastmod</c>, as written.</param>
    public static bool IsDatetime(string lastModified)
    {
        ArgumentNullException.ThrowIfNull(lastModified);
        return W3cDatetime.IsMatch(lastModified);
    }

    /// <summary>Why <paramref name="loc"/> is refused by the protocol; <see langword="null"/> where it is not.</summary>
    /// <param name="loc">A <c>&lt;loc&gt;</c>, as written, trimmed.</param>
    public static string? LocProblem(string loc)
    {
        ArgumentNullException.ThrowIfNull(loc);

        return loc.Length >= WebAddresses.SitemapLocLength
            ? $"its <loc> is {loc.Length.ToString("N0", CultureInfo.InvariantCulture)} characters, and the sitemaps protocol's are under {WebAddresses.SitemapLocLength.ToString("N0", CultureInfo.InvariantCulture)}"
            : null;
    }

    /// <summary>Reads <paramref name="text"/>, a sitemap, its addresses read against <paramref name="address"/>, its own.</summary>
    /// <param name="text">The sitemap's text.</param>
    /// <param name="address">The sitemap's own address.</param>
    public static SitemapDocument Read(string text, string address)
    {
        ArgumentNullException.ThrowIfNull(text);

        string root;
        List<XElement> entries;
        int unread;

        try
        {
            using var reader = WebXml.Stream(text);
            reader.MoveToContent();
            root = reader.LocalName;
            (entries, unread) = root switch
            {
                "urlset" => Entries(reader, "url"),
                "sitemapindex" => Entries(reader, "sitemap"),
                _ => ([], 0),
            };
        }
        catch (XmlException ex)
        {
            return Lines(text, address) ?? new([], [], $"it is no XML ({ex.Message.TrimEnd('.')}), and no list of addresses");
        }

        static string? Child(XElement element, string name) => WebXml.TextOf(element.Elements().FirstOrDefault(child => child.Name.LocalName == name))?.Trim();

        var refused = new List<(string, string)>();

        string? Loc(XElement entry)
        {
            if (Child(entry, "loc") is not { } loc)
            {
                return null;
            }

            if (LocProblem(loc) is { } problem)
            {
                refused.Add((loc, problem));
                return null;
            }

            return WebAddresses.Absolute(loc, address);
        }

        switch (root)
        {
            case "urlset":
                var pages = new List<(string, string?)>();

                foreach (var url in entries)
                {
                    if (Loc(url) is { } page)
                    {
                        // A lastmod no grammar of a date reads is none said: the page is listed all the same.
                        var lastModified = Child(url, "lastmod");
                        pages.Add((page, lastModified is { Length: > 0 } && IsDatetime(lastModified) ? lastModified : null));
                    }
                }

                return new(pages, [], null) { Refused = refused, Unread = unread };

            case "sitemapindex":
                return new([], [.. entries.Select(Loc).OfType<string>()], null) { Refused = refused, Unread = unread };

            default:
                return new([], [], $"its root is <{root}>, where a sitemap's is <urlset> or <sitemapindex>");
        }
    }

    /// <summary>
    /// The root's children named <paramref name="name"/>, <paramref name="reader"/> standing on the root: each read whole,
    /// no more than <see cref="MostEntries"/>, any other child passed over unkept; and how many more it holds past them,
    /// passed over and counted.
    /// </summary>
    private static (List<XElement> Entries, int Unread) Entries(XmlReader reader, string name)
    {
        var entries = new List<XElement>();
        var unread = 0;

        if (reader.IsEmptyElement)
        {
            return (entries, unread);
        }

        reader.Read();

        while (!reader.EOF && !(reader.NodeType == XmlNodeType.EndElement && reader.Depth == 0))
        {
            if (reader.NodeType != XmlNodeType.Element)
            {
                reader.Read();
            }
            else if (reader.LocalName == name && entries.Count < MostEntries)
            {
                entries.Add((XElement)XNode.ReadFrom(reader));
            }
            else
            {
                if (reader.LocalName == name)
                {
                    unread++;
                }

                reader.Skip();
            }
        }

        return (entries, unread);
    }

    /// <summary>
    /// A text sitemap: every line that is not blank an address, a line ending at a CR, an LF or both; <see langword="null"/>
    /// where one is not - said at the first that is not, each line read as it comes - and those past the protocol's most
    /// counted, unread.
    /// </summary>
    private static SitemapDocument? Lines(string text, string address)
    {
        var pages = new List<(string, string?)>();
        var refused = new List<(string, string)>();
        var unread = 0;
        using var lines = new StringReader(text);

        while (lines.ReadLine() is { } read)
        {
            var line = read.Trim();

            if (line.Length == 0)
            {
                continue;
            }

            if (!WebAddresses.IsWeb(line))
            {
                return null;
            }

            if (pages.Count + refused.Count >= MostEntries)
            {
                unread++;
                continue;
            }

            if (LocProblem(line) is { } problem)
            {
                refused.Add((line, problem));
                continue;
            }

            if (WebAddresses.Absolute(line, address) is not { } page)
            {
                return null;
            }

            pages.Add((page, null));
        }

        return pages.Count + refused.Count > 0 ? new(pages, [], null) { Refused = refused, Unread = unread } : null;
    }
}
