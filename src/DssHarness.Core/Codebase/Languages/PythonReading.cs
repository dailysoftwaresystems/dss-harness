using System.Collections.Immutable;
using System.Globalization;
using DssHarness.Core.Codebase.Languages.Scanning;

namespace DssHarness.Core.Codebase.Languages;

/// <summary>
/// One Python file's reading over the structural scanner: its module, a namespace named by the dotted path the file is
/// written at - a package's <c>__init__.py</c> its directory's - and one open scope, every name of which an importer may
/// reach; what stands at its top and in its classes - each <c>class</c> a type, each <c>def</c> and <c>async def</c> a function
/// or a method, <c>__init__</c> a constructor, each name assigned at the top or in a class a constant or a field, a target
/// list's each, each attribute a method assigns through <c>self</c> or <c>cls</c> a field of its class, each <c>type</c>
/// alias a type, nested classes and functions kept with the node that holds them - with its lines, signature and summary;
/// its imports - a bare module looked for by the package layout and its roots, a relative one beside the file - each name
/// they bind for the code of the declaration they are written in; its base lists, the first an <c>inherits</c> and the others
/// <c>mixesIn</c>; each call a body makes by its name and arity, with what its receiver is where the code says - <c>self</c>,
/// <c>cls</c>, <c>super()</c>, a class, what an import reaches - and the head and each member of a chain on its way, each
/// class a call makes where the code writes a capitalized name, each type an annotation, a string annotation, an
/// <c>isinstance</c>, an <c>except</c> or a <c>case</c>'s class pattern names, each attribute and name a body reads without
/// calling it and does not bind - a local, a parameter, a lambda's, a comprehension's or a closure's being the code's own -
/// each decorator's code, the name it calls with what it decorates a call, and each string literal the file holds; and the tests the
/// runners collect - a test file's <c>test*</c> functions and <c>Test*</c> classes, a <c>TestCase</c> subclass wherever it is
/// declared, a test class's <c>test*</c> methods - each fixture a parameter of a test or of a fixture names a reference of it.
/// </summary>
/// <remarks>
/// Python's blocks are made by indentation, which is no token: each token says its line, and the reader finds its column
/// from where it begins, past the last form feed its line's blanks hold (<see cref="Margin"/>). A statement ends where the next
/// logical line begins - the first token on a later line outside every bracketed group, one a backslash ending its line's code
/// does not continue (<see cref="Continued"/>) - that starts no deeper than the statement's own column: the next
/// statement of its block, or the dedent that ends the block. A header's suite is deeper than its header, and so is passed
/// over whole, however deep it nests; the suite itself is the statements that begin at one column, or the simple statements
/// of the header's own line. The file's logical lines and where each one's block ends are found once, in one pass with a
/// stack of the lines still open (<see cref="Lines"/>), so where a statement ends is looked up, never walked again by each
/// block it is nested in. A decorator's line belongs to the declaration it stands above. Each block's statements are read
/// in a walk of the reader's stack (<see cref="ScanStack"/>), never a call into the reading from inside one, so a block nested
/// any depth is read in the order the text writes it and no nesting reaches past the reader's stack. A function's body is a
/// scope of its own, closed, whose parent is the function or the module around it and never a class (<see cref="ExtractScope"/>),
/// as Python looks a body's names up. A declaration's head and body are read once every declaration of the file is made, each
/// before the declarations it holds, the functions around a body open while it is read, so a closure's names are its own
/// (<see cref="Bodies"/>): a run over its tokens (<see cref="Calls"/>) that passes a nested declaration over whole, at once, and
/// what a statement keeps to itself - a name declared, an annotation, an import, a pattern's captures - marked as it is read
/// (<see cref="Keep"/>), so each token is read once, by the one node it is the code of.
/// </remarks>
internal sealed class PythonReading
{
    /// <summary>
    /// The words of the language: no call is made by them and no value the code reads. Its soft keywords - <c>match</c>,
    /// <c>case</c>, <c>type</c> - are names, a keyword only where they head a statement of their own (<see cref="CompoundAt"/>,
    /// <see cref="TypeAlias"/>): a function or a variable may be named by one.
    /// </summary>
    private static readonly HashSet<string> Words = new(StringComparer.Ordinal)
    {
        "and", "as", "assert", "async", "await", "break", "class", "continue", "def", "del", "elif", "else", "except", "finally", "for", "from", "global", "if",
        "import", "in", "is", "lambda", "nonlocal", "not", "or", "pass", "raise", "return", "try", "while", "with", "yield",
    };

    /// <summary>The names the language and its standard library answer to, which every file sees and the repository does not: no call a reference names.</summary>
    private static readonly HashSet<string> Builtins = new(StringComparer.Ordinal)
    {
        "True", "False", "None", "NotImplemented", "Ellipsis", "abs", "aiter", "all", "anext", "any", "ascii", "bin", "bool", "breakpoint", "bytearray",
        "bytes", "callable", "chr", "classmethod", "compile", "complex", "delattr", "dict", "dir", "divmod", "enumerate", "eval", "exec", "filter",
        "float", "format", "frozenset", "getattr", "hasattr", "hash", "hex", "id", "input", "int", "isinstance", "issubclass", "iter", "len", "list",
        "locals", "map", "max", "memoryview", "min", "next", "object", "oct", "open", "ord", "pow", "print", "property", "range", "repr", "reversed",
        "round", "set", "setattr", "slice", "sorted", "staticmethod", "str", "sum", "super", "tuple", "type", "vars", "zip", "__import__", "__name__",
        "ArithmeticError", "AssertionError", "AttributeError", "BaseException", "BufferError", "EOFError", "Exception", "FileExistsError",
        "FileNotFoundError", "FloatingPointError", "GeneratorExit", "ImportError", "IndentationError", "IndexError", "KeyError", "KeyboardInterrupt",
        "LookupError", "MemoryError", "ModuleNotFoundError", "NameError", "NotImplementedError", "OSError", "OverflowError", "RecursionError",
        "ReferenceError", "RuntimeError", "StopAsyncIteration", "StopIteration", "SyntaxError", "SystemError", "SystemExit", "TabError", "TypeError",
        "UnboundLocalError", "UnicodeDecodeError", "UnicodeEncodeError", "UnicodeError", "UnicodeTranslateError", "ValueError", "ZeroDivisionError",
    };

    /// <summary>
    /// What pytest's decorators are, by the dotted name an import of their package reaches them by: a fixture - pytest's own, and
    /// pytest-asyncio's for a coroutine - and a parameter set, whose names a test is given in place of a fixture of each name. Each
    /// is known by what binds the decorator's head, an import of the package, never by the last name it is written with.
    /// </summary>
    private static readonly HashSet<string> FixtureDecorators = new(StringComparer.Ordinal) { "pytest.fixture", "pytest_asyncio.fixture" };

    /// <inheritdoc cref="FixtureDecorators"/>
    private const string ParameterSets = "pytest.mark.parametrize";

    /// <summary>The assigning punctuators, each a read and a write of the name it follows.</summary>
    private static readonly HashSet<string> Augments = new(StringComparer.Ordinal) { "+=", "-=", "*=", "/=", "//=", "%=", "@=", "&=", "|=", "^=", ">>=", "<<=", "**=" };

    /// <summary>The attribute access the code computes: no edge, marked dynamic.</summary>
    private static readonly HashSet<string> Computed = new(StringComparer.Ordinal) { "getattr", "setattr", "delattr", "hasattr" };

    /// <summary>
    /// The kinds of the marks the reading makes, each the sign of a limit of the implementation's help (<see cref="CodeGraphLimit.Signs"/>):
    /// an answer says of a file the limits its marks show, and no other.
    /// </summary>
    internal const string
        ComputedAttribute = "computed-attribute",
        DynamicImport = "dynamic-import",
        ComputedCode = "computed-code",
        FunctionValue = "function-value",
        ComputedExports = "computed-exports",
        Unclosed = "unclosed";

    /// <summary>
    /// The keywords a compound statement begins with: its header to a colon, then its suite. <c>async for</c> and <c>async
    /// with</c>, and <c>match</c> with its <c>case</c> clauses, are told apart where they head a statement (<see cref="CompoundAt"/>).
    /// </summary>
    private static readonly HashSet<string> Compound = new(StringComparer.Ordinal) { "if", "elif", "else", "while", "for", "try", "except", "finally", "with" };

    /// <summary>How Python writes its calls: a name that is no word of the language, then <c>(</c>; a trailing comma ends the arguments.</summary>
    private static readonly ScanCallRules PythonMemberCalls = new(new HashSet<string>(StringComparer.Ordinal), null, TrailingComma: true);

    private readonly InterpolatedText _text;
    private readonly ScannedText _scan;
    private readonly string _prefix;
    private readonly string _path;

    /// <summary>
    /// Whether a test runner collects the file by its name: pytest's <c>test_*.py</c> and <c>*_test.py</c>, and unittest's and
    /// Django's <c>test*.py</c> - <c>tests.py</c> among them - its <c>test*</c> functions, <c>Test*</c> classes and the classes that
    /// may be tests by a base another file declares the file's tests (<see cref="TestFile"/>).
    /// </summary>
    private readonly bool _testFile;

    /// <summary>Whether the file is a package's <c>__init__.py</c>: the one that says its directory is a unit of the layout (<see cref="FileExtract.FolderUnit"/>).</summary>
    private readonly bool _package;
    private readonly ScanExtract _extract = new();

    /// <summary>The walks of the file's blocks.</summary>
    private readonly ScanStack _walks = new();

    /// <summary>Where each line's indentation is counted from, by its number, one past it once found (<see cref="Margin"/>); 0 for a line not yet asked about.</summary>
    private long[]? _margins;

    /// <summary>Where each token begins that no run of the body it stands in reads: a declaration's own, read by its own reading.</summary>
    private readonly HashSet<long> _kept = [];

    /// <summary>The names the imports at the module's top bind - in its conditional blocks too - by the name the code reaches them by.</summary>
    private readonly Dictionary<string, Binding> _bindings = new(StringComparer.Ordinal);

    /// <summary>The names the imports of each function and class bind for its own code, by the declaration's key (<see cref="BindingOf"/>).</summary>
    private readonly Dictionary<QualifiedName, LocalBindings> _local = [];

    /// <summary>
    /// The names the module's top binds, in the order the file first binds them - what it declares and what it imports alike: those not
    /// written with a leading underscore are what a <c>*</c> import of it binds where <see cref="_all"/> names none
    /// (<see cref="Published"/>).
    /// </summary>
    private readonly List<string> _top = [];

    /// <summary>Whether <see cref="_top"/> holds a name already, so each is kept once.</summary>
    private readonly HashSet<string> _bound = new(StringComparer.Ordinal);

    /// <summary>The entries of the module's <c>__all__</c>, where it is a list of string literals alone.</summary>
    private readonly List<string> _all = [];

    /// <summary>Whether the module's <c>__all__</c> is a list of string literals the code leaves alone (<see cref="ChangedAll"/>).</summary>
    private bool _allLiteral;

    /// <summary>The classes the file declares, by name: what a member reached through their name is qualified by.</summary>
    private readonly HashSet<string> _classes = new(StringComparer.Ordinal);

    /// <summary>The functions the file declares, by name: called by their name, never through a value.</summary>
    private readonly HashSet<string> _functions = new(StringComparer.Ordinal);

    /// <summary>
    /// What each class the file declares is by its type, by name, as its bases say once they are read (<see cref="Cases"/>): a
    /// <c>TestCase</c> subclass, one that may be one by a base another file declares, or neither - what a class built on it is.
    /// </summary>
    private readonly Dictionary<string, Cases> _cases = new(StringComparer.Ordinal);

    /// <summary>The test classes the file declares, by key: a method of one named <c>test*</c> is a test.</summary>
    private readonly HashSet<QualifiedName> _testTypes = [];

    /// <summary>
    /// The classes of a test file that may be test classes by a base another file declares, by key: each is marked so
    /// (<see cref="ExtractNode.TestByType"/>), and each method of one named <c>test*</c> too, for the linker to decide.
    /// </summary>
    private readonly HashSet<QualifiedName> _maybeTestTypes = [];

    /// <summary>
    /// What a declaration's statements read that the walk meets before the file's names are bound - an <c>except</c>'s types, a
    /// <c>case</c>'s class, an annotation, a type alias's value, a class's assigned values - by where it begins: read by the run of
    /// the code it stands in when that run reaches it (<see cref="CallAt"/>), once every name of the file is bound and the functions
    /// around it are open, in the order the text writes it, with where the run goes on past it (<see cref="Defer"/>).
    /// </summary>
    private readonly Dictionary<long, Deferred> _deferred = [];

    /// <summary>
    /// The names the imports of the functions around the code under way bind, the nearest last: what a body reads through a closure,
    /// as Python looks a name up through the functions around it (<see cref="Bodies"/>, <see cref="BindingOf"/>).
    /// </summary>
    private readonly Dictionary<string, List<Binding>> _enclosed = new(StringComparer.Ordinal);

    /// <summary>The type parameters each declaration that writes some declares - <c>def f[T]</c>, <c>class C[T]</c>, <c>type A[T]</c> - by its key.</summary>
    private readonly Dictionary<QualifiedName, HashSet<string>> _typeParameters = [];

    /// <summary>The type parameters the code of each node sees - its own and those of every declaration around it - by its key, found once a node (<see cref="TypeParametersOf"/>).</summary>
    private readonly Dictionary<QualifiedName, ImmutableHashSet<string>> _typeScopes = [];

    /// <summary>
    /// Whether the code read around a declaration (<see cref="ScanExtract.Around"/>) runs in its type parameters' scope - a class's bases
    /// and keywords, which see <c>T</c> of <c>class C[T]</c> - rather than outside it, as its decorators and a function's defaults do.
    /// </summary>
    private bool _typed;

    /// <summary>
    /// The work of the reading's walks over the file's structure, counted: each token's column asked (<see cref="Column"/>) - finding
    /// where each statement and block ends by indentation - each token a split of simple statements passes (<see cref="Simple(int, int)"/>),
    /// and each name of a chain whose members' qualifiers are found (<see cref="Receivers"/>). It grows with the file's text, never with
    /// a depth times a length - blocks nested deep around a long suite, classes nested in blocks, a long chain.
    /// </summary>
    internal long Work { get; private set; }

    /// <summary>The names of the functions and classes the file declares outside every class - at its top or in a function: each, as a name the module's top binds (<see cref="_bound"/>) or an import does, one a builtin of its name gives way to (<see cref="Shadows"/>).</summary>
    private readonly HashSet<string> _free = new(StringComparer.Ordinal);

    /// <summary>Where each declaration the file holds - a function's or a class's, with the decorators above it - begins, by its first token, and the index past its last: what the code around it, and what that code binds, passes over whole, at once.</summary>
    private readonly Dictionary<int, int> _declared = [];

    /// <summary>Where each name a <c>case</c>'s pattern binds begins - a capture, a name <c>as</c> gives, a starred one: bound by the body around it, as a <c>for</c>'s targets are.</summary>
    private readonly HashSet<long> _captures = [];

    /// <summary>
    /// The colon that ends each header the file writes - a compound statement's, a <c>def</c>'s, a <c>class</c>'s - by index: what a
    /// statement on the header's own line begins after, as no other colon at a statement's depth - an annotation's - is (<see cref="StatementHead"/>).
    /// </summary>
    private readonly HashSet<int> _headerColons = [];

    /// <summary>
    /// The reading of each declaration's head and body, in the order the text writes the declarations - each before those it holds -
    /// read once every declaration of the file is made (<see cref="Bodies"/>).
    /// </summary>
    private readonly List<Body> _bodies = [];

    /// <summary>
    /// The names the functions around the code under way bind - their locals and parameters, a closure's - by how many of them bind
    /// each: the code's own, nearer than any declaration or import of its name, as a body's locals are (<see cref="Bodies"/>).
    /// </summary>
    private readonly Dictionary<string, int> _closure = new(StringComparer.Ordinal);

    /// <summary>
    /// For each scope the file puts declarations in, by name: the scope a function declared there finds the names its body
    /// reads in next after its own - a function's or the module's own scope, and a class's the one around the class, since
    /// a class encloses no function's body.
    /// </summary>
    private readonly Dictionary<QualifiedName, QualifiedName> _enclosing = [];

    /// <summary>Each class the file declares, by its key: its scope and the names it declares - what an attribute a method assigns is a field of.</summary>
    private readonly Dictionary<QualifiedName, ClassInfo> _classInfo = [];

    /// <summary>Each method that takes a receiver - <c>self</c>, or a class method's <c>cls</c> - by its key: the class an attribute its body assigns through it is a field of.</summary>
    private readonly Dictionary<QualifiedName, ClassInfo> _methods = [];

    /// <summary>
    /// The node whose code a node's references are read in, by the node's key, where it is another's than the node's own: a field an
    /// attribute a method assigns declares (<see cref="Attribute"/>), whose annotation is the method's code - its imports, the functions
    /// around it and its type parameters the ones it sees (<see cref="BindingOf"/>, <see cref="TypeParametersOf"/>).
    /// </summary>
    private readonly Dictionary<QualifiedName, QualifiedName> _codeOf = [];

    /// <summary>For each token, the logical line it is on, by its place among <see cref="_lineStarts"/> (<see cref="Lines"/>).</summary>
    private readonly int[] _lineOf;

    /// <summary>Where each logical line begins, by its first token, in order.</summary>
    private readonly List<int> _lineStarts;

    /// <summary>For each logical line, the first later one that begins no deeper - the line its block ends at, the next statement of its own block or a dedent - by its place; how many lines there are where none does.</summary>
    private readonly int[] _blockEnd;

    /// <summary>For each scanned text a run reads, the brackets that open a comprehension - each that holds a <c>for</c> of its own - and those <c>for</c>s, by their index; found once a text (<see cref="ComprehensionsOf"/>).</summary>
    private readonly Dictionary<ScannedText, Comprehensions> _comprehensions = new(ReferenceEqualityComparer.Instance);

    /// <summary>The names the lambdas and comprehensions the run under way is inside bind, by how many of them bind each: their own, as a body's locals are.</summary>
    private readonly Dictionary<string, int> _inner = new(StringComparer.Ordinal);

    /// <summary>Reads <paramref name="text"/>, the file <paramref name="path"/>.</summary>
    /// <param name="language">The implementation's name, which prefixes every id.</param>
    /// <param name="path">The file's path.</param>
    /// <param name="text">Its text.</param>
    public PythonReading(string language, string path, CodeText text)
    {
        _text = PythonLexer.Scan(text);
        _scan = _text.Scan;
        _prefix = $"{language}:";
        _path = path;
        var name = CodeGraphIds.FileName(path);
        _package = name is "__init__.py" or "__init__.pyi";
        _testFile = TestFile(name);
        (_lineOf, _lineStarts, _blockEnd) = Lines();
    }

    /// <summary>
    /// Whether a test runner collects the file named <paramref name="name"/> by its name: pytest's <c>test_*.py</c> and
    /// <c>*_test.py</c>, and unittest's and Django's <c>test*.py</c> - so <c>tests.py</c>, the module Django's <c>startapp</c>
    /// writes - and no other; a directory's name makes no file one.
    /// </summary>
    private static bool TestFile(string name)
        => name.EndsWith(".py", StringComparison.Ordinal) && name[..^3] is var stem && (stem.StartsWith("test", StringComparison.Ordinal) || stem.EndsWith("_test", StringComparison.Ordinal));

    /// <summary>A class the file declares: its key, its qualified name - its scope - and the names it declares, each once.</summary>
    private sealed record ClassInfo(QualifiedName Key, QualifiedName Qualified, HashSet<string> Names);

    /// <summary>
    /// A declaration's reading (<see cref="_bodies"/>): the scope of the function around it, or the module's, where its head runs
    /// (<paramref name="Outer"/>); its own scope where it is a function, whose locals and imports the functions it holds see
    /// (<paramref name="Own"/>), none for a class, whose names no function it holds sees; its key; what its head runs, read where it
    /// is defined; and its body, which answers the names it binds where it is a function's.
    /// </summary>
    private sealed record Body(QualifiedName Outer, QualifiedName? Own, QualifiedName Key, Action Head, Func<HashSet<string>?> Code);

    /// <summary>What a class is by its type, as unittest runs one: a <c>TestCase</c> subclass, one that may be one by a base another file declares, or neither.</summary>
    private enum Cases { None, Maybe, Case }

    /// <summary>One read the walk leaves to the run of the code it stands in (<see cref="_deferred"/>): what it reads, and the index past the tokens it reads, where the run goes on.</summary>
    private sealed record Deferred(int To, Action<Stack<CodeRun>, CodeRun> Read);

    /// <summary>The file's extract.</summary>
    public FileExtract Read()
    {
        if (_scan.Count > 0)
        {
            // The module is a namespace named by its dotted path, one open scope: every name it declares an importer may reach. It is
            // declared in its own scope, so its own code - what stands at its top outside its declarations - is looked up there.
            var dotted = Module();
            var module = QualifiedName.Of(dotted);
            var statements = Statements(0, _scan.Count, Column(0));
            var key = _extract.Add(new ExtractNode(Id(module), CodeNodeKinds.Namespace, Simple(dotted), module, 1, Math.Max(1, _text.Lines.Starts.Count), module.Whole())
            {
                SubKind = "module",
                Scope = module,
                Summary = ScanComments.Summary(_scan, 0) ?? Docstring(statements),
            });

            _extract.Scope(module, open: true, parent: null);
            _enclosing[module] = module;

            // A conftest.py's fixtures are given by pytest to every test under its directory, which no test imports: its module's
            // scope is an import of every file under it that serves the references a fixture qualifier makes alone.
            if (CodeGraphIds.FileName(_path) == "conftest.py")
            {
                _extract.Import(new ExtractImport(CodeImportForms.Directory, dotted, 1) { Serves = CodeQualifierKinds.Fixture });
            }

            Walk(statements, ScanFrame.Top.Inside(module, module, key, null));

            // Each body is read once every declaration of the file is made, as Python runs a body once the module is: a name it
            // reads that the file declares or imports below it is the file's, not a builtin's or a value's.
            Bodies();

            // What the top does outside its declarations is the module's own code; a name the module assigns is what it declares,
            // so a read of it there is a use of it, as anywhere else.
            var bound = Bind(0, _scan.Count);
            bound.ExceptWith(_bound);
            Calls(_text, 0, _scan.Count, key, bound, null);
        }

        MarkUnclosed();

        // A Python name is bound by the module's own names and its imports, or not at all: the repository's declarations are not
        // looked through for one bound by neither. What a * import of the module binds is said here, once every name is bound - what
        // its own * imports bind among it where no literal __all__ names it - and a package's __init__.py says its directory is a
        // unit of the layout whatever it holds, the commonest one, empty or of comments alone, among them.
        _extract.BoundWithin = true;
        _extract.Published = Published();
        _extract.PublishesTaken = !_allLiteral;
        _extract.FolderUnit = _package;
        _extract.Generated = ScanComments.Generated(_scan);
        return _extract.Build();
    }

    /// <summary>
    /// Marks dynamic, once each, every line a bracket that never closes opens on and every line a string that never closes begins
    /// on - text Python refuses, which a file being edited may hold: what follows it the reading takes as held in the bracket or as
    /// the string's text, so no statement it takes in is read as its own - each mark of the kind that tells the limit it shows
    /// (<see cref="Unclosed"/>).
    /// </summary>
    private void MarkUnclosed()
    {
        var marked = new HashSet<int>();

        for (var at = 0; at < _scan.Count; at++)
        {
            var bracket = _scan.Opens(at) && _scan.Match(at) < 0 && !_scan.IsStranded(at);

            if ((bracket || _scan.LeftOpen(at)) && marked.Add(_scan[at].Line))
            {
                _extract.Dynamic(_scan[at].Line, bracket ? "a bracket that never closes: what follows it is read as held in it" : "a string that never closes: what follows its quote is read as its text", Unclosed);
            }
        }
    }

    /// <summary>
    /// Reads each declaration's head and body (<see cref="_bodies"/>), in the order the text writes them, each before those it holds:
    /// the head where the declaration is defined - the function around it, whose names are bound while it is read - then the body,
    /// whose own locals and parameters, where it is a function's, are bound while the functions it holds are read (<see cref="_closure"/>),
    /// and the names its imports bind with them (<see cref="_enclosed"/>), as a closure sees them. The functions open are a stack: each
    /// pushed once, and popped once a declaration is reached that the function does not hold, so every name is bound and unbound once.
    /// </summary>
    private void Bodies()
    {
        var open = new Stack<(QualifiedName Scope, HashSet<string> Names, LocalBindings? Imports)>();

        void Close()
        {
            var (_, names, imports) = open.Pop();

            foreach (var name in names)
            {
                if (--_closure[name] == 0)
                {
                    _closure.Remove(name);
                }
            }

            foreach (var name in imports?.Names.Keys ?? Enumerable.Empty<string>())
            {
                var bound = _enclosed[name];
                bound.RemoveAt(bound.Count - 1);

                if (bound.Count == 0)
                {
                    _enclosed.Remove(name);
                }
            }
        }

        foreach (var body in _bodies)
        {
            while (open.TryPeek(out var top) && !top.Scope.Equals(body.Outer))
            {
                Close();
            }

            body.Head();

            if (body.Code() is { } names && body.Own is { } own)
            {
                var imports = _local.GetValueOrDefault(body.Key);
                open.Push((own, names, imports));

                foreach (var name in names)
                {
                    _closure[name] = _closure.GetValueOrDefault(name) + 1;
                }

                foreach (var (name, binding) in imports?.Names ?? [])
                {
                    (_enclosed.TryGetValue(name, out var bound) ? bound : _enclosed[name] = []).Add(binding);
                }
            }
        }

        while (open.Count > 0)
        {
            Close();
        }
    }

    // ---- Blocks ----

    /// <summary>
    /// The column the token at <paramref name="at"/> begins at: where it begins, less where its line's indentation is counted from
    /// (<see cref="Margin"/>).
    /// </summary>
    private int Column(int at)
    {
        Work++;
        return (int)(_scan[at].Start - Margin(_scan[at].Line));
    }

    /// <summary>
    /// Where the indentation of <paramref name="line"/> is counted from: past the last form feed among the blanks the line begins
    /// with, where Python's tokenizer counts the column from nothing again, else where the line begins - each line's found once.
    /// </summary>
    private long Margin(int line)
    {
        _margins ??= new long[_text.Lines.Count + 1];

        if (_margins[line] == 0)
        {
            var margin = _text.Lines.StartOf(line);

            for (var at = margin; at < _scan.Source.Length && _scan.Source[at] is ' ' or '\t' or '\f'; at++)
            {
                if (_scan.Source[at] == '\f')
                {
                    margin = at + 1;
                }
            }

            // Kept one past the margin, so a line not yet asked about is told by none.
            _margins[line] = margin + 1;
        }

        return _margins[line] - 1;
    }

    /// <summary>
    /// The statements of the block that begins at <paramref name="from"/>, each beginning at <paramref name="column"/>, in the order
    /// they are written: each from its first token to where the next begins or the block ends - the first token on a later line that
    /// begins no deeper than the block, a header's suite passed over whole - and none past <paramref name="to"/> or once a line
    /// dedents.
    /// </summary>
    private List<(int From, int To)> Statements(int from, int to, int column)
    {
        var statements = new List<(int From, int To)>();

        for (var at = from; at < to && at < _scan.Count && Column(at) == column && !Continued(_scan.Previous(at), at);)
        {
            var end = StatementEnd(at, to, column);
            statements.Add((at, end));
            at = end;
        }

        return statements;
    }

    /// <summary>
    /// The file's logical lines, found once: for each token the line it is on, where each line begins - the first token on a
    /// later line than the one before it, outside every bracketed group and not continued by a backslash - and for each line
    /// the first later one that begins no deeper, its block's end, found with a stack of the lines whose blocks are still
    /// open, each pushed and popped once.
    /// </summary>
    private (int[] LineOf, List<int> Starts, int[] BlockEnd) Lines()
    {
        var lineOf = new int[_scan.Count];
        var starts = new List<int>();

        for (var at = 0; at < _scan.Count;)
        {
            var next = _scan.Count;

            // Each token is tried before the group it opens is passed over, so a bracket that begins a line begins it - the line's
            // first token's own group among them.
            for (var index = _scan.Opens(at) ? _scan.Past(at) : _scan.Next(at); index < _scan.Count; index = _scan.Opens(index) ? _scan.Past(index) : _scan.Next(index))
            {
                if (_scan[index].Line > _scan[_scan.Previous(index)].EndLine && !Continued(_scan.Previous(index), index))
                {
                    next = index;
                    break;
                }
            }

            Array.Fill(lineOf, starts.Count, at, next - at);
            starts.Add(at);
            at = next;
        }

        var blockEnd = new int[starts.Count];
        var open = new Stack<int>();

        for (var line = 0; line < starts.Count; line++)
        {
            var column = Column(starts[line]);

            while (open.TryPeek(out var top) && Column(starts[top]) >= column)
            {
                blockEnd[open.Pop()] = line;
            }

            open.Push(line);
        }

        while (open.TryPop(out var left))
        {
            blockEnd[left] = starts.Count;
        }

        return (lineOf, starts, blockEnd);
    }

    /// <summary>
    /// Where the statement that begins at <paramref name="at"/>, in the block of <paramref name="column"/>, ends: the lines of
    /// decorators above a declaration are its own, the statement being the one they stand above; it ends where the next statement
    /// of the block begins or the block dedents (<see cref="NextLine"/>), a header's suite - deeper than the block - passed over
    /// whole, however deep it nests. Never past <paramref name="to"/>.
    /// </summary>
    private int StatementEnd(int at, int to, int column)
    {
        var start = at;

        while (_scan.Is(start, "@") && LineEnd(start, to) is var next && next > start && next < to && next < _scan.Count)
        {
            start = next;
        }

        return start >= to || start >= _scan.Count ? Math.Min(to, _scan.Count) : NextLine(start, to, column);
    }

    /// <summary>Where the logical line that holds the token at <paramref name="at"/> ends: the first token on a later line, every bracketed group passed over whole; <paramref name="to"/> where none stands.</summary>
    private int LineEnd(int at, int to) => NextLine(at, to, int.MaxValue);

    /// <summary>
    /// The first token past <paramref name="at"/> and before <paramref name="to"/> that begins a logical line of its own: it stands
    /// on a later line than the token before it, which a backslash does not continue, and it begins no deeper than
    /// <paramref name="column"/> - the next statement of a block or its dedent, where the column is the block's; any line, where
    /// the column is past every one. Every bracketed group is passed over whole, so a line a bracket holds is no line of its own
    /// wherever it begins, and the token a group lands on is tried like any other. <paramref name="to"/>, or the text's end, where
    /// none stands. Read from the file's logical lines (<see cref="Lines"/>): the block of a line deeper than the column is passed
    /// over whole at once, by where it ends - its own line's, where the column is that line's.
    /// </summary>
    private int NextLine(int at, int to, int column)
    {
        var limit = Math.Min(to, _scan.Count);

        if (at < 0 || at >= _scan.Count)
        {
            return limit;
        }

        var own = _lineOf[at];
        var line = _lineStarts[own] == at && Column(at) == column ? _blockEnd[own] : own + 1;

        while (line < _lineStarts.Count && Column(_lineStarts[line]) > column)
        {
            line = _blockEnd[line];
        }

        return line < _lineStarts.Count ? Math.Min(_lineStarts[line], limit) : limit;
    }

    /// <summary>
    /// Whether the line that holds the token at <paramref name="next"/> continues the one that holds the token at <paramref name="previous"/>:
    /// the two are on different lines, and each line break between them follows a backslash that ends its line's code - so a line
    /// whose break follows a comment, whose backslash is the comment's own, is continued by none, and neither is a blank line.
    /// </summary>
    private bool Continued(int previous, int next)
    {
        if (previous < 0 || next <= previous || _scan[next].Line == _scan[previous].Line)
        {
            return false;
        }

        var start = _scan[previous].End;
        var stop = _scan[next].Start;

        // A comment between them runs to the end of its line, whose break no backslash of the code goes before.
        if (_scan.HoldsComment(start, stop))
        {
            return false;
        }

        var joined = false;

        for (var at = start; at < stop; at++)
        {
            if (ScanLexing.LineBreakAt(_scan.Source, at, stop) is var width and > 0)
            {
                if (at == start || _scan.Source[at - 1] != '\\')
                {
                    return false;
                }

                joined = true;
                at += width - 1;
            }
        }

        return joined;
    }

    /// <summary>
    /// The suite of the header whose colon is at <paramref name="colon"/> and whose statement begins at <paramref name="column"/>: the
    /// simple statements written on the header's own line, or the block of lines indented past it - from its first token to where
    /// it dedents or to <paramref name="to"/>. Its statements, and where its tokens end; none where the suite is left empty.
    /// </summary>
    private (List<(int From, int To)> Statements, int To) Suite(int colon, int to, int column)
    {
        var first = _scan.Next(colon);

        if (first >= to || first >= _scan.Count)
        {
            return ([], Math.Min(to, _scan.Count));
        }

        if (_scan[first].Line == _scan[colon].Line)
        {
            var end = LineEnd(first, to);
            return (Simple(first, end), end);
        }

        if (Column(first) <= column)
        {
            return ([], first);
        }

        var statements = Statements(first, to, Column(first));

        return (statements, statements.Count > 0 ? statements[^1].To : first);
    }

    /// <summary>The simple statements from <paramref name="from"/> to <paramref name="to"/>, split at the semicolons between them.</summary>
    private List<(int From, int To)> Simple(int from, int to)
    {
        var statements = new List<(int From, int To)>();

        for (var at = from; at < to && at < _scan.Count;)
        {
            var end = at;

            while (end < to && end < _scan.Count && !_scan.Is(end, ";"))
            {
                Work++;
                end = _scan.Opens(end) ? Math.Min(_scan.Past(end), to) : _scan.Next(end);
            }

            statements.Add((at, Math.Min(end, to)));

            if (end >= to || end >= _scan.Count)
            {
                break;
            }

            at = _scan.Next(end);
        }

        return statements;
    }

    /// <summary>Reads <paramref name="statements"/>, each in <paramref name="frame"/>: one at a time, in the order they are written.</summary>
    private void Walk(List<(int From, int To)> statements, ScanFrame frame)
        => _walks.Walk(0, statements.Count, index =>
        {
            var (from, to) = statements[index];
            Statement(from, to, frame);
            return index + 1;
        });

    // ---- Statements ----

    /// <summary>The statement from <paramref name="at"/> to <paramref name="to"/>, read in <paramref name="frame"/>: the decorators above it, then what it declares or the code it holds.</summary>
    private void Statement(int at, int to, ScanFrame frame)
    {
        var decorators = new List<(int From, int To)>();

        // Each decorator: the expression to its line's end; the declaration it decorates follows on a line of its own.
        while (_scan.Is(at, "@") && _scan.Next(at) is var from && LineEnd(from, to) is var end && end > from)
        {
            decorators.Add((from, end));
            at = end;
        }

        // A text that ends in a decorator: no declaration follows it.
        if (at >= _scan.Count)
        {
            return;
        }

        // async for, async with and match: a compound statement told by more than its first word.
        if (CompoundAt(at, to) is { } compound)
        {
            Header(at, compound.Keyword, compound.Colon, to, frame);
            return;
        }

        if (Is(at, "def") || (Is(at, "async") && Is(_scan.Next(at), "def")))
        {
            Function(at, to, frame, decorators);
        }
        else if (Is(at, "class"))
        {
            Class(at, to, frame, decorators);
        }
        else if (Compound.Contains(Text(at)) && HeaderColon(at, to) is var colon && colon >= 0)
        {
            Header(at, colon, to, frame);
        }
        else
        {
            foreach (var (start, end) in Simple(at, Math.Min(to, _scan.Count)))
            {
                SimpleStatement(start, end, frame);
            }
        }
    }

    /// <summary>The colon that ends the header from <paramref name="at"/>, before <paramref name="to"/>, at the header's own depth; -1 where none does.</summary>
    private int HeaderColon(int at, int to)
        => ScanCalls.TopLevel(_scan, at, to, ":", null) is { } colon && colon < to && colon < _scan.Count ? colon : -1;

    /// <summary>
    /// The compound statement the statement at <paramref name="at"/>, ending at <paramref name="to"/>, is where its first word alone
    /// does not tell it: <c>async for</c> and <c>async with</c>, whose keyword is the word after <c>async</c>; and <c>match</c>, a
    /// keyword only where it heads a header whose colon ends its line and whose suite's first clause is a <c>case</c> - so
    /// <c>match = ...</c>, <c>match: str</c> and <c>match(x)</c> are what they read as. Its keyword and its colon;
    /// <see langword="null"/> where it is none of them.
    /// </summary>
    private (int Keyword, int Colon)? CompoundAt(int at, int to)
    {
        if (Is(at, "async") && (Is(_scan.Next(at), "for") || Is(_scan.Next(at), "with")) && HeaderColon(_scan.Next(at), to) is var colon && colon >= 0)
        {
            return (_scan.Next(at), colon);
        }

        if (Is(at, "match") && HeaderColon(_scan.Next(at), to) is var header && header > _scan.Next(at) && _scan.Next(header) is var first
            && first < to && first < _scan.Count && _scan[first].Line > _scan[header].EndLine && Column(first) > Column(at) && Is(first, "case"))
        {
            return (at, header);
        }

        return null;
    }

    /// <summary>A compound statement's header whose keyword is its first word (<see cref="Header(int, int, int, int, ScanFrame)"/>).</summary>
    private void Header(int at, int colon, int to, ScanFrame frame) => Header(at, at, colon, to, frame);

    /// <summary>
    /// A compound statement's header, from its first word at <paramref name="at"/> - its keyword at <paramref name="keyword"/>, the
    /// word after <c>async</c> where one is written - to its colon at <paramref name="colon"/>: what the header keeps to itself - a
    /// <c>for</c>'s targets to the <c>in</c>, the names <c>as</c> gives, a soft keyword itself - marked so the body's run reads the
    /// rest as its code, a <c>case</c>'s pattern read for what it names and binds (<see cref="Pattern"/>), an <c>except</c>'s types
    /// named (<see cref="TypeNames"/>); then its suite read as statements of the same frame - a <c>match</c>'s, its clauses.
    /// </summary>
    private void Header(int at, int keyword, int colon, int to, ScanFrame frame)
    {
        _headerColons.Add(colon);
        var word = Text(keyword);

        if (word == "match")
        {
            Match(at, colon, to, frame);
            return;
        }

        if (word == "for")
        {
            var end = ScanCalls.TopLevel(_scan, _scan.Next(keyword), colon, "in", null);
            Keep(_scan.Next(keyword), end < colon ? end : colon);
        }
        else if (word == "case" && frame.Parent is not null)
        {
            // case is a keyword here alone: kept; its pattern to its guard read for what it names and what it binds, the guard the
            // body's code.
            var end = ScanCalls.TopLevel(_scan, _scan.Next(keyword), colon, "if", null);
            Keep(keyword, _scan.Next(keyword));
            Pattern(_scan.Next(keyword), end < colon ? end : colon, frame);
        }
        else if (word == "except" && frame.Parent is { } catcher)
        {
            // What the clause catches - a type, or a tuple of them - is named for the node whose code the block is, where the code
            // runs; the language's own exceptions, ValueError among them, name nothing of the repository's (<see cref="TypeNames"/>).
            // Written as anything but types - a call that answers them - it is the body's code.
            var from = _scan.Next(keyword);
            var end = ScanCalls.TopLevel(_scan, from, colon, "as", null) is var asWord && asWord < colon ? asWord : colon;

            if (IsTypeList(_scan, from, end))
            {
                Defer(from, end, frame, () => TypeNames(_scan, from, end, catcher, null));
                Keep(from, end);
            }
        }

        for (var index = keyword; index < colon && index < _scan.Count; index = _scan.Opens(index) ? Math.Min(_scan.Past(index), colon) : _scan.Next(index))
        {
            // with ... as name, except ... as name: the name is bound, no value the code reads.
            if (Is(index, "as") && _scan.IsName(_scan.Next(index)))
            {
                Keep(_scan.Next(index), _scan.Next(_scan.Next(index)));
            }
        }

        Walk(Suite(colon, to, Column(at)).Statements, frame);
    }

    /// <summary>
    /// A <c>match</c> statement from <paramref name="at"/>, its header's colon at <paramref name="colon"/>: <c>match</c> kept, a keyword
    /// here alone, its subject the body's code; each clause of its suite a <c>case</c> header (<see cref="Header(int, int, int, int, ScanFrame)"/>)
    /// read in its turn, in a walk of the reader's stack.
    /// </summary>
    private void Match(int at, int colon, int to, ScanFrame frame)
    {
        Keep(at, _scan.Next(at));
        var clauses = Suite(colon, to, Column(at)).Statements;

        _walks.Walk(0, clauses.Count, index =>
        {
            var (from, end) = clauses[index];

            if (Is(from, "case") && HeaderColon(_scan.Next(from), end) is var clause && clause > _scan.Next(from))
            {
                Header(from, from, clause, end, frame);
            }
            else
            {
                Statement(from, end, frame);
            }

            return index + 1;
        });
    }

    /// <summary>
    /// A <c>case</c>'s pattern from <paramref name="from"/> to <paramref name="to"/>, read as Python reads one for the node whose code
    /// <paramref name="frame"/>'s block is: a class pattern's class - a name, or a dotted one, before <c>(</c> - a reference to it,
    /// never a call, read where the code runs (<see cref="Defer"/>); a value pattern - a dotted name - the body's code, a use of what it
    /// names; a literal the body's code, a string's value kept; a keyword pattern's attribute name no value read; and a capture - a name
    /// alone, one after <c>as</c>, <c>*</c> or <c>**</c> - bound by the body, as a <c>for</c>'s target is (<see cref="_captures"/>).
    /// </summary>
    private void Pattern(int from, int to, ScanFrame frame)
    {
        var key = frame.Parent!;

        for (var at = from; at < to && at < _scan.Count;)
        {
            if (!_scan.IsName(at) || _scan.Is(_scan.Previous(at), "."))
            {
                at = _scan.Next(at);
                continue;
            }

            var chain = Chain(_scan, at);
            var next = _scan.Next(chain[^1]);
            var word = Text(at);

            if (_scan.Is(next, "("))
            {
                // Point(x=0, y=yy): the class it matches against named, its own parentheses read on for what they bind.
                var named = at;
                Defer(named, next, frame, () => TypeNames(_scan, named, next, key, null));
                Keep(at, next);
                at = next;
                continue;
            }

            if (chain.Count > 1 || Words.Contains(word) || word is "None" or "True" or "False")
            {
                // Status.PAID, a word, a literal of the language's: the body's code.
                at = next;
                continue;
            }

            Keep(at, next);

            // A keyword pattern's attribute - x in Point(x=0) - binds nothing; any other name alone is a capture.
            if (!_scan.Is(next, "="))
            {
                _captures.Add(_scan[at].Start);
            }

            at = next;
        }
    }

    /// <summary>
    /// A simple statement from <paramref name="at"/> to <paramref name="to"/>, the semicolons between them already parted: an import, a
    /// declaration the graph keeps, or code the body's run reads.
    /// </summary>
    private void SimpleStatement(int at, int to, ScanFrame frame)
    {
        if (Is(at, "import") || Is(at, "from"))
        {
            Import(at, to, frame);
            return;
        }

        if (Is(at, "global") || Is(at, "nonlocal"))
        {
            Keep(at, to);
            return;
        }

        // The module's __all__ taken away - del __all__ - or changed after it was assigned - __all__ += [...]: no list of literals
        // names what a * import of it binds. What a del takes away - a name, an item a key names - is the body's code, read as any is.
        if (Is(at, "del"))
        {
            for (var index = _scan.Next(at); index < to && index < _scan.Count && !_scan.Is(index, ";"); index = _scan.Next(index))
            {
                if (Is(index, "__all__"))
                {
                    ChangedAll(_scan[at].Line);
                }
            }

            Keep(at, _scan.Next(at));
            return;
        }

        if (Is(at, "__all__") && _scan.Next(at) is var after && after < _scan.Count && after < to && _scan[after].Kind == ScanTokenKind.Punctuation && Augments.Contains(Text(after)))
        {
            ChangedAll(_scan[at].Line);
        }

        if (TypeAlias(at, to, frame))
        {
            return;
        }

        // a = b = value, a, *b = value, self.x = value: each target list before an = at the statement's top is what it assigns,
        // the last one's value the statement's.
        var attributes = frame.Parent is { } holder && _methods.ContainsKey(holder);
        var groups = new List<Targets>();

        for (var target = at; target < to && TargetsAt(target, to, attributes) is { } targets; target = targets.Value)
        {
            groups.Add(targets);

            if (targets.Value < 0)
            {
                break;
            }
        }

        if (groups.Count > 0)
        {
            Assign(at, groups, to, frame);
        }
    }

    /// <summary>The annotation and the assignment of the name at <paramref name="at"/>, where its line annotates it or assigns it before <paramref name="to"/>; <see langword="null"/> where it does neither.</summary>
    private Assigned? Assignment(int at, int to)
    {
        var next = _scan.Next(at);

        if (_scan.Is(next, "="))
        {
            return new Assigned(-1, next, _scan.Next(next));
        }

        if (!_scan.Is(next, ":"))
        {
            return null;
        }

        // The annotation ends at the = of the assignment, where one stands; else at the statement's end, and no value is given.
        var equal = ScanCalls.TopLevel(_scan, _scan.Next(next), to, "=", null);

        return equal < to ? new Assigned(equal, equal, _scan.Next(equal)) : new Assigned(to, -1, -1);
    }

    /// <summary>An assignment of the name at its head: where its annotation ends, its <c>=</c> where one is written, and where its value begins; -1 where a member is none.</summary>
    private sealed record Assigned(int AnnotationEnd, int Assign, int Value);

    /// <summary>One name a target list writes, by its index: a name, or the attribute a method's receiver reaches (<c>x</c> of <c>self.x</c>), and whether it is starred.</summary>
    private sealed record Target(int At, bool Attribute, bool Starred);

    /// <summary>One target list of an assignment: the names it writes, where its annotation ends, its <c>=</c>, and where its value begins; -1 where a member is none.</summary>
    private sealed record Targets(IReadOnlyList<Target> Names, int AnnotationEnd, int Assign, int Value);

    /// <summary>
    /// The targets the assignment at <paramref name="at"/> writes before an <c>=</c> at the statement's top: one name, with its
    /// annotation where one is written (<c>x: int = 1</c>, <c>x: int</c>); or names joined by commas, each maybe starred, the list
    /// maybe in parentheses or brackets (<c>a, *b = ...</c>, <c>(a, b) = ...</c>); and, where <paramref name="attributes"/> says the
    /// statement is a method's, an attribute its receiver reaches among them (<c>self.x = ...</c>, <c>cls.x: int = 0</c>).
    /// <see langword="null"/> where the tokens are no targets: an expression, a call, a subscript, an attribute of another value.
    /// </summary>
    private Targets? TargetsAt(int at, int to, bool attributes)
    {
        if (_scan.IsName(at) && !Words.Contains(Text(at)) && Assignment(at, to) is { } single)
        {
            return new Targets([new Target(at, false, false)], single.AnnotationEnd, single.Assign, single.Value);
        }

        if (attributes && AttributeAt(at) is var name and >= 0 && Assignment(name, to) is { } attribute)
        {
            return new Targets([new Target(name, true, false)], attribute.AnnotationEnd, attribute.Assign, attribute.Value);
        }

        var close = _scan.Is(at, "(") || _scan.Is(at, "[") ? _scan.Match(at) : -1;
        var cursor = close > at ? _scan.Next(at) : at;
        var stop = close > at ? close : Math.Min(to, _scan.Count);
        var names = new List<Target>();
        var commas = 0;

        while (cursor < stop && !_scan.Is(cursor, "="))
        {
            var starred = _scan.Is(cursor, "*");
            cursor = starred ? _scan.Next(cursor) : cursor;

            if (_scan.IsName(cursor) && !Words.Contains(Text(cursor)) && !_scan.Is(_scan.Next(cursor), "."))
            {
                names.Add(new Target(cursor, false, starred));
                cursor = _scan.Next(cursor);
            }
            else if (attributes && AttributeAt(cursor) is var reached and >= 0)
            {
                names.Add(new Target(reached, true, starred));
                cursor = _scan.Next(reached);
            }
            else
            {
                return null;
            }

            if (cursor >= stop || _scan.Is(cursor, "="))
            {
                break;
            }

            if (!_scan.Is(cursor, ","))
            {
                return null;
            }

            commas++;
            cursor = _scan.Next(cursor);
        }

        var equal = close > at ? _scan.Next(close) : cursor;

        return names.Count > 0 && (close > at || commas > 0) && equal < to && _scan.Is(equal, "=")
            ? new Targets(names, -1, equal, _scan.Next(equal))
            : null;
    }

    /// <summary>The index of the attribute <c>x</c> where the tokens at <paramref name="at"/> are a method's receiver reaching it alone - <c>self.x</c>, <c>cls.x</c>, no member of it after; -1 where they are not.</summary>
    private int AttributeAt(int at)
        => (Is(at, "self") || Is(at, "cls")) && _scan.Is(_scan.Next(at), ".") && _scan.Next(_scan.Next(at)) is var name && _scan.IsName(name) && !_scan.Is(_scan.Next(name), ".") ? name : -1;

    /// <summary>
    /// The target lists <paramref name="groups"/> of the statement at <paramref name="at"/> assign, the last one's value the
    /// statement's: at the file's top or in a class each name a constant where it is written in capitals and a field otherwise
    /// (<see cref="AssignName"/>), the value the code of the name it is given to - each element of a tuple the name's in its place
    /// where the two are as long and none is starred, else the whole value the last name's; inside a function each name a local, its
    /// annotation's types the function's references and its value the function's code, and an attribute a method assigns through its
    /// receiver a field of its class where the class declares it nowhere else (<see cref="Attribute"/>), a write of the field
    /// otherwise, which the method's code reads as a use.
    /// </summary>
    private void Assign(int at, List<Targets> groups, int to, ScanFrame frame)
    {
        var last = groups[^1];
        var value = last.Value;
        var end = value < 0 ? to : LineEnd(last.Assign, to);
        var local = frame.Owner is null && frame.Depth > 1;
        var owner = frame.Parent is { } holder && _methods.TryGetValue(holder, out var method) ? method : null;
        var keys = new List<QualifiedName>();

        // Each name's signature is its target list's, from where the list begins - (a, b) = of each of a and b.
        var from = at;

        foreach (var group in groups)
        {
            keys.Clear();
            var signatureEnd = group.Value < 0 ? end : group.Value;

            foreach (var target in group.Names)
            {
                if (target.Attribute)
                {
                    if (owner is not null && Attribute(target.At, from, signatureEnd, end, owner) is { } field)
                    {
                        keys.Add(field);
                        _codeOf[field] = frame.Parent!;
                    }
                }
                else if (local)
                {
                    Keep(target.At, _scan.Next(target.At));
                }
                else
                {
                    keys.Add(AssignName(target.At, from, signatureEnd, value, end, frame));
                }
            }

            if (group.AnnotationEnd >= 0)
            {
                // name: T = value - the annotation's types named by the node it declares, else by the node whose code the line is,
                // where the code runs.
                var annotation = _scan.Next(_scan.Next(group.Names[0].At));
                var annotated = keys.Count > 0 ? keys[0] : frame.Parent!;
                var annotationEnd = group.AnnotationEnd;
                Defer(annotation, annotationEnd, frame, () => TypeNames(_scan, annotation, annotationEnd, annotated, null));
                Keep(_scan.Next(group.Names[0].At), group.AnnotationEnd);
            }

            from = group.Value;
        }

        // Inside a function the value is the function's code, read by its run; an annotation alone gives no value.
        if (local)
        {
            return;
        }

        Keep(at, value < 0 ? to : value);

        if (value < 0)
        {
            return;
        }

        var pieces = Pieces(value, end);
        var given = keys.ToList();
        var apart = given.Count > 1 && given.Count == pieces.Count && !last.Names.Any(target => target.Starred);

        // The module's own code is read now, as Python runs it; a class's with the class's (<see cref="Body"/>), where the functions
        // around the class are open, by the run of the class's code as it reaches the value - a name the class declares there the
        // class's own, as anywhere in that code - each element of a tuple the name's in its place, the first read first.
        if (frame.Depth == 1)
        {
            for (var index = 0; index < (apart ? given.Count : 1); index++)
            {
                var (start, stop) = apart ? pieces[index] : (value, end);
                Calls(_text, start, stop, apart ? given[index] : given[^1], Bind(start, stop), frame.Owner);
            }

            Keep(value, end);
            return;
        }

        _deferred[_scan[value].Start] = new Deferred(end, (runs, run) =>
        {
            for (var index = apart ? given.Count - 1 : 0; index >= 0; index--)
            {
                var (start, stop) = apart ? pieces[index] : (value, end);
                runs.Push(new CodeRun(_text, start, stop, apart ? given[index] : given[^1], Bind(start, stop)) { Own = run.Own });
            }
        });
    }

    /// <summary>The elements of the value from <paramref name="value"/> to <paramref name="end"/>, split at its top's commas - a tuple's, a parenthesized or bracketed one's inside its brackets.</summary>
    private List<(int Start, int End)> Pieces(int value, int end)
    {
        var pieces = ScanCalls.Split(_scan, value, end, null);

        return pieces.Count == 1 && (_scan.Is(value, "(") || _scan.Is(value, "[")) && _scan.Match(value) is var close and > 0 && _scan.Next(close) >= end
            ? ScanCalls.Split(_scan, _scan.Next(value), close, null)
            : pieces;
    }

    /// <summary>Whether <paramref name="name"/> is written in capitals: a letter at least, and nothing but capitals, digits and underscores.</summary>
    private static bool Capitals(string name)
        => name.Any(char.IsAsciiLetter) && name.All(character => char.IsAsciiLetter(character) && char.IsUpper(character) || char.IsAsciiDigit(character) || character == '_');

    /// <summary>
    /// The name at <paramref name="at"/> assigned at the file's top or in a class, its signature its target list's, from
    /// <paramref name="signatureStart"/> to <paramref name="signatureEnd"/>, the statement's value from <paramref name="value"/> to <paramref name="end"/>: a constant where its name is written in capitals
    /// and a field otherwise, the module's or the class's name. Answers its key.
    /// </summary>
    private QualifiedName AssignName(int at, int signatureStart, int signatureEnd, int value, int end, ScanFrame frame)
    {
        var name = Text(at);
        var qualified = frame.Qualify(name);

        if (frame.Depth == 1)
        {
            Top(name);
        }

        if (frame.Parent is { } holder && _classInfo.TryGetValue(holder, out var info))
        {
            info.Names.Add(name);
        }

        var key = _extract.Add(new ExtractNode(Id(qualified), CodeNodeKinds.Member, name, qualified, _scan[at].Line, _scan[_scan.LastOf(end)].EndLine, _scan.Signature(signatureStart, signatureEnd))
        {
            SubKind = Capitals(name) ? "constant" : "field",
            Scope = frame.Scope,
            Parent = frame.Parent,
            Summary = ScanComments.Summary(_scan, at),
        });

        // The module's __all__ is read as the names a * import of it binds where it is a list of string literals alone: the names
        // are what the module publishes (<see cref="FileExtract.Published"/>), the literals this assignment places in the node being
        // its entries. Annotated without a value, or given anything else, it is marked (<see cref="ComputedExports"/>), and the module's
        // public names are published for them.
        if (name == "__all__" && frame.Depth == 1)
        {
            _allLiteral = value >= 0 && IsLiteralList(value, end);

            if (_allLiteral)
            {
                for (var entry = value; entry < end && entry < _scan.Count; entry = _scan.Next(entry))
                {
                    _all.AddRange(_scan[entry].Kind == ScanTokenKind.Text ? _scan.LiteralParts(_scan[entry]).Select(part => part.Value) : []);
                }
            }
            else
            {
                ChangedAll(_scan[at].Line);
            }
        }

        return key;
    }

    /// <summary>
    /// The attribute at <paramref name="at"/> a method assigns through its receiver - <c>self.x = ...</c>, <c>cls.x: int = 0</c> - its
    /// signature its target list's, from <paramref name="signatureStart"/> to <paramref name="signatureEnd"/>, and its statement to
    /// <paramref name="end"/>, in a method of <paramref name="owner"/>:
    /// the first assignment of a name the class declares nowhere else - in its body, by a method, by an earlier assignment of a method -
    /// is a field of the class, a constant where its name is written in capitals, over the statement's lines and held by the class, as a
    /// name its body assigns is; the receiver and the name kept to it. Any later one is a write of that field, which the method's code
    /// reads as a use of it, as a name the language keeps to itself - <c>__dict__</c> - is no field. Answers the field's key where it
    /// declares one.
    /// </summary>
    private QualifiedName? Attribute(int at, int signatureStart, int signatureEnd, int end, ClassInfo owner)
    {
        var name = Text(at);
        var receiver = _scan.Previous(_scan.Previous(at));

        if ((name.StartsWith("__", StringComparison.Ordinal) && name.EndsWith("__", StringComparison.Ordinal)) || !owner.Names.Add(name))
        {
            return null;
        }

        var qualified = ExtractText.Qualify(owner.Qualified, name);
        var key = _extract.Add(new ExtractNode(Id(qualified), CodeNodeKinds.Member, name, qualified, _scan[receiver].Line, _scan[_scan.LastOf(end)].EndLine, _scan.Signature(signatureStart, signatureEnd))
        {
            SubKind = Capitals(name) ? "constant" : "field",
            Scope = owner.Qualified,
            Parent = owner.Key,
            Summary = ScanComments.Summary(_scan, receiver),
        });

        Keep(receiver, _scan.Next(at));
        return key;
    }

    /// <summary>
    /// A type alias at <paramref name="at"/> - <c>type Name[params] = value</c>, Python 3.12's - where the statement is one: at the
    /// file's top or in a class a type of sub-kind <c>alias</c> over its line, the types its value names its references; inside a
    /// function a local, its value's types the function's. Its type parameters are its own: names no reference is made of, their
    /// bounds' types named. Whether it was one.
    /// </summary>
    private bool TypeAlias(int at, int to, ScanFrame frame)
    {
        var nameAt = _scan.Next(at);

        if (!Is(at, "type") || !_scan.IsName(nameAt))
        {
            return false;
        }

        var types = TypeParameters(nameAt);
        var equal = types.Close >= 0 ? _scan.Next(types.Close) : _scan.Next(nameAt);

        if (!_scan.Is(equal, "=") || equal >= to)
        {
            return false;
        }

        var value = _scan.Next(equal);
        var end = LineEnd(equal, to);
        var name = Text(nameAt);

        if (frame.Owner is null && frame.Depth > 1)
        {
            // A function's own alias: its parameters its own, their bounds and its value read by the function's run where it stands.
            var function = frame.Parent!;
            var own = TypeParameterList(types);
            Defer(at, end, frame, () =>
            {
                TypeParameterBounds(types, function, own);
                TypeNames(_scan, value, end, function, own);
            });
            Keep(at, end);
            return true;
        }

        var qualified = frame.Qualify(name);
        var key = _extract.Add(new ExtractNode(Id(qualified), CodeNodeKinds.Type, name, qualified, _scan[at].Line, _scan[_scan.LastOf(end)].EndLine, _scan.Signature(at, value))
        {
            SubKind = "alias",
            Scope = frame.Scope,
            Parent = frame.Parent,
            Summary = ScanComments.Summary(_scan, at),
        });

        _classes.Add(name);

        if (frame.Depth == 1)
        {
            Top(name);
            _free.Add(name);
        }

        if (frame.Parent is { } holder && _classInfo.TryGetValue(holder, out var info))
        {
            info.Names.Add(name);
        }

        Own(key, types);
        Defer(at, end, frame, () =>
        {
            TypeParameterBounds(types, key, null);
            TypeNames(_scan, value, end, key, null);
        });
        Keep(at, end);
        return true;
    }

    /// <summary>
    /// The type-parameter list written after the name at <paramref name="nameAt"/> - <c>def f[T](...)</c>, <c>class C[T: Base]</c>,
    /// <c>type A[T] = ...</c>, Python 3.12's: its brackets; (-1, -1) where none is written.
    /// </summary>
    private (int Open, int Close) TypeParameters(int nameAt)
        => _scan.IsName(nameAt) && _scan.Is(_scan.Next(nameAt), "[") && _scan.Match(_scan.Next(nameAt)) is var close and > 0 ? (_scan.Next(nameAt), close) : (-1, -1);

    /// <summary>
    /// The names the type-parameter list <paramref name="types"/> declares - <c>T</c>, <c>*Ts</c>, <c>**P</c> - which the code of its
    /// declaration and of every declaration it holds names as its own and no type of the repository's; the list kept to the
    /// declaration. <see langword="null"/> where there is no list.
    /// </summary>
    private HashSet<string>? TypeParameterList((int Open, int Close) types)
    {
        if (types.Open < 0)
        {
            return null;
        }

        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (from, _) in ScanCalls.Split(_scan, _scan.Next(types.Open), types.Close, null))
        {
            if (TypeParameterAt(from) is var at && _scan.IsName(at))
            {
                names.Add(Text(at));
            }
        }

        Keep(types.Open, _scan.Next(types.Close));
        return names;
    }

    /// <summary>The name a type parameter written from <paramref name="from"/> declares, past its stars.</summary>
    private int TypeParameterAt(int from)
    {
        var at = from;

        while (_scan.Is(at, "*") || _scan.Is(at, "**"))
        {
            at = _scan.Next(at);
        }

        return at;
    }

    /// <summary>Says the declaration of <paramref name="key"/> declares the type parameters <paramref name="types"/> writes, its own and its code's (<see cref="TypeParametersOf"/>).</summary>
    private void Own(QualifiedName key, (int Open, int Close) types)
    {
        if (TypeParameterList(types) is { } names)
        {
            _typeParameters[key] = names;
        }
    }

    /// <summary>
    /// Each bound, constraint and default the type-parameter list <paramref name="types"/> writes: the types it names, references of
    /// the node of <paramref name="key"/> (<see cref="TypeNames"/>), a parameter of the list naming none - those <paramref name="own"/>
    /// holds besides, of a declaration that is no node.
    /// </summary>
    private void TypeParameterBounds((int Open, int Close) types, QualifiedName key, HashSet<string>? own)
    {
        if (types.Open < 0)
        {
            return;
        }

        foreach (var (from, end) in ScanCalls.Split(_scan, _scan.Next(types.Open), types.Close, null))
        {
            var at = TypeParameterAt(from);

            if (_scan.IsName(at) && (_scan.Is(_scan.Next(at), ":") || _scan.Is(_scan.Next(at), "=")) && _scan.Next(_scan.Next(at)) < end)
            {
                TypeNames(_scan, _scan.Next(_scan.Next(at)), end, key, own);
            }
        }
    }

    /// <summary>
    /// The type parameters the code of the node of <paramref name="key"/> sees: its own and those of every declaration around it - a
    /// method's class's, a nested function's function's - as Python's annotation scopes give them to all a declaration holds. Found
    /// once a node, each from the one around it: a node with none of its own shares its holder's.
    /// </summary>
    private ImmutableHashSet<string> TypeParametersOf(QualifiedName key)
    {
        if (_typeParameters.Count == 0)
        {
            return [];
        }

        var path = new List<QualifiedName>();
        var seen = ImmutableHashSet.Create<string>(StringComparer.Ordinal);

        for (QualifiedName? at = _codeOf.GetValueOrDefault(key, key); at is not null; at = _extract.Node(at).Parent)
        {
            if (_typeScopes.TryGetValue(at, out var known))
            {
                seen = known;
                break;
            }

            path.Add(at);
        }

        for (var index = path.Count - 1; index >= 0; index--)
        {
            seen = _typeScopes[path[index]] = _typeParameters.TryGetValue(path[index], out var own) ? seen.Union(own) : seen;
        }

        return seen;
    }

    /// <summary>
    /// Reads what a statement of <paramref name="frame"/> writes from <paramref name="from"/> to <paramref name="to"/> by
    /// <paramref name="read"/> where the code runs it: at the module's top now, as Python runs the module in the order the text writes
    /// it; in a declaration, by the run of the declaration's code when it reaches <paramref name="from"/> (<see cref="_deferred"/>), once
    /// every name of the file is bound and the functions around it are open, so a name imported or declared below the statement - at the
    /// module's top, in a function around it - is the one it reads.
    /// </summary>
    private void Defer(int from, int to, ScanFrame frame, Action read)
    {
        if (frame.Depth == 1)
        {
            read();
            return;
        }

        _deferred[_scan[from].Start] = new Deferred(to, (_, _) => read());
    }

    /// <summary>Marks the tokens from <paramref name="from"/> to <paramref name="to"/>: each kept to the reading that declares it, read by no run of the body it stands in.</summary>
    private void Keep(int from, int to)
    {
        for (var at = Math.Max(from, 0); at < to && at < _scan.Count; at++)
        {
            _kept.Add(_scan[at].Start);
        }
    }

    /// <summary>Says the module's <c>__all__</c> is not a list of string literals the code leaves alone: marked dynamic, and the module's public names published instead (<see cref="Published"/>).</summary>
    private void ChangedAll(int line)
    {
        _allLiteral = false;
        _all.Clear();
        _extract.Dynamic(line, "a module's __all__ the code computes or changes", ComputedExports);
    }

    /// <summary>
    /// The names a <c>*</c> import of the module binds: <c>__all__</c>'s entries where it is a list of string literals the code leaves
    /// alone (<see cref="ComputedExports"/>); else every name bound at the module's top that is not written with a
    /// leading underscore - what it declares and what it imports alike - in the order the file first binds it (<see cref="Top"/>).
    /// </summary>
    private IReadOnlyList<string> Published()
        => _allLiteral
            ? [.. _all]
            : [.. _top.Where(name => !name.StartsWith('_')).Distinct(StringComparer.Ordinal)];

    /// <summary>Says the module's top binds <paramref name="name"/>, once.</summary>
    private void Top(string name)
    {
        if (_bound.Add(name))
        {
            _top.Add(name);
        }
    }

    /// <summary>
    /// Whether the tokens from <paramref name="from"/> to <paramref name="to"/> are a list or a tuple of string literals alone: string
    /// literals one after another with a comma between them and a trailing comma allowed - nothing else, no name, no call, no
    /// concatenation - in a bracket or in parentheses that are the whole of the range, or bare with a comma among them, as Python
    /// writes a tuple; one string in parentheses or alone is a string and no tuple.
    /// </summary>
    private bool IsLiteralList(int from, int to)
    {
        var end = Math.Min(to, _scan.Count);
        var grouped = _scan.Is(from, "[") || _scan.Is(from, "(");
        var close = grouped ? _scan.Match(from) : end;

        if (grouped && (close < 0 || _scan.Next(close) != end))
        {
            return false;
        }

        var strings = 0;
        var commas = 0;

        for (var at = grouped ? _scan.Next(from) : from; at < close;)
        {
            if (_scan[at].Kind != ScanTokenKind.Text)
            {
                return false;
            }

            strings++;
            at = _scan.Next(at);

            if (at >= close)
            {
                break;
            }

            if (!_scan.Is(at, ","))
            {
                return false;
            }

            commas++;
            at = _scan.Next(at);
        }

        return _scan.Is(from, "[") || commas > 0 || (grouped && strings == 0);
    }

    /// <summary>Whether the name at <paramref name="at"/> is the word <paramref name="word"/>.</summary>
    private bool Is(int at, string word) => _scan.IsName(at) && string.Equals(_scan.Text(at), word, StringComparison.Ordinal);

    private string Text(int at) => _scan.Text(at);

    // ---- Functions ----

    /// <summary>
    /// A <c>def</c> at <paramref name="at"/>, with the <paramref name="decorators"/> above it: a member node over its decorators, its head and
    /// its suite - a function at the top or inside another declaration, a method in a class, <c>__init__</c> a constructor - with how many
    /// arguments it takes positionally, a receiver taken off a method's but a static method's, each parameter's annotation and its return
    /// annotation the types they name, its type parameters its own, each decorator's expression and each parameter's default its code -
    /// each of them made around the function, where the function is defined (<see cref="ExtractReference.Around"/>); its body a scope of
    /// its own, closed, whose parent is the function or the module around it and never a class, its suite's statements read in it, and its
    /// head and its code read once every declaration of the file is made (<see cref="Bodies"/>). A test file's <c>test*</c> function and a test class's <c>test*</c>
    /// method are tests, and one of a class that may be a test class by a base another file declares a test by its type; each parameter a
    /// test requires names, as a fixture does, the fixture that provides it, but one a parameter set of pytest's gives it; a function
    /// pytest's or pytest-asyncio's fixture decorator decorates is a fixture named by the decorator's <c>name=</c> where one is given,
    /// never a test. A <c>def</c> whose head cannot be read is marked so, its lines the code around it.
    /// </summary>
    private void Function(int at, int to, ScanFrame frame, List<(int From, int To)> decorators)
    {
        var asynchronous = Is(at, "async");
        var def = asynchronous ? _scan.Next(at) : at;
        var nameAt = _scan.Next(def);
        var types = TypeParameters(nameAt);
        var open = types.Close >= 0 ? _scan.Next(types.Close) : _scan.Next(nameAt);
        var close = _scan.Is(open, "(") ? _scan.Match(open) : -1;
        var colon = close >= 0 ? HeaderColon(close, to) : -1;

        if (!_scan.IsName(nameAt) || close < 0 || colon < 0)
        {
            _extract.Dynamic(_scan[def].Line, "a def whose head the reader cannot read: its lines read as the code around it");
            return;
        }

        var parameters = Parameters(open, close);
        var returns = _scan.Is(_scan.Next(close), "->") ? _scan.Next(_scan.Next(close)) : -1;
        var first = decorators.Count > 0 ? decorators[0].From - 1 : at;
        _headerColons.Add(colon);
        var (suite, end) = Suite(colon, to, Column(first));
        var defined = Text(nameAt);
        var method = frame.Owner is not null;
        var holder = frame.Parent!;
        var receives = method && !decorators.Any(decorator => Builtin(decorator, "staticmethod", holder));

        // pytest's fixture, told by what binds its decorator's head where the function is defined - a fixture pytest finds is one of a
        // module or a class - and named as pytest names it: by its name= where one is given, else by the function's.
        var fixture = decorators.Select(decorator => FixtureName(decorator, holder)).FirstOrDefault(found => found is not null);
        var name = fixture is { Length: > 0 } given ? given : defined;
        var qualified = frame.Qualify(name);
        var body = suite.Count > 0 ? suite[0].From : colon + 1;
        var key = _extract.Add(new ExtractNode(Id(qualified), CodeNodeKinds.Member, name, qualified, _scan[first].Line, _scan[_scan.LastOf(end)].EndLine, _scan.Signature(first, colon))
        {
            SubKind = fixture is not null ? CodeSubKinds.Fixture : method && name == "__init__" ? "constructor" : method ? "method" : "function",
            Scope = frame.Scope,
            Parent = frame.Parent,
            BodyStart = suite.Count > 0 ? _scan[suite[0].From].Line : null,
            Summary = ScanComments.Summary(_scan, first) ?? Docstring(suite),
            Modifiers = [.. (asynchronous ? ["async"] : Array.Empty<string>()), .. decorators.Select(DecoratorName)],
            MinArity = Math.Max(0, parameters.Least - (receives ? 1 : 0)),
            MaxArity = parameters.Most is { } most ? Math.Max(0, most - (receives ? 1 : 0)) : null,
        });

        // The module binds the function by its own name, whatever pytest names a fixture.
        _functions.Add(defined);
        _declared[first] = end;
        Own(key, types);

        // The names a parameter set of pytest's gives the function in place of a fixture of each name.
        var parametrized = Parametrized(decorators, holder);

        if (!method)
        {
            _free.Add(defined);
        }

        if (frame.Depth == 1)
        {
            Top(defined);
        }

        if (receives && _classInfo.TryGetValue(frame.Parent!, out var owner))
        {
            _methods[key] = owner;
        }

        // A method is bound by its name alone: where a base of its type declares one of that name, along the chain, the method
        // overrides it - looked for among the bases and never in the type itself, as every reader looks for an override. No word of
        // the language says a method overrides, so the reference asks rather than names: a base declaring none is no doubt.
        if (method)
        {
            _extract.Reference(key, CodeEdgeKinds.Overrides, defined, _scan[nameAt].Line, qualifier: new ExtractQualifier(CodeQualifierKinds.Base), probe: true);
        }

        // The function's body is a scope of its own, closed - what it declares is its own and seen from no other file - whose parent is
        // the function or the module around it and never a class: Python looks a name a body reads up through the functions around it to
        // the module, and reaches a class's names through self, cls or the class's name alone. What it declares is held by the function
        // (<see cref="ScanFrame.Parent"/>) and named under it.
        var outer = Enclosing(frame.Scope)!;
        _extract.Scope(qualified, open: false, parent: outer);
        _enclosing[qualified] = qualified;

        // Read once every declaration of the file is made, before what it holds (<see cref="_bodies"/>): first whether it is a test, which
        // its class's bases decide, read before it; then its head, where the function is defined, as Python runs it - its decorators,
        // its type parameters' bounds, its annotations and defaults, the fixtures its parameters name, each made around the function -
        // then its body, whose locals and parameters are its own and the functions' it holds.
        _bodies.Add(new Body(outer, qualified, key,
            () =>
            {
                // A test is what a runner collects: a test* function at a collected file's top, and a test* method of a test class -
                // one a test by a base another file declares a test by its type; a fixture never.
                var named = fixture is null && defined.StartsWith("test", StringComparison.Ordinal);
                var test = named && (method ? _testTypes.Contains(holder) : _testFile && frame.Depth == 1);
                var byType = named && !test && method && _maybeTestTypes.Contains(holder);

                if (test || byType)
                {
                    _extract.Change(key, node => node with { Test = test, TestByType = byType });
                }

                _extract.Around = true;

                try
                {
                    foreach (var decorator in decorators)
                    {
                        Decorator(decorator, key, frame.Owner);
                    }

                    TypeParameterBounds(types, key, null);

                    if (returns >= 0)
                    {
                        TypeNames(_scan, returns, colon, key, null);
                    }

                    foreach (var (from, endOf) in parameters.Annotations)
                    {
                        TypeNames(_scan, from, endOf, key, null);
                    }

                    foreach (var (from, endOf) in parameters.Defaults)
                    {
                        Calls(_text, from, endOf, key, Bind(from, endOf), frame.Owner);
                    }

                    if (test || byType || fixture is not null)
                    {
                        // Each parameter a test or a fixture requires names the fixture that provides it, by its name alone: as pytest
                        // finds it, the test's own class's or module's, else a conftest.py's of its directory or of one above it, the
                        // nearest first, which no test imports - but one a parameter set of the function gives, which no fixture does.
                        foreach (var (needed, line) in parameters.Required.Where(required => required.Name is not ("self" or "cls") && !parametrized.Contains(required.Name)))
                        {
                            _extract.Reference(key, CodeEdgeKinds.References, needed, line, qualifier: new ExtractQualifier(CodeQualifierKinds.Fixture));
                        }
                    }
                }
                finally
                {
                    _extract.Around = false;
                }
            },
            () =>
            {
                var bound = Bind(body, end);
                bound.UnionWith(parameters.Names);
                Calls(_text, body, end, key, bound, frame.Owner);
                return bound;
            }));

        var inside = frame.Inside(qualified, qualified, key, null);
        _walks.Walk(0, 1, _ =>
        {
            Walk(suite, inside);
            return 1;
        });
    }

    /// <summary>The scope a function declared in the scope <paramref name="scope"/> finds the names its body reads in after its own (<see cref="_enclosing"/>): a class's the one around it.</summary>
    private QualifiedName? Enclosing(QualifiedName? scope) => scope is not null && _enclosing.TryGetValue(scope, out var enclosing) ? enclosing : scope;

    /// <summary>The name a decorator is said by, as a modifier: an <c>@</c> and the dotted name it is written with - <c>@dataclass</c>, <c>@pytest.fixture</c>, <c>@app.route</c> - without its arguments.</summary>
    private string DecoratorName((int From, int To) decorator)
        => _scan.IsName(decorator.From)
            ? "@" + string.Join('.', Chain(_scan, decorator.From).TakeWhile(index => index < decorator.To).Select(Text))
            : "@...";

    /// <summary>
    /// The name a <paramref name="decorator"/> calls with the function it decorates: the last of the dotted name it is written with,
    /// where it is that name alone - <c>@task</c>, <c>@pytest.fixture</c> - or that name called once - <c>@app.route("/x")</c>, whose
    /// answer is called with the function; -1 where it is any other expression, whose calls are the ones it writes.
    /// </summary>
    private int Callee((int From, int To) decorator)
    {
        if (!_scan.IsName(decorator.From))
        {
            return -1;
        }

        var last = Chain(_scan, decorator.From).TakeWhile(index => index < decorator.To).Last();
        var after = _scan.Next(last);

        return after >= decorator.To || (_scan.Is(after, "(") && _scan.Match(after) is var close and > 0 && _scan.Next(close) >= decorator.To) ? last : -1;
    }

    /// <summary>
    /// The dotted name a <paramref name="decorator"/>'s name stands for where an import of a package binds its head for the code of
    /// the node of <paramref name="holder"/>, where the decorated declaration is defined - <c>pytest.fixture</c> for
    /// <c>@pytest.fixture</c> of <c>import pytest</c>, and for <c>@fixture</c> of <c>from pytest import fixture</c> - the module's
    /// dotted path and the names after it; <see langword="null"/> where no such import binds it, its head one the file declares, a
    /// relative import's or none.
    /// </summary>
    private string? Packaged((int From, int To) decorator, QualifiedName holder)
    {
        var last = Callee(decorator);

        if (last < 0 || BindingOf(Text(decorator.From), holder) is not { Bare: true } through)
        {
            return null;
        }

        var module = through.Path.Replace('/', '.');
        var names = Chain(_scan, decorator.From).TakeWhile(index => index <= last).Skip(1).Select(Text);

        return string.Join('.', [through.Kind == BindingKind.Module ? module : $"{module}.{through.Name}", .. names]);
    }

    /// <summary>
    /// Whether <paramref name="decorator"/> is the language's own <paramref name="builtin"/> - <c>@staticmethod</c> - written alone,
    /// a name the code of the node of <paramref name="holder"/> binds to nothing of its own.
    /// </summary>
    private bool Builtin((int From, int To) decorator, string builtin, QualifiedName holder)
        => Is(decorator.From, builtin) && _scan.Next(decorator.From) >= decorator.To && !Shadows(builtin, null, holder);

    /// <summary>
    /// What pytest names the function <paramref name="decorator"/> makes a fixture (<see cref="FixtureDecorators"/>), read where the
    /// function is defined: the string its <c>name=</c> gives, where one does; empty where none does, the fixture named by its function;
    /// <see langword="null"/> where the decorator makes no fixture.
    /// </summary>
    private string? FixtureName((int From, int To) decorator, QualifiedName holder)
    {
        if (Packaged(decorator, holder) is not { } dotted || !FixtureDecorators.Contains(dotted))
        {
            return null;
        }

        var open = _scan.Next(Callee(decorator));

        if (!_scan.Is(open, "("))
        {
            return string.Empty;
        }

        foreach (var (start, end) in ScanCalls.Split(_scan, _scan.Next(open), _scan.Match(open), null))
        {
            if (Is(start, "name") && _scan.Is(_scan.Next(start), "=") && _scan.Next(_scan.Next(start)) is var value
                && _scan[value].Kind == ScanTokenKind.Text && _scan.Next(value) >= end && _scan.Value(value) is { } named && IsIdentifier(named))
            {
                return named;
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// The names the parameter sets among <paramref name="decorators"/> give the function (<see cref="ParameterSets"/>), read where it
    /// is defined: each its first argument - or its <c>argnames=</c> - names, a string of names a comma parts or a list or a tuple of
    /// strings, as pytest reads them; a parameter of one of those names is given its values, and no fixture.
    /// </summary>
    private HashSet<string> Parametrized(List<(int From, int To)> decorators, QualifiedName holder)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (var decorator in decorators)
        {
            if (Packaged(decorator, holder) != ParameterSets || _scan.Next(Callee(decorator)) is var open && !_scan.Is(open, "("))
            {
                continue;
            }

            var arguments = ScanCalls.Split(_scan, _scan.Next(open), _scan.Match(open), null);
            var keyword = arguments.FindIndex(argument => Is(argument.Start, "argnames") && _scan.Is(_scan.Next(argument.Start), "="));
            var (from, to) = keyword >= 0 ? (_scan.Next(_scan.Next(arguments[keyword].Start)), arguments[keyword].End) : arguments.Count > 0 ? arguments[0] : (-1, -1);

            for (var at = from; at >= 0 && at < to; at = _scan.Next(at))
            {
                if (_scan[at].Kind == ScanTokenKind.Text && _scan.Value(at) is { } value)
                {
                    names.UnionWith(value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                }
            }
        }

        return names;
    }

    /// <summary>Whether <paramref name="text"/> is a name Python may give a function: a letter or an underscore, then letters, digits and underscores.</summary>
    private static bool IsIdentifier(string text)
        => text.Length > 0 && (char.IsLetter(text[0]) || text[0] == '_') && text.All(character => char.IsLetterOrDigit(character) || character == '_');

    /// <summary>
    /// The expression of a <paramref name="decorator"/>, the code of the node of <paramref name="key"/>, read whole: what it calls, reads
    /// and holds - the route path or the task name among its literals - and the name it calls with the function it decorates
    /// (<see cref="Callee"/>) a call of what that name binds, of one argument where it is written alone. A name the language answers to
    /// - <c>@property</c>, <c>@staticmethod</c> - names nothing of the repository's; one an import of a package binds - a framework's, a
    /// decorator library's, <c>@pytest.fixture</c>, <c>@functools.wraps(f)</c> - asks whether the repository declares it
    /// (<see cref="ExtractReference.Probe"/>), as no other decorator does: no decorator is told by the name it is written with.
    /// </summary>
    private void Decorator((int From, int To) decorator, QualifiedName key, string? owner)
    {
        var (from, to) = decorator;
        Calls(_text, from, to, key, Bind(from, to), owner, callee: Callee(decorator));
        Keep(from, to);
    }

    // ---- Classes ----

    /// <summary>
    /// A <c>class</c> at <paramref name="at"/>, with the <paramref name="decorators"/> above it: a type node over its decorators, its head and
    /// its suite; its type parameters its own; each base its parentheses name - a name, or a dotted one through an import, a subscript
    /// after it its types' references - an <c>inherits</c> edge to the first and <c>mixesIn</c> to the others, a base the code computes or a
    /// keyword of the class its code; its suite's statements read in its own frame, the names it declares found first so an attribute a
    /// method assigns is told from them, and what the suite holds outside its declarations the class's code. Its head - its decorators,
    /// its type parameters' bounds, its bases - is read where the class is defined, once every name of the file is bound, and its bases
    /// say whether it is a test class (<see cref="Tested"/>). A <c>class</c> whose head cannot be read is marked so, its lines the code
    /// around it.
    /// </summary>
    private void Class(int at, int to, ScanFrame frame, List<(int From, int To)> decorators)
    {
        var nameAt = _scan.Next(at);
        var types = TypeParameters(nameAt);
        var head = types.Close >= 0 ? _scan.Next(types.Close) : _scan.Next(nameAt);
        var colon = _scan.IsName(nameAt) ? HeaderColon(head, to) : -1;

        if (colon < 0)
        {
            _extract.Dynamic(_scan[at].Line, "a class whose head the reader cannot read: its lines read as the code around it");
            return;
        }

        var first = decorators.Count > 0 ? decorators[0].From - 1 : at;
        var (suite, end) = Suite(colon, to, Column(first));
        var name = Text(nameAt);
        var qualified = frame.Qualify(name);
        var body = suite.Count > 0 ? suite[0].From : colon + 1;
        _headerColons.Add(colon);
        var key = _extract.Add(new ExtractNode(Id(qualified), CodeNodeKinds.Type, name, qualified, _scan[first].Line, _scan[_scan.LastOf(end)].EndLine, _scan.Signature(first, colon))
        {
            SubKind = "class",
            Scope = frame.Scope,
            Parent = frame.Parent,
            BodyStart = suite.Count > 0 ? _scan[suite[0].From].Line : null,
            Summary = ScanComments.Summary(_scan, first) ?? Docstring(suite),
            Modifiers = [.. decorators.Select(DecoratorName)],
        });

        _classes.Add(name);
        _declared[first] = end;
        Own(key, types);

        if (frame.Owner is null)
        {
            _free.Add(name);
        }

        if (frame.Depth == 1)
        {
            Top(name);
        }

        var pieces = _scan.Is(head, "(") && _scan.Match(head) is var close && close > 0 ? ScanCalls.Split(_scan, _scan.Next(head), close, null) : [];

        // The class's names are found before any of its methods is read, so an attribute a method assigns is told from a name the
        // class declares below the method. A function declared in the class finds the names its body reads in the scope around the
        // class, which a class does not enclose.
        var declared = ClassNames(suite);
        var outer = Enclosing(frame.Scope)!;
        _classInfo[key] = new ClassInfo(key, qualified, [.. declared]);
        _enclosing[qualified] = outer;

        // Read once every declaration of the file is made, before what it holds (<see cref="_bodies"/>): first what its head runs
        // where it is defined - its decorators, its type parameters' bounds, its bases, each made around it, so an import of its own
        // binds none of it - and, by its bases, whether it is a test class; then what its suite holds outside its declarations, the
        // class's own code. A name the suite declares is what the class declares: a read of it in that code is a use of the class's own,
        // as the body sees its own names and none of its bases'.
        _bodies.Add(new Body(outer, null, key,
            () =>
            {
                var cases = Cases.None;
                _extract.Around = true;

                try
                {
                    foreach (var decorator in decorators)
                    {
                        Decorator(decorator, key, frame.Owner);
                    }

                    TypeParameterBounds(types, key, null);
                    var bases = 0;

                    foreach (var (from, endOf) in pieces)
                    {
                        if (BaseName(from, endOf, key) is { } named)
                        {
                            _extract.Reference(key, bases++ == 0 ? CodeEdgeKinds.Inherits : CodeEdgeKinds.MixesIn, Simple(named.Written), _scan[from].Line, qualifier: named.Qualifier);
                            cases = (Cases)Math.Max((int)cases, (int)CasesOf(named));

                            // Repository[User]: the base is Repository, the types its subscript names references of the class.
                            if (named.Subscript >= 0)
                            {
                                TypeNames(_scan, _scan.Next(named.Subscript), _scan.Match(named.Subscript), key, null);
                            }
                        }
                        else
                        {
                            // A base the code computes - a call - or a keyword of the class, metaclass=: the code around the class, in
                            // its type parameters' scope.
                            _typed = true;
                            Calls(_text, from, endOf, key, Bind(from, endOf), frame.Owner);
                            _typed = false;
                        }
                    }
                }
                finally
                {
                    _extract.Around = false;
                    _typed = false;
                }

                Tested(key, name, cases);
            },
            () =>
            {
                var bound = Bind(body, end);
                bound.ExceptWith(declared);
                Calls(_text, body, end, key, bound, name, declared);
                return null;
            }));

        var inside = frame.Inside(qualified, qualified, key, name);
        _walks.Walk(0, 1, _ =>
        {
            Walk(suite, inside);
            return 1;
        });
    }

    /// <summary>
    /// The names a class's <paramref name="suite"/> declares in its own statements: each function's and class's, each name assigned or
    /// annotated, each type alias's, and those its compound statements' headers and suites bind - a <c>for</c>'s targets, the names
    /// <c>as</c> gives, what an <c>if</c>'s or a <c>try</c>'s suite assigns - what is the class's whatever line declares it, found before
    /// any of its statements is read. A compound statement's header is read alone and its suite's statements each in their turn, on a
    /// stack, a declaration passed over by its name: each token of the class's own statements is read once, however deep they nest.
    /// </summary>
    private HashSet<string> ClassNames(List<(int From, int To)> suite)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<(int From, int To, bool Clause)>();

        void Push(List<(int From, int To)> statements, bool clauses)
        {
            for (var index = statements.Count - 1; index >= 0; index--)
            {
                pending.Push((statements[index].From, statements[index].To, clauses));
            }
        }

        Push(suite, clauses: false);

        while (pending.TryPop(out var statement))
        {
            var (from, to, clause) = statement;
            var at = from;

            while (_scan.Is(at, "@") && LineEnd(_scan.Next(at), to) is var next && next > at && next < to)
            {
                at = next;
            }

            var keyword = Is(at, "async") ? _scan.Next(at) : at;

            if ((Is(keyword, "def") || Is(keyword, "class") || Is(keyword, "type")) && _scan.IsName(_scan.Next(keyword)))
            {
                names.Add(Text(_scan.Next(keyword)));
                continue;
            }

            // A compound statement: what its header binds, then its suite's statements - a match's, its case clauses.
            var colon = CompoundAt(at, to) is { } compound ? compound.Colon
                : (clause && Is(at, "case")) || Compound.Contains(Text(at)) ? HeaderColon(at, to)
                : -1;

            if (colon > at)
            {
                names.UnionWith(Bind(at, colon));
                Push(Suite(colon, to, Column(at)).Statements, clauses: Is(at, "match"));
                continue;
            }

            foreach (var (start, end) in Simple(at, Math.Min(to, _scan.Count)))
            {
                for (var target = start; target < end && TargetsAt(target, end, attributes: false) is { } targets; target = targets.Value)
                {
                    names.UnionWith(targets.Names.Select(name => Text(name.At)));

                    if (targets.Value < 0)
                    {
                        break;
                    }
                }
            }
        }

        return names;
    }

    /// <summary>
    /// The base the tokens from <paramref name="from"/> to <paramref name="to"/> name, where they are a name or a dotted one, alone or with
    /// one subscript after it (<c>Repository[User]</c>), read in the code of the node of <paramref name="key"/>: written out in dots, a
    /// name an import binds read as what it reaches and a dotted one through the import its head is (<see cref="Receivers"/>), a
    /// class the file declares named as a type, and the subscript's bracket; <see langword="null"/> where they are anything else.
    /// </summary>
    private BaseNamed? BaseName(int from, int to, QualifiedName key)
    {
        var chain = Chain(_scan, from);
        var after = _scan.Next(chain[^1]);
        var subscript = _scan.Is(after, "[") && _scan.Match(after) is var close && close > after && _scan.Next(close) >= to ? after : -1;

        if (!_scan.IsName(from) || Words.Contains(Text(from)) || (subscript < 0 && chain[^1] != _scan.Previous(to)) || chain.Any(index => Words.Contains(Text(index))))
        {
            return null;
        }

        var written = string.Join('.', chain.Select(Text));
        var through = BindingOf(Text(from), key);

        if (chain.Count == 1 && through is not null)
        {
            // A base an import binds: from m import A as B; class C(B) is built on A, through B.
            return through.Kind == BindingKind.Module ? null : new BaseNamed(through.Name, Itself(through), subscript, through);
        }

        if (chain.Count > 1 && through is not null)
        {
            return new BaseNamed(written, Receivers(_scan, chain, null, null, through, chain.Count - 1)[0], subscript, through);
        }

        return new BaseNamed(written, chain.Count > 1 && _classes.Contains(Text(chain[0])) ? new ExtractQualifier(CodeQualifierKinds.Type, QualifiedName.Of(Text(chain[0]))) : null, subscript, null);
    }

    /// <summary>A base a class names (<see cref="BaseName"/>): written out in dots, what it is qualified by, its subscript's bracket or -1, and the binding of the import its head is, where one is.</summary>
    private sealed record BaseNamed(string Written, ExtractQualifier? Qualifier, int Subscript, Binding? Through);

    /// <summary>
    /// What the base <paramref name="named"/> makes a class built on it by its type, as unittest runs one: a <c>TestCase</c> subclass
    /// where it names a class whose name ends so through an import - <c>unittest.TestCase</c>, <c>django.test.TestCase</c>,
    /// <c>from unittest import TestCase</c> - or one written so that nothing of the file binds, or a class of the file that is one; one that
    /// may be one where it names any other class an import reaches, or a class of the file that may be; neither where it names a class of
    /// the file that is neither, a name the language answers to, or one nothing binds.
    /// </summary>
    private Cases CasesOf(BaseNamed named)
    {
        var last = Simple(named.Written);

        if (named.Through is not null)
        {
            return last.EndsWith("TestCase", StringComparison.Ordinal) ? Cases.Case : Cases.Maybe;
        }

        return _cases.TryGetValue(last, out var cases) ? cases
            : last.EndsWith("TestCase", StringComparison.Ordinal) ? Cases.Case
            : Cases.None;
    }

    /// <summary>
    /// Says what the class of <paramref name="key"/>, named <paramref name="name"/>, is by its bases' <paramref name="cases"/>: a
    /// <c>TestCase</c> subclass is a test class wherever it is declared, as unittest runs it by its type, and a type every class built on
    /// it is a test class by (<see cref="ExtractNode.TestBase"/>); a test file's <c>Test*</c> class is a test class, as pytest collects
    /// one; any other class of a test file that may be one by a base another file declares says so (<see cref="ExtractNode.TestByType"/>),
    /// for the linker to decide. Its methods named <c>test*</c> are tests, or tests by their type, as it is.
    /// </summary>
    private void Tested(QualifiedName key, string name, Cases cases)
    {
        _cases[name] = cases;
        var testBase = cases == Cases.Case;
        var test = testBase || (_testFile && name.StartsWith("Test", StringComparison.Ordinal));
        var byType = !test && _testFile && cases == Cases.Maybe;

        if (test)
        {
            _testTypes.Add(key);
        }
        else if (byType)
        {
            _maybeTestTypes.Add(key);
        }

        if (test || byType)
        {
            _extract.Change(key, node => node with { Test = test, TestBase = testBase, TestByType = byType });
        }
    }

    // ---- Imports ----

    /// <summary>
    /// The file's imports, each the import of the frame it is written in: <c>import a.b.c</c> - the package <c>a</c> by the name the code
    /// gives it, and the module <c>a.b.c</c> reached by the dotted names to it, as Python binds <c>a</c> and reaches each module through
    /// it, a step between them - <c>a.b</c> - a module of its own where the code reaches it (<see cref="Receivers"/>);
    /// <c>import a.b.c as m</c> - the module, reached by <c>m</c>; <c>from a.b import c, d as e</c> - the module, showing each name it
    /// binds, and each name where it is a module of its own, else the name the module binds by it (<see cref="ExtractImport.Binds"/>),
    /// reached by the name the code gives it, so <c>c.f()</c> finds <c>f</c> in either; <c>from a.b import *</c> - the names the module
    /// publishes. One at the module's top - in its conditional blocks too - binds names of the module, its importers' too
    /// (<see cref="ExtractImport.Reexports"/>), and what a <c>*</c> import of the module binds; one in a function or a class binds for
    /// the code of that declaration alone, and is reached by a prefix no other code of the file writes. A module by a bare name is looked
    /// for from the file's own directory up to the tree's root, the nearest first, then under the roots of the layout
    /// (<see cref="Places"/>); one by a relative path, beside the file.
    /// </summary>
    private void Import(int at, int to, ScanFrame frame)
    {
        Keep(at, to);
        var line = _scan[at].Line;
        var top = frame.Depth == 1;
        var holder = frame.Parent!;

        // What a declaration imports is reached by a prefix of its own: its id, a colon after it, which no name of the language writes.
        var own = top ? string.Empty : $"{holder.Whole()}:";

        if (Is(at, "import"))
        {
            foreach (var (from, _) in ScanCalls.Split(_scan, _scan.Next(at), to, null))
            {
                var chain = Chain(_scan, from);
                var alias = _scan.Is(_scan.Next(chain[^1]), "as") && _scan.IsName(_scan.Next(_scan.Next(chain[^1]))) ? Text(_scan.Next(_scan.Next(chain[^1]))) : null;

                // import a.b.c as m: m is the module a.b.c.
                if (alias is not null)
                {
                    var path = string.Join('/', chain.Select(Text));
                    Imports(Reaching(0, path, line), own + alias, top);
                    Bind(holder, new Binding(BindingKind.Module, alias, path, own + alias, null, path) { Bare = true }, frame);
                    continue;
                }

                // import a.b.c: a is the package a, and the module a.b.c is reached by a.b.c.
                var head = Text(from);
                var binding = Bind(holder, new Binding(BindingKind.Module, head, head, own + head, null, head) { Bare = true }, frame);
                Imports(Reaching(0, head, line), own + head, top);

                if (chain.Count > 1)
                {
                    var step = binding.Head;

                    foreach (var name in chain.Skip(1))
                    {
                        step = step.Next.TryGetValue(Text(name), out var next) ? next : step.Next[Text(name)] = new ImportStep(step, Text(name), line);
                    }

                    Reached(step, binding);
                }
            }

            return;
        }

        // from [.]*module import names: the dots a relative path, the module a bare name or a path beside the file.
        var cursor = _scan.Next(at);
        var dots = 0;

        while (_scan.Is(cursor, ".") || _scan.Is(cursor, "..."))
        {
            dots += _scan.Is(cursor, "...") ? 3 : 1;
            cursor = _scan.Next(cursor);
        }

        var module = new List<string>();

        while (_scan.IsName(cursor) && !Is(cursor, "import"))
        {
            module.Add(Text(cursor));
            cursor = _scan.Is(_scan.Next(cursor), ".") && _scan.IsName(_scan.Next(_scan.Next(cursor))) ? _scan.Next(_scan.Next(cursor)) : _scan.Next(cursor);
        }

        if (!Is(cursor, "import"))
        {
            return;
        }

        var names = _scan.Next(cursor);

        if (_scan.Is(names, "("))
        {
            names = _scan.Next(names);
        }

        var modulePath = string.Join('/', module);

        // from m import *: the names the module publishes, alone - at the module's top names of the module's too.
        if (_scan.Is(names, "*"))
        {
            foreach (var imported in Reaching(dots, modulePath, line))
            {
                _extract.Import(imported with { Published = true, Reexports = top });
            }

            return;
        }

        var importedNames = new List<(string Name, string? Alias)>();

        foreach (var (from, _) in ScanCalls.Split(_scan, names, to, null))
        {
            if (_scan.IsName(from))
            {
                importedNames.Add((Text(from), _scan.Is(_scan.Next(from), "as") && _scan.IsName(_scan.Next(_scan.Next(from))) ? Text(_scan.Next(_scan.Next(from))) : null));
            }
        }

        var plain = importedNames.Where(item => item.Alias is null).Select(item => item.Name).ToList();

        if (plain.Count > 0)
        {
            Imports(Reaching(dots, modulePath, line), top ? null : own, top, plain);
        }

        foreach (var (name, alias) in importedNames)
        {
            var local = alias ?? name;
            var prefix = own + local;

            // A renamed name is shown by the module under the name the code gives it.
            if (alias is not null)
            {
                Imports(Reaching(dots, modulePath, line), prefix, top, [name]);
            }

            // The name where it is a module of its own, else the name the module binds by it: reached through the name the code gives
            // it, so x.f() finds f in either - the module's f, or the member f of the class the module binds as x.
            foreach (var imported in ReachingName(dots, modulePath, name, line))
            {
                _extract.Import(imported with { Prefix = prefix, Reexports = top });
            }

            Bind(holder, new Binding(alias is null ? BindingKind.Shown : BindingKind.Renamed, local, name, prefix, alias is null ? (top ? null : own) : prefix, modulePath) { Bare = dots == 0 }, frame);
        }
    }

    /// <summary>Makes each of <paramref name="places"/> an import of the file: reached by <paramref name="prefix"/>, showing <paramref name="show"/>, and - where <paramref name="reexports"/> - the file's importers' too.</summary>
    private void Imports(IEnumerable<ExtractImport> places, string? prefix, bool reexports, IReadOnlyList<string>? show = null)
    {
        foreach (var place in places)
        {
            _extract.Import(place with { Prefix = prefix, Show = show, Reexports = reexports });
        }
    }

    /// <summary>
    /// Says the import that binds <paramref name="binding"/> binds it for the code of the node of <paramref name="holder"/> - the
    /// module's for one at its top, which is a name of the module too (<see cref="Top"/>), and a class's for its own code alone where
    /// <paramref name="frame"/> is a class's body - and answers the binding the code reaches it by: <c>import a.b</c> and
    /// <c>import a.c</c> bind <c>a</c> once, the steps of both reached through it.
    /// </summary>
    private Binding Bind(QualifiedName holder, Binding binding, ScanFrame frame)
    {
        var top = frame.Depth == 1;
        var bindings = top ? _bindings : _local.TryGetValue(holder, out var held) ? held.Names : (_local[holder] = new LocalBindings(frame.Owner is not null)).Names;

        if (top)
        {
            Top(binding.Local);
        }

        if (binding.Kind == BindingKind.Module && bindings.TryGetValue(binding.Local, out var was) && was.Kind == BindingKind.Module && was.Prefix == binding.Prefix && was.Path == binding.Path)
        {
            return was;
        }

        return bindings[binding.Local] = binding;
    }

    /// <summary>
    /// The binding an import makes of <paramref name="name"/> for the code of the node of <paramref name="key"/> - the one place a name
    /// the code reads is bound to an import, in whatever text the file's code is, a string annotation's and an f-string's field among it:
    /// the declaration's own the code is of - a method's for the annotation of an attribute it assigns (<see cref="_codeOf"/>) - a
    /// class's for the class's own code alone - its body's and its fields' values - then the
    /// nearest function's around it that imports it, as Python looks a name up through the functions around a body and passes a class
    /// over (<see cref="_enclosed"/>), else the module's top's; none where no import binds it. What a declaration's head runs, made around
    /// it (<see cref="ScanExtract.Around"/>) - its decorators, bases, annotations and defaults - is the code of the node that holds the
    /// declaration, as Python runs it where the declaration is defined: its names are bound there, never by the declaration's own
    /// imports. Each name is looked for in two declarations' imports and the functions open at most, never by a walk up the file's nodes.
    /// </summary>
    private Binding? BindingOf(string name, QualifiedName key)
    {
        if (_local.Count > 0 && (_extract.Around ? _extract.Node(key).Parent : _codeOf.GetValueOrDefault(key, key)) is { } code)
        {
            if (_local.TryGetValue(code, out var own) && own.Names.TryGetValue(name, out var local))
            {
                return local;
            }

            // A field's or a constant's value in a class's body is the class's own code, whose imports bind it.
            if (OfClassBody(code) && _extract.Node(code).Parent is { } holder && _local.TryGetValue(holder, out var held) && held.Class
                && held.Names.TryGetValue(name, out var classLocal))
            {
                return classLocal;
            }
        }

        if (_enclosed.TryGetValue(name, out var enclosed))
        {
            return enclosed[^1];
        }

        return _bindings.TryGetValue(name, out var atTop) ? atTop : null;
    }

    /// <summary>Whether the node of <paramref name="key"/> is a name a class's body assigns - a field or a constant - whose value is the class's own code.</summary>
    private bool OfClassBody(QualifiedName key) => _extract.Node(key) is { Kind: CodeNodeKinds.Member, SubKind: "field" or "constant" };

    /// <summary>
    /// What a reference to the name an import binds is qualified by: the prefix of the import that shows it, where one has a prefix -
    /// a renamed one, one a declaration makes - and none where an import shows it by the name itself.
    /// </summary>
    private static ExtractQualifier? Itself(Binding binding) => binding.Shows is { } shows ? new ExtractQualifier(CodeQualifierKinds.Prefix, QualifiedName.Of(shows)) : null;

    /// <summary>
    /// The module path of <paramref name="chain"/> of <paramref name="scan"/>, whose head <paramref name="binding"/> binds: the steps of the
    /// binding's dotted import the longest run of the chain's names before its last reaches - its head, then each name a step past the one
    /// before it - one for each name of that run, the head among them. The chain's names past it are what the module holds.
    /// </summary>
    private static List<ImportStep> StepsOf(ScannedText scan, List<int> chain, Binding binding)
    {
        var steps = new List<ImportStep> { binding.Head };

        while (steps.Count < chain.Count - 1 && steps[^1].Next.TryGetValue(scan.Text(chain[steps.Count]), out var next))
        {
            steps.Add(next);
        }

        return steps;
    }

    /// <summary>
    /// The prefix the module of <paramref name="step"/>, a step of <paramref name="binding"/>'s dotted import, is reached by: the binding's,
    /// with each name from its head to the step - each step's found once, from the one before it (<see cref="ImportStep.Reach"/>) - and an
    /// import of the module made where none is yet, the import of the binding's head being made with the binding.
    /// </summary>
    private string Reached(ImportStep step, Binding binding)
    {
        var unknown = new Stack<ImportStep>();
        var at = step;

        while (at.Reach is null)
        {
            unknown.Push(at);
            at = at.Before!;
        }

        var prefix = at.Reach;

        while (unknown.TryPop(out var next))
        {
            prefix = next.Reach = $"{prefix}.{next.Name}";
        }

        if (!step.Imported)
        {
            step.Imported = true;
            var names = new List<string>();

            for (var on = step; on.Before is not null; on = on.Before)
            {
                names.Add(on.Name);
            }

            names.Reverse();
            Imports(Reaching(0, $"{binding.Path}/{string.Join('/', names)}", step.Line), prefix, reexports: false);
        }

        return prefix;
    }

    /// <summary>
    /// The imports that reach the module whose path in the tree's layout is <paramref name="path"/> - its dotted name with a slash for
    /// each dot - at each place it is looked for (<see cref="Places"/>): a package's <c>__init__.py</c> and else the module's <c>.py</c>,
    /// each place standing in for the one before it (<see cref="ExtractImport.Fallback"/>), so the linker takes the first that names a
    /// file of the tree - whichever it is, one import.
    /// </summary>
    private IEnumerable<ExtractImport> Reaching(int dots, string path, int line)
        => dots == 0 && path.Length == 0 ? [] : Run(Places(dots).SelectMany(place => Module(place, path, line)));

    /// <summary>
    /// The imports that reach what <c>from m import name</c> binds by <paramref name="name"/>, <paramref name="path"/> being <c>m</c>'s: at
    /// each place, the module <c>m.name</c> where it is one, else the name <c>m</c> binds by it (<see cref="ExtractImport.Binds"/>) - so the
    /// name is taken from the module Python finds at the nearest place.
    /// </summary>
    private IEnumerable<ExtractImport> ReachingName(int dots, string path, string name, int line)
        => dots == 0 && path.Length == 0 ? [] : Run(Places(dots).SelectMany(place => Module(place, path.Length == 0 ? name : $"{path}/{name}", line)
            .Concat(Module(place, path, line).Select(import => import with { Show = [name], Binds = true }))));

    /// <summary><paramref name="places"/>, each after the first standing in for the one before it: one run, one import.</summary>
    private static IEnumerable<ExtractImport> Run(IEnumerable<ExtractImport> places)
        => places.Select((place, at) => at == 0 ? place : place with { Fallback = true });

    /// <summary>The module <paramref name="path"/> at <paramref name="place"/>: its package's <c>__init__.py</c>, then its <c>.py</c>; a relative import's own package where it names none.</summary>
    private static IEnumerable<ExtractImport> Module(Place place, string path, int line)
        => path.Length == 0
            ? [new ExtractImport(place.Form, $"{place.Beside}__init__.py", line) { Layout = place.Layout, Either = place.Either }]
            : [
                new ExtractImport(place.Form, $"{place.Beside}{path}/__init__.py", line) { Layout = place.Layout, Either = place.Either },
                new ExtractImport(place.Form, $"{place.Beside}{path}.py", line) { Layout = place.Layout, Either = place.Either },
            ];

    /// <summary>
    /// The places a module is looked for from this file, in order. Where <paramref name="dots"/> is none - a bare name - the file's own
    /// directory and each one above it to the tree's root, the nearest first: each a place of the layout (<see cref="ExtractImport.Layout"/>),
    /// which the linker passes over where its directory is a unit of the layout around the file (<see cref="FileExtract.FolderUnit"/>), so
    /// the first directory above the file's package is tried first, as a Python import is searched from above its package - the file's
    /// own directory, where it is no package and not the tree's root, one of two places the name may stand at
    /// (<see cref="ExtractImport.Either"/>), the module the file's own directory holds when it is run as a script and the one above when
    /// it is imported as a module of a namespace package; then under the roots of the layout (<see cref="CodeImportForms.Rooted"/>), the
    /// directory a package's top stands in, as <c>src/</c> of a project's packages is. Where the import is relative, beside the file, its
    /// directory once for each dot past the first.
    /// </summary>
    private IEnumerable<Place> Places(int dots)
    {
        if (dots > 0)
        {
            yield return new Place(CodeImportForms.Relative, string.Concat(Enumerable.Repeat("../", dots - 1)), Layout: false, Either: false);
            yield break;
        }

        var depth = _path.Count(character => character == '/');

        for (var level = 0; level <= depth; level++)
        {
            yield return new Place(CodeImportForms.Relative, string.Concat(Enumerable.Repeat("../", level)), Layout: true, Either: level == 0 && depth > 0);
        }

        yield return new Place(CodeImportForms.Rooted, string.Empty, Layout: false, Either: false);
    }

    /// <summary>One place a module is looked for: the form of its import, what its path is written after, whether it is a place of the layout, and whether it is one of two places the name may stand at.</summary>
    private sealed record Place(string Form, string Beside, bool Layout, bool Either);

    /// <summary>What a name an import binds reaches: a name the module shows, a name renamed, or a module.</summary>
    private enum BindingKind { Shown, Renamed, Module }

    /// <summary>
    /// A name an import binds: what it reaches, the name the code gives it, the name the module binds - or the module's path, for a
    /// module - the prefix the code reaches through it by, the prefix of the import that shows the name itself where one has a prefix,
    /// and the path of the module it is reached in.
    /// </summary>
    private sealed record Binding(BindingKind Kind, string Local, string Name, string Prefix, string? Shows, string Path)
    {
        /// <summary>The head of the names reached through it, its own: the steps of a dotted import past it, each a module.</summary>
        public ImportStep Head { get; } = new(null, Local, 0) { Imported = true, Reach = Prefix };

        /// <summary>
        /// Whether the import that binds it names its module by a bare name - <c>import pytest</c>, <c>from flask import Flask</c> -
        /// which a package installed beside the repository may answer as well as a module of its own; never one a relative import
        /// binds, which is a module beside the file.
        /// </summary>
        public bool Bare { get; init; }
    }

    /// <summary>The names the imports of one function or class bind for its code: whether it is a class, whose names are its own code's alone.</summary>
    private sealed class LocalBindings(bool @class)
    {
        /// <summary>Whether the declaration is a class.</summary>
        public bool Class { get; } = @class;

        /// <summary>The names, by the name the code reaches them by.</summary>
        public Dictionary<string, Binding> Names { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>One name of a dotted import past its head - <c>b</c> and <c>c</c> of <c>import a.b.c</c> - a module reached through the names to it.</summary>
    private sealed class ImportStep(ImportStep? before, string name, int line)
    {
        /// <summary>The step it is a name past; none for a binding's head.</summary>
        public ImportStep? Before { get; } = before;

        /// <summary>Its name.</summary>
        public string Name { get; } = name;

        /// <summary>The line of the import that names it.</summary>
        public int Line { get; } = line;

        /// <summary>The steps past it, by name.</summary>
        public Dictionary<string, ImportStep> Next { get; } = new(StringComparer.Ordinal);

        /// <summary>Whether the file's extract holds an import of its module.</summary>
        public bool Imported { get; set; }

        /// <summary>The prefix its module is reached by, once found (<see cref="Reached"/>): the binding's for its head, and each step's the one before it with its name.</summary>
        public string? Reach { get; set; }
    }

    // ---- What a body binds and calls ----

    /// <summary>
    /// Each name the tokens from <paramref name="from"/> to <paramref name="to"/> bind as locals, as Python binds them: each name of the
    /// target lists a statement assigns or annotates (<see cref="TargetsAt"/>: <c>x = ...</c>, <c>x: int</c>, <c>a, *b = ...</c>,
    /// <c>(a, b) = ...</c>), a name before an <c>=</c> or an assigning operator at no group's depth, a walrus's name at any depth, a
    /// <c>for</c>'s targets, the names <c>as</c> gives, a <c>case</c>'s captures (<see cref="_captures"/>) - and the names a
    /// <c>global</c> or a <c>nonlocal</c> declares bound nowhere here, wherever they are written. What binds names of its own is passed
    /// over: a declaration the tokens hold, whole and at once (<see cref="_declared"/>); an import, whose names the code is bound to
    /// through it (<see cref="BindingOf"/>); and a lambda's parameters and a comprehension's targets, which the run of its own code binds
    /// (<see cref="CallAt"/>). Each token is looked at once.
    /// </summary>
    private HashSet<string> Bind(int from, int to)
    {
        var bound = new HashSet<string>(StringComparer.Ordinal);
        var outer = new HashSet<string>(StringComparer.Ordinal);
        var comprehensions = ComprehensionsOf(_scan);
        var depth = 0;

        for (var at = from; at < to && at < _scan.Count; at = _scan.Next(at))
        {
            if (_declared.TryGetValue(at, out var past))
            {
                at = past - 1;
                continue;
            }

            if (_captures.Contains(_scan[at].Start))
            {
                bound.Add(Text(at));
                continue;
            }

            // A statement's target lists: each name bound, and the reading goes on from the value, past what the targets and an
            // annotation write - no name of an annotation is bound by the = after it.
            if (depth == 0 && StatementHead(at) && AssignedFrom(at, to, bound) is var value && value > at)
            {
                at = value - 1;
                continue;
            }

            if (_scan.Opens(at))
            {
                depth++;
                continue;
            }

            if (_scan.Is(at, ")") || _scan.Is(at, "]") || _scan.Is(at, "}"))
            {
                depth = Math.Max(0, depth - 1);
                continue;
            }

            if (!_scan.IsName(at))
            {
                continue;
            }

            var word = Text(at);

            if (Words.Contains(word))
            {
                if (word == "lambda")
                {
                    // A lambda's parameters are its own: passed over to its colon.
                    at = Math.Min(ScanCalls.TopLevel(_scan, _scan.Next(at), to, ":", null), to) - 1;
                }
                else if ((word == "for" && !comprehensions.Fors.Contains(at)) || word == "as")
                {
                    at = BindTargets(at, to, bound);
                }
                else if (word == "import")
                {
                    at = StatementStop(at, to) - 1;
                }
                else if (word is "global" or "nonlocal")
                {
                    for (var index = _scan.Next(at); index < to && index < _scan.Count && (_scan.IsName(index) || _scan.Is(index, ",")); index = _scan.Next(index))
                    {
                        if (_scan.IsName(index))
                        {
                            outer.Add(Text(index));
                        }
                    }
                }

                continue;
            }

            // An attribute - self.x = ... - binds no local; a keyword argument's name, or a default's, inside a group binds nothing; and
            // the type an annotation of any other target names - T of self.x: T = ... - is the annotation's, no target.
            var next = _scan.Next(at);
            var before = _scan.Previous(at);

            if (next >= _scan.Count || _scan.Is(before, ".") || (_scan.Is(before, ":") && !_headerColons.Contains(before)))
            {
                continue;
            }

            if (_scan.Is(next, ":=") || (depth == 0 && (_scan.Is(next, "=") || (_scan[next].Kind == ScanTokenKind.Punctuation && Augments.Contains(Text(next))))))
            {
                bound.Add(word);
            }
        }

        bound.ExceptWith(outer);
        return bound;
    }

    /// <summary>
    /// Where the value of the statement whose first token is at <paramref name="at"/> begins, where the statement assigns or annotates
    /// target lists before <paramref name="to"/> - each name of them added to <paramref name="bound"/>; the statement's end where it
    /// annotates and gives no value; <paramref name="at"/> where it assigns none.
    /// </summary>
    private int AssignedFrom(int at, int to, HashSet<string> bound)
    {
        var stop = StatementStop(at, to);
        var value = at;

        for (var target = at; target < stop && TargetsAt(target, stop, attributes: false) is { } targets; target = targets.Value)
        {
            foreach (var name in targets.Names)
            {
                bound.Add(Text(name.At));
            }

            value = targets.Value < 0 ? stop : targets.Value;

            if (targets.Value < 0)
            {
                break;
            }
        }

        return value;
    }

    /// <summary>
    /// Whether the token at <paramref name="at"/> begins a statement: a logical line's first, or the first after a <c>;</c> or a header's
    /// colon (<see cref="_headerColons"/>) - never the first after an annotation's colon, which is the annotation's.
    /// </summary>
    private bool StatementHead(int at)
        => _lineStarts[_lineOf[at]] == at || _scan.Is(_scan.Previous(at), ";") || _headerColons.Contains(_scan.Previous(at));

    /// <summary>Where the simple statement the token at <paramref name="at"/> is in ends: at the next <c>;</c> at its own depth or where its logical line does; never past <paramref name="to"/>.</summary>
    private int StatementStop(int at, int to)
    {
        for (var index = _scan.Next(at); index < to && index < _scan.Count; index = _scan.Opens(index) ? _scan.Past(index) : _scan.Next(index))
        {
            if (_scan.Is(index, ";") || _lineOf[index] != _lineOf[at])
            {
                return index;
            }
        }

        return Math.Min(to, _scan.Count);
    }

    /// <summary>
    /// The names a <c>for</c>'s targets or an <c>as</c> bind from the keyword at <paramref name="at"/> - a target in parentheses or
    /// brackets among them, <c>for (a, b) in</c>, <c>as (x, y)</c> - added to <paramref name="bound"/>; answers the index the reading goes
    /// on from.
    /// </summary>
    private int BindTargets(int at, int to, HashSet<string> bound)
    {
        var next = _scan.Next(at);

        if (Is(at, "as"))
        {
            if (_scan.IsName(next))
            {
                bound.Add(Text(next));
                return next;
            }

            if ((_scan.Is(next, "(") || _scan.Is(next, "[")) && _scan.Match(next) is var close and > 0)
            {
                for (var index = _scan.Next(next); index < close; index = _scan.Next(index))
                {
                    if (_scan.IsName(index) && !Words.Contains(Text(index)))
                    {
                        bound.Add(Text(index));
                    }
                }

                return close;
            }

            return at;
        }

        // for: its targets to the in, at any depth.
        var stop = Math.Min(ScanCalls.TopLevel(_scan, next, to, "in", null), to);

        for (var index = next; index < stop && index < _scan.Count; index = _scan.Next(index))
        {
            if (_scan.IsName(index) && !Words.Contains(Text(index)))
            {
                bound.Add(Text(index));
            }
        }

        return stop - 1;
    }

    /// <summary>
    /// The comprehensions of <paramref name="scan"/>: each bracket that opens one - a group a <c>for</c> stands in directly, as only a
    /// comprehension's can - and each such <c>for</c>, by index; found once a text, in one pass with a stack of the groups open.
    /// </summary>
    private Comprehensions ComprehensionsOf(ScannedText scan)
    {
        if (_comprehensions.TryGetValue(scan, out var found))
        {
            return found;
        }

        found = new Comprehensions([], []);
        var open = new Stack<int>();

        for (var at = 0; at < scan.Count; at++)
        {
            if (scan.Opens(at))
            {
                if (scan.Match(at) > at)
                {
                    open.Push(at);
                }

                continue;
            }

            if (open.TryPeek(out var top) && scan.Match(top) == at)
            {
                open.Pop();
                continue;
            }

            if (scan.Is(at, "for") && open.TryPeek(out var holder))
            {
                found.Openers.Add(holder);
                found.Fors.Add(at);
            }
        }

        return _comprehensions[scan] = found;
    }

    /// <summary>The brackets of a text that open a comprehension, and the <c>for</c>s of them, by index.</summary>
    private sealed record Comprehensions(HashSet<int> Openers, HashSet<int> Fors);

    /// <summary>The names the comprehension the bracket at <paramref name="open"/> opens binds: each of its own <c>for</c>s' targets, to its <c>in</c>.</summary>
    private static List<string> ComprehensionTargets(ScannedText scan, int open, int close)
    {
        var names = new List<string>();

        for (var at = scan.Next(open); at < close && at < scan.Count;)
        {
            if (scan.Is(at, "for"))
            {
                var stop = Math.Min(ScanCalls.TopLevel(scan, scan.Next(at), close, "in", null), close);

                for (var index = scan.Next(at); index < stop; index = scan.Next(index))
                {
                    if (scan.IsName(index) && !Words.Contains(scan.Text(index)))
                    {
                        names.Add(scan.Text(index));
                    }
                }

                at = stop;
                continue;
            }

            at = scan.Opens(at) ? Math.Min(scan.Past(at), close) : scan.Next(at);
        }

        return names;
    }

    /// <summary>The names a lambda's <paramref name="parameters"/> bind: each parameter's, starred or not, and none its default reads.</summary>
    private static List<string> LambdaParameters(ScannedText scan, List<(int Start, int End)> parameters)
    {
        var names = new List<string>();

        foreach (var (start, _) in parameters)
        {
            var name = start;

            while (scan.Is(name, "*") || scan.Is(name, "**"))
            {
                name = scan.Next(name);
            }

            if (scan.IsName(name))
            {
                names.Add(scan.Text(name));
            }
        }

        return names;
    }

    /// <summary>
    /// Where the body of the lambda whose colon is at <paramref name="colon"/> ends: at the first comma, <c>;</c>, closing bracket or
    /// comprehension's <c>for</c> at its own depth, or where its logical line does - every group within passed over; never past
    /// <paramref name="to"/>.
    /// </summary>
    private int LambdaEnd(ScannedText scan, int colon, int to)
    {
        var own = ReferenceEquals(scan, _scan);

        for (var at = scan.Next(colon); at < to && at < scan.Count;)
        {
            if (scan.Is(at, ",") || scan.Is(at, ";") || scan.Is(at, ")") || scan.Is(at, "]") || scan.Is(at, "}") || scan.Is(at, "for")
                || (scan.Is(at, "async") && scan.Is(scan.Next(at), "for")) || (own && _lineOf[at] != _lineOf[colon]))
            {
                return at;
            }

            at = scan.Opens(at) ? scan.Past(at) : scan.Next(at);
        }

        return Math.Min(to, scan.Count);
    }

    /// <summary>
    /// Each call the tokens of <paramref name="text"/> from <paramref name="from"/> to <paramref name="to"/> make, from the node of
    /// <paramref name="key"/>: a reference by its name and arity, qualified by its receiver where the code says, a class a call makes
    /// where the code writes a capitalized name, a type an <c>isinstance</c> or a bare name names, each attribute and name the code reads
    /// without calling it, each member of a chain on its way, and what it cannot bind marked dynamic. A string's fields, a lambda's body
    /// and a comprehension are read as runs of their own, the first on top, before the run they stand in goes on - a lambda's and a
    /// comprehension's with the names they bind bound while they are read (<see cref="_inner"/>). A decorator's run is given the name it
    /// calls with the function it decorates (<paramref name="callee"/>, <see cref="Callee"/>), a call of what that name binds.
    /// </summary>
    private void Calls(InterpolatedText text, int from, int to, QualifiedName key, HashSet<string> bound, string? owner, IReadOnlySet<string>? own = null, int callee = -1)
    {
        var runs = new Stack<CodeRun>();
        runs.Push(new CodeRun(text, from, to, key, bound) { Own = own, Callee = callee });

        while (runs.TryPeek(out var run))
        {
            if (!run.Entered)
            {
                run.Entered = true;

                foreach (var name in run.Binds ?? [])
                {
                    _inner[name] = _inner.GetValueOrDefault(name) + 1;
                }
            }

            if (run.At >= run.To || run.At >= run.Text.Scan.Count)
            {
                runs.Pop();

                foreach (var name in run.Binds ?? [])
                {
                    if (--_inner[name] == 0)
                    {
                        _inner.Remove(name);
                    }
                }

                continue;
            }

            run.At = CallAt(run, owner, runs) + 1;
        }
    }

    /// <summary>One run of code <see cref="Calls"/> reads (<see cref="ScanRun{TKey, TLocals}"/>), and the names it binds of its own - a lambda's parameters, a comprehension's targets - while it is read.</summary>
    private sealed class CodeRun : ScanRun<QualifiedName, HashSet<string>>
    {
        public CodeRun(InterpolatedText text, int from, int to, QualifiedName key, HashSet<string> bound)
            : base(text, from, to, key, bound)
        {
        }

        public CodeRun(Func<InterpolatedText> scanned, QualifiedName key, HashSet<string> bound)
            : base(scanned, key, bound)
        {
        }

        /// <summary>The names it binds of its own while it is read; none where it binds none.</summary>
        public IReadOnlyList<string>? Binds { get; init; }

        /// <summary>
        /// The names the class whose own code it is declares - a class's body outside its declarations, and a string's field or a
        /// call's argument there - read as the class's own; none for any other code, a lambda's and a comprehension's in a class
        /// among it, which see no name of the class, as Python reads them.
        /// </summary>
        public IReadOnlySet<string>? Own { get; init; }

        /// <summary>
        /// Where it is a decorator's code, the index of the name the decorator calls with the function it decorates: a call of what it
        /// binds, of one argument where it is written alone; -1 for any other code.
        /// </summary>
        public int Callee { get; init; } = -1;

        /// <summary>Whether it was reached, and its names bound.</summary>
        public bool Entered { get; set; }
    }

    /// <summary>Reads the token where <paramref name="run"/> stands, as <see cref="Calls"/> reads each, stacking on <paramref name="runs"/> the code it holds that is read as code of its own; answers the index of the last token it read.</summary>
    private int CallAt(CodeRun run, string? owner, Stack<CodeRun> runs)
    {
        var text = run.Text;
        var scan = text.Scan;
        var at = run.At;
        var key = run.Key;
        var bound = run.Locals;
        var token = scan[at];

        if (ReferenceEquals(scan, _scan))
        {
            // A declaration the code holds - a nested function's or class's, with its decorators - is read by its own reading: passed
            // over whole, at once.
            if (_declared.TryGetValue(at, out var past))
            {
                return past - 1;
            }

            // What a statement leaves to the run of the code it stands in - an except's types, an annotation, a class's value - read
            // where the run reaches it, once every name of the file is bound (<see cref="Defer"/>).
            if (_deferred.Remove(token.Start, out var deferred))
            {
                deferred.Read(runs, run);
                return deferred.To - 1;
            }

            // What a declaration keeps to itself: read by its own reading, by no run of the body it stands in.
            if (_kept.Contains(token.Start))
            {
                return at;
            }
        }

        if (token.Kind == ScanTokenKind.Text)
        {
            // The literal's constant parts - an f-string's fields are code, read as code - are its data.
            foreach (var (value, part) in scan.LiteralParts(token))
            {
                _extract.Literal(value, part, key);
            }

            if (text.Interpolations.TryGetValue(token.Start, out var spans))
            {
                for (var index = spans.Count - 1; index >= 0; index--)
                {
                    var (start, end) = spans[index];
                    runs.Push(new CodeRun(() => PythonLexer.Scan(scan.Source, start, end, text.Lines, text.Closes), key, bound) { Own = run.Own });
                }
            }

            return at;
        }

        // A comprehension - [f(x) for x in xs], a generator a call's parentheses hold - binds its targets of its own: read as a run of
        // its own, its targets bound while it is.
        if (ComprehensionsOf(scan).Openers.Contains(at) && scan.Match(at) is var close and > 0)
        {
            runs.Push(new CodeRun(text, scan.Next(at), close, key, bound) { Binds = ComprehensionTargets(scan, at, close) });
            return close;
        }

        if (token.Kind != ScanTokenKind.Name)
        {
            return at;
        }

        var word = scan.Text(at);
        var line = token.Line;
        var previous = scan.Previous(at);
        var next = scan.Next(at);

        // What no reading of names reaches: an attribute the code computes, a module it names by a string, code a string holds - each
        // marked with the kind that tells the limit it shows (<see cref="CodeGraphLimit.Signs"/>).
        if (scan.Is(next, "(") && (Computed.Contains(word) || word is "eval" or "exec" or "__import__"))
        {
            if (Computed.Contains(word))
            {
                _extract.Dynamic(line, $"an attribute reached by {word}: a name the code computes", ComputedAttribute);
            }
            else if (word == "__import__")
            {
                _extract.Dynamic(line, "a dynamic import: a module the code names by a computed name", DynamicImport);
            }
            else
            {
                _extract.Dynamic(line, "a call of eval or exec: code a string holds", ComputedCode);
            }
        }

        var chain = Chain(scan, at);
        var last = chain[^1];
        var after = scan.Next(last);
        var called = scan.Is(after, "(");

        // A module's __all__ changed through a method of its own - __all__.append("x"), .extend, .sort, in an f-string's field as
        // anywhere: no list of literals names what a * import of the module binds. What only reads it, len(__all__), is no change.
        if (word == "__all__" && chain.Count > 1 && called)
        {
            ChangedAll(line);
        }
        // super(...).member: the member is reached through the base (<see cref="Receivers"/>); the arguments are the enclosing code's.
        if (word == "super" && !scan.Is(previous, "."))
        {
            return scan.Is(next, "(") ? scan.CloseOf(next) - 1 : at;
        }

        // The name of a keyword argument - f(owner=x) - names no declaration: the value is the code's.
        if (scan.Is(next, "=") && !Words.Contains(word))
        {
            return at;
        }

        // A chain a call's or a subscript's result reaches - f().a.b, x[0].a - each name of it read, the first through a value by no
        // name the file types, and through the base where super(...) reached it; each after it a value. A member of any other chain
        // is read when the chain's head is.
        if (scan.Is(previous, "."))
        {
            var before = scan.Previous(previous);

            if (!scan.Is(before, ")") && !scan.Is(before, "]"))
            {
                return at;
            }

            var reached = scan.Is(before, ")") && scan.Match(before) is var group && group >= 0 && scan.Is(scan.Previous(group), "super")
                ? new ExtractQualifier(CodeQualifierKinds.Base)
                : new ExtractQualifier(CodeQualifierKinds.Value);

            for (var index = 0; index < chain.Count; index++)
            {
                Member(scan, scan.Text(chain[index]), scan.Next(chain[index]), scan[chain[index]].Line, key, index == 0 ? reached : new ExtractQualifier(CodeQualifierKinds.Value));
            }

            return last;
        }

        if (Words.Contains(word))
        {
            return word == "lambda" ? Lambda(run, at, runs) : at;
        }

        // A name the language answers to that the file binds nowhere of its own (<see cref="Shadows"/>) names no declaration of the
        // repository's; the types isinstance and issubclass are given are named.
        if (Builtins.Contains(word) && !Shadows(word, bound, key))
        {
            // type(self)(...): the class the method's receiver is, made again (<see cref="Remade"/>); what the call is given, the code's.
            if (word == "type" && scan.Is(next, "(") && scan.Next(next) is var given && scan.IsName(given) && scan.Text(given) == "self"
                && scan.Next(given) == scan.CloseOf(next) && scan.Is(scan.Next(scan.CloseOf(next)), "(") && Remade(key, owner, line))
            {
                return scan.CloseOf(next);
            }

            return (word == "isinstance" || word == "issubclass") && scan.Is(next, "(") ? InstanceOf(run, at, runs) : at;
        }

        // A name the language keeps to itself - __name__, __doc__: no value the code reads.
        if (word.StartsWith("__", StringComparison.Ordinal))
        {
            return at;
        }

        // The name a decorator calls with the function it decorates (<see cref="Callee"/>): a call of what it binds, of one argument where
        // it is written alone.
        var decorates = run.Callee == last && ReferenceEquals(scan, _scan);
        var calls = called || decorates;

        // cls(...) in a method that takes its class: the class the method's receiver is, made again (<see cref="Remade"/>), never a call
        // through a function value.
        if (chain.Count == 1 && called && word == "cls" && Remade(key, owner, line))
        {
            return at;
        }

        // A name a body binds - a local, a parameter, a lambda's or a comprehension's own - is the body's, nearer than any import or
        // declaration of its name: a call through its value is dynamic, and a read of it is no use of a declaration.
        if (chain.Count == 1 && Local(word, bound))
        {
            if (calls)
            {
                _extract.Dynamic(line, $"a call through the function value {word}", FunctionValue);
            }

            return at;
        }

        // A type parameter of the declaration the code is of, or of one around it - T of def first[T](...) - is the code's own, nearer
        // than any import or declaration of its name, and no type of the repository's (<see cref="TypeParametersOf"/>). What a head runs
        // around its declaration - a decorator, a default - runs outside the declaration's own, as Python evaluates it; a class's bases
        // inside its own (<see cref="_typed"/>).
        var parameter = TypeParametersOf(_extract.Around && !_typed ? _extract.Node(key).Parent ?? key : key).Contains(word);

        if (chain.Count == 1 && parameter)
        {
            return at;
        }

        // A name the class declares, read by the class's own code, is the class's own, nearer than an import of its name (<see cref="CodeRun.Own"/>).
        var mine = run.Own?.Contains(word) == true;
        var through = mine || parameter ? null : BindingOf(word, key);

        // A module's name alone names no declaration.
        if (chain.Count == 1 && through is { Kind: BindingKind.Module })
        {
            return at;
        }

        // importlib.import_module("x"): a module the code names by a string, no import the graph can bind, and no call of the repository's.
        if (chain.Count == 2 && word == "importlib" && scan.Text(last) == "import_module" && called)
        {
            _extract.Dynamic(line, "a dynamic import: a module the code names by a computed name", DynamicImport);
            return last;
        }

        // A name an import binds is read as what it reaches, never as itself. What each member of a chain is reached through is found
        // once for the chain, each from the one before it (<see cref="Receivers"/>).
        // The names of a module path an import reaches are no members (<see cref="StepsOf"/>): what the chain passes begins after them.
        var target = chain.Count == 1 && through is { Kind: not BindingKind.Module } ? through.Name : scan.Text(last);
        var passed = chain.Count > 1 && through is not null && !Local(word, bound) ? StepsOf(scan, chain, through).Count : 1;
        var receivers = chain.Count > 1 ? Receivers(scan, chain, owner, bound, through, passed) : [];
        var qualifier = chain.Count > 1 ? receivers[^1] : AloneQualifier(through, mine);

        // The head of a chain a member is reached through - CONFIG of CONFIG.get(), Cart of Cart.parse() - is read as a name alone is;
        // a local, a type parameter, a receiver - self, cls - and a module's name are none.
        if (chain.Count > 1 && word is not ("self" or "cls") && !parameter && !Local(word, bound) && through is not { Kind: BindingKind.Module })
        {
            ReadAlone(word, through, mine, line, key);
        }

        // Each member the chain passes on its way to its last - items of self.items.append(x) - is read, a use of it, qualified as the
        // chain to it is (<see cref="Receivers"/>): the first as its head says, each after it a value by no name the file types; the
        // first past a module path reached through the module, and each after it through what the names before it stand for there.
        for (var index = passed; index < chain.Count - 1; index++)
        {
            Member(scan, scan.Text(chain[index]), scan.Next(chain[index]), scan[chain[index]].Line, key, receivers[index - passed]);
        }

        // A decorator's name is called with the function it decorates: of one argument where it is written alone. One an import of a
        // package binds asks whether the repository declares it (<see cref="ExtractReference.Probe"/>) - a framework's, a decorator
        // library's - as a name the repository's own code binds names it.
        if (calls)
        {
            var makes = Capital(target) && !_functions.Contains(target);
            _extract.Reference(key, makes ? CodeEdgeKinds.Instantiates : CodeEdgeKinds.Calls, target, line, makes ? null : called ? ScanCalls.CallArity(scan, after, scan.CloseOf(after), PythonMemberCalls) : 1, qualifier,
                probe: decorates && through is { Bare: true });
        }
        else if (chain.Count == 1)
        {
            ReadAlone(word, through, mine, line, key);
        }
        else
        {
            _extract.Reference(key, CodeEdgeKinds.Uses, target, line, qualifier: qualifier);
        }

        return last;
    }

    /// <summary>
    /// Makes, from the node of <paramref name="key"/>, an instantiates of the class the receiver of the method it is is -
    /// <c>cls(...)</c> in a class method, <c>type(self)(...)</c> in any - where the code is the body of a method of the class
    /// <paramref name="owner"/> names that takes its receiver: a reference to that class's own node, by its id
    /// (<see cref="CodeQualifierKinds.Own"/>), as <c>self.f()</c> calls a member of the class through <c>this</c>. A <c>this</c>
    /// qualifier reaches the members of the type and of its bases, never the type itself, so the class is named by its node. Whether
    /// it made one.
    /// </summary>
    private bool Remade(QualifiedName key, string? owner, int line)
    {
        if (owner is null || _extract.Around || !_methods.TryGetValue(key, out var info))
        {
            return false;
        }

        _extract.Reference(key, CodeEdgeKinds.Instantiates, Simple(info.Qualified.Whole()), line, qualifier: new ExtractQualifier(CodeQualifierKinds.Own, info.Key));
        return true;
    }

    /// <summary>
    /// The name <paramref name="word"/> the code reads alone, without calling it, from the node of <paramref name="key"/>: a reference
    /// where it is written as a class's and the file declares it as a type, nothing binding it of another module's - it names a type of
    /// this one - and a use otherwise, which reaches a type as well as a function, a constant or a member: a name an import binds is
    /// read as what it reaches (<paramref name="through"/>), whatever it is, <c>CONFIG</c> and <c>MAX_RETRIES</c> as much as a class,
    /// qualified as <see cref="AloneQualifier"/> says.
    /// </summary>
    private void ReadAlone(string word, Binding? through, bool mine, int line, QualifiedName key)
    {
        var target = through is { Kind: not BindingKind.Module } ? through.Name : word;
        var type = through is null && Capital(target) && _classes.Contains(target);
        _extract.Reference(key, type ? CodeEdgeKinds.References : CodeEdgeKinds.Uses, target, line, qualifier: AloneQualifier(through, mine));
    }

    /// <summary>
    /// What a name written alone is qualified by: the class's own where the class's own code reads a name it declares
    /// (<paramref name="mine"/>), as a class's body sees its own names and none of its bases'; where an import binds it
    /// (<paramref name="through"/>), the prefix of the import that shows it, where one has a prefix (<see cref="Itself"/>); none
    /// otherwise, looked for in the scopes it is written in.
    /// </summary>
    private static ExtractQualifier? AloneQualifier(Binding? through, bool mine)
        => mine ? new ExtractQualifier(CodeQualifierKinds.This) : through is not null ? Itself(through) : null;

    /// <summary>
    /// Whether the code binds <paramref name="name"/>, a name the language answers to, of its own, so the name is the code's and no
    /// builtin: a local of the body - <paramref name="bound"/>'s - a lambda's or a comprehension's own, a name the module's top binds,
    /// a function or a class the file declares outside every class, a name an import binds for the code of the node of
    /// <paramref name="key"/> (<see cref="BindingOf"/>).
    /// </summary>
    private bool Shadows(string name, HashSet<string>? bound, QualifiedName key)
        => Local(name, bound) || _bound.Contains(name) || _free.Contains(name) || BindingOf(name, key) is not null;

    /// <summary>
    /// Whether <paramref name="name"/> is a local of the code under way: one its body binds - <paramref name="bound"/>'s, a parameter
    /// among them - one a lambda or a comprehension it is inside binds (<see cref="_inner"/>), or one a function around it binds
    /// (<see cref="_closure"/>), as Python looks a name up through the functions around a body before the module.
    /// </summary>
    private bool Local(string name, HashSet<string>? bound)
        => bound?.Contains(name) == true || _inner.ContainsKey(name) || _closure.ContainsKey(name);

    /// <summary>
    /// The lambda at <paramref name="at"/> of <paramref name="run"/>'s code: its body a run of its own, its parameters bound while it
    /// is read, and each parameter's default a run of the code around it, read before the body as Python evaluates it - each on top,
    /// in the order the text writes them. Answers the index of the last token of its body, where the code around it goes on.
    /// </summary>
    private int Lambda(CodeRun run, int at, Stack<CodeRun> runs)
    {
        var scan = run.Text.Scan;
        var colon = ScanCalls.TopLevel(scan, scan.Next(at), run.To, ":", null);

        if (colon >= run.To || colon >= scan.Count)
        {
            return at;
        }

        var end = LambdaEnd(scan, colon, run.To);
        var parameters = ScanCalls.Split(scan, scan.Next(at), colon, null);
        runs.Push(new CodeRun(run.Text, scan.Next(colon), end, run.Key, run.Locals) { Binds = LambdaParameters(scan, parameters) });

        for (var index = parameters.Count - 1; index >= 0; index--)
        {
            var (start, stop) = parameters[index];

            if (ScanCalls.TopLevel(scan, start, stop, "=", null) is var equal && equal < stop)
            {
                runs.Push(new CodeRun(run.Text, scan.Next(equal), stop, run.Key, run.Locals) { Own = run.Own });
            }
        }

        return end - 1;
    }

    /// <summary>
    /// The <c>isinstance</c> or <c>issubclass</c> at <paramref name="at"/> of <paramref name="run"/>'s code: the value it tests a run of
    /// code of its own, read on top; what it tests against the types it names (<see cref="TypeNames"/>) where it is written as types -
    /// names, dotted or not, a tuple or a union of them - and code like any other where it is not, a call that answers the types among
    /// it. Answers the index of its closing parenthesis; where it is given one argument alone, of its name, the code around reading it.
    /// </summary>
    private int InstanceOf(CodeRun run, int at, Stack<CodeRun> runs)
    {
        var scan = run.Text.Scan;
        var open = scan.Next(at);
        var close = scan.CloseOf(open);
        var comma = ScanCalls.TopLevel(scan, scan.Next(open), close, ",", null);

        if (comma >= close)
        {
            return at;
        }

        var types = scan.Next(comma);

        if (IsTypeList(scan, types, close))
        {
            TypeNames(scan, types, close, run.Key, null);
        }
        else
        {
            runs.Push(new CodeRun(run.Text, types, close, run.Key, run.Locals) { Own = run.Own });
        }

        runs.Push(new CodeRun(run.Text, scan.Next(open), comma, run.Key, run.Locals) { Own = run.Own });
        return close;
    }

    /// <summary>
    /// Whether the tokens of <paramref name="scan"/> from <paramref name="from"/> to <paramref name="to"/> are written as types alone:
    /// names that are no word of the language, dots, commas, parentheses and <c>|</c> - <c>Cart</c>, <c>models.Cart</c>,
    /// <c>(A, B)</c>, <c>A | B</c> - and nothing else, no call, subscript or literal, and at least one name.
    /// </summary>
    private static bool IsTypeList(ScannedText scan, int from, int to)
    {
        var named = false;

        for (var at = from; at < to && at < scan.Count; at = scan.Next(at))
        {
            if (scan.IsName(at) && !Words.Contains(scan.Text(at)))
            {
                named = true;

                if (scan.Is(scan.Next(at), "("))
                {
                    return false;
                }

                continue;
            }

            if (!(scan.Is(at, ".") || scan.Is(at, ",") || scan.Is(at, "(") || scan.Is(at, ")") || scan.Is(at, "|")))
            {
                return false;
            }
        }

        return named;
    }

    /// <summary>
    /// The names joined by dots that begin at <paramref name="at"/> in <paramref name="scan"/>, each the token after a dot: the chain a member
    /// is reached through, its head first and the member last.
    /// </summary>
    private static List<int> Chain(ScannedText scan, int at)
    {
        var names = new List<int> { at };

        while (scan.Is(scan.Next(names[^1]), ".") && scan.IsName(scan.Next(scan.Next(names[^1]))))
        {
            names.Add(scan.Next(scan.Next(names[^1])));
        }

        return names;
    }

    /// <summary>
    /// What each member reached through the chain <paramref name="chain"/> of <paramref name="scan"/> from the one at
    /// <paramref name="first"/> to its last is qualified by, in order - each found from the one before it, so a chain of any length is
    /// read once. Where the head <paramref name="through"/> - a name an import binds, and no local of the body - is, one
    /// rule for every name an import binds, a call's, a use's, an annotation's and a base's: the prefix of the import of the module the
    /// longest run of the names before the member reaches - <c>import a.b.c</c>, then <c>a.b.c.f()</c> - where that run is all of them;
    /// else the type or scope that module's names past that run stand for, written with them - <c>a.Cart.parse()</c>, or
    /// <c>shape.Shape</c> of <c>from pkg import shape</c>. A step of a dotted import the code reaches through, <c>a.b</c> of
    /// <c>import a.b.c</c>, is made an import of its own the first time it is reached, so what the extract holds of a dotted import grows
    /// with the chains the code writes and never with the square of the import's names. Else the first member is qualified by its head
    /// - <c>self</c> and <c>cls</c> in a class the type's own, <c>super()</c> its base's, a local of the body - <paramref name="bound"/>'s,
    /// a lambda's, a comprehension's or a closure's - a value by no name the file types, nearer than any import or class of its name, a
    /// class the file declares named as a type, and any other a value by no name the file types - and each after it is reached through
    /// the member before it, a value by no name the file types.
    /// </summary>
    private ExtractQualifier[] Receivers(ScannedText scan, List<int> chain, string? owner, HashSet<string>? bound, Binding? through, int first = 1)
    {
        var qualifiers = new ExtractQualifier[chain.Count - first];
        var head = scan.Text(chain[0]);
        Work += chain.Count;
        var local = bound is not null && Local(head, bound);

        if (through is not null && !local)
        {
            var steps = StepsOf(scan, chain, through);
            string? named = null;

            for (var member = first; member < chain.Count; member++)
            {
                if (member <= steps.Count)
                {
                    qualifiers[member - first] = new ExtractQualifier(CodeQualifierKinds.Prefix, QualifiedName.Of(Reached(steps[member - 1], through)));
                    continue;
                }

                // The module's names the run of steps does not reach, to the member: written once, each member's the one before it with its name.
                named = named is null
                    ? string.Join('.', [Reached(steps[^1], through), .. chain.Skip(steps.Count).Take(member - steps.Count).Select(index => scan.Text(index))])
                    : $"{named}.{scan.Text(chain[member - 1])}";
                qualifiers[member - first] = new ExtractQualifier(CodeQualifierKinds.Type, QualifiedName.Of(named));
            }

            return qualifiers;
        }

        // self.lines.append(): what a member past the first is reached through is the member before it, a value by no name the file
        // types; the type's own and a base's are reached by one name alone.
        for (var member = first; member < chain.Count; member++)
        {
            qualifiers[member - first] = member > 1 ? new ExtractQualifier(CodeQualifierKinds.Value)
                : (head == "self" || head == "cls") && owner is not null ? new ExtractQualifier(CodeQualifierKinds.This)
                : head == "super" ? new ExtractQualifier(CodeQualifierKinds.Base)
                : !local && _classes.Contains(head) ? new ExtractQualifier(CodeQualifierKinds.Type, QualifiedName.Of(head))
                : new ExtractQualifier(CodeQualifierKinds.Value);
        }

        return qualifiers;
    }

    /// <summary>
    /// The member <paramref name="name"/> at the end of a chain a value reaches - <c>x.f()</c>, <c>x.f</c> - read or called by its name with how
    /// many arguments it is given, qualified by <paramref name="qualifier"/>; a name the language keeps to itself none.
    /// </summary>
    private void Member(ScannedText scan, string name, int next, int line, QualifiedName key, ExtractQualifier qualifier)
    {
        // A name the language keeps to itself - __init__, __name__ - reached through a value the file does not type: none; through
        // super(), the base's own, a call of it the one way a constructor reaches its base's.
        if (name.StartsWith("__", StringComparison.Ordinal) && qualifier.Kind != CodeQualifierKinds.Base)
        {
            return;
        }

        if (scan.Is(next, "("))
        {
            var makes = Capital(name);
            _extract.Reference(key, makes ? CodeEdgeKinds.Instantiates : CodeEdgeKinds.Calls, name, line, makes ? null : ScanCalls.CallArity(scan, next, scan.CloseOf(next), PythonMemberCalls), qualifier);
        }
        else
        {
            _extract.Reference(key, CodeEdgeKinds.Uses, name, line, qualifier: qualifier);
        }
    }

    /// <summary>
    /// A reference of <paramref name="kind"/> from the node of <paramref name="key"/> to the member the chain <paramref name="chain"/> of
    /// <paramref name="scan"/> ends in, read as the code reaches it - through what the import its head is binds, in any text the file's
    /// code is - on <paramref name="line"/> where it is given, else the chain's own.
    /// </summary>
    private void Mention(ScannedText scan, List<int> chain, QualifiedName key, string kind, int? line = null)
    {
        var target = scan.Text(chain[^1]);
        var through = BindingOf(scan.Text(chain[0]), key);

        // A module's name alone names no declaration, as a call's reading says (<see cref="CallAt"/>).
        if (chain.Count == 1 && through is { Kind: BindingKind.Module })
        {
            return;
        }

        var written = chain.Count == 1 && through is not null ? through.Name : target;
        var qualifier = chain.Count > 1 ? Receivers(scan, chain, null, null, through, chain.Count - 1)[0] : through is not null ? Itself(through) : null;
        _extract.Reference(key, kind, written, line ?? scan[chain[^1]].Line, qualifier: qualifier);
    }

    /// <summary>
    /// Each type the tokens of <paramref name="scan"/> from <paramref name="from"/> to <paramref name="to"/> name, read as a type
    /// expression for the node of <paramref name="key"/> - an annotation, a return annotation, an <c>except</c>'s types, an
    /// <c>isinstance</c>'s, a class pattern's class, a base's subscript, a type alias's value, a type parameter's bound - the one reading
    /// of a type the code names: each chain not reached through a dot whose last name is written as a class's - <c>Cart</c>,
    /// <c>models.Cart</c> - or one name the file declares as a type or an import binds, a reference to what it names, through the
    /// import its head is (<see cref="Mention"/>); a string in it the type expression it holds - <c>"Cart"</c>, <c>"models.Cart"</c> -
    /// read the same way, with the file's imports, on the string's line; the language's words and builtins, and the names
    /// <paramref name="skip"/> holds - a declaration's own type parameters - naming nothing. The values a type expression holds are no
    /// types: <c>Literal</c>'s arguments and <c>Annotated</c>'s metadata are read as code where the file's own text writes them
    /// (<see cref="Calls"/>) - so a literal there is one of the node's - and a string's text keeps its literals. Read on a stack of the
    /// texts its strings open, never a call into itself; the code a value is read as holds no type expression of its own but an
    /// <c>isinstance</c>'s, whose types are names alone.
    /// </summary>
    private void TypeNames(ScannedText scan, int from, int to, QualifiedName key, HashSet<string>? skip)
    {
        var texts = new Stack<(ScannedText Scan, int From, int To, int? Line)>();
        texts.Push((scan, from, to, null));

        while (texts.TryPop(out var piece))
        {
            var (text, start, end, line) = piece;

            for (var at = start; at < end && at < text.Count;)
            {
                if (text[at].Kind == ScanTokenKind.Text)
                {
                    foreach (var (value, part) in text.LiteralParts(text[at]))
                    {
                        var inner = PythonLexer.Scan(value).Scan;
                        texts.Push((inner, 0, inner.Count, line ?? part));
                    }

                    at = text.Next(at);
                    continue;
                }

                if (!text.IsName(at) || text.Is(text.Previous(at), "."))
                {
                    at = text.Next(at);
                    continue;
                }

                var chain = Chain(text, at);

                while (chain.Count > 1 && chain[^1] >= end)
                {
                    chain.RemoveAt(chain.Count - 1);
                }

                var head = text.Text(at);
                var named = text.Text(chain[^1]);
                var after = text.Next(chain[^1]);

                if (!Words.Contains(head) && !(chain.Count == 1 && (skip?.Contains(head) == true || TypeParametersOf(key).Contains(head) || (Builtins.Contains(head) && !Shadows(head, null, key))))
                    && (Capital(named) || (chain.Count == 1 && (_classes.Contains(head) || BindingOf(head, key) is { Kind: not BindingKind.Module }))))
                {
                    Mention(text, chain, key, CodeEdgeKinds.References, line);
                }

                // Literal["paid"], Annotated[int, Field(gt=0)]: the values it holds, past the type Annotated is given first.
                if ((named == "Literal" || named == "Annotated") && text.Is(after, "[") && text.Match(after) is var close && close > after && close < end)
                {
                    var values = text.Next(after);

                    if (named == "Annotated")
                    {
                        var comma = ScanCalls.TopLevel(text, values, close, ",", null);
                        texts.Push((text, values, comma, line));
                        values = comma < close ? text.Next(comma) : close;
                    }

                    Values(text, values, close, key, line);
                    at = text.Next(close);
                    continue;
                }

                at = after;
            }
        }
    }

    /// <summary>
    /// The values a type expression holds from <paramref name="from"/> to <paramref name="to"/> of <paramref name="scan"/>, for the node
    /// of <paramref name="key"/>: code where the file's own text writes them, read as code is (<see cref="Calls"/>); in a string's text,
    /// each string literal's value one of the node's literals, on <paramref name="line"/>.
    /// </summary>
    private void Values(ScannedText scan, int from, int to, QualifiedName key, int? line)
    {
        if (from >= to)
        {
            return;
        }

        if (ReferenceEquals(scan, _scan))
        {
            Calls(_text, from, to, key, Bind(from, to), null);
            return;
        }

        for (var at = from; at < to && at < scan.Count; at = scan.Next(at))
        {
            if (scan[at].Kind == ScanTokenKind.Text)
            {
                foreach (var (value, part) in scan.LiteralParts(scan[at]))
                {
                    _extract.Literal(value, line ?? part, key);
                }
            }
        }
    }

    /// <summary>Whether <paramref name="name"/> is written as a class's: its first letter a capital, and more than one character long.</summary>
    private static bool Capital(string name) => name.Length > 1 && char.IsAsciiLetter(name[0]) && char.IsUpper(name[0]);

    /// <summary>
    /// What a docstring says of the declaration whose suite it opens: the first sentence of the string literal that is the suite's
    /// whole first statement, as Python reads its escapes - a module's, a class's, a function's; <see langword="null"/> where the suite
    /// opens with none.
    /// </summary>
    private string? Docstring(List<(int From, int To)> suite)
        => suite.Count > 0 && suite[0].To - suite[0].From == 1 && _scan[suite[0].From].Kind == ScanTokenKind.Text && _scan.Value(suite[0].From) is { Length: > 0 } text
            ? ExtractText.Summary(ExtractText.Flat(text))
            : null;

    // ---- Parameters ----

    /// <summary>
    /// A function's parameters as its signature writes them: how many arguments it takes positionally - those required the least, those with a
    /// default more, <c>*args</c> no most - their names, where each annotation and each default stands, and those a call must give.
    /// </summary>
    private sealed record ParameterList(int Least, int? Most, IReadOnlyList<string> Names, IReadOnlyList<(int From, int To)> Annotations, IReadOnlyList<(int From, int To)> Defaults, IReadOnlyList<(string Name, int Line)> Required);

    /// <summary>Reads the parameters between <paramref name="open"/> and <paramref name="close"/>, each split from the next at the commas of their own depth.</summary>
    private ParameterList Parameters(int open, int close)
    {
        var names = new List<string>();
        var annotations = new List<(int From, int To)>();
        var defaults = new List<(int From, int To)>();
        var required = new List<(string Name, int Line)>();
        var positional = 0;
        var defaulted = 0;
        var starred = false;
        var keywordOnly = false;

        foreach (var (start, end) in ScanCalls.Split(_scan, open + 1, close, null))
        {
            var at = start;

            // / ends the positional-only parameters; * alone opens the keyword-only ones.
            if (_scan.Is(at, "/") || (_scan.Is(at, "*") && _scan.Next(at) >= end))
            {
                keywordOnly |= _scan.Is(at, "*");
                continue;
            }

            var stars = _scan.Is(at, "*") || _scan.Is(at, "**");

            if (stars)
            {
                at = _scan.Next(at);
            }

            if (!_scan.IsName(at))
            {
                continue;
            }

            names.Add(Text(at));
            var next = _scan.Next(at);
            var equals = ScanCalls.TopLevel(_scan, next, end, "=", null);
            var given = equals < end;

            if (_scan.Is(next, ":"))
            {
                annotations.Add((_scan.Next(next), given ? equals : end));
            }

            if (given)
            {
                defaults.Add((_scan.Next(equals), end));
            }

            if (stars)
            {
                starred = true;
                continue;
            }

            if (!keywordOnly)
            {
                positional++;
                defaulted += given ? 1 : 0;
            }

            if (!given)
            {
                required.Add((Text(at), _scan[at].Line));
            }
        }

        return new ParameterList(positional - defaulted, starred ? null : positional, names, annotations, defaults, required);
    }

    // ---- Names ----

    /// <summary>The module's name as the model holds it: the file's path in dots, a package's <c>__init__.py</c> its directory's, a stub's and a <c>.pyw</c>'s its module's alike.</summary>
    private string Module()
    {
        var slash = _path.LastIndexOf('/');
        var directory = slash >= 0 ? _path[..slash] : string.Empty;
        var name = CodeGraphIds.FileName(_path);

        if (_package)
        {
            return directory.Length > 0 ? directory.Replace('/', '.') : "__init__";
        }

        var stem = name.LastIndexOf('.') is var dot && dot > 0 ? name[..dot] : name;

        return (directory.Length > 0 ? directory + "/" + stem : stem).Replace('/', '.');
    }

    private static string Simple(string dotted) => dotted[(dotted.LastIndexOf('.') + 1)..];

    private QualifiedName Id(QualifiedName qualified) => qualified.Prepend(_prefix);
}
