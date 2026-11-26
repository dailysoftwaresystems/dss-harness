using System.Text;

namespace DssHarness.Core.Git;

/// <summary>
/// How the tool writes a section of a git configuration file, so that git reads each value back exactly as it was given:
/// every value in double quotes, with the backslash, the double quote, the line break and the tab escaped as git's own
/// parser reads them. A path holds any of those on some system, and written bare a backslash would be read as an escape, a
/// <c>;</c> or a <c>#</c> as the start of a comment, and leading or trailing blanks dropped.
/// </summary>
internal static class GitConfigText
{
    /// <summary>The section <paramref name="name"/>, holding <paramref name="entries"/> in the order given, as lines ending in a line feed.</summary>
    /// <param name="name">The section's name, such as <c>core</c>: written as given, so one of letters, digits, <c>-</c> and <c>.</c>.</param>
    /// <param name="entries">Each key, without its section, and its value.</param>
    public static string Section(string name, IEnumerable<(string Key, string Value)> entries)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(entries);

        var text = new StringBuilder().Append('[').Append(name).Append("]\n");

        foreach (var (key, value) in entries)
        {
            text.Append('\t').Append(key).Append(" = ").Append(Quoted(value)).Append('\n');
        }

        return text.ToString();
    }

    /// <summary>
    /// Whether <paramref name="text"/>, a git configuration file's, may hold a header opening the section
    /// <paramref name="name"/> or one of its subsections - <c>[name]</c>, <c>[name "sub"]</c>, <c>[name.sub]</c> - as git's
    /// parser reads a header: <c>[</c> followed at once by the name, in any case, and by no character a section's name goes
    /// on with (a letter, a digit or <c>-</c>). A file holding no such spelling holds no key of the section, whatever its
    /// values, comments and other sections spell; one that holds it - a value or a comment spelling it among them - may.
    /// </summary>
    /// <param name="text">The configuration file's text.</param>
    /// <param name="name">The section's name: letters, digits and <c>-</c>.</param>
    public static bool MayOpenSection(string text, string name)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var opening = "[" + name;

        for (var at = text.IndexOf(opening, StringComparison.OrdinalIgnoreCase); at >= 0; at = text.IndexOf(opening, at + 1, StringComparison.OrdinalIgnoreCase))
        {
            var after = at + opening.Length;

            if (after == text.Length || !(char.IsAsciiLetterOrDigit(text[after]) || text[after] == '-'))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary><paramref name="value"/> as a git configuration file holds it: in double quotes, escaped where git's parser reads an escape.</summary>
    /// <param name="value">The value.</param>
    public static string Quoted(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var quoted = new StringBuilder("\"");

        foreach (var character in value)
        {
            quoted.Append(character switch
            {
                '\\' => @"\\",
                '"' => "\\\"",
                '\n' => @"\n",
                '\t' => @"\t",
                _ => character.ToString(),
            });
        }

        return quoted.Append('"').ToString();
    }
}
