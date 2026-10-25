using System.Globalization;
using System.Text;

namespace DssHarness.Core.Web.Index;

/// <summary>
/// A site's <c>robots.txt</c> as RFC 9309 reads it, for one crawler: the groups that name it - else those for every crawler
/// (<c>*</c>) - their <c>Allow</c> and <c>Disallow</c> rules decided by the longest that matches, an <c>Allow</c> winning a
/// tie; its <c>Crawl-delay</c>; and every <c>Sitemap</c> it names. What the harness's index of a site reads of it: no page
/// a site refuses this tool is read.
/// </summary>
public sealed class RobotsTxt
{
    /// <summary>How much of a robots.txt is read: 500 KiB, as RFC 9309 (2.5) asks a crawler to read at least; past it, no line is.</summary>
    public const int MostRead = 500 * 1024;

    private readonly IReadOnlyList<Rule> _rules;

    private RobotsTxt(IReadOnlyList<Rule> rules, TimeSpan? crawlDelay, IReadOnlyList<string> sitemaps)
    {
        _rules = rules;
        CrawlDelay = crawlDelay;
        Sitemaps = sitemaps;
    }

    /// <summary>A site that refuses nothing: one whose <c>robots.txt</c> is not there, as a 4xx answers it.</summary>
    public static RobotsTxt AllowAll { get; } = new([], null, []);

    /// <summary>How long the site asks a crawler to wait between two requests; <see langword="null"/> where it asks nothing.</summary>
    public TimeSpan? CrawlDelay { get; }

    /// <summary>The sitemaps it names, as written, in its order.</summary>
    public IReadOnlyList<string> Sitemaps { get; }

    /// <summary>Reads <paramref name="text"/> for the crawler whose product token is <paramref name="agent"/>.</summary>
    /// <param name="text">The file's text.</param>
    /// <param name="agent">The crawler's product token, as its <c>User-Agent</c> begins: this tool's command.</param>
    public static RobotsTxt Read(string text, string agent)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentException.ThrowIfNullOrWhiteSpace(agent);

        // Read to the last whole line within the most read: a rule cut short would refuse what the whole one does not.
        if (text.Length > MostRead)
        {
            text = text[..Math.Max(text.LastIndexOfAny(['\r', '\n'], MostRead - 1), 0)];
        }

        var groups = new List<(List<string> Agents, List<(bool, string)> Rules, List<string> Delays)>();
        var sitemaps = new List<string>();
        (List<string> Agents, List<(bool, string)> Rules, List<string> Delays)? current = null;
        var agentsOpen = false;

        // A line ends at a CR, an LF or both (RFC 9309, 2.2): the empty line between the two of a CRLF is no record.
        foreach (var raw in text.Split('\r', '\n'))
        {
            var line = raw.Split('#', 2)[0].Trim();
            var colon = line.IndexOf(':');

            if (colon <= 0)
            {
                continue;
            }

            var key = line[..colon].Trim().ToLowerInvariant();
            var value = line[(colon + 1)..].Trim();

            switch (key)
            {
                case "user-agent":
                    if (!agentsOpen || current is null)
                    {
                        current = ([], [], []);
                        groups.Add(current.Value);
                        agentsOpen = true;
                    }

                    current.Value.Agents.Add(value);
                    break;

                case "allow" or "disallow" when current is not null:
                    agentsOpen = false;

                    // An empty Disallow refuses nothing, and an empty Allow allows nothing more: neither is a rule.
                    if (value.Length > 0)
                    {
                        current.Value.Rules.Add((key == "allow", Normalized(value)));
                    }

                    break;

                case "crawl-delay" when current is not null:
                    agentsOpen = false;
                    current.Value.Delays.Add(value);
                    break;

                case "sitemap":
                    // A sitemap belongs to no group, and leaves a group's agents as they were.
                    if (value.Length > 0)
                    {
                        sitemaps.Add(value);
                    }

                    break;

                default:
                    // A record this reader does not know is passed over, and leaves a group as it was.
                    break;
            }
        }

        var mine = groups.Where(group => group.Agents.Any(named => Names(named, agent))).ToList();

        if (mine.Count == 0)
        {
            mine = [.. groups.Where(group => group.Agents.Any(named => named == "*"))];
        }

        var delay = mine.SelectMany(group => group.Delays)
            .Select(said => double.TryParse(said, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds >= 0 && double.IsFinite(seconds) ? seconds : (double?)null)
            .FirstOrDefault(seconds => seconds is not null);

        return new RobotsTxt(
            [.. mine.SelectMany(group => group.Rules).Select(rule => Rule.Of(rule.Item1, rule.Item2))],
            delay is { } wait ? TimeSpan.FromSeconds(Math.Min(wait, WebRateLimit.MostPause.TotalSeconds)) : null,
            sitemaps);
    }

    /// <summary>
    /// Whether the crawler may read <paramref name="pathAndQuery"/>: allowed where no rule matches, or the longest that
    /// matches allows it, an allow winning a tie; <c>/robots.txt</c> itself always.
    /// </summary>
    /// <param name="pathAndQuery">The address's path and query, as the address writes them.</param>
    public bool Allows(string pathAndQuery)
    {
        ArgumentNullException.ThrowIfNull(pathAndQuery);

        var path = Normalized(pathAndQuery.Length == 0 ? "/" : pathAndQuery);

        if (path == "/robots.txt")
        {
            return true;
        }

        (bool Allow, int Length)? decided = null;

        foreach (var rule in _rules)
        {
            if (!rule.Matches(path))
            {
                continue;
            }

            if (decided is not { } best || rule.Length > best.Length || (rule.Length == best.Length && rule.Allow))
            {
                decided = (rule.Allow, rule.Length);
            }
        }

        return decided?.Allow ?? true;
    }

    /// <summary>Whether <paramref name="named"/>, a group's user-agent, names the crawler <paramref name="agent"/>: its product token, in any case.</summary>
    private static bool Names(string named, string agent)
    {
        var token = named.Split('/', ' ', '\t')[0];
        return string.Equals(token, agent, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether the rule's <paramref name="pattern"/> matches <paramref name="path"/>: from its start, <c>*</c> any run of
    /// characters, a <c>$</c> at its end the path's end; else the path need only begin with what it matches.
    /// </summary>
    internal static bool Matches(string pattern, string path) => Rule.Of(allow: false, pattern).Matches(path);

    /// <summary>
    /// A rule, read once: whether it allows, its length - which decides between two that match - and its pattern's runs of
    /// characters between its <c>*</c>s, and whether a <c>$</c> ends it.
    /// </summary>
    private sealed record Rule(bool Allow, int Length, string[] Runs, bool Anchored)
    {
        public static Rule Of(bool allow, string pattern)
        {
            var anchored = pattern.EndsWith('$');
            return new Rule(allow, pattern.Length, (anchored ? pattern[..^1] : pattern).Split('*'), anchored);
        }

        /// <summary>
        /// Whether it matches <paramref name="path"/>, in one pass of it: its first run where the path begins, each run
        /// after it where it is first found past the one before - which leaves the most of the path to the rest - and,
        /// where a <c>$</c> ends it, its last run where the path ends.
        /// </summary>
        public bool Matches(string path)
        {
            if (!path.StartsWith(Runs[0], StringComparison.Ordinal))
            {
                return false;
            }

            if (Runs.Length == 1)
            {
                return !Anchored || path.Length == Runs[0].Length;
            }

            var at = Runs[0].Length;

            for (var run = 1; run < Runs.Length - 1; run++)
            {
                var found = path.IndexOf(Runs[run], at, StringComparison.Ordinal);

                if (found < 0)
                {
                    return false;
                }

                at = found + Runs[run].Length;
            }

            var last = Runs[^1];

            return Anchored
                ? path.Length - last.Length >= at && path.EndsWith(last, StringComparison.Ordinal)
                : path.IndexOf(last, at, StringComparison.Ordinal) >= 0;
        }
    }

    /// <summary>
    /// A path or a pattern as both are compared: each character past ASCII percent-encoded as UTF-8, and every
    /// percent-encoding's digits in upper case, as RFC 9309 asks of both before they are compared.
    /// </summary>
    internal static string Normalized(string path)
    {
        var builder = new StringBuilder(path.Length);

        for (var at = 0; at < path.Length; at++)
        {
            var character = path[at];

            if (character == '%' && at + 2 < path.Length && Uri.IsHexDigit(path[at + 1]) && Uri.IsHexDigit(path[at + 2]))
            {
                builder.Append('%').Append(char.ToUpperInvariant(path[at + 1])).Append(char.ToUpperInvariant(path[at + 2]));
                at += 2;
            }
            else if (character < 0x80)
            {
                builder.Append(character);
            }
            else
            {
                var length = char.IsHighSurrogate(character) && at + 1 < path.Length && char.IsLowSurrogate(path[at + 1]) ? 2 : 1;

                foreach (var octet in Encoding.UTF8.GetBytes(path.Substring(at, length)))
                {
                    builder.Append('%').Append(octet.ToString("X2", CultureInfo.InvariantCulture));
                }

                at += length - 1;
            }
        }

        return builder.ToString();
    }
}
