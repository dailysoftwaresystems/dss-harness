using System.Globalization;
using System.Text;

namespace RepoHarness.Core.Output;

/// <summary>How a message names things in a line of text: one spelling for each, whichever command writes it.</summary>
public static class ReportText
{
    /// <summary>How many of a list a one-line message names before it counts the rest.</summary>
    public const int NamedLimit = 3;

    /// <summary>How many characters of a commit's id a message shows.</summary>
    public const int CommitLength = 12;

    /// <summary>At most <see cref="NamedLimit"/> of <paramref name="items"/>, each printable, and how many more there are.</summary>
    /// <param name="items">What to name.</param>
    public static string Listed(IReadOnlyList<string> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        var named = string.Join(", ", items.Take(NamedLimit).Select(Printable));

        return items.Count > NamedLimit ? $"{named} and {items.Count - NamedLimit} more" : named;
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
