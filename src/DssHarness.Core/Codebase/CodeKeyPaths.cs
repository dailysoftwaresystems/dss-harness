using System.Buffers;
using System.Globalization;
using System.Text;
using DssHarness.Core.Output;

namespace DssHarness.Core.Codebase;

/// <summary>
/// The path a configuration key is named by, and how code spells one: each name of it joined by a dot, an item's place in
/// brackets (<c>limits[1].value</c>), and a name held in brackets, quoted, to the place before it where it is empty, is
/// white space, holds a control character or holds one of the characters a path parts its places by, a dot or a bracket -
/// <c>Logging.LogLevel["Microsoft.AspNetCore"]</c>, <c>packages[""].name</c>,
/// <c>messages["Sure?\nIt cannot be undone."]</c> - always one line. A path is written in one form and read back exactly:
/// a plain name holds no dot and no bracket, and a quoted one is quoted as <see cref="ReportText.Quoted"/> writes it. It
/// is read back from the spellings .NET's configuration names a path by, a colon or, as an environment variable writes
/// one, a double underscore between its places (<c>Payments:TimeoutSeconds</c>, <c>Payments__TimeoutSeconds</c>,
/// <c>Limits:1:Value</c>), and a place quoted in single quotes is read as the same place and never written
/// (<c>Logging.LogLevel['Microsoft.AspNetCore']</c>). A key node is named by its own name where that stands alone on a
/// line (<see cref="NodeName"/>), <c>Microsoft.AspNetCore</c> of <c>Logging.LogLevel["Microsoft.AspNetCore"]</c>, whatever
/// its path holds. The one owner of both: every reader of data files writes a key's path by it, and the keys' join reads a
/// string literal and a node's path by it.
/// </summary>
public static class CodeKeyPaths
{
    /// <summary>The separators a path is spelt with besides the dot, in the order a string is read by them.</summary>
    private static readonly string[] Spelt = [":", "__"];

    /// <summary>The characters a path parts its places by outside a quoted name: a name holding one is written quoted in brackets (<see cref="Bracketed"/>), so it is read back as one place.</summary>
    private static readonly SearchValues<char> Parting = SearchValues.Create(".[]");

    /// <summary>
    /// Whether <paramref name="name"/> is written as it is, on a line of its own: not empty and not white space alone,
    /// which no dotted path can hold, and holding no control character - a line break a key written as its source string
    /// holds - which no one line can.
    /// </summary>
    /// <param name="name">The name.</param>
    public static bool StandsAlone(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        return !string.IsNullOrWhiteSpace(name) && !name.Any(char.IsControl);
    }

    /// <summary>
    /// Whether a path holds a place in brackets, to the place before it: an item, and a name that does not stand alone
    /// (<see cref="StandsAlone"/>), or that holds a dot or a bracket - one of the characters a path parts its places by
    /// (<c>Microsoft.AspNetCore</c>, <c>[typescript]</c>), which written as it is would read back as several places.
    /// </summary>
    /// <param name="name">The place's name; <see langword="null"/> for an item.</param>
    public static bool Bracketed(string? name) => name is null || !StandsAlone(name) || name.AsSpan().ContainsAny(Parting);

    /// <summary>The place of the item <paramref name="item"/>, from 0, as a path writes it: <c>[1]</c>.</summary>
    /// <param name="item">Its place in its list.</param>
    public static string Item(int item) => $"[{item.ToString(CultureInfo.InvariantCulture)}]";

    /// <summary>
    /// The place of the member <paramref name="name"/> as a path writes it, always on one line: the name as it is where
    /// it is not bracketed (<see cref="Bracketed"/>), else quoted in brackets as <see cref="ReportText.Quoted"/> writes it
    /// - a dot or a bracket inside the quotes parting nothing, each control character escaped.
    /// </summary>
    /// <param name="name">The member's name.</param>
    public static string Name(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        return Bracketed(name) ? $"[{ReportText.Quoted(name)}]" : name;
    }

    /// <summary>
    /// The name a key node of the member <paramref name="name"/> is named by: the name itself where it stands alone
    /// (<see cref="StandsAlone"/>), <c>Microsoft.AspNetCore</c> of the path <c>Logging.LogLevel["Microsoft.AspNetCore"]</c>
    /// - so a key is reached by its name as its file writes the name, by the keys' join, by a <c>$(Name)</c> and by name;
    /// else its place as a path writes it (<see cref="Name"/>), <c>[""]</c> and <c>["a\nb"]</c>, which no plain name writes.
    /// </summary>
    /// <param name="name">The member's name.</param>
    public static string NodeName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        return StandsAlone(name) ? name : Name(name);
    }

    /// <summary>
    /// The path that <paramref name="written"/> - one place as a path writes it - makes with <paramref name="path"/>: the
    /// place joined to the path, a dot before it where it is a plain name and nothing where it is held in brackets
    /// (<paramref name="bracketed"/>, <see cref="Bracketed"/>), to the place before it; the place alone where no path
    /// stands before it. The one statement of the join: <see cref="Of"/> folds it, and every reader of data files calls it.
    /// </summary>
    /// <param name="path">The path of what holds the place; <see langword="null"/> where the place is the first.</param>
    /// <param name="written">The place as a path writes it (<see cref="Name"/>, <see cref="Item"/>).</param>
    /// <param name="bracketed">Whether the place is held in brackets, which no dot precedes.</param>
    public static QualifiedName Under(QualifiedName? path, string written, bool bracketed)
    {
        ArgumentNullException.ThrowIfNull(written);

        return path is null ? QualifiedName.Of(written) : path.Under(written, bracketed ? "" : ".");
    }

    /// <summary>The path <paramref name="places"/> are, each as a path writes it and whether it is held in brackets: a dot before each but the first and those in brackets (<see cref="Under"/>).</summary>
    /// <param name="places">The places, from the file's top.</param>
    public static string Of(IEnumerable<(string Written, bool Bracketed)> places)
    {
        ArgumentNullException.ThrowIfNull(places);

        QualifiedName? path = null;

        foreach (var (written, bracketed) in places)
        {
            path = Under(path, written, bracketed);
        }

        return path?.Whole() ?? string.Empty;
    }

    /// <summary>
    /// The path <paramref name="written"/> names as .NET's configuration spells one - its places parted by colons, or by
    /// double underscores, a place of digits alone an item - written as a path is, a name holding a dot one place quoted in
    /// brackets (<c>Logging:LogLevel:Microsoft.AspNetCore</c> is <c>Logging.LogLevel["Microsoft.AspNetCore"]</c>);
    /// <see langword="null"/> where it is spelt so by neither, holds an empty place, or is a path already - a place quoted
    /// in brackets (<see cref="Name"/>), in either quote mark, which no configuration spells a key by.
    /// </summary>
    /// <param name="written">A string as code writes it: a literal's value.</param>
    public static string? Dotted(string written)
    {
        ArgumentNullException.ThrowIfNull(written);

        if (Parts(written).Any(place => QuotedAt(written, place)))
        {
            return null;
        }

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
    /// joined by double underscores, an item by its digits, a quoted name by the name it reads (<c>Payments__TimeoutSeconds</c>,
    /// <c>Limits__1__Value</c>, <c>Logging__LogLevel__Microsoft.AspNetCore</c>): the one spelling of a path
    /// <see cref="Dotted"/> reads back that a whole word of a line holds, for an underscore is a character of a word and a
    /// colon is none; <see langword="null"/> where no such spelling is read back as the path.
    /// </summary>
    /// <param name="path">A key's path, as <see cref="Of"/> writes one.</param>
    public static string? Environment(string path) => Joined(path, "__");

    /// <summary>
    /// Each name a key may be written with that names <paramref name="path"/> as .NET's configuration reads a key's name -
    /// its places joined by colons (<c>Payments:TimeoutSeconds</c>, a flat key of a settings file) and by double
    /// underscores (<c>Payments__TimeoutSeconds</c>, an environment variable's), an item by its digits and a quoted name by
    /// the name it reads (<c>Logging:LogLevel:Microsoft.AspNetCore</c>, <c>Logging__LogLevel__Microsoft.AspNetCore</c> of
    /// <c>Logging.LogLevel["Microsoft.AspNetCore"]</c>) - each one <see cref="Dotted"/> reads back as the path; none where
    /// the path has one place, or a place no such name can write: one that does not stand alone, or holds the separator of
    /// its own.
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
    /// The places of <paramref name="path"/> joined by <paramref name="separator"/>: an item by its digits, a name by its
    /// text, a quoted name by the name it reads (<see cref="ReportText.Unquoted"/>); null where the path has one place,
    /// holds a quoted name that reads none or does not stand alone - a name empty or of white space, one holding a control
    /// character - or is not read back as the path it is written (<see cref="Dotted"/> of it against
    /// <see cref="Canonical"/>): a place that holds the separator of its own (<c>Payments__TimeoutSeconds</c> under
    /// <c>environment</c>, which no environment variable's name writes) is no place of that spelling.
    /// </summary>
    private static string? Joined(string path, string separator)
    {
        ArgumentNullException.ThrowIfNull(path);

        var places = new List<string>();

        foreach (var place in Parts(path))
        {
            var (name, item) = NameAt(path, place);

            if (!place.InBrackets)
            {
                places.Add(name!);
            }
            else if (item)
            {
                places.Add(path[(place.Start + 1)..(place.End - 1)]);
            }
            else if (name is not null && StandsAlone(name))
            {
                places.Add(name);
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
        return string.Equals(Dotted(spelt), Canonical(path), StringComparison.Ordinal) ? spelt : null;
    }

    /// <summary>
    /// <paramref name="path"/> as the writer writes it, whatever form a reader takes it in: the path itself where it holds
    /// no place quoted in brackets, and else its places written again (<see cref="Of"/>) - a name quoted needlessly
    /// (<c>["Logging"]</c>) as the plain name it is, one quoted in single quotes (<c>Logging.LogLevel['Microsoft.AspNetCore']</c>,
    /// <c>Flags['it\'s.on']</c>) in the double quotes the writer writes, and an item or a quoted place that names nothing
    /// as it is written (<see cref="Written"/>). What every lookup by a path's text reads first, so one written form is
    /// compared wherever a path is compared.
    /// </summary>
    /// <param name="path">A key's path as a reader takes one, in either quote mark.</param>
    public static string Canonical(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        var places = Parts(path).ToList();

        return places.Any(place => QuotedAt(path, place)) ? Of(places.Select(place => Written(path, place))) : path;
    }

    /// <summary>
    /// The loose form of <paramref name="path"/>: its quoted names written as they are, after a dot, as an agent composes
    /// the path from the names the file shows - <c>Logging.LogLevel.Microsoft.AspNetCore</c> of
    /// <c>Logging.LogLevel["Microsoft.AspNetCore"]</c>, <c>.defaults.image</c> of <c>[".defaults"].image</c> - a name that
    /// does not stand alone, and an item, kept as written. Read by the queries alone, where every key a text may mean is
    /// asked for; never by the linker, for code splits a dotted literal into its places, and never read back, for it is
    /// no path: <see cref="Dotted"/> of it names the nested keys of the same dots.
    /// </summary>
    /// <param name="path">A key's path, as <see cref="Of"/> writes one.</param>
    public static string Unescaped(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        var places = Parts(path).ToList();

        return places.Any(place => QuotedAt(path, place)) ? Of(places.Select(place => Loose(path, place))) : path;
    }

    /// <summary>
    /// Whether <paramref name="path"/> ends with the places <paramref name="tail"/> names, where one of its places begins:
    /// <c>Logging.LogLevel["Microsoft.AspNetCore"]</c> ends with <c>LogLevel["Microsoft.AspNetCore"]</c>, with
    /// <c>["Microsoft.AspNetCore"]</c> and with the same place in single quotes, <c>Shapes.Canvas.this[]</c> with
    /// <c>Canvas.this[]</c>, <c>limits[1].value</c> with <c>[1].value</c>; never with a part of a place - <c>AspNetCore"]</c>
    /// of the quoted name, <c>c</c> of <c>a.bc</c>. The tail is read as the writer writes it (<see cref="Canonical"/>);
    /// <paramref name="path"/> is a qualified name as stored, which is written so.
    /// </summary>
    /// <param name="path">A qualified name, as a path writes one.</param>
    /// <param name="tail">The last places of it, as written or as a reader takes them.</param>
    public static bool EndsWithPlaces(string path, string tail)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(tail);

        tail = Canonical(tail);

        return tail.Length > 0 && tail.Length < path.Length
            && path.EndsWith(tail, StringComparison.Ordinal)
            && Parts(path).Any(place => place.Start == path.Length - tail.Length);
    }

    /// <summary>
    /// <paramref name="text"/> with <paramref name="rewrite"/> applied to each run of it outside a place quoted in
    /// brackets, the quoted places kept as written: a language's separators, read as dots (<c>::</c>, <c>\</c>), part
    /// nothing inside a quoted name, <c>a::b["c::d"].e</c> giving <c>a.b["c::d"].e</c>.
    /// </summary>
    /// <param name="text">A symbol as code writes one.</param>
    /// <param name="rewrite">What each run outside a quoted place is replaced by.</param>
    public static string OutsideQuotedNames(string text, Func<string, string> rewrite)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(rewrite);

        var made = new StringBuilder();
        var from = 0;

        foreach (var place in Parts(text))
        {
            if (!QuotedAt(text, place))
            {
                continue;
            }

            _ = made.Append(rewrite(text[from..place.Start])).Append(text[place.Start..place.End]);
            from = place.End;
        }

        return from == 0 ? rewrite(text) : made.Append(rewrite(text[from..])).ToString();
    }

    /// <summary>
    /// Each place <paramref name="path"/> is written of, in order, by where its text begins and ends: a place in brackets
    /// only as <see cref="Of"/> writes one - an item's digits (<see cref="Item"/>), or a quoted name (<see cref="Name"/>)
    /// in either quote mark, whose escapes are read as <see cref="ReportText.Quoted"/> writes them, so a dot or a bracket
    /// inside the quotes parts nothing - with its brackets; else a name, up to the dot or the place in brackets after it,
    /// the dot left out - a bracket that opens no such place a character of the name it stands in, as a name code declares
    /// holds one (<c>Canvas.this[]</c>, <c>Vec.operator[]</c>). A path is read nearly once: a bracket is asked whether it
    /// opens a place at most twice - once where a name that is no place comes upon it, again where the place it opens
    /// begins - and what one ask reads, no other reads, so a place is told in time to its own length.
    /// </summary>
    /// <param name="path">A key's path, as <see cref="Of"/> writes one, or a name as code writes one.</param>
    public static IEnumerable<(int Start, int End)> Places(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        foreach (var place in Parts(path))
        {
            yield return (place.Start, place.End);
        }
    }

    /// <summary>
    /// The last place <paramref name="path"/> is written of (<see cref="Places"/>), as a key node of that place is named:
    /// a name as it is written, a quoted name by the name it reads (<see cref="NodeName"/>) - <c>Microsoft.AspNetCore</c> of
    /// <c>["Microsoft.AspNetCore"]</c> and of <c>['Microsoft.AspNetCore']</c> - an item or a quoted place that names nothing
    /// with its brackets, as a data file's key of that place is named; or the whole path where it holds none.
    /// </summary>
    /// <param name="path">A key's path, as <see cref="Of"/> writes one, or a name as code writes one.</param>
    public static string Last(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        string? last = null;

        foreach (var place in Parts(path))
        {
            var (name, _) = NameAt(path, place);
            last = !place.InBrackets || name is null ? path[place.Start..place.End] : NodeName(name);
        }

        return last ?? path;
    }

    /// <summary>
    /// The last place of <paramref name="path"/> that is a name some text writes as it is - a plain one, or a quoted one
    /// that reads (<see cref="ReportText.Unquoted"/>) and stands alone: <c>Microsoft.AspNetCore</c> of
    /// <c>Logging.LogLevel["Microsoft.AspNetCore"]</c> and of <c>Logging.LogLevel['Microsoft.AspNetCore']</c>,
    /// <c>[0]</c> of <c>resources["[0]"]</c> - and no item and no quoted name that does not stand alone, which no text
    /// writes as it is: <c>value</c> of <c>limits[1].value</c>, <c>limits</c> of <c>limits[1]</c> and of
    /// <c>limits[""]</c>, <c>this[]</c> of <c>Canvas.this[]</c>; the whole path where it holds no such name.
    /// </summary>
    /// <param name="path">A key's path, as <see cref="Of"/> writes one, or a name as code writes one.</param>
    public static string LastName(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        string? last = null;

        foreach (var place in Parts(path))
        {
            var (name, _) = NameAt(path, place);

            if (name is not null && (!place.InBrackets || StandsAlone(name)))
            {
                last = name;
            }
        }

        return last ?? path;
    }

    /// <summary>One place of a path (<see cref="Parts"/>): where its text is written, and whether it is held in brackets - an item, or a name quoted in either quote mark.</summary>
    private readonly record struct Part(int Start, int End, bool InBrackets);

    /// <summary>
    /// Each place <paramref name="path"/> is written of (<see cref="Places"/>), each told once whether it is held in
    /// brackets (<see cref="BracketEnd"/> asked once a place): what every reader of a place's name reads.
    /// </summary>
    private static IEnumerable<Part> Parts(string path)
    {
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
                yield return new Part(start, end, InBrackets: true);
                at = end;
            }
            else
            {
                for (at++; at < path.Length && path[at] != '.' && !(path[at] == '[' && BracketEnd(path, at) is not null); at++)
                {
                }

                yield return new Part(start, at, InBrackets: false);
            }
        }
    }

    /// <summary>
    /// Whether the place <paramref name="place"/> of <paramref name="path"/> is a name quoted in brackets, in either quote
    /// mark - the one test of it every reader below makes; an item's digits are no quoted name.
    /// </summary>
    private static bool QuotedAt(string path, Part place) => place.InBrackets && path[place.Start + 1] is '"' or '\'';

    /// <summary>
    /// The name the place <paramref name="place"/> of <paramref name="path"/> stands for: a plain name's text; a quoted
    /// name's, read back (<see cref="ReportText.Unquoted"/>) where it is a form the escape rule writes with its mark, and
    /// null where it is none - such a place names nothing; and <see langword="null"/> for an item, which says so.
    /// </summary>
    private static (string? Name, bool Item) NameAt(string path, Part place)
    {
        if (!place.InBrackets)
        {
            return (path[place.Start..place.End], false);
        }

        return QuotedAt(path, place) ? (ReportText.Unquoted(path[(place.Start + 1)..(place.End - 1)]), false) : (null, true);
    }

    /// <summary>
    /// The place <paramref name="place"/> of <paramref name="path"/> as the writer writes it (<see cref="Of"/>): a plain
    /// name and an item as they are; a quoted name that reads (<see cref="Name"/>) - one quoted needlessly, in single quotes
    /// or with escapes of the other mark's written as the writer writes it; a quoted place that names nothing as it is
    /// written.
    /// </summary>
    private static (string Written, bool Bracketed) Written(string path, Part place)
    {
        var (name, item) = NameAt(path, place);

        if (!place.InBrackets || item || name is null)
        {
            return (path[place.Start..place.End], place.InBrackets);
        }

        return (Name(name), Bracketed(name));
    }

    /// <summary>
    /// The place <paramref name="place"/> of <paramref name="path"/> for the loose form (<see cref="Unescaped"/>): as
    /// <see cref="Written"/>, but a quoted name that reads and stands alone is written as the name it is, a plain place
    /// that a dot precedes.
    /// </summary>
    private static (string Written, bool Bracketed) Loose(string path, Part place)
    {
        var (name, item) = NameAt(path, place);

        return !item && name is not null && StandsAlone(name) ? (name, false) : Written(path, place);
    }

    /// <summary>
    /// Where the place in brackets <paramref name="path"/> holds at <paramref name="at"/> ends, past its closing bracket - an
    /// item's digits, or a quoted name, in either quote mark, read to its first unescaped quote mark of the one it opened
    /// with, which the closing bracket follows - or null where no such place begins there. The one place a bracket is read.
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
        else if (path[end] is '"' or '\'')
        {
            var mark = path[end];

            for (end++; end < path.Length && path[end] != mark; end++)
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
