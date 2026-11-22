using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DssHarness.Core.Codebase;

/// <summary>
/// Reads and writes an extract as its records keep it - a stored extract, and the contract kit's - so no record grows
/// with the square of how deep a file nests: each node by its place, and each name of a node - its id, its qualified name,
/// its signature, its scope and the scope it is seen in - written against its parent's names (<see cref="StoredName"/>), the
/// node it is declared inside by that node's place; each node a reference is made from, a literal is held by, a node of its
/// own file names (<see cref="CodeQualifierKinds.Own"/>) and a node's names are written onto, by its place; and each scope's
/// parent by its place where the extract declares it, its name written against that one's. Read back, every field that
/// names a node is that node's own id, and a scope that is its parent's qualified name is that very name, so the extract
/// held is the one written and holds each name once.
/// </summary>
internal sealed class FileExtractJson : JsonConverter<FileExtract>
{
    /// <summary>The names of a node, each a field another may be written against, in the order they are tried.</summary>
    private static readonly string[] Names = ["id", "qualified", "signature", "scope"];

    /// <inheritdoc/>
    public override FileExtract Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => (JsonSerializer.Deserialize<StoredExtract>(ref reader, options) ?? throw new JsonException("an extract is null")).Extract();

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, FileExtract value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(value);
        JsonSerializer.Serialize(writer, StoredExtract.Of(value), options);
    }

    /// <summary>The name of <paramref name="node"/> the field <paramref name="field"/> holds.</summary>
    private static string? NameOf(ExtractNode node, string field) => field switch
    {
        "id" => node.Id.Whole(),
        "qualified" => node.Qualified.Whole(),
        "signature" => node.Signature,
        "scope" => node.Scope?.Whole(),
        _ => throw new JsonException($"a name is written against the field '{field}', which is none of {string.Join(", ", Names)}"),
    };

    /// <summary>
    /// The name of <paramref name="node"/> the field <paramref name="field"/> holds, as a name (<see cref="QualifiedName"/>)
    /// where the field holds one and as its whole text where it holds text: what a name read back is made against.
    /// </summary>
    private static QualifiedName Base(ExtractNode node, string field) => field switch
    {
        "id" => node.Id,
        "qualified" => node.Qualified,
        "signature" => QualifiedName.Of(node.Signature),
        "scope" => node.Scope ?? throw new JsonException($"a name is written against the field '{field}', which the node it is written against holds none of"),
        _ => throw new JsonException($"a name is written against the field '{field}', which is none of {string.Join(", ", Names)}"),
    };

    /// <summary>The names of <paramref name="parent"/> a name of <paramref name="field"/> is tried against: that field's first.</summary>
    private static (string Field, string? Name)[] BasesOf(ExtractNode parent, string field)
    {
        var bases = new (string Field, string? Name)[Names.Length];
        bases[0] = (field, NameOf(parent, field));

        for (int at = 0, next = 1; at < Names.Length; at++)
        {
            if (Names[at] != field)
            {
                bases[next++] = (Names[at], NameOf(parent, Names[at]));
            }
        }

        return bases;
    }

    /// <summary>An extract as its record holds it.</summary>
    /// <param name="Nodes">Its nodes, each by its place.</param>
    private sealed record StoredExtract(IReadOnlyList<StoredNode> Nodes)
    {
        public IReadOnlyList<StoredScope> Scopes { get; init; } = [];

        public IReadOnlyList<ExtractImport> Imports { get; init; } = [];

        public IReadOnlyList<ExtractAddressRoot> AddressRoots { get; init; } = [];

        public IReadOnlyList<StoredReference> References { get; init; } = [];

        public string? FolderScope { get; init; }

        public string? Generated { get; init; }

        public IReadOnlyList<ExtractDynamic> Dynamic { get; init; } = [];

        public IReadOnlyList<StoredLiteral>? Literals { get; init; }

        /// <summary><paramref name="extract"/> as its record holds it.</summary>
        /// <exception cref="JsonException">A field names a node, or a scope, the extract holds none of: no sound extract does, and none is kept otherwise.</exception>
        public static StoredExtract Of(FileExtract extract)
        {
            var nodes = extract.Nodes ?? [];
            var places = new Dictionary<QualifiedName, int>();

            for (var at = 0; at < nodes.Count; at++)
            {
                places.TryAdd((nodes[at] ?? throw new JsonException("an extract holds a node that is null")).Id, at);
            }

            int Place(QualifiedName id, string why) => places.TryGetValue(id, out var at) ? at : throw new JsonException($"{why} '{id.Whole()}', which the extract holds no node of");

            var stored = new StoredNode[nodes.Count];

            for (var at = 0; at < nodes.Count; at++)
            {
                var node = nodes[at];
                var parent = node.Parent is { } held ? Place(held, $"its node '{node.Id}' is declared inside") : (int?)null;
                var named = parent is { } under ? nodes[under] : null;

                // The whole name's text is made here, against the bases, and never kept by the extract that holds the
                // name: what the record writes is the part the name adds to a base it begins with.
                StoredName Name(string field, QualifiedName name) => named is null ? new StoredName(name.Whole()) : StoredName.Against(name.Whole(), BasesOf(named, field));
                StoredName Text(string field, string name) => named is null ? new StoredName(name) : StoredName.Against(name, BasesOf(named, field));

                stored[at] = new StoredNode(Name("id", node.Id), node.Kind, node.Name, Name("qualified", node.Qualified), node.Start, node.End, Text("signature", node.Signature))
                {
                    SubKind = node.SubKind,
                    Scope = node.Scope is { } scope ? Name("scope", scope) : null,
                    Parent = parent,
                    BodyStart = node.BodyStart,
                    Summary = node.Summary,
                    Modifiers = node.Modifiers,
                    MinArity = node.MinArity,
                    MaxArity = node.MaxArity,
                    Prototype = node.Prototype,
                    Test = node.Test,
                    Part = node.Part,
                    ValueType = node.ValueType,
                    Extends = node.Extends,
                    SeenIn = node.SeenIn is { } seenIn ? Name("scope", seenIn) : null,
                    Exports = node.Exports,
                    SuffixOf = node.SuffixOf is { } onto ? [.. onto.Select(id => Place(id, $"its node '{node.Id}' stands for names written onto"))] : null,
                    Selectors = node.Selectors,
                    Templates = node.Templates,
                    Digest = node.Digest,
                };
            }

            var scopes = extract.Scopes ?? [];
            var scopePlaces = new Dictionary<QualifiedName, int>();

            for (var at = 0; at < scopes.Count; at++)
            {
                scopePlaces.TryAdd((scopes[at] ?? throw new JsonException("an extract holds a scope that is null")).Name, at);
            }

            return new StoredExtract(stored)
            {
                // A scope's parent may be a scope of its own in turn - a Dart library and its part's - so one is written
                // against its parent's place only where the two can never be written against each other.
                Scopes = [.. scopes.Select((scope, at) => scope.Parent is { } parent && scopePlaces.TryGetValue(parent, out var under) && StoredName.Precedes(scopes[under].Name.Whole(), under, scope.Name.Whole(), at)
                    ? new StoredScope(StoredName.Against(scope.Name.Whole(), [("name", scopes[under].Name.Whole())]), scope.Open) { ParentAt = under }
                    : new StoredScope(new StoredName(scope.Name.Whole()), scope.Open) { Parent = scope.Parent?.Whole() })],
                Imports = extract.Imports,
                AddressRoots = extract.AddressRoots,
                References = [.. (extract.References ?? []).Select(reference => new StoredReference(Place(reference.From, $"a reference to '{reference.Name}' is made from"), reference.Kind, reference.Name, reference.Line)
                {
                    Arity = reference.Arity,
                    Qualifier = reference.Qualifier is not { } qualifier ? null
                        : qualifier is { Kind: CodeQualifierKinds.Own, Name: { } own } ? new StoredQualifier(qualifier.Kind) { Node = Place(own, $"a reference to '{reference.Name}' names the node") }
                        : new StoredQualifier(qualifier.Kind) { Name = qualifier.Name?.Whole(), Link = qualifier.Link },
                })],
                FolderScope = extract.FolderScope,
                Generated = extract.Generated,
                Dynamic = extract.Dynamic,
                Literals = extract.Literals is { } literals
                    ? [.. literals.Select(literal => new StoredLiteral(literal.Value, [.. literal.Places.Select(place => new StoredPlace(place.Line) { Node = place.Node is { } node ? Place(node, $"its literal '{literal.Value}' is held by") : null })]))]
                    : null,
            };
        }

        /// <summary>The extract this record holds: each name made again from the ones it is written against, each node a field names by its place that node's own id.</summary>
        /// <exception cref="JsonException">A place is no node's or no scope's of the extract, a node or a scope is written against one written against it in turn, or a name keeps what its base does not hold.</exception>
        public FileExtract Extract()
        {
            var nodes = new ExtractNode[Nodes.Count];

            StoredName.InBaseOrder(
                Nodes.Count,
                at => Nodes[at].Parent,
                at =>
                {
                    var stored = Nodes[at];
                    var parent = stored.Parent is { } under ? nodes[under] : null;

                    // A name read back is held as the base it is written against and the part it adds to it, where it is
                    // written against all of that base - so what the record kept in bytes is what the extract holds.
                    QualifiedName Name(StoredName name, string field)
                    {
                        if (name.At is not null)
                        {
                            throw new JsonException(string.Create(CultureInfo.InvariantCulture, $"node {at}'s {field} is written against a line, and a node's names are written against its parent's"));
                        }

                        if (name.Kept == 0)
                        {
                            return QualifiedName.Of(name.Rest);
                        }

                        if (parent is null)
                        {
                            throw new JsonException(string.Create(CultureInfo.InvariantCulture, $"node {at}'s {field} keeps characters of a parent it has none of"));
                        }

                        var @base = Base(parent, name.Field ?? field);

                        if (name.Kept > @base.Length)
                        {
                            throw new JsonException(string.Create(CultureInfo.InvariantCulture, $"a name keeps {name.Kept} characters of a name of {@base.Length}"));
                        }

                        return name.Kept == @base.Length
                            ? name.Rest.StartsWith(".", StringComparison.Ordinal) ? @base.Under(name.Rest[1..]) : @base.Under(name.Rest, "")
                            : QualifiedName.Of(@base.Whole()[..name.Kept] + name.Rest);
                    }

                    var scope = stored.Scope is { } scoped ? Name(scoped, "scope") : null;

                    // The signature is text, not a name: read back against the parent's name it was written against.
                    string Signature(StoredName text) => text.Text(parent is null ? null : NameOf(parent, text.Field ?? "signature"));

                    nodes[at] = new ExtractNode(Name(stored.Id, "id"), stored.Kind, stored.Name, Name(stored.Qualified, "qualified"), stored.Start, stored.End, Signature(stored.Signature))
                    {
                        SubKind = stored.SubKind,

                        // A scope that is its parent's qualified name is that very name: held once.
                        Scope = scope is not null && parent is not null && scope == parent.Qualified ? parent.Qualified : scope,
                        Parent = parent?.Id,
                        BodyStart = stored.BodyStart,
                        Summary = stored.Summary,
                        Modifiers = stored.Modifiers,
                        MinArity = stored.MinArity,
                        MaxArity = stored.MaxArity,
                        Prototype = stored.Prototype,
                        Test = stored.Test,
                        Part = stored.Part,
                        ValueType = stored.ValueType,
                        Extends = stored.Extends,
                        SeenIn = stored.SeenIn is { } seenIn ? Name(seenIn, "scope") : null,
                        Exports = stored.Exports,
                        Selectors = stored.Selectors,
                        Templates = stored.Templates,
                        Digest = stored.Digest,
                    };
                },
                "node");

            QualifiedName IdAt(int at, string what)
                => at >= 0 && at < nodes.Length ? nodes[at].Id : throw new JsonException(string.Create(CultureInfo.InvariantCulture, $"{what} node {at}, and the extract holds {nodes.Length}"));

            for (var at = 0; at < nodes.Length; at++)
            {
                if (Nodes[at].SuffixOf is { } onto)
                {
                    nodes[at] = nodes[at] with { SuffixOf = [.. onto.Select(place => IdAt(place, $"node {at.ToString(CultureInfo.InvariantCulture)} stands for names written onto"))] };
                }
            }

            var scopes = new ExtractScope[Scopes.Count];

            StoredName.InBaseOrder(
                Scopes.Count,
                at => Scopes[at].ParentAt,
                at =>
                {
                    var stored = Scopes[at];

                    if (stored.ParentAt is not null && stored.Parent is not null)
                    {
                        throw new JsonException(string.Create(CultureInfo.InvariantCulture, $"scope {at} names its parent by its place and by its name at once"));
                    }

                    var parent = stored.ParentAt is { } under ? scopes[under] : null;

                    if (stored.Name.At is not null || stored.Name.Field is not null || (stored.Name.Kept > 0 && parent is null))
                    {
                        throw new JsonException(string.Create(CultureInfo.InvariantCulture, $"scope {at}'s name keeps characters of what is not its parent's name"));
                    }

                    if (stored.Name.Kept > 0 && parent is not null && stored.Name.Kept > parent.Name.Length)
                    {
                        throw new JsonException(string.Create(CultureInfo.InvariantCulture, $"a name keeps {stored.Name.Kept} characters of a name of {parent.Name.Length}"));
                    }

                    // A scope's name, too, is held as the one it is written against and the part it adds to it.
                    var name = stored.Name.Kept == 0 ? QualifiedName.Of(stored.Name.Rest)
                        : stored.Name.Rest.StartsWith(".", StringComparison.Ordinal) && stored.Name.Kept == parent!.Name.Length ? parent.Name.Under(stored.Name.Rest[1..])
                        : stored.Name.Kept == parent!.Name.Length ? parent.Name.Under(stored.Name.Rest, "")
                        : QualifiedName.Of(parent.Name.Whole()[..stored.Name.Kept] + stored.Name.Rest);

                    scopes[at] = new ExtractScope(name, stored.Open) { Parent = parent?.Name ?? (stored.Parent is { } held ? QualifiedName.Of(held) : null) };
                },
                "scope");

            return new FileExtract(nodes)
            {
                Scopes = scopes,
                Imports = Imports,
                AddressRoots = AddressRoots,
                References = [.. References.Select((reference, at) => new ExtractReference(IdAt(reference.From, $"reference {at.ToString(CultureInfo.InvariantCulture)} is made from"), reference.Kind, reference.Name, reference.Line)
                {
                    Arity = reference.Arity,
                    Qualifier = reference.Qualifier is not { } qualifier ? null
                        : qualifier.Node is { } own
                            ? qualifier.Kind == CodeQualifierKinds.Own && qualifier.Name is null && qualifier.Link is null
                                ? new ExtractQualifier(qualifier.Kind, IdAt(own, $"reference {at.ToString(CultureInfo.InvariantCulture)} names"))
                                : throw new JsonException(string.Create(CultureInfo.InvariantCulture, $"reference {at}'s qualifier names a node by its place, and only one of its own file does, alone"))
                            : new ExtractQualifier(qualifier.Kind, qualifier.Name is { } named ? QualifiedName.Of(named) : null) { Link = qualifier.Link },
                })],
                FolderScope = FolderScope,
                Generated = Generated,
                Dynamic = Dynamic,
                Literals = Literals is null ? null : [.. Literals.Select(literal => new ExtractLiteral(literal.Value, [.. literal.Places.Select(place => new ExtractLiteralPlace(place.Line) { Node = place.Node is { } node ? IdAt(node, $"a place of the literal '{literal.Value}' is held by") : null })]))],
            };
        }
    }

    /// <summary>One node as its record holds it: its names written against its parent's, its parent by its place.</summary>
    private sealed record StoredNode(StoredName Id, string Kind, string Name, StoredName Qualified, int Start, int End, StoredName Signature)
    {
        public string? SubKind { get; init; }

        public StoredName? Scope { get; init; }

        public int? Parent { get; init; }

        public int? BodyStart { get; init; }

        public string? Summary { get; init; }

        public IReadOnlyList<string> Modifiers { get; init; } = [];

        public int? MinArity { get; init; }

        public int? MaxArity { get; init; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool Prototype { get; init; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool Test { get; init; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool Part { get; init; }

        public string? ValueType { get; init; }

        public string? Extends { get; init; }

        public StoredName? SeenIn { get; init; }

        public IReadOnlyList<string>? Exports { get; init; }

        public IReadOnlyList<int>? SuffixOf { get; init; }

        public IReadOnlyList<string>? Selectors { get; init; }

        public IReadOnlyList<string>? Templates { get; init; }

        public string? Digest { get; init; }
    }

    /// <summary>One scope as its record holds it: its parent by its place where the extract declares it, its name written against that one's.</summary>
    private sealed record StoredScope(StoredName Name, bool Open)
    {
        public int? ParentAt { get; init; }

        public string? Parent { get; init; }
    }

    /// <summary>One reference as its record holds it: the node it is made from by its place.</summary>
    private sealed record StoredReference(int From, string Kind, string Name, int Line)
    {
        public int? Arity { get; init; }

        public StoredQualifier? Qualifier { get; init; }
    }

    /// <summary>A reference's qualifier as its record holds it: a node of its own file by its place.</summary>
    private sealed record StoredQualifier(string Kind)
    {
        public string? Name { get; init; }

        public int? Link { get; init; }

        public int? Node { get; init; }
    }

    /// <summary>One literal as its record holds it.</summary>
    private sealed record StoredLiteral(string Value, IReadOnlyList<StoredPlace> Places);

    /// <summary>One place of a literal as its record holds it: the node that holds it by its place.</summary>
    private sealed record StoredPlace(int Line)
    {
        public int? Node { get; init; }
    }
}
