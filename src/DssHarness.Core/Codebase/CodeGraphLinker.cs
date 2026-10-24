using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

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
/// import's filter applied - a file imported making visible what it declares, what it declares exported by another
/// name by that name, and what it exports, an export's filter applied in turn - then in the repository by name and arity. A type qualifier is a type or a scope found as
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
/// What a closed scope declares is seen from its file alone, as is a type or a scope whose own scope is closed; a
/// declaration kept to a scope (<see cref="ExtractNode.SeenIn"/>) is seen from the declarations written in that scope or
/// inside it alone. A file that shares another's imports (<see cref="ExtractImport.Shares"/>) imports what that one
/// does, after its own. A name an import's prefix is stands for that import wherever a value or a type is written with
/// it. A type is looked in before its bases, and its bases nearest first: a mixin before what it is applied over, the last
/// one first, the first base that holds the name deciding. A type's bases are found in two passes - with no base known, then with the bases the first found - so what a base is never
/// hangs on the order the files come in.
/// </para>
/// <para>
/// <c>dispatches</c>, <c>declares</c> and <c>tests</c> are derived after, from the <c>implements</c> edges and the marks
/// every extract may set. The extracts keep names and not targets, so linking again after a file changed binds a name it
/// added to callers in files that did not change: each reference keeps what its finding read - the names it looked up,
/// the scopes it climbed, the types whose bases it went through, the imports and paths it followed - and linking again
/// (<see cref="Relink"/>) finds again only the references of the files that changed and those that read something a
/// change altered, every other one keeping what it found.
/// </para>
/// </remarks>
public static class CodeGraphLinker
{
    /// <summary>
    /// This linker's revision: raised whenever what it answers for the same extracts changes. Every link result names the
    /// revision that made it (<see cref="CodeGraphSymbolsFile.Linker"/>), so a graph linked by another one is linked again
    /// though no file changed, and no file is read again.
    /// </summary>
    public const int Revision = 6;

    /// <summary>Links <paramref name="files"/>, one tree's every file, into its symbols and edges.</summary>
    /// <param name="files">Every file the graph keeps, each once: those whose declarations are kept with their extracts.</param>
    /// <exception cref="ArgumentException">Two of <paramref name="files"/> have one path.</exception>
    public static CodeGraphLinked Link(IReadOnlyList<CodeGraphFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        return new Linking(files, previous: null).Link();
    }

    /// <summary>
    /// Links <paramref name="files"/> again from what linking gave <paramref name="previous"/>: a file given as the very
    /// object that link was given is unchanged, and only the references a change could have changed are found again -
    /// those of the files that changed, and those whose finding read something a change altered. What it gives is what
    /// <see cref="Link"/> gives for the same files; <see cref="CodeGraphLinked.References"/> counts what it found again.
    /// </summary>
    /// <param name="previous">What an earlier link of the tree gave.</param>
    /// <param name="files">Every file the graph keeps now, each once.</param>
    /// <exception cref="ArgumentException">Two of <paramref name="files"/> have one path.</exception>
    public static CodeGraphLinked Relink(CodeGraphLinked previous, IReadOnlyList<CodeGraphFile> files)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(files);
        return new Linking(files, previous.Memory).Link();
    }

    /// <summary>One declaration of one file, placed.</summary>
    /// <param name="Node">What its extract says of it.</param>
    /// <param name="File">The file that declares it.</param>
    /// <param name="ScopeKey">The scope it is declared in, by key.</param>
    /// <param name="FileOnly">Whether it is its file's alone: declared in a closed scope, or a type or scope whose own scope is closed.</param>
    /// <param name="SeenKey">The scope whose declarations alone see it, by key; <see langword="null"/> where it is kept to none.</param>
    private sealed record Declaration(ExtractNode Node, CodeGraphFile File, string? ScopeKey, bool FileOnly, string? SeenKey)
    {
        public string Id => Node.Id;
    }

    /// <summary>One address root an extract declares, placed.</summary>
    /// <param name="Prefix">The prefix of an address, or the whole address of one file.</param>
    /// <param name="Directory">The directory it stands for - the file, for a whole address - from the tree's root.</param>
    /// <param name="Whole">Whether <paramref name="Prefix"/> is a whole address.</param>
    /// <param name="Under">The directory of the file that declares it where it is that directory's files' alone (<see cref="ExtractAddressRoot.Local"/>), the tree's root as empty; <see langword="null"/> where it is every file's.</param>
    /// <param name="Order">Its place among every root: the longest prefix first, roots of one prefix in the order their files - in their paths' order - declare them.</param>
    private sealed record AddressRoot(string Prefix, string Directory, bool Whole, string? Under, int Order)
    {
        /// <summary>Whether it gives its meaning to an address the file at <paramref name="path"/> imports.</summary>
        public bool Reaches(string path) => Under is not { } under || under.Length == 0 || path.StartsWith(under + "/", StringComparison.Ordinal);
    }

    /// <summary>What a finding may read, each kind of key in a table of its own.</summary>
    private enum Read
    {
        /// <summary>The declarations of a simple name, in every index that holds them by it.</summary>
        Name,

        /// <summary>The parts of one id.</summary>
        Id,

        /// <summary>The types and scopes of one qualified name.</summary>
        Qualified,

        /// <summary>The qualified names a run of a name's characters ends.</summary>
        Trailing,

        /// <summary>The scope one scope's key is declared in.</summary>
        Scope,

        /// <summary>The bases of one type, by its id.</summary>
        Bases,

        /// <summary>What every file of one language imports.</summary>
        Global,

        /// <summary>The address roots every extract declares.</summary>
        Roots,

        /// <summary>Whether one path is a file of the tree.</summary>
        Path,

        /// <summary>The paths of one file name.</summary>
        FileName,

        /// <summary>The open scopes one file declares: what an import of it by its path makes visible.</summary>
        Opens,

        /// <summary>What one file imports, and its language.</summary>
        Imports,
    }

    /// <summary>One reference's finding: its edges, whether it named nothing or too much, and every key it read.</summary>
    /// <param name="Edges">The edges it makes.</param>
    /// <param name="Counted">What it is counted as where it makes none: <see cref="External"/>, <see cref="Unresolved"/>, or neither.</param>
    /// <param name="Reads">Every key its finding read, as <see cref="LinkKeys"/> numbers it.</param>
    internal sealed record Finding(IReadOnlyList<CodeEdge> Edges, int Counted, int[] Reads)
    {
        public const int External = 1;
        public const int Unresolved = 2;
    }

    /// <summary>
    /// What one file's references found: in the first pass of the bases, and in the last pass each reference is found in.
    /// An unchanged file shares the arrays of the link it was linked again from until one of its references is found again.
    /// </summary>
    internal sealed class FileFound(Finding?[] first, Finding?[] final, bool owned)
    {
        public Finding?[] First { get; private set; } = first;

        public Finding?[] Final { get; private set; } = final;

        private bool _firstOwned = owned;
        private bool _finalOwned = owned;

        public void Set(bool firstPass, int at, Finding finding)
        {
            if (firstPass)
            {
                if (!_firstOwned)
                {
                    First = (Finding?[])First.Clone();
                    _firstOwned = true;
                }

                First[at] = finding;
                return;
            }

            if (!_finalOwned)
            {
                Final = (Finding?[])Final.Clone();
                _finalOwned = true;
            }

            Final[at] = finding;
        }
    }

    /// <summary>
    /// The number of every key a finding may read: one table for a graph and every link made again from it, which links of
    /// several views may add to at once.
    /// </summary>
    internal sealed class LinkKeys
    {
        private readonly ConcurrentDictionary<string, int>[] _tables = [.. Enumerable.Range(0, Enum.GetValues<Read>().Length).Select(_ => new ConcurrentDictionary<string, int>(StringComparer.Ordinal))];
        private int _next;

        public int Of(int table, string text)
            => _tables[table].TryGetValue(text, out var number) ? number : _tables[table].GetOrAdd(text, _ => Interlocked.Increment(ref _next));
    }

    /// <summary>What a link keeps for the next to be made from it: the files it was given, what each reference found, and each type's bases.</summary>
    internal sealed class LinkMemory
    {
        internal LinkMemory(LinkKeys keys, IReadOnlyDictionary<string, CodeGraphFile> files, IReadOnlyDictionary<string, FileFound> found, Dictionary<string, List<string[]>> firstBases, Dictionary<string, List<string[]>> bases)
        {
            Keys = keys;
            Files = files;
            Found = found;
            FirstBases = firstBases;
            Bases = bases;
        }

        internal LinkKeys Keys { get; }

        internal IReadOnlyDictionary<string, CodeGraphFile> Files { get; }

        internal IReadOnlyDictionary<string, FileFound> Found { get; }

        internal Dictionary<string, List<string[]>> FirstBases { get; }

        internal Dictionary<string, List<string[]>> Bases { get; }
    }

    private sealed class Linking
    {
        private readonly IReadOnlyList<CodeGraphFile> _files;
        private readonly HashSet<string> _paths;
        private readonly Dictionary<string, CodeGraphFile> _byPath = new(StringComparer.Ordinal);
        private readonly LinkMemory? _previous;
        private readonly LinkKeys _keys;

        /// <summary>Every path by its file's name, the last part of it: where an import by a path's ending looks.</summary>
        private readonly Dictionary<string, List<string>> _byFileName = new(StringComparer.Ordinal);

        /// <summary>The files each import of each file names, once found, with what finding them read.</summary>
        private readonly Dictionary<CodeGraphFile, Dictionary<ExtractImport, (IReadOnlyList<string> Value, int[] Reads)>> _imported = new(ReferenceEqualityComparer.Instance);
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

        /// <summary>The address roots every extract declares, each placed (<see cref="AddressRoot.Order"/>).</summary>
        private readonly List<AddressRoot> _roots = [];

        /// <summary>The whole roots by the address each is: an address is looked up among them, never held against each.</summary>
        private readonly Dictionary<string, List<AddressRoot>> _wholeRoots = new(StringComparer.Ordinal);

        /// <summary>The roots that are prefixes, in their order.</summary>
        private readonly List<AddressRoot> _prefixRoots = [];

        /// <summary>Each file's declarations at its top - in no node and no scope - by name: what a file imported by path makes visible of its own.</summary>
        private readonly Dictionary<CodeGraphFile, Dictionary<string, List<Declaration>>> _topByFile = new(ReferenceEqualityComparer.Instance);

        /// <summary>Each file's declarations by each name another file imports them by besides their own (<see cref="ExtractNode.Exports"/>).</summary>
        private readonly Dictionary<CodeGraphFile, Dictionary<string, List<Declaration>>> _exportedByFile = new(ReferenceEqualityComparer.Instance);

        /// <summary>Each file's closed scopes at its top, by key: found once for each file.</summary>
        private readonly Dictionary<CodeGraphFile, IReadOnlyList<string>> _tops = new(ReferenceEqualityComparer.Instance);

        /// <summary>The containers a written name was found to be, from one file and scope: a name such as a type no file declares is looked for once.</summary>
        private readonly Dictionary<(string Path, string? Scope, string Written), (IReadOnlyList<Declaration> Value, int[] Reads)> _containersFound = [];

        /// <summary>Every type's and scope's qualified name by the run of a name's characters it ends with, in ordinal order: where an ending is looked for.</summary>
        private readonly Dictionary<string, List<string>> _byTrailing = new(StringComparer.Ordinal);

        /// <summary>The containers each written name ends, as the last way a qualifier is found: looked for once.</summary>
        private readonly Dictionary<string, (IReadOnlyList<Declaration> Value, int[] Reads)> _endings = new(StringComparer.Ordinal);

        /// <summary>Every member declared to extend a type, by its name: what a call a type does not hold may be.</summary>
        private readonly Dictionary<string, List<Declaration>> _extensions = new(StringComparer.Ordinal);

        /// <summary>The types each extension extends, by id, as its own file names the type: found once.</summary>
        private readonly Dictionary<Declaration, (IReadOnlyList<string> Value, int[] Reads)> _extended = new(ReferenceEqualityComparer.Instance);

        /// <summary>What each file's references found, by the file.</summary>
        private readonly Dictionary<CodeGraphFile, FileFound> _found = new(ReferenceEqualityComparer.Instance);

        /// <summary>Every reference found in this link, by its file and its place among the file's references, once.</summary>
        private readonly HashSet<(CodeGraphFile File, int At)> _resolved = [];

        private readonly List<CodeEdge> _edges = [];
        private readonly Dictionary<string, int> _external = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _unresolved = new(StringComparer.Ordinal);

        /// <summary>
        /// Each type's bases, by id, that a finding looks through now - none in the first pass of the bases - as
        /// <see cref="BaseRanksOf"/> ranks them.
        /// </summary>
        private Dictionary<string, List<string[]>> _bases = new(StringComparer.Ordinal);

        /// <summary>Every key the finding under way has read; null outside one.</summary>
        private HashSet<int>? _reads;

        public Linking(IReadOnlyList<CodeGraphFile> files, LinkMemory? previous)
        {
            // In one order whatever order they came in: a link made again is the whole link of the same files.
            _files = [.. files.OrderBy(file => file.Path, StringComparer.Ordinal)];
            _previous = previous;
            _keys = previous?.Keys ?? new LinkKeys();
            _paths = new HashSet<string>(StringComparer.Ordinal);

            foreach (var file in _files)
            {
                if (!_paths.Add(file.Path))
                {
                    throw new ArgumentException($"two files of the tree have the path '{file.Path}'", nameof(files));
                }

                _byPath[file.Path] = file;
                Add(_byFileName, CodeGraphIds.FileName(file.Path), file.Path);

                if (file.Extract is not { } extract)
                {
                    continue;
                }

                var owners = _owners[file] = new(StringComparer.Ordinal);
                var top = _topByFile[file] = new(StringComparer.Ordinal);
                var closed = Closed(extract);

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
                    if (CodeGraphIds.Beside(file.Path, root.Directory) is { } directory)
                    {
                        _roots.Add(new AddressRoot(root.Prefix, directory, root.Whole, root.Local ? file.Path[..Math.Max(file.Path.LastIndexOf('/'), 0)] : null, _roots.Count));
                    }
                }

                foreach (var node in extract.Nodes)
                {
                    var (scopeKey, fileOnly) = Placed(file, node, closed);
                    var declaration = new Declaration(node, file, scopeKey, fileOnly, node.SeenIn is { } seenIn ? KeyOf(file, seenIn, closed) : null);
                    _declarations.Add(declaration);
                    Add(_byId, node.Id, declaration);
                    owners.TryAdd(node.Id, declaration);

                    if (node.Parent is null && node.Scope is null)
                    {
                        Add(top, node.Name, declaration);
                    }

                    // A name it is exported by is its importers' to see, whatever scope its own name is kept to.
                    foreach (var exported in node.Exports ?? [])
                    {
                        Add(_exportedByFile.TryGetValue(file, out var names) ? names : _exportedByFile[file] = new(StringComparer.Ordinal), exported, declaration with { FileOnly = false, SeenKey = null });
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

            foreach (var qualified in _containers.Keys.Order(StringComparer.Ordinal))
            {
                Add(_byTrailing, Trailing(qualified), qualified);
            }

            // The longest prefix first; roots of one prefix in the order their files declare them - a path mapping's places, a
            // package's entries - the files in their paths' order, whatever order they came in.
            var ordered = _roots.OrderByDescending(root => root.Prefix.Length).ThenBy(root => root.Prefix, StringComparer.Ordinal).Select((root, order) => root with { Order = order }).ToList();
            _roots.Clear();
            _roots.AddRange(ordered);

            foreach (var root in ordered)
            {
                if (root.Whole)
                {
                    Add(_wholeRoots, root.Prefix, root);
                }
                else
                {
                    _prefixRoots.Add(root);
                }
            }
        }

        /// <summary>The stages a reference is found in: the bases twice, then every other reference.</summary>
        private enum Stage
        {
            FirstBases,
            Bases,
            Others,
        }

        public CodeGraphLinked Link()
        {
            Structure();

            var changed = Changed();
            var references = _files.Where(file => file.Extract is not null).ToList();

            // Bases first: a member is looked for in a type's bases, which their own references name - found with no base
            // known, then again with the bases that found, so no base hangs on the order the files come in.
            var first = new Dictionary<string, List<string[]>>(StringComparer.Ordinal);
            Pass(references, Stage.FirstBases, changed, first);

            var final = new Dictionary<string, List<string[]>>(StringComparer.Ordinal);
            _bases = first;
            Forget();
            Pass(references, Stage.Bases, [.. changed, .. Moved(_previous?.FirstBases, first)], final);

            _bases = final;
            Forget();
            Pass(references, Stage.Others, [.. changed, .. Moved(_previous?.Bases, final)], null);

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

            return new CodeGraphLinked(symbols, edges, _external, _unresolved, _resolved.Count)
            {
                Memory = new LinkMemory(_keys, _byPath, _found.ToDictionary(found => found.Key.Path, found => found.Value, StringComparer.Ordinal), first, final),
            };
        }

        /// <summary>
        /// Finds each reference of <paramref name="stage"/> - or keeps what the link it is made from found for it, where its
        /// file did not change and it read nothing <paramref name="changed"/> holds - and takes what it found: its edges, its
        /// counts, and the bases it found into <paramref name="bases"/>.
        /// </summary>
        private void Pass(List<CodeGraphFile> files, Stage stage, HashSet<int> changed, Dictionary<string, List<string[]>>? bases)
        {
            var firstPass = stage == Stage.FirstBases;

            foreach (var file in files)
            {
                var references = file.Extract!.References;
                var owners = _owners[file];

                if (!_found.TryGetValue(file, out var found))
                {
                    _found[file] = found = _previous is { } previous && previous.Files.TryGetValue(file.Path, out var was) && ReferenceEquals(was, file)
                        ? new FileFound(previous.Found[file.Path].First, previous.Found[file.Path].Final, owned: false)
                        : new FileFound(new Finding?[references.Count], new Finding?[references.Count], owned: true);
                }

                for (var at = 0; at < references.Count; at++)
                {
                    var reference = references[at];

                    if (IsBase(reference.Kind) == (stage == Stage.Others))
                    {
                        continue;
                    }

                    var from = owners[reference.From];
                    var kept = firstPass ? found.First[at] : found.Final[at];

                    if (kept is null || kept.Reads.Any(changed.Contains))
                    {
                        kept = Find(from, reference);
                        found.Set(firstPass, at, kept);
                        _resolved.Add((file, at));
                    }

                    Take(from, kept, stage, bases);
                }
            }
        }

        /// <summary>Takes what one reference found: the bases it found, and, past the first pass, its edges and counts.</summary>
        private void Take(Declaration from, Finding finding, Stage stage, Dictionary<string, List<string[]>>? bases)
        {
            // A mixin's findings are a rank of their own, nearer than every base named before it; an implemented base's join
            // the rank before the last, an inherited one's the last - each empty while the type names none - as BaseRanksOf
            // ranks them.
            if (bases is not null && finding.Edges.Count > 0)
            {
                var found = finding.Edges.Select(edge => edge.To).ToArray();

                if (!bases.TryGetValue(from.Id, out var ranks))
                {
                    bases[from.Id] = ranks = [[], []];
                }

                var kind = finding.Edges[0].Kind;

                if (kind == CodeEdgeKinds.MixesIn)
                {
                    ranks.Insert(0, found);
                }
                else
                {
                    var at = kind == CodeEdgeKinds.Implements ? ranks.Count - 2 : ranks.Count - 1;
                    ranks[at] = [.. ranks[at], .. found];
                }
            }

            // The first pass's findings are only what the second looks through.
            if (stage == Stage.FirstBases)
            {
                return;
            }

            _edges.AddRange(finding.Edges);

            if (finding.Counted == Finding.External)
            {
                Count(_external, from.Id);
            }
            else if (finding.Counted == Finding.Unresolved)
            {
                Count(_unresolved, from.Id);
            }
        }

        /// <summary>What a written name was found to be while other bases were known may be otherwise now.</summary>
        private void Forget()
        {
            _containersFound.Clear();
            _extended.Clear();
        }

        /// <summary>Every declaration's <c>contains</c> edge, from what it is declared inside: its parent node, or its file.</summary>
        private void Structure()
        {
            foreach (var declaration in _declarations)
            {
                _edges.Add(new CodeEdge(declaration.Node.Parent ?? CodeGraphIds.FileId(declaration.File.Path), declaration.Id, CodeEdgeKinds.Contains, declaration.Node.Start, CodeConfidences.Exact));
            }
        }

        /// <summary>One reference found: its edges or its count, with every key the finding read.</summary>
        private Finding Find(Declaration from, ExtractReference reference)
        {
            var outer = _reads;
            _reads = [];

            try
            {
                var (all, confidence) = Found(from, reference);
                var found = Bodied(all);

                if (found.Count == 0)
                {
                    return new Finding([], Finding.External, [.. _reads]);
                }

                if (found.Count > CodeConfidences.MostCandidates)
                {
                    return new Finding([], Finding.Unresolved, [.. _reads]);
                }

                var sure = found.Count == 1 ? confidence : CodeConfidences.Candidates;

                return new Finding(
                    [.. found.Select(target => new CodeEdge(
                        from.Id,
                        target.Id,
                        reference.Kind == CodeEdgeKinds.Calls && target.Node.Kind == CodeNodeKinds.Type ? CodeEdgeKinds.Instantiates : reference.Kind,
                        reference.Line,
                        sure))],
                    0,
                    [.. _reads]);
            }
            finally
            {
                _reads = outer;
            }
        }

        /// <summary>
        /// What <paramref name="reference"/> names, one declaration for each id, and how sure the place it was found makes
        /// it: none where nothing the repository declares has that name.
        /// </summary>
        private (IReadOnlyList<Declaration> Found, string Confidence) Found(Declaration from, ExtractReference reference)
        {
            bool Accepts(Declaration target) => Takes(reference, target) && Sees(from, target);

            // A value of a type the code does not say: through the type of the member it is - nothing the repository
            // declares where that type is not the repository's - else, where no value is so named and an import's prefix
            // is, through that import alone, as a file that shares another's imports writes that file's prefixes; else by
            // its name alone, where nothing is known of its type; never in the scopes it is written in, as though it were
            // a call of their own.
            if (reference.Qualifier is { Kind: CodeQualifierKinds.Value } value)
            {
                if (value.Name is { } written)
                {
                    if (Values(from, written) is { } values)
                    {
                        return Through(from, values, reference.Name, Accepts);
                    }

                    if (ImportsOf(from.File).Any(made => string.Equals(made.Import.Prefix, written, StringComparison.Ordinal)))
                    {
                        return (Prefixed(from, written, reference.Name, Accepts), CodeConfidences.Exact);
                    }
                }

                return ByName(from, reference.Name, Accepts);
            }

            // The qualifier's scope: where the code says, and nowhere else - a callee the qualifier's type does not hold is
            // one the repository does not declare there, but for an extension of that type.
            if (reference.Qualifier is { } qualifier)
            {
                return Qualified(from, qualifier, reference.Name, Accepts);
            }

            return Scoped(from, reference.Name, Accepts) is { Count: > 0 } scoped ? (scoped, CodeConfidences.Exact) : Beyond(from, reference.Name, Accepts);
        }

        /// <summary>What the file at <paramref name="path"/> declares that another file imports by the name <paramref name="name"/> besides its own.</summary>
        private IReadOnlyList<Declaration> ExportedNamed(string path, string name)
        {
            Touch(Read.Name, name);
            return _byPath.TryGetValue(path, out var file) && _exportedByFile.TryGetValue(file, out var exported) && exported.TryGetValue(name, out var named) ? named : [];
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
            => Distinct(RepositoryNamed(from.File, name).Where(accepts));

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
                    return (Prefixed(from, qualifier.Name!, name, accepts), CodeConfidences.Exact);

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

        /// <summary>What <paramref name="name"/> is through each import <paramref name="from"/>'s file makes by the prefix <paramref name="prefix"/>, its filter applied.</summary>
        private IReadOnlyList<Declaration> Prefixed(Declaration from, string prefix, string name, Func<Declaration, bool> accepts)
            => Distinct(ImportsOf(from.File)
                .Where(made => string.Equals(made.Import.Prefix, prefix, StringComparison.Ordinal) && made.Import.Lets(name))
                .SelectMany(made => Imported(made.Import, made.File, name, accepts)));

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
            if (ExtensionsNamed(name) is not { Count: > 0 } named)
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
            => Cached(_extended, extension, () => [.. Containers(extension, extension.Node.Extends!)
                .Where(container => container.Node.Kind == CodeNodeKinds.Type)
                .Select(type => type.Id)
                .Distinct(StringComparer.Ordinal)]);

        /// <summary>
        /// Whether <paramref name="from"/> reaches <paramref name="declaration"/>, an extension: the scope it is declared in,
        /// or the one that scope is declared in, is one <paramref name="from"/> is written in, or is at the top of none; or
        /// an import of its file makes it visible - an import of that scope with no prefix, since an alias names a scope and
        /// imports nothing, or of a file that declares it, declares that scope open, or exports one that does - each filter
        /// on the way letting the name it is visible by, the declaration that holds it. One its file keeps, or kept to a
        /// scope, is reached where it is seen alone.
        /// </summary>
        private bool Reaches(Declaration from, Declaration declaration)
        {
            if (!Sees(from, declaration))
            {
                return false;
            }

            if (declaration.ScopeKey is not { } key)
            {
                return true;
            }

            var holders = new HashSet<string>(StringComparer.Ordinal) { key };

            if (ParentOf(key, out var parent))
            {
                if (parent is null)
                {
                    return true;
                }

                holders.Add(parent);
            }

            if (Chain(from).Any(holders.Contains))
            {
                return true;
            }

            // The name an import's filter lets it by: an extension's, whose member this is - itself where nothing holds it.
            var shown = declaration.Node.Parent is { } holderId && PartsOf(holderId) is [var holder, ..] ? holder.Node.Name : declaration.Node.Name;

            return ImportsOf(from.File).Any(made => made.Import.Lets(shown) && (made.Import.Form == CodeImportForms.Scope
                ? made.Import.Prefix is null && holders.Contains(OpenKey(made.Import.Target))
                : FilesOf(made.Import, made.File).Any(target => Exposes(target, shown, holders, declaration.File.Path, []))));
        }

        /// <summary>
        /// Whether the file at <paramref name="path"/>, imported, makes visible what one of <paramref name="holders"/> holds
        /// or the file <paramref name="declared"/> declares: it is that file, it declares one of those scopes open, or a file
        /// it exports does - the export's filter letting <paramref name="shown"/> - each file once.
        /// </summary>
        private bool Exposes(string path, string shown, HashSet<string> holders, string declared, HashSet<string> seen)
        {
            if (!seen.Add(path))
            {
                return false;
            }

            if (path == declared || OpensOf(path).Any(scope => holders.Contains(OpenKey(scope))))
            {
                return true;
            }

            Touch(Read.Imports, path);

            if (!_byPath.TryGetValue(path, out var file) || file.Extract is null)
            {
                return false;
            }

            return Taken(file).Any(export => export.Export && export.Lets(shown) && FilesOf(export, file).Any(target => Exposes(target, shown, holders, declared, seen)));
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
            IReadOnlyList<Declaration> values = [.. Scoped(from, parts[0], declaration => (IsValue(declaration) || IsContainer(declaration)) && Sees(from, declaration)).Where(IsValue)];
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
            bool Seen(Declaration container) => IsContainer(container) && Sees(from, container);

            // What a name is from one place it is the same from every declaration there: found once.
            return Cached(_containersFound, (from.File.Path, from.ScopeKey, written), () =>
            {
                if (Scoped(from, written, Seen) is { Count: > 0 } scoped)
                {
                    return scoped;
                }

                return Distinct(ContainersOf(written).Where(Seen)) is { Count: > 0 } named
                    ? named
                    : Beyond(from, written, Seen).Found;
            });
        }

        /// <summary>Every type and scope <paramref name="from"/>'s file sees whose qualified name ends with <paramref name="written"/> at a name's boundary: found once for each.</summary>
        private IReadOnlyList<Declaration> Ending(Declaration from, string written)
        {
            var ending = Cached(_endings, written, () => Distinct(QualifiedEnding(Trailing(written))
                .Where(qualified => EndsWithName(qualified, written))
                .SelectMany(qualified => ContainersOf(qualified))));

            return ending.Any(container => container.FileOnly || container.SeenKey is not null) ? [.. ending.Where(container => Sees(from, container))] : ending;
        }

        /// <summary>
        /// The types and scopes <paramref name="written"/> stands for in <paramref name="file"/> through an alias: the
        /// target of each scope import it sees - its own, and what every file of its language imports - whose prefix is
        /// that name, or is its first part and the rest of it names what is inside the target. The prefix names the scope
        /// itself, so it is found wherever a type's or a scope's name is written. A file imported by a prefix names no
        /// scope: its prefix and one name after it are what that import makes visible of that name, its filter applied.
        /// </summary>
        private IReadOnlyList<Declaration> Aliased(CodeGraphFile file, string written)
        {
            var dot = written.IndexOf('.', StringComparison.Ordinal);
            var head = dot < 0 ? written : written[..dot];
            var rest = dot < 0 ? string.Empty : written[dot..];
            var named = rest.Length > 1 && rest.IndexOf('.', 1) < 0 ? rest[1..] : null;

            return Distinct(ImportsOf(file)
                .Where(made => string.Equals(made.Import.Prefix, head, StringComparison.Ordinal))
                .SelectMany(made => made.Import.Form == CodeImportForms.Scope ? ContainersOf(made.Import.Target + rest)
                    : named is not null && made.Import.Lets(named) ? Imported(made.Import, made.File, named, IsContainer)
                    : []));
        }

        /// <summary>The scopes a reference made in <paramref name="from"/> is looked for in, from the inside out.</summary>
        private IEnumerable<string> Chain(Declaration from)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var key = from.ScopeKey;

            while (key is not null && seen.Add(key))
            {
                yield return key;

                // Read only where the scope before decided nothing: its parent is what is looked in next.
                key = ParentOf(key, out var parent) ? parent : null;
            }

            // Then the closed scopes at its file's top, which every declaration of the file is written in.
            foreach (var top in TopsOf(from.File).Where(seen.Add))
            {
                yield return top;
            }
        }

        /// <summary>
        /// The closed scopes at the top of <paramref name="file"/>, by key: those no scope holds and that are no type's or
        /// namespace's own - what the file keeps to itself throughout, as a C file's <c>static</c> names. Read from the
        /// file's own extract alone, so a finding that reads them is found again whenever the file changes.
        /// </summary>
        private IReadOnlyList<string> TopsOf(CodeGraphFile file)
        {
            if (_tops.TryGetValue(file, out var tops))
            {
                return tops;
            }

            var extract = file.Extract;
            var own = extract?.Nodes.Where(IsContainer).Select(node => node.Qualified).ToHashSet(StringComparer.Ordinal) ?? [];

            return _tops[file] = extract is null
                ? []
                : [.. extract.Scopes.Where(scope => !scope.Open && scope.Parent is null && !own.Contains(scope.Name)).Select(scope => ClosedKey(file.Path, scope.Name))];
        }

        /// <summary>What the scope <paramref name="key"/> declares named <paramref name="name"/>, and - where it is a type's, and <paramref name="withBases"/> - what its bases do.</summary>
        private IReadOnlyList<Declaration> InScope(string key, string name, Func<Declaration, bool> accepts, bool withBases)
        {
            var found = Distinct(ScopeNamed(key, name).Where(accepts));

            if (found.Count > 0 || !withBases)
            {
                return found;
            }

            var types = ContainersOf(QualifiedOf(key)).Where(container => container.Node.Kind == CodeNodeKinds.Type && KeyOf(container.File, container.Node.Qualified) == key).ToList();

            return types.Count > 0 ? InTypes(types, name, accepts, withSelf: false) : [];
        }

        /// <summary>
        /// What <paramref name="types"/> declare named <paramref name="name"/>: what each of them does, as
        /// <see cref="InType"/> finds it.
        /// </summary>
        private IReadOnlyList<Declaration> InTypes(IReadOnlyList<Declaration> types, string name, Func<Declaration, bool> accepts, bool withSelf)
            => Distinct(types.DistinctBy(type => type.Id, StringComparer.Ordinal).SelectMany(type => InType(type.Id, name, accepts, withSelf)));

        /// <summary>
        /// What the type <paramref name="id"/> declares named <paramref name="name"/> - itself where <paramref name="withSelf"/>,
        /// then its bases a level at a time, each level a rank at a time as <see cref="LevelUp"/> ranks it: the first rank
        /// that holds one decides, all it holds answering.
        /// </summary>
        private IReadOnlyList<Declaration> InType(string id, string name, Func<Declaration, bool> accepts, bool withSelf)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            List<BaseRank> level = [new BaseRank([id], Mixin: false)];
            level = withSelf ? level : LevelUp(level);

            while (level.Count > 0)
            {
                foreach (var rank in level)
                {
                    var found = Distinct(rank.Ids.Where(seen.Add)
                        .SelectMany(PartsOf)
                        .SelectMany(type => ScopeNamed(KeyOf(type.File, type.Node.Qualified), name))
                        .Where(accepts));

                    if (found.Count > 0)
                    {
                        return found;
                    }
                }

                level = [.. LevelUp(level).Select(rank => rank with { Ids = [.. rank.Ids.Where(next => !seen.Contains(next))] }).Where(rank => rank.Ids.Length > 0)];
            }

            return [];
        }

        /// <summary>One rank of the bases a level holds: the ids in it, and whether they are a mixin's, applied over what the rest of the level's type holds.</summary>
        private readonly record struct BaseRank(string[] Ids, bool Mixin);

        /// <summary>
        /// The ranks of the bases one level up from <paramref name="level"/>'s types, nearest first: the mixins each type
        /// names, a rank each, last first, the types taken in the order the level ranks them; then one rank of everything
        /// they inherit and implement, whatever order they are named in and by whichever type - as a member two of those
        /// declare is either's - with nothing a mixin itself inherits, though what it implements: a mixin is applied over
        /// the base of the type that names it, which is on the way already, and what it is declared on only says what that
        /// base must be.
        /// </summary>
        private List<BaseRank> LevelUp(IEnumerable<BaseRank> level)
        {
            var ranks = new List<BaseRank>();
            var inherited = new List<string>();

            foreach (var rank in level)
            {
                foreach (var id in rank.Ids)
                {
                    if (BaseRanksOf(id) is not { Count: > 0 } bases)
                    {
                        continue;
                    }

                    ranks.AddRange(bases.Take(bases.Count - 2).Select(mixin => new BaseRank(mixin, Mixin: true)));
                    inherited.AddRange(bases[^2]);

                    if (!rank.Mixin)
                    {
                        inherited.AddRange(bases[^1]);
                    }
                }
            }

            if (inherited.Count > 0)
            {
                ranks.Add(new BaseRank([.. inherited.Distinct(StringComparer.Ordinal)], Mixin: false));
            }

            return ranks;
        }

        /// <summary>The bases of the type <paramref name="id"/>, by id, nearest first (<see cref="BaseRanksOf"/>).</summary>
        private IEnumerable<string> BasesOf(string id) => BaseRanksOf(id).SelectMany(rank => rank);

        /// <summary>
        /// The bases of the type <paramref name="id"/>, by id, in ranks nearest first: each mixin it names a rank of its own,
        /// the last of them first - each applied over what comes before it, as Dart's are - with the candidates it was found
        /// among; then what it implements, then what it inherits, in the order it names them - two ranks one to a lookup
        /// (<see cref="LevelUp"/>), as a member two of those declare is either's, and apart so that a mixin's own inherited
        /// bases are told from what it implements. Each of the two is empty where the type names none; a type that names no
        /// base has no ranks.
        /// </summary>
        private IReadOnlyList<string[]> BaseRanksOf(string id)
        {
            Touch(Read.Bases, id);
            return _bases.TryGetValue(id, out var bases) ? bases : [];
        }

        /// <summary>What <paramref name="import"/>, made by <paramref name="file"/>, makes visible named <paramref name="name"/>.</summary>
        private IReadOnlyList<Declaration> Imported(ExtractImport import, CodeGraphFile file, string name, Func<Declaration, bool> accepts)
        {
            if (import.Form == CodeImportForms.Scope)
            {
                return InScope(OpenKey(import.Target), name, accepts, withBases: true);
            }

            return Distinct(FilesOf(import, file).SelectMany(target => Visible(target, name, [])).Where(accepts));
        }

        /// <summary>
        /// What a file imported by path makes visible named <paramref name="name"/>: its own top-level declarations, those of
        /// the open scopes it declares, those it declares exported by that name (<see cref="ExtractNode.Exports"/>), and what
        /// each file it exports makes visible, that export's filter applied - each file once, so exports that come round again
        /// end.
        /// </summary>
        private IEnumerable<Declaration> Visible(string path, string name, HashSet<string> seen)
        {
            if (!seen.Add(path))
            {
                return [];
            }

            var own = TopNamed(path, name).Concat(OpensOf(path).SelectMany(scope => ScopeNamed(OpenKey(scope), name))).Concat(ExportedNamed(path, name)).ToList();
            Touch(Read.Imports, path);

            if (_byPath.TryGetValue(path, out var file) && file.Extract is not null)
            {
                foreach (var export in Taken(file).Where(import => import.Export && import.Lets(name)))
                {
                    own.AddRange(FilesOf(export, file).SelectMany(target => Visible(target, name, seen)));
                }
            }

            return own;
        }

        /// <summary>The files of the tree <paramref name="import"/> names, by its form: found once for each import of each file.</summary>
        private IReadOnlyList<string> FilesOf(ExtractImport import, CodeGraphFile file)
            => Cached(_imported.TryGetValue(file, out var ofFile) ? ofFile : _imported[file] = new(ReferenceEqualityComparer.Instance), import, () => FindFiles(import, file));

        private IReadOnlyList<string> FindFiles(ExtractImport import, CodeGraphFile file)
        {
            switch (import.Form)
            {
                case CodeImportForms.Relative:
                    var relative = CodeGraphIds.Beside(file.Path, import.Target);

                    if (relative is null)
                    {
                        return [];
                    }

                    return IsFile(relative) ? [relative] : [];

                case CodeImportForms.Address:
                    // The first root that matches and names a file of the tree: a prefix every address it begins, a whole address
                    // its own alone - several roots may share one, as a path mapping lists places.
                    foreach (var root in RootsOf(file.Path, import.Target))
                    {
                        var addressed = CodeGraphIds.Normalize(root.Whole ? root.Directory : $"{root.Directory}/{import.Target[root.Prefix.Length..]}");

                        if (addressed is not null && IsFile(addressed))
                        {
                            return [addressed];
                        }
                    }

                    return [];

                default:
                    var ending = CodeGraphIds.Normalize(import.Target);

                    if (ending is null)
                    {
                        return [];
                    }

                    return [.. PathsNamed(CodeGraphIds.FileName(ending)).Where(path => path == ending || path.EndsWith("/" + ending, StringComparison.Ordinal)).Order(StringComparer.Ordinal)];
            }
        }

        /// <summary>
        /// What <paramref name="file"/> imports, in order; then what each file it shares the imports of imports; then what
        /// every file of its language imports - each with the file that made it, which an import by a relative path is read
        /// from.
        /// </summary>
        private IEnumerable<(ExtractImport Import, CodeGraphFile File)> ImportsOf(CodeGraphFile file)
        {
            Touch(Read.Imports, file.Path);

            if (file.Language is { } language)
            {
                Touch(Read.Global, language);
            }

            // An export makes names visible to the file's importers, not to the file.
            var own = Taken(file).Where(import => !import.Export).ToList();

            return own.Select(import => (import, file))
                .Concat(own.Where(import => import.Shares).SelectMany(import => FilesOf(import, file)).Distinct(StringComparer.Ordinal).SelectMany(SharedOf))
                .Concat(file.Language is { } named && _global.TryGetValue(named, out var global)
                    ? global.Where(made => !ReferenceEquals(made.File, file)).Select(made => (made.Import, made.File))
                    : []);
        }

        /// <summary>What the file at <paramref name="path"/> imports of its own, as a file that shares its imports takes them: none it shares or exports.</summary>
        private IEnumerable<(ExtractImport Import, CodeGraphFile File)> SharedOf(string path)
        {
            // Read whether or not the file has an extract now: one that gains one changes what it imports.
            Touch(Read.Imports, path);

            if (!_byPath.TryGetValue(path, out var shared) || shared.Extract is null)
            {
                return [];
            }

            return Taken(shared).Where(import => !import.Export && !import.Shares).Select(import => (import, shared));
        }

        /// <summary>
        /// The imports <paramref name="file"/> makes, in order, each run of an import and those that stand in for it
        /// (<see cref="ExtractImport.Fallback"/>) taken as the first of them that names a file of the tree - the run's first
        /// where none does.
        /// </summary>
        private IEnumerable<ExtractImport> Taken(CodeGraphFile file)
        {
            var imports = file.Extract!.Imports;

            for (var at = 0; at < imports.Count;)
            {
                var past = at + 1;

                while (past < imports.Count && imports[past].Fallback)
                {
                    past++;
                }

                var taken = imports[at];

                for (var standing = at; past - at > 1 && standing < past; standing++)
                {
                    if (FilesOf(imports[standing], file).Count > 0)
                    {
                        taken = imports[standing];
                        break;
                    }
                }

                yield return taken;
                at = past;
            }
        }

        /// <summary>Each file's <c>imports</c> edges: to a scope the repository declares, and to each file a path names.</summary>
        private void Imports()
        {
            foreach (var file in _files.Where(file => file.Extract is not null))
            {
                var from = CodeGraphIds.FileId(file.Path);

                foreach (var import in Taken(file))
                {
                    if (import.Form == CodeImportForms.Scope)
                    {
                        foreach (var id in ContainersOf(import.Target).Where(container => Sees(file, container)).Select(container => container.Id).Distinct(StringComparer.Ordinal))
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
        /// of its kind, name and arity, of the type itself, else of its bases it inherits or mixes in a level at a time, each
        /// level a rank at a time as <see cref="LevelUp"/> ranks it - the first rank that holds one deciding, as sure as the
        /// surest way up to it, a way as sure as the least sure edge on it, and <c>candidates</c> where several in that rank
        /// answer alike; none past <see cref="CodeConfidences.MostCandidates"/>.
        /// </summary>
        private List<(Declaration Answer, string Sure)> Answering(string type, Declaration member, ILookup<string, CodeEdge> bases)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal) { type };
            List<BaseRank> level = [new BaseRank([type], Mixin: false)];

            // How sure the surest way up to each of the level's types is.
            var sure = new Dictionary<string, string>(StringComparer.Ordinal) { [type] = CodeConfidences.Exact };

            while (level.Count > 0)
            {
                foreach (var rank in level)
                {
                    var found = rank.Ids
                        .SelectMany(at => PartsOf(at)
                            .SelectMany(part => ScopeNamed(KeyOf(part.File, part.Node.Qualified), member.Node.Name))
                            .Where(candidate => candidate.Node.Kind == CodeNodeKinds.Member
                                && !candidate.Node.Prototype
                                && TakeAlike(candidate.Node, member.Node))
                            .Select(answer => (Answer: answer, Sure: sure[at])))
                        .GroupBy(answer => answer.Answer.Id, StringComparer.Ordinal)
                        .Select(alike => (alike.First().Answer, Sure: alike.Select(answer => answer.Sure).Aggregate(CodeConfidences.Surer)))
                        .ToList();

                    if (found.Count > 0)
                    {
                        return found.Count == 1 ? found
                            : found.Count <= CodeConfidences.MostCandidates ? [.. found.Select(answer => (answer.Answer, CodeConfidences.Candidates))]
                            : [];
                    }
                }

                // Every way up to each base of the next level - each edge of each type that names it, a mixin's own bases
                // not among them - the surest taken.
                var ways = new Dictionary<string, string>(StringComparer.Ordinal);

                foreach (var rank in level)
                {
                    foreach (var at in rank.Ids)
                    {
                        foreach (var edge in bases[at].Where(edge => edge.Kind == CodeEdgeKinds.MixesIn || (edge.Kind == CodeEdgeKinds.Inherits && !rank.Mixin)))
                        {
                            var way = CodeConfidences.Weaker(sure[at], edge.Confidence);
                            ways[edge.To] = ways.TryGetValue(edge.To, out var known) ? CodeConfidences.Surer(known, way) : way;
                        }
                    }
                }

                level = [.. LevelUp(level)
                    .Select(rank => rank with { Ids = [.. rank.Ids.Where(id => ways.ContainsKey(id) && !seen.Contains(id))] })
                    .Where(rank => rank.Ids.Length > 0)];
                sure = ways;
                seen.UnionWith(level.SelectMany(rank => rank.Ids));
            }

            return [];
        }

        /// <summary>The members the type <paramref name="id"/> declares, each once.</summary>
        private IReadOnlyList<Declaration> MembersOf(string id)
            => Distinct(PartsOf(id)
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
                    .Where(body => body.Id != prototype.Id && Declared(prototype, body) && Sees(prototype.File, body)));

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

        /// <summary>
        /// The type <paramref name="from"/> is declared in, or is: the one what holds it reaches - else, where what holds it
        /// reaches none, the type whose scope it is declared in, as a member defined outside its type's body is; none where
        /// neither is.
        /// </summary>
        private Declaration? EnclosingType(Declaration from)
        {
            var at = from;
            var seen = new HashSet<string>(StringComparer.Ordinal);

            while (at.Node.Kind != CodeNodeKinds.Type)
            {
                if (at.Node.Parent is not { } parent || !seen.Add(parent) || !_owners[from.File].TryGetValue(parent, out var holder))
                {
                    return from.ScopeKey is { } key
                        ? ContainersOf(QualifiedOf(key)).FirstOrDefault(container => container.Node.Kind == CodeNodeKinds.Type && KeyOf(container.File, container.Node.Qualified) == key)
                        : null;
                }

                at = holder;
            }

            return at;
        }

        /// <summary>Says that the finding under way read the key <paramref name="text"/> of <paramref name="kind"/>.</summary>
        private void Touch(Read kind, string text) => _reads?.Add(_keys.Of((int)kind, text));

        // What a finding reads of the tree, each through the one way it is read, which says the key it read: no index is
        // looked in otherwise while a reference is found - only where a whole link derives its edges after, once.

        /// <summary>The declarations named <paramref name="name"/> that any file can see, and those <paramref name="file"/>'s closed scopes keep to it.</summary>
        private IEnumerable<Declaration> RepositoryNamed(CodeGraphFile file, string name)
        {
            Touch(Read.Name, name);

            return (_byName.TryGetValue(name, out var anywhere) ? anywhere : [])
                .Concat(_closedByFile.TryGetValue(file.Path, out var own) && own.TryGetValue(name, out var kept) ? kept : []);
        }

        /// <summary>The members named <paramref name="name"/> declared to extend a type.</summary>
        private IReadOnlyList<Declaration> ExtensionsNamed(string name)
        {
            Touch(Read.Name, name);
            return _extensions.TryGetValue(name, out var named) ? named : [];
        }

        /// <summary>What the scope <paramref name="key"/> declares named <paramref name="name"/>.</summary>
        private IReadOnlyList<Declaration> ScopeNamed(string key, string name)
        {
            Touch(Read.Name, name);
            return _byScope.TryGetValue(key, out var names) && names.TryGetValue(name, out var declared) ? declared : [];
        }

        /// <summary>The declarations at the top of the file at <paramref name="path"/> - in no node and no scope - named <paramref name="name"/>.</summary>
        private IReadOnlyList<Declaration> TopNamed(string path, string name)
        {
            Touch(Read.Name, name);
            return _topByFile.TryGetValue(_byPath[path], out var top) && top.TryGetValue(name, out var named) ? named : [];
        }

        /// <summary>Whether the scope <paramref name="key"/> is placed, and the scope it is declared in, where it is: none at the top.</summary>
        private bool ParentOf(string key, out string? parent)
        {
            Touch(Read.Scope, key);
            return _scopeParents.TryGetValue(key, out parent);
        }

        /// <summary>The types and scopes of the qualified name <paramref name="qualified"/>.</summary>
        private IReadOnlyList<Declaration> ContainersOf(string qualified)
        {
            Touch(Read.Qualified, qualified);
            return _containers.TryGetValue(qualified, out var containers) ? containers : [];
        }

        /// <summary>The qualified names of every type and scope that end with the run of a name's characters <paramref name="trailing"/>.</summary>
        private IReadOnlyList<string> QualifiedEnding(string trailing)
        {
            Touch(Read.Trailing, trailing);
            return _byTrailing.TryGetValue(trailing, out var named) ? named : [];
        }

        /// <summary>The parts of the declaration <paramref name="id"/>: a type of several, one for each file.</summary>
        private IReadOnlyList<Declaration> PartsOf(string id)
        {
            Touch(Read.Id, id);
            return _byId.TryGetValue(id, out var parts) ? parts : [];
        }

        /// <summary>Whether <paramref name="path"/> is a file of the tree.</summary>
        private bool IsFile(string path)
        {
            Touch(Read.Path, path);
            return _paths.Contains(path);
        }

        /// <summary>
        /// The address roots that match <paramref name="address"/> as the file at <paramref name="path"/> imports it, in the
        /// order they are tried: the local roots of the files in whose directories it is, the nearest file's first, then
        /// every root that is no file's own - in each, in their order (<see cref="AddressRoot.Order"/>).
        /// </summary>
        private IEnumerable<AddressRoot> RootsOf(string path, string address)
        {
            Touch(Read.Roots, string.Empty);

            return (_wholeRoots.TryGetValue(address, out var whole) ? whole : [])
                .Concat(_prefixRoots.Where(root => address.StartsWith(root.Prefix, StringComparison.Ordinal)))
                .Where(root => root.Reaches(path))
                .OrderBy(root => root.Under is null)
                .ThenByDescending(root => root.Under?.Length ?? 0)
                .ThenBy(root => root.Order);
        }

        /// <summary>The paths of the files named <paramref name="fileName"/>.</summary>
        private IReadOnlyList<string> PathsNamed(string fileName)
        {
            Touch(Read.FileName, fileName);
            return _byFileName.TryGetValue(fileName, out var named) ? named : [];
        }

        /// <summary>The open scopes the file at <paramref name="path"/> declares: what an import of it by its path makes visible, with its top.</summary>
        private IEnumerable<string> OpensOf(string path)
        {
            Touch(Read.Opens, path);
            return _byPath[path].Extract is { } extract ? extract.Scopes.Where(scope => scope.Open).Select(scope => scope.Name) : [];
        }

        /// <summary>
        /// What <paramref name="cache"/> holds for <paramref name="key"/>, made once by <paramref name="make"/>: the finding
        /// under way reads what making it read, whether it made it or found it made.
        /// </summary>
        private T Cached<TKey, T>(Dictionary<TKey, (T Value, int[] Reads)> cache, TKey key, Func<T> make)
            where TKey : notnull
        {
            if (cache.TryGetValue(key, out var known))
            {
                _reads?.UnionWith(known.Reads);
                return known.Value;
            }

            var outer = _reads;
            var reads = _reads = [];

            try
            {
                var value = make();
                cache[key] = (value, [.. reads]);
                return value;
            }
            finally
            {
                _reads = outer;
                outer?.UnionWith(reads);
            }
        }

        /// <summary>
        /// Every key a change altered since the link this is made from: of each file not given as the very object that link
        /// was given, each key whose part it holds there is not the part it held - a name's declarations, a type's bases'
        /// way in, an import, a scope's parent - and, for a file come or gone, its path and its name. None for a whole link,
        /// which finds every reference.
        /// </summary>
        private HashSet<int> Changed()
        {
            var changed = new HashSet<int>();

            if (_previous is not { } previous)
            {
                return changed;
            }

            foreach (var path in previous.Files.Keys.Union(_byPath.Keys, StringComparer.Ordinal))
            {
                var was = previous.Files.GetValueOrDefault(path);
                var now = _byPath.GetValueOrDefault(path);

                if (ReferenceEquals(was, now))
                {
                    continue;
                }

                if (was is null || now is null)
                {
                    changed.Add(_keys.Of((int)Read.Path, path));
                    changed.Add(_keys.Of((int)Read.FileName, CodeGraphIds.FileName(path)));
                }

                var before = Parts(was);
                var after = Parts(now);

                foreach (var key in before.Keys.Union(after.Keys))
                {
                    if (!string.Equals(before.GetValueOrDefault(key), after.GetValueOrDefault(key), StringComparison.Ordinal))
                    {
                        changed.Add(key);
                    }
                }
            }

            return changed;
        }

        /// <summary>
        /// What <paramref name="file"/> holds of each key a finding may read, in order: each of its declarations under its
        /// name and its id - and, for a type or a scope, under its qualified name, its name's last run and its own scope's -
        /// each scope's parent, its open scopes, its imports with its language, what every file of its language imports, and
        /// its address roots, each whole or not and local or not. A declaration's part is what a finding reads of it, by
        /// whichever key it was reached: its id, its kind, the scope it is declared in and the scope it is, the declaration
        /// that holds it - which says whether it is its file's top - what it takes, its value's type, what it extends, the
        /// scope it is kept to and the names it is exported by. Its name, its qualified name and each name it is exported by
        /// are keys and no field - a declaration whose name changed leaves one key for another, and a scope's key holds its
        /// qualified name; a line or a body is never read. A file with no extract holds no part: one that gains or loses its extract
        /// changes its open scopes'.
        /// </summary>
        private Dictionary<int, string> Parts(CodeGraphFile? file)
        {
            var parts = new Dictionary<int, StringBuilder>();

            void Add(Read kind, string text, string part)
            {
                var key = _keys.Of((int)kind, text);
                (parts.TryGetValue(key, out var held) ? held : parts[key] = new StringBuilder()).Append(part).Append(PartEnd);
            }

            if (file?.Extract is not { } extract)
            {
                return [];
            }

            var closed = Closed(extract);

            Add(Read.Opens, file.Path, string.Join(FieldEnd, extract.Scopes.Where(scope => scope.Open).Select(scope => scope.Name)));
            Add(Read.Imports, file.Path, string.Join(FieldEnd, [file.Language ?? None, .. extract.Imports.Select(ImportPart)]));

            if (file.Language is { } language)
            {
                foreach (var import in extract.Imports.Where(import => import.Global))
                {
                    Add(Read.Global, language, ImportPart(import));
                }
            }

            foreach (var root in extract.AddressRoots)
            {
                Add(Read.Roots, string.Empty, $"{file.Path}{FieldEnd}{root.Prefix}{FieldEnd}{root.Directory}{FieldEnd}{(root.Whole ? "whole" : None)}{FieldEnd}{(root.Local ? "local" : None)}");
            }

            foreach (var scope in extract.Scopes)
            {
                Add(Read.Scope, KeyOf(file, scope.Name, closed), scope.Parent is { } parent ? KeyOf(file, parent, closed) : None);
            }

            foreach (var node in extract.Nodes)
            {
                // Whether it is its file's alone is no field of its own: a closed scope's key is its file's, so the two keys say it.
                var scopeKey = Placed(file, node, closed).ScopeKey;
                var own = IsContainer(node) ? KeyOf(file, node.Qualified, closed) : null;
                var shape = string.Join(
                    FieldEnd,
                    node.Id,
                    node.Kind,
                    scopeKey ?? None,
                    own ?? None,
                    node.Parent ?? None,
                    node.MinArity?.ToString(CultureInfo.InvariantCulture) ?? None,
                    node.MaxArity?.ToString(CultureInfo.InvariantCulture) ?? None,
                    node.ValueType ?? None,
                    node.Extends ?? None,
                    node.SeenIn is { } seenIn ? KeyOf(file, seenIn, closed) : None,
                    node.Qualified,
                    node.Prototype ? "prototype" : None,
                    string.Join(PartEnd, node.Exports ?? []));

                Add(Read.Name, node.Name, shape);

                foreach (var exported in node.Exports ?? [])
                {
                    Add(Read.Name, exported, shape);
                }

                Add(Read.Id, node.Id, shape);

                if (own is not null)
                {
                    Add(Read.Qualified, node.Qualified, shape);
                    Add(Read.Trailing, Trailing(node.Qualified), node.Qualified);
                    Add(Read.Scope, own, scopeKey ?? None);
                }
            }

            return parts.ToDictionary(part => part.Key, part => part.Value.ToString());
        }

        /// <summary>The keys of the types whose bases are not what they were: what a finding that went through them read.</summary>
        private IEnumerable<int> Moved(Dictionary<string, List<string[]>>? before, Dictionary<string, List<string[]>> now)
        {
            if (before is null)
            {
                yield break;
            }

            // Each rank's ids and where it ends: a base moved from one rank to another is a change too.
            static string Written(List<string[]>? ranks) => ranks is null ? string.Empty : string.Join(PartEnd, ranks.Select(rank => string.Join(FieldEnd, rank)));

            foreach (var id in before.Keys.Union(now.Keys, StringComparer.Ordinal))
            {
                if (Written(before.GetValueOrDefault(id)) != Written(now.GetValueOrDefault(id)))
                {
                    yield return _keys.Of((int)Read.Bases, id);
                }
            }
        }

        /// <summary>
        /// What an import is, as a finding reads it: its form, target, prefix, filters, whether it stands in for the one
        /// before it, whether it is an export and whether it shares the imports of the file it names - never its line, and not whether it is global, which only what every file of its language imports
        /// holds it under.
        /// </summary>
        private static string ImportPart(ExtractImport import)
            => string.Join(
                FieldEnd,
                import.Form,
                import.Target,
                import.Prefix ?? None,
                import.Show is { } show ? string.Join(',', show) : None,
                import.Hide is { } hide ? string.Join(',', hide) : None,
                import.Fallback ? "fallback" : None,
                import.Export ? "export" : None,
                import.Shares ? "shares" : None);

        /// <summary>The names of the closed scopes <paramref name="extract"/> declares.</summary>
        private static HashSet<string> Closed(FileExtract extract) => extract.Scopes.Where(scope => !scope.Open).Select(scope => scope.Name).ToHashSet(StringComparer.Ordinal);

        /// <summary>
        /// The scope <paramref name="node"/> of <paramref name="file"/> is declared in, by key, and whether it is its file's
        /// alone: a type or a scope whose own scope is closed is, as what it declares is.
        /// </summary>
        private static (string? ScopeKey, bool FileOnly) Placed(CodeGraphFile file, ExtractNode node, HashSet<string> closed)
            => (node.Scope is { } scope ? KeyOf(file, scope, closed) : null,
                (node.Scope is { } declared && closed.Contains(declared)) || (IsContainer(node) && closed.Contains(node.Qualified)));

        /// <summary>What stands for nothing in a part: no name has it.</summary>
        private const string None = "\u0004";

        /// <summary>What ends one field of a part.</summary>
        private const string FieldEnd = "\u0002";

        /// <summary>What ends one part a file holds of a key.</summary>
        private const char PartEnd = '\u0001';

        /// <summary>
        /// The scope <paramref name="name"/> is from <paramref name="file"/>: that file's own where it declares it closed, and
        /// the one every file shares otherwise.
        /// </summary>
        private string KeyOf(CodeGraphFile file, string name)
            => file.Extract is { } extract && extract.Scopes.Any(scope => !scope.Open && scope.Name == name) ? ClosedKey(file.Path, name) : OpenKey(name);

        /// <summary>Whether <paramref name="file"/> sees <paramref name="declaration"/>: one that is its file's alone is seen from that file alone.</summary>
        private static bool Sees(CodeGraphFile file, Declaration declaration) => !declaration.FileOnly || ReferenceEquals(declaration.File, file);

        /// <summary>
        /// Whether <paramref name="from"/> sees <paramref name="declaration"/>: from its file, and - where it is kept to a
        /// scope - from that scope, or a scope inside it, as the scopes <paramref name="from"/> is written in climb.
        /// </summary>
        private bool Sees(Declaration from, Declaration declaration)
            => Sees(from.File, declaration) && (declaration.SeenKey is not { } seen || Chain(from).Contains(seen, StringComparer.Ordinal));

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

    /// <summary>
    /// <paramref name="found"/> with each declaration that has no body left out where one with a body, of its language,
    /// qualified name and parameters, is among them: a prototype found beside the definition it declares is that
    /// definition, as <c>declares</c> binds them.
    /// </summary>
    private static IReadOnlyList<Declaration> Bodied(IReadOnlyList<Declaration> found)
        => found.Any(declaration => declaration.Node.Prototype) && found.Any(declaration => !declaration.Node.Prototype)
            ? [.. found.Where(declaration => !declaration.Node.Prototype || !found.Any(body => !body.Node.Prototype && Declared(declaration, body)))]
            : found;

    /// <summary>Whether <paramref name="prototype"/>, a declaration with no body, declares <paramref name="body"/>: of one language, one qualified name, and taking alike.</summary>
    private static bool Declared(Declaration prototype, Declaration body)
        => body.File.Language == prototype.File.Language && body.Node.Qualified == prototype.Node.Qualified && TakeAlike(body.Node, prototype.Node);

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
/// <param name="References">How many references were found: every one by a whole link, those a change could have changed by a link made again.</param>
public sealed record CodeGraphLinked(
    IReadOnlyList<CodeGraphSymbol> Symbols,
    IReadOnlyList<CodeEdge> Edges,
    IReadOnlyDictionary<string, int> External,
    IReadOnlyDictionary<string, int> Unresolved,
    int References)
{
    /// <summary>What the link keeps for one made again from it (<see cref="CodeGraphLinker.Relink"/>).</summary>
    internal CodeGraphLinker.LinkMemory? Memory { get; init; }
}
