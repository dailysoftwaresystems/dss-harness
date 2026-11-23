using System.Globalization;
using DssHarness.Core.Codebase.Languages.Scanning;

namespace DssHarness.Core.Codebase.Languages;

/// <summary>
/// One Python file's reading over the structural scanner: its module, a namespace named by the dotted path the file is
/// written at - a package's <c>__init__.py</c> its directory's - and one open scope, every name of which an importer may
/// reach; what stands at its top and in its classes - each <c>class</c> a type, each <c>def</c> and <c>async def</c> a function
/// or a method, <c>__init__</c> a constructor, each name assigned at the top or in a class a constant or a field, nested
/// classes and functions kept with the node that holds them - with its lines, signature and summary; its imports - a bare
/// module looked for by the package layout and its roots, a relative one beside the file - each name they bind for the code of
/// the declaration they are written in; its base lists,
/// the first an <c>inherits</c> and the others <c>mixesIn</c>; each call a body makes by its name and arity, with what its
/// receiver is where the code says - <c>self</c>, <c>cls</c>, <c>super()</c>, a class, an import's name - each class a call
/// makes where the code writes a capitalized name, each type an annotation, an <c>isinstance</c> or an <c>except</c> names,
/// each attribute and name a body reads without calling it, and each string literal the file holds; and a test file's
/// <c>test*</c> functions, <c>Test*</c> classes and <c>TestCase</c> subclasses as tests, each fixture a parameter of a test
/// or of a fixture names a reference of it.
/// </summary>
/// <remarks>
/// Python's blocks are made by indentation, which is no token: each token says its line, and the reader finds its column
/// from where it begins, past the last form feed its line's blanks hold (<see cref="Margin"/>). A statement ends where the next
/// logical line begins - the first token on a later line outside every bracketed group, one a backslash ending its line's code
/// does not continue (<see cref="Continued"/>) - that starts no deeper than the statement's own column: the next
/// statement of its block, or the dedent that ends the block. A header's suite is deeper than its header, and so is passed
/// over whole, however deep it nests; the suite itself is the statements that begin at one column, or the simple statements
/// of the header's own line. A decorator's line belongs to the declaration it stands above. Each block's statements are read
/// in a walk of the reader's stack (<see cref="ScanStack"/>), never a call into the reading from inside one, so a block nested
/// any depth is read in the order the text writes it and no nesting reaches past the reader's stack. A body's code is read
/// once every declaration in it is made: a run over its tokens (<see cref="Calls"/>) that passes what a declaration keeps to
/// itself - a name declared, a parameter, an annotation, an import, a decorator, a nested declaration's own lines - marked as
/// it is read (<see cref="Keep"/>), so each token is read once, by the one node it is the code of.
/// </remarks>
internal sealed class PythonReading
{
    /// <summary>The words of the language: no call is made by them and no value the code reads.</summary>
    private static readonly HashSet<string> Words = new(StringComparer.Ordinal)
    {
        "and", "as", "assert", "async", "await", "break", "class", "continue", "def", "del", "elif", "else", "except", "finally", "for", "from", "global", "if",
        "import", "in", "is", "lambda", "nonlocal", "not", "or", "pass", "raise", "return", "try", "while", "with", "yield", "match", "case",
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

    /// <summary>The decorators the language, its standard library and <c>functools</c> declare, and those a repository's frameworks declare by a name every reader of them keeps as one: a modifier alone, their arguments the code they are.</summary>
    private static readonly HashSet<string> OwnDecorators = new(StringComparer.Ordinal)
    {
        "property", "staticmethod", "classmethod", "abstractmethod", "cached_property", "dataclass", "override", "final", "overload", "fixture", "task", "shared_task", "app", "pytest",
    };

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

    /// <summary>The keywords a compound statement begins with: its header to a colon, then its suite.</summary>
    private static readonly HashSet<string> Compound = new(StringComparer.Ordinal) { "if", "elif", "else", "while", "for", "try", "except", "finally", "with", "match", "case" };

    /// <summary>How Python writes its calls: a name that is no word of the language, then <c>(</c>; a trailing comma ends the arguments.</summary>
    private static readonly ScanCallRules PythonMemberCalls = new(new HashSet<string>(StringComparer.Ordinal), null, TrailingComma: true);

    private readonly InterpolatedText _text;
    private readonly ScannedText _scan;
    private readonly string _prefix;
    private readonly string _path;
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

    /// <summary>The test classes the file declares, by name: a method of one named <c>test*</c> is a test.</summary>
    private readonly HashSet<string> _testClasses = new(StringComparer.Ordinal);

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
        _testFile = name.StartsWith("test_", StringComparison.Ordinal) || name.EndsWith("_test.py", StringComparison.Ordinal)
            || path.StartsWith("tests/", StringComparison.Ordinal) || path.StartsWith("test/", StringComparison.Ordinal)
            || path.Contains("/tests/", StringComparison.Ordinal) || path.Contains("/test/", StringComparison.Ordinal);
    }

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

            // A conftest.py's fixtures are given by pytest to every test under its directory, which no test imports: its module's
            // scope is an import of every file under it that serves the references a fixture qualifier makes alone.
            if (CodeGraphIds.FileName(_path) == "conftest.py")
            {
                _extract.Import(new ExtractImport(CodeImportForms.Directory, dotted, 1) { Serves = CodeQualifierKinds.Fixture });
            }

            Walk(statements, ScanFrame.Top.Inside(module, module, key, null));

            // What the top does outside its declarations is the module's own code.
            Calls(_text, 0, _scan.Count, key, Bind(0, _scan.Count), null);
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

    // ---- Blocks ----

    /// <summary>
    /// The column the token at <paramref name="at"/> begins at: where it begins, less where its line's indentation is counted from
    /// (<see cref="Margin"/>).
    /// </summary>
    private int Column(int at) => (int)(_scan[at].Start - Margin(_scan[at].Line));

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

        for (var at = from; at < to && at < _scan.Count && Column(at) == column && !Continued(_scan.Previous(at), at); at = StatementEnd(at, to, column))
        {
            statements.Add((at, StatementEnd(at, to, column)));
        }

        return statements;
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
    /// none stands.
    /// </summary>
    private int NextLine(int at, int to, int column)
    {
        var limit = Math.Min(to, _scan.Count);

        for (var index = _scan.Next(at); index < limit; index = _scan.Next(index))
        {
            if (_scan.Opens(index))
            {
                index = _scan.Past(index) - 1;
                continue;
            }

            if (Column(index) <= column && _scan[index].Line > _scan[_scan.Previous(index)].EndLine && !Continued(_scan.Previous(index), index))
            {
                return index;
            }
        }

        return limit;
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
    /// A compound statement's header, from its keyword at <paramref name="at"/> to its colon at <paramref name="colon"/>: what the header keeps
    /// to itself - a <c>for</c>'s targets to the <c>in</c>, a <c>case</c>'s pattern to its guard, the names <c>as</c> gives - marked so the
    /// body's run reads the rest as its code; then its suite read as statements of the same frame.
    /// </summary>
    private void Header(int at, int colon, int to, ScanFrame frame)
    {
        var word = Text(at);

        if (word is "for" or "case")
        {
            var end = ScanCalls.TopLevel(_scan, _scan.Next(at), colon, word == "for" ? "in" : "if", null);
            Keep(_scan.Next(at), end < colon ? end : colon);
        }
        else if (word == "except" && frame.Parent is { } holder)
        {
            // What the clause catches - a type, or a tuple of them - is named for the node whose code the block is; the language's own
            // exceptions, ValueError among them, name nothing of the repository's (<see cref="TypeMention"/>).
            var from = _scan.Next(at);
            var end = ScanCalls.TopLevel(_scan, from, colon, "as", null) is var asWord && asWord < colon ? asWord : colon;

            TypeMention(from, end, holder);
            Keep(from, end);
        }

        for (var index = at; index < colon && index < _scan.Count; index = _scan.Opens(index) ? Math.Min(_scan.Past(index), colon) : _scan.Next(index))
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
        // names what a * import of it binds.
        if (Is(at, "del"))
        {
            for (var index = _scan.Next(at); index < to && index < _scan.Count && !_scan.Is(index, ";"); index = _scan.Next(index))
            {
                if (Is(index, "__all__"))
                {
                    ChangedAll(_scan[at].Line);
                }
            }

            Keep(at, to);
            return;
        }

        if (Is(at, "__all__") && _scan.Next(at) is var after && after < _scan.Count && after < to && _scan[after].Kind == ScanTokenKind.Punctuation && Augments.Contains(Text(after)))
        {
            ChangedAll(_scan[at].Line);
        }

        // a = b = value: each name before an = declares what the frame keeps; the last's value is its code.
        var target = at;

        while (_scan.IsName(target) && Assignment(target, to) is { } assignment)
        {
            Assign(target, assignment, to, frame);

            if (assignment.Value < 0)
            {
                return;
            }

            target = assignment.Value;
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

    /// <summary>
    /// The name at <paramref name="at"/> assigned by <paramref name="assignment"/>: at the file's top or in a class a constant where its name is
    /// written in capitals and a field otherwise, its value the node's code; inside a function a local, its annotation's types the
    /// function's references and its value the function's code.
    /// </summary>
    private void Assign(int at, Assigned assignment, int to, ScanFrame frame)
    {
        var name = Text(at);
        var annotation = assignment.AnnotationEnd < 0 ? -1 : _scan.Next(_scan.Next(at));
        var value = assignment.Value;
        var end = value < 0 ? to : LineEnd(assignment.Assign, to);

        // Inside a function - a frame below the module's that is no type's own - a local, its annotation's types the function's.
        if (frame.Owner is null && frame.Depth > 1)
        {
            if (annotation >= 0)
            {
                TypeMention(annotation, assignment.AnnotationEnd, frame.Parent!);
                Keep(_scan.Next(at), assignment.AnnotationEnd);
            }

            Keep(at, value < 0 ? to : value);
            return;
        }

        var qualified = frame.Qualify(name);
        var capitals = name.Any(char.IsAsciiLetter) && name.All(character => char.IsAsciiLetter(character) && char.IsUpper(character) || char.IsAsciiDigit(character) || character == '_');

        if (frame.Depth == 1)
        {
            Top(name);
        }

        var key = _extract.Add(new ExtractNode(Id(qualified), CodeNodeKinds.Member, name, qualified, _scan[at].Line, _scan[_scan.LastOf(end)].EndLine, _scan.Signature(at, value < 0 ? end : value))
        {
            SubKind = capitals ? "constant" : "field",
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

        if (annotation >= 0)
        {
            TypeMention(annotation, assignment.AnnotationEnd, key);
            Keep(_scan.Next(at), assignment.AnnotationEnd);
        }

        Keep(at, value < 0 ? to : value);

        if (value >= 0)
        {
            Calls(_text, value, end, key, Bind(value, end), frame.Owner);
            Keep(value, end);
        }
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
    /// arguments it takes positionally, each parameter's annotation and its return annotation the types they name, each decorator's
    /// expression and each parameter's default its code, its suite's statements read in its own frame and its code read as its calls. A
    /// test file's <c>test*</c> function and a test class's <c>test*</c> method are tests; each parameter a test requires names, as a
    /// fixture does, the fixture that provides it; a function pytest's <c>@fixture</c> decorates is a fixture, never a test.
    /// </summary>
    private void Function(int at, int to, ScanFrame frame, List<(int From, int To)> decorators)
    {
        var asynchronous = Is(at, "async");
        var def = asynchronous ? _scan.Next(at) : at;
        var nameAt = _scan.Next(def);
        var open = _scan.Next(nameAt);
        var close = _scan.Match(open);
        var colon = HeaderColon(close, to);

        if (!_scan.IsName(nameAt) || !_scan.Is(open, "(") || close < 0 || colon < 0)
        {
            return;
        }

        var parameters = Parameters(open, close);
        var returns = _scan.Is(_scan.Next(close), "->") ? _scan.Next(_scan.Next(close)) : -1;
        var first = decorators.Count > 0 ? decorators[0].From - 1 : at;
        var (suite, end) = Suite(colon, to, Column(first));
        var name = Text(nameAt);
        var method = frame.Owner is not null;
        var fixture = decorators.Any(IsFixture);
        var test = !fixture && name.StartsWith("test", StringComparison.Ordinal) && (_testFile && !method || method && IsTestClass(frame.Owner!));
        var qualified = frame.Qualify(name);
        var body = suite.Count > 0 ? suite[0].From : colon + 1;
        var key = _extract.Add(new ExtractNode(Id(qualified), CodeNodeKinds.Member, name, qualified, _scan[first].Line, _scan[_scan.LastOf(end)].EndLine, _scan.Signature(first, colon))
        {
            SubKind = fixture ? CodeSubKinds.Fixture : method && name == "__init__" ? "constructor" : method ? "method" : "function",
            Scope = frame.Scope,
            Parent = frame.Parent,
            BodyStart = suite.Count > 0 ? _scan[suite[0].From].Line : null,
            Summary = ScanComments.Summary(_scan, first) ?? Docstring(suite),
            Modifiers = [.. (asynchronous ? ["async"] : Array.Empty<string>()), .. decorators.Select(DecoratorName)],
            MinArity = Math.Max(0, parameters.Least - (method ? 1 : 0)),
            MaxArity = parameters.Most is { } most ? Math.Max(0, most - (method ? 1 : 0)) : null,
            Test = test,
        });

        _functions.Add(name);
        Keep(first, _scan.Next(nameAt) + 1);

        if (frame.Depth == 1)
        {
            Top(name);
        }

        // A method is bound by its name alone: where a base of its type declares one of that name, along the chain, the method
        // overrides it - looked for among the bases and never in the type itself, as every reader looks for an override. No word of
        // the language says a method overrides, so the reference asks rather than names: a base declaring none is no doubt.
        if (method)
        {
            _extract.Reference(key, CodeEdgeKinds.Overrides, name, _scan[nameAt].Line, qualifier: new ExtractQualifier(CodeQualifierKinds.Base), probe: true);
        }

        if (returns >= 0)
        {
            TypeMention(returns, colon, key);
            Keep(returns, colon);
        }

        foreach (var (from, endOf) in parameters.Annotations)
        {
            TypeMention(from, endOf, key);
        }

        foreach (var (from, endOf) in parameters.Defaults)
        {
            // Before the parameter list is kept to the declaration: a default is the declaration's code, read here and by no run around it.
            Calls(_text, from, endOf, key, Bind(from, endOf), frame.Owner);
        }

        Keep(open, _scan.Next(close));

        foreach (var decorator in decorators)
        {
            Decorator(decorator, key, frame.Owner);
        }

        if (test || fixture)
        {
            // Each parameter a test or a fixture requires names the fixture that provides it, by its name alone: as pytest finds it,
            // the test's own class's or module's, else a conftest.py's of its directory or of one above it, the nearest first, which
            // no test imports.
            foreach (var (needed, line) in parameters.Required.Where(required => required.Name != "self" && required.Name != "cls"))
            {
                _extract.Reference(key, CodeEdgeKinds.References, needed, line, qualifier: new ExtractQualifier(CodeQualifierKinds.Fixture));
            }
        }

        // A function is no scope of the model's: what it declares is declared in the scope the function itself is - its class, the
        // module - and held by the function (<see cref="ScanFrame.Parent"/>), named under it; so a name a nested function reads is
        // looked for from that scope outward, as Python looks a name up through the functions around it to the module.
        var inside = frame.Inside(qualified, frame.Scope, key, null);
        _walks.Walk(0, 1, _ =>
        {
            Walk(suite, inside);
            return 1;
        });
        _walks.Walk(0, 1, _ =>
        {
            var bound = Bind(body, end);

            foreach (var parameter in parameters.Names)
            {
                bound.Add(parameter);
            }

            Calls(_text, body, end, key, bound, frame.Owner);

            // The whole of the declaration is its own: no run of the body it stands in reads a token of it.
            Keep(first, end);
            return 1;
        });
    }

    /// <summary>
    /// Whether the <paramref name="decorator"/> is pytest's fixture - <c>@fixture</c> or <c>@pytest.fixture</c>, whatever it is
    /// given - which makes the function a fixture tests take by a parameter's name (<see cref="CodeSubKinds.Fixture"/>).
    /// </summary>
    private bool IsFixture((int From, int To) decorator)
        => _scan.IsName(decorator.From)
            && (Text(decorator.From) == "fixture"
                || Text(decorator.From) == "pytest" && _scan.Is(_scan.Next(decorator.From), ".") && Text(_scan.Next(_scan.Next(decorator.From))) == "fixture");

    /// <summary>The name a decorator is said by, as a modifier: an <c>@</c> and the dotted name it is written with - <c>@dataclass</c>, <c>@pytest.fixture</c>, <c>@app.route</c> - without its arguments.</summary>
    private string DecoratorName((int From, int To) decorator)
        => _scan.IsName(decorator.From)
            ? "@" + string.Join('.', Chain(_scan, decorator.From).TakeWhile(index => index < decorator.To).Select(Text))
            : "@...";

    /// <summary>
    /// The expression of a <paramref name="decorator"/>, the code of the node of <paramref name="key"/>: what it calls and holds - where a
    /// decorator the language or a framework declares by its own name (<see cref="OwnDecorators"/>) is a modifier alone, its arguments
    /// alone the code, the route path or the task name among them.
    /// </summary>
    private void Decorator((int From, int To) decorator, QualifiedName key, string? owner)
    {
        var (from, to) = decorator;
        var bare = _scan.IsName(from) && OwnDecorators.Contains(Text(from));
        var dotted = _scan.IsName(from) && _scan.Is(_scan.Next(from), ".") && _scan.IsName(_scan.Next(_scan.Next(from))) && OwnDecorators.Contains(Text(_scan.Next(_scan.Next(from))));
        var head = bare ? from : dotted ? _scan.Next(_scan.Next(from)) : -1;
        var open = _scan.Next(head);

        if (head < 0)
        {
            // A decorator the repository declares: its expression is the decorated node's code, whole.
            Calls(_text, from, to, key, Bind(from, to), owner);
            Keep(from, to);
            return;
        }

        // The decorator's own name - property, functools.cached_property, app.route, pytest.fixture - is a modifier; what it is given is code.
        Keep(from, _scan.Next(open));

        if (_scan.Is(open, "(") && _scan.Match(open) is var close && close > open)
        {
            Keep(close, to);
            Calls(_text, _scan.Next(open), close, key, Bind(_scan.Next(open), close), owner);
            return;
        }

        Keep(from, to);
    }

    /// <summary>Whether the class <paramref name="owner"/> is a test class: its name written <c>Test*</c>, or built on <c>TestCase</c>.</summary>
    private bool IsTestClass(string owner) => owner.StartsWith("Test", StringComparison.Ordinal) || _testClasses.Contains(owner);

    // ---- Classes ----

    /// <summary>
    /// A <c>class</c> at <paramref name="at"/>, with the <paramref name="decorators"/> above it: a type node over its decorators, its head and
    /// its suite; each base its parentheses name - a name, or a dotted one through an import - an <c>inherits</c> edge to the first and
    /// <c>mixesIn</c> to the others, a base the code computes or a keyword of the class its code; its suite's statements read in its
    /// own frame, and what the suite holds outside its declarations the class's code. A <c>Test*</c> class, or one built on
    /// <c>TestCase</c>, is a test class.
    /// </summary>
    private void Class(int at, int to, ScanFrame frame, List<(int From, int To)> decorators)
    {
        var nameAt = _scan.Next(at);

        if (!_scan.IsName(nameAt) || HeaderColon(_scan.Next(nameAt), to) is var colon && colon < 0)
        {
            return;
        }

        var first = decorators.Count > 0 ? decorators[0].From - 1 : at;
        var (suite, end) = Suite(colon, to, Column(first));
        var name = Text(nameAt);
        var qualified = frame.Qualify(name);
        var body = suite.Count > 0 ? suite[0].From : colon + 1;
        var test = name.StartsWith("Test", StringComparison.Ordinal);
        var key = _extract.Add(new ExtractNode(Id(qualified), CodeNodeKinds.Type, name, qualified, _scan[first].Line, _scan[_scan.LastOf(end)].EndLine, _scan.Signature(first, colon))
        {
            SubKind = "class",
            Scope = frame.Scope,
            Parent = frame.Parent,
            BodyStart = suite.Count > 0 ? _scan[suite[0].From].Line : null,
            Summary = ScanComments.Summary(_scan, first) ?? Docstring(suite),
            Modifiers = [.. decorators.Select(DecoratorName)],
            Test = test,
        });

        _classes.Add(name);
        Keep(first, _scan.Next(nameAt) + 1);

        if (frame.Depth == 1)
        {
            Top(name);
        }

        var bases = 0;

        if (_scan.Is(_scan.Next(nameAt), "(") && _scan.Match(_scan.Next(nameAt)) is var close && close > 0)
        {
            foreach (var (from, endOf) in ScanCalls.Split(_scan, _scan.Next(_scan.Next(nameAt)), close, null))
            {
                if (BaseName(from, endOf, frame.Parent!) is { } named)
                {
                    var (written, qualifier) = named;
                    var target = Simple(written);
                    _extract.Reference(key, bases++ == 0 ? CodeEdgeKinds.Inherits : CodeEdgeKinds.MixesIn, target, _scan[from].Line, qualifier: qualifier);
                    test |= target == "TestCase";
                }
                else
                {
                    // A base the code computes - a call - or a keyword of the class, metaclass=: the class's code.
                    Calls(_text, from, endOf, key, Bind(from, endOf), name);
                }
            }

            Keep(_scan.Next(nameAt), _scan.Next(close));
        }

        if (test)
        {
            _extract.Change(key, node => node with { Test = true });
            _testClasses.Add(name);
        }

        foreach (var decorator in decorators)
        {
            Decorator(decorator, key, name);
        }

        // The suite's declarations are read in their own walk, and what the class holds outside them in a walk after it.
        var inside = frame.Inside(qualified, qualified, key, name);
        _walks.Walk(0, 1, _ =>
        {
            Walk(suite, inside);
            return 1;
        });
        _walks.Walk(0, 1, _ =>
        {
            Calls(_text, body, end, key, Bind(body, end), name);
            Keep(first, end);
            return 1;
        });
    }

    /// <summary>
    /// The base the tokens from <paramref name="from"/> to <paramref name="to"/> name, where they are a name or a dotted one alone, read in
    /// the code of the node of <paramref name="key"/>: written out in dots, a name an import binds read as what it reaches and a dotted one
    /// through the import its head is (<see cref="ThroughImport"/>), a class the file declares named as a type; <see langword="null"/>
    /// where they are anything else.
    /// </summary>
    private (string Written, ExtractQualifier? Qualifier)? BaseName(int from, int to, QualifiedName key)
    {
        var chain = Chain(_scan, from);

        if (!_scan.IsName(from) || Words.Contains(Text(from)) || chain[^1] != _scan.Previous(to) || chain.Any(index => Words.Contains(Text(index))))
        {
            return null;
        }

        var written = string.Join('.', chain.Select(Text));
        var through = BindingOf(Text(from), key);

        if (chain.Count == 1 && through is not null)
        {
            // A base an import binds: from m import A as B; class C(B) is built on A, through B.
            return through.Kind == BindingKind.Module ? null : (through.Name, Itself(through));
        }

        if (chain.Count > 1 && through is not null)
        {
            return (written, ThroughImport(_scan, chain, through));
        }

        return (written, chain.Count > 1 && _classes.Contains(Text(chain[0])) ? new ExtractQualifier(CodeQualifierKinds.Type, QualifiedName.Of(Text(chain[0]))) : null);
    }

    // ---- Imports ----

    /// <summary>
    /// The file's imports, each the import of the frame it is written in: <c>import a.b.c</c> - the package <c>a</c> by the name the code
    /// gives it, and the module <c>a.b.c</c> reached by the dotted names to it, as Python binds <c>a</c> and reaches each module through
    /// it, a step between them - <c>a.b</c> - a module of its own where the code reaches it (<see cref="ThroughImport"/>);
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
                    Bind(holder, new Binding(BindingKind.Module, alias, path, own + alias, null, path), frame);
                    continue;
                }

                // import a.b.c: a is the package a, and the module a.b.c is reached by a.b.c.
                var head = Text(from);
                var binding = Bind(holder, new Binding(BindingKind.Module, head, head, own + head, null, head), frame);
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

            Bind(holder, new Binding(alias is null ? BindingKind.Shown : BindingKind.Renamed, local, name, prefix, alias is null ? (top ? null : own) : prefix, modulePath), frame);
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
    /// The binding an import makes of <paramref name="name"/> for the code of the node of <paramref name="key"/>: the nearest declaration's
    /// it is written in that imports it - itself, then each function that holds it, as Python looks a name up through the functions
    /// around it, a class's for the class's own code alone - its body's and its fields' values - as no class's names are a method's
    /// or a nested class's - else the module's top's; none where no import binds it.
    /// </summary>
    private Binding? BindingOf(string name, QualifiedName key)
    {
        QualifiedName? below = null;

        for (var holder = _local.Count > 0 ? key : null; holder is not null; below = holder, holder = holder.BeforeLastDot())
        {
            if (_local.TryGetValue(holder, out var held) && held.Names.TryGetValue(name, out var local) && (!held.Class || holder == key || (below == key && OfClassBody(key))))
            {
                return local;
            }
        }

        return _bindings.TryGetValue(name, out var atTop) ? atTop : null;
    }

    /// <summary>Whether the node of <paramref name="key"/> is a name a class's body assigns - a field or a constant - whose value is the class's own code.</summary>
    private bool OfClassBody(QualifiedName key) => _extract.Node(key) is { Kind: CodeNodeKinds.Member, SubKind: "field" or "constant" };

    /// <summary>The binding an import makes of <paramref name="name"/> for the code of <paramref name="key"/>, read in <paramref name="scan"/> (<see cref="BindingOf"/>).</summary>
    private Binding? ImportBound(ScannedText scan, string name, QualifiedName key) => ReferenceEquals(scan, _scan) ? BindingOf(name, key) : null;

    /// <summary>
    /// What a reference to the name an import binds is qualified by: the prefix of the import that shows it, where one has a prefix -
    /// a renamed one, one a declaration makes - and none where an import shows it by the name itself.
    /// </summary>
    private static ExtractQualifier? Itself(Binding binding) => binding.Shows is { } shows ? new ExtractQualifier(CodeQualifierKinds.Prefix, QualifiedName.Of(shows)) : null;

    /// <summary>
    /// What a member reached through <paramref name="chain"/> of <paramref name="scan"/>, whose head <paramref name="binding"/> binds, is
    /// qualified by - one rule for every name an import binds, a call's, a use's, an annotation's and a base's: the prefix of the import of
    /// the module the longest run of the chain's names before the member reaches - <c>import a.b.c</c>, then <c>a.b.c.f()</c> - where that
    /// run is all of them; else the type or scope that module's names past that run stand for, written with them -
    /// <c>a.Cart.parse()</c>, or <c>shape.Shape</c> of <c>from pkg import shape</c>. A step of a dotted import the code reaches through,
    /// <c>a.b</c> of <c>import a.b.c</c>, is made an import of its own the first time it is reached, so what the extract holds of a
    /// dotted import grows with the chains the code writes and never with the square of the import's names.
    /// </summary>
    private ExtractQualifier ThroughImport(ScannedText scan, List<int> chain, Binding binding)
    {
        var step = binding.Head;
        var covered = 1;

        while (covered < chain.Count - 1 && step.Next.TryGetValue(scan.Text(chain[covered]), out var next))
        {
            step = next;
            covered++;
        }

        var prefix = Reached(step, binding);

        return covered == chain.Count - 1
            ? new ExtractQualifier(CodeQualifierKinds.Prefix, QualifiedName.Of(prefix))
            : new ExtractQualifier(CodeQualifierKinds.Type, QualifiedName.Of(string.Join('.', [prefix, .. chain.Skip(covered).Take(chain.Count - 1 - covered).Select(index => scan.Text(index))])));
    }

    /// <summary>
    /// The prefix the module of <paramref name="step"/>, a step of <paramref name="binding"/>'s dotted import, is reached by: the binding's,
    /// with each name from its head to the step - an import of the module made where none is yet, the import of the binding's head being
    /// made with the binding.
    /// </summary>
    private string Reached(ImportStep step, Binding binding)
    {
        var names = new List<string>();

        for (var at = step; at.Before is not null; at = at.Before)
        {
            names.Add(at.Name);
        }

        if (names.Count == 0)
        {
            return binding.Prefix;
        }

        names.Reverse();
        var prefix = $"{binding.Prefix}.{string.Join('.', names)}";

        if (!step.Imported)
        {
            step.Imported = true;
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
        public ImportStep Head { get; } = new(null, Local, 0) { Imported = true };
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
    }

    // ---- What a body binds and calls ----

    /// <summary>
    /// Each name the tokens from <paramref name="from"/> to <paramref name="to"/> bind, as Python binds them: a name before an <c>=</c> or
    /// an assigning operator at no call's depth, a <c>for</c>'s targets, the names <c>as</c> gives, a lambda's parameters - and the
    /// names a <c>global</c> or a <c>nonlocal</c> declares bound nowhere here, taken out.
    /// </summary>
    private HashSet<string> Bind(int from, int to)
    {
        var bound = new HashSet<string>(StringComparer.Ordinal);
        int parentheses = 0;

        for (var at = from; at < to && at < _scan.Count; at = _scan.Next(at))
        {
            if (_scan.Is(at, "("))
            {
                parentheses++;
                continue;
            }

            if (_scan.Is(at, ")"))
            {
                parentheses = Math.Max(0, parentheses - 1);
                continue;
            }

            if (!_scan.IsName(at))
            {
                continue;
            }

            var word = Text(at);

            if (Words.Contains(word))
            {
                if (word is "for" or "as" or "lambda")
                {
                    at = BindTargets(at, to, bound);
                }
                else if (word is "global" or "nonlocal")
                {
                    for (var index = _scan.Next(at); index < to && index < _scan.Count && _scan.IsName(index); index = _scan.Next(index))
                    {
                        bound.Remove(Text(index));
                    }
                }

                continue;
            }

            // A keyword argument's name, or a default's, inside a call's parentheses binds nothing.
            var next = _scan.Next(at);

            if (next >= _scan.Count)
            {
                continue;
            }

            if (parentheses == 0 && (_scan.Is(next, "=") || _scan.Is(next, ":=") || (_scan[next].Kind == ScanTokenKind.Punctuation && Augments.Contains(Text(next)))))
            {
                bound.Add(word);
            }
        }

        return bound;
    }

    /// <summary>
    /// The names a <c>for</c>'s targets, an <c>as</c> or a lambda's parameters bind from the keyword at <paramref name="at"/>, added to
    /// <paramref name="bound"/>; answers the index the reading goes on from.
    /// </summary>
    private int BindTargets(int at, int to, HashSet<string> bound)
    {
        var word = Text(at);

        if (word == "as")
        {
            if (_scan.IsName(_scan.Next(at)))
            {
                bound.Add(Text(_scan.Next(at)));
            }

            return _scan.Next(at);
        }

        // for: its targets to the in; lambda: its parameters to its colon, groups passed over.
        var stop = word == "for" ? "in" : ":";

        for (var index = _scan.Next(at); index < to && index < _scan.Count && !_scan.Is(index, stop); index = _scan.Opens(index) ? Math.Min(_scan.Past(index), to) : _scan.Next(index))
        {
            if (_scan.IsName(index))
            {
                bound.Add(Text(index));
            }
        }

        return Math.Min(ScanCalls.TopLevel(_scan, _scan.Next(at), to, stop, null), to) - 1;
    }

    /// <summary>
    /// Each call the tokens of <paramref name="text"/> from <paramref name="from"/> to <paramref name="to"/> make, from the node of
    /// <paramref name="key"/>: a reference by its name and arity, qualified by its receiver where the code says, a class a call makes
    /// where the code writes a capitalized name, a type an annotation, an <c>isinstance</c>, an <c>except</c> or a bare name names, each
    /// attribute and name the code reads without calling it, and what it cannot bind marked dynamic. A string's fields are read as
    /// text of their own, the first on top, before the run it stands in goes on.
    /// </summary>
    private void Calls(InterpolatedText text, int from, int to, QualifiedName key, HashSet<string> bound, string? owner)
    {
        var runs = new Stack<CodeRun>();
        runs.Push(new CodeRun(text, from, to, key, bound));

        while (runs.TryPeek(out var run))
        {
            if (run.At >= run.To || run.At >= run.Text.Scan.Count)
            {
                runs.Pop();
                continue;
            }

            run.At = CallAt(run, owner, runs) + 1;
        }
    }

    /// <summary>One run of code <see cref="Calls"/> reads (<see cref="ScanRun{TKey, TLocals}"/>).</summary>
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

        // What a declaration keeps to itself: read by its own reading, by no run of the body it stands in.
        if (ReferenceEquals(scan, _scan) && _kept.Contains(token.Start))
        {
            return at;
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
                    runs.Push(new CodeRun(() => PythonLexer.Scan(scan.Source, start, end, text.Lines, text.Closes), key, bound));
                }
            }

            return at;
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

        // A module's __all__ changed through a method of its own - __all__.append("x"), .extend, .sort: no list of literals names
        // what a * import of the module binds. What only reads it, len(__all__), is no change.
        if (ReferenceEquals(scan, _scan) && word == "__all__" && chain.Count > 1 && called)
        {
            ChangedAll(line);
        }
        // super(...).member: the member is reached through the base (<see cref="Receiver"/>); the arguments are the enclosing code's.
        if (word == "super" && !scan.Is(previous, "."))
        {
            return scan.Is(next, "(") ? scan.CloseOf(next) - 1 : at;
        }

        // The name of a keyword argument - f(owner=x) - names no declaration: the value is the code's.
        if (scan.Is(next, "=") && !Words.Contains(word))
        {
            return at;
        }

        // A member of a chain is read when the chain's head is - save one a call's or a subscript's result reaches, a value by no name
        // the file types, and one super(...) reaches, its base's.
        if (scan.Is(previous, "."))
        {
            var before = scan.Previous(previous);

            if (scan.Is(before, ")") && scan.Match(before) is var group && group >= 0 && scan.Is(scan.Previous(group), "super"))
            {
                Member(scan, word, next, line, key, new ExtractQualifier(CodeQualifierKinds.Base));
                return at;
            }

            if (!scan.Is(before, ")") && !scan.Is(before, "]"))
            {
                return at;
            }

            Member(scan, word, next, line, key, new ExtractQualifier(CodeQualifierKinds.Value));
            return at;
        }

        if (Words.Contains(word) || Builtins.Contains(word))
        {
            if ((word == "isinstance" || word == "issubclass") && scan.Is(next, "("))
            {
                return InstanceOf(scan, at, key);
            }

            return at;
        }

        var through = ImportBound(scan, word, key);

        // A name the language keeps to itself - __name__, __doc__: no value the code reads.
        if (word.StartsWith("__", StringComparison.Ordinal))
        {
            return at;
        }

        // A name a body binds: a call through its value is dynamic, and a read of it is no use of a declaration.
        if (chain.Count == 1 && through is null && bound.Contains(word))
        {
            if (called)
            {
                _extract.Dynamic(line, $"a call through the function value {word}", FunctionValue);
            }

            return at;
        }

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

        // A name an import binds is read as what it reaches, never as itself.
        var target = chain.Count == 1 && through is { Kind: not BindingKind.Module } ? through.Name : scan.Text(last);
        var qualifier = chain.Count > 1 ? Receiver(scan, chain, owner, through) : through is not null ? Itself(through) : null;

        if (called)
        {
            var makes = Capital(target) && !_functions.Contains(target);
            _extract.Reference(key, makes ? CodeEdgeKinds.Instantiates : CodeEdgeKinds.Calls, target, line, makes ? null : ScanCalls.CallArity(scan, after, scan.CloseOf(after), PythonMemberCalls), qualifier);
        }
        else
        {
            // A name the code reads without calling it: a reference where a single name is written as a class's and is one an import
            // binds or the file declares - it names a type, of another module or of this one - and a use otherwise: a function, a
            // constant, a member.
            var type = chain.Count == 1 && Capital(target) && (through is { Kind: not BindingKind.Module } || _classes.Contains(target));
            _extract.Reference(key, type ? CodeEdgeKinds.References : CodeEdgeKinds.Uses, target, line, qualifier: qualifier);
        }

        return last;
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
    /// What a member reached through the chain <paramref name="chain"/> of <paramref name="scan"/> is qualified by: one whose head
    /// <paramref name="through"/> - a name an import binds - is, what the import reaches (<see cref="ThroughImport"/>), a module path
    /// however long; else <c>self</c> and <c>cls</c> in a class the type's own, <c>super()</c> its base's, a class the file declares named
    /// as a type, any other a value by no name the file types.
    /// </summary>
    private ExtractQualifier Receiver(ScannedText scan, List<int> chain, string? owner, Binding? through)
    {
        var head = scan.Text(chain[0]);

        if (through is not null)
        {
            return ThroughImport(scan, chain, through);
        }

        // self.lines.append(): what the member is reached through is the member before it, a value by no name the file types; the
        // type's own and a base's are reached by one name alone.
        if (chain.Count > 2)
        {
            return new ExtractQualifier(CodeQualifierKinds.Value);
        }

        if ((head == "self" || head == "cls") && owner is not null)
        {
            return new ExtractQualifier(CodeQualifierKinds.This);
        }

        if (head == "super")
        {
            return new ExtractQualifier(CodeQualifierKinds.Base);
        }

        if (ReferenceEquals(scan, _scan) && _classes.Contains(head))
        {
            return new ExtractQualifier(CodeQualifierKinds.Type, QualifiedName.Of(head));
        }

        return new ExtractQualifier(CodeQualifierKinds.Value);
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
    /// The second argument of the <c>isinstance</c> or <c>issubclass</c> whose parenthesis is after <paramref name="at"/>: the type the code
    /// tests against, each name of it a reference from the node of <paramref name="key"/>; answers where the reading goes on.
    /// </summary>
    private int InstanceOf(ScannedText scan, int at, QualifiedName key)
    {
        var open = scan.Next(at);
        var close = scan.CloseOf(open);
        var comma = ScanCalls.TopLevel(scan, open + 1, close, ",", null);

        if (comma >= close)
        {
            return at;
        }

        for (var index = scan.Next(comma); index < close && index < scan.Count;)
        {
            if (!scan.IsName(index) || scan.Is(scan.Previous(index), "."))
            {
                index = scan.Next(index);
                continue;
            }

            var chain = Chain(scan, index);

            // A type the language answers to - str, int, a tuple of them - names no declaration of the repository's.
            if (Words.Contains(scan.Text(index)) || Builtins.Contains(scan.Text(index)))
            {
                index = scan.Next(chain[^1]);
                continue;
            }

            Mention(scan, chain, key, CodeEdgeKinds.References);
            index = scan.Next(chain[^1]);
        }

        if (ReferenceEquals(scan, _scan))
        {
            Keep(scan.Next(comma), close);
        }

        return close - 1;
    }

    /// <summary>A reference of <paramref name="kind"/> from the node of <paramref name="key"/> to the member the chain <paramref name="chain"/> of <paramref name="scan"/> ends in, read as the code reaches it.</summary>
    private void Mention(ScannedText scan, List<int> chain, QualifiedName key, string kind)
    {
        var target = scan.Text(chain[^1]);
        var through = ImportBound(scan, scan.Text(chain[0]), key);

        // A module's name alone names no declaration, as a call's reading says (<see cref="CallAt"/>).
        if (chain.Count == 1 && through is { Kind: BindingKind.Module })
        {
            return;
        }

        var written = chain.Count == 1 && through is not null ? through.Name : target;
        var qualifier = chain.Count > 1 ? Receiver(scan, chain, null, through) : through is not null ? Itself(through) : null;
        _extract.Reference(key, kind, written, scan[chain[^1]].Line, qualifier: qualifier);
    }

    /// <summary>
    /// Each type the tokens from <paramref name="from"/> to <paramref name="to"/> of an annotation name: each capitalized name not reached
    /// through a dot a reference to the type it names - through an import's name where the chain's head is one - and a string annotation
    /// read as the text of its own.
    /// </summary>
    private void TypeMention(int from, int to, QualifiedName key)
    {
        for (var at = from; at < to && at < _scan.Count;)
        {
            if (_scan[at].Kind == ScanTokenKind.Text)
            {
                // "ClassName" as an annotation, read as the names it writes.
                foreach (var (value, line) in _scan.LiteralParts(_scan[at]))
                {
                    var inner = PythonLexer.Scan(value);

                    for (var index = 0; index < inner.Scan.Count; index++)
                    {
                        if (inner.Scan.IsName(index) && Capital(inner.Scan.Text(index)) && !inner.Scan.Is(inner.Scan.Previous(index), ".") && !Words.Contains(inner.Scan.Text(index)) && !Builtins.Contains(inner.Scan.Text(index)))
                        {
                            _extract.Reference(key, CodeEdgeKinds.References, inner.Scan.Text(index), line);
                        }
                    }
                }

                at = _scan.Next(at);
                continue;
            }

            if (_scan.IsName(at) && Capital(Text(at)) && !_scan.Is(_scan.Previous(at), ".") && !Words.Contains(Text(at)) && !Builtins.Contains(Text(at)))
            {
                var chain = Chain(_scan, at);
                Mention(_scan, chain, key, CodeEdgeKinds.References);
                at = _scan.Next(chain[^1]);
                continue;
            }

            at = _scan.Next(at);
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
