// Writes the table of Unicode's character names that DssHarness.Core embeds (UnicodeNames), from
// two files of the Unicode Character Database, downloaded by hand and never committed:
//
//     dotnet run .github/scripts/unicode-names.cs <ucd>/UnicodeData.txt <ucd>/NameAliases.txt <version> src/DssHarness.Core/Codebase/Languages/Scanning/unicode-names.bin
//
// A Python string's \N{...} names a character as CPython 3.15 reads a name, and .NET holds no character
// name to ask - and under invariant globalization loads no ICU, whose names would be whichever
// Unicode version the machine carries. So the names are read from the files once, here, and the
// table is committed: the same name decodes to the same character on every machine, and a newer
// Unicode is taken by generating the table again from its files. They are
// https://www.unicode.org/Public/<version>/ucd/UnicodeData.txt and NameAliases.txt, each under the
// Unicode License v3 - Copyright © 1991-2026 Unicode, Inc. - whose text THIRD-PARTY-NOTICES
// carries beside the data this table is made of. NamedSequences.txt is never read: a literal names
// a character, never a named sequence.
//
// What the table holds, in order, little-endian, every number an LEB128 varint: the magic DSUN and
// the format byte 2; the version; the words the names and aliases are made of, the most used first
// and then in ordinal order, so the same files always give the same bytes; the names by code point,
// each as the difference from the one before and its words; the aliases in their file's order, each
// with its kind; and the prefixes whose names are computed from a code, in CPython's order, each
// with the ranges of the code points it names.
//
// Which names are stored and which computed is CPython's own rule, as the program that writes its
// tables reads the same file (Tools/unicode/makeunicodedata.py, UnicodeData.__init__ and
// makeunicodename, at v3.15.0), so that the reader answers as CPython 3.15 does:
//
//   - A Hangul syllable's name is computed from its three jamo, which the reader holds; the range
//     the database writes for the syllables is passed over.
//   - The characters of a range the database writes <CJK Ideograph..., First> / <..., Last> and
//     <Tangut Ideograph..., First> / <..., Last> are named by a prefix - CJK UNIFIED IDEOGRAPH-,
//     TANGUT IDEOGRAPH- - and four to six hex digits of the code, each range recorded for its prefix.
//   - A name that ends with its own code - NUSHU CHARACTER-1B170, CJK COMPATIBILITY IDEOGRAPH-F900,
//     EGYPTIAN HIEROGLYPH-13460 - is named by the prefix it leaves and the code: its code point is
//     a range of that prefix, joined to the range before it where the code points follow each
//     other, and a prefix the table did not hold yet stands last, in the order the file writes them.
//   - Every other name is stored; a control's <control> never is, since CPython has no name to look
//     up for one.
//
// A line the files should never hold refuses the whole table, naming it.

using System.Text;

if (args.Length != 4)
{
    return Fail("usage: unicode-names <UnicodeData.txt> <NameAliases.txt> <version> <out.bin>");
}

var (dataFile, aliasFile, version, outputFile) = (args[0], args[1], args[2], args[3]);
var kinds = new[] { "correction", "control", "alternate", "figment", "abbreviation" };

// The ranges the database writes whose characters are named by a prefix of their own, in makeunicodedata.py's
// order: the name a range's First line is written with, and the prefix its characters' names begin with. The Hangul
// syllables' is first there and here, and is the one no range is written for: its names are made from the jamo.
var rangePrefixes = new[] { ("Hangul Syllable", "HANGUL SYLLABLE "), ("CJK Ideograph", "CJK UNIFIED IDEOGRAPH-"), ("Tangut Ideograph", "TANGUT IDEOGRAPH-") };

// The version the table is labelled with is the one the file says of itself, so a table is never
// labelled with a version that is not its files'.
if (Read(aliasFile) is not { Length: > 0 } aliasLines || !aliasLines[0].Equals($"# NameAliases-{version}.txt", StringComparison.Ordinal))
{
    return Fail($"the first line of {aliasFile} is not '# NameAliases-{version}.txt': the version given is not the one the files are");
}

// UnicodeData.txt holds one line a code point, in its order: a name; the first and the last of a
// range; or a control's <control>, which names nothing. The names stored are those CPython stores;
// the computed prefixes stand in its order, each with the ranges of the code points it names.
var names = new List<(int Code, string Name)>();
var computed = new List<(string Prefix, List<(int First, int Last)> Ranges)> { (rangePrefixes[1].Item2, []), (rangePrefixes[2].Item2, []) };
var stored = new HashSet<string>(StringComparer.Ordinal);
string? opened = null;
var first = 0;
var dataLines = Read(dataFile);

for (var at = 0; at < dataLines.Length; at++)
{
    var number = at + 1;
    var line = dataLines[at];

    // The file ends with an empty line.
    if (line.Length == 0)
    {
        continue;
    }

    var fields = line.Split(';');

    if (fields.Length != 15)
    {
        return Fail($"{dataFile}:{number}: a line of UnicodeData.txt holds 15 fields; this one holds {fields.Length}");
    }

    if (!TryCode(fields[0], out var code))
    {
        return Fail($"{dataFile}:{number}: '{fields[0]}' is no code point: up to six hexadecimal digits, none greater than 10FFFF");
    }

    var field = fields[1];

    if (field.StartsWith('<'))
    {
        const string start = ", First>";
        const string end = ", Last>";

        if (field.EndsWith(start, StringComparison.Ordinal))
        {
            if (opened is not null)
            {
                return Fail($"{dataFile}:{number}: '{field}' opens while '{opened}{start}' is still open");
            }

            (opened, first) = (field[1..^start.Length], code);
            continue;
        }

        if (field.EndsWith(end, StringComparison.Ordinal))
        {
            if (opened is null)
            {
                return Fail($"{dataFile}:{number}: '{field}' closes no range that is open");
            }

            if (opened != field[1..^end.Length])
            {
                return Fail($"{dataFile}:{number}: '{field}' closes '{opened}', which is not the range it names");
            }

            // The range is recorded for the prefix its First name is written with, where that is one of the computed
            // prefixes'; the Hangul syllables' is not written, their names being made from the jamo.
            if (RangePrefix(opened) is { } prefix && prefix != rangePrefixes[0].Item2)
            {
                RangesOf(prefix)!.Add((first, code));
            }

            opened = null;
            continue;
        }

        // A control names nothing and is no range.
        continue;
    }

    if (opened is not null)
    {
        return Fail($"{dataFile}:{number}: '{field}' names a character while the range '{opened}' is still open");
    }

    if (!IsName(field))
    {
        return Fail($"{dataFile}:{number}: '{field}' is no character name: capital letters, digits, blanks and hyphens alone");
    }

    // A name that ends with its own code, as the file spells it, is computed and never stored: the prefix it leaves
    // names every code point of its ranges, and this one joins the range before it where the code points follow each other.
    if (field.EndsWith(fields[0], StringComparison.Ordinal))
    {
        var prefix = field[..^fields[0].Length];

        if (prefix.Length == 0)
        {
            return Fail($"{dataFile}:{number}: '{field}' is a code alone, with no name before it");
        }

        var ranges = RangesOf(prefix) ?? ComputedLast(prefix);

        if (ranges.Count > 0 && ranges[^1].Last == code - 1)
        {
            ranges[^1] = (ranges[^1].First, code);
        }
        else
        {
            ranges.Add((code, code));
        }

        continue;
    }

    if (!stored.Add(field))
    {
        return Fail($"{dataFile}:{number}: '{field}' names a second code point, {code:X}");
    }

    names.Add((code, field));
}

if (opened is not null)
{
    return Fail($"{dataFile}: the range '{opened}' is never closed");
}

if (names.Count == 0)
{
    return Fail($"{dataFile} names no character");
}

// NameAliases.txt holds one line an alias: a code point, the alias, and its kind. The same code
// point may carry several. Its comments begin with #.
var aliases = new List<(int Code, string Alias, int Kind)>();

for (var at = 1; at < aliasLines.Length; at++)
{
    var number = at + 1;
    var line = aliasLines[at];

    if (line.Length == 0 || line.StartsWith('#'))
    {
        continue;
    }

    var fields = line.Split(';');

    if (fields.Length != 3)
    {
        return Fail($"{aliasFile}:{number}: a line of NameAliases.txt holds 3 fields; this one holds {fields.Length}");
    }

    var kind = Array.IndexOf(kinds, fields[2]);

    if (!TryCode(fields[0], out var code))
    {
        return Fail($"{aliasFile}:{number}: '{fields[0]}' is no code point: up to six hexadecimal digits, none greater than 10FFFF");
    }

    if (!IsName(fields[1]) || kind < 0)
    {
        return Fail($"{aliasFile}:{number}: '{line}' is no alias: a name of capital letters, digits, blanks and hyphens, and a kind among {string.Join(", ", kinds)}");
    }

    // Names and aliases are one namespace: none of them names a second code point.
    if (!stored.Add(fields[1]))
    {
        return Fail($"{aliasFile}:{number}: '{fields[1]}' is already a name or an alias in the files");
    }

    aliases.Add((code, fields[1], kind));
}

names.Sort((one, other) => one.Code.CompareTo(other.Code));

for (var at = 1; at < names.Count; at++)
{
    if (names[at].Code == names[at - 1].Code)
    {
        return Fail($"{dataFile}: the code point {names[at].Code:X} carries the names '{names[at - 1].Name}' and '{names[at].Name}'");
    }
}

// The words, the most used first and then in ordinal order: the same files always give the same table.
var used = new Dictionary<string, int>(StringComparer.Ordinal);

foreach (var word in names.SelectMany(name => name.Name.Split(' ')).Concat(aliases.SelectMany(alias => alias.Alias.Split(' '))))
{
    used[word] = used.GetValueOrDefault(word) + 1;
}

var words = used.Keys.OrderBy(word => -used[word]).ThenBy(word => word, StringComparer.Ordinal).ToArray();
var places = new Dictionary<string, int>(StringComparer.Ordinal);

for (var at = 0; at < words.Length; at++)
{
    places.Add(words[at], at);
}

var bytes = new List<byte>(1 << 19);
bytes.AddRange("DSUN"u8);
bytes.Add(2);
Text(version);
Number(words.Length);

foreach (var word in words)
{
    Text(word);
}

Number(names.Count);
var previous = 0;

foreach (var (code, name) in names)
{
    Number(code - previous);
    previous = code;
    Words(name);
}

Number(aliases.Count);

foreach (var (code, alias, kind) in aliases)
{
    Number(code);
    bytes.Add((byte)kind);
    Words(alias);
}

Number(computed.Count);

foreach (var (prefix, ranges) in computed)
{
    Text(prefix);
    Number(ranges.Count);

    foreach (var (start, end) in ranges)
    {
        Number(start);
        Number(end);
    }
}

try
{
    File.WriteAllBytes(outputFile, [.. bytes]);
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
{
    return Fail($"cannot write {outputFile}: {ex.Message}");
}

var counted = kinds.Select((kind, at) => $"{aliases.Count(alias => alias.Kind == at)} {kind}");
var longest = names.Max(name => name.Name.Length);
var prefixes = computed.Select(entry => $"{entry.Prefix}: {entry.Ranges.Count} range(s)");
Console.WriteLine($"Unicode {version}: {names.Count} names, {aliases.Count} aliases ({string.Join(", ", counted)}), {computed.Count} computed prefixes ({string.Join("; ", prefixes)}), {words.Length} words, the longest name {longest} characters; {bytes.Count} bytes into {outputFile}");
return 0;

// The prefix the characters of the range are named by, where the range's name is one of the computed prefixes': the first of
// makeunicodedata.py's list it begins with; none for any other range.
string? RangePrefix(string range)
{
    foreach (var (name, prefix) in rangePrefixes)
    {
        if (range.StartsWith(name, StringComparison.Ordinal))
        {
            return prefix;
        }
    }

    return null;
}

// The ranges recorded for a computed prefix; none where the table holds the prefix not yet.
List<(int First, int Last)>? RangesOf(string prefix)
{
    foreach (var (held, ranges) in computed)
    {
        if (held == prefix)
        {
            return ranges;
        }
    }

    return null;
}

// A computed prefix the table held not yet, added last as the file first writes it, with its ranges.
List<(int First, int Last)> ComputedLast(string prefix)
{
    var ranges = new List<(int First, int Last)>();
    computed.Add((prefix, ranges));
    return ranges;
}

string[] Read(string path)
{
    try
    {
        return File.ReadAllLines(path, Encoding.UTF8);
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        // Reported as an annotation like every other failure here, rather than as an unhandled
        // exception whose stack trace buries the cause.
        Fail($"cannot read {path}: {ex.Message}");
        throw;
    }
}

bool TryCode(string field, out int code)
{
    code = 0;

    if (field.Length is 0 or > 6)
    {
        return false;
    }

    foreach (var digit in field)
    {
        if (!char.IsAsciiHexDigit(digit))
        {
            return false;
        }

        code = (code * 16) + (char.IsAsciiDigit(digit) ? digit - '0' : (digit | 0x20) - 'a' + 10);
    }

    return code <= 0x10FFFF;
}

bool IsName(string name)
    => name.Length > 0 && name.All(character => character is (>= 'A' and <= 'Z') or (>= '0' and <= '9') or ' ' or '-');

void Number(int value)
{
    // A negative number is a defect of this program, never of the files: it is not written as the
    // five bytes its bits would take.
    if (value < 0)
    {
        throw new InvalidOperationException($"a negative number in the table: {value}");
    }

    var rest = (uint)value;

    do
    {
        var low = (byte)(rest & 0x7F);
        rest >>= 7;
        bytes.Add(rest > 0 ? (byte)(low | 0x80) : low);
    }
    while (rest > 0);
}

void Text(string text)
{
    Number(text.Length);
    bytes.AddRange(Encoding.ASCII.GetBytes(text));
}

void Words(string name)
{
    var split = name.Split(' ');
    Number(split.Length);

    foreach (var word in split)
    {
        Number(places[word]);
    }
}

int Fail(string message)
{
    Console.Error.WriteLine($"::error::{message}");
    Environment.Exit(1);
    return 1;
}
