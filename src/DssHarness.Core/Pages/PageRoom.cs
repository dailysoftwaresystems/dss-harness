namespace DssHarness.Core.Pages;

/// <summary>
/// The one owner of how much an answer's page holds. A page size is no limit: every entry of an answer stays reachable,
/// by asking its page, so a size only says how much of the whole one look gives.
/// </summary>
public static class PageRoom
{
    /// <summary>
    /// The characters held back from a page's entries for what surrounds them: the header, and the lines that say a
    /// section goes on. The notes an answer ends with are entries of their own, held within the room as any entry is.
    /// </summary>
    public const int Reserved = 4096;

    /// <summary>
    /// The least room a page gives its entries, whatever its leading lines take: what keeps a page of an answer whose
    /// leading lines are long moving the answer on.
    /// </summary>
    public const int Least = 256;

    /// <summary>The most characters one answer of the graph tool holds.</summary>
    public const int GraphAnswer = 24 * 1024;

    /// <summary>The most characters one page of a tool's answer to a model holds.</summary>
    public const int ModelAnswer = 32 * 1024;

    /// <summary>
    /// The most characters a one-line message gives a list before it says where the whole list is: the room a list's
    /// names are given in, before the line names where the whole list is kept.
    /// </summary>
    public const int Line = 4096;

    /// <summary>
    /// The room a page of an answer of at most <paramref name="characters"/> gives its entries, once
    /// <see cref="Reserved"/> and the <paramref name="leading"/> lines said on every page are taken - and never less than
    /// <see cref="Least"/>.
    /// </summary>
    /// <param name="characters">The most characters the answer's page may hold.</param>
    /// <param name="leading">The lines every page begins with.</param>
    public static int For(int characters, IReadOnlyList<string> leading)
    {
        ArgumentNullException.ThrowIfNull(leading);

        return Math.Max(Least, characters - Reserved - leading.Sum(line => line.Length + 1));
    }
}
