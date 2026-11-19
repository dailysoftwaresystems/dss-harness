using System.Globalization;
using System.Text;

namespace DssHarness.Core.Output;

/// <summary>How a message names things in a line of text: one spelling for each, whichever command writes it.</summary>
public static class ReportText
{
    /// <summary>How many of a list a one-line message names before it counts the rest.</summary>
    public const int NamedLimit = 3;

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
        => FailedInOneLine(failed, noun, one, many, Listed);

    /// <summary>
    /// What failed for each of <paramref name="failed"/>, said in one line as the other overload says it, naming as many of
    /// the items as fit the line and saying the rest are in <paramref name="whole"/> (<see cref="Names.Line"/>): never a
    /// few of them and a count, where the whole list is kept elsewhere for the line to point at.
    /// </summary>
    /// <param name="failed">Each item that failed, with why, in the order they are named.</param>
    /// <param name="noun">What an item is, as one of them is called, to count them by.</param>
    /// <param name="one">The line for one item, given it and why.</param>
    /// <param name="many">The line for more, given how many, the items named, and why.</param>
    /// <param name="whole">Where every item is kept, as the line says it after <c>all in</c>: a record's path, a <c>--json</c> member.</param>
    /// <returns>No line where nothing failed, and one otherwise.</returns>
    public static IReadOnlyList<string> FailedInOneLine(
        IReadOnlyList<(string Item, string Why)> failed,
        string noun,
        Func<string, string, string> one,
        Func<string, string, string, string> many,
        string whole)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(whole);

        return FailedInOneLine(failed, noun, one, many, items => Names.Line(items, whole));
    }

    /// <summary>The one line of what failed, its items named by <paramref name="named"/>.</summary>
    private static IReadOnlyList<string> FailedInOneLine(
        IReadOnlyList<(string Item, string Why)> failed,
        string noun,
        Func<string, string, string> one,
        Func<string, string, string, string> many,
        Func<IReadOnlyList<string>, string> named)
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

        return [many(Counted(failed.Count, noun), named([.. failed.Select(item => item.Item)]), why)];
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

    /// <summary>
    /// A commit as a message names one: abbreviated as git abbreviates it in the repository the operation works in
    /// (<see cref="CommitIds"/>), and whole where no repository is known. Only a line a person reads abbreviates it: a
    /// record or a <c>--json</c> document carries the whole id.
    /// </summary>
    /// <param name="commit">The commit's id.</param>
    public static string Commit(string commit)
    {
        ArgumentNullException.ThrowIfNull(commit);

        return CommitIds.Current?.Shown(commit) ?? commit;
    }

    /// <summary>
    /// Text as it can appear in a one-line message. A control character in a file name, such as
    /// a newline, would split the line or reach the terminal raw, so such text is quoted and escaped (<see cref="Quoted"/>).
    /// </summary>
    /// <param name="text">The text.</param>
    public static string Printable(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        return text.Any(char.IsControl) ? Quoted(text) : text;
    }

    /// <summary>
    /// Text in double quotes on one line, whatever it holds: a newline, a carriage return and a tab written as
    /// <c>\n</c>, <c>\r</c> and <c>\t</c>, a quote and a backslash escaped, every other control character as
    /// <c>\uXXXX</c>, and everything else as it is - what <see cref="Printable"/> gives text that holds a control character,
    /// and what a name no plain spelling holds is written as wherever it is quoted.
    /// </summary>
    /// <param name="text">The text.</param>
    public static string Quoted(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

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

    /// <summary>
    /// The text <paramref name="printed"/> is the printable form of (<see cref="Printable"/>): the text a quoted form
    /// stands for, where reading its escapes back gives text whose printable form it is - and else <paramref name="printed"/>
    /// itself, so a name that only looks quoted, of no control character, is read as it is written.
    /// </summary>
    /// <param name="printed">The text as a line writes it.</param>
    public static string Unprinted(string printed)
    {
        ArgumentNullException.ThrowIfNull(printed);

        if (printed.Length < 2 || printed[0] != '"' || printed[^1] != '"')
        {
            return printed;
        }

        var text = new StringBuilder();

        for (var at = 1; at < printed.Length - 1; at++)
        {
            if (printed[at] != '\\' || at + 1 >= printed.Length - 1)
            {
                text.Append(printed[at]);
                continue;
            }

            at++;

            switch (printed[at])
            {
                case 'n':
                    text.Append('\n');
                    break;
                case 'r':
                    text.Append('\r');
                    break;
                case 't':
                    text.Append('\t');
                    break;
                case 'u' when at + 4 < printed.Length - 1 && int.TryParse(printed.AsSpan(at + 1, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var code):
                    text.Append((char)code);
                    at += 4;
                    break;
                default:
                    text.Append(printed[at]);
                    break;
            }
        }

        var read = text.ToString();
        return string.Equals(Printable(read), printed, StringComparison.Ordinal) ? read : printed;
    }
}
