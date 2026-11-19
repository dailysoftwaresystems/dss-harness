using System.Globalization;
using System.Text;
using DssHarness.Core.Output;

namespace DssHarness.Core.Codebase;

/// <summary>
/// The path a configuration key is named by, and how code spells one: each name of it joined by a dot, an item's place
/// and a name that is empty, white space or holds a control character held in brackets to the place before it -
/// <c>targets.x64.callingConvention</c>, <c>limits[1].value</c>, <c>packages[""].name</c>,
/// <c>messages["Sure?\nIt cannot be undone."]</c>, always one line - and read back from the spellings .NET's configuration
/// names a path by, a colon or, as an environment variable writes one, a double underscore between its places
/// (<c>Payments:TimeoutSeconds</c>, <c>Payments__TimeoutSeconds</c>, <c>Limits:1:Value</c>). The one owner of both: every
/// reader of data files writes a key's path by it, and the keys' join reads a string literal and a node's path by it.
/// </summary>
public static class CodeKeyPaths
{
    /// <summary>The separators a path is spelt with besides the dot, in the order a string is read by them.</summary>
    private static readonly string[] Spelt = [":", "__"];

    /// <summary>
    /// Whether a path holds a place in brackets, to the place before it: an item, a name that is empty or white space,
    /// which no dotted path can hold, and a name that holds a control character - a line break a key written as its source
    /// string holds - which no one line can.
    /// </summary>
    /// <param name="name">The place's name; <see langword="null"/> for an item.</param>
    public static bool Bracketed(string? name) => name is null || string.IsNullOrWhiteSpace(name) || name.Any(char.IsControl);

    /// <summary>The place of the item <paramref name="item"/>, from 0, as a path writes it: <c>[1]</c>.</summary>
    /// <param name="item">Its place in its list.</param>
    public static string Item(int item) => $"[{item.ToString(CultureInfo.InvariantCulture)}]";

    /// <summary>
    /// The place of the member <paramref name="name"/> as a path writes it, always on one line: the name as it is, one that
    /// is bracketed (<see cref="Bracketed"/>) quoted in brackets as <see cref="ReportText.Quoted"/> writes it - each control
    /// character escaped.
    /// </summary>
    /// <param name="name">The member's name.</param>
    public static string Name(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return Bracketed(name) ? $"[{ReportText.Quoted(name)}]" : name;
    }

    /// <summary>The path <paramref name="places"/> are, each as a path writes it and whether it is held in brackets: a dot before each but the first and those in brackets.</summary>
    /// <param name="places">The places, from the file's top.</param>
    public static string Of(IEnumerable<(string Written, bool Bracketed)> places)
    {
        ArgumentNullException.ThrowIfNull(places);

        var path = new StringBuilder();

        foreach (var (written, bracketed) in places)
        {
            _ = bracketed || path.Length == 0 ? path.Append(written) : path.Append('.').Append(written);
        }

        return path.ToString();
    }

    /// <summary>
    /// The path <paramref name="written"/> names as .NET's configuration spells one - its places parted by colons, or by
    /// double underscores, a place of digits alone an item - written as a path is; <see langword="null"/> where it is
    /// spelt so by neither, or holds an empty place.
    /// </summary>
    /// <param name="written">A string as code writes it: a literal's value.</param>
    public static string? Dotted(string written)
    {
        ArgumentNullException.ThrowIfNull(written);

        if (Spelt.FirstOrDefault(separator => written.Contains(separator, StringComparison.Ordinal)) is not { } separator)
        {
            return null;
        }

        var places = written.Split(separator);

        if (places.Any(place => place.Length == 0))
        {
            return null;
        }

        return Of(places.Select(place => int.TryParse(place, NumberStyles.None, CultureInfo.InvariantCulture, out var item) && Item(item) == $"[{place}]"
            ? (Item(item), true)
            : (Name(place), Bracketed(place))));
    }

    /// <summary>
    /// Each place <paramref name="path"/> is written of, in order, by where its text begins and ends: a name up to the dot
    /// or the bracket after it, the dot left out; an item or a quoted name with its brackets - a quoted name's escapes read
    /// as <see cref="ReportText.Quoted"/> writes them, so a dot or a bracket inside the quotes parts nothing. A path is read
    /// once, so a place is told in time to its own length.
    /// </summary>
    /// <param name="path">A key's path, as <see cref="Of"/> writes one.</param>
    public static IEnumerable<(int Start, int End)> Places(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        for (var at = 0; at < path.Length;)
        {
            if (path[at] == '.')
            {
                at++;
                continue;
            }

            var start = at;

            if (path[at] == '[')
            {
                at++;

                if (at < path.Length && path[at] == '"')
                {
                    for (at++; at < path.Length && path[at] != '"'; at++)
                    {
                        at += path[at] == '\\' ? 1 : 0;
                    }

                    at++;
                }

                while (at < path.Length && path[at] != ']')
                {
                    at++;
                }

                at = Math.Min(at + 1, path.Length);
            }
            else
            {
                while (at < path.Length && path[at] is not ('.' or '['))
                {
                    at++;
                }
            }

            yield return (start, at);
        }
    }

    /// <summary>
    /// The key path <paramref name="node"/> of the data file <paramref name="file"/> stands at: a key's, its qualified name;
    /// a section's, its qualified name past the file's own and its <c>#</c>; <see langword="null"/> for a node of no path.
    /// </summary>
    /// <param name="node">A node of the file's extract.</param>
    /// <param name="file">The file's path, as the section's qualified name begins with it.</param>
    public static string? OfNode(ExtractNode node, string file)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(file);

        return node.SubKind == CodeSubKinds.Key ? node.Qualified
            : node.Kind == CodeNodeKinds.Section && node.Qualified.Length > file.Length && node.Qualified.StartsWith(file, StringComparison.Ordinal) && node.Qualified[file.Length] == '#' ? node.Qualified[(file.Length + 1)..]
            : null;
    }
}
