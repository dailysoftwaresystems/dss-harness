using System.Globalization;
using System.Text;
using DssHarness.Core.Pages;

namespace DssHarness.Core.Output;

/// <summary>
/// How a one-line message names a list: every item that fits the line's room, in order, and for the rest how many there
/// are and where the whole list is. A line never names a few of a list and drops the others: what it cannot name it
/// counts, and it says where every item is kept - a record's path, a <c>--json</c> member - so nothing a message stands
/// for is lost to its length.
/// </summary>
public static class Names
{
    /// <summary>What stands between two names, as <see cref="ReportText.Listed"/> writes it.</summary>
    private const string Separator = ", ";

    /// <summary>
    /// <paramref name="items"/> as a line names them. Each is written printable (<see cref="ReportText.Printable"/>: one
    /// holding a control character quoted and escaped), and they are named in order, <c>, </c> between two, for as long
    /// as the names and what stands between them fit <paramref name="room"/> characters: <c>a, b, c</c> where all fit, and
    /// otherwise the names that fit, then <c>and N more (all in </c><paramref name="whole"/><c>)</c>. The first item is
    /// named whole however wide it is, so a line always names one, and none is ever cut; a list of one is that one, and a
    /// list of none is no text.
    /// </summary>
    /// <param name="items">What to name, in the order it is named.</param>
    /// <param name="whole">
    /// Where the whole list is, as the line says it after <c>all in</c>: a record's path, quoted as the message quotes a
    /// path, or a <c>--json</c> member. Always given, since the caller cannot know beforehand what fits.
    /// </param>
    /// <param name="room">The most characters the names and what stands between them take; the first name is named whatever its width.</param>
    public static string Line(IReadOnlyList<string> items, string whole, int room = PageRoom.Line)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentException.ThrowIfNullOrWhiteSpace(whole);
        ArgumentOutOfRangeException.ThrowIfNegative(room);

        if (items.Count == 0)
        {
            return string.Empty;
        }

        var line = new StringBuilder(ReportText.Printable(items[0]));
        var named = 1;

        for (; named < items.Count; named++)
        {
            var name = ReportText.Printable(items[named]);

            // Counted as long, so names of any length are weighed exactly against a room of any size.
            if ((long)line.Length + Separator.Length + name.Length > room)
            {
                break;
            }

            line.Append(Separator).Append(name);
        }

        if (named < items.Count)
        {
            line.Append(More(items.Count - named, whole));
        }

        return line.ToString();
    }

    /// <summary>
    /// <paramref name="items"/> as a message of several lines names them, one to a line: by the rule <see cref="Line"/>
    /// names them by, each written printable and named in order for as long as the lines named and the line break after
    /// each fit <paramref name="room"/> characters, and for the rest one line more, <c>and N more (all in </c><paramref name="whole"/><c>)</c>.
    /// The first item is named whole however wide it is, and a list of none is no lines.
    /// </summary>
    /// <param name="items">What to name, in the order it is named.</param>
    /// <param name="whole">Where the whole list is, as the last line says it after <c>all in</c>, as for <see cref="Line"/>.</param>
    /// <param name="room">The most characters the lines named and their breaks take; the first is named whatever its width.</param>
    public static IReadOnlyList<string> Lines(IReadOnlyList<string> items, string whole, int room = PageRoom.Line)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentException.ThrowIfNullOrWhiteSpace(whole);
        ArgumentOutOfRangeException.ThrowIfNegative(room);

        var lines = new List<string>();
        long taken = 0;

        foreach (var item in items)
        {
            var name = ReportText.Printable(item);

            // Counted as long, so lines of any length are weighed exactly against a room of any size.
            if (lines.Count > 0 && taken + name.Length + 1 > room)
            {
                lines.Add(More(items.Count - lines.Count, whole).TrimStart());
                break;
            }

            lines.Add(name);
            taken += name.Length + 1;
        }

        return lines;
    }

    /// <summary>What follows the names given, for the rest: <c> and N more (all in </c><paramref name="whole"/><c>)</c>.</summary>
    private static string More(int rest, string whole) => $" and {rest.ToString(CultureInfo.InvariantCulture)} more (all in {whole})";
}
