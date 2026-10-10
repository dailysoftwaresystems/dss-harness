namespace RepoHarness.Core.Mutations;

/// <summary>A mutation of one site: how many times its before-text occurs there, and the site's bytes with it replaced.</summary>
/// <param name="Occurrences">How many times the before-text occurs, overlapping occurrences counted.</param>
/// <param name="Edited">The site with the before-text replaced, where it occurs exactly once; otherwise <see langword="null"/>.</param>
public sealed record SiteEditResult(int Occurrences, byte[]? Edited)
{
    /// <summary>
    /// Whether the edit was made and leaves the site holding what it held: what replaces the before-text is that text
    /// again, once both have the site's line endings - a mutation of nothing, which builds and runs as the unmutated tree
    /// does and would read as one no test caught. Never so of an edit that was not made, which its count says.
    /// </summary>
    public bool ChangesNothing { get; init; }
}

/// <summary>One text of several replaced together in a site.</summary>
/// <param name="Occurrences">How many times it occurs in the site as it stands, overlapping occurrences counted.</param>
/// <param name="ChangesNothing">
/// Whether it occurs exactly once and what replaces it is that text again, once both have the site's line endings.
/// </param>
public sealed record TextEdit(int Occurrences, bool ChangesNothing);

/// <summary>Several texts of one site replaced together, as one edit of it.</summary>
/// <param name="Texts">Each text, in the order given.</param>
/// <param name="Overlapping">
/// Each two texts, by their places in the order given, that each occur exactly once and share a byte of the site: which
/// of them the shared bytes become is something neither says.
/// </param>
/// <param name="Edited">
/// The site with every text replaced, where each occurs exactly once and no two overlap; otherwise <see langword="null"/>.
/// </param>
public sealed record SiteEdits(IReadOnlyList<TextEdit> Texts, IReadOnlyList<(int Earlier, int Later)> Overlapping, byte[]? Edited);

/// <summary>
/// A byte-exact replacement of texts in a site: each text must occur exactly once, and every other byte of the file
/// stays as it was - never a file read whole as text and written back, which rewrites what the edit did not touch.
/// </summary>
/// <remarks>
/// <para>
/// The texts a registry cites are read as their files hold them, less a UTF-8 byte order mark and one line ending at their
/// end, which an editor adds to every file it saves. Where a site and a text end their lines differently - a checkout
/// with CRLF endings and texts committed with LF ones, or the reverse - the text is given the site's endings, so one
/// registry serves either checkout. A site whose own lines end both ways takes the text as it is.
/// </para>
/// <para>
/// Occurrences overlap: <c>aa</c> occurs twice in <c>aaa</c>. Zero means the site moved under the arm, and two or more
/// that the arm does not know which one it hits; either is the arm's declaration failing, never a guess. So is an edit
/// that changes nothing, its after-text its before-text again: a mutation of nothing reddens no test, and would read as
/// one the tests let live.
/// </para>
/// </remarks>
public static class SiteEdit
{
    private static readonly byte[] ByteOrderMark = [0xEF, 0xBB, 0xBF];

    /// <summary>
    /// The text a file a row cites holds, as an edit reads it: its bytes, less a UTF-8 byte order mark at its start and
    /// one line ending - CRLF or LF - at its end.
    /// </summary>
    /// <param name="file">The file's bytes.</param>
    public static byte[] Text(byte[] file)
    {
        ArgumentNullException.ThrowIfNull(file);

        var text = file.AsSpan();

        if (text.StartsWith(ByteOrderMark))
        {
            text = text[ByteOrderMark.Length..];
        }

        if (text.EndsWith("\r\n"u8))
        {
            text = text[..^2];
        }
        else if (text.EndsWith("\n"u8))
        {
            text = text[..^1];
        }

        return text.ToArray();
    }

    /// <summary>
    /// <paramref name="text"/> with the line endings <paramref name="site"/> uses: CRLF where every line of the site ends
    /// so, LF where none does, and as it is where the site ends its lines both ways or has a single line.
    /// </summary>
    /// <param name="text">A text, as <see cref="Text"/> reads it.</param>
    /// <param name="site">The site's bytes.</param>
    public static byte[] Adapted(byte[] text, byte[] site)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(site);

        var feeds = site.AsSpan().Count((byte)'\n');
        var returns = site.AsSpan().Count("\r\n"u8);
        var lf = Lf(text);

        return feeds == 0 ? text
            : returns == feeds ? Crlf(lf)
            : returns == 0 ? lf
            : text;
    }

    /// <summary>How many times <paramref name="text"/> occurs in <paramref name="site"/>, overlapping occurrences counted.</summary>
    /// <param name="site">The site's bytes.</param>
    /// <param name="text">The text, as the site holds it.</param>
    public static int Occurrences(ReadOnlySpan<byte> site, ReadOnlySpan<byte> text)
    {
        if (text.IsEmpty)
        {
            throw new ArgumentException("An empty text occurs everywhere, and is never mutated.", nameof(text));
        }

        var count = 0;

        for (var at = site.IndexOf(text); at >= 0; count++)
        {
            var next = site[(at + 1)..].IndexOf(text);

            at = next < 0 ? -1 : at + 1 + next;
        }

        return count;
    }

    /// <summary>
    /// <paramref name="site"/> with <paramref name="before"/> replaced by <paramref name="after"/>, both given the site's
    /// line endings, where <paramref name="before"/> occurs exactly once - saying where that changes nothing of the site -
    /// and otherwise how many times it does.
    /// </summary>
    /// <param name="site">The site's bytes.</param>
    /// <param name="before">The text replaced, as <see cref="Text"/> reads it.</param>
    /// <param name="after">What replaces it, as <see cref="Text"/> reads it.</param>
    public static SiteEditResult Apply(byte[] site, byte[] before, byte[] after)
    {
        var edits = ApplyAll(site, [(before, after)]);

        return new SiteEditResult(edits.Texts[0].Occurrences, edits.Edited) { ChangesNothing = edits.Texts[0].ChangesNothing };
    }

    /// <summary>
    /// <paramref name="site"/> with each of <paramref name="texts"/> replaced, all given the site's line endings, as one
    /// edit: each before-text is looked for in the site as it stands - never as another of them left it, so the order
    /// they are given in changes nothing - and the site is edited only where each occurs exactly once and no two share
    /// a byte of it. Otherwise how often each occurs, and which overlap.
    /// </summary>
    /// <param name="site">The site's bytes.</param>
    /// <param name="texts">Each text replaced and what replaces it, as <see cref="Text"/> reads them.</param>
    public static SiteEdits ApplyAll(byte[] site, IReadOnlyList<(byte[] Before, byte[] After)> texts)
    {
        ArgumentNullException.ThrowIfNull(site);
        ArgumentNullException.ThrowIfNull(texts);

        var found = texts
            .Select(text =>
            {
                var needle = Adapted(text.Before, site);
                var count = Occurrences(site, needle);

                return (At: count == 1 ? site.AsSpan().IndexOf(needle) : -1, needle.Length, Replacement: Adapted(text.After, site), Count: count, Needle: needle);
            })
            .ToList();

        var overlapping = new List<(int Earlier, int Later)>();

        for (var later = 1; later < found.Count; later++)
        {
            for (var earlier = 0; earlier < later; earlier++)
            {
                var (a, b) = (found[earlier], found[later]);

                if (a.At >= 0 && b.At >= 0 && a.At < b.At + b.Length && b.At < a.At + a.Length)
                {
                    overlapping.Add((earlier, later));
                }
            }
        }

        byte[]? edited = null;

        if (found.All(text => text.Count == 1) && overlapping.Count == 0)
        {
            var bytes = new List<byte>(site.Length);
            var from = 0;

            foreach (var text in found.OrderBy(text => text.At))
            {
                bytes.AddRange(site.AsSpan(from, text.At - from));
                bytes.AddRange(text.Replacement);
                from = text.At + text.Length;
            }

            bytes.AddRange(site.AsSpan(from));
            edited = [.. bytes];
        }

        return new SiteEdits(
            [.. found.Select(text => new TextEdit(text.Count, text.Count == 1 && text.Needle.AsSpan().SequenceEqual(text.Replacement)))],
            overlapping,
            edited);
    }

    /// <summary><paramref name="text"/> with every CRLF made LF.</summary>
    private static byte[] Lf(byte[] text)
    {
        var lf = new List<byte>(text.Length);

        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] != '\r' || index + 1 >= text.Length || text[index + 1] != '\n')
            {
                lf.Add(text[index]);
            }
        }

        return [.. lf];
    }

    /// <summary><paramref name="lf"/>, whose lines end LF alone, with every LF made CRLF.</summary>
    private static byte[] Crlf(byte[] lf)
    {
        var crlf = new List<byte>(lf.Length + 16);

        foreach (var value in lf)
        {
            if (value == '\n')
            {
                crlf.Add((byte)'\r');
            }

            crlf.Add(value);
        }

        return [.. crlf];
    }
}
