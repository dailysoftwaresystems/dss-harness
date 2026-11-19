using System.Globalization;
using System.Text;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Orchestration;

namespace DssHarness.Core.Codebase;

/// <summary>
/// The kinds of node a codebase graph holds: the one vocabulary every implementation of <see cref="ICodeGraphExtractor"/>
/// answers in, and every record and answer is read by.
/// </summary>
public static class CodeNodeKinds
{
    /// <summary>A file the tree lists, whatever reads it. Made by the core for every file it keeps, never by an implementation.</summary>
    public const string File = "file";

    /// <summary>A project file that names others: a <c>.csproj</c>, a <c>.slnx</c>, a Dart package's <c>pubspec.yaml</c>, a Node package's <c>package.json</c>, a <c>tsconfig.json</c>, a Composer package's <c>composer.json</c>.</summary>
    public const string Project = "project";

    /// <summary>A namespace of a language that has them.</summary>
    public const string Namespace = "namespace";

    /// <summary>A declared type.</summary>
    public const string Type = "type";

    /// <summary>A declaration inside a type, or a function at file or namespace scope.</summary>
    public const string Member = "member";

    /// <summary>A Markdown heading and the lines under it, so one section can be served alone.</summary>
    public const string Section = "section";

    /// <summary>
    /// The kinds that are declarations - a type, a member, a section of a document - rather than a file, a project or a
    /// namespace, whose lines hold others': what a change is told by, and what a context pack seeds from a file.
    /// </summary>
    public static IReadOnlyList<string> Declarations { get; } = [Type, Member, Section];

    /// <summary>Every kind an implementation may give a node of its extract: all but <see cref="File"/>, which is the core's.</summary>
    public static IReadOnlyList<string> Extracted { get; } = [Project, Namespace, Type, Member, Section];
}

/// <summary>The kinds of directed edge a codebase graph holds, each read from the structure, a reference or derived when linking.</summary>
public static class CodeEdgeKinds
{
    /// <summary>A file, project, namespace or type to what is declared inside it.</summary>
    public const string Contains = "contains";

    /// <summary>A file to a namespace or a file it imports.</summary>
    public const string Imports = "imports";

    /// <summary>A type to its base type.</summary>
    public const string Inherits = "inherits";

    /// <summary>A type to an interface it implements.</summary>
    public const string Implements = "implements";

    /// <summary>A type to a mixin it takes.</summary>
    public const string MixesIn = "mixesIn";

    /// <summary>A member to the member of a base type it overrides.</summary>
    public const string Overrides = "overrides";

    /// <summary>A member to a member it calls.</summary>
    public const string Calls = "calls";

    /// <summary>An interface's member to each member that implements it: derived when linking.</summary>
    public const string Dispatches = "dispatches";

    /// <summary>A member to a type it makes: a call that names a type.</summary>
    public const string Instantiates = "instantiates";

    /// <summary>A member or a type to a type it names.</summary>
    public const string References = "references";

    /// <summary>
    /// A declaration to what it reads, writes or names without calling it: a field, a property, a constant or an enum
    /// member, and a name a <c>nameof</c> names; a style rule a page's markup names; a configuration key a project's
    /// <c>$(Name)</c> names, and one the keys' join gives the code that names it.
    /// </summary>
    public const string Uses = "uses";

    /// <summary>
    /// One part of a declaration to another: a XAML view and the C# type its <c>x:Class</c> names, a Blazor component and
    /// its code-behind partial - derived when linking, never asked for by an extract.
    /// </summary>
    public const string PartOf = "partOf";

    /// <summary>A declaration with no body to the one with a body and the same qualified name: derived when linking.</summary>
    public const string Declares = "declares";

    /// <summary>A test to a member it calls directly: derived when linking.</summary>
    public const string Tests = "tests";

    /// <summary>A project to a project it names.</summary>
    public const string ProjectReference = "projectReference";

    /// <summary>The kinds an implementation's references may ask for: what a reference to resolve can be, before linking makes it an edge.</summary>
    public static IReadOnlyList<string> Referenced { get; } = [Inherits, Implements, MixesIn, Overrides, Calls, Instantiates, References, Uses, ProjectReference];

    /// <summary>Every kind an edge of a graph may be, with what it means as help says it: the one list of both.</summary>
    public static IReadOnlyList<(string Kind, string Meaning)> Described { get; } =
    [
        (Contains, "a file, project, namespace or type to what is declared inside it"),
        (Imports, "a file to a namespace or a file it imports"),
        (Inherits, "a type to its base type"),
        (Implements, "a type to an interface it implements"),
        (MixesIn, "a type to a mixin it takes"),
        (Overrides, "a member to the base type's member it overrides"),
        (Calls, "a member to a member it calls"),
        (Dispatches, "an interface's member to each member that answers it for a type"),
        (Instantiates, "a member to a type it makes"),
        (References, "a member or a type to a type it names"),
        (Uses, "a declaration to what it reads, writes or names without calling it: a member, a style rule its markup names, a configuration key its code names"),
        (PartOf, "one part of a declaration to another: a view and the type its x:Class names, a component and its code-behind"),
        (Declares, "a declaration with no body to the one with a body"),
        (Tests, "a test to a member it calls directly"),
        (ProjectReference, "a project to a project it names"),
    ];

    /// <summary>Every kind an edge of a graph may be.</summary>
    public static IReadOnlyList<string> All { get; } = [.. Described.Select(described => described.Kind)];

    /// <summary>
    /// Whether an edge of <paramref name="kind"/> is recall, never a doubt - a use: what it reaches, or fails to, and how
    /// sure it is say nothing of how sure the graph is of what it declares, so no count of a node's or a file's
    /// unsureness, and no answer's, is read from it. The one owner of that rule, which the linker, the queries and the
    /// <c>graph</c> tool ask.
    /// </summary>
    /// <param name="kind">An edge's kind.</param>
    public static bool Recall(string kind) => kind == Uses;
}

/// <summary>
/// The kinds within a kind the linker reads, of the nodes of data files and style sheets - a configuration key, an
/// element, each node a style sheet declares - which no reference reaches by its name alone: only one whose qualifier
/// says it names one (<see cref="ReachedBy"/>), a use through an import's prefix, which names what the file it imports
/// declares, and a use an import binds by a name the file exports the node by - a CSS module's class.
/// Every kind a style sheet's node is of is one of these, spelt so no code's kind is, so a name code writes - a PHP
/// <c>$radius</c>, a C# <c>Button()</c> - reaches none of them. Every other kind within a kind is its implementation's
/// to name freely.
/// </summary>
public static class CodeSubKinds
{
    /// <summary>
    /// A configuration key - JSON's and YAML's, a <c>.resx</c>'s <c>data</c>, a <c>.props</c> file's property - named by
    /// its path: reached by a key qualifier, an MSBuild project's <c>$(Name)</c>, and joined by the keys' join to the
    /// code that names it.
    /// </summary>
    public const string Key = "key";

    /// <summary>An element of an XML file named by an id-like attribute: data, which no reference reaches by its name alone.</summary>
    public const string Element = "element";

    /// <summary>
    /// A style sheet's rule - a selector's simple part, <c>.btn</c>, <c>#main</c>, an element, or a suffix a <c>&amp;</c>
    /// writes onto several selectors (<c>&amp;-x</c>, <see cref="ExtractNode.SuffixOf"/>) standing for each name it writes:
    /// reached by a selector - markup's <c>class</c> and <c>id</c>, a sheet's <c>@extend</c>, <c>@apply</c> and
    /// <c>composes</c> - that names it, or one of those names.
    /// </summary>
    public const string Rule = "rule";

    /// <summary>A style sheet's <c>@keyframes</c>, by its animation's name: reached by an animation that names it.</summary>
    public const string Keyframes = "keyframes";

    /// <summary>A Sass <c>@mixin</c>: reached by an <c>@include</c> of it.</summary>
    public const string StyleMixin = "style-mixin";

    /// <summary>
    /// A Sass <c>@function</c>, or CSS's own custom one (<c>@function --x</c>): reached by a value's call of it - its name
    /// before its parenthesis, <c>rem(2px)</c> - and through a module's namespace, <c>m.rem(2px)</c>.
    /// </summary>
    public const string StyleFunction = "style-function";

    /// <summary>
    /// A style sheet's <c>@font-face</c>, by the family it names, its ASCII letters in lower case as CSS matches a family's
    /// name: reached by a <c>font-family</c> or a <c>font</c> that names the family.
    /// </summary>
    public const string FontFace = "font-face";

    /// <summary>
    /// A style sheet's variable - a custom property (<c>--x</c>, an <c>@property</c>'s among them), a Sass variable
    /// (<c>$x</c>), a Less variable or detached ruleset (<c>@x</c>): reached by a value that names it.
    /// </summary>
    public const string StyleVariable = "style-variable";

    /// <summary>
    /// A style sheet's top, <c>(sheet)</c>: what it does outside every node it declares - a top-level <c>@include</c>, a
    /// flow control's head, a rule of no parts - the node those references are made from, as a script's module node is;
    /// reached by no name.
    /// </summary>
    public const string Sheet = "sheet";

    /// <summary>Whether <paramref name="subKind"/> is a data file's or a style sheet's, which no reference reaches by its name alone.</summary>
    /// <param name="subKind">A node's kind within its kind, or <see langword="null"/>.</param>
    public static bool IsData(string? subKind) => subKind is Key or Element or Rule or Keyframes or StyleMixin or StyleFunction or FontFace or StyleVariable or Sheet;

    /// <summary>
    /// The kind within a kind a reference of the qualifier <paramref name="qualifier"/> reaches alone: a rule for a
    /// selector, a key for a key qualifier, and for each qualifier a style sheet writes where its syntax says what a name
    /// names - a variable, a mixin, a function, an animation's keyframes, a font's face - that kind; <see langword="null"/>
    /// for every other, which reaches no node of data by its name alone.
    /// </summary>
    /// <param name="qualifier">The kind of a reference's qualifier (<see cref="CodeQualifierKinds"/>), or <see langword="null"/>.</param>
    public static string? ReachedBy(string? qualifier) => qualifier switch
    {
        CodeQualifierKinds.Selector => Rule,
        CodeQualifierKinds.Key => Key,
        CodeQualifierKinds.StyleVariable => StyleVariable,
        CodeQualifierKinds.StyleMixin => StyleMixin,
        CodeQualifierKinds.StyleFunction => StyleFunction,
        CodeQualifierKinds.Keyframes => Keyframes,
        CodeQualifierKinds.FontFace => FontFace,
        _ => null,
    };
}

/// <summary>How sure an edge is of its target.</summary>
public static class CodeConfidences
{
    /// <summary>The target is known from the code itself: a qualifier, or the scopes the reference is written in.</summary>
    public const string Exact = "exact";

    /// <summary>One declaration the reference can see has that name and arity.</summary>
    public const string Unique = "unique";

    /// <summary>
    /// Two or more declarations have that name and arity: an edge to each, none picked and none dropped, however many
    /// they are. The keys' join is candidates whatever it finds, one key among them, for nothing says the code reads that
    /// key; a literal or a member is joined to every key it names.
    /// </summary>
    public const string Candidates = "candidates";

    /// <summary>Every confidence an edge may carry, the surest first.</summary>
    public static IReadOnlyList<string> All { get; } = [Exact, Unique, Candidates];

    /// <summary>The less sure of <paramref name="one"/> and <paramref name="other"/>: how sure a way of several edges is.</summary>
    /// <param name="one">One confidence of <see cref="All"/>.</param>
    /// <param name="other">Another.</param>
    public static string Weaker(string one, string other) => Rank(one) >= Rank(other) ? one : other;

    /// <summary>The surer of <paramref name="one"/> and <paramref name="other"/>: how sure an edge found two ways is.</summary>
    /// <param name="one">One confidence of <see cref="All"/>.</param>
    /// <param name="other">Another.</param>
    public static string Surer(string one, string other) => Rank(one) <= Rank(other) ? one : other;

    private static int Rank(string confidence)
    {
        for (var at = 0; at < All.Count; at++)
        {
            if (All[at] == confidence)
            {
                return at;
            }
        }

        throw new ArgumentOutOfRangeException(nameof(confidence), confidence, "Not a confidence an edge of the graph carries.");
    }
}

/// <summary>What became of a file the graph keeps: the one list of the states a file node and the manifest hold.</summary>
public static class CodeFileStates
{
    /// <summary>Read by an implementation of a language.</summary>
    public const string Parsed = "parsed";

    /// <summary>Read by an implementation that outlines its file (<see cref="ICodeGraphExtractor.Outlines"/>).</summary>
    public const string Outlined = "outlined";

    /// <summary>Read by the fallback alone: decoded and counted whole like any file read, and holding no declaration.</summary>
    public const string Listed = "listed";

    /// <summary>
    /// Generated code, under <c>codebaseGraph.generated: exclude</c>: read, its extract kept, and its declarations left out
    /// of the link, so a change of the shape alone reads nothing again.
    /// </summary>
    public const string Generated = "generated";

    /// <summary>A NUL in its first bytes, or a Git LFS pointer.</summary>
    public const string Binary = "binary";

    /// <summary>
    /// Retired: what an earlier build of this tool held a file larger than its <c>codebaseGraph.maxFileBytes</c> in, unread.
    /// No file is held in it now - every file claimed is read whole, whatever its size - and a view that still holds one says
    /// so once, at the sync that reads it.
    /// </summary>
    public const string TooLarge = "tooLarge";

    /// <summary>
    /// Not read, with why: neither UTF-8 nor UTF-16 by its mark; named in no UTF-8, or by a name no record can hold; gone,
    /// or changed, between its listing and the listing after its read; an I/O error; a content test that threw; or an
    /// implementation that threw on it or answered no sound extract - a defect of that implementation, which
    /// <c>failOn: incomplete</c> fails on too.
    /// </summary>
    public const string Unreadable = "unreadable";

    /// <summary>The implementations that claim it said yes several times, or none did.</summary>
    public const string Ambiguous = "ambiguous";

    /// <summary>A link git records as one, or a path a link lies along on disk, a directory junction among them: listed, and not followed.</summary>
    public const string Link = "link";

    /// <summary>A submodule: listed, and not entered.</summary>
    public const string Submodule = "submodule";

    /// <summary>The states whose file was read and given to an implementation, so its extract is kept.</summary>
    public static IReadOnlyList<string> Extracted { get; } = [Parsed, Outlined, Listed, Generated];

    /// <summary>Every state a file may be in.</summary>
    public static IReadOnlyList<string> All { get; } = [Parsed, Outlined, Listed, Generated, Binary, TooLarge, Unreadable, Ambiguous, Link, Submodule];
}

/// <summary>The handles and digests of a graph: the one place each is computed.</summary>
public static class CodeGraphIds
{
    /// <summary>The length of a handle where no other in its view begins alike.</summary>
    public const int HandleLength = 6;

    /// <summary>The id of the file node of <paramref name="path"/>: what every file of a graph is named by.</summary>
    /// <param name="path">The file's path, relative to the tree's root with forward separators.</param>
    public static string FileId(string path) => $"{CodeNodeKinds.File}:{path}";

    /// <summary>The name of the file <paramref name="path"/> names: its last part, which an exact claim and an import by a path's ending read.</summary>
    /// <param name="path">A path, relative to the tree's root with forward separators.</param>
    public static string FileName(string path) => path[(path.LastIndexOf('/') + 1)..];

    /// <summary>
    /// The path <paramref name="relative"/> names beside the file <paramref name="path"/> - in its directory, with its
    /// <c>.</c> and <c>..</c> taken out - as an import by a relative path and an address root name one; <see langword="null"/>
    /// where it climbs out of the tree.
    /// </summary>
    /// <param name="path">A file's path, relative to the tree's root with forward separators.</param>
    /// <param name="relative">A path relative to that file's directory.</param>
    public static string? Beside(string path, string relative)
        => Normalize(path.LastIndexOf('/') is var slash and >= 0 ? $"{path[..slash]}/{relative}" : relative);

    /// <summary><paramref name="path"/> with its <c>.</c> and <c>..</c> taken out; <see langword="null"/> where it climbs out of the tree.</summary>
    /// <param name="path">A path relative to the tree's root, with either separator.</param>
    public static string? Normalize(string path) => Parts(path, climbs: false) is { } parts ? string.Join('/', parts) : null;

    /// <summary>
    /// <paramref name="path"/>, relative to a directory, with its <c>.</c> and <c>..</c> taken out and forward separators -
    /// a <c>..</c> kept only where it climbs above that directory - and <c>.</c> where nothing is left: a path as a
    /// project file's extract writes one relative to its own directory.
    /// </summary>
    /// <param name="path">A path relative to a directory, with either separator.</param>
    public static string Clean(string path) => Parts(path, climbs: true) is { Count: > 0 } parts ? string.Join('/', parts) : ".";

    /// <summary>The parts of <paramref name="path"/> once its <c>.</c> and <c>..</c> are taken out; <see langword="null"/> where it climbs above where it begins and <paramref name="climbs"/> does not keep that.</summary>
    private static List<string>? Parts(string path, bool climbs)
    {
        var parts = new List<string>();

        foreach (var part in path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".")
            {
                continue;
            }

            if (part == "..")
            {
                if (parts.Count == 0 || parts[^1] == "..")
                {
                    if (!climbs)
                    {
                        return null;
                    }

                    parts.Add(part);
                    continue;
                }

                parts.RemoveAt(parts.Count - 1);
                continue;
            }

            parts.Add(part);
        }

        return parts;
    }

    /// <summary>
    /// Each of <paramref name="ids"/>'s handle: the first <see cref="HandleLength"/> hexadecimal characters of its
    /// SHA-256, lengthened, for every id that shares them, until no two of the given ids share one.
    /// </summary>
    /// <param name="ids">The ids of one view, each once.</param>
    public static IReadOnlyDictionary<string, string> Handles(IEnumerable<string> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);

        var hashes = ids.Distinct(StringComparer.Ordinal).ToDictionary(id => id, Sha256, StringComparer.Ordinal);
        var handles = new Dictionary<string, string>(StringComparer.Ordinal);
        var length = HandleLength;
        var left = hashes.Keys.ToList();

        while (left.Count > 0)
        {
            var clashing = new List<string>();

            foreach (var group in left.GroupBy(id => hashes[id][..length], StringComparer.Ordinal))
            {
                if (group.Count() == 1 || length == hashes[group.First()].Length)
                {
                    foreach (var id in group)
                    {
                        handles[id] = group.Key;
                    }
                }
                else
                {
                    clashing.AddRange(group);
                }
            }

            left = clashing;
            length++;
        }

        return handles;
    }

    /// <summary>
    /// The digest of the lines <paramref name="start"/> to <paramref name="end"/> of <paramref name="lines"/>: the SHA-256
    /// of those lines joined by a line feed, so a file whose line endings changed and nothing else keeps every digest.
    /// What a node's <c>digest</c> is, and what a body read fresh is checked against.
    /// </summary>
    /// <param name="lines">The file's lines, without their endings.</param>
    /// <param name="start">The first line, from 1.</param>
    /// <param name="end">The last line, inclusive.</param>
    public static string Digest(IReadOnlyList<string> lines, int start, int end)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentOutOfRangeException.ThrowIfLessThan(start, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(end, start);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(end, lines.Count);

        return FileContentHash.Of(into =>
        {
            var joined = new JoinedLines(into);

            for (var at = start - 1; at < end; at++)
            {
                joined.Line();
                joined.Write(lines[at]);
            }

            joined.End();
        });
    }

    /// <summary>
    /// The digest of the lines <paramref name="start"/> to <paramref name="end"/> of <paramref name="text"/>, as
    /// <see cref="Digest(IReadOnlyList{string}, int, int)"/> gives of its lines: each line's characters read by position a
    /// piece at a time and taken into the hash as they are read, so neither a line nor the lines together is ever one
    /// string - a node over a whole file of any size is digested in the memory of a piece.
    /// </summary>
    /// <param name="text">The file's text.</param>
    /// <param name="start">The first line, from 1.</param>
    /// <param name="end">The last line, inclusive.</param>
    public static string Digest(CodeText text, int start, int end)
    {
        ArgumentNullException.ThrowIfNull(text);

        var lines = text.LineIndex;
        ArgumentOutOfRangeException.ThrowIfLessThan(start, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(end, start);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(end, lines.Count);

        return FileContentHash.Of(into =>
        {
            var joined = new JoinedLines(into);
            var piece = new char[DigestPiece];

            for (var line = start; line <= end; line++)
            {
                joined.Line();
                var (from, to) = lines.Span(text, line);

                for (var at = from; at < to;)
                {
                    var count = (int)Math.Min(piece.Length, to - at);
                    text.CopyTo(at, piece.AsSpan(0, count));
                    joined.Write(piece.AsSpan(0, count));
                    at += count;
                }
            }

            joined.End();
        });
    }

    /// <summary>How many characters of a line <see cref="Digest(CodeText, int, int)"/> reads at a time.</summary>
    private const int DigestPiece = 4096;

    /// <summary>
    /// Lines written into a stream as the UTF-8 bytes of their join by a line feed - what a digest hashes - through one
    /// encoder, so a line given in pieces is written as it would be whole and the bytes are those of the joined string's.
    /// </summary>
    private sealed class JoinedLines(Stream into)
    {
        private readonly Encoder _encoder = Encoding.UTF8.GetEncoder();
        private readonly byte[] _bytes = new byte[16 * 1024];
        private bool _begun;

        /// <summary>A line begins: the line feed that parts it from the one before, where there is one.</summary>
        public void Line()
        {
            if (_begun)
            {
                Write("\n");
            }

            _begun = true;
        }

        /// <summary>Characters of the line, in order.</summary>
        public void Write(ReadOnlySpan<char> characters) => Encode(characters, flush: false);

        /// <summary>The lines end: what the encoder holds is written.</summary>
        public void End() => Encode([], flush: true);

        private void Encode(ReadOnlySpan<char> characters, bool flush)
        {
            while (true)
            {
                _encoder.Convert(characters, _bytes, flush, out var used, out var written, out var completed);
                into.Write(_bytes, 0, written);
                characters = characters[used..];

                if (characters.IsEmpty && (!flush || completed))
                {
                    return;
                }
            }
        }
    }

    /// <summary>The SHA-256 of <paramref name="text"/>'s UTF-8 bytes, in lowercase hexadecimal, as every content hash of the tool is (<see cref="FileContentHash"/>).</summary>
    /// <param name="text">The text.</param>
    public static string Sha256(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return FileContentHash.Of(Encoding.UTF8.GetBytes(text));
    }

    /// <summary>
    /// The name, under a graph's <c>extracts</c> directory and with forward separators, of the extract of the file at
    /// <paramref name="path"/> whose content is <paramref name="blob"/>, made by <paramref name="language"/> at
    /// <paramref name="version"/>: <c>&lt;ab&gt;/&lt;blob&gt;-&lt;path&gt;.&lt;language&gt;-&lt;version&gt;.json</c>, <c>ab</c> the blob's first
    /// two characters and the path's part its whole SHA-256 - an implementation's answer may read the path, so a file's
    /// extract is its path's, and two paths of one blob never share an extract, however many files a tree holds. Every name
    /// is of one length for a blob, a language and a version, whatever the path, so the graphify step checks the longest a
    /// run makes against the platform's path budget before it syncs (<c>PathBudget</c>).
    /// </summary>
    /// <param name="blob">The blob id git gives the file's content.</param>
    /// <param name="path">The file's path, relative to the tree with forward separators.</param>
    /// <param name="language">The implementation that made it.</param>
    /// <param name="version">Its version.</param>
    public static string ExtractName(string blob, string path, string language, int version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(blob);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(language);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{blob[..2]}/{blob}-{Sha256(path)}.{language}-{version}{OrchestratorLayout.GraphExtractExtension}");
    }

    /// <summary>
    /// The name an earlier build of this tool gave the same extract, the path's part the first eight characters of its
    /// SHA-256 alone - which two paths of one blob could share: read so a manifest that build wrote still reads, its
    /// extracts served as they are until its next sync, which finds each name not the one <see cref="ExtractName"/> makes
    /// and reads the file again. Never written.
    /// </summary>
    /// <param name="blob">The blob id git gives the file's content.</param>
    /// <param name="path">The file's path, relative to the tree with forward separators.</param>
    /// <param name="language">The implementation that made it.</param>
    /// <param name="version">Its version.</param>
    public static string EarlierExtractName(string blob, string path, string language, int version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(blob);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(language);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{blob[..2]}/{blob}-{Sha256(path)[..8]}.{language}-{version}{OrchestratorLayout.GraphExtractExtension}");
    }
}
