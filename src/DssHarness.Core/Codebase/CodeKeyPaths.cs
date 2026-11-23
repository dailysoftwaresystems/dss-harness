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
    /// The path <paramref name="path"/> names as an environment variable writes one for .NET's configuration - its places
    /// joined by double underscores, an item by its digits (<c>Payments__TimeoutSeconds</c>, <c>Limits__1__Value</c>): the
    /// one spelling of a path <see cref="Dotted"/> reads back that a whole word of a line holds, for an underscore is a
    /// character of a word and a colon is none; <see langword="null"/> where no such spelling is read back as the path.
    /// </summary>
    /// <param name="path">A key's path, as <see cref="Of"/> writes one.</param>
    public static string? Environment(string path) => Joined(path, "__");

    /// <summary>
    /// Each name a key may be written with that names <paramref name="path"/> as .NET's configuration reads a key's name -
    /// its places joined by colons (<c>Payments:TimeoutSeconds</c>, a flat key of a settings file) and by double
    /// underscores (<c>Payments__TimeoutSeconds</c>, an environment variable's), an item by its digits - each one
    /// <see cref="Dotted"/> reads back as the path; none where the path has one place, or a place no such name can write.
    /// </summary>
    /// <param name="path">A key's path, as <see cref="Of"/> writes one.</param>
    public static IEnumerable<string> Spellings(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        foreach (var separator in Spelt)
        {
            if (Joined(path, separator) is { } spelt)
            {
                yield return spelt;
            }
        }
    }

    /// <summary>
    /// The places of <paramref name="path"/> joined by <paramref name="separator"/>, an item by its digits; null where the
    /// path has one place, holds a quoted name, or is not read back as itself (<see cref="Dotted"/>) - a place that holds the
    /// separator of its own (<c>Payments__TimeoutSeconds</c> under <c>environment</c>, which no environment variable's name
    /// writes) is no place of that spelling.
    /// </summary>
    private static string? Joined(string path, string separator)
    {
        ArgumentNullException.ThrowIfNull(path);

        var places = new List<string>();

        foreach (var (start, end) in Places(path))
        {
            if (!InBrackets(path, start))
            {
                places.Add(path[start..end]);
            }
            else if (path[start + 1] != '"')
            {
                places.Add(path[(start + 1)..(end - 1)]);
            }
            else
            {
                return null;
            }
        }

        if (places.Count < 2)
        {
            return null;
        }

        var spelt = string.Join(separator, places);
        return string.Equals(Dotted(spelt), path, StringComparison.Ordinal) ? spelt : null;
    }

    /// <summary>
    /// Each place <paramref name="path"/> is written of, in order, by where its text begins and ends: a place in brackets
    /// only as <see cref="Of"/> writes one - an item's digits (<see cref="Item"/>), or a quoted name (<see cref="Name"/>)
    /// whose escapes are read as <see cref="ReportText.Quoted"/> writes them, so a dot or a bracket inside the quotes parts
    /// nothing - with its brackets; else a name, up to the dot or the place in brackets after it, the dot left out - a
    /// bracket that opens no such place a character of the name it stands in, as a name code declares holds one
    /// (<c>Canvas.this[]</c>, <c>Vec.operator[]</c>). A path is read once: each bracket is asked once whether it opens a
    /// place, and the text that asks reads is read by no other ask, so a place is told in time to its own length.
    /// </summary>
    /// <param name="path">A key's path, as <see cref="Of"/> writes one, or a name as code writes one.</param>
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

            if (BracketEnd(path, at) is { } end)
            {
                at = end;
            }
            else
            {
                for (at++; at < path.Length && path[at] != '.' && !(path[at] == '[' && BracketEnd(path, at) is not null); at++)
                {
                }
            }

            yield return (start, at);
        }
    }

    /// <summary>
    /// The last place <paramref name="path"/> is written of (<see cref="Places"/>), as it is written - a name, or an item or a
    /// quoted name with its brackets, as a data file's key of that place is named - or the whole path where it holds none.
    /// </summary>
    /// <param name="path">A key's path, as <see cref="Of"/> writes one, or a name as code writes one.</param>
    public static string Last(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        string? last = null;

        foreach (var (start, end) in Places(path))
        {
            last = path[start..end];
        }

        return last ?? path;
    }

    /// <summary>
    /// The last place of <paramref name="path"/> that is a name, no item or quoted name in brackets, which no text writes as
    /// it is - <c>value</c> of <c>limits[1].value</c>, <c>limits</c> of <c>limits[1]</c>, <c>this[]</c> of
    /// <c>Canvas.this[]</c> - as data and code write the place a path ends at; the whole path where it holds no name.
    /// </summary>
    /// <param name="path">A key's path, as <see cref="Of"/> writes one, or a name as code writes one.</param>
    public static string LastName(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        string? last = null;

        foreach (var (start, end) in Places(path))
        {
            last = InBrackets(path, start) ? last : path[start..end];
        }

        return last ?? path;
    }

    /// <summary>Whether the place of <paramref name="path"/> that begins at <paramref name="start"/> is one in brackets, an item or a quoted name, and no name.</summary>
    private static bool InBrackets(string path, int start) => BracketEnd(path, start) is not null;

    /// <summary>
    /// Where the place in brackets <paramref name="path"/> holds at <paramref name="at"/> ends, past its closing bracket - an
    /// item's digits, or a quoted name read to its first unescaped quote mark, which the closing bracket follows - or null
    /// where no such place begins there.
    /// </summary>
    private static int? BracketEnd(string path, int at)
    {
        if (at + 1 >= path.Length || path[at] != '[')
        {
            return null;
        }

        var end = at + 1;

        if (char.IsAsciiDigit(path[end]))
        {
            while (end < path.Length && char.IsAsciiDigit(path[end]))
            {
                end++;
            }
        }
        else if (path[end] == '"')
        {
            for (end++; end < path.Length && path[end] != '"'; end++)
            {
                end += path[end] == '\\' ? 1 : 0;
            }

            end++;
        }
        else
        {
            return null;
        }

        return end < path.Length && path[end] == ']' ? end + 1 : null;
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

        if (node.SubKind == CodeSubKinds.Key)
        {
            return node.Qualified.Whole();
        }

        if (node.Kind == CodeNodeKinds.Section)
        {
            var qualified = node.Qualified.Whole();
            return qualified.Length > file.Length && qualified.StartsWith(file, StringComparison.Ordinal) && qualified[file.Length] == '#' ? qualified[(file.Length + 1)..] : null;
        }

        return null;
    }
}
