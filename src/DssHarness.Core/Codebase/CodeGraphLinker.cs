using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using DssHarness.Core.Walks;

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
/// <c>unique</c> where it did not; two or more are an edge to each, of <c>candidates</c>, however many they are - every
/// declaration it may be, none dropped; none is counted external, a callee the repository does not declare.
/// </para>
/// <para>
/// A node that stands for a suffix written onto others' names (<see cref="ExtractNode.SuffixOf"/>) is found where a name
/// it writes is looked for, by the end of the name its suffix is: the rest is a name a node it is written onto stands for
/// in turn, each node at each place of the name walked once however many reach it.
/// </para>
/// <para>
/// What a closed scope declares is seen from its file alone, as is a type or a scope whose own scope is closed; a
/// declaration kept to a scope (<see cref="ExtractNode.SeenIn"/>) is seen from the declarations written in that scope or
/// inside it alone. A file that shares another's imports (<see cref="ExtractImport.Shares"/>) imports what that one
/// does, after its own. A name an import's prefix is stands for that import wherever a value or a type is written with
/// it. A type is looked in before its bases, and its bases nearest first: a mixin before what it is applied over, the last
/// one first, the first base that holds the name deciding. A type's bases are found a round at a time - the first with no
/// base known, each next through the bases the round before found - until a round finds what the one before did, or
/// comes back to bases a round before found, a cycle no round leaves: a base named through any number of bases of bases
/// is found, and what a base is never hangs on the order the files come in.
/// </para>
/// <para>
/// A chain of names written as links (<see cref="ExtractQualifier.Link"/>) is read a link at a time, each link's chain
/// answered once for each declaration that reads through it (<see cref="Memo{TKey, TAnswer}"/>) and each link stepped once
/// for each declaration, chain before it and name - so a chain of any length costs a step a link, every reference made on
/// it reads the links before it once, and a chain several statements write alike is stepped once. What a link read is kept
/// once (<see cref="KeptReads"/>), the reads of the link before it read through whole, so a chain of n links keeps n links'
/// reads however many of its references read it. A chain whose head names a type - a qualifier's, or the declared type of a
/// variable - is looked for as the whole name its links write, that name carried as the one before it and the link's own,
/// never written out: it is found through the declarations' qualified names split at their dots, so a chain of n links
/// costs a step a link however long the name it writes grows.
/// </para>
/// <para>
/// What a file exposes through the files it exports, an export's filter applied in turn, is walked once for each file and
/// name it is asked for, each file once however many exports reach it, and kept for every reference that asks it again. A
/// file whose extract names what it declares in the scope its directory's path gave (<see cref="FileExtract.FolderScope"/>)
/// is read in the scope the nearest directory above it names for the files under it (<see cref="ExtractImport.Namespace"/>)
/// instead, each directory between the two a name after that one - its ids kept, so what is shown of it never moves.
/// </para>
/// <para>
/// <c>dispatches</c>, <c>declares</c>, <c>tests</c> and <c>partOf</c> are derived after, from the <c>implements</c> edges and
/// the marks every extract may set, and so is the keys' join - the <c>uses</c> a configuration key gains from the literals
/// and the values that name it. The extracts keep names and not targets, so linking again after a file changed binds a name it
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
    public const int Revision = 11;

    /// <summary>
    /// Links <paramref name="files"/>, one tree's every file, into its symbols and edges. The keys of the configuration
    /// files among them are joined to the literals and the members that name them under <paramref name="keyNaming"/> - the
    /// naming policies the repository declares - or under the default of <see cref="CodebaseGraphShape.DefaultKeyNaming"/>
    /// where none is given.
    /// </summary>
    /// <param name="files">Every file the graph keeps, each once: those whose declarations are kept with their extracts.</param>
    /// <param name="keyNaming">The naming policies a key's last segment is joined by; null for the default.</param>
    /// <exception cref="ArgumentException">Two of <paramref name="files"/> have one path.</exception>
    public static CodeGraphLinked Link(IReadOnlyList<CodeGraphFile> files, IReadOnlyList<string>? keyNaming = null)
    {
        ArgumentNullException.ThrowIfNull(files);
        return new Linking(files, previous: null, CodebaseGraphShape.NamingOf(keyNaming)).Link();
    }

    /// <summary>
    /// Links <paramref name="files"/> again from what linking gave <paramref name="previous"/>: a file given as the very
    /// object that link was given is unchanged, and only the references a change could have changed are found again -
    /// those of the files that changed, and those whose finding read something a change altered. What it gives is what
    /// <see cref="Link"/> gives for the same files; <see cref="CodeGraphLinked.References"/> counts what it found again.
    /// The keys' join is made again whole, for it is derived and not found.
    /// </summary>
    /// <param name="previous">What an earlier link of the tree gave.</param>
    /// <param name="files">Every file the graph keeps now, each once.</param>
    /// <param name="keyNaming">The naming policies a key's last segment is joined by; null for the default.</param>
    /// <exception cref="ArgumentException">Two of <paramref name="files"/> have one path.</exception>
    public static CodeGraphLinked Relink(CodeGraphLinked previous, IReadOnlyList<CodeGraphFile> files, IReadOnlyList<string>? keyNaming = null)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(files);
        return new Linking(files, previous.Memory, CodebaseGraphShape.NamingOf(keyNaming)).Link();
    }

    /// <summary>One declaration of one file, placed.</summary>
    /// <param name="Node">What its extract says of it.</param>
    /// <param name="File">The file that declares it.</param>
    /// <param name="ScopeKey">The scope it is declared in, by key.</param>
    /// <param name="FileOnly">Whether it is its file's alone: declared in a closed scope, or a type or scope whose own scope is closed.</param>
    /// <param name="SeenKey">The scope whose declarations alone see it, by key; <see langword="null"/> where it is kept to none.</param>
    private sealed record Declaration(ExtractNode Node, CodeGraphFile File, ScopeKey? ScopeKey, bool FileOnly, ScopeKey? SeenKey)
    {
        public QualifiedName Id => Node.Id;
    }

    /// <summary>
    /// A scope as the linker holds it and a finding reads it: its qualified name, and the path of the file whose own it is
    /// where that file declares it closed - every file's where it is open. The name is the very name its extract holds -
    /// the one before its own part and that part - never a copy or a writing-out of it, so a scope nested any depth costs
    /// its tables a reference and the part the name adds, not its whole name again.
    /// </summary>
    /// <param name="Closed">The path of the file it is the closed scope of; <see langword="null"/> where it is open.</param>
    /// <param name="Name">Its qualified name.</param>
    internal readonly record struct ScopeKey(string? Closed, QualifiedName Name)
    {
        /// <summary>The scope as a part a file holds of a key is written (<see cref="Linking"/>'s parts): its file and name, apart.</summary>
        public override string ToString() => Closed is { } path ? $"closed\u0000{path}\u0000{Name.Whole()}" : $"open\u0000{Name.Whole()}";
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

        /// <summary>The scope one scope is declared in: keyed in a table of its own, by the scope as the linker holds it (<see cref="ScopeKey"/>).</summary>
        Scope,

        /// <summary>The bases of one type, by its id.</summary>
        Bases,

        /// <summary>What every file of one language imports, and what every file of it under a directory does.</summary>
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

        /// <summary>
        /// How long the suffixes one file's nodes write onto others' names are (<see cref="ExtractNode.SuffixOf"/>), those
        /// they are exported by among them - and, under no file's path, every file's: what a name is cut by to be matched.
        /// </summary>
        Suffixes,

        /// <summary>The parts a Blazor component's file and its code-behind's declare, by the component's path: which parts their files join.</summary>
        Siblings,

        /// <summary>
        /// The declarations of other files that name one file their template (<see cref="ExtractNode.Templates"/>), by the
        /// template's path: what a template's code reads by itself is found on.
        /// </summary>
        Templates,

        /// <summary>
        /// The directories a unit of their language's layout is around the files in them (<see cref="FileExtract.FolderUnit"/>), each by
        /// its path: what an import of a layout's place in one is passed over by.
        /// </summary>
        Unit,

        /// <summary>
        /// The roots of the layout, under no one key's text: every unit's directory, which says whether a unit is outermost, and the
        /// roots each project file declares (<see cref="FileExtract.LayoutRoots"/>) - what an import under a root is found through
        /// (<see cref="CodeImportForms.Rooted"/>).
        /// </summary>
        LayoutRoots,

        /// <summary>
        /// The members declared to extend a type of one name (<see cref="ExtractNode.Extends"/>), by the name's last part: what tells a
        /// function their prototype's members extend - a constructor function - from any other.
        /// </summary>
        Extending,
    }

    /// <summary>One reference's finding: its edges, whether it named nothing, and every key it read.</summary>
    /// <param name="Edges">The edges it makes.</param>
    /// <param name="External">Whether it named nothing the repository declares, so it makes none and is counted external.</param>
    /// <param name="Reads">Every key its finding read.</param>
    internal sealed record Finding(IReadOnlyList<CodeEdge> Edges, bool External, KeptReads Reads);

    /// <summary>
    /// What a finding read, kept so that a part many findings read is kept once: the keys read directly, as
    /// <see cref="LinkKeys"/> numbers them, and the reads read through whole - a chain's link's, its own keys and the reads
    /// of the link before it - so a chain of n links keeps each link's keys once, however many of its references read it.
    /// </summary>
    /// <param name="keys">The keys read directly.</param>
    /// <param name="through">The reads read through, each whole.</param>
    internal sealed class KeptReads(int[] keys, KeptReads[] through)
    {
        /// <summary>No read at all.</summary>
        public static KeptReads None { get; } = new([], []);

        /// <summary>The keys read directly.</summary>
        public int[] Keys { get; } = keys;

        /// <summary>The reads read through, each whole.</summary>
        public KeptReads[] Through { get; } = through;
    }

    /// <summary>
    /// Whether reads read any of <paramref name="keys"/>: each part that findings share looked at once, on the memo's own
    /// stack however long the chain of parts.
    /// </summary>
    /// <param name="keys">The keys looked for.</param>
    internal sealed class ReadHits(HashSet<int> keys)
    {
        private readonly Memo<KeptReads, bool> _hits = new(
            reads => reads.Through,
            (reads, through) => through.Any(hit => hit) || reads.Keys.Any(keys.Contains),
            _ => false,
            ReferenceEqualityComparer.Instance);

        /// <summary>Whether <paramref name="reads"/> read any of the keys, directly or through any part.</summary>
        public bool Of(KeptReads reads) => reads.Keys.Any(keys.Contains) || reads.Through.Any(_hits.Of);
    }

    /// <summary>
    /// What one file's references found: its bases' references in each round of the bases (<see cref="Linking.Link"/>),
    /// and every other reference once the bases are known. An unchanged file shares the arrays of the link it was linked
    /// again from until one of its references is found again.
    /// </summary>
    internal sealed class FileFound
    {
        private readonly int _count;
        private readonly List<Finding?[]> _rounds;
        private readonly List<bool> _roundsOwned;
        private bool _othersOwned;

        /// <summary>A file's findings: <paramref name="rounds"/> and <paramref name="others"/>, its own where <paramref name="owned"/> and else shared until changed.</summary>
        public FileFound(IEnumerable<Finding?[]> rounds, Finding?[] others, int count, bool owned)
        {
            _count = count;
            _rounds = [.. rounds];
            _roundsOwned = [.. _rounds.Select(_ => owned)];
            Others = others;
            _othersOwned = owned;
        }

        /// <summary>What its bases' references found in each round, the first first.</summary>
        public IReadOnlyList<Finding?[]> Rounds => _rounds;

        /// <summary>What its other references found.</summary>
        public Finding?[] Others { get; private set; }

        /// <summary>What the reference at <paramref name="at"/> found in <paramref name="round"/>, where it is held; none past the rounds held.</summary>
        public Finding? Round(int round, int at) => round < _rounds.Count ? _rounds[round][at] : null;

        /// <summary>Sets what the reference at <paramref name="at"/> found in <paramref name="round"/>.</summary>
        public void SetRound(int round, int at, Finding finding)
        {
            while (_rounds.Count <= round)
            {
                _rounds.Add(new Finding?[_count]);
                _roundsOwned.Add(true);
            }

            if (!_roundsOwned[round])
            {
                _rounds[round] = (Finding?[])_rounds[round].Clone();
                _roundsOwned[round] = true;
            }

            _rounds[round][at] = finding;
        }

        /// <summary>Sets what the reference at <paramref name="at"/>, no base's, found.</summary>
        public void SetOther(int at, Finding finding)
        {
            if (!_othersOwned)
            {
                Others = (Finding?[])Others.Clone();
                _othersOwned = true;
            }

            Others[at] = finding;
        }

        /// <summary>Keeps the first <paramref name="count"/> rounds alone: those the link ran, which the next is made from.</summary>
        public void Keep(int count)
        {
            if (_rounds.Count > count)
            {
                _rounds.RemoveRange(count, _rounds.Count - count);
                _roundsOwned.RemoveRange(count, _roundsOwned.Count - count);
            }
        }
    }

    /// <summary>
    /// The number of every key a finding may read: one table for a graph and every link made again from it, which links of
    /// several views may add to at once. A key that is a name (<see cref="QualifiedName"/>) holds the name - the one before
    /// its own part and that part - so what the tables hold grows with the names' own parts and never with the square of
    /// how deep they nest.
    /// </summary>
    internal sealed class LinkKeys
    {
        private readonly ConcurrentDictionary<string, int>[] _texts = [.. Enumerable.Range(0, Enum.GetValues<Read>().Length).Select(_ => new ConcurrentDictionary<string, int>(StringComparer.Ordinal))];

        /// <summary>The tables whose keys are names: an id's and a qualified name's.</summary>
        private readonly ConcurrentDictionary<QualifiedName, int>[] _names = [.. Enumerable.Range(0, Enum.GetValues<Read>().Length).Select(_ => new ConcurrentDictionary<QualifiedName, int>())];

        /// <summary>The scopes' keys, each a scope as the linker holds it: its name the extract's own.</summary>
        private readonly ConcurrentDictionary<ScopeKey, int> _scopes = new();
        private int _next;
        private long _characters;

        /// <summary>
        /// How many characters the texts of every key held come to, each once: what the link keeps for every one made again
        /// from it, which grows with what the findings read - each text a name the tree holds, or one a reference writes - and
        /// never with what a chain of links rebuilt. A key that is a name counts what the name itself holds, its own part,
        /// for the name before it is held by that and counted with it, never by each of its own.
        /// </summary>
        public long Characters => Interlocked.Read(ref _characters);

        public int Of(int table, string text) => Of(_texts[table], text, text.Length);

        public int Of(int table, QualifiedName name) => Of(_names[table], name, name.Held);

        public int Of(ScopeKey scope) => Of(_scopes, scope, scope.Name.Held);

        /// <summary>The number of <paramref name="key"/> in <paramref name="table"/>, given it where it has none: its characters counted once, by the add that held.</summary>
        private int Of<TKey>(ConcurrentDictionary<TKey, int> table, TKey key, int characters)
            where TKey : notnull
        {
            if (table.TryGetValue(key, out var number))
            {
                return number;
            }

            number = Interlocked.Increment(ref _next);

            if (table.TryAdd(key, number))
            {
                Interlocked.Add(ref _characters, characters);
                return number;
            }

            return table[key];
        }
    }

    /// <summary>What a link keeps for the next to be made from it: the files it was given, what each reference found, and each type's bases after each round.</summary>
    internal sealed class LinkMemory
    {
        internal LinkMemory(LinkKeys keys, IReadOnlyDictionary<string, CodeGraphFile> files, IReadOnlyDictionary<string, FileFound> found, IReadOnlyList<Dictionary<QualifiedName, List<string[]>>> rounds, IReadOnlyDictionary<string, FolderRead> folders)
        {
            Keys = keys;
            Files = files;
            Found = found;
            Rounds = rounds;
            Folders = folders;
        }

        internal LinkKeys Keys { get; }

        /// <summary>The files as the link read them: each given, but one read in the scope a directory above it names (<see cref="Folders"/>).</summary>
        internal IReadOnlyDictionary<string, CodeGraphFile> Files { get; }

        /// <summary>Each file read in the scope a directory above it names, by its path: the file given, the scope, and the file as it was read.</summary>
        internal IReadOnlyDictionary<string, FolderRead> Folders { get; }

        internal IReadOnlyDictionary<string, FileFound> Found { get; }

        /// <summary>Each type's bases after each round of the bases, the last the bases every other reference was found through.</summary>
        internal IReadOnlyList<Dictionary<QualifiedName, List<string[]>>> Rounds { get; }
    }

    /// <summary>A file read in the scope a directory above it names (<see cref="FileExtract.FolderScope"/>): the file given, that scope, and the file as the link reads it.</summary>
    /// <param name="Given">The file as it was given.</param>
    /// <param name="Scope">The scope it is read in.</param>
    /// <param name="Read">The file as the link reads it: its extract's names of its directory's scope in that one, its ids kept.</param>
    internal sealed record FolderRead(CodeGraphFile Given, string Scope, CodeGraphFile Read);

    private sealed class Linking
    {
        private readonly IReadOnlyList<CodeGraphFile> _files;

        /// <summary>Each file read in the scope a directory above it names, by its path (<see cref="Folded"/>).</summary>
        private readonly Dictionary<string, FolderRead> _folders = new(StringComparer.Ordinal);
        private readonly HashSet<string> _paths;

        /// <summary>The directories a file of the tree says are a unit of their language's layout (<see cref="FileExtract.FolderUnit"/>): what an import of a layout's place in one is passed over by (<see cref="IsUnit"/>).</summary>
        private readonly HashSet<string> _units = new(StringComparer.Ordinal);

        /// <summary>The directories project files of the tree declare roots of the layout (<see cref="FileExtract.LayoutRoots"/>), each from the tree's root.</summary>
        private readonly HashSet<string> _declaredRoots = new(StringComparer.Ordinal);

        /// <summary>The roots of the layout (<see cref="LayoutRoots"/>), made where first asked.</summary>
        private IReadOnlyList<string>? _layoutRoots;
        private readonly Dictionary<string, CodeGraphFile> _byPath = new(StringComparer.Ordinal);
        private readonly LinkMemory? _previous;
        private readonly LinkKeys _keys;

        /// <summary>Each file's node's id by its path, made once a file: every edge from or to a file names that one text.</summary>
        private readonly Dictionary<string, string> _fileIds = new(StringComparer.Ordinal);

        /// <summary>The naming policies a key's last segment is joined to a member's name by, in the order declared.</summary>
        private readonly IReadOnlyList<string> _keyNaming;

        /// <summary>Every path by its file's name, the last part of it: where an import by a path's ending looks.</summary>
        private readonly Dictionary<string, List<string>> _byFileName = new(StringComparer.Ordinal);

        /// <summary>The files each import of each file names, once found, with what finding them read.</summary>
        private readonly Dictionary<CodeGraphFile, Dictionary<ExtractImport, (IReadOnlyList<string> Value, KeptReads Reads)>> _imported = new(ReferenceEqualityComparer.Instance);
        private readonly List<Declaration> _declarations = [];

        /// <summary>Each file's declarations by id: the one a reference of that file is made from.</summary>
        private readonly Dictionary<CodeGraphFile, Dictionary<QualifiedName, Declaration>> _owners = new(ReferenceEqualityComparer.Instance);

        /// <summary>What every file of each language imports (<see cref="ExtractImport.Global"/>), with the file that says so.</summary>
        private readonly Dictionary<string, List<(CodeGraphFile File, ExtractImport Import)>> _global = new(StringComparer.Ordinal);

        /// <summary>The scope imports each file of a language declares for the whole of its directory's subtree: a Razor <c>_ViewImports.cshtml</c>'s <c>@using</c> among them.</summary>
        private readonly Dictionary<string, List<(string Directory, CodeGraphFile File, ExtractImport Import)>> _ofDirectory = new(StringComparer.Ordinal);
        private readonly Dictionary<QualifiedName, List<Declaration>> _byId = [];

        /// <summary>Every declaration by the scope it is declared in and its name.</summary>
        private readonly Dictionary<ScopeKey, Dictionary<string, List<Declaration>>> _byScope = [];

        /// <summary>Every declaration any file can see by its name: none in a closed scope.</summary>
        private readonly Dictionary<string, List<Declaration>> _byName = new(StringComparer.Ordinal);

        /// <summary>Each file's declarations in its closed scopes, by name: seen from that file alone.</summary>
        private readonly Dictionary<string, Dictionary<string, List<Declaration>>> _closedByFile = new(StringComparer.Ordinal);

        /// <summary>Every type and namespace by its qualified name.</summary>
        private readonly Dictionary<QualifiedName, List<Declaration>> _containers = [];

        /// <summary>Each scope's parent scope, by key, as the extracts declare or their containers place it.</summary>
        private readonly Dictionary<ScopeKey, ScopeKey?> _scopeParents = [];

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

        /// <summary>
        /// How long the suffixes each file's nodes write onto others' names are (<see cref="ExtractNode.SuffixOf"/>), those
        /// they are exported by among them, shortest first: the ends of a name such a node may stand for.
        /// </summary>
        private readonly Dictionary<CodeGraphFile, int[]> _suffixesByFile = new(ReferenceEqualityComparer.Instance);

        /// <summary>How long the suffixes every file's nodes write are, shortest first.</summary>
        private int[] _suffixes = [];

        /// <summary>The most names any node is exported by: each form of a name a node written onto others' is matched in.</summary>
        private int _mostExports;

        /// <summary>Each file's closed scopes at its top, by key: found once for each file.</summary>
        private readonly Dictionary<CodeGraphFile, IReadOnlyList<ScopeKey>> _tops = new(ReferenceEqualityComparer.Instance);

        /// <summary>The names of the closed scopes each file's extract declares: found once a file, as every key of one of its names is.</summary>
        private readonly Dictionary<CodeGraphFile, HashSet<QualifiedName>> _closed = new(ReferenceEqualityComparer.Instance);

        /// <summary>Each member whose body is a scope of its own, as what the references made in that body are looked for from (<see cref="LookedFrom"/>): made once a member.</summary>
        private readonly Dictionary<Declaration, Declaration> _bodies = new(ReferenceEqualityComparer.Instance);

        /// <summary>The containers a written name was found to be, from one file and scope: a name such as a type no file declares is looked for once.</summary>
        private readonly Dictionary<(string Path, ScopeKey? Scope, string Written), (IReadOnlyList<Declaration> Value, KeptReads Reads)> _containersFound = [];

        /// <summary>Every type's and scope's qualified name by the run of a name's characters it ends with, in ordinal order: where an ending is looked for.</summary>
        private readonly Dictionary<string, List<QualifiedName>> _byTrailing = new(StringComparer.Ordinal);

        /// <summary>The containers each written name ends, as the last way a qualifier is found: looked for once.</summary>
        private readonly Dictionary<string, (IReadOnlyList<Declaration> Value, KeptReads Reads)> _endings = new(StringComparer.Ordinal);

        /// <summary>Each chain of names written as links, by the chain before its last name and that name: one for each such names, however many write them.</summary>
        private readonly Dictionary<(ChainName? Before, string Name), ChainName> _chainNames = [];

        /// <summary>The names the chain each linked reference ends writes, by its file and its place among the file's references.</summary>
        private readonly Dictionary<(CodeGraphFile File, int At), ChainName> _chainOf = [];

        /// <summary>The containers a chain of names was found to be, from one file and scope (<see cref="NamedContainers(Declaration, ChainName)"/>).</summary>
        private readonly Dictionary<(string Path, ScopeKey? Scope, ChainName Written), (IReadOnlyList<Declaration> Value, KeptReads Reads)> _chainContainersFound = [];

        /// <summary>The containers each chain of names ends (<see cref="Ending(Declaration, ChainName)"/>), looked for once.</summary>
        private readonly Dictionary<ChainName, (IReadOnlyList<Declaration> Value, KeptReads Reads)> _chainEndings = new(ReferenceEqualityComparer.Instance);

        /// <summary>Every type's and scope's qualified name as the names its dots part it into, made where a chain is first looked for in it.</summary>
        private NameTrie? _qualifiedTrie;

        /// <summary>Every declaration's name that holds a dot - its own, and each it is exported by - as the names its dots part it into, made where first asked.</summary>
        private NameTrie? _dottedTrie;

        /// <summary>
        /// Where each chain of names stands in <see cref="_qualifiedTrie"/> and in <see cref="_dottedTrie"/> - its first name
        /// walked from their tops - and where the names past its first stand from each scope an alias stands for: each found
        /// once, from where the chain before it stands.
        /// </summary>
        private readonly Dictionary<(NameTrie From, ChainName Chain, bool PastFirst), NameTrie?> _chainsAt = [];

        /// <summary>What each file imported by path makes visible of each name, its exports followed: found once, every later finding reading it again.</summary>
        private readonly Dictionary<(string Path, string Name, bool Suffixed), (IReadOnlyList<Declaration> Value, KeptReads Reads)> _visible = [];

        /// <summary>Whether each file publishes each name to an import that takes its published names (<see cref="Publishes"/>): found once.</summary>
        private readonly Dictionary<(string Path, string Name), (bool Value, KeptReads Reads)> _publishes = [];

        /// <summary>The names each file's own list publishes, as a set (<see cref="PublishedOf"/>).</summary>
        private readonly Dictionary<CodeGraphFile, HashSet<string>> _publishedSets = new(ReferenceEqualityComparer.Instance);

        /// <summary>What each file imported makes visible by each name an extension is let by (<see cref="Exposed"/>): found once, every later check reading it again.</summary>
        private readonly Dictionary<(string Start, string Shown), (Exposure Value, KeptReads Reads)> _exposed = [];

        /// <summary>Every member declared to extend a type, by its name: what a call a type does not hold may be.</summary>
        private readonly Dictionary<string, List<Declaration>> _extensions = new(StringComparer.Ordinal);

        /// <summary>Every member declared to extend a type, by the last part of the name it extends: what a constructor function's prototype holds (<see cref="Constructs"/>).</summary>
        private readonly Dictionary<string, List<Declaration>> _extending = new(StringComparer.Ordinal);

        /// <summary>What each extension extends, as its own file names it (<see cref="Extended"/>): found once.</summary>
        private readonly Dictionary<Declaration, (IReadOnlyList<Declaration> Value, KeptReads Reads)> _extended = new(ReferenceEqualityComparer.Instance);

        /// <summary>Whether each function is a constructor function another file writes the prototype of (<see cref="Constructs"/>): found once.</summary>
        private readonly Dictionary<Declaration, (bool Value, KeptReads Reads)> _constructs = new(ReferenceEqualityComparer.Instance);

        /// <summary>What each file's references found, by the file.</summary>
        private readonly Dictionary<CodeGraphFile, FileFound> _found = new(ReferenceEqualityComparer.Instance);

        /// <summary>Every reference found in this link, by its file and its place among the file's references, once.</summary>
        private readonly HashSet<(CodeGraphFile File, int At)> _resolved = [];

        private readonly List<CodeEdge> _edges = [];
        private readonly Dictionary<string, int> _external = new(StringComparer.Ordinal);

        /// <summary>
        /// Each type's bases, by id, that a finding looks through now - none in the first round of the bases - as
        /// <see cref="BaseRanksOf"/> ranks them.
        /// </summary>
        private Dictionary<QualifiedName, List<string[]>> _bases = [];

        /// <summary>What the finding under way has read; null outside one.</summary>
        private Reading? _reads;

        /// <summary>What each chain a link ends comes to, from each declaration that reads through it (<see cref="ChainTo"/>).</summary>
        private Memo<ChainKey, ChainAnswer> _chains;

        /// <summary>Each link of a chain stepped, by its declaration, the chain before it and its names (<see cref="NewChains"/>).</summary>
        private readonly Dictionary<ChainLink, ChainAnswer> _chainSteps = new(ChainLink.Comparer);

        /// <summary>How many links of chains were stepped (<see cref="CodeGraphLinked.ChainLinks"/>).</summary>
        private int _chainLinks;

        /// <summary>How many files the walks of what a file imported exposes visited (<see cref="CodeGraphLinked.ExposedFiles"/>).</summary>
        private int _exposedFiles;

        /// <summary>The parts each type's files join to it though their qualified names differ (<see cref="CodeBehinds"/>), by the type's id: each of the other side, by id.</summary>
        private readonly Dictionary<QualifiedName, List<QualifiedName>> _siblings = [];

        /// <summary>
        /// The declarations that name the file at a path their template (<see cref="ExtractNode.Templates"/>), each resolved
        /// beside the file that names it: what a template's own names are members of, where its file declares no type.
        /// </summary>
        private readonly Dictionary<string, List<Declaration>> _templates = new(StringComparer.Ordinal);

        /// <summary>
        /// The scopes that hold members reached by a receiver alone (<see cref="ExtractNode.MembersByReceiver"/>) - a type's own,
        /// and the one a member added to a type from outside its body is declared in: where a name written alone passes those
        /// members over (<see cref="ByReceiver"/>), and their type's bases, and finds the rest the scope holds.
        /// </summary>
        private readonly HashSet<ScopeKey> _byReceiver = [];

        public Linking(IReadOnlyList<CodeGraphFile> files, LinkMemory? previous, IReadOnlyList<string> keyNaming)
        {
            _chains = NewChains();
            // In one order whatever order they came in: a link made again is the whole link of the same files.
            _files = [.. Folded(files, previous).OrderBy(file => file.Path, StringComparer.Ordinal)];
            _previous = previous;
            _keys = previous?.Keys ?? new LinkKeys();
            _keyNaming = keyNaming;
            _paths = new HashSet<string>(StringComparer.Ordinal);

            foreach (var file in _files)
            {
                if (!_paths.Add(file.Path))
                {
                    throw new ArgumentException($"two files of the tree have the path '{file.Path}'", nameof(files));
                }

                _byPath[file.Path] = file;
                _fileIds[file.Path] = CodeGraphIds.FileId(file.Path);
                Add(_byFileName, CodeGraphIds.FileName(file.Path), file.Path);

                if (file.Extract is not { } extract)
                {
                    continue;
                }

                var owners = _owners[file] = [];
                var top = _topByFile[file] = new(StringComparer.Ordinal);
                var closed = _closed[file] = Closed(extract);

                foreach (var import in extract.Imports.Where(import => import.Global && file.Language is not null))
                {
                    Add(_global, file.Language!, (file, import));
                }

                foreach (var import in extract.Imports.Where(import => import.Form == CodeImportForms.Directory && file.Language is not null))
                {
                    Add(_ofDirectory, file.Language!, (file.Path[..(file.Path.LastIndexOf('/') + 1)], file, import));
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

                if (SuffixLengths(extract) is { Length: > 0 } suffixes)
                {
                    _suffixesByFile[file] = suffixes;
                }

                // The file that says its directory is a unit of its language's layout: the files in it look a bare name up from above it.
                if (extract.FolderUnit)
                {
                    _units.Add(DirectoryOf(file.Path));
                }

                // A root a project file declares that climbs out of the tree stands for nothing in it.
                foreach (var root in extract.LayoutRoots ?? [])
                {
                    if (CodeGraphIds.Beside(file.Path, root) is { } directory)
                    {
                        _declaredRoots.Add(directory);
                    }
                }

                foreach (var node in extract.Nodes)
                {
                    _mostExports = Math.Max(_mostExports, node.Exports?.Count ?? 0);
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

                    // A component's element selectors and a pipe's name after its bar are names the repository finds it by, as
                    // its own: a tag or a pipe markup writes names one, and no code's name does - nothing code writes holds a dash
                    // or a bar (ExtractNode.IsSelector).
                    foreach (var selector in node.Selectors ?? [])
                    {
                        Add(_byName, selector, declaration);
                    }

                    // A file a declaration names its template: what that file's code reads by itself is its member.
                    foreach (var template in node.Templates ?? [])
                    {
                        if (CodeGraphIds.Beside(file.Path, template) is { } named)
                        {
                            Add(_templates, named, declaration);
                        }
                    }

                    if (node.Extends is not null)
                    {
                        Add(_extensions, node.Name, declaration);
                        Add(_extending, SimpleName(node.Extends), declaration);
                    }

                    // Members reached by a receiver alone: a type's own scope, or the one a member added from outside its type's
                    // body is declared in, holds members a name written alone passes over.
                    if (node.MembersByReceiver && (node.Kind == CodeNodeKinds.Type ? KeyOf(file, node.Qualified, closed) : declaration.ScopeKey) is { } passed)
                    {
                        _byReceiver.Add(passed);
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

            foreach (var qualified in _containers.Keys.Order(ByText))
            {
                Add(_byTrailing, Trailing(qualified.Whole()), qualified);
            }

            _suffixes = [.. _suffixesByFile.Values.SelectMany(lengths => lengths).Distinct().Order()];

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

            // A component and its code-behind are one type, whatever namespaces their files put them in: each finds the
            // other's members as its own.
            foreach (var (codeBehind, component) in CodeBehinds())
            {
                foreach (var (one, other) in (ReadOnlySpan<(Declaration, Declaration)>)[(codeBehind, component), (component, codeBehind)])
                {
                    var siblings = _siblings.TryGetValue(one.Id, out var held) ? held : _siblings[one.Id] = [];

                    if (!siblings.Contains(other.Id))
                    {
                        siblings.Add(other.Id);
                    }
                }
            }
        }

        /// <summary>
        /// <paramref name="files"/> as the link reads them: each whose extract says the scope its directory's path gave what it
        /// declares (<see cref="FileExtract.FolderScope"/>) read in the scope the nearest directory above it, of its language,
        /// names for every file under it (<see cref="ExtractImport.Namespace"/>) - each directory between the two a name after
        /// that one, as Razor names a component's namespace - its ids kept; every other as it was given. A file read so is
        /// the very file the link this is made from read where neither it nor the scope moved, so it is no change; and a new
        /// one where either did, so what it holds of every key is found again.
        /// </summary>
        private IEnumerable<CodeGraphFile> Folded(IReadOnlyList<CodeGraphFile> files, LinkMemory? previous)
        {
            // The scope each directory names, by its language: the first of the file of it that names one.
            var named = new Dictionary<(string Language, string Directory), string>();

            foreach (var file in files.OrderBy(file => file.Path, StringComparer.Ordinal))
            {
                if (file is { Language: { } language, Extract: { } extract } && extract.Imports.FirstOrDefault(import => import.Namespace && import.Form == CodeImportForms.Directory) is { } scope)
                {
                    named.TryAdd((language, file.Path[..(file.Path.LastIndexOf('/') + 1)]), scope.Target);
                }
            }

            foreach (var file in files)
            {
                if (named.Count == 0 || file is not { Language: { } language, Extract: { FolderScope: { } folder } extract })
                {
                    yield return file;
                    continue;
                }

                // The nearest directory above the file that names a scope, and the file's directories past it: the folder's
                // scope holds a name for each directory the file is in.
                var names = folder.Length == 0 ? [] : folder.Split('.');
                var nearest = named.Where(entry => entry.Key.Language == language && file.Path.StartsWith(entry.Key.Directory, StringComparison.Ordinal))
                    .OrderByDescending(entry => entry.Key.Directory.Length)
                    .Select(entry => (Directory: entry.Key.Directory, Scope: entry.Value))
                    .FirstOrDefault();

                if (nearest.Scope is null || names.Length != file.Path.Count(character => character == '/'))
                {
                    yield return file;
                    continue;
                }

                var scope = string.Join('.', [nearest.Scope, .. names[nearest.Directory.Count(character => character == '/')..]]);

                if (previous?.Folders.TryGetValue(file.Path, out var was) == true && ReferenceEquals(was.Given, file) && was.Scope == scope)
                {
                    _folders[file.Path] = was;
                    yield return was.Read;
                    continue;
                }

                var read = file with { Extract = new FolderScopes(folder, scope).Of(extract) };
                _folders[file.Path] = new FolderRead(file, scope, read);
                yield return read;
            }
        }

        /// <summary>
        /// Each part a Blazor component's code-behind file declares with the part of its name the component's file declares
        /// - <c>Cart.razor.cs</c> beside <c>Cart.razor</c>, as Razor pairs them - whatever namespace the tree's path gives the
        /// component, for the namespace Razor compiles it into is its project's, which the file does not say: one type,
        /// joined by its files. The one rule that pairs them, which the <c>partOf</c> edges and every lookup in a type read.
        /// </summary>
        private IEnumerable<(Declaration CodeBehind, Declaration Component)> CodeBehinds()
        {
            var components = _declarations.Where(declaration => declaration.Node.Part && declaration.File.Path.EndsWith(".razor", StringComparison.OrdinalIgnoreCase))
                .ToLookup(declaration => (declaration.File.Path, SimpleName(declaration.Node.Qualified)));

            foreach (var part in _declarations.Where(declaration => declaration.Node.Part && declaration.File.Path.EndsWith(".razor.cs", StringComparison.OrdinalIgnoreCase)))
            {
                foreach (var component in components[(part.File.Path[..^3], SimpleName(part.Node.Qualified))])
                {
                    yield return (part, component);
                }
            }
        }

        /// <summary>The path of the Blazor component <paramref name="path"/> is, or is the code-behind of; none where it is neither.</summary>
        private static string? ComponentPath(string path)
            => path.EndsWith(".razor", StringComparison.OrdinalIgnoreCase) ? path
                : path.EndsWith(".razor.cs", StringComparison.OrdinalIgnoreCase) ? path[..^3]
                : null;

        /// <summary>One round of the bases (<see cref="Link"/>): which it is, and each type's bases it found.</summary>
        private sealed record BaseRound(int Round, Dictionary<QualifiedName, List<string[]>> Bases);

        public CodeGraphLinked Link()
        {
            Structure();

            var changed = Changed();
            var references = _files.Where(file => file.Extract is not null).ToList();

            foreach (var file in references)
            {
                var count = file.Extract!.References.Count;

                _found[file] = _previous is { } previous && previous.Files.TryGetValue(file.Path, out var was) && ReferenceEquals(was, file)
                    ? new FileFound(previous.Found[file.Path].Rounds, previous.Found[file.Path].Others, count, owned: false)
                    : new FileFound([], new Finding?[count], count, owned: true);
            }

            // Bases first: a member is looked for in a type's bases, which their own references name - and a base may be
            // named through a base of a base, any number of levels up. So the bases are found a round at a time, each round
            // through the bases the round before found, the first through none, until a round finds what the one before did
            // - a base forty levels up is found in the forty-first - or comes back to bases a round before found, which is
            // a cycle no further round leaves. A round finds again only what read a base that moved; and no base hangs on
            // the order the files come in.
            var rounds = new List<Dictionary<QualifiedName, List<string[]>>>();
            var end = Fixpoint.Run(
                new BaseRound(-1, []),
                round =>
                {
                    var bases = Round(references, round.Round + 1, changed, rounds);
                    rounds.Add(bases);
                    return new FixpointStep<BaseRound>(new BaseRound(round.Round + 1, bases), Done: false);
                },
                round => Written(round.Bases));

            foreach (var found in _found.Values)
            {
                found.Keep(rounds.Count);
            }

            _bases = end.State.Bases;
            Forget();
            TakeBases(references, rounds.Count - 1);
            Others(references, [.. changed, .. Moved(_previous?.Rounds[^1], _bases)]);

            Imports();
            Dispatches();
            Declares();
            Tests();
            Parts();
            Keys();

            // Every edge once: one found more than one way is as sure as its surest finding.
            var edges = _edges.GroupBy(edge => (edge.From, edge.To, edge.Kind, edge.Line))
                .Select(alike => alike.First() with { Confidence = alike.Select(edge => edge.Confidence).Aggregate(CodeConfidences.Surer) })
                .ToList();
            var symbols = _files.Select(file => new CodeGraphSymbol(FileIdOf(file.Path), CodeNodeKinds.File, [file.Path]))
                .Concat(_byId.Select(pair => new CodeGraphSymbol(pair.Key.Whole(), pair.Value[0].Node.Kind, [.. pair.Value.Select(declaration => declaration.File.Path).Distinct(StringComparer.Ordinal)])))
                .ToList();

            return new CodeGraphLinked(symbols, edges, _external, _resolved.Count)
            {
                Memory = new LinkMemory(_keys, _byPath, _found.ToDictionary(found => found.Key.Path, found => found.Value, StringComparer.Ordinal), rounds, _folders),
                ChainLinks = _chainLinks,
                ExposedFiles = _exposedFiles,
            };
        }

        /// <summary>
        /// One round of the bases: each base's reference found through the bases the round before found - none for the first
        /// - and the bases that gives, by type. A reference keeps what it found where finding it again would find the same:
        /// what the link this is made from found in the same round, where its file did not change and it read nothing
        /// <paramref name="changed"/> holds, nor a type whose bases that round was given otherwise; else what it found in the
        /// round before, where it read no type whose bases moved between the two rounds.
        /// </summary>
        /// <param name="files">The files whose references are found.</param>
        /// <param name="round">The round, from 0.</param>
        /// <param name="changed">Every key a change altered since the link this is made from.</param>
        /// <param name="rounds">The bases each round before found.</param>
        private Dictionary<QualifiedName, List<string[]>> Round(List<CodeGraphFile> files, int round, HashSet<int> changed, List<Dictionary<QualifiedName, List<string[]>>> rounds)
        {
            Dictionary<QualifiedName, List<string[]>> none = [];
            var given = round == 0 ? none : rounds[round - 1];

            // What moved between the bases the round before was given and those this one is, in this link; and between those
            // this round was given in the link this is made from and now, where that link ran this round.
            var moved = new ReadHits(round == 0 ? [] : [.. Moved(round == 1 ? none : rounds[round - 2], given)]);
            var since = _previous is { } previous && round < previous.Rounds.Count
                ? new ReadHits([.. changed, .. Moved(round == 0 ? none : previous.Rounds[round - 1], given)])
                : null;

            _bases = given;
            Forget();

            Dictionary<QualifiedName, List<string[]>> bases = [];

            foreach (var file in files)
            {
                var references = file.Extract!.References;
                var owners = _owners[file];
                var found = _found[file];

                for (var at = 0; at < references.Count; at++)
                {
                    var reference = references[at];

                    if (!IsBase(reference.Kind))
                    {
                        continue;
                    }

                    var from = owners[reference.From];
                    var kept = since is not null && found.Round(round, at) is { } before && !since.Of(before.Reads) ? before : null;

                    if (kept is null && round > 0 && found.Round(round - 1, at) is { } last && !moved.Of(last.Reads))
                    {
                        kept = last;
                    }

                    if (kept is null)
                    {
                        kept = Find(from, reference, file, at);
                        _resolved.Add((file, at));
                    }

                    if (!ReferenceEquals(kept, found.Round(round, at)))
                    {
                        found.SetRound(round, at, kept);
                    }

                    AddBases(from, kept, bases);
                }
            }

            return bases;
        }

        /// <summary>Takes the edges and counts of every base's reference, as the last round of the bases found it.</summary>
        private void TakeBases(List<CodeGraphFile> files, int round)
        {
            foreach (var file in files)
            {
                var references = file.Extract!.References;

                for (var at = 0; at < references.Count; at++)
                {
                    if (IsBase(references[at].Kind) && _found[file].Round(round, at) is { } finding)
                    {
                        Take(_owners[file][references[at].From], finding, references[at]);
                    }
                }
            }
        }

        /// <summary>
        /// Finds each reference that names no base, through the bases the last round found - or keeps what the link it is
        /// made from found for it, where its file did not change and it read nothing <paramref name="changed"/> holds - and
        /// takes what it found: its edges and its counts.
        /// </summary>
        private void Others(List<CodeGraphFile> files, HashSet<int> changed)
        {
            var hits = new ReadHits(changed);

            foreach (var file in files)
            {
                var references = file.Extract!.References;
                var owners = _owners[file];
                var found = _found[file];

                for (var at = 0; at < references.Count; at++)
                {
                    var reference = references[at];

                    if (IsBase(reference.Kind))
                    {
                        continue;
                    }

                    var from = owners[reference.From];
                    var kept = found.Others[at];

                    if (kept is null || hits.Of(kept.Reads))
                    {
                        kept = Find(from, reference, file, at);
                        found.SetOther(at, kept);
                        _resolved.Add((file, at));
                    }

                    Take(from, kept, reference);
                }
            }
        }

        /// <summary>
        /// The bases one base's reference found, into <paramref name="bases"/>: a mixin's findings a rank of their own,
        /// nearer than every base named before it; an implemented base's joining the rank before the last, an inherited
        /// one's the last - each empty while the type names none - as <see cref="BaseRanksOf"/> ranks them.
        /// </summary>
        private static void AddBases(Declaration from, Finding finding, Dictionary<QualifiedName, List<string[]>> bases)
        {
            if (finding.Edges.Count == 0)
            {
                return;
            }

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

        /// <summary>Takes what one reference found: its edges and its counts.</summary>
        private void Take(Declaration from, Finding finding, ExtractReference reference)
        {
            _edges.AddRange(finding.Edges);

            // A use that names nothing the repository declares is the common case - a member of a type the repository
            // does not hold, a value of the language's own - and says nothing of how sure the graph is of what it does
            // declare: a use is recall, never a doubt, so its findings count nowhere the unsureness of a node or a file
            // is read from.
            if (CodeEdgeKinds.Recall(reference.Kind))
            {
                return;
            }

            // A reference that asked whether a declaration is there, and found none (<see cref="ExtractReference.Probe"/>), is no
            // external callee: nothing it depends on is missing.
            if (finding.External && !reference.Probe)
            {
                Count(_external, from.Id.Whole());
            }
        }

        /// <summary>What a written name or a chain was found to be while other bases were known may be otherwise now.</summary>
        private void Forget()
        {
            _containersFound.Clear();
            _chainContainersFound.Clear();
            _extended.Clear();
            _constructs.Clear();
            _chains = NewChains();
            _chainSteps.Clear();
        }

        /// <summary>Every declaration's <c>contains</c> edge, from what it is declared inside: its parent node, or its file.</summary>
        private void Structure()
        {
            foreach (var declaration in _declarations)
            {
                _edges.Add(new CodeEdge((declaration.Node.Parent ?? QualifiedName.Of(FileIdOf(declaration.File.Path))).Whole(), declaration.Id.Whole(), CodeEdgeKinds.Contains, declaration.Node.Start, CodeConfidences.Exact));
            }
        }

        /// <summary>One reference found - the one at <paramref name="at"/> among <paramref name="file"/>'s - its edges or its count, with every key the finding read.</summary>
        private Finding Find(Declaration from, ExtractReference reference, CodeGraphFile file, int at)
        {
            var outer = _reads;
            _reads = new Reading();

            try
            {
                var (all, confidence) = Found(LookedFrom(from, reference), reference, new ReferenceAt(file, at));
                var found = Bodied(all);

                if (found.Count == 0)
                {
                    return new Finding([], External: true, _reads.Kept());
                }

                var sure = found.Count == 1 ? confidence : CodeConfidences.Candidates;

                return new Finding(
                    [.. found.Select(target => new CodeEdge(
                        from.Id.Whole(),
                        target.Id.Whole(),
                        reference.Kind == CodeEdgeKinds.Calls && target.Node.Kind == CodeNodeKinds.Type ? CodeEdgeKinds.Instantiates : reference.Kind,
                        reference.Line,
                        sure))],
                    External: false,
                    _reads.Kept());
            }
            finally
            {
                _reads = outer;
            }
        }

        /// <summary>
        /// Where <paramref name="reference"/>, made from <paramref name="member"/>, is looked for from: the member's body, where
        /// its file declares a closed scope under the qualified name of the member it is (<see cref="ExtractScope"/>) - that
        /// scope first and its parent after it, as a Python function's body is, which the class around it does not enclose -
        /// save a reference made around the member (<see cref="ExtractReference.Around"/>), looked for where the member is
        /// declared; else the member itself, from the scope it is declared in. Its edges are the member's either way.
        /// </summary>
        private Declaration LookedFrom(Declaration member, ExtractReference reference)
        {
            if (reference.Around || member.Node.Kind != CodeNodeKinds.Member || !_closed.TryGetValue(member.File, out var closed) || !closed.Contains(member.Node.Qualified))
            {
                return member;
            }

            if (!_bodies.TryGetValue(member, out var body))
            {
                _bodies[member] = body = member with { ScopeKey = ClosedKey(member.File.Path, member.Node.Qualified) };
            }

            return body;
        }

        /// <summary>
        /// What <paramref name="reference"/> names, one declaration for each id, and how sure the place it was found makes
        /// it: none where nothing the repository declares has that name.
        /// </summary>
        private (IReadOnlyList<Declaration> Found, string Confidence) Found(Declaration from, ExtractReference reference, ReferenceAt place)
        {
            bool Accepts(Declaration target) => (Takes(from, reference, target, imported: false) || Made(target)) && Sees(from, target);

            // What an import of the file that declares it makes visible: a name it binds may be data the file exports.
            bool Imports(Declaration target) => (Takes(from, reference, target, imported: true) || Made(target)) && Sees(from, target);

            // A new of a constructor function another file writes the prototype of makes one, as a new of a type does.
            bool Made(Declaration target) => reference.Kind == CodeEdgeKinds.Instantiates && Constructs(target);

            // A value of a type the code does not say: through the type of the member it is - nothing the repository
            // declares where that type is not the repository's - else, where no value is so named and an import's prefix
            // is, through that import alone, as a file that shares another's imports writes that file's prefixes; else by
            // its name alone, where nothing is known of its type; never in the scopes it is written in, as though it were
            // a call of their own. A chain written as links is two names or more, and so no import's prefix.
            if (reference.Qualifier is { Kind: CodeQualifierKinds.Value } value)
            {
                if (value.Name is not null || value.Link is not null)
                {
                    if (ValuesOf(from, value, place) is { } values)
                    {
                        return Through(from, values, reference.Name, Accepts);
                    }

                    if (value.Name is { } written && ImportsOf(from.File).Any(made => string.Equals(made.Import.Prefix, written.Whole(), StringComparison.Ordinal)))
                    {
                        return (Prefixed(from, written.Whole(), reference.Name, Imports), CodeConfidences.Exact);
                    }
                }

                return ByName(from, reference.Name, Accepts);
            }

            // The qualifier's scope: where the code says, and nowhere else - a callee the qualifier's type does not hold is
            // one the repository does not declare there, but for an extension of that type.
            if (reference.Qualifier is { } qualifier)
            {
                return Qualified(from, qualifier, reference.Name, Accepts, Imports, place);
            }

            return Unqualified(from, reference.Name, Accepts, Imports);
        }

        /// <summary>
        /// What <paramref name="name"/> written with no qualifier names: what the scopes <paramref name="from"/> is written
        /// in declare, nearest first - exact - else what lies beyond them (<see cref="Beyond"/>), what an import makes
        /// visible taken by <paramref name="imported"/>.
        /// </summary>
        private (IReadOnlyList<Declaration> Found, string Confidence) Unqualified(Declaration from, string name, Func<Declaration, bool> accepts, Func<Declaration, bool> imported)
            => Scoped(from, name, accepts) is { Count: > 0 } scoped ? (scoped, CodeConfidences.Exact) : Beyond(from, name, accepts, imported);

        /// <summary>What the file at <paramref name="path"/> declares that another file imports by the name <paramref name="name"/> besides its own.</summary>
        private IReadOnlyList<Declaration> ExportedNamed(string path, string name)
        {
            Touch(Read.Name, name);
            return _byPath.TryGetValue(path, out var file) && _exportedByFile.TryGetValue(file, out var exported) && exported.TryGetValue(name, out var named) ? named : [];
        }

        /// <summary>
        /// What the scopes <paramref name="from"/> is written in declare named <paramref name="name"/>, from the inside out, with each
        /// type's bases: the first that holds one decides - but the members reached by a receiver alone
        /// (<see cref="ExtractNode.MembersByReceiver"/>, <see cref="ByReceiver"/>), which a name written alone does not look for, nor
        /// their type's bases. A scope that holds them holds what else is declared in it all the same - a namespace's members, where
        /// TypeScript merges a class and a namespace of one name into one scope - which the namespace's own code finds; a member
        /// reached by a receiver alone finds nothing of the scope it is declared in, its type's members and a merged namespace's alike,
        /// as a method's body is no scope of its class's.
        /// </summary>
        private IReadOnlyList<Declaration> Scoped(Declaration from, string name, Func<Declaration, bool> accepts)
        {
            foreach (var scope in Chain(from))
            {
                // Whether a scope holds members a name written alone passes over is read under the scope's key, which a type come,
                // gone or changed alters.
                _reads?.Keys.Add(_keys.Of(scope));
                var passed = _byReceiver.Contains(scope);

                if (passed && scope == from.ScopeKey && ByReceiver(from))
                {
                    continue;
                }

                if (InScope(scope, name, passed ? declaration => !ByReceiver(declaration) && accepts(declaration) : accepts, withBases: !passed) is { Count: > 0 } found)
                {
                    return found;
                }
            }

            return [];
        }

        /// <summary>
        /// Whether <paramref name="declaration"/> is reached by a receiver alone (<see cref="ExtractNode.MembersByReceiver"/>): a member of a
        /// type that says so, or a member added to a type from outside its body, which says so itself.
        /// </summary>
        private bool ByReceiver(Declaration declaration)
            => declaration.Node.Kind != CodeNodeKinds.Type && declaration.Node.MembersByReceiver
                || (declaration.Node.Parent is { } parent && _owners[declaration.File].TryGetValue(parent, out var holder) && holder.Node is { Kind: CodeNodeKinds.Type, MembersByReceiver: true });

        /// <summary>
        /// What <paramref name="name"/> is past the scopes <paramref name="from"/> is written in: a prefix written alone,
        /// what its import stands for; else what its file imports, in order, and what every file of its language imports,
        /// each taken by <paramref name="imported"/>; else the repository's.
        /// </summary>
        private (IReadOnlyList<Declaration> Found, string Confidence) Beyond(Declaration from, string name, Func<Declaration, bool> accepts, Func<Declaration, bool> imported)
        {
            if (Aliased(from.File, name).Where(accepts).ToList() is { Count: > 0 } aliased)
            {
                return (aliased, CodeConfidences.Exact);
            }

            foreach (var (import, owner) in ImportsOf(from.File).Where(made => made.Import.Prefix is null && made.Import.Lets(name)))
            {
                if (Imported(import, owner, name, imported) is { Count: > 0 } visible)
                {
                    return (visible, CodeConfidences.Unique);
                }
            }

            // A name this file's language binds within the file alone - by its scopes or its imports - is bound by nothing where
            // neither does (<see cref="FileExtract.BoundWithin"/>): the repository's names are not looked through for it. Nor is a
            // member reached by a receiver alone (ByReceiver) one a name written alone reaches, wherever it is declared.
            return (BoundWithin(from) ? [] : Repository(from, name, declaration => !ByReceiver(declaration) && accepts(declaration)), CodeConfidences.Unique);
        }

        /// <summary>
        /// What <paramref name="name"/>, written where a style sheet's syntax says what it names - a selector, a variable,
        /// a mixin, a function, an animation's keyframes, a font's face (<paramref name="kind"/>) - names: nearest first, as
        /// a name written alone is found past its scopes. What <paramref name="from"/>'s own file declares, exact - a page's
        /// own style block, the sheet's own rule; else what each file its file imports or links makes visible, in order,
        /// unique - an import by a namespace's prefix among them for what a sheet compiles into CSS (a rule, keyframes, a
        /// font face), which no namespace holds, and none for a Sass member (a variable, a mixin, a function), which its
        /// namespace's prefix alone reaches; else the repository's, unique. What a file keeps to itself - a CSS module's
        /// class, a scoped style block's rule - is seen from it alone (<see cref="Sees(Declaration, Declaration)"/>), so no
        /// other file's selector reaches it. A selector is found at each of those places by the rules named so, and by the
        /// ones that stand for a suffix written onto others' (<see cref="ExtractNode.SuffixOf"/>) whose names it is one of
        /// (<see cref="Suffixed"/>). The imports are walked as one: a file a walk of one import reached is not walked again
        /// for the next, which would find there what it found the first time - nothing.
        /// </summary>
        private (IReadOnlyList<Declaration> Found, string Confidence) Styled(Declaration from, string kind, string name, Func<Declaration, bool> accepts)
        {
            var selector = kind == CodeQualifierKinds.Selector;
            var written = selector ? Suffixed(from.File, name, exported: false) : [];

            if (Distinct(RepositoryNamed(from.File, name).Where(target => ReferenceEquals(target.File, from.File)).Concat(written).Where(accepts)) is { Count: > 0 } own)
            {
                return (own, CodeConfidences.Exact);
            }

            var member = kind is CodeQualifierKinds.StyleVariable or CodeQualifierKinds.StyleMixin or CodeQualifierKinds.StyleFunction;
            var walked = new HashSet<string>(StringComparer.Ordinal);

            foreach (var (import, owner) in ImportsOf(from.File).Where(made => !member || (made.Import.Prefix is null && made.Import.Lets(name))))
            {
                if (Imported(import, owner, name, accepts, walked, suffixed: selector) is { Count: > 0 } visible)
                {
                    return (visible, CodeConfidences.Unique);
                }
            }

            return (Distinct(Repository(from, name, accepts).Concat(selector ? Suffixed(from.File, name, exported: false, everywhere: true).Where(accepts) : [])), CodeConfidences.Unique);
        }

        /// <summary>
        /// The nodes of <paramref name="file"/> - of every file where <paramref name="everywhere"/>, as the repository's are
        /// seen from it - standing for a suffix written onto others' names (<see cref="ExtractNode.SuffixOf"/>) that stand
        /// for <paramref name="name"/>: by their names, or where <paramref name="exported"/> by a name they are exported by. Each is looked for by the end of the name its suffix
        /// is - the name cut at each length the suffixes there are - and matched by what it is written onto (<see cref="Writes"/>).
        /// </summary>
        private List<Declaration> Suffixed(CodeGraphFile file, string name, bool exported, bool everywhere = false)
        {
            Touch(Read.Suffixes, everywhere ? string.Empty : file.Path);
            var found = new List<Declaration>();

            foreach (var length in everywhere ? _suffixes : _suffixesByFile.GetValueOrDefault(file) ?? [])
            {
                // What a suffix is written onto writes a character of the name at least.
                if (length >= name.Length)
                {
                    break;
                }

                var end = $"&{name[^length..]}";
                var named = everywhere ? RepositoryNamed(file, end)
                    : exported ? ExportedNamed(file.Path, end)
                    : TopNamed(file.Path, end);

                foreach (var candidate in named.Where(candidate => candidate.Node.SuffixOf is not null))
                {
                    if (exported ? Enumerable.Range(0, _mostExports).Any(form => Writes(candidate, name, form)) : Writes(candidate, name, form: -1))
                    {
                        found.Add(candidate);
                    }
                }
            }

            return found;
        }

        /// <summary>
        /// Whether <paramref name="written"/>, a node standing for a suffix written onto others' names
        /// (<see cref="ExtractNode.SuffixOf"/>), stands for <paramref name="name"/>: by their names, where
        /// <paramref name="form"/> is below 0 - its suffix past its <c>&amp;</c> the end of the name, and the rest one of
        /// the nodes it is written onto stands for in turn, a name of its own or one written onto others' again - or else by
        /// the <paramref name="form"/>-th name each is exported by, the last standing for those past it. Walked with a stack,
        /// each node at each place in the name once, so a node many others are written onto is matched once there however
        /// many reach it.
        /// </summary>
        private bool Writes(Declaration written, string name, int form)
        {
            var walked = new HashSet<(QualifiedName Id, int End)>();
            var pending = new Stack<(Declaration Node, int End)>();
            pending.Push((written, name.Length));
            walked.Add((written.Id, name.Length));

            while (pending.TryPop(out var at))
            {
                var node = at.Node.Node;
                var own = form < 0 ? node.Name : node.Exports is { Count: > 0 } exports ? exports[Math.Min(form, exports.Count - 1)] : null;

                if (own is null)
                {
                    continue;
                }

                if (node.SuffixOf is not { } onto)
                {
                    if (own.Length == at.End && name.AsSpan(0, at.End).SequenceEqual(own))
                    {
                        return true;
                    }

                    continue;
                }

                var suffix = own.AsSpan(1);

                if (suffix.Length >= at.End || !name.AsSpan(0, at.End).EndsWith(suffix))
                {
                    continue;
                }

                var rest = at.End - suffix.Length;

                foreach (var id in onto)
                {
                    foreach (var under in PartsOf(id).Where(under => ReferenceEquals(under.File, at.Node.File)))
                    {
                        if (walked.Add((under.Id, rest)))
                        {
                            pending.Push((under, rest));
                        }
                    }
                }
            }

            return false;
        }

        /// <summary>What the repository declares named <paramref name="name"/>: what any file can see, and what <paramref name="from"/>'s own file keeps to itself.</summary>
        private IReadOnlyList<Declaration> Repository(Declaration from, string name, Func<Declaration, bool> accepts)
            => Distinct(RepositoryNamed(from.File, name).Where(accepts));

        /// <summary>What the qualifier of a reference names <paramref name="name"/> in, and how sure where it was found makes it; what an import makes visible taken by <paramref name="imported"/>.</summary>
        private (IReadOnlyList<Declaration> Found, string Confidence) Qualified(Declaration from, ExtractQualifier qualifier, string name, Func<Declaration, bool> accepts, Func<Declaration, bool> imported, ReferenceAt place)
        {
            switch (qualifier.Kind)
            {
                case CodeQualifierKinds.This:
                    var selves = Selves(from);

                    // A value this holds, by the names the code writes after it - this.cart.lines: the name is looked for in the
                    // type the last of them is declared with, by its name alone where nothing says that type, as a value's is.
                    if (qualifier.Name is { } written)
                    {
                        return OfThis(from, selves, written.Whole()).Values is { } reached ? Several(selves, Through(from, reached, name, accepts)) : ByName(from, name, accepts);
                    }

                    return selves.Count > 0 ? Several(selves, Typed(from, selves, null, name, accepts, withSelf: true)) : ([], CodeConfidences.Exact);

                case CodeQualifierKinds.Base:
                    return EnclosingTypes(from) is { Count: > 0 } derived ? Several(derived, Typed(from, derived, null, name, accepts, withSelf: false)) : ([], CodeConfidences.Exact);

                case CodeQualifierKinds.Prefix:
                    return (Prefixed(from, qualifier.Name!.Whole(), name, imported), CodeConfidences.Exact);

                case CodeQualifierKinds.Own:
                    // The node of its own file its implementation names by id - what a YAML alias or merge repeats, read
                    // once where it is written: that one, exact. Its file changing finds the reference again, so nothing is read.
                    return (_owners[from.File].TryGetValue(qualifier.Name!, out var own) && accepts(own) ? [own] : [], CodeConfidences.Exact);

                case CodeQualifierKinds.Module:
                    // What a module of its own file exports by the name - a Vue component's $style.red - as an import of the
                    // module finds it: each node exported so, a suffix written onto others' by the names it writes, of the
                    // module whose own names are kept to the scope the qualifier names alone; exact.
                    return (Distinct(ExportedNamed(from.File.Path, name).Concat(Suffixed(from.File, name, exported: true))
                        .Where(target => target.Node.SeenIn == qualifier.Name && accepts(target))), CodeConfidences.Exact);

                case CodeQualifierKinds.Type:
                    return Typed(from, Containers(from, qualifier.Name!.Whole()), qualifier.Name!.Whole(), name, accepts, withSelf: true);

                case CodeQualifierKinds.Scope:
                    // The scope the code names, else what no scope holds: a PHP function's namespace, then the global one -
                    // its language's, as another language's top is no namespace of its own.
                    bool Own(Declaration target) => target.File.Language == from.File.Language && accepts(target);

                    return qualifier.Name is { } scope && InScope(OpenKey(scope), name, Own, withBases: false) is { Count: > 0 } held
                        ? (held, CodeConfidences.Exact)
                        : (Distinct(RepositoryNamed(from.File, name).Where(target => target.Node.Scope is null && Own(target))), CodeConfidences.Exact);

                case CodeQualifierKinds.Selector or CodeQualifierKinds.StyleVariable or CodeQualifierKinds.StyleMixin or CodeQualifierKinds.StyleFunction
                    or CodeQualifierKinds.Keyframes or CodeQualifierKinds.FontFace:
                    // A name a style sheet's syntax says the kind of - a selector, a variable, a mixin, a function, an
                    // animation's keyframes, a font's face - is found nearest first, of that kind alone, as Takes keeps it.
                    return Styled(from, qualifier.Kind, name, accepts);

                case CodeQualifierKinds.Key:
                    // A key qualifier names a configuration key wherever the repository declares one so - the property a
                    // project reads - of its own kind alone, as Takes keeps it; one with a name, of the language it names alone.
                    return (Distinct(RepositoryNamed(from.File, name).Where(target => accepts(target) && (qualifier.Name is null || target.File.Language is { } language && qualifier.Name.Is(language)))), CodeConfidences.Exact);

                case CodeQualifierKinds.Fixture:
                    // A fixture a test's parameter names: what the test's own scopes declare so - its class's, its module's -
                    // else what the scope imports made for the test's file that serve fixtures alone hold, the nearest
                    // directory's first, the first that holds one deciding - a conftest.py's, which pytest gives every test under
                    // it and no test imports; exact, and nowhere else: one no file of the tree provides is none of its
                    // declarations. Takes keeps it to fixtures (<see cref="CodeSubKinds.Fixture"/>).
                    if (Scoped(from, name, accepts) is { Count: > 0 } near)
                    {
                        return (near, CodeConfidences.Exact);
                    }

                    foreach (var serving in ServingImports(from.File, CodeQualifierKinds.Fixture))
                    {
                        if (InScope(OpenKey(serving.Target), name, accepts, withBases: false) is { Count: > 0 } provided)
                        {
                            return (provided, CodeConfidences.Exact);
                        }
                    }

                    return ([], CodeConfidences.Exact);

                default:
                    // A value the scopes hold, nearer than any type or scope of its name, is that value; a type or a scope is
                    // found as one is named - its names read whole, a chain written as links as the names its links hold,
                    // the chain before its last name carried as that one's and never written out again; a name that is
                    // neither is every type or scope it ends.
                    if (ValuesOf(from, qualifier, place) is { } values)
                    {
                        return Through(from, values, name, accepts);
                    }

                    if (qualifier.Link is { } link)
                    {
                        var chain = ChainNameOf(place.File, link);
                        return Typed(from, Containers(from, chain), chain.Name, name, accepts, withSelf: true);
                    }

                    return Typed(from, Containers(from, qualifier.Name!.Whole()), qualifier.Name!.Whole(), name, accepts, withSelf: true);
            }
        }

        /// <summary>
        /// <paramref name="found"/>, what a name was found to be on <paramref name="types"/>, the types a receiver may be: as found where
        /// they are one; where they are several - each a declaration a written name ends, none it names alone - one found is the one
        /// of its name among them, unique and never exact, and several are candidates, as a finding says of any.
        /// </summary>
        private static (IReadOnlyList<Declaration> Found, string Confidence) Several(IReadOnlyList<Declaration> types, (IReadOnlyList<Declaration> Found, string Confidence) found)
            => Distinct(types).Count > 1 && found.Confidence == CodeConfidences.Exact ? (found.Found, CodeConfidences.Unique) : found;

        /// <summary>
        /// What <paramref name="name"/> is through each import <paramref name="from"/>'s file makes by the prefix <paramref name="prefix"/>:
        /// what the import makes visible by it, its filter applied - or, where the prefix stands for the one name the import shows
        /// (<see cref="ExtractImport.Binds"/>), a member of each type or scope the import makes visible by that name.
        /// </summary>
        private IReadOnlyList<Declaration> Prefixed(Declaration from, string prefix, string name, Func<Declaration, bool> accepts)
            => Distinct(ImportsOf(from.File)
                .Where(made => string.Equals(made.Import.Prefix, prefix, StringComparison.Ordinal) && (made.Import.Binds || made.Import.Lets(name)))
                .SelectMany(made => made.Import.Binds
                    ? Inside(Stood(made.Import, made.File), name, accepts, withSelf: true)
                    : Imported(made.Import, made.File, name, accepts)));

        /// <summary>The types and scopes an import whose prefix stands for the one name it shows (<see cref="ExtractImport.Binds"/>) makes visible by that name.</summary>
        private IReadOnlyList<Declaration> Stood(ExtractImport import, CodeGraphFile file) => Imported(import, file, import.Show![0], IsContainer);

        /// <summary>
        /// What <paramref name="name"/> is in <paramref name="containers"/>, which <paramref name="written"/> names where a
        /// name does: a member of each type - itself where <paramref name="withSelf"/>, then its bases - or a declaration of
        /// each scope, exact; else an extension of one of those types that <paramref name="from"/> reaches, unique.
        /// </summary>
        private (IReadOnlyList<Declaration> Found, string Confidence) Typed(Declaration from, IReadOnlyList<Declaration> containers, string? written, string name, Func<Declaration, bool> accepts, bool withSelf)
        {
            var found = Inside(containers, name, accepts, withSelf);

            return found.Count > 0 ? (found, CodeConfidences.Exact) : (Extensions(from, containers, written, name, accepts, withSelf), CodeConfidences.Unique);
        }

        /// <summary>What <paramref name="containers"/> hold named <paramref name="name"/>: a member of each type - itself where <paramref name="withSelf"/>, then its bases - or a declaration of each scope.</summary>
        private IReadOnlyList<Declaration> Inside(IReadOnlyList<Declaration> containers, string name, Func<Declaration, bool> accepts, bool withSelf)
            => Distinct(containers.SelectMany(container => container.Node.Kind == CodeNodeKinds.Type
                ? InTypes([container], name, accepts, withSelf)
                : InScope(KeyOf(container.File, container.Node.Qualified), name, accepts, withBases: false)));

        /// <summary>
        /// The members named <paramref name="name"/> declared to extend a type, that <paramref name="from"/> reaches, which
        /// extend one of the types of <paramref name="containers"/> - itself where <paramref name="withSelf"/> - or one of
        /// their bases, as its own file names the type it extends (<see cref="Extended"/>), a constructor function among them;
        /// and, where none of <paramref name="containers"/> is, one extending a type the repository does not declare named as
        /// <paramref name="written"/> is.
        /// </summary>
        private IReadOnlyList<Declaration> Extensions(Declaration from, IReadOnlyList<Declaration> containers, string? written, string name, Func<Declaration, bool> accepts, bool withSelf)
        {
            if (ExtensionsNamed(name) is not { Count: > 0 } named)
            {
                return [];
            }

            var receivers = new HashSet<QualifiedName>();
            var pending = new Queue<QualifiedName>(containers.Where(container => container.Node.Kind == CodeNodeKinds.Type || IsFunction(container))
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
                    ? extended.Any(type => receivers.Contains(type.Id))
                    : containers.Count == 0 && written is not null && SimpleName(extension.Node.Extends!) == SimpleName(written))
                && Reaches(from, extension)));
        }

        /// <summary>
        /// What <paramref name="extension"/>, a member declared to extend a type (<see cref="ExtractNode.Extends"/>), extends, as
        /// its own file names it: each type of that name; else, where none is, each function its name finds from the extension - its
        /// scopes, its file's imports, the repository's - a constructor function another file declares, whose prototype the
        /// extension's file writes. Found once.
        /// </summary>
        private IReadOnlyList<Declaration> Extended(Declaration extension)
            => Cached(_extended, extension, () =>
            {
                var extends = extension.Node.Extends!;

                if (Distinct(Containers(extension, extends).Where(container => container.Node.Kind == CodeNodeKinds.Type)) is { Count: > 0 } types)
                {
                    return types;
                }

                bool Constructor(Declaration function) => IsFunction(function) && Sees(extension, function);

                return Distinct(Unqualified(extension, extends, Constructor, Constructor).Found);
            });

        /// <summary>Whether <paramref name="declaration"/> is a function: a member a call is made of by its arguments, which a constructor function is.</summary>
        private static bool IsFunction(Declaration declaration) => declaration.Node is { Kind: CodeNodeKinds.Member, MinArity: not null };

        /// <summary>
        /// Whether <paramref name="function"/> is a constructor function another file writes the prototype of: a function some member
        /// declared to extend a type extends (<see cref="Extended"/>) - the type it is, though its own file declares no type, so its
        /// body's <c>this</c> is read on it (<see cref="EnclosingTypes"/>) and a <c>new</c> of it makes one. Found once.
        /// </summary>
        private bool Constructs(Declaration function)
            => IsFunction(function) && Cached(_constructs, function, () =>
            {
                Touch(Read.Extending, function.Node.Name);
                return _extending.TryGetValue(function.Node.Name, out var extending) && extending.Any(extension => Extended(extension).Any(extended => extended.Id == function.Id));
            });

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

            var holders = new HashSet<ScopeKey> { key };

            if (ParentOf(key, out var parent))
            {
                if (parent is null)
                {
                    return true;
                }

                holders.Add(parent.Value);
            }

            if (Chain(from).Any(holders.Contains))
            {
                return true;
            }

            // The name an import's filter lets it by: an extension's, whose member this is - itself where nothing holds it.
            var shown = declaration.Node.Parent is { } holderId && PartsOf(holderId) is [var holder, ..] ? holder.Node.Name : declaration.Node.Name;

            return ImportsOf(from.File).Any(made => made.Import.Lets(shown) && (made.Import.Form == CodeImportForms.Scope
                ? made.Import.Prefix is null && holders.Contains(OpenKey(made.Import.Target))
                : FilesOf(made.Import, made.File).Any(target => Exposes(target, shown, holders, declaration.File.Path))));
        }

        /// <summary>
        /// Whether the file at <paramref name="start"/>, imported, makes visible what one of <paramref name="holders"/> holds
        /// or the file <paramref name="declared"/> declares: it is that file, it declares one of those scopes open, or a file
        /// it exports does - the export's filter letting <paramref name="shown"/> - as what the file exposes of that name says
        /// (<see cref="Exposed"/>), found once.
        /// </summary>
        private bool Exposes(string start, string shown, HashSet<ScopeKey> holders, string declared)
        {
            var exposed = Exposed(start, shown);
            return exposed.Files.Contains(declared) || holders.Any(exposed.Opens.Contains);
        }

        /// <summary>
        /// What the file at <paramref name="start"/>, imported, makes visible by <paramref name="shown"/>: itself and each file
        /// it exports - the export's filter letting the name - in turn, and the open scopes each declares. Each file visited
        /// once, however its exports come round, followed on a stack of its own rather than the call's; found once for a file
        /// and a name, every later check reading the first one's answer and what it read.
        /// </summary>
        private Exposure Exposed(string start, string shown)
            => Cached(_exposed, (start, shown), () =>
            {
                var exposure = new Exposure(new HashSet<string>(StringComparer.Ordinal), []);
                var pending = new Stack<string>([start]);

                while (pending.TryPop(out var path))
                {
                    if (!exposure.Files.Add(path))
                    {
                        continue;
                    }

                    _exposedFiles++;
                    exposure.Opens.UnionWith(OpensOf(path).Select(OpenKey));
                    Touch(Read.Imports, path);

                    if (!_byPath.TryGetValue(path, out var file) || file.Extract is null)
                    {
                        continue;
                    }

                    // The files it exports, in the order it exports them: the first pushed last.
                    foreach (var target in Taken(file).Where(export => export.Export && export.Lets(shown)).SelectMany(export => FilesOf(export, file)).Reverse())
                    {
                        pending.Push(target);
                    }
                }

                return exposure;
            });

        /// <summary>What a file imported makes visible by a name (<see cref="Exposed"/>): the files it reaches, and the open scopes they declare.</summary>
        private sealed record Exposure(HashSet<string> Files, HashSet<ScopeKey> Opens);

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
        private IReadOnlyList<Declaration>? Values(Declaration from, string written) => Fold(from, written).Values;

        /// <summary>
        /// The values <paramref name="qualifier"/>, the qualifier of the reference at <paramref name="place"/>, comes to from
        /// where <paramref name="from"/> stands, as <see cref="Values"/> says of its names: a chain written as links read
        /// link by link, each link's chain read once whatever reads it - so a chain of n names costs n steps, the use of each
        /// of its names and the call on it reading the links before them once - and one that writes its names, by them.
        /// </summary>
        private IReadOnlyList<Declaration>? ValuesOf(Declaration from, ExtractQualifier qualifier, ReferenceAt place)
            => qualifier.Link is { } link ? ChainTo(from, place with { At = link }).Values : qualifier.Name is { } written ? Values(from, written.Whole()) : null;

        /// <summary>
        /// What the chain the reference at <paramref name="place"/> ends - its qualifier's names, then its own - comes to from
        /// where <paramref name="from"/> stands: answered once, link by link on the memo's own stack, never a call into
        /// itself however long the chain; the finding under way reads what answering it read, through the link's reads whole.
        /// </summary>
        private Chained ChainTo(Declaration from, ReferenceAt place)
        {
            var answer = _chains.Of(new ChainKey(from, place));
            _reads?.Through.Add(answer.Reads);
            return answer.State;
        }

        /// <summary>
        /// A memo of the chains links end, emptied whenever the bases they were read through are not the ones known now. A
        /// link is stepped once for each declaration, chain before it and name: two chains a declaration writes alike - the
        /// same names on the same first name - are one chain, however many statements write it, each link read once.
        /// </summary>
        private Memo<ChainKey, ChainAnswer> NewChains() => new(
            key => key.Place.File.Extract!.References[key.Place.At].Qualifier is { Link: { } link } ? [key with { Place = key.Place with { At = link } }] : [],
            (key, before) =>
            {
                var reference = key.Place.File.Extract!.References[key.Place.At];
                var prefix = before.Count > 0 ? before[0] : null;
                var link = new ChainLink(key.From, prefix, prefix is null ? reference.Qualifier?.Name?.Whole() : null, prefix is null && reference.Qualifier?.Kind is CodeQualifierKinds.Type or CodeQualifierKinds.This ? reference.Qualifier.Kind : null, reference.Name);

                if (_chainSteps.TryGetValue(link, out var stepped))
                {
                    return stepped;
                }

                var outer = _reads;
                var reads = _reads = new Reading();

                try
                {
                    // A first link's qualifier of a type - a variable's declared type, which no lookup of the variable finds -
                    // begins the chain at that type's value; one of this at what this is, its names read on it; any other at
                    // what its names come to.
                    var head = link.HeadKind == CodeQualifierKinds.This ? OfThis(key.From, Selves(key.From), link.Head)
                        : link.Head is not { } written ? Chained.None
                        : link.HeadKind == CodeQualifierKinds.Type ? new Chained(ChainedKind.Types, Distinct(Containers(key.From, written).Where(container => container.Node.Kind == CodeNodeKinds.Type)), null)
                        : Fold(key.From, written);
                    var state = Step(key.From, prefix?.State ?? head, reference.Name);
                    _chainLinks++;

                    if (prefix is not null)
                    {
                        reads.Through.Add(prefix.Reads);
                    }

                    return _chainSteps[link] = new ChainAnswer(state, reads.Kept());
                }
                finally
                {
                    _reads = outer;
                }
            },
            _ => new ChainAnswer(Chained.None, KeptReads.None),
            ChainKey.Comparer);

        /// <summary>What the chain of names <paramref name="written"/> comes to from where <paramref name="from"/> stands, a name at a time.</summary>
        private Chained Fold(Declaration from, string written)
        {
            var parts = written.Split('.');
            var state = Head(from, parts[0]);

            for (var at = 1; at < parts.Length; at++)
            {
                state = Step(from, state, parts[at]);
            }

            return state;
        }

        /// <summary>A chain's first name, <paramref name="name"/>: a value the scopes hold - nearest first, with each type's bases, none past one that declares a type or a scope of that name - else a name a type may be.</summary>
        private Chained Head(Declaration from, string name)
        {
            IReadOnlyList<Declaration> values = [.. Scoped(from, name, declaration => (IsValue(declaration) || IsContainer(declaration)) && Sees(from, declaration)).Where(IsValue)];

            return values.Count > 0 ? new Chained(ChainedKind.Values, values, null) : new Chained(ChainedKind.Head, [], name);
        }

        /// <summary>
        /// The chain <paramref name="before"/> stands for, one name longer: after a first name no value is, a value of the
        /// type it names - <c>Cache.Instance</c> - else no value at all; after values, the value of the next name in the type
        /// each is declared with, none where those types hold none - the rest of the chain then holding none - and the
        /// values themselves where one does not say its type, which a call is looked for through by its name alone.
        /// </summary>
        private Chained Step(Declaration from, Chained before, string name)
        {
            switch (before.Kind)
            {
                case ChainedKind.Head:
                    var types = Distinct(NamedContainers(from, before.Head!).Where(container => container.Node.Kind == CodeNodeKinds.Type));
                    var held = types.Count > 0 ? InTypes(types, name, IsValue, withSelf: true) : [];

                    return held.Count > 0 ? new Chained(ChainedKind.Values, held, null) : Chained.None;

                case ChainedKind.Types:
                    var typed = before.Found.Count > 0 ? InTypes(before.Found, name, IsValue, withSelf: true) : [];

                    return typed.Count > 0 ? new Chained(ChainedKind.Values, typed, null) : new Chained(ChainedKind.Empty, [], null);

                case ChainedKind.Values when before.Found.Any(value => value.Node.ValueType is null):
                    return before with { Kind = ChainedKind.Untyped };

                case ChainedKind.Values:
                    var next = Of(from, before.Found, name, IsValue).Found;

                    return next.Count > 0 ? new Chained(ChainedKind.Values, next, null) : new Chained(ChainedKind.Empty, [], null);

                default:
                    return before;
            }
        }

        /// <summary>
        /// The types and scopes <paramref name="written"/> names from where <paramref name="from"/> stands: those
        /// <see cref="NamedContainers(Declaration, string)"/> finds, else every one whose qualified name ends with it - but where
        /// <paramref name="from"/>'s file binds its names within it (<see cref="BoundWithin"/>), where a name is no ending's.
        /// </summary>
        private IReadOnlyList<Declaration> Containers(Declaration from, string written)
            => NamedContainers(from, written) is { Count: > 0 } named ? named : BoundWithin(from) ? [] : Ending(from, written);

        /// <summary>
        /// The types and scopes <paramref name="written"/> names from where <paramref name="from"/> stands, nearest first:
        /// one the scopes it is written in declare; else the one whose qualified name it is, where <paramref name="from"/>'s file
        /// does not bind its names within it (<see cref="BoundWithin"/>); else what it is past those scopes - an alias it is or
        /// begins with, an import's, the repository's.
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

                return !BoundWithin(from) && Distinct(ContainersOf(written).Where(Seen)) is { Count: > 0 } named
                    ? named
                    : Beyond(from, written, Seen, Seen).Found;
            });
        }

        /// <summary>
        /// Whether <paramref name="from"/>'s file binds its names within it (<see cref="FileExtract.BoundWithin"/>): a name neither its
        /// scopes nor its imports bind is bound by nothing - neither a declaration of the repository's by its name, nor the type or
        /// scope whose whole qualified name a qualifier writes or whose name ends with it. The one place that rule is read.
        /// </summary>
        private static bool BoundWithin(Declaration from) => from.File.Extract is { BoundWithin: true };

        /// <summary>Every type and scope <paramref name="from"/>'s file sees whose qualified name ends with <paramref name="written"/> at a name's boundary: found once for each.</summary>
        private IReadOnlyList<Declaration> Ending(Declaration from, string written)
        {
            var ending = Cached(_endings, written, () => Distinct(QualifiedEnding(Trailing(written))
                .Where(qualified => EndsWithName(qualified.Whole(), written))
                .SelectMany(qualified => ContainersOf(qualified))));

            return ending.Any(container => container.FileOnly || container.SeenKey is not null) ? [.. ending.Where(container => Sees(from, container))] : ending;
        }

        /// <summary>
        /// The types and scopes the chain <paramref name="written"/> names from where <paramref name="from"/> stands, as
        /// <see cref="Containers(Declaration, string)"/> finds those its names written out name: those
        /// <see cref="NamedContainers(Declaration, ChainName)"/> finds, else every one whose qualified name ends with it.
        /// </summary>
        private IReadOnlyList<Declaration> Containers(Declaration from, ChainName written)
            => NamedContainers(from, written) is { Count: > 0 } named ? named : BoundWithin(from) ? [] : Ending(from, written);

        /// <summary>
        /// What <see cref="NamedContainers(Declaration, string)"/> finds of the names the chain <paramref name="written"/>
        /// writes, each step from what the chain before its last name found, never from its names written out: where some
        /// declaration is named as the chain writes - a name of dots, which only such a name reaches in the scopes, the
        /// imports and the repository - by its names written out, as that one finds them; else the one whose qualified name
        /// it is, else what an alias it begins with stands for, its names past the alias in that scope. Found once from one
        /// file and scope.
        /// </summary>
        private IReadOnlyList<Declaration> NamedContainers(Declaration from, ChainName written)
        {
            bool Seen(Declaration container) => IsContainer(container) && Sees(from, container);

            return Cached(_chainContainersFound, (from.File.Path, from.ScopeKey, written), () =>
            {
                _dottedTrie ??= DottedTrie();

                if (At(_dottedTrie, written, pastFirst: false) is { Named: true })
                {
                    return NamedContainers(from, written.Text());
                }

                // No declaration is named so, whatever scope it is looked for in: a name of its end come or gone finds it again.
                Touch(Read.Name, DottedEnd(written.Name));

                _qualifiedTrie ??= QualifiedTrie();

                if (!BoundWithin(from) && Distinct(Held(At(_qualifiedTrie, written, pastFirst: false), written).Where(Seen)) is { Count: > 0 } named)
                {
                    return named;
                }

                var first = written.First;
                var last = written.Count == 2 ? written.Name : null;

                return Distinct(ImportsOf(from.File)
                    .Where(made => string.Equals(made.Import.Prefix, first.Name, StringComparison.Ordinal))
                    .SelectMany(made => made.Import.Form == CodeImportForms.Scope ? Held(Aliased(made.Import.Target, written), written)
                        : last is not null && made.Import.Lets(last) ? Imported(made.Import, made.File, last, IsContainer)
                        : [])
                    .Where(Seen));
            });
        }

        /// <summary>
        /// The containers whose qualified name is where <paramref name="at"/> stands, the names of <paramref name="written"/>
        /// walked to it: read as the very name a container holds where one does, and else as the name every qualified name the
        /// chain's names end ends with, which one come under that name alters.
        /// </summary>
        private IReadOnlyList<Declaration> Held(NameTrie? at, ChainName written)
        {
            if (at?.Here is { Count: > 0 } here)
            {
                Touch(Read.Qualified, here[0].Node.Qualified);
                return here;
            }

            Touch(Read.Trailing, Trailing(written.Name));
            return [];
        }

        /// <summary>Where the names of <paramref name="written"/> past its first stand from the scope <paramref name="target"/>, an alias's: none where no container's qualified name begins so.</summary>
        private NameTrie? Aliased(string target, ChainName written)
        {
            _qualifiedTrie ??= QualifiedTrie();

            var at = _qualifiedTrie;

            foreach (var part in target.Split('.'))
            {
                if (!at.Next.TryGetValue(part, out var next))
                {
                    return null;
                }

                at = next;
            }

            return At(at, written, pastFirst: true);
        }

        /// <summary>
        /// Where <paramref name="written"/>'s names - those past its first where <paramref name="pastFirst"/> - stand from
        /// <paramref name="from"/>: from where the chain before its last name stands, each chain found once, walked down from
        /// the first not found yet on a stack of its own; none where no name of the trie runs so.
        /// </summary>
        private NameTrie? At(NameTrie from, ChainName written, bool pastFirst)
        {
            var pending = new Stack<ChainName>();

            for (var at = written; at is not null && !_chainsAt.ContainsKey((from, at, pastFirst)); at = at.Before)
            {
                pending.Push(at);
            }

            while (pending.TryPop(out var chain))
            {
                var before = chain.Before is { } prefix ? _chainsAt[(from, prefix, pastFirst)] : null;

                _chainsAt[(from, chain, pastFirst)] = chain.Before is null
                    ? pastFirst ? from : from.Next.GetValueOrDefault(chain.Name)
                    : before?.Next.GetValueOrDefault(chain.Name);
            }

            return _chainsAt[(from, written, pastFirst)];
        }

        /// <summary>The containers each chain of names ends, as <see cref="Ending(Declaration, string)"/> finds those its names written out end, each compared from its last name back: found once.</summary>
        private IReadOnlyList<Declaration> Ending(Declaration from, ChainName written)
        {
            var ending = Cached(_chainEndings, written, () => Distinct(QualifiedEnding(Trailing(written.Name))
                .Where(qualified => written.EndsName(qualified.Whole()))
                .SelectMany(qualified => ContainersOf(qualified))));

            return ending.Any(container => container.FileOnly || container.SeenKey is not null) ? [.. ending.Where(container => Sees(from, container))] : ending;
        }

        /// <summary>
        /// The names the chain the reference at <paramref name="at"/> of <paramref name="file"/> ends writes - its qualifier's,
        /// by a name or a link in turn, then its own - each link's made once from the one it links to, the links not made yet
        /// followed on a stack of their own.
        /// </summary>
        private ChainName ChainNameOf(CodeGraphFile file, int at)
        {
            var references = file.Extract!.References;
            var pending = new Stack<int>();

            // Every link names a reference made before its own, so the walk ends; one that does not - which Problem refuses -
            // ends the chain there, as FileExtract.Written reads it.
            static int? Linked(ExtractReference reference, int place) => reference.Qualifier is { Link: { } link } && link >= 0 && link < place ? link : null;

            for (var place = at; !_chainOf.ContainsKey((file, place));)
            {
                pending.Push(place);

                if (Linked(references[place], place) is not { } link)
                {
                    break;
                }

                place = link;
            }

            while (pending.TryPop(out var place))
            {
                var reference = references[place];
                var before = Linked(reference, place) is { } link ? _chainOf[(file, link)]
                    : reference.Qualifier is { Link: null, Name: { } head } ? Names(null, head.Whole())
                    : null;

                _chainOf[(file, place)] = Names(before, reference.Name);
            }

            return _chainOf[(file, at)];
        }

        /// <summary><paramref name="before"/> with each name <paramref name="written"/> parts into at its dots after it, each chain held once.</summary>
        private ChainName Names(ChainName? before, string written)
        {
            var chain = before;

            foreach (var name in written.Split('.'))
            {
                var key = (chain, name);

                if (!_chainNames.TryGetValue(key, out var next))
                {
                    _chainNames[key] = next = new ChainName(chain, name);
                }

                chain = next;
            }

            return chain!;
        }

        /// <summary>Every type's and scope's qualified name, parted at its dots, each with the containers of that name.</summary>
        private NameTrie QualifiedTrie()
        {
            var root = new NameTrie();

            foreach (var (qualified, containers) in _containers)
            {
                root.Under(qualified.Whole()).Here = containers;
            }

            return root;
        }

        /// <summary>Every declaration's name that holds a dot, its own and each it is exported by, parted at its dots.</summary>
        private NameTrie DottedTrie()
        {
            var root = new NameTrie();

            foreach (var declaration in _declarations)
            {
                foreach (var name in ((string[])[declaration.Node.Name, .. declaration.Node.Exports ?? []]).Where(name => name.Contains('.', StringComparison.Ordinal)))
                {
                    root.Under(name).Named = true;
                }
            }

            return root;
        }

        /// <summary>
        /// The names a chain written as links writes (<see cref="ExtractQualifier.Link"/>), held as the chain before its last
        /// name and that name: so a chain of n names is n of these, each one more than the one before, and none of them writes
        /// the names before it out - what the linker looks a chain up by, each step from the one before.
        /// </summary>
        private sealed class ChainName
        {
            public ChainName(ChainName? before, string name)
            {
                Before = before;
                Name = name;
                First = before?.First ?? this;
                Count = (before?.Count ?? 0) + 1;
                Length = before is null ? name.Length : before.Length + 1 + name.Length;
            }

            /// <summary>The chain before its last name; none where it is one name.</summary>
            public ChainName? Before { get; }

            /// <summary>Its last name, which holds no dot.</summary>
            public string Name { get; }

            /// <summary>The chain of its first name alone.</summary>
            public ChainName First { get; }

            /// <summary>How many names it writes.</summary>
            public int Count { get; }

            /// <summary>How long its names are written out, joined by dots.</summary>
            public int Length { get; }

            /// <summary>Its names written out, joined by dots: made where a declaration is named so, and never kept.</summary>
            public string Text() => string.Create(Length, this, static (span, chain) =>
            {
                var end = span.Length;

                for (var at = chain; at is not null; at = at.Before)
                {
                    end -= at.Name.Length;
                    at.Name.AsSpan().CopyTo(span[end..]);

                    if (at.Before is not null)
                    {
                        span[--end] = '.';
                    }
                }
            });

            /// <summary>Whether <paramref name="qualified"/> ends with its names at a name's boundary, as a name written out is matched (<see cref="EndsWithName"/>), compared from its last name back.</summary>
            public bool EndsName(string qualified)
            {
                if (qualified.Length <= Length)
                {
                    return false;
                }

                var end = qualified.Length;

                for (var at = this; at is not null; at = at.Before)
                {
                    end -= at.Name.Length;

                    if (!qualified.AsSpan(end, at.Name.Length).SequenceEqual(at.Name))
                    {
                        return false;
                    }

                    if (at.Before is not null && qualified[--end] != '.')
                    {
                        return false;
                    }
                }

                return !IsNamePart(qualified[end - 1]);
            }
        }

        /// <summary>Names parted at their dots, each a place of the trie: what is held where a whole name ends.</summary>
        private sealed class NameTrie
        {
            public Dictionary<string, NameTrie> Next { get; } = new(StringComparer.Ordinal);

            /// <summary>The containers whose qualified name ends here, where one does.</summary>
            public List<Declaration>? Here { get; set; }

            /// <summary>Whether some declaration is named as the names walked here write.</summary>
            public bool Named { get; set; }

            /// <summary>The place <paramref name="name"/>'s parts lead to from here, made where it is not yet.</summary>
            public NameTrie Under(string name)
            {
                var at = this;

                foreach (var part in name.Split('.'))
                {
                    if (!at.Next.TryGetValue(part, out var next))
                    {
                        at.Next[part] = next = new NameTrie();
                    }

                    at = next;
                }

                return at;
            }
        }

        /// <summary>
        /// The types and scopes <paramref name="written"/> stands for in <paramref name="file"/> through an alias: the
        /// target of each scope import it sees - its own, and what every file of its language imports - whose prefix is
        /// that name, or begins it at a dot and the rest of it names what is inside the target. The prefix names the scope
        /// itself, so it is found wherever a type's or a scope's name is written. A file imported by a prefix names no
        /// scope, written alone: its prefix and a name after it are what that import makes visible of that name, its filter
        /// applied - or, where the prefix stands for the one name the import shows (<see cref="ExtractImport.Binds"/>), that name
        /// inside what it makes visible by that one - and each name past that one a type or scope inside the one before. A
        /// prefix alone is the scope a scope import is, and nothing an import of a file is: what a name it shows is found as
        /// every name an import shows is (<see cref="Beyond"/>).
        /// </summary>
        private IReadOnlyList<Declaration> Aliased(CodeGraphFile file, string written)
            => Distinct(ImportsOf(file)
                .Where(made => made.Import.Prefix is { } prefix && written.StartsWith(prefix, StringComparison.Ordinal) && (written.Length == prefix.Length || written[prefix.Length] == '.'))
                .SelectMany(made => AliasedThrough(made.Import, made.File, written[made.Import.Prefix!.Length..])));

        /// <summary>
        /// The types and scopes the names <paramref name="rest"/> - nothing, or a dot and names joined by dots - stand for past the
        /// prefix of <paramref name="import"/>, made by <paramref name="file"/>, as <see cref="Aliased(CodeGraphFile, string)"/> reads them: each name stepped
        /// into what the one before came to, a name at a time.
        /// </summary>
        private IReadOnlyList<Declaration> AliasedThrough(ExtractImport import, CodeGraphFile file, string rest)
        {
            if (import.Form == CodeImportForms.Scope)
            {
                return ContainersOf(import.Target + rest);
            }

            var names = rest.Length == 0 ? [] : rest[1..].Split('.');

            if (names.Length == 0 || (!import.Binds && !import.Lets(names[0])))
            {
                return [];
            }

            var at = import.Binds ? Stood(import, file) : Imported(import, file, names[0], IsContainer);

            for (var next = import.Binds ? 0 : 1; next < names.Length && at.Count > 0; next++)
            {
                at = Inside(at, names[next], IsContainer, withSelf: true);
            }

            return at;
        }

        /// <summary>The scopes a reference made in <paramref name="from"/> is looked for in, from the inside out.</summary>
        private IEnumerable<ScopeKey> Chain(Declaration from)
        {
            var seen = new HashSet<ScopeKey>();
            var key = from.ScopeKey;

            while (key is { } scope && seen.Add(scope))
            {
                yield return scope;

                // Read only where the scope before decided nothing: its parent is what is looked in next.
                key = ParentOf(scope, out var parent) ? parent : null;
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
        private IReadOnlyList<ScopeKey> TopsOf(CodeGraphFile file)
        {
            if (_tops.TryGetValue(file, out var tops))
            {
                return tops;
            }

            var extract = file.Extract;
            var own = extract?.Nodes.Where(IsContainer).Select(node => node.Qualified).ToHashSet() ?? [];

            return _tops[file] = extract is null
                ? []
                : [.. extract.Scopes.Where(scope => !scope.Open && scope.Parent is null && !own.Contains(scope.Name)).Select(scope => ClosedKey(file.Path, scope.Name))];
        }

        /// <summary>What the scope <paramref name="key"/> declares named <paramref name="name"/>, and - where it is a type's, and <paramref name="withBases"/> - what its bases do.</summary>
        private IReadOnlyList<Declaration> InScope(ScopeKey key, string name, Func<Declaration, bool> accepts, bool withBases)
        {
            var found = Distinct(ScopeNamed(key, name).Where(accepts));

            if (found.Count > 0 || !withBases)
            {
                return found;
            }

            var types = ContainersOf(key.Name).Where(container => container.Node.Kind == CodeNodeKinds.Type && KeyOf(container.File, container.Node.Qualified) == key).ToList();

            if (types.Count == 0)
            {
                return [];
            }

            // A part its file joins to it - a component's code-behind, or the component of one - is the same type, in a
            // scope of its own: what it declares is the type's, before any base's.
            var joined = Distinct(types.SelectMany(type => SiblingsOf(type.Id)).SelectMany(PartsOf)
                .SelectMany(part => ScopeNamed(KeyOf(part.File, part.Node.Qualified), name))
                .Where(accepts));

            return joined.Count > 0 ? joined : InTypes(types, name, accepts, withSelf: false);
        }

        /// <summary>
        /// What <paramref name="types"/> declare named <paramref name="name"/>: what each of them does, as
        /// <see cref="InType"/> finds it.
        /// </summary>
        private IReadOnlyList<Declaration> InTypes(IReadOnlyList<Declaration> types, string name, Func<Declaration, bool> accepts, bool withSelf)
            => Distinct(types.DistinctBy(type => type.Id).SelectMany(type => InType(type.Id, name, accepts, withSelf)));

        /// <summary>
        /// What the type <paramref name="id"/> declares named <paramref name="name"/> - itself where <paramref name="withSelf"/>,
        /// then its bases a level at a time, each level a rank at a time as <see cref="LevelUp"/> ranks it: the first rank
        /// that holds one decides, all it holds answering.
        /// </summary>
        private IReadOnlyList<Declaration> InType(QualifiedName id, string name, Func<Declaration, bool> accepts, bool withSelf)
        {
            var seen = new HashSet<QualifiedName>();

            // The type is itself and each part its file joins to it (SiblingsOf), whose bases are its own too.
            List<BaseRank> level = [new BaseRank([id, .. SiblingsOf(id)], Mixin: false)];
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
        private readonly record struct BaseRank(QualifiedName[] Ids, bool Mixin);

        /// <summary>One reference of a file, by its place among the file's references.</summary>
        private readonly record struct ReferenceAt(CodeGraphFile File, int At);

        /// <summary>
        /// A chain a link ends, read from where one declaration stands: the declaration and the reference whose name ends
        /// the chain. Two keys are one where they are of the very declaration, file and place.
        /// </summary>
        private readonly record struct ChainKey(Declaration From, ReferenceAt Place)
        {
            public static IEqualityComparer<ChainKey> Comparer { get; } = new ByIdentity();

            private sealed class ByIdentity : IEqualityComparer<ChainKey>
            {
                public bool Equals(ChainKey x, ChainKey y)
                    => ReferenceEquals(x.From, y.From) && ReferenceEquals(x.Place.File, y.Place.File) && x.Place.At == y.Place.At;

                public int GetHashCode(ChainKey key)
                    => HashCode.Combine(RuntimeHelpers.GetHashCode(key.From), RuntimeHelpers.GetHashCode(key.Place.File), key.Place.At);
            }
        }

        /// <summary>What a chain a link ends comes to, with everything reading it read.</summary>
        private sealed record ChainAnswer(Chained State, KeptReads Reads);

        /// <summary>
        /// One link of a chain as a declaration reads it: the declaration, what the chain before the link came to - none for
        /// the first link - the names the first link's qualifier writes and its kind, which says whether they name a type or
        /// are read on this, and the link's own name. Two are one where they are of the very declaration and the very answer
        /// before, and of the same names and kind.
        /// </summary>
        private readonly record struct ChainLink(Declaration From, ChainAnswer? Prefix, string? Head, string? HeadKind, string Name)
        {
            public static IEqualityComparer<ChainLink> Comparer { get; } = new ByIdentity();

            private sealed class ByIdentity : IEqualityComparer<ChainLink>
            {
                public bool Equals(ChainLink x, ChainLink y)
                    => ReferenceEquals(x.From, y.From) && ReferenceEquals(x.Prefix, y.Prefix) && string.Equals(x.HeadKind, y.HeadKind, StringComparison.Ordinal)
                    && string.Equals(x.Head, y.Head, StringComparison.Ordinal) && string.Equals(x.Name, y.Name, StringComparison.Ordinal);

                public int GetHashCode(ChainLink link)
                    => HashCode.Combine(
                        RuntimeHelpers.GetHashCode(link.From),
                        link.Prefix is null ? 0 : RuntimeHelpers.GetHashCode(link.Prefix),
                        link.Head is null ? 0 : StringComparer.Ordinal.GetHashCode(link.Head),
                        link.HeadKind is null ? 0 : StringComparer.Ordinal.GetHashCode(link.HeadKind),
                        StringComparer.Ordinal.GetHashCode(link.Name));
            }
        }

        /// <summary>What the finding under way has read so far: keys directly, and reads it read through whole.</summary>
        private sealed class Reading
        {
            /// <summary>The keys read directly.</summary>
            public HashSet<int> Keys { get; } = [];

            /// <summary>The reads read through whole, each once.</summary>
            public HashSet<KeptReads> Through { get; } = new(ReferenceEqualityComparer.Instance);

            /// <summary>What was read, kept.</summary>
            public KeptReads Kept() => new([.. Keys], [.. Through]);
        }

        /// <summary>What a chain of names has come to so far (<see cref="Values"/>).</summary>
        private enum ChainedKind
        {
            /// <summary>No value: its first name is none, nor a type's value.</summary>
            None,

            /// <summary>Its one name is no value the scopes hold; a type of that name may hold the next.</summary>
            Head,

            /// <summary>
            /// Its first link is qualified by a type - a variable's declared type - whose value the next name is a member of: the types
            /// of that name; or by this, whose value the next name is: what this is (<see cref="Linking.Selves"/>).
            /// </summary>
            Types,

            /// <summary>The values its last name is, each of a type it says.</summary>
            Values,

            /// <summary>Values one of which says no type: what the rest of the chain is, nothing says.</summary>
            Untyped,

            /// <summary>A link's type holds no value of the next name, so the chain holds nothing the repository declares.</summary>
            Empty,
        }

        /// <summary>What a chain of names has come to so far: of which kind, the values found, and the first name where no value is it.</summary>
        private sealed record Chained(ChainedKind Kind, IReadOnlyList<Declaration> Found, string? Head)
        {
            public static Chained None { get; } = new(ChainedKind.None, [], null);

            /// <summary>The values the chain comes to, as <see cref="Linking.Values"/> answers them.</summary>
            public IReadOnlyList<Declaration>? Values => Kind switch
            {
                ChainedKind.Values or ChainedKind.Untyped => Found,
                ChainedKind.Empty => [],
                _ => null,
            };
        }

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
            var inherited = new List<QualifiedName>();

            foreach (var rank in level)
            {
                foreach (var id in rank.Ids)
                {
                    if (BaseRanksOf(id) is not { Count: > 0 } bases)
                    {
                        continue;
                    }

                    ranks.AddRange(bases.Take(bases.Count - 2).Select(mixin => new BaseRank([.. mixin.Select(QualifiedName.Of)], Mixin: true)));
                    inherited.AddRange(bases[^2].Select(QualifiedName.Of));

                    if (!rank.Mixin)
                    {
                        inherited.AddRange(bases[^1].Select(QualifiedName.Of));
                    }
                }
            }

            if (inherited.Count > 0)
            {
                ranks.Add(new BaseRank([.. inherited.Distinct()], Mixin: false));
            }

            return ranks;
        }

        /// <summary>The bases of the type <paramref name="id"/>, by id, nearest first (<see cref="BaseRanksOf"/>).</summary>
        private IEnumerable<QualifiedName> BasesOf(QualifiedName id) => BaseRanksOf(id).SelectMany(rank => rank.Select(QualifiedName.Of));

        /// <summary>
        /// The bases of the type <paramref name="id"/>, by id, in ranks nearest first: each mixin it names a rank of its own,
        /// the last of them first - each applied over what comes before it, as Dart's are - with the candidates it was found
        /// among; then what it implements, then what it inherits, in the order it names them - two ranks one to a lookup
        /// (<see cref="LevelUp"/>), as a member two of those declare is either's, and apart so that a mixin's own inherited
        /// bases are told from what it implements. Each of the two is empty where the type names none; a type that names no
        /// base has no ranks.
        /// </summary>
        private IReadOnlyList<string[]> BaseRanksOf(QualifiedName id)
        {
            Touch(Read.Bases, id);
            return _bases.TryGetValue(id, out var bases) ? bases : [];
        }

        /// <summary>
        /// What <paramref name="import"/>, made by <paramref name="file"/>, makes visible named <paramref name="name"/> - with
        /// the nodes standing for a suffix written onto others' that stand for it where <paramref name="suffixed"/> - the files
        /// <paramref name="walked"/> holds, which a walk before reached, not walked again. An import that takes what the file it
        /// names publishes (<see cref="ExtractImport.Published"/>) makes visible what the file makes visible by a name it publishes
        /// (<see cref="Publishes"/>) alone - the name asked, which is the one the file binds and publishes, whatever the declaration
        /// it reaches is named.
        /// </summary>
        private IReadOnlyList<Declaration> Imported(ExtractImport import, CodeGraphFile file, string name, Func<Declaration, bool> accepts, HashSet<string>? walked = null, bool suffixed = false)
        {
            if (import.Form == CodeImportForms.Scope)
            {
                return InScope(OpenKey(import.Target), name, accepts, withBases: true);
            }

            var found = new List<Declaration>();

            foreach (var path in FilesOf(import, file))
            {
                if (!import.Published || Publishes(path, name))
                {
                    found.AddRange(Visible(path, name, walked, suffixed));
                }
            }

            return Distinct(found.Where(accepts));
        }

        /// <summary>
        /// Whether the file at <paramref name="path"/> publishes <paramref name="name"/> to an import that takes its published names
        /// (<see cref="ExtractImport.Published"/>): its own list holds it (<see cref="FileExtract.Published"/>), or - where what it
        /// publishes holds what its own such imports take (<see cref="FileExtract.PublishesTaken"/>) - a file one of them names
        /// publishes it, as that file says in turn. Each file walked once on a stack of its own, so imports that come round end;
        /// found once for a file and a name, every later finding reading the first one's answer and what it read. What a file
        /// publishes is read under the file's own key, as what an import of it by its path makes visible is.
        /// </summary>
        private bool Publishes(string path, string name)
            => Cached(_publishes, (path, name), () =>
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                var pending = new Stack<string>([path]);

                while (pending.TryPop(out var next))
                {
                    if (!seen.Add(next))
                    {
                        continue;
                    }

                    Touch(Read.Opens, next);

                    if (_byPath.GetValueOrDefault(next) is not { Extract: { } extract } held)
                    {
                        continue;
                    }

                    if (PublishedOf(held).Contains(name))
                    {
                        return true;
                    }

                    if (extract.PublishesTaken)
                    {
                        Touch(Read.Imports, next);

                        foreach (var target in Taken(held).Where(taken => taken.Published).SelectMany(taken => FilesOf(taken, held)))
                        {
                            pending.Push(target);
                        }
                    }
                }

                return false;
            });

        /// <summary>The names <paramref name="file"/>'s own list publishes (<see cref="FileExtract.Published"/>), as a set made once a file.</summary>
        private HashSet<string> PublishedOf(CodeGraphFile file)
            => _publishedSets.TryGetValue(file, out var set) ? set : _publishedSets[file] = new HashSet<string>(file.Extract?.Published ?? [], StringComparer.Ordinal);

        /// <summary>
        /// What a file imported by path makes visible named <paramref name="name"/>: its own top-level declarations, those of
        /// the open scopes it declares, those it declares exported by that name (<see cref="ExtractNode.Exports"/>) - a node
        /// standing for a suffix written onto others' exported by one it stands for among them - and what each file it
        /// exports makes visible, that export's filter applied. Where <paramref name="suffixed"/>, the top-level nodes standing
        /// for a suffix written onto others' whose names it is one of besides (<see cref="Suffixed"/>). A walk that shares
        /// <paramref name="walked"/> with walks before it - a style sheet's imports walked as one - visits none of the files
        /// they reached and adds those it reaches; any other is found once a file, a name and <paramref name="suffixed"/>,
        /// every later finding of it reading the first one's answer and what it read (<see cref="VisibleFrom"/>).
        /// </summary>
        private IReadOnlyList<Declaration> Visible(string start, string name, HashSet<string>? walked, bool suffixed)
            => walked is null
                ? Cached(_visible, (start, name, suffixed), () => VisibleFrom(start, name, new HashSet<string>(StringComparer.Ordinal), suffixed))
                : VisibleFrom(start, name, walked, suffixed);

        /// <summary>
        /// What <see cref="Visible"/> finds from the file at <paramref name="start"/>, the files <paramref name="seen"/> holds
        /// not visited and each it visits added to it: each file visited once, so exports that come round again end, followed
        /// on a stack of its own rather than the call's. Each file reached is asked for the name it is reached by: the one asked
        /// of <paramref name="start"/>, or the one a renamed re-export shows a file's name by (<see cref="Reached"/>) - where a
        /// re-export by a prefix stands for the file itself, the containers of the open scopes it declares, its module.
        /// </summary>
        private List<Declaration> VisibleFrom(string start, string name, HashSet<string> seen, bool suffixed)
        {
            var visible = new List<Declaration>();
            var pending = new Stack<(string Path, string? Name)>([(start, name)]);

            while (pending.TryPop(out var next))
            {
                if (!seen.Add(next.Path))
                {
                    continue;
                }

                if (next.Name is null)
                {
                    visible.AddRange(ModuleOf(next.Path));
                    continue;
                }

                visible.AddRange(TopNamed(next.Path, next.Name).Concat(OpensOf(next.Path).SelectMany(scope => ScopeNamed(OpenKey(scope), next.Name))).Concat(ExportedNamed(next.Path, next.Name)));
                var file = _byPath.GetValueOrDefault(next.Path);

                if (file is not null)
                {
                    visible.AddRange(Suffixed(file, next.Name, exported: true));

                    if (suffixed)
                    {
                        visible.AddRange(Suffixed(file, next.Name, exported: false));
                    }
                }

                Touch(Read.Imports, next.Path);

                if (file?.Extract is not null)
                {
                    // Each file whose names this one makes its importers's, the file before what it reaches in turn.
                    foreach (var reached in Reached(file, next.Name).Reverse())
                    {
                        pending.Push(reached);
                    }
                }
            }

            return visible;
        }

        /// <summary>
        /// Each file whose names <paramref name="file"/> makes its importers's, asked for the name <paramref name="name"/>: those of
        /// its exports, and - where what an import binds is the importing file's importers's too (<see cref="ExtractImport.Reexports"/>) -
        /// those of the imports that bind that name: a renamed one reached by the name it binds it under there, since that is the name
        /// the file it names declares; one by a prefix that shows nothing reached by its prefix as the file itself, a
        /// <see langword="null"/> name (<see cref="VisibleFrom"/>); and one that takes the names a file publishes where that file
        /// publishes the name (<see cref="Publishes"/>). In the order the file writes them; none for a name none of them lets.
        /// </summary>
        private IEnumerable<(string Path, string? Name)> Reached(CodeGraphFile file, string name)
        {
            foreach (var import in Taken(file))
            {
                if (!(import.Export || import.Reexports))
                {
                    continue;
                }

                // One with no name of its own lets the name asked or nothing; a renamed one is reached by the name the file gives
                // it, and shows the name the file it names declares it by - or, showing none, is the file it names.
                var (lets, shown) = import.Prefix is null ? (import.Lets(name), name)
                    : import.Prefix != name ? (false, null)
                    : import.Show is [var one] ? (true, one)
                    : import.Show is null && import.Hide is null && !import.Export ? (true, (string?)null)
                    : (false, null);

                if (!lets)
                {
                    continue;
                }

                foreach (var target in FilesOf(import, file))
                {
                    if (!import.Published || Publishes(target, name))
                    {
                        yield return (target, shown);
                    }
                }
            }
        }

        /// <summary>The files of the tree <paramref name="import"/> names, by its form: found once for each import of each file.</summary>
        private IReadOnlyList<string> FilesOf(ExtractImport import, CodeGraphFile file)
            => Cached(_imported.TryGetValue(file, out var ofFile) ? ofFile : _imported[file] = new(ReferenceEqualityComparer.Instance), import, () => FindFiles(import, file));

        private IReadOnlyList<string> FindFiles(ExtractImport import, CodeGraphFile file)
        {
            switch (import.Form)
            {
                case CodeImportForms.Relative:
                    // A place of a layout (<see cref="ExtractImport.Layout"/>) in a directory that is a unit of it around the file is
                    // passed over: the name is looked for from the first directory above the units the file is in.
                    if (import.Layout && IsUnit(ReadFrom(file.Path, import.Target)))
                    {
                        return [];
                    }

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

                case CodeImportForms.Rooted:
                    // Every root of the layout that holds the path, in the roots' order: one is the place, several each a candidate.
                    var rooted = CodeGraphIds.Normalize(import.Target);

                    return rooted is null ? [] : [.. LayoutRoots().Select(root => root.Length == 0 ? rooted : $"{root}/{rooted}").Where(IsFile)];

                default:
                    var ending = CodeGraphIds.Normalize(import.Target);

                    if (ending is null)
                    {
                        return [];
                    }

                    return [.. PathsNamed(CodeGraphIds.FileName(ending)).Where(path => path == ending || path.EndsWith("/" + ending, StringComparison.Ordinal)).Order(StringComparer.Ordinal)];
            }
        }

        /// <summary>Whether <paramref name="directory"/> is a unit of its language's layout: a file in it says so (<see cref="FileExtract.FolderUnit"/>).</summary>
        private bool IsUnit(string directory)
        {
            Touch(Read.Unit, directory);
            return _units.Contains(directory);
        }

        /// <summary>
        /// The roots of the layout, in ordinal order, each once: the directory each outermost unit stands in - a unit whose own
        /// directory is no unit, the tree's root's own unit standing in none - and each directory a project file declares one
        /// (<see cref="FileExtract.LayoutRoots"/>). Made once, from every unit the tree holds; a finding that reads them reads the
        /// roots' one key, which every unit and every declared root is a part of.
        /// </summary>
        private IReadOnlyList<string> LayoutRoots()
        {
            Touch(Read.LayoutRoots, string.Empty);

            return _layoutRoots ??= [.. _units.Where(unit => unit.Length > 0 && !_units.Contains(DirectoryOf(unit)))
                .Select(DirectoryOf)
                .Concat(_declaredRoots)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)];
        }

        /// <summary>The directory a relative <paramref name="target"/> is read from for the file at <paramref name="path"/>: the file's own, one level less for each <c>../</c> the target begins with.</summary>
        private static string ReadFrom(string path, string target)
        {
            var directory = DirectoryOf(path);

            for (var at = 0; target.AsSpan(at).StartsWith("../"); at += 3)
            {
                directory = DirectoryOf(directory);
            }

            return directory;
        }

        /// <summary>The directory of the file at <paramref name="path"/>: the tree's root as empty.</summary>
        private static string DirectoryOf(string path)
            => path.LastIndexOf('/') is var slash && slash >= 0 ? path[..slash] : string.Empty;

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

            // An export makes names visible to the file's importers, not to the file; a directory import is applied to
            // the declaring file too, below, as one of the subtree it declares for; and one that serves one qualifier's references
            // alone serves no other reference (<see cref="ServingImports"/>).
            var own = Taken(file).Where(import => !import.Export && import.Form != CodeImportForms.Directory && import.Serves is null).ToList();

            return own.Select(import => (import, file))
                .Concat(own.Where(import => import.Shares).SelectMany(import => FilesOf(import, file)).Distinct(StringComparer.Ordinal).SelectMany(SharedOf))
                .Concat(file.Language is { } named && _global.TryGetValue(named, out var global)
                    ? global.Where(made => !ReferenceEquals(made.File, file)).Select(made => (made.Import, made.File))
                    : [])
                // A directory's scope imports are its language's, read as what every file of it imports is: a file that
                // declares one come, gone or changed alters that key, so every finding that looked past its scopes is found
                // again - whether or not a file declaring one stood under it before.
                .Concat(file.Language is { } declared && _ofDirectory.TryGetValue(declared, out var ofDirectory)
                    ? ofDirectory.Where(held => held.Import.Serves is null && Under(file.Path, held.Directory)).Select(held => (held.Import with { Form = CodeImportForms.Scope }, file))
                    : []);
        }

        /// <summary>
        /// The scope imports made for <paramref name="file"/> that serve the references of the qualifier <paramref name="kind"/>
        /// alone (<see cref="ExtractImport.Serves"/>), the nearest directory's first: those of the directories it is under, each
        /// of the subtree its declaring file's directory is - a <c>conftest.py</c>'s fixtures. Read under the language's key, as
        /// every directory import is.
        /// </summary>
        private IEnumerable<ExtractImport> ServingImports(CodeGraphFile file, string kind)
        {
            if (file.Language is not { } language)
            {
                return [];
            }

            Touch(Read.Global, language);

            return _ofDirectory.TryGetValue(language, out var ofDirectory)
                ? ofDirectory.Where(held => held.Import.Serves == kind && Under(file.Path, held.Directory))
                    .OrderByDescending(held => held.Directory.Length)
                    .Select(held => held.Import)
                : [];
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
        /// (<see cref="ExtractImport.Fallback"/>) taken as <see cref="TakenRuns"/> takes it.
        /// </summary>
        private IEnumerable<ExtractImport> Taken(CodeGraphFile file) => TakenRuns(file).SelectMany(run => run);

        /// <summary>
        /// What each run of an import and those that stand in for it (<see cref="ExtractImport.Fallback"/>) that
        /// <paramref name="file"/> makes is taken as, in order: the first of them that names a file of the tree - the run's first
        /// where none does - and, where that one is one of two places the name may stand at (<see cref="ExtractImport.Either"/>),
        /// the next of the run that is none of those and names a file too, beside it.
        /// </summary>
        private IEnumerable<IReadOnlyList<ExtractImport>> TakenRuns(CodeGraphFile file)
        {
            var imports = file.Extract!.Imports;

            for (var at = 0; at < imports.Count;)
            {
                var past = at + 1;

                while (past < imports.Count && imports[past].Fallback)
                {
                    past++;
                }

                IReadOnlyList<ExtractImport> taken = [imports[at]];

                for (var standing = at; past - at > 1 && standing < past; standing++)
                {
                    if (FilesOf(imports[standing], file).Count == 0)
                    {
                        continue;
                    }

                    taken = [imports[standing]];

                    for (var beside = standing + 1; imports[standing].Either && beside < past; beside++)
                    {
                        if (!imports[beside].Either && FilesOf(imports[beside], file).Count > 0)
                        {
                            taken = [imports[standing], imports[beside]];
                            break;
                        }
                    }

                    break;
                }

                yield return taken;
                at = past;
            }
        }

        /// <summary>
        /// Each file's <c>imports</c> edges: to a scope the repository declares, and to each file a path names - <c>exact</c>
        /// where the path says the one file, <c>unique</c> where an ending or a root of the layout finds one, and
        /// <c>candidates</c> where they find several or a run takes two places a name may stand at.
        /// </summary>
        private void Imports()
        {
            foreach (var file in _files.Where(file => file.Extract is not null))
            {
                var from = FileIdOf(file.Path);

                foreach (var run in TakenRuns(file))
                {
                    foreach (var import in run.Where(import => import.Form != CodeImportForms.Directory))
                    {
                        if (import.Form == CodeImportForms.Scope)
                        {
                            foreach (var id in ContainersOf(import.Target).Where(container => Sees(file, container)).Select(container => container.Id).Distinct())
                            {
                                _edges.Add(new CodeEdge(from, id.Whole(), CodeEdgeKinds.Imports, import.Line, CodeConfidences.Exact));
                            }

                            continue;
                        }

                        var targets = FilesOf(import, file);
                        var sure = run.Count > 1 ? CodeConfidences.Candidates
                            : import.Form is CodeImportForms.Ending or CodeImportForms.Rooted
                                ? targets.Count == 1 ? CodeConfidences.Unique : CodeConfidences.Candidates
                                : CodeConfidences.Exact;

                        foreach (var target in targets)
                        {
                            _edges.Add(new CodeEdge(from, FileIdOf(target), CodeEdgeKinds.Imports, import.Line, sure));
                        }
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
                            _edges.Add(new CodeEdge(member.Id.Whole(), answer.Id.Whole(), CodeEdgeKinds.Dispatches, answer.Node.Start, CodeConfidences.Weaker(way, found)));
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
        /// answer alike, each of them.
        /// </summary>
        private List<(Declaration Answer, string Sure)> Answering(string type, Declaration member, ILookup<string, CodeEdge> bases)
        {
            var seen = new HashSet<QualifiedName> { QualifiedName.Of(type) };
            List<BaseRank> level = [new BaseRank([QualifiedName.Of(type)], Mixin: false)];

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
                            .Select(answer => (Answer: answer, Sure: sure[at.Whole()])))
                        .GroupBy(answer => answer.Answer.Id)
                        .Select(alike => (alike.First().Answer, Sure: alike.Select(answer => answer.Sure).Aggregate(CodeConfidences.Surer)))
                        .ToList();

                    if (found.Count > 0)
                    {
                        return found.Count == 1 ? found : [.. found.Select(answer => (answer.Answer, CodeConfidences.Candidates))];
                    }
                }

                // Every way up to each base of the next level - each edge of each type that names it, a mixin's own bases
                // not among them - the surest taken.
                var ways = new Dictionary<string, string>(StringComparer.Ordinal);

                foreach (var rank in level)
                {
                    foreach (var at in rank.Ids)
                    {
                        foreach (var edge in bases[at.Whole()].Where(edge => edge.Kind == CodeEdgeKinds.MixesIn || (edge.Kind == CodeEdgeKinds.Inherits && !rank.Mixin)))
                        {
                            var way = CodeConfidences.Weaker(sure[at.Whole()], edge.Confidence);
                            ways[edge.To] = ways.TryGetValue(edge.To, out var known) ? CodeConfidences.Surer(known, way) : way;
                        }
                    }
                }

                level = [.. LevelUp(level)
                    .Select(rank => rank with { Ids = [.. rank.Ids.Where(id => ways.ContainsKey(id.Whole()) && !seen.Contains(id))] })
                    .Where(rank => rank.Ids.Length > 0)];
                sure = ways;
                seen.UnionWith(level.SelectMany(rank => rank.Ids));
            }

            return [];
        }

        /// <summary>The members the type <paramref name="id"/> declares, each once.</summary>
        private IReadOnlyList<Declaration> MembersOf(QualifiedName id)
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

                if (bodies.Count > 0)
                {
                    foreach (var body in bodies)
                    {
                        _edges.Add(new CodeEdge(prototype.Id.Whole(), body.Id.Whole(), CodeEdgeKinds.Declares, prototype.Node.Start, bodies.Count == 1 ? CodeConfidences.Unique : CodeConfidences.Candidates));
                    }
                }
            }
        }

        /// <summary>
        /// Each <c>tests</c> edge: from a test to each member it calls directly, as sure as the call - a test its own file
        /// marks, and one that is a test by its type (<see cref="TestsByType"/>).
        /// </summary>
        private void Tests()
        {
            var tests = _declarations.Where(declaration => declaration.Node.Test).Select(declaration => declaration.Id.Whole()).ToHashSet(StringComparer.Ordinal);
            tests.UnionWith(TestsByType());

            foreach (var call in _edges.Where(edge => edge.Kind == CodeEdgeKinds.Calls && tests.Contains(edge.From)).ToList())
            {
                _edges.Add(call with { Kind = CodeEdgeKinds.Tests });
            }
        }

        /// <summary>
        /// The declarations that are tests by their type (<see cref="ExtractNode.TestByType"/>), by id: each type so marked
        /// that is built on a test type - one marked a test, or built on one in turn, along the <c>inherits</c> and
        /// <c>mixesIn</c> edges found, however deep - and each member so marked that such a type holds. Walked from the test
        /// types out along the edges, each type once, so a chain of bases of any length, a cycle among them, is walked once.
        /// </summary>
        private List<string> TestsByType()
        {
            var marked = _declarations.Where(declaration => declaration.Node.TestByType).ToList();
            var found = new List<string>();

            if (marked.Count == 0)
            {
                return found;
            }

            var types = marked.Where(declaration => declaration.Node.Kind == CodeNodeKinds.Type).Select(declaration => declaration.Id.Whole()).ToHashSet(StringComparer.Ordinal);
            var built = new Dictionary<string, List<string>>(StringComparer.Ordinal);

            foreach (var edge in _edges.Where(edge => edge.Kind is CodeEdgeKinds.Inherits or CodeEdgeKinds.MixesIn && types.Contains(edge.From)))
            {
                Add(built, edge.To, edge.From);
            }

            var tested = _declarations.Where(declaration => declaration.Node is { Test: true, Kind: CodeNodeKinds.Type }).Select(declaration => declaration.Id.Whole()).ToHashSet(StringComparer.Ordinal);
            var pending = new Stack<string>(tested);

            while (pending.TryPop(out var type))
            {
                foreach (var derived in built.GetValueOrDefault(type) ?? [])
                {
                    if (tested.Add(derived))
                    {
                        found.Add(derived);
                        pending.Push(derived);
                    }
                }
            }

            found.AddRange(marked.Where(declaration => declaration.Node.Kind == CodeNodeKinds.Member && declaration.Node.Parent is { } holder && tested.Contains(holder.Whole()))
                .Select(declaration => declaration.Id.Whole()));
            return found;
        }

        /// <summary>
        /// Each <c>partOf</c> edge: one part of a declaration to the first of its parts, among the declarations that say
        /// they are parts (<see cref="ExtractNode.Part"/>) and are one declaration - those of one qualified name, exact: a
        /// XAML view and the C# type its <c>x:Class</c> names; and a Blazor component and the partial of its name its
        /// code-behind file declares - <c>Cart.razor.cs</c> beside <c>Cart.razor</c>, as Razor pairs them - whatever
        /// namespace the tree's path gives the component, for the namespace Razor compiles it into is its project's, which
        /// the file does not say: unique. Ids stay per language; the edge is derived, so no stored extract changes meaning.
        /// The parts are joined as they are met, each join kept, so each is found once (<see cref="PartsJoined"/>).
        /// </summary>
        private void Parts()
        {
            var parts = _declarations.Where(declaration => declaration.Node.Part).ToList();
            var joined = new PartsJoined(parts.Count);
            var byQualified = new Dictionary<QualifiedName, int>();
            var places = new Dictionary<Declaration, int>(ReferenceEqualityComparer.Instance);

            for (var at = 0; at < parts.Count; at++)
            {
                if (!byQualified.TryAdd(parts[at].Node.Qualified, at))
                {
                    joined.Join(at, byQualified[parts[at].Node.Qualified]);
                }

                places[parts[at]] = at;
            }

            foreach (var (codeBehind, component) in CodeBehinds())
            {
                joined.Join(places[codeBehind], places[component]);
            }

            foreach (var alike in Enumerable.Range(0, parts.Count).GroupBy(joined.Root).Select(group => group.Select(at => parts[at]).ToList()).Where(group => group.Count > 1))
            {
                var byId = new Dictionary<QualifiedName, Declaration>();

                foreach (var part in alike)
                {
                    byId.TryAdd(part.Id, part);
                }

                List<QualifiedName> ids = [.. byId.Keys.Order(ByText)];
                var first = byId[ids[0]];

                for (var at = 1; at < ids.Count; at++)
                {
                    var part = byId[ids[at]];
                    _edges.Add(new CodeEdge(ids[at].Whole(), ids[0].Whole(), CodeEdgeKinds.PartOf, part.Node.Start, part.Node.Qualified == first.Node.Qualified ? CodeConfidences.Exact : CodeConfidences.Unique));
                }
            }
        }

        /// <summary>
        /// Which parts are one declaration, as the joins found so far say: each part held to the one that stands for its
        /// group, each walk from a part to it keeping what it passed - every part on the way held to it directly - so no
        /// part is walked past again.
        /// </summary>
        private sealed class PartsJoined(int count)
        {
            private readonly int[] _held = [.. Enumerable.Range(0, count)];

            /// <summary>The part that stands for <paramref name="at"/>'s group.</summary>
            public int Root(int at)
            {
                var root = at;

                while (_held[root] != root)
                {
                    root = _held[root];
                }

                while (_held[at] != root)
                {
                    (_held[at], at) = (root, _held[at]);
                }

                return root;
            }

            /// <summary>Joins the groups of <paramref name="one"/> and <paramref name="other"/>.</summary>
            public void Join(int one, int other) => _held[Root(one)] = Root(other);
        }

        /// <summary>
        /// The keys' join: each <c>uses</c> edge a key of a configuration file gains - from the declaration that holds a
        /// string literal naming the key, and from each value (a field, a property) whose name is the key's last segment as
        /// a declared naming policy writes it - of <see cref="CodeConfidences.Candidates"/>, for nothing says the code reads
        /// that key. A literal names the keys of its last segment, of its whole path, and of the path it spells as .NET's
        /// configuration does (<see cref="CodeKeyPaths.Dotted"/>), and the keys a path reaches through what its file repeats
        /// by reference (<see cref="KeyPaths"/>); every key it names is joined, however many they are - a word every file of
        /// data holds, <c>name</c>, <c>version</c>, joined to each - and the confidence says how sure each is. A key its
        /// file keeps to itself - an MSBuild project's own property - is no other file's to read, so it joins none. A use is
        /// recall and never a doubt, so none of it is counted anywhere.
        /// </summary>
        private void Keys()
        {
            var bySegment = new Dictionary<string, List<Declaration>>(StringComparer.Ordinal);
            var byPath = new Dictionary<QualifiedName, List<Declaration>>();
            var byMember = new Dictionary<string, List<Declaration>>(StringComparer.Ordinal);

            foreach (var key in _declarations.Where(declaration => declaration.Node.SubKind == CodeSubKinds.Key && !declaration.FileOnly).DistinctBy(declaration => declaration.Id))
            {
                Add(bySegment, key.Node.Name, key);
                Add(byPath, key.Node.Qualified, key);

                foreach (var name in MemberNames(key.Node.Name))
                {
                    Add(byMember, name, key);
                }
            }

            if (bySegment.Count == 0)
            {
                return;
            }

            var through = new KeyPaths(_files, _owners);

            foreach (var file in _files)
            {
                if (file.Extract?.Literals is not { Count: > 0 } literals)
                {
                    continue;
                }

                foreach (var literal in literals)
                {
                    var spelt = CodeKeyPaths.Dotted(literal.Value);
                    var joined = Distinct(
                        (bySegment.TryGetValue(literal.Value, out var byName) ? byName : [])
                            .Concat(byPath.TryGetValue(literal.Value, out var named) ? named : [])
                            .Concat(spelt is not null && byPath.TryGetValue(spelt, out var dotted) ? dotted : [])
                            .Concat(through.Reached(literal.Value))
                            .Concat(spelt is null ? [] : through.Reached(spelt))
                            .Where(key => key.Node.SubKind == CodeSubKinds.Key && !key.FileOnly));

                    // A value is joined once, and each place it is written uses what it names: from the node whose code holds
                    // it, as its implementation said it - the file's own where none does.
                    foreach (var place in joined.Count == 0 ? [] : literal.Places)
                    {
                        foreach (var key in joined)
                        {
                            _edges.Add(new CodeEdge((place.Node ?? QualifiedName.Of(FileIdOf(file.Path))).Whole(), key.Id.Whole(), CodeEdgeKinds.Uses, place.Line, CodeConfidences.Candidates));
                        }
                    }
                }
            }

            foreach (var (name, keys) in byMember)
            {
                // A deserialiser binds a key to a value of its name - a field, a property - and never to a member it calls,
                // nor to another file's data.
                foreach (var member in (_byName.TryGetValue(name, out var members) ? members : []).Where(member => IsValue(member) && !CodeSubKinds.IsData(member.Node.SubKind)))
                {
                    foreach (var key in keys)
                    {
                        _edges.Add(new CodeEdge(member.Id.Whole(), key.Id.Whole(), CodeEdgeKinds.Uses, member.Node.Start, CodeConfidences.Candidates));
                    }
                }
            }
        }

        /// <summary>
        /// The keys a path reaches through what its data file repeats by reference: each node of a file that refers to
        /// another of its own (<see cref="CodeQualifierKinds.Own"/>) - a YAML alias, a mapping that merges others - stands
        /// for what it refers to, so a path that runs on past it runs on in each node it refers to, in order. Within a file,
        /// the longest part of the path the file holds a node at decides: a path the file holds whole is what it names, and
        /// one past a node that refers to none names nothing more - as a mapping's own key shadows the key of a mapping it
        /// merges. Each place a path is looked for is found once, every later look reading the first one's answer, and a
        /// place that comes round to itself through its references ends there; the files are followed by a trie of the
        /// places their references stand at, so a path is read once whatever files hold one.
        /// </summary>
        private sealed class KeyPaths
        {
            /// <summary>The places of every node each file that refers to its own holds, as a trie, each with the nodes at it and what it refers to.</summary>
            private readonly Dictionary<CodeGraphFile, Place> _files = new(ReferenceEqualityComparer.Instance);

            /// <summary>The places where a reference stands, of every file: each with the files whose node at it refers on.</summary>
            private readonly Place _referring = new();

            /// <summary>What each path is in each file, found once.</summary>
            private readonly Dictionary<(CodeGraphFile File, string Path), IReadOnlyList<Declaration>> _found = [];

            /// <summary>The paths of each file being looked for now, so a path its references bring round again ends.</summary>
            private readonly HashSet<(CodeGraphFile File, string Path)> _looking = [];

            public KeyPaths(IEnumerable<CodeGraphFile> files, IReadOnlyDictionary<CodeGraphFile, Dictionary<QualifiedName, Declaration>> owners)
            {
                foreach (var file in files)
                {
                    if (file.Extract?.References.Where(reference => reference.Qualifier is { Kind: CodeQualifierKinds.Own }).ToList() is not { Count: > 0 } refers)
                    {
                        continue;
                    }

                    var own = owners[file];
                    var root = new Place();

                    foreach (var node in own.Values.DistinctBy(node => node.Id))
                    {
                        if (CodeKeyPaths.OfNode(node.Node, file.Path) is { } path)
                        {
                            root.At(path).Nodes.Add(node);
                        }
                    }

                    foreach (var reference in refers)
                    {
                        if (own.TryGetValue(reference.From, out var from) && own.TryGetValue(reference.Qualifier!.Name!, out var to)
                            && CodeKeyPaths.OfNode(from.Node, file.Path) is { } at && CodeKeyPaths.OfNode(to.Node, file.Path) is { } target)
                        {
                            root.At(at).Refers.Add(target);

                            if (_referring.At(at).Files.Add(file))
                            {
                                _referring.At(at).Holding.Add(file);
                            }
                        }
                    }

                    _files[file] = root;
                }
            }

            /// <summary>The nodes <paramref name="path"/> reaches through a reference, in every file whose references it runs past.</summary>
            public IEnumerable<Declaration> Reached(string path)
            {
                if (_files.Count == 0)
                {
                    return [];
                }

                var reached = new List<Declaration>();
                var place = _referring;

                // Each file a node on the path refers on from, nearest the top first: the path is looked for in each.
                foreach (var (start, end) in CodeKeyPaths.Places(path))
                {
                    if (!place.Next.TryGetValue(path[start..end], out var next))
                    {
                        break;
                    }

                    place = next;

                    if (end < path.Length)
                    {
                        foreach (var file in place.Holding)
                        {
                            reached.AddRange(In(file, path));
                        }
                    }
                }

                return reached;
            }

            /// <summary>
            /// What <paramref name="path"/> names in <paramref name="file"/>: the nodes at it, where the file holds it whole;
            /// else, past the longest part of it the file holds, what the rest names in each node that part refers to - the
            /// paths it runs on into followed on a stack of their own, each answered once.
            /// </summary>
            private IReadOnlyList<Declaration> In(CodeGraphFile file, string path)
            {
                if (_found.TryGetValue((file, path), out var known))
                {
                    return known;
                }

                // The path asked for at the bottom, answered by nothing but what it runs on into.
                var asked = new Looked(null, [path]);
                var pending = new Stack<Looked>([asked]);

                while (pending.TryPeek(out var looked))
                {
                    if (looked.Next == looked.Paths.Count)
                    {
                        pending.Pop();

                        if (looked.Key is { } done)
                        {
                            _looking.Remove(done);
                            _found[done] = looked.Found;
                            pending.Peek().Found.AddRange(looked.Found);
                        }

                        continue;
                    }

                    var key = (file, looked.Paths[looked.Next++]);

                    if (_found.TryGetValue(key, out var answered))
                    {
                        looked.Found.AddRange(answered);
                    }
                    else if (_looking.Add(key))
                    {
                        // A path being looked for already has come round to itself through its references: it names nothing more.
                        var (held, onward) = Look(file, key.Item2);

                        if (onward.Count == 0)
                        {
                            _looking.Remove(key);
                            _found[key] = held;
                            looked.Found.AddRange(held);
                        }
                        else
                        {
                            pending.Push(new Looked(key, onward));
                        }
                    }
                }

                return asked.Found;
            }

            /// <summary>
            /// What <paramref name="path"/> is in <paramref name="file"/> at a look: the nodes at it, where the file holds it
            /// whole; else the paths it runs on into, past the longest part of it the file holds, in each node that part refers
            /// to - none where that part refers to none.
            /// </summary>
            private (IReadOnlyList<Declaration> Held, IReadOnlyList<string> Onward) Look(CodeGraphFile file, string path)
            {
                var place = _files[file];
                Place? held = null;
                var heldEnd = 0;
                var whole = true;

                foreach (var (start, end) in CodeKeyPaths.Places(path))
                {
                    if (!place.Next.TryGetValue(path[start..end], out var next))
                    {
                        whole = false;
                        break;
                    }

                    place = next;

                    if (place.Nodes.Count > 0)
                    {
                        (held, heldEnd) = (place, end);
                    }
                }

                return whole && place.Nodes.Count > 0 ? (place.Nodes, [])
                    : held is null ? ([], [])
                    : ([], [.. held.Refers.Select(target => target + path[heldEnd..])]);
            }

            /// <summary>One path looked for, with the paths it runs on into, how many of them are followed, and what they name together.</summary>
            /// <param name="Key">The file and path, where it is one to answer; <see langword="null"/> for the path asked for, which is answered by what it runs on into.</param>
            /// <param name="Paths">The paths it runs on into.</param>
            private sealed record Looked((CodeGraphFile File, string Path)? Key, IReadOnlyList<string> Paths)
            {
                public int Next { get; set; }

                public List<Declaration> Found { get; } = [];
            }

            /// <summary>One place of a trie of paths: the places that follow it, the nodes at it, what they refer to, and the files referring on from it.</summary>
            private sealed class Place
            {
                public Dictionary<string, Place> Next { get; } = new(StringComparer.Ordinal);

                public List<Declaration> Nodes { get; } = [];

                public List<string> Refers { get; } = [];

                public HashSet<CodeGraphFile> Files { get; } = new(ReferenceEqualityComparer.Instance);

                public List<CodeGraphFile> Holding { get; } = [];

                /// <summary>The place <paramref name="path"/> names from this one, made where it is not yet.</summary>
                public Place At(string path)
                {
                    var place = this;

                    foreach (var (start, end) in CodeKeyPaths.Places(path))
                    {
                        var text = path[start..end];

                        if (!place.Next.TryGetValue(text, out var next))
                        {
                            place.Next[text] = next = new Place();
                        }

                        place = next;
                    }

                    return place;
                }
            }
        }

        /// <summary>
        /// The names of the members a key's last segment <paramref name="segment"/> is as the naming policies the repository
        /// declares write them: the segment itself under <c>exact</c>, and under each of the others the member's name the
        /// policy's shape of the segment gives - <c>calling_convention</c>, <c>calling-convention</c> and
        /// <c>callingConvention</c> each the member <c>CallingConvention</c>, <c>timeout</c> under any of them
        /// <c>Timeout</c>. A policy takes only a segment shaped as it writes one: a camelCase one has no separator and
        /// begins lower-case; a snake_case one is lower-case words and digits joined by underscores, a kebab-case one by
        /// dashes - one word alone among them, <c>s3_bucket</c> <c>S3Bucket</c>.
        /// </summary>
        private IEnumerable<string> MemberNames(string segment)
        {
            if (segment.Length == 0)
            {
                return [];
            }

            static bool Words(string text, char separator)
                => text.All(character => char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) || character == separator) && char.IsAsciiLetterLower(text[0]);

            var names = new HashSet<string>(StringComparer.Ordinal);

            foreach (var naming in _keyNaming)
            {
                var words = naming switch
                {
                    CodebaseGraphShape.Exact => null,
                    CodebaseGraphShape.SnakeCase when Words(segment, '_') => segment.Split('_', StringSplitOptions.RemoveEmptyEntries),
                    CodebaseGraphShape.KebabCase when Words(segment, '-') => segment.Split('-', StringSplitOptions.RemoveEmptyEntries),
                    CodebaseGraphShape.CamelCase when segment.IndexOfAny(['_', '-']) < 0 && char.IsLower(segment[0]) => [segment],
                    _ => [],
                };

                if (words is null)
                {
                    names.Add(segment);
                }
                else if (words.Length > 0)
                {
                    names.Add(string.Concat(words.Select(word => string.Create(CultureInfo.InvariantCulture, $"{char.ToUpperInvariant(word[0])}{word[1..]}"))));
                }
            }

            return names;
        }

        /// <summary>
        /// The types <paramref name="from"/> is declared in, or is: the one what holds it reaches - else, where what holds it
        /// reaches none, every type whose scope it is declared in, as a member defined outside its type's body is; else what a
        /// member declared to extend one extends (<see cref="ExtractNode.Extends"/>, <see cref="Extended"/>), as a prototype
        /// assignment's member does, as its own file names it - each type of that name, else the constructor function it finds;
        /// else, where it is a constructor function another file writes the prototype of (<see cref="Constructs"/>), itself; none
        /// where neither is. Several are every one of them, never the first of them.
        /// </summary>
        private IReadOnlyList<Declaration> EnclosingTypes(Declaration from)
        {
            var at = from;
            var seen = new HashSet<QualifiedName>();

            while (at.Node.Kind != CodeNodeKinds.Type)
            {
                if (at.Node.Parent is not { } parent || !seen.Add(parent) || !_owners[from.File].TryGetValue(parent, out var holder))
                {
                    if (from.ScopeKey is { } key && Distinct(ContainersOf(key.Name).Where(container => container.Node.Kind == CodeNodeKinds.Type && KeyOf(container.File, container.Node.Qualified) == key)) is { Count: > 0 } scoped)
                    {
                        return scoped;
                    }

                    // A constructor function another file writes the prototype of is the type its own body's this is.
                    return from.Node.Extends is not null ? Extended(from) : Constructs(from) ? [from] : [];
                }

                at = holder;
            }

            return [at];
        }

        /// <summary>
        /// What <c>this</c> is where <paramref name="from"/> is written: the types it is declared in or extends
        /// (<see cref="EnclosingTypes"/>); else, where it declares none - a template's own code - the declarations that name its
        /// file their template, whose members a template's own names are.
        /// </summary>
        private IReadOnlyList<Declaration> Selves(Declaration from)
        {
            if (EnclosingTypes(from) is { Count: > 0 } enclosing)
            {
                return enclosing;
            }

            Touch(Read.Templates, from.File.Path);
            return _templates.TryGetValue(from.File.Path, out var templates) ? templates : [];
        }

        /// <summary>
        /// What <c>this</c> - <paramref name="selves"/>, what it is where <paramref name="from"/> is written - and the names
        /// <paramref name="written"/> after it come to: a value of the types and the constructor functions it is - with each
        /// one's bases - by the first name, each next one a value of the type the one before is declared with, as a chain's
        /// names are (<see cref="Step"/>); what this is where no name is written; none where this is nothing the repository
        /// declares.
        /// </summary>
        private Chained OfThis(Declaration from, IReadOnlyList<Declaration> selves, string? written)
        {
            if (selves.Count == 0)
            {
                return Chained.None;
            }

            var state = new Chained(ChainedKind.Types, selves, null);

            foreach (var name in written?.Split('.') ?? [])
            {
                state = Step(from, state, name);
            }

            return state;
        }

        /// <summary>Says that the finding under way read the key <paramref name="text"/> of <paramref name="kind"/>.</summary>
        private void Touch(Read kind, string text) => _reads?.Keys.Add(_keys.Of((int)kind, text));

        private void Touch(Read kind, QualifiedName name) => _reads?.Keys.Add(_keys.Of((int)kind, name));

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
        private IReadOnlyList<Declaration> ScopeNamed(ScopeKey key, string name)
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
        private bool ParentOf(ScopeKey key, out ScopeKey? parent)
        {
            _reads?.Keys.Add(_keys.Of(key));
            return _scopeParents.TryGetValue(key, out parent);
        }

        /// <summary>
        /// The types and scopes of the qualified name <paramref name="qualified"/>, held as the name the extract holds and
        /// compared by the text it writes.
        /// </summary>
        private IReadOnlyList<Declaration> ContainersOf(QualifiedName qualified)
        {
            Touch(Read.Qualified, qualified);
            return _containers.TryGetValue(qualified, out var containers) ? containers : [];
        }

        /// <summary>The qualified names of every type and scope that end with the run of a name's characters <paramref name="trailing"/>.</summary>
        private IReadOnlyList<QualifiedName> QualifiedEnding(string trailing)
        {
            Touch(Read.Trailing, trailing);
            return _byTrailing.TryGetValue(trailing, out var named) ? named : [];
        }

        /// <summary>The parts of the declaration <paramref name="id"/>: a type of several, one for each file.</summary>
        private IReadOnlyList<Declaration> PartsOf(QualifiedName id)
        {
            Touch(Read.Id, id);
            return _byId.TryGetValue(id, out var parts) ? parts : [];
        }

        /// <summary>
        /// The ids of the parts the files of <paramref name="id"/>'s parts join to it though their qualified names differ
        /// (<see cref="CodeBehinds"/>): a Blazor component's code-behind partial, and the component of a code-behind. The
        /// finding under way reads the pair of files, so a code-behind come, gone or renamed finds it again.
        /// </summary>
        private IReadOnlyList<QualifiedName> SiblingsOf(QualifiedName id)
        {
            foreach (var part in PartsOf(id).Where(part => part.Node.Part))
            {
                if (ComponentPath(part.File.Path) is { } component)
                {
                    Touch(Read.Siblings, component);
                }
            }

            return _siblings.TryGetValue(id, out var siblings) ? siblings : [];
        }

        /// <summary>The id of the node of the file at <paramref name="path"/>, a file of the tree: the one text every edge names it by.</summary>
        private string FileIdOf(string path) => _fileIds[path];

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

        /// <summary>
        /// What the file at <paramref name="path"/> is as a name stands for it: the containers its own nodes declare of the open
        /// scopes it declares - a Python module's namespace, which a name an import binds to the module stands for.
        /// </summary>
        private IReadOnlyList<Declaration> ModuleOf(string path)
            => Distinct(OpensOf(path).SelectMany(ContainersOf).Where(container => container.File.Path == path));

        /// <summary>The open scopes the file at <paramref name="path"/> declares: what an import of it by its path makes visible, with its top.</summary>
        private IEnumerable<QualifiedName> OpensOf(string path)
        {
            Touch(Read.Opens, path);
            return _byPath[path].Extract is { } extract ? extract.Scopes.Where(scope => scope.Open).Select(scope => scope.Name) : [];
        }

        /// <summary>
        /// What <paramref name="cache"/> holds for <paramref name="key"/>, made once by <paramref name="make"/>: the finding
        /// under way reads what making it read, whether it made it or found it made - those reads kept once, with the answer,
        /// and read through whole (<see cref="KeptReads"/>), so a check every finding makes costs each of them a reference.
        /// </summary>
        private T Cached<TKey, T>(Dictionary<TKey, (T Value, KeptReads Reads)> cache, TKey key, Func<T> make)
            where TKey : notnull
        {
            if (cache.TryGetValue(key, out var known))
            {
                _reads?.Through.Add(known.Reads);
                return known.Value;
            }

            var outer = _reads;
            var reads = _reads = new Reading();
            T value;

            try
            {
                value = make();
            }
            finally
            {
                _reads = outer;
            }

            var kept = reads.Kept();
            cache[key] = (value, kept);
            outer?.Through.Add(kept);
            return value;
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
                    if (!(before.TryGetValue(key, out var held) ? held : []).SequenceEqual(after.TryGetValue(key, out var holds) ? holds : []))
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
        /// whichever key it was reached: its id, its file's language, its kind and its kind within it - what
        /// <see cref="Takes"/> decides by - the scope it is declared in and the scope it is, the declaration that holds it - which says whether it is its file's top - what it takes, its value's type, what
        /// it extends, the scope it is kept to and the names it is exported by. Its name, its qualified name and each name it is exported by
        /// are keys and no field - a declaration whose name changed leaves one key for another, and a scope's key holds its
        /// qualified name; a line or a body is never read. A file with no extract holds no part: one that gains or loses its extract
        /// changes its open scopes'.
        /// </summary>
        private Dictionary<int, List<object>> Parts(CodeGraphFile? file)
        {
            var parts = new Dictionary<int, List<object>>();

            void Add(int key, object part) => (parts.TryGetValue(key, out var held) ? held : parts[key] = []).Add(part);

            void Named(Read kind, string text, object part) => Add(_keys.Of((int)kind, text), part);

            if (file?.Extract is not { } extract)
            {
                return [];
            }

            var closed = Closed(extract);

            Named(Read.Opens, file.Path, file.Path);

            foreach (var scope in extract.Scopes.Where(scope => scope.Open))
            {
                Named(Read.Opens, file.Path, scope.Name);
            }

            // What a file publishes to an import that takes its published names (<see cref="ExtractImport.Published"/>), and whether what
            // its own such imports take is published too: under the file's own key, so a name it gains or loses is found again.
            Named(Read.Opens, file.Path, extract.Published is { } published ? string.Join(PartEnd, published) : None);
            Named(Read.Opens, file.Path, extract.PublishesTaken ? "publishes-taken" : None);

            // Whether the file says its directory is a unit of its language's layout: under the directory's key, so a unit come or gone
            // is found again by every import of a layout's place passed over or taken in it - and under the roots' one key, since a
            // unit come or gone may make another outermost or no longer, and so move a root.
            if (extract.FolderUnit)
            {
                Named(Read.Unit, DirectoryOf(file.Path), file.Path);
                Named(Read.LayoutRoots, string.Empty, $"unit{FieldEnd}{DirectoryOf(file.Path)}");
            }

            if (extract.LayoutRoots is { } layoutRoots)
            {
                Named(Read.LayoutRoots, string.Empty, $"declared{FieldEnd}{file.Path}{FieldEnd}{string.Join(PartEnd, layoutRoots)}");
            }

            Named(Read.Imports, file.Path, file.Language ?? None);

            foreach (var import in extract.Imports)
            {
                Named(Read.Imports, file.Path, ImportPart(import));
            }

            if (file.Language is { } language)
            {
                // What every file of the language imports, and what every file under a directory of it does: both are read
                // under the language's key, so a finding that read neither is found again when one comes.
                foreach (var import in extract.Imports.Where(import => import.Global || import.Form == CodeImportForms.Directory))
                {
                    Named(Read.Global, language, ImportPart(import));
                }
            }

            foreach (var root in extract.AddressRoots)
            {
                Named(Read.Roots, string.Empty, $"{file.Path}{FieldEnd}{root.Prefix}{FieldEnd}{root.Directory}{FieldEnd}{(root.Whole ? "whole" : None)}{FieldEnd}{(root.Local ? "local" : None)}");
            }

            foreach (var scope in extract.Scopes)
            {
                Add(_keys.Of(KeyOf(file, scope.Name, closed)), scope.Parent is { } parent ? KeyOf(file, parent, closed) : None);
            }

            if (SuffixLengths(extract) is { Length: > 0 } suffixes)
            {
                var lengths = string.Join(',', suffixes);
                Named(Read.Suffixes, file.Path, lengths);
                Named(Read.Suffixes, string.Empty, lengths);
            }

            foreach (var node in extract.Nodes)
            {
                // Whether it is its file's alone is no field of its own: a closed scope's key is its file's, so the two keys say it.
                var scopeKey = Placed(file, node, closed).ScopeKey;
                var own = IsContainer(node) ? KeyOf(file, node.Qualified, closed) : (ScopeKey?)null;
                var shape = new NodePart(
                    node.Id,
                    file.Language,
                    node.Kind,
                    node.SubKind,
                    scopeKey,
                    own,
                    node.Parent,
                    node.MinArity,
                    node.MaxArity,
                    node.ValueType,
                    node.Extends,
                    node.SeenIn is { } seenIn ? KeyOf(file, seenIn, closed) : null,
                    node.Qualified,
                    node.Prototype,
                    string.Join(PartEnd, node.Exports ?? []),
                    string.Join(PartEnd, (node.SuffixOf ?? []).Select(id => id.Whole())),
                    string.Join(PartEnd, node.Selectors ?? []),
                    string.Join(PartEnd, node.Templates ?? []),
                    node.MembersByReceiver);

                Named(Read.Name, node.Name, shape);

                // A member extending a type is read by the name it extends too, which tells a constructor function from another.
                if (node.Extends is { } extends)
                {
                    Named(Read.Extending, SimpleName(extends), shape);
                }

                // A selector is a name the declaration is found by, as its own: one changed moves it from one name's key to another's.
                foreach (var selector in node.Selectors ?? [])
                {
                    Named(Read.Name, selector, shape);
                }

                // What a template's code reads is found on the declarations naming it their template, each under the template's
                // own path: a component that names a file its template, stops naming it or names another alters that file's key.
                foreach (var template in node.Templates ?? [])
                {
                    if (CodeGraphIds.Beside(file.Path, template) is { } named)
                    {
                        Named(Read.Templates, named, $"{node.Id.Whole()}{FieldEnd}{template}");
                    }
                }

                foreach (var exported in node.Exports ?? [])
                {
                    Named(Read.Name, exported, shape);
                }

                // A name of dots is read whole by a chain of links only where some declaration is so named, and else by
                // the name it ends with (DottedEnd): one come, gone or changed alters that key too.
                foreach (var dotted in ((string[])[node.Name, .. node.Exports ?? []]).Where(name => name.Contains('.', StringComparison.Ordinal)))
                {
                    Named(Read.Name, DottedEnd(dotted), shape);
                }

                // The id and the qualified name are names: their keys are held as the names the extract holds, each its
                // own part, so what the parts keep of a file nested any depth grows with its depth, not its square.
                Add(_keys.Of((int)Read.Id, node.Id), shape);

                // A part a component's file or its code-behind's declares is what pairs the two (CodeBehinds).
                if (node.Part && ComponentPath(file.Path) is { } component)
                {
                    Named(Read.Siblings, component, (file.Path, SimpleName(node.Qualified), node.Id));
                }

                if (own is { } scope)
                {
                    Add(_keys.Of((int)Read.Qualified, node.Qualified), shape);
                    Named(Read.Trailing, Trailing(node.Qualified.Whole()), node.Qualified);
                    Add(_keys.Of(scope), node.MembersByReceiver ? (scopeKey, node.MembersByReceiver) : (object?)scopeKey ?? None);
                }
                else if (node.MembersByReceiver && scopeKey is { } passed)
                {
                    // A member a name written alone passes over makes the scope it is declared in one that holds such members: it is
                    // read under that scope's key, as a type whose own scope holds its members is.
                    Add(_keys.Of(passed), (node.Id, node.MembersByReceiver));
                }
            }

            return parts;
        }

        /// <summary>
        /// What a finding reads of one declaration, as a part of a key a file holds: its id, its file's language, its kind and
        /// its kind within it, the scope it is declared in and the scope it is, the declaration that holds it, what it takes,
        /// its value's type, what it extends, the scope it is kept to, its qualified name, whether it is a prototype, and the
        /// names it is exported by and the nodes its names are written onto - each the extract's own text, held by reference,
        /// never joined into a text of its own.
        /// </summary>
        private sealed record NodePart(
            QualifiedName Id,
            string? Language,
            string Kind,
            string? SubKind,
            ScopeKey? Scope,
            ScopeKey? Own,
            QualifiedName? Parent,
            int? MinArity,
            int? MaxArity,
            string? ValueType,
            string? Extends,
            ScopeKey? SeenIn,
            QualifiedName Qualified,
            bool Prototype,
            string Exports,
            string SuffixOf,
            string Selectors,
            string Templates,
            bool MembersByReceiver);

        /// <summary>The keys of the types whose bases are not what they were: what a finding that went through them read.</summary>
        private IEnumerable<int> Moved(Dictionary<QualifiedName, List<string[]>>? before, Dictionary<QualifiedName, List<string[]>> now)
        {
            if (before is null)
            {
                yield break;
            }

            foreach (var id in before.Keys.Union(now.Keys))
            {
                if (Ranks(before.GetValueOrDefault(id)) != Ranks(now.GetValueOrDefault(id)))
                {
                    yield return _keys.Of((int)Read.Bases, id);
                }
            }
        }

        /// <summary>
        /// The digest of every type's bases written out, the types in order: what tells two rounds' bases apart. Hashed as it
        /// is written, never held as one text, so the loop keeps a digest a round however many types and bases a round finds.
        /// </summary>
        private static string Written(Dictionary<QualifiedName, List<string[]>> bases)
            => FileSystem.FileContentHash.Of(stream =>
            {
                using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), leaveOpen: true);

                foreach (var id in bases.Keys.Order(ByText))
                {
                    writer.Write(id.Whole());
                    writer.Write(FieldEnd);
                    writer.Write(FieldEnd);

                    foreach (var (rank, at) in bases[id].Select((rank, at) => (rank, at)))
                    {
                        writer.Write(at == 0 ? string.Empty : RankEnd);

                        for (var each = 0; each < rank.Length; each++)
                        {
                            writer.Write(each == 0 ? string.Empty : FieldEnd);
                            writer.Write(rank[each]);
                        }
                    }

                    writer.Write(PartEnd);
                }
            });

        /// <summary>One type's ranks of bases written out: each rank's ids and where it ends, so a base moved from one rank to another is a change too.</summary>
        private static string Ranks(List<string[]>? ranks) => ranks is null ? string.Empty : string.Join(RankEnd, ranks.Select(rank => string.Join(FieldEnd, rank)));

        /// <summary>What ends one rank of a type's bases written out.</summary>
        private const string RankEnd = "\u0003";

        /// <summary>
        /// What an import is, as a finding reads it: its form, target, prefix, filters, whether it stands in for the one
        /// before it, whether it is an export, whether it shares the imports of the file it names, whether its importers reach
        /// what it binds, whether it takes the names a file publishes, whether it is a place of a layout, whether its prefix
        /// stands for the name it shows, whether it is one of two places a name may stand at, and the qualifier whose references
        /// alone it serves - never its line, and not whether it is global, which only what every file of its language imports
        /// holds it under, nor whether it names the scope a directory's files are declared in, which the files read in that
        /// scope are read again by whenever it moves.
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
                import.Shares ? "shares" : None,
                import.Reexports ? "reexports" : None,
                import.Published ? "published" : None,
                import.Layout ? "layout" : None,
                import.Binds ? "binds" : None,
                import.Either ? "either" : None,
                import.Serves ?? None);

        /// <summary>The names of the closed scopes <paramref name="extract"/> declares.</summary>
        private static HashSet<QualifiedName> Closed(FileExtract extract) => extract.Scopes.Where(scope => !scope.Open).Select(scope => scope.Name).ToHashSet();

        /// <summary>
        /// How long the suffixes the nodes of <paramref name="extract"/> write onto others' names are
        /// (<see cref="ExtractNode.SuffixOf"/>) - each past its <c>&amp;</c>, those they are exported by among them - shortest first.
        /// </summary>
        private static int[] SuffixLengths(FileExtract extract)
            => [.. extract.Nodes.Where(node => node.SuffixOf is not null).SelectMany(node => (string[])[node.Name, .. node.Exports ?? []]).Select(suffix => suffix.Length - 1).Distinct().Order()];

        /// <summary>
        /// The scope <paramref name="node"/> of <paramref name="file"/> is declared in, by key, and whether it is its file's
        /// alone: a type or a scope whose own scope is closed is, as what it declares is.
        /// </summary>
        private static (ScopeKey? ScopeKey, bool FileOnly) Placed(CodeGraphFile file, ExtractNode node, HashSet<QualifiedName> closed)
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
        private ScopeKey KeyOf(CodeGraphFile file, QualifiedName name)
            => _closed.TryGetValue(file, out var closed) && closed.Contains(name) ? ClosedKey(file.Path, name) : OpenKey(name);

        /// <summary>Whether <paramref name="file"/> sees <paramref name="declaration"/>: one that is its file's alone is seen from that file alone.</summary>
        private static bool Sees(CodeGraphFile file, Declaration declaration) => !declaration.FileOnly || ReferenceEquals(declaration.File, file);

        /// <summary>
        /// Whether <paramref name="from"/> sees <paramref name="declaration"/>: from its file, and - where it is kept to a
        /// scope - from that scope, or a scope inside it, as the scopes <paramref name="from"/> is written in climb. But a member
        /// reached by a receiver alone (<see cref="ByReceiver"/>) sees, of what its own scope keeps to itself, its type's members
        /// alone - never what a namespace TypeScript merges with its type into that scope keeps to the namespace - as a name it
        /// writes alone finds nothing of that scope (<see cref="Scoped"/>).
        /// </summary>
        private bool Sees(Declaration from, Declaration declaration)
            => Sees(from.File, declaration)
                && (declaration.SeenKey is not { } seen
                    || (Chain(from).Contains(seen) && !(seen == from.ScopeKey && ByReceiver(from) && !ByReceiver(declaration))));

        /// <summary>Whether <paramref name="path"/> is under the directory <paramref name="directory"/>, the tree's root as empty: a scope import a file declares for its directory's subtree.</summary>
        private static bool Under(string path, string directory)
            => directory.Length == 0 || path.StartsWith(directory, StringComparison.Ordinal);

        private static ScopeKey KeyOf(CodeGraphFile file, QualifiedName name, HashSet<QualifiedName> closed)
            => closed.Contains(name) ? ClosedKey(file.Path, name) : OpenKey(name);

        private static ScopeKey OpenKey(QualifiedName name) => new(null, name);

        private static ScopeKey ClosedKey(string path, QualifiedName name) => new(path, name);
    }

    /// <summary>
    /// How a file's names of the scope its directory's path gave it (<see cref="FileExtract.FolderScope"/>) are read in the
    /// scope a directory above it names: the folder's names, one a directory, matched from its last to the last of the
    /// other's - the scope a name of the folder's begins is the one of the other its place gives, what is declared in it in
    /// that one, and a scope of the folder's above every name of the other's is none; and the scopes above the first the
    /// other holds that the folder's do not, each in the one above it.
    /// </summary>
    private sealed class FolderScopes
    {
        private readonly string _folder;
        private readonly string _scope;
        private readonly int _folderNames;
        private readonly string[] _scopeNames;

        /// <summary>Each scope the folder's is in, by its text: how many of the folder's names it writes.</summary>
        private readonly Dictionary<string, int> _above = new(StringComparer.Ordinal);

        public FolderScopes(string folder, string scope)
        {
            _folder = folder;
            _scope = scope;
            _scopeNames = scope.Split('.');

            var names = folder.Length == 0 ? [] : folder.Split('.');
            _folderNames = names.Length;

            for (var depth = 1; depth < names.Length; depth++)
            {
                _above.TryAdd(string.Join('.', names[..depth]), depth);
            }
        }

        /// <summary>
        /// <paramref name="name"/>, a scope's or a qualified name, read in the scope: the folder's scope, or what is declared
        /// in it, in the other; a scope the folder's is in, in the other's of its place, none where the other has no scope
        /// so high; any other name unchanged. The texts are made here and never kept by the names the extract holds: a file
        /// read so is named by its path, its names few and its own.
        /// </summary>
        public QualifiedName Name(QualifiedName name)
        {
            var text = name.Whole();

            if (_folderNames == 0)
            {
                return QualifiedName.Of($"{_scope}.{text}");
            }

            if (text == _folder || text.StartsWith(_folder + ".", StringComparison.Ordinal))
            {
                return QualifiedName.Of(_scope + text[_folder.Length..]);
            }

            return _above.TryGetValue(text, out var depth) && ScopeOf(depth + _scopeNames.Length - _folderNames) is { } scope ? QualifiedName.Of(scope) : name;
        }

        /// <summary><paramref name="extract"/> read in the scope: each node's qualified name, its scope and the scope it is seen in; each scope's name and parent; the other's scopes above the folder's; its ids and everything else kept.</summary>
        public FileExtract Of(FileExtract extract)
        {
            var scopes = new Dictionary<QualifiedName, ExtractScope>();

            // The other's scopes the folder's have no place of, each in the one above it, so its names are looked for to its top.
            for (var depth = 1; depth <= _scopeNames.Length - _folderNames; depth++)
            {
                var scope = QualifiedName.Of(ScopeOf(depth)!);
                scopes.TryAdd(scope, new ExtractScope(scope, Open: true) { Parent = ScopeOf(depth - 1) is { } above ? QualifiedName.Of(above) : null });
            }

            foreach (var held in extract.Scopes)
            {
                var name = Name(held.Name);
                scopes.TryAdd(name, held with { Name = name, Parent = held.Parent is { } parent ? Name(parent) : IsTop(held.Name) ? TopName : null });
            }

            return extract with
            {
                Nodes = [.. extract.Nodes.Select(node =>
                {
                    var qualified = Name(node.Qualified);

                    // A namespace the folder's names are is named as the other's of its place.
                    return node with
                    {
                        Qualified = qualified,
                        Name = qualified != node.Qualified && !qualified.Is(node.Name) && !qualified.Whole().EndsWith("." + node.Name, StringComparison.Ordinal) ? qualified.PastLastDot() : node.Name,
                        Scope = node.Scope is { } within ? Name(within) : IsTop(node.Qualified) ? TopName : null,
                        SeenIn = node.SeenIn is { } seen ? Name(seen) : null,
                    };
                })],
                Scopes = [.. scopes.Values],
                FolderScope = null,
            };
        }

        /// <summary>Whether <paramref name="name"/> is what stood at the top of the file: the folder's first scope, or at the tree's root any name in none.</summary>
        private bool IsTop(QualifiedName name)
        {
            var text = name.Whole();
            return _folderNames == 0 || _above.GetValueOrDefault(text) == 1 || (_folderNames == 1 && text == _folder);
        }

        /// <summary>The scope what stood at the top of the file is in now: the one above the other's place of the folder's first scope; at the tree's root the other itself.</summary>
        private QualifiedName? TopName => ScopeOf(_scopeNames.Length - _folderNames) is { } top ? QualifiedName.Of(top) : null;

        /// <summary>The other scope's first <paramref name="depth"/> names, joined; none for none.</summary>
        private string? ScopeOf(int depth) => depth >= 1 ? string.Join('.', _scopeNames[..depth]) : null;
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

    /// <summary>
    /// Whether <paramref name="node"/> is what a CSS module keeps to itself and exports - a class, an id, keyframes the
    /// module renames, which a script reaches by the names it is exported by (<see cref="ExtractNode.Exports"/>) and no
    /// class markup writes reaches by its own.
    /// </summary>
    private static bool Renamed(ExtractNode node) => CodeSubKinds.IsData(node.SubKind) && node.SeenIn is not null && node.Exports is not null;

    /// <summary>Whether a reference of <paramref name="kind"/> names a base of the type it is made in.</summary>
    private static bool IsBase(string kind) => kind is CodeEdgeKinds.Inherits or CodeEdgeKinds.Implements or CodeEdgeKinds.MixesIn;

    /// <summary>
    /// Whether <paramref name="target"/> is something <paramref name="reference"/> can name: of a kind its edge reaches, and
    /// taking its arity. A node of data - a configuration key, an element, a node a style sheet declares
    /// (<see cref="CodeSubKinds"/>) - is reached only by a reference that says it names one: a selector a rule, a key
    /// qualifier a key, a style sheet's variable, mixin, function, keyframes or font face qualifier its kind - and a fixture
    /// qualifier a fixture - (<see cref="CodeSubKinds.ReachedBy"/>), each of the kind it names alone; a use through an import's prefix whatever
    /// the file it imports declares; and a use an import makes visible (<paramref name="imported"/>) by a name the file
    /// that declares the node exports it by - <c>import { btn } from './x.module.css'</c> as <c>styles.btn</c> is, and a
    /// node standing for a suffix written onto others' (<see cref="ExtractNode.SuffixOf"/>), which no name finds but one
    /// it was matched to stand for - never one that names it by its name alone, as code names code; and a reference to a
    /// node of its own file by its id (<see cref="CodeQualifierKinds.Own"/>) reaches that node, whatever it is: the one
    /// place that says what a reference may reach. What a CSS module keeps to itself and exports - a class, an id,
    /// keyframes (<see cref="Renamed"/>) - is reached by its name only from a node of a style sheet, its own sheet's
    /// <c>composes</c> or <c>@extend</c>: the module renames it, so a class markup writes - a component's
    /// <c>class="red"</c> beside its <c>&lt;style module&gt;</c> - reaches none of it; its exports do, through an import
    /// and through a binding of its own file's module (<see cref="CodeQualifierKinds.Module"/>), which reaches nothing else.
    /// </summary>
    /// <param name="from">The declaration the reference is made in.</param>
    /// <param name="reference">The reference.</param>
    /// <param name="target">A declaration it may name.</param>
    /// <param name="imported">Whether the declaration is what an import makes visible.</param>
    private static bool Takes(Declaration from, ExtractReference reference, Declaration target, bool imported)
    {
        // A reference to a node of its own file by id names that node, whatever kind it is.
        if (reference.Qualifier?.Kind == CodeQualifierKinds.Own)
        {
            return true;
        }

        // A binding of a module of its own file uses what the module exports, and nothing else.
        if (reference.Qualifier?.Kind == CodeQualifierKinds.Module)
        {
            return reference.Kind == CodeEdgeKinds.Uses && Renamed(target.Node);
        }

        if (CodeSubKinds.ReachedBy(reference.Qualifier?.Kind) is { } reached)
        {
            return target.Node.Kind == CodeNodeKinds.Member && target.Node.SubKind == reached
                && (!Renamed(target.Node) || CodeSubKinds.IsData(from.Node.SubKind));
        }

        if (CodeSubKinds.IsData(target.Node.SubKind)
            && (reference.Kind != CodeEdgeKinds.Uses
                || !(reference.Qualifier?.Kind == CodeQualifierKinds.Prefix
                    || (imported && (target.Node.SuffixOf is not null || target.Node.Exports?.Contains(reference.Name, StringComparer.Ordinal) == true)))))
        {
            return false;
        }

        return reference.Kind switch
        {
            CodeEdgeKinds.Calls => (target.Node.Kind == CodeNodeKinds.Member && target.Node.TakesArity(reference.Arity)) || target.Node.Kind == CodeNodeKinds.Type,
            CodeEdgeKinds.Uses => target.Node.Kind is CodeNodeKinds.Member or CodeNodeKinds.Type,
            CodeEdgeKinds.Overrides => target.Node is { Kind: CodeNodeKinds.Member, Extends: null } && target.Node.TakesArity(reference.Arity),
            CodeEdgeKinds.ProjectReference => target.Node.Kind == CodeNodeKinds.Project,
            _ => target.Node.Kind == CodeNodeKinds.Type,
        };
    }

    /// <summary>Whether <paramref name="declaration"/> is a type or a scope: what a qualifier may name to be looked in.</summary>
    private static bool IsContainer(Declaration declaration) => IsContainer(declaration.Node);

    private static bool IsContainer(ExtractNode node) => node.Kind is CodeNodeKinds.Type or CodeNodeKinds.Namespace;

    /// <summary>Whether <paramref name="declaration"/> is a value: a member no call is made of by its arguments - a field, a property, an event.</summary>
    private static bool IsValue(Declaration declaration)
        => declaration.Node is { Kind: CodeNodeKinds.Member, MinArity: null, MaxArity: null };

    /// <summary>One declaration of each id among <paramref name="declarations"/>: a type of several parts is one target.</summary>
    private static IReadOnlyList<Declaration> Distinct(IEnumerable<Declaration> declarations)
        => [.. declarations.DistinctBy(declaration => declaration.Id)];

    private static void Add<T>(Dictionary<string, List<T>> into, string key, T value)
    {
        if (!into.TryGetValue(key, out var values))
        {
            into[key] = values = [];
        }

        values.Add(value);
    }

    private static void Add<T>(Dictionary<QualifiedName, List<T>> into, QualifiedName key, T value)
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

    /// <summary>
    /// The key a name of dots is also held under: the name it ends with, past its last dot, after a mark no name holds - what
    /// a chain written as links reads where no declaration is named as the chain's names write (<see cref="Linking"/>), so a
    /// declaration of that name come, gone or changed finds the chain again.
    /// </summary>
    private static string DottedEnd(string name) => $"\u0005{name[(name.LastIndexOf('.') + 1)..]}";

    /// <summary>Whether <paramref name="character"/> is one a name is made of, and no boundary between two.</summary>
    private static bool IsNamePart(char character) => char.IsLetterOrDigit(character) || character is '_' or '$';

    /// <summary>The last part of a dotted name: a type's own name, as an extension names the type it extends.</summary>
    private static string SimpleName(string written) => written[(written.LastIndexOf('.') + 1)..];

    /// <summary>The last part of a dotted name a name holds, read from its own parts: a type's own name, as its file is paired with its code-behind's.</summary>
    private static string SimpleName(QualifiedName written) => written.PastLastDot();

    /// <summary>How names are told apart by the text they write, where their order is read.</summary>
    private static readonly Comparer<QualifiedName> ByText = Comparer<QualifiedName>.Create((one, other) => string.CompareOrdinal(one.Whole(), other.Whole()));

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
/// <param name="References">How many references were found: every one by a whole link, those a change could have changed by a link made again.</param>
public sealed record CodeGraphLinked(
    IReadOnlyList<CodeGraphSymbol> Symbols,
    IReadOnlyList<CodeEdge> Edges,
    IReadOnlyDictionary<string, int> External,
    int References)
{
    /// <summary>What the link keeps for one made again from it (<see cref="CodeGraphLinker.Relink"/>).</summary>
    internal CodeGraphLinker.LinkMemory? Memory { get; init; }

    /// <summary>
    /// How many links of chains of names the link stepped (<see cref="ExtractQualifier.Link"/>): each once for each
    /// declaration, chain before it and name - two statements that write one chain step it once - and again for each round
    /// of the bases that might move what it comes to.
    /// </summary>
    internal int ChainLinks { get; init; }

    /// <summary>
    /// How many files the walks of what a file imported makes visible visited - itself and each file it exports, in turn:
    /// each once for each file and name it is asked for, however many references ask it.
    /// </summary>
    internal int ExposedFiles { get; init; }
}
