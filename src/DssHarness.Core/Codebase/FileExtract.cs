using System.Globalization;
using System.Text.Json.Serialization;
using DssHarness.Core.Orchestration;

namespace DssHarness.Core.Codebase;

/// <summary>
/// What an implementation of <see cref="ICodeGraphExtractor"/> makes of one file: language-neutral, the model of the graph
/// and nothing of any language - its declarations, the scopes they are put in, what the file imports, what an address
/// means, the references it makes by name, unresolved, and four marks the linker reads without knowing why they were set.
/// </summary>
/// <remarks>
/// The linker resolves every reference by the same steps for every language - the qualifier's scope, the enclosing
/// scopes from the inside out, the imports in order, the repository by name and arity (<see cref="CodeGraphLinker"/>) -
/// so a language says in its extract what its scopes and imports are, and never adds a rule to the linker.
/// </remarks>
/// <param name="Nodes">Its declarations; the file's own node is the core's, and is not among them.</param>
public sealed record FileExtract(IReadOnlyList<ExtractNode> Nodes)
{
    /// <summary>The longest a node's signature may be, in characters.</summary>
    public const int MostSignature = 240;

    /// <summary>The longest a node's summary may be, in characters.</summary>
    public const int MostSummary = 200;

    /// <summary>An extract that holds nothing: what a file no implementation reads past its first bytes gives.</summary>
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
    /// Why the file is generated code, where the implementation knows a generator wrote it; <see langword="null"/> where
    /// it is not. Its declarations are then marked and ranked after every other, or left out under
    /// <c>codebaseGraph.generated: exclude</c>.
    /// </summary>
    public string? Generated { get; init; }

    /// <summary>Each place the implementation saw something it cannot bind - a call a macro writes, a member named by a string - with why.</summary>
    public IReadOnlyList<ExtractDynamic> Dynamic { get; init; } = [];

    /// <summary>
    /// What is wrong with this extract of a file of <paramref name="lineCount"/> lines made by
    /// <paramref name="language"/>; <see langword="null"/> where nothing is. The shape alone: a node's digest is checked
    /// against the file's lines where the lines are at hand.
    /// </summary>
    /// <param name="language">The implementation that made it, whose name prefixes every id.</param>
    /// <param name="lineCount">How many lines the file has (<see cref="CodeFileText.Lines"/>).</param>
    public string? Problem(string language, int lineCount)
    {
        var ids = new Dictionary<string, ExtractNode>(StringComparer.Ordinal);

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
        // whatever walks it up - the type a reference is made in - ends at its root.
        foreach (var node in Nodes ?? [])
        {
            var seen = new HashSet<string>(StringComparer.Ordinal) { node.Id };

            for (var parent = node.Parent; parent is not null; parent = ids[parent].Parent)
            {
                if (!seen.Add(parent))
                {
                    return $"its node '{node.Id}' is declared inside itself, through '{parent}'";
                }
            }
        }

        var scopes = new HashSet<string>(StringComparer.Ordinal);

        foreach (var scope in Scopes ?? [])
        {
            if (scope is null || string.IsNullOrWhiteSpace(scope.Name))
            {
                return "it holds a scope with no name";
            }

            if (!scopes.Add(scope.Name))
            {
                return $"it declares the scope '{scope.Name}' twice";
            }
        }

        return (Imports ?? []).Select(import => import?.Problem(lineCount) is { } wrong ? $"an import of '{import.Target}': {wrong}" : import is null ? "it holds an import that is null" : null).FirstOrDefault(found => found is not null)
            ?? (AddressRoots ?? []).Select(root => root is null || string.IsNullOrWhiteSpace(root.Prefix) || string.IsNullOrWhiteSpace(root.Directory) ? "it holds an address root with no prefix or no directory" : null).FirstOrDefault(found => found is not null)
            ?? (References ?? []).Select(reference => reference is null ? "it holds a reference that is null"
                : !ids.ContainsKey(reference.From) ? $"a reference to '{reference.Name}' is made from '{reference.From}', which it holds no node of"
                : reference.Problem(lineCount) is { } wrong ? $"a reference to '{reference.Name}': {wrong}"
                : null).FirstOrDefault(found => found is not null)
            ?? (Generated is { } generated && string.IsNullOrWhiteSpace(generated) ? "it says it is generated and not why" : null)
            ?? (Dynamic ?? []).Select(dynamic => dynamic is null || string.IsNullOrWhiteSpace(dynamic.Why) ? "it marks a line dynamic and not why"
                : !Within(dynamic.Line, lineCount) ? $"it marks line {dynamic.Line.ToString(CultureInfo.InvariantCulture)} dynamic, outside its {lineCount.ToString(CultureInfo.InvariantCulture)} lines"
                : null).FirstOrDefault(found => found is not null)
            ?? (Texts().FirstOrDefault(text => !IsWhole(text)) is { } cut ? $"it holds text that is no whole UTF-16, a surrogate cut from its pair: '{cut}'" : null);
    }

    /// <summary>
    /// This extract with every node's digest set from <paramref name="lines"/>, the file's lines it was made from: what the
    /// core does once an implementation has answered, and nothing else does - a digest an implementation set is never
    /// kept. A node whose lines are not the file's is left without one, for <see cref="Problem"/> to refuse.
    /// </summary>
    /// <param name="lines">The file's lines (<see cref="CodeFileText.Lines"/>).</param>
    public FileExtract WithDigests(IReadOnlyList<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        return this with
        {
            Nodes = [.. (Nodes ?? []).Select(node => node is null ? node! : node with
            {
                Digest = node.Start >= 1 && node.End >= node.Start && node.End <= lines.Count ? CodeGraphIds.Digest(lines, node.Start, node.End) : null,
            })],
        };
    }

    /// <summary>Whether <paramref name="line"/> is one of a file's <paramref name="lineCount"/> lines.</summary>
    internal static bool Within(int line, int lineCount) => line >= 1 && line <= lineCount;

    /// <summary>
    /// Whether <paramref name="text"/> is whole UTF-16 - no surrogate apart from its pair - as a record is written in and
    /// read back: a cut that splits a pair is no text a store holds.
    /// </summary>
    internal static bool IsWhole(string text)
    {
        for (var at = 0; at < text.Length; at++)
        {
            if (char.IsHighSurrogate(text[at]) && at + 1 < text.Length && char.IsLowSurrogate(text[at + 1]))
            {
                at++;
            }
            else if (char.IsSurrogate(text[at]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Every text the extract holds, each of which a store writes.</summary>
    private IEnumerable<string> Texts()
        => (Nodes ?? []).SelectMany(node => (string?[])[node.Id, node.Kind, node.Name, node.Qualified, node.Signature, node.SubKind, node.Scope, node.Parent, node.Summary, node.ValueType, node.Extends, .. node.Modifiers ?? []])
            .Concat((Scopes ?? []).SelectMany(scope => (string?[])[scope.Name, scope.Parent]))
            .Concat((Imports ?? []).SelectMany(import => (string?[])[import.Form, import.Target, import.Prefix, .. import.Show ?? [], .. import.Hide ?? []]))
            .Concat((AddressRoots ?? []).SelectMany(root => (string?[])[root.Prefix, root.Directory]))
            .Concat((References ?? []).SelectMany(reference => (string?[])[reference.From, reference.Kind, reference.Name, reference.Qualifier?.Kind, reference.Qualifier?.Name]))
            .Concat((Dynamic ?? []).Select(dynamic => dynamic.Why))
            .Append(Generated)
            .OfType<string>();

    private static string Lines(ExtractNode node)
        => string.Create(CultureInfo.InvariantCulture, $"{node.Start}-{node.End}");
}

/// <summary>One declaration of a file's extract.</summary>
/// <param name="Id">
/// Its id: the implementation's <see cref="ICodeGraphExtractor.Language"/>, a colon and its qualified name, with its
/// parameters' types where it takes some - stable across syncs while the declaration keeps its name and parameters.
/// </param>
/// <param name="Kind">One of <see cref="CodeNodeKinds.Extracted"/>.</param>
/// <param name="Name">Its simple name, as a reference names it.</param>
/// <param name="Qualified">Its name with every scope it is declared in, as a qualified reference names it.</param>
/// <param name="Start">Its first line, from 1.</param>
/// <param name="End">Its last line.</param>
/// <param name="Signature">Its header on one line, at most <see cref="FileExtract.MostSignature"/> characters.</param>
public sealed record ExtractNode(string Id, string Kind, string Name, string Qualified, int Start, int End, string Signature)
{
    /// <summary>The kind within its kind, which the implementation names freely: a <c>class</c>, a <c>method</c>.</summary>
    public string? SubKind { get; init; }

    /// <summary>The scope it is declared in, by its qualified name; <see langword="null"/> where it is the repository's own.</summary>
    public string? Scope { get; init; }

    /// <summary>The id of the node of this extract it is declared inside; <see langword="null"/> where it is the file's.</summary>
    public string? Parent { get; init; }

    /// <summary>The line its body begins on, after its signature, where it has one.</summary>
    public int? BodyStart { get; init; }

    /// <summary>What the code says of itself: the first sentence of its documentation, at most <see cref="FileExtract.MostSummary"/> characters.</summary>
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
    /// The SHA-256 of its lines (<see cref="CodeGraphIds.Digest"/>): set by the core once the implementation has answered,
    /// never by it, and what says whether the bytes served are the ones extracted.
    /// </summary>
    public string? Digest { get; init; }

    /// <summary>Whether a call of <paramref name="arity"/> arguments may call it.</summary>
    /// <param name="arity">How many arguments the call gives; <see langword="null"/> where that is not known.</param>
    public bool TakesArity(int? arity)
        => arity is not { } given || MinArity is not { } least || (given >= least && (MaxArity is not { } most || given <= most));

    /// <summary>What is wrong with this node of an extract of <paramref name="lineCount"/> lines; <see langword="null"/> where nothing is.</summary>
    internal string? Problem(string language, int lineCount)
        => string.IsNullOrWhiteSpace(Id) ? "it has no id"
            : !Id.StartsWith(language + ":", StringComparison.Ordinal) ? $"its id does not begin '{language}:', its implementation's name"
            : !CodeNodeKinds.Extracted.Contains(Kind, StringComparer.Ordinal) ? $"its kind is '{Kind}', which is none of {string.Join(", ", CodeNodeKinds.Extracted)}"
            : string.IsNullOrWhiteSpace(Name) || string.IsNullOrWhiteSpace(Qualified) ? "it has no name"
            : !FileExtract.Within(Start, lineCount) || End < Start || End > lineCount ? string.Create(CultureInfo.InvariantCulture, $"its lines {Start}-{End} are not lines of the file's {lineCount}")
            : BodyStart is { } body && (body < Start || body > End) ? string.Create(CultureInfo.InvariantCulture, $"its body begins on line {body}, outside its lines {Start}-{End}")
            : Signature is null || Signature.Length > FileExtract.MostSignature || Signature.AsSpan().IndexOfAny('\r', '\n') >= 0 ? $"its signature is not one line of at most {FileExtract.MostSignature} characters"
            : Summary is { } summary && (summary.Length > FileExtract.MostSummary || summary.AsSpan().IndexOfAny('\r', '\n') >= 0) ? $"its summary is not one line of at most {FileExtract.MostSummary} characters"
            : MinArity is < 0 || (MaxArity is { } most && (MinArity is not { } least || most < least)) ? "its arity is no range of argument counts"
            : ValueType is { } valueType && string.IsNullOrWhiteSpace(valueType) ? "its value's type is empty"
            : Extends is { } extends && string.IsNullOrWhiteSpace(extends) ? "the type it extends is empty"
            : Digest is { } digest && !OrchestrationRules.IsDigest(digest) ? "its digest is no SHA-256"
            : null;
}

/// <summary>One named container a file's declarations are put in.</summary>
/// <param name="Name">Its qualified name.</param>
/// <param name="Open">
/// Whether other files contribute to it - a namespace, a library and its parts - so that its name is one scope for every
/// file that declares it; a closed scope is its file's alone, and what is declared in it - and the type or scope it is
/// the scope of - is seen from no other file.
/// </param>
public sealed record ExtractScope(string Name, bool Open)
{
    /// <summary>The scope it is declared in, by its qualified name, which the linker looks in next; <see langword="null"/> where none is.</summary>
    public string? Parent { get; init; }
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
            : null;
}

/// <summary>How an import names what it imports.</summary>
public static class CodeImportForms
{
    /// <summary>A scope, by its qualified name: a namespace, a type's members.</summary>
    public const string Scope = "scope";

    /// <summary>A file, by a path relative to the importing file's directory.</summary>
    public const string Relative = "relative";

    /// <summary>A file, by an address whose prefix an address root gives a directory (<see cref="ExtractAddressRoot"/>).</summary>
    public const string Address = "address";

    /// <summary>A file, by the ending of its path: every file of the tree whose path ends so.</summary>
    public const string Ending = "ending";

    /// <summary>Every form.</summary>
    public static IReadOnlyList<string> All { get; } = [Scope, Relative, Address, Ending];
}

/// <summary>What an address prefix means, as a project file declares it: <c>package:foo/</c> is the directory <c>lib/</c> beside it.</summary>
/// <param name="Prefix">The prefix of an address.</param>
/// <param name="Directory">The directory it stands for, relative to the declaring file's own, with forward separators.</param>
public sealed record ExtractAddressRoot(string Prefix, string Directory);

/// <summary>One reference a file makes, by name, for the linker to resolve into an edge.</summary>
/// <param name="From">The id of the node of the extract it is made in.</param>
/// <param name="Kind">The edge it becomes: one of <see cref="CodeEdgeKinds.Referenced"/>.</param>
/// <param name="Name">The simple name it names.</param>
/// <param name="Line">The line it is written on.</param>
public sealed record ExtractReference(string From, string Kind, string Name, int Line)
{
    /// <summary>How many arguments a call gives; <see langword="null"/> where it is no call, or that is not known.</summary>
    public int? Arity { get; init; }

    /// <summary>Where the name is looked for, and nowhere else, where the code says: a receiver's type or value, a type's name, <c>this</c>, an import's prefix.</summary>
    public ExtractQualifier? Qualifier { get; init; }

    /// <summary>What is wrong with this reference; <see langword="null"/> where nothing is.</summary>
    internal string? Problem(int lineCount)
        => !CodeEdgeKinds.Referenced.Contains(Kind, StringComparer.Ordinal) ? $"its kind is '{Kind}', which is none of {string.Join(", ", CodeEdgeKinds.Referenced)}"
            : string.IsNullOrWhiteSpace(Name) ? "it names nothing"
            : !FileExtract.Within(Line, lineCount) ? string.Create(CultureInfo.InvariantCulture, $"it is on line {Line}, outside the file's {lineCount}")
            : Arity is < 0 ? "its arity is below zero"
            : Qualifier is { } qualifier && !CodeQualifierKinds.All.Contains(qualifier.Kind, StringComparer.Ordinal) ? $"its qualifier's kind is '{qualifier.Kind}', which is none of {string.Join(", ", CodeQualifierKinds.All)}"
            : Qualifier is { Kind: CodeQualifierKinds.Named or CodeQualifierKinds.Type or CodeQualifierKinds.Prefix } named && string.IsNullOrWhiteSpace(named.Name) ? "its qualifier names nothing"
            : Qualifier is { Kind: CodeQualifierKinds.Value, Name: { } value } && string.IsNullOrWhiteSpace(value) ? "its qualifier's name is empty"
            : null;
}

/// <summary>Where a reference's name is looked for first.</summary>
/// <param name="Kind">One of <see cref="CodeQualifierKinds"/>.</param>
/// <param name="Name">The type, scope, prefix, value or chain of names it names, where its kind names one.</param>
public sealed record ExtractQualifier(string Kind, string? Name = null);

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
    /// </summary>
    public const string Value = "value";

    /// <summary>Every kind.</summary>
    public static IReadOnlyList<string> All { get; } = [This, Base, Named, Type, Prefix, Value];
}

/// <summary>A place an implementation saw something it cannot bind.</summary>
/// <param name="Line">The line.</param>
/// <param name="Why">What it saw: a call a macro writes, a member named by a string to reflection, a call through a pointer.</param>
public sealed record ExtractDynamic(int Line, string Why);
