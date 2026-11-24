using System.Globalization;
using System.Text;

namespace DssHarness.Core.Output;

/// <summary>How a message names things in a line of text: one spelling for each, whichever command writes it.</summary>
public static class ReportText
{
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

    /// <summary>
    /// What failed for each of <paramref name="failed"/>, said in one line however many there are - a full disk would
    /// otherwise say it once for every item a command meets: one as <paramref name="one"/> says it, given the item and why;
    /// more as <paramref name="many"/> does, given how many (<see cref="Counted"/> of <paramref name="noun"/>), every one of
    /// them (<see cref="Names.All"/>) - the line is the one place the list is kept - and why: the first reason, and how
    /// many other reasons there were where they differ.
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
        => FailedInOneLine(failed, noun, one, many, Names.All);

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
    /// and what a name no plain spelling holds is written as wherever it is quoted. The one rule of the escapes with the
    /// double quote as its mark (QuotedBy), the only form ever written: a single-quoted one is read (<see cref="Unquoted"/>)
    /// and never written, so every answer, id and record keeps one form.
    /// </summary>
    /// <param name="text">The text.</param>
    public static string Quoted(string text) => QuotedBy(text, '"');

    /// <summary>
    /// The one rule of the quoted form (<see cref="Quoted"/>), with <paramref name="mark"/> as its quote mark: the text in
    /// that mark on one line, a newline, a carriage return and a tab written as <c>\n</c>, <c>\r</c> and <c>\t</c>, a
    /// backslash and the mark itself escaped, every other control character as <c>\uXXXX</c>, and everything else as it is
    /// - the other mark among it. A single-quoted form is only ever read (<see cref="Unquoted"/>): <see cref="Quoted"/>
    /// writes the double one alone.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="mark">The quote mark the form is written with.</param>
    private static string QuotedBy(string text, char mark)
    {
        ArgumentNullException.ThrowIfNull(text);

        var builder = new StringBuilder().Append(mark);

        foreach (var character in text)
        {
            if (character == mark)
            {
                builder.Append('\\').Append(mark);
                continue;
            }

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

        return builder.Append(mark).ToString();
    }

    /// <summary>
    /// The text <paramref name="quoted"/> is the quoted form of, in either quote mark - a double-quoted text as Quoted writes
    /// it, or a single-quoted one as the same rule writes it with ' as its mark (QuotedBy): its escapes read back - \n, \r,
    /// \t, \\, \uXXXX and the backslash before its own mark - and null where it is no form that rule writes with its mark: no
    /// mark or two different marks around it, its own mark unescaped inside it, an escape the rule never writes (\" inside
    /// single quotes among them), or a \u of a character the rule writes as it is. Read once, in time linear in it; the one
    /// reader of the escapes.
    /// </summary>
    /// <param name="quoted">The text as a quoted form writes it.</param>
    public static string? Unquoted(string quoted)
    {
        ArgumentNullException.ThrowIfNull(quoted);

        if (quoted.Length < 2 || quoted[0] is not ('"' or '\'') || quoted[^1] != quoted[0])
        {
            return null;
        }

        var mark = quoted[0];
        var text = new StringBuilder(quoted.Length);

        for (var at = 1; at < quoted.Length - 1; at++)
        {
            if (quoted[at] == mark)
            {
                return null;
            }

            if (quoted[at] != '\\')
            {
                text.Append(quoted[at]);
                continue;
            }

            if (++at >= quoted.Length - 1)
            {
                return null;
            }

            if (quoted[at] == mark)
            {
                text.Append(mark);
                continue;
            }

            switch (quoted[at])
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
                case '\\':
                    text.Append('\\');
                    break;
                case 'u' when at + 4 < quoted.Length - 1 && int.TryParse(quoted.AsSpan(at + 1, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var code):
                    text.Append((char)code);
                    at += 4;
                    break;
                default:
                    return null;
            }
        }

        var read = text.ToString();
        return string.Equals(QuotedBy(read, mark), quoted, StringComparison.Ordinal) ? read : null;
    }

    /// <summary>
    /// The text <paramref name="printed"/> is the printable form of (<see cref="Printable"/>): the text a quoted form
    /// stands for, where reading its escapes back (<see cref="Unquoted"/>) gives text whose printable form it is - and else
    /// <paramref name="printed"/> itself, so a name that only looks quoted, of no control character, is read as it is
    /// written, and a single-quoted text, which is never the form <see cref="Printable"/> writes, too.
    /// </summary>
    /// <param name="printed">The text as a line writes it.</param>
    public static string Unprinted(string printed)
    {
        ArgumentNullException.ThrowIfNull(printed);

        return Unquoted(printed) is { } read && string.Equals(Printable(read), printed, StringComparison.Ordinal) ? read : printed;
    }
}
