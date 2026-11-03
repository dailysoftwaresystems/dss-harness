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
