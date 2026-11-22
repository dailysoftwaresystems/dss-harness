using System.Globalization;
using System.Text.Json.Serialization;
using DssHarness.Core.Orchestration;

namespace DssHarness.Core.Codebase;

/// <summary>
/// What an implementation of <see cref="ICodeGraphExtractor"/> makes of one file: language-neutral, the model of the graph
/// and nothing of any language - its declarations, the scopes they are put in, what the file imports, what an address
/// means, the references it makes by name, unresolved, and five marks - a prototype, a test, a part of a declaration
/// another file holds the rest of, generated code and what it cannot bind - of which the linker reads the first three,
/// without knowing why they were set.
/// </summary>
/// <remarks>
/// The linker resolves every reference by the same steps for every language - the qualifier's scope, the enclosing
/// scopes from the inside out, the imports in order, the repository by name and arity (<see cref="CodeGraphLinker"/>) -
/// so a language says in its extract what its scopes and imports are, and never adds a rule to the linker.
/// <para>
/// Its record (<see cref="FileExtractJson"/>) writes each node's names against its parent's and every node a field names
/// by its place, so what is kept of a file nested any depth grows with the file, never with the square of its depth; read
/// back, it is the extract written, each name held once.
/// </para>
/// </remarks>
/// <param name="Nodes">Its declarations; the file's own node is the core's, and is not among them.</param>
[JsonConverter(typeof(FileExtractJson))]
public sealed record FileExtract(IReadOnlyList<ExtractNode> Nodes)
{
    /// <summary>An extract that holds nothing: what the fallback gives every file it lists, whose declarations nothing reads.</summary>
    public static FileExtract Empty { get; } = new([]);

    /// <summary>The named containers its declarations are put in, each by its qualified name.</summary>
    public IReadOnlyList<ExtractScope> Scopes { get; init; } = [];

    /// <summary>What the file makes visible, in the order it imports it: the order the linker looks in.</summary>
    public IReadOnlyList<ExtractImport> Imports { get; init; } = [];

    /// <summary>What an address prefix means, for every file's imports by address: declared by a project file's implementation.</summary>
    public IReadOnlyList<ExtractAddressRoot> AddressRoots { get; init; } = [];

    /// <summary>Every reference the file makes, by name, for the linker to resolve.</summary>
    public IReadOnlyList<ExtractReference> References { get; init; } = [];

    /// <summary>
    /// The scope the path of the file's directory gave what it declares, where the file declares none of its own and its
    /// language puts it in the one a directory above it names for every file under it (<see cref="ExtractImport.Namespace"/>)
    /// - a Blazor component with no <c>@namespace</c>, which Razor compiles into the nearest <c>_Imports.razor</c>'s, each
    /// directory past that one's a name after it. Its names as the dots part them, one a directory, each from the tree's
    /// root; empty for a file at the root. The linker reads every name that scope begins - the scope, what is declared in it
    /// and the scopes it is in - in the one that directory gives instead, its ids kept; where no directory above names one,
    /// as it is. <see langword="null"/> where the file's scopes are its own.
    /// </summary>
    public string? FolderScope { get; init; }

    /// <summary>
    /// Why the file is generated code, where the implementation knows a generator wrote it; <see langword="null"/> where
    /// it is not. Its declarations are then kept, its file's entry naming why it is generated, or left out under
    /// <c>codebaseGraph.generated: exclude</c>.
    /// </summary>
    public string? Generated { get; init; }

    /// <summary>
    /// Each place the implementation saw something it cannot bind - a call a macro writes, a member named by a string, the
    /// point a file stops parsing, past which nothing is read - with why.
    /// </summary>
    public IReadOnlyList<ExtractDynamic> Dynamic { get; init; } = [];

    /// <summary>
    /// The string literals the file holds, each value once however many times the file writes it, with every place it is
    /// written - its line and the node whose code holds it: the values the data of the code is written in - what the
    /// keys' join reads, a literal naming a configuration key - interpolated and template strings giving their constant
    /// parts. Every one that holds a character is kept, however many the file holds and however short, the values in the
    /// order the file first writes each; <see langword="null"/> where the file holds none.
    /// </summary>
    public IReadOnlyList<ExtractLiteral>? Literals { get; init; }

    /// <summary>
    /// The names an import of the file that takes what it publishes alone makes visible (<see cref="ExtractImport.Published"/>):
    /// what the file publishes to such an import - a Python module's <c>__all__</c> where it is a list of string literals, else
    /// every name bound at its top that is not written with a leading underscore, as the implementation says. <see langword="null"/>
    /// where nothing of the file's language imports it so.
    /// </summary>
    public IReadOnlyList<string>? Published { get; init; }

    /// <summary>
    /// Whether a name the file's code reads that none of its scopes declares and none of its imports makes visible is bound by
    /// nothing: the linker then does not look for it among the repository's declarations, as a Python module's name is bound by
    /// its module's own names and its imports or not at all. False where a name bound by neither may still be one any file declares.
    /// </summary>
    public bool BoundWithin { get; init; }

    /// <summary>
    /// Whether the file is the one that says its directory is one unit of its language's layout - a Python package's
    /// <c>__init__.py</c>. An import marked <see cref="ExtractImport.Layout"/> is passed over where it names a place in a unit
    /// around its file, so a bare name is looked for from the first directory above the units the file is in, as a Python import
    /// is from above the file's package.
    /// </summary>
    public bool FolderUnit { get; init; }

    /// <summary>
    /// What is wrong with this extract of a file of <paramref name="lineCount"/> lines made by
    /// <paramref name="language"/>; <see langword="null"/> where nothing is. The shape alone: a node's digest is checked
    /// against the file's lines where the lines are at hand.
    /// </summary>
    /// <param name="language">The implementation that made it, whose name prefixes every id.</param>
    /// <param name="lineCount">How many lines the file has (<see cref="CodeFileText.Lines"/>).</param>
    public string? Problem(string language, int lineCount)
    {
        var ids = new Dictionary<QualifiedName, ExtractNode>();

        foreach (var node in Nodes ?? [])
        {
            if (node is null)
            {
                return "it holds a node that is null";
            }

            if (node.Problem(language, lineCount) is { } wrong)
            {
                return $"its node '{node.Id}': {wrong}";
            }

            if (!ids.TryAdd(node.Id, node))
            {
                return $"two of its nodes are '{node.Id}': a node's id is unique in its extract";
            }
        }

        foreach (var node in Nodes ?? [])
        {
            if (node.Parent is { } parent)
            {
                if (!ids.TryGetValue(parent, out var holder))
                {
                    return $"its node '{node.Id}' is declared inside '{parent}', which it holds no node of";
                }

                if (holder.Start > node.Start || holder.End < node.End)
                {
                    return $"its node '{node.Id}' (lines {Lines(node)}) is declared inside '{parent}' (lines {Lines(holder)}), whose lines do not hold it";
                }
            }
        }

        // Two nodes of one span may each say the other holds it, and one may name itself: what holds what is a tree, which
        // whatever walks it up - the type a reference is made in - ends at its root. A node found to end there is never
        // walked up again, so the whole tree is walked once, however deep it nests.
        var rooted = new HashSet<QualifiedName>();

        foreach (var node in Nodes ?? [])
        {
            var seen = new List<QualifiedName> { node.Id };
            var walked = new HashSet<QualifiedName> { node.Id };

            for (var parent = node.Parent; parent is not null && !rooted.Contains(parent); parent = ids[parent].Parent)
            {
                if (!walked.Add(parent))
                {
                    return $"its node '{node.Id}' is declared inside itself, through '{parent}'";
                }

                seen.Add(parent);
            }

            rooted.UnionWith(seen);
        }

        if (SuffixProblem(ids) is { } suffixed)
        {
            return suffixed;
        }

        var scopes = new HashSet<QualifiedName>();

        foreach (var scope in Scopes ?? [])
        {
            if (scope is null || scope.Name.WhitespaceOnly())
            {
                return "it holds a scope with no name";
            }

            if (!scopes.Add(scope.Name))
            {
                return $"it declares the scope '{scope.Name}' twice";
            }
        }

        return (Imports ?? []).Select(import => import?.Problem(lineCount) is { } wrong ? $"an import of '{import.Target}': {wrong}" : import is null ? "it holds an import that is null" : null).FirstOrDefault(found => found is not null)
            ?? (Imports ?? []).Select((import, at) => import.Fallback && (at == 0 || Imports![at - 1].Form == CodeImportForms.Scope)
                ? $"its import of '{import.Target}' stands in for the import before it, and no import of a file is before it" : null).FirstOrDefault(found => found is not null)
            ?? (AddressRoots ?? []).Select(root => root is null || string.IsNullOrWhiteSpace(root.Prefix) || string.IsNullOrWhiteSpace(root.Directory) ? "it holds an address root with no prefix or no directory" : null).FirstOrDefault(found => found is not null)
            ?? (References ?? []).Select(reference => reference is null ? "it holds a reference that is null"
                : !ids.ContainsKey(reference.From) ? $"a reference to '{reference.Name}' is made from '{reference.From}', which it holds no node of"
                : reference.Problem(lineCount) is { } wrong ? $"a reference to '{reference.Name}': {wrong}"
                : reference.Qualifier is { Kind: CodeQualifierKinds.Own, Name: { } own } && !ids.ContainsKey(own) ? $"a reference to '{reference.Name}' names the node '{own}' of its own file, which it holds no node of"
                : null).FirstOrDefault(found => found is not null)
            ?? (FolderScope is { Length: > 0 } folder && folder.Split('.').Any(string.IsNullOrWhiteSpace) ? $"the scope its directory gives it, '{folder}', holds a name that is empty" : null)
            ?? (Generated is { } generated && string.IsNullOrWhiteSpace(generated) ? "it says it is generated and not why" : null)
            ?? (Dynamic ?? []).Select(dynamic => dynamic is null || string.IsNullOrWhiteSpace(dynamic.Why) ? "it marks a line dynamic and not why"
                : !Within(dynamic.Line, lineCount) ? $"it marks line {dynamic.Line.ToString(CultureInfo.InvariantCulture)} dynamic, outside its {lineCount.ToString(CultureInfo.InvariantCulture)} lines"
                : null).FirstOrDefault(found => found is not null)
            ?? LinkProblem()
            ?? LiteralProblem(ids, lineCount)
            ?? (Texts().FirstOrDefault(text => !IsWhole(text)) is { } cut ? $"it holds text that is no whole UTF-16, a surrogate cut from its pair: '{cut}'" : null);
    }

    /// <summary>
    /// The names <paramref name="qualifier"/>, the qualifier of one of this extract's references, writes, joined by dots:
    /// its own name, or - where it is a chain written as links (<see cref="ExtractQualifier.Link"/>) - the names of each
    /// link's qualifier and the link's own name, from the chain's first to its last, its links followed back from the last
    /// on a loop of their own; <see langword="null"/> where it writes none. The one reading of a chain's names.
    /// </summary>
    /// <param name="qualifier">A qualifier of one of <see cref="References"/>, or none.</param>
    public string? Written(ExtractQualifier? qualifier)
    {
        if (qualifier?.Link is not { } link)
        {
            return qualifier?.Name?.ToString();
        }

        var names = new List<string>();
        var references = References ?? [];

        // Each link's name, last first. Every link names a reference made before its own, so the walk ends; one that does
        // not - which Problem refuses - ends it there.
        for (var at = link; at >= 0 && at < references.Count;)
        {
            names.Add(references[at].Name);

            if (references[at].Qualifier is { Link: { } next } && next < at)
            {
                at = next;
                continue;
            }

            if (references[at].Qualifier is { Link: null, Name: { } first })
            {
                names.Add(first.ToString());
            }

            break;
        }

        names.Reverse();
        return string.Join('.', names);
    }

    /// <summary>
    /// What is wrong with the chains its references write as links (<see cref="ExtractQualifier.Link"/>); none where
    /// nothing is. A link names a reference made before its own, so following links always ends; a linked qualifier is a
    /// chain's - of a value or a name - and names nothing itself; and the reference it links writes names of its own or
    /// links on, so the chain's names are read whole.
    /// </summary>
    private string? LinkProblem()
    {
        var references = References ?? [];

        for (var at = 0; at < references.Count; at++)
        {
            if (references[at]?.Qualifier is not { Link: { } link } qualifier)
            {
                continue;
            }

            var named = $"a reference to '{references[at].Name}'";

            if (link < 0 || link >= at)
            {
                return $"{named} links its qualifier to reference {link.ToString(CultureInfo.InvariantCulture)}, which is not one made before it";
            }

            if (qualifier.Kind is not (CodeQualifierKinds.Value or CodeQualifierKinds.Named))
            {
                return $"{named} links its qualifier of kind '{qualifier.Kind}', and only a chain of a value or of a name is written as links";
            }

            if (qualifier.Name is not null)
            {
                return $"{named} links its qualifier and names '{qualifier.Name}' too: a linked chain's names are its links'";
            }

            if (references[link]?.Qualifier is not ({ Link: not null } or { Name: not null }))
            {
                return $"{named} links its qualifier to the reference to '{references[link]?.Name}', whose qualifier writes no name";
            }
        }

        return null;
    }

    /// <summary>What is wrong with the literals it holds: each value once, holding a character, each place on one of its lines and in one of its nodes.</summary>
    private string? LiteralProblem(Dictionary<QualifiedName, ExtractNode> ids, int lineCount)
    {
        var values = new HashSet<string>(StringComparer.Ordinal);

        foreach (var literal in Literals ?? [])
        {
            if (literal is null)
            {
                return "it holds a literal that is null";
            }

            if (string.IsNullOrEmpty(literal.Value))
            {
                return "it holds a literal of no value, and only a literal that holds a character is kept";
            }

            if (!values.Add(literal.Value))
            {
                return $"it holds the literal '{literal.Value}' twice: a value is kept once, with every place it is written";
            }

            if (literal.Places is not { Count: > 0 } places)
            {
                return $"its literal '{literal.Value}' is written nowhere";
            }

            foreach (var place in places)
            {
                if (place is null)
                {
                    return $"its literal '{literal.Value}' holds a place that is null";
                }

                if (!Within(place.Line, lineCount))
                {
                    return $"its literal '{literal.Value}' is on line {place.Line.ToString(CultureInfo.InvariantCulture)}, outside its {lineCount.ToString(CultureInfo.InvariantCulture)} lines";
                }

                if (place.Node is { } node && !ids.ContainsKey(node))
                {
                    return $"its literal '{literal.Value}' is held by '{node}', which it holds no node of";
                }
            }
        }

        return null;
    }

    /// <summary>
    /// This extract with every node's digest set from <paramref name="text"/>, the file's text it was made from: what the
    /// core does once an implementation has answered, and nothing else does - a digest an implementation set is never
    /// kept. Each is read from the text by position (<see cref="CodeGraphIds.Digest(CodeText, int, int)"/>), so a node over
    /// a whole file of any size is digested without its lines made strings. A node whose lines are not the file's is left
    /// without one, for <see cref="Problem"/> to refuse.
    /// </summary>
    /// <param name="text">The file's text.</param>
    public FileExtract WithDigests(CodeText text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var count = text.LineIndex.Count;

        return this with
        {
            Nodes = [.. (Nodes ?? []).Select(node => node is null ? node! : node with
            {
                Digest = node.Start >= 1 && node.End >= node.Start && node.End <= count ? CodeGraphIds.Digest(text, node.Start, node.End) : null,
            })],
        };
    }

    /// <summary>
    /// This extract with every field that names a line given the line <paramref name="line"/> gives - each node's first
    /// and last lines and the line its body begins on, and the line of each import, reference, dynamic mark and literal -
    /// the one way an extract's lines are moved: where a markup file's reader gives embedded code a text of its own whose
    /// lines are not the host's, each written where its pieces need, and says on which of the host's lines each stands.
    /// </summary>
    /// <param name="line">The line each line is given.</param>
    internal FileExtract Relined(Func<int, int> line)
    {
        ArgumentNullException.ThrowIfNull(line);
        return this with
        {
            Nodes = [.. Nodes.Select(node => node with { Start = line(node.Start), End = line(node.End), BodyStart = node.BodyStart is { } body ? line(body) : null })],
            Imports = [.. Imports.Select(import => import with { Line = line(import.Line) })],
            References = [.. References.Select(reference => reference with { Line = line(reference.Line) })],
            Dynamic = [.. Dynamic.Select(dynamic => dynamic with { Line = line(dynamic.Line) })],
            Literals = Literals is null ? null : [.. Literals.Select(literal => literal with { Places = [.. literal.Places.Select(place => place with { Line = line(place.Line) })] })],
        };
    }

    /// <summary>
    /// This extract with every field that names one of its nodes given the id <paramref name="renamed"/> gives - each
    /// node's id, the id of the node it is declared inside and of each node its name is written onto
    /// (<see cref="ExtractNode.SuffixOf"/>), the node each reference is made from and the node of its own file it names
    /// (<see cref="CodeQualifierKinds.Own"/>), and the node each literal is held by - so no field is left naming a node by
    /// an id the extract no longer gives it: the one way an
    /// extract's ids are moved, where a reader gives each node its id once the file is read, where a markup file's reader
    /// tells apart the ids of the code it embeds, and where one node is folded into another.
    /// </summary>
    /// <param name="renamed">The id each id is given: itself, where it is kept.</param>
    internal FileExtract Renamed(Func<QualifiedName, QualifiedName> renamed)
    {
        ArgumentNullException.ThrowIfNull(renamed);

        return this with
        {
            Nodes = [.. Nodes.Select(node => node with
            {
                Id = renamed(node.Id),
                Parent = node.Parent is { } parent ? renamed(parent) : null,
                SuffixOf = node.SuffixOf is { } onto ? [.. onto.Select(renamed)] : null,
            })],
            References = [.. References.Select(reference => reference with
            {
                From = renamed(reference.From),
                Qualifier = reference.Qualifier is { Kind: CodeQualifierKinds.Own, Name: { } own } named ? named with { Name = renamed(own) } : reference.Qualifier,
            })],
            Literals = Literals is null ? null : [.. Literals.Select(literal => literal with { Places = [.. literal.Places.Select(place => place.Node is { } node ? place with { Node = renamed(node) } : place)] })],
        };
    }

    /// <summary>Whether <paramref name="line"/> is one of a file's <paramref name="lineCount"/> lines.</summary>
    internal static bool Within(int line, int lineCount) => line >= 1 && line <= lineCount;

    /// <summary>
    /// The line of a file of <paramref name="lineCount"/> lines nearest <paramref name="line"/>: what a reader's position
    /// before the file's first line or past its last is said as, so every line an extract names is the file's.
    /// </summary>
    /// <param name="line">A line a reader names, from 1.</param>
    /// <param name="lineCount">The file's lines.</param>
    public static int LineWithin(int line, int lineCount) => Math.Clamp(line, 1, Math.Max(lineCount, 1));

    /// <summary>
    /// Whether <paramref name="text"/> is whole UTF-16 - no surrogate apart from its pair - as a record is written in and
    /// read back: a cut that splits a pair is no text a store holds.
    /// </summary>
    internal static bool IsWhole(string text) => Apart(text, 0) < 0;

    /// <summary>The index of the first surrogate apart from its pair in <paramref name="text"/> from <paramref name="from"/> on; -1 where none is.</summary>
    internal static int Apart(string text, int from)
    {
        for (var at = from; at < text.Length; at++)
        {
            if (char.IsHighSurrogate(text[at]) && at + 1 < text.Length && char.IsLowSurrogate(text[at + 1]))
            {
                at++;
            }
            else if (char.IsSurrogate(text[at]))
            {
                return at;
            }
        }

        return -1;
    }

    /// <summary>
    /// What is wrong with the nodes whose names are written onto others' (<see cref="ExtractNode.SuffixOf"/>): each must
    /// name nodes of <paramref name="ids"/>, and none its own names in turn - what they stand for is read back to names of
    /// their own, which a node written onto itself never reaches. Each node is walked once, from the first that names it.
    /// </summary>
    private static string? SuffixProblem(Dictionary<QualifiedName, ExtractNode> ids)
    {
        // 1 while a node's own are being walked, 2 once they are done.
        var walked = new Dictionary<QualifiedName, int>();

        foreach (var start in ids.Values.Where(node => node.SuffixOf is not null))
        {
            if (walked.ContainsKey(start.Id))
            {
                continue;
            }

            var path = new Stack<(ExtractNode Node, int Next)>();
            path.Push((start, 0));
            walked[start.Id] = 1;

            while (path.TryPop(out var at))
            {
                var onto = at.Node.SuffixOf ?? [];

                if (at.Next == onto.Count)
                {
                    walked[at.Node.Id] = 2;
                    continue;
                }

                path.Push(at with { Next = at.Next + 1 });
                var id = onto[at.Next];

                if (!ids.TryGetValue(id, out var under))
                {
                    return $"its node '{at.Node.Id}' stands for names written onto '{id}', which it holds no node of";
                }

                switch (walked.GetValueOrDefault(id))
                {
                    case 1:
                        return $"its node '{at.Node.Id}' stands for names written onto its own, through '{id}'";

                    case 0:
                        walked[id] = 1;
                        path.Push((under, 0));
                        break;
                }
            }
        }

        return null;
    }

    /// <summary>Every text the extract holds, each of which a store writes.</summary>
    private IEnumerable<string> Texts()
        => (Nodes ?? []).SelectMany(node => (string?[])[node.Id.ToString(), node.Kind, node.Name, node.Qualified.ToString(), node.Signature, node.SubKind, node.Scope?.ToString(), node.Parent?.ToString(), node.Summary, node.ValueType, node.Extends, node.SeenIn?.ToString(), .. node.Modifiers ?? [], .. node.Exports ?? [], .. (node.SuffixOf ?? []).Select(id => id.ToString()), .. node.Selectors ?? [], .. node.Templates ?? []])
            .Concat((Scopes ?? []).SelectMany(scope => (string?[])[scope.Name.ToString(), scope.Parent?.ToString()]))
            .Concat((Imports ?? []).SelectMany(import => (string?[])[import.Form, import.Target, import.Prefix, .. import.Show ?? [], .. import.Hide ?? []]))
            .Concat((AddressRoots ?? []).SelectMany(root => (string?[])[root.Prefix, root.Directory]))
            .Concat((References ?? []).SelectMany(reference => (string?[])[reference.From.ToString(), reference.Kind, reference.Name, reference.Qualifier?.Kind, reference.Qualifier?.Name?.ToString()]))
            .Concat((Dynamic ?? []).Select(dynamic => dynamic.Why))
            .Concat((Literals ?? []).SelectMany(literal => (string?[])[literal.Value, .. (literal.Places ?? []).Select(place => place?.Node?.ToString())]))
            .Append(FolderScope)
            .Append(Generated)
            .OfType<string>();

    private static string Lines(ExtractNode node)
        => string.Create(CultureInfo.InvariantCulture, $"{node.Start}-{node.End}");
}

/// <summary>One declaration of a file's extract.</summary>
/// <param name="Id">
/// Its id: the implementation's <see cref="ICodeGraphExtractor.Language"/>, a colon and its qualified name, with its
/// parameters' types where it takes some - stable across syncs while the declaration keeps its name and parameters. A
/// name (<see cref="QualifiedName"/>) held as the one before it and its own part, so a declaration nested any depth
/// holds an id that grows with its own part and not with the square of its depth.
/// </param>
/// <param name="Kind">One of <see cref="CodeNodeKinds.Extracted"/>.</param>
/// <param name="Name">Its simple name, as a reference names it.</param>
/// <param name="Qualified">
/// Its name with every scope it is declared in, as a qualified reference names it - held, too, as the name before its
/// own part, the whole name made again where a text is asked for (<see cref="QualifiedName.Whole"/>).
/// </param>
/// <param name="Start">Its first line, from 1.</param>
/// <param name="End">Its last line.</param>
/// <param name="Signature">Its header on one line, whole however long it is: what serves it gives a wide one in pieces.</param>
public sealed record ExtractNode(QualifiedName Id, string Kind, string Name, QualifiedName Qualified, int Start, int End, string Signature)
{
    /// <summary>
    /// The kind within its kind, which the implementation names freely - a <c>class</c>, a <c>method</c> - but for the
    /// kinds of data the linker reads (<see cref="CodeSubKinds"/>): a configuration key, an element, a style rule.
    /// </summary>
    public string? SubKind { get; init; }

    /// <summary>
    /// The scope it is declared in, by its qualified name; <see langword="null"/> where it is the repository's own. A
    /// name held as the one before its own part, so a scope nested any depth is held once by each of its names.
    /// </summary>
    public QualifiedName? Scope { get; init; }

    /// <summary>The id of the node of this extract it is declared inside; <see langword="null"/> where it is the file's.</summary>
    public QualifiedName? Parent { get; init; }

    /// <summary>The line its body begins on, after its signature, where it has one.</summary>
    public int? BodyStart { get; init; }

    /// <summary>What the code says of itself: the first sentence of its documentation on one line, whole however long it runs.</summary>
    public string? Summary { get; init; }

    /// <summary>Its modifiers, as the implementation names them.</summary>
    public IReadOnlyList<string> Modifiers { get; init; } = [];

    /// <summary>The fewest arguments a call of it takes; <see langword="null"/> where it takes any number, or is called by none.</summary>
    public int? MinArity { get; init; }

    /// <summary>The most arguments a call of it takes; <see langword="null"/> beside <see cref="MinArity"/> where there is no most.</summary>
    public int? MaxArity { get; init; }

    /// <summary>Whether it is a declaration with no body - a prototype - which linking binds to the one with a body.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Prototype { get; init; }

    /// <summary>Whether it is a test, which linking gives a <c>tests</c> edge to each member it calls.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Test { get; init; }

    /// <summary>
    /// Whether it says it is one part of a declaration another file holds the rest of - a XAML view whose
    /// <c>x:Class</c> names a C# partial type, a Blazor component and its code-behind - which the linker joins as
    /// <see cref="CodeEdgeKinds.PartOf"/> to every other part of the same qualified name that says so.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Part { get; init; }

    /// <summary>
    /// The type a value of it is declared with - a field's, a property's, an event's - as a type qualifier names it, where
    /// the code says one: what a call on that value is looked for through.
    /// </summary>
    public string? ValueType { get; init; }

    /// <summary>
    /// The type it is declared to be called on though that type does not declare it - an extension method's receiver - as a
    /// type qualifier names it from its own file: a call on a value of that type, or of a type built on it, finds it where
    /// the call's file reaches it.
    /// </summary>
    public string? Extends { get; init; }

    /// <summary>
    /// The scope whose declarations alone see it, by its qualified name, where it is kept to them: a declaration written in
    /// that scope, or in a scope inside it, sees it, and one anywhere else does not - a Dart library's private name, which
    /// the library's file and its parts see and no importer does. <see langword="null"/> where every declaration that
    /// reaches it sees it.
    /// </summary>
    public QualifiedName? SeenIn { get; init; }

    /// <summary>
    /// The names another file imports it by besides its own, where it is exported so - a JavaScript module's
    /// <c>default</c>, the name an <c>export { a as b }</c> gives it: an import of its file - or of a file that exports
    /// that file's names - finds it by one of them whatever scope <see cref="SeenIn"/> keeps it to, and nothing else finds
    /// it by one: no reference written in its own file, and none in the repository by name; <see langword="null"/> where it
    /// is exported by none.
    /// </summary>
    public IReadOnlyList<string>? Exports { get; init; }

    /// <summary>
    /// The ids of the nodes of its extract whose names its own are written onto, where it stands for one name written onto
    /// each of several - a style sheet's nested <c>&amp;-x</c> under a list of selectors: its <see cref="Name"/> is
    /// <c>&amp;</c> and the suffix it writes, and the names it stands for are each name of each of those nodes - their own,
    /// or in turn the ones they stand for - with that suffix appended, matched when linking and never written out, so a list
    /// nested in a list nested in a list is as many nodes as the sheet writes, not the product of every list. Where it is
    /// exported, each of its <see cref="Exports"/> is <c>&amp;</c> and a suffix too: the first appended to the first name
    /// each of those nodes is exported by, the second to the second, and the last of either standing for those past it.
    /// <see langword="null"/> where it is a name of its own.
    /// </summary>
    public IReadOnlyList<QualifiedName>? SuffixOf { get; init; }

    /// <summary>
    /// The element selectors a component declares itself by, as Angular's <c>@Component</c> writes them -
    /// <c>app-cart</c> - each the name of an element of markup, so a tag markup writes (<c>&lt;app-cart&gt;</c>)
    /// instantiates the component whose selector it is: a name the repository finds the declaration by, as its own
    /// name, from markup alone - for no code's name holds a dash, nothing code writes reaches one. Only a selector
    /// that names an element alone is kept; a class's, an attribute's or a combinator's names none.
    /// <see langword="null"/> where it declares none.
    /// </summary>
    public IReadOnlyList<string>? Selectors { get; init; }

    /// <summary>
    /// The files that are a component's templates, each a path relative to its own file's directory as Angular's
    /// <c>templateUrl</c> names one: what a template's code reads by itself is a member of this declaration, as
    /// <c>this</c> reads one, wherever the template names no type of its own - the linker finds each such name on
    /// the declarations that name the template theirs (<see cref="CodeQualifierKinds.This"/>). <see langword="null"/>
    /// where it has none.
    /// </summary>
    public IReadOnlyList<string>? Templates { get; init; }

    /// <summary>
    /// The SHA-256 of its lines (<see cref="CodeGraphIds.Digest(CodeText, int, int)"/>): set by the core once the implementation has answered,
    /// never by it, and what says whether the bytes served are the ones extracted.
    /// </summary>
    public string? Digest { get; init; }

    /// <summary>Whether a call of <paramref name="arity"/> arguments may call it.</summary>
    /// <param name="arity">How many arguments the call gives; <see langword="null"/> where that is not known.</param>
    public bool TakesArity(int? arity)
        => arity is not { } given || MinArity is not { } least || (given >= least && (MaxArity is not { } most || given <= most));

    /// <summary>What is wrong with this node of an extract of <paramref name="lineCount"/> lines; <see langword="null"/> where nothing is.</summary>
    internal string? Problem(string language, int lineCount)
        => Id.WhitespaceOnly() ? "it has no id"
            : !Id.StartsWith(language + ":") ? $"its id does not begin '{language}:', its implementation's name"
            : !CodeNodeKinds.Extracted.Contains(Kind, StringComparer.Ordinal) ? $"its kind is '{Kind}', which is none of {string.Join(", ", CodeNodeKinds.Extracted)}"
            : string.IsNullOrWhiteSpace(Name) || Qualified.WhitespaceOnly() ? "it has no name"
            : Name.AsSpan().IndexOfAny('\r', '\n') >= 0 || Qualified.Whole().AsSpan().IndexOfAny('\r', '\n') >= 0 ? "its name is not one line"
            : !FileExtract.Within(Start, lineCount) || End < Start || End > lineCount ? string.Create(CultureInfo.InvariantCulture, $"its lines {Start}-{End} are not lines of the file's {lineCount}")
            : BodyStart is { } body && (body < Start || body > End) ? string.Create(CultureInfo.InvariantCulture, $"its body begins on line {body}, outside its lines {Start}-{End}")
            : Signature is null || Signature.AsSpan().IndexOfAny('\r', '\n') >= 0 ? "its signature is not one line"
            : Summary is { } summary && summary.AsSpan().IndexOfAny('\r', '\n') >= 0 ? "its summary is not one line"
            : MinArity is < 0 || (MaxArity is { } most && (MinArity is not { } least || most < least)) ? "its arity is no range of argument counts"
            : ValueType is { } valueType && string.IsNullOrWhiteSpace(valueType) ? "its value's type is empty"
            : Extends is { } extends && string.IsNullOrWhiteSpace(extends) ? "the type it extends is empty"
            : SeenIn is { } seenIn && seenIn.WhitespaceOnly() ? "the scope it is seen in is empty"
            : Exports is { } exports && (exports.Count == 0 || exports.Any(string.IsNullOrWhiteSpace)) ? "a name it is exported by is empty, or it lists none"
            : SuffixOf is { } suffixOf && (suffixOf.Count == 0 || suffixOf.Any(id => string.IsNullOrWhiteSpace(id.Whole()))) ? "a node its names are written onto is empty, or it names none"
            : SuffixOf is not null && Name is not ['&', _, ..] ? "it stands for names written onto others', and its name is no & and the suffix it writes"
            : SuffixOf is not null && Exports is { } suffixes && suffixes.Any(exported => exported is not ['&', ..]) ? "it stands for names written onto others', and a name it is exported by is no & and a suffix"
            : Selectors is { } selectors && (selectors.Count == 0 || selectors.Any(string.IsNullOrWhiteSpace)) ? "an element selector it declares is empty, or it declares none"
            : Templates is { } templates && (templates.Count == 0 || templates.Any(string.IsNullOrWhiteSpace)) ? "a template it names is empty, or it names none"
            : Digest is { } digest && !OrchestrationRules.IsDigest(digest) ? "its digest is no SHA-256"
            : null;
}

/// <summary>One named container a file's declarations are put in.</summary>
/// <param name="Name">
/// Its qualified name, held as the name before its own part (<see cref="QualifiedName"/>), so a scope nested any depth
/// is held once by each of its names and never whole by each.
/// </param>
/// <param name="Open">
/// Whether other files contribute to it - a namespace, a library and its parts - so that its name is one scope for every
/// file that declares it; a closed scope is its file's alone, and what is declared in it - and the type or scope it is
/// the scope of - is seen from no other file.
/// </param>
public sealed record ExtractScope(QualifiedName Name, bool Open)
{
    /// <summary>The scope it is declared in, by its qualified name, which the linker looks in next; <see langword="null"/> where none is.</summary>
    public QualifiedName? Parent { get; init; }
}

/// <summary>What a file imports: how the linker finds what it makes visible.</summary>
/// <param name="Form">How <paramref name="Target"/> is read: one of <see cref="CodeImportForms"/>.</param>
/// <param name="Target">A scope's qualified name, a path relative to the file, an address, or a path's ending.</param>
/// <param name="Line">The line it is written on.</param>
public sealed record ExtractImport(string Form, string Target, int Line)
{
    /// <summary>
    /// The name the import is reached by, where it has one (<c>as p</c>, an alias): its names are then seen only through it,
    /// and an import of a scope by it is that scope wherever its prefix is written as a type's or a scope's name.
    /// </summary>
    public string? Prefix { get; init; }

    /// <summary>The only names it makes visible, where it names some (<c>show</c>).</summary>
    public IReadOnlyList<string>? Show { get; init; }

    /// <summary>Names it does not make visible (<c>hide</c>).</summary>
    public IReadOnlyList<string>? Hide { get; init; }

    /// <summary>Whether it is made by every file of the implementation's language in the repository, as a C# <c>global using</c> is.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Global { get; init; }

    /// <summary>
    /// Whether it stands in for the import before it, of a file: it is taken only where that one - and each that one
    /// stands in for in turn - names no file of the tree, so a run of imports is one import, the first of them that names a
    /// file - as <c>#include "x.h"</c> looks beside its file, then anywhere in the tree.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Fallback { get; init; }

    /// <summary>
    /// Whether it is an export: what the file it names makes visible - its filters applied - is made visible to every file
    /// that imports this one, and not to this file itself, as Dart's <c>export</c> and JavaScript's <c>export ... from</c>.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Export { get; init; }

    /// <summary>
    /// Whether the names it binds in the file that makes it are that file's importers's too, and not only its own: a module's
    /// own imported names are names of the module, so an importer of the module reaches them through it - <c>from pkg import
    /// a</c> reaches what a package's <c>__init__.py</c> binds by <c>from .x import a</c>, by the name it binds it under.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Reexports { get; init; }

    /// <summary>
    /// Whether it makes visible the names the file it names publishes alone (<see cref="FileExtract.Published"/>), and none of
    /// its others: an import that takes every name the module gives, as a Python <c>from m import *</c> does.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Published { get; init; }

    /// <summary>
    /// Whether it names one place a bare name of the file's language is looked for - in the file's own directory or in one above
    /// it, each further place standing in for the one before it - and is passed over where that directory is a unit of the layout
    /// around the file (<see cref="FileExtract.FolderUnit"/>): the first directory above the units the file is in is tried first.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Layout { get; init; }

    /// <summary>
    /// Whether the file it names shares its imports with this one: what that file imports - each import's filters and
    /// prefix with it - this file imports too, after its own, as a Dart part does its library's. What the file it names
    /// shares in turn is not followed.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Shares { get; init; }

    /// <summary>
    /// Whether it is, besides an import of a directory's every file, the scope those files are declared in where one
    /// declares none of its own (<see cref="FileExtract.FolderScope"/>) - a Blazor <c>_Imports.razor</c>'s
    /// <c>@namespace</c> - each directory between a file and this one's a name after it, as Razor names one. The nearest
    /// directory's decides, and the first of a file's.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Namespace { get; init; }

    /// <summary>Whether the import makes <paramref name="name"/> visible, by its filters.</summary>
    /// <param name="name">A simple name.</param>
    public bool Lets(string name)
        => (Show is null || Show.Contains(name, StringComparer.Ordinal)) && (Hide is null || !Hide.Contains(name, StringComparer.Ordinal));

    /// <summary>What is wrong with this import; <see langword="null"/> where nothing is.</summary>
    internal string? Problem(int lineCount)
        => !CodeImportForms.All.Contains(Form, StringComparer.Ordinal) ? $"its form is '{Form}', which is none of {string.Join(", ", CodeImportForms.All)}"
            : string.IsNullOrWhiteSpace(Target) ? "it names nothing"
            : !FileExtract.Within(Line, lineCount) ? string.Create(CultureInfo.InvariantCulture, $"it is on line {Line}, outside the file's {lineCount}")
            : Prefix is { } prefix && string.IsNullOrWhiteSpace(prefix) ? "its prefix is empty"
            : Fallback && Form is CodeImportForms.Scope or CodeImportForms.Directory ? "it is of a scope, and stands in for another import: only an import of a file does"
            : Export && Form is CodeImportForms.Scope or CodeImportForms.Directory ? "it is of a scope, and is an export: only an import of a file is exported"
            : Export && Prefix is not null ? "it is an export with a prefix: an export makes names visible by themselves"
            : Published && (Prefix is not null || Show is not null || Export || Reexports) ? "it takes the names a file publishes, and so names no prefix, filters none and stands for nothing else"
            : Published && Form is CodeImportForms.Scope or CodeImportForms.Directory ? "it is of a scope, and takes the names a file publishes: only an import of a file does"
            : Layout && Form != CodeImportForms.Relative ? "it names a place of a layout, which only a path relative to the file does"
            : Layout && Export ? "it names a place a name is looked for, which is no export: an export names a file"
            : Shares && Form is CodeImportForms.Scope or CodeImportForms.Directory ? "it is of a scope, and shares its imports: only a file's are shared"
            : Shares && (Export || Prefix is not null || Show is not null || Hide is not null) ? "it shares its imports, and is an export or has a prefix or a filter: what is shared is the file's imports as they are"
            : Namespace && (Form != CodeImportForms.Directory || Prefix is not null || Show is not null || Hide is not null) ? "it is the scope a directory's files are declared in, and is no import of a directory's whole scope"
            : null;
}

/// <summary>How an import names what it imports.</summary>
public static class CodeImportForms
{
    /// <summary>A scope, by its qualified name: a namespace, a type's members.</summary>
    public const string Scope = "scope";

    /// <summary>A file, by a path relative to the importing file's directory.</summary>
    public const string Relative = "relative";

    /// <summary>A file, by an address an address root gives a meaning (<see cref="ExtractAddressRoot"/>): a directory its prefix stands for, or the one file a whole address is.</summary>
    public const string Address = "address";

    /// <summary>A file, by the ending of its path: every file of the tree whose path ends so.</summary>
    public const string Ending = "ending";

    /// <summary>
    /// A scope, by its qualified name, that every file of the implementation's language under the declaring file's
    /// directory - the whole subtree - imports: a Razor <c>_ViewImports.cshtml</c>'s and Blazor's <c>_Imports.razor</c>'s
    /// <c>@using</c>.
    /// </summary>
    public const string Directory = "directory";

    /// <summary>Every form.</summary>
    public static IReadOnlyList<string> All { get; } = [Scope, Relative, Address, Ending, Directory];
}

/// <summary>
/// What an address prefix means, as a project file declares it: <c>package:foo/</c> is the directory <c>lib/</c> beside
/// it, and every address it begins is a path under that directory - or, where it is <see cref="Whole"/>, one address alone
/// is one file, as an npm package's name is its entry. An address is the first root's that matches it and names a file of
/// the tree: first the <see cref="Local"/> roots of the files in whose directories the importing file is, the nearest
/// file's first; then every other; in each, the longest prefix first, roots of one prefix in the order their files declare
/// them, the files in their paths' order.
/// </summary>
/// <param name="Prefix">The prefix of an address, or the whole address of one file.</param>
/// <param name="Directory">The directory it stands for - the file, for a whole address - relative to the declaring file's own, with forward separators.</param>
public sealed record ExtractAddressRoot(string Prefix, string Directory)
{
    /// <summary>Whether <see cref="Prefix"/> is a whole address, which <see cref="Directory"/> - a file - is the one file of.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Whole { get; init; }

    /// <summary>
    /// Whether it gives its meaning to the addresses only the files in its file's directory, or below it, import - a
    /// TypeScript project's path mappings, which its own files resolve by, before any other way - rather than to every
    /// file's, as a package's name is everyone's.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Local { get; init; }
}

/// <summary>One reference a file makes, by name, for the linker to resolve into an edge.</summary>
/// <param name="From">The id of the node of the extract it is made in.</param>
/// <param name="Kind">The edge it becomes: one of <see cref="CodeEdgeKinds.Referenced"/>.</param>
/// <param name="Name">The simple name it names.</param>
/// <param name="Line">The line it is written on.</param>
public sealed record ExtractReference(QualifiedName From, string Kind, string Name, int Line)
{
    /// <summary>How many arguments a call gives; <see langword="null"/> where it is no call, or that is not known.</summary>
    public int? Arity { get; init; }

    /// <summary>Where the name is looked for, and nowhere else, where the code says: a receiver's type or value, a type's name, <c>this</c>, an import's prefix, a scope.</summary>
    public ExtractQualifier? Qualifier { get; init; }

    /// <summary>
    /// Whether it asks whether a declaration is there rather than naming one the code depends on: where none is, that is no doubt -
    /// never counted an external callee of the node it is made from, so it makes no node unsure - as a method's name asked of its
    /// type's bases is in a language where no word says it overrides; where one is, an edge is made as of any reference.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Probe { get; init; }

    /// <summary>What is wrong with this reference; <see langword="null"/> where nothing is.</summary>
    internal string? Problem(int lineCount)
        => !CodeEdgeKinds.Referenced.Contains(Kind, StringComparer.Ordinal) ? $"its kind is '{Kind}', which is none of {string.Join(", ", CodeEdgeKinds.Referenced)}"
            : string.IsNullOrWhiteSpace(Name) ? "it names nothing"
            : !FileExtract.Within(Line, lineCount) ? string.Create(CultureInfo.InvariantCulture, $"it is on line {Line}, outside the file's {lineCount}")
            : Arity is < 0 ? "its arity is below zero"
            : Qualifier is { } qualifier && !CodeQualifierKinds.All.Contains(qualifier.Kind, StringComparer.Ordinal) ? $"its qualifier's kind is '{qualifier.Kind}', which is none of {string.Join(", ", CodeQualifierKinds.All)}"
            : Qualifier is { Kind: CodeQualifierKinds.Named or CodeQualifierKinds.Type or CodeQualifierKinds.Prefix or CodeQualifierKinds.Own or CodeQualifierKinds.Module } named && (named.Name is not { } namedName || namedName.WhitespaceOnly())
                && named is not { Kind: CodeQualifierKinds.Named, Name: null, Link: not null } ? "its qualifier names nothing"
            : Qualifier is { Kind: CodeQualifierKinds.Value or CodeQualifierKinds.Scope or CodeQualifierKinds.Key, Name: { } value } && value.WhitespaceOnly() ? "its qualifier's name is empty"
            : null;
}

/// <summary>Where a reference's name is looked for first.</summary>
/// <param name="Kind">One of <see cref="CodeQualifierKinds"/>.</param>
/// <param name="Name">
/// The type, scope, prefix, value or chain of names it names, where its kind names one and it writes them itself - a
/// name as a whole, or a node of its own file named by its id - none where it is a chain written as links
/// (<see cref="Link"/>).
/// </param>
public sealed record ExtractQualifier(string Kind, QualifiedName? Name = null)
{
    /// <summary>
    /// Where the qualifier is a chain of two names or more - a receiver's <c>a.b.c</c> - of a value or of a name
    /// (<see cref="CodeQualifierKinds.Value"/>, <see cref="CodeQualifierKinds.Named"/>): the place, among its extract's
    /// references, of the reference made for the chain's last name - <c>c</c>'s - which is made before this one and whose
    /// own qualifier writes the names before it, by a name or by a link in turn. So a chain of n names is n references of
    /// one name each, never each name written again by every reference after it, and the linker reads each link once
    /// whatever follows it. <see cref="FileExtract.Written"/> reads the chain's names. <see langword="null"/> where the
    /// qualifier writes its names itself.
    /// </summary>
    public int? Link { get; init; }
}

/// <summary>The kinds of qualifier.</summary>
public static class CodeQualifierKinds
{
    /// <summary>The type the reference is written in, and its bases.</summary>
    public const string This = "this";

    /// <summary>The bases of the type the reference is written in, and not the type itself.</summary>
    public const string Base = "base";

    /// <summary>
    /// A name the code writes before a member that may be a type's, a scope's or a value's - a type's name before a static
    /// call, a member of a base by its name: a value the scopes hold nearer than any type or scope of its name, and the
    /// call through its type, a chain of names through each one's in turn; else a type or a scope found as a name is.
    /// </summary>
    public const string Named = "named";

    /// <summary>
    /// A type, by its name as the code writes it where the code says it is a type's - a variable's declared type, a type
    /// made or cast, a type named in a declaration: looked for as a type or a scope alone, never as a value.
    /// </summary>
    public const string Type = "type";

    /// <summary>An import's prefix: the name is looked for in what that import makes visible, and nowhere else.</summary>
    public const string Prefix = "prefix";

    /// <summary>
    /// A value whose type the code does not say where the call is written - a variable given a call's result, a member of
    /// another file - by the name it is written as, where it is a name: the name is looked for as a member of the scopes the
    /// call is written in, nearest first, and its call through the type that member is declared with - a chain of names
    /// through each one's type in turn, a type the repository does not declare holding nothing it declares - else in the
    /// repository by name and arity alone, where nothing is known of its type - never in those scopes as a call of their own.
    /// A name no such member has that an import of the file is reached by is that import's prefix: the call is looked for
    /// where <see cref="Prefix"/> looks, as a file that shares another's imports writes that file's prefixes.
    /// </summary>
    public const string Value = "value";

    /// <summary>
    /// A scope by its qualified name, and past it the repository's own top: the name is looked for as that scope declares
    /// it, else as a declaration in no scope, and nowhere else - as PHP finds a function its namespace declares, else the
    /// global one; with no name, among the declarations in no scope alone - a name PHP writes from its global namespace,
    /// or after a leading <c>\</c>. Either is looked for among the declarations of the reference's own language alone.
    /// </summary>
    public const string Scope = "scope";

    /// <summary>
    /// A selector, as markup writes one - <c>.class</c>, <c>#id</c>, an element - and a sheet's <c>@extend</c>,
    /// <c>@apply</c> and <c>composes</c> do: the name looked for among rules alone (<see cref="CodeSubKinds.Rule"/>),
    /// nearest first, as every name a style sheet's syntax says the kind of is - what the reference's own file declares,
    /// what each file it imports or links declares, then the repository's - a rule that stands for a suffix written onto
    /// several (<see cref="ExtractNode.SuffixOf"/>) found where it writes the name; a rule a file keeps to itself - a CSS
    /// module's class, a scoped style block's - seen from that file alone.
    /// </summary>
    public const string Selector = "selector";

    /// <summary>
    /// A configuration key, by its name, as an MSBuild project's <c>$(Name)</c> writes one: the name looked for among keys
    /// alone (<see cref="CodeSubKinds.Key"/>), wherever the repository declares them - and its own file's, where it keeps
    /// them to itself; with a name, among the keys of the language it names alone - MSBuild's for a project's own
    /// properties, the xml extractor's for an imported <c>.props</c> file's.
    /// </summary>
    public const string Key = "key";

    /// <summary>
    /// A style sheet's variable, as a value names one - <c>var(--x)</c>, <c>$x</c>, <c>@x</c>: the name looked for among
    /// style variables alone (<see cref="CodeSubKinds.StyleVariable"/>), nearest first as a selector is - the sheet's own,
    /// what it imports by no namespace, else the repository's.
    /// </summary>
    public const string StyleVariable = CodeSubKinds.StyleVariable;

    /// <summary>
    /// A Sass mixin, as an <c>@include</c> names one: the name looked for among mixins alone
    /// (<see cref="CodeSubKinds.StyleMixin"/>), nearest first as a variable is.
    /// </summary>
    public const string StyleMixin = CodeSubKinds.StyleMixin;

    /// <summary>
    /// A style sheet's function, as a value's call names one - <c>rem(2px)</c>: the name looked for among functions alone
    /// (<see cref="CodeSubKinds.StyleFunction"/>), nearest first as a variable is.
    /// </summary>
    public const string StyleFunction = CodeSubKinds.StyleFunction;

    /// <summary>
    /// An animation's keyframes, as an <c>animation</c> or <c>animation-name</c> value names them: the name looked for
    /// among keyframes alone (<see cref="CodeSubKinds.Keyframes"/>), nearest first as a selector is.
    /// </summary>
    public const string Keyframes = CodeSubKinds.Keyframes;

    /// <summary>
    /// A font's face, as a <c>font-family</c> or a <c>font</c> names its family - in lower case, as CSS matches one: the
    /// name looked for among font faces alone (<see cref="CodeSubKinds.FontFace"/>), nearest first as a selector is.
    /// </summary>
    public const string FontFace = CodeSubKinds.FontFace;

    /// <summary>
    /// A node of the reference's own file, by its id, which its implementation names where it knows it: the one node it
    /// names, exact, of whatever kind - a YAML alias to the node its anchor names, a mapping to each mapping its merge key
    /// merges. The file reads what it repeats once, and each reference to it is this.
    /// </summary>
    public const string Own = "own";

    /// <summary>
    /// What a CSS module of the reference's own file exports by the reference's name, as a binding of the module reads it
    /// - a Vue component's <c>$style.red</c>, which its <c>&lt;style module&gt;</c> binds: with the name of the scope the
    /// module keeps its own names to (<see cref="ExtractNode.SeenIn"/>), each class, id and keyframes of it exported by
    /// that name, a suffix written onto others' by the names it writes, exact - and nothing else of that name.
    /// </summary>
    public const string Module = "module";

    /// <summary>
    /// A fixture a test is given a parameter by, as pytest names one - by the parameter's name alone, never by an import: the
    /// name is looked for among the fixtures (<see cref="ExtractNode.SubKind"/> <c>fixture</c>) the test's own scopes declare,
    /// exact, else among those the repository declares wherever - a conftest.py provides them to every test under it and no
    /// test imports it - and in nothing else.
    /// </summary>
    public const string Fixture = "fixture";

    /// <summary>Every kind.</summary>
    public static IReadOnlyList<string> All { get; } = [This, Base, Named, Type, Prefix, Value, Scope, Selector, Key, StyleVariable, StyleMixin, StyleFunction, Keyframes, FontFace, Own, Module, Fixture];
}

/// <summary>A place an implementation saw something it cannot bind, or stopped reading.</summary>
/// <param name="Line">The line.</param>
/// <param name="Why">What it saw: a call a macro writes, a member named by a string to reflection, a call through a pointer, a file that stops parsing there.</param>
public sealed record ExtractDynamic(int Line, string Why)
{
    /// <summary>
    /// The kind of thing the implementation saw, where its help tells a limit by it (<see cref="CodeGraphLimit.Signs"/>): the same
    /// word in a limit and in every mark of the kind, so an answer says of a file only the limits its marks show.
    /// <see langword="null"/> where none is named.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Kind { get; init; }
}

/// <summary>
/// One string value a file's literals hold, kept once however many literals of it the file writes, with every place one
/// is written: a value a minified bundle writes a million times costs its characters once.
/// </summary>
/// <param name="Value">
/// The literal's value as its language reads it, in every language alike: what its opener and closer hold - a prefix, a
/// raw string's delimiter, a triple quote and a heredoc's label and closing indentation no part of it - its escapes
/// decoded, a raw one's text as written, its line endings one newline; a constant part of an interpolated or template
/// string, each part a literal of its own.
/// </param>
/// <param name="Places">Each place a literal of the value is written, in the order its reader read them.</param>
public sealed record ExtractLiteral(string Value, IReadOnlyList<ExtractLiteralPlace> Places);

/// <summary>One place a file writes a string literal.</summary>
/// <param name="Line">The line its value begins on.</param>
public sealed record ExtractLiteralPlace(int Line)
{
    /// <summary>
    /// The id of the node of the extract whose code holds it - the innermost declaration it is written in, or the node of
    /// what a file's top does outside its declarations; <see langword="null"/> where none does, and the file's own holds it.
    /// </summary>
    public QualifiedName? Node { get; init; }
}
