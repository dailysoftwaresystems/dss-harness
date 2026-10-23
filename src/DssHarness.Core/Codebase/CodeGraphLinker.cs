namespace DssHarness.Core.Codebase;

/// <summary>
/// The one linker: turns the references every extract keeps, by name, into directed edges, for every language alike. It
/// knows no language - what differs between them is what their extracts say their scopes and imports are, never a rule
/// of this.
/// </summary>
/// <remarks>
/// <para>
/// A reference with a qualifier is looked for where the qualifier says alone - a type and its bases, a scope, an
/// import's prefix, a value's type. One without is looked for in the scopes it is written in, from the inside out, then
/// as a scope import's prefix (an alias) for what that import stands for, then in what its file imports, in order, each
/// import's filter applied, then in the repository by name and arity. A type qualifier is a type or a scope found as
/// such a name is: in those scopes, by its whole qualified name, as an alias it is or begins with, in the imports, in
/// the repository; else every type or scope its name ends. A named qualifier is that too, unless a value the scopes hold
/// is nearer - a member, and the call goes through the type that member is declared with, a chain of names through each
/// one's type in turn. A value of a type the code does not say is looked for through the member it is, else by the
/// call's name in the repository alone; a chain that comes to a type the repository does not declare holds nothing it
/// declares. A call a type does not hold is a member that extends that type or a base of it, which the call's file
/// reaches - <c>unique</c>, since no declaration of the type says so. The first place that holds the name decides: one
/// declaration is an edge <c>exact</c> where the code itself said where to look (a qualifier, an enclosing scope) and
/// <c>unique</c> where it did not; two to <see cref="CodeConfidences.MostCandidates"/>
/// are an edge to each, of <c>candidates</c>; more are counted unresolved; none is counted external, a callee the
/// repository does not declare.
/// </para>
/// <para>
/// What a closed scope declares is seen from its file alone, as is a type or a scope whose own scope is closed.
/// </para>
/// <para>
/// <c>dispatches</c>, <c>declares</c> and <c>tests</c> are derived after, from the <c>implements</c> edges and the marks
/// every extract may set. The extracts keep names and not targets, so linking again after a file changed binds a name it
/// added to callers in files that did not change.
/// </para>
/// </remarks>
public static class CodeGraphLinker
{
    /// <summary>
    /// This linker's revision: raised whenever what it answers for the same extracts changes. Every link result names the
    /// revision that made it (<see cref="CodeGraphSymbolsFile.Linker"/>), so a graph linked by another one is linked again
    /// though no file changed, and no file is read again.
    /// </summary>
    public const int Revision = 2;

    /// <summary>Links <paramref name="files"/>, one tree's every file, into its symbols and edges.</summary>
    /// <param name="files">Every file the graph keeps, each once: those whose declarations are kept with their extracts.</param>
    public static CodeGraphLinked Link(IReadOnlyList<CodeGraphFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        return new Linking(files).Link();
    }

    /// <summary>One declaration of one file, placed.</summary>
    /// <param name="Node">What its extract says of it.</param>
    /// <param name="File">The file that declares it.</param>
    /// <param name="ScopeKey">The scope it is declared in, by key.</param>
    /// <param name="FileOnly">Whether it is its file's alone: declared in a closed scope, or a type or scope whose own scope is closed.</param>
    private sealed record Declaration(ExtractNode Node, CodeGraphFile File, string? ScopeKey, bool FileOnly)
    {
        public string Id => Node.Id;
    }

    private sealed class Linking
    {
        private readonly IReadOnlyList<CodeGraphFile> _files;
        private readonly HashSet<string> _paths;
        private readonly Dictionary<string, CodeGraphFile> _byPath = new(StringComparer.Ordinal);

        /// <summary>Every path by its file's name, the last part of it: where an import by a path's ending looks.</summary>
        private readonly Dictionary<string, List<string>> _byFileName = new(StringComparer.Ordinal);

        /// <summary>The files each import of each file names, once found.</summary>
        private readonly Dictionary<CodeGraphFile, Dictionary<ExtractImport, IReadOnlyList<string>>> _imported = new(ReferenceEqualityComparer.Instance);
        private readonly List<Declaration> _declarations = [];

        /// <summary>Each file's declarations by id: the one a reference of that file is made from.</summary>
        private readonly Dictionary<CodeGraphFile, Dictionary<string, Declaration>> _owners = new(ReferenceEqualityComparer.Instance);

        /// <summary>What every file of each language imports (<see cref="ExtractImport.Global"/>), with the file that says so.</summary>
        private readonly Dictionary<string, List<(CodeGraphFile File, ExtractImport Import)>> _global = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<Declaration>> _byId = new(StringComparer.Ordinal);

        /// <summary>Every declaration by the scope it is declared in and its name.</summary>
        private readonly Dictionary<string, Dictionary<string, List<Declaration>>> _byScope = new(StringComparer.Ordinal);

        /// <summary>Every declaration any file can see by its name: none in a closed scope.</summary>
        private readonly Dictionary<string, List<Declaration>> _byName = new(StringComparer.Ordinal);

        /// <summary>Each file's declarations in its closed scopes, by name: seen from that file alone.</summary>
        private readonly Dictionary<string, Dictionary<string, List<Declaration>>> _closedByFile = new(StringComparer.Ordinal);

        /// <summary>Every type and namespace by its qualified name.</summary>
        private readonly Dictionary<string, List<Declaration>> _containers = new(StringComparer.Ordinal);

        /// <summary>Each scope's parent scope, by key, as the extracts declare or their containers place it.</summary>
        private readonly Dictionary<string, string?> _scopeParents = new(StringComparer.Ordinal);

        /// <summary>Each type's bases, by id, as its resolved base references give them.</summary>
        private readonly Dictionary<string, List<string>> _bases = new(StringComparer.Ordinal);

        /// <summary>The address roots every extract declares: a prefix to the directory it stands for, from the tree's root.</summary>
        private readonly List<(string Prefix, string Directory)> _roots = [];

        /// <summary>Each file's declarations at its top - in no node and no scope - by name: what a file imported by path makes visible of its own.</summary>
        private readonly Dictionary<CodeGraphFile, Dictionary<string, List<Declaration>>> _topByFile = new(ReferenceEqualityComparer.Instance);

        /// <summary>The containers a written name was found to be, from one file and scope: a name such as a type no file declares is looked for once.</summary>
        private readonly Dictionary<(string Path, string? Scope, string Written), IReadOnlyList<Declaration>> _containersFound = [];

        /// <summary>Every type's and scope's qualified name by the run of a name's characters it ends with: where an ending is looked for.</summary>
        private readonly Dictionary<string, List<string>> _byTrailing = new(StringComparer.Ordinal);

        /// <summary>The containers each written name ends, as the last way a qualifier is found: looked for once.</summary>
        private readonly Dictionary<string, IReadOnlyList<Declaration>> _endings = new(StringComparer.Ordinal);

        /// <summary>Every member declared to extend a type, by its name: what a call a type does not hold may be.</summary>
        private readonly Dictionary<string, List<Declaration>> _extensions = new(StringComparer.Ordinal);

        /// <summary>The types each extension extends, by id, as its own file names the type: found once.</summary>
        private readonly Dictionary<Declaration, IReadOnlyList<string>> _extended = new(ReferenceEqualityComparer.Instance);

        private readonly List<CodeEdge> _edges = [];
        private readonly Dictionary<string, int> _external = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _unresolved = new(StringComparer.Ordinal);
        private int _references;

        public Linking(IReadOnlyList<CodeGraphFile> files)
        {
            _files = files;
            _paths = files.Select(file => file.Path).ToHashSet(StringComparer.Ordinal);

            foreach (var file in files)
            {
                _byPath.TryAdd(file.Path, file);
                Add(_byFileName, CodeGraphIds.FileName(file.Path), file.Path);

                if (file.Extract is not { } extract)
                {
                    continue;
                }

                var owners = _owners[file] = new(StringComparer.Ordinal);
                var top = _topByFile[file] = new(StringComparer.Ordinal);
                var closed = extract.Scopes.Where(scope => !scope.Open).Select(scope => scope.Name).ToHashSet(StringComparer.Ordinal);

                foreach (var import in extract.Imports.Where(import => import.Global && file.Language is not null))
                {
                    Add(_global, file.Language!, (file, import));
                }

                foreach (var scope in extract.Scopes)
                {
                    _scopeParents.TryAdd(KeyOf(file, scope.Name, closed), scope.Parent is { } parent ? KeyOf(file, parent, closed) : null);
                }

                // A root that climbs out of the tree stands for nothing in it.
                foreach (var root in extract.AddressRoots)
                {
                    if (Normalize(Combine(DirectoryOf(file.Path), root.Directory)) is { } directory)
                    {
                        _roots.Add((root.Prefix, directory));
                    }
                }

                foreach (var node in extract.Nodes)
                {
                    // A type or a scope whose own scope is closed is its file's alone, as what it declares is.
                    var inClosed = node.Scope is { } declared && closed.Contains(declared);
                    var declaration = new Declaration(node, file, node.Scope is { } scope ? KeyOf(file, scope, closed) : null, inClosed || (IsContainer(node) && closed.Contains(node.Qualified)));
                    _declarations.Add(declaration);
                    Add(_byId, node.Id, declaration);
                    owners.TryAdd(node.Id, declaration);

                    if (node.Parent is null && node.Scope is null)
                    {
                        Add(top, node.Name, declaration);
                    }

                    if (declaration.ScopeKey is { } key)
                    {
                        Add(_byScope.TryGetValue(key, out var names) ? names : _byScope[key] = new(StringComparer.Ordinal), node.Name, declaration);
                    }

                    if (declaration.FileOnly)
                    {
                        Add(_closedByFile.TryGetValue(file.Path, out var names) ? names : _closedByFile[file.Path] = new(StringComparer.Ordinal), node.Name, declaration);
                    }
                    else
                    {
                        Add(_byName, node.Name, declaration);
                    }

                    if (node.Extends is not null)
                    {
                        Add(_extensions, node.Name, declaration);
                    }

                    if (IsContainer(node))
                    {
                        Add(_containers, node.Qualified, declaration);

                        // A container's members are put in the scope of its qualified name, whose parent is the scope the
                        // container itself is declared in, where no extract declares that scope with a parent of its own.
                        _scopeParents.TryAdd(KeyOf(file, node.Qualified, closed), declaration.ScopeKey);
                    }
                }
            }

            foreach (var qualified in _containers.Keys)
            {
                Add(_byTrailing, Trailing(qualified), qualified);
            }

            // The longest prefix first; two alike, in one order whatever order the files came in.
            _roots.Sort((one, other) => other.Prefix.Length != one.Prefix.Length
                ? other.Prefix.Length.CompareTo(one.Prefix.Length)
                : string.CompareOrdinal(one.Prefix, other.Prefix) is var byPrefix and not 0 ? byPrefix : string.CompareOrdinal(one.Directory, other.Directory));
        }

        public CodeGraphLinked Link()
        {
            Structure();

            // Bases first: a member is looked for in a type's bases, which their own references name.
            var references = _owners
                .SelectMany(file => file.Key.Extract!.References.Select(reference => (Declaration: file.Value[reference.From], Reference: reference)))
                .ToList();

            foreach (var (from, reference) in references.Where(pair => IsBase(pair.Reference.Kind)))
            {
                Resolve(from, reference);
            }

            // What a written name was found to be while the bases were still being found may be otherwise now.
            _containersFound.Clear();
            _extended.Clear();

            foreach (var (from, reference) in references.Where(pair => !IsBase(pair.Reference.Kind)))
            {
                Resolve(from, reference);
            }

            Imports();
            Dispatches();
            Declares();
            Tests();

            // Every edge once: one found more than one way is as sure as its surest finding.
            var edges = _edges.GroupBy(edge => (edge.From, edge.To, edge.Kind, edge.Line))
                .Select(alike => alike.First() with { Confidence = alike.Select(edge => edge.Confidence).Aggregate(CodeConfidences.Surer) })
                .ToList();
            var symbols = _files.Select(file => new CodeGraphSymbol(CodeGraphIds.FileId(file.Path), CodeNodeKinds.File, [file.Path]))
                .Concat(_byId.Select(pair => new CodeGraphSymbol(pair.Key, pair.Value[0].Node.Kind, [.. pair.Value.Select(declaration => declaration.File.Path).Distinct(StringComparer.Ordinal)])))
                .ToList();

            return new CodeGraphLinked(symbols, edges, _external, _unresolved, _references);
        }

        /// <summary>Every declaration's <c>contains</c> edge, from what it is declared inside: its parent node, or its file.</summary>
        private void Structure()
        {
            foreach (var declaration in _declarations)
            {
                _edges.Add(new CodeEdge(declaration.Node.Parent ?? CodeGraphIds.FileId(declaration.File.Path), declaration.Id, CodeEdgeKinds.Contains, declaration.Node.Start, CodeConfidences.Exact));
            }
        }

        private void Resolve(Declaration from, ExtractReference reference)
        {
            _references++;

            var (found, confidence) = Find(from, reference);

            if (found.Count == 0)
            {
                Count(_external, from.Id);
                return;
            }

            if (found.Count > CodeConfidences.MostCandidates)
            {
                Count(_unresolved, from.Id);
                return;
            }

            var sure = found.Count == 1 ? confidence : CodeConfidences.Candidates;

            foreach (var target in found)
            {
                var kind = reference.Kind == CodeEdgeKinds.Calls && target.Node.Kind == CodeNodeKinds.Type ? CodeEdgeKinds.Instantiates : reference.Kind;
                _edges.Add(new CodeEdge(from.Id, target.Id, kind, reference.Line, sure));

                if (IsBase(reference.Kind))
                {
                    Add(_bases, from.Id, target.Id);
                }
            }
        }

        /// <summary>
        /// What <paramref name="reference"/> names, one declaration for each id, and how sure the place it was found makes
        /// it: none where nothing the repository declares has that name.
        /// </summary>
        private (IReadOnlyList<Declaration> Found, string Confidence) Find(Declaration from, ExtractReference reference)
        {
            bool Accepts(Declaration target) => Takes(reference, target) && Sees(from.File, target);

            // A value of a type the code does not say: through the type of the member it is - nothing the repository
            // declares where that type is not the repository's - else by its name alone, where nothing is known of its type;
            // never in the scopes it is written in, as though it were a call of their own.
            if (reference.Qualifier is { Kind: CodeQualifierKinds.Value } value)
            {
                return value.Name is { } written && Values(from, written) is { } values
                    ? Through(from, values, reference.Name, Accepts)
                    : ByName(from, reference.Name, Accepts);
            }

            // The qualifier's scope: where the code says, and nowhere else - a callee the qualifier's type does not hold is
            // one the repository does not declare there, but for an extension of that type.
            if (reference.Qualifier is { } qualifier)
            {
                return Qualified(from, qualifier, reference.Name, Accepts);
            }

            return Scoped(from, reference.Name, Accepts) is { Count: > 0 } scoped ? (scoped, CodeConfidences.Exact) : Beyond(from, reference.Name, Accepts);
        }

        /// <summary>What the scopes <paramref name="from"/> is written in declare named <paramref name="name"/>, from the inside out, with each type's bases: the first that holds one decides.</summary>
        private IReadOnlyList<Declaration> Scoped(Declaration from, string name, Func<Declaration, bool> accepts)
        {
            foreach (var scope in Chain(from))
            {
                if (InScope(scope, name, accepts, withBases: true) is { Count: > 0 } found)
                {
                    return found;
                }
            }

            return [];
        }

        /// <summary>
        /// What <paramref name="name"/> is past the scopes <paramref name="from"/> is written in: a prefix written alone,
        /// what its import stands for; else what its file imports, in order, and what every file of its language imports;
        /// else the repository's.
        /// </summary>
        private (IReadOnlyList<Declaration> Found, string Confidence) Beyond(Declaration from, string name, Func<Declaration, bool> accepts)
        {
            if (Aliased(from.File, name).Where(accepts).ToList() is { Count: > 0 } aliased)
            {
                return (aliased, CodeConfidences.Exact);
            }

            foreach (var (import, owner) in ImportsOf(from.File).Where(made => made.Import.Prefix is null && made.Import.Lets(name)))
            {
                if (Imported(import, owner, name, accepts) is { Count: > 0 } imported)
                {
                    return (imported, CodeConfidences.Unique);
                }
            }

            return (Repository(from, name, accepts), CodeConfidences.Unique);
        }

        /// <summary>What the repository declares named <paramref name="name"/>: what any file can see, and what <paramref name="from"/>'s own file keeps to itself.</summary>
        private IReadOnlyList<Declaration> Repository(Declaration from, string name, Func<Declaration, bool> accepts)
            => Distinct((_byName.TryGetValue(name, out var anywhere) ? anywhere : [])
                .Concat(_closedByFile.TryGetValue(from.File.Path, out var own) && own.TryGetValue(name, out var kept) ? kept : [])
                .Where(accepts));

        /// <summary>What the qualifier of a reference names <paramref name="name"/> in, and how sure where it was found makes it.</summary>
        private (IReadOnlyList<Declaration> Found, string Confidence) Qualified(Declaration from, ExtractQualifier qualifier, string name, Func<Declaration, bool> accepts)
        {
            switch (qualifier.Kind)
            {
                case CodeQualifierKinds.This:
                    return EnclosingType(from) is { } self ? Typed(from, [self], null, name, accepts, withSelf: true) : ([], CodeConfidences.Exact);

                case CodeQualifierKinds.Base:
                    return EnclosingType(from) is { } derived ? Typed(from, [derived], null, name, accepts, withSelf: false) : ([], CodeConfidences.Exact);

                case CodeQualifierKinds.Prefix:
                    return (Distinct(ImportsOf(from.File)
                        .Where(made => string.Equals(made.Import.Prefix, qualifier.Name, StringComparison.Ordinal) && made.Import.Lets(name))
                        .SelectMany(made => Imported(made.Import, made.File, name, accepts))), CodeConfidences.Exact);

                case CodeQualifierKinds.Type:
                    return Typed(from, Containers(from, qualifier.Name!), qualifier.Name!, name, accepts, withSelf: true);

                default:
                    // A value the scopes hold, nearer than any type or scope of its name, is that value; a type or a scope is
                    // found as one is named; a name that is neither is every type or scope it ends.
                    var written = qualifier.Name!;

                    return Values(from, written) is { } values
                        ? Through(from, values, name, accepts)
                        : Typed(from, Containers(from, written), written, name, accepts, withSelf: true);
            }
        }

        /// <summary>
        /// What <paramref name="name"/> is in <paramref name="containers"/>, which <paramref name="written"/> names where a
        /// name does: a member of each type - itself where <paramref name="withSelf"/>, then its bases - or a declaration of
        /// each scope, exact; else an extension of one of those types that <paramref name="from"/> reaches, unique.
        /// </summary>
        private (IReadOnlyList<Declaration> Found, string Confidence) Typed(Declaration from, IReadOnlyList<Declaration> containers, string? written, string name, Func<Declaration, bool> accepts, bool withSelf)
        {
            var found = Distinct(containers.SelectMany(container => container.Node.Kind == CodeNodeKinds.Type
                ? InTypes([container], name, accepts, withSelf)
                : InScope(KeyOf(container.File, container.Node.Qualified), name, accepts, withBases: false)));

            return found.Count > 0 ? (found, CodeConfidences.Exact) : (Extensions(from, containers, written, name, accepts, withSelf), CodeConfidences.Unique);
        }

        /// <summary>
        /// The members named <paramref name="name"/> declared to extend a type, that <paramref name="from"/> reaches, which
        /// extend one of the types of <paramref name="containers"/> - itself where <paramref name="withSelf"/> - or one of
        /// their bases, as its own file names the type it extends; and, where none of <paramref name="containers"/> is, one
        /// extending a type the repository does not declare named as <paramref name="written"/> is.
        /// </summary>
        private IReadOnlyList<Declaration> Extensions(Declaration from, IReadOnlyList<Declaration> containers, string? written, string name, Func<Declaration, bool> accepts, bool withSelf)
        {
            if (!_extensions.TryGetValue(name, out var named))
            {
                return [];
            }

            var receivers = new HashSet<string>(StringComparer.Ordinal);
            var pending = new Queue<string>(containers.Where(container => container.Node.Kind == CodeNodeKinds.Type)
                .SelectMany(type => withSelf ? [type.Id] : BasesOf(type.Id)));

            while (pending.TryDequeue(out var id))
            {
                if (receivers.Add(id))
                {
                    foreach (var basis in BasesOf(id))
                    {
                        pending.Enqueue(basis);
                    }
                }
            }

            return Distinct(named.Where(extension => accepts(extension)
                && (Extended(extension) is { Count: > 0 } extended
                    ? extended.Any(receivers.Contains)
                    : containers.Count == 0 && written is not null && SimpleName(extension.Node.Extends!) == SimpleName(written))
                && Reaches(from, extension)));
        }

        /// <summary>The types <paramref name="extension"/> extends, by id, as its own file names the type: found once.</summary>
        private IReadOnlyList<string> Extended(Declaration extension)
        {
            if (!_extended.TryGetValue(extension, out var extended))
            {
                _extended[extension] = extended = [.. Containers(extension, extension.Node.Extends!)
                    .Where(container => container.Node.Kind == CodeNodeKinds.Type)
                    .Select(type => type.Id)
                    .Distinct(StringComparer.Ordinal)];
            }

            return extended;
        }

        /// <summary>
        /// Whether <paramref name="from"/> reaches <paramref name="declaration"/>: the scope it is declared in, or the one
        /// that scope is declared in, is one <paramref name="from"/> is written in or its file imports, or is at the top of
        /// none; or a file its file imports by path declares it. One in a closed scope is reached from its own file alone.
        /// </summary>
        private bool Reaches(Declaration from, Declaration declaration)
        {
            if (!Sees(from.File, declaration))
            {
                return false;
            }

            if (declaration.ScopeKey is not { } key)
            {
                return true;
            }

            var holders = new HashSet<string>(StringComparer.Ordinal) { key };

            if (_scopeParents.TryGetValue(key, out var parent))
            {
                if (parent is null)
                {
                    return true;
                }

                holders.Add(parent);
            }

            return Chain(from).Any(holders.Contains)
                || ImportsOf(from.File).Any(made => made.Import.Form == CodeImportForms.Scope
                    ? holders.Contains(OpenKey(made.Import.Target))
                    : FilesOf(made.Import, made.File).Contains(declaration.File.Path, StringComparer.Ordinal));
        }

        /// <summary>
        /// A call of <paramref name="name"/> on the value each of <paramref name="values"/> is: through the type each is
        /// declared with; by its name alone where one does not say its type.
        /// </summary>
        private (IReadOnlyList<Declaration> Found, string Confidence) Through(Declaration from, IReadOnlyList<Declaration> values, string name, Func<Declaration, bool> accepts)
            => values.Any(value => value.Node.ValueType is null)
                ? ByName(from, name, accepts)
                : Of(from, values, name, accepts);

        /// <summary>A member of a value whose type nothing says, by its name in the repository alone: never a type, which no call on a value makes.</summary>
        private (IReadOnlyList<Declaration> Found, string Confidence) ByName(Declaration from, string name, Func<Declaration, bool> accepts)
            => (Repository(from, name, target => accepts(target) && target.Node.Kind != CodeNodeKinds.Type), CodeConfidences.Unique);

        /// <summary>What <paramref name="name"/> is in the type each of <paramref name="values"/> is declared with, as that member's own file names it, as sure as the least sure.</summary>
        private (IReadOnlyList<Declaration> Found, string Confidence) Of(Declaration from, IReadOnlyList<Declaration> values, string name, Func<Declaration, bool> accepts)
        {
            var each = values.Select(value => Typed(from, Containers(value, value.Node.ValueType!), value.Node.ValueType!, name, accepts, withSelf: true)).ToList();

            return (Distinct(each.SelectMany(typed => typed.Found)), each.Where(typed => typed.Found.Count > 0).Select(typed => typed.Confidence).Aggregate(CodeConfidences.Exact, CodeConfidences.Weaker));
        }

        /// <summary>
        /// The values <paramref name="written"/>, a chain of names, comes to from where <paramref name="from"/> stands: its
        /// first name a value the scopes it is written in hold - nearest first, with each type's bases, and none past one
        /// that declares a type or a scope of that name - or a type's, whose value its second name is; each next one a value
        /// of the type the one before is declared with; or the values of the first link whose type is not said, which a
        /// call is looked for through by its name alone. <see langword="null"/> where its first name is no value; none where
        /// a link's type is not the repository's, or holds no value of the next name.
        /// </summary>
        private IReadOnlyList<Declaration>? Values(Declaration from, string written)
        {
            var parts = written.Split('.');
            IReadOnlyList<Declaration> values = [.. Scoped(from, parts[0], declaration => (IsValue(declaration) || IsContainer(declaration)) && Sees(from.File, declaration)).Where(IsValue)];
            var at = 1;

            // A chain that begins at a type - `Cache.Instance` - begins at that type's value.
            if (values.Count == 0 && parts.Length > 1)
            {
                var types = Distinct(NamedContainers(from, parts[0]).Where(container => container.Node.Kind == CodeNodeKinds.Type));
                values = types.Count > 0 ? InTypes(types, parts[1], IsValue, withSelf: true) : [];
                at = 2;
            }

            if (values.Count == 0)
            {
                return null;
            }

            for (; at < parts.Length; at++)
            {
                if (values.Any(value => value.Node.ValueType is null))
                {
                    return values;
                }

                if ((values = Of(from, values, parts[at], IsValue).Found).Count == 0)
                {
                    return [];
                }
            }

            return values;
        }

        /// <summary>
        /// The types and scopes <paramref name="written"/> names from where <paramref name="from"/> stands: those
        /// <see cref="NamedContainers"/> finds, else every one whose qualified name ends with it.
        /// </summary>
        private IReadOnlyList<Declaration> Containers(Declaration from, string written)
            => NamedContainers(from, written) is { Count: > 0 } named ? named : Ending(from, written);

        /// <summary>
        /// The types and scopes <paramref name="written"/> names from where <paramref name="from"/> stands, nearest first:
        /// one the scopes it is written in declare; else the one whose qualified name it is; else what it is past those
        /// scopes - an alias it is or begins with, an import's, the repository's.
        /// </summary>
        private IReadOnlyList<Declaration> NamedContainers(Declaration from, string written)
        {
            // What a name is from one place it is the same from every declaration there: found once.
            var key = (from.File.Path, from.ScopeKey, written);

            if (_containersFound.TryGetValue(key, out var known))
            {
                return known;
            }

            bool Seen(Declaration container) => IsContainer(container) && Sees(from.File, container);

            return _containersFound[key] = Scoped(from, written, Seen) is { Count: > 0 } scoped ? scoped
                : _containers.TryGetValue(written, out var whole) && Distinct(whole.Where(Seen)) is { Count: > 0 } named ? named
                : Beyond(from, written, Seen).Found;
        }

        /// <summary>Every type and scope <paramref name="from"/>'s file sees whose qualified name ends with <paramref name="written"/> at a name's boundary: found once for each.</summary>
        private IReadOnlyList<Declaration> Ending(Declaration from, string written)
        {
            if (!_endings.TryGetValue(written, out var ending))
            {
                _endings[written] = ending = Distinct((_byTrailing.TryGetValue(Trailing(written), out var named) ? named : [])
                    .Where(qualified => EndsWithName(qualified, written))
                    .SelectMany(qualified => _containers[qualified]));
            }

            return ending.Any(container => container.FileOnly) ? [.. ending.Where(container => Sees(from.File, container))] : ending;
        }

        /// <summary>
        /// The types and scopes <paramref name="written"/> stands for in <paramref name="file"/> through an alias: the
        /// target of each scope import it sees - its own, and what every file of its language imports - whose prefix is
        /// that name, or is its first part and the rest of it names what is inside the target. The prefix names the scope
        /// itself, so it is found wherever a type's or a scope's name is written.
        /// </summary>
        private IReadOnlyList<Declaration> Aliased(CodeGraphFile file, string written)
        {
            var dot = written.IndexOf('.', StringComparison.Ordinal);
            var head = dot < 0 ? written : written[..dot];
            var rest = dot < 0 ? string.Empty : written[dot..];

            return Distinct(ImportsOf(file)
                .Where(made => made.Import.Form == CodeImportForms.Scope && string.Equals(made.Import.Prefix, head, StringComparison.Ordinal))
                .SelectMany(made => _containers.TryGetValue(made.Import.Target + rest, out var stands) ? stands : []));
        }

        /// <summary>The scopes a reference made in <paramref name="from"/> is looked for in, from the inside out.</summary>
        private IEnumerable<string> Chain(Declaration from)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var key = from.ScopeKey;

            while (key is not null && seen.Add(key))
            {
                yield return key;
                key = _scopeParents.TryGetValue(key, out var parent) ? parent : null;
            }
        }

        /// <summary>What the scope <paramref name="key"/> declares named <paramref name="name"/>, and - where it is a type's, and <paramref name="withBases"/> - what its bases do.</summary>
        private IReadOnlyList<Declaration> InScope(string key, string name, Func<Declaration, bool> accepts, bool withBases)
        {
            var found = Distinct(_byScope.TryGetValue(key, out var names) && names.TryGetValue(name, out var declared) ? declared.Where(accepts) : []);

            if (found.Count > 0 || !withBases)
            {
                return found;
            }

            var types = _containers.TryGetValue(QualifiedOf(key), out var containers)
                ? containers.Where(container => container.Node.Kind == CodeNodeKinds.Type && KeyOf(container.File, container.Node.Qualified) == key).ToList()
                : [];

            return types.Count > 0 ? InTypes(types, name, accepts, withSelf: false) : [];
        }

        /// <summary>
        /// What <paramref name="types"/> declare named <paramref name="name"/> - each itself where <paramref name="withSelf"/>,
        /// then its bases, nearest first: the first that holds one decides.
        /// </summary>
        private IReadOnlyList<Declaration> InTypes(IReadOnlyList<Declaration> types, string name, Func<Declaration, bool> accepts, bool withSelf)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var level = withSelf ? types.Select(type => type.Id).ToList() : [.. types.SelectMany(type => BasesOf(type.Id))];

            while (level.Count > 0)
            {
                var found = Distinct(level.Where(seen.Add)
                    .SelectMany(id => _byId.TryGetValue(id, out var parts) ? parts : [])
                    .SelectMany(type => _byScope.TryGetValue(KeyOf(type.File, type.Node.Qualified), out var names) && names.TryGetValue(name, out var declared) ? declared : [])
                    .Where(accepts));

                if (found.Count > 0)
                {
                    return found;
                }

                level = [.. level.SelectMany(BasesOf).Where(id => !seen.Contains(id))];
            }

            return [];
        }

        private IEnumerable<string> BasesOf(string id) => _bases.TryGetValue(id, out var bases) ? bases : [];

        /// <summary>What <paramref name="import"/>, made by <paramref name="file"/>, makes visible named <paramref name="name"/>.</summary>
        private IReadOnlyList<Declaration> Imported(ExtractImport import, CodeGraphFile file, string name, Func<Declaration, bool> accepts)
        {
            if (import.Form == CodeImportForms.Scope)
            {
                return InScope(OpenKey(import.Target), name, accepts, withBases: true);
            }

            return Distinct(FilesOf(import, file).SelectMany(target => Visible(target, name)).Where(accepts));
        }

        /// <summary>What a file imported by path makes visible named <paramref name="name"/>: its own top-level declarations, and those of the open scopes it declares.</summary>
        private IEnumerable<Declaration> Visible(string path, string name)
        {
            var file = _byPath[path];

            if (file.Extract is not { } extract)
            {
                return [];
            }

            var top = _topByFile[file].TryGetValue(name, out var named) ? named : [];
            var open = extract.Scopes.Where(scope => scope.Open)
                .SelectMany(scope => _byScope.TryGetValue(OpenKey(scope.Name), out var names) && names.TryGetValue(name, out var declared) ? declared : []);

            return top.Concat(open);
        }

        /// <summary>The files of the tree <paramref name="import"/> names, by its form: found once for each import of each file.</summary>
        private IReadOnlyList<string> FilesOf(ExtractImport import, CodeGraphFile file)
        {
            var known = _imported.TryGetValue(file, out var ofFile) ? ofFile : _imported[file] = new(ReferenceEqualityComparer.Instance);

            if (!known.TryGetValue(import, out var files))
            {
                known[import] = files = FindFiles(import, file);
            }

            return files;
        }

        private IReadOnlyList<string> FindFiles(ExtractImport import, CodeGraphFile file)
        {
            switch (import.Form)
            {
                case CodeImportForms.Relative:
                    var relative = Normalize(Combine(DirectoryOf(file.Path), import.Target));
                    return relative is { } path && _paths.Contains(path) ? [path] : [];

                case CodeImportForms.Address:
                    foreach (var (prefix, directory) in _roots)
                    {
                        if (import.Target.StartsWith(prefix, StringComparison.Ordinal))
                        {
                            var addressed = Normalize(Combine(directory, import.Target[prefix.Length..]));
                            return addressed is { } found && _paths.Contains(found) ? [found] : [];
                        }
                    }

                    return [];

                default:
                    var ending = Normalize(import.Target);

                    return ending is null || !_byFileName.TryGetValue(CodeGraphIds.FileName(ending), out var named)
                        ? []
                        : [.. named.Where(path => path == ending || path.EndsWith("/" + ending, StringComparison.Ordinal)).Order(StringComparer.Ordinal)];
            }
        }

        /// <summary>
        /// What <paramref name="file"/> imports, in order, and then what every file of its language imports - each with the
        /// file that made it, which an import by a relative path is read from.
        /// </summary>
        private IEnumerable<(ExtractImport Import, CodeGraphFile File)> ImportsOf(CodeGraphFile file)
            => file.Extract!.Imports.Select(import => (import, file)).Concat(file.Language is { } language && _global.TryGetValue(language, out var global)
                ? global.Where(made => !ReferenceEquals(made.File, file)).Select(made => (made.Import, made.File))
                : []);

        /// <summary>Each file's <c>imports</c> edges: to a scope the repository declares, and to each file a path names.</summary>
        private void Imports()
        {
            foreach (var file in _files.Where(file => file.Extract is not null))
            {
                var from = CodeGraphIds.FileId(file.Path);

                foreach (var import in file.Extract!.Imports)
                {
                    if (import.Form == CodeImportForms.Scope)
                    {
                        foreach (var id in (_containers.TryGetValue(import.Target, out var containers) ? containers : []).Where(container => Sees(file, container)).Select(container => container.Id).Distinct(StringComparer.Ordinal))
                        {
                            _edges.Add(new CodeEdge(from, id, CodeEdgeKinds.Imports, import.Line, CodeConfidences.Exact));
                        }

                        continue;
                    }

                    var targets = FilesOf(import, file);

                    if (targets.Count > CodeConfidences.MostCandidates)
                    {
                        Count(_unresolved, from);
                        continue;
                    }

                    var sure = import.Form == CodeImportForms.Ending
                        ? targets.Count == 1 ? CodeConfidences.Unique : CodeConfidences.Candidates
                        : CodeConfidences.Exact;

                    foreach (var target in targets)
                    {
                        _edges.Add(new CodeEdge(from, CodeGraphIds.FileId(target), CodeEdgeKinds.Imports, import.Line, sure));
                    }
                }
            }
        }

        /// <summary>
        /// Each <c>dispatches</c> edge: from a member of an interface a type implements - itself or through a base, and of
        /// every interface that one extends - to the member that answers it for that type. What answers is a member with a
        /// body, of that kind, name and arity: the type's own, else its nearest base's it inherits or mixes in - never an
        /// interface's own, which has none. Each is as sure as the least sure edge on its way, up from the type to the
        /// interface and to the base whose member answers; <c>candidates</c> where several bases answer alike.
        /// </summary>
        private void Dispatches()
        {
            var bases = _edges.Where(edge => IsBase(edge.Kind)).ToLookup(edge => edge.From, StringComparer.Ordinal);

            foreach (var type in bases.Select(group => group.Key).ToList())
            {
                foreach (var (owner, way) in Implemented(type, bases))
                {
                    foreach (var member in MembersOf(owner))
                    {
                        foreach (var (answer, found) in Answering(type, member, bases))
                        {
                            _edges.Add(new CodeEdge(member.Id, answer.Id, CodeEdgeKinds.Dispatches, answer.Node.Start, CodeConfidences.Weaker(way, found)));
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Every interface <paramref name="type"/> is one of - each it or a base of it implements, and each that one extends -
        /// with how sure the surest way up to it is: as sure as that way's least sure edge.
        /// </summary>
        private static Dictionary<string, string> Implemented(string type, ILookup<string, CodeEdge> bases)
        {
            // A way is a type and whether an implements edge was crossed to reach it; each is taken again only where it is
            // reached surer than before, so a cycle of bases ends.
            var surest = new Dictionary<(string Id, bool Crossed), string> { [(type, false)] = CodeConfidences.Exact };
            var pending = new Queue<(string Id, bool Crossed)>([(type, false)]);

            while (pending.TryDequeue(out var at))
            {
                foreach (var edge in bases[at.Id])
                {
                    var next = (edge.To, at.Crossed || edge.Kind == CodeEdgeKinds.Implements);
                    var sure = CodeConfidences.Weaker(surest[at], edge.Confidence);

                    if (!surest.TryGetValue(next, out var known) || CodeConfidences.Surer(sure, known) != known)
                    {
                        surest[next] = sure;
                        pending.Enqueue(next);
                    }
                }
            }

            return surest.Where(way => way.Key.Crossed && way.Key.Id != type).ToDictionary(way => way.Key.Id, way => way.Value, StringComparer.Ordinal);
        }

        /// <summary>
        /// What answers <paramref name="member"/> for <paramref name="type"/>, each with how sure it is: a member with a body,
        /// of its kind, name and arity, of the type itself, else of its nearest bases it inherits or mixes in - as sure as the
        /// least sure edge up to that base, and <c>candidates</c> where several answer alike; none past
        /// <see cref="CodeConfidences.MostCandidates"/>.
        /// </summary>
        private List<(Declaration Answer, string Sure)> Answering(string type, Declaration member, ILookup<string, CodeEdge> bases)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal) { type };
            var level = new Dictionary<string, string>(StringComparer.Ordinal) { [type] = CodeConfidences.Exact };

            while (level.Count > 0)
            {
                var found = level
                    .SelectMany(at => (_byId.TryGetValue(at.Key, out var parts) ? parts : [])
                        .SelectMany(part => _byScope.TryGetValue(KeyOf(part.File, part.Node.Qualified), out var names) && names.TryGetValue(member.Node.Name, out var declared) ? declared : [])
                        .Where(candidate => candidate.Node.Kind == CodeNodeKinds.Member
                            && !candidate.Node.Prototype
                            && TakeAlike(candidate.Node, member.Node))
                        .Select(answer => (Answer: answer, Sure: at.Value)))
                    .GroupBy(answer => answer.Answer.Id, StringComparer.Ordinal)
                    .Select(alike => (alike.First().Answer, Sure: alike.Select(answer => answer.Sure).Aggregate(CodeConfidences.Surer)))
                    .ToList();

                if (found.Count > 0)
                {
                    return found.Count == 1 ? found
                        : found.Count <= CodeConfidences.MostCandidates ? [.. found.Select(answer => (answer.Answer, CodeConfidences.Candidates))]
                        : [];
                }

                var next = new Dictionary<string, string>(StringComparer.Ordinal);

                foreach (var (at, sure) in level)
                {
                    foreach (var edge in bases[at].Where(edge => edge.Kind is CodeEdgeKinds.Inherits or CodeEdgeKinds.MixesIn && !seen.Contains(edge.To)))
                    {
                        var way = CodeConfidences.Weaker(sure, edge.Confidence);
                        next[edge.To] = next.TryGetValue(edge.To, out var known) ? CodeConfidences.Surer(known, way) : way;
                    }
                }

                seen.UnionWith(next.Keys);
                level = next;
            }

            return [];
        }

        /// <summary>The members the type <paramref name="id"/> declares, each once.</summary>
        private IReadOnlyList<Declaration> MembersOf(string id)
            => Distinct((_byId.TryGetValue(id, out var parts) ? parts : [])
                .SelectMany(type => _byScope.TryGetValue(KeyOf(type.File, type.Node.Qualified), out var names) ? names.Values.SelectMany(declared => declared) : [])
                .Where(member => member.Node.Kind == CodeNodeKinds.Member));

        /// <summary>Each <c>declares</c> edge: from a declaration with no body to each one with a body, the same qualified name and arity, of its language, that it can see.</summary>
        private void Declares()
        {
            var definitions = _declarations.Where(declaration => !declaration.Node.Prototype)
                .ToLookup(declaration => (declaration.File.Language, declaration.Node.Qualified));

            foreach (var prototype in _declarations.Where(declaration => declaration.Node.Prototype))
            {
                var bodies = Distinct(definitions[(prototype.File.Language, prototype.Node.Qualified)]
                    .Where(body => body.Id != prototype.Id && TakeAlike(body.Node, prototype.Node) && Sees(prototype.File, body)));

                if (bodies.Count is > 0 and <= CodeConfidences.MostCandidates)
                {
                    foreach (var body in bodies)
                    {
                        _edges.Add(new CodeEdge(prototype.Id, body.Id, CodeEdgeKinds.Declares, prototype.Node.Start, bodies.Count == 1 ? CodeConfidences.Unique : CodeConfidences.Candidates));
                    }
                }
            }
        }

        /// <summary>Each <c>tests</c> edge: from a test to each member it calls directly, as sure as the call.</summary>
        private void Tests()
        {
            var tests = _declarations.Where(declaration => declaration.Node.Test).Select(declaration => declaration.Id).ToHashSet(StringComparer.Ordinal);

            foreach (var call in _edges.Where(edge => edge.Kind == CodeEdgeKinds.Calls && tests.Contains(edge.From)).ToList())
            {
                _edges.Add(call with { Kind = CodeEdgeKinds.Tests });
            }
        }

        /// <summary>The type <paramref name="from"/> is declared in, or is; none where what holds it never reaches one.</summary>
        private Declaration? EnclosingType(Declaration from)
        {
            var at = from;
            var seen = new HashSet<string>(StringComparer.Ordinal);

            while (at.Node.Kind != CodeNodeKinds.Type)
            {
                if (at.Node.Parent is not { } parent || !seen.Add(parent) || !_owners[from.File].TryGetValue(parent, out var holder))
                {
                    return null;
                }

                at = holder;
            }

            return at;
        }

        /// <summary>
        /// The scope <paramref name="name"/> is from <paramref name="file"/>: that file's own where it declares it closed, and
        /// the one every file shares otherwise.
        /// </summary>
        private string KeyOf(CodeGraphFile file, string name)
            => file.Extract is { } extract && extract.Scopes.Any(scope => !scope.Open && scope.Name == name) ? ClosedKey(file.Path, name) : OpenKey(name);

        /// <summary>Whether <paramref name="file"/> sees <paramref name="declaration"/>: one that is its file's alone is seen from that file alone.</summary>
        private static bool Sees(CodeGraphFile file, Declaration declaration) => !declaration.FileOnly || ReferenceEquals(declaration.File, file);

        private static string KeyOf(CodeGraphFile file, string name, HashSet<string> closed)
            => closed.Contains(name) ? ClosedKey(file.Path, name) : OpenKey(name);

        private const string OpenPrefix = "open\u0000";
        private const string ClosedPrefix = "closed\u0000";

        private static string OpenKey(string name) => OpenPrefix + name;

        private static string ClosedKey(string path, string name) => $"{ClosedPrefix}{path}\u0000{name}";

        /// <summary>The qualified name a scope's key stands for.</summary>
        private static string QualifiedOf(string key) => key[(key.LastIndexOf('\u0000') + 1)..];
    }

    /// <summary>
    /// Whether two declarations take the same parameters, as far as their counts say: both called or neither, and the same
    /// most arguments - a default one gives and the other does not changes no parameter.
    /// </summary>
    private static bool TakeAlike(ExtractNode one, ExtractNode other)
        => (one.MinArity is null) == (other.MinArity is null) && one.MaxArity == other.MaxArity;

    /// <summary>Whether a reference of <paramref name="kind"/> names a base of the type it is made in.</summary>
    private static bool IsBase(string kind) => kind is CodeEdgeKinds.Inherits or CodeEdgeKinds.Implements or CodeEdgeKinds.MixesIn;

    /// <summary>Whether <paramref name="target"/> is something <paramref name="reference"/> can name: of a kind its edge reaches, and taking its arity.</summary>
    private static bool Takes(ExtractReference reference, Declaration target)
        => reference.Kind switch
        {
            CodeEdgeKinds.Calls => (target.Node.Kind == CodeNodeKinds.Member && target.Node.TakesArity(reference.Arity)) || target.Node.Kind == CodeNodeKinds.Type,
            CodeEdgeKinds.Overrides => target.Node is { Kind: CodeNodeKinds.Member, Extends: null } && target.Node.TakesArity(reference.Arity),
            CodeEdgeKinds.ProjectReference => target.Node.Kind == CodeNodeKinds.Project,
            _ => target.Node.Kind == CodeNodeKinds.Type,
        };

    /// <summary>Whether <paramref name="declaration"/> is a type or a scope: what a qualifier may name to be looked in.</summary>
    private static bool IsContainer(Declaration declaration) => IsContainer(declaration.Node);

    private static bool IsContainer(ExtractNode node) => node.Kind is CodeNodeKinds.Type or CodeNodeKinds.Namespace;

    /// <summary>Whether <paramref name="declaration"/> is a value: a member no call is made of by its arguments - a field, a property, an event.</summary>
    private static bool IsValue(Declaration declaration)
        => declaration.Node is { Kind: CodeNodeKinds.Member, MinArity: null, MaxArity: null };

    /// <summary>One declaration of each id among <paramref name="declarations"/>: a type of several parts is one target.</summary>
    private static IReadOnlyList<Declaration> Distinct(IEnumerable<Declaration> declarations)
        => [.. declarations.DistinctBy(declaration => declaration.Id, StringComparer.Ordinal)];

    private static void Add<T>(Dictionary<string, List<T>> into, string key, T value)
    {
        if (!into.TryGetValue(key, out var values))
        {
            into[key] = values = [];
        }

        values.Add(value);
    }

    private static void Count(Dictionary<string, int> counts, string id)
        => counts[id] = counts.GetValueOrDefault(id) + 1;

    /// <summary>Whether <paramref name="qualified"/> ends with <paramref name="written"/> at a name's boundary.</summary>
    private static bool EndsWithName(string qualified, string written)
        => qualified.Length > written.Length
            && qualified.EndsWith(written, StringComparison.Ordinal)
            && !IsNamePart(qualified[qualified.Length - written.Length - 1]);

    /// <summary>
    /// The run of a name's characters <paramref name="name"/> ends with: what every qualified name that ends with it at a
    /// name's boundary ends with too.
    /// </summary>
    private static string Trailing(string name)
    {
        var start = name.Length;

        while (start > 0 && IsNamePart(name[start - 1]))
        {
            start--;
        }

        return name[start..];
    }

    /// <summary>Whether <paramref name="character"/> is one a name is made of, and no boundary between two.</summary>
    private static bool IsNamePart(char character) => char.IsLetterOrDigit(character) || character is '_' or '$';

    /// <summary>The last part of a dotted name: a type's own name, as an extension names the type it extends.</summary>
    private static string SimpleName(string written) => written[(written.LastIndexOf('.') + 1)..];

    private static string DirectoryOf(string path) => path.LastIndexOf('/') is var slash and >= 0 ? path[..slash] : string.Empty;

    private static string Combine(string directory, string relative) => directory.Length == 0 ? relative : $"{directory}/{relative}";

    /// <summary><paramref name="path"/> with its <c>.</c> and <c>..</c> taken out; <see langword="null"/> where it climbs out of the tree.</summary>
    private static string? Normalize(string path)
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
                if (parts.Count == 0)
                {
                    return null;
                }

                parts.RemoveAt(parts.Count - 1);
                continue;
            }

            parts.Add(part);
        }

        return string.Join('/', parts);
    }
}

/// <summary>One file of a tree, as the linker is given it.</summary>
/// <param name="Path">Its path, relative to the tree's root with forward separators.</param>
/// <param name="Language">The implementation that read it; <see langword="null"/> where none did.</param>
/// <param name="Extract">Its extract, where its declarations are kept; <see langword="null"/> where the file is a node and nothing more.</param>
public sealed record CodeGraphFile(string Path, string? Language, FileExtract? Extract);

/// <summary>One directed edge of a graph.</summary>
/// <param name="From">The id of the node it leaves.</param>
/// <param name="To">The id of the node it reaches.</param>
/// <param name="Kind">One of <see cref="CodeEdgeKinds.All"/>.</param>
/// <param name="Line">The line of the source file it was seen at.</param>
/// <param name="Confidence">How sure it is: one of <see cref="CodeConfidences.All"/>.</param>
public sealed record CodeEdge(string From, string To, string Kind, int Line, string Confidence);

/// <summary>One node of a linked graph: its id, its kind and every file a part of it is declared in.</summary>
/// <param name="Id">Its id.</param>
/// <param name="Kind">One of <see cref="CodeNodeKinds"/>.</param>
/// <param name="Paths">The files that declare it: one, or each of a type's parts.</param>
public sealed record CodeGraphSymbol(string Id, string Kind, IReadOnlyList<string> Paths);

/// <summary>What linking one tree gave.</summary>
/// <param name="Symbols">Every node: each file's, and each declaration's, a declaration of several parts once.</param>
/// <param name="Edges">Every edge, each once.</param>
/// <param name="External">For each node, how many of its references named nothing the repository declares.</param>
/// <param name="Unresolved">For each node, how many of its references named more declarations than an edge lists.</param>
/// <param name="References">How many references were resolved.</param>
public sealed record CodeGraphLinked(
    IReadOnlyList<CodeGraphSymbol> Symbols,
    IReadOnlyList<CodeEdge> Edges,
    IReadOnlyDictionary<string, int> External,
    IReadOnlyDictionary<string, int> Unresolved,
    int References);
