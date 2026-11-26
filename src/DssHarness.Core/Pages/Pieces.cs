using DssHarness.Core.Output;

namespace DssHarness.Core.Pages;

/// <summary>
/// The one rule an entry wider than a page is given in pieces by: a page that holds nothing yet takes as much of what is
/// left of the entry as its room holds, cut by characters and never inside a surrogate pair (<see cref="TextCut.Head"/>),
/// at least one character past the mark a continued piece begins with, and marks the piece with
/// <see cref="TextCut.Ellipsis"/> where the entry goes on; the next piece begins with the same mark. The pages of an
/// answer laid in memory (<see cref="Paging"/>) and of a text streamed from its file (<see cref="TextPages"/>) cut by it
/// alike.
/// </summary>
internal static class Pieces
{
    /// <summary>
    /// The most characters the least piece of an entry takes: the mark a continued piece begins with, a surrogate pair, the
    /// mark that says it goes on, and its line's end - so a page whose room left holds it moves any entry on within its room.
    /// </summary>
    public const int Least = 5;

    /// <summary>What is left of an entry of <paramref name="text"/> once <paramref name="offset"/> of its characters were given: the entry whole at none, else the rest marked as going on from before.</summary>
    /// <param name="text">The entry's text.</param>
    /// <param name="offset">How many of its characters the pages before gave.</param>
    public static string Rest(string text, int offset) => offset == 0 ? text : TextCut.Ellipsis + text[offset..];

    /// <summary>
    /// How many of the characters of what is left of an entry <see cref="Head"/> may look at, its leading mark among them:
    /// what a reader that streams an entry hands it of the rest, or the rest whole where it is shorter, so the head it
    /// cuts is the one the whole rest gives.
    /// </summary>
    /// <param name="continued">Whether the rest goes on from a piece before it, and so begins with the mark.</param>
    /// <param name="room">The characters the page has left for the piece, its line's end among them.</param>
    public static int Window(bool continued, int room) => Keep(continued, room) + 2;

    /// <summary>
    /// The piece of <paramref name="rest"/> a page that holds nothing yet gives, before the mark that says it goes on: as
    /// many characters as <paramref name="room"/> holds with the mark and the line's end - never fewer than one past a
    /// continued piece's leading mark, and never half of a surrogate pair. Where it is <paramref name="rest"/> whole, the
    /// rest is no piece, and is given whole.
    /// </summary>
    /// <param name="rest">What is left of the entry (<see cref="Rest"/>), or as much of it as <see cref="Window"/> says.</param>
    /// <param name="continued">Whether the rest goes on from a piece before it, and so begins with the mark.</param>
    /// <param name="room">The characters the page has left for the piece, its line's end among them.</param>
    public static string Head(string rest, bool continued, int room)
    {
        ArgumentNullException.ThrowIfNull(rest);

        var lead = continued ? 1 : 0;
        var head = TextCut.Head(rest, Keep(continued, room));

        if (head.Length <= lead)
        {
            head = rest[..Math.Min(rest.Length, lead + (char.IsHighSurrogate(rest[lead]) ? 2 : 1))];
        }

        return head;
    }

    /// <summary>A piece's text: its head, and the mark that says the entry goes on.</summary>
    /// <param name="head">The piece's head (<see cref="Head"/>).</param>
    public static string Marked(string head) => head + TextCut.Ellipsis;

    /// <summary>How many of the entry's own characters a piece of <paramref name="head"/> gave: its head, less the mark a continued one begins with.</summary>
    /// <param name="head">The piece's head (<see cref="Head"/>).</param>
    /// <param name="continued">Whether it went on from a piece before it.</param>
    public static int Taken(string head, bool continued) => head.Length - (continued ? 1 : 0);

    /// <summary>The most characters a piece's head keeps: the room less the mark and the line's end, at least one past a continued piece's leading mark.</summary>
    private static int Keep(bool continued, int room) => Math.Max((continued ? 1 : 0) + 1, room - 2);
}
