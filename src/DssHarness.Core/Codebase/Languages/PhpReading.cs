using DssHarness.Core.Codebase.Languages.Scanning;

namespace DssHarness.Core.Codebase.Languages;

/// <summary>
/// One PHP file's reading over the structural scanner: its namespaces, its <c>use</c> declarations and the files it
/// includes; what stands in each namespace - each class, interface, trait, enum, function and constant - and in each type -
/// each method, constructor and its promoted properties, property, constant and enum case - with its lines, signature
/// and summary; and each call a body makes by its name and arity, with what its receiver is where the file says.
/// </summary>
/// <remarks>
/// <para>
/// PHP names a declaration by its namespace, which the reader writes as the model does, a dot for PHP's <c>\</c>: the
/// namespace <c>App\Models</c> is the open scope <c>App.Models</c>, of no other - PHP looks in no namespace around another
/// - a type an open scope of its own in it. Every name a reference writes is written out to the name it stands for as PHP
/// resolves it, by the file's <c>use</c> declarations and its namespace: a class's in the namespace that declares it and
/// nowhere else; a function's in its namespace, else the global one (<see cref="CodeQualifierKinds.Scope"/>); a name in
/// the global namespace, or after a leading <c>\</c>, the repository's own top. A property is named by its variable,
/// <c>$name</c>, as PHP writes it, so it is no method of its name; a constructor is <c>__construct</c>.
/// </para>
/// <para>
/// What it cannot bind is marked dynamic: a call of a method or a function the code names by a variable, of a value
/// it computes, a <c>new</c> or a static call of a class the code computes, <c>call_user_func</c> and <c>eval</c>. A class
/// that extends a <c>TestCase</c> - or is named for a test and extends a class - is a test class: its public methods named
/// <c>test...</c> are tests, and its methods - or a trait's - marked <c>#[Test]</c> or <c>@test</c>. In a file under
/// <c>tests/</c> or <c>test/</c>, each of Pest's <c>test</c>, <c>it</c> and <c>describe</c> calls is a node of its own,
/// named by its description, the calls it makes its tests.
/// What the file's top does outside its declarations is the calls of its <c>(script)</c> node.
/// </para>
/// </remarks>
internal sealed class PhpReading
{
    /// <summary>What the reader reads code nested in no deeper than <see cref="ScanFrame.MostDepth"/> of, as its dynamic mark says it.</summary>
    private const string Nesting = "interpolations and test groups";

    /// <summary>The words a parenthesis follows that call nothing: PHP's statements and the constructs that read like calls.</summary>
    private static readonly HashSet<string> NoCalls = new(StringComparer.OrdinalIgnoreCase)
    {
        "if", "elseif", "else", "while", "do", "for", "foreach", "switch", "match", "case", "default", "catch", "try", "finally",
        "array", "list", "isset", "unset", "empty", "exit", "die", "echo", "print", "return", "fn", "function", "include",
        "include_once", "require", "require_once", "declare", "new", "clone", "use", "and", "or", "xor", "instanceof",
        "insteadof", "global", "static", "yield", "throw", "as", "goto", "const", "abstract", "final", "class", "interface",
        "trait", "enum", "namespace", "extends", "implements", "public", "protected", "private", "readonly", "var", "eval",
        "self", "parent", "__halt_compiler",
    };

    /// <summary>The types PHP declares, which no class of the repository is: no type a reference names.</summary>
    private static readonly HashSet<string> Builtins = new(StringComparer.OrdinalIgnoreCase)
    {
        "int", "float", "string", "bool", "array", "callable", "iterable", "object", "mixed", "void", "never", "null", "false", "true",
        "self", "static", "parent",
    };

    /// <summary>The words that name no value before a parenthesis that may follow theirs: a condition's, a construct's, a closure's.</summary>
    private static readonly HashSet<string> Headed = new(StringComparer.OrdinalIgnoreCase)
    {
        "if", "elseif", "while", "for", "foreach", "switch", "catch", "match", "declare", "array", "list", "isset", "unset", "empty", "exit",
        "die", "function", "fn", "use", "eval", "new",
    };

    /// <summary>The types a cast names: <c>(int)</c> and the rest, whose parentheses are no value called.</summary>
    private static readonly HashSet<string> Casts = new(StringComparer.OrdinalIgnoreCase)
    {
        "int", "integer", "bool", "boolean", "float", "double", "real", "string", "binary", "array", "object", "unset",
    };

    /// <summary>The words that may stand before a class's declaration.</summary>
    private static readonly HashSet<string> TypeModifiers = new(StringComparer.OrdinalIgnoreCase) { "abstract", "final", "readonly" };

    /// <summary>The words that may stand before a member's declaration, or a promoted parameter's.</summary>
    private static readonly HashSet<string> MemberModifiers = new(StringComparer.OrdinalIgnoreCase)
    {
        "public", "protected", "private", "static", "abstract", "final", "readonly", "var",
    };

    /// <summary>The visibilities a constructor's parameter is promoted to a property by.</summary>
    private static readonly HashSet<string> Visibilities = new(StringComparer.OrdinalIgnoreCase) { "public", "protected", "private", "readonly" };

    /// <summary>Pest's calls that declare a test or a group of them.</summary>
    private static readonly HashSet<string> TestCalls = new(StringComparer.Ordinal) { "test", "it", "describe" };

    /// <summary>The words that include a file.</summary>
    private static readonly HashSet<string> Includes = new(StringComparer.OrdinalIgnoreCase) { "require", "require_once", "include", "include_once" };

    /// <summary>How PHP writes its calls: a comma may end the arguments, with none after it.</summary>
    private static readonly ScanCallRules PhpCalls = new(NoCalls, null, TrailingComma: true);

    /// <summary>How PHP writes a member's call, whose name after its receiver's arrow or colons no keyword is: <c>$x-&gt;list(...)</c>.</summary>
    private static readonly ScanCallRules PhpMemberCalls = PhpCalls with { Keywords = new HashSet<string>(StringComparer.Ordinal) };

    private readonly InterpolatedText _text;
    private readonly ScannedText _scan;
    private readonly string _prefix;
    private readonly string _path;
    private readonly bool _testFile;
    private readonly ScanExtract _extract = new();

    /// <summary>The classes, and the namespaces, the file's <c>use</c> declarations name, by the name each is reached by - PHP's names of them are alike whatever their case.</summary>
    private readonly Dictionary<string, string> _classes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The functions the file's <c>use function</c> declarations name, by the name each is reached by.</summary>
    private readonly Dictionary<string, string> _functions = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The paths the file includes, each imported once.</summary>
    private readonly HashSet<string> _included = new(StringComparer.Ordinal);

    /// <summary>The namespace the file's code is in, as the model names it; <see langword="null"/> in the global one.</summary>
    private string? _namespace;

    /// <summary>The key of the node the file's top code is the calls of, once made.</summary>
    private string? _scriptKey;

    /// <summary>Reads <paramref name="text"/>, the file <paramref name="path"/>.</summary>
    /// <param name="language">The implementation's name, which prefixes every id.</param>
    /// <param name="path">The file's path.</param>
    /// <param name="text">Its text.</param>
    public PhpReading(string language, string path, string text)
    {
        _text = PhpLexer.Scan(text);
        _scan = _text.Scan;
        _prefix = $"{language}:";
        _path = path;
        _testFile = path.StartsWith("tests/", StringComparison.Ordinal) || path.Contains("/tests/", StringComparison.Ordinal)
            || path.StartsWith("test/", StringComparison.Ordinal) || path.Contains("/test/", StringComparison.Ordinal);
    }

    /// <summary>The file's extract.</summary>
    public FileExtract Read()
    {
        ScanFrame.Walk(0, _scan.Count, at => Statement(at, _scan.Count, ScanFrame.Top));
        _extract.Generated = ScanComments.Generated(_scan);
        return _extract.Build();
    }

    // ---- Statements ----

    /// <summary>The statement at <paramref name="at"/>, at the file's top, in a namespace or in a block of either, read in <paramref name="frame"/>; answers where the next begins.</summary>
    private int Statement(int at, int end, ScanFrame frame)
    {
        if (_scan.EndsStatement(at))
        {
            return at + 1;
        }

        // A namespace is declared at the file's top alone: one nested in another, or in a block, is none.
        if (IsWord(_scan, at, "namespace") && (_scan.IsName(_scan.Next(at)) || _scan.Is(_scan.Next(at), "{")) && frame.Depth == 0)
        {
            return Namespace(at, end);
        }

        if (IsWord(_scan, at, "use") && _scan.IsName(_scan.Next(at)))
        {
            Use(at, end);
            return _scan.StatementEnd(at, end);
        }

        if (IsWord(_scan, at, "declare") && _scan.Is(_scan.Next(at), "("))
        {
            return _scan.StatementEnd(at, end);
        }

        var start = at;
        var marks = new List<string>();
        at = Attributes(at, end, marks);

        var word = at;
        var modifiers = new List<string>();

        while (_scan.IsName(word) && TypeModifiers.Contains(_scan.Text(word)))
        {
            modifiers.Add(_scan.Text(word).ToLowerInvariant());
            word = _scan.Next(word);
        }

        foreach (var kind in (string[])["class", "interface", "trait", "enum"])
        {
            if (IsWord(_scan, word, kind) && _scan.IsName(_scan.Next(word)))
            {
                return Type(start, at, _scan.Next(word), end, frame, kind, modifiers, marks);
            }
        }

        if (IsWord(_scan, at, "function") && FunctionName(at) is var nameAt and >= 0)
        {
            return Callable(start, at, nameAt, end, frame, marks, [], "function", testNames: false, testMarks: false);
        }

        if (IsWord(_scan, at, "const") && marks.Count == 0)
        {
            return Constants(start, at, _scan.Next(at), end, frame, []);
        }

        if (IsWord(_scan, at, "if") && _scan.Is(_scan.Next(at), "("))
        {
            return Conditional(at, end, frame);
        }

        return Code(start, end);
    }

    /// <summary>
    /// A namespace's declaration at <paramref name="at"/>: one in braces over them, the global one's among them; one ended
    /// by its <c>;</c> over what follows to the next namespace's or the file's end - the namespace node over its lines, its
    /// open scope, and each statement read in it, the <c>use</c> declarations of another namespace forgotten.
    /// </summary>
    private int Namespace(int at, int end)
    {
        var next = _scan.Next(at);
        var name = _scan.IsName(next) ? Dotted(_scan.Text(next).TrimStart('\\')) : null;
        var open = name is null ? next : _scan.Next(next);
        var braced = _scan.Is(open, "{");
        int from, to, past;

        if (braced)
        {
            (to, past) = _scan.Bounds(open, end);
            from = open + 1;
        }
        else
        {
            from = _scan.StatementEnd(at, end);
            to = past = NextNamespace(from, end);
        }

        _namespace = name;
        _classes.Clear();
        _functions.Clear();
        var frame = ScanFrame.Top.Deeper();

        if (name is not null)
        {
            var last = Math.Clamp(braced ? to : _scan.LastOf(to), at, Math.Max(_scan.Count - 1, 0));
            var key = _extract.Part(new ExtractNode(Id(name), CodeNodeKinds.Namespace, Simple(name), name, _scan[at].Line, _scan[last].EndLine, _scan.Signature(at, Math.Min(open, to)))
            {
                SubKind = "namespace",
                Summary = ScanComments.Summary(_scan, at),
            });

            _extract.Scope(name, open: true, parent: null);
            frame = ScanFrame.Top.Inside(name, name, key, null);
        }

        ScanFrame.Walk(from, to, index => Statement(index, to, frame));
        return past;
    }

    /// <summary>Where the next namespace ended by its <c>;</c> begins, from <paramref name="at"/>: a <c>namespace</c> at the file's top, outside every group; the end where none does.</summary>
    private int NextNamespace(int at, int end)
    {
        for (var index = at; index < end && index < _scan.Count; index++)
        {
            if (_scan.Opens(index))
            {
                index = Math.Min(_scan.Past(index), end) - 1;
            }
            else if (IsWord(_scan, index, "namespace") && (_scan.IsName(_scan.Next(index)) || _scan.Is(_scan.Next(index), "{")))
            {
                return index;
            }
        }

        return end;
    }

    /// <summary>
    /// An <c>if</c> at the top or in a namespace, and its <c>elseif</c>s and <c>else</c>: what its conditions call the
    /// script's, and each block read as statements, so a function or a class declared in one - as one is where it is not
    /// yet - is declared.
    /// </summary>
    private int Conditional(int at, int end, ScanFrame frame)
    {
        while (true)
        {
            var open = _scan.Next(at);

            if (_scan.Is(open, "("))
            {
                var close = Math.Min(_scan.Bounds(open, end).Inner, end);
                Calls(_text, open + 1, close, null, new Locals(), null, depth: 0);
                at = close;
                open = _scan.Next(close);
            }

            if (!_scan.Is(open, "{"))
            {
                // A statement alone, or a block the alternative syntax writes: code of the script's, past its condition.
                return Code(Math.Min(at + 1, end), end);
            }

            var (inner, past) = _scan.Bounds(open, end);

            if (frame.Depth < ScanFrame.MostDepth)
            {
                ScanFrame.Walk(open + 1, inner, index => Statement(index, inner, frame.Deeper()));
            }
            else
            {
                _extract.Dynamic(_scan[open].Line, ScanFrame.TooDeepWhy);
            }

            if (IsWord(_scan, past, "elseif") || IsWord(_scan, past, "else"))
            {
                at = IsWord(_scan, past, "else") && IsWord(_scan, _scan.Next(past), "if") ? _scan.Next(past) : past;
                continue;
            }

            return past;
        }
    }

    /// <summary>Code at the top from <paramref name="start"/> to its statement's end: what it calls, the calls of the file's <c>(script)</c> node.</summary>
    private int Code(int start, int end)
    {
        var stop = _scan.StatementEnd(start, end);
        Calls(_text, start, stop, null, new Locals(), null, depth: 0);
        return stop;
    }

    /// <summary>The index of the name a <c>function</c> at <paramref name="at"/> declares - past a reference's <c>&amp;</c> - where its parameters follow; -1 where it declares none, a closure.</summary>
    private int FunctionName(int at)
    {
        var name = _scan.Next(at);

        if (_scan.Is(name, "&"))
        {
            name = _scan.Next(name);
        }

        return _scan.IsName(name) && _scan.Is(_scan.Next(name), "(") && _scan.Text(name)[0] != '$' ? name : -1;
    }

    // ---- Use and include ----

    /// <summary>
    /// A <c>use</c> declaration at <paramref name="at"/>: each class or namespace it names an import of it by the name it is
    /// reached by, which the file's references are written out from; each function's and constant's an import of its
    /// namespace showing that name - a group's (<c>use A\{B, C as D}</c>) each.
    /// </summary>
    private void Use(int at, int end)
    {
        var stop = _scan.StatementEnd(at, end);
        var boundary = Ending(stop);
        var line = _scan[at].Line;
        var first = _scan.Next(at);
        var kind = IsWord(_scan, first, "function") ? "function" : IsWord(_scan, first, "const") ? "const" : "class";
        var from = kind == "class" ? first : _scan.Next(first);

        foreach (var (pieceStart, pieceEnd) in ScanCalls.Split(_scan, from, boundary, null))
        {
            if (!_scan.IsName(pieceStart))
            {
                continue;
            }

            var written = _scan.Text(pieceStart).TrimStart('\\');
            var next = _scan.Next(pieceStart);

            // A group: the prefix, its separator, and each name in its braces.
            if (_scan.Is(next, "\\") && _scan.Is(_scan.Next(next), "{"))
            {
                var open = _scan.Next(next);
                var close = Math.Min(_scan.Bounds(open, pieceEnd).Inner, pieceEnd);

                foreach (var (itemStart, itemEnd) in ScanCalls.Split(_scan, open + 1, close, null))
                {
                    var item = itemStart;
                    var itemKind = kind;

                    if (IsWord(_scan, item, "function") || IsWord(_scan, item, "const"))
                    {
                        itemKind = _scan.Text(item).ToLowerInvariant();
                        item = _scan.Next(item);
                    }

                    if (_scan.IsName(item) && item < itemEnd)
                    {
                        Used(itemKind, $"{written}\\{_scan.Text(item)}", Alias(item, itemEnd), line);
                    }
                }

                continue;
            }

            Used(kind, written, Alias(pieceStart, pieceEnd), line);
        }
    }

    /// <summary>The name a <c>use</c>'s item at <paramref name="at"/> gives: the one after its <c>as</c>; <see langword="null"/> where it gives none, its own last part.</summary>
    private string? Alias(int at, int end)
        => _scan.Next(at) is var next && next < end && IsWord(_scan, next, "as") && _scan.IsName(_scan.Next(next)) && _scan.Next(next) < end ? _scan.Text(_scan.Next(next)) : null;

    /// <summary>One name a <c>use</c> declares, <paramref name="written"/> as PHP writes it: imported, and the name it is reached by written out to it.</summary>
    private void Used(string kind, string written, string? alias, int line)
    {
        var name = Dotted(written);
        var reached = alias ?? Simple(name);

        if (kind == "class")
        {
            _classes[reached] = name;
            _extract.Import(new ExtractImport(CodeImportForms.Scope, name, line) { Prefix = reached });
            return;
        }

        if (kind == "function")
        {
            _functions[reached] = name;
        }

        if (name.LastIndexOf('.') is var dot and > 0)
        {
            _extract.Import(new ExtractImport(CodeImportForms.Scope, name[..dot], line) { Show = [name[(dot + 1)..]] });
        }
    }

    /// <summary>
    /// The file an inclusion at <paramref name="at"/> names, where the code writes its path: a text, or <c>__DIR__</c> - or
    /// <c>dirname(__DIR__)</c>, <c>dirname(__DIR__, n)</c> - joined to one - an import of it, by a path from the file's
    /// directory; one the code computes marked dynamic. Answers where the inclusion ends.
    /// </summary>
    private int Included(ScannedText scan, int at, int to)
    {
        var line = scan[at].Line;
        var stop = scan.Next(at);

        // To its statement's end, or to what closes the expression it is in: include('x.php') holds its own parentheses.
        while (stop < to && stop < scan.Count && !(scan.EndsStatement(stop) || scan.Is(stop, ",") || scan.Is(stop, ")") || scan.Is(stop, "]") || scan.Is(stop, "}")))
        {
            stop = scan.Opens(stop) ? Math.Min(scan.Past(stop), to) : stop + 1;
        }

        var path = IncludedPath(scan, scan.Next(at), stop);

        if (path is null)
        {
            // What computes it is read as the code it is.
            _extract.Dynamic(line, "an inclusion of a file whose path the code computes");
            return scan.Next(at);
        }

        if (path.Length > 0 && _included.Add(path))
        {
            _extract.Import(new ExtractImport(CodeImportForms.Relative, path, line));
        }

        return stop;
    }

    /// <summary>The path the tokens from <paramref name="at"/> to <paramref name="stop"/> name from the file's directory, where the code writes it; empty where it is no file's; <see langword="null"/> where the code computes it.</summary>
    private static string? IncludedPath(ScannedText scan, int at, int stop)
    {
        var tokens = scan.Code(at, stop).ToList();

        // include('x.php'): its parentheses.
        if (tokens.Count >= 2 && scan.Is(tokens[0], "(") && scan.Match(tokens[0]) == tokens[^1])
        {
            tokens = tokens[1..^1];
        }

        // __DIR__ . 'x', dirname(__DIR__) . 'x', dirname(__DIR__, n) . 'x': the file's directory, or one n above it, joined to a text.
        var climbs = -1;
        var rest = 0;

        if (tokens.Count >= 3 && scan.Is(tokens[0], "__DIR__") && scan.Is(tokens[1], "."))
        {
            climbs = 0;
            rest = 2;
        }
        else if (tokens.Count >= 6 && IsWord(scan, tokens[0], "dirname") && scan.Is(tokens[1], "(") && scan.Is(tokens[2], "__DIR__"))
        {
            var closeAt = tokens.IndexOf(scan.Match(tokens[1]));

            if (closeAt == 3)
            {
                climbs = 1;
            }
            else if (closeAt == 5 && scan.Is(tokens[3], ",") && int.TryParse(scan.Text(tokens[4]), out var levels) && levels is > 0 and < 32)
            {
                climbs = levels;
            }

            if (climbs < 0 || closeAt + 2 >= tokens.Count || !scan.Is(tokens[closeAt + 1], "."))
            {
                return null;
            }

            rest = closeAt + 2;
        }

        if (tokens.Count != rest + 1 || scan[tokens[rest]].Kind != ScanTokenKind.Text || Unquoted(scan.Text(tokens[rest])) is not { } written)
        {
            return null;
        }

        var joined = climbs < 0 ? written : string.Concat(Enumerable.Repeat("../", climbs)) + written.TrimStart('/', '\\');
        return joined.Contains("://", StringComparison.Ordinal) || joined.StartsWith('/') ? string.Empty : joined;
    }

    /// <summary><paramref name="text"/>, a string, without its quotes; <see langword="null"/> where it is left open, holds an escape or an interpolation, or is a heredoc.</summary>
    private static string? Unquoted(string text)
        => text.Length >= 2 && text[0] is '\'' or '"' && text[^1] == text[0] && !text.Contains('\\', StringComparison.Ordinal) && !(text[0] == '"' && text.Contains('$', StringComparison.Ordinal))
            ? text[1..^1]
            : null;

    // ---- Attributes ----

    /// <summary>Past the attributes at <paramref name="at"/> - <c>#[A]</c>, <c>#[A(...), B]</c> - each named among <paramref name="marks"/> as <c>#[A]</c>.</summary>
    private int Attributes(int at, int end, List<string>? marks)
    {
        while (at < end && _scan.Is(at, "#") && _scan.Is(_scan.Next(at), "[") && _scan[_scan.Next(at)].Start == _scan[at].End)
        {
            var open = _scan.Next(at);
            var (inner, past) = _scan.Bounds(open, end);

            foreach (var (pieceStart, _) in ScanCalls.Split(_scan, open + 1, inner, null))
            {
                if (_scan.IsName(pieceStart))
                {
                    marks?.Add($"#[{Simple(Dotted(_scan.Text(pieceStart).TrimStart('\\')))}]");
                }
            }

            at = past;
        }

        return at;
    }

    // ---- Types ----

    /// <summary>
    /// A class, an interface, a trait or an enum named at <paramref name="nameAt"/>: a type node over its head and body, an
    /// open scope of its own, its clauses' edges, and its members read in its scope.
    /// </summary>
    private int Type(int start, int head, int nameAt, int end, ScanFrame frame, string subKind, List<string> modifiers, List<string> marks)
    {
        var name = _scan.Text(nameAt);

        if (name[0] == '$' || name.Contains('\\', StringComparison.Ordinal))
        {
            return _scan.Next(nameAt);
        }

        var open = BodyOpen(_scan.Next(nameAt), end);

        if (open < 0)
        {
            return _scan.StatementEnd(head, end);
        }

        var (inner, close) = _scan.Bounds(open, end);
        var qualified = frame.Qualify(name);
        var key = _extract.Add(new ExtractNode(Id(qualified), CodeNodeKinds.Type, name, qualified, _scan[start].Line, _scan[Math.Min(inner, _scan.Count - 1)].EndLine, _scan.Signature(head, open))
        {
            SubKind = subKind,
            Scope = frame.Scope,
            Parent = frame.Parent,
            BodyStart = _scan[open].Line,
            Summary = ScanComments.Summary(_scan, start),
            Modifiers = [.. modifiers, .. marks],
        });

        _extract.Scope(qualified, open: true, parent: frame.Scope);
        var testClass = Clauses(key, _scan.Next(nameAt), open, subKind) || (subKind == "class" && name.EndsWith("Test", StringComparison.Ordinal) && HasExtends(_scan.Next(nameAt), open));

        Members(open, inner, frame.Inside(qualified, qualified, key, name), subKind, testClass);
        return close;
    }

    /// <summary>Whether a type's clauses from <paramref name="at"/> to <paramref name="end"/> extend a class.</summary>
    private bool HasExtends(int at, int end) => _scan.Code(at, end).Any(index => IsWord(_scan, index, "extends"));

    /// <summary>
    /// A type's clauses from <paramref name="at"/> to <paramref name="end"/>: a class's <c>extends</c> an <c>inherits</c>
    /// edge, an interface's an <c>inherits</c> edge each, <c>implements</c> an <c>implements</c> edge each; answers
    /// whether the class extends a <c>TestCase</c>, so its tests are its methods.
    /// </summary>
    private bool Clauses(string key, int at, int end, string subKind)
    {
        string? kind = null;
        var test = false;

        for (; at < end; at = _scan.Next(at))
        {
            if (IsWord(_scan, at, "extends"))
            {
                kind = CodeEdgeKinds.Inherits;
            }
            else if (IsWord(_scan, at, "implements"))
            {
                kind = CodeEdgeKinds.Implements;
            }
            else if (_scan.Is(at, ":"))
            {
                // An enum's backing type.
                kind = null;
            }
            else if (kind is not null && _scan.IsName(at) && ClassName(_scan.Text(at)) is { } named)
            {
                Mention(key, kind, named, _scan[at].Line);
                test |= kind == CodeEdgeKinds.Inherits && subKind == "class" && Simple(named).EndsWith("TestCase", StringComparison.Ordinal);
            }
        }

        return test;
    }

    /// <summary>The index of the <c>{</c> that opens a type's body from <paramref name="at"/>, past its clauses; -1 where a <c>;</c> or a closer comes first.</summary>
    private int BodyOpen(int at, int end)
    {
        for (; at < end && at < _scan.Count; at++)
        {
            if (_scan.Is(at, "{"))
            {
                return at;
            }

            if (_scan.EndsStatement(at) || _scan.Is(at, "}") || _scan.Is(at, "(") || _scan.Is(at, "["))
            {
                return -1;
            }
        }

        return -1;
    }

    // ---- Members ----

    /// <summary>The members of the body <paramref name="open"/> opens, read in <paramref name="frame"/>, the type's own.</summary>
    private void Members(int open, int inner, ScanFrame frame, string subKind, bool testClass)
    {
        if (frame.TooDeep)
        {
            _extract.Dynamic(_scan[open].Line, ScanFrame.TooDeepWhy);
            return;
        }

        ScanFrame.Walk(open + 1, inner, next => Member(next, inner, frame, subKind, testClass));
    }

    /// <summary>A member at <paramref name="at"/>: a trait's use, an enum's case, constants, a method, or properties; answers where the next begins.</summary>
    private int Member(int at, int end, ScanFrame frame, string subKind, bool testClass)
    {
        if (_scan.EndsStatement(at))
        {
            return at + 1;
        }

        if (IsWord(_scan, at, "use") && _scan.IsName(_scan.Next(at)))
        {
            return Traits(at, end, frame);
        }

        var start = at;
        var marks = new List<string>();
        at = Attributes(at, end, marks);

        if (IsWord(_scan, at, "case") && _scan.IsName(_scan.Next(at)))
        {
            return Case(start, at, end, frame, marks);
        }

        var head = at;
        var modifiers = new List<string>();

        while (Modifier(at, MemberModifiers, modifiers) is var past and >= 0)
        {
            at = past;
        }

        if (IsWord(_scan, at, "const"))
        {
            return Constants(start, head, _scan.Next(at), end, frame, [.. modifiers, .. marks]);
        }

        if (IsWord(_scan, at, "function") && FunctionName(at) is var nameAt and >= 0)
        {
            if (subKind == "interface" && !modifiers.Contains("abstract", StringComparer.Ordinal))
            {
                modifiers.Add("abstract");
            }

            return Callable(start, head, nameAt, end, frame, marks, modifiers, _scan.Text(nameAt).Equals("__construct", StringComparison.OrdinalIgnoreCase) ? "constructor" : "method", testNames: testClass, testMarks: testClass || subKind == "trait");
        }

        return modifiers.Count > 0 || _scan.IsName(at) ? Properties(start, head, at, end, frame, modifiers, marks) : _scan.StatementEnd(head, end);
    }

    /// <summary>
    /// Past the modifier at <paramref name="at"/>, one of <paramref name="allowed"/>, added to <paramref name="modifiers"/> -
    /// a visibility of setting PHP 8.4 writes beside a property's (<c>private(set)</c>) one with its <c>(set)</c>; -1 where
    /// none is there.
    /// </summary>
    private int Modifier(int at, HashSet<string> allowed, List<string> modifiers)
    {
        if (!_scan.IsName(at) || !allowed.Contains(_scan.Text(at)))
        {
            return -1;
        }

        var word = _scan.Text(at).ToLowerInvariant();
        var next = _scan.Next(at);

        if (!_scan.Is(next, "("))
        {
            modifiers.Add(word);
            return next;
        }

        var set = _scan.Next(next);

        if (word is "public" or "protected" or "private" && IsWord(_scan, set, "set") && _scan.Is(_scan.Next(set), ")"))
        {
            modifiers.Add($"{word}(set)");
            return _scan.Next(_scan.Next(set));
        }

        return -1;
    }

    /// <summary>A trait's use in a type at <paramref name="at"/>: a <c>mixesIn</c> edge to each trait it names, what its block adapts passed over.</summary>
    private int Traits(int at, int end, ScanFrame frame)
    {
        var stop = TopLevelOf(at, end);

        for (var index = _scan.Next(at); index < stop; index = _scan.Next(index))
        {
            if (_scan.Is(index, "{"))
            {
                break;
            }

            if (_scan.IsName(index) && ClassName(_scan.Text(index)) is { } trait)
            {
                Mention(frame.Parent!, CodeEdgeKinds.MixesIn, trait, _scan[index].Line);
            }
        }

        return _scan.StatementEnd(at, end);
    }

    /// <summary>Where a trait's use from <paramref name="at"/> ends its names: at its <c>;</c>, or at the brace of its adaptations.</summary>
    private int TopLevelOf(int at, int end)
    {
        for (var index = at; index < end && index < _scan.Count; index++)
        {
            if (_scan.Is(index, ";") || _scan.Is(index, "{") || _scan.Is(index, "}"))
            {
                return index;
            }
        }

        return end;
    }

    /// <summary>An enum's case at <paramref name="at"/>: a value of the enum's type, named as PHP writes it.</summary>
    private int Case(int start, int at, int end, ScanFrame frame, List<string> marks)
    {
        var stop = _scan.StatementEnd(at, end);
        var nameAt = _scan.Next(at);
        var name = _scan.Text(nameAt);
        var qualified = frame.Qualify(name);

        _extract.Add(new ExtractNode(Id(qualified), CodeNodeKinds.Member, name, qualified, _scan[start].Line, _scan[_scan.LastOf(stop)].EndLine, _scan.Signature(at, Ending(stop)))
        {
            SubKind = "case",
            Scope = frame.Scope,
            Parent = frame.Parent,
            Summary = ScanComments.Summary(_scan, start),
            Modifiers = marks,
            ValueType = frame.Prefix,
        });

        return stop;
    }

    /// <summary>
    /// The constants from <paramref name="at"/>, past <c>const</c> - its type where it writes one - to their <c>;</c>: each a
    /// value of the scope they are in, a type's or a namespace's.
    /// </summary>
    private int Constants(int start, int head, int at, int end, ScanFrame frame, List<string> modifiers)
    {
        var stop = _scan.StatementEnd(at, end);

        foreach (var (pieceStart, pieceEnd) in ScanCalls.Split(_scan, at, Ending(stop), null))
        {
            var assign = ScanCalls.TopLevel(_scan, pieceStart, pieceEnd, "=", null);
            var nameAt = _scan.Previous(Math.Min(assign, pieceEnd));

            if (nameAt < pieceStart || !_scan.IsName(nameAt) || _scan.Text(nameAt)[0] == '$')
            {
                continue;
            }

            var name = _scan.Text(nameAt);
            var qualified = frame.Qualify(name);

            _extract.Add(new ExtractNode(Id(qualified), CodeNodeKinds.Member, name, qualified, _scan[pieceStart == at ? start : pieceStart].Line, _scan[_scan.LastOf(pieceEnd)].EndLine, _scan.Signature(pieceStart == at ? head : pieceStart, pieceEnd))
            {
                SubKind = "constant",
                Scope = frame.Scope,
                Parent = frame.Parent,
                Summary = pieceStart == at ? ScanComments.Summary(_scan, start) : null,
                Modifiers = modifiers,
            });
        }

        return stop;
    }

    /// <summary>
    /// The properties from <paramref name="at"/>, past their modifiers - their type where they write one - to their
    /// <c>;</c>, or a hook's block: each named by its variable, its value's type the one it is declared with; what its
    /// hooks call the calls of the property they are written on.
    /// </summary>
    private int Properties(int start, int head, int at, int end, ScanFrame frame, List<string> modifiers, List<string> marks)
    {
        var (stop, hooks) = PropertyEnd(at, end);
        var through = hooks >= 0 ? hooks : Ending(stop);
        var typeStart = at;
        var first = true;
        string? last = null;

        foreach (var (pieceStart, pieceEnd) in ScanCalls.Split(_scan, at, through, null))
        {
            var variable = _scan.Code(pieceStart, pieceEnd).FirstOrDefault(index => IsVariable(_scan, index), -1);

            if (variable < 0)
            {
                continue;
            }

            var type = first ? TypeKey(typeStart, variable, frame) : null;
            var name = _scan.Text(variable);
            var qualified = frame.Qualify(name);
            var hooked = hooks >= 0 && pieceEnd == through;
            var key = _extract.Add(new ExtractNode(Id(qualified), CodeNodeKinds.Member, name, qualified, _scan[first ? start : pieceStart].Line, _scan[_scan.LastOf(hooked ? stop : pieceEnd)].EndLine, _scan.Signature(first ? head : pieceStart, pieceEnd))
            {
                SubKind = "property",
                Scope = frame.Scope,
                Parent = frame.Parent,
                Summary = first ? ScanComments.Summary(_scan, start) : null,
                Modifiers = [.. modifiers, .. marks],
                ValueType = type,
            });

            if (first)
            {
                TypeReferences(key, typeStart, variable, frame);
                first = false;
            }

            last = hooked ? key : null;
        }

        if (last is { } hookedKey)
        {
            Hooks(hooks, stop, frame, hookedKey);
        }

        return stop;
    }

    /// <summary>
    /// Where properties from <paramref name="at"/> end: past their <c>;</c>, or past the hooks' block PHP 8.4 writes after
    /// one, whose <c>{</c> it answers too; -1 where they have none.
    /// </summary>
    private (int Stop, int Hooks) PropertyEnd(int at, int end)
    {
        for (var index = at; index < end && index < _scan.Count; index++)
        {
            if (_scan.EndsStatement(index))
            {
                return (index + 1, -1);
            }

            if (_scan.Is(index, "}"))
            {
                return (index, -1);
            }

            if (_scan.Is(index, "{"))
            {
                return (_scan.Bounds(index, end).Past, index);
            }

            if (_scan.Is(index, "(") || _scan.Is(index, "["))
            {
                index = _scan.Bounds(index, end).Past - 1;
            }
        }

        return (end, -1);
    }

    /// <summary>
    /// The hooks in the block <paramref name="open"/> opens, before <paramref name="past"/> - <c>get =&gt; ...;</c>,
    /// <c>set(string $value) { ... }</c> - each one's arrow or body read as the calls of the property of
    /// <paramref name="key"/>.
    /// </summary>
    private void Hooks(int open, int past, ScanFrame frame, string key)
    {
        var inner = _scan.Bounds(open, past).Inner;
        ScanFrame.Walk(open + 1, inner, at => Hook(at, inner, frame, key));
    }

    /// <summary>The hook at <paramref name="at"/>, past its attributes, its <c>final</c> and its <c>&amp;</c>: what its arrow or its body calls; answers where the next begins.</summary>
    private int Hook(int at, int end, ScanFrame frame, string key)
    {
        at = Attributes(at, end, null);

        while (at < end && ((_scan.IsName(at) && MemberModifiers.Contains(_scan.Text(at))) || _scan.Is(at, "&")))
        {
            at = _scan.Next(at);
        }

        var next = _scan.Next(at);
        var locals = new Locals();

        if (_scan.IsName(at) && _scan.Is(next, "(") && _scan.Match(next) is var close and > 0 && close < end)
        {
            locals = new Locals(ParametersOf(next, close, frame));
            next = _scan.Next(close);
        }

        if (_scan.IsName(at) && _scan.Is(next, "=>"))
        {
            var stop = _scan.StatementEnd(next, end);
            Calls(_text, next + 1, Ending(stop), key, locals, frame.Prefix, depth: 0);
            return stop;
        }

        if (_scan.IsName(at) && _scan.Is(next, "{"))
        {
            var (inner, past) = _scan.Bounds(next, end);
            Calls(_text, next + 1, inner, key, locals, frame.Prefix, depth: 0);
            return past;
        }

        return _scan.StatementEnd(at, end);
    }

    /// <summary>
    /// A function, a method or a constructor named at <paramref name="nameAt"/>: a member node over its head and body - a
    /// prototype where it has none - its promoted parameters a constructor's properties, what its signature names, the
    /// member it overrides where it is marked so, and what its body calls.
    /// </summary>
    private int Callable(int start, int head, int nameAt, int end, ScanFrame frame, List<string> marks, List<string> modifiers, string subKind, bool testNames, bool testMarks)
    {
        var open = _scan.Next(nameAt);
        var close = _scan.Match(open);

        if (close < 0 || close >= end)
        {
            return _scan.StatementEnd(head, end);
        }

        var name = _scan.Text(nameAt);
        var parameters = ParametersOf(open, close, frame);
        var at = _scan.Next(close);
        var (returnStart, returnEnd) = (-1, -1);

        if (_scan.Is(at, ":"))
        {
            returnStart = _scan.Next(at);
            returnEnd = returnStart;

            while (returnEnd < end && !_scan.Is(returnEnd, "{") && !_scan.EndsStatement(returnEnd) && !_scan.Is(returnEnd, "}"))
            {
                returnEnd = _scan.Opens(returnEnd) ? Math.Min(_scan.Past(returnEnd), end) : returnEnd + 1;
            }

            at = returnEnd;
        }

        int body;
        int last;

        if (_scan.Is(at, "{") && at < end)
        {
            body = at;
            last = Math.Min(_scan.Bounds(at, end).Inner, _scan.Count - 1);
        }
        else if (_scan.EndsStatement(at))
        {
            body = -1;
            last = at;
        }
        else
        {
            return _scan.StatementEnd(head, end);
        }

        var qualified = frame.Qualify(name);
        var test = (testNames && subKind == "method" && name.StartsWith("test", StringComparison.OrdinalIgnoreCase) && !modifiers.Contains("private", StringComparer.Ordinal) && !modifiers.Contains("protected", StringComparer.Ordinal))
            || (testMarks && subKind == "method" && (marks.Contains("#[Test]", StringComparer.Ordinal) || DocTest(start)));

        var key = _extract.Add(new ExtractNode(Id(qualified), CodeNodeKinds.Member, name, qualified, _scan[start].Line, _scan[last].EndLine, _scan.Signature(head, body >= 0 ? body : last))
        {
            SubKind = subKind,
            Scope = frame.Scope,
            Parent = frame.Parent,
            BodyStart = body >= 0 ? _scan[body].Line : null,
            Summary = ScanComments.Summary(_scan, start),
            Modifiers = [.. modifiers.Distinct(StringComparer.Ordinal), .. marks],
            MinArity = parameters.Least,
            MaxArity = parameters.Most,
            Prototype = body < 0,
            Test = test,
        });

        if (returnStart >= 0)
        {
            TypeReferences(key, returnStart, returnEnd, frame);
        }

        foreach (var (spanStart, spanEnd) in parameters.TypeSpans)
        {
            TypeReferences(key, spanStart, spanEnd, frame);
        }

        foreach (var promoted in parameters.Promoted)
        {
            Promoted(promoted, frame);
        }

        if (marks.Contains("#[Override]", StringComparer.Ordinal))
        {
            _extract.Reference(key, CodeEdgeKinds.Overrides, name, _scan[nameAt].Line, qualifier: new ExtractQualifier(CodeQualifierKinds.Base));
        }

        if (body >= 0)
        {
            Calls(_text, body + 1, last, key, new Locals(parameters), frame.Owner is null ? null : frame.Prefix, depth: 0);
        }

        return last + 1;
    }

    /// <summary>Whether the comments above the declaration at <paramref name="at"/> mark it <c>@test</c>.</summary>
    private bool DocTest(int at)
    {
        foreach (var comment in _scan.CommentsAbove(at))
        {
            var text = _scan.Source.AsSpan(comment.Start, comment.Length);

            // @test alone, past any @testdox or @testWith before it.
            for (var mark = text.IndexOf("@test", StringComparison.Ordinal); mark >= 0; mark = text[(mark + 5)..].IndexOf("@test", StringComparison.Ordinal) is var next and >= 0 ? mark + 5 + next : -1)
            {
                if (mark + 5 == text.Length || !char.IsLetterOrDigit(text[mark + 5]))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>A constructor's promoted parameter: a property of the type, named by its variable, its value's type the parameter's.</summary>
    private void Promoted(PromotedParameter parameter, ScanFrame frame)
    {
        var name = _scan.Text(parameter.Name);
        var qualified = frame.Qualify(name);

        _extract.Add(new ExtractNode(Id(qualified), CodeNodeKinds.Member, name, qualified, _scan[parameter.Start].Line, _scan[_scan.LastOf(parameter.End)].EndLine, _scan.Signature(parameter.Start, parameter.End))
        {
            SubKind = "property",
            Scope = frame.Scope,
            Parent = frame.Parent,
            Modifiers = parameter.Modifiers,
            ValueType = parameter.Type,
        });
    }

    // ---- Parameters ----

    /// <summary>A constructor's parameter promoted to a property: its span, its variable, its modifiers and its type.</summary>
    private sealed record PromotedParameter(int Start, int End, int Name, IReadOnlyList<string> Modifiers, string? Type);

    /// <summary>A callable's parameters: how many a call gives - those with no default and no spread the fewest, none the most where one spreads - the spans of their types, the type each binds, and those promoted.</summary>
    private sealed record Parameters(int? Least, int? Most, IReadOnlyList<(int Start, int End)> TypeSpans, IReadOnlyDictionary<string, string?> Locals, IReadOnlyList<PromotedParameter> Promoted);

    /// <summary>The parameters between the parentheses at <paramref name="open"/> and <paramref name="close"/>: each with its attributes, its modifiers, its type, a reference's <c>&amp;</c>, a spread's <c>...</c> and its default.</summary>
    private Parameters ParametersOf(int open, int close, ScanFrame frame)
    {
        var least = 0;
        var most = 0;
        var spread = false;
        var spans = new List<(int, int)>();
        var locals = new Dictionary<string, string?>(StringComparer.Ordinal);
        var promoted = new List<PromotedParameter>();

        foreach (var (start, end) in ScanCalls.Split(_scan, open + 1, close, null))
        {
            var at = Attributes(start, end, null);
            var modifiers = new List<string>();

            while (at < end && Modifier(at, Visibilities, modifiers) is var past and >= 0)
            {
                at = past;
            }

            var variable = _scan.Code(at, end).FirstOrDefault(index => IsVariable(_scan, index), -1);

            if (variable < 0)
            {
                continue;
            }

            var typeEnd = variable;
            var spreads = false;

            while (_scan.Previous(typeEnd) is var before && before >= at && (_scan.Is(before, "...") || _scan.Is(before, "&")))
            {
                spreads |= _scan.Is(before, "...");
                typeEnd = before;
            }

            var defaulted = ScanCalls.TopLevel(_scan, variable, end, "=", null) < end;
            most++;
            least += defaulted || spreads ? 0 : 1;
            spread |= spreads;

            var type = TypeKey(at, typeEnd, frame);
            spans.Add((at, typeEnd));
            locals[_scan.Text(variable)] = type;

            if (modifiers.Count > 0)
            {
                promoted.Add(new PromotedParameter(start, end, variable, modifiers, type));
            }
        }

        return new Parameters(least, spread ? null : most, spans, locals, promoted);
    }

    // ---- Types written in a signature ----

    /// <summary>
    /// The class the type from <paramref name="start"/> to <paramref name="end"/> is, as a type qualifier names it, where it
    /// is one class alone - nullable or with <c>null</c> - <c>self</c> and <c>static</c> the type it is written in;
    /// <see langword="null"/> where it is none, a union of classes, or one of PHP's own types.
    /// </summary>
    private string? TypeKey(int start, int end, ScanFrame frame)
    {
        string? found = null;

        foreach (var index in _scan.Code(start, end))
        {
            if (_scan.Is(index, "?") || _scan.Is(index, "|") || _scan.Is(index, "(") || _scan.Is(index, ")"))
            {
                continue;
            }

            if (!_scan.IsName(index) || found is not null && !IsWord(_scan, index, "null"))
            {
                return null;
            }

            if (IsWord(_scan, index, "null"))
            {
                continue;
            }

            found = IsWord(_scan, index, "self") || IsWord(_scan, index, "static") ? (frame.Owner is null ? null : frame.Prefix ?? string.Empty)
                : Builtins.Contains(_scan.Text(index)) ? null
                : ClassName(_scan.Text(index));

            if (found is null)
            {
                return null;
            }
        }

        return string.IsNullOrEmpty(found) ? null : found;
    }

    /// <summary>A <c>references</c> edge from the node of <paramref name="key"/> to each class the type from <paramref name="start"/> to <paramref name="end"/> names that is none of PHP's own.</summary>
    private void TypeReferences(string key, int start, int end, ScanFrame frame)
    {
        foreach (var index in _scan.Code(start, end))
        {
            if (_scan.IsName(index) && !IsVariable(_scan, index) && !Builtins.Contains(_scan.Text(index)) && ClassName(_scan.Text(index)) is { } named)
            {
                Mention(key, CodeEdgeKinds.References, named, _scan[index].Line);
            }
        }
    }

    // ---- What a body calls ----

    /// <summary>The variables a body binds, each with the class it is of where the code says: a parameter's type, a <c>new</c>'s class, a caught exception's.</summary>
    private sealed class Locals
    {
        public Locals()
        {
        }

        public Locals(Parameters parameters)
        {
            foreach (var (name, type) in parameters.Locals)
            {
                Types[name] = type;
            }
        }

        public Dictionary<string, string?> Types { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>
    /// Each call the tokens of <paramref name="text"/> from <paramref name="from"/> to <paramref name="to"/> make, from the
    /// node of <paramref name="key"/> - the file's <c>(script)</c> node where none is given: a reference by its name and
    /// arity, written out to what it names and qualified by its receiver where the code says, and what it cannot bind marked
    /// dynamic. A string's interpolations are read as text of their own, Pest's test calls as nodes of their own.
    /// <paramref name="owner"/> is the type the body is written in, as the model names it.
    /// </summary>
    private void Calls(InterpolatedText text, int from, int to, string? key, Locals locals, string? owner, int depth)
    {
        var scan = text.Scan;

        // Where the test calls read are no longer too deep to be: past the first that was.
        var shallowFrom = -1;

        string From() => key ?? ScriptKey();

        for (var at = from; at < to && at < scan.Count; at++)
        {
            var token = scan[at];

            if (token.Kind == ScanTokenKind.Text)
            {
                if (text.Interpolations.TryGetValue(token.Start, out var spans) && ScanFrame.ReadsDeeper(_extract, depth, token.Line, Nesting))
                {
                    foreach (var (spanStart, spanEnd) in spans)
                    {
                        var inner = PhpLexer.Scan(scan.Source, spanStart, spanEnd, text.Lines);
                        Calls(inner, 0, inner.Scan.Count, key, locals, owner, depth + 1);
                    }
                }

                continue;
            }

            if (token.Kind != ScanTokenKind.Name)
            {
                // A closure's or an anonymous class's attributes: kept on what they mark, no code of the body's.
                if (scan.Is(at, "#") && scan.Next(at) is var bracket && scan.Is(bracket, "[") && scan[bracket].Start == token.End)
                {
                    at = Math.Min(scan.Past(bracket), to) - 1;
                    continue;
                }

                if (scan.Is(at, "(") && Computed(scan, at) is { } computed)
                {
                    _extract.Dynamic(token.Line, computed);
                }

                if (scan.Is(at, "(") && scan.Match(at) is var close and > 0 && (IsWord(scan, scan.Previous(at), "function") || IsWord(scan, scan.Previous(at), "fn")))
                {
                    // A closure's or an arrow function's parameters: what it calls is the body's own.
                    foreach (var (name, type) in ClosureParameters(scan, at, close))
                    {
                        locals.Types[name] = type;
                    }
                }

                continue;
            }

            var word = scan.Text(at);
            var line = token.Line;
            var next = scan.Next(at);
            var previous = scan.Previous(at);

            // $q->catch(...), Factory::new(), Blade::include(...): a member's name, no keyword; function new(), const include: a name declared.
            var member = scan.Is(previous, "->") || scan.Is(previous, "?->") || scan.Is(previous, "::");
            var keyword = scan.Is(previous, "&") ? scan.Previous(previous) : previous;
            var declared = (IsWord(scan, keyword, "function") || (keyword == previous && IsWord(scan, keyword, "const")))
                && scan.Previous(keyword) is var access && !(scan.Is(access, "->") || scan.Is(access, "?->") || scan.Is(access, "::"));

            if (declared)
            {
                continue;
            }

            // build(include: true): a named argument, no inclusion.
            if (!member && Includes.Contains(word) && !scan.Is(next, ":"))
            {
                at = Included(scan, at, to) - 1;
                continue;
            }

            if (!member && IsWord(scan, at, "new"))
            {
                at = Instantiated(scan, at, From(), locals, owner) - 1;
                continue;
            }

            if (!member && IsWord(scan, at, "catch") && scan.Is(next, "(") && scan.Match(next) is var caught and > 0)
            {
                Caught(scan, next, caught, From(), locals);
                at = caught;
                continue;
            }

            if (IsWord(scan, at, "instanceof") && scan.IsName(next) && !IsVariable(scan, next) && !Builtins.Contains(scan.Text(next)) && ClassName(scan.Text(next)) is { } tested)
            {
                Mention(From(), CodeEdgeKinds.References, tested, line);
                at = next;
                continue;
            }

            // $v = new X(...): the variable is of that class.
            if (IsVariable(scan, at) && scan.Is(next, "=") && IsWord(scan, scan.Next(next), "new") && Made(scan, scan.Next(next), owner) is { } made)
            {
                locals.Types[word] = made;
            }

            if (!scan.Is(next, "("))
            {
                // A class's name before ::class or a constant: what the code names of it.
                if (scan.Is(next, "::") && scan.IsName(at) && !IsVariable(scan, at) && !NoCalls.Contains(word) && ClassName(word) is { } named && IsWord(scan, scan.Next(next), "class"))
                {
                    Mention(From(), CodeEdgeKinds.References, named, line);
                }

                continue;
            }

            if (member)
            {
                MemberCall(scan, at, previous, From(), locals, owner, line);
                continue;
            }

            if (IsVariable(scan, at))
            {
                _extract.Dynamic(line, $"a call through the function value {word}");
                continue;
            }

            if (IsWord(scan, at, "eval"))
            {
                _extract.Dynamic(line, "a call of eval");
                continue;
            }

            if (word.Equals("call_user_func", StringComparison.OrdinalIgnoreCase) || word.Equals("call_user_func_array", StringComparison.OrdinalIgnoreCase))
            {
                _extract.Dynamic(line, $"a call through {word.ToLowerInvariant()}");
                continue;
            }

            if (ScanCalls.At(scan, at, PhpCalls) is not { } call)
            {
                continue;
            }

            // A test call nested too deep is marked once, those it holds read as its holder's calls.
            if (_testFile && TestCalls.Contains(word) && at > shallowFrom && (key is null || _extract.Node(key).Test))
            {
                if (!ScanFrame.ReadsDeeper(_extract, depth, line, Nesting))
                {
                    shallowFrom = call.Close;
                }
                else if (Test(text, at, call, key, locals, owner, depth) is var testEnd and > 0)
                {
                    at = testEnd;
                    continue;
                }
            }

            Function(From(), word, line, call.Arity);
        }
    }

    /// <summary>
    /// Why the parenthesis at <paramref name="open"/> calls a value the code computes - one an element holds
    /// (<c>$a['k']()</c>), a member it names in braces (<c>$o-&gt;{$n}()</c>), what a call or a parenthesis gives
    /// (<c>f()()</c>, <c>($f)()</c>); <see langword="null"/> where it opens no such call: a condition's, a construct's, a
    /// cast's, or a closure's called where it is written, whose calls are read where they stand.
    /// </summary>
    private static string? Computed(ScannedText scan, int open)
    {
        var before = scan.Previous(open);

        if (scan.Is(before, "]") && !IsAttributeEnd(scan, before))
        {
            return "a call of a value an element holds";
        }

        if (scan.Is(before, "}") && scan.Match(before) is var brace and >= 0 && scan.Previous(brace) is var arrow && (scan.Is(arrow, "->") || scan.Is(arrow, "?->") || scan.Is(arrow, "::")))
        {
            return "a call of a member the code names in braces";
        }

        if (!scan.Is(before, ")") || scan.Match(before) is not (var group and >= 0))
        {
            return null;
        }

        var head = scan.Previous(group);
        var first = scan.Next(group);

        if (scan.IsName(head) && !IsVariable(scan, head) && Headed.Contains(scan.Text(head)))
        {
            return null;
        }

        // f()(), $f()(), $o->m()(): what a call gives; else a parenthesis's value, but a closure written in it, or a cast.
        var called = scan.Is(head, ")") || (scan.Is(head, "]") && !IsAttributeEnd(scan, head)) || IsVariable(scan, head) || (scan.IsName(head) && !NoCalls.Contains(scan.Text(head)));

        if (!called && (IsWord(scan, first, "function") || IsWord(scan, first, "fn") || IsWord(scan, first, "static") || (scan.Next(first) == before && Casts.Contains(scan.Text(first)))))
        {
            return null;
        }

        return "a call of the value a call or a parenthesis gives";
    }

    /// <summary>Whether the <c>]</c> at <paramref name="close"/> closes an attribute's <c>#[</c>: <c>#[Pure] (fn () =&gt; 1)()</c>.</summary>
    private static bool IsAttributeEnd(ScannedText scan, int close)
        => scan.Match(close) is var open and >= 0 && scan.Previous(open) is var hash && scan.Is(hash, "#") && scan[hash].End == scan[open].Start;

    /// <summary>A call of the function <paramref name="written"/> from the node of <paramref name="key"/>: written out to the function it names, by its namespace, the file's <c>use function</c>s and the global namespace.</summary>
    private void Function(string key, string written, int line, int? arity)
    {
        var name = written.TrimStart('\\');

        // \f, \A\f: the name itself; A\f: its first part a use's, else the namespace's; f: a use's, else the namespace's, then the global one.
        string? named;

        if (written.StartsWith('\\'))
        {
            named = Dotted(name);
        }
        else if (name.Contains('\\', StringComparison.Ordinal))
        {
            named = ClassName(name);
        }
        else if (_functions.TryGetValue(name, out var used))
        {
            named = used;
        }
        else
        {
            _extract.Reference(key, CodeEdgeKinds.Calls, name, line, arity, new ExtractQualifier(CodeQualifierKinds.Scope, _namespace));
            return;
        }

        if (named is null)
        {
            return;
        }

        var dot = named.LastIndexOf('.');
        _extract.Reference(key, CodeEdgeKinds.Calls, named[(dot + 1)..], line, arity, dot > 0 ? new ExtractQualifier(CodeQualifierKinds.Type, named[..dot]) : new ExtractQualifier(CodeQualifierKinds.Scope));
    }

    /// <summary>
    /// A call of the member named at <paramref name="at"/> on what stands before the <c>-&gt;</c>, <c>?-&gt;</c> or
    /// <c>::</c> at <paramref name="accessor"/>: a reference qualified by that receiver; one the code names by a variable, or
    /// of a class the code computes, marked dynamic.
    /// </summary>
    private void MemberCall(ScannedText scan, int at, int accessor, string key, Locals locals, string? owner, int line)
    {
        var word = scan.Text(at);
        var close = scan.CloseOf(scan.Next(at));
        var arity = ScanCalls.CallArity(scan, scan.Next(at), close, PhpMemberCalls);

        if (IsVariable(scan, at))
        {
            _extract.Dynamic(line, $"a call of a method the code names by the variable {word}");
            return;
        }

        var before = scan.Previous(accessor);

        if (scan.Is(accessor, "::"))
        {
            if (IsWord(scan, before, "self") || IsWord(scan, before, "static") || IsWord(scan, before, "$this"))
            {
                _extract.Reference(key, CodeEdgeKinds.Calls, word, line, arity, new ExtractQualifier(CodeQualifierKinds.This));
            }
            else if (IsWord(scan, before, "parent"))
            {
                _extract.Reference(key, CodeEdgeKinds.Calls, word, line, arity, new ExtractQualifier(CodeQualifierKinds.Base));
            }
            else if (IsVariable(scan, before) && !scan.Is(scan.Previous(before), "->") && !scan.Is(scan.Previous(before), "?->") && !scan.Is(scan.Previous(before), "::"))
            {
                _extract.Dynamic(line, $"a static call on the class the code names by {scan.Text(before)}");
            }
            else if (scan.IsName(before) && !IsVariable(scan, before) && ClassName(scan.Text(before)) is { } type)
            {
                _extract.Reference(key, CodeEdgeKinds.Calls, word, line, arity, new ExtractQualifier(CodeQualifierKinds.Type, type));
            }
            else
            {
                _extract.Reference(key, CodeEdgeKinds.Calls, word, line, arity, new ExtractQualifier(CodeQualifierKinds.Value));
            }

            return;
        }

        _extract.Reference(key, CodeEdgeKinds.Calls, word, line, arity, Receiver(scan, accessor, locals));
    }

    /// <summary>
    /// What a call on the receiver before the arrow at <paramref name="arrow"/> is qualified by: <c>$this</c>; a chain of
    /// its properties, by their names; the class a variable is of where the code says; a value by no name the code types,
    /// else.
    /// </summary>
    private static ExtractQualifier Receiver(ScannedText scan, int arrow, Locals locals)
    {
        // Read from its end, last first, and turned once it is read.
        var names = new List<string>();
        var at = scan.Previous(arrow);

        while (scan.IsName(at))
        {
            var before = scan.Previous(at);

            if (IsVariable(scan, at))
            {
                names.Add(scan.Text(at));
                names.Reverse();

                // self::$x, static::$x: a static property of the type's own.
                if (scan.Is(before, "::") && (IsWord(scan, scan.Previous(before), "self") || IsWord(scan, scan.Previous(before), "static")))
                {
                    return new ExtractQualifier(CodeQualifierKinds.Value, string.Join('.', names));
                }

                // $a->$b, X::$y: a member the code names by a variable, or one of a class it holds.
                if (scan.Is(before, "->") || scan.Is(before, "?->") || scan.Is(before, "::"))
                {
                    return new ExtractQualifier(CodeQualifierKinds.Value);
                }

                break;
            }

            if (!(scan.Is(before, "->") || scan.Is(before, "?->")))
            {
                return new ExtractQualifier(CodeQualifierKinds.Value);
            }

            names.Add("$" + scan.Text(at));
            at = scan.Previous(before);
        }

        // A chain that begins at no variable - a call's result, an element's, a parenthesis's - is a value of no type the file says.
        if (!IsVariable(scan, at))
        {
            return new ExtractQualifier(CodeQualifierKinds.Value);
        }

        if (names[0] == "$this")
        {
            names.RemoveAt(0);
            return names.Count == 0 ? new ExtractQualifier(CodeQualifierKinds.This) : new ExtractQualifier(CodeQualifierKinds.Value, string.Join('.', names));
        }

        return names.Count == 1 && locals.Types.TryGetValue(names[0], out var type) && type is not null
            ? new ExtractQualifier(CodeQualifierKinds.Type, type)
            : new ExtractQualifier(CodeQualifierKinds.Value);
    }

    /// <summary>
    /// A <c>new</c> at <paramref name="at"/>: a call of the class it names, a constructor's - <c>self</c> and <c>static</c>
    /// the type it is written in - what its arguments call read after; one of a class the code computes marked dynamic,
    /// an anonymous class's passed over to its body, which is read as the code around it. Answers where reading goes on.
    /// </summary>
    private int Instantiated(ScannedText scan, int at, string key, Locals locals, string? owner)
    {
        var name = scan.Next(at);
        var line = scan[at].Line;

        if (IsWord(scan, name, "class"))
        {
            return name + 1;
        }

        if (IsVariable(scan, name) || scan.Is(name, "("))
        {
            _extract.Dynamic(line, "a new of a class the code computes");
            return name + 1;
        }

        if (!scan.IsName(name) || Made(scan, at, owner) is not { } made)
        {
            return name;
        }

        var open = scan.Next(name);
        var arity = scan.Is(open, "(") ? ScanCalls.CallArity(scan, open, scan.CloseOf(open), PhpCalls) : 0;
        var dot = made.LastIndexOf('.');

        _extract.Reference(key, CodeEdgeKinds.Calls, made[(dot + 1)..], line, arity, dot > 0 ? new ExtractQualifier(CodeQualifierKinds.Type, made[..dot]) : new ExtractQualifier(CodeQualifierKinds.Scope));
        return name + 1;
    }

    /// <summary>The class a <c>new</c> at <paramref name="at"/> makes, as the model names it - <c>self</c> and <c>static</c> the type it is written in; <see langword="null"/> where it names none the reader knows.</summary>
    private string? Made(ScannedText scan, int at, string? owner)
    {
        var name = scan.Next(at);

        if (!scan.IsName(name) || IsVariable(scan, name))
        {
            return null;
        }

        return IsWord(scan, name, "self") || IsWord(scan, name, "static") ? owner
            : IsWord(scan, name, "parent") || IsWord(scan, name, "class") ? null
            : ClassName(scan.Text(name));
    }

    /// <summary>A <c>catch</c>'s parentheses at <paramref name="open"/> and <paramref name="close"/>: the classes it catches referenced from the node of <paramref name="key"/>, and its variable of the one class where it catches one.</summary>
    private void Caught(ScannedText scan, int open, int close, string key, Locals locals)
    {
        string? one = null;
        var count = 0;

        foreach (var index in scan.Code(open + 1, close))
        {
            if (IsVariable(scan, index))
            {
                locals.Types[scan.Text(index)] = count == 1 ? one : null;
            }
            else if (scan.IsName(index) && ClassName(scan.Text(index)) is { } caught)
            {
                Mention(key, CodeEdgeKinds.References, caught, scan[index].Line);
                one = caught;
                count++;
            }
        }
    }

    /// <summary>The parameters of a closure or an arrow function whose parentheses are at <paramref name="open"/> and <paramref name="close"/>: each variable with the class its type is, where it writes one.</summary>
    private IEnumerable<(string Name, string? Type)> ClosureParameters(ScannedText scan, int open, int close)
    {
        foreach (var (start, end) in ScanCalls.Split(scan, open + 1, close, null))
        {
            var variable = scan.Code(start, end).FirstOrDefault(index => IsVariable(scan, index), -1);

            if (variable < 0)
            {
                continue;
            }

            string? type = null;
            var names = scan.Code(start, variable).Where(index => scan.IsName(index) && !Visibilities.Contains(scan.Text(index))).ToList();

            if (names.Count == 1 && !Builtins.Contains(scan.Text(names[0])))
            {
                type = ClassName(scan.Text(names[0]));
            }

            yield return (scan.Text(variable), type);
        }
    }

    /// <summary>
    /// Pest's call at <paramref name="at"/> as a node of its own, named by its description and held by the node of
    /// <paramref name="parent"/> - none at the file's top - what its arguments call its own; answers the index of its
    /// closing parenthesis, or 0 where it gives no description.
    /// </summary>
    private int Test(InterpolatedText text, int at, ScannedCall call, string? parent, Locals locals, string? owner, int depth)
    {
        var scan = text.Scan;
        var pieces = ScanCalls.Split(scan, call.Open + 1, call.Close, null);

        if (pieces.Count == 0 || call.Close >= scan.Count || ExtractText.Flat(scan.Spelled(pieces[0].Start, pieces[0].End)) is not { Length: > 0 } name)
        {
            return 0;
        }

        var word = scan.Text(at);
        var qualified = parent is null ? $"{_path}.{name}" : $"{_extract.Node(parent).Qualified}.{name}";
        var key = _extract.Add(new ExtractNode(Id(qualified), CodeNodeKinds.Member, name, qualified, scan[at].Line, scan[call.Close].EndLine, ExtractText.Signature($"{word}({name})"))
        {
            SubKind = word == "describe" ? "testGroup" : "test",
            Parent = parent,
            Summary = scan == _scan ? ScanComments.Summary(scan, at) : null,
            Test = true,
        });

        Calls(text, pieces[0].End, call.Close, key, locals, owner, depth + 1);
        return call.Close;
    }

    /// <summary>The key of the node what the file's top does outside its declarations is the calls of: made the first time it is needed.</summary>
    private string ScriptKey()
    {
        if (_scriptKey is not null)
        {
            return _scriptKey;
        }

        var qualified = $"{_path}.(script)";

        return _scriptKey = _extract.Add(new ExtractNode(Id(qualified), CodeNodeKinds.Member, "(script)", qualified, 1, Math.Max(1, _text.Lines.Starts.Count), "(script)")
        {
            SubKind = "script",
        });
    }

    // ---- Names ----

    /// <summary>
    /// The class <paramref name="written"/> names, as PHP resolves it and the model names it: after a leading <c>\</c> the
    /// name itself; <c>namespace\X</c> the namespace's; else its first part a <c>use</c>'s, or the name in the file's
    /// namespace. <see langword="null"/> for <c>self</c>, <c>static</c> and <c>parent</c>, which name the type a call is in.
    /// </summary>
    private string? ClassName(string written)
    {
        if (written.Length == 0 || written[0] == '$' || IsSelf(written))
        {
            return null;
        }

        if (written[0] == '\\')
        {
            return written.Length > 1 ? Dotted(written[1..]) : null;
        }

        if (written.StartsWith("namespace\\", StringComparison.OrdinalIgnoreCase))
        {
            return ExtractText.Qualify(_namespace, Dotted(written["namespace\\".Length..]));
        }

        var separator = written.IndexOf('\\', StringComparison.Ordinal);
        var head = separator < 0 ? written : written[..separator];

        if (_classes.TryGetValue(head, out var used))
        {
            return separator < 0 ? used : $"{used}.{Dotted(written[(separator + 1)..])}";
        }

        return ExtractText.Qualify(_namespace, Dotted(written));
    }

    /// <summary>Whether <paramref name="written"/> is <c>self</c>, <c>static</c> or <c>parent</c>.</summary>
    private static bool IsSelf(string written)
        => written.Equals("self", StringComparison.OrdinalIgnoreCase) || written.Equals("static", StringComparison.OrdinalIgnoreCase) || written.Equals("parent", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A reference of <paramref name="kind"/> from the node of <paramref name="key"/> to the class <paramref name="named"/>
    /// names, as the model names it: looked for in its namespace alone, or among the declarations in no scope where it is
    /// the global namespace's.
    /// </summary>
    private void Mention(string key, string kind, string named, int line)
    {
        var dot = named.LastIndexOf('.');
        _extract.Reference(key, kind, named[(dot + 1)..], line, qualifier: dot > 0 ? new ExtractQualifier(CodeQualifierKinds.Type, named[..dot]) : new ExtractQualifier(CodeQualifierKinds.Scope));
    }

    /// <summary><paramref name="written"/>, a name PHP writes with its separator of namespaces, as the model writes it: each <c>\</c> a dot.</summary>
    private static string Dotted(string written) => written.Replace('\\', '.');

    /// <summary>The last part of the model's name <paramref name="qualified"/>.</summary>
    private static string Simple(string qualified) => qualified[(qualified.LastIndexOf('.') + 1)..];

    /// <summary>Whether the token at <paramref name="at"/> is a variable: a name PHP writes after a <c>$</c>.</summary>
    private static bool IsVariable(ScannedText scan, int at) => scan.IsName(at) && scan.Source[scan[at].Start] == '$';

    /// <summary>Whether the token at <paramref name="at"/> is the word <paramref name="word"/>, whatever its case, as PHP's keywords are.</summary>
    private static bool IsWord(ScannedText scan, int at, string word)
        => scan.IsName(at) && scan[at].Length == word.Length && string.Compare(scan.Source, scan[at].Start, word, 0, word.Length, StringComparison.OrdinalIgnoreCase) == 0;

    /// <summary>The index of the <c>;</c> or <c>?&gt;</c> a statement ending before <paramref name="stop"/> ends with, or <paramref name="stop"/> where it ends with neither.</summary>
    private int Ending(int stop) => _scan.EndsStatement(_scan.LastOf(stop)) ? _scan.LastOf(stop) : stop;

    /// <summary>The id of a declaration of the qualified name <paramref name="qualified"/>.</summary>
    private string Id(string qualified) => _prefix + qualified;

}
