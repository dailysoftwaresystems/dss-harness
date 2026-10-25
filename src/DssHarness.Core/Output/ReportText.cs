using System.Globalization;
using System.Text;

namespace DssHarness.Core.Output;

/// <summary>How a message names things in a line of text: one spelling for each, whichever command writes it.</summary>
public static class ReportText
{
    /// <summary>How many of a list a one-line message names before it counts the rest.</summary>
    public const int NamedLimit = 3;

    /// <summary>How many characters of a commit's id a message shows.</summary>
    public const int CommitLength = 12;

    /// <summary>The letters that, before a final <c>y</c>, leave a noun whole in its plural: <c>key(s)</c>.</summary>
    private const string Vowels = "aeiou";

    /// <summary>The endings, each a hiss, that a plural adds <c>es</c> to: <c>process(es)</c>.</summary>
    private static readonly string[] Hisses = ["s", "x", "z", "ch", "sh"];

    /// <summary>A time limit as a message says it: whole minutes as such (<c>5 minutes</c>), anything else in seconds (<c>90 s</c>).</summary>
    /// <param name="limit">The limit.</param>
    public static string Limit(TimeSpan limit)
        => limit.TotalMinutes >= 1 && limit.TotalMinutes == Math.Floor(limit.TotalMinutes)
            ? $"{limit.TotalMinutes.ToString(CultureInfo.InvariantCulture)} minute{(limit.TotalMinutes == 1 ? string.Empty : "s")}"
            : $"{limit.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)} s";

    /// <summary>At most <see cref="NamedLimit"/> of <paramref name="items"/>, each printable, and how many more there are.</summary>
    /// <param name="items">What to name.</param>
    public static string Listed(IReadOnlyList<string> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        var named = string.Join(", ", items.Take(NamedLimit).Select(Printable));

        return items.Count > NamedLimit ? $"{named} and {items.Count - NamedLimit} more" : named;
    }

    /// <summary>
    /// What failed for each of <paramref name="failed"/>, said in one line however many there are - a full disk would
    /// otherwise say it once for every item a command meets: one as <paramref name="one"/> says it, given the item and why;
    /// more as <paramref name="many"/> does, given how many (<see cref="Counted"/> of <paramref name="noun"/>), as many of
    /// them as a line names (<see cref="Listed"/>), and why - the first reason, and how many other reasons there were where
    /// they differ.
    /// </summary>
    /// <param name="failed">Each item that failed, with why, in the order they are named.</param>
    /// <param name="noun">What an item is, as one of them is called, to count them by.</param>
    /// <param name="one">The line for one item, given it and why.</param>
    /// <param name="many">The line for more, given how many, the items named, and why.</param>
    /// <returns>No line where nothing failed, and one otherwise.</returns>
    public static IReadOnlyList<string> FailedInOneLine(
        IReadOnlyList<(string Item, string Why)> failed,
        string noun,
        Func<string, string, string> one,
        Func<string, string, string, string> many)
    {
        ArgumentNullException.ThrowIfNull(failed);
        ArgumentNullException.ThrowIfNull(one);
        ArgumentNullException.ThrowIfNull(many);

        if (failed.Count == 0)
        {
            return [];
        }

        if (failed.Count == 1)
        {
            return [one(failed[0].Item, failed[0].Why)];
        }

        var others = failed.Select(item => item.Why).Distinct(StringComparer.Ordinal).Count() - 1;
        var why = others == 0 ? failed[0].Why : $"{failed[0].Why}; and {Counted(others, "other reason")}";

        return [many(Counted(failed.Count, noun), Listed([.. failed.Select(item => item.Item)]), why)];
    }

    /// <summary>
    /// How many of something there are, as a message says it: the number in digits alone, the same under every
    /// culture, then what is counted with the letters that would make it plural in brackets, so one wording reads
    /// true of one and of many - <c>3 path(s)</c>, <c>2 copy(ies)</c>, <c>4 process(es)</c>.
    /// </summary>
    /// <param name="count">How many there are.</param>
    /// <param name="noun">What is counted, as one of them is called: <c>path</c>, <c>mutation worker</c>.</param>
    public static string Counted(long count, string noun)
    {
        ArgumentException.ThrowIfNullOrEmpty(noun);

        return $"{count.ToString(CultureInfo.InvariantCulture)} {noun}({PluralLetters(noun)})";
    }

    /// <summary>
    /// The letters a count puts in brackets after <paramref name="noun"/>: <c>ies</c> where it ends in a consonant
    /// and <c>y</c>, <c>es</c> where it ends in a hiss, and <c>s</c> otherwise.
    /// </summary>
    private static string PluralLetters(string noun)
    {
        if (noun.Length > 1 && noun[^1] == 'y' && char.IsLetter(noun[^2]) && !Vowels.Contains(char.ToLowerInvariant(noun[^2])))
        {
            return "ies";
        }

        return Hisses.Any(hiss => noun.EndsWith(hiss, StringComparison.Ordinal)) ? "es" : "s";
    }

    /// <summary>A commit as a message names one: the first <see cref="CommitLength"/> characters of its id.</summary>
    /// <param name="commit">The commit's id.</param>
    public static string Commit(string commit)
    {
        ArgumentNullException.ThrowIfNull(commit);

        return commit.Length <= CommitLength ? commit : commit[..CommitLength];
    }

    /// <summary>
    /// Text as it can appear in a one-line message. A control character in a file name, such as
    /// a newline, would split the line or reach the terminal raw, so such text is quoted and escaped.
    /// </summary>
    /// <param name="text">The text.</param>
    public static string Printable(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (!text.Any(char.IsControl))
        {
            return text;
        }

        var builder = new StringBuilder("\"");

        foreach (var character in text)
        {
            switch (character)
            {
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                default:
                    if (char.IsControl(character))
                    {
                        builder.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(character);
                    }

                    break;
            }
        }

        return builder.Append('"').ToString();
    }
}
