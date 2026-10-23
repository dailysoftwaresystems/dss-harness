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

    /// <summary>A project file that names others: a <c>.csproj</c>, a <c>.slnx</c>, a Dart package's <c>pubspec.yaml</c>.</summary>
    public const string Project = "project";

    /// <summary>A namespace of a language that has them.</summary>
    public const string Namespace = "namespace";

    /// <summary>A declared type.</summary>
    public const string Type = "type";

    /// <summary>A declaration inside a type, or a function at file or namespace scope.</summary>
    public const string Member = "member";

    /// <summary>A Markdown heading and the lines under it, so one section can be served alone.</summary>
    public const string Section = "section";

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

    /// <summary>A declaration with no body to the one with a body and the same qualified name: derived when linking.</summary>
    public const string Declares = "declares";

    /// <summary>A test to a member it calls directly: derived when linking.</summary>
    public const string Tests = "tests";

    /// <summary>A project to a project it names.</summary>
    public const string ProjectReference = "projectReference";

    /// <summary>The kinds an implementation's references may ask for: what a reference to resolve can be, before linking makes it an edge.</summary>
    public static IReadOnlyList<string> Referenced { get; } = [Inherits, Implements, MixesIn, Overrides, Calls, Instantiates, References, ProjectReference];

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
        (Declares, "a declaration with no body to the one with a body"),
        (Tests, "a test to a member it calls directly"),
        (ProjectReference, "a project to a project it names"),
    ];

    /// <summary>Every kind an edge of a graph may be.</summary>
    public static IReadOnlyList<string> All { get; } = [.. Described.Select(described => described.Kind)];
}

/// <summary>How sure an edge is of its target.</summary>
public static class CodeConfidences
{
    /// <summary>The target is known from the code itself: a qualifier, or the scopes the reference is written in.</summary>
    public const string Exact = "exact";

    /// <summary>One declaration the reference can see has that name and arity.</summary>
    public const string Unique = "unique";

    /// <summary>
    /// Two to <see cref="MostCandidates"/> declarations have that name and arity: an edge to each, none picked. A name
    /// with more is counted unresolved.
    /// </summary>
    public const string Candidates = "candidates";

    /// <summary>The most declarations a reference is made an edge to each of; past it, it is counted unresolved.</summary>
    public const int MostCandidates = 5;

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

    /// <summary>Larger than <c>codebaseGraph.maxFileBytes</c>.</summary>
    public const string TooLarge = "tooLarge";

    /// <summary>
    /// Not read, with why: neither UTF-8 nor UTF-16 by its mark; named in no UTF-8; gone, or changed, between its listing and
    /// the end of its read; an I/O error; a content test that threw; or an implementation that threw on it or answered no
    /// sound extract - a defect of that implementation, which <c>failOn: incomplete</c> fails on too.
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

        return Sha256(string.Join('\n', Enumerable.Range(start - 1, end - start + 1).Select(at => lines[at])));
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
    /// two characters and the path's part the first eight hexadecimal characters of its SHA-256 - an implementation's
    /// answer may read the path, so a file's extract is its path's.
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
            $"{blob[..2]}/{blob}-{Sha256(path)[..8]}.{language}-{version}{OrchestratorLayout.GraphExtractExtension}");
    }
}
