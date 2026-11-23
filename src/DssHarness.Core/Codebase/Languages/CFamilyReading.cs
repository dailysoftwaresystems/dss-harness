using System.Text;
using DssHarness.Core.Codebase.Languages.Scanning;

namespace DssHarness.Core.Codebase.Languages;

/// <summary>
/// One C or C++ file's reading over the structural scanner: what stands at its file, namespace and class scope - each
/// namespace, type, <c>typedef</c>, function, prototype, variable, field and macro with its lines, signature and summary -
/// its <c>#include</c>s, and each call a body makes by its name and arity, with what its receiver is where the file says.
/// </summary>
/// <remarks>
/// <para>
/// A declaration's id is its language and its qualified name - joined by dots, as the model's names are - with, in C++,
/// its parameters' types and a <c>const</c> member's mark; one with internal linkage (<c>static</c> at a file's or a
/// namespace's scope, inside an anonymous namespace, a macro of a file that is no header) with the file's path before
/// its name, put in the file's closed scope. A prototype and its definition are one id, so one declared in a header and
/// defined in a source file is one node of two parts, as a partial type is; in one file, the definition takes the id
/// and the prototype the next.
/// </para>
/// <para>
/// Every branch of a conditional is read, so a declaration under each of two conditions is two nodes of one name; every
/// declaration under a conditional - but the file's include guard - is marked <c>conditional</c>. What it cannot bind
/// is marked dynamic: a call of a function-like macro the file defines, a macro standing at a scope in the place of a
/// declaration, a call through a function pointer the file shows as one - <c>(*f)(</c>, or a name declared
/// <c>(*f)(...)</c> or a <c>std::function</c> - and, in C, every call through a member, which C makes through a
/// function pointer alone.
/// </para>
/// </remarks>
internal sealed class CFamilyReading
{
    /// <summary>
    /// The kinds of the marks the reading makes, each the sign of a limit of C's help and of C++'s (<see cref="CodeGraphLimit.Signs"/>):
    /// a macro the reader does not expand, a call through a function pointer, and a bracket one branch of a conditional leaves open.
    /// </summary>
    internal const string
        UnexpandedMacro = "macro",
        PointerCall = "pointer-call",
        OpenBranch = "open-branch";

    /// <summary>The names of C no call is made by: its statements, its types, its qualifiers and what it asks of the compiler.</summary>
    private static readonly HashSet<string> CKeywords = new(StringComparer.Ordinal)
    {
        "if", "while", "for", "switch", "return", "sizeof", "alignof", "_Alignof", "_Generic", "do", "else", "case", "goto", "default",
        "typeof", "typeof_unqual", "__typeof__", "__typeof", "__attribute__", "__attribute", "__declspec", "__asm__", "__asm", "asm",
        "_Static_assert", "static_assert", "defined", "__extension__", "_Pragma", "__pragma", "__builtin_offsetof", "__alignof__",
        "void", "char", "short", "int", "long", "float", "double", "signed", "unsigned", "_Bool", "bool", "_Complex", "_Atomic",
        "struct", "union", "enum", "const", "volatile", "restrict", "static", "extern", "inline", "register", "typedef", "break", "continue",
    };

    /// <summary>C's names, and those C++ adds that no call is made by.</summary>
    private static readonly HashSet<string> CppKeywords = new(CKeywords, StringComparer.Ordinal)
    {
        "catch", "decltype", "noexcept", "alignas", "typeid", "new", "delete", "throw", "co_await", "co_return", "co_yield", "requires",
        "static_cast", "dynamic_cast", "const_cast", "reinterpret_cast", "operator", "template", "typename", "using", "explicit",
        "concept", "class", "this", "wchar_t", "char8_t", "char16_t", "char32_t", "auto", "nullptr", "true", "false", "and", "or", "not",
        "namespace", "virtual", "public", "private", "protected", "friend", "mutable", "constexpr", "consteval", "constinit", "try",
    };

    /// <summary>The words a call may follow in a statement, which no type is: after any other name, a name and <c>(</c> declare something.</summary>
    private static readonly HashSet<string> StatementWords = new(StringComparer.Ordinal)
    {
        "return", "else", "case", "throw", "co_return", "co_yield", "co_await", "do", "goto", "delete", "new", "not", "and", "or",
        "sizeof", "typeof", "__typeof__", "alignof", "decltype", "noexcept", "default",
    };

    /// <summary>The names of the types C and C++ build in, and the integer types of their standard headers: no type the repository declares.</summary>
    private static readonly HashSet<string> Builtins = new(StringComparer.Ordinal)
    {
        "void", "char", "short", "int", "long", "float", "double", "signed", "unsigned", "_Bool", "bool", "wchar_t", "char8_t", "char16_t",
        "char32_t", "auto", "_Complex", "__int128", "__int64", "__int32", "__int16", "__int8", "decltype", "_Float16", "__fp16",
        "size_t", "ssize_t", "ptrdiff_t", "intptr_t", "uintptr_t", "intmax_t", "uintmax_t", "max_align_t", "nullptr_t", "wint_t",
        "int8_t", "int16_t", "int32_t", "int64_t", "uint8_t", "uint16_t", "uint32_t", "uint64_t",
    };

    /// <summary>The words a declaration's type may hold that say how it is declared, not what type it is.</summary>
    private static readonly HashSet<string> Specifiers = new(StringComparer.Ordinal)
    {
        "static", "inline", "extern", "virtual", "explicit", "constexpr", "consteval", "constinit", "friend", "mutable", "register",
        "thread_local", "_Thread_local", "__inline", "__inline__", "__forceinline", "_Noreturn", "noreturn", "const", "volatile",
        "restrict", "__restrict", "__restrict__", "_Atomic", "typename", "struct", "class", "union", "enum", "__extension__",
    };

    /// <summary>The specifiers a declaration's node names among its modifiers.</summary>
    private static readonly HashSet<string> Modifying = new(StringComparer.Ordinal)
    {
        "static", "inline", "extern", "virtual", "explicit", "constexpr", "consteval", "constinit", "_Noreturn", "thread_local", "_Thread_local",
    };

    /// <summary>The words that begin a declaration, before which a lone name on a line of its own is a macro standing in the place of one.</summary>
    private static readonly HashSet<string> Beginners = new(StringComparer.Ordinal)
    {
        "typedef", "struct", "union", "enum", "class", "namespace", "template", "extern", "static", "inline", "using", "virtual", "explicit",
        "friend", "public", "private", "protected", "signals", "slots", "Q_SIGNALS", "Q_SLOTS", "}",
    };

    /// <summary>The access labels of a class's body, and Qt's, which declare nothing.</summary>
    private static readonly HashSet<string> AccessLabels = new(StringComparer.Ordinal) { "public", "private", "protected", "signals", "slots", "Q_SIGNALS", "Q_SLOTS" };

    /// <summary>The words that may follow a function's parameters, before its body or its semicolon.</summary>
    private static readonly HashSet<string> Trailers = new(StringComparer.Ordinal)
    {
        "const", "volatile", "noexcept", "throw", "override", "final", "__attribute__", "__attribute", "__declspec", "asm", "__asm__", "__asm",
        "mutable", "transaction_safe",
    };

    /// <summary>The smart pointers of the standard library: a call through one is through the type it holds.</summary>
    private static readonly HashSet<string> Holders = new(StringComparer.Ordinal) { "unique_ptr", "shared_ptr", "weak_ptr" };

    /// <summary>The extensions of a header, whose macros and names of internal linkage are each includer's.</summary>
    private static readonly string[] Headers = [".h", ".hpp", ".hh", ".hxx", ".inl", ".ipp"];

    /// <summary>How C writes its calls: no type arguments.</summary>
    private static readonly ScanCallRules CCalls = new(CKeywords, null);

    /// <summary>
    /// How C++ writes a type's arguments where a declaration names a type: what a type may hold - types, and constants
    /// an expression writes, as <c>std::array&lt;int, N / 2&gt;</c> and <c>enable_if&lt;N == 2&gt;</c> - and what follows
    /// a list a call or a qualified name is written with.
    /// </summary>
    private static readonly ScanTypeArguments CppTypes = new(
        new HashSet<string>(StringComparer.Ordinal) { ",", "::", "*", "&", ".", "...", "?", ":", "-", "+", "/", "%", "|", "^", "~", "!", "=" },
        new HashSet<string>(StringComparer.Ordinal) { "(", "::", "{" });

    /// <summary>
    /// How C++ writes a type's arguments in code, where <c>&lt;</c> and <c>&gt;</c> compare too: a condition's
    /// comparisons and logic - <c>i &lt; 0 || i &gt; (int)n</c>, <c>i &lt; n &amp;&amp; j &gt; m</c> - are no type's arguments,
    /// its arithmetic may be, and an address: <c>connect&lt;&amp;Listener::receive&gt;</c>.
    /// </summary>
    private static readonly ScanTypeArguments CppCodeTypes = new(
        new HashSet<string>(StringComparer.Ordinal) { ",", "::", "*", "&", ".", "...", "?", "-", "+", "/", "%", "^", "~" },
        CppTypes.Followers,
        new HashSet<string>(StringComparer.Ordinal) { "&" });

    /// <summary>How C++ writes its calls: with type arguments, as code writes them.</summary>
    private static readonly ScanCallRules CppCalls = new(CppKeywords, CppCodeTypes);

    /// <summary>The words whose parentheses hold a type or an expression, of which they measure or name the type: <c>sizeof(Node)</c>, <c>typeid(Shape)</c>.</summary>
    private static readonly HashSet<string> Measures = new(StringComparer.Ordinal) { "sizeof", "alignof", "_Alignof", "__alignof__", "__alignof", "typeid" };

    /// <summary>The words after which an expression begins, so that a parenthesis after one may be a cast's: <c>return (Node *)p</c>.</summary>
    private static readonly HashSet<string> CastFollowed = new(StringComparer.Ordinal)
    {
        "return", "case", "throw", "co_return", "co_yield", "co_await", "else", "do", "delete", "and", "or", "not",
    };

    private readonly ScannedText _scan;
    private readonly CodeLineIndex _lines;
    private readonly string _prefix;
    private readonly string _path;
    private readonly bool _cpp;
    private readonly bool _header;
    private readonly IReadOnlySet<string> _keywords;
    private readonly ScanCallRules _calls;
    private readonly ScanTypeArguments? _types;
    private readonly ScanExtract _extract = new();

    /// <summary>The walks of the file's declarations, a block's each a frame of its own: no nesting reaches past the reader's stack.</summary>
    private readonly ScanStack _walks = new();

    /// <summary>
    /// The reference each member a body reads by its name was made as, by the text and the index of the member's name: what
    /// the next name of its chain links its qualifier to (<see cref="ExtractQualifier.Link"/>).
    /// </summary>
    private readonly Dictionary<(ScannedText Scan, int At), int> _members = [];

    /// <summary>The file's closed scope: what its names with internal linkage are put in, named by the file's own id.</summary>
    private readonly QualifiedName _closed;

    /// <summary>The index of the file's include guard's conditional; -1 where it has none.</summary>
    private readonly int _guard;

    /// <summary>For each token, whether it lies under a conditional other than the file's include guard.</summary>
    private readonly bool[] _conditional;

    /// <summary>The function-like macros the file defines, by name.</summary>
    private readonly HashSet<string> _functionMacros = new(StringComparer.Ordinal);

    /// <summary>The variables of the file's and its namespaces' scope that are function pointers, by name: a call of one is through the pointer.</summary>
    private readonly HashSet<string> _pointers = new(StringComparer.Ordinal);

    /// <summary>The fields each type the file defines holds as function pointers, by the type's simple name: a call of one on the type's value is through the pointer.</summary>
    private readonly Dictionary<string, HashSet<string>> _fieldPointers = new(StringComparer.Ordinal);

    /// <summary>The bodies of the functions a type's body defines, read once the type's body is: a member's body sees every member, those after it among them.</summary>
    private List<Action>? _bodies;

    /// <summary>The names of the type parameters of the templates being read: no type the repository declares.</summary>
    private readonly HashSet<string> _typeParameters = new(StringComparer.Ordinal);

    /// <summary>
    /// The names of the values the file declares as it is read - its variables and fields, functions, enumerators and
    /// macros: a name in parentheses a code reads as a cast's or a <c>sizeof</c>'s type is a value where it is one of these.
    /// </summary>
    private readonly HashSet<string> _values = new(StringComparer.Ordinal);

    /// <summary>Whether the file's closed scope was declared.</summary>
    private bool _closedDeclared;

    /// <summary>Reads <paramref name="text"/>, the file <paramref name="path"/>.</summary>
    /// <param name="language">The implementation's name, which prefixes every id.</param>
    /// <param name="path">The file's path.</param>
    /// <param name="text">Its text.</param>
    /// <param name="cpp">Whether it is C++.</param>
    public CFamilyReading(string language, string path, CodeText text, bool cpp)
    {
        _scan = CFamilyLexer.Scan(text, cpp);
        _lines = text.LineIndex;
        _prefix = $"{language}:";
        _path = path;
        _cpp = cpp;
        _header = Headers.Any(ending => path.EndsWith(ending, StringComparison.OrdinalIgnoreCase));
        _keywords = cpp ? CppKeywords : CKeywords;
        _calls = cpp ? CppCalls : CCalls;
        _types = cpp ? CppTypes : null;
        _closed = QualifiedName.Of(CodeGraphIds.FileId(path));
        _guard = Guard();
        _conditional = Conditional();
    }

    /// <summary>
    /// Whether <paramref name="probe"/>, a file's text as its claim is tested, holds in its code a mark only C++ writes: <c>class</c>,
    /// <c>namespace</c>, <c>template</c>, an access label, <c>::</c> before a name, or an <c>#include</c> of a standard
    /// header with no extension (<c>&lt;vector&gt;</c>, <c>&lt;cstdio&gt;</c>) - an attribute's contents and what a
    /// conditional on <c>__cplusplus</c> holds, which a C header writes for C++'s includers, left out.
    /// </summary>
    public static bool HasCppMark(CodeText probe)
    {
        var scan = CFamilyLexer.Scan(probe, cpp: true);
        var forCpp = new Stack<bool>();

        for (var at = 0; at < scan.Count; at++)
        {
            if (scan[at].Kind == ScanTokenKind.Directive)
            {
                var (name, rest) = CFamilyLexer.DirectiveName(scan.Source, scan[at].Start, scan[at].End);

                switch (scan[at].Branch)
                {
                    case ScanBranch.Opens:
                        forCpp.Push(scan.Source.Find("__cplusplus", rest, scan[at].End) >= 0 || (forCpp.TryPeek(out var outer) && outer));
                        break;
                    case ScanBranch.Closes:
                        forCpp.TryPop(out _);
                        break;
                }

                if (name is "include" && !(forCpp.TryPeek(out var held) && held) && Included(scan.Source, rest, scan[at].End) is (false, var target)
                    && !target.Contains('.') && !target.Contains('/'))
                {
                    return true;
                }

                continue;
            }

            if (forCpp.TryPeek(out var under) && under)
            {
                continue;
            }

            if (scan.Is(at, "[") && scan.Is(scan.Next(at), "["))
            {
                at = scan.Past(at) - 1;
                continue;
            }

            if (scan.Is(at, "class") || scan.Is(at, "namespace") || scan.Is(at, "template")
                || (scan.Is(at, "::") && (scan.IsName(scan.Next(at)) || scan.Is(scan.Next(at), "~")))
                || ((scan.Is(at, "public") || scan.Is(at, "private") || scan.Is(at, "protected")) && scan.Is(scan.Next(at), ":")))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The file's extract.</summary>
    public FileExtract Read()
    {
        Directives();

        foreach (var stranded in _scan.Stranded)
        {
            _extract.Dynamic(_scan[stranded].Line, "a bracket one branch of a conditional leaves open and no other does: read as holding nothing", OpenBranch);
        }

        _walks.Walk(0, _scan.Count, at => Declaration(at, _scan.Count, ScanFrame.Top, local: false));
        _extract.Generated = ScanComments.Generated(_scan);
        return _extract.Build();
    }

    // ---- Directives: includes, macros, and what lies under a conditional ----

    /// <summary>
    /// Each directive: an <c>#include</c> as an import of a file - in quotes by a path relative to the file, and else by the
    /// path's ending; in angle brackets by its ending alone - and a <c>#define</c> as a macro.
    /// </summary>
    private void Directives()
    {
        for (var at = 0; at < _scan.Count; at++)
        {
            var token = _scan[at];

            if (token.Kind != ScanTokenKind.Directive)
            {
                continue;
            }

            var (name, rest) = CFamilyLexer.DirectiveName(_scan.Source, token.Start, token.End);

            switch (name)
            {
                case "include" or "include_next" or "import":
                    Include(token, rest);
                    break;

                // The include guard's own macro names nothing a reader looks for.
                case "define" when _guard < 0 || at != _guard + 1:
                    Define(at, rest);
                    break;
            }
        }
    }

    private void Include(ScanToken token, long rest)
    {
        if (Included(_scan.Source, rest, token.End) is not var (quoted, target))
        {
            return;
        }

        if (quoted)
        {
            _extract.Import(new ExtractImport(CodeImportForms.Relative, target, token.Line));
            _extract.Import(new ExtractImport(CodeImportForms.Ending, target, token.Line) { Fallback = true });
        }
        else
        {
            _extract.Import(new ExtractImport(CodeImportForms.Ending, target, token.Line));
        }
    }

    /// <summary>
    /// What the <c>#include</c> whose name ends at <paramref name="rest"/> and whose line ends at <paramref name="end"/>
    /// names: in quotes or in angle brackets, its path with forward separators; <see langword="null"/> where it names
    /// neither, as one a macro names does not.
    /// </summary>
    private static (bool Quoted, string Target)? Included(CodeText text, long rest, long end)
    {
        var at = rest;

        while (at < end && text[at] is ' ' or '\t')
        {
            at++;
        }

        if (at >= end || text[at] is not ('"' or '<'))
        {
            return null;
        }

        var quoted = text[at] == '"';
        var close = text.Find(quoted ? '"' : '>', at + 1, end);

        if (close < 0 || text.FindAny("\r\n", at + 1, close) >= 0)
        {
            return null;
        }

        var target = ExtractText.Whole(text.Slice(at + 1, close).Trim().Replace('\\', '/'));
        return target.Length > 0 ? (quoted, target) : null;
    }

    /// <summary>
    /// A <c>#define</c>: a macro, function-like where a parenthesis follows its name at once, each call its text makes a
    /// reference of it. A macro of a file that is no header is that file's alone, as what it declares <c>static</c> is.
    /// </summary>
    private void Define(int at, long rest)
    {
        var token = _scan[at];
        var body = CFamilyLexer.ScanDirective(_scan.Source, _lines, rest, token.End, _cpp);

        if (body.Count == 0 || !body.IsName(0))
        {
            return;
        }

        var name = body.Text(0);
        var functionLike = body.Is(1, "(") && body[1].Start == body[0].End;
        _values.Add(name);
        var past = functionLike ? body.Past(1) : 1;
        int? least = null;
        int? most = null;

        var taken = new HashSet<string>(StringComparer.Ordinal);

        if (functionLike)
        {
            _functionMacros.Add(name);
            var close = body.Match(1) < 0 ? body.Count : body.Match(1);
            var parameters = ScanCalls.Arity(body, 1, close, null);
            var variadic = Enumerable.Range(2, Math.Max(0, close - 2)).Any(index => body.Is(index, "..."));
            least = variadic ? parameters - 1 : parameters;
            most = variadic ? null : parameters;
            taken.UnionWith(body.Code(2, close).Where(body.IsName).Select(index => body.Text(index)));
        }

        var key = _extract.Add(new ExtractNode(Id(name, !_header), CodeNodeKinds.Member, name, name, token.Line, token.EndLine, ExtractText.Signature(ExtractText.Flat($"#define {body.Spelled(0, past)}")))
        {
            SubKind = "macro",
            Scope = Placed(null, !_header),
            Summary = ScanComments.Summary(_scan, at),
            Modifiers = Marks(at, []),
            MinArity = least,
            MaxArity = most,
        });

        Calls(body, past, body.Count, key, new Dictionary<string, QualifiedName?>(), [], owner: null, taken);
    }

    /// <summary>For each token, whether it lies under a conditional: under any but the file's include guard (<see cref="Guard"/>).</summary>
    private bool[] Conditional()
    {
        var under = new bool[_scan.Count];
        var opened = new Stack<int>();
        var depth = 0;

        for (var at = 0; at < _scan.Count; at++)
        {
            switch (_scan[at].Branch)
            {
                case ScanBranch.Opens:
                    opened.Push(at);
                    depth += at == _guard ? 0 : 1;
                    break;

                case ScanBranch.Closes when opened.TryPop(out var opener):
                    depth -= opener == _guard ? 0 : 1;
                    break;
            }

            under[at] = depth > 0;
        }

        return under;
    }

    /// <summary>
    /// The index of the file's include guard's conditional, where it has one - <c>#ifndef X</c>, <c>#if !defined X</c> or
    /// <c>#if !defined(X)</c> as its first token but a <c>#pragma once</c>, a <c>#define X</c> the next, the conditional's
    /// <c>#endif</c> the file's last token; -1 where it has none.
    /// </summary>
    private int Guard()
    {
        var opener = 0;

        while (opener < _scan.Count && _scan[opener].Kind == ScanTokenKind.Directive && _scan[opener].Branch == ScanBranch.None
            && CFamilyLexer.DirectiveName(_scan.Source, _scan[opener].Start, _scan[opener].End).Name == "pragma")
        {
            opener++;
        }

        if (opener + 2 >= _scan.Count || _scan[opener].Kind != ScanTokenKind.Directive || _scan[opener + 1].Kind != ScanTokenKind.Directive || _scan[_scan.Count - 1].Branch != ScanBranch.Closes)
        {
            return -1;
        }

        var (opens, guardedAt) = CFamilyLexer.DirectiveName(_scan.Source, _scan[opener].Start, _scan[opener].End);
        var (defines, definedAt) = CFamilyLexer.DirectiveName(_scan.Source, _scan[opener + 1].Start, _scan[opener + 1].End);
        var guarded = CFamilyLexer.ScanDirective(_scan.Source, _lines, guardedAt, _scan[opener].End, _cpp);
        var defined = CFamilyLexer.ScanDirective(_scan.Source, _lines, definedAt, _scan[opener + 1].End, _cpp);

        // The name it tests is not defined: #ifndef X, or #if !defined X, or #if !defined(X).
        var tested = opens switch
        {
            "ifndef" when guarded.Count > 0 && guarded.IsName(0) => guarded.Text(0),
            "if" when guarded.Is(0, "!") && guarded.Is(1, "defined") => guarded.IsName(2) ? guarded.Text(2) : guarded.Is(2, "(") && guarded.IsName(3) && guarded.Is(4, ")") ? guarded.Text(3) : null,
            _ => null,
        };

        if (tested is null || defines != "define" || defined.Count == 0 || defined.Text(0) != tested)
        {
            return -1;
        }

        // Its #endif is the file's last token only where every conditional after it closes before.
        var depth = 0;

        for (var at = opener; at < _scan.Count; at++)
        {
            depth += _scan[at].Branch switch { ScanBranch.Opens => 1, ScanBranch.Closes => -1, _ => 0 };

            if (depth == 0)
            {
                return at == _scan.Count - 1 ? opener : -1;
            }
        }

        return -1;
    }

    // ---- Declarations at a file's, a namespace's and a class's scope ----

    /// <summary>The declaration that begins at <paramref name="at"/>, read in <paramref name="frame"/>; answers where the next begins.</summary>
    private int Declaration(int at, int end, ScanFrame frame, bool local)
    {
        if (_scan[at].Kind == ScanTokenKind.Directive || _scan.Is(at, ";") || _scan.Is(at, "}") || _scan.Is(at, ")") || _scan.Is(at, "]"))
        {
            return at + 1;
        }

        if (_scan.Is(at, "{"))
        {
            // A block where no declaration has begun: what a macro left open.
            return _scan.Bounds(at, end).Past;
        }

        var start = at;

        if (frame.Owner is not null && _scan.IsName(at) && AccessLabels.Contains(_scan.Text(at)))
        {
            var label = _scan.Next(at);
            label = _scan.Is(label, "slots") ? _scan.Next(label) : label;

            if (_scan.Is(label, ":"))
            {
                return label + 1;
            }
        }

        at = Attributes(at, end);
        var templated = false;
        var named = new List<string>();

        while (_cpp && _scan.Is(at, "template"))
        {
            var less = _scan.Next(at);

            if (!_scan.Is(less, "<"))
            {
                // An explicit instantiation declares nothing new.
                return _scan.StatementEnd(at, end);
            }

            var past = _scan.AngleEnd(less, end);
            named.AddRange(TypeParameters(less, past).Where(name => !_typeParameters.Contains(name) && !named.Contains(name, StringComparer.Ordinal)));
            at = Attributes(past, end);
            templated = true;
        }

        if (named.Count == 0)
        {
            return Declared(start, at, end, frame, local, templated);
        }

        // A template's parameters are no types of the repository in what it declares - its members among it, read in walks
        // of their own after this reading, which are read holding them too.
        using (_walks.Holding(() => _typeParameters.UnionWith(named), () => _typeParameters.ExceptWith(named)))
        {
            return Declared(start, at, end, frame, local, templated);
        }
    }

    /// <summary>
    /// Does <paramref name="then"/> once every walk the reading under way has begun so far is read: what a declaration
    /// says after the blocks it holds - the variables after a type's body, the names a <c>typedef</c> gives it - kept in
    /// the order the text writes it.
    /// </summary>
    private void Then(Action then) => _walks.Walk(0, 0, at => at, end: then);

    /// <summary>The names a template's parameter list from <paramref name="less"/> to <paramref name="past"/> declares: each piece's last name before its default.</summary>
    private IEnumerable<string> TypeParameters(int less, int past)
    {
        foreach (var (start, end) in ScanCalls.Split(_scan, less + 1, _scan.Is(past - 1, ">") ? past - 1 : past, _types))
        {
            var name = _scan.Code(start, ScanCalls.TopLevel(_scan, start, end, "=", _types, SpellsOperator)).LastOrDefault(index => _scan.IsName(index) && !_keywords.Contains(_scan.Text(index)), -1);

            if (name >= 0)
            {
                yield return _scan.Text(name);
            }
        }
    }

    /// <summary>The declaration that begins at <paramref name="start"/>, its keyword at <paramref name="at"/> past its attributes and template heads.</summary>
    private int Declared(int start, int at, int end, ScanFrame frame, bool local, bool templated)
    {
        if (at >= end)
        {
            return end;
        }

        if (_cpp && (_scan.Is(at, "namespace") || (_scan.Is(at, "inline") && _scan.Is(_scan.Next(at), "namespace"))))
        {
            return Namespace(start, _scan.Is(at, "inline") ? _scan.Next(at) : at, end, frame, local);
        }

        if (_scan.Is(at, "extern") && _scan.Next(at) is var linkage && linkage < end && _scan[linkage].Kind == ScanTokenKind.Text)
        {
            var open = _scan.Next(linkage);

            if (_scan.Is(open, "{"))
            {
                var (inner, past) = _scan.Bounds(open, end);
                Within(open, inner, frame.Deeper(), local);
                return past;
            }

            at = open;
        }

        if (_cpp && _scan.Is(at, "using"))
        {
            return Using(start, at, end, frame, local);
        }

        if (_scan.Is(at, "typedef"))
        {
            return Typedef(start, at, end, frame, local);
        }

        if (_scan.Is(at, "static_assert") || _scan.Is(at, "_Static_assert") || _scan.Is(at, "friend"))
        {
            return _scan.StatementEnd(at, end);
        }

        // A type's keyword, the specifiers of what is declared with it before it: static const struct op { ... } ops[];
        var keyword = at;
        var specified = new HashSet<string>(StringComparer.Ordinal);

        while (keyword < end && _scan.IsName(keyword) && Specifiers.Contains(_scan.Text(keyword)) && !IsTypeKeyword(keyword))
        {
            specified.Add(_scan.Text(keyword));
            keyword = Attributes(_scan.Next(keyword), end);
        }

        if (IsTypeKeyword(keyword)
            && Type(start, keyword, end, frame, local, templated, local || (frame.Owner is null && specified.Contains("static") && !_header), specified.Contains("extern")) is var typed and > 0)
        {
            return typed;
        }

        return Other(start, at, end, frame, local, templated);
    }

    /// <summary>Whether the token at <paramref name="at"/> is a keyword a type is defined or named by: <c>class</c>, <c>struct</c>, <c>union</c>, <c>enum</c>.</summary>
    private bool IsTypeKeyword(int at) => _scan.Is(at, "class") || _scan.Is(at, "struct") || _scan.Is(at, "union") || _scan.Is(at, "enum");

    /// <summary>
    /// A namespace: a node and an open scope for each name it opens, its body read in it, two blocks of one in a file one
    /// node; an anonymous one's names its file's alone; an alias an import of what it stands for, by its name.
    /// </summary>
    private int Namespace(int start, int at, int end, ScanFrame frame, bool local)
    {
        var name = _scan.Next(at);
        var written = new List<string>();

        while (_scan.IsName(name) && !_keywords.Contains(_scan.Text(name)))
        {
            written.Add(_scan.Text(name));
            name = _scan.Next(name);

            if (!_scan.Is(name, "::"))
            {
                break;
            }

            name = _scan.Next(name);
        }

        name = Attributes(name, end);

        if (_scan.Is(name, "=") && written.Count == 1)
        {
            var (target, past) = Chain(_scan, _scan.Next(name), end, CppTypes);

            if (target is not null)
            {
                _extract.Import(new ExtractImport(CodeImportForms.Scope, target, _scan[start].Line) { Prefix = written[0] });
            }

            return _scan.StatementEnd(past, end);
        }

        if (!_scan.Is(name, "{"))
        {
            return _scan.StatementEnd(name, end);
        }

        var (inner, close) = _scan.Bounds(name, end);

        if (written.Count == 0)
        {
            // What an anonymous namespace declares is its file's alone - but a header's, which each file that includes it holds.
            Within(name, inner, frame.Deeper(), local: !_header);
            return close;
        }

        // Each name it writes a namespace of its own, held by the one before: namespace a::b is a part of a holding b.
        var scope = frame.Prefix;
        var parent = frame.Parent;

        foreach (var part in written)
        {
            var qualified = ExtractText.Qualify(scope, part);
            _extract.Scope(qualified, open: true, scope);
            parent = _extract.Part(new ExtractNode(Id(qualified, local: false), CodeNodeKinds.Namespace, part, qualified, _scan[start].Line, LastLine(close), _scan.Signature(start, name))
            {
                Scope = scope,
                Parent = parent,
                BodyStart = _scan[name].Line,
                Modifiers = Marks(start, []),
            });
            scope = qualified;
        }

        Within(name, inner, frame.Inside(scope, scope, parent, null), local);
        return close;
    }

    /// <summary>
    /// The declarations the brace at <paramref name="open"/> holds, to <paramref name="inner"/>, read in
    /// <paramref name="frame"/>: what each branch of a conditional writes after its own opener of the brace, the head the
    /// other branches repeat before theirs left out (<see cref="ScannedText.Inside"/>).
    /// </summary>
    private void Within(int open, int inner, ScanFrame frame, bool local)
    {
        foreach (var (from, to) in _scan.Inside(open, inner))
        {
            _walks.Walk(from, to, next => Declaration(next, to, frame, local));
        }
    }

    /// <summary>
    /// A <c>using</c>: <c>using namespace X;</c> and <c>using enum X;</c> imports of a scope; <c>using X::y;</c> an import of
    /// the scope with its one name; <c>using N = T;</c> a type of its own name that references <c>T</c>.
    /// </summary>
    private int Using(int start, int at, int end, ScanFrame frame, bool local)
    {
        var next = _scan.Next(at);

        if (_scan.Is(next, "namespace") || _scan.Is(next, "enum"))
        {
            var (target, past) = Chain(_scan, _scan.Next(next), end, CppTypes);

            if (target is not null && frame.Owner is null)
            {
                _extract.Import(new ExtractImport(CodeImportForms.Scope, target, _scan[start].Line));
            }

            return _scan.StatementEnd(past, end);
        }

        if (_scan.IsName(next) && Attributes(_scan.Next(next), end) is var equals && _scan.Is(equals, "="))
        {
            var stop = _scan.StatementEnd(next, end);
            var name = _scan.Text(next);
            var key = Declare(start, stop - 1, frame, local, name, CodeNodeKinds.Type, "alias", frame.Qualify(name), signatureEnd: stop - 1, templated: false);
            TypeReferences(_scan, key, _scan.Next(equals), stop - 1);
            return stop;
        }

        var (named, after) = Chain(_scan, next, end, CppTypes);

        if (named is not null && frame.Owner is null && named.LastIndexOf('.') is var dot and > 0)
        {
            _extract.Import(new ExtractImport(CodeImportForms.Scope, named[..dot], _scan[start].Line) { Show = [named[(dot + 1)..]] });
        }

        return _scan.StatementEnd(after, end);
    }

    /// <summary>
    /// A <c>typedef</c>: each name it declares a type of that name; one of a <c>struct</c>, <c>union</c>, <c>class</c> or
    /// <c>enum</c> with a body declares that type too, named by its tag - or by the first name the <c>typedef</c> gives,
    /// where it has none.
    /// </summary>
    private int Typedef(int start, int at, int end, ScanFrame frame, bool local)
    {
        var after = Attributes(_scan.Next(at), end);

        while (_scan.Is(after, "const") || _scan.Is(after, "volatile"))
        {
            after = _scan.Next(after);
        }

        if (_scan.Is(after, "struct") || _scan.Is(after, "union") || _scan.Is(after, "enum") || _scan.Is(after, "class"))
        {
            var head = TypeHead(after, end);

            if (head.Open >= 0)
            {
                var (inner, close) = _scan.Bounds(head.Open, end);
                var stop = _scan.StatementEnd(close, end);
                var names = Declarators(close, stop, typed: true);
                var tagged = head.Name ?? names.FirstOrDefault(found => !found.Pointer).Name;

                if (tagged is not null)
                {
                    DefineType(start, head, inner, frame, local, templated: false, tagged);
                }
                else
                {
                    Members(head, inner, frame.Deeper(), local, typeKey: null, outer: frame);
                }

                // The names it gives the type, once what the type's body declares is read.
                Then(() =>
                {
                    foreach (var declared in names.Where(found => found.Name is not null && found.Name != tagged))
                    {
                        var key = Declare(start, stop - 1, frame, local, declared.Name!, CodeNodeKinds.Type, "typedef", frame.Qualify(declared.Name!), signatureEnd: stop - 1, templated: false);

                        if (tagged is not null)
                        {
                            _extract.Reference(key, CodeEdgeKinds.References, tagged, _scan[declared.At].Line);
                        }
                    }
                });

                return stop;
            }
        }

        var end2 = _scan.StatementEnd(after, end);

        foreach (var declared in Declarators(after, end2).Where(found => found.Name is not null))
        {
            var key = Declare(start, end2 - 1, frame, local, declared.Name!, CodeNodeKinds.Type, "typedef", frame.Qualify(declared.Name!), signatureEnd: end2 - 1, templated: false);
            TypeReferences(_scan, key, after, declared.TypeEnd);
        }

        return end2;
    }

    /// <summary>What a <c>class</c>, <c>struct</c>, <c>union</c> or <c>enum</c> keyword begins.</summary>
    /// <param name="Name">Its tag; <see langword="null"/> where it has none.</param>
    /// <param name="Written">Its tag as written, the scopes before it joined by dots.</param>
    /// <param name="Keyword">The kind it is: <c>class</c>, <c>struct</c>, <c>union</c>, <c>enum</c> or <c>enum class</c>.</param>
    /// <param name="Bases">The index of its base list's first token; -1 where it has none.</param>
    /// <param name="Open">The index of its body's brace; -1 where no body follows.</param>
    /// <param name="Past">The index past its head.</param>
    private readonly record struct Head(string? Name, string? Written, string Keyword, int Bases, int Open, int Past)
    {
        public bool IsEnum => Keyword.StartsWith("enum", StringComparison.Ordinal);
    }

    /// <summary>
    /// The head the keyword at <paramref name="at"/> begins: its tag - the last name of its run where a body follows, a
    /// base list or a final between, since an export macro may stand before it (<c>class DllExport Widget {</c>), and a
    /// single name a base list or a final follows (<c>enum E : int;</c>); its first where C++'s brace initialises the
    /// last as a variable (<see cref="Initializes"/>) or two names or more are followed by no body - a bit-field's,
    /// <c>enum Color c : 4;</c> - a variable's name after it and no body; its bases, and its body's brace.
    /// </summary>
    private Head TypeHead(int at, int end)
    {
        var keyword = _scan.Text(at);
        var next = _scan.Next(at);

        if (keyword == "enum" && (_scan.Is(next, "class") || _scan.Is(next, "struct")))
        {
            keyword = "enum class";
            next = _scan.Next(next);
        }

        next = Attributes(next, end);
        var run = new List<(string Chain, int Past)>();

        while (_scan.IsName(next) && !_scan.Is(next, "final") && !_scan.Is(next, "sealed") && Chain(_scan, next, end, CppTypes) is ({ } chain, var past))
        {
            run.Add((chain, past));
            next = Attributes(past, end);
        }

        // After two names or more a base list or a final defines the last only where a body follows: enum Color c : 4; is a
        // bit-field of the type Color names.
        var defining = _scan.Is(next, "{")
            ? !(_cpp && run.Count > 1 && !run.SkipLast(1).All(before => IsMacroName(before.Chain)) && Initializes(keyword, next, end))
            : (_scan.Is(next, ":") || _scan.Is(next, "final") || _scan.Is(next, "sealed")) && (run.Count < 2 || _scan.Is(HeadEnd(next, end), "{"));

        if (run.Count > 1 && !defining)
        {
            next = run[0].Past;
            run.RemoveRange(1, run.Count - 1);
        }

        var written = run.Count > 0 ? (defining ? run[^1] : run[0]).Chain : null;

        while (_scan.Is(next, "final") || _scan.Is(next, "sealed"))
        {
            next = _scan.Next(next);
        }

        var bases = -1;

        if (_scan.Is(next, ":"))
        {
            bases = _scan.Next(next);
            next = HeadEnd(bases, end);
        }

        return new Head(written?[(written.LastIndexOf('.') + 1)..], written, keyword, bases, _scan.Is(next, "{") ? next : -1, next);
    }

    /// <summary>
    /// The index of the <c>{</c> or the <c>;</c> that ends a type's head from <paramref name="at"/> - its base list, its
    /// final - groups and type arguments passed over; <paramref name="end"/> where neither comes before.
    /// </summary>
    private int HeadEnd(int at, int end)
    {
        while (at < end && !_scan.Is(at, "{") && !_scan.Is(at, ";"))
        {
            at = _scan.Is(at, "(") || _scan.Is(at, "[") ? _scan.Past(at) : _scan.Is(at, "<") ? _scan.AngleEnd(at, end) : _scan.Next(at);
        }

        return Math.Min(at, end);
    }

    /// <summary>
    /// Whether the brace at <paramref name="open"/>, after <paramref name="keyword"/> and a run of names one before the last
    /// of which is written as no macro is, initialises the last as a variable of the type the first names, as C++ alone
    /// writes one - <c>struct timeval tv{};</c>, <c>struct sockaddr_in addr_{1, 2};</c> - rather than opening the body of a
    /// type an export macro stands before (<c>struct DllExport Widget { int w; };</c>): after <c>struct</c> or
    /// <c>union</c> where what it holds (<see cref="ScannedText.Inside"/>) holds at its top none of what a member list
    /// holds and an initialiser never does - a <c>;</c>, a function's body (<see cref="OpensFunctionBody"/>), an access
    /// label's colon; after an enumeration where it holds nothing, which an enumerator list is not; never after
    /// <c>class</c>, which C++ code writes before no variable's type - <c>class DllExport Empty {};</c> defines Empty.
    /// </summary>
    private bool Initializes(string keyword, int open, int end)
    {
        var (inner, _) = _scan.Bounds(open, end);
        var held = _scan.Inside(open, inner).ToList();

        if (keyword.StartsWith("enum", StringComparison.Ordinal))
        {
            return !held.Any(run => _scan.Code(run.Start, run.End).Any());
        }

        if (keyword is not ("struct" or "union"))
        {
            return false;
        }

        foreach (var (from, to) in held)
        {
            for (var at = from; at < to; at = _scan.Opens(at) ? _scan.Bounds(at, to).Past : at + 1)
            {
                if (_scan.Is(at, ";") || (_scan.Is(at, "{") && OpensFunctionBody(at))
                    || (_scan.Is(at, ":") && _scan.Previous(at) is var label && _scan.IsName(label) && AccessLabels.Contains(_scan.Text(label))))
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Whether the brace at <paramref name="open"/> opens a function's body: a parameter list's <c>)</c> before it, past
    /// the words and the reference marks that may follow one (<c>const</c>, <c>noexcept</c>, <c>override</c>, <c>&amp;</c>),
    /// which no lambda's capture stands before - <c>[](int a) { ... }</c> is a value an initialiser may hold.
    /// </summary>
    private bool OpensFunctionBody(int open)
    {
        var before = _scan.Previous(open);

        while ((_scan.IsName(before) && Trailers.Contains(_scan.Text(before))) || _scan.Is(before, "&"))
        {
            before = _scan.Previous(before);
        }

        return _scan.Is(before, ")") && _scan.Match(before) is var parameters and >= 0 && !_scan.Is(_scan.Previous(parameters), "]");
    }

    /// <summary>
    /// A type with a body, and the variables declared after it - their own file's alone where <paramref name="internals"/>,
    /// declared and not defined where <paramref name="externs"/>; 0 where the keyword at <paramref name="at"/> begins no
    /// body - a declaration that names a type it does not define, as <c>struct node *next(void);</c> does.
    /// </summary>
    private int Type(int start, int at, int end, ScanFrame frame, bool local, bool templated, bool internals, bool externs)
    {
        var head = TypeHead(at, end);

        if (head.Open < 0)
        {
            // A declaration of the tag alone declares nothing to read.
            return _scan.Is(head.Past, ";") && head.Name is not null ? head.Past + 1 : 0;
        }

        var (inner, close) = _scan.Bounds(head.Open, end);
        var stop = _scan.StatementEnd(close, end);
        var declarators = Declarators(close, stop, typed: true).Where(found => found.Name is not null).ToList();

        // An unnamed enumeration's members are its scope's, and an unnamed type's that declares no member its holder's - C11's
        // anonymous members; an unnamed struct's or union's elsewhere, or one a member is declared of, are no name of the holder.
        if (head.Name is not null)
        {
            DefineType(start, head, inner, frame, local, templated, head.Name);
        }
        else if (head.IsEnum || (frame.Owner is not null && declarators.Count == 0))
        {
            Members(head, inner, frame.Deeper(), local, typeKey: null, outer: frame);
        }

        // The variables after its body, once what its body declares is read.
        Then(() =>
        {
            foreach (var declared in declarators)
            {
                Variable(start, stop - 1, frame, internals, declared, head.Written is { } written ? frame.Qualify(written) : null, prototype: externs);
            }
        });

        return stop;
    }

    /// <summary>A type node of <paramref name="name"/> over its head and body, with its bases, and its members read in its own scope.</summary>
    private void DefineType(int start, Head head, int inner, ScanFrame frame, bool local, bool templated, string name)
    {
        var qualified = frame.Qualify(head.Name is not null ? head.Written! : name);
        var key = Declare(start, Math.Min(inner, _scan.Count - 1), frame, local, name, CodeNodeKinds.Type, head.Keyword, qualified, signatureEnd: head.Open, templated);

        _extract.Scope(qualified, open: !local, frame.Scope);

        if (head.Bases >= 0 && !head.IsEnum)
        {
            Bases(key, head.Bases, head.Open);
        }

        // The bodies its members define are read once its body is, so each sees every member of it: its members are read in
        // walks holding its own list of bodies, and the bodies once those walks are.
        var bodies = new List<Action>();
        List<Action>? outer = null;

        using (_walks.Holding(() => (outer, _bodies) = (_bodies, bodies), () => _bodies = outer))
        {
            Members(head, inner, frame.Inside(qualified, qualified, key, name), local, key, outer: frame);
        }

        Then(() =>
        {
            foreach (var body in bodies)
            {
                body();
            }
        });
    }

    /// <summary>
    /// The members of the body <paramref name="head"/> opens, read in <paramref name="frame"/> - the type's own where it is
    /// named, its holder's where it is not: an enumeration's enumerators, or a class's, a struct's or a union's declarations.
    /// </summary>
    private void Members(Head head, int inner, ScanFrame frame, bool local, QualifiedName? typeKey, ScanFrame outer)
    {
        if (head.IsEnum)
        {
            // An unscoped enumeration's names are its holder's, as C and C++ read them; its node still holds them.
            foreach (var (from, to) in _scan.Inside(head.Open, inner))
            {
                Enumerators(from, to, head.Keyword == "enum class" ? frame : outer, local, typeKey ?? frame.Parent);
            }

            return;
        }

        Within(head.Open, inner, frame, local);
    }

    /// <summary>
    /// Each enumerator from <paramref name="at"/> to <paramref name="inner"/>: a member named in <paramref name="holder"/>,
    /// held by the node of <paramref name="parent"/>, and its value - to the comma at its top, a type's arguments' commas
    /// among what it passes - code of the enumerator's, what it reads and holds read from there: <c>All = Read | Write</c>.
    /// </summary>
    private void Enumerators(int at, int inner, ScanFrame holder, bool local, QualifiedName? parent)
    {
        while (at < inner)
        {
            while (at < inner && _scan[at].Kind == ScanTokenKind.Directive)
            {
                at++;
            }

            if (at >= inner)
            {
                break;
            }

            QualifiedName? key = null;

            if (_scan.IsName(at))
            {
                var name = _scan.Text(at);
                var qualified = holder.Qualify(name);
                _values.Add(name);

                key = _extract.Add(new ExtractNode(Id(qualified, local), CodeNodeKinds.Member, name, qualified, _scan[at].Line, _scan[at].EndLine, ExtractText.Signature(name))
                {
                    SubKind = "enumerator",
                    Scope = Placed(holder.Scope, local && holder.Owner is null),
                    Parent = parent,
                    Summary = ScanComments.Summary(_scan, at),
                    Modifiers = Marks(at, []),
                });
            }

            // Past its value, and the comma after it.
            at = _scan.Next(at);
            var value = _scan.Is(at, "=") ? _scan.Next(at) : -1;

            while (at < inner && !_scan.Is(at, ","))
            {
                at = _scan.Opens(at) ? _scan.Past(at)
                    : _types is not null && _scan.Is(at, "<") && _scan.IsName(_scan.Previous(at)) && ScanCalls.TypeArgumentsEnd(_scan, at, _types) is var past and >= 0 ? past
                    : _scan.Next(at);
            }

            if (key is not null && value >= 0)
            {
                Calls(_scan, value, Math.Min(at, inner), key, new Dictionary<string, QualifiedName?>(), [], holder.Owner);
            }

            at = _scan.Next(at);
        }
    }

    /// <summary>A class's bases, each an <c>inherits</c> reference by its name, the scopes written before it its qualifier.</summary>
    private void Bases(QualifiedName key, int at, int open)
    {
        while (at < open)
        {
            while (_scan.Is(at, "public") || _scan.Is(at, "private") || _scan.Is(at, "protected") || _scan.Is(at, "virtual"))
            {
                at = _scan.Next(at);
            }

            var (chain, past) = Chain(_scan, at, open, CppTypes);

            if (chain is not null)
            {
                Mention(key, CodeEdgeKinds.Inherits, QualifiedName.Of(chain), _scan[at].Line);
            }

            at = Math.Max(past, at + 1);

            while (at < open && !_scan.Is(at, ","))
            {
                at = _scan.Opens(at) ? _scan.Past(at) : _scan.Is(at, "<") ? _scan.AngleEnd(at, open) : _scan.Next(at);
            }

            at = _scan.Next(at);
        }
    }

    // ---- Functions, prototypes, variables and fields ----

    /// <summary>
    /// A declaration no keyword began: a function with its body or a prototype, variables and fields, or a macro standing
    /// in the place of a declaration.
    /// </summary>
    private int Other(int start, int at, int end, ScanFrame frame, bool local, bool templated)
    {
        // An old-style definition, which C alone writes and only at a file's scope, is a function whose parameters' own
        // declarations are none of the file's.
        if (!_cpp && frame.Owner is null && OldStyle(at, end) is ({ } oldStyle, var oldBody))
        {
            var defined = _scan.Bounds(oldBody, end).Past;
            Function(start, at, oldStyle, oldBody, Math.Max(at, defined - 1), frame, local, templated, []);
            return defined;
        }

        // A name and its arguments where a declaration begins, with no type before it but specifiers, is a macro - static
        // DEFINE_MUTEX(lock) - but a constructor of the type it is in, or a pointer declared in parentheses. Specifiers before
        // it and a declaration after it make it a part of that declaration, read on past it with them: static EXPORT(1) int n;
        var specified = new List<string>();

        while (true)
        {
            var named = at;

            while (_scan.IsName(named) && Specifiers.Contains(_scan.Text(named)) && !IsTypeKeyword(named))
            {
                named = _scan.Next(named);
            }

            if (!(_scan.IsName(named) && !_keywords.Contains(_scan.Text(named)) && !Specifiers.Contains(_scan.Text(named)) && _scan.Text(named) != frame.Owner
                && _scan.Next(named) is var group && _scan.Is(group, "(") && !(_scan.Is(_scan.Next(group), "*") || _scan.Is(_scan.Next(group), "&") || _scan.Is(_scan.Next(group), "^"))))
            {
                break;
            }

            var rest = _scan.Past(group);
            var passed = specified.Count > 0;

            // Past a macro, a name and its arguments no declaration goes on after are the declaration's own: static
            // EXPORT(int) f(void) { ... }
            if (passed && (!_scan.IsName(rest) || rest >= end))
            {
                break;
            }

            if ((named == at && !passed) || !_scan.IsName(rest) || rest >= end)
            {
                return Macro(named, end, rest);
            }

            _extract.Dynamic(_scan[named].Line, $"the macro {_scan.Text(named)} stands in a declaration, which is not expanded", UnexpandedMacro);
            specified.AddRange(_scan.Code(at, named).Select(index => _scan.Text(index)));
            at = rest;
        }

        // A name alone on its line before what begins a declaration is a macro too: Qt's Q_OBJECT, glib's G_BEGIN_DECLS - one
        // written as a macro is before a declaration that names its own type, as G_BEGIN_DECLS before GType f(void) does.
        if (_scan.IsName(at) && !_keywords.Contains(_scan.Text(at)) && !Specifiers.Contains(_scan.Text(at)) && _scan.Next(at) is var after && after < end
            && _scan[after].Line > _scan[at].Line
            && (Beginners.Contains(_scan.Text(after)) || _scan.Is(after, "}")
                || (IsMacroName(_scan.Text(at)) && _scan.IsName(after) && _scan.IsName(_scan.Next(after)))))
        {
            _extract.Dynamic(_scan[at].Line, $"the macro {_scan.Text(at)} stands here in the place of a declaration, which is not seen", UnexpandedMacro);
            return after;
        }

        var (stop, body) = StatementOf(at, end);

        // What a branch left unbalanced is read as no declaration: its bracket is marked, and the rest of its branch passed over.
        if (body < 0 && _scan.Code(at, stop).Any(_scan.IsStranded))
        {
            return stop;
        }

        var past = body >= 0 ? _scan.Bounds(body, end).Past : stop;
        var last = Math.Max(at, past - 1);

        // A name and its arguments with no body, a literal among them, is a variable its type makes: Logger log("app");
        if (FunctionHead(at, body >= 0 ? body : stop - 1) is { } head && (body >= 0 || !Constructs(head)))
        {
            Function(start, at, head, body, last, frame, local, templated, specified);
            return past;
        }

        if (body >= 0)
        {
            _extract.Dynamic(_scan[at].Line, "a body follows what declares no function this reader knows - a macro's - and is not read", UnexpandedMacro);
            return past;
        }

        var declarators = Declarators(at, stop);

        if (declarators.All(found => found.Name is null) && _scan.IsName(at) && !_keywords.Contains(_scan.Text(at)) && !Specifiers.Contains(_scan.Text(at)))
        {
            _extract.Dynamic(_scan[at].Line, $"the macro {_scan.Text(at)} stands here in the place of a declaration, which is not seen", UnexpandedMacro);
            return stop;
        }

        var specifiers = specified.Concat(_scan.Code(at, stop).Select(index => _scan.Text(index))).ToHashSet(StringComparer.Ordinal);

        foreach (var declared in declarators.Where(found => found.Name is not null))
        {
            Variable(start, stop - 1, frame, local || (frame.Owner is null && specifiers.Contains("static") && !_header), declared, TypeKey(_scan, at, declared.TypeEnd, CppTypes), prototype: specifiers.Contains("extern"));
        }

        return stop;
    }

    /// <summary>Whether the list of what looks a function's parameters holds a literal before its default values - a number or a text - as no parameter is: the arguments a variable is made with.</summary>
    private bool Constructs(FunctionName head)
        => ScanCalls.Split(_scan, head.Open + 1, head.Close, _types)
            .Any(piece => ScanCalls.HoldsLiteral(_scan, piece.Start, ScanCalls.TopLevel(_scan, piece.Start, piece.End, "=", _types, SpellsOperator), _types));

    /// <summary>A macro standing where a declaration begins: marked, its arguments passed over - and its body, where one follows.</summary>
    private int Macro(int at, int end, int past)
    {
        _extract.Dynamic(_scan[at].Line, $"the macro {_scan.Text(at)} stands here in the place of a declaration, which is not seen", UnexpandedMacro);

        if (_scan.Is(past, ";"))
        {
            return past + 1;
        }

        if (_scan.Is(past, "{"))
        {
            var close = _scan.Bounds(past, end).Past;
            return _scan.Is(close, ";") ? close + 1 : close;
        }

        return past;
    }

    /// <summary>
    /// The index past the statement that begins at <paramref name="at"/>, and the index of its body's brace: -1 where it
    /// has none - a brace after <c>=</c>, after a name or after <c>]</c> is an initializer, and so is one after a name of a
    /// constructor's initializer list.
    /// </summary>
    private (int Stop, int Body) StatementOf(int at, int end)
    {
        var assigned = false;
        var initializers = false;
        var returns = false;

        for (var index = at; index < end; index++)
        {
            if (_scan[index].Kind == ScanTokenKind.Directive)
            {
                continue;
            }

            if (_scan.Is(index, ";"))
            {
                return (index + 1, -1);
            }

            if (_scan.Is(index, "}"))
            {
                return (index, -1);
            }

            // A bracket a conditional's branch left open ends the statement it stands in, with what is left of its branch.
            if (_scan.IsStranded(index))
            {
                return (Math.Min(_scan.BranchEnd(index), end), -1);
            }

            if (_scan.Is(index, "=") && !SpellsOperator(index))
            {
                assigned = true;
            }
            else if (_scan.Is(index, "->") && !assigned)
            {
                // A trailing return type: the brace after it is the body, whatever name it follows.
                returns = true;
            }
            else if (_scan.Is(index, ":") && !assigned && _scan.Previous(index) is var colon && (_scan.Is(colon, ")") || (_scan.IsName(colon) && Trailers.Contains(_scan.Text(colon)))))
            {
                initializers = true;
            }
            else if (_scan.Is(index, "{"))
            {
                var previous = _scan.Previous(index);
                var named = _scan.IsName(previous) && !Trailers.Contains(_scan.Text(previous)) && !IsMacroName(_scan.Text(previous)) && !_scan.Is(previous, "try");

                if (assigned || (!returns && (_scan.Is(previous, "]") || named || (initializers && _scan.Is(previous, ">")))))
                {
                    index = _scan.Bounds(index, end).Past - 1;
                    continue;
                }

                return (_scan.Bounds(index, end).Past, index);
            }
            else if (_scan.Is(index, "(") || _scan.Is(index, "["))
            {
                index = _scan.Bounds(index, end).Past - 1;
            }
        }

        return (end, -1);
    }

    /// <summary>Where a function's name stands, as its declaration writes it.</summary>
    /// <param name="Name">Its simple name: <c>f</c>, <c>~T</c>, <c>operator==</c>.</param>
    /// <param name="Written">The scopes written before its name, joined by dots; <see langword="null"/> where none are.</param>
    /// <param name="First">The index of the first token of its name, the scopes before it among them.</param>
    /// <param name="Open">The index of the parenthesis its parameters open.</param>
    /// <param name="Close">The index of the one that closes them.</param>
    private sealed record FunctionName(string Name, string? Written, int First, int Open, int Close)
    {
        /// <summary>
        /// The index of the first of the declarations an old-style definition - K&amp;R C's - declares its parameters by,
        /// after their list of names (<see cref="OldStyle"/>); -1 where its parentheses declare them.
        /// </summary>
        public int Declarations { get; init; } = -1;
    }

    /// <summary>
    /// The old-style definition at <paramref name="at"/> - K&amp;R C's, as vendored zlib writes it: names and stars, then
    /// a name and its parameters' names alone between parentheses, each of them declared after the list by declarations
    /// of their own that end in <c>;</c> - no more of them than the names - then its body; its head and the index of its
    /// body's brace; <see langword="null"/> where the tokens are no such definition.
    /// </summary>
    private (FunctionName Head, int Body)? OldStyle(int at, int end)
    {
        var open = at;

        while (open < end && (_scan.IsName(open) || _scan.Is(open, "*")))
        {
            open = _scan.Next(open);
        }

        if (open == at || !_scan.Is(open, "(") || _scan.Match(open) is not (var close and > 0) || close >= end || NameBefore(open) is not { } head
            || OldStyleNames(open, close) is not { } names)
        {
            return null;
        }

        var listed = names.ToHashSet(StringComparer.Ordinal);
        var body = _scan.Next(close);
        var declarations = 0;

        while (body < end && !_scan.Is(body, "{"))
        {
            if (++declarations > names.Count || ParameterDeclarationEnd(body, end, listed) is not (var stop and >= 0))
            {
                return null;
            }

            body = _scan.Next(stop);
        }

        return body < end && declarations > 0 ? (head with { Declarations = _scan.Next(close) }, body) : null;
    }

    /// <summary>
    /// The names the parentheses at <paramref name="open"/> and <paramref name="close"/> list as an old-style definition's
    /// do: each between commas a name alone, no keyword; <see langword="null"/> where any is anything else, or none is there.
    /// </summary>
    private List<string>? OldStyleNames(int open, int close)
    {
        var names = new List<string>();

        foreach (var (start, end) in ScanCalls.Split(_scan, open + 1, close, null))
        {
            if (_scan.Code(start, end).ToList() is not [var name] || !_scan.IsName(name) || _keywords.Contains(_scan.Text(name)))
            {
                return null;
            }

            names.Add(_scan.Text(name));
        }

        return names.Count > 0 ? names : null;
    }

    /// <summary>
    /// The index of the <c>;</c> that ends the declaration of an old-style definition's parameters at <paramref name="at"/>:
    /// specifiers, then declarators joined by commas, each naming one of <paramref name="listed"/> - alone, after stars,
    /// or in parentheses with a star (<c>int (*cmp)();</c>, <c>int (*rows)[10];</c>) - with its brackets and its
    /// parameters after it. -1 where the tokens are no such declaration, read no further than the first token that says
    /// so: a group among its specifiers that names none of <paramref name="listed"/>, as a macro's use is written
    /// (<c>X(red)</c>); a name, a star or any other token after a declarator but a comma - the next use after a group
    /// that names one; an end that comes first; or a declaration that declares none of them.
    /// </summary>
    private int ParameterDeclarationEnd(int at, int end, IReadOnlySet<string> listed)
    {
        if (!_scan.IsName(at))
        {
            return -1;
        }

        var declared = false;

        // Whether the token before ends a declarator: its name, or a group after it.
        var after = false;

        for (var index = at; index < end; index = _scan.Next(index))
        {
            if (_scan.Is(index, ";"))
            {
                return declared ? index : -1;
            }

            if (_scan.Is(index, ",") || _scan.Is(index, "*"))
            {
                if (after != _scan.Is(index, ","))
                {
                    return -1;
                }

                after = false;
            }
            else if (_scan.Is(index, "(") || _scan.Is(index, "["))
            {
                if (_scan.Match(index) is not (var close and > 0) || close >= end
                    || (!after && !(_scan.Is(index, "(") && _scan.Code(index, close).Any(name => _scan.IsName(name) && listed.Contains(_scan.Text(name))))))
                {
                    return -1;
                }

                declared = true;
                after = true;
                index = close;
            }
            else if (_scan.IsName(index) && !after)
            {
                after = listed.Contains(_scan.Text(index));
                declared |= after;
            }
            else
            {
                return -1;
            }
        }

        return -1;
    }

    /// <summary>
    /// The function the tokens from <paramref name="at"/> to <paramref name="last"/> declare: a name and its parameters,
    /// followed by nothing but what may end a function's header; <see langword="null"/> where they declare none - an
    /// <c>=</c> before them, as an initialized variable's.
    /// </summary>
    private FunctionName? FunctionHead(int at, int last)
    {
        for (var index = at; index < last; index = _scan.Next(index))
        {
            if (_scan.Is(index, "=") && !SpellsOperator(index))
            {
                return null;
            }

            if (_scan.Is(index, "[") && _scan.Is(_scan.Next(index), "["))
            {
                index = _scan.Past(index) - 1;
                continue;
            }

            if (_scan.Is(index, "<") && _cpp && _scan.IsName(_scan.Previous(index)) && !SpellsOperator(index))
            {
                index = _scan.AngleEnd(index, last) - 1;
                continue;
            }

            if (!_scan.Is(index, "("))
            {
                continue;
            }

            var close = _scan.Match(index);

            if (close < 0 || close > last)
            {
                return null;
            }

            if (NameBefore(index) is { } name && Ends(close, last))
            {
                return name;
            }

            // A group that names nothing - an attribute's, a macro's, a pointer's declarator - is passed over.
            index = close;
        }

        return null;
    }

    /// <summary>The function's name a parenthesis at <paramref name="open"/> follows, with the scopes written before it; <see langword="null"/> where none stands there.</summary>
    private FunctionName? NameBefore(int open)
    {
        var at = _scan.Previous(open);
        string name;
        int first;

        if (at < 0)
        {
            return null;
        }

        if (_scan.Is(at, ")") && _scan.Is(_scan.Previous(at), "(") && _scan.Is(_scan.Previous(_scan.Previous(at)), "operator"))
        {
            // operator(): its own parentheses, then its parameters'.
            first = _scan.Previous(_scan.Previous(at));
            name = "operator()";
        }
        else if (OperatorBefore(at) is var word and >= 0)
        {
            first = word;
            var spelled = TypeText(_scan.Next(word), at + 1, -1);
            name = spelled.Length > 0 && ScanLexing.IsNameStart(spelled[0]) ? $"operator {spelled}" : $"operator{spelled}";
        }
        else if (_scan.IsName(at) && !_keywords.Contains(_scan.Text(at)))
        {
            first = at;
            name = _scan.Text(at);

            if (_scan.Is(_scan.Previous(at), "~"))
            {
                first = _scan.Previous(at);
                name = "~" + name;
            }
        }
        else if (_cpp && _scan.Is(at, ">") && ScanCalls.TypeArgumentsStart(_scan, at, CppTypes) is var less and >= 0 && _scan.IsName(_scan.Previous(less)) && !_keywords.Contains(_scan.Text(_scan.Previous(less))))
        {
            // An explicit specialization's name, its arguments after it.
            first = _scan.Previous(less);
            name = _scan.Text(first);
        }
        else
        {
            return null;
        }

        // The scopes written before it - A::B::f, a template's arguments among them.
        var scopes = new List<string>();

        while (_scan.Is(_scan.Previous(first), "::"))
        {
            var part = _scan.Previous(_scan.Previous(first));

            if (_scan.Is(part, ">") && ScanCalls.TypeArgumentsStart(_scan, part, CppTypes) is var opens and >= 0)
            {
                part = _scan.Previous(opens);
            }

            if (!_scan.IsName(part) || _keywords.Contains(_scan.Text(part)))
            {
                break;
            }

            scopes.Add(_scan.Text(part));
            first = part;
        }

        // Read from its end, last first.
        scopes.Reverse();
        return new FunctionName(name, scopes.Count > 0 ? string.Join('.', scopes) : null, first, open, _scan.Match(open));
    }

    /// <summary>
    /// The index of the <c>operator</c> the tokens up to <paramref name="at"/> spell an operator's name after - a symbol, a
    /// word as <c>new[]</c> or a conversion's type, however long - walked back to what the grammar lets none of them hold:
    /// a parenthesis, a statement's end, a brace, or a comma outside a type's arguments, as
    /// <c>operator std::map&lt;int, int&gt;()</c> holds one; -1 where none comes before. An <c>operator</c> inside a type's
    /// arguments - <c>Box&lt;&amp;T::operator+, 3&gt; f(</c> - names an argument, not what the parentheses follow, but where
    /// what follows it is a symbol alone, whose <c>&gt;</c> is its own: <c>operator&gt;&gt;=</c>.
    /// </summary>
    private int OperatorBefore(int at)
    {
        // How many lists of a type's arguments the walk back is inside, by the >s it passed that no < closed yet; and whether
        // every token it passed is a symbol, as an operator's own name is.
        var arguments = 0;
        var symbols = true;

        for (var index = at; index >= 0; index = _scan.Previous(index))
        {
            if (_scan.Is(index, "operator") && (arguments == 0 || symbols))
            {
                return index;
            }

            if (_scan.Is(index, "(") || _scan.Is(index, ")") || _scan.Is(index, ";") || _scan.Is(index, "{") || _scan.Is(index, "}") || (_scan.Is(index, ",") && arguments == 0))
            {
                return -1;
            }

            arguments += _scan.Is(index, ">") ? 1 : _scan.Is(index, "<") && arguments > 0 ? -1 : 0;
            symbols &= _scan[index].Kind == ScanTokenKind.Punctuation;
        }

        return -1;
    }

    /// <summary>Whether the symbol at <paramref name="at"/> is one of an operator's name - <c>=</c> of <c>operator+=</c>, <c>&lt;</c> of <c>operator&lt;</c> - no initializer's or list's.</summary>
    private bool SpellsOperator(int at)
    {
        if (!_cpp)
        {
            return false;
        }

        for (var index = _scan.Previous(at); index >= 0 && at - index <= 3; index = _scan.Previous(index))
        {
            if (_scan.Is(index, "operator"))
            {
                return true;
            }

            if (_scan[index].Kind != ScanTokenKind.Punctuation || _scan.Opens(index) || _scan.Is(index, ")") || _scan.Is(index, "]"))
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>Whether what follows the parameters closed at <paramref name="close"/>, to <paramref name="last"/>, may end a function's header.</summary>
    private bool Ends(int close, int last)
    {
        for (var at = _scan.Next(close); at <= last && at < _scan.Count; at = _scan.Next(at))
        {
            if (_scan.Is(at, ";") || _scan.Is(at, "{") || _scan.Is(at, ":") || _scan.Is(at, "->") || _scan.Is(at, "requires") || _scan.Is(at, "try"))
            {
                return true;
            }

            if (_scan.Is(at, "="))
            {
                var value = _scan.Next(at);
                return _scan.Is(value, "default") || _scan.Is(value, "delete") || (value < _scan.Count && _scan[value].Kind == ScanTokenKind.Number && _scan.Text(value) == "0");
            }

            if (_scan.Is(at, "[") && _scan.Is(_scan.Next(at), "["))
            {
                at = _scan.Past(at) - 1;
                continue;
            }

            if ((_scan.IsName(at) && (Trailers.Contains(_scan.Text(at)) || IsMacroName(_scan.Text(at)))) || _scan.Is(at, "&"))
            {
                if (_scan.Is(_scan.Next(at), "("))
                {
                    at = _scan.Past(_scan.Next(at)) - 1;
                }

                continue;
            }

            return false;
        }

        return true;
    }

    /// <summary>Whether <paramref name="text"/>, a name, is written as a macro is: capitals, digits and underscores, two characters at least.</summary>
    private static bool IsMacroName(string text)
        => text.Length > 1 && text.All(character => char.IsAsciiLetterUpper(character) || char.IsAsciiDigit(character) || character == '_') && text.Any(char.IsAsciiLetterUpper);

    /// <summary>
    /// A function, or a prototype, with what its signature names and what its body calls; <paramref name="specified"/> the
    /// specifiers written before a macro it is read on past.
    /// </summary>
    private void Function(int start, int at, FunctionName head, int body, int last, ScanFrame frame, bool local, bool templated, IReadOnlyList<string> specified)
    {
        var specifiers = specified.Concat(_scan.Code(at, head.First).Select(index => _scan.Text(index))).ToList();
        var tail = _scan.Code(head.Close + 1, body >= 0 ? body : last + 1).ToList();
        _values.Add(head.Name);
        var fileLocal = local || (frame.Owner is null && head.Written is null && specifiers.Contains("static", StringComparer.Ordinal) && !_header);
        var parameters = ParametersOf(head);
        var defined = body >= 0 || tail.Any(index => _scan.Is(index, "=") && (_scan.Is(_scan.Next(index), "default") || _scan.Is(_scan.Next(index), "delete")));

        // A member defined outside its class is in that class's scope, as its scopes before its name say.
        var scopePrefix = head.Written is { } written ? frame.Qualify(written) : frame.Prefix;
        var qualified = ExtractText.Qualify(scopePrefix, head.Name);
        var owner = head.Written is { } scopes ? scopes[(scopes.LastIndexOf('.') + 1)..] : frame.Owner;
        var subKind = head.Name.StartsWith("operator", StringComparison.Ordinal) ? "operator"
            : head.Name.StartsWith('~') && head.Name[1..] == owner ? "destructor"
            : head.Name == owner ? "constructor"
            : frame.Owner is not null || head.Written is not null ? "method"
            : "function";
        var constant = _cpp && tail.TakeWhile(index => !_scan.Is(index, ":") && !_scan.Is(index, "->")).Any(index => _scan.Is(index, "const"));
        var name = _cpp ? $"{qualified}({string.Join(',', parameters.Types)}){(constant ? "const" : string.Empty)}" : qualified;
        var modifiers = specifiers.Where(Modifying.Contains).Distinct(StringComparer.Ordinal).ToList();

        modifiers.AddRange(((string[])["override", "final"]).Where(trailer => tail.Any(index => _scan.Is(index, trailer))));

        if (tail.Any(index => _scan.Is(index, "=") && _scan.Next(index) is var value && value < _scan.Count && _scan.Text(value) == "0"))
        {
            modifiers.Add("pure");
        }

        if (templated)
        {
            modifiers.Add("template");
        }

        var key = _extract.Add(new ExtractNode(Id(name, fileLocal), CodeNodeKinds.Member, head.Name, qualified, _scan[start].Line, _scan[last].EndLine, _scan.Signature(start, body >= 0 ? body : _scan.Is(last, ";") ? last : last + 1))
        {
            SubKind = subKind,
            Scope = Placed(scopePrefix ?? frame.Scope, fileLocal && frame.Owner is null && head.Written is null),
            Parent = frame.Parent,
            BodyStart = body >= 0 ? _scan[body].Line : null,
            Summary = ScanComments.Summary(_scan, start),
            Modifiers = Marks(start, modifiers),
            MinArity = parameters.Least,
            MaxArity = parameters.Most,
            Prototype = !defined,
        });

        // What its signature names: its return type, and its parameters' types.
        TypeReferences(_scan, key, at, head.First);

        foreach (var (typeStart, typeEnd) in parameters.TypeSpans)
        {
            TypeReferences(_scan, key, typeStart, typeEnd);
        }

        if (tail.Any(index => _scan.Is(index, "override")))
        {
            _extract.Reference(key, CodeEdgeKinds.Overrides, head.Name, _scan[head.Open].Line, parameters.Types.Count, new ExtractQualifier(CodeQualifierKinds.Base));
        }

        // What its parameters' defaults read and hold.
        foreach (var (defaultStart, defaultEnd) in parameters.Defaults)
        {
            Calls(_scan, defaultStart, defaultEnd, key, new Dictionary<string, QualifiedName?>(), [], owner);
        }

        if (body < 0)
        {
            return;
        }

        // The template parameters in force where it is written, which a body read later is read with.
        var typeParameters = _typeParameters.ToList();

        void Body()
        {
            var locals = new Dictionary<string, QualifiedName?>(parameters.Locals);
            var pointers = new HashSet<string>(parameters.Pointers, StringComparer.Ordinal);
            var added = typeParameters.Where(_typeParameters.Add).ToList();

            try
            {
                // A constructor's initializers: what each passes its member or base is what it calls.
                if (tail.FirstOrDefault(index => _scan.Is(index, ":"), -1) is var colon and >= 0)
                {
                    for (var item = _scan.Next(colon); item < body; item = _scan.Next(item))
                    {
                        if (_scan.Opens(item))
                        {
                            var (inner, past) = _scan.Bounds(item, body);
                            Calls(_scan, item + 1, inner, key, locals, pointers, owner);
                            item = past - 1;
                        }
                    }
                }

                foreach (var (from, to) in _scan.Inside(body, last))
                {
                    Calls(_scan, from, to, key, locals, pointers, owner);
                }
            }
            finally
            {
                _typeParameters.ExceptWith(added);
            }
        }

        // A body its type's body defines is read once the type's is, every member of the type known.
        if (frame.Owner is not null && head.Written is null && _bodies is not null)
        {
            _bodies.Add(Body);
        }
        else
        {
            Body();
        }
    }

    /// <summary>A function's parameters: their types as its id names them, the spans of those types, its arity, and the names each binds with its type.</summary>
    private sealed record Parameters(IReadOnlyList<string> Types, IReadOnlyList<(int Start, int End)> TypeSpans, int? Least, int? Most, IReadOnlyDictionary<string, QualifiedName?> Locals, IReadOnlySet<string> Pointers)
    {
        /// <summary>Each parameter's default, from past its <c>=</c> to its end: code of the function's.</summary>
        public IReadOnlyList<(int Start, int End)> Defaults { get; init; } = [];
    }

    private Parameters ParametersOf(FunctionName head)
    {
        if (head.Declarations >= 0)
        {
            return OldStyleParameters(head);
        }

        var (open, close) = (head.Open, head.Close);
        var types = new List<string>();
        var spans = new List<(int, int)>();
        var locals = new Dictionary<string, QualifiedName?>();
        var pointers = new HashSet<string>(StringComparer.Ordinal);
        var defaults = new List<(int Start, int End)>();
        var packs = 0;
        var variadic = false;
        var pieces = ScanCalls.Split(_scan, open + 1, close, _types);

        if (pieces.Count == 1 && pieces[0].End - pieces[0].Start == 1 && _scan.Is(pieces[0].Start, "void"))
        {
            return new Parameters([], [], 0, 0, locals, pointers);
        }

        if (pieces.Count == 0)
        {
            // In C an empty list says nothing of what the function takes.
            return new Parameters([], [], 0, _cpp ? 0 : null, locals, pointers);
        }

        foreach (var (start, end) in pieces)
        {
            // C's ... takes any number, and is no parameter of its own.
            if (_scan.Is(start, "...") && end - start == 1)
            {
                variadic = true;
                continue;
            }

            // A pack of C++'s - A... args - is a parameter its id names, and takes any number too.
            if (Packs(start, end))
            {
                variadic = true;
                packs++;
            }

            var stop = ScanCalls.TopLevel(_scan, start, end, "=", _types, SpellsOperator);
            if (stop < end)
            {
                defaults.Add((stop + 1, end));
            }

            var declared = Declarator(start, stop);
            var typeEnd = declared.Name is null ? stop : declared.TypeEnd;
            types.Add(TypeText(start, stop, declared.Name is null ? -1 : declared.At));
            spans.Add((start, typeEnd));

            if (declared.Name is { } name)
            {
                locals[name] = TypeKey(_scan, start, typeEnd, CppTypes);

                if ((declared.Pointer && declared.Function) || IsFunctionObject(_scan, start, typeEnd))
                {
                    pointers.Add(name);
                }
            }
        }

        return new Parameters(types, spans, types.Count - defaults.Count - packs, variadic ? null : types.Count, locals, pointers) { Defaults = defaults };
    }

    /// <summary>
    /// An old-style definition's parameters: as many as the names its parentheses list, each of the type its own
    /// declaration after them writes - <c>int</c>, as C reads one none declares - the spans of those types, and the names
    /// each binds with its type.
    /// </summary>
    private Parameters OldStyleParameters(FunctionName head)
    {
        var names = OldStyleNames(head.Open, head.Close) ?? [];
        var listed = names.ToHashSet(StringComparer.Ordinal);
        var types = new Dictionary<string, string>(StringComparer.Ordinal);
        var spans = new List<(int, int)>();
        var locals = new Dictionary<string, QualifiedName?>();
        var pointers = new HashSet<string>(StringComparer.Ordinal);

        foreach (var name in names)
        {
            types[name] = "int";
        }

        for (var at = head.Declarations; ParameterDeclarationEnd(at, _scan.Count, listed) is var stop and >= 0; at = _scan.Next(stop))
        {
            var declarators = Declarators(at, stop + 1);

            if (declarators.Count > 0)
            {
                spans.Add((at, declarators[0].TypeEnd));
            }

            foreach (var declared in declarators.Where(found => found.Name is not null && types.ContainsKey(found.Name)))
            {
                types[declared.Name!] = TypeText(at, declared.TypeEnd, -1);
                locals[declared.Name!] = TypeKey(_scan, at, declared.TypeEnd, CppTypes);

                if (declared.Pointer && declared.Function)
                {
                    pointers.Add(declared.Name!);
                }
            }
        }

        return new Parameters([.. names.Select(name => types[name])], spans, names.Count, names.Count, locals, pointers);
    }

    /// <summary>Whether the parameter from <paramref name="start"/> to <paramref name="end"/> takes any number of arguments: a <c>...</c> at its top, outside a type's arguments.</summary>
    private bool Packs(int start, int end)
    {
        for (var at = start; at < end; at++)
        {
            if (_scan.Is(at, "..."))
            {
                return true;
            }

            if (_scan.Opens(at))
            {
                at = Math.Min(_scan.Past(at), end) - 1;
            }
            else if (_cpp && _scan.Is(at, "<"))
            {
                at = _scan.AngleEnd(at, end) - 1;
            }
        }

        return false;
    }

    /// <summary>A variable or a field, of the type <paramref name="valueType"/> names.</summary>
    private void Variable(int start, int last, ScanFrame frame, bool local, DeclaratorName declared, QualifiedName? valueType, bool prototype)
    {
        var name = declared.Name!;
        var qualified = frame.Qualify(name);
        _values.Add(name);

        var key = _extract.Add(new ExtractNode(Id(qualified, local), CodeNodeKinds.Member, name, qualified, _scan[start].Line, _scan[last].EndLine, _scan.Signature(start, _scan.Is(last, ";") ? last : last + 1))
        {
            SubKind = frame.Owner is not null ? "field" : "variable",
            Scope = Placed(frame.Scope, local && frame.Owner is null),
            Parent = frame.Parent,
            Summary = ScanComments.Summary(_scan, start),
            Modifiers = Marks(start, [.. _scan.Code(start, declared.At).Select(index => _scan.Text(index)).Where(Modifying.Contains).Distinct(StringComparer.Ordinal)]),
            Prototype = prototype,
            ValueType = valueType?.Whole(),
        });

        // A function pointer of a file's or a namespace's scope is called by its name; a field, on its type's value.
        if ((declared.Pointer && declared.Function) || IsFunctionObject(_scan, start, declared.TypeEnd))
        {
            if (frame.Owner is { } type)
            {
                (_fieldPointers.TryGetValue(type, out var fields) ? fields : _fieldPointers[type] = new(StringComparer.Ordinal)).Add(name);
            }
            else
            {
                _pointers.Add(name);
            }
        }

        TypeReferences(_scan, key, start, declared.TypeEnd);

        // What its bounds and its initializer read and call; a function pointer's parameters, between, are its type's.
        var from = declared.At + 1;
        var to = Math.Min(declared.End, last + 1);

        if (declared.Pointer && declared.Function && _scan.Match(declared.TypeEnd) is var close and >= 0 && close < to)
        {
            Calls(_scan, from, close, key, new Dictionary<string, QualifiedName?>(), [], frame.Owner);
            from = Math.Min(_scan.Past(_scan.Next(close)), to);
        }

        Calls(_scan, from, to, key, new Dictionary<string, QualifiedName?>(), [], frame.Owner);
    }

    /// <summary>
    /// A node over the tokens from <paramref name="start"/> to <paramref name="last"/>, its signature the tokens before
    /// <paramref name="signatureEnd"/>; answers its key.
    /// </summary>
    private QualifiedName Declare(int start, int last, ScanFrame frame, bool local, string name, string kind, string subKind, QualifiedName qualified, int signatureEnd, bool templated)
        => _extract.Add(new ExtractNode(Id(qualified, local), kind, name, qualified, _scan[start].Line, _scan[Math.Clamp(last, start, _scan.Count - 1)].EndLine, _scan.Signature(start, signatureEnd))
        {
            SubKind = subKind,
            Scope = Placed(frame.Scope, local && frame.Owner is null),
            Parent = frame.Parent,
            BodyStart = _scan.Is(signatureEnd, "{") ? _scan[signatureEnd].Line : null,
            Summary = ScanComments.Summary(_scan, start),
            Modifiers = Marks(start, templated ? ["template"] : []),
        });

    // ---- What a body calls ----

    /// <summary>
    /// Each call the tokens of <paramref name="scan"/> from <paramref name="from"/> to <paramref name="to"/> make, from the
    /// node of <paramref name="key"/>: a reference by its name and arity, qualified by its receiver where the code says,
    /// and what it cannot bind marked dynamic. <paramref name="locals"/> are the names it binds with their types, which the
    /// variables it declares join; <paramref name="pointers"/> those that are function pointers; <paramref name="owner"/>
    /// the simple name of the type whose members it sees; <paramref name="taken"/> the parameters of the macro it is the
    /// text of, which stand for what each use gives. Read on the reader's stack of code readings (<see cref="Run"/>), so
    /// what the code nests in what - a placement's arguments in a <c>new</c>, a cast's type, a template's arguments, at any
    /// depth - reaches past no stack.
    /// </summary>
    private void Calls(ScannedText scan, int from, int to, QualifiedName key, Dictionary<string, QualifiedName?> locals, HashSet<string> pointers, string? owner, IReadOnlySet<string>? taken = null)
        => Run(new CallsReading(this, scan, from, to, key, locals, pointers, owner, taken));

    /// <summary>
    /// Reads <paramref name="reading"/> on a stack of its own: each reading it begins - a cast's type in an expression, a
    /// template's arguments in a type, an argument of those in turn - is read before the one that began it goes on, so the
    /// references come in the order the code writes them and no nesting of code reaches past the reader's stack.
    /// </summary>
    private static void Run(CodeReading reading)
    {
        var readings = new Stack<CodeReading>();
        readings.Push(reading);

        while (readings.TryPeek(out var top))
        {
            if (top.Step() is { } begun)
            {
                readings.Push(begun);
            }
            else
            {
                readings.Pop();
            }
        }
    }

    /// <summary>
    /// One reading of code on the reader's stack of them (<see cref="Run"/>): what a run of tokens calls and uses, the
    /// types a run names, a template's arguments. Each keeps where it stands, so where it reads another inside it it answers
    /// that one, read first, and goes on from where it stood once that one is read.
    /// </summary>
    private abstract class CodeReading
    {
        /// <summary>Reads on from where it stands: answers the reading it begins, read before it goes on; none once it is read whole.</summary>
        public abstract CodeReading? Step();
    }

    /// <summary>What a run of a body's tokens calls and uses (<see cref="Calls"/>), read token by token.</summary>
    private sealed class CallsReading(CFamilyReading reader, ScannedText scan, int from, int to, QualifiedName key, Dictionary<string, QualifiedName?> locals, HashSet<string> pointers, string? owner, IReadOnlySet<string>? taken)
        : CodeReading
    {
        /// <summary>The index of the next token read.</summary>
        private int _at = from;

        /// <summary>The closing brackets of the arrays a new makes, which call no element.</summary>
        private HashSet<int>? _bounds;

        /// <summary>The index of a <c>new</c> whose placement's arguments were read: what it makes is read next; -1 where none.</summary>
        private int _made = -1;

        public override CodeReading? Step()
        {
            if (_made >= 0)
            {
                _at = reader.New(scan, _made, key, _bounds ??= []);
                _made = -1;
            }

            for (; _at < to && _at < scan.Count; _at++)
            {
                var at = _at;

                if (scan[at].Kind != ScanTokenKind.Name)
                {
                    // A string's value, as its lexer read its form and its escapes, is its data.
                    foreach (var (value, part) in scan.LiteralParts(scan[at]))
                    {
                        reader._extract.Literal(value, part, key);
                    }

                    // A type the code writes - a cast's, a sizeof's, a template's arguments - is no value it reads: its names
                    // are references, and the reading goes on past it.
                    if (scan.Is(at, "(") && reader.TypeInParentheses(scan, at, locals, pointers, taken) is var typed and >= 0)
                    {
                        _at = typed + 1;
                        return new TypeReferencesReading(reader, scan, key, at + 1, Math.Min(typed, to), locals, pointers, owner, taken);
                    }

                    if (reader._cpp && scan.Is(at, "<") && ScanCalls.TypeArgumentsAt(scan, at, CppCodeTypes) is var listed and >= 0)
                    {
                        _at = listed;
                        return new TemplateArgumentsReading(reader, scan, at, Math.Min(listed, to), key, locals, pointers, owner, taken);
                    }

                    if (scan.Is(at, "(") && scan.Next(at) is var star && scan.Is(star, "*") && scan.IsName(scan.Next(star)) && scan.Is(scan.Next(scan.Next(star)), ")")
                        && scan.Next(scan.Next(scan.Next(star))) is var parameters && scan.Is(parameters, "("))
                    {
                        if (!reader.Declares(scan, scan.Previous(at), close: -1))
                        {
                            reader._extract.Dynamic(scan[at].Line, $"a call through the function pointer {scan.Text(scan.Next(star))}", PointerCall);
                        }
                        else
                        {
                            // A function pointer it declares - int (*fallback)(struct lexer *) - is a pointer of its own, and
                            // the names of its parameters are its type's: none is a value read.
                            reader.Local(scan, scan.Next(star), locals, pointers);
                            _at = Math.Min(scan.Past(parameters), to) - 1;
                        }
                    }
                    else if (reader._cpp && scan.Is(at, "[") && Bindings(scan, at) is var bound and >= 0)
                    {
                        // A structured binding's names - auto& [key, value] - are its own variables.
                        for (var name = at + 1; name < bound; name++)
                        {
                            if (scan.IsName(name))
                            {
                                locals[scan.Text(name)] = null;
                            }
                        }

                        _at = bound;
                    }
                    else if ((scan.Is(at, ".") || scan.Is(at, "->")) && scan.Is(scan.Next(at), "*"))
                    {
                        reader._extract.Dynamic(scan[at].Line, "a call through a pointer to a member", PointerCall);
                    }
                    else if (scan.Is(at, "]") && scan.Is(scan.Next(at), "(") && scan.Match(at) is var index and >= 0 && _bounds?.Contains(at) != true
                        && ((scan.IsName(scan.Previous(index)) && !reader._keywords.Contains(scan.Text(scan.Previous(index)))) || scan.Is(scan.Previous(index), ")") || scan.Is(scan.Previous(index), "]")))
                    {
                        // An element called - table[0](1) - is a function pointer; a lambda's captures, which follow no value, are not.
                        reader._extract.Dynamic(scan[at].Line, "a call through an element, a function pointer", PointerCall);
                    }

                    continue;
                }

                if (reader._cpp && scan.Is(at, "new"))
                {
                    // A placement's arguments are called as any are, before what the new makes.
                    if (scan.Next(at) is var placement && scan.Is(placement, "("))
                    {
                        _made = at;
                        return new CallsReading(reader, scan, placement + 1, Math.Min(scan.Past(placement) - 1, to), key, locals, pointers, owner, taken);
                    }

                    _at = reader.New(scan, at, key, _bounds ??= []) - 1;
                    continue;
                }

                // A declaration's type - Node *n, const std::vector<Item>& items, a lambda's or a catch's parameter's - names
                // types, no value the code reads: its references, and the reading goes on at its declarator's name.
                if (reader.DeclaredType(scan, at) is { } declared && declared.Name < to)
                {
                    _at = declared.Name;
                    return new TypeReferencesReading(reader, scan, key, at, declared.TypeEnd, locals, pointers, owner, taken);
                }

                reader.Call(scan, at, key, locals, pointers, owner, taken);
            }

            return null;
        }
    }

    /// <summary>
    /// The name at <paramref name="at"/> as a body's code writes it, past what <see cref="CallsReading"/> reads around it:
    /// a call by its name and arity, qualified by its receiver where the code says, or a use of what it reads; what it
    /// cannot bind marked dynamic.
    /// </summary>
    private void Call(ScannedText scan, int at, QualifiedName key, Dictionary<string, QualifiedName?> locals, HashSet<string> pointers, string? owner, IReadOnlySet<string>? taken)
    {
        var word = scan.Text(at);
        Local(scan, at, locals, pointers);

        if (ScanCalls.At(scan, at, _calls) is not { } call)
        {
            Used(scan, at, key, locals, pointers, taken);
            return;
        }

        var previous = scan.Previous(at);
        var line = scan[at].Line;

        if (scan.Is(previous, ".") || scan.Is(previous, "->"))
        {
            if (!_cpp)
            {
                _extract.Dynamic(line, $"a call through the member {word}, which C makes through a function pointer", PointerCall);
                return;
            }

            var receiver = Receiver(scan, previous, locals);

            if (receiver.Kind == CodeQualifierKinds.This ? IsFieldPointer(owner, word) : receiver is { Kind: CodeQualifierKinds.Type, Name: { } type } && IsFieldPointer(type.PastLastDot(), word))
            {
                _extract.Dynamic(line, $"a call through the function pointer {word}", PointerCall);
                return;
            }

            _extract.Reference(key, CodeEdgeKinds.Calls, word, line, call.Arity, receiver);
            return;
        }

        if (scan.Is(previous, "::"))
        {
            _extract.Reference(key, CodeEdgeKinds.Calls, word, line, call.Arity, Qualifier(scan, previous) is { } scope ? new ExtractQualifier(CodeQualifierKinds.Named, scope) : null);
            return;
        }

        if (Declares(scan, previous, call.Close))
        {
            if (_cpp && locals.TryGetValue(word, out var made) && made is not null)
            {
                Mention(key, CodeEdgeKinds.Instantiates, made, line);
            }

            return;
        }

        if (taken?.Contains(word) == true)
        {
            _extract.Dynamic(line, $"a call of what the macro's parameter {word} stands for", UnexpandedMacro);
            return;
        }

        if (pointers.Contains(word) || (!locals.ContainsKey(word) && (_pointers.Contains(word) || IsFieldPointer(owner, word))))
        {
            _extract.Dynamic(line, $"a call through the function pointer {word}", PointerCall);
            return;
        }

        if (_functionMacros.Contains(word))
        {
            _extract.Dynamic(line, $"a call of the function-like macro {word}, which is not expanded", UnexpandedMacro);
        }
        else if (IsMacroName(word))
        {
            _extract.Dynamic(line, $"a call of {word}, named as a macro is - one another file defines, which is not expanded", UnexpandedMacro);
        }

        _extract.Reference(key, CodeEdgeKinds.Calls, word, line, call.Arity);
    }

    /// <summary>
    /// A member read or written without a call of it - <c>s.field</c>, <c>p-&gt;field</c>, <c>Enum::member</c> - or a bare
    /// name the code reads, the first of a receiver's chain among them: a use of the member by its name, qualified by what
    /// its receiver is where the file says. A local, a function pointer the code declares, a parameter of the macro it is
    /// the text of (<paramref name="taken"/>), a word of the language, a template's type parameter, a label - one a
    /// <c>goto</c> names among them - and a type - one <c>struct</c>, <c>union</c>, <c>enum</c> or <c>class</c> names, one
    /// a declaration's name follows, a scope a <c>::</c> follows, a template a list of its arguments does - is none; a type
    /// a brace initializes, <c>Point{1, 2}</c>, is one made, as a <c>new</c>'s is; a name a <c>case</c> or a conditional
    /// writes before its colon is a value the code reads, and is.
    /// </summary>
    private void Used(ScannedText scan, int at, QualifiedName key, Dictionary<string, QualifiedName?> locals, HashSet<string> pointers, IReadOnlySet<string>? taken)
    {
        var word = scan.Text(at);
        var previous = scan.Previous(at);
        var next = scan.Next(at);
        var line = scan[at].Line;

        if (scan.Is(previous, ".") || scan.Is(previous, "->"))
        {
            // The member a chain's next name links its qualifier to.
            _members[(scan, at)] = _extract.Reference(key, CodeEdgeKinds.Uses, word, line, qualifier: Receiver(scan, previous, locals));
            return;
        }

        var scoped = _cpp && scan.Is(previous, "::");
        var tagged = scan.Is(previous, "struct") || scan.Is(previous, "union") || scan.Is(previous, "enum") || scan.Is(previous, "class") || scan.Is(previous, "namespace") || scan.Is(previous, "typename");

        // A template's name before its arguments, and a type a brace initializes.
        if (_cpp && (scan.Is(next, "{") || scan.Is(next, "<")) && !_keywords.Contains(word) && !tagged
            && (scoped || !(locals.ContainsKey(word) || pointers.Contains(word) || taken?.Contains(word) == true || _typeParameters.Contains(word))))
        {
            var listed = scan.Is(next, "<") ? ScanCalls.TypeArgumentsAt(scan, next, CppCodeTypes) : next;

            if (scan.Is(listed, "{"))
            {
                Mention(key, CodeEdgeKinds.Instantiates, QualifiedName.Of(scoped && Qualifier(scan, previous) is { } scopes ? $"{scopes}.{word}" : word), line);
                return;
            }

            if (listed >= 0)
            {
                return;
            }
        }

        if (scoped)
        {
            _extract.Reference(key, CodeEdgeKinds.Uses, word, line, qualifier: Qualifier(scan, previous) is { } scope ? new ExtractQualifier(CodeQualifierKinds.Named, scope) : null);
            return;
        }

        if (locals.ContainsKey(word) || pointers.Contains(word) || taken?.Contains(word) == true || _keywords.Contains(word) || StatementWords.Contains(word) || Builtins.Contains(word)
            || _typeParameters.Contains(word) || ScanCalls.NamesLabel(scan, at) || tagged
            || (scan.IsName(next) && !_keywords.Contains(scan.Text(next))) || scan.Is(next, "::"))
        {
            return;
        }

        if (scan.Is(next, ":") && !scan.Is(previous, "case") && !ScanCalls.IsConditionalColon(scan, next))
        {
            return;
        }

        _extract.Reference(key, CodeEdgeKinds.Uses, word, line);
    }

    /// <summary>
    /// Where the <c>]</c> of the structured binding whose <c>[</c> is at <paramref name="open"/> stands -
    /// <c>auto [a, b] = pair</c>, <c>auto&amp; [key, value] : map</c>; -1 where it opens none.
    /// </summary>
    private static int Bindings(ScannedText scan, int open)
    {
        var before = scan.Previous(open);

        while (scan.Is(before, "&") || scan.Is(before, "&&"))
        {
            before = scan.Previous(before);
        }

        if (!scan.Is(before, "auto") || scan.Match(open) is not (var close and >= 0))
        {
            return -1;
        }

        var after = scan.Next(close);
        return scan.Is(after, "=") || scan.Is(after, ":") || scan.Is(after, "{") || scan.Is(after, "(") ? close : -1;
    }

    /// <summary>
    /// Whether a name and its arguments after the token at <paramref name="previous"/>, closed at <paramref name="close"/>,
    /// declare - a prototype, or in C++ a variable its type makes: after a type's name or one C builds in, but no
    /// statement's word (<c>Foo foo(1)</c>); after a template's arguments closing a type's name that begins a statement
    /// (<c>std::lock_guard&lt;std::mutex&gt; lock(m)</c>), or an <c>if</c>'s or a <c>for</c>'s first statement
    /// (<c>if (std::lock_guard&lt;std::mutex&gt; lock(m); ready)</c>).
    /// </summary>
    private bool Declares(ScannedText scan, int previous, int close)
    {
        if (scan.IsName(previous))
        {
            var before = scan.Text(previous);
            return !StatementWords.Contains(before) && (!_keywords.Contains(before) || Builtins.Contains(before));
        }

        if (!_cpp || !scan.Is(previous, ">") || ScanCalls.TypeArgumentsStart(scan, previous, CppCodeTypes) is not (var less and >= 0) || !scan.IsName(scan.Previous(less)))
        {
            return false;
        }

        var first = scan.Previous(less);

        while (scan.Is(scan.Previous(first), "::") && scan.IsName(scan.Previous(scan.Previous(first))))
        {
            first = scan.Previous(scan.Previous(first));
        }

        return Begins(scan, first) switch
        {
            Opening.Statement => true,
            Opening.For or Opening.Condition => EndsFirstStatement(scan, close),
            _ => false,
        };
    }

    /// <summary>Whether the bracket at <paramref name="close"/> ends the first statement an <c>if</c>'s or a <c>for</c>'s parenthesis holds: a <c>;</c> follows it.</summary>
    private static bool EndsFirstStatement(ScannedText scan, int close) => close >= 0 && close < scan.Count && scan.Is(scan.Next(close), ";");

    /// <summary>Whether the type <paramref name="type"/> names, by its simple name, holds <paramref name="name"/> as a function pointer.</summary>
    private bool IsFieldPointer(string? type, string name) => type is not null && _fieldPointers.TryGetValue(type, out var fields) && fields.Contains(name);

    /// <summary>
    /// A <c>new</c>: an <c>instantiates</c> reference to the type it makes, and the closing brackets of an array's bounds
    /// among <paramref name="bounds"/>; answers the index past that type's name, what the bounds hold read on from it.
    /// </summary>
    private int New(ScannedText scan, int at, QualifiedName key, HashSet<int> bounds)
    {
        var next = scan.Next(at);

        // A placement's arguments come first.
        if (scan.Is(next, "("))
        {
            next = scan.Past(next);
        }

        var (chain, past) = Chain(scan, next, scan.Count, CppCodeTypes);

        if (chain is not null)
        {
            Mention(key, CodeEdgeKinds.Instantiates, QualifiedName.Of(chain), scan[next].Line);
        }
        else if (scan.IsName(next))
        {
            // A type C builds in.
            past = scan.Next(next);
        }

        // An array's bounds are the new's own: new Foo[n]() calls no element.
        for (var bound = past; scan.Is(bound, "[") && scan.Match(bound) is var close and >= 0; bound = scan.Next(close))
        {
            bounds.Add(close);
        }

        return Math.Max(past, at + 1);
    }

    /// <summary>
    /// The variable a body declares at <paramref name="at"/>, where it declares one: its name bound to the type it is
    /// declared with - written before it, or made by <c>new</c> or a type's name after <c>auto</c> - and a function
    /// pointer's name among <paramref name="pointers"/>; and each later declarator of a statement's declaration.
    /// </summary>
    private void Local(ScannedText scan, int at, Dictionary<string, QualifiedName?> locals, HashSet<string> pointers)
    {
        var next = scan.Next(at);
        var previous = scan.Previous(at);
        var name = scan.Text(at);

        if (scan.Is(previous, "*") && scan.Is(scan.Previous(previous), "(") && scan.Is(next, ")") && scan.Is(scan.Next(next), "("))
        {
            pointers.Add(name);
            return;
        }

        if (Declaring(scan, at) is not { } declaration)
        {
            return;
        }

        var (typeStart, typeEnd, typeName, opening) = declaration;

        if (scan.Is(typeName, "auto") && scan.Is(next, "="))
        {
            var made = scan.Next(next);
            made = scan.Is(made, "new") ? scan.Next(made) : made;
            var (chain, past) = Chain(scan, made, scan.Count, CppCodeTypes);
            locals[name] = chain is not null && (scan.Is(past, "(") || scan.Is(past, "{")) ? QualifiedName.Of(chain) : null;
        }
        else
        {
            locals[name] = TypeKey(scan, typeStart, typeEnd, CppCodeTypes);

            if (IsFunctionObject(scan, typeStart, typeEnd))
            {
                pointers.Add(name);
            }
        }

        // A statement's later declarators - int a = 1, *b, c[2] - are variables of the same declaration, of its type.
        if (opening is Opening.Statement or Opening.For)
        {
            foreach (var later in ScanCalls.LaterDeclarators(scan, at, scan.Count, lineEnds: false))
            {
                var declarator = later;

                while (scan.Is(declarator, "*") || scan.Is(declarator, "&") || scan.Is(declarator, "&&"))
                {
                    declarator = scan.Next(declarator);
                }

                var after = scan.Next(declarator);

                if (scan.IsName(declarator) && !_keywords.Contains(scan.Text(declarator))
                    && (scan.Is(after, "=") || scan.Is(after, ",") || scan.EndsStatement(after) || scan.Is(after, "[") || scan.Is(after, "(") || scan.Is(after, "{")))
                {
                    locals[scan.Text(declarator)] = locals[name];
                }
            }
        }
    }

    /// <summary>
    /// The declaration whose declarator's name is at <paramref name="at"/> in a body, where one is: where its type begins,
    /// where it ends, the type's last name, and where the declaration begins; <see langword="null"/> where the name
    /// declares nothing. The one rule a body's variable is told by - its type written before it, a name C builds in or one
    /// of the file's, its pointers' and references' marks after; begun where a statement or a declaration a statement's
    /// parentheses hold begins, no value an operator joins, as <c>a * b</c> and <c>a &amp;&amp; b</c> are; and followed by
    /// what the place it begins in declares with: its value, its end, its arguments, an array's bounds, a range-for's colon,
    /// or a catch's or a lambda's closing parenthesis, a condition's variable given its value.
    /// </summary>
    private (int TypeStart, int TypeEnd, int TypeName, Opening Opening)? Declaring(ScannedText scan, int at)
    {
        var next = scan.Next(at);
        var previous = scan.Previous(at);

        if (!(scan.Is(next, "=") || scan.Is(next, ";") || scan.Is(next, "(") || scan.Is(next, "{") || scan.Is(next, ",") || scan.Is(next, "[") || scan.Is(next, ":") || scan.Is(next, ")")))
        {
            return null;
        }

        var typeEnd = previous + 1;

        while (scan.Is(previous, "*") || scan.Is(previous, "&"))
        {
            previous = scan.Previous(previous);
        }

        if (scan.Is(previous, ">") && ScanCalls.TypeArgumentsStart(scan, previous, CppCodeTypes) is var less and >= 0)
        {
            previous = scan.Previous(less);
        }

        if (!scan.IsName(previous) || StatementWords.Contains(scan.Text(previous)) || (_keywords.Contains(scan.Text(previous)) && !Builtins.Contains(scan.Text(previous)) && !Specifiers.Contains(scan.Text(previous))))
        {
            return null;
        }

        var typeStart = previous;

        while (scan.Is(scan.Previous(typeStart), "::") && scan.IsName(scan.Previous(scan.Previous(typeStart))))
        {
            typeStart = scan.Previous(scan.Previous(typeStart));
        }

        var valued = (scan.Is(next, "=") && !scan.Is(scan.Next(next), "=")) || scan.Is(next, "{");
        var opening = Begins(scan, typeStart);
        var declares = opening switch
        {
            Opening.Statement => valued || scan.Is(next, ";") || scan.Is(next, "(") || scan.Is(next, ",") || scan.Is(next, "["),
            Opening.For => valued || scan.Is(next, ";") || scan.Is(next, "(") || scan.Is(next, ",") || scan.Is(next, "[") || scan.Is(next, ":"),
            Opening.Condition => valued || scan.Is(next, ";") || (scan.Is(next, "(") && EndsFirstStatement(scan, scan.Match(next))),
            Opening.Parameters => valued || scan.Is(next, ")") || scan.Is(next, ",") || scan.Is(next, "["),
            _ => false,
        };

        return declares ? (typeStart, typeEnd, previous, opening) : null;
    }

    /// <summary>
    /// Where the declarator's name stands of the declaration of a body whose type begins at <paramref name="at"/> - a
    /// variable's, a parameter's of a lambda or a catch - and where its type ends, as <see cref="Declaring"/> tells the name
    /// from the type before it; <see langword="null"/> where no declaration's type begins there. The type is no value the
    /// code reads: its names are references.
    /// </summary>
    private (int TypeEnd, int Name)? DeclaredType(ScannedText scan, int at)
    {
        if (_keywords.Contains(scan.Text(at)) || Builtins.Contains(scan.Text(at)) || scan.Is(scan.Previous(at), "::"))
        {
            return null;
        }

        var (chain, name) = Chain(scan, at, scan.Count, CppCodeTypes);

        while (scan.Is(name, "*") || scan.Is(name, "&"))
        {
            name = scan.Next(name);
        }

        return chain is not null && scan.IsName(name) && !_keywords.Contains(scan.Text(name)) && Declaring(scan, name) is { } declared && declared.TypeStart == at
            ? (declared.TypeEnd, name)
            : null;
    }

    /// <summary>Where a statement, or a declaration a statement's parentheses hold, begins.</summary>
    private enum Opening
    {
        /// <summary>Nowhere one begins: after an operator, a conditional's colon or a call's parenthesis.</summary>
        None,

        /// <summary>A statement's place: the text's first, or after <c>;</c>, a brace, a label's <c>:</c>, <c>else</c> or <c>do</c>.</summary>
        Statement,

        /// <summary>A <c>for</c>'s parenthesis: a statement's place, or a range-for's.</summary>
        For,

        /// <summary>An <c>if</c>'s, a <c>while</c>'s or a <c>switch</c>'s parenthesis, where a condition's comparisons are written too.</summary>
        Condition,

        /// <summary>A <c>catch</c>'s or a lambda's parenthesis: a parameter's place.</summary>
        Parameters,
    }

    /// <summary>Where what is written at <paramref name="at"/> begins, the specifiers before it passed over.</summary>
    private static Opening Begins(ScannedText scan, int at)
    {
        var before = scan.Is(scan.Previous(at), "::") ? scan.Previous(scan.Previous(at)) : scan.Previous(at);

        while (scan.IsName(before) && Specifiers.Contains(scan.Text(before)))
        {
            before = scan.Previous(before);
        }

        if (before < 0 || scan.Is(before, ";") || scan.Is(before, "{") || scan.Is(before, "}") || scan.Is(before, "else") || scan.Is(before, "do")
            || (scan.Is(before, ":") && !ScanCalls.IsConditionalColon(scan, before)))
        {
            return Opening.Statement;
        }

        if (!scan.Is(before, "("))
        {
            return Opening.None;
        }

        // An if constexpr is an if.
        var opener = scan.Is(scan.Previous(before), "constexpr") ? scan.Previous(scan.Previous(before)) : scan.Previous(before);
        return scan.Is(opener, "for") ? Opening.For
            : scan.Is(opener, "if") || scan.Is(opener, "while") || scan.Is(opener, "switch") ? Opening.Condition
            : scan.Is(opener, "catch") || scan.Is(opener, "]") ? Opening.Parameters
            : Opening.None;
    }

    /// <summary>
    /// What a call or a use on the receiver before <paramref name="dot"/> is qualified by: <c>this</c>, the type a value is
    /// declared with, or the value itself - a chain of two names or more written as links (<see cref="ExtractQualifier.Link"/>)
    /// to the reference made for its last, which was read before, so each name of a chain however long is read once.
    /// </summary>
    private ExtractQualifier Receiver(ScannedText scan, int dot, Dictionary<string, QualifiedName?> locals)
    {
        var last = scan.Previous(dot);

        // A member's chain is the one before it with its name after: this.a then a's own name, a value of no said type then
        // a value of none still, a type a local is declared with then a value of none.
        if (scan.IsName(last) && (scan.Is(scan.Previous(last), ".") || scan.Is(scan.Previous(last), "->")) && _members.TryGetValue((scan, last), out var member))
        {
            return _extract.ReferenceAt(member).Qualifier switch
            {
                { Kind: CodeQualifierKinds.This } => new ExtractQualifier(CodeQualifierKinds.Value, scan.Text(last)),
                { Link: not null } or { Kind: CodeQualifierKinds.Value, Name: not null } => new ExtractQualifier(CodeQualifierKinds.Value) { Link = member },
                _ => new ExtractQualifier(CodeQualifierKinds.Value),
            };
        }

        var names = new List<string>();
        var at = last;
        var first = -1;

        while (scan.IsName(at))
        {
            names.Add(scan.Text(at));
            first = at;
            var before = scan.Previous(at);

            if (!(scan.Is(before, ".") || scan.Is(before, "->")))
            {
                break;
            }

            at = scan.Previous(before);
        }

        // Read from its end, last first.
        names.Reverse();

        // A receiver that is no chain of names - a call's result, an element - is a value of no type the file says.
        if (names.Count == 0 || scan.Is(scan.Previous(first), ".") || scan.Is(scan.Previous(first), "->"))
        {
            return new ExtractQualifier(CodeQualifierKinds.Value);
        }

        if (names[0] == "this")
        {
            names.RemoveAt(0);
            return names.Count == 0 ? new ExtractQualifier(CodeQualifierKinds.This) : new ExtractQualifier(CodeQualifierKinds.Value, string.Join('.', names));
        }

        if (locals.TryGetValue(names[0], out var declared))
        {
            return names.Count == 1 && declared is not null ? new ExtractQualifier(CodeQualifierKinds.Type, declared) : new ExtractQualifier(CodeQualifierKinds.Value);
        }

        return new ExtractQualifier(CodeQualifierKinds.Value, string.Join('.', names));
    }

    /// <summary>The scopes written before the <c>::</c> at <paramref name="colons"/>, joined by dots; <see langword="null"/> where it stands alone, the global scope's.</summary>
    private string? Qualifier(ScannedText scan, int colons)
    {
        var names = new List<string>();
        var at = scan.Previous(colons);

        while (true)
        {
            if (scan.Is(at, ">") && ScanCalls.TypeArgumentsStart(scan, at, CppCodeTypes) is var less and >= 0)
            {
                at = scan.Previous(less);
            }

            if (!scan.IsName(at) || _keywords.Contains(scan.Text(at)))
            {
                break;
            }

            names.Add(scan.Text(at));

            if (!scan.Is(scan.Previous(at), "::"))
            {
                break;
            }

            at = scan.Previous(scan.Previous(at));
        }

        // Read from its end, last first.
        names.Reverse();
        return names.Count > 0 ? string.Join('.', names) : null;
    }

    // ---- Names, types and declarators ----

    /// <summary>One name a declaration declares.</summary>
    /// <param name="Name">The name; <see langword="null"/> where the declarator names none.</param>
    /// <param name="At">The index of its name's token.</param>
    /// <param name="TypeEnd">The index past the type written before it: the first declarator's type is the declaration's.</param>
    /// <param name="End">The index past its declarator.</param>
    /// <param name="Pointer">Whether it is declared in parentheses with a star, as a pointer to a function or an array is.</param>
    /// <param name="Function">Whether a parameter list follows it there: a pointer to a function.</param>
    private readonly record struct DeclaratorName(string? Name, int At, int TypeEnd, int End, bool Pointer, bool Function);

    /// <summary>
    /// Each name the declarators from <paramref name="at"/> to <paramref name="stop"/> - past a semicolon - declare, cut at
    /// their top-level commas: each after the first of the type the first is written after, and the first too where
    /// <paramref name="typed"/> - the declarators after a type's body, which is their type.
    /// </summary>
    private List<DeclaratorName> Declarators(int at, int stop, bool typed = false)
    {
        var names = new List<DeclaratorName>();
        var typeEnd = -1;
        var last = _scan.Is(stop - 1, ";") ? stop - 1 : stop;
        var first = true;

        foreach (var (start, end) in ScanCalls.Split(_scan, at, last, _types))
        {
            var cut = Math.Min(Math.Min(ScanCalls.TopLevel(_scan, start, end, "=", _types, SpellsOperator), ScanCalls.TopLevel(_scan, start, end, ":", _types, SpellsOperator)), ScanCalls.TopLevel(_scan, start + 1, end, "{", _types, SpellsOperator));
            var declared = Declarator(start, cut, typed || !first);
            first = false;

            if (declared.Name is null)
            {
                continue;
            }

            typeEnd = typeEnd < 0 ? declared.TypeEnd : typeEnd;
            names.Add(declared with { TypeEnd = typeEnd, End = end });
        }

        return names;
    }

    /// <summary>
    /// The name the declarator from <paramref name="start"/> to <paramref name="end"/> declares: in parentheses with a star
    /// (<c>(*f)(int)</c>), else the last name before its brackets or its arguments - where a type stands before it, or
    /// <paramref name="typed"/> says its type is written before the declarator.
    /// </summary>
    private DeclaratorName Declarator(int start, int end, bool typed = false)
    {
        var last = -1;

        for (var at = start; at < end; at = _scan.Next(at))
        {
            if (_scan.Is(at, "[") && _scan.Is(_scan.Next(at), "["))
            {
                at = _scan.Past(at) - 1;
                continue;
            }

            if (_cpp && _scan.Is(at, "<") && _scan.IsName(_scan.Previous(at)))
            {
                at = _scan.AngleEnd(at, end) - 1;
                continue;
            }

            if (_scan.Is(at, "(") && (_scan.Is(_scan.Next(at), "*") || _scan.Is(_scan.Next(at), "&") || _scan.Is(_scan.Next(at), "^")))
            {
                var close = _scan.Match(at) is var matched and >= 0 && matched < end ? matched : end;
                var named = _scan.Code(at, close).LastOrDefault(index => _scan.IsName(index) && !_keywords.Contains(_scan.Text(index)), -1);
                return named < 0 ? new DeclaratorName(null, at, at, end, true, false) : new DeclaratorName(_scan.Text(named), named, at, end, true, _scan.Is(_scan.Next(close), "("));
            }

            if (_scan.Is(at, "(") || _scan.Is(at, "[") || (_scan.Is(at, "{") && at > start))
            {
                break;
            }

            if (_scan.IsName(at))
            {
                last = at;
            }
        }

        if (last < 0)
        {
            return new DeclaratorName(null, start, start, end, false, false);
        }

        var text = _scan.Text(last);
        var before = _scan.Previous(last);

        // A name is the type, and no declarator's, where nothing that is a type, a pointer or a list of arguments stands before it.
        typed = typed || _scan.Code(start, last).Any(index => (_scan.IsName(index) && !Specifiers.Contains(_scan.Text(index))) || _scan.Is(index, "*") || _scan.Is(index, "&") || _scan.Is(index, ">"));

        if (!typed || Builtins.Contains(text) || Specifiers.Contains(text) || (_keywords.Contains(text) && text != "this") || _scan.Is(before, "::")
            || _scan.Is(before, "struct") || _scan.Is(before, "union") || _scan.Is(before, "enum") || _scan.Is(before, "class"))
        {
            return new DeclaratorName(null, start, start, end, false, false);
        }

        return new DeclaratorName(text, last, last, end, false, false);
    }

    /// <summary>The type written from <paramref name="start"/> to <paramref name="end"/>, the token at <paramref name="name"/> left out, on one line with a space only between two words: as an id names a parameter's type.</summary>
    private string TypeText(int start, int end, int name)
    {
        var text = new StringBuilder();
        var word = false;

        foreach (var at in _scan.Code(start, end))
        {
            // A tag before a type's name says nothing a parameter's type does not: class Canvas& is Canvas&.
            if (at == name || _scan.Is(at, "struct") || _scan.Is(at, "class") || _scan.Is(at, "union") || _scan.Is(at, "enum") || _scan.Is(at, "typename"))
            {
                continue;
            }

            var isWord = _scan[at].Kind is ScanTokenKind.Name or ScanTokenKind.Number;

            if (isWord && word)
            {
                text.Append(' ');
            }

            text.Append(ExtractText.Whole(_scan.Text(at)));
            word = isWord;
        }

        return text.ToString();
    }

    /// <summary>
    /// The type written from <paramref name="start"/> to <paramref name="end"/> as a value of it is qualified by: its name
    /// with its scopes, a smart pointer's the type it holds - a pointer's of a pointer's the innermost, read on in a loop,
    /// so no nesting of them reaches past the reader's stack; <see langword="null"/> where it is one C and C++ build in.
    /// </summary>
    private QualifiedName? TypeKey(ScannedText scan, int start, int end, ScanTypeArguments types)
    {
        for (var at = start; at < end && at < scan.Count; at = scan.Next(at))
        {
            if (!scan.IsName(at) || Specifiers.Contains(scan.Text(at)) || Modifying.Contains(scan.Text(at)))
            {
                continue;
            }

            if (Builtins.Contains(scan.Text(at)) || _typeParameters.Contains(scan.Text(at)))
            {
                return null;
            }

            var (chain, past) = Chain(scan, at, end, types);

            if (chain is null)
            {
                return null;
            }

            if (!(_cpp && Holders.Contains(chain[(chain.LastIndexOf('.') + 1)..]) && scan.Previous(past) is var held && scan.Is(held, ">") && ScanCalls.TypeArgumentsStart(scan, held, types) is var less and >= 0))
            {
                return QualifiedName.Of(chain);
            }

            // What the pointer holds is read next, as the type itself.
            end = held;
            at = less;
        }

        return null;
    }

    /// <summary>Whether the type from <paramref name="start"/> to <paramref name="end"/> is <c>std::function</c>, whose call is through what it holds.</summary>
    private bool IsFunctionObject(ScannedText scan, int start, int end)
    {
        if (!_cpp)
        {
            return false;
        }

        for (var at = start; at < end && at < scan.Count; at++)
        {
            if (scan.Is(at, "function") && scan.Is(scan.Previous(at), "::") && scan.Is(scan.Previous(scan.Previous(at)), "std"))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// A <c>references</c> edge from the node of <paramref name="key"/> to each type the tokens of <paramref name="scan"/>
    /// from <paramref name="start"/> to <paramref name="end"/> name that C and C++ do not build in - a signature's, and the
    /// types a body writes: a declaration's, a cast's, a <c>sizeof</c>'s - and each of a template's arguments read by
    /// what it is (<see cref="TemplateArgumentsReading"/>): <c>std::vector&lt;Widget*&gt;</c> names <c>Widget</c>,
    /// <c>std::array&lt;int, N&gt;</c> reads <c>N</c>. <paramref name="locals"/>, <paramref name="pointers"/> and
    /// <paramref name="taken"/> are what a body binds where the tokens are a body's, <paramref name="owner"/> the type whose
    /// members it sees. Read on the reader's stack of code readings (<see cref="Run"/>), so a type's arguments nested in
    /// one another at any depth reach past no stack.
    /// </summary>
    private void TypeReferences(ScannedText scan, QualifiedName key, int start, int end, Dictionary<string, QualifiedName?>? locals = null, HashSet<string>? pointers = null, string? owner = null, IReadOnlySet<string>? taken = null)
        => Run(new TypeReferencesReading(this, scan, key, start, end, locals, pointers, owner, taken));

    /// <summary>The types a run of tokens names (<see cref="TypeReferences"/>), read name by name, and the arguments of each template among them in turn.</summary>
    private sealed class TypeReferencesReading(CFamilyReading reader, ScannedText scan, QualifiedName key, int start, int end, Dictionary<string, QualifiedName?>? locals, HashSet<string>? pointers, string? owner, IReadOnlySet<string>? taken)
        : CodeReading
    {
        /// <summary>The index of the name read, or of the next token to read.</summary>
        private int _at = start;

        /// <summary>Where the run of names read last ends, its template's arguments read up to it; -1 outside one.</summary>
        private int _past = -1;

        /// <summary>The next token of that run a list of a template's arguments may open at.</summary>
        private int _less;

        public override CodeReading? Step()
        {
            while (true)
            {
                if (_past >= 0)
                {
                    for (; _less < _past; _less = scan.Next(_less))
                    {
                        if (scan.Is(_less, "<") && ScanCalls.TypeArgumentsEnd(scan, _less, CppTypes) is var listed and > 0 && listed <= _past)
                        {
                            var less = _less;
                            _less = scan.Next(listed - 1);

                            // Read with what the body binds where the tokens are a body's; with nothing bound of their own else.
                            return new TemplateArgumentsReading(reader, scan, less, listed, key, locals ?? new(StringComparer.Ordinal), pointers ?? [], owner, taken);
                        }
                    }

                    _at = scan.Next(Math.Max(_at, scan.Previous(_past)));
                    _past = -1;
                }

                if (_at >= end || _at >= scan.Count)
                {
                    return null;
                }

                var at = _at;

                if (scan.Is(at, "[") || scan.Is(at, "{"))
                {
                    _at = scan.Next(scan.Past(at) - 1);
                    continue;
                }

                if (!scan.IsName(at) || Specifiers.Contains(scan.Text(at)) || Builtins.Contains(scan.Text(at)) || Modifying.Contains(scan.Text(at))
                    || reader._keywords.Contains(scan.Text(at)) || reader._typeParameters.Contains(scan.Text(at)) || IsMacroName(scan.Text(at)) || scan.Is(scan.Previous(at), "::"))
                {
                    _at = scan.Next(at);
                    continue;
                }

                var (chain, past) = reader.Chain(scan, at, end, CppTypes);

                if (chain is not null && !chain.StartsWith("std.", StringComparison.Ordinal) && chain != "std")
                {
                    reader.Mention(key, CodeEdgeKinds.References, QualifiedName.Of(chain), scan[at].Line);
                }

                _less = scan.Next(at);
                _past = past;
            }
        }
    }

    /// <summary>What the tokens of a body from one index to another are as a type: none, a chain of names alone, or one only a type is.</summary>
    private enum TypeShape
    {
        /// <summary>No type: an expression, or nothing.</summary>
        None,

        /// <summary>A chain of names alone - <c>Node</c>, <c>ns::Node</c> - which a value is written as too.</summary>
        Bare,

        /// <summary>What only a type is written as: a type C and C++ build in, a specifier, a template's arguments, a pointer's or a reference's mark, an array's bounds, a function type's parameters.</summary>
        Certain,
    }

    /// <summary>
    /// What the tokens of <paramref name="scan"/> from <paramref name="start"/> to <paramref name="end"/> are as a type
    /// (<see cref="TypeShape"/>): specifiers and the types C builds in, a chain of names with a template's arguments, then
    /// pointers' and references' marks and their qualifiers, then an array's bounds or - where <paramref name="functions"/>,
    /// as a template's argument may be one - a function type's parameters, each a type of none of its own; and nothing
    /// more. <paramref name="head"/> is the chain's first name, -1 where none is written.
    /// </summary>
    private TypeShape TypeShapeOf(ScannedText scan, int start, int end, bool functions, out int head)
    {
        head = -1;
        var certain = false;
        var at = start;

        while (at < end && scan.IsName(at) && (Specifiers.Contains(scan.Text(at)) || Builtins.Contains(scan.Text(at))))
        {
            certain = true;
            at = scan.Next(at);
        }

        if (at < end && scan.IsName(at) && !_keywords.Contains(scan.Text(at)))
        {
            head = at;
            var (_, past) = Chain(scan, at, end, CppCodeTypes);
            certain |= scan.Code(at, past).Any(index => scan.Is(index, "<"));
            at = past;
        }

        while (at < end && (scan.Is(at, "*") || scan.Is(at, "&") || scan.Is(at, "&&") || (scan.IsName(at) && Specifiers.Contains(scan.Text(at)))))
        {
            certain = true;
            at = scan.Next(at);
        }

        // An array's bounds - (int[]) - or a function type's parameters: std::function<Result(Item)>.
        if (at < end && (head >= 0 || certain) && (scan.Is(at, "[") || (functions && scan.Is(at, "("))) && scan.Match(at) is var close and > 0 && close < end && scan.Next(close) >= end
            && (scan.Is(at, "[") || ScanCalls.Split(scan, at + 1, close, CppCodeTypes).All(piece => TypeShapeOf(scan, piece.Start, piece.End, functions: false, out _) != TypeShape.None)))
        {
            certain = true;
            at = scan.Next(close);
        }

        return at < end || (head < 0 && !certain) ? TypeShape.None : certain ? TypeShape.Certain : TypeShape.Bare;
    }

    /// <summary>
    /// Whether the tokens from <paramref name="start"/> to <paramref name="end"/> are a type the code writes - a function
    /// type among them where <paramref name="functions"/> (<see cref="TypeShapeOf"/>): one only a type is, or a chain of
    /// names whose first is no value the reader knows - a local, a parameter, a function pointer, a macro's parameter
    /// (<paramref name="taken"/>), or a variable, a field, a function, an enumerator or a macro the file declares. A name
    /// C does not say the kind of is a type's where a type or a value may stand: a value of another file is rarely
    /// measured or cast, a type of another file often.
    /// </summary>
    private bool IsType(ScannedText scan, int start, int end, bool functions, Dictionary<string, QualifiedName?> locals, HashSet<string> pointers, IReadOnlySet<string>? taken)
        => TypeShapeOf(scan, start, end, functions, out var head) switch
        {
            TypeShape.Certain => true,
            TypeShape.Bare => scan.Text(head) is var name && !locals.ContainsKey(name) && !pointers.Contains(name) && taken?.Contains(name) != true && !_values.Contains(name),
            _ => false,
        };

    /// <summary>
    /// The index of the closing parenthesis of the type the parenthesis at <paramref name="open"/> holds, where code writes
    /// one there - a <c>sizeof</c>'s or a <c>typeid</c>'s type, or a cast's: one where an operand may begin, a type in it
    /// and an operand after it, a chain of names alone cast only where a name, a literal, a group or a brace follows, as
    /// <c>(a) - b</c> subtracts; -1 where it holds none (<see cref="IsType"/>).
    /// </summary>
    private int TypeInParentheses(ScannedText scan, int open, Dictionary<string, QualifiedName?> locals, HashSet<string> pointers, IReadOnlySet<string>? taken)
    {
        if (scan.Match(open) is not (var close and > 0) || !IsType(scan, open + 1, close, functions: false, locals, pointers, taken))
        {
            return -1;
        }

        var before = scan.Previous(open);

        if (scan.IsName(before) && Measures.Contains(scan.Text(before)))
        {
            return close;
        }

        if (before >= 0 && (scan.IsName(before) ? !CastFollowed.Contains(scan.Text(before))
            : scan[before].Kind != ScanTokenKind.Punctuation || scan.Is(before, ")") || scan.Is(before, "]") || scan.Is(before, ">") || scan.Is(before, ".") || scan.Is(before, "->") || scan.Is(before, "::")))
        {
            return -1;
        }

        var after = scan.Next(close);
        var bare = TypeShapeOf(scan, open + 1, close, functions: false, out _) == TypeShape.Bare;
        var operand = after < scan.Count
            && ((scan.IsName(after) && (!StatementWords.Contains(scan.Text(after)) || Measures.Contains(scan.Text(after))))
                || scan[after].Kind is ScanTokenKind.Number or ScanTokenKind.Text
                || scan.Is(after, "(") || scan.Is(after, "{")
                || (!bare && (scan.Is(after, "*") || scan.Is(after, "&") || scan.Is(after, "-") || scan.Is(after, "+") || scan.Is(after, "!") || scan.Is(after, "~"))));

        return operand ? close : -1;
    }

    /// <summary>
    /// A list of a template's arguments, from the <c>&lt;</c> at <paramref name="less"/> to <paramref name="past"/>: each
    /// argument a type the code writes (<see cref="IsType"/>) its references, any other - <c>N / 2</c>,
    /// <c>&amp;Listener::receive</c> - a value, what it reads and calls read as code from the node of <paramref name="key"/>;
    /// each argument a reading of its own, read in turn on the reader's stack of them (<see cref="Run"/>).
    /// </summary>
    private sealed class TemplateArgumentsReading(CFamilyReading reader, ScannedText scan, int less, int past, QualifiedName key, Dictionary<string, QualifiedName?> locals, HashSet<string> pointers, string? owner, IReadOnlySet<string>? taken)
        : CodeReading
    {
        /// <summary>The arguments, split once the list is first read.</summary>
        private List<(int Start, int End)>? _arguments;

        /// <summary>The next argument to read.</summary>
        private int _next;

        public override CodeReading? Step()
        {
            _arguments ??= ScanCalls.Split(scan, less + 1, past - 1, CppTypes);

            if (_next >= _arguments.Count)
            {
                return null;
            }

            var (start, end) = _arguments[_next++];

            return reader.IsType(scan, start, end, functions: true, locals, pointers, taken)
                ? new TypeReferencesReading(reader, scan, key, start, end, locals, pointers, owner, taken)
                : new CallsReading(reader, scan, start, end, key, locals, pointers, owner, taken);
        }
    }

    /// <summary>A reference of <paramref name="kind"/> to the type <paramref name="chain"/> names: its last name, the scopes before it its qualifier.</summary>
    private void Mention(QualifiedName key, string kind, QualifiedName chain, int line)
        => _extract.Reference(key, kind, chain.PastLastDot(), line, qualifier: chain.BeforeLastDot() is { } before ? new ExtractQualifier(CodeQualifierKinds.Type, before) : null);

    /// <summary>The run of names joined by <c>::</c> at <paramref name="at"/>, a template's arguments passed over, joined by dots, and the index past it; no run where no name is there.</summary>
    private (string? Chain, int Past) Chain(ScannedText scan, int at, int end, ScanTypeArguments types)
    {
        var names = new List<string>();

        if (scan.Is(at, "::"))
        {
            at = scan.Next(at);
        }

        while (at < end && scan.IsName(at) && !_keywords.Contains(scan.Text(at)))
        {
            names.Add(scan.Text(at));
            var next = scan.Next(at);

            if (_cpp && scan.Is(next, "<") && ScanCalls.TypeArgumentsEnd(scan, next, types) is var past and >= 0)
            {
                next = past;
            }

            if (!scan.Is(next, "::") || !scan.IsName(scan.Next(next)))
            {
                return (string.Join('.', names), next);
            }

            at = scan.Next(next);
        }

        return (names.Count > 0 ? string.Join('.', names) : null, at);
    }

    // ---- What helps every reading ----

    /// <summary>Past the attributes at <paramref name="at"/>: <c>[[...]]</c>, <c>__attribute__((...))</c>, <c>__declspec(...)</c>, <c>alignas(...)</c>, and directives.</summary>
    private int Attributes(int at, int end)
    {
        while (at < end)
        {
            if (_scan.Is(at, "[") && _scan.Is(_scan.Next(at), "["))
            {
                at = _scan.Bounds(at, end).Past;
            }
            else if ((_scan.Is(at, "__attribute__") || _scan.Is(at, "__attribute") || _scan.Is(at, "__declspec") || _scan.Is(at, "alignas") || _scan.Is(at, "_Alignas")) && _scan.Is(_scan.Next(at), "("))
            {
                at = _scan.Bounds(_scan.Next(at), end).Past;
            }
            else if (_scan[at].Kind == ScanTokenKind.Directive)
            {
                at++;
            }
            else
            {
                break;
            }
        }

        return at;
    }

    /// <summary>The id of a declaration named <paramref name="name"/>: its language's prefix before its name - its file's path before it, where it is its file's alone.</summary>
    private QualifiedName Id(QualifiedName name, bool local) => local ? QualifiedName.Of($"{_prefix}{_path}:{name.Whole()}") : name.Prepend(_prefix);

    /// <summary>The scope a declaration is put in: the file's closed scope where it is its file's alone - declared at the first such - else <paramref name="scope"/>.</summary>
    private QualifiedName? Placed(QualifiedName? scope, bool local)
    {
        if (!local)
        {
            return scope;
        }

        if (!_closedDeclared)
        {
            _extract.Scope(_closed, open: false, parent: null);
            _closedDeclared = true;
        }

        return _closed;
    }


    /// <summary>The modifiers of the declaration at <paramref name="at"/>: <paramref name="modifiers"/>, and <c>conditional</c> where it lies under a conditional.</summary>
    private IReadOnlyList<string> Marks(int at, IReadOnlyList<string> modifiers)
        => at < _conditional.Length && _conditional[at] ? [.. modifiers, "conditional"] : modifiers;

    /// <summary>The line the token before <paramref name="past"/> ends on: a body's closing brace.</summary>
    private int LastLine(int past) => _scan[Math.Clamp(past - 1, 0, _scan.Count - 1)].EndLine;
}
