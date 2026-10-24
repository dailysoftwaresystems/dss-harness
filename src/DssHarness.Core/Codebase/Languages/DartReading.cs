using DssHarness.Core.Codebase.Languages.Scanning;

namespace DssHarness.Core.Codebase.Languages;

/// <summary>
/// One Dart file's reading over the structural scanner: its directives - <c>library</c>, <c>import</c>, <c>export</c>,
/// <c>part</c> and <c>part of</c> - as imports; what stands at its top and in its types - each class, mixin, extension,
/// extension type, enum, typedef, function, getter, setter, operator, field, constructor and enum value with its lines,
/// signature and summary; and each call a body makes by its name and arity, with what its receiver is where the file says.
/// </summary>
/// <remarks>
/// <para>
/// A library is an open scope named by its file's path, its parts' declarations put in it too - a part names its library
/// by a URI, which is found beside it, or by the library's name, which the library then declares a scope of beside its
/// own, each the other's parent - so the library's file and its parts see each other's names, private ones (a leading
/// <c>_</c>) included, and an import of the library's file makes them visible. A private name is kept to its library
/// (<see cref="ExtractNode.SeenIn"/>): no importer sees it. A part that names its library by a URI shares its imports
/// (<see cref="ExtractImport.Shares"/>); one that names it by its name does not. An augmentation (<c>augment</c>) is
/// passed over, its body with it. A declaration's id is its language and its qualified name - the library's
/// scope, the type's name, its own: an unnamed constructor <c>new</c>, a setter its name and <c>=</c>, an operator
/// <c>operator</c> and its symbol - since Dart has no overloads.
/// </para>
/// <para>
/// What it cannot bind is marked dynamic: a call through a function value - a parameter, a local or a field of a function
/// type, a value's <c>call</c>, <c>Function.apply</c> - and a call on a value declared <c>dynamic</c>. In a test file (under
/// <c>test/</c>, or ending <c>_test.dart</c>), each <c>test</c>, <c>testWidgets</c> and <c>group</c> call is a node of its
/// own, named by its description, the calls it makes its tests.
/// </para>
/// </remarks>
internal sealed class DartReading
{
    /// <summary>What the reader reads code nested in no deeper than <see cref="ScanFrame.MostDepth"/> of, as its dynamic mark says it.</summary>
    private const string Nesting = "interpolations and test groups";

    /// <summary>Dart's reserved words.</summary>
    private static readonly HashSet<string> Reserved = new(StringComparer.Ordinal)
    {
        "assert", "break", "case", "catch", "class", "const", "continue", "default", "do", "else", "enum", "extends", "false",
        "final", "finally", "for", "if", "in", "is", "new", "null", "rethrow", "return", "super", "switch", "this", "throw",
        "true", "try", "var", "void", "while", "with",
    };

    /// <summary>The names no call is made by: the reserved words, and those a parenthesis follows that call nothing.</summary>
    private static readonly HashSet<string> NoCalls = new(Reserved, StringComparer.Ordinal) { "await", "yield", "Function", "as", "when", "late", "required", "covariant" };

    /// <summary>The words before a name that say how it is declared or what an expression does, and no type of it.</summary>
    private static readonly HashSet<string> NoTypes = new(NoCalls, StringComparer.Ordinal)
    {
        "on", "show", "hide", "async", "sync", "static", "external", "abstract", "factory", "get", "set", "operator", "typedef",
        "deferred", "export", "import", "library", "part", "of", "extension", "mixin", "base", "interface", "sealed", "augment",
    };

    /// <summary>The types <c>dart:core</c> declares, which every library sees and the repository does not: no type a reference names.</summary>
    private static readonly HashSet<string> Builtins = new(StringComparer.Ordinal)
    {
        "int", "double", "num", "bool", "String", "dynamic", "void", "Object", "Never", "Null", "Function", "Record", "List", "Map",
        "Set", "Iterable", "Iterator", "Future", "FutureOr", "Stream", "Type", "Symbol", "DateTime", "Duration", "Uri", "BigInt",
        "Comparable", "Pattern", "RegExp", "Match", "RegExpMatch", "StringBuffer", "StringSink", "Error", "Exception", "StackTrace",
        "Enum", "Sink", "MapEntry", "Runes", "Invocation", "Expando", "WeakReference", "Finalizer", "Comparator", "Stopwatch",
    };

    /// <summary>
    /// The function types Flutter's foundation names, which a field or a parameter is most often declared with: a call of
    /// one is a call through a function value, as one of a type <c>Function</c> names is.
    /// </summary>
    private static readonly HashSet<string> CallbackTypes = new(StringComparer.Ordinal)
    {
        "VoidCallback", "ValueChanged", "ValueSetter", "ValueGetter", "AsyncCallback", "AsyncValueSetter", "AsyncValueGetter",
    };

    /// <summary>The words that may begin a class's or a mixin's declaration, before <c>class</c> or <c>mixin</c>.</summary>
    private static readonly HashSet<string> TypeModifiers = new(StringComparer.Ordinal) { "abstract", "base", "interface", "final", "sealed" };

    /// <summary>The words that may begin a member's declaration, before its type or its name.</summary>
    private static readonly HashSet<string> MemberModifiers = new(StringComparer.Ordinal)
    {
        "external", "static", "abstract", "late", "final", "const", "var", "covariant", "factory",
    };

    /// <summary>The member modifiers a node names among its modifiers.</summary>
    private static readonly HashSet<string> Modifying = new(StringComparer.Ordinal) { "external", "static", "abstract", "late", "final", "const", "covariant", "factory" };

    /// <summary>The words that may begin a parameter's declaration, before its type or its name.</summary>
    private static readonly HashSet<string> ParameterModifiers = new(StringComparer.Ordinal) { "required", "covariant", "final", "var", "const", "late" };

    /// <summary>The calls of a test framework that declare a test or a group of them.</summary>
    private static readonly HashSet<string> TestCalls = new(StringComparer.Ordinal) { "test", "testWidgets", "group" };

    /// <summary>The endings of the files Dart's generators write: <c>build_runner</c>'s, <c>freezed</c>'s, <c>mockito</c>'s, <c>protoc</c>'s.</summary>
    private static readonly string[] GeneratedEndings = [".g.dart", ".freezed.dart", ".mocks.dart", ".pb.dart", ".pbenum.dart", ".pbjson.dart", ".pbgrpc.dart", ".pbserver.dart"];

    /// <summary>
    /// How Dart writes a type's arguments: types, a nullable one's <c>?</c>, a prefix's dot - groups and nested lists
    /// passed over - followed, where a call or a constructor is written with them, by its parenthesis or a dot.
    /// </summary>
    private static readonly ScanTypeArguments DartTypes = new(
        new HashSet<string>(StringComparer.Ordinal) { ",", "?", "." },
        new HashSet<string>(StringComparer.Ordinal) { "(", "." },
        Introducers: new HashSet<string>(StringComparer.Ordinal) { "const" });

    /// <summary>How Dart writes its calls: a comma may end the arguments, with none after it.</summary>
    private static readonly ScanCallRules DartCalls = new(NoCalls, DartTypes, TrailingComma: true);

    /// <summary>How Dart writes a member's call, whose name after its receiver's dot no keyword is: <c>x.when(...)</c>, <c>Foo.new(...)</c>.</summary>
    private static readonly ScanCallRules DartMemberCalls = DartCalls with { Keywords = new HashSet<string>(StringComparer.Ordinal) };

    /// <summary>The punctuators a member's name follows its receiver after: a dot, a null-aware one, a cascade's.</summary>
    private static readonly HashSet<string> Accessors = new(StringComparer.Ordinal) { ".", "?.", "..", "?.." };

    private readonly InterpolatedText _text;
    private readonly ScannedText _scan;
    private readonly string _prefix;
    private readonly string _path;
    private readonly bool _testFile;
    private readonly ScanExtract _extract = new();

    /// <summary>The prefixes the file imports by (<c>as p</c>).</summary>
    private readonly HashSet<string> _prefixes = new(StringComparer.Ordinal);

    /// <summary>The names of the type parameters in force: no type the repository declares.</summary>
    private readonly HashSet<string> _typeParameters = new(StringComparer.Ordinal);

    /// <summary>The function types the file's typedefs name.</summary>
    private readonly HashSet<string> _functionTypes = new(StringComparer.Ordinal);

    /// <summary>The variables at the file's top of a function type: a call of one is through its value.</summary>
    private readonly HashSet<string> _functionValues = new(StringComparer.Ordinal);

    /// <summary>The fields of a function type each type the file declares holds, by the type's name.</summary>
    private readonly Dictionary<string, HashSet<string>> _functionFields = new(StringComparer.Ordinal);

    /// <summary>The bodies of the members a type declares, read once its every member is: a body sees the members after it.</summary>
    private List<Action>? _bodies;

    /// <summary>The type the extension being read extends, as its members are declared to; <see langword="null"/> outside one.</summary>
    private string? _extending;

    /// <summary>Whether the extension being read is private, so its members are its library's alone.</summary>
    private bool _privateExtension;

    /// <summary>The scope the file's declarations are put in: its library's, by the library's path or, for a part that names it so, its name.</summary>
    private string _library;

    /// <summary>The library's name, where it has one and parts that may name it by it; <see langword="null"/> otherwise.</summary>
    private string? _named;

    /// <summary>Whether the file is a part of another's library.</summary>
    private bool _part;

    /// <summary>Reads <paramref name="text"/>, the file <paramref name="path"/>.</summary>
    /// <param name="language">The implementation's name, which prefixes every id.</param>
    /// <param name="path">The file's path.</param>
    /// <param name="text">Its text.</param>
    public DartReading(string language, string path, string text)
    {
        _text = DartLexer.Scan(text);
        _scan = _text.Scan;
        _prefix = $"{language}:";
        _path = path;
        _library = path;
        _testFile = path.EndsWith("_test.dart", StringComparison.Ordinal) || path.StartsWith("test/", StringComparison.Ordinal) || path.Contains("/test/", StringComparison.Ordinal);
    }

    /// <summary>The file's extract.</summary>
    public FileExtract Read()
    {
        Directives();

        if (!_part)
        {
            _extract.Scope(_library, open: true, parent: _named);

            if (_named is not null)
            {
                _extract.Scope(_named, open: true, parent: _library);
            }
        }

        var top = new ScanFrame(_library, _library, null, null);
        ScanFrame.Walk(0, _scan.Count, at => Declaration(at, _scan.Count, top));
        _extract.Generated = Generated();
        return _extract.Build();
    }

    /// <summary>Why the file is generated: its name a generator's, or what its first lines say; <see langword="null"/> where it is not.</summary>
    private string? Generated()
        => GeneratedEndings.FirstOrDefault(ending => _path.EndsWith(ending, StringComparison.OrdinalIgnoreCase)) is { } ending
            ? $"its name ends {ending}, as a generator names what it writes"
            : ScanComments.Generated(_scan);

    // ---- Directives: the library, its imports, exports and parts ----

    /// <summary>
    /// Each directive at the file's top: a <c>library</c>'s name; an <c>import</c> or an <c>export</c> of a file, its
    /// configurations' URIs as imports that stand in for it; a <c>part</c> as an import of the part; a <c>part of</c> as
    /// the library the file's declarations are put in, an import of its file that shares its imports - and the names of
    /// the function types its typedefs declare.
    /// </summary>
    private void Directives()
    {
        string? name = null;
        var parts = false;

        for (var at = 0; at < _scan.Count; at = _scan.Opens(at) ? _scan.Past(at) : at + 1)
        {
            var next = _scan.Next(at);

            if (_scan.Is(at, "library") && (_scan.IsName(next) || _scan.Is(next, ";")))
            {
                name = Chain(_scan, next, _scan.Count).Chain;
            }
            else if ((_scan.Is(at, "import") || _scan.Is(at, "export")) && IsText(next))
            {
                Import(at, export: _scan.Is(at, "export"));
            }
            else if (_scan.Is(at, "part") && IsText(next))
            {
                if (Uri(next) is { } uri)
                {
                    _extract.Import(File(uri, _scan[at].Line));
                    parts = true;
                }
            }
            else if (_scan.Is(at, "part") && _scan.Is(next, "of"))
            {
                PartOf(at, _scan.Next(next));
            }
            else if (_scan.Is(at, "typedef") && TypedefHead(at, _scan.Count) is (>= 0 and var nameAt, var aliased, var stop)
                && (aliased < 0 || IsFunctionType(aliased + 1, _scan.LastOf(stop))))
            {
                // A typedef of a function type, wherever the file writes it: a value of it is a function value.
                _functionTypes.Add(_scan.Text(nameAt));
            }
        }

        _named = parts && !_part ? name : null;
    }

    /// <summary>An <c>import</c> or <c>export</c> at <paramref name="at"/>: its URI, its configurations' URIs, its prefix and its combinators.</summary>
    private void Import(int at, bool export)
    {
        var line = _scan[at].Line;
        var uriAt = _scan.Next(at);
        var conditions = new List<string>();
        var next = _scan.Next(uriAt);

        // import 'a.dart' if (dart.library.io) 'b.dart' - each configuration's URI stands in for the one before it.
        while (_scan.Is(next, "if") && _scan.Is(_scan.Next(next), "("))
        {
            var past = _scan.Past(_scan.Next(next));

            if (!IsText(past))
            {
                break;
            }

            if (Uri(past) is { } other)
            {
                conditions.Add(other);
            }

            next = _scan.Next(past);
        }

        string? prefix = null;
        List<string>? show = null;
        List<string>? hide = null;

        while (next < _scan.Count && !_scan.Is(next, ";"))
        {
            if (_scan.Is(next, "deferred"))
            {
                next = _scan.Next(next);
            }
            else if (_scan.Is(next, "as") && _scan.IsName(_scan.Next(next)))
            {
                prefix = _scan.Text(_scan.Next(next));
                next = _scan.Next(_scan.Next(next));
            }
            else if (_scan.Is(next, "show") || _scan.Is(next, "hide"))
            {
                var names = _scan.Is(next, "show") ? show ??= [] : hide ??= [];
                next = _scan.Next(next);

                while (_scan.IsName(next))
                {
                    names.Add(_scan.Text(next));
                    next = _scan.Next(next);

                    if (!_scan.Is(next, ","))
                    {
                        break;
                    }

                    next = _scan.Next(next);
                }
            }
            else
            {
                break;
            }
        }

        if (Uri(uriAt) is not { } uri)
        {
            return;
        }

        if (prefix is not null && !export)
        {
            _prefixes.Add(prefix);
        }

        foreach (var (target, fallback) in conditions.Select(condition => (condition, true)).Prepend((uri, false)))
        {
            _extract.Import(File(target, line) with
            {
                Prefix = export ? null : prefix,
                Show = show,
                Hide = hide,
                Export = export,
                Fallback = fallback,
            });
        }
    }

    /// <summary>
    /// A <c>part of</c> at <paramref name="at"/>, its library named at <paramref name="named"/>: by a URI, the library's
    /// file - whose path is the scope the file's declarations are put in, and whose imports it shares - or by the
    /// library's name, which is that scope.
    /// </summary>
    private void PartOf(int at, int named)
    {
        _part = true;

        if (IsText(named))
        {
            if (Uri(named) is { } uri)
            {
                _library = LibraryAt(uri);
                _extract.Import(File(uri, _scan[at].Line) with { Shares = true });
            }

            return;
        }

        if (Chain(_scan, named, _scan.Count).Chain is { } name)
        {
            _library = name;
        }
    }

    /// <summary>
    /// The path of the library a part names by <paramref name="uri"/>: beside the part, or - by a <c>package:</c> URI -
    /// under the <c>lib/</c> directory the part's own path is under, the package's; the URI itself where neither is.
    /// </summary>
    private string LibraryAt(string uri)
    {
        if (!HasScheme(uri))
        {
            return CodeGraphIds.Beside(_path, uri) ?? uri;
        }

        if (!uri.StartsWith("package:", StringComparison.Ordinal) || uri.IndexOf('/', StringComparison.Ordinal) is not (var slash and > 0))
        {
            return uri;
        }

        var lib = _path.StartsWith("lib/", StringComparison.Ordinal) ? 0 : _path.IndexOf("/lib/", StringComparison.Ordinal) is var inner and >= 0 ? inner + 1 : -1;

        return lib >= 0 && CodeGraphIds.Normalize($"{_path[..(lib + 4)]}{uri[(slash + 1)..]}") is { } path ? path : uri;
    }

    /// <summary>An import of the file <paramref name="uri"/> names: by its address where it has a scheme (<c>package:</c>, <c>dart:</c>), else by a path relative to this file.</summary>
    private static ExtractImport File(string uri, int line) => new(HasScheme(uri) ? CodeImportForms.Address : CodeImportForms.Relative, uri, line);

    /// <summary>Whether <paramref name="uri"/> begins with a scheme: letters, then a colon, before any slash.</summary>
    private static bool HasScheme(string uri) => uri.IndexOf(':', StringComparison.Ordinal) is var colon and > 1 && uri[..colon].All(char.IsAsciiLetter);

    /// <summary>The URI the string at <paramref name="at"/> holds; <see langword="null"/> where it is none - empty, interpolated, or left open.</summary>
    private string? Uri(int at)
    {
        var text = _scan.Text(at);
        var raw = text.StartsWith('r');
        var body = raw ? text[1..] : text;

        if (body.Length < 2)
        {
            return null;
        }

        var quote = body.Length >= 6 && body[1] == body[0] && body[2] == body[0] ? 3 : 1;

        if (body.Length < quote * 2 || !body.EndsWith(body[..quote], StringComparison.Ordinal))
        {
            return null;
        }

        var uri = body[quote..^quote];
        return uri.Length == 0 || (!raw && uri.Contains('$', StringComparison.Ordinal)) ? null : uri;
    }

    private bool IsText(int at) => at < _scan.Count && _scan[at].Kind == ScanTokenKind.Text;

    // ---- Declarations ----

    /// <summary>The declaration that begins at <paramref name="at"/>, read in <paramref name="frame"/>; answers where the next begins.</summary>
    private int Declaration(int at, int end, ScanFrame frame)
    {
        var start = at;
        var marks = new List<string>();
        at = Annotations(_scan, at, end, marks);

        if (at >= end)
        {
            return end;
        }

        if (_scan.Is(at, ";"))
        {
            return at + 1;
        }

        // A directive is read before the declarations; an augmentation is not read, its body with it.
        if (IsDirective(at) || _scan.Is(at, "augment"))
        {
            return _scan.StatementEnd(at, end);
        }

        var word = at;
        var modifiers = new List<string>();

        while (_scan.IsName(word) && TypeModifiers.Contains(_scan.Text(word)))
        {
            modifiers.Add(_scan.Text(word));
            word = _scan.Next(word);
        }

        if (_scan.Is(word, "class"))
        {
            return Type(start, at, _scan.Next(word), end, frame, "class", modifiers, marks);
        }

        if (_scan.Is(word, "mixin") && _scan.Is(_scan.Next(word), "class"))
        {
            return Type(start, at, _scan.Next(_scan.Next(word)), end, frame, "class", [.. modifiers, "mixin"], marks);
        }

        if (_scan.Is(word, "mixin") && _scan.IsName(_scan.Next(word)))
        {
            return Type(start, at, _scan.Next(word), end, frame, "mixin", modifiers, marks);
        }

        if (_scan.Is(at, "enum") && _scan.IsName(_scan.Next(at)))
        {
            return Type(start, at, _scan.Next(at), end, frame, "enum", [], marks);
        }

        if (_scan.Is(at, "extension"))
        {
            var next = _scan.Next(at);

            if (_scan.Is(next, "type") && _scan.IsName(_scan.Next(next)))
            {
                return ExtensionType(start, at, _scan.Next(next), end, frame, marks);
            }

            if (_scan.IsName(next) || _scan.Is(next, "<"))
            {
                return Extension(start, at, next, end, frame, marks);
            }
        }

        if (_scan.Is(at, "typedef"))
        {
            return Typedef(start, at, end, frame, marks);
        }

        return Member(start, at, end, frame, marks);
    }

    /// <summary>Whether a directive begins at <paramref name="at"/>, which <see cref="Directives"/> read.</summary>
    private bool IsDirective(int at)
    {
        var next = _scan.Next(at);

        return (_scan.Is(at, "library") && (_scan.IsName(next) || _scan.Is(next, ";")))
            || ((_scan.Is(at, "import") || _scan.Is(at, "export")) && IsText(next))
            || (_scan.Is(at, "part") && (IsText(next) || _scan.Is(next, "of")));
    }

    /// <summary>
    /// Past the annotations at <paramref name="at"/> - <c>@name</c>, <c>@p.name</c>, each with its type's arguments and its
    /// own - each named among <paramref name="marks"/> where it is given.
    /// </summary>
    private static int Annotations(ScannedText scan, int at, int end, List<string>? marks)
    {
        while (at < end && scan.Is(at, "@") && scan.IsName(scan.Next(at)))
        {
            var (written, past) = Chain(scan, scan.Next(at), end);

            if (scan.Is(past, "<"))
            {
                past = scan.AngleEnd(past, end);
            }

            if (scan.Is(past, "("))
            {
                past = scan.Past(past);
            }

            marks?.Add("@" + written);
            at = past;
        }

        return at;
    }

    // ---- Types ----

    /// <summary>
    /// A class, a mixin or an enum named at <paramref name="nameAt"/>: a type node over its head and body, its clauses'
    /// edges, and its members read in its own scope - a class alias (<c>class C = S with M;</c>) over its statement.
    /// </summary>
    private int Type(int start, int head, int nameAt, int end, ScanFrame frame, string subKind, List<string> modifiers, List<string> marks)
    {
        if (!_scan.IsName(nameAt) || NoTypes.Contains(_scan.Text(nameAt)))
        {
            return _scan.Next(head);
        }

        var name = _scan.Text(nameAt);
        var at = _scan.Next(nameAt);
        var typeParameters = _scan.Is(at, "<") ? TypeParameters(at, end) : [];

        if (_scan.Is(at, "<"))
        {
            at = _scan.AngleEnd(at, end);
        }

        if (subKind == "class" && _scan.Is(at, "="))
        {
            var stop = _scan.StatementEnd(at, end);
            var alias = DeclareType(start, head, _scan.LastOf(stop), frame, name, subKind, modifiers, marks, signatureEnd: _scan.Is(_scan.LastOf(stop), ";") ? _scan.LastOf(stop) : stop);
            Clauses(alias, _scan.Next(at), stop, CodeEdgeKinds.Inherits);
            return stop;
        }

        var open = BodyOpen(at, end);

        if (open < 0)
        {
            return _scan.StatementEnd(at, end);
        }

        var (inner, close) = _scan.Bounds(open, end);
        var key = DeclareType(start, head, Math.Min(inner, _scan.Count - 1), frame, name, subKind, modifiers, marks, signatureEnd: open);

        var added = typeParameters.Where(_typeParameters.Add).ToList();

        try
        {
            Clauses(key, at, open, kind: null);
            Members(open, inner, frame.Inside(frame.Qualify(name), frame.Qualify(name), key, name), subKind == "enum" ? key : null);
        }
        finally
        {
            _typeParameters.ExceptWith(added);
        }

        return close;
    }

    /// <summary>
    /// An extension named or not after <paramref name="head"/>, from <paramref name="at"/>: a type node - <c>extension</c>,
    /// where it has no name - whose members are declared to extend the type its <c>on</c> names; one that has no name, or a
    /// private one, its library's alone, its members with it.
    /// </summary>
    private int Extension(int start, int head, int at, int end, ScanFrame frame, List<string> marks)
    {
        string? name = null;

        if (_scan.IsName(at) && !_scan.Is(at, "on"))
        {
            name = _scan.Text(at);
            at = _scan.Next(at);
        }

        var typeParameters = _scan.Is(at, "<") ? TypeParameters(at, end) : [];

        if (_scan.Is(at, "<"))
        {
            at = _scan.AngleEnd(at, end);
        }

        if (!_scan.Is(at, "on"))
        {
            return _scan.Next(head);
        }

        var on = _scan.Next(at);
        var open = BodyOpen(on, end);

        if (open < 0)
        {
            return _scan.StatementEnd(at, end);
        }

        var (inner, close) = _scan.Bounds(open, end);
        var named = name ?? "extension";
        var hidden = name is null || IsPrivate(name);
        var key = DeclareType(start, head, Math.Min(inner, _scan.Count - 1), frame, named, "extension", [], marks, signatureEnd: open, seenIn: hidden ? _library : null);
        var added = typeParameters.Where(_typeParameters.Add).ToList();

        try
        {
            TypeReferences(key, on, open);
            _extending = ExtendedType(on, open);
            _privateExtension = hidden;
            Members(open, inner, frame.Inside(frame.Qualify(named), frame.Qualify(named), key, named), enumKey: null);
        }
        finally
        {
            _extending = null;
            _privateExtension = false;
            _typeParameters.ExceptWith(added);
        }

        return close;
    }

    /// <summary>
    /// An extension type named at <paramref name="at"/> (past <c>const</c>, where it is): a type node, its
    /// representation a field and its primary constructor a constructor of it, its <c>implements</c> its edges.
    /// </summary>
    private int ExtensionType(int start, int head, int at, int end, ScanFrame frame, List<string> marks)
    {
        var modifiers = new List<string>();

        if (_scan.Is(at, "const"))
        {
            modifiers.Add("const");
            at = _scan.Next(at);
        }

        if (!_scan.IsName(at))
        {
            return _scan.Next(head);
        }

        var name = _scan.Text(at);
        var nameLine = _scan[at].Line;
        var next = _scan.Next(at);
        var typeParameters = _scan.Is(next, "<") ? TypeParameters(next, end) : [];

        if (_scan.Is(next, "<"))
        {
            next = _scan.AngleEnd(next, end);
        }

        var constructor = "new";

        if (_scan.Is(next, ".") && _scan.IsName(_scan.Next(next)))
        {
            constructor = _scan.Text(_scan.Next(next));
            next = _scan.Next(_scan.Next(next));
        }

        if (!_scan.Is(next, "(") || _scan.Match(next) is not (var close and >= 0))
        {
            return _scan.StatementEnd(at, end);
        }

        var open = BodyOpen(_scan.Next(close), end);

        if (open < 0)
        {
            return _scan.StatementEnd(at, end);
        }

        var (inner, past) = _scan.Bounds(open, end);
        var key = DeclareType(start, head, Math.Min(inner, _scan.Count - 1), frame, name, "extensionType", modifiers, marks, signatureEnd: open);
        var inside = frame.Inside(frame.Qualify(name), frame.Qualify(name), key, name);
        var added = typeParameters.Where(_typeParameters.Add).ToList();

        try
        {
            Clauses(key, _scan.Next(close), open, kind: null);

            // The representation: a field of its type, and the constructor that takes it.
            var representation = ParametersOf(_scan, next, close);

            foreach (var (field, type) in representation.Locals)
            {
                var fieldKey = _extract.Add(new ExtractNode(Id(inside.Qualify(field)), CodeNodeKinds.Member, field, inside.Qualify(field), nameLine, _scan[close].EndLine, _scan.Signature(next + 1, close))
                {
                    SubKind = "field",
                    Scope = inside.Scope,
                    Parent = key,
                    Modifiers = ["final"],
                    ValueType = type is "dynamic" ? null : type,
                    SeenIn = IsPrivate(field) ? _library : null,
                });

                foreach (var (typeStart, typeEnd) in representation.TypeSpans)
                {
                    TypeReferences(fieldKey, typeStart, typeEnd);
                }
            }

            _extract.Add(new ExtractNode(Id(inside.Qualify(constructor)), CodeNodeKinds.Member, constructor, inside.Qualify(constructor), nameLine, _scan[close].EndLine, _scan.Signature(at, close + 1))
            {
                SubKind = "constructor",
                Scope = inside.Scope,
                Parent = key,
                Modifiers = modifiers,
                MinArity = representation.Least,
                MaxArity = representation.Most,
                SeenIn = IsPrivate(constructor) ? _library : null,
            });

            Members(open, inner, inside, enumKey: null);
        }
        finally
        {
            _typeParameters.ExceptWith(added);
        }

        return past;
    }

    /// <summary>
    /// A <c>typedef</c> at <paramref name="head"/>: a type node over its statement - <c>typedef F = T;</c>, or the older
    /// <c>typedef R F(P);</c> - referencing what it names; one of a function type a function type the file names.
    /// </summary>
    private int Typedef(int start, int head, int end, ScanFrame frame, List<string> marks)
    {
        var (nameAt, aliased, stop) = TypedefHead(head, end);

        if (nameAt < 0)
        {
            return stop;
        }

        var last = _scan.LastOf(stop);
        var aliasEnd = _scan.Is(last, ";") ? last : stop;
        var name = _scan.Text(nameAt);
        var typeParameters = _scan.Is(_scan.Next(nameAt), "<") ? TypeParameters(_scan.Next(nameAt), stop) : [];
        var key = DeclareType(start, head, last, frame, name, "typedef", [], marks, signatureEnd: aliasEnd);
        var added = typeParameters.Where(_typeParameters.Add).ToList();

        try
        {
            if (aliased >= 0)
            {
                TypeReferences(key, aliased + 1, aliasEnd);
            }
            else
            {
                TypeReferences(key, _scan.Next(head), nameAt);

                var parameters = ScanCalls.TopLevel(_scan, nameAt, stop, "(", DartTypes);

                if (parameters < stop && _scan.Match(parameters) is var close and > 0)
                {
                    foreach (var (typeStart, typeEnd) in ParametersOf(_scan, parameters, close).TypeSpans)
                    {
                        TypeReferences(key, typeStart, typeEnd);
                    }
                }
            }
        }
        finally
        {
            _typeParameters.ExceptWith(added);
        }

        return stop;
    }

    /// <summary>
    /// What a <c>typedef</c> at <paramref name="head"/> names: the index of its name - -1 where it names none - the index
    /// of the <c>=</c> of the newer form (<c>typedef F = T;</c>), -1 in the older (<c>typedef R F(P);</c>), and the index
    /// past its statement.
    /// </summary>
    private (int NameAt, int Aliased, int Stop) TypedefHead(int head, int end)
    {
        var stop = _scan.StatementEnd(head, end);
        var at = _scan.Next(head);

        if (_scan.IsName(at) && (_scan.Is(_scan.Next(at), "=") || (_scan.Is(_scan.Next(at), "<") && _scan.Is(_scan.AngleEnd(_scan.Next(at), stop), "="))))
        {
            return (at, _scan.Is(_scan.Next(at), "=") ? _scan.Next(at) : _scan.AngleEnd(_scan.Next(at), stop), stop);
        }

        // typedef R F<T>(P): the name before its parameters.
        var nameAt = _scan.Previous(ScanCalls.TopLevel(_scan, at, stop, "(", DartTypes));

        if (_scan.Is(nameAt, ">") && ScanCalls.TypeArgumentsStart(_scan, nameAt, DartTypes) is var less and >= 0)
        {
            nameAt = _scan.Previous(less);
        }

        return (_scan.IsName(nameAt) && nameAt >= at ? nameAt : -1, -1, stop);
    }

    /// <summary>A type node over the tokens from <paramref name="start"/> to <paramref name="last"/>, its signature the tokens from <paramref name="head"/> before <paramref name="signatureEnd"/>; answers its key.</summary>
    private string DeclareType(int start, int head, int last, ScanFrame frame, string name, string subKind, List<string> modifiers, List<string> marks, int signatureEnd, string? seenIn = null)
        => _extract.Add(new ExtractNode(Id(frame.Qualify(name)), CodeNodeKinds.Type, name, frame.Qualify(name), _scan[start].Line, _scan[Math.Clamp(last, start, _scan.Count - 1)].EndLine, _scan.Signature(head, signatureEnd))
        {
            SubKind = subKind,
            Scope = frame.Scope,
            Parent = frame.Parent,
            BodyStart = _scan.Is(signatureEnd, "{") ? _scan[signatureEnd].Line : null,
            Summary = ScanComments.Summary(_scan, start),
            Modifiers = [.. modifiers, .. marks],
            SeenIn = seenIn ?? (IsPrivate(name) ? _library : null),
        });

    /// <summary>
    /// A type's clauses from <paramref name="at"/> to <paramref name="end"/>: <c>extends</c> an <c>inherits</c> edge,
    /// <c>with</c> a <c>mixesIn</c> edge each, <c>implements</c> an <c>implements</c> edge each and a mixin's <c>on</c> an
    /// <c>inherits</c> edge each - its body sees their members, and its <c>super</c> reaches them - each type's arguments
    /// referenced. <paramref name="kind"/> is what a type before any clause is: a class alias's base.
    /// </summary>
    private void Clauses(string key, int at, int end, string? kind)
    {
        while (at < end)
        {
            if (_scan.Is(at, "extends") || _scan.Is(at, "on"))
            {
                kind = CodeEdgeKinds.Inherits;
            }
            else if (_scan.Is(at, "with"))
            {
                kind = CodeEdgeKinds.MixesIn;
            }
            else if (_scan.Is(at, "implements"))
            {
                kind = CodeEdgeKinds.Implements;
            }
            else if (kind is not null && _scan.IsName(at))
            {
                var (chain, past) = Chain(_scan, at, end);

                // Object, num, double and the rest of dart:core: no type the repository declares.
                if (!Builtins.Contains(chain!))
                {
                    Mention(key, kind, chain!, _scan[at].Line);
                }

                if (_scan.Is(past, "<"))
                {
                    var arguments = _scan.AngleEnd(past, end);
                    TypeReferences(key, past + 1, arguments - 1);
                    past = arguments;
                }

                at = past;
                continue;
            }

            at = _scan.Next(at);
        }
    }

    /// <summary>
    /// The members of the body <paramref name="open"/> opens, read in <paramref name="frame"/>, the type's own - an enum's
    /// values first - their bodies read once every member is declared.
    /// </summary>
    private void Members(int open, int inner, ScanFrame frame, string? enumKey)
    {
        if (frame.TooDeep)
        {
            _extract.Dynamic(_scan[open].Line, ScanFrame.TooDeepWhy);
            return;
        }

        var outer = _bodies;
        _bodies = [];

        try
        {
            var at = enumKey is not null ? Values(open + 1, inner, frame, enumKey) : open + 1;
            ScanFrame.Walk(at, inner, next => Declaration(next, inner, frame));

            foreach (var body in _bodies)
            {
                body();
            }
        }
        finally
        {
            _bodies = outer;
        }
    }

    /// <summary>
    /// An enum's values from <paramref name="at"/>, to the <c>;</c> that ends them or the body's end: each a constant of
    /// the enum's type, a call of the constructor it names with what its arguments call; answers where its members begin.
    /// </summary>
    private int Values(int at, int inner, ScanFrame frame, string enumKey)
    {
        var stop = ScanCalls.TopLevel(_scan, at, inner, ";", DartTypes);

        foreach (var (pieceStart, pieceEnd) in ScanCalls.Split(_scan, at, stop, DartTypes))
        {
            var marks = new List<string>();
            var name = Annotations(_scan, pieceStart, pieceEnd, marks);

            if (!_scan.IsName(name))
            {
                continue;
            }

            var key = _extract.Add(new ExtractNode(Id(frame.Qualify(_scan.Text(name))), CodeNodeKinds.Member, _scan.Text(name), frame.Qualify(_scan.Text(name)), _scan[pieceStart].Line, _scan[_scan.LastOf(pieceEnd)].EndLine, ExtractText.Signature(_scan.Text(name)))
            {
                SubKind = "constant",
                Scope = frame.Scope,
                Parent = enumKey,
                Summary = ScanComments.Summary(_scan, pieceStart),
                Modifiers = marks,
                ValueType = frame.Owner,
                SeenIn = IsPrivate(_scan.Text(name)) ? _library : null,
            });

            var next = _scan.Next(name);

            if (_scan.Is(next, "<"))
            {
                next = _scan.AngleEnd(next, pieceEnd);
            }

            var constructor = "new";

            if (_scan.Is(next, ".") && _scan.IsName(_scan.Next(next)))
            {
                constructor = _scan.Text(_scan.Next(next));
                next = _scan.Next(_scan.Next(next));
            }

            if (_scan.Is(next, "(") && next < pieceEnd)
            {
                var close = Math.Min(_scan.Bounds(next, pieceEnd).Inner, pieceEnd);
                _extract.Reference(key, CodeEdgeKinds.Calls, constructor, _scan[next].Line, ScanCalls.CallArity(_scan, next, close, DartCalls), new ExtractQualifier(CodeQualifierKinds.This));
                Calls(_text, next + 1, close, key, new Locals(), frame.Owner, depth: 0);
            }
        }

        return stop < inner ? stop + 1 : inner;
    }

    // ---- Members ----

    /// <summary>
    /// A member at <paramref name="at"/>, past its annotations: a constructor, a getter, a setter, an operator, a function
    /// or a method, or variables and fields.
    /// </summary>
    private int Member(int start, int at, int end, ScanFrame frame, List<string> marks)
    {
        var head = at;
        var modifiers = new List<string>();

        while (_scan.IsName(at) && MemberModifiers.Contains(_scan.Text(at)) && !IsDeclaredName(at))
        {
            modifiers.Add(_scan.Text(at));
            at = _scan.Next(at);
        }

        if (frame.Owner is { } owner && _scan.Is(at, owner) && _extending is null)
        {
            var next = _scan.Next(at);

            if (_scan.Is(next, "(") || (_scan.Is(next, ".") && _scan.IsName(_scan.Next(next)) && _scan.Is(_scan.Next(_scan.Next(next)), "(")))
            {
                return Constructor(start, head, at, end, frame, marks, modifiers);
            }
        }

        for (var index = at; index < end;)
        {
            var next = _scan.Next(index);

            if (_scan.Is(index, "get") && _scan.IsName(next) && !IsDeclaredName(index))
            {
                return Callable(start, head, at, next, end, frame, marks, modifiers, "getter");
            }

            if (_scan.Is(index, "set") && _scan.IsName(next) && _scan.Is(_scan.Next(next), "("))
            {
                return Callable(start, head, at, next, end, frame, marks, modifiers, "setter");
            }

            if (_scan.Is(index, "operator") && IsOperatorSymbol(next, end))
            {
                return Callable(start, head, at, index, end, frame, marks, modifiers, "operator");
            }

            if (_scan.Is(index, "Function") && _scan.Is(next, "("))
            {
                index = _scan.Past(next);
                continue;
            }

            // Any name but a reserved word's may be a function's or a method's: of, show, get, on among them.
            if (_scan.IsName(index) && !Reserved.Contains(_scan.Text(index)) && !_scan.Is(index, "Function")
                && (_scan.Is(next, "(") || (_scan.Is(next, "<") && _scan.Is(_scan.AngleEnd(next, end), "("))))
            {
                return Callable(start, head, at, index, end, frame, marks, modifiers, frame.Owner is null ? "function" : "method");
            }

            if (_scan.Is(index, "<"))
            {
                index = _scan.AngleEnd(index, end);
                continue;
            }

            if (_scan.Is(index, "("))
            {
                // A record type.
                index = _scan.Past(index);
                continue;
            }

            if (_scan.Is(index, "=") || _scan.Is(index, ";") || _scan.Is(index, ","))
            {
                return Variables(start, head, at, end, frame, marks, modifiers);
            }

            if (_scan.Is(index, "{") || _scan.Is(index, "}") || _scan.Is(index, "=>") || !(_scan.IsName(index) || _scan.Is(index, "?") || _scan.Is(index, ".")))
            {
                // No declaration: what stands here is passed over to the statement's end.
                return Math.Max(_scan.StatementEnd(head, end), index);
            }

            index = next;
        }

        return end;
    }

    /// <summary>Whether an operator's symbol begins at <paramref name="at"/>: one to three punctuators - a bracket's pair among them - and its parameters after.</summary>
    private bool IsOperatorSymbol(int at, int end)
    {
        var parameters = ScanCalls.TopLevel(_scan, at, end, "(", null);
        var symbol = _scan.Code(at, parameters).ToList();

        return parameters < end && symbol.Count is > 0 and <= 3 && symbol.All(index => _scan[index].Kind == ScanTokenKind.Punctuation);
    }

    /// <summary>Whether the word at <paramref name="at"/> is the name a declaration declares, not one of its modifiers: what follows it ends a name.</summary>
    private bool IsDeclaredName(int at)
        => _scan.Next(at) is var next && (_scan.Is(next, "(") || _scan.Is(next, "=") || _scan.Is(next, ";") || _scan.Is(next, ",") || _scan.Is(next, "=>") || _scan.Is(next, "{") || _scan.Is(next, "<"));

    /// <summary>
    /// A function, a method, a getter, a setter or an operator, its name at <paramref name="nameAt"/> - an operator's
    /// <c>operator</c> - its type from <paramref name="typeStart"/>: a member node over its head and body, what its
    /// signature names, the member it overrides, and what its body calls.
    /// </summary>
    private int Callable(int start, int head, int typeStart, int nameAt, int end, ScanFrame frame, List<string> marks, List<string> modifiers, string subKind)
    {
        var name = _scan.Text(nameAt);
        var at = _scan.Next(nameAt);

        // Where its type ends: before get, set or operator, else before its name.
        var typeEnd = subKind is "getter" or "setter" ? _scan.Previous(nameAt) : nameAt;

        if (subKind == "operator")
        {
            var symbol = ScanCalls.TopLevel(_scan, at, end, "(", null);
            name = "operator" + string.Concat(_scan.Code(at, symbol).Select(index => _scan.Text(index)));
            at = symbol;
        }
        else if (subKind == "setter")
        {
            name += "=";
        }

        var typeParameters = _scan.Is(at, "<") ? TypeParameters(at, end) : [];

        if (_scan.Is(at, "<"))
        {
            at = _scan.AngleEnd(at, end);
        }

        Parameters parameters;
        int signatureEnd;

        if (subKind == "getter")
        {
            parameters = new Parameters(null, null, [], new Dictionary<string, string?>(), new HashSet<string>());
            signatureEnd = at;
        }
        else if (_scan.Is(at, "(") && _scan.Match(at) is var close and > 0)
        {
            parameters = ParametersOf(_scan, at, close);
            signatureEnd = close + 1;
        }
        else
        {
            return _scan.StatementEnd(head, end);
        }

        var (body, last) = Body(signatureEnd, end);

        if (last < 0)
        {
            return Math.Max(_scan.StatementEnd(head, end), signatureEnd);
        }

        var qualified = frame.Qualify(name);
        var instance = !modifiers.Contains("static", StringComparer.Ordinal);
        var prototype = body < 0;
        var named = modifiers.Where(Modifying.Contains).Distinct(StringComparer.Ordinal).ToList();

        if (prototype && frame.Owner is not null && !named.Contains("external", StringComparer.Ordinal) && !named.Contains("abstract", StringComparer.Ordinal))
        {
            named.Add("abstract");
        }

        if (body >= 0 && _scan.Next(signatureEnd - 1) is var after && (_scan.Is(after, "async") || _scan.Is(after, "sync")))
        {
            named.Add(_scan.Is(_scan.Next(after), "*") ? $"{_scan.Text(after)}*" : _scan.Text(after));
        }

        var key = _extract.Add(new ExtractNode(Id(qualified), CodeNodeKinds.Member, name, qualified, _scan[start].Line, _scan[last].EndLine, _scan.Signature(head, body >= 0 ? body : last))
        {
            SubKind = subKind,
            Scope = frame.Scope,
            Parent = frame.Parent,
            BodyStart = body >= 0 ? _scan[body].Line : null,
            Summary = ScanComments.Summary(_scan, start),
            Modifiers = [.. named, .. marks],
            MinArity = parameters.Least,
            MaxArity = parameters.Most,
            Prototype = prototype,
            ValueType = subKind == "getter" && TypeKey(_scan, typeStart, typeEnd) is { } type and not "dynamic" ? type : null,
            Extends = instance ? _extending : null,
            SeenIn = IsPrivate(name) || (_extending is not null && _privateExtension) ? _library : null,
        });

        var added = typeParameters.Where(_typeParameters.Add).ToList();

        try
        {
            // What its signature names: its return type, and its parameters' types.
            TypeReferences(key, typeStart, typeEnd);

            foreach (var (spanStart, spanEnd) in parameters.TypeSpans)
            {
                TypeReferences(key, spanStart, spanEnd);
            }
        }
        finally
        {
            _typeParameters.ExceptWith(added);
        }

        Overriding(key, name, nameAt, marks);

        if (body >= 0)
        {
            ReadBody(body, last, key, parameters, frame, typeParameters);
        }

        return last + 1;
    }

    /// <summary>
    /// A constructor named at <paramref name="at"/> - its type's name, and <c>.name</c> where it has one: a member node
    /// named <c>new</c> or that name, what its initializers and body call, and the constructor a redirecting one names.
    /// </summary>
    private int Constructor(int start, int head, int at, int end, ScanFrame frame, List<string> marks, List<string> modifiers)
    {
        var name = "new";
        var next = _scan.Next(at);

        if (_scan.Is(next, "."))
        {
            name = _scan.Text(_scan.Next(next));
            next = _scan.Next(_scan.Next(next));
        }

        if (!_scan.Is(next, "(") || _scan.Match(next) is not (var close and > 0))
        {
            return _scan.StatementEnd(head, end);
        }

        var parameters = ParametersOf(_scan, next, close);
        var after = _scan.Next(close);
        var initializers = -1;
        var redirect = -1;

        if (_scan.Is(after, ":"))
        {
            initializers = after;
            after = ScanCalls.TopLevel(_scan, after, end, "{", DartTypes, index => !_scan.Is(index, "{") || IsCollection(index));
            after = Math.Min(after, ScanCalls.TopLevel(_scan, initializers, end, ";", DartTypes));
            after = Math.Min(after, ScanCalls.TopLevel(_scan, initializers, end, "=>", DartTypes));
        }
        else if (_scan.Is(after, "="))
        {
            redirect = after;
        }

        int body;
        int last;

        if (redirect >= 0)
        {
            body = -1;
            last = _scan.LastOf(_scan.StatementEnd(redirect, end));
        }
        else if (_scan.Is(after, "{"))
        {
            body = after;
            last = Math.Min(_scan.Bounds(after, end).Inner, _scan.Count - 1);
        }
        else if (_scan.Is(after, "=>"))
        {
            body = after;
            last = _scan.LastOf(ArrowEnd(_scan, after, end));
        }
        else
        {
            body = -1;
            last = _scan.Is(after, ";") ? after : _scan.LastOf(_scan.StatementEnd(close, end));
        }

        var qualified = frame.Qualify(name);
        var named = modifiers.Where(Modifying.Contains).Distinct(StringComparer.Ordinal).ToList();

        if (redirect >= 0 || (initializers >= 0 && _scan.Is(_scan.Next(initializers), "this")))
        {
            named.Add("redirecting");
        }

        var key = _extract.Add(new ExtractNode(Id(qualified), CodeNodeKinds.Member, name, qualified, _scan[start].Line, _scan[last].EndLine, _scan.Signature(head, initializers >= 0 ? initializers : body >= 0 ? body : redirect >= 0 ? redirect : last))
        {
            SubKind = "constructor",
            Scope = frame.Scope,
            Parent = frame.Parent,
            BodyStart = body >= 0 ? _scan[body].Line : null,
            Summary = ScanComments.Summary(_scan, start),
            Modifiers = [.. named, .. marks],
            MinArity = parameters.Least,
            MaxArity = parameters.Most,
            Prototype = named.Contains("external", StringComparer.Ordinal),
            SeenIn = IsPrivate(name) ? _library : null,
        });

        foreach (var (spanStart, spanEnd) in parameters.TypeSpans)
        {
            TypeReferences(key, spanStart, spanEnd);
        }

        if (redirect >= 0)
        {
            Redirect(key, _scan.Next(redirect), last);
            return last + 1;
        }

        var initialized = initializers;
        var typeParameters = _typeParameters.ToList();

        void Read()
        {
            var locals = new Locals(parameters);
            var added = typeParameters.Where(_typeParameters.Add).ToList();

            try
            {
                if (initialized >= 0)
                {
                    Calls(_text, initialized + 1, body >= 0 ? body : last + 1, key, locals, frame.Owner, depth: 0);
                }

                if (body >= 0)
                {
                    Calls(_text, body + 1, last, key, locals, frame.Owner, depth: 0);
                }
            }
            finally
            {
                _typeParameters.ExceptWith(added);
            }
        }

        Defer(Read);
        return last + 1;
    }

    /// <summary>The constructor a redirecting factory names from <paramref name="at"/>: <c>= Other;</c> instantiates it, <c>= Other.named;</c> calls that one.</summary>
    private void Redirect(string key, int at, int last)
    {
        var (names, _) = ChainAt(_scan, at, last + 1);

        if (names.Count == 0)
        {
            return;
        }

        var written = names.Select(index => _scan.Text(index)).ToList();
        var line = _scan[names[0]].Line;

        if (written.Count > 1 && !IsTypeName(written[^1]))
        {
            _extract.Reference(key, CodeEdgeKinds.Calls, written[^1], line, qualifier: Qualifier(string.Join('.', written[..^1])));
        }
        else
        {
            Mention(key, CodeEdgeKinds.Instantiates, string.Join('.', written), line);
        }
    }

    /// <summary>
    /// The variables or fields a declaration from <paramref name="typeStart"/> declares: each a member node of the type
    /// written before the first - or, where none is, the type its initializer makes - what its initializer calls.
    /// </summary>
    private int Variables(int start, int head, int typeStart, int end, ScanFrame frame, List<string> marks, List<string> modifiers)
    {
        var stop = _scan.StatementEnd(head, end);
        var last = _scan.LastOf(stop);
        var pieces = ScanCalls.Split(_scan, typeStart, _scan.Is(last, ";") ? last : stop, DartTypes);

        if (pieces.Count == 0)
        {
            return stop;
        }

        var (firstStart, firstEnd) = pieces[0];
        var firstAssign = ScanCalls.TopLevel(_scan, firstStart, firstEnd, "=", DartTypes);
        var firstName = _scan.Previous(firstAssign);

        if (!_scan.IsName(firstName) || firstName < typeStart)
        {
            return stop;
        }

        var typeEnd = firstName;
        var declaredType = TypeKey(_scan, typeStart, typeEnd);
        var function = IsFunctionType(typeStart, typeEnd);
        var named = modifiers.Where(Modifying.Contains).Distinct(StringComparer.Ordinal).ToList();
        var constant = modifiers.Contains("const", StringComparer.Ordinal);
        var subKind = constant ? "constant" : frame.Owner is not null ? "field" : "variable";

        foreach (var (pieceStart, pieceEnd) in pieces)
        {
            var assign = ScanCalls.TopLevel(_scan, pieceStart, pieceEnd, "=", DartTypes);
            var nameAt = pieceStart == firstStart ? firstName : _scan.Previous(assign);

            if (!_scan.IsName(nameAt) || nameAt < pieceStart)
            {
                continue;
            }

            var name = _scan.Text(nameAt);
            var qualified = frame.Qualify(name);
            var valueType = declaredType is "dynamic" ? null : declaredType ?? (assign < pieceEnd ? Made(_scan, _scan.Next(assign), pieceEnd) : null);

            var key = _extract.Add(new ExtractNode(Id(qualified), CodeNodeKinds.Member, name, qualified, _scan[start].Line, _scan[last].EndLine, ExtractText.Signature(ExtractText.Flat($"{_scan.Spelled(head, typeEnd)} {name}")))
            {
                SubKind = subKind,
                Scope = frame.Scope,
                Parent = frame.Parent,
                Summary = ScanComments.Summary(_scan, start),
                Modifiers = [.. named, .. marks],
                ValueType = valueType,
                Extends = frame.Owner is not null && _extending is not null && !modifiers.Contains("static", StringComparer.Ordinal) ? _extending : null,
                SeenIn = IsPrivate(name) || (_extending is not null && _privateExtension) ? _library : null,
            });

            if (function)
            {
                if (frame.Owner is { } owner)
                {
                    (_functionFields.TryGetValue(owner, out var fields) ? fields : _functionFields[owner] = new(StringComparer.Ordinal)).Add(name);
                }
                else
                {
                    _functionValues.Add(name);
                }
            }

            TypeReferences(key, typeStart, typeEnd);
            Overriding(key, name, nameAt, marks);

            if (assign < pieceEnd)
            {
                Calls(_text, assign + 1, pieceEnd, key, new Locals(), frame.Owner, depth: 0);
            }
        }

        return stop;
    }

    /// <summary>
    /// An <c>overrides</c> edge from the member of <paramref name="key"/>, named at <paramref name="nameAt"/>, to the one
    /// of its name its bases declare, where it is marked <c>@override</c>: a method's, an operator's, a getter's and a
    /// setter's, and a field's, which overrides a getter of its name as well.
    /// </summary>
    private void Overriding(string key, string name, int nameAt, List<string> marks)
    {
        if (marks.Contains("@override", StringComparer.Ordinal))
        {
            _extract.Reference(key, CodeEdgeKinds.Overrides, name, _scan[nameAt].Line, qualifier: new ExtractQualifier(CodeQualifierKinds.Base));
        }
    }

    /// <summary>
    /// The body of a callable at <paramref name="body"/> - a block or an arrow - to <paramref name="last"/>: what it calls,
    /// its parameters its locals; read once its type's every member is declared.
    /// </summary>
    private void ReadBody(int body, int last, string key, Parameters parameters, ScanFrame frame, IReadOnlyList<string> own)
    {
        var typeParameters = _typeParameters.Concat(own).ToList();

        void Read()
        {
            var added = typeParameters.Where(_typeParameters.Add).ToList();

            try
            {
                Calls(_text, body + 1, last, key, new Locals(parameters), frame.Owner, depth: 0);
            }
            finally
            {
                _typeParameters.ExceptWith(added);
            }
        }

        Defer(Read);
    }

    /// <summary>Reads <paramref name="read"/> once the type being read has every member declared; now, outside one.</summary>
    private void Defer(Action read)
    {
        if (_bodies is not null)
        {
            _bodies.Add(read);
        }
        else
        {
            read();
        }
    }

    /// <summary>
    /// A callable's body after its signature, which ends before <paramref name="signatureEnd"/>: past <c>async</c>,
    /// <c>async*</c> or <c>sync*</c>, a block, an arrow to its <c>;</c>, or a <c>;</c> alone - the body's first index, or
    /// -1 where there is none, and its last; -1 twice where none of these follows.
    /// </summary>
    private (int Body, int Last) Body(int signatureEnd, int end)
    {
        var at = signatureEnd < _scan.Count && _scan[signatureEnd].Kind != ScanTokenKind.Directive ? signatureEnd : _scan.Next(signatureEnd - 1);

        if (_scan.Is(at, "async"))
        {
            at = _scan.Next(at);

            if (_scan.Is(at, "*"))
            {
                at = _scan.Next(at);
            }
        }
        else if (_scan.Is(at, "sync") && _scan.Is(_scan.Next(at), "*"))
        {
            at = _scan.Next(_scan.Next(at));
        }

        if (_scan.Is(at, "{"))
        {
            return (at, Math.Min(_scan.Bounds(at, end).Inner, _scan.Count - 1));
        }

        if (_scan.Is(at, "=>"))
        {
            return (at, _scan.LastOf(ArrowEnd(_scan, at, end)));
        }

        return _scan.Is(at, ";") ? (-1, at) : (-1, -1);
    }

    /// <summary>The index past an arrow's expression at <paramref name="arrow"/>: past its <c>;</c>, or at the bracket that closes what holds it.</summary>
    private static int ArrowEnd(ScannedText scan, int arrow, int end)
    {
        for (var at = arrow + 1; at < end && at < scan.Count; at++)
        {
            if (scan.Opens(at))
            {
                at = Math.Min(scan.Past(at), end) - 1;
            }
            else if (scan.Is(at, ";"))
            {
                return at + 1;
            }
            else if (scan.Is(at, ")") || scan.Is(at, "]") || scan.Is(at, "}"))
            {
                return at;
            }
        }

        return Math.Min(end, scan.Count);
    }

    /// <summary>The index of the <c>{</c> that opens a type's body from <paramref name="at"/>, past its clauses; -1 where a <c>;</c> comes first.</summary>
    private int BodyOpen(int at, int end)
    {
        for (; at < end && at < _scan.Count; at++)
        {
            if (_scan.Is(at, "{"))
            {
                return at;
            }

            if (_scan.Is(at, ";") || _scan.Is(at, "}"))
            {
                return -1;
            }

            if (_scan.Is(at, "(") || _scan.Is(at, "["))
            {
                at = _scan.Bounds(at, end).Past - 1;
            }
            else if (_scan.Is(at, "<"))
            {
                at = _scan.AngleEnd(at, end) - 1;
            }
        }

        return -1;
    }

    /// <summary>Whether the brace at <paramref name="at"/> in an initializer list is a collection's - a map's or a set's - and no body: what comes before it is no part of a list's end.</summary>
    private bool IsCollection(int at)
        => _scan.Previous(at) is var before && (_scan.Is(before, "=") || _scan.Is(before, "(") || _scan.Is(before, ",") || _scan.Is(before, ":") || _scan.Is(before, ">") || _scan.Is(before, "const"));

    /// <summary>The names of the type parameters the list at <paramref name="less"/> declares.</summary>
    private List<string> TypeParameters(int less, int end)
    {
        var past = _scan.AngleEnd(less, end);

        return [.. ScanCalls.Split(_scan, less + 1, past - 1, DartTypes)
            .Select(piece => _scan.Code(piece.Start, piece.End).FirstOrDefault(index => _scan.IsName(index) && !_scan.Is(index, "covariant"), -1))
            .Where(index => index >= 0)
            .Select(index => _scan.Text(index))];
    }

    // ---- Parameters ----

    /// <summary>A callable's parameters: how many a call gives - its required positional and named ones the fewest - the spans of their types, and the names each binds.</summary>
    private sealed record Parameters(int? Least, int? Most, IReadOnlyList<(int Start, int End)> TypeSpans, IReadOnlyDictionary<string, string?> Locals, IReadOnlySet<string> Functions);

    /// <summary>
    /// The parameters between the parentheses at <paramref name="open"/> and <paramref name="close"/>: positional ones, then
    /// <c>[...]</c> optional ones or <c>{...}</c> named ones, each with its type, its default and its modifiers; an
    /// initializing one (<c>this.x</c>, <c>super.x</c>) binds no name of its own, the field's being the one it names.
    /// </summary>
    private static Parameters ParametersOf(ScannedText scan, int open, int close)
    {
        var least = 0;
        var most = 0;
        var spans = new List<(int, int)>();
        var locals = new Dictionary<string, string?>(StringComparer.Ordinal);
        var functions = new HashSet<string>(StringComparer.Ordinal);

        void One(int start, int end, bool optional, bool named)
        {
            var at = Annotations(scan, start, end, null);

            if (at >= end)
            {
                return;
            }

            var required = false;

            while (scan.IsName(at) && ParameterModifiers.Contains(scan.Text(at)) && scan.Next(at) < end && scan.IsName(scan.Next(at)))
            {
                required |= scan.Is(at, "required");
                at = scan.Next(at);
            }

            most++;
            least += (named && required) || (!optional && !named) ? 1 : 0;

            var stop = Math.Min(ScanCalls.TopLevel(scan, at, end, "=", DartTypes), ScanCalls.TopLevel(scan, at, end, ":", DartTypes));

            if ((scan.Is(at, "this") || scan.Is(at, "super")) && scan.Is(scan.Next(at), "."))
            {
                return;
            }

            var name = -1;
            var function = false;

            for (var index = at; index < stop; index++)
            {
                if (scan.Is(index, "<"))
                {
                    index = scan.AngleEnd(index, stop) - 1;
                }
                else if (scan.Opens(index))
                {
                    // A parameter written as a function - int compare(int a, int b) - is a function value.
                    function |= name >= 0 && scan.Is(index, "(") && scan.Previous(index) == name;
                    index = Math.Min(scan.Past(index), stop) - 1;
                }
                else if (scan.IsName(index) && !scan.Is(index, "Function"))
                {
                    name = index;
                }
            }

            if (name < 0)
            {
                return;
            }

            spans.Add((at, name));
            var text = scan.Text(name);
            locals[text] = function ? null : TypeKey(scan, at, name);

            if (function || IsFunctionType(scan, at, name))
            {
                functions.Add(text);
            }
        }

        foreach (var (start, end) in ScanCalls.Split(scan, open + 1, close, DartTypes))
        {
            if (scan.Is(start, "[") || scan.Is(start, "{"))
            {
                var inner = Math.Min(scan.Bounds(start, end).Inner, end);

                foreach (var (subStart, subEnd) in ScanCalls.Split(scan, start + 1, inner, DartTypes))
                {
                    One(subStart, subEnd, optional: true, named: scan.Is(start, "{"));
                }
            }
            else
            {
                One(start, end, optional: false, named: false);
            }
        }

        return new Parameters(least, most, spans, locals, functions);
    }

    // ---- What a body calls ----

    /// <summary>The names a body binds: each local's and parameter's type where the code says one, those that are function values, and its local functions.</summary>
    private sealed class Locals
    {
        public Locals()
        {
        }

        public Locals(Parameters parameters) => Add(parameters);

        public Dictionary<string, string?> Types { get; } = new(StringComparer.Ordinal);

        public HashSet<string> Functions { get; } = new(StringComparer.Ordinal);

        public HashSet<string> LocalFunctions { get; } = new(StringComparer.Ordinal);

        public void Add(Parameters parameters)
        {
            foreach (var (name, type) in parameters.Locals)
            {
                Types[name] = type;
            }

            Functions.UnionWith(parameters.Functions);
        }
    }

    /// <summary>
    /// Each call the tokens of <paramref name="text"/> from <paramref name="from"/> to <paramref name="to"/> make, from the
    /// node of <paramref name="key"/>: a reference by its name and arity, qualified by its receiver where the code says -
    /// a member's, a cascade's target, a constructor's type - and what it cannot bind marked dynamic. A string's
    /// interpolations are read as text of their own, a test framework's test calls as nodes of their own, and a pattern's
    /// types as what it references. <paramref name="owner"/> is the type whose members the body sees.
    /// </summary>
    private void Calls(InterpolatedText text, int from, int to, string key, Locals locals, string? owner, int depth)
    {
        var scan = text.Scan;

        // Where the test calls read are no longer too deep to be: past the first that was.
        var shallowFrom = -1;

        for (var at = from; at < to && at < scan.Count; at++)
        {
            var token = scan[at];

            if (token.Kind == ScanTokenKind.Text)
            {
                if (text.Interpolations.TryGetValue(token.Start, out var spans) && ScanFrame.ReadsDeeper(_extract, depth, token.Line, Nesting))
                {
                    foreach (var (spanStart, spanEnd) in spans)
                    {
                        var inner = DartLexer.Scan(scan.Source, spanStart, spanEnd, text.Lines);
                        Calls(inner, 0, inner.Scan.Count, key, locals, owner, depth + 1);
                    }
                }

                continue;
            }

            if (token.Kind != ScanTokenKind.Name)
            {
                if (scan.Is(at, "(") && scan.Match(at) is var close and > 0 && OpensClosure(scan, at, close))
                {
                    locals.Add(ParametersOf(scan, at, close));
                }

                continue;
            }

            var word = scan.Text(at);
            var line = token.Line;

            if (word is "catch" && scan.Is(scan.Next(at), "(") && scan.Match(scan.Next(at)) is var caught and > 0)
            {
                Caught(scan, at, caught, locals);
                at = caught;
                continue;
            }

            if (word is "case")
            {
                var stop = PatternEnd(scan, at, to);
                Pattern(scan, scan.Next(at), stop, key, locals);
                at = stop - 1;
                continue;
            }

            if (word is "new" or "const" && Instantiated(scan, at, key) is var made and > 0)
            {
                at = made - 1;
                continue;
            }

            if (word is "super" or "this" && scan.Is(scan.Next(at), "(") && !IsMember(scan, scan.Previous(at)))
            {
                var open = scan.Next(at);
                _extract.Reference(key, CodeEdgeKinds.Calls, "new", line, ScanCalls.CallArity(scan, open, scan.CloseOf(open), DartCalls),
                    new ExtractQualifier(word == "super" ? CodeQualifierKinds.Base : CodeQualifierKinds.This));
                continue;
            }

            Local(scan, at, locals);

            if (ScanCalls.At(scan, at, IsMember(scan, scan.Previous(at)) ? DartMemberCalls : DartCalls) is not { } call)
            {
                continue;
            }

            var previous = scan.Previous(at);

            if (IsMember(scan, previous))
            {
                MemberCall(scan, previous, call, key, locals, word, line);
                continue;
            }

            if (scan.Is(previous, "var") || scan.Is(previous, "final") || IsSwitchArm(scan, at, call))
            {
                // A pattern: var Point(:x, :y) = p, or an arm of a switch expression.
                Pattern(scan, at, call.Close + 1, key, locals);
                at = call.Close;
                continue;
            }

            if (IsLocalFunction(scan, previous, call))
            {
                locals.LocalFunctions.Add(word);
                locals.Add(ParametersOf(scan, call.Open, call.Close));
                at = call.Close;
                continue;
            }

            // A test call nested too deep is marked once, those it holds read as its holder's calls.
            if (_testFile && TestCalls.Contains(word) && at > shallowFrom)
            {
                if (!ScanFrame.ReadsDeeper(_extract, depth, line, Nesting))
                {
                    shallowFrom = call.Close;
                }
                else if (Test(text, at, call, key, locals, owner, depth) is var tested and > 0)
                {
                    at = tested;
                    continue;
                }
            }

            if (locals.LocalFunctions.Contains(word))
            {
                continue;
            }

            if (locals.Types.ContainsKey(word) || locals.Functions.Contains(word)
                || (owner is not null && _functionFields.TryGetValue(owner, out var fields) && fields.Contains(word))
                || _functionValues.Contains(word))
            {
                _extract.Dynamic(line, $"a call through the function value {word}");
                continue;
            }

            _extract.Reference(key, CodeEdgeKinds.Calls, word, line, call.Arity);
        }
    }

    /// <summary>
    /// A call of <paramref name="word"/> on what stands before the <c>.</c>, <c>?.</c>, <c>..</c> or <c>?..</c> at
    /// <paramref name="dot"/>: a reference qualified by that receiver, a cascade's its target; one through a function
    /// value's <c>call</c>, <c>Function.apply</c> or a field of a function type marked dynamic, and not linked.
    /// </summary>
    private void MemberCall(ScannedText scan, int dot, ScannedCall call, string key, Locals locals, string word, int line)
    {
        var target = scan.Is(dot, "..") || scan.Is(dot, "?..") ? CascadeHead(scan, dot) : dot;

        if (word == "call" || (word == "apply" && scan.Is(scan.Previous(target), "Function")))
        {
            _extract.Dynamic(line, word == "call" ? "a call through a function value's call" : "a call through Function.apply");
            return;
        }

        var receiver = Receiver(scan, target, locals, out var dynamic);

        if (dynamic is not null)
        {
            _extract.Dynamic(line, $"a call on the dynamic value {dynamic}");
        }

        if (receiver.Kind == CodeQualifierKinds.This && IsFunctionField(_extract.Node(key), word))
        {
            _extract.Dynamic(line, $"a call through the function value {word}");
            return;
        }

        _extract.Reference(key, CodeEdgeKinds.Calls, word, line, call.Arity, receiver);
    }

    /// <summary>Whether <paramref name="name"/> is a field of a function type of the type the node <paramref name="from"/> is a member of.</summary>
    private bool IsFunctionField(ExtractNode from, string name)
        => from.Qualified.LastIndexOf('.') is var dot and > 0 && from.Qualified[..dot] is var holder
            && _functionFields.TryGetValue(holder[(holder.LastIndexOf('.') + 1)..], out var fields) && fields.Contains(name);

    /// <summary>Whether the token at <paramref name="at"/> makes what follows it a member of what precedes it: <c>.</c>, <c>?.</c>, <c>..</c>, <c>?..</c>.</summary>
    private static bool IsMember(ScannedText scan, int at) => at >= 0 && at < scan.Count && Accessors.Contains(scan.Text(at));

    /// <summary>
    /// The first <c>..</c> or <c>?..</c> of the cascade the one at <paramref name="cascade"/> is a section of: back over
    /// the sections before it at its own depth - each a member's name, its arguments and what it assigns - to what
    /// begins the cascade.
    /// </summary>
    private static int CascadeHead(ScannedText scan, int cascade)
    {
        var head = cascade;

        for (var at = scan.Previous(cascade); at >= 0; at = scan.Previous(at))
        {
            if (scan.Is(at, ")") || scan.Is(at, "]") || scan.Is(at, "}"))
            {
                if (scan.Match(at) is not (var open and >= 0))
                {
                    break;
                }

                at = open;
            }
            else if (scan.Is(at, "..") || scan.Is(at, "?.."))
            {
                head = at;
            }
            else if (scan.Opens(at) || scan.Is(at, ";") || scan.Is(at, ",") || scan.Is(at, "=>"))
            {
                break;
            }
        }

        return head;
    }

    /// <summary>
    /// What a call on the receiver before the token at <paramref name="dot"/> is qualified by: <c>this</c>, <c>super</c>,
    /// an import's prefix, the type a local is declared with, a type a constructor's call makes, a type's name, or a
    /// value by its name - <paramref name="dynamic"/> the local's name where it is declared <c>dynamic</c>.
    /// </summary>
    private ExtractQualifier Receiver(ScannedText scan, int dot, Locals locals, out string? dynamic)
    {
        dynamic = null;
        var names = new List<string>();
        var at = Unasserted(scan, scan.Previous(dot));
        var first = -1;

        while (true)
        {
            if (scan.Is(at, ">") && ScanCalls.TypeArgumentsStart(scan, at, DartTypes) is var less and >= 0 && scan.IsName(scan.Previous(less)))
            {
                at = scan.Previous(less);
            }

            if (!scan.IsName(at))
            {
                break;
            }

            names.Add(scan.Text(at));
            first = at;
            var before = scan.Previous(at);

            if (!(scan.Is(before, ".") || scan.Is(before, "?.")))
            {
                break;
            }

            at = Unasserted(scan, scan.Previous(before));
        }

        // Read from its end, last first.
        names.Reverse();

        if (names.Count == 0)
        {
            var close = Unasserted(scan, scan.Previous(dot));

            return scan.Is(close, ")") && scan.Match(close) is var open and >= 0 && Made(scan, open) is { } made
                ? new ExtractQualifier(CodeQualifierKinds.Type, made)
                : new ExtractQualifier(CodeQualifierKinds.Value);
        }

        // A chain that is a member of something more - a call's result, a cascade's section - is a value of no type the file says.
        if (IsMember(scan, scan.Previous(first)))
        {
            return new ExtractQualifier(CodeQualifierKinds.Value);
        }

        if (names[0] == "this")
        {
            names.RemoveAt(0);
            return names.Count == 0 ? new ExtractQualifier(CodeQualifierKinds.This) : new ExtractQualifier(CodeQualifierKinds.Value, string.Join('.', names));
        }

        if (names[0] == "super")
        {
            return names.Count == 1 ? new ExtractQualifier(CodeQualifierKinds.Base) : new ExtractQualifier(CodeQualifierKinds.Value);
        }

        if (locals.Types.TryGetValue(names[0], out var declared))
        {
            if (declared == "dynamic")
            {
                dynamic = names[0];
                return new ExtractQualifier(CodeQualifierKinds.Value);
            }

            return names.Count == 1 && declared is not null ? new ExtractQualifier(CodeQualifierKinds.Type, declared) : new ExtractQualifier(CodeQualifierKinds.Value);
        }

        if (_prefixes.Contains(names[0]))
        {
            return names.Count == 1 ? new ExtractQualifier(CodeQualifierKinds.Prefix, names[0]) : new ExtractQualifier(CodeQualifierKinds.Named, string.Join('.', names));
        }

        return IsTypeName(names[0]) ? new ExtractQualifier(CodeQualifierKinds.Named, string.Join('.', names)) : new ExtractQualifier(CodeQualifierKinds.Value, string.Join('.', names));
    }

    /// <summary><paramref name="at"/>, or the token before it where it is a non-null assertion's <c>!</c>.</summary>
    private static int Unasserted(ScannedText scan, int at) => scan.Is(at, "!") ? scan.Previous(at) : at;

    /// <summary>
    /// The type a constructor's call at <paramref name="open"/>'s parenthesis makes, where the call is one - a type's name,
    /// a prefix before it, a constructor's name after it, <c>new</c> or <c>const</c> before - as it is written: <c>Foo</c>,
    /// <c>p.Foo</c>; <see langword="null"/> where the call is none.
    /// </summary>
    private string? Made(ScannedText scan, int open)
    {
        var at = scan.Previous(open);

        if (scan.Is(at, ">") && ScanCalls.TypeArgumentsStart(scan, at, DartTypes) is var less and >= 0)
        {
            at = scan.Previous(less);
        }

        if (!scan.IsName(at))
        {
            return null;
        }

        var names = ChainAt(scan, FirstOfChain(scan, at), scan.Next(at)).Names;

        // Foo<T>.named(: the type's arguments between its name and its constructor's.
        if (scan.Is(scan.Previous(names[0]), ".") && scan.Previous(scan.Previous(names[0])) is var greater && scan.Is(greater, ">")
            && ScanCalls.TypeArgumentsStart(scan, greater, DartTypes) is var arguments and >= 0 && scan.IsName(scan.Previous(arguments)))
        {
            names = [.. ChainAt(scan, FirstOfChain(scan, scan.Previous(arguments)), arguments).Names, .. names];
        }

        if (IsMember(scan, scan.Previous(names[0])))
        {
            return null;
        }

        var written = names.Select(index => scan.Text(index)).ToList();

        if (_prefixes.Contains(written[0]) && written.Count > 1)
        {
            return IsTypeName(written[1]) && written.Count <= 3 ? $"{written[0]}.{written[1]}" : null;
        }

        return IsTypeName(written[0]) && written.Count <= 2 ? written[0] : null;
    }

    /// <summary>
    /// The type the initializer from <paramref name="at"/> to <paramref name="end"/> makes, where it is a constructor's
    /// call and nothing more - <c>Foo(...)</c>, <c>const Foo.named(...)</c>, a cascade on one; <see langword="null"/> otherwise.
    /// </summary>
    private string? Made(ScannedText scan, int at, int end)
    {
        if (scan.Is(at, "new") || scan.Is(at, "const"))
        {
            at = scan.Next(at);
        }

        var (names, past) = ChainAt(scan, at, end);

        if (names.Count == 0)
        {
            return null;
        }

        if (scan.Is(past, "<") && ScanCalls.TypeArgumentsEnd(scan, past, DartTypes) is var arguments and >= 0)
        {
            past = arguments;

            // Foo<T>.named(...)
            if (scan.Is(past, ".") && scan.IsName(scan.Next(past)))
            {
                past = scan.Next(scan.Next(past));
            }
        }

        if (!scan.Is(past, "("))
        {
            return null;
        }

        var after = scan.Past(past);

        if (!(after >= end || scan.Is(after, ";") || scan.Is(after, ",") || scan.Is(after, "..") || scan.Is(after, "?..")))
        {
            return null;
        }

        return Made(scan, past);
    }

    /// <summary>
    /// A <c>new</c> or <c>const</c> at <paramref name="at"/> before a constructor's call: an <c>instantiates</c> reference to
    /// its type; answers where the reading goes on - at a named constructor's name, read as a call of it - or 0 where it
    /// is none, a <c>const</c> declaration's or a collection's.
    /// </summary>
    private int Instantiated(ScannedText scan, int at, string key)
    {
        var (names, past) = ChainAt(scan, scan.Next(at), scan.Count);

        if (names.Count == 0 || NoCalls.Contains(scan.Text(names[0])))
        {
            return 0;
        }

        var typed = past;

        if (scan.Is(typed, "<") && ScanCalls.TypeArgumentsEnd(scan, typed, DartTypes) is var arguments and >= 0)
        {
            typed = arguments;
        }

        if (!scan.Is(typed, "(") && !(scan.Is(typed, ".") && scan.IsName(scan.Next(typed)) && scan.Is(scan.Next(scan.Next(typed)), "(")))
        {
            return 0;
        }

        // p.Foo.named: the prefix, the type and the constructor's name.
        var written = names.Select(index => scan.Text(index)).ToList();
        var typeNames = written.Count > 1 && !IsTypeName(written[^1]) ? written.Count - 1 : written.Count;
        Mention(key, CodeEdgeKinds.Instantiates, string.Join('.', written.Take(typeNames)), scan[names[0]].Line);

        // A constructor named after its type is read on as a call of that name on the type: Foo.named( calls Foo's named.
        return typeNames < written.Count ? names[typeNames] : scan.Next(names[typeNames - 1]);
    }

    /// <summary>The names joined by dots from <paramref name="at"/>, by index, and the index past the last.</summary>
    private static (List<int> Names, int Past) ChainAt(ScannedText scan, int at, int end)
    {
        var names = new List<int>();

        while (at < end && scan.IsName(at))
        {
            names.Add(at);
            var next = scan.Next(at);

            if (!scan.Is(next, ".") || !scan.IsName(scan.Next(next)))
            {
                return (names, next);
            }

            at = scan.Next(next);
        }

        return (names, at);
    }

    /// <summary>
    /// A local the body declares at <paramref name="at"/>, where it declares one: after <c>var</c>, <c>final</c>,
    /// <c>const</c> or <c>late</c> - its type what its initializer makes - or after a type, which it is declared with.
    /// </summary>
    private void Local(ScannedText scan, int at, Locals locals)
    {
        var next = scan.Next(at);
        var word = scan.Text(at);

        if (Reserved.Contains(word) || !(scan.Is(next, "=") || scan.Is(next, ";") || scan.Is(next, ",") || scan.Is(next, "in") || scan.Is(next, ")") || scan.Is(next, ":")))
        {
            return;
        }

        var previous = scan.Previous(at);

        if (scan.Is(previous, "var") || scan.Is(previous, "final") || scan.Is(previous, "const") || scan.Is(previous, "late"))
        {
            locals.Types[word] = scan.Is(next, "=") ? Made(scan, scan.Next(next), scan.StatementEnd(next, scan.Count)) : null;
            return;
        }

        if (TypeBefore(scan, previous, nullableToo: !scan.Is(next, ":")) is { } typed)
        {
            locals.Types[word] = typed.Type;

            if (typed.Function)
            {
                locals.Functions.Add(word);
            }
        }
    }

    /// <summary>
    /// The type the tokens up to <paramref name="end"/> end with, where they end with one - a name, a prefix's chain, type
    /// arguments, a nullable <c>?</c> where <paramref name="nullableToo"/>, a function type - and whether it is a function
    /// type; <see langword="null"/> where no type ends there.
    /// </summary>
    private (string? Type, bool Function)? TypeBefore(ScannedText scan, int end, bool nullableToo)
    {
        var at = end;

        if (scan.Is(at, "?"))
        {
            if (!nullableToo)
            {
                return null;
            }

            at = scan.Previous(at);
        }

        if (scan.Is(at, ">"))
        {
            if (ScanCalls.TypeArgumentsStart(scan, at, DartTypes) is not (var less and >= 0))
            {
                return null;
            }

            at = scan.Previous(less);
        }
        else if (scan.Is(at, ")"))
        {
            // void Function(int) callback
            return scan.Match(at) is var open and >= 0 && scan.Is(scan.Previous(open), "Function") ? (null, true) : null;
        }

        if (!scan.IsName(at) || (NoTypes.Contains(scan.Text(at)) && !scan.Is(at, "Function")))
        {
            return null;
        }

        if (scan.Is(at, "Function"))
        {
            return (null, true);
        }

        var last = at;
        at = FirstOfChain(scan, last);

        if (IsMember(scan, scan.Previous(at)))
        {
            return null;
        }

        var type = scan.Text(at, last + 1).Replace(" ", string.Empty, StringComparison.Ordinal);
        return (type, IsFunctionTypeName(scan.Text(last)));
    }

    /// <summary>A <c>catch</c>'s exception and stack trace at <paramref name="at"/>, as locals: the exception of the type its <c>on</c> names.</summary>
    private void Caught(ScannedText scan, int at, int close, Locals locals)
    {
        var pieces = ScanCalls.Split(scan, scan.Next(at) + 1, close, null);
        var on = scan.Previous(at);
        var type = TypeBefore(scan, on, nullableToo: false) is { Type: { } named } && scan.Is(scan.Previous(FirstOfChain(scan, on)), "on") ? named : null;

        for (var index = 0; index < pieces.Count; index++)
        {
            if (scan.IsName(pieces[index].Start))
            {
                locals.Types[scan.Text(pieces[index].Start)] = index == 0 ? type : "StackTrace";
            }
        }
    }

    /// <summary>The first name of the chain the name at <paramref name="last"/> ends.</summary>
    private static int FirstOfChain(ScannedText scan, int last)
    {
        var at = last;

        while (scan.Is(scan.Previous(at), ".") && scan.IsName(scan.Previous(scan.Previous(at))))
        {
            at = scan.Previous(scan.Previous(at));
        }

        return at;
    }

    /// <summary>
    /// Whether the parenthesis at <paramref name="open"/>, closed at <paramref name="close"/>, holds a closure's
    /// parameters: a body follows it, and what precedes it is no call's name nor a statement's word that a parenthesis
    /// follows.
    /// </summary>
    private static bool OpensClosure(ScannedText scan, int open, int close)
    {
        var after = scan.Next(close);

        if (!(scan.Is(after, "{") || scan.Is(after, "=>") || scan.Is(after, "async") || (scan.Is(after, "sync") && scan.Is(scan.Next(after), "*"))))
        {
            return false;
        }

        var before = scan.Previous(open);

        return before < 0
            || (scan.IsName(before) ? scan.Text(before) is "return" or "await" or "yield" or "else" or "in" or "throw" or "when"
                : !(scan.Is(before, ">") || scan.Is(before, ")") || scan.Is(before, "]")));
    }

    /// <summary>
    /// Whether the call at <paramref name="call"/>, after <paramref name="previous"/>, declares a local function: a body
    /// follows its parameters, and a type or a statement's start comes before its name.
    /// </summary>
    private bool IsLocalFunction(ScannedText scan, int previous, ScannedCall call)
    {
        var after = scan.Next(call.Close);

        if (!(scan.Is(after, "{") || scan.Is(after, "=>") || scan.Is(after, "async") || (scan.Is(after, "sync") && scan.Is(scan.Next(after), "*"))))
        {
            return false;
        }

        return previous < 0 || scan.Is(previous, ";") || scan.Is(previous, "{") || scan.Is(previous, "}") || TypeBefore(scan, previous, nullableToo: true) is not null;
    }

    /// <summary>Whether the call at <paramref name="at"/> is an object pattern of an arm of a switch expression: an arrow or a guard follows it, and a switch's body holds it.</summary>
    private static bool IsSwitchArm(ScannedText scan, int at, ScannedCall call)
    {
        var after = scan.Next(call.Close);

        if (!(scan.Is(after, "=>") || scan.Is(after, "when")) || call.Close >= scan.Count)
        {
            return false;
        }

        var holder = Holder(scan, at);
        return holder >= 0 && scan.Is(holder, "{") && scan.Is(scan.Previous(holder), ")") && scan.Match(scan.Previous(holder)) is var open and >= 0 && scan.Is(scan.Previous(open), "switch");
    }

    /// <summary>The index of the bracket that holds the token at <paramref name="at"/>; -1 where none does.</summary>
    private static int Holder(ScannedText scan, int at)
    {
        for (var index = scan.Previous(at); index >= 0; index = scan.Previous(index))
        {
            if (scan.Is(index, ")") || scan.Is(index, "]") || scan.Is(index, "}"))
            {
                if (scan.Match(index) is not (var open and >= 0))
                {
                    return -1;
                }

                index = open;
            }
            else if (scan.Opens(index))
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>The index past the pattern a <c>case</c> at <paramref name="at"/> begins: at its <c>:</c>, its arrow, its guard, or the bracket that ends what holds it.</summary>
    private static int PatternEnd(ScannedText scan, int at, int to)
    {
        for (var index = scan.Next(at); index < to && index < scan.Count; index = scan.Next(index))
        {
            if (scan.Opens(index))
            {
                index = Math.Min(scan.Past(index), to) - 1;
            }
            else if (scan.Is(index, ":") || scan.Is(index, "=>") || scan.Is(index, "when") || scan.Is(index, ";") || scan.Is(index, ")") || scan.Is(index, "]") || scan.Is(index, "}"))
            {
                return index;
            }
        }

        return Math.Min(to, scan.Count);
    }

    /// <summary>
    /// A pattern from <paramref name="from"/> to <paramref name="to"/>: each object pattern a <c>references</c> edge to the
    /// type it names, each constant one's constructor an <c>instantiates</c> edge, each name it binds a local.
    /// </summary>
    private void Pattern(ScannedText scan, int from, int to, string key, Locals locals)
    {
        for (var at = from; at < to && at < scan.Count; at++)
        {
            if (!scan.IsName(at))
            {
                continue;
            }

            if ((scan.Is(at, "const") || scan.Is(at, "new")) && Instantiated(scan, at, key) is var made and > 0)
            {
                at = made - 1;
                continue;
            }

            Local(scan, at, locals);

            if (IsMember(scan, scan.Previous(at)) || NoTypes.Contains(scan.Text(at)))
            {
                continue;
            }

            var (names, past) = ChainAt(scan, at, to);

            if (scan.Is(past, "<") && ScanCalls.TypeArgumentsEnd(scan, past, DartTypes) is var arguments and >= 0)
            {
                past = arguments;
            }

            if (scan.Is(past, "("))
            {
                Mention(key, CodeEdgeKinds.References, string.Join('.', names.Select(index => scan.Text(index))), scan[at].Line);
            }

            at = names[^1];
        }
    }

    /// <summary>
    /// A test framework's call at <paramref name="at"/> - <c>test</c>, <c>testWidgets</c>, <c>group</c> - as a node of its
    /// own, named by its description and held by the node of <paramref name="parent"/>, what its arguments call its own;
    /// answers the index of its closing parenthesis, or 0 where it gives no description.
    /// </summary>
    private int Test(InterpolatedText text, int at, ScannedCall call, string parent, Locals locals, string? owner, int depth)
    {
        var scan = text.Scan;
        var pieces = ScanCalls.Split(scan, call.Open + 1, call.Close, DartTypes);

        if (pieces.Count == 0 || call.Close >= scan.Count || ExtractText.Flat(scan.Spelled(pieces[0].Start, pieces[0].End)) is not { Length: > 0 } name)
        {
            return 0;
        }

        var word = scan.Text(at);
        var qualified = $"{_extract.Node(parent).Qualified}.{name}";
        var key = _extract.Add(new ExtractNode(Id(qualified), CodeNodeKinds.Member, name, qualified, scan[at].Line, scan[call.Close].EndLine, ExtractText.Signature($"{word}({name})"))
        {
            SubKind = word == "group" ? "testGroup" : "test",
            Scope = _library,
            Parent = parent,
            Summary = scan == _scan ? ScanComments.Summary(scan, at) : null,
            Test = true,
        });

        Calls(text, pieces[0].Start, call.Close, key, locals, owner, depth + 1);
        return call.Close;
    }

    // ---- Names and types ----

    /// <summary>The names joined by dots at <paramref name="at"/>, joined by dots, and the index past the last; no chain where no name is there.</summary>
    private static (string? Chain, int Past) Chain(ScannedText scan, int at, int end)
    {
        var (names, past) = ChainAt(scan, at, end);
        return (names.Count > 0 ? string.Join('.', names.Select(index => scan.Text(index))) : null, past);
    }

    /// <summary>
    /// The type a reference of <paramref name="kind"/> names: its last name, qualified by the prefix written before it -
    /// an import's, where the file imports by it - or the names before it as a type's.
    /// </summary>
    private void Mention(string key, string kind, string chain, int line)
    {
        var dot = chain.LastIndexOf('.');

        if (dot < 0)
        {
            _extract.Reference(key, kind, chain, line);
            return;
        }

        _extract.Reference(key, kind, chain[(dot + 1)..], line, qualifier: Qualifier(chain[..dot]));
    }

    /// <summary>What a name written after <paramref name="written"/> is qualified by: an import's prefix, where the file imports by it; a type's name otherwise.</summary>
    private ExtractQualifier Qualifier(string written)
        => _prefixes.Contains(written) ? new ExtractQualifier(CodeQualifierKinds.Prefix, written) : new ExtractQualifier(CodeQualifierKinds.Type, written);

    /// <summary>
    /// A <c>references</c> edge from the node of <paramref name="key"/> to each type the tokens from <paramref name="start"/>
    /// to <paramref name="end"/> name that <c>dart:core</c> does not declare, that no type parameter in force is, and that
    /// is no record field's name.
    /// </summary>
    private void TypeReferences(string key, int start, int end)
    {
        for (var at = start; at < end && at < _scan.Count; at = _scan.Next(at))
        {
            if (!_scan.IsName(at) || NoTypes.Contains(_scan.Text(at)) || MemberModifiers.Contains(_scan.Text(at)) || Builtins.Contains(_scan.Text(at))
                || _typeParameters.Contains(_scan.Text(at)) || IsMember(_scan, _scan.Previous(at)) || _scan.Is(at, "dynamic"))
            {
                continue;
            }

            // A record's field - (int count, String name) or {int count} - follows its type, within the span.
            var next = _scan.Next(at);

            if (next < end && (_scan.Is(next, ",") || _scan.Is(next, ")") || _scan.Is(next, "}")) && _scan.Previous(at) >= start
                && TypeBefore(_scan, _scan.Previous(at), nullableToo: true) is not null)
            {
                continue;
            }

            var (chain, past) = Chain(_scan, at, end);

            if (chain is not null && !(chain.Contains('.', StringComparison.Ordinal) && !_prefixes.Contains(chain[..chain.IndexOf('.', StringComparison.Ordinal)])))
            {
                Mention(key, CodeEdgeKinds.References, chain, _scan[at].Line);
            }

            at = Math.Max(at, _scan.Previous(past));
        }
    }

    /// <summary>The type an extension's <c>on</c> names from <paramref name="at"/> to <paramref name="end"/>, as its members are declared to extend it: the chain of its name, or the type as written where it is no name.</summary>
    private string ExtendedType(int at, int end)
        => Chain(_scan, at, end).Chain is { } chain ? chain : ExtractText.Flat(_scan.Spelled(at, end)) is { Length: > 0 } written ? written : "?";

    /// <summary>
    /// What a declaration from <paramref name="start"/> to <paramref name="end"/> is declared with, as a type qualifier names
    /// it: the chain of its type's name, its arguments and a nullable mark left out - <c>dynamic</c> as it is; <see langword="null"/>
    /// where it is no name's - a record, a function type, none.
    /// </summary>
    private static string? TypeKey(ScannedText scan, int start, int end)
    {
        var at = start;

        while (at < end && scan.IsName(at) && (MemberModifiers.Contains(scan.Text(at)) || ParameterModifiers.Contains(scan.Text(at))))
        {
            at = scan.Next(at);
        }

        if (at >= end || !scan.IsName(at) || scan.Is(at, "void") || scan.Is(at, "Function"))
        {
            return null;
        }

        var (chain, past) = Chain(scan, at, end);

        if (scan.Is(past, "<"))
        {
            past = scan.AngleEnd(past, end);
        }

        // A function type written after its return type - int Function(int) - is no value of that type.
        return scan.Is(past, "?") ? scan.Is(scan.Next(past), "Function") ? null : chain : scan.Is(past, "Function") ? null : chain;
    }

    /// <summary>Whether the type from <paramref name="start"/> to <paramref name="end"/> is a function's: it writes <c>Function</c>, or names a function type.</summary>
    private bool IsFunctionType(int start, int end) => IsFunctionType(_scan, start, end) || (TypeKey(_scan, start, end) is { } type && IsFunctionTypeName(type));

    private static bool IsFunctionType(ScannedText scan, int start, int end) => scan.Code(start, end).Any(index => scan.Is(index, "Function"));

    /// <summary>Whether <paramref name="type"/> names a function type: one of the file's typedefs, or one of Flutter's callbacks.</summary>
    private bool IsFunctionTypeName(string type) => _functionTypes.Contains(type) || CallbackTypes.Contains(type);

    /// <summary>Whether <paramref name="name"/> is private to its library: it begins with <c>_</c>.</summary>
    private static bool IsPrivate(string name) => name.StartsWith('_');

    /// <summary>Whether <paramref name="name"/> is written as a type's is: its first letter, past any <c>_</c> or <c>$</c>, a capital.</summary>
    private static bool IsTypeName(string name) => name.TrimStart('_', '$') is { Length: > 0 } trimmed && char.IsUpper(trimmed[0]);

    /// <summary>The id of a declaration of the qualified name <paramref name="qualified"/>.</summary>
    private string Id(string qualified) => _prefix + qualified;

}
