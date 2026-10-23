namespace DssHarness.Core.Codebase;

/// <summary>
/// The one linker: turns the references every extract keeps, by name, into directed edges, for every language alike. It
/// knows no language - what differs between them is what their extracts say their scopes and imports are, never a rule
/// of this.
/// </summary>
/// <remarks>
/// <para>
/// Each reference is looked for in the qualifier's scope where it has one - a type and its bases, a scope, an import's
/// prefix - then in the scopes it is written in, from the inside out, then in what its file imports, in order, each
/// import's filter applied, then in the repository by name and arity. The first place that holds the name decides: one
/// declaration is an edge <c>exact</c> where the code itself said where to look (a qualifier, an enclosing scope) and
/// <c>unique</c> where it did not; two to <see cref="CodeConfidences.MostCandidates"/> are an edge to each, of
/// <c>candidates</c>; more are counted unresolved; none is counted external, a callee the repository does not declare.
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
    public const int Revision = 1;

    /// <summary>Links <paramref name="files"/>, one tree's every file, into its symbols and edges.</summary>
    /// <param name="files">Every file the graph keeps, each once: those whose declarations are kept with their extracts.</param>
    public static CodeGraphLinked Link(IReadOnlyList<CodeGraphFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        return new Linking(files).Link();
    }

    /// <summary>One declaration of one file, placed.</summary>
    private sealed record Declaration(ExtractNode Node, CodeGraphFile File, string? ScopeKey)
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
                Add(_byFileName, file.Path[(file.Path.LastIndexOf('/') + 1)..], file.Path);

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
                    var declaration = new Declaration(node, file, node.Scope is { } scope ? KeyOf(file, scope, closed) : null);
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

                    if (node.Scope is { } declared && closed.Contains(declared))
                    {
                        Add(_closedByFile.TryGetValue(file.Path, out var names) ? names : _closedByFile[file.Path] = new(StringComparer.Ordinal), node.Name, declaration);
                    }
                    else
                    {
                        Add(_byName, node.Name, declaration);
                    }

                    if (node.Kind is CodeNodeKinds.Type or CodeNodeKinds.Namespace)
                    {
                        Add(_containers, node.Qualified, declaration);

                        // A container's members are put in the scope of its qualified name, whose parent is the scope the
                        // container itself is declared in, where no extract declares that scope with a parent of its own.
                        _scopeParents.TryAdd(KeyOf(file, node.Qualified, closed), declaration.ScopeKey);
                    }
                }
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
            bool Accepts(Declaration target) => Takes(reference, target);

            // The qualifier's scope: where the code says, and nowhere else - a callee the qualifier's type does not hold is
            // one the repository does not declare there.
            if (reference.Qualifier is { } qualifier)
            {
                return (Qualified(from, qualifier, reference.Name, Accepts), CodeConfidences.Exact);
            }

            // The scopes it is written in, from the inside out, with each type's bases.
            foreach (var scope in Chain(from))
            {
                if (InScope(scope, reference.Name, Accepts, withBases: true) is { Count: > 0 } inScope)
                {
                    return (inScope, CodeConfidences.Exact);
                }
            }

            // What its file imports, in order, and what every file of its language imports.
            foreach (var (import, owner) in ImportsOf(from.File).Where(made => made.Import.Prefix is null && made.Import.Lets(reference.Name)))
            {
                if (Imported(import, owner, reference.Name, Accepts) is { Count: > 0 } imported)
                {
                    return (imported, CodeConfidences.Unique);
                }
            }

            // The repository, by name and arity: what any file can see, and what its own file keeps to itself.
            var named = (_byName.TryGetValue(reference.Name, out var anywhere) ? anywhere : [])
                .Concat(_closedByFile.TryGetValue(from.File.Path, out var own) && own.TryGetValue(reference.Name, out var kept) ? kept : []);

            return (Distinct(named.Where(Accepts)), CodeConfidences.Unique);
        }

        /// <summary>What the qualifier of a reference names <paramref name="name"/> in.</summary>
        private IReadOnlyList<Declaration> Qualified(Declaration from, ExtractQualifier qualifier, string name, Func<Declaration, bool> accepts)
        {
            switch (qualifier.Kind)
            {
                case CodeQualifierKinds.This:
                    return EnclosingType(from) is { } self ? InTypes([self], name, accepts, withSelf: true) : [];

                case CodeQualifierKinds.Base:
                    return EnclosingType(from) is { } derived ? InTypes([derived], name, accepts, withSelf: false) : [];

                case CodeQualifierKinds.Prefix:
                    return Distinct(ImportsOf(from.File)
                        .Where(made => string.Equals(made.Import.Prefix, qualifier.Name, StringComparison.Ordinal) && made.Import.Lets(name))
                        .SelectMany(made => Imported(made.Import, made.File, name, accepts)));

                default:
                    return Distinct(Containers(from, qualifier.Name!).SelectMany(container => container.Node.Kind == CodeNodeKinds.Type
                        ? InTypes([container], name, accepts, withSelf: true)
                        : InScope(KeyOf(container.File, container.Node.Qualified), name, accepts, withBases: false)));
            }
        }

        /// <summary>
        /// The types and scopes <paramref name="written"/> names from where <paramref name="from"/> stands: the one whose
        /// qualified name it is; else those of that name its scopes, its imports or the repository hold, as a reference
        /// finds a name; else every one whose qualified name ends with it.
        /// </summary>
        private IReadOnlyList<Declaration> Containers(Declaration from, string written)
        {
            if (_containers.TryGetValue(written, out var exact))
            {
                return Distinct(exact);
            }

            // What a name is from one place it is the same from every declaration there: found once.
            var key = (from.File.Path, from.ScopeKey, written);

            if (_containersFound.TryGetValue(key, out var known))
            {
                return known;
            }

            var reference = new ExtractReference(from.Id, CodeEdgeKinds.References, written, from.Node.Start);
            var (found, _) = Find(from, reference with { Kind = ContainerKind });

            return _containersFound[key] = found.Count > 0
                ? found
                : Distinct(_containers.Where(pair => EndsWithName(pair.Key, written)).SelectMany(pair => pair.Value));
        }

        /// <summary>A name to look for a container by: the kind a reference is given while its qualifier is resolved.</summary>
        private const string ContainerKind = "container";

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

                    return ending is null || !_byFileName.TryGetValue(ending[(ending.LastIndexOf('/') + 1)..], out var named)
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
                        foreach (var id in (_containers.TryGetValue(import.Target, out var containers) ? containers : []).Select(container => container.Id).Distinct(StringComparer.Ordinal))
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
                            && candidate.Node.MinArity == member.Node.MinArity
                            && candidate.Node.MaxArity == member.Node.MaxArity)
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
                    .Where(body => body.Id != prototype.Id
                        && body.Node.MinArity == prototype.Node.MinArity
                        && (body.ScopeKey is null || !body.ScopeKey.StartsWith(ClosedPrefix, StringComparison.Ordinal) || ReferenceEquals(body.File, prototype.File))));

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

        private static string KeyOf(CodeGraphFile file, string name, HashSet<string> closed)
            => closed.Contains(name) ? ClosedKey(file.Path, name) : OpenKey(name);

        private const string OpenPrefix = "open\u0000";
        private const string ClosedPrefix = "closed\u0000";

        private static string OpenKey(string name) => OpenPrefix + name;

        private static string ClosedKey(string path, string name) => $"{ClosedPrefix}{path}\u0000{name}";

        /// <summary>The qualified name a scope's key stands for.</summary>
        private static string QualifiedOf(string key) => key[(key.LastIndexOf('\u0000') + 1)..];
    }

    /// <summary>Whether a reference of <paramref name="kind"/> names a base of the type it is made in.</summary>
    private static bool IsBase(string kind) => kind is CodeEdgeKinds.Inherits or CodeEdgeKinds.Implements or CodeEdgeKinds.MixesIn;

    /// <summary>Whether <paramref name="target"/> is something <paramref name="reference"/> can name: of a kind its edge reaches, and taking its arity.</summary>
    private static bool Takes(ExtractReference reference, Declaration target)
        => reference.Kind switch
        {
            CodeEdgeKinds.Calls => (target.Node.Kind == CodeNodeKinds.Member && target.Node.TakesArity(reference.Arity)) || target.Node.Kind == CodeNodeKinds.Type,
            CodeEdgeKinds.Overrides => target.Node.Kind == CodeNodeKinds.Member && target.Node.TakesArity(reference.Arity),
            CodeEdgeKinds.ProjectReference => target.Node.Kind == CodeNodeKinds.Project,
            "container" => target.Node.Kind is CodeNodeKinds.Type or CodeNodeKinds.Namespace,
            _ => target.Node.Kind == CodeNodeKinds.Type,
        };

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
            && qualified[qualified.Length - written.Length - 1] is var before
            && !char.IsLetterOrDigit(before)
            && before != '_'
            && before != '$';

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
