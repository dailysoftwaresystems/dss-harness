using System.Globalization;
using System.Text;

namespace DssHarness.Core.Codebase.Languages;

/// <summary>
/// A Python project's <c>pyproject.toml</c> and <c>setup.cfg</c>: one <c>project</c> node over the whole file, named by the
/// project's name - <c>[project]</c>'s or <c>[tool.poetry]</c>'s in a pyproject.toml, <c>[metadata]</c>'s in a setup.cfg - its
/// header the line that names it; and the roots of the layout the file names in its own words (<see cref="FileExtract.LayoutRoots"/>),
/// each a directory beside the file under which a bare import of Python is looked for: setuptools' <c>package-dir</c> root and
/// <c>packages.find</c>'s <c>where</c>, Poetry's <c>packages</c>' <c>from</c>, the directory of each of Hatch's wheel
/// <c>packages</c>, PDM's <c>package-dir</c>, and pytest's <c>pythonpath</c> - each read as its tool reads it, a string of pytest's
/// paths split into its words and a setup.cfg's <c>where</c> its first entry. A value written as anything but a string - or, in a
/// setup.cfg, one an interpolation computes - names none.
/// </summary>
/// <remarks>
/// TOML, and the INI form a setup.cfg is written in, are read a line at a time: a value over several lines - an array, an inline
/// table, a multi-line string, a continued option - is read whole, arrays and tables nested however deep held on a stack of the
/// reading's own; a string's value is TOML's, a multi-line one's among them, and a setup.cfg's options are configparser's. A value
/// whose bracket or string never closes is read to the file's end - a one-line string to its line's - and the line it begins on is
/// marked dynamic. The file is outlined: no key of it is a node and no value a literal, so the sites operation searches it by its
/// text.
/// </remarks>
public sealed class PyprojectExtractor : ICodeGraphExtractor
{
    /// <summary>The keys of a pyproject.toml whose strings are roots of the layout, each by its path of keys.</summary>
    private static readonly string[][] TomlRoots =
    [
        ["tool", "setuptools", "package-dir", ""],
        ["tool", "setuptools", "packages", "find", "where"],
        ["tool", "poetry", "packages", "from"],
        ["tool", "pdm", "build", "package-dir"],
    ];

    /// <summary>The key of pytest's <c>pythonpath</c> in a pyproject.toml: an array's each string a root, and one string each word it writes (<see cref="TomlRoot"/>).</summary>
    private static readonly string[] PytestPaths = ["tool", "pytest", "ini_options", "pythonpath"];

    /// <summary>The keys of a pyproject.toml whose strings are packages, each in the directory that is a root: Hatch's wheel's.</summary>
    private static readonly string[][] TomlPackages = [["tool", "hatch", "build", "targets", "wheel", "packages"]];

    /// <summary>The keys of a pyproject.toml that name the project.</summary>
    private static readonly string[][] TomlNames = [["project", "name"], ["tool", "poetry", "name"]];

    /// <inheritdoc/>
    public string Language => "pyproject";

    /// <inheritdoc/>
    public int Version => 1;

    /// <inheritdoc/>
    public CodeGraphClaims Claims { get; } = new([new("pyproject.toml"), new("setup.cfg")], []);

    /// <inheritdoc/>
    public bool Outlines => true;

    /// <inheritdoc/>
    public CodeGraphExtractorHelp Help { get; } = new(
        "Python projects (pyproject.toml and setup.cfg): a project node for each, named by its name, and the roots of the layout it names - setuptools' package-dir root and packages.find's where, a setup.cfg's first entry of it, Poetry's packages' from, the directory of each of Hatch's wheel packages, PDM's package-dir and pytest's pythonpath, a string of it split into its words as pytest splits it - each a directory beside the file under which Python's bare imports are looked for",
        [
            ("other-keys", "dependencies, optional dependencies, the build backend, scripts and entry points, and every tool's configuration but the roots: not read - no key of the file is a node and no value a literal, so the sites operation searches the file by its text"),
            ("computed-roots", "a root a value computes - a setup.cfg's interpolation, a setup.py's code - and a directory package-dir maps one package to: no root"),
            ("unclosed-values", "a value whose bracket or string never closes: the rest of the file is read as that value - a one-line string's, the rest of its line - and the line it begins on is marked dynamic"),
        ]);

    /// <inheritdoc/>
    public FileExtract Extract(CodeFileInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var lines = input.Lines;
        var cfg = CodeGraphIds.FileName(input.Path) == "setup.cfg";
        var dynamic = new List<ExtractDynamic>();
        var entries = cfg ? Ini(lines) : Toml(lines, dynamic);
        var roots = new List<string>();
        (string Value, int Line)? name = null;

        foreach (var entry in entries)
        {
            if (cfg ? IniName(entry.Path) : TomlNames.Any(names => Is(entry.Path, names)))
            {
                name ??= (entry.Value, entry.Line);
                continue;
            }

            foreach (var root in cfg ? IniRoots(entry.Path, entry.Value) : TomlRoot(entry))
            {
                roots.Add(root);
            }
        }

        var id = $"{Language}:{input.Path}";
        var fileName = CodeGraphIds.FileName(input.Path);
        var header = name is { } named && named.Line <= lines.Count ? ExtractText.Signature(ExtractText.Flat(lines[named.Line - 1])) : fileName;

        return new FileExtract([new ExtractNode(id, CodeNodeKinds.Project, name is { Value.Length: > 0 } project ? project.Value : fileName, input.Path, 1, Math.Max(lines.Count, 1), header.Length > 0 ? header : fileName)])
        {
            LayoutRoots = roots.Count > 0 ? [.. roots.Distinct(StringComparer.Ordinal)] : null,
            Dynamic = dynamic,
        };
    }

    /// <summary>One string a file sets: the keys it stands under, its value, the line it is written on, and whether it is an element of an array.</summary>
    private sealed record Entry(IReadOnlyList<string> Path, string Value, int Line, bool Element = false);

    /// <summary>Whether <paramref name="path"/> is <paramref name="keys"/>.</summary>
    private static bool Is(IReadOnlyList<string> path, string[] keys) => path.SequenceEqual(keys, StringComparer.Ordinal);

    /// <summary>
    /// The roots a pyproject.toml's string <paramref name="entry"/> names: itself, for a root's key - pytest's <c>pythonpath</c> written as
    /// one string each word it writes, as pytest splits it (<see cref="ShellWords"/>), and an element of its array as written - and a
    /// package's directory, for a package's.
    /// </summary>
    private static IEnumerable<string> TomlRoot(Entry entry)
    {
        if (Is(entry.Path, PytestPaths))
        {
            foreach (var word in (entry.Element ? [entry.Value] : ShellWords(entry.Value)) ?? [])
            {
                if (Root(word) is { } root)
                {
                    yield return root;
                }
            }
        }
        else if (TomlRoots.Any(keys => Is(entry.Path, keys)) && Root(entry.Value) is { } root)
        {
            yield return root;
        }
        else if (TomlPackages.Any(keys => Is(entry.Path, keys)) && Root(entry.Value) is { } package && package.LastIndexOf('/') is var slash and > 0)
        {
            yield return package[..slash];
        }
    }

    /// <summary>
    /// The words <paramref name="value"/> writes, as pytest reads a string of paths - Python's <c>shlex.split</c>: parted by blanks and
    /// line breaks; a run quoted in single or double quotes part of one word, without its quotes, a blank in it its own; a backslash
    /// outside quotes taking the character after it as written, and in double quotes a quote or a backslash after it; no <c>#</c> a
    /// comment. <see langword="null"/> where a quote never closes or a backslash ends the value, which <c>shlex</c> refuses.
    /// </summary>
    private static List<string>? ShellWords(string value)
    {
        var words = new List<string>();
        var word = new StringBuilder();
        var begun = false;
        var quote = '\0';

        for (var at = 0; at < value.Length; at++)
        {
            var c = value[at];

            if (quote == '\'')
            {
                if (c == '\'')
                {
                    quote = '\0';
                }
                else
                {
                    word.Append(c);
                }

                continue;
            }

            if (quote == '"')
            {
                if (c == '"')
                {
                    quote = '\0';
                }
                else if (c == '\\' && at + 1 < value.Length && value[at + 1] is '"' or '\\')
                {
                    word.Append(value[++at]);
                }
                else
                {
                    word.Append(c);
                }

                continue;
            }

            if (c is ' ' or '\t' or '\r' or '\n')
            {
                if (begun)
                {
                    words.Add(word.ToString());
                    word.Clear();
                    begun = false;
                }

                continue;
            }

            begun = true;

            if (c is '\'' or '"')
            {
                quote = c;
            }
            else if (c == '\\')
            {
                if (++at >= value.Length)
                {
                    return null;
                }

                word.Append(value[at]);
            }
            else
            {
                word.Append(c);
            }
        }

        if (quote != '\0')
        {
            return null;
        }

        if (begun)
        {
            words.Add(word.ToString());
        }

        return words;
    }

    /// <summary><paramref name="value"/> as a directory beside the file: its <c>.</c> and <c>..</c> taken out, the file's own directory <c>.</c>; none where it is empty.</summary>
    private static string? Root(string value)
        => value.Trim() is { Length: > 0 } trimmed && trimmed.AsSpan().IndexOfAny('\r', '\n') < 0 ? CodeGraphIds.Clean(trimmed) : null;

    // ---- TOML ----

    /// <summary>
    /// Each string a TOML file sets, by the keys it stands under - its table's, then its own, then an inline table's; an array's
    /// elements each under the array's keys - in the order the file writes them. A value whose bracket or string never closes is
    /// marked on <paramref name="dynamic"/>.
    /// </summary>
    private static List<Entry> Toml(IReadOnlyList<string> lines, List<ExtractDynamic> dynamic)
    {
        var entries = new List<Entry>();
        IReadOnlyList<string> table = [];

        for (var line = 0; line < lines.Count;)
        {
            var text = lines[line];
            var at = Blank(text, 0);

            if (at >= text.Length || text[at] == '#')
            {
                line++;
                continue;
            }

            if (text[at] == '[')
            {
                // [table] and [[array of tables]]: the keys every key after it stands under.
                var array = at + 1 < text.Length && text[at + 1] == '[';
                table = Key(text, at + (array ? 2 : 1), ']', out _) ?? [];
                line++;
                continue;
            }

            if (Key(text, at, '=', out var equal) is not { } key)
            {
                line++;
                continue;
            }

            line = Value(lines, line, equal + 1, [.. table, .. key], entries, dynamic);
        }

        return entries;
    }

    /// <summary>
    /// The keys written from <paramref name="at"/> in <paramref name="text"/> to <paramref name="end"/> - bare, or quoted as a basic or
    /// a literal string, joined by dots - and where <paramref name="end"/> stands; none where the keys are not so written.
    /// </summary>
    private static List<string>? Key(string text, int at, char end, out int past)
    {
        var keys = new List<string>();
        past = -1;

        for (at = Blank(text, at); at < text.Length;)
        {
            string key;

            if (text[at] is '"' or '\'')
            {
                if (Quoted(text, at, out var closed) is not { } quoted)
                {
                    return null;
                }

                key = quoted;
                at = closed;
            }
            else
            {
                var start = at;

                while (at < text.Length && (char.IsAsciiLetterOrDigit(text[at]) || text[at] is '_' or '-'))
                {
                    at++;
                }

                if (at == start)
                {
                    return null;
                }

                key = text[start..at];
            }

            keys.Add(key);
            at = Blank(text, at);

            if (at < text.Length && text[at] == end)
            {
                past = at;
                return keys;
            }

            if (at >= text.Length || text[at] != '.')
            {
                return null;
            }

            at = Blank(text, at + 1);
        }

        return null;
    }

    /// <summary>One array or inline table a TOML value is inside: the keys of what it holds, whether it is a table, and - for a table - the key of the value being read and whether a key is awaited.</summary>
    private sealed class Nest(IReadOnlyList<string> path, bool table)
    {
        public IReadOnlyList<string> Path { get; } = path;

        public bool Table { get; } = table;

        public IReadOnlyList<string> Key { get; set; } = [];

        public bool Awaits { get; set; } = table;
    }

    /// <summary>
    /// Reads the value that begins at <paramref name="at"/> on <paramref name="line"/>, set under <paramref name="path"/>: each string it
    /// holds added to <paramref name="entries"/>, its arrays and inline tables on a stack of their own; answers the line after the value.
    /// </summary>
    private static int Value(IReadOnlyList<string> lines, int line, int at, IReadOnlyList<string> path, List<Entry> entries, List<ExtractDynamic> dynamic)
    {
        var nests = new Stack<Nest>();
        var begins = line;

        while (line < lines.Count)
        {
            var text = lines[line];
            at = Blank(text, at);

            // A line's end, or a comment: the value goes on past it only inside an array or a table.
            if (at >= text.Length || text[at] == '#')
            {
                if (nests.Count == 0)
                {
                    return line + 1;
                }

                line++;
                at = 0;
                continue;
            }

            var top = nests.Count > 0 ? nests.Peek() : null;
            var c = text[at];

            if (top is { Table: true, Awaits: true } && c != '}')
            {
                if (c == ',')
                {
                    at++;
                    continue;
                }

                if (Key(text, at, '=', out var equal) is not { } key)
                {
                    // No key where one is awaited: what stands here is passed over to the table's end.
                    at = text.Length;
                    continue;
                }

                top.Key = key;
                top.Awaits = false;
                at = equal + 1;
                continue;
            }

            IReadOnlyList<string> here = top is null ? path : top.Table ? [.. top.Path, .. top.Key] : top.Path;
            var done = false;

            switch (c)
            {
                case '[':
                    nests.Push(new Nest(here, table: false));
                    at++;
                    break;

                case '{':
                    nests.Push(new Nest(here, table: true));
                    at++;
                    break;

                case ']' or '}':
                    if (nests.Count > 0)
                    {
                        nests.Pop();
                    }

                    at++;
                    done = nests.Count == 0;
                    break;

                case ',':
                    if (top is { Table: true })
                    {
                        top.Awaits = true;
                    }

                    at++;
                    break;

                case '"' or '\'':
                    if (at + 2 < text.Length && text[at + 1] == c && text[at + 2] == c)
                    {
                        // A multi-line string: read to its close however many lines it takes, its value a string as a one-line string's is.
                        var (closes, past, written) = Multiline(lines, line, at + 3, c);

                        if (written is null)
                        {
                            dynamic.Add(new ExtractDynamic(begins + 1, "a string that never closes: the rest of the file is read as it"));
                            return lines.Count;
                        }

                        entries.Add(new Entry(here, written, line + 1, top is { Table: false }));
                        (line, at) = (closes, past);
                        done = nests.Count == 0;
                        break;
                    }

                    if (Quoted(text, at, out var closed) is { } value)
                    {
                        entries.Add(new Entry(here, value, line + 1, top is { Table: false }));
                        at = closed;
                    }
                    else
                    {
                        // A one-line string that never closes on its line, which TOML refuses: the rest of its line read as it.
                        dynamic.Add(new ExtractDynamic(line + 1, "a string that never closes on its line: the rest of the line is read as it"));
                        at = text.Length;
                    }

                    done = nests.Count == 0;
                    break;

                default:
                    // A number, a boolean, a date: no string, and so no root.
                    while (at < text.Length && text[at] is not (',' or ']' or '}' or '#') && !char.IsWhiteSpace(text[at]))
                    {
                        at++;
                    }

                    done = nests.Count == 0;
                    break;
            }

            if (done)
            {
                return line + 1;
            }
        }

        if (nests.Count > 0)
        {
            dynamic.Add(new ExtractDynamic(begins + 1, "a bracket that never closes: the rest of the file is read as its value"));
        }

        return lines.Count;
    }

    /// <summary>
    /// The multi-line string whose quote is <paramref name="quote"/> and whose text begins at <paramref name="at"/> on
    /// <paramref name="line"/>, as TOML reads it: a line break right after its opener none of its value; one or two quotes anywhere in
    /// it its own, the closer the last three of a run of them; a basic one's escapes decoded (<see cref="Escape"/>), and a backslash
    /// that ends a line's text taking every blank and line break after it to the next character; a literal one's text as written.
    /// Answers the line its closer stands on and the place past it, with its value; past the last line, with none, where it never
    /// closes.
    /// </summary>
    private static (int Line, int At, string? Value) Multiline(IReadOnlyList<string> lines, int line, int at, char quote)
    {
        var value = new StringBuilder();
        var first = line;

        // Whether the line break before the line being read is none of the value: the one right after the opener, and those a
        // backslash that ends a line's text takes.
        var opened = at >= lines[line].Length;
        var trimming = false;

        for (; line < lines.Count; line++, at = 0)
        {
            var text = lines[line];

            if (line > first)
            {
                if (!opened && !trimming)
                {
                    value.Append('\n');
                }

                opened = false;
            }

            for (; at < text.Length; at++)
            {
                var c = text[at];

                if (trimming && c is ' ' or '\t')
                {
                    continue;
                }

                trimming = false;

                if (c == quote)
                {
                    var run = 1;

                    while (at + run < text.Length && text[at + run] == quote)
                    {
                        run++;
                    }

                    if (run >= 3)
                    {
                        value.Append(quote, run - 3);
                        return (line, at + run, value.ToString());
                    }

                    value.Append(quote, run);
                    at += run - 1;
                    continue;
                }

                if (quote == '"' && c == '\\')
                {
                    if (Blank(text, at + 1) >= text.Length)
                    {
                        trimming = true;
                        break;
                    }

                    at = Escape(text, at, value);
                    continue;
                }

                value.Append(c);
            }
        }

        return (lines.Count, 0, null);
    }

    /// <summary>
    /// The one-line string that begins with its quote at <paramref name="at"/> in <paramref name="text"/>, as TOML reads it - a basic
    /// one's escapes decoded, a literal one's text as written - and where it ends; none where it never closes on its line.
    /// </summary>
    private static string? Quoted(string text, int at, out int past)
    {
        var quote = text[at];
        var value = new StringBuilder();
        past = -1;

        for (var index = at + 1; index < text.Length; index++)
        {
            var c = text[index];

            if (c == quote)
            {
                past = index + 1;
                return value.ToString();
            }

            if (quote == '"' && c == '\\' && index + 1 < text.Length)
            {
                index = Escape(text, index, value);
                continue;
            }

            value.Append(c);
        }

        return null;
    }

    /// <summary>
    /// Decodes the escape whose backslash is at <paramref name="at"/> in <paramref name="text"/> onto <paramref name="value"/>, as a basic
    /// string's is - one of TOML's short escapes, a code point of two, four or eight hexadecimal digits, and any other character as
    /// itself; answers the place of the last character it took.
    /// </summary>
    private static int Escape(string text, int at, StringBuilder value)
    {
        if (at + 1 >= text.Length)
        {
            value.Append('\\');
            return at;
        }

        var escaped = text[++at];
        var digits = escaped switch { 'x' => 2, 'u' => 4, 'U' => 8, _ => 0 };

        if (digits > 0 && Hex(text, at + 1, digits) is { } code)
        {
            value.Append(char.ConvertFromUtf32(code));
            return at + digits;
        }

        value.Append(escaped switch { 'b' => '\b', 't' => '\t', 'n' => '\n', 'f' => '\f', 'r' => '\r', 'e' => '\u001b', _ => escaped });
        return at;
    }

    /// <summary>The code point the <paramref name="count"/> hexadecimal digits at <paramref name="at"/> write; none where they are not so many digits, or no code point.</summary>
    private static int? Hex(string text, int at, int count)
        => at + count <= text.Length && int.TryParse(text.AsSpan(at, count), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var code) && code is >= 0 and <= 0x10FFFF and not (>= 0xD800 and <= 0xDFFF)
            ? code
            : null;

    /// <summary>The first place at or past <paramref name="at"/> in <paramref name="text"/> that holds no blank.</summary>
    private static int Blank(string text, int at)
    {
        while (at < text.Length && text[at] is ' ' or '\t')
        {
            at++;
        }

        return at;
    }

    // ---- setup.cfg ----

    /// <summary>
    /// Each option a setup.cfg sets, by its section and its name - the name in lower case and its dashes underscores, as setuptools
    /// reads it - with its value whole, and the line it is written on, as Python's configparser reads one: a line whose text begins
    /// with <c>#</c> or <c>;</c> a comment, and a blank one, neither ending a value; a line indented deeper than the one before it that
    /// continued none continuing its option; a section's header, a line whose text begins with <c>[</c>, its name the text from there
    /// to its last <c>]</c> as written; an option's name and its value parted by its first <c>=</c> or <c>:</c>.
    /// </summary>
    private static List<Entry> Ini(IReadOnlyList<string> lines)
    {
        var entries = new List<Entry>();
        var section = string.Empty;
        string? option = null;
        var value = new StringBuilder();
        var at = 0;

        // How deep the last line that continued no option is indented: a line deeper continues the option it set.
        var indent = 0;

        void Flush()
        {
            if (option is not null)
            {
                entries.Add(new Entry([section, option], value.ToString(), at));
            }

            option = null;
            value.Clear();
        }

        for (var line = 0; line < lines.Count; line++)
        {
            var text = lines[line];
            var trimmed = text.Trim();

            if (trimmed.Length == 0 || trimmed[0] is '#' or ';')
            {
                continue;
            }

            var level = text.Length - text.TrimStart().Length;

            if (option is not null && level > indent)
            {
                value.Append('\n').Append(trimmed);
                continue;
            }

            Flush();
            indent = level;

            if (trimmed[0] == '[' && trimmed.LastIndexOf(']') is var close and > 1)
            {
                section = trimmed[1..close];
                continue;
            }

            var separator = trimmed.IndexOfAny(['=', ':']);

            if (separator <= 0)
            {
                continue;
            }

            option = trimmed[..separator].Trim().ToLowerInvariant().Replace('-', '_');
            value.Append(trimmed[(separator + 1)..].Trim());
            at = line + 1;
        }

        Flush();
        return entries;
    }

    /// <summary>Whether <paramref name="path"/> is the option that names the project: <c>[metadata]</c>'s <c>name</c>.</summary>
    private static bool IniName(IReadOnlyList<string> path) => path is ["metadata", "name"];

    /// <summary>
    /// The roots a setup.cfg's option under <paramref name="path"/> names by <paramref name="value"/>, each read as the tool that reads it
    /// does: <c>[options]</c>'s <c>package_dir</c> the directory of its entry with no package's name; <c>[options.packages.find]</c>'s
    /// <c>where</c> the first entry of its list - its lines where it is written over several, else its parts between commas - which
    /// setuptools takes as the one directory it looks in; <c>[tool:pytest]</c>'s <c>pythonpath</c> each word it writes, as pytest splits it
    /// (<see cref="ShellWords"/>); a value an interpolation computes none.
    /// </summary>
    private static IEnumerable<string> IniRoots(IReadOnlyList<string> path, string value)
    {
        if (value.Contains("%(", StringComparison.Ordinal))
        {
            yield break;
        }

        if (path is ["options", "package_dir"])
        {
            foreach (var entry in value.Split([',', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (entry.IndexOf('=') is var equal && equal >= 0 && entry[..equal].Trim().Length == 0 && Root(entry[(equal + 1)..]) is { } root)
                {
                    yield return root;
                }
            }
        }
        else if (path is ["options.packages.find", "where"])
        {
            if (value.Split(value.Contains('\n') ? '\n' : ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) is [var first, ..] && Root(first) is { } root)
            {
                yield return root;
            }
        }
        else if (path is ["tool:pytest", "pythonpath"])
        {
            foreach (var word in ShellWords(value) ?? [])
            {
                if (Root(word) is { } root)
                {
                    yield return root;
                }
            }
        }
    }
}
