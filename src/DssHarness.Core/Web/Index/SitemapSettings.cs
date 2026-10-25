using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DssHarness.Core.Repository;

namespace DssHarness.Core.Web.Index;

/// <summary>What an index of a site reads, as its source's keys say it.</summary>
/// <param name="Site">The site: its scheme and host, as <c>https://digital.gov</c>.</param>
/// <param name="Sitemap">The sitemap's address; <see langword="null"/> where those <c>robots.txt</c> names are read, else <c>/sitemap.xml</c>.</param>
/// <param name="Include">Patterns of the paths of the pages read; every page where empty.</param>
/// <param name="Exclude">Patterns of the paths of pages never read.</param>
/// <param name="MaxPages">The most pages it holds.</param>
/// <param name="Refresh">How old it may be, and how long a page that could not be read waits, before it is read again.</param>
/// <remarks>Its pace is not its own: the source's turn on this machine is the run's gate's to give (<see cref="WebFetchGate.Pace"/>), its crawl delay the site's.</remarks>
public sealed record SitemapSettings(string Site, string? Sitemap, IReadOnlyList<GlobPattern> Include, IReadOnlyList<GlobPattern> Exclude, int MaxPages, TimeSpan Refresh)
{
    /// <summary>
    /// What its index holds depends on, said as one text: the site, the sitemap read, the patterns and <c>maxPages</c> -
    /// not how old the index may be, nor its pace. An index filled under another is filled again.
    /// </summary>
    public string Fingerprint
    {
        get
        {
            var said = JsonSerializer.Serialize(new[]
            {
                Site,
                Sitemap ?? string.Empty,
                string.Join('\n', Include),
                string.Join('\n', Exclude),
                MaxPages.ToString(CultureInfo.InvariantCulture),
            });

            return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(said)));
        }
    }

    /// <summary>
    /// Every setting said as one text: <see cref="Fingerprint"/> and how old the index may be, which is how long a page that
    /// could not be read waits. A fill that stopped short is held off under these alone; under any changed, it begins at once.
    /// </summary>
    public string All => $"{Fingerprint} {Refresh.Ticks.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>Whether <paramref name="address"/> is a page of the site whose path the patterns let be read (<see cref="Reads"/>).</summary>
    /// <param name="address">The page's address, absolute.</param>
    public bool ReadsPage(string address)
        => Uri.TryCreate(address, UriKind.Absolute, out var page)
            && string.Equals(page.GetLeftPart(UriPartial.Authority), Site, StringComparison.OrdinalIgnoreCase)
            && Reads(page.AbsolutePath);

    /// <summary>Whether a page whose path is <paramref name="path"/> is one the patterns let be read: included, where any are, and not excluded.</summary>
    /// <param name="path">The page's path, as its address writes it.</param>
    public bool Reads(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        // A path the patterns cannot read - the site's root, a doubled separator - is read where no pattern is to include.
        var readable = GlobPattern.Reads(path) ? path : null;

        return (Include.Count == 0 || (readable is not null && Include.Any(pattern => pattern.Matches(readable))))
            && (readable is null || !Exclude.Any(pattern => pattern.Matches(readable)));
    }
}
